using System.Globalization;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary><c>LS</c>:列一个目录(不递归),目录在前;<c>ignore</c> 里的模式只比名字(LS 不递归,路径匹配没有意义)。</summary>
public sealed class LsTool() : BuiltinTool(
    "LS",
    "Lists the files and subdirectories in a directory (NOT recursive), directories first. "
    + "path should be an absolute path. You can pass ignore with glob patterns to skip entries. "
    + "You should generally prefer Glob (search by filename) and Grep (search by content) when you already know what you are looking for — "
    + "LS is for getting a feel for what a directory holds.",
    S.Build(S.Object(
        ["path"],
        ("path", S.String("Absolute path of the directory to list. A relative path is resolved against the workspace root. It may point outside the workspace", minLength: 1)),
        ("ignore", S.Array("Glob patterns to skip, e.g. [\"*.log\", \"__pycache__\"]", S.String("A glob pattern matched against the entry name"), maxItems: 64)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "List a directory",
    ["Use LS to look inside a directory; do not use ls through Bash."])
{
    private sealed record Item(string Name, bool IsDirectory, long? Size);

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var fs = rt.Environment.Fs;
        var target = await ToolPaths.ResolveAsync(rt, RequiredString(args, "path"), ct).ConfigureAwait(false);
        var rel = target.Display;

        FsEntryInfo info;
        try
        {
            info = await fs.StatAsync(target.Absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw Fail($"Directory does not exist: {rel}");
        }

        if (!info.IsDirectory)
        {
            throw Fail($"{rel} is a file, not a directory. Use Read to read it.");
        }

        var ignores = args.Arr("ignore")
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => GlobMatcher.Compile(e.GetString()!, caseSensitive: !rt.CaseInsensitiveFileSystem))
            .ToList();
        var entries = (await fs.ListAsync(target.Absolute, ct).ConfigureAwait(false))
            .Where(e => !ignores.Any(re => re.IsMatch(e.Name)))
            .ToList();
        var truncated = entries.Count > FileLimits.DirListingLimit;

        // 符号链接要 stat 才知道指向文件还是目录、有多大;断链仍然列出来,只是没有大小。并发取,上限 16 路(SSH 上每次 stat 都是往返)
        var gate = new SemaphoreSlim(16);
        var items = await Task.WhenAll(entries.Take(FileLimits.DirListingLimit).Select(async entry =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var isDirectory = entry.Info.IsDirectory;
                long? size = null;
                if (!isDirectory)
                {
                    try
                    {
                        var stat = entry.Info.IsSymlink ? await fs.StatAsync(target.Absolute.Join(entry.Name), ct).ConfigureAwait(false) : entry.Info;
                        isDirectory = stat.IsDirectory;
                        size = isDirectory ? null : stat.Size;
                    }
                    catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied)
                    {
                    }
                }

                return new Item(entry.Name, isDirectory, size);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (items.Length == 0)
        {
            return ToolResult.Text($"{rel} is an empty directory.", Details(rt, "ls", w => WriteDetails(w, target, [], false)));
        }

        // 目录在前,各自按名字排(数字按数值、忽略大小写;平局回落到序数比较,列表不会自己抖)
        var sorted = items
            .OrderBy(i => i.IsDirectory ? 0 : 1)
            .ThenBy(i => i.Name, Comparer<string>.Create(CompareNames))
            .ToList();
        var body = new StringBuilder();
        foreach (var item in sorted)
        {
            if (body.Length > 0)
            {
                body.Append('\n');
            }

            body.Append(item.Name);
            if (item.IsDirectory)
            {
                body.Append('/');
            }
            else if (item.Size is { } size)
            {
                body.Append("  (").Append(Text.HumanSize(size)).Append(')');
            }
        }

        var note = truncated ? $"\n\n[The directory has {entries.Count} entries; showing the first {FileLimits.DirListingLimit}]" : "";
        return ToolResult.Text($"{rel}:\n{body}{note}", Details(rt, "ls", w => WriteDetails(w, target, sorted, truncated)));
    }

    private static int CompareNames(string? a, string? b)
    {
        var c = CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    private static void WriteDetails(Utf8JsonWriter w, ToolPath target, IReadOnlyList<Item> items, bool truncated)
    {
        w.WriteString("path", target.Absolute.Value);
        w.WriteString("displayPath", target.Display);
        w.WriteBoolean("outsideWorkspace", target.Outside);
        w.WriteBoolean("truncated", truncated);
        w.WriteStartArray("entries");
        foreach (var item in items)
        {
            w.WriteStartObject();
            w.WriteString("name", item.Name);
            w.WriteBoolean("isDirectory", item.IsDirectory);
            if (item.Size is { } size)
            {
                w.WriteNumber("size", size);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
