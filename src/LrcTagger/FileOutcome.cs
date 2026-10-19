namespace LrcTagger;

/// <summary>单个音频文件的处理结果状态。</summary>
public enum ProcessStatus
{
    /// <summary>歌词已写入文件。</summary>
    Written,

    /// <summary>预览模式：配对成功、将写入。</summary>
    WouldWrite,

    /// <summary>标签内歌词与 .lrc 已一致，无需写入。</summary>
    UpToDate,

    /// <summary>同级目录没有同名 .lrc。</summary>
    NoLrcFile,

    /// <summary>处理失败（文件损坏、被占用、无写权限、写后校验不通过等）。</summary>
    Failed,
}

/// <summary>单个音频文件的处理结果。</summary>
public sealed record FileOutcome(string AudioPath, ProcessStatus Status, string Detail);
