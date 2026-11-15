using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 启动加载界面(整窗覆盖层,docs/design/03 §10)。这里只有<b>纯 UI 状态</b>:
/// ① 一次性的 UI 定时器——ViewModel 给出「下一次需要重算的时刻」(延迟出现、最短停留结束、慢启动、阶段超时),到点调 <c>Tick</c>;没有周期性计时、没有轮询;
/// ② 所有阶段都完成之后,等下面的外壳在拿到数据后完成首次布局(布局完成事件 + 一轮低优先级调度)再报告,淡出才会开始——所以交接时不会有布局跳动;
/// ③ 淡出结束(<c>NcwMotion.IsPresent</c> 的退出完成,减弱 / 关闭级别立即)就报告「可交互」,并把自己从界面树里摘掉、停掉定时器、退订:释放之后不留任何订阅、定时器、动画循环;
/// ④ 窄窗口 / 手机壳(&lt; 720)只留标识、当前阶段的名字和说明,不画整张清单;
/// ⑤ <b>入场编排</b>(<see cref="BuildEntrance"/>):内容出现的那一刻,各部分按一份固定的脚本依次进入——时长、缓动、错峰全部是 Arc 的 token(经 <see cref="NcwMotion"/>),
/// 减弱级别只有一个没有延迟的短淡入,关闭级别立即就位;只播放一次(让位给数据页再回来不重播)。
/// </summary>
public partial class StartupOverlay : UserControl
{
    private StartupViewModel? _vm;
    private DispatcherTimer? _wake;
    private bool _shellReportQueued;
    private bool _released;
    private bool _entrancePlayed;

    public StartupOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SizeChanged += (_, e) => Classes.Set("compact", e.NewSize.Width < AdaptiveShellWidth);
    }

    /// <summary>窄于它不画整张清单(和 <c>AdaptiveShell.MobileBelow</c> 同一条界线)。</summary>
    private const double AdaptiveShellWidth = 720;

    /// <summary>入场脚本里的一步:哪个部分、用哪种进入、延迟多久、时长档位(测试断言编排用)。</summary>
    public sealed record EntranceStep(Control Part, NcwArrival Kind, TimeSpan Delay, NcwMotionTier Tier);

    /// <summary>最近一次入场播放的脚本(没播放过为空)。</summary>
    public IReadOnlyList<EntranceStep> LastEntrance { get; private set; } = [];

    /// <summary>入场动画还在进行(标识在长出来或轨道在被画出来;测试与诊断用)。</summary>
    public bool IsEntering => NcwMotion.IsArriving(Mark) || Ring.IsSweeping;

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowContentIfNeeded();
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
        ShowContentIfNeeded();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupViewModel.NextWake))
        {
            Arm();
        }
        else if (e.PropertyName == nameof(StartupViewModel.IsContentVisible))
        {
            ShowContentIfNeeded();
        }
        else if (e.PropertyName == nameof(StartupViewModel.IsAwaitingShell))
        {
            QueueShellReport();
        }
    }

    /// <summary>
    /// 一次性定时器到点时做的事(公开给测试):让 ViewModel 按此刻的时钟重算,然后<b>再武装一次</b>。
    /// 再武装是必须的:定时器和 ViewModel 用的不是同一个时钟(UI 定时器 vs <c>DateTimeOffset.UtcNow</c>),可能早几毫秒响;这时 ViewModel 重算出来的「下一个时刻」没有变,
    /// 不会再发属性变化通知,如果这里不再武装,没有任何东西会再叫醒它——最短停留永远等不到头,加载层停在那里不淡出(真窗口冒烟里 3 次出现过 1 次)。
    /// </summary>
    public void RunWake()
    {
        _wake?.Stop();
        _vm?.Tick();
        Arm();
    }

    /// <summary>
    /// 内容该出现了(延迟已过,或者失败要立即显示):让内容根可见、先让清单的行实例化,再在<b>同一个 UI 线程回合</b>里编排入场——所以不会有一帧「已经完整可见」的内容。
    /// 出现之后不再收起(让位给数据页时整层一起淡出);只播放一次入场。
    /// </summary>
    private void ShowContentIfNeeded()
    {
        if (_vm is not { IsContentVisible: true } || ContentRoot.IsVisible || _released || !this.IsAttachedToVisualTree())
        {
            return;
        }

        ContentRoot.IsVisible = true;
        ContentRoot.UpdateLayout();
        if (_entrancePlayed)
        {
            return;
        }

        _entrancePlayed = true;
        PlayEntrance();
    }

    private void PlayEntrance()
    {
        var steps = BuildEntrance();
        LastEntrance = steps;
        Ring.PlayEntrance();
        foreach (var step in steps)
        {
            NcwMotion.Arrive(step.Part, step.Kind, delay: step.Delay, tier: step.Tier);
        }
    }

    /// <summary>
    /// 入场脚本(完整级别下的时间线,t = 内容出现的那一刻):
    /// <list type="table">
    /// <item><term>0</term><description>标识长出来(<see cref="NcwArrival.Grow"/>,<c>Slow</c> 480 ms);轨道从十二点钟方向顺时针被画出来(<c>Considered</c> 480 ms),已有的进度弧跟着一起画</description></item>
    /// <item><term>StaggerLine</term><description>标题上移淡入(<c>Base</c> 240 ms)</description></item>
    /// <item><term>2 × StaggerLine + i × StaggerItem</term><description>清单第 i 行上移淡入(<c>Base</c>)——错峰往下一行一行进来</description></item>
    /// <item><term>清单之后</term><description>当前说明淡入(<c>Fast</c> 160 ms)</description></item>
    /// <item><term>Considered</term><description>轨道画完之后短弧淡入(<c>Fast</c>),开始转</description></item>
    /// </list>
    /// 失败时只有标识和错误面板(标识之后一个 StaggerLine)。窄窗口 / 手机壳没有清单,标题之后直接是说明。
    /// 上面所有数字都是 Arc 的 <c>Duration* / Stagger*</c> token,没有魔法数字;减弱 / 关闭级别忽略延迟和档位(<see cref="NcwMotion.Arrive"/>)。
    /// </summary>
    private List<EntranceStep> BuildEntrance()
    {
        var steps = new List<EntranceStep> { new(Mark, NcwArrival.Grow, TimeSpan.Zero, NcwMotionTier.Slow) };
        if (_vm is { IsFailed: true })
        {
            steps.Add(new EntranceStep(FailurePanel, NcwArrival.Rise, NcwMotion.StaggerLine, NcwMotionTier.Base));
            return steps;
        }

        var at = NcwMotion.StaggerLine;
        steps.Add(new EntranceStep(Title, NcwArrival.Rise, at, NcwMotionTier.Base));
        at += NcwMotion.StaggerLine;
        var rows = ContentRoot.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("rowShell") && p.IsEffectivelyVisible).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            steps.Add(new EntranceStep(rows[i], NcwArrival.Rise, at + i * NcwMotion.StaggerItem, NcwMotionTier.Base));
        }

        steps.Add(new EntranceStep(DetailStack, NcwArrival.Fade, at + rows.Count * NcwMotion.StaggerItem, NcwMotionTier.Fast));
        if (_vm is { IsIndeterminate: true })
        {
            steps.Add(new EntranceStep(CometHost, NcwArrival.Fade, NcwMotion.Duration(NcwMotionTier.Slow), NcwMotionTier.Fast));
        }

        return steps;
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
            _wake.Tick += (_, _) => RunWake();
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
