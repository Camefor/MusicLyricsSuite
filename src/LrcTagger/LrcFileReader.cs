using System.Text;

namespace LrcTagger;

/// <summary>读取 .lrc 歌词文件，并自动识别文本编码。</summary>
public static class LrcFileReader
{
    /// <summary>
    /// 支持 UTF-8（含 BOM）、UTF-16（LE/BE）与 GB18030（兼容 GBK，中文歌词文件常见编码）。
    /// 返回歌词文本（已剥离 BOM）与所用编码名（用于报告输出）。
    /// </summary>
    public static (string Text, string EncodingName) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // 带 BOM 的文件按 BOM 解码：BOM 字符若留在歌词里，播放器中会显示为乱码
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16LE");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16BE");
        }

        // 无 BOM：先做严格 UTF-8 解码探测，遇到非法字节序列则回退 GB18030
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding("GB18030").GetString(bytes), "GB18030");
        }
    }
}
