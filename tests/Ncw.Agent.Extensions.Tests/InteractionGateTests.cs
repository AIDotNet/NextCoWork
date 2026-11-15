using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// 交互闸门(移植自 2.x <c>interaction-gate.test.ts</c> 的全部用例,另补 3.0 的版本号 / 冲突 / 全量通知 / 子代理 / 执行环境标识)。
/// 每个用例都对应 03 §7 不变量清单里的一条:「交互无超时、可查询、多端先到先得」「子代理不能发起交互」。
/// </summary>
public sealed class InteractionGateTests
{
    private static readonly ExecutionEnvironmentRef Env = new("dev-1", "local", "Local", "local");

    private readonly InteractionGate _gate;
    private readonly RecordingJournal _journal = new();
    private readonly List<InteractionsSnapshot> _snapshots = [];
    private readonly List<(RunId Run, AgentEvent Event)> _events = [];

    public InteractionGateTests()
    {
        _gate = new InteractionGate(new SequentialIds(), journal: _journal);
        _gate.Changed += s =>
        {
            lock (_snapshots)
            {
                _snapshots.Add(s);
            }
        };
        _gate.RunEvent += (r, e) =>
        {
            lock (_events)
            {
                _events.Add((r, e));
            }
        };
    }

    private static ToolPermission Permission(RememberScopes scopes = RememberScopes.Session | RememberScopes.Project, string? rule = "Write(a)") => new()
    {
        CallId = "c",
        ToolName = "Write",
        InternalId = "Write",
        Input = """{"file_path":"a"}""".Json(),
        Traits = new ToolTraits(false, true, false),
        Env = Env,
        SuggestedRule = rule,
        AllowedScopes = scopes,
    };

    private static AskUser Ask(params AskUserQuestion[] questions) => new(questions);

    private static AskUserQuestion Q(string header, bool multi, bool freeform, params string[] options) =>
        new(header, header + "?", options.Select(o => new AskUserOption(o)).ToList(), multi, freeform);

    private static ToolPermissionResponse Choice(PermissionChoice c) => new(c);

    private static AskUserResponse Answers(params string[][] answers) => new(answers.Select(a => (IReadOnlyList<string>)a).ToList());

    private InteractionRequest Only() => Assert.Single(_gate.Snapshot().Pending);

    private RespondResult Respond(InteractionRequest r, InteractionResponse resp, string by = "ctrl-a", long? version = null) =>
        _gate.Respond(r.Id, version ?? r.Version, resp, by);

    // ───────────────────────── 基本流程 ─────────────────────────

    [Fact]
    public async Task Records_the_request_accepts_an_edited_input_once_and_clears_the_pending_state()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission());
        var pending = (ToolPermission)Only();
        Assert.Equal("dev-1", pending.Env.DeviceId);
        Assert.Equal(run.Interactions.RunId, pending.RunId);

        var response = Choice(PermissionChoice.AllowEdited("""{"file_path":"b"}""".Json()));
        Assert.IsType<RespondResult.Accepted>(Respond(pending, response));
        var answered = (ToolPermissionResponse)await task;
        Assert.Equal("b", ((PermissionChoice.AllowEditedChoice)answered.Choice).Input.GetProperty("file_path").GetString());
        Assert.Empty(_gate.Snapshot().Pending);
        Assert.Equal(0, _gate.PendingCount(run.Interactions.RunId));
        // 第二次回答(即使一模一样)也失败——这是结构自带的性质,不靠比较答案
        Assert.IsType<RespondResult.Conflict>(Respond(pending, response, "ctrl-b"));
    }

    [Fact]
    public async Task Rejects_mismatched_kinds_invalid_answers_and_unavailable_scopes_without_consuming_the_interaction()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Ask(Q("Pick", false, false, "A", "B")));
        var ask = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(ask, new PlanApprovalResponse(PlanApprovalAction.ApproveCurrent)));
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["C"])));
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["A", "B"])));
        Assert.IsType<RespondResult.Invalid>(Respond(ask, new AskUserResponse([])));
        Assert.Single(_gate.Snapshot().Pending);
        Assert.IsType<RespondResult.Accepted>(Respond(ask, Answers(["B"])));
        Assert.Equal([["B"]], ((AskUserResponse)await task).Answers);

        // allow_always 只接受请求里开放的作用域
        var approval = run.Interactions.RequestAsync(Permission(RememberScopes.Session | RememberScopes.Project));
        var permission = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(permission, Choice(PermissionChoice.AllowAlways(RememberScope.Global))));
        Assert.IsType<RespondResult.Accepted>(Respond(permission, Choice(PermissionChoice.AllowAlways(RememberScope.Project))));
        Assert.IsType<PermissionChoice.AllowAlwaysChoice>(((ToolPermissionResponse)await approval).Choice);
    }

    [Fact]
    public async Task Allow_always_without_a_suggested_rule_is_invalid_there_is_nothing_to_remember()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission(rule: null));
        var p = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(p, Choice(PermissionChoice.AllowAlways(RememberScope.Session))));
        Respond(p, Choice(PermissionChoice.AllowOnce));
        await task;
    }

    [Fact]
    public async Task An_edited_input_must_be_a_json_object()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission());
        var p = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(p, Choice(PermissionChoice.AllowEdited("[1]".Json()))));
        Respond(p, Choice(PermissionChoice.Deny("no")));
        await task;
    }

    // ───────────────────────── 提问(2.x 的逐题校验) ─────────────────────────

    [Fact]
    public async Task Multi_question_multi_select_validates_per_question_and_rejects_a_wrong_length_wholesale()
    {
        // ★ 多题时答案是按下标对到题上的。长度不等必须整份退回:窗口重载后拿着旧题面抢答,按下标对齐会把答案安到别的题上,而两边都不会报错。
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Ask(Q("范围", true, false, "A", "B"), Q("细节", false, true)));
        var ask = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["A"])));
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["A"], [])));
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["自己写的"], ["随便"]))); // 第一题不许自由作答
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["A", "A"], ["随便"]))); // 同一项勾两次
        Assert.IsType<RespondResult.Accepted>(Respond(ask, Answers(["A", "B"], ["随便写的"])));
        Assert.Equal([["A", "B"], ["随便写的"]], ((AskUserResponse)await task).Answers);
    }

    [Fact]
    public async Task A_single_select_question_takes_one_answer_and_dismiss_is_always_accepted()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Ask(Q("范围", false, true, "A", "B")));
        var ask = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers(["A", "B"])));
        Assert.IsType<RespondResult.Accepted>(Respond(ask, new AskUserResponse(null)));
        Assert.Null(((AskUserResponse)await task).Answers);
    }

    [Fact]
    public async Task Answers_must_be_non_empty_and_at_most_32768_characters()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Ask(Q("x", false, true)));
        var ask = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers([""])));
        Assert.IsType<RespondResult.Invalid>(Respond(ask, Answers([new string('a', 32769)])));
        Assert.IsType<RespondResult.Accepted>(Respond(ask, Answers([new string('a', 32768)])));
        await task;
    }

    [Fact]
    public void A_draft_with_an_unanswerable_or_oversized_question_is_refused_at_the_source()
    {
        using var run = new Scope(_gate);
        Assert.Throws<ArgumentException>(() => run.Interactions.RequestAsync(Ask(Q("x", false, false))).GetAwaiter().GetResult());                // 没有选项又不许自由作答
        Assert.Throws<ArgumentException>(() => run.Interactions.RequestAsync(Ask()).GetAwaiter().GetResult());                                     // 0 题
        Assert.Throws<ArgumentException>(() => run.Interactions.RequestAsync(Ask(Enumerable.Range(0, 5).Select(i => Q($"q{i}", false, true)).ToArray())).GetAwaiter().GetResult()); // 5 题
        Assert.Throws<ArgumentException>(() => run.Interactions.RequestAsync(Ask(Q("x", false, true, Enumerable.Range(0, 9).Select(i => $"o{i}").ToArray()))).GetAwaiter().GetResult()); // 9 个选项
        Assert.Throws<ArgumentException>(() => run.Interactions.RequestAsync(Ask(Q("x", false, true, "A", "A"))).GetAwaiter().GetResult());        // 重复标签
        Assert.Empty(_gate.Snapshot().Pending);
    }

    // ───────────────────────── 计划 / 工作流 / 目标 / widget ─────────────────────────

    [Fact]
    public async Task Plan_decisions_are_accepted_and_a_revision_request_needs_feedback()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(new PlanApproval("# Plan", "plan-1", ".plan/plan-1.md"));
        var plan = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(plan, new PlanApprovalResponse(PlanApprovalAction.RequestRevision)));
        Assert.IsType<RespondResult.Invalid>(Respond(plan, new PlanApprovalResponse(PlanApprovalAction.RequestRevision, "   ")));
        Assert.IsType<RespondResult.Invalid>(Respond(plan, new PlanApprovalResponse((PlanApprovalAction)99)));
        Assert.IsType<RespondResult.Accepted>(Respond(plan, new PlanApprovalResponse(PlanApprovalAction.RequestRevision, "Add tests.")));
        Assert.Equal("Add tests.", ((PlanApprovalResponse)await task).Feedback);
    }

    [Fact]
    public async Task Workflow_approval_accepts_three_actions_and_revise_needs_feedback()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(new WorkflowApproval("call_1", """{"title":"W","nodes":[]}""".Json()));
        var wf = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(wf, new WorkflowApprovalResponse((WorkflowApprovalAction)7)));
        Assert.IsType<RespondResult.Invalid>(Respond(wf, new WorkflowApprovalResponse(WorkflowApprovalAction.Revise)));
        Assert.IsType<RespondResult.Invalid>(Respond(wf, new WorkflowApprovalResponse(WorkflowApprovalAction.Revise, "  ")));
        Assert.IsType<RespondResult.Accepted>(Respond(wf, new WorkflowApprovalResponse(WorkflowApprovalAction.Revise, "加一个测试节点")));
        Assert.Equal("加一个测试节点", ((WorkflowApprovalResponse)await task).Feedback);
    }

    [Fact]
    public async Task Widget_submit_values_are_size_limited_and_null_means_dismissed()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(new WidgetSubmit("c", "Pick"));
        var w = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(w, new WidgetSubmitResponse(new string('x', 32 * 1024 + 1))));
        Assert.IsType<RespondResult.Accepted>(Respond(w, new WidgetSubmitResponse(null)));
        Assert.Null(((WidgetSubmitResponse)await task).Value);
    }

    [Fact]
    public async Task A_goal_proposal_is_answered_with_a_bool_and_a_wrong_kind_is_rejected()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(new GoalProposal("s", "make bun test green"));
        var goal = Only();
        Assert.IsType<RespondResult.Invalid>(Respond(goal, new AskUserResponse(null)));
        Assert.Single(_gate.Snapshot().Pending);
        Assert.IsType<RespondResult.Accepted>(Respond(goal, new GoalProposalResponse(true)));
        Assert.True(((GoalProposalResponse)await task).Approved);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_goal_proposal_does_not_hold_the_run_open_it_stays_answerable_after_a_normal_run_end()
    {
        // ★ 批准要问的是一句「这个目标要不要」,而模型这时候该继续干活。按其他交互那样在 run_end 一律中止的话,一个说得很快的模型会让弹窗在用户看清之前自己关掉。
        var run = _gate.OpenRun(new RunId("r-goal"));
        var task = run.RequestAsync(new GoalProposal("s", "x"));
        var goal = Only();
        run.End(RunStatus.Done);
        Assert.Single(_gate.Snapshot().Pending);
        Assert.IsType<RespondResult.Accepted>(Respond(goal, new GoalProposalResponse(false)));
        Assert.False(((GoalProposalResponse)await task).Approved);
        Assert.Empty(_gate.Snapshot().Pending);
        Assert.IsType<RespondResult.Conflict>(Respond(goal, new GoalProposalResponse(true)));
    }

    [Theory]
    [InlineData(RunStatus.Aborted)]
    [InlineData(RunStatus.Error)]
    public async Task A_goal_proposal_is_settled_as_aborted_when_the_run_aborts_or_fails(RunStatus status)
    {
        var run = _gate.OpenRun(new RunId("r-goal"));
        var task = run.RequestAsync(new GoalProposal("s", "x"));
        var goal = Only();
        run.End(status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_gate.Snapshot().Pending);
        var late = Assert.IsType<RespondResult.Conflict>(Respond(goal, new GoalProposalResponse(true)));
        Assert.Equal(ResolutionStatus.Aborted, late.Resolution!.Status);
    }

    [Fact]
    public async Task AbortWhere_settles_only_what_matches_for_example_one_sessions_goal_proposals()
    {
        var run = _gate.OpenRun(new RunId("r"));
        var first = run.RequestAsync(new GoalProposal("s1", "a"));
        var second = run.RequestAsync(new GoalProposal("s2", "b"));
        var asked = run.RequestAsync(Ask(Q("继续", false, true)));
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 3);

        Assert.Equal(1, _gate.AbortWhere(r => r is GoalProposal { SessionId: "s1" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(["ask_user", "goal_proposal"], _gate.Snapshot().Pending.Select(p => p.Kind).Order());

        // 没被取消的那两条照常回答
        Respond(_gate.Snapshot().Pending.First(p => p is GoalProposal), new GoalProposalResponse(true));
        Assert.True(((GoalProposalResponse)await second).Approved);
        Respond(_gate.Snapshot().Pending.Single(), Answers(["好"]));
        Assert.Equal([["好"]], ((AskUserResponse)await asked).Answers);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    // ───────────────────────── 中止 / run_end ─────────────────────────

    [Fact]
    public async Task Cancelling_the_token_settles_the_blocked_call_as_aborted_and_rejects_late_approvals()
    {
        using var run = new Scope(_gate);
        using var cts = new CancellationTokenSource();
        var task = run.Interactions.RequestAsync(Ask(Q("Continue", false, true)), cts.Token);
        var ask = Only();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_gate.Snapshot().Pending);
        var late = Assert.IsType<RespondResult.Conflict>(Respond(ask, Answers(["late"])));
        Assert.Equal(ConflictReason.AlreadyResolved, late.Reason);
        Assert.Equal(ResolutionStatus.Aborted, late.Resolution!.Status);
        Assert.Null(late.Resolution.ResolvedBy);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_never_registers_anything()
    {
        using var run = new Scope(_gate);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Interactions.RequestAsync(Permission(), cts.Token));
        Assert.Empty(_gate.Snapshot().Pending);
        Assert.Equal(0, _gate.Revision);
    }

    [Theory]
    [InlineData(RunStatus.Aborted)]
    [InlineData(RunStatus.Error)]
    [InlineData(RunStatus.Done)]
    public async Task Ending_the_run_settles_what_it_left_pending_and_a_closed_scope_refuses_new_requests(RunStatus status)
    {
        var run = _gate.OpenRun(new RunId("r"));
        var task = run.RequestAsync(Permission());
        var p = Only();
        run.End(status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_gate.Snapshot().Pending);
        Assert.IsType<RespondResult.Conflict>(Respond(p, Choice(PermissionChoice.AllowOnce)));
        await Assert.ThrowsAsync<InteractionRefusedException>(() => run.RequestAsync(Permission()));
        run.End(RunStatus.Aborted); // 幂等
    }

    [Fact]
    public async Task Aborting_one_run_leaves_the_other_runs_interactions_alone()
    {
        var a = _gate.OpenRun(new RunId("a"));
        var b = _gate.OpenRun(new RunId("b"));
        var ta = a.RequestAsync(Permission());
        var tb = b.RequestAsync(Permission());
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 2);
        a.End(RunStatus.Aborted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ta);
        Assert.Equal(new RunId("b"), Assert.Single(_gate.Snapshot().Pending).RunId);
        Assert.False(tb.IsCompleted);
        Assert.Equal(1, _gate.PendingCount(new RunId("b")));
        Assert.Equal(0, _gate.PendingCount(new RunId("a")));
        b.End(RunStatus.Aborted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tb);
    }

    [Fact]
    public async Task AbortAll_clears_everything_when_the_agent_shuts_down()
    {
        var a = _gate.OpenRun(new RunId("a"));
        var t1 = a.RequestAsync(Permission());
        var t2 = a.RequestAsync(Permission());
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 2);
        Assert.Equal(2, _gate.AbortAll());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task There_is_no_timeout_an_interaction_stays_pending_until_answered_or_aborted()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission());
        await Task.Delay(300);
        Assert.False(task.IsCompleted);
        Assert.Single(_gate.Snapshot().Pending);
        Respond(Only(), Choice(PermissionChoice.AllowOnce));
        await task;
    }

    // ───────────────────────── 先到先得 / 版本 ─────────────────────────

    [Fact]
    public async Task Two_controllers_answering_at_once_exactly_one_wins_and_the_loser_is_told_who_and_when()
    {
        for (var round = 0; round < 40; round++)
        {
            var run = _gate.OpenRun(new RunId($"r{round}"));
            var task = run.RequestAsync(Permission());
            var p = await Wait.UntilNotNullAsync(() => _gate.Snapshot(new RunId($"r{round}")).Pending.FirstOrDefault());
            using var barrier = new Barrier(2);
            RespondResult Go(string who, PermissionChoice choice)
            {
                barrier.SignalAndWait();
                return _gate.Respond(p.Id, p.Version, Choice(choice), who);
            }

            var a = Task.Run(() => Go("phone", PermissionChoice.AllowOnce));
            var b = Task.Run(() => Go("desktop", PermissionChoice.Deny("no")));
            var results = await Task.WhenAll(a, b);
            var winner = Assert.Single(results.OfType<RespondResult.Accepted>());
            var loser = Assert.Single(results.OfType<RespondResult.Conflict>());
            Assert.Equal(ConflictReason.AlreadyResolved, loser.Reason);
            Assert.Equal(winner.Resolution.ResolvedBy, loser.Resolution!.ResolvedBy);
            Assert.Equal(winner.Resolution.At, loser.Resolution.At);
            Assert.Equal(ResolutionStatus.Answered, loser.Resolution.Status);
            Assert.Contains(winner.Resolution.ResolvedBy, new[] { "phone", "desktop" });
            // 内核拿到的恰好是赢家的答案
            var got = ((ToolPermissionResponse)await task).Choice;
            Assert.Equal(winner.Resolution.ResolvedBy == "phone", got is PermissionChoice.AllowOnceChoice);
            run.End(RunStatus.Done);
        }
    }

    [Fact]
    public async Task The_same_answer_twice_still_fails_the_second_time()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission());
        var p = Only();
        Assert.IsType<RespondResult.Accepted>(Respond(p, Choice(PermissionChoice.AllowOnce), "a"));
        var second = Assert.IsType<RespondResult.Conflict>(Respond(p, Choice(PermissionChoice.AllowOnce), "b"));
        Assert.Equal("a", second.Resolution!.ResolvedBy);
        await task;
    }

    [Fact]
    public async Task A_wrong_expected_version_is_a_conflict_and_does_not_consume_the_interaction()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission());
        var p = Only();
        var stale = Assert.IsType<RespondResult.Conflict>(Respond(p, Choice(PermissionChoice.AllowOnce), version: p.Version - 1));
        Assert.Equal(ConflictReason.StaleVersion, stale.Reason);
        Assert.Equal(p.Version, stale.CurrentVersion);
        Assert.False(task.IsCompleted);
        Assert.IsType<RespondResult.Accepted>(Respond(p, Choice(PermissionChoice.AllowOnce)));
        await task;
    }

    [Fact]
    public void An_unknown_id_is_not_found_not_a_conflict()
    {
        Assert.IsType<RespondResult.NotFound>(_gate.Respond(new InteractionId("nope"), 1, Choice(PermissionChoice.AllowOnce), "x"));
    }

    [Fact]
    public async Task Versions_are_strictly_increasing_and_unique_across_runs_and_settlements()
    {
        var a = _gate.OpenRun(new RunId("a"));
        var b = _gate.OpenRun(new RunId("b"));
        _ = a.RequestAsync(Permission());
        _ = b.RequestAsync(Permission());
        _ = a.RequestAsync(Permission());
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 3);
        var versions = _gate.Snapshot().Pending.Select(p => p.Version).ToList();
        Assert.Equal(versions.Order().ToList(), versions);
        Assert.Equal(3, versions.Distinct().Count());
        var before = _gate.Revision;
        a.End(RunStatus.Aborted);
        Assert.True(_gate.Revision > before);
    }

    [Fact]
    public async Task The_history_is_bounded_but_recent_resolutions_are_remembered()
    {
        var gate = new InteractionGate(new SequentialIds(), historyLimit: 3);
        var run = gate.OpenRun(new RunId("r"));
        var requests = new List<InteractionRequest>();
        for (var i = 0; i < 5; i++)
        {
            var task = run.RequestAsync(Permission());
            var p = gate.Snapshot().Pending.Single();
            requests.Add(p);
            gate.Respond(p.Id, p.Version, Choice(PermissionChoice.AllowOnce), "x");
            await task;
        }

        Assert.IsType<RespondResult.NotFound>(gate.Respond(requests[0].Id, requests[0].Version, Choice(PermissionChoice.AllowOnce), "x"));
        Assert.IsType<RespondResult.Conflict>(gate.Respond(requests[4].Id, requests[4].Version, Choice(PermissionChoice.AllowOnce), "x"));
        Assert.NotNull(gate.GetResolution(requests[4].Id));
        Assert.Null(gate.GetResolution(requests[0].Id));
    }

    // ───────────────────────── 可查询 / 全量通知 ─────────────────────────

    [Fact]
    public async Task Pending_interactions_are_queryable_after_a_reconnect_by_snapshot_get_and_per_run()
    {
        var a = _gate.OpenRun(new RunId("a"));
        var b = _gate.OpenRun(new RunId("b"));
        _ = a.RequestAsync(Permission());
        _ = b.RequestAsync(Ask(Q("x", false, true)));
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 2);
        var all = _gate.Snapshot();
        Assert.Equal(["tool_permission", "ask_user"], all.Pending.Select(p => p.Kind));
        Assert.Equal(1, all.CountFor(new RunId("a")));
        Assert.Equal(["ask_user"], _gate.Snapshot(new RunId("b")).Pending.Select(p => p.Kind));
        Assert.Same(all.Pending[0], _gate.Get(all.Pending[0].Id));
        Assert.Null(_gate.Get(new InteractionId("nope")));
        Assert.Equal(_gate.Revision, all.Revision);
    }

    [Fact]
    public async Task Change_notifications_are_the_full_snapshot_type_with_strictly_increasing_revisions()
    {
        // 状态统一管理第 5 条:变更通知和首帧是同一个类型,没有增量,收到就对账。
        using var run = new Scope(_gate);
        var t1 = run.Interactions.RequestAsync(Permission());
        var t2 = run.Interactions.RequestAsync(Permission());
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 2);
        Respond(_gate.Snapshot().Pending[0], Choice(PermissionChoice.AllowOnce));
        await t1;
        _gate.AbortAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);

        List<InteractionsSnapshot> seen;
        lock (_snapshots)
        {
            seen = [.. _snapshots];
        }

        Assert.Equal([1, 2, 1, 0], seen.Select(s => s.Pending.Count));
        Assert.Equal(seen.Select(s => s.Revision).Order().ToList(), seen.Select(s => s.Revision));
        Assert.Equal(seen.Count, seen.Select(s => s.Revision).Distinct().Count());
        Assert.Equal(_gate.Snapshot().Revision, seen[^1].Revision);
    }

    [Fact]
    public async Task Notifications_are_dispatched_serially_and_in_order_even_under_concurrent_changes()
    {
        var gate = new InteractionGate(new SequentialIds());
        var revisions = new List<long>();
        var inCallback = 0;
        var overlapped = false;
        gate.Changed += s =>
        {
            if (Interlocked.Increment(ref inCallback) > 1)
            {
                overlapped = true;
            }

            lock (revisions)
            {
                revisions.Add(s.Revision);
            }

            Thread.SpinWait(200);
            Interlocked.Decrement(ref inCallback);
        };
        var run = gate.OpenRun(new RunId("r"));
        var tasks = Enumerable.Range(0, 60).Select(_ => Task.Run(() => run.RequestAsync(Permission()))).ToList();
        await Wait.UntilAsync(() => gate.Snapshot().Pending.Count == 60);
        run.End(RunStatus.Aborted);
        await Task.WhenAll(tasks.Select(async t =>
        {
            try
            {
                await t;
            }
            catch (OperationCanceledException)
            {
            }
        }));
        await Wait.UntilAsync(() => revisions.Count == 120);
        Assert.False(overlapped);
        Assert.Equal(revisions.Order().ToList(), revisions);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_change_the_decision()
    {
        var gate = new InteractionGate(new SequentialIds());
        gate.Changed += _ => throw new InvalidOperationException("ui exploded");
        var run = gate.OpenRun(new RunId("r"));
        var task = run.RequestAsync(Permission());
        var p = gate.Snapshot().Pending.Single();
        Assert.IsType<RespondResult.Accepted>(gate.Respond(p.Id, p.Version, Choice(PermissionChoice.AllowOnce), "x"));
        await task;
    }

    [Fact]
    public async Task The_run_event_stream_gets_interaction_request_and_resolved_events()
    {
        using var run = new Scope(_gate);
        var task = run.Interactions.RequestAsync(Permission());
        var p = Only();
        Respond(p, Choice(PermissionChoice.AllowOnce), "phone");
        await task;
        lock (_events)
        {
            Assert.Equal(2, _events.Count);
            Assert.Equal(p, Assert.IsType<InteractionRequestEvent>(_events[0].Event).Interaction);
            var resolved = Assert.IsType<InteractionResolvedEvent>(_events[1].Event);
            Assert.Equal(ResolutionStatus.Answered, resolved.Resolution.Status);
            Assert.Equal("phone", resolved.Resolution.ResolvedBy);
            Assert.IsType<ToolPermissionResponse>(resolved.Response);
        }
    }

    [Fact]
    public async Task An_aborted_interaction_emits_interaction_resolved_aborted_with_no_response()
    {
        var run = _gate.OpenRun(new RunId("r"));
        var task = run.RequestAsync(Permission());
        run.End(RunStatus.Aborted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        lock (_events)
        {
            var resolved = Assert.IsType<InteractionResolvedEvent>(_events[^1].Event);
            Assert.Equal(ResolutionStatus.Aborted, resolved.Resolution.Status);
            Assert.Null(resolved.Response);
        }
    }

    [Fact]
    public async Task The_journal_sees_every_open_and_every_settlement()
    {
        using var run = new Scope(_gate);
        var t1 = run.Interactions.RequestAsync(Permission());
        var t2 = run.Interactions.RequestAsync(Permission());
        await Wait.UntilAsync(() => _gate.Snapshot().Pending.Count == 2);
        Respond(_gate.Snapshot().Pending[0], Choice(PermissionChoice.AllowOnce), "phone");
        await t1;
        run.Interactions.End(RunStatus.Aborted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
        Assert.Equal(2, _journal.Opened.Count);
        Assert.Equal([ResolutionStatus.Answered, ResolutionStatus.Aborted], _journal.Settled.Select(s => s.Status));
        Assert.Equal("phone", _journal.Settled[0].ResolvedBy);
    }

    // ───────────────────────── 子代理 / 执行环境 ─────────────────────────

    [Fact]
    public async Task A_subagent_cannot_ask_it_is_refused_on_the_spot_instead_of_hanging_forever()
    {
        // ★ 2.x 真实发生过的死锁:子代理的待决项在界面上走不到用户面前,于是 await 永远不会结算。
        var child = _gate.OpenRun(new RunId("child"), depth: 1);
        Assert.False(child.CanInteract);
        var e1 = await Assert.ThrowsAsync<InteractionRefusedException>(() => child.RequestAsync(Permission()));
        Assert.Contains("subagent", e1.Message);
        await Assert.ThrowsAsync<InteractionRefusedException>(() => child.RequestAsync(Ask(Q("x", false, true))));
        Assert.Empty(_gate.Snapshot().Pending);
        Assert.Equal(0, _gate.Revision);
    }

    [Fact]
    public void Only_a_top_level_run_has_the_Interact_capability()
    {
        Assert.True(_gate.OpenRun(new RunId("top")).CanInteract);
        Assert.False(_gate.OpenRun(new RunId("sub"), depth: 2).CanInteract);
    }

    [Fact]
    public async Task A_tool_approval_must_carry_the_execution_environment_device_and_environment()
    {
        using var run = new Scope(_gate);
        var p = Permission();
        Assert.Equal("dev-1", p.Env.DeviceId);
        Assert.Equal("local", p.Env.Environment);
        var noEnv = p with { Env = null! };
        await Assert.ThrowsAsync<ArgumentException>(() => run.Interactions.RequestAsync(noEnv));
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public void Opening_a_run_twice_is_an_error()
    {
        _gate.OpenRun(new RunId("r"));
        Assert.Throws<InvalidOperationException>(() => _gate.OpenRun(new RunId("r")));
    }

    private sealed class Scope : IDisposable
    {
        private static int _n;

        public Scope(InteractionGate gate) => Interactions = gate.OpenRun(new RunId($"scope-{Interlocked.Increment(ref _n)}"));

        public RunInteractions Interactions { get; }

        public void Dispose() => Interactions.End(RunStatus.Aborted);
    }
}
