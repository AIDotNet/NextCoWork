using System.IO.Compression;
using ZXing;
using ZXing.QrCode;

namespace Ncw.MobileSmoke;

/// <summary>把文本画成二维码 PNG(8 位灰度,每个模块 <c>scale</c> 像素,四周 4 个模块的静区),给模拟器的虚拟相机当「相机看到的画面」。</summary>
internal static class QrPng
{
    /// <summary>
    /// 在 1920x1080 的画布上平铺很多份缩小的二维码(间隔留白):模拟器的 imagefile 相机会把图片放大(~1.6 倍)并只露出其中的一部分,
    /// 单个居中的二维码常常被裁掉;平铺之后,不管露出哪一块,画面里总有几份完整的二维码。
    /// </summary>
    public static void WriteWide(string path, string content, int canvasHeight = 1080, int scale = 4)
    {
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M });
        var quiet = 4;
        var cell = (matrix.Width + (2 * quiet)) * scale;
        var canvasWidth = canvasHeight * 16 / 9;
        var raw = new byte[(canvasWidth + 1) * canvasHeight];
        for (var y = 0; y < canvasHeight; y++)
        {
            var row = y * (canvasWidth + 1);
            raw[row] = 0;
            for (var x = 0; x < canvasWidth; x++)
            {
                var mx = ((x % cell) / scale) - quiet;
                var my = ((y % cell) / scale) - quiet;
                var dark = mx >= 0 && my >= 0 && mx < matrix.Width && my < matrix.Height && matrix[mx, my];
                raw[row + 1 + x] = dark ? (byte)0 : (byte)255;
            }
        }

        Save(path, canvasWidth, canvasHeight, raw);
    }

    /// <summary>校准图:1280x720,四角不同灰度的方块 + 中心十字 + 一圈边框,用来看模拟器 imagefile 相机怎么缩放 / 裁剪 / 翻转。</summary>
    public static void WriteCalibration(string path)
    {
        const int w = 1280, h = 720;
        var raw = new byte[(w + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * (w + 1);
            for (var x = 0; x < w; x++)
            {
                byte v = 255;
                if (x < 12 || y < 12 || x >= w - 12 || y >= h - 12)
                {
                    v = 128;
                }

                if (x < 160 && y < 160)
                {
                    v = 0; // 左上:黑
                }
                else if (x >= w - 160 && y < 160)
                {
                    v = 70; // 右上
                }
                else if (x >= w - 160 && y >= h - 160)
                {
                    v = 150; // 右下
                }
                else if (x < 160 && y >= h - 160)
                {
                    v = 200; // 左下
                }

                if (Math.Abs(x - (w / 2)) < 6 || Math.Abs(y - (h / 2)) < 6)
                {
                    v = 0; // 中心十字
                }

                raw[row + 1 + x] = v;
            }
        }

        Save(path, w, h, raw);
    }

    public static void Write(string path, string content, int scale = 10)
    {
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M });
        var quiet = 4;
        var modules = matrix.Width + (2 * quiet);
        var size = modules * scale;
        var raw = new byte[(size + 1) * size];
        for (var y = 0; y < size; y++)
        {
            var row = y * (size + 1);
            raw[row] = 0; // 滤波器类型:none
            for (var x = 0; x < size; x++)
            {
                var mx = (x / scale) - quiet;
                var my = (y / scale) - quiet;
                var dark = mx >= 0 && my >= 0 && mx < matrix.Width && my < matrix.Height && matrix[mx, my];
                raw[row + 1 + x] = dark ? (byte)0 : (byte)255;
            }
        }

        Save(path, size, size, raw);
    }

    private static void Save(string path, int width, int height, byte[] raw)
    {
        using var stream = File.Create(path);
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(stream, "IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 0, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", []);
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(BigEndian(data.Length));
        stream.Write(name);
        stream.Write(data);
        var crc = Crc32([.. name, .. data]);
        stream.Write(BigEndian((int)crc));
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
