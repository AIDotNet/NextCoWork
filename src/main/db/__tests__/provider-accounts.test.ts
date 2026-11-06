/**
 * `provider_accounts` 表(schema 第 24 条)。
 *
 * 重点测两件**改坏了不会报错**的事:
 * 1. 删账号有没有把密文一起收走(留下的是永远读不到的孤儿 token);
 * 2. 存量迁移有没有保住旧槽 `provider:<id>`(删掉 = 用户回退旧版本后显示未登录)。
 *
 * 和 `roundtrip.test.ts` 一样用真文件库:`:memory:` 上「关了再开」是句空话。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { OAuthCredential } from '../../../shared/domain/credential'
import { serializeCredential } from '../../../shared/domain/credential'
import type { UpstreamProvider } from '../../../shared/domain/provider'
import { providerAccountCredentialRef } from '../../../shared/domain/provider-account'
import { store } from '../../state/store'
import { closeDatabase, openDatabase } from '../index'
import * as repo from '../repo'
import {
  currentProviderAccount,
  ensureProviderAccountsSeeded,
  listProviderAccounts,
  nextProviderAccountOrder,
  putProviderAccount,
  removeProviderAccount,
  setCurrentProviderAccount,
  setProviderAccountEnabled,
  setProviderAccountLabel,
  setProviderAccountLimit,
  setProviderAccountNeedsReauth,
  setProviderAccountOrder,
  setProviderAccountQuota
} from '../provider-accounts'

const NOW = 1_700_000_000_000
let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-accounts-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

/** `codex` 是预设表里的纯 OAuth 供应商(`oauthIssuer: 'chatgpt'`) */
function codexProvider(): UpstreamProvider {
  return {
    id: 'codex',
    name: 'Codex',
    protocol: 'openai-responses',
    baseUrl: 'https://chatgpt.com/backend-api/codex',
    credentialRef: 'provider:codex',
    priority: 1,
    enabled: true
  }
}

function oauthCredential(): OAuthCredential {
  return {
    kind: 'oauth',
    issuer: 'chatgpt',
    accessToken: 'at',
    refreshToken: 'rt',
    expiresAt: NOW + 3_600_000,
    accountId: 'upstream-1'
  }
}

/** 假的 secrets:迁移只需要「读一个串、写一个串」，加解密不属于 db 层 */
function fakeSecrets(initial: Record<string, string> = {}): {
  map: Map<string, string>
  get: (ref: string) => Promise<string | null>
  set: (ref: string, value: string) => Promise<void>
} {
  const map = new Map(Object.entries(initial))
  return {
    map,
    get: (ref) => Promise.resolve(map.get(ref) ?? null),
    set: (ref, value) => {
      map.set(ref, value)
      return Promise.resolve()
    }
  }
}

function account(id: string, order: number): Parameters<typeof putProviderAccount>[0] {
  return {
    id,
    providerId: 'codex',
    issuer: 'chatgpt',
    order,
    enabled: true,
    current: order === 0,
    needsReauth: false,
    createdAt: NOW,
    updatedAt: NOW
  }
}

describe('账号行的增删改查', () => {
  it('按 sort_order 读回，并列时按 id 稳定', () => {
    putProviderAccount(account('b', 0))
    putProviderAccount(account('a', 0))
    putProviderAccount(account('c', 1))
    expect(listProviderAccounts('codex').map((r) => r.id)).toEqual(['a', 'b', 'c'])
  })

  it('★★ 删账号连带删密文 —— 只删行会留下永远读不到的孤儿 token', () => {
    putProviderAccount(account('a', 0))
    const ref = providerAccountCredentialRef('codex', 'a')
    repo.putCredential(ref, new Uint8Array([1, 2, 3]))
    expect(repo.getCredential(ref)).toEqual(new Uint8Array([1, 2, 3]))

    removeProviderAccount('a')
    expect(listProviderAccounts('codex')).toEqual([])
    expect(repo.getCredential(ref)).toBeUndefined()
  })

  it('★ 删供应商时账号与密文一起走 —— provider_accounts 上没有外键管得着这件事', () => {
    store.putProvider(codexProvider())
    putProviderAccount(account('a', 0))
    const ref = providerAccountCredentialRef('codex', 'a')
    repo.putCredential(ref, new Uint8Array([7]))

    store.removeProvider('codex')
    expect(listProviderAccounts('codex')).toEqual([])
    expect(repo.getCredential(ref)).toBeUndefined()
  })

  it('新账号排在最后', () => {
    expect(nextProviderAccountOrder('codex')).toBe(0)
    putProviderAccount(account('a', 0))
    putProviderAccount(account('b', 3))
    expect(nextProviderAccountOrder('codex')).toBe(4)
  })

  it('整批写顺序', () => {
    putProviderAccount(account('a', 0))
    putProviderAccount(account('b', 1))
    setProviderAccountOrder('codex', ['b', 'a'])
    expect(listProviderAccounts('codex').map((r) => r.id)).toEqual(['b', 'a'])
  })

  it('停用只改 enabled，不动限流那四列', () => {
    putProviderAccount(account('a', 0))
    setProviderAccountEnabled('a', false)
    const row = listProviderAccounts('codex')[0]
    expect(row?.enabled).toBe(false)
    expect(row?.limit).toBeUndefined()
  })

  it('★ needsReauth 是反范式列：写进去读得回来，值没变时不重复写', () => {
    putProviderAccount(account('a', 0))
    const before = listProviderAccounts('codex')[0]?.updatedAt
    setProviderAccountNeedsReauth('a', true)
    expect(listProviderAccounts('codex')[0]?.needsReauth).toBe(true)

    // 值相同 = 空操作。每次 token 刷新都会走这条路,重复写会连带一次云同步脏标记
    // 和一次设置页重渲
    setProviderAccountNeedsReauth('a', true)
    setProviderAccountNeedsReauth('b', true) // 不存在的账号:静默忽略,不抛
    expect(listProviderAccounts('codex')[0]?.needsReauth).toBe(true)
    expect(before).toBe(NOW)

    setProviderAccountNeedsReauth('a', false)
    expect(listProviderAccounts('codex')[0]?.needsReauth).toBe(false)
  })

  it('备注名去空白，空串回落成 NULL（显示名于是走邮箱/上游 id）', () => {
    putProviderAccount(account('a', 0))
    setProviderAccountLabel('a', '  工作号  ')
    expect(listProviderAccounts('codex')[0]?.label).toBe('工作号')
    setProviderAccountLabel('a', '   ')
    expect(listProviderAccounts('codex')[0]?.label).toBeUndefined()
  })
})

describe('限流四列同进同出', () => {
  it('写入后读得回四样，解除后整个 limit 消失', () => {
    putProviderAccount(account('a', 0))
    setProviderAccountLimit('a', {
      until: NOW + 60_000,
      since: NOW,
      source: 'quota-exhausted',
      reason: '本轮 5 小时额度用尽'
    })
    expect(listProviderAccounts('codex')[0]?.limit).toEqual({
      until: NOW + 60_000,
      since: NOW,
      source: 'quota-exhausted',
      reason: '本轮 5 小时额度用尽'
    })

    setProviderAccountLimit('a', null)
    expect(listProviderAccounts('codex')[0]?.limit).toBeUndefined()
  })
})

describe('额度快照', () => {
  it('整份 JSON 往返', () => {
    putProviderAccount(account('a', 0))
    const quota = {
      primary: { usedPercent: 62, windowMinutes: 300, resetsAt: NOW + 7_200_000 },
      secondary: { usedPercent: 31, windowMinutes: 10_080, resetsAt: NOW + 400_000_000 },
      capturedAt: NOW
    }
    setProviderAccountQuota('a', quota)
    expect(listProviderAccounts('codex')[0]?.quota).toEqual(quota)
  })
})

describe('当前账号', () => {
  it('置一个当前账号会清掉同一家的其它标记', () => {
    putProviderAccount(account('a', 0))
    putProviderAccount(account('b', 1))
    setCurrentProviderAccount('codex', 'b')
    expect(listProviderAccounts('codex').filter((r) => r.current).map((r) => r.id)).toEqual(['b'])
  })

  it('★ 一个都没标时回落到顺序第一个 —— 否则镜像槽会被清空', () => {
    putProviderAccount({ ...account('a', 0), current: false })
    putProviderAccount({ ...account('b', 1), current: false })
    expect(currentProviderAccount('codex')?.id).toBe('a')
  })
})

describe('存量迁移', () => {
  it('已登录的 OAuth 凭证迁成账号 #1，且★旧槽保留（回退旧版本仍是已登录）', async () => {
    const provider = codexProvider()
    store.putProvider(provider)
    const secrets = fakeSecrets({ 'provider:codex': serializeCredential(oauthCredential()) })

    const created = await ensureProviderAccountsSeeded([provider], secrets, NOW)
    expect(created).toBe(1)

    const rows = listProviderAccounts('codex')
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ providerId: 'codex', issuer: 'chatgpt', order: 0, enabled: true, current: true })
    // 密文搬进账号 ref
    expect(secrets.map.get(providerAccountCredentialRef('codex', rows[0]!.id)))
      .toBe(serializeCredential(oauthCredential()))
    // ★ 旧槽一个字节都没动
    expect(secrets.map.get('provider:codex')).toBe(serializeCredential(oauthCredential()))
  })

  it('幂等：第二次调用什么都不做', async () => {
    const provider = codexProvider()
    const secrets = fakeSecrets({ 'provider:codex': serializeCredential(oauthCredential()) })
    await ensureProviderAccountsSeeded([provider], secrets, NOW)
    expect(await ensureProviderAccountsSeeded([provider], secrets, NOW)).toBe(0)
    expect(listProviderAccounts('codex')).toHaveLength(1)
  })

  it('★ 迁移把凭证里的 needsReauth 一起带过来 —— 否则池子会一直挑中一条已经失效的登录', async () => {
    const provider = codexProvider()
    const secrets = fakeSecrets({
      'provider:codex': serializeCredential({ ...oauthCredential(), needsReauth: true })
    })
    await ensureProviderAccountsSeeded([provider], secrets, NOW)
    expect(listProviderAccounts('codex')[0]?.needsReauth).toBe(true)
  })

  it('★ 槽里是 API Key 的不迁 —— 否则一把 key 会被画成「账号」', async () => {
    const provider = codexProvider()
    const secrets = fakeSecrets({ 'provider:codex': 'sk-plain-key' })
    expect(await ensureProviderAccountsSeeded([provider], secrets, NOW)).toBe(0)
    expect(listProviderAccounts('codex')).toEqual([])
  })

  it('★ 不支持账号登录的供应商原地不动（API Key 供应商零改动）', async () => {
    const deepseek: UpstreamProvider = {
      id: 'deepseek',
      name: 'DeepSeek',
      protocol: 'openai-chat',
      baseUrl: 'https://api.deepseek.com/v1',
      credentialRef: 'provider:deepseek',
      priority: 2,
      enabled: true
    }
    const secrets = fakeSecrets({ 'provider:deepseek': 'sk-x' })
    expect(await ensureProviderAccountsSeeded([deepseek], secrets, NOW)).toBe(0)
    expect(listProviderAccounts('deepseek')).toEqual([])
  })

  it('没登录过的 OAuth 供应商不建空账号', async () => {
    const provider = codexProvider()
    expect(await ensureProviderAccountsSeeded([provider], fakeSecrets(), NOW)).toBe(0)
    expect(listProviderAccounts('codex')).toEqual([])
  })
})
