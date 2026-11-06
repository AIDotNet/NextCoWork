import type { BuiltinModelRecord } from '../types'
import { model, visionCapabilities, budgetThinking, effortThinking } from '../helpers'
import type { ModelAlias, ReasoningEffort, RequestAdapterConfig, ThinkingConfig, UpstreamProtocol } from '../../provider'

/** Published thinking/effort matrix; unlisted modern versions get a conservative adaptive fallback.
 * Explicit binding/catalogue declarations take precedence over this fallback.
 * https://platform.claude.com/docs/en/build-with-claude/thinking
 * https://platform.claude.com/docs/en/build-with-claude/effort
 */
export function anthropicThinkingForModel(id: string, requiredOnly = false): Pick<ModelAlias, 'thinkingConfig' | 'reasoningEfforts'> | undefined {
  const match = /(?:^|\/)claude-([a-z][a-z-]*)-(\d+)(?:[-.](\d{1,2})(?=$|[-.:]))?(?:$|[-.:])/iu.exec(id)
  if (match === null) return undefined
  const [, family, majorText, minorText] = match
  const major = Number(majorText)
  const minor = Number(minorText ?? 0)
  const line = family!.toLowerCase()
  const known4 = major === 4 && ((['opus', 'sonnet'].includes(line) && minor === 6) || (line === 'opus' && [7, 8].includes(minor)))
  const modern = major > 5 || major === 5 || (major === 4 && minor >= 7)
  // Explicit manual budgets remain valid on 4.6; only newer models require conversion.
  if ((!known4 && !modern) || (requiredOnly && known4 && minor === 6)) return undefined
  const known5 = major === 5 && (
    (['opus', 'sonnet'].includes(line) && [0, 5].includes(minor)) ||
    (line === 'haiku' && minor === 5) ||
    (['fable', 'mythos'].includes(line) && [0, 1].includes(minor)))
  // 'none' is a local Off choice, never an Anthropic effort wire value.
  // Unknown future rows intentionally omit Off rather than guessing whether disabled is valid.
  const off = known4 || (known5 && ((['opus', 'sonnet'].includes(line) && minor === 0) || line === 'haiku'))
  const reasoningEfforts: ReasoningEffort[] = off ? ['none', 'low', 'medium', 'high'] : ['low', 'medium', 'high']
  if (known5 || (known4 && minor >= 7)) reasoningEfforts.push('xhigh')
  if (known4 || known5) reasoningEfforts.push('max')
  const defaultEffort = known5 && minor === 5 && ['opus', 'haiku'].includes(line) ? 'medium' : 'high'
  return {
    thinkingConfig: { ...effortThinking('output_config.effort', defaultEffort), anthropicAdaptive: true },
    reasoningEfforts
  }
}

/*
 * ★★ 每一行都显式写 `contextWindow: 1_000_000`(2026-09-18 核对修正)。
 * 在这之前全部 16 行吃 `model()` 的 200_000 兜底(helpers.ts),表现是圆环分母、
 * 设置页窗口列、「最大上下文」开关的判据(`supportsMaxContext`)全按 200K 算 ——
 * 窗口被静默压小,而且没有任何报错指向目录。费率卡对超 200K 不加价
 * (pricing-seed.ts 文件头的 PDF 证据),所以不需要配套的长上下文价格档。
 * (2026-09-23 新增 claude-opus-5-5、2026-09-29 新增 claude-sonnet-5-5、
 * 2026-10-08 新增 claude-haiku-5-5 后共 19 行,同样显式写 1M,不破这条不变式。)
 */
/** Shared read/send projection; never changes a saved Token Budget declaration. */
export function adaptiveAnthropicBinding(
  upstreamModel: string,
  config: ThinkingConfig | undefined,
  protocol: UpstreamProtocol | undefined,
  preset: RequestAdapterConfig['preset'] | undefined,
  reasoningEfforts: readonly ReasoningEffort[] | undefined
): Pick<ModelAlias, 'thinkingConfig' | 'reasoningEfforts'> | undefined {
  if (config === undefined || config.standardWire === true || config.anthropicAdaptive === false ||
    (config.mode !== 'budget' && config.mode !== 'effort')) return undefined
  const path = config.parameterPath?.trim()
  if (config.anthropicAdaptive !== true &&
    (config.mode !== 'budget' || (path !== undefined && path !== '' && path !== 'thinking.budget_tokens'))) return undefined
  const wire = preset === 'anthropic' || preset === 'openai-chat' || preset === 'openai-responses'
    ? preset : protocol
  if (wire !== 'anthropic') return undefined
  const published = anthropicThinkingForModel(upstreamModel, config.anthropicAdaptive !== true)
  if (published === undefined && config.anthropicAdaptive !== true) return undefined
  const publishedEfforts = published?.reasoningEfforts
  const efforts: readonly ReasoningEffort[] = publishedEfforts === undefined ? reasoningEfforts ?? ['low', 'medium', 'high']
    : reasoningEfforts === undefined ? publishedEfforts
      : publishedEfforts.filter((effort) => reasoningEfforts.includes(effort))
  const preferred = (config.mode === 'effort' ? config.defaultEffort : undefined) ??
    published?.thinkingConfig?.defaultEffort ?? 'medium'
  const defaultEffort = efforts.includes(preferred) ? preferred : efforts.find((effort) => effort !== 'none')
  return {
    thinkingConfig: {
      ...config,
      mode: 'effort',
      anthropicAdaptive: true,
      parameterPath: 'output_config.effort',
      defaultEffort,
      defaultEnabled: config.mode === 'budget' && !efforts.includes('none') ? true : config.defaultEnabled
    },
    reasoningEfforts: efforts
  }
}

export const ANTHROPIC: readonly BuiltinModelRecord[] = [
  /*
   * 需求:2026-09-23 用户点名收录 Opus 5.5,连同其费率一起给的(见 pricing-seed
   * 的 Anthropic 段)。窗口按同族 Opus 5 推。官方已公布该型号为 adaptive
   * thinking + output_config.effort(见 anthropicThinkingForModel),不再用 token budget。
   */
  model('anthropic', 'claude-opus-5-5', 'Claude Opus 5.5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-opus-5-5'),
  }),
  model('anthropic', 'claude-opus-5', 'Claude Opus 5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-opus-5'),
  }),
  /*
   * 需求:2026-09-29 用户点名收录 Sonnet 5.5(费率见 pricing-seed 的 Anthropic 段)。
   * 窗口按同族 Sonnet 5 推为 1M。思考已公布为 adaptive thinking +
   * output_config.effort(见 anthropicThinkingForModel),不再按 32K token budget 推。
   */
  model('anthropic', 'claude-sonnet-5-5', 'Claude Sonnet 5.5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-sonnet-5-5'),
  }),
  model('anthropic', 'claude-sonnet-5', 'Claude Sonnet 5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-sonnet-5'),
  }),
  /*
   * 需求:2026-10-08 用户点名收录 Haiku 5.5。ID / 1M 窗口 / 128K 输出 / Adaptive 思考
   * 取自官方 Models overview 对比表(同日直读)。思考档位已公布为 adaptive thinking +
   * output_config.effort(见 anthropicThinkingForModel),不再按同族 Haiku 4.5 的 16K budget 推。
   */
  model('anthropic', 'claude-haiku-5-5', 'Claude Haiku 5.5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-haiku-5-5'),
  }),
  model('anthropic', 'claude-mythos-5-1', 'Claude Mythos 5.1', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-mythos-5-1'),
  }),
  model('anthropic', 'claude-fable-5-1', 'Claude Fable 5.1', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-fable-5-1'),
    /*
     * ★ 点号拼写是预设侧真实在用的形式(anthropic / openrouter 两条预设的
     * suggestedModels),不登记的话这两处错过目录:元数据走兜底值,
     * openai 族供应商上的协议钉也不命中 —— findBuiltinModel 的匹配
     * 认不了 `5.1` 与 `5-1` 的差别。
     */
    aliases: ['claude-fable-5.1'],
  }),
  model('anthropic', 'claude-mythos-5', 'Claude Mythos 5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-mythos-5'),
  }),
  model('anthropic', 'claude-fable-5', 'Claude Fable 5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-fable-5'),
  }),
  model('anthropic', 'claude-opus-4-8', 'Claude Opus 4.8', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-opus-4-8'),
    // ★ 点号别名,理由同 claude-fable-5-1 那条(anthropic 预设在用它)
    aliases: ['claude-opus-4.8'],
  }),
  model('anthropic', 'claude-opus-4-7', 'Claude Opus 4.7', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-opus-4-7'),
  }),
  model('anthropic', 'claude-opus-4-6', 'Claude Opus 4.6', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-opus-4-6'),
  }),
  model('anthropic', 'claude-opus-4-5', 'Claude Opus 4.5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    thinkingConfig: budgetThinking('thinking.budget_tokens', 64_000),
  }),
  model('anthropic', 'claude-sonnet-4-6', 'Claude Sonnet 4.6', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    ...anthropicThinkingForModel('claude-sonnet-4-6'),
  }),
  model('anthropic', 'claude-sonnet-4-5', 'Claude Sonnet 4.5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    thinkingConfig: budgetThinking('thinking.budget_tokens', 32_000),
  }),
  model('anthropic', 'claude-haiku-4-5-20251001', 'Claude Haiku 4.5', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    thinkingConfig: budgetThinking('thinking.budget_tokens', 16_000),
  }),
  model('anthropic', 'claude-3-7-sonnet-latest', 'Claude 3.7 Sonnet', {
    capabilities: visionCapabilities({ thinking: true }),
    contextWindow: 1_000_000,
    thinkingConfig: budgetThinking('thinking.budget_tokens', 16_000),
    aliases: ['claude-3-7-sonnet-20250219'],
  }),
  model('anthropic', 'claude-3-5-sonnet-latest', 'Claude 3.5 Sonnet', {
    capabilities: visionCapabilities(),
    contextWindow: 1_000_000,
    aliases: ['claude-3-5-sonnet-20241022'],
  }),
  model('anthropic', 'claude-3-5-haiku-latest', 'Claude 3.5 Haiku', {
    capabilities: visionCapabilities(),
    contextWindow: 1_000_000,
    aliases: ['claude-3-5-haiku-20241022'],
  }),
]
