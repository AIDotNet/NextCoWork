/**
 * KernelHost 的无头实现 —— 独立设备服务用,**零 Electron**。
 *
 * 与 `host/index.ts` 的 Electron 版是同一个思路:`nodeHost()` 是真实默认值,这里只覆盖
 * `paths` 与 `secrets` 两项。区别只有两处:
 *
 * - 凭证用程序自管的 AES-GCM(NCK1,主密钥在数据目录的 `credential.key`),
 *   **没有** safeStorage:库里若有旧 safeStorage 密文(从桌面带过来、尚未迁移的行),
 *   读取时明确失败,而不是静默当成空 —— 那些行必须先在桌面端迁移。
 * - 网络与代理走 Node 默认栈(`fetch` 即全局 fetch),没有 Chromium session 可问。
 */
import { join } from 'node:path'
import { databaseDirectory } from '../db'
import { configProfileDirectory } from '../db/config-profile'
import {
  fileCredentialProviderId, getCredential, listAllCredentialBlobs, providerFileStore, putCredential, removeCredential
} from '../db/repo'
import { agentShell } from '../kernel/node-spawn'
import { nodeHost, type KernelHost } from '../kernel/host'
import {
  decryptCredentialValue, encryptCredentialValue, isProgramEncrypted, loadMasterKey, loadOrCreateMasterKey
} from '../secrets/credential-crypto'
import { attachmentRoot } from '../services/attachment-paths'

function withMasterKey<T>(create: boolean, use: (key: Buffer) => T): T {
  const key = create ? loadOrCreateMasterKey(databaseDirectory()) : loadMasterKey(databaseDirectory())
  try { return use(key) } finally { key.fill(0) }
}

function headlessSecrets(): KernelHost['secrets'] {
  const setSync = (ref: string, value: string): void => {
    const fileProviderId = fileCredentialProviderId(ref)
    if (fileProviderId !== null) {
      const file = providerFileStore()
      if (value === '') file?.removeCredential(fileProviderId)
      else file?.setCredential(fileProviderId, value)
      return
    }
    // 库里已有 NCK1 就必须用同一把 key;key 丢了/不匹配要抛错,不能再生成一把造成同表两套密文。
    const programRow = listAllCredentialBlobs().find((row) => isProgramEncrypted(row.blob))
    putCredential(ref, withMasterKey(programRow === undefined, (key) => {
      if (programRow !== undefined) decryptCredentialValue(key, programRow.blob)
      return encryptCredentialValue(key, value)
    }))
  }
  const removeSync = (ref: string): void => removeCredential(ref)
  return {
    get: async (ref) => {
      const fileProviderId = fileCredentialProviderId(ref)
      if (fileProviderId !== null) return providerFileStore()?.getCredential(fileProviderId) ?? null
      const blob = getCredential(ref)
      if (blob === undefined) return null
      if (!isProgramEncrypted(blob)) throw new Error('Credential uses desktop-only encryption; migrate it on the desktop first')
      return withMasterKey(false, (key) => decryptCredentialValue(key, blob))
    },
    set: async (ref, value) => setSync(ref, value),
    setSync,
    remove: async (ref) => removeSync(ref),
    removeSync,
    available: () => true
  }
}

/** 数据目录必须是已打开的库所在目录(`databaseDirectory()`),与桌面版同一条规则。 */
export function headlessHost(overrides: Partial<KernelHost> = {}): KernelHost {
  return nodeHost({
    paths: {
      userData: () => configProfileDirectory(databaseDirectory()),
      attachments: attachmentRoot,
      temp: () => join(databaseDirectory(), 'tmp')
    },
    secrets: headlessSecrets(),
    ...overrides
  }, () => agentShell())
}
