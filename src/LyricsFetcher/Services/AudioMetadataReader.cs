using LyricsFetcher.Models;

namespace LyricsFetcher.Services;

/// <summary>
/// 从单个音频文件读取查询 LRCLIB 所需的元数据。
/// 优先取音频标签；标签缺失时按文件名 / 目录名 / 调用方指定的默认歌手回退。
/// </summary>
public static class AudioMetadataReader
{
    /// <param name="path">音频文件绝对路径。</param>
    /// <param name="defaultArtist">歌手标签缺失时使用的默认值；为空则 Artist 可能为空。</param>
    public static TrackMetadata Read(string path, string defaultArtist = "")
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var artist = defaultArtist;
        var album = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
        var duration = 0d;

        using var audio = TagLib.File.Create(path);
        var tag = audio.Tag;
        if (!string.IsNullOrWhiteSpace(tag.Title)) title = tag.Title.Trim();
        if (!string.IsNullOrWhiteSpace(tag.FirstPerformer)) artist = tag.FirstPerformer.Trim();
        if (!string.IsNullOrWhiteSpace(tag.Album)) album = tag.Album.Trim();
        duration = audio.Properties.Duration.TotalSeconds;

        // 把 .lrc 放在音频同目录、同基名，播放器才能自动配对
        var output = Path.ChangeExtension(path, ".lrc");

        return new TrackMetadata
        {
            AudioPath = path,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            OutputPath = output,
        };
    }
}
