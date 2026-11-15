using System.Text;
using System.Text.Json.Nodes;
using Ncw.Agent.Parity.Tests.Rig;

namespace Ncw.Agent.Parity.Tests.Compare;

public sealed record Mismatch(Section Section, string Report);

public static class Comparer
{
    /// <summary>逐部分比较录制里的 2.x 结果和 C# 回放结果;先套用已登记的规则,剩下的差异就是「未登记的差异」。</summary>
    public static (IReadOnlyList<Mismatch> Mismatches, RuleLedger Ledger) Compare(Recording recording, Replayed actual)
    {
        var ledger = new RuleLedger();
        var mismatches = new List<Mismatch>();
        var expected = recording.Expected;
        // 顺序有意义:事件里发现的差异(合并消息、流程分叉)要通过标记带到后面的部分;消息 id 重新编号按「事件 → 消息行」的出现顺序。
        var sections = new (Section Section, JsonArray Expected, JsonArray Actual)[]
        {
            (Section.Events, expected["events"]!.AsArray(), actual.Events),
            (Section.Rows, expected["rows"]!.AsArray(), actual.Rows),
            (Section.Requests, expected["requests"]!.AsArray(), actual.Requests),
            (Section.Runs, expected["runs"]!.AsArray(), actual.Runs),
            (Section.Interactions, expected["interactions"]!.AsArray(), actual.Interactions),
            (Section.Files, FilesArray(expected["files"]!.AsObject()), actual.Files),
        };
        var scope = new RuleScope(recording);
        var leftIds = new Renumber();
        var rightIds = new Renumber();
        foreach (var (section, theirs, ours) in sections)
        {
            var left = theirs.Select(n => n!.DeepClone()).ToList();
            var right = ours.Select(n => n!.DeepClone()).ToList();
            foreach (var rule in Rules.All.Where(r => r.Sections.HasFlag(section)))
            {
                ledger.Add(rule, rule.Apply(left, right, section, scope));
            }

            // 规则可能合并 / 去掉消息,m1、m2… 在规则之后按首次出现的顺序重新编号(两边同一份算法)。
            leftIds.Apply(left);
            rightIds.Apply(right);

            var report = section == Section.Requests ? PathDiff(left, right) : Diff(left, right);
            if (report is not null)
            {
                mismatches.Add(new Mismatch(section, report));
            }
        }

        return (mismatches, ledger);
    }

    private static JsonArray FilesArray(JsonObject files) =>
        new([.. files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["content"] = f.Value!.GetValue<string>() })]);

    /// <summary>LCS 对齐的可读 diff:<c>-</c> 只在 2.x,<c>+</c> 只在 C#,两边相同的只保留差异附近的上下文。</summary>
    private static string? Diff(List<JsonNode> left, List<JsonNode> right)
    {
        var a = left.Select(Compact).ToList();
        var b = right.Select(Compact).ToList();
        if (a.SequenceEqual(b))
        {
            return null;
        }

        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var lines = new List<(char Mark, string Text)>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                lines.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                lines.Add(('-', a[x++]));
            }
            else
            {
                lines.Add(('+', b[y++]));
            }
        }

        while (x < a.Count)
        {
            lines.Add(('-', a[x++]));
        }

        while (y < b.Count)
        {
            lines.Add(('+', b[y++]));
        }

        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Mark == ' ')
            {
                continue;
            }

            for (var k = Math.Max(0, i - 2); k <= Math.Min(lines.Count - 1, i + 2); k++)
            {
                keep[k] = true;
            }
        }

        var sb = new StringBuilder();
        var gap = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!keep[i])
            {
                gap = true;
                continue;
            }

            if (gap)
            {
                sb.AppendLine("   …");
                gap = false;
            }

            var text = lines[i].Text.Length > 700 ? lines[i].Text[..700] + "…" : lines[i].Text;
            sb.Append(' ').Append(lines[i].Mark).Append(' ').AppendLine(text);
        }

        return sb.ToString();
    }

    /// <summary>请求按序号一一对应,只列出不同的 JSON 路径(整条请求体太长,行级 diff 看不出差在哪)。</summary>
    private static string? PathDiff(List<JsonNode> left, List<JsonNode> right)
    {
        var sb = new StringBuilder();
        if (left.Count != right.Count)
        {
            sb.AppendLine($"   request count: 2.x sent {left.Count}, C# sent {right.Count}");
        }

        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var a = Flatten(left[i]);
            var b = Flatten(right[i]);
            foreach (var path in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                a.TryGetValue(path, out var x);
                b.TryGetValue(path, out var y);
                if (x == y)
                {
                    continue;
                }

                sb.AppendLine($"   request #{i + 1} {path}");
                sb.AppendLine($"     - {Clip(x)}");
                sb.AppendLine($"     + {Clip(y)}");
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string Clip(string? text) => text is null ? "(absent)" : text.Length > 400 ? text[..400] + "…" : text;

    private static Dictionary<string, string> Flatten(JsonNode node, string path = "$")
    {
        var map = new Dictionary<string, string>();
        void Walk(JsonNode? n, string p)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        Walk(v, $"{p}.{k}");
                    }

                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        Walk(a[i], $"{p}[{i}]");
                    }

                    break;
                default:
                    map[p] = n?.ToJsonString() ?? "null";
                    break;
            }
        }

        Walk(node, path);
        return map;
    }

    private static string Compact(JsonNode node) => node.ToJsonString();
}

/// <summary>把生成出来的消息 id(<c>m1</c>、<c>m2</c>…)在规则之后重新按首次出现的顺序编号;事件和消息行共用一份映射。</summary>
internal sealed class Renumber
{
    private readonly Dictionary<string, string> _map = [];

    public void Apply(List<JsonNode> nodes)
    {
        foreach (var node in nodes)
        {
            Visit(node);
        }
    }

    private void Visit(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (o["id"] is JsonValue v && v.TryGetValue<string>(out var id) && System.Text.RegularExpressions.Regex.IsMatch(id, "^m[0-9]+$"))
            {
                if (!_map.TryGetValue(id, out var mapped))
                {
                    mapped = $"m{_map.Count + 1}";
                    _map[id] = mapped;
                }

                o["id"] = mapped;
            }

            foreach (var (_, child) in o.ToList())
            {
                Visit(child);
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var child in a)
            {
                Visit(child);
            }
        }
    }
}
