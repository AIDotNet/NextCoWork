/**
 * 压缩绑定解析(`resolveCompactBinding`)的用例 —— 自动压缩和手动 /compact 共用它。
 *
 * 需求:这一层回答四个问题,任何一个答错都是静默故障:
 * 1. 三档来源(工作区 > 全局 > 会话)取哪一条;
 * 2. 配置的模型**当前解析不到**时怎么办 —— 必须静默回落会话模型,不判失败。
 *    判失败的话表现就是「上下文一路涨到上游报超长」,正是压缩重写前那次事故的形状;
 * 3. 档位按压缩模型归一化(关不掉推理的模型降到最低档,绝不抛);
 * 4. 输出额度和预裁窗口按**压缩模型**算,不是会话模型。
 */
import { describe, expect, it, vi } from 'vitest'
import type { ModelAlias } from '../../../../shared/domain/provider'
import { INHERIT_THINKING } from '../../../../shared/domain/subagent-thinking'
import { resolveCompactBinding, type CompactionSettings } from '../binding'

function alias(over: Partial<ModelAlias> = {}): ModelAlias {
  return {
    alias: 'claude-sonnet-4',
    providerId: 'anthropic',
    upstreamModel: 'claude-sonnet-4',
    capabilities: { tools: true, vision: false, thinking: true, caching: false },
    contextWindow: 200_000,
    maxOutputTokens: 64_000,
    thinkingConfig: { mode: 'budget', defaultEnabled: true },
    ...over
  }
}

/** gpt-6-* 那一族:effort 模型,`reasoningEfforts` 里没有 `none` —— 关不掉推理。 */
const gpt6 = alias({
  alias: 'gpt-6-astra',
  providerId: 'openai',
  upstreamModel: 'gpt-6-astra',
  contextWindow: 1_050_000,
  thinkingConfig: { mode: 'effort', defaultEnabled: true },
  reasoningEfforts: ['low', 'medium', 'high', 'xhigh', 'max']
})

function resolve(over: Parameters<typeof resolveCompactBinding>[0] extends infer T
  ? Partial<Omit<T, 'resolveModel'>> & { resolveModel?: (m: string, p?: string) => ModelAlias | undefined }
  : never = {}): ReturnType<typeof resolveCompactBinding> {
  return resolveCompactBinding({
    resolveModel: over.resolveModel ?? ((model) => (model === 'claude-sonnet-4' ? alias() : undefined)),
    session: { model: 'claude-sonnet-4', modelProviderId: 'anthropic', thinking: 'high' },
    maxOutputTokens: 32_000,
    ...over
  })
}

describe('resolveCompactBinding', () => {
  it('follows the conversation model when nothing is configured', () => {
    const binding = resolve()
    expect(binding?.model).toBe('claude-sonnet-4')
    expect(binding?.modelProviderId).toBe('anthropic')
    expect(binding?.fellBack).toBe(false)
    // inherit(出厂)→ 会话这一轮的档位。
    expect(binding?.thinking).toBe('high')
  })

  it('sends the summary to the configured model and sizes it by that model', () => {
    const binding = resolve({
      settings: { model: 'gpt-6-astra', modelProviderId: 'openai', thinking: INHERIT_THINKING },
      resolveModel: (model) => (model === 'gpt-6-astra' ? gpt6 : alias())
    })
    expect(binding?.model).toBe('gpt-6-astra')
    expect(binding?.protocolWindow).toBe(1_050_000)
    expect(binding?.maxOutputTokens).toBe(32_000)
  })

  /**
   * ★★ 这一条就是本次要修的那个 bug 的判据。
   * 会话档位是 high、压缩模型是 gpt-6(关不掉推理)时,过去这里会硬发 'off',
   * 而 thinking-adapter 对「effort 模型 + 没有 none」直接抛不可重试的错。
   */
  it('★ never asks a reasoning-only-ish model for a level it does not accept', () => {
    const binding = resolve({
      settings: { model: 'gpt-6-astra', modelProviderId: 'openai', thinking: 'off' },
      resolveModel: () => gpt6
    })
    expect(binding?.thinking).toBe('low')
  })

  it('clamps the output budget to the compaction model window', () => {
    const tiny = alias({ alias: 'tiny', contextWindow: 8_000 })
    const binding = resolve({ resolveModel: () => tiny, maxOutputTokens: 32_000 })
    expect(binding?.maxOutputTokens).toBe(8_000)
  })

  /** ★ 过期配置绝不能把压缩打死 —— 那正是「上下文一路涨」的老症状。 */
  it('★ falls back to the conversation model (with its providerId) when the configured one is gone', () => {
    const warn = vi.fn()
    const binding = resolve({
      settings: { model: 'deleted-alias', modelProviderId: 'gone', thinking: INHERIT_THINKING },
      resolveModel: (model) => (model === 'claude-sonnet-4' ? alias() : undefined),
      warn
    })
    expect(binding?.model).toBe('claude-sonnet-4')
    // 成对回落:绝不留下「会话的别名 + 配置那家的锁」。
    expect(binding?.modelProviderId).toBe('anthropic')
    expect(binding?.fellBack).toBe(true)
    expect(warn).toHaveBeenCalledOnce()
  })

  it('returns undefined only when the conversation model itself cannot be resolved', () => {
    expect(resolve({ resolveModel: () => undefined })).toBeUndefined()
  })

  it('lets the workspace override take precedence, including an explicit follow-session', () => {
    const settings: CompactionSettings = {
      model: 'gpt-6-astra', modelProviderId: 'openai', thinking: INHERIT_THINKING
    }
    const resolveModel = (model: string): ModelAlias | undefined =>
      model === 'gpt-6-astra' ? gpt6 : alias()
    expect(resolve({ settings, resolveModel, workspace: { compactModel: null } })?.model).toBe('claude-sonnet-4')
    expect(resolve({ settings, resolveModel, workspace: { compactThinking: 'medium' } })?.thinking).toBe('medium')
    // 工作区选了一个压缩模型不认的档位(gpt-6 没有 minimal):照样归一化到最近可用档,不抛。
    expect(resolve({ settings, resolveModel, workspace: { compactThinking: 'minimal' } })?.thinking).toBe('low')
  })
})
