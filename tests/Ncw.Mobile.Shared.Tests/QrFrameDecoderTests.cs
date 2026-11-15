using Ncw.Mobile;
using Ncw.Protocol.Security;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Ncw.Mobile.Shared.Tests;

/// <summary>二维码帧解码:用 ZXing 生成真实的二维码图,再当作相机的亮度平面喂给解码器。</summary>
public class QrFrameDecoderTests
{
    /// <summary>生成亮度图:二维码画在中间,周围白边,模块放大 scale 倍;<paramref name="rowStride"/> 大于宽度时行尾填 0x55 噪声。</summary>
    private static (byte[] Luma, int Width, int Height, int Stride) Render(string content, int scale = 6, int quiet = 20, int extraStride = 0, bool invert = false)
    {
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M });
        var width = (matrix.Width * scale) + (2 * quiet);
        var height = (matrix.Height * scale) + (2 * quiet);
        var stride = width + extraStride;
        var luma = new byte[stride * height];
        Array.Fill(luma, (byte)0x55);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var mx = (x - quiet) / scale;
                var my = (y - quiet) / scale;
                var dark = x >= quiet && y >= quiet && mx < matrix.Width && my < matrix.Height && matrix[mx, my];
                luma[(y * stride) + x] = (byte)(dark ^ invert ? 20 : 235);
            }
        }

        return (luma, width, height, stride);
    }

    private static string PairUri()
    {
        var identity = PeerIdentity.Create("mac");
        return new PairingQr(identity.DeviceId, identity.Fingerprint, ["192.168.1.23:7810", "10.0.2.2:7810"], new byte[PairingQr.TokenLength], 4_000_000_000_000).ToUri();
    }

    [Fact]
    public void A_real_pairing_qr_decodes_and_parses_as_a_pairing_code()
    {
        var uri = PairUri();
        var (luma, w, h, stride) = Render(uri);

        Assert.True(new QrFrameDecoder().TryDecode(luma, w, h, stride, out var text));

        Assert.Equal(uri, text);
        Assert.True(PairingQr.TryParse(text, out _));
    }

    [Fact]
    public void Row_padding_in_the_camera_plane_is_skipped()
    {
        var (luma, w, h, stride) = Render("hello ncw", extraStride: 64);

        Assert.True(new QrFrameDecoder().TryDecode(luma, w, h, stride, out var text));
        Assert.Equal("hello ncw", text);
    }

    [Fact]
    public void A_rotated_frame_still_decodes_because_the_sensor_is_usually_turned_ninety_degrees()
    {
        var (luma, w, h, stride) = Render(PairUri());
        var rotated = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                rotated[(x * h) + (h - 1 - y)] = luma[(y * stride) + x];
            }
        }

        Assert.True(new QrFrameDecoder().TryDecode(rotated, h, w, h, out var text));
        Assert.True(PairingQr.TryParse(text, out _));
    }

    [Fact]
    public void A_light_on_dark_code_decodes()
    {
        var (luma, w, h, stride) = Render("inverted", invert: true);

        Assert.True(new QrFrameDecoder().TryDecode(luma, w, h, stride, out var text));
        Assert.Equal("inverted", text);
    }

    /// <summary>1280x720 的帧里铺满很多份相同的二维码(每份 ~250 像素,四周留白),像相机正对着一面贴满二维码的墙。</summary>
    private static (byte[] Luma, int Width, int Height) Tiled(string content, int scale = 5, int offsetX = 17, int offsetY = 9)
    {
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M });
        const int quiet = 4;
        var cell = (matrix.Width + (2 * quiet)) * scale;
        const int w = 1280, h = 720;
        var luma = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var mx = (((x + offsetX) % cell) / scale) - quiet;
                var my = (((y + offsetY) % cell) / scale) - quiet;
                var dark = mx >= 0 && my >= 0 && mx < matrix.Width && my < matrix.Height && matrix[mx, my];
                luma[(y * w) + x] = (byte)(dark ? 16 : 233);
            }
        }

        return (luma, w, h);
    }

    [Fact]
    public void A_wall_of_identical_codes_still_yields_one_of_them()
    {
        var uri = PairUri();
        var (luma, w, h) = Tiled(uri);

        Assert.True(new QrFrameDecoder().TryDecode(luma, w, h, w, out var text));

        Assert.Equal(uri, text);
    }

    [Fact]
    public void A_frame_without_a_code_is_not_an_error()
    {
        var noise = new byte[320 * 240];
        new Random(7).NextBytes(noise);

        Assert.False(new QrFrameDecoder().TryDecode(noise, 320, 240, 320, out var text));
        Assert.Equal("", text);
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 10, 5)]
    public void Impossible_frame_geometry_is_rejected(int width, int height, int stride) =>
        Assert.False(new QrFrameDecoder().TryDecode(new byte[100], width, height, stride, out _));

    [Fact]
    public void A_buffer_shorter_than_the_declared_geometry_is_rejected() =>
        Assert.False(new QrFrameDecoder().TryDecode(new byte[50], 10, 10, 10, out _));

    [Fact]
    public void Scan_text_is_trimmed_and_unusable_content_is_dropped()
    {
        Assert.Equal("a", QrScanText.Normalize("  a \n"));
        Assert.Null(QrScanText.Normalize(""));
        Assert.Null(QrScanText.Normalize("   "));
        Assert.Null(QrScanText.Normalize("a\u0000b"));
        Assert.Null(QrScanText.Normalize(new string('x', QrFrameDecoder.MaxTextLength + 1)));
    }
}
