using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 子代理(协议接线,第二批):真实 Agent + <see cref="DeviceClient"/> + 按会话分派脚本的假上游 + 真实工具 / 权限 / 交互闸门 / SQLite。
/// 前台 / 后台子代理、报告回到父会话、并发上限排队、父中止级联、<c>runs.background</c> 转后台、子代理请求审批被拒、定义的列表 / 诊断 / 启停、两个控制端一致、断线按游标续传。
/// 控制端的 Store 归约在 <c>Ncw.Integration.Tests</c> / <c>Ncw.Client.Tests</c>。
/// </summary>
public sealed class SubagentTests : IAsyncLifetime
{
    private readonly ScriptedLlm _llm = new();
    private readonly ConversationRig _rig;

    public SubagentTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Models = new TwoModels(_llm),
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

    private static Task<RunsStartResult> StartAsync(DeviceClient client, string sessionId, string text, string runId, string permission = "auto", string thinking = "auto", bool maxContext = false) =>
        client.CallAsync(
            ConversationMethods.RunsStart,
            new RunsStartParams(runId, sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto("sonnet", Thinking: thinking, MaxContext: maxContext, PermissionMode: permission)),
            idem: $"start:{runId}");

    private void SetSettings(string json)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_rig.DataRoot, AgentDatabase.FileName)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (id, json) VALUES (1, $j) ON CONFLICT (id) DO UPDATE SET json = excluded.json";
        command.Parameters.AddWithValue("$j", json);
        command.ExecuteNonQuery();
    }

    private static async Task<SessionPageDto> PageAsync(DeviceClient client, string sessionId) =>
        await client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(sessionId));

    private static Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 30_000) => Watch<Unit>.UntilAsync(condition, what, timeoutMs);

    // ───────────────────────── 前台子代理 ─────────────────────────

    [Fact]
    public async Task A_foreground_subagent_streams_start_and_end_into_the_parent_run_topic_and_its_last_message_is_the_tool_result()
    {
        var (client, project, session) = await SetupAsync();
        var childSession = S.ChildSession(session.Id, "p1");
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs("find the config reader")), S.Say("parent done"))
            .On(childSession, S.Say("Report: config is read in src/config.ts:12."));

        await StartAsync(client, session.Id, "please research", "p1", thinking: "high");
        var watch = await RunWatch.OpenAsync(client, "p1");
        await watch.SettledAsync();

        // 子代理的事件并进父 Run 的主题:start / update / end,同一个 callId 和 childRunId,子会话 id 由设备带过来
        var start = Assert.Single(watch.Of<SubagentStartEventDto>());
        var end = Assert.Single(watch.Of<SubagentEndEventDto>());
        Assert.Equal(("c1", "p1:sub:1", childSession, "general-purpose", false, "sonnet"), (start.CallId, start.ChildRunId, start.ChildSessionId, start.AgentType, start.Background, start.Model));
        Assert.Equal(("c1", "p1:sub:1", "done"), (end.CallId, end.ChildRunId, end.Status));
        Assert.StartsWith("Report: config is read", end.Summary);
        var kinds = watch.Events.Select(e => e.Event).ToList();
        Assert.True(kinds.IndexOf(start) < kinds.IndexOf(end));
        Assert.True(kinds.IndexOf(end) < kinds.IndexOf(Assert.Single(watch.Of<RunEndEventDto>())));
        var seqs = watch.Events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.Order().ToList(), seqs);
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);
        // 工具结果 = 子代理的最后一条消息;每个 tool_start 配一个 tool_end
        Assert.Contains("Report: config is read in src/config.ts:12.", watch.Of<ToolEndEventDto>().Single(t => t.CallId == "c1").Content);
        Assert.Equal(watch.Of<ToolStartEventDto>().Count(), watch.Of<ToolEndEventDto>().Count());

        // 子 Run 继承父 Run 的模型和思考档位(RunLaunch.Options 带着它们)
        var childRequest = Assert.Single(_llm.RequestsOf(childSession));
        Assert.Equal(ThinkingLevel.High, childRequest.Options.Thinking);
        Assert.DoesNotContain("please research", S.WireText(childRequest)); // 子代理看不到父会话

        // 子 Run 自己的主题(点开面板时才订阅):登记在父 Run 名下、深度 +1
        var child = await RunWatch.OpenAsync(client, "p1:sub:1");
        Assert.Equal(("p1", 1, "done"), (child.Snapshot!.ParentRunId, child.Snapshot.Depth, child.Snapshot.State));
        Assert.Equal(["p1:sub:1"], watch.Snapshot!.Children.Concat(["p1:sub:1"]).Distinct());
        Assert.Equal("sonnet", child.Of<RunStartEventDto>().Single().Model);
        Assert.Contains("Report: config is read", child.Of<MessageEndEventDto>().Last().Message.PartsJson);

        // 子会话:parent_session_id 是真实父关系,不进侧边栏;父会话的工具结果带持久回执
        Assert.Equal(session.Id, (await client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(childSession))).Session.ParentSessionId);
        Assert.DoesNotContain((await client.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(project.WorkspaceId))).Sessions, s => s.Id == childSession);
        var receipt = (await PageAsync(client, session.Id)).Messages.Single(m => m.PartsJson.Contains("\"subagent\""));
        Assert.Contains("\"childRunId\":\"p1:sub:1\"", receipt.PartsJson);
        Assert.Contains("\"status\":\"done\"", receipt.PartsJson);
    }

    // ───────────────────────── 后台子代理:报告回到父会话 ─────────────────────────

    [Fact]
    public async Task A_background_subagent_reports_its_full_text_to_the_parent_session_after_the_parent_run_has_ended()
    {
        var (client, _, session) = await SetupAsync();
        var gate = new TaskCompletionSource();
        var childSession = S.ChildSession(session.Id, "p1");
        var report = "FULL REPORT: " + new string('x', 500) + " THE-END";
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs("do it", background: true)), S.Say("started it"), S.Say("thanks for the report"))
            .On(childSession, S.Gated(gate.Task, S.Say(report)));
        var topic = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(session.Id));

        await StartAsync(client, session.Id, "go", "p1");
        var watch = await RunWatch.OpenAsync(client, "p1");
        await watch.SettledAsync();

        // 父 Run 先结束了:后台子代理不随它结束;start 是后台的
        Assert.True(Assert.Single(watch.Of<SubagentStartEventDto>()).Background);
        Assert.Empty(watch.Of<SubagentEndEventDto>());
        Assert.Contains("started in the background", watch.Of<ToolEndEventDto>().Single(t => t.CallId == "c1").Content);

        gate.SetResult();
        await WaitForAsync(() => _llm.RequestsOf(session.Id).Count >= 3, "the report to start a new internal run in the parent session");

        // 全文(不是 240 字的摘要)作为内部消息回到父会话;带汇报行的 subagent 块
        var message = (await PageAsync(client, session.Id)).Messages.Single(m => m.Internal && m.PartsJson.Contains("Background subagent result"));
        Assert.Contains("THE-END", message.PartsJson);
        Assert.Contains("\"type\":\"subagent\"", message.PartsJson);
        Assert.Contains("\"callId\":\"c1\"", message.PartsJson);
        Assert.Contains("THE-END", S.WireText(_llm.RequestsOf(session.Id)[2]));

        // 父会话主题:先 stopped(父 Run 的日志已经收口,subagent_end 进不去,所以单独发一条),汇报进度 injecting → reported
        await topic.WaitAsync(() => topic.Decoded<SubagentReportDto>(SubagentEvents.Report).Any(r => r.Status == "reported"), "report status reported");
        var stopped = Assert.Single(topic.Decoded<SubagentStoppedDto>(SubagentEvents.Stopped));
        Assert.Equal(("c1", "p1:sub:1", childSession, "done", true), (stopped.CallId, stopped.ChildRunId, stopped.ChildSessionId, stopped.Status, stopped.Background));
        var statuses = topic.Decoded<SubagentReportDto>(SubagentEvents.Report).Select(r => r.Status).ToList();
        Assert.True(statuses.IndexOf("injecting") < statuses.IndexOf("reported"));
        // 回执(库里)说它是后台的、已汇报
        await WaitForAsync(() => PageAsync(client, session.Id).GetAwaiter().GetResult().Messages.Any(m => m.PartsJson.Contains("\"reportStatus\":\"reported\"")), "durable receipt reported");
        // 再点「处理」是幂等的:已汇报的不重发
        var again = await client.CallAsync(OrchestrationMethods.SubagentsReport, new SubagentsReportParams(session.Id, "c1"), idem: "report-1");
        Assert.Equal("reported", again.Status);
        Assert.Equal("none", (await client.CallAsync(OrchestrationMethods.SubagentsReport, new SubagentsReportParams(session.Id, "no-such-call"), idem: "report-2")).Status);
    }

    // ───────────────────────── 并发上限 ─────────────────────────

    [Fact]
    public async Task With_a_limit_of_one_the_second_background_dispatch_is_queued_with_a_structured_position_and_starts_only_when_the_first_ends()
    {
        SetSettings("""{"subagent":{"perSessionLimit":1,"globalLimit":1}}""");
        var (client, _, session) = await SetupAsync();
        var gate = new TaskCompletionSource();
        var parentGate = new TaskCompletionSource();
        _llm.On(session.Id,
                S.Calls(("c1", "Task", S.TaskArgs("one", background: true, description: "first")), ("c2", "Task", S.TaskArgs("two", background: true, description: "second"))),
                S.Gated(parentGate.Task, S.Say("both dispatched")))
            .On(S.ChildSession(session.Id, "p1", 1), S.Gated(gate.Task, S.Say("first result")))
            .On(S.ChildSession(session.Id, "p1", 2), S.Gated(gate.Task, S.Say("second result")));

        await StartAsync(client, session.Id, "go", "p1");
        var watch = await RunWatch.OpenAsync(client, "p1");
        await WaitForAsync(() => watch.Of<ToolEndEventDto>().Count() == 2, "both Task calls to return");

        // 排队的那一条:tool_progress 带结构化 Details(排位),话术说「排队」而不是「已启动」;这时只有一个子代理开始了
        var queued = Assert.Single(watch.Of<ToolProgressEventDto>(), p => p.DetailsJson?.Contains("queued") == true);
        Assert.Equal("""{"kind":"queued","ahead":0}""", queued.DetailsJson);
        Assert.Single(watch.Of<SubagentStartEventDto>());
        Assert.Contains("queued behind the running ones", watch.Of<ToolEndEventDto>().Single(t => t.CallId == queued.CallId).Content);
        var device = await Watch<DeviceTopicState>.OpenAsync(client, DeviceMethods.DeviceTopic);
        await device.WaitAsync(() => device.HasSnapshot, "device snapshot");
        Assert.Single(device.Snapshot.Subagents);

        gate.SetResult();
        await WaitForAsync(() => watch.Of<SubagentEndEventDto>().Count() == 2, "both subagents to finish");

        // 一个空位只给一个等待者:第一个子 Run 结束之后第二个才开始(名额在子 Run 的 run_end 就腾出来,所以不是比 subagent_end 的先后:那条要等驱动收尾),
        // 任何时刻权威索引里至多一个运行中的子代理
        var events = watch.Events.Select(e => e.Event).ToList();
        var starts = events.OfType<SubagentStartEventDto>().ToList();
        var ends = events.OfType<SubagentEndEventDto>().ToList();
        Assert.Equal(2, starts.Count);
        var first = await RunWatch.OpenAsync(client, starts[0].ChildRunId);
        Assert.True(starts[1].At >= first.Snapshot!.EndedAt, "the second subagent starts after the first one ended");
        Assert.All(ends, e => Assert.Equal("done", e.Status));
        Assert.All(device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged), s => Assert.True(s.Subagents.Length <= 1));

        parentGate.SetResult();
        await watch.SettledAsync();
    }

    // ───────────────────────── 父中止级联 ─────────────────────────

    [Fact]
    public async Task Aborting_the_parent_run_cascades_to_the_running_subagent_and_leaves_no_run_behind()
    {
        var (client, _, session) = await SetupAsync();
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs()), S.Say("never reached"))
            .On(S.ChildSession(session.Id, "p1"), S.Hang());
        var device = await Watch<DeviceTopicState>.OpenAsync(client, DeviceMethods.DeviceTopic);

        await StartAsync(client, session.Id, "go", "p1");
        var watch = await RunWatch.OpenAsync(client, "p1");
        await WaitForAsync(() => watch.Of<SubagentStartEventDto>().Any(), "the subagent to start");
        await device.WaitAsync(() => device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged).Any(s => s.Subagents.Length == 1), "the subagent in the authoritative index");

        await client.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams("p1"), idem: "abort-1");
        await watch.SettledAsync();

        Assert.Equal("aborted", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Equal(watch.Of<ToolStartEventDto>().Count(), watch.Of<ToolEndEventDto>().Count()); // 每个 tool_start 仍配得上 tool_end
        // 权威广播收口:没有运行中的 Run,也没有运行中的子代理。
        // ★ 必须先等这一条,再看子 Run 的快照:父 Run 的 run_settled 不蕴含子 Run 已收口——Task 工具在循环里是「可中止的等待」(中止时循环立即返回、不等工具),
        // 子 Run 的 run_end(aborted) 与父 Run 的 run_settled 谁先谁后不保证(整程序集并发、CPU 吃紧时,父 Run 稳定地先到,这条断言以前就是在这个窗口里读到 "running" 的)。
        // 设计上的兜底就是这条权威广播(05 §5、§9):它由真源上「仍在运行」的 Run 算出,没有子代理 ⇒ 子 Run 已是终态。
        await device.WaitAsync(() => device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged).Last() is { Runs.Length: 0, Subagents.Length: 0 }, "nothing left running");
        var child = await RunWatch.OpenAsync(client, "p1:sub:1");
        Assert.Equal("aborted", child.Snapshot!.State);
    }

    // ───────────────────────── runs.background ─────────────────────────

    [Fact]
    public async Task Runs_background_moves_a_foreground_subagent_to_the_background_the_parent_carries_on_and_the_report_arrives_later()
    {
        var (client, _, session) = await SetupAsync();
        var gate = new TaskCompletionSource();
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs()), S.Say("moving on"), S.Say("report seen"))
            .On(S.ChildSession(session.Id, "p1"), S.Gated(gate.Task, S.Say("the late result")));

        await StartAsync(client, session.Id, "go", "p1");
        var watch = await RunWatch.OpenAsync(client, "p1");
        await WaitForAsync(() => watch.Of<SubagentStartEventDto>().Any(), "the subagent to start");

        // 顶层 Run 不能挪;不存在的 Run 是 not_found
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(ConversationMethods.RunsBackground, new RunsBackgroundParams("p1"), idem: "bg-0"))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(ConversationMethods.RunsBackground, new RunsBackgroundParams("nope"), idem: "bg-1"))).Code);

        await client.CallAsync(ConversationMethods.RunsBackground, new RunsBackgroundParams("p1:sub:1"), idem: "bg-2");
        await watch.SettledAsync();

        // 这次 Task 调用当场以「后台」收场,父 Run 照常结束;子代理还在跑
        Assert.Contains("moved this subagent to the background", watch.Of<ToolEndEventDto>().Single(t => t.CallId == "c1").Content);
        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Empty(watch.Of<SubagentEndEventDto>());
        // 再挪一次不是错误(已经是后台)
        await client.CallAsync(ConversationMethods.RunsBackground, new RunsBackgroundParams("p1:sub:1"), idem: "bg-3");

        gate.SetResult();
        await WaitForAsync(() => _llm.RequestsOf(session.Id).Count >= 3, "the report to reach the parent session");
        Assert.Contains("the late result", S.WireText(_llm.RequestsOf(session.Id)[2]));
    }

    // ───────────────────────── 子代理不能提问 ─────────────────────────

    [Fact]
    public async Task A_subagent_that_needs_approval_is_refused_on_the_spot_nothing_is_ever_asked_of_a_human_and_the_command_never_runs()
    {
        var (client, project, session) = await SetupAsync();
        var interactions = await Watch<InteractionsDto>.OpenAsync(client, ConversationMethods.InteractionsTopic);
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs("clean up")), S.Say("parent done"))
            .On(S.ChildSession(session.Id, "p1"),
                S.Call("b1", "Bash", """{"command":"touch should-not-exist.txt","description":"create a file"}"""),
                S.Say("I could not run it, the parent must."));

        await StartAsync(client, session.Id, "go", "p1", permission: "auto");
        await (await RunWatch.OpenAsync(client, "p1")).SettledAsync();

        var child = await RunWatch.OpenAsync(client, "p1:sub:1");
        var denied = child.Of<ToolEndEventDto>().Single(t => t.CallId == "b1");
        Assert.True(denied.IsError);
        Assert.Contains("a subagent cannot ask for it", denied.Content);
        Assert.False(File.Exists(Path.Combine(project.Path, "should-not-exist.txt")));
        Assert.DoesNotContain(child.Of<InteractionRequestEventDto>(), _ => true);
        Assert.Empty(interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).SelectMany(s => s.Pending));
        // 拒绝之后子代理继续跑完,把话说给父代理
        Assert.Contains("I could not run it", S.WireText(_llm.RequestsOf(session.Id)[1]));
    }

    // ───────────────────────── 两个控制端一致、断线续传 ─────────────────────────

    [Fact]
    public async Task Two_controllers_see_the_same_subagent_events_and_a_reconnect_resumes_from_the_cursor_without_replaying_the_start()
    {
        var (a, _, session) = await SetupAsync("ctrl-a");
        var b = await _rig.ConnectAsync("ctrl-b");
        var gate = new TaskCompletionSource();
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs()), S.Say("done"))
            .On(S.ChildSession(session.Id, "p1"), S.Gated(gate.Task, S.Say("late answer")));

        await StartAsync(a, session.Id, "go", "p1");
        var watchA = await RunWatch.OpenAsync(a, "p1");
        var watchB = await RunWatch.OpenAsync(b, "p1");
        await WaitForAsync(() => watchA.Of<SubagentStartEventDto>().Any() && watchB.Of<SubagentStartEventDto>().Any(), "both controllers to see the start");
        var cursor = watchA.LastSeq;

        // A 的网络断了;断线期间子代理跑完、父 Run 收口
        await a.DisposeAsync();
        gate.SetResult();
        await watchB.SettledAsync();

        // A 重连:带着游标续传——只补发游标之后的事件,不重放 start;终态的 end 在里面
        var reconnected = await _rig.ConnectAsync("ctrl-a");
        var resumed = await RunWatch.OpenAsync(reconnected, "p1", cursor);
        await WaitForAsync(() => resumed.Of<RunSettledEventDto>().Any(), "the resumed events to arrive");
        Assert.Null(resumed.Snapshot);
        Assert.Empty(resumed.Of<SubagentStartEventDto>());
        Assert.Single(resumed.Of<SubagentEndEventDto>());
        Assert.True(resumed.Events.All(e => e.Seq > cursor));

        // 续传的事件 + 断线前的事件 = B 看到的全部(类型和序号都一致)
        // (文本增量不比:无损裁剪会在 message_end 之后把它从日志里清掉,重放里本来就没有)
        var merged = watchA.Events.Concat(resumed.Events).Where(e => e.Event is not MessageDeltaEventDto).OrderBy(e => e.Seq).Select(e => (e.Seq, e.Event.GetType())).ToList();
        Assert.Equal(watchB.Events.Where(e => e.Event is not MessageDeltaEventDto).Select(e => (e.Seq, e.Event.GetType())).ToList(), merged);

        // 不带游标(游标丢了):给快照,日志里的 start 和 end 都在,顺序对
        var fresh = await RunWatch.OpenAsync(reconnected, "p1");
        var kinds = fresh.Events.Select(e => e.Event).ToList();
        Assert.True(kinds.IndexOf(fresh.Of<SubagentStartEventDto>().Single()) < kinds.IndexOf(fresh.Of<SubagentEndEventDto>().Single()));
    }

    // ───────────────────────── 设置:默认子代理模型 / 思考深度 ─────────────────────────

    [Fact]
    public async Task The_default_subagent_model_and_thinking_come_from_settings_unsupported_levels_degrade_quietly_and_a_dangling_model_falls_back_to_the_parent()
    {
        SetSettings("""{"subagent":{"model":"haiku","thinking":"high"}}""");
        var (client, _, session) = await SetupAsync();
        _llm.On(session.Id, S.Call("c1", "Task", S.TaskArgs()), S.Say("one"))
            .On(S.ChildSession(session.Id, "p1"), S.Say("child one"));

        await StartAsync(client, session.Id, "go", "p1", thinking: "medium");
        await (await RunWatch.OpenAsync(client, "p1")).SettledAsync();

        // 设置里的默认模型(haiku)压过父 Run;思考深度「high」不被 haiku 支持:发请求之前静默降级,而不是到适配器才抛、整次派发失败
        var child = await RunWatch.OpenAsync(client, "p1:sub:1");
        Assert.Equal("haiku", child.Of<RunStartEventDto>().Single().Model);
        Assert.Equal(ThinkingLevel.Off, Assert.Single(_llm.RequestsOf(S.ChildSession(session.Id, "p1"))).Options.Thinking);
        Assert.Equal("done", child.Snapshot!.State);

        // 设置里那个模型已经指不到任何绑定(供应商删了 / 别名改了):落到下一档(父 Run),不是整条失败
        SetSettings("""{"subagent":{"model":"ghost","thinking":"inherit"}}""");
        _llm.On(session.Id, S.Call("c2", "Task", S.TaskArgs()), S.Say("two"))
            .On(S.ChildSession(session.Id, "p2", 2), S.Say("child two")); // 子 Run 序号是进程内单调递增(2.x 同):第二次派发是 :sub:2
        await StartAsync(client, session.Id, "again", "p2", thinking: "medium");
        await (await RunWatch.OpenAsync(client, "p2")).SettledAsync();
        var second = await RunWatch.OpenAsync(client, "p2:sub:2");
        Assert.Equal("sonnet", second.Of<RunStartEventDto>().Single().Model);
        Assert.Equal(ThinkingLevel.Medium, Assert.Single(_llm.RequestsOf(S.ChildSession(session.Id, "p2", 2))).Options.Thinking); // 「跟随」= 父 Run 这一轮的档位
    }

    // ───────────────────────── 定义:列表 / 诊断 / 启停 ─────────────────────────

    [Fact]
    public async Task Subagent_definitions_are_listed_diagnosed_and_switched_off_for_the_next_run()
    {
        var (client, project, session) = await SetupAsync();
        var agents = Directory.CreateDirectory(Path.Combine(project.Path, ".next-cowork", "agents"));
        File.WriteAllText(Path.Combine(agents.FullName, "reviewer-x.md"), "---\ndescription: reviews things\ntools: Read, Grep\ncolor: blue\n---\nYou review.\n");
        File.WriteAllText(Path.Combine(agents.FullName, "broken.md"), "---\nname: broken\n---\nno description here\n");

        var list = (await client.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams(project.WorkspaceId))).Agents;
        Assert.Equal(["general-purpose", "code-analyst", "code-reviewer", "code-editor"], list.Where(a => a.Scope == "builtin").Select(a => a.Name));
        var reviewer = Assert.Single(list, a => a.Name == "reviewer-x");
        Assert.Equal(("project", "reviews things", "blue", true), (reviewer.Scope, reviewer.Description, reviewer.Color, reviewer.Enabled));
        Assert.Equal(["Read", "Grep"], reviewer.Tools!);
        Assert.EndsWith("reviewer-x.md", reviewer.Source);
        Assert.DoesNotContain(list, a => a.Name == "broken");

        var diagnostics = (await client.CallAsync(OrchestrationMethods.SubagentsDiagnostics, new SubagentsListParams(project.WorkspaceId))).Diagnostics;
        Assert.Contains(diagnostics, d => d.Key == "agents.descriptionMissing" && d.Path.EndsWith("broken.md"));

        // 启用状态进了下一个 Run 的 Task 工具描述
        _llm.On(session.Id, S.Say("one"), S.Say("two"));
        await StartAsync(client, session.Id, "first", "p1");
        await (await RunWatch.OpenAsync(client, "p1")).SettledAsync();
        Assert.Contains("reviewer-x", TaskDescription(_llm.RequestsOf(session.Id)[0]));

        await client.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams("reviewer-x", false), idem: "off-1");
        Assert.False((await client.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams(project.WorkspaceId))).Agents.Single(a => a.Name == "reviewer-x").Enabled);
        await StartAsync(client, session.Id, "second", "p2");
        await (await RunWatch.OpenAsync(client, "p2")).SettledAsync();
        Assert.DoesNotContain("reviewer-x", TaskDescription(_llm.RequestsOf(session.Id)[1]));

        // 名字不合法:invalid_request;未知项目:not_found
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() =>
            client.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams("../evil", false), idem: "off-2"))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() =>
            client.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams("nope")))).Code);

        await client.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams("reviewer-x", true), idem: "on-1");
        Assert.True((await client.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams(project.WorkspaceId))).Agents.Single(a => a.Name == "reviewer-x").Enabled);
    }

    private static string TaskDescription(LlmRequest request) => request.Tools.Single(t => t.ExternalName == "Task").Description;
}

/// <summary>
/// 子代理 / 计划 / 目标的角色(docs/design/09 §4、§8.4):viewer 只读(定义列表、诊断、目标、<c>goal</c> 主题);member 可设定 / 清除目标、汇报、执行计划、转后台;
/// 启停子代理是设备配置(owner);<c>full</c> 档位只有 owner。控制端经真实的局域网承载(TLS 1.3 + 配对)连上,角色由信任记录决定。
/// </summary>
public sealed class OrchestrationRoleTests : IAsyncLifetime
{
    private readonly ScriptedLlm _llm = new();
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private int _port;

    public OrchestrationRoleTests()
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

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<DeviceClient> ConnectAsync(string name, string role)
    {
        var identity = Ncw.Protocol.Security.PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<string> CodeOf<T>(Func<Task<T>> call) => (await Assert.ThrowsAsync<ProtocolException>(call)).Code;

    [Fact]
    public async Task The_device_announces_the_new_capabilities_and_a_viewer_can_read_and_watch_but_nothing_that_changes_anything()
    {
        var project = await _rig.AddProjectAsync(_owner);
        var session = await _rig.CreateSessionAsync(_owner, project);
        await using var viewer = await ConnectAsync("viewer-1", "viewer");
        Assert.Contains(Capabilities.Subagents, viewer.Welcome.Features);
        Assert.Contains(Capabilities.Planning, viewer.Welcome.Features);

        // 读 / 看
        Assert.Equal(4, (await viewer.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams(project.WorkspaceId))).Agents.Length);
        _ = await viewer.CallAsync(OrchestrationMethods.SubagentsDiagnostics, new SubagentsListParams(project.WorkspaceId));
        Assert.Null((await viewer.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id))).Goal);
        var goals = await Watch<GoalsTopicState>.OpenAsync(viewer, OrchestrationMethods.GoalTopic);
        await goals.WaitAsync(() => goals.HasSnapshot, "goal snapshot");

        // 改:全部 forbidden
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams("code-editor", false), idem: "v1")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(OrchestrationMethods.SubagentsReport, new SubagentsReportParams(session.Id, "c1"), idem: "v2")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "x"), idem: "v3")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(OrchestrationMethods.GoalClear, new SessionRefParams(session.Id), idem: "v4")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.RunsBackground, new RunsBackgroundParams("r:sub:1"), idem: "v5")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(
            OrchestrationMethods.RunsExecutePlan,
            new RunsExecutePlanParams(new RunsStartParams("r", session.Id, "m", [new InputPartDto("text", "x")], new SendOptionsDto("sonnet")), new PlanFileDto("a", ".plan/a.md")), idem: "v6")));

        // 什么都没变:没有目标、没有任何上游请求
        Assert.Null((await _owner.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id))).Goal);
        Assert.Empty(_llm.All);
    }

    [Fact]
    public async Task A_member_can_set_and_clear_goals_and_report_but_not_switch_subagents_or_use_full_access()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await using var member = await ConnectAsync("member-1", "member");
        var session = await _rig.CreateSessionAsync(member, project);

        // 启停是设备配置:owner
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams("code-editor", false), idem: "m1")));
        // full 档位随 goal.set 带来也只有 owner(和 runs.start 同一条规则)
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(
            OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "x", new SendOptionsDto("sonnet", PermissionMode: "full")), idem: "m2")));
        Assert.Null((await member.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.Id))).Goal);

        var set = await member.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.Id, "ship it"), idem: "m3");
        Assert.Equal(GoalOutcomes.Set, set.Outcome);
        await member.CallAsync(OrchestrationMethods.GoalClear, new SessionRefParams(session.Id), idem: "m4");
        Assert.Equal("none", (await member.CallAsync(OrchestrationMethods.SubagentsReport, new SubagentsReportParams(session.Id, "nothing"), idem: "m5")).Status);

        // owner 可以启停
        await _owner.CallAsync(OrchestrationMethods.SubagentsSetEnabled, new SubagentsSetEnabledParams("code-editor", false), idem: "o1");
        Assert.False((await member.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams(project.WorkspaceId))).Agents.Single(a => a.Name == "code-editor").Enabled);
    }

    [Fact]
    public async Task An_unpaired_controller_cannot_use_the_new_methods_or_watch_the_goal_topic()
    {
        var identity = Ncw.Protocol.Security.PeerIdentity.Create("stranger");
        var stream = (await Ncw.Protocol.Security.SecureChannel.ConnectAsync(await Ncw.Protocol.Transport.LanTransport.ConnectAsync("127.0.0.1", _port, default), identity, null, default)).Stream;
        await using var stranger = await DeviceClient.ConnectAsync(stream, new ClientHello("stranger", "3.0.0"));

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => stranger.CallAsync(OrchestrationMethods.SubagentsList, new SubagentsListParams("w"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => stranger.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams("s"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<GoalsTopicState>.OpenAsync(stranger, OrchestrationMethods.GoalTopic)));
    }
}
