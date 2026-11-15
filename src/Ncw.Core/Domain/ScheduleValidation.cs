namespace Ncw.Core.Domain;

/// <summary>定时任务表单的一项问题(稳定的枚举,界面按语言写成文字;和设备拒绝时的 <c>schedules.invalid.*</c> key 一一对应)。</summary>
public enum ScheduleProblem
{
    Name,
    Prompt,
    Rule,
    Timezone,
    Repeat,
    NeverFires,
}

/// <summary>
/// 定时任务的表单校验与「下次触发预览」(纯函数,Agent 的 <c>ScheduledTaskService</c> 用同一套限制与同一个 <see cref="ScheduleMath"/>)。
/// 顺序和设备一致:名称 → 提示词 → 规则 → 时区 → 窗口 → 永不触发。
/// </summary>
public static class ScheduleValidation
{
    public const int MaxNameLength = 200;
    public const int MaxPromptLength = 100_000;

    public static IReadOnlyList<ScheduleProblem> Validate(string name, string prompt, ScheduleRuleSpec rule, string timezone, RepeatSpec repeat, long now)
    {
        var problems = new List<ScheduleProblem>();
        if (name.Trim().Length is 0 or > MaxNameLength)
        {
            problems.Add(ScheduleProblem.Name);
        }

        if (prompt.Trim().Length is 0 or > MaxPromptLength)
        {
            problems.Add(ScheduleProblem.Prompt);
        }

        var ruleOk = rule.Kind switch
        {
            ScheduleRuleKinds.Once => ScheduleMath.ParseLocalDateTime(rule.At) is not null,
            ScheduleRuleKinds.Daily => ScheduleMath.ParseTime(rule.Time) is not null,
            ScheduleRuleKinds.Weekly => ScheduleMath.ParseTime(rule.Time) is not null && rule.Weekdays is { Count: > 0 } days && days.All(d => d is >= 0 and <= 6),
            _ => false,
        };
        if (!ruleOk)
        {
            problems.Add(ScheduleProblem.Rule);
        }

        var zoneOk = ScheduleMath.FindZone(timezone) is not null;
        if (!zoneOk)
        {
            problems.Add(ScheduleProblem.Timezone);
        }

        var repeatOk = !repeat.Enabled || (ScheduleMath.ParseTime(repeat.EndTime) is not null && repeat.IntervalMinutes is >= 1);
        if (!repeatOk)
        {
            problems.Add(ScheduleProblem.Repeat);
        }

        // 只有前面都过了才有意义地问「会不会触发」(过去的一次性时间在界面上看起来完全正常,所以必须算一次)
        if (problems.Count == 0 && ScheduleMath.NextOccurrence(rule, timezone, repeat, now) is null)
        {
            problems.Add(ScheduleProblem.NeverFires);
        }

        return problems;
    }

    /// <summary>从 <paramref name="after"/> 起往后最多 <paramref name="count"/> 次触发(UTC 毫秒);规则不合法或不会再触发时少于 count 条(可能为空)。</summary>
    public static IReadOnlyList<long> Preview(ScheduleRuleSpec rule, string timezone, RepeatSpec repeat, long after, int count)
    {
        var result = new List<long>(count);
        var cursor = after;
        for (var i = 0; i < count; i++)
        {
            if (ScheduleMath.NextOccurrence(rule, timezone, repeat, cursor) is not { } next)
            {
                break;
            }

            result.Add(next);
            cursor = next;
        }

        return result;
    }
}
