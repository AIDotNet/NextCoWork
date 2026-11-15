using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// 扫码页里的相机预览:一个 Avalonia <see cref="Image"/>,每帧由 <see cref="CameraSession"/> 经 <c>YuvPreviewRenderer</c> 渲染成低分辨率位图推进来
/// (「填满并居中裁剪」,不拉伸)。不是原生视图,所以能被圆角裁剪、随页面一起出现 / 消失,也不依赖平台的预览表面。
/// </summary>
internal sealed class QrPreviewImage : Image
{
    private Bitmap? _bitmap;
    private volatile bool _detached;

    public QrPreviewImage() => Stretch = Stretch.UniformToFill;

    public event Action? Closed;

    /// <summary>像素是 RGBA 字节序(<c>PreviewPixelOrder.Rgba</c>):BGRA 位图在 Android 上显示成黑。可以在任何线程调用;最多排一帧(上一帧还没画完就丢掉新的)。</summary>
    public void Push(byte[] rgba, int width, int height)
    {
        if (_detached || Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_detached)
                {
                    return;
                }

                // 每帧用像素数据新建一个不可变位图再换掉 Source
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
                Bitmap next;
                try
                {
                    next = new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
                }
                finally
                {
                    handle.Free();
                }

                var previous = _bitmap;
                _bitmap = next;
                Source = next;
                previous?.Dispose();
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
    }

    private int _pending;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _detached = true;
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
        Closed?.Invoke();
    }
}
