using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Core.Domain;

namespace Ncw.Agent.Runs.Tests.Support;

public sealed class FakeInteractions : IInteractionView
{
    private readonly Dictionary<RunId, int> _counts = [];

    public void Set(RunId run, int count)
    {
        lock (_counts)
        {
            _counts[run] = count;
        }
    }

    public int PendingCount(RunId runId)
    {
        lock (_counts)
        {
            return _counts.GetValueOrDefault(runId);
        }
    }
}

/// <summary>包一层真的 kv 存档,按需注入失败。</summary>
public sealed class FlakyQueueStore(IQueueStore inner) : IQueueStore
{
    public bool FailSave { get; set; }

    public bool FailLoad { get; set; }

    public int Saves { get; private set; }

    public IReadOnlyList<QueueItem>? Load(string sessionId) => FailLoad ? throw new IOException("disk read failed") : inner.Load(sessionId);

    public void Save(string sessionId, IReadOnlyList<QueueItem> items)
    {
        if (FailSave)
        {
            throw new IOException("disk full");
        }

        Saves++;
        inner.Save(sessionId, items);
    }
}

public sealed class RecordingExtension : IRunExtension
{
    public List<string> Calls { get; } = [];

    public Action<RunHandle>? OnCreatedHook { get; set; }

    public void OnCreated(RunHandle run)
    {
        lock (Calls)
        {
            Calls.Add($"created:{run.RunId}");
        }

        OnCreatedHook?.Invoke(run);
    }

    public ToolContext EnrichToolContext(RunHandle run, ToolContext context)
    {
        lock (Calls)
        {
            Calls.Add($"enrich:{run.RunId}");
        }

        return context;
    }

    public void OnAbortRequested(RunHandle run)
    {
        lock (Calls)
        {
            Calls.Add($"abort:{run.RunId}");
        }
    }

    public void OnEnded(RunHandle run, RunStatus status, AgentError? error)
    {
        lock (Calls)
        {
            Calls.Add($"ended:{run.RunId}:{status}");
        }
    }

    public void OnSettled(RunHandle run)
    {
        lock (Calls)
        {
            Calls.Add($"settled:{run.RunId}");
        }
    }
}

public sealed class Ext(string id, int order, Action<IExtensionRegistry> register) : IAgentExtension
{
    public string Id => id;

    public int Order => order;

    public void Register(IExtensionRegistry registry) => register(registry);
}

public sealed class RigOptions
{
    public bool ManualClock { get; set; }

    public Func<Rig, IRunDriver>? Driver { get; set; }

    public IReadOnlyList<IAgentExtension> SessionExtensions { get; set; } = [];

    public IReadOnlyList<IRunExtension> RunExtensions { get; set; } = [];

    public bool Shells { get; set; }

    public Func<RunRegistryOptions, RunRegistryOptions>? Registry { get; set; }
}

/// <summary>真 Storage(临时目录里的 SQLite 文件)+ 假上游 + 假工具 + 本机执行环境,装起一个真的 <see cref="RunRegistry"/>。</summary>
public sealed class Rig : IDisposable
{
    public const string Workspace = "ws";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwruns-{Guid.NewGuid():N}");
    private readonly RigOptions _options;
    private int _runs;

    public Rig(Action<RigOptions>? configure = null)
    {
        _options = new RigOptions();
        configure?.Invoke(_options);
        Directory.CreateDirectory(_root);
        WorkRoot = Path.Combine(_root, "ws");
        Directory.CreateDirectory(WorkRoot);
        Db = AgentDatabase.Open(Path.Combine(_root, "db"));
        Repo = new SessionRepository(Db);
        Inputs = new SessionInputStore(Db);
        Store = new FlakyQueueStore(new SessionInputQueueStore(Inputs));
        Env = new LocalEnvironment("dev");
        Manual = _options.ManualClock ? new ManualClock() : null;
        Clock = Manual ?? TimeProvider.System;
        Shells = _options.Shells ? new BackgroundShells() : null;
        Sessions = new SessionDirectory(NewSession);
        Factory = new TestFactory();
        Interactions = new FakeInteractions();
        RegistryOptions = _options.Registry?.Invoke(new RunRegistryOptions()) ?? new RunRegistryOptions();
        Registry = BuildRegistry();
    }

    public RunRegistryOptions RegistryOptions { get; }

    private RunRegistry BuildRegistry()
    {
        var registry = new RunRegistry(new RunRegistryDeps
        {
            DeviceId = "dev-1",
            Sessions = Repo,
            Driver = _options.Driver?.Invoke(this) ?? new SessionRunDriver(Sessions),
            Requests = Factory,
            Queue = Store,
            SessionDirectory = Sessions,
            Ids = new SeqIds($"q{++_generation}-"),
            Clock = Clock,
            Log = Log,
            Options = RegistryOptions,
            Extensions = _options.RunExtensions,
            Interactions = Interactions,
            Shells = Shells,
        });
        registry.ActiveRunsChanged += index =>
        {
            lock (Broadcasts)
            {
                Broadcasts.Add(index);
            }
        };
        registry.QueueChanged += update =>
        {
            lock (QueueUpdates)
            {
                QueueUpdates.Add(update);
            }
        };
        return registry;
    }

    private int _generation;

    /// <summary>「重启」:丢掉注册表(内存状态),在同一个库上装一个新的。</summary>
    public RunRegistry Restart()
    {
        Registry.Dispose();
        Registry = BuildRegistry();
        return Registry;
    }

    public static readonly ModelBinding Alias = new("claude-sonnet-4", "p1", "up", 200_000, 8192);

    public string WorkRoot { get; }

    public AgentDatabase Db { get; }

    public SessionRepository Repo { get; }

    public SessionInputStore Inputs { get; }

    public FlakyQueueStore Store { get; }

    public LocalEnvironment Env { get; }

    public FakeLlm Llm { get; } = new();

    public List<IAgentTool> Tools { get; } = [];

    public ManualClock? Manual { get; }

    public TimeProvider Clock { get; }

    public BackgroundShells? Shells { get; }

    public SessionDirectory Sessions { get; }

    public TestFactory Factory { get; }

    public FakeInteractions Interactions { get; }

    public CollectingLog Log { get; } = new();

    public RunRegistry Registry { get; private set; } = null!;

    public List<ActiveRunIndex> Broadcasts { get; } = [];

    public List<QueueUpdate> QueueUpdates { get; } = [];

    public AgentLoopOptions LoopOptions { get; set; } = new() { ResumeDelays = [TimeSpan.Zero, TimeSpan.Zero] };

    public ModelRef Model => ModelRef.Unpinned(Alias.Alias);

    private AgentSession NewSession(string sessionId, string workspaceId) => new(sessionId, workspaceId, new AgentSessionDeps
    {
        Sessions = Repo,
        Llm = Llm,
        Ids = new SeqIds($"m{sessionId}-"),
        Clock = new FakeClock(),
        Delayer = new NoDelay(),
        Environment = Env,
        WorkspaceRoot = EnvPath.Posix(WorkRoot),
        Extensions = _options.SessionExtensions,
        Tools = new StaticToolSource(Tools),
        ResolveModel = m => m.Alias == Alias.Alias ? Alias : null,
        LoopOptions = LoopOptions,
        Diagnostics = Log,
    });

    public string NewSessionRow(string id = "s1")
    {
        Repo.Create(new SessionCreate(Workspace, id));
        return id;
    }

    public RunLaunch Make(string sessionId, string text, string? runId = null, string? messageId = null, RunOrigin origin = RunOrigin.User, IReadOnlyList<RunSubscription>? subscriptions = null, SendOptions? options = null)
    {
        var n = Interlocked.Increment(ref _runs);
        var id = new RunId(runId ?? $"run-{n}");
        var request = new RunRequest(id, text.Length == 0 ? [] : [Msg.User(messageId ?? $"u{n}", text)], Model);
        return new RunLaunch(sessionId, Workspace, request)
        {
            Origin = origin,
            Subscriptions = subscriptions ?? [],
            Options = options ?? Opts.Default,
        };
    }

    public RunHandle Start(string sessionId, string text, string? runId = null, string? messageId = null, IReadOnlyList<RunSubscription>? subscriptions = null)
    {
        var result = Registry.Launch(Make(sessionId, text, runId, messageId, subscriptions: subscriptions));
        var handle = Assert.IsType<LaunchResult.Started>(result).Handle;
        lock (_startedRuns)
        {
            _startedRuns.Add(handle.RunId);
        }

        return handle;
    }

    /// <summary>跑一个 Run 到底(settle 之后,含它触发的续跑之前的收尾)。</summary>
    public async Task<RunHandle> RunAsync(string sessionId, string text, string? runId = null, string? messageId = null)
    {
        var handle = Start(sessionId, text, runId, messageId);
        await Settled(handle);
        return handle;
    }

    public static async Task Settled(RunHandle handle)
    {
        await Wait.UntilAsync(() => handle.IsSettled, what: $"run {handle.RunId} to settle");
        await handle.Completion!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 等到会话里不再有任何东西占着,而且<b>所有已知 Run 的收尾都彻底跑完</b>(包括 settle 之后续跑排队消息的那一步):互斥一放开,
    /// 一个 Run 的 settle 还没做完 —— 它随后可能还会续跑队列里的东西,和这之后才发生的用户动作交错。
    /// </summary>
    public async Task IdleAsync(string sessionId)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            List<RunId> ids;
            lock (Factory.Created)
            {
                ids = [.. Factory.Created.Select(t => t.RunId)];
            }

            lock (_startedRuns)
            {
                ids.AddRange(_startedRuns);
            }

            ids = [.. ids.Distinct()];
            var open = ids.Select(Registry.Get).OfType<RunHandle>().Where(h => h.Completion is { IsCompleted: false } || h.Completion is null).ToList();
            if (open.Count == 0 && !Registry.IsSessionBusy(sessionId))
            {
                return;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"timed out waiting for session {sessionId} to go idle");
            }

            await Task.Delay(5);
        }
    }

    private readonly List<RunId> _startedRuns = [];

    public List<(string Id, string Role, string Text)> Stored(string sessionId) =>
        [.. Repo.History(sessionId).Select(r => (r.Id, r.Role, TextOf(r.PartsJson)))];

    private static string TextOf(string partsJson)
    {
        using var doc = JsonDocument.Parse(partsJson);
        return string.Concat(doc.RootElement.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : ""));
    }

    public void Dispose()
    {
        Registry.Dispose();
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>不认取消令牌的驱动(测看门狗):发一个 run_start 之后一直挂着,直到被放行。</summary>
public sealed class StuckDriver : IRunDriver
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Late { get; private set; }

    public int Lateness { get; private set; }

    public void Release() => _release.TrySetResult();

    public async Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct)
    {
        await sink.EmitAsync(new RunStartEvent(run.RunId, run.SessionId, request.Model), CancellationToken.None);
        await _release.Task.ConfigureAwait(false);
        Late = true;
        // 醒过来之后继续「写」:这些必须被丢弃。
        await sink.EmitAsync(new TurnStartEvent(99), CancellationToken.None);
        await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
        Lateness++;
        return new RunOutcome(run.RunId, RunStatus.Done, null, new TokenUsage(), CostInfo.NotMetered, 0);
    }
}

/// <summary>脚本化的驱动:按调用交给委托(测事件 / 订阅 / 广播,不需要真的会话)。</summary>
public sealed class ScriptedDriver(Func<RunHandle, RunRequest, IRunSink, CancellationToken, Task<RunOutcome>> run) : IRunDriver
{
    public Task<RunOutcome> RunAsync(RunHandle handle, RunRequest request, IRunSink sink, CancellationToken ct) => run(handle, request, sink, ct);

    public static RunOutcome Done(RunHandle run, RunStatus status = RunStatus.Done) => new(run.RunId, status, null, new TokenUsage(), CostInfo.NotMetered, 0);

    /// <summary>run_start → 若干事件 → run_end,然后返回。</summary>
    public static ScriptedDriver Simple(Func<RunHandle, Task>? during = null, RunStatus status = RunStatus.Done) => new(async (handle, request, sink, ct) =>
    {
        await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
        if (during is not null)
        {
            await during(handle);
        }

        await sink.EmitAsync(new RunEndEvent(status), CancellationToken.None);
        await sink.EmitAsync(new RunSettledEvent(handle.RunId, status), CancellationToken.None);
        return Done(handle, status);
    });
}

/// <summary>
/// 回声驱动:不依赖上游。run_start → 每条输入消息的 message_start / message_end → (可选)在「回合边界」取一次引导消息并同样回执 → run_end。
/// 用来测排队 / 内部输入这类只关心「消息有没有被消费」的场景。
/// </summary>
public sealed class EchoDriver(IRunRegistryAccess access, Func<RunHandle, bool>? takesSteering = null, Func<RunHandle, Task>? during = null, Func<RunHandle, RunStatus>? status = null) : IRunDriver
{
    public List<string> Inputs { get; } = [];

    public async Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct)
    {
        await sink.EmitAsync(new RunStartEvent(run.RunId, run.SessionId, request.Model), CancellationToken.None);
        foreach (var message in request.Input)
        {
            lock (Inputs)
            {
                Inputs.Add($"{run.RunId}:{string.Concat(message.Parts.OfType<TextPart>().Select(p => p.Text))}");
            }

            await Commit(run, sink, message);
        }

        if (during is not null)
        {
            await during(run);
        }

        if ((takesSteering?.Invoke(run) ?? true) && request.TakeSteering is { } take)
        {
            foreach (var message in take())
            {
                lock (Inputs)
                {
                    Inputs.Add($"{run.RunId}:{string.Concat(message.Parts.OfType<TextPart>().Select(p => p.Text))}");
                }

                await Commit(run, sink, message);
            }
        }

        var outcome = status?.Invoke(run) ?? RunStatus.Done;
        await sink.EmitAsync(new RunEndEvent(outcome), CancellationToken.None);
        return ScriptedDriver.Done(run, outcome);
    }

    private async Task Commit(RunHandle run, IRunSink sink, Message message)
    {
        access.Commit(run.SessionId, run.RunId, message);
        await sink.EmitAsync(new MessageStartEvent(message.Id, Role.User), CancellationToken.None);
        await sink.EmitAsync(new MessageEndEvent(message), CancellationToken.None);
    }
}

/// <summary>驱动要落库时用(同一个事务里写消费回执,和会话层的提交一样)。</summary>
public interface IRunRegistryAccess
{
    void Commit(string sessionId, RunId run, Message message);
}

public sealed class RepoAccess(SessionRepository repo) : IRunRegistryAccess
{
    public void Commit(string sessionId, RunId run, Message message)
    {
        var text = string.Concat(message.Parts.OfType<TextPart>().Select(p => p.Text));
        var json = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = text }).ToJsonString();
        repo.Commit(sessionId, new MessageRecord(message.Id.Value, "user", json, message.CreatedAt.ToUnixTimeMilliseconds(), 1, message.Internal), run.Value);
    }
}

