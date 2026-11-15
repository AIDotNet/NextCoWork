using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一张子代理卡片在某台设备上的身份:<b>父会话 + <c>Task</c> 调用 id</b>(key 带 <c>deviceId</c>,AGENTS.md §3 规则 1)。
/// 子 Run id 是派生出来的、会换(重放、转后台),卡片的身份是「哪条会话里的哪一次调用」。
/// </summary>
public readonly record struct SubagentRef(string DeviceId, string SessionId, string CallId);

/// <summary>
/// 卡片上 Core 的 <see cref="SubagentRecord"/> 装不下的那些遥测与展示字段(事件带来的:阶段、工具出错数、用量、上下文占用、重试提示;回执带来的:摘要、颜色、错误)。
/// 状态判定(谁赢、终态单调)只看 <see cref="SubagentRecord"/>,这里的字段都是「最后一次听到的值」。
/// </summary>
public sealed record SubagentTelemetry
{
    public static readonly SubagentTelemetry Empty = new();

    /// <summary><c>starting</c> / <c>background</c> / <c>tool</c> / <c>thinking</c> / <c>finishing</c>;没有 = 不知道。</summary>
    public string? Phase { get; init; }

    public int ToolErrors { get; init; }

    public UsageDto? Usage { get; init; }

    public SubagentContextUsageDto? ContextUsage { get; init; }

    public SubagentNoticeDto? Notice { get; init; }

    public string? Model { get; init; }

    public string? Color { get; init; }

    /// <summary>前 240 字的预览(全文在子会话的最后一条助手消息里,用 <c>SubagentActions.ReadReportAsync</c> 取)。</summary>
    public string? Summary { get; init; }

    public ErrorDto? Error { get; init; }
}

/// <summary>
/// 一张子代理卡片的镜像。事实在设备上(父 Run 日志里的 <c>subagent_*</c> 事件 + 父会话转录里的持久回执 + 权威广播的 <c>subagents</c> 半边);这里是「到目前为止听到的」。
/// <see cref="Record"/> 的合并走 Core 的 <see cref="SubagentView"/>(终态压过运行中,不论哪一份更晚到;重放的旧 start 不能让终态卡片复活)。
/// </summary>
/// <param name="StartedAtRev">创建这份镜像时已知的 activeRuns 版本:只有更新的权威广播才能据此对账(旧广播里没有刚起的子 Run 是正常的)。</param>
/// <param name="Guessed">
/// 终态是权威广播对账<b>推断</b>出来的(广播里已经没有这个子 Run,但 <c>subagent_end</c> / 回执还没到):界面只需要「不再运行」;
/// 真实的结局(事件 / 回执 / 父会话的 <c>subagent.stopped</c>)到达时替换这个推断,不受「第一个终态胜出」的限制。
/// </param>
/// <param name="Unknown">设备断开了:状态未知,既不判结束也不继续计时(规则 7)。收到这张卡片的任何新消息、或重连后的权威广播里还有它,就恢复。</param>
public sealed record SubagentEntry(SubagentRef Ref, SubagentRecord Record, SubagentTelemetry Telemetry, long StartedAtRev, bool Guessed = false, bool Unknown = false)
{
    /// <summary>
    /// 界面上这张卡片的样子(Core 的唯一派生函数)。设备断开时计时冻结在最后一次听到动静的那一刻。
    /// </summary>
    public SubagentViewState ViewAt(DateTimeOffset now) =>
        SubagentView.Of(Record, Unknown ? Record.LastEventAt ?? Record.StartedAt : now);

    public bool IsTerminal => SubagentView.IsTerminal(Record.Status);
}

/// <summary>
/// 一个会话的目标镜像(<c>goal</c> 主题 + <c>goal.get</c> 的结果,key 带 <c>deviceId</c>)。<see cref="Goal"/> 为 null 是<b>墓碑</b>(清掉了):
/// 带着版本号留下,所以乱序到达的旧事件不能让清掉的目标复活。<see cref="Rev"/> 是设备上目标变化的单调版本号,只收不更旧的。
/// </summary>
public sealed record GoalEntry(ActiveGoal? Goal, long Rev, GoalClearReason? Cleared = null, bool Unknown = false);
