/**
 * 概览区的全部计算 —— 日桶合并、热力图网格、粒度再聚合、模型占比、费用分组。
 *
 * 组件只负责把这里算好的结构画出来。这么切不是为了好看:vitest 是
 * `environment: 'node'` + `include: ['src/**\/*.test.ts']`,`.test.tsx` 根本不会
 * 被收 —— 逻辑留在组件里就等于永远测不到,而这一页几乎全是容易错又不会报错的
 * 算术(分位着色、周起点、跨币种)。
 *
 * ## 贯穿全文的两条约束
 *
 * 1. **绝不跨币种相加。** 费用一律 `Map<currency, micros>`,到了界面上并排显示。
 *    美元加人民币得到的数字没有单位,比不显示更糟。
 * 2. **「未计价」不是 0。** `costMicros === null` 的桶表示查不到定价,
 *    它的请求数进 `unpricedRequests`,金额一分不进合计;界面必须把这个数说出来,
 *    否则少算的钱和省下的钱长得一模一样。
 */
import type { UsageCostTotal, UsageDailyBucket } from '../../../../../shared/domain/usage'
import {
  dayFromIndex,
  dayIndex,
  dayRange,
  weekdayOf
} from '../../../../../shared/domain/usage-activity'

/** 一天的合计。跨模型、跨供应商合并之后的样子。 */
export interface DayTotal {
  day: string
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  /** 上面四项之和 —— 热力图和趋势图用的就是它。 */
  tokens: number
  requests: number
  /** 按币种分开。空数组 = 这一天没有任何可计价的请求。 */
  costs: UsageCostTotal[]
  /** 查不到定价、因而没有计入 `costs` 的请求数。 */
  unpricedRequests: number
}

function emptyDay(day: string): DayTotal {
  return {
    day,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    tokens: 0,
    requests: 0,
    costs: [],
    unpricedRequests: 0
  }
}

/** 桶的 token 总量。★ 与 `getUsageActivityStats` 里峰值那条 SQL 口径一致。 */
export function bucketTokens(bucket: UsageDailyBucket): number {
  return bucket.inputTokens + bucket.outputTokens + bucket.cacheReadTokens + bucket.cacheWriteTokens
}

/**
 * 模型的稳定键 `providerId/upstreamModel`。
 * 需求:环形图、费用表、趋势堆叠图按同一个 key 查颜色(`charts/colors.ts`),
 * 口径只能有一份 —— 各写各的模板串,改一处漏一处,颜色就会在图与图之间对不上。
 */
export function modelKeyOf(bucket: UsageDailyBucket): string {
  return `${bucket.providerId}/${bucket.upstreamModel}`
}

/** `toModelShares` 合并尾部后那一项的 key。 */
export const OTHERS_KEY = '__others__'

/** 所在周的周日(周起点口径见 `applyGranularity` 的 ★)。 */
export function weekStartOf(day: string): string {
  return dayFromIndex(dayIndex(day) - weekdayOf(day))
}

function addCost(into: Map<string, number>, bucket: UsageDailyBucket): void {
  if (bucket.costMicros === null || bucket.currency === '') return
  into.set(bucket.currency, (into.get(bucket.currency) ?? 0) + bucket.costMicros)
}

function costList(map: Map<string, number>): UsageCostTotal[] {
  return [...map.entries()]
    .map(([currency, micros]) => ({ currency, micros }) as UsageCostTotal)
    .sort((a, b) => b.micros - a.micros)
}

/** 未计入费用的请求数:整桶无定价的全算,部分计价的只算差额。 */
function unpricedOf(bucket: UsageDailyBucket): number {
  return Math.max(0, bucket.requestCount - bucket.pricedCount)
}

/** 按天合并所有桶。输出按日期升序;**不补空洞**(补洞见 `fillDayGaps`)。 */
export function toDayTotals(buckets: readonly UsageDailyBucket[]): DayTotal[] {
  const byDay = new Map<string, DayTotal>()
  const costsByDay = new Map<string, Map<string, number>>()

  for (const bucket of buckets) {
    let total = byDay.get(bucket.day)
    if (total === undefined) {
      total = emptyDay(bucket.day)
      byDay.set(bucket.day, total)
      costsByDay.set(bucket.day, new Map())
    }
    total.inputTokens += bucket.inputTokens
    total.outputTokens += bucket.outputTokens
    total.cacheReadTokens += bucket.cacheReadTokens
    total.cacheWriteTokens += bucket.cacheWriteTokens
    total.tokens += bucketTokens(bucket)
    total.requests += bucket.requestCount
    total.unpricedRequests += unpricedOf(bucket)
    addCost(costsByDay.get(bucket.day)!, bucket)
  }

  for (const [day, total] of byDay) total.costs = costList(costsByDay.get(day)!)
  return [...byDay.values()].sort((a, b) => a.day.localeCompare(b.day))
}

/**
 * 把缺失的日期补成零值行。
 *
 * ★ 折线图必须补洞。不补的话,断更三天会被画成一条直接连过去的斜线 ——
 * 看起来像那三天有稳定的中等用量,而实际是零。热力图同理:没有格子和有一个
 * 空格子在视觉上完全不同。
 */
export function fillDayGaps(totals: readonly DayTotal[], from: string, to: string): DayTotal[] {
  const byDay = new Map(totals.map((t) => [t.day, t]))
  return dayRange(from, to).map((day) => byDay.get(day) ?? emptyDay(day))
}

export type UsageGranularity = 'daily' | 'weekly' | 'cumulative'

function mergeInto(target: DayTotal, source: DayTotal): void {
  target.inputTokens += source.inputTokens
  target.outputTokens += source.outputTokens
  target.cacheReadTokens += source.cacheReadTokens
  target.cacheWriteTokens += source.cacheWriteTokens
  target.tokens += source.tokens
  target.requests += source.requests
  target.unpricedRequests += source.unpricedRequests
  const map = new Map(target.costs.map((c) => [c.currency, c.micros]))
  for (const c of source.costs) map.set(c.currency, (map.get(c.currency) ?? 0) + c.micros)
  target.costs = costList(map)
}

/**
 * 粒度再聚合。输入应当是已补洞、按日升序的序列。
 *
 * - `daily` 原样返回
 * - `weekly` 按**周日起**分组,标签取该周第一天
 * - `cumulative` 逐日累加
 *
 * ★ 周起点取周日,与 `weekdayOf`(0 = 周日)和热力图的列一致。两处不一致的话,
 * 「每周」柱子的分界会和热力图的列错开一格,而两张图各自看都是对的。
 */
export function applyGranularity(
  totals: readonly DayTotal[],
  granularity: UsageGranularity
): DayTotal[] {
  if (granularity === 'daily') return totals.map((t) => ({ ...t, costs: [...t.costs] }))

  if (granularity === 'cumulative') {
    const out: DayTotal[] = []
    const running = emptyDay('')
    for (const total of totals) {
      mergeInto(running, total)
      out.push({ ...running, day: total.day, costs: [...running.costs] })
    }
    return out
  }

  const out: DayTotal[] = []
  let current: DayTotal | null = null
  for (const total of totals) {
    const weekStart = weekStartOf(total.day)
    if (current === null || current.day !== weekStart) {
      current = emptyDay(weekStart)
      out.push(current)
    }
    mergeInto(current, total)
  }
  return out
}

// ── 热力图 ──────────────────────────────────────────────────────────────

export type HeatLevel = 0 | 1 | 2 | 3 | 4

export interface HeatCell {
  day: string
  tokens: number
  requests: number
  level: HeatLevel
}

export interface HeatmapGrid {
  /** 每列一周,列内从周日到周六共 7 格。首末两周的越界格为 `null`。 */
  weeks: (HeatCell | null)[][]
  /** 月份标签:`weekIndex` 是该月第一次出现的列号。 */
  months: { weekIndex: number; day: string }[]
  activeDays: number
  maxTokens: number
}

/**
 * 把非零值切成四档的阈值。
 *
 * ★ 用**分位数**而不是 `max/4` 等分。用量分布极度长尾:偶尔一天 4000 万 token,
 * 其余都在一两百万。按最大值等分的话,除了那一天以外全部落到第 1 档,
 * 整张图变成一片同色 —— 而它看起来只是「用得少」,不像是着色算法失效了。
 */
export function heatThresholds(values: readonly number[]): [number, number, number] {
  const nonZero = values.filter((v) => v > 0).sort((a, b) => a - b)
  if (nonZero.length === 0) return [1, 2, 3]
  const at = (q: number): number =>
    nonZero[Math.min(nonZero.length - 1, Math.floor(nonZero.length * q))]!
  // ★ 保证三档严格递增。小样本(比如只有两个非零日)下三个分位会取到同一个值,
  // 档位重合的结果是 level 3 和 4 永远取不到 —— 图只剩两种颜色,而它看起来
  // 只是「用量很平均」。
  const q1 = at(0.25)
  const q2 = Math.max(at(0.5), q1 + 1)
  const q3 = Math.max(at(0.75), q2 + 1)
  return [q1, q2, q3]
}

export function heatLevel(
  tokens: number,
  thresholds: readonly [number, number, number]
): HeatLevel {
  if (tokens <= 0) return 0
  if (tokens <= thresholds[0]) return 1
  if (tokens <= thresholds[1]) return 2
  if (tokens <= thresholds[2]) return 3
  return 4
}

/**
 * GitHub 式贡献网格。`endDay` 所在周排在最后一列,往前 `weeks` 列。
 *
 * 越界的格子给 `null` 而不是零值格:最后一列里「今天之后」的那几天还没发生,
 * 画成「零活动」是在陈述一件没发生的事。
 */
export function buildHeatmap(totals: readonly DayTotal[], endDay: string, weeks = 53): HeatmapGrid {
  const byDay = new Map(totals.map((t) => [t.day, t]))
  const endIndex = dayIndex(endDay)
  if (Number.isNaN(endIndex)) return { weeks: [], months: [], activeDays: 0, maxTokens: 0 }

  // 最后一列的周六(可能晚于 endDay),再往前推 weeks 整周
  const lastSaturday = endIndex + (6 - weekdayOf(endDay))
  const firstSunday = lastSaturday - weeks * 7 + 1

  const thresholds = heatThresholds(totals.map((t) => t.tokens))
  const grid: (HeatCell | null)[][] = []
  const months: { weekIndex: number; day: string }[] = []
  let activeDays = 0
  let maxTokens = 0
  let lastMonth = ''

  for (let w = 0; w < weeks; w++) {
    const column: (HeatCell | null)[] = []
    for (let d = 0; d < 7; d++) {
      const index = firstSunday + w * 7 + d
      if (index > endIndex) {
        column.push(null)
        continue
      }
      const day = dayFromIndex(index)
      const total = byDay.get(day)
      const tokens = total?.tokens ?? 0
      if (tokens > 0) activeDays++
      if (tokens > maxTokens) maxTokens = tokens
      column.push({
        day,
        tokens,
        requests: total?.requests ?? 0,
        level: heatLevel(tokens, thresholds)
      })
    }
    grid.push(column)

    const first = column.find((cell) => cell !== null)
    if (first !== undefined && first !== null) {
      const month = first.day.slice(0, 7)
      if (month !== lastMonth) {
        months.push({ weekIndex: w, day: first.day })
        lastMonth = month
      }
    }
  }

  return { weeks: grid, months, activeDays, maxTokens }
}

// ── 模型占比 ────────────────────────────────────────────────────────────

export interface ModelShare {
  /** 稳定键:`providerId/upstreamModel`,给 React key 和颜色分配用。 */
  key: string
  label: string
  tokens: number
  requests: number
  /** 0–1。分母是所有模型的 token 合计。 */
  share: number
  costs: UsageCostTotal[]
  unpricedRequests: number
}

function labelOf(bucket: UsageDailyBucket): string {
  return bucket.alias !== '' ? bucket.alias : bucket.upstreamModel
}

/** 撞名时用来补后缀的原始字段。 */
interface LabelSource {
  providerId: string
  providerName: string
  upstreamModel: string
}

function labelSourceOf(bucket: UsageDailyBucket): LabelSource {
  return {
    providerId: bucket.providerId,
    providerName: bucket.providerName,
    upstreamModel: bucket.upstreamModel
  }
}

/**
 * 给撞名的标签补后缀,先补供应商,还撞再补上游模型名。
 *
 * ★ 行是按「供应商 × 模型」分的,而标签只取别名 —— 同一个模型从多个供应商
 * 接入是常态(本机库里 `gpt-6-astra` 就有三个来源),不补后缀,费用表里就是
 * 三行同名不同数的行,看起来像同一行被列重了、或者像哪儿算重了。
 *
 * 只给真撞名的补:没撞名的挂上供应商只会更长,而图例里的长名字是要被截掉的。
 */
function disambiguateLabels<T extends { key: string; label: string }>(
  rows: T[],
  sources: ReadonlyMap<string, LabelSource>
): void {
  const picks = [
    (s: LabelSource): string => (s.providerName !== '' ? s.providerName : s.providerId),
    (s: LabelSource): string => s.upstreamModel
  ]
  for (const pick of picks) {
    const groups = new Map<string, T[]>()
    for (const row of rows) {
      const list = groups.get(row.label)
      if (list === undefined) groups.set(row.label, [row])
      else list.push(row)
    }

    let clean = true
    for (const list of groups.values()) {
      if (list.length < 2) continue
      clean = false
      // 「其他」那一项不在 sources 里,而且只会有一项,撞不上
      const parts = list.map((row) => {
        const source = sources.get(row.key)
        return source === undefined ? '' : pick(source)
      })
      // 这一档在这组里分不开(比如三行本来就是同一个供应商),
      // 补上去只是把每一行都变长,留给下一档
      if (new Set(parts).size < 2) continue
      list.forEach((row, index) => {
        const part = parts[index]
        if (part !== undefined && part !== '') row.label = `${row.label} · ${part}`
      })
    }
    if (clean) break
  }
}

/**
 * 按模型合并并算占比,降序。超过 `limit` 的尾部合并成一项 `key: '__others__'`。
 *
 * ★ 尾部必须合并而不是截断。19 个模型的环形图里有 11 个是细得看不见的片,
 * 图例翻三屏;而直接丢掉尾部会让占比加起来不到 100%,没人能发现少的那部分去哪了。
 */
export function toModelShares(buckets: readonly UsageDailyBucket[], limit = 8): ModelShare[] {
  const byModel = new Map<string, ModelShare>()
  const costs = new Map<string, Map<string, number>>()
  const sources = new Map<string, LabelSource>()

  for (const bucket of buckets) {
    const key = modelKeyOf(bucket)
    let share = byModel.get(key)
    if (share === undefined) {
      share = {
        key,
        label: labelOf(bucket),
        tokens: 0,
        requests: 0,
        share: 0,
        costs: [],
        unpricedRequests: 0
      }
      byModel.set(key, share)
      costs.set(key, new Map())
      sources.set(key, labelSourceOf(bucket))
    }
    share.tokens += bucketTokens(bucket)
    share.requests += bucket.requestCount
    share.unpricedRequests += unpricedOf(bucket)
    addCost(costs.get(key)!, bucket)
  }

  for (const [key, share] of byModel) share.costs = costList(costs.get(key)!)

  const sorted = [...byModel.values()].sort(
    (a, b) => b.tokens - a.tokens || a.key.localeCompare(b.key)
  )
  const total = sorted.reduce((sum, s) => sum + s.tokens, 0)

  const head = sorted.slice(0, limit)
  const tail = sorted.slice(limit)
  if (tail.length > 0) {
    const merged: ModelShare = {
      key: OTHERS_KEY,
      label: '',
      tokens: 0,
      requests: 0,
      share: 0,
      costs: [],
      unpricedRequests: 0
    }
    const map = new Map<string, number>()
    for (const item of tail) {
      merged.tokens += item.tokens
      merged.requests += item.requests
      merged.unpricedRequests += item.unpricedRequests
      for (const c of item.costs) map.set(c.currency, (map.get(c.currency) ?? 0) + c.micros)
    }
    merged.costs = costList(map)
    head.push(merged)
  }

  disambiguateLabels(head, sources)
  for (const item of head) item.share = total === 0 ? 0 : item.tokens / total
  return head
}

// ── 费用 ────────────────────────────────────────────────────────────────

export interface CostRow {
  key: string
  label: string
  micros: number
  requests: number
  /** 0–1,分母是**同币种**的合计。 */
  share: number
}

export interface CostGroup {
  currency: string
  rows: CostRow[]
  totalMicros: number
}

export interface CostBreakdown {
  groups: CostGroup[]
  /** 所有币种合计的未计价请求数。界面据此提示「合计偏低」。 */
  unpricedRequests: number
}

/**
 * 按币种分组、组内按模型降序的费用明细。
 *
 * ★ 分组是硬性的,不是展示偏好。把 USD 和 CNY 的 micros 相加会得到一个
 * 没有单位的数,而它长得和正常金额一模一样。
 */
export function toCostBreakdown(buckets: readonly UsageDailyBucket[]): CostBreakdown {
  const byCurrency = new Map<string, Map<string, CostRow>>()
  const sources = new Map<string, LabelSource>()
  let unpricedRequests = 0

  for (const bucket of buckets) {
    unpricedRequests += unpricedOf(bucket)
    if (bucket.costMicros === null || bucket.currency === '') continue

    let rows = byCurrency.get(bucket.currency)
    if (rows === undefined) {
      rows = new Map()
      byCurrency.set(bucket.currency, rows)
    }
    const key = modelKeyOf(bucket)
    const row = rows.get(key)
    if (row === undefined) {
      rows.set(key, {
        key,
        label: labelOf(bucket),
        micros: bucket.costMicros,
        requests: bucket.pricedCount,
        share: 0
      })
      sources.set(key, labelSourceOf(bucket))
    } else {
      row.micros += bucket.costMicros
      row.requests += bucket.pricedCount
    }
  }

  const groups: CostGroup[] = [...byCurrency.entries()].map(([currency, rows]) => {
    const list = [...rows.values()].sort(
      (a, b) => b.micros - a.micros || a.key.localeCompare(b.key)
    )
    // 每个币种是独立一张表,撞名只在表内算
    disambiguateLabels(list, sources)
    const totalMicros = list.reduce((sum, r) => sum + r.micros, 0)
    for (const row of list) row.share = totalMicros === 0 ? 0 : row.micros / totalMicros
    return { currency, rows: list, totalMicros }
  })
  groups.sort((a, b) => b.totalMicros - a.totalMicros)

  return { groups, unpricedRequests }
}

// ── 指标卡 ──────────────────────────────────────────────────────────────

export interface UsageTotals {
  tokens: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  requests: number
  costs: UsageCostTotal[]
  unpricedRequests: number
  modelCount: number
}

/** 时间窗内的总量。指标卡第一、第二张用它。 */
export function totalsOf(buckets: readonly UsageDailyBucket[]): UsageTotals {
  const costs = new Map<string, number>()
  const models = new Set<string>()
  const totals: UsageTotals = {
    tokens: 0,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    requests: 0,
    costs: [],
    unpricedRequests: 0,
    modelCount: 0
  }

  for (const bucket of buckets) {
    totals.inputTokens += bucket.inputTokens
    totals.outputTokens += bucket.outputTokens
    totals.cacheReadTokens += bucket.cacheReadTokens
    totals.cacheWriteTokens += bucket.cacheWriteTokens
    totals.tokens += bucketTokens(bucket)
    totals.requests += bucket.requestCount
    totals.unpricedRequests += unpricedOf(bucket)
    models.add(modelKeyOf(bucket))
    addCost(costs, bucket)
  }

  totals.costs = costList(costs)
  totals.modelCount = models.size
  return totals
}
