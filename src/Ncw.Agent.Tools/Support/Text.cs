using System.Text.RegularExpressions;

namespace Ncw.Agent.Tools;

/// <summary>文本小工具。发给模型的文字一律英文常量(本层不产出界面文字)。</summary>
internal static partial class Text
{
    /// <summary>
    /// 整条 ANSI 转义序列(OSC、CSI、nF、两字符转义、落单的 ESC),保留可见正文(移植自 2.x <c>stripAnsi</c>)。
    /// 很多 CLI 在非 TTY 下照样着色;这些序列进模型上下文是纯噪声,而 ESC 本身不可见,看上去只是「多了一堆方括号乱码」。
    /// </summary>
    [GeneratedRegex(@"\u001B(?:\][\s\S]*?(?:\u0007|\u001B\\|$)|\[[0-?]*[ -/]*[@-~]|[ -/]+[0-~]|[@-Z\\-_])?")]
    private static partial Regex Ansi();

    public static string StripAnsi(string text) => text.Contains('\u001B') ? Ansi().Replace(text, "") : text;

    /// <summary>人类可读的字节数。工具输出里出现绝对字节数只会占 token。</summary>
    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };

    /// <summary>二进制探测:前若干字节里出现 NUL 就算二进制(和 git 同一条判据)。必须在解码之前做,否则一个 300MB 的 pack 会先被解成一堆 U+FFFD。</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes) => bytes.Contains((byte)0);

    /// <summary>保留开头 <paramref name="max"/> 个字符,不切开代理对。</summary>
    public static string Head(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep];
    }

    /// <summary>保留结尾 <paramref name="max"/> 个字符,不切开代理对。</summary>
    public static string Tail(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var start = text.Length - max;
        if (start < text.Length && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>截到 <paramref name="max"/> 个字符并留下明确的标记(静默截断更糟:读起来仍然通顺,你不会想到正文根本没进去)。</summary>
    public static string ClipWithEllipsis(string text, int max, string marker = "...") =>
        text.Length <= max ? text : Head(text, Math.Max(0, max - marker.Length)) + marker;

    /// <summary>把 <c>\r\n</c> 归一成 <c>\n</c>(孤立的 CR 保留)。</summary>
    public static string ToLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
