namespace LrcTagger;

/// <summary>命令行选项。</summary>
internal sealed class CommandLineOptions
{
    /// <summary>目标路径（目录或单个音频文件，绝对路径）。</summary>
    public required string TargetPath { get; init; }

    /// <summary>目标路径是否为目录；为 false 时表示单个音频文件。</summary>
    public bool TargetIsDirectory { get; init; }

    /// <summary>预览模式：只显示将执行的动作，不修改文件。</summary>
    public bool DryRun { get; init; }

    /// <summary>只列出各音频文件当前的内嵌歌词状态，不修改文件。</summary>
    public bool ListOnly { get; init; }

    /// <summary>提取全部标签信息并以 JSON 输出，不修改文件。</summary>
    public bool DumpTags { get; init; }

    /// <summary>写入前把原文件备份为 "&lt;原文件名&gt;.bak"。</summary>
    public bool Backup { get; init; }

    /// <summary>
    /// 解析命令行参数。解析失败时返回 null，并通过 <paramref name="error"/> 给出原因。
    /// 目录支持 -d/--dir/--dir= 写法，也可直接作为位置参数传入。
    /// </summary>
    public static CommandLineOptions? TryParse(string[] args, out string? error)
    {
        error = null;
        string? target = null;
        var dryRun = false;
        var listOnly = false;
        var dumpTags = false;
        var backup = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-d" or "--dir":
                    if (i + 1 >= args.Length)
                    {
                        error = $"{arg} 缺少路径参数";
                        return null;
                    }
                    target = args[++i];
                    break;

                case "-n" or "--dry-run":
                    dryRun = true;
                    break;

                case "-l" or "--list":
                    listOnly = true;
                    break;

                case "-t" or "--tags":
                    dumpTags = true;
                    break;

                case "-b" or "--backup":
                    backup = true;
                    break;

                default:
                    if (arg.StartsWith("--dir=", StringComparison.Ordinal))
                    {
                        target = arg["--dir=".Length..];
                    }
                    else if (!arg.StartsWith('-'))
                    {
                        // 位置式写法：lrc-tagger <目录或音频文件>
                        if (target is not null)
                        {
                            error = "目录或文件参数只能指定一次";
                            return null;
                        }
                        target = arg;
                    }
                    else
                    {
                        error = $"未知参数：{arg}";
                        return null;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            error = "缺少目标目录或音频文件（-d <路径>）";
            return null;
        }

        if (dryRun && listOnly)
        {
            error = "--dry-run 与 --list 不能同时使用";
            return null;
        }

        if (dumpTags && (dryRun || listOnly || backup))
        {
            error = "--tags 不能与 --dry-run、--list 或 --backup 同时使用";
            return null;
        }

        var fullPath = Path.GetFullPath(target);
        var isDirectory = Directory.Exists(fullPath);

        if (!isDirectory)
        {
            if (!File.Exists(fullPath))
            {
                error = $"路径不存在：{target}";
                return null;
            }

            if (!AudioFileCatalog.IsSupported(fullPath))
            {
                error = $"不支持的音频文件格式：{Path.GetFileName(fullPath)}";
                return null;
            }
        }

        return new CommandLineOptions
        {
            TargetPath = fullPath,
            TargetIsDirectory = isDirectory,
            DryRun = dryRun,
            ListOnly = listOnly,
            DumpTags = dumpTags,
            Backup = backup,
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            lrc-tagger —— 按同名规则把 .lrc 歌词写入音频文件的内嵌歌词标签，或导出标签信息

            用法：
              lrc-tagger -d <目录或音频文件> [选项]
              lrc-tagger <目录或音频文件> [选项]

            选项：
              -d, --dir <路径>    目标目录（递归扫描）或单个音频文件（必填）
              -n, --dry-run       预览模式：显示配对结果与将执行的动作，不修改文件
              -l, --list          列出各音频文件当前的内嵌歌词状态，不修改文件
              -t, --tags          提取全部标签信息并以 JSON 输出，不修改文件
              -b, --backup        写入前把原文件备份为 <原文件名>.bak
              -h, --help          显示本帮助

            配对规则：
              音频文件与其同级目录下的同名 .lrc 配对，例如：
                05 - 李志 - 天空之城.flac  ←  05 - 李志 - 天空之城.lrc
              歌词编码自动识别：UTF-8（含 BOM）/ UTF-16（LE/BE）/ GB18030（兼容 GBK）

            写入行为（对齐 Lyrico）：
              歌词写入标准 LYRICS 字段：MP3→USLT 帧、M4A→©lyr、FLAC/OGG→LYRICS 注释；
              同时清理 UNSYNCED LYRICS、LYRIC、LYRICSENG 等歌词别名，写入后自动重读校验。

            标签导出（--tags）：
              输出 JSON，包含通用标签字段、音频属性、内嵌图片元信息，
              以及 ID3v2 帧 / Vorbis 注释 / APE 项 / MP4 atom 等原生字段；
              读取失败的文件会在结果中携带 error 字段，不影响其余文件。

            支持格式：
              mp3 / flac / m4a / m4b / mp4 / aac / ogg / oga / wav / ape / wma / aif / aiff
            """);
    }
}
