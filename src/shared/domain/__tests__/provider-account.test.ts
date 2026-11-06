/**
 * `shared/domain/provider-account.ts` 的纯逻辑。
 *
 * 测的是**选择规则**本身:哪个账号会被拿去发请求、什么时候跳过、限流到期之后
 * 会不会自己回来。这段逻辑主进程(`AccountPool`)和渲染层(账号列表徽章)共用,
 * 两边分家的症状是「界面说账号 A 可用、请求却发给了 B」且零报错。
 */
import { describe, expect, it } from 'vitest'
import type { ProviderAccount } from '../provider-account'
import {
  accountDisplay,
  earliestRecoveryAt,
  isAccountLimited,
  isAccountUsable,
  mergeLimit,
  nextUsableAccount,
  parseAccountCredentialRef,
  providerAccountCredentialRef,
  selectAccount,
  sortAccounts
} from '../provider-account'

const NOW = 1_700_000_000_000

function account(overrides: Partial<ProviderAccount> = {}): ProviderAccount {
  return {
    id: 'acc1',
    providerId: 'codex',
    issuer: 'chatgpt',
    order: 0,
    enabled: true,
    current: false,
    needsReauth: false,
    auth: {
      issuer: 'chatgpt',
      accountId: 'upstream-account-000123',
      expiresAt: NOW + 3_600_000,
      expired: false,
      needsReauth: false
    },
    ...overrides
  }
}

describe('凭证 ref 的派生与反解', () => {
  it('形状是 provider:<id>#<accountId> —— 前缀仍是 provider:，云同步与账户隔离才认得', () => {
    expect(providerAccountCredentialRef('codex', 'acc1')).toBe('provider:codex#acc1')
  })

  it('反解拿回两段', () => {
    expect(parseAccountCredentialRef('provider:codex#acc1')).toEqual({
      providerId: 'codex',
      accountId: 'acc1'
    })
  })

  it('★ 旧槽（没有 #）不是账号 ref —— 认错的话刷新广播会指向一个不存在的账号', () => {
    expect(parseAccountCredentialRef('provider:codex')).toBeNull()
    expect(parseAccountCredentialRef('nextcowork:client-access-token')).toBeNull()
    expect(parseAccountCredentialRef('provider:#acc1')).toBeNull()
    expect(parseAccountCredentialRef('provider:codex#')).toBeNull()
  })
})

describe('显示名的回落链', () => {
  it('label 优先', () => {
    expect(accountDisplay(account({ label: ' 工作号 ' }))).toEqual({ kind: 'label', text: '工作号' })
  })

  it('没有 label 用邮箱', () => {
    const withEmail = account()
    withEmail.auth = { ...withEmail.auth!, email: 'a@b.test' }
    expect(accountDisplay(withEmail)).toEqual({ kind: 'email', text: 'a@b.test' })
  })

  it('★ 都没有时只取上游 id 的尾 6 位 —— 整条会把一行挤爆', () => {
    expect(accountDisplay(account())).toEqual({ kind: 'id', text: '000123' })
  })

  it('★ 连上游 id 都没有时给 unknown + 空串，句子留给调用方翻译', () => {
    expect(accountDisplay(account({ auth: undefined }))).toEqual({ kind: 'unknown', text: '' })
  })
})

describe('可用性判定', () => {
  it('限流未到期不可用，到期即自动可用 —— 解除不需要任何人去「解」它', () => {
    const limited = account({
      limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '429' }
    })
    expect(isAccountLimited(limited, NOW)).toBe(true)
    expect(isAccountUsable(limited, NOW)).toBe(false)
    expect(isAccountLimited(limited, NOW + 60_001)).toBe(false)
    expect(isAccountUsable(limited, NOW + 60_001)).toBe(true)
  })

  it('★ needsReauth 永远不可用 —— 它不会自己好，所以也不该落闸等时间', () => {
    const revoked = account({ needsReauth: true })
    expect(isAccountUsable(revoked, NOW)).toBe(false)
    expect(isAccountLimited(revoked, NOW)).toBe(false)
  })

  it('手动停用不可用', () => {
    expect(isAccountUsable(account({ enabled: false }), NOW)).toBe(false)
  })

  it('★★ 判定读账号行上那个反范式的位，不读 auth.needsReauth —— 热路径上拿不到 auth', () => {
    // auth 缺席(池子在热路径上的常态)照样可用,否则池子会认为所有账号都不可用
    expect(isAccountUsable(account({ auth: undefined }), NOW)).toBe(true)
    // auth 里那份是展示用的,和判定无关
    const stale = account({ needsReauth: false })
    stale.auth = { ...stale.auth!, needsReauth: true }
    expect(isAccountUsable(stale, NOW)).toBe(true)
  })
})

describe('选账号', () => {
  const limitedFirst = account({
    id: 'a',
    order: 0,
    limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '额度用尽' }
  })
  const second = account({ id: 'b', order: 1 })

  it('固定顺序取第一个可用的', () => {
    expect(selectAccount([second, limitedFirst], NOW)?.id).toBe('b')
  })

  it('★ 限流恢复后自动切回靠前的那个', () => {
    expect(selectAccount([second, limitedFirst], NOW + 60_001)?.id).toBe('a')
  })

  it('全不可用时返回 null —— 由调用方决定是交回跨供应商 failover 还是报错', () => {
    expect(selectAccount([limitedFirst, account({ id: 'b', order: 1, enabled: false })], NOW)).toBeNull()
  })

  it('★★ 不看 current：轮换到别人头上时，用户显式设的「当前账号」不该被悄悄改掉', () => {
    const current = account({ id: 'a', order: 0, current: true,
      limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' } })
    expect(selectAccount([current, second], NOW)?.id).toBe('b')
  })

  it('★ 关掉轮换时只认 current，哪怕它正被限流 —— 那是用户要的「就用这一个」', () => {
    const current = account({ id: 'a', order: 1, current: true,
      limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' } })
    expect(selectAccount([current, second], NOW, false)?.id).toBe('a')
  })

  it('关掉轮换且没人标 current 时回落到顺序第一个', () => {
    expect(selectAccount([second, account({ id: 'a', order: 0 })], NOW, false)?.id).toBe('a')
  })

  it('空表返回 null —— 调用方据此回落到单槽（零回归路径）', () => {
    expect(selectAccount([], NOW)).toBeNull()
  })
})

describe('下一个可用账号', () => {
  it('★ 从整张表重挑而不是「order 比它大的下一个」：被跳过的那个可能排在后面', () => {
    const a = account({ id: 'a', order: 0 })
    const b = account({ id: 'b', order: 1 })
    const c = account({ id: 'c', order: 2 })
    expect(nextUsableAccount([a, b, c], NOW, 'c')?.id).toBe('a')
  })

  it('没有别的可用账号时返回 null', () => {
    expect(nextUsableAccount([account({ id: 'a' })], NOW, 'a')).toBeNull()
  })
})

describe('排序稳定性', () => {
  it('order 并列时按 id —— 否则拖拽之后顺序会随读取顺序抖', () => {
    const rows = [account({ id: 'b', order: 0 }), account({ id: 'a', order: 0 })]
    expect(sortAccounts(rows).map((r) => r.id)).toEqual(['a', 'b'])
  })
})

describe('最早恢复时刻', () => {
  it('取还在限流的那些里最小的', () => {
    const rows = [
      account({ id: 'a', limit: { until: NOW + 90_000, since: NOW, source: 'http-429', reason: '' } }),
      account({ id: 'b', limit: { until: NOW + 30_000, since: NOW, source: 'http-429', reason: '' } })
    ]
    expect(earliestRecoveryAt(rows, NOW)).toBe(NOW + 30_000)
  })

  it('★ 停用和需重新登录的不算 —— 等下去也不会好，写进「最早恢复」是骗人', () => {
    const disabled = account({ id: 'a', enabled: false,
      limit: { until: NOW + 10_000, since: NOW, source: 'manual', reason: '' } })
    const revoked = account({ id: 'b', needsReauth: true,
      limit: { until: NOW + 20_000, since: NOW, source: 'http-429', reason: '' } })
    expect(earliestRecoveryAt([disabled, revoked], NOW)).toBeNull()
  })

  it('没人被限流时是 null', () => {
    expect(earliestRecoveryAt([account()], NOW)).toBeNull()
  })
})

describe('闸门合并', () => {
  const short = { until: NOW + 10_000, since: NOW, source: 'http-429' as const, reason: '短' }
  const long = { until: NOW + 3_600_000, since: NOW, source: 'http-429' as const, reason: '长' }

  it('★★ 取更晚的那个：后到的短闸门覆盖长闸门，会让全体提前一起再撞一次', () => {
    expect(mergeLimit(long, short)).toBe(long)
    expect(mergeLimit(short, long)).toBe(long)
  })

  it('没有旧闸门时直接用新的', () => {
    expect(mergeLimit(undefined, short)).toBe(short)
  })

  it('★ 用户手动停的那条不被一次 429 续期 —— 它只由用户解除', () => {
    const manual = { until: NOW + 1_000, since: NOW, source: 'manual' as const, reason: '手动' }
    expect(mergeLimit(manual, long)).toBe(manual)
  })
})
