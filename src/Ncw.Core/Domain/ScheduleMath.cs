namespace Ncw.Core.Domain;

/// <summary>规则种类的线上字符串(<c>once</c> / <c>daily</c> / <c>weekly</c>)。</summary>
public static class ScheduleRuleKinds
{
    public const string Once = "once";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
}

/// <summary>调度规则(与协议的 <c>ScheduleRuleDto</c> 同形,Core 不依赖协议):<see cref="At"/> 是 <c>YYYY-MM-DDTHH:MM</c>,<see cref="Time"/> 是 <c>HH:MM</c>,都按任务的时区解释。</summary>
public sealed record ScheduleRuleSpec(string Kind, string? At = null, string? Time = null, IReadOnlyList<int>? Weekdays = null);

/// <summary>当天窗口内重复:从规则的开始时刻起,每 <see cref="IntervalMinutes"/> 分钟一次,直到 <see cref="EndTime"/>(含)。</summary>
public sealed record RepeatSpec(bool Enabled, string? EndTime = null, int? IntervalMinutes = null)
{
    public static readonly RepeatSpec Off = new(false);
}

/// <summary>
/// 调度规则的求值(2.x 的 <c>shared/domain/scheduled.ts</c>:<c>nextScheduledAt</c> / <c>nextScheduledOccurrence</c>),纯函数,时钟由调用方给。
/// <list type="bullet">
/// <item>规则按<b>任务自己的 IANA 时区</b>解释(<c>09:00</c> 是任务时区的 9 点,不是设备时区的),结果是 UTC 毫秒。</item>
/// <item>本地时间到 UTC 的换算对夏令时稳健:先按「当天零点的偏移」估一次,再按估出来的时刻的偏移修正一次(和 2.x 同一个两步法);不存在的本地时刻(春季跳过的那一小时)落在跳过之后。</item>
/// <item>永远只给「严格晚于 <c>after</c>」的下一次;时区名不认识、规则字段非法、一次性任务已过期都返回 null(调用方把它当成「永不触发」拒掉)。</item>
/// </list>
/// </summary>
public static class ScheduleMath
{
    /// <summary>设备本地时区的 IANA 名;取不到时是 <c>UTC</c>。</summary>
    public static string SystemTimeZone() => TimeZoneInfo.Local.Id is { Length: > 0 } id ? id : "UTC";

    /// <summary>认得出的时区返回它,否则 null。</summary>
    public static TimeZoneInfo? FindZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>规则的下一次触发(不含当天窗口重复)。</summary>
    public static long? NextAt(ScheduleRuleSpec rule, string timezone, long after) => Guard(() => NextAtCore(rule, timezone, after));

    /// <summary>规则的下一次触发,含可选的当天窗口重复。</summary>
    public static long? NextOccurrence(ScheduleRuleSpec rule, string timezone, RepeatSpec? repeat, long after) =>
        Guard(() => NextOccurrenceCore(rule, timezone, repeat ?? RepeatSpec.Off, after));

    private static long? Guard(Func<long?> compute)
    {
        try
        {
            return compute();
        }
        catch (Exception e) when (e is ArgumentException or InvalidTimeZoneException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private static long? NextAtCore(ScheduleRuleSpec rule, string timezone, long after)
    {
        if (FindZone(timezone) is not { } zone)
        {
            return null;
        }

        if (rule.Kind == ScheduleRuleKinds.Once)
        {
            if (ParseLocalDateTime(rule.At) is not { } local)
            {
                return null;
            }

            var timestamp = LocalToUtc(local.Year, local.Month, local.Day, local.Hour, local.Minute, zone);
            return timestamp > after ? timestamp : null;
        }

        if (rule.Kind is not (ScheduleRuleKinds.Daily or ScheduleRuleKinds.Weekly) || ParseTime(rule.Time) is not { } time)
        {
            return null;
        }

        var current = PartsInZone(after, zone);
        var maxDays = rule.Kind == ScheduleRuleKinds.Daily ? 370 : 14;
        for (var offset = 0; offset <= maxDays; offset++)
        {
            var date = AddDays(current.Year, current.Month, current.Day, offset);
            if (rule.Kind == ScheduleRuleKinds.Weekly && rule.Weekdays?.Contains((int)new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).DayOfWeek) != true)
            {
                continue;
            }

            var candidate = LocalToUtc(date.Year, date.Month, date.Day, time.Hour, time.Minute, zone);
            if (candidate > after)
            {
                return candidate;
            }
        }

        return null;
    }

    private static long? NextOccurrenceCore(ScheduleRuleSpec rule, string timezone, RepeatSpec repeat, long after)
    {
        if (!repeat.Enabled)
        {
            return NextAtCore(rule, timezone, after);
        }

        var interval = Math.Max(1, repeat.IntervalMinutes ?? 60);
        if (ParseTime(repeat.EndTime) is not { } end)
        {
            return NextAtCore(rule, timezone, after);
        }

        if (FindZone(timezone) is not { } zone)
        {
            return null;
        }

        var current = PartsInZone(after, zone);
        var maxDays = rule.Kind == ScheduleRuleKinds.Once ? 1 : rule.Kind == ScheduleRuleKinds.Daily ? 370 : 14;
        for (var offset = 0; offset <= maxDays; offset++)
        {
            var date = AddDays(current.Year, current.Month, current.Day, offset);
            (int Hour, int Minute) start;
            if (rule.Kind == ScheduleRuleKinds.Once)
            {
                if (ParseLocalDateTime(rule.At) is not { } local || local.Year != date.Year || local.Month != date.Month || local.Day != date.Day)
                {
                    continue;
                }

                start = (local.Hour, local.Minute);
            }
            else
            {
                if (rule.Kind is not (ScheduleRuleKinds.Daily or ScheduleRuleKinds.Weekly) || ParseTime(rule.Time) is not { } parsed)
                {
                    continue;
                }

                start = (parsed.Hour, parsed.Minute);
                if (rule.Kind == ScheduleRuleKinds.Weekly && rule.Weekdays?.Contains((int)new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).DayOfWeek) != true)
                {
                    continue;
                }
            }

            var startAt = LocalToUtc(date.Year, date.Month, date.Day, start.Hour, start.Minute, zone);
            var endAt = LocalToUtc(date.Year, date.Month, date.Day, end.Hour, end.Minute, zone);
            var lastAt = endAt > startAt ? endAt : startAt;
            for (var candidate = startAt; candidate <= lastAt; candidate += interval * 60_000L)
            {
                if (candidate > after)
                {
                    return candidate;
                }
            }

            if (rule.Kind == ScheduleRuleKinds.Once)
            {
                break;
            }
        }

        return null;
    }

    // ───────────────────────── 日历 / 时区算术 ─────────────────────────

    private static (int Year, int Month, int Day, int Hour, int Minute, int Second) PartsInZone(long at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), zone);
        return (local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second);
    }

    private static long ZoneOffsetMs(long at, TimeZoneInfo zone) => (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(at)).TotalMilliseconds;

    /// <summary>本地墙钟时间 → UTC 毫秒(2.x 的两步法:先按「把墙钟当 UTC」的偏移估一次,再按估出来的时刻的偏移修正一次)。</summary>
    public static long LocalToUtc(int year, int month, int day, int hour, int minute, TimeZoneInfo zone)
    {
        var naive = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var first = naive - ZoneOffsetMs(naive, zone);
        return naive - ZoneOffsetMs(first, zone);
    }

    private static (int Year, int Month, int Day) AddDays(int year, int month, int day, int amount)
    {
        var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc).AddDays(amount);
        return (date.Year, date.Month, date.Day);
    }

    public static (int Hour, int Minute)? ParseTime(string? value)
    {
        if (value is not { Length: 5 } || value[2] != ':' || !int.TryParse(value.AsSpan(0, 2), out var hour) || !int.TryParse(value.AsSpan(3, 2), out var minute)
            || !char.IsAsciiDigit(value[0]) || !char.IsAsciiDigit(value[1]) || !char.IsAsciiDigit(value[3]) || !char.IsAsciiDigit(value[4]))
        {
            return null;
        }

        return hour < 24 && minute < 60 ? (hour, minute) : null;
    }

    public static (int Year, int Month, int Day, int Hour, int Minute)? ParseLocalDateTime(string? value)
    {
        if (value is not { Length: 16 } || value[4] != '-' || value[7] != '-' || value[10] != 'T')
        {
            return null;
        }

        if (!int.TryParse(value.AsSpan(0, 4), out var year) || !int.TryParse(value.AsSpan(5, 2), out var month) || !int.TryParse(value.AsSpan(8, 2), out var day)
            || ParseTime(value[11..]) is not { } time || month is < 1 or > 12 || day is < 1 or > 31 || year < 1)
        {
            return null;
        }

        // 2.x 只检查 day <= 31;这里更严一点:当月没有的日期(2 月 30 日)按「永不触发」拒掉,而不是让 DateTime 抛异常。
        return day <= DateTime.DaysInMonth(year, month) ? (year, month, day, time.Hour, time.Minute) : null;
    }
}
