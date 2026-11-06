/**
 * `providers` 端到端同步快照的安全边界。
 *
 * 用真实 SQLite + 两个独立 nodeHost secret store 模拟两台设备。这里不测 HTTP,
 * 只钉最容易回归的事实:密钥在加密前快照里与 provider 元数据分离、平台登录
 * token 不被复制、远端应用后目标设备拿到完全相同的 API Key/OAuth JSON。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { closeDatabase, openDatabase } from '../db'
import { switchConfigProfile } from '../db/config-profile'
import * as repo from '../db/repo'
import { listProviderAccounts, putProviderAccount } from '../db/provider-accounts'
import { nodeHost } from '../kernel/host'
import type { KernelHost } from '../kernel/host'
import { CLIENT_PROVIDER_ID } from '../../shared/domain/presets'
import {
  applyProviderSyncData,
  captureProviderSyncData,
  mergeProviderSyncData,
  parseProviderSyncData
} from '../config-sync-provider-snapshot'

let dir = ''
let source: KernelHost
let target: KernelHost

function provider(id: string, credentialRef = `provider:${id}`) {
  return {
    id,
    name: id,
    protocol: 'openai-chat' as const,
    baseUrl: `https://${id}.invalid/v1`,
    credentialRef,
    priority: 10,
    enabled: true
  }
}

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-sync-provider-'))
  openDatabase(dir)
  source = nodeHost()
  target = nodeHost()
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

describe('encrypted provider snapshot', () => {
  it('captures API keys without credentialRef and excludes platform login identity', async () => {
    switchConfigProfile('account-a')
    repo.putProvider(provider('acme', 'legacy:custom-ref'))
    repo.putProvider(provider(CLIENT_PROVIDER_ID, 'nextcowork:client-access-token'))
    repo.putAlias({
      alias: 'model', providerId: 'acme', upstreamModel: 'model',
      capabilities: { tools: true, vision: false, thinking: false, caching: false },
      contextWindow: 4096, maxOutputTokens: 1024
    })
    await source.secrets.set('legacy:custom-ref', 'sk-account-a')
    await source.secrets.set('nextcowork:client-access-token', 'platform-login-jwt')

    const snapshot = await captureProviderSyncData(source)
    expect(snapshot.providers).toHaveLength(1)
    expect(snapshot.providers[0]?.id).toBe('acme')
    expect(snapshot.providers[0]).not.toHaveProperty('credentialRef')
    expect(snapshot.credentials).toEqual({ acme: 'sk-account-a' })
    expect(JSON.stringify(snapshot)).not.toContain('platform-login-jwt')
    expect(JSON.stringify(snapshot)).not.toContain('nextcowork:client-access-token')
  })

  it('re-derives local refs and re-encrypts API key/OAuth values into the target host', async () => {
    switchConfigProfile('account-b')
    repo.putProvider(provider('old'))
    await target.secrets.set('provider:old', 'stale-key')

    const oauth = JSON.stringify({
      kind: 'oauth', issuer: 'chatgpt', accessToken: 'access', refreshToken: 'refresh',
      expiresAt: null, accountId: 'account'
    })
    await applyProviderSyncData(target, {
      providers: [
        { id: 'acme', name: 'Acme', protocol: 'openai-chat', baseUrl: 'https://acme.invalid/v1', priority: 1, enabled: true },
        { id: 'oauth', name: 'OAuth', protocol: 'anthropic', baseUrl: 'https://oauth.invalid', priority: 2, enabled: true }
      ],
      aliases: [],
      credentials: { acme: 'sk-remote', oauth }
    })

    expect(repo.listProviders().map((item) => [item.id, item.credentialRef])).toEqual([
      ['acme', 'provider:acme'],
      ['oauth', 'provider:oauth']
    ])
    await expect(target.secrets.get('provider:acme')).resolves.toBe('sk-remote')
    await expect(target.secrets.get('provider:oauth')).resolves.toBe(oauth)
    await expect(target.secrets.get('provider:old')).resolves.toBeNull()
  })

  it('merges first-sync snapshots with local provider and credential values winning same-id conflicts', () => {
    const merged = mergeProviderSyncData(
      {
        providers: [
          { id: 'same', name: 'Remote', protocol: 'openai-chat', baseUrl: 'https://remote.invalid', priority: 1, enabled: true },
          { id: 'remote-only', name: 'Remote only', protocol: 'anthropic', baseUrl: 'https://remote-only.invalid', priority: 2, enabled: true }
        ],
        aliases: [],
        credentials: { same: 'remote-key', 'remote-only': 'remote-only-key' }
      },
      {
        providers: [
          { id: 'same', name: 'Local', protocol: 'openai-chat', baseUrl: 'https://local.invalid', priority: 3, enabled: true }
        ],
        aliases: [],
        credentials: { same: 'local-key' }
      }
    )
    expect(merged.providers.map((item) => [item.id, item.name])).toEqual([
      ['same', 'Local'],
      ['remote-only', 'Remote only']
    ])
    expect(merged.credentials).toEqual({ same: 'local-key', 'remote-only': 'remote-only-key' })
    expect(JSON.stringify(merged.providers)).not.toContain('credentialRef')
  })

  it('rejects credential refs, unknown credential owners and malformed providers before touching storage', async () => {
    expect(() => parseProviderSyncData({
      providers: [{ ...provider('acme'), credentialRef: 'provider:other' }],
      aliases: [], credentials: { other: 'sk-leak' }
    })).toThrow('供应商记录无效')

    await expect(applyProviderSyncData(target, {
      providers: [{ id: 'bad', name: 'Bad', protocol: 'openai-chat', baseUrl: 'file:///etc/passwd', priority: 1, enabled: true }],
      aliases: [], credentials: {}
    })).rejects.toThrow('供应商记录无效')
    expect(repo.listProviders()).toEqual([])
  })
})

/**
 * 多账号那一段(schema 第 24 条)。
 *
 * ★★ 本组最重要的是第一条:**只有一个账号时文档必须和多账号上线之前逐字节相同**。
 * `parseProviderSyncData` 对未知顶层键是拒绝的,而它同时也是**旧版本客户端**
 * 手里的校验器 —— 多一个 `accounts` 键,那台设备会把整份 providers 文档判无效。
 */
describe('provider snapshot · 多账号', () => {
  function oauthAccount(id: string, order: number) {
    return {
      id,
      providerId: 'acme',
      issuer: 'chatgpt' as const,
      order,
      enabled: true,
      current: order === 0,
      needsReauth: false,
      createdAt: 1,
      updatedAt: 1
    }
  }

  it('★★ 只有一个账号时不写 accounts 段 —— 否则旧版本客户端整份文档判无效', async () => {
    switchConfigProfile('account-a')
    repo.putProvider(provider('acme'))
    putProviderAccount(oauthAccount('a1', 0))
    await source.secrets.set('provider:acme', 'sk-a')
    await source.secrets.set('provider:acme#a1', 'oauth-json')

    const snapshot = await captureProviderSyncData(source)
    expect(snapshot).not.toHaveProperty('accounts')
    expect(snapshot).not.toHaveProperty('accountCredentials')
    // 那一个账号的凭证本来就由旧槽镜像带着走
    expect(snapshot.credentials).toEqual({ acme: 'sk-a' })
  })

  it('两个以上账号时整份带上（元数据 + 密文）', async () => {
    switchConfigProfile('account-a')
    repo.putProvider(provider('acme'))
    putProviderAccount(oauthAccount('a1', 0))
    putProviderAccount(oauthAccount('a2', 1))
    await source.secrets.set('provider:acme#a1', 'oauth-1')
    await source.secrets.set('provider:acme#a2', 'oauth-2')

    const snapshot = await captureProviderSyncData(source)
    expect(snapshot.accounts?.map((item) => item.id)).toEqual(['a1', 'a2'])
    expect(snapshot.accountCredentials).toEqual({ a1: 'oauth-1', a2: 'oauth-2' })
    // ★ 限流与额度一个字都不带
    expect(JSON.stringify(snapshot.accounts)).not.toContain('limit')
    expect(JSON.stringify(snapshot.accounts)).not.toContain('quota')
  })

  it('落到另一台设备:账号行重建、密文按本地 ref 重新派生', async () => {
    switchConfigProfile('account-b')
    await applyProviderSyncData(target, {
      providers: [
        { id: 'acme', name: 'Acme', protocol: 'openai-chat', baseUrl: 'https://acme.invalid/v1', priority: 1, enabled: true }
      ],
      aliases: [],
      credentials: {},
      accounts: [
        { id: 'a1', providerId: 'acme', issuer: 'chatgpt', order: 0, enabled: true, current: true },
        { id: 'a2', providerId: 'acme', issuer: 'chatgpt', order: 1, enabled: false, current: false }
      ],
      accountCredentials: { a1: 'oauth-1', a2: 'oauth-2' }
    })

    expect(listProviderAccounts('acme').map((row) => [row.id, row.order, row.enabled])).toEqual([
      ['a1', 0, true],
      ['a2', 1, false]
    ])
    await expect(target.secrets.get('provider:acme#a1')).resolves.toBe('oauth-1')
    await expect(target.secrets.get('provider:acme#a2')).resolves.toBe('oauth-2')
  })

  it('★★ 文档没带 accounts 段时保留本地账号 —— 当成「远端删光了」会让升级早的设备每同步一次丢一个号', async () => {
    switchConfigProfile('account-b')
    repo.putProvider(provider('acme'))
    putProviderAccount(oauthAccount('local-1', 0))
    await target.secrets.set('provider:acme#local-1', 'oauth-local')

    await applyProviderSyncData(target, {
      providers: [
        { id: 'acme', name: 'Acme', protocol: 'openai-chat', baseUrl: 'https://acme.invalid/v1', priority: 1, enabled: true }
      ],
      aliases: [],
      credentials: {}
    })

    expect(listProviderAccounts('acme').map((row) => row.id)).toEqual(['local-1'])
    await expect(target.secrets.get('provider:acme#local-1')).resolves.toBe('oauth-local')
  })

  it('★ provider 消失时它名下的账号与密文一起走（孤儿密文）', async () => {
    switchConfigProfile('account-b')
    repo.putProvider(provider('gone'))
    putProviderAccount({ ...oauthAccount('g1', 0), providerId: 'gone' })
    await target.secrets.set('provider:gone#g1', 'oauth-gone')

    await applyProviderSyncData(target, { providers: [], aliases: [], credentials: {} })

    expect(listProviderAccounts('gone')).toEqual([])
    await expect(target.secrets.get('provider:gone#g1')).resolves.toBeNull()
  })

  it('首次接入云端做并集，本机账号优先', () => {
    const merged = mergeProviderSyncData(
      {
        providers: [
          { id: 'acme', name: 'Remote', protocol: 'openai-chat', baseUrl: 'https://remote.invalid', priority: 1, enabled: true }
        ],
        aliases: [],
        credentials: {},
        accounts: [
          { id: 'same', providerId: 'acme', issuer: 'chatgpt', order: 9, enabled: false, current: false },
          { id: 'remote-only', providerId: 'acme', issuer: 'chatgpt', order: 2, enabled: true, current: false }
        ],
        accountCredentials: { same: 'remote', 'remote-only': 'remote-only' }
      },
      {
        providers: [
          { id: 'acme', name: 'Local', protocol: 'openai-chat', baseUrl: 'https://local.invalid', priority: 1, enabled: true }
        ],
        aliases: [],
        credentials: {},
        accounts: [{ id: 'same', providerId: 'acme', issuer: 'chatgpt', order: 0, enabled: true, current: true }],
        accountCredentials: { same: 'local' }
      }
    )
    expect(merged.accounts?.find((item) => item.id === 'same')?.order).toBe(0)
    expect(merged.accountCredentials).toEqual({ same: 'local', 'remote-only': 'remote-only' })
  })

  it('★ 两边都没有账号段时整个字段缺席 —— 空数组的意思是「远端说一个都没有」', () => {
    const merged = mergeProviderSyncData(
      { providers: [], aliases: [], credentials: {} },
      { providers: [], aliases: [], credentials: {} }
    )
    expect(merged).not.toHaveProperty('accounts')
  })

  it('拒绝归属不明的账号凭证和不认识的 issuer', () => {
    expect(() =>
      parseProviderSyncData({
        providers: [
          { id: 'acme', name: 'Acme', protocol: 'openai-chat', baseUrl: 'https://acme.invalid', priority: 1, enabled: true }
        ],
        aliases: [],
        credentials: {},
        accounts: [{ id: 'a1', providerId: 'acme', issuer: 'chatgpt', order: 0, enabled: true, current: true }],
        accountCredentials: { unknown: 'leak' }
      })
    ).toThrow('账号凭证记录无效')

    expect(() =>
      parseProviderSyncData({
        providers: [
          { id: 'acme', name: 'Acme', protocol: 'openai-chat', baseUrl: 'https://acme.invalid', priority: 1, enabled: true }
        ],
        aliases: [],
        credentials: {},
        accounts: [{ id: 'a1', providerId: 'acme', issuer: 'made-up', order: 0, enabled: true, current: true }]
      })
    ).toThrow('账号记录无效')
  })

  it('★ 账号指向一个快照里不存在的 provider 时整份拒绝（孤儿行进不了库）', () => {
    expect(() =>
      parseProviderSyncData({
        providers: [],
        aliases: [],
        credentials: {},
        accounts: [{ id: 'a1', providerId: 'ghost', issuer: 'chatgpt', order: 0, enabled: true, current: true }]
      })
    ).toThrow('账号记录无效')
  })
})
