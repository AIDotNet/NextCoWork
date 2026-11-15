using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>移植自 2.x 的 <c>shared/__tests__/context-management.test.ts</c> 与 <c>context-assembler.test.ts</c> 里的 estimateTokens / 压缩阈值几组。</summary>
public sealed class ContextWindowsTests
{
    [Fact]
    public void Effective_window_clamps_a_huge_window_to_the_billing_threshold_unless_max_context_is_on()
    {
        Assert.Equal(ContextWindows.LongContextThreshold, ContextWindows.Effective(1_000_000));
        Assert.Equal(1_000_000, ContextWindows.Effective(1_000_000, maxContext: true));
        Assert.Equal(128_000, ContextWindows.Effective(128_000));
        Assert.Equal(128_000, ContextWindows.Effective(128_000, maxContext: true)); // 开着开关也不会把小窗口放大
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_missing_or_invalid_protocol_window_falls_back_to_the_conservative_default(int? window)
    {
        Assert.Equal(ContextWindows.FallbackWindow, ContextWindows.Effective(window));
        Assert.Equal(ContextWindows.FallbackWindow, ContextWindows.Effective(window, maxContext: true));
    }

    [Fact]
    public void Max_context_only_means_something_when_the_window_exceeds_the_billing_threshold()
    {
        Assert.True(ContextWindows.SupportsMaxContext(1_000_000));
        Assert.False(ContextWindows.SupportsMaxContext(272_000)); // 恰好等于分界:开了也不会多出一个 token
        Assert.False(ContextWindows.SupportsMaxContext(128_000));
        Assert.False(ContextWindows.SupportsMaxContext(null));
    }

    [Fact]
    public void The_compaction_threshold_is_window_minus_capped_output_minus_buffer()
    {
        // 200K 窗口、8192 输出:200000 − 8192 − 13000
        Assert.Equal(178_808, ContextWindows.AutoCompactThreshold(200_000, 8192));
        // 272K、32K 输出:预留封顶 20K → 239K
        Assert.Equal(239_000, ContextWindows.AutoCompactThreshold(272_000, 32_000));
    }

    [Fact]
    public void The_output_reserve_is_capped_so_a_large_max_output_does_not_always_say_compact()
    {
        Assert.Equal(ContextWindows.AutoCompactThreshold(200_000, 20_000), ContextWindows.AutoCompactThreshold(200_000, 500_000));
        Assert.False(ContextWindows.ShouldCompact(100_000, 200_000, 500_000));
    }

    [Fact]
    public void The_threshold_never_falls_below_half_the_window_so_small_models_do_not_compact_every_turn()
    {
        Assert.Equal(4_000, ContextWindows.AutoCompactThreshold(8_000, 8_000));
        Assert.Equal(16_000, ContextWindows.AutoCompactThreshold(32_000, 32_000));
    }

    [Fact]
    public void Should_compact_and_should_warn_read_the_same_threshold()
    {
        var threshold = ContextWindows.AutoCompactThreshold(200_000, 8192);

        Assert.False(ContextWindows.ShouldCompact(threshold - 1, 200_000, 8192));
        Assert.True(ContextWindows.ShouldCompact(threshold, 200_000, 8192));
        Assert.True(ContextWindows.ShouldWarn(threshold - ContextWindows.WarningBufferTokens, 200_000, 8192));
        Assert.False(ContextWindows.ShouldWarn(threshold - ContextWindows.WarningBufferTokens - 1, 200_000, 8192));
    }

    [Fact]
    public void The_output_budget_comes_from_the_one_setting_and_is_only_limited_by_the_window()
    {
        Assert.Equal(32_000, ContextWindows.ResolveMaxOutputTokens(null, 200_000)); // 默认封顶 32000,不跟随模型更大的协议上限
        Assert.Equal(48_000, ContextWindows.ResolveMaxOutputTokens(48_000, 200_000)); // 设置里高于默认的按设置走
        Assert.Equal(8_000, ContextWindows.ResolveMaxOutputTokens(32_000, 8_000)); // 输出额度大于整个窗口必然 400
        Assert.Equal(32_000, ContextWindows.ResolveMaxOutputTokens(0, null));
    }

    [Fact]
    public void Token_estimate_is_zero_for_empty_and_four_latin_characters_per_token()
    {
        Assert.Equal(0, TokenEstimate.OfText(""));
        Assert.Equal(0, TokenEstimate.OfText(null));
        Assert.Equal(1, TokenEstimate.OfText("abcd"));
        Assert.Equal(2, TokenEstimate.OfText("abcde"));
    }

    [Fact]
    public void CJK_costs_one_token_per_character_so_chinese_is_far_dearer_than_english_of_the_same_length()
    {
        Assert.Equal(4, TokenEstimate.OfText("你好世界"));
        Assert.Equal(100, TokenEstimate.OfText(new string('字', 100)));
        Assert.Equal(25, TokenEstimate.OfText(new string('a', 100)));
    }

    [Fact]
    public void An_emoji_counts_as_one_code_point_not_two_surrogates()
    {
        Assert.Equal(1, TokenEstimate.OfText("😀😀😀😀")); // 4 个码点 = 1 token;按 UTF-16 长度会算成 2
    }

    [Fact]
    public void Longer_text_never_costs_less()
    {
        var previous = 0;
        for (var n = 0; n < 200; n++)
        {
            var current = TokenEstimate.OfText(new string('x', n) + (n % 3 == 0 ? "你" : ""));
            Assert.True(current >= previous - 1);
            previous = Math.Max(previous, current);
        }
    }
}
