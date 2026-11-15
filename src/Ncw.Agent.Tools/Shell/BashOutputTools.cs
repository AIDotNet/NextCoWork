using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>
/// <c>BashOutput</c> / <c>KillShell</c>:<c>Bash(run_in_background)</c> 的另外两半。后台命令返回的只是一个 id,它此后的一切(有没有起来、报了什么错、什么时候该收工)
/// 都得有路可问、有路可停。<b>两个工具,不是一个带 <c>action</c> 的</b>:<c>ReadOnly</c> 是工具级的静态标记,计划模式按它摘工具、权限闸门按它决定要不要弹审批;
/// 合成一个的话只能整体标成「写」,计划模式下连「那个构建跑完没有」都查不了。没有 <c>list</c> 工具:id 在 Bash 的返回里,找不到时错误消息会把现有的全列出来。
/// </summary>
public sealed class BashOutputTool() : BuiltinTool(
    "BashOutput",
    "Reads output from a background shell started by Bash with run_in_background.\n\n"
    + "- Each call returns ONLY the output that arrived since your previous call on that shell, so poll it instead of re-reading from the start\n"
    + "- Returns the shell status too: running, exited (with its exit code), or killed. A shell that has exited can still be read once for whatever it printed last\n"
    + "- filter is an optional regular expression applied per line; only matching lines are returned. Output that does not match is DISCARDED, not saved for the next call\n"
    + "- An empty result while the shell is still running means nothing new was printed. Do not poll it in a tight loop; do something else and check again later",
    S.Build(S.Object(
        ["bash_id"],
        ("bash_id", S.String("The id returned when the command was started", minLength: 1)),
        ("filter", S.String("Optional regular expression; only output lines matching it are returned")))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Read new output from a background shell",
    [])
{
    internal const string Unavailable = "Background shells are unavailable in this session. Do not retry — run commands in the foreground instead.";

    /// <summary>没有注册表的环境里整体不下发,而不是下发了再失败。</summary>
    public override bool IsEnabled(ToolContext ctx) => ctx is ExecutionToolContext { Runtime.Shells: not null };

    protected override ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        if (rt.Shells is not { } shells)
        {
            throw Fail(Unavailable);
        }

        Regex? filter = null;
        if (args.Str("filter") is { Length: > 0 } pattern)
        {
            try
            {
                // 优先线性时间引擎;只能回溯的写法先过静态筛查再带超时
                filter = Redos.Compile(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw Fail($"Invalid filter regular expression: {e.Message}");
            }
        }

        ShellRead read;
        try
        {
            read = shells.Read(RequiredString(args, "bash_id"), filter);
        }
        catch (ShellRegistryException e)
        {
            throw Fail(e.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            throw Fail("The filter regular expression took too long to evaluate and was aborted. Use a simpler filter.");
        }

        var info = read.Info;
        var state = info.Status switch
        {
            BackgroundShellStatus.Exited => $"exited with code {(info.ExitCode is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}",
            BackgroundShellStatus.Killed => "was killed",
            _ => "is still running",
        };
        var parts = new[] { Section("stdout", read.Stdout), Section("stderr", read.Stderr) }.Where(s => s.Length > 0).ToList();
        var dropped = read.Dropped
            ? "\nNOTE: this shell produced more output than the buffer holds; the OLDEST part was dropped and is gone."
            : "";
        var text = $"Shell {info.Id} ({info.Command}) {state}.\n"
            + (parts.Count == 0 ? "(no new output since the last read)" : string.Join('\n', parts))
            + dropped;
        var details = Details(rt, "bashOutput", w =>
        {
            w.WriteString("shellId", info.Id);
            w.WriteString("status", info.Status.ToString().ToLowerInvariant());
            if (info.ExitCode is { } code)
            {
                w.WriteNumber("exitCode", code);
            }

            w.WriteBoolean("dropped", read.Dropped);
        });
        return ValueTask.FromResult(ToolResult.Text(text, details));
    }

    private static string Section(string title, string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length == 0 ? "" : $"<{title}>\n{Text.ClipWithEllipsis(trimmed, ShellLimits.MaxReadChars)}\n</{title}>";
    }
}

public sealed class KillShellTool() : BuiltinTool(
    "KillShell",
    "Stops a background shell started by Bash with run_in_background, killing its whole process tree.\n\n"
    + "- Kill a shell as soon as you no longer need it. A dev server you forget about keeps holding its port after this conversation ends\n"
    + "- Killing an already finished shell is not an error; it reports the status it already had\n"
    + "- Any output it printed but you never read is discarded. Call BashOutput first if you still need it",
    S.Build(S.Object(
        ["shell_id"],
        ("shell_id", S.String("The id of the background shell to stop", minLength: 1)))),
    // 不是 destructive:它能造成的最大后果是「一个后台进程提前结束」,而那个进程正是模型自己起的。
    // 标成破坏性会让 auto 档每次收工都弹一次审批,于是模型学会不收工 —— 那比多杀一个 dev server 坏得多。
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: false),
    "Stop a background shell",
    [])
{
    public override bool IsEnabled(ToolContext ctx) => ctx is ExecutionToolContext { Runtime.Shells: not null };

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        if (rt.Shells is not { } shells)
        {
            throw Fail(BashOutputTool.Unavailable);
        }

        BackgroundShellInfo info;
        try
        {
            info = await shells.KillAsync(RequiredString(args, "shell_id"), ct).ConfigureAwait(false);
        }
        catch (ShellRegistryException e)
        {
            throw Fail(e.Message);
        }

        var details = Details(rt, "killShell", w =>
        {
            w.WriteString("shellId", info.Id);
            w.WriteString("status", info.Status.ToString().ToLowerInvariant());
        });
        return ToolResult.Text(
            info.Status == BackgroundShellStatus.Killed
                ? $"Killed shell {info.Id} ({info.Command}) and its process tree."
                : $"Shell {info.Id} ({info.Command}) had already finished ({info.Status.ToString().ToLowerInvariant()}); nothing to kill.",
            details);
    }
}
