import { describe, expect, it } from 'vitest'
import { findPricing, priceOf } from '../../shared/domain/pricing'
import { PRICING_SEED } from '../../shared/domain/pricing-seed'
import { CLIENT_PROVIDER_ID } from '../../shared/domain/presets'
import { priceUsageAttempt, resolveUsagePricing, resolveUsagePricingModelId } from '../runtime'

describe('resolveUsagePricingModelId', () => {
  it.each(['deepseek-v4.1-flash-1017', 'deepseek/deepseek-v4.1-flash-1017', 'DeepSeek-V4.1-Flash-1017', 'deepseek-flash'])(
    'resolves %s to the 1017 price without reusing the legacy Flash price', (upstreamModel) => {
      expect(resolveUsagePricingModelId(upstreamModel)).toBe('deepseek-v4.1-flash-1017')
    }
  )

  it('resolves an aggregator-prefixed Kimi alias to its official pricing model id', () => {
    const modelId = resolveUsagePricingModelId('moonshot/kimi-k3')

    expect(modelId).toBe('kimi-k3')
    expect(findPricing(PRICING_SEED, 'moonshot', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      modelId: 'kimi-k3',
    })
  })

  it('resolves a case-normalized MiniMax alias to the canonical pricing model id', () => {
    const modelId = resolveUsagePricingModelId('minimax-m3')

    expect(modelId).toBe('MiniMax-M3')
    expect(findPricing(PRICING_SEED, 'minimax', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      modelId: 'MiniMax-M3',
    })
  })

  it('resolves Doubao endpoint versions to the official pricing product id', () => {
    const modelId = resolveUsagePricingModelId('doubao-seed-2-1-pro-260915')

    expect(modelId).toBe('doubao-seed-2.1-pro')
    expect(findPricing(PRICING_SEED, 'volcengine', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      providerId: null,
      modelId: 'doubao-seed-2.1-pro',
      currency: 'CNY',
    })
  })

  it('resolves ERNIE version aliases to their shared official price row', () => {
    const modelId = resolveUsagePricingModelId('ernie-5.0-thinking-latest')

    expect(modelId).toBe('ernie-5.0')
    expect(findPricing(PRICING_SEED, 'qianfan', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      providerId: null,
      modelId: 'ernie-5.0',
      currency: 'CNY',
    })
  })

  it('resolves the former lowercase LongCat id to the official canonical price row', () => {
    const modelId = resolveUsagePricingModelId('longcat-2.0')

    expect(modelId).toBe('LongCat-2.0')
    expect(findPricing(PRICING_SEED, 'longcat', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      providerId: null,
      modelId: 'LongCat-2.0',
      currency: 'USD',
      tiers: [{ upToInputTokens: null, rate: { input: 0.3, output: 1.2, cacheRead: 0.006 } }],
    })
  })

  it('resolves DeepSeek release aliases to the connection-independent official price', () => {
    const modelId = resolveUsagePricingModelId('DeepSeek-V4-Pro-0813')

    expect(modelId).toBe('deepseek-v4-pro')
    expect(findPricing(PRICING_SEED, 'deepseek', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      providerId: null,
      modelId: 'deepseek-v4-pro',
      currency: 'USD',
      tiers: [
        {
          rate: {
            input: 0.66,
            output: 1.98,
            cacheRead: 0.022,
          },
        },
      ],
    })
  })

  it('resolves a prefixed openPangu id to the connection-independent official Flash price', () => {
    const modelId = resolveUsagePricingModelId('huaweicloud/openpangu-2.0-flash')

    expect(modelId).toBe('openpangu-2.0-flash')
    expect(findPricing(PRICING_SEED, 'huaweicloud', modelId, Date.parse('2026-09-05T00:00:00Z'))).toMatchObject({
      providerId: null,
      modelId: 'openpangu-2.0-flash',
      currency: 'CNY',
      tiers: [{ upToInputTokens: null, rate: { input: 0.8, output: 1.6, cacheRead: 0.2 } }],
    })
  })

  it('keeps an unknown upstream model id unchanged', () => {
    expect(resolveUsagePricingModelId('custom/vendor-model')).toBe('custom/vendor-model')
  })
})

describe('resolveUsagePricing', () => {
  const at = Date.parse('2026-09-05T00:00:00Z')
  const modelId = resolveUsagePricingModelId('deepseek-v4-pro')

  /*
    ★ `findPricing` 查不到 `(providerId, modelId)` 会退回 `(null, modelId)` 那条
    通用价 —— 自建供应商都走这条路。这个回退是**故意**的,钉住它是为了
    「查不到价」那条测试不至于因为回退悄悄没了而变成测了个寂寞。
  */
  it('自建供应商查不到自己的价,会命中通用价行', () => {
    const generic = resolveUsagePricing('custom-my-relay', modelId, at)
    expect(generic).not.toBeNull()
    expect(generic?.providerId).toBeNull()
  })

  it('查不到价时是 null,而不是一个看着挺像样的 0', () => {
    expect(resolveUsagePricing('custom-my-relay', '不存在的模型', at)).toBeNull()
    expect(findPricing(PRICING_SEED, 'custom-my-relay', '不存在的模型', at)).toBeNull()
  })
})

/*
  ★ 同一次请求有**两条**出口:落盘的 `usage_records.cost_micros`(设置页用量表读它)
  和随 `message_end` 推给聊天页的实时金额。两处各写一遍计价,迟早会在别名归一化或
  时段判定上分叉 —— 那时同一轮的钱在两个页面上是两个数,而且都很像样。
  所以两条出口共用这一个函数,下面钉的就是它的两个关键行为。
*/
describe('priceUsageAttempt', () => {
  const usage = { inputTokens: 1_000_000, outputTokens: 0 }

  it.each([
    ['2026-10-17T02:00:00Z', 753_000], // 周末空闲价
    ['2026-10-19T00:00:00Z', 753_000],
    ['2026-10-19T01:00:00Z', 1_506_000], // 第一段高峰起点
    ['2026-10-19T04:00:00Z', 753_000],
    ['2026-10-19T06:00:00Z', 1_506_000], // 第二段高峰起点
    ['2026-10-19T10:00:00Z', 753_000],
  ])('1017 在 %s 的 NextCoWork 渠道和官方渠道都采用基础分时价', (iso, expectedMicros) => {
    const at = Date.parse(iso)
    const tokens = { inputTokens: 1_000_000, outputTokens: 1_000_000, cacheReadInputTokens: 1_000_000 }
    const nextcowork = priceUsageAttempt(CLIENT_PROVIDER_ID, 'deepseek-v4.1-flash-1017', tokens, at)
    const official = priceUsageAttempt('deepseek', 'deepseek-flash', tokens, at)

    expect(nextcowork).toMatchObject({ micros: expectedMicros, currency: 'USD' })
    expect(official).toMatchObject({ micros: expectedMicros, currency: 'USD' })
  })

  it('走的是和落盘同一条别名归一化 → 查表 → 计价链', () => {
    const at = Date.parse('2026-09-05T00:00:00Z')
    const priced = priceUsageAttempt('custom-my-relay', 'deepseek-v4-pro', usage, at)

    const pricing = resolveUsagePricing('custom-my-relay', resolveUsagePricingModelId('deepseek-v4-pro'), at)
    expect(pricing).not.toBeNull()
    expect(priced).toEqual(priceOf(pricing!, usage, at))
  })

  /*
    ★★ `at` 必须一路传到时段判定里。DeepSeek 的高峰档是 ×2,拿错时刻算出来的数
    完全合理 —— 只是贵一倍或便宜一半,没有任何人会发现。
  */
  it('时段价按传进来的时刻算,不是按「现在」', () => {
    const peak = Date.parse('2026-09-07T02:00:00Z') // 周一 —— 落在高峰窗口内
    const idle = Date.parse('2026-09-07T00:00:00Z') // 同一天,窗口之前

    const atPeak = priceUsageAttempt('custom-my-relay', 'deepseek-v4-pro', usage, peak)
    const atIdle = priceUsageAttempt('custom-my-relay', 'deepseek-v4-pro', usage, idle)

    expect(atIdle?.micros).toBeGreaterThan(0)
    expect(atPeak?.micros).toBe(atIdle!.micros * 2)
    expect(atPeak?.window).toBeDefined()
    expect(atIdle?.window).toBeUndefined()
  })

  it('查不到价时返回 null —— 界面据此整行不画', () => {
    expect(priceUsageAttempt('custom-my-relay', '不存在的模型', usage, Date.now())).toBeNull()
  })
})
