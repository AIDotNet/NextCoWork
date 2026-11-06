/**
 * router × 多账号 —— **两条底线各有一组断言:**
 *
 * 1. **零回归**:没有账号表时,凭证 ref、请求头、重试次数与多账号上线之前逐字相同;
 * 2. 429 之后**换号重发,且不消耗重试次数** —— 三个账号的用户必须真的能用到第三个。
 *
 * 用真 `UpstreamRouter` + 假 fetch + 假 `AccountPool` Port,因为要测的正是
 * 这两者之间的接线(池子自己的规则在 `account-pool.test.ts` 里)。
 */
import { describe, expect, it, vi } from 'vitest'
import { nodeHost } from '../../host'
import { serializeCredential, type OAuthCredential } from '../../../../shared/domain/credential'
import type { RunRequest } from '../../../../shared/agent/run-request'
import type { ModelAlias, UpstreamProvider } from '../../../../shared/domain/provider'
import type {
  ProviderAccount,
  ProviderAccountLimit,
  ProviderQuotaSnapshot
} from '../../../../shared/domain/provider-account'
import { providerAccountCredentialRef } from '../../../../shared/domain/provider-account'
import { AgentSession } from '../../agent-session'
import { RunHandle } from '../../run-registry'
import { ToolRegistry } from '../../tool/registry'
import { AccountPool } from '../account-pool'
import { UpstreamRouter } from '../router'
import { messageItem, responseDone, sse } from './openai-fixtures'

const NOW = 1_700_000_000_000
const LEGACY_REF = 'provider:codex'

function credential(token: string): OAuthCredential {
  return {
    kind: 'oauth',
    issuer: 'chatgpt',
    accessToken: token,
    refreshToken: `rt-${token}`,
    expiresAt: NOW + 3_600_000,
    accountId: 'acct-42'
  }
}

const provider: UpstreamProvider = {
  id: 'codex',
  name: 'Codex',
  protocol: 'openai-responses',
  baseUrl: 'https://chatgpt.com/backend-api/codex',
  credentialRef: LEGACY_REF,
  priority: 60,
  enabled: true
}

const alias: ModelAlias = {
  alias: 'test',
  providerId: 'codex',
  upstreamModel: 'gpt-test',
  contextWindow: 200_000,
  maxOutputTokens: 8192,
  capabilities: { tools: false, thinking: false, caching: false, vision: false }
}

const request: RunRequest = {
  runId: 'accounts-run',
  sessionId: 'accounts-session',
  workspaceId: 'w1',
  depth: 0,
  model: 'test',
  input: [{ type: 'text', text: 'hi' }],
  thinking: 'off',
  mode: 'normal',
  permissionMode: 'ask',
  webSearch: false,
  skillIds: []
}

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

function rig(options: {
  accounts?: ProviderAccount[]
  responses: (n: number) => Response
  rotation?: boolean
}) {
  const calls: { headers: Record<string, string> }[] = []
  const rows = options.accounts ?? []
  const secrets = new Map<string, string>([[LEGACY_REF, serializeCredential(credential('at-legacy'))]])
  for (const row of rows) {
    secrets.set(
      providerAccountCredentialRef(row.providerId, row.id),
      serializeCredential(credential(`at-${row.id}`))
    )
  }

  const host = nodeHost({
    clock: { now: () => NOW },
    secrets: {
      get: async (ref) => secrets.get(ref) ?? null,
      set: async (ref, v) => {
        secrets.set(ref, v)
      },
      available: () => true
    },
    logger: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
    fetch: (async (_url: unknown, init?: RequestInit) => {
      const headers: Record<string, string> = {}
      for (const [k, v] of Object.entries((init?.headers ?? {}) as Record<string, string>)) {
        headers[k.toLowerCase()] = v
      }
      calls.push({ headers })
      return options.responses(calls.length)
    }) as typeof globalThis.fetch
  })

  const pool =
    options.accounts === undefined
      ? undefined
      : new AccountPool({
          list: (providerId) => rows.filter((a) => a.providerId === providerId),
          setLimit: (accountId, limit: ProviderAccountLimit | null) => {
            const index = rows.findIndex((a) => a.id === accountId)
            if (index < 0) return
            const next = { ...rows[index]! }
            if (limit === null) delete next.limit
            else next.limit = limit
            rows[index] = next
          },
          setQuota: (accountId, quota: ProviderQuotaSnapshot) => {
            const index = rows.findIndex((a) => a.id === accountId)
            if (index >= 0) rows[index] = { ...rows[index]!, quota }
          },
          now: () => NOW,
          rotationEnabled: () => options.rotation ?? true
        })

  const router = new UpstreamRouter(
    host,
    { providers: () => [provider], aliases: () => [alias], failoverEnabled: () => false },
    {
      baseDelayMs: 0,
      rateLimitFloorMs: 0,
      providerErrorFloorMs: 0,
      ...(pool === undefined ? {} : { accounts: pool })
    }
  )
  return { router, host, calls, rows, secrets }
}

async function drain(router: UpstreamRouter, host: ReturnType<typeof nodeHost>): Promise<void> {
  const handle = new RunHandle(request)
  await new AgentSession(
    { host, upstream: router, tools: new ToolRegistry(), workspaceRoot: '/workspace' },
    handle,
    request
  ).run()
}

const ok = (): Response => sse(responseDone([messageItem]))
/**
 * 带额外响应头的成功流。
 *
 * ★ 不去改公共的 `sse()` fixture:那个被十几个用例用着,给它加一个可选参数
 * 等于让每一处都要重新想一遍「我这条要不要传头」。
 */
const okWith = (headers: Record<string, string>): Response => {
  const base = sse(responseDone([messageItem]))
  const merged = new Headers(base.headers)
  for (const [k, v] of Object.entries(headers)) merged.set(k, v)
  return new Response(base.body, { headers: merged })
}
const rateLimited = (): Response =>
  new Response(JSON.stringify({ error: { message: '额度用尽', type: 'rate_limit_error' } }), {
    status: 429,
    headers: { 'content-type': 'application/json' }
  })

describe('★★ 零回归:没有账号表时逐字节等于多账号上线之前', () => {
  it('凭证走旧槽 provider:<id>', async () => {
    const r = rig({ responses: ok })
    await drain(r.router, r.host)
    expect(r.calls).toHaveLength(1)
    expect(r.calls[0]?.headers['authorization']).toBe('Bearer at-legacy')
  })

  it('429 仍然按原来的重试次数退避，不会凭空多打几次', async () => {
    const r = rig({ responses: rateLimited })
    await drain(r.router, r.host)
    // MAX_ATTEMPTS = 3(限流不借用 network 那条更宽的上限)
    expect(r.calls).toHaveLength(3)
  })
})

describe('账号选择', () => {
  it('用顺序最靠前的可用账号，而不是旧槽', async () => {
    const r = rig({ accounts: [account('a', 0), account('b', 1)], responses: ok })
    await drain(r.router, r.host)
    expect(r.calls[0]?.headers['authorization']).toBe('Bearer at-a')
  })

  it('★ 第一个被限流时直接从第二个开始 —— 闸门是落库的，重启也还在', async () => {
    const r = rig({
      accounts: [
        account('a', 0, { limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' } }),
        account('b', 1)
      ],
      responses: ok
    })
    await drain(r.router, r.host)
    expect(r.calls).toHaveLength(1)
    expect(r.calls[0]?.headers['authorization']).toBe('Bearer at-b')
  })

  it('停用和待重新登录的账号被跳过', async () => {
    const r = rig({
      accounts: [account('a', 0, { enabled: false }), account('b', 1, { needsReauth: true }), account('c', 2)],
      responses: ok
    })
    await drain(r.router, r.host)
    expect(r.calls[0]?.headers['authorization']).toBe('Bearer at-c')
  })
})

describe('★★ 429 之后换号', () => {
  it('第一个账号 429 → 第二个账号重发并成功', async () => {
    const r = rig({
      accounts: [account('a', 0), account('b', 1)],
      responses: (n) => (n === 1 ? rateLimited() : ok())
    })
    await drain(r.router, r.host)
    expect(r.calls.map((c) => c.headers['authorization'])).toEqual([
      'Bearer at-a',
      'Bearer at-b'
    ])
    // 失败的那个被落闸(其它并发流据此在发请求之前就绕开它)
    expect(r.rows[0]?.limit).toMatchObject({ source: 'http-429' })
  })

  it('★★ 换号不消耗重试次数：三个账号的用户必须真的能用到第三个', async () => {
    const r = rig({
      accounts: [account('a', 0), account('b', 1), account('c', 2)],
      responses: (n) => (n <= 2 ? rateLimited() : ok())
    })
    await drain(r.router, r.host)
    expect(r.calls.map((c) => c.headers['authorization'])).toEqual([
      'Bearer at-a',
      'Bearer at-b',
      'Bearer at-c'
    ])
  })

  it('★ 全部账号都被限流之后不回落到旧槽 —— 那个镜像绕过了刚立起来的闸门', async () => {
    const r = rig({
      accounts: [account('a', 0), account('b', 1)],
      responses: rateLimited
    })
    await drain(r.router, r.host)
    // 两个账号各打一次,然后停;绝不能出现第三次带着 at-legacy 的请求
    expect(r.calls.map((c) => c.headers['authorization'])).toEqual([
      'Bearer at-a',
      'Bearer at-b'
    ])
    expect(r.calls.some((c) => c.headers['authorization'] === 'Bearer at-legacy')).toBe(false)
  })

  it('★ 关掉轮换后不换号，只在当前账号上按原来的次数重试', async () => {
    const r = rig({
      accounts: [account('a', 0, { current: true }), account('b', 1)],
      responses: rateLimited,
      rotation: false
    })
    await drain(r.router, r.host)
    expect(new Set(r.calls.map((c) => c.headers['authorization']))).toEqual(
      new Set(['Bearer at-a'])
    )
    expect(r.calls).toHaveLength(3)
  })
})

describe('成功之后', () => {
  it('★ 清掉这个账号落库的限流 —— 一次成功 = 配额确实回来了', async () => {
    const r = rig({
      accounts: [
        account('a', 0),
        account('b', 1, { limit: { until: NOW + 60_000, since: NOW, source: 'http-429', reason: '' } })
      ],
      responses: ok
    })
    await drain(r.router, r.host)
    expect(r.rows[0]?.limit).toBeUndefined()
    // 没参与这次请求的那个照旧
    expect(r.rows[1]?.limit).toBeDefined()
  })
})

describe('Codex 额度搭便车', () => {
  const quotaHeaders = (usedPercent: string): Record<string, string> => ({
    'x-codex-primary-used-percent': usedPercent,
    'x-codex-primary-window-minutes': '300',
    'x-codex-primary-resets-in-seconds': '7200'
  })

  it('成功响应里的额度头落到账号上', async () => {
    const r = rig({
      accounts: [account('a', 0)],
      responses: () => okWith(quotaHeaders('62'))
    })
    await drain(r.router, r.host)
    expect(r.rows[0]?.quota?.primary).toEqual({
      usedPercent: 62,
      windowMinutes: 300,
      resetsAt: NOW + 7_200_000
    })
  })

  it('★★ 失败响应也读 —— 跑满之后那一次必然失败，而它带的正是「已用 100%」那组数', async () => {
    const r = rig({
      accounts: [account('a', 0), account('b', 1)],
      responses: (n) =>
        n === 1
          ? new Response(JSON.stringify({ error: { message: '额度用尽', type: 'rate_limit_error' } }), {
              status: 429,
              headers: { 'content-type': 'application/json', ...quotaHeaders('100') }
            })
          : ok()
    })
    await drain(r.router, r.host)
    expect(r.rows[0]?.quota?.primary?.usedPercent).toBe(100)
  })

  it('★★ 额度跑满 → 落闸到重置时刻（提前换号，不等下一次 429）', async () => {
    const r = rig({
      accounts: [account('a', 0)],
      responses: () => okWith(quotaHeaders('100'))
    })
    await drain(r.router, r.host)
    expect(r.rows[0]?.limit).toMatchObject({
      until: NOW + 7_200_000,
      source: 'quota-exhausted'
    })
  })

  it('★ 没有额度头时不写空快照 —— 界面要显示「尚未获取」而不是 0%', async () => {
    const r = rig({ accounts: [account('a', 0)], responses: ok })
    await drain(r.router, r.host)
    expect(r.rows[0]?.quota).toBeUndefined()
  })
})
