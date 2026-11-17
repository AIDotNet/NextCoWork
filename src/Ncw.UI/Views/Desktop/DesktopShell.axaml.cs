using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
        WatchOverlays();
    }

    private IDisposable? _airspace;
    private List<Control> _overlays = [];

    /// <summary>
    /// 盖在工作区上的整窗覆盖层(设置页、首次启动页、数据页、窄屏抽屉的遮罩)打开时,挂起所有 widget 的 WebView:原生视图是盖在窗口上的,
    /// Avalonia 画的覆盖层盖不住它(<see cref="NcwWidgetAirspace"/>)。
    /// <para>
    /// <see cref="NcwWidgetAirspace"/> 是全局计数:句柄不释放,所有窗口里的 widget 就一直被挂起。所以句柄的生命周期跟着本控件挂不挂在视觉树上走——
    /// 卸下(窗口关闭、外壳被替换)时无条件释放,重新挂上时按当前覆盖层状态重新计算;不能只在「覆盖层变不可见」时才释放(窗口在覆盖层还开着时被关掉就永远泄漏)。
    /// </para>
    /// </summary>
    private void WatchOverlays()
    {
        _overlays = Root.Children.OfType<Control>().Where(c => c is SettingsOverlay or DataGateOverlay or OnboardingOverlay or StartupOverlay or CommandPalette).Append(DrawerScrim).ToList();
        foreach (var overlay in _overlays)
        {
            overlay.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                {
                    RecomputeAirspace();
                }
            };
        }

        // 启动加载界面淡出后自己摘掉:别让这里的记录继续引用它
        if (_overlays.OfType<StartupOverlay>().FirstOrDefault() is { } startup)
        {
            startup.Released += (_, _) =>
            {
                _overlays.Remove(startup);
                RecomputeAirspace();
            };
        }

        RecomputeAirspace();
    }

    private void RecomputeAirspace()
    {
        // 没挂在视觉树上时不持有挂起:此时可见的覆盖层没有被画出来,不该影响别的窗口。
        var any = TopLevel.GetTopLevel(this) is not null && _overlays.Any(c => c.IsVisible);
        if (any && _airspace is null)
        {
            _airspace = NcwWidgetAirspace.Suspend();
        }
        else if (!any && _airspace is not null)
        {
            ReleaseAirspace();
        }
    }

    private void ReleaseAirspace()
    {
        _airspace?.Dispose();
        _airspace = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RecomputeAirspace();
        AttachShortcuts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseAirspace();
        DetachShortcuts();
        base.OnDetachedFromVisualTree(e);
    }

    private NcwShortcutRouter? _shortcuts;

    /// <summary>窗口上的快捷键路由(测试用)。</summary>
    public NcwShortcutRouter? Shortcuts => _shortcuts;

    /// <summary>快捷键挂在窗口上(隧道阶段,先于输入框),随外壳挂上 / 卸下;换 ViewModel 时重建。</summary>
    private void AttachShortcuts()
    {
        DetachShortcuts();
        if (_vm is not null && TopLevel.GetTopLevel(this) is { } top)
        {
            _shortcuts = new NcwShortcutRouter(top, _vm);
        }
    }

    private void DetachShortcuts()
    {
        _shortcuts?.Dispose();
        _shortcuts = null;
    }

    /// <summary>外壳请求把焦点放进某个输入框(⌘L)。</summary>
    private void OnViewRequested(ShellViewRequest request)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            switch (request)
            {
                case ShellViewRequest.FocusComposer:
                    this.GetVisualDescendants().OfType<Conversation.ComposerView>().FirstOrDefault(c => c.IsEffectivelyVisible)?.FocusInput();
                    break;
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
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
    public bool IsSidebarVisible => LeftArea.IsVisible && !_leftExiting;

    /// <summary>检查器是否显示(停靠或抽屉)。</summary>
    public bool IsInspectorVisible => InspectorColumn.IsVisible && !_rightExiting;

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

    // 左右栏的滑入滑出(docs/design/03 §9.4):布局(列宽)一次到位,栏自己用 RenderTransform 滑动,所以工作区只重排一次、不会随动画逐帧重排;
    // 滑出时栏临时以「覆盖层」的形态(跨列、固定宽度)留在原处,滑完才真正隐藏。
    // 覆盖层形态的阴影(每次布局重建 Effect 会让它反复重绘,所以各留一份)
    private readonly DropShadowEffect _leftShadow = new() { BlurRadius = 24, OffsetY = 0, Opacity = 0.35 };
    private readonly DropShadowEffect _rightShadow = new() { BlurRadius = 24, OffsetY = 0, Opacity = 0.35 };
    private bool _leftShown;
    private bool _leftExiting;
    private bool _rightShown;
    private bool _rightExiting;
    private bool _layoutKnown;
    private ShellBreakpoint _lastBreakpoint;

    private void AttachViewModel()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.ViewRequested -= OnViewRequested;
        }

        _vm = DataContext as ShellViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.ViewRequested += OnViewRequested;
            _vm.Palette.CommandTitle = ShortcutText.Title;
            _vm.Palette.SectionTitle = s => Localization.KeyText.Get("Settings", s.ToString());
            _vm.Palette.SettingsPrefix = () => Localization.NcwTexts.Get(Localization.NcwTextKind.PaletteSettingsPrefix);
            // 弹窗盖在这个外壳所在的窗口上;Client 层的文字按当前语言提供。
            _vm.Dialogs = new NcwDialogService(this);
            _vm.Launcher = new NcwUrlLauncher(this);
            _vm.WidgetConfirmations = new NcwWidgetConfirmations(this, id => _vm.Stores.Workspace.State.Devices.TryGetValue(id, out var device) ? device.Name : id);
            _vm.Text = Localization.NcwClientText.Instance;
        }

        ApplyLayout();
        if (TopLevel.GetTopLevel(this) is not null)
        {
            AttachShortcuts();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsSidebarOpen) or nameof(ShellViewModel.IsInspectorOpen) or nameof(ShellViewModel.IsDrawerOpen)
            or nameof(ShellViewModel.IsInspectorDrawerOpen))
        {
            ApplyLayout(animate: true);   // 用户开关栏:带滑动
        }
        else if (e.PropertyName is nameof(ShellViewModel.SidebarWidth) or nameof(ShellViewModel.InspectorWidth))
        {
            ApplyLayout();
        }
    }

    /// <summary>窄窗口下左侧抽屉是否打开(测试用)。</summary>
    public bool IsDrawerVisible { get; private set; }

    /// <summary>窄窗口(&lt; 1440)下检查器抽屉是否打开(测试用)。</summary>
    public bool IsInspectorDrawerVisible { get; private set; }

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
    /// 检查器收起时,标签栏右端给右上角常驻按钮让出位置。<paramref name="animate"/>:这次变化来自用户开关栏(不是窗口缩放跨过断点),栏带滑动。
    /// </summary>
    private void ApplyLayout(bool animate = false)
    {
        var sidebarOpen = _vm?.IsSidebarOpen ?? true;
        var inspectorOpen = _vm?.IsInspectorOpen ?? true;
        var compact = Breakpoint < ShellBreakpoint.Medium;
        animate &= _layoutKnown && Breakpoint == _lastBreakpoint;
        _lastBreakpoint = Breakpoint;
        _layoutKnown = true;
        var inspectorNarrow = Breakpoint < ShellBreakpoint.Wide;
        if (_vm is not null)
        {
            _vm.IsCompact = compact;
            _vm.IsInspectorNarrow = inspectorNarrow;
        }

        var drawer = compact && (_vm?.IsDrawerOpen ?? false);
        var inspectorDrawer = inspectorNarrow && (_vm?.IsInspectorDrawerOpen ?? false);
        IsDrawerVisible = drawer;
        IsInspectorDrawerVisible = inspectorDrawer;
        NcwMotion.SetIsPresent(DrawerScrim, drawer || inspectorDrawer);

        var showLeft = drawer || (sidebarOpen && !compact);
        var leftWidth = _vm?.SidebarWidth ?? UiPrefs.SidebarDefaultWidth;
        var leftLeaving = !showLeft && _leftShown && animate;
        if (showLeft)
        {
            var wasExiting = _leftExiting;
            _leftExiting = false;
            LeftArea.IsVisible = true;
            if (animate && (!_leftShown || wasExiting))
            {
                NcwMotion.Arrive(LeftArea, NcwArrival.SlideFromLeft, continueFromCurrent: wasExiting);
            }
            else if (!_leftShown)
            {
                NcwMotion.Rest(LeftArea);
            }
        }
        else if (leftLeaving)
        {
            _leftExiting = true;
            var width = LeftArea.Bounds.Width > 0 ? LeftArea.Bounds.Width : leftWidth;
            leftWidth = width;
            NcwMotion.Leave(LeftArea, NcwArrival.SlideFromLeft, () =>
            {
                _leftExiting = false;
                LeftArea.IsVisible = false;
                ApplyLayout();
            });
        }
        else if (!_leftExiting)
        {
            NcwMotion.Rest(LeftArea);
            LeftArea.IsVisible = false;
        }
        else
        {
            leftWidth = LeftArea.Width is var w and > 0 ? w : leftWidth;
        }

        _leftShown = showLeft;
        var leftOverlay = drawer || _leftExiting;
        Grid.SetColumnSpan(LeftArea, leftOverlay ? 3 : 1);
        LeftArea.ZIndex = leftOverlay ? 10 : 0;
        LeftArea.HorizontalAlignment = leftOverlay ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        LeftArea.Width = leftOverlay ? leftWidth : double.NaN;
        LeftArea.Effect = leftOverlay ? _leftShadow : null;

        var showRight = (inspectorOpen && Breakpoint == ShellBreakpoint.Wide) || inspectorDrawer;
        var rightOverlay = inspectorDrawer;
        var rightLeaving = !showRight && _rightShown && animate;
        var rightWidth = _vm?.InspectorWidth ?? UiPrefs.InspectorDefaultWidth;
        if (showRight)
        {
            var wasExiting = _rightExiting;
            _rightExiting = false;
            InspectorColumn.IsVisible = true;
            if (animate && (!_rightShown || wasExiting))
            {
                NcwMotion.Arrive(InspectorColumn, NcwArrival.SlideFromRight, continueFromCurrent: wasExiting);
            }
            else if (!_rightShown)
            {
                NcwMotion.Rest(InspectorColumn);
            }
        }
        else if (rightLeaving)
        {
            _rightExiting = true;
            rightWidth = InspectorColumn.Bounds.Width > 0 ? InspectorColumn.Bounds.Width : rightWidth;
            NcwMotion.Leave(InspectorColumn, NcwArrival.SlideFromRight, () =>
            {
                _rightExiting = false;
                InspectorColumn.IsVisible = false;
                ApplyLayout();
            });
        }
        else if (!_rightExiting)
        {
            NcwMotion.Rest(InspectorColumn);
            InspectorColumn.IsVisible = false;
        }
        else
        {
            rightWidth = InspectorColumn.Width is var rw and > 0 ? rw : rightWidth;
        }

        _rightShown = showRight;
        InspectorColumn.Classes.Set("overlay", _rightExiting || rightOverlay);
        if (_rightExiting || rightOverlay)
        {
            // 滑出期间检查器跨过(已经收成 0 宽的)右列和工作区,固定宽度、靠右
            Grid.SetColumn(InspectorColumn, 2);
            Grid.SetColumnSpan(InspectorColumn, 3);
            InspectorColumn.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorColumn.Width = rightOverlay ? _vm?.InspectorWidth ?? UiPrefs.InspectorDefaultWidth : rightWidth;
            InspectorColumn.ZIndex = 10;
            InspectorColumn.Effect = _rightShadow;
        }
        else
        {
            Grid.SetColumn(InspectorColumn, 4);
            Grid.SetColumnSpan(InspectorColumn, 1);
            InspectorColumn.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorColumn.Width = double.NaN;
            InspectorColumn.ZIndex = 0;
            InspectorColumn.Effect = null;
        }

        LeftSplitter.IsVisible = showLeft && !drawer;
        var rightDocked = showRight && !rightOverlay;
        RightSplitter.IsVisible = rightDocked;

        // 列宽:收起的列宽度为 0;展开时用用户拖出来的宽度,并限制在范围内。
        var columns = Main.ColumnDefinitions;
        var docked = showLeft && !drawer;
        CollapsedLead.IsVisible = !docked;
        columns[0].MinWidth = docked ? UiPrefs.SidebarMinWidth : 0;
        columns[0].MaxWidth = docked ? UiPrefs.SidebarMaxWidth : double.PositiveInfinity;
        columns[0].Width = new GridLength(docked ? _vm?.SidebarWidth ?? UiPrefs.SidebarDefaultWidth : 0);
        columns[1].Width = new GridLength(docked ? 4 : 0);
        columns[3].Width = new GridLength(rightDocked ? 4 : 0);
        columns[4].MinWidth = rightDocked ? UiPrefs.InspectorMinWidth : 0;
        columns[4].MaxWidth = rightDocked ? UiPrefs.InspectorMaxWidth : double.PositiveInfinity;
        columns[4].Width = new GridLength(rightDocked ? _vm?.InspectorWidth ?? UiPrefs.InspectorDefaultWidth : 0);

        CollapsedLead.Measure(Size.Infinity);
        var lead = CollapsedLead.IsVisible ? CollapsedLead.DesiredSize.Width : 0;
        TopRight.Measure(Size.Infinity);
        var trailing = rightDocked ? 0 : TopRight.DesiredSize.Width + TopRight.Margin.Right;
        Workspace.SetTopRowInsets(lead, trailing);
        Inspector.SetTopRowTrailing(TopRight.DesiredSize.Width + TopRight.Margin.Right, compact: _chrome.ShowsCaptionButtons);
    }
}
