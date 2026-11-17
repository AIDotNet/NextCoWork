using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 控制端的心跳(09 §7):对端静默消失时读操作会一直挂着(NAT 超时、切网、中间设备丢包:既没有 FIN 也没有 RST,TCP 自己要几十分钟才发现),
/// 所以 <see cref="DeviceClient"/> 每个间隔 ping 一次,连续几个间隔没收到设备的任何一帧就主动断开,
/// <see cref="DeviceClient.Completion"/> 带 <see cref="HeartbeatTimeoutException"/> 完成。
/// 全部确定性:手动时钟(<see cref="HeartbeatClock"/>),设备是手写的只会按脚本回话的假设备;不靠 sleep、不靠负载。
/// </summary>
public sealed class HeartbeatTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private sealed class FakeDevice(FrameStream frames) : IAsyncDisposable
    {
        public FrameStream Frames { get; } = frames;

        public int Pings;

        /// <summary>读到 hello、回 welcome,之后每收到 ping 回 pong(<paramref name="answerPings"/> 为 false 就是静默)。</summary>
        public static async Task<(FakeDevice Device, DeviceClient Client)> ConnectAsync(HeartbeatClock clock, bool answerPings, int missLimit = 3)
        {
            var (a, b) = TcpLikePipe.Create();
            var device = new FakeDevice(new FrameStream(b));
            var connecting = DeviceClient.ConnectAsync(a, new ClientHello("controller", "3.0.0"), default, new HeartbeatOptions(Interval, missLimit, clock));
            Assert.IsType<HelloFrame>(await device.Frames.ReadAsync());
            await device.Frames.WriteAsync(new WelcomeFrame(ProtocolInfo.Version, "dev-1", "3.0.0-test", "owner", [], "resume", 0));
            var client = await connecting;
            _ = device.ServeAsync(answerPings);
            // ping 循环和看门狗是握手之后在线程池上起的:等它们都挂上定时器再把连接交给测试。否则测试先推进了时钟,
            // 看门狗「还剩多久」是按旧时刻算的、定时器却按新时刻挂,到期点整体后移,永远等不到(Linux / Windows CI 上各超时过一次)。
            await clock.WaitForTimersAsync(2);
            return (device, client);
        }

        private async Task ServeAsync(bool answerPings)
        {
            try
            {
                while (await Frames.ReadAsync() is { } frame)
                {
                    if (frame is PingFrame ping)
                    {
                        Interlocked.Increment(ref Pings);
                        if (answerPings)
                        {
                            await Frames.WriteAsync(new PongFrame(ping.Time));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // 控制端关了连接
            }

            Closed.TrySetResult();
        }

        /// <summary>设备这一端读到了对端关闭(控制端把死连接关掉了)。</summary>
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => Frames.DisposeAsync();
    }

    private static async Task<T> Guard<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(30));

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var until = Environment.TickCount64 + 15_000;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException(what);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task A_silent_device_is_dropped_after_exactly_the_miss_limit_and_the_dead_transport_is_closed()
    {
        var clock = new HeartbeatClock();
        var (device, client) = await FakeDevice.ConnectAsync(clock, answerPings: false);
        await using var _ = device;
        await using var __ = client;

        clock.Advance(TimeSpan.FromSeconds(59.9)); // 3 个间隔(60 秒)还没满
        Assert.False(client.Completion.IsCompleted);
        Assert.False(device.Closed.Task.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(0.1)); // 满了:读一直挂着,但看门狗判定连接已死

        var failure = await Guard(client.Completion);
        Assert.IsType<HeartbeatTimeoutException>(failure);
        await device.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30)); // 死连接被关掉(挂着的读因此结束,不泄漏)
    }

    [Fact]
    public async Task A_request_waiting_on_a_silent_device_fails_as_a_retryable_unavailable_when_the_heartbeat_gives_up()
    {
        var clock = new HeartbeatClock();
        var (device, client) = await FakeDevice.ConnectAsync(clock, answerPings: false);
        await using var _ = device;
        await using var __ = client;

        var call = client.CallAsync(DeviceMethods.ProjectsList, Unit.Value); // 设备永远不会回
        await Task.Yield();
        clock.Advance(TimeSpan.FromSeconds(60));

        var e = await Assert.ThrowsAsync<ProtocolException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(ErrorCodes.Unavailable, e.Code);
        Assert.True(e.Retryable);
    }

    [Fact]
    public async Task A_device_that_answers_pings_is_never_dropped_however_long_the_connection_lives()
    {
        var clock = new HeartbeatClock();
        var (device, client) = await FakeDevice.ConnectAsync(clock, answerPings: true);
        await using var _ = device;
        await using var __ = client;

        for (var round = 1; round <= 10; round++) // 200 秒,远超 60 秒的时限
        {
            await clock.WaitForTimersAsync(2); // ping 循环和看门狗都已重新挂上定时器
            clock.Advance(Interval);
            await WaitUntil(() => device.Pings == round && client.IdleFor == TimeSpan.Zero, $"pong of round {round}");
            Assert.False(client.Completion.IsCompleted);
        }

        Assert.Equal(10, device.Pings); // 每个间隔恰好一次 ping
    }

    [Fact]
    public async Task Any_frame_from_the_device_counts_as_alive_not_only_pong()
    {
        var clock = new HeartbeatClock();
        var (device, client) = await FakeDevice.ConnectAsync(clock, answerPings: false);
        await using var _ = device;
        await using var __ = client;

        clock.Advance(TimeSpan.FromSeconds(50));
        await device.Frames.WriteAsync(new PongFrame(0)); // 设备主动发来的帧(这里随便用一种)
        await WaitUntil(() => client.IdleFor == TimeSpan.Zero, "frame received");

        // 分两步走:先停在看门狗原定的 60 秒上,等它按「上一帧在 50 秒」重新挂好(到期点 110 秒),再往前。
        // 一步跨过去的话,看门狗在时钟还在走的时候重新计算,到期点取决于它被调度到的那一刻。
        clock.Advance(TimeSpan.FromSeconds(10));
        await clock.WaitForTimersAsync(2);
        clock.Advance(TimeSpan.FromSeconds(40)); // 距离上一帧才 50 秒
        Assert.False(client.Completion.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(10)); // 满 60 秒
        Assert.IsType<HeartbeatTimeoutException>(await Guard(client.Completion));
    }

    [Fact]
    public async Task The_miss_limit_scales_the_deadline()
    {
        var clock = new HeartbeatClock();
        var (device, client) = await FakeDevice.ConnectAsync(clock, answerPings: false, missLimit: 5);
        await using var _ = device;
        await using var __ = client;

        clock.Advance(TimeSpan.FromSeconds(99));
        Assert.False(client.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsType<HeartbeatTimeoutException>(await Guard(client.Completion));
    }

    [Fact]
    public async Task Disposing_a_client_whose_device_went_silent_does_not_hang_and_stops_the_heartbeat()
    {
        var clock = new HeartbeatClock();
        var (device, client) = await FakeDevice.ConnectAsync(clock, answerPings: false);
        await using var _ = device;

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await device.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromMinutes(10)); // 释放之后推进时钟:不会再有 ping、也不会有迟到的失败
        Assert.True(client.Completion.IsCompleted);
        Assert.Null(await client.Completion); // 本地主动关闭,不是心跳超时
    }

    [Fact]
    public void The_defaults_follow_the_design_twenty_seconds_and_three_misses()
    {
        var options = HeartbeatOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(20), options.EffectiveInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), options.IdleTimeout);
    }
}
