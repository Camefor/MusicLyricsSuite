namespace LrcTagger;

/// <summary>参与扫描的音频扩展名（对应 TagLib 支持编辑标签的格式）。</summary>
public static class AudioFileCatalog
{
    public static readonly string[] SupportedExtensions =
    [
        ".mp3", ".flac", ".m4a", ".m4b", ".mp4", ".aac",
        ".ogg", ".oga", ".wav", ".ape", ".wma", ".aif", ".aiff",
    ];

    public static bool IsSupported(string path)
    {
        return SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
