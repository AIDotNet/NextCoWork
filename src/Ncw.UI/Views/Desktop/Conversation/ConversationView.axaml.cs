using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 会话内容。这里只有纯 UI 状态:转录的滚动位置、是否「跟随最新」、展开 / 折叠;所有事实都在 <see cref="ConversationViewModel"/> 里(来自 Store)。
/// 跟随最新:在底部时,新行和流式增量让内容变高就滚到底;用户向上滚动就停止跟随并显示「回到最新」;回到底部又恢复。
/// </summary>
public partial class ConversationView : UserControl
{
    private const double BottomTolerance = 8;

    private ScrollViewer? _scroller;
    private bool _pinning;
    private DispatcherTimer? _ticker;
    private ConversationViewModel? _vm;

    public ConversationView()
    {
        InitializeComponent();
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
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.FocusRequested -= OnFocusRequested;
        }

        _vm = DataContext as ConversationViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.FocusRequested += OnFocusRequested;
        }

        // 换了会话:回到底部,重新跟随
        IsFollowing = true;
        JumpButton.IsVisible = false;
        UpdateTicker();
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.NeedsTick))
        {
            UpdateTicker();
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
        else if (e.ExtentDelta.Y != 0 && IsFollowing)
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
