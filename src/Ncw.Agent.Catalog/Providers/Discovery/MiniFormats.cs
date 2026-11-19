using System.Text.RegularExpressions;

namespace Ncw.Agent.Catalog.Discovery;

/// <summary>
/// Shell 配置 / <c>.env</c> 里的变量赋值(<c>export NAME=值</c>、<c>NAME=值</c>、fish 的 <c>set -gx NAME 值</c>)。
/// 只认字面值:双引号或不带引号的值里有 <c>$</c>、反引号就跳过(那是要 shell 算的,我们不执行任何东西);单引号里原样。
/// </summary>
internal static partial class ShellVars
{
    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(.*)$")]
    private static partial Regex Posix();

    [GeneratedRegex(@"^\s*set\s+(?:-[a-zA-Z]+\s+)*([A-Za-z_][A-Za-z0-9_]*)\s+(.+)$")]
    private static partial Regex Fish();

    /// <summary>一个文件里的赋值(同名后面的为准)。</summary>
    public static Dictionary<string, string> Parse(string text, bool fish = false)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var match = fish ? Fish().Match(line) : Posix().Match(line);
            if (match.Success && Literal(match.Groups[2].Value) is { Length: > 0 } value)
            {
                vars[match.Groups[1].Value] = value;
            }
        }

        return vars;
    }

    private static string? Literal(string text)
    {
        text = text.Trim();
        if (text.StartsWith('\''))
        {
            var end = text.IndexOf('\'', 1);
            return end > 0 ? text[1..end] : null;
        }

        string value;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            value = text[1..end];
        }
        else
        {
            var hash = text.IndexOf(" #", StringComparison.Ordinal);
            value = (hash >= 0 ? text[..hash] : text).Trim();
            if (value.Contains(' ', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal))
            {
                return null; // 一行里还有别的命令
            }
        }

        return value.Contains('$', StringComparison.Ordinal) || value.Contains('`', StringComparison.Ordinal) ? null : value;
    }
}

/// <summary>
/// 只够读 Aider <c>.aider.conf.yml</c> 和 Continue <c>config.yaml</c> 的 YAML 子集:顶层 <c>key: 值</c>、顶层键下的列表
/// (<c>- 标量</c> 或 <c>- k: v</c> 开头的映射,后面缩进更深的 <c>k: v</c> 归同一项)、行内列表 <c>[a, b]</c>。读不懂的行跳过,不抛。
/// </summary>
internal static class MiniYaml
{
    public sealed class Doc
    {
        public Dictionary<string, string> Scalars { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<Dictionary<string, string>>> Maps { get; } = new(StringComparer.Ordinal);
    }

    public static Doc Parse(string text)
    {
        var doc = new Doc();
        string? key = null;           // 当前顶层键(它的值在下面几行)
        Dictionary<string, string>? item = null;
        var itemIndent = -1;

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw.TrimEnd('\r'));
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var body = line.Trim();

            if (indent == 0 && !body.StartsWith('-'))
            {
                item = null;
                key = null;
                var colon = body.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var name = Unquote(body[..colon]);
                var value = body[(colon + 1)..].Trim();
                if (value.Length == 0)
                {
                    key = name;
                }
                else if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    doc.Lists[name] = [.. value[1..^1].Split(',').Select(v => Unquote(v.Trim())).Where(v => v.Length > 0)];
                }
                else
                {
                    doc.Scalars[name] = Unquote(value);
                }

                continue;
            }

            if (key is null)
            {
                continue;
            }

            if (body.StartsWith("- ", StringComparison.Ordinal) || body == "-")
            {
                var rest = body.Length > 1 ? body[2..].Trim() : "";
                var colon = rest.IndexOf(": ", StringComparison.Ordinal) is var c && c > 0 ? c : (rest.EndsWith(':') ? rest.Length - 1 : -1);
                if (colon > 0 && !rest.StartsWith('"') && !rest.StartsWith('\''))
                {
                    item = new Dictionary<string, string>(StringComparer.Ordinal);
                    itemIndent = indent;
                    item[Unquote(rest[..colon])] = Unquote(rest[(colon + 1)..].Trim());
                    if (!doc.Maps.TryGetValue(key, out var maps))
                    {
                        doc.Maps[key] = maps = [];
                    }

                    maps.Add(item);
                }
                else
                {
                    item = null;
                    if (!doc.Lists.TryGetValue(key, out var list))
                    {
                        doc.Lists[key] = list = [];
                    }

                    list.Add(Unquote(rest));
                }

                continue;
            }

            if (item is not null && indent > itemIndent)
            {
                var colon = body.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    item[Unquote(body[..colon])] = Unquote(body[(colon + 1)..].Trim());
                }
            }
        }

        return doc;
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        return text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')) ? text[1..^1] : text;
    }
}
