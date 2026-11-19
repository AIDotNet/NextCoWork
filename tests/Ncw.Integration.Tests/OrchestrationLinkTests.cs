using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

using static Ncw.Integration.Tests.Support.OrchestrationRig;

namespace Ncw.Integration.Tests;

/// <summary>
/// 子代理 / 计划 / 待办 / 目标在控制端的端到端:真实 Agent + 真实 <c>DeviceLink</c> / Store / Actions + 假上游。
/// 验证「设备推送 → 归约 → Store」这条链:卡片的键与单调(终态压过运行中、重放的旧事件不复活)、两个控制端一致、
/// 断线重连按游标续传后状态正确(断线期间是未知而不是结束)、点开子代理面板才订阅、待办是转录的派生视图、目标经主题镜像。
/// </summary>
public sealed class OrchestrationLinkTests : IAsyncLifetime
{
    private readonly OrchestrationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<(ProjectRef Project, string ProjectPath)> AddProjectAsync(Controller c)
    {
        var project = await c.Link.AddProjectAsync(_rig.Workspace);
        var path = (await c.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        return (project, path);
    }

    private static SubagentRef CardOf(SessionRef session, string callId = "c1") => new(session.DeviceId, session.SessionId, callId);

    private static SubagentEntry? Entry(Controller c, SubagentRef key) => c.State.Subagents.GetValueOrDefault(key);

    // ───────────────────────── 子代理 ─────────────────────────

    [Fact]
    public async Task A_foreground_subagent_becomes_a_card_in_the_store_and_its_transcript_is_only_subscribed_when_the_panel_is_opened()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var card = CardOf(session);
        _rig.Llm.On(session.SessionId, S.Call("c1", "Task", S.TaskArgs("find the config reader")), S.Say("parent done"))
            .OnChildren(session.SessionId, S.Say("Report: config is read in src/config.ts:12. " + new string('x', 400)));

        var sent = await a.Conversations.SendAsync(session, "research", Options);
        Assert.Equal(SendOutcome.Started, sent.Outcome);
        var run = sent.Run!.Value;
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the parent run to finish");
        await WaitUntil(() => Entry(a, card) is { IsTerminal: true }, "the card to finish");

        var entry = Entry(a, card)!;
        Assert.Equal((SubagentStatus.Done, "general-purpose", false), (entry.Record.Status, entry.Record.AgentType, entry.Record.Background));
        Assert.StartsWith("Report: config is read", entry.Telemetry.Summary);
        Assert.True(entry.Telemetry.Summary!.Length <= 241); // 卡片上的预览(240 字);全文在子会话里
        Assert.Equal(S.ChildSession(session.SessionId, run.RunId), entry.Record.ChildSessionId); // 设备带过来的,和惯例一致
        Assert.False(entry.Guessed);
        var view = entry.ViewAt(DateTimeOffset.UtcNow);
        Assert.Equal((true, false, false, SubagentReportStatus.None), (view.CanOpen, view.CanStop, view.CanBackground, view.Report));

        // 没点开:子会话和子 Run 一个字都没有(一次编排能派出十几个后台子代理,绝大多数永远不会被点开)
        Assert.DoesNotContain(new SessionRef(session.DeviceId, entry.Record.ChildSessionId!), a.State.Sessions.Keys);
        Assert.DoesNotContain(a.State.Runs.Keys, r => r.RunId.Contains(":sub:"));

        // 点开(早就跑完的:只开会话,不 attach 一个已经收口的 Run)
        var child = await a.Subagents.OpenAsync(entry);
        Assert.Equal(new SessionRef(session.DeviceId, entry.Record.ChildSessionId!), child);
        await WaitUntil(() => a.State.Sessions.ContainsKey(child!.Value) && a.State.Sessions[child.Value].Messages.Length == 2, "the child transcript");
        Assert.DoesNotContain(a.State.Runs.Keys, r => r.RunId.Contains(":sub:"));

        // 汇报行展开时现取全文(不是那 240 字);没有子会话 id 的旧卡片不发那个注定失败的请求
        var full = await a.Subagents.ReadReportAsync(entry);
        Assert.Equal(ReportTextOutcome.Full, full.Outcome);
        Assert.True(full.Text!.Length > 400);
        Assert.Equal(ReportTextOutcome.NoChildSession, (await a.Subagents.ReadReportAsync(entry with { Record = entry.Record with { ChildSessionId = null } })).Outcome);
        a.Subagents.Close(child!.Value);
    }

    [Fact]
    public async Task A_running_subagent_panel_follows_the_child_run_stop_only_stops_the_child_and_the_parent_carries_on()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var card = CardOf(session);
        _rig.Llm.On(session.SessionId, S.Call("c1", "Task", S.TaskArgs()), S.Say("I will do it myself"))
            .OnChildren(session.SessionId, S.Hang());

        var run = (await a.Conversations.SendAsync(session, "go", Options)).Run!.Value;
        await WaitUntil(() => Entry(a, card) is { IsTerminal: false }, "the card to start running");
        var running = Entry(a, card)!;
        Assert.True(running.ViewAt(DateTimeOffset.UtcNow).CanStop);

        // 点开面板:订阅它自己的 run/<child> 主题(子 Run 的快照进 Store)
        var child = await a.Subagents.OpenAsync(running);
        await WaitUntil(() => a.State.Runs.Values.Any(r => r.Ref.RunId.Contains(":sub:") && r.Phase == RunPhase.Running), "the child run mirror");

        // 停止:只停子代理(cascade 只往下),父 Run 照常跑完;卡片收口为已中止
        Assert.True((await a.Subagents.StopAsync(running)).IsOk);
        await WaitUntil(() => Entry(a, card) is { Record.Status: SubagentStatus.Aborted }, "the card to be aborted");
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the parent to carry on and finish");
        await WaitUntil(() => a.State.Runs.Values.Where(r => r.Ref.RunId.Contains(":sub:")).All(r => r.Phase == RunPhase.Aborted), "the child run mirror to end");
        Assert.True((await a.Subagents.StopAsync(Entry(a, card)!)).IsOk); // 幂等:已经结束了也不报错
        a.Subagents.Close(child!.Value);
    }

    [Fact]
    public async Task Aborting_the_parent_cascades_and_every_card_and_run_converges_to_a_terminal_state_with_nothing_left_running()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var card = CardOf(session);
        _rig.Llm.On(session.SessionId, S.Call("c1", "Task", S.TaskArgs()), S.Say("never"))
            .OnChildren(session.SessionId, S.Hang());

        var run = (await a.Conversations.SendAsync(session, "go", Options)).Run!.Value;
        await WaitUntil(() => Entry(a, card) is { IsTerminal: false }, "the card to start running");

        Assert.True(await a.Conversations.AbortAsync(run));

        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Aborted, Settled: true }, "the parent to be aborted");
        await WaitUntil(() => Entry(a, card) is { IsTerminal: true }, "the card to converge");
        Assert.Equal(SubagentStatus.Aborted, Entry(a, card)!.Record.Status);
        await WaitUntil(() => a.Workspace.ActivityOf(project) is { RunningRuns: 0 }, "the project to be idle");
        Assert.Empty(a.Workspace.Devices.Values.Single().Index!.Subagents);
    }

    [Fact]
    public async Task A_background_subagent_outlives_the_parent_run_and_two_controllers_converge_on_done_and_reported()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var b = await _rig.StartControllerAsync("ctrl-b");
        var (project, _) = await AddProjectAsync(a);
        var gate = new TaskCompletionSource();
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var card = CardOf(session);
        Assert.True(await b.Conversations.OpenSessionAsync(session));
        _rig.Llm.On(session.SessionId, S.Call("c1", "Task", S.TaskArgs("do it", background: true)), S.Say("started it"), S.Say("thanks"))
            .OnChildren(session.SessionId, S.Gated(gate.Task, S.Say("THE FULL REPORT")));

        var run = (await a.Conversations.SendAsync(session, "go", Options)).Run!.Value;
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the parent to finish before the child");

        // B 没订阅这个 Run,只看着会话:卡片来自会话里的持久回执(后台、运行中),没有子会话 id(回执里没有)
        await WaitUntil(() => Entry(b, card) is { Record.Background: true }, "B's card from the receipt");
        Assert.False(Entry(b, card)!.ViewAt(DateTimeOffset.UtcNow).CanOpen);
        Assert.True(Entry(a, card)!.ViewAt(DateTimeOffset.UtcNow).CanOpen); // A 见过 subagent_start:带着子会话 id
        Assert.False(Entry(a, card)!.IsTerminal);

        gate.SetResult();
        // 父 Run 的日志已经收口,subagent_end 进不去:靠父会话主题的 subagent.stopped / subagent.report 把两边都收口;汇报进度只往前走
        await WaitUntil(() => Entry(a, card) is { Record: { Status: SubagentStatus.Done, ReportStatus: SubagentReportStatus.Reported } }
                              && Entry(b, card) is { Record: { Status: SubagentStatus.Done, ReportStatus: SubagentReportStatus.Reported } }, "both controllers to converge");
        Assert.Equal(Entry(a, card)!.Record.Status, Entry(b, card)!.Record.Status);
        Assert.False(Entry(b, card)!.ViewAt(DateTimeOffset.UtcNow).CanStop);

        // 汇报回到父会话:内部消息带全文 + 汇报行(subagent 块),两个控制端的会话镜像一致
        await WaitUntil(() => a.State.Sessions[session].Messages.Any(m => SubagentReceipts.ReportRowsOf(m).Count == 1), "the report row in A's session mirror");
        var message = a.State.Sessions[session].Messages.Single(m => SubagentReceipts.ReportRowsOf(m).Count == 1);
        Assert.True(message.Internal);
        Assert.Contains("THE FULL REPORT", SubagentReceipts.TextOf(message));
        await WaitUntil(() => b.State.Sessions[session].Messages.Any(m => m.Id == message.Id), "the report in B's session mirror");
        Assert.Equal(SubagentReceipts.ReportRowsOf(message), SubagentReceipts.ReportRowsOf(b.State.Sessions[session].Messages.Single(m => m.Id == message.Id)));
    }

    [Fact]
    public async Task Moving_a_foreground_subagent_to_the_background_updates_the_card_through_the_receipt_and_the_parent_run_carries_on()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var gate = new TaskCompletionSource();
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var card = CardOf(session);
        _rig.Llm.On(session.SessionId, S.Call("c1", "Task", S.TaskArgs()), S.Say("moving on"), S.Say("report seen"))
            .OnChildren(session.SessionId, S.Gated(gate.Task, S.Say("the late result")));

        var run = (await a.Conversations.SendAsync(session, "go", Options)).Run!.Value;
        await WaitUntil(() => Entry(a, card) is { IsTerminal: false }, "the card to start running");
        var foreground = Entry(a, card)!;
        Assert.True(foreground.ViewAt(DateTimeOffset.UtcNow).CanBackground);

        Assert.True((await a.Subagents.MoveToBackgroundAsync(foreground)).IsOk);

        // 父 Run 不再等它;那次 Task 调用的回执写着 background:true,卡片据此变成后台(不能再转)
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the parent to finish");
        await WaitUntil(() => Entry(a, card) is { Record.Background: true }, "the card to become a background one");
        Assert.False(Entry(a, card)!.ViewAt(DateTimeOffset.UtcNow).CanBackground);
        Assert.False(Entry(a, card)!.IsTerminal);

        gate.SetResult();
        await WaitUntil(() => Entry(a, card) is { Record: { Status: SubagentStatus.Done, ReportStatus: SubagentReportStatus.Reported } }, "the card to be done and reported");
    }

    [Fact]
    public async Task A_dispatch_waiting_for_a_slot_shows_its_position_in_the_store_until_it_starts()
    {
        _rig.SetSettings("""{"subagent":{"perSessionLimit":1,"globalLimit":1}}""");
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var gate = new TaskCompletionSource();
        var parentGate = new TaskCompletionSource();
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        _rig.Llm.On(session.SessionId,
                S.Calls(("c1", "Task", S.TaskArgs("one", "first", background: true)), ("c2", "Task", S.TaskArgs("two", "second", background: true))),
                S.Gated(parentGate.Task, S.Say("dispatched")))
            .OnChildren(session.SessionId, S.Gated(gate.Task, S.Say("first result")), S.Gated(gate.Task, S.Say("second result")));

        var run = (await a.Conversations.SendAsync(session, "go", Options)).Run!.Value;

        // 排队的那条 Task 调用带着结构化的排位;只有一张卡片(拿到名额的那个)
        await WaitUntil(() => a.State.Runs[run].Tools.Values.Any(t => t.QueuedAhead is not null), "the queued position");
        var queued = Assert.Single(a.State.Runs[run].Tools.Values, t => t.QueuedAhead is not null);
        Assert.Equal(0, queued.QueuedAhead);
        // 排队的派发没有开始:卡片是「排队中」,另一个在跑
        await WaitUntil(() => a.State.SubagentsOf(session).Count == 2, "both cards (one running, one queued)");
        Assert.Equal(SubagentStatus.Queued, a.State.Subagents[CardOf(session, queued.CallId)].Record.Status);
        Assert.Equal(SubagentStatus.Running, Assert.Single(a.State.SubagentsOf(session), e => e.Ref.CallId != queued.CallId).Record.Status);

        gate.SetResult();
        await WaitUntil(() => a.State.SubagentsOf(session).Count == 2 && a.State.SubagentsOf(session).All(e => e.IsTerminal), "both cards to finish");
        Assert.All(a.State.Runs[run].Tools.Values, t => Assert.Null(t.QueuedAhead)); // 开始了就清掉排位
        parentGate.SetResult();
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the parent to finish");
    }

    [Fact]
    public async Task A_dropped_connection_marks_the_card_unknown_then_the_cursor_resume_settles_it_and_a_replayed_start_cannot_revive_it()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var gate = new TaskCompletionSource();
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var card = CardOf(session);
        _rig.Llm.On(session.SessionId, S.Call("c1", "Task", S.TaskArgs()), S.Say("done"))
            .OnChildren(session.SessionId, S.Gated(gate.Task, S.Say("late answer")));

        var run = (await a.Conversations.SendAsync(session, "go", Options)).Run!.Value;
        await WaitUntil(() => Entry(a, card) is { IsTerminal: false }, "the card to start running");
        var seqBefore = a.State.Runs[run].Seq;

        // 网络断了(Agent 还在,子代理还在等):卡片是「未知」——不判结束,计时冻结
        a.Current!.Cut();
        await WaitUntil(() => a.Workspace.Devices.Values.All(d => d.Connection != DeviceConnection.Connected), "A sees the disconnect");
        await WaitUntil(() => Entry(a, card) is { Unknown: true }, "the card to be unknown");
        var frozen = Entry(a, card)!;
        Assert.Equal(SubagentStatus.Running, frozen.Record.Status);
        Assert.Equal(frozen.ViewAt(DateTimeOffset.UtcNow).Elapsed, frozen.ViewAt(DateTimeOffset.UtcNow.AddHours(1)).Elapsed);

        // 断线期间子代理跑完、父 Run 收口;A 重连后订阅按游标续传:只补发之后的事件,卡片收口为已完成
        gate.SetResult();
        await WaitUntil(() => a.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected), "A reconnected");
        await WaitUntil(() => Entry(a, card) is { Record.Status: SubagentStatus.Done, Unknown: false, Guessed: false }, "the card to be settled after the resume");
        // ★ 要等「真实的收口」(PhaseGuessed = false):activeRuns 广播先到时,Reconcile 会先把这个 Run 推断成 Done / Settled(PhaseGuessed = true,序号不动),
        // 真实的事件随后才补到——只等 Done + Settled 的话,这里会在两者之间读到旧序号。
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true, PhaseGuessed: false }, "the run to converge on the real end");
        Assert.True(a.State.Runs[run].Seq > seqBefore);
        Assert.Single(a.State.SubagentsOf(session)); // 没有重复的卡片

        // 一份完整的快照(游标丢了)重放整段日志,start 又来了一遍:终态不被复活
        var settled = Entry(a, card)!;
        await a.Link.WatchRunAsync(run.RunId);
        await Task.Delay(300);
        Assert.Equal(settled.Record.Status, Entry(a, card)!.Record.Status);
        Assert.Single(a.State.SubagentsOf(session));
    }

    // ───────────────────────── 待办 ─────────────────────────

    [Fact]
    public async Task A_TodoWrite_reaches_the_store_as_a_derived_view_that_follows_the_transcript()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, _) = await AddProjectAsync(a);
        var gate = new TaskCompletionSource();
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        const string Unfinished = """{"todos":[{"content":"read the code","status":"completed","activeForm":"Reading the code"},{"content":"fix the bug","status":"in_progress","activeForm":"Fixing the bug"}]}""";
        const string Finished = """{"todos":[{"content":"read the code","status":"completed","activeForm":"Reading the code"},{"content":"fix the bug","status":"completed","activeForm":"Fixing the bug"}]}""";
        _rig.Llm.On(session.SessionId,
            S.Call("t1", "TodoWrite", Unfinished),
            S.Gated(gate.Task, S.Call("t2", "TodoWrite", Finished)),
            S.Say("now really done"));

        var run = (await a.Conversations.SendAsync(session, "fix it", Options)).Run!.Value;

        // 第一份清单成功了:Store 里就有(未完成,当前项是「fix the bug」);Run 还卡在第二轮
        await WaitUntil(() => a.State.Sessions[session].Todo is { Total: 2 }, "the first checklist");
        var todo = a.State.Sessions[session].Todo!;
        Assert.Equal((1, "fix the bug", false), (todo.Completed, todo.Current!.Content, todo.IsComplete));
        Assert.Equal("Fixing the bug", todo.Current.ActiveForm);

        gate.SetResult();
        await WaitUntil(() => a.State.Sessions[session].Todo is { IsComplete: true }, "the checklist to be complete");
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the run to finish");

        // 另一个控制端后来才打开会话:同一份转录、同一个派生函数,同一个清单
        var b = await _rig.StartControllerAsync("ctrl-b");
        Assert.True(await b.Conversations.OpenSessionAsync(session));
        await WaitUntil(() => b.State.Sessions.ContainsKey(session) && b.State.Sessions[session].Todo is { IsComplete: true }, "B's checklist");
        Assert.Equal(a.State.Sessions[session].Todo!.Items, b.State.Sessions[session].Todo!.Items);
    }

    // ───────────────────────── 目标 ─────────────────────────

    [Fact]
    public async Task A_goal_is_mirrored_with_its_rounds_and_cleared_by_the_evaluator_and_both_controllers_agree()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var b = await _rig.StartControllerAsync("ctrl-b");
        var (project, _) = await AddProjectAsync(a);
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        Assert.True(await b.Conversations.OpenSessionAsync(session));
        var gate = new TaskCompletionSource();
        _rig.Llm.On(session.SessionId, S.Say("turn one"), S.Gated(gate.Task, S.Say("turn two")));
        _rig.Llm.Judge(S.Verdict(false, "tests are still red"), S.Verdict(true, "tests are green"));

        var set = await a.Goals.SetAsync(session, "make the tests green", Options);

        Assert.Equal((GoalOutcomes.Set, "launched"), (set.Value!.Outcome, set.Value.Delivery));
        // 第一轮判「未达成」:轮数 1、带理由,两个控制端都看得到(第二轮卡在门上)
        await WaitUntil(() => a.State.GoalOf(session) is { Iterations: 1, LastReason: "tests are still red" } && b.State.GoalOf(session) is { Iterations: 1 }, "round one on both controllers");
        Assert.Equal(GoalOrigin.User, a.State.GoalOf(session)!.Origin);
        Assert.Equal(a.State.GoalOf(session), b.State.GoalOf(session));
        Assert.Equal(a.State.GoalOf(session), (await a.Goals.GetAsync(session)).Value); // goal.get 与主题同一个类型

        gate.SetResult();
        await WaitUntil(() => a.State.Goals.GetValueOrDefault(session) is { Goal: null, Cleared: GoalClearReason.Met } && b.State.Goals.GetValueOrDefault(session) is { Goal: null, Cleared: GoalClearReason.Met }, "the goal to be met");
        Assert.Equal(a.State.Goals[session].Rev, b.State.Goals[session].Rev);
        // 带外写的 goal_status 标记随变更替换进会话镜像(不新增消息)
        await WaitUntil(() => a.State.Sessions[session].Messages.Any(m => m.PartsJson.Contains("goal_status", StringComparison.Ordinal)), "the goal markers in the mirror");

        // 设定 / 清除是幂等、可重复的意图;校验失败不是错误
        Assert.Equal(GoalOutcomes.Empty, (await a.Goals.SetAsync(session, "  ​ ")).Value!.Outcome);
        Assert.Equal(GoalOutcomes.Set, (await a.Goals.SetAsync(session, "again", Options)).Value!.Outcome);
        await WaitUntil(() => a.State.GoalOf(session)?.Condition == "again" || a.State.Goals[session].Goal is null, "the second goal");
        Assert.True((await a.Goals.ClearAsync(session)).IsOk);
        Assert.True((await a.Goals.ClearAsync(session)).IsOk);
        await WaitUntil(() => a.State.Goals[session] is { Goal: null } && b.State.Goals[session] is { Goal: null }, "the goal to be cleared");
    }

    // ───────────────────────── 计划 ─────────────────────────

    [Fact]
    public async Task An_approved_plan_is_executed_through_the_action_and_a_busy_session_refuses_it()
    {
        var a = await _rig.StartControllerAsync("ctrl-a");
        var (project, projectPath) = await AddProjectAsync(a);
        var session = (await a.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        var target = Path.Combine(projectPath, "out.txt");
        var planOptions = new SendOptionsDto("sonnet", Mode: "plan", PermissionMode: "full");
        _rig.Llm.On(session.SessionId,
            S.Call("c1", "EnterPlanMode", "{}"),
            (req, ct) => S.Call("c2", "Write", new System.Text.Json.Nodes.JsonObject
            {
                ["file_path"] = Path.Combine(projectPath, PlanPath(req)),
                ["content"] = "# Plan\n\n1. Write out.txt",
            }.ToJsonString())(req, ct),
            S.Call("c3", "ExitPlanMode", "{}"));

        var planning = await a.Conversations.SendAsync(session, "plan it", planOptions);
        await WaitUntil(() => a.State.InteractionsOf(session.DeviceId).Pending.OfType<PlanApprovalInteractionDto>().Any(), "the plan approval");
        var approval = a.State.InteractionsOf(session.DeviceId).Pending.OfType<PlanApprovalInteractionDto>().Single();
        Assert.Equal(InteractionOutcome.Answered, (await a.Conversations.RespondAsync(session.DeviceId, approval, new PlanApprovalResponseDto("approveCurrent"))).Outcome);
        await WaitUntil(() => a.State.Runs[planning.Run!.Value] is { Phase: RunPhase.Done, Settled: true }, "the planning run to end");

        // 会话忙:不进队列(计划只属于这个 RunId)
        _rig.Llm.On(session.SessionId, S.Hang());
        var busy = await a.Conversations.SendAsync(session, "something else", Options);
        await WaitUntil(() => _rig.Llm.CountOf(session.SessionId) == 4, "the busy run to be talking to the model");
        Assert.Equal(SendOutcome.Busy, (await a.Conversations.ExecutePlanAsync(session, approval, "implement", Options)).Outcome);
        Assert.True(await a.Conversations.AbortAsync(busy.Run!.Value));
        await WaitUntil(() => a.State.Runs[busy.Run!.Value] is { Settled: true }, "the busy run to end");

        // 空闲了:code 模式执行,带着计划;写入不再被围栏拦
        _rig.Llm.On(session.SessionId, S.Call("d1", "Write", new System.Text.Json.Nodes.JsonObject { ["file_path"] = target, ["content"] = "done" }.ToJsonString()), S.Say("implemented"));
        var executed = await a.Conversations.ExecutePlanAsync(session, approval, "implement the plan", new SendOptionsDto("sonnet", PermissionMode: "full"));
        Assert.Equal(SendOutcome.Started, executed.Outcome);
        await WaitUntil(() => a.State.Runs[executed.Run!.Value] is { Phase: RunPhase.Done, Settled: true }, "the executing run to finish");
        Assert.Equal("done", File.ReadAllText(target));
    }

    private static string PlanPath(Ncw.Agent.Llm.LlmRequest request)
    {
        var content = request.Messages.SelectMany(m => m.Parts).OfType<Ncw.Agent.Llm.ToolResultPart>().Select(r => r.Output.Content).First(c => c.Contains("plan_file_entered"));
        using var doc = System.Text.Json.JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("path").GetString()!;
    }
}
