using Microsoft.Data.Sqlite;
using Ncw.Agent.Services.Schedules;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Tests;

/// <summary>手动推进的时钟:定时器只在 <see cref="Advance"/> 时触发(调度器的唤醒、错过的对账都用它测,不睡真的时间)。</summary>
public sealed class ManualClock(DateTimeOffset origin) : TimeProvider
{
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Disposed)
                {
                    return false;
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._now + (long)dueTime.TotalMilliseconds;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                Disposed = true;
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

    public long UnixMs => GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>现在挂着的最近一个定时器还有多久到期(没有 = null)。</summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (_gate)
            {
                var due = _timers.Where(t => t.Due != long.MaxValue).Select(t => t.Due).DefaultIfEmpty(long.MaxValue).Min();
                return due == long.MaxValue ? null : TimeSpan.FromMilliseconds(due - _now);
            }
        }
    }

    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due != long.MaxValue);
            }
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
                next.Due = long.MaxValue;
            }

            next.Fire();
        }
    }
}

/// <summary>真文件库、每个用例独立目录(重开库才测得出「落盘之后」)。</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwsvc-{Guid.NewGuid():N}");

    public TempDatabase()
    {
        Db = AgentDatabase.Open(_root);
    }

    public string Root => _root;

    public AgentDatabase Db { get; }

    public void AddWorkspace(string id, string name = "ws", string rootPath = "/tmp/ws")
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = "INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ($id, 0, $j, 'local')";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$j", $$"""{"id":"{{id}}","name":"{{name}}","rootPath":"{{rootPath}}","settings":{},"createdAt":1,"lastOpenedAt":1}""");
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>脚本化的 <see cref="IScheduledRunHost"/>:记录每次调用,结局由测试给;可以挂起(等门)。</summary>
public sealed class FakeRunHost : IScheduledRunHost
{
    private readonly object _gate = new();

    public List<(ScheduledTask Task, string SessionId)> Calls { get; } = [];

    public Func<ScheduledTask, ScheduledOutcome> Outcome { get; set; } = _ => new ScheduledOutcome(ScheduledRunStatuses.Success, "done");

    /// <summary>非 null:调用方在拿到结局之前等这个任务(测「正在运行」的状态和重叠)。</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ScheduledOutcome> ExecuteAsync(ScheduledTask task, string sessionId, Action onStarted, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add((task, sessionId));
        }

        onStarted();
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        return Outcome(task);
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }
}
