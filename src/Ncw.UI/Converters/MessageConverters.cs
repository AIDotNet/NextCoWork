using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

/// <summary>消息时间:今天只显示 <c>HH:mm</c>,别的日子带上日期(本地时区、当前界面语言的短格式)。</summary>
public sealed class MessageTimeConverter : IValueConverter
{
    public static readonly MessageTimeConverter Instance = new();

    public static string Text(DateTimeOffset at, DateTimeOffset now, CultureInfo culture)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", culture);
        return local.Date == today ? time : $"{local.ToString("d", culture)} {time}";
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset at && at.Year > 1970 ? Text(at, DateTimeOffset.Now, culture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>消息操作失败的稳定 key(<see cref="MessageErrorKeys"/>)→ 文字。</summary>
public sealed class MessageErrorTextConverter : IValueConverter
{
    public static readonly MessageErrorTextConverter Instance = new();

    public static string? Text(string? key) => key switch
    {
        null => null,
        MessageErrorKeys.Busy => NcwTexts.Get(NcwTextKind.MsgErrBusy),
        MessageErrorKeys.ResendFailed => NcwTexts.Get(NcwTextKind.MsgErrResend),
        MessageErrorKeys.Unavailable => NcwTexts.Get(NcwTextKind.MsgErrUnavailable),
        MessageErrorKeys.Forbidden => NcwTexts.Get(NcwTextKind.MsgErrForbidden),
        MessageErrorKeys.Gone => NcwTexts.Get(NcwTextKind.MsgErrGone),
        _ => NcwTexts.Get(NcwTextKind.MsgErrFailed),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 工具条按钮的提示:能用时是动作名(<c>ConverterParameter</c> = <see cref="NcwTextKind"/> 的成员名),
/// 不能用(有 Run 在跑 / 设备断开 / 只读)时是原因,别让用户对着一个灰按钮猜。
/// </summary>
public sealed class MessageActionTipConverter : IValueConverter
{
    public static readonly MessageActionTipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && Enum.TryParse<NcwTextKind>(name, out var kind)
            ? NcwTexts.Get(kind)
            : NcwTexts.Get(NcwTextKind.MsgLocked);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 图片字节 → 位图,按 <c>ConverterParameter</c> 的宽度解码(缩略图 320、灯箱 1600),不把原图整张解成大位图。
/// 解码失败返回 null(界面显示的是「失败」状态,不会崩)。位图归调用方(绑定的 Image 控件)持有。
/// </summary>
public sealed class ImageBytesConverter : IValueConverter
{
    public static readonly ImageBytesConverter Instance = new();

    public static Bitmap? Decode(byte[]? data, int width)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Decode(value as byte[], parameter is string text && int.TryParse(text, CultureInfo.InvariantCulture, out var width) ? width : 320);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
