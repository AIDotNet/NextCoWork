using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests.Support;

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>假上游:按脚本一次一个请求回复,记录每个请求。脚本用完后回一个错误(测试一眼能看出多发了请求)。</summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<Func<LlmRequest, IReadOnlyList<LlmEvent>>> _scripts = new();

    public List<LlmRequest> Requests { get; } = [];

    public FakeLlm Then(IReadOnlyList<LlmEvent> events) => Then(_ => events);

    public FakeLlm Then(Func<LlmRequest, IReadOnlyList<LlmEvent>> script)
    {
        lock (_scripts)
        {
            _scripts.Enqueue(script);
        }

        return this;
    }

    public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        Func<LlmRequest, IReadOnlyList<LlmEvent>>? script;
        lock (_scripts)
        {
            _scripts.TryDequeue(out script);
        }

        var events = script is null
            ? [new StreamError(AgentError.Create(AgentErrorCode.Provider, "no script left", false))]
            : script(request);
        foreach (var ev in events)
        {
            await Task.Yield();
            yield return ev;
        }
    }
}

public static class Up
{
    public static IReadOnlyList<LlmEvent> Calls(params (string Id, string Name, string Json)[] calls)
    {
        List<LlmEvent> events = [new MessageStart("fake-model", "p", "anthropic", "fake-up")];
        var index = 0;
        foreach (var (id, name, json) in calls)
        {
            events.Add(new ToolCallStart(index, id, name));
            events.Add(new ToolCallDelta(index, id, json));
            events.Add(new ToolCallEnd(index, id));
            index++;
        }

        events.Add(new MessageEnd(StopReason.ToolUse, new TokenUsage(10, 5)));
        return events;
    }

    public static IReadOnlyList<LlmEvent> Say(params string[] chunks)
    {
        List<LlmEvent> events = [new MessageStart("fake-model", "p", "anthropic", "fake-up")];
        events.AddRange(chunks.Select(c => (LlmEvent)new TextDelta(0, c)));
        events.Add(new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5), 5, CostInfo.NotMetered));
        return events;
    }
}

public sealed class FakeModels(ILlmClient client) : IModelResolver
{
    public static readonly ModelBinding Sonnet = new("sonnet", "p1", "up", 200_000, 8192);

    public ILlmClient Client { get; } = client;

    public ModelBinding? Resolve(ModelRef model) => model.Alias == Sonnet.Alias ? Sonnet : null;
}

/// <summary>订阅一个主题并把收到的东西存起来(快照、事件帧),给测试等条件用。</summary>
public sealed class Watch<TState>
{
    private readonly object _gate = new();
    private readonly List<EventFrame> _events = [];
    private TState? _snapshot;
    private bool _hasSnapshot;
    private long _snapshotSeq;

    public static async Task<Watch<TState>> OpenAsync(DeviceClient client, TopicDef<TState> topic, long? cursor = null)
    {
        var watch = new Watch<TState>();
        await client.SubscribeAsync(topic, (state, seq) =>
        {
            lock (watch._gate)
            {
                watch._snapshot = state;
                watch._snapshotSeq = seq;
                watch._hasSnapshot = true;
            }
        }, evt =>
        {
            lock (watch._gate)
            {
                watch._events.Add(evt);
            }
        }, CancellationToken.None, cursor);
        return watch;
    }

    public bool HasSnapshot { get { lock (_gate) { return _hasSnapshot; } } }

    public TState Snapshot { get { lock (_gate) { return _snapshot!; } } }

    public long SnapshotSeq { get { lock (_gate) { return _snapshotSeq; } } }

    public IReadOnlyList<EventFrame> Events { get { lock (_gate) { return [.. _events]; } } }

    public IReadOnlyList<T> Decoded<T>(string type) where T : class =>
        [.. Events.Where(e => e.Type == type).Select(e => FrameCodec.DecodePayload(e.Data, PayloadShapes.GetShape<T>()))];

    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    public Task WaitAsync(Func<bool> condition, string what, int timeoutMs = 20_000) => UntilAsync(condition, what, timeoutMs);
}

/// <summary>一个 Run 主题的订阅:事件按 seq 存下来(快照里的事件和之后的增量放在一起)。</summary>
public sealed class RunWatch
{
    private readonly object _gate = new();
    private readonly List<(long Seq, RunEventDto Event)> _events = [];
    private RunSnapshotDto? _snapshot;

    public static async Task<RunWatch> OpenAsync(DeviceClient client, string runId, long? cursor = null)
    {
        var watch = new RunWatch();
        await client.SubscribeAsync(ConversationMethods.RunTopic(runId),
            (snapshot, _) =>
            {
                lock (watch._gate)
                {
                    watch._snapshot = snapshot;
                    foreach (var e in snapshot.Events)
                    {
                        watch._events.Add((e.Seq, e.Event));
                    }
                }
            },
            evt =>
            {
                var dto = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RunEventDto>());
                lock (watch._gate)
                {
                    watch._events.Add((evt.Seq, dto));
                }
            }, CancellationToken.None, cursor);
        if (cursor is null)
        {
            // 首次订阅:快照紧跟在确认之后到达,等它(续传没有快照,调用方等事件)。
            await Watch<Unit>.UntilAsync(() => watch.Snapshot is not null, "run snapshot", 10_000);
        }

        return watch;
    }

    public RunSnapshotDto? Snapshot { get { lock (_gate) { return _snapshot; } } }

    public IReadOnlyList<(long Seq, RunEventDto Event)> Events { get { lock (_gate) { return [.. _events]; } } }

    public IEnumerable<T> Of<T>() where T : RunEventDto => Events.Select(e => e.Event).OfType<T>();

    public long LastSeq => Events.Count == 0 ? 0 : Events.Max(e => e.Seq);

    public Task SettledAsync(int timeoutMs = 30_000) => Watch<Unit>.UntilAsync(() => Of<RunSettledEventDto>().Any(), "run_settled", timeoutMs);
}

/// <summary>真实 Agent(进程内)+ 假上游 + 真实工具:装起来给对话链路的端到端测试用。</summary>
public sealed class ConversationRig : IAsyncLifetime
{
    public const string Token = "conv-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwc-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-conv-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _cts = new();
    private readonly List<DeviceClient> _clients = [];
    private Task _run = Task.CompletedTask;

    private readonly TimeProvider? _clock;

    /// <param name="clock">注入 Host 的时钟(定时任务的到点触发用手动推进的假时钟测,不睡真的时间);缺省 = 系统时钟。</param>
    public ConversationRig(Func<ILlmClient, AgentServices>? services = null, TimeProvider? clock = null)
    {
        _clock = clock;
        Llm = new FakeLlm();
        Services = services?.Invoke(Llm) ?? new AgentServices
        {
            Models = new FakeModels(Llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
        };
    }

    public FakeLlm Llm { get; }

    public AgentServices Services { get; }

    public AgentHost Host { get; private set; } = null!;

    public AgentOptions Options => new(AgentMode.Local, _root, LocalTransport.DefaultEndpoint(_root), Token, null);

    public string WorkspaceDir => _workspace;

    public string DataRoot => _root;

    /// <summary>本机连接,线上收到的每个字节都被记下来(断言凭据不出现在任何帧里)。</summary>
    public async Task<(DeviceClient Client, TapStream Tap)> ConnectTappedAsync(string clientId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var tap = new TapStream(await LocalTransport.ConnectAsync(Options.Endpoint, CancellationToken.None));
                var client = await DeviceClient.ConnectAsync(tap, new ClientHello(clientId, "3.0.0", LocalToken: Token));
                lock (_clients)
                {
                    _clients.Add(client);
                }

                return (client, tap);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        Host = new AgentHost(Options, _clock, pairingFailureDelay: TimeSpan.Zero, services: Services);
        _run = Host.RunAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    /// <summary>停 Agent 但不先断开控制端:会话正在等下一帧时被取消(停机收尾必须照样跑完)。返回时 <c>RunAsync</c> 已结束。</summary>
    public async Task StopWhileConnectedAsync()
    {
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync(); } catch (Exception) { }
        }

        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir))
            {
                // Windows 上 .git/objects 里的文件是只读的(UnauthorizedAccessException);临时目录清不掉不影响测试结论
                try { Directory.Delete(dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<DeviceClient> ConnectAsync(string clientId = "ctrl-a")
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(Options.Endpoint, CancellationToken.None);
                var client = await DeviceClient.ConnectAsync(stream, new ClientHello(clientId, "3.0.0", LocalToken: Token));
                lock (_clients)
                {
                    _clients.Add(client);
                }

                return client;
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    /// <summary>等到设备数据就绪(接管 / 建库完成),对话方法才不会返回 unavailable。</summary>
    public async Task<ProjectDto> AddProjectAsync(DeviceClient client)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_workspace, null));
            }
            catch (ProtocolException e) when (e.Retryable && attempt < 100)
            {
                await Task.Delay(50);
            }
        }
    }

    public Task<SessionDto> CreateSessionAsync(DeviceClient client, ProjectDto project, string model = "sonnet") =>
        client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: model));

    public Task<RunsStartResult> StartAsync(DeviceClient client, string sessionId, string text, string? runId = null, string permission = "ask", string? idem = null) =>
        client.CallAsync(
            ConversationMethods.RunsStart,
            new RunsStartParams(runId ?? Guid.NewGuid().ToString("N"), sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto("sonnet", PermissionMode: permission)),
            idem: idem);

    public static string WriteJson(string path, string content = "hi") =>
        new System.Text.Json.Nodes.JsonObject { ["file_path"] = path, ["content"] = content }.ToJsonString();
}
