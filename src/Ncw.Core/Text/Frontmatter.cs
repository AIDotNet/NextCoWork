using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Ncw.Core.Text;

/// <summary>前置块解析的尺寸闸门。超出的部分不解析 / 被截断,并记入 <see cref="Frontmatter.Skipped"/>。</summary>
public static class FrontmatterLimits
{
    /// <summary>前置块本身的字符上限。超出的部分不解析,但正文位置仍然找得对。</summary>
    public const int BlockMax = 8 * 1024;

    /// <summary>找结束标记时最多往下看多少字符。防一个没有结束标记的大文件把整个文件当成前置块扫一遍。</summary>
    public const int ScanMax = 64 * 1024;

    /// <summary>键的数量上限。</summary>
    public const int KeysMax = 64;

    /// <summary>单个值的字符上限。description 会直接进系统提示词,长度必须有个头。</summary>
    public const int ValueMax = 4 * 1024;

    /// <summary>列表的项数上限。</summary>
    public const int ListMax = 64;
}

/// <summary>前置块里的一个值:一个字符串,或一个字符串列表。解析器只会产出这两种 —— 最坏的结果是一个错误的字符串。</summary>
public readonly struct FrontmatterValue : IEquatable<FrontmatterValue>
{
    private readonly string? _text;
    private readonly IReadOnlyList<string>? _items;

    private FrontmatterValue(string? text, IReadOnlyList<string>? items)
    {
        _text = text;
        _items = items;
    }

    /// <summary>是不是列表。</summary>
    public bool IsList => _items is not null;

    /// <summary>字符串值;是列表时为空串。</summary>
    public string Text => _text ?? "";

    /// <summary>列表项;是字符串时为空列表。</summary>
    public IReadOnlyList<string> Items => _items ?? [];

    /// <summary>造一个字符串值。</summary>
    public static FrontmatterValue FromText(string? text) => new(text ?? "", null);

    /// <summary>造一个列表值(拷贝一份,之后调用方改原集合不影响它)。</summary>
    public static FrontmatterValue FromList(IEnumerable<string> items) => new(null, items.ToArray());

    public static implicit operator FrontmatterValue(string text) => FromText(text);

    public static implicit operator FrontmatterValue(string[] items) => FromList(items);

    public bool Equals(FrontmatterValue other)
    {
        if (IsList != other.IsList)
        {
            return false;
        }

        return IsList
            ? Items.SequenceEqual(other.Items, StringComparer.Ordinal)
            : string.Equals(Text, other.Text, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is FrontmatterValue other && Equals(other);

    public override int GetHashCode()
    {
        if (!IsList)
        {
            return string.GetHashCode(Text, StringComparison.Ordinal);
        }

        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(FrontmatterValue left, FrontmatterValue right) => left.Equals(right);

    public static bool operator !=(FrontmatterValue left, FrontmatterValue right) => !left.Equals(right);

    public override string ToString() => IsList ? "[" + string.Join(", ", Items) + "]" : Text;
}

/// <summary>一次解析的结果。</summary>
/// <param name="Data">键 → 值,键按序数比较。嵌套的一层 map 已扁平成 <c>parent.child</c>。</param>
/// <param name="Body">前置块之后的全部内容。没有前置块时就是原文。</param>
/// <param name="Skipped">不支持的语法、被截断的值 —— 由调用方决定是 warn 还是忽略。</param>
public sealed record Frontmatter(
    IReadOnlyDictionary<string, FrontmatterValue> Data,
    string Body,
    IReadOnlyList<string> Skipped);

/// <summary><see cref="FrontmatterParser.Serialize(IReadOnlyDictionary{string, FrontmatterValue?}, string, FrontmatterSerializeOptions?)"/> 的选项。</summary>
public sealed record FrontmatterSerializeOptions
{
    /// <summary>优先输出的键序;其余键按字母序跟在后面。</summary>
    public IReadOnlyList<string>? Order { get; init; }
}

/// <summary>
/// YAML 前置块解析(2.x 的 <c>frontmatter.ts</c>)—— 和不可信文本的消毒(会话层 <c>PromptText</c>)平级的「单一答案」。
/// Skill 加载器与子代理加载器<b>两处</b>都要读 frontmatter,两个来源必须给出同一个答案
/// (「<c>tools: Read, Grep</c> 算不算一个列表」一旦分叉,就是不报错、只是行为不一致的洞)。
/// <para>
/// <b>为什么手写,不引 YAML 库</b>:要处理的只是一张扁平 map,值是标量或列表;而 SKILL.md 来自 zip / git,是<b>不可信输入</b>。
/// 完整 YAML 解析器的历史攻击面(alias 炸弹、merge key、类型标签)是白送的风险。这个解析器只能产出「字符串或字符串列表」,最坏的结果是一个错误的字符串。
/// </para>
/// <para>
/// <b>支持的子集(这就是契约,别扩)</b>:首行 <c>---</c>(容忍 BOM 和 CRLF),以单独一行 <c>---</c> 或 <c>...</c> 收尾;
/// 键 <c>^[A-Za-z_][A-Za-z0-9_-]*$</c>,不匹配的行跳过并记入 skipped;值为裸标量、单 / 双引号、<c>[a, b]</c> 流式序列、<c>- x</c> 块式序列;
/// 块标量 <c>|</c> / <c>&gt;</c>(含截断指示 <c>-</c> / <c>+</c> 与缩进指示 1-9);<b>一层</b>嵌套 map,扁平成 <c>parent.child</c>;<c>true</c> / <c>false</c> 保持字符串,由 <see cref="Bool"/> 统一 coerce。
/// </para>
/// <para>
/// <b>明确不支持,遇到就跳过并记入 skipped</b>:两层以上的嵌套、锚点别名、多文档。
/// <b>行内 <c>#</c> 注释不剥离</b> —— description 里就带 <c>#</c>(「用 <c>#</c> 开头的行是注释」这类说明),剥了会把语义从中间截断。
/// </para>
/// <para>
/// <b>为什么块标量要认</b>:<c>description: |</c> 是官方 Skill 写多行说明最常见的形状。跳过它的代价不止「少一个键」:安装器把 skipped 非空当硬失败,整包就装不上,
/// 而用户只看到一句「frontmatter 无法识别」。块标量只是「一种换行写法」,一层嵌套只是「一种键名写法」,都不引入新类型。
/// 写回去是双引号里的 <c>\n</c>,不是 <c>|</c>:往返后<b>内容</b>逐字相等,只有形状变了。
/// </para>
/// <para>
/// <see cref="Parse"/> <b>永不抛异常</b>:没有前置块就返回空 data 与原文。一个坏掉的 SKILL.md 不该让整次目录扫描失败 ——
/// 「这条有效吗」是加载器的判断(缺 name / description 即无效),不是解析器的。解析器只负责「尽力读出能读的,剩下的照实记下来」。
/// </para>
/// 长度一律按 UTF-16 码元计(和 2.x 的字符串长度同一个口径)。
/// </summary>
public static class FrontmatterParser
{
    // 块标量头(`|` / `>`,后面可跟截断指示 `-` / `+` 和缩进指示 1-9,两种顺序都合法)由 TryParseBlockHeader 手写识别;
    // needsQuote 直接复用它,不是抄一个形状近似的判断 —— 两边一旦分叉,「裸值恰好长得像块标量头」的字符串就会被原样写出、读回来时吃掉后面几行。

    /// <summary>
    /// 这三个键必须显式拒绝。2.x 里是防原型污染(JS 对象下游拷来拷去,<c>obj['__proto__'] = v</c> 真会污染);C# 没有这个问题,
    /// 但这是<b>对等规则</b>:同一份 SKILL.md 在两个版本里得到同一个 skipped,所以保留。
    /// </summary>
    private static bool IsForbiddenKey(string key) =>
        key is "__proto__" or "constructor" or "prototype";

    /// <summary>解析过程里的一个值槽:字符串,或可增长的列表。</summary>
    private sealed class Slot
    {
        public string Text = "";
        public List<string>? Items;
    }

    /// <summary>解析一份文本。永不抛异常。</summary>
    public static Frontmatter Parse(string src)
    {
        // BOM 与 CRLF:两者都会让「首行是不是恰好 ---」判断失败,而失败的表现是整个前置块被当成正文 —— Skill 变成「没有 name」于是静默消失。
        var text = NormalizeNewlines(src.Length > 0 && src[0] == '﻿' ? src[1..] : src);
        var lines = text.Split('\n');

        if (JsTrim(lines[0]) != "---")
        {
            return Empty(src, []);
        }

        var skipped = new List<string>();

        // ── 先找结束标记,确定正文从哪儿开始 ──
        var end = -1;
        var scanned = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            scanned += line.Length + 1;
            if (scanned > FrontmatterLimits.ScanMax)
            {
                break;
            }

            var t = JsTrim(line);
            if (t is "---" or "...")
            {
                end = i;
                break;
            }
        }

        // 没有结束标记 = 这根本不是一个前置块。绝不能把整个文件当成前置块吞掉:那样正文就没了,而模型收到的是一份空 Skill。
        if (end == -1)
        {
            return Empty(src, ["front matter has no closing marker (---), treating the whole block as body"]);
        }

        var body = string.Join('\n', lines[(end + 1)..]);

        // ── 解析块内的键值 ──
        var acc = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var order = new List<string>(); // Map 的插入序:删除再写入会排到末尾
        // 上一个「值为空」的键 —— 块式序列(`- x`)要挂到它上面
        string? pendingListKey = null;
        // 上一个「值为空」的顶层键 —— 缩进的 `k: v` 作为它的子键挂上去
        string? parentKey = null;
        // 子层的缩进宽度,由第一个子键定下来。0 = 还没见过子键
        var childIndent = 0;
        var used = 0;
        var overflowed = false;

        void Set(string key, Slot slot)
        {
            if (acc.ContainsKey(key))
            {
                acc[key] = slot;
                return;
            }

            acc[key] = slot;
            order.Add(key);
        }

        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            used += line.Length + 1;
            if (used > FrontmatterLimits.BlockMax)
            {
                overflowed = true;
                break;
            }

            var indented = line.Length > 0 && IsJsWhite(line[0]);
            var t = JsTrim(line);

            if (t.Length == 0)
            {
                continue;
            }

            // 整行注释:静默跳过。这是标准 YAML,跳过它不算「丢了东西」。
            if (t[0] == '#')
            {
                continue;
            }

            // ── 块式序列的一项 ──
            if (t.StartsWith("- ", StringComparison.Ordinal) || t == "-")
            {
                if (pendingListKey is null)
                {
                    skipped.Add($"line {i + 1}: top-level list item has no key, ignored");
                    continue;
                }

                var item = Unquote(JsTrim(t[1..]));
                if (item.Length == 0)
                {
                    continue;
                }

                var cur = acc[pendingListKey];
                var list = cur.Items ?? [];
                if (list.Count >= FrontmatterLimits.ListMax)
                {
                    skipped.Add($"\"{pendingListKey}\": list exceeds {FrontmatterLimits.ListMax} items, extras dropped");
                    continue;
                }

                list.Add(ClampValue(item, pendingListKey, skipped));
                Set(pendingListKey, new Slot { Items = list });
                continue;
            }

            // 有缩进而又不是列表项 —— 一层嵌套 map,扁平成 `parent.child` 收下。
            // 只收一层,且值仍然只有「字符串或字符串列表」:「最坏结果是一个错误的字符串」的保证没有松动,
            // 而现实里的 SKILL.md 也只用到这一层(`metadata:` 下挂 author / version / tags)。再深一层就跳过。
            var indent = indented ? line.Length - JsTrimStart(line).Length : 0;
            if (indented)
            {
                if (parentKey is null)
                {
                    skipped.Add($"line {i + 1}: indented line has no parent key, ignored");
                    pendingListKey = null;
                    continue;
                }

                if (childIndent == 0)
                {
                    childIndent = indent;
                }

                if (indent != childIndent)
                {
                    skipped.Add($"line {i + 1}: only one level of nesting is supported, deeper levels ignored");
                    pendingListKey = null;
                    continue;
                }
            }

            var colon = t.IndexOf(':');
            if (colon <= 0)
            {
                skipped.Add($"line {i + 1}: not a 'key: value' line, ignored");
                pendingListKey = null;
                continue;
            }

            var bare = JsTrim(t[..colon]);
            var rawValue = JsTrim(t[(colon + 1)..]);
            pendingListKey = null;
            if (!indented)
            {
                parentKey = null;
                childIndent = 0;
            }

            if (!IsValidKey(bare))
            {
                skipped.Add($"line {i + 1}: invalid key name \"{bare}\", ignored");
                continue;
            }

            if (IsForbiddenKey(bare))
            {
                // 记下来而不是静默忽略:出现这个键几乎不可能是手滑
                skipped.Add($"key \"{bare}\" rejected for security reasons");
                continue;
            }

            var key = indented && parentKey is not null ? $"{parentKey}.{bare}" : bare;
            if (!acc.ContainsKey(key) && acc.Count >= FrontmatterLimits.KeysMax)
            {
                skipped.Add($"key count exceeds {FrontmatterLimits.KeysMax}, \"{key}\" and the rest dropped");
                continue;
            }

            // 父键先前按「空值」记了个占位;既然它下面挂着子键,它就是一张 map,不是空串。
            if (indented && parentKey is not null && acc.TryGetValue(parentKey, out var placeholder)
                && placeholder.Items is null && placeholder.Text.Length == 0)
            {
                acc.Remove(parentKey);
                order.Remove(parentKey);
            }

            // 锚点 / 别名:不支持(值已经没了,只能记一笔)
            if (rawValue.StartsWith('&') || rawValue.StartsWith('*'))
            {
                skipped.Add($"\"{key}\": anchors and aliases are not supported, ignored");
                continue;
            }

            if (TryParseBlockHeader(rawValue, out var folded, out var chomp, out var digits))
            {
                // 缩进指示是相对父节点的列数,所以要叠上键本身的缩进:顶层的 `|2` 是第 2 列,
                // `metadata:` 下某个子键的 `|2` 是第 4 列。0 = 没给,看第一行。
                var blockIndent = digits == 0 ? 0 : indent + digits;
                var collected = new List<string>();
                var j = i + 1;
                for (; j < end; j++)
                {
                    var next = lines[j];
                    var blank = IsBlank(next);
                    if (!blank)
                    {
                        var width = LeadingBlankWidth(next);
                        // 缩进退回到键这一层(或更浅)= 块到此为止。这一行不能计进 `used`,也不能吃掉 —— 它归外层循环,下一轮还要当键值解析。
                        if (width <= indent)
                        {
                            break;
                        }

                        if (blockIndent != 0 && width < blockIndent)
                        {
                            break;
                        }

                        if (blockIndent == 0)
                        {
                            blockIndent = width;
                        }
                    }

                    used += next.Length + 1;
                    if (used > FrontmatterLimits.BlockMax)
                    {
                        overflowed = true;
                        break;
                    }

                    collected.Add(blank ? "" : next[Math.Min(blockIndent, next.Length)..]);
                }

                i = j - 1;
                var blockText = folded ? FoldLines(collected) : string.Join('\n', collected);
                Set(key, new Slot { Text = ClampValue(ChompBlock(blockText, chomp), key, skipped) });
                if (overflowed)
                {
                    break;
                }

                continue;
            }

            if (rawValue.Length == 0)
            {
                // 可能是块式序列的头、一张嵌套 map 的头,也可能就是一个空值。先记成空串,
                // `- x` 来了转成列表,缩进的 `k: v` 来了就把这个占位删掉。
                Set(key, new Slot());
                pendingListKey = key;
                if (!indented)
                {
                    parentKey = key;
                }

                continue;
            }

            if (rawValue.StartsWith('[') && rawValue.EndsWith(']'))
            {
                var items = ParseFlowSeq(rawValue);
                var take = Math.Min(items.Count, FrontmatterLimits.ListMax);
                var clamped = new List<string>(take);
                for (var n = 0; n < take; n++)
                {
                    clamped.Add(ClampValue(items[n], key, skipped));
                }

                Set(key, new Slot { Items = clamped });
                continue;
            }

            Set(key, new Slot { Text = ClampValue(Unquote(rawValue), key, skipped) });
        }

        if (overflowed)
        {
            skipped.Add($"front matter exceeds {FrontmatterLimits.BlockMax / 1024}KB, the rest was not parsed");
        }

        var data = new Dictionary<string, FrontmatterValue>(order.Count, StringComparer.Ordinal);
        foreach (var k in order)
        {
            var slot = acc[k];
            data[k] = slot.Items is null ? FrontmatterValue.FromText(slot.Text) : FrontmatterValue.FromList(slot.Items);
        }

        return new Frontmatter(new ReadOnlyDictionary<string, FrontmatterValue>(data), body, skipped.AsReadOnly());
    }

    /// <summary>
    /// 取一个字符串值。写成列表的话返回 <c>null</c> —— 拼起来是猜,猜错比缺失更难查。全是空白等于没有。
    /// </summary>
    public static string? String(Frontmatter fm, string key)
    {
        if (!fm.Data.TryGetValue(key, out var v) || v.IsList)
        {
            return null;
        }

        var t = JsTrim(v.Text);
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// 取一个列表值。「逗号分隔的字符串」与「YAML 数组」在<b>这一个函数里</b>合流,不在两个加载器里各写一遍:
    /// 用户会把 Claude Code 的 agent 文件原样粘过来,里面写的是 <c>tools: Read, Grep, Glob</c>(一个裸标量);
    /// 而 Skill 的 <c>allowed-tools</c> 更常写成 <c>[Read, Grep]</c>。两种都得认,且必须认成同一个东西。
    /// 空值和不存在都返回 <c>null</c>。
    /// </summary>
    public static IReadOnlyList<string>? List(Frontmatter fm, string key)
    {
        if (!fm.Data.TryGetValue(key, out var v))
        {
            return null;
        }

        var parts = v.IsList ? v.Items : v.Text.Split(',');
        var result = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = JsTrim(part);
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// <c>true</c> / <c>false</c> 在解析时保持字符串,coerce 只在这里发生。认不出返回 <c>null</c>(而不是当成 false:
    /// <c>globalEnabled: maybe</c> 静默变成「关闭」,用户看到的是「我明明写了」;null 让调用方能选自己的默认值)。
    /// </summary>
    public static bool? Bool(Frontmatter fm, string key)
    {
        var v = String(fm, key)?.ToLowerInvariant();
        return v switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => null,
        };
    }

    // ─────────────────────────────────────────────────────────────
    // 解析的小零件
    // ─────────────────────────────────────────────────────────────

    private static Frontmatter Empty(string body, IReadOnlyList<string> skipped) =>
        new(new ReadOnlyDictionary<string, FrontmatterValue>(new Dictionary<string, FrontmatterValue>(StringComparer.Ordinal)), body, skipped);

    /// <summary><c>\r\n</c> 与单独的 <c>\r</c> 都归一成 <c>\n</c>。</summary>
    private static string NormalizeNewlines(string text)
    {
        if (!text.Contains('\r'))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r')
            {
                sb.Append(text[i]);
                continue;
            }

            sb.Append('\n');
            if (i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 和 JS <c>trim()</c> 同一个空白集:含 BOM(U+FEFF),不含 U+0085。
    /// 不能直接用 <c>string.Trim()</c>,否则同一份文件在两个版本里会得到不同的值。
    /// </summary>
    private static bool IsJsWhite(char c) => c == '﻿' || (c != '\u0085' && char.IsWhiteSpace(c));

    private static string JsTrim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsJsWhite(s[start]))
        {
            start++;
        }

        while (end > start && IsJsWhite(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    private static string JsTrimStart(string s)
    {
        var start = 0;
        while (start < s.Length && IsJsWhite(s[start]))
        {
            start++;
        }

        return s[start..];
    }

    private static bool IsBlank(string s)
    {
        foreach (var c in s)
        {
            if (!IsJsWhite(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>行首连续的空格 / tab 个数(块标量缩进只认这两种)。</summary>
    private static int LeadingBlankWidth(string s)
    {
        var n = 0;
        while (n < s.Length && (s[n] == ' ' || s[n] == '\t'))
        {
            n++;
        }

        return n;
    }

    /// <summary>键 <c>^[A-Za-z_][A-Za-z0-9_-]*$</c>。手写而不用正则:没有「<c>$</c> 匹配在末尾换行之前」这类差异。</summary>
    private static bool IsValidKey(string s)
    {
        if (s.Length == 0 || !(IsAsciiLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (!(IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '_' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>
    /// 块标量的头:<c>|</c> 或 <c>&gt;</c>,后面可跟截断指示(<c>-</c> / <c>+</c>)和缩进指示(1-9),两种顺序(<c>|2-</c> 与 <c>|-2</c>)都合法。
    /// 即 <c>^([|&gt;])(?:([-+])([1-9])?|([1-9])([-+])?)?$</c>。<paramref name="chomp"/> 缺省为 <c>'\0'</c>,<paramref name="digits"/> 缺省为 0。
    /// </summary>
    private static bool TryParseBlockHeader(string raw, out bool folded, out char chomp, out int digits)
    {
        folded = false;
        chomp = '\0';
        digits = 0;
        if (raw.Length == 0 || raw.Length > 3 || (raw[0] != '|' && raw[0] != '>'))
        {
            return false;
        }

        folded = raw[0] == '>';
        switch (raw.Length)
        {
            case 1:
                return true;
            case 2:
                if (IsChomp(raw[1]))
                {
                    chomp = raw[1];
                    return true;
                }

                if (IsIndentDigit(raw[1]))
                {
                    digits = raw[1] - '0';
                    return true;
                }

                break;
            default:
                if (IsChomp(raw[1]) && IsIndentDigit(raw[2]))
                {
                    chomp = raw[1];
                    digits = raw[2] - '0';
                    return true;
                }

                if (IsIndentDigit(raw[1]) && IsChomp(raw[2]))
                {
                    digits = raw[1] - '0';
                    chomp = raw[2];
                    return true;
                }

                break;
        }

        folded = false;
        return false;
    }

    private static bool IsChomp(char c) => c is '-' or '+';

    private static bool IsIndentDigit(char c) => c >= '1' && c <= '9';

    /// <summary>去掉引号,并处理引号内那一层最基本的转义。</summary>
    private static string Unquote(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            // YAML 单引号里,两个连续单引号表示一个字面单引号
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            // 单次扫描,不能用链式 replace:一个字面反斜杠按规矩要写成 `\\`,于是 `a\\nb` 里的第二个反斜杠会先被
            // 「\n」那条抓去当成换行转义。有了 Serialize 之后这是「写进去再读出来就损坏」—— 往返契约要求这里必须真的可逆。
            var inner = raw[1..^1];
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\')
                {
                    sb.Append(inner[i]);
                    continue;
                }

                i++;
                // 末尾的孤立反斜杠:原样留着,不吞
                if (i >= inner.Length)
                {
                    return sb.Append('\\').ToString();
                }

                var next = inner[i];
                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case '"':
                    case '\\':
                        sb.Append(next);
                        break;
                    default:
                        // 认不出的转义序列原样保留 —— 猜错比留着更难查
                        sb.Append('\\').Append(next);
                        break;
                }
            }

            return sb.ToString();
        }

        return raw;
    }

    /// <summary><c>[a, b, "c d"]</c> → <c>a</c>, <c>b</c>, <c>c d</c>。不支持项里带逗号的引号串,那需要真解析器。</summary>
    private static List<string> ParseFlowSeq(string raw)
    {
        var result = new List<string>();
        foreach (var part in raw[1..^1].Split(','))
        {
            var item = Unquote(JsTrim(part));
            if (item.Length > 0)
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// 折叠式(<c>&gt;</c>)的换行规则,三条缺一不可:相邻非空行之间的换行折成一个空格;n 个空行变成 n 个换行;
    /// <b>更深缩进的行前后保持字面换行</b>(段落里夹一段代码 / 列表时唯一能让它不被揉成一行的办法)。
    /// </summary>
    private static string FoldLines(IReadOnlyList<string> collected)
    {
        var sb = new StringBuilder();
        var started = false;
        var breaks = 0;
        var previousDeeper = false;
        foreach (var line in collected)
        {
            if (IsBlank(line))
            {
                breaks++;
                continue;
            }

            var deeper = line[0] is ' ' or '\t';
            if (!started)
            {
                sb.Clear().Append(line);
                started = true;
            }
            else if (breaks > 0)
            {
                sb.Append('\n', breaks).Append(line);
            }
            else
            {
                sb.Append(deeper || previousDeeper ? '\n' : ' ').Append(line);
            }

            breaks = 0;
            previousDeeper = deeper;
        }

        return started ? sb.Append('\n', breaks).ToString() : "";
    }

    /// <summary>截断指示:<c>-</c> 去掉全部结尾换行,<c>+</c> 原样留着,缺省(clip)留一个。</summary>
    private static string ChompBlock(string text, char chomp)
    {
        if (IsBlank(text))
        {
            return "";
        }

        var content = text + "\n";
        return chomp switch
        {
            '+' => content,
            '-' => content.TrimEnd('\n'),
            _ => content.TrimEnd('\n') + "\n",
        };
    }

    /// <summary>削掉 C0 控制字符与 DEL,保留 <c>\n</c> / <c>\t</c> / <c>\r</c>(与会话层 <c>PromptText.StripControlChars</c> 同一个答案;Core 不引用会话层,所以这里写一份同样的规则)。</summary>
    private static string StripControlChars(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is (>= '\x00' and <= '\x08') or '\x0B' or '\x0C' or (>= '\x0E' and <= '\x1F') or '\x7F')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>削控制字符 + 限长。值会直接进系统提示词,这两步都不能省。</summary>
    private static string ClampValue(string v, string key, List<string> skipped)
    {
        var clean = StripControlChars(v);
        if (clean.Length <= FrontmatterLimits.ValueMax)
        {
            return clean;
        }

        skipped.Add($"\"{key}\": value exceeds {FrontmatterLimits.ValueMax / 1024}KB, truncated");
        return clean[..FrontmatterLimits.ValueMax];
    }

    // ─────────────────────────────────────────────────────────────
    // 序列化 —— Parse 的逆函数
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 需要加引号才能原样读回来的裸标量。每一条都对应 <see cref="Parse"/> 里一段真实的分支,不是照着 YAML 规范抄的:
    /// 空串(会变成块式序列的头);首尾有空白(解析时被削掉);<c>[</c> … <c>]</c>(被当成流式序列);<c>&amp;</c> / <c>*</c> 开头(锚点 / 别名);
    /// 块标量头的形状(会把后面的缩进行一起吃进来);<c>- </c> 开头(块式列表上下文里读成列表项);含换行或 tab;<c>#</c> 开头(整行当注释)。
    /// 值里含 <c>: </c> <b>不需要</b>加引号(解析取第一个冒号);<c>true</c> / <c>123</c> 也不需要(解析器只产出字符串,coerce 全在 <see cref="Bool"/> 里)。
    /// </summary>
    private static bool NeedsQuote(string v)
    {
        if (v.Length == 0)
        {
            return true;
        }

        if (JsTrim(v) != v)
        {
            return true;
        }

        if (v.Contains('\n') || v.Contains('\t'))
        {
            return true;
        }

        if (v.StartsWith('[') && v.EndsWith(']'))
        {
            return true;
        }

        if (v[0] is '&' or '*' or '#')
        {
            return true;
        }

        if (TryParseBlockHeader(v, out _, out _, out _))
        {
            return true;
        }

        return v.StartsWith("- ", StringComparison.Ordinal) || v == "-";
    }

    /// <summary>双引号里那一层转义 —— 和 <see cref="Unquote"/> 的双引号分支严格互为逆运算。</summary>
    private static string Quote(string v)
    {
        var sb = new StringBuilder(v.Length + 2).Append('"');
        foreach (var c in v)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string Scalar(string v) => NeedsQuote(v) ? Quote(v) : v;

    /// <summary>写一个键值对。<paramref name="pad"/> 是缩进 —— 嵌套子键和它的列表项都要跟着缩。</summary>
    private static void EmitEntry(string name, FrontmatterValue value, string pad, List<string> lines)
    {
        if (!value.IsList)
        {
            lines.Add($"{pad}{name}: {Scalar(value.Text)}");
            return;
        }

        var items = value.Items.Take(FrontmatterLimits.ListMax).ToList();
        if (items.Count == 0)
        {
            // `[]` 能原样读回一个空数组(ParseFlowSeq 把空串过滤掉了)。
            lines.Add($"{pad}{name}: []");
            return;
        }

        // 流式序列按逗号切,所以只要有一项含逗号(或方括号、或首尾空白),整列退块式。
        if (items.All(IsFlowSafe))
        {
            lines.Add($"{pad}{name}: [{string.Join(", ", items)}]");
            return;
        }

        lines.Add($"{pad}{name}:");
        foreach (var item in items)
        {
            lines.Add($"{pad}  - {Scalar(item)}");
        }
    }

    private static bool IsFlowSafe(string s) =>
        s.Length > 0
        && s.IndexOfAny([',', '[', ']', '\n', '\t']) < 0
        && JsTrim(s) == s;

    private static int IndexOfOrdinal(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 把一张扁平 map 写回 YAML 前置块 —— <see cref="Parse"/> 的<b>逆函数</b>。值为 <c>null</c> 的键跳过(不写成空值)。
    /// <para>
    /// 契约:<c>Parse(Serialize(data, body)).Data</c> 与 <c>data</c> 逐键相等。表单保存一次就丢一个字段,是那种当场看不出、三天后才发现的损坏。
    /// </para>
    /// <para>
    /// <b>只输出这个解析器读得回来的子集</b>:不产出块标量(多行值走双引号里的 <c>\n</c>)、含逗号的列表退回块式。
    /// 带一个点的键(<c>metadata.author</c>)写回成一层嵌套 map,和解析时的扁平化互逆 —— 不写回去就是真的丢(编辑器读一次写一次,<c>metadata:</c> 整块会在用户只改了 description 的那一次保存里消失)。
    /// 同名的标量与 map 无法同时表示,此时 map 赢。键名不合法、或超过两层的键直接不写。
    /// </para>
    /// <para>
    /// <b>正文紧贴着结束标记写,不额外空一行</b>:解析时 body 取结束标记的下一行起到结尾,若这里再补一个空行,而 body 又是上一次 parse 的产物,每存一次就多一个空行。
    /// 键序稳定是刻意的:每存一次就换一次顺序,git diff 会变成一片噪音。没有任何键时不写前置块(命令文件本来就允许没有 frontmatter)。
    /// </para>
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, FrontmatterValue?> data, string body, FrontmatterSerializeOptions? options = null)
    {
        var order = options?.Order ?? [];
        var named = new List<string>();
        foreach (var (k, v) in data)
        {
            if (v is null)
            {
                continue;
            }

            var parts = k.Split('.');
            if (parts.Length <= 2 && parts.All(p => IsValidKey(p) && !IsForbiddenKey(p)))
            {
                named.Add(k);
            }
        }

        // 同名的标量与 map 不能同时写出去:`metadata: x` 后面再跟一个 `metadata:` 块,读回来只会剩一个。map 赢 —— 标量那半是占位,子键才是内容。
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in named)
        {
            var dot = k.IndexOf('.');
            if (dot >= 0)
            {
                parents.Add(k[..dot]);
            }
        }

        // 稳定排序(OrderBy):order 里的键在前且按 order 的顺序,其余按字母序(2.x 的 localeCompare,这里用 InvariantCulture:不随机器区域变)。
        var keys = named
            .Where(k => k.Contains('.') || !parents.Contains(k))
            .Order(Comparer<string>.Create((a, b) =>
            {
                var ia = IndexOfOrdinal(order, a);
                var ib = IndexOfOrdinal(order, b);
                if (ia != -1 && ib != -1)
                {
                    return ia - ib;
                }

                if (ia != -1)
                {
                    return -1;
                }

                if (ib != -1)
                {
                    return 1;
                }

                return string.Compare(a, b, StringComparison.InvariantCulture);
            }))
            .ToList();

        var lines = new List<string>();
        var written = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var key in keys)
        {
            if (count >= FrontmatterLimits.KeysMax)
            {
                break;
            }

            var dot = key.IndexOf('.');
            if (dot < 0)
            {
                EmitEntry(key, data[key]!.Value, "", lines);
                count++;
                continue;
            }

            // 子键在排序后天然连成一片(都带同一个 `parent.` 前缀),整组在第一个子键的位置一次写完 —— 否则父键会被写成两个 `metadata:` 块。
            var parent = key[..dot];
            if (!written.Add(parent))
            {
                continue;
            }

            lines.Add($"{parent}:");
            foreach (var child in keys)
            {
                if (count >= FrontmatterLimits.KeysMax)
                {
                    break;
                }

                if (!child.StartsWith(parent + ".", StringComparison.Ordinal))
                {
                    continue;
                }

                EmitEntry(child[(parent.Length + 1)..], data[child]!.Value, "  ", lines);
                count++;
            }
        }

        var normalizedBody = NormalizeNewlines(body);
        // 没有任何键时不写前置块 —— 命令文件本来就允许没有 frontmatter。
        if (lines.Count == 0)
        {
            return normalizedBody;
        }

        return $"---\n{string.Join('\n', lines)}\n---\n{normalizedBody}";
    }

    /// <summary>便捷重载:直接喂 <see cref="Frontmatter.Data"/>(编辑器读一次写一次)。</summary>
    public static string Serialize(IReadOnlyDictionary<string, FrontmatterValue> data, string body, FrontmatterSerializeOptions? options = null)
    {
        var copy = new Dictionary<string, FrontmatterValue?>(data.Count, StringComparer.Ordinal);
        foreach (var (k, v) in data)
        {
            copy[k] = v;
        }

        return Serialize(copy, body, options);
    }
}
