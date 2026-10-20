using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LyricsFetcher.Models;

namespace LyricsFetcher.Services;

/// <summary>
/// LRCLIB (https://lrclib.net) 的轻封装：三步搜索——
///   1. /get 四元组精确命中（title + artist + album + duration）；
///   2. /search 三元组（title + artist + album），对候选打分；
///   3. /search 兜底宽泛搜索：简化标题（去括号后缀） + 歌手，不传专辑；
/// 任意一步命中就返回；本客户端不写文件、不发副作用，只负责"按元数据得到最合适的歌词记录"。
/// 请求节流：实例级串行闸门 + 相邻请求间隔至少 <see cref="MinRequestInterval"/>，
/// 避免对 LRCLIB 发并发或高频请求。
/// </summary>
public sealed class LrcLibClient : IDisposable
{
    private const string ApiBase = "https://lrclib.net/api";

    /// <summary>LRCLIB 对调用方很敏感，这里强制相邻请求间隔至少 10 秒。</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>匹配任意一对半/全角括号及其内容：() （） [] 【】 {}。用于标题精简。</summary>
    private static readonly Regex BracketedSegmentPattern = new(
        @"[\(\[\{（【][^\)\]\}）】]*[\)\]\}）】]",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>禁止并发访问 LRCLIB：所有 /get、/search 调用必须串行通过本闸门。</summary>
    private readonly SemaphoreSlim _throttleGate = new(1, 1);

    /// <summary>下一次允许发起请求的最早时刻（UTC），由上次请求结束时间 + <see cref="MinRequestInterval"/> 决定。</summary>
    private DateTime _nextAllowedAt = DateTime.MinValue;

    public LrcLibClient() : this(CreateDefaultClient(), ownsClient: true)
    {
    }

    public LrcLibClient(HttpClient http, bool ownsClient = false)
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
    /// 根据曲目元数据查找最合适的歌词记录。未找到或候选分值过低会返回 null，
    /// 避免把错误歌词写入 .lrc。
    /// </summary>
    /// <param name="track">曲目元数据。Duration > 0 时参与精确匹配。</param>
    /// <param name="onNotice">可选：输出中间状态提示（例如限流、候选相近跳过）。</param>
    public async Task<LyricsResult?> FindLyricsAsync(
        TrackMetadata track,
        Action<string>? onNotice = null,
        CancellationToken cancellationToken = default)
    {
        // 1. 精确命中：/get 要求四元组完全匹配，命中后直接返回
        var exactParams = new Dictionary<string, string>
        {
            ["track_name"] = track.Title,
            ["artist_name"] = track.Artist,
            ["album_name"] = track.Album,
        };
        if (track.Duration > 0)
        {
            exactParams["duration"] = track.Duration.ToString("0.###", CultureInfo.InvariantCulture);
        }

        var exact = await GetJsonAsync<LyricsResult>($"{ApiBase}/get", exactParams, onNotice, cancellationToken);
        if (exact is not null && IsPlausibleMatch(track, exact, strict: true))
        {
            return exact;
        }

        // 2. 回退搜索：按三元组（title + artist + album）搜索候选
        var strictMatch = await SearchAndPickAsync(
            track,
            new Dictionary<string, string>
            {
                ["track_name"] = track.Title,
                ["artist_name"] = track.Artist,
                ["album_name"] = track.Album,
            },
            onNotice,
            cancellationToken);
        if (strictMatch is not null)
        {
            return strictMatch;
        }

        // 3. 兜底宽泛搜索：标题精简（去括号后缀）+ 歌手，不传专辑。歌手空就不做了，避免大海捞针。
        if (string.IsNullOrWhiteSpace(track.Artist))
        {
            return null;
        }

        var simplifiedTitle = SimplifyTitleForSearch(track.Title);
        onNotice?.Invoke($"LRCLIB 三元组无命中，按简化标题 \"{simplifiedTitle}\" + 歌手再查一次");

        // 简化后标题变化时，用新 title 的 track 快照来评分 / 过滤，避免被原始标题的括号后缀卡住
        var matchTrack = string.Equals(simplifiedTitle, track.Title, StringComparison.Ordinal)
            ? track
            : CloneWithTitle(track, simplifiedTitle);

        return await SearchAndPickAsync(
            matchTrack,
            new Dictionary<string, string>
            {
                ["track_name"] = simplifiedTitle,
                ["artist_name"] = track.Artist,
            },
            onNotice,
            cancellationToken);
    }

    /// <summary>发一次 /search，对候选打分并执行"宽松二次匹配 + 相近候选跳过"的后置过滤。</summary>
    private async Task<LyricsResult?> SearchAndPickAsync(
        TrackMetadata track,
        Dictionary<string, string> searchParams,
        Action<string>? onNotice,
        CancellationToken cancellationToken)
    {
        var results = await GetJsonAsync<List<LyricsResult>>(
            $"{ApiBase}/search", searchParams, onNotice, cancellationToken);
        if (results is null || results.Count == 0)
        {
            return null;
        }

        var candidates = results
            .Select(r => (Result: r, Score: Score(track, r)))
            .OrderByDescending(x => x.Score)
            .ToList();

        var best = candidates[0];
        if (best.Score < 5 || !IsPlausibleMatch(track, best.Result, strict: false))
        {
            return null;
        }

        // 最优与次优分值接近时放弃，宁可漏匹配也不要误匹配
        if (candidates.Count > 1 && best.Score - candidates[1].Score < 1.5)
        {
            onNotice?.Invoke("搜索结果存在相近候选，跳过自动保存");
            return null;
        }

        return best.Result;
    }

    private static bool IsPlausibleMatch(TrackMetadata track, LyricsResult result, bool strict)
    {
        var resultTitle = result.Title;
        if (string.IsNullOrWhiteSpace(resultTitle)) return false;
        if (Normalize(track.Title) != Normalize(resultTitle)) return false;

        if (!string.IsNullOrWhiteSpace(result.ArtistName) &&
            !Normalize(result.ArtistName).Contains(Normalize(track.Artist), StringComparison.Ordinal) &&
            !Normalize(track.Artist).Contains(Normalize(result.ArtistName), StringComparison.Ordinal))
        {
            return false;
        }

        if (strict && !string.IsNullOrWhiteSpace(result.AlbumName) &&
            !Normalize(track.Album).Equals(Normalize(result.AlbumName), StringComparison.Ordinal))
        {
            // /get 返回的记录若专辑名明显不符，视作巧合命中，拒绝接受
            return false;
        }

        if (track.Duration > 0 && result.Duration.HasValue &&
            Math.Abs(track.Duration - result.Duration.Value) > (strict ? 8 : 12))
        {
            return false;
        }

        return true;
    }

    private static double Score(TrackMetadata track, LyricsResult result)
    {
        double score = 0;
        if (Normalize(track.Title) == Normalize(result.Title)) score += 5;
        else if (Normalize(result.Title).Contains(Normalize(track.Title), StringComparison.Ordinal) ||
                 Normalize(track.Title).Contains(Normalize(result.Title), StringComparison.Ordinal))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(result.ArtistName) &&
            (Normalize(result.ArtistName).Contains(Normalize(track.Artist), StringComparison.Ordinal) ||
             Normalize(track.Artist).Contains(Normalize(result.ArtistName), StringComparison.Ordinal)))
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(result.AlbumName) &&
            (Normalize(result.AlbumName) == Normalize(track.Album) ||
             Normalize(result.AlbumName).Contains(Normalize(track.Album), StringComparison.Ordinal) ||
             Normalize(track.Album).Contains(Normalize(result.AlbumName), StringComparison.Ordinal)))
        {
            score += 2;
        }

        if (track.Duration > 0 && result.Duration.HasValue)
        {
            var diff = Math.Abs(track.Duration - result.Duration.Value);
            if (diff <= 2) score += 2;
            else if (diff <= 5) score += 1;
            else if (diff > 12) score -= 4;
        }

        return score;
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

        // 限流闸门：串行所有 LRCLIB 调用；进入后先补齐与上次请求结束的 10 秒间隔，再发请求
        await _throttleGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            if (now < _nextAllowedAt)
            {
                var wait = _nextAllowedAt - now;
                onNotice?.Invoke($"LRCLIB 节流：等待 {wait.TotalSeconds:0.#} 秒后再发请求");
                await Task.Delay(wait, cancellationToken);
            }

            using var response = await _http.GetAsync($"{url}?{queryString}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            // 429 限流 / 503 服务繁忙：遵守 Retry-After，自动重试一次。
            // 本次重试仍在闸门内，不会与其他调用并发
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode == 503)
            {
                var waitSeconds = 5;
                if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                {
                    waitSeconds = Math.Clamp((int)Math.Ceiling(delta.TotalSeconds), 5, 120);
                }

                onNotice?.Invoke($"限流/服务繁忙 HTTP {(int)response.StatusCode}，等待 {waitSeconds} 秒后重试一次");
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
        finally
        {
            // 不管成功 / 失败 / 取消，都从"结束时间 + 10s"重新计时，确保下次调用守住间隔
            _nextAllowedAt = DateTime.UtcNow + MinRequestInterval;
            _throttleGate.Release();
        }
    }

    /// <summary>把标题 / 歌手 / 专辑统一为"小写字母+数字"用于容错比较。</summary>
    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// 把标题精简到核心关键字——去掉所有括号及其内容（支持 () （） [] 【】 {}），
    /// 清理首尾空白与连字符；清理后为空则回退原标题。
    /// 示例："定西(2014i/O版)" → "定西"。
    /// </summary>
    private static string SimplifyTitleForSearch(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var simplified = BracketedSegmentPattern.Replace(title, "")
            .Trim(' ', '\t', '-', '－', '–', '—');
        return string.IsNullOrWhiteSpace(simplified) ? title : simplified;
    }

    /// <summary>复制一份 TrackMetadata，仅替换 Title——用于把"评分/过滤用的 track"切到简化标题版本。</summary>
    private static TrackMetadata CloneWithTitle(TrackMetadata source, string title) => new()
    {
        AudioPath = source.AudioPath,
        Title = title,
        Artist = source.Artist,
        Album = source.Album,
        Duration = source.Duration,
        OutputPath = source.OutputPath,
    };

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
        _throttleGate.Dispose();
    }
}
