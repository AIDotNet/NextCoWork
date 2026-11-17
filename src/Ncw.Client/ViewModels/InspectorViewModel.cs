using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>检查器各页签用到的意图(真实现是各领域的 *Actions;单测里换成假实现)。</summary>
public sealed record InspectorServices(IFilesCommands Files, IGitCommands Git, IReviewCommands Review, ITerminalCommands Terminals, SubagentActions? Subagents = null)
{
    /// <summary>对着一个设备注册表的真实现。</summary>
    public static InspectorServices Over(DeviceRegistry devices, ClientStores stores, SubagentActions? subagents = null) =>
        new(new FilesActions(devices), new GitActions(devices, stores), new ReviewActions(devices, stores), new TerminalActions(devices, stores), subagents ?? new SubagentActions(devices));
}

/// <summary>点开 / 停止子代理时没成功的原因(界面写成明确的提示)。</summary>
public enum SubagentNotice
{
    None,

    /// <summary>卡片没有子会话 id(重启之后只靠回执重建的旧卡片):不发那个注定失败的请求。</summary>
    NoChildSession,

    /// <summary>设备没连上 / 订阅失败。</summary>
    Unavailable,

    /// <summary>停不了:没有子 Run id(从回执重建、没见过 <c>subagent_start</c>)。</summary>
    CannotStop,
}

/// <summary>
/// 右侧面板:默认是文件管理器;Git、改动审查、终端、上下文、历史通过「+」添加;当前会话有子代理时自动出现「子代理」页签。
/// 「添加了哪些页签、当前选中哪个」是纯 UI 状态,放在这里(添加的页签持久化在 Prefs);各页签的事实都从 Store 读,
/// 文件 / diff / 终端输出这类不进 Store 的东西各由页签自己的 ViewModel 持有(见 <see cref="FilesPanelViewModel"/> 等)。本类只在 Store 变化后把「当前项目 / 会话」推给各页签。
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly TimeProvider _clock;
    private readonly SubagentActions? _subagents;
    private ProjectRef? _project;
    private int _runningRuns;
    private bool _initialized;
    private bool _connected;

    /// <summary>用户通过「+」添加的页签,持久化在 Prefs 里(本机界面偏好的唯一真源)。</summary>
    private IReadOnlyList<InspectorTabKind> Added => _stores.Prefs.State.InspectorTabs;

    public InspectorViewModel(ClientStores stores, TimeProvider clock, InspectorServices? services = null)
    {
        _stores = stores;
        _clock = clock;
        services ??= InspectorServices.Over(new DeviceRegistry(), stores);
        _subagents = services.Subagents;
        Files = new FilesPanelViewModel(services.Files, () => Dialogs, () => Text, () => LocalFiles);
        Git = new GitPanelViewModel(stores, services.Git, () => Dialogs, () => Text);
        Review = new ReviewPanelViewModel(stores, services.Review);
        Terminals = new TerminalPanelViewModel(stores, services.Terminals);
        stores.Workspace.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        stores.Prefs.Changed += Refresh;
        stores.Conversations.Changed += OnConversations;
        stores.Git.Changed += OnGit;
        Refresh();
    }

    /// <summary>弹窗服务。UI 层装配时换成 Arc Dialog 的实现。</summary>
    public IDialogService Dialogs { get; set; } = NoDialogs.Instance;

    /// <summary>检查器的弹窗文字。UI 层装配时换成按当前语言的实现。</summary>
    public IInspectorText Text { get; set; } = DefaultInspectorText.Instance;

    /// <summary>客户端本机的文件动作(显示 / 打开 / 另存为 / 剪贴板)。UI 层装配时换成真实现。</summary>
    public ILocalFiles LocalFiles { get; set; } = NoLocalFiles.Instance;

    public ObservableCollection<InspectorTabViewModel> Tabs { get; } = [];

    /// <summary>「+」菜单里还能添加的页签。</summary>
    public ObservableCollection<InspectorTabKind> Addable { get; } = [];

    public FilesPanelViewModel Files { get; }

    public GitPanelViewModel Git { get; }

    public ReviewPanelViewModel Review { get; }

    public TerminalPanelViewModel Terminals { get; }

    public ObservableCollection<SubagentItemViewModel> Subagents { get; } = [];

    /// <summary>点开的子代理的只读转录;null = 显示卡片列表。</summary>
    [ObservableProperty] public partial SubagentTranscriptViewModel? Transcript { get; private set; }

    /// <summary>点开 / 停止子代理没成功的原因(没有时 <see cref="SubagentNotice.None"/>)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial SubagentNotice Notice { get; private set; }

    public bool HasNotice => Notice != SubagentNotice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiles), nameof(IsGit), nameof(IsReview), nameof(IsTerminal), nameof(IsContext), nameof(IsHistory), nameof(IsSubagents))]
    public partial InspectorTabKind Active { get; private set; } = InspectorTabKind.Files;

    [ObservableProperty] public partial bool HasAddable { get; private set; }

    /// <summary>有随时间变化的东西(运行中的子代理要计时):视图据此开每秒一次的定时器,只调 <see cref="Tick"/>。</summary>
    [ObservableProperty] public partial bool NeedsTick { get; private set; }

    public bool IsFiles => Active == InspectorTabKind.Files;

    public bool IsGit => Active == InspectorTabKind.Git;

    public bool IsReview => Active == InspectorTabKind.Review;

    public bool IsTerminal => Active == InspectorTabKind.Terminal;

    public bool IsContext => Active == InspectorTabKind.Context;

    public bool IsHistory => Active == InspectorTabKind.History;

    public bool IsSubagents => Active == InspectorTabKind.Subagents;

    partial void OnActiveChanged(InspectorTabKind value)
    {
        // 页签切换:Git / 审查取一次最新的;终端只有可见时才订阅输出
        switch (value)
        {
            case InspectorTabKind.Git:
                _ = Git.RefreshAsync();
                break;
            case InspectorTabKind.Review:
                _ = Review.LoadAsync();
                break;
        }

        _ = Terminals.SetVisibleAsync(value == InspectorTabKind.Terminal);
    }

    /// <summary>当前会话里有已完成的写 / 改文件(外壳从对话转录派生后填进来):审查页签自动出现(docs/design/03 §11.5)。</summary>
    public bool SessionHasChanges
    {
        get => _sessionHasChanges;
        set
        {
            if (_sessionHasChanges != value)
            {
                _sessionHasChanges = value;
                Refresh();
            }
        }
    }

    private bool _sessionHasChanges;

    [RelayCommand]
    private void Activate(InspectorTabKind kind)
    {
        if (Tabs.Any(t => t.Kind == kind))
        {
            Active = kind;
            SyncTabs();
        }
    }

    [RelayCommand]
    private void Add(InspectorTabKind kind)
    {
        Active = kind;
        if (!Added.Contains(kind) && InspectorTabs.Addable.Contains(kind))
        {
            _stores.Prefs.Update(p => p with { InspectorTabs = p.InspectorTabs.Add(kind) });
        }

        Refresh();
    }

    [RelayCommand]
    private void Close(InspectorTabKind kind)
    {
        if (Active == kind)
        {
            Active = InspectorTabKind.Files;
        }

        _stores.Prefs.Update(p => p with { InspectorTabs = p.InspectorTabs.Remove(kind) });
        Refresh();
    }

    /// <summary>定时器每秒调一次:子代理的耗时随时间前进(断线的卡片冻结,不走)。</summary>
    public void Tick() => RefreshSubagents();

    // ───────────────────────── 派生 ─────────────────────────

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        var project = selection.Project;
        var projectChanged = project != _project;
        _project = project;

        var info = project is { } p && workspace.Projects.TryGetValue(p, out var i) ? i : null;
        var device = project is { } q && workspace.Devices.TryGetValue(q.DeviceId, out var d) ? d : null;
        _ = Files.BindAsync(project, info?.Name ?? "", info?.Path ?? "",
            canWrite: device?.Role != "viewer",
            canOpenLocally: device is { Kind: DeviceKind.Local } && info is { Environment: EnvironmentKind.Local });
        Git.Bind(project);
        Terminals.Bind(project);
        Review.Bind(project?.DeviceId, selection.SessionId);
        Files.Tree.ApplyChanges(GitChangeGroups.TreeMarkers(project is { } g ? _stores.Git.State.Of(g).Overview : null));

        var connected = device?.Connection == DeviceConnection.Connected;
        if (projectChanged && project is not null)
        {
            // 新项目:取一次 git 概览(文件树的角标和 Git 页签都读它)
            _ = Git.RefreshAsync();
            if (Transcript is not null)
            {
                CloseTranscript();
            }
        }
        else if (_initialized && connected && !_connected && project is not null)
        {
            // 设备(重新)连上了:启动时设备还没连上,第一次列目录 / 取概览失败了,断线期间的树和概览也可能旧了——都重新取一次
            _ = Files.Tree.RefreshAsync();
            _ = Git.RefreshAsync();
        }

        _connected = connected;
        _initialized = true;

        RefreshSubagents();
    }

    private void OnConversations()
    {
        RefreshSubagents();
        // 一个 Run 跑完了:它可能改过文件,git 概览(和文件树角标)跟着取一次
        if (_project is { } project)
        {
            var running = _stores.Workspace.State.ActivityOf(project).RunningRuns;
            if (running < _runningRuns)
            {
                _ = Git.RefreshAsync();
            }

            _runningRuns = running;
        }
    }

    /// <summary>git 概览变了:文件树的角标跟着(Git 页签自己订阅 Store 派生分组)。</summary>
    private void OnGit()
    {
        if (_project is { } project)
        {
            Files.Tree.ApplyChanges(GitChangeGroups.TreeMarkers(_stores.Git.State.Of(project).Overview));
        }
    }

    private void RefreshSubagents()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        var now = _clock.GetUtcNow();

        // 子代理:真实数据在对话镜像里(设备推来的事件 / 回执 / 权威广播合并后的卡片);Workspace 里的是模拟数据。同一张卡片只取一份。
        var items = new List<(SubagentRecord Record, SubagentViewState View, SubagentRef? Ref, bool Unknown)>();
        if (selection.SessionId is { } sessionId)
        {
            var real = selection.Project is { } current ? _stores.Conversations.State.SubagentsOf(new SessionRef(current.DeviceId, sessionId)) : [];
            foreach (var entry in real)
            {
                items.Add((entry.Record, entry.ViewAt(now), entry.Ref, entry.Unknown));
            }

            foreach (var record in workspace.SubagentsOf(sessionId).Where(r => items.All(x => x.Record.CallId != r.CallId)))
            {
                items.Add((record, SubagentView.Of(record, now), null, false));
            }
        }

        var kinds = InspectorTabs.Compose(Added, items.Count > 0, SessionHasChanges);
        if (!kinds.Contains(Active))
        {
            // 子代理页签随会话消失时回到文件管理器。
            Active = InspectorTabKind.Files;
        }

        var running = items.Count(x => !SubagentView.IsTerminal(x.Record.Status));
        var tabs = kinds.Select(kind => (Kind: kind, Count: kind == InspectorTabKind.Subagents ? running : 0, Auto: kind == InspectorTabKind.Review && !Added.Contains(kind))).ToList();
        if (!Tabs.Select(t => (t.Kind, t.Count, t.IsAuto)).SequenceEqual(tabs.Select(t => (t.Kind, t.Count, t.Auto))))
        {
            Tabs.Clear();
            foreach (var (kind, count, auto) in tabs)
            {
                Tabs.Add(new InspectorTabViewModel(kind, count) { IsAuto = auto });
            }

            Addable.Clear();
            foreach (var kind in InspectorTabs.Remaining(Added))
            {
                Addable.Add(kind);
            }

            HasAddable = Addable.Count > 0;
        }

        SyncTabs();

        var ordered = items
            .OrderBy(x => SubagentView.IsTerminal(x.Record.Status))
            .ThenByDescending(x => x.Record.StartedAt)
            .Select(x => new SubagentItemViewModel(x.Record.CallId, x.View, x.Ref, x.Unknown))
            .ToList();
        if (!Subagents.SequenceEqual(ordered, SubagentItemComparer.Instance))
        {
            Subagents.Clear();
            foreach (var item in ordered)
            {
                Subagents.Add(item);
            }
        }

        NeedsTick = items.Any(x => x.View.IsActive && !x.Unknown);
        if (Transcript is { } transcript)
        {
            var unknown = !(workspace.Devices.TryGetValue(transcript.Child.DeviceId, out var device) && device.Connection == DeviceConnection.Connected);
            transcript.Refresh(_stores.Conversations.State, now, unknown);
        }
    }

    private void SyncTabs()
    {
        foreach (var tab in Tabs)
        {
            tab.IsActive = tab.Kind == Active;
        }
    }

    // ───────────────────────── 子代理 ─────────────────────────

    /// <summary>停掉一个子代理(只停它,不连父 Run)。停不了(没有子 Run id)给明确的提示。</summary>
    [RelayCommand]
    private async Task StopSubagentAsync(SubagentItemViewModel item)
    {
        Notice = SubagentNotice.None;
        if (_subagents is null || item.Ref is not { } key || !_stores.Conversations.State.Subagents.TryGetValue(key, out var entry))
        {
            Notice = SubagentNotice.CannotStop;
            return;
        }

        var result = await _subagents.StopAsync(entry).ConfigureAwait(true);
        if (!result.IsOk)
        {
            Notice = result.Outcome == ActionOutcome.NotFound ? SubagentNotice.CannotStop : SubagentNotice.Unavailable;
        }
    }

    /// <summary>
    /// 点开一个子代理看它的转录:订阅子会话(子 Run 还在跑时再订阅它的 <c>run/&lt;childRunId&gt;</c> 主题);只在点开时才订阅。
    /// 没有子会话 id 的旧卡片(重启后只靠回执重建)点不开:<see cref="Notice"/> = <see cref="SubagentNotice.NoChildSession"/>,不发请求。
    /// </summary>
    [RelayCommand]
    private async Task OpenSubagentAsync(SubagentItemViewModel item)
    {
        Notice = SubagentNotice.None;
        if (!item.CanOpen)
        {
            Notice = SubagentNotice.NoChildSession;
            return;
        }

        if (_subagents is null || item.Ref is not { } key || !_stores.Conversations.State.Subagents.TryGetValue(key, out var entry))
        {
            Notice = SubagentNotice.Unavailable;
            return;
        }

        var child = await _subagents.OpenAsync(entry).ConfigureAwait(true);
        if (child is not { } session)
        {
            Notice = entry.Record.ChildSessionId is null ? SubagentNotice.NoChildSession : SubagentNotice.Unavailable;
            return;
        }

        ShowTranscript(session, item.Title);
    }

    /// <summary>订阅成功之后:显示这个子会话的只读转录(内容从 Store 派生)。</summary>
    public void ShowTranscript(SessionRef session, string title)
    {
        CloseTranscript();
        Transcript = new SubagentTranscriptViewModel(session, title, id => _stores.Workspace.State.Devices.TryGetValue(id, out var device) ? device.Name : id);
        RefreshSubagents();
    }

    [RelayCommand]
    private void CloseTranscript()
    {
        if (Transcript is { } transcript)
        {
            _subagents?.Close(transcript.Child);
            Transcript = null;
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = SubagentNotice.None;
}

public sealed partial class InspectorTabViewModel(InspectorTabKind kind, int count) : ObservableObject
{
    public InspectorTabKind Kind { get; } = kind;

    /// <summary>文件页签不能关;子代理页签随会话自动出现和消失,也不能手动关。审查页签:用户加的能关;自动出现的不能关(<see cref="IsAuto"/>)。</summary>
    public bool IsClosable => !IsAuto && Kind is not (InspectorTabKind.Files or InspectorTabKind.Subagents);

    /// <summary>随会话自动出现的页签(子代理、改过文件时的审查)。</summary>
    public bool IsAuto { get; init; }

    public int Count { get; } = count;

    public bool HasCount => Count > 0;

    public bool IsFiles => Kind == InspectorTabKind.Files;

    public bool IsGit => Kind == InspectorTabKind.Git;

    public bool IsReview => Kind == InspectorTabKind.Review;

    public bool IsTerminal => Kind == InspectorTabKind.Terminal;

    public bool IsContext => Kind == InspectorTabKind.Context;

    public bool IsHistory => Kind == InspectorTabKind.History;

    public bool IsSubagents => Kind == InspectorTabKind.Subagents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsClose))]
    public partial bool IsActive { get; set; }

    public bool ShowsClose => IsClosable && IsActive;
}

/// <summary>一张子代理卡片。只携带 <see cref="SubagentView.Of"/> 的结构化结果,文案由 UI 按语言格式化。</summary>
/// <param name="reference">对话镜像里这张卡片的身份(停止 / 点开要用);模拟数据里的没有。</param>
/// <param name="unknown">设备断开:状态未知,计时冻结(规则 7)。</param>
public sealed class SubagentItemViewModel(string callId, SubagentViewState view, SubagentRef? reference = null, bool unknown = false)
{
    public string CallId { get; } = callId;

    public SubagentRef? Ref { get; } = reference;

    public SubagentViewState View { get; } = view;

    public string Title { get; } = $"{view.AgentType} · {view.Description}";

    public SubagentStatus Status => View.Status;

    public bool IsUnknown { get; } = unknown;

    public bool IsActive { get; } = view.IsActive && !unknown;

    public bool CanStop { get; } = view.CanStop;

    public bool IsTerminal { get; } = view.IsTerminal;

    /// <summary>点得开:有子会话 id。旧转录重建出来的卡片没有,点它给明确的提示而不是静默没反应。</summary>
    public bool CanOpen { get; } = view.CanOpen;
}

internal sealed class SubagentItemComparer : IEqualityComparer<SubagentItemViewModel>
{
    public static readonly SubagentItemComparer Instance = new();

    public bool Equals(SubagentItemViewModel? x, SubagentItemViewModel? y) =>
        x is not null && y is not null && x.CallId == y.CallId && x.View == y.View && x.IsUnknown == y.IsUnknown && x.Ref == y.Ref;

    public int GetHashCode(SubagentItemViewModel obj) => obj.CallId.GetHashCode(StringComparison.Ordinal);
}
