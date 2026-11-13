using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.ViewModels;

/// <summary>项目栏。只读 Store,修改经过 <see cref="LayoutActions"/>。</summary>
public sealed partial class RailViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly LayoutActions _actions;
    private readonly Func<IDialogService> _dialogs;

    public RailViewModel(ClientStores stores, LayoutActions actions, Func<IDialogService>? dialogs = null)
    {
        _stores = stores;
        _actions = actions;
        _dialogs = dialogs ?? (() => NoDialogs.Instance);
        stores.Layout.Changed += Rebuild;
        stores.Workspace.Changed += Rebuild;
        stores.Selection.Changed += Rebuild;
        Rebuild();
    }

    public ObservableCollection<RailItemViewModel> Items { get; } = [];

    /// <summary>当前布局(拖放规划用)。</summary>
    public RailLayout Layout => _stores.Layout.State.Rail;

    /// <summary>按 <see cref="ProjectRef.ToString"/> 找回项目(拖放数据里只放字符串 id)。</summary>
    public ProjectRef? FindProject(string id) =>
        Layout.Projects().Cast<ProjectRef?>().FirstOrDefault(p => p.ToString() == id);

    /// <summary>落地一次拖放(规则在 RailDropPlanner,落地在 LayoutActions)。</summary>
    public void Drop(RailDrop drop) => _actions.Drop(drop);

    /// <summary>
    /// 按 Store 重建。项目栏最多几十项,整批重建比维护增量映射简单,也不会和真源分叉。
    /// 同一个 key 的 ViewModel 原地更新,保住控件状态(焦点、动画)。
    /// </summary>
    private void Rebuild()
    {
        var layout = _stores.Layout.State;
        var workspace = _stores.Workspace.State;
        var selected = _stores.Selection.State.Project;

        var next = new List<RailItemViewModel>();
        foreach (var node in layout.Rail.Items)
        {
            switch (node)
            {
                case RailNode.Project p:
                    var tile = ProjectTile(p.Ref, layout, workspace, selected);
                    tile.IsInGroup = false;
                    next.Add(tile);
                    break;
                case RailNode.Group g:
                    var group = Items.OfType<RailGroupViewModel>().FirstOrDefault(x => x.GroupId == g.Info.Id)
                                ?? new RailGroupViewModel(g.Info.Id, _actions, _dialogs);
                    var members = g.Members.Select(m => ProjectTile(m, layout, workspace, selected)).ToList();
                    members.ForEach(m => m.IsInGroup = true);
                    var aggregate = ProjectActivityView.Sum(g.Members.Select(workspace.ActivityOf));
                    group.Update(g.Info, members, aggregate);
                    next.Add(group);
                    break;
            }
        }

        Sync(Items, next);
    }

    private RailProjectViewModel ProjectTile(ProjectRef project, LayoutState layout, WorkspaceState workspace, ProjectRef? selected)
    {
        var vm = FindProject(project) ?? new RailProjectViewModel(project, _actions);
        workspace.Projects.TryGetValue(project, out var info);
        workspace.Devices.TryGetValue(project.DeviceId, out var device);
        layout.Appearance.TryGetValue(project, out var appearance);
        vm.Update(info, device, appearance, workspace.ActivityOf(project), project == selected);
        return vm;
    }

    private RailProjectViewModel? FindProject(ProjectRef project)
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case RailProjectViewModel p when p.Project == project:
                    return p;
                case RailGroupViewModel g when g.Members.FirstOrDefault(m => m.Project == project) is { } member:
                    return member;
            }
        }

        return null;
    }

    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], next[i]))
            {
                continue;
            }

            var existing = target.IndexOf(next[i]);
            if (existing > i)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, next[i]);
            }
        }

        while (target.Count > next.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

public abstract class RailItemViewModel : ObservableObject;

/// <summary>项目栏上的一个项目图标。</summary>
public sealed partial class RailProjectViewModel : RailItemViewModel
{
    private readonly LayoutActions _actions;

    public RailProjectViewModel(ProjectRef project, LayoutActions actions)
    {
        Project = project;
        _actions = actions;
    }

    public ProjectRef Project { get; }

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Glyph { get; private set; } = "";
    [ObservableProperty] public partial string Color { get; private set; } = "#64748B";
    [ObservableProperty] public partial bool IsSelected { get; private set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial int PendingCount { get; private set; }
    [ObservableProperty] public partial bool HasPending { get; private set; }
    [ObservableProperty] public partial bool IsOffline { get; private set; }
    [ObservableProperty] public partial bool IsMissing { get; private set; }
    [ObservableProperty] public partial DeviceKind DeviceKind { get; private set; }
    [ObservableProperty] public partial bool IsSsh { get; private set; }
    [ObservableProperty] public partial string Tooltip { get; private set; } = "";

    /// <summary>是否在某个项目组里(右键菜单显示「移出项目组」还是「新建项目组」)。</summary>
    [ObservableProperty] public partial bool IsInGroup { get; internal set; }

    public bool IsCloud => DeviceKind == DeviceKind.Cloud;

    [RelayCommand]
    private void Select() => _actions.Select(Project);

    [RelayCommand]
    private void WrapInGroup() => _actions.WrapInGroup(Project);

    [RelayCommand]
    private void LeaveGroup() => _actions.LeaveGroup(Project);

    [RelayCommand]
    private void RemoveFromRail() => _actions.RemoveFromRail(Project);

    internal void Update(ProjectInfo? info, DeviceInfo? device, ProjectAppearance? appearance, ProjectActivity activity, bool selected)
    {
        Name = info?.Name ?? Project.WorkspaceId;
        Glyph = appearance?.Glyph ?? Initials(Name);
        Color = appearance?.Color ?? "#64748B";
        IsSelected = selected;
        IsRunning = activity.IsRunning;
        PendingCount = activity.PendingInteractions;
        HasPending = activity.PendingInteractions > 0;
        IsOffline = device is null || device.Connection is DeviceConnection.Offline or DeviceConnection.Connecting || activity.Unknown;
        IsMissing = info is null;
        DeviceKind = device?.Kind ?? DeviceKind.Remote;
        OnPropertyChanged(nameof(IsCloud));
        IsSsh = info?.Environment == EnvironmentKind.Ssh;
        Tooltip = device is null ? Name : $"{Name} · {device.Name}";
    }

    /// <summary>项目名的首字母(中文取首字)。</summary>
    internal static string Initials(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "?";
        }

        var first = trimmed.EnumerateRunes().First();
        return first.Utf16SequenceLength > 1 || first.Value > 0x2E80 ? first.ToString() : char.ToUpperInvariant(trimmed[0]).ToString();
    }
}

/// <summary>项目栏上的一个项目组。</summary>
public sealed partial class RailGroupViewModel : RailItemViewModel
{
    private readonly LayoutActions _actions;
    private readonly Func<IDialogService> _dialogs;

    public RailGroupViewModel(string groupId, LayoutActions actions, Func<IDialogService> dialogs)
    {
        GroupId = groupId;
        _actions = actions;
        _dialogs = dialogs;
    }

    public string GroupId { get; }

    public ObservableCollection<RailProjectViewModel> Members { get; } = [];

    /// <summary>收起时显示的 2×2 迷你网格(最多 4 个)。</summary>
    public ObservableCollection<RailProjectViewModel> Preview { get; } = [];

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Color { get; private set; } = "#3B82F6";
    [ObservableProperty] public partial bool IsCollapsed { get; private set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial int PendingCount { get; private set; }
    [ObservableProperty] public partial bool HasPending { get; private set; }
    [ObservableProperty] public partial bool ContainsSelection { get; private set; }

    [RelayCommand]
    private void Toggle() => _actions.ToggleGroup(GroupId);

    [RelayCommand]
    private void Dissolve() => _actions.DissolveGroup(GroupId);

    [RelayCommand]
    private void Recolor(string color) => _actions.RecolorGroup(GroupId, color);

    /// <summary>右键「重命名」:弹窗输入新名字;取消或空名字不改。</summary>
    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await _dialogs().PromptTextAsync("重命名项目组", "名字会同步到你的所有设备。", "名称", Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _actions.RenameGroup(GroupId, name.Trim());
        }
    }

    public IReadOnlyList<string> Palette => LayoutActions.GroupPalette;

    internal void Update(ProjectGroup info, IReadOnlyList<RailProjectViewModel> members, ProjectActivity aggregate)
    {
        Name = info.Name;
        Color = info.Color;
        IsCollapsed = info.Collapsed;
        RailViewModel.Sync(Members, members);
        RailViewModel.Sync(Preview, members.Take(4).ToList());

        // 组角标只来自 ProjectActivityView.Sum,不在这里另算(AGENTS.md §3 规则 3)。
        IsRunning = aggregate.IsRunning;
        PendingCount = aggregate.PendingInteractions;
        HasPending = PendingCount > 0;
        ContainsSelection = members.Any(m => m.IsSelected);
    }
}
