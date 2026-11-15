using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 栏 3 顶部的标签栏。现在只有会话标签(终端、文件、Diff 等随各自的功能加入)。
/// 「打开了哪些标签」是纯 UI 状态,按项目各记一份;标签的标题、运行状态都从 Store 读,选中哪个会话是 <see cref="ClientStores.Selection"/> 的事实。
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly SessionActions? _sessions;
    private readonly Dictionary<ProjectRef, List<string>> _open = [];

    public WorkspaceViewModel(ClientStores stores, SessionActions? sessions = null)
    {
        _stores = stores;
        _sessions = sessions;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    [ObservableProperty] public partial WorkspaceTabViewModel? ActiveTab { get; private set; }

    [RelayCommand]
    private void Activate(WorkspaceTabViewModel tab)
    {
        if (_stores.Selection.State.Project is { } project)
        {
            _sessions?.Open(project, tab.SessionId);
        }
    }

    [RelayCommand]
    private void Close(WorkspaceTabViewModel tab)
    {
        if (_stores.Selection.State.Project is not { } project || !_open.TryGetValue(project, out var list))
        {
            return;
        }

        var index = list.IndexOf(tab.SessionId);
        list.Remove(tab.SessionId);
        if (tab.IsActive)
        {
            if (list.Count > 0)
            {
                _sessions?.Open(project, list[Math.Min(index, list.Count - 1)]);
            }
            else
            {
                _sessions?.Deselect(project);
            }
        }

        Refresh();
    }

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project)
        {
            Tabs.Clear();
            ActiveTab = null;
            return;
        }

        if (!_open.TryGetValue(project, out var list))
        {
            _open[project] = list = [];
        }

        // 选中的会话一定有标签;设备上已经删掉的会话(列表里没有了)不再有标签
        if (selection.SessionId is { } selected && !list.Contains(selected))
        {
            list.Add(selected);
        }

        var known = workspace.Sessions.Where(s => s.Project == project).ToDictionary(s => s.Id);
        if (known.Count > 0)
        {
            list.RemoveAll(id => id != selection.SessionId && !known.ContainsKey(id));
        }

        var device = workspace.Devices.GetValueOrDefault(project.DeviceId);
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        var tabs = list.Select(id => new WorkspaceTabViewModel(
            id,
            known.TryGetValue(id, out var s) ? s.Title : _stores.Conversations.State.Sessions.GetValueOrDefault(new SessionRef(project.DeviceId, id))?.Meta.Title ?? "",
            SessionActivityView.Of(id, device?.Index, connection),
            id == selection.SessionId)).ToList();

        if (!tabs.SequenceEqual(Tabs, TabComparer.Instance))
        {
            Tabs.Clear();
            foreach (var tab in tabs)
            {
                Tabs.Add(tab);
            }
        }

        ActiveTab = Tabs.FirstOrDefault(t => t.IsActive);
    }

    private sealed class TabComparer : IEqualityComparer<WorkspaceTabViewModel>
    {
        public static readonly TabComparer Instance = new();

        public bool Equals(WorkspaceTabViewModel? x, WorkspaceTabViewModel? y) =>
            x is not null && y is not null && x.SessionId == y.SessionId && x.Title == y.Title && x.Activity == y.Activity && x.IsActive == y.IsActive;

        public int GetHashCode(WorkspaceTabViewModel obj) => obj.SessionId.GetHashCode(StringComparison.Ordinal);
    }
}

public sealed class WorkspaceTabViewModel(string sessionId, string title, SessionActivity activity, bool isActive)
{
    public string SessionId { get; } = sessionId;

    public string Title { get; } = title;

    public SessionActivity Activity { get; } = activity;

    public bool IsRunning => Activity == SessionActivity.Running;

    public bool IsUnknown => Activity == SessionActivity.Unknown;

    public bool IsActive { get; } = isActive;
}
