using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>MCP 镜像的折叠规则(规则 5–7):全量状态只收不更旧的、旧快照不能盖掉新的、断开是未知、重启之后版本号从头来过的第一份快照照收、key 带 deviceId、没有任何密钥值。</summary>
public class McpMirrorTests
{
    private const string Dev = "dev-1";

    private static McpServerStatusDto Server(string id, string state = McpStates.Connected, string? error = null, params string[] tools) =>
        new(new McpServerConfigDto(id, id.ToUpperInvariant(), McpTransports.Stdio, true, Command: "npx", EnvNames: ["TOKEN"]), state, error, null, tools, [], null, 0, null, null);

    private static McpTopicState Topic(long rev, params McpServerStatusDto[] servers) => new(rev, servers);

    [Fact]
    public void A_newer_snapshot_replaces_the_state_and_an_older_replay_is_ignored()
    {
        var m = McpReducer.ApplyTopic(McpMirror.Empty, Dev, Topic(5, Server("b"), Server("a", tools: ["add"])));
        Assert.Equal((true, 5L), (m.Of(Dev).Known, m.Of(Dev).Rev));
        Assert.Equal(["a", "b"], m.Of(Dev).Servers.Select(s => s.Config.Id)); // 稳定顺序

        var older = McpReducer.ApplyTopic(m, Dev, Topic(4, Server("zzz")));
        Assert.Same(m, older);                                                  // 重放的旧快照:不变

        m = McpReducer.ApplyTopic(m, Dev, Topic(9, Server("a", McpStates.Failed, "mcp.connect.processExited")));
        Assert.Equal(["a"], m.Of(Dev).Servers.Select(s => s.Config.Id));
        Assert.Equal("mcp.connect.processExited", m.Of(Dev).Find("a")!.ErrorKey);
    }

    [Fact]
    public void The_same_revision_with_the_same_content_is_a_no_op_so_the_store_does_not_churn()
    {
        var m = McpReducer.ApplyTopic(McpMirror.Empty, Dev, Topic(5, Server("a")));

        Assert.Same(m, McpReducer.ApplyTopic(m, Dev, Topic(5, Server("a"))));
    }

    [Fact]
    public void A_disconnect_marks_the_mirror_unknown_and_the_first_snapshot_after_a_device_restart_is_accepted_even_with_a_lower_revision()
    {
        var m = McpReducer.ApplyTopic(McpMirror.Empty, Dev, Topic(40, Server("a")));

        m = McpReducer.MarkUnknown(m, Dev);
        Assert.False(m.Of(Dev).Known);
        Assert.Single(m.Of(Dev).Servers); // 旧内容还在,但标了未知:界面不把它当现状(规则 7)

        m = McpReducer.ApplyTopic(m, Dev, Topic(2, Server("b")));
        Assert.True(m.Of(Dev).Known);
        Assert.Equal(["b"], m.Of(Dev).Servers.Select(s => s.Config.Id));
    }

    [Fact]
    public void Devices_are_mirrored_independently_and_an_unknown_device_has_nothing()
    {
        var m = McpReducer.ApplyTopic(McpMirror.Empty, Dev, Topic(1, Server("a")));
        m = McpReducer.ApplyTopic(m, "dev-2", Topic(1, Server("x")));

        Assert.Equal(["a"], m.Of(Dev).Servers.Select(s => s.Config.Id));
        Assert.Equal(["x"], m.Of("dev-2").Servers.Select(s => s.Config.Id));
        Assert.False(m.Of("dev-3").Known);
        Assert.Same(m, McpReducer.MarkUnknown(m, "dev-3"));
    }
}
