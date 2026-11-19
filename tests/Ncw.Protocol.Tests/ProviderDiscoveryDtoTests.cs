using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 供应商自动发现(<c>providers.discovery.*</c>)的线上形状:代表性载荷往返、方法表声明齐全、冻结向量(<see cref="GoldenV1ProviderDiscovery"/>)。
/// 只新增方法 / 载荷类型(后加的能力 <c>providers.discovery</c>),没改帧格式也没改任何已冻结类型,所以仍是协议 v1。
/// </summary>
public class ProviderDiscoveryDtoTests
{
    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("discovery.scan", new DiscoveryScanResult(
        [
            new DiscoveredProviderDto("c1f0a2b3c4d5e6f7", DiscoverySources.ClaudeCode, "~/.claude/settings.json", [DiscoverySources.CcSwitch], "deepseek", "DeepSeek",
                "anthropic", "https://api.deepseek.com/anthropic", "9f3a", ["deepseek-chat"], DiscoveryStatus.New, null),
            new DiscoveredProviderDto("0a1b2c3d4e5f6071", DiscoverySources.Env, "OPENAI_API_KEY", [], "openai", "OpenAI",
                "openai-chat", "https://api.openai.com/v1", "abcd", [], DiscoveryStatus.Imported, "openai"),
        ])),
        ("discovery.importParams", new DiscoveryImportParams(["c1f0a2b3c4d5e6f7", "0a1b2c3d4e5f6071"])),
        ("discovery.import", new DiscoveryImportResult(
        [
            new DiscoveryImportItemDto("c1f0a2b3c4d5e6f7", "deepseek", null),
            new DiscoveryImportItemDto("0a1b2c3d4e5f6071", null, DiscoveryErrorKeys.AlreadyImported),
        ])),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    private static byte[] Encode(object value) => value switch
    {
        DiscoveryScanResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DiscoveryScanResult>()),
        DiscoveryImportParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DiscoveryImportParams>()),
        DiscoveryImportResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DiscoveryImportResult>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        DiscoveryScanResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<DiscoveryScanResult>())),
        DiscoveryImportParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<DiscoveryImportParams>())),
        DiscoveryImportResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<DiscoveryImportResult>())),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_sample_round_trips_to_the_same_bytes(string name)
    {
        var bytes = Encode(Samples().Single(s => s.Name == name).Value);
        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(ReEncode(Samples().Single(s => s.Name == name).Value, bytes)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_vectors_v1_provider_discovery(string name) =>
        Assert.Equal(GoldenV1ProviderDiscovery.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void Both_methods_are_owner_only_under_their_own_capability()
    {
        Assert.Equal(2, ProviderDiscoveryMethods.All.Count);
        Assert.All(ProviderDiscoveryMethods.All, m =>
        {
            Assert.Equal(Capabilities.ProviderDiscovery, m.Capability);
            Assert.Equal(Role.Owner, m.MinRole);
            Assert.StartsWith("providers.discovery.", m.Name, StringComparison.Ordinal);
        });
        Assert.Equal(MethodKind.Query, ProviderDiscoveryMethods.Scan.Kind);
        Assert.Equal(MethodKind.Command, ProviderDiscoveryMethods.Import.Kind); // 路由据此要求幂等键

        // 新能力的方法不混进已冻结的 providers 方法表
        Assert.DoesNotContain(ProvidersMethods.All, m => m.Name.StartsWith("providers.discovery.", StringComparison.Ordinal));
    }

    [Fact]
    public void No_discovery_payload_has_a_field_that_could_carry_a_key()
    {
        // 密钥明文只在设备上:候选只有后四位,导入只传候选 id
        NoKeyField(typeof(DiscoveredProviderDto));
        NoKeyField(typeof(DiscoveryScanResult));
        NoKeyField(typeof(DiscoveryImportParams));
        NoKeyField(typeof(DiscoveryImportItemDto));
        NoKeyField(typeof(DiscoveryImportResult));
    }

    private static void NoKeyField([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        Assert.DoesNotContain(type.GetProperties(), p => p.Name is "ApiKey" or "Key" or "Secret" or "Token" or "Credential");
}
