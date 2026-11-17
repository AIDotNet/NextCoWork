using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Actions;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>统计的时间范围。</summary>
public enum UsageRange
{
    Days7,
    Days30,
    Days90,
    All,
}

/// <summary>日序列图展示哪个量。</summary>
public enum UsageMetric
{
    Tokens,
    Requests,
    Cost,
}

/// <summary>图上的一个点(一天)。</summary>
public sealed record UsageDayPoint(DateOnly Day, double Value);

/// <summary>
/// 用量页的派生(纯函数,可单测):时间窗、日序列补零、费用的币种分开与「未知」。
/// ★ <c>CostMicros</c> 为 null = 未知,绝不当 0 相加;不同币种分开,绝不相加。
/// </summary>
public static class UsageView
{
    public static UsageWindowDto Window(UsageRange range, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var tomorrow = new DateTimeOffset(local.Date.AddDays(1), zone.GetUtcOffset(local.Date.AddDays(1)));
        var days = Days(range);
        long? from = days is null ? null : new DateTimeOffset(local.Date.AddDays(-(days.Value - 1)), zone.GetUtcOffset(local.Date.AddDays(-(days.Value - 1)))).ToUnixTimeMilliseconds();
        return new UsageWindowDto(from, tomorrow.ToUnixTimeMilliseconds());
    }

    public static int? Days(UsageRange range) => range switch { UsageRange.Days7 => 7, UsageRange.Days30 => 30, UsageRange.Days90 => 90, _ => null };

    private static long Tokens(UsageDailyBucketDto b) => b.InputTokens + b.OutputTokens + b.CacheReadTokens + b.CacheWriteTokens;

    /// <summary>日序列:按天汇总所选的量;窗口有下界时缺的天补 0(图上连续),「全部」只画有数据的区间。费用只算所选币种。</summary>
    public static IReadOnlyList<UsageDayPoint> Daily(IEnumerable<UsageDailyBucketDto> buckets, UsageMetric metric, string? currency, UsageRange range, DateOnly today)
    {
        var byDay = new Dictionary<DateOnly, double>();
        foreach (var b in buckets)
        {
            if (!DateOnly.TryParseExact(b.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                continue;
            }

            var value = metric switch
            {
                UsageMetric.Tokens => Tokens(b),
                UsageMetric.Requests => b.RequestCount,
                _ => b.Currency == currency && b.CostMicros is { } micros ? micros / 1_000_000.0 : 0,
            };
            byDay[day] = byDay.GetValueOrDefault(day) + value;
        }

        if (byDay.Count == 0 && Days(range) is null)
        {
            return [];
        }

        var days = Days(range);
        var first = days is { } n ? today.AddDays(-(n - 1)) : byDay.Keys.Min();
        var last = days is not null ? today : byDay.Keys.Max();
        var points = new List<UsageDayPoint>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            points.Add(new UsageDayPoint(d, byDay.GetValueOrDefault(d)));
        }

        return points;
    }

    /// <summary>出现过的币种(有已计价请求的),按字母序。</summary>
    public static IReadOnlyList<string> Currencies(IEnumerable<UsageDailyBucketDto> buckets) =>
        [.. buckets.Where(b => b.Currency.Length > 0 && b.CostMicros is not null).Select(b => b.Currency).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>有请求没能定价(费用只是一部分)。</summary>
    public static bool HasUnpriced(IEnumerable<UsageDailyBucketDto> buckets) => buckets.Any(b => b.PricedCount < b.RequestCount);

    /// <summary>金额文字:USD → $1.23,CNY → ¥1.23,别的币种带代码。</summary>
    public static string Money(string currency, long micros)
    {
        var amount = (micros / 1_000_000.0).ToString(micros is > -1_000_000 and < 1_000_000 && micros != 0 ? "0.0000" : "N2", CultureInfo.InvariantCulture);
        return currency switch { "USD" => "$" + amount, "CNY" => "¥" + amount, _ => amount + " " + currency };
    }

    /// <summary>多币种费用:每个币种一段,逗号分隔;没有任何已计价请求 = null(界面显示「未知」,不是 0)。</summary>
    public static string? Costs(IReadOnlyList<UsageCostTotalDto> costs) =>
        costs.Count == 0 ? null : string.Join(" · ", costs.OrderBy(c => c.Currency, StringComparer.Ordinal).Select(c => Money(c.Currency, c.Micros)));

    public static double? SuccessRate(long requests, long success) => requests == 0 ? null : success / (double)requests;

    /// <summary>1.2K / 3.4M 的紧凑数字。</summary>
    public static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>供应商 / 模型的一行统计。</summary>
public sealed record UsageStatRow(string Id, string Label, long Requests, double? SuccessRate, long Tokens, string? Cost, double? AverageLatencyMs, double Share);

/// <summary>
/// 设置 · 设备 · 用量(<c>usage.*</c>,只读,viewer 起):汇总、按供应商 / 模型、日序列图(只做用量统计,没有逐条的请求日志)。
/// 数据来自设备上的库,不进 Store:这一页自己的状态。切换范围 / 设备时迟到的旧结果被丢弃。设备断开 = 未知。
/// </summary>
public sealed partial class UsageViewModel : ManagementPageViewModel
{
    private readonly IUsageCommands _commands;
    private readonly TimeProvider _clock;
    private long _version;

    public UsageViewModel(SettingsScope scope, IUsageCommands commands, TimeProvider clock) : base(scope, Need.Viewer)
    {
        _commands = commands;
        _clock = clock;
    }

    protected override Need WriteNeed => Need.Viewer;

    [ObservableProperty] public partial UsageRange Range { get; set; } = UsageRange.Days30;

    [ObservableProperty] public partial UsageMetric Metric { get; set; } = UsageMetric.Tokens;

    [ObservableProperty] public partial string? Currency { get; set; }

    [ObservableProperty] public partial UsageSummaryDto? Summary { get; private set; }

    public ObservableCollection<UsageStatRow> Providers { get; } = [];

    public ObservableCollection<UsageStatRow> Models { get; } = [];

    /// <summary>日序列(整体替换,不逐项增删:图表按「一批数据换成另一批」做过渡)。</summary>
    [ObservableProperty] public partial IReadOnlyList<UsageDayPoint> Series { get; private set; } = [];

    public ObservableCollection<string> Currencies { get; } = [];

    /// <summary>窗口里一个请求都没有(空状态)。</summary>
    public bool IsEmptyData => IsReady && Summary is { RequestCount: 0 };

    public bool HasUnpriced { get; private set; }

    public string? TotalCost => Summary is null ? null : UsageView.Costs(Summary.Costs);

    public double? SuccessRate => Summary is null ? null : UsageView.SuccessRate(Summary.RequestCount, Summary.SuccessCount);

    partial void OnRangeChanged(UsageRange value) => _ = ReloadAsync();

    partial void OnMetricChanged(UsageMetric value) => RebuildSeries();

    partial void OnCurrencyChanged(string? value) => RebuildSeries();

    private UsageDailyBucketDto[] _buckets = [];

    protected override void OnUnavailable()
    {
        Summary = null;
        Providers.Clear();
        Models.Clear();
        Series = [];
    }

    protected override async Task LoadAsync()
    {
        var device = Scope.DeviceId!;
        var version = Interlocked.Increment(ref _version);
        var window = UsageView.Window(Range, _clock.GetUtcNow(), TimeZoneInfo.Local);
        var summary = _commands.GetSummaryAsync(device, window);
        var providers = _commands.GetProviderStatsAsync(device, window);
        var models = _commands.GetModelStatsAsync(device, window);
        var daily = _commands.GetDailySeriesAsync(device, window);
        await Task.WhenAll(summary, providers, models, daily);
        if (version != _version || !IsCurrent(device))
        {
            return; // 范围 / 设备已经变了
        }

        foreach (var failed in new[] { Failure.OfOrNull(summary.Result), Failure.OfOrNull(providers.Result), Failure.OfOrNull(models.Result), Failure.OfOrNull(daily.Result) })
        {
            if (failed is not null)
            {
                LoadFailure = failed;
                State = failed.Outcome == ActionOutcome.Unavailable ? PageState.Unknown : PageState.Error;
                return;
            }
        }

        Summary = summary.Result.Value;
        Fill(Providers, providers.Result.Value!.Stats);
        Fill(Models, models.Result.Value!.Stats);
        _buckets = daily.Result.Value!.Buckets;
        Currencies.Clear();
        foreach (var c in UsageView.Currencies(_buckets))
        {
            Currencies.Add(c);
        }

        if (Currency is null || !Currencies.Contains(Currency))
        {
            Currency = Currencies.FirstOrDefault();
        }

        HasUnpriced = UsageView.HasUnpriced(_buckets);
        RebuildSeries();
        Ready();
        OnPropertyChanged(string.Empty);
    }

    private static void Fill(ObservableCollection<UsageStatRow> target, UsageDimensionStatDto[] stats)
    {
        target.Clear();
        var max = stats.Length == 0 ? 1 : Math.Max(1, stats.Max(s => s.RequestCount));
        foreach (var s in stats)
        {
            target.Add(new UsageStatRow(s.Id, s.Label, s.RequestCount, UsageView.SuccessRate(s.RequestCount, s.SuccessCount),
                s.InputTokens + s.OutputTokens, UsageView.Costs(s.Costs), s.AverageLatencyMs, s.RequestCount / (double)max));
        }
    }

    private void RebuildSeries()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), TimeZoneInfo.Local).Date);
        var points = UsageView.Daily(_buckets, Metric, Currency, Range, today);
        Series = [.. points];
        OnPropertyChanged(nameof(ShowsCostPicker));
    }

    /// <summary>费用图且有多个币种:出币种选择(币种不相加)。</summary>
    public bool ShowsCostPicker => Metric == UsageMetric.Cost && Currencies.Count > 1;
}
