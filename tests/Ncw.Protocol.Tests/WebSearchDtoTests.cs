using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 搜索提供方(<c>websearch.*</c>)的线上形状:代表性载荷往返、方法表声明齐全(种类、角色、能力)、冻结向量(<see cref="GoldenV1WebSearch"/>)。
/// 只新增方法 / 主题 / 载荷类型,没改帧格式也没改任何已冻结类型的键,所以仍是协议 v1。向量失败说明某个 DTO 的键名 / 形状变了,那必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
public class WebSearchDtoTests
{
    private static WebSearchProviderDto Provider(string id, bool enabled, int priority, bool hasKey, string? last4 = null, string? unavailable = null) =>
        new(id, id.ToUpperInvariant(), $"https://keys.example/{id}", unavailable, enabled, priority, hasKey, last4);

    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("websearch.state", new WebSearchTopicState(7, [Provider("tavily", true, 0, true, "1234"), Provider("exa", false, 1, false), Provider("bing", false, 2, false, null, "websearch.unavailable.bing")])),
        ("websearch.setEnabled", new WebSearchSetEnabledParams("tavily", true)),
        ("websearch.reorder", new WebSearchReorderParams(["exa", "tavily", "brave"])),
        ("websearch.setCredential", new WebSearchCredentialSetParams("tavily", "tvly-0123456789")),
        ("websearch.credentialInfo", new WebSearchCredentialInfoDto(true, "6789")),
        ("websearch.test", new WebSearchTestResultDto(false, null, "websearch.test.failed", "Tavily returned HTTP 401 Unauthorized")),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    public static string HexOf(object value) => Convert.ToHexStringLower(Encode(value));

    private static byte[] Encode(object value) => value switch
    {
        WebSearchTopicState v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WebSearchTopicState>()),
        WebSearchSetEnabledParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WebSearchSetEnabledParams>()),
        WebSearchReorderParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WebSearchReorderParams>()),
        WebSearchCredentialSetParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WebSearchCredentialSetParams>()),
        WebSearchCredentialInfoDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WebSearchCredentialInfoDto>()),
        WebSearchTestResultDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<WebSearchTestResultDto>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        WebSearchTopicState => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WebSearchTopicState>())),
        WebSearchSetEnabledParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WebSearchSetEnabledParams>())),
        WebSearchReorderParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WebSearchReorderParams>())),
        WebSearchCredentialSetParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WebSearchCredentialSetParams>())),
        WebSearchCredentialInfoDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WebSearchCredentialInfoDto>())),
        WebSearchTestResultDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WebSearchTestResultDto>())),
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
    public void Golden_vectors_v1_websearch(string name) =>
        Assert.Equal(GoldenV1WebSearch.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void Every_method_declares_the_websearch_capability_with_the_right_kind_and_role()
    {
        Assert.Equal(6, WebSearchMethods.All.Count);
        Assert.All(WebSearchMethods.All, m =>
        {
            Assert.Equal(Capabilities.WebSearch, m.Capability);
            Assert.StartsWith("websearch.", m.Name, StringComparison.Ordinal);
        });

        // 读 member 起;写和测试 owner(Key 是设备的秘密,测试花额度);写入类是 command(路由据此要求幂等键),测试和读是 query
        Assert.Equal(Role.Member, WebSearchMethods.List.MinRole);
        Assert.All(WebSearchMethods.All.Where(static m => m.Name is "websearch.setEnabled" or "websearch.reorder" or "websearch.setCredential" or "websearch.clearCredential"), m =>
        {
            Assert.Equal(Role.Owner, m.MinRole);
            Assert.Equal(MethodKind.Command, m.Kind);
        });
        Assert.Equal(Role.Owner, WebSearchMethods.Test.MinRole);
        Assert.Equal(MethodKind.Query, WebSearchMethods.Test.Kind);
        Assert.Equal(MethodKind.Query, WebSearchMethods.List.Kind);
        Assert.Equal(Role.Member, WebSearchMethods.Topic.MinRole);
    }

    [Fact]
    public void There_is_no_method_that_returns_the_plaintext_key()
    {
        // 明文 Key 只有一个方向:进。方法表里没有 reveal / get 之类读回它的方法。
        Assert.DoesNotContain(WebSearchMethods.All, m => m.Name.Contains("reveal", StringComparison.OrdinalIgnoreCase) || m.Name.EndsWith(".getCredential", StringComparison.Ordinal));
    }
}
