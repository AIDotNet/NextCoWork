using Android;
using Android.Content.PM;
using Ncw.Client.Services;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// <see cref="IQrScanner"/> + <see cref="IQrPreviewProvider"/>(docs/design/11 §2):Camera2 取帧 + 纯托管 ZXing.Net(<see cref="QrFrameDecoder"/>)解码,
/// 预览是软件渲染进 Avalonia <c>Image</c> 的低分辨率位图(<see cref="QrPreviewImage"/>,<see cref="YuvPreviewRenderer"/>)。
/// <b>选型</b>:不用 ML Kit(需要 Google Play 服务或 ~3 MB 的模型,在没有 GMS 的设备上不可用)和 CameraX 的 Xamarin 绑定(版本停在 1.0.x,和 AndroidX 其他包的版本链纠缠),
/// 不依赖任何额外的 Android 绑定包;解码和预览渲染都在 <c>Ncw.Mobile.Shared</c> 里有单测。
/// 只在扫码页打开、用户点了「开始扫描」时才打开相机;读到第一个二维码、被取消、Activity 暂停、扫码页离开时立刻关闭相机。
/// </summary>
internal sealed class AndroidQrScanner : IQrScanner, IQrPreviewProvider
{
    private readonly object _gate = new();
    private TaskCompletionSource<string?>? _scan;
    private CameraSession? _session;
    private QrPreviewImage? _preview;
    private bool _resumed = true;

    public bool IsSupported => HasCamera;

    private static bool HasCamera => AndroidHost.Context.PackageManager?.HasSystemFeature(PackageManager.FeatureCameraAny) == true;

    public async Task<CameraAccess> RequestAccessAsync(CancellationToken ct = default)
    {
        if (!HasCamera)
        {
            return CameraAccess.NoCamera;
        }

        if (AndroidHost.Context.CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
        {
            return CameraAccess.Granted;
        }

        if (AndroidHost.Activity is not { } activity)
        {
            return CameraAccess.Denied;
        }

        var results = await ActivityResults.RequestPermissionsAsync(activity, [Manifest.Permission.Camera], ActivityResults.CameraPermission).ConfigureAwait(true);
        return results.Length > 0 && results[0] == Permission.Granted ? CameraAccess.Granted : CameraAccess.Denied;
    }

    public async Task<string?> ScanAsync(CancellationToken ct = default)
    {
        TaskCompletionSource<string?> scan;
        lock (_gate)
        {
            _scan?.TrySetResult(null); // 同一时刻只有一次扫描
            StopSessionLocked();
            scan = _scan = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await using var registration = ct.Register(() => Finish(scan, null));
        TryStart(scan);
        try
        {
            return await scan.Task.ConfigureAwait(true);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_scan, scan))
                {
                    _scan = null;
                    StopSessionLocked();
                }
            }
        }
    }

    /// <summary>创建扫码页的相机预览(一个页面一个;页面离开视图树时 <see cref="QrPreviewImage.Closed"/> 通知回来,关掉相机)。</summary>
    public object CreatePreview()
    {
        var image = new QrPreviewImage();
        image.Closed += () => DetachPreview(image);
        lock (_gate)
        {
            _preview = image;
        }

        return image;
    }

    private void DetachPreview(QrPreviewImage image)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_preview, image))
            {
                _preview = null;
                StopSessionLocked(); // 预览没了(页面离开)就关相机;VM 会在页面离开时取消扫描
            }
        }
    }

    public void OnActivityPaused()
    {
        lock (_gate)
        {
            _resumed = false;
            StopSessionLocked();
        }
    }

    public void OnActivityResumed()
    {
        TaskCompletionSource<string?>? scan;
        lock (_gate)
        {
            _resumed = true;
            scan = _scan;
        }

        if (scan is not null)
        {
            TryStart(scan);
        }
    }

    private void TryStart(TaskCompletionSource<string?> scan)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_scan, scan) || _session is not null || !_resumed)
            {
                return;
            }

            try
            {
                _session = new CameraSession(AndroidHost.Context, _preview, text => Finish(scan, text), () => Finish(scan, null));
                _session.Start();
            }
            catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                global::Android.Util.Log.Warn("Ncw", "camera start failed: " + e.Message);
                _session?.Dispose();
                _session = null;
                Finish(scan, null);
            }
        }
    }

    private void Finish(TaskCompletionSource<string?> scan, string? text)
    {
        scan.TrySetResult(text);
        lock (_gate)
        {
            if (ReferenceEquals(_scan, scan))
            {
                StopSessionLocked();
            }
        }
    }

    private void StopSessionLocked()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }
}
