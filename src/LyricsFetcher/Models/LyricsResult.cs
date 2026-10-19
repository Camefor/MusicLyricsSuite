using System.Text.Json.Serialization;

namespace LyricsFetcher.Models;

/// <summary>
/// LRCLIB API 返回的歌词记录。字段名严格匹配接口返回，用于反序列化。
/// </summary>
public sealed class LyricsResult
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("trackName")] public string? TrackName { get; set; }
    [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
    [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
    [JsonPropertyName("duration")] public double? Duration { get; set; }
    [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
    [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }

    /// <summary>LRCLIB 在不同端点下 Title 字段的字段名不一致，统一按 TrackName → Name 回退。</summary>
    public string Title => !string.IsNullOrWhiteSpace(TrackName) ? TrackName! : Name ?? "";
}
