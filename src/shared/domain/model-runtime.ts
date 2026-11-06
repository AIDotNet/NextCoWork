import type { AgentMessage } from '../agent/message'
import {
  THINKING_BUDGET,
  THINKING_LEVELS,
  THINKING_STRENGTH,
  type ThinkingLevel
} from '../agent/run-request'
import type { ModelAlias, ModelCapabilities, ReasoningEffort, ThinkingConfig } from './provider'
import { adaptiveAnthropicBinding } from './model-catalog-inventory/vendors/anthropic'

export type ModelReasoningEffort = NonNullable<ThinkingConfig['defaultEffort']>

/**
 * Provider-neutral result of applying the conversation-level thinking choice
 * to one model's declaration.  Wire adapters consume this object; they never
 * need to guess what `auto`, `higher`, or a token budget means.
 */
export interface ResolvedModelThinking {
  mode: Exclude<ThinkingConfig['mode'], 'unsupported'>
  enabled: boolean
  /** True when the user explicitly chose `off` or a non-auto strength. */
  explicit: boolean
  effort?: ModelReasoningEffort
  budgetTokens?: number
}

const MIN_THINKING_BUDGET = 1024
const MIN_OUTPUT_HEADROOM = 1024

type ThinkingModel = Pick<ModelAlias, 'thinkingConfig' | 'reasoningEfforts' | 'capabilities'> &
  Partial<Pick<ModelAlias, 'upstreamModel' | 'runtimeProtocol' | 'protocolOverride' | 'requestAdapter'>>

/** Read-only thinking declaration matching the wire adapter, including legacy budgets. */
export function effectiveModelThinking(model: ThinkingModel): Pick<ModelAlias, 'thinkingConfig' | 'reasoningEfforts'> {
  return adaptiveAnthropicBinding(
    model.upstreamModel ?? '', model.thinkingConfig,
    model.runtimeProtocol ?? model.protocolOverride, model.requestAdapter?.preset, model.reasoningEfforts
  ) ?? model
}

/** UI choices describe the selected binding, including provider overrides. */
export function modelThinkingLevels(model: ThinkingModel | undefined): readonly ThinkingLevel[] {
  if (model === undefined) return ['auto']
  const effective = effectiveModelThinking(model)
  const mode = effective.thinkingConfig?.mode
  if (mode === 'unsupported' || mode === 'always') return ['auto']
  if (mode === 'toggle') return ['auto', 'medium', 'off'] // medium is the legacy enabled value; UI labels it On.
  if (mode === 'effort') {
    const efforts = effective.reasoningEfforts
    if (efforts === undefined) return THINKING_LEVELS
    return THINKING_LEVELS.filter((level) => level === 'auto' || efforts.includes(
      level === 'off' ? 'none' : level === 'higher' ? 'xhigh' : level
    ))
  }
  if (mode === 'budget' || model.capabilities.thinking) return THINKING_LEVELS
  // Unknown legacy imports can still explicitly disable protocol defaults.
  return ['auto', 'off']
}

export function normalizeModelThinkingLevel(level: ThinkingLevel, model: ThinkingModel | undefined): ThinkingLevel {
  const levels = modelThinkingLevels(model)
  return levels.includes(level) ? level : 'auto'
}

/**
 * 旁路请求(上下文压缩 / 目标判定 / 权限审核 / 会话标题)这一次该用哪个档位。
 *
 * 需求:这些请求要么想省钱关掉思考、要么想跟随会话档位,但**都不能因为档位不可用而失败**。
 * 在这个函数出现之前,压缩硬发 `'off'`,而 `gpt-6-*` 这类 effort 模型的 `reasoningEfforts`
 * 不含 `'none'` —— `thinking-adapter.ts` 会直接抛「该模型不支持关闭推理」,router 把它包成
 * 不可重试的错。表现是:压缩每一次都失败,三次后熔断,此后整个 run 再也不压缩,
 * 上下文一路涨到上游报超长,而界面上一个报错都没有。
 *
 * 规则:想要的档位可用就用它;不可用就在这个模型**支持的档位**里取「不超过它的最强一档」;
 * 模型最低的那一档都比它强时取那一档(`'off'` 关不掉的模型走的就是这一支);再没有就 `'auto'`。
 *
 * ★ 与 `normalizeModelThinkingLevel` 的差别是**故意的**,不要合并成一个:
 *   那个服务于界面上换模型(落回 `'auto'` = 「交给模型自己定」,是用户看得见的中性结果);
 *   这个服务于后台请求(没有界面可看,落回 `'auto'` 会让一次「尽量别思考」的短请求
 *   悄悄按模型默认的高档位去想,而账单上才看得出来)。
 * ★ 绝不抛,绝不返回一个这个模型不支持的档位。
 */
export function auxiliaryThinkingLevel(wanted: ThinkingLevel, model: ThinkingModel | undefined): ThinkingLevel {
  const levels = modelThinkingLevels(model)
  if (levels.includes(wanted)) return wanted
  // `'auto'` 不在强度轴上,没有「更弱的一档」可退;而它恒在 `modelThinkingLevels` 里,
  // 所以走到这里的 wanted 一定是个强度档。
  const wantedAt = THINKING_STRENGTH.indexOf(wanted)
  if (wantedAt < 0) return 'auto'
  const available = THINKING_STRENGTH
    .map((level, index): { level: ThinkingLevel; index: number } => ({ level, index }))
    .filter((entry) => levels.includes(entry.level))
  const weaker = available.filter((entry) => entry.index <= wantedAt)
  // 一个都不比它弱(模型最低档也比要的强)时取最低的那一档 —— 宁可多想一点,也不能让请求失败。
  const picked = weaker.length > 0 ? weaker[weaker.length - 1] : available[0]
  return picked?.level ?? 'auto'
}

/**
 * 这个模型的一次旁路请求能不能**完全不思考**:要么能关(`'off'` 可用),要么它根本不支持思考。
 *
 * 需求:输入框补全只要一行字,输出预算按「不思考」定(几十个 token)。关不掉思考的模型
 * (`always`、effort 轴上没有 `'none'` 的)会先把预算花在推理上,回来的是空答案 ——
 * 调用方据此要么放宽预算,要么干脆不发。
 */
export function canDisableThinking(model: ThinkingModel | undefined): boolean {
  if (model === undefined) return false
  return auxiliaryThinkingLevel('off', model) === 'off'
    || effectiveModelThinking(model).thinkingConfig?.mode === 'unsupported'
}

function effortFor(level: ThinkingLevel, fallback: ModelReasoningEffort): ModelReasoningEffort {
  switch (level) {
    case 'minimal':
    case 'low':
    case 'medium':
    case 'high':
    case 'max':
      return level
    case 'higher':
      return 'xhigh'
    default:
      return fallback
  }
}

function budgetFor(level: ThinkingLevel, fallback: number): number {
  if (level === 'auto') return fallback
  if (level === 'off') return 0
  return THINKING_BUDGET[level]
}

function clampBudget(wanted: number, maxOutputTokens: number): number | undefined {
  const ceiling = Math.floor(maxOutputTokens) - MIN_OUTPUT_HEADROOM
  if (ceiling < MIN_THINKING_BUDGET) return undefined
  return Math.max(MIN_THINKING_BUDGET, Math.min(Math.floor(wanted), ceiling))
}

/**
 * Resolve model defaults and a per-run choice without leaking provider field
 * names into the AgentSession. Unsupported models deliberately return
 * `undefined`, which guarantees that no reasoning field is emitted later.
 */
export function resolveModelThinking(
  level: ThinkingLevel,
  config: ThinkingConfig | undefined,
  maxOutputTokens: number,
  reasoningEfforts?: readonly ReasoningEffort[]
): ResolvedModelThinking | undefined {
  if (config === undefined || config.mode === 'unsupported') return undefined

  const explicit = level !== 'auto'
  const defaultEffort = config.defaultEffort !== undefined &&
    (reasoningEfforts === undefined || reasoningEfforts.includes(config.defaultEffort))
    ? config.defaultEffort : reasoningEfforts?.find((effort) => effort !== 'none') ?? 'medium'
  const enabled = config.mode === 'always'
    ? true
    : level === 'off'
      ? false
      : level === 'auto'
        ? config.defaultEnabled && !(config.mode === 'effort' && defaultEffort === 'none')
        : true

  const base: ResolvedModelThinking = { mode: config.mode, enabled, explicit }
  if (!enabled || config.mode === 'always') return base

  if (config.mode === 'effort') {
    return {
      ...base,
      effort: effortFor(level, defaultEffort)
    }
  }

  // Budget is also useful to adapters such as Anthropic when a catalogue row
  // describes a simple toggle rather than exposing a budget in the UI.
  if (config.mode === 'budget' || config.mode === 'toggle') {
    const wanted = budgetFor(
      config.mode === 'toggle' ? 'auto' : level,
      config.defaultBudgetTokens ?? THINKING_BUDGET.medium
    )
    const budgetTokens = clampBudget(wanted, maxOutputTokens)
    return budgetTokens === undefined ? { ...base, enabled: false } : { ...base, budgetTokens }
  }

  return base
}

export type ModelRuntimeIssueCode =
  | 'invalid_context_window'
  | 'invalid_max_output_tokens'
  | 'context_length'
  | 'text_input_unsupported'
  | 'vision_input_unsupported'
  | 'file_input_unsupported'
  | 'video_input_unsupported'
  | 'audio_input_unsupported'
  | 'tools_unsupported'
  | 'web_search_unsupported'

export interface ModelRuntimeIssue {
  code: ModelRuntimeIssueCode
  message: string
}

export interface ModelRuntimeValidationInput {
  alias: Pick<ModelAlias, 'alias' | 'capabilities' | 'contextWindow' | 'maxOutputTokens'>
  messages: readonly AgentMessage[]
  /** A tool call/result already present in history requires tool-capable input. */
  includeAdvertisedTools?: boolean
  /** The conversation asked for either built-in search or the app search tool. */
  webSearchRequested?: boolean
  /** Estimated prompt tokens after system/reminder decoration. */
  estimatedInputTokens?: number
  /** 本次请求实际预留的输出额度;缺省时保留旧调用方按模型协议上限校验的语义。 */
  maxOutputTokens?: number
}

function supports(capabilities: ModelCapabilities, key: keyof ModelCapabilities): boolean {
  if (key === 'textInput') return capabilities.textInput !== false
  if (key === 'textOutput') return capabilities.textOutput !== false
  if (key === 'vision') return capabilities.visionInput ?? capabilities.vision
  return capabilities[key] === true
}

function partKinds(messages: readonly AgentMessage[]): Set<string> {
  const kinds = new Set<string>()
  for (const message of messages) {
    for (const part of message.parts) {
      const type = (part as { type?: unknown }).type
      if (typeof type === 'string') kinds.add(type)
    }
  }
  return kinds
}

/**
 * Validate the model declaration against the real canonical request.  The
 * function returns all deterministic issues so callers can show the most
 * useful one while tests and management UIs can inspect the full result.
 */
export function validateModelRuntime(input: ModelRuntimeValidationInput): ModelRuntimeIssue[] {
  const { alias, messages } = input
  const issues: ModelRuntimeIssue[] = []
  const kinds = partKinds(messages)

  if (!Number.isInteger(alias.contextWindow) || alias.contextWindow <= 0) {
    issues.push({
      code: 'invalid_context_window',
      message: `模型「${alias.alias}」的上下文窗口必须是正整数。`
    })
  }
  if (
    !Number.isInteger(alias.maxOutputTokens) ||
    alias.maxOutputTokens <= 0 ||
    (Number.isInteger(alias.contextWindow) && alias.maxOutputTokens > alias.contextWindow)
  ) {
    issues.push({
      code: 'invalid_max_output_tokens',
      message: `模型「${alias.alias}」的最大输出 Token 配置无效。`
    })
  }

  if (kinds.has('text') && !supports(alias.capabilities, 'textInput')) {
    issues.push({ code: 'text_input_unsupported', message: `模型「${alias.alias}」不支持文本输入。` })
  }
  if (kinds.has('image') && !supports(alias.capabilities, 'vision')) {
    issues.push({ code: 'vision_input_unsupported', message: `模型「${alias.alias}」不支持图片 / Vision 输入。` })
  }
  // These part kinds are intentionally recognized before they join the public
  // ContentPart union, so the runtime boundary is ready for file/media pipes
  // without silently accepting a hand-written/imported payload meanwhile.
  if (kinds.has('file') && !supports(alias.capabilities, 'fileInput')) {
    issues.push({ code: 'file_input_unsupported', message: `模型「${alias.alias}」不支持文件输入。` })
  }
  if (kinds.has('video') && !supports(alias.capabilities, 'videoInput')) {
    issues.push({ code: 'video_input_unsupported', message: `模型「${alias.alias}」不支持视频输入。` })
  }
  if (kinds.has('audio') && !supports(alias.capabilities, 'audioInput')) {
    issues.push({ code: 'audio_input_unsupported', message: `模型「${alias.alias}」不支持音频输入。` })
  }

  const historyUsesTools = kinds.has('tool_call') || kinds.has('tool_result')
  if ((historyUsesTools || input.includeAdvertisedTools === true) && !alias.capabilities.tools) {
    issues.push({ code: 'tools_unsupported', message: `模型「${alias.alias}」不支持工具调用。` })
  }
  if (
    input.webSearchRequested === true &&
    alias.capabilities.webSearch !== true &&
    alias.capabilities.tools !== true
  ) {
    issues.push({
      code: 'web_search_unsupported',
      message: `模型「${alias.alias}」既不支持内置 Web Search，也不支持通过工具搜索。`
    })
  }

  const maxOutputTokens = input.maxOutputTokens ?? alias.maxOutputTokens
  if (
    typeof input.estimatedInputTokens === 'number' &&
    Number.isFinite(input.estimatedInputTokens) &&
    input.estimatedInputTokens + maxOutputTokens > alias.contextWindow
  ) {
    issues.push({
      code: 'context_length',
      message:
        `模型「${alias.alias}」的请求预计需要 ${String(Math.ceil(input.estimatedInputTokens))} 个输入 Token` +
        `，再加 ${String(maxOutputTokens)} 个最大输出 Token，超过 ${String(alias.contextWindow)} 的上下文窗口。`
    })
  }

  return issues
}

/** Old aliases predate the detailed capability matrix. */
export function modelSupportsTools(alias: Pick<ModelAlias, 'capabilities'> | undefined): boolean {
  return alias?.capabilities.tools !== false
}
