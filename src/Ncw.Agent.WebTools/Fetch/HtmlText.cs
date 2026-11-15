using System.Globalization;
using System.Text;

namespace Ncw.Agent.WebTools.Fetch;

/// <summary>
/// HTML → 纯文本,以及「这个 content-type 能不能当文本解码」(2.x <c>shared/text/html-text.ts</c> 的移植)。
/// <para>
/// 故意不是 Markdown 转换器,也不是 HTML 解析器:只要「把标签去掉、把块级元素之间的换行留住」。表格和嵌套列表的结构会丢(描述里已对模型说明)。
/// </para>
/// 和 2.x 的差异:2.x 是几条链式正则(其中 <c>&lt;script&gt;…&lt;/script&gt;</c> 用了反向引用 + 惰性匹配,遇到几十万个没闭合的 <c>&lt;script&gt;</c> 是平方级);
/// 3.0 是单趟线性扫描——「没有闭合标签」的结论记下来,之后同名的开标签不再去找(所以一个恶意页面撑不起平方级的时间)。
/// <c>a &lt; b</c> 这种 <c>&lt;</c> 后面不是字母 / <c>/</c> / <c>!</c> / <c>?</c> 的,当文本保留(2.x 会把 <c>&lt; b … &gt;</c> 当标签吃掉)。
/// </summary>
public static class HtmlText
{
    private static readonly string[] SkippedElements = ["script", "style", "noscript", "svg", "template"];

    private static readonly string[] BlockClosers = ["p", "div", "section", "article", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre"];

    /// <summary>
    /// 允许解码成文本的 content-type。白名单而不是黑名单:黑名单漏一个类型的后果是把二进制倒进上下文;白名单漏一个类型的后果是模型收到一条说得清楚的拒绝。
    /// </summary>
    public static bool IsTextual(string? contentType)
    {
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type.StartsWith("text/", StringComparison.Ordinal) || type.EndsWith("+json", StringComparison.Ordinal) || type.EndsWith("+xml", StringComparison.Ordinal))
        {
            return true;
        }

        return type is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript" or "application/ld+json"
            or "application/rss+xml" or "application/atom+xml" or "application/x-yaml" or "application/yaml";
    }

    public static bool LooksLikeHtml(string text)
    {
        var span = text.AsSpan().TrimStart();
        return span.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || span.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToText(string html)
    {
        var sb = new StringBuilder(html.Length / 2);
        var noClose = new HashSet<string>(StringComparer.Ordinal);
        var noGreaterThan = false;
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, lt - i);
            if (lt + 1 >= html.Length || !IsTagStart(html[lt + 1]))
            {
                sb.Append('<'); // 「a < b」是文本
                i = lt + 1;
                continue;
            }

            // 注释:整段扔掉;没有结尾就到文末(浏览器也是这么处理的)
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                sb.Append(' ');
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (noGreaterThan)
            {
                sb.Append('<');
                i = lt + 1;
                continue;
            }

            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                noGreaterThan = true; // 之后不会再有 '>' 了,不必每个 '<' 都扫一遍
                sb.Append('<');
                i = lt + 1;
                continue;
            }

            var closing = html[lt + 1] == '/';
            var nameStart = lt + (closing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < gt && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':'))
            {
                nameEnd++;
            }

            var name = html.AsSpan(nameStart, nameEnd - nameStart).ToString().ToLowerInvariant();
            var selfClosing = gt > lt + 1 && html[gt - 1] == '/';

            if (!closing && !selfClosing && Array.IndexOf(SkippedElements, name) >= 0 && !noClose.Contains(name))
            {
                // script / style / noscript / svg / template 的内容对阅读毫无价值,而且能占掉整页的体积
                var close = IndexOfClosing(html, name, gt + 1);
                if (close >= 0)
                {
                    var closeGt = html.IndexOf('>', close);
                    sb.Append(' ');
                    i = closeGt < 0 ? html.Length : closeGt + 1;
                    continue;
                }

                noClose.Add(name);
            }

            if (name == "br" && !closing)
            {
                sb.Append('\n');
            }
            else if (closing && Array.IndexOf(BlockClosers, name) >= 0)
            {
                sb.Append('\n');
            }
            else if (!closing && name == "li")
            {
                // 上一个 </li> 已经换过行了:不再空一行(2.x 在相邻的列表项之间会多出一个空行)
                sb.Append(sb.Length > 0 && sb[^1] == '\n' ? "- " : "\n- ");
            }
            else
            {
                sb.Append(' ');
            }

            i = gt + 1;
        }

        return Normalize(DecodeEntities(sb.ToString()));
    }

    /// <summary>HTML 片段 → 一行纯文本(标题、摘要这种本来就该是一行的东西)。</summary>
    public static string FragmentToText(string html)
    {
        var text = ToText(html);
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsTagStart(char c) => char.IsAsciiLetter(c) || c is '/' or '!' or '?';

    private static int IndexOfClosing(string html, string name, int from)
    {
        var needle = "</" + name;
        var at = from;
        while (at < html.Length)
        {
            var found = html.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return -1;
            }

            var after = found + needle.Length;
            if (after >= html.Length || !char.IsAsciiLetterOrDigit(html[after]))
            {
                return found;
            }

            at = after;
        }

        return -1;
    }

    // ───────────────────────── 实体 ─────────────────────────

    private static string DecodeEntities(string text)
    {
        if (text.IndexOf('&') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var amp = text.IndexOf('&', i);
            if (amp < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, amp - i);
            var semi = text.IndexOf(';', amp + 1, Math.Min(32, text.Length - amp - 1));
            if (semi < 0)
            {
                sb.Append('&');
                i = amp + 1;
                continue;
            }

            var body = text.AsSpan(amp + 1, semi - amp - 1);
            if (TryDecode(body, out var decoded))
            {
                sb.Append(decoded);
            }
            else
            {
                sb.Append(text, amp, semi - amp + 1); // 不认识的实体原样保留
            }

            i = semi + 1;
        }

        return sb.ToString();
    }

    private static bool TryDecode(ReadOnlySpan<char> body, out string decoded)
    {
        decoded = "";
        if (body.Length == 0)
        {
            return false;
        }

        if (body[0] == '#')
        {
            var hex = body.Length > 1 && body[1] is 'x' or 'X';
            var digits = body[(hex ? 2 : 1)..];
            if (digits.Length == 0 || !int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                return false;
            }

            // 0、代理区、超出 Unicode 范围:不解成怪字符,原样留着(2.x 的 fromCodePoint 会在这里抛)
            if (code is <= 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
            {
                return false;
            }

            decoded = char.ConvertFromUtf32(code);
            return true;
        }

        var named = body.ToString().ToLowerInvariant() switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            "copy" => "©",
            "reg" => "®",
            "trade" => "™",
            "hellip" => "…",
            "mdash" => "—",
            "ndash" => "–",
            "lsquo" => "‘",
            "rsquo" => "’",
            "ldquo" => "“",
            "rdquo" => "”",
            "laquo" => "«",
            "raquo" => "»",
            "middot" => "·",
            "bull" => "•",
            "times" => "×",
            "deg" => "°",
            "euro" => "€",
            "pound" => "£",
            "yen" => "¥",
            "cent" => "¢",
            _ => null,
        };
        if (named is null)
        {
            return false;
        }

        decoded = named;
        return true;
    }

    // ───────────────────────── 空白 ─────────────────────────

    /// <summary>行内空白压成一个空格但保留换行(换行是这里仅剩的结构);三个以上的连续换行压成两个;每行 trim。</summary>
    private static string Normalize(string text)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = CollapseSpaces(raw).Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
                if (blank > 0)
                {
                    sb.Append('\n'); // 最多保留一个空行
                }
            }

            blank = 0;
            sb.Append(line);
        }

        return sb.ToString();
    }

    private static string CollapseSpaces(string line)
    {
        var sb = new StringBuilder(line.Length);
        var space = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        if (space)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    // ───────────────────────── 不可信文本清洗 ─────────────────────────

    /// <summary>
    /// 抓回来的内容是不可信输入:去掉 C0 / C1 控制字符(保留 <c>\n</c>、<c>\t</c>)、DEL、零宽 / 双向覆盖字符(用来把指令藏进「看起来无害」的文字里),把孤立的代理半边换成 U+FFFD。
    /// </summary>
    public static string StripControlChars(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\t')
            {
                sb.Append(c);
            }
            else if (c < 0x20 || c is >= '\u007f' and <= '\u009f' || c is >= '​' and <= '‏' || c is >= '‪' and <= '‮' || c is >= '⁠' and <= '⁤' || c is >= '⁦' and <= '⁩' || c == '﻿')
            {
                // 丢弃
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
