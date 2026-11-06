/**
 * KernelHost 的 Electron 实现 —— **electron 只在这里泄漏**(方案 §2 的拓扑图)。
 *
 * `host.ts` 已经把 `nodeHost()` 定义成真实默认值,所以这里只覆盖五项:
 *
 * | 端口 | 为什么非 Electron 不可 |
 * |---|---|
 * | `paths`   | 统一的用户级 `~/.next-cowork` 数据根与系统临时目录 |
 * | `secrets` | 程序主密钥加密后存 SQLite;旧 safeStorage 密文仍需 Electron 解密迁移 |
 * | `fetch`   | `net.fetch` 走 Chromium 网络栈,于是 `net/proxy.ts` 那一次 `setProxy` 对全应用的出站请求一起生效 |
 * | `childEnv`| Agent 子进程的代理环境变量 —— 系统代理/PAC 只有 Chromium 的 session 知道,`shellProxyEnv` 负责去问 |
 * | `resolveProxyForHost` | SSH 同样是子进程,`setProxy` 管不到它 —— 「连这台主机该走哪个代理」也只有 Chromium 的 session 知道,`net/proxy.ts` 负责去问 |
 *
 * 其余端口(clock / logger / fs / spawn)在 Electron 里和在 Node 里是同一件事,
 * 覆盖它们只会多一份要同步维护的代码。(spawn 会**消费** childEnv,见 nodeHost;
 * 那是转发,不是覆盖。)
 */
import { app, net, safeStorage } from 'electron'
import {
  fileCredentialProviderId,
  getCredential,
  getSettings,
  listAllCredentialBlobs,
  providerFileStore,
  putCredential,
  putCredentialBlobAtPhysicalRef,
  removeCredential
} from '../db/repo'
import { databaseDirectory } from '../db'
import type { ProviderFileStore } from '../db/provider-file'
import { migrateProvidersToFileWith, type ProviderFileMigrationResult } from '../db/provider-file-migration'
import {
  decryptCredentialValue,
  encryptCredentialValue,
  isProgramEncrypted,
  loadMasterKey,
  loadOrCreateMasterKey,
  migrateCredentialRows,
  type CredentialMigrationResult
} from '../secrets/credential-crypto'
import { configProfileDirectory } from '../db/config-profile'
import { attachmentRoot } from '../net/attachment-protocol'
import { shellProxyEnv, resolveProxyForHost } from '../net/proxy'
import type { KernelHost } from '../kernel/host'
import { nodeHost } from '../kernel/host'
import { agentShell } from '../kernel/node-spawn'
import { withDemo } from '../kernel/upstream/demo'

/**
 * 程序自管 AES-GCM 版的凭证存取。
 *
 * 密文落在 `credentials` 表里(`db/schema.ts`),主密钥在同目录的
 * `credential.key`。`masterKey()` **每次现读**而不永久缓存:恢复流程会关库、
 * 在同一路径替换数据库和 key;按目录缓存识别不出这次替换,会拿旧 key 解
 * 新库,让整张凭证表看起来都损坏。32 字节本地读取远小于一次上游请求的开销。
 *
 * ★ 无 NCK1 magic 的行仍走 safeStorage。这不是迁移结束就能删的临时代码:
 * 用户随时可能从旧版本导入数据,双格式读是那条回退路径的最后一道保险。
 */
function withMasterKey<T>(create: boolean, use: (key: Buffer) => T): T {
  const key = create
    ? loadOrCreateMasterKey(databaseDirectory())
    : loadMasterKey(databaseDirectory())
  try { return use(key) } finally { key.fill(0) }
}

function decryptBlob(blob: Uint8Array): string {
  return isProgramEncrypted(blob)
    ? withMasterKey(false, (key) => decryptCredentialValue(key, blob))
    : safeStorage.decryptString(Buffer.from(blob))
}

function electronSecrets(): KernelHost['secrets'] {
  const setSync = (ref: string, value: string): void => {
      // `provider:<id>` 的密钥在文件模式下归 providers.json(明文),不走 NCK1。
      const fileProviderId = fileCredentialProviderId(ref)
      if (fileProviderId !== null) {
        const file = providerFileStore()
        if (value === '') file?.removeCredential(fileProviderId)
        else file?.setCredential(fileProviderId, value)
        return
      }
      // 库里已有 NCK1 说明它们和某一把 key 绑定。key 文件丢了或不匹配
      // 就必须抛错;生成新 key 会让同一张表出现两套不可兼容密文。
      const programRow = listAllCredentialBlobs().find((row) => isProgramEncrypted(row.blob))
      const create = programRow === undefined
      putCredential(ref, withMasterKey(create, (key) => {
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
      return blob === undefined ? null : decryptBlob(blob)
    },
    set: async (ref, value) => setSync(ref, value),
    setSync,
    remove: async (ref) => removeSync(ref),
    removeSync,
    // 可用性的判据从「系统 keyring」变成「数据目录可读写」。真正的权限错误
    // 会在第一次 get/set 时抛出,而不是把密钥静默降级成明文。
    available: () => true
  }
}

export { PROVIDER_FILE_BACKUP_NAME, type ProviderFileMigrationResult } from '../db/provider-file-migration'

/** 启动期一次性迁移,见 `db/provider-file-migration.ts`。这里只负责把「怎么解密」递进去。 */
export function migrateProvidersToFile(file: ProviderFileStore): ProviderFileMigrationResult {
  return migrateProvidersToFileWith(file, decryptBlob)
}

/**
 * 启动期把所有作用域的 safeStorage 行迁到 NCK1。单行解不开只计 failed 并原样保留,
 * 不能因一个损坏/异机密文让应用整体起不来。
 */
export function migrateLegacyCredentials(): CredentialMigrationResult {
  const rows = listAllCredentialBlobs()
  if (rows.length === 0) return { migrated: 0, skipped: 0, failed: 0 }
  const hasProgramRows = rows.some((row) => isProgramEncrypted(row.blob))
  let key: Buffer
  try {
    key = hasProgramRows
      ? loadMasterKey(databaseDirectory())
      : loadOrCreateMasterKey(databaseDirectory())
  } catch {
    // 旧行原样保留。若已有 NCK1 却丢了 key,生成一把新 key 只会制造同表两套
    // 不兼容密文;这里宁可让迁移全部显式失败。
    return {
      migrated: 0,
      skipped: rows.filter((row) => isProgramEncrypted(row.blob)).length,
      failed: rows.filter((row) => !isProgramEncrypted(row.blob)).length
    }
  }
  try {
    return migrateCredentialRows({
      list: () => rows,
      put: putCredentialBlobAtPhysicalRef,
      decryptLegacy: (blob) => {
        if (!safeStorage.isEncryptionAvailable()) return null
        try {
          return safeStorage.decryptString(Buffer.from(blob))
        } catch {
          return null
        }
      },
      encrypt: (plain) => encryptCredentialValue(key, plain)
    })
  } finally {
    key.fill(0)
  }
}

/**
 * `net.fetch` 的签名比 WHATWG fetch 窄一点(不吃 `URL`),补一层适配。
 *
 * 用它而不是全局 fetch,是为了让请求走 Chromium 网络栈:企业证书、系统代理,
 * 以及**设置页那份代理配置** —— 后者不是自动的,由 `net/proxy.ts` 显式
 * `session.defaultSession.setProxy()` 装上去(Chromium 默认只跟随系统代理)。
 * 全局 fetch 走的是 Node 的网络栈,那三样一样都拿不到。
 */
const electronFetch: typeof fetch = (input, init) =>
  net.fetch(input instanceof URL ? input.href : input, init)

export function electronHost(): KernelHost {
  /**
   * ★ `safeStorage` 与 `net.fetch` 都要求 app ready(方案 §9)。早一步调用拿到的是
   * 一个看起来正常、实际不可用的 host,症状会推迟到第一次发请求才出现 ——
   * 那时错误信息里已经没有「调早了」这条线索了。
   */
  if (!app.isReady()) {
    throw new Error('electronHost() 必须在 app.whenReady() 之后调用')
  }
  return withDemo(
    nodeHost({
      paths: {
        // ★ 必须是 `databaseDirectory()`(**已打开的库**所在目录)而不是那个默认值 ——
        // 传了 `--user-data-dir` 或走恢复流程时两者会分叉,skills/agents/commands 的
        // 文件树就会写到一个跟数据库无关的目录里去。这个函数是 lazy 的,调用时库一定已经打开。
        //
        // ★★ 外面再套一层 `configProfileDirectory`:**skills / commands / agents /
        // settings.json 是账户配置**,一个账户写的 Skill 不该在另一个账户的列表里出现。
        // `local` 返回这个根本身(老库的文件一个都不动),账户作用域返回
        // `<root>/config-profiles/<sha256(id)>` —— 于是这一层不需要任何「读的时候再筛一次」,
        // 上下两路拿到的是两个真正不同的目录。
        userData: () => configProfileDirectory(databaseDirectory()),
        /*
          ★ 附件根**不**跟作用域走,直接复用上传/预览的 `attachmentRoot`。
          不走 `configProfileDirectory`,避免再维护一份目录拼接规则,
          否则「写进去的图读不出来」会在登录之后才第一次出现。
        */
        attachments: attachmentRoot,
        temp: () => app.getPath('temp')
      },
      secrets: electronSecrets(),
      fetch: electronFetch,
      // Agent 的 shell 命令默认跟随应用/系统代理 —— 见上面表格里 childEnv 那一行
      childEnv: shellProxyEnv,
      /*
        SSH 也是子进程,`setProxy` 管不到它 —— 它反过来问「连这台主机该走哪个代理」
        (`environment/ssh/proxy.ts`)。判定与凭据全在 `net/proxy.ts`,和模型请求同一份。
      */
      resolveProxyForHost
    }, () => agentShell(getSettings().shell))
  )
}
