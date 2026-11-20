using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
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
    private readonly IMessageCommands? _messages;
    private readonly IImageSource? _images;
    private readonly IGitCommands? _git;
    private readonly IWorkflowCommands? _workflows;
    private SessionRef? _workflowsLoadedFor;
    private readonly TimeProvider _clock;
    private readonly Ncw.Client.Widgets.WidgetHostContext? _widgets;
    private string _lastUserText = "";
    private SessionRef? _session;

    /// <summary>上一次派生转录用的输入指纹和结果:输入没变(按引用)就不重新派生(<see cref="TranscriptBuilder.Inputs"/>)。</summary>
    private TranscriptBuilder.Inputs? _lastInputs;
    private ImmutableArray<TranscriptRow> _lastRows = [];
    private bool _forceRebuild;
    private string? _pendingFocus;

    /// <summary>空会话为哪个会话拉过一次 git 概览(每个空会话最多拉一次,见 <see cref="RefreshEmptyContext"/>)。</summary>
    private SessionRef? _gitProbed;

    public ConversationViewModel(ClientStores stores, IConversationCommands commands, SubagentActions? subagents = null, GoalActions? goals = null, TimeProvider? clock = null, IComposerLookups? lookups = null,
        AttachmentTrayViewModel? attachments = null, Ncw.Client.Widgets.WidgetHostContext? widgets = null, IMessageCommands? messages = null, IImageSource? images = null, IGitCommands? git = null,
        IWorkflowCommands? workflows = null)
    {
        _messages = messages;
        _git = git;
        _images = images;
        _widgets = widgets;
        _workflows = workflows;
        _stores = stores;
        _commands = commands;
        _subagents = subagents;
        _goals = goals;
        _clock = clock ?? TimeProvider.System;
        Composer = new ComposerViewModel(commands, lookups, attachments: attachments, goals: goals);
        Composer.ProviderSettingsRequested += () => ProviderSettingsRequested?.Invoke();
        Composer.NewSessionRequested += () => NewSessionRequested?.Invoke();
        stores.Conversations.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        stores.Providers.Changed += Refresh;
        stores.Git.Changed += RefreshEmptyContext;
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

    /// <summary>
    /// 消息工具条的编辑 / 分支 / 删除能不能用:接了 <see cref="MessageActions"/>、设备连着(断开时状态未知,规则 7)、会话没有 Run 在跑(设备会拒绝改写历史)、
    /// 角色不是只读。复制不受它限制。
    /// </summary>
    [ObservableProperty] public partial bool CanEditHistory { get; private set; }

    /// <summary>消息操作失败的稳定 key(<see cref="MessageErrorKeys"/>),界面翻译;null = 没有错误。用户关掉或下一次操作开始时清掉。</summary>
    [ObservableProperty] public partial string? MessageError { get; private set; }

    public bool HasMessageError => MessageError is not null;

    /// <summary>灯箱里放大显示的图片;null = 没有打开。纯 UI 状态,换会话时关掉。</summary>
    [ObservableProperty] public partial ImageThumbViewModel? Lightbox { get; private set; }

    public bool HasLightbox => Lightbox is not null;

    partial void OnLightboxChanged(ImageThumbViewModel? value) => OnPropertyChanged(nameof(HasLightbox));

    [RelayCommand]
    private void OpenImage(ImageThumbViewModel image)
    {
        if (image.IsReady)
        {
            Lightbox = image;
        }
    }

    [RelayCommand]
    private void CloseLightbox() => Lightbox = null;

    /// <summary>上下文占用 0–100;没有数据时为 0(<see cref="HasContext"/> 为假)。</summary>
    [ObservableProperty] public partial int ContextPercent { get; private set; }

    [ObservableProperty] public partial bool HasContext { get; private set; }

    /// <summary>最近一次上下文占用的明细(窗口、各部分的 tokens);输入框的上下文环点开的卡片据它画。没有数据时为 null。</summary>
    [ObservableProperty] public partial ContextUsageEventDto? Context { get; private set; }

    [ObservableProperty] public partial TodoPanelViewModel? Todo { get; private set; }

    [ObservableProperty] public partial GoalBarViewModel? Goal { get; private set; }

    /// <summary>有没有随时间变化的行(运行中的子代理要计时):视图据此决定要不要开每秒一次的定时器,定时器只调 <see cref="Tick"/>。</summary>
    [ObservableProperty] public partial bool NeedsTick { get; private set; }

    public bool IsEmpty => HasSession && !IsLoading && Rows.Count == 0;

    // ── 空会话的上下文(docs/design/03 §11.4):标题里的项目名、执行环境、分支、未提交改动数。都从 Store 读,不缓存事实 ──

    /// <summary>当前项目的名字(空会话标题「在 X 里做点什么?」)。</summary>
    [ObservableProperty] public partial string EmptyProjectName { get; private set; } = "";

    /// <summary>「设备 · 环境」:本机项目只有设备名,远程环境带上环境名(AGENTS.md §5:涉及执行的地方带上执行环境标识)。</summary>
    [ObservableProperty] public partial string EmptyEnvLabel { get; private set; } = "";

    [ObservableProperty] public partial bool EmptyEnvIsSsh { get; private set; }

    /// <summary>当前分支;git 概览未知(没取到 / 设备断开,规则 7)、不是仓库、分离头指针时为 null,不显示。</summary>
    [ObservableProperty] public partial string? EmptyBranch { get; private set; }

    [ObservableProperty] public partial bool HasEmptyBranch { get; private set; }

    /// <summary>未提交改动的文件数(Core 的 <see cref="GitStatusSummary"/> 唯一生成函数算出的总数);概览未知时为 0,不显示。</summary>
    [ObservableProperty] public partial int EmptyChanges { get; private set; }

    [ObservableProperty] public partial bool HasEmptyChanges { get; private set; }

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

    /// <summary>定时器每秒调一次:重新派生一遍(时间是输入指纹之外的唯一输入,所以强制),运行中子代理的耗时随时间前进。</summary>
    public void Tick()
    {
        _forceRebuild = true;
        Refresh();
    }

    /// <summary>当前项目在设备上的根目录(工具行把项目里的路径显示成相对的)。</summary>
    private string? ProjectRoot() =>
        _stores.Selection.State.Project is { } project && _stores.Workspace.State.Projects.TryGetValue(project, out var info) ? info.Path : null;

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
            _lastInputs = null;
            _lastRows = [];
            Reconcile([]);
            Todo = null;
            Goal = null;
            IsLoading = false;
            IsRunning = false;
            CanEditHistory = false;
            HasContext = false;
            ContextPercent = 0;
            Context = null;
            NeedsTick = false;
            Composer.Update(null, null, null, null, SessionActivity.Idle, DeviceConnection.Connecting, null, []);
            OnPropertyChanged(nameof(IsEmpty));
            RefreshEmptyContext();
            return;
        }

        var session = new SessionRef(project.DeviceId, sessionId);
        if (_session != session)
        {
            Notices.Clear();
            OnPropertyChanged(nameof(HasNotices));
            _pendingFocus = null;
            Lightbox = null;
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
        // 只在这份转录的输入真的变了时重新派生 + 对齐:别的会话在流式、会话列表 / 设备 / 模型候选变了,都会走到这里,但和这份转录无关
        var inputs = TranscriptBuilder.Inputs.Of(conversations, session, fallback, unknown);
        var rows = _lastRows;
        if (_forceRebuild || !inputs.SameAs(_lastInputs))
        {
            rows = TranscriptBuilder.Build(conversations, session, fallback, _clock.GetUtcNow(), unknown, groupFinishedWork: true);
            _lastInputs = inputs;
            _lastRows = rows;
            _lastUserText = rows.OfType<UserRow>().LastOrDefault()?.Text ?? _lastUserText;
            Reconcile(rows);
        }

        _forceRebuild = false;
        TryFocus();

        var activity = SessionActivityView.Of(sessionId, device?.Index, connection);
        IsRunning = activity == SessionActivity.Running;
        CanEditHistory = _messages is not null && !unknown && !IsRunning && device?.Role is not "viewer";
        MarkRegenerable();
        NeedsTick = !unknown && rows.OfType<SubagentRow>().Any(r => r.View.IsActive);

        Todo = view?.Todo is { } todo && !(Todo?.View.Equals(todo) ?? false) ? new TodoPanelViewModel(todo) : view?.Todo is null ? null : Todo;
        EnsureSessionWorkflowsLoaded(session);
        var goal = conversations.GoalOf(session);
        Goal = goal is null ? null : new GoalBarViewModel(goal, unknown, ClearGoalCommand);

        var run = conversations.Runs.Values.Where(r => r.Ref.DeviceId == project.DeviceId && r.SessionId == sessionId && r.Context is not null)
            .OrderByDescending(r => r.Ref.RunId, StringComparer.Ordinal).FirstOrDefault();
        HasContext = run?.Context is { Window: > 0 };
        Context = run?.Context is { Window: > 0 } usage ? usage : null;
        ContextPercent = run?.Context is { Window: > 0 } ctx ? (int)Math.Clamp(Math.Round(100.0 * ctx.Used / ctx.Window), 0, 100) : 0;

        Composer.Update(session, view?.Meta, view?.Queue, ActiveRunOf(conversations, session, view?.ActiveRunId), activity, connection, device?.Role,
            _stores.Providers.State.Of(project.DeviceId).Options);
        OnPropertyChanged(nameof(IsEmpty));
        RefreshEmptyContext();
    }

    /// <summary>
    /// 空会话的上下文胶囊。分支和改动数读 git 镜像(<see cref="ClientStores.Git"/>):只有「已知且可用」才显示,未知(设备断开 / 还没取到)时不拿旧值当现状(规则 7)。
    /// git 概览没有推送主题,平时由检查器的 Git 面板拉;空会话第一次出现、镜像里还没有这个项目时,这里经 <see cref="IGitCommands"/> 拉一次(每个会话最多一次)。
    /// </summary>
    private void RefreshEmptyContext()
    {
        if (_stores.Selection.State.Project is not { } project)
        {
            EmptyProjectName = "";
            EmptyEnvLabel = "";
            EmptyEnvIsSsh = false;
            SetEmptyGit(null, 0);
            return;
        }

        var workspace = _stores.Workspace.State;
        var info = workspace.Projects.GetValueOrDefault(project);
        var device = DeviceName(project.DeviceId);
        EmptyProjectName = info?.Name ?? "";
        EmptyEnvLabel = info?.EnvironmentName is { Length: > 0 } env ? $"{device} · {env}" : device;
        EmptyEnvIsSsh = info?.Environment == EnvironmentKind.Ssh;

        var git = _stores.Git.State.Of(project);
        var overview = git.Available ? git.Overview : null;
        SetEmptyGit(overview is { Detached: false, Branch.Length: > 0 } o ? o.Branch : null, overview is null ? 0 : git.Summary.Total);

        var connected = workspace.Devices.TryGetValue(project.DeviceId, out var d) && d.Connection == DeviceConnection.Connected;
        if (IsEmpty && _git is not null && connected && !git.Known && _session is { } session && _gitProbed != session)
        {
            _gitProbed = session;
            _ = _git.RefreshAsync(project); // 结果经 Store 回来(Git.Changed → 这里);失败就不显示分支,不打扰
        }
    }

    private void SetEmptyGit(string? branch, int changes)
    {
        EmptyBranch = branch;
        HasEmptyBranch = branch is not null;
        EmptyChanges = changes;
        HasEmptyChanges = changes > 0;
    }

    /// <summary>
    /// 「停止」该打哪个 Run。真源是 Run 的实时状态(<see cref="ConversationState.Runs"/>,随事件和全量广播对账);
    /// 会话快照里的 <paramref name="snapshotRunId"/> 只在拿到快照那一刻对,之后不会更新——它排在前面的话,快照后又起了新 Run(排队续跑 / 目标续跑 / 后台报告)时
    /// 停止会打在一个早已结束的旧 Run 上:<c>runs.abort</c> 对不存在的 Run 静默成功,界面不报错,新的 Run 照常跑。所以实时的优先,快照只兜底(且它在 Store 里已是终态时不采用)。
    /// </summary>
    private static string? ActiveRunOf(ConversationState state, SessionRef session, string? snapshotRunId)
    {
        var live = state.Runs.Values.Where(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId && r.Phase == RunPhase.Running)
            .Select(r => r.Ref.RunId).OrderByDescending(id => id, StringComparer.Ordinal).FirstOrDefault();
        if (live is not null || snapshotRunId is null)
        {
            return live;
        }

        return state.Runs.TryGetValue(new RunRef(session.DeviceId, snapshotRunId), out var known) && known.Phase != RunPhase.Running && known.Phase != RunPhase.Unknown
            ? null
            : snapshotRunId;
    }

    // ───────────────────────── 行对齐 ─────────────────────────

    /// <summary>
    /// 把新派生的行对齐到 <see cref="Rows"/> 上:先删掉已经不在的行(从后往前),再按顺序原地更新 / 移动 / 插入。
    /// 先删是为了让「中间少了一行」只产生一次删除通知——否则它后面的每一行都会被逐个「移动」一格(n 次移动通知、每次 O(n) 查找,虚拟化列表每次都要重排)。
    /// </summary>
    private void Reconcile(ImmutableArray<TranscriptRow> all)
    {
        // 「这个会话里 Agent 写 / 改过文件」从完整的行算:成员收起后没有视图模型,但文件改动还在,检查器的「审查」页签不能跟着消失
        HasFileChanges = all.Any(static r => r is ToolRow { Kind: ToolKind.Edit or ToolKind.Write, Status: ToolRowStatus.Done });
        var rows = VisibleRows(all, out var members);
        var wanted = new HashSet<string>(rows.Length, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            wanted.Add(row.Key);
        }

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Rows[i].Key))
            {
                Rows.RemoveAt(i);
            }
        }

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

            var inGroup = members.Contains(row.Key);
            if (target < Rows.Count && Rows[target].Key == row.Key)
            {
                Apply(Rows[target], row);
                Rows[target].InWorkGroup = inGroup;
            }
            else if (existing.TryGetValue(row.Key, out var vm) && Rows.IndexOf(vm) is var old and >= 0 && old >= target)
            {
                Rows.Move(old, target);
                Apply(vm, row);
                vm.InWorkGroup = inGroup;
            }
            else
            {
                var created = Create(row);
                created.InWorkGroup = inGroup; // 插入之前标好:容器准备时读它来缩进
                Rows.Insert(target, created);
            }

            target++;
        }

        while (Rows.Count > target)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        PendingHere = Rows.Count(static r => r is InteractionRowViewModel);
    }

    /// <summary>
    /// 「工作区」分组的展开状态(纯 UI 状态,按表头的 key 记;key 取自成员第一行,一轮落库之后稳定)。
    /// 收起的分组把紧跟在表头后面的 <see cref="WorkGroupRow.Members"/> 行从转录里拿掉(不创建视图模型);展开的分组成员留着,<paramref name="members"/> 收集它们的 key 用来缩进。
    /// </summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);

    private ImmutableArray<TranscriptRow> VisibleRows(ImmutableArray<TranscriptRow> all, out HashSet<string> members)
    {
        members = [];
        if (!all.Any(static r => r is WorkGroupRow))
        {
            return all;
        }

        var visible = ImmutableArray.CreateBuilder<TranscriptRow>(all.Length);
        for (var i = 0; i < all.Length; i++)
        {
            visible.Add(all[i]);
            if (all[i] is not WorkGroupRow group)
            {
                continue;
            }

            var count = Math.Min(group.Members, all.Length - i - 1);
            if (_expandedGroups.Contains(group.Key))
            {
                for (var m = 1; m <= count; m++)
                {
                    visible.Add(all[i + m]);
                    members.Add(all[i + m].Key);
                }
            }

            i += count;
        }

        return visible.ToImmutable();
    }

    private void OnWorkGroupToggled(WorkGroupRowViewModel group)
    {
        if (!(group.IsExpanded ? _expandedGroups.Add(group.Key) : _expandedGroups.Remove(group.Key)))
        {
            return;
        }

        if (!_lastRows.IsDefault)
        {
            Reconcile(_lastRows);
            MarkRegenerable();
        }
    }

    /// <summary>这个会话里 Agent 写 / 改过文件(有已完成的写 / 改工具行):检查器据此自动给出「审查」页签(03 §11.5)。</summary>
    [ObservableProperty] public partial bool HasFileChanges { get; private set; }

    // ───────────────────────── 待处理条(docs/design/03 §11.3)─────────────────────────

    /// <summary>这个会话里正在等用户处理的卡片数(转录里的交互行)。</summary>
    [ObservableProperty] public partial int PendingHere { get; private set; }

    /// <summary>别的会话 / 设备上现在能处理的待办数(收件箱里扣掉这个会话的;外壳从收件箱算好填进来)。</summary>
    [ObservableProperty] public partial int PendingElsewhere { get; set; }

    /// <summary>输入框上方显示「N 项等你处理」条。</summary>
    public bool ShowsPendingBar => PendingHere > 0 || PendingElsewhere > 0;

    partial void OnPendingHereChanged(int value) => OnPropertyChanged(nameof(ShowsPendingBar));

    partial void OnPendingElsewhereChanged(int value) => OnPropertyChanged(nameof(ShowsPendingBar));

    private string? _lastPendingFocus;

    /// <summary>待处理条的「下一项」:在这个会话的交互卡片之间循环,滚过去并高亮。</summary>
    [RelayCommand]
    private void FocusNextPending()
    {
        var keys = Rows.OfType<InteractionRowViewModel>().Select(static r => r.Key).ToList();
        if (keys.Count == 0)
        {
            return;
        }

        var next = keys[(keys.IndexOf(_lastPendingFocus ?? "") + 1) % keys.Count];
        _lastPendingFocus = next;
        RequestFocus(next);
    }

    /// <summary>错误卡片 / 「没有模型」提示上的「打开模型与供应商设置」:交给外壳打开设置的那一段。</summary>
    public event Action? ProviderSettingsRequested;

    [RelayCommand]
    private void OpenProviderSettings() => ProviderSettingsRequested?.Invoke();

    /// <summary>输入框的 <c>/clear</c>:交给外壳在当前项目里开一个新会话(和 ⌘N 同一个意图)。</summary>
    public event Action? NewSessionRequested;

    /// <summary>待处理条的「其他会话」:交给外壳转到下一项(和 ⌘J 同一个意图)。</summary>
    public event Action? ElsewhereRequested;

    [RelayCommand]
    private void GoToElsewhere() => ElsewhereRequested?.Invoke();

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
        UserRow r => new UserRowViewModel(r, ImagesOf(r)),
        AssistantRow r => new AssistantRowViewModel(r),
        ThinkingRow r => new ThinkingRowViewModel(r),
        ToolRow r => new ToolRowViewModel(r, DeviceName, ProjectRoot),
        WorkGroupRow r => new WorkGroupRowViewModel(r, _expandedGroups.Contains(r.Key), OnWorkGroupToggled),
        WidgetRow r => new WidgetRowViewModel(r, () => _session ?? default, _widgets, DeviceName),
        WorkflowRow r => CreateWorkflowCard(r),
        SubagentRow r => new SubagentRowViewModel(r, StopSubagentCommand, OpenSubagentCommand),
        SubagentReportRow r => new ReportRowViewModel(r),
        CompactRow r => new CompactRowViewModel(r),
        ErrorRow r => new ErrorRowViewModel(r, RetryCommand, OpenProviderSettingsCommand),
        GoalStatusRow r => new GoalStatusRowViewModel(r),
        StatusRow r => new StatusRowViewModel(r),
        InteractionRow r => new InteractionRowViewModel(r, _session?.DeviceId ?? "", _commands, DeviceName, OnHandled, _clock),
        _ => throw new InvalidOperationException($"Unknown transcript row {row.GetType().Name}"),
    };

    private void EnsureSessionWorkflowsLoaded(SessionRef session)
    {
        if (_workflows is not null && _workflowsLoadedFor != session)
        {
            _workflowsLoadedFor = session;
            _ = LoadSessionWorkflowsAsync(session);
        }
    }

    private async Task LoadSessionWorkflowsAsync(SessionRef session)
    {
        if (_workflows is null)
        {
            return;
        }

        var list = await _workflows.ListSessionRunsAsync(session.DeviceId, session.SessionId).ConfigureAwait(false);
        if (!list.IsOk || list.Value is null || list.Value.Runs.Length == 0)
        {
            return;
        }

        // 订阅并拉取本会话最近一次工作流的权威快照
        await _workflows.WatchRunAsync(session.DeviceId, list.Value.Runs[0].RunId).ConfigureAwait(false);
    }

    private WorkflowRunCardViewModel CreateWorkflowCard(WorkflowRow row)
    {
        var deviceId = _session?.DeviceId ?? "";
        return new WorkflowRunCardViewModel(_stores, deviceId, row.RunId, _workflows, row.Key, row);
    }

    /// <summary>这条用户消息里的图片引用 → 缩略图(会话镜像里找消息;图片字节按需向设备取,不进 Store)。</summary>
    private IReadOnlyList<ImageThumbViewModel> ImagesOf(UserRow row)
    {
        if (_images is null || row.ImageCount == 0 || _session is not { } session
            || _stores.Conversations.State.Sessions.GetValueOrDefault(session) is not { } view
            || view.Messages.FirstOrDefault(m => m.Id == row.MessageId) is not { } message)
        {
            return [];
        }

        return [.. MessageImages.Of(message).Select(i => new ImageThumbViewModel(i.Mime, i.DataRef, ct => _images.LoadAsync(session.DeviceId, i.DataRef, ct)))];
    }

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

    // ───────────────────────── 消息操作(编辑 / 分支 / 删除一轮) ─────────────────────────
    // 一个意图一个方法(规则 4):工具条、快捷键都调这里;改写由设备执行,结果经 history.reset 回到 Store。

    partial void OnMessageErrorChanged(string? value) => OnPropertyChanged(nameof(HasMessageError));

    [RelayCommand]
    private void DismissMessageError() => MessageError = null;

    [RelayCommand]
    private void BeginEdit(UserRowViewModel row)
    {
        if (!CanEditHistory)
        {
            return;
        }

        MessageError = null;
        row.IsConfirmingDelete = false;
        row.EditText = row.Text;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit(UserRowViewModel row) => row.IsEditing = false;

    /// <summary>只改写这条提问的文字,后面的回复保留。</summary>
    [RelayCommand]
    private async Task SaveEditAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session || string.IsNullOrWhiteSpace(row.EditText))
        {
            return;
        }

        MessageError = null;
        var result = await _messages.EditAsync(session, row.MessageId, row.EditText.Trim());
        if (result.IsOk)
        {
            row.IsEditing = false;
        }
        else
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    /// <summary>编辑后从这里重新运行:这条和之后的全部移除,再用新文字发一次。发送失败时历史已截断,文字放回输入框。</summary>
    [RelayCommand]
    private async Task ResendEditAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session || string.IsNullOrWhiteSpace(row.EditText))
        {
            return;
        }

        MessageError = null;
        var text = row.EditText.Trim();
        var result = await _messages.EditAndResendAsync(session, row.MessageId, text, FilesOf(session, row.MessageId), Composer.Options());
        if (result.IsOk)
        {
            row.IsEditing = false;
            return;
        }

        if (result.ErrorKey == MessageActions.ResendFailedKey)
        {
            Composer.Draft = text; // 历史已经截断:别让用户丢掉这段文字
        }

        MessageError = MessageErrorKeys.Of(result);
    }

    /// <summary>
    /// 重新生成最近一轮的回复:用同一条提问(文字和附件都原样)重跑一次,旧回复被截断移除。和「编辑后重新运行」是同一个入口,只是文字不变。
    /// 只对最近一轮开放(更早的回复后面还有对话,重跑会把后面的全部截掉)。
    /// </summary>
    [RelayCommand]
    private async Task RegenerateAsync(AssistantRowViewModel row)
    {
        if (_messages is null || !CanEditHistory || !row.CanRegenerate || _session is not { } session
            || Rows.OfType<UserRowViewModel>().LastOrDefault() is not { } turn)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.EditAndResendAsync(session, turn.MessageId, turn.Text, FilesOf(session, turn.MessageId), Composer.Options());
        if (result.IsOk)
        {
            return;
        }

        if (result.ErrorKey == MessageActions.ResendFailedKey)
        {
            Composer.Draft = turn.Text; // 历史已经截断:原提问放回输入框,用户可以直接再发
        }

        MessageError = MessageErrorKeys.Of(result);
    }

    /// <summary>原消息的附件(会话镜像里找;找不到就没有附件)。</summary>
    private IReadOnlyList<InputFile> FilesOf(SessionRef session, string messageId) =>
        _stores.Conversations.State.Sessions.GetValueOrDefault(session)?.Messages.FirstOrDefault(m => m.Id == messageId) is { } message
            ? MessageInputs.FilesOf(message)
            : [];

    /// <summary>每次对齐行之后标记助手回复的两个位置属性:「重新生成」落在哪一段(最后一条用户提问之后、最后一段已写完的助手文字),以及这段回复属不属于某一轮(能不能分支)。</summary>
    private void MarkRegenerable()
    {
        var lastUser = -1;
        var lastAssistant = -1;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is UserRowViewModel)
            {
                lastUser = i;
            }
            else if (Rows[i] is AssistantRowViewModel)
            {
                lastAssistant = i;
            }
        }

        var seenQuestion = false;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is UserRowViewModel)
            {
                seenQuestion = true;
            }
            else if (Rows[i] is AssistantRowViewModel assistant)
            {
                assistant.CanRegenerate = i == lastAssistant && lastUser >= 0 && i > lastUser && !assistant.IsStreaming && !IsRunning;
                assistant.HasTurn = seenQuestion; // 之前有可见提问 = 属于某一轮,可以分支
            }
        }
    }

    /// <summary>
    /// 从这段回复所在的这一轮分支出新会话并打开它(和用户消息上的「从这里分支」是同一个入口:<c>sessions.branch</c> 按「引出这一轮的提问」切,
    /// 所以带上的是到这一轮结束为止的全部,不会在一轮中间切断)。
    /// </summary>
    [RelayCommand]
    private async Task BranchReplyAsync(AssistantRowViewModel row)
    {
        if (_messages is null || !CanEditHistory || _session is not { } session || _stores.Selection.State.Project is not { } project
            || MessagesOf(session) is not { } messages || MessageTurns.QuestionOf(messages, row.MessageId) is not { } question)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.BranchAsync(project, session.SessionId, question);
        if (!result.IsOk)
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    [RelayCommand]
    private void AskDeleteReply(AssistantRowViewModel row)
    {
        if (CanEditHistory)
        {
            row.IsConfirmingDelete = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteReply(AssistantRowViewModel row) => row.IsConfirmingDelete = false;

    /// <summary>第二次确认之后删除这一轮 AI 的全部回复(提问留着)。范围取自会话镜像(<see cref="MessageTurns.ReplyOf"/>)。</summary>
    [RelayCommand]
    private async Task DeleteReplyAsync(AssistantRowViewModel row)
    {
        if (_messages is null || _session is not { } session)
        {
            return;
        }

        MessageError = null;
        var reply = MessagesOf(session) is { } messages ? MessageTurns.ReplyOf(messages, row.MessageId) : null;
        if (reply is not { } range)
        {
            row.IsConfirmingDelete = false;
            MessageError = MessageErrorKeys.Gone; // 这条回复已经不在会话里了(别的控制端先删了)
            return;
        }

        var result = await _messages.DeleteReplyAsync(session, range.From, range.To);
        row.IsConfirmingDelete = false;
        if (!result.IsOk)
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    private IReadOnlyList<MessageDto>? MessagesOf(SessionRef session) =>
        _stores.Conversations.State.Sessions.GetValueOrDefault(session)?.Messages;

    /// <summary>从这一轮分支出新会话并打开它。</summary>
    [RelayCommand]
    private async Task BranchTurnAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session || _stores.Selection.State.Project is not { } project)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.BranchAsync(project, session.SessionId, row.MessageId);
        if (!result.IsOk)
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    [RelayCommand]
    private void AskDeleteTurn(UserRowViewModel row)
    {
        if (CanEditHistory)
        {
            row.IsEditing = false;
            row.IsConfirmingDelete = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteTurn(UserRowViewModel row) => row.IsConfirmingDelete = false;

    /// <summary>第二次确认之后删除这一轮。</summary>
    [RelayCommand]
    private async Task DeleteTurnAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.DeleteTurnAsync(session, row.MessageId);
        row.IsConfirmingDelete = false;
        if (!result.IsOk)
        {
            MessageError = MessageErrorKeys.Of(result);
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
