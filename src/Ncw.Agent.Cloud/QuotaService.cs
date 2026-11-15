using Ncw.Protocol.Methods;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud;

/// <summary>配额检查要读的真实用量(Host 用用量表、Run 注册表、磁盘实现;测试用假的)。</summary>
public interface IUsageSource
{
    /// <summary>用量表里 [<paramref name="fromMs"/>, <paramref name="toMs"/>] 区间内的 token 数(输入 + 输出 + 缓存读写)。</summary>
    long TokensBetween(long fromMs, long toMs);

    /// <summary>自有记录以来累计的 token(输入 / 输出 / 缓存),用量上报算增量用。</summary>
    (long Input, long Output, long Cache) TokenTotals();

    /// <summary>正在运行的顶层 Run 数。</summary>
    int ActiveRuns();

    /// <summary>数据根 + 工作区占用的字节数(最近一次量到的值;还没量过为 null)。</summary>
    long? StorageBytes();
}

/// <summary>Agent 自己记的账本:窗口内发起的 Run 数、计算时长,以及用于上报增量的累计值。持久化在 <see cref="CloudKeys.Ledger"/>。</summary>
public sealed record UsageLedger(long DayStart, long DayRuns, long MonthStart, long MonthRuns, long MonthComputeSeconds, long CumRuns, long CumComputeSeconds);

/// <summary>一次检查的结论。<see cref="Window"/> 是超限的那一项(<see cref="QuotaWindows"/>)。</summary>
public sealed record QuotaVerdict(bool Allowed, string? Window = null, long? Limit = null, long? Used = null, long? ResetsAt = null)
{
    public static readonly QuotaVerdict Ok = new(true);
}

/// <summary>超限项的名字(<c>CloudStatus.ExceededWindow</c> 和 <c>quota.exceeded</c> 的 <c>window</c> 参数)。</summary>
public static class QuotaWindows
{
    public const string Frozen = "frozen";
    public const string ConcurrentRuns = "concurrentRuns";
    public const string DailyRuns = "dailyRuns";
    public const string MonthlyRuns = "monthlyRuns";
    public const string DailyTokens = "dailyTokens";
    public const string MonthlyTokens = "monthlyTokens";
    public const string MonthlyCompute = "monthlyCompute";
    public const string Storage = "storage";
}

/// <summary>日 / 月窗口(UTC 自然日 / 自然月,Unix 毫秒)。</summary>
public static class UtcWindows
{
    public static long DayStart(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    public static long NextDay(long ms) => DayStart(ms) + 24L * 3600 * 1000;

    public static long MonthStart(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return new DateTimeOffset(t.Year, t.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    public static long NextMonth(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return new DateTimeOffset(t.Year, t.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).ToUnixTimeMilliseconds();
    }
}

/// <summary>被准入的 Run 的凭条:Run 没有真正发起(被注册表拒绝 / 校验失败)时 <see cref="Refund"/> 把这一次从账本里退掉。</summary>
public sealed class RunTicket
{
    private readonly QuotaService _owner;
    private readonly long _dayStart;
    private readonly long _monthStart;
    private int _settled;

    internal RunTicket(QuotaService owner, long dayStart, long monthStart)
    {
        _owner = owner;
        _dayStart = dayStart;
        _monthStart = monthStart;
    }

    public void Refund()
    {
        if (Interlocked.Exchange(ref _settled, 1) == 0)
        {
            _owner.Refund(_dayStart, _monthStart);
        }
    }
}

/// <summary>
/// 配额与用量(02 A6、05 B9):编排服务下发上限(<see cref="Set"/>,持久化,休眠唤醒后仍有效),Agent 自己数用量并在<b>Run 发起前</b>检查。
/// <list type="bullet">
/// <item>检查顺序:冻结 → 并发 Run → 日 / 月 Run 数 → 日 / 月 token → 月计算时长 → 存储。第一个超限的项就是结论(<see cref="QuotaVerdict.Window"/>)。</item>
/// <item>Run 数是 Agent 自己的账本(<see cref="UsageLedger"/>),token 来自用量表(Run 跑完才入账,所以 token 限额是「软」的:一个正在跑的 Run 可以超出一点);计算时长 = Agent 醒着的秒数(休眠不计)。</item>
/// <item>3.0 不收费:这里只拒绝新 Run(「用尽后转为只读」),不扣任何东西。</item>
/// </list>
/// 线程安全。
/// </summary>
public sealed class QuotaService
{
    private readonly ICloudStore _store;
    private readonly IUsageSource _usage;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private QuotaLimits _limits;
    private UsageLedger _ledger;
    private long _lastMeter;

    public QuotaService(ICloudStore store, IUsageSource usage, TimeProvider clock)
    {
        _store = store;
        _usage = usage;
        _clock = clock;
        _limits = store.Read(CloudKeys.Quota, CloudStateJson.Default.QuotaLimits) ?? new QuotaLimits();
        var now = Now;
        _ledger = store.Read(CloudKeys.Ledger, CloudStateJson.Default.UsageLedger) ?? new UsageLedger(UtcWindows.DayStart(now), 0, UtcWindows.MonthStart(now), 0, 0, 0, 0);
        _lastMeter = now;
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public QuotaLimits Limits
    {
        get
        {
            lock (_gate)
            {
                return _limits;
            }
        }
    }

    /// <summary>整体替换配额(<c>quota.set</c> 指令)并持久化。</summary>
    public void Set(QuotaLimits limits)
    {
        lock (_gate)
        {
            _limits = limits;
            _store.Write(CloudKeys.Quota, limits, CloudStateJson.Default.QuotaLimits);
        }
    }

    /// <summary>当前账本(滚过窗口的已归零)。</summary>
    public UsageLedger Ledger
    {
        get
        {
            lock (_gate)
            {
                MeterLocked();
                return _ledger;
            }
        }
    }

    /// <summary>把「醒着的时间」记进计算时长(整秒;零头留到下次)。休眠期间进程不在,所以不会被计入。</summary>
    public void Meter()
    {
        lock (_gate)
        {
            MeterLocked();
        }
    }

    private void MeterLocked()
    {
        var now = Now;
        RollLocked(now);
        var seconds = (now - _lastMeter) / 1000;
        if (seconds <= 0)
        {
            return;
        }

        _lastMeter += seconds * 1000;
        _ledger = _ledger with { MonthComputeSeconds = _ledger.MonthComputeSeconds + seconds, CumComputeSeconds = _ledger.CumComputeSeconds + seconds };
        PersistLocked();
    }

    private void RollLocked(long now)
    {
        var day = UtcWindows.DayStart(now);
        var month = UtcWindows.MonthStart(now);
        if (day != _ledger.DayStart || month != _ledger.MonthStart)
        {
            _ledger = _ledger with
            {
                DayStart = day,
                DayRuns = day == _ledger.DayStart ? _ledger.DayRuns : 0,
                MonthStart = month,
                MonthRuns = month == _ledger.MonthStart ? _ledger.MonthRuns : 0,
                MonthComputeSeconds = month == _ledger.MonthStart ? _ledger.MonthComputeSeconds : 0,
            };
            PersistLocked();
        }
    }

    private void PersistLocked() => _store.Write(CloudKeys.Ledger, _ledger, CloudStateJson.Default.UsageLedger);

    /// <summary>只检查,不记账(排队消息、状态查询用)。</summary>
    public QuotaVerdict Check()
    {
        lock (_gate)
        {
            MeterLocked();
            return EvaluateLocked(Now);
        }
    }

    /// <summary>
    /// 检查并记账:允许时返回凭条(这次 Run 已计入今天 / 本月),不允许时 <paramref name="verdict"/> 里是超限项、返回 null。
    /// 记账和检查在同一把锁里,并发的发起不会一起挤过最后一个名额。
    /// </summary>
    public RunTicket? Admit(out QuotaVerdict verdict)
    {
        lock (_gate)
        {
            MeterLocked();
            var now = Now;
            verdict = EvaluateLocked(now);
            if (!verdict.Allowed)
            {
                return null;
            }

            _ledger = _ledger with { DayRuns = _ledger.DayRuns + 1, MonthRuns = _ledger.MonthRuns + 1, CumRuns = _ledger.CumRuns + 1 };
            PersistLocked();
            return new RunTicket(this, _ledger.DayStart, _ledger.MonthStart);
        }
    }

    internal void Refund(long dayStart, long monthStart)
    {
        lock (_gate)
        {
            _ledger = _ledger with
            {
                DayRuns = dayStart == _ledger.DayStart ? Math.Max(0, _ledger.DayRuns - 1) : _ledger.DayRuns,
                MonthRuns = monthStart == _ledger.MonthStart ? Math.Max(0, _ledger.MonthRuns - 1) : _ledger.MonthRuns,
                CumRuns = Math.Max(0, _ledger.CumRuns - 1),
            };
            PersistLocked();
        }
    }

    private QuotaVerdict EvaluateLocked(long now)
    {
        RollLocked(now);
        var limits = _limits;
        if (limits.Frozen)
        {
            return new QuotaVerdict(false, QuotaWindows.Frozen);
        }

        var day = _ledger.DayStart;
        var month = _ledger.MonthStart;
        if (limits.MaxConcurrentRuns is { } concurrent && _usage.ActiveRuns() is var active && active >= concurrent)
        {
            return new QuotaVerdict(false, QuotaWindows.ConcurrentRuns, concurrent, active);
        }

        if (limits.DailyRuns is { } dailyRuns && _ledger.DayRuns >= dailyRuns)
        {
            return new QuotaVerdict(false, QuotaWindows.DailyRuns, dailyRuns, _ledger.DayRuns, UtcWindows.NextDay(now));
        }

        if (limits.MonthlyRuns is { } monthlyRuns && _ledger.MonthRuns >= monthlyRuns)
        {
            return new QuotaVerdict(false, QuotaWindows.MonthlyRuns, monthlyRuns, _ledger.MonthRuns, UtcWindows.NextMonth(now));
        }

        if (limits.DailyTokens is { } dailyTokens && _usage.TokensBetween(day, now) is var tokensToday && tokensToday >= dailyTokens)
        {
            return new QuotaVerdict(false, QuotaWindows.DailyTokens, dailyTokens, tokensToday, UtcWindows.NextDay(now));
        }

        if (limits.MonthlyTokens is { } monthlyTokens && _usage.TokensBetween(month, now) is var tokensMonth && tokensMonth >= monthlyTokens)
        {
            return new QuotaVerdict(false, QuotaWindows.MonthlyTokens, monthlyTokens, tokensMonth, UtcWindows.NextMonth(now));
        }

        if (limits.MonthlyComputeSeconds is { } compute && _ledger.MonthComputeSeconds >= compute)
        {
            return new QuotaVerdict(false, QuotaWindows.MonthlyCompute, compute, _ledger.MonthComputeSeconds, UtcWindows.NextMonth(now));
        }

        if (limits.StorageBytes is { } storage && _usage.StorageBytes() is { } used && used >= storage)
        {
            return new QuotaVerdict(false, QuotaWindows.Storage, storage, used);
        }

        return QuotaVerdict.Ok;
    }

    /// <summary>给 <c>cloud.getStatus</c>:配额上限、当前用量和是否已超限。</summary>
    public (CloudQuotaDto Quota, CloudUsageDto Usage, QuotaVerdict Verdict) Snapshot()
    {
        lock (_gate)
        {
            MeterLocked();
            var now = Now;
            var verdict = EvaluateLocked(now);
            var l = _limits;
            var quota = new CloudQuotaDto(l.DailyRuns, l.MonthlyRuns, l.DailyTokens, l.MonthlyTokens, l.MaxConcurrentRuns, l.MonthlyComputeSeconds, l.StorageBytes, l.Frozen);
            var usage = new CloudUsageDto(
                _ledger.DayRuns, _ledger.MonthRuns,
                _usage.TokensBetween(_ledger.DayStart, now), _usage.TokensBetween(_ledger.MonthStart, now),
                _usage.ActiveRuns(), _ledger.MonthComputeSeconds, _usage.StorageBytes(), _ledger.DayStart, _ledger.MonthStart);
            return (quota, usage, verdict);
        }
    }
}
