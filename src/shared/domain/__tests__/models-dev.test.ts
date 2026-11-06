import { describe, expect, it } from 'vitest'
import { diffPricing, mergePricing, parseModelsDev } from '../models-dev'
import type { ModelPricing } from '../pricing'
import { PRICING_SEED } from '../pricing-seed'

const AT = Date.parse('2026-10-14T00:00:00Z')

const api = {
  google: {
    models: {
      'gemini-3.1-pro-preview': {
        id: 'gemini-3.1-pro-preview',
        name: 'Gemini 3.1 Pro Preview',
        cost: {
          input: 2,
          output: 12,
          cache_read: 0.2,
          tiers: [{ input: 4, output: 18, cache_read: 0.4, tier: { type: 'context', size: 200000 } }],
          context_over_200k: { input: 4, output: 18, cache_read: 0.4 }
        }
      },
      'gemini-4-argon': { id: 'gemini-4-argon', name: 'Gemini 4 Argon', cost: { input: 4, output: 20, cache_read: 0.2 } },
      'no-cost': { id: 'no-cost', name: 'x' },
      'half-cost': { id: 'half-cost', name: 'x', cost: { input: 1 } }
    }
  },
  // 不在白名单里的厂商不收(币种未知)
  zhipuai: { models: { 'glm-x': { id: 'glm-x', name: 'GLM', cost: { input: 1, output: 2 } } } }
}

describe('parseModelsDev', () => {
  const rows = parseModelsDev(api, '2026-10-14')

  it('只收白名单厂商里有 input/output 的模型', () => {
    expect(rows.map((r) => r.modelId)).toEqual(['gemini-3.1-pro-preview', 'gemini-4-argon'])
  })

  it('tiers 按「超过 size 之后」的价生成阶梯,末档无上界', () => {
    const pro = rows[0]!
    expect(pro.tiers).toEqual([
      { upToInputTokens: 200000, rate: { input: 2, output: 12, cacheRead: 0.2 } },
      { upToInputTokens: null, rate: { input: 4, output: 18, cacheRead: 0.4 } }
    ])
    expect(rows[1]!.tiers).toEqual([{ upToInputTokens: null, rate: { input: 4, output: 20, cacheRead: 0.2 } }])
  })

  it('整体不是对象时抛', () => {
    expect(() => parseModelsDev([], '2026-10-14')).toThrow()
  })
})

describe('diffPricing / mergePricing', () => {
  const rows = parseModelsDev(api, '2026-10-14')

  it('与种子一致的不出现;价不同是 changed,种子没有的是 new', () => {
    // 3.1 Pro 与种子逐值一致;argon 的种子在 10 月是 2/10(12 月才翻倍),候选是 4/20
    expect(diffPricing(PRICING_SEED, rows, AT).map((i) => [i.modelId, i.kind])).toEqual([['gemini-4-argon', 'changed']])
    expect(diffPricing([], rows, AT).map((i) => i.kind)).toEqual(['new', 'new'])
  })

  it('价格不同是 changed,并带上 models.dev 没有的 cacheWrite1h', () => {
    const current: ModelPricing = {
      providerId: null, modelId: 'm', displayName: 'M', currency: 'USD', modality: 'text',
      tiers: [{ upToInputTokens: null, rate: { input: 1, output: 2, cacheWrite1h: 9 } }], source: 's', fetchedAt: '2026-01-01'
    }
    const incoming = { ...current, tiers: [{ upToInputTokens: null, rate: { input: 1, output: 3 } }] }
    const [item] = diffPricing([current], [incoming], AT)
    expect(item?.kind).toBe('changed')
    expect(item?.incoming.tiers[0]?.rate).toEqual({ input: 1, output: 3, cacheWrite1h: 9 })
  })

  it('同步行让同 (provider, model) 的种子行整组让位', () => {
    const synced = rows[1]!
    const merged = mergePricing([...PRICING_SEED, { ...synced, source: 'old' }], [synced])
    expect(merged.filter((p) => p.modelId === 'gemini-4-argon')).toEqual([synced])
    expect(merged.length).toBe(PRICING_SEED.length + 1 - PRICING_SEED.filter((p) => p.modelId === 'gemini-4-argon').length)
  })
})
