using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.Goals;

namespace Ncw.Agent.Planning.Tests;

/// <summary>
/// 端到端:<c>AgentLoop</c> / <c>RunRegistry</c> + 假上游 + <b>真实工具</b>(Read / Write / Edit 跑在 <c>LocalEnvironment</c> 上)+ 真实的权限扩展 / 交互闸门 + 计划 / 待办 / 目标三个一方扩展。
/// 验证接缝:进入计划模式后写入被围栏拦住 → 批准计划后退出并可写、TodoWrite 对账、目标续跑直到完成、空转刹车触发,以及各自跨 Run 的状态。
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string S = "s1";
    private const string Goal = "让 bun test 全绿(转录里有这次运行的结果)";

    private readonly E2eRig _rig = new();

    public EndToEndTests() => _rig.NewSessionRow(S);

    public void Dispose() => _rig.Dispose();

    private static Script Say(string text) => Up.Text(text);

    private string PlanPath => _rig.Plans.ActiveFor(S)!.Absolute.Value;

    private static string WriteArgs(string path, string content) => J.Obj(("file_path", path), ("content", content));

    private async Task<PlanApproval> PendingPlan() =>
        await Wait.UntilNotNull(() => _rig.Gate.Snapshot().Pending.OfType<PlanApproval>().FirstOrDefault(), what: "a plan approval");

    private RespondResult AnswerPlan(PlanApproval pending, PlanApprovalAction action, string? feedback = null) =>
        _rig.Gate.Respond(pending.Id, pending.Version, new PlanApprovalResponse(action, feedback), "ctrl");

    private static string ToolResult(IEnumerable<ToolResultPart> results, string callId) => results.Single(r => r.CallId == callId).Output.Content;

    private static bool IsError(IEnumerable<ToolResultPart> results, string callId) => results.Single(r => r.CallId == callId).IsError;

    // ───────────────────────── 计划模式 ─────────────────────────

    [Fact]
    public async Task Plan_mode_fences_writes_until_the_plan_is_approved_and_then_the_run_ends_and_writes_work_again()
    {
        _rig.PlanWorkflow = true;
        _rig.Put("src.ts", "old");
        _rig.Llm.Main(
            Up.Call("c1", "EnterPlanMode", "{}"),
            Up.Call("c2", "Write", WriteArgs(_rig.Abs("src.ts"), "no")), // 围栏之外:被拦
            Up.Lazy(() => Up.Call("c3", "Write", WriteArgs(PlanPath, "# Plan\n\n1. Do it."))),
            Up.Call("c4", "ExitPlanMode", "{}"));

        var run = _rig.Send(S, "做个计划");
        var pending = await PendingPlan();
        Assert.Equal("# Plan\n\n1. Do it.", pending.Plan);
        Assert.Equal(_rig.Plans.ActiveFor(S)!.Path, pending.Path);
        Assert.IsType<RespondResult.Accepted>(AnswerPlan(pending, PlanApprovalAction.ApproveCurrent));
        await E2eRig.Settled(run);

        var results = _rig.Results(S).ToList();
        Assert.Equal(RunState.Done, run.State);
        Assert.False(IsError(results, "c1"));
        Assert.Contains("plan_file_entered", ToolResult(results, "c1"));
        // 进入计划模式后写入被围栏拦住,文件一个字节没动
        Assert.True(IsError(results, "c2"));
        Assert.Contains("only modify its active Markdown plan", ToolResult(results, "c2"));
        Assert.Equal("old", File.ReadAllText(_rig.Abs("src.ts")));
        Assert.False(IsError(results, "c3"), ToolResult(results, "c3"));
        Assert.Contains("\"action\":\"approve_current\"", ToolResult(results, "c4"));
        // 批准即终点:Run 在 ExitPlanMode 之后没有再请求一次模型
        Assert.Equal(4, _rig.Llm.MainRequests.Count);
        Assert.Null(_rig.Plans.ActiveFor(S));
        Assert.Null(_rig.Runtimes[run.RunId].WriteFence);

        // 工具表每回合现算:没有计划时只有 EnterPlanMode;计划活着时 Write / Edit / ExitPlanMode 回来、EnterPlanMode 走
        var first = E2eRig.ToolNames(_rig.Llm.MainRequests[0]);
        Assert.Contains("EnterPlanMode", first);
        Assert.DoesNotContain("Write", first);
        Assert.DoesNotContain("Edit", first);
        Assert.DoesNotContain("ExitPlanMode", first);
        var second = E2eRig.ToolNames(_rig.Llm.MainRequests[1]);
        Assert.DoesNotContain("EnterPlanMode", second);
        Assert.Contains("Write", second);
        Assert.Contains("Edit", second);
        Assert.Contains("ExitPlanMode", second);
        // 状态块里带着计划文件路径(压缩会抹掉工具输出里的那份)
        Assert.Contains("Active Plan mode file: .plan/", E2eRig.WireText(_rig.Llm.MainRequests[1]));

        // 批准之后:新 Run 里写入不再被围栏拦(这次是 code 模式)
        _rig.PlanWorkflow = false;
        _rig.Llm.Main(
            Up.Call("d1", "Read", J.Obj(("file_path", _rig.Abs("src.ts")))),
            Up.Call("d2", "Write", WriteArgs(_rig.Abs("src.ts"), "new")),
            Say("写好了"));
        await _rig.RunAsync(S, "按计划做");

        Assert.False(IsError(_rig.Results(S), "d2"));
        Assert.Equal("new", File.ReadAllText(_rig.Abs("src.ts")));
        var later = E2eRig.ToolNames(_rig.Llm.MainRequests[4]);
        Assert.DoesNotContain("EnterPlanMode", later);
        Assert.DoesNotContain("ExitPlanMode", later);
        Assert.Contains("Write", later);
    }

    [Fact]
    public async Task A_revision_request_keeps_the_plan_across_runs_and_the_fence_is_back_in_the_next_run()
    {
        _rig.PlanWorkflow = true;
        _rig.Put("src.ts", "old");
        _rig.Llm.Main(
            Up.Call("c1", "EnterPlanMode", "{}"),
            Up.Lazy(() => Up.Call("c2", "Write", WriteArgs(PlanPath, "# Draft"))),
            Up.Call("c3", "ExitPlanMode", "{}"),
            Say("好的,下一轮我来改。"));

        var first = _rig.Send(S, "做个计划");
        AnswerPlan(await PendingPlan(), PlanApprovalAction.RequestRevision, "Add tests.");
        await E2eRig.Settled(first);

        Assert.Equal(RunState.Done, first.State);
        Assert.Contains("Add tests.", ToolResult(_rig.Results(S), "c3"));
        // 要求修订:计划留着(修订往往发生在下一轮)
        var plan = _rig.Plans.ActiveFor(S)!;

        // 下一轮(新 RunId、新的 ToolRuntime):围栏已经在,Write / Edit / ExitPlanMode 还在工具表里
        _rig.Llm.Main(
            Up.Call("d1", "Write", WriteArgs(_rig.Abs("src.ts"), "sneaky")),
            Up.Call("d2", "Read", J.Obj(("file_path", plan.Absolute.Value))),
            Up.Call("d3", "Edit", J.Obj(("file_path", plan.Absolute.Value), ("old_string", "# Draft"), ("new_string", "# Draft\n\n- add tests"))),
            Up.Call("d4", "ExitPlanMode", "{}"));
        var second = _rig.Send(S, "按意见改");
        var pending = await PendingPlan();
        Assert.Contains("- add tests", pending.Plan);
        AnswerPlan(pending, PlanApprovalAction.Reject);
        await E2eRig.Settled(second);

        var results = _rig.Results(S).ToList();
        Assert.True(IsError(results, "d1"));
        Assert.Contains("only modify its active Markdown plan", ToolResult(results, "d1"));
        Assert.Equal("old", File.ReadAllText(_rig.Abs("src.ts")));
        Assert.False(IsError(results, "d3"), ToolResult(results, "d3"));
        Assert.Contains("\"action\":\"reject\"", ToolResult(results, "d4"));
        Assert.Null(_rig.Plans.ActiveFor(S));
        var firstRequestOfSecondRun = _rig.Llm.MainRequests[4];
        Assert.Contains(plan.Path, E2eRig.WireText(firstRequestOfSecondRun)); // 新 Run 的状态块里计划文件路径还在
        Assert.Contains("Write", E2eRig.ToolNames(firstRequestOfSecondRun));
        Assert.DoesNotContain("EnterPlanMode", E2eRig.ToolNames(firstRequestOfSecondRun));
    }

    [Fact]
    public async Task In_ask_mode_the_plan_file_is_trusted_as_read_only_but_any_other_write_still_asks()
    {
        // 03 §3 的 P11:权限层和工具层的围栏是同一个值——只有改的正是围栏文件时才当作只读放行
        _rig.PlanWorkflow = true;
        _rig.Put("src.ts", "old");
        _rig.Llm.Main(
            Up.Call("c1", "EnterPlanMode", "{}"),
            Up.Lazy(() => Up.Call("c2", "Write", WriteArgs(PlanPath, "# Plan"))),
            Up.Call("c3", "Write", WriteArgs(_rig.Abs("src.ts"), "x")),
            Say("done"));

        var run = _rig.Send(S, "做个计划", permission: "ask");
        var asked = await Wait.UntilNotNull(() => _rig.Gate.Snapshot().Pending.OfType<ToolPermission>().FirstOrDefault(), what: "an approval for the non-plan write");
        // 走到第三个调用才问:进入计划、写计划文件都没有弹审批
        Assert.Equal("c3", asked.CallId);
        Assert.Equal("Write", asked.ToolName);
        _rig.Gate.Respond(asked.Id, asked.Version, new ToolPermissionResponse(PermissionChoice.Deny("not now")), "ctrl");
        await E2eRig.Settled(run);

        var results = _rig.Results(S).ToList();
        Assert.False(IsError(results, "c2"), ToolResult(results, "c2"));
        Assert.True(IsError(results, "c3"));
        Assert.Equal("old", File.ReadAllText(_rig.Abs("src.ts")));
        Assert.Equal("# Plan", File.ReadAllText(PlanPath));
    }

    [Fact]
    public async Task Aborting_the_run_while_the_plan_is_under_review_leaves_the_plan_active_and_no_pending_approval()
    {
        _rig.PlanWorkflow = true;
        _rig.Llm.Main(
            Up.Call("c1", "EnterPlanMode", "{}"),
            Up.Lazy(() => Up.Call("c2", "Write", WriteArgs(PlanPath, "# Plan"))),
            Up.Call("c3", "ExitPlanMode", "{}"));
        var run = _rig.Send(S, "做个计划");
        await PendingPlan();

        _rig.Registry.Abort(run.RunId);
        await E2eRig.Settled(run);

        Assert.Equal(RunState.Aborted, run.State);
        Assert.Empty(_rig.Gate.Snapshot().Pending);
        Assert.NotNull(_rig.Plans.ActiveFor(S)); // 没批准也没拒绝:计划还在,下一轮可以接着改
    }

    [Fact]
    public async Task Without_the_plan_workflow_the_plan_tools_are_not_offered_at_all()
    {
        _rig.Llm.Main(Say("hi"));

        await _rig.RunAsync(S, "你好");

        var tools = E2eRig.ToolNames(_rig.Llm.MainRequests[0]);
        Assert.DoesNotContain("EnterPlanMode", tools);
        Assert.DoesNotContain("ExitPlanMode", tools);
        Assert.Contains("Write", tools);
        Assert.Contains("TodoWrite", tools);
    }

    [Fact]
    public async Task An_approved_plan_is_carried_into_the_state_block_of_the_executing_run()
    {
        _rig.Put(".plan/PLX.md", "# Approved\n\n1. Do the thing.");
        _rig.Llm.Main(Say("on it"));
        var run = _rig.Send(S, "执行批准的计划", runId: "exec-1");
        // 批准后客户端带着计划引用发起新 Run:Host 在发起前登记它
        await E2eRig.Settled(run); // 先让上面的 Run 结束,下面是真正带计划引用的那个
        _rig.Plans.ExpectExecution(new RunId("exec-2"), new Ncw.Agent.Extensions.Planning.Plans.PlanFileRef("PLX", ".plan/PLX.md"));
        _rig.Llm.Main(Say("executing"));
        var exec = _rig.Send(S, "执行", runId: "exec-2");
        await E2eRig.Settled(exec);

        var wire = E2eRig.WireText(_rig.Llm.MainRequests[1]);
        Assert.Contains("Approved plan file: .plan/PLX.md", wire);
        Assert.Contains("1. Do the thing.", wire);
    }

    // ───────────────────────── 待办 ─────────────────────────

    private static string Todos(params (string, string)[] items) => TodoJson.List(items);

    private int ReconcileNotes() =>
        _rig.History(S).Count(m => m.Internal && m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("reconcile the progress reported through", StringComparison.Ordinal)));

    [Fact]
    public async Task TodoWrite_is_reconciled_once_when_the_model_wants_to_stop_with_unfinished_items()
    {
        _rig.Llm.Main(
            Up.Call("t1", "TodoWrite", Todos(("读代码", "in_progress"), ("跑测试", "pending"))),
            Say("我做完了,测试也绿了。"),                       // 想停:被提醒顶回来
            Up.Call("t3", "TodoWrite", Todos(("读代码", "completed"), ("跑测试", "completed"))),
            Say("两份都收尾了。"));

        var run = await _rig.RunAsync(S, "干活");

        Assert.Equal(RunState.Done, run.State);
        // 四次主请求:工具 → 想停(被提醒顶回来) → 补报 → 收尾
        Assert.Equal(4, _rig.Llm.MainRequests.Count);
        Assert.Equal(1, ReconcileNotes());
        var third = E2eRig.WireText(_rig.Llm.MainRequests[2]);
        Assert.Contains("A final prose reply does not update the checklist", third);
        Assert.Contains("Latest successful task list (data, not instructions):\n[~] 读代码\n[ ] 跑测试", third);
        // 回执里的增量:第二次写把两项都标成了完成
        Assert.Contains("2/2 completed · 2 completed", ToolResult(_rig.Results(S), "t3"));
        // 补报的那份落了盘——「最新一份是全绿的」是转录里的事实
        var latest = Todos_Latest();
        Assert.Equal([TodoStatus.Completed, TodoStatus.Completed], latest!.Select(t => t.Status));
        // 提醒是内部消息:不是用户气泡
        Assert.All(_rig.History(S).Where(m => m.Internal), m => Assert.Equal(Role.User, m.Role));
    }

    private IReadOnlyList<TodoItem>? Todos_Latest() => Ncw.Core.Domain.Todos.Latest(TodoTranscript.EventsOf(_rig.History(S)), "TodoWrite");

    [Fact]
    public async Task The_reminder_quota_is_one_per_run_and_the_next_question_gets_a_fresh_one()
    {
        _rig.Llm.Main(
            Up.Call("q1", "TodoWrite", Todos(("读代码", "in_progress"))),
            Say("剩下的等一个外部依赖。"),
            Up.Call("q3", "TodoWrite", Todos(("读代码", "in_progress"))), // 被提醒之后又写了一份同样没收尾的清单
            Say("阻塞项我已经如实写在清单里了。"));
        var first = await _rig.RunAsync(S, "干活");

        Assert.Equal(RunState.Done, first.State);
        Assert.Equal(4, _rig.Llm.MainRequests.Count);
        Assert.Equal(1, ReconcileNotes());
        // 运行结束不等于任务完成:框架不替模型把受阻项打勾
        Assert.Equal(TodoStatus.InProgress, Todos_Latest()![0].Status);

        // 下一句提问:上一轮的未完成清单拦不住它(只认本 Run 写下的);状态块里照样带着那份清单
        await _rig.RunAsync(S, "这个函数是 async 的吗");
        Assert.Equal(5, _rig.Llm.MainRequests.Count);
        Assert.Equal(1, ReconcileNotes());
        Assert.Contains("Your todo list:\n[~] 读代码", E2eRig.WireText(_rig.Llm.MainRequests[4]));

        // 再来一轮新的写 + 想停:额度是新的
        _rig.Llm.Main(Up.Call("q5", "TodoWrite", Todos(("新的活", "pending"))), Say("这一轮也想直接停下。"), Say("好。"));
        await _rig.RunAsync(S, "再干一件");
        Assert.Equal(2, ReconcileNotes());
    }

    [Fact]
    public async Task A_list_the_tool_rejected_does_not_trigger_a_reminder()
    {
        // 两个 in_progress:被工具拒绝,但原样躺在转录里(循环先提交、后校验)
        _rig.Llm.Main(
            Up.Call("r1", "TodoWrite", Todos(("A", "in_progress"), ("B", "in_progress"))),
            Say("这一轮我没写清单。"));

        await _rig.RunAsync(S, "干活");

        Assert.True(IsError(_rig.Results(S), "r1"));
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());
        Assert.Null(Todos_Latest());
    }

    [Fact]
    public async Task A_run_that_wrote_a_fully_completed_list_is_not_reminded()
    {
        _rig.Llm.Main(Up.Call("c1", "TodoWrite", Todos(("读代码", "completed"))), Say("都做完了。"));

        await _rig.RunAsync(S, "干活");

        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());
    }

    [Fact]
    public async Task A_provider_error_ends_the_run_without_an_extra_reminder_request()
    {
        _rig.Llm.Main(Up.Call("e1", "TodoWrite", Todos(("干活", "in_progress"))), Up.Fail(AgentErrorCode.Provider));

        var run = await _rig.RunAsync(S, "干活");

        Assert.Equal(RunState.Error, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());
        Assert.Equal(TodoStatus.InProgress, Todos_Latest()![0].Status);
    }

    [Fact]
    public async Task A_user_abort_ends_the_run_with_no_reminder_and_no_further_request()
    {
        _rig.Llm.Main(Up.Call("a1", "TodoWrite", Todos(("读代码", "in_progress"))), Up.Hang());
        var run = _rig.Send(S, "干活");
        await Wait.UntilAsync(() => _rig.Llm.MainRequests.Count == 2, what: "the second turn");

        _rig.Registry.Abort(run.RunId);
        await E2eRig.Settled(run);

        Assert.Equal(RunState.Aborted, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());
    }

    [Fact]
    public async Task While_a_plan_is_active_the_checklist_is_not_nagged()
    {
        _rig.PlanWorkflow = true;
        _rig.Llm.Main(
            Up.Call("p1", "EnterPlanMode", "{}"),
            Up.Call("p2", "TodoWrite", Todos(("调研", "in_progress"))),
            Say("计划写到一半,先停一下。"));

        await _rig.RunAsync(S, "做个计划");

        Assert.Equal(3, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());
    }

    // ───────────────────────── 目标 ─────────────────────────

    private async Task SendHello()
    {
        _rig.Llm.Main(Say("你好"));
        await _rig.RunAsync(S, "你好"); // 先有一个用户 Run:内部输入起新 Run 时沿用它的发送档位
    }

    private async Task WaitGoalDone()
    {
        await Wait.UntilAsync(() => _rig.Goals.Store.Get(S) is null, what: "the goal to finish");
        await _rig.IdleAsync(S);
    }

    private List<GoalStatusPart> Markers() => [.. _rig.History(S).SelectMany(m => m.Parts).OfType<GoalStatusPart>()];

    [Fact]
    public async Task A_goal_keeps_the_agent_working_until_the_evaluator_confirms_it()
    {
        await SendHello();
        _rig.Llm.Main(Say("第 1 轮的主模型回复"), Say("第 2 轮的主模型回复"));
        _rig.Llm.Judge(Up.Verdict(false, "转录里还没看到测试输出"), Up.Verdict(true, "测试已经全绿了"));

        var result = Assert.IsType<GoalSetResult.Ok>(_rig.Goals.SetByUser(S, Goal));
        await WaitGoalDone();

        // 设目标这一刻盖了「已设定」;kickoff 走内部输入起了一个 Run,主模型被叫了两次:第二次是判定器判 not_met 之后真的回来的那一轮
        Assert.Equal(InternalDelivery.Launched, result.Delivery);
        Assert.Equal(1 + 2, _rig.Llm.MainRequests.Count);
        Assert.Equal(2, _rig.Llm.JudgeRequests.Count);
        var markers = Markers();
        Assert.Equal(
            [(true, false, 0), (false, false, 1), (false, true, 2)],
            markers.Select(m => (m.Set == true, m.Met, m.Iterations ?? 0)));
        Assert.Equal("转录里还没看到测试输出", markers[1].Reason);
        Assert.Equal("user", markers[0].Origin);
        // 「真的被叫回来」的硬证据:第二轮上行的消息里带着那条续跑注入
        var second = E2eRig.WireText(_rig.Llm.MainRequests[2]);
        Assert.Contains(GoalPrompts.StopHookFeedbackPrefix, second);
        Assert.Contains("转录里还没看到测试输出", second);
        // 第一轮上行的是 kickoff
        Assert.Contains(GoalPrompts.KickoffMessage(Goal), E2eRig.WireText(_rig.Llm.MainRequests[1]));
        // 达成即自动清掉,而且重载恢复不会把它捞回来
        Assert.Null(_rig.Goals.Get(S));
        Assert.Null(GoalRestore.RestorableCondition(_rig.History(S)));
        Assert.Equal(0, _rig.Goals.TimerCount);
        // 标记只属于界面那一轨:它们挂在已有的助手消息上,不新增消息(用户「你好」+ 助手 + kickoff + 助手 + 反馈 + 助手)
        Assert.Equal(6, _rig.History(S).Count);
        Assert.All(_rig.History(S).Where(m => m.Role == Role.Assistant), m => Assert.Contains(m.Parts, p => p is not GoalStatusPart));
    }

    [Fact]
    public async Task The_idle_streak_brake_stops_a_goal_that_keeps_stalling_but_keeps_the_goal()
    {
        await SendHello();
        // 主模型一直只说话不调用工具,判定器一直说「未达成」(两条队列耗尽后的缺省就是这样)
        _rig.Goals.Activate(S, Goal, GoalOrigin.User); // 直接设,不经内部输入:下面自己发一条用户消息起 Run
        var run = await _rig.RunAsync(S, "接着干");

        Assert.Equal(RunState.Done, run.State);
        // 连续 8 个回合没有调用工具 → 第 9 次回合末强制结束
        Assert.Equal(1 + GoalConditions.IdleStreakCap + 1, _rig.Llm.MainRequests.Count);
        Assert.Equal(GoalConditions.IdleStreakCap + 1, _rig.Llm.JudgeRequests.Count);
        var warning = Assert.Single(_rig.Events(run).OfType<NotificationEvent>());
        Assert.Equal("goal.warn.idleStreak", warning.Key);
        // 目标留着:停下来的是「反复空转」,不是「跑了很久」
        var goal = _rig.Goals.Store.Get(S)!;
        Assert.Equal(Goal, goal.Condition);
        Assert.Equal(GoalConditions.IdleStreakCap + 1, goal.Iterations);
        // 不会自己再跑:等一会儿也没有新请求
        await Task.Delay(100);
        Assert.Equal(1 + GoalConditions.IdleStreakCap + 1, _rig.Llm.MainRequests.Count);
    }

    [Fact]
    public async Task A_tool_call_resets_the_stall_count_so_a_goal_that_is_making_progress_is_never_braked()
    {
        await SendHello();
        _rig.Put("a.txt", "x");
        var main = new List<Script>();
        for (var i = 0; i < 12; i++)
        {
            // 每两轮调一次工具:空转计数一直被清零
            main.Add(Up.Call($"r{i}", "Read", J.Obj(("file_path", _rig.Abs("a.txt")))));
            main.Add(Say($"第 {i} 轮"));
        }

        _rig.Llm.Main([.. main]);
        // 判定器 11 次说「未达成」、第 12 次说「达成」:一共 12 次回合末,比刹车的 9 次多
        _rig.Llm.Judge([.. Enumerable.Repeat(Up.Verdict(false, "还红着"), 11), Up.Verdict(true, "好了")]);
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        var run = await _rig.RunAsync(S, "接着干");

        Assert.Equal(RunState.Done, run.State);
        Assert.Empty(_rig.Events(run).OfType<NotificationEvent>());
        Assert.Equal(12, _rig.Llm.JudgeRequests.Count); // 超过 8 轮判定也没被刹住
        Assert.Null(_rig.Goals.Store.Get(S));
    }

    [Fact]
    public async Task A_user_abort_pauses_the_goal_without_clearing_it_and_the_next_turn_picks_it_up()
    {
        await SendHello();
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        var before = _rig.Goals.Store.Get(S)!.Id;
        _rig.Llm.Main(Up.Hang());
        var run = _rig.Send(S, "接着干");
        await Wait.UntilAsync(() => _rig.Llm.MainRequests.Count == 2, what: "the hanging turn");

        _rig.Registry.Abort(run.RunId);
        await E2eRig.Settled(run);

        Assert.Equal(RunState.Aborted, run.State);
        Assert.Empty(_rig.Llm.JudgeRequests); // 中止不判定
        var paused = _rig.Goals.Store.Get(S)!;
        Assert.Equal(Goal, paused.Condition);
        Assert.NotEqual(before, paused.Id); // 轮换身份:还在路上的判定 / 检查作废
        Assert.Equal(0, _rig.Goals.TimerCount);

        _rig.Llm.Main(Say("我接着做了"));
        _rig.Llm.Judge(Up.Verdict(true, "已经达成"));
        await _rig.RunAsync(S, "继续");

        Assert.Null(_rig.Goals.Store.Get(S));
        Assert.True(Markers().Last().Met);
    }

    [Fact]
    public async Task A_goal_the_user_cleared_before_the_next_run_is_not_evaluated()
    {
        await SendHello();
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        _rig.Goals.Clear(S);

        await _rig.RunAsync(S, "继续");

        Assert.Empty(_rig.Llm.JudgeRequests);
        Assert.True(Markers().Last().Cleared);
        Assert.Null(GoalRestore.RestorableCondition(_rig.History(S)));
    }

    [Fact]
    public async Task The_model_can_propose_a_goal_set_directly_and_the_kickoff_reaches_the_running_run()
    {
        _rig.Llm.Main(
            Up.Call("g1", "ProposeGoal", new System.Text.Json.Nodes.JsonObject { ["condition"] = Goal, ["ask_user"] = false }.ToJsonString()),
            Say("条件已经立好了,继续干活。"));
        _rig.Llm.Judge(Up.Verdict(true, "工具已经执行过了"));

        var run = await _rig.RunAsync(S, "帮我把测试修绿");

        Assert.Equal(RunState.Done, run.State);
        // 「下发了」「被执行了」:工具回的是「直接设立」那一支;只读工具在 ask 以外的档位不弹审批
        Assert.Contains("\"status\":\"set\"", ToolResult(_rig.Results(S), "g1"));
        Assert.Empty(_rig.Gate.Snapshot().Pending);
        // kickoff 走的是内部通道:不以用户的名义出现在对话里,但进了模型的下一轮
        var kickoff = _rig.History(S).Single(m => m.Role == Role.User && m.Internal && m.Parts.OfType<TextPart>().Any(p => p.Text.StartsWith("A session-scoped Stop hook", StringComparison.Ordinal)));
        Assert.Contains(GoalPrompts.KickoffMessage(Goal), ((TextPart)kickoff.Parts[0]).Text);
        Assert.Contains(GoalPrompts.KickoffMessage(Goal), E2eRig.WireText(_rig.Llm.MainRequests[1]));
        // 直接设立的来源看得见,随后判成 met
        var markers = Markers();
        Assert.Equal("proposal_direct", markers[0].Origin);
        Assert.True(markers[0].Set);
        Assert.True(markers[^1].Met);
        Assert.Single(_rig.Llm.JudgeRequests);
        Assert.Null(_rig.Goals.Store.Get(S));
    }

    [Fact]
    public async Task A_proposal_that_needs_approval_does_not_hold_the_run_and_is_approved_after_it_ends()
    {
        _rig.Policy = GoalProposalPolicy.AlwaysAsk;
        _rig.Llm.Main(
            Up.Call("g1", "ProposeGoal", new System.Text.Json.Nodes.JsonObject { ["condition"] = Goal }.ToJsonString()),
            Say("先继续干别的。"));

        var run = await _rig.RunAsync(S, "帮我把测试修绿"); // 没有人回答,Run 照样跑完

        Assert.Equal(RunState.Done, run.State);
        Assert.Contains("\"status\":\"pending\"", ToolResult(_rig.Results(S), "g1"));
        var pending = Assert.Single(_rig.Gate.Snapshot().Pending.OfType<GoalProposal>());
        Assert.Null(_rig.Goals.Store.Get(S));

        _rig.Llm.Main(Say("kickoff 收到"));
        _rig.Llm.Judge(Up.Verdict(true, "好了"));
        _rig.Gate.Respond(pending.Id, pending.Version, new GoalProposalResponse(true), "ctrl");
        // 批准之后「设立目标 + 投递 kickoff」在提案那个等待的续体里异步发生:Respond 返回时目标可能还没设上,这时「目标为空」「会话空闲」都成立,
        // 直接 WaitGoalDone 会在目标开始之前就返回(Linux CI 满载时出现过)。所以先等到「达成」那一条落盘。
        await Wait.UntilAsync(() => Markers() is { Count: > 0 } markers && markers[^1].Met, what: "the approved goal to be met");
        await WaitGoalDone();

        Assert.Equal("proposal_approved", Markers()[0].Origin);
        Assert.True(Markers()[^1].Met);
    }

    [Fact]
    public async Task While_the_plan_workflow_is_on_the_goal_proposal_tool_is_not_offered()
    {
        _rig.PlanWorkflow = true;
        _rig.Llm.Main(Say("先问几个问题"));

        await _rig.RunAsync(S, "做个计划");

        Assert.DoesNotContain("ProposeGoal", E2eRig.ToolNames(_rig.Llm.MainRequests[0]));
        _rig.PlanWorkflow = false;
        _rig.Llm.Main(Say("好"));
        await _rig.RunAsync(S, "普通任务");
        Assert.Contains("ProposeGoal", E2eRig.ToolNames(_rig.Llm.MainRequests[1]));
    }

    [Fact]
    public async Task While_a_goal_is_active_the_checklist_reminder_yields_to_the_evaluator()
    {
        // 目标判成 met 的强制收尾不被覆盖:清单补报一句都不加
        await SendHello();
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        _rig.Llm.Main(Up.Call("t1", "TodoWrite", Todos(("干活", "in_progress"))), Say("目标达成了。"));
        _rig.Llm.Judge(Up.Verdict(true, "已经达成"));

        var run = await _rig.RunAsync(S, "干活");

        Assert.Equal(RunState.Done, run.State);
        Assert.Single(_rig.Llm.JudgeRequests);
        Assert.Equal(3, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());
        Assert.Null(_rig.Goals.Store.Get(S));
    }

    [Fact]
    public async Task The_judge_model_is_frozen_with_its_provider_at_run_start_a_mid_run_settings_change_applies_to_the_next_run()
    {
        await SendHello();
        _rig.EvaluatorSetting = new ModelRef("p1", "judge-a");
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        // 在第一条主模型的流里改设置:冻结发生在 before_run,所以这一改只该影响下一个 Run
        _rig.Llm.Main(Up.Before(() => _rig.EvaluatorSetting = new ModelRef("p1", "judge-b"), Say("第一轮")), Say("第二轮"));
        _rig.Llm.Judge(Up.Verdict(false, "还没"), Up.Verdict(true, "好了"));

        await _rig.RunAsync(S, "接着干");
        // 同一个 Run 的两次判定都用冻结的那一对,哪怕设置在第一轮里已经改了
        Assert.Equal([new ModelRef("p1", "judge-a"), new ModelRef("p1", "judge-a")], _rig.Llm.JudgeModels);

        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        _rig.Llm.Main(Say("第三轮"));
        _rig.Llm.Judge(Up.Verdict(true, "好了"));
        await _rig.RunAsync(S, "再来");

        Assert.Equal(new ModelRef("p1", "judge-b"), _rig.Llm.JudgeModels[^1]);
        Assert.Equal(3, _rig.Llm.JudgeModels.Count);
    }

    [Fact]
    public async Task A_goal_cleared_while_the_judge_is_still_answering_gets_no_met_stamp_and_the_clear_marker_survives()
    {
        await SendHello();
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        _rig.Llm.Main(Say("先按目标做事。"));
        // 判定还没回话,用户先按了「清除」:这正是那条裁决最可能作废的一刻
        _rig.Llm.Judge(Up.Before(() => _rig.Goals.Clear(S), Up.Verdict(true, "其实已经好了")));

        var run = await _rig.RunAsync(S, "接着干");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        var markers = Markers();
        Assert.DoesNotContain(markers, m => m.Met);
        Assert.DoesNotContain(markers, m => m.Failed == true);
        Assert.Contains(markers, m => m.Cleared == true);
        // 清除标记活过收尾:恢复读最后一条标记,目标下次打开不会自己回来
        Assert.Null(GoalRestore.RestorableCondition(_rig.History(S)));
        Assert.Null(_rig.Goals.Get(S));
    }

    [Fact]
    public async Task A_background_subagent_in_the_session_defers_the_evaluation_the_judge_is_never_called_and_the_goal_stays()
    {
        await SendHello();
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        var child = _rig.StartBackgroundChild(S);
        _rig.Llm.Main(Say("先看一眼现状。"));

        var run = await _rig.RunAsync(S, "接着干");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        // 判定器一条请求都没发:后台还在跑,这一轮读到的转录本来就是半截的
        Assert.Empty(_rig.Llm.JudgeRequests);
        var goal = _rig.Goals.Store.Get(S)!;
        Assert.Equal(0, goal.Iterations);
        Assert.NotNull(goal.DeferredSince);
        Assert.Equal(1, _rig.Goals.TimerCount); // 空闲唤醒的定时器挂着
        Assert.Equal(Goal, _rig.Goals.Get(S)!.Condition);

        _rig.Registry.Abort(child.RunId);
        await E2eRig.Settled(child);
    }

    [Fact]
    public async Task With_no_goal_but_a_background_subagent_running_the_half_finished_checklist_is_not_nagged()
    {
        var child = _rig.StartBackgroundChild(S);
        _rig.Llm.Main(Up.Call("bg1", "TodoWrite", Todos(("读代码", "in_progress"))), Say("先把结果交出去。"));

        var run = await _rig.RunAsync(S, "干活");

        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(2, _rig.Llm.MainRequests.Count);
        Assert.Equal(0, ReconcileNotes());

        _rig.Registry.Abort(child.RunId);
        await E2eRig.Settled(child);
    }

    [Fact]
    public async Task A_goal_that_survives_a_restart_comes_back_from_the_transcript_without_running_anything()
    {
        await SendHello();
        _rig.Goals.Activate(S, Goal, GoalOrigin.User);
        _rig.Goals.EndSession(S); // 退出:不是用户清除,持久的活跃标记留给下次启动

        // 「重启」:一个全新的目标服务(内存状态没了),同一个库
        using var fresh = new GoalService(new GoalOptions { Llm = _rig.Llm, Transcript = new SessionGoalTranscript(_rig.Repo) });
        var restored = fresh.Restore(S);

        Assert.Equal(Goal, restored!.Condition);
        Assert.Equal(GoalOrigin.Restored, restored.Origin);
        Assert.Equal(0, restored.Iterations);
        Assert.Single(_rig.Llm.MainRequests); // 恢复不重开一轮 Run
    }
}
