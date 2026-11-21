using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;
using Ncw.Agent.Extensions.Planning;

namespace Ncw.Agent.Hooks.Tests;

/// <summary>
/// Hooks 接进权限链和会话扩展点之后的端到端行为:真 <c>AgentSession</c> / <c>RunRegistry</c> + 真 SQLite + 假上游 + 真实工具(Bash / Write 跑在 LocalEnvironment 上)+
/// 真实权限扩展和交互闸门 + 真子进程脚本 + 计划 / 待办 / 目标。「脚本写标记文件」用来证明它到底<b>有没有被执行</b>(deny 规则命中时钩子一次都不该跑)。
/// 其中「Stop 钩子 × 目标 / 待办共存」几条是 Planning 子代理留下、随 Hooks 移植的用例(2.x:<c>goal-wiring.test.ts</c> 的 stopOnceHook 两条、<c>goal-runtime.test.ts</c> 的「goal 钩子摘掉、别的 Stop 钩子照跑」)。
/// </summary>
public sealed class HookWiringTests : IAsyncLifetime
{
    private const string S = "s1";
    private const string Goal = "make the tests green (the transcript has this run's results)";

    private readonly E2eRig _rig = new();

    public ValueTask InitializeAsync()
    {
        _rig.NewSessionRow(S);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static Script Say(string text) => Up.Text(text);

    private string Marker(string name) => Path.Combine(_rig.WorkRoot, name);

    private void ProjectSettings(string json) => _rig.Put(Path.Combine(".next-cowork", "settings.local.json"), json);

    private static string BashArgs(string command) => J.Obj(("command", command));

    private static string WriteArgs(string path, string content) => J.Obj(("file_path", path), ("content", content));

    private ToolResultPart Result(string callId) => _rig.Results(S).Single(r => r.CallId == callId);

    private static string Esc(string path) => path.Replace("'", "'\\''", StringComparison.Ordinal);

    /// <summary>只看 <c>stop_hook_active</c> 的 Stop 钩子(2.x <c>stopOnceHook</c>):第一次 false → exit 2(这一轮继续),第二次 true → exit 0(放行)。只用 POSIX 的 read / case。</summary>
    private const string StopOnceCommand = """
        read -r line || true
        case "$line" in
          *'"stop_hook_active":false'*) printf '%s\n' 'the transcript has no evidence yet - keep working' >&2; exit 2 ;;
        esac
        exit 0
        """;

    private static string StopOnceEntry(string id = "stop-once") =>
        new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["command"] = StopOnceCommand }.ToJsonString();

    // ───────────────────────── PreToolUse(权限链) ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_PreToolUse_hook_denies_one_bash_call_the_command_never_runs_and_the_model_sees_the_reason()
    {
        var created = Marker("created.txt");
        ProjectSettings("""{"hooks":{"PreToolUse":[{"id":"guard","matcher":"Bash","command":"echo 'production is off limits' >&2; exit 2"}]}}""");
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(created)}'")), Say("understood"));

        var run = await _rig.RunAsync(S, "make the file");

        Assert.Equal(RunState.Done, run.State);
        Assert.False(File.Exists(created));
        Assert.True(Result("c1").IsError);
        Assert.Contains("production is off limits", Result("c1").Output.Content);
        Assert.Contains("production is off limits", E2eRig.WireText(_rig.Llm.MainRequests[1]));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_hook_deny_beats_an_allow_rule_the_user_clicked_once_and_does_not_touch_other_tools()
    {
        var target = _rig.Abs("out.txt");
        var other = _rig.Abs("fine.txt");
        // 用户早就「以后都允许」了 Write;钩子只拦 out.txt 之类的路径(脚本自己判断)
        ProjectSettings("""
            {"permissions":{"allow":["Write"],"ask":[],"deny":[]},
             "hooks":{"PreToolUse":[{"id":"g","matcher":"Write","command":"cat | grep -q 'out.txt' && { echo 'not this file' >&2; exit 2; }; exit 0"}]}}
            """);
        _rig.Llm.Main(Up.Calls(("w1", "Write", WriteArgs(target, "x")), ("w2", "Write", WriteArgs(other, "y"))), Say("done"));

        await _rig.RunAsync(S, "write both", permission: "ask");

        Assert.False(File.Exists(target));
        Assert.True(Result("w1").IsError);
        Assert.Contains("not this file", Result("w1").Output.Content);
        Assert.Equal("y", File.ReadAllText(other));
        Assert.False(Result("w2").IsError);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_hook_ask_beats_an_allow_rule_and_full_mode_and_raises_an_approval()
    {
        var created = Marker("asked.txt");
        ProjectSettings("""
            {"permissions":{"allow":["Bash"],"ask":[],"deny":[]},
             "hooks":{"PreToolUse":[{"id":"ask","matcher":"Bash","command":"printf '{\"decision\":\"ask\",\"reason\":\"look at this\"}'"}]}}
            """);
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(created)}'")), Say("done"));

        var run = _rig.Send(S, "do it", permission: "full");
        var pending = await Wait.UntilNotNull(() => _rig.Gate.Snapshot().Pending.OfType<ToolPermission>().FirstOrDefault(), what: "an approval raised by the hook");
        Assert.False(File.Exists(created));
        Assert.IsType<RespondResult.Accepted>(_rig.Gate.Respond(pending.Id, pending.Version, new ToolPermissionResponse(new PermissionChoice.AllowOnceChoice()), "ctrl"));
        await E2eRig.Settled(run);

        Assert.True(File.Exists(created));
        Assert.False(Result("c1").IsError);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_hook_allow_lets_a_call_through_without_asking_in_ask_mode()
    {
        var created = Marker("allowed.txt");
        ProjectSettings("""{"hooks":{"PreToolUse":[{"id":"ok","matcher":"Bash","command":"printf '{\"decision\":\"allow\"}'"}]}}""");
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(created)}'")), Say("done"));

        var run = await _rig.RunAsync(S, "do it", permission: "ask");

        Assert.Equal(RunState.Done, run.State);
        Assert.True(File.Exists(created));
        Assert.Empty(_rig.Gate.Snapshot().Pending);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task When_a_deny_rule_matches_the_hook_script_is_never_executed()
    {
        var ran = Marker("hook-ran.txt");
        ProjectSettings("""
            {"permissions":{"allow":[],"ask":[],"deny":["Bash"]},
             "hooks":{"PreToolUse":[{"id":"g","command":"touch '@@ran@@'"}]}}
            """.Replace("@@ran@@", Esc(ran)));
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs("echo hi")), Say("done"));

        await _rig.RunAsync(S, "run it", permission: "full");

        Assert.True(Result("c1").IsError);
        Assert.Contains("deny rule", Result("c1").Output.Content);
        Assert.False(File.Exists(ran), "a deny rule must stop the call before any hook is run");
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_broken_PreToolUse_hook_abstains_the_call_proceeds_and_the_failure_is_visible_in_the_diagnostics()
    {
        var created = Marker("proceed.txt");
        ProjectSettings("""{"hooks":{"PreToolUse":[{"id":"broken","command":"echo boom >&2; exit 1"}]}}""");
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(created)}'")), Say("done"));

        var run = await _rig.RunAsync(S, "do it", permission: "full");

        Assert.Equal(RunState.Done, run.State);
        Assert.True(File.Exists(created));
        Assert.Contains(await _rig.Hooks.DiagnosticsAsync(E2eRig.Workspace), d => d.Key == HookDiagnosticKeys.Command);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_global_hook_applies_to_every_project()
    {
        var created = Marker("global-blocked.txt");
        _rig.Put("../profile/settings.json", """{"hooks":{"PreToolUse":[{"id":"g","command":"echo 'global policy' >&2; exit 2"}]}}""");
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(created)}'")), Say("done"));

        await _rig.RunAsync(S, "do it", permission: "full");

        Assert.False(File.Exists(created));
        Assert.Contains("global policy", Result("c1").Output.Content);
    }

    // ───────────────────────── PostToolUse ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task PostToolUse_appends_feedback_to_the_result_and_never_rewrites_it()
    {
        ProjectSettings("""{"hooks":{"PostToolUse":[{"id":"lint","matcher":"Write","command":"echo 'lint: 2 warnings'"}]}}""");
        var target = _rig.Abs("a.txt");
        _rig.Llm.Main(Up.Call("w1", "Write", WriteArgs(target, "hello")), Say("done"));

        await _rig.RunAsync(S, "write it");

        var result = Result("w1");
        Assert.False(result.IsError);
        Assert.Contains("a.txt", result.Output.Content);          // 原输出还在
        Assert.EndsWith("\n\n<hook-feedback>\nlint: 2 warnings\n</hook-feedback>", result.Output.Content); // 反馈追加在后面,用 <hook-feedback> 标出是钩子说的(2.x 同)
        Assert.Equal("hello", File.ReadAllText(target));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task PostToolUse_exit_two_flips_the_result_to_an_error_so_the_model_can_act_on_it()
    {
        ProjectSettings("""{"hooks":{"PostToolUse":[{"id":"lint","matcher":"Write","command":"echo 'rejected by lint' >&2; exit 2"}]}}""");
        _rig.Llm.Main(Up.Call("w1", "Write", WriteArgs(_rig.Abs("a.txt"), "hello")), Say("done"));

        await _rig.RunAsync(S, "write it");

        Assert.True(Result("w1").IsError);
        Assert.EndsWith("\n\n<hook-feedback>\nrejected by lint\n</hook-feedback>", Result("w1").Output.Content);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task PostToolUse_gets_the_tool_output_and_error_flag_in_its_payload_and_a_matcher_filters_by_tool()
    {
        var payload = Marker("post-payload.json");
        var other = Marker("other-ran.txt");
        ProjectSettings("""
            {"hooks":{"PostToolUse":[
              {"id":"p","matcher":"Bash","command":"cat > '@@payload@@'"},
              {"id":"o","matcher":"Write","command":"touch '@@other@@'"}]}}
            """.Replace("@@payload@@", Esc(payload)).Replace("@@other@@", Esc(other)));
        _rig.Llm.Main(Up.Call("b1", "Bash", BashArgs("echo from-the-tool")), Say("done"));

        await _rig.RunAsync(S, "go", permission: "full");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(payload));
        Assert.Equal("PostToolUse", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("Bash", doc.RootElement.GetProperty("toolInternalId").GetString());
        Assert.Contains("from-the-tool", doc.RootElement.GetProperty("toolOutput").GetString());
        Assert.False(doc.RootElement.GetProperty("toolIsError").GetBoolean());
        Assert.False(File.Exists(other));
    }

    // ───────────────────────── UserPromptSubmit ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_blocking_UserPromptSubmit_hook_ends_the_run_with_an_error_and_the_prompt_never_reaches_the_conversation_or_the_model()
    {
        ProjectSettings("""{"hooks":{"UserPromptSubmit":[{"id":"nosecrets","command":"echo 'no secrets in prompts' >&2; exit 2"}]}}""");

        var run = await _rig.RunAsync(S, "here is my password hunter2");

        Assert.Equal(RunState.Error, run.State);
        Assert.Contains("no secrets in prompts", run.Error!.Message);
        Assert.Equal("hooks.promptBlocked", run.Error.MessageKey);
        Assert.Empty(_rig.Llm.MainRequests);
        Assert.DoesNotContain(_rig.History(S), m => m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("hunter2", StringComparison.Ordinal)));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task UserPromptSubmit_additional_context_reaches_the_model_but_never_the_transcript()
    {
        ProjectSettings("""{"hooks":{"UserPromptSubmit":[{"id":"branch","command":"echo 'current git branch: feature-x'"}]}}""");
        _rig.Llm.Main(Say("ok"));

        var run = await _rig.RunAsync(S, "what branch am I on?");

        Assert.Equal(RunState.Done, run.State);
        var wire = E2eRig.WireText(_rig.Llm.MainRequests[0]);
        Assert.Contains("<hook-context>\ncurrent git branch: feature-x\n</hook-context>", wire);
        Assert.DoesNotContain(_rig.History(S), m => m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("feature-x", StringComparison.Ordinal)));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task UserPromptSubmit_receives_the_prompt_text_in_its_payload()
    {
        var payload = Marker("prompt-payload.json");
        ProjectSettings("""{"hooks":{"UserPromptSubmit":[{"id":"p","command":"cat > '@@payload@@'"}]}}""".Replace("@@payload@@", Esc(payload)));
        _rig.Llm.Main(Say("ok"));

        await _rig.RunAsync(S, "fix the flaky test");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(payload));
        Assert.Equal("fix the flaky test", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("UserPromptSubmit", doc.RootElement.GetProperty("event").GetString());
    }

    // ───────────────────────── Stop × 目标 / 待办 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_real_stop_hook_blocks_the_first_stop_and_lets_the_second_through_via_stop_hook_active()
    {
        ProjectSettings("{\"hooks\":{\"Stop\":[" + StopOnceEntry() + "]}}");
        _rig.Llm.Main(Up.Text("turn 1"), Up.Text("turn 2"));

        var run = await _rig.RunAsync(S, "go");

        Assert.Equal(RunState.Done, run.State);
        // 恰好两轮就是「第二次真的放行了」的证据:再阻断一次就会多跑一轮,而 stop_hook_active 没被解析出来的话它永远不会变成 true
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Empty(_rig.Llm.JudgeRequests); // 没有目标:这条钩子和目标是两条独立的路
        Assert.Null(_rig.Goals.Store.Get(S));
        var second = E2eRig.WireText(_rig.Llm.MainRequests[1]);
        Assert.Contains(HookPrompts.StopFeedbackPrefix, second);
        Assert.Contains("keep working", second);
        Assert.Contains(_rig.History(S), m => m.Internal && m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("keep working", StringComparison.Ordinal)));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_stop_hook_that_blocks_comes_before_the_goal_so_the_evaluator_is_not_called_until_the_hook_lets_go()
    {
        ProjectSettings("{\"hooks\":{\"Stop\":[" + StopOnceEntry() + "]}}");
        _rig.NewSessionRow("g");
        _rig.Goals.Activate("g", Goal, GoalOrigin.User);
        _rig.Llm.Main(Up.Text("turn 1"), Up.Text("turn 2"));
        _rig.Llm.Judge(Up.Verdict(true, "all green"));

        var run = await _rig.RunAsync("g", "go");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        // 第一轮:Stop 钩子阻断 → 这一轮继续,目标没被叫醒;第二轮:钩子放行 → 目标判定一次 → met
        Assert.Single(_rig.Llm.JudgeRequests);
        Assert.Contains(HookPrompts.StopFeedbackPrefix, E2eRig.WireText(_rig.Llm.MainRequests[1]));
        Assert.Null(_rig.Goals.Store.Get("g"));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task While_a_goal_is_active_a_file_stop_hook_that_always_lets_go_does_not_get_in_the_way_of_the_evaluator()
    {
        ProjectSettings("""{"hooks":{"Stop":[{"id":"notify","command":"exit 0"}]}}""");
        _rig.NewSessionRow("g");
        _rig.Goals.Activate("g", Goal, GoalOrigin.User);
        _rig.Llm.Main(Up.Text("done?"), Up.Text("now it is"));
        _rig.Llm.Judge(Up.Verdict(false, "still red"), Up.Verdict(true, "green"));

        var run = await _rig.RunAsync("g", "go");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(2, _rig.Llm.JudgeRequests.Count);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task The_stop_hooks_continuation_advice_is_kept_first_and_the_todo_check_only_happens_after_it_lets_go()
    {
        const string NoteMark = "reconcile the progress reported through";
        ProjectSettings("{\"hooks\":{\"Stop\":[" + StopOnceEntry() + "]}}");
        _rig.Llm.Main(
            Up.Call("t1", "TodoWrite", TodoJson.List(("work", "in_progress"))),
            Say("ready to stop"),    // Stop 钩子:阻断(续跑意见)
            Say("ready to stop"),    // Stop 钩子放行 → 清单补报
            Up.Call("t4", "TodoWrite", TodoJson.List(("work", "completed"))),
            Say("all finished"));

        var run = await _rig.RunAsync(S, "do the work");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(5, _rig.Llm.MainRequests.Count);
        var afterHook = E2eRig.WireText(_rig.Llm.MainRequests[2]);
        var afterReminder = E2eRig.WireText(_rig.Llm.MainRequests[3]);
        Assert.Contains(HookPrompts.StopFeedbackPrefix, afterHook);
        Assert.DoesNotContain(NoteMark, afterHook);
        Assert.Contains(NoteMark, afterReminder);
        Assert.Equal(1, _rig.History(S).Count(m => m.Internal && m.Parts.OfType<TextPart>().Any(p => p.Text.Contains(NoteMark, StringComparison.Ordinal))));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_stop_hook_runs_once_per_stop_not_twice_when_the_run_ends_normally()
    {
        var log = Marker("stops.txt");
        ProjectSettings("""{"hooks":{"Stop":[{"id":"n","command":"echo stop >> '@@log@@'"}]}}""".Replace("@@log@@", Esc(log)));
        _rig.Llm.Main(Say("done"));

        await _rig.RunAsync(S, "go");
        await _rig.Hooks.DrainAsync();

        Assert.Equal(["stop"], File.ReadAllLines(log));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_run_that_ends_in_error_still_notifies_command_stop_hooks_once_with_the_status()
    {
        var log = Marker("stop-error.json");
        ProjectSettings("""{"hooks":{"Stop":[{"id":"n","command":"cat > '@@log@@'"}]}}""".Replace("@@log@@", Esc(log)));
        _rig.Llm.Main(Up.Fail(AgentErrorCode.Provider));

        var run = await _rig.RunAsync(S, "go");
        await _rig.Hooks.DrainAsync();

        Assert.Equal(RunState.Error, run.State);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(log));
        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("stop_hook_active").GetBoolean());
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task An_aborting_stop_notification_cannot_block_it_only_notifies()
    {
        var log = Marker("stop-abort.txt");
        ProjectSettings("""{"hooks":{"Stop":[{"id":"n","command":"echo ran >> '@@log@@'; exit 2"}]}}""".Replace("@@log@@", Esc(log)));
        _rig.Llm.Main(Up.Fail(AgentErrorCode.Provider));

        var run = await _rig.RunAsync(S, "go");
        await _rig.Hooks.DrainAsync();

        Assert.Equal(RunState.Error, run.State); // exit 2 在这里没有「继续」可言
        Assert.Equal(["ran"], File.ReadAllLines(log));
    }

    // ───────────────────────── SubagentStop / Notification ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_child_run_fires_SubagentStop_and_not_Stop_nor_UserPromptSubmit()
    {
        var log = Marker("child.txt");
        ProjectSettings("""
            {"hooks":{
              "SubagentStop":[{"id":"sub","command":"cat >> '@@log@@'; echo >> '@@log@@'"}],
              "Stop":[{"id":"stop","command":"echo STOP-RAN >> '@@log@@'; exit 2"}],
              "UserPromptSubmit":[{"id":"u","command":"echo PROMPT-RAN >> '@@log@@'; exit 2"}]}}
            """.Replace("@@log@@", Esc(log)));
        _rig.Repo.Create(new Ncw.Agent.Storage.Sessions.SessionCreate(E2eRig.Workspace, "child-session"));
        var session = _rig.Sessions.Get("child-session", E2eRig.Workspace);
        _rig.Llm.Main(Say("child result"));

        var outcome = await session.RunAsync(
            new RunRequest(new RunId("child-run"), [Msg.User("cu1", "do a sub task")], ModelRef.Unpinned(E2eRig.Alias)) { Depth = 1 }, new ListRunSink());
        await _rig.Hooks.DrainAsync();

        Assert.Equal(RunStatus.Done, outcome.Status);
        var text = File.ReadAllText(log);
        Assert.Contains("\"event\":\"SubagentStop\"", text);
        Assert.Contains("\"status\":\"done\"", text);
        Assert.DoesNotContain("STOP-RAN", text);
        Assert.DoesNotContain("PROMPT-RAN", text);
        Assert.Single(_rig.Llm.MainRequests); // 没有被 Stop 钩子顶回去再跑一轮
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_Notification_hook_fires_when_an_approval_is_waiting_and_does_not_delay_it()
    {
        var payload = Marker("notify.json");
        var created = Marker("notified.txt");
        ProjectSettings("""{"hooks":{"Notification":[{"id":"n","command":"sleep 1; cat > '@@payload@@'"}]}}""".Replace("@@payload@@", Esc(payload)));
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(created)}'")), Say("done"));

        var run = _rig.Send(S, "do it", permission: "ask");
        var pending = await Wait.UntilNotNull(() => _rig.Gate.Snapshot().Pending.OfType<ToolPermission>().FirstOrDefault(), what: "the approval");
        Assert.False(File.Exists(payload)); // 通知脚本还在睡:审批没有等它
        _rig.Gate.Respond(pending.Id, pending.Version, new ToolPermissionResponse(new PermissionChoice.DenyChoice(null)), "ctrl");
        await E2eRig.Settled(run);
        await _rig.Hooks.DrainAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(payload));
        Assert.Equal("Notification", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("tool_permission", doc.RootElement.GetProperty("notification").GetProperty("kind").GetString());
        Assert.Equal("Bash", doc.RootElement.GetProperty("notification").GetProperty("toolName").GetString());
        Assert.False(File.Exists(created));
    }

    // ───────────────────────── 扩展自身 ─────────────────────────

    [Fact]
    public void The_extension_identity_and_order_leave_room_for_goals_and_todos_after_it()
    {
        var extension = _rig.Hooks.CreateExtension("ws");

        Assert.Equal("hooks", extension.Id);
        Assert.Equal(100, extension.Order);
        Assert.True(extension.Order < 500); // 目标 500、待办 600:turn_end 闸门按 Order 取第一个不是 Stop 的决定
    }

    // ───────────────────────── updatedInput / PreCompact / SubagentStart / Prompt ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_PreToolUse_hook_can_rewrite_input_via_updatedInput_and_attach_additionalContext()
    {
        var original = Marker("orig.txt");
        var rewritten = Marker("rewritten.txt");
        ProjectSettings("""
            {"hooks":{"PreToolUse":[{"id":"rw","matcher":"Bash","command":"printf '{\"hookSpecificOutput\":{\"permissionDecision\":\"allow\",\"additionalContext\":\"redirected by policy\",\"updatedInput\":{\"command\":\"touch \\\"@@rw@@\\\"\"}}}'"}]}}
            """.Replace("@@rw@@", Esc(rewritten)));
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs($"touch '{Esc(original)}'")), Say("done"));

        var run = await _rig.RunAsync(S, "go", permission: "ask");

        Assert.Equal(RunState.Done, run.State);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(rewritten));
        Assert.Contains("<hook-feedback>\nredirected by policy\n</hook-feedback>", Result("c1").Output.Content);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Invariant_rewritten_updatedInput_is_still_checked_against_deny_rules_even_if_hook_returned_allow()
    {
        var dangerous = Marker("should-not-run.txt");
        ProjectSettings("""
            {"permissions":{"allow":[],"ask":[],"deny":["Bash(rm -rf *)"]},
             "hooks":{"PreToolUse":[{"id":"rw","matcher":"Bash","command":"printf '{\"decision\":\"allow\",\"updatedInput\":{\"command\":\"rm -rf @@target@@\"}}'"}]}}
            """.Replace("@@target@@", Esc(dangerous)));
        _rig.Llm.Main(Up.Call("c1", "Bash", BashArgs("echo safe")), Say("ok"));

        var run = await _rig.RunAsync(S, "go", permission: "full");

        Assert.Equal(RunState.Done, run.State);
        Assert.True(Result("c1").IsError);
        Assert.DoesNotContain("rm -rf", File.Exists(dangerous) ? "ran" : "");
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_PreCompact_hook_blocks_compaction_when_it_exits_2()
    {
        var log = Marker("compact-hook.json");
        ProjectSettings("""{"hooks":{"PreCompact":[{"id":"pc","command":"cat > '@@log@@'; echo 'compact frozen' >&2; exit 2"}]}}""".Replace("@@log@@", Esc(log)));
        _rig.Llm.Main(Say("hello"));
        await _rig.RunAsync(S, "hi");

        var session = _rig.Sessions.Get(S, E2eRig.Workspace);
        var res = await session.CompactNowAsync(ModelRef.Unpinned(E2eRig.Alias), ThinkingLevel.Off, instructions: "keep details");

        Assert.Null(res.Boundary);
        Assert.NotNull(res.Error);
        Assert.Contains(res.Events.OfType<ContextStatusEvent>(), e => e.Phase == ContextStatusPhase.Failed);
        Assert.True(_rig.Log.Any("compact frozen"));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(log));
        Assert.Equal("PreCompact", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("manual", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("keep details", doc.RootElement.GetProperty("custom_instructions").GetString());
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_SubagentStart_hook_fires_with_agent_type_and_injects_additionalContext_into_child_head_sections()
    {
        var log = Marker("substart.json");
        ProjectSettings("""
            {"hooks":{"SubagentStart":[{"id":"ss","command":"cat > '@@log@@'; printf '{\"additionalContext\":\"Follow repo security guide\"}'"}]}}
            """.Replace("@@log@@", Esc(log)));
        _rig.Repo.Create(new Ncw.Agent.Storage.Sessions.SessionCreate(E2eRig.Workspace, "child-s"));
        var session = _rig.Sessions.Get("child-s", E2eRig.Workspace);
        _rig.Llm.Main(Say("child output"));

        var outcome = await session.RunAsync(
            new RunRequest(new RunId("child-start-run"), [Msg.User("cu1", "inspect code")], ModelRef.Unpinned(E2eRig.Alias))
            {
                Depth = 1,
                AgentType = "code-reviewer",
            }, new ListRunSink());

        Assert.Equal(RunStatus.Done, outcome.Status);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(log));
        Assert.Equal("SubagentStart", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("code-reviewer", doc.RootElement.GetProperty("agent_type").GetString());
        Assert.Contains("Follow repo security guide", E2eRig.WireText(_rig.Llm.MainRequests.Last()));
    }

    [Fact]
    public async Task A_prompt_Stop_hook_evaluates_transcript_via_LlmHookEvaluator_and_blocks_until_met()
    {
        ProjectSettings("""{"hooks":{"Stop":[{"id":"rev","type":"prompt","prompt":"Did the assistant name the file it edited?"}]}}""");
        _rig.Llm.Main(Say("Done."), Say("Edited Program.cs.")).Judge(Up.Verdict(false, "file name missing"), Up.Verdict(true, "Program.cs named"));

        var run = await _rig.RunAsync(S, "edit the file");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Equal(2, _rig.Llm.JudgeRequests.Count);
        Assert.Contains("Did the assistant name the file it edited?", E2eRig.WireText(_rig.Llm.JudgeRequests[0]));
        Assert.Contains("Done.", E2eRig.WireText(_rig.Llm.JudgeRequests[0]));
    }
}
