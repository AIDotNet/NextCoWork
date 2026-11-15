# 11 · 移动端宿主(iOS / Android)

本篇只写**设计与脚手架说明**。本机没有 iOS / Android 的 .NET workload,宿主项目现在**不创建**——创建了也无法在这里构建,
会破坏 `dotnet build NextCoWork.slnx`。共享部分(`MobileShell`、收件箱、设备页、扫码配对状态机、推送 / 附件的抽象与界面)已经在 `Ncw.UI` / `Ncw.Client` 里做完,
在 Avalonia.Headless 下验证(见 [03 §7](03-ui-layout.md#7-移动端m5-共享层实现说明));宿主项目要做的只是**把平台能力接到这些抽象上**。

## 1. 项目与依赖

```
src/
├─ Ncw.Mobile.iOS/        # net10.0-ios,iOS 和 iPadOS(同一个 App)
└─ Ncw.Mobile.Android/    # net10.0-android
```

> 命名:[01 §2](01-architecture.md) 里原来写的是 `Ncw.Android` / `Ncw.iOS`,改为 `Ncw.Mobile.iOS` / `Ncw.Mobile.Android`(和 `Ncw.Desktop` 并列,名字一眼看出是移动宿主)。

依赖规则和桌面一样:**只引用 `Ncw.UI` 和 `Ncw.Client`**(它们带上 `Ncw.Core` / `Ncw.Protocol`),**不能**引用任何 `Ncw.Agent.*`、不能引用 `Ncw.Desktop`。
手机不跑 Agent(02 A4),包里也就不会有 SQLite 原生库、PTY、Jaunt。

**不放进 `NextCoWork.slnx`**:新建 `NextCoWork.Mobile.slnx`,只含这两个宿主项目(和它们的测试),由有 workload 的 CI 任务单独构建。
主解决方案保持「任何一台装了 .NET 10 SDK 的机器 `dotnet build NextCoWork.slnx` 都能过」。

每个宿主项目的结构(两个平台对称):

| 文件 | 内容 |
|---|---|
| `Ncw.Mobile.<平台>.csproj` | 目标框架、平台元数据、AOT / 裁剪设置(见 §4);包版本只写在 `Directory.Packages.props` |
| 入口(`AppDelegate` / `MainActivity`) | 用 Avalonia 的平台生命周期类启动 `NcwApp`(`UseNcw()` 注册 Arc、字体、语言,和桌面同一个 `NcwAppSetup`) |
| `Platform/…IdentityStore.cs` | `IControllerIdentityStore` 的 Keychain / Keystore 实现 |
| `Platform/…Push.cs` | `IPushRegistration`(APNs / FCM) |
| `Platform/…QrScanner.cs` | `IQrScanner` + `IQrPreviewProvider`(相机预览与解码) |
| `Platform/…AttachmentPicker.cs` | `IAttachmentPicker`(相册、拍照、文件) |
| `Platform/RemoteOnlyBackend.cs` | `INcwBackend`:手机没有本机 Agent,只创建 `RemoteDeviceManager`(`Start` 里读出已配对设备并连接) |
| `Info.plist` / `AndroidManifest.xml` | 权限说明、`nextcowork://` 链接、推送能力(§3) |

启动顺序(都在 `Application` 创建之前):

```csharp
NcwApp.BackendFactory = () => new RemoteOnlyBackend(new KeychainIdentityStore(), devicesFilePath);
NcwApp.MobileHostFactory = () => new MobileHostServices
{
    QrScanner = new VisionQrScanner(),          // 同时实现 IQrPreviewProvider
    Push = new ApnsPushRegistration(),
    AttachmentPicker = new PhotoAttachmentPicker(),
};
```

`NcwApp` 在 `ISingleViewApplicationLifetime` 下把根视图设为 `AdaptiveShell`(按宽度切换:手机宽度 `MobileShell`,iPad 横屏 / 大平板 `DesktopShell`),所以 iPad 不需要单独的界面。

## 2. 要实现的接口

接口都在 `Ncw.Client/Services/MobileServices.cs`(Client 不依赖 Avalonia 和平台包),`IControllerIdentityStore` 在 `Ncw.Client/Services/DeviceTrust.cs`。

| 接口 | 作用 | iOS | Android |
|---|---|---|---|
| `IControllerIdentityStore` | 控制端的密钥对(远程连接的 TLS 客户端证书,09 §4)。`LoadOrCreate()` 取或生成并保存;**平台不能安全保存时抛 `PlatformNotSupportedException`,绝不降级为明文** | Keychain:私钥(PKCS#8)存 `kSecClassGenericPassword`,`kSecAttrAccessibleWhenUnlockedThisDeviceOnly`,不进 iCloud 同步;可选 `LAContext`(Face ID)解锁 | Keystore 里一把 AES-GCM 包裹密钥(`setUserAuthenticationRequired` 可选),私钥密文存应用私有目录;不要把私钥本身放进 SharedPreferences |
| `IQrScanner` | `RequestAccessAsync`(相机权限)→ `ScanAsync`(读到一个二维码返回其文本,取消返回 null)。解析(`PairingQr.TryParse`)和配对(`DeviceActions.PairWithQrAsync`)在共享层,**宿主只负责相机和解码** | AVFoundation 取帧 + Vision `VNDetectBarcodesRequest`(QR);`NSCameraUsageDescription` | CameraX + ML Kit Barcode Scanning(只要 QR 格式);`CAMERA` 运行时权限 |
| `IQrPreviewProvider` | 可选:返回相机预览的 Avalonia 控件(`NativeControlHost` 承载 `AVCaptureVideoPreviewLayer` / `PreviewView`)。没实现时扫码页画一个占位框,仍然可以用「粘贴二维码内容」 | 同左 | 同左 |
| `IPushRegistration` | `GetPermissionAsync` / `RequestPermissionAsync` / `GetTokenAsync` / `TokenRefreshed`。令牌拿到后由共享层的 `PushViewModel` 经 `push.register` 登记到每台已连接的设备(设备只存不发) | `UNUserNotificationCenter` 请求授权 + `UIApplication.RegisterForRemoteNotifications`;令牌从 `DidRegisterForRemoteNotifications` 来(转十六进制)。`Platform = Apns` | Android 13+ 的 `POST_NOTIFICATIONS` 运行时权限 + `FirebaseMessaging.getToken()` / `onNewToken`。`Platform = Fcm` |
| `IAttachmentPicker` | `IsAvailable(source)` / `PickAsync(source)`;来源:相册(多选)、拍照、文件。返回 `PickedAttachment(名称, MIME, 大小, ReadAllAsync)`,**字节按需读**(上传托盘在读之前先判 32 MiB 上限) | `PHPickerViewController`(不需要相册权限)/ `UIImagePickerController`(相机)/ `UIDocumentPickerViewController` | Photo Picker(`ACTION_PICK_IMAGES`)/ `ACTION_IMAGE_CAPTURE`(用 `FileProvider` 的临时 URI)/ SAF `ACTION_OPEN_DOCUMENT`;用 `ContentResolver` 流式读,不要求存储权限 |
| `INcwBackend` | 手机版:没有本机 Agent。`Start` 里创建 `RemoteDeviceManager`(`controllerName` 用设备型号)并 `devices.Remote = manager; manager.Start()` | — | — |
| `IUrlLauncher` / `IDialogService` | 打开系统浏览器(OAuth)、系统文件选择;能用 Avalonia 的 `ILauncher` / `IStorageProvider` 就不另写 | — | — |

要点:

- **平台差异只在这几个接口里**。共享层不写 `OperatingSystem.IsIOS()` 判断;「手机还是桌面」由宽度决定,「这个平台有没有相机 / 推送」由对应服务是否为 null / `IsSupported` 决定。
- 设备令牌、身份私钥、配对记录不进日志、不进崩溃报告。
- 宿主必须在**后台**把已有的 `DeviceLink` 重连(系统挂起 / 恢复时):App 回到前台时调用 `RemoteDeviceManager` 的重连(现在连接层自己按退避重连,宿主不需要额外代码;如果真机验证发现挂起后 TCP 不会自己断,再加前台恢复的钩子)。

## 3. 权限、链接与推送配置

| 项 | iOS(`Info.plist` / `Entitlements.plist`) | Android(`AndroidManifest.xml`) |
|---|---|---|
| 相机 | `NSCameraUsageDescription`(「扫描电脑上显示的配对二维码」) | `<uses-permission android:name="android.permission.CAMERA" />` + `<uses-feature android:name="android.hardware.camera" android:required="false" />`(没有相机的设备也能装,走粘贴) |
| 相册 / 文件 | PHPicker 不需要权限;拍照要相机权限;`NSPhotoLibraryAddUsageDescription` 只在以后要保存图片时加 | Photo Picker / SAF 不需要存储权限 |
| 本地网络 | `NSLocalNetworkUsageDescription` + `NSBonjourServices`(以后局域网发现);连 `IP:端口` 也会触发本地网络授权弹窗,说明文字写清楚「连接你电脑上的 NextCoWork」 | `ACCESS_NETWORK_STATE`、`INTERNET`;Android 12+ 的 `NEARBY_WIFI_DEVICES` 只在做 mDNS 发现时才要 |
| 推送 | Capability「Push Notifications」(`aps-environment`);`UIBackgroundModes` 含 `remote-notification` | `POST_NOTIFICATIONS`(13+);Firebase 配置文件;通知渠道 `approvals`(高优先级)、`runs`(默认) |
| 链接 | URL Scheme `nextcowork`;Universal Links(`applinks:nextco.work`,需要后端 B11 的 `apple-app-site-association`) | `intent-filter`:scheme `nextcowork` + App Links(`assetlinks.json`,B11) |
| 密钥 | Keychain Sharing(只在自己的 App 内用,不需要组) | Keystore 无需声明 |

**推送内容**只有 `{ deviceId, kind, opaqueId }`(02 A4、09 §10):App 被唤起后用协议拉详情。打开通知的链接形如 `nextcowork://inbox?d=<deviceId>&k=<kind>&id=<opaqueId>`,
由 `LinkRouter`(07 §登录与链接)路由到 `Navigation.SelectTab(Inbox)`;收件箱已经按设备、按待办排好,用户点条目进入会话并定位到卡片。链接路由的这一条还没有接(随宿主一起做,见 §6)。
通知渠道与分类:审批 / 提问 / 计划 = 需要处理(高优先级,锁屏可见标题「需要你确认」,不含命令或路径);运行完成 / 失败 = 普通。

## 4. 构建与发布

需要的环境(只在 CI 的移动任务和开发者的 Mac / Windows 上,**不在默认构建里**):

| 项 | 要求 |
|---|---|
| .NET workload | `dotnet workload install ios android`(.NET 10);Avalonia 12.1.5 的 `Avalonia.iOS` / `Avalonia.Android` 包,版本与桌面锁定一致 |
| iOS | macOS + Xcode(与 .NET 10 iOS workload 绑定的版本)、Apple Developer 账户(签名、推送、Associated Domains)、真机或模拟器(**扫码和推送只能真机验证**) |
| Android | JDK 17 / 21、Android SDK(API 36 平台 + 构建工具)、`google-services.json`;推送需要真机或带 Google Play 的模拟器 |
| 签名 | iOS:发布证书 + 描述文件(Q3 已有开发者账号);Android:上传密钥 / Play 应用签名。`tools/Ncw.Build` 以后加 `mobile` 子命令(只给开发和 CI 用) |
| 发布 | iOS:NativeAOT(`PublishAot`,设备构建默认)、零裁剪警告;Android:`PublishTrimmed` + R8,AOT 可选(启动速度 vs 包体积,真机上量过再定) |

必须的兼容性检查(M5 的第一步,在真机上做):

1. **BouncyCastle TLS 1.3 / SRP 在 iOS NativeAOT 下零警告并连得上**(M2 的 spike 只在桌面做过)。
2. **`TKCode.Arc.Mobile` 在真机上的手势和动画**(NavigationStack 的左缘右滑、Sheet、PullToRefresh、Tabbar)。文档注明「尚未在真机上验证」(05 §3 风险),共享层只在 Headless 里验证了版式和状态;真机手感不行的页,换成 Arc 核心组件加自己写的导航(业务视图只引用 `Ncw.UI.Controls` 的封装,替换范围就是 `Controls/MobileControls.cs` 和 `Views/Mobile/MobileShell`)。
3. **软键盘与安全区**:`Page.AvoidKeyboard`、`UsePlatformSafeArea` 在 iOS 刘海 / 灵动岛、Android 手势导航条上的表现。
4. **系统字体回退**(中文)、深色 / 浅色跟随系统、动态字体大小。

## 5. 验收(M5,05 §1)

真机上走通:**收到审批推送 → 打开 → 长按允许 → Run 继续**。

后端的推送中继(B4)是这个验收的前置依赖。中继上线之前,用下面的办法把链路拆开验:

| 段 | 验证办法 |
|---|---|
| 令牌登记 | 真机上点「设置 → 通知 → 开启通知」,桌面端 Agent 的 `v3.push.registrations`(kv)里出现这台手机的令牌(`PushRegistrations`,按控制端指纹) |
| 推送 → 打开 | 用 APNs / FCM 的测试工具直接给令牌发一条带 `{ deviceId, kind, opaqueId }` 的消息 |
| 打开 → 长按允许 | 在桌面让 Agent 触发一个需要审批的命令,手机上的收件箱出现待办(经协议,不依赖推送),点进去长按允许 |
| Run 继续 | 桌面上看到命令执行、Run 继续 |

## 6. 现在还没有的(宿主项目要补的)

- 两个宿主项目本身,以及上面五个平台服务的实现。
- `nextcowork://inbox?…` 链接的路由、通知点击后的定位。
- 从后台恢复时对「设备还连着吗」的立即复核(`DeviceLink` 目前靠自己的重连,见 §2 末尾)。
- 云端 Agent 的「创建」引导和唤醒状态(M6):设备页目前只显示云端设备的连接状态,没有创建入口。
- 首次启动引导(07)在手机壳里的版式:引导页和账户页是桌面壳的视图,`MobileShell` 还没有承载它们(设备页提供了「扫码 / 输入地址」两个添加入口,可以先用)。
