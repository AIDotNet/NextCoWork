using Ncw.Mobile;

namespace Ncw.Mobile.Shared.Tests;

/// <summary>监督器:事件 → 策略 → 控制器动作;后台宽限期靠计时器触发挂起。</summary>
public class ConnectionSupervisorTests
{
    private sealed class Recorder : IConnectionController
    {
        public List<string> Calls { get; } = [];

        public Task SuspendAsync()
        {
            Calls.Add("suspend");
            return Task.CompletedTask;
        }

        public void Resume() => Calls.Add("resume");

        public Task ReconnectAsync()
        {
            Calls.Add("reconnect");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Staying_in_the_background_past_the_grace_period_suspends_via_the_timer_then_the_foreground_resumes()
    {
        var time = new FakeTime();
        var recorder = new Recorder();
        await using var supervisor = new ConnectionSupervisor(recorder, new ConnectionPolicyOptions { BackgroundGrace = TimeSpan.FromSeconds(60) }, time);

        await supervisor.EnteredBackgroundAsync();
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(recorder.Calls);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => recorder.Calls.Count == 1);
        Assert.Equal(["suspend"], recorder.Calls);

        await supervisor.EnteredForegroundAsync();
        Assert.Equal(["suspend", "resume"], recorder.Calls);
    }

    [Fact]
    public async Task Returning_before_the_grace_period_cancels_the_timer()
    {
        var time = new FakeTime();
        var recorder = new Recorder();
        await using var supervisor = new ConnectionSupervisor(recorder, new ConnectionPolicyOptions { BackgroundGrace = TimeSpan.FromSeconds(60) }, time);

        await supervisor.EnteredBackgroundAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await supervisor.EnteredForegroundAsync();
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task Network_events_drive_suspend_resume_and_reconnect()
    {
        var recorder = new Recorder();
        await using var supervisor = new ConnectionSupervisor(recorder, time: new FakeTime());

        await supervisor.NetworkAvailableAsync("wifi");
        await supervisor.NetworkLostAsync("wifi");
        await supervisor.NetworkAvailableAsync("wifi");
        await supervisor.NetworkAvailableAsync("cell");

        Assert.Equal(["suspend", "resume"], recorder.Calls); // 第二个 available 在去抖窗口内
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>手动推进的时间源,计时器在 <see cref="Advance"/> 到点时同步触发。</summary>
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        private readonly List<FakeTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(_now);
            }
        }

        private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool _done;

            public void FireIfDue(DateTimeOffset now)
            {
                if (!_done && now >= due)
                {
                    _done = true;
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
                _done = true;
                owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
