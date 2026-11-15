using System.Diagnostics;
using System.Security.Cryptography;
using Ncw.Desktop.Sidecar;
using Ncw.Protocol.Wire;

namespace Ncw.Desktop;

/// <summary>
/// 本机 Agent(sidecar)的守护(docs/design/04 §3、01 §1):
/// <list type="bullet">
/// <item><b>找到并拉起</b> 同包的 ncw-agent(<c>--mode local</c>),令牌只经环境变量传给它;</item>
/// <item><b>就绪握手</b>:本机通道 + 本机令牌握手成功才算就绪(而不是看到它打印 ready);</item>
/// <item><b>接上</b> 还在运行的 Agent(上次「关窗后继续运行」留下的),不重复拉起;</item>
/// <item><b>崩溃重启</b>:指数退避,稳定运行一段时间后计数清零,连续崩溃太多次就停下并报 <see cref="SidecarIssue.CrashLoop"/>;</item>
/// <item><b>版本不一致</b>:Agent 比桌面端旧就让它退出并换包里的;比桌面端新(或 Agent 拒绝了过旧的客户端)就报 <see cref="SidecarIssue.ClientOutdated"/>;</item>
/// <item><b>退出</b>:优雅关闭(关 stdin → SIGTERM → 强杀),或按「关窗后继续运行」留下它;</item>
/// <item><b>单实例</b>:同一数据根只允许一个 Agent —— 锁在 Agent 里(<c>run/agent.lock</c>,退出码 75),这里据此报 <see cref="SidecarIssue.AlreadyRunningElsewhere"/>。</item>
/// </list>
/// 逻辑只依赖 <see cref="SidecarDependencies"/> 里的抽象(进程启动、握手、时钟),所以可以用假进程和假时钟测试。
/// </summary>
public sealed class LocalAgentSidecar : IAsyncDisposable
{
    /// <summary>ncw-agent 在单实例锁被占用时的退出码(src/Ncw.Agent/AgentInstanceLock.cs)。</summary>
    public const int AlreadyRunningExitCode = 75;

    private readonly SidecarPaths _paths;
    private readonly SidecarDependencies _deps;
    private readonly SidecarStateFile _stateFile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private TaskCompletionSource _ready = NewSignal();
    private TaskCompletionSource _retry = NewSignal();
    private SidecarStatus _status = SidecarStatus.Initial;
    private Task _supervisor = Task.CompletedTask;
    private int _disposed;

    // 当前 Agent(supervisor 线程写;DisposeAsync 在 supervisor 结束后读)
    private IAgentProcess? _process;
    private bool _spawnedByUs;
    private bool _stdinOwned;
    private bool _detachedSpawn;
    private volatile bool _forceStop;

    public LocalAgentSidecar(string dataRoot)
        : this(dataRoot, SidecarDependencies.Production(dataRoot, Ncw.UI.NcwApp.ClientDirectory, typeof(LocalAgentSidecar).Assembly))
    {
    }

    public LocalAgentSidecar(string dataRoot, SidecarDependencies dependencies)
    {
        _paths = new SidecarPaths(dataRoot);
        _deps = dependencies;
        _stateFile = new SidecarStateFile(_paths.StateFile);
        // 令牌跨桌面端会话保留(见 SidecarState):上次留下的 Agent 只认它。
        Token = _stateFile.Read() is { Token.Length: > 0 } state ? state.Token : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public string Token { get; }

    public string DataRoot => _paths.DataRoot;

    public SidecarStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>状态变化(在守护线程上触发,订阅者自己切线程)。</summary>
    public event Action<SidecarStatus>? StatusChanged;

    /// <summary>
    /// 开发阶段的数据根:<c>~/.next-cowork/agent-dev</c>(<c>NCW_AGENT_DATA_ROOT</c> 可改)。
    /// 2.x 数据接管(04 §4)做好之前,不碰 2.x 的 <c>~/.next-cowork/data</c>。
    /// </summary>
    public static string DefaultDataRoot => SidecarPaths.DefaultDataRoot();

    /// <summary>找 ncw-agent:<c>NCW_AGENT_PATH</c>,否则应用目录下的 <c>agent/ncw-agent</c>(见 <see cref="AgentLocator"/>)。</summary>
    public static string? Locate() => AgentLocator.Locate();

    public void Start() => _supervisor = Task.Run(SuperviseAsync);

    /// <summary>等到当前这一次启动的 Agent 握手成功。Agent 重启后会换成下一次的信号。</summary>
    public Task WaitReadyAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _ready.Task.WaitAsync(ct);
        }
    }

    /// <summary>用户点「重试」:从 <see cref="SidecarPhase.Failed"/> 或退避等待中立刻再来一次,并清零连续失败计数。</summary>
    public void Retry()
    {
        lock (_gate)
        {
            _retry.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SetStatus(SidecarStatus status)
    {
        lock (_gate)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    // ---- 守护循环 ----

    private async Task SuperviseAsync()
    {
        var ct = _lifetime.Token;
        var failures = 0;
        var restarts = 0;
        var outdatedHandled = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SetStatus(new SidecarStatus(SidecarPhase.Starting, RestartCount: restarts));

                // 1. 接上还在运行的,否则拉起新的
                var attached = TryAttach();
                var spawned = false;
                IAgentProcess process;
                if (attached is not null)
                {
                    process = attached;
                }
                else
                {
                    if (_deps.Locate() is not { } executable)
                    {
                        // 找不到 Agent:界面显示离线。隔一会儿再找(开发时可能还在构建)。
                        SetStatus(new SidecarStatus(SidecarPhase.Failed, SidecarIssue.AgentNotFound, RestartCount: restarts));
                        await DelayAsync(_deps.Timings.NotFoundRetry, ct).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        process = Spawn(executable);
                        spawned = true;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        failures++;
                        if (await BackOffAsync(failures, restarts, SidecarIssue.LaunchFailed, e.Message, ct).ConfigureAwait(false))
                        {
                            failures = 0;
                        }

                        continue;
                    }
                }

                var started = _deps.Clock.GetTimestamp();

                // 2. 就绪握手
                var outcome = await AwaitReadyAsync(process, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case Ready { Hello: var hello }:
                        var verdict = SidecarVersionPolicy.Evaluate(_deps.DesktopVersion, hello.AgentVersion);
                        if (verdict == VersionVerdict.AgentNewer)
                        {
                            SetStatus(new SidecarStatus(SidecarPhase.Failed, SidecarIssue.ClientOutdated, hello.AgentVersion, restarts, Detail: $"agent {hello.AgentVersion}, desktop {_deps.DesktopVersion}"));
                            await WaitForRetryAsync(ct).ConfigureAwait(false);
                            failures = 0;
                            ClearProcess(); // 不动这个 Agent,只放掉句柄;重试时重新接上
                            continue;
                        }

                        if (verdict == VersionVerdict.AgentOlder)
                        {
                            // 留下来的旧 Agent(应用更新之后还活着)→ 让它退出,换包里的。拉起来的新 Agent 还是旧版本说明安装包不完整,换了也没用。
                            await StopAsync(process, spawned || _stdinOwned).ConfigureAwait(false);
                            _stateFile.Delete();
                            ClearProcess();
                            if (spawned || outdatedHandled)
                            {
                                SetStatus(new SidecarStatus(SidecarPhase.Failed, SidecarIssue.AgentOutdated, hello.AgentVersion, restarts, Detail: $"agent {hello.AgentVersion}, desktop {_deps.DesktopVersion}"));
                                await WaitForRetryAsync(ct).ConfigureAwait(false);
                                outdatedHandled = false;
                            }
                            else
                            {
                                outdatedHandled = true;
                            }

                            continue;
                        }

                        outdatedHandled = false;
                        MarkRunning(hello, restarts, process);
                        break;

                    case Rejected { Issue: var issue, Detail: var detail }:
                        // 令牌被拒 / 客户端过旧:重试也没用,不去动这个 Agent。
                        SetStatus(new SidecarStatus(SidecarPhase.Failed, issue, null, restarts, Detail: detail));
                        await WaitForRetryAsync(ct).ConfigureAwait(false);
                        failures = 0;
                        ClearProcess();
                        continue;

                    case Exited { Code: var code }:
                        ClearProcess();
                        failures++;
                        var conflict = spawned && code == AlreadyRunningExitCode;
                        if (await BackOffAsync(failures, restarts, conflict ? SidecarIssue.AlreadyRunningElsewhere : SidecarIssue.None, $"agent exited with code {code} before it was ready", ct, conflict ? _deps.Timings.ConflictRetry : null).ConfigureAwait(false))
                        {
                            failures = 0;
                        }

                        restarts++;
                        continue;

                    default: // Timeout
                        await StopAsync(process, hasStdin: _stdinOwned).ConfigureAwait(false);
                        ClearProcess();
                        failures++;
                        if (await BackOffAsync(failures, restarts, SidecarIssue.None, "agent did not become ready in time", ct).ConfigureAwait(false))
                        {
                            failures = 0;
                        }

                        restarts++;
                        continue;
                }

                // 3. 运行中:等它退出(接上的、没有句柄的靠定期握手确认还在)
                await MonitorAsync(process, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // 意外退出:下一轮用新的就绪信号,退避后重启
                var ranFor = _deps.Clock.GetElapsedTime(started);
                ResetReady();
                var code2 = process.ExitCode;
                ClearProcess();
                failures = ranFor >= _deps.Timings.StableAfter ? 1 : failures + 1;
                restarts++;
                if (await BackOffAsync(failures, restarts, SidecarIssue.None, $"agent exited with code {code2?.ToString() ?? "?"}", ct).ConfigureAwait(false))
                {
                    failures = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>退避并重启;连续失败太多次则停在 <see cref="SidecarIssue.CrashLoop"/> 等用户重试。返回 true = 用户点了重试(计数清零)。</summary>
    private async Task<bool> BackOffAsync(int failures, int restarts, SidecarIssue issue, string detail, CancellationToken ct, TimeSpan? fixedDelay = null)
    {
        ResetReady();
        if (failures >= _deps.Timings.MaxConsecutiveFailures)
        {
            SetStatus(new SidecarStatus(SidecarPhase.Failed, issue == SidecarIssue.None ? SidecarIssue.CrashLoop : issue, null, restarts, Detail: detail));
            await WaitForRetryAsync(ct).ConfigureAwait(false);
            return true;
        }

        var delay = fixedDelay ?? SidecarTimings.Backoff(failures);
        SetStatus(new SidecarStatus(SidecarPhase.Restarting, issue, null, restarts, delay, detail));
        return await DelayOrRetryAsync(delay, ct).ConfigureAwait(false);
    }

    private IAgentProcess? TryAttach()
    {
        var state = _stateFile.Read();
        if (state is null)
        {
            return null;
        }

        if (_deps.Launcher.Attach(state.Pid) is { } existing)
        {
            _process = existing;
            _spawnedByUs = false;
            _stdinOwned = false;
            _detachedSpawn = state.Detached;
            return existing;
        }

        _stateFile.Delete(); // 进程已经不在了:过期的状态文件
        return null;
    }

    private IAgentProcess Spawn(string executable)
    {
        var keep = _deps.Policy.KeepAgentRunningAfterExit;
        var args = new List<string> { "--mode", "local", "--data-root", _paths.DataRoot };
        if (!keep)
        {
            // 桌面端崩溃时 Agent 跟着退出,不留孤儿:stdin 管道断开,以及父进程监视(双保险)。
            args.AddRange(["--lifeline", "stdin", "--parent-pid", _deps.DesktopPid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var spec = new AgentLaunchSpec(executable, args, new Dictionary<string, string> { ["NCW_LOCAL_TOKEN"] = Token }, Lifeline: !keep);
        var process = _deps.Launcher.Start(spec, _deps.Log.Write);
        _process = process;
        _spawnedByUs = true;
        _stdinOwned = !keep;
        _detachedSpawn = keep;
        _stateFile.Write(new SidecarState(process.Id, Token, Detached: keep, null, _deps.Clock.GetUtcNow()));
        return process;
    }

    private void MarkRunning(AgentHello hello, int restarts, IAgentProcess process)
    {
        _stateFile.Write(new SidecarState(process.Id, Token, _detachedSpawn, hello.AgentVersion, _deps.Clock.GetUtcNow()));
        SetStatus(new SidecarStatus(SidecarPhase.Running, SidecarIssue.None, hello.AgentVersion, restarts));
        lock (_gate)
        {
            _ready.TrySetResult();
        }
    }

    private void ResetReady()
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted)
            {
                _ready = NewSignal();
            }
        }
    }

    private void ClearProcess()
    {
        _process?.Dispose();
        _process = null;
        _spawnedByUs = false;
        _stdinOwned = false;
        _detachedSpawn = false;
    }

    // ---- 就绪等待 ----

    private abstract record ReadyOutcome;

    private sealed record Ready(AgentHello Hello) : ReadyOutcome;

    private sealed record Rejected(SidecarIssue Issue, string Detail) : ReadyOutcome;

    private sealed record Exited(int Code) : ReadyOutcome;

    private sealed record TimedOut : ReadyOutcome;

    private async Task<ReadyOutcome> AwaitReadyAsync(IAgentProcess process, CancellationToken ct)
    {
        var started = _deps.Clock.GetTimestamp();
        while (true)
        {
            if (process.HasExited)
            {
                return new Exited(process.ExitCode ?? -1);
            }

            try
            {
                var hello = await _deps.Handshake.ProbeAsync(_paths.Endpoint, Token, _deps.DesktopVersion, ct).ConfigureAwait(false);
                return new Ready(hello);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Ncw.Protocol.Wire.ProtocolException e) when (e.Code == ErrorCodes.ClientOutdated)
            {
                return new Rejected(SidecarIssue.ClientOutdated, e.Message);
            }
            catch (Ncw.Protocol.Wire.ProtocolException e) when (e.Code == ErrorCodes.Unauthorized)
            {
                return new Rejected(SidecarIssue.AlreadyRunningElsewhere, e.Message);
            }
            catch (Exception)
            {
                // 还没监听 / 握手超时:再试
            }

            if (_deps.Clock.GetElapsedTime(started) >= _deps.Timings.ReadyTimeout)
            {
                return new TimedOut();
            }

            await DelayAsync(_deps.Timings.ProbeInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>运行期间等 Agent 消失:有进程句柄就等它退出;接上的 Agent 句柄只能查 pid,另外每隔一段时间握手确认。</summary>
    private async Task MonitorAsync(IAgentProcess process, CancellationToken ct)
    {
        var exited = process.WaitForExitAsync(ct);
        var misses = 0;
        while (!ct.IsCancellationRequested)
        {
            var tick = Task.Delay(_deps.Timings.HealthInterval, _deps.Clock, ct);
            try
            {
                if (await Task.WhenAny(exited, tick).ConfigureAwait(false) == exited)
                {
                    await exited.ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_spawnedByUs)
            {
                continue; // 自己拉起的:退出事件就够了
            }

            try
            {
                await _deps.Handshake.ProbeAsync(_paths.Endpoint, Token, _deps.DesktopVersion, ct).ConfigureAwait(false);
                misses = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (++misses >= 2)
            {
                return; // 连续两次握手失败:它不在了(或卡死),走重启
            }
            catch (Exception)
            {
            }
        }
    }

    // ---- 等待工具 ----

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _deps.Clock, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>等 <paramref name="delay"/>,或用户点重试(返回 true)。</summary>
    private async Task<bool> DelayOrRetryAsync(TimeSpan delay, CancellationToken ct)
    {
        Task retry;
        lock (_gate)
        {
            retry = _retry.Task;
        }

        var wait = Task.Delay(delay, _deps.Clock, ct);
        var first = await Task.WhenAny(wait, retry).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (first == retry)
        {
            ConsumeRetry();
            return true;
        }

        return false;
    }

    private async Task WaitForRetryAsync(CancellationToken ct)
    {
        Task retry;
        lock (_gate)
        {
            retry = _retry.Task;
        }

        await retry.WaitAsync(ct).ConfigureAwait(false);
        ConsumeRetry();
    }

    private void ConsumeRetry()
    {
        lock (_gate)
        {
            if (_retry.Task.IsCompleted)
            {
                _retry = NewSignal();
            }
        }
    }

    // ---- 停止 ----

    /// <summary>
    /// 让一个 Agent 退出:关 stdin(<c>--lifeline stdin</c> 据此优雅退出;跨平台)→ SIGTERM(Unix)→ 强杀。每一步给它时间收尾。
    /// 不用 <c>CancellationToken</c>:关闭阶段不能被取消。
    /// </summary>
    private async Task StopAsync(IAgentProcess process, bool hasStdin)
    {
        if (process.HasExited)
        {
            return;
        }

        SetStatus(Status with { Phase = SidecarPhase.Stopping, Issue = SidecarIssue.None, RetryIn = null });
        if (hasStdin)
        {
            process.CloseStdin();
            if (await WaitExitAsync(process, _deps.Timings.GracefulStop).ConfigureAwait(false))
            {
                return;
            }
        }

        if (process.RequestTerminate() && await WaitExitAsync(process, _deps.Timings.TerminateWait).ConfigureAwait(false))
        {
            return;
        }

        process.Kill();
        await WaitExitAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private async Task<bool> WaitExitAsync(IAgentProcess process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout, _deps.Clock).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return process.HasExited;
        }
    }

    /// <summary>
    /// 应用更新重启之前收尾(docs/design/04 §3):和 <see cref="DisposeAsync"/> 一样优雅关闭 Agent(关 stdin → SIGTERM → 强杀),
    /// 但<b>不管「关窗后继续运行」开没开</b>——Agent 的可执行文件马上要被新版本替换,留一个旧版本在后台只会让下次启动多一次「版本不一致」的换人。
    /// 完成后守护停止(不会再重启它);之后的 <see cref="DisposeAsync"/> 是空操作。
    /// </summary>
    public ValueTask StopForUpdateAsync()
    {
        _forceStop = true;
        return DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // 重复释放(DI 容器和显式关闭各调一次)
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _supervisor.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (_process is { } process)
        {
            var keep = _deps.Policy.KeepAgentRunningAfterExit;
            // 留下的条件:开关打开,并且这个 Agent 是脱离的(拉起时没有 stdin 管道和父进程监视,桌面端退出后它能继续跑)。
            // 开关是后来才打开的、Agent 却带着管道:管道随桌面端退出而断,它会自己退出 —— 所以这里就优雅地关掉它。开关对「下一次拉起」生效。
            var leave = keep && _detachedSpawn && !_forceStop;
            if (!leave)
            {
                // 接上的 Agent:只有它是上次「关窗后继续运行」留下的(脱离的),并且用户已经关掉开关,才由我们收尾;
                // 别的程序拉起的不动。
                if (_spawnedByUs || _detachedSpawn || _forceStop)
                {
                    await StopAsync(process, _stdinOwned).ConfigureAwait(false);
                    _stateFile.Delete();
                }
            }

            SetStatus(new SidecarStatus(SidecarPhase.Stopped, AgentVersion: Status.AgentVersion, Detail: leave ? "left running" : null));
            process.Dispose();
            _process = null;
        }
        else
        {
            SetStatus(new SidecarStatus(SidecarPhase.Stopped));
        }

        _lifetime.Dispose();
    }
}
