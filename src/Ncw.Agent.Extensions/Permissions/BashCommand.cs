using System.Text;
using System.Text.RegularExpressions;
using Ncw.Agent.Extensions.Interactions;

namespace Ncw.Agent.Extensions.Permissions;

/// <summary>
/// Bash 命令的拆分与分级。<b>2.x 没有这一层</b>(Bash 恒为「非只读、破坏性」,规则只比整条命令字符串),这是 3.0 的收紧:
/// <list type="bullet">
/// <item><see cref="Segments"/>:把 <c>a; b &amp;&amp; c | d</c>、<c>$(…)</c>、反引号、<c>bash -c '…'</c>、<c>find -exec …</c>、<c>sudo / env / VAR=x</c> 前缀拆开,
/// deny / ask 规则对每一段都比一遍——否则 <c>Bash(rm:*)</c> 这条 deny 只要在前面垫一句 <c>echo hi;</c> 就绕过去了。同时给出「引号感知」和「无视引号」两种拆法的并集(宁可多命中)。</item>
/// <item><see cref="Classify"/>:<see cref="CommandRisk"/>。</item>
/// </list>
/// 这里不求完备的 shell 解析(那是不可能的),方向永远是<b>保守</b>:拆出更多的段只会让 deny / ask 更容易命中;只读白名单很窄,拿不准就是 <see cref="CommandRisk.Unknown"/>。
/// </summary>
public static partial class BashCommand
{
    private const int MaxDepth = 4;

    private const int MaxSegments = 64;

    // ───────────────────────── 拆分 ─────────────────────────

    /// <summary>整条命令之外的所有子命令(去重、保持出现顺序,第一项恒为整条命令的去空白原文)。命令为空白返回空列表。</summary>
    public static IReadOnlyList<string> Segments(string command)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(command, result, seen, 0);
        return result;
    }

    private static void Add(string text, List<string> result, HashSet<string> seen)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0 && result.Count < MaxSegments && seen.Add(trimmed))
        {
            result.Add(trimmed);
        }
    }

    private static void Collect(string command, List<string> result, HashSet<string> seen, int depth)
    {
        Add(command, result, seen);
        if (depth >= MaxDepth)
        {
            return;
        }

        var pieces = new List<string>();
        var nested = new List<string>();
        Split(command, quoteAware: true, pieces, nested);
        Split(command, quoteAware: false, pieces, nested);
        foreach (var piece in pieces)
        {
            Add(piece, result, seen);
            foreach (var variant in Unwrap(piece, depth))
            {
                Add(variant, result, seen);
                if (variant != piece)
                {
                    Collect(variant, result, seen, depth + 1);
                }
            }
        }

        foreach (var inner in nested)
        {
            Collect(inner, result, seen, depth + 1);
        }
    }

    /// <summary>
    /// 按 <c>; &amp; | 换行</c> 切开;顺带收集 <c>$(…)</c> / <c>&lt;(…)</c> / <c>&gt;(…)</c> / 反引号里的内层命令。
    /// <paramref name="quoteAware"/> 为假时不理会引号(把引号里的分隔符也当成分隔符),作为引号解析出错时的保守补充。
    /// </summary>
    private static void Split(string text, bool quoteAware, List<string> pieces, List<string> nested)
    {
        var current = new StringBuilder();
        var single = false;
        var @double = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoteAware && c == '\\' && !single && i + 1 < text.Length)
            {
                current.Append(c).Append(text[++i]);
                continue;
            }

            if (quoteAware && c == '\'' && !@double)
            {
                single = !single;
                current.Append(c);
                continue;
            }

            if (quoteAware && c == '"' && !single)
            {
                @double = !@double;
                current.Append(c);
                continue;
            }

            if (!single)
            {
                if (c == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        nested.Add(text[(i + 1)..end]);
                        current.Append(text, i, end - i + 1);
                        i = end;
                        continue;
                    }
                }

                if (i + 1 < text.Length && text[i + 1] == '(' && (c == '$' || c == '<' || c == '>'))
                {
                    var end = MatchingParen(text, i + 1);
                    nested.Add(text[(i + 2)..end]);
                    current.Append(text, i, Math.Min(end + 1, text.Length) - i);
                    i = end;
                    continue;
                }
            }

            if (!(quoteAware && (single || @double)) && c is ';' or '&' or '|' or '\n' or '\r')
            {
                pieces.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        pieces.Add(current.ToString());
    }

    /// <summary>从 <c>(</c> 的位置找配对的 <c>)</c>;没有配对就取到末尾(宁可多拆)。返回 <c>)</c> 的下标(或 text.Length)。</summary>
    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return text.Length;
    }

    // ───────────────────────── 词法与包装 ─────────────────────────

    /// <summary>粗略的 shell 分词:去引号、处理反斜杠。不展开任何东西。</summary>
    internal static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var has = false;
        var single = false;
        var @double = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '\\' && !single && i + 1 < segment.Length)
            {
                current.Append(segment[++i]);
                has = true;
                continue;
            }

            if (c == '\'' && !@double)
            {
                single = !single;
                has = true;
                continue;
            }

            if (c == '"' && !single)
            {
                @double = !@double;
                has = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !single && !@double)
            {
                if (has)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    has = false;
                }

                continue;
            }

            current.Append(c);
            has = true;
        }

        if (has)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "env", "command", "builtin", "exec", "nohup", "time", "nice", "ionice", "stdbuf", "timeout", "xargs", "setsid", "caffeinate",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh", "fish", "ash" };

    private static bool IsEnvAssignment(string token)
    {
        var eq = token.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return false;
        }

        for (var i = 0; i < eq; i++)
        {
            var c = token[i];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' || (i > 0 && c is >= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private static string BaseName(string token)
    {
        var slash = token.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? token[(slash + 1)..] : token;
    }

    /// <summary>
    /// 去掉开头的 <c>VAR=x</c> 与包装命令(<c>sudo env nohup …</c>)得到真正执行的命令;<c>sh -c 'script'</c> 的 script、<c>eval …</c>、<c>find … -exec cmd …</c> 的 cmd 另算一段。
    /// 返回的各项都是「可以再拿去比 deny 规则的命令文本」。
    /// </summary>
    private static List<string> Unwrap(string segment, int depth)
    {
        var variants = new List<string>();
        var tokens = Tokenize(segment);
        var start = 0;
        for (var guard = 0; guard < 8 && start < tokens.Count; guard++)
        {
            if (IsEnvAssignment(tokens[start]))
            {
                start++;
            }
            else if (Wrappers.Contains(BaseName(tokens[start])))
            {
                start++;
                while (start < tokens.Count && tokens[start].StartsWith('-') && tokens[start].Length > 1)
                {
                    start++;
                }
            }
            else
            {
                break;
            }
        }

        if (start >= tokens.Count)
        {
            return variants;
        }

        if (start > 0)
        {
            variants.Add(string.Join(' ', tokens.Skip(start).Select(Requote)));
        }

        var head = BaseName(tokens[start]);
        if (Shells.Contains(head))
        {
            for (var i = start + 1; i < tokens.Count - 1; i++)
            {
                var flag = tokens[i];
                if (flag.StartsWith('-') && !flag.StartsWith("--", StringComparison.Ordinal) && flag.Contains('c'))
                {
                    variants.Add(tokens[i + 1]);
                    break;
                }
            }
        }
        else if (head == "eval" && tokens.Count > start + 1)
        {
            variants.Add(string.Join(' ', tokens.Skip(start + 1)));
        }
        else if (head is "find" or "fd")
        {
            for (var i = start + 1; i < tokens.Count; i++)
            {
                if (tokens[i] is "-exec" or "-execdir" or "-ok" or "-okdir" or "-x" or "--exec")
                {
                    var inner = tokens.Skip(i + 1).TakeWhile(static t => t is not ";" and not "+").Select(Requote);
                    variants.Add(string.Join(' ', inner));
                    break;
                }
            }
        }

        return variants;
    }

    private static string Requote(string token) => token.Length == 0 || token.Any(char.IsWhiteSpace) ? $"'{token}'" : token;

    // ───────────────────────── 分级 ─────────────────────────

    [GeneratedRegex(@"\b(?:curl|wget|fetch)\b[^|;&\n]*\|\s*(?:sudo\s+)?(?:-\S+\s+)*(?:/\S*/)?(?:ba|z|da|k|a)?sh\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex PipeToShell();

    [GeneratedRegex(@">\s*/dev/(?:sd|nvme|disk|hd|mmcblk|xvd)|>>?\s*/etc/|>\s*/boot/", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex WritesSystemPath();

    private static readonly HashSet<string> DangerousPrograms = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "su", "pkexec", "mkfs", "fdisk", "parted", "wipefs", "shred", "shutdown", "reboot", "halt", "poweroff", "diskutil", "format",
    };

    private static readonly HashSet<string> RootLikeTargets = new(StringComparer.Ordinal)
    {
        "/", "/*", "~", "~/", "~/*", "$HOME", "${HOME}", "$HOME/", "*", ".", "..", "./", "../", "./*", ".*", "/home", "/usr", "/etc", "/var", "/bin", "/lib", "/System", "/Users", "C:\\", "C:/",
    };

    private static bool HasShortFlag(List<string> args, params char[] letters) =>
        args.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Skip(1).Any(c => letters.Contains(c)));

    private static bool HasLongFlag(List<string> args, string name) => args.Contains(name, StringComparer.Ordinal);

    private static bool IsDangerousSegment(string segment)
    {
        var tokens = Tokenize(segment);
        var start = 0;
        while (start < tokens.Count && IsEnvAssignment(tokens[start]))
        {
            start++;
        }

        if (start >= tokens.Count)
        {
            return false;
        }

        var head = BaseName(tokens[start]);
        var args = tokens.Skip(start + 1).ToList();
        if (head.StartsWith("mkfs", StringComparison.Ordinal) || DangerousPrograms.Contains(head))
        {
            return true;
        }

        switch (head)
        {
            case "rm":
            {
                var recursive = HasShortFlag(args, 'r', 'R') || HasLongFlag(args, "--recursive");
                var force = HasShortFlag(args, 'f') || HasLongFlag(args, "--force");
                var targets = args.Where(static a => !a.StartsWith('-')).ToList();
                return HasLongFlag(args, "--no-preserve-root") || (recursive && (force || targets.Any(RootLikeTargets.Contains)));
            }

            case "dd":
                return args.Any(static a => a.StartsWith("of=/dev/", StringComparison.Ordinal));
            case "chmod" or "chown" or "chgrp":
            {
                var recursive = HasShortFlag(args, 'R') || HasLongFlag(args, "--recursive");
                var targets = args.Where(static a => !a.StartsWith('-')).ToList();
                return (recursive && targets.Any(RootLikeTargets.Contains)) || (head == "chmod" && args.Contains("777", StringComparer.Ordinal));
            }

            case "git":
            {
                var sub = args.FirstOrDefault(static a => !a.StartsWith('-'));
                return sub switch
                {
                    "push" => HasShortFlag(args, 'f') || args.Any(static a => a is "--force" or "--force-with-lease" || a.StartsWith("--force-with-lease=", StringComparison.Ordinal)),
                    "reset" => HasLongFlag(args, "--hard"),
                    "clean" => HasShortFlag(args, 'f') || HasLongFlag(args, "--force"),
                    "branch" => HasShortFlag(args, 'D'),
                    _ => false,
                };
            }

            default:
                return false;
        }
    }

    private static readonly HashSet<string> ReadOnlyPrograms = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "wc", "pwd", "echo", "printf", "which", "whoami", "date", "uname", "file", "stat", "du", "df", "tree", "grep", "egrep", "fgrep",
        "rg", "sort", "uniq", "cut", "tr", "basename", "dirname", "realpath", "readlink", "id", "hostname", "uptime", "true", "false", "test", "[", "diff", "cmp", "nl", "column", "less", "more", "type",
    };

    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.Ordinal)
    {
        "status", "diff", "log", "show", "rev-parse", "ls-files", "blame", "describe", "shortlog", "grep", "ls-tree", "cat-file", "rev-list", "name-rev", "whatchanged",
    };

    private static bool IsReadOnlySegment(string segment)
    {
        var tokens = Tokenize(segment);
        var start = 0;
        while (start < tokens.Count && IsEnvAssignment(tokens[start]))
        {
            start++;
        }

        if (start >= tokens.Count)
        {
            return false;
        }

        var head = BaseName(tokens[start]);
        var args = tokens.Skip(start + 1).ToList();
        if (head == "git")
        {
            var sub = args.FirstOrDefault(static a => !a.StartsWith('-'));
            if (sub is null)
            {
                return false;
            }

            if (sub == "branch")
            {
                // 只有「列出」是只读:带任何非选项参数(新建 / 删除 / 改名)或写类选项都不是。
                return args.Where(static a => a != "branch").All(static a => a is "-a" or "-r" or "-v" or "-vv" or "--list" or "--show-current" or "--all" or "--remotes");
            }

            if (sub == "remote")
            {
                return args.Count(static a => !a.StartsWith('-')) == 1 && args.Contains("-v", StringComparer.Ordinal);
            }

            return ReadOnlyGit.Contains(sub) && !args.Any(static a => a is "--output" || a.StartsWith("--output=", StringComparison.Ordinal) || a == "-o");
        }

        if (head == "find")
        {
            return !args.Any(static a => a is "-delete" or "-exec" or "-execdir" or "-ok" or "-okdir" or "-fprint" or "-fprintf" or "-fls");
        }

        if (head == "sort")
        {
            return !args.Any(static a => a == "-o" || a.StartsWith("--output", StringComparison.Ordinal));
        }

        return ReadOnlyPrograms.Contains(head);
    }

    /// <summary>
    /// 分级。<see cref="CommandRisk.Dangerous"/> 优先于 <see cref="CommandRisk.ReadOnly"/>;
    /// 只读要求<b>每一段</b>都在白名单里、整条命令没有任何重定向(<c>&gt;</c> <c>&lt;</c>)、命令替换(<c>$(</c> 反引号)和 here-doc。
    /// </summary>
    public static CommandRisk Classify(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return CommandRisk.Unknown;
        }

        try
        {
            if (command.Contains(":(){", StringComparison.Ordinal) || PipeToShell().IsMatch(command) || WritesSystemPath().IsMatch(command))
            {
                return CommandRisk.Dangerous;
            }

            var segments = Segments(command);
            if (segments.Any(IsDangerousSegment))
            {
                return CommandRisk.Dangerous;
            }

            if (command.AsSpan().IndexOfAny(['>', '<', '`']) >= 0 || command.Contains("$(", StringComparison.Ordinal))
            {
                return CommandRisk.Unknown;
            }

            // 只看「叶子」段:Segments 的第一项是整条命令(含接续符),它自己不是一个命令。
            var leaves = new List<string>();
            var pieces = new List<string>();
            Split(command, quoteAware: true, pieces, []);
            leaves.AddRange(pieces.Select(static p => p.Trim()).Where(static p => p.Length > 0));
            return leaves.Count is > 0 and <= 8 && leaves.All(IsReadOnlySegment) ? CommandRisk.ReadOnly : CommandRisk.Unknown;
        }
        catch (RegexMatchTimeoutException)
        {
            return CommandRisk.Unknown;
        }
    }
}
