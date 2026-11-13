# 09 · 设备协议(3.0 全新设计)

控制端(桌面、手机)与设备(`ncw-agent`:本机、云端)之间的协议。**不参考、不兼容 2.x 的设备协议**:帧格式、加密、配对、
中继、方法表都重新设计。2.x 的独立设备服务和配对记录不迁移。

## 1. 原则

1. **局域网直连优先,开关即用**。在设备上打开「允许远程连接」,局域网里的另一台控制端输入 `IP:端口` 就能连上。
2. **中继以后由服务器提供,协议现在就为它留好位置**。中继只是另一种「字节流承载」,安全层和上层协议完全不变,加中继不改协议。
3. **不自研密码学**。远程连接的安全层用 TLS 1.3 双向认证;配对码验证用库里现成的 PAKE(SRP-6a)。
4. **一切可续传、可兜底**。订阅带游标;游标续不上就给权威快照(客户端状态规则 5);送不过来就让客户端重同步,不悄悄丢。
5. **方法即能力**。方法表声明种类、所需角色、所需能力;路由统一检查。
6. **协议版本独立于应用版本**。3.0 的协议从 `1` 开始。

## 2. 分层

```
L4 应用    方法(query / command / stream)、订阅(topic + cursor)、交互
L3 会话    hello / welcome、版本与能力协商、心跳、续连
L2 帧      4 字节长度前缀 + MessagePack
L1 安全    本机:操作系统权限 + 本机令牌;远程:TLS 1.3 双向认证(证书钉扎),ALPN = "ncw/1"
L0 承载    本机:Unix socket / 命名管道;局域网:TCP;中继(以后):经中继服务器拼接的字节流
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

3.0 实现 `Local` 和 `Lan`;`Relay` 在服务器中继上线时加一个 connector 和一个 listener 即可(见 §9)。

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
4. 双方用 K 对「TLS exporter 值 + 双方证书指纹」做 HMAC 并交换校验
   —— 把配对码的验证绑定到这条 TLS 会话和这两张证书上,中间人无法转接
5. 校验通过:设备保存信任记录 { 控制端证书指纹, 名称, 角色, 时间 };控制端保存 { deviceId, 设备证书指纹, 已知地址 }
6. 设备弹出通知「新控制端 iPhone(张三)已配对」,带「吊销」按钮;可选开启「新控制端需要在本机确认」
```

- SRP-6a 用 BouncyCastle(纯托管)的实现,不手写;M2 验证它在 NativeAOT 下可用。
- 之后再连接:TLS 双向认证时直接按指纹校验,不再需要配对码。设备的 IP 变了也没关系,身份看证书,不看地址。

### 扫码配对

二维码:`nextcowork://pair?v=1&d=<deviceId>&f=<设备证书 SHA-256>&a=<地址列表>&t=<一次性密钥 128bit>&e=<过期时间>`。
控制端连接后先按 `f` 校验设备证书,再出示 `t`(一次性、10 分钟过期、常数时间比较)。二维码本身是可信的带外通道,所以不需要 SRP。

## 4. 身份、信任与角色

- **身份密钥**:ECDSA P-256 自签名证书(.NET 在所有目标平台内置支持)。`deviceId` = 设备公钥的 SHA-256(截断后 base32)。
  设备私钥在数据根里用 `NCK1` 加密存储(云端用 KMS);控制端私钥存在系统钥匙串(Keychain / Keystore / DPAPI / libsecret)。
- **TLS 校验**:不依赖 CA,`SslStream` 的自定义校验回调按信任记录里的指纹钉扎。**需要验证**:iOS / Android 上的行为(见风险)。
- **控制端记录的设备地址**:`{ lan: [最近成功的 ip:port…], relay?: route }`。连接时并行尝试所有地址,先通的胜出,
  局域网优先;身份由证书保证,所以地址可以随便试。
- **角色**:`owner`(全部)、`member`(对话、审批;不能改设备设置和权限模式、不能开 `full` 模式)、`viewer`(只读)。
  经局域网配对的控制端默认 `member`,设备上可以改。
- **吊销**:设备删除信任记录 → 使用该证书的连接立即断开;以后上线中继时,同时作废对应的中继路由令牌。
- **云端设备**:不需要配对码。控制端登录账户后把证书公钥注册到账户 API,编排服务把信任记录下发给云端设备,
  并把云端设备证书指纹和中继路由返回给控制端(通过 nextco.work 的 HTTPS,由账户登录保证可信)。

## 5. 本机连接

- macOS / Linux:`<数据根>/run/agent.sock`,权限 0600;Windows:命名管道 `\\.\pipe\ncw-agent-<用户 SID>`,ACL 只允许当前用户。
- 不跑 TLS。桌面端拉起 sidecar 时通过环境变量传入一次性本机令牌,`hello` 里出示(纵深防御)。本机连接的角色固定为 `owner`。

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

- **心跳**:双方每 20 秒 ping;连续 3 次没回应就断开。手机进后台时直接断,回前台再连。
- **续连**:`resumeToken` 在设备上保留 5 分钟,包含订阅列表和各订阅已确认的游标。**续连可以换承载**:
  局域网断了改走中继(以后),订阅从游标继续,上层感知不到。
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
| `stream` | 返回流 id,之后用 `chunk { streamId, seq, data, end? }` 双向传输,带信用值流控。用于大文件、附件上传、终端 |

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
| `device` | 活动 Run 索引(含子代理和待处理交互数)、执行环境状态、模型目录版本、排队消息计数 | **这就是权威广播**:首帧和后续增量用同一个生成函数 |
| `session/<id>` | 已提交的消息、排队消息、标题、目标状态 | 会话元数据 + 最近一页消息 |
| `run/<id>` | Agent 事件(见 [08.02 §3](08-agent-runtime/02-loop.md#3-事件流)),自动包含后代 Run | Run 状态 + 挂起交互 + 子 Run + 日志 |
| `interactions` | 本设备所有挂起的交互 | 全部挂起交互 |
| `data` | 设备数据状态:`preparing` / `taking-over`(带步骤)/ `ready` / `blocked` / `failed`(带原因),刚完成 2.x 接管时附摘要 | 当前状态;事件 `data.changed` 用同一个类型(见 [04 §4](04-update-install.md#4-数据接管直接接管-2x-数据)) |
| `terminal/<id>` | 终端输出(二进制) | 最近的滚动缓冲 |

- 同一主题内严格按 `seq` 有序;不同主题之间不保证顺序。
- **流控**:设备按每个控制端的 `coalesceMs` 合并增量;每条连接的发送缓冲上限 8 MiB。超过时,`run/*` 先丢增量;
  结构性事件(`message_end`、`tool_end`、`run_end` 等)**永不悄悄丢弃**,送不出去就对该主题发 `resync`。

### 8.3 交互

回答用 `interactions.respond { id, expectedVersion, response }`(command)。已被处理时返回 `conflict`,
并带上由哪个控制端在什么时候处理,界面显示「已在另一台设备上处理」。

### 8.4 方法表

方法名格式 `domain.verb`,完整清单在功能盘点(Q15)之后补齐:

| 领域 | 例子 |
|---|---|
| `pair` / `trust` | `pair.begin`(SRP)、`pair.claim`(二维码)、`trust.list`、`trust.revoke`、`trust.setRole` |
| `remote` | `remote.getStatus`、`remote.setEnabled`、`remote.rotateCode`(仅本机通道) |
| `projects` / `environments` | `projects.list/create/remove`、`environments.list/create/test/browse` |
| `sessions` / `runs` / `queue` | `sessions.list/get/create/archive`、`runs.start/abort/steer`、`queue.enqueue/promote/remove` |
| `interactions` | `interactions.list/respond` |
| `files` / `git` / `terminals` | `files.read`(stream)、`files.write`、`git.status/diff/commit`、`terminals.open`(stream) |
| `catalog` / `providers` | `catalog.query`、`catalog.refresh`、`providers.list/upsert` |
| `extensions` | `skills.*`、`mcp.*`、`plugins.*`、`hooks.*` |
| `settings` / `diagnostics` | `settings.get/set`、`diagnostics.logs`(stream)、`diagnostics.bundle` |
| `data` | `data.status`(query,viewer)、`data.retry`(command,owner,仅本机通道)。数据没就绪时,依赖数据的方法返回 `unavailable`(可重试) |
| `account` / `push` | `account.attachDelegatedToken`(仅本机通道)、`push.register` |

每个方法在 `Ncw.Protocol` 里声明:种类、参数和结果类型、所需角色、所需能力、允许的承载
(例如 `remote.*`、`account.*` 只允许本机通道,远程控制端不能关掉远程开关或改配对码)。

## 9. 中继(以后由服务器提供)

3.0 先上线局域网直连;中继服务在服务器准备好时加入(云端 Agent 和局域网外的手机都需要它,排在 M6)。协议现在就为它定好以下约定,
到时候**只新增承载,不改 L1 以上任何东西**:

- **中继是哑管道**:控制端和设备各自用普通 `wss://`(中继服务器的正规 CA 证书)连到中继,中继把两条连接拼成一条字节流。
  端到端的 TLS 1.3 双向认证在这条字节流里进行,和局域网直连**完全相同**。中继只看到密文和连接元数据。
- **设备注册**:设备连上中继,用设备私钥对挑战签名(`deviceId` 就是公钥哈希),不需要账户;设备把当前有效的「路由令牌」哈希列表推给中继。
- **路由令牌**:配对成功时,设备额外给控制端发一个路由令牌,写进控制端的设备地址里(`relay: route`)。现在就随配对一起生成和保存,
  中继上线后老的配对记录直接可用,不用重新配对。
- **控制端连接**:`wss://<中继>/connect?d=<deviceId>` 并出示路由令牌;中继通知设备,设备为这个控制端反向开一条数据连接,中继拼接。
- **地址选择**:控制端并行尝试局域网地址和中继,先通的胜出,局域网优先;直连断开时续连到中继。
- **限制与部署**:每台设备并发控制端 16 个、带宽和空闲超时上限、连接频率限制;无状态,按 `deviceId` 粘性路由,可以水平扩展。

## 10. 推送

推送不走设备协议。设备通过 nextco.work 的推送中继发送,内容只有 `{ deviceId, kind, opaqueId }`,
手机收到后打开 App,再通过协议拉取详情(见 [02 A4](02-agent.md#a4-移动端约束))。推送和中继一样依赖服务器,一起上线。

## 11. 开放网关(HTTP API)

重新设计,**不兼容 2.x 的 `/v1`**:同一张方法表的 HTTP 映射,`POST /api/<method>`,订阅用 `GET /api/topics/<topic>?cursor=` 的 SSE;
API 密钥绑定角色;OpenAPI 由方法表自动导出。它和远程开关是两个独立的开关。

## 12. 测试

- 帧编解码:属性往返测试;对随机和畸形帧做模糊测试(必须报错断开,不能崩溃或静默继续)。
- 黄金向量:C# 自己生成,**按协议版本冻结**。
- 配对:正确码、错误码、5 次后换码、中间人转接(必须失败)、二维码过期、重复使用。
- 承载:本机、局域网各跑一遍同一套用例;中继上线时加一遍,并验证中继只看到密文。
- 续连(含换承载)、重同步、积压溢出、幂等重放、交互先到先得、吊销后立即断开、关闭远程开关后远程连接立即断开。
