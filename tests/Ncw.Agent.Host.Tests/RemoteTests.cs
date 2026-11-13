using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 局域网远程连接的端到端测试(docs/design/09 §12「配对」):真实的 Agent 打开远程开关,
/// 控制端走真实的 TCP + TLS 1.3 配对、连接。
/// </summary>
public sealed class RemoteTests : IAsyncLifetime
{
    private const string Token = "t0ken";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwr-{Guid.NewGuid():N}"[..16]);
    private readonly CancellationTokenSource _cts = new();
    private readonly PeerIdentity _controller = PeerIdentity.Create("test-controller");
    private Task _run = Task.CompletedTask;
    private AgentHost _host = null!;
    private DeviceClient _local = null!;

    private AgentOptions Options => new(AgentMode.Local, _root, LocalTransport.DefaultEndpoint(_root), Token, null);

    private int Port => _host.Remote.Port ?? throw new InvalidOperationException("remote is off");

    private string Code => _host.Remote.PairingCode ?? throw new InvalidOperationException("remote is off");

    public async ValueTask InitializeAsync()
    {
        _host = new AgentHost(Options, pairingFailureDelay: TimeSpan.Zero);
        _run = _host.RunAsync(_cts.Token);
        _local = await ConnectLocal();
        var status = await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        Assert.True(status.Enabled);
        Assert.NotNull(status.Port);
        Assert.Matches("^[0-9]{6}$", status.PairingCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _local.DisposeAsync();
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<DeviceClient> ConnectLocal()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(Options.Endpoint, CancellationToken.None);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("local", "3.0.0", LocalToken: Token));
            }
            catch (Exception e) when (e is SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    private Task<PairedDevice> Pair(string? code = null, PeerIdentity? controller = null) =>
        RemoteDevices.PairAsync("127.0.0.1", Port, controller ?? _controller, code ?? Code, "Test iPhone");

    private async Task<DeviceClient> ConnectRemote(byte[]? pin, PeerIdentity? controller = null)
    {
        Stream stream = pin is null
            ? (await SecureChannel.ConnectAsync(await LanTransport.ConnectAsync("127.0.0.1", Port, default), controller ?? _controller, null, default)).Stream
            : await RemoteDevices.ConnectAsync("127.0.0.1", Port, controller ?? _controller, pin);
        return await DeviceClient.ConnectAsync(stream, new ClientHello("remote", "3.0.0"));
    }

    [Fact]
    public async Task Pairing_with_the_code_then_pinned_reconnect_gets_member_role()
    {
        var firstCode = Code;
        var paired = await Pair();

        Assert.Equal(_host.DeviceId, paired.DeviceId);
        Assert.Equal(_host.Fingerprint, paired.DeviceFingerprint);
        Assert.Equal("member", paired.Role);
        Assert.NotEmpty(paired.RouteToken);
        Assert.NotEqual(firstCode, Code); // 配对码用一次就换

        await using var client = await ConnectRemote(paired.DeviceFingerprint);
        Assert.Equal("member", client.Welcome.Role);
        Assert.NotNull(await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value));

        var remoteState = await WaitForRemoteState(s => s.Controllers.Any(c => c.Connected));
        var controller = Assert.Single(remoteState.Controllers);
        Assert.Equal("Test iPhone", controller.Name);
        Assert.Equal(_controller.FingerprintHex, controller.Fingerprint);
    }

    [Fact]
    public async Task Unpaired_controller_can_only_pair()
    {
        await using var client = await ConnectRemote(pin: null);

        Assert.Equal("unpaired", client.Welcome.Role);
        var call = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.ProjectsList, Unit.Value));
        Assert.Equal(ErrorCodes.Forbidden, call.Code);
        var sub = await Assert.ThrowsAsync<ProtocolException>(() => client.SubscribeAsync(DeviceMethods.DeviceTopic, (_, _) => { }, _ => { }));
        Assert.Equal(ErrorCodes.Forbidden, sub.Code);
    }

    [Fact]
    public async Task Remote_controllers_cannot_touch_the_remote_switch_or_see_the_code()
    {
        var paired = await Pair();
        await using var client = await ConnectRemote(paired.DeviceFingerprint);

        var set = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(false)));
        Assert.Equal(ErrorCodes.Forbidden, set.Code);
        var sub = await Assert.ThrowsAsync<ProtocolException>(() => client.SubscribeAsync(DeviceMethods.RemoteTopic, (_, _) => { }, _ => { }));
        Assert.Equal(ErrorCodes.Forbidden, sub.Code);
    }

    [Fact]
    public async Task Wrong_code_fails_and_five_failures_rotate_the_code()
    {
        var code = Code;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++)
        {
            var e = await Assert.ThrowsAsync<PairingException>(() => Pair(wrong));
            Assert.Equal(PairingFailure.WrongCode, e.Failure);
            Assert.Equal(code, Code);
        }

        await Assert.ThrowsAsync<PairingException>(() => Pair(wrong));
        Assert.NotEqual(code, Code);
        Assert.Empty((await WaitForRemoteState(_ => true)).Controllers);
    }

    /// <summary>
    /// 中间人:分别和控制端、设备做 TLS(用自己的证书),把明文原样转发。控制端在 deviceId 和公钥对不上时就停下;
    /// 即使绕过这一步(这里手工走配对),设备也会因为绑定证明对不上而拒绝,不会留下信任记录。
    /// </summary>
    [Fact]
    public async Task Relayed_pairing_through_a_man_in_the_middle_fails()
    {
        var mitm = PeerIdentity.Create("mitm");
        using var relay = new TcpListener(IPAddress.Loopback, 0);
        relay.Start();
        var relayPort = ((IPEndPoint)relay.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var inbound = await relay.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    var fromController = await SecureChannel.AcceptAsync(inbound.GetStream(), mitm, default);
                    var toDevice = await SecureChannel.ConnectAsync(await LanTransport.ConnectAsync("127.0.0.1", Port, default), mitm, null, default);
                    _ = fromController.Stream.CopyToAsync(toDevice.Stream);
                    _ = toDevice.Stream.CopyToAsync(fromController.Stream);
                });
            }
        });

        // 正常的控制端流程:deviceId 和对方公钥对不上,直接拒绝。
        var e = await Assert.ThrowsAsync<PairingException>(() => RemoteDevices.PairAsync("127.0.0.1", relayPort, _controller, Code, "victim"));
        Assert.Equal(PairingFailure.DeviceNotVerified, e.Failure);

        // 绕过 deviceId 检查,手工配对(码也是对的):设备端的绑定证明对不上。
        var channel = await SecureChannel.ConnectAsync(await LanTransport.ConnectAsync("127.0.0.1", relayPort, default), _controller, null, default);
        await using var client = await DeviceClient.ConnectAsync(channel.Stream, new ClientHello("victim", "3.0.0"));
        var begin = await client.CallAsync(DeviceMethods.PairBegin, new PairBeginParams("victim"));
        var transcript = new PairingTranscript(channel.Exporter, channel.LocalFingerprint, channel.PeerFingerprint);
        var pairing = new PairingClient(client.Welcome.DeviceId, Code);
        var (a, m1, proof) = pairing.Respond(begin.Salt, begin.B, transcript);
        var finish = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.PairFinish, new PairFinishParams(a, m1, proof)));

        Assert.Equal(ErrorCodes.Unauthorized, finish.Code);
        Assert.Empty((await WaitForRemoteState(_ => true)).Controllers);
    }

    [Fact]
    public async Task Pinned_connect_to_a_different_device_fails()
    {
        await Pair();
        var other = PeerIdentity.Create("other");

        await Assert.ThrowsAsync<SecureChannelException>(() => RemoteDevices.ConnectAsync("127.0.0.1", Port, _controller, other.Fingerprint));
    }

    [Fact]
    public async Task Revoking_disconnects_the_controller_immediately()
    {
        var paired = await Pair();
        await using var client = await ConnectRemote(paired.DeviceFingerprint);

        await _local.CallAsync(DeviceMethods.TrustRevoke, new TrustRevokeParams(_controller.FingerprintHex));

        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await using var again = await ConnectRemote(paired.DeviceFingerprint);
        Assert.Equal("unpaired", again.Welcome.Role);
    }

    [Fact]
    public async Task Turning_remote_off_disconnects_remote_sessions_but_not_local()
    {
        var paired = await Pair();
        await using var client = await ConnectRemote(paired.DeviceFingerprint);

        var off = await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(false));

        Assert.False(off.Enabled);
        Assert.Null(off.PairingCode);
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(await _local.CallAsync(DeviceMethods.ProjectsList, Unit.Value));
    }

    [Fact]
    public async Task Remote_setting_and_trust_survive_restart()
    {
        var paired = await Pair();
        await _cts.CancelAsync(); // 停掉旧的 Agent(本机连接随之断开,夹具清理时再释放)
        try { await _run; } catch (OperationCanceledException) { }

        using var cts = new CancellationTokenSource();
        var host = new AgentHost(Options, pairingFailureDelay: TimeSpan.Zero);
        var run = host.RunAsync(cts.Token);
        try
        {
            Assert.Equal(_host.DeviceId, host.DeviceId); // 身份(密钥)持久
            var port = host.Remote.Port ?? throw new InvalidOperationException("remote should come back on");
            var stream = await RemoteDevices.ConnectAsync("127.0.0.1", port, _controller, paired.DeviceFingerprint);
            await using var client = await DeviceClient.ConnectAsync(stream, new ClientHello("remote", "3.0.0"));
            Assert.Equal("member", client.Welcome.Role);
        }
        finally
        {
            await cts.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private async Task<RemoteState> WaitForRemoteState(Func<RemoteState, bool> predicate)
    {
        var done = new TaskCompletionSource<RemoteState>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await _local.SubscribeAsync(DeviceMethods.RemoteTopic,
            (state, _) => { if (predicate(state)) done.TrySetResult(state); },
            evt =>
            {
                var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RemoteState>());
                if (predicate(state)) done.TrySetResult(state);
            });
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
