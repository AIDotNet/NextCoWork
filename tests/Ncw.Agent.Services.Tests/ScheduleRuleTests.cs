using Ncw.Agent.Services.Schedules;

namespace Ncw.Agent.Services.Tests;

/// <summary>调度规则的求值。移植自 2.x 的 <c>shared/domain/scheduled.test.ts</c>(一次性按任务时区解释并存 UTC、每天跨 UTC 日界、每周几 + 当天窗口重复)并补上夏令时、非法输入、人话描述。</summary>
public sealed class ScheduleRuleTests
{
    private const string Shanghai = "Asia/Shanghai";

    private static long Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public void A_one_off_local_time_is_interpreted_in_the_task_timezone_and_stored_as_utc()
    {
        var next = ScheduleCalculator.NextAt(new ScheduleRule("once", At: "2026-09-12T09:30"), Shanghai, Utc("2026-09-11T00:00:00Z"));
        Assert.Equal(Utc("2026-09-12T01:30:00Z"), next);
    }

    [Fact]
    public void A_daily_task_advances_by_local_clock_time_across_the_utc_day_boundary()
    {
        var next = ScheduleCalculator.NextAt(new ScheduleRule("daily", Time: "00:15"), Shanghai, Utc("2026-09-11T00:30:00Z"));
        Assert.Equal(Utc("2026-09-11T16:15:00Z"), next);
    }

    [Fact]
    public void Weekly_selection_and_same_day_repeat_windows()
    {
        var rule = new ScheduleRule("weekly", Time: "09:00", Weekdays: [1]);
        var window = new RepeatWindow(true, "11:00", 60);
        Assert.Equal(Utc("2026-09-14T01:00:00Z"), ScheduleCalculator.NextOccurrence(rule, Shanghai, window, Utc("2026-09-13T02:00:00Z")));
        Assert.Equal(Utc("2026-09-14T02:00:00Z"), ScheduleCalculator.NextOccurrence(rule, Shanghai, window, Utc("2026-09-14T01:30:00Z")));
    }

    [Fact]
    public void The_repeat_window_ends_at_its_end_time_and_the_next_day_starts_again()
    {
        var rule = new ScheduleRule("daily", Time: "09:00");
        var window = new RepeatWindow(true, "10:00", 30);
        // 上海 09:00 / 09:30 / 10:00 三次(UTC 01:00 / 01:30 / 02:00),之后是次日 09:00
        Assert.Equal(Utc("2026-09-14T02:00:00Z"), ScheduleCalculator.NextOccurrence(rule, Shanghai, window, Utc("2026-09-14T01:30:00Z")));
        Assert.Equal(Utc("2026-09-15T01:00:00Z"), ScheduleCalculator.NextOccurrence(rule, Shanghai, window, Utc("2026-09-14T02:00:00Z")));
    }

    [Fact]
    public void The_next_time_is_strictly_after_the_reference_instant()
    {
        var rule = new ScheduleRule("daily", Time: "09:00");
        var at = Utc("2026-09-14T01:00:00Z");
        Assert.Equal(Utc("2026-09-15T01:00:00Z"), ScheduleCalculator.NextAt(rule, Shanghai, at));
    }

    [Fact]
    public void A_past_one_off_time_never_fires()
    {
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("once", At: "2000-01-01T09:00"), Shanghai, Utc("2026-09-11T00:00:00Z")));
    }

    [Fact]
    public void Malformed_rules_and_unknown_timezones_never_fire_instead_of_throwing()
    {
        var now = Utc("2026-09-11T00:00:00Z");
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("daily", Time: "9:00"), Shanghai, now));
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("daily", Time: "24:00"), Shanghai, now));
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("once", At: "2026-02-30T09:00"), Shanghai, now));
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("weekly", Time: "09:00", Weekdays: []), Shanghai, now));
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("daily", Time: "09:00"), "Mars/Olympus", now));
        Assert.Null(ScheduleCalculator.NextAt(new ScheduleRule("yearly", Time: "09:00"), Shanghai, now));
    }

    [Fact]
    public void Daylight_saving_keeps_the_local_clock_time_not_the_utc_offset()
    {
        const string newYork = "America/New_York";
        var rule = new ScheduleRule("daily", Time: "09:00");
        // 2026-03-08 美国夏令时开始:3 月 7 日 09:00 EST = 14:00Z,3 月 8 日 09:00 EDT = 13:00Z
        Assert.Equal(Utc("2026-03-07T14:00:00Z"), ScheduleCalculator.NextAt(rule, newYork, Utc("2026-03-07T00:00:00Z")));
        Assert.Equal(Utc("2026-03-08T13:00:00Z"), ScheduleCalculator.NextAt(rule, newYork, Utc("2026-03-07T14:00:00Z")));
    }

    [Fact]
    public void A_local_time_that_does_not_exist_lands_after_the_skipped_hour()
    {
        // 2026-03-08 02:30 在纽约不存在(02:00 直接跳到 03:00):按两步法落在跳过之后,不会回到前一天
        var next = ScheduleCalculator.NextAt(new ScheduleRule("once", At: "2026-03-08T02:30"), "America/New_York", Utc("2026-03-08T00:00:00Z"));
        Assert.NotNull(next);
        Assert.InRange(next!.Value, Utc("2026-03-08T06:30:00Z"), Utc("2026-03-08T07:30:00Z"));
    }

    [Fact]
    public void The_human_description_is_stable_and_names_the_timezone()
    {
        Assert.Equal("every day at 09:00 (Asia/Shanghai)", ScheduleDescriptions.Describe(new ScheduleRule("daily", Time: "09:00"), Shanghai));
        Assert.Equal("every Monday, Friday at 08:00, then every 30 minutes until 10:00 (UTC)",
            ScheduleDescriptions.Describe(new ScheduleRule("weekly", Time: "08:00", Weekdays: [1, 5]), "UTC", new RepeatWindow(true, "10:00", 30)));
        Assert.Equal("once at 2026-09-12 09:30 (UTC)", ScheduleDescriptions.Describe(new ScheduleRule("once", At: "2026-09-12T09:30"), "UTC"));
    }

    [Fact]
    public void An_instant_is_formatted_in_the_task_timezone_not_utc()
    {
        Assert.Equal("2026-09-12 09:30", ScheduleDescriptions.FormatInstant(Utc("2026-09-12T01:30:00Z"), Shanghai));
    }
}
