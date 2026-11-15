using Android.Content;
using Android.OS;
using Avalonia;
using Ncw.Client.Services;
using Ncw.UI;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// 宿主的装配与全局状态(一个进程一份):后端工厂、平台服务工厂、连接监督器、前后台事件的转发。
/// 在 <see cref="NcwAndroidApplication"/> 里、Application 创建之前调用 <see cref="Initialize"/>。
/// </summary>
internal static class AndroidHost
{
    private static ConnectionSupervisor? _supervisor;
    private static AndroidQrScanner? _scanner;
    private static AndroidPushRegistration? _push;
    private static AndroidAttachmentPicker? _picker;
    private static Context? _context;

    /// <summary>当前的 Activity(没有界面时为 null:权限请求、选择器都要有 Activity,拿不到就按拒绝处理)。</summary>
    public static MainActivity? Activity { get; set; }

    public static Context Context => _context ?? throw new InvalidOperationException("AndroidHost is not initialized");

    public static AndroidUrlLauncher UrlLauncher { get; } = new();

    public static void Initialize(Context context)
    {
        _context = context;
        var clientDirectory = Path.Combine(context.FilesDir!.AbsolutePath, "client");
        Directory.CreateDirectory(clientDirectory);
        // 界面偏好(ui.json)等控制端文件也放应用私有目录;NcwApp.ClientDirectory 读这个环境变量
        System.Environment.SetEnvironmentVariable("NCW_CLIENT_DIR", clientDirectory);
        NotificationChannels.EnsureCreated(context);
        AndroidAttachmentPicker.PurgeOldCaptures(context.CacheDir!.AbsolutePath);

        var deviceName = string.IsNullOrWhiteSpace(Build.Model) ? "Android" : Build.Model!;
        // 控制端身份:密文文件在应用私有目录,包裹密钥在 AndroidKeyStore(不可导出)。不能安全保存时 LoadOrCreate 抛 PlatformNotSupportedException,绝不降级为明文。
        var identity = new SealedIdentityStore(Path.Combine(clientDirectory, "controller-identity.bin"), new AndroidKeystoreSecretBox(), $"NextCoWork {deviceName}");

        NcwApp.BackendFactory = () =>
        {
            var backend = new RemoteOnlyBackend(identity, Path.Combine(clientDirectory, "devices.json"), deviceName);
            _supervisor = new ConnectionSupervisor(backend, log: message => global::Android.Util.Log.Info("NcwConn", message));
            NetworkMonitor.Start(context, _supervisor);
            return backend;
        };
        NcwApp.MobileHostFactory = () => new MobileHostServices
        {
            QrScanner = _scanner ??= new AndroidQrScanner(),
            Push = _push ??= new AndroidPushRegistration(),
            AttachmentPicker = _picker ??= new AndroidAttachmentPicker(),
        };
    }

    /// <summary>系统转来的 <c>nextcowork://</c> 链接 → 共享层的链接收件箱(解析、白名单、「等用户确认」都在共享层)。</summary>
    public static void SubmitLink(string uri) => NcwLinkInbox.Submit("link " + uri);

    public static void OnForeground() => Run(_supervisor?.EnteredForegroundAsync());

    public static void OnBackground() => Run(_supervisor?.EnteredBackgroundAsync());

    public static void OnResumed() => _scanner?.OnActivityResumed();

    public static void OnPaused() => _scanner?.OnActivityPaused();

    private static void Run(Task? task)
    {
        task?.ContinueWith(static t => global::Android.Util.Log.Warn("Ncw", $"connection policy failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
