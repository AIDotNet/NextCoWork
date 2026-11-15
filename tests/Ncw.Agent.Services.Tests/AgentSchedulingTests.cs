using Ncw.Agent.Services.Schedules;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Tests;

/// <summary>
/// Agent 侧的定时任务窄缝。移植自 2.x 的 <c>scheduled/__tests__/bridge.test.ts</c>(工具那侧只管形状,这一侧管「这件事在这台机器上成不成立」):
/// 任务落在会话的项目、跨项目当成不存在、链深度到顶就拒、永不触发的规则当场拒、每次写都通知(重排 + 广播)、通知抛错不让创建失败。
/// </summary>
public sealed class AgentSchedulingTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
    private readonly ScheduledRepository _repo;
    private readonly ScheduledTaskService _tasks;
    private readonly List<string> _announced = [];

    public AgentSchedulingTests()
    {
        _db.AddWorkspace("ws_1");
        _db.AddWorkspace("ws_2");
        _repo = new ScheduledRepository(_db.Db);
        _tasks = new ScheduledTaskService(_repo, _clock, id => id is "ws_1" or "ws_2");
        _tasks.Changed += _announced.Add;
    }

    public void Dispose() => _db.Dispose();

    private AgentScheduling Bridge(string sessionId = "session_chat", string workspace = "ws_1") =>
        new(_tasks, _repo, workspace, sessionId, "gpt-5.6-sol", null, (model, _) => model is "gpt-5.6-sol" or "claude-4.6-opus");

    private static AgentScheduleCreate Daily(string name = "每日构建") => new(name, "跑一遍构建", new ScheduleRule("daily", Time: "09:00"));

    private ScheduledTask Seed(string workspace = "ws_1", int chainDepth = 0, string? creator = null, string name = "已有任务") =>
        _tasks.Create(new ScheduledTaskInput(name, "x", workspace, "gpt-5.6-sol", new ScheduleRule("daily", Time: "08:00"), Timezone: "Asia/Shanghai",
            CreatedBy: creator ?? ScheduleCreators.User, ChainDepth: chainDepth));

    // ───────────────────────── 创建 ─────────────────────────

    [Fact]
    public void A_task_lands_in_the_sessions_project_marked_as_agent_made_with_chain_depth_one()
    {
        var created = Bridge().Create(Daily() with { Timezone = "Asia/Shanghai" });

        var stored = _tasks.Get(created.Id)!;
        Assert.Equal(("ws_1", ScheduleCreators.Agent, 1), (stored.WorkspaceId, stored.CreatedBy, stored.ChainDepth));
        Assert.Equal("gpt-5.6-sol", stored.Model); // 模型没指定就继承当前 run 的模型
        Assert.Equal("every day at 09:00 (Asia/Shanghai)", created.Schedule);
        Assert.NotNull(created.NextRunAt);
    }

    [Fact]
    public void Every_write_is_announced_so_the_scheduler_reschedules_and_the_ui_broadcasts()
    {
        var created = Bridge().Create(Daily());
        Assert.Equal([created.Id], _announced);
    }

    [Fact]
    public void A_listener_that_throws_does_not_turn_the_creation_into_a_failure()
    {
        _tasks.Changed += _ => throw new InvalidOperationException("window destroyed");
        var created = Bridge().Create(Daily()); // 任务已经落库:报失败会让模型再建一遍
        Assert.NotNull(_tasks.Get(created.Id));
    }

    [Fact]
    public void An_unknown_model_alias_is_refused_and_points_back_to_inheriting_the_current_model()
    {
        var error = Assert.Throws<ScheduleException>(() => Bridge().Create(Daily() with { Model = "gpt-9-imaginary" }));
        Assert.Contains("gpt-5.6-sol", error.Message);
        Assert.Empty(_tasks.List());
    }

    [Fact]
    public void A_known_model_alias_is_used()
    {
        Assert.Equal("claude-4.6-opus", Bridge().Create(Daily() with { Model = "claude-4.6-opus" }).Model);
    }

    /// <summary>永不触发的规则当场拒:过去的一次性时间在界面上看起来完全正常。</summary>
    [Fact]
    public void A_schedule_that_never_fires_is_refused_on_the_spot()
    {
        var error = Assert.Throws<ScheduleException>(() => Bridge().Create(Daily() with { Schedule = new ScheduleRule("once", At: "2000-01-01T09:00") }));
        Assert.Contains("never fires", error.Message);
    }

    [Fact]
    public void Repeat_window_with_a_too_short_interval_or_without_an_end_time_is_refused()
    {
        Assert.Contains("end_time", Assert.Throws<ScheduleException>(() => Bridge().Create(Daily() with { RepeatWindow = new RepeatWindow(true, null, 30) })).Message);
        Assert.Contains(ScheduledAgentLimits.MinIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Throws<ScheduleException>(() => Bridge().Create(Daily() with { RepeatWindow = new RepeatWindow(true, "18:00", 1) })).Message);
    }

    [Fact]
    public void The_per_project_task_limit_refuses_and_tells_the_model_to_delete_one_first()
    {
        for (var i = 0; i < ScheduledAgentLimits.MaxTasksPerWorkspace; i++)
        {
            Seed();
        }

        Assert.Contains("Delete one", Assert.Throws<ScheduleException>(() => Bridge().Create(Daily())).Message);
    }

    [Fact]
    public void A_project_that_no_longer_exists_is_refused_without_leaving_an_orphan_task()
    {
        var gone = new AgentScheduling(_tasks, _repo, "ws_gone", "s", "m", null, (_, _) => true);
        Assert.Contains("no longer exists", Assert.Throws<ScheduleException>(() => gone.Create(Daily())).Message);
        Assert.Empty(_tasks.List());
    }

    // ───────────────────────── 链深度 ─────────────────────────

    private void ScheduledSession(ScheduledTask parent, string sessionId) =>
        _repo.PutRun(new ScheduledRunRow("run_1", parent.Id, sessionId, "scheduled", "running", 1));

    [Fact]
    public void A_scheduled_run_may_schedule_one_more_level()
    {
        var parent = Seed(chainDepth: 1);
        ScheduledSession(parent, "session_scheduled");

        var created = Bridge("session_scheduled").Create(Daily());

        Assert.Equal(2, _tasks.Get(created.Id)!.ChainDepth);
    }

    /// <summary>到顶那一层拒掉,否则每一层都会在下一次运行时再生出一层(指数增长,用户打开面板之前没有任何症状)。</summary>
    [Fact]
    public void Beyond_the_maximum_chain_depth_nothing_more_can_be_scheduled()
    {
        var parent = Seed(chainDepth: ScheduledAgentLimits.MaxChainDepth);
        ScheduledSession(parent, "session_scheduled");

        Assert.Contains("chain depth", Assert.Throws<ScheduleException>(() => Bridge("session_scheduled").Create(Daily())).Message);
    }

    // ───────────────────────── 改与删与查 ─────────────────────────

    [Fact]
    public void A_user_made_task_can_be_changed_and_only_the_given_fields_move()
    {
        var task = Seed(creator: ScheduleCreators.User, name: "用户建的");

        var updated = Bridge().Update(task.Id, new ScheduledTaskPatch { Enabled = false });

        Assert.False(updated.Enabled);
        Assert.Equal("用户建的", _tasks.Get(task.Id)!.Name);
    }

    /// <summary>别的项目的任务当成「不存在」,改不了也删不了(不泄漏它存在)。</summary>
    [Fact]
    public void A_task_in_another_project_is_treated_as_not_existing_for_update_and_remove()
    {
        var other = Seed("ws_2");

        Assert.Contains("ListScheduledTasks", Assert.Throws<ScheduleException>(() => Bridge().Update(other.Id, new ScheduledTaskPatch { Enabled = false })).Message);
        Assert.Contains("ListScheduledTasks", Assert.Throws<ScheduleException>(() => Bridge().Remove(other.Id)).Message);
        Assert.NotNull(_tasks.Get(other.Id));
    }

    [Fact]
    public void Changing_the_time_validates_that_the_rule_still_fires()
    {
        var task = Seed();
        Assert.Contains("never fires", Assert.Throws<ScheduleException>(() =>
            Bridge().Update(task.Id, new ScheduledTaskPatch { Schedule = new ScheduleRule("once", At: "2001-02-03T04:05") })).Message);
    }

    [Fact]
    public void Remove_reports_the_name_and_announces()
    {
        var task = Seed(name: "要删的");
        _announced.Clear();

        Assert.Equal((task.Id, "要删的"), Bridge().Remove(task.Id));
        Assert.Null(_tasks.Get(task.Id));
        Assert.Equal([task.Id], _announced);
    }

    [Fact]
    public void Listing_shows_only_the_current_project_with_human_readable_times()
    {
        Seed();
        Seed("ws_2");

        var listed = Bridge().List();

        var one = Assert.Single(listed);
        Assert.Contains("every day at 08:00", one.Schedule);
        Assert.Equal(ScheduleCreators.User, one.CreatedBy);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", one.NextRunAt!); // 本地时间串,不是时间戳
    }
}
