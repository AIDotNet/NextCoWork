using System.Text;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>中继承载的线上格式:控制消息的冻结 JSON、追加字段的向后兼容(旧载荷照常解码)与冻结字节。</summary>
public sealed class RelayWireTests
{
    private static readonly byte[] Nonce = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    public static IEnumerable<(string Name, RelayMessage Message, string Golden)> Messages() =>
    [
        ("challenge", new RelayMessage("challenge") { V = 1, Nonce = Convert.ToBase64String(Nonce) }, GoldenRelay.Challenge),
        ("register", new RelayMessage("register") { V = 1, PublicKey = "cHVibGlj", Signature = "c2ln", Tokens = ["aa", "bb"] }, GoldenRelay.Register),
        ("registered", new RelayMessage("registered") { DeviceId = "dev_abcdefghjkmnpqrstvwxyz0123" }, GoldenRelay.Registered),
        ("tokens.set", new RelayMessage("tokens") { Set = ["aa"] }, GoldenRelay.TokensSet),
        ("tokens.delta", new RelayMessage("tokens") { Add = ["aa"], Remove = ["bb"] }, GoldenRelay.TokensDelta),
        ("session", new RelayMessage("session") { Session = "0123456789abcdef0123456789abcdef", TokenHash = "aa" }, GoldenRelay.Session),
        ("connect", new RelayMessage("connect") { V = 1, Token = "dG9rZW4=" }, GoldenRelay.Connect),
        ("ready", new RelayMessage("ready"), GoldenRelay.Ready),
        ("error", new RelayMessage("error") { Code = RelayErrors.DeviceOffline, Message = "gone" }, GoldenRelay.Error),
    ];

    public static TheoryData<string> Names() => [.. Messages().Select(m => m.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_relay_messages_v1(string name)
    {
        var (_, message, golden) = Messages().Single(m => m.Name == name);
        Assert.Equal(golden, RelayProtocol.Serialize(message));
        Assert.Equal(golden, RelayProtocol.Serialize(RelayProtocol.Deserialize(Encoding.UTF8.GetBytes(golden))!)); // 解码再编码,字节不变
    }

    [Fact]
    public void Unknown_keys_are_ignored_and_garbage_is_not_a_message()
    {
        var future = RelayProtocol.Deserialize("{\"type\":\"ready\",\"somethingNew\":{\"a\":1},\"v\":1}"u8);
        Assert.Equal("ready", future!.Type);

        Assert.Null(RelayProtocol.Deserialize("not json"u8));
        Assert.Null(RelayProtocol.Deserialize("[]"u8));
        Assert.Null(RelayProtocol.Deserialize(""u8));
        Assert.Null(RelayProtocol.Deserialize("{\"v\":1}"u8)); // 没有 type
    }

    // ---- 追加的末尾字段 -----------------------------------------------------------------------------------------

    /// <summary>手写一个 MessagePack map(字符串键),代表「旧版本设备 / 控制端」发来的载荷:它们没有新字段。</summary>
    private static byte[] Map(params (string Key, object? Value)[] fields)
    {
        var bytes = new List<byte> { (byte)(0x80 | fields.Length) };
        foreach (var (key, value) in fields)
        {
            Str(bytes, key);
            switch (value)
            {
                case null: bytes.Add(0xc0); break;
                case bool b: bytes.Add(b ? (byte)0xc3 : (byte)0xc2); break;
                case int i when i < 128: bytes.Add((byte)i); break;
                case int i: bytes.AddRange([0xcd, (byte)(i >> 8), (byte)i]); break;
                case string s: Str(bytes, s); break;
                case byte[] bin: bytes.AddRange([0xc4, (byte)bin.Length]); bytes.AddRange(bin); break;
                case string[] list:
                    bytes.Add((byte)(0x90 | list.Length));
                    foreach (var item in list)
                    {
                        Str(bytes, item);
                    }

                    break;
                default: throw new NotSupportedException(value.GetType().Name);
            }
        }

        return [.. bytes];

        static void Str(List<byte> to, string s)
        {
            var utf8 = Encoding.UTF8.GetBytes(s);
            to.Add((byte)(0xa0 | utf8.Length));
            to.AddRange(utf8);
        }
    }

    [Fact]
    public void Old_pairing_results_without_relay_still_decode_with_a_null_relay()
    {
        var finish = FrameCodec.DecodePayload(
            Map(("M2", new byte[] { 1, 2 }), ("Proof", new byte[] { 3 }), ("Role", "member"), ("RouteToken", "tok")),
            PayloadShapes.GetShape<PairFinishResult>());
        Assert.Equal("tok", finish.RouteToken);
        Assert.Null(finish.Relay);

        var claim = FrameCodec.DecodePayload(Map(("Role", "member"), ("RouteToken", "tok")), PayloadShapes.GetShape<PairClaimResult>());
        Assert.Equal("member", claim.Role);
        Assert.Null(claim.Relay);
    }

    [Fact]
    public void Old_device_info_and_remote_status_decode_with_null_relay_fields()
    {
        var info = FrameCodec.DecodePayload(
            Map(("DeviceId", "d"), ("Name", "n"), ("Kind", "local"), ("AgentVersion", "3.0.0"), ("Os", "os"), ("DataRoot", "/r")),
            PayloadShapes.GetShape<DeviceInfoResult>());
        Assert.Null(info.Relay);

        var status = FrameCodec.DecodePayload(
            Map(("Enabled", true), ("Port", 7810), ("PreferredPort", 7810), ("LanOnly", true), ("Addresses", new[] { "192.168.1.2" }), ("PairingCode", "123456"), ("Error", null)),
            PayloadShapes.GetShape<RemoteStatus>());
        Assert.Equal(7810, status.Port);
        Assert.Null(status.RelayUrl);
        Assert.Null(status.RelayState);
        Assert.Null(status.RelayError);
    }

    [Fact]
    public void Old_remote_configure_means_do_not_touch_the_relay()
    {
        var configure = FrameCodec.DecodePayload(Map(("Port", 8000), ("LanOnly", null)), PayloadShapes.GetShape<RemoteConfigureParams>());
        Assert.Equal(8000, configure.Port);
        Assert.Null(configure.RelayUrl); // null = 不改;空串才是「关闭中继」
    }

    public static IEnumerable<(string Name, object Value, string Golden)> Frozen() =>
    [
        ("pair.finish.withRelay", new PairFinishResult([1, 2], [3], "member", "tok", "wss://relay.example"), GoldenRelay.PairFinishResultWithRelay),
        ("pair.claim.withRelay", new PairClaimResult("member", "tok", "wss://relay.example"), GoldenRelay.PairClaimResultWithRelay),
        ("device.info.withRelay", new DeviceInfoResult("d", "n", "local", "3.0.0", "os", "/r", "wss://relay.example"), GoldenRelay.DeviceInfoWithRelay),
        ("remote.status.withRelay", new RemoteStatus(true, 7810, 7810, true, ["192.168.1.2"], "123456", null, "wss://relay.example", "registered", null), GoldenRelay.RemoteStatusWithRelay),
        ("remote.configure.withRelay", new RemoteConfigureParams(null, null, "wss://relay.example"), GoldenRelay.RemoteConfigureWithRelay),
        ("remote.configure.clearRelay", new RemoteConfigureParams(null, null, ""), GoldenRelay.RemoteConfigureClearRelay),
    ];

    public static TheoryData<string> FrozenNames() => [.. Frozen().Select(f => f.Name)];

    private static byte[] Encode(object value) => value switch
    {
        PairFinishResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<PairFinishResult>()),
        PairClaimResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<PairClaimResult>()),
        DeviceInfoResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DeviceInfoResult>()),
        RemoteStatus v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<RemoteStatus>()),
        RemoteConfigureParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<RemoteConfigureParams>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(FrozenNames))]
    public void Golden_vectors_v1_relay_fields(string name)
    {
        var (_, value, golden) = Frozen().Single(f => f.Name == name);
        Assert.Equal(golden, Convert.ToHexStringLower(Encode(value)));
    }

    [Fact]
    public void Relay_fields_round_trip()
    {
        var finish = FrameCodec.DecodePayload(Encode(new PairFinishResult([1], [2], "member", "t", "wss://r.example")), PayloadShapes.GetShape<PairFinishResult>());
        Assert.Equal("wss://r.example", finish.Relay);
        var status = FrameCodec.DecodePayload(Encode(new RemoteStatus(true, 1, 1, true, [], null, null, "wss://r.example", "failed", "unauthorized")), PayloadShapes.GetShape<RemoteStatus>());
        Assert.Equal(("wss://r.example", "failed", "unauthorized"), (status.RelayUrl, status.RelayState, status.RelayError));
        var configure = FrameCodec.DecodePayload(Encode(new RemoteConfigureParams(7000, false, "")), PayloadShapes.GetShape<RemoteConfigureParams>());
        Assert.Equal((7000, false, ""), (configure.Port, configure.LanOnly, configure.RelayUrl));
    }
}
