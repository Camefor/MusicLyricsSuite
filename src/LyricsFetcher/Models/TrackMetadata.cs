namespace LyricsFetcher.Models;

/// <summary>
/// 单首歌曲用于查询 LRCLIB 的元数据。
/// 由音频标签或上层调用者填充；标签缺失时上层应回退到合理默认值（如目录名）。
/// </summary>
public sealed class TrackMetadata
{
    /// <summary>音频文件绝对路径。</summary>
    public required string AudioPath { get; init; }

    /// <summary>曲目标题（优先取音频标签 Title，缺失时上层应回退到文件名）。</summary>
    public required string Title { get; init; }

    /// <summary>歌手名（优先取音频标签 FirstPerformer，缺失时上层应提供默认值）。</summary>
    public required string Artist { get; init; }

    /// <summary>专辑名（优先取音频标签 Album，缺失时上层通常回退到目录名）。</summary>
    public required string Album { get; init; }

    /// <summary>曲目时长（秒）。0 表示未知；LRCLIB 的 /get 接口在已知时长时匹配更精确。</summary>
    public required double Duration { get; init; }

    /// <summary>将保存 .lrc 的目标路径（与音频同名、同目录，扩展名 .lrc）。</summary>
    public required string OutputPath { get; init; }
}
