using System.Text;

namespace Ncw.Client.Widgets;

public enum WidgetNavigation
{
    /// <summary>放行(只有外壳自己的那一次加载、以及同一页内的锚点跳转)。</summary>
    Allow,

    /// <summary>取消,什么也不做。</summary>
    Cancel,

    /// <summary>取消,并把地址交给「在系统浏览器里打开」的确认流程(<see cref="WidgetBridge.OpenExternalAsync"/>)。</summary>
    OpenExternal,
}

/// <summary>
/// WebView 导航与弹窗的决策(纯逻辑,每个 WebView 一个实例)。<b>widget 不许导航</b>:链接点击、<c>location.href</c>、<c>window.open</c>、<c>target=_blank</c>、表单提交,一律取消;
/// http / https 的目标改走「系统浏览器 + 确认对话」,别的方案(<c>javascript:</c> / <c>file:</c> / <c>data:</c> / <c>mailto:</c>……)直接丢弃。
/// <para>放行的只有两种:<b>外壳自己的加载</b>(<see cref="ExpectShellLoad"/> 到页面发出「就绪」消息之间的导航:这时 widget 的代码还没有被推进来,
/// 所以 widget 没有办法借这个窗口导航;各平台的 <c>NavigateToString</c> 会用什么样的内部地址不一,所以不在这里写死);就绪之后,只放行和外壳同一页的锚点跳转(<c>#id</c>)。</para>
/// </summary>
public sealed class WidgetNavigationPolicy
{
    private bool _loading;
    private Uri? _shell;

    /// <summary>即将加载外壳页面(<c>NavigateToString</c> 之前调)。</summary>
    public void ExpectShellLoad()
    {
        _loading = true;
        _shell = null;
    }

    /// <summary>页面发出「就绪」:关上放行窗口。</summary>
    public void MarkReady() => _loading = false;

    public WidgetNavigation Decide(Uri? target)
    {
        if (_loading)
        {
            _shell ??= target;
            return WidgetNavigation.Allow;
        }

        if (target is null)
        {
            return WidgetNavigation.Cancel;
        }

        if (_shell is not null && SamePage(_shell, target))
        {
            return WidgetNavigation.Allow;
        }

        // 外壳自己的内部来源(某些平台上 NavigateToString 用一个 localhost 地址):不是用户该去的外链,别交给浏览器
        if (_shell is { IsAbsoluteUri: true } shell && target.IsAbsoluteUri && string.Equals(shell.Authority, target.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetNavigation.Cancel;
        }

        return WidgetInboundParser.TryExternalUrl(target.OriginalString, out _) ? WidgetNavigation.OpenExternal : WidgetNavigation.Cancel;
    }

    /// <summary>新窗口(<c>window.open</c> / <c>target=_blank</c>)永远不开;http(s) 目标走外部打开。</summary>
    public WidgetNavigation DecideNewWindow(Uri? target) =>
        target is not null && WidgetInboundParser.TryExternalUrl(target.OriginalString, out _) ? WidgetNavigation.OpenExternal : WidgetNavigation.Cancel;

    /// <summary>除了片段(<c>#…</c>)以外完全相同:同一页内的锚点跳转。</summary>
    private static bool SamePage(Uri a, Uri b) =>
        string.Equals(Strip(a), Strip(b), StringComparison.Ordinal);

    private static string Strip(Uri uri)
    {
        var text = uri.OriginalString;
        var hash = text.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? text : text[..hash];
    }
}

/// <summary>
/// 手机端 / WebView 不可用时的降级:从静态快照(无脚本、无外链资源的 HTML / SVG,Agent 产出)里抽出可读的文字,画成只读视图。
/// 手写的小分词器(不引入 HTML 解析库):跳过 <c>style</c> / <c>script</c> / <c>defs</c> 的内容,块级标签换行,SVG 的 <c>text</c> 标签内容按顺序拼起来,解几个常见实体。
/// 抽出来的只是给人看的文字,不会再被当成标记渲染——所以这条降级路径上没有任何可执行的东西。
/// </summary>
public static class WidgetSnapshotText
{
    public const int MaxChars = 2000;

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "style", "script", "defs", "title", "desc", "metadata" };

    private static readonly HashSet<string> Breaks = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "p", "div", "li", "ul", "ol", "tr", "table", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer", "pre", "blockquote", "text", "figcaption", "caption",
    };

    public static string Extract(string? html, int maxChars = MaxChars)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        var text = new StringBuilder();
        var i = 0;
        while (i < html.Length && text.Length <= maxChars * 2)
        {
            if (html[i] != '<')
            {
                var next = html.IndexOf('<', i);
                var end = next < 0 ? html.Length : next;
                text.Append(Decode(html.AsSpan(i, end - i)));
                i = end;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }

            var gt = FindTagEnd(html, i);
            if (gt < 0)
            {
                break; // 没有收尾的 `<`:后面的丢掉,不当文字
            }

            var name = TagName(html.AsSpan(i + 1, gt - i - 1), out var closing);
            i = gt + 1;
            if (!closing && Skipped.Contains(name))
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                var closeEnd = close < 0 ? -1 : html.IndexOf('>', close);
                i = closeEnd < 0 ? html.Length : closeEnd + 1;
                continue;
            }

            if (Breaks.Contains(name))
            {
                text.Append('\n');
            }
            else
            {
                text.Append(' ');
            }
        }

        return Tidy(text.ToString(), maxChars);
    }

    private static int FindTagEnd(string html, int start)
    {
        char quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static string TagName(ReadOnlySpan<char> tag, out bool closing)
    {
        closing = tag.Length > 0 && tag[0] == '/';
        if (closing)
        {
            tag = tag[1..];
        }

        var end = 0;
        while (end < tag.Length && !char.IsWhiteSpace(tag[end]) && tag[end] is not ('/' or '>'))
        {
            end++;
        }

        var name = tag[..end].ToString();
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    private static string Decode(ReadOnlySpan<char> span)
    {
        if (!span.Contains('&'))
        {
            return span.ToString();
        }

        var sb = new StringBuilder(span.Length);
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != '&')
            {
                sb.Append(span[i]);
                continue;
            }

            var semi = span[i..].IndexOf(';');
            if (semi is < 2 or > 10)
            {
                sb.Append('&');
                continue;
            }

            var entity = span.Slice(i + 1, semi - 1).ToString();
            var decoded = entity switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" or "#39" => "'",
                "nbsp" => " ",
                _ => NumericEntity(entity),
            };
            if (decoded is null)
            {
                sb.Append('&');
                continue;
            }

            sb.Append(decoded);
            i += semi;
        }

        return sb.ToString();
    }

    private static string? NumericEntity(string entity)
    {
        if (entity.Length < 2 || entity[0] != '#')
        {
            return null;
        }

        var ok = entity[1] is 'x' or 'X'
            ? int.TryParse(entity.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code)
            : int.TryParse(entity.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out code);
        return ok && code is > 0 and <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? char.ConvertFromUtf32(code) : null;
    }

    /// <summary>空白折叠、行首尾去空格、去掉控制字符和空行、截断。</summary>
    private static string Tidy(string raw, int maxChars)
    {
        var lines = raw.Split('\n')
            .Select(l => string.Join(' ', new string([.. l.Where(c => !char.IsControl(c) || c == '\t')]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0);
        var joined = string.Join('\n', lines);
        return joined.Length <= maxChars ? joined : joined[..maxChars].TrimEnd() + "…";
    }
}
