using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>某台设备上的一个会话(key 带 deviceId,AGENTS.md §3 规则 1)。</summary>
public readonly record struct SessionRef(string DeviceId, string SessionId);

/// <summary>某台设备上的一个 Run。</summary>
public readonly record struct RunRef(string DeviceId, string RunId);

public enum ToolCallStatus
{
    Running,
    Done,
    Failed,
}

/// <summary>一次工具调用在界面上的状态(来自 <c>tool_start</c> / <c>tool_end</c>)。<see cref="Environment"/> 说清在哪台设备的哪个环境里执行。</summary>
/// <param name="QueuedAhead">
/// <c>Task</c> 调用在等子代理名额时的排位(<c>tool_progress</c> 的结构化 <c>Details</c>:前面还有几个);不在排队为 null。子代理开始(<c>subagent_start</c>)或工具结束后清掉。
/// </param>
/// <param name="DetailsJson">
/// 只给界面的结构化详情(<c>tool_progress</c> / <c>tool_end</c> 的 <c>Details</c>),目前只保留 widget 卡片(<c>kind: "widget"</c> / <c>"widgetData"</c>)——
/// 交互模式的卡片在工具返回之前就经进度推出来了;终态(<c>tool_end</c>)的详情压过进度的(规则 6)。
/// </param>
public sealed record ToolCallView(string CallId, string Name, string InputJson, ToolCallStatus Status, string? Output, EnvRefDto? Environment, int? QueuedAhead = null, string? DetailsJson = null);

/// <summary>
/// 一个 Run 的镜像。事实只在设备上(<c>run/&lt;id&gt;</c> 主题);这里只有「到目前为止收到了什么」。
/// <see cref="Phase"/> 单调:终态压过运行中,运行中压过未知,重放的旧事件不能改回运行中(规则 6);设备断开时运行中的 Run 变成 <see cref="RunPhase.Unknown"/>,既不判结束也不继续算运行(规则 7)。
/// </summary>
/// <param name="Seq">收到的最后一个事件序号(= 续传游标);序号 ≤ 它的事件是重放,丢弃。</param>
/// <param name="StartedAtRev">创建这份镜像时已知的 activeRuns 版本:只有更新的权威广播才能据此对账(旧广播里没有它是正常的)。</param>
/// <param name="Streaming">消息 id → 到目前为止的文本增量;<c>message_end</c> 带完整消息后清掉。</param>
/// <param name="PhaseGuessed">
/// <see cref="Phase"/> 是权威广播对账推断的(广播里已经没有这个 Run,但它的 <c>run_end</c> 还没收到):界面只需要「不再运行」。
/// 真实的结局事件(<c>run_end</c> / <c>run_settled</c> / 快照)到达时替换这个推断,不受「终态不再改」的限制。
/// </param>
public sealed record RunView(
    RunRef Ref,
    string SessionId,
    RunPhase Phase,
    long Seq,
    long StartedAtRev,
    bool Settled,
    ErrorDto? Error,
    ImmutableDictionary<string, string> Streaming,
    ImmutableDictionary<string, ToolCallView> Tools,
    bool PhaseGuessed = false)
{
    /// <summary>消息 id → 到目前为止的思考增量(和 <see cref="Streaming"/> 一样,<c>message_end</c> 带完整消息后清掉)。</summary>
    public ImmutableDictionary<string, string> Thinking { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>最近一次上下文占用(<c>context_usage</c> 事件);没收到过为 null。状态行的圆环据它算,窗口判据在 Core 的 <see cref="ContextWindows"/>。</summary>
    public ContextUsageEventDto? Context { get; init; }

    /// <summary>上下文压缩的阶段(<c>compacting</c> / <c>compacted</c> / <c>failed</c> / <c>disabled</c>);没有为 null。</summary>
    public string? CompactPhase { get; init; }

    /// <summary>上游正在重试(<c>provider_retry</c>);下一个流式增量或消息结束就清掉。</summary>
    public ProviderRetryEventDto? Retry { get; init; }

    public static RunView New(RunRef run, string sessionId, long startedAtRev) => new(
        run, sessionId, RunPhase.Unknown, 0, startedAtRev, false, null,
        ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, ToolCallView>.Empty);
}

/// <summary>一个会话的镜像:元数据 + 最近的消息 + 排队消息 + 正在运行的顶层 Run。</summary>
public sealed record SessionView(
    SessionRef Ref,
    SessionDto Meta,
    ImmutableArray<MessageDto> Messages,
    bool HasMore,
    QueueDto Queue,
    string? ActiveRunId)
{
    /// <summary>
    /// 当前的待办清单(转录里最近一次<b>成功</b>的 <c>TodoWrite</c> 调用,Core 的 <see cref="Todos"/> 派生;没有为 null)。待办没有表:它是会话消息的派生视图,
    /// 随消息一起更新(落库 / 快照 / 历史改写),不会比转录新也不会比转录旧。
    /// </summary>
    public TodoView? Todo { get; init; }
}

/// <summary>一台设备上挂起的交互(<c>interactions</c> 主题的镜像:全量,收到更新的就整体替换)。</summary>
public sealed record DeviceInteractions(long Revision, ImmutableArray<InteractionDto> Pending)
{
    public static readonly DeviceInteractions Empty = new(0, ImmutableArray<InteractionDto>.Empty);
}

/// <summary>对话链路的镜像:会话、Run、待处理的交互。修改只经 <c>ConversationActions</c> 和设备连接(<c>DeviceLink</c>)。</summary>
public sealed record ConversationState(
    ImmutableDictionary<SessionRef, SessionView> Sessions,
    ImmutableDictionary<RunRef, RunView> Runs,
    ImmutableDictionary<string, DeviceInteractions> Interactions)
{
    /// <summary>子代理卡片(<c>(deviceId, 父会话, Task 调用)</c> → 镜像)。事件、库里的回执、权威广播三路合并,规则在 Core 的 <c>SubagentView</c>。</summary>
    public ImmutableDictionary<SubagentRef, SubagentEntry> Subagents { get; init; } = ImmutableDictionary<SubagentRef, SubagentEntry>.Empty;

    /// <summary>各会话的目标(<c>goal</c> 主题 / <c>goal.get</c>;清除留带版本号的墓碑)。</summary>
    public ImmutableDictionary<SessionRef, GoalEntry> Goals { get; init; } = ImmutableDictionary<SessionRef, GoalEntry>.Empty;

    /// <summary>某个会话下的子代理卡片(新开的在前,在跑的更靠前:<see cref="SubagentView.CompareByRecency"/>)。</summary>
    public IReadOnlyList<SubagentEntry> SubagentsOf(SessionRef session) =>
        [.. Subagents.Values.Where(e => e.Ref.DeviceId == session.DeviceId && e.Ref.SessionId == session.SessionId).OrderBy(e => e.Record, Comparer<SubagentRecord>.Create(SubagentView.CompareByRecency))];

    /// <summary>某个会话当前的目标(没有 / 已清除为 null)。</summary>
    public ActiveGoal? GoalOf(SessionRef session) => Goals.GetValueOrDefault(session)?.Goal;

    public static readonly ConversationState Empty = new(
        ImmutableDictionary<SessionRef, SessionView>.Empty,
        ImmutableDictionary<RunRef, RunView>.Empty,
        ImmutableDictionary<string, DeviceInteractions>.Empty);

    public DeviceInteractions InteractionsOf(string deviceId) => Interactions.GetValueOrDefault(deviceId) ?? DeviceInteractions.Empty;
}
