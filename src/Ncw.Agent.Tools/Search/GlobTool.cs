using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>
/// <c>Glob</c>:按文件名找。结果按修改时间排序(新的在前),200 条、5 秒预算;不读 <c>.gitignore</c>(固定的忽略表,描述里直说)。
/// 目标在项目根外时遍历基准整个换成目标本身,命中路径按绝对形式给出。
/// </summary>
public sealed class GlobTool() : BuiltinTool(
    "Glob",
    "- Fast file-pattern matching that works on any codebase size\n"
    + "- Supports glob patterns like \"**/*.js\" or \"src/**/*.ts\"\n"
    + "- Returns matching paths SORTED BY MODIFICATION TIME, most recent first\n"
    + "- Patterns are matched against paths relative to the workspace root (relative to path when path is outside the workspace), so with path set to a subdirectory write \"src/**/*.ts\" or \"**/*.ts\"\n"
    + "- Use this when you are looking for files by name; use Grep when you are looking for them by CONTENT\n"
    + "- You can call multiple tools in one reply. Speculatively firing several searches at once beats trying one at a time\n"
    + "- Note that * does not cross directory boundaries: to find every .ts file in every subdirectory write \"**/*.ts\", not \"*.ts\"\n"
    + $"- {IgnoreList.Note}",
    S.Build(S.Object(
        ["pattern"],
        ("pattern", S.String("The glob pattern to match file paths against", minLength: 1)),
        ("path", S.String("Directory to search in (absolute path; it may be outside the workspace). Omit it to search the whole workspace. IMPORTANT: to use the default, OMIT this field entirely — do not pass \"undefined\" or \"null\"")))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Find files by name pattern",
    ["Use Glob to find files by name; do not use find through Bash."])
{
    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var fs = rt.Environment.Fs;
        var pattern = RequiredString(args, "pattern");
        var target = await ToolPaths.ResolveAsync(rt, args.Str("path"), ct).ConfigureAwait(false);

        FsEntryInfo info;
        try
        {
            info = await fs.StatAsync(target.Absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw Fail($"Directory does not exist: {target.Display}");
        }

        if (!info.IsDirectory)
        {
            throw Fail($"{target.Display} is a file, not a directory. Use Read to read it, or Grep to search inside it.");
        }

        var walkBase = await WalkBase.OfAsync(rt, target, ct).ConfigureAwait(false);
        var re = GlobMatcher.Compile(pattern, caseSensitive: !rt.CaseInsensitiveFileSystem);
        var walk = await Walker.WalkAsync(
            new WalkOptions { Fs = fs, Root = walkBase.Root, Start = walkBase.Start, Deadline = SearchLimits.SearchBudget },
            rt.Clock,
            ct).ConfigureAwait(false);

        var hits = walk.Entries.Where(e => !e.IsDirectory && re.IsMatch(GlobMatcher.Normalize(e.Rel))).ToList();
        if (hits.Count == 0)
        {
            return ToolResult.Text(
                $"No files match \"{pattern}\" under {walkBase.Label}. Note that * does not cross directory boundaries — use **/ to descend."
                + (walk.TimedOut ? " The traversal also timed out, so this result may be incomplete." : ""),
                Details(rt, "glob", w => WriteDetails(w, pattern, walkBase.Label, [], 0, walk.TruncatedOrTimedOut())));
        }

        // 只对命中的文件 stat。对全部遍历结果 stat 的话,一个大仓库要多几万次系统调用
        var gate = new SemaphoreSlim(16);
        var withTime = await Task.WhenAll(hits.Take(SearchLimits.MaxGlobResults * 4).Select(async e =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var modified = DateTimeOffset.MinValue;
                try
                {
                    modified = (await fs.StatAsync(e.Abs, ct).ConfigureAwait(false)).ModifiedAt;
                }
                catch (EnvironmentException ex) when (ex.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied)
                {
                    // 刚被删掉 / 断链:仍然列出来,只是排在最后
                }

                return (Rel: walkBase.Display(e.Rel), Modified: modified);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var shown = withTime
            .OrderByDescending(h => h.Modified)
            .ThenBy(h => h.Rel, StringComparer.Ordinal)
            .Take(SearchLimits.MaxGlobResults)
            .ToList();
        var notes = new List<string>();
        if (hits.Count > shown.Count)
        {
            notes.Add($"{hits.Count} files matched; listing the {shown.Count} most recently modified");
        }

        if (walk.Truncated)
        {
            notes.Add("traversal limit reached; some directories were not scanned");
        }

        if (walk.TimedOut)
        {
            notes.Add("traversal timed out; the result may be incomplete");
        }

        var text = string.Join('\n', shown.Select(h => h.Rel)) + (notes.Count > 0 ? $"\n\n[{string.Join("; ", notes)}]" : "");
        return ToolResult.Text(text, Details(rt, "glob", w => WriteDetails(w, pattern, walkBase.Label, shown.Select(h => h.Rel).ToList(), hits.Count, walk.TruncatedOrTimedOut())));
    }

    private static void WriteDetails(Utf8JsonWriter w, string pattern, string basePath, IReadOnlyList<string> files, int total, bool incomplete)
    {
        w.WriteString("pattern", pattern);
        w.WriteString("path", basePath);
        w.WriteNumber("total", total);
        w.WriteBoolean("incomplete", incomplete);
        w.WriteStartArray("files");
        foreach (var f in files)
        {
            w.WriteStringValue(f);
        }

        w.WriteEndArray();
    }
}

internal static class WalkResultExtensions
{
    public static bool TruncatedOrTimedOut(this WalkResult result) => result.Truncated || result.TimedOut;
}
