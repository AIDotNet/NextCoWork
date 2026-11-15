using System.Collections.Immutable;

namespace Ncw.Core.Domain;

/// <summary>
/// 活动收件箱里一条的种类(docs/design/03 §3「活动」页)。声明顺序没有意义;排序见 <see cref="ActivityInboxView.Priority"/>。
/// </summary>
public enum InboxKind
{
    /// <summary>工具审批(执行命令、写文件……)。危险的另有 <see cref="InboxEntry.Dangerous"/> 标记。</summary>
    ToolApproval,

    /// <summary>Agent 在等用户回答的提问(含小组件提交)。</summary>
    Question,

    /// <summary>计划待批准。</summary>
    PlanApproval,

    /// <summary>目标提案待确认。</summary>
    GoalProposal,

    /// <summary>工作流待批准运行。</summary>
    WorkflowApproval,

    /// <summary>设备级的确认(SSH 主机指纹、凭据提示):不属于任何会话。</summary>
    DeviceApproval,

    /// <summary>Run 正常完成的通知。</summary>
    RunCompleted,

    /// <summary>Run 失败的通知。</summary>
    RunFailed,
}

/// <summary>某台设备上一条挂起的交互(控制端把 <c>interactions</c> 主题的镜像翻成这个形状;Core 不认识协议 DTO)。</summary>
/// <param name="RunId">交互所属的 Run;设备级交互是 <c>env:…</c> 这样的作用域,不在任何活动 Run 里。</param>
/// <param name="CreatedAtMs">设备登记这条交互的时间(Unix 毫秒)。</param>
/// <param name="SessionHint">交互自己带的会话 id(目标提案有);其他种类靠 Run 反查。</param>
public sealed record InboxInteraction(string Id, string RunId, InboxKind Kind, long CreatedAtMs, bool Dangerous = false, string? SessionHint = null);

/// <summary>控制端见过的一个已结束的 Run(只含正常完成和失败;被用户中止的不通知)。</summary>
/// <param name="ObservedAt">控制端第一次看到它结束的时间(设备广播里没有结束时间,通知按这个排序)。</param>
public sealed record InboxFinishedRun(string RunId, string SessionId, bool Failed, DateTimeOffset ObservedAt);

/// <summary>一台设备喂给收件箱的全部事实。</summary>
/// <param name="Index">设备最近一次的活动 Run 索引(把 Run 对应到会话 / 项目);从没收到过为 null。</param>
public sealed record InboxDevice(
    string DeviceId,
    string Name,
    DeviceConnection Connection,
    ActiveRunIndex? Index,
    ImmutableArray<InboxInteraction> Interactions,
    ImmutableArray<InboxFinishedRun> Finished);

/// <summary>
/// 收件箱的一条。<see cref="Unknown"/>:设备断开了,这条是上次看到的样子,现在是否还在等待未知(规则 7)——
/// 不能当成已经处理,也不能让用户在这里回答(回答要带设备当前的版本号)。
/// </summary>
/// <param name="Key">稳定 key(<c>i:交互 id</c> / <c>r:Run id</c>),界面据此对齐行。</param>
/// <param name="SessionId">所属会话;设备级交互或查不到时为 null。</param>
/// <param name="WorkspaceId">所属项目;查不到为 null。</param>
public sealed record InboxEntry(
    string DeviceId,
    string Key,
    InboxKind Kind,
    string? InteractionId,
    string RunId,
    string? SessionId,
    string? WorkspaceId,
    DateTimeOffset At,
    bool Dangerous,
    bool Unknown)
{
    /// <summary>需要用户处理(而不是只读的通知)。</summary>
    public bool IsPending => Kind is not (InboxKind.RunCompleted or InboxKind.RunFailed);

    /// <summary>现在就能处理:要处理的,而且设备连着。</summary>
    public bool IsActionable => IsPending && !Unknown;
}

/// <summary>一台设备在收件箱里的分组。</summary>
/// <param name="Unknown">设备没连上:这一组里的待办状态未知。</param>
/// <param name="RunningRuns">设备上正在运行的顶层 Run 数(断开时是上次的值,<see cref="Unknown"/> 为真时界面不显示数字)。</param>
public sealed record InboxGroup(string DeviceId, string Name, DeviceConnection Connection, bool Unknown, int RunningRuns, ImmutableArray<InboxEntry> Entries)
{
    public int ActionableCount => Entries.Count(static e => e.IsActionable);
}

/// <summary>收件箱的完整派生结果。</summary>
public sealed record ActivityInbox(ImmutableArray<InboxGroup> Groups)
{
    public static readonly ActivityInbox Empty = new(ImmutableArray<InboxGroup>.Empty);

    /// <summary>底部标签栏的角标:现在能处理的待办总数(断线设备上的不算,它们是「未知」)。</summary>
    public int BadgeCount => Groups.Sum(static g => g.ActionableCount);

    /// <summary>有设备断开(界面在收件箱顶部提醒「部分设备状态未知」)。</summary>
    public bool HasUnknown => Groups.Any(static g => g.Unknown);

    /// <summary>没有任何分组:所有设备都连着且没有待办 / 通知。</summary>
    public bool IsEmpty => Groups.IsEmpty;

    public IEnumerable<InboxEntry> Entries => Groups.SelectMany(static g => g.Entries);
}

/// <summary>
/// 「待我处理」的唯一派生函数(AGENTS.md §3 规则 3):把各设备上挂起的交互、已结束的 Run、连接状态聚合成收件箱,
/// 按设备分组。输入来自 Store 镜像(<c>interactions</c> 全量、<c>activeRuns</c> 权威广播、连接状态),视图只显示这里的结果,不另算。
/// </summary>
public static class ActivityInboxView
{
    /// <summary>
    /// <list type="bullet">
    /// <item>断线设备:分组保留并标 <see cref="InboxGroup.Unknown"/>,它的待办条目标 <see cref="InboxEntry.Unknown"/>、不计入角标、不判成已处理;已结束的通知是终态事实,保留(规则 6)。</item>
    /// <item>连着的设备没有任何条目就不出现;断线的设备即使没有条目也出现(用户要知道它的状态未知)。</item>
    /// <item>组内:待办在前(危险审批 → 工具审批 / 设备确认 → 提问 → 计划 / 工作流 → 目标,同级先登记的在前),通知在后(失败在前,新的在前)。</item>
    /// <item>组间:有现在能处理的待办的在前(最早的那条先),然后按名字、设备 id。</item>
    /// </list>
    /// </summary>
    /// <param name="dismissed">已读(用户划掉)的通知 key。待办不可划掉:它只会因设备上被处理而消失。</param>
    public static ActivityInbox Build(IEnumerable<InboxDevice> devices, IReadOnlySet<string>? dismissed = null)
    {
        var groups = new List<InboxGroup>();
        foreach (var device in devices)
        {
            var connected = device.Connection == DeviceConnection.Connected;
            var entries = new List<InboxEntry>();
            var lookup = RunLookup.Of(device.Index);

            foreach (var item in device.Interactions)
            {
                var (session, workspace) = lookup.Resolve(item.RunId, item.SessionHint);
                entries.Add(new InboxEntry(
                    device.DeviceId, "i:" + item.Id, item.Kind, item.Id, item.RunId, session, workspace,
                    DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAtMs), item.Dangerous, Unknown: !connected));
            }

            foreach (var run in device.Finished)
            {
                var key = "r:" + run.RunId;
                if (dismissed?.Contains(key) == true)
                {
                    continue;
                }

                entries.Add(new InboxEntry(
                    device.DeviceId, key, run.Failed ? InboxKind.RunFailed : InboxKind.RunCompleted, null, run.RunId, run.SessionId,
                    lookup.Resolve(run.RunId, run.SessionId).Workspace, run.ObservedAt, Dangerous: false, Unknown: false));
            }

            if (entries.Count == 0 && connected)
            {
                continue;
            }

            entries.Sort(CompareEntries);
            groups.Add(new InboxGroup(device.DeviceId, device.Name, device.Connection, !connected, device.Index?.Runs.Length ?? 0, [.. entries]));
        }

        groups.Sort(CompareGroups);
        return new ActivityInbox([.. groups]);
    }

    /// <summary>条目的优先级(越小越靠前):危险审批 0、工具审批 / 设备确认 1、提问 2、计划 / 工作流 3、目标 4、失败通知 5、完成通知 6。</summary>
    public static int Priority(InboxEntry entry) => entry.Kind switch
    {
        InboxKind.ToolApproval => entry.Dangerous ? 0 : 1,
        InboxKind.DeviceApproval => 1,
        InboxKind.Question => 2,
        InboxKind.PlanApproval or InboxKind.WorkflowApproval => 3,
        InboxKind.GoalProposal => 4,
        InboxKind.RunFailed => 5,
        _ => 6,
    };

    private static int CompareEntries(InboxEntry a, InboxEntry b)
    {
        // 待办在前,通知在后
        var pending = b.IsPending.CompareTo(a.IsPending);
        if (pending != 0)
        {
            return pending;
        }

        var priority = Priority(a).CompareTo(Priority(b));
        if (priority != 0)
        {
            return priority;
        }

        // 待办:先登记的先处理;通知:新的在前
        var time = a.IsPending ? a.At.CompareTo(b.At) : b.At.CompareTo(a.At);
        return time != 0 ? time : string.CompareOrdinal(a.Key, b.Key);
    }

    private static int CompareGroups(InboxGroup a, InboxGroup b)
    {
        var aActionable = a.ActionableCount > 0;
        var bActionable = b.ActionableCount > 0;
        if (aActionable != bActionable)
        {
            return aActionable ? -1 : 1;
        }

        if (aActionable)
        {
            var oldest = OldestActionable(a).CompareTo(OldestActionable(b));
            if (oldest != 0)
            {
                return oldest;
            }
        }

        var name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return name != 0 ? name : string.CompareOrdinal(a.DeviceId, b.DeviceId);
    }

    private static DateTimeOffset OldestActionable(InboxGroup group) => group.Entries.Where(static e => e.IsActionable).Min(static e => e.At);

    /// <summary>Run id → (会话, 项目):顶层 Run 直接查;子 Run(子代理)的审批归到父会话。</summary>
    private readonly struct RunLookup(Dictionary<string, (string Session, string Workspace)> byRun)
    {
        public static RunLookup Of(ActiveRunIndex? index)
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            if (index is not null)
            {
                foreach (var run in index.Runs)
                {
                    map[run.RunId] = (run.SessionId, run.WorkspaceId);
                }

                foreach (var sub in index.Subagents)
                {
                    map.TryAdd(sub.RunId, (sub.SessionId, sub.WorkspaceId));
                }
            }

            return new RunLookup(map);
        }

        public (string? Session, string? Workspace) Resolve(string runId, string? sessionHint)
        {
            if (byRun.TryGetValue(runId, out var found))
            {
                return found;
            }

            return (sessionHint, null);
        }
    }
}
