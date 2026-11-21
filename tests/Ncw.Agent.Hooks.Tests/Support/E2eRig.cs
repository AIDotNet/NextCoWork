using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Extensions.Planning.Plans;
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

namespace Ncw.Agent.Hooks.Tests.Support;

/// <summary>闸门 → 编排层的「待处理交互数」视图(Host 里它是一个适配器)。</summary>
internal sealed class GateView(InteractionGate gate) : IInteractionView
{
    public int PendingCount(RunId runId) => gate.PendingCount(runId);
}

/// <summary>
/// 照 Host 的 <c>ConversationRunExtension</c> 写的测试替身:每个 Run 创建时造它的 <see cref="ToolRuntime"/>、打开交互作用域、登记 <see cref="PermissionRunState"/>;
/// 工具上下文里套上 <see cref="ExecutionToolContext"/>;Run 结束时清理。计划 / 目标的 Run 扩展排在它<b>之后</b>(围栏的同步靠它造好的运行时)。
/// </summary>
internal sealed class HostLikeRunExtension(E2eRig rig) : IRunExtension
{
    private readonly ConcurrentDictionary<RunId, ToolRuntime> _runtimes = new();

    public void OnCreated(RunHandle run)
    {
        var runtime = new ToolRuntime { Environment = rig.Env, Root = rig.Root };
        var interactions = rig.Gate.OpenRun(run.RunId, run.Depth);
        var mode = PermissionModes.TryParse(run.LivePermissionMode, out var parsed) ? parsed : PermissionMode.Full;
        rig.PermissionRuns.Register(new PermissionRunState(run.RunId, run.SessionId, runtime.Environment, interactions, mode)
        {
            Root = runtime.Root,
            WorkspaceId = run.WorkspaceId,
            PlanFence = runtime.WriteFence,
            UserTask = "do the thing",
            PlanWorkflow = rig.PlanWorkflow,
        });
        _runtimes[run.RunId] = runtime;
        rig.Runtimes[run.RunId] = runtime;
    }

    public ToolContext EnrichToolContext(RunHandle run, ToolContext context) =>
        _runtimes.TryGetValue(run.RunId, out var runtime) ? ExecutionToolContext.Enricher(runtime)(context) : context;

    public void OnEnded(RunHandle run, RunStatus status, AgentError? error)
    {
        rig.PermissionRuns.Get(run.RunId)?.Interactions.End(status);
        rig.PermissionRuns.Remove(run.RunId);
    }

    public void OnSettled(RunHandle run) => _runtimes.TryRemove(run.RunId, out _);
}

internal sealed class RigFactory : IRunRequestFactory
{
    public RunRequest Create(RunTemplate template) =>
        new(template.RunId, template.Input, template.Options.Model, template.Options.Thinking) { MaxContext = template.Options.MaxContext };
}

/// <summary>深度 &gt; 0 的 Run 不真的跑,只发 <c>run_start</c> 然后挂着直到被中止——当「会话名下还有后台子代理在跑」用。</summary>
internal sealed class ChildAwareDriver(IRunDriver inner) : IRunDriver
{
    public async Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct)
    {
        if (run.Depth == 0)
        {
            return await inner.RunAsync(run, request, sink, ct).ConfigureAwait(false);
        }

        await sink.EmitAsync(new RunStartEvent(run.RunId, run.SessionId, request.Model), CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await sink.EmitAsync(new RunEndEvent(RunStatus.Aborted), CancellationToken.None).ConfigureAwait(false);
        return new RunOutcome(run.RunId, RunStatus.Aborted, null, new TokenUsage(), CostInfo.NotMetered, 0);
    }
}

/// <summary>
/// 端到端的装配:<b>真</b> <c>AgentLoop</c> / <c>AgentSession</c> / <c>RunRegistry</c> + 真 SQLite + 假上游 + <b>真实工具</b>(Read / Write / Edit 跑在 <c>LocalEnvironment</c> 上)+ 真实的权限扩展和交互闸门 +
/// <see cref="PlanningKit"/>(计划模式 / 待办 / 目标三个一方扩展)。上游只有一件假东西:主模型的脚本和判定器的脚本。
/// </summary>
public sealed class E2eRig : IAsyncDisposable
{
    public const string Workspace = "ws";
    public const string Alias = "claude-sonnet-4";

    private readonly string _base;
    private int _runs;

    public E2eRig()
    {
        var raw = Path.Combine(Path.GetTempPath(), $"ncw-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(raw);
        Env = new LocalEnvironment("dev");
        _base = Env.Fs.RealPathAsync(EnvPath.Parse(raw, HookFixture.Style)).AsTask().GetAwaiter().GetResult().Value;
        WorkRoot = Path.Combine(_base, "ws");
        Directory.CreateDirectory(WorkRoot);
        Directory.CreateDirectory(Path.Combine(_base, "profile"));
        Root = EnvPath.Parse(WorkRoot, HookFixture.Style);
        Db = AgentDatabase.Open(Path.Combine(_base, "db"));
        Repo = new SessionRepository(Db);
        Gate = new InteractionGate(new SeqIds("i"));
        PermissionRuns = new PermissionRuns();
        Ids = new SeqIds("x");
        RunRegistry? registryForHooks = null;
        Hooks = new HooksService(new HooksServiceOptions
        {
            GlobalEnvironment = Env,
            GlobalSettingsPath = EnvPath.Parse(Path.Combine(_base, "profile", "settings.json"), HookFixture.Style),
            ResolveProject = (id, _) => ValueTask.FromResult(id == Workspace ? new HookProjectTarget(Env, Root) : null),
            Evaluator = new LlmHookEvaluator(new LlmHookEvaluatorOptions
            {
                Llm = Llm,
                ResolveModel = ResolveAlias,
                ReadMessages = sessionId => Repo.History(sessionId).Select(MessageCodec.FromRecord).ToList(),
                ActiveModel = (sessionId, runId) => registryForHooks?.Get(new RunId(runId))?.Model ?? new ModelRef("p1", Alias),
            }),
            Log = Log,
        });
        Hooks.AttachNotifications(Gate, PermissionRuns);

        Kit = PlanningKit.Create(new PlanningKitOptions
        {
            Llm = Llm,
            Sessions = Repo,
            Gate = Gate,
            PermissionRuns = PermissionRuns,
            Ids = new SeqIds("k"),
            ResolveModel = ResolveAlias,
            EvaluatorModel = () => EvaluatorSetting,
            PlanWorkflow = _ => PlanWorkflow,
            ProposalPolicy = () => Policy,
            Log = Log,
            Telemetry = (name, fields) =>
            {
                lock (Telemetry)
                {
                    Telemetry.Add($"{name} {string.Join(' ', fields.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
            },
        });
        Sessions = new SessionDirectory(NewSession);
        Registry = new RunRegistry(new RunRegistryDeps
        {
            DeviceId = "dev-1",
            Sessions = Repo,
            Driver = new ChildAwareDriver(new SessionRunDriver(Sessions)),
            Requests = new RigFactory(),
            Queue = new SessionInputQueueStore(new SessionInputStore(Db)),
            SessionDirectory = Sessions,
            Ids = new SeqIds("q"),
            Log = Log,
            Extensions = [new HostLikeRunExtension(this), .. Kit.RunExtensions],
            Interactions = new GateView(Gate),
        });
        registryForHooks = Registry;
        Kit.Bind(Registry);
    }

    public static readonly SendOptions Options = new(ModelRef.Unpinned(Alias), ThinkingLevel.Auto, false, "code", "full");

    public LocalEnvironment Env { get; }

    public EnvPath Root { get; }

    public string WorkRoot { get; }

    public AgentDatabase Db { get; }

    public SessionRepository Repo { get; }

    public InteractionGate Gate { get; }

    public PermissionRuns PermissionRuns { get; }

    public IIdGenerator Ids { get; }

    public FakeLlm Llm { get; } = new();

    public CollectingLog Log { get; } = new();

    public List<string> Telemetry { get; } = [];

    public HooksService Hooks { get; }

    public PlanningKit Kit { get; }

    public SessionDirectory Sessions { get; }

    public RunRegistry Registry { get; }

    public ConcurrentDictionary<RunId, ToolRuntime> Runtimes { get; } = new();

    private static ModelBinding? ResolveAlias(ModelRef m) =>
        m.Alias is Alias or "judge-a" or "judge-b" ? new ModelBinding(m.Alias, "p1", "up", 200_000, 8192) : null;

    /// <summary>设置里的判定模型(每个 Run 开始时冻结)。</summary>
    public ModelRef? EvaluatorSetting { get; set; }

    /// <summary>下一个 Run 开着计划工作流吗(装配方按模式回答)。</summary>
    public bool PlanWorkflow { get; set; }

    public GoalProposalPolicy Policy { get; set; } = GoalProposalPolicy.Auto;

    public GoalService Goals => Kit.Goals;

    public PlanService Plans => Kit.Plans;

    private AgentSession NewSession(string sessionId, string workspaceId) => new(sessionId, workspaceId, new AgentSessionDeps
    {
        Sessions = Repo,
        Llm = Llm,
        Ids = new SeqIds($"m{sessionId}-"),
        Clock = TimeProvider.System,
        Delayer = new NoDelay(),
        Environment = Env,
        WorkspaceRoot = Root,
        Extensions = [new PermissionExtension(PermissionRuns, PermissionRuleBook.CreateDefault(null), reviewer: null, hooks: [Hooks.PreToolSource]), Hooks.CreateExtension(workspaceId), .. Kit.Extensions],
        Tools = new StaticToolSource(BuiltinTools.Create()),
        ResolveModel = ResolveAlias,
        LoopOptions = new AgentLoopOptions { ResumeDelays = [] },
        Diagnostics = Log,
    });

    public string NewSessionRow(string id = "s1")
    {
        Repo.Create(new SessionCreate(Workspace, id));
        return id;
    }

    /// <summary>用户发一句话,起一个 Run。</summary>
    public RunHandle Send(string sessionId, string text, string? runId = null, string permission = "full")
    {
        var n = Interlocked.Increment(ref _runs);
        var id = new RunId(runId ?? $"run-{n}");
        var request = new RunRequest(id, [Msg.User($"u{n}", text)], ModelRef.Unpinned(Alias));
        var launch = new RunLaunch(sessionId, Workspace, request) { Options = Options, PermissionMode = permission };
        return Assert.IsType<LaunchResult.Started>(Registry.Launch(launch)).Handle;
    }

    public static async Task Settled(RunHandle handle)
    {
        await Wait.UntilAsync(() => handle.IsSettled, what: $"run {handle.RunId} to settle");
        await handle.Completion!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task<RunHandle> RunAsync(string sessionId, string text, string permission = "full")
    {
        var handle = Send(sessionId, text, permission: permission);
        await Settled(handle);
        return handle;
    }

    /// <summary>等到会话里没有任何 Run 占着(包括目标检查 / 内部输入起的那些)。</summary>
    public async Task IdleAsync(string sessionId)
    {
        await Wait.UntilAsync(() => !Registry.IsSessionBusy(sessionId) && Registry.RunningIn(Workspace).All(h => h.SessionId != sessionId), what: "the session to go idle");
        await Task.Delay(30);
        await Wait.UntilAsync(() => !Registry.IsSessionBusy(sessionId), what: "the session to stay idle");
    }

    public IReadOnlyList<Message> History(string sessionId) => [.. Repo.History(sessionId).Select(MessageCodec.FromRecord)];

    public IEnumerable<ToolResultPart> Results(string sessionId) => History(sessionId).SelectMany(m => m.Parts).OfType<ToolResultPart>();

    public static string ResultText(ToolResultPart r) => r.Output.Content;

    public IReadOnlyList<AgentEvent> Events(RunHandle run) => [.. run.Since(0).Select(e => e.Event)];

    /// <summary>上行请求里发出去的全部正文(模型读到的)。</summary>
    public static string WireText(LlmRequest request) =>
        string.Join('\n', request.Messages.SelectMany(m => m.Parts).Select(p => p switch
        {
            TextPart t => t.Text,
            ToolResultPart r => r.Output.Content,
            ToolCallPart c => c.Input.GetRawText(),
            _ => "",
        }));

    public static IReadOnlyList<string> ToolNames(LlmRequest request) => [.. request.Tools.Select(t => t.ExternalName)];

    public string ProjectFile => Path.Combine(WorkRoot, ".next-cowork", "settings.local.json");

    public string GlobalFile => Path.Combine(_base, "profile", "settings.json");

    public string Abs(string rel) => Path.Combine(WorkRoot, rel);

    public void Put(string rel, string content)
    {
        var path = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public async ValueTask DisposeAsync()
    {
        await Hooks.DisposeAsync();
        Goals.Dispose();
        Registry.Dispose();
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
