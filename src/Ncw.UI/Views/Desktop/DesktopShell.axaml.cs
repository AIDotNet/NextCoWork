using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop;

/// <summary>宽度断点(docs/design/03 §2)。</summary>
public enum ShellBreakpoint
{
    /// <summary>&lt; 720:应当切到移动端外壳(M5)。桌面窗口最小宽度保证不会进入这里。</summary>
    Compact,
    /// <summary>720–1024:项目面板以抽屉形式出现,默认隐藏。</summary>
    Narrow,
    /// <summary>1024–1440:项目栏 + 项目面板 + 工作区;检查器以抽屉覆盖。</summary>
    Medium,
    /// <summary>≥ 1440:四栏全部停靠。</summary>
    Wide,
}

public partial class DesktopShell : UserControl
{
    private ShellViewModel? _vm;

    public DesktopShell()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
    }

    public ShellBreakpoint Breakpoint { get; private set; } = ShellBreakpoint.Wide;

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
        if (e.PropertyName is nameof(ShellViewModel.IsPanelOpen) or nameof(ShellViewModel.IsInspectorOpen))
        {
            ApplyLayout();
        }
    }

    /// <summary>栏的显示与否 = 断点 × 用户开关。只改可见性,不动 ViewModel(纯 UI 状态)。</summary>
    private void ApplyLayout()
    {
        var panelOpen = _vm?.IsPanelOpen ?? true;
        var inspectorOpen = _vm?.IsInspectorOpen ?? true;

        PanelColumn.IsVisible = panelOpen && Breakpoint >= ShellBreakpoint.Medium;
        InspectorColumn.IsVisible = inspectorOpen && Breakpoint == ShellBreakpoint.Wide;
    }
}
