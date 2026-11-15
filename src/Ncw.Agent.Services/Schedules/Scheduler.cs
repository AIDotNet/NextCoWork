using System.Collections.Concurrent;
using Ncw.Agent.Storage;
using Ncw.Core.Ids;

namespace Ncw.Agent.Services.Schedules;

/// <summary>一次定时执行的结局(由 <see cref="IScheduledRunHost"/> 报告)。<see cref="Status"/> 是 <see cref="ScheduledRunStatuses"/> 的终态(success / error / aborted)。</summary>
public sealed record ScheduledOutcome(string Status, string? Summary = null, string? Error = null);

/// <summary>
/// 调度器和 Run 之间的接缝(定义在服务层,由 Host 用 <c>RunRegistry</c> 实现):建一个 <c>origin = scheduled</c> 的会话,经注册表的唯一入口 <c>Launch</c> 以
/// <c>RunOrigin.Scheduled</c> 发起 Run(2.x 里定时任务绕过了入口,3.0 不再允许),跑完返回结局。目标对定时任务的 Run 不生效(编排层已排除)。
/// </summary>
public interface IScheduledRunHost
{
    /// <summary>
    /// 跑一次任务。会话建好、Run 起来之后调 <paramref name="onStarted"/>(调度器把记录改成 running);工作区或模型不可用时抛 <see cref="ScheduleException"/> 或返回 <c>error</c> 结局。
    /// 永不返回 running;取消(设备关闭)按 aborted 收。
    /// </summary>
    Task<ScheduledOutcome> ExecuteAsync(ScheduledTask task, string sessionId, Action onStarted, CancellationToken ct);
}

/// <summary>
/// 调度器(2.x 的 <c>scheduled/scheduler.ts</c>):<b>一个</b>定时器,总是对准最近的下一次触发;到点把任务推到下一次、落一条 <c>queued</c> 记录、再在后台执行。
/// <list type="bullet">
/// <item><b>时钟可注入</b>(<see cref="TimeProvider"/>):测试推进假时钟,不睡真的时间。</item>
/// <item><b>定时器有上限</b>(<see cref="MaxTimerDelay"/> ≈ 24.8 天):更远的触发分段唤醒,醒来后由 tick 判断是否真的到期(2.x 为 Node 的 32 位上限做的,这里沿用,免得一个远期任务让定时器溢出成 1ms 循环)。</item>
/// <item><b>错过的补跑策略</b>(按 2.x):启动 / 唤醒时对账,<b>不回头补跑</b>——到点没有在线的任务记一条 <c>skipped</c>(<c>scheduled.missed</c>)并推到下一次。
/// 差异:2.x 只推一格(多次错过时,随后紧跟着的 tick 会把剩下的过期触发当场执行成一串补跑),3.0 一次推到<b>现在之后</b>的第一次触发,只留一条 skipped,不会启动时突然跑一串。</item>
/// <item><b>同一个任务不重叠</b>:上一次还在跑时到点,记一条 <c>skipped</c>(<c>scheduled.overlap</c>,2.x 是静默跳过,3.0 让界面看得到)。</item>
/// <item>一次性任务按计划跑完就关掉(手动运行不关)。</item>
/// <item>写入收口在 <see cref="ScheduledTaskService"/>:任何任务变化都会触发重排(<see cref="ScheduledTaskService.Changed"/>),所以「建了但不跑」不会发生。</item>
/// </list>
/// </summary>
public sealed class Scheduler : IAsyncDisposable
{
    /// <summary>单次定时器睡眠上限(毫秒,32 位有符号上限)。</summary>
    public const long MaxTimerDelay = 2_147_483_647;

    public const string OverlapError = "scheduled.overlap";

    private readonly ScheduledTaskService _tasks;
    private readonly ScheduledRepository _repository;
    private readonly IScheduledRunHost _host;
    private readonly TimeProvider _clock;
    private readonly Action<string, Exception>? _log;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private ITimer? _timer;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public Scheduler(ScheduledTaskService tasks, ScheduledRepository repository, IScheduledRunHost host, TimeProvider clock, Action<string, Exception>? log = null)
    {
        _tasks = tasks;
        _repository = repository;
        _host = host;
        _clock = clock;
        _log = log;
        _tasks.Changed += _ => Reschedule();
    }

    /// <summary>运行记录变了(排队 / 开始 / 结束 / 跳过 / 删除)。参数是记录 id。</summary>
    public event Action<string>? RunChanged;

    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>当前正在跑(或排队)的任务 id。</summary>
    public IReadOnlyCollection<string> RunningTasks => [.. _running.Keys];

    /// <summary>启动:先对账一次(错过的记 skipped、推到下一次),再按最近的触发排定时器。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped)
            {
                return;
            }

            _started = true;
        }

        Tick(reconcileOnly: true);
        Reschedule();
    }

    public void Reschedule()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (!_started || _stopped)
            {
                return;
            }

            long? next = null;
            foreach (var task in _tasks.List())
            {
                if (task is { Enabled: true, NextRunAt: { } at } && (next is null || at < next))
                {
                    next = at;
                }
            }

            if (next is not { } due)
            {
                return;
            }

            var delay = Math.Min(MaxTimerDelay, Math.Max(0, due - Now));
            _timer = _clock.CreateTimer(_ => Tick(reconcileOnly: false), null, TimeSpan.FromMilliseconds(delay), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>到期的任务:推到下一次 → 落记录 → 后台执行。</summary>
    internal void Tick(bool reconcileOnly)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
        }

        var now = Now;
        foreach (var task in _tasks.List())
        {
            if (!task.Enabled || task.NextRunAt is not { } scheduledAt || scheduledAt > now)
            {
                continue;
            }

            if (reconcileOnly)
            {
                // 错过的:不补跑。一次推到「现在之后」的第一次触发,只留一条 skipped。
                _tasks.Advance(task, Math.Max(scheduledAt, now), now);
                Record(new ScheduledRunRow(Ulid.New(_clock), task.Id, "", ScheduledRunTriggers.Scheduled, ScheduledRunStatuses.Skipped, scheduledAt, EndedAt: now, Error: ScheduledErrors.Missed));
                continue;
            }

            _tasks.Advance(task, scheduledAt, now);
            if (_running.ContainsKey(task.Id))
            {
                Record(new ScheduledRunRow(Ulid.New(_clock), task.Id, "", ScheduledRunTriggers.Scheduled, ScheduledRunStatuses.Skipped, scheduledAt, EndedAt: now, Error: OverlapError));
                continue;
            }

            Enqueue(task, scheduledAt, ScheduledRunTriggers.Scheduled);
        }

        Reschedule();
    }

    /// <summary>立即运行一次:先落 <c>queued</c> 记录并返回它,运行在后台进行。上一次还没跑完 → <see cref="ScheduleFailure.Conflict"/>。</summary>
    public ScheduledRunRow RunNow(string taskId)
    {
        var task = _tasks.Get(taskId) ?? throw new ScheduleException(ScheduleFailure.NotFound, "schedules.notFound", $"No scheduled task with id \"{taskId}\".");
        if (_running.ContainsKey(taskId))
        {
            throw new ScheduleException(ScheduleFailure.Conflict, "schedules.alreadyRunning", "The task is already running.");
        }

        lock (_gate)
        {
            if (_stopped)
            {
                throw new ScheduleException(ScheduleFailure.Conflict, "schedules.stopped", "The scheduler is shutting down.");
            }
        }

        return Enqueue(task, Now, ScheduledRunTriggers.Manual);
    }

    /// <summary>删除一条已结束的运行记录(排队 / 运行中的 → Conflict)。</summary>
    public bool DeleteRun(string runId)
    {
        if (_repository.GetRun(runId) is not { } run)
        {
            return false;
        }

        if (ScheduledRunStatuses.IsActive(run.Status))
        {
            throw new ScheduleException(ScheduleFailure.Conflict, "schedules.runActive", "A queued or running record cannot be deleted.");
        }

        var deleted = _repository.DeleteRun(runId);
        if (deleted)
        {
            Fire(runId);
        }

        return deleted;
    }

    // ───────────────────────── 执行 ─────────────────────────

    /// <summary>先落库一条 queued 记录(在做任何异步工作之前),再后台执行;<c>runNow</c> 因此可以立刻把记录返回给控制端。</summary>
    private ScheduledRunRow Enqueue(ScheduledTask task, long scheduledAt, string trigger)
    {
        var run = new ScheduledRunRow(Ulid.New(_clock), task.Id, Ulid.New(_clock), trigger, ScheduledRunStatuses.Queued, scheduledAt);
        Record(run);
        // 占位先于执行:执行很快结束时,它的 finally 也一定发生在占位之后(否则会留下一条永远不清的「正在跑」)。
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running[task.Id] = done.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(task, run).ConfigureAwait(false);
            }
            finally
            {
                done.TrySetResult();
            }
        });
        return run;
    }

    private async Task ExecuteAsync(ScheduledTask task, ScheduledRunRow queued)
    {
        var at = Now;
        ScheduledRunRow final;
        try
        {
            long? startedAt = null;
            var outcome = await _host.ExecuteAsync(task, queued.SessionId, () =>
            {
                startedAt = Now;
                Record(queued with { Status = ScheduledRunStatuses.Running, StartedAt = startedAt });
            }, _stopping.Token).ConfigureAwait(false);
            final = queued with
            {
                Status = outcome.Status,
                StartedAt = startedAt,
                EndedAt = Now,
                Summary = outcome.Status == ScheduledRunStatuses.Success ? outcome.Summary : null,
                Error = outcome.Status == ScheduledRunStatuses.Error ? outcome.Error ?? ScheduledErrors.ExecutionFailed : null,
            };
        }
        catch (OperationCanceledException)
        {
            final = queued with { Status = ScheduledRunStatuses.Aborted, EndedAt = Now };
        }
        catch (ScheduleException e)
        {
            final = queued with { Status = ScheduledRunStatuses.Error, EndedAt = Now, Error = e.Key };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log?.Invoke($"A scheduled run of '{task.Name}' failed.", e);
            final = queued with { Status = ScheduledRunStatuses.Error, EndedAt = Now, Error = ScheduledErrors.ExecutionFailed };
        }

        try
        {
            Record(final);
            if (queued.Trigger == ScheduledRunTriggers.Scheduled && task.Schedule.Kind == ScheduleKinds.Once)
            {
                _tasks.CloseOneShot(task.Id, final.EndedAt ?? at);
            }
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            Reschedule();
        }
    }

    private void Record(ScheduledRunRow run)
    {
        _repository.PutRun(run);
        Fire(run.Id);
    }

    private void Fire(string runId)
    {
        try
        {
            RunChanged?.Invoke(runId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 记录已落库:通知失败不影响调度。
        }
    }

    /// <summary>停:取消定时器和正在跑的执行(设备关闭 → aborted),等它们收尾。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_running.Values).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 每个执行自己吞异常;这里只是兜底。
        }

        _stopping.Dispose();
    }
}
