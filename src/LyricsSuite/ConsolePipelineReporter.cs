using LrcTagger;
using LyricsFetcher.Models;

namespace LyricsSuite;

/// <summary>
/// 把编排器的事件翻译成人类可读的中文控制台输出。
/// 每个事件占一行并带颜色标签，便于逐行扫描结果。
/// </summary>
internal sealed class ConsolePipelineReporter : IPipelineReporter
{
    public void OnAlbumStart(string albumDirectory, int audioCount)
    {
        Console.WriteLine();
        Console.WriteLine($"扫描专辑：{albumDirectory}");
        Console.WriteLine($"发现音频：{audioCount} 首");
        Console.WriteLine();
    }

    public void OnEmptyAlbum(string albumDirectory)
    {
        Console.WriteLine();
        Console.WriteLine($"目录中未找到可处理的音频文件：{albumDirectory}");
    }

    public void OnTrackStart((int Index, int Total) position, TrackMetadata track)
    {
        Console.WriteLine($"[{position.Index}/{position.Total}] {track.Artist} - {track.Title} [{track.Album}]");
    }

    public void OnNotice((int Index, int Total) position, string notice)
    {
        PrintLine("提示", ConsoleColor.Yellow, notice);
    }

    public void OnFetchResult((int Index, int Total) position, FetchOutcome outcome)
    {
        switch (outcome.Status)
        {
            case FetchStatus.FoundSynced:
                PrintLine("歌词·同步", ConsoleColor.Green, outcome.OutputPath ?? "");
                break;
            case FetchStatus.FoundPlain:
                PrintLine("歌词·纯文本", ConsoleColor.Green, $"{outcome.OutputPath}（无时间轴）");
                break;
            case FetchStatus.NotFound:
                PrintLine("歌词·未找到", ConsoleColor.DarkGray, "LRCLIB 没有返回足够匹配的歌词");
                break;
            case FetchStatus.Empty:
                PrintLine("歌词·空", ConsoleColor.DarkGray, "匹配记录没有歌词文本");
                break;
            case FetchStatus.SkippedExists:
                PrintLine("歌词·跳过", ConsoleColor.DarkGray, outcome.Message);
                break;
            case FetchStatus.Error:
                PrintLine("歌词·失败", ConsoleColor.Red, outcome.Message);
                break;
        }
    }

    public void OnEmbedResult((int Index, int Total) position, EmbedOutcome outcome)
    {
        switch (outcome.Status)
        {
            case ProcessStatus.Written:
                PrintLine("嵌入·写入", ConsoleColor.Green, outcome.Detail);
                break;
            case ProcessStatus.UpToDate:
                PrintLine("嵌入·一致", ConsoleColor.DarkGray, outcome.Detail);
                break;
            case ProcessStatus.NoLrcFile:
                PrintLine("嵌入·跳过", ConsoleColor.DarkGray, outcome.Detail);
                break;
            case ProcessStatus.Failed:
                PrintLine("嵌入·失败", ConsoleColor.Red, outcome.Detail);
                break;
        }
    }

    public void OnMetadataFailure((int Index, int Total) position, string audioPath, Exception error)
    {
        PrintLine("读取标签失败", ConsoleColor.Red, $"{audioPath}（{error.GetType().Name}：{error.Message}）");
    }

    public void OnEmbedFailure((int Index, int Total) position, string audioPath, Exception error)
    {
        PrintLine("嵌入·异常", ConsoleColor.Red, $"{audioPath}（{error.GetType().Name}：{error.Message}）");
    }

    public void OnAlbumComplete(AlbumPipelineSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine("========== 完成 ==========");
        Console.WriteLine($"音频总数：{summary.AudioCount}");
        Console.WriteLine();
        Console.WriteLine("[歌词获取]");
        Console.WriteLine($"  同步歌词：{summary.FoundSynced}");
        Console.WriteLine($"  纯文本歌词：{summary.FoundPlain}");
        Console.WriteLine($"  未找到：{summary.NotFound}");
        Console.WriteLine($"  记录无文本：{summary.Empty}");
        Console.WriteLine($"  跳过已有 .lrc：{summary.SkippedExists}");
        Console.WriteLine($"  下载错误：{summary.FetchErrors}");
        Console.WriteLine();
        Console.WriteLine("[歌词嵌入]");
        Console.WriteLine($"  已写入：{summary.Written}");
        Console.WriteLine($"  已一致（无需改动）：{summary.UpToDate}");
        Console.WriteLine($"  无 .lrc 跳过：{summary.NoLrcFile}");
        Console.WriteLine($"  嵌入失败：{summary.EmbedFailures}");
        Console.WriteLine();
        Console.WriteLine($"读取标签失败：{summary.MetadataFailures}");

        // 失败清单：精确到"用户选的专辑根下的子路径 + 歌曲名"，方便手动补歌词
        var failures = summary.Failures.ToList();
        Console.WriteLine();
        if (failures.Count == 0)
        {
            PrintLine("全部成功", ConsoleColor.Green, "无需人工干预");
        }
        else
        {
            Console.WriteLine($"========== 需要手动获取的歌曲（{failures.Count}）==========");
            foreach (var f in failures)
            {
                PrintLine("待手动", ConsoleColor.Yellow,
                    $"{f.RelativePath} | {f.Artist} - {f.Title} | {f.DescribeFailure()}");
            }
        }
    }

    private static void PrintLine(string label, ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"    [{label}]");
        Console.ForegroundColor = previous;
        Console.WriteLine($" {message}");
    }
}
