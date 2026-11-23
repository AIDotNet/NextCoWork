# 08.01 · 模型层(`Ncw.Agent.Llm`)

对应 pi 的 `pi-ai` 和 2.x 的 `kernel/upstream/*`、`kernel/oauth/*`。职责:把「统一的请求」变成各家 API 的请求,
把各家的流式响应变成「统一的事件」,并负责路由、重试、认证和计价。**上层永远看不到线协议**。

实现位置:`src/Ncw.Agent.Llm`(只依赖 BCL 和 `Ncw.Core`;不引用 Avalonia、客户端项目、`Ncw.Protocol`)。
JSON 请求体用 `System.Text.Json.Nodes` 构造、用 `JsonElement` 读,凭据存储格式用 System.Text.Json 源生成,没有任何反射序列化;
已用 NativeAOT 发布验证零裁剪警告。**本层不产出界面文字**:本地生成的错误只带英文诊断串 + `MessageKey` + `MessageParams`,
由控制端翻译(见 §5 末尾的 key 表);上游返回的错误文本原样保留。

## 1. 统一消息模型

以 2.x 的 Anthropic 块模型为底(R2),它信息最全:从块模型降到扁平字符串会丢信息,反过来不会。

```csharp
public sealed record Message(MessageId Id, Role Role /* User | Assistant */, IReadOnlyList<Part> Parts,
                             DateTimeOffset CreatedAt, bool Internal = false,
                             Provenance? Source = null,   // 只有助手消息有:来自哪个 Api / Provider / Model(取自 pi)
                             StopReason? Stop = null);    // 只有助手消息有;Aborted 或带 ErrorPart 的消息回放时整条跳过
public sealed record Provenance(string Api /* 线协议名 */, string Provider, string Model /* 上游真实模型名 */);

public abstract record Part;
public sealed record TextPart(string Text) : Part;
public sealed record ThinkingPart(string Text, ProtocolOpaque? Opaque, int? DurationMs, int? Tokens) : Part;
public sealed record ToolCallPart(string CallId, string Name, JsonElement Input, string? InputError = null) : Part;
public sealed record ToolResultPart(string CallId, ToolOutput Output, bool IsError, SubagentReceipt? Subagent) : Part;
public sealed record ImagePart(string Mime, string DataRef /* data: 或 ncw:// */) : Part;
public sealed record FileRefPart(string Path, string Name) : Part;
// 只给界面的部分:编码器一律丢弃
public sealed record ErrorPart(AgentError Error) : Part;
public sealed record GoalStatusPart(...) : Part;
public sealed record CompactBoundaryPart(...) : Part;

/// 各家私有数据(签名、加密推理、redacted thinking)。内核不解释,原样存、原样回放。带协议标签:标签对不上的编码器一律忽略
public sealed record ProtocolOpaque(string Protocol, JsonElement Data);
/// 工具结果里给模型看的部分(Content + Images);Details 只给界面,编码器丢弃
public sealed record ToolOutput(string Content, IReadOnlyList<ToolOutputImage>? Images, bool Truncated, long? OriginalBytes, JsonElement? Details);
```

- 工具名只用 `externalName` 上线,必须匹配 `^[a-zA-Z0-9_-]{1,64}$`,同一会话内稳定。路由发送前校验,不合法直接本地报错,一个字节都不发。
- `ToolCallPart.InputError` 非空 = 参数 JSON 解析失败(原文在其中,`Input` 为 `{}`):上层把它变成工具错误,而不是异常。
- `ProtocolOpaque.Data` 的形状:`anthropic` → `{signature}` 或 `{redacted}`;`openai-chat` → `{field:"reasoning_content"}`;
  `openai-responses` → `{item:{…上游原样的 reasoning 条目…}}`。
- `AgentError` 定义在本层(`Ncw.Agent.Llm.AgentError`,字段 `Code / Message / Retryable / Status / RetryAfterMs / MessageKey / MessageParams`),
  `AgentErrorCode` 沿用 2.x(`Auth / RateLimit / ContextLength / Network / Aborted / ToolFailed / Provider / CacheUnsupported / NoHealthyProvider / Conflict / Unknown`)。

## 2. 流式事件

```csharp
public abstract record LlmEvent;
record MessageStart(string Model, string? ProviderId, string? Api, string? UpstreamModel) : LlmEvent;  // 后三项由路由补上 = 助手消息的 Provenance
record TextDelta(int Index, string Text)                               : LlmEvent;
record ThinkingDelta(int Index, string Text)                           : LlmEvent;
record ToolCallStart(int Index, string CallId, string Name)            : LlmEvent;
record ToolCallDelta(int Index, string CallId, string ArgsDelta)       : LlmEvent;
record ToolCallEnd(int Index, string CallId)                           : LlmEvent;
record BlockOpaque(int Index, ProtocolOpaque Opaque)                   : LlmEvent;
record ProviderRetry(int Attempt, int DelayMs, string Reason)          : LlmEvent;
record ProviderSwitch(string From, string To, string Reason)           : LlmEvent;
record MessageEnd(StopReason Stop, TokenUsage Usage, int? LatencyMs, CostInfo Cost) : LlmEvent;
record StreamError(AgentError Error)                                   : LlmEvent;
```

- `Index` 是内容块下标,必填;块可以交错,按下标归位。Responses 协议里每个 output item 独占一段下标空间(`output_index × 1_000_000`,
  摘要占前半、推理正文占后半),所以下标不连续;只依赖「同下标 = 同一个块」和「升序 = 内容顺序」。
- 工具调用一定有 start … end,中间可以没有 delta;参数在 end 时一次性解析,失败变成工具错误而不是异常。
  `stopReason=MaxTokens` 时已经开始的工具调用也会收尾(参数大概率被截断,会带 `InputError`),由 Loop 判失败并提示重发。
- **流返回后永不抛异常**:错误变成 `StreamError` 事件(pi 规则),最终消息保留已收到的内容和用量。**取消也是**:调用方取消令牌触发后,
  流以 `StreamError(Aborted)` 结束(不可重试),不抛 `OperationCanceledException`;`StreamAssembler.ToMessage` 把它折成 `Stop = Aborted` 的消息。
- `StopReason`:`EndTurn | ToolUse | MaxTokens | StopSequence | Refusal | Aborted`。
- `TokenUsage` 里 `ReasoningTokens` 是输出的子集,不能再加一遍;`InputTokens` 不含缓存读写(OpenAI 的 `prompt_tokens` 含缓存,解码时扣掉)。
- `Cost` 三态,用 `CostInfo { State = NotMetered | Unknown | Known, Micros, Currency }` 表达(C# 里 `Cost?` 表达不了三态):
  `NotMetered` = 没接计价,累加方保持已有值;`Unknown` = 查不到价格(整轮锁定为「未知」);`Known` = 累加。币种不同不能相加,得 `Unknown`。
- `StreamAssembler` 把事件流折成最终助手消息(2.x 的 `BlockAccumulator` / `ToolCallAccumulator`):按下标归位、同下标的 `BlockOpaque` 合进思考块、
  工具调用在 end 时解析、没有 end 的工具调用判失败。Loop 用它,不要自己再写一份。
  思考块记下**耗时**(`ThinkingPart.DurationMs`,时钟注入,Loop 传自己的 `TimeProvider`):从第一段思考增量到它之后第一个别的内容事件(文字、工具调用、消息结束;都没有就到最后一段思考增量)。
  量不出来(没有思考增量、时钟没走、只有透传载体)是 null,不写假的 0——界面据此显示「思考了 N 秒」或只写「思考」。此前一直没记,转录里每个思考块都是「思考了 0 秒」。

C# 实现:`ILlmClient.StreamAsync(ModelRef, LlmRequest, RequestContext, CancellationToken)` 返回 `IAsyncEnumerable<LlmEvent>`,内部用 `Channel<LlmEvent>`
(生产者任务跑重试循环,消费者提前退出时取消生产者并等它收尾)。

## 3. 线协议

2.x 只有三种线协议,所有供应商(Gemini、Grok、Kimi、DeepSeek、GLM、Ollama……)都通过其中之一接入:

| 协议 | 路径 | 关键点(2.x ★) |
|---|---|---|
| `anthropic` | `/v1/messages` | 空文本块丢弃;没有签名的思考块整块丢弃;同角色相邻消息合并;**提示词缓存必加**(顶层 `cache_control`,断点在 system 或最后一个工具;5 分钟不写 ttl);`metadata.user_id` = 工作区 id;`output_tokens` 是累计值,赋值不累加;事件类型读 `data.type` 不读 `event:` 行 |
| `openai-chat` | `/chat/completions` | 工具结果单独成 `tool` 消息,其中的图片挪到后面的用户消息;o 系列和 gpt-5+ 用 `max_completion_tokens`;`prompt_tokens` 包含缓存,`inputTokens = prompt - cached`;没有 `finish_reason` 的 EOF 永远不算成功 |
| `openai-responses` | `/responses` | `store:false`,`include` 加密推理;推理回放分「只回放加密条目」(官方)和「必须回放文本」(DeepSeek)两种方言,用**白名单**过滤字段;超过 64 字符的推理 id 跳过,不截断;条目身份按 id → call_id → output_index 依次识别 |

- 编解码器是纯函数,不知道自己在给哪个供应商服务;**也不碰凭据**:编码结果只有路径、固定头(`content-type`、`anthropic-version`)和请求体,
  鉴权头由路由统一加(`x-api-key` 或 `Authorization: Bearer`)。供应商相关的处理在「传输装饰」(`ITransportDecorator`)和「思考参数适配」里。
- 解码器是 `IWireDecoder`:喂一个 SSE 事件吐出零到多个事件,终局(`MessageEnd` / `StreamError`)之后不再读;EOF 时补上缺失的终局。
  畸形输入统一「跳过,不抛」,真正的致命错误走 `StreamError`。
- 新增协议值要谨慎:透传数据按协议打标签,加一个新协议值会让已有记录的推理回放全部失效(2.x ★)。
- 响应不是 SSE 而是 `application/json` 时(部分兼容网关),整体当成一个 `message` 事件交给解码器。

## 4. 跨模型切换(取自 pi)

每次编码请求前,对历史做一次只读转换(不改存储):

| 情况 | 处理 |
|---|---|
| 历史消息的来源与目标**同一个 api + provider + model** | 原样保留,包括签名和加密推理 |
| 来源不同(含来源未知的旧消息) | 丢弃思考块和透传数据;文本去掉签名(文本块本身没有签名字段,天然成立) |
| 工具调用 id 不符合目标协议的格式(Anthropic / Responses:`^[a-zA-Z0-9_-]{1,64}$`;Chat:≤ 40 字符) | 改写成 `call_` + SHA-256 前 24 个十六进制字符(id 的纯函数,每轮同一个结果,不破坏缓存),工具结果同步改写 |
| 有调用没结果(理论上不该出现) | 紧跟在助手消息之后补一条 `isError` 的合成结果(`tool_result` 必须在紧随的用户消息里);没有对应调用的结果丢弃 |
| 出错(带 `ErrorPart`)或中止(`Stop = Aborted`)的助手消息 | 回放时整条跳过,它的工具调用随之作废 |
| 目标模型不支持图片 | 图片换成占位文本 |

## 5. 路由与重试

```
解析别名 → 候选列表(按供应商优先级;固定了供应商就只有它)→ 每次尝试:
  取凭据 → 历史转换(§4)→ 解析图片引用(只在发出去的副本上)→ 编码 → 思考参数适配 → 用户请求补丁(JSON Patch 子集,禁止改 model/messages/tools/鉴权)
  → 重新强制思考偏好 → 重新应用 Anthropic 缓存和身份(补丁不能覆盖)→ 传输装饰 → 发送 → 解码
```

| 规则 | 值 |
|---|---|
| 每个供应商最多尝试 | 3 次;网络错误 6 次 |
| 退避 | 网络 500ms×2ⁿ;限流和可重试的 5xx 4s×2ⁿ;上限 60s;有 `Retry-After` 就照办(0 表示立即) |
| **重试和换供应商的时机** | **只在收到第一个内容字节之前**(`message_start` 不算内容) |
| 不重试 | `cache_unsupported`;`auth` 和「没有可用供应商」结束整个 Run(有 auth 错误时,最终报 auth 而不是后面的网络错误) |
| 健康度 | 连续 3 次失败冷却 30 秒;固定了供应商时不过冷却过滤(无处可绕);用户「重置健康状态」同时清冷却和限流闸门 |
| 进程级限流闸门 | 按账户凭据或供应商:一个流遇到 429,其他并发流发送前都等;等待不消耗尝试次数;成功后清闸 |
| 空闲超时 | 每个事件重置;默认 600 秒,可配 60–3600(`LlmRouterOptions.IdleTimeoutFromSettings` 夹取);超时是不可重试的网络错误 |
| 401 | OAuth 凭据强制刷新一次再发(只一次;API Key 不刷新) |
| 故障切换开关 | `IProviderConfigSource.FailoverEnabled`(沿用 2.x 的旁路开关):关闭时只取优先级最高的一家 |

账户池:按顺序选第一个可用账户;配额耗尽(429,或 402/403 含配额字样,**401 不算**)标记限制并换下一个,
换账户不消耗尝试次数,最多换 8 次;全部受限时报 `rate_limit` 并给出最早恢复时间,**不能退回旧的单凭据槽位**。
关闭轮换时落闸照落但不返回下一个账号(否则「换号不消耗次数」会变成死循环)。

用量:每次 HTTP 尝试(含失败)通过 `IUsageSink` 发一条 `UsageAttempt`,费用在发出时冻结;sink 抛异常不影响请求。
`RunId` 在重试和换供应商之间保持稳定。时钟(`TimeProvider`)、ID(`IIdGenerator`)、退避睡眠(`IDelayer`)、上游 HTTP(`IUpstreamHttp`)都是注入的。

本地生成的错误的 `MessageKey`(`LlmErrorKeys`),控制端按它翻译,参数在 `MessageParams`:
`llm.invalidResponse{detail}`、`llm.incompleteResponse`、`llm.upstreamTimeout{provider,seconds}`、`llm.cacheUnsupported{provider,ttl,detail}`、
`llm.cacheUnsupportedUnnamed{ttl,detail}`、`llm.missingCredential{provider}`、`llm.allAccountsUnusable{provider}`、`llm.allAccountsRateLimited{provider,recoveryAt}`
(`recoveryAt` 是毫秒时间戳,格式化是控制端的事)、`llm.pinnedProviderMissing{model}`、`llm.pinnedProviderDisabled{model,provider}`、`llm.pinnedModelMissing{model,provider}`、
`llm.unknownAlias{model}`、`llm.allProvidersCooling{model}`、`llm.noEnabledProvider{model}`、`llm.invalidWorkspaceId{max}`、`llm.invalidToolName{name}`、
`llm.thinkingUnsupported{detail,code}`、`llm.invalidRequestPatch{detail,code}`、`llm.imageInput`。

## 6. 思考参数

- 统一入参是 `ThinkingLevel`:`auto / off / minimal / low / medium / high / xhigh / max`(`auto` = 不替用户选,按模型声明的默认),由适配器按模型族映射成各家参数。
- **显式关闭必须发出关闭参数**(`effort: none` 或 `thinking: disabled`),不能省略字段,省略等于用模型默认值。模型无法关闭思考时报错(本地错误,不发请求)。
- 预算:minimal 1024 … max 64000,上限 `maxOutputTokens - 1024`,下限 1024;满足不了就降级为关闭而不是报错。
  上限用这次真的会发出去的 `max_tokens`,不和模型目录里的输出上限取小。
- 方言由「模型声明 + 模型名」决定:Anthropic(`thinking.budget_tokens` / adaptive 的 `output_config.effort`)、OpenAI Chat(`reasoning_effort`)、Responses(`reasoning.effort`)、
  DeepSeek(`thinking.type` + 扁平 `reasoning_effort`)、GLM/混元(`thinking.type`)、自定义参数路径(白名单)。`standardWire` 声明读标准线形的托管方(Ollama)不套名字方言。
- Anthropic 开启思考时:删掉 `temperature`;`max_tokens` 不大于预算时抬到 `budget + 4096`。
- **自适应思考的 Claude(4.6+,`ClaudeThinking.For` 按模型名认,允许中转前缀和点号写法)不走预算方言**(issue #202):Opus 4.7+ / Sonnet 5+ / Haiku 5.5 / Fable 收到
  `budget_tokens` 直接 400,Opus 5.5 / Fable 还拒绝 `disabled`,Sonnet 5.5 关闭要发 `between_tools`。规则:
  Anthropic 协议(预设 Auto / Anthropic)下,目录或 2.x 别名里的预算 / 开关声明对这些模型一律当档位处理;开启 = `thinking: {type: adaptive}`
  (默认只回空思考块的型号加 `display: summarized`,否则界面上的思考行永远是空的)+ 显式档位时 `output_config.effort`(不支持的档位就近往下收敛,minimal → low,4.6 的 xhigh → high,不报错);
  Auto 只开自适应、不指定档位;显式 Off 按型号发 `disabled` / `between_tools`,关不掉的型号(Opus 5.5、Fable)不发 `thinking`、档位降到 low;
  4.7+ 和 5.x 一律删掉 `temperature` / `top_p` / `top_k`。Opus 4.5 / Sonnet 4.5 / Haiku 4.5 及更早照旧用 `budget_tokens`。
- `always` / `unsupported` 模型不收任何思考开关,补丁(包括 `extra_body` 容器)也恢复不了;会话里显式的 Off 压过已保存的模型补丁。

## 7. 认证

- 凭据形态:API Key、OAuth(授权码 + PKCE、设备码、CLI 轮询)、签名(ed25519 等)。存储格式沿用 2.x:API Key 是**裸字符串**(降级兼容的守门线),
  其余是带 `kind` 的 JSON;解析失败一律退回「当作裸 API Key」,残缺的 OAuth / 签名记录判 null(不拿整段 JSON 当 Bearer)。
- OAuth 刷新:过期前 60 秒;同一凭据的并发刷新合并(刷新令牌会轮换);刷新**不用调用方的取消令牌**,用自己的 30 秒超时,
  避免取消导致新令牌丢失;新凭据先写回再返回。网络故障和 5xx 不动库(可重试);上游明确拒绝才标 `NeedsReauth`(保留 refresh token)。
  过期时间未知(`ExpiresAt = null`)不主动刷新,靠 401 之后的强制刷新兜底。
- 签名凭据每次请求重新签名(`IRequestSigner`,签名用最终 URL 的 pathname),私钥永远不进请求头;有签名器时路由不加鉴权头。
- 各家 OAuth 的细节(ChatGPT、GLM、Kimi、Grok、Ollama 等)按 2.x `oauth/issuers/*` 逐个移植。M3 本层只定义抽象:
  `OAuthProviderSpec`(端点、client_id、身份解析、可选的自定义刷新钩子)、`IOAuthRegistry`、`IOAuthLogin`(四种授权方式的登录流程接口)、`Pkce`,
  以及可测试的令牌刷新(`CredentialResolver`)。

## 8. 模型目录与计价

- 模型元数据(上下文窗口、输出上限、能力)来自 `ModelCatalogService`(models.dev,见 [02 A8](../02-agent.md#a8-模型目录modelsdev-自动更新))。
  本层不内置任何厂商价格数据(数字会过期,规则不会),只定义 `IPriceBook`(按 `(providerId, modelId, 时刻)` 查)和纯函数 `PricingCalculator`。
  `IPriceBook` 的实现是 `Ncw.Agent.Catalog.CatalogPriceBook`(价格自动采用 models.dev 的、用户覆盖优先),路由的 `IProviderConfigSource` 的实现是 `Ncw.Agent.Catalog.ProviderConfigSource`(同时是 `IModelResolver`),两者都在 Host 装配时注入。
- 价格:每百万 token 的绝对价格(`decimal`);**档位一旦跨过阈值,整个请求所有 token 都按新档计价**(含缓存 token 计入阈值);
  用到了但没有价格的 token 类别得到 `Unknown` 而不是 0(1 小时缓存写入不退回 5 分钟价);时段价按窗口自己的时区、起点含终点不含、可跨午夜、可限星期;
  生效日期区间按 UTC 日;金额以整数微单位存储;按每次尝试、按 `alias.upstreamModel`(不是回包里的模型名)、按请求发起时刻计价;计价函数界面和落库共用一个。
- 用量:每次 HTTP 尝试一条 `usage_records`,写入时冻结费用,不回算。

## 9. 测试

- 演示上游(2.x `demo.ts`)的做法移植为假的 `HttpMessageHandler`(`FakeUpstream`):按字节切分响应,并像真实 API 一样严格校验请求
  (Anthropic 的角色交替、`tool_use`/`tool_result` 配对、空文本块、签名;Chat 的 `tool` 消息相邻;Responses 的 reasoning 条目字段和 64 字符 id 等),违规回 400 并记录,
  每个路由测试结束时断言无违规。代码路径里**没有任何 `isDemo` 分支**。
- 解码器用录制好的 SSE 原始样本(`tests/Ncw.Agent.Llm.Tests/Samples/*.sse`)回放,每份样本按 1 / 2 / 3 / 7 / 64 / 整块字节切分,事件序列必须逐项一致。
  样本是按各家官方文档的线格式整理的(含中文,测多字节边界),**不是真实流量抓包**;与 TS 版的逐事件比对待 2.x 录制工具产出真实抓包后补上。
- SSE 解析:去 BOM、末尾孤立的 `\r` 要保留到下一块、UTF-8 解码必须是流式的(否则跨块的多字节字符会变成 U+FFFD)。

## 10. 不变量与对应测试

每一条都是移植验收项,测试在 `tests/Ncw.Agent.Llm.Tests`。

| # | 不变量 | 测试 |
|---|---|---|
| I1 | 空文本块不上行,整条为空的消息跳过 | `EncoderTests.Anthropic_drops_empty_text_blocks…` |
| I2 | 没有签名的思考块整块丢弃,有签名的原样回放;redacted 整块透传 | `EncoderTests.Anthropic_drops_a_thinking_block…`、`DecoderTests.Anthropic_redacted_thinking…`、`…thinking_block_without_a_signature…` |
| I3 | 同角色相邻消息合并,tool_result 在前 | `EncoderTests.Anthropic_merges_adjacent_same_role_messages…` |
| I4 | 提示词缓存必加(顶层标记 + system/最后工具断点;5m 不写 ttl,1h 显式) | `EncoderTests.Anthropic_always_enables_prompt_caching…`、`…one_hour_ttl…`、`…last_tool` |
| I5 | `metadata.user_id` = 工作区 id;用户补丁不能覆盖身份、缓存和 TTL | `EncoderTests.Anthropic_policy_reapplied_after_a_patch…`、`RouterTests.A_model_patch_cannot_override_the_anthropic_identity…` |
| I6 | `output_tokens` 是累计值,赋值不累加 | `DecoderTests.Anthropic_output_tokens_is_cumulative…` |
| I7 | 事件类型读 `data.type` 不读 `event:` 行 | `DecoderTests.Anthropic_event_type_is_read_from_data_type…` |
| I8 | Chat:工具结果单独成 `tool` 消息,图片挪到后面的用户消息 | `EncoderTests.Chat_tool_results_become_tool_messages…` |
| I9 | o 系列和 gpt-5+ 用 `max_completion_tokens` | `EncoderTests.Chat_o_series_and_gpt5…` |
| I10 | `prompt_tokens` 含缓存,`inputTokens = prompt - cached` | `DecoderTests.Chat_prompt_tokens_include_the_cache…`、`Responses_sample_decodes…`(用量断言) |
| I11 | 没有 `finish_reason` 的 EOF 永远不算成功 | `DecoderTests.Chat_eof_without_finish_reason…`、`…without_done…` |
| I12 | Responses:`store:false`,`include` 加密推理 | `EncoderTests.Responses_is_stateless…` |
| I13 | 推理回放两种方言;字段白名单(剥 `status`、`content`);超过 64 字符的 id 跳过不截断 | `EncoderTests.Responses_opaque_only_replay…`、`…over_64_characters…`、`…text_required_dialect…`、`…never_synthesises…` |
| I14 | 条目身份按 id → call_id → output_index;终局快照不重复已流出的内容 | `DecoderTests.Responses_item_identity_follows_id…`、`…final_snapshot_does_not_duplicate…`、`…reserialised_arguments…` |
| I15 | 工具调用一定有 start … end,可以没有 delta;参数在 end 时一次性解析,失败变成工具错误 | `DecoderTests.Anthropic_tool_call_with_zero_deltas…`、`AssemblerTests.Invalid_tool_arguments…`、`…Tool_call_arguments_are_parsed_once…` |
| I16 | 并行工具调用不串台(Anthropic 按下标;Chat 缺 index 时按 id/位置开新槽) | `DecoderTests.Anthropic_parallel_tool_calls…`、`Chat_parallel_calls_without_indexes…`、`Chat_tool_call_frozen_whole…` |
| I17 | 块可以交错,按下标归位;透传按下标合进思考块 | `AssemblerTests.Interleaved_blocks…`、`An_opaque_with_the_same_index…` |
| I18 | 流返回后永不抛异常:错误(含取消)变成 `StreamError`,最终消息保留已收到的内容和用量 | `RouterTests.Cancellation_ends_the_stream…`、`AssemblerTests.An_error_keeps_the_content…`、`…Cancellation_marks…` |
| I19 | `stopReason=length`:三种协议都映射成 `MaxTokens`,被截断的工具调用收尾并标记 | `DecoderTests.Anthropic_stop_reason_max_tokens…`、`Chat_finish_reason_length…`、`Responses_incomplete…`、`RouterTests.Stop_reason_length…` |
| I20 | `ReasoningTokens` 是输出的子集,不再加一遍 | `DecoderTests.Anthropic_reasoning_tokens…`、`PricingTests.Reasoning_tokens_are_part_of_output…` |
| I21 | Cost 三态:没接计价 / 查不到价(整轮锁定未知)/ 有值;币种不同不能相加 | `PricingTests.Cost_has_three_states…`、`…different_currencies…`、`RouterTests.A_model_without_a_price…`、`…price_book_that_throws…` |
| I22 | 工具名匹配 `^[a-zA-Z0-9_-]{1,64}$`,不合法不发请求 | `AssemblerTests.Tool_names_are_validated…`、`RouterTests.An_invalid_tool_name…` |
| I23 | 跨模型切换:同源原样保留(含签名) | `HistoryTransformTests.Same_api_provider_and_model…`、`RouterTests.Switching_to_a_different_model…` |
| I24 | 跨模型切换:来源不同(含未知)丢弃思考;只读转换 | `HistoryTransformTests.A_different_api…`、`…unknown_origin…`、`…read_only…` |
| I25 | 跨模型切换:工具调用 id 按目标协议改写,结果同步,改写稳定 | `HistoryTransformTests.A_tool_call_id_the_target_cannot_carry…`、`Id_rewriting_is_a_stable_function…`、`Chat_ids_have_a_tighter…` |
| I26 | 跨模型切换:有调用没结果补合成错误结果;孤儿结果丢弃 | `HistoryTransformTests.A_call_without_a_result…`、`Only_the_missing…`、`A_dangling_call…`、`A_result_that_belongs_to_an_older…` |
| I27 | 跨模型切换:出错或中止的助手消息整条跳过 | `HistoryTransformTests.Errored_and_aborted_assistant_messages…` |
| I28 | 跨模型切换:目标不支持图片时换占位文本 | `HistoryTransformTests.Images_become_placeholder_text…` |
| I29 | 图片引用只在发出去的副本上解析 | `RouterTests.Image_references_are_resolved_only_on_the_outgoing_copy`、`An_unresolvable_image…` |
| I30 | 重试和换供应商只在首个内容字节之前;`message_start` 不算内容 | `RouterTests.After_content_has_reached_the_consumer…`、`A_stream_that_dies_after_only_message_start…`、`An_in_stream_error_after_content…` |
| I31 | 每个供应商 3 次、网络错误 6 次;退避 500ms×2ⁿ / 4s×2ⁿ;上限 60s | `RouterTests.Provider_errors_are_tried_three_times…`、`Network_errors_get_six_attempts…`、`Rate_limits_without_retry_after…`、`Backoff_is_capped_at_sixty_seconds` |
| I32 | `Retry-After` 照办,0 表示立即;支持秒数和 HTTP 日期 | `RouterTests.Retry_after_is_obeyed_including_zero…`、`Retry_after_parses_seconds_and_http_dates`、`Retry_after_accepts_an_http_date` |
| I33 | `cache_unsupported` 不重试不换供应商;缓存拒绝在三种错误体形状下都能识别;请求回显不算证据 | `RouterTests.Cache_unsupported_ends_the_request…`、`DecoderTests.Anthropic_cache_rejections…`、`Request_echoes_in_error_bodies…` |
| I34 | `auth` 优先于后面的网络错误报告;缺凭据的供应商不发请求直接切下一家 | `RouterTests.An_auth_error_is_reported_in_preference…`、`A_provider_without_a_credential…` |
| I35 | 固定了供应商就只有它(无回退、无切换事件、不过冷却);三种「没了」的错误都明说不会自动切走 | `RouterTests.A_pinned_provider_is_a_hard_constraint…`、`…was_deleted_or_disabled…`、`A_pinned_provider_is_never_filtered_out_by_cooldown` |
| I36 | 健康度:连续 3 次失败冷却 30 秒,到期重新纳入,成功后恢复 | `RouterTests.Three_consecutive_failures_cool…`、`When_every_provider_is_cooling…` |
| I37 | 进程级限流闸门:一个流遇到 429,其他流发送前都等,等待不消耗尝试次数;成功和重置健康状态清闸 | `RouterTests.A_stream_that_hits_a_429_makes_other_streams_wait…`、`A_success_clears_the_gate…`、`Resetting_health_clears…` |
| I38 | 空闲超时每个事件重置,没有总时长上限;默认 600 秒,可配 60–3600 | `RouterTests.A_stalled_stream_times_out…`、`Progress_resets_the_idle_deadline…`、`The_idle_timeout_defaults_to_600_seconds…` |
| I39 | 401:OAuth 强制刷新一次再发,只一次;API Key 不刷新;刷新的是这次用的那条凭据 ref | `RouterTests.A_401_on_an_oauth_credential…`、`A_second_401…`、`A_401_on_a_plain_api_key…` |
| I40 | 账户池:顺序选第一个可用;配额耗尽(429,402/403 含配额字样,401 不算)换号且不消耗次数;最多换 8 次;全部受限给最早恢复时间且不退回旧槽;关轮换不死循环 | `AuthTests.Quota_exhaustion_is_429…`、`The_first_usable_account…`、`RouterTests.A_rate_limited_account_hands_over…`、`Three_accounts_can_all_be_reached…`、`When_every_account_is_limited…`、`With_rotation_off…`、`Account_switching_is_bounded_at_eight…` |
| I41 | 显式关闭必须发出关闭参数(Anthropic / Chat / Responses);无法关闭的模型报错;预算夹取并降级 | `ThinkingAndPatchTests.Explicit_off_on_*`、`A_model_that_cannot_turn_thinking_off…`、`The_budget_is_capped…`、`When_the_budget_cannot_be_satisfied…`、`RouterTests.Thinking_level_is_adapted…` |
| I42 | 不支持 / 总是思考的模型不收任何思考开关,补丁恢复不了;显式 Off 压过补丁 | `ThinkingAndPatchTests.Always_and_unsupported…`、`A_patch_cannot_smuggle…`、`An_explicit_off_beats…` |
| I43 | 用户请求补丁只能改白名单参数,禁止改 model / messages / tools / stream / 鉴权;原子;非法补丁发请求前失败 | `ThinkingAndPatchTests.Patches_may_not_touch…`、`A_failing_patch_leaves…`、`RouterTests.An_invalid_request_patch…` |
| I44 | OAuth 刷新:过期前 60 秒;并发刷新合并;不用调用方取消令牌;新凭据先写回再返回;网络故障不动库;明确拒绝才标重登 | `AuthTests.A_credential_within_60_seconds…`、`Concurrent_refreshes…`、`Refresh_does_not_use_the_callers_cancellation_token…`、`A_network_failure_during_refresh…`、`An_explicit_rejection…` |
| I45 | 签名凭据每次发送重新签名(含 401 重发),签名用最终 URL 的 pathname,私钥不进请求头 | `RouterTests.A_signed_request_is_signed_on_the_final_url_path…`、`Every_send_is_signed_afresh…`、`A_signature_credential_without_a_signer…` |
| I46 | 凭据存储:API Key 回吐裸字符串;解析失败当裸 Key;残缺记录判 null | `AuthTests.A_bare_string_is_an_api_key…`、`A_key_that_merely_starts_with_a_brace…`、`A_damaged_oauth_record…` |
| I47 | 计价:档位整请求重算(含缓存 token 计入阈值);缺价得 Unknown;1h 缓存写入不退回 5m;整数微单位;时段价按窗口时区 | `PricingTests.*` |
| I48 | 计价按每次尝试、按 `alias.upstreamModel`、请求发起时刻;落库与 `MessageEnd` 同一个数;用量每次 HTTP 尝试一条,sink 抛异常不影响请求 | `RouterTests.Message_end_carries_the_cost…`、`Every_http_attempt_gets_its_own_usage_record…`、`A_usage_sink_that_throws…` |
| I49 | SSE:去 BOM、末尾孤立 `\r` 保留、流式 UTF-8、任意分块边界结果一致 | `SseTests.*`、`DecoderTests` 的样本回放 |
| I50 | 工作区标识 1–512 个字符(按码点),不合法不发请求 | `RouterTests.The_workspace_id_must_be_1_to_512_characters…` |
