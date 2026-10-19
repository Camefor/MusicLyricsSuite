using System.Text;
using LyricsFetcher.Services;

namespace LyricsSuite;

/// <summary>
/// LyricsSuite 入口：交互式选择专辑目录 → 调用 <see cref="AlbumPipeline"/> 完成"下载 + 嵌入"。
/// 也支持命令行参数直接指定目录，便于脚本调用：
///   lyrics-suite &lt;专辑目录&gt; [--artist 歌手名] [--overwrite] [--delay 毫秒]
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsageError = 2;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向且没有控制台时忽略
        }

        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return ExitOk;
        }

        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            Console.Error.WriteLine();
            PrintHelp();
            return ExitUsageError;
        }

        PrintBanner();

        var albumDirectory = cli.AlbumDirectory ?? PromptAlbumDirectory();
        if (string.IsNullOrWhiteSpace(albumDirectory))
        {
            Console.Error.WriteLine("未输入专辑目录，已取消。");
            return ExitUsageError;
        }

        var fullPath = Path.GetFullPath(albumDirectory);
        if (!Directory.Exists(fullPath))
        {
            Console.Error.WriteLine($"目录不存在：{fullPath}");
            return ExitUsageError;
        }

        var pipelineOptions = new PipelineOptions
        {
            DefaultArtist = cli.DefaultArtist,
            OverwriteLrc = cli.Overwrite,
            PerTrackDelayMs = cli.DelayMs,
        };

        using var client = new LrcLibClient();
        var fetcher = new LyricsFetcherService(client);
        var pipeline = new AlbumPipeline(fetcher, pipelineOptions);
        var reporter = new ConsolePipelineReporter();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // 允许 Ctrl+C 优雅取消：触发一次取消，第二次直接退出
            e.Cancel = !cts.IsCancellationRequested;
            cts.Cancel();
        };

        try
        {
            await pipeline.RunAsync(fullPath, reporter, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("已取消。");
            return ExitUsageError;
        }

        return ExitOk;
    }

    private static string? PromptAlbumDirectory()
    {
        Console.Write("请输入专辑目录路径（可拖拽文件夹到此处）：");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // 粘贴 / 拖拽时常自带包裹的引号，这里统一剥掉
        return input.Trim().Trim('"', '\'');
    }

    private static void PrintBanner()
    {
        Console.WriteLine("======== LyricsSuite ========");
        Console.WriteLine("流程：选专辑 → 读标签 → 拉歌词 → 嵌入音频");
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            lyrics-suite —— 一体化歌词处理：下载 LRCLIB 歌词并嵌入音频文件

            用法：
              lyrics-suite                           交互模式，运行时提示输入专辑目录
              lyrics-suite <专辑目录>                 直接处理指定专辑
              lyrics-suite <专辑目录> [选项]

            选项：
              --artist <歌手名>   标签缺失时使用的默认歌手
              --overwrite         覆盖已存在的 .lrc 并重新下载
              --delay <毫秒>      相邻两首之间的间隔，默认 500，最低 200
              -h, --help          显示本帮助

            流程说明：
              1. 递归扫描专辑目录中的 MP3/FLAC/M4A/MP4/AAC/OGG/OPUS/WAV/WMA/APE/AIFF
              2. 读取音频标签拿到标题 / 歌手 / 专辑 / 时长
              3. 调用 LRCLIB 下载同步歌词，保存为同名 .lrc
              4. 把 .lrc 嵌入音频的内嵌歌词标签（MP3→USLT / M4A→©lyr / FLAC/OGG→LYRICS）

            子工具（可单独运行）：
              lyrics-fetcher     只做下载 .lrc 这一步
              lrc-tagger         只做嵌入这一步，支持 --dry-run / --list / --tags
            """);
    }
}

internal sealed class CliOptions
{
    public string? AlbumDirectory { get; init; }
    public string DefaultArtist { get; init; } = "";
    public bool Overwrite { get; init; }
    public int DelayMs { get; init; } = 500;

    public static CliOptions Parse(string[] args)
    {
        string? album = null;
        var artist = "";
        var overwrite = false;
        var delay = 500;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
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
                case "-h" or "--help":
                    // 由上层单独处理；跳过
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"未知参数：{arg}");
                    }
                    if (album is not null)
                    {
                        throw new ArgumentException("只能指定一个专辑目录");
                    }
                    album = arg;
                    break;
            }
        }

        return new CliOptions
        {
            AlbumDirectory = album,
            DefaultArtist = artist,
            Overwrite = overwrite,
            DelayMs = delay,
        };
    }
}
