using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>
/// <c>Bash</c>:在项目的执行环境里跑一条 shell 命令。名字、参数(<c>command / timeout / description / run_in_background</c>)对齐 Claude Code,
/// 但有两处必须照实说的差异,写进描述里(每一处说错了,模型都会按 CC 的假设行事,然后拿到一个它无法归因的结果):
/// <list type="number">
/// <item><b>不是持久会话</b>:每次调用现开一个 shell。不说的话,模型会先 <c>cd</c> 再在下一次调用里写相对路径 —— 文件安安静静地落在项目根上,而两次调用都返回成功。</item>
/// <item><b>stdin 是关着的</b>:任何等输入的命令会挂到超时。</item>
/// </list>
/// 输出<b>保留尾部</b>,完整输出存环境里的临时文件并在结果里给出路径(pi)。
/// <c>run_in_background</c> 没有后台 Shell 能力(<see cref="ToolRuntime.Shells"/> 为 null)时<b>显式失败</b>,不静默降级成前台执行 —— 降级同样会让模型以为自己起了后台服务。
/// 取消(整轮中止)原样抛 <see cref="OperationCanceledException"/>;用户只停这一条命令则是一个说清楚「不是命令坏了、不要重试」的工具错误。
/// </summary>
public sealed class BashTool() : BuiltinTool(
    "Bash",
    "Executes a shell command in the workspace directory of the execution environment, with a timeout and an output cap.\n\n"
    + "Before you run anything:\n"
    + "1. VERIFY THE DIRECTORY. If the command creates files in a new directory, first use LS to confirm the parent exists and is where you think it is.\n"
    + "2. VERIFY THE COMMAND. It runs directly on the user's machine (or the remote host of this project). If it deletes data, changes global configuration, or sends anything outward, tell the user what it does before running it.\n\n"
    + "Usage notes:\n"
    + $"- command is required. timeout is optional, in milliseconds, up to {ShellLimits.MaxTimeoutMs} (10 minutes); it defaults to {ShellLimits.DefaultTimeoutMs} (2 minutes)\n"
    + "- Write a 5-10 word description; that is REQUIRED and is what the user sees in the UI\n"
    + "- Use the Shell identified in the Environment section. The tool name Bash does not imply Bash or POSIX syntax.\n"
    + $"- When a stream produces more than {ShellLimits.MaxOutputChars} characters you get the LAST {ShellLimits.MaxOutputChars}; the complete output is saved to a file whose path is given in the result, so you can Read or Grep it\n"
    + "- IMPORTANT: EVERY CALL GETS A FRESH SHELL AND KEEPS NO STATE. A cd, an export, or a variable you set in one call does not exist in the next. "
    + "The working directory is always the workspace root, so use absolute or workspace-relative paths instead of relying on cd. "
    + "To run somewhere else, change directory and execute the command inside a SINGLE call using the current shell syntax\n"
    + "- IMPORTANT: STDIN IS CLOSED. Any command that waits for input will hang until it times out: pass -m to git commit, pass --yes / --no-input to package managers, and never run something that needs an interactive login\n"
    + "- Set run_in_background for a command that does not finish on its own — a dev server, a watcher, a long build you want to keep working alongside. "
    + "It returns a shell id immediately; read its output with BashOutput and stop it with KillShell. "
    + "NEVER background a command you need the result of right now, and NEVER append & to fake it: a backgrounded shell is the only one whose output can still be read\n"
    + "- VERY IMPORTANT: NEVER use shell `find` or `grep` to search — use Grep and Glob. NEVER use `cat`, `head`, `tail`, or `ls` to read files and list directories — use Read and LS. "
    + "Those tools apply the ignore list, skip binaries, add line numbers, and enforce a timeout; the shell equivalents do none of that\n"
    + "- Chain commands using syntax supported by the current shell. Windows PowerShell 5 does not support `&&`; use an explicit success check when later commands depend on earlier ones succeeding\n"
    + "- Quote paths that contain spaces: `cd \"path with spaces\"`\n"
    + "- A non-zero exit code comes back to you as an error, with stdout and stderr included\n"
    + "- The user can stop a single running command from the UI. That comes back as a tool error saying so; it is not a failure of the command and must not be retried\n"
    + "- The user can also move a running command to the background from the UI. You then get its output so far and a shell id; it is still running, so read the rest with BashOutput instead of running it again",
    S.Build(S.Object(
        ["command"],
        ("command", S.String("The command to execute", minLength: 1)),
        ("timeout", S.Integer($"Optional timeout in milliseconds, up to {ShellLimits.MaxTimeoutMs}. Defaults to {ShellLimits.DefaultTimeoutMs}", minimum: 1, maximum: ShellLimits.MaxTimeoutMs)),
        // description 在描述里写成必填(与 CC 不同,是有意的),但在校验上缺席时补空串,不拒绝调用:
        // 省掉它是模型的疏忽,不是用户的;因为它缺席就让整条命令失败,用户看到的是「我的命令怎么跑不了了」,而他什么都没做错。
        ("description", S.String("REQUIRED — always write it. What this command accomplishes, in 5-10 words, written for a person: say the goal, not the flags. This is the only thing the user sees before expanding the call", maxLength: 200, @default: "")),
        ("run_in_background", S.Boolean("Set to true to start the command and return immediately. Use BashOutput to read its output later, and KillShell to stop it. timeout does not apply to a background command")))),
    new ToolTraits(ReadOnly: false, Destructive: true, NeedsNetwork: false),
    "Run a shell command (fresh shell per call, stdin closed)",
    [
        "Use Grep, Glob, Read and LS instead of grep, find, cat and ls through Bash.",
        "Every Bash call starts a fresh shell in the workspace root; chain dependent commands in one call.",
    ])
{
    /// <summary>用户点了这张卡片上的停止按钮之后给模型的那句话:是<b>用户</b>停的、<b>不是</b>命令坏了、<b>不要</b>自动重试。</summary>
    internal const string StoppedByUser =
        "The user stopped this command from the UI. It did not fail — do not run it again or work around it. Ask what to do next, or continue with something else.";

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var command = RequiredString(args, "command");
        var description = args.Str("description") ?? "";
        var timeoutMs = OptionalInt(args, "timeout") ?? ShellLimits.DefaultTimeoutMs;
        if (rt.Root is not { } cwd)
        {
            throw Fail(ToolPaths.NoWorkspace);
        }

        var shells = rt.Shells;
        var background = args.Bool("run_in_background") == true;

        // 模型省了 description 时界面退回去画命令主干;状态行这里同样要有兜底
        var detachable = !background && shells is not null;
        ctx.Progress.Report(new ToolProgress(
            description.Length == 0 ? Text.ClipWithEllipsis(command, 80) : description,
            Details: detachable ? Jsn.Parse("""{"detachable":true}""") : null));

        if (background)
        {
            if (shells is null)
            {
                throw Fail("Background commands are unavailable in this environment. Run the command in the foreground instead, with a timeout that fits it.");
            }

            try
            {
                var shell = await shells.StartAsync(new ShellStartRequest(command, cwd, description.Length == 0 ? null : description, ctx.RunId, ctx.CallId), rt.Environment, ct).ConfigureAwait(false);
                var details = Details(rt, "bash", w =>
                {
                    WriteCommand(w, command, description, cwd);
                    w.WriteString("outcome", "background");
                    w.WriteString("shellId", shell.Id);
                });
                return ToolResult.Text(
                    $"Started in the background as shell id {shell.Id}.\n"
                    + $"Read its new output with BashOutput({{ bash_id: \"{shell.Id}\" }}) — each read returns only what arrived since the previous one. "
                    + "Stop it with KillShell when you are done; it keeps running across turns until then.",
                    details);
            }
            catch (ShellRegistryException e)
            {
                throw Fail(e.Message);
            }
        }

        return await RunForegroundAsync(ctx, command, description, cwd, timeoutMs, detachable ? shells : null, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> RunForegroundAsync(ExecutionToolContext ctx, string command, string description, EnvPath cwd, int timeoutMs, IShellRegistry? shells, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var env = rt.Environment;
        var started = rt.Clock.GetTimestamp();
        var spillPath = rt.ResolveTempDirectory().Join($"ncw-bash-{Guid.NewGuid():N}.log");
        var process = await env.OpenProcessAsync(new CommandSpec { ShellCommand = command, WorkingDirectory = cwd }, ct).ConfigureAwait(false);
        var capture = new ShellCapture(process, new SpillTarget(env.Fs, spillPath, ShellLimits.MaxOutputChars));
        capture.Start();

        var adopted = false;
        var keepSpill = false;
        try
        {
            // 「停这一条」与「停整轮」是两个信号,不能合成一个:整轮中止原样往上抛;而卡片上那颗停止按钮只想掐掉这一条命令,Run 要继续,所以翻译成一次工具失败
            var userStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var detach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hold = shells?.Hold(new ForegroundCall(ctx.RunId, ctx.CallId, command), () => userStop.TrySetResult(), () => detach.TrySetResult());
            var runCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), runCancelled).ConfigureAwait(false);
            using var timeoutCts = new CancellationTokenSource();
            var timeout = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), timeoutCts.Token);

            await Task.WhenAny(process.Exited, timeout, runCancelled.Task, userStop.Task, detach.Task).ConfigureAwait(false);
            await timeoutCts.CancelAsync().ConfigureAwait(false);

            string outcome;
            if (process.Exited.IsCompleted)
            {
                outcome = "exited";
            }
            else if (runCancelled.Task.IsCompleted)
            {
                outcome = "cancelled";
            }
            else if (userStop.Task.IsCompleted)
            {
                outcome = "stopped";
            }
            else if (detach.Task.IsCompleted)
            {
                outcome = "detached";
            }
            else
            {
                outcome = "timedOut";
            }

            if (outcome == "detached")
            {
                return await AdoptAsync(ctx, command, description, cwd, process, capture, shells!, () => adopted = true).ConfigureAwait(false);
            }

            if (outcome is "cancelled" or "stopped" or "timedOut")
            {
                // 杀整棵进程树,返回时进程已经结束(或连接已断开)
                await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ProcessExit exit;
            try
            {
                exit = await process.Exited.WaitAsync(KillWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                exit = new ProcessExit(null, null, ProcessExitReason.Killed);
            }

            // 进程已经没了,但后台子进程可能还攥着管道:给读取一个宽限期,超时就放弃
            await capture.DrainAsync(DrainGrace).ConfigureAwait(false);

            if (outcome == "cancelled")
            {
                // 整轮中止:原样抛上去,不包装成工具失败(R15)
                throw new OperationCanceledException(ct);
            }

            if (outcome == "stopped")
            {
                return ToolResult.Error(StoppedByUser, Details(rt, "bash", w =>
                {
                    WriteCommand(w, command, description, cwd);
                    w.WriteString("outcome", "stopped");
                }));
            }

            var elapsed = (long)rt.Clock.GetElapsedTime(started).TotalMilliseconds;
            var file = await capture.StopSpillAsync().ConfigureAwait(false);
            var (stdoutTail, stderrTail) = capture.SnapshotTails();
            var truncated = false;
            var stdout = Section("stdout", stdoutTail, file, ref truncated);
            var stderr = Section("stderr", stderrTail, file, ref truncated);
            keepSpill = truncated && file is not null;

            var sections = new[] { stdout, stderr }.Where(s => s.Length > 0).ToList();
            var body = string.Join('\n', sections);
            var lost = exit.Reason == ProcessExitReason.ConnectionLost;
            string? head = null;
            if (outcome == "timedOut")
            {
                head = $"Command timed out after {timeoutMs}ms; the whole process tree was killed.";
            }
            else if (lost)
            {
                head = "The connection to the execution environment was lost while the command was running; its outcome is unknown. Check the state before you retry.";
            }
            else if (exit.ExitCode is { } code and not 0)
            {
                head = $"Command exited with code {code}.";
            }
            else if (exit.ExitCode is null)
            {
                head = exit.Signal is { } signal ? $"Command was terminated by signal {signal}." : "Command ended without an exit code.";
            }

            var details = Details(rt, "bash", w =>
            {
                WriteCommand(w, command, description, cwd);
                w.WriteString("outcome", outcome == "timedOut" ? "timedOut" : lost ? "connectionLost" : "exited");
                if (exit.ExitCode is { } c)
                {
                    w.WriteNumber("exitCode", c);
                }

                if (exit.Signal is { } sig)
                {
                    w.WriteNumber("signal", sig);
                }

                w.WriteNumber("durationMs", elapsed);
                w.WriteBoolean("truncated", truncated);
                w.WriteNumber("totalBytes", capture.TotalBytes);
                if (keepSpill)
                {
                    w.WriteString("outputFile", file!);
                }
            });

            ToolResult result;
            if (head is null)
            {
                result = ToolResult.Text(body.Length == 0 ? "(command succeeded with no output)" : body, details);
            }
            else
            {
                result = ToolResult.Error(body.Length == 0 ? $"{head} (no output)" : $"{head}\n{body}", details);
            }

            return truncated ? result with { Truncated = true, OriginalBytes = capture.TotalBytes } : result;
        }
        finally
        {
            if (!adopted)
            {
                var file = await capture.StopSpillAsync().ConfigureAwait(false);
                if (file is not null && !keepSpill)
                {
                    await DeleteQuietlyAsync(env.Fs, spillPath).ConfigureAwait(false);
                }

                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 用户把一条前台命令转去后台:收编进程(连同已经收集的输出),告诉模型它现在是哪个 shell id、还在跑、怎么读它 / 停它。
    /// 收编失败时<b>杀掉它</b>再报错:此刻进程已经脱离了前台,没人杀的话它会成为一个谁也看不见、也停不了的孤儿。
    /// </summary>
    private static async Task<ToolResult> AdoptAsync(ExecutionToolContext ctx, string command, string description, EnvPath cwd, IEnvProcess process, ShellCapture capture, IShellRegistry shells, Action markAdopted)
    {
        var rt = ctx.Runtime;
        await capture.StopSpillAsync().ConfigureAwait(false);
        var request = new ShellStartRequest(command, cwd, description.Length == 0 ? null : description, ctx.RunId, ctx.CallId);
        BackgroundShellInfo info;
        string so;
        string se;
        try
        {
            // 先取「到目前为止的输出」再收编:之后的新输出由后台缓冲接手,BashOutput 只读这之后的
            var take = capture.Take(null);
            (so, se) = (take.Stdout, take.Stderr);
            info = shells.Adopt(new DetachedShell(rt.Environment, process, capture), request);
        }
        catch (ShellRegistryException e)
        {
            await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
            return ToolResult.Error($"The user tried to move this command to the background, but it could not be adopted, so it was stopped: {e.Message}");
        }

        markAdopted();
        var parts = new[] { ClipSection("stdout", so), ClipSection("stderr", se) }.Where(s => s.Length > 0).ToList();
        var details = Details(rt, "bash", w =>
        {
            WriteCommand(w, command, description, cwd);
            w.WriteString("outcome", "detached");
            w.WriteString("shellId", info.Id);
        });
        return ToolResult.Text(
            $"The user moved this command to the background while it was still running. It keeps running as shell id {info.Id}; its timeout no longer applies.\n"
            + (parts.Count == 0 ? "(no output so far)\n" : $"Output so far:\n{string.Join('\n', parts)}\n")
            + $"Read its new output with BashOutput({{ bash_id: \"{info.Id}\" }}) — each read returns only what arrived since the previous one. Stop it with KillShell when you no longer need it.",
            details);
    }

    private static string ClipSection(string title, string body)
    {
        var text = Text.StripAnsi(body).Trim();
        return text.Length == 0 ? "" : $"<{title}>\n{Text.Tail(text, ShellLimits.MaxOutputChars)}\n</{title}>";
    }

    /// <summary>
    /// 一段输出的呈现:空的时候不输出(调用方会说「没有输出」),超长时<b>保留尾部</b>并指向完整输出文件。
    /// 剥 ANSI 在截断之前:不然那 30K 的预算会被转义序列吃掉一大截。
    /// </summary>
    private static string Section(string title, OutputSnapshot snapshot, string? file, ref bool truncated)
    {
        var body = Text.StripAnsi(snapshot.ToText()).Trim();
        var cut = snapshot.Truncated;
        if (body.Length > ShellLimits.MaxOutputChars)
        {
            body = Text.Tail(body, ShellLimits.MaxOutputChars);
            cut = true;
        }

        if (body.Length == 0)
        {
            return "";
        }

        var note = "";
        if (cut)
        {
            truncated = true;
            note = $"[{title} truncated: showing the last {body.Length} characters of {Text.HumanSize(snapshot.TotalBytes)}."
                + (file is null ? "" : $" The full output (stdout and stderr interleaved) is saved at {file}")
                + "]\n";
        }

        return $"<{title}>\n{note}{body}\n</{title}>";
    }

    private static async Task DeleteQuietlyAsync(IEnvironmentFs fs, EnvPath path)
    {
        try
        {
            await fs.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (EnvironmentException)
        {
            // 临时文件清不掉不是命令的问题
        }
    }

    private static void WriteCommand(Utf8JsonWriter w, string command, string description, EnvPath cwd)
    {
        w.WriteString("command", command);
        if (description.Length > 0)
        {
            w.WriteString("description", description);
        }

        w.WriteString("cwd", cwd.Value);
    }
}
