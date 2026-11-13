# 04 · 更新与安装

目标:**已经装了 2.x 的用户,通过自动更新收到 3.0 并完成安装,数据原地保留。**
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
| 数据 | Electron userData 下的 SQLite(`node:sqlite`)。凭据大部分已经是 `NCK1` 自管加密,少量旧行可能仍是 safeStorage 密文 |
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
2. **用户确认**:显示一张「升级到 3.0」卡片,列出新特性、变化和回退方式。用户可以推迟,
   直到服务端标记为 mandatory 并超过宽限期。
3. **整理数据**(必须在关闭 Electron 之前完成):
   - 停掉所有 Run 和子代理,等收尾完成。
   - 把残留的 safeStorage 凭据转成 `NCK1` 格式。3.0 的 Agent 不在 Electron 里运行,解不开 safeStorage。
   - 把数据库迁移到 2.x 的最终 schema,做一次一致性备份(`backup()` 在线备份)。
   - 写交接文件 `<数据根>/handoff-v3.json`,记录数据根路径、2.x 版本、schema 版本、备份位置和设备配对信息的位置。
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
- 桥接版能在 Electron 还活着的时候做完数据整理。这是 3.0 能「原地接管数据」的前提。

## 3. 3.x 的自更新(Velopack)

- 产物:Windows `Setup.exe` + `.nupkg`(全量和增量);macOS `.pkg` 或 `.zip`(要求 Developer ID 签名和公证);
  Linux AppImage。
- Feed:Velopack 的 `releases.{channel}.json` 是静态文件。nextco.work 新增 `/api/client/updates/v3/{channel}/` 来提供,
  通道沿用 `stable` / `beta`。
- 发布脚本改用 `vpk pack` 和 `vpk upload`,或者在 `upload-client-release` 上加一个 v3 模式,沿用同一个上传 token
  和同一组元数据(mandatory、minimumSupportedVersion、graceUntil、更新说明 base64)。
- 强制更新的语义保持不变。`canStartNewRuns` 的判定改到 Agent 侧,由控制端上报版本,
  Agent 拒绝来自过低版本控制端的新 Run。
- **sidecar 和客户端一起更新**:内置的 ncw-agent 和 Node 运行时都打进同一个 Velopack 包,版本号和客户端一致。
  更新前,桌面端先让 sidecar 收尾,再执行重启安装(沿用 2.x「先走应用自己的退出流程,再安装」的经验)。
- **单独部署的设备服务**通过 npm 或它自己的更新器升级,和客户端版本不必同步,靠协议 N/N-1 兼容。

### 移动端分发

- iOS:App Store 加 TestFlight(beta 通道)。
- Android:Google Play,另外在 nextco.work 提供 APK 直装(在 App 内检查更新,跳转系统安装器)。
- 移动端也要遵守最低支持版本:Agent 在握手时拒绝过低版本的控制端,并返回「请更新 App」的错误码。

## 4. 数据接管

3.0 首次启动的流程:

1. 读取 `handoff-v3.json`。没有这个文件就是全新安装,创建新的数据根。
2. 用 2.x 已有的 `takeover begin` 和 `takeover commit` 接管 Electron 数据根:查旧进程、一致性备份、
   逐表核对行数、取锁、写所有权标记。数据**原地使用,不复制**。
3. 执行 3.0 的数据库迁移(新增项目组布局、Profile、推送注册等表)。
4. 从账户的配置同步里拉取 `RailLayout`。没有的话,根据 2.x 的工作区列表生成默认布局,所有项目都不分组。
5. 弹出欢迎页:「已从 2.x 迁移 N 个项目、M 个会话」。

## 5. 回滚

- 在 `takeover commit` 之前,任何失败都可以无损回退。之后需要 `rollback --acknowledge-data-loss`,
  它会先把接管后写入的库另存一份。
- 2.9 的安装包继续在 nextco.work 上保留 6 个月。「设置 → 关于」里提供「回退到 2.x」的说明。
- 3.0 的数据库迁移要么完全可逆,要么在迁移前备份,备份保留 30 天。

## 6. 签名(3.0 强烈建议补齐)

| 平台 | 建议 | 原因 |
|---|---|---|
| macOS | Developer ID 签名加公证 | 不签名的话,Gatekeeper 会拦截首次打开。Velopack 在 macOS 上也依赖签名 |
| Windows | Authenticode(EV 或 OV) | 减少 SmartScreen 警告 |
| 所有平台 | 发布清单用 Ed25519 签名 | 不依赖平台签名,也能保证 2.9 拿到的 3.0 产物没被篡改 |
