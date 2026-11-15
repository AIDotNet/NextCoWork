using System.Text.Json;

namespace Ncw.Agent.Extensions.Permissions;

/// <summary><c>Tool</c> 或 <c>Tool(specifier)</c>。<see cref="Tool"/> 是工具的 <c>InternalId</c>(外部名会被注册表截断去重,跨会话不稳定)。</summary>
public sealed record PermissionRule(string Tool, string? Specifier = null);

/// <summary>
/// 一次调用拿去和 specifier 比对的<b>对象</b>。<see cref="Strict"/> 只含「已经规范化」的写法(路径字段是规范化后的相对 / 绝对路径),用于 allow 匹配;
/// <see cref="Loose"/> 是 <see cref="Strict"/> 再加上模型原样给的写法(Bash 还有拆开的各个子命令),用于 deny / ask 匹配。
/// 两张名单取不同的一边是同一个方向:放行只认「确切规范的东西」,拒绝和强制询问宁可多命中。
/// </summary>
public sealed record RuleSubjects(IReadOnlyList<string> Strict, IReadOnlyList<string> Loose)
{
    public static RuleSubjects Empty { get; } = new([], []);
}

/// <summary>
/// 「以后都允许」落成的那条<b>规则</b>(2.x <c>shared/agent/permission-rule.ts</c>)。语法沿用 Claude Code:
/// <c>Bash</c> / <c>Bash(git status:*)</c> / <c>Read(/Users/me/a.ts)</c> / <c>Write(src/*.ts)</c> / <c>WebFetch(domain:example.com)</c>。
/// 这里只有<b>匹配</b>,没有 IO,也没有档位判断;谁比谁优先见 <see cref="PermissionDecisions"/>。
/// </summary>
public static class PermissionRules
{
    public const int RuleMax = 512;

    private const int ToolMax = 128;

    /// <summary>
    /// 前缀授权(<c>xxx:*</c>)不能被接续符扩写成第二条命令。这是这套语法唯一真正危险的地方:<c>Bash(git status:*)</c> 字面上也匹配 <c>git status &amp;&amp; rm -rf /</c>。
    /// 前缀命中之后,<b>剩下那截</b>里出现任何一个接续 / 重定向符号就整条不算命中,退回人工审批。宁可多问一次,也不要把一条窄授权悄悄变成通行证。
    /// </summary>
    private static readonly char[] ShellEscapeChars = [';', '&', '|', '`', '\n', '\r', '<', '>'];

    private static bool HasShellEscape(ReadOnlySpan<char> text) => text.IndexOfAny(ShellEscapeChars) >= 0 || text.Contains("$(", StringComparison.Ordinal);

    private static bool IsToolName(ReadOnlySpan<char> tool)
    {
        if (tool.Length is 0 or > ToolMax)
        {
            return false;
        }

        foreach (var c in tool)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static PermissionRule? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > RuleMax)
        {
            return null;
        }

        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return IsToolName(trimmed) ? new PermissionRule(trimmed) : null;
        }

        if (!trimmed.EndsWith(')'))
        {
            return null;
        }

        var tool = trimmed[..open].Trim();
        var specifier = trimmed[(open + 1)..^1].Trim();
        return IsToolName(tool) && specifier.Length > 0 ? new PermissionRule(tool, specifier) : null;
    }

    public static string Format(PermissionRule rule) => rule.Specifier is null ? rule.Tool : $"{rule.Tool}({rule.Specifier})";

    public static bool IsValid(string? text) => Parse(text) is not null;

    /// <summary>
    /// 2.x 的比对对象:<c>command / file_path / path / pattern / query / url</c>(外加 url 的 <c>domain:&lt;host&gt;</c>)。只认这几个约定字段,
    /// 不做「把整个 input 拍平去比」——后者会让一条 <c>Write(README.md)</c> 因为某个无关字段里恰好出现同样的字符串而命中。
    /// 认不出来的工具(多数 MCP 工具)只剩下裸 <c>ToolName</c> 规则可用——那是保守的一侧。这是未规范化的原始写法。
    /// </summary>
    public static IReadOnlyList<string> Subjects(JsonElement input)
    {
        var result = new List<string>();
        if (input.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        void Push(string name)
        {
            if (input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text)
            {
                result.Add(text);
            }
        }

        Push("command");
        Push("file_path");
        Push("path");
        Push("pattern");
        Push("query");
        if (input.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { } raw)
        {
            Push("url");
            if (HostOf(raw) is { Length: > 0 } host)
            {
                result.Add($"domain:{host}");
            }
        }

        return result;
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    /// <summary>
    /// 线性通配匹配(<c>*</c> 匹配任意字符,包括 <c>/</c>)。刻意不编译成正则:<c>*a*a*a*a*</c> 这类模式在回溯正则上是指数级的,
    /// 而这些模式来自一个可以被手改、也可能随仓库被别人改的文件。这个两指针版本最坏 O(n·m)。
    /// </summary>
    public static bool WildcardMatch(string pattern, string text)
    {
        var p = 0;
        var t = 0;
        var star = -1;
        var mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool MatchSpecifier(string specifier, string subject, string tool)
    {
        if (specifier.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = specifier[..^2];
            if (!subject.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (tool != "Bash")
            {
                return true;
            }

            // 3.0 收紧:Bash 的前缀要落在词边界上(`git:*` 不该匹配 `gitk`、`ls:*` 不该匹配 `lsof`)。2.x 是裸 startsWith。
            var rest = subject.AsSpan(prefix.Length);
            return (rest.Length == 0 || char.IsWhiteSpace(rest[0]) || prefix.Length == 0) && !HasShellEscape(rest);
        }

        return specifier.Contains('*', StringComparison.Ordinal) ? WildcardMatch(specifier, subject) : specifier == subject;
    }

    /// <summary>比对一条规则。<paramref name="subjects"/> 是调用方按 allow / deny 的口径选好的那一边(见 <see cref="RuleSubjects"/>)。</summary>
    public static bool Matches(string ruleText, string tool, IReadOnlyList<string> subjects)
    {
        var rule = Parse(ruleText);
        if (rule is null || rule.Tool != tool)
        {
            return false;
        }

        if (rule.Specifier is null)
        {
            return true;
        }

        foreach (var subject in subjects)
        {
            if (MatchSpecifier(rule.Specifier, subject, tool))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Matches(string ruleText, string tool, JsonElement input) => Matches(ruleText, tool, Subjects(input));

    /// <summary>命中的第一条规则原文——返回文本而不是布尔,是为了能告诉用户「是哪一条放行 / 拦下的」。</summary>
    public static string? MatchFirst(IEnumerable<string> rules, string tool, IReadOnlyList<string> subjects)
    {
        foreach (var rule in rules)
        {
            if (Matches(rule, tool, subjects))
            {
                return rule;
            }
        }

        return null;
    }

    public static string? MatchFirst(IEnumerable<string> rules, string tool, JsonElement input) => MatchFirst(rules, tool, Subjects(input));

    /// <summary>
    /// 这些命令的第二个词是子命令,把它带进规则里才有意义:<c>Bash(git:*)</c> 等于把整个 git 交出去,<c>Bash(git status:*)</c> 才是用户以为自己点的那个。
    /// </summary>
    private static readonly HashSet<string> BashDrivers = new(StringComparer.Ordinal)
    {
        "bun", "bunx", "cargo", "docker", "dotnet", "gh", "git", "go", "gradle", "kubectl", "make", "mvn",
        "node", "npm", "npx", "pip", "pip3", "pnpm", "poetry", "python", "python3", "terraform", "uv", "yarn",
    };

    private static bool IsHeadToken(string head)
    {
        if (head.Length == 0)
        {
            return false;
        }

        foreach (var c in head)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '/' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSubcommandToken(string token)
    {
        if (token.Length == 0 || token[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or ':' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string BashSpecifier(string command)
    {
        // 带接续符的命令不给前缀授权:前缀只覆盖第一段,后面那几段等于没被审过。
        if (HasShellEscape(command))
        {
            return command;
        }

        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !IsHeadToken(tokens[0]))
        {
            return command;
        }

        var head = tokens[0];
        var slash = head.LastIndexOf('/');
        var basename = slash >= 0 ? head[(slash + 1)..] : head;
        return BashDrivers.Contains(basename) && tokens.Length > 1 && IsSubcommandToken(tokens[1])
            ? $"{head} {tokens[1]}:*"
            : $"{head}:*";
    }

    /// <summary>
    /// 给「以后都允许」按钮用的<b>建议规则</b>。由 Agent 算,不由控制端拼——控制端送什么就写什么的话,这颗按钮就是一个「往权限文件里写任意一行」的接口。
    /// <paramref name="exactBash"/>:Bash 命令只授权这一条原样命令、不给前缀(危险命令用,见 <see cref="BashCommand"/>)。
    /// </summary>
    public static string Suggest(string tool, IReadOnlyList<string> subjects, bool exactBash = false)
    {
        bool Fits(string specifier) => tool.Length + specifier.Length + 2 <= RuleMax;
        if (tool == "Bash")
        {
            if (subjects.Count == 0)
            {
                return tool;
            }

            var specifier = exactBash ? subjects[0] : BashSpecifier(subjects[0]);
            return Fits(specifier) ? $"{tool}({specifier})" : tool;
        }

        var chosen = subjects.FirstOrDefault(static s => s.StartsWith("domain:", StringComparison.Ordinal)) ?? subjects.FirstOrDefault();
        return chosen is null || !Fits(chosen) ? tool : $"{tool}({chosen})";
    }

    public static string Suggest(string tool, JsonElement input) => Suggest(tool, Subjects(input));
}
