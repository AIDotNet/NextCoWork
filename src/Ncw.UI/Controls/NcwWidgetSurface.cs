using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
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
/// </summary>
public sealed class NcwWidgetSurface : Decorator, IDisposable
{
    private readonly NativeWebView _web = new() { Background = Brushes.Transparent };
    private readonly WidgetNavigationPolicy _policy = new();
    private string? _pendingHtml;
    private DispatcherTimer? _adapterTimer;
    private bool _adapterReady;
    private bool _disposed;

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

    /// <summary>页面发出「就绪」:外壳加载阶段结束,从此 widget 的代码在跑,任何导航都不许。</summary>
    public void MarkReady() => _policy.MarkReady();

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
