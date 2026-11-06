import { THINKING_BUDGET, type ThinkingLevel } from '../agent/run-request'
import { resolveModelThinking, type ResolvedModelThinking } from './model-runtime'
import { adaptiveAnthropicBinding } from './model-catalog-inventory/vendors/anthropic'
import type { ReasoningEffort, ReasoningReplay, RequestAdapterConfig, ThinkingConfig, UpstreamProtocol } from './provider'

export class ThinkingAdapterError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'ThinkingAdapterError'
  }
}

export interface ThinkingAdapterInput {
  protocol: UpstreamProtocol
  upstreamModel: string
  config: ThinkingConfig | undefined
  reasoning: ResolvedModelThinking | undefined
  maxOutputTokens: number
  preset?: RequestAdapterConfig['preset']
  reasoningEfforts?: readonly ReasoningEffort[]
  /** Original UI selection, before a legacy token budget was clamped. */
  thinkingLevel?: ThinkingLevel
}

type AdapterKind = 'anthropic' | 'openai-chat' | 'openai-responses' | 'deepseek' | 'glm' | 'hunyuan' | 'custom'

const THINKING_ROOTS = [
  'thinking',
  'reasoning',
  'reasoning_effort',
  'enable_thinking',
  'thinking_budget',
  'thinking_mode',
  'reasoning_split',
  'chat_template_kwargs',
  'thinkingConfig',
] as const

const CUSTOM_PATHS = new Set([
  'thinking',
  'thinking.enabled',
  'thinking.type',
  'thinking.budget_tokens',
  'thinkingConfig.thinkingLevel',
  'chat_template_kwargs.enable_thinking',
  'chat_template_kwargs.reasoning_effort',
  'reasoning',
  'reasoning.effort',
  'reasoning_effort',
  'reasoning_split',
  'enable_thinking',
  'thinking_budget',
  'thinking_mode',
])

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function cloneBody(body: unknown): Record<string, unknown> {
  if (!isRecord(body)) throw new ThinkingAdapterError('Think 适配要求请求体是 JSON 对象。')
  return structuredClone(body)
}

function stripThinking(body: Record<string, unknown>): void {
  for (const key of THINKING_ROOTS) delete body[key]
}

/**
 * 模型名里带的厂商族。抽出来是给 `kindFor` 与 `reasoningReplayFor` **共用同一份正则** ——
 * 两处各抄一份,迟早只改一处,表现是请求线形按一家走、历史思考的回传按另一家走。
 */
function nameFamily(upstreamModel: string): 'deepseek' | 'glm' | 'hunyuan' | undefined {
  if (/(?:^|\/)(?:deepseek|deep-seek)[/:._-]/iu.test(upstreamModel)) return 'deepseek'
  if (/(?:^|\/)(?:glm|chatglm)[/:._-]/iu.test(upstreamModel)) return 'glm'
  if (/(?:^|\/)(?:hy4-preview|hy3(?:-preview)?)(?:$|[/:._-])/iu.test(upstreamModel)) return 'hunyuan'
  return undefined
}

function kindFor(input: ThinkingAdapterInput): AdapterKind {
  if (input.config?.anthropicAdaptive === true && input.preset === 'custom') return input.protocol
  switch (input.preset) {
    case 'anthropic':
      return 'anthropic'
    case 'openai-chat':
      return 'openai-chat'
    case 'openai-responses':
      return 'openai-responses'
    case 'custom':
      return 'custom'
    default:
      break
  }
  if (input.protocol === 'anthropic') return 'anthropic'
  if (input.config?.anthropicAdaptive === true) return input.protocol
  /*
   * ★★ standardWire(见 provider.ts 的 ThinkingConfig):条目声明这家读标准线形时,
   * 不套按模型名的厂商方言 —— Ollama 托管的 deepseek/glm 走这里。注意必须放在
   * 名字启发式**之前**:官方 DeepSeek 的条目也声明 reasoning_effort 路径,靠的
   * 就是名字启发式补上它家要的 thinking:{type},顺序反了会改掉官方渠道的行为。
   */
  if (input.config?.standardWire === true) return input.protocol
  const family = nameFamily(input.upstreamModel)
  if (family !== undefined) return family
  if (
    input.config?.parameterPath !== undefined &&
    !['reasoning_effort', 'reasoning.effort'].includes(input.config.parameterPath)
  )
    return 'custom'
  return input.protocol
}

/**
 * 这次请求该按哪种方言回传历史思考(语义见 provider.ts 的 `ThinkingConfig.reasoningReplay`)。
 *
 * 需求:官方 OpenAI 的 `input` 里 reasoning item 的 `content` 上限是 0,带非空正文的
 * item 会让整轮 400(`array_above_max_length`);DeepSeek 思考模式反过来要求正文全文
 * 回传,缺了同样 400。两边互斥,所以这个判定必须发生在**知道上游是谁**的这一层,
 * 编码器只收结论(它看不到供应商,那是路由器那一层的事实)。
 *
 * 不满足会怎样:出事的 item 埋在转录里、每轮都被重放,表现是**会话从某一轮起每轮都
 * 发不出去,`input[N]` 的 N 随历史长度漂移,且本地零报错**。
 *
 * 顺序照 `standardWire` 自己的先例:显式声明 > 声明读标准线形的托管方 > 模型名兜底。
 */
export function reasoningReplayFor(
  config: ThinkingConfig | undefined,
  upstreamModel: string,
): ReasoningReplay {
  if (config?.reasoningReplay !== undefined) return config.reasoningReplay
  // Ollama 这类托管方跑的是 deepseek-* 名字的模型,但读的是标准线形 —— 回传同样按
  // 标准约束走,不能被下面的名字启发式抓回 DeepSeek 官方方言。
  if (config?.standardWire === true) return 'opaque-only'
  return nameFamily(upstreamModel) === 'deepseek' ? 'text-required' : 'opaque-only'
}

function budgetForEffort(
  effort: NonNullable<ResolvedModelThinking['effort']> | undefined,
  maxOutputTokens: number,
): number {
  const requested =
    effort === undefined
      ? THINKING_BUDGET.medium
      : effort === 'none'
        ? 0
        : effort === 'xhigh'
          ? THINKING_BUDGET.higher
          : THINKING_BUDGET[effort]
  const ceiling = Math.max(1024, Math.floor(maxOutputTokens) - 1024)
  return Math.min(requested, ceiling)
}

function effortForBudget(tokens: number | undefined): NonNullable<ResolvedModelThinking['effort']> {
  if (tokens === undefined || tokens <= THINKING_BUDGET.minimal) return 'minimal'
  if (tokens <= THINKING_BUDGET.low) return 'low'
  if (tokens <= THINKING_BUDGET.medium) return 'medium'
  if (tokens <= THINKING_BUDGET.high) return 'high'
  return 'max'
}

function customValue(config: ThinkingConfig, reasoning: ResolvedModelThinking, path: string): unknown {
  if (config.mode === 'effort') {
    if (!reasoning.enabled) return config.disabledValue ?? config.effortMap?.none
    if (reasoning.effort === undefined) {
      throw new ThinkingAdapterError(`Think effort 适配缺少推理强度: ${path}`)
    }
    return config.effortMap?.[reasoning.effort] ?? reasoning.effort
  }
  if (config.mode === 'budget') {
    const leaf = path.split('.').at(-1)
    if (
      leaf === 'enable_thinking' ||
      leaf === 'thinking_mode' ||
      path === 'thinking.enabled' ||
      path === 'reasoning_split'
    ) {
      throw new ThinkingAdapterError(`Token budget 不能写入开关参数: ${path}`)
    }
    if (!reasoning.enabled) return undefined
    if (reasoning.budgetTokens === undefined) {
      throw new ThinkingAdapterError(`Think budget 适配缺少 Token 预算: ${path}`)
    }
    return reasoning.budgetTokens
  }
  if (config.mode === 'toggle' && (path === 'thinking_budget' || path.endsWith('.budget_tokens'))) {
    throw new ThinkingAdapterError(`Think 开关不能写入 Token budget 参数: ${path}`)
  }
  if (reasoning.enabled && config.enabledValue !== undefined) return config.enabledValue
  if (!reasoning.enabled && config.disabledValue !== undefined) return config.disabledValue
  if (path.endsWith('.type')) return reasoning.enabled ? 'enabled' : 'disabled'
  if (path === 'thinking') return { type: reasoning.enabled ? 'enabled' : 'disabled' }
  return reasoning.enabled
}

function setDotted(body: Record<string, unknown>, path: string, value: unknown): void {
  if (!CUSTOM_PATHS.has(path)) {
    throw new ThinkingAdapterError(`Think 参数路径不在允许的白名单中: ${path}`)
  }
  const parts = path.split('.')
  let cursor = body
  for (const part of parts.slice(0, -1)) {
    const current = cursor[part]
    if (current === undefined) {
      if (value === undefined) return
      const next: Record<string, unknown> = {}
      cursor[part] = next
      cursor = next
      continue
    }
    if (!isRecord(current)) {
      throw new ThinkingAdapterError(`Think 参数父路径不是对象: ${path}`)
    }
    cursor = current
  }
  const leaf = parts.at(-1)
  if (leaf === undefined) throw new ThinkingAdapterError('Think 参数路径不能为空。')
  if (value === undefined) delete cursor[leaf]
  else cursor[leaf] = value
}

/**
 * Convert provider-neutral reasoning into a concrete request-body shape.
 * Unsupported declarations remove every known Think field, even when a
 * legacy encoder or stale request supplied one.
 */
function adaptiveInput(input: ThinkingAdapterInput): ThinkingAdapterInput {
  const projected = adaptiveAnthropicBinding(
    input.upstreamModel, input.config, input.protocol, input.preset, input.reasoningEfforts
  )
  if (projected === undefined) return input
  const effective = projected.thinkingConfig!
  const efforts = projected.reasoningEfforts!
  const config = input.config!
  let reasoning = input.reasoning
  if (input.thinkingLevel !== undefined) {
    // Budget-mode Minimal has no adaptive equivalent; use the lowest effort.
    const level = config.mode === 'budget' && input.thinkingLevel === 'minimal' ? 'low' : input.thinkingLevel
    reasoning = resolveModelThinking(level, effective, input.maxOutputTokens, efforts)
  } else if (reasoning !== undefined && !reasoning.explicit) {
    reasoning = resolveModelThinking('auto', effective, input.maxOutputTokens, efforts)
  }
  // An Off-only binding needs no enabled effort, but still requires confirmed Off support.
  if (reasoning?.enabled === false && efforts.includes('none')) {
    return { ...input, config: effective, reasoning, reasoningEfforts: efforts }
  }
  const defaultEffort = effective.defaultEffort
  if (defaultEffort === undefined || !efforts.includes(defaultEffort) || efforts.every((effort) => effort === 'none')) {
    throw new ThinkingAdapterError('Anthropic adaptive thinking 缺少可用推理强度。')
  }
  if (input.thinkingLevel === undefined && reasoning !== undefined) {
    if (reasoning.enabled && reasoning.effort === undefined) {
      // Budget-only legacy API callers have no UI selection. This conversion
      // is used only there; modern calls preserve the original level above.
      const tokens = reasoning.budgetTokens
      const wanted = tokens === undefined ? defaultEffort : tokens <= THINKING_BUDGET.low ? 'low' :
        tokens <= THINKING_BUDGET.medium ? 'medium' : tokens <= THINKING_BUDGET.high ? 'high' :
          tokens <= THINKING_BUDGET.higher ? 'xhigh' : 'max'
      const order: readonly ReasoningEffort[] = ['low', 'medium', 'high', 'xhigh', 'max']
      const supported = order.filter((effort) => efforts.includes(effort))
      const weaker = supported.filter((effort) => order.indexOf(effort) <= order.indexOf(wanted))
      reasoning = { ...reasoning, mode: 'effort', effort: weaker.at(-1) ?? supported[0] ?? defaultEffort }
    }
  }
  return { ...input, config: effective, reasoning, reasoningEfforts: efforts }
}

export function applyThinkingAdapter(body: unknown, input: ThinkingAdapterInput): unknown {
  input = adaptiveInput(input)
  if (input.config === undefined) return body
  const next = cloneBody(body)

  if (input.config.mode === 'unsupported') {
    stripThinking(next)
    return next
  }

  const reasoning = input.reasoning
  if (input.config.mode === 'always') {
    // The model id itself selects a reasoning-only model. Sending a vendor
    // switch is both redundant and rejected by several reasoning-only APIs.
    stripThinking(next)
    return next
  }
  if (reasoning === undefined) {
    throw new ThinkingAdapterError('模型声明支持 Think，但请求缺少已解析的 Think 配置。')
  }

  const kind = kindFor(input)
  if (
    reasoning.enabled &&
    reasoning.effort !== undefined &&
    input.reasoningEfforts !== undefined &&
    !input.reasoningEfforts.includes(reasoning.effort)
  ) {
    throw new ThinkingAdapterError(`模型不支持推理强度「${reasoning.effort}」。`)
  }
  if (
    !reasoning.enabled &&
    reasoning.explicit &&
    input.config.mode === 'effort' &&
    input.reasoningEfforts !== undefined &&
    !input.reasoningEfforts.includes('none')
  ) {
    throw new ThinkingAdapterError('该模型不支持关闭推理，不能把 Off 静默还原成默认强度。')
  }
  if (kind === 'anthropic') {
    delete next['reasoning']
    delete next['reasoning_effort']
    delete next['enable_thinking']
    delete next['thinking_budget']
    if (!reasoning.enabled) {
      // Compatible relays may default to thinking even when Anthropic itself
      // does not. Omitting the field does not express the user's Off choice.
      if (input.config.anthropicAdaptive === true) {
        if (input.reasoningEfforts === undefined || !input.reasoningEfforts.includes('none')) {
          throw new ThinkingAdapterError('未确认该模型支持关闭 Anthropic adaptive thinking。')
        }
        // Omitting thinking enables it on newer models. Clear stale high effort
        // so conditional Off (Opus 5 / Haiku 5.5) stays at a supported level.
        next['thinking'] = { type: 'disabled' }
        const outputConfig = next['output_config']
        if (isRecord(outputConfig)) {
          delete outputConfig['effort']
          if (Object.keys(outputConfig).length === 0) delete next['output_config']
        }
        return next
      }
      next['thinking'] = { type: 'disabled' }
      return next
    }
    /*
     * ★★ standardWire 的 Anthropic 线形(Ollama):档位走 `output_config.effort`,
     * 而且**绝不能同时发 `thinking.type`** —— Ollama 源码里 output_config 那一支
     * 挂在 `think == nil` 上(它先处理 thinking,只在 think 仍为空时才看 output_config),
     * 两个一起发的表现是 effort 被静默无视、永远停在默认档。这也解释了关的时候
     * 为什么仍写 `thinking:{type:'disabled'}`:那条路上 output_config 只能
     * 「开 + 定档」,表达不了「关」。
     */
    if (input.config.anthropicAdaptive === true) {
      const effort = reasoning.effort
      if (effort === undefined) throw new ThinkingAdapterError('Anthropic adaptive thinking 缺少推理强度。')
      if (effort === 'none' || effort === 'minimal') {
        throw new ThinkingAdapterError(`Anthropic adaptive thinking 不支持推理强度「${effort}」。`)
      }
      next['thinking'] = { type: 'adaptive' }
      next['output_config'] = {
        ...(isRecord(next['output_config']) ? next['output_config'] : {}),
        effort
      }
      return next
    }
    if (input.config.standardWire === true && reasoning.effort !== undefined) {
      next['output_config'] = { effort: reasoning.effort }
      return next
    }
    const budget = reasoning.budgetTokens ?? budgetForEffort(reasoning.effort, input.maxOutputTokens)
    next['thinking'] = { type: 'enabled', budget_tokens: budget }
    return next
  }

  if (kind === 'openai-chat' || kind === 'openai-responses') {
    delete next['thinking']
    delete next['enable_thinking']
    delete next['thinking_budget']
    delete next['reasoning']
    delete next['reasoning_effort']
    if (!reasoning.enabled) {
      // Omitting effort lets current OpenAI reasoning models fall back to
      // their default (commonly medium), which violates an explicit Off.
      if (reasoning.explicit) {
        if (input.reasoningEfforts !== undefined && !input.reasoningEfforts.includes('none')) {
          throw new ThinkingAdapterError('该模型不支持关闭推理，不能把 Off 静默还原成默认强度。')
        }
        const off = input.config.effortMap?.none ?? 'none'
        if (kind === 'openai-responses') next['reasoning'] = { effort: off }
        else next['reasoning_effort'] = off
      }
      return next
    }
    const effort = reasoning.effort ?? effortForBudget(reasoning.budgetTokens)
    const mappedEffort = input.config.effortMap?.[effort] ?? effort
    if (kind === 'openai-responses') next['reasoning'] = { effort: mappedEffort }
    else next['reasoning_effort'] = mappedEffort
    return next
  }

  if (kind === 'deepseek') {
    delete next['reasoning']
    delete next['reasoning_effort']
    delete next['enable_thinking']
    delete next['thinking_budget']
    const thinking: Record<string, unknown> = {
      type: reasoning.enabled ? 'enabled' : 'disabled',
    }
    // DeepSeek's current OpenAI-compatible API uses a separate flat effort
    // field. It does not document `thinking.budget_tokens` as a request input.
    if (reasoning.enabled && input.config.mode === 'effort') {
      const effort = reasoning.effort ?? effortForBudget(reasoning.budgetTokens)
      next['reasoning_effort'] = input.config.effortMap?.[effort] ?? effort
    }
    next['thinking'] = thinking
    return next
  }

  if (kind === 'glm' || kind === 'hunyuan') {
    delete next['reasoning']
    delete next['reasoning_effort']
    delete next['enable_thinking']
    delete next['thinking_budget']
    next['thinking'] = { type: reasoning.enabled ? 'enabled' : 'disabled' }
    if (reasoning.enabled && input.config.mode === 'effort') {
      const effort = reasoning.effort ?? effortForBudget(reasoning.budgetTokens)
      next['reasoning_effort'] = input.config.effortMap?.[effort] ?? effort
    }
    return next
  }

  const path = input.config.parameterPath?.trim()
  if (path === undefined || path === '') {
    throw new ThinkingAdapterError('自定义 Think 适配必须配置参数路径。')
  }
  stripThinking(next)
  setDotted(next, path, customValue(input.config, reasoning, path))
  return next
}

/**
 * Final guard used after JSON Patch for models that must not receive a wire
 * switch. `always` models select reasoning through the model id itself, while
 * `unsupported` models reject every Think parameter. Also sanitize the common
 * `extra_body` passthrough container so a broad container patch cannot restore
 * a field removed at the top level.
 */
export function removeUnsupportedThinking(body: unknown, config: ThinkingConfig | undefined): unknown {
  if (config?.mode !== 'unsupported' && config?.mode !== 'always') return body
  const next = cloneBody(body)
  stripThinking(next)
  const extraBody = next['extra_body']
  if (isRecord(extraBody)) stripThinking(extraBody)
  return next
}

/** An explicit conversation Off takes precedence over saved model patches. */
export function enforceThinkingPreference(body: unknown, input: ThinkingAdapterInput): unknown {
  input = adaptiveInput(input)
  const guarded = removeUnsupportedThinking(body, input.config)
  if (input.config === undefined || input.config.mode === 'unsupported' || input.config.mode === 'always'
    || input.reasoning?.explicit !== true || input.reasoning.enabled) return guarded
  const next = cloneBody(guarded)
  stripThinking(next)
  if (isRecord(next['extra_body'])) stripThinking(next['extra_body'])
  return applyThinkingAdapter(next, input)
}
