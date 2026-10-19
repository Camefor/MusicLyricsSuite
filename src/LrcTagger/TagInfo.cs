namespace LrcTagger;

/// <summary>一次标签导出任务的顶层结果。</summary>
public sealed record TagInfoReport(
    string Target,
    string TargetType,
    int FileCount,
    IReadOnlyList<AudioTagInfo> Files);

/// <summary>单个音频文件的标签信息；读取失败时只包含 Path、FileName 与 Error。</summary>
public sealed record AudioTagInfo
{
    public required string Path { get; init; }
    public string? FileName { get; init; }
    public string? Error { get; init; }
    public long? SizeBytes { get; init; }
    public string? Format { get; init; }
    public string? MimeType { get; init; }
    public IReadOnlyList<string>? TagTypes { get; init; }
    public AudioPropertiesInfo? AudioProperties { get; init; }
    public TagFieldsInfo? Tags { get; init; }
    public IReadOnlyList<PictureInfo>? Pictures { get; init; }
    public IReadOnlyList<RawTagGroup>? RawTags { get; init; }
}

/// <summary>音频流属性（时长、码率、采样率等）。</summary>
public sealed record AudioPropertiesInfo
{
    public string? Duration { get; init; }
    public double? DurationSeconds { get; init; }
    public int? BitrateKbps { get; init; }
    public int? SampleRateHz { get; init; }
    public int? Channels { get; init; }
    public int? BitsPerSample { get; init; }
    public string? MediaTypes { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<CodecInfo>? Codecs { get; init; }
    public int? VideoWidth { get; init; }
    public int? VideoHeight { get; init; }
}

/// <summary>单个编码流的描述信息。</summary>
public sealed record CodecInfo
{
    public string? Description { get; init; }
    public string? MediaTypes { get; init; }
    public double? DurationSeconds { get; init; }
}

/// <summary>TagLib 归一化后的通用标签字段；空字段不输出。</summary>
public sealed record TagFieldsInfo
{
    public string? Title { get; init; }
    public string? TitleSort { get; init; }
    public string? Subtitle { get; init; }
    public string? Description { get; init; }

    public string[]? Performers { get; init; }
    public string[]? PerformersSort { get; init; }
    public string[]? PerformersRole { get; init; }
    public string[]? AlbumArtists { get; init; }
    public string[]? AlbumArtistsSort { get; init; }
    public string[]? Composers { get; init; }
    public string[]? ComposersSort { get; init; }

    public string? Album { get; init; }
    public string? AlbumSort { get; init; }
    public string? Comment { get; init; }
    public string[]? Genres { get; init; }

    public uint? Year { get; init; }
    public uint? Track { get; init; }
    public uint? TrackCount { get; init; }
    public uint? Disc { get; init; }
    public uint? DiscCount { get; init; }
    public uint? BeatsPerMinute { get; init; }

    public string? Lyrics { get; init; }
    public string? Grouping { get; init; }
    public string? Conductor { get; init; }
    public string? Copyright { get; init; }
    public string? Publisher { get; init; }
    public string? RemixedBy { get; init; }
    public string? InitialKey { get; init; }
    public string? Isrc { get; init; }
    public bool? IsCompilation { get; init; }
    public DateTime? DateTagged { get; init; }

    public string? MusicBrainzArtistId { get; init; }
    public string? MusicBrainzReleaseArtistId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseGroupId { get; init; }
    public string? MusicBrainzTrackId { get; init; }
    public string? MusicBrainzDiscId { get; init; }
    public string? MusicBrainzReleaseStatus { get; init; }
    public string? MusicBrainzReleaseType { get; init; }
    public string? MusicBrainzReleaseCountry { get; init; }
    public string? MusicIpId { get; init; }
    public string? AmazonId { get; init; }

    public double? ReplayGainTrackGain { get; init; }
    public double? ReplayGainTrackPeak { get; init; }
    public double? ReplayGainAlbumGain { get; init; }
    public double? ReplayGainAlbumPeak { get; init; }
}

/// <summary>内嵌图片的元信息（不输出图片二进制数据）。</summary>
public sealed record PictureInfo
{
    public required string Type { get; init; }
    public string? MimeType { get; init; }
    public string? Description { get; init; }
    public string? Filename { get; init; }
    public required int SizeBytes { get; init; }
}

/// <summary>某一种标签容器中的原生字段（ID3v2 帧、Vorbis 注释、APE 项、MP4 atom 等）。</summary>
public sealed record RawTagGroup(string TagType, IReadOnlyDictionary<string, string[]> Fields);
