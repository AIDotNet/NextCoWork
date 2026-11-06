# 设备模式与独立 Agent 服务实施计划

## 1. 已确认的交付范围

目标：桌面及未来手机控制台连接执行设备上的完整 Agent。切设备改变整个业务后端，不是新增 SSH 项目，也不是同步或挂载远程数据库。

| 项目 | 已确认决策 |
|---|---|
| 执行设备 | Windows、Linux、macOS。 |
| 本地设备 | 桌面默认设备，与远程统一采用独立常驻服务；完全退出桌面不停止任务。 |
| 桌面首版 | 完整工作台：Agent、项目、会话、配置、文件、终端、Git、Skills、MCP、插件、自定义编辑器、浏览器、Office、后台任务等已实现能力。允许内部阶段开发，不先发布功能受限的远程模式。 |
| 安装 | 小核心＋明确提示后安装按需运行时，另提供预装方式；未约定硬性 MB 上限。 |
| 切换 | 同一桌面应用全部业务窗口统一切换；不改变其他控制台的所选设备。 |
| 并发 | 多台已配对桌面及未来手机可同时操作，不设置设备级唯一控制者。 |
| 同资源并发 | 同会话消息排队，文件版本冲突保护，审批一次性处理；同一终端、浏览器、Office 文档用可交接输入租约，其他端可观看，不做逐字符协同编辑。 |
| 数据归属 | 账号、模型凭据、用量、项目、主题、语言、快捷键等全部随设备；控制端设备清单、配对凭据、窗口等必要本机能力例外。 |
| 网络 | 直连不依赖官方账号或中转；交付可自部署、业务端到端加密的中转服务。 |
| 恢复 | 控制台退出/切设备/断网不停止任务；执行设备或服务重启后保留状态、标记中断、人工续跑；正常服务升级等待任务结束。 |
| 旧浏览器 | 保留原始 Profile，迁移可兼容 Cookie/存储，允许个别网站重新登录；其他业务数据保留。 |
| 后续手机 | iOS/Android App，原生或跨平台均可，本期不选 UI 框架。先做配对/切设备、项目/会话、任务发起/停止、审批、文件及结果查看；触屏完整工作台后续适配。 |
| 手机本期准备 | 共享客户端、跨语言协议、无窗口服务接口、原生安全通道验证、后台恢复和推送端口；不开发完整手机产品 UI，不接真实 APNs/FCM，不做 PWA。 |

手机是 controller，不运行 Agent，也不依赖桌面充当网关。关闭控制台电脑不影响另一台执行设备；本机执行设备关机/睡眠时不能继续执行，本计划不自动修改睡眠策略。

## 2. 已验证代码基础

这是代码调查结论，不表示功能已实现或测试已通过。当前版本为 2.6.1；现有发行矩阵为 Windows x64、Linux x64、macOS arm64/x64（`.github/workflows/release.yml:90`）。下文“四平台”指这四个组合，不宣称额外架构已支持。

| 位置 | 事实及改造依据 |
|---|---|
| `src/main/db/index.ts:57`、`src/main/index.ts:778` | 主要数据在 `~/.next-cowork/data/`，供应商文件在 profile 根 `providers.json`；必须复用现有路径解析，不能只换一个目录变量。 |
| `src/main/kernel/host.ts:256` | 有 Node 宿主，但默认 cwd 路径和内存凭据不能用于生产服务。 |
| `src/main/runtime.ts:1`、`src/main/__tests__/architecture.test.ts:235` | 有宿主注入；当前架构测试只守直接依赖，仍有间接 Electron/IPC 耦合。 |
| `src/main/ipc/index.ts:299` | 窗口与业务 handler 混合，需要全量分类，不能整体开放为网络接口。 |
| `src/renderer/src/services/ipc.ts:30`、`src/preload/index.ts:37` | 有统一 invoke/send/on 和白名单，但依赖 `window.nextcowork`，不是移动 SDK。manifest 未声明移动 UI 框架。 |
| `src/main/session-runtime.ts:118`、`src/main/kernel/run-registry.ts:295` | 后台排队与运行快照可复用；日志有界、会裁剪和回收，不是持久化事件日志。 |
| `src/main/ipc/agent.ts:192`、`:585` | 忙会话入队早于 runId 查重，不能直接当网络幂等；桌面退出目前会停止全部运行。 |
| `src/main/terminal-host.ts:356` | 部分终端随 WebContents 销毁，必须拆开订阅与资源寿命。 |
| `src/main/ipc/workspace.ts:33`、`src/renderer/src/shell/CreateSshWorkspaceDialog.tsx:52` | 系统目录对话框需远程化；现有 SSH 目录浏览交互可复用。 |
| `src/main/ipc/workspace-files.ts:266`、`src/main/net/attachment-protocol.ts:113` | 已有文件 revision、临时文件/fsync/替换和附件 Range；需保留保护并加设备绑定。 |
| `src/main/browser/bindings.ts:117`、`src/main/browser/headless.ts:55` | 可见页面依赖桌面 webview；已有 headless 后端，但没有完整远程交互 surface。 |
| `src/main/plugin/host-window.ts:132`、`src/main/plugin/protocol.ts:71` | 插件后端是沙箱化隐藏 BrowserWindow，独立服务不得降级为裸 Node 执行第三方代码。 |
| `src/main/document-engine/manager.ts:24`、`src/main/plugin/document-view.ts:75` | 文档模型可脱离 Electron；视图身份、释放和推送仍需适配。 |
| `src/main/account-switch.ts:75`、`src/main/ipc/client-auth.ts:542` | 换账号有运行/文档闸门，OAuth 依赖本机 loopback；换设备不能复用“换账号”操作。 |
| `src/main/net/proxy.ts:106`、`src/main/update/update-service.ts:135` | 网络代理、证书及更新生命周期需 Node 服务实现，不能简单换裸 fetch。 |
| `plugins/ncw.writer/package.json:16` | Office 依赖 `ncw.office-runtime`；兄弟仓库存在，源码 manifest targets 由构建生成，四平台发行件尚未在本任务验证。 |

CI 使用 Node 24.16.0（`.github/workflows/ci.yml:29`）；服务固定采用经验证的 Node 24 运行时，不因为根 manifest 允许 Node 20 就假定支持 `node:sqlite`。实施前重新检查工作区改动，保护用户及其他任务的修改。

## 3. 目标架构与数据边界

```text
桌面 UI → Desktop Broker ─┐
                         ├→ ControllerClient / 共享 wire 协议
手机 UI → Native Adapter ┘       ↓ 平台安全传输/凭据/资源端口
                 本机 UDS/Named Pipe，或直连 HTTPS/WSS，或中转内 TLS
                                      ↓
                    Agent Gateway：认证、schema、权限、限流
                                      ↓
                  应用服务：Agent、会话、项目、文件、账号、调度
                                      ↓
                  数据库/文件系统、OS/MCP、浏览器/插件/Office

Agent 通知 outbox → 可选推送适配 → 后续系统推送 → 手机提示
                     不在任务执行链路中
```

- 每个规范化数据根只有一个 Agent 拥有业务库、调度器和运行时。桌面退出仅 detach，不关业务库、不调用 `shutdownRuns()`。
- 使用服务生命周期内持有的 OS 互斥/文件锁，包含规范化根和用户身份；PID 文件只作诊断。新版桌面连接已有服务，不启动第二个写入者。
- 首版共享一个服务用户及其当前账号作用域，不新增多租户或多账号同时激活。不同 OS 用户的服务根独立。
- 既有 SSH 工作区保留在所选 Agent 内；远程 Agent 的本机项目仍是 local environment，不能因为控制台跨网络就套用 SSH 能力限制。
- 模型、搜索、Git、MCP、工具与组件下载由设备发起，不绕回桌面；中转不是模型代理或数据中心。
- 保留 kernel/db/environment/document-engine 等现有目录，仅拆必要服务、宿主与传输，不进行无关重命名或全仓搬迁。

### 身份与生命周期

| 字段 | 用途 |
|---|---|
| `deviceId` | 设备信任根公钥派生，地址改变不变；不得复用云同步 deviceId。 |
| `controllerId` | 每个桌面/手机安装实例的配对身份，各持自身密钥。 |
| `clientSessionId`、`viewId` | 控制台运行实例及窗口/页面身份，不使用跨机器 WebContents 数字 ID。 |
| `clientKind`、`clientCapabilities` | desktop/ios/android 与展示/输入/资源能力，只用于协商，不构成授权。 |
| `serverEpoch` | 服务每次启动改变，废止旧运行态句柄。 |
| `dataEpoch` | 恢复/重置改变；保存在不被业务备份覆盖的服务状态，不能倒退。 |
| `configScope`、`scopeEpoch` | 账号作用域及代际，防止旧请求落入新账号。 |
| `bindingEpoch` | 控制台切设备代际，拦截迟到响应、定时器、通知点击和资源加载。 |

上下文由已认证连接和服务端登记推导，报文里的 ID 不等于权限。

### 存储归属

- 保留 `data/`、`providers.json`、账号配置树、附件、主题及已有 headless profiles 的解析规则；项目文件留在原设备原目录。
- 新增 profile 根下 `agent/`：配置、身份、受信控制端、锁、端点与组件状态；安全授权不进入普通业务备份/云同步。
- 新增 `controller/`：本桌面设备清单、连接偏好、密钥引用和隔离的 UI Chromium 数据。手机使用 App 沙箱/安全存储，不假定存在 home 或 `.next-cowork`。
- “设备”页不能依赖当前远端数据库；数据清理按资产类别操作，不递归删除整个 profile 根。
- 业务按 API 按需获取，不下载整个目录、不挂载 SQLite、不在控制台写远程库副本。
- 显示缓存和未确认输入有界且按设备/作用域隔离，不是权威数据；正常保存等待回执，断线不自动将缓冲当成新命令发送，未确认按键不能宣称已保存。

## 4. 通用客户端与通信协议

### 4.1 接口边界

对现有 invoke/send/event 建立编译期完备分类，新入口遗漏分类即失败：

1. `controller-local`：窗口/页面、剪贴板、文件选择、平台版本、设备清单、配对凭据。
2. `device`：全部业务、账号、项目、Agent、文件、插件、备份与统计。
3. `composed`：OAuth、上传/下载、插件交互、浏览器/Office 显示等跨两侧操作。

网络方法登记请求/响应 schema、授权、变更属性、幂等类别、最大载荷和上下文。TS 类型、preload 白名单都不能代替网络校验。任务、审批和资源 API 不要求桌面窗口。

共享核心放 `src/controller-client/`，不依赖 Electron、Node、React、DOM、`window.nextcowork`、`ncw://`。桌面实际消费该核心；未来跨平台 App 可复用 TS，纯原生 App 按相同 schema/测试向量实现，不锁定 UI 框架。

| 注入端口 | 职责 |
|---|---|
| `SecureTransport` | 已认证字节通道、直连/中转、有界发送；TLS 与密钥留在平台适配。 |
| `CredentialStore` | 信任记录、密钥句柄及证书操作，不要求导出私钥。 |
| `ClientStateStore` | 设备清单、游标、未确认操作摘要，不绑定桌面路径。 |
| `Lifecycle` / `Clock` | 前后台、网络变化、单调时钟与退避，不假定定时器一直执行。 |
| `ResourceReader` / `UploadSource` | ResourceRef 范围流、本机文件或手机 URI 的受控上传。 |
| `PlatformIntents` | 文件选择/分享、授权回调、剪贴板和通知权限，结果绑定发起设备。 |

### 4.2 传输与编码

- 控制面采用类型化 RPC＋事件订阅、MessagePack、HTTPS/WSS。本机使用受限 UDS/Named Pipe 承载同一业务协议并认证，默认不对外监听。
- 文件/附件使用独立流式 HTTP 端点，支持 HEAD、Range、ETag/revision、分块上传、校验和原子提交。
- 浏览器画面、Office tile 独立数据连接；不用大段 Base64 挤占控制面。连接隔离之外还需限速/公平调度，保留控制流发送机会。
- 直连与中转共用 Gateway、权限和 DTO；首版不做 QUIC、P2P 打洞、数据库同步。
- Node 适配预定依赖 `ws`，通用 codec 用 `@msgpack/msgpack`；证书生成使用审查过的 X.509 库，TLS 使用成熟实现，不自研密码协议。新依赖锁定实际验证版本。
- 现有 zod/undici/playwright-core 用于对应服务模块；`ws`、Node TLS、undici 不得进入共享客户端传递依赖。原生锁、代理与编码组件先验证再登记，不假定系统命令/库存在。
- 手机通过原生安全适配实现 mTLS 和中转内 TLS；默认移动 WebSocket 不等于支持客户端证书或 Node Duplex。必须先验证，不能改成中转解密或关闭证书校验来迁就客户端。

### 4.3 Wire contract

- 请求包含版本、requestId、method、params、绑定/数据/作用域代际；变更带 idempotencyKey 或 inputSeq、必要的 expectedRevision。响应为结果或结构化错误；事件带 streamId/cursor/资源范围。
- ID/游标统一不透明字符串，时间明确为毫秒，字节为标准 binary；定义 UTF-8、未知字段、空值和大小写规则，提供跨语言黄金向量。
- 省略、undefined 与清空不得混淆；清空采用明确 nullable/操作类型，适配现有 settings patch。不得传函数、WebContents、AbortSignal、Error 等运行时对象。
- 握手协商 protocol range、服务端组件能力、客户端展示/输入/资源能力、帧上限及插件协议。客户端不支持视图不等于服务端无能力，也不增加授权。
- 新字段向后兼容，破坏性变更升协议主版本；无交集明确拒绝业务写入。手机独立发版，不能要求随桌面即时升级或静默改变旧协议语义。
- 超时仅停止等待，不等于取消已接受任务；取消查询/传输和 `agent.abort` 分开。换网络/路由只重建连接，不重建任务。

初始预算（工程起点，尚未实测）：活跃连接 15s 心跳/45s 失联，前台 0.5–30s 抖动退避；手机后台不主动保活。控制帧 1 MiB；文本 16–33ms 合批、结构事件及时 flush；事件缓冲 4 MiB 软/8 MiB 硬上限；文件块 256 KiB、有界并行；过期画面替换，慢消费者 resync。默认不开 per-message deflate，压缩收益需实测。

## 5. 配对、安全与中转

### 配对与权限

- 初始化持久化设备信任根和端点证书，私钥严格权限/ACL，管理 API 和日志不返回私钥。
- 单次、5 分钟、至少 256 位随机熵的 enrollment 信息包含设备指纹和连接路由，支持文本/二维码。可信桌面或 CLI 可请求新邀请，不能把已有桌面私钥交给手机。
- 控制端先确认带入的设备身份，再提交自己的公钥；扫码/App Link 只带入邀请，不自动执行操作。限制尝试速率，不以短 PIN 当加密密钥。
- 固定 TLS 1.3 双向认证，分别校验证书用途、身份和有效期。初次 enrollment 只需钉住设备的服务器认证，但该连接只能访问配对端点。
- 自签名证书通过明确的信任根验证，不全局关闭校验。端点证书可在原信任根内轮换；根身份改变需重新配对。
- 桌面密钥用安全存储，手机用 Keychain/Keystore；不暴露到 renderer、插件、普通 JS 存储或 URL。手机重装/丢钥后重新配对，不从云备份复制私钥冒用旧身份。
- 配对授予服务用户级工作台控制，UI 明示影响；Agent 审批、插件权限、工作区归属和视图租约仍逐调用检查，ID 和客户端能力声明不构成授权。
- 支持命名/列出/撤销控制端，撤销作用于存活连接、后续请求、资源流与续租。“断开/撤销”“停止任务”“删数据”分别确认。

### 网络防护

局域网同样认证。提供 local/direct/relay/hybrid 配置，非回环监听必须明确启用，不自动开防火墙、不降为明文。拒绝任意 Electron IPC、未授权 CDP、任意 URL/TCP 代理；限制握手时间、连接/订阅数、帧/集合深度、请求速率和上传大小。保留既有路径/软链/原生组件/插件网络门控；校验 Host/Origin 等而不依赖 CORS。日志只保留必要 actor、方法、资源标识、耗时和结果，不默认记录正文、Cookie、token、命令内容。

### 可自部署中转

- Agent 与控制端均可仅出站 WSS；Agent 保持服务连接，手机不成为常驻网络节点。
- 外层 WSS 管路由和流量，内部 TLS 端点是控制端安全适配与 Agent，中转不见业务明文。Node Duplex 是实现细节，公开协议是跨语言有序字节/路由帧。
- 控制、文件、画面使用独立隧道，不重新复用成一个无界队列。中转仅见必要路由、时长、流量，不承诺隐藏流量特征。
- 中转注册凭据与设备配对分离，不能代替设备认证。重复实例抢同一身份明确拒绝/诊断，不含糊接管。
- relay 重启两端重连，Agent 继续执行。直连完全不依赖 relay、官方登录或官方发现服务。
- 交付独立 Node 产物、容器/反向代理配置、健康检查与 CLI 帮助；不实际购买、部署或发布公网服务。

## 6. 耐久性、幂等与恢复

### 统一命令与存储

手动发送、排队续跑、定时任务、目标唤醒和子代理回报共用服务端启动协调器：验证上下文 → 按 actor＋dataEpoch＋幂等 key 查重并核对参数摘要 → 事务写命令/队列/运行意图与变更 → 提交后返回 accepted/operationId → 驱动 Agent。

查重必须先于“忙则入队”；同 key 同参数返回原结果，参数不同拒绝，避免回执丢失后重复入队。

在 `src/main/db/` 追加迁移与访问器，编号实施时从当前最大版本之后分配：

| 存储 | 内容 |
|---|---|
| command receipts | actor、代际、key、摘要、operationId、accepted/running/succeeded/failed/unknown/interrupted；大结果清理后仍留紧凑去重依据。 |
| domain outbox | 与业务事务同提交的变更、资源、序号、时间；有界重放。 |
| interactions | 审批/提问/计划状态、上下文、处理者/结果，一次性转换。 |
| controller view state | 设备端按控制端/视图保存布局/草稿，避免各端覆盖；共享消息和执行队列不分叉。 |
| mutation/recovery metadata | 外部副作用意图、目标 revision、结果/恢复摘要和中断原因，不承诺任意 shell exactly-once。 |
| notification outbox | 已提交事件派生的通知 ID、注册引用、TTL/投递状态，无正文副本；渠道目标凭据在独立安全状态，不随业务恢复。 |

### 执行规则

- 正式服务关键持久化采用 SQLite WAL＋FULL，提交成功才确认；高频 token/光标/画面/终端输出不逐帧刷盘。
- 消息提交、队列、状态、审批必须持久化；未提交输出保留有界快照，崩溃后未知尾部如实标明。
- 修正“保存失败被吞但仍成功”的回执路径；磁盘满、权限、busy 均可见。事务不包漫长网络等待，重型清理只在安全空闲期。
- 文件采用意图、revision、同目录临时文件、fsync、替换；文件系统与 SQLite 非同事务，崩溃间隙核对目标内容/记录，不能证明则 result_unknown，不盲目重复删除/覆盖。
- PTY/浏览器/Office 输入按实例代际与 inputSeq 去重；跨服务/子进程重启不重放按键或点击。

### 重连与服务重启

- Bootstrap 带明确游标；先建立有界订阅缓冲/高水位，再返回一致边界快照和之后事件，不留快照/订阅空窗。溢出重新快照。
- 仅订阅可见内容，其他用摘要/失效事件。短断线按游标续接；游标裁剪、代际改变明确 resync。
- RunRegistry seq 与 domain cursor 分开，现有稀疏 replay 不能按数组长度推连续性；run 回收后从消息和终态恢复。
- Agent 不等待 UI ACK 才执行；慢消费者只影响自己，丢可替代增量时提供恢复依据。
- 服务重启废止旧视图 token、租约和运行态句柄；未收敛任务标 interrupted/unknown，旧队列/旧 shell/旧批准不自动执行，用户核验后继续。
- 定时任务未来触发照常；错过触发保持现有跳过/记录语义，不补跑已中断 run。正常升级进入 draining，停新调度，等待活动任务结束。

## 7. 多端并发、切换与 UI

### 共享资源

- 一会话一个顶层 run；不同消息按服务端接收顺序排队。编辑/替换历史、删除、压缩遵守服务端互斥。
- 设置按字段/revision 检测冲突，不同字段可合并；文件保存带读取 revision，冲突保留草稿，提供对比、重读、另存，不静默覆盖。
- 审批携带 interactionId、上下文修订及 data/scope 代际；手机与桌面最多一个响应成功，变更内容或已处理的旧批准被拒并同步终态。
- 账号切换是该服务全局配置域变化，所有端看到；保持运行/脏文档时拒绝切账号的闸门。切设备不能触发源设备切账号或停止其运行时。
- 同终端/浏览器/Office 输入租约初始 30s、10s 续租，递增 fencing token 拒绝旧输入；观看不自动获取/续租。租约不决定资源寿命。
- Agent run 与人工视图共用协调器，不抢组字/光标；显式交接、释放或到期后才能换 actor，等待有界并进入可见交接状态，不能因观看永久阻塞 Agent。
- 关闭一个视图只释放该视图；kill 终端、关共享页面、丢弃共享文档是独立动作。

### 全应用切换事务

Desktop Broker 协调同一桌面的全部窗口：
1. 连接/验证 B 的身份、权限、版本、能力和健康；失败保留 A。
2. 冻结本控制台旧绑定的新写入，收集草稿、脏文档、组字与在途 UI 变更。
3. 保存、保留恢复草稿、明确丢弃或取消；未确认操作仍归 A，不能转到 B。
4. 获取 B 的一致快照和订阅边界。
5. 提交 bindingEpoch，整体替换项目、账号、设置、主题、语言、资源代理与 store。
6. 释放 A 的内容订阅及本端输入租约，不停其 Agent/插件/调度。

已接受 Agent 任务不必等完成才能切换。其他控制端不被冻结；旁观者不能因切换而保存/丢弃别人持有输入权的文档。窗口无响应则取消，不允许半切换；新建/重载窗口等待最终绑定。请求、debounce、保存队列和资源加载创建时捕获 binding，回调落 store 前复检代际。

### 桌面与通用状态

设置增加“设备”，本地默认项不可删除；显示别名、用户/主机、系统、连接方式、延迟、版本、组件和最后在线。主界面固定显示“设备 → 项目”；文件、终端、备份/恢复/删除/停服明确目标设备。

当前设备离线只读并标明，设备页仍可用，不自动回退本机执行。桌面首次本地、之后记住本端所选设备；手机无本机 Agent，从添加设备开始。“移除连接”“撤销配对”“停止服务”“删数据”分开。设置源在设备，“跟随系统”主题、窗口按钮、快捷键符号由控制端显示环境派生；手机不支持的字段不能被删。新增文案双语并保持现有可访问性规范。

## 8. 完整桌面工作台适配

“完整”以当前已实现能力为基线，不补做 StubPage/未实现网关，也不假定设备安装了任意第三方桌面软件。

| 能力 | 必须保持的设备端行为 |
|---|---|
| 会话/目标/计划/子代理 | 存储、执行、排队、审批、续跑与恢复；UI 不驱动下一轮。 |
| 模型/配额/搜索/图像视频 | 使用设备凭据与网络，后台收取产物；默认脱敏，显式揭示/导出才返回敏感值。 |
| Skills/Agents/Commands/Hooks/MCP | 扫描设备目录、安装配置、权限与后台进程，不扫描控制端同名目录。 |
| 文件/Git/搜索/变更审查 | 目标文件、Git 凭据、索引、修改恢复；变更由服务端广播，不只靠单窗口 CustomEvent。 |
| 终端/后台 shell | PTY/进程树、缓冲、退出状态与输入租约，UI 重开 attach，不以卸载视图 kill。 |
| 插件/自定义编辑器/卡片/WebApp | 安装、权限、存储、工具、交互均归设备；外部网页型 WebApp 在设备浏览器执行。 |
| 浏览器/Office | 设备上保持真实页面/文档模型，控制台只是展示与受控输入。 |
| 账号/钱包/用量/云同步 | 设备独立登录、刷新和同步；配对不要求官方登录、不自动启用云同步或复制账号。 |
| 导入/备份/清理/主题 | 目标设备目录、资产和设置；明确区分“设备保存”与显式上传/下载到控制端。 |
| 更新/诊断 | 分开显示控制台、Agent 和组件版本，不能混成一个客户端版本。 |

### 文件、附件与预览

- 提炼 SSH 目录选择交互，提供 home/roots、面包屑、父目录、隐藏项、分页、路径输入和建目录；确认工作区使用绑定控制端的短时票据，再 realpath/stat/授权。
- 路径由设备解析，覆盖盘符/大小写、Unicode、权限、断链、网络盘和浏览后目录被替换；保留已有 revision/软链/原生文件防护。
- 删除不得静默降为永久删除；本机系统废纸篓通过宿主适配保留，无头设备明确使用可恢复回收流程。
- 拖拽/粘贴/手机 content URI 经显式上传，完成后返回设备附件引用；控制端路径不能当远端路径。
- 跨端返回 ResourceRef，含设备、dataEpoch、作用域、资源/revision。`ncw://`/插件/widget scheme 只是桌面适配；手机用原生认证资源流。插件 origin/cache 同时隔离设备、账号、插件。
- 旧历史引用在展示适配层补上下文，不批量改写正文。Range、视频 seek、取消、上传清理、磁盘满、同名冲突和撤销均测试。
- localhost 预览优先在设备浏览器打开；显式桌面预览使用工作区/端口限定的 HTTP/WebSocket 转发，不开放任意代理。“显示文件”打开远端文件浏览器；下载/外部打开必须明确，不伪装成原地远程编辑。

### 浏览器

- 从 BrowserBindings 分离通用注册/生命周期与 Electron adapter，服务用独立 Chromium/Playwright；本地设备同样不依赖桌面 webview 存活。
- 页面、Cookie、localStorage、IndexedDB、下载和网络按设备/账号/workspace/profile 隔离；Agent DOM snapshot/CUA 操作同一个页面。
- surface 协议覆盖导航/历史/刷新、frameId、viewport/DPR、键鼠/滚轮、IME、选区/剪贴板意图、上传下载、错误/租约；其他观看者缩放，不反复 resize。
- 画面自适应编码/帧率，隐藏视图停止推画面但不关页面。截图能显示不等于完整浏览器；登录、媒体、交互、文件与必要调试入口必须验证，不开放通用公网 CDP。
- file:// 始终指设备文件，遵守现有协议/路径限制。

### 插件与 Office

- 复用 PluginRuntime 接缝及 bootstrap/import map，新增服务端 Chromium 沙箱实现；不把第三方代码加载到 agentd、普通 Node worker 或非安全 vm。
- 后端插件保持独立 origin/持久存储和进程隔离，启用 Chromium OS 沙箱、非 root，不能用 `--no-sandbox` 解决部署问题。
- 桥身份来自登记的页面/主框架，不信报文 pluginId；保留 CSP、网络白名单、逐调用 capability 与资源预算。后台计时器/工具不依赖视图订阅。
- 插件持久业务数据经设备存储/文档 API。依赖网页执行或 Web Storage 的视图用设备持久 surface 适配；桌面不得成为远程业务存储真源。
- `ncw:doc`、交互/实时卡片、tabs/window 意图绑定 controller/view/作用域。插件 UI runtime 按设备版本提供或明确协商，不任意换成本桌面版本。
- `tabs.openTerminal` 由设备先创建，桌面只 attach；需用户选择/回答的调用存为待处理；无 UI 时不自动同意、不伪报成功。
- Office 的 DocumentSessionManager/NativeProvider 留设备，Agent 与 UI 共用模型；token 绑定控制端/视图/作用域，generation/revision/输入 fencing 拒绝陈旧坐标与输入。
- tile 按可见区、修订和缩放缓存，用按需组件无损编码，不跨公网无界传 RGBA。IME 开始/结束、断线和交接必须成对处理。
- 关闭观看视图不关别人编辑的文档；脏文档保留设备恢复信息。保存继续校验外部文件版本、临时导出和安全发布。
- bundled/示例插件的 tools、卡片、custom editors、WebApp 与四平台真实 Office helper 为回归集合；缺原生发行件或无头失败是发布阻断，不能用 fake/skip 代替。

### 账号与宿主差异

OAuth verifier、换 token、刷新归设备；控制端只处理平台授权页/回调，绑定 controller、设备、scope、redirectUri、过期时间，切设备后不能落入新绑定。不同端登录不覆盖彼此流程，最终账号变更仍过服务全局闸门。

Node Host 必须保持当前手动/系统/PAC、认证、bypass、SOCKS、证书及子进程代理语义，不裸换 fetch。控制台连接代理与设备业务出站代理分开。通知来自设备事件，恢复历史不重复提示音。导入扫描、备份位置、外部程序探测以设备环境为准；纯控制台 OS 动作用明确平台意图，不伪称服务端执行。

## 9. 后续移动端设计约束

### 本期准备与未来 App

本期交付共享客户端、wire/跨语言向量、无窗口 RPC、原生 mTLS/中转/资源互操作验证、通知订阅及可替换投递端口。桌面必须实际使用共享核心。最小 Swift/Kotlin 协议验证程序不等于完整手机 UI 工程，也不决定未来框架。

未来手机首阶段通过同一服务完成：扫码/输入并确认配对、切设备、项目/会话分页搜索、发消息/启动/停止、结构化审批/提问/计划、文件与结果查看/显式分享下载。审批必须展示设备/操作/路径及必要 diff/计划/风险，不另建手机专属审批状态。

手机终端、浏览器、插件和 Office 触屏编辑后续交付；不能展示的交互明确引导支持的控制端处理，不自动批准。手机没有本机 Agent，localhost 指手机；目标仅开放 socket 时必须先启用安全 LAN listener 或中转，不能依赖桌面在线。

### 生命周期、弱网与资源

- 后台、锁屏、进程被回收都视为正常离开，不发 Agent abort；即使系统不给离开回调，服务端依靠断连/租约到期收敛。
- 不依赖后台 WebSocket、永久心跳、silent push 必达或 App 必被唤醒。前台可退避重连，后台停止主动保活。
- 恢复先认证并核对数据/作用域代际，取摘要/pending，再按游标恢复；不恢复旧输入租约。网络/直连中转换路保留命令 key，未确认命令先查回执，不新建 key 重发。
- 草稿按确认修订保存，最后未确认输入可能丢失；不依赖退出时 flush。手机恢复缓存按设备/scope 隔离，不跨设备发送。
- 默认摘要＋当前会话＋可见资源，分页/懒加载，有界缓存。手机失联不等于 Agent 离线。
- ResourceReader 经原生认证读取，再提供受控流/临时文件/平台 URI；不假定 WebView/系统播放器继承客户端证书，不在图片/视频/分享 URL 放长效凭据。
- 照片/文件 URI 仅由本机 UploadSource 读取，上传后使用远端资源 ID；临时文件按设备/scope/revision 隔离并清理。
- 手机 OAuth 采用供应商实际支持的系统认证会话、App/Universal Link 或 Device Authorization；不能假定现有 redirect 已登记，不复制桌面 token。真实渠道接入在移动发布阶段验证。
- 设置仍随设备，手机只作触控/无键盘展示适配，不删除不支持字段。

### 推送预留

- 推送是独立可选通道，不让 relay 解密业务，也不成为执行/连接设备的前提。本期提供 register/update/revoke/status、通知 outbox、PushDelivery 接口和测试投递器；真实渠道未配置须如实返回，不能显示“已开启”。
- 订阅绑定认证 controller、App 安装实例、平台、设备/scope、注册引用/偏好/有效期；只能登记自己，不接受任意收件地址或任意推送内容。渠道目标凭据不进业务备份。
- 通知从已提交事件派生，按 ID 去重/合并，TTL、有限重试和队列预算；推送失败不阻断任务。
- 只发通用提示和不透明引用，不能含正文、路径、命令、Cookie、密钥、Bearer token 或直接批准链接；必要路由信息仅接收端可解。
- 推送可能重复、乱序、迟到、丢失。点击后重新认证取最新状态；已被桌面处理、恢复过备份或已撤销的旧通知不再允许批准。
- token 轮换、卸载/失效 token、用户关闭通知、撤销配对联动；OS 队列里的提示可能无法撤回，但没有访问授权能力。
- 后续 APNs/FCM 凭据在受信推送服务，不放手机包或分发给普通中转用户。真实运营/分发后续明确，不部署官方推送平台，不承诺所有 Android 环境均可用 FCM。

## 10. 数据迁移、备份与回退

### 接管原则与步骤

本地原地接管，不上传中转，不给新远程设备自动复制账号/密钥。迁移前做可验证备份，journal 记录阶段并支持重入；新服务/新版桌面遵守同根锁，旧桌面共用迁移后数据不受支持，不能声称旧版会识别新锁。

1. 解析原 `--user-data-dir`、profile/data、providers、账号与浏览器路径，检查旧进程；仍在运行则拒绝接管。
2. 停新写入、处理任务与脏文档，生成一致性备份并验证可读。
3. 在原 OS 用户环境迁移旧 safeStorage 凭据；失败项保留并要求重认证，不新造 key 覆盖旧密文。
4. 仅追加 schema 和 ownership 标记，迁移控制端布局/草稿；共享执行队列保留一份并保留迁移依据。
5. 创建服务身份、本机端点及控制端信任；控制端密钥与业务密钥分离。
6. 分离 UI Chromium 与设备浏览器/插件资产，保留原 Profile，禁止两进程同时打开同一 Profile。
7. 独立服务打开原数据并通过健康检查后提交切换，Electron 不再持有业务运行时；核对项目/会话/消息/配置/凭据/附件/主题/Skills/MCP/插件/任务。

旧 Electron Profile 保留只读来源，不能直接复制到不同 Chromium 版本写入；在原用户环境迁移可兼容 Cookie/存储，已有 headless profiles 尽量保路径。无法兼容站点明确重登，不清理原件。插件宿主存储按 origin/账号迁移，不混入控制台全局存储。

### 恢复与回退

- 备份区分业务库、配套业务 key、附件/主题/浏览器数据、组件缓存及安全身份；不能只还原密文库而缺 key。
- 普通恢复不覆盖 agent 信任、controller 清单、手机/推送注册，不复活撤销授权；旧通知 outbox 不重新投递。
- 全控制端写屏障 → drain/停调度/关库 → 验证/替换 → 新 dataEpoch → 重新 bootstrap。代际在非还原服务状态持久化，失败/回滚不倒退；未完成时拒业务握手。
- 旧请求、view/resource token、OAuth 回调在新代际失效。克隆业务到另一机器生成新设备身份，不默认复制服务私钥。
- 接管提交前可停新服务并按 journal 恢复旧布局；新版本已写数据后不得直接让旧版开库，必须停服、恢复迁移前备份并提示期间数据不在该备份内。二进制回退和 schema 回退分开验证。

## 11. 服务安装与更新

- 核心独立构建，只带经验证的 Node 24 与匹配原生依赖，不要求服务器安装桌面/开发依赖；本地桌面携带匹配核心并连接服务。
- Linux systemd、macOS launchd、Windows Service，指定普通用户和数据根；安装提权单独提示，运行不变成 root/LocalSystem。开机无人登录的安装使用相应配置，不拿随登录退出的 user service 冒充常驻。
- 提供 install/start/status/stop/uninstall、pair/revoke、组件安装/列出和诊断 CLI；前台诊断模式明确会随进程结束。
- 组件按版本/OS/架构登记，安装有进度、签名/摘要、空间预算、共享下载锁、失败重试和离线预装；不跳过 sandbox/证书保护。
- 退出控制台 detach；停服务明确列活动任务、终端、脏文档影响。卸载默认留数据，删除单独确认。
- 桌面/Agent/组件/手机独立更新。Agent 更新 prepare/download/verify/drain/switch/health-check；任务、脏文档或必要交互进程未处理不强杀。组件不得替换正使用的文件，旧实例结束再回收。
- 不兼容控制端只拒新绑定，不影响既有任务；保留安全诊断/升级入口，不能通过降低认证或改旧请求语义兼容。

## 12. 实施阶段与具体文件

下列为获批后的改动位置，不表示已创建应用文件。`renderer` 简写均指 `src/renderer/src/`；办公三插件为 `plugins/ncw.writer`、`plugins/ncw.sheets`、`plugins/ncw.slides`，独立引擎依赖仓库为 `../ncw-office-runtime`。主线 M0 → M1 → M2 → M3 → M4/M5 → M6 → M7 → M8。最小构建/类型检查在 M1 即建立；M2–M6 只在隔离数据根验证，正式接管依赖 M7，全部门禁通过才发布桌面完整首版。

### M0：高风险验证与能力清单
- 扩展 `src/main/__tests__/architecture.test.ts`、`src/main/document-engine/__tests__/office-runtime.integration.test.ts`、`.github/workflows/ci.yml`。
- 完成全部 IPC 的 controller-local/device/composed 分类；检查可达静态和动态 import，不只查 runtime 直接依赖。
- 验证四平台 Node/PTY/锁/凭据/代理，以及无 DISPLAY Linux、Windows 服务会话、macOS launchd 中的 Chromium sandbox、插件和真实 Office。
- 最小原生协议夹具放 `test/mobile-protocol/ios/AgentProtocolTests.swift`、`test/mobile-protocol/android/` 及必要测试配置，验证证书/密钥句柄、直连、中转内 TLS、Range；它不是手机产品工程。原生方案不可行必须在协议冻结前修正。
- 验证浏览器 surface/媒体/IME、文档 tile；依赖仓库确需修正时单独列 helper/构建/测试修改，不伪造 native target。

**完成条件**：有可重复验证和支持矩阵，不成立的依赖先解决，不自行削减范围。

### M1：协议、共享客户端和存储
- 新增 `src/shared/device/{identity,contract,errors,capabilities,codec,tunnel,notifications}.ts`。
- 新增 `src/controller-client/{client,ports,binding,requests,subscriptions,lifecycle,assets}.ts` 与 `src/transport/node/{channel,direct,relay,secure-tunnel,limits}.ts`。
- 新增 `src/main/services/{request-context,event-hub,errors}.ts`、`src/main/db/{device-commands,device-events,device-interactions,controller-state,device-notifications}.ts` 和 `test/device-protocol-vectors/`。
- 修改 `src/shared/ipc/contract.ts`、`src/shared/agent/{event,interaction,error}.ts`、`src/main/db/{index,schema,repo}.ts`、`src/main/state/store.ts`；建立各入口构建/typecheck。

**完成条件**：校验/幂等/事务/代际/失败回执单测，跨语言向量通过，客户端无 Node/Electron/DOM 传递依赖。

### M2：独立执行服务
- 新增 `src/agent/{entry,bootstrap,host,lifecycle,instance-lock,security-state,network,component-manager}.ts`、`src/main/services/{agent,session,settings,credentials,scheduler}.ts`。
- 修改 `src/main/{entry,index,runtime,session-runtime,agent-shells,quit-flow}.ts`、`src/main/kernel/host.ts`、`src/main/host/index.ts`、`src/main/ipc/{agent,index,settings}.ts`、`src/main/scheduled/{scheduler,bridge}.ts`、`src/main/goal/runtime.ts`、`src/main/secrets/credential-crypto.ts`。

**完成条件**：所有任务来源同协调器，测试控制端离开不停止执行，同根双写被拒，重启人工恢复。真实桌面退出/重连在 M3/M4 接 Broker 后验收。

### M3：Gateway、直连与业务服务化
- 新增 `src/agent/server/{gateway,rpc,events,pairing,assets,uploads,notifications}.ts`、`src/main/device/{manager,transport,registry,credentials}.ts`、`src/main/services/{workspaces,files,accounts,imports,storage,notifications}.ts`、`src/agent/notifications/{delivery,outbox}.ts`。
- 拆分 `src/main/ipc/index.ts` 及 device/composed handler，重点 `client-auth`、`provider-auth`、`config-sync`、`attachment`、`workspace`、`workspace-files`、`storage`、`import`。
- 适配 `src/main/account-switch.ts`、`src/main/imports/{service,sync}.ts`、`src/main/net/proxy.ts`、`src/preload/{index.ts,index.d.ts}`。

**完成条件**：未知方法拒绝、认证/授权完整，独立账号刷新，直连不依赖 relay/官方账号；无窗口客户端能任务/审批/资源，通知未配置诚实返回且不阻断执行。

### M4：设备页和原子切换
- 新增 `src/main/device/switch-coordinator.ts`、renderer `services/device.ts`、`stores/device.ts`、`settings/pages/DevicesPage.tsx`、`shell/DeviceSwitcher.tsx`。
- 修改 renderer `App.tsx`、`shell/AppShell.tsx`、`services/ipc.ts`、`settings/{nav.ts,icons.ts,SettingsOverlay.tsx}`；Broker 实际消费 ControllerClient，不另保留桌面专用业务协议。
- 审计 renderer stores：`session/window/tabs/documents/document-saves/file-tree/models/mcp/skills/plugins/websearch/video-jobs/themeProfiles/imageTheme/notification-sounds`；修改 `src/shared/domain/bootstrap.ts` 及双语文案。

**完成条件**：多窗口一致切换，迟到保存/响应不串设备，账号/主题随设备，离线不回退本机写入。

### M5：文件、资源和共享控制
- 新增 `src/main/services/{directory-browser,resource-leases,terminals}.ts`、`src/main/device/resource-proxy.ts`、`src/agent/server/preview-tunnel.ts`、`src/renderer/src/components/DeviceDirectoryPicker.tsx`。
- 修改 `src/renderer/src/shell/CreateSshWorkspaceDialog.tsx`、`src/main/{terminal-host,agent-shells}.ts`、`src/main/environment/{files,local,contract}.ts`、`src/main/net/{attachment-protocol,widget-protocol}.ts`、`src/main/plugin/protocol.ts`。
- 适配 renderer 文件服务/动作、拖放、文件引用和终端生命周期，服务端广播变化。

**完成条件**：远端选目录/文件/Git、Range/seek/上传恢复、终端续接、输入 fencing、预览与资源隔离端到端通过。

### M6：浏览器、插件和 Office
- 新增 `src/agent/browser/{bindings,surface,profiles}.ts`、`src/main/plugin/host-headless.ts`、`src/agent/media/{codec,tile-stream}.ts`、`src/renderer/src/views/browser/RemoteBrowserSurface.tsx`。
- 修改 `src/main/browser/{bindings,headless,manager,runtime,session}.ts`，renderer `views/browser/{BrowserView,BrowserFeature}.tsx`。
- 修改 `src/main/plugin/{host-window,protocol,manager,rpc,document-rpc,document-view}.ts`、`src/main/ipc/{plugins,document-engine}.ts`、`src/main/document-engine/{manager,native-host,provider-registry,file-store}.ts`、`src/shared/document-engine/{view,interaction,protocol}.ts`。
- 修改 renderer `shell/PluginViewFrame.tsx`、plugin-ui 的 view/DocumentCanvas/engine-document，`scripts/build-plugin-runtime.mjs`，办公三插件仅必要协议适配。

**完成条件**：桌面离开后浏览器/插件工具仍执行，全部已有插件能力可用；真实 Office 多端观看/交接/保存，Agent/UI 共用模型。

### M7：中转、迁移与服务管理
- 新增 `src/relay/{entry,server,auth,routes,limits}.ts`、`src/agent/relay-connector.ts`、`src/main/device/relay-transport.ts`、`src/agent/install/`、`src/agent/update/`、`src/main/db/device-migration.ts`、`deploy/relay/` 配置。
- 修改 `src/main/db/{startup-migration,chromium-layout,flat-layout}.ts`、`src/main/ipc/{storage,data-migration,app}.ts`、`src/main/update/update-service.ts`、`src/main/index.ts` 与设备管理 UI。

**完成条件**：中转无明文，非 Node 原生夹具可经中转操作且不依赖桌面；relay 重启不停任务，迁移可核验/回退，四平台指定用户服务可运行。

### M8：构建、完整回归与发行加固
- 新增/完善 `scripts/{build-agent,build-relay,device-e2e-probe,device-network-bench,controller-protocol-probe}.mjs`、`tsconfig.{agent,relay,controller-client}.json`，共享客户端测试及原生协议夹具。
- 修改 `package.json`/lock、`electron.vite.config.ts`、`electron-builder.yml`、`.github/workflows/{ci,release}.yml`。新增命令先登记再用，检查实际 Xcode/Android SDK，不假定工具链存在。
- 核心产物不带 Electron/UI/测试/开发依赖，Node 与 Electron 的 node-pty ABI 分离；共享客户端不能靠补 Buffer/process/window 假装兼容。

**完成条件**：下节全部门禁通过。本期不自动 commit、push、上传发行包或部署公网服务。

## 13. 验证与发布条件

### 命令和测试环境

已确认仓库命令：
```text
npm run typecheck
npm run lint
npm test
npm run build
npm run e2e
npm run e2e:office
npm run test:office-native
npm run test:release
```
新增 Agent/relay/client/interop 命令登记后执行。Office 使用真实 `NCW_OFFICE_RUNTIME` 或 `NCW_OFFICE_RUNTIME_ZIP` 及 `NCW_OFFICE_FIXTURES`，发布 required 模式不能以 skip 通过。所有基础验证使用临时根、测试密钥和确定性模型，不动真实 `.next-cowork`、不调用付费模型。

### 自动化门禁

- 架构：Agent/relay 无可达 Electron/renderer；客户端无 Node/Electron/DOM/Broker 依赖；网络分类完备，桌面不再写业务库。
- 协议/存储：schema、清空语义、版本、载荷、游标/稀疏 replay、错误、先查重再入队、参数冲突、outbox 原子性、磁盘满/回滚/重启。
- 安全：错误指纹、过期/重复配对、伪造 actor、撤销、越权资源、旧 dataEpoch、插件跨 origin、路径/原生组件和 relay 限制。
- 并发/迁移：会话互斥、设置/文件冲突、审批单次、输入 fencing/IME、迁移重入/数量/凭据/旧进程、恢复不复活授权。
- 真组件：四平台 PTY/Chromium sandbox/插件/Office；原生 iOS/Android 直连/中转/mTLS/Range 互操作。Node 成功、mock、假 helper 不代替真实验证。
- 移动预留：本期测试状态机暂停/恢复及通知端口；后续 App 发布另验真实系统后台、耗电、APNs/FCM、平台分发。模拟通知不等于渠道上线。

### 必测端到端故障

1. A 开主/子 Agent、后台 shell、定时任务 → 切 B → 退出桌面 → 重连 A：执行未因 UI 离线中断，结果可查。
2. A/B/控制端放同名同路径文件，所有读写、上传、插件、浏览器只碰所属设备。
3. 命令提交前、提交后回执前、结果回传中断线，同 key 不重复运行或入队。
4. 文件替换后结果记录前杀服务，恢复核验/unknown，不重删重写。
5. 大文件、视频 seek、浏览器流和 Agent 文本并行，控制/审批仍及时。
6. 两桌面同时发送、写文件/设置、批同一操作，队列/冲突/单次审批正确。
7. 租约交接后旧端复网，迟到按键/光标/Office 输入被拒，资源不重启。
8. 窗口挂起/重载/新建穿插切换，不半切换，不将 A debounce 保存到 B。
9. 快照交接持续变更，不漏终态、不重复拼正文；过期游标明确 resync。
10. relay 重启、断直连、换路、证书不符分别测；身份与幂等 key 不随路由变。
11. 服务重启显示 interrupted/unknown，旧队列/shell/批准不自动执行。
12. 恢复备份后旧 token/URL/回调失效，授权及手机推送不复活。
13. 浏览器登录/存储、媒体、上传下载、IME、file://；重登提示保原件。
14. 插件 tools/卡片/custom editor/WebApp、真实 Office 文档/表格/演示编辑保存，后台不依赖 UI。
15. 桌面更新不停 Agent；服务更新等待任务/脏文档，失败不丢库、不双启动。
16. 所有桌面关闭，原生手机协议端仍可配对、提交、看结果、审批。
17. 手机后台/锁屏/无离开回调、复网和蜂窝切换：任务不断、租约到期、恢复不重复发送/抢输入权。
18. 手机与桌面抢审批、迟到通知、未配置/重复/过期推送、token 轮换/撤销：最新状态为准，通知无敏感正文或授权。
19. 无 Window/ncw:// 的端读取文件/图片/Range，本机 content URI 不成为设备路径。
20. 老控制协议在声明兼容窗口内正确工作，超出窗口拒绝而不改语义、停任务或降安全。

### 性能目标（待实测，不是已达成）

| 场景 | 初始验收目标 |
|---|---|
| 空闲核心 | 无浏览器/插件/Office 进程，CPU 长期均值 <单核 1%，RSS 预算 150 MiB；分别报告核心包与全组件安装量。 |
| 事件/控制 | 合批附加延迟 p95 ≤50ms；受控带宽并发负载下控制传输/调度 p95 目标 ≤RTT＋100ms，不包含外部工具实际停止耗时。 |
| 切换 | 已连、LAN RTT ≤5ms、分页首屏下提交到可用 p95 ≤1s，首次配对/安装另计。 |
| 文件/多视图 | 内存不随整文件增大，同 TLS 基线比吞吐；共享 tile/帧生成，无观看停止推送/编码。 |
| 移动/长期 | 100–300ms RTT、断线/限带宽有界恢复，后台不保活；24h 压测无事件缓冲/句柄/PTY/helper 线性泄漏。手机真机耗电后续测。 |

未达目标需优化或明确评审调整，不能把协议选型当成性能通过。

### 阻断项与非目标

任一能力偷跑控制端、执行寿命绑窗口、网络变更缺校验/幂等、同根双写/切换/恢复隔离无法验证、中转见明文或可换身份、迁移丢原件/凭据、真实组件/原生安全通道未验，均阻断发布。桌面首版浏览器/插件/Office 不得削为后续；手机需要桌面网关、客户端核心绑 Node/Electron、执行依赖手机保活/推送必达同样阻断。

不包含：官方托管/购买/公网部署；P2P/QUIC/全盘同步/任意远程桌面；多租户与多账号同时激活；逐字符协作或崩溃自动续跑；自动复制密钥/启云同步；保证所有网站登录无损；补完无关占位/清理代码/全仓升级。

手机完整 UI、框架选型、触屏工作台、商店上架、真实推送运营与手机 OAuth 注册属于后续交付；本期只有上述共享接口、状态机、测试和最小原生验证。PWA 不在兼容范围，未来加入须重新评估浏览器认证/端到端通道，不降低当前安全边界。

本计划是唯一实施依据。当前仅调查代码并编写计划，未修改应用代码、运行测试、安装组件或部署服务。完成标准是完整工作台、可验证的身份/写入边界、断线不影响执行、故障状态诚实可恢复，并为独立手机控制端保留经过验证的接入路径。
