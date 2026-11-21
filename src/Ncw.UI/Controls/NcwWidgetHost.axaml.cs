using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ncw.Client.ViewModels;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;

namespace Ncw.UI.Controls;

/// <summary>
/// 内联可视化卡片的内容区。DataContext 是 <see cref="WidgetRowViewModel"/>(阶段、卡片、状态都从它读,它们都来自 Store 的派生,本控件只持有纯 UI 状态:WebView 实例、
/// 页面就绪与否、这一页的失败)。
/// <list type="bullet">
/// <item>桌面且有 WebView:放一个 <see cref="NcwWidgetSurface"/>,页面「就绪」后推主题 / 环境 / 内容;页面经桥(<see cref="WidgetBridge"/>)调设备,设备返回之后镜像变了(<c>EntryChanged</c>)、转录里的卡片变了,就把环境重推给页面。</item>
/// <item>手机壳(触控密度)、WebView 不可用、或 WebView 出过「不可用」:降级为静态快照的文字(只读)。</item>
/// <item>WebView 崩溃 / 加载失败 / 桥被锁死:显示重新加载;12 秒没有就绪同样当加载失败。</item>
/// </list>
/// 同一时刻最多 <see cref="MaxLiveSurfaces"/> 个 WebView(虚拟化列表只会实例化屏幕附近的行,这个上限是兜底),超出的先画快照。
/// </summary>
public partial class NcwWidgetHost : UserControl
{
    public const int MaxLiveSurfaces = 8;
    private const int ReadyTimeoutSeconds = 12;
    private const double DefaultHeight = 120;
    private const int MaxRememberedHeights = 256;

    private static int s_live;

    /// <summary>
    /// 每张卡片(设备 + 会话 + 调用)上次报上来的内容高度。纯 UI 缓存:虚拟化列表把卡片滚出去会销毁 WebView,滚回来重建时直接用上次的高度,
    /// 而不是先占 120 再跳到真实高度——那一跳会让列表的总高度和滚动位置一起跳(跟随最新时还会把视图拽来拽去)。
    /// </summary>
    private static readonly Dictionary<WidgetRef, double> s_heights = [];

    private WidgetRowViewModel? _vm;
    private NcwWidgetSurface? _surface;
    private WidgetBridge? _bridge;
    private WidgetCard? _shownCard;
    private WidgetSurfaceFailure? _failure;
    private bool _pageReady;
    private bool _counted;
    private DispatcherTimer? _readyTimer;
    private DispatcherTimer? _rotateTimer;
    private WidgetCard? _failedCard;
    private int _loadingIndex;

    public NcwWidgetHost()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        ActualThemeVariantChanged += (_, _) => PushTheme();
    }

    /// <summary>当前有没有 WebView 在显示(测试 / 诊断用)。</summary>
    public bool HasSurface => _surface is not null;

    /// <summary>页面发出过「就绪」(测试 / 诊断用)。</summary>
    public bool IsPageReady => _pageReady;

    /// <summary>最近一次的失败(没有为 null)。</summary>
    public WidgetSurfaceFailure? Failure => _failure;

    /// <summary>是否显示静态快照(测试用)。</summary>
    public bool IsShowingSnapshot => SnapshotPanel.IsVisible;

    /// <summary>页面内容的整体高度(WebView 报上来的,已钳制);没有 WebView 为 0。</summary>
    public double SurfaceHeight => _surface?.Height ?? 0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        NcwWidgetAirspace.Changed += OnAirspace;
        _vm?.Attach();
        Evaluate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        NcwWidgetAirspace.Changed -= OnAirspace;
        _vm?.Detach();
        TearDownSurface();
        _rotateTimer?.Stop();
        _rotateTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.EntryChanged -= OnEntryChanged;
            if (VisualRoot is not null)
            {
                _vm.Detach();
            }
        }

        TearDownSurface();
        _failure = null;
        _vm = DataContext as WidgetRowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.EntryChanged += OnEntryChanged;
            if (VisualRoot is not null)
            {
                _vm.Attach();
            }
        }

        Evaluate();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 数据更新(转录里的行变了):卡片没换就只重推环境(状态 / 数据 / 提交状态变了),换了(edit 之后 / 终态的卡片替换了实时卡)就重新加载
        if (_vm?.Card is { } card && _surface is not null && ReferenceEquals(card, _shownCard))
        {
            PushEnv();
            return;
        }

        Evaluate();
    }

    private void OnEntryChanged() => PushEnv();

    private void OnAirspace()
    {
        if (_surface is not null)
        {
            _surface.IsVisible = !NcwWidgetAirspace.IsSuspended;
        }
    }

    // ───────────────────────── 模式选择 ─────────────────────────

    private bool WantsSurface =>
        _vm is { Card: not null, HasHost: true }
        && !NcwDensity.GetIsTouch(this)
        && NcwWidgetSurface.IsSupportedPlatform
        && _failure != WidgetSurfaceFailure.Unavailable;

    private void Evaluate()
    {
        ApplyLoadingText();
        if (_vm?.Card is not { } card || VisualRoot is null)
        {
            if (_vm?.Card is null)
            {
                TearDownSurface();
            }

            UpdateVisuals();
            return;
        }

        var want = WantsSurface && (_surface is not null || s_live < MaxLiveSurfaces);
        if (!want)
        {
            TearDownSurface();
        }
        else if (_surface is null && _failure is WidgetSurfaceFailure.Crashed or WidgetSurfaceFailure.LoadFailed && ReferenceEquals(card, _failedCard))
        {
            // 这一份卡片的页面坏过:等用户点「重新加载」,不在每次数据更新时自动重试(会反复崩)
        }
        else if (_surface is null || !ReferenceEquals(card, _shownCard))
        {
            StartSurface(card);
        }

        UpdateVisuals();
    }

    private void StartSurface(WidgetCard card)
    {
        TearDownSurface();
        if (_vm is null || s_live >= MaxLiveSurfaces)
        {
            return;
        }

        NcwWidgetSurface surface;
        try
        {
            surface = new NcwWidgetSurface
            {
                Height = InitialHeight(card),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                IsVisible = !NcwWidgetAirspace.IsSuspended,
            };
        }
        catch (Exception e) when (NcwWidgetSurface.IsPlatformInitializationFailure(e))
        {
            OnSurfaceFailed(null, WidgetSurfaceFailure.Unavailable, e.Message);
            return;
        }

        var bridge = _vm.CreateBridge(json => surface.Post(json));
        if (bridge is null)
        {
            surface.Dispose();
            return;
        }

        _surface = surface;
        _bridge = bridge;
        _shownCard = card;
        _pageReady = false;
        _failure = null;
        s_live++;
        _counted = true;

        surface.MessageReceived += body => _ = HandleMessageAsync(bridge, body);
        surface.ExternalRequested += uri => _ = bridge.OpenExternalAsync(uri);
        surface.Failed += (failure, detail) => OnSurfaceFailed(surface, failure, detail);
        bridge.Ready += () => Dispatcher.UIThread.Post(() => OnPageReady(surface));
        var key = _vm.Ref;
        bridge.HeightChanged += height => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_surface, surface))
            {
                surface.Height = height;
                RememberHeight(key, height);
            }
        });
        // 页面没消费的滚轮:交给 WebView 外的滚动容器(原生视图把滚轮整个吃掉了,不转交的话在小组件上滚不动转录)
        bridge.Wheel += dy =>
        {
            // 页面消息本来就在 UI 线程上到达:直接滚,少一次排队(滚动跟手)
            if (Dispatcher.UIThread.CheckAccess())
            {
                ForwardWheel(surface, dy);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ForwardWheel(surface, dy));
            }
        };
        bridge.Locked += () => Dispatcher.UIThread.Post(() => OnSurfaceFailed(surface, WidgetSurfaceFailure.Crashed, "locked"));

        try
        {
            SurfaceSlot.Child = surface;
        }
        catch (Exception e) when (NcwWidgetSurface.IsPlatformInitializationFailure(e))
        {
            OnSurfaceFailed(surface, WidgetSurfaceFailure.Unavailable, e.Message);
            return;
        }

        surface.LoadShell(WidgetShell.Build(WidgetShell.NewNonce()));
        if (!ReferenceEquals(_surface, surface))
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ReadyTimeoutSeconds) };
        _readyTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_pageReady)
            {
                OnSurfaceFailed(surface, WidgetSurfaceFailure.LoadFailed, "timeout");
            }
        };
        timer.Start();
    }

    private void ForwardWheel(NcwWidgetSurface surface, double dy)
    {
        if (ReferenceEquals(_surface, surface))
        {
            surface.ForwardWheel(dy);
        }
    }

    private static async Task HandleMessageAsync(WidgetBridge bridge, string body)
    {
        try
        {
            await bridge.HandleAsync(body).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"widget bridge failed: {e.Message}");
        }
    }

    private double InitialHeight(WidgetCard card) =>
        _vm is not null && s_heights.TryGetValue(_vm.Ref, out var remembered) ? remembered
        : card.Size.Aspect is { } aspect && Bounds.Width > 0 ? WidgetLimits.ClampHeight(Bounds.Width * aspect)
        : DefaultHeight;

    private static void RememberHeight(WidgetRef key, double height)
    {
        if (s_heights.Count >= MaxRememberedHeights && !s_heights.ContainsKey(key))
        {
            s_heights.Clear(); // 简单的上限:满了就清掉重来,只影响首帧高度
        }

        s_heights[key] = height;
    }

    private void TearDownSurface()
    {
        _readyTimer?.Stop();
        _readyTimer = null;
        if (_surface is { } surface)
        {
            surface.Dispose();
            SurfaceSlot.Child = null;
        }

        if (_counted)
        {
            s_live--;
            _counted = false;
        }

        _surface = null;
        _bridge = null;
        _shownCard = null;
        _pageReady = false;
    }

    // ───────────────────────── 页面生命周期 ─────────────────────────

    private void OnPageReady(NcwWidgetSurface surface)
    {
        if (!ReferenceEquals(_surface, surface) || _vm?.Card is not { } card)
        {
            return;
        }

        _readyTimer?.Stop();
        surface.MarkReady();
        _pageReady = true;
        // 页面就绪(含重载):整份重推,两边的启动顺序不用靠猜
        PushTheme();
        PushEnv();
        surface.Post(WidgetPush.Content(card.Code, card.Format));
        UpdateVisuals();
    }

    private void PushTheme()
    {
        if (_surface is { } surface && _pageReady)
        {
            surface.Post(WidgetPush.Theme(NcwWidgetTheme.Current(this)));
        }
    }

    private void PushEnv()
    {
        if (_surface is { } surface && _pageReady && _vm is not null)
        {
            surface.Post(WidgetPush.Env(_vm.Environment()));
        }
    }

    /// <summary>模型给的加载提示(1–4 条)轮换显示;没有就用默认文案。只在「加载中」且附着在视觉树上时才有定时器。</summary>
    private void ApplyLoadingText()
    {
        var messages = _vm is { IsLoading: true } vm ? vm.LoadingText.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
        LoadingText.Text = messages.Length == 0
            ? Localization.NcwTexts.Get(Localization.NcwTextKind.WidgetLoading)
            : messages[_loadingIndex % messages.Length];

        var rotate = messages.Length > 1 && VisualRoot is not null;
        if (rotate && _rotateTimer is null)
        {
            _rotateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            _rotateTimer.Tick += (_, _) =>
            {
                _loadingIndex++;
                ApplyLoadingText();
            };
            _rotateTimer.Start();
        }
        else if (!rotate && _rotateTimer is not null)
        {
            _rotateTimer.Stop();
            _rotateTimer = null;
        }
    }

    private void OnSurfaceFailed(NcwWidgetSurface? surface, WidgetSurfaceFailure failure, string? detail)
    {
        if (Dispatcher.UIThread.CheckAccess() is false)
        {
            Dispatcher.UIThread.Post(() => OnSurfaceFailed(surface, failure, detail));
            return;
        }

        if (!ReferenceEquals(_surface, surface))
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine($"widget surface failed: {failure} {detail}");
        _failure = failure;
        _failedCard = _shownCard;
        TearDownSurface();
        UpdateVisuals();
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e) => Reload();

    /// <summary>重新加载这一页(清掉失败、重建 WebView)。</summary>
    public void Reload()
    {
        TearDownSurface();
        _failure = null;
        _failedCard = null;
        Evaluate();
    }

    // ───────────────────────── 视觉状态 ─────────────────────────

    private void UpdateVisuals()
    {
        var hasCard = _vm?.Card is not null;
        var showSurface = _surface is not null;
        var crashed = _failure is WidgetSurfaceFailure.Crashed or WidgetSurfaceFailure.LoadFailed;
        SurfaceSlot.IsVisible = showSurface;
        CrashPanel.IsVisible = hasCard && crashed && !showSurface;
        SnapshotPanel.IsVisible = hasCard && !showSurface && !crashed;
        SnapshotNote.Text = _failure == WidgetSurfaceFailure.Unavailable
            ? Localization.NcwTexts.Get(Localization.NcwTextKind.WidgetUnavailable)
            : Localization.NcwTexts.Get(Localization.NcwTextKind.WidgetStaticPreview);
    }
}
