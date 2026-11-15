# 02 · Agent 设计

本篇分两部分:**A. Agent 运行时**(服务端如何做到通用,适配多种客户端,尤其是移动端)和
**B. Agent 交互界面**(会话界面如何在桌面和手机上共用一套组件)。

---

## A. Agent 运行时

### A1. 一个运行时,三种部署

| 部署形态 | 谁来拉起 | 典型场景 | 3.0 范围 |
|---|---|---|---|
| **本机内置** | 桌面端(sidecar) | 普通用户,开箱即用 | ✅ |
| **独立设备服务** | systemd / LaunchDaemon / WinSW | NAS、开发服务器、关机也要跑的任务 | ❌ 3.0 不做(2.x 有 TS 版) |
| **云端托管** | nextco.work 的编排服务 | 没有自己的电脑或服务器;手机上随时开工 | ✅(见 A6) |

3.0 做的两种形态是**同一个 C# 二进制(`ncw-agent`,NativeAOT)、同一套协议**,差别只在启动参数(`--mode local|cloud`)和数据根。
架构上不堵死独立部署,以后要做时只需加一种模式和对应的 GUI 管理入口。

### A2. 对客户端类型无感知

- 运行时不对界面做任何假设:不存在「只有渲染层才知道」的状态。2.x 里组件内 `useState` 维护运行时数据的做法,
  在 3.0 一律改到 Agent 侧,通过协议暴露出来。
- **交互(审批、提问、计划确认)是协议对象**:有 id、有版本号。任何已连接的控制端都可以回答,
  先到先得,后到的返回 409。界面上要显示「已在另一台设备上处理」。
- **多控制端并发**:同一台设备允许多个控制端同时连接。独占型操作(终端输入、编辑同一个文件)
  使用租约(lease)机制:控制端申请租约后才能写,租约有心跳和过期时间,断线后自动释放。
- **Agent 配置(Profile)通用化**:模型、工具集、Skills、MCP、权限策略合成一个 Profile,存在 Agent 侧,
  每个控制端都能选择和编辑,不再分散在客户端设置里。

### A3. 能力协商

控制端在 `hello` 里声明自己的能力(协议见 [09 §7](09-protocol.md#7-会话l3)):

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
| 手机上跑不了 Agent(iOS 禁止起子进程、JIT 受限;两个平台的后台执行时间都很短) | 手机只做控制端,Run 一律在设备上执行。手机断开不影响 Run 继续。没有自有设备的用户用云端 Agent(A6) |
| 网络不稳定,经常进后台 | 事件流按游标续传。游标断档时退回全量快照,并按[状态规则 5](01-architecture.md#5-状态统一管理c-版)对账 |
| 流量和电量 | 流式 token 在 Agent 侧按 `coalesceMs` 合并。大块工具输出只下发摘要,详情按需分页拉取。图片先发缩略图 |
| 用户不在 App 里 | **推送**:需要审批、Run 完成或失败、定时任务的结果。推送内容只有「哪台设备、哪类事件、一个不透明的 id」,不带会话内容。nextco.work 的推送中继只转发,看不到明文 |
| 手机屏幕小 | 见 B 部分 |
| 手机更容易丢 | 每台控制端有独立证书,可以单独吊销。手机端默认权限是「可以审批和对话,但不能开启 bypass 权限模式、不能改 Agent 配置」,桌面上可以放开。本地密钥存在 Keychain / Keystore,可以要求 Face ID / 指纹解锁 |
| 从手机发附件(照片、文件) | 新增分块上传方法,支持断点续传 |

### A5. 相比 2.x,C# Agent 要新增的能力

移植 2.x 现有能力的计划见 [01 §4](01-architecture.md#4-agent-移植策略)。下面是 2.x 没有、3.0 要新做的部分:

1. 能力协商字段和按能力降级(A3)。
2. 流式事件合并、工具输出分页。
3. 推送:设备注册推送令牌,Agent 在交互创建和 Run 结束时发出推送事件,经由 nextco.work 推送中继送达。
4. 交互的跨端「先到先得」语义和对应的提示事件。
5. 控制端分角色(owner / member / viewer),按角色限制可调用的方法。
6. Profile 的存储与 CRUD 方法。
7. 本机通道:回环监听加本机令牌,令牌由桌面端拉起 sidecar 时通过环境变量传入(「关窗后继续运行」时跨会话保留在 `run/sidecar.json`,见 [04 §7.2](04-update-install.md));同一数据根只允许一个 Agent(`run/agent.lock`,已有实例时退出码 75)。
8. 从 Electron 数据根接管(用 C# 重新实现 2.x 的 `takeover` 流程,由 3.0 首次启动时自动执行,没有命令行入口,详见 [04](04-update-install.md#4-数据接管))。
9. 云端运行所需的改动:账户签发身份、休眠与唤醒、密钥托管、资源配额(见 A6)。

### A6. 云端 Agent

**定位**:nextco.work 为每个用户托管一个 ncw-agent。它和本机、自有设备是**同一个运行时、同一套协议**(决策 D11),
客户端把它当成一台名叫「云端」的设备。3.0 先做个人版,团队共享放到后续版本。

```
控制端 ──中继(隧道内 TLS)──▶ ncw-relay ──▶ 用户沙箱(容器 / microVM)    ← 中继本体 `Ncw.Relay` 已实现(09 §9)
                                     ├─ ncw-agent(和桌面内置的是同一个版本)
                                     ├─ 持久卷:数据根(SQLite)+ 工作区
                                     └─ 出网策略:模型 API、Git 托管、包管理源
                 ▲
nextco.work 编排服务:创建 / 唤醒 / 休眠 / 升级 / 配额
```

| 方面 | 设计 |
|---|---|
| **开通** | 在「设备」页或首次登录引导里点「创建云端 Agent」。编排服务分配沙箱和持久卷,用账户签发设备身份 |
| **身份与配对** | 不扫码、没有配对码。设备密钥在沙箱里生成(私钥永不离开设备,落盘时由 KMS 包裹),启动时向编排服务证明「我持有这把私钥」,编排服务回一份**签名的身份绑定**(设备 ↔ 账户)。控制端登录账户后把自己的证书公钥注册到账户 API,编排服务用**签名指令**把信任记录下发给云端设备,并把设备证书指纹返回给控制端(见 [09 §4](09-protocol.md#4-身份信任与角色)、[§13](09-protocol.md#13-编排控制通道m6agent--编排服务))。之后照常经中继建立端到端 TLS,中继看不到明文。**确定方案**见 [A6.1](#a61-云端-agent-的-agent-侧实现m6) |
| **生命周期** | 没有运行中的 Run、没有连接的控制端、也没有临近的定时任务时,空闲 N 分钟后休眠(保存卷,停掉计算)。控制端连接或者定时任务到点时唤醒。冷启动目标在 10 秒以内,界面上显示「正在唤醒云端…」 |
| **Run 不依赖在线** | 手机断开后 Run 继续执行;完成或者需要审批时发推送 |
| **工作区来源** | 从 Git 仓库克隆(GitHub / GitLab OAuth 或部署密钥)、上传压缩包,或者新建空项目。云端没有用户本地文件系统 |
| **本地 ↔ 云端接力** | 3.0 通过 Git 完成:「在云端继续」会把当前分支推到远程,云端拉下来继续;反向同理。不做文件级的实时同步 |
| **模型** | 默认用 nextco.work 的模型通道,也可以填自己的供应商密钥 |
| **密钥** | 本机用的 `NCK1` 主密钥文件放在云端不安全。云端改为用 KMS 包裹主密钥,运行时只在内存里解开;凭据加密格式不变 |
| **隔离与安全** | 每个用户一个沙箱,不共享内核态资源(优先 microVM)。出网走白名单加审计。工具权限默认仍是「询问」,用户可以放宽,因为破坏范围局限在自己的沙箱里 |
| **能力差异** | 没有本地 GUI 应用(「用编辑器打开」不可用);终端、Git、浏览器工具(自研 CDP 客户端 + 设备上已有的无头 Chrome / Edge / Chromium,D15)可用。差异通过能力协商告诉客户端,界面上隐藏不可用的入口 |
| **配额** | 3.0 **暂不考虑支付**。按账户设置计算时长、存储和并发 Run 的上限(灰度期按白名单开通)。界面显示用量,接近上限时提醒,用尽后转为只读 |
| **升级** | 由编排服务滚动升级云端 Agent,只在没有运行中 Run 的时候换版本。协议 N/N-1 兼容保证新旧客户端都能连 |
| **数据导出** | 可以导出整个数据根和工作区(压缩包),也可以把云端数据迁到自有设备 |

**Agent 侧需要的改动**:账户签发身份的握手、休眠前收尾和唤醒后恢复(包括未完成 Run 的状态)、KMS 密钥提供器、
按配额拒绝新 Run、上报资源用量。**M6 已实现**,见 [A6.1](#a61-云端-agent-的-agent-侧实现m6)。

**后端(nextco.work)需要的部分**:编排服务、沙箱镜像、持久卷与快照、KMS、配额。见 [05 §6](05-roadmap.md#6-后端配合清单)。

### A6.1 云端 Agent 的 Agent 侧实现(M6)

**边界**:编排服务、沙箱镜像、KMS 本身、持久卷、固定出口 IP 与出网白名单都是**后端 / 基础设施**,不在本仓库。本仓库实现的是「`ncw-agent` 进程在云端沙箱里运行时需要的全部行为」和「Agent 与编排服务之间的接口契约」
(契约见 [09 §13](09-protocol.md#13-编排控制通道m6agent--编排服务),待后端对齐的假设见 [05 §6.1](05-roadmap.md#61-编排服务接口契约与待对齐的假设m6))。测试用假编排服务、假 KMS、进程内中继,不依赖任何真实后端。
代码:`src/Ncw.Agent.Cloud`(领域层:配置、通道、指令泵、配额、上报、空闲检测、出网钩子、健康端点)、`src/Ncw.Agent.Host/Cloud`(`CloudRuntime` 装配)、`Ncw.Protocol/Orchestration`(通道类型)、`Ncw.Protocol/Methods/CloudMethods.cs`(客户端可见部分)。

#### 启动(`ncw-agent --mode cloud`)

- **无头**:没有本机通道(没有 socket、没有 `NCW_LOCAL_TOKEN`、没有 `--endpoint` / `--parent-pid` / `--lifeline`),远程连接**只经中继**,局域网不监听;可选的内网 `GET /healthz` 端点只回「活着 / 没在收尾」和设备 id(公开标识),不是控制通道。`--mode local` 的行为一个字节没动。
- **启动配置只来自编排服务注入的环境,不在命令行、不进日志**:`NCW_CLOUD_CONFIG`(配置文件路径,JSON,**不含密钥**,文件不能被组 / 其它用户写)、`NCW_ACCOUNT_TOKEN` 或 `NCW_ACCOUNT_TOKEN_FILE`(账户身份令牌)、可选 `NCW_ORCH_KEYS`(编排验证公钥 `kid:base64,…`,覆盖文件里的)。
  文件里:数据根、工作区根、编排服务地址(必须 `https`,仅回环允许 `http`——令牌在这条连接上)、中继地址(`wss`;`ws` 仅本机 / 局域网)、编排验证公钥、KMS(`provider` / `context` / 可选 `keyId`)、可选的期望设备 id、空闲 / 上报间隔、健康端点、出网策略。不认识的字段、范围外的数值一律启动失败。
- **启动失败是一行结构化输出**:`ncw-agent startup-failed <稳定 key> <字段名>`(**只有 key 和字段名,绝无值**,值可能是密钥),退出码:`78` 配置不合法(重启没用)、`69` 依赖不可用(KMS 连不上 / 拒绝,可退避重启)、`65` 身份 / 主密钥不符(挂错了卷,重启没用)、`75` 同一数据根已有 Agent、`2` 命令行参数错误。就绪时打印 `ncw-agent ready <deviceId> cloud`。
- **拒绝配对码流程**:云端设备不需要配对码,`pair.begin / finish / claim / createQr` 返回 `unsupported`(`cloud.pairing.unsupported`);`remote.*` 配置本来只有本机通道可调,云端的中继地址固定为启动配置的值。

#### 身份与信任(确定方案)

在 02 A6 / 09 §4 的两种读法之间选了第二种:**设备自己生成密钥并注册,编排服务签发「身份绑定」**,而不是由编排服务签发设备私钥。理由:私钥是设备身份的根,永远只在沙箱里(落盘由 KMS 包裹),编排服务从不接触它;「账户签发身份」体现为编排服务对「这台设备(指纹)属于这个账户、经这个中继到达」的**签名声明**。

1. 第一次启动生成 ECDSA P-256 身份(和本机同一套,`deviceId` = 公钥指纹派生);私钥用主密钥 NCK1 加密写 `device.json`,主密钥由 KMS 包裹(见下)。
2. 每次启动 / 唤醒向编排服务 `hello`:带公钥和**持有私钥证明**(对 `域前缀 ‖ deviceId ‖ 时间 ‖ 本次启动 id` 的签名,换一次启动签名就不同,旧注册不能重放)。编排服务验证后回**身份绑定**(Ed25519 签名)和积压的指令。Agent 验签、核对绑定里的指纹等于自己的(**不符 = 退出码 65**,不会用别人的身份上线)、记住账户 id,并把账户身份令牌交给模型通道(`DelegatedAccount`)。
3. **信任记录由编排服务下发**:控制端登录账户后把证书公钥注册到账户 API;编排服务生成路由令牌(只交给控制端,设备和中继只有 SHA-256 哈希),并向设备下发 `trust.put {指纹, 名称, 角色, 路由令牌哈希}`;同时把设备证书指纹和中继路由经 HTTPS 返回给控制端。设备按指纹授权、按令牌哈希核对「这个令牌属于 TLS 里的这个控制端」(和局域网配对后的中继会话同一套校验)。
4. **吊销**(`trust.revoke`,或 `trust.replace` 里不再出现的控制端)= 删信任行 → **立即断开**它的所有连接 → 把令牌哈希从中继撤掉(中继掐断用它的会话,令牌之后一律 `unauthorized`)。
5. **休眠期间到达的吊销**:唤醒后 Agent **先处理编排服务积压的指令,再向中继注册**(上限 15 秒;编排服务不可用时带着数据库里已有的信任记录先上线,指令之后到达再吊销)——唤醒的空档里被吊销的控制端连不上(有测试:设备一上线就拼命连)。
6. 信任记录在数据库里,休眠唤醒后不需要重新下发;唤醒后的第一个控制端用旧凭据直接连上。

**指令的安全性**(现成 Ed25519,BouncyCastle,不自研):每条指令是 `{kid, payload(JSON 字节的 base64), sig}`,签名覆盖 `域前缀 ‖ 0x00 ‖ payload`(域分隔:身份绑定的签名不能当指令用);验签 → 解析 → 类型已知 → **发给本设备** → 在有效期内(容忍 5 分钟时钟偏差)→ **序号严格大于已应用的最大序号**(防重放,序号**先落盘再应用**,休眠唤醒后仍有效)。
伪造 / 篡改的指令不回执、不消耗序号(伪造的高序号挡不住真指令);验签通过但被拒绝的(发给别人、过期)回带原因的 `rejected`;重放回 `duplicate`。指令串行处理;`busy`(例如休眠请求时有 Run 在跑)把序号退回,编排服务可以重发。

#### 密钥保护(KMS)

`IKeyWrap`(`Ncw.Agent.Storage.Credentials`)抽象 `Wrap / Unwrap` 数据主密钥(NCK1 的 32 字节 AES 密钥),带**加密上下文**(启动配置的 `kms.context`,通常含账户 id 和沙箱 id,KMS 据此保证别人的卷解不开)。
云端模式下磁盘上**只有** `credential.key.kms`(KMS 包裹后的密文 + keyId),明文主密钥只在进程内存里(启动时解开一次并缓存,运行中 KMS 抖动不影响读写凭据;`CredentialStore` 拿到的是副本、用完清零)。**凭据格式 NCK1 不变**。
第一次启动生成并包裹;数据根里有明文 `credential.key`(本机数据导入云端)时包裹后**删除明文**;库里有 NCK1 密文却找不到任何形态的主密钥 → 拒绝启动(退出码 65),绝不生成新密钥。本机模式仍用 0600 的 `credential.key` 文件,行为不变;来源按数据根登记(`CredentialCrypto.UseSource`)。
**适配点**:`CloudBoot.CreateKeyWrap(config, env)` 按 `kms.provider` 造 `IKeyWrap`。**本仓库不含任何真实云厂商的 KMS 客户端**——后端在这里加一个 `case`、实现 `IKeyWrap` 即可。唯一内置的 `dev-env`(环境变量里的 AES 密钥做 AES-GCM)只给开发和冒烟测试:**它只在定义了 `NCW_DEV_KMS` 的构建里才编译进去**(MSBuild 属性 `NcwDevKms`:Debug 默认开,Release / 发布默认关),发布的 `ncw-agent` 里根本没有这段代码,不能靠环境变量把它打开;带它的构建还要显式设 `NCW_CLOUD_ALLOW_DEV_KMS=1` 才可用。`tools/Ncw.Build publish-agent` 发布后会扫描二进制,出现开发 KMS 的标记就失败;冒烟测试用的带它的二进制要加 `--dev-kms` 发布,产物不得分发。

#### 休眠与唤醒

**空闲判定**(`IdlePolicy`,纯函数,时钟可注入):没有运行中的 Run、没有连接的控制端、已空闲 N 分钟(默认 15)、最近的定时任务离现在不少于 `minSleep`(默认 10 分钟)。可以休眠时把「下一个定时任务 − 提前量(默认 2 分钟)」作为 `wakeAt` 告诉编排服务。编排服务也可以用 `sleep.request`(`whenIdle`:控制端连着或有 Run 就 `busy`;`now`:立即)或 `upgrade.request`(只有运行中的 Run 才挡)触发同一个收尾。

**收尾**(`CloudRuntime.SleepAsync`,一旦开始不可逆,成功后进程退出):

1. 置「收尾中」:新的 Run 被拒绝(`unavailable`,`cloud.preparing`,可重试),`cloud.getStatus` 报告 `preparing`;停止指令长轮询(正在处理的这批含回执处理完)。
2. 记下将被打断的 Run(`now` 模式才可能有)、后台 Shell 数。
3. **停止对外服务并注销中继**(等设备端注销真正完成;中继随即察觉,控制端得到 `device_offline` 而不是超时)、断开所有会话 → **中止所有 Run**(消息 / 状态在这一步落库)、**杀后台 Shell**(策略:一律清理,它们活不过沙箱停止;数量写进摘要)、关终端和浏览器进程、停调度器。
4. 最后一次用量上报(尽力而为、有超时),写**休眠快照**(`v3.cloud.sleep`),**数据库检查点**(`wal_checkpoint(TRUNCATE)`,卷快照里只有一个自洽的 `nextcowork.db`)。
5. 通知编排服务(`lifecycle: sleeping | upgrading`,带 `wakeAt` 和摘要;重试 3 次,**失败不阻止退出**——退出码 0 加休眠快照本身就是真相),进程以 0 退出。
通知到达的瞬间设备端的中继注册已经注销(中继随即察觉并对控制端回 `device_offline`)、WAL 已截断(有测试)。

**唤醒恢复**:新进程读休眠快照(`ResumedFromSleep`)。多数状态本来就在数据库里,不需要恢复动作——会话与消息、排队消息、信任记录与路由令牌哈希(中继注册时整体推送,按游标续传由协议本来的快照 / 续连机制负责)、目标(转录里的 `goal_status` 标记,按需恢复)、定时任务(调度器启动时对账:错过的记 `skipped`,**不补跑**,所以编排服务应在 `wakeAt` 唤醒)。
要做的只有一件事:**被休眠打断的会话里排队的消息接着发**(用户的意图还在队列里,没有「停止」这个动作;配额用尽则留在队列里)。**被打断的那个 Run 不自动重跑**(重跑可能重复有副作用的工具调用),列在 `cloud.getStatus.InterruptedRuns` 里让控制端提示「上次的任务被中断了」,用户确认后 `cloud.clearInterrupted`。
冷启动没有额外步骤(解开主密钥 → 打开数据库 → 打招呼 → 注册中继);macOS 本机实测 NativeAOT 二进制从启动到打印 `ready` 约 0.5 秒(含向回环上的假编排服务打招呼;不含后端拉起沙箱、挂载卷的时间),冒烟测试断言 < 10 秒(设计的冷启动目标)。

#### 配额与用量

- **配额由编排服务下发**(`quota.set`,整体替换,持久化,休眠唤醒后仍有效)。字段(null = 不限):`dailyRuns` / `monthlyRuns`(顶层 Run 数)、`dailyTokens` / `monthlyTokens`、`maxConcurrentRuns`、`monthlyComputeSeconds`(Agent 醒着的秒数,休眠不计)、`storageBytes`(数据根 + 工作区)、`frozen`(账户冻结)。日 / 月窗口按 **UTC** 自然日 / 月。「用尽后转为只读」= 拒绝新 Run(其余方法照常,3.0 不收费)。
- **Run 发起前检查**(`runs.start`、`runs.executePlan`、开放网关、定时任务、排队 / 插话):超限返回 `rate_limited`(不可重试)+ `error.details` 的稳定 key **`quota.exceeded`**,参数 `window`(`dailyRuns` / `monthlyTokens` / `concurrentRuns` / `frozen` ……)、`limit`、`used`、`resetsAt`。检查与记账在同一把锁里(并发发起不会一起挤过最后一个名额);Run 没真正发起(被注册表拒绝)时退款。token 来自用量表(Run 跑完才入账,所以 token 限额是「软」的:一个正在跑的 Run 可以超出一点)。
- **用量上报**(`UsageReporter`):批量、可重试、**幂等**。每批是自上一批被确认以来的增量(Run 数、token、计算秒数、存储字节),`batchId = <deviceId>:<序号>`;失败(网络 / 5xx / 429)的批次留在发件箱(落盘)原样重发,期间新增的用量是下一批;只有收到成功(含 `duplicate`)才推进序号和基线。休眠前最后冲刷一次。
- 控制端用 `cloud.getStatus`(能力 `cloud`,只有云端设备声明)读状态、配额、用量、被打断的 Run;没有这个能力的设备(本机)界面隐藏用量显示。

#### Git 接力与工作区来源

- **云端项目的代码来源**:`projects.add` 带 `CloneUrl`(+ 可选 `CloneBranch`)——`Path` 是要创建的目标目录(云端相对路径从 `workspacesRoot` 解析,**必须在它里面**),经项目环境的 git(`IExecutionEnvironment`,argv 不经 shell)克隆后登记成项目。只接受 `https://`、不带用户信息的地址(`file:` / `ext::` / `ssh:` / `-` 开头全拒;`GIT_ALLOW_PROTOCOL=https` 同时限制重定向和子模块),URL 放在 `--` 之后,分支名挡前导 `-`;目标必须不存在或为空,失败时删掉这次创建的目录,不留半成品。协议是 `ProjectsAddParams` 追加两个带默认值的末尾字段(旧载荷照常解码,冻结向量 `GoldenV1Cloud`)。
- **凭据经 NCK1**:`git.credentials.set / remove / list`(能力 `git.credentials`,**owner 专属**,令牌只进不出)把某个 Git 主机的 HTTPS 令牌(GitHub / GitLab OAuth 令牌或 PAT)存进凭据库(ref `git:https:<host>`)。注入方式:每次起 git 进程时用**环境变量**(`GIT_CONFIG_COUNT/KEY_n/VALUE_n` 的 `http.https://<host>/.extraHeader`),令牌因此不在命令行、不在 `.git/config`、不在 URL 里,并且**按 URL 作用域**(git 只把它发给匹配的主机;有用真 git 的测试)。`git pull / push`(已有的 `GitService`,经 `GitServiceOptions.ExtraEnvironment`)和克隆共用它。克隆失败时 stderr 里的令牌被抹掉。
- 本地 ↔ 云端接力仍按设计「通过 Git」:控制端把分支推到远程、云端 `git pull`(反向同理)——已有的 `git.*` 方法就够,缺口只有「云端从没有这个仓库开始」,由克隆补齐。
- **未做**:SSH 部署密钥(需要把私钥落成临时文件并设置 `GIT_SSH_COMMAND`,密钥生命周期要单独设计)、用 OAuth 刷新令牌自动续期(控制端重新 `git.credentials.set` 即可)、上传压缩包与「新建空项目」(后者本来就是 `projects.add` 一个空目录)。

#### 出网策略钩子

真正的出网白名单与固定出口 IP(B12)是基础设施(沙箱网络策略 / 出口网关)。Agent 侧只留 `IEgressPolicy`:发起出网前问「这个目标可以吗」,给用户**提前的、结构化的拒绝**(`cloud.egress.denied` + 主机,而不是连接超时)并留审计口(`HostAllowlistEgress.audit`,只记用途、主机、结论)。
启动配置的 `egress { allowedHosts, enforceFor }`:精确主机或 `*.域名` 通配,`enforceFor` 限定受约束的用途(`Model` / `WebFetch` / `Browser` / `Mcp` / `SshTarget` / `GitRemote` / `Plugin`;空 = 全部)。**已接线:Git 克隆**。WebFetch(`IWebHttp`)、浏览器(`IBrowserLauncher`)、MCP(`IMcpHttpFactory`)、SSH 目标、插件 `net.fetch`(`IPluginHttp`)、模型路由的 HTTP 出口各有一个注入点,云端装配时把策略包进去即可,**还没接**(列入 05 §6.1 待做)。本机模式用 `AllowAllEgress`,行为不变。

#### 已知缺口

- 备份 / 数据导出(`storage.*`)直接读 `credential.key` 文件;云端没有它(只有 `credential.key.kms`),导出云端数据到自有设备的流程需要经编排服务 / KMS 解开后重新包裹,**未做**。
- 开放网关(`gateway.*`)在云端没有特殊处理(默认关、只绑回环);云端不应开启它,尚未在配置里硬禁。
- 退款会让累计 Run 数(上报基线)少一个,极端情况下(退款发生在已上报之后)用量上报会少报 1 次 Run;不影响配额判定。
- 配额的 token 窗口按 UTC;若编排服务按账户时区计算,两边的「今天」会不一致(05 §6.1 假设 A5)。

### A7. 插件与 JS 执行(Jaunt)

**原则**:Agent 里所有 JS 执行都经过 `Ncw.Agent.Plugins` 的 Jaunt 宿主,别处不允许自己起 JS 引擎。
Jaunt 是纯托管、兼容 NativeAOT 的 JS 引擎(ES2025、ES Modules、async),自带隔离 realm、执行预算和不依赖反射的数据映射。

**和 2.x 插件模型的对应关系**

| 2.x | 3.0 |
|---|---|
| 清单(manifest)、权限申请与授予、API 版本兼容 | 照搬,逻辑从 `shared/plugin/*` 移植到 C# |
| 插件代码跑在 BrowserWindow 里的沙箱 iframe,通过 MessagePort 发 RPC 给宿主 | 插件代码跑在 Jaunt realm 里,宿主 API 以 `SetFunction` / `SetAsyncFunction` 显式绑定;**每个绑定都先做权限检查**,检查逻辑和 2.x 的 RPC 四道门一致 |
| `nextcowork` 插件 API(`packages/plugin-api/nextcowork.d.ts`) | 保持兼容(非 UI 部分):命令、菜单贡献、Agent 工具、Skills、配置、存储、受控网络访问 |
| `nextcowork/view`(React 视图、自定义编辑器) | 暂不考虑(Q2) |
| 加载时用 sucrase 擦除 TS 类型 | **不做**(M4 评估后修正,见 [A7.1](#a71-jaunt-评估与实现m4)):2.x 的插件 `main` 本来就是作者用 esbuild 打好的单文件 ESM(清单校验要求 `.js`),sucrase 在 2.x 只用于 widget 的 TSX,不用于插件。3.0 只加载 `.js` / `.mjs`,TypeScript 由作者自己在仓库之外编译 |

**运行模型**

- **编译一次,到处共享**:插件模块用 `JsCompiler.CompileModule` 编译成不可变字节码,多个 realm、多个线程共用。
- **隔离**:每个插件有自己的 `JsEngine` 和 realm,插件之间互相看不到全局对象。无状态的 Agent 工具用 `JsPlugin.Create`,
  每次调用一个新 realm,并发数有上限;有状态的插件(激活 / 休眠生命周期)用长驻 realm,休眠时释放。
- **预算**:每次调用都设 `JsLimits`(超时、最大指令数、调用栈深度、内存、输出大小),默认使用 `JsProfile.Hardened`(禁用 `eval` 和 `Function`)。
  Run 被停止时,通过 `CancellationToken` 中止正在执行的插件代码。超出预算的 engine 进入故障态,直接丢弃并重建。
- **没有 Node API**:不提供 `require('fs')`、`process`、`child_process`。文件、网络、计时器只能通过宿主 API 拿到,而且都要经过权限检查
  (网络还要匹配清单里的 host 权限)。依赖 Node 内置模块的 npm 包不能用,这一点和 2.x 的 iframe 沙箱一样。
- **数据映射**:宿主和插件之间的 DTO 用 Jaunt 的 `[JsSerializable]` 源生成器,兼容 AOT。
- **错误**:`JsException`(插件自己 throw)作为工具错误返回给模型;`JsCompileException` 和权限拒绝记成插件诊断,
  在插件详情页展示(沿用 2.x「失败一律变诊断,永不 throw 到调用方」的原则)。

**其他 JS 使用场景(可选)**:一个给 Agent 用的「JS 计算」内置工具(数据转换、计算),跑在 Hardened realm 里,不绑定任何宿主 API。

**不在范围内**:MCP 服务器仍然是外部进程,用户自己配置的 `npx` 类服务器需要用户机器上有 Node,那是用户的环境,不是我们的依赖。

### A7.1 Jaunt 评估与实现(M4)

**结论:可用,按设计落地。** 依据是下面的实测,不是文档:

| 评估项 | 实测结果 |
|---|---|
| 包与版本 | NuGet `Jaunt` **1.0.3**(2026-11-11 发布,MIT,依赖 Acornima 1.9.1;1.0.1 / 1.0.2 / 1.0.3 三个版本)。版本只写在 `Directory.Packages.props`;只有 `Ncw.Agent.Plugins` 引用它(`ArchitectureTests` 机械检查:别的项目没有 Jaunt 引用、没有 `new JsEngine`) |
| NativeAOT / 裁剪 | **零警告**。`tools/PluginAotProbe`(最小程序,`dotnet publish -r osx-arm64`,`IlcTreatWarningsAsErrors`)发布 0 条 IL / AOT 警告,**原生二进制里**实跑:目录安装 → 授权 → 激活(Hardened)→ 异步命令(顶层 await、定时器、宿主调用)→ 工具(文本 + 卡片)→ 死循环被指令预算终止、插件 faulted、宿主不受影响。`ncw-agent` 引用插件宿主之后 `dotnet publish src/Ncw.Agent -c Release -r osx-arm64` 同样零 IL / 裁剪警告(只有 macOS 工具链两条 clang 模块缓存提示)。Jaunt 自带的 `eng/verify-aot.sh` 在 net8 / net10 / net11 上发布并运行「发布之后才写出的 JS」,`eval` / `Function` 在 AOT 下也能用(Hardened 里禁用) |
| `JsLimits` 预算 | `Timeout`(整个根执行的墙钟,含等待异步宿主工作)、`MaxInstructions`(工作单位:循环回边、调用、长内置的逐元素步骤,正则引擎也计)、`MaxStackDepth`、`MaxTrackedBytes`(**引擎生命周期内累计**的保守分配计数,垃圾也算,不是 RSS)、`MaxPendingJobs`、`MaxOutputBytes`、`MaxModules`、`MaxStringLength`;每次执行可用 `JsExecutionOptions` 再收紧。越界 = 根执行被终止、JS 抓不到、**引擎进入故障态**(只能丢弃);调用方取消同样让引擎故障 |
| `JsProfile.Hardened` | 内建对象全部冻结;`eval` / `Function` / 生成器与异步函数构造器及其别名抛 `EvalError`。裸引擎没有 `console` / 计时器 / 网络 / 文件 / `process` / `require`,宿主显式加。注意:这是语义上的限制,不是进程沙箱——CPU / 内存预算才是真正的隔离手段 |
| 宿主绑定 | `SetFunction`(类型化的 `string` / `double` / `int` / `bool` / `JsValue`,或原始 `JsNativeFunction(ref JsCallContext)`)、`SetAsyncFunction`(只能在 Async 执行里调用)。另有 `[JsSerializable]` 源生成 DTO 映射;我们**不用**,宿主边界一律是 JSON 字符串(插件侧一层很薄的 JS 前导做 `JSON.stringify` / `parse`),绑定面小、两边类型不用对齐 |
| Promise / async | ES2025 的 `async` / `await`、异步生成器、`for await`、顶层 await、`Promise.*` 全有。Async 执行模式驱动宿主的异步完成。**一条硬约束**:一次执行结束时不能还有没完成的异步宿主操作(`UnjoinedAsyncWork`,引擎故障)——所以插件里没 `await` 的宿主调用、还没触发的 `setTimeout`、等卡片按钮的泵都会让引擎故障。宿主的 JS 前导把这些在每次调用返回之前收齐(定时器取消、动作通道关闭、其余等跑完),插件作者不必知道这条 |
| ES 模块 | `JsCompiler.CompileModule` + `LoadModuleAsync` + `IJsModuleResolver`(含动态 `import()`、JSON / 文本模块、顶层 await)。`nextcowork` 是虚拟模块(解析器注入,身份由宿主写死),其它 import 一律拒绝(单文件打包,和 2.x 一样)。引擎同一时刻只有一次根执行,所以一个插件内的调用串行,跨插件并行 |
| 2.x 兼容性验证 | `CompatibilityTests` 用 main 分支的 12 份真实清单(`plugins/ncw.{writer,sheets,slides}` + `examples/*`)逐份解析:**非 UI 的全部读得懂**(think、wechat-exports、claude-code、codex、bilibili);办公三件套 / image-studio / markdown-studio / note-editor / excalidraw 是纯编辑器 UI,清单读得懂、能装能启用,`views` / `customEditors` 在诊断里逐条标「不支持」(Q2),`nativeComponents` 整份拒绝。JS 侧把 think、wechat-exports(去掉 ZIP)、脚手架模板移植成夹具(`tests/Ncw.Agent.Plugins.Tests/Fixtures/`:esbuild 输出风格的单文件 ESM,`import * as ncw from 'nextcowork'`),逻辑原样跑通 |

**TypeScript 的决定**:3.0 只加载 `.js` / `.mjs` 的单文件 ESM,**不在 Agent 里转译 TypeScript**。理由:① 2.x 的插件本来就是这样(清单校验 `main` 必须是 `.js` 的单文件 ESM 打包,脚手架用 esbuild 构建;D14 原稿里「加载时用 sucrase」是把 widget 的 TSX 编译(`widget-compile.ts`,model 写的小组件)误当成了插件加载);② AGENTS.md §6 不允许 TypeScript 构建步骤,而 sucrase 本身是 Node 生态的 JS 库,在 Jaunt 里跑它等于把一个转译器带进 Agent;③ 插件作者的构建工具链在仓库之外,不是我们的依赖。`plugin-cli` / `create-nextcowork-plugin` 不移植(05 §3)。

**实现要点**(代码在 `src/Ncw.Agent.Plugins`,Host 接线在 `Ncw.Agent.Host/Plugins`,逐条行为见 [08.06 §7](08-agent-runtime/06-extensions.md#7-实现注插件宿主ncwagentplugins)):

- **每个插件一个引擎**(`PluginRuntime`):`JsProfile.Hardened` + `JsLimits`,宿主 API 全部来自 `PluginHostMethods` 方法表,**显式逐个绑定**(`PluginBinder` 是唯一调用 `SetFunction` / `SetAsyncFunction` 的地方)。每个绑定的调用先过四道门(2.x 的 RPC 四道门):① 白名单(没绑定 = `unknown_method`)→ ② 授权(**实时**读,撤权立刻生效)→ ③ 参数收窄(相对路径 + realpath 守卫、命令白名单、域名白名单)→ ④ 行为审批(执行命令)。
- **崩溃隔离**:超预算(时间 / 指令 / 内存 / 栈 / 输出)或引擎故障 = 引擎丢弃、插件 `faulted`、诊断上报、贡献撤下,Agent 和别的插件不受影响;插件自己 `throw` 不是故障(失败原因回给调用方);超时而插件当时在等宿主(IO / 用户)也不算故障。调用方取消、累计分配超过预算 75% 时在两次调用之间**透明重建**引擎(重新激活)。故障的插件要用户重新启用才会再试。
- **偏离 D14 原稿的地方**:① 不用 `JsPlugin.Create`(每次调用一个新 realm):插件是有状态的长驻引擎(命令注册、工具注册、实时卡片),按插件一个引擎 + 一把锁;② 没有 5 分钟休眠和激活事件按需唤醒:工具的描述 / schema 只有跑过 `activate` 才知道,所以启用的插件在启动 / 启用 / 安装后立即激活(`activationEvents` 只留作清单数据);③ 不用 `[JsSerializable]`(见上)。

### A8. 模型目录(models.dev 自动更新)

**数据源**:`https://models.dev/api.json`(2.x 实测大约 5.6MB)。它提供模型的上下文窗口、输出上限、能力(工具调用、推理、多模态)和参考价格。

**真源在 Agent**:由 `ModelCatalogService` 负责拉取、校验和合并,控制端通过协议读取,不自己去拉。手机只按需拉取已配置供应商的那部分。

| 方面 | 设计 |
|---|---|
| 时机 | Agent 启动 30 秒后拉一次;之后每 6 小时一次(设置里可以改);设置页有「立即更新」 |
| 省流量 | 条件请求(`If-None-Match` / `If-Modified-Since`),支持 gzip;返回 304 时只更新「上次检查时间」 |
| 防护 | 沿用 2.x `models-dev.ts` 的限制:30 秒超时、32MB 上限、不跟随重定向。User-Agent 只写 `NextCoWork/3.x`,不带任何用户或设备标识 |
| 校验 | 用 System.Text.Json 源生成器解析,未知字段忽略;一个模型的字段类型不对只丢这一个条目,整份照常采用。模型数量比上一份少一半以上(新数据 ≤ 上一份的一半,含恰好一半;空数据同样)视为异常数据,丢弃并保留上一份 |
| 存储 | 最近一份有效快照(原始响应体)存在数据根的 `catalog/models-dev.json`,另存 `catalog/models-dev.meta.json`(ETag、Last-Modified、拉取时间、上次检查时间、SHA-256、模型数);哈希对不上当作没有缓存。程序集里内嵌一份发版时的精简快照(`Seed/models-dev.seed.json`,和线上数据同一种格式、同一个解析器),首次启动离线也能用;远端数据一经采用就整份替换种子。用户覆盖(改名 / 窗口 / 能力 / 价格 / 删除)存在 kv 表的 `v3.catalog.overrides`,不和同步数据混存 |
| 失败 | 保留上一份。重试间隔从 15 分钟开始指数退避,最长 6 小时。设置页显示「上次更新于……」和最近一次错误 |
| 合并顺序 | 内置快照 ← models.dev 最新数据 ← 供应商自己的模型列表(`/models` 接口)← 用户覆盖(别名、自定义上下文窗口、自定义价格)。后面的覆盖前面的 |
| 价格 | 2.x 要用户确认后才采用同步来的价格;3.0 改为**自动采用**,只用于用量的费用估算(3.0 不涉及支付)。已经记录的用量费用不回算,沿用 2.x「写入那一刻就冻结」的规则。**只采用 USD 计价的一手厂商**(anthropic / openai / google / xai,沿用 2.x 的 `SYNC_VENDORS`):models.dev 没有币种字段,国内厂商里有人民币计价的,混进来会把 ¥ 当成 $。其余厂商的模型照常进目录,只是没有价格 → 费用是「未知」而不是 0;用户可以用本地覆盖填一个带币种的价格。查价顺序:用户价格覆盖 → 目录里同供应商 id → 同模型 id 的一手厂商 |
| 通知 | 目录变化(远端数据被采用、用户覆盖改了)后在 `catalog` 主题上发 `catalog.changed` 事件,负载是带版本号的全量状态 `CatalogTopicState`(版本、来源、检查时间、最近一次错误、用户覆盖;首帧与后续广播同一个类型,目录本身几千个模型不进主题);304 或内容一字不差时不改版本,但每次检查结束都会再发一份状态(「上次检查时间」「最近一次错误」,定时器自己发起的也算)。控制端的 Store(`ClientStores.Catalog`,key 带 deviceId)收到后按版本变化用 `catalog.query` 按需重新拉取。协议见 [09 §8.4](09-protocol.md#84-方法表) |
| 实现 | `src/Ncw.Agent.Catalog`:`ModelCatalogService`(HTTP、时钟、定时器都是注入的)、`ProviderService`、`IModelResolver`(把会话选的模型 / 供应商解析成路由配置)。供应商自己的 `/models` 列表只决定「这家有哪些模型」,元数据仍取目录。**Host 装配**(`Ncw.Agent.Host/Providers/ProviderRuntime`):数据就绪后创建,目录定时器随 Host 启停(默认 HTTP 是不跟随重定向、自动解压的 `HttpClient`;测试注入假 `IUpstreamHttp` 与定时器),价目表 = `CatalogPriceBook`,路由 `LlmRouter` 的用量写 `usage_records`(费用写入即冻结),凭据刷新的 `onChanged` 接 `ProviderService.OnCredentialChanged`;`providers.json` 在 profile 根下(数据根叫 `data` 时是它的上一级,否则就在数据根里);设置里可以关自动更新、改间隔(`catalog.settings.*`,存 kv 表 `v3.catalog.settings`) |
| 云端 | 云端 Agent 用同样的逻辑自己拉取。以后可以加一个 nextco.work 镜像作为首选地址,models.dev 作为后备,地址可配置 |

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
| 审批 | 消息流内联的审批卡片,支持快捷键(⌘↵ 允许,Esc 拒绝) | 消息流内联的审批卡片(触控版式:拒绝 + **长按允许**,按钮更大;危险操作要按更久)。**实现与最初稿的差异**:没有做成底部弹层,理由和防误触细节见 [03 §7](03-ui-layout.md#7-移动端m5-共享层实现说明) |
| 工具调用 | 默认显示一行摘要,可以展开 | 只显示一行摘要,点击后在导航栈里推出详情页 |
| 子代理 | 右侧检查器里有列表,卡片可以就地展开 | 点击后推入子会话页面(只读,运行态跟随父会话那条记录) |
| 终端 | 完整的交互式终端 | 只读查看输出,提供 Ctrl-C 和「发送一行」 |
| 文件 | 编辑器加 diff | 只读查看,diff 用上下排列的统一视图 |
| 输入框 | 底部固定,多行 | 底部固定,适配安全区和软键盘;附件可以直接拍照或从相册选 |
| 全局活动 | 右上角活动胶囊 | 底部标签栏的「收件箱」页:跨项目、跨设备的待办(审批、提问、计划、目标提案)和任务完成 / 失败通知,按设备分组(03 §7);正在运行的任务数显示在设备分组头上 |
