using System.Text;

namespace Ncw.Agent.Catalog.Discovery;

/// <summary>
/// 只够读 Codex <c>config.toml</c> 的 TOML 子集:表头 <c>[a.b]</c> / <c>[a."b c"]</c>、<c>key = 值</c>(基本 / 字面量字符串、数字、布尔)、
/// 点号键、注释。数组、内联表、多行字符串整条跳过(找供应商用不到)。读不懂的行跳过,不抛——这是别人的文件,格式坏了就当没有。
/// </summary>
internal static class MiniToml
{
    /// <summary>表路径(<c>""</c> 是根表,<c>model_providers.deepseek</c>)→ 键 → 值(字符串原文;数字 / 布尔是字面文本)。</summary>
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal) { [""] = new(StringComparer.Ordinal) };
        var current = "";
        var skipDepth = 0; // 多行数组 / 内联表:括号没配平之前的行都跳过
        var skipTripleQuote = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (skipTripleQuote)
            {
                skipTripleQuote = !line.Contains("\"\"\"", StringComparison.Ordinal) && !line.Contains("'''", StringComparison.Ordinal);
                continue;
            }

            if (skipDepth > 0)
            {
                skipDepth += Balance(line);
                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (line.StartsWith("[[", StringComparison.Ordinal))
                {
                    current = "\u0000"; // 表数组:不支持,里面的键全丢掉
                    continue;
                }

                var end = line.IndexOf(']', StringComparison.Ordinal);
                if (end > 0 && SplitKey(line[1..end]) is { } path)
                {
                    current = string.Join('.', path);
                    tables.TryAdd(current, new Dictionary<string, string>(StringComparer.Ordinal));
                }

                continue;
            }

            var eq = IndexOfEquals(line);
            if (eq <= 0 || current == "\u0000" || SplitKey(line[..eq]) is not { Count: > 0 } keyPath)
            {
                continue;
            }

            var valueText = line[(eq + 1)..].Trim();
            if (valueText.StartsWith("\"\"\"", StringComparison.Ordinal) || valueText.StartsWith("'''", StringComparison.Ordinal))
            {
                var rest = valueText[3..];
                skipTripleQuote = !rest.Contains("\"\"\"", StringComparison.Ordinal) && !rest.Contains("'''", StringComparison.Ordinal);
                continue;
            }

            if (valueText.StartsWith('[') || valueText.StartsWith('{'))
            {
                skipDepth = Balance(valueText);
                continue;
            }

            if (ParseScalar(valueText) is not { } value)
            {
                continue;
            }

            var table = keyPath.Count == 1 ? current : string.Join('.', current.Length == 0 ? keyPath.SkipLast(1) : [current, .. keyPath.SkipLast(1)]);
            if (!tables.TryGetValue(table, out var entries))
            {
                tables[table] = entries = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            entries[keyPath[^1]] = value;
        }

        return tables;
    }

    private static int Balance(string line)
    {
        var depth = 0;
        var inString = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c is '[' or '{')
            {
                depth++;
            }
            else if (!inString && c is ']' or '}')
            {
                depth--;
            }
        }

        return depth;
    }

    private static int IndexOfEquals(string line)
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
            else if (c == '=')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary><c>a."b.c".d</c> → [a, b.c, d];格式不对为 null。</summary>
    private static List<string>? SplitKey(string text)
    {
        var parts = new List<string>();
        var i = 0;
        text = text.Trim();
        while (i < text.Length)
        {
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            if (i >= text.Length)
            {
                return null;
            }

            string part;
            if (text[i] is '"' or '\'')
            {
                var quote = text[i];
                var end = text.IndexOf(quote, i + 1);
                if (end < 0)
                {
                    return null;
                }

                part = text[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != '.' && text[i] != ' ')
                {
                    i++;
                }

                part = text[start..i];
                if (part.Length == 0)
                {
                    return null;
                }
            }

            parts.Add(part);
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            if (i < text.Length)
            {
                if (text[i] != '.')
                {
                    return null;
                }

                i++;
            }
        }

        return parts;
    }

    private static string? ParseScalar(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '\'')
        {
            var end = text.IndexOf('\'', 1);
            return end > 0 ? text[1..end] : null;
        }

        if (text[0] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    sb.Append(text[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => text[i],
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }

            return null; // 没有收尾引号
        }

        var hash = text.IndexOf('#', StringComparison.Ordinal);
        return (hash >= 0 ? text[..hash] : text).Trim();
    }
}
