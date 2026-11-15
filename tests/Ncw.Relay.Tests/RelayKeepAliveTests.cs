using Ncw.Protocol.Transport;

namespace Ncw.Relay.Tests;

/// <summary>
/// 中继服务端 WebSocket 的保活超时(<see cref="RelayOptions.KeepAliveTimeout"/>)。以前 Kestrel 一侧只设了 ping 间隔、没设超时(.NET 的超时默认无限):
/// 控制端静默消失(NAT 超时、掉电、断网,没有 FIN / RST)时,这条连接要等拼接的 2 分钟空闲判定才被发现,设备那一边一直攥着这条会话。
/// 现在对端不回 ping 就中止,会话随之结束、设备那一边的会话处理器返回(设备读到的是「会话结束」,是 EOF 还是连接被重置取决于 ASP.NET 先走了哪条
/// 路径:连接一断 <c>RequestAborted</c> 就硬取消拼接,所以这里不断言是哪一种)。
/// 「静默消失」用一个只连接、之后一次都不读的 <c>ClientWebSocket</c> 模拟(托管的 WebSocket 只在有读在进行时才回 pong)。
/// 保活间隔 / 超时用 <see cref="RelayOptions"/> 调小,测试等的是事件(会话结束),不是某个时长;2 分钟的空闲判定被设得足够大,不会是它先掐断。
/// </summary>
public sealed class RelayKeepAliveTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_controller_that_never_answers_pings_is_dropped_by_the_keep_alive_timeout_and_the_device_session_ends()
    {
        var log = new LogCapture();
        await using var relay = await TestRelay.StartAsync(
            new RelayOptions
            {
                KeepAliveInterval = TimeSpan.FromMilliseconds(500),
                KeepAliveTimeout = TimeSpan.FromSeconds(3), // 建立会话期间设备那一侧也要在这个时限内回 ping,留足余量
                IdleTimeout = TimeSpan.FromMinutes(30), // 不是空闲判定掐断的
            },
            log);
        var token = Tokens.New();
        var deviceSawEnd = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new FakeDevice(relay.WebSocketUrl, tokenHashes: [Tokens.Hash(token)], onSession: async s =>
        {
            await using var stream = s.Stream;
            try
            {
                deviceSawEnd.TrySetResult(await stream.ReadAsync(new byte[1]) == 0 ? "eof" : "data");
            }
            catch (Exception e)
            {
                deviceSawEnd.TrySetResult(e.GetType().Name);
            }
        });
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(device.DeviceId), "device online");

        var (socket, first) = await RawController.OpenAsync(relay.WebSocketUrl, device.DeviceId, token);
        using (socket)
        {
            Assert.Equal("ready", first!.Type);
            Assert.Equal(1, relay.SessionCount(device.DeviceId));

            // 控制端之后一次都不读:不回 ping,中继必须自己发现并结束会话
            await Wait.UntilAsync(() => relay.SessionCount(device.DeviceId) == 0, "the silent controller's session to be ended by the keep-alive timeout");
            await deviceSawEnd.Task.WaitAsync(Limit); // 设备那一边的会话也结束了(不再攥着它)
            await Wait.UntilAsync(() => device.ActiveSessions == 0, "the device session handler to return");
        }
    }
}
