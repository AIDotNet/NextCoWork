namespace Ncw.Client.Services;

// 移动端宿主(Ncw.Mobile.iOS / Ncw.Mobile.Android,见 docs/design/02 §A4 与 04 §8)要实现的平台能力。
// 接口在这里(Client 层,不引用 Avalonia 和任何平台包),宿主项目以后实现并在启动时放进 MobileHostServices;
// 桌面端和测试里这些服务缺省为 null / 「不支持」,界面据此显示备用入口(手动粘贴二维码内容、不提供附件来源),不会崩。

/// <summary>相机权限的结果。</summary>
public enum CameraAccess
{
    Granted,

    /// <summary>用户拒绝了(界面引导去系统设置,并提供「粘贴二维码内容」备用入口)。</summary>
    Denied,

    /// <summary>系统限制(家长控制 / 企业策略),用户改不了。</summary>
    Restricted,

    /// <summary>设备没有相机(模拟器、平板的少数型号)。</summary>
    NoCamera,
}

/// <summary>
/// 二维码扫描(扫码配对,docs/design/09 §3)。相机预览和解码是平台的事,由宿主实现;解析二维码内容用 <c>PairingQr.TryParse</c>,
/// 配对走 <c>DeviceActions.PairWithQrAsync</c>,都在共享层。
/// </summary>
public interface IQrScanner
{
    /// <summary>这个平台有扫码能力(没有就只提供手动粘贴)。</summary>
    bool IsSupported { get; }

    /// <summary>请求相机权限(首次会弹系统对话框)。</summary>
    Task<CameraAccess> RequestAccessAsync(CancellationToken ct = default);

    /// <summary>
    /// 开始扫描并等到读到一个二维码:返回它的文本内容。被取消(用户退出页面)返回 null。
    /// 同一时刻只有一次扫描;预览画面如果需要放进界面里,宿主同时实现 <see cref="IQrPreviewProvider"/>。
    /// </summary>
    Task<string?> ScanAsync(CancellationToken ct = default);
}

/// <summary>
/// 扫码页里相机预览的承载:宿主的原生预览控件(Avalonia 控件)。Client 不依赖 Avalonia,所以返回 <see cref="object"/>,
/// 由界面层放进预览区域。没有实现这个接口时界面画一个占位框。
/// </summary>
public interface IQrPreviewProvider
{
    /// <summary>创建一个预览控件(每次扫码页打开创建一个,关闭后由宿主释放)。</summary>
    object CreatePreview();
}

/// <summary>推送平台(对应协议里的 <c>apns</c> / <c>fcm</c>)。</summary>
public enum PushPlatform
{
    Apns,
    Fcm,
}

/// <summary>通知权限的结果。</summary>
public enum PushPermission
{
    Granted,
    Denied,

    /// <summary>还没问过(界面显示「开启通知」按钮,点了才弹系统对话框)。</summary>
    NotDetermined,
}

/// <summary>
/// 推送令牌的取得(docs/design/02 A4):APNs / FCM 的注册由宿主做。令牌拿到之后,共享层(<c>PushRegistrationCoordinator</c>)经 <c>push.register</c>
/// 登记到每台已连接的设备;设备只存不发,发送由 nextco.work 的推送中继负责(05 §6 B4)。
/// </summary>
public interface IPushRegistration
{
    /// <summary>这个平台有推送(桌面、模拟器没有)。</summary>
    bool IsSupported { get; }

    PushPlatform Platform { get; }

    Task<PushPermission> GetPermissionAsync(CancellationToken ct = default);

    /// <summary>请求通知权限(首次会弹系统对话框)。</summary>
    Task<PushPermission> RequestPermissionAsync(CancellationToken ct = default);

    /// <summary>取当前的设备令牌;没有权限或平台暂时拿不到返回 null。</summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>平台刷新了令牌(APNs / FCM 都会不定期换):共享层收到后重新登记。事件可能在任何线程触发。</summary>
    event Action<string>? TokenRefreshed;
}

/// <summary>附件的来源。</summary>
public enum AttachmentSource
{
    /// <summary>相册(可多选)。</summary>
    PhotoLibrary,

    /// <summary>拍照。</summary>
    Camera,

    /// <summary>文件选择器。</summary>
    File,
}

/// <summary>用户选中的一个附件。<see cref="ReadAllAsync"/> 按需读出全部字节(单个附件上限见 <c>Attachments.MaxBytes</c>,超过的由共享层在读之前就拒绝)。</summary>
public sealed record PickedAttachment(string Name, string Mime, long Size, Func<CancellationToken, Task<byte[]>> ReadAllAsync);

/// <summary>附件选择(照片、拍照、文件)。平台的选择器由宿主实现;选择结果由共享层的上传托盘(<c>AttachmentTrayViewModel</c>)上传并显示进度。</summary>
public interface IAttachmentPicker
{
    bool IsAvailable(AttachmentSource source);

    /// <summary>弹出选择器;用户取消返回空列表。</summary>
    Task<IReadOnlyList<PickedAttachment>> PickAsync(AttachmentSource source, CancellationToken ct = default);
}

/// <summary>
/// 移动端宿主提供的平台服务。宿主启动时填好,界面从这里取;缺省全是 null(桌面端、测试),各界面自己降级。
/// 另有 <c>IControllerIdentityStore</c>(Keychain / Keystore 实现)在 <c>RemoteDeviceManager</c> 构造时传入,不在这里。
/// </summary>
public sealed class MobileHostServices
{
    public IQrScanner? QrScanner { get; set; }

    public IPushRegistration? Push { get; set; }

    public IAttachmentPicker? AttachmentPicker { get; set; }
}
