using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Localization;
using TKCode.Arc.Localization;

namespace Ncw.UI.Converters;

/// <summary>按钮被禁用的原因(<see cref="Gate"/>)→ 文字;允许时为 null(不显示提示)。</summary>
public sealed class GateTextConverter : IValueConverter
{
    public static readonly GateTextConverter Instance = new();

    public static string? Text(Gate gate) => gate.Reason switch
    {
        GateReason.None => null,
        GateReason.Offline => NcwTexts.Get(NcwTextKind.GateOffline),
        GateReason.NeedsMember => NcwTexts.Get(NcwTextKind.GateNeedsMember),
        GateReason.NeedsOwner => NcwTexts.Get(NcwTextKind.GateNeedsOwner),
        _ => NcwTexts.Get(NcwTextKind.GateLocalOnly),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Gate gate ? Text(gate) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>读不了这一页的原因 → 整页说明。</summary>
public sealed class ForbiddenTextConverter : IValueConverter
{
    public static readonly ForbiddenTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Gate gate
        ? NcwTexts.Get(gate.Reason == GateReason.NeedsOwner ? NcwTextKind.ForbiddenNeedsOwner : NcwTextKind.ForbiddenNeedsMember)
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>失败(<see cref="Failure"/>)→ 当前语言的说明。</summary>
public sealed class FailureTextConverter : IValueConverter
{
    public static readonly FailureTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Failure failure => KeyText.Error(failure),
        string key => KeyText.Error(key),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>设备给的稳定字符串(或枚举)→ 文字,参数是 <see cref="KeyText"/> 的前缀。</summary>
public sealed class KeyTextConverter : IValueConverter
{
    public static readonly KeyTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : KeyText.Get(parameter as string ?? "", value.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>诊断 key → 文字。</summary>
public sealed class DiagnosticTextConverter : IValueConverter
{
    public static readonly DiagnosticTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string key ? KeyText.Diagnostic(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>值的名字等于参数(枚举页签 / 分组的选中判断)。</summary>
public sealed class NameEqualsConverter : IValueConverter
{
    public static readonly NameEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>值的名字不等于参数。</summary>
public sealed class NameNotEqualsConverter : IValueConverter
{
    public static readonly NameNotEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>状态 → 色调(<c>ok</c> / <c>warn</c> / <c>bad</c> / <c>mute</c>),给 <c>NcwStatusDot</c>。</summary>
public sealed class ToneConverter : IValueConverter
{
    public static readonly ToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() switch
    {
        "Connected" or "success" or "ok" => "ok",
        "Connecting" or "queued" or "running" or "skipped" => "warn",
        "Failed" or "error" or "aborted" => "bad",
        _ => "mute",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>时间 → 文字。参数 <c>full</c> = 带年份;否则「MM-dd HH:mm」。</summary>
public sealed class DateTimeTextConverter : IValueConverter
{
    public static readonly DateTimeTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var time = value switch { DateTimeOffset t => t, long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms), _ => (DateTimeOffset?)null };
        return time?.ToLocalTime().ToString(parameter as string == "full" ? "yyyy-MM-dd HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字节数 → 「12.3 MB」。</summary>
public sealed class BytesTextConverter : IValueConverter
{
    public static readonly BytesTextConverter Instance = new();

    public static string Text(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is long b ? Text(b) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>紧凑数字 1.2K / 3.4M。</summary>
public sealed class CompactNumberConverter : IValueConverter
{
    public static readonly CompactNumberConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long n => UsageView.Compact(n),
        int n => UsageView.Compact(n),
        double d => UsageView.Compact((long)d),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>毫秒 / 时长 → 「1.2 s」「340 ms」「2 min」。</summary>
public sealed class DurationTextConverter : IValueConverter
{
    public static readonly DurationTextConverter Instance = new();

    public static string Text(double ms) => ms switch
    {
        < 1000 => Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " ms",
        < 60_000 => (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s",
        _ => (ms / 60_000).ToString("0.#", CultureInfo.InvariantCulture) + " min",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TimeSpan t => Text(t.TotalMilliseconds),
        long ms => Text(ms),
        int ms => Text(ms),
        double ms => Text(ms),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0–1 → 「98%」。</summary>
public sealed class PercentTextConverter : IValueConverter
{
    public static readonly PercentTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? Math.Round(d * 100).ToString(CultureInfo.InvariantCulture) + "%" : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>数组 → 「A, B, C」。</summary>
public sealed class JoinConverter : IValueConverter
{
    public static readonly JoinConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>星期索引(0 = 周日)→ 简称。</summary>
public sealed class WeekdayTextConverter : IValueConverter
{
    public static readonly WeekdayTextConverter Instance = new();

    public static string Text(int day) => NcwTexts.Get(day switch
    {
        0 => NcwTextKind.WeekdaySun,
        1 => NcwTextKind.WeekdayMon,
        2 => NcwTextKind.WeekdayTue,
        3 => NcwTextKind.WeekdayWed,
        4 => NcwTextKind.WeekdayThu,
        5 => NcwTextKind.WeekdayFri,
        _ => NcwTextKind.WeekdaySat,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int day ? Text(day) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>定时任务一行的规则摘要:「每天 09:00」「每周 一 二 09:00」「2026-11-20 09:00」,窗口重复附在后面。</summary>
public sealed class ScheduleSummaryConverter : IValueConverter
{
    public static readonly ScheduleSummaryConverter Instance = new();

    public static string Text(string kind, string? at, string? time, IEnumerable<int> weekdays, bool repeat, string? repeatEnd, int? interval)
    {
        var rule = kind switch
        {
            ScheduleRuleKinds.Once => NcwTexts.Format(NcwTextKind.SchedSummaryOnce, (at ?? "").Replace('T', ' ')),
            ScheduleRuleKinds.Weekly => NcwTexts.Format(NcwTextKind.SchedSummaryWeekly, string.Join(' ', weekdays.Order().Select(WeekdayTextConverter.Text)), time),
            _ => NcwTexts.Format(NcwTextKind.SchedSummaryDaily, time),
        };
        return repeat ? rule + " · " + NcwTexts.Format(NcwTextKind.SchedSummaryRepeat, interval ?? 0, repeatEnd) : rule;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is ScheduleTaskRowViewModel r
        ? Text(r.Kind, r.At, r.Time, r.Weekdays, r.RepeatEnabled, r.RepeatEnd, r.RepeatInterval)
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>多个值填进一条带占位符的文字(<c>ConverterParameter</c> = <see cref="NcwTextKind"/> 的名字)。</summary>
public sealed class TextFormatMultiConverter : IMultiValueConverter
{
    public static readonly TextFormatMultiConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string name && Enum.TryParse<NcwTextKind>(name, out var kind) ? NcwTexts.Format(kind, [.. values]) : null;
}

/// <summary>问题枚举(表单校验)→ 文字,参数是前缀(<c>SchedProblem</c> / <c>HookProblem</c> / <c>McpProblem</c>)。</summary>
public sealed class FormProblemTextConverter : IValueConverter
{
    public static readonly FormProblemTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IEnumerable<ScheduleProblem> list => Join(list.Select(p => KeyText.Get("SchedProblem", p.ToString()))),
        IEnumerable<HookProblem> list => Join(list.Select(p => KeyText.Get("HookProblem", p.ToString()))),
        IEnumerable<McpProblem> list => Join(list.Select(p => KeyText.Get("McpProblem", p.ToString()))),
        _ => null,
    };

    private static string Join(IEnumerable<string> items) => string.Join(" · ", items);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>日序列图的单位文字(随指标)。</summary>
public sealed class MetricUnitConverter : IValueConverter
{
    public static readonly MetricUnitConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is UsageMetric m
        ? NcwTexts.Get(m switch { UsageMetric.Tokens => NcwTextKind.UsageUnitTokens, UsageMetric.Requests => NcwTextKind.UsageUnitRequests, _ => NcwTextKind.UsageUnitCost })
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>设备连接 → 状态点色调。</summary>
public sealed class ConnectionToneConverter : IValueConverter
{
    public static readonly ConnectionToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DeviceConnection.Connected => "ok",
        DeviceConnection.Connecting or DeviceConnection.Sleeping => "warn",
        DeviceConnection.Revoked => "bad",
        _ => "mute",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>成败 → 状态点色调。</summary>
public sealed class BoolToneConverter : IValueConverter
{
    public static readonly BoolToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "ok" : "bad";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>任务时区的本地时间 → 「周一 11-16 09:00」(预览用)。</summary>
public sealed class ZonedTimeTextConverter : IValueConverter
{
    public static readonly ZonedTimeTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is DateTimeOffset t
        ? WeekdayTextConverter.Text((int)t.DayOfWeek) + " " + t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
