using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Ncw.Client.ViewModels;
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
        }

        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsSidebarOpen) or nameof(ShellViewModel.IsInspectorOpen))
        {
            ApplyLayout();
        }
    }

    /// <summary>拖完分隔条:把实际列宽记回 ViewModel(纯 UI 状态),下次展开沿用。</summary>
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

        LeftArea.IsVisible = sidebarOpen && Breakpoint >= ShellBreakpoint.Medium;
        InspectorColumn.IsVisible = inspectorOpen && Breakpoint == ShellBreakpoint.Wide;
        CollapsedLead.IsVisible = !LeftArea.IsVisible;
        LeftSplitter.IsVisible = LeftArea.IsVisible;
        RightSplitter.IsVisible = InspectorColumn.IsVisible;

        // 列宽:收起的列宽度为 0;展开时用用户拖出来的宽度,并限制在范围内。
        var columns = Main.ColumnDefinitions;
        columns[0].MinWidth = LeftArea.IsVisible ? ShellViewModel.SidebarMinWidth : 0;
        columns[0].MaxWidth = LeftArea.IsVisible ? ShellViewModel.SidebarMaxWidth : double.PositiveInfinity;
        columns[0].Width = new GridLength(LeftArea.IsVisible ? _vm?.SidebarWidth ?? ShellViewModel.SidebarDefaultWidth : 0);
        columns[1].Width = new GridLength(LeftArea.IsVisible ? 4 : 0);
        columns[3].Width = new GridLength(InspectorColumn.IsVisible ? 4 : 0);
        columns[4].MinWidth = InspectorColumn.IsVisible ? ShellViewModel.InspectorMinWidth : 0;
        columns[4].MaxWidth = InspectorColumn.IsVisible ? ShellViewModel.InspectorMaxWidth : double.PositiveInfinity;
        columns[4].Width = new GridLength(InspectorColumn.IsVisible ? _vm?.InspectorWidth ?? ShellViewModel.InspectorDefaultWidth : 0);

        CollapsedLead.Measure(Size.Infinity);
        var lead = CollapsedLead.IsVisible ? CollapsedLead.DesiredSize.Width : 0;
        TopRight.Measure(Size.Infinity);
        var trailing = InspectorColumn.IsVisible ? 0 : TopRight.DesiredSize.Width + TopRight.Margin.Right;
        Workspace.SetTopRowInsets(lead, trailing);
        Inspector.SetTopRowTrailing(TopRight.DesiredSize.Width + TopRight.Margin.Right, compact: _chrome.ShowsCaptionButtons);
    }
}
