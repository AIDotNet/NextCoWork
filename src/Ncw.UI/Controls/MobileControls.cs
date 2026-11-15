using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Ncw.UI.Controls;

// 手机壳用的 TKCode.Arc.Mobile 薄封装(同 ArcWrappers.cs:业务视图只引用这里的类型,Arc 升级的破坏性变更收敛在这一个文件)。
// Arc.Mobile 文档注明「尚未在真机上验证」(docs/design/05 §3 风险):这里的外观和手势在 Headless 里验证了版式与状态,真机手感待 M5 的宿主项目验证。

/// <summary>页面栈:推入的页面从右侧滑入盖住下面一页,左缘右滑或返回按钮弹出。</summary>
public class NcwNavigationStack : TKCode.Arc.Mobile.NavigationStack
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.NavigationStack);
}

/// <summary>一屏:头部(导航栏)、内容、底部(标签栏),自带安全区和软键盘避让。</summary>
public class NcwPage : TKCode.Arc.Mobile.Page
{
    private Avalonia.Controls.Platform.IInsetsManager? _insets;
    private TopLevel? _top;

    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.Page);

    // 安全区(刘海、状态栏、手势条)自己取,不用 Arc 的 UsePlatformSafeArea:Avalonia 的 IInsetsManager.SafeAreaPadding 属性是 dip,
    // 但 SafeAreaChanged 事件参数里的值是物理像素(模拟器实测:缩放 2.625 的手机上属性 = 48.8,事件 = 128),Arc.Mobile 1.1.0 用的是事件参数,
    // 于是页面顶部多出 2.6 倍的空白(见 docs/design/11 §6)。这里始终读属性(dip),事件只当作「变了」的通知。
    // 只有最外层的页面(MobileShell 的框架页)留出安全区;推进导航栈里的页面在它的内容区内,不再重复留。
    public NcwPage() => UsePlatformSafeArea = false;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.FindAncestorOfType<TKCode.Arc.Mobile.Page>() is not null)
        {
            SafeArea = default;
            return;
        }

        _top = TopLevel.GetTopLevel(this);
        _insets = _top?.InsetsManager;
        if (_insets is not null && _top is not null)
        {
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            _top.ScalingChanged += OnScalingChanged;
            ReadSafeArea();
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }

        if (_top is not null)
        {
            _top.ScalingChanged -= OnScalingChanged;
            _top = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // 属性值 = 物理像素 / 当前渲染缩放;启动早期缩放还是 1 时读到的是像素值(128),缩放更新到 2.625 后不再有 SafeAreaChanged,
    // 所以缩放变化时也要重读。
    private void OnSafeAreaChanged(object? sender, Avalonia.Controls.Platform.SafeAreaChangedArgs e) => ReadSafeArea();

    private void OnScalingChanged(object? sender, EventArgs e) => ReadSafeArea();

    private void ReadSafeArea()
    {
        if (_insets is not null)
        {
            SafeArea = _insets.SafeAreaPadding;
        }
    }
}

/// <summary>44px 的顶部导航栏(返回按钮、居中标题、右侧操作)。</summary>
public class NcwNavbar : TKCode.Arc.Mobile.Navbar
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.Navbar);
}

/// <summary>底部标签栏(图标 + 文字,平分宽度)。</summary>
public class NcwTabbar : TKCode.Arc.Mobile.Tabbar
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.Tabbar);
}

public class NcwTabbarItem : TKCode.Arc.Mobile.TabbarItem
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.TabbarItem);
}

/// <summary>底部弹层(可停在几个高度,点遮罩 / 下拉关闭)。</summary>
public class NcwSheet : TKCode.Arc.Mobile.Sheet
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.Sheet);
}

/// <summary>下拉刷新。</summary>
public class NcwPullToRefresh : TKCode.Arc.Mobile.PullToRefresh
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.PullToRefresh);
}

/// <summary>分组列表(圆角卡片 + 头 / 尾说明),手机设置入口和设备页用。</summary>
public class NcwListGroup : TKCode.Arc.Mobile.ListGroup
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.ListGroup);
}

/// <summary>列表行(图标、标题、副标题、右侧内容、箭头;最小 44px 触控高度)。</summary>
public class NcwListItem : TKCode.Arc.Mobile.ListItem
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Mobile.ListItem);
}

/// <summary>
/// 界面密度:手机壳把它设为 true(继承属性),对话区里的控件据此切换成触控版式
/// (审批卡片的「长按允许」、输入框的紧凑工具行)。桌面窗口缺省为 false,即使窗口很窄——宽度判断和触控判断是两回事。
/// </summary>
public static class NcwDensity
{
    public static readonly Avalonia.AttachedProperty<bool> IsTouchProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<Avalonia.Controls.Control, bool>("IsTouch", typeof(NcwDensity), defaultValue: false, inherits: true);

    public static bool GetIsTouch(Avalonia.Controls.Control control) => control.GetValue(IsTouchProperty);

    public static void SetIsTouch(Avalonia.Controls.Control control, bool value) => control.SetValue(IsTouchProperty, value);
}

/// <summary>
/// 左滑 / 右滑露出行操作的列表(会话列表左滑:停止 / 归档,docs/design/03 §3)。动作用 <see cref="AddTrailing"/> 在 code-behind 里挂(Arc 的 SwipeAction 不在视觉树里,没法写在 XAML 里绑命令),
/// Arc 的类型不外露到视图。命令的参数是行的数据项。
/// </summary>
public class NcwSwipeActions : TKCode.Arc.SwipeActions
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.SwipeActions);

    /// <summary>加一个左滑露出的动作(越靠后越靠外,整行滑到底触发最外面那个;这里关掉整行滑动,避免误触)。</summary>
    public void AddTrailing(string label, Avalonia.Media.Geometry? icon, System.Windows.Input.ICommand command, bool destructive = false)
    {
        IsFullSwipeEnabled = false;
        TrailingActions.Add(new TKCode.Arc.SwipeAction
        {
            Label = label,
            Icon = icon,
            Tone = destructive ? TKCode.Arc.SwipeActionTone.Danger : TKCode.Arc.SwipeActionTone.Neutral,
            KeepsRow = true, // 动作的结果(归档 / 停止)由设备推回来,列表自己更新;行先弹回原位
            Command = command,
        });
    }
}
