using Ncw.Mobile;

namespace Ncw.Mobile.Shared.Tests;

/// <summary>后台 / 网络切换的连接策略(纯状态机)。</summary>
public class ConnectionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static ConnectionPolicy Make() => new(new ConnectionPolicyOptions
    {
        BackgroundGrace = TimeSpan.FromSeconds(60),
        StaleAfter = TimeSpan.FromSeconds(20),
        Debounce = TimeSpan.FromSeconds(1.5),
    });

    [Fact]
    public void A_short_trip_to_the_background_keeps_the_connection_untouched()
    {
        var p = Make();

        Assert.Equal(ConnectionAction.None, p.OnBackground(At(0)));
        Assert.Equal(ConnectionAction.None, p.OnTick(At(30)));
        Assert.Equal(ConnectionAction.None, p.OnForeground(At(5)));
        Assert.False(p.IsSuspended);
    }

    [Fact]
    public void A_medium_trip_reconnects_on_return_without_ever_suspending()
    {
        var p = Make();
        p.OnBackground(At(0));

        Assert.Equal(ConnectionAction.Reconnect, p.OnForeground(At(30)));
    }

    [Fact]
    public void Staying_in_the_background_past_the_grace_period_suspends_and_returning_resumes()
    {
        var p = Make();
        p.OnBackground(At(0));

        Assert.Equal(At(60), p.SuspendDeadline);
        Assert.Equal(ConnectionAction.None, p.OnTick(At(59)));
        Assert.Equal(ConnectionAction.Suspend, p.OnTick(At(60)));
        Assert.True(p.IsSuspended);
        Assert.Null(p.SuspendDeadline);
        Assert.Equal(ConnectionAction.None, p.OnTick(At(120))); // 只挂起一次

        Assert.Equal(ConnectionAction.Resume, p.OnForeground(At(300)));
        Assert.False(p.IsSuspended);
    }

    [Fact]
    public void Losing_the_network_suspends_immediately_so_devices_show_unknown()
    {
        var p = Make();
        p.OnNetworkAvailable("wifi", At(0));

        Assert.Equal(ConnectionAction.Suspend, p.OnNetworkLost("wifi"));
        Assert.Equal(ConnectionAction.Resume, p.OnNetworkAvailable("wifi", At(10)));
    }

    [Fact]
    public void Switching_networks_reconnects_and_the_late_loss_of_the_old_network_is_ignored()
    {
        var p = Make();
        p.OnNetworkAvailable("wifi", At(0)); // 基线,不触发

        Assert.Equal(ConnectionAction.Reconnect, p.OnNetworkAvailable("cell", At(10)));
        Assert.Equal(ConnectionAction.None, p.OnNetworkLost("wifi"));
        Assert.True(p.IsNetworkUp);
    }

    [Fact]
    public void The_first_network_callback_is_only_a_baseline()
    {
        var p = Make();

        Assert.Equal(ConnectionAction.None, p.OnNetworkAvailable("wifi", At(0)));
    }

    [Fact]
    public void Repeated_available_callbacks_within_the_debounce_window_reconnect_once()
    {
        var p = Make();
        p.OnNetworkAvailable("a", At(0));

        Assert.Equal(ConnectionAction.Reconnect, p.OnNetworkAvailable("b", At(10)));
        Assert.Equal(ConnectionAction.None, p.OnNetworkAvailable("c", At(10.5)));
        Assert.Equal(ConnectionAction.Reconnect, p.OnNetworkAvailable("d", At(12)));
    }

    [Fact]
    public void A_network_switch_in_the_background_is_acted_on_when_the_app_returns()
    {
        var p = Make();
        p.OnNetworkAvailable("wifi", At(0));
        p.OnBackground(At(1));

        Assert.Equal(ConnectionAction.None, p.OnNetworkAvailable("cell", At(2)));
        Assert.Equal(ConnectionAction.Reconnect, p.OnForeground(At(3))); // 离开才 2 秒,但网络换过
    }

    [Fact]
    public void Network_loss_while_suspended_in_the_background_resumes_only_when_it_is_back_and_the_app_is_foreground()
    {
        var p = Make();
        p.OnNetworkAvailable("wifi", At(0));
        p.OnBackground(At(1));
        p.OnTick(At(61)); // Suspend
        Assert.Equal(ConnectionAction.None, p.OnNetworkLost("wifi"));

        Assert.Equal(ConnectionAction.None, p.OnForeground(At(100))); // 没网:等
        Assert.Equal(ConnectionAction.Resume, p.OnNetworkAvailable("wifi", At(110)));
    }

    [Fact]
    public void A_network_recovery_in_the_background_while_suspended_waits_for_the_foreground()
    {
        var p = Make();
        p.OnNetworkAvailable("wifi", At(0));
        p.OnBackground(At(1));
        Assert.Equal(ConnectionAction.Suspend, p.OnNetworkLost("wifi")); // 后台丢网:立刻挂起
        Assert.Equal(ConnectionAction.None, p.OnNetworkAvailable("wifi", At(5)));

        Assert.Equal(ConnectionAction.Resume, p.OnForeground(At(8)));
    }

    [Fact]
    public void Duplicate_lifecycle_events_are_harmless()
    {
        var p = Make();

        Assert.Equal(ConnectionAction.None, p.OnForeground(At(0)));
        p.OnBackground(At(1));
        Assert.Equal(ConnectionAction.None, p.OnBackground(At(50))); // 不重置起点
        Assert.Equal(At(61), p.SuspendDeadline);
    }
}
