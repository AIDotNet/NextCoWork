/**
 * 账号在**导出 / 导入 / 云同步 / 账户隔离**四条搬运路径上的行为。
 *
 * 这四条各自的坏法都很安静:
 * - 导出漏了账号 → 换台机器只剩一个号,而界面不报任何错;
 * - 导入把限流带过来 → 新机器上一个好账号显示「限流中」;
 * - 云同步文档多一个键 → **旧版本客户端整份 providers 文档判无效**;
 * - 账号表不随账户走 → A 账户的号出现在 B 账户里,一发消息报「没有配置密钥」。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { serializeCredential, type OAuthCredential } from '../../../shared/domain/credential'
import type { UpstreamProvider } from '../../../shared/domain/provider'
import { providerAccountCredentialRef } from '../../../shared/domain/provider-account'
import { isDataExport, type DataExport } from '../../../shared/domain/data'
import {
  listProviderAccounts,
  putProviderAccount,
  type ProviderAccountRow
} from '../../db/provider-accounts'
import { closeDatabase, openDatabase } from '../../db'
import * as repo from '../../db/repo'
import { switchConfigProfile } from '../../db/config-profile'
import { store } from '../../state/store'
import { exportProviderAccounts, mergeProviderAccounts } from '../provider-accounts-transfer'

const NOW = 1_700_000_000_000
let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-transfer-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

function codex(): UpstreamProvider {
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

function account(id: string, order: number, extra: Partial<ProviderAccountRow> = {}): ProviderAccountRow {
  return {
    id,
    providerId: 'codex',
    issuer: 'chatgpt',
    order,
    enabled: true,
    current: order === 0,
    needsReauth: false,
    createdAt: NOW,
    updatedAt: NOW,
    ...extra
  }
}

function oauth(token: string): OAuthCredential {
  return {
    kind: 'oauth',
    issuer: 'chatgpt',
    accessToken: token,
    refreshToken: `rt-${token}`,
    expiresAt: NOW + 3_600_000,
    accountId: `upstream-${token}`
  }
}

/** 最小可用的导出文件骨架 */
function exportFile(overrides: Partial<DataExport> = {}): DataExport {
  return {
    type: 'nextcowork-data-export',
    version: 1,
    exportedAt: new Date(NOW).toISOString(),
    settings: store.getSettings(),
    workspaces: [],
    sessions: [],
    providers: [codex()],
    aliases: [],
    mcpServers: [],
    searchProviders: [],
    disabledSkillIds: [],
    ...overrides
  }
}

describe('导出', () => {
  it('带上全部账号的元数据', () => {
    putProviderAccount(account('a', 0, { label: '工作号' }))
    putProviderAccount(account('b', 1))
    expect(exportProviderAccounts()).toEqual([
      { id: 'a', providerId: 'codex', issuer: 'chatgpt', label: '工作号', order: 0, enabled: true, current: true, updatedAt: NOW },
      { id: 'b', providerId: 'codex', issuer: 'chatgpt', order: 1, enabled: true, current: false, updatedAt: NOW }
    ])
  })

  it('★★ 限流与额度快照不出门 —— 它们是「那台机器那一刻」的观察，换台机器就不成立了', () => {
    putProviderAccount(
      account('a', 0, {
        limit: { until: NOW + 3_600_000, since: NOW, source: 'http-429', reason: '额度用尽' },
        quota: { primary: { usedPercent: 100, windowMinutes: 300, resetsAt: NOW }, capturedAt: NOW }
      })
    )
    const [exported] = exportProviderAccounts()
    expect(exported).not.toHaveProperty('limit')
    expect(exported).not.toHaveProperty('quota')
    expect(JSON.stringify(exported)).not.toContain('额度用尽')
  })

  it('★ 元数据里不含任何密文', () => {
    putProviderAccount(account('a', 0))
    expect(JSON.stringify(exportProviderAccounts())).not.toContain('accessToken')
  })
})

describe('导出文件的校验', () => {
  it('带账号段的新文件合法', () => {
    expect(isDataExport(exportFile({ providerAccounts: exportProviderAccounts() }))).toBe(true)
  })

  it('★★ 缺账号段的旧备份照样合法 —— 判成非法的表现是整份旧备份导不进来', () => {
    expect(isDataExport(exportFile())).toBe(true)
  })

  it('账号 id 重复时整份拒绝（重复主键会让合并变成顺序相关的）', () => {
    const one = { id: 'a', providerId: 'codex', issuer: 'chatgpt', order: 0, enabled: true, current: true, updatedAt: NOW }
    expect(isDataExport(exportFile({ providerAccounts: [one, { ...one, order: 1 }] }))).toBe(false)
  })

  it('字段类型不对时拒绝', () => {
    expect(
      isDataExport(
        exportFile({
          providerAccounts: [
            { id: 'a', providerId: 'codex', issuer: 'chatgpt', order: 0, enabled: 'yes', current: true, updatedAt: NOW }
          ] as never
        })
      )
    ).toBe(false)
  })
})

describe('导入', () => {
  it('把账号行写进来', () => {
    store.putProvider(codex())
    const written = mergeProviderAccounts(
      exportFile({
        providerAccounts: [
          { id: 'a', providerId: 'codex', issuer: 'chatgpt', order: 0, enabled: true, current: true, updatedAt: NOW }
        ]
      })
    )
    expect(written).toBe(1)
    expect(listProviderAccounts('codex').map((row) => row.id)).toEqual(['a'])
  })

  it('★ provider 没导进来的账号是孤儿，直接丢掉', () => {
    const written = mergeProviderAccounts(
      exportFile({
        providers: [],
        providerAccounts: [
          { id: 'a', providerId: 'codex', issuer: 'chatgpt', order: 0, enabled: true, current: true, updatedAt: NOW }
        ]
      })
    )
    expect(written).toBe(0)
    expect(listProviderAccounts('codex')).toEqual([])
  })

  it('★ 认不出 issuer 的行丢掉 —— 界面上的登录按钮名字查的是一张穷尽表', () => {
    store.putProvider(codex())
    expect(
      mergeProviderAccounts(
        exportFile({
          providerAccounts: [
            { id: 'a', providerId: 'codex', issuer: 'not-an-issuer', order: 0, enabled: true, current: true, updatedAt: NOW }
          ]
        })
      )
    ).toBe(0)
  })

  it('★★ 本地已有的限流不被一次导入抹掉 —— 那是这台机器刚撞出来的事实', () => {
    store.putProvider(codex())
    const limit = { until: NOW + 3_600_000, since: NOW, source: 'http-429' as const, reason: '本机撞的' }
    putProviderAccount(account('a', 0, { limit, updatedAt: NOW - 1000 }))
    mergeProviderAccounts(
      exportFile({
        providerAccounts: [
          { id: 'a', providerId: 'codex', issuer: 'chatgpt', order: 3, enabled: false, current: false, updatedAt: NOW }
        ]
      })
    )
    const row = listProviderAccounts('codex')[0]
    expect(row?.order).toBe(3)
    expect(row?.enabled).toBe(false)
    expect(row?.limit).toEqual(limit)
  })

  it('本地那份更新时跳过（dataMergeDecision 的既有规矩）', () => {
    store.putProvider(codex())
    putProviderAccount(account('a', 0, { label: '本地的', updatedAt: NOW + 10_000 }))
    mergeProviderAccounts(
      exportFile({
        providerAccounts: [
          { id: 'a', providerId: 'codex', issuer: 'chatgpt', label: '档案里的', order: 0, enabled: true, current: true, updatedAt: NOW }
        ]
      })
    )
    expect(listProviderAccounts('codex')[0]?.label).toBe('本地的')
  })
})

describe('账户作用域隔离', () => {
  it('★★ 账号随作用域走：A 账户的号不出现在 B 账户里', () => {
    store.putProvider(codex())
    putProviderAccount(account('a', 0))
    repo.putCredential(providerAccountCredentialRef('codex', 'a'), new Uint8Array([1, 2, 3]))

    switchConfigProfile('account-b')
    // B 账户看不到 A 的账号行
    expect(listProviderAccounts('codex')).toEqual([])

    switchConfigProfile(null)
    // 切回来原样在
    expect(listProviderAccounts('codex').map((row) => row.id)).toEqual(['a'])
    expect(repo.getCredential(providerAccountCredentialRef('codex', 'a')))
      .toEqual(new Uint8Array([1, 2, 3]))
  })

  it('★ 密文本来就按作用域隔离：同一条 ref 在两个账户里是两行', () => {
    store.putProvider(codex())
    const ref = providerAccountCredentialRef('codex', 'a')
    repo.putCredential(ref, new Uint8Array([1]))

    switchConfigProfile('account-b')
    expect(repo.getCredential(ref)).toBeUndefined()
    repo.putCredential(ref, new Uint8Array([2]))
    expect(repo.getCredential(ref)).toEqual(new Uint8Array([2]))

    switchConfigProfile(null)
    expect(repo.getCredential(ref)).toEqual(new Uint8Array([1]))
  })
})

describe('凭证与账号行的配套', () => {
  it('账号 ref 拼得出、读得回(端到端的一次往返)', () => {
    store.putProvider(codex())
    putProviderAccount(account('a', 0))
    const ref = providerAccountCredentialRef('codex', 'a')
    repo.putCredential(ref, new TextEncoder().encode(serializeCredential(oauth('at-1'))))
    const raw = repo.getCredential(ref)
    expect(raw === undefined ? '' : new TextDecoder().decode(raw)).toContain('at-1')
  })
})
