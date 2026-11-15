using System.Globalization;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Services.Composer;

/// <summary>
/// 输入框补全(灰字 + Tab 接受)的纯文本规则(2.x 的 <c>shared/domain/composer-completion.ts</c> + <c>prompt-suggestion.ts</c> 的解析部分)。
/// ★ 同一条规则在<b>两头</b>各判一次:控制端决定「要不要发请求」(例如草稿像带着密钥就不发),设备决定「模型回来的这句能不能给人看」;两边各写一份迟早出现互相矛盾的口径,所以只有这一份。
/// ★ 一切长度都按<b>字素</b>数,不按空格切词也不按 UTF-16 码元:中文没有空格,emoji 一个字素占两三个码元。
/// </summary>
public static partial class ComposerText
{
    /// <summary>建议本身的上限(字素)。60 已经是一句很长的话了,再长就不是「顺手接一句」。</summary>
    public const int SuggestionLimit = 60;

    private static readonly string[] AgentVoice =
    [
        "好的", "好呀", "当然", "以下是", "下面是", "我来", "我将", "我会", "让我", "没问题", "收到",
        "sure", "here's", "here is", "i'll", "i will", "let me", "certainly", "of course", "okay,", "ok,",
    ];

    private static readonly string[] Evaluations =
    [
        "不错", "很好", "太好了", "完美", "谢谢", "感谢", "辛苦了", "好的谢谢",
        "thanks", "thank you", "looks good", "lgtm", "great", "perfect", "nice", "awesome",
    ];

    public static IReadOnlyList<string> Graphemes(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            list.Add((string)e.Current);
        }

        return list;
    }

    public static int GraphemeCount(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>取前 <paramref name="max"/> 个字素(不会把一个 emoji 或组合字符切成两半)。</summary>
    public static string TakeGraphemes(string text, int max) => string.Concat(Graphemes(text).Take(max));

    /// <summary>取后 <paramref name="max"/> 个字素。</summary>
    public static string LastGraphemes(string text, int max)
    {
        var all = Graphemes(text);
        return all.Count <= max ? text : string.Concat(all.Skip(all.Count - max));
    }

    /// <summary>「回答腔」开头:模型没有续写用户的话,而是开始<b>回答</b>它了。这是这个功能最大的失败形态,拦下来的建议一个字都不给人看。</summary>
    public static bool StartsWithAgentVoice(string text)
    {
        var head = text.TrimStart().ToLowerInvariant();
        return AgentVoice.Any(p => head.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>评价语(「不错」「谢谢」「looks good」):用户不会把这类话当成下一条指令打出来。</summary>
    public static bool IsEvaluation(string text)
    {
        var t = TrailingPunctuation().Replace(text.Trim().ToLowerInvariant(), "");
        return Evaluations.Any(e => t == e || t.StartsWith($"{e},", StringComparison.Ordinal) || t.StartsWith($"{e}，", StringComparison.Ordinal));
    }

    /// <summary>含 markdown 语法。一条要填进输入框的建议不该带格式。★ 只认语法标记,不认普通标点:<c>fix the bug - then test</c> 里的连字符是正常文字。</summary>
    public static bool HasMarkdownSyntax(string text) => Markdown().IsMatch(text);

    /// <summary>
    /// 草稿看起来像带着密钥:带着就<b>不发</b>给补全模型。★ 宁可漏判不可滥判(滥判的代价是「这一句没有补全」,规则太宽会让整类正常输入永远没有补全);
    /// 所以不认 40 位十六进制——那是 git 提交哈希的样子,在编码代理的输入框里比密钥常见得多。
    /// </summary>
    public static bool LooksSecret(string text) => Secrets().IsMatch(text);

    /// <summary>把摘录里的 <c>&lt;/tag</c> 改写成 <c>&lt;\/tag</c>,免得摘录在提示词里<b>闭合我们自己的包装标签</b>(摘录是不可信输入)。</summary>
    public static string NeutralizeClosingTags(string text, IEnumerable<string> tags)
    {
        var output = text;
        foreach (var tag in tags)
        {
            output = output.Replace($"</{tag}", $"<\\/{tag}", StringComparison.Ordinal);
        }

        return output;
    }

    /// <summary>取 <c>&lt;tag&gt;…&lt;/tag&gt;</c> 的内容:有开有闭取中间;只有开标签取开标签之后的全部(认停止词的供应商会把闭标签吞掉);一个开标签都没有整段当内容。</summary>
    public static string ExtractTagged(string raw, string tag)
    {
        var open = $"<{tag}>";
        var start = raw.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return raw;
        }

        var body = raw[(start + open.Length)..];
        var end = body.IndexOf($"</{tag}>", StringComparison.Ordinal);
        return end < 0 ? body : body[..end];
    }

    /// <summary>削控制字符(换行 / 制表保留)。</summary>
    public static string StripControl(string text) => new(text.Where(static c => c is '\n' or '\t' or '\r' || !char.IsControl(c)).ToArray());

    /// <summary>
    /// 模型回答 → 一句能直接画成灰字的建议;空串 = 沉默。★ 每一条「返回空」都对应一种已知的失败形态:问句、回答腔、评价语、markdown、过长、斜杠命令。
    /// </summary>
    public static string ParseSuggestion(string raw)
    {
        var body = ExtractTagged(raw, "suggestion");
        var line = StripControl(body).Replace("\r", "", StringComparison.Ordinal).Split('\n').FirstOrDefault(static l => l.Trim().Length > 0) ?? "";
        var text = Quotes().Replace(line.Trim(), "").Trim();
        if (text.Length == 0 || text.EndsWith('?') || text.EndsWith('？') || text.StartsWith('/') || text.Contains("<suggestion", StringComparison.Ordinal) || text.Contains("</", StringComparison.Ordinal))
        {
            return "";
        }

        if (StartsWithAgentVoice(text) || IsEvaluation(text) || HasMarkdownSyntax(text) || GraphemeCount(text) > SuggestionLimit)
        {
            return "";
        }

        return text;
    }

    [GeneratedRegex("[!！。.~～]+$")]
    private static partial Regex TrailingPunctuation();

    [GeneratedRegex(@"\*\*|`|^\s*#{1,6}\s|^\s*[-*+]\s|^\s*\d+\.\s|\[[^\]]*\]\([^)]*\)|</?[a-z][\w-]*>", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Markdown();

    [GeneratedRegex("^[\"'“”‘’`「」『』]+|[\"'“”‘’`「」『』]+$")]
    private static partial Regex Quotes();

    [GeneratedRegex(
        @"\bsk-[A-Za-z0-9_-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bAKIA[0-9A-Z]{16}\b|\bxox[baprs]-[A-Za-z0-9-]{10,}|\bAIza[0-9A-Za-z_-]{30,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\s*[=:]\s*\S{4,}|(?:密码|口令|密钥)\s*[=:：是为]\s*\S{4,}",
        RegexOptions.IgnoreCase)]
    private static partial Regex Secrets();
}
