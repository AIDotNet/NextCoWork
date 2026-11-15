using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;

namespace Ncw.Mobile;

/// <summary>
/// 相机帧里的二维码解码(纯托管 ZXing.Net,不依赖 Google Play 服务和平台相机 API,所以能在单测里直接用生成的二维码图验证)。
/// 输入是 YUV 帧的亮度(Y)平面 —— Android 的 <c>ImageReader</c>(YUV_420_888)和 iOS 的 <c>kCVPixelFormatType_420YpCbCr8</c> 都能直接给出。
/// QR 的定位图案与旋转无关,传感器方向(竖屏时通常转了 90°)不需要先转正。
/// </summary>
public sealed class QrFrameDecoder
{
    /// <summary>扫到的文本长度上限(和 <c>NcwLinkParser.MaxLength</c> 一致:配对二维码不会更长,更长的一定不是)。</summary>
    public const int MaxTextLength = 4096;

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryHarder = true,
            TryInverted = true,
        },
    };

    /// <summary>
    /// 在一帧里找二维码。<paramref name="rowStride"/> 是亮度平面每行的字节数(可能比 <paramref name="width"/> 大)。
    /// 找到返回 true 与文本(已 <see cref="QrScanText.Normalize"/>);没找到、帧不合法返回 false。
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> luma, int width, int height, int rowStride, out string text)
    {
        text = "";
        if (width <= 0 || height <= 0 || rowStride < width || luma.Length < (long)rowStride * (height - 1) + width)
        {
            return false;
        }

        var compact = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            luma.Slice(y * rowStride, width).CopyTo(compact.AsSpan(y * width, width));
        }

        var source = new PlanarYUVLuminanceSource(compact, width, height, 0, 0, width, height, false);
        try
        {
            // 单码读取器在画面里有很多个二维码时(一面二维码墙、屏幕上并排两个)会把不同码的定位图案混在一起而失败 → 失败再用多码读取器,取第一个能用的
            if (_reader.Decode(source) is { } single && QrScanText.Normalize(single.Text) is { } first)
            {
                text = first;
                return true;
            }

            var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true, [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } };
            var results = new QRCodeMultiReader().decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), hints);
            foreach (var result in results ?? [])
            {
                if (QrScanText.Normalize(result.Text) is { } normalized)
                {
                    text = normalized;
                    return true;
                }
            }
        }
        catch (ReaderException)
        {
        }

        return false;
    }
}

/// <summary>扫描结果文本的整理(纯函数)。解码器给出什么就给界面层什么——是不是 NextCoWork 的配对码由共享层的 <c>PairingQr.TryParse</c> 判断,这里只挡明显不能用的内容。</summary>
public static class QrScanText
{
    /// <summary>去掉首尾空白;空、超长、含控制字符(换行以外的)返回 null。</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        return text.Length > QrFrameDecoder.MaxTextLength || text.Any(static c => char.IsControl(c)) ? null : text;
    }
}
