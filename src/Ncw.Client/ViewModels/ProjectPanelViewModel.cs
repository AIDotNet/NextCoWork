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

    public ProjectPanelViewModel(ClientStores stores, LayoutActions actions, ProjectActions? projects = null, SessionActions? sessions = null,
        Func<IDialogService>? dialogs = null, Func<IClientText>? text = null)
    {
        _stores = stores;
        _actions = actions;
        _projects = projects;
        _sessions = sessions;
        _dialogs = dialogs ?? (() => NoDialogs.Instance);
        _text = text ?? (() => DefaultClientText.Instance);
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
    [ObservableProperty] public partial string? Branch { get; private set; }
    [ObservableProperty] public partial bool WorkingTreeClean { get; private set; }

    /// <summary>设备没连上:会话列表是上次的镜像,运行状态未知(规则 7)。</summary>
    [ObservableProperty] public partial bool IsDeviceUnknown { get; private set; }

    /// <summary>搜索框草稿,纯 UI 状态。</summary>
    [ObservableProperty] public partial string SearchText { get; set; } = "";

    /// <summary>「显示已归档」,纯 UI 状态。</summary>
    [ObservableProperty] public partial bool ShowArchived { get; set; }

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
    private Task AddProjectAsync() => _projects?.AddLocalProjectAsync() ?? Task.CompletedTask;

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

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project || !workspace.Projects.TryGetValue(project, out var info))
        {
            HasProject = false;
            Sessions.Clear();
            Terminals.Clear();
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
        EnvironmentName = info.EnvironmentName;
        Branch = info.Branch;
        WorkingTreeClean = info.WorkingTreeClean;
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        IsDeviceUnknown = connection != DeviceConnection.Connected;

        var mine = workspace.Sessions.Where(s => s.Project == project).ToList();
        HasAnySession = mine.Count > 0;
        var items = SessionListQuery.Apply(mine, SearchText, ShowArchived)
            .Select(s => new SessionItemViewModel(s, SessionActivityView.Of(s.Id, device?.Index, connection), s.Id == selection.SessionId, this))
            .ToList();
        Replace(Sessions, items);
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowsEmptyHint));
        OnPropertyChanged(nameof(ShowsNoMatch));

        var terminals = workspace.Terminals
            .Where(t => t.Project == project)
            .Select(t => new TerminalItemViewModel(t))
            .ToList();
        Replace(Terminals, terminals);
    }

    // ───────────────────────── 会话行的意图(都转给 SessionActions) ─────────────────────────

    internal void Open(SessionItemViewModel item)
    {
        if (_stores.Selection.State.Project is { } project)
        {
            _sessions?.Open(project, item.Id);
        }
    }

    /// <summary>停止这个会话正在跑的 Run(手机会话列表左滑的「停止」;桌面标签栏的停止在输入框上)。</summary>
    internal async Task StopAsync(SessionItemViewModel item)
    {
        if (_sessions is not null && _stores.Selection.State.Project is { } project)
        {
            await _sessions.StopAsync(project, item.Id);
        }
    }

    internal async Task RenameAsync(SessionItemViewModel item)
    {
        if (_sessions is null || _stores.Selection.State.Project is not { } project)
        {
            return;
        }

        var text = _text();
        var title = await _dialogs().PromptTextAsync(text.RenameSessionTitle, text.RenameSessionDescription, text.NameLabel, item.Title);
        if (!string.IsNullOrWhiteSpace(title) && title != item.Title)
        {
            await _sessions.RenameAsync(project, item.Id, title);
        }
    }

    internal async Task SetFavoriteAsync(SessionItemViewModel item, bool favorited)
    {
        if (_sessions is not null && _stores.Selection.State.Project is { } project)
        {
            await _sessions.FavoriteAsync(project, item.Id, favorited);
        }
    }

    internal async Task SetArchivedAsync(SessionItemViewModel item, bool archived)
    {
        if (_sessions is not null && _stores.Selection.State.Project is { } project)
        {
            await _sessions.ArchiveAsync(project, item.Id, archived);
        }
    }

    /// <summary>删除会话:先二次确认,确认了才发(设备上有 Run 在跑会先中止)。</summary>
    internal async Task DeleteAsync(SessionItemViewModel item)
    {
        if (_sessions is null || _stores.Selection.State.Project is not { } project)
        {
            return;
        }

        var text = _text();
        if (await _dialogs().ConfirmAsync(text.DeleteSessionTitle, text.DeleteSessionMessage(item.Title), text.DeleteLabel))
        {
            await _sessions.DeleteAsync(project, item.Id);
        }
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

/// <summary>会话列表里的一行。运行状态是 <see cref="SessionActivity"/>(Core 的唯一派生函数),设备断开时为未知:既不显示运行也不显示结束。</summary>
public sealed partial class SessionItemViewModel(SessionSummary session, SessionActivity activity, bool isSelected, ProjectPanelViewModel panel) : ObservableObject
{
    public string Id => session.Id;

    public string Title => session.Title;

    public DateTimeOffset UpdatedAt => session.UpdatedAt;

    public SessionActivity Activity { get; } = activity;

    public bool IsRunning => Activity == SessionActivity.Running;

    public bool IsUnknown => Activity == SessionActivity.Unknown;

    public bool IsFavorite => session.Favorited;

    public bool IsArchived => session.Archived;

    public bool IsSelected { get; } = isSelected;

    [RelayCommand]
    private void Open() => panel.Open(this);

    [RelayCommand]
    private Task StopAsync() => panel.StopAsync(this);

    [RelayCommand]
    private Task RenameAsync() => panel.RenameAsync(this);

    [RelayCommand]
    private Task ToggleFavoriteAsync() => panel.SetFavoriteAsync(this, !IsFavorite);

    [RelayCommand]
    private Task ToggleArchivedAsync() => panel.SetArchivedAsync(this, !IsArchived);

    [RelayCommand]
    private Task DeleteAsync() => panel.DeleteAsync(this);
}

public sealed class TerminalItemViewModel(TerminalSummary terminal)
{
    public string Id => terminal.Id;

    public string Title => terminal.Title;

    public bool IsExternalAgent => terminal.Kind == TerminalKind.ExternalAgent;
}
