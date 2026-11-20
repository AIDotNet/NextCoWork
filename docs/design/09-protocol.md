# 09 · 设备协议(3.0 全新设计)

控制端(桌面、手机)与设备(`ncw-agent`:本机、云端)之间的协议。**不参考、不兼容 2.x 的设备协议**:帧格式、加密、配对、
中继、方法表都重新设计。2.x 的独立设备服务和配对记录不迁移。

## 1. 原则

1. **局域网直连优先,开关即用**。在设备上打开「允许远程连接」,局域网里的另一台控制端输入 `IP:端口` 就能连上。
2. **中继由服务器提供,协议一开始就为它留好了位置**(M6 已上线)。中继只是另一种「字节流承载」,安全层和上层协议完全不变,加中继没有改协议。
3. **不自研密码学**。远程连接的安全层用 TLS 1.3 双向认证;配对码验证用库里现成的 PAKE(SRP-6a)。
4. **一切可续传、可兜底**。订阅带游标;游标续不上就给权威快照(客户端状态规则 5);送不过来就让客户端重同步,不悄悄丢。
5. **方法即能力**。方法表声明种类、所需角色、所需能力;路由统一检查。
6. **协议版本独立于应用版本**。3.0 的协议从 `1` 开始。

## 2. 分层

```
L4 应用    方法(query / command / stream)、订阅(topic + cursor)、交互
L3 会话    hello / welcome、版本与能力协商、心跳、续连
L2 帧      4 字节长度前缀 + MessagePack
L1 安全    本机:操作系统权限 + 本机令牌;远程:TLS 1.3 双向认证(证书钉扎),ALPN = "ncw/1",用 BouncyCastle 的纯托管实现(见 §4)
L0 承载    本机:Unix socket / 命名管道;局域网:TCP;中继:经中继服务器拼接的 WebSocket 字节流
```

**承载抽象**:L1 以上只依赖一个双向字节流。

```csharp
public interface ITransportConnector            // 控制端:按地址类型选择连接方式
{
    bool CanHandle(DeviceEndpoint endpoint);    // Local / Lan(ip:port) / Relay(route) / 以后的其他类型
    Task<Stream> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct);
}
public interface ITransportListener            // 设备:每种承载一个监听器,接受到的连接交给同一个会话层
{
    IAsyncEnumerable<AcceptedStream> AcceptAsync(CancellationToken ct);
}
```

3.0 实现 `Local` 和 `Lan`;`Relay`(M6)已作为第三种承载加入:`RelayTransport`(控制端 connector)和 `RelayRegistration`(设备端 listener)在 `Ncw.Protocol/Transport/RelayTransport.cs`,L1 以上没有任何改动(见 §9)。

## 3. 局域网远程连接

### 设备端:远程开关

「设置 → 连接与远程」(设备作用域,见 [07](07-onboarding-settings.md)):

| 设置 | 默认 | 说明 |
|---|---|---|
| **允许远程连接** | 关 | 打开后 `ncw-agent` 在 `0.0.0.0` 和 `[::]` 上监听;关闭后停止监听并断开所有远程连接(本机连接不受影响) |
| 端口 | 7810 | 被占用时自动换一个,界面显示实际端口 |
| 仅允许局域网地址 | 开 | 只接受私有地址段的来源:`10/8`、`172.16/12`、`192.168/16`、`169.254/16`、`fc00::/7`、`fe80::/10`,以及 `100.64/10`(方便 Tailscale 等组网工具) |
| 配对码 | 自动生成 | 6 位数字,界面上显示;每次配对成功或连续 5 次错误后自动更换;可以手动刷新 |
| 在局域网中广播 | 开 | 通过 mDNS(`_nextcowork._tcp`)广播设备名和端口,方便其他控制端发现 |

界面显示:本机的局域网地址列表(`192.168.1.23:7810` 这样)、当前配对码、配对二维码、已连接 / 已配对的控制端列表(可以改角色、吊销)。

- 打开开关时,系统防火墙可能弹窗。Windows 安装器预先为「专用网络」添加入站规则,不放开公用网络。
- 监听由 Agent 负责,所以「关窗后 Agent 继续运行」时远程连接也继续可用。

### 控制端:添加设备

「设备 → 添加设备」提供三种方式,结果都是一条相同的信任记录:

1. **局域网发现**:列出 mDNS 发现的设备,点选后输入配对码。
2. **手动输入**:输入 `IP:端口`,再输入配对码。
3. **扫二维码**(手机):二维码里带地址、设备证书指纹和一次性高强度密钥,扫完不用输配对码。

### 首次配对(IP + 端口 + 配对码)

```
1. 控制端生成自己的密钥对和自签名证书(只生成一次)
2. 控制端 TCP 连接 IP:端口,TLS 1.3 握手:双方都出示自签名证书,此时还不信任对方(先记下对方证书)
3. 在这条 TLS 连接里跑 SRP-6a(用户名 = deviceId,口令 = 配对码),得到共享密钥 K
   —— SRP 是 PAKE:中间人拿不到可以离线暴力破解的数据,只能在线猜,设备端限速 + 5 次失败就换码
4. 双方用 K 对「TLS exporter 值(标签 `EXPORTER-ncw-pair`,32 字节)+ 双方公钥指纹」做 HMAC-SHA256 并交换校验
   —— 把配对码的验证绑定到这条 TLS 会话和这两把公钥上,中间人无法转接
5. 校验通过:设备保存信任记录 { 控制端证书指纹, 名称, 角色, 时间 };控制端保存 { deviceId, 设备证书指纹, 已知地址 }
6. 设备弹出通知「新控制端 iPhone(张三)已配对」,带「吊销」按钮;可选开启「新控制端需要在本机确认」
```

- SRP-6a 用 BouncyCastle(纯托管)的实现,不手写:RFC 5054 的 2048 位组,SHA-256。M2 已验证它在 NativeAOT 下可用(零裁剪警告)。
- **消息**:未配对的控制端也能完成 TLS 握手和 `hello`,但 `welcome.role = "unpaired"`,只能调用 `pair.*`,不能订阅。
  `pair.begin { clientName } → { salt, b }`;`pair.finish { a, m1, proof } → { m2, proof, role, routeToken }`。
  设备连续 5 次校验失败就换码;成功后控制端断开,按钉扎的设备指纹重新连接,得到正式角色。
- 之后再连接:TLS 双向认证时直接按指纹校验,不再需要配对码。设备的 IP 变了也没关系,身份看证书,不看地址。

### 扫码配对

二维码:`nextcowork://pair?v=1&d=<deviceId>&f=<设备证书 SHA-256>&a=<地址列表>&t=<一次性密钥 128bit>&e=<过期时间>`。
控制端连接后先按 `f` 校验设备证书,再出示 `t`(一次性、10 分钟过期、常数时间比较)。二维码本身是可信的带外通道,所以不需要 SRP。
实现:`a` 是逗号分隔的 `host:port`(IPv6 加方括号),`f` 和 `t` 用 base64url;控制端依次尝试地址,TLS 握手就按 `f` 钉扎(换了证书直接握手失败),再调 `pair.claim { clientName, token }`。设备最多同时保留 8 枚有效密钥,关闭远程开关时全部作废;错误的密钥不消耗真密钥,但每次失败都有延迟。

## 4. 身份、信任与角色

- **身份密钥**:ECDSA P-256 自签名证书(.NET 在所有目标平台内置支持)。`deviceId` = 设备公钥的 SHA-256(截断后 base32)。
  设备私钥在数据根里用 `NCK1` 加密存储(云端用 KMS);控制端私钥存在系统钥匙串(Keychain / Keystore / DPAPI / libsecret)。
- **控制端身份的保存**(桌面端,`Ncw.Desktop/ControllerKeyStore`):macOS 存钥匙串的通用口令(服务「NextCoWork Controller」),
  Windows 用 DPAPI(当前用户)加密的文件;Linux 的 libsecret 尚未接入,在那之前拒绝保存(配对时提示),不降级为明文。
  已配对设备列表(设备 id、钉扎指纹、地址、路由令牌,不含私钥)存在客户端目录的 `devices.json`(0600)。
- **指纹** = 证书里公钥(SubjectPublicKeyInfo)的 SHA-256。按公钥而不是整张证书钉扎,证书到期换发时信任不受影响。
- **TLS 实现**:用 BouncyCastle 的 TLS 1.3(纯托管),不用 `SslStream`。M2 验证结果:macOS 的 `SslStream` 走 SecureTransport,最高只有 TLS 1.2
  (Network.framework 模式只接管客户端,服务端仍是 1.2,握手失败);iOS 同样受限;Windows 10 的 SChannel 不支持 TLS 1.3 服务端;
  .NET 也不提供 TLS exporter。BouncyCastle 在所有平台(含移动端、NativeAOT,零裁剪警告)行为一致,并提供 exporter。
- **TLS 校验**:不依赖 CA,按信任记录里的指纹钉扎;未配对的控制端在设备上得到 `unpaired` 角色(见 §3)。
- **控制端记录的设备地址**:`{ lan: [最近成功的 ip:port…], relay?: route }`。连接时并行尝试所有地址,先通的胜出,
  局域网优先;身份由证书保证,所以地址可以随便试。**已实现**(`RemoteDevices.ConnectAsync(deviceId, DeviceAddresses, …)`,见 §9):局域网地址立刻开始,中继晚 250 毫秒起跑(或所有局域网地址都失败时立刻起跑),
  第一条完成 TLS 握手(设备指纹钉扎)的胜出,其余取消并关闭。`SavedDevice.Relay` 是中继地址,和 `RouteToken` 合起来就是 `relay: route`;`Host` 为空表示没有局域网地址(云端设备)。
- **角色**:`owner`(全部)、`member`(对话、审批;不能改设备设置和权限模式、不能开 `full` 模式)、`viewer`(只读)。
  经局域网配对的控制端默认 `member`,设备上可以改。
- **吊销**:设备删除信任记录 → 使用该证书的连接立即断开(局域网和中继的会话都是),同时把对应的路由令牌哈希从中继上撤掉(中继掐断用它的会话,令牌之后一律 `unauthorized`)。**已实现**。
- **云端设备**(**M6 已实现 Agent 侧**,确定方案见 [02 A6.1](02-agent.md#a61-云端-agent-的-agent-侧实现m6)):不需要配对码(`pair.*` 返回 `unsupported`,key `cloud.pairing.unsupported`)。
  设备密钥在沙箱里生成,启动时向编排服务证明持有私钥,编排服务回签名的**身份绑定**;控制端登录账户后把证书公钥注册到账户 API,
  编排服务用**签名指令**(`trust.put` / `trust.revoke` / `trust.replace`,见 [§13](#13-编排控制通道m6agent--编排服务))把信任记录(指纹、名称、角色、路由令牌哈希)下发给云端设备,
  并把云端设备证书指纹和中继路由返回给控制端(通过 nextco.work 的 HTTPS,由账户登录保证可信)。云端设备只经中继,不监听局域网。
  吊销 = 立即断开该控制端的所有会话并撤掉它的路由令牌哈希(和局域网配对的吊销同一条路径);休眠期间到达的吊销在唤醒后、向中继注册**之前**生效。

## 5. 本机连接

- macOS / Linux:`<数据根>/run/agent.sock`,权限 0600;Windows:命名管道 `\\.\pipe\ncw-agent-<用户 SID>`,ACL 只允许当前用户。
- 不跑 TLS。桌面端拉起 sidecar 时通过环境变量传入本机令牌,`hello` 里出示(纵深防御)。本机连接的角色固定为 `owner`。令牌跨桌面端会话保留在 `<数据根>/run/sidecar.json`(0600,和 socket 同一个信任边界),这样「关窗后继续运行」的 Agent 下次启动能被接上;桌面端停掉 Agent 时删除,下次换新令牌(见 [04 §7.2](04-update-install.md))。

## 6. 帧(L2)

- `uint32` 大端长度 + MessagePack 负载。最大帧 4 MiB(在 `hello` 里协商);更大的数据走流式方法分块传输。
- MessagePack 用**字符串键**,源生成器序列化;未知字段忽略,便于向前兼容。
- 开发模式下 `ncw-agent` 可以把帧以 JSON 形式打到日志,发布版不提供。

## 7. 会话(L3)

```
→ hello   { protocol: [min, max], clientId, clientVersion, formFactor,
            capabilities: { features[], coalesceMs, maxFrame }, localToken?, resume? { token } }
← welcome { protocol, deviceId, deviceVersion, role, capabilities: { features[] }, resumeToken, serverTime, transport }
← reject  { code, message }        // 版本不兼容、未授权、客户端版本过低等
```

- **拒绝必达**(不改线上格式,只约束关闭时机):`reject` 之后设备**不立即释放连接**——关闭一条对端还有未读 / 在途数据的 TCP 连接会让内核发 RST,RST 会把还没被对端读走的 `reject` 丢掉,控制端看到的就成了「连接丢失」(可重试的断线,`DeviceLink` 会不停重连),而不是明确的 `unauthorized` / `client_outdated`(`DeviceLink` 据此标记已吊销、不再重试)。
  所以 hello 阶段**所有**拒绝路径(未授权 / 令牌绑定不符、客户端过旧、协议版本不兼容、期望 hello)发完 `reject` 后统一走 `RejectLinger`:只读并丢弃对端继续发来的数据,直到对端自己关闭(读到 `reject` 后控制端本来就会关)再释放。
  排空有边界(不是拒绝服务面):每条连接最多 64 KiB、最多 2 秒,同时排空的连接最多 32 条,超限直接中止(等同旧行为);会话取消(停机)不再排空。局域网 TCP、本机 socket、经中继的字节流同一套逻辑(`ServerSession` 内,与承载无关)。
- **能力声明**:方法表里每个方法声明它属于哪个能力(`Capability`,缺省 = 协议 v1 的基础能力);设备在 `welcome.features` 里列出自己已注册方法所属的能力(M3 起有 `conversation`,之后有 `catalog`、`providers`、`subagents`、`planning`),控制端据此决定能调哪些方法、订阅哪些主题,老设备缺哪个能力就降级,不报错。
- **心跳**:控制端每 20 秒发一次 `ping`,设备回 `pong`;**连续 3 个间隔(60 秒)没有收到对端的任何一帧**就判定连接已死、主动断开(设备端对控制端同样:`ServerOptions.IdleTimeout` 60 秒内没有任何帧就断)。手机进后台时直接断,回前台再连。
  这条不只是礼貌:对端**静默消失**(NAT 映射超时、手机切网、中间设备丢包,也包括承载被一端中止而 RST 在路上丢了)时,既没有 FIN 也没有 RST,控制端的读会**一直挂着**,TCP 自己要几十分钟才发现——设备在界面上会一直显示「已连接」、运行中的状态一直不收口。
  所以控制端的 `DeviceClient` 带看门狗(`HeartbeatOptions`:间隔、连续几次、可注入的时钟,出厂 20 秒 × 3):任何一帧(不只是 `pong`)都算「还活着」;超时后 `DeviceClient.Completion` 带 `HeartbeatTimeoutException` 完成,未完成的请求以可重试的 `unavailable` 失败,死连接被关掉(挂着的读随之结束,关闭有 5 秒上限,不会挂住调用方),`DeviceLink` 走和普通断线完全相同的路径:设备标「连接中」、数据状态**未知**(AGENTS.md §3 规则 7:不判结束、不继续计时)、退避后自动重连。看门狗与 ping 是两条独立的循环——对端消失时 ping 的写可能挂在写满的发送缓冲上,不能拖住判定。
  不改线上格式:`ping` / `pong` 帧本来就在协议里,没有新增帧也没有新增能力开关,不需要升协议版本、不动黄金向量。局域网 TCP、本机、经中继的字节流同一套逻辑(在 `DeviceClient` 里,与承载无关);中继承载的 `ClientWebSocket` 虽然配了 `KeepAliveInterval`(20 秒的 WebSocket ping),但 .NET 的 `KeepAliveTimeout` 默认是无限,对端不回 pong 也不会断,所以不能指望它。
- **续连**:`resumeToken` 在设备上保留 5 分钟,包含订阅列表和各订阅已确认的游标。**续连可以换承载**:
  局域网断了改走中继,订阅从游标继续,上层感知不到(控制端每次重连都重新选路,游标是控制端按订阅记的,和承载无关)。
- **版本**:取双方支持范围交集里的最大值;3.x 的客户端和设备都至少支持 N 和 N-1。同一大版本内的新增功能用 `features` 开关。

## 8. 应用层(L4)

### 8.1 请求与响应

```
→ req    { id: u64, method, params, idem? }
← res    { id, ok: true, result } | { id, ok: false, error: { code, message, retryable, retryAfterMs?, details? } }
→ cancel { id }
```

| 方法种类 | 语义 |
|---|---|
| `query` | 只读,可以安全重试 |
| `command` | 有副作用;**必须带幂等键** `idem`。设备保存 24 小时内的「幂等键 → 结果」,重复请求直接返回原结果(续连、换承载后重发也安全) |
| `stream` | 返回流 id,之后用 `chunk { streamId, seq, data, end? }` 双向传输,带信用值流控。用于大文件、附件上传、终端。**3.0 暂未实现**:大文件用 `files.readRange` 分块读(query,带 `revision`),终端用 `terminal/<id>` 主题 + `terminals.write`,不需要信用值流控 |

`error.details` 是方法自己约定的结构化补充(MessagePack 编码的 DTO,`ProtocolError.Details` / `ProtocolException.Details`),没有补充为空:例如 `interactions.respond` 的 `conflict` 带 `InteractionConflictDto`(谁、什么时候处理的)。新增 `details` 只增键,未知键忽略,不升协议版本。

错误码:`invalid_request`、`unauthorized`、`forbidden`、`not_found`、`conflict`、`busy`、`rate_limited`、`unsupported`、
`too_large`、`unavailable`、`client_outdated`、`internal`。

### 8.2 订阅

```
→ sub      { id, topic, cursor? }
← snapshot { topic, seq, state }          // 游标为空,或游标已不在日志里
← evt      { topic, seq, type, data }     // 之后的增量,seq 严格递增
← resync   { topic, reason }              // 设备无法继续,客户端必须不带游标重新订阅
→ unsub    { id }
```

| 主题 | 内容 | 快照 |
|---|---|---|
| `device` | 活动 Run 索引(含子代理和待处理交互数)、项目列表版本;事件 `activeRuns.changed`、`projects.changed` | **这就是权威广播**:首帧和后续增量是同一个类型 `DeviceTopicState`(`runs`、`subagents`、`projectsRevision`、单调的 `rev`),由 Agent 上 Core 的唯一生成函数 `ActiveRunIndexBuilder` 产出;在 Run 创建、`run_end`、交互发起 / 处理时广播,运行期间每 30 秒兜底重播。控制端只收 `rev` 更新的那份,并拿它给本地的 Run 镜像对账(丢了结束事件的 Run 在这里收口) |
| `session/<id>` | 会话元数据 + 最近一页消息、排队消息、正在运行的顶层 Run;事件 `session.updated` / `queue.updated`(全量,`rev` 全局单调)/ `message.committed`(消息落库,按 id 追加或替换)/ `history.reset`(历史被改写,整页替换)/ `session.deleted` | `SessionTopicState`。**前缀主题**:有人订阅才存在,最后一个订阅者离开就丢弃。另有两个子代理事件:`subagent.report`(后台子代理的汇报进度变了,`SubagentReportDto`;`injecting` 不落盘所以走事件)、`subagent.stopped`(一个子 Run 结束了,`SubagentStoppedDto`;后台子代理常比父 Run 活得久,父 Run 日志那时已收口、`subagent_end` 进不去,靠它收口卡片) |
| `run/<id>` | Agent 事件(见 [08.02 §3](08-agent-runtime/02-loop.md#3-事件流)),**主题的 `seq` 就是 Run 事件日志的序号**,游标就是 `sinceSeq`。事件里包括子代理层 `Post` 进来的 `subagent_start / update / end`(子 Run 的状态镜像,见下) | `RunSnapshotDto`:Run 状态 + 日志里的事件 + 挂起交互 + 子 Run。**来源主题**:序号和游标由 Run 注册表管理(见下) |
| `goal` | 本设备上所有会话的目标;事件 `goal.changed`。能力 `planning`,viewer 起 | `GoalsTopicState`(`Rev` + 内存里所有会话的目标)。**首帧与增量同一个全量语义**:`GoalChangeDto` 是一个会话的目标全量(`Goal = null` = 没有,带清除原因 `Cleared`)+ 带外写了 `goal_status` 标记的那条助手消息(按 id 替换会话镜像里的那条)。`Rev` 是设备上目标变化的单调版本号(版本号与推送在同一把锁里,推送的目标取此刻的真源,所以最后一条一定是终局);控制端按 `(deviceId, sessionId)` 镜像、只收 `Rev` 不更旧的,**清除留带版本号的墓碑**,迟到的旧设定复活不了它。目标是进程内状态:快照只含内存里已有的(会话被打开 / 恢复时才进来),取某个会话的用 `goal.get`(它先恢复,恢复本身也会广播) |
| `extensions` | Skills / 命令 / Hooks 三个域的**修订号**;事件 `extensions.changed`。能力 `skills` / `commands` / `hooks` 任一在即可订阅,viewer 起 | `ExtensionsTopicState`(`SkillsRev` / `CommandsRev` / `HooksRev`):这几个域的清单是**按项目取**的(`skills.list` 等带 `workspaceId`),放不进一个全量快照,所以主题只广播修订号(设备上每次成功改动加一,单调;修订号递增与推送在同一把锁里,首帧不取锁,避免与主题表的锁反序)。首帧与增量同一个类型;控制端逐域取「不更旧」的,发现前进了就把自己镜像里**持有的**清单重拉一遍(没人看的清单不拉);设备断开标未知,重连首帧到来后全部重拉 |
| `mcp` | MCP 服务器的配置与连接状态;事件 `mcp.changed`。能力 `mcp`,**member 起**(viewer 不能订阅:服务器配置属于设备配置) | `McpTopicState`(`Rev` + 全部服务器按 id 排序,每台 = 配置〔只有键名,没有密钥值〕+ 运行状态 `connecting` / `connected` / `failed` / `disabled`、稳定的错误 key + 结构化参数、已接入的工具名、被拒绝的工具及原因 key、重连次数)。**快照 = `mcp.changed` = `mcp.list` 是同一个类型**(配置和状态合成一行:分两份各自到达的话,中间那一帧必有一边是旧的),`Rev` 单调,控制端只收不更旧的、断开标未知。服务器的错误原文 / stderr 不进主题 |
| `plugins` | 已安装插件的清单、状态、授权、诊断、注册的命令 / 工具,以及插件正在等用户回答的提问;事件 `plugins.changed`。能力 `plugins`,**member 起**(清单里有命令白名单 / 域名和诊断,提问里有待批准的命令行) | `PluginsTopicState`(`Rev` + `ApiVersion` + 全部插件按显示名排序 + 挂起的提问)。**快照 = `plugins.changed` = `plugins.list` = 每个变更方法的返回值是同一个类型**,`Rev` 单调,控制端只收不更旧的、断开标未知(规则 5–7)。状态 `idle` / `activating` / `active` / `disabled` / `pending-approval` / `error` / `faulted`;提问(`PluginInteractionDto`)是确认 / 输入 / 选择 / 运行时权限申请 / 执行命令的审批,界面文字一律是 l10n key,**涉及执行的带设备 + 环境标识**。活动日志不进主题(`plugins.activity` 按需取) |
| `environments` | 本设备上全部执行环境(`local` + 配置的 SSH 环境)的配置与连接状态;事件 `environments.changed`。能力 `environments`,**member 起**(清单里有内网主机名和用户名,属于设备配置) | `EnvironmentsTopicState`(`Rev` + `DefaultId` + 全部环境:种类、状态 `idle` / `connecting` / `ready` / `reconnecting` / `failed`、最近错误 key〔指纹不一致时带 `HostKeyMismatchDto`〕、延迟、是否默认、配置〔路径只以 `Has*` 出现〕、环境事实)。**快照 = `environments.changed` = `environments.list` 是同一个类型**,`Rev` 单调(在发布锁里递增、推送同锁),控制端只收不更旧的、断开标未知(规则 5–7)。状态只由各环境实例回答,凭据永远不在里面 |
| `interactions` | 本设备所有挂起的交互;事件 `interactions.changed` | 全量 `InteractionsDto`(`revision` + 全部挂起交互),快照与增量同一个类型(没有增量:收到更新的就整体替换) |
| `data` | 设备数据状态:`preparing` / `taking-over`(带步骤)/ `ready` / `blocked` / `failed`(带原因),刚完成 2.x 接管时附摘要 | 当前状态;事件 `data.changed` 用同一个类型(见 [04 §4](04-update-install.md#4-数据接管直接接管-2x-数据)) |
| `catalog` | 模型目录的版本、状态(来源、上次检查时间、最近一次错误)和用户覆盖;事件 `catalog.changed`。能力 `catalog`,viewer 起 | `CatalogTopicState`:**首帧与事件同一个类型**(全量:`Status`(带单调的 `Version`)+ `Overrides`)。目录本身有几千个模型,不进主题:控制端看到 `Status.Version` 变了,用 `catalog.query` / `catalog.providers` 按需重拉。远端数据被采用、用户覆盖改了、每次检查结束(304、失败、被拒、定时器自己发起的也算)都发一份;304 与内容一字不差不改版本 |
| `providers` | 供应商、模型别名、读 `providers.json` 的诊断、走账号登录的供应商的账号;事件 `providers.changed`(全量)、`providers.accounts.changed`(一家的账号,整体替换)。能力 `providers`,**member 起**(不含任何凭据,只有「有没有 key」) | `ProvidersTopicState`:首帧与 `providers.changed` 同一个类型,`Version` 单调,控制端只收不更旧的;设置 / 删除 key、登录这类不改快照版本的变化,同一个版本再发一份(所以是「不倒退」而不是「严格递增」) |
| `providers.oauth` | 正在进行的那一轮登录的进度(授权页由控制端打开);事件 `providers.oauth.progress`。**owner 才能订阅**(授权 URL 带 `state`) | `OAuthTopicState { Current }`:当前进度,没有登录在进行为 null。终态(`succeeded` / `failed` / `cancelled`)事件后这一轮结束 |
| `terminal/<id>` | 终端输出(二进制)。**来源主题**:`seq` 就是 `TerminalManager` 输出批的 `Seq`(每个终端内严格连续,退出事件也占一个),游标就是上次收到的最后一个 `seq`;事件 `terminals.data` / `terminals.exit`。能力 `terminals`,viewer 起 | `TerminalSnapshotDto`:终端元数据 + 整个环形缓冲(最近 256 KiB)+ `Seq`。**游标还在日志里**:不发快照,只补发游标之后的事件帧;游标不在了或不带游标:给快照(控制端用它重置显示)。「取快照 + 挂订阅」在管理器的锁里原子完成。终端输出**没有可丢的增量**(每批都是结构性的):发送缓冲满(8 MiB)时对该主题发 `resync`,控制端不带游标重订、拿整个缓冲;终端不存在(从没有过或已回收)`not_found` |

**`run/<id>`(attach)的订阅规则**(实现:`Ncw.Agent.Host` 的 `RunTopicSource`,编排层 `RunRegistry.Attach`):

- **不带游标**(首次订阅)或游标已被日志上限挤出去(`lost`):发一个 `RunSnapshotDto`(含日志里的全部事件,`cursor = fresh | lost`),控制端据此从头折出状态。
- **游标还在日志里**:不发快照,只补发游标之后的事件帧(`evt`,`seq` 是 Run 日志的序号),然后实时推送。日志裁剪后序号有空洞,控制端**不**做「`seq == last + 1`」检查,直接以收到的最大 `seq` 为游标。
- **「取快照 + 挂订阅」在 Run 的锁里原子完成**:快照里的事件不会再推一遍,快照之后的事件一条不漏。所以协议层不需要「先订阅再启动」:`runs.start` 之后再订阅,日志重放保证不丢(`RunId` 仍由控制端生成,续连后重发同一个幂等键拿到同一个结果)。
- 每个订阅有自己的事件泵,按控制端 `hello.coalesceMs` 合并文本增量(结构性事件立即送,并带上它前面攒着的增量);发送缓冲满(§ 下面的流控)时只有增量的批整批丢弃,含结构性事件的批改发 `resync`。
- 无损裁剪:`message_end` 之后清该消息的增量、`tool_end` 之后清该调用的进度,所以断线期间错过的增量不会出现在之后的快照里(完整消息已在 `message_end` 里)。
- Run 不存在(从没有过,或已结束且被回收)返回 `not_found`:控制端退回库里的终态(`sessions.get` / 会话主题)。
- **子代理**:父 Run 的主题只转发**根 Run 自己的事件**(`seq` 是它的序号),其中包括子代理层经 `RunRegistry.Post` 追加的 `subagent_start`(子 Run id、**子会话 id 由设备带来**,控制端不许从 id 里拼;排队中的子代理还没有这个事件,排位走带结构化 `DetailsJson` 的 `tool_progress`:`{"kind":"queued","ahead":N}`;日志在 `tool_end` 之后仍保留它,且它永远排在这次调用的 `tool_start` 之后,见 05 §2)、
  `subagent_update`(遥测镜像:阶段、当前工具 / 目标、工具数、用量、上下文占用、重试提示;**键不存在 = 保持,补丁类型 `StringPatchDto` / `SubagentNoticePatchDto` 的 `Value` 为空 = 清除**)和 `subagent_end`(终态 + 240 字摘要 + 错误)。它们和其它事件一样享有游标续传与「终态之后丢弃」。
  子 Run 自己的完整事件流(逐字转录)是它自己的 `run/<childRunId>` 主题:控制端点开子代理面板时才订阅(没点开的子代理一个字都不占),所以**不需要「设备主动推送的子主题」**——服务端只回应订阅,不替控制端订阅;早已回收的子 Run 订阅得到 `not_found`,控制端退回库里的子会话。

- 同一主题内严格按 `seq` 有序;不同主题之间不保证顺序。
- **流控**:设备按每个控制端的 `coalesceMs` 合并增量;每条连接的发送缓冲上限 8 MiB(`ProtocolInfo.MaxPendingBytes`,按排队帧的估算字节数计)。超过时,`run/*` 先丢增量(`ITopicWriter.TryWrite` 返回 false,订阅方整批丢弃);
  结构性事件(`message_end`、`tool_end`、`run_end` 等)**永不悄悄丢弃**,送不出去就对该主题发 `resync`(控制端自动不带游标重新订阅)。初始帧(快照 / 补发)总是入队。

### 8.3 交互

回答用 `interactions.respond { id, expectedVersion, response }`(command)。已被处理时返回 `conflict`,
并带上由哪个控制端在什么时候处理(`resolvedBy`、`at`、`status: answered | aborted`),界面显示「已在另一台设备上处理」。
`expectedVersion` 是控制端看到的那次交互的版本(登记时的设备修订号,全设备唯一、只增);仍挂起但版本对不上也是 `conflict`(带当前版本)。
`conflict` 的 `details` 是 `InteractionConflictDto`:`reason = alreadyResolved`(带 `resolution`:`resolvedBy` 是回答者的 clientId,中止时为空;`at`、`status`)或 `staleVersion`(带 `currentVersion`)。
回答校验失败是 `invalid_request`,且不消耗这次交互;从没有过的 id(或早已被历史淘汰)是 `not_found`。
**SSH 的两种交互**(`ssh_host_key`:首次出现的主机指纹确认;`ssh_credential`:密码 / 私钥口令 / 键盘交互式问答,见 [06 §10.4](06-environments-ssh.md))是**设备级**交互:`RunId` 是 `env:<环境 id>:<ulid>` 这样的作用域,不属于任何会话 / Run,不进任何 Run 的事件流,只经 `interactions` 主题到达所有控制端(先到先得,带执行环境标识)。
请求里没有凭据;**回答里有秘密**(`SshCredentialResponseDto` 的 `Answers`):它只出现在 `interactions.respond` 的**请求**里(控制端 → 设备单向),`interaction_resolved` 事件不带回答(只有谁、什么时候),设备从不把它发回任何控制端。`interactions` 主题的快照与增量用同一个全量类型(`revision` + 全部挂起交互,实现见 08.03 §9)。

### 8.4 方法表

方法名格式 `domain.verb`,完整清单按 [10 功能盘点](10-inventory.md) 的领域随里程碑补齐。每个方法在 `Ncw.Protocol` 里声明:**种类**(query / command)、**所需角色**、**所需能力**、**允许的承载**、参数和结果的类型形状(源生成,AOT 友好)。路由统一检查角色、承载、幂等键,方法实现里不再各自判断;command 没带 `idem` 是 `invalid_request`。

| 领域 | 例子 |
|---|---|
| `pair` / `trust` | `pair.begin` / `pair.finish`(SRP,`unpaired` 可调,仅远程)、`pair.claim`(二维码,远程可调)、`pair.createQr`(生成二维码,仅本机通道)、`trust.list`、`trust.revoke`、`trust.setRole`(仅本机通道) |
| `remote` | `remote.getStatus`、`remote.setEnabled`、`remote.rotateCode`、`remote.configure`(改端口、仅允许局域网地址、**中继地址**;正在监听时重新监听,远程连接随之断开重连)(仅本机通道)。`RemoteConfigureParams.RelayUrl`(null = 不改,空串 = 关闭中继)和 `RemoteStatus` 的 `RelayUrl` / `RelayState`(`off` / `connecting` / `registered` / `failed`)/ `RelayError` 是 M6 追加的带默认值的末尾字段,旧载荷照常解码,冻结向量见 `GoldenRelay` |
| `projects` / `environments` | `projects.list/add/remove`(SSH 项目:`add` 带 `EnvironmentId`,`list` 带 `EnvironmentId`)、`environments.*`(**M4 已实现**,见 §8.6) |
| `sessions` / `runs` / `queue` / `interactions` / `tools` / `context` | **M3 已实现**,见下表 |
| `files` / `git` / `review` / `terminals` | **M4 已实现**,见下面的「工作区工具」。`files.read` 的「大文件分块」用 `files.readRange`(协议没有 `stream` 种类,留给以后);06 §9.4 的 `terminals.*` 方法表**取代**早先草案里的 `terminals.open`(stream) |
| `catalog` / `providers` | **已实现**,见下面的「目录与供应商」 |
| `extensions` | `skills.*`、`mcp.*`、`plugins.*`、`hooks.*` |
| `settings` / `usage` / `schedules` / `storage` / `search` / `attachments` / `composer` / `widgets` / `gateway` / `websearch` | **M4 已实现**,见下面的「杂项 Agent 服务」 |
| `diagnostics` | `diagnostics.logs`(stream)、`diagnostics.bundle` |
| `data` | `data.status`(query,viewer)、`data.retry`(command,owner,仅本机通道;状态是 `rolled-back` 时表示用户要求重新迁移)。数据没就绪时,依赖数据的方法返回 `unavailable`(可重试)。回退到 2.x(04 §5,都是 owner + 仅本机通道):`data.rollbackInfo`(query:能不能回退、有没有已登记的请求)、`data.requestRollback`(command:只登记,Agent 关库之后执行;参数必须确认数据损失,否则 `invalid_request` + key `data.rollback.notAcknowledged`;不能回退时 key `data.rollback.unavailable`)、`data.cancelRollback`(command)。`data` 主题的状态新增 `rolled-back`(协议仍是 v1:只新增方法、载荷类型和一个状态值,黄金向量 `GoldenV1DataRollback`) |
| `cloud` / `git.credentials` | `cloud.getStatus`(query,viewer)、`cloud.clearInterrupted`(command,member):云端设备的状态、配额、用量和被休眠打断的 Run,**只有 `--mode cloud` 的设备注册并声明能力 `cloud`**(**已实现**,见下面「云端」)。`git.credentials.set` / `remove` / `list`(owner;令牌只进不出,能力 `git.credentials`,两种模式都注册) |
| `account` / `push` | `account.attachDelegatedToken` / `detachDelegatedToken` / `getStatus`(**已实现**,仅本机通道,见下面「账户委托」)、`push.register`(**已实现**,见 §10) |

`remote.*`、`account.*` 只允许本机通道:远程控制端不能关掉远程开关或改配对码。

**对话链路**(能力 `conversation`,承载 本机 + 远程;viewer 只读、member 可对话审批、owner 全部):

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `sessions.list` | query | viewer | 项目下的会话(侧边栏一行:标题、更新时间、归档、收藏)。**不含「是否在跑」**:那只由 `device` 主题的 activeRuns 回答(一个事实一个真源) |
| `sessions.get` / `sessions.getPage` | query | viewer | 整段详情 / 一页(`limit`、`beforeMessageId`):消息、产生它们的 Run、用量、模型 |
| `sessions.create` | command | member | 给了 `id` 时用 Ensure 语义(同 id 已存在返回现有的,不覆盖);没给模型时按项目默认模型 → 设备默认模型整对补上,都没配留空 |
| `sessions.rename` / `setMode` / `setModel` / `archive` / `favorite` | command | member | 返回更新后的 `SessionDto`;`setMode` 同时让排队里还没被消费的条目跟着换模式 |
| `sessions.delete` | command | **owner** | 先中止并等 Run 收尾、杀属于它们的后台 Shell、丢排队状态,再删库(含子代理转录) |
| `sessions.duplicate` / `branch` | command | member | 复制整条 / 复制到某一轮为止的前缀,新会话是独立的顶层会话 |
| `sessions.editMessage` / `deleteTurn` / `deleteReply` | command | member | 改写历史。占会话互斥:Run 在跑 → `busy`(可重试);成功后会话主题发 `history.reset` |
| `runs.start` | command | member | `runId`、`messageId` 由控制端生成;会话忙时进队列(`outcome = queued`,不是失败);`permissionMode = full` 只有 owner |
| `runs.abort` | command | member | 幂等,不存在(已回收)的 Run 不报错;`cascade` 级联子 Run |
| `runs.steer` | command | member | 插话 = 入队再引入(下一个回合边界送进正在跑的 Run) |
| `runs.background` | command | member | 把前台子代理挪到后台(`RunId` 是**子 Run** 的 id;顶层 Run → `invalid_request`,不存在 / 还在排队没有 Run → `not_found`;转成了、已跑完、本来就是后台都**不是错误**:结果经父会话里那次 `Task` 调用的回执 `background: true` 到达卡片) |
| `runs.setPermissionMode` | command | **owner** | 运行中改权限档位,只影响还没做出的判定 |
| `interactions.list` | query | viewer | 全量 `InteractionsDto` |
| `interactions.respond` | command | member | `{ id, expectedVersion, response }`;先到先得,`conflict` 带 `InteractionConflictDto` |
| `queue.list` | query | viewer | 某会话的排队消息(全量) |
| `queue.enqueue` / `promote` / `remove` | command | member | 回执 `QueueResultDto { accepted, queue }`:`accepted = false` = 被拒绝(超 20 条、同 id、找不到),队列没变,控制端据此保留草稿;`promote` 是开关;落盘失败 `unavailable`(可重试),绝不回 accepted |
| `tools.list` | query | viewer | 内置工具(名字、描述、只读 / 破坏性 / 联网、输入 schema) |
| `context.compact` | command | member | 手动压缩,占会话互斥(忙 → `busy`);结果是边界消息或结构化错误 |
| `context.preview` | query | viewer | 现在这段历史占多少上下文(与每回合 `context_usage` 同一套估算),各档之和恒等于总数 |

**目录与供应商**(能力 `catalog` / `providers`,承载 本机 + 远程;实现:`Ncw.Agent.Host/Providers`,领域层 `Ncw.Agent.Catalog`)。真源在 Agent 的 `ModelCatalogService` / `ProviderService`:控制端只通过协议读取,不自己请求 models.dev。
角色:目录读取 viewer 可;写入(刷新、覆盖、删除 / 恢复、同步设置)和 `providers.*` 的写入、凭据、账号、登录默认 owner;只读的供应商配置、模型选择器候选、解析、账号列表 member 可。
**凭据 / 令牌永远不出现在任何 DTO 或事件里**:`providers.list` 与主题只带「有没有 key」(`HasCredential`),`credential.info` 只回后四位和登录态摘要(邮箱、计划、过期时间,没有 token),
明文只经 `providers.credential.reveal` 单次返回(仅 owner、仅本机通道)——以及方向是控制端 → Agent 的三个请求参数(`credential.set` 的 key、`oauth.submitCode` 的回调码、`providers.debug.*` 调试草稿的 key)。
主题 `providers.debug/<runId>`(前缀主题,owner):一次调试的进度。快照 `ProviderDebugTopicState { RunId, Running, Events }` 是到此刻为止的全部事件(没开始 / 早已回收的 runId 给空快照,所以可以先订阅再发起);事件 `providers.debug.event` 一次一条 `ProviderDebugEventDto { Seq, ElapsedMs, Kind, Name, Data }`,`Kind` 是 `request` / `response` / `sse` / `text` / `thinking` / `toolCall` / `end` / `error`。这是尽力而为的实时视图,权威结果是 `providers.debug.run` 的返回值。
上游错误正文回显了凭据时,诊断文本里已抹掉(`providers.test` / `providers.models.fetch`)。这一条由测试守:结果与主题状态的类型树里没有任何可能装凭据的字段,整段线上字节里扫不到密钥。
业务失败是粗粒度错误码 + `error.details`(`ProviderErrorDto { Key, Params }`,稳定的 `providers.*` key,控制端翻译):`not_found`(未知供应商 / 别名 / 账号 / 没配 key)、`invalid_request`(地址不是 http(s)、id 含斜杠……)、`conflict`(别名已存在、登录被取消 / 顶掉)、`unsupported`(这家不走账号登录)、`unavailable`(上游连不上 / 超时可重试、模型列表解析不出、登录失败)、`forbidden`(托管供应商)。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `catalog.query` | query | viewer | 按条件查模型(`providerId` / `modelId` / `text` / 能力位 / `minContextWindow`),分页(`offset` / `limit`,缺省 500、上限 2000);结果带目录版本和匹配总数 |
| `catalog.providers` | query | viewer | 目录里的供应商(不含模型,带模型数) |
| `catalog.get` | query | viewer | 一个模型的详情(已套用覆盖),没有为 null |
| `catalog.status` | query | viewer | 版本、来源(`seed` / `cache` / `remote`)、上次更新 / 检查时间、下次检查时间、模型数、最近一次错误 |
| `catalog.refresh` | command | owner | 立即检查一次。**永不报错**:结局在结果里(`updated` / `notModified` / `rejected`(缩水、不是有效 JSON)/ `failed`(网络、超时、状态码、超大)),带错误 key(`catalog.*`) |
| `catalog.override.set` / `remove` | command | owner | 写入 / 撤销一条覆盖(改名、窗口、输出上限、能力位、带币种的价格;内容全空 = 撤销);返回新的 `CatalogTopicState` |
| `catalog.model.delete` / `restore` | command | owner | 从目录里隐藏一个模型(同步数据再来也不会回来;已有的其他覆盖保留)/ 恢复 |
| `catalog.overrides.list` | query | viewer | 全部覆盖 |
| `catalog.settings.get` / `set` | query / command | viewer / owner | 自动更新开关与间隔(分钟,夹在 15 分钟到 7 天);关掉自动更新定时器就停,「立即更新」仍可用 |
| `providers.list` | query | member | 全量 `ProvidersTopicState`(供应商、别名、坏条目诊断、账号) |
| `providers.models.list` | query | member | 模型选择器的候选(用户配过的全部模型,含停用的,窗口 / 能力 / 思考档位已按目录解析) |
| `providers.resolve` | query | member | 「模型 + 可选供应商」会落在哪条绑定上(和路由同一段规则),带价格;没落上时 `issue` 说明原因 |
| `providers.accounts.list` | query | member | 一家的账号(行 + 登录态摘要) |
| `providers.upsert` / `remove` | command | owner | 新建或更新 / 删除供应商(连带别名、账号、密钥)。凭据 ref 由 id 派生,不接受调用方给的值 |
| `providers.test` | query | owner | 连通性测试:用模型列表端点探一次(不烧 token),结局是结构化结果,不是错误 |
| `providers.credential.set` / `info` / `remove` | command / query / command | owner | 存一把 API Key(只回摘要)/ 摘要 / 清掉 |
| `providers.credential.reveal` | query | owner,**仅本机通道** | 明文单次返回,不进任何列表和广播 |
| `providers.models.fetch` | query | owner | 从上游拉「这家有哪些模型」(导入弹窗)。先试这家协议那一族的列表端点,失败或一条都解不出时再试另一族(Anthropic `/v1/models` + `x-api-key`、OpenAI 族 `/models` + Bearer);都拉不到才报错,报的是这家协议那一族的失败 |
| `providers.models.setAliases` / `update` / `rename` / `remove` | command | owner | 整表替换别名 / 逐条编辑(写入后归供应商,不再跟随目录)/ 改名 / 删除 |
| `providers.accounts.remove` / `setEnabled` / `setLabel` / `setCurrent` / `reorder` / `clearLimit` / `pause` | command | owner | 账号池操作,结果是这一家的账号全量 |
| `providers.oauth.start` / `addAccount` / `reauth` | command | owner | 走一遍 OAuth;**登录结束(成功、失败、取消)才返回**,进度走 `providers.oauth` 主题;同一时刻只有一条登录,再次发起先掐掉上一条 |
| `providers.oauth.signOut` / `cancel` / `submitCode` | command | owner | 退出当前账号 / 取消正在进行的登录 / 把授权页给的回调码交回(`needsPastedCode` 的登录) |
| `providers.discovery.scan` | query | owner | **能力 `providers.discovery`**(后加的能力,老设备不报就不调)。在设备上找其他客户端已经配好的供应商(Claude Code、Codex、cc-switch、opencode、常见环境变量),只读、不写任何东西。结果每条带来源、建议的 id / 名字 / 协议 / 地址、密钥后四位、模型提示,以及和现有供应商的关系(`new` / `imported` / `conflict`);**密钥明文从不出设备** |
| `providers.discovery.import` | command | owner | 能力同上。按候选 id 导入:设备**重新扫一遍**、在设备上直接建供应商并存密钥(控制端从头到尾碰不到明文),有模型提示的同时建别名;id 冲突时加后缀;逐条给结果(成功的供应商 id / 失败的 key)。扫描后来源变了(候选没了)这一条报 `providers.discovery.gone` |
| `providers.templates.list` | query | member | **能力 `providers.templates`**(后加的能力,老设备不报:添加供应商只有「自定义」)。「添加供应商」的内置模版:一个端点一条(`deepseek`、`deepseek-anthropic`、`moonshot-intl`……),带所属服务(控制端据此找 logo)、分组(`intl` / `cn` / `aggregator` / `local`)、端点标签(`intl` / `anthropic`)、协议、地址、拿 key 的地址、要不要 key、是否「常用」。静态数据,和自动发现共用 Agent 的已知服务表(`Ncw.Agent.Catalog/Providers/Templates`);名字是品牌名不本地化,分组和端点标签是开放的字符串,控制端认不出就放「其他」/ 原样显示。冻结向量 `GoldenV1ProviderTemplates` |
| `providers.debug.models` | query | owner | **能力 `providers.debug`**(后加的能力,老设备不报:「测试」退回 `providers.test`)。调试弹窗的模型候选:对一个**调试对象**(`ProviderDebugTargetDto`:已保存的供应商 id,和 / 或草稿的地址、协议、key)把两族列表端点都试一遍,逐条给尝试结果(族、URL、状态码、条数、失败 key + 已脱敏的说明)和合并后的模型(每个模型带「哪一族报了它」)。都拉不到不是错误,结果里模型为空,控制端让用户手填 |
| `providers.debug.run` | command | owner | 能力同上。用**选定的协议 + 模型**对调试对象发一条真实的流式请求(和对话同一套编码、鉴权、传输装饰、解码代码,只是不走重试 / 换家 / 账号轮换 / 用量记录),**跑完才返回**完整记录 `ProviderDebugResultDto`:脱敏后的请求(方法、URL、头、正文)、状态码与响应头、错误正文、全部 SSE 事件(带相对毫秒)、解码出的回复 / 思考 / 工具调用、停止原因、用量、首字与总耗时、失败 key。进度走 `providers.debug/<runId>` 主题(`runId` 由控制端生成,先订阅再调用);取消就是取消这次调用(取消帧),设备随即掐断上游连接。调试对象带草稿 key 时 key 只用于这一次请求,不落盘;请求头里的 key 一律显示成 `••••后四位`,响应 / 事件 / 说明里出现的 key 抹成 `[redacted]` |

**子代理 / 计划 / 目标**(能力 `subagents` / `planning`,承载 本机 + 远程;实现:`Ncw.Agent.Host/Conversation/OrchestrationHandlers`,领域层 `Ncw.Agent.Subagents` / `Ncw.Agent.Planning`)。方法声明齐全(种类、角色、能力、承载),command 带幂等键:

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `subagents.list` | query | viewer | 工作区磁盘上有哪些子代理定义(内建 + 全局 `<数据根>/agents` + 项目 `.next-cowork/agents`,**含被关掉的**,`Enabled` 标出来)。只读,不碰运行中的注册表(打开管理面板不会让正在跑的 Run 的工具表中途换掉) |
| `subagents.diagnostics` | query | viewer | 加载诊断:哪个文件为什么被跳过(稳定 `agents.*` key + 英文说明 + 结构化参数,控制端翻译) |
| `subagents.setEnabled` | command | **owner** | 启用 / 关闭一个子代理(设备级偏好,kv `agents.disabled`,和 2.x 同一个键)。关掉的既不进 `Task` 的清单也不能被派遣;名字必须合法(小写字母数字和连字符),否则 `invalid_request` |
| `subagents.report` | command | member | 用户在卡片上点「处理」:把后台子代理的结果汇报给主代理(重启后、或自动那一趟被挡住后)。结果是汇报进度(`none` / `pending` / `injecting` / `reported` / `blocked`),不是错误;没有发送档位时进 `blocked` |
| `goal.get` | query | viewer | 某个会话当前的目标(先从转录恢复),带版本号 `Rev` |
| `goal.set` | command | member | 设定目标:校验(去掉不可见字符后空 → `empty`;超 4000 字 → `tooLong`,不截断;**都是结果不是错误**)→ 生效 → kickoff 经内部输入交给会话(`Delivery`:`interjected` / `launched` / `deferred` / `failed`)。可带 `Options`(发送档位,记在会话上供 kickoff 和之后的检查续用;`full` 只有 owner) |
| `goal.clear` | command | member | 清除目标(幂等) |
| `runs.executePlan` | command | member | 批准计划后以 `code` 模式发起执行 Run:一次 `runs.start` + 批准的计划引用(取自 `plan_approval` 交互的 `PlanId` / `Path`)。设备在 Run 开始前登记「这个 RunId 执行这份计划」,`before_run` 载入放进状态块(载入失败不静默:写成「could not be loaded … do not guess」让模型如实告诉用户)。会话忙时是 `busy`(可重试),**不进队列**:计划只属于这个 RunId |

状态的归约规则只有一份(AGENTS.md §3):子代理在 Core 的 `SubagentView`(终态压过运行中、重放的旧 `subagent_start` 不复活终态卡片、第一个终态胜出),待办在 Core 的 `Todos` / `TodoView`(待办没有表,**是会话消息的派生视图**:最近一次成功的 `TodoWrite`),目标在 Core 的 `ActiveGoal` / `GoalWire`。
`runs.start` 的 `permissionMode = full` 等限制同样适用于 `goal.set` 带来的档位。计划模式是会话模式 `plan`(`runs.start` 的 `Options.Mode`):该模式的 Run 才下发 `EnterPlanMode` / `ExitPlanMode` 并摘掉 `Task`,写入围栏在工具层与权限层是同一个值。
消息里的子代理回执(`tool_result.subagent`)和汇报行(内部消息里的 `subagent` 块)是**消息内容块 JSON**,形状只有会话层 `MessageCodec` 一份定义,协议不另有 DTO;控制端从 `MessageDto.PartsJson` 读。

**工作区工具**(能力 `files` / `git` / `review` / `terminals`,承载 本机 + 远程;方法表在 `Ncw.Protocol/Methods/{Files,Git,Review,Terminal}Methods.cs`,Host 实现在 `Ncw.Agent.Host/Workspace` 与 `Ncw.Agent.Host/Terminals`,领域层 `Ncw.Agent.Workspace` / `Ncw.Agent.Terminal`)。
读取 viewer 起;写入 member 起(command 带幂等键)。所有文件和进程访问都经项目的 `IExecutionEnvironment`(本机和 SSH 项目同一套行为);路径一律是**项目相对路径**(`/` 分隔,空串 = 项目根;git 的路径是**仓库相对**,仓库根可能在项目根上层),
由路径守卫(`PathGuard`)拒绝越界;响应里永远不出现设备上的绝对路径。错误是粗粒度错误码 + `error.details`(`FilesErrorDto` / `GitErrorDto` / `TerminalErrorDto` 的稳定 key,控制端翻译)。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `files.list` | query | viewer | 列一层目录(不递归):目录在前,各自再按 `name` / `mtime` / `size` 排,分页(`offset` / `limit`,缺省 2000、上限 5000);条目带 `hidden` / `ignored`(命中固定忽略表,不读 `.gitignore`)/ `link`,`hideIgnored` 可在设备上直接滤掉 |
| `files.read` | query | viewer | 文本(≤ 2 MiB,保留 BOM 和换行)/ 图片(≤ 3 MiB 内联字节,更大 `chunked`)/ 二进制(`unsupported` / `tooLarge` / `encoding`);`revision` = 内容 SHA-256,保存必须带回 |
| `files.readRange` | query | viewer | 大文件分块读(单块 ≤ 1 MiB),带 `revision`:分块读的中途文件变了得到 `conflict` |
| `files.write` | command | member | 覆写已存在的文件(文本或图片字节),乐观锁(`revision` 对不上 `conflict`),同目录临时文件 + 原子替换 |
| `files.mutate` | command | member | `createFile` / `createDirectory` / `rename` / `move` / `copy` / `delete`;目标已存在 `conflict`;`overwrite` 只对 rename 且目标是文件;**delete 不是永久删除**:移进 `<项目根>/.next-cowork/trash/`,结果带 `recoveryPath` |
| `files.suggest` | query | viewer | 输入框 `@` 的文件名建议(带 TTL 的索引 + 模糊匹配分档排序,设备上算,只回十几条) |
| `files.search` | query | viewer | 内容搜索(字面或正则,正则用不回溯的引擎,不支持的语法 `invalid_request`),跳过忽略表、二进制和 > 1 MiB 的文件,量与时间有上限,结果带 `truncated` |
| `files.resource.get` / `save` / `delete` | query / command / command | viewer / member / member | 命令、子代理、模式的 markdown 单文件定义(frontmatter 保序保真 + 正文,`revision` 乐观锁);全局作用域(数据根)的写 / 删只有 owner,项目作用域(`<项目根>/.next-cowork/`)member 可;内建模式只读 |
| `git.overview` | query | viewer | 状态一次拿全:分支、上游、ahead / behind、文件列表(porcelain v2 的 XY 原样带上来 + Core 派生的 staged / unstaged / untracked / conflicted)。「开不出来」(目录没了 / 没装 git / 不是仓库)是 `available = false` + 原因,不是错误 |
| `git.branches` / `git.commits` / `git.diff` / `git.diffSides` | query | viewer | 分支、提交(1..500)、unified diff(未跟踪文件自己拼、200 KiB 截断、二进制标记)、对比视图的两侧全文(没法按全文对比 `comparable = false`) |
| `git.stage` / `unstage` / `commit` / `checkout` / `createBranch` | command | member | 参数校验挡 flag 注入(路径放 `--` 之后,分支名拒绝前导 `-`);git 以 argv 执行,不经 shell;凭据提示被关掉(没有终端可答) |
| `git.pull` / `git.push` | command | member | `pull --ff-only`(快进不了如实失败);`push` 没有上游时补 `-u origin <branch>` |
| `git.generateMessage` | query | member | 读暂存区 diff,用设置里的辅助模型写一条提交信息草稿(填进输入框,不直接提交);会花钱,所以 member 起 |
| `review.changeSet` / `list` / `fileDiff` / `precheckUndo` | query | viewer | 每个顶层 Run 的变更集摘要(子代理的改动并进父 Run)、按会话列出、单文件 before / after 全文、撤销预检(磁盘现状已和记录不符的文件) |
| `review.undo` / `review.redo` | command | member | 撤销 / 恢复整轮:磁盘现状 hash ≠ 记录的 after 时标 `conflict` 跳过,`force` 才覆盖;超限(`oversize`)和项目外(`outsideWorkspace`)的文件跳过 |
| `terminals.list` / `getBuffer` | query | viewer | 终端表(按项目)、当前环形缓冲 |
| `terminals.prepare` / `approve` / `create` | command | member | 本机项目 `prepare` 直接就绪;**SSH 环境每个终端先逐个批准**:`prepare` 返回带执行环境标识(设备 + 环境)的意图,发起它的控制端 `approve` 得到一次性凭据,凭它 `create`;远程终端只有创建它的控制端能看、写、杀、列出,控制端的连接断开时作废 |
| `terminals.write` / `resize` / `kill` / `close` | command | member | 键盘输入(≤ 64 KiB)、窗口大小、终止进程、关闭页签。租约闸(02 A2)尚未做:member 即可写,多个控制端同时写同一个终端不互斥 |

终端的限额(每项目 8、设备总共 32)、环形缓冲、退出保留 10 分钟、创建中途取消不留孤儿 PTY 都在 `TerminalManager`(见 [06 §9](06-environments-ssh.md#9-终端m3ncwagentterminal));Host 只做协议映射、控制端归属(本机连接 `local`,远程连接对端公钥指纹)和连接结束时的 `ReleaseController`。
错误映射:`NotFound` → `not_found`;`Conflict` / `ApprovalRequired` / `ApprovalExpired` → `conflict`(`details` 区分);`ResourceLimit` → `busy`;`TooLarge` → `too_large`;`InvalidArgument` → `invalid_request`;`Cancelled` → `unavailable`;环境层 `Disconnected` → `unavailable`,`Unsupported` → `unsupported`。

**扩展域(M4)**:Skills、命令、Hooks、MCP 四个一方扩展(设计 [08.06 §3](08-agent-runtime/06-extensions.md))的协议面,方法表在 `Ncw.Protocol/Methods/{Skills,Commands,Hooks,Mcp}Methods.cs`,修订号主题在 `ExtensionsTopic.cs`,Host 实现在 `Ncw.Agent.Host/Extensions/`。能力名分别是 `skills` / `commands` / `hooks` / `mcp`(握手的 `welcome.features` 由已注册方法自动得出),承载 本机 + 远程。
业务失败是粗粒度错误码 + `error.details`(复用 `ProviderErrorDto`:稳定 key + 结构化参数,控制端翻译;**参数里永不含密钥**)。写设备文件系统、起进程、连网的方法一律 owner:Hook 的命令和 MCP 的 stdio 命令会在设备上真的执行。
**凭据不上线**:MCP 的环境变量值 / 请求头值存 NCK1(`mcp:<id>:env|headers`),Hook 的环境变量值存在设置文件里;DTO 里只有**键名**(`EnvNames` / `HeaderNames`),`mcp.getSecretsInfo` 只回「有哪些键、各自是否已设置」,值只出现在 `mcp.setSecrets` / `hooks.save` / `hooks.test` 的**请求**里(控制端 → Agent 单向),响应、主题、事件、错误、诊断、日志、钩子输出和工具结果里都被抹掉。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `skills.list` | query | viewer | 某项目看到的 Skill 清单(插件提供的 → 全局 `<数据根>/skills` → 项目 `.next-cowork/skills`,后者覆盖前者,最多 200),带「全局启用」「在该项目里激活」(空清单 = 全都要)、用量。每次都重扫(用户刚往目录里拖了一个就来看,不能是旧缓存) |
| `skills.diagnostics` | query | viewer | 加载诊断:哪个目录 / 文件为什么被跳过(稳定 `skills.*` key + 英文 + 参数;软链逃逸的诊断不回显目标) |
| `skills.setGlobalEnabled` | command | **owner** | 全局启停(设备级偏好,kv `skills.disabled`,存被关掉的那些),返回完整清单 |
| `skills.setWorkspaceActive` | command | member | 某项目选装哪几条(工作区设置 `activeSkillIds` + `skillSelectionMode`),返回完整清单 |
| `skills.installZip` | command | **owner** | 安装 zip(请求带字节,**线上上限 3 MB**,帧上限是 4 MB;更大的走市场)。解压全经项目 / 本机的 `IExecutionEnvironment`:路径穿越、绝对路径、符号链接条目、重复条目、保留名一律拒绝,解压体积边读边计数,先解到同级临时目录再原子改名。失败 key:`skills.packageTooLarge` / `zipInvalid` / `skillMdInvalid` ……;**没有进度主题**,结果带终态 |
| `skills.installMarket` | command | **owner** | 市场安装(设备代为联网下载,≤ 20 MB,sha256 校验)。需要账户令牌:取自 `account.attachDelegatedToken` 交付的委托令牌(每次请求现取);未交付 / 已过期 / 已撤销返回 `skills.authRequired`(浏览照常),交付之后不用重启就恢复 |
| `skills.uninstall` | command | **owner** | 卸载:只删 skills 目录下的、不跟随软链,插件提供的不可卸载 |
| `skills.marketList` / `marketCategories` / `marketDetail` | query | member | 市场目录(设备抓取;HTTP 抓取可注入)。member 起:设备代为联网 |
| `commands.list` | query | viewer | 斜杠命令(已启用的:内置 `/init` + 一方扩展 `RegisterCommand` 贡献的 + 全局 `<数据根>/commands/<name>.md` + 项目 `.next-cowork/commands/<name>.md`,名字只来自文件名,同名项目覆盖全局),带提示词模板。**展开在客户端发送前完成**(`$ARGUMENTS`、`$1–$9`,纯函数在 Core 的 `CommandExpansion`),设备只收到展开后的文本,手机和桌面看到的输入一致 |
| `commands.listAll` | query | viewer | 含被关掉的(`Enabled` 标出来),管理面板用 |
| `commands.diagnostics` | query | viewer | 加载诊断(稳定 key) |
| `commands.setEnabled` | command | **owner** | 启用 / 关闭一个命令(设备级偏好,kv `commands.disabled`),返回完整清单 |
| `hooks.list` | query | member | 全局(`<profile 根>/settings.json` 的 `hooks` 段)+ 项目(`.next-cowork/settings.local.json` 的 `hooks` 段)的钩子。**viewer 不行**:命令文本属于设备配置。DTO 只有环境变量名 |
| `hooks.diagnostics` | query | member | 最近 20 条失败记录(超时 / 起不来 / 非 0 非 2 的退出码)和读不懂的配置,环境变量的值已抹掉 |
| `hooks.save` / `hooks.delete` / `hooks.setEnabled` | command | **owner** | 保存(新建时设备铸 id;环境变量值为 `null` = 沿用已存的值,值不回传;`NCW_` 前缀保留)/ 删除 / 启停。读不懂的配置文件拒绝覆盖,别的键(权限规则等)原样保留。软警告 `hooks.warn.weakMatcher` 不拦保存 |
| `hooks.test` | command | **owner** | 用样例载荷**真跑一次**命令型钩子(prompt 型只预览展开结果,不真调模型),返回 退出码 / stdout(≤ 8 KB)/ stderr(≤ 4 KB)/ 耗时 / 决定,环境变量的值已抹掉。会在设备上执行命令,所以 owner |
| `mcp.list` | query | member | 全部服务器的配置 + 状态(同 `mcp` 主题的全量类型),不含任何密钥 |
| `mcp.upsert` / `mcp.setEnabled` / `mcp.reconnect` | command | **owner** | 新建或更新 / 启停 / 重连。返回**第一次连接尝试结束时**的状态(最长 `ConnectTimeout`),之后后台按退避继续重连 |
| `mcp.remove` | command | **owner** | 删除服务器:断开、注销它的工具、清掉它的 NCK1 凭据 |
| `mcp.testConnection` | command | **owner** | 连一次、列工具、返回 工具名 / 耗时 / 稳定错误 key(可带**未保存的草稿配置**);不留常驻连接、不动注册表与状态。脱敏后的 stderr 尾部只在这里的 `Detail` 里(owner 排错用) |
| `mcp.setSecrets` | command | **owner** | 整张替换某台服务器的环境变量 / 请求头值(未声明的键丢弃),存 NCK1;请求带明文,响应只有键名与「是否已设置」 |
| `mcp.getSecretsInfo` | query | **owner** | 只回键名和是否已设置,**绝不回值**(没有 reveal 方法) |
| `mcp.resources.list` / `mcp.prompts.list` | query | member | 服务器的资源 / 提示列表(只读) |

**插件(M4)**:Jaunt 宿主(设计 [02 A7.1](02-agent.md#a71-jaunt-评估与实现m4)、[08.06 §7](08-agent-runtime/06-extensions.md#7-实现注插件宿主ncwagentplugins))的协议面,方法表在 `Ncw.Protocol/Methods/PluginsMethods.cs`,Host 实现在 `Ncw.Agent.Host/Plugins/`,能力名 `plugins`,承载 本机 + 远程。功能盘点 10 的 `plugins` 模块:安装 / 启停 / 权限 / 市场 / 配置 / 更新 → Agent;`runCommand` / `cardAction` / `interactionReply` 按协议转发;React 视图相关的 `activateEditor` / `openWebApp` / `setEditorDirty` / `confirmClose` 不做(Q2),`pickPackage`(文件选择框)是客户端本地的。
**所有变更方法返回变更之后的完整状态**(`PluginsTopicState`,控制端镜像整体替换,不拼增量);安装类返回 `PluginsInstallResult`(装好的那个 + 完整状态)。业务失败是粗粒度错误码 + `error.details`(`ProviderErrorDto`,稳定 `plugins.*` key,参数里永不含凭据);**市场安装需要账户令牌,设备没有 → `unauthorized` + `plugins.authRequired`**(取自 `account.attachDelegatedToken` 交付的委托令牌:未交付 / 已过期 / 已撤销时是 `authRequired`,交付之后立即恢复;浏览照常)。
**角色的取舍**:写设备上的东西(安装、启停、卸载、授权、配置、更新)= owner——在设备上装并运行第三方代码、决定它能碰哪些文件 / 命令 / 域名;读(列表、活动、配置读取、市场浏览、更新检查)= member——清单里有命令白名单 / 域名 / 诊断、活动日志里有插件做过什么,viewer 不给;**用户的回应**(`runCommand` / `cardAction` / `interactionReply`)= member——命令是插件自己在清单里声明的入口、效果受它被授予的能力约束,卡片按钮和插件的提问(确认、执行命令的审批)与 `interactions.respond` 一样由发起 Run 的 member 回答;能力没批的调用在宿主里就被拒,不靠角色。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `plugins.list` | query | member | 全量状态(同 `plugins` 主题的类型) |
| `plugins.setEnabled` | command | **owner** | 启用 / 禁用。启用先核对必选能力是否都批过(没批 → 状态 `pending-approval`,不激活;装载失败的 `plugins.notRunnable`);禁用先注销贡献再销毁运行时 |
| `plugins.uninstall` | command | **owner** | 卸载:先下线再删包、状态、配置、私有存储、密钥、活动日志;插件提供的 Skill 随之消失 |
| `plugins.installPackage` | command | **owner** | 安装 zip(请求带字节,**线上上限 3 MB**,帧上限 4 MB;更大的走市场)。路径穿越 / 符号链接 / 重复条目 / 多个顶层目录 / zip bomb / 清单与包内文件不符一律拒绝(`plugins.zipInvalid` / `packageTooLarge` / `manifestInvalid` / `packageFileMissing`)。新装的插件**禁用且无授权** |
| `plugins.grantPermissions` / `revokePermissions` | command | **owner** | 授予(越过清单上界的静默丢弃)/ 撤销(撤掉必选能力 = 插件停下来;对正在跑的插件立刻生效) |
| `plugins.activity` | query | member | 最近的宿主调用(最近的在前,≤ 500 条):方法、人话摘要、裁决 `ok` / `denied` / `error` / `timeout`、耗时,**不含参数原文** |
| `plugins.runCommand` | command | member | 执行清单声明且已注册的命令;`ArgsJson` 是附加参数,`WorkspaceId` 是调用点的项目(命令里的文件 / 命令类能力以它为根,调用方改写不了)。失败:插件 throw = `plugins.commandFailed`,超预算 = `plugins.faulted` |
| `plugins.cardAction` | command | member | 实时工具卡片上的按钮;即发即忘,送不到是 `Delivered = false` 不是错误 |
| `plugins.interactionReply` | command | member | 回答插件的一个提问;取消 = 全部缺省;请求已经不存在是 `plugins.interactionNotFound` |
| `plugins.marketList` / `marketCategories` / `marketDetail` | query | member | 市场目录(设备抓取,HTTP 客户端可注入);列表里就带这一版声明的必选能力和自带的 Skill 名 |
| `plugins.installMarket` | command | **owner** | 授权(账户令牌,拿权威摘要)→ 下载(≤ 20 MB)→ 校验 → 安装;同一个 slug 的两次安装合流成一次 |
| `plugins.checkUpdates` | query | member | 已装插件的新版(市场整张表翻完,缓存 10 分钟,`Force` 跳过);新增的必选能力逐条列出,`FromMarket = false` 的(本地装的)不计入批量更新 |
| `plugins.updateAll` | command | **owner** | 更新所有市场来源的插件,串行、失败不中断,结果带成功 / 失败(key)清单 |
| `plugins.getConfiguration` / `setConfiguration` | query / command | member / **owner** | 配置(清单默认值 + 用户的值,以清单为准);`Value = null` 恢复默认;类型 / `enum` 不符 `plugins.configurationInvalid` |

**账户委托(M4)**:账户会话(刷新令牌)只属于控制端、存在它的系统钥匙串里(07 §2);本机 Agent 拿到的是一份**委托令牌**(短期访问令牌),只用于模型通道、配置同步和技能 / 插件市场。方法表在 `Ncw.Protocol/Methods/AccountMethods.cs`,Host 实现在 `Ncw.Agent.Host/Accounts/`,能力名 `account`。**三个方法全部 owner + 仅本机通道**(远程控制端——哪怕是 owner——不能替本机换账户;开放网关也调不到):

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `account.attachDelegatedToken` | command | owner,仅本机 | 交付(替换)委托令牌:`Token`、`ExpiresAt`(Unix 毫秒)、`AccountId`、`TeamId?`。形状不对(空 / 含空白或控制字符 / 超 8 KB / 账户 id 为空)= `invalid_request` + `account.tokenInvalid`;已经过期 = `account.tokenExpired`。返回 `AccountStatusDto`(**不含令牌**) |
| `account.detachDelegatedToken` | command | owner,仅本机 | 撤销(退出登录、切换账户)。幂等 |
| `account.getStatus` | query | owner,仅本机 | 有没有委托令牌、属于哪个账户 / 团队、何时过期(控制端重连、Agent 重启之后对账用) |

令牌**只出现在 attach 的请求参数里**(方向是控制端 → Agent,和 `providers.credential.set` 的密钥一样);**只放 Agent 内存**(`DelegatedAccount`,不落盘、不进 NCK1——Agent 重启之后由控制端重新交付,过期等同于没有);任何结果、状态、事件、诊断日志里都没有它,只有 `{ Attached, AccountId, TeamId, ExpiresAt }`。这一条由测试守:结果类型的类型树里没有可装令牌的字段(`AccountDtoTests`),并且在真实 Agent 上扫描控制端收到的每个字节、诊断日志和数据根下的所有文件(`AccountDelegationTests`)。Agent 里令牌的使用者:技能 / 插件市场(每次请求现取,**接线并有端到端测试**);模型通道(托管供应商 `nextcowork` 经 `/v1/models` 同步模型)和账户配置同步(B10)会从同一个 `DelegatedAccount` 取,**本批只提供令牌访问点,这两个消费方随后端(B10 / B11)到位再接**(见 07 §2「实现状态」)。

`tools.list` 现在列**所有注册工具**:统一注册表(`ToolRegistry`:内置 + MCP + 插件,含被禁用的)加上一方扩展注册的(`Task` / `TodoWrite` / `EnterPlanMode` / `ExitPlanMode` / `ProposeGoal` / `Skill`……,它们随会话扩展进每回合的快照),同一个内部 id 只列一次;`Name` 是稳定的内部 id(`mcp__<服务器>__<工具>`),不是分配给模型的外部名。

`attach` 没有对应的方法:它是订阅 `run/<id>` 主题(见 §8.2)。载荷里的消息内容块、工具输入和详情是 JSON 文本(块的形状只有一份定义:会话层的消息编解码,见 [08.04](08-agent-runtime/04-session-context.md)),协议不再镜像一份类型层次。
线上枚举一律是小写 / 驼峰字符串,控制端不认识的值按「未知」处理,不报错。

**云端**(M6,能力 `cloud`,承载 本机 + 远程;类型在 `Ncw.Protocol/Methods/CloudMethods.cs`,冻结向量 `GoldenV1Cloud`;语义见 [02 A6.1](02-agent.md#a61-云端-agent-的-agent-侧实现m6)):

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `cloud.getStatus` | query | viewer | `CloudStatus { State(awake / preparing), StartedAt, ResumedFromSleep, WokeAt?, Quota, Usage, ExceededKey?, ExceededWindow?, InterruptedRuns[], AccountId? }`。配额与用量的真源在 Agent(自己数 Run、读用量表),编排服务下发的只是上限;`Usage` 带窗口起点(UTC)供界面显示重置时间 |
| `cloud.clearInterrupted` | command | member | `{ RunIds[] }`(空 = 全部):确认「被休眠打断的 Run」已处理 |
| `projects.add` | command | member | 追加 `CloneUrl?` / `CloneBranch?`(带默认值的末尾字段,旧载荷照常解码):给了 `CloneUrl` 时 `Path` 是要创建的目标目录,经项目环境的 git 克隆后登记。只收 `https://` 无凭据地址;云端目标必须在工作区根里。错误:`invalid_request`(`projects.clone.url` / `.target` / `.environment`)、`forbidden`(`cloud.egress.denied`)、`conflict`(`git.failed`,details 带抹掉令牌的 stderr)、`busy` / `unavailable` 同 git |
| `git.credentials.set` / `remove` / `list` | command / command / query | owner | Git 主机 HTTPS 凭据(GitHub / GitLab 令牌)。`Set { Host, Token, Username? }`(用户名缺省 `x-access-token`);结果只有 `{ Host, Username }[]`,**令牌永远不出现在任何会回到控制端的类型里**(有测试守着)。存 NCK1,经环境变量按 URL 作用域注入 git |

**配额错误**:Run 发起前超限返回 `rate_limited`(`retryable = false`)+ `error.details`(`ProviderErrorDto`)的 key **`quota.exceeded`**,参数 `window` / `limit` / `used` / `resetsAt`(Unix 毫秒);作用于 `runs.start`、`runs.executePlan`、`queue.enqueue`、`runs.steer`、开放网关和定时任务(定时任务的运行记录 `Error = quota.exceeded`)。收尾休眠 / 升级中新 Run 返回 `unavailable`(`cloud.preparing`,可重试)。云端的 `remote.*` / `pair.*` 返回 `unsupported`(`cloud.managed` / `cloud.pairing.unsupported`)。

### 8.5 杂项 Agent 服务(M4)

能力 `settings` / `usage` / `schedules` / `storage` / `search` / `attachments` / `composer` / `widgets` / `gateway`,承载 本机 + 远程(除非表里写「仅本机」)。方法实现在 `Ncw.Agent.Host/Services/`,领域逻辑在 `Ncw.Agent.Services`(不依赖协议 DTO,由 Host 的 `ServiceMapping` 映射);业务失败是粗粒度错误码 + `error.details`(`ProviderErrorDto`:稳定 `domain.*` key + 结构化参数)。
**线上格式**:每个 DTO 的键 = 属性名(PascalCase);构造函数里**没有默认值**的参数必须出现(可空的写 `null`),缺键是 `invalid_request`(消息里带缺的字段名);带默认值的可以省略。

**设置**。三种作用域:设备 / 项目走 Agent,应用级(主题、语言、窗口)只在客户端本地,不经协议。设置存在 2.x 的 `settings` 行(JSON,未知键保留,补丁深合并,整份补丁先校验再写)。**每次现读**:改完设置,下一个 Run 就用新值(指令目录、各辅助模型、空闲超时、账号轮换、故障转移、目标检查间隔、代理),没有缓存。
`settings` 主题是全量类型 `DeviceSettingsState(Rev, Settings)`,首帧和 `settings.changed` 同一个类型,`Rev` 单调(规则 5)。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `settings.get` | query | member | 当前设备设置(与主题同类型) |
| `settings.update` | command | owner | 补丁(键缺失 = 不动),返回新状态。校验失败 `invalid_request`(`settings.invalid.*`)。**补丁含 `Gateway` 块只允许本机通道**(远程 owner 不能替设备开 HTTP 端口) |
| `settings.project.get` / `settings.project.update` | query / command | member / owner | 项目设置(执行环境、权限规则、指令、激活的 Skill 等) |
| `settings.proxy.setPassword` | command | owner,**仅本机** | 代理密码存 NCK1,`DeviceSettingsDto` 里只有 `HasPassword` |

**用量**(只读,viewer):`usage.getSummary`、`getSessionAttempts`、`getProviderStats`、`getModelStats`、`getDailySeries`、`getActivityStats`。费用 `null` = 未知,**永不当 0**,币种之间不相加;日汇总 `usage_daily` 按整天重算、按写入顺序的水位线增量刷新,查询前先刷一次(没有定时刷新)。**不提供逐条的请求日志**(只做用量统计):`usage_records` 只作为汇总、统计和会话费用核算的原始数据,不对控制端分页列出。

**定时任务**。规则沿用 2.x:`once` / `daily` / `weekly` + 重复窗口(不是 cron),IANA 时区,时钟可注入。运行经 `RunRegistry`,`RunOrigin.Scheduled`,权限 `full`,所以**所有写方法都是 owner**。目标(goal)不进定时任务。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `schedules.listTasks` / `getTask` / `listRuns` / `getRun` | query | viewer | 游标分页 |
| `schedules.create` / `update` / `delete` / `setEnabled` / `runNow` / `deleteRun` | command | owner | `delete` 幂等(已不存在不是错误);`runNow` 与定时触发走同一条路径 |

`schedules` 主题:`SchedulesTopicState(Rev, Tasks, Runs)`,首帧与 `schedules.changed` 同型。**断线重启的补跑策略**:启动对账时跳过全部错过的触发(记一条 `skipped` / `scheduled.missed`,不补发一串);上一次还在跑时到点记 `skipped` / `scheduled.overlap`;一次性任务触发后关闭。

**存储**。危险操作 = owner + 本机通道 + 二次确认令牌(预览签发,一次性,绑定操作,10 分钟过期,常数时间比较)。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `storage.getStats` / `backup.status` | query | member | 体积 / 计数 / 上次备份 |
| `storage.vacuum` / `backup.create` / `cleanup.attachments` | command | owner | 整理(`VACUUM`)/ 备份(zip v2:`manifest.json`、`database.sqlite`、`settings.json`、`credential.key`,用 `VACUUM INTO` 快照)/ 回收未被引用的附件(不丢用户内容,远程 owner 可做) |
| `storage.cleanup.preview` | query | owner | 按 年龄 / 全部历史 / 本地数据 预览,返回数字与确认令牌 |
| `storage.backup.restorePreview` / `restore` / `cleanup.execute` | command | owner,**仅本机** | 恢复(在线,SQLite 备份 API)/ 执行清理;令牌不对、过期、用过都拒绝 |
| `storage.export.create` / `read` / `discard` | command | owner | 导出 `nextcowork-data-export` v1(工作区 + 会话 + 消息;**不含凭据、供应商、MCP**),`read` 分块取回 |
| `storage.import.write` / `preview` / `apply` / `discard` | command | owner,**仅本机** | 分块上传 → 预览(新增 / 覆盖 / 跳过)→ 确认应用 |

**搜索 / 附件 / 输入框 / 小组件**:

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `search.conversations` | query | viewer | 全文检索(FTS),可限定项目 |
| `attachments.upload.begin` / `chunk` / `commit` / `abort` | command | member | 分块上传(单个 ≤ 32 MiB,每块 ≤ 帧上限),草稿先登记,和消息**同一个事务**里提交;按 scope + owner 去重;磁盘文件名是 ULID,原始名只存在 `display_name`;7 天未提交的草稿回收 |
| `attachments.remove` / `listBySession` | command / query | member | 被消息引用的附件不能单独删 |
| `attachments.read` | query | viewer | 按 `ncw://attachments/...` 分块读(路径逃逸在纯函数里校验) |
| `composer.suggestNext` / `complete` | query | member | 提示词建议 / 行内补全(带密钥样子的草稿不发给模型,回答腔 / 评价语 / markdown / 过长的结果一律沉默);`composer.history`(viewer)、`composer.cancel`(member) |
| `widgets.getState` | query | viewer | 小组件状态(≤ 64 KiB / 卡,≤ 200 卡 / 会话,不可信输入) |
| `widgets.setState` / `submit` / `runAction` | command | member | `submit`:按 `callId` 找挂起的 `widget_submit` 交互,先到先得(和 `interactions.respond` 同一个闸门);`runAction`:从转录里找动作声明、绑定参数、经权限链代参执行,返回 `deny`(稳定 key `widgets.action.*`)/ `confirm`(`Output` 是要确认的工具与入参 JSON,确认后带 `Confirmed = true` 重发)/ `result`(`Reason` null 成功,`widgets.action.failed` 失败);详见 [08.03 可视化工具](08-agent-runtime/03-tools-permissions.md) |

**搜索提供方**(能力 `websearch`,承载 本机 + 远程;`web_search` 工具的数据源,实现见 [08.03 §13](08-agent-runtime/03-tools-permissions.md)):

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `websearch.list` | query | member | 全量状态 `WebSearchTopicState{Version, Providers[]}`(8 家目录项:`Id`、品牌名、领 Key 的地址、`UnavailableKey`、`Enabled`、`Priority`、`HasKey`、`Last4`),按优先级排好序;状态里没有任何凭据内容 |
| `websearch.setEnabled` / `reorder` | command | owner | 启停(用不了的家 → `invalid_request` + `websearch.error.unavailable`)/ 整批重排(缺的掉到末尾、认不出的忽略、重编号 0..n-1);返回新的全量状态 |
| `websearch.setCredential` / `clearCredential` | command | owner | 写 Key(NCK1,ref `websearch:<id>`,**只进不出**:没有 reveal,回程只有 `HasKey` / `Last4`,太短的 Key 不给 `Last4`);清 Key 顺手关掉这家 |
| `websearch.test` | query | owner | 真发一次最小查询;只回 `Ok` / `LatencyMs` / 稳定 `Key`(`websearch.test.noKey / unavailable / noAdapter / failed / noResults`)/ `Detail`(服务商的英文原话),**不回搜索结果** |
| 主题 `websearch` | — | member | 全量 `WebSearchTopicState`,首帧与 `websearch.changed` 同一个类型,`Version` 单调;冻结向量 `GoldenV1WebSearch` |

会话复制 / 分支时附件由 `AttachmentService.Relocate` 重新落到新会话名下;删除会话先记下附件路径、事务后再回收没有别处引用的文件。

**网关管理**(能力 `gateway`;网关本体见 §11):

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `gateway.getStatus` / `getAddresses` | query | owner | 是否开着、实际端口、监听范围、故障转移、各供应商健康、可访问的地址(`0.0.0.0` 不是地址) |
| `gateway.setEnabled` | command | owner,**仅本机** | 写设置 `gateway.enabled` 并立即对账(开 / 关 / 换端口 / 换监听范围串行,最后一份设置获胜) |
| `gateway.getKey` / `rotateKey` | query / command | owner,**仅本机** | 取 / 换某个角色(`viewer` / `member`)的 API 密钥;密钥存 NCK1(`gateway:key:<role>`),只在这两个方法的返回值里出现 |
| `gateway.resetHealth` | command | owner | 重置某个(或全部)供应商的健康(冷却、失败计数、限流闸门) |
| `gateway.openApi` | query | viewer | OpenAPI 3.1 文档 |

### 8.6 执行环境(M4)

能力 `environments`,承载 本机 + 远程。方法表在 `Ncw.Protocol/Methods/EnvironmentsMethods.cs`,实现在 `Ncw.Agent.Host/Environments/`,设计见 [06 §10](06-environments-ssh.md)。真源在设备的 `EnvironmentRegistry` 与各环境实例(控制端只镜像)。
业务失败是粗粒度错误码 + `error.details`(`ProviderErrorDto`,稳定 `environments.*` key,控制端翻译;**消息是固定短句,主机名、路径、服务器文本都不外泄**):`not_found`(没有这个环境 / 路径)、`invalid_request`(配置不合法、路径不是目录)、`conflict`(配置修订号过期、在用、改目标要确认)、
`unavailable`(连不上 / 没连接 / 失败;可重试的才带 `retryable`)、`unsupported`(平台)。**凭据只写不读**:密码、私钥口令、代理密码、私钥文件路径只出现在 `create / update / test` 的请求里(控制端 → 设备单向),响应 / 主题 / 事件 / 错误 / 日志里只有 `HasSavedPassword / HasIdentityFile / HasPassword / HasConfigFile`;参数记录的 `ToString()` 被覆盖成 `redacted`。这一条由测试守:整段线上字节里扫不到密码 / 口令 / 私钥路径。

| 方法 | 种类 | 角色 | 说明 |
|---|---|---|---|
| `environments.list` | query | member | 全量 `EnvironmentsTopicState`(与主题同型) |
| `environments.status` | query | member | 一个环境(`EnvironmentDto`) |
| `environments.create` | command | **owner** | 新建 SSH 环境(设备铸 id);`Secrets` 里的凭据存 NCK1,响应里没有 |
| `environments.update` | command | **owner** | 改配置,`ExpectedRevision` 对不上 `conflict`;路径 / 凭据的 null = 保持、空串 = 清除;改了目标而已有项目绑在上面要 `ConfirmTargetChange`(`environments.targetChangeNeedsConfirm`);在用 `conflict`(`environments.busy`) |
| `environments.remove` | command | **owner** | 删除(连同保存的凭据);有项目绑定 `conflict`(`environments.inUse`);`local` 不能删 |
| `environments.setDefault` | command | **owner** | 默认环境(新建项目对话框的预选;删掉默认环境后回到 `local`) |
| `environments.connect` | command | member | 连接 / 失败后重连。**等这次尝试结束才返回**,失败是结果(`failed` + 错误 key)不是错误;等待期间主机指纹 / 凭据提问以交互出现,请求取消 = 放弃这次连接(环境回到 `idle`) |
| `environments.disconnect` | command | member | 断开并回收连接(配置保留);有 Run / 终端 / 后台 Shell 在用 `conflict`(`environments.busy`) |
| `environments.test` | command | **owner** | 试连接(可以是未保存的草稿,可带 `ExistingId` 沿用已存凭据):真连一次、测往返、断开;结果是结构化的(`Ok` / 错误 key / 延迟 / 事实)。首次指纹照常以交互确认并钉扎(和正式连接同一个指纹库) |
| `environments.browse` | query | member | 远程目录浏览(只列目录、符号链接到目录的也算;面包屑、根〔POSIX `/`、Windows 盘符〕、最多 2000 条);环境没连接先连接(同 `connect` 的交互);`local` 不能浏览(`environments.notRemote`) |
| `environments.importSshConfig` | query | **owner** | 读设备上的 `~/.ssh/config`(或指定文件),列出可导入的别名(已套用配置解析;`ProxyCommand` 标不可导入) |
| `environments.knownHosts` / `forgetHostKey` | query / command | **owner** | 已知主机 / 忘记一台主机的钉扎(指纹变化被拒之后的处理入口) |

项目绑定:`projects.add { Path, Name?, EnvironmentId? }`(member)——`EnvironmentId` 给了 = 这个 SSH 环境里的目录(`Path` 是远程绝对路径,经环境 `realpath` + `stat` 核实是已存在的目录;同一环境同一目录只登记一次),会先连接环境;
`ProjectDto.EnvironmentId`(本机项目为 null)带环境标识,`Environment` 是 `local` / `ssh`。这两个字段是带默认值的末尾字段(省略 = null),旧载荷照常解码,黄金向量守着。
`runs.start`、`files.*`、`git.*`、`terminals.*` 等在 SSH 项目上的行为见 [06 §10.5](06-environments-ssh.md):惰性连接、member 起才发起连接、Failed 不自动重连。

**会话层的取消**:控制端取消一个调用(`cancel` 帧)会真的送到设备上正在等的处理器(`DeviceClient` 此前在取消时可能不发取消帧,本批修复并有测试守)。`MethodRouter.BeforeInvoke` 是路由统一的前置闸门(角色 / 承载 / 幂等键之后、参数解码之前),宿主用它做「项目的执行环境先就绪」这类横切前置条件。

## 9. 中继(M6,**已实现**:`Ncw.Relay` + `RelayTransport`)

3.0 先上线局域网直连;中继是第三种字节流承载,**只新增承载,不改 L1 以上任何东西**(帧、会话、方法表、黄金向量一个字节没动):

- **中继是哑管道**:控制端和设备各自用普通 `wss://`(中继服务器的正规 CA 证书)连到中继,中继把两条连接拼成一条字节流。
  端到端的 TLS 1.3 双向认证在这条字节流里进行,和局域网直连**完全相同**。中继只看到密文和连接元数据。
- **设备注册**:设备连上中继,用设备私钥对挑战签名(`deviceId` 就是公钥哈希),不需要账户;设备把当前有效的「路由令牌」哈希列表推给中继。
- **路由令牌**:配对成功时,设备额外给控制端发一个路由令牌,写进控制端的设备地址里(`relay: route`)。配对时就生成和保存,
  中继上线后老的配对记录直接可用,不用重新配对。
- **控制端连接**:`wss://<中继>/connect?d=<deviceId>` 并出示路由令牌;中继通知设备,设备为这个控制端反向开一条数据连接,中继拼接。
- **地址选择**:控制端并行尝试局域网地址和中继,先通的胜出,局域网优先;直连断开时续连到中继(每次重连都重新竞速)。
- **限制与部署**:每台设备并发控制端 16 个、带宽和空闲超时上限、连接频率限制;无状态,按 `deviceId` 粘性路由,可以水平扩展。

### 9.1 承载线路(L0,`Ncw.Protocol/Transport/RelayWire.cs`)

三条 WebSocket 连接,控制消息是 JSON 文本(`RelayMessage`,平铺类型 + `type` 区分,省略 null,源生成序列化,零反射),拼接之后只有二进制消息(= 端到端 TLS 的字节,每条 ≤ 16 KiB,中继上限 64 KiB)。这一层**不是**帧表的一部分,但格式同样冻结(`GoldenRelay`:每种消息的 JSON、以及 M6 追加字段的 MessagePack 字节);改它必须升 `RelayProtocol.Version` 或加能力开关。

| 连接 | 路径 | 过程 |
|---|---|---|
| 设备注册连接 | `/device?d=<deviceId>` | 中继 → `challenge { v, nonce(32 字节随机) }`;设备 → `register { v, publicKey(SPKI), signature, tokens[] }`,签名 = ECDSA P-256 / SHA-256(IEEE P1363)对 `"ncw-relay-register/1" ‖ 0 ‖ nonce ‖ deviceId`;中继验 `deviceId == dev_ + base32(SHA-256(publicKey)[0..16])` 和签名(.NET 自带 `ECDsa`,不自研),通过回 `registered`,否则 `error { code: unauthorized }` 并关闭。之后设备可发 `tokens { set | add / remove }` 实时增删路由令牌哈希;被撤掉的哈希对应的进行中会话立即被掐断。同一台设备重新注册会顶掉旧的注册连接(`replaced`),进行中的会话不受影响 |
| 控制端接入连接 | `/connect?d=<deviceId>` | 首条文本消息 `connect { v, token }`(令牌**不放 URL**,不进访问日志)。中继:设备在线?(不在线 `device_offline`)→ 令牌哈希在设备的集合里?(逐个常数时间比较,不命中 `unauthorized`)→ 设备并发会话未满 16?(`busy`)→ 向设备的注册连接推 `session { session(一次性 128 位 id), tokenHash }` → 等设备的数据连接(10 秒,`timeout`)→ 给控制端回 `ready`,之后纯二进制拼接 |
| 设备数据连接 | `/device/data?d=<deviceId>&s=<session>` | 会话 id 是中继生成的一次性随机数,只经设备的注册连接告诉设备;猜错 403 并计一次失败,重复使用 409。接上就和控制端那条拼起来 |

错误码(`RelayErrors`,控制端 / 设备读到的 `error.code`;升级前被拒绝的映射为 HTTP 状态):`bad_request`(400)、`unauthorized`、`device_offline`、`busy`、`rate_limited`(429)、`misdirected`(421)、`timeout`、`unavailable`、`replaced`、`too_large`、`idle`、`revoked`。

### 9.2 安全性质(都有测试)

- **中继无法冒充设备或控制端**:端到端 TLS 双向认证,控制端按钉扎的设备指纹校验证书(`SecureChannel.ConnectAsync(pin)`),设备按信任记录里的控制端指纹授权。恶意中继换成自己的证书,控制端握手直接失败(`Ncw.Relay.Tests`:恶意中继测试,真的用 `HttpListener` 搭了一个假中继);中继冒充控制端,设备在 hello 时拒绝(未配对的指纹)。
- **偷到路由令牌没用**:令牌不绑定 TLS 私钥,所以设备在经中继的会话里额外核对「中继出示的令牌哈希 == 这个 TLS 对端指纹的路由令牌哈希」(`RemoteService.AuthenticateRelay`);用别人的令牌、或自己没配对过的密钥,都是 `unauthorized`。中继是不可信的:它谎报令牌哈希只会让握手被拒。经中继来的会话只接受已配对的控制端。
- **配对只能直连**:`pair.begin / finish / claim` 在经中继的会话上返回 `forbidden`(没有来源 IP 的会话 = 中继会话)。理由:路由令牌是配对的**产物**——首次配对时控制端还没有令牌,中继那边没有可以验证的东西;让中继转发配对就等于在公网上开一个无需令牌的、可以在线猜配对码的入口。局域网 `IP:端口 + 配对码`、扫码两种配对方式不变;配对时设备把自己的中继地址一并告诉控制端(`PairFinishResult.Relay` / `PairClaimResult.Relay`),设备后来才配置中继的,控制端在局域网连上时从 `device.info.Relay` 学到。云端设备的路由由账户 API 下发(A6),同样不经中继配对。
- **路由令牌哈希无法反推**:令牌是 256 位随机数,哈希是 SHA-256,中继手里只有哈希(设备库里也只有哈希);哈希不能当令牌用(测试:出示哈希被拒)。
- **拒绝必达,中继不吞掉它**:设备在经中继的会话里拒绝时(上面的「令牌绑定不符」就是 `unauthorized`),`reject` 必须可靠送到控制端。设备侧数据连接走 WebSocket 关闭握手(`WebSocketStream` 优雅关闭 + 最多 2 秒宽限,会话处理返回后不得直接 `Abort` / `Dispose` 底层 `WebSocket`);中继侧一边没走关闭握手就断了(崩溃、RST)时**不中止另一边**,而是把已转发的数据送完、再给它一个关闭帧并等它的关闭帧(宽限同正常关闭)。测试:`RelayCloseDeliveryTests`、`RejectDeliveryTests`(见 `docs/parity.md`「并发不稳定测试的根因与修复」)。
- **重放 / 暴力**:注册的挑战每个连接一个新的 nonce,重放旧签名被拒;失败计数(每 IP、每 deviceId)**先于比较**检查,超限的来源连正确的令牌也被拒(窗口过后解除);连接速率、并发连接、注册速率、设备控制消息速率都有限。令牌 256 位,暴力在数学上无望,限流保护的是中继和设备的资源。
- **中继只看到密文**:`RelayOptions.Tap`(只给测试)记录转发的全部字节,测试断言里面没有明文标记、证书里的名字、双方指纹。设备证书和控制端证书在 TLS 1.3 里是加密传输的,中继连「谁在连」都只能从 `deviceId`(公开标识)和来源 IP 推断。
- **日志与指标不含秘密**:日志只记设备 id、来源 IP、错误码、时长、字节数(`LoggerMessage` 源生成);不记令牌(明文或哈希)、签名、nonce、内容;ASP.NET 的请求日志关掉(它会打出含一次性会话 id 的 URL)。指标没有任何设备标识。测试扫描日志与指标。

### 9.3 中继服务(`src/Ncw.Relay`)

- **形态**:服务端部署物(可执行文件 `ncw-relay`),不是用户命令行,不对用户发布;配置全部来自环境变量(`RelayConfig`:监听地址、PEM 证书、空闲时限、每设备会话数、每会话带宽、可信代理、实例列表),不认识的值直接报错。TLS 默认由部署层(负载均衡 / 反向代理)终止,也可以配置证书让 Kestrel 自己终止。
- **无状态**:设备目录、令牌哈希、进行中的会话只在内存里;重启 = 全部丢失,设备(带抖动的退避)自动重新注册并带回令牌哈希,控制端的 `DeviceLink` 自动重连(测试:中继重启后双方自动恢复)。
- **按 deviceId 粘性路由(接口已留,多实例未部署)**:一台设备的注册连接和它所有控制端的会话必须落在同一个实例(拼接在内存里发生),所以负载均衡器要按 URL 查询串里的 `d=` 做一致性哈希(三条路径都带 `d=` 正是为此)。`IRelayPlacement` 让实例发现「这台设备不归我」:返回 421 和 `X-Ncw-Relay-Owner`(归属实例地址),客户端 / 负载均衡器据此重试。内置实现:`SingleInstancePlacement`(默认)和 `RendezvousPlacement`(rendezvous / 最高随机权重哈希,所有实例用同一份实例列表得到同一个结果;增减实例只迁移被增减实例上的设备)。跨实例转发(实例间代理)没做:靠负载均衡器的粘性路由 + 421 兜底。
- **限制(默认值,`RelayOptions`)**:每设备 16 个并发会话;消息 ≤ 64 KiB;空闲 2 分钟(L3 每 20 秒心跳,所以正常连接不会触发);每会话 16 MiB/s(两个方向合计);每 IP 并发连接 128、新连接 240/分钟、注册 60/分钟、失败 20/分钟;每设备失败 300/分钟、接入 600/分钟、控制消息 60/分钟;握手 10 秒时限;请求头 16 KiB;设备目录 20 万台。
- **背压**:每条会话每个方向「收一条、发完再收下一条」,慢的一边靠 TCP 窗口让快的一边的读停下来,中继每条会话最多缓冲一条消息(测试:对端不读时,发送方被挡住,中继转发量不再增长)。正常关闭把关闭帧转给另一边并让另一个方向把剩下的数据送完;一边没走关闭握手就断了,另一边同样先送完在途数据、再收到关闭帧(见 §9.2「拒绝必达」);超限 / 空闲 / 吊销 / 协议违规直接中止两边。**中继停机例外**(SIGTERM、滚动升级):已拼接的会话走**优雅排空**——`RelayApp.Stop()` 不再取消拼接的读(.NET 的 `WebSocket` 在读被取消时会中止整条连接,两边就只看到被重置的 TCP),而是 `Pipe` 给**两边**各发一个关闭帧(`EndpointUnavailable`)、让已转发的数据送完、再等两边自己的关闭帧(宽限同正常关闭,3 秒),原因记 `unavailable`;两边读到干净的 EOF,**立刻**发现连接没了并重连,不必靠 §9.4 的心跳(60 秒)兜底。还没拼接的(等设备接入、握手中、设备注册连接)仍然立即掐断;排空超过 8 秒的兜底硬取消。吊销、请求中止仍是硬取消(吊销要立刻切断)。纯 WebSocket 关闭帧,不改任何线上格式。测试:`RelayCloseDeliveryTests.Stopping_the_relay_closes_both_sides_…`。
- **进程生命周期(入口 `Program.cs`)**:SIGTERM / Ctrl+C 触发宿主停止(ASP.NET `ConsoleLifetime`)后主流程随之结束、以 0 退出(`RelayServer.Stopping`)——`build-relay` 的产物自检发现过「SIGTERM 之后进程不退出」和「退出时对已释放的 CTS 调 `Cancel` 导致 134」两处缺陷,均已修。`NCW_RELAY_LIFELINE=stdin` 只给构建工具的自检用(stdin 关闭即优雅退出,Windows 没有可发给后台进程的 SIGTERM 等价物),正常部署不设置。
- **观测**:`/healthz`(存活)、`/metrics`(Prometheus 文本:在线设备、会话数、转发字节、按原因分类的拒绝、空闲 / 吊销掐断;只接受本机和内网来源,除非显式配置公开)、结构化 JSON 控制台日志。
- **Kestrel + WebSocket 与 NativeAOT 的评估**:`dotnet publish src/Ncw.Relay -r osx-arm64 -p:PublishAot=true` **零警告**,产物约 12 MiB,冒烟(健康检查、指标、426 拒绝非 WebSocket 请求)通过。做法:`WebApplication.CreateSlimBuilder`、一个终端中间件按路径分派(不用 minimal API 的反射式路由绑定)、日志用 `LoggerMessage` 源生成、控制消息用 `System.Text.Json` 源生成、配置直接读环境变量(不用反射式的配置绑定)。结论:AOT 可行,但中继是服务端程序,**不要求** AOT,默认按普通部署(框架依赖或自包含)即可,需要冷启动 / 体积时再切 AOT;`tools/Ncw.Build` 的 `build-relay`(发布 + 产物自检,默认自包含,`--aot` / `--framework-dependent` 可选,IL / 裁剪警告规则同 `publish-agent`)已做,三种形态的实测见 04 §7.1。**这和 §11 对网关的判断不同**:网关用自带的最小服务器,是因为 `ncw-agent` 必须零警告 AOT 并且不想引入 ASP.NET 共享框架;中继是独立的服务端程序,用 Kestrel 才合适(HTTP/2、TLS、WebSocket 保活、限流都是现成的)。`ncw-agent` 和客户端不引用 `Ncw.Relay` 也不引用 ASP.NET 共享框架:设备侧 / 控制端侧的中继承载只用 BCL 的 `ClientWebSocket`(AOT 零警告,发布验证过)。

### 9.4 设备侧与控制端侧

- **设置**:`remote.configure` 的 `RelayUrl`(默认空 = 关闭),`RemoteStatus.RelayState` 报告注册状态。地址必须是 `wss://`;`ws://` 只允许本机 / 局域网地址(开发和测试),否则 `invalid_request`——令牌在明文连接上会被旁路看到(端到端 TLS 保护内容,但令牌泄露会让别人占用设备的会话名额)。**「仅允许局域网地址」开关语义不变**:它只管局域网监听的来源过滤,不影响经中继;关掉远程开关 = 停止监听、注销中继、断开所有会话(局域网和中继)。
- **设备**(`RemoteService`):远程开关打开且配了中继地址时启动 `RelayRegistration`(每台设备一个),经中继来的字节流交给**同一个** `SecureChannel.AcceptAsync` + `ServerSession`;配对、吊销时 `SetTokens` 把全部有效令牌哈希整体推给中继(带最新值,并发推送后到的一定是最新的)。
- **控制端**(`RemoteDeviceManager`):`SavedDevice.Relay` + `RouteToken` 组成中继路由,每次(重)连接都用 `RemoteDevices.ConnectAsync(deviceId, DeviceAddresses, …)` 竞速;中继明确说令牌无效(`unauthorized`)且局域网也没通时,`DeviceLink` 把设备标为 `Revoked`,不再重试。
- **静默断开的检测(中继承载尤其需要)**:中继停机(或重启 / 被负载均衡摘掉)是对两边的「掐断」——连接被中止,而不是走关闭握手;承载被中止时 RST 在路上丢掉、NAT 映射超时、手机切网都会让控制端**既收不到关闭帧也收不到 RST**,`WebSocketStream` 的读一直挂着(实测:macOS 回环上、24 个 CPU 空转进程的压力下,中继停机后约 15% 的运行里控制端的 TCP 连接仍是 `ESTABLISHED`、对端 socket 早已不存在,15 秒内没有任何信号)。
  检测手段有两层:① 端到端的 L3 心跳(§7,`DeviceClient` 看门狗,与承载无关,60 秒内必判);② 中继那一跳的 WebSocket 保活:控制端 / 设备的 `ClientWebSocket` 同时设了 `KeepAliveInterval` = 20 秒和 `KeepAliveTimeout` = 20 秒(.NET 的 `KeepAliveTimeout` 默认**无限**,只设间隔时对端不回 pong 也永远不会断)。中继服务端的 Kestrel 同样设了 `KeepAliveInterval` = 20 秒和 `KeepAliveTimeout` = 20 秒(`RelayOptions.KeepAliveInterval` / `KeepAliveTimeout`):控制端 / 设备静默消失(没有 FIN / RST)时,中继 20 秒内就中止这条连接,拼接按「一边没走关闭握手就断了」收尾,不必等 `Pipe` 的 2 分钟空闲时限。**取舍**:保活超时靠对端回 pong,而 pong 只有在中继的读在进行时才会被处理;拼接对「正在往慢的一边发送」的那个方向不读,所以一边被反压挡住超过 20 秒时,另一边会被当作静默断开中止——这和 2 分钟空闲时限掐慢消费者是同一个意图(只是更早),正常流量不受影响。测试:`RelayKeepAliveTests`。**中继停机时控制端发现断开的时间**:已拼接会话 = 立刻(优雅排空,见 §9.3);仍可能只靠心跳(60 秒)发现的是中继进程被直接杀掉(SIGKILL / 机器掉电)且 RST 丢失的情形。
- **配对结果带中继地址**:`PairFinishResult.Relay` / `PairClaimResult.Relay` / `DeviceInfoResult.Relay`(都是带默认值的末尾字段,旧载荷照常解码)。

## 10. 推送

推送不走设备协议。设备通过 nextco.work 的推送中继发送,内容只有 `{ deviceId, kind, opaqueId }`,
手机收到后打开 App,再通过协议拉取详情(见 [02 A4](02-agent.md#a4-移动端约束))。推送和中继一样依赖服务器,一起上线。

### 令牌登记:`push.register`(M5,**已实现**)

| 方法 | 种类 | 角色 | 能力 | 承载 | 参数 → 结果 |
|---|---|---|---|---|---|
| `push.register` | command(必带幂等键) | member | `push` | 本机 + 远程 | `PushRegisterParams { Platform, Token, AppVersion? }` → `PushRegisterResult { Platform, RegisteredAt }` |

- `Platform` 是 `apns` / `fcm`(不认识的值是 `invalid_request`,`error.details` 的 key 为 `push.error.platform`);`Token` 是平台给的不透明字符串,空或超过 4096 字符是 `invalid_request`(`push.error.token`)。
- **设备只存不发**:令牌存在 kv 表 `v3.push.registrations`(整份 JSON,`Ncw.Agent.Storage.PushRegistrations`),按**控制端公钥指纹**覆盖(同一个控制端再登记就是替换);本机通道没有指纹,用 `local`(桌面不接推送,它不是发送目标);最多 64 条,超出淘汰最久没更新的。
  发送方(后端推送中继 B4,以后)取目标时用 `PushRegistrations.ActiveFor(trust)`:只含仍被信任的控制端——吊销之后它的令牌自动作废,不需要吊销流程另外通知这里。
- viewer 不能登记(`forbidden`):viewer 看不到也答不了审批,推送对它没有意义;以后要给 viewer 推「完成」通知,再把角色放宽(改方法表,加向量)。
- 握手的 `welcome.features` 里出现 `push`,控制端据此决定要不要登记(`PushActions.Supports`);老设备没有这个能力就跳过。
- 新 DTO 的冻结向量:`Ncw.Protocol.Tests.GoldenV1Push`(`push.register.params` / `push.register.paramsNoVersion` / `push.register.result`),只新增、没改已冻结的类型,仍是协议 v1。
- 推送本身(APNs / FCM 经中继)的内容仍然只有 `{ deviceId, kind, opaqueId }`;手机打开 App 后用上面这些协议方法拉详情。

## 11. 开放网关(HTTP API)

重新设计,**不兼容 2.x 的 `/v1`**:同一张方法表的 HTTP 映射。它和远程开关是两个独立的开关,**默认关闭**,开关与密钥只有本机 owner 能动(§8.5)。实现在 `Ncw.Agent.Gateway`(自带最小 HTTP/1.1 服务器,见下面的取舍)。

- `POST /api/<method>`:body 是参数的 JSON(键名 = DTO 属性名,空 body = 空对象),`Authorization: Bearer <密钥>`;command 必须带 `Idempotency-Key` 头(没带 = 400,与协议一致,重放只执行一次)。成功 `{"ok":true,"result":…}`,失败 `{"ok":false,"error":{code,message,retryable,details?}}`。
- 状态码:`invalid_request` 400、`unauthorized` 401、`forbidden` 403、`not_found` 404、`conflict` / `busy` 409、`too_large` 413、`rate_limited` 429、`unsupported` 501、`unavailable` 503、其余 500。
- `GET /api/topics/<topic>?cursor=`:SSE,`event: snapshot | evt | resync`,订阅语义与协议相同(游标续得上只补发增量,续不上给快照);每 20 秒一条 keepalive 注释。
- `GET /api/openapi.json`(与 `gateway.openApi` 同内容):由方法表自动导出,路径、种类、角色、能力、参数与结果的 schema 都来自方法声明和 PolyType 类型形状(源生成,零反射)。
- **角色**:密钥绑定 `viewer` 或 `member`(没有 owner 密钥);请求按该角色、**远程承载**进同一个方法路由,所以本机专用的方法(危险存储操作、密钥、远程开关、信任管理……)天然调不到,网关里不存在这些路径。`pair.*`(未配对专用)不暴露;带二进制参数的方法(`attachments.upload.chunk`、`storage.import.write`)和 `terminals.*` 不暴露(JSON 里无法判断一个字符串是不是 base64 二进制);二进制字段在响应里是 base64。
- **不排队**:开放网关的 Run 不排队,会话忙时 `runs.start` 直接 409 `busy`(可重试)。
- **监听**:默认只绑回环;「允许局域网」绑全部地址但只收回环和私有网段来源;期望端口被占用时换一个系统分配的(状态里带实际端口与 `gateway.portInUse`)。**明文 HTTP**:密钥在网络上不加密,只用在可信的局域网;TLS 随中继 / 证书方案一起做(**已知限制**)。
- **防护**:密钥常数时间比较;认证失败按来源地址限速(默认每分钟 20 次,超过 429);请求头 ≤ 16 KiB、body ≤ 4 MiB(帧上限)、读超时 15 秒、并发连接上限 64、不支持分块请求体。
- **为什么不用 Kestrel**(评估只做了分析,没有跑 NativeAOT 发布验证):Kestrel 依赖 ASP.NET Core 共享框架,`ncw-agent` 的 NativeAOT 零裁剪警告要逐个核对其注解、体积也大;网关要的只是「带 Bearer 密钥的 JSON 请求 → 方法表」,自带 `TcpListener` 的最小服务器零反射、零警告。HTTP/2、TLS、分块请求体需要时再换承载,方法表映射不变。

## 12. 测试

- 帧编解码:属性往返测试;对随机和畸形帧做模糊测试(必须报错断开,不能崩溃或静默继续)。
- 黄金向量:C# 自己生成,**按协议版本冻结**。
- 配对:正确码、错误码、5 次后换码、中间人转接(必须失败)、二维码过期、重复使用。
- 承载:本机、局域网各跑一遍同一套用例;中继承载:`Ncw.Relay.Tests`(进程内 Kestrel:注册、签名失败 / 重放、令牌错误、设备离线、并发会话、限流、背压、空闲超时、吊销、重启重注册、日志 / 指标卫生、**中继只看到密文**、恶意中继)和 `Ncw.Integration.Tests.RelayLinkTests`(真实 Agent + 进程内中继 + 控制端:经中继连接、调用、订阅、断线重连、重启后重注册、吊销后立即断开、局域网优先竞速、偷到令牌无用、配对只能直连)。
- 续连(含换承载)、重同步、积压溢出、幂等重放、交互先到先得、吊销后立即断开、关闭远程开关后远程连接立即断开。
- 云端(M6):`Ncw.Agent.Cloud.Tests` 用假编排服务 + 假 KMS + 进程内中继 + 真实的 `--mode cloud` AgentHost + 控制端经中继,覆盖启动配置、身份注册、信任下发、经中继对话、空闲休眠(通知到达时已离开中继、WAL 已检查点)、唤醒恢复、休眠期间的吊销、配额、指令重放 / 伪造 / 篡改 / 过期 / 发给别人、用量上报、编排服务不可用时降级;另有真实 `ncw-agent` 进程(含 NativeAOT 二进制,`NCW_AGENT_BINARY`)的冒烟。

## 13. 编排控制通道(M6:Agent ↔ 编排服务)

云端 Agent 和 nextco.work 编排服务之间的小协议。**这不是客户端协议**:控制端(桌面、手机)永远不会看到它,也不经设备协议(L1–L4)收发;
放在 `Ncw.Protocol/Orchestration` 只因为「协议定义只有一个来源」(AGENTS.md §4)。注意和 `OrchestrationMethods`(子代理 / 目标的对话编排)重名但无关。
格式是 HTTPS + JSON(源生成,零反射),由 `GoldenOrchestration` 冻结(键名、省略 null、字段顺序、Ed25519 签名的确定性向量);改格式必须升 `OrchestrationProtocol.Version` 或加可选字段。**Agent 永远是发起方**(沙箱不需要入站端口)。

**传输**:基础地址 = 启动配置的 `orchestrationUrl`(必须 `https`,令牌在这条连接上);每个请求带 `Authorization: Bearer <账户身份令牌>`(每次现取,轮换立即生效)和 `X-Ncw-Device: <deviceId>`。HTTP 状态:2xx 成功;`429` / `408` / `5xx` / 网络错误 = 可重试(Agent 指数退避 1 秒起、60 秒封顶);其它 `4xx`(401 令牌无效、403 证明不对、404 设备未知)= 明确拒绝,不重试(慢速重连,等令牌轮换)。

| 路径 | 方法 | 请求 → 响应 | 说明 |
|---|---|---|---|
| `/v1/agent/hello` | POST | `AgentHello` → `HelloResponse { Binding?, Commands[], ServerTime }` | 每次启动 / 唤醒一次;**第一次也是身份注册**。`AgentHello { DeviceId, PublicKey(SPKI base64), Signature, IssuedAt, AgentVersion, BootId, Resumed, LastSeq }`,`Signature` = 设备私钥(ECDSA P-256 / SHA-256,IEEE P1363)对 `ncw-cloud-register/1 ‖ 0 ‖ deviceId ‖ 0 ‖ issuedAt ‖ 0 ‖ bootId` 的签名;编排服务验 `deviceId == dev_ + base32(SHA-256(公钥)[0..16])` 和签名,回签名的身份绑定和 `seq > LastSeq` 的积压指令 |
| `/v1/agent/commands?after=<seq>&wait=<秒>` | GET | → `CommandsResponse { Commands[] }` | 长轮询:没有 `seq > after` 的指令时最多等 `wait` 秒(Agent 用 25),返回空数组 |
| `/v1/agent/ack` | POST | `CommandAck { Seq, Status, Code? }` → `AckResponse` | 每条指令一个回执:`applied` / `rejected`(`Code` = 原因)/ `duplicate`(序号已应用,停止重发)/ `busy`(暂时做不了,序号不被消耗,可重发)。**签名都验不过的指令不回执**(没有可信的东西可以回)。**信任类指令(`trust.put` / `trust.revoke` / `trust.replace`)的 `applied` 在路由令牌哈希已经发给中继之后才回**(最多等 5 秒):回执之后编排服务才会把路由令牌交给控制端,控制端马上经中继来连,令牌还没推出去中继就回 `unauthorized`,仅中继可达的云端设备会因此被控制端判成已吊销、不再重试(整套并行时抓到过 `code=unauthorized, tokensAtRelay=0`)。「已发出」不等于「中继已处理」:中继的 `tokens` 控制消息没有回执,加回执要改中继控制协议的线上格式,**没做**;窗口被压到一次中继读的时间 |
| `/v1/agent/usage` | POST | `UsageBatch` → `AckResponse { Ok, Duplicate }` | 用量增量,见下;编排服务按 `BatchId` 去重(重复回 `Duplicate = true`,Agent 当成功) |
| `/v1/agent/lifecycle` | POST | `LifecycleNotice` → `AckResponse` | `awake`(起来了,含从休眠醒来)、`sleeping` / `upgrading`(收尾完成,可以停掉计算资源;带 `WakeAt` 和 `SleepSummary { InterruptedRuns, KilledShells, QueuedMessages }`) |

**签名信封**(指令和身份绑定共用):`SignedEnvelope { V, Kid, Payload(JSON 字节的 base64), Sig }`,`Sig` = Ed25519(编排密钥)对 `域前缀 ‖ 0x00 ‖ payload 字节` 的签名——签的是**字节**,两端不需要约定 JSON 的字段顺序或空白。域前缀:指令 `ncw-orch-command/1`、身份绑定 `ncw-orch-binding/1`(互不通用)。
验证公钥(`kid → 32 字节原始 Ed25519 公钥`)只来自启动配置,**不来自通道本身**;编排服务轮换密钥时新旧 `kid` 并存一段时间。

**指令**(`CommandBody`,平铺类型,`Type` 决定用哪个字段):公共字段 `V`、`DeviceId`(发给哪台设备)、`Seq`(这台设备的序号,严格递增)、`IssuedAt` / `ExpiresAt`(Unix 毫秒)。

| `Type` | 字段 | 语义(都幂等) |
|---|---|---|
| `trust.put` | `Trust { Fingerprint(64 位小写十六进制), Name, Role, RouteTokenHash(64 位小写十六进制) }` | 下发 / 覆盖一个控制端;角色变了断开它现有的连接;令牌哈希立即推给中继 |
| `trust.revoke` | `Fingerprint` | 吊销:立即断开 + 撤令牌哈希;不存在也不是错误 |
| `trust.replace` | `Trusts[]` | 整体替换(唤醒后对账用):列表外的控制端被吊销 |
| `quota.set` | `Quota { DailyRuns?, MonthlyRuns?, DailyTokens?, MonthlyTokens?, MaxConcurrentRuns?, MonthlyComputeSeconds?, StorageBytes?, Frozen }` | 整体替换,null = 不限 |
| `sleep.request` | `Sleep { Mode(whenIdle / now), Reason? }` | `whenIdle` 在有控制端或有 Run 时回 `busy`;`now` 中止 Run、断开控制端 |
| `upgrade.request` | `Upgrade { Version, Mode }` | 同样的收尾,通知状态 `upgrading`;`whenIdle` 只被运行中的 Run 挡住 |
| `account.token` | `Account { Token, ExpiresAt, AccountId, TeamId? }` | 令牌轮换:换通道的 Bearer 和模型通道用的委托令牌 |

**验证与防重放**(`CommandVerifier` + `ReplayGuard` + `CommandPump`):验签 → 解析 → 类型已知 → `DeviceId` 等于本设备 → `ExpiresAt` 未过期且 `IssuedAt` 不在未来(都容忍 5 分钟偏差)→ `Seq` 严格大于已应用的最大序号。已应用的最大序号**先落盘再应用**(`v3.cloud.cmdSeq`),休眠唤醒后仍有效;一批指令按序号升序串行处理;`busy` 把序号退回。
拒绝原因(`CommandAck.Code`):`bad_format` / `unknown_key` / `bad_signature`(这三种没有可信序号,不回执)/ `wrong_device` / `expired` / `not_yet_valid` / `replayed`(回 `duplicate`)/ `unknown_type` / `invalid`(内容不合法,例如指纹不是 64 位十六进制)。

**身份绑定**(`DeviceBinding { DeviceId, Fingerprint, AccountId, RelayUrl, IssuedAt, TeamId? }`,签名信封):编排服务对「这台设备属于这个账户」的声明;控制端经账户 API 拿到的是同一份。Agent 只接受指纹等于自己的绑定(不符 = 退出码 65)。

**用量**(`UsageBatch { V, BatchId, DeviceId, Seq, From, To, Runs, InputTokens, OutputTokens, CacheTokens, ComputeSeconds, StorageBytes? }`):增量;`BatchId = <deviceId>:<Seq>`;批次内容一旦生成就不变(失败时落盘的发件箱原样重发,期间新增的用量是下一批);只有收到成功才推进序号。

**测试**:`Ncw.Protocol.Tests.OrchestrationWireTests`(冻结 JSON、Ed25519 确定性向量、密钥列表解析)、`Ncw.Agent.Cloud.Tests`(验签 / 防重放 / 指令泵、HTTP 通道对真实 HTTP、假编排服务端到端、真实进程冒烟)。
