using System.Collections.Immutable;
using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>活动收件箱的聚合与排序(docs/design/03 §3):按设备分组、待办在前、危险审批最先、断线设备是「未知」而不是「已处理」(规则 7)。</summary>
public class ActivityInboxTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static long Ms(int secondsAgo) => T0.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds();

    private static ActiveRunIndex Index(string device, params (string Run, string Session, string Workspace)[] runs) =>
        new(device, [.. runs.Select(r => new ActiveRun(device, r.Run, r.Session, r.Workspace, 1))]);

    private static InboxInteraction Tool(string id, string run, int secondsAgo, bool dangerous = false) =>
        new(id, run, InboxKind.ToolApproval, Ms(secondsAgo), dangerous);

    private static InboxDevice Device(string id, string name, DeviceConnection connection, ActiveRunIndex? index = null,
        InboxInteraction[]? interactions = null, InboxFinishedRun[]? finished = null) =>
        new(id, name, connection, index, [.. interactions ?? []], [.. finished ?? []]);

    private static InboxDevice Connected(string id, string name, ActiveRunIndex? index = null, InboxInteraction[]? interactions = null, InboxFinishedRun[]? finished = null) =>
        Device(id, name, DeviceConnection.Connected, index, interactions, finished);

    [Fact]
    public void Nothing_pending_on_connected_devices_is_an_empty_inbox()
    {
        var inbox = ActivityInboxView.Build([Connected("a", "本机"), Connected("b", "云端")]);

        Assert.True(inbox.IsEmpty);
        Assert.Equal(0, inbox.BadgeCount);
        Assert.False(inbox.HasUnknown);
    }

    [Fact]
    public void Entries_are_grouped_by_device_and_resolved_to_their_session_and_project()
    {
        var inbox = ActivityInboxView.Build(
        [
            Connected("a", "本机", Index("a", ("r1", "s1", "ws1")), [Tool("i1", "r1", 30)]),
            Connected("b", "工作室", Index("b", ("r2", "s2", "ws2")), [Tool("i2", "r2", 10)]),
        ]);

        Assert.Equal(["a", "b"], inbox.Groups.Select(g => g.DeviceId));
        var first = Assert.Single(inbox.Groups[0].Entries);
        Assert.Equal(("s1", "ws1", "i1"), (first.SessionId, first.WorkspaceId, first.InteractionId));
        Assert.Equal(2, inbox.BadgeCount);
    }

    [Fact]
    public void A_subagent_approval_belongs_to_the_parent_session()
    {
        var index = Index("a", ("parent", "s1", "ws1")) with
        {
            Subagents = [new ActiveSubagent("a", "child", "parent", "s1", "ws1", T0)],
        };

        var entry = ActivityInboxView.Build([Connected("a", "本机", index, [Tool("i1", "child", 5)])]).Entries.Single();

        Assert.Equal("s1", entry.SessionId);
    }

    [Fact]
    public void A_device_level_interaction_has_no_session_and_a_goal_proposal_uses_its_own_session_hint()
    {
        var inbox = ActivityInboxView.Build([Connected("a", "本机", null,
        [
            new InboxInteraction("ssh1", "env:dev:connect", InboxKind.DeviceApproval, Ms(5)),
            new InboxInteraction("g1", "gone-run", InboxKind.GoalProposal, Ms(6), SessionHint: "s9"),
        ])]);

        Assert.Null(inbox.Entries.Single(e => e.InteractionId == "ssh1").SessionId);
        Assert.Equal("s9", inbox.Entries.Single(e => e.InteractionId == "g1").SessionId);
    }

    [Fact]
    public void Within_a_device_dangerous_approvals_come_first_then_older_before_newer_then_kinds_in_priority_order_and_notifications_last()
    {
        var finished = new[] { new InboxFinishedRun("done1", "s1", false, T0.AddMinutes(-5)), new InboxFinishedRun("bad1", "s1", true, T0.AddMinutes(-20)) };
        var inbox = ActivityInboxView.Build([Connected("a", "本机", Index("a", ("r1", "s1", "ws1")),
        [
            new InboxInteraction("goal", "r1", InboxKind.GoalProposal, Ms(500)),
            new InboxInteraction("plan", "r1", InboxKind.PlanApproval, Ms(400)),
            new InboxInteraction("ask", "r1", InboxKind.Question, Ms(300)),
            Tool("new-safe", "r1", 10),
            Tool("old-safe", "r1", 200),
            Tool("danger", "r1", 5, dangerous: true),
        ], finished)]);

        Assert.Equal(["danger", "old-safe", "new-safe", "ask", "plan", "goal", null, null], inbox.Groups[0].Entries.Select(e => e.InteractionId));
        Assert.Equal(["bad1", "done1"], inbox.Groups[0].Entries.Skip(6).Select(e => e.RunId)); // 失败的在前
    }

    [Fact]
    public void Devices_with_actionable_items_come_first_oldest_waiting_first_then_by_name()
    {
        var inbox = ActivityInboxView.Build(
        [
            Connected("z", "Z 机", null, finished: [new InboxFinishedRun("r", "s", false, T0)]),
            Connected("b", "B 机", Index("b", ("rb", "s", "w")), [Tool("ib", "rb", 10)]),
            Connected("a", "A 机", Index("a", ("ra", "s", "w")), [Tool("ia", "ra", 100)]),
        ]);

        Assert.Equal(["a", "b", "z"], inbox.Groups.Select(g => g.DeviceId));
    }

    [Fact]
    public void A_disconnected_device_is_unknown_its_items_are_not_actionable_and_do_not_count_toward_the_badge()
    {
        var inbox = ActivityInboxView.Build(
        [
            Connected("a", "本机", Index("a", ("r1", "s1", "w")), [Tool("live", "r1", 10)]),
            Device("b", "工作室", DeviceConnection.Offline, Index("b", ("r2", "s2", "w")), [Tool("stale", "r2", 100)]),
        ]);

        var offline = inbox.Groups.Single(g => g.DeviceId == "b");
        Assert.True(offline.Unknown);
        Assert.True(offline.Entries.Single().Unknown);
        Assert.False(offline.Entries.Single().IsActionable);
        Assert.Equal(1, inbox.BadgeCount);
        Assert.True(inbox.HasUnknown);
        Assert.Equal("a", inbox.Groups[0].DeviceId); // 现在能处理的排在前面
    }

    [Fact]
    public void A_disconnected_device_with_nothing_remembered_still_appears_as_unknown_but_a_connected_empty_one_does_not()
    {
        var inbox = ActivityInboxView.Build(
        [
            Device("cloud", "云端", DeviceConnection.Sleeping),
            Connected("a", "本机"),
        ]);

        var group = Assert.Single(inbox.Groups);
        Assert.Equal("cloud", group.DeviceId);
        Assert.True(group.Unknown);
        Assert.Empty(group.Entries);
        Assert.False(inbox.IsEmpty);
    }

    [Theory]
    [InlineData(DeviceConnection.Connecting)]
    [InlineData(DeviceConnection.Offline)]
    [InlineData(DeviceConnection.Sleeping)]
    [InlineData(DeviceConnection.Revoked)]
    public void Every_non_connected_state_makes_pending_items_unknown(DeviceConnection connection)
    {
        var inbox = ActivityInboxView.Build([Device("a", "x", connection, Index("a", ("r", "s", "w")), [Tool("i", "r", 1)])]);

        Assert.True(inbox.Entries.Single().Unknown);
        Assert.Equal(0, inbox.BadgeCount);
    }

    [Fact]
    public void Finished_run_notifications_survive_a_disconnect_because_a_terminal_state_is_a_fact_and_dismissed_ones_are_hidden()
    {
        var finished = new[] { new InboxFinishedRun("r1", "s1", false, T0), new InboxFinishedRun("r2", "s1", true, T0) };

        var offline = ActivityInboxView.Build([Device("a", "x", DeviceConnection.Offline, null, null, finished)]);
        Assert.All(offline.Entries, e => Assert.False(e.Unknown));
        Assert.Equal(2, offline.Entries.Count());

        var dismissed = ActivityInboxView.Build([Connected("a", "x", null, null, finished)], new HashSet<string> { "r:r1" });
        Assert.Equal("r2", dismissed.Entries.Single().RunId);
        Assert.Equal(InboxKind.RunFailed, dismissed.Entries.Single().Kind);
        Assert.Equal(0, dismissed.BadgeCount); // 通知不是待办
    }

    [Fact]
    public void Running_run_count_comes_from_the_authoritative_index()
    {
        var inbox = ActivityInboxView.Build([Connected("a", "x", Index("a", ("r1", "s", "w"), ("r2", "s2", "w")), [Tool("i", "r1", 1)])]);

        Assert.Equal(2, inbox.Groups[0].RunningRuns);
    }

    [Fact]
    public void The_result_is_a_pure_function_of_its_inputs()
    {
        InboxDevice[] input = [Connected("a", "x", Index("a", ("r", "s", "w")), [Tool("i1", "r", 9), Tool("i2", "r", 8, true)])];

        var first = ActivityInboxView.Build(input);
        var second = ActivityInboxView.Build(input);

        Assert.Equal(first.Entries.Select(e => e.Key), second.Entries.Select(e => e.Key));
        Assert.Equal(ImmutableArray.Create("i:i2", "i:i1"), first.Entries.Select(e => e.Key).ToImmutableArray());
    }
}
