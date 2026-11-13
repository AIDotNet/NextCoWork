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

## 2. 升级路径:经过桥接版本 2.9

```
  2.x(<2.9)──electron-updater──▶ 2.9 桥接版 ──自管安装──▶ 3.0(Avalonia + 内置 ncw-agent)──Velopack──▶ 3.x
```

**服务端门控**:feed 根据请求里的客户端版本决定下发什么。低于 2.9 的客户端只能拿到 2.9,不会直接拿到 3.0。
2.9 通过新端点 `/api/client/updates/v3/manifest` 获取 3.0 的信息。
→ 需要后端配合(见待定问题 Q5)。

### 2.9 桥接版做的事

1. **预检**:磁盘空间、操作系统版本是否满足 3.0 的最低要求(.NET 10 和 Avalonia 12 的平台下限)、
   数据根是否可写。不满足的话留在 2.9,并说明原因。
   如果用户配对过 2.x 的独立设备服务,要明确提示:3.0 连不上它们(3.0 不做独立设备服务)。用户可以选择暂不升级,
   留在 2.9(服务端标记为 mandatory 并超过宽限期时除外),也可以改用云端 Agent。
2. **用户确认**:显示一张「升级到 3.0」卡片,列出新特性、变化和回退方式。用户可以推迟,
   直到服务端标记为 mandatory 并超过宽限期。
3. **整理数据**(必须在关闭 Electron 之前完成):
   - 停掉所有 Run 和子代理,等收尾完成。
   - 把残留的 safeStorage 凭据转成 `NCK1` 格式(尽力而为;3.0 自己也能解,见 §4)。
   - 把数据库迁移到 2.x 的最终 schema,做一次一致性备份(`backup()` 在线备份)。
   - 写交接文件 `<数据根>/handoff-v3.json`,记录 2.x 版本、schema 版本和备份位置。这个文件只是加速和留痕,
     3.0 没有它也能接管。
4. **下载并校验 3.0**:按平台下载对应的产物,校验 sha256,再用 2.9 内置的发布公钥(Ed25519)校验清单签名。
   这样即使 2.x 没有代码签名,也能防止安装包被篡改。
5. **安装**(由 2.9 自己完成,不经过 electron-updater):
   - **Windows**:静默运行 3.0 的 Velopack `Setup.exe`(安装到 `%LocalAppData%\NextCoWork`),然后退出 2.9。
     3.0 首次启动时,从注册表的 Uninstall 键找到 2.x 的 NSIS 卸载程序,以「保留用户数据」的方式静默卸载。
     开始菜单和桌面快捷方式换成新的。
   - **macOS**:下载 3.0 的 `.app.zip` 并解压到临时目录,校验签名。2.9 退出后,由一个独立的 helper 脚本
     把 `/Applications/NextCoWork.app` 原子替换为新的 bundle,然后启动 3.0。
     bundle id 保持 `com.nextcowork.app`,通知、登录项和钥匙串条目都能延续。
   - **Linux**:下载 3.0 的 AppImage,替换 `$APPIMAGE` 指向的文件,然后重启。
6. **失败处理**:任何一步失败,都停在 2.9 并保持可用。错误信息给出可以复制的诊断内容,并提供手动下载链接。

### 为什么不直接把 3.0 放进 electron-updater 的 feed

- Windows 上,electron-updater 会以 `/S --updated` 参数运行 NSIS 安装包。3.0 的安装器虽然可以配合这组参数,
  但在交接之前没有机会整理数据。
- macOS 上,Squirrel.Mac 会用当前 App 的 designated requirement 去校验新 bundle。ad-hoc 签名的
  designated requirement 绑定的是 cdhash,换成另一个程序的 bundle,结果没法保证。
- 桥接版能在 Electron 还活着的时候先停掉 Run、做好备份,接管更稳。但它不是接管的前提,3.0 能直接接管(§4)。

## 3. 3.x 的自更新(Velopack)

- 产物:Windows `Setup.exe` + `.nupkg`(全量和增量);macOS `.pkg` 或 `.zip`(要求 Developer ID 签名和公证);
  Linux AppImage。
- Feed:Velopack 的 `releases.{channel}.json` 是静态文件。nextco.work 新增 `/api/client/updates/v3/{channel}/` 来提供,
  通道沿用 `stable` / `beta`。
- 发布工具用 C# 重写(`tools/Ncw.Release`),打包调用 Velopack 的 `vpk`,沿用 2.x 的上传 token
  和同一组元数据(mandatory、minimumSupportedVersion、graceUntil、更新说明 base64)。
- 强制更新的语义保持不变。`canStartNewRuns` 的判定改到 Agent 侧,由控制端上报版本,
  Agent 拒绝来自过低版本控制端的新 Run。
- **sidecar 和客户端一起更新**:内置的 `ncw-agent`(NativeAOT 可执行文件 + 同目录的 `libe_sqlite3` 原生库)打进同一个 Velopack 包,版本号和客户端一致。
  更新前,桌面端先让 sidecar 收尾,再执行重启安装(沿用 2.x「先走应用自己的退出流程,再安装」的经验)。
- **云端 Agent** 由编排服务滚动升级,和客户端版本不必同步,靠协议 N/N-1 兼容。
- 3.0 不做独立设备服务,也就没有单独的服务端更新链路。

### 移动端分发

- iOS:App Store 加 TestFlight(beta 通道)。
- Android:Google Play,另外在 nextco.work 提供 APK 直装(在 App 内检查更新,跳转系统安装器)。
- 移动端也要遵守最低支持版本:Agent 在握手时拒绝过低版本的控制端,并返回「请更新 App」的错误码。

## 4. 数据接管(直接接管 2.x 数据)

**任何安装方式都能接管**:经 2.9 自动升级、从官网手动下载 3.0、测试版,首次启动都走同一套流程。
`handoff-v3.json` 有就读取(拿备份位置和版本),没有也照样接管。

### 发现

1. 按 2.x 的规则找 profile 根:默认 `~/.next-cowork`,数据根是其下的 `data/`。还要检查 Electron 默认 userData
   (`%APPDATA%\NextCoWork`、`~/Library/Application Support/NextCoWork`)里有没有 2.x 还没迁走的旧数据。
2. 找到 2.x 数据,就进入接管页;找不到,就是全新安装,直接创建新的数据根。

### 接管步骤(启动页上显示进度,没有命令行入口)

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
- 迁移版本号和 2.x 共用一个序列(1–37 是 2.x 的,38 起是 3.0 的),所以**桥接版 2.9 不能再新增数据库迁移**。

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
- 接管提交之后的回退在「设置 → 数据 → 回退到 2.x」里操作:先把接管后写入的库另存一份,再恢复接管前的备份,
  然后引导用户下载 2.9。全程在图形界面里完成。
- 2.9 的安装包继续保留在 nextco.work 上(2.x 维护期暂不规划,先不设下线时间)。「设置 → 数据」里提供「回退到 2.x」的入口和说明。
- 3.0 的数据库迁移要么完全可逆,要么在迁移前备份,备份保留 30 天。

## 6. 签名(证书已具备,3.0 起启用)

| 平台 | 做法 | 原因 |
|---|---|---|
| macOS | 3.0 起用 Developer ID 签名,开启 Hardened Runtime,并公证(notarytool + staple)。`ncw-agent` 和内置的原生库(e_sqlite3 等)都要签名 | Gatekeeper 不再拦截首次打开;Velopack 在 macOS 上依赖签名 |
| Windows | 3.0 起用 Authenticode 签名:`Setup.exe`、主程序、`ncw-agent.exe` 和原生库 | 减少 SmartScreen 警告 |
| 所有平台 | 发布清单另外用 Ed25519 签名 | 不依赖平台签名,也能保证 2.9 拿到的 3.0 产物没被篡改 |

**2.9 桥接版自己仍然保持 ad-hoc 签名**:2.8 → 2.9 这一跳走的还是 electron-updater 和 Squirrel.Mac,
签名方式一变,就可能过不了旧版本的 designated requirement 校验。换签名身份只发生在 2.9 → 3.0 这一跳,
而这一跳由 2.9 自己的 helper 安装,不经过 Squirrel。
