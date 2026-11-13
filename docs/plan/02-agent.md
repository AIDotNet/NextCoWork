# 02 · Agent 设计

本篇分两部分:**A. Agent 运行时**(服务端如何做到通用,适配多种客户端,尤其是移动端)和
**B. Agent 交互界面**(会话界面如何在桌面和手机上共用一套组件)。

---

## A. Agent 运行时

### A1. 一个运行时,三种部署

| 部署形态 | 谁来拉起 | 典型场景 | 3.0 范围 |
|---|---|---|---|
| **本机内置** | 桌面端(sidecar) | 普通用户,开箱即用 | ✅ |
| **独立设备服务** | systemd / LaunchDaemon / WinSW | NAS、开发服务器、关机也要跑的任务 | ✅(2.x 已有) |
| **云端托管** | nextco.work | 手机用户没有自己的电脑可连 | ❌ 预留 |

三种形态是**同一个二进制、同一套协议**,差别只在启动参数和数据根。

### A2. 对客户端类型无感知

- 运行时不对界面做任何假设:不存在「只有渲染层才知道」的状态。2.x 里组件内 `useState` 维护运行时数据的做法,
  在 3.0 一律改到 Agent 侧,通过协议暴露出来。
- **交互(审批、提问、计划确认)是协议对象**:有 id、有版本号。任何已连接的控制端都可以回答,
  先到先得,后到的返回 409。界面上要显示「已在另一台设备上处理」。
- **多控制端并发**:同一台设备允许多个控制端同时连接。独占型操作(终端输入、编辑同一个文件)
  使用 2.x 已有的 lease 机制(`methods/leases.ts`)。
- **Agent 配置(Profile)通用化**:模型、工具集、Skills、MCP、权限策略合成一个 Profile,存在 Agent 侧,
  每个控制端都能选择和编辑,不再分散在客户端设置里。

### A3. 能力协商

控制端在 `hello` 里声明自己的能力(2.x 已有 `clientCapabilitiesSchema`),3.0 增加以下字段:

```jsonc
{
  "formFactor": "phone" | "tablet" | "desktop",
  "features": ["terminal.write", "file.edit", "widget.html", "diff.inline", "push"],
  "stream": { "coalesceMs": 120, "maxDeltaBytes": 16384 }
}
```

Agent 根据声明的能力降级。例如控制端不支持 `widget.html`(手机上没有 WebView 沙箱)时,
内联可视化以静态快照(PNG)或「在桌面端打开」的占位卡片形式下发。

### A4. 移动端约束

| 约束 | 设计 |
|---|---|
| 手机上跑不了 Agent(iOS 禁止起子进程、JIT 受限;两个平台的后台执行时间都很短) | 手机只做控制端,Run 一律在设备上执行。手机断开不影响 Run 继续 |
| 网络不稳定,经常进后台 | 事件流按游标续传。游标断档时退回全量快照,并按[状态规则 5](01-architecture.md#5-状态统一管理c-版)对账 |
| 流量和电量 | 流式 token 在 Agent 侧按 `coalesceMs` 合并。大块工具输出只下发摘要,详情按需分页拉取。图片先发缩略图 |
| 用户不在 App 里 | **推送**:需要审批、Run 完成或失败、定时任务的结果。推送内容只有「哪台设备、哪类事件、一个不透明的 id」,不带会话内容。nextco.work 的推送中继只转发,看不到明文 |
| 手机屏幕小 | 见 B 部分 |
| 手机更容易丢 | 每台控制端有独立证书,可以单独吊销。手机端默认权限是「可以审批和对话,但不能开启 bypass 权限模式、不能改 Agent 配置」,桌面上可以放开。本地密钥存在 Keychain / Keystore,可以要求 Face ID / 指纹解锁 |
| 从手机发附件(照片、文件) | 新增分块上传方法,支持断点续传 |

### A5. 3.0 需要在 Agent 侧新增或改动的内容

1. 能力协商字段和按能力降级(A3)。
2. 流式事件合并、工具输出分页。
3. 推送:设备注册推送令牌,Agent 在交互创建和 Run 结束时发出推送事件,经由 nextco.work 推送中继送达。
4. 交互的跨端「先到先得」语义和对应的提示事件。
5. 控制端分角色(owner / member / viewer),按角色限制可调用的方法。
6. Profile 的存储与 CRUD 方法。
7. 本机通道:回环监听加一次性本机令牌,令牌由桌面端拉起 sidecar 时通过环境变量传入。
8. 从 Electron 数据根接管(复用 2.x 的 `takeover` 流程,详见 [04](04-update-install.md#4-数据接管))。

---

## B. Agent 交互界面

### B1. 组件清单(桌面和手机共用,通过密度变量适配)

| 组件 | 内容 | Arc 基础组件 |
|---|---|---|
| `MessageBlock` | 用户和助手消息,支持流式 Markdown | Markdown(流式)、Code block |
| `ReasoningBlock` | 思考过程,默认折叠 | Reasoning block |
| `ToolCallCard` | 工具调用:名称、参数摘要、状态、耗时、输出 | Tool call、Expandable card |
| `PlanCard` / `TodoList` | 计划和待办 | Agent plan |
| `SubagentCard` | 子代理状态。状态文案、计时、能否停止都来自同一个派生函数 | Agent status、Agent timeline |
| `InteractionPanel` | 审批和提问 | Dialog / Bottom sheet、Hold to confirm |
| `DiffSummary` | 变更文件列表,点开看 diff | Tree view、自研 DiffView |
| `Composer` | 输入框、@提及、/命令、附件、模型和模式选择 | Token text editor、Mention input |
| `ContextMeter` | 上下文占用和费用 | Context window、Usage meter |

### B2. 桌面和手机的差异

| 场景 | 桌面 | 手机 |
|---|---|---|
| 审批 | 消息流内联的审批卡片,支持快捷键(⌘↵ 允许,Esc 拒绝) | 底部弹出 Bottom sheet,按钮更大。危险操作(删除文件、执行命令)需要长按确认 |
| 工具调用 | 默认显示一行摘要,可以展开 | 只显示一行摘要,点击后在导航栈里推出详情页 |
| 子代理 | 右侧检查器里有列表,卡片可以就地展开 | 点击后推入子会话页面(只读,运行态跟随父会话那条记录) |
| 终端 | 完整的交互式终端 | 只读查看输出,提供 Ctrl-C 和「发送一行」 |
| 文件 | 编辑器加 diff | 只读查看,diff 用上下排列的统一视图 |
| 输入框 | 底部固定,多行 | 底部固定,适配安全区和软键盘;附件可以直接拍照或从相册选 |
| 全局活动 | 右上角活动胶囊 | 底部标签栏的「活动」页:跨项目、跨设备的审批收件箱和正在运行的任务 |
