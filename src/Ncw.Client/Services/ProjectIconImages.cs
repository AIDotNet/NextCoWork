using System.Buffers.Binary;

namespace Ncw.Client.Services;

/// <summary>
/// 项目自定义图标图片的规格(docs/design/03 §1「栏 1」)。图标在项目栏 / 手机项目列表里是 40×40 的方块,
/// 存 96×96 的正方形就够 2.4 倍屏用;解码后每个图标常驻约 36 KB,存储(和以后的账户同步)是几 KB 的 JPEG,有透明像素时才用 PNG。
/// </summary>
public static class ProjectIconSpec
{
    /// <summary>存储与显示的边长(像素)。</summary>
    public const int PixelSize = 96;

    /// <summary>选中的文件最多多大(编码后的字节数,解码前要整体读进内存)。</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 不能边解码边缩小的格式(PNG / GIF / BMP)最多多少像素:它们要先按原尺寸解码再缩,这一步的瞬时内存是 宽 × 高 × 4 字节(2500 万像素 ≈ 100 MB)。
    /// </summary>
    public const long MaxPixels = 25_000_000;

    /// <summary>JPEG / WebP 的解码器能直接按缩小后的尺寸解码(JPEG 最多 1/8),上限放宽到这里。</summary>
    public const long MaxScalablePixels = 200_000_000;

    /// <summary>布局里存的图片(base64)最长多少字符;超过的视为坏数据,不解码(防止同步来的数据撑爆内存)。</summary>
    public const int MaxStoredLength = 256 * 1024;

    /// <summary>不透明图片编码成 JPEG 的质量。</summary>
    public const int JpegQuality = 85;
}

/// <summary>图标图片的格式(只认这几种,解码器都由 Avalonia / Skia 提供)。</summary>
public enum IconImageFormat
{
    Png,
    Jpeg,
    Gif,
    Bmp,
    WebP,
}

/// <summary>从文件头读出的尺寸;<see cref="Orientation"/> 是 EXIF 方向(1–8,没有 = 1)。</summary>
public readonly record struct IconImageHeader(IconImageFormat Format, int Width, int Height, int Orientation = 1)
{
    /// <summary>解码器能否直接按缩小后的尺寸解码(不必先解出原尺寸)。</summary>
    public bool CanDecodeScaled => Format is IconImageFormat.Jpeg or IconImageFormat.WebP;
}

/// <summary>选图标图片为什么失败。</summary>
public enum IconPickError
{
    /// <summary>文件或像素数超过 <see cref="ProjectIconSpec"/> 的上限。</summary>
    TooLarge,

    /// <summary>不是认得的图片,或解码失败。</summary>
    Unsupported,
}

/// <summary>选图标图片的结果:<see cref="Image"/> 是压缩好的图片(base64),失败时为 null 并给出 <see cref="Error"/>。</summary>
public sealed record PickedIcon(string? Image, IconPickError? Error)
{
    public static PickedIcon Ok(string image) => new(image, null);

    public static PickedIcon Fail(IconPickError error) => new(null, error);
}

/// <summary>图标图片处理里与界面无关的纯函数:读文件头、判断内存预算、按 EXIF 方向摆正像素。解码和编码在 UI 层(Avalonia)。</summary>
public static class IconImages
{
    /// <summary>读文件头需要的字节数(JPEG 的尺寸在 EXIF / ICC 段之后,留足余量)。</summary>
    public const int HeaderBytes = 512 * 1024;

    /// <summary>按文件大小和文件头判断能不能处理;能处理返回 null。</summary>
    public static IconPickError? Check(long fileBytes, IconImageHeader? header)
    {
        if (fileBytes > ProjectIconSpec.MaxFileBytes)
        {
            return IconPickError.TooLarge;
        }

        if (header is not { } h || h.Width <= 0 || h.Height <= 0)
        {
            return IconPickError.Unsupported;
        }

        var pixels = (long)h.Width * h.Height;
        return pixels > (h.CanDecodeScaled ? ProjectIconSpec.MaxScalablePixels : ProjectIconSpec.MaxPixels) ? IconPickError.TooLarge : null;
    }

    /// <summary>从文件开头的字节读出格式、尺寸和方向;认不出返回 null。</summary>
    public static IconImageHeader? ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return new IconImageHeader(IconImageFormat.Png, (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        }

        if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            return new IconImageHeader(IconImageFormat.Gif, BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        }

        if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
        {
            var dib = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
            return dib == 12
                ? new IconImageHeader(IconImageFormat.Bmp, BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]))
                : new IconImageHeader(IconImageFormat.Bmp, Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[18..])), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])));
        }

        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return ReadWebP(data);
        }

        return data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 ? ReadJpeg(data) : null;
    }

    private static IconImageHeader? ReadWebP(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return new IconImageHeader(IconImageFormat.WebP, BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return new IconImageHeader(IconImageFormat.WebP, (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return new IconImageHeader(IconImageFormat.WebP, Read24(data[24..]) + 1, Read24(data[27..]) + 1);
        }

        return null;

        static int Read24(ReadOnlySpan<byte> b) => b[0] | (b[1] << 8) | (b[2] << 16);
    }

    /// <summary>JPEG:顺着段往下找 SOFn(尺寸),路上遇到 APP1 的 EXIF 就读方向。</summary>
    private static IconImageHeader? ReadJpeg(ReadOnlySpan<byte> data)
    {
        var orientation = 1;
        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF)
            {
                return null;
            }

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // 填充字节
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2; // 没有长度的标记
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null; // 图像数据开始了还没见到 SOF
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (length < 2)
            {
                return null;
            }

            var segment = data.Slice(i + 4, Math.Max(0, Math.Min(length - 2, data.Length - i - 4)));
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                return segment.Length >= 5
                    ? new IconImageHeader(IconImageFormat.Jpeg, BinaryPrimitives.ReadUInt16BigEndian(segment[3..]), BinaryPrimitives.ReadUInt16BigEndian(segment[1..]), orientation)
                    : null;
            }

            if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                orientation = ExifOrientation(segment[6..]);
            }

            i += 2 + length;
        }

        return null;
    }

    /// <summary>TIFF 结构里 IFD0 的 0x0112(Orientation);读不到按 1。</summary>
    private static int ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return 1;
        }

        var little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M'))
        {
            return 1;
        }

        var ifd = (int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]));
        if (ifd < 8 || ifd + 2 > tiff.Length)
        {
            return 1;
        }

        var count = U16(tiff, ifd, little);
        for (var n = 0; n < count; n++)
        {
            var entry = ifd + 2 + (n * 12);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            if (U16(tiff, entry, little) == 0x0112)
            {
                var value = U16(tiff, entry + 8, little);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }

    private static int U16(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    /// <summary>
    /// 把一块 <paramref name="size"/>×<paramref name="size"/>、每像素 4 字节的正方形像素按 EXIF 方向摆正(手机照片常见 6 = 顺时针转 90°)。
    /// 只搬像素、不关心通道顺序;方向是 1 时原样返回同一个数组。
    /// </summary>
    public static byte[] Orient(byte[] pixels, int size, int orientation)
    {
        if (orientation is < 2 or > 8)
        {
            return pixels;
        }

        var result = new byte[pixels.Length];
        var m = size - 1;
        for (var dy = 0; dy < size; dy++)
        {
            for (var dx = 0; dx < size; dx++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (m - dx, dy),
                    3 => (m - dx, m - dy),
                    4 => (dx, m - dy),
                    5 => (dy, dx),
                    6 => (dy, m - dx),
                    7 => (m - dy, m - dx),
                    _ => (m - dy, dx),
                };
                Buffer.BlockCopy(pixels, ((sy * size) + sx) * 4, result, ((dy * size) + dx) * 4, 4);
            }
        }

        return result;
    }

    /// <summary>有没有不完全不透明的像素(每像素 4 字节,alpha 在第 4 个字节:BGRA / RGBA 都一样)。有就存 PNG,没有就存更小的 JPEG。</summary>
    public static bool HasTransparency(ReadOnlySpan<byte> pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0xFF)
            {
                return true;
            }
        }

        return false;
    }
}
