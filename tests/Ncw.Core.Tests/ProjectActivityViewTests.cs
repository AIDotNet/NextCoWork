using System.Collections.Immutable;
using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

public class ProjectActivityViewTests
{
    private static readonly ProjectRef Project = new("local", "ws1");

    private static ActiveRunIndex Index(params ActiveRun[] runs) => new("local", runs.ToImmutableArray());

    [Fact]
    public void Counts_runs_and_pending_interactions_of_the_project_only()
    {
        var index = Index(
            new ActiveRun("local", "r1", "s1", "ws1", 2),
            new ActiveRun("local", "r2", "s2", "ws1", 0),
            new ActiveRun("local", "r3", "s3", "ws2", 5));

        var activity = ProjectActivityView.Of(Project, index, DeviceConnection.Connected);

        Assert.Equal(new ProjectActivity(2, 2, false), activity);
        Assert.True(activity.IsRunning);
    }

    [Fact]
    public void Offline_device_with_known_runs_is_unknown_not_finished_and_not_running()
    {
        var index = Index(new ActiveRun("local", "r1", "s1", "ws1", 1));

        var activity = ProjectActivityView.Of(Project, index, DeviceConnection.Offline);

        Assert.True(activity.Unknown);
        Assert.False(activity.IsRunning);
        Assert.Equal(0, activity.PendingInteractions);
    }

    [Fact]
    public void Index_from_another_device_is_ignored()
    {
        var index = new ActiveRunIndex("cloud", [new ActiveRun("cloud", "r1", "s1", "ws1", 3)]);

        Assert.Equal(ProjectActivity.Idle, ProjectActivityView.Of(Project, index, DeviceConnection.Connected));
    }

    [Fact]
    public void Group_badge_is_the_sum_of_members()
    {
        var sum = ProjectActivityView.Sum([new(1, 2, false), new(0, 0, true), new(3, 1, false)]);

        Assert.Equal(new ProjectActivity(4, 3, true), sum);
    }
}
