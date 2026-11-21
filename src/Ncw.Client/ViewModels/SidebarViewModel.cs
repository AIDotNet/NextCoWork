using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.ViewModels;

/// <summary>侧边栏要外壳去做的事(打开命令面板、定时任务页、某条待办、检查器的终端页签……)。由 <see cref="ShellViewModel"/> 实现,测试里换成假的。</summary>
public interface ISidebarHost
{
    void OpenPalette();

    void OpenScheduledTasks();

    void OpenAttention(InboxEntry entry);

    /// <summary>「新建终端」:选中项目、打开检查器的「终端」页签、新建(和页签里的「+」同一个命令)。</summary>
    void NewTerminal(ProjectRef project);

    /// <summary>「新建文件」:选中项目、打开检查器的「文件」页签并在项目根发起新建文件。</summary>
    void NewFile(ProjectRef project);

    /// <summary>「新建文件夹」:选中项目、打开检查器的「文件」页签并在项目根发起新建文件夹。</summary>
    void NewFolder(ProjectRef project);

    void OpenSettings();

    /// <summary>本机的「在文件管理器中显示 / 复制」。</summary>
    ILocalFiles LocalFiles { get; }
}

/// <summary>
/// 桌面左侧边栏(docs/design/03 §1「左侧边栏」):全局入口 + 按 <see cref="RailLayout"/> 排的项目树 + 账户。
/// 只读 Store;意图都转给已有的入口(<see cref="LayoutActions"/>、<see cref="SessionActions"/>、<see cref="ProjectActions"/>、外壳)。
/// 哪些项目展开、哪些项目「显示更多 / 显示已归档」、收件箱列表开没开,是纯 UI 状态,留在本类,不持久化。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly LayoutActions _actions;
    private readonly ProjectActions? _projects;
    private readonly SessionActions? _sessions;
    private readonly InboxViewModel? _inbox;
    private readonly ISidebarHost _host;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IClientText> _text;
    private readonly SessionRowIntents _rows;
    private readonly HashSet<ProjectRef> _expanded = [];
    private readonly HashSet<ProjectRef> _showAll = [];
    private readonly HashSet<ProjectRef> _showArchived = [];
    private ProjectRef? _lastSelected;

    public SidebarViewModel(ClientStores stores, LayoutActions actions, ISidebarHost host, ProjectActions? projects = null, SessionActions? sessions = null,
        InboxViewModel? inbox = null, Func<IDialogService>? dialogs = null, Func<IClientText>? text = null, FilesPanelViewModel? files = null)
    {
        _stores = stores;
        Files = files;
        stores.Prefs.Changed += OnPrefsChanged;
        _actions = actions;
        _host = host;
        _projects = projects;
        _sessions = sessions;
        _inbox = inbox;
        _dialogs = dialogs ?? (() => NoDialogs.Instance);
        _text = text ?? (() => DefaultClientText.Instance);
        _rows = new SessionRowIntents(sessions, _dialogs, _text);
        stores.Layout.Changed += Rebuild;
        stores.Workspace.Changed += Rebuild;
        stores.Selection.Changed += OnSelectionChanged;
        if (inbox is not null)
        {
            inbox.Updated += RebuildInbox;
        }

        OnSelectionChanged();
        RebuildInbox();
    }

    /// <summary>保留旧布局的共享入口;文件浏览器只在右侧检查器渲染。</summary>
    public FilesPanelViewModel? Files { get; }

    /// <summary>旧左侧文件分栏的比例偏好,保留兼容,当前布局不使用。</summary>
    public double SidebarFilesRatio
    {
        get => _stores.Prefs.State.SidebarFilesRatio;
        set => _stores.Prefs.Update(p => (p with { SidebarFilesRatio = value }).Normalize());
    }

    [ObservableProperty] public partial bool IsProjectsSectionOpen { get; private set; } = true;

    [RelayCommand]
    private void ToggleProjectsSection() => IsProjectsSectionOpen = !IsProjectsSectionOpen;

    [RelayCommand]
    private void CollapseAllProjects()
    {
        _expanded.Clear();
        Rebuild();
    }

    private void OnPrefsChanged() => OnPropertyChanged(nameof(SidebarFilesRatio));

    /// <summary>项目树的顶层:项目和项目组,顺序就是 <see cref="RailLayout"/>。</summary>
    public ObservableCollection<SidebarNodeViewModel> Items { get; } = [];

    /// <summary>收件箱里现在要你处理的待办(点开收件箱时就地列出)。</summary>
    public ObservableCollection<InboxEntryViewModel> InboxItems { get; } = [];

    [ObservableProperty] public partial bool HasProjects { get; private set; }

    /// <summary>有选中的项目(全局「新会话」才有意义)。</summary>
    [ObservableProperty] public partial bool HasCurrentProject { get; private set; }

    /// <summary>收件箱的数字:和 Dock 角标、⌘J 同一份(<see cref="InboxViewModel.BadgeCount"/>)。</summary>
    [ObservableProperty] public partial int InboxCount { get; private set; }

    [ObservableProperty] public partial bool IsInboxOpen { get; set; }

    public bool HasInboxItems => InboxItems.Count > 0;

    /// <summary>当前布局(拖放规划用)。</summary>
    public RailLayout Layout => _stores.Layout.State.Rail;

    /// <summary>按 <see cref="ProjectRef.ToString"/> 找回项目(拖放数据里只放字符串 id)。</summary>
    public ProjectRef? FindProject(string id) =>
        Layout.Projects().Cast<ProjectRef?>().FirstOrDefault(p => p.ToString() == id);

    /// <summary>落地一次拖放(规则在 RailDropPlanner,落地在 LayoutActions)。</summary>
    public void Drop(RailDrop drop) => _actions.Drop(drop);

    // ───────────────────────── 全局入口 ─────────────────────────

    /// <summary>「新会话」(⌘N):在当前项目里新建。</summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (_sessions is not null && _stores.Selection.State.Project is { } project)
        {
            await _sessions.CreateAsync(project);
        }
    }

    [RelayCommand]
    private void NewTerminal()
    {
        if (_stores.Selection.State.Project is { } project)
        {
            _host.NewTerminal(project);
        }
    }

    [RelayCommand]
    private void NewFile()
    {
        if (_stores.Selection.State.Project is { } project)
        {
            _host.NewFile(project);
        }
    }

    [RelayCommand]
    private void NewFolder()
    {
        if (_stores.Selection.State.Project is { } project)
        {
            _host.NewFolder(project);
        }
    }

    [RelayCommand]
    private void Search() => _host.OpenPalette();

    [RelayCommand]
    private void ToggleInbox() => IsInboxOpen = !IsInboxOpen;

    [RelayCommand]
    private void OpenInboxItem(InboxEntryViewModel item) => _host.OpenAttention(item.Entry);

    [RelayCommand]
    private void OpenScheduledTasks() => _host.OpenScheduledTasks();

    /// <summary>项目树标题的「+」和没有项目时的「添加文件夹」:弹出「新建项目」窗口。</summary>
    [RelayCommand]
    private Task AddProjectAsync() => _projects?.NewProjectAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenSettings() => _host.OpenSettings();

    // ───────────────────────── 项目行的意图(SidebarProjectViewModel 转进来) ─────────────────────────

    /// <summary>点项目行:不是当前项目 → 选中(选中会自动展开);是当前项目 → 展开 / 收起。</summary>
    internal void Activate(SidebarProjectViewModel project)
    {
        if (_stores.Selection.State.Project == project.Project)
        {
            ToggleExpanded(project);
        }
        else
        {
            _actions.Select(project.Project);
        }
    }

    internal void ToggleExpanded(SidebarProjectViewModel project)
    {
        if (!_expanded.Remove(project.Project))
        {
            _expanded.Add(project.Project);
        }

        Rebuild();
    }

    internal void ToggleShowAll(SidebarProjectViewModel project)
    {
        if (!_showAll.Remove(project.Project))
        {
            _showAll.Add(project.Project);
        }

        Rebuild();
    }

    internal void ToggleArchived(SidebarProjectViewModel project)
    {
        if (!_showArchived.Remove(project.Project))
        {
            _showArchived.Add(project.Project);
        }

        _expanded.Add(project.Project);
        Rebuild();
    }

    internal async Task NewSessionInAsync(SidebarProjectViewModel project)
    {
        if (_sessions is not null)
        {
            await _sessions.CreateAsync(project.Project);
        }
    }

    internal void NewTerminal(SidebarProjectViewModel project) => _host.NewTerminal(project.Project);

    internal void NewFile(SidebarProjectViewModel project) => _host.NewFile(project.Project);

    internal void NewFolder(SidebarProjectViewModel project) => _host.NewFolder(project.Project);

    internal Task RevealAsync(SidebarProjectViewModel project) => _host.LocalFiles.RevealAsync(project.Path);

    internal Task CopyPathAsync(SidebarProjectViewModel project) => _host.LocalFiles.CopyTextAsync(project.Path);

    /// <summary>「从侧边栏移除」:接了设备时在设备上注销(<see cref="ProjectActions.RemoveAsync"/>),否则只改本地布局。</summary>
    internal Task RemoveProjectAsync(RailProjectViewModel tile)
    {
        if (_projects is not null)
        {
            return _projects.RemoveAsync(tile.Project);
        }

        _actions.RemoveFromRail(tile.Project);
        return Task.CompletedTask;
    }

    internal Task EditIconAsync(RailProjectViewModel tile)
    {
        var form = new ProjectIconViewModel(tile.Project, tile.Name, _stores.Layout.State.Appearance.GetValueOrDefault(tile.Project), _actions, _dialogs);
        return _dialogs().ShowProjectIconAsync(form);
    }

    // ───────────────────────── 派生 ─────────────────────────

    /// <summary>用别的方式选中了项目(⌘1…⌘9、⌃Tab、命令面板、通知、点项目行):自动展开它。</summary>
    private void OnSelectionChanged()
    {
        var selected = _stores.Selection.State.Project;
        if (selected is { } project && selected != _lastSelected)
        {
            _expanded.Add(project);
        }

        _lastSelected = selected;
        Rebuild();
    }

    private void RebuildInbox()
    {
        if (_inbox is null)
        {
            return;
        }

        InboxCount = _inbox.BadgeCount;
        var items = _inbox.Groups.SelectMany(g => g.Entries).Where(e => e.NeedsAction).ToList();
        RailViewModel.Sync(InboxItems, items);
        OnPropertyChanged(nameof(HasInboxItems));
    }

    /// <summary>
    /// 按 Store 重建。项目最多几十个,整批重建比维护增量映射简单,也不会和真源分叉;
    /// 同一个项目 / 项目组的 ViewModel 原地更新,保住控件状态(悬停、动画)。
    /// </summary>
    private void Rebuild()
    {
        var layout = _stores.Layout.State;
        var workspace = _stores.Workspace.State;
        var selection = _stores.Selection.State;
        HasProjects = layout.Rail.Projects().Any();
        HasCurrentProject = selection.Project is { } current && workspace.Projects.ContainsKey(current);
        var showsDevices = DeviceDimension.Shows(
            workspace.Devices.Values.Count(d => d.Connection != DeviceConnection.Revoked),
            workspace.Devices.Values.Any(d => d.Kind != DeviceKind.Local && d.Connection != DeviceConnection.Revoked),
            workspace.Projects.Values.Any(p => p.Environment == EnvironmentKind.Ssh));

        var next = new List<SidebarNodeViewModel>();
        foreach (var node in layout.Rail.Items)
        {
            switch (node)
            {
                case RailNode.Project p:
                    var project = ProjectNode(p.Ref, layout, workspace, selection, showsDevices);
                    project.Tile.IsInGroup = false;
                    next.Add(project);
                    break;
                case RailNode.Group g:
                    var group = Items.OfType<SidebarGroupViewModel>().FirstOrDefault(x => x.Group.GroupId == g.Info.Id)
                                ?? new SidebarGroupViewModel(new RailGroupViewModel(g.Info.Id, _actions, _dialogs, _text));
                    var members = g.Members.Select(m => ProjectNode(m, layout, workspace, selection, showsDevices)).ToList();
                    members.ForEach(m => m.Tile.IsInGroup = true);
                    group.Update(g.Info, members, ProjectActivityView.Sum(g.Members.Select(workspace.ActivityOf)));
                    next.Add(group);
                    break;
            }
        }

        RailViewModel.Sync(Items, next);
    }

    private SidebarProjectViewModel ProjectNode(ProjectRef project, LayoutState layout, WorkspaceState workspace, SelectionState selection, bool showsDevices)
    {
        var vm = FindNode(project) ?? new SidebarProjectViewModel(new RailProjectViewModel(project, _actions, EditIconAsync, RemoveProjectAsync), this);
        workspace.Projects.TryGetValue(project, out var info);
        workspace.Devices.TryGetValue(project.DeviceId, out var device);
        layout.Appearance.TryGetValue(project, out var appearance);
        vm.Tile.Update(info, device, appearance, workspace.ActivityOf(project), project == selection.Project);

        var connection = device?.Connection ?? DeviceConnection.Connecting;
        var expanded = _expanded.Contains(project);
        var showArchived = _showArchived.Contains(project);
        var mine = workspace.Sessions.Where(s => s.Project == project).ToList();
        var list = SidebarSessions.Arrange(
            mine,
            s => SessionActivityView.PendingOf(s.Id, device?.Index, connection),
            s => SessionActivityView.Of(s.Id, device?.Index, connection) == SessionActivity.Running,
            selection.Project == project ? selection.SessionId : null,
            _showAll.Contains(project),
            showArchived);
        var sessions = expanded
            ? list.Visible.Select(s => new SessionItemViewModel(s, SessionActivityView.Of(s.Id, device?.Index, connection),
                selection.Project == project && s.Id == selection.SessionId, _rows, SessionActivityView.PendingOf(s.Id, device?.Index, connection))).ToList()
            : [];

        var deviceLabel = info?.Environment == EnvironmentKind.Ssh && !string.IsNullOrEmpty(info.EnvironmentName)
            ? info.EnvironmentName
            : showsDevices && device is { Kind: not DeviceKind.Local } ? device.Name : null;
        var local = device?.Kind == DeviceKind.Local && info?.Environment != EnvironmentKind.Ssh;
        vm.Update(info?.Path ?? "", deviceLabel, local, expanded, sessions, list.Hidden, _showAll.Contains(project), showArchived, mine.Count == 0);
        return vm;
    }

    private SidebarProjectViewModel? FindNode(ProjectRef project)
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case SidebarProjectViewModel p when p.Project == project:
                    return p;
                case SidebarGroupViewModel g when g.Members.FirstOrDefault(m => m.Project == project) is { } member:
                    return member;
            }
        }

        return null;
    }
}

public abstract class SidebarNodeViewModel : ObservableObject;

/// <summary>项目树里的一个项目组:一行可折叠的标题。折叠状态、重命名、改色、解散都沿用 <see cref="RailGroupViewModel"/>(落地在 <see cref="LayoutActions"/>)。</summary>
public sealed partial class SidebarGroupViewModel(RailGroupViewModel group) : SidebarNodeViewModel
{
    public RailGroupViewModel Group { get; } = group;

    public ObservableCollection<SidebarProjectViewModel> Members { get; } = [];

    [ObservableProperty] public partial bool IsOpen { get; private set; }

    /// <summary>折叠时标题右侧的待处理数(组内项目相加,<see cref="ProjectActivityView.Sum"/>)。</summary>
    [ObservableProperty] public partial bool ShowsPending { get; private set; }

    internal void Update(ProjectGroup info, IReadOnlyList<SidebarProjectViewModel> members, ProjectActivity aggregate)
    {
        Group.Update(info, [.. members.Select(m => m.Tile)], aggregate);
        RailViewModel.Sync(Members, members);
        IsOpen = !info.Collapsed;
        ShowsPending = info.Collapsed && aggregate.PendingInteractions > 0;
    }
}

/// <summary>项目树里的一个项目:一行项目标题,展开后是它的会话。图标、名字、状态、右键里的分组 / 图标 / 移除沿用 <see cref="RailProjectViewModel"/>。</summary>
public sealed partial class SidebarProjectViewModel(RailProjectViewModel tile, SidebarViewModel owner) : SidebarNodeViewModel
{
    public RailProjectViewModel Tile { get; } = tile;

    public ProjectRef Project => Tile.Project;

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>项目所在的文件夹(菜单里的「复制路径 / 在文件管理器中显示」)。</summary>
    [ObservableProperty] public partial string Path { get; private set; } = "";

    /// <summary>名字后面写的设备 / SSH 环境名;只有一台本机、没有 SSH 时为 null(03 §11.7)。</summary>
    [ObservableProperty] public partial string? DeviceLabel { get; private set; }

    /// <summary>「本机设备 + 本机环境」:只有这样的项目能在本机的文件管理器里显示。</summary>
    [ObservableProperty] public partial bool IsLocal { get; private set; }

    [ObservableProperty] public partial bool IsExpanded { get; private set; }

    /// <summary>「显示更多」收着的会话条数。</summary>
    [ObservableProperty] public partial int HiddenCount { get; private set; }

    [ObservableProperty] public partial bool ShowsAll { get; private set; }

    [ObservableProperty] public partial bool ShowsArchived { get; private set; }

    /// <summary>展开了,但项目里一个会话都没有。</summary>
    [ObservableProperty] public partial bool ShowsEmpty { get; private set; }

    /// <summary>收起时行尾的待处理数(提醒色)。</summary>
    [ObservableProperty] public partial bool ShowsPending { get; private set; }

    /// <summary>收起、没有待处理、但有 Run 在跑:行尾一个呼吸点。</summary>
    [ObservableProperty] public partial bool ShowsRunning { get; private set; }

    public bool HasMore => HiddenCount > 0;

    /// <summary>「收起」只在点过「显示更多」以后出现。</summary>
    public bool CanShowLess => ShowsAll && IsExpanded;

    [RelayCommand]
    private void Activate() => owner.Activate(this);

    [RelayCommand]
    private void ToggleExpanded() => owner.ToggleExpanded(this);

    [RelayCommand]
    private void ToggleShowAll() => owner.ToggleShowAll(this);

    [RelayCommand]
    private void ToggleArchived() => owner.ToggleArchived(this);

    [RelayCommand]
    private Task NewSessionAsync() => owner.NewSessionInAsync(this);

    [RelayCommand]
    private void NewTerminal() => owner.NewTerminal(this);

    [RelayCommand]
    private void NewFile() => owner.NewFile(this);

    [RelayCommand]
    private void NewFolder() => owner.NewFolder(this);

    [RelayCommand]
    private Task RevealAsync() => owner.RevealAsync(this);

    [RelayCommand]
    private Task CopyPathAsync() => owner.CopyPathAsync(this);

    internal void Update(string path, string? deviceLabel, bool local, bool expanded, IReadOnlyList<SessionItemViewModel> sessions, int hidden, bool showsAll, bool showsArchived, bool empty)
    {
        Path = path;
        DeviceLabel = deviceLabel;
        IsLocal = local;
        IsExpanded = expanded;
        HiddenCount = hidden;
        ShowsAll = showsAll;
        ShowsArchived = showsArchived;
        ShowsEmpty = expanded && empty;
        ShowsPending = !expanded && !Tile.IsOffline && Tile.HasPending;
        ShowsRunning = !expanded && !Tile.IsOffline && !Tile.HasPending && Tile.IsRunning;
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(CanShowLess));

        // 设备的广播很频繁:行的内容没变就不换(换了会重建行控件,打开着的右键菜单、悬停状态都会丢)
        if (Sessions.Count == sessions.Count && Sessions.Zip(sessions).All(pair => SameRow(pair.First, pair.Second)))
        {
            return;
        }

        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(session);
        }
    }

    private static bool SameRow(SessionItemViewModel a, SessionItemViewModel b) =>
        a.Id == b.Id && a.Title == b.Title && a.UpdatedAt == b.UpdatedAt && a.Activity == b.Activity && a.IsSelected == b.IsSelected
        && a.PendingCount == b.PendingCount && a.IsFavorite == b.IsFavorite && a.IsArchived == b.IsArchived;
}
