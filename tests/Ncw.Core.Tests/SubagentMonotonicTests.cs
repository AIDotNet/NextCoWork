using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>
/// 子代理状态单调(AGENTS.md §3 规则 6)与展示投影的补充用例。移植自 2.x 的 <c>subagent-monotonic.test.ts</c>、<c>subagent-view.test.ts</c>:
/// 症状是漏掉一条子 Run 的结束事件之后,卡片永远「运行中」,而点开子会话(读库)看到的是「已完成」;或者一次 attach 重放把早已完成的卡片改回了 running。
/// </summary>
public class SubagentMonotonicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static SubagentRecord Card(SubagentStatus status = SubagentStatus.Running, string? tool = "Bash", int tools = 7) =>
        new("parent", "task-1", "researcher", "find readers", status, T0, null, tools, tool) { ChildRunId = "child-1", Background = true };

    [Fact]
    public void A_terminal_receipt_from_the_database_wins_over_a_stale_live_running()
    {
        var saved = Card(SubagentStatus.Done, tool: null, tools: 0) with { EndedAt = T0.AddSeconds(5) };

        var merged = SubagentView.Merge(saved, Card());

        Assert.Equal(SubagentStatus.Done, merged.Status);
        Assert.Null(merged.CurrentTool);
        Assert.Equal(7, merged.ToolCalls); // 遥测只有实时那份有,照样留着
    }

    [Fact]
    public void Live_telemetry_is_kept_when_both_sides_agree_it_is_running()
    {
        var merged = SubagentView.Merge(Card(tool: null, tools: 0), Card());

        Assert.Equal(SubagentStatus.Running, merged.Status);
        Assert.Equal("Bash", merged.CurrentTool);
        Assert.Equal(7, merged.ToolCalls);
    }

    [Fact]
    public void A_live_terminal_state_wins_over_a_saved_running_receipt() =>
        Assert.Equal(SubagentStatus.Aborted, SubagentView.Merge(Card(), Card(SubagentStatus.Aborted)).Status);

    [Theory]
    [InlineData(SubagentStatus.Queued, SubagentStatus.Running, SubagentStatus.Running)]
    [InlineData(SubagentStatus.Running, SubagentStatus.Queued, SubagentStatus.Running)]
    [InlineData(SubagentStatus.Running, SubagentStatus.Done, SubagentStatus.Done)]
    [InlineData(SubagentStatus.Done, SubagentStatus.Running, SubagentStatus.Done)]
    [InlineData(SubagentStatus.Error, SubagentStatus.Queued, SubagentStatus.Error)]
    [InlineData(SubagentStatus.Done, SubagentStatus.Error, SubagentStatus.Done)] // 同为终态:先落下的不被改写
    [InlineData(SubagentStatus.Error, SubagentStatus.Done, SubagentStatus.Error)]
    public void Status_rank_is_queued_then_running_then_terminal_whichever_side_arrives_later(SubagentStatus saved, SubagentStatus live, SubagentStatus expected) =>
        Assert.Equal(expected, SubagentView.MergeStatus(saved, live));

    [Fact]
    public void A_replayed_subagent_start_does_not_revive_a_finished_card()
    {
        var finished = Card(SubagentStatus.Done, tool: null) with { EndedAt = T0.AddSeconds(9), ChildSessionId = null };
        var replayed = Card() with { ChildSessionId = "parent:sub:child-1" };

        var next = SubagentView.ApplyStart(finished, replayed);

        Assert.Equal(SubagentStatus.Done, next.Status);
        Assert.Equal(T0.AddSeconds(9), next.EndedAt);
        Assert.Equal("parent:sub:child-1", next.ChildSessionId); // 描述性字段照补
    }

    [Fact]
    public void Replayed_telemetry_is_ignored_once_the_card_is_terminal()
    {
        var finished = Card(SubagentStatus.Done, tool: null);

        var next = SubagentView.ApplyUpdate(finished, 99, Patch<string>.Set("Read"), Patch<string>.Keep, T0.AddSeconds(30));

        Assert.Equal(finished, next);
    }

    [Fact]
    public void A_second_terminal_state_does_not_overwrite_the_first()
    {
        var done = SubagentView.ApplyEnd(Card(), SubagentStatus.Done, T0.AddSeconds(3));

        var again = SubagentView.ApplyEnd(done, SubagentStatus.Aborted, T0.AddSeconds(8));

        Assert.Equal(SubagentStatus.Done, again.Status);
        Assert.Equal(T0.AddSeconds(3), again.EndedAt);
        Assert.Null(again.CurrentTool);
    }

    [Fact]
    public void A_queued_card_becomes_running_on_the_first_telemetry_and_never_goes_back()
    {
        var queued = Card(SubagentStatus.Queued, tool: null, tools: 0);

        var running = SubagentView.ApplyUpdate(queued, 1, Patch<string>.Set("Read"), Patch<string>.Set("a.ts"), T0.AddSeconds(1));
        var restarted = SubagentView.ApplyStart(running, queued);

        Assert.Equal(SubagentStatus.Running, running.Status);
        Assert.Equal(SubagentStatus.Running, restarted.Status);
    }

    [Fact]
    public void An_update_key_that_is_present_but_empty_clears_and_an_absent_key_keeps()
    {
        var card = Card() with { CurrentTarget = "a.ts" };

        var kept = SubagentView.ApplyUpdate(card, null, Patch<string>.Keep, Patch<string>.Keep, null);
        var cleared = SubagentView.ApplyUpdate(card, null, Patch<string>.Clear, Patch<string>.Keep, null);

        Assert.Equal("Bash", kept.CurrentTool);
        Assert.Equal("a.ts", kept.CurrentTarget);
        Assert.Null(cleared.CurrentTool);
        Assert.Equal("a.ts", cleared.CurrentTarget);
    }

    [Fact]
    public void Reported_is_the_end_of_the_report_progress_and_is_never_pulled_back()
    {
        var saved = Card(SubagentStatus.Done) with { ReportStatus = SubagentReportStatus.Reported };
        var live = Card(SubagentStatus.Done) with { ReportStatus = SubagentReportStatus.Pending };

        Assert.Equal(SubagentReportStatus.Reported, SubagentView.Merge(saved, live).ReportStatus);
        Assert.Equal(SubagentReportStatus.Reported, SubagentView.Merge(live, saved).ReportStatus);
        Assert.Equal(SubagentReportStatus.Injecting, SubagentView.Merge(live, live with { ReportStatus = SubagentReportStatus.Injecting }).ReportStatus);
    }

    // ───────────────────────── subagent-view.test.ts ─────────────────────────

    [Fact]
    public void A_running_agent_counts_up_to_now() =>
        Assert.Equal(TimeSpan.FromSeconds(3), SubagentView.Of(Card(), T0.AddSeconds(3)).Elapsed);

    [Fact]
    public void A_finished_agent_never_keeps_ticking_when_the_end_time_is_missing()
    {
        // 胶囊以前用 endedAt ?? now:从库里回执重建的终态卡片没有 endedAt,计时一直在走
        var withLastEvent = Card(SubagentStatus.Done) with { LastEventAt = T0.AddSeconds(2) };

        Assert.Equal(TimeSpan.FromSeconds(2), SubagentView.Of(withLastEvent, T0.AddSeconds(99)).Elapsed);
        Assert.Equal(TimeSpan.Zero, SubagentView.Of(Card(SubagentStatus.Done), T0.AddSeconds(99)).Elapsed);
    }

    [Fact]
    public void Still_streaming_is_told_apart_from_an_old_transcript_without_a_record()
    {
        Assert.Equal(SubagentStatus.Queued, SubagentView.WhenMissing(argumentsStreaming: true));
        Assert.Equal(SubagentStatus.Done, SubagentView.WhenMissing(argumentsStreaming: false));
    }

    [Fact]
    public void Stop_and_background_are_offered_only_while_running_and_background_only_for_foreground_agents()
    {
        var foreground = Card() with { Background = false };

        var running = SubagentView.Of(foreground, T0);
        Assert.True(running.CanStop);
        Assert.True(running.CanBackground);

        var moved = SubagentView.Of(Card(), T0);
        Assert.True(moved.CanStop);
        Assert.False(moved.CanBackground);

        var aborted = SubagentView.Of(Card(SubagentStatus.Aborted), T0);
        Assert.False(aborted.CanStop);
        Assert.False(aborted.CanBackground);
    }

    [Fact]
    public void A_report_exists_only_for_background_agents()
    {
        Assert.Equal(SubagentReportStatus.None, SubagentView.Of(Card(SubagentStatus.Done) with { Background = false }, T0).Report);
        Assert.Equal(SubagentReportStatus.Pending, SubagentView.Of(Card(SubagentStatus.Done), T0).Report);
        Assert.Equal(SubagentReportStatus.Reported, SubagentView.Of(Card(SubagentStatus.Done) with { ReportStatus = SubagentReportStatus.Reported }, T0).Report);
        Assert.Equal(SubagentReportStatus.None, SubagentView.Of(Card(), T0).Report);
        Assert.Equal(SubagentReportStatus.Reported, SubagentView.Of(Card(SubagentStatus.Aborted), T0).Report);
    }

    [Fact]
    public void The_current_tool_and_target_show_only_while_running()
    {
        var running = SubagentView.Of(Card() with { CurrentTarget = "a.ts" }, T0);
        var done = SubagentView.Of(Card(SubagentStatus.Done) with { CurrentTarget = "a.ts" }, T0);

        Assert.Equal(("Bash", "a.ts"), (running.CurrentTool, running.CurrentTarget));
        Assert.Equal((null, null), (done.CurrentTool, done.CurrentTarget));
    }

    [Fact]
    public void Only_a_card_with_a_child_session_can_be_opened()
    {
        Assert.False(SubagentView.Of(Card(), T0).CanOpen);
        Assert.True(SubagentView.Of(Card() with { ChildSessionId = "parent:sub:child-1" }, T0).CanOpen);
    }

    [Fact]
    public void Running_agents_sort_first_then_newest()
    {
        var list = new[]
        {
            Card(SubagentStatus.Done) with { CallId = "old-done", StartedAt = T0.AddSeconds(1) },
            Card(SubagentStatus.Done) with { CallId = "new-done", StartedAt = T0.AddSeconds(5) },
            Card() with { CallId = "old-run", StartedAt = T0.AddSeconds(2) },
        };

        var sorted = list.Order(Comparer<SubagentRecord>.Create(SubagentView.CompareByRecency)).Select(r => r.CallId);

        Assert.Equal(["old-run", "new-done", "old-done"], sorted);
    }

    [Fact]
    public void Wire_names_round_trip()
    {
        foreach (var status in Enum.GetValues<SubagentStatus>())
        {
            Assert.Equal(status, SubagentView.ParseStatus(SubagentView.ToWire(status)));
        }

        foreach (var report in Enum.GetValues<SubagentReportStatus>())
        {
            Assert.Equal(report, SubagentView.ParseReportStatus(SubagentView.ToWire(report)));
        }

        Assert.Null(SubagentView.ParseStatus("weird"));
        Assert.Null(SubagentView.ParseReportStatus(null));
    }
}
