/**
 * 钱包充值取数的失败分流与收银页放行规则。
 *
 * ★ 钉住三件事：
 *   - 403 是业务拒绝（角色不够），不是「未登录」—— 归错了用户会被要求重新登录，而那永远不会好；
 *   - 404 是「平台还没部署这组接口」，不是网络错误；
 *   - 只有 `https://checkout.stripe.com` 才会被交给系统浏览器。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  fetch: vi.fn(),
  openExternal: vi.fn(),
  authMode: 'authenticated' as 'authenticated' | 'offline' | 'undecided',
  accessToken: 'access' as string | null
}))

vi.mock('electron', () => ({ shell: { openExternal: mocks.openExternal } }))
vi.mock('../../runtime', () => ({ getHost: () => ({ fetch: mocks.fetch }) }))
vi.mock('../client-auth', () => ({
  getClientAuthState: () => ({ mode: mocks.authMode, user: null, expiresAt: null }),
  getClientAccessToken: () => Promise.resolve(mocks.accessToken)
}))

import { createRechargeCheckout, getRechargeOptions, getRechargeOrder } from '../recharge'

const reply = (status: number, body: unknown): { ok: boolean; status: number; json: () => Promise<unknown> } =>
  ({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) })

beforeEach(() => {
  mocks.authMode = 'authenticated'
  mocks.accessToken = 'access'
  mocks.fetch.mockReset()
  mocks.openExternal.mockReset()
  mocks.openExternal.mockResolvedValue(undefined)
})

describe('充值档位', () => {
  it('reads amounts from the server envelope, coerces decimal strings and sorts them', async () => {
    mocks.fetch.mockResolvedValue(reply(200, {
      code: 200,
      data: { currency: 'USD', amounts: [20, '5', 10], rechargeEnabled: true, canRecharge: false, blockedReason: 'role' }
    }))
    expect(await getRechargeOptions()).toEqual({
      kind: 'ready',
      options: { currency: 'USD', amounts: [5, 10, 20], rechargeEnabled: true, canRecharge: false, blockedReason: 'role' }
    })
  })

  it('maps 404 to unsupported and team_context_required to team-required', async () => {
    mocks.fetch.mockResolvedValue(reply(404, {}))
    expect(await getRechargeOptions()).toEqual({ kind: 'unavailable', reason: 'unsupported' })
    mocks.fetch.mockResolvedValue(reply(409, { error: 'team_context_required', message: '请选择一个可用 Team 后继续' }))
    expect(await getRechargeOptions()).toEqual({ kind: 'unavailable', reason: 'team-required' })
  })

  it('never sends a request in offline mode', async () => {
    mocks.authMode = 'offline'
    expect(await getRechargeOptions()).toEqual({ kind: 'unavailable', reason: 'signed-out' })
    expect(mocks.fetch).not.toHaveBeenCalled()
  })
})

describe('创建充值', () => {
  it('opens the Stripe checkout in the system browser and returns only the order number', async () => {
    mocks.fetch.mockResolvedValue(reply(200, {
      code: 200,
      data: { orderNo: 'RC20260923abc', checkoutUrl: 'https://checkout.stripe.com/c/pay/cs_live_x', amount: 10, currency: 'USD' }
    }))
    expect(await createRechargeCheckout(10)).toEqual({ kind: 'opened', orderNo: 'RC20260923abc', amount: 10, currency: 'USD' })
    expect(mocks.openExternal).toHaveBeenCalledWith('https://checkout.stripe.com/c/pay/cs_live_x')
    const [, init] = mocks.fetch.mock.calls[0] as [string, RequestInit]
    expect(init.method).toBe('POST')
    expect(init.body).toBe(JSON.stringify({ amount: 10 }))
  })

  it('refuses to open a checkout url that is not checkout.stripe.com', async () => {
    mocks.fetch.mockResolvedValue(reply(200, { data: { orderNo: 'RC1', checkoutUrl: 'https://evil.example/pay' } }))
    expect(await createRechargeCheckout(10)).toEqual({ kind: 'rejected', message: null })
    expect(mocks.openExternal).not.toHaveBeenCalled()
  })

  it('treats 403 as a business rejection carrying the server message, not as signed-out', async () => {
    mocks.fetch.mockResolvedValue(reply(403, { code: 403, message: '只有 Team Owner 或 Admin 可以充值', data: null }))
    expect(await createRechargeCheckout(10)).toEqual({ kind: 'rejected', message: '只有 Team Owner 或 Admin 可以充值' })
  })

  it('maps 401 to signed-out and a bare 502 or thrown fetch to network', async () => {
    mocks.fetch.mockResolvedValue(reply(401, {}))
    expect(await createRechargeCheckout(10)).toEqual({ kind: 'unavailable', reason: 'signed-out' })
    mocks.fetch.mockResolvedValue(reply(502, {}))
    expect(await createRechargeCheckout(10)).toEqual({ kind: 'unavailable', reason: 'network' })
    mocks.fetch.mockRejectedValue(new Error('offline'))
    expect(await createRechargeCheckout(10)).toEqual({ kind: 'unavailable', reason: 'network' })
  })
})

describe('订单轮询', () => {
  it('returns the order status and treats 404 as an invisible order', async () => {
    mocks.fetch.mockResolvedValue(reply(200, { data: { orderNo: 'RC1', status: 'Completed', amount: 10, currency: 'USD' } }))
    expect(await getRechargeOrder('RC1')).toEqual({
      kind: 'ready', order: { orderNo: 'RC1', status: 'Completed', amount: 10, currency: 'USD' }
    })
    mocks.fetch.mockResolvedValue(reply(404, {}))
    expect(await getRechargeOrder('RC1')).toEqual({ kind: 'not-found' })
  })

  it('does not put arbitrary strings into the request path', async () => {
    expect(await getRechargeOrder('../account')).toEqual({ kind: 'not-found' })
    expect(mocks.fetch).not.toHaveBeenCalled()
  })
})
