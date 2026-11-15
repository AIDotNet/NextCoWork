using System.Text;
using Ncw.Protocol.Orchestration;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 编排控制通道(Agent ↔ 编排服务,<c>Ncw.Protocol/Orchestration</c>)的线上格式:JSON 往返、冻结向量(<see cref="GoldenOrchestration"/>)、
/// Ed25519 签名的确定性向量(同一个种子和载荷永远得到同一个签名,后端用任何标准 Ed25519 实现都能互验)。
/// 这不是客户端协议:控制端永远不会看到这些类型。
/// </summary>
public sealed class OrchestrationWireTests
{
    private const string Device = "dev_abcdefghjkmnpqrstvwxyz0123";
    private static readonly string Fingerprint = new('a', 64);

    private static CommandBody Body(string type) => new(type) { DeviceId = Device, Seq = 7, IssuedAt = 1_700_000_000_000, ExpiresAt = 1_700_000_600_000 };

    public static IEnumerable<(string Name, string Json)> Samples()
    {
        var entry = new TrustEntry(Fingerprint, "iPhone", "owner", new string('b', 64));
        yield return ("command.trust.put", Json(Body(CommandTypes.TrustPut) with { Trust = entry }));
        yield return ("command.trust.revoke", Json(Body(CommandTypes.TrustRevoke) with { Fingerprint = Fingerprint }));
        yield return ("command.trust.replace", Json(Body(CommandTypes.TrustReplace) with { Trusts = [entry] }));
        yield return ("command.quota.set", Json(Body(CommandTypes.QuotaSet) with { Quota = new QuotaLimits(DailyRuns: 50, MonthlyTokens: 5_000_000, MaxConcurrentRuns: 2) }));
        yield return ("command.sleep.request", Json(Body(CommandTypes.SleepRequest) with { Sleep = new SleepRequest(SleepModes.WhenIdle, "scale-down") }));
        yield return ("command.upgrade.request", Json(Body(CommandTypes.UpgradeRequest) with { Upgrade = new UpgradeRequest("3.0.1", UpgradeModes.WhenIdle) }));
        yield return ("command.account.token", Json(Body(CommandTypes.AccountToken) with { Account = new AccountTokenGrant("example-token", 1_700_003_600_000, "acct-1", "team-1") }));
        yield return ("envelope", Json(new SignedEnvelope(1, "orch-1", "e30=", "c2ln")));
        yield return ("binding", Json(new DeviceBinding(Device, Fingerprint, "acct-1", "wss://relay.example.com", 1_700_000_000_000, "team-1")));
        yield return ("hello", Json(new AgentHello(1, Device, "cHVibGlj", "c2ln", 1_700_000_000_000, "3.0.0", "0123456789abcdef", true, 7)));
        yield return ("hello.response", Json(new HelloResponse(new SignedEnvelope(1, "orch-1", "e30=", "c2ln"), [new SignedEnvelope(1, "orch-1", "e30=", "c2ln")], 1_700_000_000_500)));
        yield return ("ack", Json(new CommandAck(7, AckStatuses.Rejected, RejectCodes.BadSignature)));
        yield return ("ack.applied", Json(new CommandAck(8, AckStatuses.Applied)));
        yield return ("usage", Json(new UsageBatch(1, $"{Device}:3", Device, 3, 1_700_000_000_000, 1_700_000_060_000, 2, 1000, 500, 250, 60, 123_456_789)));
        yield return ("lifecycle.sleeping", Json(new LifecycleNotice(1, Device, LifecycleStates.Sleeping, "idle", 1_700_000_000_000, 1_700_003_600_000, new SleepSummary(1, 2, 3))));
        yield return ("lifecycle.awake", Json(new LifecycleNotice(1, Device, LifecycleStates.Awake, "wake", 1_700_000_000_000)));
        yield return ("ack.response", Json(new AckResponse(true, true)));
    }

    private static string Json(CommandBody v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v));
    private static string Json(SignedEnvelope v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.SignedEnvelope));
    private static string Json(DeviceBinding v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.DeviceBinding));
    private static string Json(AgentHello v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.AgentHello));
    private static string Json(HelloResponse v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.HelloResponse));
    private static string Json(CommandAck v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.CommandAck));
    private static string Json(UsageBatch v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.UsageBatch));
    private static string Json(LifecycleNotice v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.LifecycleNotice));
    private static string Json(AckResponse v) => Encoding.UTF8.GetString(OrchestrationWire.Encode(v, OrchestrationJson.Default.AckResponse));

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_orchestration_messages_v1(string name) =>
        Assert.Equal(GoldenOrchestration.Of(name), Samples().Single(s => s.Name == name).Json);

    [Fact]
    public void A_command_round_trips_and_unknown_keys_from_a_newer_orchestrator_are_ignored()
    {
        var original = Body(CommandTypes.QuotaSet) with { Quota = new QuotaLimits(DailyRuns: 3) };
        var decoded = OrchestrationWire.DecodeCommand(OrchestrationWire.Encode(original))!;

        Assert.Equal(original, decoded);
        var future = OrchestrationWire.DecodeCommand("""{"type":"quota.set","v":1,"deviceId":"d","seq":1,"issuedAt":1,"expiresAt":2,"somethingNew":{"a":1},"quota":{"dailyRuns":1,"brandNewLimit":5}}"""u8);
        Assert.Equal(1, future!.Quota!.DailyRuns);
        Assert.Null(OrchestrationWire.DecodeCommand("not json"u8));
        Assert.Null(OrchestrationWire.DecodeCommand("[]"u8));
    }

    [Fact]
    public void Absent_optional_fields_are_omitted_on_the_wire_not_written_as_null()
    {
        Assert.DoesNotContain("null", GoldenOrchestration.Of("lifecycle.awake"));
        Assert.DoesNotContain("trust", GoldenOrchestration.Of("command.quota.set"));
    }

    [Fact]
    public void Ed25519_signatures_are_deterministic_and_match_the_frozen_vector()
    {
        var seed = Convert.ToBase64String([.. Enumerable.Range(0, 32).Select(static i => (byte)i)]);
        var payload = Encoding.UTF8.GetBytes(GoldenOrchestration.Of("command.trust.put"));

        var envelope = EnvelopeSigning.Sign(OrchestrationProtocol.CommandContext, "orch-1", payload, Convert.FromBase64String(seed));

        Assert.Equal(GoldenOrchestration.SignatureBase64, envelope.Sig);
        Assert.Equal(Convert.ToBase64String(payload), envelope.Payload);
        var keys = new OrchestratorKeys(new Dictionary<string, byte[]> { ["orch-1"] = Convert.FromBase64String(GoldenOrchestration.PublicKeyBase64) });
        Assert.Null(EnvelopeSigning.Open(envelope, OrchestrationProtocol.CommandContext, keys, out var opened));
        Assert.Equal(payload, opened);
        // 域分隔:同一个签名放到「身份绑定」的域里就验不过
        Assert.Equal(RejectCodes.BadSignature, EnvelopeSigning.Open(envelope, OrchestrationProtocol.BindingContext, keys, out _));
    }

    [Fact]
    public void The_signed_bytes_are_the_domain_prefix_a_zero_byte_and_the_payload()
    {
        var signed = EnvelopeSigning.SignedBytes(OrchestrationProtocol.CommandContext, "xyz"u8);

        Assert.Equal("ncw-orch-command/1\0xyz", Encoding.ASCII.GetString(signed));
    }

    [Fact]
    public void The_key_list_parser_is_all_or_nothing()
    {
        var key = Convert.ToBase64String(new byte[32]);

        Assert.True(OrchestratorKeys.TryParse($"a:{key},b:{key}", out var keys));
        Assert.Equal(["a", "b"], keys.KeyIds.Order());
        Assert.False(OrchestratorKeys.TryParse($"a:{key},b:AAAA", out _));
        Assert.False(OrchestratorKeys.TryParse($"{key}", out _));
        Assert.False(OrchestratorKeys.TryParse(":", out _));
        Assert.False(OrchestratorKeys.TryParse(null, out _));
    }
}
