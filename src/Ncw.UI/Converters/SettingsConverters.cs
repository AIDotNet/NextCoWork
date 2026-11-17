using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Converters;

/// <summary>设置导航项 → 当前语言的名字(<c>Settings</c> + 导航项名,和分区同一套词条)。</summary>
public sealed class SettingsPageTitleConverter : IValueConverter
{
    public static readonly SettingsPageTitleConverter Instance = new();

    public static string Text(SettingsPage page) => KeyText.Get("Settings", page.ToString());

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is SettingsPage p ? Text(p) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>当前导航项 == 参数(导航高亮)。</summary>
public sealed class SettingsPageIsConverter : IValueConverter
{
    public static readonly SettingsPageIsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is SettingsPage p && parameter is SettingsPage q && p == q;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>当前分区 == 参数(内容区只显示当前分区)。</summary>
public sealed class SettingsSectionIsConverter : IValueConverter
{
    public static readonly SettingsSectionIsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is SettingsSection s && parameter is SettingsSection t && s == t;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>设置分区 → 图标(列表栏)。和导航里同一套图标。</summary>
public sealed class SettingsSectionIconConverter : IValueConverter
{
    public static readonly SettingsSectionIconConverter Instance = new();

    public static LucideIconKind Icon(SettingsSection section) => section switch
    {
        SettingsSection.Account => LucideIconKind.CircleUser,
        SettingsSection.Devices => LucideIconKind.Monitor,
        SettingsSection.Appearance => LucideIconKind.Palette,
        SettingsSection.Shortcuts => LucideIconKind.Keyboard,
        SettingsSection.Notifications => LucideIconKind.Bell,
        SettingsSection.About => LucideIconKind.Info,
        SettingsSection.General => LucideIconKind.SlidersHorizontal,
        SettingsSection.Models => LucideIconKind.Cpu,
        SettingsSection.Skills => LucideIconKind.Sparkles,
        SettingsSection.Commands => LucideIconKind.SquareSlash,
        SettingsSection.Hooks => LucideIconKind.Webhook,
        SettingsSection.Mcp => LucideIconKind.Plug,
        SettingsSection.Plugins => LucideIconKind.Puzzle,
        SettingsSection.Usage => LucideIconKind.ChartColumn,
        SettingsSection.Storage => LucideIconKind.HardDrive,
        SettingsSection.Remote => LucideIconKind.Wifi,
        SettingsSection.Gateway => LucideIconKind.Network,
        SettingsSection.Project => LucideIconKind.FolderCog,
        _ => LucideIconKind.Settings,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is SettingsSection s ? Icon(s) : LucideIconKind.Settings;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
