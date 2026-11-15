using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 对话链路的端到端测试:真实 Agent(进程内)+ <see cref="DeviceClient"/> 经本机承载 + 假上游 + 真实内置工具 + 真实权限扩展 / 交互闸门 / Run 注册表 / SQLite。
/// 控制端的 Store 和 Actions 在 <c>Ncw.Integration.Tests</c>;这里只验协议行为。
/// </summary>
public sealed class ConversationTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    // ───────────────────────── 默认实现:没有供应商 ─────────────────────────

    [Fact]
    public async Task Without_a_provider_the_run_ends_with_a_structured_error_instead_of_hanging()
    {
        await using var rig = new ConversationRig(_ => new AgentServices { Delayer = new NoDelay(), Http = new NoNetwork() }); // 默认:真实的模型解析(设备上还没有任何供应商)
        await rig.InitializeAsync();
        var client = await rig.ConnectAsync();
        var project = await rig.AddProjectAsync(client);
        var session = await rig.CreateSessionAsync(client, project, "anything");

        var start = await rig.StartAsync(client, session.Id, "hello");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await watch.SettledAsync();

        var end = Assert.Single(watch.Of<RunEndEventDto>());
        Assert.Equal("error", end.Status);
        // 设备上一个供应商也没配:路由给出结构化的「别名没有任何供应商」(配了但全停用是 llm.noEnabledProvider,见 CatalogProvidersTests)。
        Assert.Equal(Ncw.Agent.Llm.LlmErrorKeys.UnknownAlias, end.Error!.MessageKey);
        Assert.Equal("nohealthyprovider", end.Error.Code);
    }

    // ───────────────────────── 会话 ─────────────────────────

    [Fact]
    public async Task Session_metadata_methods_round_trip_and_unknown_ids_are_not_found()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);

        var created = await _rig.CreateSessionAsync(client, project, "sonnet");
        Assert.Equal("sonnet", created.Model);
        Assert.Equal("code", created.Mode);

        var renamed = await client.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams(created.Id, "重命名"));
        Assert.Equal("重命名", renamed.Title);
        Assert.Equal("manual", renamed.TitleSource);

        var model = await client.CallAsync(ConversationMethods.SessionsSetModel, new SessionsSetModelParams(created.Id, "opus", "p2"));
        Assert.Equal(("opus", "p2"), (model.Model, model.ModelProviderId));

        Assert.True((await client.CallAsync(ConversationMethods.SessionsArchive, new SessionsArchiveParams(created.Id, true))).Archived);
        Assert.True((await client.CallAsync(ConversationMethods.SessionsFavorite, new SessionsFavoriteParams(created.Id, true))).Favorited);

        var active = await client.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(project.WorkspaceId, Archived: false));
        var all = await client.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(project.WorkspaceId));
        Assert.Empty(active.Sessions);
        var item = Assert.Single(all.Sessions);
        Assert.True(item.Archived && item.Favorited);

        var copy = await client.CallAsync(ConversationMethods.SessionsDuplicate, new SessionsDuplicateParams(created.Id, "副本"));
        Assert.NotEqual(created.Id, copy.Id);
        Assert.Equal("副本", copy.Title);
        Assert.Equal(2, (await client.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(project.WorkspaceId))).Sessions.Length);

        await client.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(copy.Id));
        var gone = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(copy.Id)));
        Assert.Equal(ErrorCodes.NotFound, gone.Code);

        var missing = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams("nope", "x")));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        var noProject = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams("nope")));
        Assert.Equal(ErrorCodes.NotFound, noProject.Code);
    }

    [Fact]
    public async Task Creating_a_session_with_a_chosen_id_twice_returns_the_existing_one_and_changing_mode_is_visible_on_the_topic()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var first = await client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Id: "fixed-id", Title: "A"));
        var second = await client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Id: "fixed-id", Title: "B"));
        Assert.Equal(first, second);

        var watch = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(first.Id));
        await watch.WaitAsync(() => watch.HasSnapshot, "session snapshot");
        Assert.Equal("A", watch.Snapshot.Page.Session.Title);

        await client.CallAsync(ConversationMethods.SessionsSetMode, new SessionsSetModeParams(first.Id, "code")); // 没变:不发事件
        await client.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams(first.Id, "新标题"));
        await watch.WaitAsync(() => watch.Decoded<SessionDto>(SessionEvents.Updated).Any(s => s.Title == "新标题"), "session.updated");
    }

    [Fact]
    public async Task Editing_history_needs_an_idle_session_and_publishes_a_reset()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        _rig.Llm.Then(Up.Say("first answer")).Then(Up.Say("second answer"));

        var first = await _rig.StartAsync(client, session.Id, "question one");
        await (await RunWatch.OpenAsync(client, first.RunId)).SettledAsync();
        var second = await _rig.StartAsync(client, session.Id, "question two");
        await (await RunWatch.OpenAsync(client, second.RunId)).SettledAsync();

        var page = await client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id));
        Assert.Equal(4, page.Messages.Length);
        var userOne = page.Messages[0];
        var topic = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(session.Id));
        await topic.WaitAsync(() => topic.HasSnapshot, "snapshot");

        await client.CallAsync(ConversationMethods.SessionsDeleteTurn, new SessionsDeleteTurnParams(session.Id, userOne.Id));

        var after = await client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id));
        Assert.Equal(2, after.Messages.Length);
        await topic.WaitAsync(() => topic.Decoded<SessionPageDto>(SessionEvents.HistoryReset).Any(p => p.Messages.Length == 2), "history.reset");

        var branch = await client.CallAsync(ConversationMethods.SessionsBranch, new SessionsBranchParams(session.Id, after.Messages[0].Id, "分支"));
        Assert.Equal(2, (await client.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(branch.Id))).Messages.Length);

        var unknown = await Assert.ThrowsAsync<ProtocolException>(() =>
            client.CallAsync(ConversationMethods.SessionsEditMessage, new SessionsEditMessageParams(session.Id, "no-such-message", "x", false)));
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);
    }

    // ───────────────────────── 流式对话 ─────────────────────────

    [Fact]
    public async Task A_conversation_streams_ordered_events_and_commits_messages_visible_on_the_session_topic()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        var topic = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(session.Id));
        var device = await Watch<DeviceTopicState>.OpenAsync(client, DeviceMethods.DeviceTopic);
        _rig.Llm.Then(Up.Say("Hello", ", ", "world"));

        var start = await _rig.StartAsync(client, session.Id, "say hi");
        Assert.Equal(StartOutcomes.Started, start.Outcome);
        var watch = await RunWatch.OpenAsync(client, start.RunId); // 订阅晚于启动也不丢:日志重放
        await watch.SettledAsync();

        var kinds = watch.Events.Select(e => e.Event).Select(e => e.GetType().Name.Replace("EventDto", "")).ToList();
        Assert.Equal("RunStart", kinds[0]);
        Assert.Equal("RunSettled", kinds[^1]);
        Assert.True(kinds.IndexOf("RunEnd") < kinds.IndexOf("RunSettled"));
        Assert.True(kinds.IndexOf("RunStart") < kinds.IndexOf("MessageStart"));
        // seq 严格递增(同一个 Run 的日志)
        var seqs = watch.Events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.Order().ToList(), seqs);
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);

        // 文本增量拼起来 = 完整消息;message_end 带完整消息和用量
        var text = string.Concat(watch.Of<MessageDeltaEventDto>().Where(d => d.Kind == "text").Select(d => d.Delta));
        Assert.Equal("Hello, world", text);
        var assistantEnd = watch.Of<MessageEndEventDto>().Single(e => e.Message.Role == "assistant");
        Assert.Contains("Hello, world", assistantEnd.Message.PartsJson);
        Assert.Equal(10, assistantEnd.Usage!.InputTokens);
        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);

        // 已落库的消息:用户输入 + 助手回复,经会话主题推给另一个观察者
        await topic.WaitAsync(() => topic.Decoded<MessageDto>(SessionEvents.MessageCommitted).Count >= 2, "message.committed x2");
        var committed = topic.Decoded<MessageDto>(SessionEvents.MessageCommitted);
        Assert.Contains(committed, m => m.Role == "user" && m.PartsJson.Contains("say hi"));
        Assert.Contains(committed, m => m.Role == "assistant");
        var page = await client.CallAsync(ConversationMethods.SessionsGetPage, new SessionsGetPageParams(session.Id, 10));
        Assert.Equal(2, page.Messages.Length);
        Assert.Equal(start.RunId, page.MessageRuns[page.Messages[1].Id]);

        // activeRuns:起止都播,Rev 严格递增;最后一帧没有运行中的 Run
        await device.WaitAsync(() => device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged).Any(s => s.Runs.Length == 0 && s.Rev > 0), "activeRuns drained");
        var broadcasts = device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged);
        Assert.Contains(broadcasts, s => s.Runs.Any(r => r.RunId == start.RunId && r.SessionId == session.Id));
        var revs = broadcasts.Select(b => b.Rev).ToList();
        Assert.Equal(revs.Order().ToList(), revs);
        Assert.Equal(revs.Distinct().Count(), revs.Count);
    }

    [Fact]
    public async Task Runs_start_is_idempotent_and_a_reused_run_id_with_a_new_key_is_a_conflict()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        _rig.Llm.Then(Up.Say("once"));

        var first = await _rig.StartAsync(client, session.Id, "go", runId: "run-fixed", idem: "k1");
        var again = await _rig.StartAsync(client, session.Id, "go", runId: "run-fixed", idem: "k1"); // 续连后重发:同一个结果
        Assert.Equal(first, again);
        await (await RunWatch.OpenAsync(client, "run-fixed")).SettledAsync();
        Assert.Single(_rig.Llm.Requests);
        var clash = await Assert.ThrowsAsync<ProtocolException>(() => _rig.StartAsync(client, session.Id, "go", runId: "run-fixed", idem: "k2"));
        Assert.Equal(ErrorCodes.Conflict, clash.Code);
    }

    [Fact]
    public async Task Tools_list_reports_the_builtin_tools_with_their_traits()
    {
        var client = await _rig.ConnectAsync();
        var tools = (await client.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools;

        Assert.Contains(tools, t => t.Name == "Read" && t.ReadOnly && !t.Destructive);
        Assert.Contains(tools, t => t.Name == "Bash" && !t.ReadOnly && t.Destructive);
        Assert.All(tools, t => Assert.StartsWith("{", t.InputSchemaJson));
    }

    // ───────────────────────── 审批 ─────────────────────────

    [Fact]
    public async Task A_tool_call_asks_for_approval_two_controllers_race_only_one_wins_and_the_run_completes()
    {
        var a = await _rig.ConnectAsync("ctrl-a");
        var b = await _rig.ConnectAsync("ctrl-b");
        var project = await _rig.AddProjectAsync(a);
        var session = await _rig.CreateSessionAsync(a, project);
        var target = Path.Combine(project.Path, "hello.txt");
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target)))).Then(Up.Say("written"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(b, ConversationMethods.InteractionsTopic);

        var start = await _rig.StartAsync(a, session.Id, "write the file");
        var watch = await RunWatch.OpenAsync(a, start.RunId);

        // 审批出现在 interactions 主题(另一个控制端也看得见)和 Run 事件流里,带执行环境标识(设备 + 环境)
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending approval");
        var pending = Assert.IsType<ToolPermissionInteractionDto>(Assert.Single(interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending));
        Assert.Equal("Write", pending.ToolName);
        Assert.Equal(start.RunId, pending.RunId);
        Assert.Equal(_rig.Host.DeviceId, pending.Env.DeviceId);
        Assert.Equal("local", pending.Env.Environment);
        Assert.Contains("session", pending.AllowedScopes);
        await Watch<Unit>.UntilAsync(() => watch.Of<InteractionRequestEventDto>().Any(), "interaction_request in the run stream");
        Assert.False(File.Exists(target), "nothing runs before the answer");

        var device = await Watch<DeviceTopicState>.OpenAsync(b, DeviceMethods.DeviceTopic);
        await device.WaitAsync(() => device.HasSnapshot, "device snapshot");
        var running = Assert.Single(device.Snapshot.Runs);
        Assert.Equal(1, running.PendingInteractions); // 权威广播的首帧带着待处理交互数
        Assert.True(device.Snapshot.Rev > 0);

        // 两个控制端同时回答:只有一个成功,另一个 conflict 并说明是谁什么时候处理的
        var answer = new ToolPermissionResponseDto(PermissionChoices.AllowOnce);
        var ra = Try(() => a.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, answer)));
        var rb = Try(() => b.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, answer)));
        var results = await Task.WhenAll(ra, rb);

        var winner = Assert.Single(results, r => r.Result is not null);
        var loser = Assert.Single(results, r => r.Error is not null);
        Assert.Equal(ErrorCodes.Conflict, loser.Error!.Code);
        var details = FrameCodec.DecodePayload(loser.Error.Details!, PayloadShapes.GetShape<InteractionConflictDto>());
        Assert.Equal(ConflictReasons.AlreadyResolved, details.Reason);
        Assert.Equal("answered", details.Resolution!.Status);
        Assert.Equal(winner.Result!.Resolution.ResolvedBy, details.Resolution.ResolvedBy);
        Assert.Contains(details.Resolution.ResolvedBy, new[] { "ctrl-a", "ctrl-b" });
        Assert.True(details.Resolution.At > 0);

        // 审批通过后 Run 完成,文件真的写了
        await watch.SettledAsync();
        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Equal("hi", File.ReadAllText(target));
        var toolEnd = Assert.Single(watch.Of<ToolEndEventDto>());
        Assert.False(toolEnd.IsError);
        var toolStart = Assert.Single(watch.Of<ToolStartEventDto>());
        Assert.Equal("local", toolStart.Environment!.Environment);
        var resolved = Assert.Single(watch.Of<InteractionResolvedEventDto>());
        Assert.Equal(winner.Result.Resolution.ResolvedBy, resolved.Resolution.ResolvedBy);
        Assert.IsType<ToolPermissionResponseDto>(resolved.Response);

        // 全量广播收口:交互清空,Run 不再活动
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last().Pending.Length == 0, "interactions drained");
        await device.WaitAsync(() => device.Decoded<DeviceTopicState>(DeviceEvents.ActiveRunsChanged).Last().Runs.Length == 0, "activeRuns drained");

        // 已处理的再答一次:conflict(哪怕答案一模一样);没有过的 id:not_found
        var late = await Assert.ThrowsAsync<ProtocolException>(() => a.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, answer)));
        Assert.Equal(ErrorCodes.Conflict, late.Code);
        var never = await Assert.ThrowsAsync<ProtocolException>(() => a.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams("never-existed", 1, answer)));
        Assert.Equal(ErrorCodes.NotFound, never.Code);
    }

    [Fact]
    public async Task An_invalid_or_stale_answer_does_not_consume_the_interaction_and_a_denial_reaches_the_model()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        var target = Path.Combine(project.Path, "denied.txt");
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target)))).Then(Up.Say("ok, not writing"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(client, ConversationMethods.InteractionsTopic);

        var start = await _rig.StartAsync(client, session.Id, "write it");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending");
        var pending = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending[0];

        // 版本对不上:conflict,带当前版本;仍然挂起
        var stale = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(
            ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version + 100, new ToolPermissionResponseDto(PermissionChoices.AllowOnce))));
        Assert.Equal(ErrorCodes.Conflict, stale.Code);
        Assert.Equal(ConflictReasons.StaleVersion, FrameCodec.DecodePayload(stale.Details!, PayloadShapes.GetShape<InteractionConflictDto>()).Reason);

        // 回答的种类不对 / allowAlways 没给作用域:invalid_request,不消耗
        var wrongKind = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(
            ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new GoalProposalResponseDto(true))));
        Assert.Equal(ErrorCodes.InvalidRequest, wrongKind.Code);
        var noScope = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(
            ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowAlways))));
        Assert.Equal(ErrorCodes.InvalidRequest, noScope.Code);
        Assert.Single((await client.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending);

        // 拒绝:工具没跑,模型在下一个请求里看到拒绝和理由
        await client.CallAsync(ConversationMethods.InteractionsRespond,
            new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.Deny, Reason: "keep it clean")));
        await watch.SettledAsync();
        Assert.False(File.Exists(target));
        Assert.True(Assert.Single(watch.Of<ToolEndEventDto>()).IsError);
        var seen = string.Join('\n', _rig.Llm.Requests[^1].Messages.SelectMany(m => m.Parts).OfType<Ncw.Agent.Llm.ToolResultPart>().Select(r => r.Output.Content));
        Assert.Contains("keep it clean", seen);
    }

    [Fact]
    public async Task The_full_permission_mode_skips_approval_but_only_an_owner_may_use_it()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        var target = Path.Combine(project.Path, "full.txt");
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target, "full")))).Then(Up.Say("done"));

        var start = await _rig.StartAsync(client, session.Id, "write", permission: "full");
        await (await RunWatch.OpenAsync(client, start.RunId)).SettledAsync();

        Assert.Equal("full", File.ReadAllText(target));
        Assert.Empty((await client.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending);
    }

    // ───────────────────────── 重连与续传 ─────────────────────────

    [Fact]
    public async Task A_reconnecting_controller_resumes_from_its_cursor_without_gaps_or_duplicates_and_a_fresh_one_gets_a_snapshot()
    {
        var first = await _rig.ConnectAsync("ctrl-a");
        var project = await _rig.AddProjectAsync(first);
        var session = await _rig.CreateSessionAsync(first, project);
        var target = Path.Combine(project.Path, "resume.txt");
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target)))).Then(Up.Say("all done"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(first, ConversationMethods.InteractionsTopic);

        var start = await _rig.StartAsync(first, session.Id, "write");
        var before = await RunWatch.OpenAsync(first, start.RunId);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending");
        await Watch<Unit>.UntilAsync(() => before.Of<InteractionRequestEventDto>().Any(), "request event");
        var cursor = before.LastSeq;
        Assert.NotNull(before.Snapshot); // 首次订阅给快照
        await first.DisposeAsync();      // 断线

        // 断线期间:另一个控制端批准,Run 跑完
        var other = await _rig.ConnectAsync("ctrl-b");
        var pending = (await other.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending.Single();
        await other.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await (await RunWatch.OpenAsync(other, start.RunId)).SettledAsync();

        // 重连:带上游标,只补发之后的事件(没有快照)
        var again = await _rig.ConnectAsync("ctrl-a");
        var resumed = await RunWatch.OpenAsync(again, start.RunId, cursor);
        await resumed.SettledAsync();
        Assert.Null(resumed.Snapshot);
        Assert.All(resumed.Events, e => Assert.True(e.Seq > cursor));
        Assert.Contains(resumed.Events, e => e.Event is InteractionResolvedEventDto);
        // 断线前 + 续传后 = 完整日志,没有重复、没有缺口。(消息提交后日志无损地清掉了它的文本增量:完整消息已在 message_end 里,
        // 所以对比时把增量排除——断线前看过的增量不会出现在之后的快照里,这是设计。)
        static List<long> Structural(IEnumerable<(long Seq, RunEventDto Event)> events) =>
            [.. events.Where(e => e.Event is not MessageDeltaEventDto).Select(e => e.Seq)];
        var merged = Structural(before.Events.Concat(resumed.Events));
        var complete = Structural((await RunWatch.OpenAsync(again, start.RunId)).Events);
        Assert.Equal(complete, merged);
        Assert.Equal(merged.Distinct().Count(), merged.Count);

        // 新的控制端(不带游标):给权威快照,状态已是终态,日志齐全
        var fresh = await RunWatch.OpenAsync(again, start.RunId);
        Assert.Equal("done", fresh.Snapshot!.State);
        Assert.True(fresh.Snapshot.Settled);
        Assert.Equal("fresh", fresh.Snapshot.Cursor);
        Assert.Equal(complete, Structural(fresh.Events));
        Assert.Equal(fresh.Snapshot.Seq, fresh.Snapshot.Events.Max(e => e.Seq));
    }

    [Fact]
    public async Task Subscribing_to_a_run_that_never_existed_is_not_found_and_aborting_it_is_harmless()
    {
        var client = await _rig.ConnectAsync();
        await _rig.AddProjectAsync(client);

        var e = await Assert.ThrowsAsync<ProtocolException>(() => RunWatch.OpenAsync(client, "no-such-run"));
        Assert.Equal(ErrorCodes.NotFound, e.Code);
        await client.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams("no-such-run")); // 幂等,不抛
    }

    [Fact]
    public async Task Aborting_a_run_that_waits_for_approval_ends_it_as_aborted_and_clears_the_interaction()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        var target = Path.Combine(project.Path, "aborted.txt");
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target))));

        var start = await _rig.StartAsync(client, session.Id, "write");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await Watch<Unit>.UntilAsync(() => watch.Of<InteractionRequestEventDto>().Any(), "request");

        await client.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams(start.RunId));
        await watch.SettledAsync();

        Assert.Equal("aborted", watch.Of<RunEndEventDto>().Single().Status);
        Assert.False(File.Exists(target));
        Assert.True(Assert.Single(watch.Of<ToolEndEventDto>()).IsError); // 工具没跑,界面上的卡片以失败收口
        // 挂起的交互被清掉(权威状态在 interactions 主题 / list;中止时的 interaction_resolved 事件和 run_end 谁先谁后不保证,控制端不依赖它)
        await Watch<Unit>.UntilAsync(() => client.CallAsync(ConversationMethods.InteractionsList, Unit.Value).GetAwaiter().GetResult().Pending.Length == 0, "interaction cleared");
        Assert.False(File.Exists(target));
    }

    // ───────────────────────── 排队 ─────────────────────────

    [Fact]
    public async Task A_message_sent_while_a_run_is_active_is_queued_and_continues_after_the_run_settles()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        var target = Path.Combine(project.Path, "queue.txt");
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target)))).Then(Up.Say("first done")).Then(Up.Say("second done"));
        var topic = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(session.Id));

        var start = await _rig.StartAsync(client, session.Id, "first");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await Watch<Unit>.UntilAsync(() => watch.Of<InteractionRequestEventDto>().Any(), "paused on approval");

        // 忙时再发:进队列(不是失败),队列经会话主题推送(全量,Rev 全局单调)
        var queued = await _rig.StartAsync(client, session.Id, "second");
        Assert.Equal(StartOutcomes.Queued, queued.Outcome);
        var item = Assert.Single(queued.Queue!.Items);
        Assert.Equal(QueueStates.Pending, item.State);
        await topic.WaitAsync(() => topic.Decoded<QueueDto>(SessionEvents.QueueUpdated).Any(q => q.Items.Length == 1), "queue.updated");

        // 入队 / 插话开关 / 移除
        var extra = await client.CallAsync(ConversationMethods.QueueEnqueue,
            new QueueEnqueueParams(session.Id, "extra-id", [new InputPartDto(InputPartKinds.Text, "extra")], new SendOptionsDto("sonnet")));
        Assert.True(extra.Accepted);
        Assert.Equal(2, extra.Queue.Items.Length);
        var promoted = await client.CallAsync(ConversationMethods.QueuePromote, new QueueItemParams(session.Id, "extra-id"));
        Assert.Equal(QueueStates.Promoted, promoted.Queue.Items.Single(i => i.Id == "extra-id").State);
        var removed = await client.CallAsync(ConversationMethods.QueueRemove, new QueueItemParams(session.Id, "extra-id"));
        Assert.True(removed.Accepted);
        var missing = await client.CallAsync(ConversationMethods.QueueRemove, new QueueItemParams(session.Id, "extra-id"));
        Assert.False(missing.Accepted); // 找不到:被拒绝,队列没变
        Assert.Single((await client.CallAsync(ConversationMethods.QueueList, new SessionRefParams(session.Id))).Items);
        var revs = topic.Decoded<QueueDto>(SessionEvents.QueueUpdated).Select(q => q.Rev).ToList();
        Assert.Equal(revs.Order().ToList(), revs);

        // 批准后第一个 Run 完成,排队的那句话在 run_settled 之后自动续跑成第二个 Run
        var pending = (await client.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending.Single();
        await client.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await watch.SettledAsync();
        await Watch<Unit>.UntilAsync(() => _rig.Llm.Requests.Count == 3, "the queued message ran as a follow-up run");
        await topic.WaitAsync(() => topic.Decoded<QueueDto>(SessionEvents.QueueUpdated).Last().Items.Length == 0, "queue drained");
        await topic.WaitAsync(() => topic.Decoded<MessageDto>(SessionEvents.MessageCommitted).Count(m => m.Role == "assistant") >= 3, "all assistant messages committed");
    }

    // ───────────────────────── 上下文 ─────────────────────────

    [Fact]
    public async Task Context_preview_counts_the_history_and_manual_compact_replaces_it_with_a_boundary()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);

        var empty = await client.CallAsync(ConversationMethods.ContextPreview, new ContextPreviewParams(session.Id));
        Assert.Equal(200_000, empty.Window);
        Assert.Equal(empty.Used, empty.Segments.Sum(s => s.Tokens)); // 各档之和恒等于总数

        _rig.Llm.Then(Up.Say(new string('a', 4000)));
        var start = await _rig.StartAsync(client, session.Id, "fill the context");
        await (await RunWatch.OpenAsync(client, start.RunId)).SettledAsync();
        var full = await client.CallAsync(ConversationMethods.ContextPreview, new ContextPreviewParams(session.Id));
        Assert.True(full.Used > empty.Used);

        var topic = await Watch<SessionTopicState>.OpenAsync(client, ConversationMethods.SessionTopic(session.Id));
        await topic.WaitAsync(() => topic.HasSnapshot, "snapshot");
        _rig.Llm.Then(Up.Say("The summary of the conversation."));
        var compacted = await client.CallAsync(ConversationMethods.ContextCompact, new ContextCompactParams(session.Id, "keep the facts"));

        Assert.Null(compacted.Error);
        Assert.NotNull(compacted.Boundary);
        Assert.Contains("compact_boundary", compacted.Boundary!.PartsJson);
        await topic.WaitAsync(() => topic.Decoded<SessionPageDto>(SessionEvents.HistoryReset).Any(p => p.Messages.Any(m => m.Id == compacted.Boundary.Id)), "history.reset with the boundary");
    }

    private sealed record Attempt<T>(T? Result, ProtocolException? Error) where T : class;

    private static async Task<Attempt<T>> Try<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return new Attempt<T>(await call(), null);
        }
        catch (ProtocolException e)
        {
            return new Attempt<T>(null, e);
        }
    }
}
