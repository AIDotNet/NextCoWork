/**
 * `model-groups.ts` —— 供应商 / 模型的统一数据源。
 *
 * 钉住的都是「之前各处各写一份、写法不一致」的那几条规则。
 */
import { describe, expect, it } from 'vitest'
import type { ModelAlias, UpstreamProvider } from '../provider'
import {
  availableBindings,
  isBindingAvailable,
  matchesModelQuery,
  modelBindingOptions,
  modelDisplayName,
  providerModelGroups
} from '../model-groups'

const prov = (id: string, enabled = true): UpstreamProvider => ({
  id, name: id.toUpperCase(), protocol: 'openai-chat', baseUrl: 'https://x', credentialRef: `cred:${id}`, priority: 0, enabled
})
const model = (alias: string, providerId: string, extra: Partial<ModelAlias> = {}): ModelAlias => ({
  alias, providerId, upstreamModel: alias,
  capabilities: { tools: true, vision: false, thinking: false, caching: false },
  contextWindow: 200_000, maxOutputTokens: 8192, ...extra
})

describe('isBindingAvailable', () => {
  it('供应商启用 + 别名没停用 才可用', () => {
    expect(isBindingAvailable({}, { enabled: true })).toBe(true)
    expect(isBindingAvailable({ enabled: false }, { enabled: true })).toBe(false)
    expect(isBindingAvailable({}, { enabled: false })).toBe(false)
  })

  it('★ 供应商已删除(查不到)= 不可用 —— 之前有一处写成 `?.enabled !== false`,把它当成了可用', () => {
    expect(isBindingAvailable({}, undefined)).toBe(false)
  })
})

describe('availableBindings', () => {
  it('保序过滤,并支持模态过滤', () => {
    const models = [model('a', 'p'), model('img', 'p', { modality: 'image' }), model('b', 'off'), model('c', 'p', { enabled: false })]
    expect(availableBindings([prov('p'), prov('off', false)], models).map((m) => m.alias)).toEqual(['a', 'img'])
    expect(availableBindings([prov('p')], models, (m) => m.modality !== 'image').map((m) => m.alias)).toEqual(['a'])
  })
})

describe('providerModelGroups 与 modelBindingOptions 同一条判据', () => {
  it('选择器分组里出现的绑定 == 下拉选项里出现的绑定', () => {
    const providers = [prov('a'), prov('b', false), prov('c')]
    const models = [model('x', 'a'), model('y', 'a', { enabled: false }), model('x', 'b'), model('z', 'c')]
    const fromGroups = providerModelGroups(providers, models).flatMap((g) => g.models.map((m) => `${m.providerId}/${m.alias}`))
    const fromOptions = modelBindingOptions(providers, models).map((o) => o.value)
    expect(fromOptions).toEqual(fromGroups)
  })
})

describe('modelBindingOptions 标签', () => {
  const providers = [prov('a'), prov('b')]
  it("'always':总是带供应商", () => {
    expect(modelBindingOptions(providers, [model('x', 'a')]).map((o) => o.label)).toEqual(['x · A'])
  })
  it("'onConflict':只有撞名时才带", () => {
    const opts = modelBindingOptions(providers, [model('x', 'a'), model('x', 'b'), model('y', 'a')], { providerLabel: 'onConflict' })
    expect(opts.map((o) => o.label)).toEqual(['x · A', 'x · B', 'y'])
  })
})

describe('modelDisplayName / matchesModelQuery', () => {
  it('展示名为空白时回落到别名', () => {
    expect(modelDisplayName({ alias: 'a', displayName: '  ' })).toBe('a')
    expect(modelDisplayName({ alias: 'a', displayName: 'Alpha' })).toBe('Alpha')
  })
  it('展示名 / 别名 / 上游名 / 供应商名都能搜到', () => {
    const m = model('ds', 'p', { displayName: 'DeepSeek V4', upstreamModel: 'deepseek-v4-flash' })
    for (const q of ['deepseek v4', 'ds', 'flash', 'P']) expect(matchesModelQuery(m, prov('p'), q)).toBe(true)
    expect(matchesModelQuery(m, prov('p'), 'claude')).toBe(false)
  })
})
