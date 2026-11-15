using System.Diagnostics;
using System.Text.Json;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Hooks;

/// <summary>
/// 一条命令型钩子的执行器(2.x <c>kernel/hook/run.ts</c>)。<b>只经 <see cref="IExecutionEnvironment"/> 起进程</b>(AGENTS.md §5):钩子跑在项目的执行环境里,
/// 远程工作区上就跑在<b>远端</b>(载荷里的 <c>file_path</c> 是远端路径,在控制端本机跑没有意义)。
/// <para>
/// 协议:<b>stdin</b> 一行 JSON 然后关;<b>环境变量</b> <c>NCW_HOOK_EVENT / NCW_SESSION_ID / NCW_RUN_ID / NCW_PROJECT_DIR / NCW_HOOK_SCOPE</c> + 用户配置的 <c>env</c>
/// (2.x 不设环境变量,是因为它的 SSH 路径用 stdin 的第一行传环境变量、和载荷打架;3.0 的执行环境自己处理,不会出现在远端命令行里);
/// <b>exit 0</b>:stdout 若是形如 <c>{decision?, reason?, additionalContext?}</c> 的 JSON 就结构化读,否则整段当 <c>additionalContext</c>;
/// <b>exit 2</b>:阻断,stderr 当理由(空则退回 stdout);<b>其余非零 / 超时 / 起不来 / 连接中断</b>:记诊断,<b>不阻断</b>。
/// </para>
/// <para>
/// 为什么失败放行(fail-open):一条安全用途的 PreToolUse 钩子超时了,放行还是拦住?选放行——钩子最常见的故障是脚本写错、依赖没装、路径不对,
/// 而 fail-closed 的表现是「整个应用突然什么工具都用不了,且没有任何线索指向钩子」。代价用可见性补:失败进 <c>hooks.diagnostics</c>。
/// </para>
/// 输出上限之后仍持续读管道(<see cref="ProcessRunner"/> 保证:不读会把管道写满,子进程卡在 write 上永不退出,表现成「这条钩子每次都超时」);超时杀整棵进程树。
/// </summary>
public static class CommandHookRunner
{
    public static async Task<HookRunReport> RunAsync(
        IExecutionEnvironment environment, HookDefinition hook, HookScope scope, HookPayload payload, EnvPath? cwd, TimeProvider clock, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in hook.Env)
        {
            variables[name] = value;
        }

        variables["NCW_HOOK_EVENT"] = payload.Event;
        variables["NCW_SESSION_ID"] = Clean(payload.SessionId);
        variables["NCW_RUN_ID"] = Clean(payload.RunId);
        variables["NCW_PROJECT_DIR"] = Clean(payload.WorkspaceRoot);
        variables["NCW_HOOK_SCOPE"] = scope == HookScope.Global ? "global" : "project";

        var spec = new CommandSpec { ShellCommand = hook.Command, WorkingDirectory = cwd, Environment = variables, Stdin = StdinMode.Pipe };
        ProcessResult result;
        try
        {
            result = await environment.RunAsync(
                spec,
                new RunOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(hook.TimeoutMs),
                    Input = System.Text.Encoding.UTF8.GetBytes(payload.ToJson() + "\n"),
                    // 保留开头的上限字节;超出后继续消费管道,只是不再保留。
                    OutputPolicy = OutputPolicies.Head(HookLimits.StdoutMax),
                },
                ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e)
        {
            return new HookRunReport(hook.Id, scope, null, "", Cap(e.Message, HookLimits.StderrMax), Elapsed(), HookOutcome.SpawnFailed);
        }

        var stdout = Cap(result.Stdout.ToText(), HookLimits.StdoutMax);
        var stderr = Cap(result.Stderr.ToText(), HookLimits.StderrMax);
        var duration = Elapsed();
        switch (result.Outcome)
        {
            case ProcessOutcome.TimedOut or ProcessOutcome.Cancelled:
                return new HookRunReport(hook.Id, scope, result.ExitCode, stdout, stderr, duration, HookOutcome.Timeout);
            case ProcessOutcome.ConnectionLost:
                return new HookRunReport(hook.Id, scope, null, stdout, string.IsNullOrEmpty(stderr) ? "connection lost" : stderr, duration, HookOutcome.SpawnFailed);
        }

        if (result.ExitCode == 2)
        {
            // exit 2 = 阻断。理由取 stderr;它空着的话退回 stdout,总好过给用户一个空原因。
            var reason = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();
            return new HookRunReport(hook.Id, scope, 2, stdout, stderr, duration, HookOutcome.Blocked, HookDecision.Deny, reason);
        }

        if (result.ExitCode != 0)
        {
            return new HookRunReport(hook.Id, scope, result.ExitCode, stdout, stderr, duration, HookOutcome.Ok);
        }

        var parsed = ParseDecision(stdout);
        return new HookRunReport(hook.Id, scope, 0, stdout, stderr, duration, HookOutcome.Ok, parsed.Decision, parsed.Reason, parsed.AdditionalContext);
    }

    private static string Clean(string value) => value.Replace("\0", "", StringComparison.Ordinal);

    private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;

    internal readonly record struct Parsed(HookDecision? Decision, string? Reason, string? AdditionalContext);

    /// <summary>钩子 stdout 里那个可选的结构化回包。</summary>
    internal static Parsed ParseDecision(string stdout)
    {
        var text = stdout.Trim();
        if (text.Length == 0)
        {
            return default;
        }

        if (!text.StartsWith('{'))
        {
            return new Parsed(null, null, text);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Parsed(null, null, text);
            }

            HookDecision? decision = null;
            string? reason = null;
            string? context = null;
            if (doc.RootElement.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String)
            {
                decision = d.GetString() switch
                {
                    "allow" => HookDecision.Allow,
                    "deny" => HookDecision.Deny,
                    "ask" => HookDecision.Ask,
                    _ => null, // 认不出的值忽略掉,不当成 deny
                };
            }

            if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reason = r.GetString();
            }

            if (doc.RootElement.TryGetProperty("additionalContext", out var c) && c.ValueKind == JsonValueKind.String)
            {
                context = c.GetString();
            }

            // 合法 JSON、但一个认得的字段都没有——当普通文本用:不然 echo '{"foo":"bar"}' 的输出会被静默丢掉,中间没有任何报错。
            return decision is null && reason is null && context is null ? new Parsed(null, null, text) : new Parsed(decision, reason, context);
        }
        catch (JsonException)
        {
            // 看着像 JSON 但不是——当普通文本用,别把用户的输出丢掉
            return new Parsed(null, null, text);
        }
    }
}
