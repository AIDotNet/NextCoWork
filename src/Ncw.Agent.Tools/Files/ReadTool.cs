using System.Text.Json;
using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>文件工具共用的上限(沿用 2.x)。</summary>
public static class FileLimits
{
    /// <summary>单次读取的上限。超过就让模型改用 Grep,或 Bash 里的 <c>sed -n</c>。</summary>
    public const long MaxReadBytes = 8 * 1024 * 1024;

    /// <summary>单行截断长度(和 Claude Code 一致)。压缩产物一行能有几十万字符。</summary>
    public const int MaxLineChars = 2000;

    /// <summary>不带 <c>limit</c> 时默认读多少行(和 Claude Code 一致)。</summary>
    public const int DefaultReadLimit = 2000;

    /// <summary>
    /// 一次 Read 回给模型的字符预算。3.0 新增(pi):比循环层的 64K 截断略小,到了就停并告诉模型用 <c>offset</c> 继续,
    /// 而不是让循环层在中间一刀切掉、模型不知道从哪继续。
    /// </summary>
    public const int MaxReadOutputChars = 60_000;

    /// <summary>二进制探测的取样长度。</summary>
    public const int SniffBytes = 4096;

    /// <summary>Write / Edit 一次写入的上限。</summary>
    public const long MaxWriteBytes = 64L * 1024 * 1024;

    /// <summary>LS 列出的条目上限。</summary>
    public const int DirListingLimit = 2000;
}

/// <summary>
/// <c>Read</c>:名字、参数名、输出格式<b>全部对齐 Claude Code</b>(模型见过海量 <c>Read(file_path=...)</c> 的训练数据),行号用 <c>cat -n</c> 的形式
/// (右对齐 6 位 + 制表符)—— Edit 要求模型给出原文,换一种它不熟悉的前缀,它会把前缀一起抄进 <c>old_string</c>,然后每一次编辑都匹配不上。
/// </summary>
public sealed class ReadTool() : BuiltinTool(
    "Read",
    "Reads a file from the execution environment. Paths inside the workspace are the normal case, but an absolute path outside it works too.\n\n"
    + "Usage:\n"
    + "- file_path should be an absolute path; a relative path is resolved against the workspace root\n"
    + "- By default it reads up to 2000 lines from the start of the file\n"
    + "- You can pass offset and limit to read a slice of a long file, but read the whole file when you can — do not paginate for no reason\n"
    + "- Lines longer than 2000 characters are truncated\n"
    + "- Output is in `cat -n` format, with line numbers starting at 1. Line endings (LF or CRLF) are not shown\n"
    + "- When the output is cut short, the last line tells you which offset to continue from\n"
    + "- You can call multiple tools in one reply. Speculatively reading several related files at once is much faster than one round trip each\n"
    + "- Binary files are rejected; for files over 8MB use Grep, or `sed -n` through Bash, to pull out a slice\n"
    + "- You MUST read an existing file with this tool before you change it — Write and Edit will fail outright otherwise",
    S.Build(S.Object(
        ["file_path"],
        ("file_path", S.String("Absolute path of the file to read. A relative path is resolved against the workspace root; an absolute path may point outside the workspace", minLength: 1)),
        ("offset", S.Integer("Line number to start reading from (1-based). Only pass it when the file is large enough to need paging", minimum: 1)),
        ("limit", S.Integer("Maximum number of lines to read. Only pass it when the file is large enough to need paging", minimum: 1, maximum: 50_000)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Read file contents (with line numbers)",
    [
        "Use Read to look at files; do not use cat, head or tail through Bash.",
        "Read a file before you Edit or Write over it.",
    ])
{
    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var fs = rt.Environment.Fs;
        var target = await ToolPaths.ResolveAsync(rt, RequiredString(args, "file_path"), ct).ConfigureAwait(false);
        var rel = target.Display;

        FsEntryInfo info;
        try
        {
            info = await fs.StatAsync(target.Absolute, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw Fail($"File does not exist: {rel}. Use LS to see what is in the directory, or Glob to find it by pattern.");
        }

        if (info.IsDirectory)
        {
            throw Fail($"{rel} is a directory, not a file. Use LS to list its contents.");
        }

        if (info.Size > FileLimits.MaxReadBytes)
        {
            throw Fail($"{rel} is too large ({Text.HumanSize(info.Size)}; the limit is {Text.HumanSize(FileLimits.MaxReadBytes)}). "
                + "Use Grep to search for what you need, or run `sed -n 'START,ENDp'` through Bash to pull out a slice.");
        }

        if (info.Size == 0)
        {
            // 空文件也算「读过」—— 否则模型永远没法用 Write 往一个占位空文件里填内容
            rt.Reads.MarkRead(ctx.RunId, target.Absolute);
            return ToolResult.Text($"{rel} exists but is empty (0 bytes).", Details(rt, "read", w => WriteDetails(w, target, 0, 0, 0)));
        }

        // 探测在解码之前
        var head = await fs.ReadHeadAsync(target.Absolute, FileLimits.SniffBytes, ct).ConfigureAwait(false);
        if (Text.LooksBinary(head))
        {
            throw Fail($"{rel} looks like a binary file ({Text.HumanSize(info.Size)}); this tool only reads text.");
        }

        ctx.Progress.Report(new ToolProgress($"Reading {rel} ({Text.HumanSize(info.Size)})"));
        var file = await TextFile.ReadAsync(fs, target.Absolute, FileLimits.MaxReadBytes, ct).ConfigureAwait(false);
        var lines = SplitLines(file.Text);

        var offset = OptionalInt(args, "offset") ?? 1;
        var start = offset - 1;
        if (start >= lines.Count)
        {
            throw Fail($"{rel} has only {lines.Count} lines; offset={offset} is past the end.");
        }

        var limit = OptionalInt(args, "limit") ?? FileLimits.DefaultReadLimit;
        var output = new StringBuilder();
        var shown = 0;
        var cutByBudget = false;
        for (var i = start; i < lines.Count && shown < limit; i++)
        {
            var line = lines[i];
            var body = line.Length > FileLimits.MaxLineChars ? $"{Text.Head(line, FileLimits.MaxLineChars)}…[line truncated]" : line;
            var row = $"{i + 1,6}\t{body}";
            if (shown > 0 && output.Length + row.Length + 1 > FileLimits.MaxReadOutputChars)
            {
                cutByBudget = true;
                break;
            }

            if (shown > 0)
            {
                output.Append('\n');
            }

            output.Append(row);
            shown++;
        }

        // 读过(哪怕只读了一段)就记「已读」:2.x 的行为
        rt.Reads.MarkRead(ctx.RunId, target.Absolute);

        var last = start + shown;
        var truncated = false;
        if (last < lines.Count)
        {
            output.Append($"\n\n[Showing lines {start + 1}–{last} of {lines.Count}{(cutByBudget ? " (output size limit)" : "")}. Pass offset={last + 1} to continue.]");
            truncated = cutByBudget;
        }

        var details = Details(rt, "read", w => WriteDetails(w, target, start + 1, last, lines.Count));
        return ToolResult.Text(output.ToString(), details) with { Truncated = truncated, OriginalBytes = truncated ? info.Size : null };
    }

    /// <summary>按 <c>\n</c> 切行,去掉每行末尾的 <c>\r</c>(CRLF 对模型不可见,Edit 会按文件原来的风格写回去);文件以换行结尾时不多出一个空行。</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var begin = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            lines.Add(Trim(text.AsSpan(begin, i - begin)));
            begin = i + 1;
        }

        if (begin < text.Length)
        {
            lines.Add(Trim(text.AsSpan(begin)));
        }

        return lines;

        static string Trim(ReadOnlySpan<char> line) => (line.Length > 0 && line[^1] == '\r' ? line[..^1] : line).ToString();
    }

    private static void WriteDetails(Utf8JsonWriter w, ToolPath target, int firstLine, int lastLine, int totalLines)
    {
        w.WriteString("path", target.Absolute.Value);
        w.WriteString("displayPath", target.Display);
        w.WriteBoolean("outsideWorkspace", target.Outside);
        w.WriteNumber("firstLine", firstLine);
        w.WriteNumber("lastLine", lastLine);
        w.WriteNumber("totalLines", totalLines);
    }
}
