/**
 * 奖励中心取数的失败分流。
 *
 * ★ 这三种失败在界面上是三句不同的话、三种不同的下一步（重新登录 / 等平台部署 /
 *   重试），一旦被合并成一句「加载失败」，**桌面端比服务端先发版**的那一种就会
 *   变成「重试永远不会好」。所以 404 单独成一支，这里钉住它。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  fetch: vi.fn(),
  authMode: 'authenticated' as 'authenticated' | 'offline' | 'undecided',
  accessToken: 'access' as string | null
}))

vi.mock('../../runtime', () => ({ getHost: () => ({ fetch: mocks.fetch }) }))
vi.mock('../client-auth', () => ({
  getClientAuthState: () => ({ mode: mocks.authMode, user: null, expiresAt: null }),
  getClientAccessToken: () => Promise.resolve(mocks.accessToken)
}))

import { getReferralCenter } from '../referral'

const payload = {
  code: 'abc123def456',
  inviteUrl: '/login?ref=abc123def456',
  enabled: true,
  inviterAmount: 5,
  // decimal 被序列化成字符串的那一种，两种都得收
  inviteeAmount: '3.5',
  giftValidDays: 30,
  qualifyMinPaidCalls: 1,
  totalInvites: 2,
  rewardedInvites: 1,
  pendingInvites: 1,
  earnedAmount: 5,
  currency: 'USD',
  invites: [{ name: 'a***@b.c', status: 'Rewarded', amount: 5, at: '2026-09-01T00:00:00Z', rewardedAt: null }],
  rewards: [{ side: 'inviter', amount: 5, remainingAmount: 4, at: '2026-09-01T00:00:00Z', expireAt: null, revoked: false }]
}

beforeEach(() => {
  mocks.authMode = 'authenticated'
  mocks.accessToken = 'access'
  mocks.fetch.mockReset()
})

describe('奖励中心取数', () => {
  it('把相对邀请链接拼成可直接分享的绝对地址', async () => {
    mocks.fetch.mockResolvedValue({ ok: true, status: 200, json: () => Promise.resolve({ data: payload }) })
    const state = await getReferralCenter()
    expect(state.kind).toBe('ready')
    if (state.kind !== 'ready') return
    expect(state.center.inviteUrl).toBe('https://nextco.work/login?ref=abc123def456')
    // 字符串金额也要落成数字，否则界面上会出现 "3.5" 直接拼进货币格式
    expect(state.center.inviteeAmount).toBe(3.5)
    expect(state.center.invites).toHaveLength(1)
    expect(state.center.rewards[0]?.side).toBe('inviter')
  })

  it('接口缺席时按邀请码自己拼链接，不给空串', async () => {
    const { inviteUrl: _omitted, ...withoutUrl } = payload
    mocks.fetch.mockResolvedValue({ ok: true, status: 200, json: () => Promise.resolve(withoutUrl) })
    const state = await getReferralCenter()
    expect(state.kind === 'ready' && state.center.inviteUrl).toBe('https://nextco.work/login?ref=abc123def456')
  })

  it('404 是「平台还没上线这条接口」，不是网络错误', async () => {
    mocks.fetch.mockResolvedValue({ ok: false, status: 404, json: () => Promise.resolve({}) })
    expect(await getReferralCenter()).toEqual({ kind: 'unavailable', reason: 'unsupported' })
  })

  it('401 / 403 归到「要重新登录」', async () => {
    mocks.fetch.mockResolvedValue({ ok: false, status: 401, json: () => Promise.resolve({}) })
    expect(await getReferralCenter()).toEqual({ kind: 'unavailable', reason: 'signed-out' })
  })

  it('网络抛异常和 5xx 都归到可重试的网络失败', async () => {
    mocks.fetch.mockRejectedValue(new Error('offline'))
    expect(await getReferralCenter()).toEqual({ kind: 'unavailable', reason: 'network' })
    mocks.fetch.mockResolvedValue({ ok: false, status: 503, json: () => Promise.resolve({}) })
    expect(await getReferralCenter()).toEqual({ kind: 'unavailable', reason: 'network' })
  })

  it('本地模式压根不发请求', async () => {
    mocks.authMode = 'offline'
    expect(await getReferralCenter()).toEqual({ kind: 'unavailable', reason: 'signed-out' })
    expect(mocks.fetch).not.toHaveBeenCalled()
  })
})
