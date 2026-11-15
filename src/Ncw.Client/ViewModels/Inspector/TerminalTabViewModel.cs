using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>终端页签的阶段。</summary>
public enum TerminalPhase
{
    /// <summary>远程环境的终端等用户批准(此时设备上什么都没启动)。</summary>
    AwaitingApproval,

    /// <summary>已经批准 / 本机直接创建,元数据还没到。</summary>
    Starting,

    Live,

    /// <summary>终态(退出码 / 信号在页签上)。退出之后不会因为断线被改成别的:终态压过一切(AGENTS.md §3 规则 6)。</summary>
    Exited,

    /// <summary>设备断开:状态未知(规则 7)。不判成结束,不继续计时,不接受输入;重连后按游标续传。</summary>
    Unknown,

    /// <summary>终端在设备上已经不存在(关闭、被回收)。</summary>
    Gone,
}

public enum TerminalExitKind
{
    /// <summary>退出码(<c>Value</c>)。</summary>
    Code,

    /// <summary>被信号杀死(<c>Value</c> 是信号)。</summary>
    Signal,

    /// <summary>用户终止了它。</summary>
    Killed,

    /// <summary>远程环境的连接断了,终端随之结束。</summary>
    ConnectionLost,

    /// <summary>退出了,没有更多信息。</summary>
    Plain,
}

/// <summary>终端怎么结束的(结构化,不含界面文字)。</summary>
public readonly record struct TerminalExit(TerminalExitKind Kind, int Value = 0)
{
    /// <summary>从设备给的退出码 / 信号 / 原因(<c>Exited</c> / <c>Killed</c> / <c>ConnectionLost</c>)派生。纯函数。</summary>
    public static TerminalExit Of(int? exitCode, int? signal, string? reason) => reason switch
    {
        _ when string.Equals(reason, "ConnectionLost", StringComparison.OrdinalIgnoreCase) => new(TerminalExitKind.ConnectionLost),
        _ when string.Equals(reason, "Killed", StringComparison.OrdinalIgnoreCase) => new(TerminalExitKind.Killed),
        _ when signal is { } s => new(TerminalExitKind.Signal, s),
        _ when exitCode is { } c => new(TerminalExitKind.Code, c),
        _ => new(TerminalExitKind.Plain),
    };
}

/// <summary>终端阶段的唯一派生函数(纯函数,单测见 Ncw.Client.Tests)。</summary>
public static class TerminalPhases
{
    public static TerminalPhase Of(TerminalView? view, bool deviceConnected, bool approvalPending, bool gone)
    {
        if (gone)
        {
            return TerminalPhase.Gone;
        }

        if (approvalPending)
        {
            return TerminalPhase.AwaitingApproval;
        }

        if (view is null)
        {
            return TerminalPhase.Starting;
        }

        if (view.Exited)
        {
            return TerminalPhase.Exited;
        }

        return !deviceConnected || !view.Known ? TerminalPhase.Unknown : TerminalPhase.Live;
    }
}

/// <summary>
/// 一个终端页签。元数据(状态、退出码、大小)读 <see cref="ClientStores.Terminals"/> 的镜像(由 <see cref="TerminalPanelViewModel"/> 推进来);
/// 输出字节经 <see cref="Relay"/> 直接交给显示面,不进 Store;键盘输入经 <see cref="TerminalInputPump"/> 合并后一个命令一批,大小变化经 <see cref="TerminalResizePump"/>。
/// 显示面在页签被选中时订阅(<c>terminals.attach</c>:先收整个缓冲,断线重连后设备按游标补发错过的那些,续不上就再给整个缓冲),切走时退订(终端本身不受影响)。
/// </summary>
public sealed partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private readonly ITerminalCommands _terminals;
    private readonly TerminalInputPump _input;
    private readonly TerminalResizePump _resize;
    private ITerminalSurface? _surface;
    private bool _attached;
    private int _columns = 80;
    private int _rows = 24;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public TerminalTabViewModel(ITerminalCommands terminals, string deviceId, string terminalId, TerminalIntentDto? intent = null)
    {
        _terminals = terminals;
        DeviceId = deviceId;
        Id = terminalId;
        Intent = intent;
        Phase = intent is null ? TerminalPhase.Starting : TerminalPhase.AwaitingApproval;
        Relay = new TerminalOutputRelay();
        Relay.Resync += OnResync;
        Relay.Gone += OnGone;
        _input = new TerminalInputPump(data => WriteAsync(data));
        _input.Failed += OnInputFailed;
        _resize = new TerminalResizePump((columns, rows) => ResizeAsync(columns, rows));
    }

    public string DeviceId { get; }

    public string Id { get; }

    public TerminalOutputRelay Relay { get; }

    /// <summary>等批准的意图(远程环境);批准 / 拒绝之后为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwaitingApproval))]
    public partial TerminalIntentDto? Intent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsExited), nameof(IsUnknown), nameof(IsStarting), nameof(IsAwaitingApproval), nameof(IsGone), nameof(CanType), nameof(CanKill), nameof(ShowsSurface))]
    public partial TerminalPhase Phase { get; private set; }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    /// <summary>「设备 · 环境」:这个终端在哪台设备的哪个环境里(AGENTS.md §5)。</summary>
    [ObservableProperty] public partial string EnvLabel { get; private set; } = "";

    /// <summary>远程(SSH)环境:页签上标出来。</summary>
    [ObservableProperty] public partial bool IsRemote { get; private set; }

    [ObservableProperty] public partial string Cwd { get; private set; } = "";

    [ObservableProperty] public partial string Shell { get; private set; } = "";

    [ObservableProperty] public partial int? ExitCode { get; private set; }

    [ObservableProperty] public partial int? Signal { get; private set; }

    [ObservableProperty] public partial string? ExitReason { get; private set; }

    /// <summary>结束的方式(只有 <see cref="IsExited"/> 时有意义)。</summary>
    [ObservableProperty] public partial TerminalExit Exit { get; private set; }

    [ObservableProperty] public partial Problem? Failure { get; set; }

    /// <summary>页签被选中(纯 UI 状态,由面板设置)。</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public bool IsAwaitingApproval => Phase == TerminalPhase.AwaitingApproval;

    public bool IsStarting => Phase == TerminalPhase.Starting;

    public bool IsLive => Phase == TerminalPhase.Live;

    public bool IsExited => Phase == TerminalPhase.Exited;

    public bool IsUnknown => Phase == TerminalPhase.Unknown;

    public bool IsGone => Phase == TerminalPhase.Gone;

    /// <summary>键盘输入能写进去(只有 Live;断线时不攒输入,免得重连后把几分钟前敲的键一股脑发出去)。</summary>
    public bool CanType => Phase == TerminalPhase.Live;

    public bool CanKill => Phase is TerminalPhase.Live or TerminalPhase.Unknown;

    /// <summary>显示面可见:已经有输出可看(批准前、已经没有的终端不显示)。</summary>
    public bool ShowsSurface => Phase is TerminalPhase.Starting or TerminalPhase.Live or TerminalPhase.Exited or TerminalPhase.Unknown;

    public int Columns => _columns;

    public int Rows => _rows;

    public bool IsSurfaceAttached => _surface is not null;

    /// <summary>测试 / 诊断:输入合并泵的写命令次数。</summary>
    public int InputSends => _input.Sends;

    public Task InputIdle => _input.Idle;

    /// <summary>用 Store 里的元数据和设备连接状态刷新页签(<see cref="TerminalPanelViewModel"/> 在 Store 变化后调)。</summary>
    public void Update(TerminalView? view, bool deviceConnected, string deviceName, bool gone = false)
    {
        if (view is not null)
        {
            Title = view.Info.Title;
            EnvLabel = $"{deviceName} · {view.Info.Environment.Name}";
            IsRemote = view.Info.Environment.Kind == "ssh";
            Cwd = view.Info.Cwd;
            Shell = view.Info.Shell;
            ExitCode = view.Info.ExitCode;
            Signal = view.Info.Signal;
            ExitReason = view.Info.ExitReason;
            Exit = TerminalExit.Of(view.Info.ExitCode, view.Info.Signal, view.Info.ExitReason);
            _columns = view.Info.Columns;
            _rows = view.Info.Rows;
            _resize.Known(_columns, _rows);
        }
        else if (Intent is { } intent)
        {
            Title = intent.Cwd[(intent.Cwd.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']) + 1)..];
            EnvLabel = $"{deviceName} · {intent.Environment.Name}";
            IsRemote = intent.Environment.Kind == "ssh";
            Cwd = intent.Cwd;
            Shell = intent.Shell;
        }

        Phase = TerminalPhases.Of(view, deviceConnected, Intent is not null, gone);
        if (Phase is TerminalPhase.Unknown or TerminalPhase.Exited or TerminalPhase.Gone)
        {
            _input.Clear();
        }
    }

    // ───────────────────────── 显示面 ─────────────────────────

    /// <summary>终端控件建好了:挂上去(之前攒的输出依次放出),并开始转发键盘输入和大小变化。</summary>
    public void AttachSurface(ITerminalSurface surface)
    {
        DetachSurface();
        _surface = surface;
        surface.Input += OnSurfaceInput;
        surface.GridChanged += OnSurfaceGrid;
        Relay.Attach(surface);
        OnPropertyChanged(nameof(IsSurfaceAttached));
        var (columns, rows) = surface.Grid;
        if (columns > 0 && rows > 0)
        {
            _resize.Request(columns, rows);
        }
    }

    public void DetachSurface()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        surface.Input -= OnSurfaceInput;
        surface.GridChanged -= OnSurfaceGrid;
        Relay.Detach(surface);
        _surface = null;
        OnPropertyChanged(nameof(IsSurfaceAttached));
    }

    private void OnSurfaceInput(byte[] data)
    {
        if (CanType)
        {
            _input.Enqueue(data);
        }
    }

    private void OnSurfaceGrid(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        if (CanType || IsStarting)
        {
            _resize.Request(columns, rows);
        }
    }

    // ───────────────────────── 订阅 ─────────────────────────

    /// <summary>接上输出(页签被选中)。终端还没创建出来(等批准)时什么也不做。</summary>
    public async Task AttachAsync()
    {
        if (_attached || Intent is not null)
        {
            return;
        }

        _attached = true;
        var result = await _terminals.AttachAsync(DeviceId, Id, Relay).ConfigureAwait(true);
        if (!result.IsOk)
        {
            _attached = false;
            Failure = Problem.Of(result);
        }
        else
        {
            Failure = null;
        }
    }

    /// <summary>退订(页签切走 / 整个面板不可见)。终端本身不受影响,回来再 <see cref="AttachAsync"/> 拿回缓冲。</summary>
    public async Task DetachAsync()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        await _terminals.DetachAsync(DeviceId, Id).ConfigureAwait(true);
    }

    private void OnResync()
    {
        // 攒的输出被丢了:重新订阅,设备给一份完整缓冲
        if (_attached)
        {
            _attached = false;
            _ = AttachAsync();
        }
    }

    private void OnGone() => _attached = false;

    private void OnInputFailed()
    {
        // 断线 / 已退出:状态由 Store 的镜像说话(未知 / 已退出),这里只记一笔。回调来自网络线程,切回创建本页签的线程再改属性
        var problem = new Problem(ActionOutcome.Unavailable, TerminalErrorKeys.Disconnected);
        if (_context is null)
        {
            Failure = problem;
        }
        else
        {
            _context.Post(_ => Failure = problem, null);
        }
    }

    private async Task<bool> WriteAsync(byte[] data) =>
        (await _terminals.WriteAsync(DeviceId, Id, data).ConfigureAwait(false)).IsOk;

    private async Task<bool> ResizeAsync(int columns, int rows) =>
        (await _terminals.ResizeAsync(DeviceId, Id, columns, rows).ConfigureAwait(false)).IsOk;

    public void Dispose()
    {
        DetachSurface();
        Relay.Resync -= OnResync;
        Relay.Gone -= OnGone;
        _input.Failed -= OnInputFailed;
    }
}
