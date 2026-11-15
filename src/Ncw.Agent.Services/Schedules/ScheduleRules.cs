namespace Ncw.Agent.Services.Schedules;

public static class ScheduleKinds
{
    public const string Once = "once";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
}

/// <summary>调度规则(2.x 的 <c>ScheduleRule</c>):一次 / 每天 / 每周几。<see cref="At"/> 是 <c>YYYY-MM-DDTHH:MM</c>,<see cref="Time"/> 是 <c>HH:MM</c>,都按任务的时区解释。</summary>
public sealed record ScheduleRule(string Kind, string? At = null, string? Time = null, IReadOnlyList<int>? Weekdays = null);

/// <summary>当天窗口内重复:从规则的开始时刻起,每 <see cref="IntervalMinutes"/> 分钟一次,直到 <see cref="EndTime"/>(含)。</summary>
public sealed record RepeatWindow(bool Enabled, string? EndTime = null, int? IntervalMinutes = null)
{
    public static readonly RepeatWindow Off = new(false);
}

/// <summary>
/// 调度规则的求值。<b>算法只有一份</b>:Core 的 <see cref="Ncw.Core.Domain.ScheduleMath"/>(控制端的「下次触发预览」与这里共用,AGENTS.md §3 规则 3);
/// 这里只把 Agent 的规则类型转成 Core 的,签名不变。
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>设备本地时区的 IANA 名;取不到时是 <c>UTC</c>。</summary>
    public static string SystemTimeZone() => Ncw.Core.Domain.ScheduleMath.SystemTimeZone();

    /// <summary>认得出的时区返回它,否则 null。</summary>
    public static TimeZoneInfo? FindZone(string id) => Ncw.Core.Domain.ScheduleMath.FindZone(id);

    /// <summary>规则的下一次触发(不含当天窗口重复)。</summary>
    public static long? NextAt(ScheduleRule rule, string timezone, long after) => Ncw.Core.Domain.ScheduleMath.NextAt(Spec(rule), timezone, after);

    /// <summary>规则的下一次触发,含可选的当天窗口重复。</summary>
    public static long? NextOccurrence(ScheduleRule rule, string timezone, RepeatWindow? repeat, long after) =>
        Ncw.Core.Domain.ScheduleMath.NextOccurrence(Spec(rule), timezone, repeat is null ? null : new Ncw.Core.Domain.RepeatSpec(repeat.Enabled, repeat.EndTime, repeat.IntervalMinutes), after);

    internal static long LocalToUtc(int year, int month, int day, int hour, int minute, TimeZoneInfo zone) =>
        Ncw.Core.Domain.ScheduleMath.LocalToUtc(year, month, day, hour, minute, zone);

    internal static (int Hour, int Minute)? ParseTime(string? value) => Ncw.Core.Domain.ScheduleMath.ParseTime(value);

    internal static (int Year, int Month, int Day, int Hour, int Minute)? ParseLocalDateTime(string? value) => Ncw.Core.Domain.ScheduleMath.ParseLocalDateTime(value);

    private static Ncw.Core.Domain.ScheduleRuleSpec Spec(ScheduleRule rule) => new(rule.Kind, rule.At, rule.Time, rule.Weekdays);
}
