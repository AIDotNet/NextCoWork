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

/// <summary>供应商自动发现的来源 → 名字(<c>claude-code</c> → Claude Code;一组来源用「、」/ 逗号连起来)。认不出的原样显示。</summary>
public sealed class DiscoverySourceTextConverter : IValueConverter
{
    public static readonly DiscoverySourceTextConverter Instance = new();

    public static string Text(string? source) => KeyText.Get("DiscoverySource", source);

    /// <param name="parameter">可选:一个格式词条名(<see cref="NcwTextKind"/>),把名字填进去(「也见于 {0}」)。</param>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value switch
        {
            string s => Text(s),
            IEnumerable<string> list => string.Join(", ", list.Select(Text)),
            _ => "",
        };
        return parameter is string name && Enum.TryParse<NcwTextKind>(name, out var kind) ? NcwTexts.Format(kind, text) : text;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>候选的状态(<c>new</c> / <c>imported</c> / <c>conflict</c>)→ 文字。</summary>
public sealed class DiscoveryStatusTextConverter : IValueConverter
{
    public static readonly DiscoveryStatusTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => KeyText.Get("DiscoveryStatus", value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
