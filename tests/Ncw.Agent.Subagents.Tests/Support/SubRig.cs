using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Core.Domain;

namespace Ncw.Agent.Subagents.Tests.Support;

public sealed class GateView(InteractionGate gate) : IInteractionView
{
    public int PendingCount(RunId runId) => gate.PendingCount(runId);
}

/// <summary>装配方(Host)那一半的替身:顶层 Run 登记权限状态与交互作用域、给工具上下文套上执行环境;子 Run 的权限登记归 <see cref="SubagentService"/>。</summary>
public sealed class HostLikeRunExtension(SubRig rig) : IRunExtension
{
    private readonly Dictionary<RunId, ToolRuntime> _runtimes = [];

    public void OnCreated(RunHandle run)
    {
        var runtime = new ToolRuntime { Environment = rig.Env, Root = run.WorkspaceRoot ?? LocalEnvironment.PathOf(rig.WorkRoot), Shells = rig.Shells };
        lock (_runtimes)
        {
            _runtimes[run.RunId] = runtime;
        }

        if (run.Origin == RunOrigin.Subagent)
        {
            return;
        }

        var interactions = rig.Gate.OpenRun(run.RunId, run.Depth);
        var mode = PermissionModes.TryParse(run.LivePermissionMode, out var parsed) ? parsed : PermissionMode.Ask;
        rig.Perms.Register(new PermissionRunState(run.RunId, run.SessionId, rig.Env, interactions, mode)
        {
            Root = runtime.Root,
            WorkspaceId = run.WorkspaceId,
            WebSearch = true,
        });
    }

    public ToolContext EnrichToolContext(RunHandle run, ToolContext context)
    {
        lock (_runtimes)
        {
            return _runtimes.TryGetValue(run.RunId, out var runtime) ? ExecutionToolContext.Enricher(runtime)(context) : context;
        }
    }

    public void OnEnded(RunHandle run, RunStatus status, AgentError? error)
    {
        if (run.Origin == RunOrigin.Subagent)
        {
            return;
        }

        rig.Perms.Get(run.RunId)?.Interactions.End(status);
        rig.Perms.Remove(run.RunId);
    }
}

/// <summary>可编程的路由器替身:哪些「别名 + 供应商」还指得到绑定、思考档位怎么降级。</summary>
public sealed class FakeModels : ISubagentModels
{
    public Func<Ncw.Agent.Catalog.ModelSelection, bool> ExistsFn { get; set; } = static _ => true;

    public Func<ThinkingLevel, Ncw.Agent.Catalog.ModelSelection, ThinkingLevel> NormalizeFn { get; set; } = static (level, _) => level;

    public bool Exists(Ncw.Agent.Catalog.ModelSelection selection) => ExistsFn(selection);

    public ThinkingLevel NormalizeThinking(ThinkingLevel wanted, Ncw.Agent.Catalog.ModelSelection selection) => NormalizeFn(wanted, selection);
}

/// <summary>
/// 子代理的端到端台架:真 Storage(临时目录里的 SQLite 文件)+ 假上游 + 真实工具(Read / Bash,跑在本机环境上)+ 真实的 <c>RunRegistry</c> / 权限扩展 / 交互闸门 /
/// <see cref="SubagentService"/> / <c>Task</c> 工具。上游按<b>会话</b>分派脚本(多个会话并发调用上游时,按「谁先到」消费脚本队列不可靠)。
/// </summary>
public sealed class SubRig : IDisposable
{
    public const string Workspace = "ws";
    public const string Alias = "claude-sonnet-4";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwsub-{Guid.NewGuid():N}");
    private readonly Dictionary<string, Queue<Script>> _scripts = [];
    private readonly Dictionary<string, List<LlmRequest>> _requests = [];

    public SubRig(Action<SubRig>? configure = null)
    {
        Directory.CreateDirectory(_root);
        Env = new LocalEnvironment("dev");
        WorkRoot = Env.Fs.RealPathAsync(LocalEnvironment.PathOf(Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName)).AsTask().GetAwaiter().GetResult().Value;
        AgentsDir = Path.Combine(_root, "global", "agents");
        Directory.CreateDirectory(AgentsDir);
        Db = AgentDatabase.Open(Path.Combine(_root, "db"));
        Repo = new SessionRepository(Db);
        Store = new SessionInputQueueStore(new SessionInputStore(Db));
        Shells = new BackgroundShells();
        Gate = new InteractionGate(new SeqIds("i"), log: Log);
        Catalog = new AgentCatalog(new ScanSource(this), new MemoryAgentSwitchStore(), Log);
        Factory = new TestFactory();
        Settings = new FixedSubagentSettings();
        Service = new SubagentService(new SubagentServiceDeps
        {
            Sessions = Repo,
            Requests = Factory,
            Agents = Catalog,
            Settings = Settings,
            Models = Models,
            PermissionRuns = Perms,
            Gate = Gate,
            Log = Log,
            ResolveProject = _ => (Env, LocalEnvironment.PathOf(WorkRoot)),
        });
        Llm.Route = Route;
        Sessions = new SessionDirectory(NewSession);
        configure?.Invoke(this);
        Registry = new RunRegistry(new RunRegistryDeps
        {
            DeviceId = "dev-1",
            Sessions = Repo,
            Driver = new SessionRunDriver(Sessions),
            Requests = Factory,
            Queue = Store,
            SessionDirectory = Sessions,
            Ids = new SeqIds("q"),
            Log = Log,
            Extensions = [Service, new HostLikeRunExtension(this)],
            Interactions = new GateView(Gate),
            Shells = Shells,
        });
        Service.Bind(Registry);
        Registry.ActiveRunsChanged += index =>
        {
            lock (Broadcasts)
            {
                Broadcasts.Add(index);
            }
        };
    }

    private sealed class ScanSource(SubRig rig) : IAgentScanSource
    {
        public ValueTask<AgentScanInput> InputForAsync(string workspaceId, CancellationToken ct) =>
            ValueTask.FromResult(new AgentScanInput(rig.Env, LocalEnvironment.PathOf(rig.AgentsDir), null, null));
    }

    public string WorkRoot { get; }

    public string AgentsDir { get; }

    public LocalEnvironment Env { get; }

    public AgentDatabase Db { get; }

    public SessionRepository Repo { get; }

    public SessionInputQueueStore Store { get; }

    public BackgroundShells Shells { get; }

    public InteractionGate Gate { get; }

    public PermissionRuns Perms { get; } = new();

    public AgentCatalog Catalog { get; }

    public TestFactory Factory { get; }

    public FixedSubagentSettings Settings { get; }

    public FakeModels Models { get; } = new();

    public SubagentService Service { get; }

    public FakeLlm Llm { get; } = new();

    public CollectingLog Log { get; } = new();

    public SessionDirectory Sessions { get; }

    public RunRegistry Registry { get; private set; } = null!;

    public List<ActiveRunIndex> Broadcasts { get; } = [];

    public List<IAgentTool> ExtraTools { get; } = [];

    public ModelRef Model => ModelRef.Unpinned(Alias);

    private static readonly ModelBinding Binding = new(Alias, "p1", "up", 200_000, 8192);

    /// <summary>每个会话一个脚本队列;用完了就回 "ok"。</summary>
    public void Script(string sessionId, params Script[] scripts)
    {
        lock (_scripts)
        {
            if (!_scripts.TryGetValue(sessionId, out var queue))
            {
                _scripts[sessionId] = queue = new Queue<Script>();
            }

            foreach (var script in scripts)
            {
                queue.Enqueue(script);
            }
        }
    }

    /// <summary>这个会话发过的上游请求(按顺序)。</summary>
    public IReadOnlyList<LlmRequest> RequestsOf(string sessionId)
    {
        lock (_scripts)
        {
            return _requests.TryGetValue(sessionId, out var list) ? [.. list] : [];
        }
    }

    private Script? Route(RequestContext context)
    {
        var session = context.SessionId ?? "";
        return (request, model, ctx, ct) =>
        {
            Script script;
            lock (_scripts)
            {
                if (!_requests.TryGetValue(session, out var list))
                {
                    _requests[session] = list = [];
                }

                list.Add(request);
                script = _scripts.TryGetValue(session, out var queue) && queue.Count > 0 ? queue.Dequeue() : Up.Text("ok");
            }

            return script(request, model, ctx, ct);
        };
    }

    private AgentSession NewSession(string sessionId, string workspaceId) => new(sessionId, workspaceId, new AgentSessionDeps
    {
        Sessions = Repo,
        Llm = Llm,
        Ids = new SeqIds($"m{sessionId.Replace(':', '_')}-"),
        Clock = new FakeClock(),
        Delayer = new NoDelay(),
        Environment = Env,
        WorkspaceRoot = LocalEnvironment.PathOf(WorkRoot),
        Extensions =
        [
            new PermissionExtension(Perms, PermissionRuleBook.CreateDefault()),
            new SubagentExtension(Service, workspaceId),
        ],
        Tools = new StaticToolSource([.. BuiltinTools.Create(), .. ExtraTools]),
        ResolveModel = m => m.Alias is Alias or "cheap" ? Binding : null,
        LoopOptions = new AgentLoopOptions { ResumeDelays = [TimeSpan.Zero, TimeSpan.Zero] },
        Diagnostics = Log,
    });

    public string NewSessionRow(string id = "s1")
    {
        Repo.Create(new SessionCreate(Workspace, id));
        return id;
    }

    /// <summary>顶层 Run。<paramref name="mode"/> 是权限档位(<c>ask / auto / full</c>)。</summary>
    public RunHandle Start(
        string sessionId, string text, string runId = "p1", string mode = "full", IReadOnlyList<RunSubscription>? subscriptions = null,
        ModelRef? model = null, ThinkingLevel thinking = ThinkingLevel.Auto, bool maxContext = false)
    {
        var options = new SendOptions(model ?? Model, thinking, maxContext, "code", mode);
        var request = new RunRequest(new RunId(runId), [Msg.User($"u-{runId}", text)], model ?? Model);
        var result = Registry.Launch(new RunLaunch(sessionId, Workspace, request)
        {
            Origin = RunOrigin.User,
            Options = options,
            PermissionMode = mode,
            Subscriptions = subscriptions ?? [],
        });
        return Assert.IsType<LaunchResult.Started>(result).Handle;
    }

    public static async Task Settled(RunHandle handle)
    {
        await Wait.UntilAsync(() => handle.IsSettled, what: $"run {handle.RunId} to settle");
        await handle.Completion!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public RunHandle Child(string parentRunId, int n = 1) => Registry.Get(new RunId($"{parentRunId}:sub:{n}")) ?? throw new InvalidOperationException($"no child {n} of {parentRunId}");

    public static string ChildSession(string parentSession, string parentRunId, int n = 1) => $"{parentSession}:sub:{parentRunId}:sub:{n}";

    public async Task<RunHandle> WaitForChild(string parentRunId, int n = 1)
    {
        var id = new RunId($"{parentRunId}:sub:{n}");
        await Wait.UntilAsync(() => Registry.Get(id) is not null, what: $"child {id} to exist");
        return Registry.Get(id)!;
    }

    public List<Message> Transcript(string sessionId) => [.. Repo.History(sessionId).Select(Ncw.Agent.Kernel.Session.MessageCodec.FromRecord)];

    public List<ToolResultPart> ToolResults(string sessionId) => [.. Transcript(sessionId).SelectMany(m => m.Parts).OfType<ToolResultPart>()];

    public static string Text(ToolResultPart result) => result.Output.Content;

    public static string[] ToolNames(LlmRequest request) => [.. request.Tools.Select(t => t.ExternalName)];

    public static string TaskArgs(string type = "general-purpose", string prompt = "do the thing", string description = "a task", bool background = false) =>
        $$"""{"description":"{{description}}","prompt":"{{prompt}}","subagent_type":"{{type}}"{{(background ? ",\"run_in_background\":true" : "")}}}""";

    /// <summary>一个回合里的若干工具调用。</summary>
    public static Script Calls(params (string Id, string Name, string Json)[] calls)
    {
        var events = new List<LlmEvent> { new MessageStart("fake", "p1", "anthropic", "fake-up") };
        for (var i = 0; i < calls.Length; i++)
        {
            events.Add(new ToolCallStart(i, calls[i].Id, calls[i].Name));
            events.Add(new ToolCallDelta(i, calls[i].Id, calls[i].Json));
            events.Add(new ToolCallEnd(i, calls[i].Id));
        }

        events.Add(new MessageEnd(StopReason.ToolUse, Up.Usage));
        return Up.Events([.. events]);
    }

    public void Dispose()
    {
        Service.Dispose();
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
