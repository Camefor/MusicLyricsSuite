using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LrcTagger;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitProcessingFailure = 1;
    private const int ExitUsageError = 2;

    private static readonly JsonSerializerOptions TagJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        // 保证中文路径与歌词在控制台正常显示
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向且没有控制台时忽略
        }

        if (args.Length == 0)
        {
            CommandLineOptions.PrintUsage();
            return ExitUsageError;
        }

        if (args.Contains("-h") || args.Contains("--help"))
        {
            CommandLineOptions.PrintUsage();
            return ExitOk;
        }

        var options = CommandLineOptions.TryParse(args, out var parseError);
        if (options is null)
        {
            Console.Error.WriteLine($"参数错误：{parseError}");
            Console.Error.WriteLine();
            CommandLineOptions.PrintUsage();
            return ExitUsageError;
        }

        return Run(options);
    }

    private static int Run(CommandLineOptions options)
    {
        var audioFiles = ScanAudioFiles(options);
        var root = options.TargetIsDirectory ? options.TargetPath : Path.GetDirectoryName(options.TargetPath)!;

        if (options.DumpTags)
        {
            return RunTags(audioFiles, root, options);
        }

        if (audioFiles.Count == 0)
        {
            Console.WriteLine($"目录中未找到可处理的音频文件：{options.TargetPath}");
            return ExitOk;
        }

        var modeText = options.ListOnly ? "列出歌词现状" : options.DryRun ? "预览模式，不修改文件" : "写入歌词";
        Console.WriteLine(options.TargetIsDirectory ? $"目录：{options.TargetPath}" : $"文件：{options.TargetPath}");
        Console.WriteLine($"共 {audioFiles.Count} 个音频文件（{modeText}）");
        Console.WriteLine();

        return options.ListOnly ? RunList(audioFiles, root) : RunWrite(audioFiles, options, root);
    }

    /// <summary>目标为目录时递归扫描；目标为单个音频文件时直接返回该文件。</summary>
    private static List<string> ScanAudioFiles(CommandLineOptions options)
    {
        if (!options.TargetIsDirectory)
        {
            return [options.TargetPath];
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        // 固定排序，保证每次运行输出顺序一致、报告可对照
        return Directory.EnumerateFiles(options.TargetPath, "*", enumerationOptions)
            .Where(AudioFileCatalog.IsSupported)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RunWrite(List<string> audioFiles, CommandLineOptions options, string root)
    {
        var outcomes = audioFiles.Select(audioPath => ProcessFile(audioPath, options, root)).ToList();

        // 预览模式下实际不会写入，"将写入"与"写入"两种措辞避免误读
        var writeCount = outcomes.Count(o => o.Status is ProcessStatus.Written or ProcessStatus.WouldWrite);
        var writeVerb = options.DryRun ? "将写入" : "写入";

        Console.WriteLine();
        Console.WriteLine($"完成：{writeVerb} {writeCount}" +
                          $" | 一致 {outcomes.Count(o => o.Status == ProcessStatus.UpToDate)}" +
                          $" | 跳过 {outcomes.Count(o => o.Status == ProcessStatus.NoLrcFile)}" +
                          $" | 失败 {outcomes.Count(o => o.Status == ProcessStatus.Failed)}");

        return outcomes.Any(o => o.Status == ProcessStatus.Failed) ? ExitProcessingFailure : ExitOk;
    }

    /// <summary>处理单个音频文件：同名配对 .lrc → 读取 → 比对 → 写入（含可选备份与写后校验）。</summary>
    private static FileOutcome ProcessFile(string audioPath, CommandLineOptions options, string root)
    {
        var relativePath = Path.GetRelativePath(root, audioPath);

        try
        {
            // 同名配对：Windows 文件系统不区分大小写，File.Exists 自动兼容 .LRC 后缀
            var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
            if (!File.Exists(lrcPath))
            {
                return Report(new FileOutcome(relativePath, ProcessStatus.NoLrcFile, "无同名 .lrc"));
            }

            var (lrcText, encodingName) = LrcFileReader.Read(lrcPath);
            if (string.IsNullOrWhiteSpace(lrcText))
            {
                // 拒绝空歌词，防止误清掉文件里已有的歌词
                return Report(new FileOutcome(relativePath, ProcessStatus.Failed,
                    $"歌词文件内容为空：{Path.GetFileName(lrcPath)}"));
            }

            if (LyricsTagService.ReadLyrics(audioPath) == lrcText)
            {
                return Report(new FileOutcome(relativePath, ProcessStatus.UpToDate, "歌词与 .lrc 已一致，未改动"));
            }

            var detail = $"← {Path.GetFileName(lrcPath)}（{encodingName}，{lrcText.Length} 字符）";
            if (options.DryRun)
            {
                return Report(new FileOutcome(relativePath, ProcessStatus.WouldWrite, detail));
            }

            if (options.Backup)
            {
                File.Copy(audioPath, audioPath + ".bak", overwrite: true);
            }

            LyricsTagService.WriteLyrics(audioPath, lrcText);

            // 写后重读校验（与 Lyrico 保存后的重读校验行为一致），确认歌词真正落盘
            if (LyricsTagService.ReadLyrics(audioPath) != lrcText)
            {
                return Report(new FileOutcome(relativePath, ProcessStatus.Failed,
                    "写入后校验未通过，文件可能未被正确修改"));
            }

            return Report(new FileOutcome(relativePath, ProcessStatus.Written, detail));
        }
        catch (Exception ex)
        {
            // 文件损坏、被播放器占用、无写权限等：单个文件失败不影响其余文件
            return Report(new FileOutcome(relativePath, ProcessStatus.Failed, $"{ex.GetType().Name}：{ex.Message}"));
        }
    }

    /// <summary>导出模式下把每个文件的标签信息序列化为 JSON；单文件失败不影响其余文件。</summary>
    private static int RunTags(List<string> audioFiles, string root, CommandLineOptions options)
    {
        var files = audioFiles.Select(audioPath => ReadTagInfo(audioPath, root)).ToList();
        var report = new TagInfoReport(
            options.TargetPath,
            options.TargetIsDirectory ? "directory" : "file",
            files.Count,
            files);

        Console.WriteLine(JsonSerializer.Serialize(report, TagJsonOptions));

        return files.Any(file => file.Error is not null) ? ExitProcessingFailure : ExitOk;
    }

    private static AudioTagInfo ReadTagInfo(string audioPath, string root)
    {
        var relativePath = Path.GetRelativePath(root, audioPath);

        try
        {
            return TagInfoService.Read(audioPath, relativePath);
        }
        catch (Exception ex)
        {
            return new AudioTagInfo
            {
                Path = relativePath,
                FileName = Path.GetFileName(audioPath),
                Error = $"{ex.GetType().Name}：{ex.Message}",
            };
        }
    }

    /// <summary>列出模式下逐个读取文件的歌词现状，不做任何修改。</summary>
    private static int RunList(List<string> audioFiles, string root)
    {
        var hasLyrics = 0;
        var noLyrics = 0;
        var errors = 0;

        foreach (var audioPath in audioFiles)
        {
            var relativePath = Path.GetRelativePath(root, audioPath);
            try
            {
                var lyrics = LyricsTagService.ReadLyrics(audioPath);
                var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
                var lrcNote = File.Exists(lrcPath) ? $"配对 {Path.GetFileName(lrcPath)}" : "未配对 .lrc";

                if (string.IsNullOrEmpty(lyrics))
                {
                    noLyrics++;
                    PrintTagged("无", ConsoleColor.DarkGray, $"{relativePath}（{lrcNote}）");
                }
                else
                {
                    hasLyrics++;
                    PrintTagged("有", ConsoleColor.Green, $"{relativePath}（歌词 {lyrics.Length} 字符，{lrcNote}）");
                }
            }
            catch (Exception ex)
            {
                errors++;
                PrintTagged("错误", ConsoleColor.Red, $"{relativePath}（{ex.GetType().Name}：{ex.Message}）");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"完成：有歌词 {hasLyrics} | 无歌词 {noLyrics} | 读取失败 {errors}");
        return errors > 0 ? ExitProcessingFailure : ExitOk;
    }

    private static FileOutcome Report(FileOutcome outcome)
    {
        var (label, color) = outcome.Status switch
        {
            ProcessStatus.Written => ("写入", ConsoleColor.Green),
            ProcessStatus.WouldWrite => ("预览", ConsoleColor.Cyan),
            ProcessStatus.UpToDate => ("一致", ConsoleColor.DarkGray),
            ProcessStatus.NoLrcFile => ("跳过", ConsoleColor.DarkGray),
            _ => ("失败", ConsoleColor.Red),
        };

        PrintTagged(label, color, $"{outcome.AudioPath} {outcome.Detail}");
        return outcome;
    }

    /// <summary>输出一行带状态标签的结果；标签固定两字宽，便于逐行对齐阅读。</summary>
    private static void PrintTagged(string label, ConsoleColor color, string message)
    {
        var previousColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"[{label}]");
        Console.ForegroundColor = previousColor;
        Console.WriteLine($" {message}");
    }
}
