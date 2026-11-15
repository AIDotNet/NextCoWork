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
using Ncw.Client.Stores;
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

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
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
        Assert.Empty(a.State.Runs[run].Streaming);
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
}
