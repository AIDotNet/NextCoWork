using Avalonia;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Mobile;

namespace Ncw.UI.Views;

/// <summary>当前显示的外壳。</summary>
public enum ShellKind
{
    /// <summary>栏式布局(项目栏 + 项目面板 + 主工作区 + 检查器)。</summary>
    Desktop,

    /// <summary>导航栈(底部标签栏 + 页面栈)。</summary>
    Mobile,
}

/// <summary>
/// 桌面外壳和手机外壳的切换容器(docs/design/03 §2):<b>按宽度切换,不按平台切换</b>——宽度 &lt; 720 用 <see cref="MobileShell"/>,否则用 <see cref="DesktopShell"/>,
/// 所以 iPad 横屏和桌面上拖窄的窗口走的是同一条规则。两个外壳共用同一个 <see cref="ShellViewModel"/>(一套 ViewModel),切换时状态一点不丢。
/// 外壳按需创建,创建后留着(切回来保住滚动位置等纯 UI 状态);暂时不在显示的外壳不挂在视觉树上。
/// 桌面主窗口的最小宽度是 760,不会自己落进手机宽度;手机宿主(iOS / Android)的单视图直接用这个容器当根视图。
/// </summary>
public sealed class AdaptiveShell : ContentControl
{
    /// <summary>窄于它就是手机外壳(和 <see cref="ShellBreakpoint.Compact"/> 同一个界线)。</summary>
    public const double MobileBelow = 720;

    private DesktopShell? _desktop;
    private MobileShell? _mobile;

    public AdaptiveShell()
    {
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
    }

    /// <summary>当前外壳。</summary>
    public ShellKind Kind { get; private set; } = ShellKind.Desktop;

    /// <summary>桌面外壳的平台留白(红绿灯等)。默认按当前操作系统;只对桌面外壳有意义。</summary>
    public WindowChrome? Chrome { get; set; }

    /// <summary>宽度 → 外壳。纯函数,测试直接用。</summary>
    public static ShellKind KindFor(double width) => width < MobileBelow ? ShellKind.Mobile : ShellKind.Desktop;

    /// <summary>当前显示的桌面外壳(没创建过为 null;测试用)。</summary>
    public DesktopShell? Desktop => _desktop;

    /// <summary>当前显示的手机外壳(没创建过为 null;测试用)。</summary>
    public MobileShell? Mobile => _mobile;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 手机宿主的单视图:内容画到系统栏后面(状态栏、手势条),页面自己按安全区留白(NcwPage)。桌面窗口没有 InsetsManager,这里什么也不做。
        if (TopLevel.GetTopLevel(this) is { InsetsManager: { } insets } top)
        {
            insets.DisplayEdgeToEdgePreference = true;
            TopLevel.SetAutoSafeAreaPadding(this, false); // Avalonia 默认还会自己给根视图加安全区 padding;关掉,避免和 NcwPage 的留白叠加
            _insets = insets;
            _top = top;
            insets.SafeAreaChanged += OnSafeAreaChanged;
            top.ScalingChanged += OnScalingChanged;
            ApplySafeArea();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null && _top is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _top.ScalingChanged -= OnScalingChanged;
        }

        _insets = null;
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private Avalonia.Controls.Platform.IInsetsManager? _insets;
    private TopLevel? _top;

    private void OnSafeAreaChanged(object? sender, Avalonia.Controls.Platform.SafeAreaChangedArgs e) => ApplySafeArea();

    private void OnScalingChanged(object? sender, EventArgs e) => ApplySafeArea();

    /// <summary>桌面外壳没有自己的安全区处理(手机外壳的页面 <c>NcwPage</c> 自己留):手机 / 平板横屏走桌面外壳时,在这里给它留出状态栏 / 手势条 / 刘海。</summary>
    private void ApplySafeArea() =>
        Padding = Kind == ShellKind.Desktop && _insets is not null ? _insets.SafeAreaPadding : default;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Apply(KindFor(e.NewSize.Width));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // 宽度还不知道(第一次测量之前)就等 OnSizeChanged:手机上不要先建一个桌面外壳再丢掉
        if (Content is null && Bounds.Width > 0)
        {
            Apply(KindFor(Bounds.Width));
        }
    }

    private void Apply(ShellKind kind)
    {
        if (Content is not null && kind == Kind)
        {
            return;
        }

        Kind = kind;
        ApplySafeArea();
        Content = kind == ShellKind.Mobile
            ? _mobile ??= new MobileShell()
            : _desktop ??= CreateDesktop();
    }

    private DesktopShell CreateDesktop()
    {
        var shell = new DesktopShell();
        if (Chrome is { } chrome)
        {
            shell.Chrome = chrome;
        }

        return shell;
    }
}
