using Ncw.Agent.Llm.Pricing;

namespace Ncw.Agent.Llm.Tests;

/// <summary>计价(01-llm §8):档位整请求重算、缺价得 null、整数微单位、三态累加。</summary>
public sealed class PricingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero); // 周一

    private static ModelPricing Anthropicish(decimal read = 0.3m, decimal? w5m = 3.75m, decimal? w1h = 6m) => new(
        null, "claude-x", Currency.Usd, [new PriceTier(null, new TokenRates(3, 15, read, w5m, w1h))]);

    // 两档长上下文:阈值 272K,超档后输入 ×2、输出 ×1.5。
    private static readonly ModelPricing TwoTier = new(
        null, "gpt-x", Currency.Usd,
        [
            new PriceTier(272_000, new TokenRates(2.5m, 15m, 0.25m)),
            new PriceTier(null, new TokenRates(5m, 22.5m, 0.5m)),
        ]);

    [Fact]
    public void Cost_is_tokens_times_the_per_million_rate_as_integer_micros()
    {
        var result = PricingCalculator.PriceOf(Anthropicish(), new TokenUsage(1000, 500), Noon)!;

        // 1000 × 3 + 500 × 15 = 10 500 微美元
        Assert.Equal(10_500, result.Micros);
        Assert.Equal(Currency.Usd, result.Currency);
        Assert.Equal(0, result.Tier);
    }

    [Fact]
    public void Cache_read_write_5m_and_1h_are_priced_separately()
    {
        var usage = new TokenUsage(100, 10, CacheCreationInputTokens: 1000, CacheReadInputTokens: 2000, CacheCreation1hInputTokens: 400);

        var result = PricingCalculator.PriceOf(Anthropicish(), usage, Noon)!;

        // 100×3 + 10×15 + 2000×0.3 + (1000-400)×3.75 + 400×6 = 300+150+600+2250+2400
        Assert.Equal(5700, result.Micros);
    }

    [Fact]
    public void Crossing_a_tier_threshold_reprices_every_token_of_the_request_not_just_the_excess()
    {
        // 阶梯不是个税式的分段累加:越过阈值,整个请求的每一个 token 都按高档价重算。
        var below = PricingCalculator.PriceOf(TwoTier, new TokenUsage(272_000, 1000), Noon)!;
        var above = PricingCalculator.PriceOf(TwoTier, new TokenUsage(272_001, 1000), Noon)!;

        Assert.Equal(0, below.Tier);
        Assert.Equal(272_000 * 2.5m + 1000 * 15m, below.Micros);
        Assert.Equal(1, above.Tier);
        Assert.Equal(272_001 * 5m + 1000 * 22.5m, above.Micros); // 全部 272 001 个输入 token 按 5,输出也按高档
    }

    [Fact]
    public void Cached_tokens_count_toward_the_tier_threshold()
    {
        // 缓存 token 计入阈值判定,并且自己也按高档的缓存价重算:一个很大的缓存前缀在阈值附近是负担。
        var usage = new TokenUsage(10_000, 0, CacheReadInputTokens: 270_000);

        var result = PricingCalculator.PriceOf(TwoTier, usage, Noon)!;

        Assert.Equal(1, result.Tier);
        Assert.Equal(10_000 * 5m + 270_000 * 0.5m, result.Micros);
    }

    [Fact]
    public void A_token_class_that_was_used_but_has_no_price_is_unknown_not_zero()
    {
        var noCacheRead = new ModelPricing(null, "m", Currency.Usd, [new PriceTier(null, new TokenRates(1, 2))]);

        Assert.Null(PricingCalculator.PriceOf(noCacheRead, new TokenUsage(10, 10, CacheReadInputTokens: 5), Noon));
        Assert.NotNull(PricingCalculator.PriceOf(noCacheRead, new TokenUsage(10, 10), Noon)); // 没用到就不要求有价
    }

    [Fact]
    public void One_hour_cache_writes_never_fall_back_to_the_5m_price()
    {
        // 1h 是 5m 的两倍价:退回去就是静默少收一半。
        var usage = new TokenUsage(0, 0, CacheCreationInputTokens: 100, CacheCreation1hInputTokens: 100);

        Assert.Null(PricingCalculator.PriceOf(Anthropicish(w1h: null), usage, Noon));
    }

    [Fact]
    public void Reasoning_tokens_are_part_of_output_and_are_not_charged_again()
    {
        var plain = PricingCalculator.PriceOf(Anthropicish(), new TokenUsage(0, 1000), Noon)!;
        var withReasoning = PricingCalculator.PriceOf(Anthropicish(), new TokenUsage(0, 1000, ReasoningTokens: 600), Noon)!;

        Assert.Equal(plain.Micros, withReasoning.Micros);
    }

    [Fact]
    public void Micros_are_rounded_to_whole_units()
    {
        var pricing = new ModelPricing(null, "m", Currency.Cny, [new PriceTier(null, new TokenRates(0.075m, 0.3m))]);

        var result = PricingCalculator.PriceOf(pricing, new TokenUsage(7, 0), Noon)!;

        Assert.Equal(1, result.Micros); // 0.525 → 1
    }

    // ── 时段价 ──

    // 基准价录空闲价,窗口录高峰 ×2(错的方向是少算)。工作日 Asia/Shanghai 09:00–16:00 高峰。
    private static readonly ModelPricing PeakPriced = new(
        null, "ds", Currency.Cny, [new PriceTier(null, new TokenRates(1, 2))],
        [new PriceWindow("Asia/Shanghai", "09:00", "16:00", "peak", [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], Multiplier: 2m)]);

    [Fact]
    public void A_window_is_evaluated_in_its_own_timezone_start_inclusive_end_exclusive()
    {
        var inPeak = new DateTimeOffset(2026, 9, 7, 2, 0, 0, TimeSpan.Zero); // 周一 UTC 02:00 = 上海 10:00
        var atEnd = new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero); // 上海 16:00 整:不含
        var atStart = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero); // 上海 09:00 整:含

        Assert.Equal("peak", PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1000, 0), inPeak)!.Window);
        Assert.Equal(2000, PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1000, 0), inPeak)!.Micros);
        Assert.Null(PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1000, 0), atEnd)!.Window);
        Assert.Equal("peak", PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1000, 0), atStart)!.Window);
    }

    [Fact]
    public void Hour_and_weekday_come_from_the_same_timezone_conversion()
    {
        // UTC 周日 23:30 在上海已经是周一 07:30:星期用本机/UTC 判会错。07:30 不在 09–16 内,但换成 09:30 的周一要命中。
        var sundayUtcMondayShanghai = new DateTimeOffset(2026, 9, 6, 23, 30, 0, TimeSpan.Zero);
        var mondayShanghaiPeak = new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero).AddDays(1); // 周一 01:30Z = 上海 09:30

        Assert.Null(PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1, 0), sundayUtcMondayShanghai)!.Window);
        Assert.Equal("peak", PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1, 0), mondayShanghaiPeak)!.Window);
    }

    [Fact]
    public void Weekends_are_never_peak()
    {
        var saturday = new DateTimeOffset(2026, 9, 5, 2, 0, 0, TimeSpan.Zero); // 周六上海 10:00

        Assert.Null(PricingCalculator.PriceOf(PeakPriced, new TokenUsage(1000, 0), saturday)!.Window);
    }

    [Fact]
    public void A_window_may_wrap_past_midnight()
    {
        var night = new PriceWindow("UTC", "22:00", "06:00", "night", Multiplier: 0.5m);
        var pricing = new ModelPricing(null, "m", Currency.Usd, [new PriceTier(null, new TokenRates(2, 2))], [night]);

        Assert.True(PricingCalculator.InWindow(night, new DateTimeOffset(2026, 9, 7, 23, 0, 0, TimeSpan.Zero)));
        Assert.True(PricingCalculator.InWindow(night, new DateTimeOffset(2026, 9, 7, 5, 59, 0, TimeSpan.Zero)));
        Assert.False(PricingCalculator.InWindow(night, new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1000, PricingCalculator.PriceOf(pricing, new TokenUsage(1000, 0), new DateTimeOffset(2026, 9, 7, 23, 0, 0, TimeSpan.Zero))!.Micros);
    }

    // ── 查表 ──

    [Fact]
    public void Lookup_prefers_the_provider_override_then_the_generic_row_and_respects_effective_dates()
    {
        var generic = new ModelPricing(null, "m", Currency.Usd, [new PriceTier(null, new TokenRates(1, 1))]);
        var overridden = generic with { ProviderId = "azure", Tiers = [new PriceTier(null, new TokenRates(2, 2))] };
        var future = generic with { EffectiveFrom = new DateOnly(2027, 1, 1), Tiers = [new PriceTier(null, new TokenRates(9, 9))] };
        var table = new[] { generic with { EffectiveUntil = new DateOnly(2026, 12, 31) }, overridden, future };
        var book = new StaticPriceBook(table);

        Assert.Equal(2, book.Find("azure", "m", Noon)!.Tiers[0].Rate.Input);
        Assert.Equal(1, book.Find("other", "m", Noon)!.Tiers[0].Rate.Input);
        Assert.Equal(9, book.Find("other", "m", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))!.Tiers[0].Rate.Input); // 厂商预告的调价日
        Assert.Null(book.Find(null, "unknown", Noon));
    }

    [Fact]
    public void A_missing_price_row_is_unknown_cost_and_a_price_gap_is_unknown_too()
    {
        var book = new StaticPriceBook([new ModelPricing(null, "m", Currency.Usd, [new PriceTier(null, new TokenRates(1, 1))])]);

        Assert.Equal(CostInfo.Unknown, PricingCalculator.CostOf(book, "p", "nope", new TokenUsage(1, 1), Noon));
        Assert.Equal(CostInfo.Unknown, PricingCalculator.CostOf(book, "p", "m", new TokenUsage(1, 1, CacheReadInputTokens: 1), Noon));
        Assert.Equal(CostInfo.Of(2, Currency.Usd), PricingCalculator.CostOf(book, "p", "m", new TokenUsage(1, 1), Noon));
    }

    // ── 三态 ──

    [Fact]
    public void Cost_has_three_states_and_accumulates_with_a_locking_unknown()
    {
        var total = CostInfo.NotMetered;

        total = total.Add(CostInfo.NotMetered); // 没接计价:保持
        Assert.Equal(CostState.NotMetered, total.State);

        total = total.Add(CostInfo.Of(100, Currency.Usd));
        total = total.Add(CostInfo.NotMetered); // 已有累计值不动
        total = total.Add(CostInfo.Of(50, Currency.Usd));
        Assert.Equal(CostInfo.Of(150, Currency.Usd), total);

        total = total.Add(CostInfo.Unknown); // 整轮锁定为「未知」
        total = total.Add(CostInfo.Of(1, Currency.Usd));
        Assert.Equal(CostState.Unknown, total.State);
    }

    [Fact]
    public void Costs_in_different_currencies_cannot_be_added()
    {
        var mixed = CostInfo.Of(1, Currency.Usd).Add(CostInfo.Of(1, Currency.Cny));

        Assert.Equal(CostState.Unknown, mixed.State); // 没有汇率源:把 ¥ 和 $ 加到一起会得到看着合理的错数
    }

    [Fact]
    public void Token_usage_adds_field_by_field_and_keeps_optional_fields_absent_when_neither_side_has_them()
    {
        var sum = new TokenUsage(1, 2).Add(new TokenUsage(10, 20, CacheReadInputTokens: 5));

        Assert.Equal(new TokenUsage(11, 22, null, 5, null, null), sum);
    }
}
