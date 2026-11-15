using System.Net.WebSockets;
using System.Security.Cryptography;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;

namespace Ncw.Relay.Tests;

/// <summary>
/// 数据连接结束时「已经发出的数据必须送到」(docs/design/09 §9.3、docs/parity.md「并发不稳定测试的根因与修复」):
/// 设备在 hello 阶段发出拒绝帧后马上结束会话,控制端读到的必须是这条拒绝,而不是「连接丢失」。
/// 两环都要守住:设备侧不能把数据连接直接中止(要走关闭握手),中继侧在一边没走关闭握手就断了时,不能把另一边也中止(要送完在途数据、再给关闭帧)。
/// </summary>
public sealed class RelayCloseDeliveryTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static Task<Stream> ConnectAsync(RelayServer relay, string deviceId, string token) =>
        RelayTransport.ConnectAsync(new RelayRoute(relay.WebSocketUrl, token), deviceId, CancellationToken.None);

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Limit);
        return buffer;
    }

    [Fact]
    public async Task A_device_that_ends_a_session_without_closing_the_stream_delivers_everything_and_closes_gracefully()
    {
        var log = new LogCapture();
        await using var relay = await TestRelay.StartAsync(log: log);
        var token = Tokens.New();
        var payload = RandomNumberGenerator.GetBytes(40_000);
        // 会话处理一写完就返回,不自己关流(就像设备写完拒绝帧、ServerSession 返回):数据连接由 RelayRegistration 收尾——必须走关闭握手
        await using var device = new FakeDevice(relay.WebSocketUrl, tokenHashes: [Tokens.Hash(token)], onSession: async s => await s.Stream.WriteAsync(payload));
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(device.DeviceId), "device online");

        var stream = await ConnectAsync(relay, device.DeviceId, token);
        Assert.Equal(payload, await ReadExactlyAsync(stream, payload.Length));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Limit)); // 干净的 EOF(关闭帧),不是「连接丢失」

        await stream.DisposeAsync();
        await Wait.UntilAsync(() => log.All.Contains("session ended"), "session end logged");
        Assert.Contains("reason=closed", log.All);
        Assert.DoesNotContain("reason=aborted", log.All); // 设备没有把数据连接直接中止
    }

    [Fact]
    public async Task A_device_that_drops_without_a_close_handshake_still_delivers_what_it_sent_and_the_controller_sees_a_clean_close()
    {
        var log = new LogCapture();
        await using var relay = await TestRelay.StartAsync(log: log);
        var identity = PeerIdentity.Create("crashing-device");
        var token = Tokens.New();
        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        var nonce = Convert.FromBase64String((await raw.ReceiveAsync())!.Nonce!);
        await raw.SendAsync(new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(identity)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(identity, nonce)),
            Tokens = [Tokens.Hash(token)],
        });
        Assert.Equal("registered", (await raw.ReceiveAsync())!.Type);

        var controller = ConnectAsync(relay, identity.DeviceId, token);
        var notice = (await raw.ReceiveAsync())!;
        using var data = new ClientWebSocket();
        await data.ConnectAsync(new Uri($"{relay.WebSocketUrl.ToString().TrimEnd('/')}{RelayProtocol.DataPath}?d={identity.DeviceId}&s={notice.Session}"), CancellationToken.None);
        await using var stream = await controller;

        // 设备发了几条数据……
        var payload = RandomNumberGenerator.GetBytes(3000);
        foreach (var chunk in payload.Chunk(1000))
        {
            await data.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }

        Assert.Equal(payload, await ReadExactlyAsync(stream, payload.Length)); // (先等数据过了中继:设备到中继那一段的 RST 丢数据是设备侧的事,见 WebSocketStream 的优雅关闭)

        // ……然后崩溃(没有关闭握手,TCP 直接断)。中继不能把控制端也中止:控制端读到关闭帧(EOF),而不是 IOException「连接丢失」
        data.Abort();
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Limit));

        await stream.DisposeAsync();
        await Wait.UntilAsync(() => log.All.Contains("session ended"), "session end logged");
        Assert.Contains("reason=aborted", log.All); // 记下的原因仍是「设备没走关闭握手」
    }
}
