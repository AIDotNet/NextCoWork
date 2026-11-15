using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// <c>push.register</c> 的线上形状:载荷往返、方法表声明(种类、角色、能力、承载)、冻结向量(<see cref="GoldenV1Push"/>)。
/// 冻结的旧向量不动;这一批只新增方法 / 载荷类型,没改帧格式,所以仍是协议 v1。
/// </summary>
public class PushDtoTests
{
    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("push.register.params", new PushRegisterParams(PushPlatforms.Apns, "a1b2c3d4", "3.0.0")),
        ("push.register.paramsNoVersion", new PushRegisterParams(PushPlatforms.Fcm, "fcm-token")),
        ("push.register.result", new PushRegisterResult(PushPlatforms.Apns, 1_700_000_000_000)),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    public static string HexOf(object value) => Convert.ToHexStringLower(Encode(value));

    private static byte[] Encode(object value) => value switch
    {
        PushRegisterParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<PushRegisterParams>()),
        PushRegisterResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<PushRegisterResult>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        PushRegisterParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<PushRegisterParams>())),
        PushRegisterResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<PushRegisterResult>())),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_sample_round_trips_to_the_same_bytes(string name)
    {
        var value = Samples().Single(s => s.Name == name).Value;
        var bytes = Encode(value);

        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(ReEncode(value, bytes)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_vectors_v1_push(string name) =>
        Assert.Equal(GoldenV1Push.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void An_absent_app_version_stays_null_after_a_round_trip()
    {
        var decoded = FrameCodec.DecodePayload(Encode(new PushRegisterParams(PushPlatforms.Fcm, "t")), PayloadShapes.GetShape<PushRegisterParams>());

        Assert.Null(decoded.AppVersion);
        Assert.Equal("t", decoded.Token);
    }

    [Fact]
    public void Push_register_is_a_member_command_of_the_push_capability_in_the_method_table()
    {
        var method = Assert.Single(PushMethods.All);

        Assert.Equal("push.register", method.Name);
        Assert.Equal(MethodKind.Command, method.Kind); // command:路由要求幂等键
        Assert.Equal(Role.Member, method.MinRole);
        Assert.Equal(Capabilities.Push, method.Capability);
        Assert.StartsWith(method.Capability + ".", method.Name, StringComparison.Ordinal);
        Assert.Equal(Transports.Any, method.Allowed);
    }

    [Fact]
    public void Only_apns_and_fcm_are_known_platforms()
    {
        Assert.True(PushPlatforms.IsKnown("apns"));
        Assert.True(PushPlatforms.IsKnown("fcm"));
        Assert.False(PushPlatforms.IsKnown("wns"));
        Assert.False(PushPlatforms.IsKnown(null));
    }
}
