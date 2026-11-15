namespace Ncw.Mobile;

/// <summary>输出像素的字节顺序。</summary>
public enum PreviewPixelOrder
{
    /// <summary>B、G、R、A(Avalonia 的 <c>PixelFormat.Bgra8888</c>)。</summary>
    Bgra,

    /// <summary>R、G、B、A(<c>PixelFormat.Rgba8888</c>)。Android 的 Avalonia / Skia 上 BGRA 位图显示成黑(模拟器实测),用这个。</summary>
    Rgba,
}

/// <summary>
/// 相机预览的软件渲染:把 YUV_420_888 的一帧缩小、转正、转成 BGRA8888,供 Avalonia 的 <c>Image</c> 显示(扫码页的预览)。
/// 不用原生预览控件(<c>TextureView</c> / <c>SurfaceView</c> 放进 Avalonia 的 <c>NativeControlHost</c> 在 Android 上拿不到绘制面,见 docs/design/11 §6),
/// 也就不依赖平台的预览生命周期;代价是 ~15 帧 / 秒、低分辨率,扫二维码够用。纯函数,有单测。
/// </summary>
public static class YuvPreviewRenderer
{
    /// <summary>
    /// 渲染一帧。<paramref name="rotationDegrees"/>:顺时针转多少度才是正的(0 / 90 / 180 / 270;后置摄像头 = 传感器方向 − 显示方向)。
    /// 输出宽度约 <paramref name="targetWidth"/>(按整数步长抽样,不做平滑)。返回像素(默认 BGRA,见 <paramref name="order"/>)和尺寸;帧不合法返回 false。
    /// </summary>
    public static bool TryRender(
        ReadOnlySpan<byte> y, int yRowStride, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int uvRowStride, int uvPixelStride,
        int width, int height, int rotationDegrees, int targetWidth, out byte[] bgra, out int outWidth, out int outHeight, PreviewPixelOrder order = PreviewPixelOrder.Bgra)
    {
        bgra = [];
        outWidth = outHeight = 0;
        if (width < 2 || height < 2 || targetWidth < 2 || yRowStride < width || uvRowStride < 1 || uvPixelStride < 1 || rotationDegrees % 90 != 0)
        {
            return false;
        }

        var step = Math.Max(1, Math.Max(width, height) / Math.Max(targetWidth, 2));
        var sw = width / step;
        var sh = height / step;
        var rotation = ((rotationDegrees % 360) + 360) % 360;
        var swapped = rotation is 90 or 270;
        outWidth = swapped ? sh : sw;
        outHeight = swapped ? sw : sh;

        var neededY = ((long)yRowStride * (height - 1)) + width;
        var uvWidth = (width + 1) / 2;
        var uvHeight = (height + 1) / 2;
        var neededUv = ((long)uvRowStride * (uvHeight - 1)) + ((long)uvPixelStride * (uvWidth - 1)) + 1;
        if (y.Length < neededY || u.Length < neededUv || v.Length < neededUv)
        {
            return false;
        }

        bgra = new byte[outWidth * outHeight * 4];
        for (var oy = 0; oy < outHeight; oy++)
        {
            for (var ox = 0; ox < outWidth; ox++)
            {
                int sx, sy;
                switch (rotation)
                {
                    case 90:
                        sx = oy;
                        sy = sh - 1 - ox;
                        break;
                    case 180:
                        sx = sw - 1 - ox;
                        sy = sh - 1 - oy;
                        break;
                    case 270:
                        sx = sw - 1 - oy;
                        sy = ox;
                        break;
                    default:
                        sx = ox;
                        sy = oy;
                        break;
                }

                var px = sx * step;
                var py = sy * step;
                var luma = y[(py * yRowStride) + px];
                var uvIndex = ((py / 2) * uvRowStride) + ((px / 2) * uvPixelStride);
                int cb = u[uvIndex] - 128;
                int cr = v[uvIndex] - 128;
                var r = luma + ((91881 * cr) >> 16);
                var g = luma - (((22554 * cb) + (46802 * cr)) >> 16);
                var b = luma + ((116130 * cb) >> 16);
                var o = ((oy * outWidth) + ox) * 4;
                bgra[o] = Clamp(order == PreviewPixelOrder.Bgra ? b : r);
                bgra[o + 1] = Clamp(g);
                bgra[o + 2] = Clamp(order == PreviewPixelOrder.Bgra ? r : b);
                bgra[o + 3] = 255;
            }
        }

        return true;
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
