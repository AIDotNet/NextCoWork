using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ncw.Client.Services;

namespace Ncw.UI.Converters;

/// <summary>
/// 项目图标图片(<see cref="Ncw.Client.Stores.ProjectAppearance.Image"/> 的 base64)→ 位图。
/// 按字符串实例缓存(弱引用):项目栏、手机列表、图标窗口的预览拿到的是同一个字符串,只解码一份;外观换掉后旧位图随字符串一起回收。
/// 解码时按 <see cref="ProjectIconSpec.PixelSize"/> 限宽,别处同步来的大图也不会按原尺寸进内存;坏数据返回 null(界面退回文字图标的底色)。
/// </summary>
public sealed class ProjectIconImageConverter : IValueConverter
{
    public static readonly ProjectIconImageConverter Instance = new();

    private static readonly ConditionalWeakTable<string, Bitmap?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 and <= ProjectIconSpec.MaxStoredLength } image ? Cache.GetValue(image, Decode) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Bitmap? Decode(string base64)
    {
        try
        {
            using var stream = new MemoryStream(System.Convert.FromBase64String(base64), writable: false);
            return Bitmap.DecodeToWidth(stream, ProjectIconSpec.PixelSize, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>
/// 项目图标图片 → 铺满的图片画刷(<see cref="Stretch.UniformToFill"/>)。图标是圆角方块,用画刷做 <c>Border.Background</c> 才能跟着 CornerRadius 裁圆角
/// (<c>ClipToBounds</c> 只裁矩形)。位图来自 <see cref="ProjectIconImageConverter"/> 的缓存,画刷也按字符串缓存,多处显示共用一份。
/// </summary>
public sealed class ProjectIconBrushConverter : IValueConverter
{
    public static readonly ProjectIconBrushConverter Instance = new();

    private static readonly ConditionalWeakTable<string, IBrush?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 and <= ProjectIconSpec.MaxStoredLength } image
            ? Cache.GetValue(image, key => ProjectIconImageConverter.Instance.Convert(key, typeof(Bitmap), null, culture) is Bitmap bitmap
                ? new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }
                : null)
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>两个值的字符串相等(色块的「当前选中」:色块的颜色 = 窗口里选中的颜色),不区分大小写。</summary>
public sealed class StringEqualsMultiConverter : IMultiValueConverter
{
    public static readonly StringEqualsMultiConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is string a && values[1] is string b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
