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
/// 后台子代理的「汇报给主代理」进度(2.x 的 <c>SubagentReportStatus</c>):<c>pending → injecting → reported / blocked</c>。
/// 只有后台子代理有这一步;前台子代理的结果就是它那条 <c>tool_result</c>,同步回到主代理,不存在「回传」。
/// </summary>
public enum SubagentReportStatus
{
    None,
    Pending,
    Injecting,
    Reported,
    Blocked,
}

/// <summary>
/// 「键存在但值为空 = 清除;键不存在 = 保持」(<c>subagent_update</c> 的字段语义,05 §5)。<see cref="IsSet"/> 为假 = 保持原值;
/// 为真而 <see cref="Value"/> 为 null = 清除。
/// </summary>
public readonly record struct Patch<T>(bool IsSet, T? Value)
{
    public static Patch<T> Keep => default;

    public static Patch<T> Set(T value) => new(true, value);

    public static Patch<T> Clear => new(true, default);

    /// <summary>把补丁应用到原值上。</summary>
    public T? Apply(T? current) => IsSet ? Value : current;
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
    string? CurrentTool)
{
    /// <summary>子 Run 的 id(卡片与子 Run、广播里的 <c>subagents</c> 对账用)。</summary>
    public string? ChildRunId { get; init; }

    /// <summary>子会话的 id。<b>必须由 Agent 带过来,不许拼</b>:id 是递归拼出来的,想从字符串里反推父亲会切出爷爷(04 §1)。没有它(旧转录)的卡片点开是空的,仅此而已。</summary>
    public string? ChildSessionId { get; init; }

    public bool Background { get; init; }

    /// <summary>库里回执上的汇报进度;null = 回执上没写。</summary>
    public SubagentReportStatus? ReportStatus { get; init; }

    /// <summary>「正在做什么」的目标(文件路径、命令……),和 <see cref="CurrentTool"/> 一起显示。</summary>
    public string? CurrentTarget { get; init; }

    /// <summary>
    /// 最后一次从这个子代理听到任何动静的时间。区分「卡死」和「在跑一件慢活」的唯一可靠信号;终态但缺 <see cref="EndedAt"/> 的卡片(从库里回执重建)用它收口计时。
    /// </summary>
    public DateTimeOffset? LastEventAt { get; init; }
}

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
    TimeSpan Elapsed)
{
    public string? CurrentTarget { get; init; }

    /// <summary>汇报进度。前台子代理恒为 <see cref="SubagentReportStatus.None"/>。</summary>
    public SubagentReportStatus Report { get; init; }

    /// <summary>能不能点开它的只读会话(旧转录没有 <c>ChildSessionId</c>)。</summary>
    public bool CanOpen { get; init; }

    /// <summary>能不能转后台:只对还在跑的前台子代理成立。</summary>
    public bool CanBackground { get; init; }

    public bool Background { get; init; }
}

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
        // 终态但缺结束时间:用最后一次动静收口,再不行就按零耗时——绝不拿 now 去算一个终态子代理的耗时,那会让它的计时一直走。
        var end = terminal ? record.EndedAt ?? record.LastEventAt ?? record.StartedAt : now;
        var elapsed = end > record.StartedAt ? end - record.StartedAt : TimeSpan.Zero;
        var running = record.Status == SubagentStatus.Running;

        return new SubagentViewState(
            AgentType: record.AgentType,
            Description: record.Description,
            Status: record.Status,
            // 只有运行中才显示当前工具
            CurrentTool: running ? record.CurrentTool : null,
            ToolCalls: record.ToolCalls,
            IsActive: !terminal,
            CanStop: !terminal,
            IsTerminal: terminal,
            Elapsed: elapsed)
        {
            CurrentTarget = running ? record.CurrentTarget : null,
            Report = ReportOf(record),
            CanOpen = record.ChildSessionId is { Length: > 0 },
            CanBackground = running && !record.Background,
            Background = record.Background,
        };
    }

    /// <summary>
    /// 汇报进度的唯一判定。<b>只对后台子代理成立</b>:前台子代理的结果就是它那条 <c>tool_result</c>,同步回到主代理,不存在「回传」这一步
    /// (2.x 曾给每张跑完的前台卡片都挂一句「结果已汇报给主代理」,把后台专属的信号抹平了)。
    /// </summary>
    public static SubagentReportStatus ReportOf(SubagentRecord record)
    {
        if (!record.Background)
        {
            return SubagentReportStatus.None;
        }

        if (record.ReportStatus is { } saved)
        {
            return saved;
        }

        return record.Status switch
        {
            SubagentStatus.Done => SubagentReportStatus.Pending,
            SubagentStatus.Running or SubagentStatus.Queued => SubagentReportStatus.None,
            _ => SubagentReportStatus.Reported,
        };
    }

    /// <summary>
    /// 表里没有这张卡片时显示什么:<c>Task</c> 的参数还在流(<paramref name="argumentsStreaming"/>,<c>subagent_start</c> 还没到)= 排队 / 起步;
    /// 否则是旧转录里的子代理块——它们同样没有记录、而早就跑完了,所以兜底 <see cref="SubagentStatus.Done"/>。★ 不能拿「记录为空」代替这个参数。
    /// </summary>
    public static SubagentStatus WhenMissing(bool argumentsStreaming) => argumentsStreaming ? SubagentStatus.Queued : SubagentStatus.Done;

    /// <summary>「在跑的在前、同一档里新开的在前」。侧栏列表与任何以后的列表共用这一个序。</summary>
    public static int CompareByRecency(SubagentRecord left, SubagentRecord right)
    {
        var live = (right.Status == SubagentStatus.Running ? 1 : 0) - (left.Status == SubagentStatus.Running ? 1 : 0);
        return live != 0 ? live : right.StartedAt.CompareTo(left.StartedAt);
    }

    /// <summary>
    /// 两个状态里谁赢(规则 6):终态压过运行中,运行中压过排队 / 未知,<b>不论哪一份更晚到</b>。同档时取 <paramref name="saved"/>(先落下的终态不被改写)。
    /// 一个子 Run 只会从排队 / 运行走向终态,不会反过来,所以「谁是终态听谁的」永远不会错。
    /// </summary>
    public static SubagentStatus MergeStatus(SubagentStatus saved, SubagentStatus live) => Rank(saved) >= Rank(live) ? saved : live;

    /// <summary>
    /// 合并库里的回执和实时遥测(规则 6):终态压过运行中,不论来自哪一边;遥测(工具数、当前工具)取实时值。
    /// 汇报进度:<see cref="SubagentReportStatus.Reported"/> 是终点,不会被另一份退回 pending;其余以实时的为准,实时没有就用库里的。
    /// </summary>
    public static SubagentRecord Merge(SubagentRecord saved, SubagentRecord live)
    {
        var status = MergeStatus(saved.Status, live.Status);
        var terminal = IsTerminal(status);
        return live with
        {
            Status = status,
            EndedAt = terminal ? live.EndedAt ?? saved.EndedAt : null,
            CurrentTool = terminal ? null : live.CurrentTool,
            CurrentTarget = terminal ? null : live.CurrentTarget,
            ToolCalls = Math.Max(saved.ToolCalls, live.ToolCalls),
            ChildRunId = live.ChildRunId ?? saved.ChildRunId,
            ChildSessionId = live.ChildSessionId ?? saved.ChildSessionId,
            Background = live.Background || saved.Background,
            ReportStatus = saved.ReportStatus == SubagentReportStatus.Reported || live.ReportStatus == SubagentReportStatus.Reported
                ? SubagentReportStatus.Reported
                : live.ReportStatus ?? saved.ReportStatus,
            LastEventAt = Latest(saved.LastEventAt, live.LastEventAt),
        };
    }

    // ───────────────────────── 事件归约(客户端和 Agent 共用同一份规则) ─────────────────────────

    /// <summary>
    /// <c>subagent_start</c>。★ 重放的旧 start 不能让终态卡片复活(<c>attach</c> 重放日志、重连补发都会再来一次):
    /// 表里已经有终态时只补缺失的描述性字段(子会话、描述、类型),状态原样不动。
    /// </summary>
    public static SubagentRecord ApplyStart(SubagentRecord? previous, SubagentRecord started)
    {
        if (previous is null)
        {
            return started;
        }

        if (IsTerminal(previous.Status))
        {
            return previous with
            {
                ChildRunId = previous.ChildRunId ?? started.ChildRunId,
                ChildSessionId = previous.ChildSessionId ?? started.ChildSessionId,
                Background = previous.Background || started.Background,
            };
        }

        // 同一张卡片的重复 start:状态取更靠后的(Queued → Running),计时从最早的开始。
        return Merge(previous, started) with { StartedAt = previous.StartedAt <= started.StartedAt ? previous.StartedAt : started.StartedAt };
    }

    /// <summary>
    /// <c>subagent_update</c>。★ 终态之后的遥测一律忽略(重放的旧更新不能改回「正在用某个工具」);运行中才收。
    /// 字段语义:<see cref="Patch{T}"/> 的键存在但值为空 = 清除,键不存在 = 保持。
    /// </summary>
    public static SubagentRecord ApplyUpdate(SubagentRecord previous, int? toolCalls, Patch<string> currentTool, Patch<string> currentTarget, DateTimeOffset? at)
    {
        if (IsTerminal(previous.Status))
        {
            return previous;
        }

        return previous with
        {
            // 排队中收到遥测 = 已经在跑了
            Status = SubagentStatus.Running,
            ToolCalls = toolCalls is { } n ? Math.Max(previous.ToolCalls, n) : previous.ToolCalls,
            CurrentTool = currentTool.Apply(previous.CurrentTool),
            CurrentTarget = currentTarget.Apply(previous.CurrentTarget),
            LastEventAt = Latest(previous.LastEventAt, at),
        };
    }

    /// <summary><c>subagent_end</c>。第一个终态胜出:已经是终态的卡片不被另一个终态改写。</summary>
    public static SubagentRecord ApplyEnd(SubagentRecord previous, SubagentStatus status, DateTimeOffset? at)
    {
        if (!IsTerminal(status) || IsTerminal(previous.Status))
        {
            return previous;
        }

        return previous with
        {
            Status = status,
            EndedAt = at ?? previous.EndedAt ?? previous.LastEventAt,
            CurrentTool = null,
            CurrentTarget = null,
            LastEventAt = Latest(previous.LastEventAt, at),
        };
    }

    // ───────────────────────── 线上写法 ─────────────────────────

    /// <summary>回执 / 事件上的状态写法(和 2.x 一致:<c>running</c> / <c>done</c> / <c>error</c> / <c>aborted</c>,另有 <c>queued</c>)。认不出返回 null。</summary>
    public static SubagentStatus? ParseStatus(string? wire) => wire switch
    {
        "queued" => SubagentStatus.Queued,
        "running" => SubagentStatus.Running,
        "done" => SubagentStatus.Done,
        "error" => SubagentStatus.Error,
        "aborted" => SubagentStatus.Aborted,
        _ => null,
    };

    public static string ToWire(SubagentStatus status) => status switch
    {
        SubagentStatus.Queued => "queued",
        SubagentStatus.Running => "running",
        SubagentStatus.Done => "done",
        SubagentStatus.Error => "error",
        _ => "aborted",
    };

    public static SubagentReportStatus? ParseReportStatus(string? wire) => wire switch
    {
        "none" => SubagentReportStatus.None,
        "pending" => SubagentReportStatus.Pending,
        "injecting" => SubagentReportStatus.Injecting,
        "reported" => SubagentReportStatus.Reported,
        "blocked" => SubagentReportStatus.Blocked,
        _ => null,
    };

    public static string ToWire(SubagentReportStatus status) => status switch
    {
        SubagentReportStatus.Pending => "pending",
        SubagentReportStatus.Injecting => "injecting",
        SubagentReportStatus.Reported => "reported",
        SubagentReportStatus.Blocked => "blocked",
        _ => "none",
    };

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => a > b ? a : b,
    };

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
