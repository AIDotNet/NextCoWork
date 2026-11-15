namespace Ncw.Agent.Llm.Pricing;

/// <summary>
/// 一整套费率,每百万 token 的<b>绝对价格</b>(不存倍率:「缓存读 = 输入的 0.1 倍」有真实例外,用公式生成会算错)。
/// </summary>
public sealed record TokenRates(
    decimal Input,
    decimal Output,
    decimal? CacheRead = null,
    decimal? CacheWrite = null,
    decimal? CacheWrite1h = null,
    decimal? PerCall = null);

/// <summary><see cref="UpToInputTokens"/> 是该档上界(含);最后一档为 null = 无上界。</summary>
public sealed record PriceTier(long? UpToInputTokens, TokenRates Rate);

/// <summary>偏离基准价的时段。语义定死:tiers 里是基准价,窗口描述偏离。start 含、end 不含,允许 start &gt; end 表示跨午夜。</summary>
public sealed record PriceWindow(
    string Timezone,
    string Start,
    string End,
    string Label,
    IReadOnlyList<DayOfWeek>? DaysOfWeek = null,
    decimal? Multiplier = null,
    TokenRates? Rates = null);

public sealed record ModelPricing(
    string? ProviderId,
    string ModelId,
    Currency Currency,
    IReadOnlyList<PriceTier> Tiers,
    IReadOnlyList<PriceWindow>? Windows = null,
    DateOnly? EffectiveFrom = null,
    DateOnly? EffectiveUntil = null,
    string Source = "",
    string FetchedAt = "");

public sealed record PriceResult(long Micros, Currency Currency, int Tier, string? Window = null);

/// <summary>价目表:数据来自模型目录(见 02 A8)。本层只定义查询接口,不内置任何厂商数据(数字会过期,规则不会)。</summary>
public interface IPriceBook
{
    /// <summary>按 <c>(providerId, modelId)</c> → <c>(null, modelId)</c> 查;查不到返回 null。</summary>
    ModelPricing? Find(string? providerId, string modelId, DateTimeOffset at);
}

public sealed class StaticPriceBook(IEnumerable<ModelPricing> rows) : IPriceBook
{
    private readonly IReadOnlyList<ModelPricing> _rows = rows.ToList();

    public ModelPricing? Find(string? providerId, string modelId, DateTimeOffset at) => PricingCalculator.Find(_rows, providerId, modelId, at);
}

/// <summary>计价纯函数(2.x <c>shared/domain/pricing.ts</c>)。时间是显式参数,界面和落库共用这一个。</summary>
public static class PricingCalculator
{
    /// <summary>
    /// 档位一旦跨过阈值,<b>整个请求的每一个 token</b> 都按新档重算(不是累进分段,不是个税):xAI 和阿里云百炼的原话都是这样。
    /// 写成累进会让长上下文请求的费用系统性偏低,而且低得不扎眼。所以这里只选一次档,之后全用选中那一档的费率。
    /// </summary>
    public static PriceResult? PriceOf(ModelPricing pricing, TokenUsage usage, DateTimeOffset at)
    {
        if (pricing.Tiers.Count == 0)
        {
            return null;
        }

        var tierIndex = SelectTier(pricing.Tiers, BillableInput(usage));
        var window = ActiveWindow(pricing.Windows, at);
        var micros = CostMicros(EffectiveRate(pricing.Tiers[tierIndex].Rate, window), usage);
        return micros is null ? null : new PriceResult(micros.Value, pricing.Currency, tierIndex, window?.Label);
    }

    /// <summary>选档的分母是输入侧的全部 token(含缓存读写):缓存 token 计入阈值,自己也按长上下文档的缓存价重算。</summary>
    private static long BillableInput(TokenUsage u) => u.InputTokens + (u.CacheReadInputTokens ?? 0) + (u.CacheCreationInputTokens ?? 0);

    private static int SelectTier(IReadOnlyList<PriceTier> tiers, long inputTokens)
    {
        for (var i = 0; i < tiers.Count; i++)
        {
            if (tiers[i].UpToInputTokens is not { } upTo || inputTokens <= upTo)
            {
                return i;
            }
        }

        return tiers.Count - 1; // 每档都有上界且都没够着:只在价目表被改坏时到达;计价不该让整条请求失败
    }

    private static TokenRates EffectiveRate(TokenRates baseRate, PriceWindow? window)
    {
        if (window is null)
        {
            return baseRate;
        }

        if (window.Rates is { } rates)
        {
            return rates; // 整套覆盖比一个标量更具体
        }

        if (window.Multiplier is not { } m)
        {
            return baseRate;
        }

        return new TokenRates(
            baseRate.Input * m, baseRate.Output * m, baseRate.CacheRead * m, baseRate.CacheWrite * m, baseRate.CacheWrite1h * m, baseRate.PerCall * m);
    }

    /// <summary>
    /// 费率是「每百万 token」,金额是微单位(百万分之一货币单位):两个百万正好约掉,直接相乘。
    /// 某一类 token 有数量、费率表里却没有对应的价时返回 null,而不是当 0 算(当 0 的结果是看着合理的偏低数字,没人会发现)。
    /// </summary>
    private static long? CostMicros(TokenRates rate, TokenUsage usage)
    {
        var write1h = usage.CacheCreation1hInputTokens ?? 0;
        var write5m = Math.Max(0, (usage.CacheCreationInputTokens ?? 0) - write1h);
        var read = usage.CacheReadInputTokens ?? 0;

        // 推理 token 是输出的子集,不再加一遍。
        var total = usage.InputTokens * rate.Input + usage.OutputTokens * rate.Output;
        if (read > 0)
        {
            if (rate.CacheRead is not { } cacheRead)
            {
                return null;
            }

            total += read * cacheRead;
        }

        if (write5m > 0)
        {
            if (rate.CacheWrite is not { } cacheWrite)
            {
                return null;
            }

            total += write5m * cacheWrite;
        }

        if (write1h > 0)
        {
            // 刻意不退回 CacheWrite:1h 是 5m 的两倍价,退回去就是静默少收一半。
            if (rate.CacheWrite1h is not { } cacheWrite1h)
            {
                return null;
            }

            total += write1h * cacheWrite1h;
        }

        if (rate.PerCall is { } perCall)
        {
            total += perCall * 1_000_000;
        }

        return (long)Math.Round(total, MidpointRounding.AwayFromZero);
    }

    private static int ToMinutes(string hhmm)
    {
        var parts = hhmm.Split(':');
        return int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>小时和星期必须来自同一次时区换算(否则跨日边界上周日/周一会错位)。起点含、终点不含。</summary>
    public static bool InWindow(PriceWindow window, DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(window.Timezone));
        if (window.DaysOfWeek is { } days && !days.Contains(local.DayOfWeek))
        {
            return false;
        }

        var minutes = local.Hour * 60 + local.Minute;
        var start = ToMinutes(window.Start);
        var end = ToMinutes(window.End);
        return start <= end ? minutes >= start && minutes < end : minutes >= start || minutes < end;
    }

    private static PriceWindow? ActiveWindow(IReadOnlyList<PriceWindow>? windows, DateTimeOffset at) =>
        windows?.FirstOrDefault(w => InWindow(w, at));

    /// <summary>
    /// 查找顺序 <c>(providerId, modelId)</c> → <c>(null, modelId)</c> → null。主键必须带 providerId:同一个 modelId 在不同平台是不同的价。
    /// 返回 null = 没有定价,调用方记「未知」,不要编一个 0。
    /// </summary>
    public static ModelPricing? Find(IReadOnlyList<ModelPricing> table, string? providerId, string modelId, DateTimeOffset at)
    {
        var day = DateOnly.FromDateTime(at.UtcDateTime); // 厂商公告的调价日,按 UTC 比是够用的近似
        var rows = table.Where(p => p.ModelId == modelId
            && (p.EffectiveFrom is not { } from || day >= from)
            && (p.EffectiveUntil is not { } until || day <= until)).ToList();
        if (providerId is not null && rows.FirstOrDefault(p => p.ProviderId == providerId) is { } overridden)
        {
            return overridden;
        }

        return rows.FirstOrDefault(p => p.ProviderId is null);
    }

    /// <summary>
    /// 一次请求的费用三态:查不到价 / 用到了没价格的类别 → <see cref="CostInfo.Unknown"/>。
    /// 按每次尝试、按 <c>alias.upstreamModel</c>(不是回包里的名字)计价,时刻取请求发起时刻。
    /// </summary>
    public static CostInfo CostOf(IPriceBook book, string? providerId, string upstreamModel, TokenUsage usage, DateTimeOffset at)
    {
        var pricing = book.Find(providerId, upstreamModel, at);
        if (pricing is null)
        {
            return CostInfo.Unknown;
        }

        return PriceOf(pricing, usage, at) is { } result ? CostInfo.Of(result.Micros, result.Currency) : CostInfo.Unknown;
    }
}
