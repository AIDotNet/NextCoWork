using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

/// <summary>
/// 用户选择的颜色(项目图标、项目组)是用户数据,不是主题色,所以允许是 hex 字符串。
/// 参数是 0–1 的不透明度,用于组胶囊的浅色底。
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || !Color.TryParse(hex, out var color))
        {
            return null;
        }

        if (parameter is string alphaText && double.TryParse(alphaText, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
        {
            color = Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
        }

        return new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>会话列表的相对时间:「刚刚 / 3 分钟前 / 2 小时前 / 昨天 / 11-12」,按当前语言。</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public static readonly RelativeTimeConverter Instance = new();

    /// <summary>时间来源。测试里换成固定时钟,保证截图稳定。</summary>
    public static TimeProvider Clock { get; set; } = TimeProvider.System;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset time)
        {
            return null;
        }

        var elapsed = Clock.GetUtcNow() - time;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => NcwTexts.Get(NcwTextKind.TimeJustNow),
            { TotalHours: < 1 } => NcwTexts.Format(NcwTextKind.TimeMinutesAgo, (int)elapsed.TotalMinutes),
            { TotalDays: < 1 } => NcwTexts.Format(NcwTextKind.TimeHoursAgo, (int)elapsed.TotalHours),
            { TotalDays: < 2 } => NcwTexts.Get(NcwTextKind.TimeYesterday),
            _ => time.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>待处理交互数:超过 9 显示 9+。</summary>
public sealed class CountConverter : IValueConverter
{
    public static readonly CountConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n ? (n > 9 ? "9+" : n.ToString(CultureInfo.InvariantCulture)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>右侧面板页签的名称(页签类型 → 当前语言的文字)。</summary>
public sealed class InspectorTabTitleConverter : IValueConverter
{
    public static readonly InspectorTabTitleConverter Instance = new();

    public static string Title(InspectorTabKind kind) => NcwTexts.Get(kind switch
    {
        InspectorTabKind.Files => NcwTextKind.TabFiles,
        InspectorTabKind.Git => NcwTextKind.TabGit,
        InspectorTabKind.Context => NcwTextKind.TabContext,
        InspectorTabKind.History => NcwTextKind.TabHistory,
        _ => NcwTextKind.TabSubagents,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InspectorTabKind kind ? Title(kind) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>子代理卡片的状态与明细。数据来自 SubagentView.Of,这里只负责按语言写成文字。</summary>
public sealed class SubagentTextConverter : IValueConverter
{
    public static readonly SubagentTextConverter Status = new(detail: false);
    public static readonly SubagentTextConverter Detail = new(detail: true);

    private readonly bool _detail;

    private SubagentTextConverter(bool detail) => _detail = detail;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SubagentViewState view)
        {
            return null;
        }

        if (!_detail)
        {
            return NcwTexts.Get(view.Status switch
            {
                SubagentStatus.Queued => NcwTextKind.SubagentQueued,
                SubagentStatus.Running => NcwTextKind.SubagentRunning,
                SubagentStatus.Done => NcwTextKind.SubagentDone,
                SubagentStatus.Error => NcwTextKind.SubagentError,
                _ => NcwTextKind.SubagentAborted,
            });
        }

        var elapsed = SubagentView.FormatElapsed(view.Elapsed);
        return view.CurrentTool is { } tool
            ? NcwTexts.Format(NcwTextKind.SubagentDetailWithTool, tool, view.ToolCalls, elapsed)
            : NcwTexts.Format(NcwTextKind.SubagentDetail, view.ToolCalls, elapsed);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>带一个参数的文字:<c>ConverterParameter</c> 是 <see cref="NcwTextKind"/> 的名字,绑定值是参数。</summary>
public sealed class TextFormatConverter : IValueConverter
{
    public static readonly TextFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string name && Enum.TryParse<NcwTextKind>(name, out var kind)
            ? NcwTexts.Format(kind, value)
            : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
