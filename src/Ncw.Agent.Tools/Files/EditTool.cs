using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>
/// <c>Edit</c>:参数名对齐 Claude Code(<c>old_string / new_string / replace_all</c>),并吸收 pi 的 <c>edits</c> 数组(一次多处替换)。
/// 每处必须唯一(除非 <c>replace_all</c>)、互不重叠,都相对原文件匹配;精确匹配失败时退到宽松匹配(NFKC、引号破折号、行尾空白);
/// 保留 CRLF 和 BOM、保留文件权限位;<c>Details</c> 里带 diff。仍要求先读过(2.x)。
/// C# 的 <c>string.Replace</c> 没有 <c>$&amp;</c> 这类替换模式,2.x 的那个 bug 自然消失。
/// </summary>
public sealed class EditTool() : BuiltinTool(
    "Edit",
    "Performs exact string replacements inside a file.\n\n"
    + "Usage:\n"
    + "- You MUST read the file with Read before editing it, or this call fails outright\n"
    + "- When you copy text out of Read output, preserve the exact indentation (tabs/spaces) AFTER the line-number prefix. "
    + "That prefix is \"spaces + line number + a tab\"; only what follows the tab is real file content. "
    + "NEVER include any part of the line-number prefix in old_string or new_string\n"
    + "- Pass old_string / new_string for one change, or pass edits (an array of {old_string, new_string}) to make several changes to the same file in ONE call. "
    + "Every edit is matched against the ORIGINAL file, so edits must not overlap each other\n"
    + "- The edit FAILS if old_string is not unique in the file and nothing is changed. Either add surrounding lines until it is unique, "
    + "or pass replace_all to change every occurrence (for example to rename a variable)\n"
    + "- If the exact text is not found, a looser match is tried (curly quotes, dashes and trailing whitespace are ignored); only the matched text is replaced\n"
    + "- The file's line endings (LF or CRLF) and BOM are preserved\n"
    + "- ALWAYS prefer editing an existing file. Do not create a new one unless it is genuinely needed\n"
    + "- NEVER add emoji to a file unless the user explicitly asks for it",
    S.Build(S.Object(
        ["file_path"],
        ("file_path", S.String("Absolute path of the file to edit. It may point outside the workspace", minLength: 1)),
        ("old_string", S.String("The exact text to replace", minLength: 1)),
        ("new_string", S.String("What to replace it with (must differ from old_string). An empty string deletes the text")),
        ("replace_all", S.Boolean("Replace every occurrence of old_string. Defaults to false — a non-unique match fails instead, so that you add context")),
        ("edits", S.Array(
            "Several replacements to make in one call, instead of old_string / new_string. Each is matched against the original file",
            S.Object(
                ["old_string", "new_string"],
                ("old_string", S.String("The exact text to replace", minLength: 1)),
                ("new_string", S.String("What to replace it with. An empty string deletes the text")),
                ("replace_all", S.Boolean("Replace every occurrence of this old_string")))
            , minItems: 1, maxItems: 100)))),
    new ToolTraits(ReadOnly: false, Destructive: true, NeedsNetwork: false),
    "Make precise edits to a file (one or several replacements)",
    [
        "Use Edit for changes to existing files; use Write only for new files or complete rewrites.",
        "Read the file first, and group several changes to one file into one Edit call with edits.",
    ])
{
    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var specs = ParseEdits(args, out var singleForm);
        var rt = ctx.Runtime;
        var fs = rt.Environment.Fs;
        var target = await ToolPaths.ResolveAsync(rt, RequiredString(args, "file_path"), ct).ConfigureAwait(false);
        var rel = target.Display;
        WriteGuards.CheckFence(rt, target);

        return await ctx.FileMutations.RunAsync(ToolPaths.MutationKey(target.Absolute), async token =>
        {
            FsEntryInfo info;
            try
            {
                info = await fs.StatAsync(target.Absolute, token).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
            {
                throw Fail($"File does not exist: {rel}. Use Write to create a new file.");
            }

            if (info.IsDirectory)
            {
                throw Fail($"{rel} is a directory, not a file.");
            }

            if (!rt.Reads.WasRead(ctx.RunId, target.Absolute))
            {
                throw Fail(WriteGuards.MustReadFirst(rel));
            }

            var file = await TextFile.ReadAsync(fs, target.Absolute, EnvironmentFsExtensions.DefaultMaxReadBytes, token).ConfigureAwait(false);
            var original = Text.ToLf(file.Text);
            var located = EditEngine.Locate(original, specs, rel, singleForm);
            var edited = EditEngine.Apply(original, located.Replacements);
            if (string.Equals(edited, original, StringComparison.Ordinal))
            {
                throw Fail($"The edit would not change {rel}, so nothing was written.");
            }

            var bytes = file.Encode(edited, rt.Environment.Facts.NewLine);
            await fs.WriteStreamAsync(
                target.Absolute,
                new MemoryStream(bytes, writable: false),
                new FsWriteStreamOptions(FileLimits.MaxWriteBytes, UnixMode: info.UnixMode != 0 ? info.UnixMode : null),
                token).ConfigureAwait(false);

            var style = file.Newline ?? (rt.Environment.Facts.NewLine == "\r\n" ? NewlineStyle.CrLf : NewlineStyle.Lf);
            rt.Changes.Record(ctx.RunId, target.Absolute, rel, file.Text, TextNewlines.Apply(edited, style), !target.Outside);

            var (diff, firstChanged, added, removed) = EditEngine.BuildDiff(original, located.Replacements);
            var count = located.Replacements.Count;
            var details = Details(rt, "edit", w =>
            {
                w.WriteString("path", target.Absolute.Value);
                w.WriteString("displayPath", rel);
                w.WriteBoolean("outsideWorkspace", target.Outside);
                w.WriteNumber("edits", specs.Count);
                w.WriteNumber("replacements", count);
                w.WriteBoolean("fuzzy", located.Fuzzy);
                w.WriteNumber("firstChangedLine", firstChanged);
                w.WriteNumber("added", added);
                w.WriteNumber("removed", removed);
                w.WriteString("diff", diff);
            });
            var note = located.Fuzzy ? " (matched after ignoring quote, dash and trailing-whitespace differences)" : "";
            return ToolResult.Text($"Edited {rel}: replaced {count} occurrence(s){note}.", details);
        }, ct).ConfigureAwait(false);
    }

    private static List<EditSpec> ParseEdits(JsonElement args, out bool singleForm)
    {
        var oldText = args.Str("old_string");
        var newText = args.Str("new_string");
        var hasEdits = args.Prop("edits") is { ValueKind: JsonValueKind.Array };
        var specs = new List<EditSpec>();
        if (hasEdits)
        {
            if (oldText is not null || newText is not null)
            {
                throw Fail("Pass either old_string / new_string or edits, not both.");
            }

            singleForm = false;
            foreach (var item in args.Arr("edits"))
            {
                specs.Add(new EditSpec(item.Str("old_string") ?? "", item.Str("new_string") ?? "", item.Bool("replace_all") ?? false));
            }
        }
        else
        {
            singleForm = true;
            if (string.IsNullOrEmpty(oldText))
            {
                throw Fail("old_string is required (or pass edits with several replacements).");
            }

            if (newText is null)
            {
                throw Fail("new_string is required; use an empty string to delete the text.");
            }

            specs.Add(new EditSpec(oldText, newText, args.Bool("replace_all") ?? false));
        }

        for (var i = 0; i < specs.Count; i++)
        {
            if (specs[i].Old == specs[i].New)
            {
                throw Fail(singleForm
                    ? "old_string and new_string are identical, so this edit would change nothing."
                    : $"edits[{i}]: old_string and new_string are identical, so this edit would change nothing.");
            }
        }

        return specs;
    }
}
