/**
 * 上游配置与网关状态 —— 方案 §5.2 / §5.4。
 *
 * ★ 别名表是故障切换的前提:同一个 alias 可以由多个 provider 提供,
 * 才谈得上「切到下一个」。它同时是网关 GET /v1/models 的数据源。
 */
import type { OAuthIssuerId } from './oauth-issuer'
import type { SignatureScheme } from './credential'
import type { VideoAdapterId } from './video-generation'

export type UpstreamProtocol = 'anthropic' | 'openai-chat' | 'openai-responses'

export function isUpstreamProtocol(value: unknown): value is UpstreamProtocol {
  return value === 'anthropic' || value === 'openai-chat' || value === 'openai-responses'
}

/** Anthropic prompt caching is mandatory; providers only choose its lifetime. */
export type AnthropicCacheTtl = '5m' | '1h'

/** Protocol-specific options are deliberately nested so future protocols can add their own fields. */
export interface AnthropicProtocolOptions {
  cacheTtl: AnthropicCacheTtl
}

export interface ProviderProtocolOptions {
  anthropic?: AnthropicProtocolOptions
}

/**
 * Runtime boundary for provider JSON. Missing, legacy `off`, and unknown values
 * use the mandatory 5-minute default; an explicit 1-hour lifetime is preserved.
 */
export function normalizeAnthropicCacheTtl(value: unknown): AnthropicCacheTtl {
  return value === '1h' ? '1h' : '5m'
}

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined
}

/**
 * Sanitize the persisted protocol-options boundary without throwing away
 * fields belonging to protocols added in a later version.
 *
 * The IPC write path rejects an explicitly invalid TTL. This helper is for
 * older JSON/imports/direct storage reads, which keep the record readable and
 * normalize legacy or unknown Anthropic values to the mandatory 5m default.
 */
export function normalizeProviderProtocolOptions(value: unknown): ProviderProtocolOptions | undefined {
  const source = record(value)
  if (source === undefined) return undefined

  const normalized: Record<string, unknown> = { ...source }
  if (Object.hasOwn(source, 'anthropic')) {
    const rawAnthropic = source['anthropic']
    const anthropic = record(rawAnthropic)
    normalized['anthropic'] = {
      ...(anthropic ?? {}),
      cacheTtl: normalizeAnthropicCacheTtl(anthropic?.['cacheTtl'])
    }
  }
  return Object.keys(normalized).length === 0
    ? undefined
    : (normalized as ProviderProtocolOptions)
}

/** Normalize only the protocol-specific portion of a provider JSON record. */
export function normalizeUpstreamProvider(provider: UpstreamProvider): UpstreamProvider {
  const protocolOptions = normalizeProviderProtocolOptions(provider.protocolOptions)
  return protocolOptions === undefined
    ? (() => {
        const { protocolOptions: _omitted, ...rest } = provider
        return rest
      })()
    : { ...provider, protocolOptions }
}

export function anthropicCacheTtlOf(provider: Pick<UpstreamProvider, 'protocolOptions'>): AnthropicCacheTtl {
  const options = normalizeProviderProtocolOptions(provider?.protocolOptions)
  const anthropic = record(options?.anthropic)
  return normalizeAnthropicCacheTtl(anthropic?.['cacheTtl'])
}

export const PROTOCOL_LABEL: Record<UpstreamProtocol, string> = {
  anthropic: 'Anthropic Messages',
  'openai-chat': 'OpenAI Chat Completions',
  'openai-responses': 'OpenAI Responses'
}

/**
 * 参考图把协议做成了两个控件:「API 格式:OpenAI 格式 | Anthropic 格式」
 * 加一个只在 OpenAI 下出现的「使用 Responses API」开关。
 *
 * 那正好是上面三个值的一种**二维投影** —— 所以这里是两个纯函数,
 * **不加字段**。多存一个 `family` 就多了一个会和 `protocol` 不同步的值,
 * 而不同步的表现是「界面显示 Anthropic,请求按 OpenAI 发出去」。
 */
export type ProtocolFamily = 'anthropic' | 'openai'

export function splitProtocol(p: UpstreamProtocol): { family: ProtocolFamily; responses: boolean } {
  if (p === 'anthropic') return { family: 'anthropic', responses: false }
  return { family: 'openai', responses: p === 'openai-responses' }
}

/**
 * ★ `family: 'anthropic'` 时**忽略** `responses`。
 *
 * 界面上那个开关在切到 Anthropic 时会被藏起来但状态还留着(用户切回来
 * 希望它还是原样),所以这个函数一定会收到 `('anthropic', true)` 这种组合。
 * 它不是非法输入,是正常交互的中间态。
 */
export function joinProtocol(family: ProtocolFamily, responses: boolean): UpstreamProtocol {
  if (family === 'anthropic') return 'anthropic'
  return responses ? 'openai-responses' : 'openai-chat'
}

export interface UpstreamProvider {
  id: string
  name: string
  protocol: UpstreamProtocol
  baseUrl: string
  /** ★ credentials 表的逻辑引用,**永不是明文 key** */
  credentialRef: string
  /** 故障切换顺序,小的优先 */
  priority: number
  enabled: boolean
  /** Protocol-specific settings. Missing on legacy provider JSON. */
  protocolOptions?: ProviderProtocolOptions
  /**
   * 视频生成的连接配置。缺席 = 这家不提供视频生成(绝大多数聊天供应商)。
   *
   * ★★ **和 `protocol` / `baseUrl` 是两件事,所以是两个字段。**
   * 视频接口既不是 `openai-chat` 也不是 `anthropic`,而且**地址常常不同** ——
   * 火山方舟的聊天是 `/api/v3`,`contents/generations/tasks` 挂在同一 host 上;
   * 百炼的 chat compatible-mode 是 `/compatible-mode/v1`,而**原生视频任务**
   * 是 `/api/v1/services/aigc/…`。把视频地址塞进 `baseUrl` 会让聊天请求 404,
   * 反过来把视频请求发到 compatible-mode 上则是另一句读不出所以然的 404。
   */
  videoGeneration?: ProviderVideoGeneration
}

/**
 * 一个视频连接描述的是**怎么出网**,不是**能生成什么** —— 后者的真源是
 * `ModelAlias` 上的档案(`videoProfileId`),因为同一家不同型号的能力差得很远。
 */
export interface ProviderVideoGeneration {
  adapter: VideoAdapterId
  /** 视频接口的基地址。规范形式:不带尾斜杠 */
  baseUrl: string
  /** 阿里百炼这类"模型/Key/地域必须同属一地"的选择;仅诊断与请求修正用 */
  region?: string
  /**
   * AWS Nova Reel 这类以 S3 为输出的:成品落在哪。
   * ★ 刻意**只存桶与前缀**,不存凭据 —— 凭据在 `credentialRef` 指的那个槽里。
   */
  s3?: { bucket: string; prefix?: string; region?: string }
}

/**
 * 一条视频绑定引用的档案 —— 「这个型号的这条绑定,按哪套请求形状发」。
 *
 * ★ 两级(`provider.videoGeneration` + `alias.videoProfileId`)是因为聚合商:
 * 同一个 `veo-3.1-generate-preview` 名字挂在 Google 原生和某聚合站上,
 * 出网方式完全不同,而档案是按**出网方式**写的。
 */
export interface ModelVideoBinding {
  /** `VideoProfile.id`。认不出的档案 = 这条不可调用(但不影响别的模态) */
  profileId: string
  /**
   * 上游真实模型/endpoint id。缺席 = 用 `upstreamModel`。
   * fal / Replicate 这类"型号即 endpoint"的商城要靠它填 `fal-ai/…`。
   */
  endpointId?: string
}

/**
 * Canonical credential reference for a user-configured provider.
 *
 * The value is security-sensitive metadata even though it is not the secret
 * itself: accepting it from a renderer or import file would let one provider
 * point at another provider's encrypted key. Untrusted write paths derive it
 * here; updates to an existing legacy provider preserve its local ref.
 */
export function providerCredentialRef(id: string): string {
  return `provider:${id}`
}

/**
 * OAuth 登录态在列表/广播里**可以给渲染层看**的那部分。
 *
 * ★ 这里一个 token 字符都没有:常规刷新只需要账号信息。完整 token 只在用户
 * 显式点击「查看」时通过 `provider:revealCredential` 单次返回。
 */
export interface CredentialAuthInfo {
  issuer: OAuthIssuerId
  accountId: string
  email?: string
  planType?: string
  /**
   * ★★ `null` = **过期时间未知**(有的家 `expires_in` 就是 null),不是永不过期。
   * 界面上不该把它渲染成一个日期,更不该当成「已过期」。
   */
  expiresAt: number | null
  /**
   * ★ **由主进程按 `host.clock` 算好**,不让渲染层自己拿 `Date.now()` 去比。
   * 两个进程不是同一个时钟源,而「过期了没有」这件事只能有一个答案。
   *
   * ★★ `expiresAt` 未知时这里恒为 `false` —— 「不知道」必须落到「先当它有效」,
   * 落到 `true` 的话界面会对一把完全好用的凭证常年显示「已过期，请重新登录」。
   */
  expired: boolean
  needsReauth: boolean
}

/** 设置页列表/初始化路径只返回摘要信息,永不顺带回传明文。 */
export interface CredentialInfo {
  hasKey: boolean
  last4: string | null
  /** 历史兼容字段。程序主密钥可用时恒为 true;旧渲染层据它隐藏不可用提示。 */
  encryptionAvailable: boolean
  /**
   * ★ **缺失 ≠ 未登录**,而是「这条凭证不是 OAuth」(绝大多数供应商)。
   * 界面该画登录按钮还是画密钥输入框,判据是预设的 `oauthIssuer`,不是这个字段 ——
   * 否则一家 OAuth 供应商在**还没登录**时会退化成 API Key 表单。
   */
  auth?: CredentialAuthInfo
  /**
   * 这条槽里装的是**签名凭证**(腾讯 TC3 / AWS SigV4)时,它的方案。
   *
   * ★ 界面据此把"密钥输入框"换成"AccessKeyId + SecretKey"那一对,并且
   *   **不显示 last4** —— 签名凭证的尾四位是 SecretKey 的尾四位,
   *   等于把密钥的一部分印在屏幕上,而它对"这是哪一把"毫无帮助
   *   (真正的身份标识是 AccessKeyId,那个本来就不是秘密)。
   */
  signatureScheme?: SignatureScheme
  /** 签名凭证的 AccessKeyId —— **不是秘密**,设置页用它显示"用的是哪一把"。 */
  accessKeyId?: string
}

/**
 * 只有用户显式点击「查看」才经专用 IPC 返回。列表、广播和初始化路径都不得携带它。
 * OAuth 的两个 token 都允许查看(产品决策);`accountId` 等非秘密元数据继续走 CredentialInfo。
 */
export type RevealedCredential =
  | { kind: 'api-key'; apiKey: string }
  | { kind: 'oauth'; accessToken: string; refreshToken: string }
  | { kind: 'signature'; scheme: SignatureScheme; accessKeyId: string; secretKey: string; region: string | null; sessionToken?: string }

export interface ModelCapabilities {
  tools: boolean
  vision: boolean
  /** 为 false 时忽略 ThinkingLevel:界面「不支持该参数的模型将自动忽略此设置」 */
  thinking: boolean
  caching: boolean
  /** Extended capability matrix used by the model management console. */
  textInput?: boolean
  /** Explicit input-side name; legacy records use `vision`. */
  visionInput?: boolean
  fileInput?: boolean
  videoInput?: boolean
  audioInput?: boolean
  textOutput?: boolean
  imageOutput?: boolean
  videoOutput?: boolean
  audioOutput?: boolean
  webSearch?: boolean
  structuredOutput?: boolean
  streaming?: boolean
  batch?: boolean
}

export type ModelModality = 'text' | 'image' | 'video' | 'speech' | 'transcription'
export type ThinkingMode = 'unsupported' | 'always' | 'toggle' | 'effort' | 'budget'
export type ReasoningEffort = 'none' | 'minimal' | 'low' | 'medium' | 'high' | 'xhigh' | 'max'

/**
 * Responses 协议回传历史思考的两种方言,见 `ThinkingConfig.reasoningReplay`。
 *
 * - `opaque-only`:只回传上游自己签发过的载体(`id` / `summary` / `encrypted_content`),
 *   推理正文(`content`)一律剥掉 —— 这是官方 OpenAI 输入侧的约束。
 * - `text-required`:除了载体,还要用转录里的思考正文补 `content` —— 这是 DeepSeek
 *   思考模式的硬校验。
 */
export type ReasoningReplay = 'opaque-only' | 'text-required'

export interface ThinkingConfig {
  mode: ThinkingMode
  defaultEnabled: boolean
  defaultEffort?: ReasoningEffort
  defaultBudgetTokens?: number
  parameterPath?: string
  /** Optional provider-specific JSON values for a toggle field. */
  enabledValue?: unknown
  disabledValue?: unknown
  /** Optional wire-value mapping when provider labels differ from our levels. */
  effortMap?: Partial<Record<ReasoningEffort, unknown>>
  /**
   * ★★ 声明这家读**标准协议线形**,跳过按模型名的厂商方言适配。
   *
   * 存在的理由:Ollama 这类托管方也跑 `deepseek-*` / `glm-*` 名字的模型,而
   * 适配器认到这些名字会套 DeepSeek/智谱**官方 API** 的方言(比如 DeepSeek 要
   * 额外的 `thinking:{type}`)。Ollama 的 OpenAI 兼容层只读 `reasoning_effort`,
   * 方言字段被静默丢弃 —— 不声明这个,那些模型的 Think 开关(尤其「关」)失真。
   * 官方供应商的条目不声明,行为一个字节不变。
   */
  standardWire?: boolean
  /** Anthropic adaptive thinking sends thinking.type=adaptive with output_config.effort. */
  anthropicAdaptive?: boolean
  /**
   * ★★ Responses 协议回传历史 reasoning item 时的方言开关。缺席 = `opaque-only`。
   *
   * 存在的理由:官方 OpenAI 的**输入侧**对 reasoning item 的 `content`(推理正文)
   * 上限是 0 —— 带非空 `content` 的 item 会被整轮 400(`array_above_max_length`);
   * 而 DeepSeek 思考模式反过来**要求**历史每轮的 reasoning_text 全文回传,缺了同样 400。
   * 两边互斥,只能按上游分流。
   *
   * 判定顺序见 `thinking-adapter.ts` 的 `reasoningReplayFor`:显式声明 >
   * `standardWire`(声明读标准线形的托管方按标准约束走,照 `standardWire` 自己的先例)>
   * 模型名兜底 > 默认 `opaque-only`。
   *
   * 代码里**不按供应商分支**:供应商属于路由器那一层的事实,编码器只看这个开关。
   */
  reasoningReplay?: ReasoningReplay
}

export interface RequestPatchRule {
  op: 'add' | 'replace' | 'remove'
  path: string
  value?: unknown
}

export interface RequestAdapterConfig {
  preset: 'auto' | 'anthropic' | 'openai-chat' | 'openai-responses' | 'custom'
  patches: RequestPatchRule[]
}

export interface ModelAlias {
  /** 调用方要的名字:"claude-sonnet-4" / "glm-4.6" */
  alias: string
  providerId: string
  /** 实际下发给上游的名字 */
  upstreamModel: string
  /** Optional protocol override for this provider×model binding. Missing means inherit provider.protocol. */
  protocolOverride?: UpstreamProtocol
  /** Read-only effective provider protocol for thinking controls, never persisted. */
  runtimeProtocol?: UpstreamProtocol
  /** 同一供应商内的优先级；数字越小越靠前。旧记录缺失时按别名稳定排序。 */
  priority?: number
  capabilities: ModelCapabilities
  contextWindow: number
  maxOutputTokens: number
  displayName?: string
  modality?: ModelModality
  enabled?: boolean
  thinkingConfig?: ThinkingConfig
  /** Exact strengths accepted by this provider×model binding, when known. */
  reasoningEfforts?: readonly ReasoningEffort[]
  requestAdapter?: RequestAdapterConfig
  source?: { url: string; fetchedAt: string; verifiedAt?: string }
  /** 视频绑定的档案引用。只在视频模型上有;缺席 = 这条不能用来生成视频。 */
  video?: ModelVideoBinding
  /** Fields explicitly customized for this binding. Other fields follow the catalogue.
   * Missing on legacy records; an empty list explicitly opts into all catalogue defaults. */
  catalogOverrides?: readonly ModelCatalogOverride[]
}

/** Resolve the protocol used for a concrete provider/model binding. */
export function effectiveModelProtocol(
  provider: Pick<UpstreamProvider, 'protocol'>,
  alias: Pick<ModelAlias, 'protocolOverride'>
): UpstreamProtocol {
  return alias.protocolOverride ?? provider.protocol
}

/**
 * 这条别名是**图片模型**吗。
 *
 * 需求:三个消费方共用同一条判据,分家的症状各不相同但都不报错 ——
 * 1. 「图片生成」设置页的供应商列表/模型选择器(`ImageModelPage`)过滤;
 * 2. 对话模型选择器(输入框、通用页、工作区、Hooks)**反向**过滤,不让图片模型
 *    混进聊天模型列表 —— 选中它发消息是一次注定失败的对话;
 * 3. 生图桥(`kernel/image-gen.ts`)确认点名的模型确实是图片模型。
 *
 * ★ 判据是 `modality === 'image' || capabilities.imageOutput === true` 两路取或:
 *   目录收录的模型带 modality,拉取/手建的只有能力位,缺一边都会漏一族。
 */
export function isImageModelAlias(alias: Pick<ModelAlias, 'modality' | 'capabilities'>): boolean {
  return alias.modality === 'image' || alias.capabilities.imageOutput === true
}

/**
 * 这条别名是**视频生成模型**吗。
 *
 * 需求:与 `isImageModelAlias` 一一对应,三个消费方共用同一条判据 ——
 * 1. 「视频生成」设置页的供应商列表/模型选择器(`VideoModelPage`)过滤;
 * 2. 对话模型选择器**反向**过滤,不让视频模型混进聊天列表(选中它发消息
 *    是一次注定失败的对话,而且它多半连 `textOutput` 都没声明);
 * 3. 视频桥(`kernel/video-gen.ts`)确认点名的模型确实是视频模型。
 *
 * ★ 判据是 `modality === 'video' || capabilities.videoOutput === true` 两路取或,
 *   与图片那条同构:目录收录的带 modality,拉取/手建的只有能力位。
 */
export function isVideoModelAlias(alias: Pick<ModelAlias, 'modality' | 'capabilities'>): boolean {
  return alias.modality === 'video' || alias.capabilities.videoOutput === true
}

/**
 * 这条别名能当**对话模型**吗(与两个生成模态互补 + 排除纯输出非文本的)。
 *
 * 需求:所有「选对话模型」的选择器都用它过滤(见 `isImageModelAlias` 的消费方清单第 2 条)。
 * `textOutput !== false` 那半排除的是显式标了「不产文本」的模型(纯生图/纯视频),
 * 它们即使没被认成任一种生成模型,照样不是聊天候选 —— 让用户选中它是画一个失效的控件。
 *
 * ★ 视频那半必须是**独立的一次判断**,不能靠 `textOutput !== false` 兜:
 *   用户手建一条视频绑定、只勾了 `videoOutput` 时,`textOutput` 缺席(旧记录)或
 *   仍是 `true`(目录默认),那次判断放它过去,于是视频模型出现在聊天下拉里。
 */
export function isChatModelAlias(alias: Pick<ModelAlias, 'modality' | 'capabilities'>): boolean {
  return !isImageModelAlias(alias) && !isVideoModelAlias(alias) && alias.capabilities.textOutput !== false
}

export const MODEL_METADATA_FIELDS = [
  'displayName', 'modality', 'contextWindow', 'maxOutputTokens',
  'thinkingConfig', 'reasoningEfforts', 'requestAdapter', 'source', 'video'
] as const

export type ModelCatalogOverride = typeof MODEL_METADATA_FIELDS[number] | `capabilities.${keyof ModelCapabilities}`

export function isModelCatalogOverride(value: unknown): value is ModelCatalogOverride {
  return typeof value === 'string' && (
    (MODEL_METADATA_FIELDS as readonly string[]).includes(value) ||
    ['tools', 'vision', 'thinking', 'caching', 'textInput', 'visionInput', 'fileInput',
      'videoInput', 'audioInput', 'textOutput', 'imageOutput', 'videoOutput', 'audioOutput',
      'webSearch', 'structuredOutput', 'streaming', 'batch'].some((key) => value === `capabilities.${key}`)
  )
}

/**
 * 从上游 `GET …/models` 拉回来的一条。**这不是配置,是上游报上来的事实。**
 *
 * 和 `ModelAlias` 分开是因为两者知道的东西完全不同:这边只有一个真实模型名
 * (顶多再加个显示名),而别名表里那些 `capabilities` / `contextWindow`
 * **模型列表端点根本不提供** —— 两族协议都不提供。合成一个类型的话,
 * 那几个字段在导入这条路上只能被编出来。
 */
export interface FetchedModel {
  /** 下发给上游的真实模型名 */
  id: string
  /** Anthropic 的 `display_name`。OpenAI 族没有这个字段 */
  displayName?: string
}

/**
 * 一家供应商最多配多少个别名。参考图的导入弹窗写死了这个数
 * (「取消勾选会从当前列表删除(最多 20 个)」/「更新列表(17/20)」)。
 *
 * ★ 主进程**也要**照它拒绝,不是只做界面上的置灰:频道可以被直接调用
 * (协议 §3 规则 6 同一个理由),而这个上限的实际作用是挡住
 * 「一家聚合平台拉回来 300 个模型,用户手一滑全勾上」——
 * 那之后左列和模型下拉框会长到没法用。
 */
export const MAX_ALIASES_PER_PROVIDER = 20

/**
 * 导入一个**新**模型时,别名表那三样填什么。
 *
 * ★★ **模型列表端点不给这些信息,所以这里没有「正确答案」,只有「错的方向」。**
 * 三个值各自选了错起来代价小的那一侧:
 *
 * - `thinking: false` —— 全表唯一有运行时后果的一个
 *   (`agent-session.ts` 拿它决定要不要下发思考预算)。填 true 而模型不支持,
 *   请求直接 400,用户连话都发不出去;填 false 而模型支持,只是思考档位被忽略,
 *   而界面上本来就写着「不支持该参数的模型将自动忽略此设置」。
 * - `maxOutputTokens: 8192` —— 这是「查得到模型、但不知道其协议输出上限」时的
 *   保守能力声明。正文请求默认上限是 32K(见 `run-request.ts`),不会被更大的模型上限
 *   抬高;这里仍保留 8192 作为较小的安全封顶,避免未知模型收到超出能力的请求。
 * - `tools` / `vision` / `caching` —— 今天全应用没有任何代码读它们
 *   (grep 得到零个消费者),所以填什么都不改变行为。跟着 seed 那条走,
 *   将来真接上去的时候是一次统一的改动,而不是「导入进来的和种进去的不一样」。
 */
export const IMPORTED_ALIAS_DEFAULTS: {
  capabilities: ModelCapabilities
  contextWindow: number
  maxOutputTokens: number
} = {
  capabilities: {
    tools: true,
    vision: true,
    thinking: false,
    caching: true,
    textInput: true,
    fileInput: false,
    videoInput: false,
    audioInput: false,
    textOutput: true,
    imageOutput: false,
    videoOutput: false,
    audioOutput: false,
    webSearch: false,
    structuredOutput: true,
    streaming: true,
    batch: false
  },
  contextWindow: 200_000,
  maxOutputTokens: 8192
}

// ─── 健康与故障切换(方案 §5.3) ───

export interface ProviderHealth {
  providerId: string
  healthy: boolean
  /** 0–1 健康度:成功/失败/错误码/延迟综合 */
  score: number
  consecutiveFailures: number
  /** 冷却到期时间戳;之后重新纳入候选 */
  cooldownUntil?: number
  lastLatencyMs?: number
  lastError?: string
  lastCheckedAt: number
}

/**
 * ★ 旁路开关落在**路由器**,不落在 HTTP 壳(方案 §5.6)。
 * 内核和网关都受这一个开关支配,不会出现「UI 里的对话有 failover、
 * 外部 SDK 调进来没有」这种分裂。
 */
export interface GatewayStatus {
  enabled: boolean
  /** ★ 实际端口(19836 被占时会动态选)经这里回传渲染层(方案 §5.4 第 5 条) */
  port: number | null
  /** 原先固定 loopback 以免意外暴露；现在仅显式 allowRemote 才绑定 0.0.0.0。 */
  host: '127.0.0.1' | '0.0.0.0'
  /** 网关 key 是**必需的**,不是可选的;这里只报告有没有 */
  hasKey: boolean
  failover: boolean
  health: ProviderHealth[]
  error?: string
}

export interface FailoverEvent {
  from: string
  to: string
  reason: string
  at: number
}
