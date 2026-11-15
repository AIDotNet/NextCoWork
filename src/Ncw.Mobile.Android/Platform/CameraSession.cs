using System.Diagnostics;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using Java.Lang;
using Java.Nio;
using Ncw.Mobile;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// 一次相机会话:打开后置摄像头(没有就用第一个),一路 YUV_420_888 的 <see cref="ImageReader"/>(只取亮度平面给 <see cref="QrFrameDecoder"/> 解码),
/// 同一路帧还(可选地)软件渲染成预览位图推给 <see cref="QrPreviewImage"/>。回调都在专用的 <see cref="HandlerThread"/> 上;解码最多 ~8 帧 / 秒、同一时刻只解一帧(丢弃中间帧),预览 ~15 帧 / 秒。
/// <see cref="Dispose"/> 幂等,关闭相机设备、ImageReader、线程。
/// </summary>
internal sealed class CameraSession : IDisposable
{
    private const int TargetWidth = 1280;
    private const int TargetHeight = 720;
    private static readonly TimeSpan MinDecodeInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MinPreviewInterval = TimeSpan.FromMilliseconds(66);
    private const int PreviewWidth = 480;

    private readonly Context _context;
    private readonly QrPreviewImage? _preview;
    private readonly Action<string> _onCode;
    private readonly Action _onFailure;
    private readonly QrFrameDecoder _decoder = new();
    private HandlerThread? _thread;
    private Handler? _handler;
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;
    private int _sensorOrientation;
    private int _decoding;
    private long _lastDecode;
    private long _lastPreview;
    private volatile bool _disposed;

    public CameraSession(Context context, QrPreviewImage? preview, Action<string> onCode, Action onFailure)
    {
        _context = context;
        _preview = preview;
        _onCode = onCode;
        _onFailure = onFailure;
    }

    public void Start()
    {
        var manager = (CameraManager)_context.GetSystemService(Context.CameraService)!;
        var id = PickCamera(manager) ?? throw new InvalidOperationException("No camera");
        var characteristics = manager.GetCameraCharacteristics(id);
        _sensorOrientation = (characteristics.Get(CameraCharacteristics.SensorOrientation) as Integer)?.IntValue() ?? 90;
        var map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)!;
        var analysis = Closest(map.GetOutputSizes((int)ImageFormatType.Yuv420888), TargetWidth, TargetHeight);

        _thread = new HandlerThread("ncw-camera");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);

        _reader = ImageReader.NewInstance(analysis.Width, analysis.Height, ImageFormatType.Yuv420888, 2)!;
        _reader.SetOnImageAvailableListener(new FrameListener(this), _handler);

        manager.OpenCamera(id, new DeviceCallback(this), _handler);
    }

    private static string? PickCamera(CameraManager manager)
    {
        var ids = manager.GetCameraIdList();
        foreach (var id in ids)
        {
            if (manager.GetCameraCharacteristics(id).Get(CameraCharacteristics.LensFacing) is Integer facing && facing.IntValue() == (int)LensFacing.Back)
            {
                return id;
            }
        }

        return ids.Length > 0 ? ids[0] : null;
    }

    /// <summary>选和目标面积最接近的尺寸(偏好 16:9,和 4:3 一样都能用)。</summary>
    private static Size Closest(Size[]? sizes, int width, int height)
    {
        if (sizes is null or { Length: 0 })
        {
            return new Size(width, height);
        }

        Size best = sizes[0];
        long bestScore = long.MaxValue;
        foreach (var size in sizes)
        {
            var score = System.Math.Abs((long)size.Width * size.Height - (long)width * height);
            if (score < bestScore)
            {
                best = size;
                bestScore = score;
            }
        }

        return best;
    }

    private void StartCapture(CameraDevice device)
    {
        if (_disposed)
        {
            device.Close();
            return;
        }

        _device = device;
        var surfaces = new List<Surface> { _reader!.Surface! };

#pragma warning disable CA1422 // 这个重载在 API 28 起标为过时,但在 26–36 上都能用;SessionConfiguration 版本需要 API 28
        device.CreateCaptureSession(surfaces, new SessionCallback(this), _handler);
#pragma warning restore CA1422
    }

    private void StartRepeating(CameraCaptureSession session)
    {
        if (_disposed || _device is null)
        {
            return;
        }

        _session = session;
        var request = _device.CreateCaptureRequest(CameraTemplate.Preview)!;
        request.AddTarget(_reader!.Surface!);

#pragma warning disable CA1422 // Camera2 的键值要装箱成 java.lang.Integer(绑定里 API 33 起标过时,没有替代)
        request.Set(CaptureRequest.ControlAfMode!, new Integer((int)ControlAFMode.ContinuousPicture));
#pragma warning restore CA1422
        session.SetRepeatingRequest(request.Build()!, null, _handler);
    }

    private void OnFrame(ImageReader reader)
    {
        // Image 必须显式 Close():Java 对象的 Dispose 不会归还缓冲,ImageReader 只有 2 个槽,用满就抛 IllegalStateException(相机线程上的未处理异常会带走整个进程)
        var image = reader.AcquireLatestImage();
        if (image is null)
        {
            return;
        }

        try
        {
            if (!_disposed)
            {
                ProcessFrame(image);
            }
        }
        catch (Java.Lang.Exception e)
        {
            global::Android.Util.Log.Warn("Ncw", "camera frame failed: " + e.Message);
        }
        finally
        {
            image.Close();
        }
    }

    private void ProcessFrame(Image image)
    {
        var now = Stopwatch.GetTimestamp();
        var wantPreview = _preview is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastPreview), now) >= MinPreviewInterval;
        var wantDecode = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDecode), now) >= MinDecodeInterval && Interlocked.CompareExchange(ref _decoding, 1, 0) == 0;
        if (!wantPreview && !wantDecode)
        {
            return;
        }

        var planes = image.GetPlanes()!;
        var yBuffer = planes[0].Buffer!;
        var luma = new byte[yBuffer.Remaining()];
        yBuffer.Get(luma);
        int width = image.Width, height = image.Height, stride = planes[0].RowStride;
        if (wantPreview)
        {
            Volatile.Write(ref _lastPreview, now);
            var uBuffer = planes[1].Buffer!;
            var vBuffer = planes[2].Buffer!;
            var u = new byte[uBuffer.Remaining()];
            var v = new byte[vBuffer.Remaining()];
            uBuffer.Get(u);
            vBuffer.Get(v);
            if (YuvPreviewRenderer.TryRender(luma, stride, u, v, planes[1].RowStride, planes[1].PixelStride, width, height, UprightRotation(), PreviewWidth, out var pixels, out var w, out var h, PreviewPixelOrder.Rgba))
            {
                _preview!.Push(pixels, w, h);
            }
        }

        if (!wantDecode)
        {
            return;
        }

        Volatile.Write(ref _lastDecode, now);
        _ = Task.Run(() =>
        {
            try
            {
                if (!_disposed && _decoder.TryDecode(luma, width, height, stride, out var text))
                {
                    _onCode(text);
                }
            }
            finally
            {
                Volatile.Write(ref _decoding, 0);
            }
        });
    }

    /// <summary>让预览转正需要顺时针转的角度:后置摄像头 = 传感器方向 − 当前显示方向。</summary>
    private int UprightRotation()
    {
#pragma warning disable CA1422 // Activity.WindowManager.DefaultDisplay 在 API 30 起过时(Activity.Display 需要 API 30);minSdk 26
        var rotation = AndroidHost.Activity?.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
#pragma warning restore CA1422
        var display = rotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0,
        };
        return (_sensorOrientation - display + 360) % 360;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _session?.Close();
        }
        catch (Java.Lang.Exception)
        {
        }

        _device?.Close();
        _reader?.Close();
        _thread?.QuitSafely();
        _session = null;
        _device = null;
        _reader = null;
    }

    private sealed class DeviceCallback(CameraSession owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.StartCapture(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            owner._onFailure();
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            global::Android.Util.Log.Warn("Ncw", "camera error " + error);
            camera.Close();
            owner._onFailure();
        }
    }

    private sealed class SessionCallback(CameraSession owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.StartRepeating(session);

        public override void OnConfigureFailed(CameraCaptureSession session) => owner._onFailure();
    }

    private sealed class FrameListener(CameraSession owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                owner.OnFrame(reader);
            }
        }
    }
}
