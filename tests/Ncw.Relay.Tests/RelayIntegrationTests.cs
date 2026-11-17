using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;

namespace Ncw.Relay.Tests;

/// <summary>
/// 中继的集成测试:进程内 Kestrel + 真实的设备侧(<see cref="RelayRegistration"/>)和控制端侧(<see cref="RelayTransport"/>)。
/// 覆盖设备注册、挑战签名、令牌校验、离线、并发会话、限流、背压、空闲超时、吊销、重启重注册、日志与指标卫生、密文。
/// </summary>
public sealed class RelayIntegrationTests
{
    private static RelayRoute Route(RelayServer relay, string token) => new(relay.WebSocketUrl, token);

    private static Task<Stream> ConnectAsync(RelayServer relay, string deviceId, string token, CancellationToken ct = default) =>
        RelayTransport.ConnectAsync(Route(relay, token), deviceId, ct);

    private static async Task<RelayServer> RelayWith(Func<RelayOptions, RelayOptions>? _ = null, LogCapture? log = null, RelayOptions? options = null, int port = 0) =>
        await TestRelay.StartAsync(options, log, port);

    private static async Task<FakeDevice> OnlineDevice(RelayServer relay, params string[] tokens)
    {
        var device = new FakeDevice(relay.WebSocketUrl, tokenHashes: [.. tokens.Select(Tokens.Hash)]);
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(device.DeviceId) && device.Status.State == RelayConnectionState.Registered, "device registered");
        return device;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, int timeoutMs = 20_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task<RelayException> RefusedAsync(RelayServer relay, string deviceId, string token) =>
        await Assert.ThrowsAsync<RelayException>(() => ConnectAsync(relay, deviceId, token));

    // ---- 注册与路由 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_registered_device_is_reachable_and_bytes_round_trip_intact()
    {
        await using var relay = await RelayWith();
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);
        Assert.Equal(1, relay.TokenCount(device.DeviceId));
        Assert.Equal(1, relay.DevicesOnline);

        await using var stream = await ConnectAsync(relay, device.DeviceId, token);
        var payload = RandomNumberGenerator.GetBytes(1024 * 1024 + 123);
        var write = Task.Run(async () =>
        {
            await stream.WriteAsync(payload);
        });
        var echoed = await ReadExactlyAsync(stream, payload.Length);
        await write;

        Assert.True(payload.AsSpan().SequenceEqual(echoed));
        await Wait.UntilAsync(() => relay.Metrics.BytesToDevice >= payload.Length && relay.Metrics.BytesToController >= payload.Length, "metrics");
        Assert.Equal(1, relay.Metrics.SessionsStarted);
    }

    [Fact]
    public async Task Registration_with_a_bad_signature_is_refused_and_the_device_stays_offline()
    {
        await using var relay = await RelayWith();
        var identity = PeerIdentity.Create("victim");
        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        var challenge = await raw.ReceiveAsync();
        Assert.Equal("challenge", challenge!.Type);

        await raw.SendAsync(new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(identity)),
            Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)), // 不是签名
            Tokens = [Tokens.Hash(Tokens.New())],
        });

        var error = await raw.ReceiveAsync();
        Assert.Equal("error", error!.Type);
        Assert.Equal(RelayErrors.Unauthorized, error.Code);
        Assert.False(relay.IsOnline(identity.DeviceId));
        Assert.Equal(1, relay.Metrics.RegistrationFailures);
    }

    [Fact]
    public async Task An_attacker_cannot_register_under_someone_elses_device_id()
    {
        await using var relay = await RelayWith();
        var victim = PeerIdentity.Create("victim");
        var attacker = PeerIdentity.Create("attacker");
        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, victim.DeviceId); // 声称是受害者
        var nonce = Convert.FromBase64String((await raw.ReceiveAsync())!.Nonce!);

        // 攻击者用自己的密钥正确地签了名,公钥也是自己的:但 deviceId 不是这把公钥派生的。
        await raw.SendAsync(new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(attacker)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(attacker, nonce)),
            Tokens = [],
        });

        Assert.Equal(RelayErrors.Unauthorized, (await raw.ReceiveAsync())!.Code);
        Assert.False(relay.IsOnline(victim.DeviceId));
    }

    [Fact]
    public async Task A_signature_cannot_be_replayed_on_another_connection()
    {
        await using var relay = await RelayWith();
        var identity = PeerIdentity.Create("dev");

        // 连接一:设备正常注册,窃听者记下了它的注册消息。
        await using var first = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        var nonce1 = Convert.FromBase64String((await first.ReceiveAsync())!.Nonce!);
        var register = new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(identity)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(identity, nonce1)),
            Tokens = [],
        };
        await first.SendAsync(register);
        Assert.Equal("registered", (await first.ReceiveAsync())!.Type);

        // 连接二:原样重放同一条注册消息;挑战不同,签名对不上。
        await using var replay = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        var nonce2 = Convert.FromBase64String((await replay.ReceiveAsync())!.Nonce!);
        Assert.NotEqual(nonce1, nonce2);
        await replay.SendAsync(register);
        Assert.Equal(RelayErrors.Unauthorized, (await replay.ReceiveAsync())!.Code);
    }

    [Theory]
    [InlineData("noversion")]
    [InlineData("badtoken")]
    [InlineData("toomany")]
    [InlineData("notregister")]
    public async Task Malformed_registration_is_refused_with_bad_request(string kind)
    {
        await using var relay = await RelayWith();
        var identity = PeerIdentity.Create("dev");
        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        var nonce = Convert.FromBase64String((await raw.ReceiveAsync())!.Nonce!);
        var good = new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(identity)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(identity, nonce)),
            Tokens = [],
        };
        await raw.SendAsync(kind switch
        {
            "noversion" => good with { V = null },
            "badtoken" => good with { Tokens = ["not-hex"] },
            "toomany" => good with { Tokens = [.. Enumerable.Range(0, RelayProtocol.MaxTokensPerDevice + 1).Select(_ => Tokens.Hash(Tokens.New()))] },
            _ => good with { Type = "hello" },
        });

        Assert.Equal(RelayErrors.BadRequest, (await raw.ReceiveAsync())!.Code);
        Assert.False(relay.IsOnline(identity.DeviceId));
    }

    [Fact]
    public async Task A_silent_registration_is_dropped_after_the_handshake_timeout()
    {
        await using var relay = await RelayWith(options: new RelayOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        var identity = PeerIdentity.Create("dev");
        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        await raw.ReceiveAsync(); // 挑战
        var error = await raw.ReceiveAsync(); // 不发 register:中继关掉连接
        Assert.Equal(RelayErrors.BadRequest, error!.Code);
        Assert.False(relay.IsOnline(identity.DeviceId));
    }

    [Fact]
    public async Task Garbage_device_ids_and_plain_http_are_rejected_before_any_work()
    {
        await using var relay = await RelayWith();
        var e = await Assert.ThrowsAsync<RelayException>(() => RelayTransport.ConnectAsync(Route(relay, "x"), "not-a-device", CancellationToken.None));
        Assert.Equal(RelayErrors.BadRequest, e.Code);

        using var http = new HttpClient { BaseAddress = new Uri(relay.Urls[0]) };
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await http.GetAsync("/connect?d=" + PeerIdentity.Create("x").DeviceId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/nope")).StatusCode);
    }

    [Fact]
    public async Task Wrong_token_is_unauthorized_and_an_offline_device_is_reported_as_offline()
    {
        await using var relay = await RelayWith();
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);

        Assert.Equal(RelayErrors.Unauthorized, (await RefusedAsync(relay, device.DeviceId, Tokens.New())).Code);
        Assert.Equal(RelayErrors.Unauthorized, (await RefusedAsync(relay, device.DeviceId, Tokens.Hash(token))).Code); // 出示哈希也不行:必须是令牌本身
        Assert.Equal(RelayErrors.DeviceOffline, (await RefusedAsync(relay, PeerIdentity.Create("nobody").DeviceId, token)).Code);
        Assert.Equal(0, relay.SessionCount(device.DeviceId));

        await using var ok = await ConnectAsync(relay, device.DeviceId, token); // 正确的令牌照常通
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task Malformed_connect_messages_are_bad_requests()
    {
        await using var relay = await RelayWith();
        await using var device = await OnlineDevice(relay, Tokens.New());
        foreach (var token in new[] { "", new string('a', 200), "has space", "控制" })
        {
            var (socket, first) = await RawController.OpenAsync(relay.WebSocketUrl, device.DeviceId, token);
            using (socket)
            {
                Assert.Equal(RelayErrors.BadRequest, first!.Code);
            }
        }
    }

    // ---- 并发与限制 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sixteen_controllers_can_share_a_device_and_the_seventeenth_is_busy()
    {
        await using var relay = await RelayWith(options: new RelayOptions { MaxBytesPerSecondPerSession = 0, ConnectsPerIp = new(10_000, TimeSpan.FromMinutes(1)), ConnectsPerDevice = new(10_000, TimeSpan.FromMinutes(1)) });
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);

        var streams = new List<Stream>();
        for (var i = 0; i < 16; i++)
        {
            streams.Add(await ConnectAsync(relay, device.DeviceId, token));
        }

        Assert.Equal(16, relay.SessionCount(device.DeviceId));
        // 16 条会话各自独立:每条发自己的数据,只收到自己的回显。
        var payloads = streams.Select((_, i) => RandomNumberGenerator.GetBytes(4096 + i)).ToArray();
        await Task.WhenAll(streams.Select((s, i) => s.WriteAsync(payloads[i]).AsTask()));
        for (var i = 0; i < streams.Count; i++)
        {
            var echoed = await ReadExactlyAsync(streams[i], payloads[i].Length);
            Assert.True(payloads[i].AsSpan().SequenceEqual(echoed));
        }

        Assert.Equal(RelayErrors.Busy, (await RefusedAsync(relay, device.DeviceId, token)).Code);

        await streams[3].DisposeAsync(); // 释放一个名额
        await Wait.UntilAsync(() => relay.SessionCount(device.DeviceId) == 15, "slot freed");
        await using var again = await ConnectAsync(relay, device.DeviceId, token);
        Assert.NotNull(again);
        foreach (var s in streams)
        {
            await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task One_ip_that_keeps_failing_is_locked_out_even_with_the_right_token()
    {
        var time = new ManualTime();
        await using var relay = await RelayWith(options: new RelayOptions { FailuresPerIp = new(3, TimeSpan.FromMinutes(1)), Clock = time });
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RelayErrors.Unauthorized, (await RefusedAsync(relay, device.DeviceId, Tokens.New())).Code); // 暴力尝试令牌
        }

        // 限制先于比较:现在连正确的令牌也被拒绝,暴力尝试没有「试到对为止」的机会。
        Assert.Equal(RelayErrors.RateLimited, (await RefusedAsync(relay, device.DeviceId, token)).Code);
        Assert.True(relay.Metrics.Rejected(RelayErrors.RateLimited) >= 1);

        time.Advance(TimeSpan.FromMinutes(3)); // 窗口过去,解除
        await using var ok = await ConnectAsync(relay, device.DeviceId, token);
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task Connection_rate_per_ip_and_per_device_is_limited()
    {
        await using (var relay = await RelayWith(options: new RelayOptions { ConnectsPerIp = new(4, TimeSpan.FromMinutes(1)), Clock = new ManualTime() }))
        {
            var token = Tokens.New();
            await using var device = await OnlineDevice(relay, token); // 注册占 1 个
            for (var i = 0; i < 3; i++)
            {
                await using var s = await ConnectAsync(relay, device.DeviceId, token);
            }

            Assert.Equal(RelayErrors.RateLimited, (await RefusedAsync(relay, device.DeviceId, token)).Code);
        }

        await using (var relay = await RelayWith(options: new RelayOptions { ConnectsPerDevice = new(2, TimeSpan.FromMinutes(1)), Clock = new ManualTime() }))
        {
            var token = Tokens.New();
            await using var device = await OnlineDevice(relay, token);
            await using (await ConnectAsync(relay, device.DeviceId, token))
            await using (await ConnectAsync(relay, device.DeviceId, token))
            {
                Assert.Equal(RelayErrors.RateLimited, (await RefusedAsync(relay, device.DeviceId, token)).Code);
            }
        }
    }

    [Fact]
    public async Task Open_connections_per_ip_and_registrations_per_ip_are_limited()
    {
        await using var relay = await RelayWith(options: new RelayOptions { MaxConnectionsPerIp = 2, RegistersPerIp = new(100, TimeSpan.FromMinutes(1)) });
        var a = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, PeerIdentity.Create("a").DeviceId);
        var b = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, PeerIdentity.Create("b").DeviceId);
        await a.ReceiveAsync();
        await b.ReceiveAsync();
        var refused = await Assert.ThrowsAsync<WebSocketException>(() => RawDeviceConnection.OpenAsync(relay.WebSocketUrl, PeerIdentity.Create("c").DeviceId));
        Assert.Contains("429", refused.Message);
        await a.DisposeAsync();
        await b.DisposeAsync();

        await using var relay2 = await RelayWith(options: new RelayOptions { RegistersPerIp = new(2, TimeSpan.FromMinutes(1)), Clock = new ManualTime() });
        for (var i = 0; i < 2; i++)
        {
            await using var c = await RawDeviceConnection.OpenAsync(relay2.WebSocketUrl, PeerIdentity.Create("r" + i).DeviceId);
            await c.ReceiveAsync();
        }

        var third = await Assert.ThrowsAsync<WebSocketException>(() => RawDeviceConnection.OpenAsync(relay2.WebSocketUrl, PeerIdentity.Create("r3").DeviceId));
        Assert.Contains("429", third.Message);
    }

    [Fact]
    public async Task A_device_that_floods_control_messages_is_cut_off()
    {
        await using var relay = await RelayWith(options: new RelayOptions { ControlMessagesPerDevice = new(3, TimeSpan.FromMinutes(1)), Clock = new ManualTime() });
        var identity = PeerIdentity.Create("dev");
        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId);
        var nonce = Convert.FromBase64String((await raw.ReceiveAsync())!.Nonce!);
        await raw.SendAsync(new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(identity)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(identity, nonce)),
            Tokens = [],
        });
        Assert.Equal("registered", (await raw.ReceiveAsync())!.Type);

        // 超过上限后中继发 rate_limited 并关闭连接(发送方可能先看到错误消息,也可能先看到连接被关)。
        for (var i = 0; i < 20 && relay.IsOnline(identity.DeviceId); i++)
        {
            try
            {
                await raw.SendAsync(new RelayMessage("tokens") { Add = [Tokens.Hash(Tokens.New())] });
            }
            catch (WebSocketException)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(1, relay.Metrics.Rejected(RelayErrors.RateLimited));
        await Wait.UntilAsync(() => !relay.IsOnline(identity.DeviceId), "device cut off");
    }

    // ---- 背压、大小、空闲、带宽 ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_stalled_reader_stops_the_sender_instead_of_filling_relay_memory()
    {
        await using var relay = await RelayWith(options: new RelayOptions { MaxBytesPerSecondPerSession = 0, IdleTimeout = TimeSpan.FromMinutes(5) });
        var token = Tokens.New();
        var release = new TaskCompletionSource();
        long received = 0;
        await using var device = new FakeDevice(relay.WebSocketUrl, tokenHashes: [Tokens.Hash(token)], onSession: async s =>
        {
            await using var stream = s.Stream;
            await release.Task; // 设备「卡住」:不读
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                Interlocked.Add(ref received, n);
            }
        });
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(device.DeviceId), "online");

        await using var stream = await ConnectAsync(relay, device.DeviceId, token);
        const int total = 128 * 1024 * 1024;
        var chunk = new byte[32 * 1024];
        var sender = Task.Run(async () =>
        {
            for (var sent = 0; sent < total; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk);
            }
        });

        await Task.Delay(1500);
        Assert.False(sender.IsCompleted); // 被背压挡住
        var forwarded = relay.Metrics.BytesToDevice;
        Assert.InRange(forwarded, 0, 40 * 1024 * 1024); // 中继转发出去的有上限(只有 TCP 缓冲那么多),不是 128 MiB
        await Task.Delay(500);
        // 没有继续涨:中继没有在内存里囤数据。Windows 的 TCP 发送 / 接收缓冲会自动调大,卡住之后还会再吞进去一两百 KiB(CI 上 196608 → 327680),
        // 所以比的是「这半秒里只多了内核缓冲那么一点」,不是逐字节相等。
        Assert.InRange(relay.Metrics.BytesToDevice - forwarded, 0, 4 * 1024 * 1024);

        release.SetResult(); // 设备恢复读取:发送方继续,直到全部到达
        await sender.WaitAsync(TimeSpan.FromSeconds(60));
        await Wait.UntilAsync(() => Interlocked.Read(ref received) == total, "all bytes delivered", 30_000); // 先等全部到达再断开:断开是中止,在途的字节会丢
    }

    [Fact]
    public async Task An_oversized_message_closes_the_session_with_too_large()
    {
        var log = new LogCapture();
        await using var relay = await RelayWith(log: log, options: new RelayOptions { MaxMessageBytes = 1024 });
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);

        var (socket, first) = await RawController.OpenAsync(relay.WebSocketUrl, device.DeviceId, token);
        using (socket)
        {
            Assert.Equal("ready", first!.Type);
            await socket.SendAsync(new byte[4096], WebSocketMessageType.Binary, true, CancellationToken.None);
            await Wait.UntilAsync(() => relay.SessionCount(device.DeviceId) == 0, "session closed");
            await Wait.UntilAsync(() => log.All.Contains("reason=too_large"), "logged reason");
        }
    }

    [Fact]
    public async Task Text_messages_after_ready_are_a_protocol_violation()
    {
        var log = new LogCapture();
        await using var relay = await RelayWith(log: log);
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);

        var (socket, _) = await RawController.OpenAsync(relay.WebSocketUrl, device.DeviceId, token);
        using (socket)
        {
            await socket.SendAsync("{\"type\":\"connect\"}"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await Wait.UntilAsync(() => relay.SessionCount(device.DeviceId) == 0, "session closed");
            await Wait.UntilAsync(() => log.All.Contains("reason=bad_request"), "logged reason");
        }
    }

    [Fact]
    public async Task An_idle_session_is_closed_but_traffic_keeps_it_alive()
    {
        await using var relay = await RelayWith(options: new RelayOptions { IdleTimeout = TimeSpan.FromMilliseconds(600) });
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);

        await using (var busy = await ConnectAsync(relay, device.DeviceId, token))
        {
            for (var i = 0; i < 10; i++)
            {
                await busy.WriteAsync(new byte[] { 1 });
                Assert.Single(await ReadExactlyAsync(busy, 1));
                await Task.Delay(150); // 一共 1.5 秒,远超空闲时限,但一直有数据
            }

            Assert.Equal(1, relay.SessionCount(device.DeviceId));

            var read = busy.ReadAsync(new byte[1]).AsTask();
            // 不再有数据:空闲超时后连接被关闭(读到 EOF 或连接断开)
            try
            {
                Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            catch (IOException)
            {
            }
        }

        await Wait.UntilAsync(() => relay.Metrics.IdleClosed == 1 && relay.SessionCount(device.DeviceId) == 0, "idle close counted");
        Assert.Equal(0, relay.Metrics.SessionsActive);
    }

    [Fact]
    public async Task Per_session_bandwidth_is_capped()
    {
        await using var relay = await RelayWith(options: new RelayOptions { MaxBytesPerSecondPerSession = 128 * 1024 });
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);
        await using var stream = await ConnectAsync(relay, device.DeviceId, token);

        var payload = RandomNumberGenerator.GetBytes(256 * 1024); // 去 + 回 = 512 KiB 配额,每方向合计
        var started = Environment.TickCount64;
        var write = Task.Run(() => stream.WriteAsync(payload).AsTask());
        var echoed = await ReadExactlyAsync(stream, payload.Length, 60_000);
        await write;
        var elapsed = Environment.TickCount64 - started;

        Assert.True(payload.AsSpan().SequenceEqual(echoed));
        Assert.True(elapsed >= 2500, $"512 KiB at 128 KiB/s should take about 4 s, took {elapsed} ms");
    }

    // ---- 设备与会话的生命周期 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_token_cuts_its_live_sessions_and_blocks_new_ones_but_not_other_tokens()
    {
        await using var relay = await RelayWith();
        var (revoked, kept) = (Tokens.New(), Tokens.New());
        await using var device = await OnlineDevice(relay, revoked, kept);

        await using var doomed = await ConnectAsync(relay, device.DeviceId, revoked);
        await using var survivor = await ConnectAsync(relay, device.DeviceId, kept);
        await doomed.WriteAsync(new byte[] { 7 });
        await ReadExactlyAsync(doomed, 1);

        device.SetTokens(Tokens.Hash(kept)); // 吊销:令牌哈希从设备推给中继的集合里消失

        var read = doomed.ReadAsync(new byte[1]).AsTask();
        try
        {
            Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        catch (IOException)
        {
        }

        Assert.Equal(RelayErrors.Unauthorized, (await RefusedAsync(relay, device.DeviceId, revoked)).Code);
        Assert.Equal(1, relay.TokenCount(device.DeviceId));
        Assert.Equal(1, relay.Metrics.RevocationsClosed);

        await survivor.WriteAsync(new byte[] { 9 }); // 另一个令牌的会话不受影响
        Assert.Equal(9, (await ReadExactlyAsync(survivor, 1))[0]);

        device.SetTokens(Tokens.Hash(kept), Tokens.Hash(revoked)); // 重新登记(例如重新配对):立即可用
        await Wait.UntilAsync(() => relay.TokenCount(device.DeviceId) == 2, "token re-added");
        await using var again = await ConnectAsync(relay, device.DeviceId, revoked);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task A_new_registration_replaces_the_old_one_without_dropping_live_sessions()
    {
        await using var relay = await RelayWith();
        var token = Tokens.New();
        var identity = PeerIdentity.Create("dev");
        await using var device = new FakeDevice(relay.WebSocketUrl, identity, [Tokens.Hash(token)]);
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(identity.DeviceId), "online");
        await using var live = await ConnectAsync(relay, identity.DeviceId, token);

        await using var raw = await RawDeviceConnection.OpenAsync(relay.WebSocketUrl, identity.DeviceId); // 同一把密钥另起一条注册
        var nonce = Convert.FromBase64String((await raw.ReceiveAsync())!.Nonce!);
        await raw.SendAsync(new RelayMessage("register")
        {
            V = 1,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(identity)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(identity, nonce)),
            Tokens = [Tokens.Hash(token)],
        });
        Assert.Equal("registered", (await raw.ReceiveAsync())!.Type);

        await live.WriteAsync(new byte[] { 5 }); // 已有的会话继续通
        Assert.Equal(5, (await ReadExactlyAsync(live, 1))[0]);
        Assert.True(relay.IsOnline(identity.DeviceId));
        Assert.Equal(1, relay.DevicesOnline);
    }

    [Fact]
    public async Task A_device_that_never_attaches_makes_the_controller_time_out_and_frees_the_slot()
    {
        await using var relay = await RelayWith(options: new RelayOptions { DeviceAcceptTimeout = TimeSpan.FromMilliseconds(400) });
        var identity = PeerIdentity.Create("slow");
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

        var error = await RefusedAsync(relay, identity.DeviceId, token);
        Assert.Equal(RelayErrors.Timeout, error.Code);
        var notice = await raw.ReceiveAsync(); // 设备确实被通知了,只是没来
        Assert.Equal("session", notice!.Type);
        await Wait.UntilAsync(() => relay.SessionCount(identity.DeviceId) == 0, "slot freed");
    }

    [Fact]
    public async Task Session_ids_are_single_use_and_wrong_ones_are_refused()
    {
        await using var relay = await RelayWith();
        var identity = PeerIdentity.Create("dev");
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
        await raw.ReceiveAsync();

        var controller = ConnectAsync(relay, identity.DeviceId, token);
        var notice = (await raw.ReceiveAsync())!;
        Assert.Equal(Tokens.Hash(token), notice.TokenHash); // 设备收到的是令牌哈希,不是令牌

        Uri Data(string session) => new($"{relay.WebSocketUrl.ToString().TrimEnd('/')}{RelayProtocol.DataPath}?d={identity.DeviceId}&s={session}");

        using var guess = new ClientWebSocket();
        var wrong = await Assert.ThrowsAsync<WebSocketException>(() => guess.ConnectAsync(Data(new string('0', 32)), CancellationToken.None));
        Assert.Contains("403", wrong.Message);

        using var first = new ClientWebSocket();
        await first.ConnectAsync(Data(notice.Session!), CancellationToken.None);
        using var second = new ClientWebSocket();
        var reused = await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(Data(notice.Session!), CancellationToken.None));
        Assert.Contains("409", reused.Message);

        await using var stream = await controller;
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task Controller_sessions_end_when_the_device_connection_drops_and_the_slot_is_released()
    {
        await using var relay = await RelayWith();
        var token = Tokens.New();
        Stream? deviceSide = null;
        await using var device = new FakeDevice(relay.WebSocketUrl, tokenHashes: [Tokens.Hash(token)], onSession: async s =>
        {
            deviceSide = s.Stream;
            await Task.Delay(Timeout.Infinite);
        });
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(device.DeviceId), "online");
        await using var stream = await ConnectAsync(relay, device.DeviceId, token);
        await Wait.UntilAsync(() => deviceSide is not null, "device attached");

        await deviceSide!.DisposeAsync(); // 设备那一端突然断开

        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        catch (IOException)
        {
        }

        await Wait.UntilAsync(() => relay.SessionCount(device.DeviceId) == 0 && relay.Metrics.SessionsActive == 0, "session cleaned up");
    }

    [Fact]
    public async Task Restarting_the_relay_makes_the_device_re_register_by_itself()
    {
        var token = Tokens.New();
        var relay1 = await RelayWith();
        var port = relay1.WebSocketUrl.Port;
        await using var device = new FakeDevice(relay1.WebSocketUrl, tokenHashes: [Tokens.Hash(token)]);
        device.Start();
        await Wait.UntilAsync(() => relay1.IsOnline(device.DeviceId), "online at first relay");

        await relay1.DisposeAsync(); // 中继重启:内存状态全丢
        await Wait.UntilAsync(() => device.Status.State != RelayConnectionState.Registered, "device noticed");

        await using var relay2 = await RelayWith(port: port); // 同一个地址上起新的实例
        await Wait.UntilAsync(() => relay2.IsOnline(device.DeviceId) && device.Status.State == RelayConnectionState.Registered, "re-registered", 20_000);
        Assert.Equal(1, relay2.TokenCount(device.DeviceId)); // 令牌哈希也随注册一起带回来了

        await using var stream = await ConnectAsync(relay2, device.DeviceId, token);
        await stream.WriteAsync(new byte[] { 42 });
        Assert.Equal(42, (await ReadExactlyAsync(stream, 1))[0]);
    }

    [Fact]
    public async Task A_device_the_relay_does_not_own_gets_misdirected_with_the_owner_address()
    {
        var elsewhere = new RendezvousPlacement("https://relay-a.example", ["https://relay-a.example", "https://relay-b.example"]);
        // 挑一个不归 relay-a 的设备 id。
        string deviceId;
        do
        {
            deviceId = PeerIdentity.DeviceIdOf(RandomNumberGenerator.GetBytes(32));
        }
        while (elsewhere.Locate(deviceId).IsLocal);

        await using var relay = await RelayWith(options: new RelayOptions { Placement = elsewhere });
        var error = await RefusedAsync(relay, deviceId, "token");
        Assert.Equal(RelayErrors.Misdirected, error.Code);

        // 负载均衡器看得到的 HTTP 层:421 + 归属实例的地址。
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{relay.Urls[0]}/connect?d={deviceId}");
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new System.Net.Http.Headers.ProductHeaderValue("websocket"));
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Equal("https://relay-b.example", Assert.Single(response.Headers.GetValues("X-Ncw-Relay-Owner")));
        Assert.Equal(2, relay.Metrics.Rejected(RelayErrors.Misdirected)); // RelayTransport 那次 + 这一次
    }

    // ---- 观测:健康、指标、日志 ---------------------------------------------------------------------------------

    [Fact]
    public async Task Health_and_metrics_endpoints_report_state()
    {
        await using var relay = await RelayWith();
        var token = Tokens.New();
        await using var device = await OnlineDevice(relay, token);
        await using (await ConnectAsync(relay, device.DeviceId, token))
        {
            await RefusedAsync(relay, device.DeviceId, Tokens.New());
        }

        using var http = new HttpClient { BaseAddress = new Uri(relay.Urls[0]) };
        Assert.Equal("ok", await http.GetStringAsync("/healthz"));
        var metrics = await http.GetStringAsync("/metrics");
        Assert.Contains("ncw_relay_devices_online 1", metrics);
        Assert.Contains("ncw_relay_registrations_total 1", metrics);
        Assert.Contains("ncw_relay_sessions_total 1", metrics);
        Assert.Contains("ncw_relay_rejected_total{reason=\"unauthorized\"} 1", metrics);
        Assert.DoesNotContain(token, metrics);
        Assert.DoesNotContain(device.DeviceId, metrics); // 指标里没有任何设备标识
    }

    [Fact]
    public async Task Logs_are_structured_and_never_contain_tokens_hashes_or_content()
    {
        var log = new LogCapture();
        await using var relay = await RelayWith(log: log);
        var token = Tokens.New();
        var bad = Tokens.New();
        await using var device = await OnlineDevice(relay, token);
        var marker = "PLAINTEXT-MARKER-" + Guid.NewGuid().ToString("N");
        await using (var stream = await ConnectAsync(relay, device.DeviceId, token))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(marker));
            await ReadExactlyAsync(stream, marker.Length);
        }

        await RefusedAsync(relay, device.DeviceId, bad);
        device.SetTokens();
        await Wait.UntilAsync(() => relay.TokenCount(device.DeviceId) == 0, "tokens cleared");
        await Wait.UntilAsync(() => log.All.Contains("session ended"), "session end logged");

        var all = log.All;
        Assert.Contains("device registered", all);
        Assert.Contains(device.DeviceId, all); // 设备 id 是公开标识,允许
        Assert.Contains("session started", all);
        Assert.Contains("reason=unauthorized", all);
        foreach (var secret in new[] { token, bad, Tokens.Hash(token), Tokens.Hash(bad), marker })
        {
            Assert.DoesNotContain(secret, all);
        }

        Assert.DoesNotContain("tokenHash", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kestrel_can_terminate_tls_itself_when_a_certificate_is_configured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncw-relay-cert-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=relay.test", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllTextAsync(Path.Combine(dir, "cert.pem"), cert.ExportCertificatePem());
            await File.WriteAllTextAsync(Path.Combine(dir, "key.pem"), key.ExportPkcs8PrivateKeyPem());

            await using var relay = await RelayServer.StartAsync(new RelayOptions
            {
                Urls = ["https://127.0.0.1:0"],
                CertificatePath = Path.Combine(dir, "cert.pem"),
                CertificateKeyPath = Path.Combine(dir, "key.pem"),
                ConfigureLogging = logging => logging.ClearProviders(),
            });

            Assert.StartsWith("https://", relay.Urls[0]);
            Assert.Equal("wss", relay.WebSocketUrl.Scheme);
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Subject == "CN=relay.test" });
            Assert.Equal("ok", await http.GetStringAsync(relay.Urls[0] + "/healthz"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- 端到端密文 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_relay_only_sees_ciphertext_of_an_end_to_end_tls_session()
    {
        var recorder = new TrafficRecorder();
        await using var relay = await RelayWith(options: new RelayOptions { Tap = recorder.Tap });
        var deviceKey = PeerIdentity.Create("secret-device-name");
        var controllerKey = PeerIdentity.Create("secret-controller-name");
        var token = Tokens.New();
        var marker = "TOP-SECRET-PLAINTEXT-" + Guid.NewGuid().ToString("N");

        await using var device = new FakeDevice(relay.WebSocketUrl, deviceKey, [Tokens.Hash(token)], onSession: async s =>
        {
            await using var channel = await SecureChannel.AcceptAsync(s.Stream, deviceKey, CancellationToken.None);
            var buffer = new byte[marker.Length];
            await channel.Stream.ReadExactlyAsync(buffer);
            await channel.Stream.WriteAsync(buffer); // 回显
            await Task.Delay(200);
        });
        device.Start();
        await Wait.UntilAsync(() => relay.IsOnline(device.DeviceId), "online");

        var raw = await ConnectAsync(relay, device.DeviceId, token);
        await using var channel = await SecureChannel.ConnectAsync(raw, controllerKey, deviceKey.Fingerprint, CancellationToken.None);
        Assert.Equal(deviceKey.Fingerprint, channel.PeerFingerprint); // 经中继,对端身份仍由证书保证
        await channel.Stream.WriteAsync(Encoding.ASCII.GetBytes(marker));
        var echoed = new byte[marker.Length];
        await channel.Stream.ReadExactlyAsync(echoed);
        Assert.Equal(marker, Encoding.ASCII.GetString(echoed));

        Assert.True(recorder.All.Length > 1000, "the relay carried the handshake and the data");
        Assert.False(recorder.Contains(Encoding.ASCII.GetBytes(marker)), "plaintext leaked through the relay");
        Assert.False(recorder.Contains(Encoding.ASCII.GetBytes("secret-device-name")), "device certificate leaked in the clear");
        Assert.False(recorder.Contains(Encoding.ASCII.GetBytes("secret-controller-name")), "controller certificate leaked in the clear");
        Assert.False(recorder.Contains(deviceKey.Fingerprint), "device fingerprint visible to the relay");
        Assert.False(recorder.Contains(controllerKey.Fingerprint), "controller fingerprint visible to the relay");
    }

    // ---- 恶意中继 -----------------------------------------------------------------------------------------------

    /// <summary>一个恶意中继:接受控制端的接入,但不连真设备,自己用别的证书当「设备」做 TLS(只会在控制端握手这一步露馅)。</summary>
    private sealed class EvilRelay : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly PeerIdentity _impostor = PeerIdentity.Create("impostor");
        private readonly Task _loop;
        private readonly TaskCompletionSource<Exception?> _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EvilRelay(int port)
        {
            Url = new Uri($"ws://127.0.0.1:{port}");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(RunAsync);
        }

        public Uri Url { get; }

        public PeerIdentity Impostor => _impostor;

        /// <summary>冒名者那一侧的 TLS 握手结果(null = 握手成功,也就是控制端没有钉扎)。</summary>
        public Task<Exception?> Handshake => _handshake.Task;

        private async Task RunAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var buffer = new byte[4096];
                await ws.ReceiveAsync(buffer, CancellationToken.None); // connect 消息(令牌就是这样交给恶意中继的)
                await ws.SendAsync(Encoding.UTF8.GetBytes(RelayProtocol.Serialize(new RelayMessage("ready"))), WebSocketMessageType.Text, true, CancellationToken.None);
                try
                {
                    await using var channel = await SecureChannel.AcceptAsync(new Ncw.Protocol.Transport.WebSocketStream(ws), _impostor, CancellationToken.None);
                    _handshake.TrySetResult(null);
                }
                catch (Exception e)
                {
                    _handshake.TrySetResult(e);
                }
            }
            catch (Exception e)
            {
                _handshake.TrySetResult(e);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Abort();
            await _loop;
        }
    }

    [Fact]
    public async Task A_malicious_relay_that_swaps_in_its_own_certificate_fails_the_controllers_pinned_handshake()
    {
        var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var free = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        await using var evil = new EvilRelay(free);
        var realDevice = PeerIdentity.Create("real-device");
        var controller = PeerIdentity.Create("controller");

        var stream = await RelayTransport.ConnectAsync(new RelayRoute(evil.Url, Tokens.New()), realDevice.DeviceId, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<SecureChannelException>(() => SecureChannel.ConnectAsync(stream, controller, realDevice.Fingerprint, CancellationToken.None));

        Assert.Contains("TLS handshake failed", failure.Message);
        // 冒名者那边的握手也没有成功:控制端在看到假证书时就中止了,没有向它出示任何应用数据。
        Assert.NotNull(await evil.Handshake.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_relay_that_bridges_two_separate_tls_sessions_cannot_read_or_forward_the_conversation()
    {
        // 恶意中继「两头各做一次 TLS」(经典中间人):对控制端冒充设备,对设备冒充控制端。控制端钉扎了设备指纹,第一段就失败。
        var real = PeerIdentity.Create("real-device");
        var controller = PeerIdentity.Create("controller");
        var impostor = PeerIdentity.Create("impostor");
        var (toController, controllerEnd) = await StreamPair();
        var mitm = SecureChannel.AcceptAsync(toController, impostor, CancellationToken.None);

        var attempt = Assert.ThrowsAsync<SecureChannelException>(() => SecureChannel.ConnectAsync(controllerEnd, controller, real.Fingerprint, CancellationToken.None));
        await attempt;
        await Assert.ThrowsAnyAsync<Exception>(() => mitm);
    }

    private static async Task<(Stream A, Stream B)> StreamPair()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        return ((await accept).GetStream(), client.GetStream());
    }
}
