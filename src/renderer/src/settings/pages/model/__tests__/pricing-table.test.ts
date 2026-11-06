import { describe, expect, it } from 'vitest'
import { PRICING_SEED } from '../../../../../../shared/domain/pricing-seed'
import type { ModelPricing, PriceTier, PriceWindow } from '../../../../../../shared/domain/pricing'
import { compactTokens, describeDays, describeEffective, describeWindow, formatRate, groupPricing, matchPricing, selectCatalogPricing, tierLabel } from '../pricing-table'

const seedRow = (modelId: string, providerId: string | null = null) => {
  const found = PRICING_SEED.find((p) => p.modelId === modelId && p.providerId === providerId)
  if (found === undefined) throw new Error(`种子表里没有 ${providerId ?? '通用'}/${modelId}`)
  return found
}

describe('selectCatalogPricing', () => {
  const base = PRICING_SEED[0] as ModelPricing
  const at = (date: string): number => Date.parse(`${date}T12:00:00Z`)
  const row = (providerId: string | null, modelId: string, input: number, effectiveFrom?: string, effectiveUntil?: string): ModelPricing => ({
    ...base,
    providerId,
    modelId,
    tiers: [{ upToInputTokens: null, rate: { input, output: input * 2 } }],
    ...(effectiveFrom === undefined ? {} : { effectiveFrom }),
    ...(effectiveUntil === undefined ? {} : { effectiveUntil }),
  })

  it('按日期选择当前有效价格，不会在调价后继续显示旧价', () => {
    const rows = [row(null, 'dated-model', 0.75, undefined, '2026-12-31'), row(null, 'dated-model', 1.5, '2027-01-01')]
    const model = { id: 'dated-model', providerIds: [] }

    expect(selectCatalogPricing(rows, model, at('2026-09-05'))?.tiers[0]?.rate.input).toBe(0.75)
    expect(selectCatalogPricing(rows, model, at('2027-01-01'))?.tiers[0]?.rate.input).toBe(1.5)
  })

  it('模型管理行能直接命中 Gemini 3.8 Flash 当期官方价', () => {
    const model = { id: 'gemini-3.8-flash', providerIds: [] }

    expect(selectCatalogPricing(PRICING_SEED, model, at('2026-09-05'))?.tiers[0]?.rate).toEqual({
      input: 0.75,
      output: 3.75,
      cacheRead: 0.075,
    })
    expect(selectCatalogPricing(PRICING_SEED, model, at('2027-01-01'))?.tiers[0]?.rate).toEqual({
      input: 1.5,
      output: 7.5,
      cacheRead: 0.15,
    })
  })

  it('DeepSeek 模型无需供应商绑定也能命中通用官方 USD 价', () => {
    const unconfigured = { id: 'deepseek-v4-flash', providerIds: [] }
    const configured = { id: 'deepseek-v4-flash', providerIds: ['deepseek'] }

    for (const model of [unconfigured, configured]) {
      const pricing = selectCatalogPricing(PRICING_SEED, model, at('2026-09-05'))
      expect(pricing?.providerId).toBeNull()
      expect(pricing?.currency).toBe('USD')
      expect(pricing?.tiers[0]?.rate).toEqual({
        input: 0.22,
        output: 0.66,
        cacheRead: 0.007,
      })
    }
  })

  it('只使用已绑定供应商的覆盖价，未绑定时不会把专属价冒充通用价', () => {
    const rows = [row('opencode-go', 'covered-model', 0.1)]

    expect(selectCatalogPricing(rows, { id: 'covered-model', providerIds: [] }, at('2026-09-05'))).toBeUndefined()
    expect(selectCatalogPricing(rows, { id: 'covered-model', providerIds: ['another-provider'] }, at('2026-09-05'))).toBeUndefined()
    expect(selectCatalogPricing(rows, { id: 'covered-model', providerIds: ['opencode-go'] }, at('2026-09-05'))?.tiers[0]?.rate.input).toBe(0.1)
  })

  it('已绑定供应商没有覆盖价时回退到通用基础价', () => {
    const rows = [row(null, 'fallback-model', 1), row('some-provider', 'another-model', 9)]

    expect(selectCatalogPricing(rows, { id: 'fallback-model', providerIds: ['some-provider'] }, at('2026-09-05'))?.tiers[0]?.rate.input).toBe(1)
  })

  it('有通用官方价时稳定显示通用价，不随区域连接顺序变化', () => {
    const rows = [row(null, 'regional-model', 3), row('cn-provider', 'regional-model', 20)]

    for (const providerIds of [
      ['cn-provider', 'global-provider'],
      ['global-provider', 'cn-provider'],
    ]) {
      const pricing = selectCatalogPricing(rows, { id: 'regional-model', providerIds }, at('2026-09-05'))
      expect(pricing?.providerId).toBeNull()
      expect(pricing?.tiers[0]?.rate.input).toBe(3)
    }
  })

  it('国内厂商逐款核实的旗舰价在未绑定供应商时都能显示', () => {
    const models = [
      ['deepseek-v4-pro', undefined, 0.66, 'USD'],
      ['glm-5.3', undefined, 1.4, 'USD'],
      ['qwen3.8-max', undefined, 2, 'USD'],
      ['kimi-k3', undefined, 3, 'USD'],
      ['MiniMax-M3', undefined, 0.3, 'USD'],
      ['mimo-v2.5-pro', undefined, 0.435, 'USD'],
      ['step-3.7-flash', undefined, 1.35, 'CNY'],
      ['Baichuan-M3-Plus', undefined, 5, 'CNY'],
      ['doubao-seed-2-1-pro-260915', 'doubao-seed-2.1-pro', 3, 'CNY'],
      ['ernie-5.1', undefined, 4, 'CNY'],
      ['hy4-preview', undefined, 6, 'CNY'],
      ['SenseNova-V6-5-Pro', undefined, 3, 'CNY'],
      ['spark-x2', undefined, 3, 'CNY'],
      ['openpangu-2.0-flash', undefined, 0.8, 'CNY'],
      ['LongCat-2.0', undefined, 0.3, 'USD'],
    ] as const

    for (const [id, pricingModelId, input, currency] of models) {
      const pricing = selectCatalogPricing(PRICING_SEED, { id, pricingModelId, providerIds: [] }, at('2026-09-05'))
      expect(pricing?.providerId, id).toBeNull()
      expect(pricing?.currency, id).toBe(currency)
      expect(pricing?.tiers[0]?.rate.input, id).toBe(input)
    }
  })
})

describe('formatRate', () => {
  /**
   * ★★ 这一条是整个文件的理由。`cacheWrite` 缺省是常态(DeepSeek 就没有这一项),
   * 显示成 `$0.00` 等于告诉用户「写缓存免费」—— 而 `priceOf` 对这种请求
   * 返回的是 null(整条无定价)。种子表那边有一整条断言禁止 0 进表,
   * 显示层再把 undefined 变回 0,那条断言就白写了。
   */
  it('缺省是「—」,不是 0', () => {
    expect(formatRate(undefined, 'USD')).toBe('—')
    expect(formatRate(Number.NaN, 'USD')).toBe('—')
  })

  it('至少两位小数,尾零去掉但不截断有效位', () => {
    expect(formatRate(3, 'USD')).toBe('$3.00')
    expect(formatRate(0.25, 'USD')).toBe('$0.25')
    // ★ 固定两位会把 GLM 促销价截成 $0.08 —— 差 7%
    expect(formatRate(0.075, 'USD')).toBe('$0.075')
    expect(formatRate(15, 'USD')).toBe('$15.00')
  })

  it('币种符号跟着行走,不做换算', () => {
    expect(formatRate(1.5, 'CNY')).toBe('¥1.50')
    expect(formatRate(1.5, 'USD')).toBe('$1.50')
  })

  it('种子表里每个已填的费率都显示得出来,每个缺省的都是「—」', () => {
    for (const p of PRICING_SEED) {
      for (const t of p.tiers) {
        for (const key of ['input', 'output', 'cacheRead', 'cacheWrite', 'cacheWrite1h'] as const) {
          const v = t.rate[key]
          const got = formatRate(v, p.currency)
          if (v === undefined) expect(got, `${p.modelId}.${key}`).toBe('—')
          else expect(got, `${p.modelId}.${key}`).not.toBe('—')
        }
      }
    }
  })

  /** DeepSeek 没有缓存写入费 —— 表里必须是「—」,这是那条语义的显示端 */
  it('DeepSeek 的缓存写入显示「—」', () => {
    const ds = seedRow('deepseek-v4-flash')
    expect(formatRate(ds.tiers[0]?.rate.cacheWrite, ds.currency)).toBe('—')
  })
})

describe('compactTokens', () => {
  it('整千整百万才缩写', () => {
    expect(compactTokens(272_000)).toBe('272K')
    expect(compactTokens(200_000)).toBe('200K')
    expect(compactTokens(1_000_000)).toBe('1M')
    expect(compactTokens(1500)).toBe('1500')
  })
})

describe('tierLabel', () => {
  const one: readonly PriceTier[] = [{ upToInputTokens: null, rate: { input: 1, output: 2 } }]
  const two: readonly PriceTier[] = [
    { upToInputTokens: 272_000, rate: { input: 1, output: 2 } },
    { upToInputTokens: null, rate: { input: 2, output: 3 } },
  ]

  it('单档不给标签 —— 单档模型没有「第 1 档」这回事', () => {
    expect(tierLabel(one, 0)).toBe('')
  })

  it('多档:前档给上界,末档由前一档的上界推出', () => {
    expect(tierLabel(two, 0)).toBe('≤272K')
    expect(tierLabel(two, 1)).toBe('>272K')
  })

  /** 拿真实的阶梯行再走一遍,防的是种子表改了阈值而这里还写着 272K */
  it('真实的 OpenAI 阶梯行标成 ≤272K / >272K', () => {
    const p = seedRow('gpt-5.6-sol')
    expect(p.tiers.length).toBe(2)
    expect(p.tiers.map((_, i) => tierLabel(p.tiers, i))).toEqual(['≤272K', '>272K'])
  })

  it('Gemini 与 xAI 的阈值是 200K,不是 272K', () => {
    for (const id of ['gemini-3.1-pro-preview', 'grok-4.6']) {
      const p = seedRow(id)
      expect(tierLabel(p.tiers, 0), id).toBe('≤200K')
    }
  })
})

describe('describeDays', () => {
  it('省略 = 每天', () => {
    expect(describeDays(undefined)).toBe('每天')
    expect(describeDays([])).toBe('每天')
    expect(describeDays([0, 1, 2, 3, 4, 5, 6])).toBe('每天')
  })

  it('连续三天以上折成区间', () => {
    expect(describeDays([1, 2, 3, 4, 5])).toBe('周一 至 周五')
  })

  it('不连续的逐个列出 —— 折成区间会把中间那几天算进去', () => {
    expect(describeDays([0, 6])).toBe('周日、周六')
    expect(describeDays([1, 3, 5])).toBe('周一、周三、周五')
  })

  it('乱序与重复都归一', () => {
    expect(describeDays([5, 1, 3, 2, 4, 4])).toBe('周一 至 周五')
  })
})

describe('describeWindow', () => {
  /**
   * ★ 时区名照原样写出来。`inWindow` 判断用的是 `w.timezone`,
   * 显示时替用户换算成本机时间会让界面和实际计费规则对不上 ——
   * 而对不上的时候,用户信的是界面。
   */
  it('DeepSeek 的高峰段:星期、时刻、时区、倍率一样不少', () => {
    const ds = seedRow('deepseek-v4-flash')
    const first = ds.windows?.[0]
    expect(first).toBeDefined()
    const s = describeWindow(first as PriceWindow)
    expect(s).toContain('周一 至 周五')
    expect(s).toContain('UTC')
    expect(s).toContain('×2')
  })

  it('整套费率覆盖优先于倍率 —— 和 priceOf 的优先级一致', () => {
    const w: PriceWindow = {
      timezone: 'UTC',
      start: '01:00',
      end: '04:00',
      multiplier: 2,
      rates: { input: 9, output: 9 },
      label: '高峰',
    }
    expect(describeWindow(w)).toContain('整套费率覆盖')
    expect(describeWindow(w)).not.toContain('×2')
  })

  it('两个都没有时只说时段,不编一个倍率', () => {
    const w: PriceWindow = {
      timezone: 'UTC',
      start: '01:00',
      end: '04:00',
      label: '维护窗口',
    }
    expect(describeWindow(w)).toBe('维护窗口 · 每天 01:00–04:00 UTC')
  })
})

describe('describeEffective', () => {
  const base = PRICING_SEED[0] as ModelPricing

  it('两个都省略返回空串 —— 大多数长期价格不该顶着一句「长期有效」', () => {
    expect(describeEffective(base)).toBe('')
  })

  it('只有起点 / 只有终点 / 两端都有,各说各的', () => {
    expect(describeEffective({ ...base, effectiveFrom: '2026-09-10' })).toBe('2026-09-10 起生效')
    expect(describeEffective({ ...base, effectiveUntil: '2026-09-09' })).toBe('2026-09-09 前有效')
    expect(
      describeEffective({
        ...base,
        effectiveFrom: '2026-09-01',
        effectiveUntil: '2026-09-09',
      }),
    ).toBe('2026-09-01 至 2026-09-09 生效')
  })

  /** ★ glm-5.3-flash 那两行**都**得说得出话来,否则界面上是同名不同价的两行 */
  it('glm-5.3-flash 的促销行与原价行都给得出区间说明', () => {
    const flash = PRICING_SEED.filter((p) => p.modelId === 'glm-5.3-flash')
    expect(flash.length).toBe(2)
    for (const p of flash) expect(describeEffective(p), p.effectiveFrom).not.toBe('')
  })
})

describe('groupPricing', () => {
  const groups = groupPricing(PRICING_SEED)

  it('通用价排第一组', () => {
    expect(groups[0]?.key).toBe('*')
    expect(groups[0]?.rows.every((p) => p.providerId === null)).toBe(true)
  })

  /** ★★ 分组不能吞行 —— 少一行就是少一个模型的价,而表看着仍然正常 */
  it('每一行都恰好落在一个组里', () => {
    const total = groups.reduce((n, g) => n + g.rows.length, 0)
    expect(total).toBe(PRICING_SEED.length)
    // ★ 主键含 `effectiveFrom` —— 少了它 glm-5.3-flash 的促销价与原价会被当成重复。
    // 顺带这也是界面必须把生效区间显示出来的理由:两行同名不同价,不写日期就是自相矛盾。
    const key = (p: ModelPricing): string => `${p.providerId ?? '*'}/${p.modelId}@${p.effectiveFrom ?? ''}`
    expect(new Set(groups.flatMap((g) => g.rows.map(key))).size).toBe(PRICING_SEED.length)
  })

  /** 同名两行确实存在,而且落在同一组里 —— 界面得靠日期区间把它们区分开 */
  it('glm-5.3-flash 的两行都在,靠 effectiveFrom 区分', () => {
    const generic = groups.find((g) => g.key === '*')
    const flash = generic?.rows.filter((p) => p.modelId === 'glm-5.3-flash') ?? []
    expect(flash.length).toBe(2)
    expect(flash.every((p) => p.effectiveFrom !== undefined || p.effectiveUntil !== undefined)).toBe(true)
  })

  it('组内保持种子表原序 —— 那个顺序本身就是按厂商聚簇的', () => {
    const generic = PRICING_SEED.filter((p) => p.providerId === null)
    expect(groups[0]?.rows).toEqual(generic)
  })

  /**
   * 标题回落到裸 id 是**故意留的**(新厂商不该让这一页崩),
   * 但今天一条都不该走到那个分支 —— 走到了就是 presets 少了一家。
   */
  it('每个覆盖价组都拿到了真正的厂商名,没有回落到裸 id', () => {
    for (const g of groups.slice(1)) {
      expect(g.title, g.key).not.toBe(g.key)
      expect(g.title.trim(), g.key).not.toBe('')
    }
  })

  it('空表不产生空组', () => {
    expect(groupPricing([])).toEqual([])
  })
})

describe('matchPricing', () => {
  /** ★ 和 `nav.ts` 的 `matchRows` **相反**:那边空查询返回空,这里返回全表 */
  it('空查询返回全表', () => {
    expect(matchPricing(PRICING_SEED, '').length).toBe(PRICING_SEED.length)
    expect(matchPricing(PRICING_SEED, '   ').length).toBe(PRICING_SEED.length)
  })

  it('命中 modelId / 显示名 / providerId', () => {
    expect(matchPricing(PRICING_SEED, 'claude-fable').length).toBeGreaterThan(0)
    const deepseek = matchPricing(PRICING_SEED, 'deepseek')
    expect(deepseek).toHaveLength(5)
    expect(deepseek.every((p) => p.providerId === null)).toBe(true)
  })

  it('大小写与前后空格无关', () => {
    expect(matchPricing(PRICING_SEED, '  GPT-5.6  ')).toEqual(matchPricing(PRICING_SEED, 'gpt-5.6'))
  })

  it('无命中返回空(驱动空态)', () => {
    expect(matchPricing(PRICING_SEED, 'zzzz没有这个模型')).toEqual([])
  })
})
