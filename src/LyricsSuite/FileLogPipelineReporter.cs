using System.Text;
using LrcTagger;
using LyricsFetcher.Models;

namespace LyricsSuite;

/// <summary>
/// 把编排器的事件写入日志文件，每行一条时间戳记录，便于事后排查。
/// 日志文件在构造时打开（AutoFlush = true），即便程序被 Ctrl+C 打断也能保留已处理的部分；
/// <see cref="OnAlbumComplete"/> 会追加"总览 + 需手动处理的失败清单"段落。
/// </summary>
internal sealed class FileLogPipelineReporter : IPipelineReporter, IDisposable
{
    private readonly StreamWriter _writer;

    public FileLogPipelineReporter(string logPath)
    {
        LogPath = logPath;
        _writer = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true,
            NewLine = Environment.NewLine,
        };
        WriteLine($"==== 开始：{DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
    }

    public string LogPath { get; }

    public void OnAlbumStart(string albumDirectory, int audioCount)
    {
        WriteLine($"专辑目录：{albumDirectory}");
        WriteLine($"音频数：{audioCount}");
        WriteLine(string.Empty);
    }

    public void OnEmptyAlbum(string albumDirectory)
    {
        WriteLine($"[空目录] {albumDirectory}");
    }

    public void OnTrackStart((int Index, int Total) position, TrackMetadata track)
    {
        WriteEvent("开始",
            $"[{position.Index}/{position.Total}] {track.Artist} - {track.Title} [{track.Album}] | {track.AudioPath}");
    }

    public void OnNotice((int Index, int Total) position, string notice)
    {
        WriteEvent("提示", $"[{position.Index}/{position.Total}] {notice}");
    }

    public void OnFetchResult((int Index, int Total) position, FetchOutcome outcome)
    {
        WriteEvent($"歌词·{outcome.Status}",
            $"[{position.Index}/{position.Total}] {outcome.Message} | {outcome.AudioPath}");
    }

    public void OnEmbedResult((int Index, int Total) position, EmbedOutcome outcome)
    {
        WriteEvent($"嵌入·{outcome.Status}",
            $"[{position.Index}/{position.Total}] {outcome.Detail} | {outcome.AudioPath}");
    }

    public void OnMetadataFailure((int Index, int Total) position, string audioPath, Exception error)
    {
        WriteEvent("读取标签失败",
            $"[{position.Index}/{position.Total}] {audioPath} | {error.GetType().Name}: {error.Message}");
    }

    public void OnEmbedFailure((int Index, int Total) position, string audioPath, Exception error)
    {
        WriteEvent("嵌入·异常",
            $"[{position.Index}/{position.Total}] {audioPath} | {error.GetType().Name}: {error.Message}");
    }

    public void OnAlbumComplete(AlbumPipelineSummary summary)
    {
        WriteLine(string.Empty);
        WriteLine("========== 总览 ==========");
        WriteLine($"音频总数：{summary.AudioCount}");
        WriteLine("[歌词获取]");
        WriteLine($"  同步歌词：{summary.FoundSynced}");
        WriteLine($"  纯文本歌词：{summary.FoundPlain}");
        WriteLine($"  未找到：{summary.NotFound}");
        WriteLine($"  记录无文本：{summary.Empty}");
        WriteLine($"  跳过已有 .lrc：{summary.SkippedExists}");
        WriteLine($"  下载错误：{summary.FetchErrors}");
        WriteLine("[歌词嵌入]");
        WriteLine($"  已写入：{summary.Written}");
        WriteLine($"  已一致（无需改动）：{summary.UpToDate}");
        WriteLine($"  无 .lrc 跳过：{summary.NoLrcFile}");
        WriteLine($"  嵌入失败：{summary.EmbedFailures}");
        WriteLine($"读取标签失败：{summary.MetadataFailures}");

        WriteLine(string.Empty);
        var successes = summary.Successes.ToList();
        WriteLine($"========== 成功清单（{successes.Count}）==========");
        foreach (var s in successes)
        {
            WriteLine($"  - {s.RelativePath} | {s.Artist} - {s.Title} | {DescribeSuccess(s)}");
        }

        WriteLine(string.Empty);
        var failures = summary.Failures.ToList();
        if (failures.Count == 0)
        {
            WriteLine("========== 失败清单 ==========");
            WriteLine("  无需人工干预，全部成功。");
        }
        else
        {
            WriteLine($"========== 需要手动获取的歌曲（{failures.Count}）==========");
            foreach (var f in failures)
            {
                WriteLine($"  - {f.RelativePath} | {f.Artist} - {f.Title} | {f.DescribeFailure()}");
            }
        }

        WriteLine(string.Empty);
        WriteLine($"==== 结束：{DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
    }

    private static string DescribeSuccess(TrackRecord r)
    {
        var parts = new List<string>();
        if (r.FetchStatus is FetchStatus.FoundSynced) parts.Add("同步歌词");
        else if (r.FetchStatus is FetchStatus.FoundPlain) parts.Add("纯文本歌词");
        else if (r.FetchStatus is FetchStatus.SkippedExists) parts.Add("复用已有 .lrc");

        if (r.EmbedStatus is ProcessStatus.Written) parts.Add("已嵌入");
        else if (r.EmbedStatus is ProcessStatus.UpToDate) parts.Add("标签已一致");
        else if (r.EmbedStatus is ProcessStatus.NoLrcFile) parts.Add("未嵌入（无 .lrc）");

        return parts.Count == 0 ? "成功" : string.Join(" + ", parts);
    }

    private void WriteLine(string text) => _writer.WriteLine(text);

    private void WriteEvent(string label, string message) =>
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss} [{label}] {message}");

    public void Dispose() => _writer.Dispose();
}
