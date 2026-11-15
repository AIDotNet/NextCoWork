using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>收件箱的一条(待办、通知,或「已在另一台设备上处理」的墓碑)。只带结构化数据,界面按 <see cref="Kind"/> 写文字。</summary>
public sealed partial class InboxEntryViewModel : ObservableObject
{
    internal InboxEntryViewModel(InboxEntry entry, InteractionDto? interaction, string? projectName, string? sessionTitle, string deviceName, ProjectRef? project)
    {
        Entry = entry;
        Interaction = interaction;
        ProjectName = projectName;
        SessionTitle = sessionTitle;
        DeviceName = deviceName;
        Project = project;
        if (interaction is ToolPermissionInteractionDto tool)
        {
            ToolName = tool.ToolName;
            Target = TranscriptBuilder.TargetOf(tool.ToolName, tool.InputJson);
            EnvLabel = $"{deviceName} · {tool.Env.Name}";
        }
        else if (interaction is PlanApprovalInteractionDto plan)
        {
            Detail = FirstLine(plan.Plan);
        }
        else if (interaction is GoalProposalInteractionDto goal)
        {
            Detail = goal.Condition;
        }
        else if (interaction is AskUserInteractionDto ask)
        {
            Detail = ask.Questions.FirstOrDefault()?.Question;
        }
        else if (interaction is WidgetSubmitInteractionDto widget)
        {
            Detail = widget.Title;
        }
        else if (interaction is SshHostKeyInteractionDto hostKey)
        {
            EnvLabel = $"{deviceName} · {hostKey.Env.Name}";
            Detail = hostKey.HostName;
        }
        else if (interaction is SshCredentialInteractionDto credential)
        {
            EnvLabel = $"{deviceName} · {credential.Env.Name}";
        }
    }

    public InboxEntry Entry { get; internal set; }

    /// <summary>Store 里的那份交互(通知为 null)。</summary>
    public InteractionDto? Interaction { get; }

    public string Key => Entry.Key;

    public InboxKind Kind => Entry.Kind;

    public string DeviceName { get; }

    public string? ProjectName { get; }

    public string? SessionTitle { get; }

    public ProjectRef? Project { get; }

    /// <summary>工具审批:工具名和被审批调用的一行摘要(命令 / 路径)。</summary>
    public string? ToolName { get; }

    public string? Target { get; }

    /// <summary>执行环境标识(设备 · 环境):审批必须带它(AGENTS.md §5)。</summary>
    public string? EnvLabel { get; }

    /// <summary>计划的第一行、目标条件、提问的第一个问题等一行说明。</summary>
    public string? Detail { get; }

    public DateTimeOffset At => Entry.At;

    public bool IsPending => Entry.IsPending;

    public bool IsDangerous => Entry.Dangerous;

    /// <summary>设备断开,这条是上次看到的样子,是否还在等待未知(规则 7)。</summary>
    public bool IsUnknown => Entry.Unknown;

    public bool IsNotification => !Entry.IsPending;

    /// <summary>待办已经不在了而且不是本机处理的:显示「已在另一台设备上处理」。</summary>
    [ObservableProperty] public partial bool IsHandledElsewhere { get; internal set; }

    /// <summary>可以直接划掉:通知和墓碑(待办只会因设备上被处理而消失)。</summary>
    public bool CanDismiss => IsNotification || IsHandledElsewhere;

    /// <summary>现在就在等用户处理(已经被别处处理的墓碑不是)。卡片的醒目边框只给它。</summary>
    public bool NeedsAction => IsPending && !IsHandledElsewhere && !IsUnknown;

    public bool ShowsDanger => IsDangerous && NeedsAction;

    /// <summary>淡显:设备断开(状态未知)或已在别处处理。</summary>
    public bool IsDimmed => IsUnknown || IsHandledElsewhere;

    partial void OnIsHandledElsewhereChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDismiss));
        OnPropertyChanged(nameof(NeedsAction));
        OnPropertyChanged(nameof(ShowsDanger));
        OnPropertyChanged(nameof(IsDimmed));
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(static l => !l.StartsWith('#')) ?? text.Trim();
}

/// <summary>收件箱里一台设备的分组。</summary>
public sealed partial class InboxGroupViewModel : ObservableObject
{
    internal InboxGroupViewModel(string deviceId) => DeviceId = deviceId;

    public string DeviceId { get; }

    [ObservableProperty] public partial string Name { get; private set; } = "";

    [ObservableProperty] public partial DeviceKind Kind { get; private set; }

    [ObservableProperty] public partial DeviceConnection Connection { get; private set; }

    /// <summary>设备没连上:这一组的待办状态未知(规则 7)。</summary>
    [ObservableProperty] public partial bool IsUnknown { get; private set; }

    [ObservableProperty] public partial int RunningRuns { get; private set; }

    [ObservableProperty] public partial int ActionableCount { get; private set; }

    public ObservableCollection<InboxEntryViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    internal void Update(string name, DeviceKind kind, DeviceConnection connection, bool unknown, int running, int actionable, IReadOnlyList<InboxEntryViewModel> entries)
    {
        Name = name;
        Kind = kind;
        Connection = connection;
        IsUnknown = unknown;
        RunningRuns = running;
        ActionableCount = actionable;
        if (!entries.SequenceEqual(Entries))
        {
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }
        }

        OnPropertyChanged(nameof(HasEntries));
    }
}

/// <summary>
/// 活动收件箱(手机壳「收件箱」标签,docs/design/03 §3):所有设备上「待我处理」的聚合视图,按设备分组。
/// 聚合与排序是 Core 的 <see cref="ActivityInboxView"/>(纯函数,Agent 与客户端共用),这里只把 Store 镜像
/// (<c>interactions</c> 全量、<c>activeRuns</c> 权威广播、各设备连接状态、已结束的 Run)翻成它的输入,再把结果映射成行;视图不另算。
/// <para>
/// 本类持有的只有纯 UI 状态:已读的通知、「已在另一台设备上处理」的墓碑、通知第一次被看到的时间。
/// 待办在设备上被处理就从 Store 里消失:不是本机回答的(<see cref="InteractionLedger"/>),就留一条墓碑,直到用户划掉。
/// 设备断开时它的待办标 <see cref="InboxEntryViewModel.IsUnknown"/>,不当成已处理(规则 7),也不让用户在这里回答。
/// </para>
/// </summary>
public sealed partial class InboxViewModel : ObservableObject
{
    private const int MaxTombstones = 20;

    private readonly ClientStores _stores;
    private readonly InteractionLedger _ledger;
    private readonly MobileNavigationViewModel _navigation;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private readonly Dictionary<RunRef, DateTimeOffset> _observed = [];
    private readonly Dictionary<string, InboxGroupViewModel> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InboxEntryViewModel> _tombstones = new(StringComparer.Ordinal);
    private Dictionary<string, InboxEntryViewModel> _lastPending = [];

    public InboxViewModel(ClientStores stores, MobileNavigationViewModel navigation, InteractionLedger? ledger = null, TimeProvider? clock = null)
    {
        _stores = stores;
        _navigation = navigation;
        _ledger = ledger ?? new InteractionLedger();
        _clock = clock ?? TimeProvider.System;
        stores.Conversations.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        stores.Selection.Changed += OnSelectionChanged;
        Refresh();
    }

    public ObservableCollection<InboxGroupViewModel> Groups { get; } = [];

    /// <summary>底部标签栏的角标:现在能处理的待办总数(断线设备上的不算)。</summary>
    [ObservableProperty] public partial int BadgeCount { get; private set; }

    /// <summary>有设备断开:收件箱顶部提醒「部分设备状态未知」。</summary>
    [ObservableProperty] public partial bool HasUnknown { get; private set; }

    /// <summary>没有任何分组,也没有墓碑:所有设备都连着且没有待办 / 通知。</summary>
    [ObservableProperty] public partial bool IsEmpty { get; private set; } = true;

    /// <summary>当前 Core 的聚合结果(测试和视图的断言用)。</summary>
    public ActivityInbox Current { get; private set; } = ActivityInbox.Empty;

    // ───────────────────────── 意图 ─────────────────────────

    /// <summary>点一条:进入对应会话并定位到那张卡片。待办已经不在了 → 变成「已在另一台设备上处理」而不是白白跳转;设备级条目去设备页。</summary>
    [RelayCommand]
    private void Open(InboxEntryViewModel item)
    {
        if (item.IsHandledElsewhere)
        {
            return; // 墓碑没有可去的地方,划掉它
        }

        if (item.IsPending && item.Entry.InteractionId is { } id && !IsStillPending(item.Entry.DeviceId, id))
        {
            Tombstone(item);
            Refresh();
            return;
        }

        if (item.Entry.SessionId is not { } sessionId)
        {
            _navigation.SelectTabCommand.Execute(MobileTab.Devices);
            return;
        }

        if (item.Project is not { } project)
        {
            return;
        }

        _navigation.OpenSession(project, sessionId, item.IsPending ? item.Key : null);
        if (item.IsNotification)
        {
            Dismiss(item); // 点开通知就是已读
        }
    }

    /// <summary>划掉一条通知或墓碑。</summary>
    [RelayCommand]
    private void Dismiss(InboxEntryViewModel item)
    {
        if (item.IsHandledElsewhere)
        {
            _tombstones.Remove(item.Key);
        }
        else if (item.IsNotification)
        {
            _dismissed.Add(item.Key);
        }
        else
        {
            return;
        }

        Refresh();
    }

    /// <summary>全部已读:划掉所有通知和墓碑。</summary>
    [RelayCommand]
    private void DismissAll()
    {
        foreach (var entry in Groups.SelectMany(static g => g.Entries).Where(static e => e.CanDismiss).ToList())
        {
            if (entry.IsHandledElsewhere)
            {
                _tombstones.Remove(entry.Key);
            }
            else
            {
                _dismissed.Add(entry.Key);
            }
        }

        Refresh();
    }

    private bool IsStillPending(string deviceId, string interactionId) =>
        _stores.Conversations.State.InteractionsOf(deviceId).Pending.Any(i => i.Id == interactionId);

    private void OnSelectionChanged()
    {
        // 打开了某个会话:它已经结束的 Run 的通知算已读
        if (_stores.Selection.State is { Project: { } project, SessionId: { } sessionId })
        {
            var changed = false;
            foreach (var (run, view) in _stores.Conversations.State.Runs)
            {
                if (run.DeviceId == project.DeviceId && view.SessionId == sessionId && _dismissed.Add("r:" + run.RunId))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                Refresh();
            }
        }
    }

    // ───────────────────────── 派生 ─────────────────────────

    private void Refresh()
    {
        var workspace = _stores.Workspace.State;
        var conversations = _stores.Conversations.State;
        var now = _clock.GetUtcNow();

        var devices = ImmutableArray.CreateBuilder<InboxDevice>();
        var interactionsById = new Dictionary<string, InteractionDto>(StringComparer.Ordinal);
        foreach (var (deviceId, device) in workspace.Devices)
        {
            var interactions = conversations.InteractionsOf(deviceId).Pending
                .Select(i =>
                {
                    interactionsById[i.Id] = i;
                    return ToFact(i);
                })
                .ToImmutableArray();

            var finished = ImmutableArray.CreateBuilder<InboxFinishedRun>();
            foreach (var (run, view) in conversations.Runs)
            {
                if (run.DeviceId != deviceId || view.Phase is not (RunPhase.Done or RunPhase.Error))
                {
                    continue;
                }

                if (!_observed.TryGetValue(run, out var observed))
                {
                    _observed[run] = observed = now;
                }

                finished.Add(new InboxFinishedRun(run.RunId, view.SessionId, view.Phase == RunPhase.Error, observed));
            }

            devices.Add(new InboxDevice(deviceId, device.Name, device.Connection, device.Index, interactions, finished.ToImmutable()));
        }

        var inbox = ActivityInboxView.Build(devices.ToImmutable(), _dismissed);
        Current = inbox;

        // 消失的待办:设备还连着、不是本机回答的 → 「已在另一台设备上处理」(先于建行,这一轮就能显示出来)
        var pendingKeys = inbox.Entries.Where(static e => e.IsPending).Select(static e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, old) in _lastPending)
        {
            if (!pendingKeys.Contains(key)
                && old.Entry.InteractionId is { } id
                && !_ledger.WasAnsweredHere(id)
                && workspace.Devices.TryGetValue(old.Entry.DeviceId, out var device) && device.Connection == DeviceConnection.Connected)
            {
                Tombstone(old);
            }
        }

        // 映射成行(同一个 key 复用行 ViewModel 的墓碑标记不在这里,墓碑单独保存)
        var pendingNow = new Dictionary<string, InboxEntryViewModel>(StringComparer.Ordinal);
        var groupVms = new List<InboxGroupViewModel>();
        foreach (var group in inbox.Groups)
        {
            var info = workspace.Devices.GetValueOrDefault(group.DeviceId);
            var rows = new List<InboxEntryViewModel>();
            foreach (var entry in group.Entries)
            {
                var row = Map(entry, group.Name, interactionsById, workspace);
                rows.Add(row);
                if (entry.IsPending)
                {
                    pendingNow[entry.Key] = row;
                }
            }

            // 墓碑放在这台设备的待办之后、通知之前(它们是刚刚发生的事)
            var tombs = _tombstones.Values.Where(t => t.Entry.DeviceId == group.DeviceId).OrderByDescending(static t => t.At).ToList();
            rows.InsertRange(rows.TakeWhile(static r => r.IsPending).Count(), tombs);

            if (!_groups.TryGetValue(group.DeviceId, out var vm))
            {
                _groups[group.DeviceId] = vm = new InboxGroupViewModel(group.DeviceId);
            }

            vm.Update(group.Name, info?.Kind ?? DeviceKind.Remote, group.Connection, group.Unknown, group.RunningRuns, group.ActionableCount, rows);
            groupVms.Add(vm);
        }

        _lastPending = pendingNow;

        // 墓碑所在设备没有分组(这台设备上已经没有别的条目了):补一个分组承载它
        foreach (var tomb in _tombstones.Values.Where(t => groupVms.All(g => g.DeviceId != t.Entry.DeviceId)).GroupBy(static t => t.Entry.DeviceId))
        {
            var info = workspace.Devices.GetValueOrDefault(tomb.Key);
            if (!_groups.TryGetValue(tomb.Key, out var vm))
            {
                _groups[tomb.Key] = vm = new InboxGroupViewModel(tomb.Key);
            }

            vm.Update(info?.Name ?? tomb.Key, info?.Kind ?? DeviceKind.Remote, info?.Connection ?? DeviceConnection.Offline, info is null || info.Connection != DeviceConnection.Connected, info?.Index?.Runs.Length ?? 0, 0,
                [.. tomb.OrderByDescending(static t => t.At)]);
            groupVms.Add(vm);
        }

        if (!groupVms.SequenceEqual(Groups))
        {
            Groups.Clear();
            foreach (var group in groupVms)
            {
                Groups.Add(group);
            }
        }

        BadgeCount = inbox.BadgeCount;
        HasUnknown = inbox.HasUnknown;
        IsEmpty = Groups.Count == 0;
    }

    private void Tombstone(InboxEntryViewModel row)
    {
        if (_tombstones.ContainsKey(row.Key))
        {
            return;
        }

        row.IsHandledElsewhere = true;
        _tombstones[row.Key] = row;
        while (_tombstones.Count > MaxTombstones)
        {
            _tombstones.Remove(_tombstones.OrderBy(static t => t.Value.At).First().Key);
        }
    }

    private InboxEntryViewModel Map(InboxEntry entry, string deviceName, Dictionary<string, InteractionDto> interactions, WorkspaceState workspace)
    {
        var interaction = entry.InteractionId is { } id && interactions.TryGetValue(id, out var found) ? found : null;
        var session = entry.SessionId is { } sid
            ? workspace.Sessions.FirstOrDefault(s => s.Id == sid && s.Project.DeviceId == entry.DeviceId && (entry.WorkspaceId is null || s.Project.WorkspaceId == entry.WorkspaceId))
            : null;
        ProjectRef? project = entry.WorkspaceId is { } workspaceId ? new ProjectRef(entry.DeviceId, workspaceId) : session?.Project;
        var projectName = project is { } p && workspace.Projects.TryGetValue(p, out var info) ? info.Name : null;
        return new InboxEntryViewModel(entry, interaction, projectName, session?.Title, deviceName, project);
    }

    /// <summary>协议 DTO → Core 的收件箱事实(Core 不认识协议类型)。</summary>
    private static InboxInteraction ToFact(InteractionDto i) => i switch
    {
        ToolPermissionInteractionDto t => new InboxInteraction(t.Id, t.RunId, InboxKind.ToolApproval, t.CreatedAt, t.Risk == "dangerous" || t.Destructive),
        AskUserInteractionDto => new InboxInteraction(i.Id, i.RunId, InboxKind.Question, i.CreatedAt),
        WidgetSubmitInteractionDto => new InboxInteraction(i.Id, i.RunId, InboxKind.Question, i.CreatedAt),
        PlanApprovalInteractionDto => new InboxInteraction(i.Id, i.RunId, InboxKind.PlanApproval, i.CreatedAt),
        GoalProposalInteractionDto g => new InboxInteraction(i.Id, i.RunId, InboxKind.GoalProposal, i.CreatedAt, SessionHint: g.SessionId),
        WorkflowApprovalInteractionDto => new InboxInteraction(i.Id, i.RunId, InboxKind.WorkflowApproval, i.CreatedAt),
        _ => new InboxInteraction(i.Id, i.RunId, InboxKind.DeviceApproval, i.CreatedAt),
    };
}
