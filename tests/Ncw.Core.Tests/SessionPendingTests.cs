using System.Collections.Immutable;
using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>侧边栏会话行的「n 待处理」(docs/design/03 §1「左侧边栏」):只来自 activeRuns 广播,断线时不当现状。</summary>
public class SessionPendingTests
{
    private static ActiveRunIndex Index(params ActiveRun[] runs) => new("d1", [.. runs]);

    [Fact]
    public void Sums_the_pending_interactions_of_the_session_runs_only()
    {
        var index = Index(
            new ActiveRun("d1", "r1", "s1", "w1", 2),
            new ActiveRun("d1", "r2", "s1", "w1", 1),
            new ActiveRun("d1", "r3", "s2", "w1", 5));

        Assert.Equal(3, SessionActivityView.PendingOf("s1", index, DeviceConnection.Connected));
        Assert.Equal(5, SessionActivityView.PendingOf("s2", index, DeviceConnection.Connected));
        Assert.Equal(0, SessionActivityView.PendingOf("s3", index, DeviceConnection.Connected));
    }

    [Theory]
    [InlineData(DeviceConnection.Offline)]
    [InlineData(DeviceConnection.Connecting)]
    public void Is_zero_while_the_device_is_not_connected(DeviceConnection connection)
    {
        var index = Index(new ActiveRun("d1", "r1", "s1", "w1", 2));
        Assert.Equal(0, SessionActivityView.PendingOf("s1", index, connection));
    }

    [Fact]
    public void Is_zero_without_an_index()
    {
        Assert.Equal(0, SessionActivityView.PendingOf("s1", null, DeviceConnection.Connected));
        Assert.Equal(0, SessionActivityView.PendingOf("s1", new ActiveRunIndex("d1", ImmutableArray<ActiveRun>.Empty), DeviceConnection.Connected));
    }
}
