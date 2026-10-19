using System.Text;
using TagLib;
using TagLib.Ape;
using TagLib.Asf;
using TagLib.Id3v2;
using TagLib.Mpeg4;
using TagLib.Ogg;
using TagLib.Riff;
using ApeTag  = TagLib.Ape.Tag;
using AsfTag  = TagLib.Asf.Tag;
using Box     = TagLib.Mpeg4.Box;
using Id3v1Tag = TagLib.Id3v1.Tag;
using Id3v2Tag = TagLib.Id3v2.Tag;

namespace LrcTagger;

/// <summary>
/// 提取音频文件的全部标签信息：通用字段、音频属性、内嵌图片元信息，
/// 以及各标签容器中的原生字段（ID3v2 帧 / Vorbis 注释 / APE 项 / MP4 atom）。
/// 仅读取，不修改文件。
/// </summary>
public static class TagInfoService
{
    /// <summary>Vorbis 注释中以 base64 存储封面的字段，输出时只保留占位符。</summary>
    private static readonly string[] XiphPictureFields = ["METADATA_BLOCK_PICTURE", "COVERART"];

    /// <summary>MP4 data box 中表示文本的 data type（UTF-8 / UTF-16 及其排序变体）。</summary>
    private static readonly uint[] AppleTextDataTypes = [1, 2, 4, 5];

    public static AudioTagInfo Read(string audioPath, string relativePath)
    {
        using var file = TagLib.File.Create(audioPath);

        return new AudioTagInfo
        {
            Path = relativePath,
            FileName = Path.GetFileName(audioPath),
            SizeBytes = new FileInfo(audioPath).Length,
            Format = Optional(Path.GetExtension(audioPath).TrimStart('.').ToUpperInvariant()),
            MimeType = Optional(file.MimeType),
            TagTypes = ReadTagTypes(file.TagTypes),
            AudioProperties = ReadAudioProperties(file.Properties),
            Tags = ReadTagFields(file),
            Pictures = ReadPictures(file),
            RawTags = ReadRawTags(file),
        };
    }

    private static TagFieldsInfo ReadTagFields(TagLib.File file)
    {
        var tag = file.Tag;

        return new TagFieldsInfo
        {
            Title = Optional(tag.Title),
            TitleSort = Optional(tag.TitleSort),
            Subtitle = Optional(tag.Subtitle),
            Description = Optional(tag.Description),

            Performers = Optional(tag.Performers),
            PerformersSort = Optional(tag.PerformersSort),
            PerformersRole = Optional(tag.PerformersRole),
            AlbumArtists = Optional(tag.AlbumArtists),
            AlbumArtistsSort = Optional(tag.AlbumArtistsSort),
            Composers = Optional(tag.Composers),
            ComposersSort = Optional(tag.ComposersSort),

            Album = Optional(tag.Album),
            AlbumSort = Optional(tag.AlbumSort),
            Comment = Optional(tag.Comment),
            Genres = Optional(tag.Genres),

            Year = Optional(tag.Year),
            Track = Optional(tag.Track),
            TrackCount = Optional(tag.TrackCount),
            Disc = Optional(tag.Disc),
            DiscCount = Optional(tag.DiscCount),
            BeatsPerMinute = Optional(tag.BeatsPerMinute),

            Lyrics = Optional(tag.Lyrics),
            Grouping = Optional(tag.Grouping),
            Conductor = Optional(tag.Conductor),
            Copyright = Optional(tag.Copyright),
            Publisher = Optional(tag.Publisher),
            RemixedBy = Optional(tag.RemixedBy),
            InitialKey = Optional(tag.InitialKey),
            Isrc = Optional(tag.ISRC),
            IsCompilation = ReadIsCompilation(file),
            DateTagged = tag.DateTagged,

            MusicBrainzArtistId = Optional(tag.MusicBrainzArtistId),
            MusicBrainzReleaseArtistId = Optional(tag.MusicBrainzReleaseArtistId),
            MusicBrainzReleaseId = Optional(tag.MusicBrainzReleaseId),
            MusicBrainzReleaseGroupId = Optional(tag.MusicBrainzReleaseGroupId),
            MusicBrainzTrackId = Optional(tag.MusicBrainzTrackId),
            MusicBrainzDiscId = Optional(tag.MusicBrainzDiscId),
            MusicBrainzReleaseStatus = Optional(tag.MusicBrainzReleaseStatus),
            MusicBrainzReleaseType = Optional(tag.MusicBrainzReleaseType),
            MusicBrainzReleaseCountry = Optional(tag.MusicBrainzReleaseCountry),
            MusicIpId = Optional(tag.MusicIpId),
            AmazonId = Optional(tag.AmazonId),

            ReplayGainTrackGain = OptionalNonZero(tag.ReplayGainTrackGain),
            ReplayGainTrackPeak = OptionalNonZero(tag.ReplayGainTrackPeak),
            ReplayGainAlbumGain = OptionalNonZero(tag.ReplayGainAlbumGain),
            ReplayGainAlbumPeak = OptionalNonZero(tag.ReplayGainAlbumPeak),
        };
    }

    /// <summary>IsCompilation 不在 TagLib.Tag 基类上，需按具体的标签容器读取。</summary>
    private static bool? ReadIsCompilation(TagLib.File file)
    {
        foreach (var tagType in GetPresentTagTypes(file.TagTypes))
        {
            bool? value = file.GetTag(tagType) switch
            {
                XiphComment xiph => xiph.IsCompilation,
                Id3v2Tag id3v2 => id3v2.IsCompilation,
                AppleTag apple => apple.IsCompilation,
                _ => null,
            };

            if (value == true)
            {
                return true;
            }

            if (value == false)
            {
                return false;
            }
        }

        return null;
    }

    private static AudioPropertiesInfo ReadAudioProperties(Properties properties)
    {
        return new AudioPropertiesInfo
        {
            Duration = FormatDuration(properties.Duration),
            DurationSeconds = Math.Round(properties.Duration.TotalSeconds, 3),
            BitrateKbps = Optional(properties.AudioBitrate),
            SampleRateHz = Optional(properties.AudioSampleRate),
            Channels = Optional(properties.AudioChannels),
            BitsPerSample = Optional(properties.BitsPerSample),
            MediaTypes = Optional(properties.MediaTypes.ToString()),
            Description = Optional(properties.Description),
            Codecs = ReadCodecs(properties.Codecs),
            VideoWidth = Optional(properties.VideoWidth),
            VideoHeight = Optional(properties.VideoHeight),
        };
    }

    private static IReadOnlyList<CodecInfo>? ReadCodecs(IEnumerable<ICodec> codecs)
    {
        var result = codecs
            .Select(codec => new CodecInfo
            {
                Description = Optional(codec.Description),
                MediaTypes = Optional(codec.MediaTypes.ToString()),
                DurationSeconds = codec.Duration > TimeSpan.Zero
                    ? Math.Round(codec.Duration.TotalSeconds, 3)
                    : null,
            })
            .ToArray();

        return result.Length > 0 ? result : null;
    }

    /// <summary>
    /// 逐个标签容器收集图片。部分格式（如 FLAC）的 CombinedTag.Pictures 不会回退到
    /// 次级标签，直接读 file.Tag.Pictures 会漏掉封面，因此这里显式遍历所有标签类型。
    /// </summary>
    private static IReadOnlyList<PictureInfo>? ReadPictures(TagLib.File file)
    {
        var pictures = GetPresentTagTypes(file.TagTypes)
            .Select(tagType => file.GetTag(tagType))
            .Where(tag => tag is not null)
            .SelectMany(tag => tag!.Pictures)
            .ToArray();

        if (pictures.Length == 0)
        {
            return null;
        }

        return pictures
            .Select(picture => new PictureInfo
            {
                Type = picture.Type.ToString(),
                MimeType = Optional(picture.MimeType),
                Description = Optional(picture.Description),
                Filename = Optional(picture.Filename),
                SizeBytes = picture.Data.Count,
            })
            .ToArray();
    }

    private static IReadOnlyList<RawTagGroup>? ReadRawTags(TagLib.File file)
    {
        var groups = new List<RawTagGroup>();

        foreach (var tagType in GetPresentTagTypes(file.TagTypes))
        {
            var fields = file.GetTag(tagType) switch
            {
                XiphComment xiph => ReadXiphFields(xiph),
                ApeTag ape       => ReadApeFields(ape),
                Id3v2Tag id3v2   => ReadId3v2Fields(id3v2),
                AppleTag apple   => ReadAppleFields(apple),
                AsfTag asf       => ReadAsfFields(asf),
                InfoTag riffInfo => ReadRiffInfoFields(riffInfo),
                MovieIdTag movieId => ReadMovieIdFields(movieId),
                DivXTag divx     => ReadDivXFields(divx),
                Id3v1Tag id3v1   => ReadId3v1Fields(id3v1),
                _ => null,
            };

            if (fields is { Count: > 0 })
            {
                groups.Add(new RawTagGroup(tagType.ToString(), fields));
            }
        }

        return groups.Count > 0 ? groups : null;
    }

    private static SortedDictionary<string, string[]> ReadXiphFields(XiphComment xiph)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in xiph)
        {
            fields[key] = IsXiphPictureField(key)
                ? ["<图片数据>"]
                : xiph.GetField(key) ?? [];
        }

        return fields;
    }

    private static SortedDictionary<string, string[]> ReadApeFields(ApeTag ape)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in ape)
        {
            var item = ape.GetItem(key);
            if (item is null)
            {
                continue;
            }

            fields[key] = item.Type == ItemType.Text
                ? item.ToStringArray()
                : [$"<{item.Type}，{item.Value.Count} 字节>"];
        }

        return fields;
    }

    private static SortedDictionary<string, string[]> ReadId3v2Fields(Id3v2Tag id3v2)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var frame in id3v2.GetFrames())
        {
            var key = Encoding.ASCII.GetString(frame.FrameId.Data);
            var values = DescribeId3v2Frame(frame);

            fields[key] = fields.TryGetValue(key, out var existing)
                ? [.. existing, .. values]
                : values;
        }

        return fields;
    }

    private static string[] DescribeId3v2Frame(Frame frame)
    {
        return frame switch
        {
            CommentsFrame comments => [WithContext(comments.Text, comments.Description, comments.Language)],
            UnsynchronisedLyricsFrame lyrics => [WithContext(lyrics.Text, lyrics.Description, lyrics.Language)],
            UserTextInformationFrame userText => userText.Text
                .Select(text => WithContext(text, userText.Description, null))
                .ToArray(),
            TextInformationFrame text => Clean(text.Text),
            UserUrlLinkFrame userUrl => userUrl.Text
                .Select(url => WithContext(url, userUrl.Description, null))
                .ToArray(),
            UrlLinkFrame url => Clean(url.Text),
            AttachmentFrame picture => [$"<{picture.MimeType}，{picture.Data.Count} 字节>"],
            PrivateFrame privateFrame => [$"<{privateFrame.Owner}，{privateFrame.PrivateData.Count} 字节>"],
            PopularimeterFrame rating => [$"<{rating.User}，评分 {rating.Rating}，播放 {rating.PlayCount} 次>"],
            UniqueFileIdentifierFrame identifier => [$"<{identifier.Owner}，{identifier.Identifier.Count} 字节>"],
            SynchronisedLyricsFrame synced => [$"<同步歌词，{synced.Text.Length} 行>"],
            _ => [$"<{frame.GetType().Name}，{frame.Size} 字节>"],
        };
    }

    private static SortedDictionary<string, string[]> ReadAppleFields(AppleTag apple)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var box in apple)
        {
            var key = Encoding.Latin1.GetString(box.BoxType.Data);
            var values = DescribeAppleBox(box);

            fields[key] = fields.TryGetValue(key, out var existing)
                ? [.. existing, .. values]
                : values;
        }

        return fields;
    }

    private static string[] DescribeAppleBox(Box box)
    {
        var values = new List<string>();

        foreach (var child in box.Children)
        {
            switch (child)
            {
                case AppleDataBox dataBox when AppleTextDataTypes.Contains(dataBox.Flags) && dataBox.Text is { } text:
                    values.Add(text);
                    break;
                case AppleDataBox dataBox:
                    values.Add($"<data type {dataBox.Flags}，{dataBox.Data.Count} 字节>");
                    break;
                case AppleAnnotationBox annotation:
                    values.Add(Encoding.UTF8.GetString(annotation.Data.Data));
                    break;
                default:
                    values.Add($"<{child.GetType().Name}，{child.Size} 字节>");
                    break;
            }
        }

        return values.Count > 0 ? values.ToArray() : [$"<{box.GetType().Name}，{box.Size} 字节>"];
    }

    private static IEnumerable<TagTypes> GetPresentTagTypes(TagTypes tagTypes)
    {
        return Enum.GetValues<TagTypes>()
            .Where(type => type is not (TagTypes.None or TagTypes.AllTags) && tagTypes.HasFlag(type));
    }

    private static IReadOnlyList<string>? ReadTagTypes(TagTypes tagTypes)
    {
        var names = GetPresentTagTypes(tagTypes)
            .Select(type => type.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return names.Length > 0 ? names : null;
    }

    private static string WithContext(string? text, string? description, string? language)
    {
        var context = new List<string>(2);

        if (!string.IsNullOrEmpty(description))
        {
            context.Add(description);
        }

        if (!string.IsNullOrEmpty(language))
        {
            context.Add(language);
        }

        var value = text ?? string.Empty;
        return context.Count == 0 ? value : $"[{string.Join(" / ", context)}] {value}";
    }

    private static bool IsXiphPictureField(string key)
    {
        return XiphPictureFields.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    // ── ASF (WMA / Windows Media) ──────────────────────────────────────────────

    /// <summary>
    /// ASF 标签实现 IEnumerable&lt;ContentDescriptor&gt;，直接迭代即可拿到所有描述符。
    /// 非文本类型（Binary、Guid）保留占位符，其余转为字符串值。
    /// </summary>
    private static SortedDictionary<string, string[]> ReadAsfFields(AsfTag asf)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var descriptor in asf)
        {
            var value = descriptor.Type switch
            {
                DataType.Bytes => $"<Bytes，{descriptor.ToByteVector().Count} 字节>",
                DataType.Guid  => "<Guid>",
                DataType.Bool  => descriptor.ToBool().ToString().ToLowerInvariant(),
                DataType.DWord => descriptor.ToDWord().ToString(),
                DataType.QWord => descriptor.ToQWord().ToString(),
                DataType.Word  => descriptor.ToWord().ToString(),
                _              => descriptor.ToString(), // Unicode
            };

            if (fields.TryGetValue(descriptor.Name, out var existing))
                fields[descriptor.Name] = [.. existing, value];
            else
                fields[descriptor.Name] = [value];
        }

        return fields;
    }

    // ── RIFF INFO (WAV / AVI 等 RIFF 容器) ────────────────────────────────────

    /// <summary>
    /// RIFF INFO 使用四字符码（FOURCC）标识字段，TagLib 只暴露属性接口不提供枚举，
    /// 这里按 RIFF INFO 标准将属性映射回对应的 FOURCC 键。
    /// </summary>
    private static SortedDictionary<string, string[]>? ReadRiffInfoFields(InfoTag riffInfo)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        AddRaw(fields, "INAM", riffInfo.Title);
        AddRaw(fields, "IART", riffInfo.Performers);
        AddRaw(fields, "IPRD", riffInfo.Album);
        AddRaw(fields, "ICMT", riffInfo.Comment);
        AddRaw(fields, "IGNR", riffInfo.Genres);
        AddRaw(fields, "ICRD", riffInfo.Year);
        AddRawTrack(fields, "ITRK", riffInfo.Track, riffInfo.TrackCount);
        AddRaw(fields, "IENG", riffInfo.Conductor);
        AddRaw(fields, "ICOP", riffInfo.Copyright);
        AddRaw(fields, "ISBJ", riffInfo.Description);

        return fields.Count > 0 ? fields : null;
    }

    // ── RIFF MovieId ───────────────────────────────────────────────────────────

    private static SortedDictionary<string, string[]>? ReadMovieIdFields(MovieIdTag movieId)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        // MovieId 使用 LIST-MID 格式，字段 FOURCC
        AddRaw(fields, "TITL", movieId.Title);
        AddRaw(fields, "ISBJ", movieId.Performers);
        AddRaw(fields, "COMM", movieId.Comment);
        AddRaw(fields, "GENR", movieId.Genres);
        AddRawTrack(fields, "PRT1", movieId.Track, movieId.TrackCount);

        return fields.Count > 0 ? fields : null;
    }

    // ── RIFF DivX ──────────────────────────────────────────────────────────────

    private static SortedDictionary<string, string[]>? ReadDivXFields(DivXTag divx)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        // DivX 扩展标签为固定偏移结构，无标准键名，使用描述性标识
        AddRaw(fields, "Title",   divx.Title);
        AddRaw(fields, "Artist",  divx.Performers);
        AddRaw(fields, "Year",    divx.Year);
        AddRaw(fields, "Comment", divx.Comment);
        AddRaw(fields, "Genre",   divx.Genres);

        return fields.Count > 0 ? fields : null;
    }

    // ── ID3v1 ──────────────────────────────────────────────────────────────────

    /// <summary>ID3v1 是固定长度结构，无命名键，使用字段功能名称作为键。</summary>
    private static SortedDictionary<string, string[]>? ReadId3v1Fields(Id3v1Tag id3v1)
    {
        var fields = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        AddRaw(fields, "Title",   id3v1.Title);
        AddRaw(fields, "Artist",  id3v1.Performers);
        AddRaw(fields, "Album",   id3v1.Album);
        AddRaw(fields, "Year",    id3v1.Year);
        AddRaw(fields, "Comment", id3v1.Comment);
        AddRawTrack(fields, "Track", id3v1.Track, 0);
        AddRaw(fields, "Genre",   id3v1.Genres);

        return fields.Count > 0 ? fields : null;
    }

    // ── 原生字段写入辅助 ────────────────────────────────────────────────────────

    private static void AddRaw(SortedDictionary<string, string[]> fields, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            fields[key] = [value];
    }

    private static void AddRaw(SortedDictionary<string, string[]> fields, string key, string[]? values)
    {
        var nonEmpty = values?.Where(v => !string.IsNullOrEmpty(v)).ToArray();
        if (nonEmpty is { Length: > 0 })
            fields[key] = nonEmpty;
    }

    private static void AddRaw(SortedDictionary<string, string[]> fields, string key, uint value)
    {
        if (value > 0)
            fields[key] = [value.ToString()];
    }

    private static void AddRawTrack(
        SortedDictionary<string, string[]> fields, string key, uint track, uint trackCount)
    {
        if (track == 0)
            return;

        fields[key] = [trackCount > 0 ? $"{track}/{trackCount}" : track.ToString()];
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string? Optional(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string[]? Optional(IEnumerable<string>? values)
    {
        var result = values?.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        return result is { Length: > 0 } ? result : null;
    }

    private static string[] Clean(string?[] values)
    {
        return values.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToArray();
    }

    private static uint? Optional(uint value)
    {
        return value == 0 ? null : value;
    }

    private static int? Optional(int value)
    {
        return value > 0 ? value : null;
    }

    private static double? OptionalNonZero(double value)
    {
        return double.IsFinite(value) && value != 0 ? value : null;
    }
}
