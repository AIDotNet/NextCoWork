# 04 · 更新与安装

目标:**已经装了 2.x 的用户,通过自动更新收到 3.0 并完成安装,数据原地保留。**
不管 3.0 是怎么装上的(自动更新、手动下载安装、测试版),都能**直接接管 2.x 的数据**(见 §4)。
3.0 以后的版本由新的更新器接管。

## 1. 2.x 现状(基线)

| 项 | 现状 |
|---|---|
| 更新库 | `electron-updater`,`provider: generic` |
| Feed | `https://nextco.work/api/client/updates/feed/stable/` |
| 发布 | `scripts/upload-client-release.mjs` POST 到 `/api/client/updates/upload`,带版本、平台、架构、通道、sha256,以及 mandatory / minimumSupportedVersion / graceUntil 等请求头 |
| 产物 | Windows x64 NSIS `.exe`;macOS arm64/x64 `.dmg`(安装用)和 `.zip`(更新用);Linux x64 `.AppImage` |
| 签名 | macOS 用 ad-hoc 签名(`scripts/mac-adhoc-sign.mjs`),Windows 未签名 |
| 安装位置 | NSIS 按用户安装、目录可选;appId `com.nextcowork.app` |
| 数据 | profile 根 `~/.next-cowork`(Chromium 的 `Local State` 等在这一层),数据根是它下面的 `data/`(SQLite 主库、附件、settings.json、skills 等)。更早的打包版数据在 Electron 默认 userData 里,已由 2.x 自己迁过来。凭据大部分已经是 `NCK1` 自管加密,少量旧行可能仍是 safeStorage 密文 |
| 强制更新 | `canStartNewRuns()` 在低于 minimumSupportedVersion 且超过宽限期时禁止新开 Run |

## 2. 升级路径:2.9 引导到 3.0

```
  2.x(<2.9)──electron-updater──▶ 2.9(引导)──用户从官网装 3.0──▶ 3.0(Avalonia + 内置 ncw-agent)──Velopack──▶ 3.x
```

**服务端门控**:3.0 不进 electron-updater 的 feed(见下「为什么不直接把 3.0 放进 electron-updater 的 feed」),2.x 的 feed 里最新的就是 2.9,
所以不需要按客户端版本门控(B1 不做)。2.9 通过 `GET /api/client/updates/v3/latest?channel=stable`(后端已实现,见 §7.4「后端」)得知各平台 3.0 的版本和安装包;
签过名的清单原样在 `/api/client/updates/v3/{channel}/{rid}/release-manifest.json(.sig)`。

### 2.9 只做引导(2026-11-17 定,偏离原设计)

原设计里 2.9 是「桥接版」:预检、整理数据、写 `handoff-v3.json`、自己下载校验并安装 3.0(Windows 静默 `Setup.exe`、macOS helper 原子替换 `.app`、Linux 替换 AppImage)。
**改为只引导**,理由:3.0 首次启动自己就能完整接管 2.x 的数据(§4:一致性备份 → 所有权标记 → 增量迁移,已在 macOS 上用真实 2.x 库验证),
桥接版的那些步骤都不是接管的前提;而自动安装要依赖正式签名、三个平台各一套 helper,且 2.x 一侧没有真机就无法验证。2.9(`main` 分支的 `release/2.9`)做的是:

1. **告诉用户 3.0 出来了**:主进程按 electron-updater 同一个节奏(启动后 30 秒、每天一次、手动「检查更新」时)问 `/v3/latest`;
   只有这台电脑的 RID 确实有可下载的安装包、主版本 ≥ 3 时才算有(问不到 = 不知道,界面什么都不显示)。标题栏右端出一颗升级图标(2.x 自己有更新时让位,先装 2.9.x 的修复),
   点它打开「设置 → 关于」,顶部是升级卡片:数据会自动迁移(迁移前完整备份;个别密钥可能要重新填)、需要重新配对、2.x 的独立设备服务 3.0 不支持、迁移后 2.x 不再打开这份数据。
2. **「前往下载并退出」**:打开官网下载页(它按访客系统自动选包),然后走 2.x 正常的退出流程(收尾运行中的任务、拦下未保存的文档)。必须退出:3.0 在 2.x 开着时拒绝接管。
3. **认 3.0 的接管**:2.9 启动时、建窗和开库之前,只读地看一眼数据库:`migrations` 里有比 2.x 已知的最新(37)更新的版本,或者有所有权标记、但它的 `device_id` 不是同一数据根里
   2.x 独立设备服务的 `device-identity.json` —— 就是被 3.0 接管了(2.x 的设备服务接管时行为不变)。这时不建窗、不开库,弹系统对话框「打开 NextCoWork 3.0 / 下载 3.0 / 退出」。
   「打开 3.0」只在找得到装好的 3.x 时出现(靠 Velopack 的 `sq.version` 认,macOS 上两代的 bundle 名和 bundle id 都一样)。
   **为什么必须拦**:2.x 的迁移执行器跳过它不认识的版本、照常往库里写,两个版本交替打开会写坏数据。
4. **不做**:预检、凭据预转换(3.0 自己能解 safeStorage)、2.9 自己的备份(3.0 接管时做 `VACUUM INTO`)、`handoff-v3.json`(3.0 不读它)、下载校验与自动安装。

**回退**:3.0 的「设置 → 数据 → 回到 NextCoWork 2.x」见 §5;2.9 的升级卡片和「数据已由 3.0 接管」对话框都指向它。

### 为什么不直接把 3.0 放进 electron-updater 的 feed

- Windows 上,electron-updater 会以 `/S --updated` 参数运行 NSIS 安装包。3.0 的安装器虽然可以配合这组参数,
  但在交接之前没有机会整理数据。
- macOS 上,Squirrel.Mac 会用当前 App 的 designated requirement 去校验新 bundle。ad-hoc 签名的
  designated requirement 绑定的是 cdhash,换成另一个程序的 bundle,结果没法保证。
- 3.0 能直接接管(§4),不需要 2.x 一侧先停 Run、做备份;2.9 只要保证接管时 2.x 已经退出(引导流程最后一步就是正常退出)。

## 3. 3.x 的自更新(Velopack)

- 产物:Windows `Setup.exe` + `.nupkg`(全量和增量);macOS `.pkg` 或 `.zip`(要求 Developer ID 签名和公证);
  Linux AppImage。
- Feed:Velopack 的 `releases.{channel}.json` 是静态文件。nextco.work 新增 `/api/client/updates/v3/{channel}/` 来提供,
  通道沿用 `stable` / `beta`。**客户端实现里多了一级平台目录**:`/api/client/updates/v3/{channel}/{rid}/`(`rid` = `osx-arm64` / `win-x64` / `linux-x64`…)——
  vpk 的产物文件名里没有平台标识,`releases.<channel>.json` 也是同名文件,几个平台放同一个目录会互相覆盖。后端已按这个布局实现(见 §7.4「后端」);
  feed 位置在客户端是可配置的(`NCW_UPDATE_FEED`:本地目录或本地 HTTP 服务,开发和测试用)。
- 发布工具用 C# 重写:打包、签名、清单在 `tools/Ncw.Build`(M4 已做,见 §7),打包调用 Velopack 的 `vpk`;上传是 `tools/Ncw.Build upload`(见 §7.1;偏离:没有单独的 `Ncw.Release` 项目),沿用 2.x 的上传 token
  和同一组元数据(mandatory、minimumSupportedVersion、graceUntil、更新说明 base64)。
- 强制更新的语义保持不变。`canStartNewRuns` 的判定改到 Agent 侧,由控制端上报版本,
  Agent 拒绝来自过低版本控制端的新 Run。**判定函数已经在 Core**(`Ncw.Core.Domain.UpdatePolicy`,纯函数,时钟由调用方传入,见 §7.4),
  控制端的「关于」页用它显示必需 / 阻断;Agent 侧据此拒绝新 Run 的接线(握手带版本、`client_outdated` 之外再加一个「需要更新」的拒绝码)还没做。
- **自更新运行时已接入桌面端**(M7,§7.4),并在本机用真实的 feed 演练过「3.0.0 → 3.0.1」(§7.5)。
- **sidecar 和客户端一起更新**:内置的 `ncw-agent`(NativeAOT 可执行文件 + 同目录的 `libe_sqlite3` 原生库)打进同一个 Velopack 包,版本号和客户端一致。
  更新前,桌面端先让 sidecar 收尾,再执行重启安装(沿用 2.x「先走应用自己的退出流程,再安装」的经验)。
- **云端 Agent** 由编排服务滚动升级,和客户端版本不必同步,靠协议 N/N-1 兼容。
- 3.0 不做独立设备服务,也就没有单独的服务端更新链路。

### 移动端分发

- iOS:App Store 加 TestFlight(beta 通道)。
- Android:Google Play,另外在 nextco.work 提供 APK 直装(在 App 内检查更新,跳转系统安装器)。
- 移动端也要遵守最低支持版本:Agent 在握手时拒绝过低版本的控制端,并返回「请更新 App」的错误码。
- 移动端宿主项目(`Ncw.Mobile.iOS` / `Ncw.Mobile.Android`)的结构、要实现的平台接口、权限与构建所需的 workload 见 [11 移动端宿主](11-mobile-hosts.md)。

## 4. 数据接管(直接接管 2.x 数据)

**任何安装方式都能接管**:经 2.9 自动升级、从官网手动下载 3.0、测试版,首次启动都走同一套流程。
`handoff-v3.json` 有就读取(拿备份位置和版本),没有也照样接管。

### 发现

1. 按 2.x 的规则找 profile 根:默认 `~/.next-cowork`,数据根是其下的 `data/`。还要检查 Electron 默认 userData
   (`%APPDATA%\NextCoWork`、`~/Library/Application Support/NextCoWork`)里有没有 2.x 还没迁走的旧数据。
2. 找到 2.x 数据,就进入接管页;找不到,就是全新安装,直接创建新的数据根。

### 接管步骤(启动加载界面上显示进度,没有命令行入口)

> 接管中的步骤(备份 / 升级 / 提交 / 凭据)显示在**启动加载界面**里(和数据页读同一个 `data` 事实,[03 §10](03-ui-layout.md#10-启动与加载));被挡住、失败、接管完成等「继续」时才由数据页接管整窗。

实现位置:`Ncw.Agent.Storage/Takeover/`(移植自 2.x 的 `main/migration/takeover.ts` 和 `db/takeover.ts`),
由 `Ncw.Agent.Host/DataGate` 在 Agent 启动时自动执行;进度和结果通过协议的 `data` 主题推给控制端(09 §8.2),
被挡住或失败时控制端调 `data.retry`。补充约定:

- **每次启动**都检查 2.x 是否在用这份数据,不只是接管时(2.x 不认识所有权标记)。`SingletonLock` 同时查数据根和它的上一级
  (2.x 的 Electron profile 根是 `~/.next-cowork`,锁落在那里;2.x 自己只查数据根会漏)。
- 全新的数据根在建库时也写入所有权标记(`journal_id` 为空),以后不会被误当成 2.x 数据再接管。
- 数据根里没有库、但扁平布局或 Electron 默认目录里有 2.x 的库时,不在 3.0 里重做 2.x 的搬家逻辑,
  而是提示用户先用最新的 2.x 打开一次(`blocked/unsupported-layout`)。
- 凭据(第 4 步)在提交之后、就绪之前转换,只有库里还有旧 safeStorage 行时才执行,并在接管页上先显示「系统可能请求访问钥匙串」。
  实现在 `Ncw.Agent.Storage/Credentials/`;钥匙串只在遇到第一条旧行时读取一次。
- 迁移版本号和 2.x 共用一个序列(1–37 是 2.x 的,38 起是 3.0 的),所以 **2.9 不能再新增数据库迁移**(2.9 也正是靠「迁移版本 > 37」认出 3.0 的接管,见 §2)。

1. **检查 2.x 是否在运行**:Chromium 的 `SingletonLock` 指向活进程,或者服务锁被占用时,界面提示「请先退出 NextCoWork 2.x」,
   并提供「帮我退出」按钮。不抢锁。
2. **一致性备份**:`VACUUM INTO` 到备份目录,同时复制凭据主密钥和供应商文件;做完整性检查,逐表核对行数。
3. **取锁并写所有权标记**:沿用 2.x 的 `service_ownership` 表和 journal(begin → verified → committed,崩溃后从记录的阶段继续)。
4. **解开遗留的 safeStorage 凭据**:3.0 用 C# 按 Chromium `os_crypt` 的格式直接解密,然后改存成 `NCK1`:
   - macOS:从钥匙串读 `NextCoWork Safe Storage` 口令,PBKDF2 派生后 AES-CBC 解密(`v10` 前缀)。
     因为 3.0 的签名和 2.x 不同,系统可能弹出钥匙串授权框,接管页要提前说明。
   - Windows:用 DPAPI 解开 `Local State` 里的 `os_crypt.encrypted_key`,再用 AES-GCM 解密(`v10` 前缀)。
   - Linux:`v11` 走 libsecret,`v10` 用固定口令。
   - 解不开的凭据不阻断接管,标记为「需要重新输入」,接管完成后列出清单让用户补填。
   - 具体格式要按 2.x 实际用的 Electron 版本核实,并用真实数据测试。
5. **不迁移的数据**:2.x 的设备配对记录和设备身份(协议全新设计,旧配对无法使用),接管完成后在清单里说明,需要时重新扫码配对。
6. **增量迁移**:执行 3.0 的数据库迁移(新增 Profile、推送注册、云端身份、执行环境等表)。C# Agent 直接沿用 2.x 的 schema
   和 `NCK1` 凭据格式(决策 D13)。
7. **安置 2.x 程序**:2.x 桌面端**不认识**接管后的锁(2.x 自己的 takeover 文档写明了这一点),如果两个版本同时打开同一份数据会出问题。所以:
   - Windows:找到 2.x 的 NSIS 卸载程序,以「保留用户数据」的方式静默卸载。
   - macOS:3.0 的 bundle 本来就安装在同一位置(`/Applications/NextCoWork.app`)。如果检测到另一个位置还有 2.x 的 App,提示用户删掉。
   - Linux:提示用户删掉旧的 AppImage。
   - 2.9 起的 2.x 会认这个所有权标记,启动时提示「数据已由 3.0 接管」,并给出打开 3.0 的按钮。
8. **布局**:从账户的配置同步里拉取 `RailLayout`;没有的话,根据 2.x 的工作区列表生成默认布局,所有项目都不分组。
9. **完成**:欢迎页显示「已从 2.x 迁移 N 个项目、M 个会话」,如果有需要重新输入的凭据,一并列出。

## 5. 回滚

- 在接管提交之前,任何失败都会自动无损回退,并在启动页上给出原因和「重试」按钮。
- 接管提交之后的回退在「设置 → 数据 → 回到 NextCoWork 2.x」里操作(已实现):先把接管后写入的库另存一份,再恢复接管前的备份,
  然后退出 3.0,用户接着用 2.9 打开。全程在图形界面里完成。细节:
  - **入口只在能回退时出现**:本机设备的 owner、这份数据确实是从 2.x 接管来的(接管日志是 committed)、接管前的备份还在。全新的 3.0 数据没有这个入口。
  - **回退要求数据库已经关闭**,而 Agent 运行时一直开着库,所以分两步(`RollbackToLegacy`):① 用户勾选「迁移之后在 3.0 里新写的数据不会带回 2.x」并确认 →
    `data.requestRollback` 只在数据根里登记一个请求文件;② 控制端让本机 Agent 优雅退出(和更新重启同一条路,不管「关窗后继续运行」),
    Agent 在收尾的最后一步、关库之后执行回退(`DataGate.Dispose`),控制端随后退出应用。动作顺序固定为「登记 → 停 Agent → 退出」,登记失败时什么都不停。
  - **Agent 被强杀、没来得及执行**:请求文件还在,下次启动 3.0、开库之前补做。这种情况下用户如果先开了 2.9,2.9 会看到数据仍属于 3.0 并提示打开 3.0——打开一次即完成回退。
    「2.x 正在运行」之类以后能成功的失败保留请求;备份没了之类永远不会成功的失败删掉请求。
  - **回退之后 3.0 不自动再接管**:接管日志停在 rolled-back,`data` 状态是 `rolled-back`,启动页显示「数据已回退给 NextCoWork 2.x」和「重新迁移」;
    用户点了才调用 `data.retry` 再接管一次(先备份,再迁移 2.x 现在的数据)。否则刚回到 2.x 的用户误开一次 3.0,数据就又被拿走。
  - 回退后的库没有所有权标记、迁移版本 ≤ 37,所以 2.9 的「数据已由 3.0 接管」检查(§2)放行。3.0 里写的那份库在备份目录的 `post-takeover.db`,不删除。
- 2.9 的安装包继续保留在 nextco.work 上(2.x 维护期暂不规划,先不设下线时间)。「设置 → 数据」里提供「回退到 2.x」的入口和说明。
- 3.0 的数据库迁移要么完全可逆,要么在迁移前备份,备份保留 30 天。

## 6. 签名(证书已具备,3.0 起启用)

| 平台 | 做法 | 原因 |
|---|---|---|
| macOS | 3.0 起用 Developer ID 签名,开启 Hardened Runtime,并公证(notarytool + staple)。`ncw-agent` 和内置的原生库(e_sqlite3 等)都要签名 | Gatekeeper 不再拦截首次打开;Velopack 在 macOS 上依赖签名 |
| Windows | 3.0 起用 Authenticode 签名:`Setup.exe`、主程序、`ncw-agent.exe` 和原生库 | 减少 SmartScreen 警告 |
| 所有平台 | 发布清单另外用 Ed25519 签名 | 不依赖平台签名,也能保证 3.x 自更新拿到的产物没被篡改(2.9 只引导、不下载 3.0) |

**2.9 自己仍然保持 ad-hoc 签名**:2.8 → 2.9 这一跳走的还是 electron-updater 和 Squirrel.Mac,
签名方式一变,就可能过不了旧版本的 designated requirement 校验。换签名身份只发生在 2.9 → 3.0 这一跳,
而这一跳是用户从官网下载安装,不经过 Squirrel。

## 7. 实现备注(M4:sidecar 打包与守护、`tools/Ncw.Build`)

### 7.1 `tools/Ncw.Build`(只给开发和 CI 用,不对用户发布,没有任何用户可见的命令行)

C# 控制台项目,在 `NextCoWork.slnx` 的 `/tools/` 下;单测在 `tests/Ncw.Build.Tests`。命令行解析是手写的(未知选项直接报错)。子命令按一条发布流水线的顺序:

| 子命令 | 做什么 | 要点 |
|---|---|---|
| `publish-agent` | `ncw-agent` 按 RID 做 NativeAOT 发布(`dotnet publish -c Release -r <rid> --self-contained -p:PublishAot=true`) | 输出里任何 `IL…` / `ILC…` / "Trim|AOT analysis warning" **都当失败**(即使 `dotnet` 退出码是 0);clang 模块缓存 `.pcm` 的环境警告在白名单里并如实打印(本机实测每次 2 条,内容是 `EXEC : warning : …/ModuleCache/…Foundation-….pcm: No such file or directory`);其他警告只报告,`--strict-warnings` 才失败。同一条警告在多个项目里重复出现会去重。不能跨操作系统编译(NativeAOT 需要目标平台的原生链接器),会直接说明原因。`.pdb` / `.dSYM` 移到 `artifacts/symbols/<rid>/`,发布目录里不带符号 |
| `publish-desktop` | 桌面自包含发布 | **不做 AOT、不裁剪**(Avalonia / Arc 的 XAML 与资源加载不适合裁剪);`--ready-to-run` 可选。开发构建的 `Ncw.Desktop.csproj` 会把 Debug 的 agent 复制到 `<输出>/agent/`,发布目录里如果出现它会被删掉 |
| `bundle` | 把 `ncw-agent`(及同目录的 `libe_sqlite3`)放进桌面应用的约定目录,写 `bundle.json` | 布局见下;不带 `.pdb` / `.dSYM` |
| `verify` | 先核对 `bundle.json` 里每个文件的 sha256,再启动包里的 `ncw-agent` 做冒烟 | 本机通道 + 本机令牌握手、`device.info` 报告版本(必须等于包的版本)、同一数据根再起一个必须被单实例锁拒绝(退出码 75)且第一个不受影响、关闭 stdin 后必须以 0 退出。也可以 `--agent <路径>` 直接测某个二进制 |
| `sign` | macOS:`codesign`(hardened runtime、时间戳、entitlements)由内到外逐个文件签,最后签 `.app`,`codesign --verify --deep --strict`;`--notarize` 再 `notarytool submit --wait` + `stapler staple`。Windows:`signtool sign` / `verify`(所有 `.exe` 和原生 `.dll`,不逐个签托管 dll)。Linux:没有平台签名步骤 | 凭据**只读环境变量**,缺失就**跳过并明说**,`--require` 改为失败(CI 的正式发布任务必须加,防止悄悄发出未签名的包);签完重写 `bundle.json`(`signed: true`)。变量:`NCW_MAC_SIGN_IDENTITY`("-" 表示 ad-hoc,只用来在本机演练流水线)、`NCW_MAC_KEYCHAIN`、`NCW_NOTARY_PROFILE` 或 `NCW_NOTARY_APPLE_ID`+`NCW_NOTARY_TEAM_ID`+`NCW_NOTARY_PASSWORD`、`NCW_MAC_INSTALLER_IDENTITY`(.pkg 签名,传给 vpk)、`NCW_WIN_CERT_FILE`+`NCW_WIN_CERT_PASSWORD` 或 `NCW_WIN_CERT_THUMBPRINT`、`NCW_WIN_TIMESTAMP_URL`、`NCW_SIGNTOOL`。密码在日志里脱敏。entitlements 沿用 2.x 的 `build/entitlements.mac.plist`(JIT、可执行内存、关闭库验证,自包含 .NET 运行时需要) |
| `velopack` | `dotnet tool run vpk pack`:全量 `.nupkg`、portable、安装器(macOS `.pkg`;Windows `Setup.exe`;Linux AppImage)、`releases.<channel>.json` | `vpk` 版本只写在 `.config/dotnet-tools.json`(dotnet 工具包唯一能固定版本的地方);运行时库 `Velopack`(M7 桌面端接入时用)的版本写在 `Directory.Packages.props`,两者必须相同,`Ncw.Build.Tests` 核对。目标系统和宿主不同时带 vpk 的 `[os]` 前缀。macOS 签名交给 vpk(见偏离 4) |
| `manifest` | 生成 `release-manifest.json`:各平台产物、sha256、大小、强制更新策略(mandatory / minimumSupportedVersion / graceUntil)、`url`(上传后才有,先为 null) | 有 Ed25519 私钥(`NCW_RELEASE_SIGNING_KEY` 为 32 字节种子的 base64,或 `NCW_RELEASE_SIGNING_KEY_FILE`;`NCW_RELEASE_SIGNING_KEY_ID`)就签,**没有就只写清单、`signing.status = "pending"`,并明说**(持有密钥的发布任务再跑一次)。用 BouncyCastle(.NET 没有内置 Ed25519,仓库本来就固定了它);已用 RFC 8032 §7.1 的向量验证 |
| `manifest-verify` / `keygen` | 按公钥验证清单与分离签名并核对产物 sha256 / 生成密钥对 | `keygen` 拒绝把私钥写进仓库 |
| `upload` | 把一个平台的 Velopack 输出目录(含签过名的 `release-manifest.json(.sig)`)发布到后端的 3.x feed:清单里每个文件 `HEAD` → `PUT /v3/blobs/{sha256}`,再 `POST /v3/{channel}/{rid}/publish` | **先在本地做客户端会做的检查**:`signing.status = signed`、用 `NCW_RELEASE_PUBLIC_KEYS`(按清单 keyId;或 `--public-key` / `NCW_RELEASE_PUBLIC_KEY`)验签、通道与平台一致、每个文件的大小和 sha256 与清单一致,任何一条不过就一个字节都不传。令牌只读 `NCW_RELEASE_UPLOAD_TOKEN`(不进日志),服务器 `--server` / `NCW_RELEASE_SERVER`(默认 `https://nextco.work`,公网只接受 HTTPS);`--dry-run` 只做本地检查;`--allow-older` 显式回滚。重跑幂等(已有的文件跳过)。后端协议见 §7.4「后端」 |
| `build-relay` | 发布服务器中继 `ncw-relay`(`src/Ncw.Relay`,服务端部署物,09 §9.3),再对产物自检;**独立于上面桌面应用的流水线** | 形态:默认**自包含**(部署机不用装 .NET / ASP.NET 共享框架,约 123 MiB)、`--framework-dependent`(约 12 MiB,部署机要有 ASP.NET Core 10 运行时)、`--aot`(NativeAOT,约 11.5 MiB;和框架依赖互斥,不能跨操作系统编译,只支持 Release)。非 AOT 时显式 `PublishTrimmed=false`(AOT 隐含裁剪,不能再传这个参数)。**IL / 裁剪警告的规则和 `publish-agent` 完全一致**(任何 `IL…` / `ILC…` 都失败,clang `.pcm` 环境警告白名单,`--strict-warnings` 让其他警告也失败);`.pdb` / `.dSYM` 移到 `artifacts/symbols/<rid>-relay/`;产物在 `artifacts/publish/<rid>/relay/`。**自检**(`--skip-check` 跳过;产物平台和本机不同则只发布不启动并明说):以环境变量配置(回环上的空闲端口)启动产物 → `/healthz` 200 → `/metrics` 200 且非空 → 非 WebSocket 的 `GET /connect` 得 426 → 关闭 stdin(`NCW_RELAY_LIFELINE=stdin`,跨平台含 Windows)后限时内以 0 退出 → Unix 上再起一个实例发 **SIGTERM**(容器 / systemd 的停止方式)同样必须以 0 退出。健康检查路径是 `/healthz`(不是 `/health`) |

**清单签名是分离签名**:对 `release-manifest.json` 的**原始字节**签,签名文件是 `release-manifest.json.sig`(base64)。验证方(2.9 里的 JS 或后端)不需要做 JSON 规范化。清单里的 `signing` 字段(算法、keyId、状态)在被签的字节里。

**产物目录**(都在被 `.gitignore` 忽略的 `artifacts/` 下,不在 `bin/`、`obj/`):`publish/<rid>/{agent,desktop}`、`symbols/<rid>`、`bundle/<rid>/`、`velopack/<rid>/<channel>/`、`release/<version>/`。版本号只有一个来源:`Directory.Build.props` 的 `<Version>`(客户端、`ncw-agent`、安装包共用;`--version` 可覆盖)。

**应用包布局**(桌面端 `AgentLocator` 按同一约定找 sidecar):桌面可执行文件所在目录下的 `agent/`。

```
macOS    NextCoWork.app/Contents/MacOS/NextCoWork
         NextCoWork.app/Contents/MacOS/agent/NextCoWork Agent.app/Contents/MacOS/NextCoWork Agent  + libe_sqlite3.dylib
                                                         (AppContext.BaseDirectory = Contents/MacOS/;Agent 是嵌套的后台应用,见下「进程名称与图标」)
         …/NextCoWork Agent.app/Contents/Info.plist      (com.nextcowork.app.agent、LSBackgroundOnly、CFBundleIconFile)
         …/NextCoWork Agent.app/Contents/Resources/AppIcon.icns
         NextCoWork.app/Contents/Info.plist              (com.nextcowork.app、nextcowork:// 的 CFBundleURLTypes、CFBundleIconFile、LSMinimumSystemVersion 14.0 ← 待核实)
         NextCoWork.app/Contents/Resources/AppIcon.icns  (Dock / Finder / ⌘Tab 的应用图标)
Win/Linux NextCoWork/NextCoWork[.exe]  与  NextCoWork/agent/ncw-agent[.exe] + e_sqlite3
bundle.json 在包的外面(和包同级):macOS 签名之后包里任何文件都不能再改
```

**应用图标**:源图是 2.x 的 1024×1024 `build/icon.png`(已带 macOS 圆角方块和留白),放在 `src/Ncw.Desktop/Assets/AppIcon.png`;`AppIcon.icns` 用 `iconutil` 从它生成后**入库**(不在构建时生成,这样在非 macOS 主机上跨平台打 osx 包也有图标),`bundle` 把它复制进 `Contents/Resources/` 并在 Info.plist 写 `CFBundleIconFile`,缺文件就让 `bundle` 失败(而不是发出一个 Dock 里空白图标的包)。
**不是从 .app 启动的**(IDE 直接起 `bin/` 下的可执行文件,Dock 会显示默认图标)由 `MacDockIcon` 在 Avalonia 平台初始化后把内嵌的 `AppIcon.png` 设成 `NSApplication.applicationIconImage`(只改 Dock 里这一份,不写文件);从 .app 启动的不动,用包自带的图标。Windows / Linux 的窗口与安装包图标还没做。

**进程名称与图标(macOS)**:活动监视器、菜单栏、Dock、⌘Tab 里看到的名字和图标要和 2.x 一样是「NextCoWork」+ 应用图标,不能是可执行文件名 + 通用图标。
- **应用名**:`NcwApp` 的 `Application.Name = "NextCoWork"`,并在应用上挂自己的原生应用菜单(「关于 NextCoWork」打开设置的「关于」、「设置…」);否则 Avalonia 给的默认菜单是「About Avalonia」。Avalonia 会在后面补上服务、隐藏、退出这些系统项。
- **Agent**:活动监视器显示的是**可执行文件名**,图标取自它所在的应用包。所以 macOS 上 sidecar 是嵌套的后台应用 `agent/NextCoWork Agent.app`(`LSBackgroundOnly`:不进 Dock、没有菜单),可执行文件改名为 `NextCoWork Agent`(正好 16 个字符,内核的进程名不会被截断),和 2.x 的「NextCoWork Helper」同一个做法;Windows / Linux 不变(`agent/ncw-agent[.exe]`)。`publish-agent` 的产物仍叫 `ncw-agent`,`bundle` 放进包时改名。签名顺序:嵌套包里的文件 → 嵌套包 → 主程序 → 主包。
- **开发构建**:`dotnet run` 在 macOS 上启动的是构建时生成的 `<输出>/NextCoWork.app`(输出目录用硬链接放进 `Contents/MacOS/`,`Info.plist` 用开发专用的 bundle id `com.nextcowork.app.dev`,不注册 `nextcowork://`,免得抢正式安装的链接),Agent 同样包成 `agent/NextCoWork Agent.app`。`bin/` 下原来的 `NextCoWork` 和 `agent/ncw-agent` 保留,测试和冒烟工具照旧用它们。

**与设计的偏离**(先改文档,再实现):

1. **D16「单文件程序(`dotnet run app.cs`)」→ 普通 C# 控制台项目**。工具链有十几个文件、要引用 `Ncw.Protocol`(`verify` 真的走协议)和 BouncyCastle、并且需要单测,单文件程序都不合适。仍然只有 C#,CI 只需要 .NET SDK。
2. **上传是 `Ncw.Build` 的 `upload` 子命令,不是单独的 `tools/Ncw.Release` 项目**:它要读清单、用同一套 Ed25519 代码验签、共用命令行解析和产物目录约定,拆成两个项目只多一份重复。
3. **发布清单用分离签名**(见上),不是把签名嵌进 JSON。
4. **macOS 的最终签名交给 vpk,不是我们的 `sign`**:vpk 会往 `.app` 里加自己的文件(更新器、`sq.version`),本机实测「`sign` 签完再 `vpk pack`」得到的 `.app` 通不过 `codesign --verify --deep --strict`(`a sealed resource is missing or invalid`)。所以 `velopack` 在设置了 `NCW_MAC_SIGN_IDENTITY` 时给 vpk 传 `--signAppIdentity`、`--signEntitlements`(vpk 要求扩展名 `.entitlements`),并在 `sign` 已经签过时加 `--signDisableDeep`(vpk 只补它自己加的文件);`.pkg` 签名与公证传 `--signInstallIdentity` / `--notaryProfile`。`sign` 仍然保留:Windows 必须先签主程序和 `ncw-agent.exe`,macOS 上用来尽早发现签名问题,并且是单独验证 sidecar 签名的入口。
5. **`Contents/MacOS` 里的每个文件都要签,主程序最后签**(嵌套的 `NextCoWork Agent.app` 只签它自己 `Contents/MacOS` 里的文件,再签整个嵌套包,在主程序之前)。只签 Mach-O 会在签主程序时报 `In subcomponent: xxx.dll … code object is not signed at all`(.NET 应用把全部程序集放在 `Contents/MacOS`)。2.x 的另一个教训也带过来了:签名前 `xattr -cr`(位于 iCloud 同步目录里的输出会被盖上 `com.apple.FinderInfo`,`codesign` 拒绝)。

**本机实测**(macOS arm64,.NET 10.0.401,Release;`artifacts/` 在 `.gitignore` 里):

| 步骤 | 耗时 | 结果 |
|---|---|---|
| `publish-agent --rid osx-arm64` | 54 s(清掉 `obj/Release` 后的完整 ILC 编译与链接) | 零裁剪警告;2 条 clang `.pcm` 环境警告在白名单里;`ncw-agent` 48.6 MiB,发布目录 50.2 MiB(`ncw-agent` + `libe_sqlite3.dylib` + Jaunt 的第三方声明文件),23 个符号文件移出 |
| `publish-desktop --rid osx-arm64` | 10 s | 自包含、不裁剪,约 143 MiB |
| `bundle --rid osx-arm64` | 0.6 s | `NextCoWork.app`,262 个文件,192.9 MiB |
| `verify --rid osx-arm64` | 2.0 s | PASS:ready 1.3 s、握手(协议 1、角色 owner)、版本 3.0.0-alpha.1、第二个实例被拒(75)、优雅退出(0) |
| `sign`(ad-hoc,`NCW_MAC_SIGN_IDENTITY=-`) | 6.2 s | 261 个文件 + `.app`,`codesign --verify --deep --strict` 通过;签名后 `verify` 仍然 PASS(263 个文件与刷新后的 `bundle.json` 一致) |
| `velopack --rid osx-arm64 --channel stable`(ad-hoc) | 11 s(首次 `dotnet tool restore` 另需约 1.5 分钟下载 vpk) | `NextCoWork-3.0.0-alpha.1-stable-full.nupkg` 76.6 MiB、`NextCoWork-stable-Portable.zip` 74.3 MiB、`NextCoWork-stable-Setup.pkg` 73.9 MiB、`releases.stable.json` 等;从 portable 包解出的 `.app` 通过 `codesign --verify --deep --strict` |
| `sign --require` + `velopack`(**Developer ID Application**,2026-11-17,不公证) | `sign` 约 2.5 分钟(每个文件都要向 Apple 的时间戳服务取时间戳) | 266 个文件 + `.app`:`codesign --verify --deep --strict` 通过,`Authority=Developer ID Application`、带时间戳、`flags=runtime`(hardened runtime),主程序与 `NextCoWork Agent.app` 的 entitlements 为 allow-jit / allow-unsigned-executable-memory / disable-library-validation;签名后 `verify` 仍然 PASS;vpk 产物 `NextCoWork-3.0.0-stable-full.nupkg` 88.0 MiB、`Portable.zip` 86.7 MiB、`Setup.pkg` 86.0 MiB,从 portable 包解出的 `.app` 验签通过。`spctl` 报 `Unnotarized Developer ID`(没公证,预期);`Setup.pkg` **没有签名**(这台机器没有 Developer ID Installer 证书)。`timestamp.apple.com` 连不上时 codesign 报 "A timestamp was expected but was not found",网络恢复后重跑即可 |
| `manifest` | 0.5 s | 4 个产物;没有密钥,`status = pending` 并明说。另用 `keygen` 生成的临时密钥走了一遍签名 → `manifest-verify`(签名与 sha256 通过;改一个字节后验证失败) |
| `publish-desktop --version 3.0.0` / `3.0.1`(内置开发公钥)→ `bundle` → `verify` → `sign`(ad-hoc)→ `velopack`(3.0.1 加 `--keep-output`)→ `manifest`(Ed25519 签名)| 见 §7.5 | 自更新演练用的两个版本;`velopack --keep-output` 让 feed 累积版本、vpk 生成 3.0.0 → 3.0.1 的增量包(107 KiB,268 个文件里 11 个有改动) |
| `build-relay`(默认自包含 / `--framework-dependent` / `--aot`) | 自包含约 4 s、框架依赖约 1 s(清掉 `obj/Release` 后)、AOT 约 9 s(完整 ILC) | 三种形态都零 IL / 裁剪警告、自检 PASS:自包含 `ncw-relay` 启动到 `/healthz` 200 约 1.7 s(123.2 MiB)、框架依赖约 0.3 s(11.9 MiB)、AOT 约 0.7 s(11.5 MiB);`/metrics` 200、非 WebSocket 请求 426、关 stdin 与 SIGTERM 都在几十毫秒内以 0 退出。**自检抓出两处真实缺陷(已修)**:① `Program.cs` 的 `ProcessExit` 处理器在 `using var stop` 释放之后再 `Cancel()`,优雅退出时抛 `ObjectDisposedException`、进程以 134 退出;② SIGTERM 被 ASP.NET 的 `ConsoleLifetime` 接管(只触发 `ApplicationStopping`),主流程不跟着退出,进程要等 SIGKILL——改为 `RelayServer.Stopping` 触发主流程结束 |

**没有验证的部分**:公证(Developer ID 签名已实跑,见上表;`--notarize` 的命令序列只有单测,决定 3.0 首发先不公证——从浏览器下载的包第一次打开会被 Gatekeeper 拦,要在「系统设置 → 隐私与安全性」里放行)、正式签名的应用被 vpk 更新后的行为(§7.5 的演练用的是 ad-hoc 签名)、Windows 与 Linux(没有真机:`signtool`、Setup.exe、AppImage 只有命令构造的单测;`build-relay` 的自检在 Windows 上走的是关 stdin 这条路,SIGTERM 那条只在 macOS 上跑过)、`.pkg` 安装器签名。~~Velopack 的增量包与真正的自更新、已安装应用被 vpk 更新后 `Contents/MacOS/agent` 的行为~~:M7 已在 macOS 上演练(§7.5);仍没有验证的是 `.pkg` 安装器装出来的应用(演练用的是 portable 包解出来的 `.app`)。

### 7.1.1 CI 工作流(`.github/workflows/`;`ci.yml` 已在真实 GitHub Actions 上跑通,`release.yml` **没有运行过**)

> **`ci.yml`**:2026-11-17 起在真实的 GitHub Actions 上运行(PR #195):三平台每一步都通过过——构建零警告、测试、`publish-agent`(NativeAOT、零裁剪警告)、`verify --agent` 冒烟、`build-relay`(默认与 `--aot`)。
> 一共跑了 6 轮才三平台同时全绿(第 6 轮,8417 个测试;Linux 16 分钟、Windows 24 分钟、macOS 30 分钟)。
> **`release.yml` 仍然没有在真实 GitHub Actions 上运行过**:只做了静态检查(actionlint 通过、YAML 能被解析、没有任何密钥内容);`signtool` 的位置、AppImage 的 vpk 依赖、
> `security` 钥匙串步骤、Ed25519 清单签名的 secrets 接线都要在第一次真实运行时核对。

**第一次真实运行暴露的问题**(都只在 macOS 上开发时看不出来):

| 类别 | 现象 | 处理 |
| --- | --- | --- |
| 测试夹具假设 POSIX | Windows 首轮 349 个失败:把本机路径当 POSIX 路径、JSON 里拼反斜杠路径、`\r\n` 换行、SChannel 不接受 PEM 的临时私钥、往终端里敲 `$((…))` / `$$` / `rm -rf` | 夹具改成按平台取路径风格;落盘的 JSON 固定 `\n`;中继证书在 Windows 上转 PKCS#12;只能在 POSIX shell 下成立的用例在 Windows 上跳过并写明原因 |
| 界面测试不关窗口 | 每条截图用例留下一个窗口和它的 Skia 位图:Linux runner 内存耗尽失联、Windows 60 分钟跑不完 | `Ncw.UI.Tests/TestApp.cs` 的程序集级钩子每条用例后关窗、定期回收(钩子不能碰 `Dispatcher.UIThread`,原因写在那里) |
| 满载下才出现的时序假设 | 等待 5 秒不够、git 读操作超过 10 秒、假时钟在定时器挂上之前就被推进、挂在 `tool_end` 上的测试动作晚于下一个工具 | 逐个改成等条件而不是等时间 |
| 产品里的真实问题 | 改角色时取消一个已释放的会话令牌源(控制端收到 Internal error);迟到的旧快照把已清掉的流式文本带回镜像;Grep 的预算从尝试原生后端之前起算;单实例的转发管道在成为主实例之后才开始监听;写一个已断开的连接时抛出原始的 `IOException` | 均已修复;「迟到的旧快照」和「Grep 预算」各有一条修复前稳定失败的单测,其余几项没有单独的回归测试(竞态难以稳定触发) |

**还没有根治的偶发**(各出现过一次,只加了诊断信息,再出现时断言会带上现场):

- Windows:Bash 工具跑 `cat app.txt` 退出码 0 但没有输出(`Ncw.Agent.Tools.Tests` 的 `Read_then_Edit_then_Bash_through_the_real_loop`)。如果不是 runner 的问题,就是 Windows 上命令输出可能被丢掉,**值得优先查**。
- Windows:杀浏览器进程后 10 秒内没有报告退出(`RealBrowserTests`,出现过两轮);超时信息现在会区分「已退出但通知未到」和「仍在运行」。
- macOS:真实浏览器用例里 `browser_cua_click` 报错(整条用例 81 秒,runner 很慢);断言现在会带上工具的错误文本。
- Windows:`ConversationLinkTests` 的「重新生成」20 秒没等到新回复(本机加压 12 次复现不出);超时信息现在会带上当时的消息。

**Windows 上跳过的 294 个用例**(`Assert.SkipWhen/SkipUnless`,原因写在用例里):`Ncw.Agent.Environments.Tests` 119、`Hooks` 59、`Host` 42、`Terminal` 27、`Parity` 13、`Workspace` 13、`Storage` 6、`Integration` 5、
`Mcp` 3、`Extensions` 2、`Desktop` 2,以及 `Browser` / `Tools` / `UI` 各 1。它们依赖 POSIX shell、POSIX PTY、符号链接或 Unix 权限位;**这些路径在 Windows 上没有被验证**,「CI 绿」不覆盖它们。
ConPTY 本身(`WindowsConPtyTests`:起 cmd、回显、退出码、调整大小、杀进程)在 Windows runner 上通过。

两个文件,只用 .NET SDK(`global.json` 固定版本)和 `tools/Ncw.Build`,不装 Node / npm(AGENTS.md §6)。当前分支(`v3/avalonia`)上本来没有 `.github`;
**`main` 上有 2.x 的同名文件 `ci.yml` / `release.yml`(另有 `publish-packages.yml`)**,所以 3.0 合入 `main` 时这两个文件会冲突,必须有意识地处理(替换、或改名为 `v3-*.yml` 与 2.x 并存),不会悄悄覆盖。

- **`ci.yml`**(push / PR 到 `v3/avalonia`、手动):macOS / Windows / Linux 三个 runner 的矩阵(`osx-arm64` / `win-x64` / `linux-x64`;NativeAOT 不能跨操作系统编译,所以一平台一 runner):
  还原 → `dotnet build NextCoWork.slnx`(`Directory.Build.props` 里 `TreatWarningsAsErrors=true`,有警告就失败,不另加开关)→ `dotnet test`(trx)→ **上传 UI 截图**(`tests/Ncw.UI.Tests/bin/Debug/net10.0/screenshots/`,无论成败)
  → `publish-agent`(零裁剪警告,失败即红)→ 对发布出的 `ncw-agent` 做 `verify --agent` 冒烟 → `build-relay` 与 `build-relay --aot`(发布 + 产物自检)。Linux 上先装 `clang zlib1g-dev`(NativeAOT)和 `libfontconfig1`(Headless 截图的 Skia)。
- **`release.yml`**(`v3.*` tag 或手动;手动可选通道、`require_signing`、`mandatory`、`minimum_supported_version`、`release_notes`):`preflight`(读 `Directory.Build.props` 的版本,tag 必须是 `v<版本>`)→ 三平台矩阵按本节流水线顺序 `publish-agent` → `publish-desktop` → `bundle` → `verify` → (准备凭据)`sign` → 签名后再 `verify` → `velopack`,
  产物(`artifacts/velopack/<rid>/<channel>/`)与符号上传为 workflow artifact → 汇总任务 `manifest`(下载三个平台的包,逐个文件交给 `manifest`,有 Ed25519 密钥才签名,再 `manifest-verify`,清单与签名上传为 artifact)。
  **凭据只经 secrets / vars 的名字进入环境变量**(文件里没有任何值):macOS `NCW_MAC_CERT_P12_BASE64` + `NCW_MAC_CERT_PASSWORD`(导入临时钥匙串,结束后删除)、`NCW_MAC_SIGN_IDENTITY`、公证 `NCW_NOTARY_*`、`NCW_MAC_INSTALLER_IDENTITY`;
  Windows `NCW_WIN_CERT_PFX_BASE64` + `NCW_WIN_CERT_PASSWORD`、`vars.NCW_WIN_TIMESTAMP_URL`;清单 `NCW_RELEASE_SIGNING_KEY` / `NCW_RELEASE_SIGNING_KEY_ID`、`vars.NCW_RELEASE_PUBLIC_KEY`。
  **缺凭据就跳过并明说**:`sign` 自己打印 `SKIPPED signing: …`,`manifest` 打印 `NOT SIGNED`,工作流再写 `::warning::` 和 job 摘要(每个平台的签名状态、清单是否已签名)。
  与 §6 / 上面 `sign --require` 的关系:tag 触发**默认不加 `--require`**(secrets 还没配、流水线还没在真实 runner 上走通,硬要求只会让每次都红);手动触发勾选 `require_signing` 即加 `--require`。
  **secrets 配齐并跑通一次之后,应把 `REQUIRE_SIGNING` 的 tag 默认值改成 true**,正式发布不能悄悄发出未签名的包。第三方 action 目前按主版本号引用(`@v4`),生产使用前应固定到提交 SHA。

### 7.2 sidecar 守护(`Ncw.Desktop`)

在 M2 已有的 `LocalAgentSidecar` 上补全,逻辑只依赖可注入的抽象(`IProcessLauncher`、`IAgentHandshake`、`ISidecarPolicy`、`TimeProvider`),代码在 `src/Ncw.Desktop/Sidecar/`,单测在 `tests/Ncw.Desktop.Tests`(假进程 + 假握手 + 手动推进的假时钟,另有两个真实 `ncw-agent` 的冒烟)。

- **找到 sidecar**:`NCW_AGENT_PATH`,否则桌面可执行文件目录下的 `agent/ncw-agent[.exe]`;macOS 上先找 `agent/NextCoWork Agent.app/Contents/MacOS/NextCoWork Agent`(7.1 的布局)。找不到:状态 `Failed / AgentNotFound`,每 5 秒再找一次(开发时可能还在构建)。
- **就绪 = 握手成功**:本机通道 + 本机令牌 `hello` 成功才算就绪(不再看 stdout 的 ready 行);`WaitReadyAsync` 在 Agent 重启后换成新的信号,连接方不会拿到旧的「就绪」。拉起后最多等 30 秒。
- **启动参数**(只给桌面端用,不是面向用户的命令行):`--mode local --data-root <数据根>`;令牌只经环境变量 `NCW_LOCAL_TOKEN`。「关窗后继续运行」关闭时再加 `--lifeline stdin`(stdin 管道断开,即 EOF,Agent 就优雅退出;桌面端崩溃时管道也会断)和 `--parent-pid`(双保险),所以不会留下孤儿进程;打开时两者都不带,Agent 脱离桌面端继续运行。
- **崩溃重启与退避**:意外退出后等 1、2、4、8、16、30 秒(封顶)再重启;连续运行满 60 秒才算稳定、计数清零;连续 8 次失败后停下,状态 `Failed / CrashLoop`,等用户点重试(`Retry()`,同时可以跳过正在等待的退避)。拉不起来(权限、被安全软件拦截)和握手超时也走同一套退避。
- **拒绝启动(退出码 65 / 78):重启没用,不退避**。Agent 以 65(数据根里有加密数据却没有可用的主密钥,见 [02 A6.1](02-agent.md))或 78(启动配置不合法)退出,表示重启也不会好:守护**不进入重启退避、不重启 8 次**,直接停在 `Failed`(`RestartCount` 不增),原因取自 Agent 退出前打印的那一行 `ncw-agent startup-failed <key> <字段名>`(退出和最后一行输出是两个事件,守护等输出读完再判断;只收标识符字符,不信任输出):`agent.masterKey.missing` → `MasterKeyMissing`,`agent.masterKey.unreadable` → `MasterKeyUnreadable`,认不出的(没有这一行 / 没见过的 key)→ `LaunchFailed`。`Detail` 带退出码、key、字段名和数据目录,**没有任何密钥内容**。「重试」照旧可用(用户可能把 `credential.key` 放回去了),重试从头计数。风格和退出码 75 一致,没有新机制;区别是 75 先退避重试(对方可能马上退出),65 / 78 不会自己好,所以不退避。只有拉起的进程算数(接上的 Agent 没有可信的退出码);退出码不是 65 / 78 的(含崩溃的 134)仍按崩溃退避重启,不信它的输出。
- **版本规则**(SemVer 优先级,忽略 `+构建元数据`):sidecar 和客户端版本一致才直接用。① Agent **比桌面端旧**(典型:上次「关窗后继续运行」留下的旧 Agent,应用更新后还活着):让它退出(SIGTERM),换包里的新 Agent;如果新拉起的还是旧版本,说明安装包不完整,状态 `Failed / AgentOutdated`,不循环重启。② Agent **比桌面端新**,或 Agent 在握手时返回 `client_outdated`(协议 N / N-1 都不满足):状态 `Failed / ClientOutdated`,不动 Agent、不重试,提示用户更新 NextCoWork。看不懂的版本串不拦(协议握手本身还有一道检查)。
- **单实例**:同一数据根只允许一个 Agent。锁在 **Agent 里**:`ncw-agent` 启动时独占打开 `<数据根>/run/agent.lock`(崩溃、被杀时由操作系统释放,不会留下过期的锁),已被占用就以**退出码 75** 退出,不去抢端点(没有这把锁,第二个 Agent 的 `LocalListener` 会把第一个的 socket 文件删掉)。桌面端据此报 `AlreadyRunningElsewhere`(退避重试,8 次后停下);握手被拒(令牌不对,别的用户或别的程序占着)也是这个问题,并且绝不去终止它。
- **接上还在运行的 Agent**:桌面端拉起 Agent 时写 `<数据根>/run/sidecar.json`(pid、令牌、是否脱离、版本,0600)。下次启动先看它:pid 还活着、而且进程名确实是 `ncw-agent`(pid 会被复用,不能误杀别的程序)就直接握手接上,不重复拉起;进程已不在则删掉过期的状态文件。接上的 Agent 没有进程句柄,运行期间每 10 秒握手确认一次,连续两次失败就当它不在了。
- **退出**:自己拉起的 Agent:关 stdin → 等 5 秒 → SIGTERM(Unix;Windows 没有等价物,跳过)→ 等 3 秒 → 强杀;完成后删状态文件。**「关窗后继续运行」**打开(且这个 Agent 是脱离拉起的)时什么都不做,状态文件保留,下次接上。接上的 Agent 只有在它是「上次留下的」(脱离的)并且用户已经关掉开关时才由桌面端收尾;别的实例拉起的不动。开关是在 Agent 带着 stdin 管道运行期间才打开的:这个 Agent 本来就会随桌面端退出,所以仍然优雅关闭,开关对下一次拉起生效。
- **目录约定**:profile 根 `~/.next-cowork`(`NCW_PROFILE_ROOT` 可改),控制端文件在 `client/`;数据根仍是 M2 的 `~/.next-cowork/agent-dev`(`NCW_AGENT_DATA_ROOT` 可改)——2.x 数据接管(§4)由 Agent 里的 `DataGate` 在启动时执行,入口和行为**没有改动**,数据根切到 `data/` 的时机仍等接管验收。数据根下:`run/`(`agent.sock`、`agent.lock`、`sidecar.json`)、`logs/agent.log`(sidecar 的 stdout / stderr,2 MiB 滚动、保留一份旧的,给诊断页用)。
- **状态**:`LocalAgentSidecar.Status` / `StatusChanged`(`Phase`:Idle / Starting / Running / Restarting / Failed / Stopping / Stopped;`Issue`:AgentNotFound / LaunchFailed / AlreadyRunningElsewhere / AgentOutdated / ClientOutdated / CrashLoop / MasterKeyMissing / MasterKeyUnreadable;重启次数、下一次重启等待、诊断详情),另有 `Spawned` 事件(进程拉起;接上上次留下的 Agent 不触发)。`LocalBackend` 把状态写进跟踪日志,**并经 `Sidecar/StartupBridge` 写进启动事实**(`ClientStores.Startup`):启动加载界面据此显示「启动本机 Agent」阶段和各种失败的错误面板(文案按 `SidecarIssue` 对应的 key 取,Core / Client 不产出界面文字,见 [03 §10](03-ui-layout.md#10-启动与加载))。找不到 Agent 时的等待现在可被「重试」打断(以前要等满 5 秒)。

**偏离与待接入**:

1. **本机令牌不再是「每次启动随机、一次性」**(09 §5、02 A5.7 已同步):要接上脱离的 Agent,令牌必须跨桌面端会话保留,所以存在 `run/sidecar.json`(0600,和 socket 同一个信任边界:同一个用户)。Agent 自己仍然只从环境变量拿令牌;桌面端正常退出并停掉 Agent 时删除该文件,下次启动换新令牌。
2. **ncw-agent 新增两个启动参数 / 行为**(同样只给桌面端调用):`--lifeline stdin`,以及 SIGTERM 优雅关闭、忽略 SIGHUP(「关窗后继续运行」时终端关闭不能带走它)。
3. **「关窗后本机 Agent 继续运行」开关的值**目前来自 `Ncw.Desktop` 自己的 `desktop.json`(控制端目录下;`NCW_KEEP_AGENT=1` 可强制打开),通过 `ISidecarPolicy` 读取,默认关。设置页的开关(07 §3「通用」)、对应的 `UiPrefs` 字段和界面文字属于 UI 批次:接入时让 `UiPrefs` 实现 `ISidecarPolicy` 即可,守护逻辑不用改。
4. **托盘常驻**(2.x 的托盘行为)还没做:没有托盘时「关窗」就是退出应用,所以开关现在的语义是「退出桌面端后 Agent 继续运行」。

### 7.2.1 启动流程(进程启动 → 外壳可交互)

用户双击应用到能操作之间,桌面进程按下面的顺序走;**每一步都是一个真实的事实**,启动加载界面([03 §10](03-ui-layout.md#10-启动与加载))只显示这些事实,不用计时器假装进度。括号里是它在启动时间线里的里程碑名。

1. `Program.Main`:进程创建时间(`ProcessStarted`,操作系统记的)→ 进入 `Main`(`MainEntered`)→ Velopack 钩子 → 单实例锁拿到(`InstanceReady`)。Avalonia 起来之前的这三个时间经 `NcwApp.EarlyMilestones` 带进启动事实。(Avalonia / Arc 初始化完成是 `FrameworkReady`,窗口和外壳创建完是 `ShellCreated`,都只进时间线。)
2. `NcwApp.OnFrameworkInitializationCompleted`:开始跟踪启动(`ClientStores.Startup.Begin`)→ **先于后端** → `LocalBackend.Start`:拉起守护(`LocalAgentSidecar.Start`),守护的状态 / `Spawned` 事件经 `StartupBridge` 写进事实(`AgentSpawned`、`AgentReady`)→ `DeviceLink` 开始连接(`WaitReadyAsync` 之后才去连本机通道)→ 偏好读完、动效级别写进主题(`PlatformReady`)→ 创建主窗口(原生窗口底色在显示前设成加载界面的底色,见 03 §10.6)→ 窗口打开(`WindowShown`)→ 账户记录读完(`AccountDecided`)。
3. 连接层(`DeviceLink`):连接 → 握手 → 设备信息(`Connected`)→ 订阅各主题,首帧都到(`TopicsSynced`)→ `data` 主题 `ready`(`DataReady`;首次接管 2.x 数据时这里有 备份 → 升级 → 提交 → 凭据 四个真实步骤)→ 项目列表写进镜像(`ProjectsLoaded`)→ 每个项目的会话列表拉回来(`SessionsLoaded`)。
4. 视图:外壳在拿到数据后完成第一次布局(`ShellLaidOut`)→ 加载界面淡出并释放(`Interactive`)。

**实测的位置**(Release,macOS arm64,机器负载高,[03 §10.6](03-ui-layout.md#106-第一帧窗口底色与真窗口验证)):`FrameworkReady` ≈ 0.9 秒、`ShellCreated` ≈ 2.2 秒、`WindowShown` ≈ 2.5 秒(窗口显示之前用户什么也看不到)、`Interactive` ≈ 4.6 秒;无界面时 Agent 的拉起到可交互:热启动约 0.9 秒、冷启动约 1.3 秒、接上上次留下的 Agent 约 6 ms([03 §10.11](03-ui-layout.md#1011-实测时间线与阈值校准))。**评估过「在 `Main` 里提前启动守护」**:和框架初始化 / 窗口创建并行,A/B 没有收益(抢 CPU),没有采用。

**data 主题与加载界面**:首次启动时数据没就绪(接管中)的进度由加载界面显示(读的是和数据页同一个 `Data` 事实);数据被挡住 / 失败 / 接管完成要点「继续」时加载界面让位给数据页(03 §10.7)。**真实时间线数据**(冷启动 / 热启动 / 接上上次留下的 Agent / 真实 2.x 库接管)见 [03 §10.11](03-ui-layout.md#1011-实测时间线与阈值校准)。

### 7.3 `nextcowork://` 协议链接(桌面端,`Ncw.Desktop`)

登录回跳、扫码配对跳转都走自定义 scheme(07 §2)。桌面端的三件事:

1. **注册**:macOS 在 bundle 的 Info.plist 里(7.1,已有);Windows / Linux 在**运行时**由 `UrlSchemeRegistration.EnsureRegistered()` 尽力注册——Windows 写 `HKCU\Software\Classes\nextcowork`(`shell\open\command` = `"<exe>" "%1"`),Linux 写 `~/.local/share/applications/nextcowork-url-handler.desktop`(`MimeType=x-scheme-handler/nextcowork`,`NoDisplay=true`,`Exec` 按 Desktop Entry 规范转义)再 `xdg-mime default`;AppImage 用 `APPIMAGE` 指向的文件(挂载点每次不同)。`dotnet` 主机启动(开发)和 `NCW_REGISTER_URL_SCHEME=0` 不注册。Velopack 安装钩子(M7)接管之后,Windows 的写入改在安装 / 卸载钩子里(卸载时删),运行时注册退化为自检。
2. **单实例转发**:`SingleInstance`(07 §2「实现状态」)。启动参数里的 `nextcowork:` 链接和 macOS 的 OpenUri 激活都进 `NcwLinkInbox`(应用装配好链接路由之前排队,最多 16 条)。
3. **没有验证的部分(需要真机)**:Windows 注册表的真实生效和 Edge / Chrome 的外部协议确认框、Linux 各桌面环境的 `xdg-mime` 行为与 AppImage 首次运行、macOS 的 OpenUri 激活(Avalonia 12 的 `ProtocolActivatedEventArgs` 已接线,没有在打包后的 `.app` 上点链接实测)、「应用未运行时点链接」和「应用已运行时点链接」两条路径的三平台演练(05 §4)。命名管道 / 锁文件的转发本身有真实进程内的测试(`UrlProtocolTests`)。

### 7.4 自更新运行时(M7:`Ncw.Core` / `Ncw.Client/Updates` / `Ncw.Desktop/Updates`)

分层(客户端项目不引用 `Ncw.Agent.*`,共享库保持裁剪兼容;Velopack 只在桌面宿主里):

| 层 | 位置 | 内容 |
|---|---|---|
| 判定 | `Ncw.Core/Domain/UpdatePolicy.cs` | 纯函数:SemVer 优先级比较、`EnforcementOf`(None / Required / Blocking)、`CanStartNewRuns`、清单自洽检查。时钟由调用方传入;「关于」页和将来 Agent 侧共用(规则 3) |
| 状态机 | `Ncw.Client/Updates/UpdateService.cs`(`IUpdateService`) | 阶段 Disabled / Idle / Checking / UpToDate / Available / Downloading / Ready / Installing / Failed;单飞;单调;状态只由它写进 `ClientStores.Updates`(规则 1);时钟、引擎、宿主、记录文件全部可注入 |
| 清单与验签 | `Ncw.Client/Updates/ManifestVerifier.cs`、`ReleaseManifest.cs`、`UpdateFeed.cs` | 读 `release-manifest.json` + 分离签名,BouncyCastle 的 Ed25519 验签,`ReleaseKeyRing`(`kid → 公钥`,可轮换),feed 读取(本地目录 / HTTPS / 回环 HTTP) |
| 引擎 | `Ncw.Desktop/Updates/VelopackUpdateEngine.cs`、`SignedFeedSource.cs` | `UpdateManager`(`AllowVersionDowngrade = false`)+ 验签的 `IUpdateSource` 包装 |
| 宿主 | `Ncw.Desktop/Updates/DesktopUpdateHost.cs`、`Program.cs`、`NcwApp.UpdateServiceFactory` | 启动钩子、sidecar 收尾、应用退出(含兜底)、装配 |
| 动作与界面 | `Ncw.Client/Actions/UpdateActions.cs`、`ViewModels/AboutViewModel.cs`、`Ncw.UI/Views/Desktop/AboutSettingsPage.axaml` | 一个意图一个方法(检查 / 下载 / 重启并更新 / 换通道);ViewModel 只读 Store、只调 Actions |
| 仅开发 | `Ncw.Desktop/Updates/DevUpdateRehearsal.cs` | 无界面的更新演练,只认环境变量(§7.5);不是用户可见的入口 |

**启动**:`Program.Main` 的第一件事是 `VelopackApp.Build().Run()`(在单实例锁、协议链接注册、Avalonia 之前:Windows 上安装 / 更新 / 卸载的钩子进程不能去抢单实例或建窗口)。
已下载但还没装的更新在这时(没有窗口、sidecar 还没起)被应用并重启——这是最安全的时机,所以 `SetAutoApplyOnStartup` 保持默认开。Windows 的安装 / 更新后钩子里重写 `nextcowork://` 注册(`EnsureRegistered`,幂等);
**卸载钩子里删注册还没做**(没有 Windows 真机)。开发时直接 `dotnet run` 不是安装出来的应用,更新服务进入 `Disabled`。

**检查 → 下载 → 应用**(顺序都由 `UpdateService` 保证):

1. **检查**:后台第一次在启动 45 秒后,之后每 6 小时;手动随时。`VelopackUpdateEngine` 为通道新建 `UpdateManager`(`ExplicitChannel` = `stable` / `beta`),数据源是 `SignedFeedSource`:
   **先读清单与签名并验签,验不过 Velopack 根本拿不到 feed**。验过之后只放行清单里列出、sha256 一致的包(全量 / 增量),放行的包的 SHA256 换成清单里的值(Velopack 对增量重建出来的全量包做的校验也就是校验签过名的值);
   清单宣布的版本必须在放行的全量包里。服务层再比一次版本:清单里的版本不比当前新(feed 回滚)一律「已是最新」,**不降级**。
2. **下载**:Velopack 下载并校验;`SignedFeedSource` 下载完再算一遍 sha256 与清单比,不一致删文件、报 `ChecksumMismatch`。必需的更新(`mandatory`)发现后**自动下载**,可选的等用户点。
3. **立即重启并更新**:① `WaitExitThenApplyUpdates`——让更新器就位(它只等本进程退出 60 秒);失败就停在这里,应用和 sidecar 一点没动,状态回 Ready 并带 `ApplyFailed`;
   ② `LocalAgentSidecar.StopForUpdateAsync()`——ncw-agent 优雅收尾(关 stdin → SIGTERM → 强杀),**不管「关窗后继续运行」开没开**(Agent 的可执行文件马上要被替换);完成后守护不再重启它;
   ③ 请求应用退出(走应用自己的退出流程;更新器只等 60 秒,所以另有 20 秒的硬退出兜底,此时 sidecar 已经收尾)。更新器替换 `.app` 后重新拉起应用,新版本的 Agent 在启动时自己做数据库迁移(§4 步骤 6)。

**签名与信任根**:公钥(`kid:base64[,…]`,可轮换)**只在构建时**写进程序集元数据(`NcwReleasePublicKeys`;`publish-desktop` 从 `NCW_RELEASE_PUBLIC_KEYS` 传 `-p:NcwReleasePublicKeys=…`,逗号转义成 `%2C`),
运行时没有任何环境变量或配置能改它。没有内置公钥的构建(开发)拒绝一切清单(`NoTrustedKeys`),不是跳过验证。轮换:新版本里放新旧两把,确认用户都升过之后从后续构建里删旧的(删除即吊销)。
验证规则:信任列表为空 → 拒绝;缺签名 / 签名不是 64 字节 base64 → 拒绝;清单声明的 `keyId` 不在列表里 → `UnknownSigningKey`(声明了就只用那把,没声明逐把试);验签通过之后才解析并检查:`signing.status` 必须是 `signed`、schema = 1、产品名 = `NextCoWork`、
**通道必须和请求的一致**(防止把合法签名的 beta 清单放进 stable 的 feed)、版本号能读懂、策略自洽(`minimumSupportedVersion` 不能高于这个发布本身,否则装完仍被阻断)。已知限制:一份旧的、合法签过名的清单被重放(冻结攻击)会让用户停在旧版本,
但不能让它装比清单更新的东西,也不能降级。

**强制更新的语义**(`UpdatePolicy`,逐条对应 2.x 的 `canStartNewRuns`;清单里的策略在被签名的字节里,改不了):

| 情况 | 约束强度 |
|---|---|
| 没有待装更新,或更新不是 `mandatory` | `None` |
| `mandatory`,没有 `minimumSupportedVersion`(或看不懂) | `Required`(只有「必需」的标记,不阻断——2.x 同) |
| `mandatory`,当前版本 ≥ `minimumSupportedVersion` | `Required` |
| `mandatory`,当前版本 < 最低版本,`graceUntil` 晚于现在 | `Required`(宽限期内;界面显示截止时间) |
| `mandatory`,当前版本 < 最低版本,没有 `graceUntil` 或已到点(恰好等于算已到) | `Blocking`:界面阻断提示,`CanStartNewRuns` = false |

看不懂的版本串不拦。宽限期到点是时间流逝带来的、没有任何状态事件:`UpdateService` 在 `graceUntil` 到点时给状态加一个刻度(`Tick`),持有「按时钟派生」的视图据此重新读时钟。
**阻断目前只体现在「关于」页**(必需 / 阻断卡片 + 「立即重启并更新」);主界面的全局阻断条和 Agent 侧「拒绝新 Run」的接线是下一步(见上面 §3)。

**界面**(设置 · 本应用 · **关于**):当前版本、更新通道(稳定版 / 测试版)、检查更新、状态一句话、下载进度、更新说明、「下载更新」、「立即重启并更新」(本机还有任务在跑时提示会被中断)、必需 / 阻断卡片、**签名 / 清单类失败用单独的醒目卡片**
(「更新已被拒绝……没有下载也没有安装任何东西」,和普通的网络 / 下载失败区分开)、上次检查时间(跨会话保留在 `<控制端目录>/update.json`,和通道一起)。文字全部在 `NcwTextKind` + 中英 xlf;失败原因按 `UpdateFail<Kind>` 约定翻译(从 2.x `update-error.test.ts` 移植的覆盖测试防止缺 key)。

**后端**(B2 / B3。已在后端仓库 `Codex/CoWork` 实现:`src/CoWork.Api/Endpoints/ClientFeedV3.cs`、`ClientReleaseV3Endpoints.cs`,用例在 `tests/CoWork.AuthTests`;**还没部署**):
1. feed 按平台分目录:`/api/client/updates/v3/{channel}/{rid}/`,目录里是 vpk 的产物(`releases.<channel>.json`、全量 / 增量 `.nupkg`、安装器、portable)+ `release-manifest.json` + `release-manifest.json.sig`。
   后端不解释包的内容,只原样返回(支持 Range 续传、ETag = sha256、`Cache-Control: no-cache`);Velopack 请求 `releases.<channel>.json` 时带的查询串不影响结果。
2. **存储是「按 sha256 存内容 + 每个通道 × 平台一份快照」**,不进数据库(和 2.x 的 `client_release` 表完全分开):vpk 的安装器、portable、`releases.<channel>.json` 的文件名里没有版本号,
   每次发布都是同名的新内容,不能按文件名做「上传一次不可改」。一次发布 = 先 `PUT /v3/blobs/{sha256}`(后端边收边算哈希,对不上就丢弃;已有就直接返回,重跑不重传;`HEAD` 先问),
   再 `POST /v3/{channel}/{rid}/publish`(清单**原始字节**的 base64 + 分离签名),后端核对清单里每个文件都已上传、大小一致,再**原子地**换上新快照;换之前客户端看到的一直是上一份完整的快照。
   旧快照留在 `history/`;默认拒绝发布比当前低的版本(`allowOlder` 显式回滚)。
3. **后端不验 Ed25519 签名**(后端没有、也不需要发布公钥):它只检查 `signing.status = signed`、签名是 64 字节、schema / 产品 / 通道 / 平台一致、列出了 `releases.<channel>.json`。
   **验签在 `upload` 里做**:用和 `publish-desktop` 同一份信任列表 `NCW_RELEASE_PUBLIC_KEYS`(按清单的 keyId 取),验不过就不上传——否则发出去的是一份所有客户端都会拒绝、并显示成「签名有问题」的更新。
4. 清单由 `tools/Ncw.Build manifest --scan <velopack 输出目录>` 生成并签名,**清单里列出这个目录里的全部包**(含历史版本的全量包,增量包要靠它们);`url` 字段为 null(下载地址 = feed 目录 + 文件名)。
5. `mandatory` / `minimumSupportedVersion` / `graceUntil` 在清单里(被签名),**后端不靠请求头决定**。
6. `GET /v3/latest?channel=` 汇总各平台当前快照:版本、发布时间、更新说明、强制更新策略、给人下载的安装包(安装器 / portable / AppImage,不含 nupkg 与 feed 文件)和相对下载地址。官网下载页与 2.9 用它。
7. 公网只提供 HTTPS;客户端和 `upload` 都拒绝公网明文 HTTP(回环地址的 HTTP 只给开发)。
8. **已知窗口**:客户端分两次请求取清单和签名,恰好在两次之间换了快照,这一轮验签失败(下一轮检查恢复)。发布不频繁、两次请求相隔毫秒级,没有为它引入版本化 URL。
9. **端到端已在本机走通**(2026-11-17,macOS arm64):后端用 `CoWork.AuthTests --serve` 起在回环上 → `upload` 把 §7.5 演练的 feed(3.0.0 + 3.0.1,6 个文件、318 MiB)传上去并发布 →
   装好的 3.0.0 用 `NCW_UPDATE_FEED=http://127.0.0.1:5040/api/client/updates/v3/{channel}/{rid}/` 检查(43 ms)、下载增量包(157 ms)、交给更新器,3.0.1 起来后读回更新前写的数据,`report.json` 为 PASS。
   注意:Velopack 把下载好的包缓存在 `~/Library/Caches/velopack/NextCoWork/packages`,启动时会先装上已下载未安装的更新;重复演练前要把那里的旧包挪开,否则测不到下载。

### 7.5 自更新本机演练(macOS arm64,真实的 Velopack 更新器与真实的 `ncw-agent`)

**做法**:不需要有人点界面。`Ncw.Desktop/Updates/DevUpdateRehearsal.cs` 是应用内的无界面演练程序,**只认环境变量**(`NCW_DEV_UPDATE_REHEARSAL=<工作目录>`;没有任何命令行参数,用户环境里不会有它),
在 `VelopackApp.Run()` 之后、Avalonia 之前接管进程:阶段 1 在 3.0.0 里起 sidecar、握手、经协议写一条设置(`settings.update` → 个性化名字 = 随机标记)并读回,然后走**和界面完全相同的** `UpdateService`(检查 → 下载 → 重启并更新:更新器就位 → sidecar 收尾 → 退出);
Velopack 的更新器替换 `.app` 并重新拉起 3.0.1,阶段 2 在 3.0.1 里起 sidecar、握手(版本必须是 3.0.1)、读回那条设置、核对数据文件,写 `report.json`。`NCW_DEV_UPDATE_MODE=check` 只检查并报告(负面路径用)。

**准备**(全部用 `tools/Ncw.Build`,开发用密钥放在仓库之外):

```
dotnet run --project tools/Ncw.Build -- keygen --out <仓库外>/dev-release.key        # 打印公钥
export NCW_RELEASE_PUBLIC_KEYS="dev-1:<公钥>"  NCW_MAC_SIGN_IDENTITY=-  NCW_RELEASE_SIGNING_KEY_FILE=<仓库外>/dev-release.key  NCW_RELEASE_SIGNING_KEY_ID=dev-1
# 对 3.0.0、3.0.1 各做一遍(--artifacts 各用各的目录;feed 目录共用,3.0.1 加 --keep-output):
dotnet run --project tools/Ncw.Build -- publish-agent   --rid osx-arm64 --version 3.0.0 --artifacts artifacts/rehearsal/v3.0.0
dotnet run --project tools/Ncw.Build -- publish-desktop --rid osx-arm64 --version 3.0.0 --artifacts artifacts/rehearsal/v3.0.0
dotnet run --project tools/Ncw.Build -- bundle   --rid osx-arm64 --version 3.0.0 --artifacts artifacts/rehearsal/v3.0.0
dotnet run --project tools/Ncw.Build -- verify   --rid osx-arm64 --artifacts artifacts/rehearsal/v3.0.0
dotnet run --project tools/Ncw.Build -- sign     --rid osx-arm64 --artifacts artifacts/rehearsal/v3.0.0
dotnet run --project tools/Ncw.Build -- velopack --rid osx-arm64 --version 3.0.0 --channel stable --artifacts artifacts/rehearsal/v3.0.0 --out artifacts/rehearsal/feed [--keep-output] --release-notes <说明.md>
dotnet run --project tools/Ncw.Build -- manifest --scan artifacts/rehearsal/feed --rid osx-arm64 --version 3.0.0 --channel stable --out artifacts/rehearsal/feed
# 3.0.0 做完后:cp -R feed feed-3.0.0-only;ditto -xk feed/NextCoWork-stable-Portable.zip install   (装出 3.0.0)
```

**运行**(`install/` 是 3.0.0 的 `.app`;下面的目录都是临时的):
`NCW_DEV_UPDATE_REHEARSAL=$W/driver NCW_UPDATE_FEED=<feed 目录或 http://127.0.0.1:端口/> NCW_AGENT_DATA_ROOT=$W/data NCW_CLIENT_DIR=$W/client NCW_PROFILE_ROOT=$W/profile install/NextCoWork.app/Contents/MacOS/NextCoWork`。
把上面做成了**可选的自动化测试**(默认跳过):`NCW_REHEARSAL_ROOT=<准备好的目录> dotnet test tests/Ncw.Desktop.Tests --filter RealUpdateRehearsalTests`(`tests/Ncw.Desktop.Tests/Updates/RealUpdateRehearsalTests.cs`,约 9 秒,三步:更新 + 数据保留、篡改清单被拒、回滚不降级)。

**真实结果**(本机 macOS arm64、.NET 10.0.401、Release、`NCW_MAC_SIGN_IDENTITY=-` 的 ad-hoc 签名;耗时取自 `rehearsal.log`):

| 步骤 | 耗时 | 结果 |
|---|---|---|
| `publish-agent`(3.0.0 / 3.0.1) | 57 s / 70 s | 零裁剪警告(2 条白名单里的 clang `.pcm` 环境警告),`ncw-agent` 55.7 MiB |
| `publish-desktop`(内置公钥) | 12 s / 18 s | 公钥经 `-p:NcwReleasePublicKeys` 写进程序集元数据;开发构建没传时打印「自更新会拒绝一切清单」 |
| `verify` | 2 s | PASS:3.0.0 ready 1270 ms、3.0.1 ready 1077 ms,版本与包一致 |
| `velopack`(3.0.0 / 3.0.1 `--keep-output`) | 11 s / 11 s | 3.0.1 同时产出增量包 `NextCoWork-3.0.1-stable-delta.nupkg`(107 KiB;全量 94 MiB) |
| `manifest` | < 1 s | 6 个产物(含两个版本的全量包、增量包、`releases.stable.json`),Ed25519 签名 |
| **更新(本地目录 feed)3.0.0 → 3.0.1** | 阶段 1:启动到交接 1.3 s(sidecar 就绪 + 握手 0.9 s、检查 54 ms、下载 201 ms、sidecar 优雅收尾 41 ms、交给更新器 72 ms);更新器替换 `.app` 约 1.4 s;阶段 2:新版本 sidecar 就绪 549 ms;从开始到 PASS 共约 4.2 s | **成功**:`sq.version` 变成 3.0.1;新拉起的 ncw-agent 报 3.0.1(与桌面版本一致);更新前写的设置(`rehearsal-b77eadd3`)读回一致,并直接用 `sqlite3` 在库里查到 `settings` 表 `{"personalization":{"name":"rehearsal-b77eadd3"}}`;数据根(库、`credential.key`、`device.json`)都在,迁移表最大版本 39;没有残留 `ncw-agent` 进程 |
| **更新(本地 HTTP feed + 增量包)** | 检查 42 ms、下载 3.0 s(只取了 107 KiB 的增量包,Velopack 在本地重建出 94 MiB 的全量包,清单被读了 2 次、都验过签)、sidecar 就绪 302 ms | **成功**(HTTP 请求只有 `releases.stable.json`、清单、签名、增量包) |
| 负面:**清单被篡改**(把 `mandatory` 改成 `true`,签名没变) | 17 ms | **拒绝**:`SignatureInvalid`,没下载任何东西(Velopack 的包目录都没创建),版本仍是 3.0.0 |
| 负面:**feed 回滚**(feed 里只有 3.0.0,应用是 3.0.1;签名完全合法) | 29 ms | **不更新**:`UpToDate`,不给出任何更新;Velopack 与服务层各自都不降级 |
| 负面:**服务器上的包在签名之后被换了**(全量包末尾多一个字节) | 60 ms | **拒绝**:`ChecksumMismatch`,文件被删,应用留在 3.0.0、sidecar 正常收尾 |

**演练抓到的真实缺陷**(都已修,有单测):
1. **Velopack 的校验和比较区分大小写**:它算出来的是大写十六进制,`tools/Ncw.Build` 写的清单是小写,第一次真实运行时每次下载都报 `ChecksumMismatch`(单测用大写造数据所以没发现)。现在 `SignedFeedSource` 统一成大写再交给 Velopack(`The_lowercase_hex_the_build_tool_writes…`、引擎测试里清单改用小写)。
2. 没跑过 `VelopackApp.Run()`(没有定位器)时 `UpdateManager` 会抛 `InvalidOperationException`:`IsInstalled` / `CurrentVersion` 现在兜住(引擎在任何启动钩子之前 / 之外构造都不抛)。
3. `vpk --release-notes` 要的是**文件路径**,不是文本(工具链用法)。

**观察到的行为(写下来是为了以后别踩)**:更新器用 `open -n <.app>` 重新拉起应用,这次演练里原进程设的环境变量在新进程里仍然可见(所以阶段 2 能接着跑)——**生产逻辑不能依赖这一点**;更新器继承了我们的 stdout,日志在 `~/Library/Logs/velopack_NextCoWork.log`;下载的包在 `~/Library/Caches/velopack/NextCoWork/packages`(和同一台机器上别的运行共用,演练前要清);
更新器的「等原进程退出」会报 `No such process … Continuing`(我们在它开始等之前就已经退出了,无害);更新器只等 60 秒,所以应用的退出要有兜底。

**没有验证的部分**:Windows 与 Linux(没有真机:`Setup.exe` 的静默更新、`%LocalAppData%` 下的安装位置、AppImage 的替换、Windows 钩子里的协议注册、卸载钩子);Developer ID 签名与公证(演练用 ad-hoc 签名;Velopack 在 macOS 上对更新后的 `.app` 做 Gatekeeper 检查会怎样,换成正式签名后要重做一遍);`.pkg` 安装器装出来的应用(演练用的是 portable 包解出来的 `.app`);
部署后的真实后端 feed(B2 / B3 已实现、本机端到端走通,但还没部署到 nextco.work);2.8 → 2.9 → 3.0 的桥接(2.9 在 `main` 上,不在本分支);「更新前有任务在运行」的真实收尾(演练里没有跑中的 Run;Agent 对被中断 Run 的修复在 Agent 启动时,有 Agent 侧测试,没在更新场景里串起来);数据库迁移随更新执行(3.0.0 与 3.0.1 之间没有新迁移,迁移在 Agent 启动时做的这条路径没被更新场景真正走到);
界面上点按钮(演练走的是同一个 `UpdateService`,界面层由 Headless 测试覆盖)。

