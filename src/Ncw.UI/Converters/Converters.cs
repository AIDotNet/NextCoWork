using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

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

/// <summary>会话列表的相对时间:「刚刚 / 3 分钟前 / 2 小时前 / 昨天 / 11-12」。</summary>
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
            { TotalMinutes: < 1 } => "刚刚",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} 分钟前",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} 小时前",
            { TotalDays: < 2 } => "昨天",
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
