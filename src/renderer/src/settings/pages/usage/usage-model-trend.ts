/**
 * 趋势图的「按模型拆分」序列。
 *
 * 需求:趋势图不能只画一条总量线 —— 用户要看出「这几天的量是哪个模型跑出来的」。
 * 所以每个时间点除了总量,还带一份 `byModel`,趋势图据此画堆叠面积,
 * 颜色与环形图 / 费用表按同一个 key 查(`charts/colors.ts`)。
 *
 * 不变式:
 * - **总量走 `applyGranularity`,不在这里重算。** 点位(哪些日期、每周从哪天起)
 *   和总量都复用 `usage-overview.ts` 的既有口径;这里只负责把同一口径下的 token
 *   再按模型分一次。两份实现的话,堆叠的顶和总量线会差一截,而两边各自看都对。
 * - **`byModel` 各项之和 = `tokens`。** 不在 `seriesKeys` 里的模型并进 `othersKey`;
 *   若 `seriesKeys` 里压根没有「其他」(模型数没超过上限),就不会出现未归属的模型。
 *
 * 放在独立文件而不是继续堆进 `usage-overview.ts`:那个文件已经 500+ 行。
 */
import type { UsageDailyBucket } from '../../../../../shared/domain/usage'
import {
  applyGranularity,
  bucketTokens,
  fillDayGaps,
  modelKeyOf,
  toDayTotals,
  weekStartOf,
  type UsageGranularity
} from './usage-overview'

export interface ModelTrendPoint {
  /** 日粒度 / 累计:当天;周粒度:该周周日。 */
  day: string
  tokens: number
  requests: number
  /** key → token 数。缺项等于 0(稀疏,省得每个点都铺满全部模型)。 */
  byModel: Record<string, number>
}

export function toModelTrend(
  buckets: readonly UsageDailyBucket[],
  seriesKeys: readonly string[],
  othersKey: string,
  granularity: UsageGranularity
): ModelTrendPoint[] {
  const days = toDayTotals(buckets)
  if (days.length === 0) return []
  // ★ 先补洞再聚合 —— 理由同 `UsageOverview` 里那段:没用量的日子必须是 0 点
  const totals = applyGranularity(
    fillDayGaps(days, days[0]!.day, days[days.length - 1]!.day),
    granularity
  )

  const known = new Set(seriesKeys)
  const hasOthers = known.has(othersKey)
  const periodOf = granularity === 'weekly' ? weekStartOf : (day: string): string => day

  const byPeriod = new Map<string, Map<string, number>>()
  for (const bucket of buckets) {
    const raw = modelKeyOf(bucket)
    const key = known.has(raw) ? raw : hasOthers ? othersKey : raw
    const period = periodOf(bucket.day)
    let models = byPeriod.get(period)
    if (models === undefined) {
      models = new Map()
      byPeriod.set(period, models)
    }
    models.set(key, (models.get(key) ?? 0) + bucketTokens(bucket))
  }

  if (granularity === 'cumulative') {
    const running = new Map<string, number>()
    return totals.map((total) => {
      for (const [key, tokens] of byPeriod.get(total.day) ?? []) {
        running.set(key, (running.get(key) ?? 0) + tokens)
      }
      return {
        day: total.day,
        tokens: total.tokens,
        requests: total.requests,
        byModel: Object.fromEntries(running)
      }
    })
  }

  return totals.map((total) => ({
    day: total.day,
    tokens: total.tokens,
    requests: total.requests,
    byModel: Object.fromEntries(byPeriod.get(total.day) ?? [])
  }))
}
