namespace Ncw.Core.Domain;

/// <summary>连续活跃天数(<see cref="UsageActivity.ComputeStreaks"/> 的结果)。</summary>
public readonly record struct Streaks(int Current, int Longest);

/// <summary>
/// 活跃日的日历运算(2.x 的 <c>shared/domain/usage-activity.ts</c>):连续天数、日期序列、周几。Agent 算「连续天数」要用它,控制端画热力图网格也要用它——
/// 放在 Core 里让两边共用同一套日期算术(AGENTS.md §3 规则 3),否则热力图上明明连着七个方块,指标卡却写着「连续 6 天」。
/// <para>
/// ★ 全部换算成「UTC 纪元日序号」再算:输入是本地日期串 <c>YYYY-MM-DD</c>,不带时刻。判断两天是否相邻不能拿两个本地时间戳相减除以一天——
/// 夏令时切换那天只有 23 小时(或 25 小时),连续天数就在那一天断掉。把日期当 <b>UTC 午夜</b>解析得到纯粹的日历天序号(UTC 没有夏令时,相邻日期序号必差 1),只当整数用。
/// </para>
/// </summary>
public static class UsageActivity
{
    /// <summary>毫秒 → 设备本地时区的日期 <c>YYYY-MM-DD</c>(日桶的 <c>day</c> 就是这么来的)。这是唯一碰时区的函数,时区由调用方给(测试可注入)。</summary>
    public static string LocalDayOf(long unixMs, TimeZoneInfo zone)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}";
    }

    /// <summary><c>YYYY-MM-DD</c> → UTC 纪元日序号;非法输入返回 null(而不是碰巧算出一个数)。</summary>
    public static long? DayIndex(string day)
    {
        if (day.Length != 10 || day[4] != '-' || day[7] != '-')
        {
            return null;
        }

        if (!int.TryParse(day.AsSpan(0, 4), out var y) || !int.TryParse(day.AsSpan(5, 2), out var m) || !int.TryParse(day.AsSpan(8, 2), out var d)
            || y < 1 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
        {
            return null;
        }

        return (long)(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalDays;
    }

    /// <summary>UTC 纪元日序号 → <c>YYYY-MM-DD</c>(<see cref="DayIndex"/> 的逆运算)。</summary>
    public static string DayFromIndex(long index)
    {
        var d = DateTime.UnixEpoch.AddDays(index);
        return $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
    }

    /// <summary>该日期是星期几(0 = 周日),热力图据此把方块对到正确的行。非法日期返回 0。</summary>
    public static int WeekdayOf(string day) =>
        DayIndex(day) is { } index ? (int)(((index + 4) % 7 + 7) % 7) : 0; // 1970-01-01 是周四 = 4

    /// <summary>含两端的连续日期序列。起点晚于终点或非法时返回空(不是倒序也不是死循环)。</summary>
    public static IReadOnlyList<string> DayRange(string from, string to)
    {
        if (DayIndex(from) is not { } start || DayIndex(to) is not { } end || start > end)
        {
            return [];
        }

        var days = new List<string>();
        for (var i = start; i <= end; i++)
        {
            days.Add(DayFromIndex(i));
        }

        return days;
    }

    /// <summary>
    /// 连续活跃天数。<paramref name="days"/> 不要求有序或去重。
    /// ★「当前连续」以<b>今天或昨天</b>为终点都算数:只认今天的话,每天零点一过这个数字就掉到 0,直到当天第一次请求才恢复——用户看到的是自己的连胜断了。
    /// </summary>
    public static Streaks ComputeStreaks(IEnumerable<string> days, string today)
    {
        var indices = days.Select(DayIndex).OfType<long>().Distinct().Order().ToList();
        if (indices.Count == 0)
        {
            return new Streaks(0, 0);
        }

        var longest = 1;
        var run = 1;
        for (var i = 1; i < indices.Count; i++)
        {
            run = indices[i] == indices[i - 1] + 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        var current = 0;
        if (DayIndex(today) is { } todayIndex)
        {
            var last = indices[^1];
            if (last == todayIndex || last == todayIndex - 1)
            {
                current = 1;
                for (var i = indices.Count - 1; i > 0; i--)
                {
                    if (indices[i] != indices[i - 1] + 1)
                    {
                        break;
                    }

                    current++;
                }
            }
        }

        return new Streaks(current, longest);
    }
}
