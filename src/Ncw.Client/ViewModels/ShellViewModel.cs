using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 桌面外壳。栏宽和栏的开关是需要持久化的界面偏好,唯一真源是 <see cref="ClientStores.Prefs"/>,
/// 这里的属性只是读写它的入口;窄窗口下的抽屉开关是不持久化的纯 UI 状态,留在本类。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ClientStores stores, TimeProvider? clock = null, IDialogService? dialogs = null)
    {
        Stores = stores;
        Dialogs = dialogs ?? NoDialogs.Instance;
        Actions = new LayoutActions(stores, () => Text);
        Rail = new RailViewModel(stores, Actions, () => Dialogs, () => Text);
        Panel = new ProjectPanelViewModel(stores, Actions);
        Workspace = new WorkspaceViewModel(stores);
        Inspector = new InspectorViewModel(stores, clock ?? TimeProvider.System);
        stores.Prefs.Changed += OnPrefsChanged;
    }

    public ClientStores Stores { get; }

    public LayoutActions Actions { get; }

    /// <summary>弹窗服务。UI 层装配时换成 Arc Dialog 的实现。</summary>
    public IDialogService Dialogs { get; set; }

    /// <summary>Client 层要给用户看的文字。UI 层装配时换成按当前语言的实现。</summary>
    public IClientText Text { get; set; } = DefaultClientText.Instance;

    public RailViewModel Rail { get; }

    public ProjectPanelViewModel Panel { get; }

    public WorkspaceViewModel Workspace { get; }

    /// <summary>右侧面板:默认文件管理器。</summary>
    public InspectorViewModel Inspector { get; }

    private UiPrefs Prefs => Stores.Prefs.State;

    /// <summary>⌘B:收起 / 展开左侧区域(项目栏 + 项目面板)。</summary>
    public bool IsSidebarOpen
    {
        get => Prefs.IsSidebarOpen;
        set => Stores.Prefs.Update(p => p with { IsSidebarOpen = value });
    }

    /// <summary>⌘⌥I:右侧面板。</summary>
    public bool IsInspectorOpen
    {
        get => Prefs.IsInspectorOpen;
        set => Stores.Prefs.Update(p => p with { IsInspectorOpen = value });
    }

    /// <summary>左侧区域宽度(拖分隔条调整)。</summary>
    public double SidebarWidth
    {
        get => Prefs.SidebarWidth;
        set => Stores.Prefs.Update(p => p with { SidebarWidth = Math.Clamp(value, UiPrefs.SidebarMinWidth, UiPrefs.SidebarMaxWidth) });
    }

    /// <summary>右侧面板宽度(拖分隔条调整)。</summary>
    public double InspectorWidth
    {
        get => Prefs.InspectorWidth;
        set => Stores.Prefs.Update(p => p with { InspectorWidth = Math.Clamp(value, UiPrefs.InspectorMinWidth, UiPrefs.InspectorMaxWidth) });
    }

    /// <summary>窗口是否窄到左侧区域只能以抽屉出现(由视图按断点设置)。</summary>
    [ObservableProperty] public partial bool IsCompact { get; set; }

    /// <summary>窄窗口下左侧抽屉是否打开(纯 UI 状态,不持久化)。</summary>
    [ObservableProperty] public partial bool IsDrawerOpen { get; set; }

    /// <summary>⌘B:宽窗口收起 / 展开左侧区域;窄窗口打开 / 关闭抽屉。</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsCompact)
        {
            IsDrawerOpen = !IsDrawerOpen;
        }
        else
        {
            IsSidebarOpen = !IsSidebarOpen;
        }
    }

    [RelayCommand]
    private void CloseDrawer() => IsDrawerOpen = false;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void SelectByIndex(int oneBased) => Actions.SelectByIndex(oneBased);

    partial void OnIsCompactChanged(bool value)
    {
        if (!value)
        {
            IsDrawerOpen = false;
        }
    }

    private void OnPrefsChanged()
    {
        OnPropertyChanged(nameof(IsSidebarOpen));
        OnPropertyChanged(nameof(IsInspectorOpen));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(InspectorWidth));
    }
}
