namespace Ncw.Core.Domain;

/// <summary>子代理的状态。只会从 Queued / Running 走向终态(AGENTS.md §3 规则 6)。</summary>
public enum SubagentStatus
{
    Queued,
    Running,
    Done,
    Error,
    Aborted,
}

/// <summary>
/// 父会话里一张子代理卡片的记录(真源:父会话转录里的回执 + 子 Run 的实时遥测,见 docs/design/08-agent-runtime/05 §5)。
/// </summary>
public sealed record SubagentRecord(
    string ParentSessionId,
    string CallId,
    string AgentType,
    string Description,
    SubagentStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int ToolCalls,
    string? CurrentTool);

/// <summary>
/// 子代理在界面上的样子。所有展示点(右侧面板、转录卡片、手机详情页)都用 <see cref="SubagentView.Of"/>。
/// 这里只给结构化的值,不含任何界面文字:文案由各端按当前语言格式化。
/// </summary>
public sealed record SubagentViewState(
    string AgentType,
    string Description,
    SubagentStatus Status,
    string? CurrentTool,
    int ToolCalls,
    bool IsActive,
    bool CanStop,
    bool IsTerminal,
    TimeSpan Elapsed);

public static class SubagentView
{
    public static bool IsTerminal(SubagentStatus status) =>
        status is SubagentStatus.Done or SubagentStatus.Error or SubagentStatus.Aborted;

    /// <summary>子代理的唯一派生函数(AGENTS.md §3 规则 3)。</summary>
    /// <param name="record">合并后的记录。</param>
    /// <param name="now">当前时间(由调用方的计时器提供,运行中的子代理据此计时)。</param>
    public static SubagentViewState Of(SubagentRecord record, DateTimeOffset now)
    {
        var terminal = IsTerminal(record.Status);
        var end = terminal ? record.EndedAt ?? record.StartedAt : now;
        var elapsed = end > record.StartedAt ? end - record.StartedAt : TimeSpan.Zero;

        return new SubagentViewState(
            AgentType: record.AgentType,
            Description: record.Description,
            Status: record.Status,
            // 只有运行中才显示当前工具
            CurrentTool: record.Status == SubagentStatus.Running ? record.CurrentTool : null,
            ToolCalls: record.ToolCalls,
            IsActive: !terminal,
            CanStop: !terminal,
            IsTerminal: terminal,
            Elapsed: elapsed);
    }

    /// <summary>
    /// 合并库里的回执和实时遥测(规则 6):终态压过运行中,不论来自哪一边;遥测(工具数、当前工具)取实时值。
    /// </summary>
    public static SubagentRecord Merge(SubagentRecord saved, SubagentRecord live)
    {
        var status = Rank(saved.Status) >= Rank(live.Status) ? saved.Status : live.Status;
        var terminal = IsTerminal(status);
        return live with
        {
            Status = status,
            EndedAt = terminal ? live.EndedAt ?? saved.EndedAt : null,
            CurrentTool = terminal ? null : live.CurrentTool,
            ToolCalls = Math.Max(saved.ToolCalls, live.ToolCalls),
        };
    }

    private static int Rank(SubagentStatus status) => status switch
    {
        SubagentStatus.Queued => 0,
        SubagentStatus.Running => 1,
        _ => 2,
    };

    /// <summary>耗时的统一写法:1:15、1:02:03(与语言无关)。</summary>
    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
}
