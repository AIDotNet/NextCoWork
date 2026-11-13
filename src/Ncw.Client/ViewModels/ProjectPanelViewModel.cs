using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>栏 2:当前项目的面板(docs/design/03 §1)。</summary>
public sealed partial class ProjectPanelViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly LayoutActions _actions;

    public ProjectPanelViewModel(ClientStores stores, LayoutActions actions)
    {
        _stores = stores;
        _actions = actions;
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

    /// <summary>搜索框草稿,纯 UI 状态。</summary>
    [ObservableProperty] public partial string SearchText { get; set; } = "";

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public ObservableCollection<TerminalItemViewModel> Terminals { get; } = [];

    partial void OnSearchTextChanged(string value) => Refresh();

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project || !workspace.Projects.TryGetValue(project, out var info))
        {
            HasProject = false;
            Sessions.Clear();
            Terminals.Clear();
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

        var filter = SearchText.Trim();
        var sessions = workspace.Sessions
            .Where(s => s.Project == project && (filter.Length == 0 || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new SessionItemViewModel(s, workspace.IsSessionRunning(s), s.Id == selection.SessionId, _actions))
            .ToList();
        Replace(Sessions, sessions);

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

public sealed partial class SessionItemViewModel(SessionSummary session, bool isRunning, bool isSelected, LayoutActions actions) : ObservableObject
{
    public string Id => session.Id;

    public string Title => session.Title;

    public DateTimeOffset UpdatedAt => session.UpdatedAt;

    public bool IsRunning { get; } = isRunning;

    public bool IsSelected { get; } = isSelected;

    [RelayCommand]
    private void Open() => actions.SelectSession(session.Id);
}

public sealed class TerminalItemViewModel(TerminalSummary terminal)
{
    public string Id => terminal.Id;

    public string Title => terminal.Title;

    public bool IsExternalAgent => terminal.Kind == TerminalKind.ExternalAgent;
}
