# 01 · 整体架构

## 1. 进程模型

```
┌──────────────────────── NextCoWork 3.0 控制端(Avalonia + TKCode.Arc)────────────────────────┐
│  Shell(桌面三栏 / 平板双栏 / 手机导航栈)                                                         │
│  Views ── 只放纯 UI 状态                                                                         │
│  ViewModels(CommunityToolkit.Mvvm)── 只读 Store,通过 Actions 发起操作                           │
│  Stores ── 每个运行时事实在这里只有唯一一份镜像                                                    │
│  Ncw.Protocol(C# 协议 SDK):编解码 / 配对 / E2E / 直连 / 中转 / RPC / 带游标的事件流              │
└───────────────┬──────────────────────────────────────────────┬─────────────────────────────────┘
                │ 本机通道                                        │ 远程通道
                │ 127.0.0.1 + 本机令牌(Unix socket / 命名管道)      │ 直连 TLS(证书钉扎)或 中转 + E2E
        ┌───────▼────────┐                              ┌───────▼────────┐        ┌──────────────┐
        │ ncw-agent(本机) │  ← 随桌面安装包分发           │ ncw-agent(远程) │ ←──→   │ ncw-relay    │
        │ Node 24 + 内核   │    由桌面端负责拉起和守护       │ NAS / 服务器 /   │        │(nextco.work)│
        │ SQLite / PTY    │                              │ 另一台电脑        │        └──────────────┘
        └────────────────┘                              └────────────────┘
                                                                ▲
                                          手机端只有远程通道 ──────┘(配对后直连或走中转)
```

要点:

- **桌面端 = 控制端 + 本机 Agent 的守护者**。桌面端负责拉起 sidecar、做健康检查、崩溃后重启,退出时让它优雅收尾。
  sidecar 也可以设置为「关窗后继续运行」,和 2.x 的托盘常驻行为一致。
- **手机端 = 纯控制端**。通过扫码配对添加设备。设备可以是用户电脑上的本机 Agent(桌面端打开「允许远程控制」),
  也可以是单独部署的 ncw-agent。
- **一个控制端可以同时连多台设备**,项目栏里的项目来自哪台设备都可以。
- 本机通道和远程通道只是承载(transport)不同,上层的 RPC、事件、交互语义完全一样。

## 2. 解决方案结构(本分支)

```
/
├─ AGENTS.md / README.md
├─ Directory.Build.props / Directory.Packages.props   # 集中管理包版本,Arc 和 Avalonia 版本只写在这里
├─ NextCoWork.slnx
├─ protocol/                         # ★ 设备协议唯一真源
│  ├─ schema/*.json                  #   JSON Schema:帧、方法入参/出参、事件、错误码
│  ├─ methods.yaml                   #   方法表:名称 / 种类(query|transaction|execution)/ 能力要求
│  └─ vectors/                       #   黄金向量:编解码、E2E 握手、游标续传,TS 和 C# 两边都要跑通
├─ src/
│  ├─ Ncw.Core/                      # 纯 .NET:领域模型、派生纯函数(对应 2.x 的 shared/)、Store
│  ├─ Ncw.Protocol/                  # 协议 SDK(由 protocol/ 生成类型,手写传输与加密)
│  ├─ Ncw.Client/                    # ViewModels、Actions、服务接口(和平台无关)
│  ├─ Ncw.UI/                        # Avalonia 视图 + 对 Arc 的薄封装(Ncw.UI.Controls)
│  ├─ Ncw.Desktop/                   # 桌面宿主:窗口、托盘、sidecar 管理、Velopack 更新、系统集成
│  ├─ Ncw.Android/                   # Android 宿主:FCM、Keystore、分享入口
│  └─ Ncw.iOS/                       # iOS 宿主:APNs、Keychain、Face ID
├─ agent/                            # Agent 运行时(TypeScript),从 2.x 迁入,见 §4
├─ tests/
│  ├─ Ncw.Core.Tests/                # 派生函数、Store 对账、状态单调
│  ├─ Ncw.Protocol.Tests/            # 黄金向量、断线续传、E2E
│  └─ Ncw.UI.Tests/                  # Avalonia.Headless 截图与交互测试
└─ build/                            # 打包:Velopack、Node 运行时下载、签名、移动端流水线
```

依赖方向只能向下:`UI → Client → Core ← Protocol`。`Core` 不引用 Avalonia,宿主项目只负责装配。

## 3. 协议 SDK(Ncw.Protocol)

2.x 已经有完整的设备协议,3.0 在它的基础上演进,不推倒重来:

| 层 | 2.x 现状(TS) | 3.0 C# 实现 |
|---|---|---|
| 值与编解码 | `shared/device/value.ts`、`codec.ts`(msgpack) | MessagePack-CSharp,用源生成器,兼容 AOT |
| 帧 | `contract.ts`:hello / welcome / request / response / event | 从 `protocol/schema` 生成 record |
| E2E 加密 | `transport/node/e2e.ts`:P-256 ECDH + HKDF + AES-256-GCM,计数严格连续 | `System.Security.Cryptography`(ECDiffieHellman、HKDF、AesGcm),黄金向量逐字节对齐 |
| 配对 | `pairing.ts`、`invitation.ts`(`ncwpair1.…` 邀请串) | 同格式,手机端支持扫二维码 |
| 中转 | `relay-client.ts` + `src/relay` | WebSocket 客户端 |
| RPC | `rpc.ts`:query / transaction / execution 三类,执行类先落回执 | 同语义;execution 必须带 idempotencyKey |
| 事件流 | 带 `cursor` / `previousCursor` 的事件 | 断线后按游标续传;游标对不上时退回全量快照 |

协议版本协商:控制端同时支持协议的 **N 和 N-1** 两个版本,这样单独部署的设备服务晚一版升级也能连。

## 4. Agent 代码的来源

本分支是孤儿分支,Agent 代码需要迁入。建议做法:

1. 以 2.x 某个打了 tag 的提交为基线(例如 `v2.9.0`),把 Agent 相关目录拷到 `agent/` 下:
   `src/agent`、`src/main/kernel`、`src/main/db`、`src/shared`、`src/transport`、`src/relay` 以及它们的依赖。
   在 `agent/UPSTREAM.md` 里记下基线提交号。
2. 拷进来之后,切断对 Electron 的所有引用(无界面的 host 已经做了大半,见 `src/main/host/headless.ts`)。
3. 2.x 进入维护期。期间修 Agent 内核 bug 的提交,要按 `UPSTREAM.md` 的记录 cherry-pick 到本分支。

备选方案是直接依赖已发布到 npm 的 `@aidotnet/nextcowork-device-service` 固定版本,本分支不放 Agent 源码。
它的问题是 3.0 需要改 Agent(项目组、推送、能力协商、移动端节流),改动要在两个仓库之间来回发版。
所以不推荐,列为[待定问题](05-roadmap.md#5-待定问题) Q1。

## 5. 状态统一管理(C# 版)

2.x `AGENTS.md` 的七条规则在 3.0 里照搬,C# 里的对应位置如下:

| 规则 | 3.0 落点 |
|---|---|
| 一个事实只有一个真源 | 真源在 Agent。控制端每个事实只在 `Ncw.Core/Stores/` 的一个 Store 里有唯一一份镜像 |
| 视图不持有业务状态 | View / code-behind 只能放纯 UI 状态。ViewModel 不自己订阅协议事件,只读 Store |
| 派生只有一个函数 | 状态文案、耗时、能否停止、能否打开……都是 `Ncw.Core/Domain/` 下的 `static` 纯函数 |
| 动作只有一份 | 用户意图集中在 `Ncw.Client/Actions/`(例如 `RunActions.StopSubagent`),多个入口都调用同一个方法 |
| 能从丢消息中自我收敛 | 每台设备定期推一次全量 `activeRuns` 广播(runs + subagents),Store 收到后对账:多出来的补上,缺少的在宽限期后向设备核实再收口。首帧快照和后续广播用同一个类型 |
| 状态单调 | 合并实时遥测、库里的回执和重放事件时,终态压过 running。`Ncw.Core` 里有对应的单测 |
| 派生视图跟随真源 | 子会话、手机端详情页的运行态都跟随父会话里的那条记录 |

**多设备带来的新问题**:Store 的 key 一律是 `(deviceId, …)`。设备断开时,该设备的所有「运行中」状态标为
`unknown`(界面显示「连接中…」),**不能**直接判成结束,也不能继续计时。等重新连上、对账完成后再收口。
