using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>栏 3:主工作区的标签页(M1 只有界面,内容在 M4 接入)。</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly ClientStores _stores;

    public WorkspaceViewModel(ClientStores stores)
    {
        _stores = stores;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    [ObservableProperty] public partial WorkspaceTabViewModel? ActiveTab { get; set; }

    /// <summary>上下文占用(0–100),M1 用模拟值。</summary>
    [ObservableProperty] public partial int ContextPercent { get; private set; }

    /// <summary>输入框草稿,纯 UI 状态。</summary>
    [ObservableProperty] public partial string Draft { get; set; } = "";

    [RelayCommand]
    private void Activate(WorkspaceTabViewModel tab)
    {
        foreach (var t in Tabs)
        {
            t.IsActive = ReferenceEquals(t, tab);
        }

        ActiveTab = tab;
    }

    [RelayCommand]
    private void Close(WorkspaceTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (tab.IsActive && Tabs.Count > 0)
        {
            Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
        }
        else if (Tabs.Count == 0)
        {
            ActiveTab = null;
        }
    }

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        Tabs.Clear();
        ActiveTab = null;
        if (selection.Project is not { } project)
        {
            return;
        }

        var sessions = workspace.Sessions.Where(s => s.Project == project).OrderByDescending(s => s.UpdatedAt).Take(2).ToList();
        foreach (var session in sessions)
        {
            Tabs.Add(new WorkspaceTabViewModel(session.Title, WorkspaceTabKind.Session));
        }

        foreach (var terminal in workspace.Terminals.Where(t => t.Project == project).Take(1))
        {
            Tabs.Add(new WorkspaceTabViewModel(terminal.Title, WorkspaceTabKind.Terminal));
        }

        if (Tabs.Count > 0)
        {
            var preferred = selection.SessionId is { } id
                ? Tabs.FirstOrDefault(t => t.Title == workspace.Sessions.FirstOrDefault(s => s.Id == id)?.Title)
                : null;
            Activate(preferred ?? Tabs[0]);
        }

        ContextPercent = Tabs.Count == 0 ? 0 : 51;
    }
}

public enum WorkspaceTabKind
{
    Session,
    Terminal,
    File,
    Diff,
    Browser,
    Settings,
}

public sealed partial class WorkspaceTabViewModel(string title, WorkspaceTabKind kind) : ObservableObject
{
    public string Title { get; } = title;

    public WorkspaceTabKind Kind { get; } = kind;

    public bool IsSession => Kind == WorkspaceTabKind.Session;

    public bool IsTerminal => Kind == WorkspaceTabKind.Terminal;

    [ObservableProperty] public partial bool IsActive { get; set; }
}
