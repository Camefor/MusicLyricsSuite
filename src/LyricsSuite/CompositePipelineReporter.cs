using LrcTagger;
using LyricsFetcher.Models;

namespace LyricsSuite;

/// <summary>
/// 把事件同时派发给多个 <see cref="IPipelineReporter"/>——例如控制台 + 日志文件。
/// 不吞异常：任何子 reporter 抛错都会直接向上冒泡，调用方自行决定是否兜底。
/// </summary>
internal sealed class CompositePipelineReporter : IPipelineReporter
{
    private readonly IReadOnlyList<IPipelineReporter> _reporters;

    public CompositePipelineReporter(params IPipelineReporter[] reporters)
    {
        _reporters = reporters;
    }

    public void OnAlbumStart(string albumDirectory, int audioCount)
    {
        foreach (var r in _reporters) r.OnAlbumStart(albumDirectory, audioCount);
    }

    public void OnEmptyAlbum(string albumDirectory)
    {
        foreach (var r in _reporters) r.OnEmptyAlbum(albumDirectory);
    }

    public void OnTrackStart((int Index, int Total) position, TrackMetadata track)
    {
        foreach (var r in _reporters) r.OnTrackStart(position, track);
    }

    public void OnNotice((int Index, int Total) position, string notice)
    {
        foreach (var r in _reporters) r.OnNotice(position, notice);
    }

    public void OnFetchResult((int Index, int Total) position, FetchOutcome outcome)
    {
        foreach (var r in _reporters) r.OnFetchResult(position, outcome);
    }

    public void OnEmbedResult((int Index, int Total) position, EmbedOutcome outcome)
    {
        foreach (var r in _reporters) r.OnEmbedResult(position, outcome);
    }

    public void OnMetadataFailure((int Index, int Total) position, string audioPath, Exception error)
    {
        foreach (var r in _reporters) r.OnMetadataFailure(position, audioPath, error);
    }

    public void OnEmbedFailure((int Index, int Total) position, string audioPath, Exception error)
    {
        foreach (var r in _reporters) r.OnEmbedFailure(position, audioPath, error);
    }

    public void OnAlbumComplete(AlbumPipelineSummary summary)
    {
        foreach (var r in _reporters) r.OnAlbumComplete(summary);
    }
}
