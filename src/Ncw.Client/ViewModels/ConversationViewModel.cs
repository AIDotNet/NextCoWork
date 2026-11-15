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

/// <summary>
/// 当前打开的会话(栏 3 的内容):转录、待办、目标条、输入框。
/// 一切事实都从 <see cref="ClientStores"/> 读(会话镜像、Run、交互、子代理、目标、模型候选),这里只在 Store 变化后重新派生
/// (<see cref="TranscriptBuilder"/>)并按行 key 对齐到已有的行视图模型上;ViewModel 不订阅任何协议事件。
/// 本类持有的只有纯 UI 状态:展开 / 折叠、草稿、「已在另一台设备上处理」的提示。
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IConversationCommands _commands;
    private readonly SubagentActions? _subagents;
    private readonly GoalActions? _goals;
    private readonly TimeProvider _clock;
    private readonly Ncw.Client.Widgets.WidgetHostContext? _widgets;
    private string _lastUserText = "";
    private SessionRef? _session;
    private string? _pendingFocus;

    public ConversationViewModel(ClientStores stores, IConversationCommands commands, SubagentActions? subagents = null, GoalActions? goals = null, TimeProvider? clock = null, IComposerLookups? lookups = null,
        AttachmentTrayViewModel? attachments = null, Ncw.Client.Widgets.WidgetHostContext? widgets = null)
    {
        _widgets = widgets;
        _stores = stores;
        _commands = commands;
        _subagents = subagents;
        _goals = goals;
        _clock = clock ?? TimeProvider.System;
        Composer = new ComposerViewModel(commands, lookups, attachments: attachments);
        stores.Conversations.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        stores.Providers.Changed += Refresh;
        Refresh();
    }

    public ComposerViewModel Composer { get; }

    /// <summary>转录的行(按 key 对齐,原地更新)。视图用虚拟化的列表显示。</summary>
    public ObservableCollection<RowViewModel> Rows { get; } = [];

    /// <summary>别的设备抢先处理了的交互(纯 UI 提示,用户关掉就没了)。</summary>
    public ObservableCollection<HandledNotice> Notices { get; } = [];

    [ObservableProperty] public partial bool HasSession { get; private set; }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    /// <summary>已经选中了会话但它的快照还没到(订阅中)。</summary>
    [ObservableProperty] public partial bool IsLoading { get; private set; }

    /// <summary>设备断开:运行中的东西状态未知(规则 7),界面显示「连接中…」,不判结束、不继续计时。</summary>
    [ObservableProperty] public partial bool IsDeviceUnknown { get; private set; }

    [ObservableProperty] public partial bool IsRunning { get; private set; }

    /// <summary>上下文占用 0–100;没有数据时为 0(<see cref="HasContext"/> 为假)。</summary>
    [ObservableProperty] public partial int ContextPercent { get; private set; }

    [ObservableProperty] public partial bool HasContext { get; private set; }

    [ObservableProperty] public partial TodoPanelViewModel? Todo { get; private set; }

    [ObservableProperty] public partial GoalBarViewModel? Goal { get; private set; }

    /// <summary>有没有随时间变化的行(运行中的子代理要计时):视图据此决定要不要开每秒一次的定时器,定时器只调 <see cref="Tick"/>。</summary>
    [ObservableProperty] public partial bool NeedsTick { get; private set; }

    public bool IsEmpty => HasSession && !IsLoading && Rows.Count == 0;

    public bool HasNotices => Notices.Count > 0;

    public SessionRef? Session => _session;

    /// <summary>要求视图滚到并高亮某一行(收件箱点进来定位到交互卡片)。行已经在就立刻触发,还没到(快照在路上)就等它出现。</summary>
    public event Action<RowViewModel>? FocusRequested;

    /// <summary>
    /// 请求定位到一行(转录行的稳定 key,例如交互卡片是 <c>i:交互 id</c>);传 null 清掉待定的请求。
    /// 高亮是纯 UI 状态(<see cref="InteractionRowViewModel.IsHighlighted"/>),换会话时自动清掉。
    /// </summary>
    public void RequestFocus(string? rowKey)
    {
        _pendingFocus = rowKey;
        foreach (var row in Rows.OfType<InteractionRowViewModel>())
        {
            row.IsHighlighted = false;
        }

        TryFocus();
    }

    private void TryFocus()
    {
        if (_pendingFocus is not { } key || Rows.FirstOrDefault(r => r.Key == key) is not { } row)
        {
            return;
        }

        _pendingFocus = null;
        if (row is InteractionRowViewModel interaction)
        {
            interaction.IsHighlighted = true;
        }

        FocusRequested?.Invoke(row);
    }

    /// <summary>定时器每秒调一次:重新派生一遍,运行中子代理的耗时随时间前进。</summary>
    public void Tick() => Refresh();

    private string DeviceName(string deviceId) =>
        _stores.Workspace.State.Devices.TryGetValue(deviceId, out var device) ? device.Name : deviceId;

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        var conversations = _stores.Conversations.State;

        if (selection.Project is not { } project || selection.SessionId is not { } sessionId)
        {
            _session = null;
            HasSession = false;
            Title = "";
            Reconcile([]);
            Todo = null;
            Goal = null;
            IsLoading = false;
            IsRunning = false;
            HasContext = false;
            ContextPercent = 0;
            NeedsTick = false;
            Composer.Update(null, null, null, null, SessionActivity.Idle, DeviceConnection.Connecting, null, []);
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var session = new SessionRef(project.DeviceId, sessionId);
        if (_session != session)
        {
            Notices.Clear();
            OnPropertyChanged(nameof(HasNotices));
            _pendingFocus = null;
        }

        _session = session;
        workspace.Devices.TryGetValue(project.DeviceId, out var device);
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        var unknown = connection != DeviceConnection.Connected;
        var view = conversations.Sessions.GetValueOrDefault(session);
        var summary = workspace.Sessions.FirstOrDefault(s => s.Id == sessionId && s.Project == project);

        HasSession = true;
        Title = summary?.Title ?? view?.Meta.Title ?? "";
        IsLoading = view is null && !unknown;
        IsDeviceUnknown = unknown;

        var info = workspace.Projects.GetValueOrDefault(project);
        var fallback = info is null ? null : new EnvRefDto(project.DeviceId, info.EnvironmentName ?? "local", info.EnvironmentName ?? "local", info.Environment == EnvironmentKind.Ssh ? "ssh" : "local");
        var rows = TranscriptBuilder.Build(conversations, session, fallback, _clock.GetUtcNow(), unknown);
        _lastUserText = rows.OfType<UserRow>().LastOrDefault()?.Text ?? _lastUserText;
        Reconcile(rows);
        TryFocus();

        var activity = SessionActivityView.Of(sessionId, device?.Index, connection);
        IsRunning = activity == SessionActivity.Running;
        NeedsTick = !unknown && rows.OfType<SubagentRow>().Any(r => r.View.IsActive);

        Todo = view?.Todo is { } todo && !(Todo?.View.Equals(todo) ?? false) ? new TodoPanelViewModel(todo) : view?.Todo is null ? null : Todo;
        var goal = conversations.GoalOf(session);
        Goal = goal is null ? null : new GoalBarViewModel(goal, unknown, ClearGoalCommand);

        var run = conversations.Runs.Values.Where(r => r.Ref.DeviceId == project.DeviceId && r.SessionId == sessionId && r.Context is not null)
            .OrderByDescending(r => r.Ref.RunId, StringComparer.Ordinal).FirstOrDefault();
        HasContext = run?.Context is { Window: > 0 };
        ContextPercent = run?.Context is { Window: > 0 } ctx ? (int)Math.Clamp(Math.Round(100.0 * ctx.Used / ctx.Window), 0, 100) : 0;

        Composer.Update(session, view?.Meta, view?.Queue, view?.ActiveRunId ?? ActiveRunOf(conversations, session), activity, connection, device?.Role,
            _stores.Providers.State.Of(project.DeviceId).Options);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string? ActiveRunOf(ConversationState state, SessionRef session) =>
        state.Runs.Values.Where(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId && r.Phase == RunPhase.Running)
            .Select(r => r.Ref.RunId).FirstOrDefault();

    // ───────────────────────── 行对齐 ─────────────────────────

    private void Reconcile(ImmutableArray<TranscriptRow> rows)
    {
        var existing = new Dictionary<string, RowViewModel>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            existing.TryAdd(row.Key, row);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var target = 0;
        foreach (var row in rows)
        {
            if (!seen.Add(row.Key))
            {
                continue;
            }

            if (target < Rows.Count && Rows[target].Key == row.Key)
            {
                Apply(Rows[target], row);
            }
            else if (existing.TryGetValue(row.Key, out var vm) && Rows.IndexOf(vm) is var old and >= 0 && old >= target)
            {
                Rows.Move(old, target);
                Apply(vm, row);
            }
            else
            {
                Rows.Insert(target, Create(row));
            }

            target++;
        }

        while (Rows.Count > target)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    private static void Apply(RowViewModel vm, TranscriptRow row)
    {
        if (vm is InteractionRowViewModel interaction && row is InteractionRow ir)
        {
            interaction.Refresh(ir);
        }
        else
        {
            vm.Update(row);
        }
    }

    private RowViewModel Create(TranscriptRow row) => row switch
    {
        UserRow r => new UserRowViewModel(r),
        AssistantRow r => new AssistantRowViewModel(r),
        ThinkingRow r => new ThinkingRowViewModel(r),
        ToolRow r => new ToolRowViewModel(r, DeviceName),
        WidgetRow r => new WidgetRowViewModel(r, () => _session ?? default, _widgets, DeviceName),
        SubagentRow r => new SubagentRowViewModel(r, StopSubagentCommand, OpenSubagentCommand),
        SubagentReportRow r => new ReportRowViewModel(r),
        CompactRow r => new CompactRowViewModel(r),
        ErrorRow r => new ErrorRowViewModel(r, RetryCommand),
        GoalStatusRow r => new GoalStatusRowViewModel(r),
        StatusRow r => new StatusRowViewModel(r),
        InteractionRow r => new InteractionRowViewModel(r, _session?.DeviceId ?? "", _commands, DeviceName, OnHandled, _clock),
        _ => throw new InvalidOperationException($"Unknown transcript row {row.GetType().Name}"),
    };

    private void OnHandled(HandledNotice notice)
    {
        if (Notices.All(n => n.InteractionId != notice.InteractionId))
        {
            Notices.Add(notice);
            OnPropertyChanged(nameof(HasNotices));
        }
    }

    // ───────────────────────── 意图 ─────────────────────────

    [RelayCommand]
    private void DismissNotice(HandledNotice notice)
    {
        Notices.Remove(notice);
        OnPropertyChanged(nameof(HasNotices));
    }

    /// <summary>失败的 Run:把最后一条用户消息再发一次(同一个发送入口,档位取输入框当前的)。</summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_session is { } session && _lastUserText.Length > 0)
        {
            await _commands.SendAsync(session, _lastUserText, Composer.Options());
        }
    }

    [RelayCommand]
    private async Task StopSubagentAsync(SubagentRef card)
    {
        if (_subagents is not null && _stores.Conversations.State.Subagents.TryGetValue(card, out var entry))
        {
            await _subagents.StopAsync(entry);
        }
    }

    /// <summary>点开子代理的完整记录(右侧只读面板接入前:订阅它的子会话,内容在 Store 里)。</summary>
    [RelayCommand]
    private async Task OpenSubagentAsync(SubagentRef card)
    {
        if (_subagents is not null && _stores.Conversations.State.Subagents.TryGetValue(card, out var entry))
        {
            await _subagents.OpenAsync(entry);
        }
    }

    [RelayCommand]
    private async Task ClearGoalAsync()
    {
        if (_goals is not null && _session is { } session)
        {
            await _goals.ClearAsync(session);
        }
    }
}
