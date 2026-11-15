using System.Text.RegularExpressions;

namespace Ncw.Agent.Tools;

/// <summary>
/// 灾难性回溯的<b>静态筛查</b>(移植自 2.x <c>redos.ts</c>):只认一个家族 —— 一个被无界量词修饰的分组,它的内部还有另一个无界量词
/// (<c>(a+)+</c>、<c>(\s*\w+)*</c>、<c>(a{2,})+</c>),这种嵌套让同一段输入有指数级多种切分方式,匹配失败时全部要试一遍。
/// 刻意不管交替(<c>(a|aa)+</c>):静态判不出分支能否匹配同一串,这类写法交给匹配超时兜底。
/// <para>
/// 3.0 里它只挡「必须用回溯引擎」的那条路:托管引擎优先用 <c>RegexOptions.NonBacktracking</c>(线性时间,根本没有回溯问题);
/// 只有模式用了它不支持的语法(环视、反向引用)才退到回溯引擎,那时才先过这道闸,再带上匹配超时。
/// </para>
/// 误报的代价是可见且可操作的(模型收到一条说明怎么改写的错误,重试一次就行),所以宁可紧一点。
/// </summary>
internal static class Redos
{
    private static int SkipCharClass(string p, int open)
    {
        // `[]a]` 里第一个 `]` 是字面量,`[^]a]` 同理
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

    /// <summary><paramref name="i"/> 处是不是一个<b>无界</b>量词。返回它的字符长度,不是就返回 0。<c>{2,}</c> 无界;<c>{2}</c> / <c>{2,5}</c> 有界(工作量有上限)。</summary>
    private static int UnboundedQuantifierAt(string p, int i)
    {
        if (i >= p.Length)
        {
            return 0;
        }

        if (p[i] is '*' or '+')
        {
            return 1;
        }

        if (p[i] != '{')
        {
            return 0;
        }

        var close = p.IndexOf('}', i);
        if (close < 0)
        {
            return 0;
        }

        var body = p.AsSpan(i + 1, close - i - 1);
        var comma = body.IndexOf(',');
        if (comma <= 0 || !IsDigits(body[..comma]) || !body[(comma + 1)..].Trim().IsEmpty)
        {
            return 0;
        }

        return close - i + 1;
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsUnbounded(string body)
    {
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '[')
            {
                i = SkipCharClass(body, i);
                continue;
            }

            if (UnboundedQuantifierAt(body, i) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>有风险就返回一段<b>给模型看的</b>说明(必须包含「改成什么」,否则模型会原样重试),没有就返回 <c>null</c>。</summary>
    public static string? Risk(string pattern)
    {
        var stack = new Stack<int>();
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '[')
            {
                i = SkipCharClass(pattern, i);
                continue;
            }

            if (ch == '(')
            {
                stack.Push(i);
                continue;
            }

            if (ch != ')' || !stack.TryPop(out var start) || UnboundedQuantifierAt(pattern, i + 1) == 0)
            {
                continue;
            }

            var body = pattern[(start + 1)..i];
            if (!ContainsUnbounded(body))
            {
                continue;
            }

            return "This pattern risks catastrophic backtracking and was refused (it could freeze the search for minutes). "
                + $"The problem is the group \"({body})\": it contains an unbounded quantifier (*, +, or {{n,}}) and is itself wrapped in another unbounded quantifier, "
                + "so a failed match has to try exponentially many splits. "
                + "Fix it by dropping one of the two quantifiers (write (a+)+ as just a+), by bounding the inner one (e.g. {1,20}), or by adding a more specific literal to narrow the search.";
        }

        return null;
    }

    /// <summary>把模型给的正则编成托管 <see cref="Regex"/>:优先线性时间引擎;用了它不支持的语法才退到带超时的回溯引擎(此时先过静态筛查)。</summary>
    public static Regex Compile(string pattern, RegexOptions options, TimeSpan timeout)
    {
        options |= RegexOptions.CultureInvariant;
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking, timeout);
        }
        catch (NotSupportedException)
        {
            // 环视、反向引用、原子组等:只能回溯
        }

        if (Risk(pattern) is { } risk)
        {
            throw new ToolFailureException(risk);
        }

        return new Regex(pattern, options, timeout);
    }
}
