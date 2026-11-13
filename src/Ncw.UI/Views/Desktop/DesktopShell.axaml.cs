using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Chrome;

namespace Ncw.UI.Views.Desktop;

/// <summary>宽度断点(docs/design/03 §2)。</summary>
public enum ShellBreakpoint
{
    /// <summary>&lt; 720:应当切到移动端外壳(M5)。桌面窗口最小宽度保证不会进入这里。</summary>
    Compact,
    /// <summary>720–1024:左侧区域默认收起(以后以抽屉形式出现)。</summary>
    Narrow,
    /// <summary>1024–1440:左侧区域 + 工作区;检查器默认收起。</summary>
    Medium,
    /// <summary>≥ 1440:三栏 + 检查器全部停靠。</summary>
    Wide,
}

public partial class DesktopShell : UserControl
{
    private ShellViewModel? _vm;
    private WindowChrome _chrome = WindowChrome.ForCurrentPlatform();

    public DesktopShell()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        ApplyChrome();
    }

    public ShellBreakpoint Breakpoint { get; private set; } = ShellBreakpoint.Wide;

    /// <summary>平台留给应用的空间。默认按当前操作系统;测试可以指定。</summary>
    public WindowChrome Chrome
    {
        get => _chrome;
        set
        {
            _chrome = value;
            ApplyChrome();
            ApplyLayout();
        }
    }

    /// <summary>左侧区域是否显示(测试和窗口拖动区计算用)。</summary>
    public bool IsSidebarVisible => LeftArea.IsVisible;

    /// <summary>检查器是否显示。</summary>
    public bool IsInspectorVisible => InspectorColumn.IsVisible;

    public static ShellBreakpoint BreakpointFor(double width) => width switch
    {
        < 720 => ShellBreakpoint.Compact,
        < 1024 => ShellBreakpoint.Narrow,
        < 1440 => ShellBreakpoint.Medium,
        _ => ShellBreakpoint.Wide,
    };

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Breakpoint = BreakpointFor(e.NewSize.Width);
        ApplyLayout();
    }

    private void AttachViewModel()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as ShellViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            // 弹窗盖在这个外壳所在的窗口上。
            _vm.Dialogs = new NcwDialogService(this);
        }

        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsSidebarOpen) or nameof(ShellViewModel.IsInspectorOpen)
            or nameof(ShellViewModel.SidebarWidth) or nameof(ShellViewModel.InspectorWidth) or nameof(ShellViewModel.IsDrawerOpen))
        {
            ApplyLayout();
        }
    }

    /// <summary>窄窗口下抽屉是否打开(测试用)。</summary>
    public bool IsDrawerVisible { get; private set; }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseDrawerCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>拖完分隔条:把实际列宽写回偏好(Prefs Store),下次启动沿用。</summary>
    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (ReferenceEquals(sender, LeftSplitter))
        {
            _vm.SidebarWidth = Main.ColumnDefinitions[0].ActualWidth;
        }
        else
        {
            _vm.InspectorWidth = Main.ColumnDefinitions[4].ActualWidth;
        }
    }

    private void ApplyChrome()
    {
        SidebarInset.Width = _chrome.LeftInset;
        CollapsedInset.Width = _chrome.LeftInset;
        Captions.IsVisible = _chrome.ShowsCaptionButtons;
    }

    /// <summary>
    /// 栏的显示 = 断点 × 用户开关。左侧区域收起时,它的头部(红绿灯留白、名称、展开按钮)并入标签栏最左边;
    /// 检查器收起时,标签栏右端给右上角常驻按钮让出位置。
    /// </summary>
    private void ApplyLayout()
    {
        var sidebarOpen = _vm?.IsSidebarOpen ?? true;
        var inspectorOpen = _vm?.IsInspectorOpen ?? true;
        var compact = Breakpoint < ShellBreakpoint.Medium;
        if (_vm is not null)
        {
            _vm.IsCompact = compact;
        }

        var drawer = compact && (_vm?.IsDrawerOpen ?? false);
        IsDrawerVisible = drawer;
        DrawerScrim.IsVisible = drawer;
        Grid.SetColumnSpan(LeftArea, drawer ? 3 : 1);
        LeftArea.ZIndex = drawer ? 10 : 0;
        LeftArea.HorizontalAlignment = drawer ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        LeftArea.Width = drawer ? _vm?.SidebarWidth ?? UiPrefs.SidebarDefaultWidth : double.NaN;
        LeftArea.Effect = drawer ? new DropShadowEffect { BlurRadius = 24, OffsetY = 0, Opacity = 0.35 } : null;

        LeftArea.IsVisible = drawer || (sidebarOpen && !compact);
        InspectorColumn.IsVisible = inspectorOpen && Breakpoint == ShellBreakpoint.Wide;
        LeftSplitter.IsVisible = LeftArea.IsVisible && !drawer;
        RightSplitter.IsVisible = InspectorColumn.IsVisible;

        // 列宽:收起的列宽度为 0;展开时用用户拖出来的宽度,并限制在范围内。
        var columns = Main.ColumnDefinitions;
        var docked = LeftArea.IsVisible && !drawer;
        CollapsedLead.IsVisible = !docked;
        columns[0].MinWidth = docked ? UiPrefs.SidebarMinWidth : 0;
        columns[0].MaxWidth = docked ? UiPrefs.SidebarMaxWidth : double.PositiveInfinity;
        columns[0].Width = new GridLength(docked ? _vm?.SidebarWidth ?? UiPrefs.SidebarDefaultWidth : 0);
        columns[1].Width = new GridLength(docked ? 4 : 0);
        columns[3].Width = new GridLength(InspectorColumn.IsVisible ? 4 : 0);
        columns[4].MinWidth = InspectorColumn.IsVisible ? UiPrefs.InspectorMinWidth : 0;
        columns[4].MaxWidth = InspectorColumn.IsVisible ? UiPrefs.InspectorMaxWidth : double.PositiveInfinity;
        columns[4].Width = new GridLength(InspectorColumn.IsVisible ? _vm?.InspectorWidth ?? UiPrefs.InspectorDefaultWidth : 0);

        CollapsedLead.Measure(Size.Infinity);
        var lead = CollapsedLead.IsVisible ? CollapsedLead.DesiredSize.Width : 0;
        TopRight.Measure(Size.Infinity);
        var trailing = InspectorColumn.IsVisible ? 0 : TopRight.DesiredSize.Width + TopRight.Margin.Right;
        Workspace.SetTopRowInsets(lead, trailing);
        Inspector.SetTopRowTrailing(TopRight.DesiredSize.Width + TopRight.Margin.Right, compact: _chrome.ShowsCaptionButtons);
    }
}
