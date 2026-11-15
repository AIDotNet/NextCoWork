using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

public sealed class SchedulesPageTests
{
    private static ManagementKit Seeded(string role = "owner")
    {
        var kit = new ManagementKit(role);
        kit.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace), null));
        kit.Schedules.Tasks.AddRange([
            FakeSchedules.MakeTask("t1", "Morning triage"),
            FakeSchedules.MakeTask("t2", "Weekly report", "weekly", "17:30", weekdays: [5]),
            FakeSchedules.MakeTask("t3", "Paused task", enabled: false),
            FakeSchedules.MakeTask("other", "Other project", workspace: "ws2"),
        ]);
        return kit;
    }

    private static async Task<ManagementKit> Open(string role = "owner")
    {
        var kit = Seeded(role);
        kit.Schedules.Push();
        await kit.Scheduled.OpenCommand.ExecuteAsync(null);
        return kit;
    }

    [Fact]
    public async Task The_page_lists_only_the_current_projects_tasks_and_opens_over_the_workspace()
    {
        var kit = await Open();
        Assert.True(kit.Scheduled.IsOpen);
        Assert.Equal(PageState.Ready, kit.Scheduled.State);
        Assert.Equal(3, kit.Scheduled.Tasks.Count);
        Assert.DoesNotContain(kit.Scheduled.Tasks, t => t.Id == "other");
        Assert.Equal("Paused task", kit.Scheduled.Tasks.Last().Name); // 停用的排最后

        // 切到别的会话 = 回到对话
        kit.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace), "s-1"));
        Assert.False(kit.Scheduled.IsOpen);
    }

    [Fact]
    public async Task Loading_empty_and_unknown_states()
    {
        var kit = Seeded();
        await kit.Scheduled.OpenCommand.ExecuteAsync(null);
        Assert.Equal(PageState.Loading, kit.Scheduled.State); // 主题还没推

        var empty = new ManagementKit();
        empty.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace), null));
        empty.Schedules.Push();
        await empty.Scheduled.OpenCommand.ExecuteAsync(null);
        Assert.True(empty.Scheduled.ShowsEmpty);

        var ready = await Open();
        ready.Rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, ready.Scheduled.State);
        Assert.Empty(ready.Scheduled.Tasks);
    }

    [Fact]
    public async Task Running_is_answered_only_by_the_active_runs_and_run_now_is_blocked_while_one_runs()
    {
        var kit = Seeded();
        kit.Schedules.Runs.Add(FakeSchedules.Run("r1", "t1", "running"));
        kit.Schedules.Push();
        await kit.Scheduled.OpenCommand.ExecuteAsync(null);

        var t1 = kit.Scheduled.Tasks.Single(t => t.Id == "t1");
        Assert.True(t1.IsRunning);
        await t1.RunNowCommand.ExecuteAsync(null);
        Assert.Empty(kit.Schedules.RanNow);

        // 终态到来:不再运行中
        kit.Schedules.Runs[0] = FakeSchedules.Run("r1", "t1", "success");
        kit.Schedules.Push();
        Assert.False(kit.Scheduled.Tasks.Single(t => t.Id == "t1").IsRunning);
        await kit.Scheduled.Tasks.Single(t => t.Id == "t1").RunNowCommand.ExecuteAsync(null);
        Assert.Equal(["t1"], kit.Schedules.RanNow);
        Assert.True(kit.Scheduled.Tasks.Single(t => t.Id == "t1").IsRunning);
    }

    [Fact]
    public async Task Every_write_is_owner_only_and_the_reason_is_available()
    {
        var kit = await Open("member");
        Assert.True(kit.Scheduled.IsReadOnly);
        Assert.Equal(GateReason.NeedsOwner, kit.Scheduled.WriteGate.Reason);
        kit.Scheduled.NewCommand.Execute(null);
        Assert.False(kit.Scheduled.Editor.IsOpen);
        var row = kit.Scheduled.Tasks[0];
        await row.ToggleCommand.ExecuteAsync(null);
        await row.RunNowCommand.ExecuteAsync(null);
        row.EditCommand.Execute(null);
        row.AskDeleteCommand.Execute(null);
        Assert.False(kit.Scheduled.Editor.IsOpen);
        Assert.False(kit.Scheduled.HasPendingDelete);
        Assert.Empty(kit.Schedules.RanNow);
        Assert.Equal(kit.Schedules.Tasks[0].Enabled, kit.Scheduled.Tasks[0].Enabled);
    }

    [Fact]
    public async Task The_editor_validates_the_rule_and_previews_the_next_five_runs_from_the_core_function()
    {
        var kit = await Open();
        kit.Scheduled.NewCommand.Execute(null);
        var e = kit.Scheduled.Editor;
        e.Timezone = "Asia/Shanghai";
        Assert.Contains(ScheduleProblem.Name, e.Problems);
        Assert.Contains(ScheduleProblem.Prompt, e.Problems);
        Assert.Empty(e.Preview.Take(0));

        e.Name = "Daily";
        e.Prompt = "Do it";
        e.Kind = ScheduleRuleKinds.Daily;
        e.Time = "09:00";
        Assert.Empty(e.Problems);
        Assert.Equal(5, e.Preview.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.FromHours(8)), e.Preview[0]); // 现在是 11-14 10:00(+8):今天的 9 点已过
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(8)), e.Preview[4]);

        e.Time = "25:00";
        Assert.Contains(ScheduleProblem.Rule, e.Problems);
        Assert.Empty(e.Preview);
        e.Time = "09:00";
        e.Timezone = "Nope/Nowhere";
        Assert.Contains(ScheduleProblem.Timezone, e.Problems);
    }

    [Fact]
    public async Task Weekly_once_and_repeat_rules_follow_the_2x_semantics()
    {
        var kit = await Open();
        kit.Scheduled.NewCommand.Execute(null);
        var e = kit.Scheduled.Editor;
        (e.Name, e.Prompt, e.Timezone, e.Time) = ("T", "P", "Asia/Shanghai", "08:30");

        e.Kind = ScheduleRuleKinds.Weekly;
        foreach (var w in e.Weekdays)
        {
            w.IsOn = false;
        }

        Assert.Contains(ScheduleProblem.Rule, e.Problems); // 每周几至少选一天
        e.Weekdays[1].IsOn = true; // 周一
        e.Weekdays[3].IsOn = true; // 周三
        Assert.Empty(e.Problems);
        Assert.Equal([1, 3], e.Rule.Weekdays);
        Assert.All(e.Preview, t => Assert.Contains(t.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.FromHours(8)), e.Preview[0]); // 11-14 是周六

        e.Kind = ScheduleRuleKinds.Once;
        e.Date = "2026-11-01"; // 过去
        Assert.Contains(ScheduleProblem.NeverFires, e.Problems);
        e.Date = "2026-12-24";
        Assert.Empty(e.Problems);
        Assert.Equal([new DateTimeOffset(2026, 12, 24, 8, 30, 0, TimeSpan.FromHours(8))], e.Preview);
        e.Date = "2026-02-30";
        Assert.Contains(ScheduleProblem.Rule, e.Problems);

        e.Kind = ScheduleRuleKinds.Daily;
        e.RepeatEnabled = true;
        e.RepeatEnd = "10:30";
        e.RepeatInterval = 30;
        Assert.Empty(e.Problems);
        // 现在是 10:00,今天窗口 08:30..10:30 每 30 分钟:下一次是 10:30,再下一次是明天 08:30
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 10, 30, 0, TimeSpan.FromHours(8)), e.Preview[0]);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 8, 30, 0, TimeSpan.FromHours(8)), e.Preview[1]);
        e.RepeatEnd = "";
        Assert.Contains(ScheduleProblem.Repeat, e.Problems);
    }

    [Fact]
    public async Task Saving_creates_in_the_current_project_and_updates_without_changing_the_project_and_failures_show_their_key()
    {
        var kit = await Open();
        kit.Scheduled.NewCommand.Execute(null);
        var e = kit.Scheduled.Editor;
        (e.Name, e.Prompt, e.Timezone) = ("Nightly", "Clean up", "Asia/Shanghai");
        await kit.Scheduled.SaveCommand.ExecuteAsync(null);
        var input = Assert.Single(kit.Schedules.Created);
        Assert.Equal((ManagementRig.Workspace, "Nightly", "daily"), (input.WorkspaceId, input.Name, input.Schedule.Kind));
        Assert.False(e.IsOpen);
        Assert.Contains(kit.Scheduled.Tasks, t => t.Name == "Nightly");

        kit.Scheduled.Tasks.Single(t => t.Id == "t1").EditCommand.Execute(null);
        Assert.False(e.IsNew);
        Assert.Equal("Morning triage", e.Name);
        e.Name = "Morning triage 2";
        await kit.Scheduled.SaveCommand.ExecuteAsync(null);
        Assert.Equal("t1", kit.Schedules.Updated.Single().Id);
        Assert.Equal("Morning triage 2", kit.Schedules.Updated.Single().Patch.Name);

        kit.Scheduled.NewCommand.Execute(null);
        (e.Name, e.Prompt, e.Timezone) = ("X", "Y", "Asia/Shanghai");
        kit.Schedules.CreateFailure = R.Fail<ScheduledTaskDto>(ActionOutcome.Invalid, "schedules.invalid.neverFires");
        await kit.Scheduled.SaveCommand.ExecuteAsync(null);
        Assert.Equal("schedules.invalid.neverFires", e.SaveFailure?.Key);
        Assert.True(e.IsOpen);
    }

    [Fact]
    public async Task Delete_asks_first_and_a_run_now_conflict_shows_its_key()
    {
        var kit = await Open();
        kit.Scheduled.Tasks.Single(t => t.Id == "t2").AskDeleteCommand.Execute(null);
        Assert.Equal("Weekly report", kit.Scheduled.PendingDelete?.Subject);
        kit.Scheduled.CancelDeleteCommand.Execute(null);
        Assert.Empty(kit.Schedules.Deleted);

        kit.Scheduled.Tasks.Single(t => t.Id == "t2").AskDeleteCommand.Execute(null);
        await kit.Scheduled.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(["t2"], kit.Schedules.Deleted);
        Assert.DoesNotContain(kit.Scheduled.Tasks, t => t.Id == "t2");

        kit.Schedules.RunNowFailure = R.Fail<ScheduledRunDto>(ActionOutcome.Conflict, "schedules.alreadyRunning");
        await kit.Scheduled.Tasks.Single(t => t.Id == "t1").RunNowCommand.ExecuteAsync(null);
        Assert.Equal("schedules.alreadyRunning", kit.Scheduled.ActionFailure?.Key);
    }

    [Fact]
    public async Task Runs_merge_the_live_topic_with_older_pages_and_a_terminal_status_beats_running()
    {
        var kit = Seeded();
        for (var i = 0; i < 25; i++)
        {
            kit.Schedules.History.Add(FakeSchedules.Run("h" + i, "t1", at: 1_700_000_000_000 + i * 1000));
        }

        kit.Schedules.Runs.Add(FakeSchedules.Run("live", "t1", "running", at: 1_800_000_000_000));
        // 翻页取回的旧记录里这条已经是终态(重放的旧事件不能把它改回运行中)
        kit.Schedules.History.Add(FakeSchedules.Run("live", "t1", "success", at: 1_800_000_000_000));
        kit.Schedules.Push();
        await kit.Scheduled.OpenCommand.ExecuteAsync(null);

        await kit.Scheduled.Tasks.Single(t => t.Id == "t1").ShowRunsCommand.ExecuteAsync(null);
        Assert.Equal(20, kit.Schedules.RunQueries[0].Limit);
        Assert.Equal("live", kit.Scheduled.Runs[0].Id);
        Assert.Equal("success", kit.Scheduled.Runs[0].Status);
        Assert.True(kit.Scheduled.HasMoreRuns);

        await kit.Scheduled.MoreRunsCommand.ExecuteAsync(null);
        Assert.NotNull(kit.Schedules.RunQueries[1].BeforeScheduledAt);
        Assert.Equal(26, kit.Scheduled.Runs.Count);
        Assert.Equal(kit.Scheduled.Runs.Select(r => r.Id).Distinct().Count(), kit.Scheduled.Runs.Count);

        await kit.Scheduled.Runs[1].SelectCommand.ExecuteAsync(null);
        Assert.Equal(kit.Scheduled.Runs[1].Id, kit.Scheduled.RunDetail!.Id);
        await kit.Scheduled.Runs[1].DeleteCommand.ExecuteAsync(null);
        Assert.Equal(25, kit.Scheduled.Runs.Count);
        Assert.Null(kit.Scheduled.RunDetail);
    }

    [Fact]
    public async Task An_active_run_cannot_be_deleted()
    {
        var kit = Seeded();
        kit.Schedules.Runs.Add(FakeSchedules.Run("live", "t1", "running"));
        kit.Schedules.Push();
        await kit.Scheduled.OpenCommand.ExecuteAsync(null);
        await kit.Scheduled.Tasks.Single(t => t.Id == "t1").ShowRunsCommand.ExecuteAsync(null);
        await kit.Scheduled.Runs[0].DeleteCommand.ExecuteAsync(null);
        Assert.Single(kit.Scheduled.Runs);
    }
}
