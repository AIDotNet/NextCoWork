using System.Text;

namespace Ncw.Agent.Widgets.Sanitizing;

/// <summary>
/// <see cref="Interactive"/>:桌面渲染用——内联脚本照常保留(widget 的交互靠它),但只有 CDN 白名单里的外链脚本 / 样式。
/// <see cref="Static"/>:手机端静态快照——不含任何脚本、不含外链资源,只剩可以安全渲染成一张静态图 / 静态 HTML 的部分。
/// </summary>
public enum SanitizeMode
{
    Interactive,
    Static,
}

/// <summary>一类被拿掉的内容。<paramref name="Rule"/> 是稳定的规则 id(给界面和测试用),<paramref name="Detail"/> 是第一个例子(标签名 / 属性名 / 截短的地址),<paramref name="Count"/> 是出现次数。</summary>
public sealed record SanitizeFinding(string Rule, string Detail, int Count);

/// <summary>根 <c>&lt;svg&gt;</c> 声明的尺寸(像素或 viewBox 的宽高),给客户端预留版面 / 手机端决定缩放用;读不出就是 null。</summary>
public readonly record struct SizeHint(int? Width, int? Height);

/// <param name="Code">消毒后的源码。没有任何改动时与输入逐字相同(<c>visualize_edit_widget</c> 的 <c>old_string</c> 因此仍然能对上)。</param>
public sealed record SanitizeResult(string Code, IReadOnlyList<SanitizeFinding> Findings, SizeHint? Size)
{
    public bool Changed(string original) => !string.Equals(Code, original, StringComparison.Ordinal);
}

/// <summary>
/// 小组件载荷(HTML / SVG 片段)的服务端消毒(3.0 新增,2.x 只靠渲染宿主的 CSP)。它是<b>纵深防御的第一层</b>:真正的边界仍是渲染宿主的沙箱 iframe(不透明源)和 CSP,
/// 但载荷要进转录、要发给别的控制端(手机端的静态快照),所以 Agent 先把明显的危险物拿掉,并把「拿掉了什么」说给模型听,让它下一次写对。
/// <para>
/// 做法是<b>手写分词器 + 白名单</b>(不用正则剥标签,不用反射型库):标签名过白名单(HTML 与 SVG 各一张),未知标签只去标签、留内容;
/// 属性过黑名单加按元素的 URL 策略;<c>&lt;script&gt;</c> / <c>&lt;style&gt;</c> 按 HTML 规则当原始文本读(SVG 里的 <c>&lt;style&gt;</c> 也这样读,但块内 <c>&lt;</c> 被转义,两种解释结果相同);
/// 注释一律去掉;孤立的 <c>&lt;</c> 转义成 <c>&amp;lt;</c>(去掉注释 / 标签后不会把相邻文本拼成新标签)。没有改动的标签 / 文本逐字复制。
/// </para>
/// <para>
/// 规则 id:<c>element.forbidden</c>(iframe / object / embed / base / meta / foreignObject / template / math 等)、<c>element.unsupported</c>(其余未知标签)、<c>attr.event</c>(<c>on*</c> 事件处理属性)、
/// <c>attr.forbidden</c>(srcdoc / formaction / nonce 等)、<c>url.scheme</c>(<c>javascript:</c> / 非图片 <c>data:</c> 等)、<c>url.external</c>(外链图片 / 非 <c>#</c> 的引用)、
/// <c>script.external</c>(不在 CDN 白名单里的 <c>&lt;script src&gt;</c>)、<c>script.type</c>、<c>script.svg</c>、<c>script.static</c>、<c>link.external</c>、<c>css.unsafe</c>、<c>animate.href</c>、<c>element.depth</c>。
/// </para>
/// </summary>
public static class WidgetSanitizer
{
    public static SanitizeResult Sanitize(string source, SanitizeMode mode) => new Run(source.Replace("\0", "", StringComparison.Ordinal), mode).Execute();

    private static readonly HashSet<string> HtmlAllowed = new(StringComparer.Ordinal)
    {
        "a", "abbr", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br", "button", "canvas", "caption", "center", "cite", "code", "col", "colgroup",
        "data", "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup",
        "hr", "i", "img", "input", "ins", "kbd", "label", "legend", "li", "main", "mark", "menu", "meter", "nav", "ol", "optgroup", "option", "output", "p", "pre", "progress", "q",
        "rp", "rt", "ruby", "s", "samp", "section", "select", "small", "span", "strong", "sub", "summary", "sup", "table", "tbody", "td", "textarea", "tfoot", "th", "thead", "time",
        "tr", "tt", "u", "ul", "var", "wbr",
    };

    private static readonly HashSet<string> HtmlVoid = new(StringComparer.Ordinal) { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    /// <summary>SVG 元素的规范写法(小写 → 规范名)。<c>script</c> / <c>style</c> / <c>foreignObject</c> 不在这里:各有专门的处理。</summary>
    private static readonly Dictionary<string, string> SvgAllowed = BuildSvg();

    private static Dictionary<string, string> BuildSvg()
    {
        var names = new[]
        {
            "svg", "g", "defs", "symbol", "use", "image", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "textPath", "title", "desc", "switch", "marker",
            "pattern", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "filter", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite", "feConvolveMatrix",
            "feDiffuseLighting", "feDisplacementMap", "feDistantLight", "feDropShadow", "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feImage", "feMerge",
            "feMergeNode", "feMorphology", "feOffset", "fePointLight", "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence", "animate", "animateTransform", "animateMotion", "set",
            "mpath", "a", "view", "metadata",
        };
        return names.ToDictionary(static n => n.ToLowerInvariant(), static n => n, StringComparer.Ordinal);
    }

    /// <summary>在 SVG 内容里遇到就「跳出外来内容」回到 HTML 的标签(HTML 规范的 breakout 列表;<c>font</c> 另行判断属性)。</summary>
    private static readonly HashSet<string> Breakout = new(StringComparer.Ordinal)
    {
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2", "h3", "h4", "h5", "h6", "head", "hr", "i", "img", "li", "listing",
        "menu", "meta", "nobr", "ol", "p", "pre", "ruby", "s", "small", "span", "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var",
    };

    /// <summary>被整体丢弃的元素(连同内容)。读法见 <see cref="RawKinds"/> / 深度计数。</summary>
    private static readonly HashSet<string> SkipWithContent = new(StringComparer.Ordinal) { "foreignobject", "math", "template", "object", "applet" };

    /// <summary>HTML 里内容按原始文本读的元素:丢弃时读到第一个 <c>&lt;/name</c>。</summary>
    private static readonly HashSet<string> RawDropped = new(StringComparer.Ordinal) { "title", "iframe", "noembed", "noframes", "noscript", "xmp" };

    private static readonly HashSet<string> ReportedElements = new(StringComparer.Ordinal)
    {
        "iframe", "frame", "frameset", "object", "embed", "applet", "base", "meta", "noembed", "noframes", "xmp", "template", "math", "foreignobject", "portal", "fencedframe", "plaintext",
    };

    private static readonly HashSet<string> SilentElements = new(StringComparer.Ordinal) { "html", "head", "body", "title" };

    /// <summary>除 <c>href</c> / <c>src</c> 之外一律拿掉的「地址类」属性。</summary>
    private static readonly HashSet<string> UrlLikeAttributes = new(StringComparer.Ordinal)
    {
        "poster", "data", "background", "srcset", "imagesrcset", "codebase", "longdesc", "usemap", "profile", "lowsrc", "dynsrc", "ping", "action", "formaction", "manifest", "cite",
    };

    private static readonly HashSet<string> ForbiddenAttributes = new(StringComparer.Ordinal) { "srcdoc", "nonce", "is", "http-equiv", "formtarget" };

    private static readonly string[] KnownNamespaces = ["http://www.w3.org/2000/svg", "http://www.w3.org/1999/xlink", "http://www.w3.org/1999/xhtml"];

    private enum Ctx
    {
        Html,
        Svg,
    }

    private readonly record struct Open(string Name, bool Svg, bool Integration);

    private sealed record Attr(string Name, string? Raw, int Start, int End);

    private sealed record Tag(string Name, string Lower, List<Attr> Attrs, bool SelfClosing, int Start, int End);

    private sealed class Run(string src, SanitizeMode mode)
    {
        private const int MaxDepth = 256;

        private readonly StringBuilder _out = new(src.Length);
        private readonly List<Open> _stack = [];
        private readonly Dictionary<(string, string), int> _findingIndex = [];
        private readonly List<(string Rule, string Detail, int Count)> _findings = [];
        private string? _skipName;
        private int _skipDepth;
        private SizeHint? _size;
        private bool _sawRoot;

        public SanitizeResult Execute()
        {
            var i = 0;
            var n = src.Length;
            while (i < n)
            {
                if (src[i] != '<')
                {
                    var next = src.IndexOf('<', i);
                    if (next < 0)
                    {
                        next = n;
                    }

                    if (_skipName is null)
                    {
                        _out.Append(src, i, next - i);
                    }

                    i = next;
                    continue;
                }

                if (i + 1 >= n)
                {
                    EmitStrayLt();
                    i++;
                    continue;
                }

                var c = src[i + 1];
                if (char.IsAsciiLetter(c))
                {
                    i = StartTag(i);
                }
                else if (c == '/')
                {
                    if (i + 2 < n && char.IsAsciiLetter(src[i + 2]))
                    {
                        i = EndTag(i);
                    }
                    else if (i + 2 < n && src[i + 2] == '>')
                    {
                        i += 3;
                    }
                    else
                    {
                        i = SkipPast('>', i + 2);
                    }
                }
                else if (c == '!')
                {
                    i = src.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal) ? SkipComment(i) : SkipPast('>', i + 2);
                }
                else if (c == '?')
                {
                    i = SkipPast('>', i + 2);
                }
                else
                {
                    EmitStrayLt();
                    i++;
                }
            }

            // 流里没收尾的原始文本元素已经在各自的处理里补上了结束标签;其余元素不补(没有改动 = 原样)。
            return new SanitizeResult(_out.ToString(), Summarize(), _size);
        }

        // ───────────────────────── 分词辅助 ─────────────────────────

        private void EmitStrayLt()
        {
            if (_skipName is null)
            {
                _out.Append("&lt;");
            }
        }

        private int SkipPast(char terminator, int from)
        {
            var at = src.IndexOf(terminator, Math.Min(from, src.Length));
            return at < 0 ? src.Length : at + 1;
        }

        private int SkipComment(int start)
        {
            // <!--> 和 <!---> 是空注释(HTML 规范);否则读到 -->,没有结尾就读到末尾。
            var body = start + 4;
            if (body < src.Length && src[body] == '>')
            {
                return body + 1;
            }

            if (body + 1 < src.Length && src[body] == '-' && src[body + 1] == '>')
            {
                return body + 2;
            }

            var end = src.IndexOf("-->", body, StringComparison.Ordinal);
            return end < 0 ? src.Length : end + 3;
        }

        private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

        /// <summary>解析起始标签;文件在标签中间结束(没有 <c>&gt;</c>)时返回 null:HTML 规范里这种标签直接丢弃。</summary>
        private Tag? ParseTag(int start, out int next)
        {
            var n = src.Length;
            var i = start + 1;
            var nameStart = i;
            while (i < n && !IsSpace(src[i]) && src[i] != '/' && src[i] != '>')
            {
                i++;
            }

            var name = src[nameStart..i];
            var attrs = new List<Attr>();
            var selfClosing = false;
            while (true)
            {
                while (i < n && (IsSpace(src[i]) || src[i] == '/'))
                {
                    selfClosing = src[i] == '/' && i + 1 < n && src[i + 1] == '>';
                    i++;
                }

                if (i >= n)
                {
                    next = n;
                    return null;
                }

                if (src[i] == '>')
                {
                    i++;
                    break;
                }

                selfClosing = false;
                var attrStart = i;
                var an = i;
                if (src[i] == '=')
                {
                    i++;
                }

                while (i < n && !IsSpace(src[i]) && src[i] != '/' && src[i] != '>' && src[i] != '=')
                {
                    i++;
                }

                var attrName = src[an..i];
                var j = i;
                while (j < n && IsSpace(src[j]))
                {
                    j++;
                }

                string? raw = null;
                if (j < n && src[j] == '=')
                {
                    j++;
                    while (j < n && IsSpace(src[j]))
                    {
                        j++;
                    }

                    if (j < n && src[j] is '"' or '\'')
                    {
                        var q = src[j];
                        var close = src.IndexOf(q, j + 1);
                        if (close < 0)
                        {
                            next = n;
                            return null;
                        }

                        raw = src[(j + 1)..close];
                        i = close + 1;
                    }
                    else
                    {
                        var vs = j;
                        while (j < n && !IsSpace(src[j]) && src[j] != '>')
                        {
                            j++;
                        }

                        raw = src[vs..j];
                        i = j;
                    }
                }

                attrs.Add(new Attr(attrName, raw, attrStart, i));
            }

            next = i;
            return new Tag(name, name.ToLowerInvariant(), attrs, selfClosing, start, i);
        }

        private int EndTag(int start)
        {
            var n = src.Length;
            var i = start + 2;
            var nameStart = i;
            while (i < n && !IsSpace(src[i]) && src[i] != '/' && src[i] != '>')
            {
                i++;
            }

            var lower = src[nameStart..i].ToLowerInvariant();
            var after = SkipPast('>', i); // 结束标签上的属性被忽略(规范:解析后丢弃)。引号里的 '>' 不特殊处理——保守:提前结束只会让后面的内容被当作标记继续检查。
            if (_skipName is not null)
            {
                if (lower == _skipName && --_skipDepth <= 0)
                {
                    _skipName = null;
                }

                return after;
            }

            var index = _stack.FindLastIndex(o => o.Name == lower);
            if (index >= 0)
            {
                _stack.RemoveRange(index, _stack.Count - index);
                _out.Append("</").Append(src, nameStart, i - nameStart).Append('>');
            }
            else if (CurrentCtx() == Ctx.Svg && lower is "p" or "br")
            {
                // 外来内容里的 </p> / </br> 会把解析器带回 HTML。
                PopToHtml();
            }

            return after;
        }

        // ───────────────────────── 上下文 ─────────────────────────

        private Ctx CurrentCtx()
        {
            if (_stack.Count == 0)
            {
                return Ctx.Html;
            }

            var top = _stack[^1];
            return top.Svg && !top.Integration ? Ctx.Svg : Ctx.Html;
        }

        private void PopToHtml()
        {
            while (_stack.Count > 0 && CurrentCtx() == Ctx.Svg)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        // ───────────────────────── 起始标签 ─────────────────────────

        private int StartTag(int start)
        {
            var tag = ParseTag(start, out var next);
            if (tag is null)
            {
                return next; // 标签没写完:丢弃到末尾
            }

            var lower = tag.Lower;
            if (_skipName is not null)
            {
                if (lower == _skipName && !tag.SelfClosing)
                {
                    _skipDepth++;
                }

                return next;
            }

            var ctx = CurrentCtx();
            if (ctx == Ctx.Svg && (Breakout.Contains(lower) || (lower == "font" && tag.Attrs.Any(static a => a.Name.ToLowerInvariant() is "color" or "face" or "size"))))
            {
                PopToHtml();
                ctx = Ctx.Html;
            }

            return ctx == Ctx.Html ? HtmlStart(tag, next) : SvgStart(tag, next);
        }

        private int HtmlStart(Tag tag, int next)
        {
            var lower = tag.Lower;
            switch (lower)
            {
                case "svg":
                    return EmitElement(tag, next, svg: true, canonical: "svg", forceNoPush: tag.SelfClosing);
                case "script":
                    return ScriptElement(tag, next, svgCtx: false);
                case "style":
                    return StyleElement(tag, next, svgCtx: false);
                case "textarea":
                {
                    var (contentEnd, after, closed) = ReadRaw("textarea", next);
                    var kept = ProcessAttrs(tag, svgCtx: false, out var changed);
                    WriteTag(tag, kept, changed);
                    _out.Append(src, next, contentEnd - next).Append("</textarea>");
                    _ = closed;
                    return after;
                }

                case "plaintext":
                    Found("element.forbidden", "<plaintext>");
                    return src.Length;
                case "link":
                    LinkElement(tag);
                    return next;
            }

            if (RawDropped.Contains(lower))
            {
                if (ReportedElements.Contains(lower))
                {
                    Found("element.forbidden", "<" + lower + ">");
                }

                return ReadRaw(lower, next).After;
            }

            if (SkipWithContent.Contains(lower))
            {
                Found("element.forbidden", "<" + (lower == "foreignobject" ? "foreignObject" : lower) + ">");
                if (!tag.SelfClosing)
                {
                    _skipName = lower;
                    _skipDepth = 1;
                }

                return next;
            }

            if (HtmlAllowed.Contains(lower))
            {
                return EmitElement(tag, next, svg: false, canonical: lower, forceNoPush: HtmlVoid.Contains(lower));
            }

            if (HtmlVoid.Contains(lower) || ReportedElements.Contains(lower))
            {
                Found("element.forbidden", "<" + lower + ">");
            }
            else if (!SilentElements.Contains(lower))
            {
                Found("element.unsupported", "<" + lower + ">");
            }

            return next; // 只去标签,内容留下
        }

        private int SvgStart(Tag tag, int next)
        {
            var lower = tag.Lower;
            switch (lower)
            {
                case "script":
                    return ScriptElement(tag, next, svgCtx: true);
                case "style":
                    return StyleElement(tag, next, svgCtx: true);
                case "foreignobject":
                    Found("element.forbidden", "<foreignObject>");
                    if (!tag.SelfClosing)
                    {
                        _skipName = lower;
                        _skipDepth = 1;
                    }

                    return next;
            }

            if (!SvgAllowed.TryGetValue(lower, out var canonical))
            {
                Found("element.unsupported", "<" + lower + ">");
                return next;
            }

            if (lower is "animate" or "set" or "animatetransform" or "animatemotion")
            {
                foreach (var attr in tag.Attrs)
                {
                    if (!attr.Name.Equals("attributeName", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var target = WidgetUrls.Normalize(WidgetUrls.DecodeEntities(attr.Raw ?? ""));
                    if (target.Contains("href", StringComparison.Ordinal) || target.StartsWith("on", StringComparison.Ordinal))
                    {
                        Found("animate.href", "<" + canonical + " attributeName=" + target + ">");
                        if (!tag.SelfClosing)
                        {
                            _skipName = lower;
                            _skipDepth = 1;
                        }

                        return next;
                    }
                }
            }

            return EmitElement(tag, next, svg: true, canonical: canonical, forceNoPush: tag.SelfClosing, integration: lower is "title" or "desc");
        }

        private int EmitElement(Tag tag, int next, bool svg, string canonical, bool forceNoPush, bool integration = false)
        {
            var kept = ProcessAttrs(tag, svgCtx: svg || CurrentCtx() == Ctx.Svg, out var changed);
            if (!forceNoPush && _stack.Count >= MaxDepth)
            {
                Found("element.depth", "<" + canonical + ">");
                return next;
            }

            if (svg && !_sawRoot && _stack.Count == 0 && tag.Lower == "svg")
            {
                _sawRoot = true;
                _size = ReadSize(kept);
            }

            WriteTag(tag, kept, changed);
            if (!forceNoPush)
            {
                _stack.Add(new Open(canonical.ToLowerInvariant(), svg, integration));
            }

            return next;
        }

        private void WriteTag(Tag tag, List<Attr> kept, bool changed)
        {
            if (!changed)
            {
                _out.Append(src, tag.Start, tag.End - tag.Start);
                return;
            }

            _out.Append('<').Append(tag.Name);
            foreach (var attr in kept)
            {
                _out.Append(' ').Append(attr.Name);
                if (attr.Raw is not null)
                {
                    _out.Append("=\"").Append(attr.Raw.Replace("\"", "&quot;", StringComparison.Ordinal)).Append('"');
                }
            }

            _out.Append(tag.SelfClosing ? "/>" : ">");
        }

        // ───────────────────────── 原始文本元素 ─────────────────────────

        /// <summary>读原始文本:内容到第一个 <c>&lt;/name</c>(后面跟空白、<c>/</c> 或 <c>&gt;</c>)为止;没有就读到末尾。</summary>
        private (int ContentEnd, int After, bool Closed) ReadRaw(string name, int from)
        {
            var i = from;
            while (true)
            {
                var at = src.IndexOf("</", i, StringComparison.Ordinal);
                if (at < 0)
                {
                    return (src.Length, src.Length, false);
                }

                var nameEnd = at + 2 + name.Length;
                if (nameEnd <= src.Length
                    && string.Compare(src, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (nameEnd == src.Length || IsSpace(src[nameEnd]) || src[nameEnd] is '/' or '>'))
                {
                    return (at, SkipPast('>', nameEnd), true);
                }

                i = at + 2;
            }
        }

        private int ScriptElement(Tag tag, int next, bool svgCtx)
        {
            var (contentEnd, after, _) = ReadRaw("script", next);
            if (svgCtx)
            {
                Found("script.svg", "<script> inside <svg>");
                return after;
            }

            if (mode == SanitizeMode.Static)
            {
                Found("script.static", "<script>");
                return after;
            }

            string? type = null;
            string? scriptSrc = null;
            foreach (var attr in tag.Attrs)
            {
                var name = attr.Name.ToLowerInvariant();
                if (name == "type" && type is null)
                {
                    type = attr.Raw ?? "";
                }
                else if (name == "src" && scriptSrc is null)
                {
                    scriptSrc = attr.Raw ?? "";
                }
            }

            if (type is not null)
            {
                var t = type.Trim().ToLowerInvariant();
                if (t is not ("" or "text/javascript" or "application/javascript" or "module" or "application/json" or "application/ld+json"))
                {
                    Found("script.type", "type=" + Clip(t));
                    return after;
                }
            }

            if (scriptSrc is not null && !WidgetUrls.IsCdnUrl(scriptSrc))
            {
                Found("script.external", Clip(WidgetUrls.DecodeEntities(scriptSrc).Trim()));
                return after;
            }

            var kept = ProcessAttrs(tag, svgCtx: false, out var changed);
            // 只留脚本需要的属性:事件处理属性已经被 ProcessAttrs 拿掉;其余的(id / defer / async / crossorigin / integrity / type / src)保留。
            WriteTag(tag, kept, changed);
            _out.Append(src, next, contentEnd - next).Append("</script>");
            return after;
        }

        private int StyleElement(Tag tag, int next, bool svgCtx)
        {
            var (contentEnd, after, _) = ReadRaw("style", next);
            var css = src[next..contentEnd];
            if (CssGuard.Check(css) is { } problem)
            {
                Found("css.unsafe", "<style> (" + problem + ")");
                return after;
            }

            var kept = ProcessAttrs(tag, svgCtx, out var changed);
            WriteTag(tag, kept, changed);
            _out.Append(CssGuard.ForOutput(css)).Append("</style>");
            return after;
        }

        private void LinkElement(Tag tag)
        {
            string? rel = null;
            string? href = null;
            foreach (var attr in tag.Attrs)
            {
                switch (attr.Name.ToLowerInvariant())
                {
                    case "rel" when rel is null:
                        rel = attr.Raw ?? "";
                        break;
                    case "href" when href is null:
                        href = attr.Raw ?? "";
                        break;
                }
            }

            var stylesheet = rel is not null && rel.Trim().Equals("stylesheet", StringComparison.OrdinalIgnoreCase);
            if (mode == SanitizeMode.Interactive && stylesheet && href is not null && WidgetUrls.IsCdnUrl(href))
            {
                var kept = ProcessAttrs(tag, svgCtx: false, out var changed);
                WriteTag(tag, kept, changed);
                return;
            }

            Found("link.external", Clip(href is null ? "<link>" : WidgetUrls.DecodeEntities(href).Trim()));
        }

        // ───────────────────────── 属性 ─────────────────────────

        private List<Attr> ProcessAttrs(Tag tag, bool svgCtx, out bool changed)
        {
            changed = false;
            var kept = new List<Attr>(tag.Attrs.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var element = tag.Lower;
            foreach (var attr in tag.Attrs)
            {
                var name = attr.Name.ToLowerInvariant();
                if (!IsValidAttributeName(attr.Name))
                {
                    changed = true;
                    Found("attr.forbidden", Clip(attr.Name));
                    continue;
                }

                if (!seen.Add(name))
                {
                    changed = true; // 重复属性:浏览器只认第一个,多余的去掉
                    continue;
                }

                var verdict = Judge(element, svgCtx, name, attr);
                if (verdict is not null)
                {
                    changed = true;
                    Found(verdict.Value.Rule, verdict.Value.Detail);
                    continue;
                }

                kept.Add(attr);
            }

            return kept;
        }

        private static bool IsValidAttributeName(string name)
        {
            if (name.Length is 0 or > 64 || !(char.IsAsciiLetter(name[0]) || name[0] is '_' or ':'))
            {
                return false;
            }

            foreach (var c in name)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>null = 保留;否则是拿掉它的规则与说明。</summary>
        private (string Rule, string Detail)? Judge(string element, bool svgCtx, string name, Attr attr)
        {
            var raw = attr.Raw ?? "";
            if (name.StartsWith("on", StringComparison.Ordinal))
            {
                return ("attr.event", name);
            }

            if (ForbiddenAttributes.Contains(name))
            {
                return ("attr.forbidden", name);
            }

            if (WidgetUrls.IsScriptScheme(raw))
            {
                return ("url.scheme", name + "=" + Clip(raw.Trim()));
            }

            if (name == "style")
            {
                return CssGuard.Check(WidgetUrls.DecodeEntities(raw)) is { } css ? ("css.unsafe", "style attribute (" + css + ")") : null;
            }

            if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
            {
                return KnownNamespaces.Contains(raw.Trim(), StringComparer.OrdinalIgnoreCase) ? null : ("attr.forbidden", name);
            }

            if (UrlLikeAttributes.Contains(name))
            {
                return ("url.external", name);
            }

            if (name is "href" or "xlink:href" or "src")
            {
                var kind = WidgetUrls.Classify(raw);
                var allowed = element switch
                {
                    "a" or "area" when name != "src" => kind is UrlKind.Fragment or UrlKind.Http or UrlKind.Mailto or UrlKind.Empty,
                    "img" when name == "src" => kind == UrlKind.DataImage,
                    "image" or "feimage" when name != "src" => kind == UrlKind.DataImage,
                    "script" or "link" => true, // 在各自的专门处理里已经校验过白名单
                    _ => name != "src" && kind is UrlKind.Fragment or UrlKind.Empty,
                };
                if (!allowed)
                {
                    return (kind is UrlKind.Http or UrlKind.Other ? "url.external" : "url.scheme", name + "=" + Clip(WidgetUrls.DecodeEntities(raw).Trim()));
                }
            }

            _ = svgCtx;
            return null;
        }

        // ───────────────────────── 结果 ─────────────────────────

        private void Found(string rule, string detail)
        {
            if (_findingIndex.TryGetValue((rule, detail), out var index))
            {
                var f = _findings[index];
                _findings[index] = f with { Count = f.Count + 1 };
                return;
            }

            if (_findings.Count >= 64)
            {
                return;
            }

            _findingIndex[(rule, detail)] = _findings.Count;
            _findings.Add((rule, detail, 1));
        }

        private List<SanitizeFinding> Summarize() => [.. _findings.Select(static f => new SanitizeFinding(f.Rule, f.Detail, f.Count))];

        private static string Clip(string text) => text.Length <= 80 ? text : text[..80] + "...";

        private static SizeHint? ReadSize(List<Attr> attrs)
        {
            int? width = null;
            int? height = null;
            string? viewBox = null;
            foreach (var attr in attrs)
            {
                switch (attr.Name.ToLowerInvariant())
                {
                    case "width":
                        width = ParsePixels(attr.Raw);
                        break;
                    case "height":
                        height = ParsePixels(attr.Raw);
                        break;
                    case "viewbox":
                        viewBox = attr.Raw;
                        break;
                }
            }

            if ((width is null || height is null) && viewBox is not null)
            {
                var parts = viewBox.Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4
                    && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
                    && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
                    && w > 0 && h > 0 && w < 100_000 && h < 100_000)
                {
                    width ??= (int)Math.Round(w);
                    height ??= (int)Math.Round(h);
                }
            }

            return width is null && height is null ? null : new SizeHint(width, height);
        }

        private static int? ParsePixels(string? value)
        {
            if (value is null)
            {
                return null;
            }

            var text = value.Trim();
            if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^2];
            }

            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is > 0 and < 100_000 ? (int)Math.Round(v) : null;
        }
    }
}
