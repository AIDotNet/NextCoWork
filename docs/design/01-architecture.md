# 01 · 整体架构

## 1. 进程模型

```
┌──────────────────────── NextCoWork 3.0 控制端(Avalonia + TKCode.Arc)────────────────────────┐
│  Shell(桌面三栏 / 平板双栏 / 手机导航栈)                                                         │
│  Views ── 只放纯 UI 状态                                                                         │
│  ViewModels(CommunityToolkit.Mvvm)── 只读 Store,通过 Actions 发起操作                           │
│  Stores ── 每个运行时事实在这里只有唯一一份镜像                                                    │
│  Ncw.Core(共享)+ Ncw.Protocol(共享)                                                          │
└───────────────┬──────────────────────────────────────────────┬─────────────────────────────────┘
                │ 本机通道                                        │ 远程通道
                │ 本机令牌(Unix socket / 命名管道)                  │ 局域网 IP:端口 + TLS 1.3(证书钉扎);以后加服务器中继
        ┌───────▼─────────┐                             ┌───────▼────────┐        ┌──────────────┐
        │ ncw-agent(本机)  │  ← 随桌面安装包分发          │ ncw-agent(远程) │ ←──→   │ ncw-relay    │
        │ .NET 10 NativeAOT│    由桌面端负责拉起和守护      │ 另一台电脑的     │        │(nextco.work)│
        │ SQLite / PTY     │                             │ 另一台电脑        │        └──────────────┘
        │ Ncw.Core(共享)   │                             └────────────────┘
        └─────────────────┘                                     ▲
                                          手机端只有远程通道 ──────┘(局域网直连;离开局域网走中继)

                                                        ┌──────────────────────────────────┐
                              远程通道(中继 + 隧道内 TLS)──────▶│ ncw-agent(云端,nextco.work 托管) │
                                                        │ 每个用户一个隔离沙箱,同一个二进制    │
                                                        └──────────────────────────────────┘
```

要点:

- **桌面端 = 控制端 + 本机 Agent 的守护者**。桌面端负责拉起 sidecar、做健康检查、崩溃后重启,退出时让它优雅收尾。
  sidecar 也可以设置为「关窗后继续运行」,和 2.x 的托盘常驻行为一致。
- **手机端 = 纯控制端**。设备可以是用户电脑上的本机 Agent:桌面端打开「允许远程连接」后,局域网里的手机或另一台电脑输入 `IP:端口` 和配对码(或扫码)即可连接,
  也可以是 nextco.work 上的云端 Agent。3.0 不做独立部署的设备服务。
- **云端 Agent 也是一台「设备」**。登录账户后自动出现在设备列表里,不需要扫码配对(见 [02 A6](02-agent.md#a6-云端-agent))。
- **一个控制端可以同时连多台设备**,项目栏里的项目来自哪台设备都可以。
- 本机通道和远程通道只是承载(transport)不同,上层的 RPC、事件、交互语义完全一样。
- 本机 sidecar 和云端 Agent 是**同一个 `ncw-agent` 二进制**,用 `--mode local|cloud` 区分。
  这些启动参数只给桌面端和云端编排调用,不是面向用户的命令行。

## 2. 解决方案结构(本分支)

```
/
├─ AGENTS.md / README.md
├─ Directory.Build.props / Directory.Packages.props   # 集中管理包版本
├─ NextCoWork.slnx
├─ src/
│  │  ── 共享(客户端和 Agent 都引用)──
│  ├─ Ncw.Core/                  # 领域模型、派生纯函数(例如子代理视图、状态合并)、ID、错误码
│  ├─ Ncw.Protocol/              # ★ 协议唯一真源:帧、方法表、事件、编解码(MessagePack)、TLS 承载、配对、中继客户端(见 09)
│  │
│  │  ── 客户端 ──
│  ├─ Ncw.Client/                # Stores、ViewModels、Actions、连接管理(和平台无关)
│  ├─ Ncw.UI/                    # Avalonia 视图 + 对 Arc 的薄封装(Ncw.UI.Controls)
│  ├─ Ncw.Desktop/               # 桌面宿主:窗口、托盘、sidecar 管理、Velopack 更新、系统集成
│  ├─ Ncw.Android/               # Android 宿主:FCM、Keystore、分享入口
│  ├─ Ncw.iOS/                   # iOS 宿主:APNs、Keychain、Face ID
│  │
│  │  ── Agent ──
│  ├─ Ncw.Agent.Storage/         # SQLite、迁移(兼容 2.x schema)、NCK1 凭据加密、在线备份、接管(takeover)
│  ├─ Ncw.Agent.Llm/             # 模型层(对应 pi-ai),详见 08-agent-runtime/01:OpenAI Responses/Chat、Anthropic Messages、Gemini 等;SSE;账户池;配额;模型目录(models.dev 定时更新)
│  ├─ Ncw.Agent.Environments/    # 执行环境:IExecutionEnvironment、Local、SSH(SSH.NET、ssh_config、跳板机、ssh-agent、SFTP)
│  ├─ Ncw.Agent.Kernel/          # Loop(回合循环)/ Session(上下文、压缩、落库)/ Runs(编排、子代理、队列、目标),详见 08-agent-runtime
│  ├─ Ncw.Agent.Tools/           # 内置工具:文件、Shell、Git、搜索、浏览器、图片和视频生成;路径守卫
│  ├─ Ncw.Agent.Extensions/      # 扩展接口 + 一方扩展:权限、Hooks、计划模式、待办、目标、子代理、Skills、MCP
│  ├─ Ncw.Agent.Plugins/         # 插件:清单、权限、生命周期;Jaunt 宿主(realm、预算、宿主 API 绑定)
│  ├─ Ncw.Agent.Terminal/        # PTY:Unix forkpty、Windows ConPTY
│  ├─ Ncw.Agent.Host/            # 服务:RPC 路由、事件流、配对、租约、推送、开放网关(Kestrel 最小 API)
│  ├─ Ncw.Agent/                 # 可执行入口(NativeAOT),local / cloud 两种模式
│  │
│  │  ── 服务端 ──
│  └─ Ncw.Relay/                 # 中继服务(ASP.NET Core),全新设计,随服务器上线(见 09 §9)
├─ tools/
│  ├─ Ncw.Build/                 # 构建、签名、公证、Velopack 打包
│  └─ Ncw.Release/               # 上传发布产物(替代 upload-client-release.mjs)
├─ tests/
│  ├─ Ncw.Core.Tests/            # 派生函数、状态单调
│  ├─ Ncw.Protocol.Tests/        # 按协议版本冻结的黄金向量、配对(含中间人)、续连、重同步、本机和局域网承载的集成测试
│  ├─ Ncw.Client.Tests/          # Store 对账、多设备断线
│  ├─ Ncw.Agent.*.Tests/         # 各模块单测(从 2.x 的 vitest 用例移植)
│  ├─ Ncw.Agent.Parity.Tests/    # ★ 回放对照:同一份录制输入,C# 和 2.x 的事件序列、落库结果必须一致
│  └─ Ncw.UI.Tests/              # Avalonia.Headless 截图与交互测试
├─ fixtures/                     # 从 2.x 录制的上游 SSE 流和工具结果(只用于 Agent 行为对照,不涉及设备协议)
└─ build/                        # 打包:Velopack、NativeAOT、签名与公证、移动端流水线
```

**依赖规则**:

- 客户端项目只能引用 `Ncw.Core` 和 `Ncw.Protocol`,**不能**引用任何 `Ncw.Agent.*`。手机包里不能混进 Agent 代码。
- Agent 项目不能引用 Avalonia,也不能引用任何客户端项目。
- `Ncw.Core` 和 `Ncw.Protocol` 不引用任何平台相关的包。
- 全部项目都要能在 NativeAOT 下裁剪,发布时零裁剪警告。
- **仓库里不引入 Node / TypeScript 运行时依赖**。需要执行 JS 的地方只能经过 `Ncw.Agent.Plugins` 里的 Jaunt 宿主。

## 3. 协议(Ncw.Protocol)

协议是 3.0 全新设计,不参考、不兼容 2.x,完整设计见 [09 设备协议](09-protocol.md)。要点:

- 客户端和 Agent 直接共享 `Ncw.Protocol` 里的同一份 C# 类型,不需要代码生成。
- 分层:承载(Unix socket / 命名管道 / 局域网 TCP / 以后的服务器中继)→ 安全(本机靠操作系统权限;远程是 TLS 1.3 双向认证,首次配对用配对码 + SRP-6a)
  → 帧(长度前缀 + MessagePack)→ 会话(hello / welcome、续连)→ 应用(方法、订阅、交互)。
- 3.0 先做局域网直连(设备上打开远程开关,其他机器用 `IP:端口` 连接);服务器中继以后上线,只新增一种承载,协议不变。
- 订阅带游标,续不上就给权威快照;`device` 主题的快照就是活动 Run 的权威广播。
- 版本协商:3.x 的客户端和 Agent 都同时支持协议的 **N 和 N-1** 两个版本。
- 对第三方:由方法表导出 JSON Schema;开放网关是方法表的 HTTP 映射,导出 OpenAPI。

## 4. Agent 移植策略

### 规模

2.x 里 Agent 侧的 TypeScript 代码粗略统计约 6.5 万行(不含测试):`kernel` 3.5 万、`db` 1.2 万、`plugin` 0.8 万,
另有 agent server、environment、services、transport、mcp 等,外加 `shared` 里的一部分。**这是 3.0 最大的一块工作量。**

### 依赖替换

| 2.x(Node) | 3.0(.NET) | 备注 |
|---|---|---|
| `node:sqlite` | Microsoft.Data.Sqlite(SQLitePCLRaw 内置 e_sqlite3) | 在线备份用 `BackupDatabase` |
| `node-pty` | 自己写 P/Invoke:Unix `forkpty`,Windows ConPTY | 先评估现成的库,不合适再自研 |
| `@modelcontextprotocol/sdk` | ModelContextProtocol 官方 C# SDK | 要验证 NativeAOT 兼容性 |
| `ssh2` + 系统 OpenSSH + askpass | SSH.NET | 执行环境,见 [06](06-environments-ssh.md);要验证 AOT |
| `playwright-core` | PuppeteerSharp(纯 C# 的 CDP 客户端) | 不用 Microsoft.Playwright,因为它自带 Node 驱动。Chromium 像 2.x 一样按需下载 |
| `undici` / `ws` | HttpClient(SocketsHttpHandler)/ ClientWebSocket | |
| `zod` | System.Text.Json 源生成 + 手写校验 | |
| `@msgpack/msgpack` | MessagePack-CSharp(源生成) | |
| `smol-toml` | Tomlyn | |
| `@peculiar/x509` / `node:crypto` | System.Security.Cryptography | |
| `unzipper` | System.IO.Compression | |
| 插件宿主(BrowserWindow + iframe + MessagePort) | **Jaunt**(`JsEngine` / `JsPlugin`,每个插件独立 realm) | 见 [02 A7](02-agent.md#a7-插件与-js-执行jaunt) |
| `sucrase`(加载时擦除插件的 TS 类型) | 在 Jaunt 里运行 sucrase(它是纯 JS),结果按内容哈希缓存 | 要验证 sucrase 在 Jaunt 上的兼容性和速度 |
| 构建和发布脚本(`scripts/*.mjs`) | `tools/` 下的 C# 单文件程序 | D16;2.x 的 `plugin-cli` 不移植 |
| `src/relay`(TS 中继) | `Ncw.Relay`(ASP.NET Core) | 全新设计,不兼容 2.x,全新部署 |

### 顺序

1. **Storage**:兼容 2.x schema、NCK1 凭据、备份、接管。验收:能打开一份真实的 2.x 数据根,读出全部会话。
2. **Protocol 和 Host 骨架**:本机和局域网承载、TLS 双向认证、远程开关与配对、会话、方法路由、订阅。验收:本机连接,以及局域网里另一台电脑用 IP:端口 + 配对码连接,都能列出项目。
3. **Llm(模型层)**:各家上游的编解码和 SSE。验收:回放 2.x 录制的上游流,解码结果一致。
4. **Kernel 主循环**:会话、Run、上下文组装、工具调用循环、权限与审批、交互闸门。
5. **Tools**:文件、Shell、Git、搜索、终端。
6. **子代理、压缩、计划模式、待办、目标**。
7. **Skills、Hooks、MCP、定时任务、插件(Jaunt 宿主)**。
8. **开放网关、图片和视频生成、浏览器工具**。

### 怎么保证行为一致

- **回放对照**(`Ncw.Agent.Parity.Tests`):在 2.x 上录制上游流、工具结果和用户操作(2.x 已有 `fake-emitter` 和 demo 上游可以复用),
  分别用 C# 和 TS 回放,比较两边的事件序列和落库结果。
- **单测移植**:2.x 的 vitest 用例逐个改写成 xUnit,特别是状态相关的几组(subagent-view、subagent-monotonic、
  subagent-sync、active-run-broadcast)。
- **对照清单**:`docs/parity.md` 逐模块记录移植状态。2.x 在此期间修的 bug 也要登记,并同步到 C# 版。

## 5. 状态统一管理(C# 版)

2.x `AGENTS.md` 的七条规则在 3.0 里照搬,C# 里的对应位置如下:

| 规则 | 3.0 落点 |
|---|---|
| 一个事实只有一个真源 | 真源在 Agent。控制端每个事实只在 `Ncw.Client/Stores/` 的一个 Store 里有唯一一份镜像 |
| 视图不持有业务状态 | View / code-behind 只能放纯 UI 状态。ViewModel 不自己订阅协议事件,只读 Store |
| 派生只有一个函数 | 状态文案、耗时、能否停止、能否打开……都是 `Ncw.Core/Domain/` 下的 `static` 纯函数。**Agent 和客户端调用的是同一个函数** |
| 动作只有一份 | 用户意图集中在 `Ncw.Client/Actions/`(例如 `RunActions.StopSubagent`),多个入口都调用同一个方法 |
| 能从丢消息中自我收敛 | 每台设备定期推一次全量 `activeRuns` 广播(runs + subagents),Store 收到后对账:多出来的补上,缺少的在宽限期后向设备核实再收口。首帧快照和后续广播用同一个类型(定义在 `Ncw.Protocol`)、同一个生成函数(在 Agent 侧) |
| 状态单调 | 合并实时遥测、库里的回执和重放事件时,终态压过 running。合并函数放在 `Ncw.Core`,两端共用 |
| 派生视图跟随真源 | 子会话、手机端详情页的运行态都跟随父会话里的那条记录 |

**多设备带来的新问题**:Store 的 key 一律是 `(deviceId, …)`。设备断开时,该设备的所有「运行中」状态标为
`unknown`(界面显示「连接中…」),**不能**直接判成结束,也不能继续计时。等重新连上、对账完成后再收口。
