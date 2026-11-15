using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

internal static class WriteGuards
{
    /// <summary>「没读过就想写」的统一说辞。两个写工具必须给出同一句话。</summary>
    public static string MustReadFirst(string rel) =>
        $"You have not read {rel} in this session yet. Read it first, then edit. "
        + "This exists so you cannot overwrite a file from memory and silently wipe out what you never saw.";

    /// <summary>计划模式的写入围栏:非空时只允许改那一个文件。</summary>
    public static void CheckFence(ToolRuntime rt, ToolPath target)
    {
        if (rt.WriteFence is { } fence && !string.Equals(fence.Value, target.Absolute.Value, PathRules.Comparison(fence.Style)))
        {
            throw new ToolFailureException($"Plan mode may only modify its active Markdown plan. {target.Display} is outside that file; no changes were made.");
        }
    }
}

/// <summary>
/// <c>Write</c>:覆盖已有文件前<b>必须在本 Run 里读过</b>(2.x 的读取跟踪);遵守写入围栏(计划模式);记录变更。
/// 写入是「同目录临时文件 + 改名」:写一半失败不留半截文件,也不碰旧文件;已有文件的权限位保留。同一文件的读-改-写走 <c>FileMutationQueue</c> 串行。
/// </summary>
public sealed class WriteTool() : BuiltinTool(
    "Write",
    "Writes a file in the execution environment. Paths inside the workspace are the normal case, but an absolute path outside it works too.\n\n"
    + "Usage:\n"
    + "- If a file already exists at that path, this tool OVERWRITES it completely\n"
    + "- You MUST read an existing file with Read before writing over it, or this call fails\n"
    + "- Parent directories are created for you\n"
    + "- ALWAYS prefer editing an existing file. Do not create a new one unless it is genuinely needed\n"
    + "- NEVER create documentation files (*.md) or a README on your own initiative. Only write docs when the user explicitly asks\n"
    + "- NEVER write emoji into a file unless the user explicitly asks for it\n"
    + "- When you overwrite a file, its BOM and CRLF line endings are kept\n"
    + "- Use Edit, not this tool, to change part of a file — overwriting a file you have not read in full will almost always drop something you did not know was there",
    S.Build(S.Object(
        ["file_path", "content"],
        ("file_path", S.String("Absolute path of the file to write. Parent directories are created for you. It may point outside the workspace", minLength: 1)),
        ("content", S.String("The complete contents to write. This OVERWRITES the existing file in full")))),
    new ToolTraits(ReadOnly: false, Destructive: true, NeedsNetwork: false),
    "Create or overwrite a file",
    [
        "Use Write only to create new files or to completely rewrite one; use Edit for changes to an existing file.",
        "An existing file must be read with Read before you overwrite it.",
    ])
{
    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var fs = rt.Environment.Fs;
        var target = await ToolPaths.ResolveAsync(rt, RequiredString(args, "file_path"), ct).ConfigureAwait(false);
        var rel = target.Display;
        var content = RequiredString(args, "content");
        WriteGuards.CheckFence(rt, target);

        return await ctx.FileMutations.RunAsync(ToolPaths.MutationKey(target.Absolute), async token =>
        {
            FsEntryInfo? existing = null;
            try
            {
                existing = await fs.StatAsync(target.Absolute, token).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
            {
            }

            TextFile? before = null;
            if (existing is { } info)
            {
                if (info.IsDirectory)
                {
                    throw Fail($"{rel} is a directory; it cannot be written as a file.");
                }

                // 只对已有文件要求先读。新建文件当然没得读
                if (!rt.Reads.WasRead(ctx.RunId, target.Absolute))
                {
                    throw Fail(WriteGuards.MustReadFirst(rel));
                }

                // 覆盖前补读旧内容:采集撤销所需的 before,也用它保留 BOM 和 CRLF
                before = await TextFile.ReadAsync(fs, target.Absolute, EnvironmentFsExtensions.DefaultMaxReadBytes, token).ConfigureAwait(false);
            }

            byte[] bytes;
            if (before is null)
            {
                bytes = TextFile.EncodeAs(content, bom: false);
            }
            else
            {
                // Read 把 CRLF 藏起来了,模型写回来的是 LF:文件原来是 CRLF 就还原,BOM 同理
                var text = before.Newline == NewlineStyle.CrLf ? TextNewlines.Apply(Text.ToLf(content), NewlineStyle.CrLf) : content;
                bytes = TextFile.EncodeAs(text, before.HasBom && !content.StartsWith('﻿'));
            }

            await fs.WriteStreamAsync(
                target.Absolute,
                new MemoryStream(bytes, writable: false),
                new FsWriteStreamOptions(FileLimits.MaxWriteBytes, UnixMode: existing is { UnixMode: not 0 } prior ? prior.UnixMode : null, CreateParents: true),
                token).ConfigureAwait(false);

            // 写完就等于知道当下内容,后续的 Edit 不该再被拦
            rt.Reads.MarkRead(ctx.RunId, target.Absolute);
            rt.Changes.Record(ctx.RunId, target.Absolute, rel, before?.Text, content, !target.Outside);

            var lines = content.Length == 0 ? 0 : ReadTool.SplitLines(content).Count;
            var created = existing is null;
            var details = Details(rt, "write", w =>
            {
                w.WriteString("path", target.Absolute.Value);
                w.WriteString("displayPath", rel);
                w.WriteBoolean("outsideWorkspace", target.Outside);
                w.WriteBoolean("created", created);
                w.WriteNumber("lines", lines);
                w.WriteNumber("bytes", bytes.Length);
            });
            return ToolResult.Text($"{(created ? "Created" : "Overwrote")} {rel} ({lines} lines, {Text.HumanSize(bytes.Length)}).", details);
        }, ct).ConfigureAwait(false);
    }
}
