using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Kernel.Session;

public sealed record InstructionsDiagnostic(string Path, string Key, string? Detail = null);

/// <summary>一个读进来的指令文件。<see cref="Included"/> = 是否(至少部分)进了预算。</summary>
public sealed record InstructionsFile(string Path, string FileName, bool Global, bool Included, bool Truncated);

public sealed record InstructionsResult(string Text, IReadOnlyList<InstructionsFile> Files, IReadOnlyList<InstructionsDiagnostic> Diagnostics)
{
    public static InstructionsResult Empty { get; } = new("", [], []);
}

/// <summary>全局那一层(应用数据根)在设备本地,不在项目的执行环境里(SSH 项目的全局指令仍然来自本机设备),所以单独给环境和路径。</summary>
public sealed record GlobalInstructions(IExecutionEnvironment Environment, EnvPath Root);

/// <summary>
/// 项目指令文件的发现与读取(04 §2,按 pi 的规则兼容,Q16;不可信边界等安全处理沿用 2.x 的 <c>instructions.ts</c>)。
/// <list type="bullet">
/// <item><b>每个目录只取一个文件</b>:按 <c>AGENTS.override.md</c> → <c>AGENTS.md</c> → <c>CLAUDE.md</c> 的顺序取第一个存在的;</item>
/// <item><b>读哪些目录</b>:全局目录 → 从文件系统根到项目根之间的各级祖先 → 项目根;项目是 git worktree 时(<c>.git</c> 是个文件)同时检查主仓库根(与 pi 一致),排在项目根之前;
/// 祖先目录经项目的执行环境读取(SSH 环境里就是远程主机上的);</item>
/// <item><b>拼接顺序</b>:全局在前,然后由外到内,离项目越近越靠后、优先级越高;每段前面一行分隔线注明来源路径和「后面的优先」;</item>
/// <item><b>预算</b>:单文件读入 64 KiB;总共 32 K 字符,按<b>由内到外</b>分配(项目根的文件先占预算),超出部分截断并注明,更外层的整份省略并注明;</item>
/// <item><b>围栏</b>:每个文件只能解析到它自己所在的目录之内(realpath):<c>AGENTS.md -&gt; ~/.ssh/id_rsa</c> 这样的软链配上「每一轮都把这份正文发给上游」就是一次静默的密钥外泄,
/// 跳过并记一条诊断,<b>诊断里不回显目标路径</b>(那是逃逸想探的东西);</item>
/// <item><b>消毒</b>:削控制字符、中和 <c>&lt;system-reminder&gt;</c>(<see cref="PromptText.Sanitize"/>);「不可信边界」声明由装配时的指令块带上;</item>
/// <item><b>永不抛</b>(连接断开除外,那是环境的故障,不能当成「没有指令」):读不了就记一条诊断,当它不存在。</item>
/// </list>
/// 2.x 从其他工具导入的指令副本(<paramref name="imported"/>)仍然附在最后,本地文件优先。
/// </summary>
public static class ProjectInstructions
{
    public static readonly IReadOnlyList<string> FileNames = ["AGENTS.override.md", "AGENTS.md", "CLAUDE.md"];

    /// <summary>单个文件读进内存的字节上限。留出余量,好让「几份加起来超了」被总预算那一道截住。</summary>
    public const long FileMaxBytes = 64 * 1024;

    /// <summary>拼接之后的字符上限。项目规矩写到 32K 就不是规矩了,是文档;而它每一轮都要重发。</summary>
    public const int TotalMaxChars = 32 * 1024;

    public const string TruncationMarker = "\n[... truncated: the project instructions budget was reached]";

    public static async Task<InstructionsResult> DiscoverAsync(
        IExecutionEnvironment environment,
        EnvPath projectRoot,
        GlobalInstructions? global = null,
        string? imported = null,
        CancellationToken ct = default)
    {
        var diagnostics = new List<InstructionsDiagnostic>();
        var style = projectRoot.Style;

        // 目录清单,由外到内(全局在最前,项目根在最后)
        var directories = new List<(IExecutionEnvironment Environment, EnvPath Directory, bool Global)>();
        if (global is not null)
        {
            directories.Add((global.Environment, global.Root, true));
        }

        var ancestors = new List<EnvPath>();
        for (var current = projectRoot.Parent; current.Value != projectRoot.Value; current = current.Parent)
        {
            ancestors.Add(current);
            if (current.Parent.Value == current.Value)
            {
                break;
            }
        }

        ancestors.Reverse();
        foreach (var ancestor in ancestors)
        {
            directories.Add((environment, ancestor, false));
        }

        if (await MainRepositoryRootAsync(environment, projectRoot, ct).ConfigureAwait(false) is { } main
            && !directories.Any(d => !d.Global && SamePath(d.Directory, main, style)) && !SamePath(main, projectRoot, style))
        {
            directories.Add((environment, main, false));
        }

        directories.Add((environment, projectRoot, false));

        // 每个目录取一个文件
        var found = new List<(string Path, string FileName, bool Global, string Text)>();
        foreach (var (env, directory, isGlobal) in directories)
        {
            if (await ReadOneAsync(env, directory, diagnostics, ct).ConfigureAwait(false) is { } file)
            {
                found.Add((file.Path, file.FileName, isGlobal, file.Text));
            }
        }

        // 预算由内到外分配
        var files = new List<InstructionsFile>();
        var chunks = new List<string>(); // 与 found 同序(由外到内)
        var remaining = TotalMaxChars;
        var included = new bool[found.Count];
        var texts = new string?[found.Count];
        var truncated = new bool[found.Count];
        for (var i = found.Count - 1; i >= 0; i--)
        {
            var body = found[i].Text;
            if (body.Length == 0)
            {
                continue;
            }

            var header = Header(found[i].Path, found[i].Global);
            var cost = header.Length + body.Length + 2;
            if (remaining <= header.Length + TruncationMarker.Length + 2)
            {
                continue; // 预算耗尽:更外层的整份省略
            }

            if (cost <= remaining)
            {
                texts[i] = header + body;
                remaining -= cost;
            }
            else
            {
                var keep = remaining - header.Length - TruncationMarker.Length - 2;
                texts[i] = header + PromptText.Clamp(body, keep, "") + TruncationMarker;
                truncated[i] = true;
                remaining = 0;
            }

            included[i] = true;
        }

        var omitted = 0;
        for (var i = 0; i < found.Count; i++)
        {
            files.Add(new InstructionsFile(found[i].Path, found[i].FileName, found[i].Global, included[i], truncated[i]));
            if (texts[i] is { } text)
            {
                chunks.Add(text);
            }
            else if (found[i].Text.Length > 0)
            {
                omitted++;
            }
        }

        if (imported is { } copy && PromptText.Sanitize(copy) is { Length: > 0 } importedText)
        {
            var header = "--- Imported instructions from another tool (the local files above take precedence over this copy) ---\n";
            var room = Math.Max(0, remaining - header.Length - TruncationMarker.Length - 2);
            if (room > 0)
            {
                chunks.Add(header + (importedText.Length > room ? PromptText.Clamp(importedText, room, "") + TruncationMarker : importedText));
            }
            else
            {
                omitted++;
            }
        }

        var result = string.Join("\n\n", chunks);
        if (omitted > 0)
        {
            result += $"\n\n[{omitted} more instruction file(s) were left out because the project instructions budget was reached]";
        }

        return new InstructionsResult(result, files, diagnostics);
    }

    private static string Header(string path, bool global) =>
        $"--- Instructions from {(global ? "the global instructions file " : "")}{path} (when instruction sections conflict, the ones further down win) ---\n";

    private static bool SamePath(EnvPath a, EnvPath b, PathStyle style) =>
        string.Equals(a.Value, b.Value, PathRules.Comparison(style));

    /// <summary>
    /// 项目是 git worktree 时(<c>&lt;root&gt;/.git</c> 是个文件,内容 <c>gitdir: &lt;主仓库&gt;/.git/worktrees/&lt;名字&gt;</c>)返回主仓库根;不是 / 读不出返回 null。
    /// </summary>
    private static async Task<EnvPath?> MainRepositoryRootAsync(IExecutionEnvironment environment, EnvPath projectRoot, CancellationToken ct)
    {
        try
        {
            var gitFile = projectRoot.Join(".git");
            var info = await environment.Fs.StatAsync(gitFile, ct).ConfigureAwait(false);
            if (!info.IsFile || info.Size > 4096)
            {
                return null;
            }

            var text = await environment.Fs.ReadTextAsync(gitFile, 4096, ct).ConfigureAwait(false);
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var gitDir = line["gitdir:".Length..].Trim().Replace('\\', '/');
            const string marker = "/.git/worktrees/";
            var at = gitDir.LastIndexOf(marker, StringComparison.Ordinal);
            if (at <= 0)
            {
                return null;
            }

            var main = gitDir[..at];
            return EnvPath.Parse(main, projectRoot.Style) is { IsAbsolute: true } root ? root : null;
        }
        catch (EnvironmentException e) when (!IsConnectionFault(e))
        {
            return null;
        }
    }

    private static bool IsConnectionFault(EnvironmentException e) =>
        e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration
            or EnvironmentErrorCode.Cancelled or EnvironmentErrorCode.ResultUnknown;

    private static async Task<(string Path, string FileName, string Text)?> ReadOneAsync(
        IExecutionEnvironment environment, EnvPath directory, List<InstructionsDiagnostic> diagnostics, CancellationToken ct)
    {
        foreach (var name in FileNames)
        {
            EnvPath file;
            try
            {
                // ★ 必须过围栏,不能 Join:软链指到目录之外 = 静默的密钥外泄。目录本身不存在时 realpath 会抛,这是常态(没建过全局目录 / 祖先目录没有这个文件)。
                file = await environment.Paths.ResolveWithinAsync(directory, name, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PathEscape)
            {
                // ★ 只说它自己那个名字,不回显目标路径
                diagnostics.Add(new InstructionsDiagnostic(directory.Join(name).Value, "instructions.escapesDirectory"));
                return null; // 它存在、只是不能用:不再往下找下一个名字(override 的意图是「用我而不是下一个」)
            }
            catch (EnvironmentException e) when (!IsConnectionFault(e))
            {
                continue;
            }

            try
            {
                var info = await environment.Fs.StatAsync(file, ct).ConfigureAwait(false);
                if (info.IsDirectory)
                {
                    diagnostics.Add(new InstructionsDiagnostic(directory.Join(name).Value, "instructions.unreadable", "is a directory"));
                    return null;
                }

                if (!info.IsFile)
                {
                    continue;
                }

                var bytes = await environment.Fs.ReadHeadAsync(file, (int)FileMaxBytes, ct).ConfigureAwait(false);
                return (directory.Join(name).Value, name, PromptText.Sanitize(Encoding.UTF8.GetString(bytes)));
            }
            catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
            {
                continue; // 没有这个文件是常态,不是错误
            }
            catch (EnvironmentException e) when (!IsConnectionFault(e))
            {
                diagnostics.Add(new InstructionsDiagnostic(directory.Join(name).Value, "instructions.unreadable", e.Code.ToString()));
                return null;
            }
        }

        return null;
    }
}
