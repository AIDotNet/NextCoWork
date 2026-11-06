/**
 * 概览查询合并其它设备同步来的日汇总(`usage_daily_remote`)的边界:
 * 同键求和、「算不出钱」不被当成免费、按同步账户隔离、本机那片不会出现两份。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { UsageDailyBucket } from '../../../shared/domain/usage'
import { localDayOf } from '../../../shared/domain/usage-activity'
import { closeDatabase, openDatabase, stmt } from '../index'
import { configureSyncAccount, ensureSyncDeviceId } from '../repo'
import { getUsageActivityStats, getUsageDailySeries } from '../usage-rollup'
import { clearRemoteUsage, listRemoteUsage, replaceRemoteUsage } from '../usage-remote'

let directory = ''

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'nextcowork-usage-remote-'))
  openDatabase(directory)
})

afterEach(() => {
  closeDatabase()
  rmSync(directory, { recursive: true, force: true })
})

const day = localDayOf(Date.now())
const window = { to: Date.now() + 60 * 60 * 1000 }

function bucket(patch: Partial<UsageDailyBucket> = {}): UsageDailyBucket {
  return {
    day, providerId: 'acme', providerName: 'Acme', upstreamModel: 'model-a', alias: 'A', currency: 'USD',
    requestCount: 1, successCount: 1, inputTokens: 10, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
    cacheWrite1hTokens: 0, thinkingTokens: 0, costMicros: null, pricedCount: 0, latencySum: 100, ttftSum: 0, ttftCount: 0,
    ...patch
  }
}

/** 直接写本机汇总表 —— 这里测的是合并查询,不是汇总刷新。 */
function putLocal(b: UsageDailyBucket): void {
  stmt(`INSERT INTO usage_daily (day, provider_id, provider_name, upstream_model, alias, currency,
          request_count, success_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
          cache_write_1h_tokens, thinking_tokens, cost_micros, priced_count, latency_sum, ttft_sum, ttft_count)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run(
    b.day, b.providerId, b.providerName, b.upstreamModel, b.alias, b.currency, b.requestCount, b.successCount,
    b.inputTokens, b.outputTokens, b.cacheReadTokens, b.cacheWriteTokens, b.cacheWrite1hTokens, b.thinkingTokens,
    b.costMicros, b.pricedCount, b.latencySum, b.ttftSum, b.ttftCount
  )
}

describe('usage overview merged with synced devices', () => {
  it('sums the same bucket across devices and keeps unpriced remote rows from zeroing the cost', () => {
    configureSyncAccount('acct', true)
    putLocal(bucket({ inputTokens: 10, costMicros: 500, pricedCount: 1 }))
    replaceRemoteUsage('acct', new Map([
      ['other', { longestChatMs: 0, buckets: [bucket({ inputTokens: 30, costMicros: null, pricedCount: 0 })] }]
    ]), ensureSyncDeviceId())

    const series = getUsageDailySeries(window)
    expect(series).toHaveLength(1)
    expect(series[0]).toMatchObject({ inputTokens: 40, requestCount: 2, costMicros: 500, pricedCount: 1 })
  })

  it('reports null cost when no device could price the bucket', () => {
    configureSyncAccount('acct', true)
    putLocal(bucket())
    replaceRemoteUsage('acct', new Map([['other', { longestChatMs: 0, buckets: [bucket()] }]]), ensureSyncDeviceId())
    expect(getUsageDailySeries(window)[0]?.costMicros).toBeNull()
  })

  it('shows remote data only for the signed-in sync account', () => {
    configureSyncAccount('acct', true)
    replaceRemoteUsage('acct', new Map([['other', { longestChatMs: 0, buckets: [bucket()] }]]), ensureSyncDeviceId())
    expect(getUsageDailySeries(window)).toHaveLength(1)

    configureSyncAccount('someone-else', true)
    expect(getUsageDailySeries(window)).toHaveLength(0)
    configureSyncAccount(null)
    expect(getUsageDailySeries(window)).toHaveLength(0)
    expect(getUsageActivityStats().syncedDevices).toBe(0)
  })

  it('drops this device own slice from a remote snapshot so local usage is never counted twice', () => {
    configureSyncAccount('acct', true)
    putLocal(bucket({ inputTokens: 10 }))
    const self = ensureSyncDeviceId()
    replaceRemoteUsage('acct', new Map([
      [self, { longestChatMs: 0, buckets: [bucket({ inputTokens: 10 })] }],
      ['other', { longestChatMs: 7_000, buckets: [bucket({ inputTokens: 1 })] }]
    ]), self)

    expect(getUsageDailySeries(window)[0]?.inputTokens).toBe(11)
    expect([...listRemoteUsage('acct').keys()]).toEqual(['other'])
    const stats = getUsageActivityStats()
    expect(stats).toMatchObject({ syncedDevices: 1, longestChatMs: 7_000, activeDays: [day] })

    clearRemoteUsage('acct')
    expect(getUsageDailySeries(window)[0]?.inputTokens).toBe(10)
  })
})
