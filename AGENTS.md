# AGENTS.md(3.0 / Avalonia 分支)

本文件是在本分支工作的 Agent(以及人)的**硬性约束**。标注「必须」的条目是合并的前提条件。

## 0. 当前阶段

M0(设计)、M1(骨架与界面)已完成;M2(协议与 Agent 骨架)基本完成:协议(含黄金向量)、本机与局域网承载、配对、C# Agent、2.x 数据接管、控制端连真实 Agent 都有测试
(但真实 2.x 数据根与 safeStorage 凭据只在 macOS 上验证过)。M3 / M4 的 Agent 侧(模型层、回合循环、权限、执行环境、工具、终端、子代理、压缩、计划、待办、目标、Skills、Hooks、MCP、插件宿主、定时任务、开放网关、模型目录)
和桌面界面主体已完成,M4 的 sidecar 打包守护与 `tools/Ncw.Build` 在 macOS 上跑通;M5 完成共享层和 Android 宿主(本机 Android 模拟器上走通「配对 → 审批 → 长按允许 → Run 继续」,推送 / 真机 / iOS 没做);M6 只完成中继(`Ncw.Relay` 与中继承载)。
**尚未完成**:云端 Agent 模式(`--mode cloud`)、2.x 回放对照测试、iOS 宿主、Android 真机验证、推送、真实后端(账户、更新 feed、推送中继、云端编排)、正式签名与公证、Windows / Linux 真机验证、
升级链路的其余部分(M7:桌面自更新运行时已接入并在 macOS 上本机演练过,后端 feed、2.9 桥接、正式签名与公证、Windows / Linux 未做)、CI 工作流(已写但没在真实 GitHub Actions 上跑过)。逐项状态以 `docs/parity.md` 和 `docs/design/05-roadmap.md` 的「当前进度」为准。
设计文档在 `docs/design/`;实现必须与设计一致,偏离时先改设计文档。

## 1. 范围(必须)

- 3.0 只做 GUI。不新增任何面向用户的命令行入口(命令行控制端、插件 CLI、`ncw-agent` 的用户子命令)。
  `tools/` 下的构建和发布脚本只给开发和 CI 用,不对用户发布。
  服务器中继 `src/Ncw.Relay` 是**服务端部署物**(配置全部来自环境变量,没有命令行参数),不是用户命令行,同样不对用户发布。
- 3.0 的 Agent 只有两种部署形态:桌面内置(`--mode local`)和云端(`--mode cloud`)。不做独立设备服务。

## 2. 技术栈(必须)

- .NET 10(`global.json`);Avalonia **锁定** `12.1.5`(Arc 按这个版本构建,所有 Avalonia 包保持一致);TKCode.Arc **锁定** `1.1.0`。所有版本只写在 `Directory.Packages.props` 里。
- 唯一的例外:内联可视化的 WebView 用官方 MIT 包 `Avalonia.Controls.WebView`(版本号与 Avalonia 核心包不同步,单独锁 12.1.2,同样只写在 `Directory.Packages.props`);整个仓库只有 `src/Ncw.UI/Controls/NcwWidgetSurface.cs` 可以引用 `NativeWebView`(评估见 `docs/design/03-ui-layout.md` §8)。
- 注册方式是 `AppBuilder.UseArc(arc => arc.UseArcControls() …)`,`App.axaml` 里不放 Fluent 或 Simple 主题。
- 业务视图只引用 `Ncw.UI.Controls` 封装,不直接引用 `arc:` 控件(封装层自己除外)。
- 和 Avalonia 同名的控件(Button、TreeView、Slider……)在 XAML 里必须加 `arc:` 前缀,在 C# 里用别名。
- 不写死颜色值,一律使用 Arc token(`{arc:ArcDesignTokenResource …}`)。
- 不写死界面文字:XAML 用 `{l:NcwText Key}`,C# 用 `NcwTexts.Get / Format`。新增文字时在 `src/Ncw.UI/Localization/NcwTextKind.cs`
  加成员,并在 `en-US.xlf`、`zh-CN.xlf` 各加一条(缺一条构建失败)。`Ncw.Core`、`Ncw.Client` 不产出界面文字,只给结构化数据。
- 提示用 `arc:Tooltip.Tip`,不要用 Avalonia 的 `ToolTip.Tip`(在 Arc 下没有样式)。
- 不启用 `InvariantGlobalization`。不使用依赖反射的 MVVM 和序列化,必须兼容 AOT 和裁剪。
- C# 写法遵守 `docs/coding-style.md`;其中能机器检查的部分在 `.editorconfig`,构建时执行(`EnforceCodeStyleInBuild`,warning 级别违反即构建失败)。改规则时两处同步,升级为 warning 前先把全仓库清到零违规。

## 3. 状态统一管理(必须)

继承 2.x 的七条规则,C# 里的对应位置见 `docs/design/01-architecture.md` §5。简述如下:

1. 一个事实只有一个真源(在 Agent),控制端只在 `Ncw.Client/Stores/` 的**一个** Store 里保留一份镜像。key 带上 `deviceId`。
2. View 和 code-behind 只能放纯 UI 状态。ViewModel 不自己订阅协议事件。只传 id,不传状态快照。
3. 同一个事实在多处展示时,派生逻辑只能是 `Ncw.Core/Domain/` 下的一个纯函数,Agent 和客户端共用。
4. 同一个用户意图只对应 `Ncw.Client/Actions/` 里的一个方法。
5. 「运行中」类的状态必须有全量权威广播兜底,收到后对账;首帧和后续广播用同一个类型。
6. 状态单调:终态压过 running,重放的旧事件不能把状态改回运行中。
7. 派生视图跟随真源。设备断开时状态标为 `unknown`,不判成结束,也不继续计时。

## 4. 协议(必须)

- 协议定义只有一个来源:`src/Ncw.Protocol`。客户端和 Agent 直接引用它,不允许在别处另写一份帧或方法的类型。
- 协议是 3.0 全新设计(`docs/design/09-protocol.md`),**不参考、不兼容 2.x 的设备协议**。
- 不自研密码学:远程连接用 TLS 1.3 双向认证(证书钉扎);配对码验证用现成库的 SRP-6a;经中继时在隧道里跑同样的 TLS。
- 3.0 先实现本机和局域网承载;中继以后作为新的承载加入,不得为它修改 L1 以上的协议。
- 线上格式由按协议版本冻结的黄金向量保护;改线上格式必须升协议版本或加能力开关,并同步更新黄金向量和两端测试。
- 每个方法必须在方法表里声明种类、所需角色和所需能力;`command` 类方法必须带幂等键。

## 5. Agent(必须)

- Agent 用 C# 实现。2.x 的 TypeScript 代码只作为参考实现,不拷进本分支。
- 客户端项目不能引用 `Ncw.Agent.*`;Agent 项目不能引用 Avalonia 或客户端项目。
- `ncw-agent` 必须能以 NativeAOT 发布,发布时零裁剪警告。
- 移植一个 2.x 模块时,对应的单测要一起移植,并在 `docs/parity.md` 里登记;行为差异要写明原因。
- 数据库沿用 2.x 的 schema 和 `NCK1` 凭据格式,只做增量迁移。
- 工具、Hooks、插件宿主 API 只能通过项目的 `IExecutionEnvironment` 访问文件和进程,不允许直接用 `System.IO` 或 `Process` 碰设备本地(执行环境实现自身除外)。
- 涉及执行的审批和工具卡片必须带上执行环境标识(设备 + 环境)。
- 模型目录的真源在 Agent(`ModelCatalogService`),控制端只通过协议读取,不自己请求 models.dev。

## 6. 全栈 C# 与 JS 执行(必须)

- 整个仓库只用 C#(.NET 10)。不允许引入 Node、npm 包或 TypeScript 构建步骤,构建和发布工具也用 C# 写(`tools/`)。
- 需要执行 JS 时只能用 Jaunt,而且只能经过 `Ncw.Agent.Plugins` 的宿主,不允许在别处自己 `new JsEngine()`。
- 插件代码一律设置 `JsLimits` 并使用 `JsProfile.Hardened`;宿主 API 只能显式绑定,每个绑定都要先做权限检查。

## 7. 常用命令(骨架建好后补全)

- 构建:`dotnet build NextCoWork.slnx`
- 测试:`dotnet test NextCoWork.slnx`(UI 测试的截图输出在 `tests/Ncw.UI.Tests/bin/Debug/net10.0/screenshots/`)
- 桌面运行:`dotnet run --project src/Ncw.Desktop`
- Android 宿主(需要 .NET 的 android workload + JDK + Android SDK,不在 `NextCoWork.slnx` 里;安装与回退见 `docs/design/11-mobile-hosts.md` §2):`dotnet build NextCoWork.Mobile.slnx -p:AndroidSdkDirectory=$HOME/Library/Android/sdk -p:JavaSdkDirectory=$HOME/Library/Developer/Xamarin/jdk`;模拟器冒烟(开发 / CI,默认不跑):`NCW_ANDROID_SMOKE=1 dotnet run --project tools/Ncw.MobileSmoke -- smoke`(前提:模拟器已启动、`src/Ncw.Agent` 与 Android 宿主已构建)
- Agent 运行:`dotnet run --project src/Ncw.Agent -- --mode local`
- CI:`.github/workflows/ci.yml`(三平台:构建零警告、测试、UI 截图、`publish-agent` 零裁剪警告、`build-relay`)与 `release.yml`(tag / 手动触发的发布流水线);均未在真实 GitHub Actions 上运行过
- 中继(开发用,部署物):`NCW_RELAY_URLS=http://127.0.0.1:8080 dotnet run --project src/Ncw.Relay`(配置见 `docs/design/09-protocol.md` §9.3)
- 插件宿主的 NativeAOT 探针(开发 / CI,零警告 + 原生二进制里实跑一个插件):`dotnet publish tools/PluginAotProbe -c Release -r <rid> -o <dir>`,运行 `<dir>/PluginAotProbe`(退出码 0 = 通过)
- 动效的真窗口冒烟(开发用,不进 slnx,需要图形会话,只在 macOS 上验证过):`dotnet run --project tools/MotionSmoke`——真实窗口里在完整级别下触发展开 / 收起、左右栏滑动、设置覆盖层,记录每一帧的间隔、断言终态精确,再切到减弱 / 关闭核对终态一致,中间帧离屏截图到 `NCW_MOTION_SMOKE_OUT`(设计见 `docs/design/03-ui-layout.md` §9)。
- 启动加载的真实启动冒烟(开发用,不进 slnx,需要真实的 `ncw-agent` NativeAOT 产物;数据根 / 客户端目录都在临时目录,不碰 `~/.next-cowork`):`dotnet run --project tools/StartupSmoke -- timeline|faults|takeover|window --agent <ncw-agent> [--legacy-db <2.x 库的只读副本>]`——无界面的启动时间线(冷 / 热 / 接上)、故障注入(Agent 不存在 / 起不来 / 反复崩溃 / 版本不匹配 / 数据根被占用 / 运行中被杀)、真实 2.x 库接管、真窗口(原生底色读回、第一帧、帧间隔、淡出布局稳定、释放后空闲 CPU);只在 macOS 上验证过(设计与数据见 `docs/design/03-ui-layout.md` §10)。
- 构建与发布工具链(`tools/Ncw.Build`,只给开发和 CI 用;每个子命令 `--help` 看选项,产物都在被忽略的 `artifacts/` 下)。下面是 macOS arm64 上完整走一遍的命令(耗时和产物大小见 `docs/design/04-update-install.md` §7):
  - `dotnet run --project tools/Ncw.Build -- publish-agent --rid osx-arm64`:`ncw-agent` NativeAOT 发布,输出里有任何 IL / 裁剪警告就失败(clang 模块缓存 `.pcm` 的环境警告在白名单里);发布产物里出现开发用 KMS 提供者(`NCW_DEV_KMS_KEY`)也失败,云端模式的真进程冒烟要加 `--dev-kms` 发布一份不可分发的冒烟二进制,用 `NCW_AGENT_BINARY` 指给 `Ncw.Agent.Cloud.Tests`;
  - `dotnet run --project tools/Ncw.Build -- publish-desktop --rid osx-arm64`:Avalonia 桌面自包含发布;
  - `dotnet run --project tools/Ncw.Build -- bundle --rid osx-arm64`:把 `ncw-agent` 放进桌面应用的 `agent/` 约定目录(macOS 是 `.app`),写 `bundle.json`;
  - `dotnet run --project tools/Ncw.Build -- verify --rid osx-arm64`:启动包里的 `ncw-agent` 冒烟(本机通道握手、版本、单实例锁、优雅退出);
  - `dotnet run --project tools/Ncw.Build -- sign --rid osx-arm64`:codesign / signtool;凭据只读环境变量,缺失就跳过并说明,CI 的正式发布加 `--require` 改为失败(`--notarize` 公证);
  - `dotnet run --project tools/Ncw.Build -- velopack --rid osx-arm64 --channel stable`:用 `.config/dotnet-tools.json` 里固定版本的 `vpk` 打安装包和更新包;
  - `dotnet run --project tools/Ncw.Build -- manifest --scan artifacts/velopack/osx-arm64/stable --rid osx-arm64`:生成发布清单,有 `NCW_RELEASE_SIGNING_KEY` / `NCW_RELEASE_SIGNING_KEY_FILE` 才做 Ed25519 签名(`keygen`、`manifest-verify` 配套)。
  - `dotnet run --project tools/Ncw.Build -- upload --rid osx-arm64 --channel stable`:把这个平台的 Velopack 输出目录发布到后端的 3.x feed(`/api/client/updates/v3/{channel}/{rid}/`);上传前用 `NCW_RELEASE_PUBLIC_KEYS` 验签,验不过不传;令牌 `NCW_RELEASE_UPLOAD_TOKEN`,`--dry-run` 只做本地检查(后端协议见 `docs/design/04-update-install.md` §7.4「后端」)。清单要用 `manifest --scan <同一目录> --out <同一目录>` 生成;
  - 自更新演练(仅开发,macOS):`publish-desktop` 前设 `NCW_RELEASE_PUBLIC_KEYS=<kid>:<base64 公钥>`(构建时内置的信任根;不设则该构建的自更新拒绝一切清单),`velopack` 对第二个版本加 `--keep-output`(feed 累积版本、生成增量包),用 `keygen` 生成的仓库外私钥签 `manifest`;应用内的无界面演练只认环境变量 `NCW_DEV_UPDATE_REHEARSAL` / `NCW_UPDATE_FEED`(不是用户入口);自动化为可选测试 `NCW_REHEARSAL_ROOT=<准备好的目录> dotnet test tests/Ncw.Desktop.Tests --filter RealUpdateRehearsalTests`(默认跳过;准备步骤见 `docs/design/04-update-install.md` §7.5);
  - `dotnet run --project tools/Ncw.Build -- build-relay [--rid <rid>] [--aot] [--framework-dependent]`:发布服务器中继 `ncw-relay`(服务端部署物;默认自包含、`--aot` 为 NativeAOT、IL / 裁剪警告规则同 `publish-agent`),再对产物自检:启动 → `/healthz` → `/metrics` → 非 WebSocket 请求得 426 → 关 stdin 与(Unix)SIGTERM 两种方式优雅退出且退出码 0;`--skip-check` 跳过自检;
- 工具链与守护的测试:`dotnet test tests/Ncw.Build.Tests`、`dotnet test tests/Ncw.Desktop.Tests`(含真实 `ncw-agent` 冒烟;没有构建产物时自动跳过,`NCW_SKIP_AGENT_SMOKE=1` 强制跳过,`NCW_AGENT_PATH` 指向别的产物)。
