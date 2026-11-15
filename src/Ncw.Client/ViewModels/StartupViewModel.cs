using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>清单里的一行。行是稳定的(每个阶段一行),状态原地更新,所以每一行可以自己做「完成」的状态过渡。</summary>
public sealed partial class StartupStepViewModel(StartupStage stage) : ObservableObject
{
    public StartupStage Stage { get; } = stage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsActive), nameof(IsPending))]
    public partial StartupStepState State { get; internal set; }

    public bool IsDone => State == StartupStepState.Done;

    public bool IsActive => State == StartupStepState.Active;

    public bool IsPending => State == StartupStepState.Pending;
}

/// <summary>
/// 启动加载界面(整窗覆盖层)的 ViewModel(docs/design/03 §10)。它<b>只读 Store</b>:启动事实(<see cref="ClientStores.Startup"/>)、设备的连接状态(Workspace)和数据状态(和数据页读同一个
/// <see cref="DataGateViewModel"/>),交给 Core 的纯函数 <see cref="StartupProgress.Derive"/> 派生视图、<see cref="StartupPresentation.Decide"/> 决定出现 / 停留 / 淡出;
/// 不订阅任何协议事件,自己没有定时器——需要在某个时刻再算一次时只给出 <see cref="NextWake"/>,由视图用一次性的 UI 定时器调 <see cref="Tick"/>(和对话区的计时一样,计时在视图里)。
/// 时钟是 <see cref="StartupStore.Clock"/>,所以测试用手动时钟就能驱动「延迟出现 / 最短停留 / 慢启动 / 超时」。
/// 没有在跟踪启动(<see cref="StartupState.IsTracking"/> 为假)时什么也不显示,和没有这个功能时一样。
/// </summary>
public sealed partial class StartupViewModel : ObservableObject, IDisposable
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private readonly DataGateViewModel _dataGate;
    private readonly StartupActions _actions;
    private readonly Func<IStartupHost?> _host;
    private readonly StartupTimings _timings;
    private StartupPresentation.State? _presentation;
    private bool _disposed;
    private bool _revealReported;

    public StartupViewModel(ClientStores stores, DeviceRegistry devices, DataGateViewModel dataGate, StartupActions actions, Func<IStartupHost?> host, StartupTimings? timings = null)
    {
        _stores = stores;
        _devices = devices;
        _dataGate = dataGate;
        _actions = actions;
        _host = host;
        _timings = timings ?? StartupTimings.Default;
        stores.Startup.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        dataGate.PropertyChanged += OnDataGateChanged;
        Refresh();
    }

    /// <summary>清单(每个阶段一行)。开始跟踪之后才有;形态不同阶段不同。</summary>
    public ImmutableArray<StartupStepViewModel> Steps { get; private set; } = [];

    /// <summary>
    /// 覆盖层是否「在场」(绑定到 <c>NcwMotion.IsPresent</c>):盖在窗口上、没在淡出、没有让位给数据页。变为假 = 让位或开始淡出,视图按动效级别播放退出。
    /// </summary>
    [ObservableProperty] public partial bool IsPresented { get; private set; }

    /// <summary>整窗此刻归启动加载界面(含淡出期间)。数据页据此不同时出现:同一时刻只有一个全屏层。</summary>
    [ObservableProperty] public partial bool OwnsScreen { get; private set; }

    /// <summary>内容(标识、清单、提示)是否显示;第一帧只有底色,内容要等延迟(<see cref="StartupTimings.ShowDelay"/>)。</summary>
    [ObservableProperty] public partial bool IsContentVisible { get; private set; }

    /// <summary>已经开始淡出(外壳已布局好、最短停留已过)。</summary>
    [ObservableProperty] public partial bool IsRevealing { get; private set; }

    [ObservableProperty] public partial StartupPhase Phase { get; private set; }

    [ObservableProperty] public partial StartupStage Current { get; private set; }

    [ObservableProperty] public partial StartupDetail Detail { get; private set; }

    /// <summary>正在接管 2.x 数据的哪一步(<c>backing-up</c> 等);和数据页读的是同一个事实。</summary>
    [ObservableProperty] public partial string? DataStep { get; private set; }

    /// <summary>总进度 0..1(只由已完成的阶段和真实的接管步骤算出,单调不降)。标识的描边按它画。</summary>
    [ObservableProperty] public partial double Fraction { get; private set; }

    [ObservableProperty] public partial int StepsDone { get; private set; }

    [ObservableProperty] public partial int StepsTotal { get; private set; }

    /// <summary>当前阶段内的真实步骤序号(数据接管 1..4),没有为 0。</summary>
    [ObservableProperty] public partial int SubStep { get; private set; }

    [ObservableProperty] public partial int SubTotal { get; private set; }

    /// <summary>当前阶段有真实的步骤进度(否则是不确定的:只有阶段数,标识上转一段「正在进行」的弧)。</summary>
    [ObservableProperty] public partial bool IsDeterminate { get; private set; }

    [ObservableProperty] public partial bool IsSlow { get; private set; }

    [ObservableProperty] public partial StartupSlowHint SlowHint { get; private set; }

    [ObservableProperty] public partial bool IsReconnecting { get; private set; }

    [ObservableProperty] public partial bool IsFailed { get; private set; }

    /// <summary>失败的稳定 key(<see cref="StartupFailure.Reason"/>);视图翻译。</summary>
    [ObservableProperty] public partial string? FailureReason { get; private set; }

    [ObservableProperty] public partial bool CanRetry { get; private set; }

    /// <summary>所有阶段都完成了,在等下面的外壳完成首次布局(视图据此在两帧之后调 <see cref="ReportShellLaidOut"/>)。</summary>
    [ObservableProperty] public partial bool IsAwaitingShell { get; private set; }

    /// <summary>需要再算一次的时刻(延迟出现、最短停留结束、慢启动、阶段超时);没有为 null。</summary>
    [ObservableProperty] public partial DateTimeOffset? NextWake { get; private set; }

    /// <summary>错误面板里「查看诊断」展开了(纯 UI 状态)。</summary>
    [ObservableProperty] public partial bool IsDiagnosticsOpen { get; set; }

    /// <summary>诊断文本(展开时显示,「复制诊断信息」复制它)。</summary>
    [ObservableProperty] public partial string DiagnosticsText { get; private set; } = "";

    /// <summary>剪贴板等客户端本机动作(视图装配时换成 Avalonia 的实现)。</summary>
    public ILocalFiles LocalFiles { get; set; } = NoLocalFiles.Instance;

    /// <summary>距离下一次需要重新计算还有多久(给视图的一次性定时器);没有或已过为 null / 零。</summary>
    public TimeSpan? UntilWake => NextWake is { } at ? TimeSpan.FromTicks(Math.Max(0, (at - _stores.Startup.Clock.GetUtcNow()).Ticks)) : null;

    private void OnDataGateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DataGateViewModel.Mode) or nameof(DataGateViewModel.Step) or nameof(DataGateViewModel.IsReady))
        {
            Refresh();
        }
    }

    /// <summary>按当前时钟重新算一次(视图的一次性定时器到点时调;事实变化时 Store 的订阅也会调)。</summary>
    public void Tick() => Refresh();

    /// <summary>外壳在拿到数据后完成了首次布局(视图在 <see cref="IsAwaitingShell"/> 变真之后的第二个动画帧报告)。</summary>
    public void ReportShellLaidOut()
    {
        if (!_disposed && IsAwaitingShell)
        {
            _actions.ReportShellLaidOut();
        }
    }

    /// <summary>覆盖层淡出完毕(或在没有动画的级别下立即隐藏)。只报告一次;之后本对象释放订阅,不留任何东西。</summary>
    public void ReportRevealed()
    {
        if (_disposed || _revealReported || !IsRevealing)
        {
            return;
        }

        _revealReported = true;
        _actions.ReportInteractive();   // 写 Interactive → Store 通知 → Refresh 看到 Done → 释放
    }

    [RelayCommand]
    private void Retry() => _actions.Retry();

    [RelayCommand]
    private async Task CopyDiagnosticsAsync() => await _actions.CopyDiagnosticsAsync(BuildDiagnostics());

    [RelayCommand]
    private void Quit() => _actions.Quit();

    [RelayCommand]
    private void ToggleDiagnostics()
    {
        DiagnosticsText = BuildDiagnostics();
        IsDiagnosticsOpen = !IsDiagnosticsOpen;
    }

    private StartupFacts? LiveFacts()
    {
        if (_stores.Startup.State.Facts is not { } stored)
        {
            return null;
        }

        var data = _dataGate.Mode switch
        {
            DataGateMode.Preparing => new StartupData(StartupDataPhase.Preparing),
            DataGateMode.TakingOver => new StartupData(StartupDataPhase.TakingOver, _dataGate.Step),
            DataGateMode.Blocked or DataGateMode.Failed or DataGateMode.Completed => new StartupData(StartupDataPhase.NeedsUser),
            _ => new StartupData(_dataGate.IsReady ? StartupDataPhase.Ready : StartupDataPhase.Unknown),
        };
        return stored with { Connection = StartupLive.ConnectionOf(_stores.Workspace.State, _devices, stored.Profile), Data = data };
    }

    private string BuildDiagnostics()
    {
        if (LiveFacts() is not { } facts)
        {
            return "";
        }

        var now = _stores.Startup.Clock.GetUtcNow();
        var text = StartupProgress.Diagnostics(facts, StartupProgress.Derive(facts, now, _timings), now);
        return _host()?.Diagnostics() is { Length: > 0 } extra ? text + "\n" + extra : text;
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (LiveFacts() is not { } facts)
        {
            // 没在跟踪启动:没有加载界面
            ApplyClosed();
            return;
        }

        var now = _stores.Startup.Clock.GetUtcNow();
        _presentation ??= new StartupPresentation.State(now);   // 第一次看到事实就是覆盖层的第一帧
        var view = StartupProgress.Derive(facts, now, _timings);
        var decision = StartupPresentation.Decide(view, _presentation, now, _timings);
        _presentation = decision.Next;
        Apply(view, decision);
        if (view.Phase == StartupPhase.Done)
        {
            Dispose();
        }
    }

    private void ApplyClosed()
    {
        IsPresented = false;
        OwnsScreen = false;
        IsContentVisible = false;
        IsRevealing = false;
        IsAwaitingShell = false;
        IsFailed = false;
        NextWake = null;
    }

    private void Apply(StartupView view, StartupPresentation.Decision decision)
    {
        if (Steps.IsDefaultOrEmpty)
        {
            Steps = [.. view.Steps.Select(s => new StartupStepViewModel(s.Stage))];
            OnPropertyChanged(nameof(Steps));
        }

        for (var i = 0; i < view.Steps.Length && i < Steps.Length; i++)
        {
            Steps[i].State = view.Steps[i].State;
        }

        Phase = view.Phase;
        Current = view.Current;
        Detail = view.Detail;
        DataStep = view.DataStep;
        Fraction = view.Progress.Fraction;
        StepsDone = view.Progress.StepsDone;
        StepsTotal = view.Progress.StepsTotal;
        SubStep = view.Progress.SubStep;
        SubTotal = view.Progress.SubTotal;
        IsDeterminate = view.Progress.IsDeterminateInStage;
        IsSlow = view.IsSlow;
        SlowHint = view.SlowHint;
        IsReconnecting = view.IsReconnecting;
        IsFailed = view.Failure is not null;
        FailureReason = view.Failure?.Reason;
        CanRetry = view.Failure?.CanRetry ?? false;
        IsAwaitingShell = view.Phase == StartupPhase.Settling;
        IsContentVisible = decision.ContentVisible;
        IsRevealing = decision.Reveal;
        OwnsScreen = decision.IsOpen;
        IsPresented = decision.IsOpen && !decision.Reveal;
        NextWake = decision.WakeAt;
        if (view.Failure is not null && IsDiagnosticsOpen)
        {
            DiagnosticsText = BuildDiagnostics();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stores.Startup.Changed -= Refresh;
        _stores.Workspace.Changed -= Refresh;
        _dataGate.PropertyChanged -= OnDataGateChanged;
        NextWake = null;
        IsPresented = false;
        OwnsScreen = false;
        IsContentVisible = false;
        IsRevealing = false;
        IsAwaitingShell = false;
    }
}
