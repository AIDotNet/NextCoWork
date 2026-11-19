using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

/// <summary>
/// 控制端 Store / Actions 对真实 Agent 的对话链路集成测试:Actions 发起、设备推送经 <see cref="DeviceLink"/> 折进 <see cref="ConversationState"/>,
/// 审批先到先得、断线重连后按游标续传、设备断开时运行中的 Run 是「未知」而不是「已结束」。
/// </summary>
public sealed class ConversationLinkTests : IAsyncLifetime
{
    private const string Token = "integration-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwi-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-conv-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _agentCts = new();
    private readonly FakeLlm _llm = new();
    private readonly List<Controller> _controllers = [];
    private Task _agent = Task.CompletedTask;
    private AgentHost _host = null!;

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    /// <summary>一个控制端:自己的 Store、自己的连接(可以被掐断)。</summary>
    private sealed class Controller
    {
        public ClientStores Stores { get; } = new();

        public LockedDispatcher Ui { get; } = new();

        public DeviceLink Link { get; set; } = null!;

        public DeviceRegistry Devices { get; set; } = null!;

        public ConversationActions Actions { get; set; } = null!;

        public CuttableStream? Current { get; set; }

        public ConversationState State
        {
            get
            {
                lock (Ui.Gate)
                {
                    return Stores.Conversations.State;
                }
            }
        }

        public WorkspaceState Workspace
        {
            get
            {
                lock (Ui.Gate)
                {
                    return Stores.Workspace.State;
                }
            }
        }
    }

    private sealed class LockedDispatcher : IUiDispatcher
    {
        public readonly object Gate = new();

        public void Post(Action action)
        {
            lock (Gate)
            {
                action();
            }
        }
    }

    /// <summary>可以从外面掐断的连接(模拟网络断开,Agent 进程不受影响)。</summary>
    private sealed class CuttableStream(Stream inner) : Stream
    {
        public void Cut() => inner.Dispose();

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NoDelay : IDelayer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeModels(ILlmClient client) : IModelResolver
    {
        public ILlmClient Client { get; } = client;

        public ModelBinding? Resolve(ModelRef model) => model.Alias == "sonnet" ? new ModelBinding("sonnet", "p1", "up", 200_000, 8192) : null;
    }

    private sealed class FakeLlm : ILlmClient
    {
        private readonly Queue<IReadOnlyList<LlmEvent>> _scripts = new();

        public int Requests { get; private set; }

        public FakeLlm Then(IReadOnlyList<LlmEvent> events)
        {
            lock (_scripts)
            {
                _scripts.Enqueue(events);
            }

            return this;
        }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (_scripts)
            {
                Requests++;
                _scripts.TryDequeue(out var script);
                events = script;
            }

            foreach (var ev in events ?? [new StreamError(AgentError.Create(AgentErrorCode.Provider, "no script left", false))])
            {
                await Task.Yield();
                yield return ev;
            }
        }

        private IReadOnlyList<LlmEvent>? events;
    }

    private static IReadOnlyList<LlmEvent> Say(params string[] chunks) =>
        [new MessageStart("m", "p", "anthropic", "up"), .. chunks.Select(c => (LlmEvent)new TextDelta(0, c)), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5), 5, CostInfo.NotMetered)];

    private static IReadOnlyList<LlmEvent> CallWrite(string callId, string path) =>
    [
        new MessageStart("m", "p", "anthropic", "up"),
        new ToolCallStart(0, callId, "Write"),
        new ToolCallDelta(0, callId, new System.Text.Json.Nodes.JsonObject { ["file_path"] = path, ["content"] = "from the client test" }.ToJsonString()),
        new ToolCallEnd(0, callId),
        new MessageEnd(StopReason.ToolUse, new TokenUsage(10, 5)),
    ];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        _host = new AgentHost(
            new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null),
            services: new AgentServices { Models = new FakeModels(_llm), Delayer = new NoDelay(), Loop = new AgentLoopOptions { ResumeDelays = [] } });
        _agent = _host.RunAsync(_agentCts.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var controller in _controllers)
        {
            await controller.Link.DisposeAsync();
        }

        await _agentCts.CancelAsync();
        try { await _agent; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private async Task<Controller> StartControllerAsync(string clientId)
    {
        var controller = new Controller();
        controller.Link = new DeviceLink(controller.Stores, controller.Ui, $"local-{clientId}", DeviceKind.Local, "本机",
            async ct =>
            {
                var stream = new CuttableStream(await LocalTransport.ConnectAsync(Endpoint, ct));
                controller.Current = stream;
                return stream;
            },
            new ClientHello(clientId, "3.0.0", LocalToken: Token));
        controller.Devices = new DeviceRegistry { Local = controller.Link };
        controller.Actions = new ConversationActions(controller.Devices);
        _controllers.Add(controller);
        controller.Link.Start();
        await WaitUntil(() => controller.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected && d.Data?.State == DataStates.Ready), $"{clientId} connected");
        return controller;
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000, Func<string>? detail = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail(detail is null ? $"Timed out waiting for: {what}" : $"Timed out waiting for: {what}; got: {detail()}");
            }

            await Task.Delay(10);
        }
    }

    private static readonly SendOptionsDto Options = new("sonnet");

    [Fact]
    public async Task A_conversation_through_actions_fills_the_stores_and_approval_by_a_second_controller_wins_only_once()
    {
        var a = await StartControllerAsync("ctrl-a");
        var b = await StartControllerAsync("ctrl-b");
        var project = await a.Link.AddProjectAsync(_workspace);
        var projectPath = (await a.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        var target = Path.Combine(projectPath, "client.txt");
        _llm.Then(CallWrite("c1", target)).Then(Say("All ", "done."));

        // A 新建会话并发送;B 打开同一个会话
        var session = await a.Actions.CreateSessionAsync(project, "sonnet");
        Assert.NotNull(session);
        Assert.True(await b.Actions.OpenSessionAsync(session!.Value));
        var started = await a.Actions.SendAsync(session.Value, "write the file", Options);
        Assert.Equal(SendOutcome.Started, started.Outcome);
        var run = started.Run!.Value;

        // 运行中:A 立刻有一份「运行中」的镜像;审批出现在两个控制端的 Store 里(带执行环境标识)
        Assert.Equal(RunPhase.Running, a.State.Runs[run].Phase);
        await WaitUntil(() => a.State.InteractionsOf(run.DeviceId).Pending.Length == 1 && b.State.InteractionsOf(run.DeviceId).Pending.Length == 1, "approval in both stores");
        var pending = Assert.IsType<ToolPermissionInteractionDto>(a.State.InteractionsOf(run.DeviceId).Pending[0]);
        Assert.Equal(_host.DeviceId, pending.Env.DeviceId);
        await WaitUntil(() => a.State.Runs[run].Tools.Values.Any(t => t.Status == ToolCallStatus.Running && t.Environment?.Environment == "local"), "tool card with environment");

        // 活动索引:该项目有一个运行中的 Run、一个待处理交互(项目栏角标的来源)
        await WaitUntil(() => a.Workspace.ActivityOf(project) is { RunningRuns: 1, PendingInteractions: 1 }, "project activity");

        // 两个控制端同时回答:只有一个 Answered,另一个 AlreadyHandled(带谁、什么时候)
        var response = new ToolPermissionResponseDto(PermissionChoices.AllowOnce);
        var results = await Task.WhenAll(
            a.Actions.RespondAsync(run.DeviceId, pending, response),
            b.Actions.RespondAsync(run.DeviceId, b.State.InteractionsOf(run.DeviceId).Pending[0], response));
        var winner = Assert.Single(results, r => r.Outcome == InteractionOutcome.Answered);
        var loser = Assert.Single(results, r => r.Outcome == InteractionOutcome.AlreadyHandled);
        Assert.Equal(winner.Resolution!.ResolvedBy, loser.Resolution!.ResolvedBy);
        Assert.Equal("answered", loser.Resolution.Status);

        // Run 完成:镜像终态、已收口;消息落进会话镜像(A 和 B 一致);流式文本被完整消息取代;交互清空;项目不再活动
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "run done in A");
        await WaitUntil(() => b.State.Sessions[session.Value].Messages.Length == 4, "messages in B's session mirror"); // 用户输入、含工具调用的助手消息、工具结果、最终回复
        Assert.Equal("from the client test", File.ReadAllText(target));
        var messagesA = a.State.Sessions[session.Value].Messages;
        Assert.Equal(messagesA.Select(m => m.Id), b.State.Sessions[session.Value].Messages.Select(m => m.Id));
        // 「已完成、已收口」可以先由权威广播对账得出(规则 5),这时 A 的事件流可能还没送完最后几个增量和 message_end:等它送完。
        // 送完之后必须是空的——留着就是镜像里有清不掉的流式文本(迟到的旧快照曾经造成过,见 ConversationReducerTests)。
        await WaitUntil(
            () => a.State.Runs[run].Streaming.IsEmpty,
            "streaming text replaced by the complete message",
            detail: () => string.Join(" | ", a.State.Runs[run].Streaming.Select(p => $"{p.Key}={p.Value}")));
        Assert.Equal(ToolCallStatus.Done, a.State.Runs[run].Tools["c1"].Status);
        await WaitUntil(() => a.State.InteractionsOf(run.DeviceId).Pending.IsEmpty && b.State.InteractionsOf(run.DeviceId).Pending.IsEmpty, "interactions cleared");
        await WaitUntil(() => a.Workspace.ActivityOf(project) is { RunningRuns: 0, PendingInteractions: 0 }, "project idle");
        Assert.False(a.Workspace.IsSessionRunning(new SessionSummary(session.Value.SessionId, project, "t", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task A_dropped_connection_marks_the_run_unknown_then_resumes_from_the_cursor_and_converges()
    {
        var a = await StartControllerAsync("ctrl-a");
        var b = await StartControllerAsync("ctrl-b");
        var project = await a.Link.AddProjectAsync(_workspace);
        var projectPath = (await a.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        _llm.Then(CallWrite("c1", Path.Combine(projectPath, "resume.txt"))).Then(Say("finished after reconnect"));

        var session = (await a.Actions.CreateSessionAsync(project, "sonnet"))!.Value;
        var run = (await a.Actions.SendAsync(session, "go", Options)).Run!.Value;
        await WaitUntil(() => a.State.InteractionsOf(run.DeviceId).Pending.Length == 1, "approval pending");
        var seqBefore = a.State.Runs[run].Seq;
        Assert.True(seqBefore > 0);

        // 网络断了(Agent 还在,Run 还在等审批):A 的镜像里这个 Run 是「未知」——不是结束,也不继续算运行中
        a.Current!.Cut();
        await WaitUntil(() => a.Workspace.Devices.Values.All(d => d.Connection != DeviceConnection.Connected), "A sees the disconnect");
        await WaitUntil(() => a.State.Runs[run].Phase == RunPhase.Unknown, "run is unknown while disconnected");
        Assert.True(a.Workspace.ActivityOf(project).Unknown);

        // 断线期间 B 批准,Run 跑完
        await WaitUntil(() => b.State.InteractionsOf(run.DeviceId).Pending.Length == 1, "B's mirror has the pending approval"); // B 的镜像经自己的订阅异步追上,不能假设此刻已到
        var pendingForB = b.State.InteractionsOf(run.DeviceId).Pending[0];
        Assert.Equal(InteractionOutcome.Answered, (await b.Actions.RespondAsync(run.DeviceId, pendingForB, new ToolPermissionResponseDto(PermissionChoices.AllowOnce))).Outcome);

        // A 自动重连:订阅按游标续传,镜像追平——终态、已收口、没有重复的消息、交互清空
        await WaitUntil(() => a.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected), "A reconnected");
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "A's run converged after the resume");
        await WaitUntil(() => a.State.Runs[run].Seq > seqBefore, "the resumed events arrived (the authoritative broadcast may close the run first; the cursor resume then fills in the rest)");
        await WaitUntil(() => a.State.Sessions[session].Messages.Length == 4, "A's session mirror has all messages");
        Assert.Equal(a.State.Sessions[session].Messages.Select(m => m.Id).Distinct().Count(), a.State.Sessions[session].Messages.Length);
        await WaitUntil(() => a.State.InteractionsOf(run.DeviceId).Pending.IsEmpty, "A's interactions cleared");
        Assert.False(a.Workspace.ActivityOf(project).Unknown);
        Assert.Equal(0, a.Workspace.ActivityOf(project).RunningRuns);
    }

    [Fact]
    public async Task Sending_while_a_run_is_active_queues_the_message_and_the_queue_arrives_in_the_store()
    {
        var a = await StartControllerAsync("ctrl-a");
        var project = await a.Link.AddProjectAsync(_workspace);
        var projectPath = (await a.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        _llm.Then(CallWrite("c1", Path.Combine(projectPath, "q.txt"))).Then(Say("first")).Then(Say("second"));

        var session = (await a.Actions.CreateSessionAsync(project, "sonnet"))!.Value;
        var first = await a.Actions.SendAsync(session, "first", Options);
        await WaitUntil(() => a.State.InteractionsOf(first.Run!.Value.DeviceId).Pending.Length == 1, "paused on approval");

        var second = await a.Actions.SendAsync(session, "second", Options);
        Assert.Equal(SendOutcome.Queued, second.Outcome);
        await WaitUntil(() => a.State.Sessions[session].Queue.Items.Length == 1, "queued message in the store");
        var item = a.State.Sessions[session].Queue.Items[0];
        Assert.True(await a.Actions.PromoteQueuedAsync(session, item.Id));
        await WaitUntil(() => a.State.Sessions[session].Queue.Items[0].State == QueueStates.Promoted, "promoted");
        Assert.True(await a.Actions.RemoveQueuedAsync(session, item.Id));
        await WaitUntil(() => a.State.Sessions[session].Queue.Items.Length == 0, "queue empty");
        Assert.False(await a.Actions.RemoveQueuedAsync(session, item.Id)); // 已经没有了:被拒绝,不是错误

        // 收尾:中止这个 Run
        Assert.True(await a.Actions.AbortAsync(first.Run!.Value));
        await WaitUntil(() => a.State.Runs[first.Run!.Value] is { Phase: RunPhase.Aborted, Settled: true }, "aborted");
    }

    // ───────────────────────── 消息操作:编辑 / 编辑后重跑 / 删除一轮 / 分支 ─────────────────────────

    private static string TextOf(MessageDto message) =>
        System.Text.Json.JsonDocument.Parse(message.PartsJson).RootElement.EnumerateArray().First(p => p.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;

    private static MessageActions MessagesOf(Controller c) => new(c.Devices, new SessionActions(c.Stores, c.Devices, c.Actions), c.Actions);

    /// <summary>发一条消息、等这个 Run 跑完;返回会话、用户消息 id 和项目。</summary>
    private async Task<(Controller C, ProjectRef Project, SessionRef Session, string UserId)> OneFinishedTurnAsync(string question, string answer)
    {
        var c = await StartControllerAsync("ctrl-m");
        var project = await c.Link.AddProjectAsync(_workspace);
        _llm.Then(Say(answer));
        var session = (await c.Actions.CreateSessionAsync(project, "sonnet"))!.Value;
        var sent = await c.Actions.SendAsync(session, question, Options);
        Assert.Equal(SendOutcome.Started, sent.Outcome);
        await WaitUntil(() => c.State.Runs[sent.Run!.Value] is { Phase: RunPhase.Done, Settled: true } && c.State.Sessions[session].Messages.Length == 2, "first turn done");
        return (c, project, session, sent.MessageId!);
    }

    [Fact]
    public async Task Compact_through_actions_puts_a_boundary_in_the_mirror_and_a_second_one_has_nothing_to_do()
    {
        // 输入框的 /compact:ConversationActions.CompactAsync → context.compact → 边界经会话主题(history.reset)进 Store
        var (c, _, session, _) = await OneFinishedTurnAsync("remember the protocol decisions", "noted");
        _llm.Then(Say("The summary of the conversation."));

        Assert.Equal(CompactOutcome.Done, await c.Actions.CompactAsync(session, "keep the protocol decisions", Options));
        await WaitUntil(() => c.State.Sessions[session].Messages.Any(m => m.PartsJson.Contains("compact_boundary", StringComparison.Ordinal)), "the boundary reached the mirror");

        // 刚压过、边界之后没有新的对话:设备说没东西可压,不是失败
        Assert.Equal(CompactOutcome.NothingToCompact, await c.Actions.CompactAsync(session, null, Options));
    }

    [Fact]
    public async Task Editing_a_question_rewrites_only_its_text_and_keeps_the_reply()
    {
        var (c, _, session, userId) = await OneFinishedTurnAsync("original question", "the answer");
        var result = await MessagesOf(c).EditAsync(session, userId, "reworded question");
        Assert.True(result.IsOk);

        await WaitUntil(() => c.State.Sessions[session].Messages.FirstOrDefault(m => m.Id == userId) is { } m && TextOf(m) == "reworded question", "history reset reached the mirror");
        var messages = c.State.Sessions[session].Messages;
        Assert.Equal(2, messages.Length);
        Assert.Equal("the answer", TextOf(messages[1]));
    }

    [Fact]
    public async Task Editing_and_rerunning_cuts_the_old_turn_and_runs_the_new_text()
    {
        var (c, _, session, userId) = await OneFinishedTurnAsync("original question", "old answer");
        _llm.Then(Say("new answer"));
        var result = await MessagesOf(c).EditAndResendAsync(session, userId, "sharper question", [], Options);
        Assert.True(result.IsOk);

        await WaitUntil(() => c.State.Sessions[session].Messages is { Length: 2 } m && TextOf(m[0]) == "sharper question" && TextOf(m[1]) == "new answer", "old turn replaced by the new one");
        Assert.DoesNotContain(c.State.Sessions[session].Messages, m => m.Id == userId); // 旧提问和旧回复都没了
    }

    [Fact]
    public async Task Deleting_a_turn_removes_the_question_and_its_replies()
    {
        var (c, _, session, userId) = await OneFinishedTurnAsync("to be deleted", "gone soon");
        Assert.True((await MessagesOf(c).DeleteTurnAsync(session, userId)).IsOk);
        await WaitUntil(() => c.State.Sessions[session].Messages.Length == 0, "turn removed from the mirror");
    }

    [Fact]
    public async Task Deleting_an_unknown_message_is_reported_not_silently_ignored()
    {
        var (c, _, session, _) = await OneFinishedTurnAsync("keep me", "kept");
        var result = await MessagesOf(c).DeleteTurnAsync(session, "no-such-message");
        Assert.False(result.IsOk);
        Assert.Equal(2, c.State.Sessions[session].Messages.Length);
    }

    [Fact]
    public async Task Branching_a_turn_opens_a_new_independent_session_with_the_transcript_up_to_it()
    {
        var (c, project, session, userId) = await OneFinishedTurnAsync("branch from here", "first answer");
        var branch = await MessagesOf(c).BranchAsync(project, session.SessionId, userId);
        Assert.True(branch.IsOk);
        var branchId = branch.Value!;
        Assert.NotEqual(session.SessionId, branchId);

        var branchRef = new SessionRef(session.DeviceId, branchId);
        await WaitUntil(() => c.State.Sessions.TryGetValue(branchRef, out var view) && view.Messages.Length == 2, "branch transcript in the mirror");
        Assert.Equal("branch from here", TextOf(c.State.Sessions[branchRef].Messages[0]));
        Assert.Equal(branchId, c.Stores.Selection.State.SessionId); // 分支被打开
        Assert.Contains(c.Workspace.Sessions, s => s.Id == branchId); // 会话列表刷新过
        Assert.Equal(2, c.State.Sessions[session].Messages.Length);   // 原会话不变
    }

    [Fact]
    public async Task Rewriting_history_while_a_run_is_active_is_refused_as_busy_and_changes_nothing()
    {
        var c = await StartControllerAsync("ctrl-m");
        var project = await c.Link.AddProjectAsync(_workspace);
        var projectPath = (await c.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        _llm.Then(CallWrite("c1", Path.Combine(projectPath, "busy.txt"))).Then(Say("done"));
        var session = (await c.Actions.CreateSessionAsync(project, "sonnet"))!.Value;
        var sent = await c.Actions.SendAsync(session, "write it", Options);
        await WaitUntil(() => c.State.InteractionsOf(sent.Run!.Value.DeviceId).Pending.Length == 1, "run paused on approval");

        var messages = MessagesOf(c);
        var edit = await messages.EditAsync(session, sent.MessageId!, "too late");
        Assert.Equal(ActionOutcome.Conflict, edit.Outcome);
        Assert.Equal(MessageActions.BusyKey, edit.ErrorKey);
        Assert.Equal(MessageActions.BusyKey, (await messages.DeleteTurnAsync(session, sent.MessageId!)).ErrorKey);

        // 收尾
        Assert.True(await c.Actions.AbortAsync(sent.Run!.Value));
        await WaitUntil(() => c.State.Runs[sent.Run!.Value] is { Phase: RunPhase.Aborted, Settled: true }, "aborted");
    }

    [Fact]
    public async Task A_sent_image_comes_back_as_a_ref_in_the_transcript_and_loads_through_the_image_source()
    {
        var c = await StartControllerAsync("ctrl-img");
        var project = await c.Link.AddProjectAsync(_workspace);
        _llm.Then(Say("nice picture"));
        var session = (await c.Actions.CreateSessionAsync(project, "sonnet"))!.Value;

        // 一张真实的小 PNG(不是任意字节:要验证取回来的和上传的逐字节相同)
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var attachments = new AttachmentActions(c.Devices);
        var uploaded = await attachments.UploadAsync(session.DeviceId, session.SessionId, "pic.png", "image/png", png);
        Assert.True(uploaded.IsOk);

        var sent = await c.Actions.SendAsync(session, "what is this?", [InputFile.Attachment(uploaded.Value!.Url, "pic.png", "image/png")], Options);
        Assert.Equal(SendOutcome.Started, sent.Outcome);
        await WaitUntil(() => c.State.Runs[sent.Run!.Value] is { Phase: RunPhase.Done, Settled: true } && c.State.Sessions[session].Messages.Length == 2, "turn done");

        // 转录镜像里的用户消息带着一条 ncw:// 图片引用
        var message = c.State.Sessions[session].Messages.First(m => m.Id == sent.MessageId);
        var image = Assert.Single(MessageImages.Of(message));
        Assert.Equal("image/png", image.Mime);
        Assert.StartsWith("ncw://", image.DataRef);

        // 经图片来源按引用向设备取回字节,与上传的逐字节相同;第二次走缓存
        var source = new AttachmentImageSource(attachments);
        var loaded = await source.LoadAsync(session.DeviceId, image.DataRef);
        Assert.NotNull(loaded);
        Assert.Equal(png, loaded!.Data);
        Assert.Equal("image/png", loaded.Mime);
        Assert.Same(loaded, await source.LoadAsync(session.DeviceId, image.DataRef));

        // 对话视图模型据此给这一行生成缩略图,加载后就绪
        var conversation = new Ncw.Client.ViewModels.ConversationViewModel(c.Stores, c.Actions, images: source);
        c.Stores.Selection.Update(_ => new SelectionState(project, session.SessionId));
        var row = conversation.Rows.OfType<Ncw.Client.ViewModels.UserRowViewModel>().Single();
        var thumb = Assert.Single(row.Images);
        await thumb.EnsureLoadedAsync();
        Assert.True(thumb.IsReady);
        Assert.Equal(png, thumb.Data);
    }

    [Fact]
    public async Task Rerunning_a_question_that_had_an_image_keeps_the_image_readable_and_attached_to_the_new_message()
    {
        var c = await StartControllerAsync("ctrl-rerun-img");
        var project = await c.Link.AddProjectAsync(_workspace);
        _llm.Then(Say("first look"));
        var session = (await c.Actions.CreateSessionAsync(project, "sonnet"))!.Value;
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var attachments = new AttachmentActions(c.Devices);
        var uploaded = await attachments.UploadAsync(session.DeviceId, session.SessionId, "pic.png", "image/png", png);
        var sent = await c.Actions.SendAsync(session, "what is this?", [InputFile.Attachment(uploaded.Value!.Url, "pic.png", "image/png")], Options);
        await WaitUntil(() => c.State.Runs[sent.Run!.Value] is { Phase: RunPhase.Done, Settled: true } && c.State.Sessions[session].Messages.Length == 2, "first turn done");

        // 和界面一样:从原消息还原附件,截断历史后带着它们重发
        var original = c.State.Sessions[session].Messages.First(m => m.Id == sent.MessageId);
        var files = MessageInputs.FilesOf(original);
        Assert.Single(files);
        _llm.Then(Say("second look"));
        Assert.True((await MessagesOf(c).EditAndResendAsync(session, sent.MessageId!, "what is this, in French?", files, Options)).IsOk);

        await WaitUntil(() => c.State.Sessions[session].Messages is { Length: 2 } m && TextOf(m[0]) == "what is this, in French?" && TextOf(m[1]) == "second look", "turn replaced");
        var rerun = c.State.Sessions[session].Messages[0];
        Assert.NotEqual(sent.MessageId, rerun.Id);
        var image = Assert.Single(MessageImages.Of(rerun)); // 新消息仍然带着图
        var loaded = await new AttachmentImageSource(attachments).LoadAsync(session.DeviceId, image.DataRef);
        Assert.NotNull(loaded);                              // 截断没有把附件回收掉:字节还在,逐字节相同
        Assert.Equal(png, loaded!.Data);
    }

    [Fact]
    public async Task Regenerating_replaces_the_reply_and_keeps_the_question()
    {
        var (c, _, session, userId) = await OneFinishedTurnAsync("tell me a joke", "joke number one");
        _llm.Then(Say("joke number two"));
        var question = c.State.Sessions[session].Messages.First(m => m.Id == userId);
        Assert.True((await MessagesOf(c).EditAndResendAsync(session, userId, TextOf(question), MessageInputs.FilesOf(question), Options)).IsOk);

        await WaitUntil(
            () => c.State.Sessions[session].Messages is { Length: 2 } m && TextOf(m[0]) == "tell me a joke" && TextOf(m[1]) == "joke number two",
            "reply replaced, question kept",
            detail: () => string.Join(" | ", c.State.Sessions[session].Messages.Select(TextOf)));
    }

    /// <summary>一轮带工具调用的对话:提问 → 助手(调用 Write)→ 工具回执 → 助手(最终回复)= 4 条消息。</summary>
    private async Task<(Controller C, ProjectRef Project, SessionRef Session, MessageDto[] History)> ToolTurnAsync()
    {
        var c = await StartControllerAsync("ctrl-reply");
        var project = await c.Link.AddProjectAsync(_workspace);
        var projectPath = (await c.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        _llm.Then(CallWrite("c1", Path.Combine(projectPath, "reply.txt"))).Then(Say("all done"));
        var session = (await c.Actions.CreateSessionAsync(project, "sonnet"))!.Value;
        var sent = await c.Actions.SendAsync(session, "write the file", Options);
        await WaitUntil(() => c.State.InteractionsOf(sent.Run!.Value.DeviceId).Pending.Length == 1, "approval pending");
        var pending = c.State.InteractionsOf(sent.Run!.Value.DeviceId).Pending[0];
        await c.Actions.RespondAsync(sent.Run!.Value.DeviceId, pending, new ToolPermissionResponseDto(PermissionChoices.AllowOnce));
        await WaitUntil(() => c.State.Runs[sent.Run!.Value] is { Phase: RunPhase.Done, Settled: true } && c.State.Sessions[session].Messages.Length == 4, "tool turn done");
        return (c, project, session, [.. c.State.Sessions[session].Messages]);
    }

    [Fact]
    public async Task Deleting_a_reply_removes_every_assistant_message_of_the_turn_and_its_receipts_but_keeps_the_question()
    {
        var (c, _, session, history) = await ToolTurnAsync();
        Assert.Equal(["user", "assistant", "user", "assistant"], history.Select(m => m.Role)); // 工具回执是「user」角色、只含 tool_result 块

        // 和界面一样:点最后一段(「all done」),范围取这一轮 AI 的全部回复(第一条到最后一条助手消息)
        var range = MessageTurns.ReplyOf(history, history[3].Id);
        Assert.Equal((history[1].Id, history[3].Id), range);
        Assert.True((await MessagesOf(c).DeleteReplyAsync(session, range!.Value.From, range.Value.To)).IsOk);

        await WaitUntil(() => c.State.Sessions[session].Messages.Length == 1, "reply removed from the mirror");
        var left = Assert.Single(c.State.Sessions[session].Messages);
        Assert.Equal(history[0].Id, left.Id); // 提问留着
        Assert.Equal("write the file", TextOf(left));
    }

    [Fact]
    public async Task Deleting_only_the_last_assistant_message_leaves_a_consistent_tool_pair()
    {
        var (c, _, session, history) = await ToolTurnAsync();
        // 直接删最后一条助手消息(不是界面的做法,但协议允许):留下 提问 + 助手(调用)+ 回执,配对仍然完整
        Assert.True((await MessagesOf(c).DeleteReplyAsync(session, history[3].Id, history[3].Id)).IsOk);
        await WaitUntil(() => c.State.Sessions[session].Messages.Length == 3, "last reply removed");
        Assert.Equal(["user", "assistant", "user"], c.State.Sessions[session].Messages.Select(m => m.Role)); // 提问 + 助手(调用)+ 回执
    }

    [Fact]
    public async Task Branching_from_an_assistant_reply_takes_the_whole_turn_it_belongs_to()
    {
        var (c, project, session, history) = await ToolTurnAsync();
        var question = MessageTurns.QuestionOf(history, history[1].Id); // 点的是调用工具的那一段,不是最后一段
        Assert.Equal(history[0].Id, question);

        var branch = await MessagesOf(c).BranchAsync(project, session.SessionId, question!);
        Assert.True(branch.IsOk);
        var branchRef = new SessionRef(session.DeviceId, branch.Value!);
        await WaitUntil(() => c.State.Sessions.TryGetValue(branchRef, out var view) && view.Messages.Length == 4, "the whole turn is in the branch");
        Assert.Equal(["user", "assistant", "user", "assistant"], c.State.Sessions[branchRef].Messages.Select(m => m.Role)); // 不会在一轮中间被切断
        Assert.Equal(4, c.State.Sessions[session].Messages.Length); // 原会话不变
    }
}
