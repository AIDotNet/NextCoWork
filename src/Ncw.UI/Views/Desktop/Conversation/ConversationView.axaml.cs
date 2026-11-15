using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 会话内容。这里只有纯 UI 状态:转录的滚动位置、是否「跟随最新」、展开 / 折叠;所有事实都在 <see cref="ConversationViewModel"/> 里(来自 Store)。
/// 跟随最新:在底部时,新行和流式增量让内容变高就滚到底;用户向上滚动就停止跟随并显示「回到最新」;回到底部又恢复。
/// </summary>
public partial class ConversationView : UserControl
{
    private const double BottomTolerance = 8;

    /// <summary>一轮里最多有几行算「刚到的」。一次性插进来更多(打开会话、快照到了、重连补齐)是加载历史,不是新消息,不播放进入动画。</summary>
    private const int MaxArrivalsPerTurn = 4;

    private ScrollViewer? _scroller;
    private bool _pinning;
    private DispatcherTimer? _ticker;
    private ConversationViewModel? _vm;
    private IDisposable? _lightboxAirspace;

    // 新行的进入动画(纯 UI 状态,docs/design/03 §9.2):虚拟化列表里只有「真正新追加」的行才动,滚动时被实例化的旧行不动,所以不影响滚动和跟随最新。
    private readonly HashSet<RowViewModel> _arriving = [];
    private SessionRef? _arrivalSession;
    private bool _arrivalsArmed;
    private int _turnAdds;
    private bool _turnScheduled;

    public ConversationView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => ReleaseLightboxAirspace();
        Transcript.ContainerPrepared += OnContainerPrepared;
        Transcript.ContainerClearing += (_, e) => NcwMotion.Rest(e.Container);
        Transcript.TemplateApplied += (_, e) =>
        {
            if (_scroller is not null)
            {
                _scroller.ScrollChanged -= OnScrollChanged;
            }

            _scroller = e.NameScope.Find<ScrollViewer>("PART_Scroller");
            if (_scroller is not null)
            {
                _scroller.ScrollChanged += OnScrollChanged;
            }
        };
    }

    /// <summary>当前是否跟随最新(测试用)。</summary>
    public bool IsFollowing { get; private set; } = true;

    /// <summary>转录的滚动容器(测试用)。</summary>
    public ScrollViewer? Scroller => _scroller;

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
        IsFollowing = true;
        JumpButton.IsVisible = false;
        UpdateTicker();
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 哪些行算「刚到的」:同一个会话里、快照已经到了、一轮里不超过 <see cref="MaxArrivalsPerTurn"/> 行的追加。
    /// 切会话后的第一批插入(整段历史)在这一轮结束之后才「武装」,所以不会整屏淡入。
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is null || e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        if (_vm.Session != _arrivalSession)
        {
            _arrivalSession = _vm.Session;
            _arrivalsArmed = false;
            _arriving.Clear();
            Dispatcher.UIThread.Post(() => _arrivalsArmed = true, DispatcherPriority.Background);
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
    private void OnPreviewKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape && _vm?.HasLightbox == true)
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

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        var atBottom = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - BottomTolerance;
        if (e.OffsetDelta.Y < 0 && !atBottom && !_pinning)
        {
            // 用户向上滚(程序自己贴底期间,虚拟化重估高度造成的偏移回退不算)
            IsFollowing = false;
        }
        else if (atBottom)
        {
            IsFollowing = true;
        }
        else if ((e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) && IsFollowing)
        {
            // 内容变高(新行、流式增量):跟随就继续贴着底
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
            return;
        }

        JumpButton.IsVisible = !IsFollowing && scroller.Extent.Height > scroller.Viewport.Height;
    }

    private void ScrollToEnd()
    {
        _pinning = true;
        _scroller?.ScrollToEnd();
        IsFollowing = true;
        JumpButton.IsVisible = false;
        Dispatcher.UIThread.Post(() => _pinning = false, DispatcherPriority.Background);
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e) => ScrollToEnd();

    /// <summary>
    /// 定位到一行(收件箱点进来:滚到那张审批卡片)。不再跟随最新,否则下一次内容变高又会把它顶走;「回到最新」按钮照常可用。
    /// 虚拟化列表里目标行可能还没有实体,所以等布局之后再滚,并反复确认(滚动可能让邻近行重估高度)。
    /// </summary>
    private void OnFocusRequested(RowViewModel row)
    {
        IsFollowing = false;
        var index = _vm?.Rows.IndexOf(row) ?? -1;
        if (index < 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Transcript.ScrollIntoView(index);
            JumpButton.IsVisible = _scroller is { } scroller && scroller.Extent.Height > scroller.Viewport.Height;
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
}
