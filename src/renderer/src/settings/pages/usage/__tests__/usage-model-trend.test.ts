import { describe, expect, it } from 'vitest'
import type { UsageDailyBucket } from '../../../../../../shared/domain/usage'
import { toModelTrend } from '../usage-model-trend'

function bucket(patch: Partial<UsageDailyBucket> = {}): UsageDailyBucket {
  return {
    day: '2026-09-14',
    providerId: 'p1',
    providerName: 'Provider One',
    upstreamModel: 'model-a',
    alias: '',
    currency: 'USD',
    requestCount: 1,
    successCount: 1,
    inputTokens: 100,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    cacheWrite1hTokens: 0,
    thinkingTokens: 0,
    costMicros: 0,
    pricedCount: 1,
    latencySum: 0,
    ttftSum: 0,
    ttftCount: 0,
    ...patch
  }
}

const sum = (record: Record<string, number>): number =>
  Object.values(record).reduce((a, b) => a + b, 0)

describe('toModelTrend', () => {
  it('每个点的按模型拆分之和等于总量 —— 堆叠的顶必须和总量线重合', () => {
    const points = toModelTrend(
      [bucket(), bucket({ upstreamModel: 'model-b', inputTokens: 50 })],
      ['p1/model-a', 'p1/model-b'],
      '__others__',
      'daily'
    )
    expect(points).toHaveLength(1)
    expect(points[0]?.byModel).toEqual({ 'p1/model-a': 100, 'p1/model-b': 50 })
    expect(sum(points[0]!.byModel)).toBe(points[0]?.tokens)
  })

  it('不在序列里的模型并进「其他」,而不是被丢掉', () => {
    const points = toModelTrend(
      [bucket(), bucket({ upstreamModel: 'model-tail', inputTokens: 30 })],
      ['p1/model-a', '__others__'],
      '__others__',
      'daily'
    )
    expect(points[0]?.byModel).toEqual({ 'p1/model-a': 100, __others__: 30 })
  })

  it('中间没有用量的日子补成空点,不把「歇了几天」画成斜线', () => {
    const points = toModelTrend(
      [bucket({ day: '2026-09-14' }), bucket({ day: '2026-09-16' })],
      ['p1/model-a'],
      '__others__',
      'daily'
    )
    expect(points.map((p) => p.day)).toEqual(['2026-09-14', '2026-09-15', '2026-09-16'])
    expect(points[1]?.tokens).toBe(0)
    expect(points[1]?.byModel).toEqual({})
  })

  it('每周粒度按周日起分组,与总量的分组口径一致', () => {
    // 2026-09-13 是周日;09-19 周六;09-20 下一周周日
    const points = toModelTrend(
      [
        bucket({ day: '2026-09-13' }),
        bucket({ day: '2026-09-19', upstreamModel: 'model-b' }),
        bucket({ day: '2026-09-20' })
      ],
      ['p1/model-a', 'p1/model-b'],
      '__others__',
      'weekly'
    )
    expect(points.map((p) => p.day)).toEqual(['2026-09-13', '2026-09-20'])
    expect(points[0]?.byModel).toEqual({ 'p1/model-a': 100, 'p1/model-b': 100 })
    expect(sum(points[0]!.byModel)).toBe(points[0]?.tokens)
  })

  it('累计粒度逐日累加每个模型,补出来的空日沿用前一天的累计值', () => {
    const points = toModelTrend(
      [
        bucket({ day: '2026-09-14' }),
        bucket({ day: '2026-09-16', upstreamModel: 'model-b', inputTokens: 40 })
      ],
      ['p1/model-a', 'p1/model-b'],
      '__others__',
      'cumulative'
    )
    expect(points.map((p) => p.byModel)).toEqual([
      { 'p1/model-a': 100 },
      { 'p1/model-a': 100 },
      { 'p1/model-a': 100, 'p1/model-b': 40 }
    ])
    expect(points[2]?.tokens).toBe(140)
  })

  it('没有数据时返回空数组', () => {
    expect(toModelTrend([], [], '__others__', 'daily')).toEqual([])
  })
})
