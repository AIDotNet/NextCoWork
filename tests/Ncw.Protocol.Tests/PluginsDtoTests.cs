using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 插件的线上形状:每个类型往返、方法表声明齐全(命令带幂等键、写入 owner、读取 member、用户的回应 member)、没有承载凭据 / 参数原文的字段。
/// 冻结的向量在 <see cref="GoldenV1Plugins"/>(只增向量,不改已冻结的旧向量):这批只新增方法、主题和载荷类型,没改帧格式也没改任何已冻结类型的键,所以仍是协议 v1。
/// 这些测试失败说明某个 DTO 的键名 / 形状变了,必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
public class PluginsDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    internal static PluginInfoDto Plugin(string id = "acme.hello") => new(
        id, "hello", "acme", "extension", "Hello", "d", "0.1.0", "^0.3.0", ["Other"], PluginStatuses.Active, true, "/data/plugins/" + id,
        ["storage"], ["workspace.read", "process"], ["storage"], [], ["https://api.example.com/*"], ["echo"],
        [new PluginCommandDto("acme.hello.hello", "%cmd.hello%", "sparkles")],
        [new PluginMenuItemDto("tabBar/new", "acme.hello.hello", "create@10", null)],
        [new PluginToolDto("think", "%tool.think%", "reasoning", null, "%tool.think.summary%", "plugin__acme_hello__think")],
        ["skills/guard-style"],
        [new PluginSlashCommandDto("hello", "acme.hello.hello", "%cmd.hello%", null)],
        [new PluginKeybindingDto("acme.hello.hello", "ctrl+h", null)],
        new PluginConfigurationDto("%config.title%", [new PluginConfigPropertyDto("greeting", "string", "%config.greeting%", null, [], "Hello", null, null), new PluginConfigPropertyDto("mode", "enum", "%config.mode%", "d", ["fast", "safe"], "safe", null, null)]),
        ["views"],
        [new PluginDiagnosticDto("contributes.views", "plugins.diag.unsupported", "not supported", "warn", new Dictionary<string, string> { ["contribution"] = "views" })],
        ["think"], ["acme.hello.hello"],
        [new PluginMessagesDto("en-US", new Dictionary<string, string> { ["plugin.acme.hello.cmd.hello"] = "Say hello" })],
        1_700_000_000_000, 1_700_000_100_000, "hello");

    internal static PluginInteractionDto ExecPrompt() => new(
        "req1", "acme.hello", PluginInteractionKindNames.Exec, null, null, null, [], false, true, null, [], "echo 'hello world'",
        new PluginEnvironmentDto("dev-1", "local", "Local", "local"), 1_700_000_000_000);

    [Fact]
    public void Every_plugins_payload_round_trips_and_keeps_its_fields()
    {
        var topic = RoundTrip(new PluginsTopicState(9, "0.3.3", [Plugin(), Plugin("acme.other") with { Status = PluginStatuses.Faulted, Slug = null, Configuration = null }], [ExecPrompt()]));
        Assert.Equal((9L, "0.3.3"), (topic.Rev, topic.ApiVersion));
        var info = topic.Plugins[0];
        Assert.Equal(("acme.hello", PluginStatuses.Active, true), (info.Id, info.Status, info.Enabled));
        Assert.Equal(["workspace.read", "process"], info.Optional);
        Assert.Equal("plugin__acme_hello__think", info.Tools[0].InternalId);
        Assert.Equal("contributes.views", info.Diagnostics[0].Path);
        Assert.Equal("views", info.Diagnostics[0].Params!["contribution"]);
        Assert.Equal(["fast", "safe"], info.Configuration!.Properties[1].Enum);
        Assert.Equal("Say hello", info.Messages[0].Entries["plugin.acme.hello.cmd.hello"]);
        Assert.Null(topic.Plugins[1].Slug);
        Assert.Null(topic.Plugins[1].Configuration);
        var prompt = topic.Interactions[0];
        Assert.Equal(("exec", "echo 'hello world'", true), (prompt.Kind, prompt.Command, prompt.Danger));
        Assert.Equal(new PluginEnvironmentDto("dev-1", "local", "Local", "local"), prompt.Environment); // 涉及执行的审批带设备 + 环境

        Assert.Equal(("acme.hello", false), (RoundTrip(new PluginsSetEnabledParams("acme.hello", false)).PluginId, RoundTrip(new PluginsSetEnabledParams("acme.hello", false)).Enabled));
        Assert.Equal("acme.hello", RoundTrip(new PluginsUninstallParams("acme.hello")).PluginId);
        var zip = RoundTrip(new PluginsInstallPackageParams([1, 2, 3, 0, 255], "hello.zip"));
        Assert.Equal([1, 2, 3, 0, 255], zip.Zip);
        Assert.Equal("hello.zip", zip.FileName);
        var market = RoundTrip(new PluginsInstallMarketParams("hello", null));
        Assert.Equal(("hello", null), (market.Slug, market.Version));
        var installed = RoundTrip(new PluginsInstallResult(Plugin(), new PluginsTopicState(1, "0.3.3", [Plugin()], [])));
        Assert.Equal("acme.hello", installed.Installed.Id);
        Assert.Single(installed.State.Plugins);
        Assert.Equal(["net", "storage"], RoundTrip(new PluginsPermissionsParams("acme.hello", ["net", "storage"])).Permissions);
        Assert.Null(RoundTrip(new PluginsActivityParams(null)).PluginId);
        var activity = RoundTrip(new PluginsActivityResult([new PluginActivityDto(1, "acme.hello", "workspace.readFile", "read a.txt", "denied", 3)]));
        Assert.Equal(("workspace.readFile", "denied"), (activity.Entries[0].Method, activity.Entries[0].Verdict));
        var run = RoundTrip(new PluginsRunCommandParams("acme.hello", "acme.hello.hello", "{\"a\":1}", "w1"));
        Assert.Equal(("acme.hello.hello", "{\"a\":1}", "w1"), (run.CommandId, run.ArgsJson, run.WorkspaceId));
        var action = RoundTrip(new PluginsCardActionParams("acme.card", "call-9", "yes", "{\"why\":\"ok\"}"));
        Assert.Equal(("call-9", "yes", "{\"why\":\"ok\"}"), (action.CallId, action.ActionId, action.ValueJson));
        Assert.True(RoundTrip(new PluginsCardActionResult(true)).Delivered);
        var reply = RoundTrip(new PluginsInteractionReplyParams("req1", true, null, null));
        Assert.Equal(("req1", true, null, null), (reply.RequestId, reply.Confirmed, reply.Text, reply.PickedId));
        Assert.Null(RoundTrip(new PluginsInteractionReplyParams("req1", null, null, null)).Confirmed); // 取消 = 全部缺省
    }

    [Fact]
    public void Market_update_and_configuration_payloads_round_trip()
    {
        var item = new PluginMarketItemDto("acme.pdf", "pdf", "acme", "PDF", "d", "docs", "https://nextco.work/i.png", 7, "1.0.0", "^0.3.0", ["net"], ["pdf-tools"], "Acme");
        var list = RoundTrip(new PluginsMarketListResult([item], true, 2));
        Assert.Equal((true, 2, "https://nextco.work/i.png"), (list.HasNext, list.Page, list.Items[0].IconUrl));
        Assert.Equal(["net"], list.Items[0].Permissions);
        Assert.Equal(["pdf-tools"], list.Items[0].Skills);
        var query = RoundTrip(new PluginsMarketListParams(null, "docs", 1));
        Assert.Equal((null, "docs", 1), (query.Query, query.Category, query.Page));
        Assert.Equal(["a", "b"], RoundTrip(new PluginsMarketCategoriesResult(["a", "b"])).Categories);
        Assert.Equal("pdf", RoundTrip(new PluginsMarketDetailParams("pdf")).Slug);
        var detail = RoundTrip(new PluginMarketDetailDto(item, [new PluginMarketVersionDto("1.0.0", "first", true, 123)]));
        Assert.Equal(("1.0.0", true, 123L), (detail.Versions[0].Version, detail.Versions[0].PermissionEscalated, detail.Versions[0].FileSize));
        Assert.True(RoundTrip(new PluginsCheckUpdatesParams(true)).Force);
        var updates = RoundTrip(new PluginsUpdatesResult([new PluginUpdateDto("acme.pdf", "pdf", "PDF", "1.0.0", "1.1.0", ["net"], true)]));
        Assert.Equal(("1.1.0", true), (updates.Updates[0].LatestVersion, updates.Updates[0].FromMarket));
        var all = RoundTrip(new PluginsUpdateAllResult(["acme.pdf"], [new PluginUpdateFailureDto("acme.x", "plugins.digestMismatch")]));
        Assert.Equal(("acme.pdf", "plugins.digestMismatch"), (all.Updated[0], all.Failed[0].ErrorKey));

        Assert.Equal("acme.hello", RoundTrip(new PluginsConfigurationParams("acme.hello")).PluginId);
        var set = RoundTrip(new PluginsSetConfigurationParams("acme.hello", "loud", new PluginConfigValueDto(null, null, true)));
        Assert.Equal(("loud", true), (set.Key, set.Value!.Flag));
        Assert.Null(RoundTrip(new PluginsSetConfigurationParams("acme.hello", "loud", null)).Value);
        var values = RoundTrip(new PluginsConfigurationResult("acme.hello", new Dictionary<string, PluginConfigValueDto> { ["greeting"] = new("Hello", null, null), ["retries"] = new(null, 3, null) }));
        Assert.Equal(("Hello", 3d), (values.Values["greeting"].Text, values.Values["retries"].Number));
    }

    [Fact]
    public void Every_plugins_method_declares_kind_role_capability_and_transport_writes_are_owner_commands_reads_are_member_queries_and_user_responses_are_member_commands()
    {
        Assert.Equal(18, PluginsMethods.All.Count);
        Assert.Equal(PluginsMethods.All.Count, PluginsMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in PluginsMethods.All)
        {
            Assert.Equal(Capabilities.Plugins, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
            Assert.StartsWith("plugins.", method.Name);
        }

        foreach (var write in new MethodDef[]
        {
            PluginsMethods.SetEnabled, PluginsMethods.Uninstall, PluginsMethods.InstallPackage, PluginsMethods.GrantPermissions, PluginsMethods.RevokePermissions,
            PluginsMethods.InstallMarket, PluginsMethods.UpdateAll, PluginsMethods.SetConfiguration,
        })
        {
            Assert.Equal((MethodKind.Command, Role.Owner), (write.Kind, write.MinRole)); // 在设备上装并运行第三方代码、决定它能碰什么:owner,带幂等键
        }

        foreach (var response in new MethodDef[] { PluginsMethods.RunCommand, PluginsMethods.CardAction, PluginsMethods.InteractionReply })
        {
            Assert.Equal((MethodKind.Command, Role.Member), (response.Kind, response.MinRole));
        }

        foreach (var read in new MethodDef[]
        {
            PluginsMethods.List, PluginsMethods.Activity, PluginsMethods.MarketList, PluginsMethods.MarketCategories, PluginsMethods.MarketDetail, PluginsMethods.CheckUpdates, PluginsMethods.GetConfiguration,
        })
        {
            Assert.Equal((MethodKind.Query, Role.Member), (read.Kind, read.MinRole));
        }

        Assert.Equal(("plugins", Role.Member, Transports.Any), (PluginsMethods.Topic.Name, PluginsMethods.Topic.MinRole, PluginsMethods.Topic.Allowed));
        Assert.Equal("plugins", Capabilities.Plugins);
        Assert.Equal("plugins.changed", PluginsEvents.Changed);
        Assert.True(PluginsLimits.MaxZipBytes < 4 * 1024 * 1024); // 单帧上限 4MB 之内
    }

#pragma warning disable IL2070, IL2075 // 测试里读 DTO 的属性名,不涉及裁剪
    [Fact]
    public void No_dto_carries_a_credential_or_a_call_argument_and_the_ui_view_methods_are_not_in_the_table()
    {
        foreach (var type in new[] { typeof(PluginsTopicState), typeof(PluginInfoDto), typeof(PluginInteractionDto), typeof(PluginActivityDto), typeof(PluginsConfigurationResult) })
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain("Secret", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Token", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Args", property.Name, StringComparison.Ordinal);
            }
        }

        // 活动日志只有摘要,没有参数原文;React 视图类的方法(Q2)不在方法表里
        Assert.DoesNotContain(typeof(PluginActivityDto).GetProperties(), p => p.Name is "Params" or "Arguments" or "Input");
        foreach (var absent in new[] { "plugins.activateEditor", "plugins.openWebApp", "plugins.setEditorDirty", "plugins.confirmClose", "plugins.pickPackage" })
        {
            Assert.DoesNotContain(PluginsMethods.All, m => m.Name == absent);
        }
    }
#pragma warning restore IL2070, IL2075

    [Fact]
    public void Golden_vectors_v1_plugins()
    {
        Assert.Equal(GoldenV1Plugins.TopicState, Hex(new PluginsTopicState(9, "0.3.3", [Plugin()], [ExecPrompt()])));
        Assert.Equal(GoldenV1Plugins.InstallPackageParams, Hex(new PluginsInstallPackageParams([1, 2, 3, 0, 255], "hello.zip")));
        Assert.Equal(GoldenV1Plugins.RunCommandParams, Hex(new PluginsRunCommandParams("acme.hello", "acme.hello.hello", "{\"a\":1}", "w1")));
        Assert.Equal(GoldenV1Plugins.CardActionParams, Hex(new PluginsCardActionParams("acme.card", "call-9", "yes", "{\"why\":\"ok\"}")));
        Assert.Equal(GoldenV1Plugins.InteractionReply, Hex(new PluginsInteractionReplyParams("req1", true, null, null)));
        Assert.Equal(GoldenV1Plugins.SetConfigurationParams, Hex(new PluginsSetConfigurationParams("acme.hello", "loud", new PluginConfigValueDto(null, null, true))));
        Assert.Equal(GoldenV1Plugins.MarketList, Hex(new PluginsMarketListResult([new PluginMarketItemDto("acme.pdf", "pdf", "acme", "PDF", "d", "docs", null, 7, "1.0.0", "^0.3.0", ["net"], ["pdf-tools"], "Acme")], true, 2)));
        Assert.Equal(GoldenV1Plugins.UpdateAll, Hex(new PluginsUpdateAllResult(["acme.pdf"], [new PluginUpdateFailureDto("acme.x", "plugins.digestMismatch")])));
        Assert.Equal(GoldenV1Plugins.Activity, Hex(new PluginsActivityResult([new PluginActivityDto(1, "acme.hello", "workspace.readFile", "read a.txt", "denied", 3)])));
    }
}
