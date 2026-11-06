/**
 * 文件模式(`providers.json`)下 repo 的路由,以及库 → 文件的迁移。
 *
 * 用真文件库:迁移要做 `VACUUM INTO`,`:memory:` 上那一步没有意义。
 * 解密函数是注入的,这里用「加前缀」的假加密,不碰 electron。
 */
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { providerCredentialRef, type ModelAlias, type UpstreamProvider } from '../../../shared/domain/provider'
import { closeDatabase, openDatabase } from '../index'
import { PROVIDER_FILE_NAME, ProviderFileStore } from '../provider-file'
import { migrateProvidersToFileWith, PROVIDER_FILE_BACKUP_NAME } from '../provider-file-migration'
import * as repo from '../repo'

const enc = (s: string): Uint8Array => new TextEncoder().encode(`enc:${s}`)
const dec = (b: Uint8Array): string => {
  const t = new TextDecoder().decode(b)
  if (!t.startsWith('enc:')) throw new Error('cannot decrypt')
  return t.slice(4)
}

const provider = (id: string, over: Partial<UpstreamProvider> = {}): UpstreamProvider => ({
  id,
  name: id,
  protocol: 'openai-chat',
  baseUrl: `http://${id}/v1`,
  credentialRef: providerCredentialRef(id),
  priority: 50,
  enabled: true,
  ...over
})

const alias = (providerId: string, name: string): ModelAlias => ({
  alias: name,
  providerId,
  upstreamModel: name,
  capabilities: { tools: true, vision: false, thinking: false, caching: false },
  contextWindow: 100_000,
  maxOutputTokens: 4096
})

let dir = ''
let file: ProviderFileStore

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'provider-file-repo-'))
  openDatabase(join(dir, 'data'))
  file = new ProviderFileStore(join(dir, PROVIDER_FILE_NAME))
})

afterEach(() => {
  repo.useProviderFileStore(null)
  file.stopWatching()
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

describe('repo in file mode', () => {
  it('keeps the legacy DB behaviour until a file store is installed', () => {
    repo.putProvider(provider('a'))
    expect(repo.listProviders().map((p) => p.id)).toEqual(['a'])
    expect(existsSync(join(dir, PROVIDER_FILE_NAME))).toBe(false)
  })

  it('routes user providers and aliases to the file and the managed one to the DB', () => {
    file.load()
    repo.useProviderFileStore(file)
    repo.putProvider(provider('nextcowork', { priority: 0 }))
    repo.putProvider(provider('mine', { priority: 10 }))
    repo.putAlias(alias('mine', 'm1'))
    repo.putAlias(alias('nextcowork', 'n1'))

    expect(repo.listProviders().map((p) => p.id)).toEqual(['nextcowork', 'mine'])
    expect(repo.listAliases().map((a) => `${a.providerId}/${a.alias}`)).toEqual(['mine/m1', 'nextcowork/n1'])
    const doc = JSON.parse(readFileSync(join(dir, PROVIDER_FILE_NAME), 'utf8')) as { providers: Record<string, unknown> }
    expect(Object.keys(doc.providers)).toEqual(['mine'])
    // 库里只有托管那条
    expect(repo.listLegacyDbProviders()).toEqual([])
  })

  it('ignores stale non-managed rows left in the DB', () => {
    repo.putProvider(provider('stale'))
    file.load()
    repo.useProviderFileStore(file)
    expect(repo.listProviders()).toEqual([])
    expect(repo.listAliases()).toEqual([])
  })

  it('removes a provider with its aliases and key from the file', () => {
    file.load()
    repo.useProviderFileStore(file)
    repo.putProvider(provider('mine'))
    repo.putAlias(alias('mine', 'm1'))
    file.setCredential('mine', 'sk-1')
    repo.removeProvider('mine')
    expect(repo.listProviders()).toEqual([])
    expect(repo.listAliases()).toEqual([])
    expect(file.getCredential('mine')).toBeNull()
  })

  it('removeCredential clears the key in the file but leaves the provider', () => {
    file.load()
    repo.useProviderFileStore(file)
    repo.putProvider(provider('mine'))
    file.setCredential('mine', 'sk-1')
    repo.removeCredential(providerCredentialRef('mine'))
    expect(file.getCredential('mine')).toBeNull()
    expect(repo.listProviders().map((p) => p.id)).toEqual(['mine'])
  })

  it('maps only non-managed provider refs to the file', () => {
    expect(repo.fileCredentialProviderId('provider:x')).toBeNull() // 文件模式未启用
    file.load()
    repo.useProviderFileStore(file)
    expect(repo.fileCredentialProviderId('provider:x')).toBe('x')
    expect(repo.fileCredentialProviderId('provider:nextcowork')).toBeNull()
    expect(repo.fileCredentialProviderId('nextcowork:client-access')).toBeNull()
    expect(repo.fileCredentialProviderId('provider-account:x:1')).toBeNull()
    // 多账号密文(`providerAccountCredentialRef`)留在库里,不能落进 providers.json
    expect(repo.fileCredentialProviderId('provider:x#01ACCOUNT')).toBeNull()
  })
})

describe('migrateProvidersToFileWith', () => {
  const seedLegacy = (): void => {
    repo.putProvider(provider('keep', { priority: 20 }))
    repo.putAlias(alias('keep', 'k1'))
    repo.putCredential(providerCredentialRef('keep'), enc('sk-keep'))
    repo.putProvider(provider('nokey'))
    repo.putProvider(provider('routin')) // 内置预设,没配过 key
    repo.putAlias(alias('routin', 'seed-model'))
    repo.putProvider(provider('broken'))
    repo.putCredential(providerCredentialRef('broken'), new TextEncoder().encode('garbage'))
    repo.putProvider(provider('nextcowork'))
  }

  it('moves user providers, drops unconfigured built-ins, keeps undecryptable ciphertext', () => {
    seedLegacy()
    const result = migrateProvidersToFileWith(file, dec)
    expect(result).toEqual({ status: 'migrated', providers: 3, skippedBuiltin: 1, keysFailed: 1 })

    const doc = JSON.parse(readFileSync(join(dir, PROVIDER_FILE_NAME), 'utf8')) as { providers: Record<string, Record<string, unknown>> }
    expect(Object.keys(doc.providers).sort()).toEqual(['broken', 'keep', 'nokey'])
    expect(doc.providers['keep']).toMatchObject({ apiKey: 'sk-keep', priority: 20 })
    expect(Object.keys(doc.providers['keep']!['models'] as object)).toEqual(['k1'])
    expect(doc.providers['broken']!['apiKey']).toBeUndefined()

    // 旧行已清,托管那条还在
    expect(repo.listLegacyDbProviders()).toEqual([])
    expect(repo.listProviders().map((p) => p.id)).toEqual(['nextcowork']) // 文件模式尚未安装,只剩库里的
    // 成功迁走的密文已删,解不开的留着
    expect(repo.getCredential(providerCredentialRef('keep'))).toBeUndefined()
    expect(repo.getCredential(providerCredentialRef('broken'))).toBeDefined()
    // 回滚用的整库快照
    expect(existsSync(join(dir, 'data', PROVIDER_FILE_BACKUP_NAME))).toBe(true)
  })

  it('is idempotent: a second run does nothing, even if the file was deleted', () => {
    seedLegacy()
    migrateProvidersToFileWith(file, dec)
    rmSync(join(dir, PROVIDER_FILE_NAME))
    expect(migrateProvidersToFileWith(file, dec).status).toBe('already-done')
    expect(existsSync(join(dir, PROVIDER_FILE_NAME))).toBe(false)
  })

  it('merges into a hand-written file without overwriting it', () => {
    writeFileSync(join(dir, PROVIDER_FILE_NAME), JSON.stringify({ providers: { keep: { baseUrl: 'http://mine/v1', protocol: 'anthropic', apiKey: 'mine' } } }))
    seedLegacy()
    migrateProvidersToFileWith(file, dec)
    const doc = JSON.parse(readFileSync(join(dir, PROVIDER_FILE_NAME), 'utf8')) as { providers: Record<string, Record<string, unknown>> }
    expect(doc.providers['keep']).toEqual({ baseUrl: 'http://mine/v1', protocol: 'anthropic', apiKey: 'mine' })
    expect(doc.providers['nokey']).toBeDefined()
  })

  it('leaves the DB untouched and throws when the file is corrupt', () => {
    seedLegacy()
    writeFileSync(join(dir, PROVIDER_FILE_NAME), '{ broken')
    expect(() => migrateProvidersToFileWith(file, dec)).toThrow('解析失败')
    expect(repo.listLegacyDbProviders().length).toBe(4)
    expect(repo.getCredential(providerCredentialRef('keep'))).toBeDefined()
  })

  it('marks done without creating a file when there is nothing to migrate', () => {
    repo.putProvider(provider('nextcowork'))
    expect(migrateProvidersToFileWith(file, dec).status).toBe('nothing-to-migrate')
    expect(existsSync(join(dir, PROVIDER_FILE_NAME))).toBe(false)
  })
})
