using Ncw.Agent.Services.Schedules;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Tests;

/// <summary>
/// 调度器与任务服务。移植自 2.x 的 <c>scheduled/__tests__/scheduler.test.ts</c>(定时器上限、短延迟保留、关停清定时器)并按 3.0 补上:
/// 到点触发、错过的对账(不补跑)、重叠、一次性任务关闭、结局落库、立即运行、写入收口重排。时钟是手动推进的假时钟,不睡真的时间。
/// </summary>
public sealed class SchedulerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _db = new();
    private readonly ManualClock _clock = new(Origin);
    private readonly FakeRunHost _host = new();
    private readonly ScheduledRepository _repo;
    private readonly ScheduledTaskService _tasks;
    private readonly Scheduler _scheduler;
    private readonly List<string> _changedRuns = [];

    public SchedulerTests()
    {
        _db.AddWorkspace("ws");
        _repo = new ScheduledRepository(_db.Db);
        _tasks = new ScheduledTaskService(_repo, _clock, id => id == "ws");
        _scheduler = new Scheduler(_tasks, _repo, _host, _clock);
        _scheduler.RunChanged += id =>
        {
            lock (_changedRuns)
            {
                _changedRuns.Add(id);
            }
        };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        _db.Dispose();
    }

    private static ScheduledTaskInput Daily(string name = "Nightly", string time = "09:00", bool? enabled = null) =>
        new(name, "summarize", "ws", "model-x", new ScheduleRule("daily", Time: time), Timezone: "UTC", Enabled: enabled);

    private IReadOnlyList<ScheduledRunRow> Runs(string? taskId = null) => _repo.ListRuns(taskId, 100);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // ───────────────────────── scheduler.test.ts:定时器上限 ─────────────────────────

    [Fact]
    public void A_distant_occurrence_is_capped_and_rechecked_without_a_1ms_overflow_loop()
    {
        _tasks.Create(new ScheduledTaskInput("Future", "x", "ws", "m", new ScheduleRule("once", At: "2026-12-20T00:00"), Timezone: "UTC"));
        _scheduler.Start();

        Assert.Equal(TimeSpan.FromMilliseconds(Scheduler.MaxTimerDelay), _clock.NextDue); // 36 天后:封顶
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, _clock.PendingTimers); // 没到期的唤醒不会堆出第二个定时器
        Assert.Empty(Runs());

        _clock.Advance(TimeSpan.FromMilliseconds(Scheduler.MaxTimerDelay) - TimeSpan.FromDays(1)); // 封顶那一跳醒来:tick 判断没到期,重新排一个更短的
        Assert.True(_clock.NextDue < TimeSpan.FromMilliseconds(Scheduler.MaxTimerDelay));
        Assert.Equal(1, _clock.PendingTimers);
    }

    [Fact]
    public async Task A_short_delay_is_preserved_and_stopping_removes_the_timer()
    {
        _tasks.Create(Daily());
        _scheduler.Start();
        Assert.Equal(TimeSpan.FromHours(9), _clock.NextDue);

        await _scheduler.DisposeAsync();
        Assert.Equal(0, _clock.PendingTimers);
    }

    // ───────────────────────── 到点触发 ─────────────────────────

    [Fact]
    public async Task A_due_task_runs_through_the_host_records_the_outcome_and_advances_to_the_next_occurrence()
    {
        var task = _tasks.Create(Daily());
        _scheduler.Start();

        _clock.Advance(TimeSpan.FromHours(9));
        await Wait.UntilAsync(() => Runs().Any(r => r.Status == ScheduledRunStatuses.Success), "the run to finish");

        var run = Assert.Single(Runs(task.Id));
        Assert.Equal((ScheduledRunTriggers.Scheduled, ScheduledRunStatuses.Success, "done", null), (run.Trigger, run.Status, run.Summary, run.Error));
        Assert.Equal(Origin.AddHours(9).ToUnixTimeMilliseconds(), run.ScheduledAt);
        Assert.NotNull(run.StartedAt);
        Assert.NotNull(run.EndedAt);
        var call = Assert.Single(_host.Calls);
        Assert.Equal((task.Id, run.SessionId), (call.Task.Id, call.SessionId));
        // 推到了次日 09:00,定时器重新对准它
        Assert.Equal(Origin.AddHours(33).ToUnixTimeMilliseconds(), _tasks.Get(task.Id)!.NextRunAt);
        Assert.Equal(TimeSpan.FromHours(24), _clock.NextDue);
    }

    [Fact]
    public async Task Run_changes_are_announced_for_queued_running_and_the_final_state()
    {
        var gate = _host.Gate = new TaskCompletionSource();
        _tasks.Create(Daily());
        _scheduler.Start();
        _clock.Advance(TimeSpan.FromHours(9));
        await Wait.UntilAsync(() => Runs().Any(r => r.Status == ScheduledRunStatuses.Running), "running");

        gate.SetResult();
        await Wait.UntilAsync(() => Runs().Any(r => r.Status == ScheduledRunStatuses.Success), "success");

        lock (_changedRuns)
        {
            Assert.True(_changedRuns.Count >= 3, "queued, running and the final record are each announced");
        }
    }

    [Fact]
    public async Task A_failed_outcome_is_recorded_with_a_stable_error_key()
    {
        _host.Outcome = _ => new ScheduledOutcome(ScheduledRunStatuses.Error, Error: ScheduledErrors.ExecutionFailed);
        var task = _tasks.Create(Daily());
        _scheduler.Start();
        _clock.Advance(Hour * 9);
        await Wait.UntilAsync(() => Runs(task.Id).Any(r => r.Status == ScheduledRunStatuses.Error), "error");

        Assert.Equal(ScheduledErrors.ExecutionFailed, Runs(task.Id).Single().Error);
    }

    [Fact]
    public async Task A_host_that_throws_a_schedule_exception_records_its_key_and_an_unexpected_exception_records_a_generic_failure()
    {
        var logged = new List<string>();
        await using var scheduler = new Scheduler(_tasks, _repo, new ThrowingHost(), _clock, (message, _) => logged.Add(message));
        var first = _tasks.Create(Daily("a"));
        var second = _tasks.Create(Daily("b", "09:00"));
        scheduler.Start();
        _clock.Advance(Hour * 9);
        await Wait.UntilAsync(() => Runs().Count == 2 && Runs().All(r => r.Status == ScheduledRunStatuses.Error), "both failed");

        var errors = Runs().Select(r => r.Error).Order().ToList();
        Assert.Equal([ScheduledErrors.ExecutionFailed, ScheduledErrors.WorkspaceUnavailable], errors);
        Assert.Single(logged); // 只有「意外」的那个留了诊断
        _ = (first, second);
    }

    private sealed class ThrowingHost : IScheduledRunHost
    {
        private int _n;

        public Task<ScheduledOutcome> ExecuteAsync(ScheduledTask task, string sessionId, Action onStarted, CancellationToken ct) =>
            Interlocked.Increment(ref _n) == 1
                ? throw new ScheduleException(ScheduleFailure.NotFound, ScheduledErrors.WorkspaceUnavailable, "gone")
                : throw new InvalidOperationException("boom");
    }

    // ───────────────────────── 一次性任务 ─────────────────────────

    [Fact]
    public async Task A_one_off_task_is_disabled_after_its_scheduled_run_but_not_after_a_manual_one()
    {
        var once = _tasks.Create(new ScheduledTaskInput("Once", "x", "ws", "m", new ScheduleRule("once", At: "2026-11-14T08:00"), Timezone: "UTC"));
        var manual = _tasks.Create(new ScheduledTaskInput("Manual", "x", "ws", "m", new ScheduleRule("once", At: "2026-11-20T08:00"), Timezone: "UTC"));
        _scheduler.Start();

        _scheduler.RunNow(manual.Id);
        await Wait.UntilAsync(() => Runs(manual.Id).Any(r => r.Status == ScheduledRunStatuses.Success), "manual run");
        Assert.True(_tasks.Get(manual.Id)!.Enabled); // 手动运行不关

        _clock.Advance(Hour * 8);
        await Wait.UntilAsync(() => Runs(once.Id).Any(r => r.Status == ScheduledRunStatuses.Success), "scheduled run");
        await Wait.UntilAsync(() => !_tasks.Get(once.Id)!.Enabled, "the one-off task to close");
        Assert.Null(_tasks.Get(once.Id)!.NextRunAt);
        await Wait.UntilAsync(() => _clock.PendingTimers == 1, "the timer to point at the remaining task only"); // 只剩 manual 那条的下一次
    }

    // ───────────────────────── 错过与重叠 ─────────────────────────

    [Fact]
    public void Missed_occurrences_are_not_run_catch_up_style_one_skipped_record_and_the_next_is_after_now()
    {
        var task = _tasks.Create(Daily());
        // 设备离线了三天:nextRunAt 还停在第一天的 09:00
        _clock.Advance(TimeSpan.FromDays(3) + Hour * 12);
        _scheduler.Start();

        var run = Assert.Single(Runs(task.Id));
        Assert.Equal((ScheduledRunStatuses.Skipped, ScheduledErrors.Missed, ""), (run.Status, run.Error, run.SessionId));
        Assert.Equal(Origin.AddHours(9).ToUnixTimeMilliseconds(), run.ScheduledAt);
        Assert.Empty(_host.Calls); // 没有补跑
        var next = _tasks.Get(task.Id)!.NextRunAt!.Value;
        Assert.True(next > _clock.UnixMs, "the next occurrence is after now, so there is no burst of catch-up runs");
        Assert.Equal(Origin.AddDays(4).AddHours(9).ToUnixTimeMilliseconds(), next);
    }

    [Fact]
    public async Task A_due_occurrence_while_the_previous_run_is_still_going_is_recorded_as_skipped_overlap()
    {
        _host.Gate = new TaskCompletionSource();
        var task = _tasks.Create(new ScheduledTaskInput("Often", "x", "ws", "m", new ScheduleRule("daily", Time: "01:00"), Timezone: "UTC", RepeatWindow: new RepeatWindow(true, "05:00", 60)));
        _scheduler.Start();

        _clock.Advance(Hour * 1);
        await Wait.UntilAsync(() => Runs(task.Id).Any(r => r.Status == ScheduledRunStatuses.Running), "the first run");
        _clock.Advance(Hour * 1); // 第一次还没跑完,下一次到点
        await Wait.UntilAsync(() => Runs(task.Id).Any(r => r.Error == Scheduler.OverlapError), "the overlap record");

        Assert.Single(_host.Calls);
        _host.Gate.SetResult();
    }

    // ───────────────────────── 立即运行与删除运行记录 ─────────────────────────

    [Fact]
    public async Task Run_now_returns_the_queued_record_immediately_and_refuses_a_second_one_while_running()
    {
        var gate = _host.Gate = new TaskCompletionSource();
        var task = _tasks.Create(Daily(enabled: false)); // 停用的任务也能手动运行

        var queued = _scheduler.RunNow(task.Id);
        Assert.Equal((ScheduledRunStatuses.Queued, ScheduledRunTriggers.Manual, task.Id), (queued.Status, queued.Trigger, queued.TaskId));
        Assert.NotEmpty(queued.SessionId);
        Assert.Equal(queued.Id, _repo.GetRun(queued.Id)!.Id); // 落库先于执行

        await Wait.UntilAsync(() => _repo.GetRun(queued.Id)!.Status == ScheduledRunStatuses.Running, "running");
        var conflict = Assert.Throws<ScheduleException>(() => _scheduler.RunNow(task.Id));
        Assert.Equal((ScheduleFailure.Conflict, "schedules.alreadyRunning"), (conflict.Kind, conflict.Key));
        Assert.Contains(task.Id, _scheduler.RunningTasks);

        gate.SetResult();
        await Wait.UntilAsync(() => _repo.GetRun(queued.Id)!.Status == ScheduledRunStatuses.Success, "success");
        await Wait.UntilAsync(() => _scheduler.RunningTasks.Count == 0, "the running marker to clear");
        Assert.Throws<ScheduleException>(() => _scheduler.RunNow("missing"));
    }

    [Fact]
    public async Task Only_finished_run_records_can_be_deleted()
    {
        var gate = _host.Gate = new TaskCompletionSource();
        var task = _tasks.Create(Daily());
        var run = _scheduler.RunNow(task.Id);
        await Wait.UntilAsync(() => _repo.GetRun(run.Id)!.Status == ScheduledRunStatuses.Running, "running");

        Assert.Equal(ScheduleFailure.Conflict, Assert.Throws<ScheduleException>(() => _scheduler.DeleteRun(run.Id)).Kind);
        gate.SetResult();
        await Wait.UntilAsync(() => _repo.GetRun(run.Id)!.Status == ScheduledRunStatuses.Success, "success");

        Assert.True(_scheduler.DeleteRun(run.Id));
        Assert.False(_scheduler.DeleteRun(run.Id));
        Assert.Null(_repo.GetRun(run.Id));
    }

    [Fact]
    public async Task Stopping_the_scheduler_aborts_running_work_and_records_it_as_aborted()
    {
        _host.Gate = new TaskCompletionSource(); // 永不放行
        var task = _tasks.Create(Daily());
        var run = _scheduler.RunNow(task.Id);
        await Wait.UntilAsync(() => _repo.GetRun(run.Id)!.Status == ScheduledRunStatuses.Running, "running");

        await _scheduler.DisposeAsync();

        Assert.Equal(ScheduledRunStatuses.Aborted, _repo.GetRun(run.Id)!.Status);
    }

    // ───────────────────────── 写入收口 ─────────────────────────

    [Fact]
    public void Every_write_reschedules_the_timer_so_a_task_created_after_start_still_runs()
    {
        _scheduler.Start();
        Assert.Equal(0, _clock.PendingTimers);

        var task = _tasks.Create(Daily());
        Assert.Equal(TimeSpan.FromHours(9), _clock.NextDue);

        _tasks.Update(task.Id, new ScheduledTaskPatch { Schedule = new ScheduleRule("daily", Time: "03:00") });
        Assert.Equal(TimeSpan.FromHours(3), _clock.NextDue);

        _tasks.SetEnabled(task.Id, false);
        Assert.Equal(0, _clock.PendingTimers);
        _tasks.SetEnabled(task.Id, true);
        Assert.Equal(TimeSpan.FromHours(3), _clock.NextDue);

        _tasks.Delete(task.Id);
        Assert.Equal(0, _clock.PendingTimers);
    }

    [Fact]
    public void A_failing_change_listener_does_not_make_the_write_fail()
    {
        _tasks.Changed += _ => throw new InvalidOperationException("window destroyed");
        var created = _tasks.Create(Daily());
        Assert.NotNull(_tasks.Get(created.Id)); // 任务已经落库:通知失败不能把创建变成失败
    }

    // ───────────────────────── 任务服务的校验 ─────────────────────────

    [Fact]
    public void Creating_validates_the_workspace_the_text_the_rule_and_that_it_will_fire()
    {
        Assert.Equal("schedules.workspaceNotFound", Assert.Throws<ScheduleException>(() => _tasks.Create(Daily() with { WorkspaceId = "nope" })).Key);
        Assert.Equal("schedules.invalid.name", Assert.Throws<ScheduleException>(() => _tasks.Create(Daily() with { Name = "  " })).Key);
        Assert.Equal("schedules.invalid.prompt", Assert.Throws<ScheduleException>(() => _tasks.Create(Daily() with { Prompt = "" })).Key);
        Assert.Equal("schedules.invalid.rule", Assert.Throws<ScheduleException>(() => _tasks.Create(Daily(time: "9am"))).Key);
        Assert.Equal("schedules.invalid.timezone", Assert.Throws<ScheduleException>(() => _tasks.Create(Daily() with { Timezone = "Mars/Olympus" })).Key);
        Assert.Equal("schedules.invalid.repeat", Assert.Throws<ScheduleException>(() => _tasks.Create(Daily() with { RepeatWindow = new RepeatWindow(true, null, 30) })).Key);
        Assert.Equal("schedules.invalid.neverFires", Assert.Throws<ScheduleException>(() =>
            _tasks.Create(new ScheduledTaskInput("Past", "x", "ws", "m", new ScheduleRule("once", At: "2000-01-01T09:00"), Timezone: "UTC"))).Key);
        Assert.Empty(_tasks.List());
    }

    [Fact]
    public void Update_merges_only_the_given_fields_keeps_unknown_json_fields_and_a_disabled_task_stays_unscheduled()
    {
        var task = _tasks.Create(Daily());
        // 往存储的 JSON 里塞一个 3.0 不认识的字段(2.x 以后新增的):修改之后必须还在
        var raw = _repo.GetTask(task.Id)!;
        _repo.PutTask(raw with { Json = raw.Json.Replace("{", "{\"futureField\":42,", StringComparison.Ordinal) });

        var updated = _tasks.Update(task.Id, new ScheduledTaskPatch { Enabled = false });

        Assert.Equal(("Nightly", "summarize", false, null), (updated.Name, updated.Prompt, updated.Enabled, updated.NextRunAt));
        Assert.Contains("\"futureField\":42", _repo.GetTask(task.Id)!.Json);
        Assert.Equal(task.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public void A_scheduled_task_survives_reopening_the_database_with_the_2x_json_shape()
    {
        var task = _tasks.Create(new ScheduledTaskInput("Weekly", "p", "ws", "m", new ScheduleRule("weekly", Time: "08:30", Weekdays: [1, 3]), "prov", "Asia/Shanghai",
            new RepeatWindow(true, "10:00", 30)));
        var json = _repo.GetTask(task.Id)!.Json;

        // 2.x 的形状:camelCase、schedule.kind、repeatWindow.endTime / intervalMinutes
        Assert.Contains("\"workspaceId\":\"ws\"", json);
        Assert.Contains("\"schedule\":{\"kind\":\"weekly\",\"time\":\"08:30\",\"weekdays\":[1,3]}", json);
        Assert.Contains("\"repeatWindow\":{\"enabled\":true,\"endTime\":\"10:00\",\"intervalMinutes\":30}", json);
        Assert.Contains("\"modelProviderId\":\"prov\"", json);

        var read = _tasks.Get(task.Id)!;
        Assert.Equal([1, 3], read.Schedule.Weekdays!);
        Assert.Equal(task with { Schedule = read.Schedule }, read); // 星期数组是列表:记录相等比的是引用,所以单独比
    }

    [Fact]
    public void Runs_page_by_scheduled_time_then_id_with_a_total_order()
    {
        var task = _tasks.Create(Daily());
        for (var i = 0; i < 5; i++)
        {
            _repo.PutRun(new ScheduledRunRow($"r{i}", task.Id, "", "scheduled", "success", 1_000)); // 同一毫秒
        }

        var first = _repo.ListRuns(task.Id, 2);
        var second = _repo.ListRuns(task.Id, 2, first[^1].ScheduledAt, first[^1].Id);
        var third = _repo.ListRuns(task.Id, 2, second[^1].ScheduledAt, second[^1].Id);

        Assert.Equal(["r4", "r3", "r2", "r1", "r0"], first.Concat(second).Concat(third).Select(r => r.Id));
    }
}
