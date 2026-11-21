using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>栏 2:当前项目的面板(docs/design/03 §1)。会话列表是 <c>sessions.list</c> 的镜像,运行状态来自 activeRuns 的权威广播。</summary>
public sealed partial class ProjectPanelViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly LayoutActions _actions;
    private readonly ProjectActions? _projects;
    private readonly SessionActions? _sessions;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IClientText> _text;
    private readonly SessionRowIntents _rows;

    public ProjectPanelViewModel(ClientStores stores, LayoutActions actions, ProjectActions? projects = null, SessionActions? sessions = null,
        Func<IDialogService>? dialogs = null, Func<IClientText>? text = null)
    {
        _stores = stores;
        _actions = actions;
        _projects = projects;
        _sessions = sessions;
        _dialogs = dialogs ?? (() => NoDialogs.Instance);
        _text = text ?? (() => DefaultClientText.Instance);
        _rows = new SessionRowIntents(sessions, _dialogs, _text);
        stores.Workspace.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        Refresh();
    }

    [ObservableProperty] public partial bool HasProject { get; private set; }
    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Path { get; private set; } = "";
    [ObservableProperty] public partial string DeviceName { get; private set; } = "";
    [ObservableProperty] public partial DeviceKind DeviceKind { get; private set; }
    [ObservableProperty] public partial string? EnvironmentName { get; private set; }
    [ObservableProperty] public partial bool IsSsh { get; private set; }

    /// <summary>头部要不要写「本机」这类设备名:只有一台本机、没有 SSH 时不写(03 §11.7)。</summary>
    [ObservableProperty] public partial bool ShowsDevice { get; private set; } = true;
    [ObservableProperty] public partial string? Branch { get; private set; }
    [ObservableProperty] public partial bool WorkingTreeClean { get; private set; }

    /// <summary>设备没连上:会话列表是上次的镜像,运行状态未知(规则 7)。</summary>
    [ObservableProperty] public partial bool IsDeviceUnknown { get; private set; }

    /// <summary>搜索框草稿,纯 UI 状态。</summary>
    [ObservableProperty] public partial string SearchText { get; set; } = "";

    /// <summary>「显示已归档」,纯 UI 状态。</summary>
    [ObservableProperty] public partial bool ShowArchived { get; set; }

    /// <summary>
    /// 当前打开的会话(不受搜索 / 「显示已归档」过滤):主工作区右上角「更多」菜单对它操作,和会话行的右键菜单走同一组意图。没有打开的会话时为 null。
    /// </summary>
    [ObservableProperty] public partial SessionItemViewModel? ActiveSession { get; private set; }

    /// <summary>这个项目有没有会话(不论是否被过滤掉):空状态和「没有匹配」要区分。</summary>
    [ObservableProperty] public partial bool HasAnySession { get; private set; }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public ObservableCollection<TerminalItemViewModel> Terminals { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public bool ShowsEmptyHint => HasProject && !HasAnySession;

    public bool ShowsNoMatch => HasAnySession && Sessions.Count == 0;

    /// <summary>点侧栏的「定时任务」入口(外壳把它接到定时任务页)。</summary>
    public event Action? ScheduledTasksRequested;

    [RelayCommand]
    private void OpenScheduledTasks() => ScheduledTasksRequested?.Invoke();

    /// <summary>没有项目时空状态里的「添加文件夹」,与项目栏的「+」是同一个动作。</summary>
    [RelayCommand]
    private Task AddProjectAsync() => _projects?.NewProjectAsync() ?? Task.CompletedTask;

    /// <summary>「新建会话」:在当前项目里建一个并打开(项目面板的 + 和空状态的按钮是同一个意图)。</summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (_sessions is not null && _stores.Selection.State.Project is { } project)
        {
            await _sessions.CreateAsync(project);
        }
    }

    [RelayCommand]
    private void ToggleArchived() => ShowArchived = !ShowArchived;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnShowArchivedChanged(bool value) => Refresh();

    /// <summary>这个项目里的会话(不按搜索 / 归档过滤),供工作区会话标签的右键菜单 / 双击改名复用同一套意图。</summary>
    public SessionItemViewModel? FindSessionItem(string sessionId)
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project || workspace.Sessions.FirstOrDefault(s => s.Project == project && s.Id == sessionId) is not { } summary)
        {
            return null;
        }

        workspace.Devices.TryGetValue(project.DeviceId, out var device);
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        return new SessionItemViewModel(summary, SessionActivityView.Of(summary.Id, device?.Index, connection),
            summary.Id == selection.SessionId, _rows, SessionActivityView.PendingOf(summary.Id, device?.Index, connection));
    }

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project || !workspace.Projects.TryGetValue(project, out var info))
        {
            HasProject = false;
            Sessions.Clear();
            Terminals.Clear();
            ActiveSession = null;
            OnPropertyChanged(nameof(HasSessions));
            return;
        }

        workspace.Devices.TryGetValue(project.DeviceId, out var device);
        HasProject = true;
        Name = info.Name;
        Path = info.Path;
        DeviceName = device?.Name ?? project.DeviceId;
        DeviceKind = device?.Kind ?? DeviceKind.Remote;
        IsSsh = info.Environment == EnvironmentKind.Ssh;
        ShowsDevice = DeviceDimension.Shows(
            workspace.Devices.Values.Count(d => d.Connection != DeviceConnection.Revoked),
            workspace.Devices.Values.Any(d => d.Kind != DeviceKind.Local && d.Connection != DeviceConnection.Revoked),
            workspace.Projects.Values.Any(p => p.Environment == EnvironmentKind.Ssh));
        EnvironmentName = info.EnvironmentName;
        Branch = info.Branch;
        WorkingTreeClean = info.WorkingTreeClean;
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        IsDeviceUnknown = connection != DeviceConnection.Connected;

        var mine = workspace.Sessions.Where(s => s.Project == project).ToList();
        HasAnySession = mine.Count > 0;
        var items = SessionListQuery.Apply(mine, SearchText, ShowArchived)
            .Select(s => new SessionItemViewModel(s, SessionActivityView.Of(s.Id, device?.Index, connection), s.Id == selection.SessionId, _rows,
                SessionActivityView.PendingOf(s.Id, device?.Index, connection)))
            .ToList();
        Replace(Sessions, items);
        ActiveSession = mine.FirstOrDefault(s => s.Id == selection.SessionId) is { } active
            ? new SessionItemViewModel(active, SessionActivityView.Of(active.Id, device?.Index, connection), true, _rows,
                SessionActivityView.PendingOf(active.Id, device?.Index, connection))
            : null;
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowsEmptyHint));
        OnPropertyChanged(nameof(ShowsNoMatch));

        var terminals = workspace.Terminals
            .Where(t => t.Project == project)
            .Select(t => new TerminalItemViewModel(t))
            .ToList();
        Replace(Terminals, terminals);
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>
/// 会话列表里的一行(桌面侧边栏、手机项目会话页共用)。运行状态是 <see cref="SessionActivity"/>(Core 的唯一派生函数),设备断开时为未知:既不显示运行也不显示结束;
/// 待处理数来自 <see cref="SessionActivityView.PendingOf"/>。意图按行自己的项目转给 <see cref="ISessionRowHost"/>。
/// </summary>
public sealed partial class SessionItemViewModel(SessionSummary session, SessionActivity activity, bool isSelected, ISessionRowHost host, int pendingCount = 0) : ObservableObject
{
    public string Id => session.Id;

    public ProjectRef Project => session.Project;

    public string Title => session.Title;

    public DateTimeOffset UpdatedAt => session.UpdatedAt;

    public SessionActivity Activity { get; } = activity;

    public bool IsRunning => Activity == SessionActivity.Running;

    public bool IsUnknown => Activity == SessionActivity.Unknown;

    public bool IsFavorite => session.Favorited;

    public bool IsArchived => session.Archived;

    public bool IsSelected { get; } = isSelected;

    /// <summary>这个会话里等用户处理的交互数(0 = 没有;断线时也是 0)。</summary>
    public int PendingCount { get; } = pendingCount;

    public bool HasPending => PendingCount > 0;

    [RelayCommand]
    private void Open() => host.Open(this);

    [RelayCommand]
    private Task StopAsync() => host.StopAsync(this);

    [RelayCommand]
    private Task RenameAsync() => host.RenameAsync(this);

    [RelayCommand]
    private Task ToggleFavoriteAsync() => host.SetFavoriteAsync(this, !IsFavorite);

    [RelayCommand]
    private Task ToggleArchivedAsync() => host.SetArchivedAsync(this, !IsArchived);

    [RelayCommand]
    private Task DeleteAsync() => host.DeleteAsync(this);
}

public sealed class TerminalItemViewModel(TerminalSummary terminal)
{
    public string Id => terminal.Id;

    public string Title => terminal.Title;

    public bool IsExternalAgent => terminal.Kind == TerminalKind.ExternalAgent;
}
