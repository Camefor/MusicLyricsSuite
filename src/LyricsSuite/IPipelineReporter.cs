using LrcTagger;
using LyricsFetcher.Models;

namespace LyricsSuite;

/// <summary>
/// 把编排器的事件与控制台 UI 解耦，便于后续替换为 JSON 输出 / 日志 / 测试桩。
/// 所有事件都应是幂等打印：实现方不应改变 Pipeline 状态。
/// </summary>
public interface IPipelineReporter
{
    void OnAlbumStart(string albumDirectory, int audioCount);
    void OnEmptyAlbum(string albumDirectory);

    void OnTrackStart((int Index, int Total) position, TrackMetadata track);
    void OnNotice((int Index, int Total) position, string notice);

    void OnFetchResult((int Index, int Total) position, FetchOutcome outcome);
    void OnEmbedResult((int Index, int Total) position, EmbedOutcome outcome);

    void OnMetadataFailure((int Index, int Total) position, string audioPath, Exception error);
    void OnEmbedFailure((int Index, int Total) position, string audioPath, Exception error);

    void OnAlbumComplete(AlbumPipelineSummary summary);
}
