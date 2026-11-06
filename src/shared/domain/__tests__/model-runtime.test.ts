import { describe, expect, it } from 'vitest'
import type { AgentMessage } from '../../agent/message'
import type { ModelAlias, ThinkingConfig } from '../provider'
import { auxiliaryThinkingLevel, effectiveModelThinking, modelThinkingLevels, normalizeModelThinkingLevel, resolveModelThinking, validateModelRuntime } from '../model-runtime'

const message = (types: string[]): AgentMessage => ({
  id: 'm1',
  role: 'user',
  parts: types.map((type) => (
    type === 'text'
      ? { type: 'text', text: 'hello' }
      : type === 'image'
        ? { type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/themes/a.png' }
        : { type }
  )) as AgentMessage['parts'],
  createdAt: 1,
  schemaVersion: 1
})

function alias(over: Partial<ModelAlias> = {}): ModelAlias {
  return {
    alias: 'model-a',
    providerId: 'provider-a',
    upstreamModel: 'model-a',
    capabilities: { tools: true, vision: false, thinking: true, caching: false },
    contextWindow: 128_000,
    maxOutputTokens: 16_384,
    ...over
  }
}

describe('resolveModelThinking', () => {
  it('uses only declared efforts, maps none/xhigh to UI names, and clears a stale choice', () => {
    const model = alias({ thinkingConfig: { mode: 'effort', defaultEnabled: true, defaultEffort: 'max' },
      reasoningEfforts: ['low', 'high', 'max'] })
    expect(modelThinkingLevels(model)).toEqual(['auto', 'low', 'high', 'max'])
    expect(normalizeModelThinkingLevel('medium', model)).toBe('auto')
    expect(normalizeModelThinkingLevel('off', model)).toBe('auto')
    expect(normalizeModelThinkingLevel('high', model)).toBe('high')
    expect(modelThinkingLevels({ ...model, reasoningEfforts: ['none', 'xhigh'] })).toEqual(['auto', 'higher', 'off'])
    expect(resolveModelThinking('auto', model.thinkingConfig, 32_000, model.reasoningEfforts)?.effort).toBe('max')
  })

  it('exposes only relevant controls for unsupported, always, toggle and budget modes', () => {
    expect(modelThinkingLevels(alias({ thinkingConfig: { mode: 'unsupported', defaultEnabled: false } }))).toEqual(['auto'])
    expect(modelThinkingLevels(alias({ thinkingConfig: { mode: 'always', defaultEnabled: true } }))).toEqual(['auto'])
    const toggle = { mode: 'toggle', defaultEnabled: false, defaultBudgetTokens: 4096 } as const
    expect(modelThinkingLevels(alias({ thinkingConfig: toggle }))).toEqual(['auto', 'medium', 'off'])
    expect(resolveModelThinking('medium', toggle, 32_000)?.budgetTokens).toBe(4096)
    expect(modelThinkingLevels(alias({ thinkingConfig: { mode: 'budget', defaultEnabled: true } }))).toContain('higher')
  })

  it('treats automatic default none as disabled and repairs an obsolete default within the accepted set', () => {
    expect(resolveModelThinking('auto', { mode: 'effort', defaultEnabled: true, defaultEffort: 'none' }, 32_000, ['none', 'high']))
      .toMatchObject({ enabled: false, explicit: false })
    expect(resolveModelThinking('auto', { mode: 'effort', defaultEnabled: true, defaultEffort: 'medium' }, 32_000, ['low', 'high'])?.effort)
      .toBe('low')
  })
  it('unsupported never produces a reasoning request', () => {
    expect(resolveModelThinking('max', { mode: 'unsupported', defaultEnabled: false }, 32_000))
      .toBeUndefined()
  })

  it('effort uses defaults and maps the UI higher notch to xhigh', () => {
    const config: ThinkingConfig = {
      mode: 'effort',
      defaultEnabled: true,
      defaultEffort: 'low',
      parameterPath: 'reasoning_effort'
    }
    expect(resolveModelThinking('auto', config, 32_000)).toMatchObject({
      enabled: true,
      effort: 'low',
      explicit: false
    })
    expect(resolveModelThinking('higher', config, 32_000)).toMatchObject({
      enabled: true,
      effort: 'xhigh',
      explicit: true
    })
    expect(resolveModelThinking('off', config, 32_000)).toMatchObject({
      enabled: false,
      explicit: true
    })
  })

  it('budget is clamped below max output and always mode cannot be disabled', () => {
    expect(resolveModelThinking('max', {
      mode: 'budget',
      defaultEnabled: true,
      defaultBudgetTokens: 64_000
    }, 8_192)).toMatchObject({ enabled: true, budgetTokens: 7_168 })
    expect(resolveModelThinking('off', { mode: 'always', defaultEnabled: true }, 8_192))
      .toMatchObject({ mode: 'always', enabled: true })
  })
})

describe('legacy Anthropic budget controls', () => {
  const budget: ThinkingConfig = {
    mode: 'budget', defaultEnabled: true, defaultBudgetTokens: 60_000,
    parameterPath: 'thinking.budget_tokens'
  }
  const legacy = (over: Partial<ModelAlias> = {}): ModelAlias => alias({
    upstreamModel: 'claude-opus-5-5', runtimeProtocol: 'anthropic',
    thinkingConfig: { ...budget }, ...over
  })

  it.each([
    ['claude-opus-5', 'high', true],
    ['claude-opus-5-5', 'medium', false],
    ['claude-sonnet-5-5', 'high', false],
    ['claude-opus-6-1', 'high', false]
  ] as const)('shares %s defaults and supported Off with the sender', (upstreamModel, defaultEffort, off) => {
    const model = legacy({ upstreamModel })
    const saved = structuredClone(model)
    const effective = effectiveModelThinking(model)
    expect(effective.thinkingConfig).toMatchObject({ mode: 'effort', anthropicAdaptive: true, defaultEffort })
    expect(modelThinkingLevels(model).includes('off')).toBe(off)
    expect(modelThinkingLevels(model)).not.toContain('minimal')
    expect(auxiliaryThinkingLevel('off', model)).toBe(off ? 'off' : 'low')
    expect(normalizeModelThinkingLevel('off', model)).toBe(off ? 'off' : 'auto')
    expect(resolveModelThinking('auto', effective.thinkingConfig, 1024, effective.reasoningEfforts)?.effort).toBe(defaultEffort)
    expect(model).toEqual(saved)
  })

  it('projects explicit effort restrictions onto UI and auxiliary choices', () => {
    const model = legacy({ reasoningEfforts: ['none', 'medium', 'high'] })
    expect(modelThinkingLevels(model)).toEqual(['auto', 'medium', 'high'])
    expect(auxiliaryThinkingLevel('off', model)).toBe('medium')
    expect(auxiliaryThinkingLevel('higher', model)).toBe('high')
    expect(normalizeModelThinkingLevel('higher', model)).toBe('auto')
  })

  it('repairs legacy default Off without pretending that explicit Off is supported', () => {
    const model = legacy({ thinkingConfig: { ...budget, defaultEnabled: false } })
    expect(effectiveModelThinking(model).thinkingConfig).toMatchObject({ defaultEnabled: true, defaultEffort: 'medium' })
    expect(modelThinkingLevels(model)).not.toContain('off')
    expect(model.thinkingConfig?.defaultEnabled).toBe(false)
  })

  it.each(['claude-opus-4-5', 'claude-opus-4-6'])('keeps valid %s manual controls', (upstreamModel) => {
    const model = legacy({ upstreamModel })
    expect(effectiveModelThinking(model)).toBe(model)
    expect(modelThinkingLevels(model)).toContain('minimal')
    expect(auxiliaryThinkingLevel('off', model)).toBe('off')
  })

  it('uses declared protocol context, including preset overrides, not the Claude name', () => {
    for (const model of [
      legacy({ runtimeProtocol: undefined }),
      legacy({ runtimeProtocol: 'openai-chat' }),
      legacy({ requestAdapter: { preset: 'openai-responses', patches: [] } }),
      legacy({ thinkingConfig: { ...budget, standardWire: true } }),
      legacy({ thinkingConfig: { ...budget, anthropicAdaptive: false } }),
      legacy({ thinkingConfig: { ...budget, parameterPath: 'thinking_budget' },
        requestAdapter: { preset: 'custom', patches: [] } })
    ]) {
      expect(effectiveModelThinking(model)).toBe(model)
      expect(modelThinkingLevels(model)).toContain('off')
    }
    for (const model of [
      legacy({ runtimeProtocol: undefined, protocolOverride: 'anthropic' }),
      legacy({ runtimeProtocol: 'openai-chat', requestAdapter: { preset: 'anthropic', patches: [] } }),
      legacy({ requestAdapter: { preset: 'custom', patches: [] } })
    ]) expect(modelThinkingLevels(model)).not.toContain('off')
  })

  it('exposes only Auto for an empty accepted set and does not retain a stale default', () => {
    const model = legacy({ reasoningEfforts: [], thinkingConfig: { ...budget, defaultEffort: 'max' } })
    expect(modelThinkingLevels(model)).toEqual(['auto'])
    expect(auxiliaryThinkingLevel('off', model)).toBe('auto')
    expect(effectiveModelThinking(model).thinkingConfig?.defaultEffort).toBeUndefined()
  })
})

describe('validateModelRuntime', () => {
  it('honours explicit visionInput and legacy vision', () => {
    const legacy = validateModelRuntime({ alias: alias({ capabilities: { tools: true, vision: true, thinking: false, caching: false } }), messages: [message(['image'])] })
    expect(legacy).toEqual([])

    const explicit = validateModelRuntime({
      alias: alias({ capabilities: { tools: true, vision: true, visionInput: false, thinking: false, caching: false } }),
      messages: [message(['image'])]
    })
    expect(explicit.map((issue) => issue.code)).toContain('vision_input_unsupported')
  })

  it('checks file/media part kinds at the runtime boundary', () => {
    const issues = validateModelRuntime({
      alias: alias({ capabilities: { tools: true, vision: false, thinking: false, caching: false } }),
      messages: [message(['file', 'video', 'audio'])]
    })
    expect(issues.map((issue) => issue.code)).toEqual([
      'file_input_unsupported',
      'video_input_unsupported',
      'audio_input_unsupported'
    ])
  })

  it('checks tool history, web-search reachability, and configured limits', () => {
    const issues = validateModelRuntime({
      alias: alias({
        capabilities: { tools: false, vision: false, thinking: false, caching: false },
        contextWindow: 8_000,
        maxOutputTokens: 9_000
      }),
      messages: [message(['tool_call'])],
      webSearchRequested: true,
      estimatedInputTokens: 100
    })
    expect(issues.map((issue) => issue.code)).toEqual([
      'invalid_max_output_tokens',
      'tools_unsupported',
      'web_search_unsupported',
      'context_length'
    ])
  })

  it('uses input plus reserved output for the context-window check', () => {
    expect(validateModelRuntime({
      alias: alias({ contextWindow: 10_000, maxOutputTokens: 2_000 }),
      messages: [message(['text'])],
      estimatedInputTokens: 8_001
    }).map((issue) => issue.code)).toContain('context_length')
  })

  it('uses the request output budget instead of the model protocol limit when provided', () => {
    expect(validateModelRuntime({
      alias: alias({ contextWindow: 10_000, maxOutputTokens: 9_000 }),
      messages: [message(['text'])],
      estimatedInputTokens: 7_000,
      maxOutputTokens: 2_000
    }).map((issue) => issue.code)).not.toContain('context_length')
  })
})

/**
 * 需求:压缩 / 目标判定 / 权限审核 / 会话标题这些旁路请求想「尽量别思考」,
 * 但**绝不能因为档位不可用而失败**。这一组盯的正是那条失败:
 * `gpt-6-*` 的 reasoningEfforts 不含 'none',硬发 'off' 会被 thinking-adapter
 * 抛成不可重试的错 —— 压缩每次必败、三次熔断、上下文再也压不下去,全程零报错。
 */
describe('auxiliaryThinkingLevel', () => {
  it('keeps the wanted level when the model accepts it', () => {
    const model = alias({ thinkingConfig: { mode: 'effort', defaultEnabled: true },
      reasoningEfforts: ['none', 'low', 'high'] })
    expect(auxiliaryThinkingLevel('off', model)).toBe('off')
    expect(auxiliaryThinkingLevel('high', model)).toBe('high')
  })

  it('★ falls back to the lowest available strength when the model cannot disable reasoning', () => {
    // gpt-6-astra / sol / luna 的档位集合(openAiAstraEfforts):没有 none。
    const gpt6 = alias({ thinkingConfig: { mode: 'effort', defaultEnabled: true },
      reasoningEfforts: ['low', 'medium', 'high', 'xhigh', 'max'] })
    expect(auxiliaryThinkingLevel('off', gpt6)).toBe('low')
  })

  it('steps down to the closest weaker level instead of returning auto', () => {
    const model = alias({ thinkingConfig: { mode: 'effort', defaultEnabled: true },
      reasoningEfforts: ['none', 'low', 'max'] })
    // high 不在表里 —— 取「不超过它的最强一档」= low,而不是 normalize 的 'auto'。
    expect(auxiliaryThinkingLevel('high', model)).toBe('low')
    expect(normalizeModelThinkingLevel('high', model)).toBe('auto')
  })

  it('returns auto for auto, for reasoning-only models and when the model is unknown', () => {
    const always = alias({ thinkingConfig: { mode: 'always', defaultEnabled: true } })
    expect(auxiliaryThinkingLevel('auto', alias())).toBe('auto')
    expect(auxiliaryThinkingLevel('off', always)).toBe('auto')
    expect(auxiliaryThinkingLevel('off', undefined)).toBe('auto')
  })

  it('keeps Off available on toggle models', () => {
    const toggle = alias({ thinkingConfig: { mode: 'toggle', defaultEnabled: true } })
    expect(auxiliaryThinkingLevel('off', toggle)).toBe('off')
    // 轨道只有 ['auto','medium','off'],high 降到 medium。
    expect(auxiliaryThinkingLevel('high', toggle)).toBe('medium')
  })
})
