namespace Ncw.Mobile;

/// <summary>连接策略对设备连接下达的动作。</summary>
public enum ConnectionAction
{
    None,

    /// <summary>断开所有设备连接,设备状态标 unknown(AGENTS.md §3 规则 7),停掉重连计时。</summary>
    Suspend,

    /// <summary>重新连接所有已配对设备。</summary>
    Resume,

    /// <summary>丢弃现有连接并重连(连接可能已经在网络切换 / 长时间后台后失效)。</summary>
    Reconnect,
}

/// <summary>策略参数。</summary>
public sealed record ConnectionPolicyOptions
{
    /// <summary>进入后台后保持连接的宽限期:权限弹窗、系统选择器、切去别的应用看一眼,都不该断开。超过它就断开(后台进程随时会被冻结,留着半死的连接只会让界面说谎)。</summary>
    public TimeSpan BackgroundGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>后台超过这么久再回到前台,即使没断开也重连一次(TCP 在休眠中多半已经失效,没必要等应用层心跳超时)。</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>同一类网络事件的去抖:系统回调常常成串出现。</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(1500);
}

/// <summary>
/// 移动端的「什么时候连、什么时候断」(docs/design/11 §2)——纯状态机,时间和事件都由调用方给,不碰平台 API,所以可以直接单测。
/// <list type="bullet">
/// <item>前台:连接保持;网络<b>换了</b>(新的网络 id)→ <see cref="ConnectionAction.Reconnect"/>;网络<b>没了</b> → <see cref="ConnectionAction.Suspend"/>(立刻标 unknown,不干等超时);网络回来 → <see cref="ConnectionAction.Resume"/>。</item>
/// <item>后台:宽限期内保持;超过宽限期 → <see cref="ConnectionAction.Suspend"/>。回到前台:已挂起 → <see cref="ConnectionAction.Resume"/>(网络在的话);没挂起但离开太久,或后台期间网络换过 → <see cref="ConnectionAction.Reconnect"/>。</item>
/// </list>
/// 所有方法线程不安全,调用方串行化(<see cref="ConnectionSupervisor"/> 用一把锁)。
/// </summary>
public sealed class ConnectionPolicy(ConnectionPolicyOptions? options = null)
{
    private readonly ConnectionPolicyOptions _options = options ?? new ConnectionPolicyOptions();
    private bool _foreground = true;
    private bool _suspended;
    private bool _networkUp = true;
    private string? _network;
    private DateTimeOffset _backgroundSince;
    private bool _networkChangedInBackground;
    private DateTimeOffset _lastNetworkAction = DateTimeOffset.MinValue;

    public bool IsForeground => _foreground;

    public bool IsSuspended => _suspended;

    public bool IsNetworkUp => _networkUp;

    /// <summary>后台宽限期到期的时刻(还在前台 / 已经挂起为 null)。宿主据此安排一次 <see cref="OnTick"/>。</summary>
    public DateTimeOffset? SuspendDeadline => !_foreground && !_suspended ? _backgroundSince + _options.BackgroundGrace : null;

    public ConnectionAction OnBackground(DateTimeOffset now)
    {
        if (!_foreground)
        {
            return ConnectionAction.None;
        }

        _foreground = false;
        _backgroundSince = now;
        _networkChangedInBackground = false;
        return ConnectionAction.None;
    }

    public ConnectionAction OnForeground(DateTimeOffset now)
    {
        if (_foreground)
        {
            return ConnectionAction.None;
        }

        _foreground = true;
        var away = now - _backgroundSince;
        if (_suspended)
        {
            if (!_networkUp)
            {
                return ConnectionAction.None; // 没网:等网络回来(OnNetworkAvailable)
            }

            _suspended = false;
            return ConnectionAction.Resume;
        }

        return _networkChangedInBackground || away >= _options.StaleAfter ? ConnectionAction.Reconnect : ConnectionAction.None;
    }

    /// <summary>计时:后台宽限期到了就挂起。</summary>
    public ConnectionAction OnTick(DateTimeOffset now)
    {
        if (SuspendDeadline is { } deadline && now >= deadline)
        {
            _suspended = true;
            return ConnectionAction.Suspend;
        }

        return ConnectionAction.None;
    }

    /// <summary>默认网络可用了(<paramref name="networkId"/> 区分哪一个网络;第一次回调只是基线,不触发重连)。</summary>
    public ConnectionAction OnNetworkAvailable(string networkId, DateTimeOffset now)
    {
        var changed = _network is not null && _network != networkId;
        var wasDown = !_networkUp;
        _network = networkId;
        _networkUp = true;

        if (_foreground)
        {
            if (_suspended)
            {
                _suspended = false;
                _lastNetworkAction = now;
                return ConnectionAction.Resume;
            }

            if ((changed || wasDown) && now - _lastNetworkAction >= _options.Debounce)
            {
                _lastNetworkAction = now;
                return ConnectionAction.Reconnect;
            }

            return ConnectionAction.None;
        }

        if (changed || wasDown)
        {
            _networkChangedInBackground = true; // 回到前台时统一处理
        }

        return ConnectionAction.None;
    }

    /// <summary>网络丢了。丢的不是当前默认网络(切换时旧网络的 onLost 比新网络的 onAvailable 晚到)就忽略。</summary>
    public ConnectionAction OnNetworkLost(string networkId)
    {
        if (_network != networkId || !_networkUp)
        {
            return ConnectionAction.None;
        }

        _networkUp = false;
        if (_suspended)
        {
            return ConnectionAction.None;
        }

        _suspended = true;
        return ConnectionAction.Suspend; // 立刻让界面说「状态未知」,而不是等 TCP 超时
    }
}
