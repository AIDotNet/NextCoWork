import { describe, expect, it } from 'vitest'
import type { UsageDailyBucket } from '../../shared/domain/usage'
import {
  buildUsageSyncData,
  mergeUsageSlices,
  parseUsageSyncData,
  type UsageSlices
} from '../config-sync-usage-snapshot'

function bucket(day: string, patch: Partial<UsageDailyBucket> = {}): UsageDailyBucket {
  return {
    day, providerId: 'acme', providerName: 'Acme', upstreamModel: 'model-a', alias: '', currency: 'USD',
    requestCount: 2, successCount: 1, inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0,
    cacheWrite1hTokens: 0, thinkingTokens: 0, costMicros: 1_000, pricedCount: 2, latencySum: 300, ttftSum: 0, ttftCount: 0,
    ...patch
  }
}

describe('usage sync snapshot', () => {
  it('builds the same document regardless of device and row order', () => {
    const one: UsageSlices = new Map([
      ['device-b', { longestChatMs: 5, buckets: [bucket('2026-09-02'), bucket('2026-09-01')] }],
      ['device-a', { longestChatMs: 1, buckets: [bucket('2026-09-01', { upstreamModel: 'model-b' })] }]
    ])
    const two: UsageSlices = new Map([
      ['device-a', { longestChatMs: 1, buckets: [bucket('2026-09-01', { upstreamModel: 'model-b' })] }],
      ['device-b', { longestChatMs: 5, buckets: [bucket('2026-09-01'), bucket('2026-09-02')] }]
    ])
    expect(JSON.stringify(buildUsageSyncData(one))).toBe(JSON.stringify(buildUsageSyncData(two)))
  })

  it('round-trips unpriced buckets as null cost and empty currency, never as free', () => {
    const slices: UsageSlices = new Map([
      ['device-a', { longestChatMs: 0, buckets: [bucket('2026-09-01', { costMicros: null, currency: '', pricedCount: 0 })] }]
    ])
    const parsed = parseUsageSyncData(JSON.parse(JSON.stringify(buildUsageSyncData(slices))))
    expect(parsed.get('device-a')?.buckets[0]).toMatchObject({ costMicros: null, currency: '' })
  })

  it('replaces only this device slice when merging', () => {
    const remote: UsageSlices = new Map([
      ['device-a', { longestChatMs: 9, buckets: [bucket('2026-09-01', { inputTokens: 1 })] }],
      ['device-b', { longestChatMs: 3, buckets: [bucket('2026-09-01', { inputTokens: 2 })] }]
    ])
    const merged = mergeUsageSlices(remote, 'device-a', { longestChatMs: 10, buckets: [bucket('2026-09-01', { inputTokens: 7 })] })
    expect(merged.get('device-a')?.buckets[0]?.inputTokens).toBe(7)
    expect(merged.get('device-b')?.buckets[0]?.inputTokens).toBe(2)
    expect(remote.get('device-a')?.buckets[0]?.inputTokens).toBe(1)
  })

  it('rejects malformed rows before anything touches the database', () => {
    expect(() => parseUsageSyncData({ devices: { a: { longestChatMs: 0, rows: [['not-a-day']] } } })).toThrow()
    expect(() => parseUsageSyncData({ devices: { a: { longestChatMs: -1, rows: [] } } })).toThrow()
  })
})
