using System.Collections.Concurrent;

namespace Ncw.Relay;

/// <summary>
/// 近似滑动窗口计数器(当前窗口 + 上一窗口按剩余比例加权),每个 key 只占常数内存。时间用注入的 <see cref="TimeProvider"/>,测试可以拨动。
/// <see cref="TryAcquire"/> 用于「限制次数」(每次占一个名额);<see cref="IsExceeded"/> + <see cref="Record"/> 用于「失败计数」(先查再记)。
/// </summary>
public sealed class RateLimiter(RateRule rule, TimeProvider clock)
{
    private sealed class Counter
    {
        public long WindowStart;
        public int Current;
        public int Previous;
    }

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    public int Tracked => _counters.Count;

    /// <summary>占一个名额;窗口内已经满了返回 false(不占)。</summary>
    public bool TryAcquire(string key)
    {
        var counter = _counters.GetOrAdd(key, _ => new Counter { WindowStart = Now() });
        lock (counter)
        {
            Roll(counter);
            if (Estimate(counter) >= rule.Limit)
            {
                return false;
            }

            counter.Current++;
            return true;
        }
    }

    /// <summary>窗口内的次数是否已经达到上限(不改变计数)。</summary>
    public bool IsExceeded(string key)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            return false;
        }

        lock (counter)
        {
            Roll(counter);
            return Estimate(counter) >= rule.Limit;
        }
    }

    /// <summary>记一次。</summary>
    public void Record(string key)
    {
        var counter = _counters.GetOrAdd(key, _ => new Counter { WindowStart = Now() });
        lock (counter)
        {
            Roll(counter);
            counter.Current++;
        }
    }

    /// <summary>清掉已经过期两个窗口的 key(定时调用,防止 key 越积越多)。</summary>
    public void Sweep()
    {
        var now = Now();
        foreach (var (key, counter) in _counters)
        {
            lock (counter)
            {
                if (now - counter.WindowStart >= 2 * rule.Window.Ticks)
                {
                    _counters.TryRemove(new KeyValuePair<string, Counter>(key, counter));
                }
            }
        }
    }

    private long Now() => clock.GetTimestamp() * TimeSpan.TicksPerSecond / clock.TimestampFrequency;

    private void Roll(Counter counter)
    {
        var elapsed = Now() - counter.WindowStart;
        if (elapsed < rule.Window.Ticks)
        {
            return;
        }

        counter.Previous = elapsed >= 2 * rule.Window.Ticks ? 0 : counter.Current;
        counter.Current = 0;
        counter.WindowStart += elapsed / rule.Window.Ticks * rule.Window.Ticks;
    }

    private double Estimate(Counter counter)
    {
        var fraction = (double)(Now() - counter.WindowStart) / rule.Window.Ticks;
        return counter.Current + counter.Previous * Math.Max(0, 1 - fraction);
    }
}

/// <summary>每个 key 的并发数上限(例如每个来源 IP 同时打开的连接)。</summary>
public sealed class ConcurrencyGate(int limit)
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public int Tracked => _counts.Count;

    /// <summary>进入;满了返回 null。释放返回值归还名额。</summary>
    public IDisposable? TryEnter(string key)
    {
        var admitted = false;
        _counts.AddOrUpdate(key,
            _ =>
            {
                admitted = limit > 0;
                return admitted ? 1 : 0;
            },
            (_, n) =>
            {
                admitted = n < limit;
                return admitted ? n + 1 : n;
            });
        return admitted ? new Lease(this, key) : null;
    }

    private void Leave(string key)
    {
        while (true)
        {
            if (!_counts.TryGetValue(key, out var n))
            {
                return;
            }

            if (n <= 1)
            {
                if (_counts.TryRemove(new KeyValuePair<string, int>(key, n)))
                {
                    return;
                }
            }
            else if (_counts.TryUpdate(key, n - 1, n))
            {
                return;
            }
        }
    }

    private sealed class Lease(ConcurrencyGate gate, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Leave(key);
            }
        }
    }
}
