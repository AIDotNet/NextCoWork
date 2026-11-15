using System.Security.Cryptography;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;

namespace Ncw.Relay.Tests;

/// <summary>中继的纯逻辑单元测试(不起服务器):限流、并发闸门、粘性路由、配置、注册签名、地址校验。</summary>
public sealed class UnitTests
{
    // ---- 限流 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Rate_limiter_allows_the_limit_then_refuses_until_the_window_slides()
    {
        var time = new ManualTime();
        var limiter = new RateLimiter(new RateRule(3, TimeSpan.FromSeconds(10)), time);

        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.False(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("b")); // 各 key 独立

        time.Advance(TimeSpan.FromSeconds(25)); // 过了两个窗口:全部过期
        Assert.True(limiter.TryAcquire("a"));
    }

    [Fact]
    public void Rate_limiter_weights_the_previous_window_so_a_burst_at_the_boundary_is_not_free()
    {
        var time = new ManualTime();
        var limiter = new RateLimiter(new RateRule(10, TimeSpan.FromSeconds(10)), time);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire("k"));
        }

        time.Advance(TimeSpan.FromSeconds(11)); // 刚进入下一个窗口:上一窗口的 10 次还占约 90% 的权重,只剩零头名额
        var allowed = 0;
        while (limiter.TryAcquire("k"))
        {
            allowed++;
        }

        Assert.InRange(allowed, 1, 2);
        time.Advance(TimeSpan.FromSeconds(9)); // 上一窗口的权重已降到约 0
        Assert.True(limiter.TryAcquire("k"));
    }

    [Fact]
    public void Failure_counting_checks_before_recording()
    {
        var time = new ManualTime();
        var limiter = new RateLimiter(new RateRule(2, TimeSpan.FromMinutes(1)), time);
        Assert.False(limiter.IsExceeded("ip"));
        limiter.Record("ip");
        Assert.False(limiter.IsExceeded("ip"));
        limiter.Record("ip");
        Assert.True(limiter.IsExceeded("ip"));
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.False(limiter.IsExceeded("ip"));
    }

    [Fact]
    public void Sweep_forgets_idle_keys()
    {
        var time = new ManualTime();
        var limiter = new RateLimiter(new RateRule(5, TimeSpan.FromSeconds(1)), time);
        for (var i = 0; i < 100; i++)
        {
            limiter.TryAcquire("k" + i);
        }

        Assert.Equal(100, limiter.Tracked);
        time.Advance(TimeSpan.FromSeconds(5));
        limiter.Sweep();
        Assert.Equal(0, limiter.Tracked);
    }

    [Fact]
    public void Concurrency_gate_admits_up_to_the_limit_and_releases()
    {
        var gate = new ConcurrencyGate(2);
        var a = gate.TryEnter("ip");
        var b = gate.TryEnter("ip");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(gate.TryEnter("ip"));
        Assert.NotNull(gate.TryEnter("other"));

        a!.Dispose();
        a.Dispose(); // 重复释放不会多还一个名额
        var c = gate.TryEnter("ip");
        Assert.NotNull(c);
        Assert.Null(gate.TryEnter("ip"));
        b!.Dispose();
        c!.Dispose();
        Assert.Equal(1, gate.Tracked); // 只剩 "other"
    }

    // ---- 粘性路由 -----------------------------------------------------------------------------------------------

    [Fact]
    public void Rendezvous_placement_is_deterministic_and_agrees_across_instances()
    {
        string[] all = ["https://relay-a", "https://relay-b", "https://relay-c"];
        var views = all.Select(self => new RendezvousPlacement(self, all)).ToArray();
        for (var i = 0; i < 200; i++)
        {
            var id = PeerIdentity.DeviceIdOf(RandomNumberGenerator.GetBytes(32));
            var owners = views.Select(v => v.Locate(id)).ToArray();
            Assert.Single(owners, o => o.IsLocal); // 恰好一个实例认领
            var owner = all[Array.FindIndex(owners, o => o.IsLocal)];
            Assert.All(owners.Where(o => !o.IsLocal), o => Assert.Equal(owner, o.OwnerUrl)); // 其余实例指向同一个
        }
    }

    [Fact]
    public void Rendezvous_placement_moves_only_the_devices_of_a_removed_instance()
    {
        string[] three = ["https://relay-a", "https://relay-b", "https://relay-c"];
        string[] two = ["https://relay-a", "https://relay-b"];
        var before = new RendezvousPlacement("https://relay-a", three);
        var after = new RendezvousPlacement("https://relay-a", two);
        var moved = 0;
        var ownedByA = 0;
        for (var i = 0; i < 500; i++)
        {
            var id = PeerIdentity.DeviceIdOf(RandomNumberGenerator.GetBytes(32));
            var was = before.Locate(id);
            var now = after.Locate(id);
            if (was.IsLocal)
            {
                ownedByA++;
                Assert.True(now.IsLocal); // A 原来的设备不会因为 C 下线而挪走
            }
            else if (was.OwnerUrl != "https://relay-c")
            {
                Assert.Equal(was.OwnerUrl, now.OwnerUrl); // B 的也不动
            }
            else
            {
                moved++; // 只有 C 的设备迁移
            }
        }

        Assert.InRange(ownedByA, 100, 250); // 大致均匀(1/3)
        Assert.True(moved > 0);
    }

    [Fact]
    public void Rendezvous_placement_requires_self_in_the_instance_list() =>
        Assert.Throws<ArgumentException>(() => new RendezvousPlacement("https://x", ["https://a", "https://b"]));

    // ---- 配置 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Config_reads_environment_variables_and_rejects_garbage()
    {
        var env = new Dictionary<string, string>
        {
            ["NCW_RELAY_URLS"] = "http://0.0.0.0:9000; http://[::]:9001",
            ["NCW_RELAY_IDLE_SECONDS"] = "45",
            ["NCW_RELAY_MAX_SESSIONS_PER_DEVICE"] = "4",
            ["NCW_RELAY_MAX_MB_PER_SEC"] = "2",
            ["NCW_RELAY_TRUST_FORWARDED_FOR"] = "true",
            ["NCW_RELAY_SELF"] = "https://a",
            ["NCW_RELAY_INSTANCES"] = "https://a;https://b",
        };
        var options = RelayConfig.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(["http://0.0.0.0:9000", "http://[::]:9001"], options.Urls);
        Assert.Equal(TimeSpan.FromSeconds(45), options.IdleTimeout);
        Assert.Equal(4, options.MaxSessionsPerDevice);
        Assert.Equal(2L * 1024 * 1024, options.MaxBytesPerSecondPerSession);
        Assert.True(options.TrustForwardedFor);
        Assert.IsType<RendezvousPlacement>(options.Placement);

        var defaults = RelayConfig.FromEnvironment(_ => null);
        Assert.Equal(16, defaults.MaxSessionsPerDevice);
        Assert.False(defaults.TrustForwardedFor);
        Assert.False(defaults.MetricsPublic);

        Assert.Throws<FormatException>(() => RelayConfig.FromEnvironment(k => k == "NCW_RELAY_IDLE_SECONDS" ? "soon" : null));
        Assert.Throws<FormatException>(() => RelayConfig.FromEnvironment(k => k == "NCW_RELAY_TRUST_FORWARDED_FOR" ? "yes" : null));
        Assert.Throws<FormatException>(() => RelayConfig.FromEnvironment(k => k == "NCW_RELAY_INSTANCES" ? "https://a" : null));
    }

    // ---- 注册签名(现成的 .NET ECDSA)-----------------------------------------------------------------------------

    [Fact]
    public void Registration_signature_verifies_only_for_the_right_key_nonce_and_device_id()
    {
        var identity = PeerIdentity.Create("dev");
        var nonce = RandomNumberGenerator.GetBytes(RelayProtocol.NonceBytes);
        var spki = RelayProtocol.PublicKeyInfo(identity);
        var signature = RelayProtocol.Sign(identity, nonce);

        Assert.True(RelayProtocol.VerifyRegistration(spki, signature, nonce, identity.DeviceId));

        Assert.False(RelayProtocol.VerifyRegistration(spki, signature, RandomNumberGenerator.GetBytes(32), identity.DeviceId)); // 别的挑战(重放)
        var other = PeerIdentity.Create("other");
        Assert.False(RelayProtocol.VerifyRegistration(spki, signature, nonce, other.DeviceId)); // deviceId 不是这把公钥派生的
        Assert.False(RelayProtocol.VerifyRegistration(RelayProtocol.PublicKeyInfo(other), signature, nonce, identity.DeviceId)); // 换了公钥
        Assert.False(RelayProtocol.VerifyRegistration(RelayProtocol.PublicKeyInfo(other), RelayProtocol.Sign(other, nonce), nonce, identity.DeviceId)); // 别人拿自己的密钥冒充
        var flipped = (byte[])signature.Clone();
        flipped[10] ^= 1;
        Assert.False(RelayProtocol.VerifyRegistration(spki, flipped, nonce, identity.DeviceId));
        Assert.False(RelayProtocol.VerifyRegistration([1, 2, 3], signature, nonce, identity.DeviceId)); // 垃圾公钥:返回 false,不抛
        Assert.False(RelayProtocol.VerifyRegistration(spki, [], nonce, identity.DeviceId));
    }

    [Fact]
    public void Device_id_format_is_checked_before_anything_else()
    {
        Assert.True(RelayProtocol.IsValidDeviceId(PeerIdentity.Create("x").DeviceId));
        Assert.False(RelayProtocol.IsValidDeviceId(null));
        Assert.False(RelayProtocol.IsValidDeviceId(""));
        Assert.False(RelayProtocol.IsValidDeviceId("dev_" + new string('a', 25)));
        Assert.False(RelayProtocol.IsValidDeviceId("dev_" + new string('A', 26)));
        Assert.False(RelayProtocol.IsValidDeviceId("dex_" + new string('a', 26)));
        Assert.False(RelayProtocol.IsValidDeviceId("dev_" + new string('a', 25) + "/"));
    }

    [Fact]
    public void Token_hash_is_one_way_and_stable()
    {
        var token = Tokens.New();
        var hash = RelayProtocol.HashToken(token);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, RelayProtocol.HashToken(token));
        Assert.DoesNotContain(token, hash);
        Assert.NotEqual(hash, RelayProtocol.HashToken(Tokens.New()));
        // 和设备库里存的哈希是同一个函数的结果(RemoteService 用它):令牌 → SHA-256(ASCII) → 小写十六进制
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token))), hash);
    }

    // ---- 地址 ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("wss://relay.nextco.work", true, "wss://relay.nextco.work")]
    [InlineData("wss://relay.nextco.work/", true, "wss://relay.nextco.work")]
    [InlineData("wss://relay.nextco.work:8443/eu/", true, "wss://relay.nextco.work:8443/eu")]
    [InlineData("ws://127.0.0.1:5000", true, "ws://127.0.0.1:5000")]
    [InlineData("ws://localhost:5000", true, "ws://localhost:5000")]
    [InlineData("ws://192.168.1.5:5000", true, "ws://192.168.1.5:5000")]
    [InlineData("ws://relay.example.com", false, null)] // 明文只给本机 / 局域网
    [InlineData("ws://8.8.8.8", false, null)]
    [InlineData("https://relay.nextco.work", false, null)]
    [InlineData("wss://user:pw@relay.nextco.work", false, null)]
    [InlineData("wss://relay.nextco.work/?x=1", false, null)]
    [InlineData("wss://relay.nextco.work/#f", false, null)]
    [InlineData("relay.nextco.work", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void Relay_address_normalization(string? text, bool ok, string? expected)
    {
        Assert.Equal(ok, RelayAddress.TryNormalize(text, out var url));
        if (ok)
        {
            Assert.Equal(expected, RelayAddress.ToText(url));
        }
    }

    // ---- 令牌集合 -----------------------------------------------------------------------------------------------

    [Fact]
    public void Entry_matches_tokens_in_a_set_and_updates_by_set_add_remove()
    {
        var metrics = new RelayMetrics();
        var entry = new DeviceEntry("dev_x");
        byte[] H(string t) => Convert.FromHexString(Tokens.Hash(t));
        var (a, b, c) = (Tokens.New(), Tokens.New(), Tokens.New());

        entry.UpdateTokens([H(a), H(b)], [], [], metrics);
        Assert.Equal(2, entry.TokenCount);
        Assert.True(entry.Matches(H(a)));
        Assert.True(entry.Matches(H(b)));
        Assert.False(entry.Matches(H(c)));
        Assert.False(entry.Matches(new byte[32]));

        entry.UpdateTokens(null, [H(c)], [H(a)], metrics);
        Assert.False(entry.Matches(H(a)));
        Assert.True(entry.Matches(H(c)));
        Assert.Equal(2, entry.TokenCount);

        entry.UpdateTokens([], [], [], metrics); // 整体替换成空:全部作废
        Assert.Equal(0, entry.TokenCount);
        Assert.False(entry.Matches(H(b)));
    }

    [Fact]
    public void Dropping_a_token_cancels_the_sessions_that_used_it()
    {
        var metrics = new RelayMetrics();
        var entry = new DeviceEntry("dev_x");
        var (a, b) = (Tokens.New(), Tokens.New());
        entry.UpdateTokens([Convert.FromHexString(Tokens.Hash(a)), Convert.FromHexString(Tokens.Hash(b))], [], [], metrics);
        var sa = entry.TryAddSession("s1", Tokens.Hash(a), "1.1.1.1", 16)!;
        var sb = entry.TryAddSession("s2", Tokens.Hash(b), "1.1.1.1", 16)!;

        var revoked = entry.UpdateTokens(null, [], [Convert.FromHexString(Tokens.Hash(a))], metrics);

        Assert.Equal(1, revoked);
        Assert.True(sa.Cancel.IsCancellationRequested);
        Assert.Equal(RelayErrors.Revoked, sa.CancelReason);
        Assert.False(sb.Cancel.IsCancellationRequested);
        Assert.Equal(1, metrics.RevocationsClosed);
    }

    [Fact]
    public void Session_slots_are_capped_per_device()
    {
        var entry = new DeviceEntry("dev_x");
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(entry.TryAddSession("s" + i, "h", "ip", 3));
        }

        Assert.Null(entry.TryAddSession("s3", "h", "ip", 3));
        entry.RemoveSession("s1");
        Assert.NotNull(entry.TryAddSession("s3", "h", "ip", 3));
    }
}
