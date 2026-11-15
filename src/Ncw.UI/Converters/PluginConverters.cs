using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Localization;
using TKCode.Arc.Localization;

namespace Ncw.UI.Converters;

/// <summary>撤销能力确认卡的标题(两个参数:能力名、插件名)。</summary>
public sealed class PluginRevokeTitleConverter : IValueConverter
{
    public static readonly PluginRevokeTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PendingPermissionRevoke r ? NcwTexts.Format(NcwTextKind.PluginRevokeTitle, r.Permission, r.PluginName) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>「旧版 → 新版」。</summary>
public sealed class PluginUpdateLineConverter : IValueConverter
{
    public static readonly PluginUpdateLineConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PluginUpdateRow row ? NcwTexts.Format(NcwTextKind.PluginUpdateLine, row.Dto.CurrentVersion, row.Dto.LatestVersion) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>「已更新 N 个,失败 M 个」。</summary>
public sealed class PluginUpdateResultConverter : IValueConverter
{
    public static readonly PluginUpdateResultConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PluginsUpdateAllResult result ? NcwTexts.Format(NcwTextKind.PluginUpdateResult, result.Updated.Length, result.Failed.Length) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字节数 → 「N 字节」之类的短文字用 <see cref="BytesTextConverter"/>;这里是活动日志的耗时「N 毫秒」。</summary>
public sealed class PluginDurationConverter : IValueConverter
{
    public static readonly PluginDurationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long ms ? NcwTexts.Format(NcwTextKind.PluginActivityMs, ms) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>数量 &gt; 0(绑定数组长度 / 集合个数给 IsVisible)。</summary>
public sealed class PluginAnyConverter : IValueConverter
{
    public static readonly PluginAnyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int n && n > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>值恰好是 true(可空布尔:null 和 false 都不算)。</summary>
public sealed class PluginIsTrueConverter : IValueConverter
{
    public static readonly PluginIsTrueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
