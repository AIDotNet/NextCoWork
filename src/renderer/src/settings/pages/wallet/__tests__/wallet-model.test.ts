import { describe, expect, it } from 'vitest'
import type { RechargeOptions } from '../../../../../../shared/domain/recharge'
import {
  formatAmount,
  formatBalance,
  isSettledPhase,
  orderPhaseOf,
  rechargeGate
} from '../wallet-model'

const options = (patch: Partial<RechargeOptions> = {}): RechargeOptions => ({
  currency: 'USD', amounts: [5, 10], rechargeEnabled: true, canRecharge: true, blockedReason: null, ...patch
})

describe('金额格式化', () => {
  it('groups the balance and keeps two decimals', () => {
    expect(formatBalance(5699.04, 'en-US')).toBe('5,699.04')
  })

  it('formats top-up amounts as whole currency and falls back when the currency code is invalid', () => {
    expect(formatAmount(10, 'USD', 'en-US')).toBe('$10')
    expect(formatAmount(10, 'not-a-code', 'en-US')).toBe('10 not-a-code')
  })
})

describe('充值入口', () => {
  it('only offers the top-up button when the server says the team can recharge and top-ups are enabled', () => {
    expect(rechargeGate(null)).toEqual({ kind: 'loading' })
    expect(rechargeGate({ kind: 'ready', options: options() }).kind).toBe('ready')
    expect(rechargeGate({ kind: 'ready', options: options({ canRecharge: false, blockedReason: 'role' }) }))
      .toEqual({ kind: 'blocked', reason: 'role' })
    expect(rechargeGate({ kind: 'ready', options: options({ rechargeEnabled: false }) }))
      .toEqual({ kind: 'blocked', reason: 'disabled' })
    expect(rechargeGate({ kind: 'ready', options: options({ amounts: [] }) }))
      .toEqual({ kind: 'blocked', reason: 'disabled' })
    expect(rechargeGate({ kind: 'unavailable', reason: 'unsupported' }))
      .toEqual({ kind: 'blocked', reason: 'unsupported' })
  })
})

describe('订单轮询阶段', () => {
  const order = (status: 'Pending' | 'Paid' | 'Completed' | 'Cancelled'): Parameters<typeof orderPhaseOf>[0] =>
    ({ kind: 'ready', order: { orderNo: 'RC1', status, amount: 10, currency: 'USD' } })

  it('keeps polling while pending or paid-but-not-credited, and stops on an outcome', () => {
    expect(orderPhaseOf(order('Pending'))).toBe('waiting')
    expect(orderPhaseOf(order('Paid'))).toBe('paid')
    expect(isSettledPhase('waiting')).toBe(false)
    expect(isSettledPhase('paid')).toBe(false)
    expect(isSettledPhase(orderPhaseOf(order('Completed')) ?? 'waiting')).toBe(true)
    expect(isSettledPhase(orderPhaseOf(order('Cancelled')) ?? 'waiting')).toBe(true)
  })

  it('does not turn a transient network failure into an error while the user is paying', () => {
    expect(orderPhaseOf({ kind: 'unavailable', reason: 'network' })).toBeNull()
    expect(orderPhaseOf({ kind: 'unavailable', reason: 'signed-out' })).toBe('signed-out')
    expect(orderPhaseOf({ kind: 'not-found' })).toBe('not-found')
  })
})
