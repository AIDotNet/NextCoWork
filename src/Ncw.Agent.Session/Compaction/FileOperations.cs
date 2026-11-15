using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>压缩要认的「碰过文件」的工具的<b>外部名</b>(转录里 <c>tool_call.name</c> 存的是它)和路径参数的键。由装配方按注册表查出来传入,不写字面量(撞名时外部名会带哈希后缀)。</summary>
public sealed record FileToolNames(IReadOnlySet<string> Read, IReadOnlySet<string> Modify)
{
    public static FileToolNames Default { get; } = new(
        new HashSet<string>(["Read"], StringComparer.Ordinal),
        new HashSet<string>(["Write", "Edit"], StringComparer.Ordinal));

    public static FileToolNames None { get; } = new(new HashSet<string>(), new HashSet<string>());

    public bool Touches(string name) => Read.Contains(name) || Modify.Contains(name);
}

/// <summary>累积的读写文件清单(pi):摘要末尾的 <c>&lt;read-files&gt;</c>(只读过的)和 <c>&lt;modified-files&gt;</c>(改过的),迭代摘要时把上次的并进来。</summary>
public sealed record FileOperations(IReadOnlyList<string> Read, IReadOnlyList<string> Modified)
{
    public static FileOperations Empty { get; } = new([], []);

    public bool IsEmpty => Read.Count == 0 && Modified.Count == 0;
}

public static partial class FileOperationTracker
{
    private static readonly string[] PathKeys = ["file_path", "path"];

    /// <summary>
    /// 从消息里的工具调用累积读写清单。<b>只认成功的调用</b>(2.x ★:失败的 Read——文件不存在——重附一次只会再失败一次),
    /// 改过的文件不会同时出现在只读清单里。
    /// </summary>
    public static FileOperations Collect(IEnumerable<Message> messages, FileToolNames tools, FileOperations? previous = null)
    {
        var list = messages.ToList();
        var failed = FailedCalls(list);
        var read = new SortedSet<string>(previous?.Read ?? [], StringComparer.Ordinal);
        var modified = new SortedSet<string>(previous?.Modified ?? [], StringComparer.Ordinal);
        foreach (var call in list.SelectMany(m => m.Parts).OfType<ToolCallPart>())
        {
            if (failed.Contains(call.CallId) || PathOf(call) is not { } path)
            {
                continue;
            }

            if (tools.Modify.Contains(call.Name))
            {
                modified.Add(path);
            }
            else if (tools.Read.Contains(call.Name))
            {
                read.Add(path);
            }
        }

        read.ExceptWith(modified);
        return new FileOperations([.. read], [.. modified]);
    }

    /// <summary>最近碰过的文件,<b>新的在前</b>,去重(压缩后重附的候选)。</summary>
    public static IReadOnlyList<string> RecentPaths(IReadOnlyList<Message> messages, FileToolNames tools)
    {
        var failed = FailedCalls(messages);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var parts = messages[i].Parts;
            for (var j = parts.Count - 1; j >= 0; j--)
            {
                if (parts[j] is ToolCallPart call && tools.Touches(call.Name) && !failed.Contains(call.CallId)
                    && PathOf(call) is { } path && seen.Add(path))
                {
                    result.Add(path);
                }
            }
        }

        return result;
    }

    private static HashSet<string> FailedCalls(IEnumerable<Message> messages) =>
        messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Where(r => r.IsError).Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);

    private static string? PathOf(ToolCallPart call)
    {
        if (call.Input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in PathKeys)
        {
            if (call.Input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } path)
            {
                return path;
            }
        }

        return null;
    }

    // ── 摘要里的清单(写出 / 读回)──

    /// <summary>摘要末尾追加的清单块。空清单不出标签。</summary>
    public static string Format(FileOperations ops)
    {
        var text = "";
        if (ops.Read.Count > 0)
        {
            text += $"\n\n<read-files>\n{string.Join("\n", ops.Read)}\n</read-files>";
        }

        if (ops.Modified.Count > 0)
        {
            text += $"\n\n<modified-files>\n{string.Join("\n", ops.Modified)}\n</modified-files>";
        }

        return text;
    }

    /// <summary>把上一份摘要拆成「正文」和「累积的清单」:清单喂给下一轮的累积,正文喂给模型做迭代摘要(清单由代码重新追加,不让模型改)。</summary>
    public static (string Body, FileOperations Operations) Split(string summary)
    {
        static IReadOnlyList<string> Lines(Match match) =>
            match.Success ? match.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

        var read = Lines(ReadBlock().Match(summary));
        var modified = Lines(ModifiedBlock().Match(summary));
        var body = ModifiedBlock().Replace(ReadBlock().Replace(summary, ""), "").Trim();
        return (body, new FileOperations(read, modified));
    }

    [GeneratedRegex(@"<read-files>\n?([\s\S]*?)\n?</read-files>")]
    private static partial Regex ReadBlock();

    [GeneratedRegex(@"<modified-files>\n?([\s\S]*?)\n?</modified-files>")]
    private static partial Regex ModifiedBlock();
}
