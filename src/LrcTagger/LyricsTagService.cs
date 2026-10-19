using TagLib;
using TagLib.Ogg;

namespace LrcTagger;

/// <summary>
/// 内嵌歌词读写的唯一入口，写入行为对齐 Lyrico：
/// 统一写标准 LYRICS 字段（由 TagLibSharp 映射到 MP3 的 USLT 帧、M4A 的 ©lyr、FLAC/OGG 的 LYRICS 注释），
/// 并把历史遗留的歌词别名键一并清除，避免同一份歌词重复出现在多个字段中。
/// </summary>
public static class LyricsTagService
{
    /// <summary>歌词别名键，与 Lyrico 的 AudioTagWriteRules 保持一致。</summary>
    private static readonly string[] LyricsAliasKeys =
    [
        "UNSYNCED LYRICS", "USLT", "LYRIC", "LYRICSENG",
    ];

    /// <summary>读取文件当前的内嵌歌词；尚未写入过则为 null。</summary>
    public static string? ReadLyrics(string audioPath)
    {
        var file = TagLib.File.Create(audioPath);
        return file.Tag.Lyrics;
    }

    /// <summary>把歌词写入文件并落盘。</summary>
    public static void WriteLyrics(string audioPath, string lyrics)
    {
        var file = TagLib.File.Create(audioPath);

        // FLAC/OGG 的歌词别名以独立的 VorbisComment 字段存在，需要显式删除；
        // MP3 的 USLT 与 M4A 的 ©lyr 由 Tag.Lyrics 的 setter 原地覆盖，无需额外清理
        var xiphComment = file.Tag as XiphComment ?? file.GetTag(TagTypes.Xiph, false) as XiphComment;
        if (xiphComment is not null)
        {
            foreach (var aliasKey in LyricsAliasKeys)
            {
                xiphComment.RemoveField(aliasKey);
            }
        }

        file.Tag.Lyrics = lyrics;
        file.Save();
    }
}
