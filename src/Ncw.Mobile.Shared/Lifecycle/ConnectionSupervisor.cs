namespace Ncw.Mobile;

/// <summary>被监督的设备连接(<c>RemoteOnlyBackend</c> 实现)。三个方法都要幂等、可以重复调用。</summary>
public interface IConnectionController
{
    Task SuspendAsync();

    void Resume();

    Task ReconnectAsync();
}

/// <summary>
/// 把平台的生命周期 / 网络事件喂给 <see cref="ConnectionPolicy"/>,并执行它下达的动作。事件可以来自任何线程(Android 的网络回调在线程池);
/// 动作串行执行(上一个没做完下一个排队),后台宽限期用一个一次性计时器。时间来自注入的 <see cref="TimeProvider"/>。
/// </summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly ConnectionPolicy _policy;
    private readonly IConnectionController _controller;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private ITimer? _timer;
    private bool _disposed;

    private readonly Action<string>? _log;

    /// <param name="log">诊断日志(平台宿主写进 logcat / os_log):只有动作名和触发它的事件,不含任何设备或用户数据。</param>
    public ConnectionSupervisor(IConnectionController controller, ConnectionPolicyOptions? options = null, TimeProvider? time = null, Action<string>? log = null)
    {
        _log = log;
        _controller = controller;
        _time = time ?? TimeProvider.System;
        _policy = new ConnectionPolicy(options);
    }

    /// <summary>最近执行的动作(诊断用)。</summary>
    public ConnectionAction LastAction { get; private set; }

    public Task EnteredBackgroundAsync() => Apply(p => p.OnBackground(_time.GetUtcNow()), rearm: true);

    public Task EnteredForegroundAsync() => Apply(p => p.OnForeground(_time.GetUtcNow()), rearm: true);

    public Task NetworkAvailableAsync(string networkId) => Apply(p => p.OnNetworkAvailable(networkId, _time.GetUtcNow()), rearm: false);

    public Task NetworkLostAsync(string networkId) => Apply(p => p.OnNetworkLost(networkId), rearm: false);

    private async Task Apply(Func<ConnectionPolicy, ConnectionAction> step, bool rearm)
    {
        ConnectionAction action;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            action = step(_policy);
            _log?.Invoke($"event step -> {action} (foreground={_policy.IsForeground} suspended={_policy.IsSuspended} network={(_policy.IsNetworkUp ? "up" : "down")})");
            if (rearm)
            {
                ArmTimer();
            }
        }

        await Run(action).ConfigureAwait(false);
    }

    private void ArmTimer()
    {
        _timer?.Dispose();
        _timer = null;
        if (_policy.SuspendDeadline is { } deadline)
        {
            var delay = deadline - _time.GetUtcNow();
            _timer = _time.CreateTimer(_ => _ = TickAsync(), null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task TickAsync()
    {
        ConnectionAction action;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            action = _policy.OnTick(_time.GetUtcNow());
        }

        await Run(action).ConfigureAwait(false);
    }

    private async Task Run(ConnectionAction action)
    {
        if (action == ConnectionAction.None)
        {
            return;
        }

        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            LastAction = action;
            _log?.Invoke($"action={action}");
            switch (action)
            {
                case ConnectionAction.Suspend:
                    await _controller.SuspendAsync().ConfigureAwait(false);
                    break;
                case ConnectionAction.Resume:
                    _controller.Resume();
                    break;
                case ConnectionAction.Reconnect:
                    await _controller.ReconnectAsync().ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _actions.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        return ValueTask.CompletedTask;
    }
}
