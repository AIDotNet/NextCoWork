/**
 * UpstreamRouter —— 方案 §5.1 的中枢:**归一化与治理是一个纯模块**,
 * 内核在进程内直接调它,HTTP 网关(步骤 13)只是同一个模块上的一层薄协议壳。
 *
 * 天真的做法是内核有自己的 provider 抽象、网关另写一套协议转换 —— 那就有
 * 两份停止原因映射、两份工具定义互转、两份 SSE 解析,并且它们会**缓慢地不一致**。
 *
 * ★ 这个文件里最重要的一行是 `sawContent` 的那个判断,见 §5.3。
 */
import type { AgentError } from '../../../shared/agent/error'
import { agentError } from '../../../shared/agent/error'
import { bearerOf } from '../../../shared/domain/credential'
import { CredentialAuthError, CredentialResolver } from './credential-resolver'
import type { ProviderStreamEvent, StopReason, TokenUsage } from '../../../shared/agent/stream'
import type { ResolvedModelThinking } from '../../../shared/domain/model-runtime'
import { resolveModelThinking } from '../../../shared/domain/model-runtime'
import { modelBindingsFor } from '../../../shared/domain/model-selection'
import { REQUEST_PATH } from '../../../shared/domain/baseurl'
import type { ModelAlias, ProviderHealth, ThinkingConfig, UpstreamProvider, UpstreamProtocol } from '../../../shared/domain/provider'
import { anthropicCacheTtlOf, effectiveModelProtocol } from '../../../shared/domain/provider'
import { applyRequestPatches, RequestPatchError } from '../../../shared/domain/request-patch'
import { DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS } from '../../../shared/domain/settings'
import {
  applyThinkingAdapter,
  enforceThinkingPreference,
  reasoningReplayFor,
  ThinkingAdapterError,
  type ThinkingAdapterInput
} from '../../../shared/domain/thinking-adapter'
import { abortable, abortableSleep, abortableStream, isAbortError } from '../abort'
import type { AccountPool } from './account-pool'
import type { ProviderAccount } from '../../../shared/domain/provider-account'
import { accountCredentialRef } from '../../../shared/domain/provider-account'
import { codexQuotaHeaderNames, headerReaderOf, parseCodexQuota } from './codex-quota'
import { estimateTokens } from '../context-assembler'
import { userAgent } from '../user-agent'
import type { KernelHost } from '../host'
import type { UnpricedUsageAttempt } from '../../../shared/domain/usage'
import type { RunCost } from '../../../shared/domain/pricing'
import { ulid } from '../../../shared/util/id'
import {
  joinUpstreamUrl,
  type CanonicalRequest,
  type UpstreamRequestContext
} from './canonical'
import { anthropicErrorToAgentError } from './decode/anthropic'
import { interruptedResponse, openAIErrorToAgentError } from './decode/openai-common'
import { applyAnthropicRequestOptions } from './encode/anthropic'
import { decodeUpstream, encodeUpstream } from './codec'
import { upstreamTransport, authHeader } from './transport'
import { ImageInputError, prepareRequestImages } from './images'

/** 每个 provider 最多试几次(含首次)。第 3 次还不行,换下一个 provider 比继续磕更有用。 */
const MAX_ATTEMPTS = 3
/**
 * 纯连接层错误(fetch 本身抛出,比如 `net::ERR_HTTP2_PROTOCOL_ERROR`、DNS 失败、
 * 握手中断)不是「这家供应商不行」,是「这台机器这一刻连不上网」——换下一个 provider
 * 大概率也是同一个病因,真正管用的是**多等一会儿再碰**。所以给它比 `MAX_ATTEMPTS`
 * 更多的机会,而不是三次就判它「换家」或者干脆报错干瞪眼(方案 §4.2 的失败态本该
 * 是「明确没救了」,一次 HTTP2 协议错误显然算不上)。
 */
const MAX_NETWORK_ATTEMPTS = 6
const DEFAULT_BASE_DELAY_MS = 500
/**
 * 可重试的 provider 错误(5xx / overloaded / 网关转发失败)专用的退避基数,
 * 和上面那个**刻意不是一个数**。
 *
 * ★★ 500ms 起步的指数退避只对**网络层**抖动是对的 —— TCP 重传、DNS 换源,那类
 * 故障毫秒级自愈。网关回 5xx 不是抖动:是它后面那家挂了或者过载,恢复窗口至少
 * 是秒级。亚秒级退回去只是再吃一次同样的错,三次机会两秒内用光,用户看到的
 * 是「报错一条接一条地刷」而不是「在慢慢重试」。上游给了 `Retry-After` 依旧
 * 一切以它为准(见 `retryDelayFor`)。
 */
const DEFAULT_PROVIDER_ERROR_FLOOR_MS = 4_000
/**
 * 默认值取自设置层的 `DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS` —— 那边是
 * 「设置页那一栏的默认」,这边是「没注入函数时的兜底」,两者必须是同一个数。
 */
const DEFAULT_IDLE_TIMEOUT_MS = DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS * 1000
/**
 * 限流(429)专用的退避基数,和上面那个**刻意不是一个数**。
 *
 * ★★ 500ms 起步的指数退避对**网络抖动**是对的 —— 那类故障是瞬时的,退得越快
 * 恢复越快。**限流不是**:配额窗口是分钟级的,0.5s、1s 退回去只是再撞两次墙,
 * 三次机会两秒内用光,然后报一条「超出速率限制」——用户看到的是「重试根本没发生」。
 * (可重试的 provider 5xx 是第三张表 `DEFAULT_PROVIDER_ERROR_FLOOR_MS`,理由见那行。)
 * 上游给了 `Retry-After` 就一切以它为准(见 `retryDelayFor`),这个数只在**没给**
 * 的时候兜底,而 Azure / 各家网关经常不给。
 */
const DEFAULT_RATE_LIMIT_FLOOR_MS = 4_000
/**
 * 退避的硬上限。`Retry-After: 3600` 是真会出现的(配额按小时重置),照着睡
 * 一小时和卡死没有区别 —— 截断到一分钟,让用户在状态行上看见还在退避,
 * 再由他决定要不要换一家或者停掉。
 */
const MAX_RETRY_DELAY_MS = 60_000
/** 连续失败到这个数就判不健康 */
const UNHEALTHY_AFTER = 3
/**
 * 一次逻辑请求里最多换几个账号。
 *
 * ★★ **这是一道保险,不是业务规则。** 正常情况下换号次数天然被账号数量兜住:
 * 每换一次都会先给上一个落闸,`select()` 不会再挑到它。但「换号不消耗重试次数」
 * 这条规则意味着**只要池子一直说"还能换",循环就一直不前进** —— 池子那边任何一个
 * 判断写歪(比如轮换关掉时仍返回下一个账号)都会变成一个死循环:run 卡住、
 * CPU 打满、一条错误都不输出。这个上限保证最坏情况也只是多打几次请求。
 */
const MAX_ACCOUNT_SWITCHES = 8
const COOLDOWN_MS = 30_000
const ANTHROPIC_USER_ID_MAX = 512

/**
 * 配置来源。步骤 6 之后由 SQLite 提供,现在由内存 store 提供 ——
 * 路由器不关心它们从哪来,只关心「现在这一刻的配置是什么」。
 * 用函数而不是快照数组:设置页改完 provider 立刻生效,不必重建路由器。
 */
export interface ProviderConfigSource {
  providers(): readonly UpstreamProvider[]
  aliases(): readonly ModelAlias[]
  /** ★ 旁路开关落在路由器,不落在 HTTP 壳(方案 §5.6) */
  failoverEnabled(): boolean
}

interface Candidate {
  provider: UpstreamProvider
  alias: ModelAlias
}

function thinkingConfigFor(req: CanonicalRequest, c: Candidate, protocol: UpstreamProtocol): ThinkingConfig | undefined {
  if (c.alias.thinkingConfig !== undefined) return c.alias.thinkingConfig
  if (req.thinkingBudget !== undefined) return { mode: 'budget', defaultEnabled: true }
  if (req.reasoning?.explicit !== true || req.reasoning.enabled) return undefined
  // Old model imports defaulted capabilities.thinking to false and had no
  // ThinkingConfig. Known toggle protocols still support an explicit Off.
  if (protocol === 'anthropic'
    || /(?:^|\/)(?:deepseek|deep-seek|glm|chatglm|hy3|hy4)[/:._-]/iu.test(c.alias.upstreamModel)
    || c.alias.capabilities.thinking) return { mode: 'toggle', defaultEnabled: false }
  return undefined
}

function reasoningFor(req: CanonicalRequest, alias: ModelAlias): ResolvedModelThinking | undefined {
  if (req.thinkingLevel !== undefined && alias.thinkingConfig !== undefined) {
    /*
      需求:思考预算的天花板要用**这次真的会发出去的** max_tokens,也就是 req 里那个
      (由全局设置项决定,见 `shared/agent/run-request.ts` 的 `resolveMaxOutputTokens`)。
      原先这里还和 `alias.maxOutputTokens` 取小 —— 那是输出额度仍由模型目录说了算时
      的同一条边界;现在目录里那个数不再进请求体,继续拿它当分母只会把预算按一个
      没人在用的上限压低(症状:设置里放开到 32K,思考却还是按 16K 的余量算)。
      Anthropic 的 `budget_tokens >= max_tokens` 那道 400 仍由 encode 侧兜底。
    */
    return resolveModelThinking(req.thinkingLevel, alias.thinkingConfig,
      req.maxOutputTokens, alias.reasoningEfforts)
  }
  if (req.reasoning !== undefined) return req.reasoning
  if (req.thinkingBudget === undefined || alias.thinkingConfig?.mode === 'unsupported') return undefined
  return {
    mode: alias.thinkingConfig?.mode ?? 'budget',
    enabled: true,
    explicit: true,
    budgetTokens: req.thinkingBudget
  }
}

/**
 * ★ 「第一个内容字节」的定义。
 *
 * `message_start` **不算** —— 它不携带任何内容,此时重试不会产生重复输出。
 * 从 text/thinking/tool_call/opaque 中任何一个开始,消费者手里就有了半条消息,
 * 换个 provider 重来就会出现重复文字和错位的工具调用。
 */
function isContent(e: ProviderStreamEvent): boolean {
  switch (e.type) {
    case 'text_delta':
    case 'thinking_delta':
    case 'tool_call_start':
    case 'tool_call_delta':
    case 'tool_call_end':
    case 'block_opaque':
    case 'message_end':
      return true
    default:
      return false
  }
}

/**
 * `Retry-After` 可以是秒数,也可以是 HTTP 日期。
 *
 * ★ 尊重它不是礼貌问题:限流时按自己的退避表硬撞,只会让冷却期不断延长
 * (很多上游把「限流期间的请求」也计入配额)。
 */
export function parseRetryAfter(raw: string | null, now: number): number | undefined {
  if (raw === null || raw.trim() === '') return undefined
  const secs = Number(raw)
  if (Number.isFinite(secs) && secs >= 0) return Math.min(secs * 1000, 60_000)
  const at = Date.parse(raw)
  if (Number.isFinite(at)) return Math.min(Math.max(at - now, 0), 60_000)
  return undefined
}

/** 一次尝试的结局。generator 的返回值 —— 用 `yield*` 一句话拿到。 */
type Outcome =
  | { kind: 'ok' }
  /** 失败。`sawContent` 决定还能不能重试/切换 —— §5.3 那条边界 */
  | { kind: 'failed'; error: AgentError; sawContent: boolean }

export interface UpstreamRouterOptions {
  baseDelayMs?: number
  /**
   * 上游空闲超时。给数字就是定值(测试用);给函数则**每次请求时重新求值** ——
   * `runtime.ts` 用后者把设置页「连接 › 网络」的那一栏接进来,改完立刻对
   * 下一条请求生效,不必重建路由器(和 `ProviderConfigSource` 用函数同一个理由)。
   */
  idleTimeoutMs?: number | (() => number)
  /** 限流退避的基数。见 `DEFAULT_RATE_LIMIT_FLOOR_MS`;测试压成 0 就不会真的睡。 */
  rateLimitFloorMs?: number
  /**
   * 可重试 provider 错误(5xx)退避的基数。见 `DEFAULT_PROVIDER_ERROR_FLOOR_MS`;
   * 测试压成 0 就不会真的睡 —— 否则每个 500 用例都要等上几秒。
   */
  providerErrorFloorMs?: number
  /** Synchronous sink; failures are isolated so telemetry can never fail a request. */
  onUsageAttempt?: (record: UnpricedUsageAttempt) => void
  /**
   * 某条凭证被刷新(或被标成需要重新登录)了。
   *
   * ★ 内核拿不到窗口,所以这是**注入回调**,和上面 `onUsageAttempt` 同一个套路。
   * 设置页据此把「已登录」改成「登录已失效」—— 不推的话,用户要关掉再打开设置页
   * 才知道自己已经掉线了。
   */
  onCredentialChanged?: (credentialRef: string) => void
  /**
   * 这一次请求花了多少钱。★ 又是**注入回调**,和上面两个同一个套路:价目表在
   * `runtime.ts`(它要查内置模型表才知道该按哪个 modelId 计价),内核不认识它。
   *
   * 返回 `null` = 查不到价。它会原样进 `message_end.cost`,渲染层据此把整轮
   * 报成「算不出」而不是显示一个偏低的数(见 `transcript.ts` 的 `addCost`)。
   *
   * 不注入时 `message_end` 里**根本不会有 cost 这个字段** —— 现有测试、
   * gateway、fake-emitter 于是一行都不用改。
   */
  priceAttempt?: (
    providerId: string,
    upstreamModel: string,
    usage: TokenUsage,
    at: number
  ) => RunCost | null
  /**
   * 同一家供应商的多个登录账号(schema 第 24 条)。
   *
   * ★ **不给就退化成单槽**:`attempt()` 里 `select()` 返回 null 时用的是
   * `provider.credentialRef` —— 那条路径逐字节等于多账号上线之前。
   * 于是 gateway、会话标题生成器和现有全部 router 测试一行都不用改
   * (和 `credentials` 在构造函数里自己建是同一个套路)。
   */
  accounts?: AccountPool
}

export class UpstreamRouter {
  private readonly healthMap = new Map<string, ProviderHealth>()
  /**
   * 「这一家在这个时刻之前别再发请求了」—— 被限流之后立起来的闸门。
   *
   * ★★ 它的价值**整个在于这个类是进程内单例**(`runtime.ts` 的 `getRouter()`):
   * 主代理和它派出去的每一个子代理共用同一个实例,于是任意一条流吃到 429,
   * 其余几条**在发请求之前**就会被挡住。没有它,四个并发子代理是各自独立退避的:
   * 同时撞、同时退、再同时撞,把本来够用的配额彼此打光,而每一条看到的都只是
   * 「我被限流了」,没有任何一条知道是自己人干的。
   *
   * ★ 只记 `rate_limit`。5xx / 网络错误是**这一条请求**的事,拿它去挡住别的流
   * 会把一次偶发抖动放大成全局停顿。
   *
   * ★★ **键从 providerId 细化成了「账号 ref ?? providerId」**(多账号上线,
   * 见 `gateKeyFor`)。上面那条理由一个字都没变 —— 变的只是粒度:额度是按账号
   * 算的,拿一个账号的 429 去挡住同一家的**另一个**账号,等于把多账号这件事
   * 白做了(表现:配了三个号,一个被限流之后整家都发不出请求)。
   * 没有账号表的供应商仍然按 providerId 记,行为逐字节不变。
   */
  private readonly rateLimitGate = new Map<string, { until: number; reason: string }>()
  private readonly baseDelayMs: number
  private readonly idleTimeoutMs: () => number
  private readonly rateLimitFloorMs: number
  private readonly providerErrorFloorMs: number
  private readonly onUsageAttempt: ((record: UnpricedUsageAttempt) => void) | undefined
  private readonly priceAttempt: UpstreamRouterOptions['priceAttempt']
  /**
   * ★ 在构造函数里自己建,**不作为必填参数** —— 于是 gateway 和现有全部测试里那些
   * `new UpstreamRouter(host, config)` 一行都不用改。
   */
  private readonly credentials: CredentialResolver
  /** 见 `UpstreamRouterOptions.accounts`:不给就走单槽,逐字节等于多账号上线之前 */
  private readonly accounts: AccountPool | undefined

  constructor(
    private readonly host: KernelHost,
    private readonly config: ProviderConfigSource,
    opts: UpstreamRouterOptions = {}
  ) {
    this.baseDelayMs = opts.baseDelayMs ?? DEFAULT_BASE_DELAY_MS
    const idleTimeout = opts.idleTimeoutMs
    this.idleTimeoutMs = typeof idleTimeout === 'function'
      ? idleTimeout
      : () => idleTimeout ?? DEFAULT_IDLE_TIMEOUT_MS
    this.rateLimitFloorMs = opts.rateLimitFloorMs ?? DEFAULT_RATE_LIMIT_FLOOR_MS
    this.providerErrorFloorMs = opts.providerErrorFloorMs ?? DEFAULT_PROVIDER_ERROR_FLOOR_MS
    this.onUsageAttempt = opts.onUsageAttempt
    this.priceAttempt = opts.priceAttempt
    this.credentials = new CredentialResolver(host, opts.onCredentialChanged)
    this.accounts = opts.accounts
  }

  /**
   * 算这一次请求的钱。★ 抛异常**绝不能**掀掉整条流 —— 和 `onUsageAttempt`
   * 外面那圈 try/catch 同一个道理,只是这里更要紧:那个是收尾时的遥测,
   * 这个在流中间,抛出去用户看到的是回复被截断。
   *
   * 出错按 `null`(算不出)记,不按 undefined(没接计价):钩子接了却抛了,
   * 那这一轮的总额确实不可信,该整轮不显示,而不是悄悄少算这一次。
   */
  private priceOfAttempt(
    providerId: string,
    upstreamModel: string,
    usage: TokenUsage,
    at: number
  ): RunCost | null | undefined {
    if (this.priceAttempt === undefined) return undefined
    try {
      return this.priceAttempt(providerId, upstreamModel, usage, at)
    } catch (error) {
      this.host.logger.warn('[usage] 计价失败,本轮不显示金额,请求本身不受影响', error)
      return null
    }
  }

  listModels(): ModelAlias[] {
    const enabled = new Set(
      this.config.providers().filter((p) => p.enabled).map((p) => p.id)
    )
    return this.config.aliases().filter((a) => enabled.has(a.providerId) && a.enabled !== false)
  }

  /**
   * 「这个别名 + 这个供应商」最终落在哪一条绑定上 —— 路由器之外的调用方
   * (`agent-session` 校验上下文、`session-title` 挑思考档、`runtime` 校验
   * 审核模型)都必须走这里,而不是自己 `listModels().find((m) => m.alias === x)`。
   *
   * ★ 自己 find 的话取的是数组第一条,而路由器取的是 priority 最小的那条 ——
   * 两者只是碰巧一致。分家之后的症状是:界面按 A 的上下文窗口做校验,
   * 请求却发给了 B,而且不报错。
   *
   * ★★ **不过健康冷却**(所以不复用 `candidates()`):它回答的是「这个选择指向
   * 哪一条绑定」,不是「现在该试谁」。冷却期内返回 undefined 会让上下文长度校验
   * 被静默跳过 —— 那是个只在供应商刚挂过之后才复现的幽灵 bug。
   */
  resolveModel(model: string, modelProviderId?: string): ModelAlias | undefined {
    const providers = this.config.providers()
    const alias = modelBindingsFor(
      this.config.aliases(), providers, model, modelProviderId
    )[0]
    if (alias === undefined) return undefined
    const provider = providers.find((p) => p.id === alias.providerId)
    return provider === undefined
      ? undefined
      : { ...alias, runtimeProtocol: effectiveModelProtocol(provider, alias) }
  }

  health(): ProviderHealth[] {
    return [...this.healthMap.values()]
  }

  resetHealth(providerId?: string): void {
    if (providerId === undefined) {
      this.healthMap.clear()
      this.rateLimitGate.clear()
    } else {
      this.healthMap.delete(providerId)
      // 「重置健康状态」在用户眼里就是「当它没挂过,现在就重试」——
      // 留着限流闸门会让那一下点击看起来毫无反应(它会安静地睡满剩下的退避)。
      // ★ 闸门现在按账号记(见 `rateLimitGate`),所以这家名下的几把一起清:
      //   只清 providerId 那一把会漏掉全部账号级闸门,而用户点的是同一颗按钮。
      this.rateLimitGate.delete(providerId)
      for (const key of [...this.rateLimitGate.keys()]) {
        if (key.startsWith(`provider:${providerId}#`)) this.rateLimitGate.delete(key)
      }
    }
  }

  /**
   * 候选集:同一个 alias 可能由多个 provider 提供 —— **这正是别名表存在的理由**,
   * 没有它就谈不上「切到下一个」。
   *
   * ★★ `modelProviderId` 有值 = 用户在药丸里**显式选了那一家**,这是一条**硬约束**:
   * 候选集被压到最多一条,于是下面那两层 for(换供应商 / 重试)天然只在这一家里
   * 打转,`provider_switch` 也永远不会发出来。**锁死语义整个由候选集表达**,
   * 不要去 `stream()` 里加 `if (pinned)` 分支 —— 那会造出第二个真相来源。
   */
  private candidates(model: string, modelProviderId?: string): Candidate[] {
    const byId = new Map(this.config.providers().map((p) => [p.id, p]))
    /*
      ★★ 收集与排序**整个借给** `model-selection.ts` —— 渲染层的药丸用的是同一个
      函数。这是整套设计的支点:药丸上显示的那家,和这里真正发请求的那家,由同一段
      代码算出。以前两边各写一遍(那边取数组第一条、这边按 priority 排),只是碰巧
      一致,一旦分家就是「界面说 A、请求发给 B」且不报任何错 —— 正是用户报的那个 bug。

      `modelProviderId` 有值 = 用户显式选了那一家,那边会把候选压到最多一条,
      于是下面两层 for(换供应商 / 重试)天然只在这一家里打转,`provider_switch`
      也永远不会发出来。**锁死语义整个由候选集表达**,不要去 `stream()` 里加
      `if (pinned)` 分支 —— 那会造出第二个真相来源。
    */
    const list: Candidate[] = modelBindingsFor(
      this.config.aliases(), this.config.providers(), model, modelProviderId
    ).map((alias) => ({ provider: byId.get(alias.providerId)!, alias }))

    // 旁路模式:直取优先级最高的一个,不做健康评分、不做切换。延迟最低、路径最短。
    // ★ `failoverEnabled` 出厂就是 false,所以这条短路才是绝大多数用户实际走的
    //   路径 —— provider 过滤必须发生在它**之前**(已经在上面那个函数里了)。
    if (!this.config.failoverEnabled()) return list.slice(0, 1)

    /*
      ★ 钉住时跳过冷却过滤。冷却的意义是「绕开一家坏的」,而锁死之后**无处可绕**:
      过滤掉唯一那条候选,用户会收到「所有供应商都在冷却中」—— 他明明只选了一家,
      而且界面上没有任何「再试一次」的入口,看起来就是应用坏了。
      `MAX_ATTEMPTS` 加指数退避已经护得住配额。健康分照常记(`recordFailure` 不动),
      设置页那个健康指示器仍然准确。
    */
    if (modelProviderId !== undefined) return list

    const now = this.host.clock.now()
    const usable = list.filter((c) => {
      const h = this.healthMap.get(c.provider.id)
      return h === undefined || h.cooldownUntil === undefined || h.cooldownUntil <= now
    })
    // 健康度只在同优先级内部起作用 —— 优先级是用户的显式意图,不该被评分推翻
    usable.sort((a, b) => {
      const d = a.provider.priority - b.provider.priority
      if (d !== 0) return d
      return (this.healthMap.get(b.provider.id)?.score ?? 1) - (this.healthMap.get(a.provider.id)?.score ?? 1)
    })
    return usable
  }

  private healthOf(id: string): ProviderHealth {
    let h = this.healthMap.get(id)
    if (!h) {
      h = { providerId: id, healthy: true, score: 1, consecutiveFailures: 0, lastCheckedAt: 0 }
      this.healthMap.set(id, h)
    }
    return h
  }

  private recordSuccess(id: string, latencyMs: number, gateKey = id): void {
    const h = this.healthOf(id)
    h.healthy = true
    h.consecutiveFailures = 0
    // 恢复比衰减快,但要多次成功才回到 1 —— 一次侥幸成功不该抹掉刚才连挂三次的事实
    h.score = Math.min(1, h.score * 1.5 + 0.1)
    h.lastLatencyMs = latencyMs
    h.lastCheckedAt = this.host.clock.now()
    delete h.cooldownUntil
    delete h.lastError
    // 一次成功 = 配额确实回来了。留着闸门只会让后面那几条流白等一场。
    // ★ 清的是**这一次真的用了的那个键**(账号 ref 或 providerId)——
    //   拿 providerId 去清账号级闸门等于没清,表现是换号成功之后
    //   其余并发流仍然在等一个已经不存在的限流。
    this.rateLimitGate.delete(gateKey)
  }

  /**
   * 这次失败该退避多久。三张表:限流、可重试的 provider 5xx、其余(网络抖动)。
   *
   * ★ 上游给了 `Retry-After` 就**只听它的**,不套下面那个下限:它知道配额窗口
   * 什么时候重置,我们不知道。(`retry-after: 0` 是合法的「立刻再来」,别抬成 4 秒。)
   */
  private retryDelayFor(err: AgentError, attempt: number): number {
    if (err.retryAfterMs !== undefined) return Math.min(err.retryAfterMs, MAX_RETRY_DELAY_MS)
    const base = err.code === 'rate_limit' ? this.rateLimitFloorMs
      : err.code === 'provider' && err.retryable === true ? this.providerErrorFloorMs
      : this.baseDelayMs
    return Math.min(base * 2 ** attempt, MAX_RETRY_DELAY_MS)
  }

  /** 闸门还剩多久;没被挡住就是 0。★ 键是 `gateKeyFor` 给的那个,不一定是 providerId */
  private gateWaitFor(gateKey: string): { waitMs: number; reason: string } {
    const gate = this.rateLimitGate.get(gateKey)
    if (gate === undefined) return { waitMs: 0, reason: '' }
    const waitMs = gate.until - this.host.clock.now()
    if (waitMs <= 0) {
      this.rateLimitGate.delete(gateKey)
      return { waitMs: 0, reason: '' }
    }
    return { waitMs, reason: gate.reason }
  }

  /**
   * 进程内限流闸门的键。
   *
   * ★ 有账号时用账号的凭证 ref,没有时退回 providerId —— 后者正是多账号上线之前
   * 的行为,所以纯 API Key 供应商的闸门逐字节不变。
   */
  private gateKeyFor(providerId: string, account: ProviderAccount | null): string {
    return account === null ? providerId : accountCredentialRef(account)
  }

  private recordFailure(id: string, err: AgentError): void {
    const h = this.healthOf(id)
    h.consecutiveFailures += 1
    h.score = h.score * 0.5
    h.lastError = err.message
    h.lastCheckedAt = this.host.clock.now()
    if (h.consecutiveFailures >= UNHEALTHY_AFTER) {
      h.healthy = false
      h.cooldownUntil = this.host.clock.now() + COOLDOWN_MS
    }
  }

  /**
   * 一次 HTTP 尝试。yield 归一化事件,**返回**结局。
   *
   * 拆成独立 generator 是为了让上面那层的重试/切换逻辑能用一行 `yield*` 读完,
   * 而不是三层嵌套的 try/for/for。
   */
  private async *attempt(
    c: Candidate,
    req: CanonicalRequest,
    parentSignal: AbortSignal,
    context: UpstreamRequestContext,
    attemptNumber: number,
    /**
     * 这一次用哪个账号。**`null` = 这家没有账号表**(纯 API Key / 还没登录),
     * 凭证走 `provider.credentialRef` —— 那条路径逐字节等于多账号上线之前。
     *
     * ★ 账号由上一层(`stream`)挑好再传进来,不在这里自己挑:挑账号要读闸门,
     * 而闸门的等待发生在上一层(等待不消耗重试次数)。两处各挑一次的话,
     * 等的是 A、发给的是 B。
     */
    account: ProviderAccount | null
  ): AsyncGenerator<ProviderStreamEvent, Outcome> {
    let sawContent = false
    const protocol = effectiveModelProtocol(c.provider, c.alias)
    const startedAt = this.host.clock.now()
    const endpoint = joinUpstreamUrl(c.provider.baseUrl, REQUEST_PATH[protocol])
    let httpStatus: number | null = null
    let responseModel: string | null = null
    let stopReason: StopReason | null = null
    let timeToFirstTokenMs: number | null = null
    let thinkingText = ''
    let toolCalls = 0
    let usage: TokenUsage = { inputTokens: 0, outputTokens: 0 }
    let recorded = false
    const controller = new AbortController()
    const signal = AbortSignal.any([parentSignal, controller.signal])
    let timedOut = false
    let idleTimer: ReturnType<typeof setTimeout> | undefined
    const resetIdleTimeout = (): void => {
      if (idleTimer !== undefined) clearTimeout(idleTimer)
      idleTimer = setTimeout(() => {
        timedOut = true
        controller.abort()
      }, this.idleTimeoutMs())
    }
    const waitFor = <T>(operation: () => PromiseLike<T>): Promise<T> => {
      resetIdleTimeout()
      return abortable(operation, signal)
    }

    const finish = (outcome: Outcome): Outcome => {
      if (recorded) return outcome
      recorded = true
      const endedAt = this.host.clock.now()
      const explicitThinking = usage.reasoningTokens
      const rawThinkingEstimate =
        explicitThinking === undefined && thinkingText !== ''
          ? estimateTokens(thinkingText)
          : null
      // Reasoning is a subset of outputTokens. Text-based estimation can be
      // higher than the provider tokenizer, so never let the diagnostic
      // breakdown contradict the provider's billable output total.
      const estimatedThinking =
        rawThinkingEstimate === null
          ? null
          : usage.outputTokens > 0
            ? Math.min(rawThinkingEstimate, usage.outputTokens)
            : rawThinkingEstimate
      const error = outcome.kind === 'failed' ? outcome.error : null
      try {
        this.onUsageAttempt?.({
          id: ulid(endedAt),
          at: startedAt,
          runId: context.runId ?? `run_${ulid(startedAt)}`,
          workspaceId: context.workspaceId,
          sessionId: context.sessionId ?? '',
          attempt: attemptNumber,
          providerId: c.provider.id,
          providerName: c.provider.name,
          protocol,
          endpoint,
          alias: req.model,
          upstreamModel: c.alias.upstreamModel,
          responseModel,
          inputTokens: usage.inputTokens,
          outputTokens: usage.outputTokens,
          cacheReadTokens: usage.cacheReadInputTokens ?? 0,
          cacheWriteTokens: usage.cacheCreationInputTokens ?? 0,
          cacheWrite1hTokens: usage.cacheCreation1hInputTokens ?? 0,
          thinkingTokens: explicitThinking ?? estimatedThinking,
          thinkingTokensEstimated: explicitThinking === undefined && estimatedThinking !== null,
          latencyMs: Math.max(0, endedAt - startedAt),
          timeToFirstTokenMs,
          ok: outcome.kind === 'ok',
          httpStatus: httpStatus ?? error?.status ?? null,
          errorKind: error?.code ?? null,
          errorMessage: error?.message.slice(0, 4096) ?? null,
          stopReason,
          toolCalls,
          toolErrors: 0
        })
      } catch (error) {
        this.host.logger.warn('[usage] 写入请求记录失败，请求本身不受影响', error)
      }
      return outcome
    }

    try {
      signal.throwIfAborted()
      /*
        ★★ **凭证 ref 是这一层唯一知道「用了哪个账号」的地方。**
        账号为 null 时取 `provider.credentialRef`(旧槽)—— 那既是 API Key 供应商
        的常态,也是多账号上线之前的全部行为,所以零回归。
      */
      const credentialRef = account === null ? c.provider.credentialRef : accountCredentialRef(account)
      const cred = await waitFor(() => this.credentials.resolve(credentialRef, signal))
      if (cred === null) {
        return finish({
          kind: 'failed',
          sawContent,
          error: agentError('auth', `供应商「${c.provider.name}」还没有配置密钥`, { retryable: false })
        })
      }
      /*
        ★ 两种凭证在**鉴权头这一点上是同构的**:OAuth 的 access token 就是塞进
        `Authorization: Bearer` 的那个串,正是 `encodeOpenAIResponses` 已经在写的头。
        所以 `encodeUpstream` 的签名一个字不用改 —— OAuth 多出来的那些东西
        (账号头、body 强制字段)走 `transport`,在最终线上边界合并。
      */
      const apiKey = bearerOf(cred)
      const cacheTtl = anthropicCacheTtlOf(c.provider)
      /*
        需求:Responses 的回传方言必须在**下发请求体之前**定好,而它和思考线形读的是
        同一份 thinkingConfig(显式声明、standardWire 都在里面)。提上来共用一次调用,
        不在下面再判一遍 —— 两处判据迟早会分叉。
      */
      const thinkingConfig = thinkingConfigFor(req, c, protocol)
      const prepared = await waitFor(() => prepareRequestImages(req, this.host, context, signal))
      const enc = encodeUpstream(protocol, prepared, c.alias.upstreamModel, apiKey, {
        userId: context.workspaceId,
        cacheTtl,
        reasoningReplay: reasoningReplayFor(thinkingConfig, c.alias.upstreamModel)
      })
      const thinkingInput: ThinkingAdapterInput = {
        protocol,
        upstreamModel: c.alias.upstreamModel,
        config: thinkingConfig,
        reasoning: reasoningFor(req, c.alias),
        ...(req.thinkingLevel === undefined ? {} : { thinkingLevel: req.thinkingLevel }),
        maxOutputTokens: req.maxOutputTokens,
        // Legacy bindings without a request adapter must retain automatic
        // model-specific thinking detection. Explicit adapters still win;
        // protocol remains the wire/protocol default for patch validation.
        preset: c.alias.requestAdapter?.preset ?? 'auto',
        ...(c.alias.reasoningEfforts !== undefined
          ? { reasoningEfforts: c.alias.reasoningEfforts }
          : {})
      }
      const adaptedBody = applyThinkingAdapter(enc.body, thinkingInput)
      const patchedBody = applyRequestPatches(
        adaptedBody,
        c.alias.requestAdapter?.patches,
        c.alias.requestAdapter?.preset ?? protocol
      )
      const guardedBody = enforceThinkingPreference(patchedBody, thinkingInput)
      // Model-level patches are allowed to customise ordinary parameters, but
      // Provider-owned Anthropic identity/cache fields are re-applied at the
      // final wire boundary. This keeps metadata.user_id and caching mandatory,
      // with the Provider's 5m/1h lifetime authoritative even for legacy aliases
      // with broad custom patches.
      const anthropicBody = protocol === 'anthropic' ? applyAnthropicRequestOptions(guardedBody, {
        userId: context.workspaceId,
        cacheTtl
      }) : guardedBody
      /*
        ★ **凭证与供应商**决定的那部分请求形状(额外的头、被钉死的 body 字段)在这里
        合并 —— 和上面那段英文注释是**同一条规矩的第二个实例**:供应商自己的硬约束
        压过模型级自定义。API Key 凭证 + 非 OpenCode 供应商走的是恒等变换,
        老供应商逐字节不变。
      */
      const transport = upstreamTransport({ ...c.provider, protocol }, cred, { ...(context.sessionId === undefined ? {} : { sessionId: context.sessionId }) })
      const body = transport.body(anthropicBody)
      const url = joinUpstreamUrl(c.provider.baseUrl, enc.path)
      const payload = JSON.stringify(body)
      /*
        ★★ 签名式鉴权(Ollama)的头和 `?ts=` query 在**这里**逐次现算 —— `send()`
        每被调用一次(首发、以及 401 之后那次重发)都重新签:时间戳式签名复用旧值
        等于没重试。没有 `signRequest` 的供应商这一步是恒等,请求逐字节不变。
      */
      const send = (extraHeaders: Record<string, string>): Promise<Response> => {
        const target = new URL(url)
        /*
         * ★★ 签名用的 path 是**最终 URL 的 pathname**,不是 `enc.path` ——
         * OpenAI 族的 baseUrl 自带 `/v1` 段,服务端按它看到的完整路径重建签名串,
         * 用 `enc.path` 签出来的串对不上,且错误只表现为一个不解释的 401。
         */
        const signed = transport.signRequest?.({ method: 'POST', path: target.pathname })
        if (signed?.query !== undefined) {
          for (const [k, v] of Object.entries(signed.query)) target.searchParams.set(k, v)
        }
        const headers: Record<string, string> = {
          // 自报家门排在最前面:任何一个 encode / transport 想自己写 UA 都压得过它
          'user-agent': userAgent(),
          ...enc.headers,
          ...transport.headers,
          ...(signed?.headers ?? {})
        }
        // ★ 删在合并 extraHeaders 之前:那一个是 401 之后重发的鉴权头,不该被这里带走
        for (const name of transport.dropHeaders ?? []) delete headers[name]
        return this.host.fetch(target.toString(), {
          method: 'POST',
          headers: { ...headers, ...extraHeaders, accept: 'text/event-stream' },
          body: payload,
          signal
        })
      }

      let res = await waitFor(() => send({}))
      /*
        ★★ **401 之后强制刷新一次,就一次。**

        主动的过期检查(`CredentialResolver` 的 SKEW)覆盖不了两种情况:本机时钟偏,
        以及服务端**主动吊销**。两者的表现都是一个「凭证看着没过期却 401」。

        ★ 为什么不交给外层那个重试循环:`openAIErrorToAgentError` 把 401 归为
        `auth` 且 `retryable: false`(见 `decode/openai-common.ts`),外层于是会
        **切到下一个 provider** —— 而下一家根本不提供这个别名的话,最终错误是
        `no_healthy_provider`,一句和真实原因(token 过期)毫无关系的话。

        ★ 只在 oauth 凭证上做,且此刻 `sawContent` 必然为 false(流还没开始),
        所以不存在「重发导致重复输出」的问题 —— 那正是 §5.3 那条判断守的东西。
      */
      if (res.status === 401 && cred.kind === 'oauth') {
        // 必须读完,否则这条连接不会被释放
        await waitFor(() => res.text().catch(() => ''))
        // ★ 刷的必须是**这次用的那条 ref**。拿 `provider.credentialRef`(旧槽)去刷,
        //   刷新后的 token 会写回旧槽,而下一次请求读的是账号 ref —— 401 于是一直复发,
        //   且每次都"刷新成功"。
        const fresh = await waitFor(() => this.credentials.refreshNow(credentialRef, signal))
        /*
         * ★★ 签名式凭证(Ollama)重发**不带 extraHeaders**:`send()` 会现签一把新的
         * (新 ts),而 `authHeader()` 会把凭证槽里的东西当 Bearer/x-api-key 塞进去 ——
         * 那个槽里装的是 SSH 私钥 PEM,塞进请求头等于把私钥发出去。
         */
        res = await waitFor(() =>
          transport.signRequest !== undefined ? send({}) : send(authHeader(protocol, fresh.accessToken))
        )
      }
      httpStatus = res.status
      /*
        ★★ **额度快照在这里读,而且成功失败都读。**

        只在成功路径读的表现是:额度条永远停在 99%,因为**跑满之后的那一次请求
        必然失败** —— 而那一次的响应头里带的正是「已用 100%、几点重置」这组数,
        也就是唯一能让界面说清楚「为什么现在发不出去」的数据。

        ★ 整条路径自己吞异常:一次额度解析绝不能掀掉用户的对话(同
        `onUsageAttempt` 外面那圈 try/catch 的理由)。
      */
      this.captureQuota(res, account)

      if (!res.ok) {
        // 必须把 body 读完(或 cancel),否则连接不会被释放
        const text = await waitFor(() => res.text().catch(() => ''))
        /*
          ★ 原始 body 只在这一刻存在过。分类器认不出形状时给出的是一句
          「Upstream request failed (HTTP 400)」—— 上游到底抱怨什么一个字都不说。
          记下来,排查时不必让用户再复现一次。
        */
        /*
          ★ 连 URL 和模型一起记。只记供应商名的话,用户把这一行发过来时
          「打的哪个端点」仍然要靠猜 —— 而同一家供应商换个 baseUrl 就是完全不同的
          一条链路(GLM Coding Plan 那两家 coding / anthropic 两条端点的错误体形状
          都不一样,谁也认不出对方)。
        */
        this.host.logger.warn(
          `[upstream] ${c.provider.name} HTTP ${res.status} ${url} model=${c.alias.upstreamModel}`,
          text.slice(0, 2048)
        )
        let parsed: unknown = text
        try {
          parsed = JSON.parse(text)
        } catch {
          /* 非 JSON 的错误体(网关的 HTML 页)—— 原样交给分类器 */
        }
        const error = protocol === 'anthropic' ? anthropicErrorToAgentError(res.status, parsed, {
          cacheTtl,
          providerName: c.provider.name
        }) : openAIErrorToAgentError(res.status, parsed)
        const after = parseRetryAfter(res.headers.get('retry-after'), this.host.clock.now())
        if (after !== undefined) error.retryAfterMs = after
        return finish({ kind: 'failed', sawContent, error })
      }

      resetIdleTimeout()
      for await (const ev of abortableStream(decodeUpstream(protocol, res, signal), signal)) {
        if (isContent(ev) || ev.type === 'message_start') resetIdleTimeout()
        if (ev.type === 'error') {
          /*
            ★ 流内错误在这里留一条痕迹。界面上只看得到翻译过的那一句,而
            `error.message` 带着解码器给出的原始原因(`Invalid upstream response: …`)——
            用户把日志发过来时,这一行往往是唯一能定位到具体哪条协议检查的东西。
          */
          this.host.logger.warn(`[upstream] ${c.provider.name} 返回错误`, ev.error.code, ev.error.message)
          return finish({ kind: 'failed', sawContent, error: ev.error })
        }
        if (ev.type === 'message_start') responseModel = ev.model
        if (ev.type === 'message_end') {
          usage = ev.usage
          stopReason = ev.stopReason
        }
        if (ev.type === 'thinking_delta') thinkingText += ev.text
        if (ev.type === 'tool_call_start') toolCalls += 1
        if (
          timeToFirstTokenMs === null &&
          (ev.type === 'text_delta' ||
            ev.type === 'thinking_delta' ||
            ev.type === 'tool_call_start' ||
            ev.type === 'block_opaque')
        ) {
          timeToFirstTokenMs = Math.max(0, this.host.clock.now() - startedAt)
        }
        if (isContent(ev)) sawContent = true
        /*
          message_end 同理补上「这一次请求花了多久」。渲染层用它累出一轮的
          Σ 上游耗时,再除出平均 TPS —— 它必须在这里量,因为只有这一层知道
          请求是什么时候发出去的。

          ★ 和 `finish()` 里写进 usage_records 的 `latencyMs` 差几毫秒(那个量
          到流真正收完为止,这个量到本条消息收完为止)。刻意不为了对齐而把
          message_end 押后到循环结束再发 —— 那会让整条流多等一趟。
        */
        if (ev.type === 'message_end') {
          /*
            ★ 计价的时刻传 `startedAt`,**不是** now()。落盘那条走的是
            `finish()` 里的 `at: startedAt`,两处必须是同一个时刻 ——
            分时段计价的模型(DeepSeek 有夜间档)在跨过窗口边界的那一刻,
            两个数会不一样,而且只在凌晨那几分钟不一样,几乎不可能被测出来。

            ★ 传 `c.alias.upstreamModel` 而不是回包里的 `responseModel`,也是
            为了和落盘那条对齐:服务端会悄悄换名字,两边各查各的就会在同一轮上
            报出两个不同的金额(聊天页一个、设置页另一个)。
          */
          const cost = this.priceOfAttempt(c.provider.id, c.alias.upstreamModel, ev.usage, startedAt)
          yield {
            ...ev,
            latencyMs: Math.max(0, this.host.clock.now() - startedAt),
            ...(cost === undefined ? {} : { cost })
          }
          break
        }
        /*
          ★ 给 message_start 补上「这一段是谁给的」。这里是唯一的 choke point:
          三条解码路径(anthropic / openai-chat / openai-responses)都从这过,
          于是 decoder 一个都不用改 —— 它们本来也不知道自己在为哪家解码。
          抬头那行「Codex / gpt-5.6-sol」显示的就是这个值,故障切换真换了家时
          它跟着变,说的是既成事实而不是用户的意图。
        */
        yield ev.type === 'message_start' ? { ...ev, providerId: c.provider.id } : ev
      }

      if (stopReason === null) {
        return finish({ kind: 'failed', sawContent, error: interruptedResponse() })
      }
      this.recordSuccess(
        c.provider.id,
        this.host.clock.now() - startedAt,
        this.gateKeyFor(c.provider.id, account)
      )
      // 这个账号成功了 = 它的额度确实回来了,清掉落库的那条限流(见 `AccountPool`)
      if (account !== null) this.accounts?.reportSuccess(account)
      return finish({ kind: 'ok' })
    } catch (err) {
      if (parentSignal.aborted || (isAbortError(err) && !timedOut)) {
        finish({
          kind: 'failed',
          sawContent,
          error: agentError('aborted', '已中断', { retryable: false })
        })
        throw err
      }
      if (timedOut) {
        const seconds = Math.ceil(this.idleTimeoutMs() / 1000)
        return finish({
          kind: 'failed', sawContent,
          error: agentError('network', `No response progress from ${c.provider.name} for ${seconds} seconds.`, {
            retryable: false,
            messageKey: 'agent.error.upstreamTimeout',
            messageParams: { provider: c.provider.name, seconds }
          })
        })
      }
      if (err instanceof RequestPatchError || err instanceof ThinkingAdapterError) {
        return finish({
          kind: 'failed',
          sawContent,
          error: agentError('provider', err.message, { retryable: false })
        })
      }
      if (err instanceof ImageInputError) {
        return finish({ kind: 'failed', sawContent, error: err.error })
      }
      /*
        ★ 刷新凭证失败。`error` 已经在 resolver 里分好类了(网络故障 retryable、
        上游明确拒绝 not retryable)—— 这里原样用,不要在这一层重新猜一遍。
        `auth` 在 `FATAL_ERROR_CODES` 里,于是整个 run 会终止并让界面跳设置页。
      */
      if (err instanceof CredentialAuthError) {
        return finish({ kind: 'failed', sawContent, error: err.error })
      }
      return finish({
        kind: 'failed',
        sawContent,
        error: agentError('network', `连接「${c.provider.name}」失败:${(err as Error).message}`)
      })
    } finally {
      if (idleTimer !== undefined) clearTimeout(idleTimer)
      controller.abort()
    }
  }

  /**
   * 主入口。**重试放在这里,绝不放在 session 里** —— 放在 session 里会重放
   * 已经执行过的工具调用(方案 §5.3)。
   */
  async *stream(
    req: CanonicalRequest,
    signal: AbortSignal,
    context: UpstreamRequestContext
  ): AsyncGenerator<ProviderStreamEvent> {
    // `context` comes from the typed AgentSession boundary, but this router is
    // also used by the local gateway and by integrations that can cross a
    // JavaScript (rather than TypeScript) boundary. Treat malformed runtime
    // input as an ordinary local validation failure instead of throwing before
    // the generator can emit an error event.
    const workspaceId =
      typeof context?.workspaceId === 'string' ? context.workspaceId : ''
    if (
      workspaceId.trim() === '' ||
      // Anthropic specifies this limit in characters. JavaScript's
      // `String.length` counts UTF-16 code units, so use code points here;
      // production `ws_...` IDs are ASCII, but this keeps the boundary
      // correct for callers that provide a Unicode identifier in tests or
      // through an integration.
      [...workspaceId].length > ANTHROPIC_USER_ID_MAX
    ) {
      yield {
        type: 'error',
        error: agentError(
          'provider',
          `工作区标识必须是 1–${ANTHROPIC_USER_ID_MAX} 个字符，Anthropic 请求尚未发送。`,
          { retryable: false }
        )
      }
      return
    }

    const runContext: UpstreamRequestContext = {
      ...context,
      workspaceId,
      runId: context.runId ?? `run_${ulid(this.host.clock.now())}`,
      sessionId: context.sessionId ?? ''
    }

    const candidates = this.candidates(req.model, req.modelProviderId)
    if (candidates.length === 0) {
      yield { type: 'error', error: this.noCandidateError(req.model, req.modelProviderId) }
      return
    }

    let lastError: AgentError | undefined
    let authError: AgentError | undefined
    let attemptOrdinal = 0

    for (let ci = 0; ci < candidates.length; ci++) {
      const c = candidates[ci]
      if (c === undefined) continue

      if (ci > 0) {
        const prev = candidates[ci - 1]
        yield {
          type: 'provider_switch',
          from: prev?.provider.name ?? '?',
          to: c.provider.name,
          reason: lastError?.message ?? '上游不可用'
        }
      }

      // 见 `MAX_ACCOUNT_SWITCHES`:换号不消耗重试次数,所以要单独有个数管住它
      let accountSwitches = 0
      for (let attempt = 0; attempt < MAX_NETWORK_ATTEMPTS; attempt++) {
        /*
          这一次用哪个账号。**挑在发请求之前、闸门之前** —— 下面那次等待等的是
          这个账号的闸门,而不是"这家"的。

          ★★ 换号是**涌现**出来的,不是一条 if:上一轮失败时 `reportFailure` 把那个
          账号落了闸,于是这一轮 `select()` 自然返回下一个。写成显式的
          "switchAccount()" 会造出第二个真相来源 —— 和 `candidates()` 里
          「锁死语义整个由候选集表达」是同一条规矩。
        */
        const account = this.accounts?.select(c.provider.id) ?? null
        /*
          ★ 账号表非空但一个都挑不出来 = 这家的**全部账号**都在限流/停用/待重登。
          此时**绝不能**回落到 `provider.credentialRef`:那个旧槽里装的正是
          当前账号的镜像,用它发请求等于绕过刚刚立起来的闸门,必然再吃一个 429。
          交回外层候选循环(产品决策 D11),错误文案带上最早恢复时刻。
        */
        if (account === null && this.accounts?.hasAccounts(c.provider.id) === true) {
          lastError = this.allAccountsLimitedError(c.provider.name, c.provider.id)
          break
        }
        const gateKey = this.gateKeyFor(c.provider.id, account)
        /*
          ★ 别人刚在这一家上吃了 429 —— **在发请求之前**先把这次退避等掉。

          等待不消耗 `attempt`:被别的流连累而推迟,不该算这条流自己的重试次数,
          否则四个并发子代理里最后醒来的那个,机会已经被闸门吃光了。

          复用 `provider_retry` 事件而不是新造一个:界面上要说的话是同一句
          (「在等,因为上游限流」),而新增事件类型意味着 `stream.ts`、转录、
          `block-accumulator` 各改一遍,换不来任何新信息。
        */
        const gate = this.gateWaitFor(gateKey)
        if (gate.waitMs > 0) {
          yield { type: 'provider_retry', attempt: attempt + 1, delayMs: gate.waitMs, reason: gate.reason }
          await abortableSleep(gate.waitMs, signal)
        }

        attemptOrdinal += 1
        const outcome = yield* this.attempt(
          c,
          req,
          signal,
          runContext,
          attemptOrdinal,
          account
        )
        if (outcome.kind === 'ok') return

        // A cache compatibility error is a configuration mismatch, not an
        // unhealthy provider. Preserve the exact requested wire shape and
        // stop this logical request without retrying or switching candidates.
        if (outcome.error.code === 'cache_unsupported') {
          yield { type: 'error', error: outcome.error }
          return
        }

        this.recordFailure(c.provider.id, outcome.error)
        lastError = outcome.error
        if (outcome.error.code === 'auth' && authError === undefined) authError = outcome.error

        /*
          ★ 退避时长在这里就算出来,**早于**下面那两个 return/break —— 因为闸门
          要在「这条流自己还重不重试」之前立起来。这次尝试是这条流的最后一次
          (或者内容已经吐出去了、只能放弃),对**其它并发流**来说配额照样是空的,
          那一句「等一下再发」依旧成立。
        */
        const delayMs = this.retryDelayFor(outcome.error, attempt)
        if (outcome.error.code === 'rate_limit') {
          this.rateLimitGate.set(gateKey, {
            until: this.host.clock.now() + delayMs,
            reason: outcome.error.message
          })
        }

        /*
          账号级落闸(**落库**,重启后仍然有效)+ 还有没有下一个账号可用。
          ★ 和上面那个进程内闸门是两回事:那个管"这几秒别再发",量级是秒;
          这个管"这个账号这一轮额度没了",量级是分钟到小时(见 `AccountPool`)。
        */
        const nextAccount =
          account === null ? null : (this.accounts?.reportFailure(account, outcome.error) ?? null)

        /**
         * ★ §5.3 的那条边界:**只能在收到第一个内容字节之前切换或重试**。
         *
         * 一旦上游已经吐了 500 个 token 和一个 tool_use 块,换个 provider 重来
         * 就会产生重复输出和错位的工具调用。之后的失败是硬错误,由用户决定重发。
         * 这五行事后补要重写整个流式路径。
         */
        if (outcome.sawContent) {
          yield { type: 'error', error: outcome.error }
          return
        }

        /*
          ★★ **换号不消耗重试次数。** 刚才那次失败是"那个账号的额度没了",
          不是"这次请求不行" —— 拿一个全新的账号重发本质上是第一次尝试。
          算进 `attempt` 的话,三个账号的用户实际只能用到前两个
          (第三次尝试时次数已经用光),而界面上第三个账号一直显示可用。

          ★ 循环上限 `MAX_NETWORK_ATTEMPTS` 仍然兜着底:账号再多也不会无限换,
          因为每换一次都会先给上一个落闸,`select()` 不会再挑到它。
        */
        if (nextAccount !== null && accountSwitches < MAX_ACCOUNT_SWITCHES) {
          accountSwitches += 1
          attempt -= 1
          continue
        }

        // network 借用更宽的上限,其余可重试错误(限流、5xx …)维持原来的 3 次
        const attemptLimit = outcome.error.code === 'network' ? MAX_NETWORK_ATTEMPTS : MAX_ATTEMPTS
        const canRetry = outcome.error.retryable && attempt < attemptLimit - 1
        if (!canRetry) break

        // 没有这条事件,用户看到的就是白白冻结 30 秒(方案 §4.2)
        yield { type: 'provider_retry', attempt: attempt + 1, delayMs, reason: outcome.error.message }
        await abortableSleep(delayMs, signal)
      }
    }

    // auth 比「最后一个网络错误」更值得展示:它有明确的行动(去设置页填密钥),
    // 而一个 network 错误只会让用户干瞪眼重试
    yield { type: 'error', error: authError ?? lastError ?? agentError('unknown', '上游请求失败') }
  }

  /**
   * 把这次响应里的额度快照记到账号上(今天只有 Codex 有这组头)。
   *
   * ★ 非 chatgpt 的账号、没有账号表的供应商:一个字节都不做,连头都不读。
   * ★ 解析不出来时**什么都不写** —— 写一个空快照会把界面上那句
   *   「尚未获取,发一条消息后更新」变成一条 0% 的进度条,而那是一句假话。
   */
  private captureQuota(res: Response, account: ProviderAccount | null): void {
    if (account === null || account.issuer !== 'chatgpt') return
    if (this.accounts === undefined) return
    try {
      const snapshot = parseCodexQuota(headerReaderOf(res.headers), this.host.clock.now())
      if (snapshot === null) {
        /*
          ★★ 计划 §11 第 1 条:头名**还没有拿真实响应验证过**。解析不出来和
          「上游根本没给」在日志里长得一样,所以把看到的 `x-codex-*` 原样记一行 ——
          这是把那组候选名收敛成实测结论的唯一入口。
          debug 级:正常用户看不到,排查时开日志就有。
        */
        const seen = codexQuotaHeaderNames(res.headers)
        if (seen.length > 0) {
          this.host.logger.debug('[codex-quota] 认不出这组头,请补进 codex-quota.ts', seen.join(' '))
        }
        return
      }
      this.accounts.reportQuota(account, snapshot)
    } catch (error) {
      this.host.logger.warn('[codex-quota] 解析额度失败,请求本身不受影响', error)
    }
  }

  /**
   * 这家的全部账号都用不了。
   *
   * ★★ **必须给得出「最早什么时候能再用」。** 没有这个数的话,用户看到的是
   * 一句「全部账号都在限流中」,而他完全不知道该等 5 分钟还是 5 小时 ——
   * 于是他会反复点重发,每一次都在给冷却期续命。
   *
   * ★ `retryable: true`:限流会自己过去,界面上那颗「重试」按钮是有意义的。
   * 账号全被停用/待重新登录时(`earliestRecoveryAt` 为 null)则不是 —— 那要人去动手。
   *
   * ★ 文案走 `messageKey`,时刻以**时间戳**下发:格式化成「14:30」是渲染层的事
   * (那边才知道用户的 locale 和时区)。在这里拼一个中文时间串,英文界面上会出现半句中文。
   */
  private allAccountsLimitedError(providerName: string, providerId: string): AgentError {
    const recoveryAt = this.accounts?.earliestRecoveryAt(providerId) ?? null
    if (recoveryAt === null) {
      return agentError(
        'no_healthy_provider',
        `供应商「${providerName}」的账号都不可用,请检查账号是否被停用或需要重新登录。`,
        {
          retryable: false,
          messageKey: 'agent.error.allAccountsUnusable',
          messageParams: { provider: providerName }
        }
      )
    }
    return agentError(
      'rate_limit',
      `供应商「${providerName}」的全部账号都在限流中,最早恢复时间已在设置页显示。`,
      {
        retryable: true,
        retryAfterMs: Math.max(0, recoveryAt - this.host.clock.now()),
        messageKey: 'agent.error.allAccountsRateLimited',
        messageParams: { provider: providerName, recoveryAt }
      }
    )
  }

  /**
   * ★ 钉住时的三条分支都必须**明说「不会自动切到其它供应商」**。
   * 用户对这个应用的既有心智是「配了多家就会自动兜底」,不写这句,他看到
   * 「Codex 已停用」只会理解成「切换也挂了」,然后去查网络而不是去启用那一家。
   */
  private noCandidateError(model: string, modelProviderId?: string): AgentError {
    if (modelProviderId !== undefined) {
      const provider = this.config.providers().find((p) => p.id === modelProviderId)
      if (provider === undefined) {
        return agentError('no_healthy_provider',
          `你选择的供应商已被删除,「${model}」不会自动切到其它供应商。请在模型菜单里重新选择。`,
          { retryable: false, messageKey: 'agent.error.pinnedProviderMissing', messageParams: { model } })
      }
      if (!provider.enabled) {
        return agentError('no_healthy_provider',
          `供应商「${provider.name}」已停用,「${model}」不会自动切到其它供应商。请启用它,或另选一个模型。`,
          { retryable: false, messageKey: 'agent.error.pinnedProviderDisabled',
            messageParams: { model, provider: provider.name } })
      }
      return agentError('no_healthy_provider',
        `供应商「${provider.name}」下已经没有可用的模型「${model}」,请重新选择。`,
        { retryable: false, messageKey: 'agent.error.pinnedModelMissing',
          messageParams: { model, provider: provider.name } })
    }
    const anyAlias = this.config.aliases().some((a) => a.alias === model)
    if (!anyAlias) {
      return agentError('no_healthy_provider', `没有配置模型别名「${model}」`, { retryable: false })
    }
    const cooling = this.config
      .aliases()
      .filter((a) => a.alias === model)
      .some((a) => (this.healthMap.get(a.providerId)?.cooldownUntil ?? 0) > this.host.clock.now())
    return agentError(
      'no_healthy_provider',
      cooling
        ? `「${model}」的所有供应商都在冷却中,稍后自动恢复`
        : `「${model}」没有已启用的供应商`,
      { retryable: cooling }
    )
  }
}
