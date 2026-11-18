using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests.Support;

public delegate IAsyncEnumerable<LlmEvent> Script(LlmRequest request, CancellationToken ct);

/// <summary>按会话分派脚本的假上游(父会话 / 子会话 / 目标判定器并发调用上游时,按「谁先到」消费一条队列不可靠);判定器的请求走单独一条。</summary>
public sealed class ScriptedLlm : ILlmClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<Script>> _bySession = new(StringComparer.Ordinal);
    private readonly Queue<Script> _judge = new();
    private readonly List<(string SessionId, LlmRequest Request)> _requests = [];

    public ScriptedLlm On(string sessionId, params Script[] scripts)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var queue))
            {
                _bySession[sessionId] = queue = new Queue<Script>();
            }

            foreach (var s in scripts)
            {
                queue.Enqueue(s);
            }
        }

        return this;
    }

    /// <summary>
    /// 这个会话的子代理会话(<c>&lt;父会话&gt;:sub:…</c>)的脚本。控制端自己生成 RunId,子会话 id 事先不知道,所以按前缀认:
    /// 各个子会话按「谁先到」从同一条队列里取(测试里它们共用一道门,先后无所谓)。
    /// </summary>
    public ScriptedLlm OnChildren(string parentSessionId, params Script[] scripts) => On(parentSessionId + ":sub:", scripts);

    public ScriptedLlm Judge(params Script[] scripts)
    {
        lock (_gate)
        {
            foreach (var s in scripts)
            {
                _judge.Enqueue(s);
            }
        }

        return this;
    }

    public int CountOf(string sessionId)
    {
        lock (_gate)
        {
            return _requests.Count(r => r.SessionId == sessionId && r.Request.System != GoalPrompts.EvaluatorSystem);
        }
    }

    // 精确的会话优先;否则按前缀(子会话)。调用方持有 _gate。
    private Queue<Script>? Queue(string session)
    {
        if (_bySession.TryGetValue(session, out var exact) && exact.Count > 0)
        {
            return exact;
        }

        return _bySession.FirstOrDefault(kv => kv.Key.EndsWith(":sub:", StringComparison.Ordinal) && session.StartsWith(kv.Key, StringComparison.Ordinal) && kv.Value.Count > 0).Value;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Script script;
        lock (_gate)
        {
            var session = context.SessionId ?? "";
            _requests.Add((session, request));
            script = request.System == GoalPrompts.EvaluatorSystem
                ? (_judge.Count > 0 ? _judge.Dequeue() : S.Verdict(false, "no scripted verdict"))
                : (Queue(session) is { Count: > 0 } queue ? queue.Dequeue() : S.Say("ok"));
        }

        return script(request, ct);
    }
}

public static class S
{
    public static readonly TokenUsage Usage = new(10, 5);

    public static Script Events(params LlmEvent[] events) => (_, _) => Gen(events);

    private static async IAsyncEnumerable<LlmEvent> Gen(IEnumerable<LlmEvent> events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    public static Script Say(string text) => Events(
        new MessageStart("m", "p", "anthropic", "up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, Usage, 5, CostInfo.NotMetered));

    public static Script Calls(params (string Id, string Name, string Json)[] calls)
    {
        var events = new List<LlmEvent> { new MessageStart("m", "p", "anthropic", "up") };
        var index = 0;
        foreach (var (id, name, json) in calls)
        {
            events.Add(new ToolCallStart(index, id, name));
            events.Add(new ToolCallDelta(index, id, json));
            events.Add(new ToolCallEnd(index, id));
            index++;
        }

        events.Add(new MessageEnd(StopReason.ToolUse, Usage));
        return Events([.. events]);
    }

    public static Script Call(string id, string name, string json) => Calls((id, name, json));

    public static Script Gated(Task gate, Script then) => (req, ct) => GatedGen(gate, then, req, ct);

    private static async IAsyncEnumerable<LlmEvent> GatedGen(Task gate, Script then, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("m", "p", "anthropic", "up");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        await foreach (var ev in then(req, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    public static Script Hang() => (_, ct) => HangGen(ct);

    private static async IAsyncEnumerable<LlmEvent> HangGen([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("m", "p", "anthropic", "up");
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    public static Script Verdict(bool ok, string reason) =>
        Say(new JsonObject { ["ok"] = ok, ["reason"] = reason }.ToJsonString());

    public static string TaskArgs(string prompt = "look into it", string description = "Look into it", bool background = false) =>
        new JsonObject { ["description"] = description, ["prompt"] = prompt, ["subagent_type"] = "general-purpose", ["run_in_background"] = background }.ToJsonString();

    public static string ChildSession(string parentSession, string parentRunId, int n = 1) =>
        SubagentIds.ChildSession(parentSession, SubagentIds.ChildRun(new RunId(parentRunId), n));
}

/// <summary>可以从外面掐断的连接(模拟网络断开,Agent 进程不受影响)。</summary>
public sealed class CuttableStream(Stream inner) : Stream
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

public sealed class LockedDispatcher : IUiDispatcher
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

/// <summary>一个控制端:自己的 Store、自己的连接(可以被掐断)、全部 Actions。</summary>
public sealed class Controller
{
    public ClientStores Stores { get; } = new();

    public LockedDispatcher Ui { get; } = new();

    public DeviceLink Link { get; set; } = null!;

    public DeviceRegistry Devices { get; set; } = null!;

    public ConversationActions Conversations { get; set; } = null!;

    public SubagentActions Subagents { get; set; } = null!;

    public GoalActions Goals { get; set; } = null!;

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

/// <summary>真实 Agent(进程内)+ 假上游 + 真实 DeviceLink / Store / Actions:子代理 / 计划 / 待办 / 目标的端到端台架。</summary>
public sealed class OrchestrationRig : IAsyncLifetime
{
    public const string Token = "integration-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwo-{Guid.NewGuid():N}"[..16]);
    private readonly CancellationTokenSource _agentCts = new();
    private readonly List<Controller> _controllers = [];
    private Task _agent = Task.CompletedTask;

    public string Workspace { get; } = Path.Combine(Path.GetTempPath(), $"ncw-orch-{Guid.NewGuid():N}");

    public ScriptedLlm Llm { get; } = new();

    public AgentHost Host { get; private set; } = null!;

    public string DataRoot => _root;

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    public static readonly SendOptionsDto Options = new("sonnet", PermissionMode: "auto");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Workspace);
        Host = new AgentHost(
            new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null),
            services: new AgentServices { Models = new FakeModels(Llm), Delayer = new NoDelay(), Loop = new AgentLoopOptions { ResumeDelays = [] } });
        _agent = Host.RunAsync(_agentCts.Token);
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
        foreach (var dir in new[] { _root, Workspace })
        {
            if (Directory.Exists(dir))
            {
                // Windows 上 .git/objects 里的文件是只读的(UnauthorizedAccessException);临时目录清不掉不影响测试结论
                try { Directory.Delete(dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void SetSettings(string json)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, AgentDatabase.FileName)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (id, json) VALUES (1, $j) ON CONFLICT (id) DO UPDATE SET json = excluded.json";
        command.Parameters.AddWithValue("$j", json);
        command.ExecuteNonQuery();
    }

    public async Task<Controller> StartControllerAsync(string clientId)
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
        controller.Conversations = new ConversationActions(controller.Devices);
        controller.Subagents = new SubagentActions(controller.Devices);
        controller.Goals = new GoalActions(controller.Devices);
        _controllers.Add(controller);
        controller.Link.Start();
        await WaitUntil(() => controller.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected && d.Data?.State == DataStates.Ready), $"{clientId} connected");
        return controller;
    }

    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000)
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
}

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

public sealed class FakeModels(ILlmClient client) : IModelResolver
{
    public ILlmClient Client { get; } = client;

    public ModelBinding? Resolve(ModelRef model) => model.Alias == "sonnet" ? new ModelBinding("sonnet", "p1", "up", 200_000, 8192) : null;
}
