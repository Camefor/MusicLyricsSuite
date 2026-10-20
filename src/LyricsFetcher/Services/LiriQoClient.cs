using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LyricsFetcher.Models;

namespace LyricsFetcher.Services;

/// <summary>
/// LiriQo (https://api.liriqo-alfarrizi.my.id) 的轻封装：
/// 作为 LRCLIB 未命中时的降级来源。两步调用——
///   1. GET /v1/search?title&amp;artist&amp;duration →取 source=youtube 的 videoId；
///      title 会预先精简（去掉括号后缀，如 "定西(2014i／O版)" → "定西"），尽量宽松地拿到候选；
///      不传 album，避免被专辑名卡住，先解决"拿不到歌词"的问题；
///   2. GET /v1/lyrics?v={videoId} →聚合多 provider 的候选，按 syncLevel 质量和 title/artist 相似度挑最佳；
/// 并转换为统一的 <see cref="LyricsResult"/>：
///   - 高同步级别（syllable/word/line）→ 组装成 LRC 文本写入 SyncedLyrics；
///   - plain 级别或无有效时间轴 → 使用 plain 字段写入 PlainLyrics。
/// 本客户端只负责"按元数据得到最合适的歌词记录"，不写文件、不发副作用。
/// </summary>
public sealed class LiriQoClient : IDisposable
{
    private const string ApiBase = "https://api.liriqo-alfarrizi.my.id/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // LiriQo 的 metadata.duration 偶尔以字符串回传（如 "199" 或 ""），要能同时解析字符串数值
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            // AllowReadingFromString 遇到空串仍会抛，这里把空串/空白串统一转成 null
            new LenientNullableDoubleConverter(),
        },
    };

    /// <summary>匹配任意一对半/全角括号及其内容：() （） [] 【】 {}。用于清理标题中的版本/现场/翻唱等修饰。</summary>
    private static readonly Regex BracketedSegmentPattern = new(
        @"[\(\[\{（【][^\)\]\}）】]*[\)\]\}）】]",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public LiriQoClient() : this(CreateDefaultClient(), ownsClient: true)
    {
    }

    public LiriQoClient(HttpClient http, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LyricsFetcher/2.0 (personal local music library)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// LRCLIB 未命中时的降级入口。/v1/search 拿 videoId → /v1/lyrics 聚合多 provider → 挑最优 track。
    /// 匹配不上或候选质量太低时返回 null，避免把错误歌词写入 .lrc。
    /// </summary>
    /// <param name="track">曲目元数据。Duration &gt; 0 时会作为搜索的辅助过滤条件。</param>
    /// <param name="onNotice">可选：输出中间状态提示（例如限流、降级进度）。</param>
    public async Task<LyricsResult?> FindLyricsAsync(
        TrackMetadata track,
        Action<string>? onNotice = null,
        CancellationToken cancellationToken = default)
    {
        // 1. 搜索 videoId：只接受 source="youtube" 的结果，其他源暂不处理
        //    标题先精简（去括号后缀），不传 album——目标是先拿到 videoId，专辑可能完全对不上
        var simplifiedTitle = SimplifyTitleForSearch(track.Title);
        var searchParams = new Dictionary<string, string>
        {
            ["title"] = simplifiedTitle,
            ["artist"] = track.Artist,
        };
        if (track.Duration > 0)
        {
            searchParams["duration"] = ((int)Math.Round(track.Duration))
                .ToString(CultureInfo.InvariantCulture);
        }

        var search = await GetJsonAsync<SearchResponse>(
            $"{ApiBase}/search", searchParams, onNotice, cancellationToken);
        if (search is null ||
            string.IsNullOrWhiteSpace(search.VideoId) ||
            !string.Equals(search.Source, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 2. 用 videoId 聚合多 provider 的歌词候选
        var lyricsParams = new Dictionary<string, string>
        {
            ["v"] = search.VideoId!,
        };
        var lyrics = await GetJsonAsync<LyricsResponse>(
            $"{ApiBase}/lyrics", lyricsParams, onNotice, cancellationToken);
        if (lyrics?.Tracks is null || lyrics.Tracks.Count == 0)
        {
            return null;
        }

        // 3. 过滤 + 打分：title/artist 必须对得上；syncLevel 作为质量系数（syllable > word > line > plain）
        var scored = lyrics.Tracks
            .Where(t => IsPlausibleMatch(track, t))
            .Select(t => (Track: t, Score: Score(track, t)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored[0];
        if (best.Score < 5)
        {
            onNotice?.Invoke("LiriQo 候选相似度不足，放弃降级结果");
            return null;
        }

        return ToLyricsResult(best.Track, lyrics.Metadata ?? search.Metadata);
    }

    private static bool IsPlausibleMatch(TrackMetadata track, TrackPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Title)) return false;
        if (!TitleMatches(track.Title, payload.Title!, track.Artist)) return false;

        // artist 为空的候选不直接否决——有些 provider 不返回 artist，先放行后由分值兜底
        if (!string.IsNullOrWhiteSpace(payload.Artist) &&
            !string.IsNullOrWhiteSpace(track.Artist))
        {
            var a = Normalize(payload.Artist!);
            var b = Normalize(track.Artist);
            if (!a.Contains(b, StringComparison.Ordinal) &&
                !b.Contains(a, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 标题模糊匹配，放得尽量宽：
    ///   1. 两边都做 <see cref="Normalize"/>（已经自动去掉 "-"、"，"、空格、括号等符号），
    ///      再把 artist 的痕迹从标题里剔除（文件名常见 "歌手 - 歌名" 这种前缀）；
    ///   2. 完全相等或互相包含 → 匹配；
    ///   3. 否则看两边能否共享一段 2-4 字的连续片段，优先尝试更长片段；
    ///      纯数字片段（如年份 "2016"）信息量不足，不作为匹配依据。
    /// </summary>
    private static bool TitleMatches(string a, string b, string? artist = null)
    {
        var na = StripArtist(Normalize(a), artist);
        var nb = StripArtist(Normalize(b), artist);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        if (na.Contains(nb, StringComparison.Ordinal) ||
            nb.Contains(na, StringComparison.Ordinal))
        {
            return true;
        }

        var shorter = na.Length <= nb.Length ? na : nb;
        var longer = na.Length <= nb.Length ? nb : na;
        var maxLen = Math.Min(4, shorter.Length);
        for (var len = maxLen; len >= 2; len--)
        {
            for (var i = 0; i + len <= shorter.Length; i++)
            {
                var slice = shorter.Substring(i, len);
                if (IsAllDigits(slice)) continue;
                if (longer.Contains(slice, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 把标题精简到核心关键字，用于向 LiriQo /v1/search 发请求。
    ///   - 去掉所有括号及其内容（支持 () （） [] 【】 {}），如 "定西(2014i／O版)" → "定西"
    ///   - 清理首尾空白与连字符
    ///   - 清理后若为空串则回退到原标题，避免发出空 title
    /// 目的：第一步搜索尽量宽松，先拿到 videoId；准确性由后续 /v1/lyrics 的候选打分兜底。
    /// </summary>
    private static string SimplifyTitleForSearch(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var simplified = BracketedSegmentPattern.Replace(title, "")
            .Trim(' ', '\t', '-', '－', '–', '—');
        return string.IsNullOrWhiteSpace(simplified) ? title : simplified;
    }

    /// <summary>从归一化后的标题里剔除 artist 的痕迹，避免 "李志-墙上的向日葵" 这种前缀影响匹配。</summary>
    private static string StripArtist(string normalizedTitle, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return normalizedTitle;
        var normalizedArtist = Normalize(artist!);
        if (normalizedArtist.Length == 0) return normalizedTitle;
        return normalizedTitle.Replace(normalizedArtist, string.Empty);
    }

    private static bool IsAllDigits(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch)) return false;
        }
        return value.Length > 0;
    }

    private static double Score(TrackMetadata track, TrackPayload payload)
    {
        double score = 0;

        if (Normalize(track.Title) == Normalize(payload.Title ?? ""))
        {
            score += 5;
        }
        else
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(payload.Artist) &&
            (Normalize(payload.Artist!).Contains(Normalize(track.Artist), StringComparison.Ordinal) ||
             Normalize(track.Artist).Contains(Normalize(payload.Artist!), StringComparison.Ordinal)))
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(payload.Album) &&
            !string.IsNullOrWhiteSpace(track.Album) &&
            (Normalize(payload.Album!).Contains(Normalize(track.Album), StringComparison.Ordinal) ||
             Normalize(track.Album).Contains(Normalize(payload.Album!), StringComparison.Ordinal)))
        {
            score += 2;
        }

        // syncLevel 质量加成：优先挑同步级别更高的 provider
        score += payload.SyncLevel?.ToLowerInvariant() switch
        {
            "syllable" => 4,
            "word" => 3,
            "line" => 2,
            _ => 0,
        };

        // 真实带时间轴的 timed[] 再加一分（和 syncLevel=plain 但带 timed 的情况区分开）
        if (HasRealTimings(payload))
        {
            score += 1;
        }

        return score;
    }

    /// <summary>把 LiriQo 的 track 填充回 <see cref="LyricsResult"/>，优先 SyncedLyrics，否则 PlainLyrics。</summary>
    private static LyricsResult ToLyricsResult(TrackPayload track, MetadataPayload? meta)
    {
        var synced = BuildSyncedLyrics(track);
        var plain = track.Plain?.TrimEnd();

        return new LyricsResult
        {
            Id = 0,
            TrackName = !string.IsNullOrWhiteSpace(track.Title) ? track.Title : meta?.Title,
            ArtistName = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist : meta?.Artist,
            AlbumName = !string.IsNullOrWhiteSpace(track.Album) ? track.Album : meta?.Album,
            Duration = meta?.Duration,
            PlainLyrics = string.IsNullOrWhiteSpace(plain) ? null : plain,
            SyncedLyrics = synced,
            Instrumental = false,
        };
    }

    /// <summary>LRC 文本来源优先级：upstream 原生 lrc → 由 timed[] 组装 → null。</summary>
    private static string? BuildSyncedLyrics(TrackPayload track)
    {
        var level = track.SyncLevel?.ToLowerInvariant();
        if (level == "plain" && !HasRealTimings(track))
        {
            // syncLevel=plain 且 timed[] 都是 0,0 时间戳，视作无同步歌词
            return null;
        }

        if (!string.IsNullOrWhiteSpace(track.Lrc))
        {
            return track.Lrc!.TrimEnd();
        }

        if (!HasRealTimings(track))
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var line in track.Timed!)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            sb.Append('[')
                .Append(FormatLrcTimestamp(line.Start))
                .Append(']')
                .Append(line.Text)
                .Append('\n');
        }

        var text = sb.ToString().TrimEnd();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool HasRealTimings(TrackPayload track)
    {
        if (track.Timed is null || track.Timed.Count == 0) return false;
        return track.Timed.Any(t => t.Start > 0 || t.End > 0);
    }

    private static string FormatLrcTimestamp(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var minutes = (int)(seconds / 60);
        var remainder = seconds - minutes * 60;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00.00}", minutes, remainder);
    }

    private async Task<T?> GetJsonAsync<T>(
        string url,
        Dictionary<string, string> query,
        Action<string>? onNotice,
        CancellationToken cancellationToken)
    {
        var queryString = string.Join("&", query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        using var response = await _http.GetAsync($"{url}?{queryString}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        // 429 限流 / 503 服务繁忙：遵守 Retry-After，自动重试一次
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode == 503)
        {
            var waitSeconds = 3;
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            {
                waitSeconds = Math.Clamp((int)Math.Ceiling(delta.TotalSeconds), 1, 120);
            }

            onNotice?.Invoke($"LiriQo 限流/服务繁忙 HTTP {(int)response.StatusCode}，等待 {waitSeconds} 秒后重试一次");
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);

            using var retry = await _http.GetAsync($"{url}?{queryString}", cancellationToken);
            if (retry.StatusCode == HttpStatusCode.NotFound) return default;
            retry.EnsureSuccessStatusCode();
            var retryBody = await retry.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(retryBody, JsonOptions);
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return default;
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    /// <summary>把标题 / 歌手 / 专辑统一为"小写字母+数字"用于容错比较。</summary>
    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    // ---------- Response DTOs ----------

    private sealed class SearchResponse
    {
        [JsonPropertyName("isVideoId")] public bool IsVideoId { get; set; }
        [JsonPropertyName("videoId")] public string? VideoId { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("metadata")] public MetadataPayload? Metadata { get; set; }
        [JsonPropertyName("matchScore")] public double? MatchScore { get; set; }
    }

    private sealed class LyricsResponse
    {
        [JsonPropertyName("videoId")] public string? VideoId { get; set; }
        [JsonPropertyName("metadata")] public MetadataPayload? Metadata { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("tracks")] public List<TrackPayload>? Tracks { get; set; }
    }

    private sealed class MetadataPayload
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }

    private sealed class TrackPayload
    {
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("syncLevel")] public string? SyncLevel { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("plain")] public string? Plain { get; set; }
        [JsonPropertyName("timed")] public List<TimedLine>? Timed { get; set; }
        [JsonPropertyName("lrc")] public string? Lrc { get; set; }
        [JsonPropertyName("ttml")] public string? Ttml { get; set; }
        [JsonPropertyName("upstreamProvider")] public string? UpstreamProvider { get; set; }
    }

    private sealed class TimedLine
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("start")] public double Start { get; set; }
        [JsonPropertyName("end")] public double End { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("isCue")] public bool IsCue { get; set; }
        [JsonPropertyName("isInstrumental")] public bool IsInstrumental { get; set; }
    }

    /// <summary>
    /// 容忍 LiriQo 把数字字段写成字符串甚至空串的宽松解析器——String 空串 → null、
    /// String 数字 → 解析、Number → 原样取。失败时返回 null 而非抛异常，避免把一张专辑全部拖垮。
    /// </summary>
    private sealed class LenientNullableDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    return reader.GetDouble();
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                        ? v
                        : null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
    }
}
