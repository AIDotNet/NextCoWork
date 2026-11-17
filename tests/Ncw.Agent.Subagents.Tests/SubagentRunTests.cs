using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>
/// 端到端:<c>AgentLoop</c> / <c>RunRegistry</c> + 假上游 + <b>真实的 <c>Task</c> 工具</b>、真实的 <see cref="SubagentService"/>、权限扩展、交互闸门、SQLite。
/// 验证接缝:同步 / 后台子代理、报告回到父会话、并发上限排队、父中止级联、子代理请求审批被拒、深度限制、工具快照过滤、权限只收窄。
/// </summary>
public sealed class SubagentRunTests : IDisposable
{
    private readonly SubRig _rig = new();

    public SubagentRunTests() => _rig.NewSessionRow();

    public void Dispose() => _rig.Dispose();

    private static RunSubscription Collect(SubRig rig, CollectingListener listener) => rig.Registry.Subscribe(listener);

    // ───────────────────────── 同步子代理 ─────────────────────────

    [Fact]
    public async Task A_foreground_subagent_runs_in_its_own_session_and_its_last_message_becomes_the_tool_result()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(prompt: "find the config reader"))), Up.Text("The parent is done."));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Text("Report: config is read in src/config.ts:12."));
        var listener = new CollectingListener();

        var parent = _rig.Start("s1", "please research", subscriptions: [Collect(_rig, listener)]);
        await SubRig.Settled(parent);

        Assert.Equal(RunState.Done, parent.State);
        // ① 父会话:工具结果 = 子代理的最后一条消息(全文),带持久回执
        var result = _rig.ToolResults("s1").Single();
        Assert.Equal("Report: config is read in src/config.ts:12.", SubRig.Text(result));
        Assert.False(result.IsError);
        Assert.Equal(("p1:sub:1", "done", "Report: config is read in src/config.ts:12."), (result.Subagent!.ChildRunId, result.Subagent.Status, result.Subagent.Summary));
        Assert.NotEqual(true, result.Subagent.Background);
        // 父代理据此收工:下一次上行请求里模型看得见子代理的产出
        var second = _rig.RequestsOf("s1")[1];
        Assert.Contains("config is read in src/config.ts:12", string.Join('\n', second.Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(r => r.Output.Content)));

        // ② 子会话:parent_session_id 是真实父关系;全新的上下文窗(只有那条 prompt 和子代理的回复)
        var childSession = SubRig.ChildSession("s1", "p1");
        Assert.Equal("s1", _rig.Repo.Get(childSession)!.ParentSessionId);
        var childTranscript = _rig.Transcript(childSession);
        Assert.Equal(["find the config reader", "Report: config is read in src/config.ts:12."], childTranscript.Select(m => string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text))));
        Assert.DoesNotContain(_rig.Repo.List(SubRig.Workspace), s => s.Id == childSession); // 子转录不进侧边栏
        Assert.DoesNotContain(_rig.Repo.ListAll(topLevelOnly: true), s => s.Id == childSession);
        Assert.DoesNotContain(_rig.Transcript("s1").SelectMany(m => m.Parts).OfType<TextPart>(), p => p.Text.Contains("find the config reader")); // 父转录里没有子代理的内部对话

        // ③ 子 Run 登记在父 Run 名下,深度 +1,起源是子代理
        var child = _rig.Registry.Get(new RunId("p1:sub:1"))!;
        Assert.Equal((new RunId("p1"), 1, RunOrigin.Subagent, "s1"), (child.ParentRunId, child.Depth, child.Origin, child.ParentSessionId));
        Assert.Equal(RunState.Done, child.State);

        // ④ 事件:subagent_start / subagent_end 成对,同一个 callId 和 childRunId,子会话 id 由 Agent 带过来
        var events = listener.Events(new RunId("p1"));
        var start = Assert.Single(events.OfType<SubagentStartEvent>());
        var end = Assert.Single(events.OfType<SubagentEndEvent>());
        Assert.Equal(("c1", new RunId("p1:sub:1"), childSession, "general-purpose"), (start.CallId, start.ChildRunId, start.ChildSessionId, start.AgentType));
        Assert.Equal(("c1", new RunId("p1:sub:1"), RunStatus.Done), (end.CallId, end.ChildRunId, end.Status));
        var ordered = events.ToList();
        Assert.True(ordered.IndexOf(start) < ordered.IndexOf(end));
        Assert.False(start.Background);
        // 控制端订阅的是整棵 Run 树:子 Run 的原始事件也落在父订阅上(先继承订阅、再启动)
        Assert.Contains(listener.Events(new RunId("p1:sub:1")), e => e is RunStartEvent);
        Assert.Contains(listener.Events(new RunId("p1:sub:1")), e => e is RunEndEvent { Status: RunStatus.Done });
    }

    [Fact]
    public async Task The_subagent_gets_the_role_prompt_appended_and_never_sees_the_parent_conversation()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(type: "code-analyst", prompt: "explain the loop"))), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Text("explained"));

        await SubRig.Settled(_rig.Start("s1", "SECRET parent context that must not leak"));

        var request = _rig.RequestsOf(SubRig.ChildSession("s1", "p1")).Single();
        Assert.Contains("You explain how code works", request.System);
        Assert.Equal(["explain the loop"], request.Messages.SelectMany(m => m.Parts).OfType<TextPart>().Select(p => p.Text).Where(t => !t.StartsWith('<')));
        Assert.DoesNotContain("SECRET", request.System + string.Concat(request.Messages.SelectMany(m => m.Parts).OfType<TextPart>().Select(p => p.Text)));
    }

    [Fact]
    public async Task Invariant_an_unknown_agent_name_is_refused_with_the_list_of_available_ones_and_never_falls_back_to_general_purpose()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(type: "reviewr"))), Up.Text("ok"));

        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);

        var result = _rig.ToolResults("s1").Single();
        Assert.True(result.IsError);
        Assert.Contains("There is no subagent named \"reviewr\"", SubRig.Text(result));
        Assert.Contains("general-purpose, code-analyst, code-reviewer, code-editor", SubRig.Text(result));
        Assert.Null(result.Subagent);
        Assert.Null(_rig.Registry.Get(new RunId("p1:sub:1"))); // 没有悄悄起一个通用代理
        Assert.DoesNotContain(_rig.Repo.ListAll(topLevelOnly: false), s => s.Id.Contains(":sub:"));
    }

    [Fact]
    public async Task A_subagent_that_finishes_without_saying_anything_is_reported_as_a_failure()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Events(
            new MessageStart("fake", "p1", "anthropic", "fake-up"), new MessageEnd(StopReason.EndTurn, Up.Usage)));

        await SubRig.Settled(_rig.Start("s1", "go"));

        var result = _rig.ToolResults("s1").Single();
        Assert.True(result.IsError);
        Assert.Contains("finished without producing any text", SubRig.Text(result));
        Assert.Equal("error", result.Subagent!.Status);
    }

    [Fact]
    public async Task A_subagent_that_fails_upstream_comes_back_as_an_error_with_the_original_message()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Fail(AgentError.Create(AgentErrorCode.Auth, "bad key", false)));

        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);

        Assert.Equal(RunState.Done, parent.State); // 子代理失败不拖垮父代理
        var result = _rig.ToolResults("s1").Single();
        Assert.True(result.IsError);
        Assert.Contains("The subagent general-purpose failed", SubRig.Text(result));
        Assert.Contains("bad key", SubRig.Text(result));
        Assert.Equal("error", result.Subagent!.Status);
        Assert.Equal("bad key", result.Subagent.Error!.Message);
    }

    // ───────────────────────── 深度限制 ─────────────────────────

    [Fact]
    public async Task Invariant_the_nesting_limit_is_two_levels_the_third_is_refused_with_a_reason_and_no_session_is_created()
    {
        var task = SubRig.TaskArgs();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", task)), Up.Text("parent done"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), SubRig.Calls(("c2", "Task", task)), Up.Text("child done"));
        // 子 RunId 的序号是进程内单调递增(2.x 同):孙代理是第 2 个
        var grandchildSession = SubRig.ChildSession(SubRig.ChildSession("s1", "p1"), "p1:sub:1", 2);
        _rig.Script(grandchildSession, SubRig.Calls(("c3", "Task", task)), Up.Text("grandchild done"));

        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);

        // depth 0 → 1 → 2 都派得出;depth 2 的 Task 被拒
        Assert.Equal(RunState.Done, parent.State);
        Assert.Equal(2, _rig.Registry.Get(new RunId("p1:sub:1:sub:2"))!.Depth);
        var refused = _rig.ToolResults(grandchildSession).Single();
        Assert.True(refused.IsError);
        Assert.Contains("nesting limit of 2 levels", SubRig.Text(refused));
        Assert.Contains("do NOT launch another subagent", SubRig.Text(refused));
        Assert.Null(_rig.Registry.Get(new RunId("p1:sub:1:sub:2:sub:3")));
        // 子代理的结果一层层交回来
        Assert.Equal("grandchild done", SubRig.Text(_rig.ToolResults(SubRig.ChildSession("s1", "p1")).Single()));
        Assert.Equal("child done", SubRig.Text(_rig.ToolResults("s1").Single()));
        // parent_session_id 是真实的逐层父子关系(不靠解析 id)
        Assert.Equal("s1", _rig.Repo.Get(SubRig.ChildSession("s1", "p1"))!.ParentSessionId);
        Assert.Equal(SubRig.ChildSession("s1", "p1"), _rig.Repo.Get(grandchildSession)!.ParentSessionId);
    }

    // ───────────────────────── 子代理不能提问,权限只收窄 ─────────────────────────

    [Fact]
    public async Task Invariant_a_subagent_request_for_approval_is_refused_on_the_spot_nothing_is_ever_queued_for_a_human_and_the_run_carries_on()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(prompt: "clean up"))), Up.Text("parent done"));
        _rig.Script(SubRig.ChildSession("s1", "p1"),
            SubRig.Calls(("b1", "Bash", """{"command":"touch should-not-exist.txt","description":"create a file"}""")),
            Up.Text("I could not run it, the parent must."));
        var listener = new CollectingListener();

        // auto 档:Task 本身(非破坏性)直接放行;子代理里的 Bash(破坏性)要问人——而子代理没有人可问
        var parent = _rig.Start("s1", "go", mode: "auto", subscriptions: [Collect(_rig, listener)]);
        await SubRig.Settled(parent);

        Assert.Equal(RunState.Done, parent.State);
        var childSession = SubRig.ChildSession("s1", "p1");
        var denied = _rig.ToolResults(childSession).Single();
        Assert.True(denied.IsError);
        Assert.Contains("a subagent cannot ask for it", SubRig.Text(denied));
        Assert.Contains("Bash(touch:*)", SubRig.Text(denied)); // 给出可添加的规则
        Assert.False(File.Exists(Path.Combine(_rig.WorkRoot, "should-not-exist.txt")), "the command must not have run");
        Assert.Empty(_rig.Gate.Snapshot().Pending);
        Assert.DoesNotContain(listener.Events(), e => e is Ncw.Agent.Extensions.Interactions.InteractionRequestEvent);
        // 子代理拿到拒绝之后继续跑完,把话说给父代理
        Assert.Equal("I could not run it, the parent must.", SubRig.Text(_rig.ToolResults("s1").Single()));
    }

    [Fact]
    public async Task The_child_has_no_way_to_open_an_interaction_its_scope_has_depth_greater_than_zero()
    {
        var gate = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(gate.Task, "done"));

        var parent = _rig.Start("s1", "go");
        var child = await _rig.WaitForChild("p1");
        await Wait.UntilAsync(() => _rig.Perms.Get(child.RunId) is not null, what: "the child's permission state");

        var state = _rig.Perms.Get(child.RunId)!;
        Assert.True(state.IsSubagent);
        Assert.False(state.Interactions.CanInteract);
        await Assert.ThrowsAsync<Ncw.Agent.Extensions.Interactions.InteractionRefusedException>(() =>
            state.Interactions.RequestAsync(new Ncw.Agent.Extensions.Interactions.ToolPermission
            {
                CallId = "x",
                ToolName = "Bash",
                InternalId = "Bash",
                Input = Ncw.Agent.Llm.Json.Jsn.Parse("{}"),
                Traits = new Ncw.Agent.Kernel.Tools.ToolTraits(false, true, false),
                Env = state.EnvironmentRef,
                SuggestedRule = "Bash",
                AllowedScopes = Ncw.Agent.Extensions.Interactions.RememberScopes.Session,
            }));

        gate.SetResult();
        await SubRig.Settled(parent);
    }

    [Fact]
    public async Task Invariant_the_permission_mode_is_the_min_of_the_parents_live_value_and_the_definitions_and_follows_the_parent_down_at_once()
    {
        File.WriteAllText(Path.Combine(_rig.AgentsDir, "careful.md"), "---\ndescription: careful\npermissionMode: auto\n---\nBe careful.\n");
        var gate = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(type: "careful"))), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(gate.Task, "done"));

        var parent = _rig.Start("s1", "go", mode: "full");
        var child = await _rig.WaitForChild("p1");
        await Wait.UntilAsync(() => _rig.Perms.Get(child.RunId) is not null, what: "the child's permission state");
        var childState = _rig.Perms.Get(child.RunId)!;

        // 父 full、定义 auto → auto:取 min,子代理不继承 full
        Assert.Equal(PermissionMode.Auto, childState.Mode);
        Assert.Equal("auto", child.LivePermissionMode);
        // 父中途调低 → 子代理立刻跟着降
        _rig.Perms.Get(parent.RunId)!.Mode = PermissionMode.Ask;
        Assert.Equal(PermissionMode.Ask, childState.Mode);
        // 子代理永远不能被调得比定义更宽
        childState.Mode = PermissionMode.Full;
        Assert.Equal(PermissionMode.Ask, childState.Mode);
        _rig.Perms.Get(parent.RunId)!.Mode = PermissionMode.Full;
        Assert.Equal(PermissionMode.Auto, childState.Mode);

        gate.SetResult();
        await SubRig.Settled(parent);
    }

    [Fact]
    public async Task A_definition_without_a_permission_mode_takes_the_parents_mode_it_is_not_widened_and_not_narrowed()
    {
        var gate = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(gate.Task, "done"));

        var parent = _rig.Start("s1", "go", mode: "auto");
        var child = await _rig.WaitForChild("p1");
        await Wait.UntilAsync(() => _rig.Perms.Get(child.RunId) is not null, what: "the child's permission state");

        Assert.Equal(PermissionMode.Auto, _rig.Perms.Get(child.RunId)!.Mode);
        gate.SetResult();
        await SubRig.Settled(parent);
    }

    [Fact]
    public async Task Invariant_the_childs_tool_list_has_no_interactive_tools_and_honours_the_definitions_tools_while_the_parent_keeps_them()
    {
        _rig.ExtraTools.Add(new FakeTool("AskUserQuestion"));
        _rig.ExtraTools.Add(new FakeTool("ExitPlanMode"));
        File.WriteAllText(Path.Combine(_rig.AgentsDir, "narrow.md"), "---\ndescription: narrow\ntools: Read, Grep, AskUserQuestion\n---\nLook.\n");
        _rig.Script("s1",
            SubRig.Calls(("c1", "Task", SubRig.TaskArgs(type: "narrow")), ("c2", "Task", SubRig.TaskArgs(type: "general-purpose"))),
            Up.Text("ok"));

        await SubRig.Settled(_rig.Start("s1", "go"));

        var parentTools = SubRig.ToolNames(_rig.RequestsOf("s1")[0]);
        Assert.Contains("AskUserQuestion", parentTools); // 收窄的只有 depth > 0
        Assert.Contains("ExitPlanMode", parentTools);
        Assert.Contains("Task", parentTools);
        var narrow = SubRig.ToolNames(_rig.RequestsOf(SubRig.ChildSession("s1", "p1", 1)).Single());
        var general = SubRig.ToolNames(_rig.RequestsOf(SubRig.ChildSession("s1", "p1", 2)).Single());
        // tools: 写了 Read, Grep, AskUserQuestion → 去掉交互类之后只剩 Read, Grep
        Assert.Equal(["Read", "Grep"], narrow.Order(StringComparer.Ordinal).Reverse());
        Assert.DoesNotContain("AskUserQuestion", general);
        Assert.DoesNotContain("ExitPlanMode", general);
        Assert.Contains("Bash", general); // 省略 tools = 继承父 Run 的其余全部工具
        Assert.Contains("Task", general);
    }

    [Fact]
    public async Task The_interactive_tool_set_is_exactly_the_two_that_wait_for_the_user()
    {
        // 多摘一个等于悄悄削弱子代理,少摘一个等于留一条死锁的路。
        Assert.Equal(["AskUserQuestion", "ExitPlanMode"], SubagentToolFilter.InteractiveToolIds.Order(StringComparer.Ordinal));
        await Task.CompletedTask;
    }

    // ───────────────────────── 后台子代理 ─────────────────────────

    [Fact]
    public async Task A_background_subagent_returns_at_once_and_when_it_finishes_after_the_parent_its_full_report_is_delivered_to_the_parent_session()
    {
        var release = new TaskCompletionSource();
        _rig.Script("s1",
            SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true, prompt: "dig deep"))),
            Up.Text("Started it, carrying on."),
            Up.Text("Got the report, thanks.")); // 报告到达后父会话的新一轮
        var childSession = SubRig.ChildSession("s1", "p1");
        var report = "FULL REPORT: " + new string('r', 600) + " end";
        _rig.Script(childSession, Up.Gate(release.Task, report));
        var changes = new List<ReportChange>();
        _rig.Service.ReportChanged += changes.Add;
        var listener = new CollectingListener();

        var parent = _rig.Start("s1", "go", subscriptions: [Collect(_rig, listener)]);
        await SubRig.Settled(parent);

        // 父代理立刻拿到回话,而子代理还在跑
        var started = _rig.ToolResults("s1").Single();
        Assert.False(started.IsError);
        Assert.Contains("started in the background", SubRig.Text(started));
        Assert.Equal((true, "running"), (started.Subagent!.Background, started.Subagent.Status));
        Assert.Equal(RunState.Running, _rig.Child("p1").State);
        Assert.Equal("a task", _rig.Child("p1").Background!.Description);
        Assert.Single(_rig.Registry.ActiveBackgroundChildrenOfSession("s1"));
        Assert.True(listener.Events(new RunId("p1")).OfType<SubagentStartEvent>().Single().Background);

        release.SetResult();

        // 子代理结束 → 回执改成 done + 汇报进度 → 全文作为内部消息回到父会话(会话空闲:起一个新的内部 Run)
        await Wait.UntilAsync(() => _rig.Transcript("s1").Any(m => m.Internal), what: "the report message in the parent session");
        await Wait.UntilAsync(() => changes.Any(c => c.Status == SubagentReportStatus.Reported), what: "reported");
        var internalMessage = _rig.Transcript("s1").Single(m => m.Internal);
        Assert.Equal(Role.User, internalMessage.Role);
        var text = string.Concat(internalMessage.Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.StartsWith("Background subagent result (general-purpose, p1:sub:1):", text);
        Assert.Contains(report, text); // ★ 全文,不是 240 字的摘要
        Assert.Contains("Review this result and continue the conversation if action is needed.", text);
        Assert.Equal(new SubagentReportPart("c1", "p1:sub:1", report[..240]), internalMessage.Parts.OfType<SubagentReportPart>().Single());

        await Wait.UntilAsync(() => _rig.ToolResults("s1").Single().Subagent!.ReportStatus == "reported", what: "the durable receipt to say reported");
        var receipt = _rig.ToolResults("s1").Single().Subagent!;
        Assert.Equal(("done", true, 240), (receipt.Status, receipt.Background, receipt.Summary!.Length));
        Assert.Equal([SubagentReportStatus.Injecting, SubagentReportStatus.Reported], changes.Select(c => c.Status).Distinct());
        // 父 Run 已经结束:subagent_end 到不了它的事件流(原始子事件仍经继承的订阅送达)
        Assert.Empty(listener.Events(new RunId("p1")).OfType<SubagentEndEvent>());
        Assert.Contains(listener.Events(new RunId("p1:sub:1")), e => e is RunEndEvent { Status: RunStatus.Done });
        // 内部 Run 是排队续跑之外的另一个入口,同样走 Launch
        Assert.Contains(_rig.Factory.Created, t => t.Origin == RunOrigin.Internal);
    }

    [Fact]
    public async Task When_the_parent_run_is_still_going_the_report_is_injected_as_a_steering_message_exactly_once()
    {
        var childGate = new TaskCompletionSource();
        var reported = new TaskCompletionSource();
        _rig.Service.ReportChanged += c =>
        {
            if (c.Status == SubagentReportStatus.Injecting)
            {
                reported.TrySetResult();
            }
        };
        var waiter = new FakeTool("wait_for_child", async (_, _, ct) =>
        {
            childGate.SetResult();
            await reported.Task.WaitAsync(ct);
            await Task.Delay(50, ct); // 报告已经进了引导队列
            return Ncw.Agent.Kernel.Tools.ToolResult.Text("waited");
        });
        _rig.ExtraTools.Add(waiter);
        _rig.Script("s1",
            SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true))),
            SubRig.Calls(("c2", "wait_for_child", "{}")),
            Up.Text("done after steering"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(childGate.Task, "child result text"));

        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);

        await Wait.UntilAsync(() => _rig.ToolResults("s1").Any(r => r.Subagent?.ReportStatus == "reported"), what: "reported");
        var internals = _rig.Transcript("s1").Where(m => m.Internal).ToList();
        Assert.Single(internals); // 只汇报一次
        Assert.Contains("child result text", string.Concat(internals[0].Parts.OfType<TextPart>().Select(p => p.Text)));
    }

    [Fact]
    public async Task Invariant_an_aborted_background_subagent_is_not_reported_automatically_the_receipt_stays_pending_for_the_user_to_press_handle()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true))), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Hang());
        var changes = new List<ReportChange>();
        _rig.Service.ReportChanged += changes.Add;
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<SubagentStopped>();
        _rig.Service.Stopped += stopped.Enqueue;
        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);
        var child = _rig.Child("p1");

        // 用户只停了这个子代理(不级联、不是父会话)
        _rig.Registry.Abort(child.RunId, cascade: false);
        await SubRig.Settled(child);

        // 子代理的收尾(写回执 → 发 subagent_end → 登记待汇报并发 ReportChanged → 通知 Stopped)是在子 Run settle 之后异步做完的,
        // Stopped 是它最后一步:等它,之后「回执、变更通知、没有自动开新一轮」才都是定局(回执先于变更通知可见,只等回执会在两者之间读到旧的 changes)。
        await Wait.UntilAsync(() => stopped.Count == 1, what: "the subagent's bookkeeping to finish");
        var receipt = _rig.ToolResults("s1").Single().Subagent!;
        Assert.Equal(("aborted", "pending"), (receipt.Status, receipt.ReportStatus));
        Assert.DoesNotContain(_rig.Transcript("s1"), m => m.Internal); // 没有自动开一轮新的对话:那正好违背了「停下」这个意图
        Assert.Contains(changes, c => c.Status == SubagentReportStatus.Pending);

        // 用户自己点「处理」:同一条路径,汇报照样送回父会话(中止的子代理没有自己的最后一条消息时,退回摘要 / 说明)
        _rig.Script("s1", Up.Text("noted"));
        Assert.Equal(SubagentReportStatus.Injecting, _rig.Service.ReportManually("s1", "c1", new SendOptions(_rig.Model, ThinkingLevel.Auto, false, "code", "full")));
        await Wait.UntilAsync(() => _rig.ToolResults("s1").Single().Subagent!.ReportStatus == "reported", what: "the manual report");
        Assert.Single(_rig.Transcript("s1"), m => m.Internal);
    }

    [Fact]
    public async Task Pressing_handle_on_an_already_reported_one_leaves_it_alone_and_an_unknown_call_is_none()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true))), Up.Text("ok"), Up.Text("noted"));
        var childSession = SubRig.ChildSession("s1", "p1");
        _rig.Script(childSession, Up.Text("partial but useful findings"));
        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);
        await Wait.UntilAsync(() => _rig.ToolResults("s1").Single().Subagent!.ReportStatus == "reported", what: "the automatic report");
        // 模拟「重启之后只有库里的回执」:回执退回 pending(崩溃时 injecting 落盘就是 pending),内部消息也还没发
        var internalIds = _rig.Transcript("s1").Where(m => m.Internal).Select(m => m.Id.Value).ToList();
        Assert.Single(internalIds);
        Assert.Equal(SubagentReportStatus.Reported, _rig.Service.ReportManually("s1", "c1", null)); // 已汇报的原样返回,不重发
        Assert.Single(_rig.Transcript("s1"), m => m.Internal);
        Assert.Equal(SubagentReportStatus.None, _rig.Service.ReportManually("s1", "no-such-call", null));
    }

    /// <summary>模拟「重启之后」:库里有一个后台子代理的回执(待汇报)和它的子会话,但这个进程没见过这条会话发过消息。</summary>
    private void SeedRestartedSession(string session = "s2")
    {
        _rig.Repo.Create(new SessionCreate(SubRig.Workspace, session));
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        _rig.Repo.Commit(session, MessageCodec.ToRecord(new Message(new MessageId("a-call"), Role.Assistant,
            [new ToolCallPart("c1", "Task", Ncw.Agent.Llm.Json.Jsn.Parse(SubRig.TaskArgs(type: "code-analyst", background: true)))], at)));
        _rig.Repo.Commit(session, MessageCodec.ToRecord(new Message(new MessageId("a-result"), Role.User,
            [new ToolResultPart("c1", new ToolOutput("started"), false, new SubagentReceipt("old:sub:1", "done", "short", true, "pending"))], at.AddSeconds(1))));
        var child = $"{session}:sub:old:sub:1";
        _rig.Repo.Ensure(new SessionCreate(SubRig.Workspace, Id: child, ParentSessionId: session));
        _rig.Repo.Commit(child, MessageCodec.ToRecord(new Message(new MessageId("c-a"), Role.Assistant, [new TextPart("the saved full report")], at.AddSeconds(2))));
    }

    [Fact]
    public void Invariant_with_no_send_options_at_all_the_report_is_blocked_not_silently_dropped()
    {
        SeedRestartedSession();
        var changes = new List<ReportChange>();
        _rig.Service.ReportChanged += changes.Add;

        // 这个进程没见过这条会话发消息,也没有装配方给的兜底档位:一个按下去什么都不发生的按钮不是诚实的界面。
        var status = _rig.Service.ReportManually("s2", "c1", fallback: null);

        Assert.Equal(SubagentReportStatus.Blocked, status);
        Assert.Equal("blocked", _rig.ToolResults("s2").Single().Subagent!.ReportStatus);
        Assert.Contains(changes, c => c is { SessionId: "s2", CallId: "c1", Status: SubagentReportStatus.Blocked });
        Assert.DoesNotContain(_rig.Transcript("s2"), m => m.Internal);
    }

    [Fact]
    public async Task After_a_restart_pressing_handle_with_the_fallback_options_delivers_the_saved_full_report_once()
    {
        SeedRestartedSession();
        _rig.Script("s2", Up.Text("handled"));
        var fallback = new SendOptions(_rig.Model, ThinkingLevel.Auto, false, "code", "full");

        var first = _rig.Service.ReportManually("s2", "c1", fallback);
        await Wait.UntilAsync(() => _rig.ToolResults("s2").Single().Subagent!.ReportStatus == "reported", what: "reported");
        var again = _rig.Service.ReportManually("s2", "c1", fallback);

        Assert.Equal(SubagentReportStatus.Injecting, first);
        Assert.Equal(SubagentReportStatus.Reported, again); // 已汇报的原样返回,不重发
        var internals = _rig.Transcript("s2").Where(m => m.Internal).ToList();
        Assert.Single(internals);
        var text = string.Concat(internals[0].Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.StartsWith("Background subagent result (code-analyst, old:sub:1):", text); // 类型取自父会话里 Task 调用的参数
        Assert.Contains("the saved full report", text);
    }

    [Fact]
    public async Task A_foreground_subagent_can_be_moved_to_the_background_the_parent_stops_waiting_and_the_child_finishes_as_a_background_one()
    {
        var release = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(prompt: "long job"))), Up.Text("Carrying on without it."), Up.Text("Report received."));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(release.Task, "the long result"));
        var parent = _rig.Start("s1", "go");
        var child = await _rig.WaitForChild("p1");
        await Wait.UntilAsync(() => child.HasStarted, what: "the child to start");

        Assert.True(_rig.Service.DetachForeground(child.RunId));
        Assert.False(_rig.Service.DetachForeground(child.RunId)); // 已经是后台:不是错误,只是没有可转的了
        await SubRig.Settled(parent);

        var moved = _rig.ToolResults("s1").Single();
        Assert.Contains("The user moved this subagent to the background", SubRig.Text(moved));
        Assert.Equal((true, "running"), (moved.Subagent!.Background, moved.Subagent.Status));
        Assert.Equal(RunState.Running, child.State);

        release.SetResult();

        // ★ 结束那一刻读的是可变标记:被转走的那个按后台收尾——回执 pending、结论送回主代理
        await Wait.UntilAsync(() => _rig.ToolResults("s1").Single().Subagent!.ReportStatus == "reported", what: "the report");
        Assert.Contains("the long result", string.Concat(_rig.Transcript("s1").Single(m => m.Internal).Parts.OfType<TextPart>().Select(p => p.Text)));
        Assert.False(_rig.Service.DetachForeground(child.RunId)); // 跑完的转不了
    }

    // ───────────────────────── 并发上限与排队 ─────────────────────────

    [Fact]
    public async Task With_a_limit_of_one_per_parent_three_dispatches_all_run_in_dispatch_order_none_is_refused()
    {
        _rig.Settings.Value = new SubagentSettings { PerSessionLimit = 1, GlobalLimit = 4 };
        var gate1 = new TaskCompletionSource();
        var gate2 = new TaskCompletionSource();
        _rig.Script("s1",
            SubRig.Calls(("c1", "Task", SubRig.TaskArgs(prompt: "one")), ("c2", "Task", SubRig.TaskArgs(prompt: "two")), ("c3", "Task", SubRig.TaskArgs(prompt: "three"))),
            Up.Text("all three came back"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 1), Up.Gate(gate1.Task, "result one"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 2), Up.Gate(gate2.Task, "result two"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 3), Up.Text("result three"));
        var listener = new CollectingListener();

        var parent = _rig.Start("s1", "go", subscriptions: [Collect(_rig, listener)]);
        await Wait.UntilAsync(() => _rig.Service.QueuedCount == 2, what: "two dispatches to be queued");

        // 只有第一个在跑:其余两个排着队(还没有 subagent_start,也还没有 Run)
        Assert.NotNull(_rig.Registry.Get(new RunId("p1:sub:1")));
        Assert.Null(_rig.Registry.Get(new RunId("p1:sub:2")));
        Assert.Single(_rig.Registry.ActiveChildrenOf(new RunId("p1")));
        // 排队期间在父卡片上报排位——易失的 tool_progress,不写进转录。
        // 排位进度是入队(QueuedCount 变化)之后、在队列的锁外才投出去的,而且要等父 Run 的 tool_start 落下之后才发给订阅者(RunHandle 保序):
        // QueuedCount 到 2 的那一刻这两条可能还在路上,所以等它们真的到了再断言(而不是在 QueuedCount 上赌它们已经到了)。
        List<ToolProgressEvent> QueuedProgress() => listener.Events(new RunId("p1")).OfType<ToolProgressEvent>().Where(e => e.Progress.Message!.StartsWith("Queued", StringComparison.Ordinal)).ToList();
        await Wait.UntilAsync(
            () => QueuedProgress() is var p && p.Any(e => e.CallId == "c2" && e.Progress.Message == "Queued, waiting for a free subagent slot.") && p.Any(e => e.CallId == "c3" && e.Progress.Message == "Queued, 1 ahead."),
            what: "the two queue-position progress events");

        gate1.SetResult();
        await Wait.UntilAsync(() => _rig.Registry.Get(new RunId("p1:sub:2")) is not null, what: "the second to start");
        Assert.Null(_rig.Registry.Get(new RunId("p1:sub:3")));
        gate2.SetResult();
        await SubRig.Settled(parent);

        Assert.Equal(RunState.Done, parent.State);
        var results = _rig.ToolResults("s1").ToDictionary(r => r.CallId, SubRig.Text);
        Assert.Equal(new Dictionary<string, string> { ["c1"] = "result one", ["c2"] = "result two", ["c3"] = "result three" }, results);
        Assert.All(_rig.ToolResults("s1"), r => Assert.False(r.IsError)); // ★ 满了不等于失败
        Assert.Equal(0, _rig.Service.QueuedCount);
        Assert.Equal(3, listener.Events(new RunId("p1")).OfType<SubagentStartEvent>().Count());
    }

    [Fact]
    public async Task Invariant_a_global_limit_of_zero_is_refused_outright_it_is_not_full_it_is_off()
    {
        _rig.Settings.Value = new SubagentSettings { GlobalLimit = 0 };
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));

        await SubRig.Settled(_rig.Start("s1", "go"));

        var result = _rig.ToolResults("s1").Single();
        Assert.True(result.IsError);
        Assert.Contains("Subagents are disabled", SubRig.Text(result));
        Assert.Contains("Do this step yourself", SubRig.Text(result));
        Assert.Equal(0, _rig.Service.QueuedCount);
    }

    [Fact]
    public async Task A_background_dispatch_into_a_full_pool_answers_queued_at_once_and_a_queued_one_dies_with_its_parent_run()
    {
        _rig.Settings.Value = new SubagentSettings { PerSessionLimit = 1, GlobalLimit = 4 };
        var gate = new TaskCompletionSource();
        _rig.Script("s1",
            SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true, prompt: "first")), ("c2", "Task", SubRig.TaskArgs(background: true, prompt: "second"))),
            Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 1), Up.Gate(gate.Task, "first done"));

        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent); // 父代理的这一轮没有被排队的那个占着

        var results = _rig.ToolResults("s1").ToDictionary(r => r.CallId, SubRig.Text);
        Assert.Contains("started in the background", results["c1"]);
        Assert.Contains("queued behind the running ones", results["c2"]);
        Assert.Contains("starts automatically", results["c2"]);
        // 2.x 沿用:排队中的等待者在父 Run 结束时也要取消(后台派发也一样),否则它们会在父 Run 没了之后才起来,而那时没有人会去消费结果。
        // 取消 = 回执改成 aborted(卡片不会永远停在「运行中」),留给用户点「处理」。
        Assert.Equal(0, _rig.Service.QueuedCount);
        await Wait.UntilAsync(() => _rig.ToolResults("s1").Single(r => r.CallId == "c2").Subagent!.Status == "aborted", what: "the never-started receipt to say aborted");
        Assert.Null(_rig.Registry.Get(new RunId("p1:sub:2")));
        Assert.Equal("running", _rig.ToolResults("s1").Single(r => r.CallId == "c1").Subagent!.Status);
        gate.SetResult();
    }

    [Fact]
    public async Task Raising_the_limit_while_queued_takes_effect_without_anyone_calling_notify_the_one_second_rescan_picks_it_up()
    {
        // 用真实时钟的 1 秒兜底重扫:这里等一小会儿(≤ 5 秒)。
        _rig.Settings.Value = new SubagentSettings { PerSessionLimit = 1, GlobalLimit = 4 };
        var gate = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(prompt: "one")), ("c2", "Task", SubRig.TaskArgs(prompt: "two"))), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 1), Up.Gate(gate.Task, "r1"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 2), Up.Text("r2"));

        var parent = _rig.Start("s1", "go");
        await Wait.UntilAsync(() => _rig.Service.QueuedCount == 1, what: "one queued");

        _rig.Settings.Value = new SubagentSettings { PerSessionLimit = 2, GlobalLimit = 4 };

        await Wait.UntilAsync(() => _rig.Registry.Get(new RunId("p1:sub:2")) is not null, timeoutMs: 8000, what: "the rescan to start the second");
        gate.SetResult();
        await SubRig.Settled(parent);
    }

    // ───────────────────────── 父中止:级联取消 ─────────────────────────

    [Fact]
    public async Task Invariant_aborting_the_parent_cascades_to_the_running_child_and_the_queued_one_leaves_no_waiter_behind()
    {
        _rig.Settings.Value = new SubagentSettings { PerSessionLimit = 1, GlobalLimit = 4 };
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(prompt: "one")), ("c2", "Task", SubRig.TaskArgs(prompt: "two"))), Up.Text("never reached"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 1), Up.Hang());
        var parent = _rig.Start("s1", "go");
        var child = await _rig.WaitForChild("p1");
        await Wait.UntilAsync(() => _rig.Service.QueuedCount == 1, what: "the second dispatch to be queued");
        await Wait.UntilAsync(() => child.HasStarted, what: "the child to start");

        _rig.Registry.Abort(parent.RunId); // 级联

        await SubRig.Settled(parent);
        await SubRig.Settled(child);
        Assert.Equal(RunState.Aborted, parent.State);
        Assert.Equal(RunState.Aborted, child.State);
        Assert.Equal(0, _rig.Service.QueuedCount); // 排着队的那个跟着走:不留等待者
        // 排着队的那个没有起来(或者在名额恰好腾出来、父 Run 的取消还没轮到的那一瞬起来了,但立刻被父 Run 的取消令牌带走):任何情况下都不留运行中的子 Run
        await Wait.UntilAsync(() => _rig.Registry.ActiveSubagentRunIds().Count == 0, what: "no running subagent to be left");
        // 每个 tool_call 仍配得上 tool_result(中止收尾补「已中止」),同一个 callId 没有两条结果
        var ids = _rig.ToolResults("s1").Select(r => r.CallId).Order().ToList();
        Assert.Equal(["c1", "c2"], ids);
    }

    [Fact]
    public async Task Aborting_the_parent_while_the_child_is_stuck_in_the_queue_behind_a_background_one_also_leaves_nothing_queued()
    {
        _rig.Settings.Value = new SubagentSettings { PerSessionLimit = 1, GlobalLimit = 4 };
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true, prompt: "bg")), ("c2", "Task", SubRig.TaskArgs(prompt: "fg"))), Up.Text("never"));
        _rig.Script(SubRig.ChildSession("s1", "p1", 1), Up.Hang());
        var parent = _rig.Start("s1", "go");
        await Wait.UntilAsync(() => _rig.Service.QueuedCount == 1, what: "the foreground dispatch to be queued behind the background child");

        _rig.Registry.Abort(parent.RunId);
        await SubRig.Settled(parent);

        Assert.Equal(RunState.Aborted, parent.State);
        Assert.Equal(0, _rig.Service.QueuedCount);
        await SubRig.Settled(_rig.Child("p1"));
        Assert.Equal(RunState.Aborted, _rig.Child("p1").State);
    }

    [Fact]
    public async Task A_parent_that_ends_normally_does_not_kill_its_background_child_and_a_later_end_event_does_not_reach_the_ended_parent()
    {
        var release = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(background: true))), Up.Text("bye"), Up.Text("ack"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(release.Task, "late result"));
        var parent = _rig.Start("s1", "go");
        await SubRig.Settled(parent);

        Assert.Equal(RunState.Done, parent.State);
        Assert.Equal(RunState.Running, _rig.Child("p1").State);
        // 子 Run 在父 Run 之后才结束:广播里它在自己那半边(父会话),父 Run 已经不在运行中
        var index = _rig.Registry.ActiveRuns();
        var sub = Assert.Single(index.Subagents);
        Assert.Equal(("p1:sub:1", "p1", "s1"), (sub.RunId, sub.ParentRunId, sub.SessionId));
        Assert.Empty(index.Runs);

        release.SetResult();
        await Wait.UntilAsync(() => _rig.ToolResults("s1").Single().Subagent!.Status == "done", what: "the receipt to say done");
        await Wait.UntilAsync(() => _rig.Log.Any("background child finished after parent"), what: "the log line");
        Assert.Empty(_rig.Registry.ActiveRuns().Subagents);
    }

    // ───────────────────────── 子代理自身的状态 ─────────────────────────

    [Fact]
    public async Task The_childs_tool_use_is_mirrored_on_the_parent_as_updates_with_child_seq_and_ends_with_the_end_event()
    {
        File.WriteAllText(Path.Combine(_rig.WorkRoot, "a.txt"), "hello");
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), SubRig.Calls(("r1", "Read", $$"""{"file_path":"{{System.Text.Json.JsonEncodedText.Encode(Path.Combine(_rig.WorkRoot, "a.txt"))}}"}""")), Up.Text("read it"));
        var listener = new CollectingListener();

        await SubRig.Settled(_rig.Start("s1", "go", subscriptions: [Collect(_rig, listener)]));

        var updates = listener.Events(new RunId("p1")).OfType<SubagentUpdateEvent>().ToList();
        var toolStart = updates.First(u => u.CurrentTool.IsSet && u.CurrentTool.Value == "Read");
        Assert.Equal((SubagentPhase.Tool, 1, true), (toolStart.Phase, toolStart.ToolCalls, toolStart.CurrentTarget.IsSet));
        Assert.EndsWith("a.txt", toolStart.CurrentTarget.Value);
        Assert.True(toolStart.ChildSeq > 0);
        Assert.Contains(updates, u => u.Phase == SubagentPhase.Thinking && u is { CurrentTool: { IsSet: true, Value: null } }); // 键在即清
        Assert.Contains(updates, u => u.Phase == SubagentPhase.Finishing && u.Notice is { IsSet: true, Value: null });
        Assert.Contains(updates, u => u.ContextUsage is not null);
        // 全序:start < update… < end
        var all = listener.Events(new RunId("p1")).Where(e => e is SubagentStartEvent or SubagentUpdateEvent or SubagentEndEvent).ToList();
        Assert.IsType<SubagentStartEvent>(all[0]);
        Assert.IsType<SubagentEndEvent>(all[^1]);
        // 客户端的归约:同一批事件喂给 Core 的纯函数,得到「运行中 → 终态」且终态不被改回
        var card = new SubagentRecord("s1", "c1", "general-purpose", "a task", SubagentStatus.Running, DateTimeOffset.UnixEpoch, null, 0, null);
        foreach (var update in updates)
        {
            card = SubagentView.ApplyUpdate(card, update.ToolCalls, update.CurrentTool, update.CurrentTarget, update.At);
        }

        var finished = SubagentView.ApplyEnd(card, SubagentStatus.Done, DateTimeOffset.UnixEpoch.AddSeconds(2));
        var replayed = SubagentView.ApplyUpdate(finished, 9, Patch<string>.Set("Read"), Patch<string>.Keep, null);
        Assert.Equal((SubagentStatus.Done, 1), (replayed.Status, replayed.ToolCalls));
        Assert.Null(replayed.CurrentTool);
    }

    [Fact]
    public async Task The_stopped_event_fires_for_every_finished_child_for_the_SubagentStop_hook()
    {
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<SubagentStopped>();
        _rig.Service.Stopped += stopped.Enqueue;
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));

        await SubRig.Settled(_rig.Start("s1", "go"));

        var one = Assert.Single(stopped);
        Assert.Equal(("s1", "c1", "p1:sub:1", RunStatus.Done, false), (one.ParentSessionId, one.CallId, one.ChildRunId.Value, one.Status, one.Background));
    }

    [Fact]
    public async Task The_subagent_is_visible_in_the_active_runs_broadcast_while_it_runs_and_gone_when_it_ends_the_parent_session_is_its_session()
    {
        var gate = new TaskCompletionSource();
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        _rig.Script(SubRig.ChildSession("s1", "p1"), Up.Gate(gate.Task, "r"));

        var parent = _rig.Start("s1", "go");
        await _rig.WaitForChild("p1");
        await Wait.UntilAsync(() => _rig.Registry.ActiveRuns().Subagents.Length == 1, what: "the child in the broadcast");

        var sub = _rig.Registry.ActiveRuns().Subagents.Single();
        Assert.Equal(("p1:sub:1", "p1", "s1"), (sub.RunId, sub.ParentRunId, sub.SessionId)); // 子代理卡片住在父会话里
        Assert.Single(_rig.Registry.ActiveRuns().Runs);
        gate.SetResult();
        await SubRig.Settled(parent);
        Assert.Empty(_rig.Registry.ActiveRuns().Subagents);
        lock (_rig.Broadcasts)
        {
            Assert.Contains(_rig.Broadcasts, b => b.Subagents.Length == 1);
            Assert.Empty(_rig.Broadcasts[^1].Subagents);
        }
    }

    [Fact]
    public async Task Deleting_the_parent_session_takes_the_subagent_transcripts_with_it_through_parent_session_id()
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs())), Up.Text("ok"));
        await SubRig.Settled(_rig.Start("s1", "go"));
        var childSession = SubRig.ChildSession("s1", "p1");
        Assert.NotNull(_rig.Repo.Get(childSession));

        var deleted = _rig.Repo.Delete("s1");

        Assert.Contains(childSession, deleted);
        Assert.Null(_rig.Repo.Get(childSession));
    }

    [Fact]
    public async Task A_disabled_agent_cannot_be_dispatched_it_is_not_in_the_list_and_not_in_the_registry()
    {
        _rig.Catalog.SetEnabled("code-reviewer", false);
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(type: "code-reviewer"))), Up.Text("ok"));

        await SubRig.Settled(_rig.Start("s1", "go"));

        var result = _rig.ToolResults("s1").Single();
        Assert.True(result.IsError);
        Assert.Contains("There is no subagent named \"code-reviewer\"", SubRig.Text(result));
        Assert.DoesNotContain("code-reviewer", SubRig.ToolNames(_rig.RequestsOf("s1")[0]));
        var description = _rig.RequestsOf("s1")[0].Tools.Single(t => t.ExternalName == "Task").Description;
        Assert.DoesNotContain("- code-reviewer:", description);
        Assert.Contains("- general-purpose:", description);
    }

    [Fact]
    public async Task A_new_agent_file_is_picked_up_on_the_next_run_without_a_restart_and_shows_in_the_task_description()
    {
        _rig.Script("s1", Up.Text("first"), Up.Text("second"));
        await SubRig.Settled(_rig.Start("s1", "one", "p1"));
        Assert.DoesNotContain("- fresh:", _rig.RequestsOf("s1")[0].Tools.Single(t => t.ExternalName == "Task").Description);

        File.WriteAllText(Path.Combine(_rig.AgentsDir, "fresh.md"), "---\ndescription: brand new\n---\nbody\n");
        await SubRig.Settled(_rig.Start("s1", "two", "p2"));

        Assert.Contains("- fresh: brand new", _rig.RequestsOf("s1")[1].Tools.Single(t => t.ExternalName == "Task").Description);
    }

    // ───────────────────────── 模型 / 思考档位 / 最大上下文的继承(subagent-wiring.test.ts) ─────────────────────────

    private async Task<RunHandle> RunOneChild(string type = "general-purpose", ModelRef? model = null, ThinkingLevel thinking = ThinkingLevel.Auto, bool maxContext = false)
    {
        _rig.Script("s1", SubRig.Calls(("c1", "Task", SubRig.TaskArgs(type: type))), Up.Text("ok"));
        await SubRig.Settled(_rig.Start("s1", "go", model: model, thinking: thinking, maxContext: maxContext));
        return _rig.Child("p1");
    }

    [Fact]
    public async Task A_subagent_without_a_declared_model_inherits_the_parents_alias_together_with_the_providers_lock()
    {
        var child = await RunOneChild(model: new ModelRef("p1", "claude-sonnet-4"));

        Assert.Equal(new ModelRef("p1", "claude-sonnet-4"), child.Options!.Model);
    }

    [Fact]
    public async Task Invariant_when_the_parent_pinned_no_provider_the_child_does_not_grow_one_out_of_nowhere()
    {
        var child = await RunOneChild();

        Assert.Equal(ModelRef.Unpinned("claude-sonnet-4"), child.Options!.Model);
    }

    [Fact]
    public async Task A_declared_model_wins_and_without_a_declared_provider_it_chooses_by_alias_not_by_the_parents_lock()
    {
        File.WriteAllText(Path.Combine(_rig.AgentsDir, "cheap-one.md"), "---\ndescription: cheap\nmodel: cheap\n---\nbody\n");

        var child = await RunOneChild("cheap-one", model: new ModelRef("p1", "claude-sonnet-4"));

        // 别名和供应商必须成对决定:声明了别名但没声明供应商 = 只认别名,不是沿用父亲锁的那家
        Assert.Equal(ModelRef.Unpinned("cheap"), child.Options!.Model);
    }

    [Fact]
    public async Task A_declared_provider_that_can_no_longer_serve_the_model_loses_its_lock_but_keeps_the_alias()
    {
        File.WriteAllText(Path.Combine(_rig.AgentsDir, "pinned.md"), "---\ndescription: d\nmodel: cheap\nmodelProviderId: gone\n---\nbody\n");
        _rig.Models.ExistsFn = sel => sel.ProviderId != "gone";

        var child = await RunOneChild("pinned");

        Assert.Equal(ModelRef.Unpinned("cheap"), child.Options!.Model);
        Assert.True(_rig.Log.Any("pins provider gone"));
    }

    [Fact]
    public async Task The_configured_default_subagent_model_is_used_as_a_pair()
    {
        _rig.Settings.Value = new SubagentSettings { Model = new Ncw.Agent.Catalog.ModelSelection("cheap", "p2") };

        var child = await RunOneChild();

        Assert.Equal(new ModelRef("p2", "cheap"), child.Options!.Model);
    }

    [Fact]
    public async Task Invariant_a_dangling_default_subagent_model_falls_back_to_the_parent_instead_of_a_request_with_an_alias_nobody_can_serve()
    {
        _rig.Settings.Value = new SubagentSettings { Model = new Ncw.Agent.Catalog.ModelSelection("deleted", "p2") };
        _rig.Models.ExistsFn = sel => sel.Alias != "deleted";

        var child = await RunOneChild();

        Assert.Equal(ModelRef.Unpinned("claude-sonnet-4"), child.Options!.Model);
        Assert.True(_rig.Log.Any("configured default subagent model is unavailable"));
    }

    [Fact]
    public async Task The_thinking_level_follows_the_parent_run_by_default()
    {
        var inherited = await RunOneChild(thinking: ThinkingLevel.Medium);
        Assert.Equal(ThinkingLevel.Medium, inherited.Options!.Thinking);
    }

    [Fact]
    public async Task The_settings_column_for_thinking_beats_the_parent_run()
    {
        _rig.Settings.Value = new SubagentSettings { Thinking = SubagentThinking.Of(ThinkingLevel.Low) };

        var child = await RunOneChild(thinking: ThinkingLevel.High);

        Assert.Equal(ThinkingLevel.Low, child.Options!.Thinking);
    }

    [Fact]
    public async Task A_thinking_level_in_the_agent_file_beats_the_settings_column()
    {
        File.WriteAllText(Path.Combine(_rig.AgentsDir, "deep.md"), "---\ndescription: d\nthinking: high\n---\nbody\n");
        _rig.Settings.Value = new SubagentSettings { Thinking = SubagentThinking.Of(ThinkingLevel.Low) };

        var child = await RunOneChild("deep", thinking: ThinkingLevel.Medium);

        Assert.Equal(ThinkingLevel.High, child.Options!.Thinking);
    }

    [Fact]
    public async Task Invariant_a_level_the_childs_model_does_not_support_is_silently_downgraded_not_sent_as_is()
    {
        // 档位是模型的属性,而子 Run 的模型未必和父 Run 是同一个;不归一化的话,每一次派发都会在发请求那一刻失败。
        _rig.Models.NormalizeFn = (level, _) => level == ThinkingLevel.XHigh ? ThinkingLevel.High : level;

        var child = await RunOneChild(thinking: ThinkingLevel.XHigh);

        Assert.Equal(ThinkingLevel.High, child.Options!.Thinking);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_max_context_switch_is_passed_down_as_is(bool maxContext)
    {
        var child = await RunOneChild(maxContext: maxContext);

        Assert.Equal(maxContext, child.Options!.MaxContext);
        Assert.Equal(maxContext, _rig.Factory.Created.Single(t => t.RunId == child.RunId).Options.MaxContext);
        Assert.Equal("code", child.Options.Mode); // 子代理始终按编程模式运行
    }
}
