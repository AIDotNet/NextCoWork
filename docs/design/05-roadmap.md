# 05 · 路线图与风险

## 1. 里程碑

| 里程碑 | 内容 | 验收标准 |
|---|---|---|
| **M0 设计评审** | 本目录全部文档 | §5 的待定问题都有结论 |
| **M1 骨架** | 解决方案、集中包管理、接入 Arc、`Ncw.UI.Controls` 薄封装、三栏 Shell 加 mock 数据、主题和中英文、项目栏和分组的交互、首次启动和设置页的界面(mock 数据) | 三个桌面平台都能跑起来;Avalonia.Headless 截图测试覆盖四个断点 |
| **M2 协议与 Agent 骨架** | `Ncw.Protocol`(全新设计:本机和局域网承载、TLS 双向认证、远程开关、配对码 + SRP-6a、扫码配对、会话与续连、方法路由、订阅);BouncyCastle TLS 1.3 双向认证和 SRP 的 AOT spike(已完成);`Ncw.Agent.Storage`(兼容 2.x schema、NCK1、直接接管、解密遗留的 safeStorage);SSH.NET 的 AOT spike;`Ncw.Agent.Host` 骨架;从 2.x 录制 Agent 行为回放素材 | 协议黄金向量冻结并通过;打开远程开关后,局域网里另一台电脑用 `IP:端口` + 配对码连上,并通过中间人测试;C# Agent 接管三个平台上真实的 2.x 数据根(包括 safeStorage 凭据)并列出全部会话;3.0 客户端连上 C# Agent |
| **M3 Agent 内核** | Llm 模型层、Loop 回合循环(先用假上游和假工具单独验收)、权限与审批、交互闸门、执行环境(local + SSH)、Tools、终端(PTY,本机和 SSH)、子代理、压缩、计划、待办、目标;回放对照测试 | 回放对照测试全部通过;2.x 状态相关单测已移植并通过;`docs/parity.md` 中标为 3.0 的模块全部完成 |
| **M4 核心功能** | 客户端:会话和流式消息、交互、子代理(按状态规则来)、终端、文件、Git 和 Diff、模型和供应商、MCP、Skills、插件、定时任务、设置;Agent:Skills、Hooks、MCP、插件(Jaunt 宿主)、定时任务、开放网关、模型目录(models.dev 自动更新);客户端:登录(`nextcowork://`)、首次启动、设置页、SSH 环境管理;sidecar 打包和守护;`tools/` 下的 C# 工具链 | 功能对齐矩阵里标「3.0」的条目全部完成;全新安装开箱可用 |
| **M5 移动端** | iOS 和 Android 宿主、`MobileShell`、扫码配对、活动收件箱、推送、附件上传 | 真机上走通「收到审批推送 → 打开 → 长按允许 → Run 继续」 |
| **M6 云端 Agent** | Agent 侧:账户签发身份、休眠与唤醒、KMS 密钥、配额。后端:编排服务、沙箱镜像、持久卷、配额、**服务器中继 `Ncw.Relay`**(局域网外的手机也靠它,见 09 §9)。客户端:「创建云端 Agent」引导、唤醒状态、用量显示、Git 接力 | 只装了手机 App 的新用户能完成「注册 → 创建云端 Agent → 克隆仓库 → 发消息 → 收到完成推送」;休眠后唤醒,会话和未完成的状态都不丢 |
| **M7 升级链路** | 2.9 桥接版(在 `main` 上开发)、Velopack、后端 v3 feed 和清单签名、3.x 自更新、各平台正式签名和公证 | 三个桌面平台都完成「2.8 → 2.9 → 3.0 → 3.0.1」和「2.8 → 手动安装 3.0」两条路径的演练,数据完整 |
| **M8 扩展对齐** | 内联可视化、内置浏览器、Office 文档(用 WebView 承载) | 按矩阵分到 3.0 或 3.1 |
| **Beta → GA** | beta 通道灰度,收集崩溃和指标 | 崩溃率和升级成功率达标 |

**M6 进度(中继与云端 Agent 的 Agent 侧已做,后端与客户端引导未做)**:
- **中继**:`Ncw.Relay`(Kestrel + WebSocket,无状态哑管道、设备挑战签名注册、路由令牌哈希、限流 / 背压 / 空闲 / 带宽、指标)、`Ncw.Protocol` 的 `RelayTransport` / `RelayRegistration`(控制端 / 设备端承载)、控制端「局域网优先」的并行竞速、吊销同步作废中继令牌、设置页的中继地址;线上格式冻结(`GoldenRelay`),L1 以上没有任何改动。详见 [09 §9](09-protocol.md#9-中继m6已实现ncwrelay--relaytransport)。
- **云端 Agent 的 Agent 侧**(`ncw-agent --mode cloud`,设计与确定方案见 [02 A6.1](02-agent.md#a61-云端-agent-的-agent-侧实现m6)):无头启动与配置校验(环境注入、结构化失败退出码)、设备身份注册与编排服务签发的身份绑定、**编排控制通道**(签名指令:信任下发 / 吊销 / 整体替换、配额、休眠、升级、令牌轮换,Ed25519 验签 + 序号防重放 + 有效期,类型在 `Ncw.Protocol/Orchestration`,契约见 [09 §13](09-protocol.md#13-编排控制通道m6agent--编排服务))、KMS 包裹主密钥(`IKeyWrap` 接口与适配点,NCK1 不变)、空闲检测与休眠收尾(Run / Shell / 终端 / 浏览器 / WAL 检查点 / 中继注销 / 通知)、唤醒恢复、配额(Run 发起前检查,`quota.exceeded`)与幂等的用量上报、Git 接力(`projects.add` 克隆 + `git.credentials.*`)、出网策略钩子(Git 已接线)、`cloud.getStatus`。用假编排服务 / 假 KMS / 进程内中继 / 真实 AgentHost 端到端测试(`Ncw.Agent.Cloud.Tests`),`ncw-agent` NativeAOT 发布零裁剪警告并用发布出来的二进制跑过冒烟。
- **还没做**:编排服务、沙箱镜像、持久卷与快照、真实 KMS 适配、账户 API(B6–B9、B12,假设与待对齐项见 [§6.1](#61-编排服务接口契约与待对齐的假设m6));客户端的「创建云端 Agent」引导、唤醒状态、用量显示、Git 接力界面;「控制端连接 → 唤醒沙箱」的触发链路;出网钩子在 WebFetch / 浏览器 / MCP / SSH / 插件出口的接线;SSH 部署密钥;云端数据导出;中继的多实例部署与容量验证。

**M5 进度(共享层 + Android 宿主已做并在模拟器上跑通,推送 / 真机 / iOS 未做)**:共享层:`MobileShell` + `AdaptiveShell`(按宽度切换)、活动收件箱(Core 纯函数 `ActivityInboxView` + `InboxViewModel`)、设备页、扫码配对状态机、
推送令牌登记(协议 `push.register`、Agent 存令牌、`IPushRegistration` 抽象,新增「未配置」状态)、附件选择与上传进度、审批「长按允许」、设置导航栈,均在 Avalonia.Headless 下验证,详见 [03 §7](03-ui-layout.md#7-移动端m5-共享层实现说明)。
**Android 宿主**(`src/Ncw.Mobile.Android` + 平台无关的 `src/Ncw.Mobile.Shared`,`NextCoWork.Mobile.slnx`,详见 [11](11-mobile-hosts.md)):Keystore 封存的控制端身份、Camera2 + ZXing.Net 扫码(软件预览)、SAF / 拍照附件选择、FCM(没有 Firebase 配置时报「未配置」)、`nextcowork://` 深链、
后台 / 网络切换的连接策略;**在本机 Android 模拟器上真实走通**「深链或虚拟相机扫码配对 → 收件箱出现审批卡片 → 长按允许 → Run 继续」(脚本化冒烟 `tools/Ncw.MobileSmoke`,33 秒,默认不跑)。
**还没做 / 没验证**:推送整段(没有 Firebase 配置与后端推送中继 B4)、真机验收「收到审批推送 → 打开 → 长按允许 → Run 继续」、Android 真机手势 / 软键盘 / 相机、Release 构建的验证、iOS 宿主(`Ncw.Mobile.iOS`,本机没有验证条件)。

### 当前进度(以 `docs/parity.md` 为准;只写能证实的,「没在真机 / 真实后端上验证」的地方一律写明)

本节是对上面里程碑表的**状态标注**,不改验收标准。「完成」= 有单测或集成测试通过;「已验证」只指在这台 macOS arm64 开发机上跑过。
没有任何一个里程碑的**验收标准**已经满足(验收都要求真机、真实后端或回放对照),下面逐项写完成度与缺口。

| 里程碑 | 完成度 | 已完成(有测试) | 未完成 / 无法在本机验证 |
|---|---|---|---|
| **M0 设计评审** | 完成 | `docs/design/` 全部文档;§5 的待定问题已有结论 | — |
| **M1 骨架** | 完成 | 解决方案、集中包管理、Arc 接入、`Ncw.UI.Controls` 封装、三栏 Shell、主题与中英文、项目栏与分组、首次启动与设置页;Headless 截图覆盖断点 | 「三个桌面平台都能跑起来」只在 macOS 上验证;Windows / Linux 没有真机 |
| **M2 协议与 Agent 骨架** | 基本完成 | `Ncw.Protocol`(黄金向量冻结、本机与局域网承载、TLS 1.3 双向认证、SRP-6a 配对、扫码配对、会话与续连、方法路由、订阅);`Ncw.Agent.Storage`(2.x schema、NCK1、接管、safeStorage 转换);SSH.NET 与 BouncyCastle 的 AOT spike;`Ncw.Agent.Host`;控制端连真实 Agent(`Ncw.Integration.Tests`) | **2.x 回放对照测试**(「从 2.x 录制 Agent 行为回放素材」及对照)未完成,由另一条线进行中;三平台上真实 2.x 数据根与 safeStorage 凭据的接管只在 macOS 上验证过(Windows DPAPI、Linux 的 v10 / v11 没有真机);「局域网里另一台电脑用 `IP:端口` + 配对码连上」只用进程内与回环测过,没有两台真机 |
| **M3 Agent 内核** | Agent 侧主体完成 | Llm 模型层、Loop、权限与审批、交互闸门、执行环境(local + SSH,SSH 用户态 sshd 夹具)、Tools、终端(PTY)、子代理、压缩、计划、待办、目标、编排层(`Ncw.Agent.Runs`,含看门狗放弃后不再写库);2.x 的状态相关单测按 `docs/parity.md` 逐条移植 | **回放对照测试**已建立并通过(`Ncw.Agent.Parity.Tests`,71 个场景,覆盖循环 / 工具 / 审批 / 中止 / 插话 / 孤儿修复 / 压缩 / 待办补报 / 写入围栏 / 断流续传与重试 / 跨模型切换,以及整机回放的子代理派发(同步 / 后台报告 / 并发上限排队 / 父中止级联 / 深度 / 拒绝 / 错误)、计划模式(三种审批 + 执行计划)、目标(自提 / 批准 / 拒绝 / 禁用 / 判定续跑 / 空转刹车)、审批规则落盘(allow_always / deny / ask / allow)、Hooks 的 PreToolUse 阻断,见 `docs/parity.md`「回放对照」);插件、MCP、AI 审核器、定时任务、Hooks 的其余事件等还没有录制场景,验收标准的「全部通过」以此为界;parity 中仍有「部分」项(见 `docs/parity.md` 各行的「未完成」:如供应商登录流程里的各家 issuers、子代理回执里没有子会话 id、自动标题、`LlmRouter` 的 `IImageResolver` 接线);`--mode cloud` 不属于本里程碑 |
| **M4 核心功能** | Agent 侧与桌面界面主体完成,发布工具链在 macOS 上跑通 | Skills、Hooks、MCP、插件宿主(Jaunt)、定时任务、开放网关、模型目录;客户端的会话 / 交互 / 子代理 / 终端 / 文件 / Git / 模型与供应商 / MCP / Skills / 插件 / 定时任务 / 设置 / 首次启动 / SSH 环境管理界面;sidecar 打包与守护;`tools/Ncw.Build`(`publish-agent` / `publish-desktop` / `bundle` / `verify` / `sign` / `velopack` / `manifest` / `build-relay`);`.github/workflows/ci.yml` 与 `release.yml` 已写 | 登录(`nextcowork://` 回跳)需要真实后端,只测了客户端一侧;**CI 工作流没有在真实 GitHub Actions 上运行过**;Windows / Linux 的打包、签名、`nextcowork://` 注册、PTY 没有真机;MCP 的 OAuth、`agents` 的「AI 生成」等小项未做(托盘常驻、系统通知 / 角标、快捷键与命令面板等可用性改进已做,见 [03 §11](03-ui-layout.md#11-可用性改进2026-11以用户视角补齐的一批);左侧的「项目栏 + 项目面板」两栏已换成一栏项目树侧边栏(03 §1);系统通知只在打包好的 macOS .app 里有,Windows / Linux 未验证);「全新安装开箱可用」没有在干净机器上演练 |
| **M5 移动端** | 共享层与 Android 宿主完成(模拟器上走通),推送 / 真机 / iOS 未做 | 共享层(Headless);Android 宿主(`Ncw.Mobile.Android` + `Ncw.Mobile.Shared`):Keystore 封存的身份、Camera2 + ZXing 扫码、深链配对、后台 / 网络策略、附件选择器;**模拟器上真实走通**「配对 → 收件箱审批卡片 → 长按允许 → Run 继续」,`tools/Ncw.MobileSmoke` 脚本化冒烟 PASS;`Ncw.Mobile.Shared.Tests`(44)、`MobileLinkTests`(26) | 推送整段(无 Firebase 配置、无后端推送中继 B4)、真机验收(收到审批推送 → 打开 → 长按允许 → Run 继续)、真机手势 / 软键盘 / 相机 / 折叠屏、Release / 裁剪验证、`Ncw.Mobile.iOS`;已知小问题见 [11 §6 / §8](11-mobile-hosts.md) |
| **M6 云端 Agent** | 中继与 Agent 侧完成,后端与客户端未做 | `Ncw.Relay` 与中继承载(见上);`--mode cloud` 的 Agent 侧:启动配置、身份与信任(签名指令、防重放、吊销立即断开)、KMS 包裹主密钥、空闲休眠与唤醒恢复(未完成的 Run / 排队消息 / 定时任务 / 会话不丢)、配额与用量上报、Git 克隆与凭据、出网钩子、`cloud.getStatus`;`Ncw.Agent.Cloud.Tests`(假编排服务 + 假 KMS + 进程内中继 + 真实 AgentHost + 控制端经中继,含真实 `ncw-agent` 进程冒烟)、NativeAOT 发布零裁剪警告 | 后端(编排服务、沙箱镜像、持久卷、真实 KMS 适配、账户 API、固定出口 IP)不在本仓库,Agent 侧对后端的假设见 §6.1;客户端「创建云端 Agent」引导、唤醒状态、用量显示、Git 接力界面;「控制端连接 → 唤醒沙箱」触发链路;出网钩子除 Git 外未接线;SSH 部署密钥;云端数据导出;M6 验收的端到端(注册 → 创建 → 克隆 → 对话 → 推送)需要真实后端;中继多实例部署与容量验证 |
| **M7 升级链路** | 桌面自更新运行时已接入并在本机演练通过,后端与跨平台未做 | `Ncw.Build` 的 `velopack`(`--keep-output` 累积版本、增量包)/ `manifest` / Ed25519 清单签名、macOS 上 ad-hoc 签名后的完整流水线;**自更新运行时**:`UpdatePolicy`(Core 纯函数:mandatory / minimumSupportedVersion / graceUntil)、`UpdateService` 状态机、`ManifestVerifier`(下载前验 Ed25519 签名,公钥构建时内置、可轮换,没有公钥的构建拒绝一切)、`SignedFeedSource`(Velopack 只能看到 / 下载清单里签过的包)、`VelopackApp.Run()` 启动钩子、重启时 sidecar 先优雅收尾、设置页「关于」(版本 / 通道 / 检查 / 进度 / 立即重启并更新 / 必需与阻断 / 签名失败提示);**本机真实演练**(macOS arm64:真实更新器 + 真实 `ncw-agent` + 本地目录与本地 HTTP feed):3.0.0 → 3.0.1(全量与增量)成功、新版本 sidecar 起来并握手、数据库与设置保留;负面路径(清单被篡改、feed 回滚、包被替换)都按预期拒绝 / 不降级(04 §7.4、§7.5);CI / 发布工作流已写 | 2.9 桥接版在 `main` 上(不在本分支);后端 v3 feed 与 `tools/Ncw.Release` 上传(B2 / B3 不存在,客户端对 feed 的假设见 04 §7.4);Agent 侧「拒绝过低版本控制端的新 Run」与主界面的全局阻断条;Developer ID 签名与公证、Windows Authenticode、Windows / Linux 的安装与更新(没有证书 / 真机;Windows 卸载钩子里的协议注册清理未做);`.pkg` 安装器装出来的应用的更新;「2.8 → 2.9 → 3.0 → 3.0.1」和「2.8 → 手动安装 3.0」的整条演练 |
| **M8 扩展对齐** | 内联可视化完成(桌面端 WebView;手机静态快照),其余未做 | 内联可视化的 Agent 侧(`Ncw.Agent.Widgets`:四个 `visualize_*` 工具、消毒、静态快照、动作)**与客户端渲染**:WebView 方案评估与选型([03 §8](03-ui-layout.md#8-内联可视化m8webview-评估与渲染宿主):Avalonia 官方 `Avalonia.Controls.WebView` 的 `NativeWebView`,macOS 上用真实 WKWebView 实测)、外壳页面 + CSP + `window.ncw` 宿主桥、桥消息白名单与限速、导航 / 弹窗全拦、状态镜像、静态快照降级、`NcwWidgetHost` / `WidgetCard`;恶意载荷测试、四断点 × 深浅主题截图、真实 WebView 冒烟(`tools/WidgetSmoke`) | **Windows WebView2 / Linux WebKitGTK / WPE 的真实渲染没有机器可测**;macOS 上 WebView 在滚动列表里的合成 / 裁剪、透明背景、键盘焦点只验证了逻辑、没有目测;不做边写边渲染;手机端快照是文字不是图;内置浏览器的客户端、Office 编辑器未做 |
| **Beta → GA** | 未开始 | — | beta 通道、崩溃与指标收集需要真实后端与用户 |

**测试现状**(本节写成时的一次全量运行;个别并发敏感的用例在整套并行时偶发失败,单独重跑通过,见 `docs/parity.md`):`dotnet build NextCoWork.slnx` 零警告;`dotnet test NextCoWork.slnx` 全绿(此后新增的回放对照项目 `Ncw.Agent.Parity.Tests` 另算,它自己全绿),共 6947 个测试、31 个测试项目(含 Headless UI 截图测试与对真实 `ncw-agent` / `ncw-relay` 进程的冒烟)。  **M7 自更新接入之后的一次全量运行**:`dotnet build NextCoWork.slnx` 零警告;`dotnet test NextCoWork.slnx` 共 33 个测试项目、7622 个测试(含自更新新增的约 300 个:Core 判定、验签、feed、状态机、About 页 ViewModel、Desktop 引擎与重启顺序集成、Build 契约测试、「关于」截图与文字覆盖;可选的真实演练测试默认跳过),同时有别的任务在机器上跑、负载很高时,`Ncw.Agent.Runs / Host / Tools / Subagents / Planning / Integration` 里若干带 10 秒限时的用例整套并行时超时,**单独重跑这些项目全部通过**(性质同上面「个别并发敏感的用例」,与更新代码无关)。
并发不稳定的测试已逐个定因修复,见 `docs/parity.md`「并发不稳定测试的根因与修复」。这些测试都跑在 macOS 开发机上;其他平台的结果未知。

M1 和 M2 可以并行。**M3(Agent 内核重写)是整个 3.0 的关键路径**,M2 一完成就要投入最多人力。
M6 的后端部分(编排、沙箱)工期也长,应该和 M1 同时启动。
M7 里的 2.9 桥接版也要尽早启动,因为它需要先在 2.x 用户中铺开。

## 2. 功能对齐矩阵

| 2.x 功能 | 3.0 | 说明 |
|---|---|---|
| 多工作区、多会话、流式回复 | ✅ 3.0 | 改为项目栏加分组 |
| 工具调用、审批链、计划模式、子代理、后台任务、待办 | ✅ 3.0 | |
| 终端(PTY) | ✅ 3.0 | 使用 `TKCode.Arc.Terminal`,PTY 由 Agent 的 terminals 方法提供 |
| 文件浏览、编辑器、Diff | ✅ 3.0 | 编辑器先用 AvaloniaEdit 或 Arc 的代码组件,复杂编辑放到后续版本 |
| Git(状态、diff、提交、分支) | ✅ 3.0 | |
| Skills、MCP | ✅ 3.0 | |
| 定时任务、目标(Goal) | ✅ 3.0 | |
| 账户、登录 | ✅ 3.0 | 改为 `nextcowork://` 跳转加 PKCE(07) |
| 模型目录同步 | ✅ 3.0 | models.dev 定时自动更新,价格自动采用(02 A8) |
| 钱包、余额、充值、奖励 | ➖ 暂不考虑 | 3.0 不涉及支付 |
| SSH 远程环境 | ✅ 3.0 | 改为 SSH.NET,支持多设备执行(06) |
| 设置页 | ♻️ 重新设计 | 改成标签页,分三种作用域(07) |
| 2.x 数据 | ✅ 直接接管 | 任何安装方式都可以(04 §4) |
| 局域网远程连接、配对 | ♻️ 全新设计 | 远程开关 + `IP:端口` + 配对码;不兼容 2.x,2.x 的配对记录不迁移(09 §3) |
| 中继 | 🟡 M6:承载与中继服务已做,部署未做 | `Ncw.Relay` + `RelayTransport`(09 §9)已实现并有测试;配对时就生成的路由令牌让老配对直接可用;还没有生产部署(B5 的多实例 / 容量 / 运维) |
| 独立设备服务(NAS / 服务器部署) | ➖ 3.0 不做 | 2.9 升级前提示受影响的用户 |
| 命令行控制端、`ncw-device-service` 的各个子命令 | ➖ 3.0 不做 | 3.0 只做 GUI |
| 开放网关(Agent HTTP API) | ♻️ 重新设计 | 方法表的 HTTP 映射,不兼容 2.x 的 `/v1`(09 §10) |
| 内联 HTML/SVG 可视化 | ⚠️ 3.0 桌面端 | 依赖 WebView;手机端降级为静态快照。**Agent 侧已做**(`Ncw.Agent.Widgets`:`visualize_*` 四个工具、载荷消毒、静态快照字段、动作与 `widgets.runAction`,见 [08.03](08-agent-runtime/03-tools-permissions.md));**不做 TSX**(2.x 用 sucrase 编译,AGENTS.md §6 不允许);**客户端渲染已做**(桌面端 WebView 外壳 + `window.ncw` 宿主桥 + 手机静态快照,见 [03 §8](03-ui-layout.md#8-内联可视化m8webview-评估与渲染宿主))|
| 插件系统(命令、菜单、Agent 工具、Skills) | ✅ 3.0 | 插件 JS 跑在 Jaunt 里(D14),`nextcowork` API 的非 UI 部分保持兼容 |
| 浏览器工具 | ✅ 3.0 | 自研最小 CDP 客户端,驱动设备上已有的 Chrome / Edge / Chromium(D15 修订;PuppeteerSharp 实测 AOT 不可行,见 08.03 §14) |
| 中继服务、构建发布脚本 | ♻️ C# 重写 | D16 |
| 插件 CLI(`plugin-cli`、`create-nextcowork-plugin`) | ➖ 3.0 不做 | 插件作者写 JS(TypeScript 自己在仓库之外编译成单文件 ESM,Agent 不转译,见 02 A7.1) |
| 插件的 React 视图和自定义编辑器 | ➖ 暂不考虑 | 不在 3.0 范围,以后再评估 |
| (新增)云端 Agent | ✅ 3.0 | 见 [02 A6](02-agent.md#a6-云端-agent) |
| 内置浏览器(按工作区分区) | ⏳ 3.1 | 取决于 WebView 方案 |
| Office 编辑器(writer、sheets、slides) | ⏳ 3.1 | 同上 |
| 主题工作室 | ♻️ 重做 | 改用 Arc 的 token 和强调色 |

## 3. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| TKCode.Arc 还是 preview 版本 | API 可能变,也可能有 bug | 锁定版本;业务视图只用 `Ncw.UI.Controls` 封装;每次升级 Arc 都跑截图回归 |
| `TKCode.Arc.Mobile` 文档注明「尚未在真机上验证」 | 移动端组件可能不可用 | M1 就做一次真机 spike(iOS 和 Android 各一台)。不行的话,移动端改用 Arc 核心组件加自己写的导航。**进度**:共享层已经用 `TKCode.Arc.Mobile` 1.1.0 做完(`NavigationStack` / `Page` / `Navbar` / `Tabbar` / `PullToRefresh` / `ListGroup`,封装在 `Ncw.UI/Controls/MobileControls.cs`),Headless 里版式和状态都对;**真机 spike 仍没做**(手势、安全区、软键盘),替换范围就是那个封装文件和 `MobileShell` |
| Avalonia 上的 WebView 方案 | 影响浏览器、Office、可视化 | **M8 已评估并落地(可视化)**([03 §8](03-ui-layout.md#8-内联可视化m8webview-评估与渲染宿主)):Avalonia 12.1.5 核心包没有 WebView,官方 MIT 包 `Avalonia.Controls.WebView`(12.1.2)的 `NativeWebView` 用系统引擎(macOS WKWebView / Windows WebView2 / Linux WebKitGTK + WPE),支持内联 HTML、导航拦截、JS ↔ C# 双向消息、非持久存储、透明背景;没有设响应头的入口(CSP 写 meta);**macOS 上真实窗口实测**加载脚本、双向桥、CSP、导航 / 弹窗拦截都通过;**iOS 目标不存在**(手机降级为静态快照);Windows / Linux 同一份代码但没有机器验证;原生视图的「空域」问题(盖住 Avalonia 覆盖层)用挂起机制规避。浏览器面板(3.1)与 Office 编辑器可复用这个包,但它们对「滚动合成」和「键盘焦点」的要求更高,开工前先在真机上目测这两项 |
| **Agent 用 C# 重写的工作量和行为回归** | 2.x Agent 侧约 6.5 万行 TS;工期最长,最容易出现「看起来能用、细节不一样」 | 回放对照测试 + 单测移植 + `docs/parity.md` 对照清单;按 [01 §4](01-architecture.md#4-agent-移植策略) 的顺序推进,每一步都有可验证的验收标准 |
| NativeAOT 兼容性 | MCP SDK 等第三方库可能用到反射,AOT 下会失败 | M2 先做 AOT spike,把所有依赖都过一遍;不兼容的换库或自己实现 |
| Jaunt 用于插件的成熟度(1.0.x,刚发布) | 个别 JS 特性或性能问题影响插件 | **M4 已评估并落地**(02 A7.1):NuGet 1.0.3,NativeAOT 零警告且原生二进制实跑通过,`JsLimits` / `Hardened` 实测可用,用 2.x 的 12 份真实清单和移植的三个插件夹具验证过兼容性;**「sucrase」那半条取消**(2.x 插件本来就是打好的单文件 ESM,sucrase 只用于 widget 的 TSX)。已知约束:引擎同一时刻一次根执行(插件内调用串行)、执行结束不能有未汇合的异步宿主操作(前导已兜住)、`MaxTrackedBytes` 是累计计数(接近预算时透明重建引擎)。同属 AIDotNet,问题可以直接在上游修;插件跑在预算内,出问题不会拖垮 Agent |
| ~~PuppeteerSharp 和 Playwright 的能力差异~~ | 浏览器工具的部分行为变化 | **已处理**:PuppeteerSharp 25.17.1 在 NativeAOT 下有 11 类裁剪 / AOT 警告且绕过执行环境起进程,改为自研最小 CDP 客户端(`Ncw.Agent.Browser`,零第三方依赖、零警告);2.x 浏览器工具逐条对照见 08.03 §14。已知缺口:浏览器只支持本机环境(SSH 环境给结构化错误)、没有内置浏览器面板(3.1)、设备上没装 Chrome / Edge / Chromium 时不会自动下载 |
| 自研 PTY(posix_spawn / ConPTY) | 终端是高频功能,边界情况多 | **已评估并实现**:现成的库(Pty.Net、Porta.Pty、MiniPty、Ghostflyby.Pty 等)都不合适,见 06 §9.1;2.x 终端的回归用例已移植(`docs/parity.md`),POSIX 真 PTY 在 macOS 与 Linux(glibc / musl)上通过;**Windows ConPTY 没有真机验证**,第一次在 Windows CI 上跑时预计需要微调 |
| ~~移动端 `SslStream` 用自签名证书做双向认证的行为差异~~ | 手机连不上设备 | **已处理**:M2 spike 发现 macOS / iOS 的 `SslStream` 没有 TLS 1.3,改用 BouncyCastle 的纯托管 TLS 1.3,各平台同一实现(09 §4) |
| 局域网连接被系统防火墙拦截 | 远程开关打开了却连不上 | Windows 安装器预先添加「专用网络」入站规则;macOS 首次监听时弹窗说明;诊断页提供「从另一台设备测试连接」的指引 |
| SRP-6a 实现(BouncyCastle)的 AOT 兼容性 | 配对码配对不可用 | M2 spike;不行就换成同样现成的 PAKE 库,不手写 |
| 2.x 开放网关用户 | 升级后脚本失效 | 升级说明写明新接口;OpenAPI 文档自动导出 |
| 2.x 用户升级失败 | 用户流失 | 桥接版先做完预检和数据整理;服务端灰度;回滚说明 |
| 云端沙箱逃逸、租户间隔离 | 安全事故 | 优先 microVM;出网白名单加审计;上线前做第三方渗透测试 |
| 云端成本失控(空闲实例、长时间 Run) | 亏损 | 3.0 不收费,只能靠空闲休眠、配额上限和白名单开通来控制 |
| SSH.NET 的 AOT 兼容性和功能缺口(ssh-agent、ProxyJump、Windows 远程主机) | SSH 体验不完整 | M2 做 spike;缺的部分自己补(ssh-agent 协议、跳板链) |
| 遗留 safeStorage 的解密 | 个别凭据丢失;macOS 弹出钥匙串授权框 | 按 2.x 实际用的 Electron 版本核实格式,用三个平台的真实数据测试;解不开的凭据标为「需要重新输入」,不阻断接管 |
| 2.x 和 3.0 同时打开同一份数据 | 数据损坏 | 接管时卸载或提示删除 2.x;2.9 起认所有权标记 |
| Linux 上 `nextcowork://` 注册不生效 | 登录卡住 | 提供登录码备用方式 |
| 云端冷启动慢 | 手机端体验差 | 预热镜像池、卷快照;界面显示唤醒进度 |
| 签名和公证流水线 | 公证被拒或运行时被系统杀掉 | M2 出第一个 `ncw-agent` 二进制时就接入签名和公证,别留到 M7。**进度**:`tools/Ncw.Build` 的 `sign`(codesign / notarytool / signtool)已接入,macOS 上用 ad-hoc 身份把「逐文件签 → verify → vpk 打包 → 从包里解出的 .app 再验证」走通;Developer ID 与公证需要证书和账户,尚未实跑(04 §7.1) |
| 移植期间 2.x 还在修 bug | C# 版漏掉修复 | 2.x 的每个 Agent 侧修复都登记到 `docs/parity.md`,作为 C# 版的待办 |

## 4. 测试策略

- `Ncw.Core`:派生函数、Store 对账、状态单调、多设备断线。对应 2.x 的 subagent-view、subagent-monotonic、subagent-sync 这几组单测。
- `Ncw.Protocol`:按协议版本冻结的黄金向量;模糊测试;续连、重同步、积压溢出、幂等重放、交互先到先得;配对的中间人测试;本机和局域网承载各跑一遍,中继上线时再加一遍(见 09 §12)。
- UI:Avalonia.Headless,覆盖四个断点和深浅两种主题的截图回归。
- **回放对照**:2.x 录制的上游流、工具结果和用户操作,分别在 C# 和 TS 上回放,比较事件序列和落库结果(`Ncw.Agent.Parity.Tests`)。
- 端到端:真实的 ncw-agent 加 3.0 客户端,跑冒烟脚本(沿用 2.x 的 `agent-smoke` 思路)。
- 升级演练:在三个平台的虚拟机里跑 2.8 → 2.9 → 3.0 → 3.0.1,以及 2.8 → 手动安装 3.0,前后对比数据库行数和凭据是否可以解密(包括遗留的 safeStorage 行)。
- SSH:用容器起 sshd(Linux)加一台 Windows OpenSSH 主机,覆盖密钥、口令、键盘交互式、跳板机、断线重连、指纹变化。
- 登录:三个桌面平台和两个移动平台上,覆盖「应用未运行时点跳转链接」和「应用已运行时点跳转链接」两种情况。
- 模型目录:304、超时、超大响应、数据缩水一半以上、断网首次启动这几种情况。

## 5. 待定问题

| # | 问题 | 结论 |
|---|---|---|
| Q1 | Agent 用什么实现? | ✅ C# 重写(决策 D3),代码在本分支 `src/Ncw.Agent.*`;2.x 的 TS 代码只作参考 |
| Q2 | 插件的 React 视图在 3.0 GA 时是否必须可用? | ✅ 暂不考虑,不在 3.0 范围 |
| Q3 | 有没有 Apple Developer ID 和 Windows 代码签名证书? | ✅ 有。3.0 起启用正式签名和公证(决策 D12) |
| Q4 | 2.x 的维护期多长? | ✅ 先不考虑 |
| Q5 | nextco.work 后端能不能配合? | ✅ 可以配合,清单见 §6 |
| Q6 | 3.0 是否提供云端 Agent? | ✅ 需要,见 [02 A6](02-agent.md#a6-云端-agent) 和里程碑 M6 |
| Q7 | 插件里的 JS 怎么执行? | ✅ 用 Jaunt(决策 D14);整个技术栈全部用 C#(D15、D16) |
| Q8 | 3.0 要不要 CLI 和独立设备服务? | ✅ 都不做,3.0 只做 GUI;部署形态只有桌面内置和云端 |
| Q9 | SSH | ✅ 要支持,并按多设备执行设计(06) |
| Q10 | 2.x 数据 | ✅ 直接接管(04 §4,D20) |
| Q11 | 支付 | ✅ 暂不考虑 |
| Q12 | 首次启动和登录 | ✅ 保留,浏览器登录后用 `nextcowork://` 跳回(07,D18) |
| Q13 | 设置页 | ✅ 重新设计(07,D21) |
| Q14 | 模型数据 | ✅ 定时从 models.dev 自动更新(02 A8,D19) |
| Q15 | 2.x 的 IPC 通道(约 415 个)和全部功能,哪些要迁成协议方法、哪些只在客户端本地处理、哪些不要了? | ✅ 已盘点:359 个调用通道(351 invoke + 8 send),217 个进 Agent 协议、67 个拆分、21 个客户端本地、39 个放 3.1、15 个不做;见 [10 盘点](10-inventory.md) |
| Q16 | 项目指令文件是否兼容 `CLAUDE.md`(pi 按 `AGENTS.override.md` → `AGENTS.md` → `CLAUDE.md` 取第一个;2.x 只读 `AGENTS.md`)? | ✅ 按 pi 兼容读取(08-agent-runtime/04 §2) |
| Q17 | 工具预检从 2.x 的「并行里各自审批」改为 pi 的「顺序预检、并行执行」,可以吗? | ✅ 跟 pi 一样:顺序预检、并行执行(08-agent-runtime/02 §4、03 §3) |
| Q18 | 设备协议是否沿用 2.x? | ✅ 不参考 2.x,全新设计(09,D4) |

## 6. 后端配合清单

| # | 内容 | 用于 | 里程碑 |
|---|---|---|---|
| B1 | ~~更新 feed 按客户端版本门控:低于 2.9 只下发 2.9~~ | 升级链路 | **不做**:3.0 不进 electron-updater 的 feed,2.x 的 feed 里最新的就是 2.9(04 §2) |
| B2 | 3.0 各平台的版本、安装包、sha256、Ed25519 签名的清单 | 2.9 → 3.0、官网下载页 | M7。**后端已实现,未部署**:`GET /api/client/updates/v3/latest?channel=`(汇总)+ 每个平台目录里原样的 `release-manifest.json(.sig)`(04 §7.4「后端」)。清单格式与签名有契约测试(`UpdateContractTests`) |
| B3 | Velopack feed `/api/client/updates/v3/{channel}/{rid}/`,上传接口支持 Velopack 产物,沿用 mandatory / minimumSupportedVersion / graceUntil | 3.x 自更新 | M7。**后端已实现,未部署**(`Codex/CoWork` 分支 `feat/client-updates-v3`):按 sha256 上传 + 原子换快照,策略在被签名的清单里;上传工具是 `tools/Ncw.Build upload`(上传前验签)。本机端到端走通:`upload` → 后端 → 装好的 3.0.0 经真实 Velopack 更新到 3.0.1(04 §7.4「后端」第 9 条) |
| B4 | 推送中继:注册 APNs / FCM 令牌,只转发不透明的事件 id | 移动端推送 | M5 |
| B5 | 全新部署 C# 中继服务 `Ncw.Relay`(09 §9);无状态、按 deviceId 粘性路由;容量要能支撑手机和云端的长连接 | 局域网外连接、云端 | M6。**状态:服务本体与两端承载已完成**(单实例内存实现、哈希路由接口 + 421、限流 / 背压 / 空闲 / 带宽上限、指标与健康检查、结构化日志,`tests/Ncw.Relay.Tests` + `RelayLinkTests`)。**未做**:多实例部署与实例间转发(靠负载均衡器按 `d=` 粘性路由)、容量压测、TLS 证书与运维脚本(`Ncw.Build` 的 `build-relay`)、云端设备的路由下发(B7) |
| B6 | 云端 Agent 编排服务:创建、唤醒、休眠、升级、删除;沙箱镜像;持久卷和快照 | 云端 Agent | M6。**Agent 侧已完成**:编排通道的 5 个 HTTP 端点和指令格式是契约([09 §13](09-protocol.md#13-编排控制通道m6agent--编排服务)),后端实现它们;假设见 §6.1 |
| B7 | 账户 API:注册控制端证书公钥,把信任记录下发给云端设备,向控制端返回云端设备证书指纹(09 §4) | 云端配对 | M6。**Agent 侧已完成**:后端把「注册控制端公钥」翻译成 `trust.put` 指令(指纹 + 名称 + 角色 + 路由令牌哈希);设备证书指纹来自设备 `hello`(§6.1 A4) |
| B8 | KMS:云端密钥包裹 | 云端密钥 | M6。**Agent 侧只有接口与适配点**:`IKeyWrap`(`Wrap` / `Unwrap` 数据主密钥,带加密上下文)+ `CloudBoot.CreateKeyWrap`;真实云厂商适配由后端写(§6.1 A10) |
| B9 | ~~计费~~(暂不考虑支付)。只需要按账户设置配额,并提供用量查询接口 | 云端配额 | M6。**Agent 侧已完成**:`quota.set` 指令下发上限、Agent 在 Run 发起前检查、`/v1/agent/usage` 批量幂等上报;后端存用量并提供查询(§6.1 A5 / A6) |
| B10 | 账户配置同步新增 `RailLayout`(项目组和顺序) | 跨端分组 | M4 |
| B11 | OAuth:**桌面已就绪**——后端的 `nextcowork-desktop` 客户端 + `/client/authorize` 授权页 + 回环回调(`http://127.0.0.1:{端口}/callback`)+ PKCE(S256)就是 2.x 在用的那套,3.0 桌面直接使用。**后端仓库(`Codex/CoWork`)已补**(`ClientOAuth.NextCoWorkDesktop`,**待部署到 nextco.work**):设备授权码(`POST /api/client/oauth/device`,`ClientGrant` 改为可组合,`nextcowork-desktop` 同时支持授权码和设备码,网页确认页 `/client/device` 对 NextCoWork 有专门文案)、自定义 scheme 回调 `nextcowork://auth/callback`(逐字匹配,回环回调继续可用)。**仍待后端**:手机端的 client_id 与回调(目前只有 Tack 的 `tack-mobile` + `https://nextco.work/client/mobile-callback` Universal Link)、iOS Universal Links 和 Android App Links 的域名关联文件 | 登录 | M4 |
| B12 | 云端 Agent 使用固定的出口 IP 并公开,SSH 目标走出网白名单 | 云端 SSH | M6。出网白名单与固定出口 IP 是基础设施;Agent 侧只有 `IEgressPolicy` 钩子(Git 克隆已接,其它出口待接,§6.1 A11) |
| B13 | **委托令牌交换**(RFC 8693 `urn:ietf:params:oauth:grant-type:token-exchange`,`POST /api/client/oauth/delegate`)。**后端仓库已实现(待部署)**:用桌面访问令牌换一枚 `audience = ncw-agent` 的令牌,scope 只含模型 / 配置同步(不含机密)/ 技能与插件市场(`ClientOAuth.DelegableScopes` ∩ 调用者已有的),沿用同一个会话(会话吊销即失效)、没有刷新令牌、有效期 = 调用者令牌的剩余有效期;委托令牌不含 `profile:read`,所以换不了新令牌也管不了会话。**没有它时客户端退回把访问令牌本身交给 Agent**(07 §2「实现状态」),功能不受影响,只是权限没有收窄 | 本机 Agent 的账户授权 | M4 |
| B14 | **控制端会话列表与撤销**(**后端仓库已实现,待部署**):`GET /api/client/sessions`(`items[]`:`id`、`name`、`platform`、`lastActiveAt`、`isCurrent`;只含这个账户的 `nextcowork-desktop` 会话,不含网站和 Tack)、`DELETE /api/client/sessions/{id}`(撤销那个控制端的会话,刷新令牌与访问令牌一起失效;当前会话不能从这里撤销,退出走 `/oauth/revoke`),用于设置页「已登录的控制端」。设备名和平台取自 User-Agent(`NextCoWork/3.x (macOS; arm64)`) | 账户页 | M4 |
| B15 | B11 的补充约定。**已按线上后端校正**:授权页是 `/client/authorize`(不是 `/oauth/authorize`)、`client_id = nextcowork-desktop`(不是 `ncw-desktop`);scope 串对后端是**全等匹配**,以客户端 `AccountEndpoints.Scope` 为准。设备授权端点按后端实际路径是 `POST /api/client/oauth/device`(不是早期约定的 `/device_authorization`),令牌端点对 `nextcowork-desktop` 的 `device_code` 授权类型已在后端仓库实现(待部署;部署前线上得到 404 / 400 `invalid_client` → 客户端显示「登录码暂时不可用」)。手机的 client_id 仍未约定 | 登录 | M4 |

### 6.1 编排服务接口契约与待对齐的假设(M6)

Agent 侧(本仓库)对着下面的契约实现并用假编排服务测试;**后端目前不存在**,以下每条都是「我们假设后端这样做」,需要和后端对齐(对不上的改后端或改 Agent 侧适配,类型都在 `Ncw.Protocol/Orchestration`)。

**契约**(详见 [09 §13](09-protocol.md#13-编排控制通道m6agent--编排服务)):Agent → 编排服务的 5 个端点(`hello`、`commands` 长轮询、`ack`、`usage`、`lifecycle`),`Authorization: Bearer <账户身份令牌>` + `X-Ncw-Device`;编排服务 → Agent 只有一种东西:**签名指令**(经 `hello` 响应和 `commands` 长轮询送达)。
对应 B6(编排:创建 / 唤醒 / 休眠 / 升级 / 删除)、B7(账户 API:注册控制端公钥 → `trust.put`;返回设备指纹和中继路由)、B8(KMS)、B9(配额与用量)、B12(出口 IP 与白名单)。

| # | 假设 | 若不成立的影响 |
|---|---|---|
| A1 | **传输**:Agent 作为 HTTP 客户端访问编排服务(`https`;仅回环允许 `http`),长轮询 25 秒拉指令;沙箱不需要入站端口 | 后端更想用 WebSocket / SSE / 推送:换一个 `IOrchestrationChannel` 实现,指令与回执的类型不变 |
| A2 | **账户身份令牌**由编排服务在启动时注入(`NCW_ACCOUNT_TOKEN` 或 `_FILE`),同一枚既是编排通道的 Bearer,又作为模型通道 / 配置同步的委托令牌(`DelegatedAccount`);到期时间由配置 `accountTokenExpiresAt` 给(缺省启动后 1 小时),轮换用 `account.token` 指令 | 后端要求两枚不同的令牌(例如 B13 的 `audience = ncw-agent` 令牌单独签发):在 `AccountTokenGrant` 加字段、通道的 Bearer 与委托令牌分开持有 |
| A3 | **编排密钥**:Ed25519,验证公钥(`kid:base64`)随启动配置注入,多个 `kid` 并存以轮换;私钥只在编排服务(或 HSM) | 用别的签名算法:`EnvelopeSigning` 一处替换,信封格式不变 |
| A4 | **身份注册的时序**:`deviceId` 由设备公钥派生,所以编排服务不能预先分配;流程是「创建云端 Agent → 沙箱启动 → 设备 `hello`(带持有私钥证明)→ 后端记下指纹 → 账户 API 才能把设备指纹和中继路由返回给控制端」。客户端必须等 `awake` 之后才拿得到指纹 | 后端要预先分配设备身份:需要改成编排服务签发密钥(私钥离开设备),与「私钥永不离开设备」冲突,不建议 |
| A5 | **配额**:字段见 `QuotaLimits`;日 / 月窗口按 **UTC** 自然日 / 月;token = 输入 + 输出 + 缓存读写;「计算时长」= Agent 醒着的秒数(后端可用沙箱运行时间核对);超限由 Agent 自己拒绝(后端不在线时仍有效);`quota.set` 整体替换 | 后端按账户时区计窗口:两边的「今天」不一致,需要在 `QuotaLimits` 加窗口偏移;后端要别的维度:加可选字段(旧 Agent 忽略) |
| A6 | **用量上报幂等**:后端按 `BatchId` 去重并至少保留 Agent 的重试期;已见过的批次回 `2xx` + `duplicate: true`;批次是增量,不是累计;后端提供用量查询接口(给客户端「用量显示」和账户页) | 后端要累计值而不是增量:Agent 本来就有累计值(账本),改 `UsageBatch` 加字段即可 |
| A7 | **休眠 / 唤醒**:收到 `lifecycle: sleeping` **或**进程以退出码 0 退出,后端即可停掉沙箱、保留持久卷(并拍快照,在 Agent 退出之后拍,WAL 已检查点);`wakeAt` 到点唤醒(Agent 已减去 2 分钟提前量);**控制端连接触发唤醒的链路目前没有任何实现**:假设是控制端经账户 API 请求唤醒(客户端显示「正在唤醒云端…」),后端拉起沙箱,Agent 注册中继后控制端重试(中继对离线设备回 `device_offline`) | 想让中继直接触发唤醒:中继收到对云端 `deviceId` 的 `connect` 时回调编排服务(中继侧小改动,`device_offline` 路径上加一个钩子),Agent 侧不变 |
| A8 | **指令队列**:后端为每台设备保存**未确认**的指令,序号每设备单调递增、**跨沙箱重建也不回退**(Agent 把已应用的最大序号存在数据根里,后端若重置序号会被当成重放拒绝);`hello.LastSeq` 之后的积压随 `hello` 响应送达;收到 `duplicate` / `rejected` 的回执后停止重发,`busy` 的稍后重发同一条;休眠期间到达的指令保留到下次 `hello` | 后端序号不单调:需要在 `hello` 里带一个「纪元」让 Agent 重置序号(Agent 侧小改) |
| A9 | **持久卷**:数据根和 `workspacesRoot` 都在卷上;同一卷同一时间只挂一个沙箱(Agent 另有 `run/agent.lock` 兜底,第二个实例以 75 退出);卷在沙箱之间迁移时 `expectedDeviceId` 防止挂错卷(退出码 65) | — |
| A10 | **KMS**:后端提供 `IKeyWrap` 实现(包裹端点 / 云厂商 KMS),加密上下文 = 配置的 `kms.context`(建议含账户 id + 沙箱 id),`keyId` 随包裹文件保存并在解开时传回;Agent 启动时解开**一次**并缓存在内存。**密钥轮换(用新 KMS 密钥重新包裹)没有做**:现在要换只能由后端在沙箱外重写 `credential.key.kms`。KMS 不可用 = 启动失败(退出码 69,可退避重启) | 想要运行中重新包裹:`KmsMasterKeySource` 加 `RewrapAsync`(格式已带 `keyId`) |
| A11 | **出网**:沙箱网络策略强制白名单与固定出口 IP;模型通道、Git 托管、包管理源在白名单里。Agent 的 `egress` 配置只是提前拒绝 + 审计;SSH 目标(B12)由基础设施约束 | — |
| A12 | **路由令牌**:编排服务生成 256 位随机令牌,只交给控制端,设备只存哈希 = `SHA-256(ASCII 令牌)` 的小写十六进制(`RelayProtocol.HashToken`);中继地址在启动配置里,和账户 API 返回给控制端的路由一致 | 哈希算法不同:`TrustEntry.RouteTokenHash` 必须和中继、设备同一种,三方一起改(中继协议已冻结) |
| A13 | **角色**:账户所有者的控制端下发 `owner`(个人版);团队共享(`member` / `viewer`)后续再说,协议已支持 | — |
| A14 | **升级**:`upgrade.request` 后 Agent 通知 `upgrading` 并以 0 退出,后端换二进制后在同一数据根重新拉起;协议 N / N-1 兼容保证新旧客户端都能连 | — |
| A15 | **健康检查**:沙箱探针用配置里的内网地址 / 端口访问 `GET /healthz`(`200` = 就绪,`503` = 还在收尾 / 未打招呼);不配置则没有这个端点 | — |
| A16 | **时钟**:沙箱时钟由后端保证 NTP;指令有效期容忍 5 分钟偏差,休眠唤醒后设备时钟偏差更大时指令会被当成过期拒绝(编排服务据 `rejected: expired` 重新签发) | — |

