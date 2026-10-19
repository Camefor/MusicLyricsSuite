using LrcTagger;
using LyricsFetcher.Models;
using LyricsFetcher.Services;

namespace LyricsSuite;

/// <summary>
/// 单个专辑目录的"读标签 → 拉歌词 → 嵌入音频"编排。
/// 本服务串接 LyricsFetcher 与 LrcTagger 两个子服务：
///   1. <see cref="AudioMetadataReader"/> 读音频标签得到 <see cref="TrackMetadata"/>；
///   2. <see cref="LyricsFetcherService"/> 调 LRCLIB 下载 .lrc；
///   3. <see cref="LyricsTagService"/> 把 .lrc 嵌入音频的内嵌歌词标签。
/// 嵌入前会清除同目录下遗留的无关 .lrc 歌词别名，确保落盘一致。
/// </summary>
public sealed class AlbumPipeline
{
    private readonly LyricsFetcherService _fetcher;
    private readonly PipelineOptions _options;

    public AlbumPipeline(LyricsFetcherService fetcher, PipelineOptions options)
    {
        _fetcher = fetcher;
        _options = options;
    }

    public async Task<AlbumPipelineSummary> RunAsync(
        string albumDirectory,
        IPipelineReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var audioFiles = ScanAudioFiles(albumDirectory);
        var summary = new AlbumPipelineSummary(albumDirectory, audioFiles.Count);

        if (audioFiles.Count == 0)
        {
            reporter.OnEmptyAlbum(albumDirectory);
            return summary;
        }

        reporter.OnAlbumStart(albumDirectory, audioFiles.Count);

        for (var i = 0; i < audioFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var audioPath = audioFiles[i];
            var position = (Index: i + 1, Total: audioFiles.Count);

            TrackMetadata track;
            try
            {
                track = AudioMetadataReader.Read(audioPath, _options.DefaultArtist);
            }
            catch (Exception ex)
            {
                summary.MetadataFailures++;
                reporter.OnMetadataFailure(position, audioPath, ex);
                continue;
            }

            reporter.OnTrackStart(position, track);

            // 1. 下载 / 复用 .lrc 文件
            var fetchOutcome = await _fetcher.FetchAsync(
                track,
                _options.OverwriteLrc,
                notice => reporter.OnNotice(position, notice),
                cancellationToken);

            summary.Record(fetchOutcome.Status);
            reporter.OnFetchResult(position, fetchOutcome);

            // 2. 若 .lrc 存在就尝试嵌入；允许"复用已有 .lrc"的场景也完成嵌入
            if (!fetchOutcome.HasLrcOnDisk)
            {
                continue;
            }

            try
            {
                var embed = EmbedLyrics(audioPath, fetchOutcome.OutputPath ?? Path.ChangeExtension(audioPath, ".lrc"));
                summary.Record(embed.Status);
                reporter.OnEmbedResult(position, embed);
            }
            catch (Exception ex)
            {
                summary.EmbedFailures++;
                reporter.OnEmbedFailure(position, audioPath, ex);
            }

            // 节流：避免相邻两次 LRCLIB 请求过近触发限流
            if (i < audioFiles.Count - 1)
            {
                await Task.Delay(_options.PerTrackDelayMs, cancellationToken);
            }
        }

        reporter.OnAlbumComplete(summary);
        return summary;
    }

    private static List<string> ScanAudioFiles(string albumDirectory)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        return Directory.EnumerateFiles(albumDirectory, "*", enumerationOptions)
            .Where(AudioFileCatalog.IsSupported)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>读取 .lrc 文本并写入音频的内嵌歌词标签，带写后校验。</summary>
    private static EmbedOutcome EmbedLyrics(string audioPath, string lrcPath)
    {
        if (!File.Exists(lrcPath))
        {
            return new EmbedOutcome(audioPath, lrcPath, ProcessStatus.NoLrcFile, "无同名 .lrc");
        }

        var (lrcText, encodingName) = LrcFileReader.Read(lrcPath);
        if (string.IsNullOrWhiteSpace(lrcText))
        {
            // 空歌词会覆盖掉文件里原有的歌词，拒绝写入
            return new EmbedOutcome(audioPath, lrcPath, ProcessStatus.Failed, "歌词文件内容为空");
        }

        if (LyricsTagService.ReadLyrics(audioPath) == lrcText)
        {
            return new EmbedOutcome(audioPath, lrcPath, ProcessStatus.UpToDate,
                $"歌词与 .lrc 已一致，未改动（{encodingName}，{lrcText.Length} 字符）");
        }

        LyricsTagService.WriteLyrics(audioPath, lrcText);

        // 对齐 LrcTagger：写后再读一次确认落盘
        if (LyricsTagService.ReadLyrics(audioPath) != lrcText)
        {
            return new EmbedOutcome(audioPath, lrcPath, ProcessStatus.Failed,
                "写入后校验未通过，文件可能未被正确修改");
        }

        return new EmbedOutcome(audioPath, lrcPath, ProcessStatus.Written,
            $"← {Path.GetFileName(lrcPath)}（{encodingName}，{lrcText.Length} 字符）");
    }
}

/// <summary>编排流程的运行时参数。</summary>
public sealed class PipelineOptions
{
    /// <summary>音频标签缺失时使用的默认歌手名。</summary>
    public string DefaultArtist { get; init; } = "";

    /// <summary>目标 .lrc 已存在时是否覆盖重新下载。</summary>
    public bool OverwriteLrc { get; init; }

    /// <summary>相邻两首 LRCLIB 请求之间的间隔，避免触发限流。</summary>
    public int PerTrackDelayMs { get; init; } = 500;
}

/// <summary>一次 .lrc → 音频嵌入的结果。</summary>
public sealed record EmbedOutcome(string AudioPath, string LrcPath, ProcessStatus Status, string Detail);

/// <summary>一张专辑运行完成后的汇总。</summary>
public sealed class AlbumPipelineSummary
{
    public AlbumPipelineSummary(string albumDirectory, int audioCount)
    {
        AlbumDirectory = albumDirectory;
        AudioCount = audioCount;
    }

    public string AlbumDirectory { get; }
    public int AudioCount { get; }

    public int FoundSynced { get; private set; }
    public int FoundPlain { get; private set; }
    public int NotFound { get; private set; }
    public int Empty { get; private set; }
    public int SkippedExists { get; private set; }
    public int FetchErrors { get; private set; }

    public int Written { get; private set; }
    public int UpToDate { get; private set; }
    public int NoLrcFile { get; private set; }
    public int EmbedFailures { get; set; }
    public int MetadataFailures { get; set; }

    public void Record(FetchStatus status)
    {
        switch (status)
        {
            case FetchStatus.FoundSynced: FoundSynced++; break;
            case FetchStatus.FoundPlain: FoundPlain++; break;
            case FetchStatus.NotFound: NotFound++; break;
            case FetchStatus.Empty: Empty++; break;
            case FetchStatus.SkippedExists: SkippedExists++; break;
            case FetchStatus.Error: FetchErrors++; break;
        }
    }

    public void Record(ProcessStatus status)
    {
        switch (status)
        {
            case ProcessStatus.Written: Written++; break;
            case ProcessStatus.UpToDate: UpToDate++; break;
            case ProcessStatus.NoLrcFile: NoLrcFile++; break;
            case ProcessStatus.Failed: EmbedFailures++; break;
            // WouldWrite 只在 dry-run 模式产生，这里不触发
        }
    }
}
