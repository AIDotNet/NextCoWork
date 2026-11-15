using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>目标条件本身的规则(<see cref="GoalConditions"/>):2.x <c>shared/domain/__tests__/goal.test.ts</c> 的规范化 / 清除词 / 命令解析,加上检查间隔(<c>goalCheckinInterval</c>)。</summary>
public class GoalConditionsTests
{
    [Fact]
    public void Normalize_trims_the_ends()
    {
        Assert.Equal("让测试全绿", GoalConditions.Normalize("  让测试全绿  "));
    }

    [Fact]
    public void Normalize_strips_zero_width_and_control_characters_so_an_invisible_condition_is_empty()
    {
        // ★ 否则「看起来写了东西」的空条件会被设立:判定器收到一个空条件,稳定地判未达成,这一轮永远停不下来
        Assert.Equal("", GoalConditions.Normalize("​​  ﻿"));
        Assert.Equal("", GoalConditions.Normalize("\u0000\u001f"));
        Assert.Equal("", GoalConditions.Normalize("‮⁠­"));
    }

    [Fact]
    public void Normalize_keeps_ordinary_spaces_inside_the_condition()
    {
        Assert.Equal("bun test 退出码为 0", GoalConditions.Normalize("bun test 退出码为 0"));
        Assert.Equal("a\tb\nc", GoalConditions.Normalize("a\tb\nc"));
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("stop")]
    [InlineData("off")]
    [InlineData("reset")]
    [InlineData("none")]
    [InlineData("cancel")]
    public void Clear_words_are_clear_words(string word)
    {
        Assert.True(GoalConditions.IsClearWord(word));
    }

    [Fact]
    public void Clear_words_ignore_case_and_surrounding_whitespace()
    {
        Assert.True(GoalConditions.IsClearWord("  CLEAR "));
    }

    [Fact]
    public void Only_the_whole_argument_counts_stop_the_server_is_a_condition()
    {
        Assert.False(GoalConditions.IsClearWord("stop the server"));
    }

    [Fact]
    public void An_empty_argument_opens_the_panel()
    {
        Assert.IsType<GoalCommandIntent.Show>(GoalConditions.ParseCommand(""));
        Assert.IsType<GoalCommandIntent.Show>(GoalConditions.ParseCommand("   "));
    }

    [Fact]
    public void A_clear_word_clears()
    {
        Assert.IsType<GoalCommandIntent.Clear>(GoalConditions.ParseCommand("clear"));
    }

    [Fact]
    public void An_ordinary_argument_sets_the_normalized_condition()
    {
        Assert.Equal(new GoalCommandIntent.Set("让 bun test 全绿"), GoalConditions.ParseCommand("  让 bun test 全绿 "));
    }

    [Fact]
    public void An_argument_that_is_only_invisible_characters_is_invalid_not_a_panel_request()
    {
        Assert.Equal(new GoalCommandIntent.Invalid(GoalInvalidReason.Empty, 0), GoalConditions.ParseCommand("​​"));
    }

    [Fact]
    public void Too_long_is_rejected_with_the_real_length_never_truncated()
    {
        // ★ 截断后的条件是一个用户没同意过的目标
        var intent = GoalConditions.ParseCommand(new string('x', GoalConditions.Max + 5));

        Assert.Equal(new GoalCommandIntent.Invalid(GoalInvalidReason.TooLong, GoalConditions.Max + 5), intent);
    }

    [Fact]
    public void Exactly_the_limit_is_accepted()
    {
        Assert.IsType<GoalCommandIntent.Set>(GoalConditions.ParseCommand(new string('x', GoalConditions.Max)));
    }

    [Fact]
    public void The_check_in_interval_starts_at_the_base_doubles_and_caps_at_four_times()
    {
        var half = TimeSpan.FromMinutes(30);

        Assert.Equal(half, GoalConditions.CheckinInterval(0, half));
        Assert.Equal(TimeSpan.FromMinutes(60), GoalConditions.CheckinInterval(1, half));
        Assert.Equal(TimeSpan.FromMinutes(120), GoalConditions.CheckinInterval(2, half));
        Assert.Equal(TimeSpan.FromMinutes(120), GoalConditions.CheckinInterval(3, half));
    }

    [Fact]
    public void A_base_below_the_floor_is_raised_to_one_minute_and_a_missing_one_is_the_default()
    {
        Assert.Equal(GoalConditions.CheckinMinDelay, GoalConditions.CheckinInterval(0, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromMinutes(30), GoalConditions.CheckinInterval(0));
        Assert.Equal(TimeSpan.FromMinutes(30), GoalConditions.CheckinInterval(0, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(30), GoalConditions.CheckinInterval(-4, TimeSpan.FromMinutes(30)));
    }
}
