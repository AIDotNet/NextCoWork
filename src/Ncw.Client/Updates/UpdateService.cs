using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.Updates;

/// <summary>更新服务的可调参数(测试里调小)。</summary>
public sealed record UpdateOptions
{
    /// <summary>启动后多久做第一次后台检查(别和启动抢资源)。</summary>
    public TimeSpan FirstCheckDelay { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>后台检查的间隔(失败也按它重试)。</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>必需的更新(<c>mandatory</c>)发现后自动下载,不等用户点;可选的更新要用户点。</summary>
    public bool AutoDownloadMandatory { get; init; } = true;

    /// <summary>后台检查是否开启。关了只能手动检查。</summary>
    public bool AutoCheck { get; init; } = true;
}

/// <summary>
/// 自更新的状态机(<see cref="IUpdateService"/> 的实现)。
/// <list type="bullet">
/// <item><b>唯一写入者</b>:状态只由本类写进 <see cref="ClientStores.Updates"/>(规则 1);ViewModel 只读,动作只经 <see cref="Actions.UpdateActions"/>。</item>
/// <item><b>单飞</b>:检查 / 下载 / 安装同一时刻只有一个在进行;重复点击拿到同一个任务。</item>
/// <item><b>单调</b>(规则 6):<c>Ready</c> / <c>Installing</c> 之后迟到的检查结果不会把它改回 Available;每次新操作代数加一,旧操作的进度和结果被丢弃。
/// 清单里的版本不比当前新(feed 回滚)一律当「已是最新」,<b>不降级</b>。</item>
/// <item><b>失败不覆盖已知的好状态</b>:已经发现了更新(Available)而后来一次检查失败,仍是 Available,只在 <c>Failure</c> 里记下原因。</item>
/// <item><b>时钟可注入</b>:<see cref="TimeProvider"/> 管「上次检查时间」、后台定时器、宽限期到点的刻度。</item>
/// </list>
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private readonly ClientStores _stores;
    private readonly IUpdateEngine _engine;
    private readonly IUpdateHost _host;
    private readonly IUpdateMetaStore _meta;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _clock;
    private readonly UpdateOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private UpdateState _state = UpdateState.Initial;
    private UpdateOffer? _offer;
    private Task _flight = Task.CompletedTask;
    private string _flightKind = "";
    private ITimer? _autoTimer;
    private ITimer? _graceTimer;
    private int _disposed;

    public UpdateService(ClientStores stores, IUpdateEngine engine, IUpdateHost? host = null, IUpdateMetaStore? meta = null, IUiDispatcher? ui = null,
        TimeProvider? clock = null, UpdateOptions? options = null, Action<string>? log = null)
    {
        _stores = stores;
        _engine = engine;
        _host = host ?? NoUpdateHost.Instance;
        _meta = meta ?? new MemoryUpdateMetaStore();
        _ui = ui ?? InlineDispatcher.Instance;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new UpdateOptions();
        _log = log ?? (_ => { });
    }

    /// <summary>当前状态(服务自己持有的那份;Store 里是它的镜像,经 UI 线程更新)。</summary>
    public UpdateState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        var meta = _meta.Load();
        var channel = UpdateChannels.TryParse(meta?.Channel, out var saved) ? saved : UpdateChannel.Stable;
        var phase = _engine.IsInstalled ? UpdatePhase.Idle : UpdatePhase.Disabled;
        Set(UpdateState.Initial with { Phase = phase, CurrentVersion = _engine.CurrentVersion, Channel = channel, LastCheckedAt = meta?.LastCheckedAt });
        if (phase == UpdatePhase.Idle && _options.AutoCheck)
        {
            _autoTimer = _clock.CreateTimer(_ => _ = AutoCheckAsync(), null, _options.FirstCheckDelay, _options.CheckInterval);
        }

        return Task.CompletedTask;
    }

    public Task CheckAsync(CancellationToken ct = default) =>
        Single("check", UpdatePhaseSet.CanCheck, () => CheckCoreAsync(ct));

    public Task DownloadAsync(CancellationToken ct = default) =>
        Single("download", UpdatePhaseSet.CanDownload, () => DownloadCoreAsync(ct));

    public async Task<bool> RestartAndApplyAsync(CancellationToken ct = default)
    {
        UpdateOffer offer;
        lock (_gate)
        {
            // 只有 Ready(已下载并校验)才能装;并发的第二次点击看到 Installing,什么也不做
            if (_state.Phase != UpdatePhase.Ready || _offer is null || _flight is { IsCompleted: false })
            {
                return false;
            }

            offer = _offer;
            _state = _state with { Phase = UpdatePhase.Installing, Failure = null, Generation = _state.Generation + 1 };
            Mirror(_state);
        }

        try
        {
            // 先让更新器就位:它失败时应用还完整、sidecar 还在跑,用户什么都没失去
            _engine.StageRestart(offer);
        }
        catch (Exception e) when (e is UpdateException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _log($"update: handing over to the updater failed: {e.Message}");
            Mutate(s => s.Phase == UpdatePhase.Installing ? s with { Phase = UpdatePhase.Ready, Failure = UpdateFailureKind.ApplyFailed } : s);
            return false;
        }

        try
        {
            await _host.PrepareForRestartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // 收尾失败不拦退出:更新器已经在等了,sidecar 随进程退出时会被强制关掉
            _log($"update: preparing for the restart failed: {e.Message}");
        }

        _host.RequestExit();
        return true;
    }

    public async Task<bool> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_state.Phase is UpdatePhase.Disabled or UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing
                || _flight is { IsCompleted: false })
            {
                return false;
            }

            if (_state.Channel == channel)
            {
                return true;
            }

            _offer = null;
            _graceTimer?.Dispose();
            _graceTimer = null;
            _state = _state with
            {
                Phase = UpdatePhase.Idle, Channel = channel, Release = null, Notes = null, Progress = 0, Failure = null,
                Generation = _state.Generation + 1,
            };
            Mirror(_state);
        }

        SaveMeta();
        await CheckAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _autoTimer?.Dispose();
        _graceTimer?.Dispose();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flight.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 退出时不关心进行中的操作怎么结束
        }

        _lifetime.Dispose();
    }

    // ───────────────────────── 检查 ─────────────────────────

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        UpdateChannel channel;
        long generation;
        lock (_gate)
        {
            channel = _state.Channel;
            generation = _state.Generation + 1;
            _state = _state with { Phase = UpdatePhase.Checking, Failure = null, Generation = generation };
            Mirror(_state);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            var result = await _engine.CheckAsync(channel, cts.Token).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var current = _state.CurrentVersion;
            var newer = result.Offer is not null && UpdatePolicy.IsNewer(result.Manifest.Version, current);
            lock (_gate)
            {
                if (_state.Generation != generation)
                {
                    return; // 期间换了通道:迟到的结果丢弃
                }

                if (newer)
                {
                    _offer = result.Offer;
                    _state = _state with
                    {
                        Phase = UpdatePhase.Available, Release = result.Manifest.ToRelease(), Notes = result.Manifest.Notes,
                        Failure = null, Progress = 0, LastCheckedAt = now,
                    };
                }
                else
                {
                    // 没有更新,或 feed 里的版本不比当前新(回滚的 feed):不降级,当作已是最新
                    _offer = null;
                    _state = _state with { Phase = UpdatePhase.UpToDate, Release = null, Notes = null, Failure = null, Progress = 0, LastCheckedAt = now };
                }

                Mirror(_state);
            }

            SaveMeta();
            ScheduleGraceTick();
            if (!newer)
            {
                _log($"update: up to date (running {current}, feed offers {result.Manifest.Version})");
            }
        }
        catch (UpdateException e)
        {
            _log($"update: check failed ({e.Kind}): {e.Message}");
            FailCheck(generation, e.Kind);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            FailCheck(generation, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            _log($"update: check failed: {e.Message}");
            FailCheck(generation, UpdateFailureKind.Network);
        }
    }

    private void FailCheck(long generation, UpdateFailureKind? kind)
    {
        lock (_gate)
        {
            if (_state.Generation != generation)
            {
                return;
            }

            // 之前已经发现了更新:保留它,只记下这次检查失败的原因
            var hasKnown = _offer is not null && _state.Release is not null;
            _state = _state with
            {
                Phase = hasKnown ? UpdatePhase.Available : kind is null ? UpdatePhase.Idle : UpdatePhase.Failed,
                Failure = kind,
            };
            Mirror(_state);
        }
    }

    // ───────────────────────── 下载 ─────────────────────────

    private async Task DownloadCoreAsync(CancellationToken ct)
    {
        UpdateOffer offer;
        long generation;
        lock (_gate)
        {
            if (_offer is null)
            {
                return;
            }

            offer = _offer;
            generation = _state.Generation + 1;
            _state = _state with { Phase = UpdatePhase.Downloading, Progress = 0, Failure = null, Generation = generation };
            Mirror(_state);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await _engine.DownloadAsync(offer, percent =>
            {
                lock (_gate)
                {
                    var clamped = Math.Clamp(percent, 0, 100);
                    // 进度只前进(重复的 / 倒退的回调不闪烁),且只属于当前这次下载
                    if (_state.Generation != generation || _state.Phase != UpdatePhase.Downloading || clamped <= _state.Progress)
                    {
                        return;
                    }

                    _state = _state with { Progress = clamped };
                    Mirror(_state);
                }
            }, cts.Token).ConfigureAwait(false);
            Mutate(s => s.Generation == generation ? s with { Phase = UpdatePhase.Ready, Progress = 100, Failure = null } : s);
        }
        catch (UpdateException e)
        {
            _log($"update: download failed ({e.Kind}): {e.Message}");
            Mutate(s => s.Generation == generation ? s with { Phase = UpdatePhase.Available, Progress = 0, Failure = e.Kind } : s);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Mutate(s => s.Generation == generation ? s with { Phase = UpdatePhase.Available, Progress = 0 } : s);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            _log($"update: download failed: {e.Message}");
            Mutate(s => s.Generation == generation ? s with { Phase = UpdatePhase.Available, Progress = 0, Failure = UpdateFailureKind.DownloadFailed } : s);
        }
    }

    // ───────────────────────── 后台检查与宽限期刻度 ─────────────────────────

    private async Task AutoCheckAsync()
    {
        try
        {
            await CheckAsync(_lifetime.Token).ConfigureAwait(false);
            var shouldDownload = false;
            lock (_gate)
            {
                shouldDownload = _options.AutoDownloadMandatory && _state.Phase == UpdatePhase.Available
                    && UpdatePolicy.EnforcementOf(_state.CurrentVersion, _state.Release, _clock.GetUtcNow()) != UpdateEnforcement.None;
            }

            if (shouldDownload)
            {
                await DownloadAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出
        }
        catch (Exception e)
        {
            // 后台任务:任何没料到的异常只记日志,不能变成未观察的任务异常
            _log($"update: background check failed: {e.Message}");
        }
    }

    /// <summary>宽限期到点时给状态加一个刻度:「必需 → 阻断」是时间流逝带来的,没有任何状态事件,界面要靠它重新按时钟派生。</summary>
    private void ScheduleGraceTick()
    {
        DateTimeOffset? grace;
        lock (_gate)
        {
            grace = _state.Release is { Mandatory: true, GraceUntil: { } g } ? g : null;
            _graceTimer?.Dispose();
            _graceTimer = null;
        }

        if (grace is not { } until)
        {
            return;
        }

        var wait = until - _clock.GetUtcNow();
        if (wait < TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            _graceTimer = _clock.CreateTimer(_ => Mutate(s => s with { Tick = s.Tick + 1 }), null, wait + TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    // ───────────────────────── 内部 ─────────────────────────

    private Task Single(string kind, Func<UpdatePhase, bool> allowed, Func<Task> work)
    {
        lock (_gate)
        {
            if (_flight is { IsCompleted: false })
            {
                // 单飞:同一种操作的重复点击拿到同一个任务;别的操作进行中(检查时点下载、下载时点检查)什么也不做,立刻返回
                return _flightKind == kind ? _flight : Task.CompletedTask;
            }

            if (!allowed(_state.Phase) || _disposed == 1)
            {
                return Task.CompletedTask;
            }

            _flightKind = kind;
            return _flight = work();
        }
    }

    private void Mutate(Func<UpdateState, UpdateState> change)
    {
        lock (_gate)
        {
            var next = change(_state);
            if (ReferenceEquals(next, _state))
            {
                return;
            }

            _state = next;
            Mirror(next);
        }
    }

    private void Set(UpdateState state)
    {
        lock (_gate)
        {
            _state = state;
            Mirror(state);
        }
    }

    /// <summary>把服务持有的状态镜像到 Store(Store 只在 UI 线程上写)。调用方持有 <see cref="_gate"/>:按调用顺序投递,UI 线程上的最终值就是最后一次。</summary>
    private void Mirror(UpdateState state) => _ui.Post(() => _stores.Updates.Update(_ => state));

    private void SaveMeta()
    {
        UpdateState snapshot;
        lock (_gate)
        {
            snapshot = _state;
        }

        _meta.Save(new UpdateMeta(UpdateChannels.Id(snapshot.Channel), snapshot.LastCheckedAt));
    }
}

/// <summary>各操作允许的起始阶段。</summary>
internal static class UpdatePhaseSet
{
    /// <summary>可以检查:还没开始 / 已有结论 / 失败 / 已发现但没下载。下载中、已下载待装、正在安装、停用时不检查(不能让旧检查结果动摇它们)。</summary>
    public static bool CanCheck(UpdatePhase phase) => phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Failed or UpdatePhase.Available;

    public static bool CanDownload(UpdatePhase phase) => phase == UpdatePhase.Available;
}
