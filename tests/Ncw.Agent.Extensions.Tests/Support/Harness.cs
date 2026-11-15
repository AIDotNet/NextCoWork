using System.Text.Json;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Extensions.Tests.Support;

public sealed class FakeReviewer(ReviewResult result = ReviewResult.Unknown) : IPermissionReviewer
{
    public int Calls { get; private set; }

    public ReviewResult Result { get; set; } = result;

    public Exception? Throws { get; set; }

    public ValueTask<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        Calls++;
        return Throws is null ? ValueTask.FromResult(Result) : throw Throws;
    }
}

public sealed class FakeHook(HookVerdict verdict = default) : IPreToolVerdictSource
{
    public int Calls { get; private set; }

    public HookVerdict Verdict { get; set; } = verdict;

    public Exception? Throws { get; set; }

    public List<JsonElement> Inputs { get; } = [];

    public ValueTask<HookVerdict> EvaluateAsync(PreToolContext context, CancellationToken ct)
    {
        Calls++;
        Inputs.Add(context.Input);
        return Throws is null ? ValueTask.FromResult(Verdict) : throw Throws;
    }
}

public sealed class FakeInterceptor(InterceptorVerdict verdict = default) : IToolInterceptor
{
    public int Calls { get; private set; }

    public InterceptorVerdict Verdict { get; set; } = verdict;

    public ValueTask<InterceptorVerdict> InterceptAsync(PreToolContext context, CancellationToken ct)
    {
        Calls++;
        return ValueTask.FromResult(Verdict);
    }
}

/// <summary>
/// 权限扩展的测试台:真实的 <c>ExtensionHost</c> + <c>InteractionGate</c> + 规则簿(项目作用域是真文件),假工具、假钩子、假审核器。
/// 直接对 <c>before_tool</c> 闸门提问(<see cref="Decide"/>),和循环层的接缝在 <c>EndToEndTests</c> 里。
/// </summary>
public sealed class Harness : IDisposable
{
    public Harness(
        PermissionMode mode = PermissionMode.Ask,
        bool webSearch = true,
        bool withRoot = true,
        FakeReviewer? reviewer = null,
        IEnumerable<IPreToolVerdictSource>? hooks = null,
        IEnumerable<IToolInterceptor>? interceptors = null,
        PermissionOptions? options = null,
        IPermissionRuleStore? global = null,
        EnvironmentOverride? environment = null)
    {
        Fx = new Fixture(withRoot);
        Gate = new InteractionGate(new SequentialIds(), journal: Journal, log: Log);
        Book = PermissionRuleBook.CreateDefault(global);
        Reviewer = reviewer;
        Extension = new PermissionExtension(Runs, Book, reviewer, hooks, interceptors, options);
        Host = new ExtensionHost([Extension]);
        Interactions = Gate.OpenRun(RunId, 0);
        State = new PermissionRunState(RunId, "session-1", environment?.Wrap(Fx.Env) ?? Fx.Env, Interactions, mode)
        {
            Root = withRoot ? Fx.Runtime.Root : null,
            WebSearch = webSearch,
            UserTask = "do the thing",
            ReviewerModel = new Ncw.Agent.Llm.ModelRef(null, "reviewer"),
        };
        Runs.Register(State);
    }

    public Fixture Fx { get; }

    public RecordingJournal Journal { get; } = new();

    public Diagnostics Log { get; } = new();

    public InteractionGate Gate { get; }

    public PermissionRuns Runs { get; } = new();

    public PermissionRuleBook Book { get; }

    public FakeReviewer? Reviewer { get; }

    public PermissionExtension Extension { get; }

    public ExtensionHost Host { get; }

    public RunId RunId { get; } = new("run-1");

    public RunInteractions Interactions { get; }

    public PermissionRunState State { get; }

    public ExtensionContext Context(RunId? run = null) => new(run ?? RunId, "session-1", Log);

    public Task<PreflightDecision> Decide(IAgentTool tool, string json, string callId = "c1", CancellationToken ct = default, RunId? run = null) =>
        Host.GateAsync(LoopExtensionPoints.BeforeTool, new ToolInvocation(callId, tool.Info.InternalId, tool, json.Json()), Context(run), ct).AsTask();

    public async Task<ToolPermission> NextPending(int index = 0)
    {
        await Wait.UntilAsync(() => Gate.Snapshot().Pending.Count > index, what: "a pending approval");
        return (ToolPermission)Gate.Snapshot().Pending[index];
    }

    public RespondResult Respond(InteractionRequest request, PermissionChoice choice, string by = "ctrl-a") =>
        Gate.Respond(request.Id, request.Version, new ToolPermissionResponse(choice), by);

    public static string Deny(PreflightDecision d) => ((PreflightDecision.DenyDecision)d).Reason ?? "";

    public void Dispose() => Fx.Dispose();
}

public sealed class RecordingJournal : IInteractionJournal
{
    public List<InteractionRequest> Opened { get; } = [];

    public List<InteractionResolution> Settled { get; } = [];

    void IInteractionJournal.Opened(InteractionRequest request)
    {
        lock (Opened)
        {
            Opened.Add(request);
        }
    }

    void IInteractionJournal.Settled(InteractionResolution resolution)
    {
        lock (Settled)
        {
            Settled.Add(resolution);
        }
    }
}

/// <summary>把本机环境包成另一种 Kind(模拟 SSH:规则文件每次重读、读不了就失败)。</summary>
public sealed class EnvironmentOverride(Ncw.Agent.Environments.EnvironmentKind kind, Func<Ncw.Agent.Environments.IEnvironmentFs, Ncw.Agent.Environments.IEnvironmentFs>? fs = null)
{
    public Ncw.Agent.Environments.IExecutionEnvironment Wrap(Ncw.Agent.Environments.IExecutionEnvironment inner) => new Wrapped(inner, kind, fs?.Invoke(inner.Fs) ?? inner.Fs);

    private sealed class Wrapped(Ncw.Agent.Environments.IExecutionEnvironment inner, Ncw.Agent.Environments.EnvironmentKind kind, Ncw.Agent.Environments.IEnvironmentFs fs) : Ncw.Agent.Environments.IExecutionEnvironment
    {
        public Ncw.Agent.Environments.EnvironmentId Id => inner.Id;

        public Ncw.Agent.Environments.EnvironmentKind Kind => kind;

        public string DisplayName => inner.DisplayName;

        public int Generation => inner.Generation;

        public Ncw.Agent.Environments.EnvironmentState State => inner.State;

        public event EventHandler<Ncw.Agent.Environments.EnvironmentStatusChangedEventArgs>? StatusChanged
        {
            add => inner.StatusChanged += value;
            remove => inner.StatusChanged -= value;
        }

        public Ncw.Agent.Environments.EnvironmentFacts Facts => inner.Facts;

        public Ncw.Agent.Environments.PathGuard Paths => inner.Paths;

        public Ncw.Agent.Environments.IEnvironmentFs Fs => fs;

        public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

        public Task<Ncw.Agent.Environments.EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

        public Task<Ncw.Agent.Environments.IEnvProcess> OpenProcessAsync(Ncw.Agent.Environments.CommandSpec spec, CancellationToken ct = default) => inner.OpenProcessAsync(spec, ct);

        public Task<Ncw.Agent.Environments.ITerminal> OpenTerminalAsync(Ncw.Agent.Environments.TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

        public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

        public Task<Ncw.Agent.Environments.IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
