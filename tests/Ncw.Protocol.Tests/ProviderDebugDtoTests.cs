using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 供应商调试(<c>providers.debug.*</c>)的线上形状:代表性载荷往返、方法表声明、前缀主题、冻结向量(<see cref="GoldenV1ProviderDebug"/>)。
/// 只新增方法 / 载荷类型 / 主题(后加的能力 <c>providers.debug</c>),没改帧格式也没改任何已冻结类型,所以仍是协议 v1。
/// </summary>
public class ProviderDebugDtoTests
{
    private static readonly ProviderDebugTargetDto Draft = new(null, "https://api.deepseek.com/anthropic", "anthropic", "sk-draft");

    private static readonly ProviderDebugEventDto Sse = new(3, 412, DebugEventKinds.Sse, "content_block_delta", """{"delta":{"type":"text_delta","text":"OK"}}""");

    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("debug.modelsParams", new ProviderDebugModelsParams(Draft)),
        ("debug.models", new ProviderDebugModelsResult(
        [
            new DebugListAttemptDto("anthropic", "https://api.deepseek.com/anthropic/v1/models?limit=1000", false, 404, 0, "providers.fetchNotFound", null),
            new DebugListAttemptDto("openai", "https://api.deepseek.com/anthropic/models", true, 200, 2, null, null),
        ],
        [
            new DebugModelDto("deepseek-chat", null, ["openai"]),
            new DebugModelDto("deepseek-reasoner", "DeepSeek Reasoner", ["openai", "anthropic"]),
        ])),
        ("debug.runParams", new ProviderDebugRunParams("0f1e2d3c", new ProviderDebugTargetDto("deepseek"), "openai-chat", "deepseek-chat", "", 1024, DebugThinking.Auto, true)),
        ("debug.event", Sse),
        ("debug.result", new ProviderDebugResultDto(
            "0f1e2d3c", true, "anthropic", "deepseek-chat", "POST", "https://api.deepseek.com/anthropic/v1/messages",
            [new DebugHeaderDto("x-api-key", "••••a1b2"), new DebugHeaderDto("anthropic-version", "2023-06-01")],
            """{"model":"deepseek-chat","max_tokens":1024}""",
            200,
            [new DebugHeaderDto("content-type", "text/event-stream")],
            null,
            [new ProviderDebugEventDto(1, 0, DebugEventKinds.Request, "https://api.deepseek.com/anthropic/v1/messages", "{}"), Sse],
            false,
            "OK",
            "",
            [],
            "endTurn",
            new DebugUsageDto(12, 3, null, null, null),
            "deepseek-chat",
            412,
            1310,
            null,
            null,
            null)),
        ("debug.failed", new ProviderDebugResultDto(
            "1a2b3c4d", false, "openai-chat", "deepseek-chat", "POST", "https://api.deepseek.com/anthropic/chat/completions",
            [new DebugHeaderDto("authorization", "Bearer ••••a1b2")], "{}", 404, [], """{"error":{"message":"Not Found"}}""",
            [], false, "", "", [], null, null, null, null, 220, DebugFailures.NotFound, "provider", "Not Found")),
        ("debug.topic", new ProviderDebugTopicState("0f1e2d3c", true, [Sse])),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    private static byte[] Encode(object value) => value switch
    {
        ProviderDebugModelsParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProviderDebugModelsParams>()),
        ProviderDebugModelsResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProviderDebugModelsResult>()),
        ProviderDebugRunParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProviderDebugRunParams>()),
        ProviderDebugEventDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProviderDebugEventDto>()),
        ProviderDebugResultDto v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProviderDebugResultDto>()),
        ProviderDebugTopicState v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProviderDebugTopicState>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        ProviderDebugModelsParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderDebugModelsParams>())),
        ProviderDebugModelsResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderDebugModelsResult>())),
        ProviderDebugRunParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderDebugRunParams>())),
        ProviderDebugEventDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderDebugEventDto>())),
        ProviderDebugResultDto => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderDebugResultDto>())),
        ProviderDebugTopicState => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderDebugTopicState>())),
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
    public void Golden_vectors_v1_provider_debug(string name) =>
        Assert.Equal(GoldenV1ProviderDebug.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void Old_debug_targets_without_kind_still_decode_with_null_kind()
    {
        var models = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1ProviderDebug.ModelsParamsWithoutKind), PayloadShapes.GetShape<ProviderDebugModelsParams>());
        Assert.Equal(("https://api.deepseek.com/anthropic", "anthropic"), (models.Target.BaseUrl, models.Target.Protocol));
        Assert.Null(models.Target.Kind);

        var run = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1ProviderDebug.RunParamsWithoutKind), PayloadShapes.GetShape<ProviderDebugRunParams>());
        Assert.Equal("deepseek", run.Target.ProviderId);
        Assert.Null(run.Target.Kind);
    }

    [Fact]
    public void Both_methods_are_owner_only_under_their_own_capability_and_run_is_a_command()
    {
        Assert.Equal(2, ProviderDebugMethods.All.Count);
        Assert.All(ProviderDebugMethods.All, m =>
        {
            Assert.Equal(Capabilities.ProviderDebug, m.Capability);
            Assert.Equal(Role.Owner, m.MinRole); // 它用这家的凭据真的发请求
            Assert.StartsWith("providers.debug.", m.Name, StringComparison.Ordinal);
        });
        Assert.Equal(MethodKind.Query, ProviderDebugMethods.Models.Kind);
        Assert.Equal(MethodKind.Command, ProviderDebugMethods.Run.Kind); // 路由据此要求幂等键

        // 新能力的方法不混进已冻结的 providers 方法表
        Assert.DoesNotContain(ProvidersMethods.All, m => m.Name.StartsWith("providers.debug.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_progress_topic_is_a_per_run_owner_topic()
    {
        var topic = ProviderDebugMethods.Topic("0f1e2d3c");
        Assert.Equal("providers.debug/0f1e2d3c", topic.Name);
        Assert.StartsWith(ProviderDebugMethods.TopicPrefix, topic.Name, StringComparison.Ordinal);
        Assert.Equal(Role.Owner, topic.MinRole);
    }

    [Fact]
    public void Only_the_request_target_can_carry_a_key()
    {
        // 草稿 key 的方向只有控制端 → Agent;结果、事件、主题、模型候选里没有能装明文的字段
        Assert.Contains(typeof(ProviderDebugTargetDto).GetProperties(), p => p.Name == "ApiKey");
        NoKeyField(typeof(ProviderDebugModelsResult));
        NoKeyField(typeof(DebugListAttemptDto));
        NoKeyField(typeof(DebugModelDto));
        NoKeyField(typeof(ProviderDebugResultDto));
        NoKeyField(typeof(ProviderDebugEventDto));
        NoKeyField(typeof(ProviderDebugTopicState));
        NoKeyField(typeof(DebugHeaderDto));
    }

    private static void NoKeyField([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        Assert.DoesNotContain(type.GetProperties(), p => p.Name is "ApiKey" or "Key" or "Secret" or "Token" or "Credential");
}

/// <summary>
/// 供应商调试 DTO 的冻结向量(协议 v1,能力 <c>providers.debug</c> 批次生成)。旧向量不动:这一批只新增方法、载荷类型和主题。
/// 这些测试失败说明某个 DTO 的键名 / 形状变了,那必须升协议版本或加能力开关,不能直接改向量(见 <see cref="ProviderDebugDtoTests"/>)。
/// </summary>
internal static class GoldenV1ProviderDebug
{
    public const string ModelsParamsWithoutKind =
        "81a654617267657484aa50726f76696465724964c0a74261736555726cd92268747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f706963a850726f746f636f6ca9616e7468726f706963a64170694b6579a8736b2d6472616674";

    public const string ModelsParams =
        "81a654617267657485aa50726f76696465724964c0a74261736555726cd92268747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f706963a850726f746f636f6ca9616e7468726f706963a64170694b6579a8736b2d6472616674a44b696e64c0";

    public const string Models =
        "82a8417474656d7074739287a646616d696c79a9616e7468726f706963a355726cd93768747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f7069632f76312f6d6f64656c733f6c696d69743d31303030a24f6bc2a6537461747573cd0194a5436f756e7400a84572726f724b6579b770726f7669646572732e66657463684e6f74466f756e64a644657461696cc087a646616d696c79a66f70656e6169a355726cd92968747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f7069632f6d6f64656c73a24f6bc3a6537461747573ccc8a5436f756e7402a84572726f724b6579c0a644657461696cc0a64d6f64656c739283a24964ad646565707365656b2d63686174ab446973706c61794e616d65c0a846616d696c69657391a66f70656e616983a24964b1646565707365656b2d726561736f6e6572ab446973706c61794e616d65b1446565705365656b20526561736f6e6572a846616d696c69657392a66f70656e6169a9616e7468726f706963";

    public const string RunParamsWithoutKind =
        "88a552756e4964a83066316532643363a654617267657484aa50726f76696465724964a8646565707365656ba74261736555726cc0a850726f746f636f6cc0a64170694b6579c0a850726f746f636f6cab6f70656e61692d63686174a54d6f64656cad646565707365656b2d63686174a650726f6d7074a0af4d61784f7574707574546f6b656e73cd0400a85468696e6b696e67a46175746fa857697468546f6f6cc3";

    public const string RunParams =
        "88a552756e4964a83066316532643363a654617267657485aa50726f76696465724964a8646565707365656ba74261736555726cc0a850726f746f636f6cc0a64170694b6579c0a44b696e64c0a850726f746f636f6cab6f70656e61692d63686174a54d6f64656cad646565707365656b2d63686174a650726f6d7074a0af4d61784f7574707574546f6b656e73cd0400a85468696e6b696e67a46175746fa857697468546f6f6cc3";

    public const string Event =
        "85a353657103a9456c61707365644d73cd019ca44b696e64a3737365a44e616d65b3636f6e74656e745f626c6f636b5f64656c7461a444617461d92b7b2264656c7461223a7b2274797065223a22746578745f64656c7461222c2274657874223a224f4b227d7d";

    public const string Result =
        "de0018a552756e4964a83066316532643363a24f6bc3a850726f746f636f6ca9616e7468726f706963a54d6f64656cad646565707365656b2d63686174a64d6574686f64a4504f5354a355726cd92e68747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f7069632f76312f6d65737361676573ae52657175657374486561646572739282a44e616d65a9782d6170692d6b6579a556616c7565b0e280a2e280a2e280a2e280a26131623282a44e616d65b1616e7468726f7069632d76657273696f6ea556616c7565aa323032332d30362d3031ab52657175657374426f6479d92b7b226d6f64656c223a22646565707365656b2d63686174222c226d61785f746f6b656e73223a313032347da6537461747573ccc8af526573706f6e7365486561646572739182a44e616d65ac636f6e74656e742d74797065a556616c7565b1746578742f6576656e742d73747265616dac526573706f6e7365426f6479c0a64576656e74739285a353657101a9456c61707365644d7300a44b696e64a772657175657374a44e616d65d92e68747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f7069632f76312f6d65737361676573a444617461a27b7d85a353657103a9456c61707365644d73cd019ca44b696e64a3737365a44e616d65b3636f6e74656e745f626c6f636b5f64656c7461a444617461d92b7b2264656c7461223a7b2274797065223a22746578745f64656c7461222c2274657874223a224f4b227d7daf4576656e74735472756e6361746564c2a454657874a24f4ba85468696e6b696e67a0a9546f6f6c43616c6c7390aa53746f70526561736f6ea7656e645475726ea5557361676585a5496e7075740ca64f757470757403a9436163686552656164c0aa43616368655772697465c0a9526561736f6e696e67c0ad526573706f6e73654d6f64656cad646565707365656b2d63686174ac4669727374546f6b656e4d73cd019ca7546f74616c4d73cd051ea74661696c757265c0a94572726f72436f6465c0a644657461696cc0";

    public const string Failed =
        "de0018a552756e4964a83161326233633464a24f6bc2a850726f746f636f6cab6f70656e61692d63686174a54d6f64656cad646565707365656b2d63686174a64d6574686f64a4504f5354a355726cd93368747470733a2f2f6170692e646565707365656b2e636f6d2f616e7468726f7069632f636861742f636f6d706c6574696f6e73ae52657175657374486561646572739182a44e616d65ad617574686f72697a6174696f6ea556616c7565b742656172657220e280a2e280a2e280a2e280a261316232ab52657175657374426f6479a27b7da6537461747573cd0194af526573706f6e73654865616465727390ac526573706f6e7365426f6479d9217b226572726f72223a7b226d657373616765223a224e6f7420466f756e64227d7da64576656e747390af4576656e74735472756e6361746564c2a454657874a0a85468696e6b696e67a0a9546f6f6c43616c6c7390aa53746f70526561736f6ec0a55573616765c0ad526573706f6e73654d6f64656cc0ac4669727374546f6b656e4d73c0a7546f74616c4d73ccdca74661696c757265a86e6f74466f756e64a94572726f72436f6465a870726f7669646572a644657461696ca94e6f7420466f756e64";

    public const string Topic =
        "83a552756e4964a83066316532643363a752756e6e696e67c3a64576656e74739185a353657103a9456c61707365644d73cd019ca44b696e64a3737365a44e616d65b3636f6e74656e745f626c6f636b5f64656c7461a444617461d92b7b2264656c7461223a7b2274797065223a22746578745f64656c7461222c2274657874223a224f4b227d7d";

    public static string Of(string name) => name switch
    {
        "debug.modelsParams" => ModelsParams,
        "debug.models" => Models,
        "debug.runParams" => RunParams,
        "debug.event" => Event,
        "debug.result" => Result,
        "debug.failed" => Failed,
        "debug.topic" => Topic,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
