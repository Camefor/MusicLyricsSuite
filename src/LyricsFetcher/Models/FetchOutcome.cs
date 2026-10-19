namespace LyricsFetcher.Models;

/// <summary>单首歌曲一次 LRCLIB 查询的最终状态。</summary>
public enum FetchStatus
{
    /// <summary>获取到带时间轴的同步歌词并写入 .lrc。</summary>
    FoundSynced,

    /// <summary>只有纯文本歌词，无时间轴，写入 .lrc。</summary>
    FoundPlain,

    /// <summary>LRCLIB 未返回任何匹配，或候选过于相近无法安全选择。</summary>
    NotFound,

    /// <summary>LRCLIB 匹配记录内容为空（无 plain / synced 文本）。</summary>
    Empty,

    /// <summary>目标 .lrc 已存在且未开启覆盖。</summary>
    SkippedExists,

    /// <summary>读取音频、请求 LRCLIB 或写入 .lrc 过程中出现异常。</summary>
    Error,
}

/// <summary>单首歌曲下载歌词的结果。</summary>
public sealed record FetchOutcome
{
    public required string AudioPath { get; init; }
    public required FetchStatus Status { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public int? SourceId { get; init; }
    public string? OutputPath { get; init; }
    public string Message { get; init; } = "";

    public bool IsSuccess => Status is FetchStatus.FoundSynced or FetchStatus.FoundPlain;
    public bool HasLrcOnDisk => IsSuccess || Status == FetchStatus.SkippedExists;
}
