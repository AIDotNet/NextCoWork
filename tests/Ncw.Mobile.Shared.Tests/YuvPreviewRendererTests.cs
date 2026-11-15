using Ncw.Mobile;

namespace Ncw.Mobile.Shared.Tests;

/// <summary>相机预览的软件渲染:尺寸、旋转方向、颜色转换、步长与异常输入。</summary>
public class YuvPreviewRendererTests
{
    /// <summary>4x2 的帧:亮度 = 10,20,30,40 / 50,60,70,80;色度中性(128)。</summary>
    private static (byte[] Y, byte[] U, byte[] V) Frame()
    {
        byte[] y = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] chroma = [128, 128];
        return (y, chroma, chroma);
    }

    private static byte[] Lumas(byte[] bgra) => [.. Enumerable.Range(0, bgra.Length / 4).Select(i => bgra[i * 4 + 1])];

    [Fact]
    public void Without_rotation_the_frame_keeps_its_size_and_neutral_chroma_is_gray()
    {
        var (y, u, v) = Frame();

        Assert.True(YuvPreviewRenderer.TryRender(y, 4, u, v, 2, 1, 4, 2, 0, 360, out var bgra, out var w, out var h));

        Assert.Equal((4, 2), (w, h));
        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80], Lumas(bgra));
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal((bgra[i * 4], bgra[i * 4 + 2], bgra[i * 4 + 3]), (bgra[i * 4 + 1], bgra[i * 4 + 1], (byte)255)));
    }

    [Fact]
    public void Ninety_degrees_clockwise_moves_the_top_left_to_the_top_right()
    {
        var (y, u, v) = Frame();

        Assert.True(YuvPreviewRenderer.TryRender(y, 4, u, v, 2, 1, 4, 2, 90, 360, out var bgra, out var w, out var h));

        Assert.Equal((2, 4), (w, h));
        // 转后每行两个像素:第一行 = 原来的第一列自下而上 (50,10),依此类推
        Assert.Equal([50, 10, 60, 20, 70, 30, 80, 40], Lumas(bgra));
    }

    [Fact]
    public void A_hundred_eighty_degrees_reverses_the_frame_and_270_is_the_mirror_of_90()
    {
        var (y, u, v) = Frame();

        Assert.True(YuvPreviewRenderer.TryRender(y, 4, u, v, 2, 1, 4, 2, 180, 360, out var r180, out _, out _));
        Assert.Equal([80, 70, 60, 50, 40, 30, 20, 10], Lumas(r180));

        Assert.True(YuvPreviewRenderer.TryRender(y, 4, u, v, 2, 1, 4, 2, 270, 360, out var r270, out var w, out var h));
        Assert.Equal((2, 4), (w, h));
        Assert.Equal([40, 80, 30, 70, 20, 60, 10, 50], Lumas(r270));
    }

    [Fact]
    public void Padded_rows_and_interleaved_chroma_planes_are_read_with_their_strides()
    {
        // 行距 6(多出 2 个填充字节);色度每个采样占 2 字节(像 NV21 的交错布局),行距 4
        byte[] y = [10, 20, 30, 40, 99, 99, 50, 60, 70, 80, 99, 99];
        byte[] u = [128, 0, 128, 0];
        byte[] v = [128, 0, 128, 0];

        Assert.True(YuvPreviewRenderer.TryRender(y, 6, u, v, 4, 2, 4, 2, 0, 360, out var bgra, out _, out _));

        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80], Lumas(bgra));
    }

    [Fact]
    public void Chroma_shifts_the_colors_in_the_expected_direction()
    {
        byte[] y = [128, 128, 128, 128];
        byte[] cr = [200]; // 偏红
        byte[] cb = [128];

        Assert.True(YuvPreviewRenderer.TryRender(y, 2, cb, cr, 1, 1, 2, 2, 0, 360, out var bgra, out _, out _));

        Assert.True(bgra[2] > bgra[1] && bgra[2] > bgra[0], "red should dominate"); // BGRA:[2] = R
    }

    [Fact]
    public void The_rgba_order_swaps_only_the_red_and_blue_channels()
    {
        byte[] y = [128, 128, 128, 128];
        byte[] cr = [200];
        byte[] cb = [100];

        Assert.True(YuvPreviewRenderer.TryRender(y, 2, cb, cr, 1, 1, 2, 2, 0, 360, out var bgra, out _, out _));
        Assert.True(YuvPreviewRenderer.TryRender(y, 2, cb, cr, 1, 1, 2, 2, 0, 360, out var rgba, out _, out _, PreviewPixelOrder.Rgba));

        Assert.Equal((bgra[0], bgra[1], bgra[2], bgra[3]), (rgba[2], rgba[1], rgba[0], rgba[3]));
    }

    [Fact]
    public void Large_frames_are_decimated_toward_the_target_width()
    {
        var y = new byte[1280 * 720];
        var chroma = new byte[640 * 360];

        Assert.True(YuvPreviewRenderer.TryRender(y, 1280, chroma, chroma, 640, 1, 1280, 720, 90, 320, out var bgra, out var w, out var h));

        Assert.Equal(w * h * 4, bgra.Length);
        Assert.InRange(Math.Max(w, h), 200, 330);
    }

    [Theory]
    [InlineData(1, 1, 0, 100)]
    [InlineData(4, 2, 45, 100)]
    [InlineData(4, 2, 0, 1)]
    public void Impossible_geometry_is_rejected(int width, int height, int rotation, int target) =>
        Assert.False(YuvPreviewRenderer.TryRender(new byte[16], 4, new byte[4], new byte[4], 2, 1, width, height, rotation, target, out _, out _, out _));

    [Fact]
    public void Buffers_shorter_than_the_geometry_are_rejected()
    {
        Assert.False(YuvPreviewRenderer.TryRender(new byte[3], 4, new byte[2], new byte[2], 2, 1, 4, 2, 0, 100, out _, out _, out _));
        Assert.False(YuvPreviewRenderer.TryRender(new byte[8], 4, new byte[1], new byte[2], 2, 1, 4, 2, 0, 100, out _, out _, out _));
    }
}
