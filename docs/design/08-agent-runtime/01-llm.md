# 08.01 · 模型层(`Ncw.Agent.Llm`)

对应 pi 的 `pi-ai` 和 2.x 的 `kernel/upstream/*`、`kernel/oauth/*`。职责:把「统一的请求」变成各家 API 的请求,
把各家的流式响应变成「统一的事件」,并负责路由、重试、认证和计价。**上层永远看不到线协议**。

## 1. 统一消息模型

以 2.x 的 Anthropic 块模型为底(R2),它信息最全:从块模型降到扁平字符串会丢信息,反过来不会。

```csharp
public sealed record Message(MessageId Id, Role Role /* User | Assistant */, IReadOnlyList<Part> Parts,
                             DateTimeOffset CreatedAt, bool Internal = false,
                             Provenance? Source = null);        // 只有助手消息有:来自哪个 Api / Provider / Model(取自 pi)

public abstract record Part;
public sealed record TextPart(string Text) : Part;
public sealed record ThinkingPart(string Text, ProtocolOpaque? Opaque, int? DurationMs, int? Tokens) : Part;
public sealed record ToolCallPart(string CallId, string Name, JsonElement Input) : Part;
public sealed record ToolResultPart(string CallId, ToolOutput Output, bool IsError, SubagentReceipt? Subagent) : Part;
public sealed record ImagePart(string Mime, string DataRef /* data: 或 ncw:// */) : Part;
public sealed record FileRefPart(string Path, string Name) : Part;
// 只给界面的部分:编码器一律丢弃
public sealed record ErrorPart(AgentError Error) : Part;
public sealed record GoalStatusPart(...) : Part;
public sealed record CompactBoundaryPart(...) : Part;

/// 各家私有数据(签名、加密推理、redacted thinking)。内核不解释,原样存、原样回放。带协议标签
public sealed record ProtocolOpaque(string Protocol, JsonElement Data);
```

- 工具名只用 `externalName` 上线,必须匹配 `^[a-zA-Z0-9_-]{1,64}$`,同一会话内稳定。

## 2. 流式事件

```csharp
public abstract record LlmEvent;
record MessageStart(string Model, string? ProviderId)                 : LlmEvent;
record TextDelta(int Index, string Text)                               : LlmEvent;
record ThinkingDelta(int Index, string Text)                           : LlmEvent;
record ToolCallStart(int Index, string CallId, string Name)            : LlmEvent;
record ToolCallDelta(int Index, string CallId, string ArgsDelta)       : LlmEvent;
record ToolCallEnd(int Index, string CallId)                           : LlmEvent;
record BlockOpaque(int Index, ProtocolOpaque Opaque)                   : LlmEvent;
record ProviderRetry(int Attempt, int DelayMs, string Reason)          : LlmEvent;
record ProviderSwitch(string From, string To, string Reason)           : LlmEvent;
record MessageEnd(StopReason Stop, TokenUsage Usage, int? LatencyMs, Cost? Cost) : LlmEvent;
record StreamError(AgentError Error)                                   : LlmEvent;
```

- `Index` 是内容块下标,必填;块可以交错,按下标归位。
- 工具调用一定有 start … end,中间可以没有 delta;参数在 end 时一次性解析,失败变成工具错误而不是异常。
- **流返回后永不抛异常**:错误变成 `StreamError` 事件(pi 规则),最终消息保留已收到的内容和用量。
- `StopReason`:`EndTurn | ToolUse | MaxTokens | StopSequence | Refusal | Aborted`。
- `TokenUsage` 里 `ReasoningTokens` 是输出的子集,不能再加一遍。
- `Cost` 三态:字段不存在 = 没接计价;`null` = 查不到价格(整轮锁定为「未知」);有值 = 累加。

C# 实现:`IAsyncEnumerable<LlmEvent>`,内部用 `Channel<LlmEvent>`。

## 3. 线协议

2.x 只有三种线协议,所有供应商(Gemini、Grok、Kimi、DeepSeek、GLM、Ollama……)都通过其中之一接入:

| 协议 | 路径 | 关键点(2.x ★) |
|---|---|---|
| `anthropic` | `/v1/messages` | 空文本块丢弃;没有签名的思考块整块丢弃;同角色相邻消息合并;**提示词缓存必加**(顶层 `cache_control`,断点在 system 或最后一个工具;5 分钟不写 ttl);`metadata.user_id` = 工作区 id;`output_tokens` 是累计值,赋值不累加;事件类型读 `data.type` 不读 `event:` 行 |
| `openai-chat` | `/chat/completions` | 工具结果单独成 `tool` 消息,其中的图片挪到后面的用户消息;o 系列和 gpt-5+ 用 `max_completion_tokens`;`prompt_tokens` 包含缓存,`inputTokens = prompt - cached`;没有 `finish_reason` 的 EOF 永远不算成功 |
| `openai-responses` | `/responses` | `store:false`,`include` 加密推理;推理回放分「只回放加密条目」(官方)和「必须回放文本」(DeepSeek)两种方言,用**白名单**过滤字段;超过 64 字符的推理 id 跳过,不截断;条目身份按 id → call_id → output_index 依次识别 |

- 编解码器是纯函数,不知道自己在给哪个供应商服务;供应商相关的处理在「传输装饰」和「思考参数适配」里。
- 新增协议值要谨慎:透传数据按协议打标签,加一个新协议值会让已有记录的推理回放全部失效(2.x ★)。

## 4. 跨模型切换(取自 pi)

每次编码请求前,对历史做一次只读转换(不改存储):

| 情况 | 处理 |
|---|---|
| 历史消息的来源与目标**同一个 api + provider + model** | 原样保留,包括签名和加密推理 |
| 来源不同 | 丢弃思考块和透传数据;文本去掉签名 |
| 工具调用 id 不符合目标协议的格式 | 按目标协议规则改写,工具结果同步改写 |
| 有调用没结果(理论上不该出现) | 补一个 `isError` 的合成结果 |
| 出错或中止的助手消息 | 回放时整条跳过 |
| 目标模型不支持图片 | 图片换成占位文本 |

## 5. 路由与重试

```
解析别名 → 候选列表(按供应商优先级;固定了供应商就只有它)→ 每次尝试:
  取凭据 → 解析图片引用(只在发出去的副本上)→ 编码 → 思考参数适配 → 用户请求补丁(JSON Patch 子集,禁止改 model/messages/tools/鉴权)
  → 重新应用 Anthropic 缓存和身份(补丁不能覆盖)→ 发送 → 解码
```

| 规则 | 值 |
|---|---|
| 每个供应商最多尝试 | 3 次;网络错误 6 次 |
| 退避 | 网络 500ms×2ⁿ;限流和 5xx 4s×2ⁿ;上限 60s;有 `Retry-After` 就照办(0 表示立即) |
| **重试和换供应商的时机** | **只在收到第一个内容字节之前**(`message_start` 不算内容) |
| 不重试 | `cache_unsupported`;`auth` 和「没有可用供应商」结束整个 Run |
| 健康度 | 连续 3 次失败冷却 30 秒 |
| 进程级限流闸门 | 按账户凭据或供应商:一个流遇到 429,其他并发流发送前都等;等待不消耗尝试次数 |
| 空闲超时 | 每个事件重置;默认 600 秒,可配 60–3600 |
| 401 | OAuth 凭据强制刷新一次再发 |

账户池:按顺序选第一个可用账户;配额耗尽(429,或 402/403 含配额字样,**401 不算**)标记限制并换下一个,
换账户不消耗尝试次数,最多换 8 次;全部受限时报 `rate_limit` 并给出最早恢复时间,**不能退回旧的单凭据槽位**。

## 6. 思考参数

- 统一入参是 `ThinkingLevel`:`off / minimal / low / medium / high / xhigh / max`,由适配器按模型族映射成各家参数。
- **显式关闭必须发出关闭参数**(`effort: none` 或 `thinking: disabled`),不能省略字段,省略等于用模型默认值。模型无法关闭思考时报错。
- 预算:minimal 1024 … max 64000,上限 `maxOutputTokens - 1024`,下限 1024;满足不了就降级而不是报错。

## 7. 认证

- 凭据形态:API Key、OAuth(授权码 + PKCE、设备码、CLI 轮询)、签名(ed25519 等)。
- OAuth 刷新:过期前 60 秒;同一凭据的并发刷新合并(刷新令牌会轮换);刷新**不用调用方的取消令牌**,用自己的 30 秒超时,
  避免取消导致新令牌丢失;新凭据先写回再返回。
- 签名凭据每次请求重新签名,私钥永远不进请求头。
- 各家 OAuth 的细节(ChatGPT、GLM、Kimi、Grok、Ollama 等)按 2.x `oauth/issuers/*` 逐个移植。

## 8. 模型目录与计价

- 模型元数据(上下文窗口、输出上限、能力)来自 `ModelCatalogService`(models.dev,见 [02 A8](../02-agent.md#a8-模型目录modelsdev-自动更新))。
- 价格:每百万 token 的绝对价格;**档位一旦跨过阈值,整个请求所有 token 都按新档计价**;用到了但没有价格的 token 类别得到 `null` 而不是 0;
  金额以整数微单位存储;按每次尝试、按 `alias.upstreamModel` 计价;计价函数界面和落库共用一个。
- 用量:每次 HTTP 尝试一条 `usage_records`,写入时冻结费用,不回算。

## 9. 测试

- 演示上游(2.x `demo.ts`)移植为假的 `HttpMessageHandler`:按字节切分响应,并像真实 API 一样严格校验请求。
  代码路径里**没有任何 `isDemo` 分支**。
- 解码器用 2.x 录制的 SSE 原始字节做回放测试,和 TS 版逐事件比较。
- SSE 解析:去 BOM、末尾孤立的 `\r` 要保留到下一块、UTF-8 解码必须是流式的(否则跨块的多字节字符会变成 U+FFFD)。
