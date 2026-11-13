using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

public class SubagentViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static SubagentRecord Record(SubagentStatus status, DateTimeOffset? ended = null, int tools = 3, string? tool = "Grep") =>
        new("s1", "c1", "code-analyst", "阅读 kernel", status, T0, ended, tools, tool);

    [Fact]
    public void Running_counts_up_and_can_stop()
    {
        var view = SubagentView.Of(Record(SubagentStatus.Running), T0.AddSeconds(75));

        Assert.True(view.IsActive);
        Assert.True(view.CanStop);
        Assert.Equal(SubagentStatus.Running, view.Status);
        Assert.Equal("Grep", view.CurrentTool);
        Assert.Equal("1:15", SubagentView.FormatElapsed(view.Elapsed));
    }

    [Fact]
    public void Terminal_stops_the_clock_at_end_time()
    {
        var view = SubagentView.Of(Record(SubagentStatus.Done, T0.AddSeconds(30)), T0.AddHours(2));

        Assert.False(view.CanStop);
        Assert.Null(view.CurrentTool);
        Assert.Equal(TimeSpan.FromSeconds(30), view.Elapsed);
    }

    [Fact]
    public void Merge_keeps_terminal_state_from_either_side()
    {
        var saved = Record(SubagentStatus.Done, T0.AddSeconds(30), tools: 5);
        var replayedLive = Record(SubagentStatus.Running, tools: 2);

        var merged = SubagentView.Merge(saved, replayedLive);

        Assert.Equal(SubagentStatus.Done, merged.Status);
        Assert.Equal(T0.AddSeconds(30), merged.EndedAt);
        Assert.Null(merged.CurrentTool);
        Assert.Equal(5, merged.ToolCalls);
    }

    [Fact]
    public void Merge_takes_live_terminal_over_saved_running()
    {
        var merged = SubagentView.Merge(Record(SubagentStatus.Running), Record(SubagentStatus.Error, T0.AddSeconds(9)));

        Assert.Equal(SubagentStatus.Error, merged.Status);
    }
}
