import { describe, expect, it } from 'vitest'
import { findPreset } from '../presets'
import { BUILTIN_MODEL_CATALOG } from '../model-catalog-inventory'
import type { ModelPricing, PriceTier, TokenRates } from '../pricing'
import { findPricing, priceOf } from '../pricing'
import { NOT_SEEDED, PRICING_SEED } from '../pricing-seed'

/**
 * 种子表的结构测试 —— 方案 §4.6。
 *
 * ★ **这里测不了「价格对不对」。** 一个数字抄错了,没有任何本地断言能发现 ——
 * 唯一的防线是采集纪律(双独立来源、逐条 `source`),那发生在写这张表的时候。
 *
 * 所以这个文件测的是另外三件事,每一件都对应一种**能悄悄发生**的坏:
 *
 * 1. **形状错**会让 `priceOf` 返回 null 或选错档 —— 表现是费用列变「—」或数字偏低,
 *    两种都不会有人报 bug。
 * 2. **跨模块失约**:`providerId` 写了个 `presets.ts` 里不存在的 id,
 *    覆盖价就永远命不中,静默退回通用价(币种可能都不对)。
 * 3. **抄写笔误**:各家的费率之间有严格的代数关系(Anthropic 的 1.25×/2×、
 *    OpenAI 越档的 2×/1.5×、xAI 的一律 2×)。这些关系**不用来生成数据**
 *    (方案 §4.2 明令存绝对价),但拿来**校验**手抄的数据正合适 ——
 *    敲错一位数几乎必然破坏其中一条。
 */

const rows = PRICING_SEED
const rateOf = (t: PriceTier): TokenRates => t.rate
const numericRates = (r: TokenRates): number[] => Object.values(r).filter((v): v is number => typeof v === 'number')

/** 每档都取到,包括阶梯的高档 */
const allRates = (p: ModelPricing): TokenRates[] => p.tiers.map(rateOf)

const byId = (modelId: string, providerId: string | null = null): ModelPricing => {
  const found = rows.find((p) => p.modelId === modelId && p.providerId === providerId)
  if (found === undefined) throw new Error(`种子表里没有 ${providerId ?? '通用'}/${modelId}`)
  return found
}

describe('种子表 · 基本形状', () => {
  it('非空,且规模没有意外缩水', () => {
    // 下界防的是「合并分支时整段被删掉」—— 不是精确计数,免得加一行就得改测试
    expect(rows.length).toBeGreaterThan(50)
  })

  it('每条都有 displayName 与合法币种', () => {
    for (const p of rows) {
      expect(p.displayName.trim(), p.modelId).not.toBe('')
      expect(['USD', 'CNY'], p.modelId).toContain(p.currency)
    }
  })

  it('本轮只有文本模态 —— 图像等 Tab 是占位,不该有数据混进来', () => {
    for (const p of rows) expect(p.modality, p.modelId).toBe('text')
  })

  it('fetchedAt 记录逐行采集日期,历史快照不随新模型刷新', () => {
    for (const p of rows) {
      expect(p.fetchedAt, p.modelId).toBe(p.modelId === 'deepseek-v4.1-flash-1017' ? '2026-10-19' : '2026-09-05')
      expect(Number.isNaN(Date.parse(p.fetchedAt)), p.modelId).toBe(false)
    }
  })

  /** 界面上逐行显示它,点得开 —— 所以必须是个真 URL,而且不能是 http */
  it('source 是可解析的 https URL', () => {
    for (const p of rows) {
      expect(() => new URL(p.source), p.modelId).not.toThrow()
      expect(new URL(p.source).protocol, p.modelId).toBe('https:')
    }
  })

  it('每个价格 modelId 都能从内置目录的 pricingModelId 命中', () => {
    const reachable = new Set(BUILTIN_MODEL_CATALOG.map((row) => row.pricingModelId))
    const orphaned = [...new Set(rows.map((row) => row.modelId))].filter((modelId) => !reachable.has(modelId)).sort()
    expect(orphaned).toEqual([])
  })
})

describe('种子表 · 阶梯结构', () => {
  it('tiers 非空', () => {
    for (const p of rows) expect(p.tiers.length, p.modelId).toBeGreaterThan(0)
  })

  /**
   * ★ **末档必须无上界。** 末档写成具体数字的话,超过它的请求一档都命不中 ——
   * `selectTier` 会退到最后一档兜底(不抛),于是**没有任何症状**,
   * 只是超长请求按了错的档算。这条断言是那个兜底分支的唯一守卫。
   */
  it('末档 upToInputTokens 为 null', () => {
    for (const p of rows) {
      expect(p.tiers[p.tiers.length - 1]?.upToInputTokens, p.modelId).toBeNull()
    }
  })

  it('非末档都有上界,且严格递增', () => {
    for (const p of rows) {
      const bounds = p.tiers.slice(0, -1).map((t) => t.upToInputTokens)
      for (const b of bounds) expect(b, p.modelId).not.toBeNull()
      const nums = bounds as number[]
      for (let i = 1; i < nums.length; i++) {
        expect(nums[i], `${p.modelId} 第 ${i} 档`).toBeGreaterThan(nums[i - 1] as number)
      }
    }
  })

  /**
   * ★ 0 只能表示官方明确公布的免费价格，不能表示“未知”。未知的正确写法是
   * 不写该字段；否则 `costMicros` 会把它当作免费并静默低估成本。
   */
  it('只有官方明确免费的型号可以使用零费率，其余费率严格为正', () => {
    const officiallyFree = new Set(['spark-x2.5-4b', 'spark-x2.5-1.7b'])
    for (const p of rows) {
      for (const r of allRates(p)) {
        for (const v of numericRates(r)) {
          if (officiallyFree.has(p.modelId)) expect(v, p.modelId).toBe(0)
          else expect(v, p.modelId).toBeGreaterThan(0)
        }
      }
    }
  })

  it('每条都至少能算出「纯输入输出」这一种请求', () => {
    for (const p of rows) {
      const got = priceOf(p, { inputTokens: 1000, outputTokens: 100 }, Date.UTC(2026, 8, 4))
      expect(got, p.modelId).not.toBeNull()
      expect(got?.currency, p.modelId).toBe(p.currency)
    }
  })
})

describe('种子表 · 主键与生效区间', () => {
  it('(providerId, modelId, effectiveFrom) 无重复', () => {
    const seen = new Set<string>()
    for (const p of rows) {
      const key = `${p.providerId ?? '*'} ${p.modelId} ${p.effectiveFrom ?? ''}`
      expect(seen.has(key), key).toBe(false)
      seen.add(key)
    }
  })

  it('同一 (providerId, modelId) 的多行,日期区间互不重叠', () => {
    const groups = new Map<string, ModelPricing[]>()
    for (const p of rows) {
      const key = `${p.providerId ?? '*'} ${p.modelId}`
      groups.set(key, [...(groups.get(key) ?? []), p])
    }
    for (const [key, g] of groups) {
      if (g.length < 2) continue
      const sorted = [...g].sort((a, b) => (a.effectiveFrom ?? '').localeCompare(b.effectiveFrom ?? ''))
      for (let i = 1; i < sorted.length; i++) {
        const prevEnd = sorted[i - 1]?.effectiveUntil
        const thisStart = sorted[i]?.effectiveFrom
        // 前一行必须封口、后一行必须有起点,否则「永远生效」会盖住另一行
        expect(prevEnd, `${key} 第 ${i - 1} 行缺 effectiveUntil`).toBeDefined()
        expect(thisStart, `${key} 第 ${i} 行缺 effectiveFrom`).toBeDefined()
        expect(String(thisStart) > String(prevEnd), key).toBe(true)
      }
    }
  })

  /**
   * ★★ **每一行都必须能被查到。** 这条防的是「写了但永远用不上」——
   * 比如两行日期区间错位留了个洞、或者一行被同 key 的另一行完全遮住。
   * 没有它,一行错误的数据和一行不存在的数据看起来一模一样。
   */
  it('每一行在自己的生效期内都能被 findPricing 命中', () => {
    for (const p of rows) {
      // 取一个落在本行区间内的时刻:有起点用起点,否则用采集日
      const at = Date.parse(`${p.effectiveFrom ?? p.fetchedAt}T12:00:00Z`)
      const label = `${p.providerId ?? '*'}/${p.modelId}`
      expect(findPricing(rows, p.providerId, p.modelId, at), label).toBe(p)
    }
  })
})

/**
 * ★★ 跨模块契约:种子表的 `providerId` 必须是 `presets.ts` 里真实存在的 id。
 *
 * 对不上的后果是**静默**的:`findPricing` 查不到覆盖价就退回通用价 ——
 * 某些国内型号没有通用价行时费用列会变「—」;更糟的情况是错误退到一条
 * **美元**的通用价上,把人民币的账算成美元的数。因此通用价与连接覆盖价必须分开校验。
 */
describe('种子表 × presets', () => {
  it('每个非空 providerId 都对应一条真实预设', () => {
    for (const p of rows) {
      if (p.providerId === null) continue
      expect(findPreset(p.providerId), `${p.providerId}/${p.modelId}`).not.toBeNull()
    }
  })

  it('只有区域价或平台价使用 providerId，厂商通用官方价不依赖连接', () => {
    const ids = [...new Set(rows.map((p) => p.providerId).filter((x): x is string => x !== null))]
    expect(ids.sort()).toEqual(['dashscope', 'moonshot', 'opencode-go'])
  })

  it('国内站的行记人民币,国际站的行记美元 —— 不做换算(方案 §5.3)', () => {
    const expected: Record<string, 'USD' | 'CNY'> = {
      moonshot: 'CNY',
      dashscope: 'CNY',
      'opencode-go': 'USD',
    }
    for (const p of rows) {
      if (p.providerId === null) continue
      expect(p.currency, `${p.providerId}/${p.modelId}`).toBe(expected[p.providerId])
    }
  })

  it('通用价保留官方原币，人民币通用价只来自逐款核实的国内型号', () => {
    const cnyGeneric = rows
      .filter((p) => p.providerId === null && p.currency === 'CNY')
      .map((p) => p.modelId)
      .sort()
    expect(cnyGeneric).toEqual([
      'Baichuan-M2',
      'Baichuan-M2-Plus',
      'Baichuan-M3',
      'Baichuan-M3-Plus',
      'Baichuan2-Turbo',
      'Baichuan3-Turbo',
      'Baichuan3-Turbo-128k',
      'Baichuan4',
      'Baichuan4-Air',
      'Baichuan4-Turbo',
      'SenseChat-Character',
      'SenseChat-Character-Pro',
      'SenseChat-Vision',
      'SenseNova-V6-5-Pro',
      'SenseNova-V6-5-Turbo',
      'SenseNova-V6-Pro',
      'SenseNova-V6-Reasoner',
      'SenseNova-V6-Turbo',
      'doubao-1.5-lite-32k',
      'doubao-1.5-pro-32k',
      'doubao-1.5-vision-pro',
      'doubao-seed-1.6-flash',
      'doubao-seed-1.6-vision',
      'doubao-seed-2.0-code',
      'doubao-seed-2.0-lite',
      'doubao-seed-2.0-mini',
      'doubao-seed-2.0-pro',
      'doubao-seed-2.1-lite',
      'doubao-seed-2.1-pro',
      'doubao-seed-character',
      'doubao-seed-code',
      'doubao-seed-evolving',
      'doubao-seed-translation',
      'ernie-4.5-turbo',
      'ernie-4.5-turbo-vl',
      'ernie-5.0',
      'ernie-5.1',
      'ernie-x1.1-preview',
      'hunyuan-role-latest',
      'hy-mt2-lite',
      'hy-mt2-plus',
      'hy-mt2-pro',
      'hy-role',
      'hy3',
      'hy4-preview',
      'internvl3-38b',
      'openpangu-2.0-flash',
      'spark-x2',
      'spark-x2-flash',
      'spark-x2.5-1.7b',
      'spark-x2.5-4b',
      'step-1o-turbo-vision',
      'step-3.5-flash',
      'step-3.5-flash-2603',
      'step-3.7-flash',
    ])
    for (const p of rows) {
      if (p.providerId === null && !cnyGeneric.includes(p.modelId)) {
        expect(p.currency, p.modelId).toBe('USD')
      }
    }
  })
})

/**
 * ─────────────────────────────────────────────────────────────
 * 抄写校验:各家费率之间的代数关系
 * ─────────────────────────────────────────────────────────────
 *
 * ★ **这些关系不是用来生成数据的**(方案 §4.2:存绝对价,不存倍率),
 * 是用来**复核手抄结果**的。敲错一位数几乎必然破坏其中一条。
 *
 * 浮点一律 `toBeCloseTo` —— `3 * 0.1 !== 0.3`,而这张表里恰好有 input=3 的行。
 */
describe('抄写校验 · Anthropic', () => {
  const anth = rows.filter((p) => p.modelId.startsWith('claude-'))

  /** 两档行(目前只有 Haiku 5.5)每一档都要过下面的倍率校验,不能只看 tiers[0] */
  const anthRates = (p: ModelPricing): TokenRates[] => p.tiers.map((t) => t.rate as TokenRates)

  it('16 个 SKU,除 Haiku 5.5 外全部单档(官方费率卡已无 >200K 档)', () => {
    expect(anth.length).toBe(16)
    for (const p of anth) expect(p.tiers.length, p.modelId).toBe(p.modelId === 'claude-haiku-5-5' ? 2 : 1)
  })

  it('缓存写 = 1.25× 输入,1h 写 = 2× 输入', () => {
    for (const p of anth) {
      for (const r of anthRates(p)) {
        expect(r.cacheWrite, p.modelId).toBeCloseTo(r.input * 1.25, 6)
        expect(r.cacheWrite1h, p.modelId).toBeCloseTo(r.input * 2, 6)
      }
    }
  })

  /**
   * ★ 「缓存读 = 0.1× 输入」有**三个真实例外** —— 前两个正是方案 §4.2 引为
   * 「不能用公式生成种子表」证据的那两行。把它们钉死在这里,
   * 是为了让「顺手把这几个数改成 0.1× 好让规律统一」这件事必须先删掉一条测试。
   *
   * ★ 第三条例外 `claude-opus-5-5` = **0.05×** 比 0.1× 还低一半(输入 $4、命中 $0.2,
   * 2026-09-23 用户口径,见 pricing-seed 的 Anthropic 段)。「统一成 0.1×」
   * 会把这行的缓存读改成 0.4 —— 凭空多收 2 倍,而且没有任何报错指向这里。
   */
  it('缓存读 = 0.1× 输入,除 Mythos/Fable 5.1 是 0.025×、Opus 5.5 是 0.05×', () => {
    const exceptionFactor = new Map<string, number>([
      ['claude-mythos-5-1', 0.025],
      ['claude-fable-5-1', 0.025],
      ['claude-opus-5-5', 0.05],
    ])
    for (const p of anth) {
      const factor = exceptionFactor.get(p.modelId) ?? 0.1
      for (const r of anthRates(p)) expect(r.cacheRead, p.modelId).toBeCloseTo(r.input * factor, 6)
    }
    // 上一代同价位机型确实是 0.1x($1.00)—— 证明这个例外是**代际**差异,不是笔误
    expect(byId('claude-fable-5').tiers[0]?.rate.cacheRead).toBe(1.0)
    expect(byId('claude-fable-5-1').tiers[0]?.rate.cacheRead).toBe(0.25)
  })

  /**
   * 需求:Opus 5.5 的五个数是用户口径给的,不是从同族推的 —— 按 Opus 5 的族内
   * 规律反推会得出 5/25/6.25/10/0.5 那一套,与真实费率全错。
   * 所以这里钉**绝对数**而不是任何倍率关系:日后有人「照族对齐」这行必须先删本条。
   */
  it('Opus 5.5:用户口径的五个数原样钉死(输入4/输出20/读0.2/写5/1h写8)', () => {
    expect(byId('claude-opus-5-5').tiers[0]?.rate).toMatchObject({
      input: 4,
      output: 20,
      cacheRead: 0.2,
      cacheWrite: 5,
      cacheWrite1h: 8,
    })
  })

  /**
   * 需求:2026-10-08 收录 Haiku 5.5,官方定价页按 prompt 长度分两档。
   * 钉**阈值与绝对数**:阈值是 100K(写成别家的 200K / 272K 会让 100K–200K 的请求
   * 少收 5 倍),高档是低档 ×5(不是 OpenAI 那种输入 ×2 / 输出 ×1.5 的不对称)。
   */
  it('Haiku 5.5:阈值 100K,两档数值钉死,高档五项一律 ×5', () => {
    expect(byId('claude-haiku-5-5').tiers).toEqual([
      { upToInputTokens: 100_000, rate: { input: 0.1, output: 0.5, cacheWrite: 0.125, cacheWrite1h: 0.2, cacheRead: 0.01 } },
      { upToInputTokens: null, rate: { input: 0.5, output: 2.5, cacheWrite: 0.625, cacheWrite1h: 1, cacheRead: 0.05 } },
    ])
  })

  it('输出 = 5× 输入(全 16 条、每一档都成立)', () => {
    for (const p of anth) {
      for (const r of anthRates(p)) expect(r.output, p.modelId).toBeCloseTo(r.input * 5, 6)
    }
  })
})

describe('抄写校验 · 长上下文档', () => {
  /** ★ 越档倍率**不对称**:输入/缓存 ×2,输出只 ×1.5。写反了费用会偏低 */
  const asymmetric = (p: ModelPricing, threshold: number): void => {
    expect(p.tiers.length, p.modelId).toBe(2)
    expect(p.tiers[0]?.upToInputTokens, p.modelId).toBe(threshold)
    const lo = p.tiers[0]?.rate as TokenRates
    const hi = p.tiers[1]?.rate as TokenRates
    expect(hi.input, p.modelId).toBeCloseTo(lo.input * 2, 6)
    expect(hi.output, p.modelId).toBeCloseTo(lo.output * 1.5, 6)
    if (lo.cacheRead !== undefined) expect(hi.cacheRead, p.modelId).toBeCloseTo(lo.cacheRead * 2, 6)
    if (lo.cacheWrite !== undefined) expect(hi.cacheWrite, p.modelId).toBeCloseTo(lo.cacheWrite * 2, 6)
  }

  it('OpenAI:阈值 272K,输入/缓存 ×2、输出 ×1.5', () => {
    const tiered = rows.filter((p) => p.modelId.startsWith('gpt-') && p.tiers.length > 1)
    expect(tiered.map((p) => p.modelId).sort()).toEqual(['gpt-5.6-luna', 'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-6-astra', 'gpt-6-luna', 'gpt-6-sol', 'gpt-6.1-sol'])
    for (const p of tiered) asymmetric(p, 272_000)
  })

  it('Gemini:只有 Pro 两款,阈值 200K(不是 1.5 时代的 128K)', () => {
    const gem = rows.filter((p) => p.modelId.startsWith('gemini-') && p.tiers.length > 1)
    expect(gem.map((p) => p.modelId).sort()).toEqual(['gemini-2.5-pro', 'gemini-3.1-pro-preview'])
    for (const p of gem) asymmetric(p, 200_000)
  })

  /*
   * 需求:2026-09-20 用户要求 MAI-Code 1.1 Flash「定价与 GPT-5.6 Luna 一致」,
   * 但**独立成行、不依赖 Luna**。这里钉**绝对数**而不是「与 Luna 相等」:
   * 日后 Luna 调价不应连带这条(联动恰恰是被要求去掉的),谁要改这条
   * 必须带着微软官方价来,而不是顺手抄 Luna 的新价。
   */
  it('MAI-Code 1.1 Flash:独立价目行,数值钉死(录入口径=与 Luna 同价,不联动)', () => {
    expect(byId('mai-code-1.1-flash')).toMatchObject({
      providerId: null,
      currency: 'USD',
      tiers: [
        { upToInputTokens: 272_000, rate: { input: 0.2, output: 1.2, cacheRead: 0.02, cacheWrite: 0.25 } },
        { upToInputTokens: null, rate: { input: 0.4, output: 1.8, cacheRead: 0.04, cacheWrite: 0.5 } },
      ],
    })
  })

  /** xAI 是唯一一家各计费项倍率统一的 —— 所以它**不**走上面那个不对称断言 */
  it('xAI:阈值 200K,所有计费项一律 ×2', () => {
    const grok = rows.filter((p) => p.modelId.startsWith('grok-'))
    expect(grok.map((p) => p.modelId).sort()).toEqual(['grok-4.3', 'grok-4.5', 'grok-4.6', 'grok-4.7'])
    for (const p of grok) {
      expect(p.tiers[0]?.upToInputTokens, p.modelId).toBe(200_000)
      const lo = p.tiers[0]?.rate as TokenRates
      const hi = p.tiers[1]?.rate as TokenRates
      expect(hi.input, p.modelId).toBeCloseTo(lo.input * 2, 6)
      expect(hi.output, p.modelId).toBeCloseTo(lo.output * 2, 6)
      expect(hi.cacheRead, p.modelId).toBeCloseTo((lo.cacheRead ?? 0) * 2, 6)
    }
  })

  /**
   * ★ 反向断言:所有已录入的阶梯模型都必须点名。
   * 没有它,给某一行悄悄加个档不会被任何测试注意到。
   */
  it('其余所有行都是单档', () => {
    const tiered = rows.filter((p) => p.tiers.length > 1).map((p) => p.modelId)
    expect(tiered.sort()).toEqual([
      'MiniMax-M3',
      'claude-haiku-5-5',
      'doubao-seed-1.6-flash',
      'doubao-seed-1.6-vision',
      'doubao-seed-2.0-code',
      'doubao-seed-2.0-lite',
      'doubao-seed-2.0-mini',
      'doubao-seed-2.0-pro',
      'doubao-seed-character',
      'doubao-seed-code',
      'ernie-5.0',
      'ernie-5.1',
      'gemini-2.5-pro',
      'gemini-3.1-pro-preview',
      'gpt-5.6-luna',
      'gpt-5.6-sol',
      'gpt-5.6-terra',
      'gpt-6-astra',
      'gpt-6-luna',
      'gpt-6-sol',
      'gpt-6.1-sol',
      'grok-4.3',
      'grok-4.5',
      'grok-4.6',
      'grok-4.7',
      'mai-code-1.1-flash',
      'qwen3-coder-30b-a3b-instruct',
      'qwen3-coder-flash',
      'qwen3-coder-next',
      'qwen3-coder-plus',
      'qwen3-max',
      'qwen3.5-plus',
      'qwen3.6-flash',
      'qwen3.6-max-preview',
      'qwen3.6-plus',
      'qwen3.7-flash',
      'qwen3.7-plus',
    ])
  })
})

/**
 * ─────────────────────────────────────────────────────────────
 * ★★ DeepSeek 时段计费 —— 种子表 × `priceOf` 的联测
 * ─────────────────────────────────────────────────────────────
 *
 * 这一组不是在测 `pricing.ts` 的时段逻辑(那边有自己的测试),
 * 是在测**这张表的录入方向对不对**:基准价录的是不是空闲价、
 * `daysOfWeek` 有没有漏、UTC 那两段有没有抄错。
 *
 * 官方原文(见 `pricing-seed.ts` 的注释):高峰 = **UTC 周一至周五**
 * 01:00–04:00 与 06:00–10:00,其余(含整个周末)全是空闲。
 */
describe('DeepSeek · 时段计费', () => {
  const ds = byId('deepseek-v4-flash')
  const use = { inputTokens: 1_000_000, outputTokens: 1_000_000 }
  const at = (iso: string): number => Date.parse(iso)
  const cost = (iso: string): number => {
    const got = priceOf(ds, use, at(iso))
    expect(got, iso).not.toBeNull()
    return got?.micros ?? 0
  }
  const hitWindow = (iso: string): string | undefined => priceOf(ds, use, at(iso))?.window

  // 2026-09-07 是周一,2026-09-05 是周六(与 fetchedAt 同周)
  it('周一 UTC 02:00 落在第一段高峰', () => {
    expect(hitWindow('2026-09-07T02:00:00Z')).toContain('高峰')
  })

  it('周一 UTC 07:00 落在第二段高峰', () => {
    expect(hitWindow('2026-09-07T07:00:00Z')).toContain('高峰')
  })

  it('两段之间的 UTC 05:00 是空闲', () => {
    expect(hitWindow('2026-09-07T05:00:00Z')).toBeUndefined()
  })

  /** 起点**含**、终点**不含** —— 边界读法钉死在这里 */
  it('01:00 整点已在高峰内,04:00 整点已在高峰外', () => {
    expect(hitWindow('2026-09-07T01:00:00Z')).toContain('高峰')
    expect(hitWindow('2026-09-07T04:00:00Z')).toBeUndefined()
  })

  /**
   * ★★ **周末整天空闲。** 这是 `daysOfWeek` 字段存在的唯一理由 ——
   * 漏了它,「周六凌晨 2 点」会按高峰价算,**贵一倍**,而没有任何人会发现。
   */
  it('周六 UTC 02:00 是空闲价,不是高峰', () => {
    expect(hitWindow('2026-09-05T02:00:00Z')).toBeUndefined()
    expect(cost('2026-09-05T02:00:00Z')).toBe(cost('2026-09-07T05:00:00Z'))
  })

  /**
   * ★ **录入方向**:基准 = 空闲。高峰恰好是它的两倍,而不是反过来。
   * 方向写反了,「没命中窗口」会落在**贵**的那一侧 —— 见 `pricing-seed.ts` 文件头。
   */
  it('高峰价 = 空闲价 × 2,且基准(不命中窗口时)是便宜的那一侧', () => {
    const peak = cost('2026-09-07T02:00:00Z')
    const off = cost('2026-09-07T05:00:00Z')
    expect(peak).toBe(off * 2)
    // $0.22/M 输入 + $0.66/M 输出,各一百万 token = $0.88 = 880_000 micros
    expect(off).toBe(880_000)
  })

  it('三条 DeepSeek 记录都是无需连接即可显示的通用 USD 单档价', () => {
    const all = rows.filter((p) => p.modelId.startsWith('deepseek-v4-'))
    expect(all.length).toBe(3)
    for (const p of all) {
      expect(p.providerId, p.modelId).toBeNull()
      expect(p.currency, p.modelId).toBe('USD')
      expect(p.tiers, p.modelId).toHaveLength(1)
      expect(p.windows, p.modelId).toHaveLength(2)
      expect(p.source, p.modelId).toBe('https://api-docs.deepseek.com/quick_start/pricing/')
      expect(p.effectiveFrom, p.modelId).toBeUndefined()
      expect(p.effectiveUntil, p.modelId).toBeUndefined()
    }
  })

  it('逐项保存官方空闲时段输入、输出与缓存命中美元价', () => {
    expect(byId('deepseek-v4-flash').tiers[0]?.rate).toEqual({
      input: 0.22,
      output: 0.66,
      cacheRead: 0.007,
    })
    expect(byId('deepseek-v4-pro').tiers[0]?.rate).toEqual({
      input: 0.66,
      output: 1.98,
      cacheRead: 0.022,
    })
    expect(byId('deepseek-v4-flash-vision-exp').tiers[0]?.rate).toEqual({
      input: 0.22,
      output: 0.66,
      cacheRead: 0.007,
    })
  })

  /** DeepSeek 没有缓存写入费 —— **缺省**,不是 0(0 会被算成「写入免费」) */
  it('无缓存写入价:写缓存的请求返回 null(无定价),而不是算成免费', () => {
    for (const p of rows.filter((x) => x.modelId.startsWith('deepseek-v4-'))) {
      expect(p.tiers[0]?.rate.cacheWrite, p.modelId).toBeUndefined()
      const got = priceOf(p, { inputTokens: 100, outputTokens: 10, cacheCreationInputTokens: 50 }, at('2026-09-07T05:00:00Z'))
      expect(got, p.modelId).toBeNull()
    }
  })
})

/**
 * ★ 全表唯一一处有据可查的调价:GLM-5.3-Flash 的促销于 UTC+8 的 2026-09-09 24:00 结束。
 * 两行、区间相邻不重叠 —— 到点自动切,而不是等用户某天发现统计全错。
 */
describe('GLM-5.3-Flash · 促销到期自动切换', () => {
  const at = (d: string): number => Date.parse(`${d}T12:00:00Z`)
  const inputAt = (d: string): number | undefined => findPricing(rows, null, 'glm-5.3-flash', at(d))?.tiers[0]?.rate.input

  it('促销期内是 $0.075', () => {
    expect(inputAt('2026-09-04')).toBe(0.075)
    expect(inputAt('2026-09-09')).toBe(0.075)
  })

  it('次日起是原价 $0.15', () => {
    expect(inputAt('2026-09-10')).toBe(0.15)
    expect(inputAt('2026-12-31')).toBe(0.15)
  })

  it('两段之间没有空档 —— 任何一天都查得到价', () => {
    for (const d of ['2026-09-08', '2026-09-09', '2026-09-10', '2026-09-11']) {
      expect(findPricing(rows, null, 'glm-5.3-flash', at(d)), d).not.toBeNull()
    }
  })

  it('与 Gemini 4 Argon、3.8/3.7/3.6 Flash 一起构成全表五组带日期区间的行', () => {
    const dated = rows.filter((p) => p.effectiveFrom !== undefined || p.effectiveUntil !== undefined)
    expect(dated.map((p) => p.modelId)).toEqual(['gemini-4-argon', 'gemini-4-argon', 'gemini-3.8-flash', 'gemini-3.8-flash', 'gemini-3.7-flash', 'gemini-3.7-flash', 'gemini-3.6-flash', 'gemini-3.6-flash', 'glm-5.3-flash', 'glm-5.3-flash'])
  })
})

describe('国内厂商 · 通用官方价与区域覆盖价', () => {
  it('GLM 的 Z.AI 官方美元价是无需绑定连接的通用价', () => {
    for (const modelId of ['glm-5.3', 'glm-4.7-flashx', 'glm-4.6v', 'glm-4.5v']) {
      expect(byId(modelId).providerId, modelId).toBeNull()
      expect(byId(modelId).currency, modelId).toBe('USD')
    }
    expect(byId('glm-5.3').tiers[0]?.rate).toEqual({
      input: 1.4,
      output: 4.4,
      cacheRead: 0.26,
    })
    expect(byId('glm-4.7-flashx').tiers[0]?.rate).toEqual({
      input: 0.07,
      output: 0.4,
      cacheRead: 0.01,
    })
  })

  it('Kimi 国际站 USD 是通用价，国内 Moonshot CNY 是连接覆盖价', () => {
    expect(byId('kimi-k3').tiers[0]?.rate).toEqual({
      input: 3,
      output: 15,
      cacheRead: 0.3,
    })
    expect(byId('kimi-k3').currency).toBe('USD')
    expect(byId('kimi-k3', 'moonshot').tiers[0]?.rate).toEqual({
      input: 20,
      output: 100,
      cacheRead: 2,
    })
    expect(byId('kimi-k3', 'moonshot').currency).toBe('CNY')
  })

  it('Qwen 保存官方 USD 阶梯，边界按 K=1000 且整单命中', () => {
    const max = byId('qwen3-max')
    expect(max.providerId).toBeNull()
    expect(max.tiers).toEqual([
      { upToInputTokens: 32_000, rate: { input: 1.2, output: 6 } },
      { upToInputTokens: 128_000, rate: { input: 2.4, output: 12 } },
      { upToInputTokens: null, rate: { input: 3, output: 15 } },
    ])

    const below = priceOf(max, { inputTokens: 32_000, outputTokens: 1_000 }, Date.UTC(2026, 8, 5))
    const above = priceOf(max, { inputTokens: 32_001, outputTokens: 1_000 }, Date.UTC(2026, 8, 5))
    expect(below?.tier).toBe(0)
    expect(above?.tier).toBe(1)
    expect(below?.micros).toBe(44_400)
    expect(above?.micros).toBe(88_802)
  })

  it('MiniMax M3 在 512K 边界切换整套输入、输出和缓存读取价', () => {
    const m3 = byId('MiniMax-M3')
    expect(m3.providerId).toBeNull()
    expect(m3.tiers).toEqual([
      {
        upToInputTokens: 512_000,
        rate: { input: 0.3, output: 1.2, cacheRead: 0.06 },
      },
      {
        upToInputTokens: null,
        rate: { input: 0.6, output: 2.4, cacheRead: 0.12 },
      },
    ])
    expect(priceOf(m3, { inputTokens: 512_000, outputTokens: 1_000 }, Date.UTC(2026, 8, 5))?.tier).toBe(0)
    expect(priceOf(m3, { inputTokens: 512_001, outputTokens: 1_000 }, Date.UTC(2026, 8, 5))?.tier).toBe(1)
  })

  it('MiMo V2.5 两款保存海外官方美元价', () => {
    expect(byId('mimo-v2.5-pro').tiers[0]?.rate).toEqual({
      input: 0.435,
      output: 0.87,
      cacheRead: 0.0036,
    })
    expect(byId('mimo-v2.5').tiers[0]?.rate).toEqual({
      input: 0.14,
      output: 0.28,
      cacheRead: 0.0028,
    })
    expect(byId('mimo-v2.5-pro').source).toBe('https://mimo.mi.com/docs/en-US/pricing')
  })

  it('LongCat 2.0 保存官方当前 USD 限时折扣价且不杜撰结束日期', () => {
    const price = byId('LongCat-2.0')

    expect(price).toMatchObject({
      providerId: null,
      currency: 'USD',
      source: 'https://longcat.chat/platform/docs/pricing/longcat-2.0',
    })
    expect(price.tiers).toEqual([
      {
        upToInputTokens: null,
        rate: { input: 0.3, output: 1.2, cacheRead: 0.006 },
      },
    ])
    expect(price.effectiveUntil).toBeUndefined()
  })

  it('StepFun 当前四款模型保存无需连接绑定的官方人民币价', () => {
    const expected: Record<string, TokenRates> = {
      'step-3.7-flash': { input: 1.35, output: 8.1, cacheRead: 0.27 },
      'step-3.5-flash': { input: 0.7, output: 2.1, cacheRead: 0.14 },
      'step-3.5-flash-2603': { input: 0.7, output: 2.1, cacheRead: 0.14 },
      'step-1o-turbo-vision': { input: 2.5, output: 8, cacheRead: 0.5 },
    }

    for (const [modelId, rate] of Object.entries(expected)) {
      const price = byId(modelId)
      expect(price.providerId, modelId).toBeNull()
      expect(price.currency, modelId).toBe('CNY')
      expect(price.tiers).toEqual([{ upToInputTokens: null, rate }])
      expect(price.source, modelId).toBe('https://platform.stepfun.com/docs/zh/guides/pricing/details.md')
    }
  })

  it('百川每千 Token 官方价正确换算为每百万 Token，合并价同时用于输入输出', () => {
    const expected: Record<string, TokenRates> = {
      Baichuan4: { input: 100, output: 100 },
      'Baichuan4-Turbo': { input: 15, output: 15 },
      'Baichuan4-Air': { input: 0.98, output: 0.98 },
      'Baichuan3-Turbo': { input: 12, output: 12 },
      'Baichuan3-Turbo-128k': { input: 24, output: 24 },
      'Baichuan2-Turbo': { input: 8, output: 8 },
      'Baichuan-M3-Plus': { input: 5, output: 9 },
      'Baichuan-M3': { input: 10, output: 30 },
      'Baichuan-M2-Plus': { input: 10, output: 30 },
      'Baichuan-M2': { input: 2, output: 20 },
    }

    for (const [modelId, rate] of Object.entries(expected)) {
      const price = byId(modelId)
      expect(price.providerId, modelId).toBeNull()
      expect(price.currency, modelId).toBe('CNY')
      expect(price.tiers).toEqual([{ upToInputTokens: null, rate }])
      expect(price.source, modelId).toBe('https://platform.baichuan-ai.com/prices')
    }
  })

  it('SenseNova 主定价页八款 Token 模型保存通用人民币单档价', () => {
    const expected: Record<string, TokenRates> = {
      'SenseNova-V6-5-Pro': { input: 3, output: 9 },
      'SenseNova-V6-5-Turbo': { input: 1.5, output: 4.5 },
      'SenseNova-V6-Pro': { input: 3, output: 9 },
      'SenseNova-V6-Turbo': { input: 1.5, output: 4.5 },
      'SenseNova-V6-Reasoner': { input: 4, output: 16 },
      'SenseChat-Vision': { input: 10, output: 60 },
      'SenseChat-Character-Pro': { input: 15, output: 15 },
      'SenseChat-Character': { input: 12, output: 12 },
    }

    for (const [modelId, rate] of Object.entries(expected)) {
      const price = byId(modelId)
      expect(price.providerId, modelId).toBeNull()
      expect(price.currency, modelId).toBe('CNY')
      expect(price.tiers).toEqual([{ upToInputTokens: null, rate }])
      expect(price.source, modelId).toBe('https://www.sensecore.cn/help/docs/model-as-a-service/nova/pricing')
      expect(price.windows, modelId).toBeUndefined()
      expect(price.tiers[0]?.rate.cacheRead, modelId).toBeUndefined()
      expect(price.tiers[0]?.rate.cacheWrite, modelId).toBeUndefined()
    }

    for (const secondary of ['SenseChat-5', 'SenseChat', 'SenseChat-Turbo', 'SenseChat-5-Cantonese', 'SenseChat-FunctionCall']) {
      expect(rows.some((row) => row.modelId === secondary), secondary).toBe(false)
    }
  })

  it('讯飞星火当前四款 MaaS 模型保存官方人民币按量价', () => {
    const expected: Record<string, TokenRates> = {
      'spark-x2': { input: 3, output: 3 },
      'spark-x2-flash': { input: 1, output: 2 },
      'spark-x2.5-4b': { input: 0, output: 0, cacheRead: 0 },
      'spark-x2.5-1.7b': { input: 0, output: 0, cacheRead: 0 },
    }

    for (const [modelId, rate] of Object.entries(expected)) {
      const price = byId(modelId)
      expect(price.providerId, modelId).toBeNull()
      expect(price.currency, modelId).toBe('CNY')
      expect(price.tiers, modelId).toEqual([{ upToInputTokens: null, rate }])
      expect(price.source, modelId).toMatch(/^https:\/\/maas\.xfyun\.cn\/modelSquare\/base\//)
      expect(price.windows, modelId).toBeUndefined()
    }

    for (const legacy of ['spark-4.0-ultra', 'spark-x1', 'generalv3.5', 'max-32k', 'generalv3', 'pro-128k', 'spark-lite']) {
      expect(rows.some((row) => row.modelId === legacy), legacy).toBe(false)
    }
  })

  it('openPangu 2.0 Flash 保存可无损表达的官方人民币按量价', () => {
    const price = byId('openpangu-2.0-flash')
    expect(price).toMatchObject({
      providerId: null,
      currency: 'CNY',
      source: 'https://support.huaweicloud.com/price-maas/price-maas-0002.html',
      tiers: [
        {
          upToInputTokens: null,
          rate: { input: 0.8, output: 1.6, cacheRead: 0.2 },
        },
      ],
    })
    expect(rows.some((row) => row.modelId === 'openpangu-2.0-pro')).toBe(false)
  })

  it('豆包常规在线推理价按官方 K=1000 阶梯整单命中', () => {
    expect(byId('doubao-seed-2.1-pro').tiers).toEqual([{ upToInputTokens: null, rate: { input: 3, output: 15, cacheRead: 1.2 } }])
    expect(byId('doubao-seed-2.1-lite').tiers).toEqual([{ upToInputTokens: null, rate: { input: 0.8, output: 2.7, cacheRead: 0.16 } }])
    // ★ 2.1 Turbo 整档已从官方价格表消失,目录行也同时退役 —— 价目行一起删。
    // 留一行查不到现价的旧价,用户拿到的是一个看着挺像样的错数字。
    expect(rows.some((row) => row.modelId === 'doubao-seed-2.1-turbo')).toBe(false)

    const pro = byId('doubao-seed-2.0-pro')
    expect(pro.providerId).toBeNull()
    expect(pro.currency).toBe('CNY')
    expect(pro.tiers).toEqual([
      {
        upToInputTokens: 32_000,
        rate: { input: 3.2, output: 16, cacheRead: 0.64 },
      },
      {
        upToInputTokens: 128_000,
        rate: { input: 4.8, output: 24, cacheRead: 0.96 },
      },
      {
        upToInputTokens: null,
        rate: { input: 9.6, output: 48, cacheRead: 1.92 },
      },
    ])
    expect(priceOf(pro, { inputTokens: 32_000, outputTokens: 1 }, Date.now())?.tier).toBe(0)
    expect(priceOf(pro, { inputTokens: 32_001, outputTokens: 1 }, Date.now())?.tier).toBe(1)
    expect(priceOf(pro, { inputTokens: 128_001, outputTokens: 1 }, Date.now())?.tier).toBe(2)
    expect(pro.source).toBe('https://docs.volcengine.com/docs/82379/1544106')
  })

  it('百度千帆价格从每千 Token 正确换算为每百万 Token', () => {
    expect(byId('ernie-5.1').tiers).toEqual([
      { upToInputTokens: 32_000, rate: { input: 4, output: 18 } },
      { upToInputTokens: null, rate: { input: 6, output: 22 } },
    ])
    expect(byId('ernie-5.0').tiers).toEqual([
      { upToInputTokens: 32_000, rate: { input: 6, output: 24 } },
      { upToInputTokens: null, rate: { input: 10, output: 40 } },
    ])
    expect(byId('ernie-4.5-turbo').tiers[0]?.rate).toEqual({
      input: 0.8,
      output: 3.2,
      cacheRead: 0.2,
    })
    expect(byId('ernie-4.5-turbo-vl').tiers[0]?.rate).toEqual({
      input: 3,
      output: 9,
      cacheRead: 0.75,
    })
    expect(byId('ernie-x1.1-preview').tiers[0]?.rate).toEqual({
      input: 1,
      output: 4,
    })
    expect(byId('internvl3-38b').tiers[0]?.rate).toEqual({
      input: 8,
      output: 24,
    })
    expect(byId('ernie-5.1').source).toBe('https://cloud.baidu.com/doc/qianfan/s/wmh4sv6ya')
  })

  it('腾讯 TokenHub 当前七款混元语言模型保存官方人民币价', () => {
    const expected: Record<string, TokenRates> = {
      'hy4-preview': { input: 6, output: 18, cacheRead: 0.3 },
      hy3: { input: 1, output: 4, cacheRead: 0.25 },
      'hy-mt2-pro': { input: 0.5, output: 2 },
      'hy-mt2-plus': { input: 0.5, output: 2 },
      'hy-mt2-lite': { input: 0.3, output: 1.2 },
      'hunyuan-role-latest': { input: 2.4, output: 9.6 },
      'hy-role': { input: 2.4, output: 9.6 },
    }

    for (const [modelId, rate] of Object.entries(expected)) {
      const price = byId(modelId)
      expect(price.providerId, modelId).toBeNull()
      expect(price.currency, modelId).toBe('CNY')
      expect(price.tiers).toEqual([{ upToInputTokens: null, rate }])
      expect(price.source, modelId).toBe('https://cloud.tencent.com/document/product/1823/130055')
    }
  })
})

describe('Gemini 3.8/3.7/3.6 Flash · 官方调价自动切换', () => {
  const datedFlashIds = ['gemini-3.8-flash', 'gemini-3.7-flash', 'gemini-3.6-flash']
  const at = (d: string): number => Date.parse(`${d}T12:00:00Z`)
  const rateAt = (modelId: string, d: string): TokenRates | undefined => findPricing(rows, null, modelId, at(d))?.tiers[0]?.rate

  it('2026-12-31 前使用介绍价', () => {
    for (const modelId of datedFlashIds) {
      expect(rateAt(modelId, '2026-09-05'), modelId).toEqual({
        input: 0.75,
        output: 3.75,
        cacheRead: 0.075,
      })
      expect(rateAt(modelId, '2026-12-31'), modelId).toEqual({
        input: 0.75,
        output: 3.75,
        cacheRead: 0.075,
      })
    }
  })

  it('2027-01-01 起使用新价', () => {
    for (const modelId of datedFlashIds) {
      expect(rateAt(modelId, '2027-01-01'), modelId).toEqual({
        input: 1.5,
        output: 7.5,
        cacheRead: 0.15,
      })
      expect(rateAt(modelId, '2027-06-01'), modelId).toEqual({
        input: 1.5,
        output: 7.5,
        cacheRead: 0.15,
      })
    }
  })

  it('缓存小时存储费没有被错误写成一次性 cacheWrite', () => {
    for (const p of rows.filter((row) => datedFlashIds.includes(row.modelId))) {
      expect(p.tiers[0]?.rate.cacheWrite).toBeUndefined()
      expect(p.source).toBe(`https://ai.google.dev/gemini-api/docs/pricing#${p.modelId}`)
    }
  })
})

describe('Gemini Flash · 官方 Standard 文本链路价格', () => {
  const expected = new Map<string, TokenRates>([
    ['gemini-3.5-flash', { input: 1.5, output: 9, cacheRead: 0.15 }],
    ['gemini-3.5-flash-lite', { input: 0.3, output: 2.5, cacheRead: 0.03 }],
    ['gemini-3.1-flash-lite', { input: 0.25, output: 1.5, cacheRead: 0.025 }],
    ['gemini-3-flash-preview', { input: 0.5, output: 3, cacheRead: 0.05 }],
    ['gemini-2.5-flash', { input: 0.3, output: 2.5, cacheRead: 0.03 }],
    ['gemini-2.5-flash-lite', { input: 0.1, output: 0.4, cacheRead: 0.01 }],
  ])

  it('逐型号保存 Google 官方输入、输出与缓存读取价格', () => {
    for (const [modelId, rate] of expected) {
      expect(byId(modelId).tiers[0]?.rate, modelId).toEqual(rate)
    }
  })

  it('所有 Gemini 行都链接到 Google 官方对应价格段落', () => {
    const gemini = rows.filter((row) => row.modelId.startsWith('gemini-'))
    for (const row of gemini) {
      expect(row.source, row.modelId).toBe(`https://ai.google.dev/gemini-api/docs/pricing#${row.modelId}`)
    }
  })
})

describe('Meta Muse · 官方基础价与 OpenCode Go 覆盖价', () => {
  it('基础模型使用 Meta 官方美元价', () => {
    for (const id of ['muse-spark-1.3', 'muse-spark-1.2']) {
      expect(byId(id).tiers[0]?.rate).toEqual({
        input: 1.25,
        output: 4.25,
        cacheRead: 0.15,
      })
      expect(byId(id).providerId).toBeNull()
    }
  })

  it('contributor SKU 使用 OpenCode Go 的供应商覆盖价', () => {
    for (const id of ['muse-spark-1.3-contributor', 'muse-spark-1.2-contributor']) {
      expect(byId(id, 'opencode-go').tiers[0]?.rate).toEqual({
        input: 0.1,
        output: 0.2,
        cacheRead: 0.002,
      })
      expect(byId(id, 'opencode-go').source).toBe('https://opencode.ai/docs/go/')
    }
  })
})

/**
 * ★★ `NOT_SEEDED` 的测试防的是**相反**的事:
 * 有人看见表里没有 Mistral,以为是漏了,「顺手」补回来 —— 而那批数据是单源的。
 *
 * 把排除项写成可断言的数据(而不是散落的注释),排除决定才有牙齿。
 */
describe('NOT_SEEDED · 排除项是决定,不是遗漏', () => {
  it('每条都写清了是什么、为什么', () => {
    expect(NOT_SEEDED.length).toBeGreaterThan(5)
    for (const n of NOT_SEEDED) {
      expect(n.what.trim()).not.toBe('')
      expect(n.why.length, n.what).toBeGreaterThan(20)
    }
  })

  /** 被点名排除的厂商,不能同时出现在种子表里 —— 一处改了另一处必须跟着改 */
  it('被排除的厂商确实不在表里', () => {
    const banned = ['mistral', 'magistral']
    for (const p of rows) {
      for (const b of banned) {
        expect(p.modelId.toLowerCase().includes(b), `${p.modelId} 命中排除项 ${b}`).toBe(false)
      }
    }
  })

  it('不把 InternLM 公测配额误写成零价或第三方托管价格', () => {
    const internlm = NOT_SEEDED.find((row) => row.what.includes('InternLM'))
    expect(internlm?.why).toContain('没有公开按量输入、输出或缓存单价')
    expect(internlm?.why).toContain('不能把配额制公测服务推断为零价')
    expect(rows.some((row) => row.modelId.startsWith('intern-s') || row.modelId.startsWith('internvl3.5'))).toBe(false)
  })

  it('不把 openPangu Pro 的单次请求 Token 阶梯猜成输入 Token 阶梯', () => {
    const pangu = NOT_SEEDED.find((row) => row.what.includes('openPangu 2.0 Pro'))
    expect(pangu?.why).toContain('单次请求的Token数')
    expect(pangu?.why).toContain('输入 Token 数')
    expect(pangu?.why).toContain('31,999')
    expect(rows.some((row) => row.modelId === 'openpangu-2.0-pro')).toBe(false)
  })

  it('StepFun 与百川只保留精确缺口，不再按厂商全系排除', () => {
    const stepfun = NOT_SEEDED.find((row) => row.what.startsWith('StepFun'))
    expect(stepfun?.what).toContain('旧型号')
    expect(stepfun?.why).toContain('step-3.7-flash')
    expect(stepfun?.why).toContain('非 Token')

    const baichuan = NOT_SEEDED.find((row) => row.what.startsWith('百川'))
    expect(baichuan?.what).toContain('搜索费用')
    expect(baichuan?.why).toContain('十个文本 Token 型号')
    expect(baichuan?.why).toContain('Baichuan-Omni-1.5')
  })

  it('豆包与百度只保留现有计费结构无法无损表达的精确缺口', () => {
    const doubao = NOT_SEEDED.find((row) => row.what.startsWith('豆包'))
    expect(doubao?.why).toContain('已收录')
    /*
     * ★ 原先这里钉的是「Seed 1.8 / Seed 1.6 Lite 的首档还依赖输出是否超过 200 Token」——
     * 那句描述的是当时收录的型号,而目录里那几行早已退役,理由正文里的型号名会随目录
     * 一起过期。改成钉**计费维度本身**(音频输入 / Token×小时 / 按张按秒),
     * 那是这一条排除项真正不变的部分。
     */
    expect(doubao?.why).toContain('音频输入')
    expect(doubao?.why).toContain('Token×小时')
    expect(doubao?.why).toContain('按张或秒计费')

    const baidu = NOT_SEEDED.find((row) => row.what.startsWith('百度'))
    expect(baidu?.why).toContain('已收录')
    expect(baidu?.why).toContain('搜索增强按次')
    expect(baidu?.why).toContain('ERNIE X1.1 正式版')
  })

  it('腾讯混元只保留旧平台与媒体等不同计费单位缺口', () => {
    const hunyuan = NOT_SEEDED.find((row) => row.what.startsWith('腾讯混元'))
    expect(hunyuan?.why).toContain('已收录 TokenHub 当前七款')
    expect(hunyuan?.why).toContain('已停服')
    expect(hunyuan?.why).toContain('按张、秒、字符')
  })

  /** 只允许已从 Google 官方页逐款核实并有明确 Standard 价的 Flash 型号。 */
  it('Gemini Flash 收录当前逐款核实的完整 Standard 价格集合', () => {
    const flash = rows.filter((p) => p.modelId.startsWith('gemini') && p.modelId.includes('flash'))
    expect([...new Set(flash.map((p) => p.modelId))]).toEqual(['gemini-3.8-flash', 'gemini-3.7-flash', 'gemini-3.6-flash', 'gemini-3.5-flash', 'gemini-3.5-flash-lite', 'gemini-3.1-flash-lite', 'gemini-3-flash-preview', 'gemini-2.5-flash', 'gemini-2.5-flash-lite'])
    expect(flash).toHaveLength(12)
  })

  it('明确保留按音频输入模态区分价格的建模缺口', () => {
    const gap = NOT_SEEDED.find((row) => row.what.includes('音频输入差价'))
    expect(gap?.what).toContain('Gemini')
    expect(gap?.why).toContain('TokenRates')
    expect(gap?.why).toContain('text/image/video')
  })
})
