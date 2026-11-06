/**
 * 库 → `providers.json` 的一次性迁移。**不依赖 electron**(解密函数由调用方注入),
 * 所以无头测试能直接跑;`host/index.ts` 里的 `migrateProvidersToFile` 是它的薄包装。
 */
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { BUILTIN_PROVIDER_IDS } from '../../shared/domain/presets'
import { providerCredentialRef, type UpstreamProvider } from '../../shared/domain/provider'
import { databaseDirectory } from './index'
import type { ProviderFileStore } from './provider-file'
import {
  backupDatabaseTo,
  getCredential,
  getKv,
  listLegacyDbAliases,
  listLegacyDbProviders,
  purgeLegacyDbProviders,
  setKv
} from './repo'

const PROVIDER_FILE_MIGRATED_KEY = 'providers.file.migrated'
/** 迁移前的整库快照(含密文),要回退到旧版本时拿它。 */
export const PROVIDER_FILE_BACKUP_NAME = 'nextcowork.pre-providers-file.db'

export interface ProviderFileMigrationResult {
  status: 'migrated' | 'already-done' | 'nothing-to-migrate'
  /** 写进(或并入)文件的供应商数 */
  providers: number
  /** 没配过密钥的内置预设,不是用户添加的,直接丢弃 */
  skippedBuiltin: number
  /** 密文解不开、供应商进了文件但没带 key 的数量 */
  keysFailed: number
}

/**
 * 启动期一次性:把库里用户添加的供应商 / 别名 / 密钥搬进 `providers.json`。
 *
 * ★ **不动手之前先做整库快照**(`VACUUM INTO`),快照失败就整体放弃 —— 后面要删旧行,
 *   没有退路的迁移不做。
 * ★ 文件已存在(用户或外部工具先手写了)时**并入而不是覆盖**:文件是权威,
 *   库里只补文件没有的那几条。
 * ★ 内置预设(RoutinAI 那两条)没配过 key 的不是「用户添加的」,不进文件,
 *   否则「文件里只有用户自己加的」这条对老用户就不成立。
 * ★ 解不开的密文:供应商仍进文件(没有 key),密文**留在库里**不删。
 *
 * 任何一步抛错都不会留下半成品:库只在文件校验通过之后才清理,调用方捕获异常后
 * 不装上文件模式,继续按库运行。
 */
export function migrateProvidersToFileWith(
  file: ProviderFileStore,
  decrypt: (blob: Uint8Array) => string
): ProviderFileMigrationResult {
  const loaded = file.load()
  if (!loaded.ok) throw new Error(loaded.error)
  const none = { providers: 0, skippedBuiltin: 0, keysFailed: 0 }
  if (getKv<boolean>(PROVIDER_FILE_MIGRATED_KEY, false) === true) return { status: 'already-done', ...none }

  const legacy = listLegacyDbProviders()
  if (legacy.length === 0) {
    setKv(PROVIDER_FILE_MIGRATED_KEY, true)
    return { status: 'nothing-to-migrate', ...none }
  }

  const builtin = new Set(BUILTIN_PROVIDER_IDS)
  const credentials: Record<string, string> = {}
  const migratedRefs: string[] = []
  const kept: UpstreamProvider[] = []
  let skippedBuiltin = 0
  let keysFailed = 0
  for (const provider of legacy) {
    const ref = providerCredentialRef(provider.id)
    const blob = getCredential(ref)
    if (blob === undefined && builtin.has(provider.id)) {
      skippedBuiltin += 1
      continue
    }
    kept.push(provider)
    if (blob === undefined) continue
    try {
      const plain = decrypt(blob)
      if (plain !== '') credentials[provider.id] = plain
      migratedRefs.push(ref)
    } catch {
      keysFailed += 1
    }
  }

  const backup = join(databaseDirectory(), PROVIDER_FILE_BACKUP_NAME)
  if (!existsSync(backup)) backupDatabaseTo(backup)

  const keptIds = new Set(kept.map((p) => p.id))
  file.importLegacy({
    providers: kept,
    aliases: listLegacyDbAliases().filter((a) => keptIds.has(a.providerId)),
    credentials
  })
  const missing = kept.filter((p) => !file.hasProvider(p.id))
  if (missing.length > 0) throw new Error(`迁移校验失败:文件里缺少 ${missing.map((p) => p.id).join(', ')}`)

  purgeLegacyDbProviders(legacy.map((p) => p.id), migratedRefs)
  setKv(PROVIDER_FILE_MIGRATED_KEY, true)
  return { status: 'migrated', providers: kept.length, skippedBuiltin, keysFailed }
}
