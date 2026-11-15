using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// Skills / 命令镜像的折叠规则(规则 1、6、7:全量替换、只收不更旧的、key 带 deviceId、断开是未知)与不碰设备的那几条 Actions 行为(设备没连上、zip 太大、非命令草稿零开销)。
/// 设备在线的往返(真实 Agent + DeviceLink)在 Ncw.Integration.Tests。
/// </summary>
public sealed class ExtensionsMirrorTests
{
    private const string Dev = "dev-1";

    private static SkillInfoDto Skill(string name, bool active = true) =>
        new(name, name, "d", "未分类", "folder", "global", null, true, active, false, null, null, "/s/" + name, 0, 0);

    private static SkillsListResult List(params SkillInfoDto[] skills) => new(skills);

    [Fact]
    public void A_list_replaces_the_layer_and_an_older_response_arriving_late_cannot_overwrite_a_newer_one()
    {
        var m = SkillsReducer.ApplyList(SkillsMirror.Empty, Dev, "ws", List(Skill("a")), 5);
        m = SkillsReducer.ApplyList(m, Dev, "ws", List(Skill("a"), Skill("b")), 7);
        var late = SkillsReducer.ApplyList(m, Dev, "ws", List(Skill("stale")), 6);

        Assert.Same(m, late); // 乱序到达的旧响应:不变(规则 6)
        Assert.Equal(["a", "b"], m.Of(Dev, "ws")!.Skills.Select(s => s.Name));
        Assert.True(m.Of(Dev, "ws")!.Known);
    }

    [Fact]
    public void Layers_are_keyed_by_device_and_workspace_so_nothing_leaks_between_them()
    {
        var m = SkillsReducer.ApplyList(SkillsMirror.Empty, Dev, "ws", List(Skill("a")), 1);
        m = SkillsReducer.ApplyList(m, Dev, null, List(Skill("global-only")), 2);
        m = SkillsReducer.ApplyList(m, "dev-2", "ws", List(Skill("other-device")), 3);

        Assert.Equal(["a"], m.Of(Dev, "ws")!.Skills.Select(s => s.Name));
        Assert.Equal(["global-only"], m.Of(Dev, null)!.Skills.Select(s => s.Name));
        Assert.Equal(["other-device"], m.Of("dev-2", "ws")!.Skills.Select(s => s.Name));
        Assert.Null(m.Of(Dev, "never-fetched")); // 没取过 ≠ 取过、一条都没有
        Assert.Equivalent(new string?[] { "ws", null }, m.WorkspacesOf(Dev));
    }

    [Fact]
    public void A_disconnect_marks_only_that_devices_layers_unknown_and_keeps_the_data_and_the_next_list_makes_it_known_again()
    {
        var m = SkillsReducer.ApplyList(SkillsMirror.Empty, Dev, "ws", List(Skill("a")), 1);
        m = SkillsReducer.ApplyList(m, "dev-2", "ws", List(Skill("b")), 2);

        var down = SkillsReducer.MarkDeviceUnknown(m, Dev);

        Assert.False(down.Of(Dev, "ws")!.Known);
        Assert.Equal(["a"], down.Of(Dev, "ws")!.Skills.Select(s => s.Name)); // 不把旧值丢掉,也不当现状
        Assert.True(down.Of("dev-2", "ws")!.Known);
        Assert.Same(down, SkillsReducer.MarkDeviceUnknown(down, Dev)); // 幂等

        var back = SkillsReducer.ApplyList(down, Dev, "ws", List(Skill("a2")), 9);
        Assert.True(back.Of(Dev, "ws")!.Known);
    }

    [Fact]
    public void An_unknown_layer_still_accepts_any_fetch_after_a_reconnect_even_with_a_smaller_sequence()
    {
        // 重连后进程内序号不会倒退,但 Known = false 的层要无条件接受第一份新数据
        var m = SkillsReducer.MarkDeviceUnknown(SkillsReducer.ApplyList(SkillsMirror.Empty, Dev, "ws", List(Skill("a")), 50), Dev);

        var back = SkillsReducer.ApplyList(m, Dev, "ws", List(Skill("fresh")), 1);

        Assert.Equal(["fresh"], back.Of(Dev, "ws")!.Skills.Select(s => s.Name));
    }

    [Fact]
    public void Diagnostics_are_a_separate_stream_with_their_own_sequence()
    {
        var m = SkillsReducer.ApplyList(SkillsMirror.Empty, Dev, "ws", List(Skill("a")), 10);
        m = SkillsReducer.ApplyDiagnostics(m, Dev, "ws", new SkillsDiagnosticsResult([new SkillDiagnosticDto("/p", "skills.outsideRoot", "x")]), 4);
        var late = SkillsReducer.ApplyDiagnostics(m, Dev, "ws", new SkillsDiagnosticsResult([]), 3);

        Assert.Same(m, late);
        Assert.True(m.Of(Dev, "ws")!.HasDiagnostics);
        Assert.Equal("skills.outsideRoot", m.Of(Dev, "ws")!.Diagnostics[0].Key);
        Assert.Equal(["a"], m.Of(Dev, "ws")!.Skills.Select(s => s.Name)); // 诊断不动清单
    }

    // ───────────────────────── 命令镜像 ─────────────────────────

    private static CommandDto Command(string name, string prompt = "p") => new(name, "d", prompt, "global", "/c/" + name + ".md", null);

    private static CommandItemDto Item(string name, bool enabled = true) => new(name, "d", "global", "/c/" + name + ".md", null, enabled);

    [Fact]
    public void The_runtime_list_and_the_management_list_are_two_streams_that_do_not_disturb_each_other()
    {
        var m = CommandsReducer.ApplyList(CommandsMirror.Empty, Dev, "ws", new CommandsListResult([Command("a", "Do $ARGUMENTS")]), 5);
        m = CommandsReducer.ApplyListAll(m, Dev, "ws", new CommandsListAllResult([Item("a"), Item("b", false)]), 2);
        var lateList = CommandsReducer.ApplyList(m, Dev, "ws", new CommandsListResult([]), 4);
        var lateAll = CommandsReducer.ApplyListAll(m, Dev, "ws", new CommandsListAllResult([]), 1);

        Assert.Same(m, lateList);
        Assert.Same(m, lateAll);
        var layer = m.Of(Dev, "ws")!;
        Assert.Equal(["a"], layer.Commands.Select(c => c.Name));
        Assert.Equal([("a", true), ("b", false)], layer.All.Select(c => (c.Name, c.Enabled)));
        Assert.Equal("Do $ARGUMENTS", layer.Commands[0].Prompt);
    }

    [Fact]
    public void Command_layers_follow_the_same_device_key_and_unknown_rules()
    {
        var m = CommandsReducer.ApplyList(CommandsMirror.Empty, Dev, "ws", new CommandsListResult([Command("a")]), 1);
        m = CommandsReducer.ApplyList(m, "dev-2", null, new CommandsListResult([Command("b")]), 2);

        var down = CommandsReducer.MarkDeviceUnknown(m, Dev);

        Assert.False(down.Of(Dev, "ws")!.Known);
        Assert.True(down.Of("dev-2", null)!.Known);
        Assert.Equal(["a"], down.Of(Dev, "ws")!.Commands.Select(c => c.Name));
        Assert.Same(down, CommandsReducer.MarkDeviceUnknown(down, Dev));
        Assert.Equal([(string?)"ws"], down.WorkspacesOf(Dev));
    }

    // ───────────────────────── Actions(不碰设备的那几条) ─────────────────────────

    [Fact]
    public async Task Skills_actions_on_a_device_that_is_not_connected_are_unavailable_and_never_throw()
    {
        var actions = new SkillsActions(new DeviceRegistry(), new Store<SkillsMirror>(SkillsMirror.Empty));

        Assert.Equal(ActionOutcome.Unavailable, (await actions.ListAsync(Dev, "ws")).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.SetWorkspaceActiveAsync(Dev, "ws", "a", true)).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.MarketListAsync(Dev, null, null)).Outcome);
    }

    [Fact]
    public async Task Installing_a_zip_that_cannot_fit_in_one_request_is_refused_on_the_client_with_the_stable_key()
    {
        var actions = new SkillsActions(new DeviceRegistry(), new Store<SkillsMirror>(SkillsMirror.Empty));

        var big = await actions.InstallZipAsync(Dev, new byte[SkillsLimits.MaxZipBytes + 1], "x.zip", SkillScopes.Global);
        var empty = await actions.InstallZipAsync(Dev, [], null, SkillScopes.Global);

        Assert.Equal((ActionOutcome.Invalid, "skills.packageTooLarge"), (big.Outcome, big.ErrorKey));
        Assert.Equal(ActionOutcome.Invalid, empty.Outcome);
    }

    [Fact]
    public async Task Expanding_an_ordinary_message_never_touches_the_device_but_a_command_without_a_connection_fails_instead_of_sending_the_raw_slash_text()
    {
        var actions = new CommandsActions(new DeviceRegistry(), new Store<CommandsMirror>(CommandsMirror.Empty));

        var plain = await actions.ExpandAsync(Dev, "ws", "帮我看下 /init 这个命令");
        var notConnected = await actions.ExpandAsync(Dev, "ws", "/init update");

        Assert.Equal((ActionOutcome.Ok, "帮我看下 /init 这个命令"), (plain.Outcome, plain.Value));
        Assert.Equal(ActionOutcome.Unavailable, notConnected.Outcome);
        Assert.Null(notConnected.Value);
    }

    [Fact]
    public async Task Commands_actions_on_a_device_that_is_not_connected_are_unavailable()
    {
        var actions = new CommandsActions(new DeviceRegistry(), new Store<CommandsMirror>(CommandsMirror.Empty));

        Assert.Equal(ActionOutcome.Unavailable, (await actions.ListAsync(Dev)).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.SetEnabledAsync(Dev, "init", false)).Outcome);
    }
}
