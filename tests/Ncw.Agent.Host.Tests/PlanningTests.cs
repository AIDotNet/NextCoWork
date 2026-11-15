using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 计划模式 / 待办 / 目标(协议接线,第二批):真实 Agent + <see cref="DeviceClient"/> + 按会话分派脚本的假上游(含目标判定器)+ 真实工具 / 权限 / 交互闸门 / SQLite。
/// 计划:写入被围栏 → 批准 → <c>runs.executePlan</c> 之后可写且模型看得见计划;待办:TodoWrite 落进会话;目标:<c>goal.set</c> 之后续跑直到判定器确认、空转刹车、
/// <c>goal</c> 主题(全量、版本号单调、两个控制端一致)。
/// </summary>
public sealed class PlanningTests : IAsyncLifetime
{
    private readonly ScriptedLlm _llm = new();
    private readonly ConversationRig _rig;

    public PlanningTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Models = new FakeModels(_llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
        });
    }

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<(DeviceClient Client, ProjectDto Project, SessionDto Session)> SetupAsync(string clientId = "ctrl-a", string? mode = null)
    {
        var client = await _rig.ConnectAsync(clientId);
        var project = await _rig.AddProjectAsync(client);
        var session = await client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "sonnet", Mode: mode));
        return (client, project, session);
    }

    private static RunsStartParams StartParams(string sessionId, string text, string runId, string mode = "", string permission = "auto") =>
        new(runId, sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto("sonnet", Mode: mode, PermissionMode: permission));

    private static Task<RunsStartResult> StartAsync(DeviceClient client, string sessionId, string text, string runId, string mode = "", string permission = "auto") =>
        client.CallAsync(ConversationMethods.RunsStart, StartParams(sessionId, text, runId, mode, permission), idem: $"start:{runId}");

    private static Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 30_000) => Watch<Unit>.UntilAsync(condition, what, timeoutMs);

    private static string Write(string path, string content) => ConversationRig.WriteJson(path, content);

    // ───────────────────────── 计划模式 ─────────────────────────

    [Fact]
    public async Task Plan_mode_fences_writes_then_the_approved_plan_runs_in_code_mode_where_writes_work_and_the_model_sees_the_plan()
    {
        var (client, project, session) = await SetupAsync(mode: "plan");
        var target = Path.Combine(project.Path, "src.txt");
        var interactions = await Watch<InteractionsDto>.OpenAsync(client, ConversationMethods.InteractionsTopic);
        _llm.On(session.Id,
            S.Call("c1", "EnterPlanMode", "{}"),
            S.Call("c2", "Write", Write(target, "no")), // 围栏之外:被拦
            S.Lazy(req => S.Call("c3", "Write", Write(Path.Combine(project.Path, PlanPathOf(req)), "# Plan\n\n1. Do it."))),
            S.Call("c4", "ExitPlanMode", "{}"));

        await StartAsync(client, session.Id, "make a plan", "p1", mode: "plan", permission: "full"); // full:不弹审批,留下围栏本身(它在工具层,和权限档位无关)
        var watch = await RunWatch.OpenAsync(client, "p1");
        await WaitForAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.OfType<PlanApprovalInteractionDto>().Any()), "the plan approval");
        var approval = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending.OfType<PlanApprovalInteractionDto>().Single();
        Assert.Equal("# Plan\n\n1. Do it.", approval.Plan);
        Assert.Equal($".plan/{approval.PlanId}.md", approval.Path);

        // 批准:交互先到先得;批准即终点——Run 在 ExitPlanMode 之后没有再请求一次模型
        await client.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(approval.Id, approval.Version, new PlanApprovalResponseDto("approveCurrent")), idem: "approve-1");
        await watch.SettledAsync();
        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Equal(4, _llm.RequestsOf(session.Id).Count);

        // 进入计划模式之后写入被围栏拦住,文件一个字节没动;计划文件本身写成了
        var tools = watch.Of<ToolEndEventDto>().ToDictionary(t => t.CallId);
        Assert.False(tools["c1"].IsError);
        Assert.True(tools["c2"].IsError);
        Assert.Contains("only modify its active Markdown plan", tools["c2"].Content);
        Assert.False(File.Exists(target));
        Assert.False(tools["c3"].IsError, tools["c3"].Content);
        Assert.Equal("# Plan\n\n1. Do it.", File.ReadAllText(Path.Combine(project.Path, approval.Path)));
        // 工具表每回合现算:计划模式下一开始只有 EnterPlanMode,没有 Write / Task;进入之后 Write / ExitPlanMode 回来
        var first = _llm.RequestsOf(session.Id)[0].Tools.Select(t => t.ExternalName).ToList();
        Assert.Contains("EnterPlanMode", first);
        Assert.DoesNotContain("Write", first);
        Assert.DoesNotContain("Task", first);
        var second = _llm.RequestsOf(session.Id)[1].Tools.Select(t => t.ExternalName).ToList();
        Assert.Contains("Write", second);
        Assert.Contains("ExitPlanMode", second);
        Assert.DoesNotContain("EnterPlanMode", second);

        // 批准之后:以 code 模式发起执行 Run,带着计划引用——模型在状态块里看见计划全文,写入不再被围栏拦
        _llm.On(session.Id, S.Call("d1", "Write", Write(target, "implemented")), S.Say("done implementing"));
        var execute = await client.CallAsync(
            OrchestrationMethods.RunsExecutePlan,
            new RunsExecutePlanParams(StartParams(session.Id, "implement the plan", "p2", mode: "code", permission: "full"), new PlanFileDto(approval.PlanId, approval.Path)),
            idem: "exec-1");
        Assert.Equal(StartOutcomes.Started, execute.Outcome);
        await (await RunWatch.OpenAsync(client, "p2")).SettledAsync();

        Assert.Equal("implemented", File.ReadAllText(target));
        var executing = _llm.RequestsOf(session.Id)[4];
        Assert.Contains("Approved plan file: .plan/", S.WireText(executing));
        Assert.Contains("1. Do it.", S.WireText(executing));
        var executingTools = executing.Tools.Select(t => t.ExternalName).ToList();
        Assert.Contains("Write", executingTools);
        Assert.DoesNotContain("EnterPlanMode", executingTools);
        Assert.DoesNotContain("ExitPlanMode", executingTools);
    }

    [Fact]
    public async Task Executing_a_plan_needs_an_idle_session_and_a_plan_that_cannot_be_loaded_is_reported_to_the_model_instead_of_being_guessed()
    {
        var (client, _, session) = await SetupAsync();
        _llm.On(session.Id, S.Hang());

        await StartAsync(client, session.Id, "busy", "p1");
        await WaitForAsync(() => _llm.RequestsOf(session.Id).Count == 1, "the first run to be talking to the model");
        // 会话忙:busy(可重试),不进队列——计划只属于这个 RunId
        var busy = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(
            OrchestrationMethods.RunsExecutePlan, new RunsExecutePlanParams(StartParams(session.Id, "go", "p2"), new PlanFileDto("someid", ".plan/someid.md")), idem: "exec-busy"));
        Assert.Equal((ErrorCodes.Busy, true), (busy.Code, busy.Retryable));
        Assert.Empty((await client.CallAsync(ConversationMethods.QueueList, new SessionRefParams(session.Id))).Items);
        await client.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams("p1"), idem: "abort-1");
        await (await RunWatch.OpenAsync(client, "p1")).SettledAsync();

        // 参数不合法:invalid_request;文件不存在:Run 照常起来,但状态块里写明「载入失败,别猜」
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(
            OrchestrationMethods.RunsExecutePlan, new RunsExecutePlanParams(StartParams(session.Id, "go", "p3"), new PlanFileDto("", "")), idem: "exec-bad"))).Code);
        _llm.On(session.Id, S.Say("I will tell the user"));
        await client.CallAsync(
            OrchestrationMethods.RunsExecutePlan, new RunsExecutePlanParams(StartParams(session.Id, "go", "p4"), new PlanFileDto("gone", ".plan/gone.md")), idem: "exec-gone");
        await (await RunWatch.OpenAsync(client, "p4")).SettledAsync();
        var wire = S.WireText(_llm.RequestsOf(session.Id)[1]);
        Assert.Contains("could not be loaded", wire);
        Assert.Contains("do not guess", wire);
    }

    private static string PlanPathOf(LlmRequest request)
    {
        var content = request.Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(r => r.Output.Content).First(c => c.Contains("plan_file_entered"));
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("path").GetString()!;
    }

    // ───────────────────────── 待办 ─────────────────────────

    [Fact]
    public async Task TodoWrite_lands_in_the_session_and_a_half_finished_list_gets_exactly_one_reminder_before_the_run_may_stop()
    {
        var (client, _, session) = await SetupAsync();
        const string Unfinished = """{"todos":[{"content":"read the code","status":"completed","activeForm":"Reading the code"},{"content":"fix the bug","status":"in_progress","activeForm":"Fixing the bug"}]}""";
        const string Finished = """{"todos":[{"content":"read the code","status":"completed","activeForm":"Reading the code"},{"content":"fix the bug","status":"completed","activeForm":"Fixing the bug"}]}""";
        _llm.On(session.Id,
            S.Call("t1", "TodoWrite", Unfinished),
            S.Say("I think I am done"),             // 想收尾,清单还有未完成项 → 补报一次
            S.Call("t2", "TodoWrite", Finished),
            S.Say("now really done"));
        var topic = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(session.Id));

        await StartAsync(client, session.Id, "fix it", "p1");
        await (await RunWatch.OpenAsync(client, "p1")).SettledAsync();

        var requests = _llm.RequestsOf(session.Id);
        Assert.Equal(4, requests.Count);
        Assert.Contains("fix the bug", S.WireText(requests[2])); // 补报:提醒里带着未完成项
        var committed = topic.Decoded<MessageDto>(SessionEvents.MessageCommitted);
        Assert.Contains(committed, m => m.PartsJson.Contains("\"name\":\"TodoWrite\"") && m.PartsJson.Contains("in_progress"));
        Assert.Contains(committed, m => m.PartsJson.Contains("\"callId\":\"t1\"") && m.PartsJson.Contains("tool_result"));
        Assert.Contains("completed", S.WireText(requests[3]));
    }

    // ───────────────────────── 目标 ─────────────────────────

    private static SendOptionsDto Options => new("sonnet", PermissionMode: "auto");

    private static IEnumerable<GoalChangeDto> Changes(Watch<GoalsTopicState> topic) => topic.Decoded<GoalChangeDto>(GoalEvents.Changed);

    [Fact]
    public async Task Goal_set_keeps_the_agent_working_until_the_evaluator_confirms_and_the_goal_topic_carries_every_step_with_a_monotonic_version()
    {
        var (a, _, session) = await SetupAsync("ctrl-a");
        var b = await _rig.ConnectAsync("ctrl-b");
        var topicA = await Watch<GoalsTopicState>.OpenAsync(a, OrchestrationMethods.GoalTopic);
        var topicB = await Watch<GoalsTopicState>.OpenAsync(b, OrchestrationMethods.GoalTopic);
        await topicA.WaitAsync(() => topicA.HasSnapshot && topicB.HasSnapshot, "goal snapshots");
        Assert.Empty(topicA.Snapshot.Goals);
        _llm.On(session.Id, S.Say("turn one"), S.Say("turn two"));
        _llm.Judge(S.Verdict(false, "tests are still red"), S.Verdict(true, "tests are green"));

        var result = await a.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "  make the tests green ​", Options), idem: "goal-1");

        Assert.Equal(GoalOutcomes.Set, result.Outcome);
        Assert.Equal("launched", result.Delivery); // 会话空闲:kickoff 起了一个内部 Run
        Assert.Equal(("make the tests green", "user", 0), (result.Goal!.Condition, result.Goal.Origin, result.Goal.Iterations));
        await WaitForAsync(() => Changes(topicA).Any(c => c.Goal is null && c.Cleared == "met"), "the goal to be met");

        // 主模型被叫了两次(第二次是判定器说「未达成」之后真的回来的那一轮,带着续跑注入),判定器两次
        Assert.Equal(2, _llm.RequestsOf(session.Id).Count);
        Assert.Equal(2, _llm.JudgeRequests.Count);
        Assert.Contains("tests are still red", S.WireText(_llm.RequestsOf(session.Id)[1]));
        Assert.Contains("make the tests green", S.WireText(_llm.RequestsOf(session.Id)[0]));
        // 带外标记(goal_status)挂在助手消息上,是在「达成」之后随回合末 / Run 结束的冲刷才推出来的——先等它到,再取快照;
        // 以前这里取快照时它还可能没到,B 的变更数随后比这份快照多一条,下面「数量相等」的等待就永远等不到(30 s 超时,稳定出现在整程序集并发时)。
        await WaitForAsync(() => Changes(topicA).Any(c => c.Message is not null && c.Message.PartsJson.Contains("goal_status")), "the out-of-band goal marker");
        // 主题:设定 → 未达成(轮数 1,带理由)→ 达成清掉;版本号严格递增;清除带墓碑(Goal = null)
        var changes = Changes(topicA).ToList();
        var revs = changes.Select(c => c.Rev).ToList();
        Assert.Equal(revs.Order().ToList(), revs);
        Assert.Equal(revs.Distinct().Count(), revs.Count);
        Assert.Contains(changes, c => c.Goal is { Iterations: 1, LastReason: "tests are still red" });
        Assert.Single(changes, c => c.Cleared == "met");
        Assert.All(changes.SkipWhile(c => c.Cleared != "met"), c => Assert.Null(c.Goal)); // 清除之后的变更(带外标记)只带消息,目标仍是空
        // 两个控制端看到的完全一样
        // (A 的快照之后还可能有更晚的变更,所以 B 追上的判据是「至少有 A 看到的这些」,并且这些的顺序与内容逐条相同)
        await WaitForAsync(() => Changes(topicB).Count() >= changes.Count, "controller B to catch up");
        Assert.Equal(changes.Select(c => (c.Rev, c.Goal?.Id, c.Goal?.Iterations, c.Cleared)), Changes(topicB).Take(changes.Count).Select(c => (c.Rev, c.Goal?.Id, c.Goal?.Iterations, c.Cleared)));
        // 带外写的「已设定 / 未达成 / 达成」标记:挂在助手消息上,随变更带给界面(不新增消息)
        Assert.Contains(changes, c => c.Message is not null && c.Message.PartsJson.Contains("goal_status"));
        var state = await a.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id));
        Assert.Null(state.Goal);
        Assert.True(state.Rev >= revs[^1]);
    }

    [Fact]
    public async Task A_goal_that_makes_no_progress_is_braked_after_eight_tool_free_turns_with_a_warning_and_the_goal_stays()
    {
        var (client, _, session) = await SetupAsync();
        var device = await Watch<DeviceTopicState>.OpenAsync(client, DeviceMethods.DeviceTopic);

        // 主模型一直只说话、判定器一直说「未达成」(脚本用完后的缺省就是这样)
        await client.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "an impossible goal", Options), idem: "goal-1");
        await WaitForAsync(() => _llm.RequestsOf(session.Id).Count >= 9, "the ninth turn");
        await WaitForAsync(() => client.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id)).GetAwaiter().GetResult().Goal is { Iterations: 9 }, "nine evaluated rounds");

        // 连续 8 个回合没有调用工具 → 第 9 次回合末强制结束并警告;目标留着
        var runId = device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged).SelectMany(s => s.Runs).First(r => r.SessionId == session.Id).RunId;
        var run = await RunWatch.OpenAsync(client, runId);
        await run.SettledAsync();
        Assert.Equal("goal.warn.idleStreak", Assert.Single(run.Of<NotificationEventDto>()).Key);
        Assert.Equal(9, _llm.RequestsOf(session.Id).Count);
        Assert.Equal(9, _llm.JudgeRequests.Count);
        Assert.Equal("an impossible goal", (await client.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id))).Goal!.Condition);
        await Task.Delay(100);
        Assert.Equal(9, _llm.RequestsOf(session.Id).Count); // 不会自己再跑
    }

    [Fact]
    public async Task Goal_set_validates_and_clear_is_idempotent_and_a_deleted_session_ends_its_goal()
    {
        var (client, _, session) = await SetupAsync();
        var topic = await Watch<GoalsTopicState>.OpenAsync(client, OrchestrationMethods.GoalTopic);

        // 校验失败不是错误,是结果;什么都没改
        Assert.Equal(GoalOutcomes.Empty, (await client.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, " ​​ "), idem: "g-empty")).Outcome);
        var tooLong = await client.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, new string('x', 4001)), idem: "g-long");
        Assert.Equal((GoalOutcomes.TooLong, 4001), (tooLong.Outcome, tooLong.Length));
        Assert.Null((await client.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id))).Goal);
        Assert.Empty(_llm.RequestsOf(session.Id));
        Assert.Empty(Changes(topic));

        // 会话从没发过消息、也没带档位:目标设上了,kickoff 没送出去(failed),不是错误
        var set = await client.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "ship it"), idem: "g-set");
        Assert.Equal((GoalOutcomes.Set, "failed"), (set.Outcome, set.Delivery));
        await WaitForAsync(() => Changes(topic).Any(c => c.Goal?.Condition == "ship it"), "the goal on the topic");

        // 清除:广播带原因;再清一次不报错
        await client.CallAsync(OrchestrationMethods.GoalClear, new SessionRefParams(session.Id), idem: "g-clear-1");
        await WaitForAsync(() => Changes(topic).Any(c => c.Goal is null && c.Cleared == "userClear"), "the clear");
        await client.CallAsync(OrchestrationMethods.GoalClear, new SessionRefParams(session.Id), idem: "g-clear-2");

        // 会话删除:目标跟着结束(不是用户清除)
        await client.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "again"), idem: "g-set-2");
        await WaitForAsync(() => Changes(topic).Any(c => c.Goal?.Condition == "again"), "the second goal");
        await client.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(session.Id), idem: "del-1");
        await WaitForAsync(() => Changes(topic).Any(c => c.Goal is null && c.Cleared == "sessionEnded"), "the goal to end with its session");
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id)))).Code);
    }

    [Fact]
    public async Task The_judge_model_and_proposal_policy_come_from_the_settings_row()
    {
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_rig.DataRoot, AgentDatabase.FileName)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (id, json) VALUES (1, $j) ON CONFLICT (id) DO UPDATE SET json = excluded.json";
            command.Parameters.AddWithValue("$j", """{"goalEvaluatorModel":"sonnet","goalEvaluatorModelProviderId":"p1","modelProposedGoals":"disabled"}""");
            command.ExecuteNonQuery();
        }

        var (client, _, session) = await SetupAsync();
        _llm.On(session.Id, S.Say("working"));
        _llm.Judge(S.Verdict(true, "done"));

        await client.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "do the thing", Options), idem: "g-1");
        await WaitForAsync(() => _llm.JudgeRequests.Count == 1, "the judge to be asked");

        // 判定走设置里配的模型 + 供应商(成对);「模型自提目标」被设置关掉:ProposeGoal 不下发
        Assert.Equal(new ModelRef("p1", "sonnet"), Assert.Single(_llm.JudgeModels));
        Assert.DoesNotContain("ProposeGoal", _llm.RequestsOf(session.Id)[0].Tools.Select(t => t.ExternalName));
    }
}
