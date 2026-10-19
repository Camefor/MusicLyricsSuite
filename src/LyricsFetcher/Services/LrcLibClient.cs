using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LyricsFetcher.Models;

namespace LyricsFetcher.Services;

/// <summary>
/// LRCLIB (https://lrclib.net) 的轻封装：精确命中 /get 优先，失败后回退 /search，
/// 并对候选打分避免错配。本客户端不写文件、不发副作用，只负责"按元数据得到最合适的歌词记录"。
/// </summary>
public sealed class LrcLibClient : IDisposable
{
    private const string ApiBase = "https://lrclib.net/api";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

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
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
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

        // 2. 回退搜索：按三元组搜索候选，只接受"显著高于次优"的最优解
        var searchParams = new Dictionary<string, string>
        {
            ["track_name"] = track.Title,
            ["artist_name"] = track.Artist,
            ["album_name"] = track.Album,
        };
        var results = await GetJsonAsync<List<LyricsResult>>($"{ApiBase}/search", searchParams, onNotice, cancellationToken);
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
}
