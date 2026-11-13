using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>桌面三栏外壳。布局开关(面板收起、检查器)是纯 UI 状态,放在这里而不是 Store。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ClientStores stores, TimeProvider? clock = null)
    {
        Stores = stores;
        Actions = new LayoutActions(stores);
        Rail = new RailViewModel(stores, Actions);
        Panel = new ProjectPanelViewModel(stores, Actions);
        Workspace = new WorkspaceViewModel(stores);
        Inspector = new InspectorViewModel(stores, clock ?? TimeProvider.System);
    }

    public ClientStores Stores { get; }

    public LayoutActions Actions { get; }

    public RailViewModel Rail { get; }

    public ProjectPanelViewModel Panel { get; }

    public WorkspaceViewModel Workspace { get; }

    /// <summary>右侧面板:默认文件管理器。</summary>
    public InspectorViewModel Inspector { get; }

    /// <summary>⌘B:收起 / 展开左侧区域(项目栏 + 项目面板)。</summary>
    [ObservableProperty] public partial bool IsSidebarOpen { get; set; } = true;

    public const double SidebarDefaultWidth = 352;
    public const double SidebarMinWidth = 304;   // 项目栏 64 + 项目面板 240
    public const double SidebarMaxWidth = 484;   // 项目栏 64 + 项目面板 420
    public const double InspectorDefaultWidth = 320;
    public const double InspectorMinWidth = 260;
    public const double InspectorMaxWidth = 520;

    /// <summary>左侧区域宽度(拖分隔条调整),纯 UI 状态。</summary>
    public double SidebarWidth
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, SidebarMinWidth, SidebarMaxWidth));
    } = SidebarDefaultWidth;

    /// <summary>右侧面板宽度(拖分隔条调整),纯 UI 状态。</summary>
    public double InspectorWidth
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, InspectorMinWidth, InspectorMaxWidth));
    } = InspectorDefaultWidth;

    /// <summary>⌘⌥I:右侧面板。</summary>
    [ObservableProperty] public partial bool IsInspectorOpen { get; set; } = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void SelectByIndex(int oneBased) => Actions.SelectByIndex(oneBased);
}
