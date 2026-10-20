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

            // 每首歌同步落一条 TrackRecord，供最终总览与日志展开"失败清单"使用
            var record = new TrackRecord
            {
                AudioPath = audioPath,
                RelativePath = Path.GetRelativePath(albumDirectory, audioPath),
            };
            summary.Records.Add(record);

            TrackMetadata track;
            try
            {
                track = AudioMetadataReader.Read(audioPath, _options.DefaultArtist);
            }
            catch (Exception ex)
            {
                summary.MetadataFailures++;
                record.MetadataError = $"{ex.GetType().Name}: {ex.Message}";
                reporter.OnMetadataFailure(position, audioPath, ex);
                continue;
            }

            record.Title = track.Title;
            record.Artist = track.Artist;
            record.Album = track.Album;

            reporter.OnTrackStart(position, track);

            // 1. 下载 / 复用 .lrc 文件
            var fetchOutcome = await _fetcher.FetchAsync(
                track,
                _options.OverwriteLrc,
                notice => reporter.OnNotice(position, notice),
                cancellationToken);

            summary.Record(fetchOutcome.Status);
            record.FetchStatus = fetchOutcome.Status;
            record.FetchMessage = fetchOutcome.Message;
            record.OutputLrcPath = fetchOutcome.OutputPath;
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
                record.EmbedStatus = embed.Status;
                record.EmbedDetail = embed.Detail;
                reporter.OnEmbedResult(position, embed);
            }
            catch (Exception ex)
            {
                summary.EmbedFailures++;
                record.EmbedStatus = ProcessStatus.Failed;
                record.EmbedDetail = $"{ex.GetType().Name}: {ex.Message}";
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
        StartedAt = DateTime.Now;
    }

    public string AlbumDirectory { get; }
    public int AudioCount { get; }
    public DateTime StartedAt { get; }

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

    /// <summary>按顺序记录每一首歌从读标签到嵌入的完整结果，供日志与总览使用。</summary>
    public List<TrackRecord> Records { get; } = new();

    /// <summary>需要人工介入的失败清单（读标签失败 / 歌词未找到 / 歌词空 / 下载异常 / 嵌入失败）。</summary>
    public IEnumerable<TrackRecord> Failures => Records.Where(r => r.NeedsManualAttention);

    /// <summary>成功落盘的清单：拿到并写入歌词、或已有 .lrc 本次嵌入成功 / 已一致。</summary>
    public IEnumerable<TrackRecord> Successes => Records.Where(r => r.IsSuccess);

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

/// <summary>
/// 单首歌从读标签到嵌入的全过程记录。<see cref="RelativePath"/> 以本次运行选择的专辑目录为根，
/// 便于总览里精确告知"子目录下的哪首歌"需要手动处理。
/// </summary>
public sealed class TrackRecord
{
    public required string AudioPath { get; init; }

    /// <summary>相对于本次运行选择的专辑根目录的路径，如 "我爱南京/定西.mp3"。</summary>
    public required string RelativePath { get; init; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    public FetchStatus? FetchStatus { get; set; }
    public string? FetchMessage { get; set; }
    public string? OutputLrcPath { get; set; }

    public ProcessStatus? EmbedStatus { get; set; }
    public string? EmbedDetail { get; set; }

    /// <summary>读取音频标签阶段的异常文本；非空意味着这首歌连元数据都没拿到。</summary>
    public string? MetadataError { get; set; }

    public bool NeedsManualAttention =>
        MetadataError is not null ||
        FetchStatus is LyricsFetcher.Models.FetchStatus.NotFound
                    or LyricsFetcher.Models.FetchStatus.Empty
                    or LyricsFetcher.Models.FetchStatus.Error ||
        EmbedStatus is ProcessStatus.Failed;

    public bool IsSuccess =>
        !NeedsManualAttention &&
        FetchStatus is LyricsFetcher.Models.FetchStatus.FoundSynced
                    or LyricsFetcher.Models.FetchStatus.FoundPlain
                    or LyricsFetcher.Models.FetchStatus.SkippedExists;

    /// <summary>把失败原因汇总成一行人类可读的描述，供总览与日志展示。</summary>
    public string DescribeFailure()
    {
        if (MetadataError is not null) return $"读取标签失败：{MetadataError}";
        if (FetchStatus is LyricsFetcher.Models.FetchStatus.NotFound) return "歌词未找到";
        if (FetchStatus is LyricsFetcher.Models.FetchStatus.Empty) return $"歌词记录为空：{FetchMessage}";
        if (FetchStatus is LyricsFetcher.Models.FetchStatus.Error) return $"下载异常：{FetchMessage}";
        if (EmbedStatus is ProcessStatus.Failed) return $"嵌入失败：{EmbedDetail}";
        return "未知失败";
    }
}
