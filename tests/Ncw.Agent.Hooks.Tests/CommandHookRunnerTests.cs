using System.Text.Json;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Hooks.Tests;

/// <summary>
/// 命令型钩子执行器,<b>真起进程、真走 shell、真经 LocalEnvironment</b>。移植自 2.x 的 <c>hook-run.test.ts</c>(协议)和 <c>hook-run-integration.test.ts</c>(真进程)两份:
/// 2.x 把「协议」(假进程)和「协议之外的那一半」(真进程)分两份测,是因为假进程测不出 stdin 有没有真的被脚本读到、<c>exit 2</c> 在真 shell 下是不是 2、
/// 超时的时候进程树是不是真的死了——3.0 的执行环境本身就是可以真跑的,所以两份并成一份,全部真跑。
/// </summary>
public sealed class CommandHookRunnerTests : IAsyncLifetime
{
    private readonly HookFixture _fx = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    private Task<HookRunReport> RunAsync(string command, int timeoutMs = 10_000, HookPayload? payload = null, IReadOnlyDictionary<string, string>? env = null, HookScope scope = HookScope.Project)
    {
        var hook = HookFixture.Command(command, timeoutMs: timeoutMs) with { Env = env ?? HookDefinition.EmptyEnv };
        return CommandHookRunner.RunAsync(_fx.Env, hook, scope, payload ?? HookFixture.Payload(), _fx.RootPath, TimeProvider.System, CancellationToken.None);
    }

    // ───────────────────────── 协议 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Stdin_carries_the_one_line_of_json_and_the_script_really_reads_it()
    {
        var report = await RunAsync("cat");

        Assert.Equal(0, report.ExitCode);
        Assert.EndsWith("\n", report.Stdout);
        using var doc = JsonDocument.Parse(report.Stdout);
        Assert.Equal("PreToolUse", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("Bash", doc.RootElement.GetProperty("toolName").GetString());
        Assert.Equal("echo hello", doc.RootElement.GetProperty("toolInput").GetProperty("command").GetString());
        Assert.Equal("project", doc.RootElement.GetProperty("scope").GetString());
        Assert.Single(report.Stdout.TrimEnd().Split('\n'));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_script_can_pull_a_field_out_of_the_payload_to_decide()
    {
        var report = await RunAsync("""grep -q '"toolInternalId":"Bash"' && echo hit || echo miss""");

        Assert.Equal("hit", report.AdditionalContext);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Model_produced_tool_input_is_json_escaped_in_the_payload_whatever_it_contains()
    {
        var nasty = "line1\nline2 \"quoted\" \\ back\tslash \u0001 emoji \U0001F600 </script> $(rm -rf /)";
        var input = JsonDocument.Parse(new System.Text.Json.Nodes.JsonObject { ["command"] = nasty }.ToJsonString()).RootElement.Clone();
        var payload = HookFixture.Payload() with { ToolInput = input, ToolOutput = nasty };

        var report = await RunAsync("cat", payload: payload);

        using var doc = JsonDocument.Parse(report.Stdout);
        Assert.Equal(nasty, doc.RootElement.GetProperty("toolInput").GetProperty("command").GetString());
        Assert.Equal(nasty, doc.RootElement.GetProperty("toolOutput").GetString());
        Assert.Single(report.Stdout.TrimEnd().Split('\n'));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task The_command_runs_through_a_shell_with_the_project_root_as_the_working_directory()
    {
        var report = await RunAsync("echo hi && pwd");

        Assert.Equal("hi\n" + _fx.Root, report.AdditionalContext);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Exit_zero_with_plain_text_makes_the_whole_text_additional_context()
    {
        var report = await RunAsync("echo '提醒:先跑 lint'");

        Assert.Equal(HookOutcome.Ok, report.Outcome);
        Assert.Equal("提醒:先跑 lint", report.AdditionalContext);
        Assert.Null(report.Decision);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Exit_zero_with_structured_json_is_read_field_by_field()
    {
        var report = await RunAsync("""printf '{"decision":"ask","reason":"take a look","additionalContext":"fyi"}'""");

        Assert.Equal(HookDecision.Ask, report.Decision);
        Assert.Equal("take a look", report.Reason);
        Assert.Equal("fyi", report.AdditionalContext);
        Assert.False(report.Blocks);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_structured_deny_blocks_even_with_exit_zero()
    {
        var report = await RunAsync("""printf '{"decision":"deny","reason":"nope"}'""");

        Assert.True(report.Blocks);
        Assert.Equal("nope", report.Reason);
    }

    [Theory(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    [InlineData("""printf '{ not finished'""", "{ not finished")]
    [InlineData("""printf '{"foo":"bar"}'""", """{"foo":"bar"}""")]
    [InlineData("""printf '["a"]'""", """["a"]""")]
    public async Task Output_that_looks_like_json_but_has_no_known_field_is_kept_as_text_not_silently_dropped(string command, string expected)
    {
        var report = await RunAsync(command);

        Assert.Equal(expected, report.AdditionalContext);
        Assert.Null(report.Decision);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task An_unrecognised_decision_value_is_ignored_it_is_not_treated_as_deny()
    {
        var report = await RunAsync("""printf '{"decision":"maybe"}'""");

        Assert.Null(report.Decision);
        Assert.False(report.Blocks);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Exit_two_is_a_block_and_stderr_is_the_reason()
    {
        var report = await RunAsync("echo 'touches the production db' >&2; exit 2");

        Assert.Equal(HookOutcome.Blocked, report.Outcome);
        Assert.Equal(HookDecision.Deny, report.Decision);
        Assert.Equal("touches the production db", report.Reason);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Exit_two_with_an_empty_stderr_falls_back_to_stdout_for_the_reason()
    {
        var report = await RunAsync("echo 'blocked by policy'; exit 2");

        Assert.Equal("blocked by policy", report.Reason);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Any_other_non_zero_exit_does_not_block_a_command_not_found_must_not_stop_the_whole_run()
    {
        var report = await RunAsync("echo not found >&2; exit 127");

        Assert.Equal(HookOutcome.Ok, report.Outcome);
        Assert.Null(report.Decision);
        Assert.Equal(127, report.ExitCode);
        Assert.Equal("not found", report.Stderr.Trim());
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_command_that_does_not_exist_does_not_block()
    {
        var report = await RunAsync("this-command-certainly-does-not-exist-xyzzy");

        Assert.Equal(HookOutcome.Ok, report.Outcome);
        Assert.NotEqual(0, report.ExitCode);
        Assert.False(report.Blocks);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_process_that_cannot_start_is_a_spawn_failure_and_does_not_block()
    {
        var hook = HookFixture.Command("echo hi");
        var missing = EnvPath.Parse(Path.Combine(_fx.Base, "no-such-dir"), HookFixture.Style);

        var report = await CommandHookRunner.RunAsync(_fx.Env, hook, HookScope.Project, HookFixture.Payload(), missing, TimeProvider.System, CancellationToken.None);

        Assert.Equal(HookOutcome.SpawnFailed, report.Outcome);
        Assert.False(report.Blocks);
    }

    // ───────────────────────── 环境变量 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task The_agent_sets_event_session_run_project_and_scope_variables()
    {
        var payload = HookFixture.Payload(HookEvents.PostToolUse, HookScope.Global);

        var report = await RunAsync("""echo "$NCW_HOOK_EVENT|$NCW_SESSION_ID|$NCW_RUN_ID|$NCW_PROJECT_DIR|$NCW_HOOK_SCOPE" """, payload: payload, scope: HookScope.Global);

        Assert.Equal("PostToolUse|s1|r1|/ws|global", report.AdditionalContext);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task User_configured_variables_reach_the_script_and_never_override_the_reserved_ones()
    {
        var env = new Dictionary<string, string> { ["MY_TOKEN"] = "s3cret-value", ["NCW_RUN_ID"] = "spoofed" };

        var report = await RunAsync("""echo "$MY_TOKEN|$NCW_RUN_ID" """, env: env);

        // 保存时 NCW_ 前缀就被拒绝(HookValidation);即使绕过校验直接构造,执行器也让 Agent 自己的值胜出。
        Assert.Equal("s3cret-value|r1", report.AdditionalContext);
    }

    // ───────────────────────── 超时 / 中断 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_timeout_kills_the_whole_process_tree_not_just_the_shell_and_does_not_block()
    {
        var pidFile = Path.Combine(_fx.Base, "child.pid");
        var started = DateTime.UtcNow;

        // sleep 是 sh 的子进程:只杀 sh 的话它会活下来。
        var report = await RunAsync($"sleep 30 & echo $! > '{pidFile}'; wait", timeoutMs: 700);

        Assert.Equal(HookOutcome.Timeout, report.Outcome);
        Assert.False(report.Blocks);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(400);
        Assert.False(IsAlive(pid), "the grandchild must be gone");
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Cancelling_kills_the_process_and_reports_it_as_a_timeout_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        var hook = HookFixture.Command("sleep 30");
        var task = CommandHookRunner.RunAsync(_fx.Env, hook, HookScope.Project, HookFixture.Payload(), _fx.RootPath, TimeProvider.System, cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();

        var report = await task;

        Assert.Equal(HookOutcome.Timeout, report.Outcome);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ───────────────────────── 输出上限 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Stdout_over_the_cap_is_cut_but_the_pipe_is_still_drained_so_the_child_exits_normally()
    {
        // 吐 200KB,远超 8KB 上限:不读管道的话子进程会卡在 write 上,表现成「这条钩子每次都超时」
        var report = await RunAsync("i=0; while [ $i -lt 200 ]; do printf '%1024d' 0; i=$((i+1)); done");

        Assert.Equal(HookOutcome.Ok, report.Outcome);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(HookLimits.StdoutMax, report.Stdout.Length);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Stderr_is_capped_at_four_kilobytes()
    {
        var report = await RunAsync("i=0; while [ $i -lt 20 ]; do printf '%1024d' 0 >&2; i=$((i+1)); done; exit 1");

        Assert.Equal(HookLimits.StderrMax, report.Stderr.Length);
    }

    // ───────────────────────── 载荷 ─────────────────────────

    [Fact]
    public void The_payload_json_omits_what_the_event_does_not_have_and_keeps_the_two_x_key_names()
    {
        var stop = new HookPayload { Event = "Stop", SessionId = "s", RunId = "r", WorkspaceRoot = "/w", Scope = HookScope.Global, Status = "done", StopHookActive = true };
        var notification = new HookPayload { Event = "Notification", SessionId = "s", RunId = "r", WorkspaceRoot = "/w", Scope = HookScope.Project, Notification = new HookNotification("tool_permission", "Bash") };

        using var a = JsonDocument.Parse(stop.ToJson());
        using var b = JsonDocument.Parse(notification.ToJson());

        Assert.True(a.RootElement.GetProperty("stop_hook_active").GetBoolean());
        Assert.Equal("done", a.RootElement.GetProperty("status").GetString());
        Assert.False(a.RootElement.TryGetProperty("toolName", out _));
        Assert.Equal("tool_permission", b.RootElement.GetProperty("notification").GetProperty("kind").GetString());
        Assert.Equal("Bash", b.RootElement.GetProperty("notification").GetProperty("toolName").GetString());
    }

    [Fact]
    public void The_sample_payload_is_shared_by_preview_and_real_runs_and_has_a_tool_for_tool_events()
    {
        var pre = HookPayload.Sample(HookEvents.PreToolUse, HookScope.Project, "/ws");
        var prompt = HookPayload.Sample(HookEvents.UserPromptSubmit, HookScope.Project, "/ws");
        var stop = HookPayload.Sample(HookEvents.Stop, HookScope.Global, "/ws");

        Assert.Equal("Bash", pre.ToolName);
        Assert.NotNull(prompt.Prompt);
        Assert.Null(prompt.ToolName);
        Assert.Equal(false, stop.StopHookActive);
    }

    [Fact]
    public void ParseDecision_reads_updatedInput_and_claude_code_hookSpecificOutput()
    {
        var flat = CommandHookRunner.ParseDecision("""{"decision":"allow","reason":"rewritten","additionalContext":"note","updatedInput":{"command":"echo safe"}}""");
        Assert.Equal(HookDecision.Allow, flat.Decision);
        Assert.Equal("rewritten", flat.Reason);
        Assert.Equal("note", flat.AdditionalContext);
        Assert.Equal("echo safe", flat.UpdatedInput!.Value.GetProperty("command").GetString());

        var specific = CommandHookRunner.ParseDecision("""
            {"hookSpecificOutput":{"permissionDecision":"ask","permissionDecisionReason":"check this","additionalContext":"extra","updatedInput":{"command":"ls -la"}}}
            """);
        Assert.Equal(HookDecision.Ask, specific.Decision);
        Assert.Equal("check this", specific.Reason);
        Assert.Equal("extra", specific.AdditionalContext);
        Assert.Equal("ls -la", specific.UpdatedInput!.Value.GetProperty("command").GetString());
    }

    [Fact]
    public void Payload_json_includes_precompact_and_subagent_start_fields_when_present_only()
    {
        var compact = new HookPayload { Event = HookEvents.PreCompact, SessionId = "s", RunId = "r", WorkspaceRoot = "/w", Scope = HookScope.Project, Trigger = "manual", CustomInstructions = "focus on api" };
        var subStart = new HookPayload { Event = HookEvents.SubagentStart, SessionId = "s", RunId = "r", WorkspaceRoot = "/w", Scope = HookScope.Project, AgentType = "code-reviewer" };
        var subStop = new HookPayload { Event = HookEvents.SubagentStop, SessionId = "s", RunId = "r", WorkspaceRoot = "/w", Scope = HookScope.Project, Status = "done" };

        using var cDoc = JsonDocument.Parse(compact.ToJson());
        using var sDoc = JsonDocument.Parse(subStart.ToJson());
        using var eDoc = JsonDocument.Parse(subStop.ToJson());

        Assert.Equal("manual", cDoc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("focus on api", cDoc.RootElement.GetProperty("custom_instructions").GetString());
        Assert.Equal("code-reviewer", sDoc.RootElement.GetProperty("agent_type").GetString());
        Assert.False(eDoc.RootElement.TryGetProperty("agent_type", out _));
    }
}
