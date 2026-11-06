/**
 * 压缩三档来源(`compactModelSelection` / `compactThinkingSelection`)的用例。
 *
 * 需求:「设置页显示的那一档」和「请求真正发给谁」必须由同一段代码算出来 ——
 * 分头算的话两边只是碰巧一致,一旦不一致就是「设置里写着 A、账单上记着 B」,零报错。
 * 这里盯的是三个最容易写错的地方:
 * 1. 工作区那一档的 `null`(显式跟随会话)和 `undefined`(没配)是**两个不同的答案**;
 * 2. 别名和 providerId 必须成对,绝不拼出「A 家的别名 + B 家的锁」;
 * 3. `'inherit'` 落到会话档位,而不是某个写死的默认值。
 */
import { describe, expect, it } from 'vitest'
import { compactModelSelection, compactThinkingSelection } from '../compaction-model'
import { INHERIT_THINKING } from '../subagent-thinking'

const session = { model: 'claude-sonnet-4', modelProviderId: 'anthropic' }

describe('compactModelSelection', () => {
  it('falls back to the conversation model when nothing is configured', () => {
    expect(compactModelSelection(undefined, { model: '' }, session)).toEqual({
      model: 'claude-sonnet-4', modelProviderId: 'anthropic', followsSession: true
    })
  })

  it('uses the global setting when the workspace has not said anything', () => {
    expect(compactModelSelection(undefined, { model: 'gpt-6-luna', modelProviderId: 'openai' }, session)).toEqual({
      model: 'gpt-6-luna', modelProviderId: 'openai', followsSession: false
    })
  })

  it('lets the workspace override the global setting', () => {
    const workspace = { compactModel: 'glm-5', compactModelProviderId: 'zhipu' }
    expect(compactModelSelection(workspace, { model: 'gpt-6-luna', modelProviderId: 'openai' }, session)).toEqual({
      model: 'glm-5', modelProviderId: 'zhipu', followsSession: false
    })
  })

  /** ★ 这一条是工作区那一层存在的全部理由 —— 没有它就没法在某个工作区退回会话模型。 */
  it('★ null in the workspace means "follow the conversation model" and beats the global setting', () => {
    expect(compactModelSelection({ compactModel: null }, { model: 'gpt-6-luna', modelProviderId: 'openai' }, session))
      .toEqual({ model: 'claude-sonnet-4', modelProviderId: 'anthropic', followsSession: true })
  })

  it('treats an empty string in the workspace as "not configured", not as "follow the conversation"', () => {
    // 空串是「改回跟随全局设置」写进来的值(浅合并清不掉键),所以必须落到全局那一档。
    expect(compactModelSelection({ compactModel: '' }, { model: 'gpt-6-luna', modelProviderId: 'openai' }, session))
      .toEqual({ model: 'gpt-6-luna', modelProviderId: 'openai', followsSession: false })
  })

  it('never mixes an alias from one tier with a providerId from another', () => {
    // 工作区只给了别名(没锁供应商)—— 绝不能顺手把会话那家的 providerId 带上。
    expect(compactModelSelection({ compactModel: 'glm-5' }, { model: '', modelProviderId: 'openai' }, session))
      .toEqual({ model: 'glm-5', modelProviderId: undefined, followsSession: false })
  })
})

describe('compactThinkingSelection', () => {
  it('follows the conversation level when both tiers say inherit', () => {
    expect(compactThinkingSelection(undefined, INHERIT_THINKING, 'high')).toBe('high')
  })

  it('prefers the workspace level over the global one', () => {
    expect(compactThinkingSelection('low', 'max', 'high')).toBe('low')
  })

  it('uses the global level when the workspace has not said anything', () => {
    expect(compactThinkingSelection(undefined, 'minimal', 'high')).toBe('minimal')
  })

  it('treats null in the workspace as "follow the global setting"', () => {
    // null = 「改回跟随全局」写进来的值,和缺席同义。
    expect(compactThinkingSelection(null, 'minimal', 'high')).toBe('minimal')
  })

  it('lets the workspace pick inherit even when the global setting is explicit', () => {
    expect(compactThinkingSelection(INHERIT_THINKING, 'minimal', 'off')).toBe('off')
  })
})
