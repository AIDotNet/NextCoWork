using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 右侧面板:默认是文件管理器;Git、上下文、历史通过「+」添加;当前会话有子代理时自动出现「子代理」页签。
/// 「添加了哪些页签、当前选中哪个」是纯 UI 状态,放在这里;文件、变更、子代理的事实都从 Store 读。
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly TimeProvider _clock;
    private readonly List<InspectorTabKind> _added = [];

    public InspectorViewModel(ClientStores stores, TimeProvider clock)
    {
        _stores = stores;
        _clock = clock;
        stores.Workspace.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<InspectorTabViewModel> Tabs { get; } = [];

    /// <summary>「+」菜单里还能添加的页签。</summary>
    public ObservableCollection<InspectorTabKind> Addable { get; } = [];

    public FileTreeViewModel Files { get; } = new();

    public ObservableCollection<GitChangeItemViewModel> GitChanges { get; } = [];

    public ObservableCollection<SubagentItemViewModel> Subagents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiles), nameof(IsGit), nameof(IsContext), nameof(IsHistory), nameof(IsSubagents))]
    public partial InspectorTabKind Active { get; private set; } = InspectorTabKind.Files;

    [ObservableProperty] public partial bool HasAddable { get; private set; }

    [ObservableProperty] public partial string? Branch { get; private set; }

    public bool IsFiles => Active == InspectorTabKind.Files;

    public bool IsGit => Active == InspectorTabKind.Git;

    public bool IsContext => Active == InspectorTabKind.Context;

    public bool IsHistory => Active == InspectorTabKind.History;

    public bool IsSubagents => Active == InspectorTabKind.Subagents;

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
        if (!_added.Contains(kind) && InspectorTabs.Addable.Contains(kind))
        {
            _added.Add(kind);
        }

        Active = kind;
        Refresh();
    }

    [RelayCommand]
    private void Close(InspectorTabKind kind)
    {
        _added.Remove(kind);
        if (Active == kind)
        {
            Active = InspectorTabKind.Files;
        }

        Refresh();
    }

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        var subagents = selection.SessionId is { } sessionId
            ? workspace.SubagentsOf(sessionId).ToList()
            : [];

        var kinds = InspectorTabs.Compose(_added, subagents.Count > 0);
        if (!kinds.Contains(Active))
        {
            // 子代理页签随会话消失时回到文件管理器。
            Active = InspectorTabKind.Files;
        }

        Tabs.Clear();
        foreach (var kind in kinds)
        {
            Tabs.Add(new InspectorTabViewModel(kind, kind == InspectorTabKind.Subagents ? subagents.Count(s => !SubagentView.IsTerminal(s.Status)) : 0));
        }

        SyncTabs();

        Addable.Clear();
        foreach (var kind in InspectorTabs.Remaining(_added))
        {
            Addable.Add(kind);
        }

        HasAddable = Addable.Count > 0;

        // 文件与 Git 变更
        var project = selection.Project;
        var entries = project is { } p && workspace.Files.TryGetValue(p, out var files) ? files : default;
        var info = project is { } q && workspace.Projects.TryGetValue(q, out var i) ? i : null;
        Files.Load(info?.Name ?? "", info?.Path ?? "", entries.IsDefault ? [] : entries);
        Branch = info?.Branch;

        GitChanges.Clear();
        if (!entries.IsDefault)
        {
            foreach (var entry in entries.Where(e => e.Change is not null).OrderBy(e => e.RelativePath, StringComparer.Ordinal))
            {
                GitChanges.Add(new GitChangeItemViewModel(entry.RelativePath, entry.Change!.Value));
            }
        }

        // 子代理:状态文案、计时、能否停止都来自 SubagentView.Of
        var now = _clock.GetUtcNow();
        Subagents.Clear();
        foreach (var record in subagents.OrderBy(s => SubagentView.IsTerminal(s.Status)).ThenByDescending(s => s.StartedAt))
        {
            Subagents.Add(new SubagentItemViewModel(record.CallId, SubagentView.Of(record, now)));
        }
    }

    private void SyncTabs()
    {
        foreach (var tab in Tabs)
        {
            tab.IsActive = tab.Kind == Active;
        }
    }
}

public sealed partial class InspectorTabViewModel(InspectorTabKind kind, int count) : ObservableObject
{
    public InspectorTabKind Kind { get; } = kind;

    public string Title { get; } = InspectorTabs.Title(kind);

    /// <summary>文件页签不能关;子代理页签随会话自动出现和消失,也不能手动关。</summary>
    public bool IsClosable { get; } = kind is not (InspectorTabKind.Files or InspectorTabKind.Subagents);

    public int Count { get; } = count;

    public bool HasCount => Count > 0;

    public bool IsFiles => Kind == InspectorTabKind.Files;

    public bool IsGit => Kind == InspectorTabKind.Git;

    public bool IsContext => Kind == InspectorTabKind.Context;

    public bool IsHistory => Kind == InspectorTabKind.History;

    public bool IsSubagents => Kind == InspectorTabKind.Subagents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsClose))]
    public partial bool IsActive { get; set; }

    public bool ShowsClose => IsClosable && IsActive;
}

public sealed class GitChangeItemViewModel(string path, GitChange change)
{
    public string Path { get; } = path;

    public string Name { get; } = path[(path.LastIndexOf('/') + 1)..];

    public string Directory { get; } = path.LastIndexOf('/') is > 0 and var i ? path[..i] : "";

    public GitChange Change { get; } = change;

    public string Letter { get; } = change switch
    {
        GitChange.Modified => "M",
        GitChange.Added => "A",
        GitChange.Deleted => "D",
        GitChange.Renamed => "R",
        _ => "U",
    };

    public bool IsAdded => Change is GitChange.Added or GitChange.Untracked;

    public bool IsDeleted => Change == GitChange.Deleted;

    public bool IsModified => Change is GitChange.Modified or GitChange.Renamed;
}

public sealed class SubagentItemViewModel(string callId, SubagentViewState view)
{
    public string CallId { get; } = callId;

    public string Title { get; } = view.Title;

    public string StatusText { get; } = view.StatusText;

    public string Detail { get; } = view.Detail;

    public bool IsActive { get; } = view.IsActive;

    public bool CanStop { get; } = view.CanStop;

    public bool IsTerminal { get; } = view.IsTerminal;
}
