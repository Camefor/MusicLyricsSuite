using System.Text;
using LyricsFetcher.Models;

namespace LyricsFetcher.Services;

/// <summary>
/// 单首歌曲的"查歌词 → 写 .lrc"业务编排。
/// 本服务不扫描目录，只处理单个 <see cref="TrackMetadata"/>；批量循环与节流由上层决定。
/// </summary>
public sealed class LyricsFetcherService
{
    private readonly LrcLibClient _client;

    public LyricsFetcherService(LrcLibClient client)
    {
        _client = client;
    }

    /// <param name="track">曲目元数据（含 OutputPath — .lrc 的目标路径）。</param>
    /// <param name="overwrite">目标 .lrc 已存在时是否覆盖；false 则返回 <see cref="FetchStatus.SkippedExists"/>。</param>
    /// <param name="onNotice">可选：中间状态提示（限流、相近候选跳过等）。</param>
    public async Task<FetchOutcome> FetchAsync(
        TrackMetadata track,
        bool overwrite = false,
        Action<string>? onNotice = null,
        CancellationToken cancellationToken = default)
    {
        if (System.IO.File.Exists(track.OutputPath) && !overwrite)
        {
            return new FetchOutcome
            {
                AudioPath = track.AudioPath,
                Status = FetchStatus.SkippedExists,
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                OutputPath = track.OutputPath,
                Message = "目标 .lrc 已存在；未覆盖",
            };
        }

        try
        {
            var result = await _client.FindLyricsAsync(track, onNotice, cancellationToken);
            if (result is null)
            {
                return new FetchOutcome
                {
                    AudioPath = track.AudioPath,
                    Status = FetchStatus.NotFound,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    Message = "未找到匹配结果",
                };
            }

            var lyrics = !string.IsNullOrWhiteSpace(result.SyncedLyrics)
                ? result.SyncedLyrics
                : result.PlainLyrics;

            if (string.IsNullOrWhiteSpace(lyrics) && !result.Instrumental)
            {
                return new FetchOutcome
                {
                    AudioPath = track.AudioPath,
                    Status = FetchStatus.Empty,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    SourceId = result.Id,
                    Message = "匹配记录无歌词文本",
                };
            }

            // Instrumental 轨：写一个占位 LRC 行，避免播放器重复找歌词
            if (string.IsNullOrWhiteSpace(lyrics))
            {
                lyrics = "[00:00.00]纯音乐/无歌词（LRCLIB 标记）";
            }

            lyrics = NormalizeNewlines(lyrics!);
            await System.IO.File.WriteAllTextAsync(
                track.OutputPath,
                lyrics.TrimEnd() + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);

            var isSynced = !string.IsNullOrWhiteSpace(result.SyncedLyrics);
            return new FetchOutcome
            {
                AudioPath = track.AudioPath,
                Status = isSynced ? FetchStatus.FoundSynced : FetchStatus.FoundPlain,
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                SourceId = result.Id,
                OutputPath = track.OutputPath,
                Message = isSynced ? "保存 syncedLyrics" : "仅 plainLyrics，无同步时间轴",
            };
        }
        catch (Exception ex)
        {
            return new FetchOutcome
            {
                AudioPath = track.AudioPath,
                Status = FetchStatus.Error,
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Message = ex.Message,
            };
        }
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
}
