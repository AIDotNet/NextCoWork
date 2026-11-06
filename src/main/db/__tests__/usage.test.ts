import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { UsageAttemptRecord } from '../../../shared/domain/usage'
import { closeDatabase, openDatabase } from '../index'
import {
  getUsageModelStats,
  getUsageProviderStats,
  getUsageRequestLogs,
  getSessionUsageAttempts,
  getUsageSummary,
  recordUsageAttempt,
  updateUsageToolsForRun
} from '../repo'

let directory = ''

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'nextcowork-usage-'))
  openDatabase(directory)
})

afterEach(() => {
  closeDatabase()
  rmSync(directory, { recursive: true, force: true })
})

function attempt(
  id: string,
  at: number,
  patch: Partial<UsageAttemptRecord> = {}
): UsageAttemptRecord {
  return {
    id,
    at,
    runId: `run-${id}`,
    workspaceId: 'workspace-1',
    sessionId: 'session-1',
    attempt: 1,
    providerId: 'provider-1',
    providerName: 'Provider One',
    protocol: 'anthropic',
    endpoint: 'https://provider.example/v1/messages',
    alias: 'assistant',
    upstreamModel: 'model-a',
    responseModel: 'model-a-20260901',
    inputTokens: 100,
    outputTokens: 20,
    cacheReadTokens: 300,
    cacheWriteTokens: 50,
    cacheWrite1hTokens: 10,
    thinkingTokens: 5,
    thinkingTokensEstimated: false,
    latencyMs: 1_200,
    timeToFirstTokenMs: 250,
    ok: true,
    httpStatus: 200,
    errorKind: null,
    errorMessage: null,
    stopReason: 'end_turn',
    costMicros: 1_500_000,
    currency: 'USD',
    pricingTier: 0,
    pricingWindow: null,
    toolCalls: 2,
    toolErrors: 0,
    ...patch
  }
}

describe('usage repository', () => {
  it('stores detailed attempt metadata and returns filtered, paged logs', () => {
    recordUsageAttempt(attempt('a', 1_000))
    recordUsageAttempt(
      attempt('b', 2_000, {
        providerId: 'provider-2',
        providerName: 'Fallback Cloud',
        upstreamModel: 'model-b',
        ok: false,
        httpStatus: 429,
        errorKind: 'rate_limit',
        errorMessage: 'too many requests',
        stopReason: null,
        inputTokens: 0,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheWriteTokens: 0,
        thinkingTokens: null,
        timeToFirstTokenMs: null,
        costMicros: null,
        currency: null
      })
    )

    const page = getUsageRequestLogs({
      from: 500,
      to: 3_000,
      query: 'fallback',
      status: 'failed',
      limit: 10
    })

    expect(page.total).toBe(1)
    expect(page.items[0]).toMatchObject({
      id: 'b',
      providerName: 'Fallback Cloud',
      httpStatus: 429,
      errorKind: 'rate_limit',
      errorMessage: 'too many requests'
    })
  })

  it('returns every attempt for only the requested session, including fallback models and failed retries', () => {
    recordUsageAttempt(attempt('a', 1_000))
    recordUsageAttempt(attempt('b', 2_000, { runId: 'run-a', upstreamModel: 'model-b', ok: false, costMicros: null, currency: null }))
    recordUsageAttempt(attempt('foreign', 3_000, { sessionId: 'session-2' }))
    expect(getSessionUsageAttempts('session-1').map((row) => [row.id, row.upstreamModel, row.costMicros]))
      .toEqual([['a', 'model-a', 1_500_000], ['b', 'model-b', null]])
    expect(getSessionUsageAttempts('session-2').map((row) => row.id)).toEqual(['foreign'])
  })

  it('aggregates token, cache, cost, latency, model, provider, and tool totals', () => {
    recordUsageAttempt(attempt('a', 1_000))
    recordUsageAttempt(
      attempt('b', 2_000, {
        inputTokens: 50,
        outputTokens: 10,
        cacheReadTokens: 0,
        cacheWriteTokens: 0,
        cacheWrite1hTokens: 0,
        thinkingTokens: 3,
        thinkingTokensEstimated: true,
        latencyMs: 800,
        timeToFirstTokenMs: 150,
        costMicros: 500_000,
        toolCalls: 1,
        toolErrors: 1
      })
    )
    recordUsageAttempt(attempt('outside', 10_000, { costMicros: 99_000_000 }))

    const window = { from: 0, to: 3_000 }
    const summary = getUsageSummary(window)
    expect(summary).toMatchObject({
      requestCount: 2,
      successCount: 2,
      inputTokens: 150,
      outputTokens: 30,
      cacheReadTokens: 300,
      cacheWriteTokens: 50,
      cacheWrite1hTokens: 10,
      thinkingTokens: 8,
      estimatedThinkingRequestCount: 1,
      totalTokens: 530,
      averageLatencyMs: 1_000,
      averageTimeToFirstTokenMs: 200,
      toolCalls: 3,
      toolErrors: 1,
      costs: [{ currency: 'USD', micros: 2_000_000 }]
    })
    expect(summary.cacheHitRate).toBeCloseTo(300 / 450)

    expect(getUsageProviderStats(window)[0]).toMatchObject({
      id: 'provider-1',
      label: 'Provider One',
      requestCount: 2,
      costs: [{ currency: 'USD', micros: 2_000_000 }]
    })
    expect(getUsageModelStats(window)[0]).toMatchObject({
      id: 'model-a',
      requestCount: 2,
      thinkingTokens: 8
    })
  })

  it('finalizes tool outcomes on the latest successful tool-use attempt only', () => {
    recordUsageAttempt(
      attempt('retry-failed', 1_000, {
        runId: 'shared-run',
        ok: false,
        stopReason: null,
        toolCalls: 0
      })
    )
    recordUsageAttempt(
      attempt('tool-turn', 2_000, {
        runId: 'shared-run',
        stopReason: 'tool_use',
        toolCalls: 2,
        toolErrors: 0
      })
    )
    recordUsageAttempt(
      attempt('final-turn', 3_000, {
        runId: 'shared-run',
        stopReason: 'end_turn',
        toolCalls: 0,
        toolErrors: 0
      })
    )

    expect(updateUsageToolsForRun('shared-run', 2, 1)).toBe(true)
    expect(updateUsageToolsForRun('missing-run', 1, 1)).toBe(false)

    const page = getUsageRequestLogs({ from: 0, to: 4_000, limit: 10 })
    expect(page.items.find((row) => row.id === 'tool-turn')).toMatchObject({
      toolCalls: 2,
      toolErrors: 1
    })
    expect(page.items.find((row) => row.id === 'final-turn')).toMatchObject({
      toolCalls: 0,
      toolErrors: 0
    })
  })
})
