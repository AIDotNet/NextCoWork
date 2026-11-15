using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// MCP 的线上形状:每个类型往返、方法表声明齐全(命令带幂等键、写入 owner、读取 member)、密钥不会出现在任何响应 / 主题类型里(类型上就没有承载明文的字段,除了 <c>mcp.setSecrets</c> 的请求)。
/// 冻结的向量在 <see cref="GoldenV1Mcp"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class McpDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    internal static McpServerConfigDto Stdio() =>
        new("fix", "Fixture", McpTransports.Stdio, true, "a server", "w1", "npx", ["-y", "pkg"], ["FIX_TOKEN"], "sub");

    internal static McpServerConfigDto Remote() =>
        new("far", "Remote", McpTransports.StreamableHttp, false, Url: "https://example.com/mcp", HeaderNames: ["Authorization"]);

    internal static McpServerStatusDto Status() =>
        new(Stdio(), McpStates.Connected, null, null, ["add", "echo"], [new McpRejectedToolDto("../x", "mcp.tool.nameInvalid")], 1_700_000_000_000, 0, "srv", "1.2.3");

    internal static McpServerStatusDto Failed() =>
        new(Remote(), McpStates.Failed, "mcp.connect.http", new Dictionary<string, string> { ["status"] = "502" }, [], [], null, 2, null, null);

    [Fact]
    public void Every_dto_round_trips_and_keeps_its_fields()
    {
        var topic = RoundTrip(new McpTopicState(7, [Status(), Failed()]));
        Assert.Equal(7, topic.Rev);
        Assert.Equal(["add", "echo"], topic.Servers[0].Tools);
        Assert.Equal("mcp.tool.nameInvalid", topic.Servers[0].Rejected[0].ReasonKey);
        Assert.Equal("502", topic.Servers[1].ErrorParams!["status"]);
        Assert.Equal(2, topic.Servers[1].Attempt);
        Assert.Null(topic.Servers[1].ConnectedAt);

        Assert.Equal(["FIX_TOKEN"], RoundTrip(new McpUpsertParams(Stdio())).Config.EnvNames!);
        Assert.Equal("fix", RoundTrip(new McpIdParams("fix")).Id);
        Assert.False(RoundTrip(new McpSetEnabledParams("fix", false)).Enabled);
        Assert.Equal("far", RoundTrip(new McpTestParams("far", Remote())).Config!.Id);
        Assert.Null(RoundTrip(new McpTestParams("far")).Config);
        Assert.True(RoundTrip(new McpRemoveResult(true)).Removed);
        var secrets = RoundTrip(new McpSetSecretsParams("fix", new Dictionary<string, string> { ["FIX_TOKEN"] = "v" }));
        Assert.Equal("v", secrets.Values["FIX_TOKEN"]);
        var info = RoundTrip(new McpSecretsInfoDto("fix", "env", [new McpSecretEntryDto("FIX_TOKEN", true), new McpSecretEntryDto("OTHER", false)]));
        Assert.Equal([true, false], info.Entries.Select(e => e.IsSet));
        var test = RoundTrip(new McpTestResultDto(false, [], 12, "mcp.connect.processExited", new Dictionary<string, string> { ["exitCode"] = "3" }, "detail", null, null, null, []));
        Assert.False(test.Ok);
        Assert.Equal("3", test.ErrorParams!["exitCode"]);
        Assert.Equal("file:///a", RoundTrip(new McpResourcesResult([new McpResourceDto("file:///a", "a", null, "text/plain")])).Resources[0].Uri);
        Assert.Equal(["file"], RoundTrip(new McpPromptsResult([new McpPromptDto("review", "d", ["file"])])).Prompts[0].Arguments);
    }

    [Fact]
    public void Every_mcp_method_declares_kind_role_capability_and_transport_writes_are_owner_commands_and_reads_are_member_queries()
    {
        Assert.Equal(10, McpMethods.All.Count);
        Assert.Equal(McpMethods.All.Count, McpMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in McpMethods.All)
        {
            Assert.Equal(Capabilities.Mcp, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
            Assert.StartsWith("mcp.", method.Name);
        }

        foreach (var write in new MethodDef[] { McpMethods.Upsert, McpMethods.Remove, McpMethods.SetEnabled, McpMethods.Reconnect, McpMethods.TestConnection, McpMethods.SetSecrets })
        {
            Assert.Equal((MethodKind.Command, Role.Owner), (write.Kind, write.MinRole)); // 会在设备上起进程 / 连网:owner,带幂等键
        }

        foreach (var read in new MethodDef[] { McpMethods.List, McpMethods.ResourcesList, McpMethods.PromptsList })
        {
            Assert.Equal((MethodKind.Query, Role.Member), (read.Kind, read.MinRole));
        }

        // 密钥信息只给 owner(键名本来就在配置里,但「哪些已设置」是凭据面)
        Assert.Equal((MethodKind.Query, Role.Owner), (McpMethods.GetSecretsInfo.Kind, McpMethods.GetSecretsInfo.MinRole));
        Assert.Equal(("mcp", Role.Member, Transports.Any), (McpMethods.Topic.Name, McpMethods.Topic.MinRole, McpMethods.Topic.Allowed));
        Assert.Equal("mcp", Capabilities.Mcp);
        Assert.Equal("mcp.changed", McpEvents.Changed);
    }

#pragma warning disable IL2070, IL2075 // 测试里读 DTO 的属性名,不涉及裁剪
    [Fact]
    public void No_response_or_topic_type_has_a_field_that_could_carry_a_secret_value()
    {
        // 承载明文的只有 mcp.setSecrets 的请求(控制端 → Agent)。响应 / 主题类型里没有 Dictionary<string,string> 形的「值」字段:
        // 只有 ErrorParams(结构化错误参数:状态码、退出码、秒数)。
        var responseTypes = new[]
        {
            typeof(McpTopicState), typeof(McpServerStatusDto), typeof(McpServerConfigDto), typeof(McpSecretsInfoDto), typeof(McpSecretEntryDto),
            typeof(McpTestResultDto), typeof(McpRemoveResult), typeof(McpResourcesResult), typeof(McpPromptsResult),
        };
        foreach (var type in responseTypes)
        {
            foreach (var property in type.GetProperties())
            {
                if (property.PropertyType == typeof(Dictionary<string, string>))
                {
                    Assert.Contains(property.Name, new[] { "ErrorParams" });
                }

                Assert.DoesNotContain("Secret", property.Name.Replace("SecretEntry", "").Replace("Entries", ""), StringComparison.Ordinal);
                Assert.NotEqual("Values", property.Name);
                Assert.NotEqual("Value", property.Name);
                Assert.NotEqual("Env", property.Name);
                Assert.NotEqual("Headers", property.Name);
            }
        }

        // 配置只有键名
        Assert.Contains(typeof(McpServerConfigDto).GetProperties(), p => p.Name == "EnvNames");
        Assert.DoesNotContain(typeof(McpServerConfigDto).GetProperties(), p => p.Name is "Env" or "Headers" or "Secrets");
        Assert.Contains(typeof(McpSetSecretsParams).GetProperties(), p => p.Name == "Values");
    }

#pragma warning restore IL2070, IL2075

    [Fact]
    public void Golden_vectors_v1_mcp()
    {
        Assert.Equal(GoldenV1Mcp.TopicState, Hex(new McpTopicState(7, [Status(), Failed()])));
        Assert.Equal(GoldenV1Mcp.UpsertParams, Hex(new McpUpsertParams(Stdio())));
        Assert.Equal(GoldenV1Mcp.SetSecretsParams, Hex(new McpSetSecretsParams("fix", new Dictionary<string, string> { ["FIX_TOKEN"] = "v" })));
        Assert.Equal(GoldenV1Mcp.SecretsInfo, Hex(new McpSecretsInfoDto("fix", "env", [new McpSecretEntryDto("FIX_TOKEN", true), new McpSecretEntryDto("OTHER", false)])));
        Assert.Equal(GoldenV1Mcp.TestResult, Hex(new McpTestResultDto(false, [], 12, "mcp.connect.processExited", new Dictionary<string, string> { ["exitCode"] = "3" }, "detail", null, null, null, [])));
        Assert.Equal(GoldenV1Mcp.TestParams, Hex(new McpTestParams("far", Remote())));
        Assert.Equal(GoldenV1Mcp.Resources, Hex(new McpResourcesResult([new McpResourceDto("file:///a", "a", null, "text/plain")])));
        Assert.Equal(GoldenV1Mcp.Prompts, Hex(new McpPromptsResult([new McpPromptDto("review", "d", ["file"])])));
    }
}
