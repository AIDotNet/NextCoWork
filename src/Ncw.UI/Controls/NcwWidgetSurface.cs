using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.Widgets;

namespace Ncw.UI.Controls;

public enum WidgetSurfaceFailure
{
    /// <summary>这个平台 / 这台机器上没有可用的 WebView(没装运行时、没有原生句柄、Headless)。</summary>
    Unavailable,

    /// <summary>页面加载失败。</summary>
    LoadFailed,

    /// <summary>WebView 进程崩了或卡死被系统终止。</summary>
    Crashed,
}

/// <summary>
/// 一个 widget 的 WebView 外壳——整个仓库里<b>唯一</b>引用 Avalonia 官方 <see cref="NativeWebView"/> 的地方(docs/design/03 §8 的评估:
/// macOS WKWebView / Windows WebView2 / Linux WebKitGTK 与 WPE;MIT 包 <c>Avalonia.Controls.WebView</c>)。
/// <para>它只做「加载一页 HTML、把页面发来的字符串消息交出去、执行宿主要推进去的脚本语句、按导航策略拦截一切导航与弹窗」,
/// <b>不含任何业务判断</b>:消息校验和限速在 Client 层的 <see cref="WidgetBridge"/>,导航决策在 <see cref="WidgetNavigationPolicy"/>——这样业务逻辑不依赖 WebView,可以在没有 WebView 的环境里单测。</para>
/// <para>安全默认值:每个 WebView 一个<b>非持久</b>数据存储(WKWebView 的 <c>NonPersistentDataStore</c>、WebView2 的 InPrivate、WebKitGTK 的 ephemeral),没有 cookie / 缓存 / localStorage 落盘;
/// 关掉开发者工具;背景透明(widget 长在对话里)。</para>
/// <para><b>裁剪</b>(docs/design/03 §8.5):<see cref="NativeWebView"/> 在 macOS / Linux 上是 <c>NativeControlHost</c>,原生视图按控件的<b>完整</b>绝对边界摆放,
/// 不认任何祖先的裁剪——放在转录的 <c>ScrollViewer</c> 里时,滚出视口的部分会盖在输入框、标签栏、「回到最新」上。所以本控件在布局里占完整高度(列表的高度 / 滚动都按它算),
/// 但只把 WebView 摆在<b>可见的那一段</b>(和所有裁剪祖先的交集,减去祖先声明的 <see cref="NcwWidgetClip.BottomInsetProperty"/>),再让页面滚到这一段的起点
/// (<see cref="WidgetPush.Viewport"/>),内容看起来钉在卡片里。完全看不见、或横向被裁(侧栏滑动时;横向裁会让页面按更窄的宽度重排)就整个隐藏(页面不销毁)。</para>
/// </summary>
public sealed class NcwWidgetSurface : Decorator, IDisposable
{
    private readonly NativeWebView _web = new() { Background = Brushes.Transparent };
    private readonly WidgetNavigationPolicy _policy = new();
    private string? _pendingHtml;
    private DispatcherTimer? _adapterTimer;
    private bool _adapterReady;
    private bool _disposed;
    private bool _pageReady;

    /// <summary>WebView 当前摆放的区域(本控件坐标);空 = 隐藏。</summary>
    private Rect _visible;

    /// <summary>上一次推给页面的可见段起点;-1 = 还没推过(页面就绪后必推一次)。</summary>
    private double _pushedTop = -1;

    /// <summary>WebView 适配器多久建不起来就当「没有可用的 WebView」(没有原生窗口句柄的环境——Headless、远程桌面的某些会话——适配器永远不会建好)。</summary>
    public static TimeSpan AdapterTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 测试 / 排障开关:当作这个平台没有 WebView(走静态快照降级)。Headless 截图测试用它得到确定的外壳状态——真实渲染见 <c>NcwWidgetSmokeTests</c>。
    /// </summary>
    public static bool DisableForTesting { get; set; }

    public NcwWidgetSurface()
    {
        Child = _web;
        _web.EnvironmentRequested += OnEnvironment;
        _web.AdapterCreated += OnAdapterCreated;
        _web.NavigationStarted += OnNavigationStarted;
        _web.NavigationCompleted += OnNavigationCompleted;
        _web.NewWindowRequested += OnNewWindow;
        _web.WebMessageReceived += OnMessage;
        _web.ProcessTerminated += OnTerminated;
        // 滚动只改祖先的排布,不会重新排布本控件:每一轮布局之后重算可见段(滚动、窗口缩放、上方的行变高都会触发布局)。
        // 在布局结束、渲染之前把 WebView 挪到新区域,NativeControlHost 在 AfterRender 读到的就是新位置,和这一帧的内容一致
        LayoutUpdated += (_, _) => UpdateVisible(Bounds.Size);
    }

    /// <summary>WebView 当前可见的那一段(本控件坐标,测试 / 诊断用);空 = 整个隐藏。</summary>
    public Rect VisibleRect => _visible;

    protected override Size ArrangeOverride(Size finalSize)
    {
        _visible = Visible(finalSize);
        PlaceWeb();
        return finalSize;
    }

    private void UpdateVisible(Size size)
    {
        if (_disposed || VisualRoot is null)
        {
            return;
        }

        var visible = Visible(size);
        if (visible != _visible)
        {
            _visible = visible;
            PlaceWeb();
        }
    }

    /// <summary>
    /// 本控件和所有裁剪祖先(<c>ClipToBounds</c>,如 <c>ScrollContentPresenter</c>;以及窗口本身)的交集。祖先上的 <see cref="NcwWidgetClip.BottomInsetProperty"/>
    /// 把它的底边再往上收(盖在内容上的浮层,如「回到最新」)。横向被裁一点就当看不见:页面宽度跟着 WebView 走,横向裁剪会让内容重排。
    /// </summary>
    private Rect Visible(Size size)
    {
        var rect = new Rect(size);
        if (size.Width < 1 || size.Height < 1 || VisualRoot is null)
        {
            return default;
        }

        foreach (var ancestor in this.GetVisualAncestors())
        {
            var inset = NcwWidgetClip.GetBottomInset(ancestor);
            if (!ancestor.ClipToBounds && ancestor is not TopLevel && inset <= 0)
            {
                continue;
            }

            if (ancestor.TransformToVisual(this) is not { } toLocal)
            {
                return default;
            }

            var clip = new Rect(ancestor.Bounds.Size).TransformToAABB(toLocal);
            if (inset > 0)
            {
                clip = new Rect(clip.X, clip.Y, clip.Width, Math.Max(0, clip.Height - inset));
            }

            rect = rect.Intersect(clip);
            if (rect.Width < 1 || rect.Height < 1)
            {
                return default;
            }
        }

        if (rect.Width < size.Width - 0.5)
        {
            return default;
        }

        // 摆到整像素上(NativeControlHost 也会取整),避免亚像素的反复挪动
        var top = Math.Floor(rect.Y);
        var bottom = Math.Ceiling(rect.Bottom);
        return new Rect(0, top, size.Width, Math.Max(0, bottom - top));
    }

    private void PlaceWeb()
    {
        var show = _visible.Height >= 1;
        _web.IsVisible = show;
        if (!show)
        {
            return;
        }

        // 直接排布子控件(可能在布局轮之外,从 LayoutUpdated 里调):边界变了,NativeControlHost 随之挪动原生视图
        _web.Arrange(_visible);
        PushViewport();
    }

    private void PushViewport()
    {
        if (_pageReady && Math.Abs(_visible.Y - _pushedTop) >= 0.5)
        {
            _pushedTop = _visible.Y;
            Post(WidgetPush.Viewport(_visible.Y));
        }
    }

    /// <summary>页面经 <c>invokeCSharpAction</c> 发来的原始字符串(不可信,交给 <see cref="WidgetBridge.HandleAsync"/>)。</summary>
    public event Action<string>? MessageReceived;

    /// <summary>页面想去的外链(导航或新窗口被拦下之后,只有 http / https 的目标会走到这里)。</summary>
    public event Action<Uri>? ExternalRequested;

    public event Action<WidgetSurfaceFailure, string?>? Failed;

    /// <summary>引擎名与版本(诊断用),没有时为 null。</summary>
    public string? EngineInfo { get; private set; }

    /// <summary>这个平台原则上有 WebView 可用(桌面三大平台)。手机端 / 浏览器不走这个控件(降级为静态快照)。</summary>
    public static bool IsSupportedPlatform => !DisableForTesting && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    /// <summary>加载外壳页面。适配器还没建好就等它建好再加载。</summary>
    public void LoadShell(string html)
    {
        if (_disposed)
        {
            return;
        }

        if (!_adapterReady)
        {
            _pendingHtml = html;
            _adapterTimer ??= StartAdapterTimer();
            return;
        }

        Navigate(html);
    }

    private DispatcherTimer StartAdapterTimer()
    {
        var timer = new DispatcherTimer { Interval = AdapterTimeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_adapterReady && !_disposed)
            {
                Failed?.Invoke(WidgetSurfaceFailure.Unavailable, "adapter-timeout");
            }
        };
        timer.Start();
        return timer;
    }

    /// <summary>页面发出「就绪」:外壳加载阶段结束,从此 widget 的代码在跑,任何导航都不许。页面(含重载后)从顶上开始,把当前的可见段起点推一次。</summary>
    public void MarkReady()
    {
        _policy.MarkReady();
        _pageReady = true;
        _pushedTop = -1;
        if (_visible.Height >= 1)
        {
            PushViewport();
        }
    }

    /// <summary>把一条宿主消息(JSON)推进页面。失败只记诊断,不抛(页面可能正在重载)。</summary>
    public void Post(string messageJson)
    {
        if (_disposed || !_adapterReady)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Post(messageJson));
            return;
        }

        _ = RunScriptAsync(WidgetPush.Script(messageJson));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adapterTimer?.Stop();
        _web.EnvironmentRequested -= OnEnvironment;
        _web.AdapterCreated -= OnAdapterCreated;
        _web.NavigationStarted -= OnNavigationStarted;
        _web.NavigationCompleted -= OnNavigationCompleted;
        _web.NewWindowRequested -= OnNewWindow;
        _web.WebMessageReceived -= OnMessage;
        _web.ProcessTerminated -= OnTerminated;
        Child = null; // 离开视觉树:原生视图随之销毁
    }

    private void Navigate(string html)
    {
        _pageReady = false;
        _policy.ExpectShellLoad();
        try
        {
            _web.NavigateToString(html, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed?.Invoke(WidgetSurfaceFailure.Unavailable, e.GetType().Name);
        }
    }

    private async Task RunScriptAsync(string script)
    {
        try
        {
            await _web.InvokeScript(script).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 页面正在重载 / 已销毁:这条消息作废。页面就绪后宿主会重推全部状态
            System.Diagnostics.Debug.WriteLine($"widget script failed: {e.Message}");
        }
    }

    private static void OnEnvironment(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        switch (e)
        {
            case Avalonia.Platform.AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
            case Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.IsInPrivateModeEnabled = true;
                break;
            case Avalonia.Platform.GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                break;
        }
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        var info = _web.AdapterInfo;
        if (info is null)
        {
            Failed?.Invoke(WidgetSurfaceFailure.Unavailable, "no-adapter");
            return;
        }

        EngineInfo = $"{info.Type} {info.Engine} {info.Version}".Trim();
        var detail = WebViewAdapterInfo.GetAdapterInfo(info.Type);
        if (info.Type is WebViewAdapterType.Unknown or WebViewAdapterType.Headless || !detail.IsSupported || !detail.IsInstalled)
        {
            Failed?.Invoke(WidgetSurfaceFailure.Unavailable, string.IsNullOrEmpty(detail.UnavailableReason) ? info.Type.ToString() : detail.UnavailableReason);
            return;
        }

        _adapterReady = true;
        _adapterTimer?.Stop();
        if (_pendingHtml is { } html)
        {
            _pendingHtml = null;
            Navigate(html);
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        switch (_policy.Decide(e.Request))
        {
            case WidgetNavigation.Allow:
                break;
            case WidgetNavigation.OpenExternal:
                e.Cancel = true;
                RaiseExternal(e.Request);
                break;
            default:
                e.Cancel = true;
                break;
        }
    }

    private void OnNewWindow(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true; // 永远不开新窗口
        if (_policy.DecideNewWindow(e.Request) == WidgetNavigation.OpenExternal)
        {
            RaiseExternal(e.Request);
        }
    }

    private void RaiseExternal(Uri? uri)
    {
        if (uri is not null && WidgetInboundParser.TryExternalUrl(uri.OriginalString, out var safe))
        {
            ExternalRequested?.Invoke(safe);
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Failed?.Invoke(WidgetSurfaceFailure.LoadFailed, null);
        }
    }

    private void OnMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (e.Body is { } body)
        {
            MessageReceived?.Invoke(body);
        }
    }

    private void OnTerminated(object? sender, WebViewProcessTerminatedEventArgs e) =>
        Failed?.Invoke(WidgetSurfaceFailure.Crashed, e.Reason);
}
