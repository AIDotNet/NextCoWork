namespace Ncw.Agent.Environments;

public enum NewlineStyle
{
    Lf,
    CrLf,
}

/// <summary>
/// 跨平台的换行和 BOM 处理。Windows 上的文件常带 CRLF 和 BOM,Edit 一类的工具改完要原样保留
/// (2.x 的 Edit 工具要求「保留 CRLF 和 BOM」),不能因为在 macOS 上执行就把整个文件的换行悄悄改掉。
/// </summary>
public static class TextNewlines
{
    /// <summary>
    /// 判断文本的主导换行;没有任何换行返回 <c>null</c>(调用方用环境的 <see cref="EnvironmentFacts.NewLine"/>)。
    /// 混用时取出现次数多的那一种,平手取 LF。
    /// </summary>
    public static NewlineStyle? Detect(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0)
        {
            return null;
        }

        return crlf > lf ? NewlineStyle.CrLf : NewlineStyle.Lf;
    }

    /// <summary>把所有 CRLF 归一成 LF(孤立的 CR 保留)。</summary>
    public static string ToLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>把(已归一成 LF 的)文本写成目标换行。</summary>
    public static string Apply(string lfText, NewlineStyle style) =>
        style == NewlineStyle.CrLf ? lfText.Replace("\n", "\r\n", StringComparison.Ordinal) : lfText;

    public static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    public static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> bytes) => HasUtf8Bom(bytes) ? bytes[3..] : bytes;
}
