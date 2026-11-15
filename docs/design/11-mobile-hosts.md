# 11 · 移动端宿主(iOS / Android)

状态一句话:**Android 宿主已经做出来,并在本机 Android 模拟器上真实跑通了「深链 / 扫码配对 → 收件箱出现审批 → 长按允许 → Run 继续」**(推送、真机、iOS 没做,见 §8)。
iOS 宿主仍然只有设计(§9),本机没有可用的 iOS 验证条件。

共享部分(`MobileShell`、收件箱、设备页、扫码配对状态机、推送 / 附件的抽象与界面)在 `Ncw.UI` / `Ncw.Client`,见 [03 §7](03-ui-layout.md#7-移动端m5-共享层实现说明);
宿主要做的只是**把平台能力接到这些抽象上**。

## 1. 项目与依赖

```
src/
├─ Ncw.Mobile.Shared/     # net10.0:不依赖任何平台 workload 的宿主共用逻辑(在 NextCoWork.slnx 里,有单测)
├─ Ncw.Mobile.Android/    # net10.0-android:Android 宿主(只在 NextCoWork.Mobile.slnx 里)
└─ (Ncw.Mobile.iOS/)      # 还没有
tests/Ncw.Mobile.Shared.Tests/   # 44 个单测,随 `dotnet test NextCoWork.slnx` 跑
tools/Ncw.MobileSmoke/           # 模拟器冒烟的桌面一侧(开发 / CI 工具,默认不跑)
```

> 命名:[01 §2](01-architecture.md) 里原来写的是 `Ncw.Android` / `Ncw.iOS`,改为 `Ncw.Mobile.iOS` / `Ncw.Mobile.Android`。

依赖规则和桌面一样:宿主**只引用 `Ncw.UI`、`Ncw.Client` 和 `Ncw.Mobile.Shared`**,**不能**引用任何 `Ncw.Agent.*`、不能引用 `Ncw.Desktop`;手机不跑 Agent(02 A4)。
`Ncw.Mobile.Shared` 引用 `Ncw.UI`(为了 `INcwBackend`),所以它不是「纯逻辑」库,但不依赖 Android / iOS 的 workload,`dotnet build NextCoWork.slnx` 在任何装了 .NET 10 SDK 的机器上都能覆盖它。

**解决方案拆分**:`NextCoWork.slnx` 不含 Android 宿主(没有 workload 的机器上会构建失败);`NextCoWork.Mobile.slnx` = 共享项目 + `Ncw.Mobile.Shared` + `Ncw.Mobile.Android` + 它的测试,由有 workload 的 CI 任务单独构建。

### `Ncw.Mobile.Shared`(平台无关,有单测)

| 内容 | 作用 | 测试 |
|---|---|---|
| `Identity/SealedIdentityStore`、`ISecretBox` | 控制端身份(TLS 客户端证书 + 私钥)落盘前用平台「封存盒」加密:磁盘上只有密文(魔数 `NCWI` + 版本 + 平台密文,0600);文件坏了 / 包裹密钥没了 → 换新身份(同桌面端);封存失败抛 `PlatformNotSupportedException`,**绝不降级为明文** | `SealedIdentityStoreTests`(同一把密钥重启后读回同一身份、磁盘上没有私钥明文、0600、密钥丢失 / 篡改 / 截断 / 旧明文格式都换新身份、封存失败不落盘、AAD 绑定用途) |
| `Lifecycle/ConnectionPolicy`、`ConnectionSupervisor`、`IConnectionController` | 后台 / 网络切换的连接策略(§6):纯状态机 + 一个把事件串行化的监督器 | `ConnectionPolicyTests`(11)、`ConnectionSupervisorTests`(3,假时间源) |
| `Backend/RemoteOnlyBackend` | `INcwBackend`:没有本机 Agent,只创建 `RemoteDeviceManager`;同时实现 `IConnectionController` | 经 Android 模拟器验证(§7) |
| `Scanning/QrFrameDecoder`、`QrScanText` | 相机帧(YUV 亮度平面)里找二维码:ZXing.Net;单码失败再用多码读取器 | `QrFrameDecoderTests`(生成真实二维码图:带行距填充、旋转 90°、反色、一面二维码墙、无码帧、非法几何) |
| `Scanning/YuvPreviewRenderer` | 相机预览的软件渲染(缩小、转正、YUV→RGB / BGR) | `YuvPreviewRendererTests`(9) |

另在 `Ncw.Client` 追加了 `ViewModels/Mobile/MobileLinks.cs`(`MobileLinkParser` / `MobileLinkHandler`:`nextcowork://` 链接的白名单解析与路由,测试 `Ncw.Client.Tests.Mobile.MobileLinkTests`,26 个)、
`RemoteDeviceManager.SuspendAsync / Resume / ReconnectAsync`(+ `DeviceLink.SuspendAsync`)、`IPushRegistration.IsConfigured` 与 `PushStatus.NotConfigured`。

### `Ncw.Mobile.Android`

| 文件 | 内容 |
|---|---|
| `Ncw.Mobile.Android.csproj` | `net10.0-android`、minSdk 26、`work.nextco.ncw`、64 位(默认只出 `android-arm64`,`-p:NcwAndroidRids="android-arm64;android-x64"` 加 x86_64)、`EmbedAssembliesIntoApk`(调试也嵌入,否则 `adb install` 装出来的包启动即崩)、只保留 en / zh-Hans 卫星资源、去掉 `TKCode.Arc.Terminal` 带来的 glibc 版 `libghostty-vt.so`;有 `google-services.json` 才引用 Firebase(§4) |
| `NcwAndroidApplication` | `AvaloniaAndroidApplication<NcwApp>`:在 Application 创建之前设置 `NcwApp.BackendFactory` / `MobileHostFactory`,`UseNcw()`(和桌面同一个 `NcwAppSetup`) |
| `MainActivity` | `AvaloniaMainActivity`(单视图,根视图是 `AdaptiveShell`):`nextcowork://` 的 intent-filter、`OnNewIntent`、前后台事件、权限 / 选择器结果;`ConfigurationChanges` 全部自己处理(旋转、折叠、深浅色、软键盘、语言、字体缩放),Activity 不重建 |
| `Platform/AndroidHost` | 装配与全局状态(私有目录、通知渠道、后端工厂、监督器) |
| `Platform/AndroidKeystoreSecretBox` | `ISecretBox`:AndroidKeyStore 里的 AES-256-GCM 密钥(不可导出,随机 IV) |
| `Platform/AndroidQrScanner`、`CameraSession`、`QrPreviewImage` | `IQrScanner` + `IQrPreviewProvider`:Camera2 + ZXing.Net + 软件预览 |
| `Platform/AndroidAttachmentPicker` | `IAttachmentPicker`:SAF(Avalonia `IStorageProvider`)+ `ACTION_IMAGE_CAPTURE` + `FileProvider` |
| `Platform/AndroidPushRegistration`、`FcmService`(`#if NCW_FCM`) | `IPushRegistration`:没有 Firebase 配置就报「未配置」,有配置走 FCM |
| `Platform/AndroidUrlLauncher` | `IUrlLauncher`:`ACTION_VIEW`,只放行 http(s)(**还没有调用方**,见 §8) |
| `Platform/NetworkMonitor`、`NotificationChannels`、`ActivityResults`、`CjkWeightFix`、`LayoutDiagnostics` | 默认网络回调;通知渠道 `approvals` / `runs`;Activity 结果 / 权限结果变成 `Task`;中文字重缺陷绕行(§6);开发诊断(写 logcat,tag `NcwLayout`) |
| `Properties/AndroidManifest.xml`、`Resources/` | 权限、`FileProvider`、`allowBackup=false`、主题、字符串(en / zh-CN)、占位图标(原创的两个相扣的圆环,不是品牌素材) |

启动顺序(都在 `Application` 创建之前,`NcwAndroidApplication.CustomizeAppBuilder`):

```csharp
NcwApp.BackendFactory = () => new RemoteOnlyBackend(new SealedIdentityStore(identityFile, new AndroidKeystoreSecretBox(), name), devicesFile, deviceModel);
NcwApp.MobileHostFactory = () => new MobileHostServices { QrScanner = scanner, Push = push, AttachmentPicker = picker };
// 控制端文件目录 NCW_CLIENT_DIR = <应用私有目录>/client(身份密文、devices.json、ui.json)
```

`NcwApp` 在 `ISingleViewApplicationLifetime` 下把根视图设为 `AdaptiveShell`;手机宽度走 `MobileShell`,平板横屏走桌面外壳。链接经 `NcwLinkInbox` → `MobileLinkHandler`(追加在 `NcwApp` 单视图分支里)。

## 2. 本机环境:装了什么、装在哪、占多大、怎么回退

> 这一节是 2026-11-14 在 macOS arm64 开发机上做 Android 宿主时的**实际记录**。所有安装都是用户级、没有 sudo、没有 brew;没有删除或覆盖用户已有的任何东西。

| 项 | 做法 | 位置 | 大小 | 回退 |
|---|---|---|---|---|
| .NET Android workload | 在仓库目录(SDK 10.0.401 / 特性带 10.0.400)里 `dotnet workload install android`(workload set 模式;`Microsoft.Android.Sdk` 35.0.105 + `Microsoft.Android.Ref.36` 36.1.118 + 各 RID 的运行时包 / AOT 交叉编译包) | `~/.dotnet/packs/`、`~/.dotnet/sdk-manifests/`、`~/.dotnet/metadata/workloads/10.0.400/` | `~/.dotnet` 由 16 GB 变成 19 GB(约 +3 GB) | `dotnet workload uninstall android`(在仓库目录里执行,即 10.0.400 带) |
| JDK 17 | `dotnet build -t:InstallAndroidDependencies -f net10.0-android -p:AndroidSdkDirectory=$HOME/Library/Android/sdk -p:JavaSdkDirectory=$HOME/Library/Developer/Xamarin/jdk -p:AcceptAndroidSDKLicenses=True`(Microsoft OpenJDK 17.0.14) | `~/Library/Developer/Xamarin/jdk` | 309 MB | 删这个目录 |
| Android SDK 平台与构建工具 | 同一条命令按 workload 的要求**只新增**:`platforms/android-36`、`build-tools/36.0.0`(已有的 `android-37.0`、`build-tools/37.0.0`、`cmdline-tools`、`emulator`、`platform-tools`、`system-images` 一个字节没动,安装前后逐个核对了 `package.xml` 的 MD5) | `~/Library/Android/sdk/platforms/android-36`、`…/build-tools/36.0.0` | 134 MB + 188 MB | 删这两个目录 |
| 测试用 AVD `ncw_test` | `avdmanager create avd -n ncw_test -k "system-images;android-35;google_apis;arm64-v8a" -d pixel_6`(用已有的系统镜像,镜像目录只读;**没有碰** `arc_video_test`) | `~/.android/avd/ncw_test.avd`、`ncw_test.ini` | 2.9 GB(运行过之后;用户数据分区) | `avdmanager delete avd -n ncw_test`(或删这两个) |
| NuGet 缓存 | `Avalonia.Android`、`ZXing.Net`、AndroidX / Kotlin 绑定等 | `~/.nuget/packages/` | 几十 MB | `dotnet nuget locals all --clear` 会清掉所有项目的缓存(慎用) |

**一次误操作与纠正**:最先在仓库外的临时目录里跑了 `dotnet workload install android`,那里没有 `global.json`,用的是用户机器上最新的 SDK(11.0.100 带),装上了 .NET 11 的 Android 工作负载(37.x)。发现后立即
在同一个目录里 `dotnet workload uninstall android` 撤销(`~/.dotnet` 回到 16 GB),再在仓库目录里按 10.0.400 带重装。用户本来就有的 11.0.100-rc.2 / rc.3 带的 android 工作负载记录没有受影响。

构建命令里要带的两个属性(也可以设环境变量 `ANDROID_HOME` / `JAVA_HOME`,工具自己会找):
`-p:AndroidSdkDirectory=$HOME/Library/Android/sdk -p:JavaSdkDirectory=$HOME/Library/Developer/Xamarin/jdk`。

`Directory.Build.targets` 里的 `NcwPinServicingPacks`(把运行时包钉到 10.0.15)对 Android 目标**不生效**(加了 `Condition="'$(TargetPlatformIdentifier)' != 'android'"`):Android 的运行时包版本由 workload 清单决定(`Microsoft.Android.Runtime.*` 是 36.x),钉成 10.0.15 会还原失败。

## 3. 平台服务:实现与选型理由

接口都在 `Ncw.Client/Services/MobileServices.cs` 和 `DeviceTrust.cs`。

| 接口 | Android 实现 | 选型理由 / 注意 |
|---|---|---|
| `IControllerIdentityStore` | `SealedIdentityStore` + `AndroidKeystoreSecretBox`:AndroidKeyStore 里的 AES-256-GCM 包裹密钥(不可导出、随机 IV;不要求设备解锁,否则后台重连被锁屏卡住);密文放在 `files/client/controller-identity.bin`(0600,目录 0700,`allowBackup=false`) | 私钥本身不进 SharedPreferences、不明文落盘。模拟器上验证:文件头 `NCWI`,里面没有 `MII` / `privateKey` / `certificate` 字样;应用重启 / 重新安装(`install -r`)后能解开并自动重连 |
| `IDeviceBook` | 沿用 `JsonFileDeviceBook`(`files/client/devices.json`,0600,里面没有私钥) | 同桌面语义,权限最小 |
| `IQrScanner` | Camera2(后置,1280×720 附近的 YUV_420_888)+ ZXing.Net。只在用户点「开始扫描」时开相机;读到第一个二维码 / 取消 / Activity 暂停 / 扫码页离开就关 | **选型**:ML Kit Barcode Scanning 在没有 Google Play 服务的设备上不可用(或要自带 ~3 MB 模型),CameraX 的 Xamarin 绑定版本停在 1.0.x、和 AndroidX 其他包版本链纠缠;ZXing.Net 纯托管(Apache-2.0)、Android / iOS 共用、能在单测里用生成的二维码图验证。**代价**:弱光 / 小码 / 抖动下的识别率不如 ML Kit,真机要再评估(§8) |
| `IQrPreviewProvider` | `QrPreviewImage`(Avalonia `Image`):每帧在相机线程把 YUV 缩到 ~480 宽、转正、转 RGBA(`YuvPreviewRenderer`),~15 帧/秒推给 UI | **没有用原生 `TextureView` / `SurfaceView` 放进 `NativeControlHost`**:实测 `TextureView` 附着、可见、有尺寸,但永远拿不到绘制面(`IsAvailable=false`),预览是黑的。软件预览能被圆角裁剪、随页面出现 / 消失。**坑**:Avalonia/Skia 在 Android 上 BGRA 位图显示成纯黑,必须用 `PixelFormat.Rgba8888`;`Image` 必须每帧换新位图(见 §6) |
| `IPushRegistration` | `AndroidPushRegistration`:`IsConfigured` = 构建时有没有 `google-services.json`(定义 `NCW_FCM`)。**没配置**:界面显示「推送服务未配置」,不弹权限框、不取令牌、**不伪造令牌**。**配置了**:Android 13+ 的 `POST_NOTIFICATIONS` 运行时权限 + `FirebaseMessaging.getToken()` / `onNewToken`,收到消息只显示固定标题的本地通知(「需要你确认」,不含命令、路径、对话),点击打开 `nextcowork://inbox?d=&k=&id=` | `google-services.json` 不进仓库(`.gitignore` 已加);**FCM 分支没有在真实 Firebase 项目上验证**,也没有在构建里编译验证过(只有写了代码) |
| `IAttachmentPicker` | 相册 / 文件:Avalonia `IStorageProvider`(Android 上是系统文档选择器 SAF,不要存储权限);拍照:`ACTION_IMAGE_CAPTURE` + `FileProvider` 的一次性授权 URI(应用 `cache/capture/`,启动时清掉超过一天的);字节按需读 | **差异**:设计写的是系统 Photo Picker(`ACTION_PICK_IMAGES`),这里相册也走 SAF + 图片过滤,少一条代码路径。**没有在模拟器上点过**(模拟器没有相册 / 相机应用;只验证了代码编译) |
| `IUrlLauncher` | `AndroidUrlLauncher`(`ACTION_VIEW`,只 http / https) | 账户服务在手机壳里还没有接线(`NcwApp` 单视图分支不创建 `AccountService`,手机壳没有账户页),所以**目前没有调用方** |
| `INcwBackend` | `RemoteOnlyBackend` | 见 §1 |

## 4. 权限、链接与推送配置(实际的 `AndroidManifest.xml`)

| 项 | 内容 |
|---|---|
| 权限 | `INTERNET`、`ACCESS_NETWORK_STATE`、`CAMERA`(`uses-feature camera` 与 `autofocus` 都 `required=false`:没有相机的设备也能装,走「粘贴二维码内容」)、`POST_NOTIFICATIONS`(只在点「开启通知」时才申请)。相册 / 文件不需要存储权限 |
| 链接 | `intent-filter`:`ACTION_VIEW` + `DEFAULT` + `BROWSABLE` + scheme `nextcowork`(Activity `LaunchMode=SingleTask`,已运行时走 `OnNewIntent`)。App Links(`assetlinks.json`)要后端 B11,没做 |
| 备份 | `allowBackup=false`:身份密文绑定在 Keystore 密钥上,备份还原到别的设备没有意义 |
| 通知 | 渠道 `approvals`(高优先级,锁屏私密)、`runs`(默认);名称来自 `strings.xml`(随系统语言) |
| FileProvider | `${applicationId}.captures`,只暴露 `cache/capture/` |
| 推送构建开关 | 在 `src/Ncw.Mobile.Android/` 放 `google-services.json` → 自动定义 `NCW_FCM`、引用 `Xamarin.Firebase.Messaging`(版本在 `Directory.Packages.props`)和 `GoogleServicesJson`;没有这个文件就完全不拉 Firebase |

**链接路由**(`MobileLinkParser` / `MobileLinkHandler`,严格白名单,总长 ≤ 4096、不含空白 / 控制字符):

| 链接 | 处理 |
|---|---|
| `nextcowork://pair?v=1&d=…&f=…&a=…&t=…&e=…`(配对二维码本身,用系统相机 / 别的应用扫到后跳进来) | 推「设备」标签的扫码页并 `QrPairing.Submit`:解析 → 检查过期 → **等用户点「配对」**;链接本身不会触发配对。已在扫码页时复用,不叠页 |
| `nextcowork://inbox?d=<deviceId>&k=<kind>&id=<opaqueId>`(通知点击) | 切到「收件箱」标签;参数只允许 token 字符、不做百分号解码、不允许未知 / 重复参数 |
| `nextcowork://auth/callback?code=&state=` | 登录回调:交给 `LinkRouter`(state 校验);**手机壳没有账户服务时返回 `AccountNotWired`、什么也不改**(如实标注:账户流程在手机壳里还没有接线) |
| 其他(`pair?invite=`、`open`、未知主机、畸形) | 忽略 |

## 5. 构建与运行

```bash
# 构建(零警告;第一次约 3–4 分钟,APK 约 50 MB)
dotnet build NextCoWork.Mobile.slnx -p:AndroidSdkDirectory=$HOME/Library/Android/sdk -p:JavaSdkDirectory=$HOME/Library/Developer/Xamarin/jdk
# 开发循环(Fast Deployment:增量约 10–40 秒;不能用于 adb install 出来的 APK)
dotnet build src/Ncw.Mobile.Android -t:Install -p:EmbedAssembliesIntoApk=false -p:AndroidSdkDirectory=… -p:JavaSdkDirectory=…
# 模拟器(headless;用已有的测试 AVD ncw_test,不要用别的项目的 AVD)
emulator -avd ncw_test -no-snapshot -no-audio -no-window -no-boot-anim -gpu host
# 用完清理
adb emu kill
```

`global.json` 钉的是 SDK 10.0.401,workload 必须装在这一个特性带(10.0.400)上(§2 的误操作就是在仓库外用了别的 SDK)。
`dotnet build NextCoWork.slnx` 不依赖 Android workload,零警告。

## 6. 设计里的几个决定和在模拟器上发现的问题

**后台 / 前台与网络切换(规则 7)** —— `ConnectionPolicy`(纯状态机)+ `ConnectionSupervisor`,Android 的 `MainActivity.OnStart / OnStop` 与 `ConnectivityManager.registerDefaultNetworkCallback` 喂事件:

| 事件 | 动作 |
|---|---|
| 进入后台 | 宽限期 60 秒内保持连接(权限弹窗、系统选择器、切去看一眼都不该断);超过 → `Suspend`:断开所有设备,设备标「状态未知」(`DeviceLink.SuspendAsync` → `Offline`,会话里运行中的 Run 标 unknown,不判结束) |
| 回到前台 | 已挂起 → `Resume`(重新读已配对列表并连接);没挂起但离开超过 20 秒或后台期间换过网络 → `Reconnect`(丢弃现有连接重连,不等心跳超时) |
| 默认网络丢了 | 立刻 `Suspend`(界面马上说「状态未知」,不干等 TCP 超时) |
| 默认网络(新 id)可用 | 前台:挂起中 → `Resume`;换了网络 → `Reconnect`(1.5 秒去抖);第一次回调只当基线。旧网络的 `onLost` 比新网络的 `onAvailable` 晚到,忽略 |

模拟器上实测:进后台 60 秒后日志出现 `Suspend`,回前台出现 `Resume`;关 Wi-Fi 时模拟器回退到蜂窝网,日志 `Suspend → Resume`,开回 Wi-Fi 出现 `Reconnect`,设备页一直恢复为「已连接」。
应用层心跳(另一条线)落地后,真机上半死连接的发现会更快;现在的策略不依赖它。

**在模拟器上发现并处理的问题**(共享层的在 `Ncw.UI` / `Ncw.Client` 里修,宿主的在宿主里):

1. **安全区被算了两次且单位不对**(`NcwPage`):`Avalonia` 默认还会给根视图加安全区 padding(`TopLevel.AutoSafeAreaPadding`),`AdaptiveShell` 现在关掉它并启用 `DisplayEdgeToEdgePreference`(内容画到状态栏 / 手势条后面);
   `TKCode.Arc.Mobile 1.1.0` 的 `Page.UsePlatformSafeArea` 用的是 `SafeAreaChanged` 事件参数——那个值是**物理像素**(缩放 2.625 的手机上状态栏 48.8 变成 128),页面顶部多出一大块空白;而 `IInsetsManager.SafeAreaPadding` 属性是 dip,且启动早期渲染缩放还是 1 时读到的是像素值、缩放更新后没有 `SafeAreaChanged`。
   `NcwPage` 现在关掉 Arc 的平台安全区,自己读属性(dip)并在 `SafeAreaChanged` / `ScalingChanged` 时重读,只有最外层页面留安全区。Arc 修复后可以简化。
2. **中文常规字重显示成方框**(`CjkWeightFix`):字重**恰好是 400** 时,系统 CJK 字体(`NotoSansCJK-Regular.ttc`)匹配到的字形表是空的,100 / 300 / 399 / 401 / 500+ 都正常,拉丁字母不受影响。字体管理器接口(`IFontManagerImpl`)不允许外部实现,不能在那一层改;
   绕行:顶层样式把继承属性 `TextElement.FontWeight` 的默认值设成 401。**限制**:控件模板里显式写 `Normal` 的文字不受影响(没有逐个核查;设置页的输入框要在真机上看)。Avalonia / SkiaSharp 修复后删除。`LayoutDiagnostics.FontTest()`(`adb shell am start … --ez fonttest true`)能复现。
3. **原生 `TextureView` 预览取不到绘制面**、**BGRA 位图显示成黑**、**`Image` 复用同一个 `WriteableBitmap` 不刷新**:见 §3 的 `IQrPreviewProvider`。
4. **ZXing 单码读取器在画面里有很多个二维码时整帧失败**(把不同码的定位图案混在一起):`QrFrameDecoder` 现在失败后再用多码读取器(单测「一面二维码墙」复现并覆盖)。
5. **`Image.Close()`**:Camera2 的 `Image` 必须显式 `Close()`,Java 对象的 `Dispose()` 不归还缓冲,`ImageReader` 只有 2 个槽,第三帧抛 `IllegalStateException`,在相机线程上的未处理异常带走整个进程。已修,帧回调整体 `try/catch`。
6. **Fast Deployment 的 APK 不能 `adb install`**(启动即崩 `No assemblies found in .__override__`):csproj 默认嵌入程序集。
7. **点击已选中的「设备」标签不会回到根页**(设计 03 §3 写的是回到根页):Arc `Tabbar` 在索引没变时不触发事件。**未修**(只在这里记录)。
8. `有效期至 10:30 PM`:扫码确认页的时间用了英文区域的 12 小时制,界面语言是中文。**未修**。
9. **横屏走桌面外壳时状态栏重叠、出现 Windows 样式的窗口按钮**:`AdaptiveShell` 现在给桌面外壳设安全区 `Padding`,`WindowChrome.ForCurrentPlatform()` 在 Android / iOS 上不画窗口按钮(截图 23)。

## 7. 验收与验证结果(M5,05 §1:「收到审批推送 → 打开 → 长按允许 → Run 继续」)

后端的推送中继(B4)不存在,所以按下表把链路拆开验(**除「推送」一段之外都在模拟器上走通了**):

| 段 | 做法 | 结果 |
|---|---|---|
| 令牌登记 | 点「设置 → 通知 → 开启通知」 | **未验证**:没有 Firebase 配置;界面显示「推送服务未配置」,不弹权限框、不伪造令牌(`PushViewModelTests` 覆盖状态机)|
| 推送 → 打开 | FCM 测试工具发 `{ deviceId, kind, opaqueId }` | **未验证**:没有 Firebase 项目与后端中继。链路的后半段(`nextcowork://inbox?…` 深链 → 收件箱标签)有单测;`adb shell am start -a android.intent.action.VIEW -d nextcowork://inbox?d=…` 的真机 / 模拟器点击没有单独录证据 |
| 配对 | `adb shell am start -a VIEW -d '<二维码内容>'`(冷启动 / 已运行都试了)→ 手机「找到设备」页 → 点「配对」;以及虚拟相机扫码(下面) | **通过**(截图 06 / 07) |
| 打开 → 长按允许 | 桌面 Agent(真实 `ncw-agent` 进程,隔离的临时数据根)用本机假上游触发需要审批的 `Bash`;手机收件箱出现卡片(设备 · 环境 · 高风险)、点开定位到卡片、长按「允许」(危险命令 1.5 秒,`adb shell input swipe x y x y 2600`) | **通过**(截图 09–11) |
| Run 继续 | 假上游收到工具结果后的第二次请求,回复「Done: the marker file was written.」;桌面工作目录里出现标记文件 | **通过**(截图 12:会话页显示工具调用 ✓ 和结尾文字) |

**模拟器上的具体结果**(2026-11-14,macOS arm64,模拟器 `ncw_test`:Pixel 6 配置、Android 15 / API 35 google_apis、1080×2400、420dpi;应用为 Debug 构建,APK 嵌入程序集,约 53 MB):

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build src/Ncw.Mobile.Android` 零警告零错误(首次全量约 3 分 30 秒;增量 10–40 秒);装机(`adb install -r`)约 3–10 秒 |
| 启动 | 冷启动到首屏约 4–5 秒(含 Avalonia / Arc 初始化) |
| 局域网放行 | 模拟器经 NAT 访问宿主 `10.0.2.2:<端口>`,Agent 看到的来源是回环 / 私有地址,`LanOnly=true`(默认「仅局域网地址」)**没有拦**,没有放宽任何安全设置。二维码里 Agent 写的是宿主的局域网 IP(`192.168.x.x`),冒烟工具把地址改写成 `10.0.2.2:端口` 再喂给手机(指纹钉扎和一次性密钥不变,安全性相同) |
| 配对 | 深链 → 「找到设备」(设备 ID 前 8 位 `NNG2 9XCQ`、地址、有效期)→ 点「配对」→「配对成功」→ 设备页「tokendeMacBook-Air · 电脑 · 已连接 · 1 个项目」 |
| 持久化 | 杀进程重启后身份从 Keystore 解封,设备列表还在,自动重连(截图 13) |
| 虚拟相机扫码 | 见下 |
| 后台 / 网络 | 见 §6 |
| 权限 | 点「开始扫描」弹系统相机权限框(截图 18),同意后开相机 |

**虚拟相机扫码**:模拟器无窗口(headless)时 `virtualscene` 相机只给黑帧;改用 `emulator … -camera-back imagefile:<png>`(命令行参数,不改 AVD 配置)把一张图当相机画面。这个相机会把图放大约 1.6 倍并只露出一块,单个居中的二维码总被裁掉,
所以冒烟工具把二维码在 1920×1080 的画布上**平铺**(`QrPng.WriteWide`),这才暴露了第 6 节第 4 条(多码)。结果:点「开始扫描」约 2 秒内解出 `nextcowork://pair?…` 并进入「找到设备」(截图 19 / 21;状态栏的绿色相机图标是系统的相机使用指示)。
预览:`19b` 显示软件预览正在画相机帧(用校准图,能看到十字线)。**相机扫码之后的「点配对」没有再走一遍**(配对本身已经由深链路径验证,两条路径在 `QrPairingViewModel.Submit` 之后完全相同)。

**可选的脚本化冒烟**(默认不跑;`tools/Ncw.MobileSmoke`):

```bash
# 前提:模拟器已启动(adb devices 有 emulator-5554),Agent 已 dotnet build(src/Ncw.Agent),手机 APK 已 dotnet build 到 bin/Debug
NCW_ANDROID_SMOKE=1 dotnet run --project tools/Ncw.MobileSmoke -- smoke     # 完整流程,每步 PASS|FAIL + 耗时,截图在 artifacts/mobile-android/smoke-*.png
NCW_ANDROID_SMOKE=1 dotnet run --project tools/Ncw.MobileSmoke -- serve     # 交互式:拉起隔离 Agent + 假上游,打印二维码,stdin 命令 qr | qrpng <path> | run | state | quit
```

没设 `NCW_ANDROID_SMOKE=1` 时只打印「skipped」。点击坐标按屏幕比例换算(按 Pixel 6 版式量的),换屏幕 / 版式要重新量。步骤:隔离 Agent + 假上游启动 → `adb install -r` → `pm clear` 后**冷启动带 `nextcowork://pair` 深链** → 点「配对」→ 桌面看到新控制端 →
桌面发起一轮会触发审批的 Run → 手机收件箱(截图)→ 点开卡片 → 长按允许(`input swipe` 2.6 秒)→ 桌面看到:没有待审批、标记文件已写、假上游收到第二次请求。

**冒烟运行记录**(2026-11-14,macOS arm64,模拟器 `ncw_test`,最终代码、Debug 构建、嵌入程序集的 APK):

| 步骤 | 结果 | 耗时 |
|---|---|---|
| agent + remote + fake upstream start | PASS | 1.3 s |
| install apk | PASS | 2.6 s |
| cold start with `nextcowork://pair` deep link | PASS | 14.3 s(含 14 秒固定等待) |
| tap Pair → desktop sees the new controller | PASS | 0.8 s |
| start a run that needs approval → pending interaction on the desktop | PASS | 0.5 s |
| phone inbox shows the approval card | PASS(只能截图,收件箱内容没有可编程的读取通道) | 3.4 s |
| open the card, long-press Allow | PASS | 5.9 s |
| run continues: nothing pending, marker file written, upstream saw the tool result | PASS | 1.2 s |
| **合计** | **PASS(0 个失败步骤)** | **31 s**(`time`),命令 `NCW_ANDROID_SMOKE=1 dotnet run --no-build --project tools/Ncw.MobileSmoke -- smoke` |

截图都在 `artifacts/mobile-android/`(被忽略的目录,不入库):开发过程的 `00`–`24`(探针、首屏、安全区修复前后、字体实验、深链配对、审批卡片、长按进度、重启重连、后台 / 网络、相机权限 / 预览 / 解码、软键盘、横屏、窄屏),脚本化冒烟的 `smoke-01`–`smoke-05`。
没有推送这一段(见 §8)。

## 8. 没验证的 / 还没做的

- **推送**:没有 Firebase 项目配置(`google-services.json` 不进仓库)和后端推送中继(B4),**未验证**。`#if NCW_FCM` 分支只写了代码,没有编译过(没有 `google-services.json` 就不编译),没有在真实 FCM 上跑过。
- **真机**:Arc.Mobile 的手势(左缘右滑、Sheet、PullToRefresh)、手势导航条、折叠屏、动态字体、中文输入法——没有真机,模拟器上只点过标签、导航栈推入 / 返回、长按。
  模拟器上看过的版式(截图 22–24):**软键盘**能弹出、`input text` 能输入、页面按键盘高度缩小(`Page.AvoidKeyboard`);但那一次输入框(已断开设备的会话页)被排在页面顶部而不是贴着键盘上沿,原因没查清(可能与断开后转录被清空有关),**未确认是缺陷**;
  **横屏**(914dp 宽 ≥ 720)走桌面外壳,已修状态栏重叠与窗口按钮(§6);**窄屏**(360dp)可用,输入框工具行里的权限图标掉到第二行;**深浅色**:应用默认深色并固定(`ArcAppearance.Dark`),没有跟随系统的开关,所以没测浅色。
- **相机**:真机的弱光 / 对焦 / 抖动 / 不同传感器方向与折叠屏;ML Kit 作对照没做;Camera2 的 `LEGACY` 级别设备没测。
- **附件选择器**:相册 / 文件 / 拍照在模拟器上没点过。
- **账户 / 登录**:手机壳还没有账户页,`auth/callback` 链接如实返回 `AccountNotWired`;`AndroidUrlLauncher` 没有调用方。
- **平板宽屏的桌面外壳**:终端页签不可用(`libghostty-vt` 没有 Android 构建,从 APK 里去掉了),其他桌面页签未在 Android 上验证。
- **Release / 签名**:Release(裁剪)构建在模拟器上跑通冒烟(§10),但没有发布签名、AAB、Play 上架;没评估 NativeAOT。
- 第 6 节第 7、8 条两个小问题;`IPushRegistration` 的 FCM 令牌刷新事件没有测。
- 云端 Agent 的「创建」引导和唤醒状态(M6);首次启动引导(07)在手机壳里的版式。

## 9. iOS 宿主(仍是设计,没有创建项目)

本机没有 iOS workload 的验证条件(模拟器没有相机 / 推送),设计保持原样:

| 接口 | iOS 实现 |
|---|---|
| `IControllerIdentityStore` | Keychain:私钥(PKCS#8)存 `kSecClassGenericPassword`,`kSecAttrAccessibleWhenUnlockedThisDeviceOnly`,不进 iCloud 同步;可选 `LAContext`。可以直接复用 `Ncw.Mobile.Shared` 的 `SealedIdentityStore`(封存盒换成 Keychain 里的包裹密钥)或像桌面一样直接存钥匙串 |
| `IQrScanner` / `IQrPreviewProvider` | AVFoundation 取帧 + Vision `VNDetectBarcodesRequest`(或复用 `QrFrameDecoder` + `YuvPreviewRenderer`);`NSCameraUsageDescription` |
| `IPushRegistration` | `UNUserNotificationCenter` + `RegisterForRemoteNotifications`;`Platform = Apns`;未配置证书 / 能力时同 Android 报「未配置」 |
| `IAttachmentPicker` | `PHPickerViewController` / `UIImagePickerController` / `UIDocumentPickerViewController` |
| 链接 | URL Scheme `nextcowork`;Universal Links 要后端 B11 |
| 权限 | `NSCameraUsageDescription`、`NSLocalNetworkUsageDescription`(连 `IP:端口` 会触发本地网络授权,说明文字写「连接你电脑上的 NextCoWork」) |
| 构建 | `dotnet workload install ios`;iOS 用 NativeAOT(`PublishAot`),**必须先验证 BouncyCastle TLS 1.3 / SRP 在 iOS NativeAOT 下零警告**(M2 的 spike 只在桌面做过) |

`Ncw.Mobile.Shared` 就是为了 iOS 复用设计的(后台 / 网络策略、`RemoteOnlyBackend`、二维码解码与预览渲染);iOS 的系统事件(`WillResignActive` / `DidBecomeActive`、`NWPathMonitor`)喂给同一个 `ConnectionSupervisor`。

## 10. Release 构建与裁剪

```bash
dotnet publish src/Ncw.Mobile.Android -c Release -f net10.0-android -o <输出目录> -p:AndroidSdkDirectory=… -p:JavaSdkDirectory=…   # 约 2 分 10 秒
```

- Release 默认开启程序集裁剪(ILLink)+ R8,APK 约 **27 MB**(Debug 嵌入程序集约 53 MB)。用的是调试证书签名,**不是发布签名**(Play 上传密钥 / 应用签名没有做)。
- **裁剪警告**:用 `-p:TrimmerSingleWarn=false` 看原始警告,共 7 条(IL2026 ×4、IL2072 ×2、IL2075 ×1),**全部来自第三方的 `Avalonia.DesignerSupport`**(Avalonia 包的传递依赖,设计器支持,Android 上用不到);
  `Ncw.*` 和 `ZXing.Net` 一条都没有。默认的汇总警告是一条 IL2104,仓库的 `TreatWarningsAsErrors` 会把它升成错误,所以 csproj 里只放行 IL2104 这一条(`WarningsNotAsErrors`),`Ncw.*` 自己的裁剪警告仍然是错误。
  这是**无法在本仓库消除的第三方警告**,如实列出;Avalonia 去掉这个传递依赖或加上裁剪注解后可以删掉放行。
- **运行验证**:裁剪后的 Release APK 在同一个模拟器上跑完整的脚本化冒烟(深链配对 → 审批 → 长按允许 → Run 继续),全部 PASS(28.7 秒),说明 ILLink 没有裁掉运行时需要的东西(Avalonia 编译绑定、Arc、源生成 JSON、ZXing、Camera2 绑定)。
- 没做:NativeAOT(Android 的 .NET NativeAOT 工作负载在 workload 里有包,但没评估)、`android-x64` / 32 位 ABI、AAB 与 Play 签名、R8 规则(默认规则够用,没有单独配置)。
