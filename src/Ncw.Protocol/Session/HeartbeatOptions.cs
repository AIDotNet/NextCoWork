namespace Ncw.Protocol.Session;

/// <summary>
/// 控制端一侧的心跳(docs/design/09 §7):每个 <see cref="Interval"/> 发一次 <c>ping</c>,设备回 <c>pong</c>;
/// 连续 <see cref="MissLimit"/> 个间隔没有收到设备的任何一帧,就判定连接已经死了(NAT 超时、手机切网、中间设备静默丢包时,
/// 读操作会一直挂着,TCP 自己要几十分钟才发现),主动断开让上层(<c>DeviceLink</c>)按「状态未知」处理并重连。
/// 不改线上格式:ping / pong 帧本来就在协议里,设备端对控制端也有同样的空闲判定(<c>ServerOptions.IdleTimeout</c>)。
/// </summary>
/// <param name="Interval">ping 的间隔。</param>
/// <param name="MissLimit">连续没有收到任何帧的间隔数;达到就断开。</param>
/// <param name="Clock">时钟(测试注入手动时钟;null = 系统时钟)。时限和 ping 的定时都走它。</param>
public sealed record HeartbeatOptions(TimeSpan? Interval = null, int MissLimit = 3, TimeProvider? Clock = null)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(20);

    /// <summary>出厂值:20 秒 ping,3 次没回应(60 秒没有任何帧)断开。</summary>
    public static HeartbeatOptions Default { get; } = new();

    public TimeSpan EffectiveInterval => Interval ?? DefaultInterval;

    /// <summary>连续这么久没有收到设备的任何一帧就断开。</summary>
    public TimeSpan IdleTimeout => EffectiveInterval * Math.Max(MissLimit, 1);

    public TimeProvider EffectiveClock => Clock ?? TimeProvider.System;
}

/// <summary>心跳判定连接已死(对端静默消失,读一直挂着)时,<see cref="DeviceClient.Completion"/> 给出的异常。是可重试的断线。</summary>
public sealed class HeartbeatTimeoutException(TimeSpan idle) : IOException($"The device sent nothing for {idle.TotalSeconds:0.#} seconds; the connection is considered dead");
