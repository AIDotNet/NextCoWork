using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Converters;

/// <summary>手势文本 → 按平台画的样子(⌘K / Ctrl+K)。</summary>
public sealed class GestureTextConverter : IValueConverter
{
    public static readonly GestureTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ShortcutText.Format(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>意图 → 当前语言标题(快捷键页)。</summary>
public sealed class AppCommandTitleConverter : IValueConverter
{
    public static readonly AppCommandTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Ncw.Client.Shortcuts.AppCommand command ? ShortcutText.Title(command) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>命令面板一行的标题:数据项用数据本身,命令和设置分区按当前语言翻译(就是排序时用的 <c>SearchText</c>)。</summary>
public sealed class PaletteTitleConverter : IValueConverter
{
    public static readonly PaletteTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PaletteItemViewModel item ? item.Title ?? item.SearchText : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>命令面板一行的种类 → 图标 / 右侧的种类名。</summary>
public sealed class PaletteKindConverter : IValueConverter
{
    public static readonly PaletteKindConverter Icon = new(true);
    public static readonly PaletteKindConverter Label = new(false);

    private readonly bool _icon;

    private PaletteKindConverter(bool icon) => _icon = icon;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PaletteItemKind kind)
        {
            return null;
        }

        if (_icon)
        {
            return kind switch
            {
                PaletteItemKind.Session => LucideIconKind.MessageSquare,
                PaletteItemKind.Project => LucideIconKind.Folder,
                PaletteItemKind.File => LucideIconKind.FileText,
                PaletteItemKind.Settings => LucideIconKind.Settings,
                _ => LucideIconKind.ChevronRight,
            };
        }

        return NcwTexts.Get(kind switch
        {
            PaletteItemKind.Session => NcwTextKind.PaletteKindSession,
            PaletteItemKind.Project => NcwTextKind.PaletteKindProject,
            PaletteItemKind.File => NcwTextKind.PaletteKindFile,
            PaletteItemKind.Settings => NcwTextKind.PaletteKindSettings,
            _ => NcwTextKind.PaletteKindCommand,
        });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>整数 &gt; 0。</summary>
public sealed class PositiveConverter : IValueConverter
{
    public static readonly PositiveConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int n && n > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>待处理条的文字:这个会话里几项、别的会话里几项(docs/design/03 §11.3)。</summary>
public sealed class PendingBarTextConverter : IMultiValueConverter
{
    public static readonly PendingBarTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var here = values.Count > 0 && values[0] is int h ? h : 0;
        var elsewhere = values.Count > 1 && values[1] is int e ? e : 0;
        return (here, elsewhere) switch
        {
            ( > 0, > 0) => NcwTexts.Format(NcwTextKind.PendingBarBoth, here, elsewhere),
            ( > 0, _) => NcwTexts.Format(NcwTextKind.PendingBarHere, here),
            _ => NcwTexts.Format(NcwTextKind.PendingBarElsewhereOnly, elsewhere),
        };
    }
}

/// <summary>设置分区 → 当前语言的名字(和导航里的一样)。</summary>
public sealed class SettingsSectionTitleConverter : IValueConverter
{
    public static readonly SettingsSectionTitleConverter Instance = new();

    public static string Text(SettingsSection section) => KeyText.Get("Settings", section.ToString());

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is SettingsSection s ? Text(s) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
