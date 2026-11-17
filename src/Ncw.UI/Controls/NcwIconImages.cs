using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ncw.Client.Services;

namespace Ncw.UI.Controls;

/// <summary>
/// 项目图标图片的解码与压缩(规格见 <see cref="ProjectIconSpec"/>)。为了尽量少占内存:
/// <list type="number">
///   <item>先只读文件头(<see cref="IconImages.HeaderBytes"/>)拿到尺寸,超出预算的直接拒绝,不解码;</item>
///   <item>按「短边 = 96」解码:JPEG / WebP 由解码器直接按缩小后的尺寸解码(JPEG 最多 1/8),不会先解出原图;</item>
///   <item>居中裁成 96×96、按 EXIF 方向摆正,立刻释放解码出来的大图;</item>
///   <item>没有透明像素存 JPEG(质量 <see cref="ProjectIconSpec.JpegQuality"/>,通常几 KB),有透明像素才存 PNG。</item>
/// </list>
/// 显示时每个图标常驻 96×96×4 ≈ 36 KB(<see cref="Converters.ProjectIconImageConverter"/> 按字符串缓存,同一张图只解码一份)。
/// </summary>
public static class NcwIconImages
{
    /// <summary>从选中的文件压缩出图标。解码在线程池上做,不卡界面。</summary>
    public static async Task<PickedIcon> CompressAsync(Func<Task<Stream>> open, long size)
    {
        if (size > ProjectIconSpec.MaxFileBytes)
        {
            return PickedIcon.Fail(IconPickError.TooLarge);
        }

        try
        {
            byte[] head;
            await using (var stream = await open())
            {
                head = await ReadHeadAsync(stream, IconImages.HeaderBytes);
            }

            var header = IconImages.ReadHeader(head);
            if (IconImages.Check(size, header) is { } error)
            {
                return PickedIcon.Fail(error);
            }

            await using var body = await open();
            return await Task.Run(() => Compress(body, header!.Value));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return PickedIcon.Fail(IconPickError.Unsupported);
        }
    }

    /// <summary>解码、裁剪、摆正、编码(同步;测试直接调)。</summary>
    internal static PickedIcon Compress(Stream stream, IconImageHeader header)
    {
        const int target = ProjectIconSpec.PixelSize;
        Bitmap decoded;
        try
        {
            // 按短边解码:另一边按比例更长,之后居中裁掉
            decoded = header.Width <= header.Height
                ? Bitmap.DecodeToWidth(stream, target, BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(stream, target, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return PickedIcon.Fail(IconPickError.Unsupported);
        }

        using (decoded)
        {
            if (decoded.Format is not { BitsPerPixel: 32 } format)
            {
                return PickedIcon.Fail(IconPickError.Unsupported);
            }

            var width = decoded.PixelSize.Width;
            var height = decoded.PixelSize.Height;
            var side = Math.Min(target, Math.Min(width, height));
            if (side <= 0)
            {
                return PickedIcon.Fail(IconPickError.Unsupported);
            }

            var stride = side * 4;
            var pixels = new byte[stride * side];
            CopyPixels(decoded, new PixelRect((width - side) / 2, (height - side) / 2, side, side), pixels, stride);
            pixels = IconImages.Orient(pixels, side, header.Orientation);
            var transparent = IconImages.HasTransparency(pixels);

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var icon = new Bitmap(format, decoded.AlphaFormat ?? AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(side, side), new Vector(96, 96), stride);
                using var output = new MemoryStream();
                icon.Save(output, transparent ? PngBitmapEncoderOptions.Default : new JpegBitmapEncoderOptions { Quality = ProjectIconSpec.JpegQuality });
                return PickedIcon.Ok(Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length));
            }
            finally
            {
                handle.Free();
            }
        }
    }

    private static void CopyPixels(Bitmap source, PixelRect rect, byte[] buffer, int stride)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(rect, handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }
    }

    private static async Task<byte[]> ReadHeadAsync(Stream stream, int max)
    {
        var buffer = new byte[max];
        var read = 0;
        while (read < max)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, max - read));
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == max ? buffer : buffer[..read];
    }
}
