using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Cloud;
using Ncw.Agent.Cloud.Tests.Support;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud.Tests;

/// <summary>启动配置的装载与校验(docs/design/02 A6):来源、校验、失败时只有稳定 key 和字段名、绝不带值。</summary>
public sealed class ConfigTests
{
    private const string Token = "tok-SECRET-0123456789";
    private static readonly Issuer Issuer = new();

    private static string ConfigJson(Action<JsonObject>? edit = null)
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var doc = new JsonObject
        {
            ["dataRoot"] = Path.Combine(root, "ncw-cfg-data"),
            ["workspacesRoot"] = Path.Combine(root, "ncw-cfg-ws"),
            ["orchestrationUrl"] = "https://orchestrator.example.com/api",
            ["relayUrl"] = "wss://relay.example.com",
            ["orchestratorKeys"] = new JsonArray(new JsonObject { ["kid"] = Issuer.Kid, ["key"] = Issuer.PublicKey }),
            ["kms"] = new JsonObject { ["provider"] = "dev-env", ["context"] = "acct-1/sandbox-1" },
        };
        edit?.Invoke(doc);
        return doc.ToJsonString();
    }

    private static CloudConfig Load(string json, Dictionary<string, string?>? env = null, Func<string, string>? readFile = null, bool looseMode = false)
    {
        env ??= [];
        env.TryAdd(CloudConfigLoader.ConfigEnv, "/etc/ncw/cloud.json");
        if (!env.ContainsKey(CloudConfigLoader.TokenFileEnv))
        {
            env.TryAdd(CloudConfigLoader.TokenEnv, Token);
        }

        return CloudConfigLoader.Load(name => env.GetValueOrDefault(name), readFile ?? (_ => json), _ => looseMode);
    }

    private static CloudStartupException Fails(string json, Dictionary<string, string?>? env = null, bool looseMode = false) =>
        Assert.Throws<CloudStartupException>(() => Load(json, env, looseMode: looseMode));

    [Fact]
    public void A_valid_configuration_loads_with_defaults_and_the_token_from_the_environment()
    {
        var config = Load(ConfigJson());

        Assert.Equal("orchestrator.example.com", config.OrchestrationUrl.Host);
        Assert.EndsWith("/api/", config.OrchestrationUrl.AbsolutePath); // 结尾带 /,相对路径才能正确拼接
        Assert.Equal("relay.example.com", config.RelayUrl.Host);
        Assert.Equal(TimeSpan.FromMinutes(15), config.IdleAfter);
        Assert.Equal(1, config.Keys.Count);
        Assert.Equal(Token, config.AccountToken);
        Assert.Equal("dev-env", config.Kms.Provider);
    }

    [Fact]
    public void The_account_token_can_come_from_a_secret_file_and_the_loaded_config_never_prints_secrets()
    {
        var config = Load(ConfigJson(), new Dictionary<string, string?> { [CloudConfigLoader.TokenFileEnv] = "/run/secrets/token" }, readFile: p => p.EndsWith("token", StringComparison.Ordinal) ? $"  {Token}\n" : ConfigJson());

        Assert.Equal(Token, config.AccountToken);
        Assert.DoesNotContain(Token, config.ToString());
        Assert.DoesNotContain(Token, new AccountTokenHolder(Token).ToString());
    }

    [Fact]
    public void A_missing_configuration_path_or_token_is_a_structured_failure_with_the_config_exit_code()
    {
        var noPath = Assert.Throws<CloudStartupException>(() => CloudConfigLoader.Load(_ => null, _ => ""));
        Assert.Equal((CloudStartupCodes.ConfigMissing, CloudConfigLoader.ConfigEnv, CloudExitCodes.Config), (noPath.Code, noPath.Field, noPath.ExitCode));

        var noToken = Assert.Throws<CloudStartupException>(() =>
            CloudConfigLoader.Load(n => n == CloudConfigLoader.ConfigEnv ? "/c.json" : null, _ => ConfigJson(), _ => false));
        Assert.Equal(CloudStartupCodes.SecretMissing, noToken.Code);
    }

    [Theory]
    [InlineData("dataRoot", null, CloudStartupCodes.ConfigMissing)]
    [InlineData("dataRoot", "relative/path", CloudStartupCodes.ConfigInvalid)]
    [InlineData("orchestrationUrl", "http://orchestrator.example.com", CloudStartupCodes.ConfigInvalid)] // 账户令牌在这条连接上:必须 TLS
    [InlineData("orchestrationUrl", "https://user:pass@orchestrator.example.com", CloudStartupCodes.ConfigInvalid)]
    [InlineData("relayUrl", "ws://relay.example.com", CloudStartupCodes.ConfigInvalid)] // ws:// 只允许本机 / 局域网
    [InlineData("expectedDeviceId", "not-a-device", CloudStartupCodes.ConfigInvalid)]
    [InlineData("kms", null, CloudStartupCodes.ConfigMissing)]
    public void Invalid_fields_are_rejected_and_named_without_echoing_the_value(string field, string? value, string code)
    {
        var json = ConfigJson(doc =>
        {
            if (value is null)
            {
                doc.Remove(field);
            }
            else
            {
                doc[field] = value;
            }
        });

        var error = Fails(json);

        Assert.Equal(code, error.Code);
        Assert.Equal(field, error.Field);
        if (value is { Length: > 4 })
        {
            Assert.DoesNotContain(value, error.Message);
        }
    }

    [Fact]
    public void An_unknown_field_is_an_error_not_silently_ignored()
    {
        var error = Fails(ConfigJson(doc => doc["dataRooot"] = "/typo"));

        Assert.Equal(CloudStartupCodes.ConfigInvalid, error.Code);
        Assert.DoesNotContain("/typo", error.Message);
    }

    [Fact]
    public void A_configuration_file_writable_by_others_is_refused_and_a_loopback_http_orchestrator_is_allowed_for_development()
    {
        Assert.Equal(CloudStartupCodes.ConfigInsecure, Fails(ConfigJson(), looseMode: true).Code);

        var dev = Load(ConfigJson(doc => doc["orchestrationUrl"] = "http://127.0.0.1:9000"));
        Assert.Equal("127.0.0.1", dev.OrchestrationUrl.Host);
    }

    [Fact]
    public void Keys_from_the_environment_override_the_file_and_a_bad_key_voids_the_whole_setting()
    {
        var other = new Issuer("orch-2");
        var config = Load(ConfigJson(), new Dictionary<string, string?> { [CloudConfigLoader.KeysEnv] = $"orch-2:{other.PublicKey}" });
        Assert.Equal(["orch-2"], config.Keys.KeyIds);

        var bad = Fails(ConfigJson(), new Dictionary<string, string?> { [CloudConfigLoader.KeysEnv] = $"orch-2:{other.PublicKey},broken:AAAA" });
        Assert.Equal(CloudConfigLoader.KeysEnv, bad.Field);
    }

    [Fact]
    public void Idle_settings_are_range_checked()
    {
        var error = Fails(ConfigJson(doc => doc["idle"] = new JsonObject { ["idleMinutes"] = 0 }));
        Assert.Equal("idle.idleMinutes", error.Field);
    }
}

/// <summary>编排指令的验签、防重放、有效期、设备绑定和指令泵的串行 / 回执 / 持久化语义。</summary>
public sealed class CommandTests
{
    private const string Device = "dev_abcdefghjkmnpqrstvwxyz0123";
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
    private readonly Issuer _issuer = new();
    private readonly ManualClock _clock = new(T0);

    private CommandVerifier Verifier(string device = Device) => new(_issuer.Keys, device, _clock);

    private SignedEnvelope Command(long seq, string type = CommandTypes.QuotaSet, string device = Device, TimeSpan? lifetime = null) =>
        _issuer.Signer.Sign(_issuer.Body(type, device, seq, T0, lifetime) with { Quota = new QuotaLimits(DailyRuns: 5) });

    [Fact]
    public void A_correctly_signed_command_for_this_device_verifies()
    {
        var check = Verifier().Verify(Command(1));

        Assert.True(check.Ok);
        Assert.Equal(1, check.Body!.Seq);
        Assert.Equal(5, check.Body.Quota!.DailyRuns);
    }

    [Fact]
    public void A_signature_from_another_key_is_rejected_even_with_a_known_key_id()
    {
        var imposter = new Issuer(_issuer.Kid); // 同一个 kid,不同的密钥
        var forged = imposter.Signer.Sign(imposter.Body(CommandTypes.QuotaSet, Device, 1, T0));

        Assert.Equal(RejectCodes.BadSignature, Verifier().Verify(forged).RejectCode);
    }

    [Fact]
    public void An_unknown_key_id_a_tampered_payload_and_a_garbage_envelope_are_rejected()
    {
        var other = new Issuer("someone-else");
        Assert.Equal(RejectCodes.UnknownKey, Verifier().Verify(other.Signer.Sign(other.Body(CommandTypes.QuotaSet, Device, 1, T0))).RejectCode);

        var good = Command(1);
        var body = Convert.FromBase64String(good.Payload);
        body[^2] ^= 0x01;
        Assert.Equal(RejectCodes.BadSignature, Verifier().Verify(good with { Payload = Convert.ToBase64String(body) }).RejectCode);

        Assert.Equal(RejectCodes.BadFormat, Verifier().Verify(new SignedEnvelope(1, "orch-1", "!!!", "???")).RejectCode);
        Assert.Equal(RejectCodes.BadFormat, Verifier().Verify(null).RejectCode);
        Assert.Equal(RejectCodes.BadFormat, Verifier().Verify(good with { V = 99 }).RejectCode);
    }

    [Fact]
    public void A_command_addressed_to_another_device_is_rejected_so_it_cannot_be_replayed_elsewhere()
    {
        var forOther = Command(1, device: "dev_zzzzzzzzzzzzzzzzzzzzzzzzzz");

        Assert.Equal(RejectCodes.WrongDevice, Verifier().Verify(forOther).RejectCode);
    }

    [Fact]
    public void Expired_and_future_dated_commands_are_rejected_within_the_clock_skew()
    {
        var shortLived = Command(1, lifetime: TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(Verifier().Verify(shortLived).Ok); // 偏差容忍:休眠唤醒后时钟可能不准

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(RejectCodes.Expired, Verifier().Verify(shortLived).RejectCode);

        var future = _issuer.Signer.Sign(_issuer.Body(CommandTypes.QuotaSet, Device, 2, _clock.GetUtcNow() + TimeSpan.FromHours(1)));
        Assert.Equal(RejectCodes.NotYetValid, Verifier().Verify(future).RejectCode);
    }

    [Fact]
    public void An_identity_binding_cannot_pass_as_a_command_and_vice_versa()
    {
        var binding = _issuer.Signer.Sign(new DeviceBinding(Device, new string('a', 64), "acct-1", "wss://relay.example.com", 1));

        Assert.Equal(RejectCodes.BadSignature, Verifier().Verify(binding).RejectCode); // 域分隔
        Assert.NotNull(Verifier().OpenBinding(binding));
        Assert.Null(Verifier().OpenBinding(Command(1)));
        Assert.Null(Verifier("dev_zzzzzzzzzzzzzzzzzzzzzzzzzz").OpenBinding(binding)); // 别的设备的绑定
    }

    [Fact]
    public void The_replay_guard_accepts_each_sequence_number_once_and_is_race_free()
    {
        var guard = new ReplayGuard(10);

        Assert.False(guard.TryAdvance(10));
        Assert.False(guard.TryAdvance(3));
        Assert.True(guard.TryAdvance(11));
        Assert.False(guard.TryAdvance(11));

        var winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (guard.TryAdvance(500))
            {
                Interlocked.Increment(ref winners);
            }
        });
        Assert.Equal(1, winners);
        Assert.Equal(500, guard.Last);
    }

    private sealed class Handler : ICommandHandler
    {
        public List<CommandBody> Applied { get; } = [];

        public Func<CommandBody, CommandOutcome> Outcome { get; set; } = _ => CommandOutcome.Applied;

        public Task<CommandOutcome> ApplyAsync(CommandBody command, CancellationToken ct)
        {
            Applied.Add(command);
            return Task.FromResult(Outcome(command));
        }
    }

    private sealed class Channel : IOrchestrationChannel
    {
        public List<CommandAck> Acks { get; } = [];

        public bool FailAcks { get; set; }

        public Task<HelloResponse> HelloAsync(AgentHello hello, CancellationToken ct) => throw new NotSupportedException();

        public Task<SignedEnvelope[]> PollCommandsAsync(long afterSeq, TimeSpan wait, CancellationToken ct) => throw new NotSupportedException();

        public Task AckAsync(CommandAck ack, CancellationToken ct)
        {
            if (FailAcks)
            {
                throw new OrchestrationException("down", true);
            }

            Acks.Add(ack);
            return Task.CompletedTask;
        }

        public Task<AckResponse> ReportUsageAsync(UsageBatch batch, CancellationToken ct) => throw new NotSupportedException();

        public Task NotifyLifecycleAsync(LifecycleNotice notice, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_pump_applies_in_sequence_order_acks_each_and_turns_a_replay_into_duplicate_without_applying_it()
    {
        var store = new MemoryCloudStore();
        var handler = new Handler();
        var channel = new Channel();
        var pump = new CommandPump(Verifier(), store, handler, channel);

        await pump.ProcessAsync([Command(3), Command(1), Command(2)], default); // 乱序到达
        Assert.Equal([1L, 2, 3], handler.Applied.Select(c => c.Seq));
        Assert.All(channel.Acks, a => Assert.Equal(AckStatuses.Applied, a.Status));

        var outcomes = await pump.ProcessAsync([Command(2), Command(3)], default); // 重放
        Assert.All(outcomes, o => Assert.Equal(AckStatuses.Duplicate, o.Status));
        Assert.Equal(3, handler.Applied.Count);
        Assert.Equal(AckStatuses.Duplicate, channel.Acks[^1].Status);
        Assert.Equal(3, pump.LastSeq);
    }

    [Fact]
    public async Task The_sequence_number_survives_a_restart_so_a_captured_command_cannot_be_replayed_after_wake_up()
    {
        var store = new MemoryCloudStore();
        var first = new CommandPump(Verifier(), store, new Handler(), new Channel());
        await first.ProcessAsync([Command(7)], default);

        var handler = new Handler();
        var woken = new CommandPump(Verifier(), store, handler, new Channel()); // 「重启」后的新实例
        var outcomes = await woken.ProcessAsync([Command(7), Command(6)], default);

        Assert.All(outcomes, o => Assert.Equal(AckStatuses.Duplicate, o.Status));
        Assert.Empty(handler.Applied);
        Assert.Equal(7, woken.LastSeq);
    }

    [Fact]
    public async Task Rejected_commands_do_not_consume_the_sequence_number_and_are_acked_with_the_reason()
    {
        var store = new MemoryCloudStore();
        var handler = new Handler();
        var channel = new Channel();
        var pump = new CommandPump(Verifier(), store, handler, channel);
        var imposter = new Issuer(_issuer.Kid);
        var forged = imposter.Signer.Sign(imposter.Body(CommandTypes.QuotaSet, Device, 5, T0));

        var outcomes = await pump.ProcessAsync([forged], default);

        Assert.Equal(RejectCodes.BadSignature, outcomes.Single().Code);
        Assert.Empty(handler.Applied);
        Assert.Equal(0, pump.LastSeq); // 伪造的高序号不能把真指令挡在门外
        Assert.Equal(AckStatuses.Applied, (await pump.ProcessAsync([Command(5)], default)).Single().Status);
    }

    [Fact]
    public async Task A_busy_outcome_rewinds_the_sequence_so_the_same_command_can_be_retried_and_a_lost_ack_is_harmless()
    {
        var store = new MemoryCloudStore();
        var handler = new Handler { Outcome = _ => CommandOutcome.Busy("busy") };
        var channel = new Channel { FailAcks = true };
        var pump = new CommandPump(Verifier(), store, handler, channel);

        var first = await pump.ProcessAsync([Command(4)], default);
        Assert.Equal(AckStatuses.Busy, first.Single().Status);
        Assert.Equal(0, pump.LastSeq);
        Assert.Equal(0, store.ReadLong(CloudKeys.CommandSeq));

        handler.Outcome = _ => CommandOutcome.Applied;
        var again = await pump.ProcessAsync([Command(4)], default); // 回执丢了也不影响:状态已经应用
        Assert.Equal(AckStatuses.Applied, again.Single().Status);
        Assert.Equal(4, pump.LastSeq);
    }

    [Fact]
    public async Task A_handler_that_throws_is_reported_as_invalid_and_does_not_stop_the_pump()
    {
        var handler = new Handler { Outcome = c => c.Seq == 1 ? throw new InvalidOperationException("boom") : CommandOutcome.Applied };
        var channel = new Channel();
        var pump = new CommandPump(Verifier(), new MemoryCloudStore(), handler, channel);

        var outcomes = await pump.ProcessAsync([Command(1), Command(2)], default);

        Assert.Equal([RejectCodes.Invalid, null], outcomes.Select(o => o.Code));
        Assert.Equal(AckStatuses.Applied, outcomes[1].Status);
    }

    [Fact]
    public void The_device_proof_binds_the_device_id_to_the_key_and_to_this_boot()
    {
        var identity = Ncw.Protocol.Security.PeerIdentity.Create("t");
        var spki = Convert.ToBase64String(Ncw.Protocol.Transport.RelayProtocol.PublicKeyInfo(identity));
        var hello = new AgentHello(1, identity.DeviceId, spki, Convert.ToBase64String(DeviceProof.Sign(identity, 123, "boot1")), 123, "3.0", "boot1", false, 0);

        Assert.True(DeviceProof.Verify(hello));
        Assert.False(DeviceProof.Verify(hello with { BootId = "boot2" })); // 换一次启动签名就不对:不能重放旧注册
        Assert.False(DeviceProof.Verify(hello with { DeviceId = "dev_zzzzzzzzzzzzzzzzzzzzzzzzzz" }));
        Assert.False(DeviceProof.Verify(hello with { Signature = "AAAA" }));
    }
}

/// <summary>配额与用量(02 A6、05 B9):发起前检查、窗口、持久化、记账与退款、计算时长。</summary>
public sealed class QuotaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 22, 0, 0, TimeSpan.Zero);

    private (QuotaService Quota, FakeUsage Usage, ManualClock Clock, MemoryCloudStore Store) Rig(QuotaLimits? limits = null, MemoryCloudStore? store = null, ManualClock? clock = null)
    {
        store ??= new MemoryCloudStore();
        clock ??= new ManualClock(T0);
        var usage = new FakeUsage();
        var quota = new QuotaService(store, usage, clock);
        if (limits is not null)
        {
            quota.Set(limits);
        }

        return (quota, usage, clock, store);
    }

    [Fact]
    public void Without_limits_everything_is_allowed()
    {
        var (quota, _, _, _) = Rig();

        Assert.NotNull(quota.Admit(out var verdict));
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void The_daily_run_limit_admits_exactly_that_many_runs_then_reports_the_window_and_the_reset_time()
    {
        var (quota, _, clock, _) = Rig(new QuotaLimits(DailyRuns: 2));

        Assert.NotNull(quota.Admit(out _));
        Assert.NotNull(quota.Admit(out _));
        var third = quota.Admit(out var verdict);

        Assert.Null(third);
        Assert.False(verdict.Allowed);
        Assert.Equal(QuotaWindows.DailyRuns, verdict.Window);
        Assert.Equal((2, 2), (verdict.Limit, verdict.Used));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), verdict.ResetsAt);

        clock.Advance(TimeSpan.FromHours(3)); // 跨过 UTC 午夜:日窗口重置
        Assert.NotNull(quota.Admit(out _));
    }

    [Fact]
    public void The_monthly_window_survives_the_day_roll_and_resets_at_the_month_boundary()
    {
        var (quota, _, clock, _) = Rig(new QuotaLimits(MonthlyRuns: 3));
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(quota.Admit(out _));
            clock.Advance(TimeSpan.FromHours(30));
        }

        Assert.Null(quota.Admit(out var verdict));
        Assert.Equal(QuotaWindows.MonthlyRuns, verdict.Window);
        clock.Advance(TimeSpan.FromDays(20)); // 12 月
        Assert.NotNull(quota.Admit(out _));
    }

    [Fact]
    public void Tokens_concurrency_compute_storage_and_frozen_are_checked_in_order()
    {
        var (quota, usage, _, _) = Rig(new QuotaLimits(DailyTokens: 100, MaxConcurrentRuns: 1, StorageBytes: 1000));

        usage.Tokens = 100;
        Assert.Equal(QuotaWindows.DailyTokens, quota.Check().Window);
        usage.Tokens = 0;
        usage.Active = 1;
        Assert.Equal(QuotaWindows.ConcurrentRuns, quota.Check().Window);
        usage.Active = 0;
        usage.Storage = 1000;
        Assert.Equal(QuotaWindows.Storage, quota.Check().Window);
        usage.Storage = 999;
        Assert.True(quota.Check().Allowed);

        quota.Set(new QuotaLimits(Frozen: true));
        Assert.Equal(QuotaWindows.Frozen, quota.Check().Window);
    }

    [Fact]
    public void A_refund_gives_the_slot_back_once()
    {
        var (quota, _, _, _) = Rig(new QuotaLimits(DailyRuns: 1));
        var ticket = quota.Admit(out _)!;

        Assert.Null(quota.Admit(out _));
        ticket.Refund();
        ticket.Refund(); // 幂等
        Assert.NotNull(quota.Admit(out _));
        Assert.Null(quota.Admit(out _));
    }

    [Fact]
    public void Limits_and_the_ledger_persist_across_a_restart_and_the_sleep_gap_is_not_counted_as_compute_time()
    {
        var (quota, _, clock, store) = Rig(new QuotaLimits(DailyRuns: 5, MonthlyComputeSeconds: 600));
        quota.Admit(out _);
        clock.Advance(TimeSpan.FromSeconds(100));
        quota.Meter();
        Assert.Equal(100, quota.Ledger.MonthComputeSeconds);

        // 休眠两小时后醒来:新进程,同一份存储。休眠期间的时间不计入计算时长。
        var wakeClock = new ManualClock(T0 + TimeSpan.FromHours(2));
        var woken = new QuotaService(store, new FakeUsage(), wakeClock);
        wakeClock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(5, woken.Limits.DailyRuns);
        Assert.Equal(1, woken.Ledger.CumRuns);
        Assert.Equal(130, woken.Ledger.MonthComputeSeconds);
    }

    [Fact]
    public void Compute_seconds_are_a_limit_too()
    {
        var (quota, _, clock, _) = Rig(new QuotaLimits(MonthlyComputeSeconds: 60));
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(QuotaWindows.MonthlyCompute, quota.Check().Window);
    }
}

/// <summary>用量上报(B9):批量、可重试、幂等。</summary>
public sealed class UsageReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    private sealed class Channel : IOrchestrationChannel
    {
        public List<UsageBatch> Received { get; } = [];

        public int Failures { get; set; }

        public bool AckDuplicate { get; set; }

        public Task<HelloResponse> HelloAsync(AgentHello hello, CancellationToken ct) => throw new NotSupportedException();

        public Task<SignedEnvelope[]> PollCommandsAsync(long afterSeq, TimeSpan wait, CancellationToken ct) => throw new NotSupportedException();

        public Task AckAsync(CommandAck ack, CancellationToken ct) => throw new NotSupportedException();

        public Task<AckResponse> ReportUsageAsync(UsageBatch batch, CancellationToken ct)
        {
            if (Failures > 0)
            {
                Failures--;
                throw new OrchestrationException("503", true, 503);
            }

            Received.Add(batch);
            return Task.FromResult(new AckResponse(true, AckDuplicate));
        }

        public Task NotifyLifecycleAsync(LifecycleNotice notice, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (UsageReporter Reporter, QuotaService Quota, FakeUsage Usage, ManualClock Clock, Channel Channel, MemoryCloudStore Store) Rig(MemoryCloudStore? store = null)
    {
        store ??= new MemoryCloudStore();
        var clock = new ManualClock(T0);
        var usage = new FakeUsage();
        var quota = new QuotaService(store, usage, clock);
        var channel = new Channel();
        return (new UsageReporter("dev_a", store, usage, quota, channel, clock), quota, usage, clock, channel, store);
    }

    [Fact]
    public async Task Nothing_new_means_no_batch_and_each_batch_is_an_increment_since_the_last_confirmed_one()
    {
        var (reporter, quota, usage, clock, channel, _) = Rig();

        Assert.Equal(0, await reporter.FlushAsync(default));
        Assert.Empty(channel.Received);

        quota.Admit(out _);
        quota.Admit(out _);
        usage.Tokens = 1000;
        clock.Advance(TimeSpan.FromSeconds(60));
        await reporter.FlushAsync(default);

        var first = Assert.Single(channel.Received);
        Assert.Equal(2L, first.Runs);
        Assert.Equal(1000L, first.InputTokens);
        Assert.Equal(60L, first.ComputeSeconds);
        Assert.Equal("dev_a:1", first.BatchId);

        quota.Admit(out _);
        usage.Tokens = 1500;
        await reporter.FlushAsync(default);

        var second = channel.Received[1];
        Assert.Equal(1L, second.Runs);
        Assert.Equal(500L, second.InputTokens);
        Assert.Equal(("dev_a:2", 2L), (second.BatchId, second.Seq));
    }

    [Fact]
    public async Task A_failed_send_keeps_the_batch_in_the_outbox_and_the_retry_resends_the_identical_batch_id()
    {
        var (reporter, quota, _, _, channel, store) = Rig();
        quota.Admit(out _);
        channel.Failures = 1;

        await Assert.ThrowsAsync<OrchestrationException>(() => reporter.FlushAsync(default));
        Assert.Equal("dev_a:1", reporter.State.Outbox!.BatchId);

        quota.Admit(out _); // 重试之前又有新的用量:不能并进旧批次
        await reporter.FlushAsync(default);

        Assert.Equal(["dev_a:1", "dev_a:2"], channel.Received.Select(b => b.BatchId));
        Assert.Equal(1, channel.Received[0].Runs);
        Assert.Equal(1, channel.Received[1].Runs);
        Assert.Null(reporter.State.Outbox);
        Assert.Equal(2, JsonDocument.Parse(store.Get(CloudKeys.Report)!).RootElement.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task The_outbox_and_baseline_survive_a_restart_nothing_is_lost_or_double_counted()
    {
        var (reporter, quota, _, _, channel, store) = Rig();
        quota.Admit(out _);
        channel.Failures = 1;
        await Assert.ThrowsAsync<OrchestrationException>(() => reporter.FlushAsync(default));

        // 休眠 / 重启:新实例读到同一份发件箱
        var clock = new ManualClock(T0 + TimeSpan.FromHours(1));
        var usage = new FakeUsage();
        var woken = new UsageReporter("dev_a", store, usage, new QuotaService(store, usage, clock), channel, clock);
        await woken.FlushAsync(default);

        var batch = Assert.Single(channel.Received);
        Assert.Equal(("dev_a:1", 1L), (batch.BatchId, batch.Runs));
        Assert.Equal(0, await woken.FlushAsync(default)); // 没有新增量
    }

    [Fact]
    public async Task A_duplicate_acknowledgement_counts_as_success()
    {
        var (reporter, quota, _, _, channel, _) = Rig();
        quota.Admit(out _);
        channel.AckDuplicate = true;

        Assert.Equal(1, await reporter.FlushAsync(default));
        Assert.Null(reporter.State.Outbox);
    }
}

public sealed class IdleTests
{
    private static readonly IdlePolicy Policy = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
    private const long Minute = 60_000;

    private static IdleVerdict Evaluate(int runs = 0, int controllers = 0, long idleForMinutes = 20, long? nextInMinutes = null) =>
        Policy.Evaluate(new IdleInputs(100 * Minute, runs, controllers, 100 * Minute - idleForMinutes * Minute, nextInMinutes is { } n ? 100 * Minute + n * Minute : null));

    [Fact]
    public void Idle_long_enough_with_nothing_going_on_can_sleep_with_no_wake_time()
    {
        var verdict = Evaluate();

        Assert.True(verdict.CanSleep);
        Assert.Null(verdict.WakeAt);
    }

    [Theory]
    [InlineData(1, 0, 20, null, IdleBlockers.Runs)]
    [InlineData(0, 1, 20, null, IdleBlockers.Controllers)]
    [InlineData(0, 0, 14, null, IdleBlockers.RecentActivity)]
    [InlineData(0, 0, 20, 9L, IdleBlockers.Schedule)]
    public void Each_blocker_prevents_sleeping(int runs, int controllers, long idle, long? next, string blocker)
    {
        var verdict = Evaluate(runs, controllers, idle, next);

        Assert.False(verdict.CanSleep);
        Assert.Equal(blocker, verdict.Blocker);
    }

    [Fact]
    public void A_distant_scheduled_task_allows_sleep_and_asks_to_be_woken_ahead_of_time()
    {
        var verdict = Evaluate(nextInMinutes: 60);

        Assert.True(verdict.CanSleep);
        Assert.Equal(100 * Minute + 58 * Minute, verdict.WakeAt); // 触发时间 − 2 分钟提前量
    }
}

public sealed class EgressTests
{
    [Fact]
    public void The_allowlist_matches_exact_hosts_and_wildcards_and_only_for_the_enforced_kinds()
    {
        var audit = new List<(EgressRequest, EgressDecision)>();
        var policy = new HostAllowlistEgress(["github.com", "*.githubusercontent.com"], [EgressKind.GitRemote, EgressKind.WebFetch], (r, d) => audit.Add((r, d)));

        Assert.True(policy.Evaluate(new EgressRequest(EgressKind.GitRemote, "github.com")).Allowed);
        Assert.True(policy.Evaluate(new EgressRequest(EgressKind.GitRemote, "GitHub.com.")).Allowed);
        Assert.True(policy.Evaluate(new EgressRequest(EgressKind.WebFetch, "raw.githubusercontent.com")).Allowed);
        Assert.False(policy.Evaluate(new EgressRequest(EgressKind.WebFetch, "githubusercontent.com")).Allowed); // 通配不含根域
        Assert.False(policy.Evaluate(new EgressRequest(EgressKind.GitRemote, "evil.example")).Allowed);
        Assert.False(policy.Evaluate(new EgressRequest(EgressKind.GitRemote, "notgithub.com")).Allowed);
        Assert.True(policy.Evaluate(new EgressRequest(EgressKind.Model, "anything.example")).Allowed); // 不受约束的用途
        Assert.Equal(7, audit.Count);
    }

    [Fact]
    public void The_policy_comes_from_the_startup_config_and_no_allowlist_means_unconstrained()
    {
        Assert.Same(AllowAllEgress.Instance, HostAllowlistEgress.FromConfig(null));
        Assert.Same(AllowAllEgress.Instance, HostAllowlistEgress.FromConfig(new CloudEgressFile()));
        Assert.False(HostAllowlistEgress.FromConfig(new CloudEgressFile(["github.com"], ["gitRemote"]))!.Evaluate(new EgressRequest(EgressKind.GitRemote, "x.example")).Allowed);
        Assert.Null(HostAllowlistEgress.FromConfig(new CloudEgressFile(["github.com"], ["teleport"])));
    }
}
