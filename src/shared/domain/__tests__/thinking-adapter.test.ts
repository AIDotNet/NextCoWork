import { describe, expect, it } from 'vitest'
import type { ThinkingLevel } from '../../agent/run-request'
import type { ResolvedModelThinking } from '../model-runtime'
import { applyThinkingAdapter, enforceThinkingPreference, reasoningReplayFor, removeUnsupportedThinking, ThinkingAdapterError } from '../thinking-adapter'
import { OLLAMA_STANDARD_THINKING } from '../model-catalog-inventory'
import { modelBindingResolver } from '../model-binding'
import { modelThinkingLevels, resolveModelThinking } from '../model-runtime'
import { IMPORTED_ALIAS_DEFAULTS, type ThinkingConfig } from '../provider'

const effort: ResolvedModelThinking = {
  mode: 'effort',
  enabled: true,
  explicit: true,
  effort: 'xhigh',
}

describe('applyThinkingAdapter', () => {
  it('maps adaptive Anthropic thinking to type adaptive and output_config effort', () => {
    const config = {
      mode: 'effort' as const,
      defaultEnabled: true,
      defaultEffort: 'medium' as const,
      parameterPath: 'output_config.effort',
      anthropicAdaptive: true
    }
    expect(applyThinkingAdapter(
      { thinking: { type: 'enabled', budget_tokens: 64_000 } },
      {
        protocol: 'anthropic',
        upstreamModel: 'claude-opus-5-5',
        maxOutputTokens: 64_000,
        config,
        reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'high' },
        reasoningEfforts: ['none', 'low', 'medium', 'high', 'max']
      }
    )).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: 'high' } })
  })

  it('uses protocol effort rather than a custom path on compatible non-Anthropic bindings', () => {
    expect(applyThinkingAdapter({}, {
      protocol: 'openai-chat', upstreamModel: 'claude-opus-5-5', maxOutputTokens: 8192,
      config: { mode: 'effort', defaultEnabled: true, defaultEffort: 'medium',
        parameterPath: 'output_config.effort', anthropicAdaptive: true },
      reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'high' }
    })).toEqual({ reasoning_effort: 'high' })
  })

  it.each(['openai-chat', 'openai-responses'] as const)('uses %s effort with custom patches on an adaptive binding', (protocol) => {
    expect(applyThinkingAdapter({}, {
      protocol, preset: 'custom', upstreamModel: 'claude-opus-5-5', maxOutputTokens: 8192,
      config: { mode: 'effort', defaultEnabled: true, defaultEffort: 'medium',
        parameterPath: 'output_config.effort', anthropicAdaptive: true },
      reasoning: { mode: 'effort', enabled: true, explicit: false, effort: 'medium' }
    })).toEqual(protocol === 'openai-chat'
      ? { reasoning_effort: 'medium' } : { reasoning: { effort: 'medium' } })
  })

  it('supports custom patches on an Anthropic adaptive binding', () => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic', preset: 'custom', upstreamModel: 'claude-opus-5-5', maxOutputTokens: 8192,
      config: { mode: 'effort', defaultEnabled: true, defaultEffort: 'medium',
        parameterPath: 'output_config.effort', anthropicAdaptive: true },
      reasoning: { mode: 'effort', enabled: true, explicit: false, effort: 'medium' }
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: 'medium' } })
  })

  it('sends disabled and clears stale adaptive effort for an explicit Off', () => {
    const body = applyThinkingAdapter(
      { thinking: { type: 'enabled', budget_tokens: 64_000 }, output_config: { effort: 'high', format: 'json' } },
      {
        protocol: 'anthropic',
        upstreamModel: 'claude-opus-5',
        maxOutputTokens: 64_000,
        config: { mode: 'effort', defaultEnabled: true, defaultEffort: 'high', anthropicAdaptive: true },
        reasoningEfforts: ['none', 'low', 'medium', 'high', 'xhigh', 'max'],
        reasoning: { mode: 'effort', enabled: false, explicit: true }
      }
    )
    expect(body).toHaveProperty('thinking', { type: 'disabled' })
    expect(body).toHaveProperty('output_config', { format: 'json' })
  })
  it.each(['off', 'auto'] as const)('accepts %s on a confirmed Off-only adaptive binding', (thinkingLevel) => {
    const config: ThinkingConfig = {
      mode: 'effort', defaultEnabled: true, defaultEffort: 'none', anthropicAdaptive: true
    }
    expect(applyThinkingAdapter({ output_config: { effort: 'max', format: 'json' } }, {
      protocol: 'anthropic', upstreamModel: 'claude-opus-5', maxOutputTokens: 1024,
      config, reasoningEfforts: ['none'], thinkingLevel,
      reasoning: resolveModelThinking(thinkingLevel, config, 1024, ['none'])
    })).toEqual({ thinking: { type: 'disabled' }, output_config: { format: 'json' } })
  })

  it('keeps disabled budget-only legacy callers usable with an Off-only accepted set', () => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic', upstreamModel: 'claude-opus-5', maxOutputTokens: 8192,
      config: { mode: 'budget', defaultEnabled: false, parameterPath: 'thinking.budget_tokens' },
      reasoningEfforts: ['none'], reasoning: { mode: 'budget', enabled: false, explicit: true }
    })).toEqual({ thinking: { type: 'disabled' } })
  })

  it('honours a supported adaptive default Off on Auto', () => {
    const config = { mode: 'effort' as const, defaultEnabled: false, defaultEffort: 'high' as const,
      parameterPath: 'output_config.effort', anthropicAdaptive: true }
    const reasoningEfforts = ['none', 'low', 'medium', 'high'] as const
    expect(applyThinkingAdapter({ output_config: { effort: 'max' } }, {
      protocol: 'anthropic', upstreamModel: 'claude-opus-5', maxOutputTokens: 8192,
      config, reasoningEfforts,
      reasoning: resolveModelThinking('auto', config, 8192, reasoningEfforts)
    })).toEqual({ thinking: { type: 'disabled' } })
  })

  it('maps an Anthropic budget and removes incompatible fields', () => {
    expect(
      applyThinkingAdapter(
        { reasoning_effort: 'high' },
        {
          protocol: 'anthropic',
          upstreamModel: 'claude-sonnet-4',
          maxOutputTokens: 20_000,
          config: {
            mode: 'budget',
            defaultEnabled: true,
            parameterPath: 'thinking.budget_tokens',
          },
          reasoning: {
            mode: 'budget',
            enabled: true,
            explicit: true,
            budgetTokens: 8_000,
          },
        },
      ),
    ).toEqual({ thinking: { type: 'enabled', budget_tokens: 8_000 } })
  })

  it('maps OpenAI Responses effort and sends nested none for an explicit Off', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      parameterPath: 'reasoning_effort',
    } as const
    expect(
      applyThinkingAdapter(
        {
          reasoning_effort: 'high',
          reasoning: { effort: 'low' },
          thinking: { type: 'enabled' },
          temperature: 0.2,
        },
        {
          protocol: 'openai-responses',
          upstreamModel: 'gpt-6-astra',
          maxOutputTokens: 32_000,
          config,
          reasoning: effort,
        },
      ),
    ).toEqual({ temperature: 0.2, reasoning: { effort: 'xhigh' } })
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-responses',
          upstreamModel: 'gpt-5.6-sol',
          maxOutputTokens: 32_000,
          config,
          reasoning: { mode: 'effort', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ reasoning: { effort: 'none' } })
  })

  it('maps OpenAI Chat Completions effort to the flat field', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      parameterPath: 'reasoning_effort',
    } as const
    expect(
      applyThinkingAdapter(
        { reasoning: { effort: 'low' } },
        {
          protocol: 'openai-chat',
          upstreamModel: 'gpt-5.4',
          maxOutputTokens: 32_000,
          config,
          reasoning: effort,
        },
      ),
    ).toEqual({ reasoning_effort: 'xhigh' })
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'gpt-5.4',
          maxOutputTokens: 32_000,
          config,
          reasoning: { mode: 'effort', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ reasoning_effort: 'none' })
  })

  it('lets an explicit OpenAI preset override the provider protocol', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      parameterPath: 'reasoning_effort',
    } as const
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          preset: 'openai-responses',
          upstreamModel: 'gpt-model',
          maxOutputTokens: 32_000,
          config,
          reasoning: effort,
        },
      ),
    ).toEqual({ reasoning: { effort: 'xhigh' } })
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-responses',
          preset: 'openai-chat',
          upstreamModel: 'gpt-model',
          maxOutputTokens: 32_000,
          config,
          reasoning: effort,
        },
      ),
    ).toEqual({ reasoning_effort: 'xhigh' })
  })

  it('maps DeepSeek and GLM toggles to their provider objects', () => {
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'deepseek-v3.2',
          maxOutputTokens: 16_000,
          config: {
            mode: 'toggle',
            defaultEnabled: false,
            parameterPath: 'thinking',
          },
          reasoning: {
            mode: 'toggle',
            enabled: true,
            explicit: true,
            budgetTokens: 4_096,
          },
        },
      ),
    ).toEqual({ thinking: { type: 'enabled' } })

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'glm-4.7',
          maxOutputTokens: 16_000,
          config: {
            mode: 'toggle',
            defaultEnabled: false,
            parameterPath: 'thinking.type',
          },
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })
  })

  it('maps Hunyuan effort together with the official thinking toggle', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      defaultEffort: 'high',
      parameterPath: 'reasoning_effort',
    } as const

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'hy4-preview',
          maxOutputTokens: 65_536,
          config,
          reasoning: {
            mode: 'effort',
            enabled: true,
            explicit: true,
            effort: 'low',
          },
          reasoningEfforts: ['none', 'low', 'high'],
        },
      ),
    ).toEqual({ thinking: { type: 'enabled' }, reasoning_effort: 'low' })

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'tencent/hy3',
          maxOutputTokens: 131_072,
          config,
          reasoning: { mode: 'effort', enabled: false, explicit: true },
          reasoningEfforts: ['none', 'low', 'high'],
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })
  })

  it('maps DeepSeek V4 toggle and effort without sending unsupported budget_tokens', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      defaultEffort: 'high',
      parameterPath: 'reasoning_effort',
    } as const

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'deepseek-v4-pro',
          maxOutputTokens: 384_000,
          config,
          reasoning: {
            mode: 'effort',
            enabled: true,
            explicit: true,
            effort: 'max',
          },
          reasoningEfforts: ['none', 'low', 'high', 'max'],
        },
      ),
    ).toEqual({
      reasoning_effort: 'max',
      thinking: { type: 'enabled' },
    })

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'deepseek-v4-flash',
          maxOutputTokens: 384_000,
          config,
          reasoning: { mode: 'effort', enabled: false, explicit: true },
          reasoningEfforts: ['none', 'low', 'high', 'max'],
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })
  })

  it('keeps the GLM thinking switch and forwards its supported reasoning effort', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      defaultEffort: 'max',
      parameterPath: 'reasoning_effort',
    } as const

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'glm-5.3-flash',
          maxOutputTokens: 128_000,
          config,
          reasoning: {
            mode: 'effort',
            enabled: true,
            explicit: true,
            effort: 'high',
          },
          reasoningEfforts: ['low', 'high', 'max'],
        },
      ),
    ).toEqual({
      thinking: { type: 'enabled' },
      reasoning_effort: 'high',
    })

    expect(() =>
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'glm-5.3-flash',
          maxOutputTokens: 128_000,
          config,
          reasoning: { mode: 'effort', enabled: false, explicit: true },
          reasoningEfforts: ['low', 'high', 'max'],
        },
      ),
    ).toThrow(/不支持关闭推理/u)
  })

  it('auto honours Qwen/Hunyuan-style declared paths instead of forcing OpenAI effort', () => {
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'qwen3-plus',
          maxOutputTokens: 16_000,
          config: {
            mode: 'toggle',
            defaultEnabled: false,
            parameterPath: 'enable_thinking',
          },
          reasoning: {
            mode: 'toggle',
            enabled: true,
            explicit: true,
            budgetTokens: 4_096,
          },
        },
      ),
    ).toEqual({ enable_thinking: true })

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'hunyuan-turbo',
          maxOutputTokens: 16_000,
          config: {
            mode: 'toggle',
            defaultEnabled: false,
            parameterPath: 'chat_template_kwargs.enable_thinking',
          },
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ chat_template_kwargs: { enable_thinking: false } })
  })

  it('supports declarative toggle values and effort mappings', () => {
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'MiniMax-M3',
          maxOutputTokens: 16_000,
          config: {
            mode: 'toggle',
            defaultEnabled: true,
            parameterPath: 'thinking',
            enabledValue: { type: 'adaptive' },
            disabledValue: { type: 'disabled' },
          },
          reasoning: {
            mode: 'toggle',
            enabled: true,
            explicit: false,
            budgetTokens: 4_096,
          },
        },
      ),
    ).toEqual({ thinking: { type: 'adaptive' } })

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'MiniMax-M3',
          maxOutputTokens: 16_000,
          config: {
            mode: 'toggle',
            defaultEnabled: true,
            parameterPath: 'thinking',
            enabledValue: { type: 'adaptive' },
            disabledValue: { type: 'disabled' },
          },
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-responses',
          upstreamModel: 'vendor-reasoner',
          maxOutputTokens: 16_000,
          config: {
            mode: 'effort',
            defaultEnabled: true,
            parameterPath: 'reasoning_effort',
            effortMap: { xhigh: 'extra_high' },
          },
          reasoning: effort,
          reasoningEfforts: ['low', 'medium', 'high', 'xhigh'],
        },
      ),
    ).toEqual({ reasoning: { effort: 'extra_high' } })
  })

  it('maps MiMo nested toggle values from its catalogue declaration', () => {
    const config = {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
      enabledValue: 'enabled',
      disabledValue: 'disabled',
    } as const
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'mimo-v2.5-pro',
          maxOutputTokens: 16_000,
          config,
          reasoning: {
            mode: 'toggle',
            enabled: true,
            explicit: false,
            budgetTokens: 4_096,
          },
        },
      ),
    ).toEqual({ thinking: { type: 'enabled' } })
    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'mimo-v2.5-pro',
          maxOutputTokens: 16_000,
          config,
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })
  })

  it('maps the official LongCat Think toggle to thinking.type without an effort or budget', () => {
    const config = {
      mode: 'toggle',
      defaultEnabled: false,
      parameterPath: 'thinking.type',
    } as const

    expect(
      applyThinkingAdapter(
        { reasoning_effort: 'high', thinking_budget: 8_192 },
        {
          protocol: 'openai-chat',
          upstreamModel: 'LongCat-2.0',
          maxOutputTokens: 131_072,
          config,
          reasoning: { mode: 'toggle', enabled: true, explicit: true },
        },
      ),
    ).toEqual({ thinking: { type: 'enabled' } })
    expect(
      applyThinkingAdapter(
        { thinking: { type: 'enabled' } },
        {
          protocol: 'openai-chat',
          upstreamModel: 'longcat-2.0',
          maxOutputTokens: 131_072,
          config,
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })
  })

  it('maps the official openPangu Think toggle to thinking.type', () => {
    const config = {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking.type',
    } as const

    expect(
      applyThinkingAdapter(
        { reasoning_effort: 'high', thinking_budget: 8_192 },
        {
          protocol: 'openai-chat',
          upstreamModel: 'openpangu-2.0-pro',
          maxOutputTokens: 128_000,
          config,
          reasoning: { mode: 'toggle', enabled: true, explicit: false },
        },
      ),
    ).toEqual({ thinking: { type: 'enabled' } })

    expect(
      applyThinkingAdapter(
        { thinking: { type: 'enabled' } },
        {
          protocol: 'openai-chat',
          upstreamModel: 'openpangu-2.0-flash',
          maxOutputTokens: 128_000,
          config,
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking: { type: 'disabled' } })
  })

  it('maps the official InternLM boolean thinking_mode switch both ways', () => {
    const config = {
      mode: 'toggle',
      defaultEnabled: true,
      parameterPath: 'thinking_mode',
      enabledValue: true,
      disabledValue: false,
    } as const

    expect(
      applyThinkingAdapter(
        { thinking: { type: 'enabled' }, reasoning_effort: 'high' },
        {
          protocol: 'openai-chat',
          upstreamModel: 'intern-s2-preview-397b',
          maxOutputTokens: 16_384,
          config,
          reasoning: { mode: 'toggle', enabled: true, explicit: false },
        },
      ),
    ).toEqual({ thinking_mode: true })
    expect(
      applyThinkingAdapter(
        { thinking_mode: true },
        {
          protocol: 'openai-chat',
          upstreamModel: 'intern-s1-mini',
          maxOutputTokens: 16_384,
          config,
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking_mode: false })
  })

  it('maps the SenseNova V6.5 nested thinking.enabled boolean switch', () => {
    const config = {
      mode: 'toggle',
      defaultEnabled: false,
      parameterPath: 'thinking.enabled',
    } as const

    expect(
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'SenseNova-V6-5-Pro',
          maxOutputTokens: 16_384,
          config,
          reasoning: { mode: 'toggle', enabled: true, explicit: true },
        },
      ),
    ).toEqual({ thinking: { enabled: true } })
    expect(
      applyThinkingAdapter(
        { thinking: { enabled: true } },
        {
          protocol: 'openai-chat',
          upstreamModel: 'SenseNova-V6-5-Turbo',
          maxOutputTokens: 16_384,
          config,
          reasoning: { mode: 'toggle', enabled: false, explicit: true },
        },
      ),
    ).toEqual({ thinking: { enabled: false } })
  })

  it('blocks unsupported effort and an Off choice when none is unavailable', () => {
    const config = {
      mode: 'effort',
      defaultEnabled: true,
      parameterPath: 'reasoning_effort',
    } as const
    expect(() =>
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-responses',
          upstreamModel: 'gpt-model',
          maxOutputTokens: 16_000,
          config,
          reasoning: effort,
          reasoningEfforts: ['low', 'medium', 'high'],
        },
      ),
    ).toThrow(/不支持推理强度/u)
    expect(() =>
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-responses',
          upstreamModel: 'gpt-model',
          maxOutputTokens: 16_000,
          config,
          reasoning: { mode: 'effort', enabled: false, explicit: true },
          reasoningEfforts: ['low', 'medium', 'high'],
        },
      ),
    ).toThrow(/不支持关闭推理/u)
  })

  it('strips stale or patched fields for unsupported models', () => {
    expect(
      applyThinkingAdapter(
        {
          thinking: { type: 'enabled' },
          reasoning_effort: 'high',
          thinking_mode: true,
          temperature: 0.2,
        },
        {
          protocol: 'openai-chat',
          upstreamModel: 'plain-model',
          maxOutputTokens: 8_000,
          config: { mode: 'unsupported', defaultEnabled: false },
          reasoning: undefined,
        },
      ),
    ).toEqual({ temperature: 0.2 })
  })

  it.each(['unsupported', 'always'] as const)('prevents JSON Patch from restoring Think fields for %s models', (mode) => {
    expect(
      removeUnsupportedThinking(
        {
          thinking: { type: 'enabled' },
          reasoning_effort: 'high',
          extra_body: {
            enable_thinking: true,
            thinking_budget: 4_096,
            thinking_mode: true,
            keep: 'value',
          },
          temperature: 0.2,
        },
        { mode, defaultEnabled: mode === 'always' },
      ),
    ).toEqual({ extra_body: { keep: 'value' }, temperature: 0.2 })
  })

  it('requires a whitelisted path for custom adapters', () => {
    expect(() =>
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'vendor-model',
          maxOutputTokens: 8_000,
          preset: 'custom',
          config: {
            mode: 'toggle',
            defaultEnabled: true,
            parameterPath: 'dangerous.field',
          },
          reasoning: {
            mode: 'toggle',
            enabled: true,
            explicit: false,
            budgetTokens: 2_000,
          },
        },
      ),
    ).toThrow(ThinkingAdapterError)
  })

  it.each(['enable_thinking', 'chat_template_kwargs.enable_thinking', 'thinking_mode', 'thinking.enabled', 'reasoning_split'])('rejects budget mode on non-budget path %s', (parameterPath) => {
    expect(() =>
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'vendor-model',
          maxOutputTokens: 16_000,
          config: { mode: 'budget', defaultEnabled: true, parameterPath },
          reasoning: {
            mode: 'budget',
            enabled: true,
            explicit: true,
            budgetTokens: 4_096,
          },
        },
      ),
    ).toThrow(/Token budget 不能写入开关参数/u)
  })

  it.each(['thinking_budget', 'thinking.budget_tokens'])('rejects toggle mode on budget path %s', (parameterPath) => {
    expect(() =>
      applyThinkingAdapter(
        {},
        {
          protocol: 'openai-chat',
          upstreamModel: 'vendor-model',
          maxOutputTokens: 16_000,
          config: { mode: 'toggle', defaultEnabled: true, parameterPath },
          reasoning: {
            mode: 'toggle',
            enabled: true,
            explicit: true,
            budgetTokens: 4_096,
          },
        },
      ),
    ).toThrow(/开关不能写入 Token budget/u)
  })
})

/**
 * Legacy budget rows still reach the adapter. A 1024-token output disables
 * every budget (ceiling is 0); 8192 clamps medium and above to 7168, which a
 * token→effort map would collapse. thinkingLevel must win over that resolution.
 * anthropicAdaptive omitted and true both convert this budget path.
 */
describe('adaptiveInput legacy Anthropic budgets', () => {
  const levels = ['auto', 'low', 'medium', 'high', 'higher', 'max'] as const satisfies readonly ThinkingLevel[]

  function budgetConfig(flag: 'omitted' | 'true', defaultEnabled = true): ThinkingConfig {
    const config: ThinkingConfig = {
      mode: 'budget',
      defaultEnabled,
      defaultBudgetTokens: 64_000,
      parameterPath: 'thinking.budget_tokens'
    }
    if (flag === 'true') config.anthropicAdaptive = true
    return config
  }

  function effortFor(model: string, level: ThinkingLevel): 'low' | 'medium' | 'high' | 'xhigh' | 'max' {
    if (level === 'auto') return model === 'claude-opus-5' ? 'high' : 'medium'
    if (level === 'minimal') return 'low'
    if (level === 'higher') return 'xhigh'
    if (level === 'low' || level === 'medium' || level === 'high' || level === 'max') return level
    throw new Error(`unexpected level ${level}`)
  }

  const preserved = (['claude-opus-5', 'claude-opus-5-5'] as const).flatMap((model) =>
    levels.flatMap((level) =>
      ([1024, 8192] as const).flatMap((maxOutputTokens) =>
        (['omitted', 'true'] as const).map((flag) => [model, level, maxOutputTokens, flag] as const)
      )
    )
  )

  it.each(preserved)('%s %s at maxOutputTokens %i (anthropicAdaptive %s) keeps the original effort', (model, level, maxOutputTokens, flag) => {
    const config = budgetConfig(flag)
    const reasoning = resolveModelThinking(level, config, maxOutputTokens)
    if (maxOutputTokens === 1024) expect(reasoning).toMatchObject({ enabled: false })
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: model,
      maxOutputTokens,
      config,
      reasoning,
      thinkingLevel: level
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: effortFor(model, level) } })
  })

  it.each([
    ['claude-opus-5', 1024],
    ['claude-opus-5', 8192],
    ['claude-opus-5-5', 1024],
    ['claude-opus-5-5', 8192]
  ] as const)('%s minimal budget maps to low at maxOutputTokens %i', (model, maxOutputTokens) => {
    const config = budgetConfig('omitted')
    const reasoning = resolveModelThinking('minimal', config, maxOutputTokens)
    if (maxOutputTokens === 1024) expect(reasoning).toMatchObject({ enabled: false })
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: model,
      maxOutputTokens,
      config,
      reasoning,
      thinkingLevel: 'minimal'
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: 'low' } })
  })

  it.each([
    ['claude-opus-5', 'omitted', 'high'],
    ['claude-opus-5', 'true', 'high'],
    ['claude-opus-5-5', 'omitted', 'medium'],
    ['claude-opus-5-5', 'true', 'medium']
  ] as const)('%s Auto without thinkingLevel (anthropicAdaptive %s) uses %s, not the 64000-budget max', (model, flag, effort) => {
    const config = budgetConfig(flag)
    const reasoning = resolveModelThinking('auto', config, 128_000)
    expect(reasoning).toMatchObject({ explicit: false, enabled: true, budgetTokens: 64_000, mode: 'budget' })
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: model,
      maxOutputTokens: 128_000,
      config,
      reasoning
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort } })
  })

  it.each(['omitted', 'true'] as const)('explicit Off on opus-5 disables thinking and keeps the output format (anthropicAdaptive %s)', (flag) => {
    const config = budgetConfig(flag)
    expect(applyThinkingAdapter(
      { thinking: { type: 'enabled', budget_tokens: 64_000 }, output_config: { effort: 'high', format: 'json' } },
      {
        protocol: 'anthropic',
        upstreamModel: 'claude-opus-5',
        maxOutputTokens: 64_000,
        config,
        reasoning: resolveModelThinking('off', config, 64_000),
        thinkingLevel: 'off'
      }
    )).toEqual({ thinking: { type: 'disabled' }, output_config: { format: 'json' } })
  })

  it.each(['omitted', 'true'] as const)('explicit Off on opus-5.5 is rejected (anthropicAdaptive %s)', (flag) => {
    const config = budgetConfig(flag)
    expect(() => applyThinkingAdapter(
      { output_config: { effort: 'max', format: 'json' } },
      {
        protocol: 'anthropic',
        upstreamModel: 'claude-opus-5-5',
        maxOutputTokens: 64_000,
        config,
        reasoning: resolveModelThinking('off', config, 64_000),
        thinkingLevel: 'off'
      }
    )).toThrow(/不支持关闭推理/u)
  })

  it('legacy defaultEnabled false budget on opus-5.5 Auto stays enabled at medium', () => {
    const config = budgetConfig('omitted', false)
    const reasoning = resolveModelThinking('auto', config, 8_192)
    expect(reasoning).toMatchObject({ enabled: false, explicit: false })
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: 'claude-opus-5-5',
      maxOutputTokens: 8_192,
      config,
      reasoning,
      thinkingLevel: 'auto'
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: 'medium' } })
  })

  it('maps a budget-only explicit caller of 32000 tokens to xhigh', () => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: 'claude-opus-5',
      maxOutputTokens: 128_000,
      config: budgetConfig('omitted'),
      reasoning: { mode: 'budget', enabled: true, explicit: true, budgetTokens: 32_000 }
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: 'xhigh' } })
  })

  it('maps an unknown modern claude-opus-6-1 budget of 64000 to conservative high', () => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: 'claude-opus-6-1',
      maxOutputTokens: 128_000,
      config: budgetConfig('omitted'),
      reasoning: { mode: 'budget', enabled: true, explicit: true, budgetTokens: 64_000 }
    })).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: 'high' } })
  })

  it('throws for explicit higher when the effort list is only low, medium, and high', () => {
    const config = budgetConfig('omitted')
    const reasoningEfforts = ['low', 'medium', 'high'] as const
    expect(() => applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: 'claude-opus-5',
      maxOutputTokens: 8_192,
      config,
      reasoning: resolveModelThinking('higher', config, 8_192),
      thinkingLevel: 'higher',
      reasoningEfforts
    })).toThrow(/不支持推理强度「xhigh」/u)
  })

  it('throws when the adaptive capability list is empty', () => {
    expect(() => applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: 'claude-opus-5',
      maxOutputTokens: 8_192,
      config: budgetConfig('omitted'),
      reasoning: { mode: 'budget', enabled: true, explicit: true, budgetTokens: 32_000 },
      reasoningEfforts: []
    })).toThrow(/缺少可用推理强度/u)
  })

  it.each(['claude-opus-4-5', 'claude-opus-4-6'] as const)('%s manual budget stays enabled and does not add effort', (model) => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      upstreamModel: model,
      maxOutputTokens: 8_192,
      config: budgetConfig('omitted'),
      reasoning: { mode: 'budget', enabled: true, explicit: true, budgetTokens: 8_000 },
      thinkingLevel: 'high'
    })).toEqual({ thinking: { type: 'enabled', budget_tokens: 8_000 } })
  })

  it('standardWire and anthropicAdaptive false do not convert an opus-5 budget', () => {
    const reasoning: ResolvedModelThinking = { mode: 'budget', enabled: true, explicit: true, budgetTokens: 8_000 }
    for (const config of [
      { ...budgetConfig('omitted'), standardWire: true },
      { ...budgetConfig('omitted'), anthropicAdaptive: false }
    ] as const) {
      expect(applyThinkingAdapter({}, {
        protocol: 'anthropic',
        upstreamModel: 'claude-opus-5',
        maxOutputTokens: 8_192,
        config,
        reasoning,
        thinkingLevel: 'high'
      })).toEqual({ thinking: { type: 'enabled', budget_tokens: 8_000 } })
    }
  })

  it('a custom preset on thinking_budget stays on that custom path', () => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      preset: 'custom',
      upstreamModel: 'claude-opus-5',
      maxOutputTokens: 8_192,
      config: {
        mode: 'budget',
        defaultEnabled: true,
        defaultBudgetTokens: 64_000,
        parameterPath: 'thinking_budget'
      },
      reasoning: { mode: 'budget', enabled: true, explicit: true, budgetTokens: 8_000 },
      thinkingLevel: 'high'
    })).toEqual({ thinking_budget: 8_000 })
  })

  it.each([
    ['openai-chat', { reasoning_effort: 'high' }],
    ['openai-responses', { reasoning: { effort: 'high' } }]
  ] as const)('explicit %s preset stays in OpenAI format and does not adapt', (preset, expected) => {
    expect(applyThinkingAdapter({}, {
      protocol: 'anthropic',
      preset,
      upstreamModel: 'claude-opus-5-5',
      maxOutputTokens: 8_192,
      config: budgetConfig('true'),
      reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'high' },
      thinkingLevel: 'high'
    })).toEqual(expected)
  })

  it.each(['claude-opus-5', 'claude-opus-5-5'] as const)('%s higher at 1024 output stays xhigh instead of being treated as Off', (model) => {
    const config = budgetConfig('omitted')
    const reasoning = resolveModelThinking('higher', config, 1024)
    expect(reasoning).toMatchObject({ enabled: false, explicit: true })
    const input = {
      protocol: 'anthropic' as const,
      upstreamModel: model,
      maxOutputTokens: 1024,
      config,
      reasoning,
      thinkingLevel: 'higher' as const
    }
    const expected = { thinking: { type: 'adaptive' }, output_config: { effort: 'xhigh', format: 'json' } }
    const adapted = applyThinkingAdapter(
      { thinking: { type: 'enabled', budget_tokens: 64_000 }, output_config: { format: 'json' } },
      input
    )
    expect(adapted).toEqual(expected)
    expect(enforceThinkingPreference(adapted, input)).toEqual(expected)
  })
})

describe('resolved Anthropic adaptive bindings', () => {
  const bound = (upstreamModel: string) => modelBindingResolver().resolve({
    ...structuredClone(IMPORTED_ALIAS_DEFAULTS),
    providerId: 'relay', alias: 'claude', upstreamModel
  })

  const wire = (upstreamModel: string, level: 'auto' | 'higher' | 'off', body: unknown = {}) => {
    const alias = bound(upstreamModel)
    const reasoning = resolveModelThinking(level, alias.thinkingConfig, alias.maxOutputTokens, alias.reasoningEfforts)
    return applyThinkingAdapter(body, {
      protocol: 'anthropic',
      upstreamModel: alias.upstreamModel,
      maxOutputTokens: alias.maxOutputTokens,
      config: alias.thinkingConfig,
      reasoning,
      ...(alias.reasoningEfforts === undefined ? {} : { reasoningEfforts: alias.reasoningEfforts })
    })
  }

  it.each([
    ['claude-opus-4-6', 'high', null],
    ['claude-sonnet-4-6', 'high', null],
    ['claude-opus-4-7', 'high', 'xhigh'],
    ['claude-opus-5', 'high', 'xhigh'],
    ['claude-sonnet-5', 'high', 'xhigh'],
    ['claude-opus-5-5', 'medium', 'xhigh'],
    ['claude-haiku-5-5', 'medium', 'xhigh'],
    ['claude-sonnet-5-5', 'high', 'xhigh'],
    ['claude-fable-5', 'high', 'xhigh'],
    ['claude-mythos-5', 'high', 'xhigh']
  ] as const)('%s Auto is %s and higher is %s', (id, auto, higher) => {
    expect(wire(id, 'auto')).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: auto } })
    if (higher === null) {
      expect(() => wire(id, 'higher')).toThrow(/不支持推理强度「xhigh」/u)
    } else {
      expect(wire(id, 'higher')).toEqual({ thinking: { type: 'adaptive' }, output_config: { effort: higher } })
    }
  })

  it.each([
    'claude-opus-5-5',
    'claude-fable-5',
    'claude-fable-5-1',
    'claude-mythos-5',
    'claude-mythos-5-1',
    'claude-sonnet-5-5'
  ])('%s rejects Off', (id) => {
    expect(() => wire(id, 'off', {
      thinking: { type: 'adaptive' },
      output_config: { effort: 'max', format: 'json' }
    })).toThrow(/不支持关闭推理/u)
  })

  it.each([
    'claude-opus-5',
    'claude-haiku-5-5',
    'claude-sonnet-5',
    'claude-opus-4-7'
  ])('%s Off sends disabled, drops a stale max effort, and keeps the output format', (id) => {
    expect(wire(id, 'off', {
      thinking: { type: 'enabled', budget_tokens: 64_000 },
      output_config: { effort: 'max', format: 'json' }
    })).toEqual({
      thinking: { type: 'disabled' },
      output_config: { format: 'json' }
    })
  })

  it.each([
    ['claude-opus-5-5', ['auto', 'low', 'medium', 'high', 'higher', 'max']],
    ['claude-fable-5', ['auto', 'low', 'medium', 'high', 'higher', 'max']],
    ['claude-fable-5-1', ['auto', 'low', 'medium', 'high', 'higher', 'max']],
    ['claude-mythos-5', ['auto', 'low', 'medium', 'high', 'higher', 'max']],
    ['claude-mythos-5-1', ['auto', 'low', 'medium', 'high', 'higher', 'max']],
    ['claude-sonnet-5-5', ['auto', 'low', 'medium', 'high', 'higher', 'max']],
    ['claude-opus-4-6', ['auto', 'low', 'medium', 'high', 'max', 'off']],
    ['claude-sonnet-4-6', ['auto', 'low', 'medium', 'high', 'max', 'off']],
    ['claude-opus-4-7', ['auto', 'low', 'medium', 'high', 'higher', 'max', 'off']],
    ['claude-opus-5', ['auto', 'low', 'medium', 'high', 'higher', 'max', 'off']],
    ['claude-haiku-5-5', ['auto', 'low', 'medium', 'high', 'higher', 'max', 'off']],
    ['claude-sonnet-5', ['auto', 'low', 'medium', 'high', 'higher', 'max', 'off']]
  ] as const)('%s thinking levels follow the published efforts', (id, levels) => {
    const shown = modelThinkingLevels(bound(id))
    expect(shown).toEqual([...levels])
    if (!shown.includes('off')) {
      expect(shown).toContain('higher')
    }
  })
})

/* ================================================================
 * Ollama 语义(vendors/ollama.ts 的配置落到线上的形状)—— 2026-09-15
 * 逆向 ollama v0.34.0 定案:OpenAI 层吃 reasoning_effort('none'=关,
 * 其余档位一律折成 enable_thinking:true);Anthropic 层只认开/关两态。
 * ================================================================ */
describe('applyThinkingAdapter · Ollama', () => {
  /*
   * ★★ 用**出货常量**而不是手抄一份配置:覆盖路径(model-binding)与目录条目
   * 共用 `OLLAMA_STANDARD_THINKING`,测试也读同一个对象,三处不会分叉。
   * reasoning 的形状对齐 `resolveModelThinking` 在 effort 模式下真实的输出。
   */
  const off: ResolvedModelThinking = { mode: 'effort', enabled: false, explicit: true }
  const on: ResolvedModelThinking = { mode: 'effort', enabled: true, explicit: true, effort: 'high' }

  it('★★★ 显式关 → reasoning_effort:"none"(Ollama 的 think:false),且名字劫持被 standardWire 挡下', () => {
    const body = applyThinkingAdapter(
      { model: 'deepseek-v4-pro:0813' },
      {
        protocol: 'openai-chat',
        upstreamModel: 'deepseek-v4-pro:0813',
        maxOutputTokens: 16_384,
        config: OLLAMA_STANDARD_THINKING,
        reasoning: off
      }
    ) as Record<string, unknown>
    expect(body['reasoning_effort']).toBe('none')
    expect(body['enable_thinking']).toBeUndefined()
    // deepseek 官方方言的字段一个都不许冒出来(那正是它被静默丢弃的原因)
    expect(body['thinking']).toBeUndefined()
  })

  it('★★ 开 + 档位 → reasoning_effort 原样透传', () => {
    const body = applyThinkingAdapter(
      { model: 'qwen3.5:397b' },
      {
        protocol: 'openai-chat',
        upstreamModel: 'qwen3.5:397b',
        maxOutputTokens: 16_384,
        config: OLLAMA_STANDARD_THINKING,
        reasoning: on
      }
    ) as Record<string, unknown>
    expect(body['reasoning_effort']).toBe('high')
    expect(body['enable_thinking']).toBeUndefined()
  })

  it('★★★ standardWire 逃生口:同名模型声明后走标准分支,不声明时官方方言原样保留', () => {
    const base = {
      protocol: 'openai-chat' as const,
      upstreamModel: 'deepseek-v4-pro',
      maxOutputTokens: 16_384,
      reasoning: { mode: 'effort' as const, enabled: false, explicit: true }
    }
    // 官方 DeepSeek(不声明):方言分支 —— 关只写 thinking:{type:disabled}
    const dialect = applyThinkingAdapter(
      {},
      {
        ...base,
        config: { mode: 'effort', defaultEnabled: true, parameterPath: 'reasoning_effort' }
      }
    ) as Record<string, unknown>
    expect(dialect['thinking']).toEqual({ type: 'disabled' })
    expect(dialect['reasoning_effort']).toBeUndefined()
    // Ollama 托管(声明 standardWire):标准分支 —— 关写 reasoning_effort:'none'
    const standard = applyThinkingAdapter(
      {},
      {
        ...base,
        config: { mode: 'effort', defaultEnabled: true, parameterPath: 'reasoning_effort', standardWire: true }
      }
    ) as Record<string, unknown>
    expect(standard['reasoning_effort']).toBe('none')
    expect(standard['thinking']).toBeUndefined()
  })

  it('★★★ glm 名字同样被逃生口接管(Ollama 上的 glm-5.3 走 reasoning_effort)', () => {
    const body = applyThinkingAdapter(
      {},
      {
        protocol: 'openai-chat',
        upstreamModel: 'glm-5.3',
        maxOutputTokens: 16_384,
        config: { mode: 'toggle', defaultEnabled: false, parameterPath: 'reasoning_effort', standardWire: true },
        reasoning: { mode: 'toggle', enabled: false, explicit: true }
      }
    ) as Record<string, unknown>
    expect(body['reasoning_effort']).toBe('none')
    expect(body['thinking']).toBeUndefined()
  })

  it('★★★ Anthropic 路径两态:effort 配置也只落 thinking.type(档位/budget 被 Ollama 忽略)', () => {
    const enabled = applyThinkingAdapter(
      {},
      {
        protocol: 'anthropic',
        upstreamModel: 'gpt-oss:120b',
        maxOutputTokens: 32_768,
        config: { mode: 'effort', defaultEnabled: true, defaultEffort: 'medium', parameterPath: 'reasoning_effort' },
        reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'high' }
      }
    ) as Record<string, unknown>
    expect(enabled['thinking']).toEqual({ type: 'enabled', budget_tokens: expect.any(Number) })
    expect(enabled['reasoning_effort']).toBeUndefined()

    const disabled = applyThinkingAdapter(
      {},
      {
        protocol: 'anthropic',
        upstreamModel: 'gpt-oss:120b',
        maxOutputTokens: 32_768,
        config: { mode: 'effort', defaultEnabled: true, defaultEffort: 'medium', parameterPath: 'reasoning_effort' },
        reasoning: { mode: 'effort', enabled: false, explicit: true }
      }
    ) as Record<string, unknown>
    expect(disabled['thinking']).toEqual({ type: 'disabled' })
  })
})

/* ================================================================
 * standardWire 的 Anthropic 线形(Ollama /v1/messages)—— 档位走
 * output_config.effort,且**不能与 thinking.type 同发**:Ollama 源码里
 * output_config 那一支挂在 `think == nil` 上,同发的表现是档位被静默无视。
 * ================================================================ */
describe('applyThinkingAdapter · Ollama Anthropic 线形', () => {
  const ollamaAnthropic = {
    protocol: 'anthropic' as const,
    upstreamModel: 'glm-5.3',
    maxOutputTokens: 16_384,
    config: {
      mode: 'effort' as const,
      defaultEnabled: true,
      defaultEffort: 'medium' as const,
      parameterPath: 'reasoning_effort',
      standardWire: true
    }
  }

  it('★★★ 开 + 档位 → output_config.effort,且**没有** thinking 字段(同发会让档位失效)', () => {
    const body = applyThinkingAdapter(
      {},
      { ...ollamaAnthropic, reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'high' } }
    ) as Record<string, unknown>
    expect(body['output_config']).toEqual({ effort: 'high' })
    expect(body['thinking']).toBeUndefined()
  })

  it('★★★ 关 → thinking.type:disabled(output_config 表达不了「关」,只能走这一支)', () => {
    const body = applyThinkingAdapter(
      {},
      { ...ollamaAnthropic, reasoning: { mode: 'effort', enabled: false, explicit: true } }
    ) as Record<string, unknown>
    expect(body['thinking']).toEqual({ type: 'disabled' })
    expect(body['output_config']).toBeUndefined()
  })

  it('★★ 档位原样透传(max 也是 Ollama 认的档位)', () => {
    const body = applyThinkingAdapter(
      {},
      { ...ollamaAnthropic, reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'max' } }
    ) as Record<string, unknown>
    expect(body['output_config']).toEqual({ effort: 'max' })
  })

  it('★★ 不声明 standardWire 的 Anthropic 供应商照旧走 budget_tokens(零回归)', () => {
    const { standardWire: _omitted, ...config } = ollamaAnthropic.config
    const body = applyThinkingAdapter(
      {},
      {
        ...ollamaAnthropic,
        config,
        reasoning: { mode: 'effort', enabled: true, explicit: true, effort: 'high' }
      }
    ) as Record<string, unknown>
    expect(body['thinking']).toEqual({ type: 'enabled', budget_tokens: expect.any(Number) })
    expect(body['output_config']).toBeUndefined()
  })
})

/* ================================================================
 * 端到端:ollama-cloud 上的 glm-5.3 —— 用户报的「glm 没有思考强度」那条链,
 * 从目录命中一路走到最终请求体,四层各改一处就断在这条上。
 * ================================================================ */
describe('端到端 · ollama-cloud 上的 glm-5.3', () => {
  const alias = modelBindingResolver().resolve({
    ...structuredClone(IMPORTED_ALIAS_DEFAULTS),
    providerId: 'ollama-cloud',
    alias: 'glm',
    upstreamModel: 'glm-5.3'
  })

  it('★★★ 档位选择器拿得到强度(低/中/高/最高 + 关)', () => {
    expect(modelThinkingLevels(alias)).toEqual(['auto', 'low', 'medium', 'high', 'max', 'off'])
  })

  it('★★★ 「关」真的落到 reasoning_effort:none —— 而不是被丢弃的智谱方言字段', () => {
    const reasoning = resolveModelThinking('off', alias.thinkingConfig, alias.maxOutputTokens, alias.reasoningEfforts)
    expect(reasoning).toMatchObject({ enabled: false, explicit: true })
    const body = applyThinkingAdapter(
      { model: 'glm-5.3' },
      {
        protocol: 'openai-chat',
        upstreamModel: alias.upstreamModel,
        config: alias.thinkingConfig,
        reasoning,
        maxOutputTokens: alias.maxOutputTokens,
        reasoningEfforts: alias.reasoningEfforts
      }
    ) as Record<string, unknown>
    expect(body['reasoning_effort']).toBe('none')
    expect(body['thinking']).toBeUndefined()
  })

  it('★★ 选「高」→ reasoning_effort:high', () => {
    const reasoning = resolveModelThinking('high', alias.thinkingConfig, alias.maxOutputTokens, alias.reasoningEfforts)
    const body = applyThinkingAdapter(
      { model: 'glm-5.3' },
      {
        protocol: 'openai-chat',
        upstreamModel: alias.upstreamModel,
        config: alias.thinkingConfig,
        reasoning,
        maxOutputTokens: alias.maxOutputTokens,
        reasoningEfforts: alias.reasoningEfforts
      }
    ) as Record<string, unknown>
    expect(body['reasoning_effort']).toBe('high')
  })
})

/* ================================================================
 * 回传方言(Responses 协议的历史 reasoning item)—— 两家的输入侧约束互斥:
 * 官方 OpenAI 给 `content` 的上限是 0,DeepSeek 反过来要求正文全文回传。
 * 判错的症状是会话从某一轮起每轮 400,且本地零报错。
 * ================================================================ */
describe('reasoningReplayFor', () => {
  const effortConfig = { mode: 'effort' as const, defaultEnabled: true, parameterPath: 'reasoning_effort' }

  it('★★★ 默认走官方那条;deepseek 名字兜底走要求正文那条', () => {
    expect(reasoningReplayFor(undefined, 'gpt-5')).toBe('opaque-only')
    expect(reasoningReplayFor(effortConfig, 'gpt-5')).toBe('opaque-only')
    expect(reasoningReplayFor(effortConfig, 'deepseek-v4-pro')).toBe('text-required')
    expect(reasoningReplayFor(effortConfig, 'deepseek-v4-pro:0813')).toBe('text-required')
  })

  it('★★★ 显式声明压过名字启发式(声明了就不看模型名)', () => {
    expect(reasoningReplayFor({ ...effortConfig, reasoningReplay: 'text-required' }, 'some-relay-model'))
      .toBe('text-required')
  })

  it('★★★ standardWire(托管方的标准线形)压过名字:Ollama 上的 deepseek 不许被拉回官方方言', () => {
    expect(reasoningReplayFor(OLLAMA_STANDARD_THINKING, 'deepseek-v4-pro:0813')).toBe('opaque-only')
  })

  it('★ 显式声明本身也压过 standardWire(逃生口,顺序与 kindFor 一致)', () => {
    expect(reasoningReplayFor({ ...OLLAMA_STANDARD_THINKING, reasoningReplay: 'text-required' }, 'gpt-oss:120b')).toBe('text-required')
  })
})
