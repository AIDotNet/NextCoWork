# 10 · 2.x 功能盘点(Q15)

盘点对象:2.x 的类型化 IPC 契约(`main` 分支 `src/shared/ipc/contract.ts`)。
共 49 个模块、359 个调用通道(`invoke` 351 个,渲染层单向 `send` 8 个),另有若干推送事件通道(`on*`)。
事件通道不单独盘点:3.0 里所有「状态」都是协议主题的订阅,首帧和增量事件用同一个全量类型(AGENTS.md §3 第 5 条),按领域一并设计。

## 1. 汇总

| 去向 | 通道数 | 说明 |
|---|---|---|
| Agent 协议方法 | 217 | |
| 拆分:一部分在 Agent,一部分在客户端 | 67 | |
| 客户端本地 | 21 | |
| 后续版本(3.1) | 39 | |
| 不做 | 15 | |

「Agent 协议方法」是要补进 `Ncw.Protocol` 方法表(09 §8.4)的部分;每个方法声明种类、所需角色、所需能力,`command` 类带幂等键。
「客户端本地」不进协议,由 `Ncw.UI` / `Ncw.Client` 自己处理。

## 2. 逐模块去向

| 2.x 模块 | 通道数 | 去向 | 3.0 位置 | 说明 |
|---|---|---|---|---|
| `agent` | 10 | Agent 协议方法 | `runs.*`、`interactions.*`、`tools.list` | run→`runs.start`;attach→订阅 `runs` 主题带游标;abort/backgroundSubagent/interject→`runs.abort/background/steer`;respond/listInteractions→`interactions.*`;listTools→`tools.list` |
| `agents` | 4 | Agent 协议方法 | `subagents.*` | 子代理定义的列表、诊断、启停(M3 已实现);AI 生成随 `settings.*` / 模型调用封装后做 |
| `app` | 13 | 拆分 | 客户端本地 + `device.info` | getBootstrap 由 `device`/`data`/`projects` 等主题的首帧取代;openExternal、copy、save 文件、窗口 → 客户端本地;更新检查/下载/安装 → 客户端(Velopack,04);openSessionWindow → 客户端 |
| `attachment` | 7 | 拆分 | `attachments.*` | upload、remove、listBySession、workspace 上传三步 → Agent(流式上传);pick(文件选择框)→ 客户端本地 |
| `browser` | 11 | 后续版本(3.1) | 3.1 | 内置浏览器(按工作区分区)随 WebView 方案放到 3.1;Agent 的浏览器工具(自研 CDP 客户端,D15)是另一回事,走工具协议,不在这里 |
| `clientAuth` | 7 | 客户端本地 | 客户端账户(07) | 每个控制端自己登录、自己持有账户会话;`nextcowork://` + PKCE。**已落地**(`Ncw.Client/Accounts/`;7 个通道:登录 / 选团队 / 取用户 / 用量 / 离线 / 退出 / 状态,钱包不做)。本机 Agent 的委托令牌走 `account.attachDelegatedToken`(09 §8.4「账户委托」) |
| `commands` | 4 | Agent 协议方法 | `commands.*` | 斜杠命令的列表、诊断、启停 |
| `composer` | 4 | Agent 协议方法 | `composer.*` | suggestNext、history、complete、cancel |
| `configSync` | 7 | 拆分 | 客户端 + `settings.*` | 账户配置同步用控制端的账户会话;落到设备的设置通过 `settings.*` 读写。**后端(B10)到位前保持关闭**:只留接口位置和本地快照语义,没有任何同步代码在跑(07 §2「实现状态」) |
| `connection` | 10 | Agent 协议方法 | `environments.*` | SSH 连接管理(06);认证提示(密码/口令/指纹确认)走 `interactions`,不再有专用通道;pickFile 是客户端本地 |
| `context` | 2 | Agent 协议方法 | `context.*` | compact、preview(会话上下文) |
| `conversations` | 1 | Agent 协议方法 | `search.*` | 全局会话搜索 |
| `dataMigration` | 5 | Agent 协议方法 | `data.*` | 已由 `data.status/retry` 取代(接管);skip、undoMerge 随 2.x 的合并流程取消;openDataDirectory 是客户端本地 |
| `device` | 7 | 不做 | `pair.*`、`remote.*`、`trust.*` | 2.x 的设备协议不参考不兼容(Q18);功能由全新的配对与远程设置取代 |
| `documentEngine` | 10 | 后续版本(3.1) | 3.1 | Office 编辑器(writer / sheets / slides) |
| `gateway` | 5 | Agent 协议方法 | `gateway.*` | 开放网关(09 §11),重新设计,不兼容 2.x 的 `/v1` |
| `git` | 13 | Agent 协议方法 | `git.*` | 概览、分支、提交、diff、暂存、提交、拉取、推送、生成提交信息 |
| `goal` | 3 | Agent 协议方法 | `goal.*` + 主题 `goal` | get、set、clear(M3 已实现;实际方法名是 `goal.*`) |
| `hooks` | 6 | Agent 协议方法 | `hooks.*` | 列表、诊断、保存、删除、启停、测试 |
| `imports` | 13 | 后续版本(3.1) | 3.1 | 从其他工具导入会话/配置;3.0 先靠直接接管 2.x 数据(D20) |
| `mcp` | 6 | Agent 协议方法 | `mcp.*` | 服务器配置、连接测试、密钥(NCK1) |
| `model` | 3 | Agent 协议方法 | `catalog.*` | 模型的改名、覆盖、删除并入模型目录(02 A8) |
| `modelCatalog` | 5 | Agent 协议方法 | `catalog.*` | 真源在 Agent 的 `ModelCatalogService`;models.dev 定时同步(D19),控制端只读 |
| `modes` | 1 | Agent 协议方法 | `sessions.modes` | 会话模式列表(并入会话元数据) |
| `plugins` | 23 | 拆分 | `plugins.*` | **已落地(M4,协议面 18 个方法 + `plugins` 主题,见 09 §8.4「插件」)**:安装、启停、权限、市场、配置、更新 → Agent(Jaunt 宿主,D14);runCommand、cardAction、interactionReply 按协议转发;React 视图相关的 activateEditor、openWebApp、setEditorDirty、confirmClose 不做(Q2),`pickPackage` 是客户端本地的文件选择框 |
| `pricing` | 4 | 不做 | — | 价格随 models.dev 自动采用(02 A8),不再有手动同步预览/应用 |
| `provider` | 26 | Agent 协议方法 | `providers.*` | 供应商、凭据(NCK1)、账号池、额度、OAuth(授权页在控制端打开,回调码经 `providers.oauth.*` 回到 Agent)、模型拉取、别名、测试 |
| `providers` | 1 | Agent 协议方法 | `providers.*` | listImportable 并入 providers |
| `proxy` | 3 | Agent 协议方法 | `settings.*` | 代理设置和密码(NCK1) |
| `recharge` | 3 | 不做 | — | 3.0 不涉及支付(Q11) |
| `referral` | 1 | 不做 | — | 3.0 不涉及支付/奖励(Q11) |
| `resource` | 3 | Agent 协议方法 | `files.*` | markdown 资源的读、存、删 |
| `review` | 5 | Agent 协议方法 | `review.*` | 变更集、文件 diff、撤销/重做预检 |
| `scheduled` | 10 | Agent 协议方法 | `schedules.*` | 定时任务及运行记录 |
| `session` | 4 | Agent 协议方法 | `queue.*` | getInput、queue、reportBackground → 排队消息;persistDraft 是客户端本地(草稿) |
| `sessions` | 19 | Agent 协议方法 | `sessions.*` | 列表、分页、摘要、编辑、删除回合、复制、分支(复制前缀)、重命名、模式、模型、归档、收藏、删除 |
| `settings` | 2 | 拆分 | `settings.*` | 按三种作用域(07)分开:本应用 → 客户端本地;设备 / 项目 → Agent |
| `shell` | 5 | Agent 协议方法 | `runs.*` | 停止、转后台、列表、查看、杀掉后台 Shell |
| `skills` | 11 | Agent 协议方法 | `skills.*` | 除 pickZip(客户端文件选择框)外都在 Agent |
| `storage` | 15 | 拆分 | `storage.*` | 统计、整理、备份、清理 → Agent;选目录/打开数据目录 → 客户端本地;导出导入对话框在客户端 |
| `tabs` | 3 | 客户端本地 | 客户端本地 | 页签布局的持久化是控制端自己的 UI 状态(`UiPrefs`) |
| `terminal` | 8 | Agent 协议方法 | `terminals.*` | PTY 由 Agent 提供(双向流),Arc.Terminal 渲染 |
| `theme` | 9 | 客户端本地 | 客户端本地 | Arc token 和强调色(主题工作室重做);背景图保存在客户端 |
| `usage` | 7 | Agent 协议方法 | `usage.*` | 用量统计来自设备上的库 |
| `video` | 5 | 后续版本(3.1) | 3.1 | 视频生成及任务 |
| `websearch` | 7 | Agent 协议方法 | `websearch.*` | 搜索提供方的启停、顺序、凭据、测试(**已落地**:独立的 `websearch.*` + 主题,不并入 `providers.*`,见 09 §8.5 与 08.03 §13;2.x 的「免 Key 内置源」兜底未移植) |
| `widget` | 6 | Agent 协议方法 | `widgets.*` | 内联可视化的状态、提交、动作;手机端降级为静态快照。**客户端渲染已做**(桌面 WebView 外壳 + `window.ncw` 宿主桥,见 [03 §8](03-ui-layout.md#8-内联可视化m8webview-评估与渲染宿主)) |
| `window` | 2 | 客户端本地 | 客户端本地 | 窗口控制(自定义标题栏) |
| `workspace` | 20 | Agent 协议方法 | `projects.*`、`files.*`、`environments.*` | 工作区在 3.0 叫项目;列表/新建/更新/关闭 → `projects.*`;文件读写/搜索/目录 → `files.*`;打开方式、复制路径、另存为 → 客户端本地;prepare/commitActivation 是 2.x 的激活握手,由项目登记取代 |

## 3. 实现顺序

随里程碑推进,每个领域落地时在 `docs/parity.md` 登记、在 `DeviceMethods`(或按领域拆分的方法文件)里声明方法:

- **M2(已完成)**:`pair` / `remote` / `trust` / `data` / `projects`(登记)/ `device.info`。
- **M3**:`runs`、`interactions`、`sessions`、`queue`、`context`、`environments`、`terminals`、`files`、`tools`。其中 `sessions` / `runs` / `interactions` / `queue` / `context` / `tools` 已落地(方法表见 [09 §8.4](09-protocol.md),能力名 `conversation`);`terminals` / `files`(含 markdown 资源)已随「工作区工具」落地(能力名 `terminals` / `files`,见 09 §8.4),`environments` 随 SSH 环境接线(**已落地**:`environments.*` + `environments` 主题 + `ssh_host_key` / `ssh_credential` 交互,见 [09 §8.6](09-protocol.md)、[06 §10](06-environments-ssh.md);`connection` 模块的 `pickFile` 是客户端本地的,`workspace` 的 `createSsh` = `projects.add` 带 `EnvironmentId`,`prepare / commitActivation` 由「惰性连接 + 租约」取代)。
- **M4**(`git` / `review` 已落地,能力名 `git` / `review`,见 09 §8.4「工作区工具」;**`skills` / `commands` / `hooks` / `mcp` 已落地**,能力名同名,见 09 §8.4「扩展域」和 08.06 §6;**`plugins` 已落地**,能力名同名,见 09 §8.4「插件」和 08.06 §7;**`settings` / `usage` / `schedules` / `storage` / `search` / `attachments` / `composer` / `widgets` / `gateway` / `websearch` 已落地**,能力名同名,见 09 §8.5 和 §11):`git`、`review`、`catalog`、`providers`、`skills`、`mcp`、`plugins`、`hooks`、`commands`、`subagents`、`schedules`、`goals`、`gateway`、`usage`、`storage`、`settings`、`search`、`widgets`、`attachments`、`composer`、`account.attachDelegatedToken`(**已落地**:`account.attachDelegatedToken` / `detachDelegatedToken` / `getStatus`,能力名 `account`,仅本机通道,见 09 §8.4「账户委托」)。
- **M5**:`push.register`(**已落地**:能力名 `push`,member 起,设备只存令牌不发推送,见 [09 §10](09-protocol.md);发送随后端推送中继 B4)。

## 4. 待确认

- `imports`(13 个通道)放 3.1 的依据是 3.0 已经能直接接管 2.x 数据;如果要支持从其他工具(例如 Claude Code)导入,需要提前。
- `video`(视频生成)不在功能对齐矩阵里,暂放 3.1。
- `configSync` 的账户配置同步依赖后端(B10),细节随 M4 的账户同步一起定。
