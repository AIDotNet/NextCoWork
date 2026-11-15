using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>活跃日的日历运算。逐条移植自 2.x 的 <c>shared/domain/__tests__/usage-activity.test.ts</c>(日期序号、周几、日期序列、连续天数、本地日期)。</summary>
public sealed class UsageActivityTests
{
    [Fact]
    public void Adjacent_days_differ_by_one_in_index()
    {
        Assert.Equal(1, UsageActivity.DayIndex("2026-09-14") - UsageActivity.DayIndex("2026-09-13"));
    }

    [Fact]
    public void Across_months_and_years_still_differ_by_one()
    {
        Assert.Equal(1, UsageActivity.DayIndex("2026-10-01") - UsageActivity.DayIndex("2026-09-30"));
        Assert.Equal(1, UsageActivity.DayIndex("2027-01-01") - UsageActivity.DayIndex("2026-12-31"));
    }

    [Fact]
    public void A_leap_day_is_not_skipped()
    {
        Assert.Equal(1, UsageActivity.DayIndex("2028-03-01") - UsageActivity.DayIndex("2028-02-29"));
        Assert.Equal(1, UsageActivity.DayIndex("2028-02-29") - UsageActivity.DayIndex("2028-02-28"));
    }

    /// <summary>整个文件存在的理由:夏令时切换日按本地时间戳相减得到 0.958 / 1.042 天,「连续天数」每年断两次。</summary>
    [Fact]
    public void Daylight_saving_switch_days_still_differ_by_exactly_one_day()
    {
        Assert.Equal(1, UsageActivity.DayIndex("2026-03-09") - UsageActivity.DayIndex("2026-03-08"));
        Assert.Equal(1, UsageActivity.DayIndex("2026-11-02") - UsageActivity.DayIndex("2026-11-01"));
    }

    [Fact]
    public void Day_index_and_day_from_index_are_inverses()
    {
        foreach (var day in new[] { "1970-01-01", "2026-09-14", "2026-03-08", "2099-12-31" })
        {
            Assert.Equal(day, UsageActivity.DayFromIndex(UsageActivity.DayIndex(day)!.Value));
        }
    }

    [Fact]
    public void Invalid_input_is_null_not_a_coincidental_number()
    {
        Assert.Null(UsageActivity.DayIndex(""));
        Assert.Null(UsageActivity.DayIndex("2026-9-14"));
        Assert.Null(UsageActivity.DayIndex("not-a-date"));
        Assert.Null(UsageActivity.DayIndex("2026-02-30"));
    }

    [Fact]
    public void Weekday_of_knows_the_epoch_and_sundays_and_saturdays()
    {
        Assert.Equal(4, UsageActivity.WeekdayOf("1970-01-01"));
        Assert.Equal(0, UsageActivity.WeekdayOf("2026-09-13"));
        Assert.Equal(6, UsageActivity.WeekdayOf("2026-09-19"));
    }

    [Fact]
    public void Day_range_includes_both_ends_single_day_and_empty_when_reversed()
    {
        Assert.Equal(["2026-09-12", "2026-09-13", "2026-09-14"], UsageActivity.DayRange("2026-09-12", "2026-09-14"));
        Assert.Equal(["2026-09-14"], UsageActivity.DayRange("2026-09-14", "2026-09-14"));
        Assert.Empty(UsageActivity.DayRange("2026-09-14", "2026-09-12"));
    }

    [Fact]
    public void Streaks_of_nothing_are_zero()
    {
        Assert.Equal(new Streaks(0, 0), UsageActivity.ComputeStreaks([], "2026-09-14"));
    }

    [Fact]
    public void A_streak_ending_today_counts()
    {
        Assert.Equal(new Streaks(3, 3), UsageActivity.ComputeStreaks(["2026-09-12", "2026-09-13", "2026-09-14"], "2026-09-14"));
    }

    /// <summary>零点一过就把连胜清零的话,用户会以为自己断签了。</summary>
    [Fact]
    public void A_streak_ending_yesterday_still_continues()
    {
        Assert.Equal(new Streaks(2, 2), UsageActivity.ComputeStreaks(["2026-09-12", "2026-09-13"], "2026-09-14"));
    }

    [Fact]
    public void A_streak_that_broke_the_day_before_yesterday_is_zero_but_the_longest_stays()
    {
        Assert.Equal(new Streaks(0, 3), UsageActivity.ComputeStreaks(["2026-09-01", "2026-09-02", "2026-09-03"], "2026-09-14"));
    }

    [Fact]
    public void The_longest_is_the_historical_best_not_the_latest_run()
    {
        string[] days = ["2026-08-01", "2026-08-02", "2026-08-03", "2026-08-04", "2026-08-05", "2026-09-13", "2026-09-14"];
        Assert.Equal(new Streaks(2, 5), UsageActivity.ComputeStreaks(days, "2026-09-14"));
    }

    [Fact]
    public void Unordered_and_duplicate_days_do_not_matter()
    {
        Assert.Equal(new Streaks(3, 3), UsageActivity.ComputeStreaks(["2026-09-14", "2026-09-12", "2026-09-13", "2026-09-13", "2026-09-14"], "2026-09-14"));
    }

    [Fact]
    public void Streaks_continue_across_months_and_daylight_saving()
    {
        Assert.Equal(new Streaks(3, 3), UsageActivity.ComputeStreaks(["2026-08-30", "2026-08-31", "2026-09-01"], "2026-09-01"));
        Assert.Equal(new Streaks(3, 3), UsageActivity.ComputeStreaks(["2026-03-07", "2026-03-08", "2026-03-09"], "2026-03-09"));
    }

    [Fact]
    public void A_single_active_day_is_a_streak_of_one()
    {
        Assert.Equal(new Streaks(1, 1), UsageActivity.ComputeStreaks(["2026-09-14"], "2026-09-14"));
    }

    [Fact]
    public void Local_day_follows_the_given_zone_not_utc()
    {
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var at = new DateTimeOffset(2026, 9, 14, 23, 30, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds(); // 上海 23:30 = UTC 15:30,同一天
        Assert.Equal("2026-09-14", UsageActivity.LocalDayOf(at, shanghai));
        var lateUtc = new DateTimeOffset(2026, 9, 14, 20, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(); // UTC 20:00 = 上海次日 04:00
        Assert.Equal("2026-09-15", UsageActivity.LocalDayOf(lateUtc, shanghai));
        Assert.Equal("2026-09-14", UsageActivity.LocalDayOf(lateUtc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Month_and_day_are_zero_padded()
    {
        Assert.Equal("2026-01-05", UsageActivity.LocalDayOf(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), TimeZoneInfo.Utc));
    }
}
