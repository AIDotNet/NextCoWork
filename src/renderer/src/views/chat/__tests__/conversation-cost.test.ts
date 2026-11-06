import { describe, expect, it } from 'vitest'
import type { UsageAttemptRecord } from '../../../../../shared/domain/usage'
import { summarizeModelCosts } from '../conversation-cost'

const attempt = (overrides: Partial<UsageAttemptRecord> = {}): UsageAttemptRecord => ({
  id: 'a', at: Date.parse('2026-09-06T12:00:00Z'), runId: 'run-1',
  workspaceId: 'workspace', sessionId: 'session', attempt: 1,
  providerId: 'provider', providerName: 'Provider', protocol: 'unknown', endpoint: '',
  alias: 'alias', upstreamModel: 'claude-sonnet-5', responseModel: null,
  inputTokens: 1000, outputTokens: 500, cacheReadTokens: 200,
  cacheWriteTokens: 100, cacheWrite1hTokens: 0, thinkingTokens: null,
  thinkingTokensEstimated: false, latencyMs: 100, timeToFirstTokenMs: null,
  ok: true, httpStatus: 200, errorKind: null, errorMessage: null, stopReason: null,
  costMicros: 10_000, currency: 'USD', pricingTier: 0, pricingWindow: null,
  toolCalls: 0, toolErrors: 0, ...overrides
})

describe('summarizeModelCosts', () => {
  it('groups attempts by actual model and provider even within one run', () => {
    const groups = summarizeModelCosts([
      attempt(), attempt({ id: 'b', upstreamModel: 'claude-opus-5', costMicros: 20_000 }),
      attempt({ id: 'c', providerId: 'other', costMicros: 30_000 }),
      attempt({ id: 'd', costMicros: 40_000 })
    ])
    expect(groups).toHaveLength(3)
    expect(groups.find((group) => group.model === 'claude-sonnet-5' && group.provider === 'Provider')?.micros).toBe(50_000)
    expect(groups.find((group) => group.model === 'claude-opus-5')?.micros).toBe(20_000)
    expect(groups.find((group) => group.key.includes('other'))?.micros).toBe(30_000)
  })

  it('keeps frozen total when estimated category charges are available', () => {
    const [group] = summarizeModelCosts([attempt()])
    expect(group?.micros).toBe(10_000)
    expect(group?.parts === null ? null : Object.values(group?.parts ?? {}).reduce((sum, part) => sum + part, 0)).toBe(10_000)
    expect(group?.parts?.other).toBe(0)
  })

  it('uses the frozen request tier for output rather than the lowest input tier', () => {
    const [group] = summarizeModelCosts([attempt({
      upstreamModel: 'gpt-5.6-sol', inputTokens: 300_000, outputTokens: 1_000,
      cacheReadTokens: 0, cacheWriteTokens: 0, pricingTier: 1, costMicros: 2_430_000
    })])
    expect(group?.parts?.output).toBe(30_000)
    expect(group?.parts?.input).toBe(2_400_000)
  })

  it('splits 5-minute writes from their 1-hour subset', () => {
    const [group] = summarizeModelCosts([attempt({ cacheWriteTokens: 1000, cacheWrite1hTokens: 300 })])
    expect(group?.tokens.cacheWrite).toBe(700)
    expect(group?.tokens.cacheWrite1h).toBe(300)
  })

  it('does not add charges across currencies for the same model', () => {
    const [group] = summarizeModelCosts([attempt(), attempt({ id: 'b', currency: 'CNY' })])
    expect(group?.mixedCurrency).toBe(true)
    expect(group?.micros).toBeNull()
  })

  it('does not report a partial total or invented categories for unpriced attempts', () => {
    const [group] = summarizeModelCosts([attempt(), attempt({ id: 'b', costMicros: null, currency: null })])
    expect(group?.micros).toBeNull()
    expect(group?.parts).toBeNull()
    expect(group?.unpriced).toBe(true)
    const [unknown] = summarizeModelCosts([attempt({ costMicros: null, currency: null })])
    expect(unknown?.micros).toBeNull()
  })
})
