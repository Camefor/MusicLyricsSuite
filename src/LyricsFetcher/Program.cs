using System.Text;
using LyricsFetcher.Models;
using LyricsFetcher.Services;

namespace LyricsFetcher;

/// <summary>
/// LyricsFetcher 独立 CLI 入口：扫描指定目录、调用 <see cref="LyricsFetcherService"/>
/// 为每首歌写入同名 .lrc，并把统计信息写入 lyrics-fetch-report.csv。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsageError = 2;

    private sealed class CliOptions
    {
        public string Root { get; init; } = "";
        public string DefaultArtist { get; init; } = "";
        public bool Overwrite { get; init; }
        public int DelayMs { get; init; } = 500;
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向且没有控制台时忽略
        }

        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return args.Length == 0 ? ExitUsageError : ExitOk;
        }

        CliOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            PrintHelp();
            return ExitUsageError;
        }

        if (!Directory.Exists(options.Root))
        {
            Console.Error.WriteLine($"目录不存在：{options.Root}");
            return ExitUsageError;
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".aiff", ".ape",
        };

        var audioFiles = Directory.EnumerateFiles(options.Root, "*", SearchOption.AllDirectories)
            .Where(p => extensions.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (audioFiles.Count == 0)
        {
            Console.WriteLine("没有找到支持的音频文件。");
            return ExitOk;
        }

        Console.WriteLine($"扫描目录：{options.Root}");
        Console.WriteLine($"发现音频：{audioFiles.Count} 首；默认歌手：{options.DefaultArtist}");
        Console.WriteLine("提示：程序会优先使用音频标签，标签缺失时才使用默认歌手和专辑文件夹名。");
        Console.WriteLine();

        var logPath = Path.Combine(options.Root, "lyrics-fetch-report.csv");
        var report = new List<string>
        {
            "AudioPath,Title,Artist,Album,Status,SourceId,OutputPath,Message",
        };

        int foundSynced = 0, foundPlain = 0, notFound = 0, empty = 0, skipped = 0, errors = 0;

        using var client = new LrcLibClient();
        using var fallback = new LiriQoClient();
        var service = new LyricsFetcherService(client, fallback);

        for (var i = 0; i < audioFiles.Count; i++)
        {
            var path = audioFiles[i];
            TrackMetadata track;
            try
            {
                track = AudioMetadataReader.Read(path, options.DefaultArtist);
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine($"[{i + 1}/{audioFiles.Count}] [读取失败] {path} — {ex.Message}");
                report.Add(Csv(path, "", "", "", "ERROR", "", "", ex.Message));
                continue;
            }

            Console.WriteLine($"[{i + 1}/{audioFiles.Count}] 查询：{track.Artist} - {track.Title} [{track.Album}]");
            var outcome = await service.FetchAsync(
                track,
                options.Overwrite,
                notice => Console.WriteLine($"    [提示] {notice}"));

            switch (outcome.Status)
            {
                case FetchStatus.FoundSynced:
                    foundSynced++;
                    Console.WriteLine($"    [成功·同步歌词] {outcome.OutputPath}");
                    break;
                case FetchStatus.FoundPlain:
                    foundPlain++;
                    Console.WriteLine($"    [成功·纯文本歌词] {outcome.OutputPath}（无时间轴）");
                    break;
                case FetchStatus.NotFound:
                    notFound++;
                    Console.WriteLine("    [未找到] LRCLIB 没有返回足够匹配的歌词");
                    break;
                case FetchStatus.Empty:
                    empty++;
                    Console.WriteLine("    [无歌词] 匹配记录没有歌词文本");
                    break;
                case FetchStatus.SkippedExists:
                    skipped++;
                    Console.WriteLine($"    [跳过已有歌词] {track.Title}");
                    break;
                case FetchStatus.Error:
                    errors++;
                    Console.WriteLine($"    [请求/写入失败] {outcome.Message}");
                    break;
            }

            report.Add(Csv(
                outcome.AudioPath,
                outcome.Title ?? "",
                outcome.Artist ?? "",
                outcome.Album ?? "",
                outcome.Status.ToString(),
                outcome.SourceId?.ToString() ?? "",
                outcome.OutputPath ?? "",
                outcome.Message));

            if (i < audioFiles.Count - 1)
            {
                await Task.Delay(options.DelayMs);
            }
        }

        await System.IO.File.WriteAllLinesAsync(logPath, report, new UTF8Encoding(true));

        Console.WriteLine();
        Console.WriteLine("========== 完成 ==========");
        Console.WriteLine($"发现音频：{audioFiles.Count}");
        Console.WriteLine($"同步歌词：{foundSynced}");
        Console.WriteLine($"纯文本歌词：{foundPlain}");
        Console.WriteLine($"未找到：{notFound}");
        Console.WriteLine($"记录无文本：{empty}");
        Console.WriteLine($"跳过已有文件：{skipped}");
        Console.WriteLine($"错误：{errors}");
        Console.WriteLine($"报告：{logPath}");
        Console.WriteLine("程序不会自动生成逐字 KTV 时间轴；LRCLIB 返回什么格式就保存什么格式。");
        return ExitOk;
    }

    private static string Csv(params string[] values) =>
        string.Join(",", values.Select(v => "\"" + (v ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\""));

    private static CliOptions ParseOptions(string[] args)
    {
        var root = args[0];
        var artist = "";
        var overwrite = false;
        var delay = 500;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--artist":
                    if (++i >= args.Length) throw new ArgumentException("--artist 后需要歌手名");
                    artist = args[i];
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--delay":
                    if (++i >= args.Length || !int.TryParse(args[i], out delay) || delay < 200)
                    {
                        throw new ArgumentException("--delay 需要一个不小于 200 的毫秒数");
                    }
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[i]}");
            }
        }

        return new CliOptions
        {
            Root = Path.GetFullPath(root),
            DefaultArtist = artist,
            Overwrite = overwrite,
            DelayMs = delay,
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            lyrics-fetcher —— 从 LRCLIB 下载歌词并保存为同名 .lrc

            用法：
              lyrics-fetcher <目录> [选项]

            选项：
              --artist <歌手名>   标签缺失时使用的默认歌手
              --overwrite         覆盖已存在的 .lrc
              --delay <毫秒>      相邻两首之间的间隔，默认 500，最低 200
              -h, --help          显示本帮助

            说明：
              - 递归扫描 MP3 / FLAC / M4A / AAC / OGG / OPUS / WAV / WMA / AIFF / APE
              - 优先读取音频标签；标签缺失时专辑名回退到所在目录名
              - 优先获取同步歌词 syncedLyrics；仅有 plainLyrics 时保存纯文本 .lrc（无时间轴）
              - 默认不覆盖已有 .lrc；使用 --overwrite 可覆盖
              - 在根目录生成 lyrics-fetch-report.csv，便于复核 NOT_FOUND / ERROR 等
            """);
    }
}
