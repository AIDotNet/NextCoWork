using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 会话内容。这里只有纯 UI 状态:转录的滚动位置、是否「跟随最新」、展开 / 折叠;所有事实都在 <see cref="ConversationViewModel"/> 里(来自 Store)。
/// <para>跟随最新的规则在 <see cref="TranscriptFollow"/>(只认用户意图);这里只接线:滚动容器上的输入(滚轮 / 键盘 / 拖滚动条 / 手势)喂给它,
/// 每一轮布局之后按它的判断贴底(<see cref="OnScrollerLayoutUpdated"/>,同一帧完成),「回到最新」按钮跟着它显示。</para>
/// </summary>
public partial class ConversationView : UserControl
{
    /// <summary>一轮里最多有几行算「刚到的」。一次性插进来更多(打开会话、快照到了、重连补齐)是加载历史,不是新消息,不播放进入动画。</summary>
    private const int MaxArrivalsPerTurn = 4;

    /// <summary>贴底在一次连续的布局里最多追几轮(虚拟化列表每贴一次底都可能实例化新行、重估高度);到了上限就留到下一帧再追,不会卡死在一帧里。</summary>
    private const int MaxPinsPerBurst = 12;

    private readonly TranscriptFollow _follow = new();
    private ScrollViewer? _scroller;
    private int _pinBurst;
    private bool _pinBurstResetQueued;
    private DispatcherTimer? _ticker;
    private ConversationViewModel? _vm;
    private IDisposable? _lightboxAirspace;

    /// <summary>
    /// 空会话时输入框下方那一行(第 3 行)分走的剩余高度,相对上方第 1 行的 1*。略大于 1:输入框落在视觉中心偏上一点(标题在它上面,建议在它下面)。
    /// </summary>
    private const double EmptyBelowWeight = 1.2;

    // 空会话布局(纯 UI 状态):当前按哪种布局排的,以及是在哪个会话里变成空的(只有同一个会话里「空 → 有内容」= 发出了第一条消息,才让输入框滑回底部)
    private bool _layoutEmpty;
    private SessionRef? _emptySession;

    // 新行的进入动画(纯 UI 状态,docs/design/03 §9.2):虚拟化列表里只有「真正新追加」的行才动,滚动时被实例化的旧行不动,所以不影响滚动和跟随最新。
    private readonly HashSet<RowViewModel> _arriving = [];
    private SessionRef? _arrivalSession;
    private bool _arrivalsArmed;
    private int _turnAdds;
    private bool _turnScheduled;

    public ConversationView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => ReleaseLightboxAirspace();
        Transcript.ContainerPrepared += OnContainerPrepared;
        Transcript.ContainerClearing += (_, e) => NcwMotion.Rest(e.Container);
        Transcript.TemplateApplied += (_, e) => AttachScroller(e.NameScope.Find<ScrollViewer>("PART_Scroller"));
        JumpButton.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == BoundsProperty)
            {
                UpdateWidgetInset();
            }
        };
    }

    /// <summary>当前是否跟随最新(测试用)。</summary>
    public bool IsFollowing => _follow.IsFollowing;

    /// <summary>转录的滚动容器(测试用)。</summary>
    public ScrollViewer? Scroller => _scroller;

    // ───────────────────────── 滚动容器接线 ─────────────────────────

    private void AttachScroller(ScrollViewer? scroller)
    {
        if (_scroller is { } old)
        {
            old.ScrollChanged -= OnScrollChanged;
            old.LayoutUpdated -= OnScrollerLayoutUpdated;
            old.RemoveHandler(PointerWheelChangedEvent, OnScrollerWheel);
            old.RemoveHandler(NcwWidgetSurface.WheelForwardedEvent, OnWidgetWheel);
            old.RemoveHandler(KeyDownEvent, OnScrollerKeyDown);
            old.RemoveHandler(PointerPressedEvent, OnScrollerPointerPressed);
            old.RemoveHandler(PointerReleasedEvent, OnScrollerPointerReleased);
            old.RemoveHandler(PointerCaptureLostEvent, OnScrollerCaptureLost);
            old.RemoveHandler(ScrollGestureEvent, OnScrollerGesture);
        }

        _scroller = scroller;
        if (scroller is null)
        {
            return;
        }

        scroller.ScrollChanged += OnScrollChanged;
        scroller.LayoutUpdated += OnScrollerLayoutUpdated;
        // 隧道 + handledEventsToo:在 ScrollViewer 自己处理(并标记已处理)之前看到用户的意图
        scroller.AddHandler(PointerWheelChangedEvent, OnScrollerWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        // 小组件(原生 WebView)吃掉的滚轮由页面交回来,同样是用户的滚动意图;不标 Handled,滚动本身由小组件交给 ScrollViewer
        scroller.AddHandler(NcwWidgetSurface.WheelForwardedEvent, OnWidgetWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.AddHandler(KeyDownEvent, OnScrollerKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(PointerPressedEvent, OnScrollerPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(PointerReleasedEvent, OnScrollerPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.AddHandler(PointerCaptureLostEvent, OnScrollerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.AddHandler(ScrollGestureEvent, OnScrollerGesture, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        UpdateWidgetInset();
    }

    private bool AtBottom(ScrollViewer scroller) =>
        TranscriptFollow.AtBottom(scroller.Offset.Y, scroller.Extent.Height, scroller.Viewport.Height);

    private void OnScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_scroller is { } scroller && e.Delta.Y != 0)
        {
            // Avalonia 的滚轮 Delta.Y > 0 = 往上滚
            _follow.UserScroll(Environment.TickCount64, towardTop: e.Delta.Y > 0, canMoveUp: scroller.Offset.Y > 0.5);
            UpdateJumpButton();
        }
    }

    private void OnWidgetWheel(object? sender, NcwWidgetWheelEventArgs e)
    {
        if (_scroller is { } scroller)
        {
            // DeltaY > 0 = 内容往下走(和 Avalonia 滚轮的符号相反)
            _follow.UserScroll(Environment.TickCount64, towardTop: e.DeltaY < 0, canMoveUp: scroller.Offset.Y > 0.5);
            UpdateJumpButton();
        }
    }

    private void OnScrollerGesture(object? sender, ScrollGestureEventArgs e) =>
        // 触摸 / 触控板手势:方向交给随后的偏移变化判断
        _follow.UserScroll(Environment.TickCount64, towardTop: false, canMoveUp: true);

    private void OnScrollerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.PageUp or Key.Up or Key.Home:
                _follow.UserScroll(Environment.TickCount64, towardTop: true, canMoveUp: scroller.Offset.Y > 0.5);
                break;
            case Key.PageDown or Key.Down or Key.End or Key.Space:
                _follow.UserScroll(Environment.TickCount64, towardTop: false, canMoveUp: true);
                break;
        }

        UpdateJumpButton();
    }

    /// <summary>按在滚动条上(拖滑块、点轨道):拖动期间不贴底,方向由偏移变化判断。</summary>
    private void OnScrollerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            _follow.BeginDrag();
        }
    }

    private void OnScrollerPointerReleased(object? sender, PointerReleasedEventArgs e) => EndDrag();

    private void OnScrollerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_follow.IsDragging && _scroller is { } scroller)
        {
            _follow.EndDrag(AtBottom(scroller));
            UpdateJumpButton();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        if (e.OffsetDelta.Y != 0)
        {
            _follow.OffsetChanged(Environment.TickCount64, e.OffsetDelta.Y, AtBottom(scroller));
        }

        UpdateJumpButton();
    }

    /// <summary>
    /// 每一轮布局之后:跟随中而不在底部(新行、流式增量、卡片报高、虚拟化重估)就贴到底。在布局结束、渲染之前完成,屏幕上不会先出现错位的一帧;
    /// 贴底本身会触发新的布局(虚拟化列表实例化底部的行、修正估算高度),所以会连追几轮直到稳定,有上限。
    /// </summary>
    private void OnScrollerLayoutUpdated(object? sender, EventArgs e)
    {
        if (_scroller is not { } scroller || !_follow.ShouldPin(AtBottom(scroller)))
        {
            return;
        }

        if (_pinBurst++ >= MaxPinsPerBurst)
        {
            QueuePinBurstReset();
            return;
        }

        QueuePinBurstReset();
        scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
    }

    private void QueuePinBurstReset()
    {
        if (_pinBurstResetQueued)
        {
            return;
        }

        _pinBurstResetQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pinBurstResetQueued = false;
            var exhausted = _pinBurst > MaxPinsPerBurst;
            _pinBurst = 0;
            if (exhausted)
            {
                _scroller?.InvalidateArrange(); // 上一帧没追上:再来一轮
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateJumpButton()
    {
        JumpButton.IsVisible = !_follow.IsFollowing && _scroller is { } scroller && scroller.Extent.Height > scroller.Viewport.Height + TranscriptFollow.BottomTolerance;
    }

    /// <summary>「回到最新」浮在转录底部:WebView 小组件是盖在窗口上的原生视图,摆放时要让出这一条(<see cref="NcwWidgetClip"/>)。</summary>
    private void UpdateWidgetInset()
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        var inset = JumpButton.IsVisible ? JumpButton.Bounds.Height + JumpButton.Margin.Bottom + 8 : 0;
        if (Math.Abs(NcwWidgetClip.GetBottomInset(scroller) - inset) > 0.5)
        {
            NcwWidgetClip.SetBottomInset(scroller, inset);
            scroller.InvalidateArrange();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReleaseLightboxAirspace();
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.FocusRequested -= OnFocusRequested;
            _vm.Rows.CollectionChanged -= OnRowsChanged;
        }

        _vm = DataContext as ConversationViewModel;
        _arriving.Clear();
        _arrivalSession = _vm?.Session;
        _arrivalsArmed = false;
        if (_vm is not null)
        {
            // 绑定那一刻已经在列表里的行是「已有的」;这一轮布局之后才开始认「刚到的」
            Dispatcher.UIThread.Post(() => _arrivalsArmed = true, DispatcherPriority.Background);
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.FocusRequested += OnFocusRequested;
            _vm.Rows.CollectionChanged += OnRowsChanged;
        }

        // 换了会话:回到底部,重新跟随
        UpdateTicker();
        ScrollToLatest();
        ApplyEmptyLayout();
    }

    /// <summary>
    /// 空会话的布局(docs/design/03 §11.4):空的时候第 3 行(起步建议)和第 1 行按比例分走剩余高度,输入框落在中间;有内容了第 3 行收起,输入框回到底部。
    /// 行高一次到位(只重排一次);同一个会话里从空变成有内容(发出了第一条消息)时,输入框从原来的位置用 <see cref="NcwMotion.Glide"/> 滑下去(只动 RenderTransform)。
    /// 换会话、快照到了这类变化直接就位,不播放。
    /// </summary>
    private void ApplyEmptyLayout()
    {
        var empty = _vm?.IsEmpty == true;
        if (empty == _layoutEmpty)
        {
            return;
        }

        _layoutEmpty = empty;
        var glide = !empty && _vm?.Session is { } session && session == _emptySession && BottomArea.IsEffectivelyVisible;
        _emptySession = empty ? _vm?.Session : null;
        var before = glide ? BottomArea.TranslatePoint(default, Root)?.Y : null;
        Root.RowDefinitions[3].Height = empty ? new GridLength(EmptyBelowWeight, GridUnitType.Star) : GridLength.Auto;
        if (before is not { } from)
        {
            NcwMotion.StopGlide(BottomArea);
            return;
        }

        NcwMotion.StopGlide(BottomArea);
        Root.UpdateLayout();
        if (BottomArea.TranslatePoint(default, Root) is { } after)
        {
            NcwMotion.Glide(BottomArea, from - after.Y);
        }
    }

    /// <summary>空会话布局当前是否是「输入框居中」(测试用)。</summary>
    public bool IsCenteredLayout => _layoutEmpty;

    /// <summary>
    /// 哪些行算「刚到的」:同一个会话里、快照已经到了、一轮里不超过 <see cref="MaxArrivalsPerTurn"/> 行的追加。
    /// 切会话后的第一批插入(整段历史)在这一轮结束之后才「武装」,所以不会整屏淡入。
    /// 同一个视图模型里换了会话(行整批换掉)同样回到底部、重新跟随。
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.Session != _arrivalSession)
        {
            // 从一个会话换到另一个:整个转录换了内容,短淡入(只动 Opacity,不碰布局与滚动;
            // 第一次打开会话时整个对话区由外层 ShowMotion 淡入,这里不重复)。关闭级别下是空操作。
            if (_arrivalSession is not null && _vm.Session is not null)
            {
                NcwMotion.Arrive(Transcript, NcwArrival.Fade, tier: NcwMotionTier.Fast);
            }

            _arrivalSession = _vm.Session;
            _arrivalsArmed = false;
            _arriving.Clear();
            Dispatcher.UIThread.Post(() => _arrivalsArmed = true, DispatcherPriority.Background);
            ScrollToLatest();
            return;
        }

        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        if (!_arrivalsArmed || _vm.IsLoading)
        {
            return;
        }

        if (!_turnScheduled)
        {
            _turnScheduled = true;
            Dispatcher.UIThread.Post(() => (_turnAdds, _turnScheduled) = (0, false), DispatcherPriority.Background);
        }

        _turnAdds += e.NewItems.Count;
        if (_turnAdds > MaxArrivalsPerTurn)
        {
            _arriving.Clear();
            return;
        }

        foreach (RowViewModel row in e.NewItems)
        {
            _arriving.Add(row);
        }
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // 展开着的「工作区」成员缩进一格(容器会被回收复用,每次都要重写)
        e.Container.Margin = e.Container.DataContext is RowViewModel { InWorkGroup: true } ? new Thickness(18, 0, 0, 0) : default;
        if (e.Container.DataContext is RowViewModel row && _arriving.Remove(row))
        {
            NcwMotion.Arrive(e.Container, NcwArrival.Rise);
        }
        else
        {
            // 容器被回收复用:上一行没播完的进入动画不能带到这一行上
            NcwMotion.Rest(e.Container);
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.NeedsTick))
        {
            UpdateTicker();
        }
        else if (e.PropertyName == nameof(ConversationViewModel.IsEmpty))
        {
            ApplyEmptyLayout();
        }
        else if (e.PropertyName == nameof(ConversationViewModel.HasLightbox))
        {
            // 灯箱盖在对话区上面,而 WebView 小组件是盖在窗口上的原生视图:打开期间让出空域,关闭恢复
            if (_vm?.HasLightbox == true)
            {
                _lightboxAirspace ??= NcwWidgetAirspace.Suspend();
            }
            else
            {
                ReleaseLightboxAirspace();
            }
        }
    }

    private void ReleaseLightboxAirspace()
    {
        _lightboxAirspace?.Dispose();
        _lightboxAirspace = null;
    }

    /// <summary>Esc 关灯箱(不管焦点在输入框还是转录里;灯箱没开时什么都不拦)。</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm?.HasLightbox == true)
        {
            _vm.CloseLightboxCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>有运行中的子代理就每秒让 ViewModel 重新派生一次(耗时前进);没有就停掉。设备断开时 NeedsTick 为假,计时冻结(规则 7)。</summary>
    private void UpdateTicker()
    {
        var need = _vm?.NeedsTick == true;
        if (need && _ticker is null)
        {
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += (_, _) => _vm?.Tick();
            _ticker.Start();
        }
        else if (!need && _ticker is not null)
        {
            _ticker.Stop();
            _ticker = null;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ticker?.Stop();
        _ticker = null;
    }

    /// <summary>回到最新:恢复跟随并贴到底(「回到最新」按钮、换会话;测试也调它)。真正的贴底在下一轮布局之后(<see cref="OnScrollerLayoutUpdated"/>)。</summary>
    public void ScrollToLatest()
    {
        _follow.Resume();
        UpdateJumpButton();
        _scroller?.InvalidateArrange();
        Dispatcher.UIThread.Post(() =>
        {
            // 绑定刚换、或列表还没有模板时没有布局可等:在 Loaded 之后补一次
            if (_follow.IsFollowing && _scroller is { } scroller && !AtBottom(scroller))
            {
                scroller.InvalidateArrange();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 用户展开 / 收起转录里的一行(工具、思考块):不再跟随最新。否则正好贴底(跟随)时,展开让内容变高,
    /// 「跟随就贴底」会把视图拽到最底,刚展开的内容反而被顶出视野。用户自己滚到底或点「回到最新」时恢复跟随。
    /// </summary>
    public void PauseFollowing()
    {
        _follow.Pause();
        UpdateJumpButton();
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e) => ScrollToLatest();

    /// <summary>
    /// 定位到一行(收件箱点进来:滚到那张审批卡片)。不再跟随最新,否则下一次内容变高又会把它顶走;「回到最新」按钮照常可用。
    /// 虚拟化列表里目标行可能还没有实体,所以等布局之后再滚。
    /// </summary>
    private void OnFocusRequested(RowViewModel row)
    {
        _follow.Pause();
        var index = _vm?.Rows.IndexOf(row) ?? -1;
        if (index < 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Transcript.ScrollIntoView(index);
            UpdateJumpButton();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>缩略图被实例化并挂上视觉树(虚拟化列表只给视野附近的行实例化):这时才向设备取字节,滚出去没挂上的不取。</summary>
    private void OnThumbAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageThumbViewModel thumb)
        {
            _ = thumb.EnsureLoadedAsync();
        }
    }

    private void OnOpenImage(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageThumbViewModel thumb)
        {
            _vm?.OpenImageCommand.Execute(thumb);
        }
    }

    /// <summary>点灯箱的空白处关闭;点图片本身不关(<see cref="OnLightboxImagePressed"/> 拦住冒泡)。</summary>
    private void OnLightboxBackdropPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) => _vm?.CloseLightboxCommand.Execute(null);

    private void OnLightboxImagePressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>复制一条消息的文字(<c>Tag</c> 绑的是消息正文):本机剪贴板,纯 UI 动作,不经设备。</summary>
    private async void OnCopyText(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string text && text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 剪贴板被别的程序占着:复制失败不值得打断对话
            }
        }
    }

    private void OnToggleThinking(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ThinkingRowViewModel row)
        {
            if (!row.IsExpanded)
            {
                PauseFollowing();
            }

            row.IsExpanded = !row.IsExpanded;
        }
    }

    private void OnToggleWorkGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WorkGroupRowViewModel row)
        {
            if (!row.IsExpanded)
            {
                PauseFollowing(); // 展开会让内容变高,和展开思考块 / 工具行一样先暂停跟随最新
            }

            row.IsExpanded = !row.IsExpanded;
        }
    }

    private void OnToggleTodo(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Todo is { } todo)
        {
            todo.IsExpanded = !todo.IsExpanded;
        }
    }

    private void OnDismissNotice(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HandledNotice notice)
        {
            _vm?.DismissNoticeCommand.Execute(notice);
        }
    }

    /// <summary>起步建议:把这句话放进输入框(不发送),焦点放进去,用户可以改了再发。</summary>
    private void OnStarterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: string key } && Enum.TryParse<Localization.NcwTextKind>(key, out var kind) && _vm is { } vm)
        {
            vm.Composer.Draft = Localization.NcwTexts.Get(kind);
            this.GetVisualDescendants().OfType<ComposerView>().FirstOrDefault()?.FocusInput();
        }
    }
}
