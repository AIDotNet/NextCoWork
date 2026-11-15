using System.Text.RegularExpressions;

namespace Ncw.Agent.Tools;

/// <summary>
/// Grep 的<b>整文件预筛</b>(移植自 2.x <c>grep-prefilter.ts</c>):从正则里抽出一段「任何命中都必须包含」的字面量,先拿它在整篇文本上查一次,
/// 查不到的文件整个跳过,不再逐行跑正则。绝大多数搜索是在找一个标识符,而一个仓库里 99% 的文件根本不含它。
/// <para>
/// 不变式:<b>只许误报,不许漏报</b>。抽出来的字面量必须是「所有匹配的必经之路」,抽错一个字符,文件就被静默跳过,模型拿到的是「仓库里没有」。
/// 所以一律取保守:看不懂的语法(分组、交替、字符类、<c>\w</c> 这类转义、断言)全部当成「断开」,宁可抽不出(退回逐行扫描,只是慢)也不猜。
/// </para>
/// </summary>
internal static class GrepPrefilter
{
    /// <summary>抽出来的字面量短于这个就不用:一两个字符几乎每个文件都有,预筛只是白查一遍。</summary>
    private const int MinLiteralChars = 3;

    private static int SkipCharClass(string p, int open)
    {
        var i = open + 1;
        if (i < p.Length && p[i] == '^')
        {
            i++;
        }

        if (i < p.Length && p[i] == ']')
        {
            i++;
        }

        for (; i < p.Length; i++)
        {
            if (p[i] == '\\')
            {
                i++;
                continue;
            }

            if (p[i] == ']')
            {
                return i;
            }
        }

        return p.Length;
    }

    private static int SkipGroup(string p, int open)
    {
        var depth = 0;
        for (var i = open; i < p.Length; i++)
        {
            var ch = p[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '[')
            {
                i = SkipCharClass(p, i);
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')' && --depth == 0)
            {
                return i;
            }
        }

        return p.Length;
    }

    /// <summary><paramref name="i"/> 处的量词。<c>Length</c> = 0 表示不是量词;<c>Optional</c> 表示允许零次(<c>?</c> <c>*</c> <c>{0,…}</c>)。不成形的 <c>{</c> 是字面量。</summary>
    private static (int Length, bool Optional) QuantifierAt(string p, int i)
    {
        var ch = p[i];
        int length;
        bool optional;
        switch (ch)
        {
            case '*' or '?':
                length = 1;
                optional = true;
                break;
            case '+':
                length = 1;
                optional = false;
                break;
            case '{':
            {
                var close = p.IndexOf('}', i);
                if (close < 0)
                {
                    return (0, false);
                }

                var body = p.AsSpan(i + 1, close - i - 1);
                var comma = body.IndexOf(',');
                var first = comma < 0 ? body : body[..comma];
                var second = comma < 0 ? default : body[(comma + 1)..];
                if (first.IsEmpty || !AllDigits(first) || (comma >= 0 && !second.IsEmpty && !AllDigits(second)))
                {
                    return (0, false);
                }

                length = close - i + 1;
                optional = int.Parse(first, System.Globalization.CultureInfo.InvariantCulture) == 0;
                break;
            }

            default:
                return (0, false);
        }

        // 惰性修饰 *? / +? 只改匹配偏好,不改「必须出现」与否
        if (i + length < p.Length && p[i + length] == '?')
        {
            length++;
        }

        return (length, optional);
    }

    private static bool AllDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>抽出正则里一段<b>必然出现</b>的字面量;抽不出就返回 <c>null</c>。只看顶层的连续字面量,遇到任何非字面量的东西就断开,取最长的一段;顶层有交替就放弃。</summary>
    public static string? RequiredLiteral(string pattern)
    {
        var best = "";
        var run = new System.Text.StringBuilder();
        var lastWasLiteral = false;

        void Flush()
        {
            if (run.Length > best.Length)
            {
                best = run.ToString();
            }

            run.Clear();
            lastWasLiteral = false;
        }

        void PushLiteral(char c)
        {
            run.Append(c);
            lastWasLiteral = true;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            var (qLength, qOptional) = QuantifierAt(pattern, i);
            if (qLength > 0)
            {
                // 可选的量词作用在上一个字面量上 → 那个字符不是必经的
                if (lastWasLiteral && qOptional && run.Length > 0)
                {
                    run.Length--;
                }

                // 不管哪种量词,它后面的字符都不再和前面相邻(ab+c 不保证出现 abc)
                Flush();
                i += qLength - 1;
                continue;
            }

            switch (ch)
            {
                case '|':
                    return null;
                case '\\':
                {
                    i++;
                    // 非字母数字的转义(\. \( \/)就是那个字符本身;\w \d \b \n \1 \u… 之类一律当成断开
                    if (i < pattern.Length && !char.IsAsciiLetterOrDigit(pattern[i]))
                    {
                        PushLiteral(pattern[i]);
                    }
                    else
                    {
                        Flush();
                    }

                    break;
                }

                case '[':
                    Flush();
                    i = SkipCharClass(pattern, i);
                    break;
                case '(':
                    Flush();
                    i = SkipGroup(pattern, i);
                    break;
                case '.' or '^' or '$':
                    Flush();
                    break;
                default:
                    PushLiteral(ch);
                    break;
            }
        }

        Flush();
        return best.Length >= MinLiteralChars ? best : null;
    }

    /// <summary>把字面量变成「这篇文本可能命中吗」的判定。<c>-i</c> 时用同样带 IgnoreCase 的纯字面量正则,折叠规则和主正则一致。</summary>
    public static Func<string, bool>? Create(string pattern, bool ignoreCase)
    {
        var literal = RequiredLiteral(pattern);
        if (literal is null)
        {
            return null;
        }

        if (!ignoreCase)
        {
            return text => text.Contains(literal, StringComparison.Ordinal);
        }

        var re = new Regex(Regex.Escape(literal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text => re.IsMatch(text);
    }
}
