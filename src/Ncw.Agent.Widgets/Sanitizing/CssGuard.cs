using System.Globalization;
using System.Text;

namespace Ncw.Agent.Widgets.Sanitizing;

/// <summary>
/// CSS 的静态检查(<c>style</c> 属性与 <c>&lt;style&gt;</c> 块共用)。判定在「还原了转义、去掉了注释」的文本上做——
/// <c>\75rl(</c>、<c>ur/**/l(</c>、<c>expr\65 ssion(</c> 都是浏览器会还原的写法。有任何一条不通过,整个属性 / 整个块被拿掉(不尝试局部修补:半截 CSS 比没有更难排查)。
/// 放行的只有「文档内引用」(<c>url(#grad)</c>)和图片 data URL;外链的 <c>url()</c>、<c>@import</c>、<c>image-set()</c>、IE 的 <c>expression()</c> / <c>behavior</c>、Firefox 的 <c>-moz-binding</c> 都不行。
/// </summary>
public static class CssGuard
{
    /// <summary>返回 null = 通过;否则是稳定的规则 id(<c>import</c> / <c>url</c> / <c>expression</c> / <c>behavior</c> / <c>script</c> / <c>image-set</c>)。</summary>
    public static string? Check(string css)
    {
        var text = Decode(css);
        if (text.Contains("@import", StringComparison.Ordinal))
        {
            return "import";
        }

        if (text.Contains("expression(", StringComparison.Ordinal) || text.Contains("expression\\(", StringComparison.Ordinal))
        {
            return "expression";
        }

        if (text.Contains("-moz-binding", StringComparison.Ordinal) || text.Contains("behavior:", StringComparison.Ordinal) || text.Contains("behaviour:", StringComparison.Ordinal))
        {
            return "behavior";
        }

        if (text.Contains("javascript:", StringComparison.Ordinal) || text.Contains("vbscript:", StringComparison.Ordinal) || text.Contains("livescript:", StringComparison.Ordinal))
        {
            return "script";
        }

        if (text.Contains("image-set(", StringComparison.Ordinal))
        {
            return "image-set";
        }

        var from = 0;
        while (text.IndexOf("url(", from, StringComparison.Ordinal) is var at and >= 0)
        {
            var i = at + 4;
            while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\r')
            {
                i++;
            }

            if (i < text.Length && text[i] is '"' or '\'')
            {
                var quote = text[i];
                var close = text.IndexOf(quote, i + 1);
                if (close < 0)
                {
                    return "url";
                }

                if (!AllowedUrl(text[(i + 1)..close]))
                {
                    return "url";
                }

                from = close + 1;
                continue;
            }

            var end = text.IndexOf(')', i);
            if (end < 0)
            {
                return "url";
            }

            if (!AllowedUrl(text[i..end]))
            {
                return "url";
            }

            from = end + 1;
        }

        return null;
    }

    private static bool AllowedUrl(string url) => WidgetUrls.Classify(url) is UrlKind.Fragment or UrlKind.DataImage;

    /// <summary>
    /// 输出用的形式:<c>&lt;</c> 换成 CSS 转义 <c>\3c </c>。CSS 里不会有合法的裸 <c>&lt;</c>(只可能在字符串或属性选择器里,转义后语义不变),
    /// 而去掉它就保证了块内没有任何能被 HTML 分词器当作标签的东西——无论浏览器把 <c>&lt;style&gt;</c> 当 RAWTEXT(HTML)还是普通元素(SVG)解析,结果相同。
    /// </summary>
    public static string ForOutput(string css) => css.Contains('<', StringComparison.Ordinal) ? css.Replace("<", "\\3c ", StringComparison.Ordinal) : css;

    /// <summary>还原 CSS 转义、去掉注释、转小写(只用于判定,不用于输出)。</summary>
    private static string Decode(string css)
    {
        var sb = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                continue;
            }

            if (c == '\\' && i + 1 < css.Length)
            {
                var j = i + 1;
                var hexStart = j;
                while (j < css.Length && j - hexStart < 6 && Uri.IsHexDigit(css[j]))
                {
                    j++;
                }

                if (j > hexStart)
                {
                    var code = int.Parse(css.AsSpan(hexStart, j - hexStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    if (j < css.Length && css[j] is ' ' or '\t' or '\n' or '\r')
                    {
                        j++;
                    }

                    sb.Append(code is > 0 and <= 0x10FFFF && !(code is >= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�");
                    i = j;
                    continue;
                }

                sb.Append(css[i + 1]);
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString().ToLowerInvariant();
    }
}
