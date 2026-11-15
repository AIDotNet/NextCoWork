namespace Ncw.Agent.Host.Tests.Support;

/// <summary>手动推进的时钟:定时器只在 <see cref="Advance"/> 时触发。注入 <c>AgentHost</c> 后,定时任务的到点触发不睡真的时间。</summary>
public sealed class ManualClock(DateTimeOffset origin) : TimeProvider
{
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public long Period = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._now + (long)dueTime.TotalMilliseconds;
                Period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return origin.AddMilliseconds(_now);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + (long)by.TotalMilliseconds;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = Math.Max(_now, next.Due);
                if (next.Period > 0)
                {
                    next.Due += next.Period;
                }
                else
                {
                    next.Due = long.MaxValue;
                }
            }

            next.Fire();
        }
    }
}
