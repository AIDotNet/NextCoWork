using System.Globalization;
using System.Text;

namespace Ncw.Agent.Widgets.Sanitizing;

public enum UrlKind
{
    /// <summary>空串。</summary>
    Empty,

    /// <summary><c>#id</c>:文档内的片段引用。</summary>
    Fragment,

    Http,

    Mailto,

    /// <summary><c>data:image/png|jpeg|gif|webp|avif|bmp|svg+xml</c>(图片 data URL:作为 <c>img</c> 的来源不会执行脚本)。</summary>
    DataImage,

    /// <summary><c>javascript:</c> / <c>vbscript:</c> / <c>data:</c>(非图片)/ <c>blob:</c> 等会执行或承载代码的方案。</summary>
    Script,

    /// <summary>相对地址、其它协议:在沙箱里没有意义,当作不安全处理。</summary>
    Other,
}

/// <summary>
/// URL 的分类与 HTML 实体解码。属性值在判定之前<b>先解码</b>(<c>jav&amp;#x61;script:</c>、<c>&amp;Tab;</c>、不带分号的数字引用都是浏览器会还原的写法),
/// 再去掉所有控制字符和空白(浏览器会忽略 URL 里的制表 / 换行,<c>java\tscript:</c> 照样是 <c>javascript:</c>),最后小写比较。
/// </summary>
public static class WidgetUrls
{
    /// <summary>
    /// 允许 <c>&lt;script src&gt;</c> / <c>&lt;link rel=stylesheet&gt;</c> 取回资源的 CDN(精确主机名)。与渲染宿主的 CSP 同一张表(2.x <c>widget-protocol.ts</c> 的 <c>CDN_SOURCES</c>);
    /// 设计规范里 Chart.js / D3 的例子引用的就是这几个域名。加域名前先想清楚:这是「模型写的代码能从哪里取回脚本」的通道。
    /// </summary>
    public static IReadOnlyList<string> CdnHosts { get; } = ["cdn.jsdelivr.net", "unpkg.com", "cdnjs.cloudflare.com", "esm.sh"];

    /// <summary>解码 HTML 实体(数字引用不带分号也解;具名的只认安全判定需要的那几个)。未知的原样保留。</summary>
    public static string DecodeEntities(string value)
    {
        if (value.IndexOf('&') < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c != '&')
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '#')
            {
                var j = i + 2;
                var hex = j < value.Length && (value[j] is 'x' or 'X');
                if (hex)
                {
                    j++;
                }

                var digitsStart = j;
                while (j < value.Length && (hex ? Uri.IsHexDigit(value[j]) : char.IsAsciiDigit(value[j])))
                {
                    j++;
                }

                if (j > digitsStart && j - digitsStart <= 8
                    && long.TryParse(value.AsSpan(digitsStart, j - digitsStart), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                {
                    if (j < value.Length && value[j] == ';')
                    {
                        j++;
                    }

                    sb.Append(code is > 0 and <= 0x10FFFF && !(code is >= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32((int)code) : "�");
                    i = j;
                    continue;
                }

                sb.Append(c);
                i++;
                continue;
            }

            var end = i + 1;
            while (end < value.Length && char.IsAsciiLetterOrDigit(value[end]) && end - i <= 12)
            {
                end++;
            }

            var name = value.Substring(i + 1, end - i - 1);
            var hasSemicolon = end < value.Length && value[end] == ';';
            string? replacement = name switch
            {
                "amp" or "AMP" => "&",
                "lt" or "LT" => "<",
                "gt" or "GT" => ">",
                "quot" or "QUOT" => "\"",
                "apos" when hasSemicolon => "'",
                "colon" when hasSemicolon => ":",
                "Tab" when hasSemicolon => "\t",
                "NewLine" when hasSemicolon => "\n",
                "nbsp" or "NBSP" => " ",
                _ => null,
            };
            if (replacement is not null)
            {
                sb.Append(replacement);
                i = hasSemicolon ? end + 1 : end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>把解码后的 URL 压成判定用的形式:去掉全部 ASCII 控制字符与空白,小写。</summary>
    public static string Normalize(string decoded)
    {
        var sb = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            if (c <= 0x20 || (c >= 0x7F && c <= 0x9F) || c is ' ' or '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>属性值(原样,未解码)是不是以脚本类方案开头。所有属性——不只是 <c>href</c>——都过这一道。</summary>
    public static bool IsScriptScheme(string rawAttributeValue) => Classify(rawAttributeValue) == UrlKind.Script;

    public static UrlKind Classify(string raw)
    {
        var n = Normalize(DecodeEntities(raw));
        if (n.Length == 0)
        {
            return UrlKind.Empty;
        }

        if (n[0] == '#')
        {
            return UrlKind.Fragment;
        }

        if (n.StartsWith("javascript:", StringComparison.Ordinal) || n.StartsWith("vbscript:", StringComparison.Ordinal) || n.StartsWith("livescript:", StringComparison.Ordinal)
            || n.StartsWith("blob:", StringComparison.Ordinal) || n.StartsWith("filesystem:", StringComparison.Ordinal) || n.StartsWith("file:", StringComparison.Ordinal))
        {
            return UrlKind.Script;
        }

        if (n.StartsWith("data:", StringComparison.Ordinal))
        {
            return IsDataImage(n) ? UrlKind.DataImage : UrlKind.Script;
        }

        if (n.StartsWith("http://", StringComparison.Ordinal) || n.StartsWith("https://", StringComparison.Ordinal) || n.StartsWith("//", StringComparison.Ordinal))
        {
            return UrlKind.Http;
        }

        if (n.StartsWith("mailto:", StringComparison.Ordinal) || n.StartsWith("tel:", StringComparison.Ordinal))
        {
            return UrlKind.Mailto;
        }

        return UrlKind.Other;
    }

    private static bool IsDataImage(string normalized)
    {
        foreach (var type in new[] { "png", "jpeg", "jpg", "gif", "webp", "avif", "bmp", "svg+xml" })
        {
            var prefix = "data:image/" + type;
            if (normalized.StartsWith(prefix, StringComparison.Ordinal) && normalized.Length > prefix.Length && normalized[prefix.Length] is ';' or ',')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>是不是 CDN 白名单里的 https 地址(精确主机名、无用户信息、默认端口)。</summary>
    public static bool IsCdnUrl(string raw)
    {
        var decoded = DecodeEntities(raw).Trim();
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.IsDefaultPort
            && CdnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
