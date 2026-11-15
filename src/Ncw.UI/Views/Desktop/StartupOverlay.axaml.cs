using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 启动加载界面(整窗覆盖层,docs/design/03 §10)。这里只有<b>纯 UI 状态</b>:
/// ① 一次性的 UI 定时器——ViewModel 给出「下一次需要重算的时刻」(延迟出现、最短停留结束、慢启动、阶段超时),到点调 <c>Tick</c>;没有周期性计时、没有轮询;
/// ② 所有阶段都完成之后,等下面的外壳在拿到数据后完成首次布局(布局完成事件 + 一轮低优先级调度)再报告,淡出才会开始——所以交接时不会有布局跳动;
/// ③ 淡出结束(<c>NcwMotion.IsPresent</c> 的退出完成,减弱 / 关闭级别立即)就报告「可交互」,并把自己从界面树里摘掉、停掉定时器、退订:释放之后不留任何订阅、定时器、动画循环;
/// ④ 窄窗口 / 手机壳(&lt; 720)只留标识、当前阶段的名字和说明,不画整张清单。
/// </summary>
public partial class StartupOverlay : UserControl
{
    private StartupViewModel? _vm;
    private DispatcherTimer? _wake;
    private bool _shellReportQueued;
    private bool _released;

    public StartupOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SizeChanged += (_, e) => Classes.Set("compact", e.NewSize.Width < AdaptiveShellWidth);
    }

    /// <summary>窄于它不画整张清单(和 <c>AdaptiveShell.MobileBelow</c> 同一条界线)。</summary>
    private const double AdaptiveShellWidth = 720;

    /// <summary>淡出结束、已经释放(外壳据此把它从自己的记录里去掉)。</summary>
    public event EventHandler? Released;

    /// <summary>是否已经释放(测试用)。</summary>
    public bool IsReleased => _released;

    /// <summary>一次性定时器此刻是否在等(测试用:释放之后必须为假)。</summary>
    public bool IsWaitingForWake => _wake is { IsEnabled: true };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 淡出走完(或没有动画时立即)= 隐藏了。让位给数据页时 IsRevealing 为假,不算
        if (change.Property == IsVisibleProperty && change.NewValue is false && _vm is { IsRevealing: true } && !_released)
        {
            Release();
        }
    }

    private void Attach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as StartupViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.LocalFiles = new NcwLocalFiles(this);   // 「复制诊断信息」
        Arm();
        QueueShellReport();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupViewModel.NextWake))
        {
            Arm();
        }
        else if (e.PropertyName == nameof(StartupViewModel.IsAwaitingShell))
        {
            QueueShellReport();
        }
    }

    /// <summary>按 ViewModel 给的时刻重新武装一次性定时器(没有要等的就停掉)。</summary>
    private void Arm()
    {
        if (_vm?.UntilWake is not { } until || _released)
        {
            _wake?.Stop();
            return;
        }

        if (_wake is null)
        {
            _wake = new DispatcherTimer();
            _wake.Tick += (_, _) =>
            {
                _wake?.Stop();
                _vm?.Tick();
            };
        }

        _wake.Stop();
        _wake.Interval = until < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : until;
        _wake.Start();
    }

    /// <summary>
    /// 所有阶段都完成了:下面的外壳此刻拿到了完整的数据并需要重排。等<b>布局完成事件</b>(数据进去之后的第一次完整布局),再等一轮低优先级的调度
    /// (布局和渲染的优先级都更高,所以那时画面已经是带着数据的完整一帧),然后才报告——淡出才会开始,交接时外壳不会再有布局跳动。
    /// 不用 <c>RequestAnimationFrame</c>:它依赖渲染循环在跑,Headless 和被遮挡 / 最小化的窗口里不可靠;布局事件和调度队列到处都有。
    /// </summary>
    private void QueueShellReport()
    {
        if (_vm is not { IsAwaitingShell: true } || _shellReportQueued || _released)
        {
            return;
        }

        _shellReportQueued = true;

        void Report()
        {
            _shellReportQueued = false;
            _vm?.ReportShellLaidOut();
        }

        void OnLayout(object? sender, EventArgs e)
        {
            LayoutUpdated -= OnLayout;
            Dispatcher.UIThread.Post(Report, DispatcherPriority.Background);
        }

        LayoutUpdated += OnLayout;
        InvalidateMeasure();   // 保证会有一次布局(数据早就在了,但此刻可能没有挂起的布局)
    }

    private void Release()
    {
        _released = true;
        _wake?.Stop();
        _wake = null;
        if (_vm is { } vm)
        {
            vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
            vm.ReportRevealed();   // 写「可交互」,ViewModel 随即释放自己的订阅
        }

        (Parent as Panel)?.Children.Remove(this);
        DataContext = null;
        Released?.Invoke(this, EventArgs.Empty);
    }
}
