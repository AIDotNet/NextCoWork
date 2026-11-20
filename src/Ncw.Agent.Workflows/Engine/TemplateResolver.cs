using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workflows.Engine;

/// <summary>
/// 纯托管、零反射的模板插值与条件表达式求值器(docs/design/12-workflows.md §5.2)。
/// 支持 <c>{{inputs.name}}</c>、<c>{{nodes.id.output}}</c>、<c>{{nodes.id.summary}}</c>、
/// <c>{{nodes.id.json.a.b}}</c> 以及回退语法 <c>{{exprA ?? exprB}}</c>。
/// </summary>
public static partial class TemplateResolver
{
    public const int MaxInterpolatedValueLength = 128 * 1024;

    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    public static string Render(
        string? template,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, WorkflowNodeRunRecord> nodes)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex().Replace(template, match =>
        {
            var expr = match.Groups[1].Value.Trim();
            var value = ResolveWithFallback(expr, inputs, nodes);
            return Sanitize(value);
        });
    }

    public static string? ResolveWithFallback(
        string expression,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, WorkflowNodeRunRecord> nodes)
    {
        var parts = expression.Split("??", StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var resolved = ResolveSingle(part, inputs, nodes);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveSingle(
        string path,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, WorkflowNodeRunRecord> nodes)
    {
        if (path.StartsWith("inputs.", StringComparison.Ordinal))
        {
            var key = path["inputs.".Length..];
            return inputs.TryGetValue(key, out var val) ? val : null;
        }

        if (path.StartsWith("nodes.", StringComparison.Ordinal))
        {
            var rest = path["nodes.".Length..];
            var dot = rest.IndexOf('.');
            if (dot <= 0)
            {
                return null;
            }

            var nodeId = rest[..dot];
            var prop = rest[(dot + 1)..];
            if (!nodes.TryGetValue(nodeId, out var node))
            {
                return null;
            }

            if (string.Equals(prop, "output", StringComparison.Ordinal))
            {
                return node.OutputText;
            }

            if (string.Equals(prop, "summary", StringComparison.Ordinal))
            {
                return node.Summary ?? node.OutputText;
            }

            if (string.Equals(prop, "status", StringComparison.Ordinal))
            {
                return node.Status.ToString().ToLowerInvariant();
            }

            if (string.Equals(prop, "error", StringComparison.Ordinal))
            {
                return node.Error;
            }

            if (prop.StartsWith("json.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(node.OutputJson))
            {
                return ResolveJsonPath(node.OutputJson, prop["json.".Length..]);
            }
        }

        return null;
    }

    private static string? ResolveJsonPath(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
                {
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < current.GetArrayLength())
                {
                    current = current[index];
                }
                else
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => current.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 轻量确定性条件求值:支持 <c>==</c>、<c>!=</c>、<c>contains</c> 以及真值判定。
    /// 先在原始表达式上解析顶层运算符(跳过 <c>{{...}}</c> 占位符和引号内部),再对左右操作数各自插值,
    /// 避免上游输出包含 <c>==</c> / <c>!=</c> / <c>contains</c> 时破坏条件判定。
    /// </summary>
    public static bool EvaluateCondition(
        string? expression,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, WorkflowNodeRunRecord> nodes)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var trimmed = expression.Trim();

        var eqIdx = FindTopLevelOperator(trimmed, "==", StringComparison.Ordinal);
        if (eqIdx >= 0)
        {
            var left = RenderOperand(trimmed[..eqIdx], inputs, nodes);
            var right = RenderOperand(trimmed[(eqIdx + 2)..], inputs, nodes);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        var neqIdx = FindTopLevelOperator(trimmed, "!=", StringComparison.Ordinal);
        if (neqIdx >= 0)
        {
            var left = RenderOperand(trimmed[..neqIdx], inputs, nodes);
            var right = RenderOperand(trimmed[(neqIdx + 2)..], inputs, nodes);
            return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        var containsIdx = FindTopLevelOperator(trimmed, " contains ", StringComparison.OrdinalIgnoreCase);
        if (containsIdx >= 0)
        {
            var left = RenderOperand(trimmed[..containsIdx], inputs, nodes);
            var right = RenderOperand(trimmed[(containsIdx + " contains ".Length)..], inputs, nodes);
            return left.Contains(right, StringComparison.OrdinalIgnoreCase);
        }

        var rendered = Render(trimmed, inputs, nodes).Trim();
        return !string.IsNullOrEmpty(rendered)
            && !string.Equals(rendered, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rendered, "0", StringComparison.Ordinal)
            && !string.Equals(rendered, "null", StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderOperand(
        string raw,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, WorkflowNodeRunRecord> nodes)
    {
        var trimmed = raw.Trim();
        var unquoted = Unquote(trimmed);
        return Render(unquoted, inputs, nodes);
    }

    private static int FindTopLevelOperator(string expr, string op, StringComparison comparison)
    {
        var inDouble = false;
        var inSingle = false;
        var braceDepth = 0;

        for (var i = 0; i <= expr.Length - op.Length; i++)
        {
            var c = expr[i];

            if (inDouble)
            {
                if (c == '\\' && i + 1 < expr.Length)
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inDouble = false;
                }

                continue;
            }

            if (inSingle)
            {
                if (c == '\\' && i + 1 < expr.Length)
                {
                    i++;
                    continue;
                }

                if (c == '\'')
                {
                    inSingle = false;
                }

                continue;
            }

            if (c == '{' && i + 1 < expr.Length && expr[i + 1] == '{')
            {
                braceDepth++;
                i++;
                continue;
            }

            if (c == '}' && i + 1 < expr.Length && expr[i + 1] == '}' && braceDepth > 0)
            {
                braceDepth--;
                i++;
                continue;
            }

            if (braceDepth == 0)
            {
                if (c == '"')
                {
                    inDouble = true;
                    continue;
                }

                if (c == '\'')
                {
                    inSingle = true;
                    continue;
                }

                if (expr.AsSpan(i, op.Length).Equals(op.AsSpan(), comparison))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            return s[1..^1];
        }

        return s;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var truncated = value.Length > MaxInterpolatedValueLength
            ? value[..MaxInterpolatedValueLength] + "\n…[truncated]"
            : value;

        var sb = new StringBuilder(truncated.Length);
        foreach (var ch in truncated)
        {
            if (ch == '\n' || ch == '\r' || ch == '\t' || !char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
