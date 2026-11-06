/**
 * `AccountPool` —— 「这一刻用哪个账号、这次失败要不要换号」。
 *
 * 用假 Port 直测:池子自己不发请求、不读密文、不碰 Electron,那正是把它从
 * `router.ts` 里拆出来的理由。
 */
import { describe, expect, it } from 'vitest'
import { agentError } from '../../../../shared/agent/error'
import type {
  ProviderAccount,
  ProviderAccountLimit,
  ProviderQuotaSnapshot
} from '../../../../shared/domain/provider-account'
import { AccountPool, DEFAULT_ACCOUNT_LIMIT_MS, isAccountQuotaFailure } from '../account-pool'

const NOW = 1_700_000_000_000

function account(id: string, order: number, extra: Partial<ProviderAccount> = {}): ProviderAccount {
  return {
    id,
    providerId: 'codex',
    issuer: 'chatgpt',
    order,
    enabled: true,
    current: order === 0,
    needsReauth: false,
    ...extra
  }
}

/** 假 Port:把写入记在内存里,断言时直接读 */
function harness(accounts: ProviderAccount[], options: { rotation?: boolean; now?: number } = {}) {
  const changed: string[] = []
  const rows = [...accounts]
  const port = {
    list: (providerId: string) => rows.filter((a) => a.providerId === providerId),
    setLimit: (accountId: string, limit: ProviderAccountLimit | null) => {
      const index = rows.findIndex((a) => a.id === accountId)
      if (index < 0) return
      const next = { ...rows[index]! }
      if (limit === null) delete next.limit
      else next.limit = limit
      rows[index] = next
    },
    setQuota: (accountId: string, quota: ProviderQuotaSnapshot) => {
      const index = rows.findIndex((a) => a.id === accountId)
      if (index >= 0) rows[index] = { ...rows[index]!, quota }
    },
    now: () => options.now ?? NOW,
    rotationEnabled: () => options.rotation ?? true,
    onChanged: (providerId: string) => changed.push(providerId)
  }
  return { pool: new AccountPool(port), rows, changed }
}

const RATE_LIMIT = agentError('rate_limit', '超出速率限制', { status: 429 })

describe('什么算「这个账号的额度没了」', () => {
  it('429 一律算', () => {
    expect(isAccountQuotaFailure(RATE_LIMIT)).toBe(true)
  })

  it('★ 403/402 带配额词才算 —— 否则一次普通的权限错误会被说成「1 小时后恢复」', () => {
    expect(isAccountQuotaFailure(agentError('provider', 'quota exceeded', { status: 403 }))).toBe(true)
    expect(isAccountQuotaFailure(agentError('provider', 'Insufficient_quota', { status: 402 }))).toBe(true)
    expect(isAccountQuotaFailure(agentError('provider', 'model not allowed', { status: 403 }))).toBe(false)
  })

  it('★★ 401 永远不算 —— 一把真失效的 token 被当成限流，界面会说「1 小时后恢复」而它永远不会恢复', () => {
    expect(isAccountQuotaFailure(agentError('auth', 'quota', { status: 401 }))).toBe(false)
  })

  it('网络错误、5xx 不算', () => {
    expect(isAccountQuotaFailure(agentError('network', 'ECONNRESET'))).toBe(false)
    expect(isAccountQuotaFailure(agentError('provider', 'overloaded', { status: 503 }))).toBe(false)
  })
})

describe('选账号', () => {
  it('没有账号表时返回 null —— 调用方回落到单槽（零回归路径）', () => {
    const { pool } = harness([])
    expect(pool.select('codex')).toBeNull()
    expect(pool.hasAccounts('codex')).toBe(false)
  })

  it('★ 全被限流时 select 是 null 但 hasAccounts 是 true —— 两句话要说的完全不同', () => {
    const { pool } = harness([
      account('a', 0, { limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' } })
    ])
    expect(pool.select('codex')).toBeNull()
    expect(pool.hasAccounts('codex')).toBe(true)
  })
})

describe('失败落闸与换号', () => {
  it('429 落闸并返回下一个账号', () => {
    const { pool, rows, changed } = harness([account('a', 0), account('b', 1)])
    const next = pool.reportFailure(rows[0]!, RATE_LIMIT)
    expect(next?.id).toBe('b')
    expect(rows[0]?.limit).toEqual({
      until: NOW + DEFAULT_ACCOUNT_LIMIT_MS,
      since: NOW,
      source: 'http-429',
      reason: '超出速率限制'
    })
    expect(changed).toEqual(['codex'])
  })

  it('★ 上游给了 Retry-After 就听它的 —— 它知道配额窗口什么时候重置，我们不知道', () => {
    const { pool, rows } = harness([account('a', 0)])
    pool.reportFailure(rows[0]!, { ...RATE_LIMIT, retryAfterMs: 3_600_000 })
    expect(rows[0]?.limit?.until).toBe(NOW + 3_600_000)
  })

  it('★★ 兜底是分钟级，不是 router 那个秒级的退避基数', () => {
    expect(DEFAULT_ACCOUNT_LIMIT_MS).toBeGreaterThanOrEqual(60_000)
  })

  it('与额度无关的失败不落闸、不换号 —— 拿网络抖动去禁用账号会放大成全局停顿', () => {
    const { pool, rows, changed } = harness([account('a', 0), account('b', 1)])
    expect(pool.reportFailure(rows[0]!, agentError('network', '连接失败'))).toBeNull()
    expect(rows[0]?.limit).toBeUndefined()
    expect(changed).toEqual([])
  })

  it('没有别的账号可换时返回 null（但闸门照落 —— 其它并发流照样该被挡住）', () => {
    const { pool, rows } = harness([account('a', 0)])
    expect(pool.reportFailure(rows[0]!, RATE_LIMIT)).toBeNull()
    expect(rows[0]?.limit).toBeDefined()
  })

  it('★★ 已有的更晚闸门不被这次更短的覆盖 —— 否则全体提前一起再撞一次', () => {
    const long: ProviderAccountLimit = {
      until: NOW + 3_600_000, since: NOW, source: 'http-429', reason: '长'
    }
    const { pool, rows, changed } = harness([account('a', 0, { limit: long })])
    pool.reportFailure(rows[0]!, RATE_LIMIT)
    expect(rows[0]?.limit).toBe(long)
    // 没有实际变化就不广播:一次无谓的广播 = 设置页整列表重渲
    expect(changed).toEqual([])
  })
})

describe('成功清闸', () => {
  it('一次成功 = 配额确实回来了', () => {
    const { pool, rows } = harness([
      account('a', 0, { limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' } })
    ])
    pool.reportSuccess(rows[0]!)
    expect(rows[0]?.limit).toBeUndefined()
  })

  it('★ 用户手动停的那条不被一次成功解除 —— 它只由用户解除', () => {
    const manual: ProviderAccountLimit = { until: NOW + 60_000, since: NOW, source: 'manual', reason: '' }
    const { pool, rows } = harness([account('a', 0, { limit: manual })])
    pool.reportSuccess(rows[0]!)
    expect(rows[0]?.limit).toBe(manual)
  })

  it('本来就没闸门时什么都不做（不写库、不广播）', () => {
    const { pool, rows, changed } = harness([account('a', 0)])
    pool.reportSuccess(rows[0]!)
    expect(changed).toEqual([])
  })
})

describe('额度驱动的提前落闸', () => {
  const window = (usedPercent: number, minutes: number, resetsIn: number) => ({
    usedPercent, windowMinutes: minutes, resetsAt: NOW + resetsIn
  })

  it('快照照存，没跑满就不落闸', () => {
    const { pool, rows } = harness([account('a', 0)])
    pool.reportQuota(rows[0]!, { primary: window(62, 300, 7_200_000), capturedAt: NOW })
    expect(rows[0]?.quota?.primary?.usedPercent).toBe(62)
    expect(rows[0]?.limit).toBeUndefined()
  })

  it('★★ 跑满就提前落闸到重置时刻 —— 不等那次注定失败的 429', () => {
    const { pool, rows } = harness([account('a', 0)])
    pool.reportQuota(rows[0]!, { primary: window(100, 300, 7_200_000), capturedAt: NOW })
    expect(rows[0]?.limit).toMatchObject({
      until: NOW + 7_200_000,
      source: 'quota-exhausted'
    })
  })

  it('★ 两个窗口都跑满时取更晚的那个 —— 早的那个到了也还是发不出去', () => {
    const { pool, rows } = harness([account('a', 0)])
    pool.reportQuota(rows[0]!, {
      primary: window(100, 300, 7_200_000),
      secondary: window(100, 10_080, 400_000_000),
      capturedAt: NOW
    })
    expect(rows[0]?.limit?.until).toBe(NOW + 400_000_000)
  })

  it('重置时刻已经过去的快照不落闸（那是一份过期数据）', () => {
    const { pool, rows } = harness([account('a', 0)])
    pool.reportQuota(rows[0]!, { primary: window(100, 300, -1_000), capturedAt: NOW - 10_000 })
    expect(rows[0]?.limit).toBeUndefined()
  })
})

describe('最早恢复时刻', () => {
  it('给 router 拼那句「最早 HH:MM 恢复」用', () => {
    const { pool } = harness([
      account('a', 0, { limit: { until: NOW + 90_000, since: NOW, source: 'http-429', reason: '' } }),
      account('b', 1, { limit: { until: NOW + 30_000, since: NOW, source: 'http-429', reason: '' } })
    ])
    expect(pool.earliestRecoveryAt('codex')).toBe(NOW + 30_000)
  })
})

describe('轮换开关关掉之后', () => {
  it('★ 只认当前账号，哪怕它正被限流 —— 那是用户要的「就用这一个」', () => {
    const { pool } = harness(
      [
        account('a', 0, {
          current: true,
          limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' }
        }),
        account('b', 1)
      ],
      { rotation: false }
    )
    expect(pool.select('codex')?.id).toBe('a')
  })

  it('★ 失败仍然落闸(界面要看得见为什么失败)，但 select 不会换人', () => {
    const { pool, rows } = harness([account('a', 0, { current: true }), account('b', 1)], {
      rotation: false
    })
    pool.reportFailure(rows[0]!, RATE_LIMIT)
    expect(rows[0]?.limit).toBeDefined()
    expect(pool.select('codex')?.id).toBe('a')
  })

  it('★★ reportFailure 不返回下一个账号 —— 返回了就是一个死循环（router 把换号当作「这次不算重试」）', () => {
    const { pool, rows } = harness([account('a', 0, { current: true }), account('b', 1)], {
      rotation: false
    })
    expect(pool.reportFailure(rows[0]!, RATE_LIMIT)).toBeNull()
  })
})
