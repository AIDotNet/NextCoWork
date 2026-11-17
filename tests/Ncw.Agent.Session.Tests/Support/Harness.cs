using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Session.Tests.Support;

/// <summary>真 Storage(临时目录里的 SQLite 文件,重开库才测得出「落盘之后」)+ 假上游 + 本机执行环境(真临时目录)。</summary>
public sealed class Harness : IDisposable
{
    public const string SessionId = "s1";

    public static readonly ModelBinding Alias = new("claude-sonnet-4", "p1", "up", 200_000, 8192);

    private readonly string _dbRoot = Path.Combine(Path.GetTempPath(), $"ncwsession-{Guid.NewGuid():N}");

    public Harness(IReadOnlyList<IAgentExtension>? extensions = null, IEnumerable<IAgentTool>? tools = null, Func<ModelRef, ModelBinding?>? resolve = null)
    {
        Directory.CreateDirectory(_dbRoot);
        Root = Path.Combine(_dbRoot, "ws");
        Directory.CreateDirectory(Root);
        Db = AgentDatabase.Open(Path.Combine(_dbRoot, "db"));
        Repo = new SessionRepository(Db);
        Repo.Create(new SessionCreate("ws", SessionId));
        Env = new LocalEnvironment("dev");
        Llm = new FakeLlm();
        Extensions = extensions ?? [];
        Tools = tools?.ToList() ?? [];
        Resolve = resolve ?? (m => m.Alias == Alias.Alias ? Alias : null);
        Session = NewSession();
    }

    private readonly SeqIds _ids = new();

    public string Root { get; }

    public EnvPath RootPath => LocalEnvironment.PathOf(Root);

    public AgentDatabase Db { get; }

    public SessionRepository Repo { get; }

    public LocalEnvironment Env { get; }

    public FakeLlm Llm { get; }

    public IReadOnlyList<IAgentExtension> Extensions { get; }

    public List<IAgentTool> Tools { get; }

    public Func<ModelRef, ModelBinding?> Resolve { get; }

    public CollectingLog Log { get; } = new();

    public AgentSession Session { get; private set; }

    public AgentLoopOptions LoopOptions { get; set; } = new() { ResumeDelays = [TimeSpan.Zero, TimeSpan.Zero] };

    public ModelRef Model => ModelRef.Unpinned(Alias.Alias);

    public AgentSession NewSession() => new(SessionId, "ws", new AgentSessionDeps
    {
        Sessions = Repo,
        Llm = Llm,
        Ids = _ids,
        Clock = new FakeClock(),
        Delayer = new NoDelay(),
        Environment = Env,
        WorkspaceRoot = RootPath,
        Extensions = Extensions,
        Tools = new StaticToolSource(Tools),
        ResolveModel = Resolve,
        LoopOptions = LoopOptions,
        Diagnostics = Log,
    });

    public static Message User(string id, string text) =>
        new(new MessageId(id), Role.User, [new TextPart(text)], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    public static Message Assistant(string id, params Part[] parts) =>
        new(new MessageId(id), Role.Assistant, parts, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    private int _run;

    public RunRequest Request(string text, string? id = null, Action<RunRequestBuilder>? configure = null)
    {
        var builder = new RunRequestBuilder();
        configure?.Invoke(builder);
        return new RunRequest(new RunId($"run-{++_run}"), text.Length == 0 ? [] : [User(id ?? $"u{_run}", text)], Model, builder.Thinking)
        {
            Settings = builder.Settings,
            MaxContext = builder.MaxContext,
            Depth = builder.Depth,
            TakeSteering = builder.TakeSteering,
            EnrichToolContext = builder.Enrich,
            IsAbandoned = builder.IsAbandoned,
        };
    }

    /// <summary>跑一个 Run,收集事件。每次都检查事件流的完整性(run_start 第一个、run_end 倒数第二个、run_settled 最后一个)。</summary>
    public async Task<Ran> RunAsync(string text, Action<RunRequestBuilder>? configure = null, CancellationToken ct = default, AgentSession? session = null, string? id = null)
    {
        var sink = new ListRunSink();
        var outcome = await (session ?? Session).RunAsync(Request(text, id, configure), sink, ct);
        var events = sink.Events;
        if (outcome.Started)
        {
            Assert.IsType<RunStartEvent>(events[0]);
            Assert.IsType<RunSettledEvent>(events[^1]);
            Assert.IsType<RunEndEvent>(events[^2]);
        }

        return new Ran(outcome, events, Llm);
    }

    public IReadOnlyList<Message> Stored() => Repo.History(SessionId).Select(MessageCodec.FromRecord).ToList();

    public void SeedHistory(params Message[] messages)
    {
        foreach (var message in messages)
        {
            Repo.Commit(SessionId, MessageCodec.ToRecord(message));
        }
    }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dbRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class RunRequestBuilder
{
    public ThinkingLevel Thinking { get; set; } = ThinkingLevel.Auto;

    public SessionSettings Settings { get; set; } = new();

    public bool MaxContext { get; set; }

    public int Depth { get; set; }

    public Func<IReadOnlyList<Message>>? TakeSteering { get; set; }

    public Func<ToolContext, ToolContext>? Enrich { get; set; }

    public Func<bool>? IsAbandoned { get; set; }
}

public sealed record Ran(RunOutcome Outcome, IReadOnlyList<AgentEvent> Events, FakeLlm Llm)
{
    public RunStatus Status => Outcome.Status;

    public IEnumerable<T> Of<T>() where T : AgentEvent => Events.OfType<T>();

    public IReadOnlyList<ContextStatusPhase> Phases => Of<ContextStatusEvent>().Select(e => e.Phase).ToList();
}

public sealed class CollectingLog : ILoopDiagnostics
{
    public List<string> Warnings { get; } = [];

    public void Warn(string message, Exception? error = null)
    {
        lock (Warnings)
        {
            Warnings.Add(message);
        }
    }
}

/// <summary>写一个扩展用的小壳。</summary>
public sealed class Ext(string id, int order, Action<IExtensionRegistry> register) : IAgentExtension
{
    public string Id => id;

    public int Order => order;

    public void Register(IExtensionRegistry registry) => register(registry);
}
