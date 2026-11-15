namespace Ncw.Protocol.Tests;

/// <summary>
/// 心跳测试用的手动时钟(同时被 Ncw.Integration.Tests 以链接方式引用):时间只在 <see cref="Advance"/> 时前进,
/// 定时器(<c>Task.Delay(…, clock)</c>)按到期顺序在那一刻触发。没有 sleep:测试要什么时候「过了 N 秒」,就自己推进 N 秒。
/// </summary>
internal sealed class HeartbeatClock : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now; // 毫秒

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Volatile.Read(ref _now);

    public override DateTimeOffset GetUtcNow() => Origin.AddMilliseconds(Volatile.Read(ref _now));

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

    /// <summary>当前挂着的(还没触发也没释放的)定时器数。</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Armed);
            }
        }
    }

    /// <summary>推进时间,按到期顺序依次触发定时器(触发时钟读数就是它的到期时刻);推进完时钟停在目标时刻。</summary>
    public void Advance(TimeSpan by)
    {
        var target = Volatile.Read(ref _now) + (long)by.TotalMilliseconds;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Armed && t.DueAt <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    Volatile.Write(ref _now, target);
                    return;
                }

                Volatile.Write(ref _now, Math.Max(_now, next.DueAt));
            }

            next.Fire();
        }
    }

    /// <summary>等到至少挂着 <paramref name="count"/> 个定时器(心跳的 ping 和看门狗各一个):上一轮触发的循环重新挂上定时器之后才能安全地继续推进。</summary>
    public async Task WaitForTimersAsync(int count)
    {
        var until = Environment.TickCount64 + 15_000;
        while (PendingTimers < count)
        {
            if (Environment.TickCount64 > until)
            {
                throw new TimeoutException($"Expected {count} armed timers, have {PendingTimers}");
            }

            await Task.Delay(1);
        }
    }

    private sealed class ManualTimer(HeartbeatClock owner, TimerCallback callback, object? state) : ITimer
    {
        private long _period = -1;

        public long DueAt { get; private set; } = -1;

        public bool Armed => DueAt >= 0;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? -1 : Volatile.Read(ref owner._now) + (long)dueTime.TotalMilliseconds;
                _period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (!Armed)
                {
                    return;
                }

                DueAt = _period > 0 ? Volatile.Read(ref owner._now) + _period : -1;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = -1;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// 「对端静默消失」(NAT 超时、切网、中间设备丢包):<see cref="BlackHole"/> 之后读永远挂着(不返回数据、不返回 EOF、不抛错),
/// 写被悄悄吞掉,下面的真实连接保持打开。释放才会让挂着的读结束。包在真实的局域网 / 中继字节流外面就能确定地复现这种断线,
/// 不靠真的断网、也不靠负载和睡眠。
/// </summary>
internal sealed class BlackHoleStream(Stream inner) : Stream
{
    private readonly CancellationTokenSource _disposed = new();
    private volatile bool _hole;

    /// <summary>从这一刻起对端「消失」。</summary>
    public void BlackHole() => _hole = true;

    public bool IsDisposed => _disposed.IsCancellationRequested;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (true)
        {
            if (_hole)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
                try
                {
                    await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_disposed.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(BlackHoleStream));
                }
            }

            int n;
            try
            {
                n = await inner.ReadAsync(destination, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (_hole && e is IOException or ObjectDisposedException)
            {
                continue; // 黑洞不转交错误
            }

            if (!_hole)
            {
                return n;
            }

            // 黑洞开启之前就挂着的读此刻返回了:数据和 EOF 一律吞掉,回到挂起。
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _hole ? ValueTask.CompletedTask : inner.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => _hole ? Task.CompletedTask : inner.FlushAsync(ct);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed.IsCancellationRequested)
        {
            _disposed.Cancel();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
