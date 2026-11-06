/**
 * 圆环菜单里压缩那两栏的取值编解码。
 *
 * 需求:工作区这一层是三态(跟随全局 / 显式跟随会话模型 / 指定一个模型),而下拉的
 * value 只能是字符串。编解码错一处的表现都是静默的:
 * - 把 `null` 和缺席混为一谈 → 「显式跟随会话模型」退化成「跟随全局」,用户在这个
 *   工作区就再也反盖不了全局配的压缩模型;
 * - 「改回跟随全局」写成省略键 → 浅合并保留旧值,那一项**点了没反应**;
 * - 只写别名不写 providerId → 「新别名 + 旧供应商」,候选集为空。
 */
import { describe, expect, it } from 'vitest'
import {
  compactModelOptions,
  compactModelPatch,
  compactModelValue,
  compactThinkingPatch,
  compactThinkingValue,
  FOLLOW_GLOBAL,
  FOLLOW_SESSION
} from '../compact-choice'

describe('compactModelValue', () => {
  it('maps the three states onto three distinct dropdown values', () => {
    expect(compactModelValue(undefined)).toBe(FOLLOW_GLOBAL)
    expect(compactModelValue({})).toBe(FOLLOW_GLOBAL)
    expect(compactModelValue({ compactModel: '' })).toBe(FOLLOW_GLOBAL)
    expect(compactModelValue({ compactModel: null })).toBe(FOLLOW_SESSION)
    expect(compactModelValue({ compactModel: 'glm-5', compactModelProviderId: 'zhipu' })).toBe('zhipu/glm-5')
  })

  it('round-trips an alias that itself contains a slash', () => {
    // `openrouter/claude-sonnet-4` 这种别名是真实存在的 —— 分隔符只切第一个斜杠。
    const value = compactModelValue({ compactModel: 'openrouter/claude-sonnet-4', compactModelProviderId: 'or' })
    expect(compactModelPatch(value)).toEqual({
      compactModel: 'openrouter/claude-sonnet-4', compactModelProviderId: 'or'
    })
  })
})

describe('compactModelPatch', () => {
  /** ★ 「改回跟随全局」必须写一个值进去:浅合并下省略一个键等于什么都没改。 */
  it('★ writes an empty string for follow-global instead of omitting the key', () => {
    expect(compactModelPatch(FOLLOW_GLOBAL)).toEqual({ compactModel: '', compactModelProviderId: undefined })
  })

  it('writes null for the explicit follow-the-conversation choice', () => {
    expect(compactModelPatch(FOLLOW_SESSION)).toEqual({ compactModel: null, compactModelProviderId: undefined })
  })

  /** ★ providerId 恒在返回值里(哪怕是 undefined)—— 少给一个就会留下旧供应商。 */
  it('★ always returns both halves of the pair', () => {
    expect(compactModelPatch('/bare-alias')).toEqual({
      compactModel: 'bare-alias', compactModelProviderId: undefined
    })
  })
})

describe('compactModelOptions', () => {
  const models = [
    { alias: 'a', providerId: 'p1' },
    { alias: 'b', providerId: 'p2', enabled: false },
    { alias: 'c', providerId: 'p3' }
  ]
  const providers = [
    { id: 'p1', name: 'Alpha' },
    { id: 'p2', name: 'Beta' },
    { id: 'p3', name: 'Gamma', enabled: false }
  ]

  it('lists one option per usable binding and skips disabled ones', () => {
    // 画出来的每个控件都是一次会失败的承诺 —— 停用的绑定不列。
    expect(compactModelOptions(models, providers)).toEqual([{ value: 'p1/a', label: 'a · Alpha' }])
  })
})

describe('compactThinking codec', () => {
  it('treats both undefined and null as follow-global', () => {
    expect(compactThinkingValue(undefined)).toBe(FOLLOW_GLOBAL)
    expect(compactThinkingValue({ compactThinking: null })).toBe(FOLLOW_GLOBAL)
    expect(compactThinkingValue({ compactThinking: 'inherit' })).toBe('inherit')
    expect(compactThinkingValue({ compactThinking: 'low' })).toBe('low')
  })

  it('★ writes null for follow-global, and never lets an unknown level reach the database', () => {
    expect(compactThinkingPatch(FOLLOW_GLOBAL)).toEqual({ compactThinking: null })
    expect(compactThinkingPatch('inherit')).toEqual({ compactThinking: 'inherit' })
    expect(compactThinkingPatch('deep')).toEqual({ compactThinking: null })
  })
})
