using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>插件镜像的折叠规则(规则 5–7):全量状态只收不更旧的、旧快照不能盖掉新的、断开是未知、设备重启之后版本号从头来过的第一份快照照收、key 带 deviceId、提问随快照一起镜像、没有任何凭据。</summary>
public class PluginsMirrorTests
{
    private const string Dev = "dev-1";

    private static PluginInfoDto Plugin(string id, string status = PluginStatuses.Active, string displayName = "") =>
        new(id, id.Split('.')[1], id.Split('.')[0], "extension", displayName.Length == 0 ? id : displayName, "d", "1.0.0", "^0.3.0", [], status, status != PluginStatuses.Disabled, "/p/" + id,
            ["storage"], [], ["storage"], [], [], [], [], [], [], [], [], [], null, [], [], [], [], [], 1, 1, null);

    private static PluginInteractionDto Prompt(string requestId, string pluginId) =>
        new(requestId, pluginId, PluginInteractionKindNames.Exec, null, null, null, [], false, true, null, [], "echo hi", new PluginEnvironmentDto("dev-1", "local", "Local", "local"), 1);

    private static PluginsTopicState Topic(long rev, PluginInfoDto[] plugins, params PluginInteractionDto[] prompts) => new(rev, "0.3.3", plugins, prompts);

    [Fact]
    public void A_newer_snapshot_replaces_the_state_and_an_older_replay_is_ignored()
    {
        var m = PluginsReducer.ApplyTopic(PluginsMirror.Empty, Dev, Topic(5, [Plugin("acme.b"), Plugin("acme.a")]));
        Assert.Equal((true, 5L, "0.3.3"), (m.Of(Dev).Known, m.Of(Dev).Rev, m.Of(Dev).ApiVersion));
        Assert.Equal(["acme.a", "acme.b"], m.Of(Dev).Plugins.Select(p => p.Id)); // 稳定顺序

        var older = PluginsReducer.ApplyTopic(m, Dev, Topic(4, [Plugin("acme.zzz")]));
        Assert.Same(m, older); // 重放的旧快照:不变

        m = PluginsReducer.ApplyTopic(m, Dev, Topic(9, [Plugin("acme.a", PluginStatuses.Faulted)]));
        Assert.Equal(["acme.a"], m.Of(Dev).Plugins.Select(p => p.Id));
        Assert.Equal(PluginStatuses.Faulted, m.Of(Dev).Find("acme.a")!.Status);
    }

    [Fact]
    public void The_same_revision_is_a_no_op_so_the_store_does_not_churn()
    {
        var m = PluginsReducer.ApplyTopic(PluginsMirror.Empty, Dev, Topic(5, [Plugin("acme.a")]));

        Assert.Same(m, PluginsReducer.ApplyTopic(m, Dev, Topic(5, [Plugin("acme.a")])));
    }

    [Fact]
    public void A_mutation_result_and_the_topic_event_are_the_same_type_so_whichever_arrives_second_cannot_write_older_state()
    {
        var fromResponse = Topic(7, [Plugin("acme.a", PluginStatuses.Active)]);
        var fromEvent = Topic(6, [Plugin("acme.a", PluginStatuses.Activating)]);

        var m = PluginsReducer.ApplyTopic(PluginsMirror.Empty, Dev, fromResponse);
        m = PluginsReducer.ApplyTopic(m, Dev, fromEvent); // 事件晚到

        Assert.Equal(PluginStatuses.Active, m.Of(Dev).Find("acme.a")!.Status);
    }

    [Fact]
    public void A_disconnect_marks_the_mirror_unknown_and_the_first_snapshot_after_a_device_restart_is_accepted_even_with_a_lower_revision()
    {
        var m = PluginsReducer.ApplyTopic(PluginsMirror.Empty, Dev, Topic(40, [Plugin("acme.a")]));

        m = PluginsReducer.MarkUnknown(m, Dev);
        Assert.False(m.Of(Dev).Known);
        Assert.Single(m.Of(Dev).Plugins); // 旧内容还在,但标了未知:界面不把它当现状(规则 7)

        m = PluginsReducer.ApplyTopic(m, Dev, Topic(2, [Plugin("acme.b")]));
        Assert.True(m.Of(Dev).Known);
        Assert.Equal(["acme.b"], m.Of(Dev).Plugins.Select(p => p.Id));
    }

    [Fact]
    public void Pending_interactions_are_mirrored_with_the_snapshot_and_vanish_with_the_next_one()
    {
        var m = PluginsReducer.ApplyTopic(PluginsMirror.Empty, Dev, Topic(1, [Plugin("acme.a"), Plugin("acme.b")], Prompt("r1", "acme.a")));

        Assert.Equal(["r1"], m.Of(Dev).InteractionsOf("acme.a").Select(i => i.RequestId));
        Assert.Empty(m.Of(Dev).InteractionsOf("acme.b"));
        Assert.Equal("local", m.Of(Dev).Interactions[0].Environment!.Environment); // 涉及执行的审批带设备 + 环境

        m = PluginsReducer.ApplyTopic(m, Dev, Topic(2, [Plugin("acme.a"), Plugin("acme.b")]));
        Assert.Empty(m.Of(Dev).Interactions);
    }

    [Fact]
    public void Devices_are_mirrored_independently_and_an_unknown_device_has_nothing()
    {
        var m = PluginsReducer.ApplyTopic(PluginsMirror.Empty, Dev, Topic(1, [Plugin("acme.a")]));
        m = PluginsReducer.ApplyTopic(m, "dev-2", Topic(1, [Plugin("acme.x")]));

        Assert.Equal(["acme.a"], m.Of(Dev).Plugins.Select(p => p.Id));
        Assert.Equal(["acme.x"], m.Of("dev-2").Plugins.Select(p => p.Id));
        Assert.False(m.Of("dev-3").Known);
        Assert.Empty(m.Of("dev-3").Plugins);
        Assert.Null(m.Of(Dev).Find("acme.x")); // 别的设备的插件不会串过来
    }

#pragma warning disable IL2070, IL2075 // 测试里读 DTO 的属性名,不涉及裁剪
    [Fact]
    public void The_mirrored_types_have_no_credential_field()
    {
        foreach (var type in new[] { typeof(DevicePlugins), typeof(PluginInfoDto), typeof(PluginInteractionDto) })
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain("Secret", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Token", property.Name, StringComparison.Ordinal);
            }
        }
    }
#pragma warning restore IL2070, IL2075
}
