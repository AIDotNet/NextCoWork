using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>
/// 运行中 → 终态(05 §1)。只能从 <see cref="Running"/> 走向 <see cref="Done"/> / <see cref="Error"/> / <see cref="Aborted"/>;
/// 终态之后再来的事件丢弃并记警告。与 <see cref="RunPhase"/>(Core,客户端也用)一一对应。
/// </summary>
public enum RunState
{
    Running,
    Done,
    Error,
    Aborted,
}

public static class RunStates
{
    public static bool IsTerminal(this RunState state) => state != RunState.Running;

    public static RunState Of(RunStatus status) => status switch
    {
        RunStatus.Done => RunState.Done,
        RunStatus.Aborted => RunState.Aborted,
        _ => RunState.Error,
    };

    public static RunStatus ToStatus(this RunState state) => state switch
    {
        RunState.Done => RunStatus.Done,
        RunState.Aborted => RunStatus.Aborted,
        _ => RunStatus.Error,
    };

    public static RunPhase ToPhase(this RunState state) => state switch
    {
        RunState.Running => RunPhase.Running,
        RunState.Done => RunPhase.Done,
        RunState.Aborted => RunPhase.Aborted,
        _ => RunPhase.Error,
    };
}

/// <summary>入口(05 §1 的表)。统一走 <see cref="RunRegistry.Launch"/>,没有任何入口能绕过它。</summary>
public enum RunOrigin
{
    /// <summary>用户发送。会话已有运行中的顶层 Run 时改为进入排队消息(<see cref="RunRegistry.Send"/>)。</summary>
    User,

    /// <summary>内部发起:排队消息续跑、后台子代理报告、目标检查。</summary>
    Internal,

    /// <summary>子代理:先继承父 Run 的订阅再启动。</summary>
    Subagent,

    /// <summary>开放网关:会话忙时直接拒绝(409),不排队。</summary>
    Gateway,

    /// <summary>定时任务。同样走 <c>Launch</c>(2.x 里它绕过了入口,3.0 不再允许)。</summary>
    Scheduled,
}

/// <summary>
/// 发起一个 Run 需要的全部东西。<see cref="Request"/> 是会话层的请求(RunId 由<b>客户端</b>生成,这样客户端能在 Run 开始前就订阅);
/// 编排层只往里接两个钩子(<c>TakeSteering</c> 和 <c>EnrichToolContext</c>),其余原样交给会话层。
/// </summary>
public sealed record RunLaunch(string SessionId, string WorkspaceId, RunRequest Request)
{
    public RunId RunId => Request.RunId;

    public int Depth => Request.Depth;

    public RunOrigin Origin { get; init; } = RunOrigin.User;

    /// <summary>父 Run(子代理)。子 Run 登记到父 Run 的 children,并继承父 Run 全部订阅——先继承、再启动。</summary>
    public RunId? ParentRunId { get; init; }

    /// <summary>真实父会话(子会话 id 是 <c>&lt;父会话&gt;:sub:&lt;子 RunId&gt;</c>,但永远不解析 id,见 04 §1)。父 Run 被回收后广播的子代理半边靠它找父会话。</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>发送档位的快照:顶层 Run 开始时记住,内部输入(后台报告、目标检查)续用它。</summary>
    public SendOptions? Options { get; init; }

    /// <summary>实时权限模式的初值(不透明字符串,由权限扩展解释)。</summary>
    public string PermissionMode { get; init; } = "";

    /// <summary>启动前就绑定的订阅(「先订阅再启动」):Run 的第一个事件之前它们已经挂在 Run 上。</summary>
    public IReadOnlyList<RunSubscription> Subscriptions { get; init; } = [];
}

public enum RunRefusal
{
    /// <summary>会话互斥:同一会话同时最多一个运行中的 Run 或一个「操作」(如手动压缩),<c>run_settled</c> 之前下一个 Run 不能开始(R8)。</summary>
    SessionBusy,

    DuplicateRunId,

    ShuttingDown,

    InvalidRequest,
}

public abstract record LaunchResult
{
    private LaunchResult()
    {
    }

    public sealed record Started(RunHandle Handle) : LaunchResult;

    public sealed record Refused(RunRefusal Reason, string Detail) : LaunchResult;
}

/// <summary>用户发送:RunId 和消息 id 都由客户端生成。</summary>
public sealed record UserSend(RunId RunId, string SessionId, string WorkspaceId, MessageId MessageId, IReadOnlyList<Part> Parts, SendOptions Options)
{
    public RunOrigin Origin { get; init; } = RunOrigin.User;

    public IReadOnlyList<RunSubscription> Subscriptions { get; init; } = [];

    public string PermissionMode { get; init; } = "";
}

public abstract record SendResult
{
    private SendResult()
    {
    }

    /// <summary>开了一个新 Run。</summary>
    public sealed record StartedRun(RunHandle Handle) : SendResult;

    /// <summary>会话已有运行中的顶层 Run:这句话进了队列(回执里是新的队列)。客户端据此撤掉乐观消息,而不是当成失败。</summary>
    public sealed record Queued(QueueResult Result) : SendResult;

    public sealed record Refused(RunRefusal Reason, string Detail) : SendResult;
}

/// <summary>
/// 发送档位(2.x 的 <c>SendOptions</c> 里续跑要用的那几项)。逐条冻结:排队 5 分钟里改两次模型,三条消息就该有三份档位。
/// <see cref="Mode"/> / <see cref="PermissionMode"/> 是不透明字符串(模式和权限由扩展解释,本层不引用它们)。
/// </summary>
public sealed record SendOptions(ModelRef Model, ThinkingLevel Thinking = ThinkingLevel.Auto, bool MaxContext = false, string Mode = "", string PermissionMode = "");

/// <summary>编排层为一个 Run 向装配方要请求时给的模板(排队续跑、内部发起:这些 Run 不是调用方亲手构造的)。</summary>
public sealed record RunTemplate(RunId RunId, string SessionId, string WorkspaceId, IReadOnlyList<Message> Input, SendOptions Options, RunOrigin Origin);

/// <summary>
/// 装配方(Host)实现:读设置、拼 <see cref="SessionSettings"/>、模式提示词等,产出会话层的 <see cref="RunRequest"/>(不带编排层的两个钩子,由 <see cref="RunRegistry"/> 接)。
/// </summary>
public interface IRunRequestFactory
{
    RunRequest Create(RunTemplate template);
}

/// <summary>按会话取 <see cref="AgentSession"/>(同一个会话必须是同一个对象:会话锁和 <c>session_start</c> 都挂在它身上)。</summary>
public interface ISessionDirectory
{
    AgentSession Get(string sessionId, string workspaceId);
}

/// <summary>缓存式的会话目录。会话删除后调 <see cref="Remove"/>。</summary>
public sealed class SessionDirectory(Func<string, string, AgentSession> create) : ISessionDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    public AgentSession Get(string sessionId, string workspaceId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                _sessions[sessionId] = session = create(sessionId, workspaceId);
            }

            return session;
        }
    }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }
}

/// <summary>驱动一个 Run 的东西。默认是 <see cref="SessionRunDriver"/>(真的 <c>AgentSession</c>);测试可以换成不认取消令牌的假驱动来测看门狗。</summary>
public interface IRunDriver
{
    /// <summary>不应抛(取消 = 中止);抛了也会被收成错误结局。</summary>
    Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct);
}

public sealed class SessionRunDriver(ISessionDirectory sessions) : IRunDriver
{
    public Task<RunOutcome> RunAsync(RunHandle run, RunRequest request, IRunSink sink, CancellationToken ct) =>
        sessions.Get(run.SessionId, run.WorkspaceId).RunAsync(request, sink, ct);
}

/// <summary>
/// 待处理交互的只读视图(闸门实现,Host 适配)。编排层只用它数数:广播里的 <c>PendingInteractions</c>、回收时「还有挂起的交互就钉住不收」。
/// 交互本身的状态只有闸门一个真源。
/// </summary>
public interface IInteractionView
{
    int PendingCount(RunId runId);
}

/// <summary>
/// 编排层的扩展点:交互闸门(<c>OpenRun</c> / <c>End</c>)、子代理队列、目标、定时任务从这里接进来,而不是改编排层。所有回调的异常都被吞掉并记诊断,不影响 Run 的生命周期。
/// </summary>
public interface IRunExtension
{
    /// <summary>Run 已登记、会话互斥已持有,驱动还没开始(子 Run 此时已继承了父 Run 的订阅)。</summary>
    void OnCreated(RunHandle run)
    {
    }

    /// <summary>把基础工具上下文换成带 <c>Interact</c> / <c>SpawnSubagent</c> 等能力的派生类型。多个扩展按注册顺序依次套。</summary>
    ToolContext EnrichToolContext(RunHandle run, ToolContext context) => context;

    /// <summary>有人要求中止(只发信号,Run 还在运行)。顶层 Run 被用户停止时,目标层据此暂停目标(轮换目标 id)、撤掉它名下的目标提议。</summary>
    void OnAbortRequested(RunHandle run)
    {
    }

    /// <summary><c>run_end</c> 进了日志(终态)。此时<b>可能还没落库完</b>。</summary>
    void OnEnded(RunHandle run, RunStatus status, AgentError? error)
    {
    }

    /// <summary>落库、释放会话互斥、<c>run_settled</c> 之后,续跑排队消息之前。</summary>
    void OnSettled(RunHandle run)
    {
    }
}

/// <summary>编排层的可调参数(默认值全部沿用 2.x)。</summary>
public sealed record RunRegistryOptions
{
    /// <summary>按了停止之后给驱动自己收尾的时间;超过就由看门狗强行收口(2.x #187)。</summary>
    public TimeSpan AbortGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>已结束的 Run 在内存里留多久(重载窗口、晚到的 attach 还能拿到终态快照)。</summary>
    public TimeSpan FinishedRunTtl { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>已结束 Run 的日志总预算;超了不等 TTL,从最早结束的回收起。</summary>
    public long FinishedCacheBytes { get; init; } = 64L * 1024 * 1024;

    public TimeSpan ReapInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 有 Run 在运行时,每隔这么久把 <c>activeRuns</c> 全量重播一次(01 §5「能从丢消息中自我收敛」:定期推一次全量广播)。除了起止和交互变化的事件驱动广播之外的兜底;
    /// 没有任何 Run 在运行时不播(那时没有什么可收口的)。<see cref="TimeSpan.Zero"/> = 关闭。
    /// </summary>
    public TimeSpan ActiveRebroadcast { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单个 Run 事件日志的硬上限(条数 / 字节,02 §3)。回放预算,不是模型上下文。</summary>
    public int LogMaxEntries { get; init; } = 2000;

    public long LogMaxBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>会话被历史操作(手动压缩)占着时,内部输入隔多久再试一次。</summary>
    public TimeSpan DeferredRetry { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>最多重试多少次(一个永远不释放的互斥不该让定时器永远转下去)。</summary>
    public int DeferredMaxRetries { get; init; } = 120;

    /// <summary>记住多少条会话的「上一次发送档位」。</summary>
    public int LastOptionsMax { get; init; } = 256;

    /// <summary>应用退出时等所有 Run 收尾的最长时间。</summary>
    public TimeSpan ShutdownWait { get; init; } = TimeSpan.FromSeconds(6);
}

public static class RunErrorKeys
{
    /// <summary>看门狗:中止后驱动没有在宽限期内收尾,被强行结束。</summary>
    public const string Abandoned = "run.abandoned";

    /// <summary>驱动抛了意料之外的异常。</summary>
    public const string Internal = "run.internal";

    /// <summary>编排层只给了一个没开始的结局(例如会话不存在)。</summary>
    public const string NotStarted = "run.notStarted";
}
