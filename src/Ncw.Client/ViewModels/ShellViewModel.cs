using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>桌面三栏外壳。布局开关(面板收起、检查器)是纯 UI 状态,放在这里而不是 Store。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ClientStores stores)
    {
        Stores = stores;
        Actions = new LayoutActions(stores);
        Rail = new RailViewModel(stores, Actions);
        Panel = new ProjectPanelViewModel(stores, Actions);
        Workspace = new WorkspaceViewModel(stores);
    }

    public ClientStores Stores { get; }

    public LayoutActions Actions { get; }

    public RailViewModel Rail { get; }

    public ProjectPanelViewModel Panel { get; }

    public WorkspaceViewModel Workspace { get; }

    /// <summary>⌘B:收起 / 展开项目面板。</summary>
    [ObservableProperty] public partial bool IsPanelOpen { get; set; } = true;

    /// <summary>⌘⌥I:右侧检查器。</summary>
    [ObservableProperty] public partial bool IsInspectorOpen { get; set; } = true;

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void SelectByIndex(int oneBased) => Actions.SelectByIndex(oneBased);
}
