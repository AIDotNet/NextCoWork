using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Ncw.Agent.Host;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Tests;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;
using Ncw.Relay;

namespace Ncw.Integration.Tests;

/// <summary>
/// 经服务器中继连接设备的端到端测试(docs/design/09 §9、§12):真实的 Agent(<c>RemoteService</c> 配了中继地址)+ 进程内的中继 +
/// 控制端(<c>DeviceClient</c> / <c>RemoteDeviceManager</c>)。局域网外的控制端经中继完成连接、协议调用、订阅、断线重连、中继重启后设备自动重注册、吊销后立即断开;
/// 局域网优先的竞速;偷到令牌的人没有用;配对只能直连。
/// </summary>
public sealed class RelayLinkTests : IAsyncLifetime
{
    private const string Token = "integration-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwy-{Guid.NewGuid():N}"[..16]);
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"ncw-relay-proj-{Guid.NewGuid():N}");
    private CancellationTokenSource _cts = new();
    private readonly PeerIdentity _controller = PeerIdentity.Create("relay-test-controller");
    private readonly ClientStores _stores = new();
    private readonly LockedDispatcher _ui = new();
    private readonly MemoryDeviceBook _book = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private Task _run = Task.CompletedTask;
    private AgentHost _agent = null!;
    private DeviceClient _local = null!;
    private RelayServer _relay = null!;

    private sealed class LockedDispatcher : IUiDispatcher
    {
        public readonly object Gate = new();

        /// <summary>每个 UI 动作执行完之后调用(在锁里):静默断开的测试用它按顺序记下设备连接状态的每一次变化。</summary>
        public Action? Observe;

        public void Post(Action action)
        {
            lock (Gate)
            {
                action();
                Observe?.Invoke();
            }
        }
    }

    private static readonly RelayClientOptions FastRetry = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_project);
        _relay = await StartRelayAsync();
        var endpoint = LocalTransport.DefaultEndpoint(_root);
        _agent = new AgentHost(new AgentOptions(AgentMode.Local, _root, endpoint, Token, null), pairingFailureDelay: TimeSpan.Zero, relayOptions: FastRetry);
        _run = _agent.RunAsync(_cts.Token);
        _local = await ConnectLocal(endpoint);
        await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        await _local.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_project, "relay-project"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try { await disposable.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        }

        await _local.DisposeAsync();
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        await _relay.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _project })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static Task<RelayServer> StartRelayAsync(int port = 0, RelayOptions? options = null) =>
        RelayServer.StartAsync(new RelayOptions
        {
            Urls = [$"http://127.0.0.1:{port}"],
            IdleTimeout = options?.IdleTimeout ?? TimeSpan.FromMinutes(2),
            MaxBytesPerSecondPerSession = 0,
            ConfigureLogging = logging => logging.ClearProviders(),
        });

    private static async Task<DeviceClient> ConnectLocal(string endpoint)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(endpoint, CancellationToken.None);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("owner", "3.0.0", LocalToken: Token));
            }
            catch (Exception e) when (e is SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    private string RelayUrl => RelayAddress.ToText(_relay.WebSocketUrl);

    private async Task UseRelayAsync()
    {
        await _local.CallAsync(DeviceMethods.RemoteConfigure, new RemoteConfigureParams(null, null, RelayUrl));
        await WaitUntil(() => _agent.Remote.RelayStatus?.State == RelayConnectionState.Registered && _relay.IsOnline(_agent.DeviceId), "device registered at relay");
    }

    private Task<PairedDevice> PairAsync(PeerIdentity? controller = null, string name = "Test Phone") =>
        RemoteDevices.PairAsync("127.0.0.1", _agent.Remote.Port!.Value, controller ?? _controller, _agent.Remote.PairingCode!, name);

    /// <summary>一个绝对连不上的局域网地址(模拟控制端在局域网外):一个刚关掉的端口。</summary>
    private static LanAddress DeadLan()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new LanAddress("127.0.0.1", port);
    }

    private DeviceAddresses RelayOnly(PairedDevice paired) => new([], new RelayRoute(_relay.WebSocketUrl, paired.RouteToken));

    private async Task<DeviceClient> ClientOver(Stream stream, string id = "phone")
    {
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(id, "3.0.0", FormFactor: "phone"));
        _disposables.Add(client);
        return client;
    }

    // ---- 配对与路由令牌 -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Pairing_over_the_lan_hands_out_the_route_token_and_the_relay_address_and_registers_the_token_hash()
    {
        await UseRelayAsync();
        Assert.Equal(0, _relay.TokenCount(_agent.DeviceId)); // 还没有人配对

        var paired = await PairAsync();

        Assert.Equal(RelayUrl, paired.Relay);
        Assert.NotEmpty(paired.RouteToken);
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "route token hash pushed to the relay");
        // 中继手里只有哈希:出示令牌本身能通,出示哈希不行。
        await using var ok = await RelayTransport.ConnectAsync(new RelayRoute(_relay.WebSocketUrl, paired.RouteToken), _agent.DeviceId, default);
        await Assert.ThrowsAsync<RelayException>(() => RelayTransport.ConnectAsync(new RelayRoute(_relay.WebSocketUrl, RelayProtocol.HashToken(paired.RouteToken)), _agent.DeviceId, default));
    }

    [Fact]
    public async Task Re_pairing_replaces_the_route_token()
    {
        await UseRelayAsync();
        var first = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "first token");
        var second = await PairAsync(); // 同一个控制端重新配对

        Assert.NotEqual(first.RouteToken, second.RouteToken);
        await WaitUntil(async () => await TokenRefused(first.RouteToken), "old token refused");
        await using var stream = await RelayTransport.ConnectAsync(new RelayRoute(_relay.WebSocketUrl, second.RouteToken), _agent.DeviceId, default);
        Assert.Equal(1, _relay.TokenCount(_agent.DeviceId));
    }

    private async Task<bool> TokenRefused(string token)
    {
        try
        {
            await using var s = await RelayTransport.ConnectAsync(new RelayRoute(_relay.WebSocketUrl, token), _agent.DeviceId, default);
            return false;
        }
        catch (RelayException e) when (e.Code == RelayErrors.Unauthorized)
        {
            return true;
        }
    }

    private static Task WaitUntil(Func<Task<bool>> condition, string what) => WaitUntilAsync(condition, what);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var until = Environment.TickCount64 + 15_000;
        while (!await condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    // ---- 经中继的会话 -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_controller_outside_the_lan_connects_calls_methods_and_receives_events_through_the_relay()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        // 局域网地址不通(控制端在局域网外):只有中继能到。
        var connection = await RemoteDevices.ConnectAsync(paired.DeviceId, new DeviceAddresses([DeadLan()], new RelayRoute(_relay.WebSocketUrl, paired.RouteToken)), _controller, paired.DeviceFingerprint);
        Assert.Equal(DeviceRoute.Relay, connection.Route);
        var client = await ClientOver(connection.Stream);

        Assert.Equal("member", client.Welcome.Role);
        Assert.Equal(_agent.DeviceId, client.Welcome.DeviceId);
        var info = await client.CallAsync(DeviceMethods.Info, Unit.Value);
        Assert.Equal(RelayUrl, info.Relay);
        var projects = await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value);
        Assert.Contains(projects.Projects, p => p.Name == "relay-project");

        // 订阅:首帧(权威快照)+ 之后的增量事件都经中继到达。
        var snapshot = new TaskCompletionSource<DeviceTopicState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<ProjectsListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await client.SubscribeAsync(DeviceMethods.DeviceTopic,
            (state, _) => snapshot.TrySetResult(state),
            evt =>
            {
                if (evt.Type == DeviceEvents.ProjectsChanged)
                {
                    changed.TrySetResult(FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<ProjectsListResult>()));
                }
            });
        await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var extra = Path.Combine(_project, "second");
        Directory.CreateDirectory(extra);
        await _local.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(extra, "second-project"));
        var list = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(list.Projects, p => p.Name == "second-project");

        // 命令类方法带幂等键,经中继同样可用。
        var added = await client.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams(list.Projects.Single(p => p.Name == "second-project").WorkspaceId), idem: "idem-1");
        Assert.NotNull(added);
        Assert.True(_relay.Metrics.SessionsStarted >= 1);
        Assert.True(_relay.Metrics.BytesToController > 0);

        // 设备上看得到这个控制端已连接。
        var state = _agent.Remote.State();
        Assert.Contains(state.Controllers, c => c.Connected);
    }

    [Fact]
    public async Task The_lan_wins_the_race_and_the_relay_is_not_even_bothered()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        var addresses = new DeviceAddresses([new LanAddress("127.0.0.1", _agent.Remote.Port!.Value)], new RelayRoute(_relay.WebSocketUrl, paired.RouteToken));
        var connection = await RemoteDevices.ConnectAsync(paired.DeviceId, addresses, _controller, paired.DeviceFingerprint);
        var client = await ClientOver(connection.Stream);

        Assert.Equal(DeviceRoute.Lan, connection.Route);
        Assert.Equal("member", client.Welcome.Role);
        await Task.Delay(500); // 过了中继的起跑延迟:它也没有被启动
        Assert.Equal(0, _relay.Metrics.SessionsStarted);
        Assert.Equal(0, _relay.SessionCount(_agent.DeviceId));
    }

    [Fact]
    public async Task A_lan_address_that_hangs_does_not_hold_back_the_relay_and_the_loser_is_cleaned_up()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        // 黑洞:接受 TCP 连接但永远不回应(局域网地址指向了一台不说话的机器)。
        using var blackhole = new TcpListener(IPAddress.Loopback, 0);
        blackhole.Start();
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true) held.Add(await blackhole.AcceptTcpClientAsync());
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException)
            {
            }
        });

        var started = Environment.TickCount64;
        var addresses = new DeviceAddresses([new LanAddress("127.0.0.1", ((IPEndPoint)blackhole.LocalEndpoint).Port)], new RelayRoute(_relay.WebSocketUrl, paired.RouteToken));
        var connection = await RemoteDevices.ConnectAsync(paired.DeviceId, addresses, _controller, paired.DeviceFingerprint);
        var client = await ClientOver(connection.Stream);

        Assert.Equal(DeviceRoute.Relay, connection.Route);
        Assert.True(Environment.TickCount64 - started < 10_000, "the relay must win long before the 15 s TLS handshake timeout of the hung LAN attempt");
        Assert.NotNull(await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value));
        blackhole.Stop();
        foreach (var c in held) c.Dispose();
    }

    [Fact]
    public async Task Pinning_holds_over_the_relay_a_device_with_another_key_behind_the_same_route_is_rejected()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        // 控制端按另一把设备指纹钉扎(例如记录被替换 / 中继把它接到了别的设备):握手失败,不会降级成「信任它」。
        var wrongPin = PeerIdentity.Create("someone-else").Fingerprint;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), _controller, wrongPin));
        Assert.IsType<SecureChannelException>(error);
    }

    [Fact]
    public async Task A_stolen_route_token_is_useless_without_the_controllers_key()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        // 攻击者拿到了令牌(比如从中继日志 / 被入侵的设备里),但没有控制端的私钥:用自己的身份连。
        var attacker = PeerIdentity.Create("attacker");
        var connection = await RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), attacker, paired.DeviceFingerprint);

        // TLS 握手本身能完成(任何出示证书的人都能),但设备在 hello 时按指纹 + 令牌绑定拒绝。
        var rejected = await Assert.ThrowsAsync<ProtocolException>(() => DeviceClient.ConnectAsync(connection.Stream, new ClientHello("attacker", "3.0.0")));
        Assert.Equal(ErrorCodes.Unauthorized, rejected.Code);
    }

    [Fact]
    public async Task A_rejected_controller_that_keeps_sending_still_gets_the_rejection_over_the_relay()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        // 设备在 hello 时拒绝,而控制端紧跟 hello 又发了数据(设备没读):设备不能立刻关连接(RST 会把拒绝帧丢掉),要等控制端先关(拒绝送达,09 §7)。
        var attacker = PeerIdentity.Create("attacker-2");
        var connection = await RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), attacker, paired.DeviceFingerprint);
        var frames = new FrameStream(connection.Stream);
        await frames.WriteAsync(new HelloFrame(ProtocolInfo.MinVersion, ProtocolInfo.Version, "attacker-2", "3.0.0", "desktop", [], 16, null, null));
        await connection.Stream.WriteAsync(new byte[512]);

        var reply = await frames.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ErrorCodes.Unauthorized, Assert.IsType<RejectFrame>(reply).Code);
        await frames.DisposeAsync();
    }

    [Fact]
    public async Task A_paired_controller_cannot_borrow_another_controllers_route_token()
    {
        await UseRelayAsync();
        var alice = await PairAsync(name: "Alice");
        var bobIdentity = PeerIdentity.Create("bob");
        var bob = await PairAsync(bobIdentity, "Bob");
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 2, "both tokens registered");

        // Bob 用自己的令牌:正常。
        var own = await RemoteDevices.ConnectAsync(bob.DeviceId, RelayOnly(bob), bobIdentity, bob.DeviceFingerprint);
        Assert.Equal("member", (await ClientOver(own.Stream, "bob")).Welcome.Role);

        // Bob 拿着 Alice 的令牌(中继放行,因为令牌有效),但令牌不属于 Bob 的指纹:设备拒绝。
        var borrowed = await RemoteDevices.ConnectAsync(alice.DeviceId, RelayOnly(alice), bobIdentity, alice.DeviceFingerprint);
        var rejected = await Assert.ThrowsAsync<ProtocolException>(() => DeviceClient.ConnectAsync(borrowed.Stream, new ClientHello("bob", "3.0.0")));
        Assert.Equal(ErrorCodes.Unauthorized, rejected.Code);
    }

    [Fact]
    public async Task Pairing_is_not_available_through_the_relay()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");
        var client = await ClientOver((await RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), _controller, paired.DeviceFingerprint)).Stream);

        var begin = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.PairBegin, new PairBeginParams("sneaky")));
        Assert.Equal(ErrorCodes.Forbidden, begin.Code);
        var claim = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.PairClaim, new PairClaimParams("sneaky", new byte[16]), idem: "i"));
        Assert.Equal(ErrorCodes.Forbidden, claim.Code);
    }

    // ---- 吊销、开关、设置 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoking_a_controller_cuts_its_relay_session_at_once_and_voids_its_route_token()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");
        var client = await ClientOver((await RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), _controller, paired.DeviceFingerprint)).Stream);
        Assert.NotNull(await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value));

        var started = Environment.TickCount64;
        await _local.CallAsync(DeviceMethods.TrustRevoke, new TrustRevokeParams(_controller.FingerprintHex));

        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5)); // 立即断开
        Assert.True(Environment.TickCount64 - started < 5000);
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 0, "token voided at the relay");
        var refused = await Assert.ThrowsAsync<RelayException>(() => RelayTransport.ConnectAsync(new RelayRoute(_relay.WebSocketUrl, paired.RouteToken), _agent.DeviceId, default));
        Assert.Equal(RelayErrors.Unauthorized, refused.Code);
    }

    [Fact]
    public async Task Turning_the_remote_switch_off_drops_relay_sessions_and_unregisters_the_device()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");
        var client = await ClientOver((await RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), _controller, paired.DeviceFingerprint)).Stream);

        await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(false));

        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => !_relay.IsOnline(_agent.DeviceId), "device unregistered");
        var offline = await Assert.ThrowsAsync<RelayException>(() => RelayTransport.ConnectAsync(new RelayRoute(_relay.WebSocketUrl, paired.RouteToken), _agent.DeviceId, default));
        Assert.Equal(RelayErrors.DeviceOffline, offline.Code);

        var status = await _local.CallAsync(DeviceMethods.RemoteGetStatus, Unit.Value);
        Assert.False(status.Enabled);
        Assert.Equal("off", status.RelayState); // 地址还在(设置保留),但没有在注册
        Assert.Equal(RelayUrl, status.RelayUrl);

        await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true)); // 重新打开:自动重新注册
        await WaitUntil(() => _relay.IsOnline(_agent.DeviceId) && _relay.TokenCount(_agent.DeviceId) == 1, "registered again with the token");
    }

    [Fact]
    public async Task The_relay_address_setting_is_validated_reported_and_can_be_cleared()
    {
        // 默认:没有中继。
        var initial = await _local.CallAsync(DeviceMethods.RemoteGetStatus, Unit.Value);
        Assert.Null(initial.RelayUrl);
        Assert.Null(initial.RelayState);
        Assert.False(_relay.IsOnline(_agent.DeviceId));

        foreach (var bad in new[] { "http://relay.example", "ws://relay.example.com", "relay.example", "wss://relay.example/?x=1", "wss://u:p@relay.example" })
        {
            var e = await Assert.ThrowsAsync<ProtocolException>(() => _local.CallAsync(DeviceMethods.RemoteConfigure, new RemoteConfigureParams(null, null, bad)));
            Assert.Equal(ErrorCodes.InvalidRequest, e.Code);
        }

        Assert.Null((await _local.CallAsync(DeviceMethods.RemoteGetStatus, Unit.Value)).RelayUrl); // 失败的配置没有留下痕迹

        await UseRelayAsync();
        var on = await _local.CallAsync(DeviceMethods.RemoteGetStatus, Unit.Value);
        Assert.Equal(RelayUrl, on.RelayUrl);
        Assert.Equal("registered", on.RelayState);
        Assert.Null(on.RelayError);
        Assert.True(on.LanOnly); // 「仅局域网」开关语义不变:它管的是局域网监听,不影响经中继
        Assert.NotNull(on.Port);

        await _local.CallAsync(DeviceMethods.RemoteConfigure, new RemoteConfigureParams(null, null, "")); // 空串 = 关闭中继
        await WaitUntil(() => !_relay.IsOnline(_agent.DeviceId), "unregistered after clearing");
        var off = await _local.CallAsync(DeviceMethods.RemoteGetStatus, Unit.Value);
        Assert.Null(off.RelayUrl);
        Assert.Null(off.RelayState);

        // 不带 RelayUrl 的 configure(旧控制端 / 只改端口)不碰中继设置。
        await UseRelayAsync();
        await _local.CallAsync(DeviceMethods.RemoteConfigure, new RemoteConfigureParams(null, false));
        Assert.Equal(RelayUrl, (await _local.CallAsync(DeviceMethods.RemoteGetStatus, Unit.Value)).RelayUrl);
    }

    [Fact]
    public async Task The_remote_topic_reports_the_relay_registration_and_its_failure()
    {
        await UseRelayAsync();
        var states = new List<RemoteState>();
        await using var subscription = await _local.SubscribeAsync(DeviceMethods.RemoteTopic,
            (state, _) => { lock (states) states.Add(state); },
            evt => { lock (states) states.Add(FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RemoteState>())); });
        await WaitUntil(() => { lock (states) return states.Any(s => s.Status.RelayState == "registered"); }, "remote topic shows the registration");

        // 中继没了:设备在状态里如实报告失败(而不是假装在线)。
        await _relay.StopAsync();
        await WaitUntil(() => { lock (states) return states.Any(s => s.Status.RelayState is "failed" or "connecting"); }, "remote topic shows the failure");
    }

    [Fact]
    public async Task The_relay_setting_and_the_registered_tokens_survive_an_agent_restart()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");

        // Agent 重启(同一个数据根):远程开关、中继地址、配对记录都在库里,中继注册自动恢复,令牌哈希重新推给中继。
        await _local.DisposeAsync();
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        await WaitUntil(() => !_relay.IsOnline(_agent.DeviceId), "device left the relay");

        _cts = new CancellationTokenSource();
        var endpoint = LocalTransport.DefaultEndpoint(_root);
        _agent = new AgentHost(new AgentOptions(AgentMode.Local, _root, endpoint, Token, null), pairingFailureDelay: TimeSpan.Zero, relayOptions: FastRetry);
        _run = _agent.RunAsync(_cts.Token);
        _local = await ConnectLocal(endpoint);

        await WaitUntil(() => _relay.IsOnline(_agent.DeviceId) && _relay.TokenCount(_agent.DeviceId) == 1, "re-registered with the stored token after the restart");
        var client = await ClientOver((await RemoteDevices.ConnectAsync(paired.DeviceId, RelayOnly(paired), _controller, paired.DeviceFingerprint)).Stream);
        Assert.Equal("member", client.Welcome.Role); // 老的配对记录直接可用,不用重新配对
    }

    // ---- 控制端侧:DeviceLink / RemoteDeviceManager -------------------------------------------------------------

    private async Task<(RemoteDeviceManager Manager, SavedDevice Saved)> ManagerOverRelayAsync(PairedDevice paired, HeartbeatOptions? heartbeat = null)
    {
        // 云端设备 / 离开局域网的手机:记录里没有可用的局域网地址,只有中继路由。
        var saved = new SavedDevice(paired.DeviceId, "Studio", Convert.ToHexStringLower(paired.DeviceFingerprint), "", 0, paired.Role, paired.RouteToken, 0, RelayUrl);
        var book = new MemoryDeviceBook();
        book.Save([saved]);
        var manager = new RemoteDeviceManager(_stores, _ui, new FixedIdentity(_controller), book, "Phone", heartbeat: heartbeat);
        _disposables.Add(manager);
        manager.Start();
        return (manager, saved);
    }

    private sealed class FixedIdentity(PeerIdentity identity) : IControllerIdentityStore
    {
        public PeerIdentity LoadOrCreate() => identity;
    }

    private bool ConnectionIs(string deviceId, DeviceConnection state)
    {
        lock (_ui.Gate)
        {
            return _stores.Workspace.State.Devices.TryGetValue(deviceId, out var d) && d.Connection == state;
        }
    }

    [Fact]
    public async Task A_device_link_over_the_relay_syncs_the_store_and_reconnects_by_itself_when_the_relay_restarts()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");
        // 中继停机是「掐断」:连接被中止,控制端这一侧不一定收到 FIN / RST(macOS 回环上压力下实测 RST 会丢,读一直挂着)。
        // 这时能让控制端知道的只有心跳;出厂是 20 秒 ping、60 秒没有任何帧才断(下面的 15 秒等不到),所以这条用例把间隔调快。
        // 「静默断开被心跳检出」本身的确定性回归测试在 Silent_peer_… 里(手动时钟)。
        await ManagerOverRelayAsync(paired, new HeartbeatOptions(TimeSpan.FromSeconds(1), MissLimit: 4));

        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "connected through the relay");
        await WaitUntil(() =>
        {
            lock (_ui.Gate) return _stores.Workspace.State.Projects.Values.Any(p => p.Ref.DeviceId == paired.DeviceId && p.Name == "relay-project");
        }, "projects synced through the relay");

        // 中继重启(同一个地址):设备自动重新注册,控制端的 DeviceLink 自动重连,Store 回到已连接。
        var port = _relay.WebSocketUrl.Port;
        await _relay.DisposeAsync();
        await WaitUntil(() => !ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "link noticed the drop");
        _relay = await StartRelayAsync(port);

        await WaitUntil(() => _agent.Remote.RelayStatus?.State == RelayConnectionState.Registered && _relay.IsOnline(_agent.DeviceId), "device re-registered with the new relay", 30_000);
        Assert.Equal(1, _relay.TokenCount(_agent.DeviceId)); // 令牌哈希随注册带回来
        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "link reconnected through the new relay", 30_000);
    }

    // ---- 对端静默消失(NAT 超时、切网、中间设备丢包:没有 FIN 也没有 RST,读一直挂着) -----------------------------------------
    // 确定性:真实的 Agent + 真实的局域网 TCP / 中继承载,字节流外面包一层 BlackHoleStream(开启后读永远挂着、写被吞掉),
    // 心跳的时间由手动时钟推进。不靠 sleep、不靠真的断网、也不靠负载。

    [Fact]
    public async Task Silent_peer_over_the_lan_is_noticed_by_the_heartbeat_marked_unknown_and_reconnected()
    {
        var paired = await PairAsync();
        await AssertSilentPeerIsDroppedAndReconnectedAsync(paired, new DeviceAddresses([new LanAddress("127.0.0.1", _agent.Remote.Port!.Value)], null));
    }

    [Fact]
    public async Task Silent_peer_over_the_relay_is_noticed_by_the_heartbeat_marked_unknown_and_reconnected()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");
        await AssertSilentPeerIsDroppedAndReconnectedAsync(paired, RelayOnly(paired));
    }

    private async Task AssertSilentPeerIsDroppedAndReconnectedAsync(PairedDevice paired, DeviceAddresses addresses)
    {
        var clock = new HeartbeatClock();
        var streams = new List<BlackHoleStream>();
        var history = new List<(DeviceConnection Connection, bool HasData)>();
        lock (_ui.Gate)
        {
            _ui.Observe = () =>
            {
                if (_stores.Workspace.State.Devices.TryGetValue(paired.DeviceId, out var d) && (history.Count == 0 || history[^1].Connection != d.Connection))
                {
                    history.Add((d.Connection, d.Data is not null));
                }
            };
        }

        var link = new DeviceLink(_stores, _ui, paired.DeviceId, DeviceKind.Remote, "Studio",
            async ct =>
            {
                var connection = await RemoteDevices.ConnectAsync(paired.DeviceId, addresses, _controller, paired.DeviceFingerprint, ct);
                var stream = new BlackHoleStream(connection.Stream);
                lock (streams)
                {
                    streams.Add(stream);
                }

                return stream;
            },
            new ClientHello("phone", "3.0.0", FormFactor: "phone"),
            heartbeat: new HeartbeatOptions(TimeSpan.FromSeconds(20), MissLimit: 3, clock));
        _disposables.Add(link);
        link.Start();
        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "connected");
        await WaitUntil(() =>
        {
            lock (_ui.Gate) return _stores.Workspace.State.Projects.Values.Any(p => p.Ref.DeviceId == paired.DeviceId && p.Name == "relay-project")
                                   && _stores.Workspace.State.Devices[paired.DeviceId].Data is not null;
        }, "synced");

        // 对端消失:读永远挂着,没有任何错误、没有 FIN。心跳的 ping 和看门狗都挂在手动时钟上。
        streams[0].BlackHole();
        await clock.WaitForTimersAsync(2);
        clock.Advance(TimeSpan.FromSeconds(59.9));
        Assert.True(ConnectionIs(paired.DeviceId, DeviceConnection.Connected)); // 3 个间隔还没满:此刻不能提前判死
        lock (streams) Assert.Single(streams); // 还是同一条连接

        clock.Advance(TimeSpan.FromSeconds(0.1));

        // 规则 7:断开后标「连接中」(不是结束、不是离线),设备的数据状态变未知(不沿用旧值),并自动重连到一条新连接上。
        // (历史里一开始就有一个 Connecting:Start 之后、连上之前。要找的是「已连接」之后的那一次。)
        int DropIndex() => history.FindIndex(history.FindIndex(h => h.Connection == DeviceConnection.Connected) + 1, h => h.Connection != DeviceConnection.Connected);
        await WaitUntil(() => { lock (_ui.Gate) return DropIndex() >= 0; }, "the heartbeat dropped the silent connection");
        lock (_ui.Gate)
        {
            var dropped = DropIndex();
            Assert.Equal(DeviceConnection.Connecting, history[dropped].Connection);
            Assert.False(history[dropped].HasData);
        }

        await WaitUntil(() => { lock (streams) return streams.Count == 2; }, "reconnected over a new connection");
        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "connected again");
        // 「已连接」先于 data 主题的首个快照写进 Store(SyncAsync 先标 Connected,再订阅):数据状态要等重新同步到达。
        await WaitUntil(() => { lock (_ui.Gate) return _stores.Workspace.State.Devices[paired.DeviceId].Data is not null; }, "data state re-synced");
        Assert.True(streams[0].IsDisposed); // 死连接被关掉了
        Assert.False(streams[1].IsDisposed);
        lock (_ui.Gate)
        {
            Assert.DoesNotContain(history, h => h.Connection is DeviceConnection.Offline or DeviceConnection.Revoked);
        }
    }

    [Fact]
    public async Task A_revoked_controller_is_marked_revoked_by_its_device_link_and_stops_retrying()
    {
        await UseRelayAsync();
        var paired = await PairAsync();
        await WaitUntil(() => _relay.TokenCount(_agent.DeviceId) == 1, "token registered");
        await ManagerOverRelayAsync(paired);
        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "connected");

        await _local.CallAsync(DeviceMethods.TrustRevoke, new TrustRevokeParams(_controller.FingerprintHex));

        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Revoked), "marked revoked (relay says the token is void)");
        var sessionsAtRevoke = _relay.Metrics.SessionsStarted;
        await Task.Delay(1500);
        Assert.Equal(sessionsAtRevoke, _relay.Metrics.SessionsStarted); // 不再重试
    }

    [Fact]
    public async Task A_relay_configured_after_pairing_is_learned_over_the_lan_so_the_saved_device_gains_the_route()
    {
        // 固定一个端口:关掉再打开远程开关后设备仍在同一个地址上监听(记录里的局域网地址不变)。
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await _local.CallAsync(DeviceMethods.RemoteConfigure, new RemoteConfigureParams(port, null));
        Assert.Equal(port, _agent.Remote.Port);

        // 配对时设备还没有中继:记录里没有 relay。
        var paired = await PairAsync();
        Assert.Null(paired.Relay);

        var saved = new SavedDevice(paired.DeviceId, "Studio", Convert.ToHexStringLower(paired.DeviceFingerprint), "127.0.0.1", _agent.Remote.Port!.Value, paired.Role, paired.RouteToken, 0);
        var book = new MemoryDeviceBook();
        book.Save([saved]);
        var manager = new RemoteDeviceManager(_stores, _ui, new FixedIdentity(_controller), book, "Phone");
        _disposables.Add(manager);
        manager.Start();
        await WaitUntil(() => ConnectionIs(paired.DeviceId, DeviceConnection.Connected), "connected over the lan");
        Assert.Null(book.Saved.Single().Relay);

        // 设备主人后来配置了中继,并重新打开远程开关(局域网连接断开重连)。
        await UseRelayAsync();
        await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(false));
        await _local.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));

        await WaitUntil(() => book.Saved.Single().Relay == RelayUrl, "relay address learned from device.info and saved");
        Assert.Equal(paired.RouteToken, book.Saved.Single().RouteToken); // 令牌不变:老的配对记录直接可用
    }
}
