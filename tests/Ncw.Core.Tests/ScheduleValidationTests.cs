using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>定时任务的校验与预览(Core 纯函数,Agent 的 <c>ScheduleCalculator</c> 委托给同一个 <see cref="ScheduleMath"/>)。</summary>
public sealed class ScheduleValidationTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly long Now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

    private static ScheduleRuleSpec Daily(string time) => new(ScheduleRuleKinds.Daily, Time: time);

    private static long At(int day, int hour, int minute) => new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

    [Fact]
    public void A_valid_daily_rule_has_no_problems_and_previews_strictly_after_now()
    {
        Assert.Empty(ScheduleValidation.Validate("n", "p", Daily("09:00"), Zone, RepeatSpec.Off, Now));
        Assert.Equal([At(15, 9, 0), At(16, 9, 0), At(17, 9, 0)], ScheduleValidation.Preview(Daily("09:00"), Zone, RepeatSpec.Off, Now, 3));
        Assert.Equal([At(14, 10, 30)], ScheduleValidation.Preview(Daily("10:30"), Zone, RepeatSpec.Off, Now, 1));
    }

    [Theory]
    [InlineData("", "p", ScheduleProblem.Name)]
    [InlineData("  ", "p", ScheduleProblem.Name)]
    [InlineData("n", "", ScheduleProblem.Prompt)]
    public void Name_and_prompt_are_required(string name, string prompt, ScheduleProblem expected) =>
        Assert.Contains(expected, ScheduleValidation.Validate(name, prompt, Daily("09:00"), Zone, RepeatSpec.Off, Now));

    [Fact]
    public void Name_and_prompt_have_length_limits()
    {
        Assert.Contains(ScheduleProblem.Name, ScheduleValidation.Validate(new string('x', ScheduleValidation.MaxNameLength + 1), "p", Daily("09:00"), Zone, RepeatSpec.Off, Now));
        Assert.DoesNotContain(ScheduleProblem.Name, ScheduleValidation.Validate(new string('x', ScheduleValidation.MaxNameLength), "p", Daily("09:00"), Zone, RepeatSpec.Off, Now));
    }

    [Theory]
    [InlineData("daily", null, "9:00", null)]
    [InlineData("daily", null, "24:00", null)]
    [InlineData("daily", null, "09:60", null)]
    [InlineData("weekly", null, "09:00", null)]
    [InlineData("weekly", null, "09:00", new[] { 7 })]
    [InlineData("once", "2026-12-01 09:00", null, null)]
    [InlineData("once", "2026-02-30T09:00", null, null)]
    [InlineData("monthly", null, "09:00", null)]
    public void Malformed_rules_are_reported(string kind, string? at, string? time, int[]? weekdays) =>
        Assert.Contains(ScheduleProblem.Rule, ScheduleValidation.Validate("n", "p", new ScheduleRuleSpec(kind, at, time, weekdays), Zone, RepeatSpec.Off, Now));

    [Fact]
    public void An_unknown_zone_and_an_incomplete_repeat_window_are_reported_separately()
    {
        Assert.Contains(ScheduleProblem.Timezone, ScheduleValidation.Validate("n", "p", Daily("09:00"), "Nope/Nowhere", RepeatSpec.Off, Now));
        Assert.Contains(ScheduleProblem.Repeat, ScheduleValidation.Validate("n", "p", Daily("09:00"), Zone, new RepeatSpec(true, null, 30), Now));
        Assert.Contains(ScheduleProblem.Repeat, ScheduleValidation.Validate("n", "p", Daily("09:00"), Zone, new RepeatSpec(true, "18:00", 0), Now));
        Assert.DoesNotContain(ScheduleProblem.Repeat, ScheduleValidation.Validate("n", "p", Daily("09:00"), Zone, new RepeatSpec(true, "18:00", 30), Now));
    }

    [Fact]
    public void A_one_off_time_in_the_past_never_fires()
    {
        var past = new ScheduleRuleSpec(ScheduleRuleKinds.Once, At: "2026-11-01T09:00");
        Assert.Equal([ScheduleProblem.NeverFires], ScheduleValidation.Validate("n", "p", past, Zone, RepeatSpec.Off, Now));
        Assert.Empty(ScheduleValidation.Preview(past, Zone, RepeatSpec.Off, Now, 5));
    }

    [Fact]
    public void Weekly_rules_pick_the_listed_weekdays_in_the_task_zone()
    {
        var mondaysAndWednesdays = new ScheduleRuleSpec(ScheduleRuleKinds.Weekly, Time: "08:30", Weekdays: [1, 3]);
        Assert.Equal([At(16, 8, 30), At(18, 8, 30), At(23, 8, 30)], ScheduleValidation.Preview(mondaysAndWednesdays, Zone, RepeatSpec.Off, Now, 3)); // 11-14 是周六
    }

    [Fact]
    public void A_repeat_window_runs_every_interval_until_the_end_time_inclusive_then_continues_the_next_day()
    {
        var repeat = new RepeatSpec(true, "10:30", 30);
        Assert.Equal([At(14, 10, 30), At(15, 8, 30), At(15, 9, 0)], ScheduleValidation.Preview(Daily("08:30"), Zone, repeat, Now, 3));
    }

    [Fact]
    public void The_same_rule_gives_different_instants_in_different_zones()
    {
        var shanghai = ScheduleMath.NextAt(Daily("09:00"), Zone, Now);
        var utc = ScheduleMath.NextAt(Daily("09:00"), "UTC", Now);
        Assert.NotEqual(shanghai, utc);
        Assert.Equal(At(15, 9, 0), shanghai);
    }
}
