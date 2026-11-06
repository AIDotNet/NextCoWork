/**
 * models.dev `api.json` → 定价行 —— 纯函数,不碰网络也不碰 DB。
 *
 * ★ **models.dev 是聚合源,不是官方价目表。** `pricing-seed.ts` 文件头记着聚合源
 * 犯过的错(多收、少收一半、量纲错),所以这里**只产出候选**:`diffPricing` 把候选
 * 与当前生效价对比,逐条交给用户确认,不会静默覆盖已核实的数字。
 *
 * ★ 只收 **USD 计价的一手厂商**(`SYNC_VENDORS`)。models.dev 的记录没有币种字段,
 * 国内厂商里有人民币计价的,混进来会把 ¥ 当成 $ —— 看着合理的错数。
 */
import { findPricing, type ModelPricing, type PriceTier, type TokenRates } from './pricing'

export const MODELS_DEV_URL = 'https://models.dev/api.json'

/** models.dev 的 provider id。都是 USD 计价的官方直连渠道 */
export const SYNC_VENDORS: readonly string[] = ['anthropic', 'openai', 'google', 'xai']

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const num = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : undefined)

function rateOf(cost: Record<string, unknown>): TokenRates | null {
  const input = num(cost['input'])
  const output = num(cost['output'])
  if (input === undefined || output === undefined) return null
  const cacheRead = num(cost['cache_read'])
  const cacheWrite = num(cost['cache_write'])
  return {
    input,
    output,
    ...(cacheRead === undefined ? {} : { cacheRead }),
    ...(cacheWrite === undefined ? {} : { cacheWrite })
  }
}

/**
 * 阶梯:优先 `tiers`(type=context,按 size 升序),退回 `context_over_200k`。
 * 每个 tier 条目的费率是「超过 size 之后」的价,所以第 i 档的上界是第 i 个 size。
 * 末档无上界。任何一档缺 input/output 就整模型按单档处理 —— 宁可少一个阶梯也不编。
 */
function tiersOf(cost: Record<string, unknown>, base: TokenRates): readonly PriceTier[] {
  const steps: { size: number; rate: TokenRates }[] = []
  const raw = cost['tiers']
  if (Array.isArray(raw)) {
    for (const t of raw) {
      if (!isRecord(t) || !isRecord(t['tier']) || t['tier']['type'] !== 'context') continue
      const size = num(t['tier']['size'])
      const rate = rateOf(t)
      if (size === undefined || size <= 0 || rate === null) return [{ upToInputTokens: null, rate: base }]
      steps.push({ size, rate })
    }
  } else if (isRecord(cost['context_over_200k'])) {
    const rate = rateOf(cost['context_over_200k'])
    if (rate !== null) steps.push({ size: 200_000, rate })
  }
  if (steps.length === 0) return [{ upToInputTokens: null, rate: base }]
  steps.sort((a, b) => a.size - b.size)
  const tiers: PriceTier[] = []
  let prev = base
  for (const s of steps) {
    tiers.push({ upToInputTokens: s.size, rate: prev })
    prev = s.rate
  }
  tiers.push({ upToInputTokens: null, rate: prev })
  return tiers
}

/** 解析整份 api.json;结构不对的条目静默跳过,整体不是对象则抛 */
export function parseModelsDev(json: unknown, fetchedAt: string): ModelPricing[] {
  if (!isRecord(json)) throw new Error('models.dev 返回的不是对象。')
  const rows: ModelPricing[] = []
  const seen = new Set<string>()
  for (const vendor of SYNC_VENDORS) {
    const provider = json[vendor]
    if (!isRecord(provider) || !isRecord(provider['models'])) continue
    for (const [key, m] of Object.entries(provider['models'])) {
      if (!isRecord(m) || !isRecord(m['cost'])) continue
      const modelId = typeof m['id'] === 'string' && m['id'] !== '' ? m['id'] : key
      if (seen.has(modelId)) continue
      const base = rateOf(m['cost'])
      if (base === null) continue
      seen.add(modelId)
      rows.push({
        providerId: null,
        modelId,
        displayName: typeof m['name'] === 'string' && m['name'] !== '' ? m['name'] : modelId,
        currency: 'USD',
        modality: 'text',
        tiers: tiersOf(m['cost'], base),
        source: `${MODELS_DEV_URL}#${vendor}/${modelId}`,
        fetchedAt
      })
    }
  }
  return rows
}

export type PricingDiffKind = 'new' | 'changed'

export interface PricingDiffItem {
  modelId: string
  kind: PricingDiffKind
  /** 当前生效的价;`new` 没有 */
  current?: ModelPricing
  incoming: ModelPricing
}

/** `pricing:syncPreview` 的返回:候选与当前价的差异 */
export interface PricingSyncPreview {
  fetchedAt: string
  /** models.dev 里解析出的候选总数 */
  total: number
  items: PricingDiffItem[]
}

const RATE_FIELDS = ['input', 'output', 'cacheRead', 'cacheWrite'] as const

/** 只比 models.dev 给得出的四项;它不提供的(1h 缓存写、perCall)不算差异 */
function sameRates(a: TokenRates, b: TokenRates): boolean {
  return RATE_FIELDS.every((f) => a[f] === b[f])
}

function sameTiers(a: readonly PriceTier[], b: readonly PriceTier[]): boolean {
  return a.length === b.length && a.every((t, i) => t.upToInputTokens === b[i]!.upToInputTokens && sameRates(t.rate, b[i]!.rate))
}

/**
 * 入库前把当前价里 models.dev 没有的字段(`cacheWrite1h`、`perCall`)带过来 ——
 * 否则覆盖之后 1h 缓存写入会变成「查不到价」而不是原来的价。仅在档数一致时才能逐档对应。
 */
function carryOver(current: ModelPricing, incoming: ModelPricing): ModelPricing {
  if (current.tiers.length !== incoming.tiers.length) return incoming
  return {
    ...incoming,
    tiers: incoming.tiers.map((t, i) => {
      const old = current.tiers[i]!.rate
      return {
        ...t,
        rate: {
          ...t.rate,
          ...(old.cacheWrite1h === undefined ? {} : { cacheWrite1h: old.cacheWrite1h }),
          ...(old.perCall === undefined ? {} : { perCall: old.perCall })
        }
      }
    })
  }
}

/** 候选 vs 当前生效表(`at` 时刻)。相同的不返回 */
export function diffPricing(table: readonly ModelPricing[], candidates: readonly ModelPricing[], at: number): PricingDiffItem[] {
  const items: PricingDiffItem[] = []
  for (const incoming of candidates) {
    const current = findPricing(table, null, incoming.modelId, at)
    if (current === null) {
      items.push({ modelId: incoming.modelId, kind: 'new', incoming })
    } else if (current.currency !== incoming.currency || !sameTiers(current.tiers, incoming.tiers)) {
      items.push({ modelId: incoming.modelId, kind: 'changed', current, incoming: carryOver(current, incoming) })
    }
  }
  return items
}

/**
 * 种子 + 同步行。同一 `(providerId, modelId)` 只要同步行存在,种子行**整组**让位
 * (含带日期区间的多行 —— 用户已选择采用同步价);同步行之间按 `effectiveFrom` 区分。
 */
export function mergePricing(seed: readonly ModelPricing[], synced: readonly ModelPricing[]): ModelPricing[] {
  if (synced.length === 0) return [...seed]
  const key = (p: ModelPricing): string => `${p.providerId ?? ''}\u0000${p.modelId}`
  const overridden = new Set(synced.map(key))
  return [...seed.filter((p) => !overridden.has(key(p))), ...synced]
}
