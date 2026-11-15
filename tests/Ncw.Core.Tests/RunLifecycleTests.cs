using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>Run 状态的单调性(规则 6、7)和 <c>activeRuns</c> 权威广播的唯一生成函数(规则 3、5)。移植自 2.x <c>active-run-broadcast.test.ts</c> 的口径部分。</summary>
public class RunLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static RunFact Fact(string id, string session = "s", string? parent = null, string? parentSession = null, RunPhase phase = RunPhase.Running, int pending = 0, int sec = 0, string workspace = "w") =>
        new(id, session, workspace, parent, parentSession, phase, T0.AddSeconds(sec), pending);

    [Theory]
    [InlineData(RunPhase.Running, RunPhase.Done, RunPhase.Done)]
    [InlineData(RunPhase.Done, RunPhase.Running, RunPhase.Done)] // 重放的旧事件 / 晚到的旧广播不能把终态改回运行中
    [InlineData(RunPhase.Aborted, RunPhase.Running, RunPhase.Aborted)]
    [InlineData(RunPhase.Error, RunPhase.Running, RunPhase.Error)]
    [InlineData(RunPhase.Unknown, RunPhase.Running, RunPhase.Running)] // 重连后广播把「未知」收口
    [InlineData(RunPhase.Running, RunPhase.Unknown, RunPhase.Running)] // 断线不会把运行中降级
    [InlineData(RunPhase.Done, RunPhase.Aborted, RunPhase.Done)] // 两个终态:先写下的赢
    [InlineData(RunPhase.Aborted, RunPhase.Done, RunPhase.Aborted)]
    [InlineData(RunPhase.Unknown, RunPhase.Aborted, RunPhase.Aborted)]
    public void Merge_is_monotonic_terminal_beats_running_beats_unknown(RunPhase current, RunPhase incoming, RunPhase expected) =>
        Assert.Equal(expected, RunLifecycle.Merge(current, incoming));

    [Fact]
    public void Terminal_phases_are_exactly_done_error_and_aborted()
    {
        Assert.All([RunPhase.Done, RunPhase.Error, RunPhase.Aborted], p => Assert.True(RunLifecycle.IsTerminal(p)));
        Assert.All([RunPhase.Running, RunPhase.Unknown], p => Assert.False(RunLifecycle.IsTerminal(p)));
    }

    [Fact]
    public void Only_running_top_level_runs_are_in_the_runs_half()
    {
        var index = ActiveRunIndexBuilder.Build("dev", [Fact("a"), Fact("b", phase: RunPhase.Done), Fact("c", phase: RunPhase.Aborted)], rev: 7);
        Assert.Equal(["a"], index.Runs.Select(r => r.RunId));
        Assert.Equal(7, index.Rev);
        Assert.Equal("dev", index.DeviceId);
        Assert.Empty(index.Subagents);
    }

    [Fact]
    public void Subagents_are_in_their_own_half_and_never_counted_as_a_session_level_run()
    {
        var index = ActiveRunIndexBuilder.Build("dev", [Fact("p", "ps"), Fact("c", "cs", parent: "p", parentSession: "ps", sec: 1)], 1);
        Assert.Equal(["p"], index.Runs.Select(r => r.RunId));
        var sub = Assert.Single(index.Subagents);
        Assert.Equal(("c", "p", "ps"), (sub.RunId, sub.ParentRunId, sub.SessionId));
    }

    [Fact]
    public void A_background_child_whose_parent_is_gone_falls_back_to_the_parent_session_and_is_not_dropped()
    {
        var index = ActiveRunIndexBuilder.Build("dev", [Fact("c", "cs", parent: "reaped", parentSession: "ps", workspace: "w2")], 1);
        var sub = Assert.Single(index.Subagents);
        Assert.Equal("ps", sub.SessionId);
        Assert.Equal("w2", sub.WorkspaceId);
        Assert.Empty(index.Runs);
    }

    [Fact]
    public void A_child_with_no_parent_session_at_all_is_skipped_rather_than_guessed()
    {
        var index = ActiveRunIndexBuilder.Build("dev", [Fact("c", "cs", parent: "gone")], 1);
        Assert.Empty(index.Subagents);
    }

    [Fact]
    public void Pending_interactions_include_every_descendant_all_the_way_down()
    {
        var facts = new[]
        {
            Fact("p", pending: 1),
            Fact("c", "cs", parent: "p", parentSession: "s", pending: 2),
            Fact("g", "gs", parent: "c", parentSession: "cs", pending: 3),
            Fact("other", "o", pending: 5),
        };
        var index = ActiveRunIndexBuilder.Build("dev", facts, 1);
        Assert.Equal(6, index.Runs.Single(r => r.RunId == "p").PendingInteractions);
        Assert.Equal(5, index.Runs.Single(r => r.RunId == "other").PendingInteractions);
    }

    [Fact]
    public void A_cyclic_parent_link_cannot_hang_the_builder()
    {
        var index = ActiveRunIndexBuilder.Build("dev", [Fact("a", parent: "b", parentSession: "x"), Fact("b", parent: "a", parentSession: "x")], 1);
        Assert.Equal(2, index.Subagents.Length);
    }

    [Fact]
    public void The_result_is_ordered_by_start_time_so_two_builds_of_the_same_facts_are_identical()
    {
        var facts = new[] { Fact("late", sec: 9), Fact("early", sec: 1), Fact("mid", sec: 5) };
        Assert.Equal(["early", "mid", "late"], ActiveRunIndexBuilder.Build("d", facts, 1).Runs.Select(r => r.RunId));
        Assert.Equal(ActiveRunIndexBuilder.Build("d", facts, 1).Runs.Select(r => r.RunId), ActiveRunIndexBuilder.Build("d", [.. facts.Reverse()], 1).Runs.Select(r => r.RunId));
    }

    [Fact]
    public void The_index_feeds_the_same_project_activity_derivation_the_client_uses()
    {
        var index = ActiveRunIndexBuilder.Build("dev", [Fact("a", workspace: "w", pending: 2), Fact("b", workspace: "w2")], 1);
        var activity = ProjectActivityView.Of(new ProjectRef("dev", "w"), index, DeviceConnection.Connected);
        Assert.Equal(new ProjectActivity(1, 2, false), activity);
    }
}
