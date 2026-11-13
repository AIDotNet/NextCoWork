using System.Net;
using System.Net.Sockets;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;

namespace Ncw.Protocol.Tests;

/// <summary>远程安全层(docs/design/09 §3、§4):TLS 1.3 双向认证与钉扎、SRP 配对与会话绑定、局域网地址过滤。</summary>
public sealed class SecurityTests
{
    private static readonly PeerIdentity Device = PeerIdentity.Create("device");
    private static readonly PeerIdentity Controller = PeerIdentity.Create("controller");

    /// <summary>一对相连的 TCP 流(真实 socket,BouncyCastle 的阻塞读写在上面跑)。</summary>
    private static async Task<(Stream Server, Stream Client)> SocketPair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        return ((await accept).GetStream(), client.GetStream());
    }

    private static async Task<(SecureChannel Server, SecureChannel Client)> Handshake(byte[]? pin)
    {
        var (s, c) = await SocketPair();
        var server = SecureChannel.AcceptAsync(s, Device, CancellationToken.None);
        var client = SecureChannel.ConnectAsync(c, Controller, pin, CancellationToken.None);
        return (await server, await client);
    }

    [Fact]
    public async Task Mutual_tls13_sees_both_keys_and_agrees_on_the_exporter()
    {
        var (server, client) = await Handshake(Device.Fingerprint);
        await using (server)
        await using (client)
        {
            Assert.Equal(Controller.Fingerprint, server.PeerFingerprint);
            Assert.Equal(Device.Fingerprint, client.PeerFingerprint);
            Assert.Equal(server.Exporter, client.Exporter);
            Assert.Equal(32, client.Exporter.Length);

            // 读写可以并发:一边挂着读,另一边照样能写。
            var read = new byte[4];
            var pending = server.Stream.ReadAsync(read).AsTask();
            await client.Stream.WriteAsync("ping"u8.ToArray());
            Assert.Equal(4, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await server.Stream.WriteAsync("pong"u8.ToArray());
            var back = new byte[4];
            await client.Stream.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("pong"u8.ToArray(), back);
        }
    }

    [Fact]
    public async Task Pinned_connection_to_another_device_fails()
    {
        var (s, c) = await SocketPair();
        var server = SecureChannel.AcceptAsync(s, Device, CancellationToken.None);
        var other = PeerIdentity.Create("someone-else");

        await Assert.ThrowsAsync<SecureChannelException>(() => SecureChannel.ConnectAsync(c, Controller, other.Fingerprint, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => server);
    }

    [Fact]
    public async Task Non_tls_peer_is_rejected_without_hanging()
    {
        var (s, c) = await SocketPair();
        var server = SecureChannel.AcceptAsync(s, Device, CancellationToken.None);
        await c.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

        await Assert.ThrowsAsync<SecureChannelException>(() => server.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static PairingTranscript Transcript(byte[] exporter) => new(exporter, Controller.Fingerprint, Device.Fingerprint);

    [Fact]
    public void Correct_code_pairs_and_both_sides_verify()
    {
        var exporter = new byte[32];
        var server = new PairingServer(Device.DeviceId, "123456");
        var client = new PairingClient(Device.DeviceId, "123 456"); // 用户输入的空格忽略

        var (a, m1, proof) = client.Respond(server.Salt, server.B, Transcript(exporter));
        var accepted = server.Finish(a, m1, proof, Transcript(exporter));

        Assert.NotNull(accepted);
        Assert.True(client.Verify(accepted.Value.M2, accepted.Value.Proof, Transcript(exporter)));
        Assert.Null(server.Finish(a, m1, proof, Transcript(exporter))); // 一次性
    }

    [Fact]
    public void Wrong_code_is_rejected()
    {
        var server = new PairingServer(Device.DeviceId, "123456");
        var client = new PairingClient(Device.DeviceId, "654321");

        var (a, m1, proof) = client.Respond(server.Salt, server.B, Transcript(new byte[32]));

        Assert.Null(server.Finish(a, m1, proof, Transcript(new byte[32])));
    }

    /// <summary>
    /// 中间人转接:两段 TLS 各自的 exporter 和证书不同。即使中间人把 SRP 消息原样转发(码也对),
    /// 两端算出的绑定证明也对不上,设备拒绝。
    /// </summary>
    [Fact]
    public void Relayed_pairing_fails_because_the_binding_differs()
    {
        var mitm = PeerIdentity.Create("mitm");
        var deviceSees = new PairingTranscript(new byte[32], mitm.Fingerprint, Device.Fingerprint);
        var controllerSees = new PairingTranscript(Enumerable.Repeat((byte)1, 32).ToArray(), Controller.Fingerprint, mitm.Fingerprint);
        var server = new PairingServer(Device.DeviceId, "123456");
        var client = new PairingClient(Device.DeviceId, "123456");

        var (a, m1, proof) = client.Respond(server.Salt, server.B, controllerSees);

        Assert.Null(server.Finish(a, m1, proof, deviceSees));
    }

    [Fact]
    public void Device_id_is_derived_from_the_public_key()
    {
        Assert.Matches("^dev_[0-9a-hjkmnp-tv-z]{26}$", Device.DeviceId);
        Assert.Equal(Device.DeviceId, PeerIdentity.DeviceIdOf(PeerIdentity.FingerprintOf(Device.Certificate)));
        Assert.NotEqual(Device.DeviceId, Controller.DeviceId);

        var reloaded = PeerIdentity.Load(Device.Certificate, Device.PrivateKey);
        Assert.Equal(Device.Fingerprint, reloaded.Fingerprint);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => PeerIdentity.Load(Device.Certificate, Controller.PrivateKey));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.23", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8::1", false)]
    public void Lan_only_filter(string address, bool allowed)
    {
        Assert.Equal(allowed, LanTransport.IsLanAddress(IPAddress.Parse(address)));
    }
}
