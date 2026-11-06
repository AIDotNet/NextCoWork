/**
 * 本机数据管理的主进程实现。
 *
 * 所有路径、对话框、数据库快照和删除操作都集中在这里；渲染层只收到
 * 结构化结果，不能把任意路径传进来。归档使用无压缩 ZIP（读取时同时支持
 * deflate），不依赖系统 zip 命令，因此打包到另一台设备仍可恢复。
 */
import { app, dialog, shell } from 'electron'
import { createHash, createCipheriv, createDecipheriv, randomBytes, scryptSync } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import {
  copyFileSync,
  accessSync,
  constants,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  unlinkSync,
  writeFileSync,
  openSync,
  closeSync,
  chmodSync,
  fsyncSync,
  lstatSync,
  realpathSync
} from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, dirname, isAbsolute, join, relative, resolve } from 'node:path'
import { inflateRawSync } from 'node:zlib'
import type {
  BackupManifest,
  BackupStatus,
  CleanupAge,
  CleanupPreview,
  CleanupResult,
  DataExport,
  EncryptedCredentials,
  ImportApplyResult,
  ImportPreview,
  RestorePreview,
  RestoreResult
} from '../../shared/domain/data'
import {
  BACKUP_FORMAT_VERSION,
  DATA_EXPORT_VERSION,
  cutoffForAge,
  dataMergeDecision,
  isDataExport
} from '../../shared/domain/data'
import { DEFAULT_SETTINGS, mergeSettings, type ShellPreference, type StorageStats } from '../../shared/domain/settings'
import { mcpSecretKind, mcpSecretRef } from '../../shared/domain/mcp'
import { searchSecretRef } from '../../shared/domain/search'
import { providerCredentialRef } from '../../shared/domain/provider'
import { providerAccountCredentialRef } from '../../shared/domain/provider-account'
import { DRAFT_ATTACHMENT_TTL_MS } from '../../shared/domain/attachment'
import { databaseDirectory, databaseFilePath, checkpointDatabase, closeDatabase, openDatabase, txAsync, vacuumDatabase } from '../db'
import { MIGRATIONS } from '../db/schema'
import { PROFILE_DIRECTORY_SEGMENT } from '../db/config-profile'
import {
  CHROMIUM_PROFILE_ENTRIES,
  CHROMIUM_PROFILE_FILE_PATTERNS,
  CHROMIUM_SUBDIRNAME
} from '../db/chromium-layout'
import * as repo from '../db/repo'
import { getHost } from '../runtime'
import { migrateLegacyCredentials } from '../host'
import { pauseConfigSyncForDatabaseReplacement } from './config-sync'
import { sessionRuntime } from './agent'
import { resumeStoredAccountAfterDatabaseReplacement } from './client-auth'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { PROXY_PASSWORD_REF } from '../net/proxy'
import { THUMBNAIL_CACHE_DIR } from '../net/attachment-thumbnail'
import { IpcError } from './errors'
import { exportProviderAccounts, mergeProviderAccounts } from './provider-accounts-transfer'
import { isWithin, recordPendingDelete } from './pending-delete'
import { runs } from '../kernel/run-registry'
import { jobStatusFor } from '../imports/service'
import { store as stateStore } from '../state/store'
import { GLOBAL_SETTINGS_FILENAME, clearGlobalSettingsCache } from '../kernel/local-settings'
import { CREDENTIAL_KEY_FILENAME, isProgramEncrypted } from '../secrets/credential-crypto'
import { runBackupArchive, runBackupSnapshot } from '../db/backup-worker'

/**
 * 破坏性操作与导入作业**互斥**。
 *
 * ★ 和上面那道「有运行中的 Agent」是同一类守卫,但挡的是另一种事故:
 * 导入作业正在逐项写会话和文件,此时恢复整库或清空数据库,会让一半的
 * `import_mappings` 指向已经不存在的行 —— 而下一轮自动同步读到那些行,
 * 会认为「导过了」,于是那部分内容再也不会被重新导入。
 */
function assertNoImportJob(action: string): void {
  for (const source of stateStore.listImportSources()) {
    const job = jobStatusFor(source.sourceId)
    if (job === null) continue
    if (job.phase === 'scanning' || job.phase === 'importing' || job.phase === 'ready') {
      throw new IpcError('unknown', `有正在进行的导入任务，请先等待或取消后再${action}`)
    }
  }
}

const BACKUP_STATUS_KEY = 'data.backup.status'
const ATTACHMENTS_DIR = 'attachments'
const BACKUP_EXT = '.ncwbackup'
const AUTO_BACKUP_RE = /^nextcowork-auto\.ncwbackup$/
const MAX_IMPORT_BYTES = 128 * 1024 * 1024
const MAX_BACKUP_BYTES = 1024 * 1024 * 1024
// Backup format v1 was introduced together with the session/message schema.
// Earlier database versions do not contain the tables required by its
// manifest counts and must be migrated by the old app before archiving.
const MIN_BACKUP_SCHEMA_VERSION = 4
/**
 * Electron keeps its profile caches beside our database because the app uses
 * the same project-level userData directory.  They are application-owned
 * local data too, and must be removed by "delete and quit".
 *
 * ★ 迁移后会话集整体落在 `<profile 根>/chromium`(见 `db/chromium-layout.ts`);
 *   这份扁平名单是「删除全部数据」认领 Chromium 资产的单一事实源,同时兜底那几样
 *   仍留在根层的条目(`Preferences` / `Local State` / `Crashpad`)。
 */
const ELECTRON_PROFILE_PATHS = CHROMIUM_PROFILE_ENTRIES
/**
 * 应用自己的数据 —— 全部挂在**数据根**(`<profile 根>/data`)下。
 *
 * 清单保持显式:用户在排查问题时可能往目录里放无关文件,一刀递归清扫会很意外。
 */
const MANAGED_DATA_PATHS = [
  ATTACHMENTS_DIR,
  // 图片缩略档的派生缓存(`net/attachment-thumbnail.ts`)。不进备份,但「删除全部数据」要带走
  THUMBNAIL_CACHE_DIR,
  'skills',
  'agents',
  // ★ 全局斜杠命令。漏了它的话「删除全部数据并退出」之后,用户自己写的 `/xxx`
  // 还原封不动地留在盘上 —— 和 skills/agents 同一棵树,没有理由区别对待。
  'commands',
  'modes',
  'plugins',
  // 文档引擎的私有副本与 helper 临时目录也属于应用数据，清理时不能留下用户文档副本。
  'document-engine',
  'workspaces',
  // credentials 表的 NCK1 密文离了这 32 字节就永远解不开。
  CREDENTIAL_KEY_FILENAME,
  // 账户作用域的 skills / agents / settings.json 各占一棵 `<hash>` 子树。
  PROFILE_DIRECTORY_SEGMENT,
  // Migrated application instructions also belong to this app-level tree.
  'AGENTS.md',
  // ★ 全局设置(目前只有钩子)。同上:不清的话「全部删除」之后钩子还在跑。
  GLOBAL_SETTINGS_FILENAME
] as const
/**
 * 挂在 **profile 根**上的条目:Chromium 自己的 profile,加上几样同层的历史遗留。
 *
 * `themes` 是数据根搬家之前的旧主题目录(`ipc/theme.ts` 的 `legacyThemesDir()`
 * 仍按 `app.getPath('userData')` 去找它);`plugin`(单数) / `logs` / `cache`
 * 已经没有任何生产代码往里写,但老版本留下的目录还在用户盘上,继续清掉。
 *
 * ★ `chromium` 是迁移后会话集所在的子目录 —— 删它一条就带走整套 cookie / 缓存 /
 *   分区(`db/chromium-layout.ts`)。扁平的 `...ELECTRON_PROFILE_PATHS` 保留:兜底
 *   未迁移(或留在根层)的 `Preferences` / `Local State` / `Crashpad`。
 */
const MANAGED_PROFILE_PATHS = [
  'themes',
  'plugin',
  'logs',
  'cache',
  'headless-browsers',
  'headless-profiles',
  'DevToolsActivePort',
  CHROMIUM_SUBDIRNAME,
  ...ELECTRON_PROFILE_PATHS
] as const
/**
 * Chromium and SQLite may append a numeric collision suffix to a top-level
 * file (for example `DIPS-wal 3` or `nextcowork 2.db-shm`).  Keep the match
 * deliberately narrow: delete-and-quit must remove every file the app owns
 * without turning either root into an arbitrary recursive delete.
 *
 * ★ 两组分别扫各自的根 —— `nextcowork 2.db-shm` 只会出现在数据根,
 * `DIPS-wal 3` 只会出现在 profile 根。拿一组去扫两边等于放宽了删除边界。
 */
const MANAGED_DATA_FILE_PATTERNS = [/^nextcowork [1-9]\d*\.db(?:-(?:wal|shm))?$/u] as const
const MANAGED_PROFILE_FILE_PATTERNS = CHROMIUM_PROFILE_FILE_PATTERNS
/**
 * 「删不掉就推迟到下次启动」只对 Chromium 自己拥有的那些路径成立。
 *
 * ★ 主进程活着的时候 Chromium 一直握着 profile 里的文件句柄,而 Windows 不允许
 *   rename 一棵内部有打开句柄的目录。删除请求恰恰是在应用还活着时发出的,所以
 *   `GPUCache` 之流在 Windows 上**必然** `EPERM` —— 旧代码让它整体回滚,结果是
 *   「删除并退出」在 Windows 上一个字节也删不掉。收尾方案见 `./pending-delete.ts`。
 *
 * ★ 判据刻意只认 Chromium 的东西。`nextcowork.db*` / `attachments` / `logs` /
 *   `settings.json` 这些是我们自己的数据,它们搬不动就是真出了问题,必须维持
 *   「全有或全无」—— 暂存机制存在的全部理由就是不留下删了一半的库。
 *   `nextcowork 2.db` 这类带碰撞后缀的副本同样归我们,不在此列。
 */
const DEFERRABLE_LOCAL_NAMES = new Set<string>([
  ...ELECTRON_PROFILE_PATHS,
  // 迁移后 Chromium 一直握着 `chromium/` 整棵目录的句柄,Windows 上 rename 它必 EPERM。
  CHROMIUM_SUBDIRNAME,
  'headless-browsers',
  'headless-profiles',
  'DevToolsActivePort'
])
const DEFERRABLE_LOCAL_PATTERNS = CHROMIUM_PROFILE_FILE_PATTERNS

function isDeferrableLocalPath(path: string): boolean {
  const name = basename(path)
  return DEFERRABLE_LOCAL_NAMES.has(name) || DEFERRABLE_LOCAL_PATTERNS.some((p) => p.test(name))
}

/**
 * 目标被别的进程占着的那几种 errno。Windows 报 `EPERM`,POSIX 上更常见 `EBUSY`。
 *
 * ★ 要穿过 `cause` 看。`stageManagedPath` 把 fs 的错误重包成 `IpcError`,
 *   只看最外层的话 `code` 永远是 undefined,这个判据会一律返回 false。
 */
function isLockedError(err: unknown): boolean {
  for (let current: unknown = err, depth = 0; current !== undefined && current !== null && depth < 4; depth++) {
    const code = (current as NodeJS.ErrnoException).code
    if (code === 'EPERM' || code === 'EACCES' || code === 'EBUSY' || code === 'ENOTEMPTY') return true
    current = (current as { cause?: unknown }).cause
  }
  return false
}

// Confirmation state is scoped to the WebContents that opened the preview.
// Otherwise a confirmation in window B could apply the file selected in A.
// Direct unit tests use owner 0.
const pendingImports = new Map<number, DataExport>()
const pendingRestores = new Map<number, { path: string; preview: RestorePreview }>()
let backupRunning = false

interface StoredBackupStatus {
  lastBackupAt: number | null
  lastBackupPath: string | null
  lastError: string | null
}

function dataDirectory(): string {
  return databaseDirectory()
}

/**
 * Electron profile 根 —— 数据根的父目录。
 *
 * ★ 契约:数据根**必须**是 profile 根下的 `data/`(见 `db/index.ts` 的
 * `DATA_SUBDIRNAME`)。`clearLocalData` 把删除边界定在 profile 根上,靠的正是
 * 这层父子关系 —— 两边的路径才能同时落在 `stageManagedPath` 的界内。
 */
function profileRootDirectory(): string {
  return dirname(dataDirectory())
}

function collectManagedPaths(
  root: string,
  names: readonly string[],
  patterns: readonly RegExp[]
): string[] {
  const set = new Set<string>(names)
  try {
    for (const name of readdirSync(root)) {
      if (patterns.some((pattern) => pattern.test(name))) set.add(name)
    }
  } catch { /* a missing/unreadable root is handled by each target operation */ }
  return [...set].map((name) => join(root, name))
}

/**
 * 「删除全部数据并退出」要搬走的全部路径。
 *
 * ★ 数据根的条目排在前面。profile 那一侧可能被 Chromium 占着而推迟到下次启动,
 * 让我们自己的数据先落袋更符合「全有或全无」的语义。
 */
function managedLocalPaths(profileRoot: string, dataRoot: string): string[] {
  return [
    ...collectManagedPaths(dataRoot, MANAGED_DATA_PATHS, MANAGED_DATA_FILE_PATTERNS),
    ...collectManagedPaths(profileRoot, MANAGED_PROFILE_PATHS, MANAGED_PROFILE_FILE_PATTERNS)
  ]
}

function attachmentDirectory(): string {
  return join(dataDirectory(), ATTACHMENTS_DIR)
}

function readBackupStatus(): StoredBackupStatus {
  const raw = store.getKv<unknown>(BACKUP_STATUS_KEY, null)
  if (typeof raw !== 'object' || raw === null) return { lastBackupAt: null, lastBackupPath: null, lastError: null }
  const r = raw as Record<string, unknown>
  return {
    lastBackupAt: typeof r.lastBackupAt === 'number' ? r.lastBackupAt : null,
    lastBackupPath: typeof r.lastBackupPath === 'string' ? r.lastBackupPath : null,
    lastError: typeof r.lastError === 'string' ? r.lastError : null
  }
}

function writeBackupStatus(status: StoredBackupStatus): void {
  store.setKv(BACKUP_STATUS_KEY, status)
}

interface LocalBackupState {
  directory: string | null
  status: StoredBackupStatus
  shell: ShellPreference
}

/**
 * A backup directory is a local-device preference. It must never be imported
 * from another machine's database, and a malformed legacy value must not turn
 * into a path relative to the process working directory.
 */
function normalizeLocalBackupDirectory(value: unknown, root = dataDirectory(), dbPath = databaseFilePath()): string | null {
  if (typeof value !== 'string' || !isAbsolute(value)) return null
  const path = resolve(value)
  if (isDangerousBackupPath(path, root, dbPath)) return null
  // A symlink selected as the backup directory is ambiguous: preserving the
  // link can preserve an application-managed path, while following it can
  // make a delete operation cross the data boundary.  Treat legacy symlink
  // values as invalid; the user can choose the real directory again.
  try {
    if (lstatSync(path).isSymbolicLink()) return null
  } catch (err) {
    if ((err as NodeJS.ErrnoException)?.code !== 'ENOENT') return null
  }
  return path
}

/**
 * Resolve an existing prefix through symlinks while retaining non-existent
 * trailing components.  Backup paths are user-controlled filesystem input;
 * lexical `resolve()` alone is not enough to decide whether a path overlaps
 * the application's managed tree.
 */
function canonicalBoundaryPath(path: string): string {
  let current = resolve(path)
  const tail: string[] = []
  while (true) {
    try {
      const real = realpathSync(current)
      return resolve(real, ...tail.reverse())
    } catch (err) {
      if ((err as NodeJS.ErrnoException)?.code !== 'ENOENT') return resolve(path)
      const parent = dirname(current)
      if (parent === current) return resolve(path)
      tail.push(basename(current))
      current = parent
    }
  }
}

/**
 * Backup directories must be disjoint from the application data tree.  This
 * rejects the root itself, every managed descendant (including the database
 * file), and an ancestor that would make the whole application tree part of
 * the protected backup directory.  Symlink targets are checked canonically.
 */
function isDangerousBackupPath(path: string, root: string, dbPath: string | null): boolean {
  const managedRoot = resolve(root)
  if (path === managedRoot || isWithin(managedRoot, path) || isWithin(path, managedRoot)) return true
  if (dbPath !== null && path === resolve(dbPath)) return true
  const canonicalRoot = canonicalBoundaryPath(managedRoot)
  const canonicalPath = canonicalBoundaryPath(path)
  return canonicalPath === canonicalRoot || isWithin(canonicalRoot, canonicalPath) || isWithin(canonicalPath, canonicalRoot)
}

/** Return the lexical path only when it is a safe external location. */
function externalBackupPath(value: unknown, root = dataDirectory(), dbPath = databaseFilePath()): string | null {
  if (typeof value !== 'string' || !isAbsolute(value)) return null
  const path = resolve(value)
  const managedRoot = resolve(root)
  if (dbPath !== null && path === resolve(dbPath)) return null
  if (path === managedRoot || isWithin(path, managedRoot)) return null

  const canonicalRoot = canonicalBoundaryPath(managedRoot)
  const canonicalPath = canonicalBoundaryPath(path)
  if (isWithin(canonicalPath, canonicalRoot)) return null
  if (isWithin(canonicalRoot, canonicalPath)) {
    // A legacy configuration may be a symlink *inside* the managed tree that
    // points to an external backup directory. Preserve the link itself, but
    // never follow it. Regular descendants and symlinks that resolve back
    // into the managed tree are not protected.
    try {
      if (lstatSync(path).isSymbolicLink()) return path
    } catch { /* a missing path cannot be a managed symlink */ }
    return null
  }
  // The directory must be disjoint from the managed root. An ancestor would
  // otherwise protect the entire application tree during clear-local-data.
  return path
}

function captureLocalBackupState(): LocalBackupState {
  const settings = store.getSettings()
  return {
    directory: normalizeLocalBackupDirectory(settings.data.backupDirectory),
    status: readBackupStatus(),
    shell: settings.shell
  }
}

/**
 * Keep only a status that can be proven to belong to the current device's
 * configured directory. This prevents an archive from another machine from
 * leaving an absolute path in `data.backup.status` after restore.
 */
function sanitizeLocalBackupStatus(directory: string | null, status: StoredBackupStatus): StoredBackupStatus {
  if (directory === null) return { lastBackupAt: null, lastBackupPath: null, lastError: null }
  if (status.lastBackupPath === null) return { ...status }
  if (!isAbsolute(status.lastBackupPath) || !isWithin(directory, status.lastBackupPath)) {
    return { lastBackupAt: null, lastBackupPath: null, lastError: null }
  }
  return { ...status, lastBackupPath: resolve(status.lastBackupPath) }
}

/** Re-apply local-only backup settings and the shell after replacing a database file. */
function restoreLocalBackupState(state: LocalBackupState): void {
  const directory = normalizeLocalBackupDirectory(state.directory)
  store.updateSettings({ data: { backupDirectory: directory }, shell: state.shell })
  writeBackupStatus(sanitizeLocalBackupStatus(directory, state.status))
}

function toBackupStatus(): BackupStatus {
  const settings = store.getSettings()
  const s = readBackupStatus()
  const directory = normalizeLocalBackupDirectory(settings.data.backupDirectory)
  return {
    directory,
    lastBackupAt: s.lastBackupAt,
    lastBackupPath: s.lastBackupPath,
    lastError: s.lastError,
    running: backupRunning
  }
}

function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex')
}

function jsonBytes(value: unknown): Buffer {
  return Buffer.from(JSON.stringify(value, null, 2), 'utf8')
}

function atomicWrite(path: string, bytes: Uint8Array): void {
  mkdirSync(dirname(path), { recursive: true })
  const tmp = `${path}.tmp-${process.pid}-${Date.now()}`
  let fd: number | null = null
  try {
    fd = openSync(tmp, 'w')
    writeFileSync(fd, bytes)
    fsyncSync(fd)
    closeSync(fd)
    fd = null
    const persisted = readFileSync(tmp)
    if (persisted.length !== bytes.length || sha256(persisted) !== sha256(bytes)) {
      throw new IpcError('unknown', '临时文件写入校验失败')
    }
    renameSync(tmp, path)
  } catch (err) {
    // A failed write/rename must never leave a file that looks like a usable
    // export or backup.  In particular, a later automatic-backup sweep must
    // not mistake the temporary file for the last successful backup.
    if (fd !== null) {
      try { closeSync(fd) } catch { /* best effort */ }
    }
    try { unlinkSync(tmp) } catch { /* the rename may already have won */ }
    throw err
  }
}

// ── minimal ZIP writer/reader ──────────────────────────────────────────────

const CRC_TABLE = (() => {
  const table = new Uint32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = (c & 1) !== 0 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    table[n] = c >>> 0
  }
  return table
})()

function crc32(data: Uint8Array): number {
  let c = 0xffffffff
  for (const b of data) c = CRC_TABLE[(c ^ b) & 0xff]! ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

/**
 * Read the central directory instead of trusting local headers alone.
 *
 * A local-header-only reader accepts truncated archives, duplicate names and
 * arbitrary bytes after the last entry.  That is especially dangerous for a
 * restore operation because the first matching file silently wins.  We only
 * need the small, single-disk subset used by our backups, but we validate that
 * subset completely (including CRC and local/central header agreement).
 */
function readZip(bytes: Buffer): Map<string, Buffer> {
  const out = new Map<string, Buffer>()
  if (bytes.length < 22) throw new IpcError('unknown', '不是有效的 ZIP 备份文件')

  // EOCD may be preceded by a comment of at most 65535 bytes.  Require its
  // declared comment to end exactly at EOF; this rejects tail garbage.
  let eocd = -1
  const first = Math.max(0, bytes.length - (22 + 0xffff))
  for (let i = bytes.length - 22; i >= first; i--) {
    if (bytes.readUInt32LE(i) !== 0x06054b50) continue
    const commentLength = bytes.readUInt16LE(i + 20)
    if (i + 22 + commentLength === bytes.length) {
      eocd = i
      break
    }
  }
  if (eocd < 0) throw new IpcError('unknown', 'ZIP 归档缺少结束目录')

  const disk = bytes.readUInt16LE(eocd + 4)
  const centralDisk = bytes.readUInt16LE(eocd + 6)
  const onDisk = bytes.readUInt16LE(eocd + 8)
  const total = bytes.readUInt16LE(eocd + 10)
  const centralSize = bytes.readUInt32LE(eocd + 12)
  const centralOffset = bytes.readUInt32LE(eocd + 16)
  // ZIP64/multi-disk archives are outside the backup format we produce.
  if (disk !== 0 || centralDisk !== 0 || onDisk !== total || total === 0 || total === 0xffff || centralSize === 0xffffffff || centralOffset === 0xffffffff) {
    throw new IpcError('unknown', '不支持的 ZIP 归档结构')
  }
  if (centralOffset > eocd || centralSize > eocd - centralOffset || centralOffset + centralSize !== eocd) {
    throw new IpcError('unknown', 'ZIP 中央目录越界')
  }

  interface CentralEntry {
    name: string
    flags: number
    method: number
    crc: number
    compressedSize: number
    uncompressedSize: number
    localOffset: number
  }

  const entries: CentralEntry[] = []
  const names = new Set<string>()
  let offset = centralOffset
  for (let i = 0; i < total; i++) {
    if (offset + 46 > eocd || bytes.readUInt32LE(offset) !== 0x02014b50) {
      throw new IpcError('unknown', 'ZIP 中央目录条目损坏')
    }
    const flags = bytes.readUInt16LE(offset + 8)
    const method = bytes.readUInt16LE(offset + 10)
    const crc = bytes.readUInt32LE(offset + 16)
    const compressedSize = bytes.readUInt32LE(offset + 20)
    const uncompressedSize = bytes.readUInt32LE(offset + 24)
    const nameLength = bytes.readUInt16LE(offset + 28)
    const extraLength = bytes.readUInt16LE(offset + 30)
    const commentLength = bytes.readUInt16LE(offset + 32)
    const localOffset = bytes.readUInt32LE(offset + 42)
    if (compressedSize === 0xffffffff || uncompressedSize === 0xffffffff || localOffset === 0xffffffff) {
      throw new IpcError('unknown', '不支持 ZIP64 条目')
    }
    const end = offset + 46 + nameLength + extraLength + commentLength
    if (end > eocd) throw new IpcError('unknown', 'ZIP 中央目录条目越界')
    const name = bytes.subarray(offset + 46, offset + 46 + nameLength).toString('utf8')
    // Backups contain files, never directories or paths supplied by a caller.
    // Reject traversal and absolute names before they can enter the map.
    const segments = name.split('/')
    if (name === '' || name.startsWith('/') || name.includes('\\') || segments.some((part) => part === '' || part === '.' || part === '..')) {
      throw new IpcError('unknown', `ZIP 条目路径非法: ${name}`)
    }
    if (names.has(name)) throw new IpcError('unknown', `ZIP 条目重复: ${name}`)
    names.add(name)
    // We can decode UTF-8 (our writer sets the flag); accepting legacy names
    // here would make two byte sequences map to the same logical entry.
    if ((flags & 0x1) !== 0 || (flags & ~0x0808) !== 0) {
      throw new IpcError('unknown', `ZIP 条目使用了不支持的特性: ${name}`)
    }
    entries.push({ name, flags, method, crc, compressedSize, uncompressedSize, localOffset })
    offset = end
  }
  if (offset !== eocd) throw new IpcError('unknown', 'ZIP 中央目录长度不匹配')

  // Validate local entries in physical order.  This also catches overlapping
  // offsets and bytes inserted between entries (except an optional data
  // descriptor, which is explicitly checked below).
  const physical = [...entries].sort((a, b) => a.localOffset - b.localOffset)
  let previousEnd = 0
  for (let physicalIndex = 0; physicalIndex < physical.length; physicalIndex++) {
    const entry = physical[physicalIndex]!
    const lo = entry.localOffset
    if (physicalIndex === 0 && lo !== 0) throw new IpcError('unknown', 'ZIP 本地条目前存在未声明数据')
    if (lo < previousEnd || lo + 30 > centralOffset || bytes.readUInt32LE(lo) !== 0x04034b50) {
      throw new IpcError('unknown', `ZIP 本地条目偏移无效: ${entry.name}`)
    }
    const localFlags = bytes.readUInt16LE(lo + 6)
    const localMethod = bytes.readUInt16LE(lo + 8)
    const localCrc = bytes.readUInt32LE(lo + 14)
    const localCompressed = bytes.readUInt32LE(lo + 18)
    const localUncompressed = bytes.readUInt32LE(lo + 22)
    const nameLength = bytes.readUInt16LE(lo + 26)
    const extraLength = bytes.readUInt16LE(lo + 28)
    const nameEnd = lo + 30 + nameLength
    const dataStart = nameEnd + extraLength
    if (dataStart > centralOffset || dataStart < lo || bytes.subarray(lo + 30, nameEnd).toString('utf8') !== entry.name) {
      throw new IpcError('unknown', `ZIP 本地条目损坏: ${entry.name}`)
    }
    if (localMethod !== entry.method || (localFlags & 0x1) !== 0 || (localFlags & ~0x0808) !== 0 || (localFlags & 0x0808) !== (entry.flags & 0x0808)) {
      throw new IpcError('unknown', `ZIP 本地/中央目录不一致: ${entry.name}`)
    }
    if ((localFlags & 0x8) === 0 && (localCrc !== entry.crc || localCompressed !== entry.compressedSize || localUncompressed !== entry.uncompressedSize)) {
      throw new IpcError('unknown', `ZIP 本地大小或校验和不一致: ${entry.name}`)
    }
    if (entry.compressedSize > centralOffset - dataStart) throw new IpcError('unknown', `ZIP 条目越界: ${entry.name}`)
    const raw = bytes.subarray(dataStart, dataStart + entry.compressedSize)
    let data: Buffer
    try {
      data = entry.method === 0 ? Buffer.from(raw) : entry.method === 8 ? inflateRawSync(raw) : (() => { throw new Error('unsupported compression') })()
    } catch {
      throw new IpcError('unknown', `备份归档条目解压失败: ${entry.name}`)
    }
    if (data.length !== entry.uncompressedSize || crc32(data) !== entry.crc) {
      throw new IpcError('unknown', `备份归档条目校验失败: ${entry.name}`)
    }
    out.set(entry.name, data)

    let end = dataStart + entry.compressedSize
    const next = physical[physicalIndex + 1]?.localOffset ?? centralOffset
    if ((localFlags & 0x8) !== 0) {
      // Optional data descriptor: signature + crc + two sizes, or the
      // signature-less 12-byte form.  The next local header/central directory
      // determines where it ends, so accept only 12 or 16 bytes.
      const gap = next - end
      if (gap !== 12 && gap !== 16) throw new IpcError('unknown', `ZIP 数据描述符无效: ${entry.name}`)
      const descriptor = bytes.subarray(end, next)
      const at = descriptor.readUInt32LE(0) === 0x08074b50 ? 4 : 0
      if (descriptor.length - at !== 12 || descriptor.readUInt32LE(at) !== entry.crc || descriptor.readUInt32LE(at + 4) !== entry.compressedSize || descriptor.readUInt32LE(at + 8) !== entry.uncompressedSize) {
        throw new IpcError('unknown', `ZIP 数据描述符校验失败: ${entry.name}`)
      }
      end = next
    } else if (next !== end) {
      // Our format never emits padding between local entries.  Rejecting a
      // gap prevents hidden bytes from being smuggled into an archive that
      // otherwise passes the central-directory checks.
      throw new IpcError('unknown', `ZIP 本地条目之间存在未声明数据: ${entry.name}`)
    }
    previousEnd = end
  }
  if (out.size !== entries.length) throw new IpcError('unknown', 'ZIP 条目数量不一致')
  return out
}

// ── credentials and JSON export ────────────────────────────────────────────

function credentialRefs(): string[] {
  const refs = new Set<string>()
  for (const p of store.listProviders()) refs.add(p.credentialRef)
  /*
    ★★ 账号密文是**另一族 ref**(`provider:<id>#<accountId>`,schema 第 24 条)。
    漏在这里的表现分两种,都很难查:导出包里多账号的登录态整个缺失(换台机器后
    只剩当前那一个),以及导入回滚时账号密文不在快照里 —— 一次失败的导入会把
    它们留在被改写之后的状态上。
  */
  for (const account of store.listProviderAccounts()) {
    refs.add(providerAccountCredentialRef(account.providerId, account.id))
  }
  for (const cfg of store.listMcpServers()) refs.add(mcpSecretRef(cfg.id, mcpSecretKind(cfg)))
  for (const c of store.listSearchProviders()) refs.add(searchSecretRef(c.id))
  refs.add(PROXY_PASSWORD_REF)
  return [...refs]
}

interface CredentialImportPlan {
  /** Source archive ref -> local ref. null means the config lost its merge. */
  destinations: Map<string, string | null>
  /** Local refs made obsolete by an imported configuration change. */
  removals: Set<string>
}

/**
 * Build a credential allow-list from the imported records only.
 *
 * An encrypted block must not gain permission to overwrite every credential
 * already installed on this machine. A secret follows its configuration only
 * when that record is new or wins the updatedAt comparison. Provider refs are
 * also remapped: an archive is never allowed to choose a local provider's
 * credential pointer.
 */
function credentialImportPlan(data: DataExport): CredentialImportPlan {
  const destinations = new Map<string, string | null>()
  const removals = new Set<string>()
  const add = (source: string, destination: string | null): void => {
    if (source === '') throw new IpcError('unknown', '导入文件包含空凭证引用')
    if (destinations.has(source) && destinations.get(source) !== destination) {
      throw new IpcError('unknown', '导入文件包含冲突的凭证引用')
    }
    destinations.set(source, destination)
  }

  const localProviders = store.listProviders()
  for (const incoming of data.providers) {
    const local = localProviders.find((item) => item.id === incoming.id)
    const decision = dataMergeDecision(local, incoming)
    add(
      incoming.credentialRef,
      decision === 'skip'
        ? null
        : (local?.credentialRef ?? providerCredentialRef(incoming.id))
    )
  }

  /*
    账号密文。**ref 一律在本地重新派生**,绝不接受导入文件里写的那个 ——
    和上面 provider 那条是同一条规矩(`credentialImportPlan` 的文件头英文注释:
    一个档案永远不许指定本地的凭证指针)。派生用的是
    `providerId + accountId`,两者都来自导入记录本身,所以这条映射是自洽的。

    ★ 账号的去留跟着**它所属的 provider** 走:provider 那条被判 `skip`
    (本地那份更新),它名下的账号密文也不该被覆盖 —— 否则会出现
    「provider 配置是本地的、登录态是档案里的」这种半新半旧的组合。
  */
  const incomingAccounts = data.providerAccounts ?? []
  for (const account of incomingAccounts) {
    const source = providerAccountCredentialRef(account.providerId, account.id)
    const provider = data.providers.find((item) => item.id === account.providerId)
    const local = localProviders.find((item) => item.id === account.providerId)
    const decision = provider === undefined ? 'skip' : dataMergeDecision(local, provider)
    add(source, decision === 'skip' ? null : source)
  }

  const localMcp = store.listMcpServers()
  for (const incoming of data.mcpServers) {
    const ref = mcpSecretRef(incoming.id, mcpSecretKind(incoming))
    const local = localMcp.find((item) => item.id === incoming.id)
    const decision = dataMergeDecision(local, incoming)
    add(ref, decision === 'skip' ? null : ref)
    if (
      decision !== 'skip' &&
      local !== undefined &&
      mcpSecretKind(local) !== mcpSecretKind(incoming)
    ) {
      removals.add(mcpSecretRef(local.id, mcpSecretKind(local)))
    }
  }

  const localSearch = repo.listStoredSearchProviders()
  for (const incoming of data.searchProviders) {
    const ref = searchSecretRef(incoming.id)
    const local = localSearch.find((item) => item.id === incoming.id)
    add(ref, dataMergeDecision(local, incoming) === 'skip' ? null : ref)
  }

  // Proxy settings are imported as one user-confirmed settings block and do
  // not have a per-record updatedAt, so their optional password follows it.
  add(PROXY_PASSWORD_REF, PROXY_PASSWORD_REF)
  return { destinations, removals }
}

function mapImportedCredentials(
  plan: CredentialImportPlan,
  sourceValues: Record<string, string>
): Record<string, string> {
  const { destinations } = plan
  const mapped = Object.create(null) as Record<string, string>
  const destinationSources = new Map<string, string>()
  for (const [source, value] of Object.entries(sourceValues)) {
    if (!destinations.has(source)) {
      throw new IpcError('unknown', '导入文件包含未知凭证引用')
    }
    const destination = destinations.get(source) ?? null
    if (destination === null) continue
    const previous = destinationSources.get(destination)
    if (previous !== undefined && previous !== source) {
      throw new IpcError('unknown', '导入文件中的多个凭证会写入同一本机配置')
    }
    destinationSources.set(destination, source)
    mapped[destination] = value
  }
  return mapped
}

function encryptCredentials(values: Record<string, string>, password: string): EncryptedCredentials {
  const salt = randomBytes(16)
  const nonce = randomBytes(12)
  const key = scryptSync(password, salt, 32)
  const cipher = createCipheriv('aes-256-gcm', key, nonce)
  const ciphertext = Buffer.concat([cipher.update(JSON.stringify(values), 'utf8'), cipher.final()])
  return {
    algorithm: 'scrypt-aes-256-gcm',
    salt: salt.toString('base64'),
    nonce: nonce.toString('base64'),
    tag: cipher.getAuthTag().toString('base64'),
    ciphertext: ciphertext.toString('base64')
  }
}

function decryptCredentials(block: EncryptedCredentials, password: string): Record<string, string> {
  try {
    if (block.algorithm !== 'scrypt-aes-256-gcm') throw new Error('unsupported algorithm')
    const salt = Buffer.from(block.salt, 'base64')
    const nonce = Buffer.from(block.nonce, 'base64')
    const tag = Buffer.from(block.tag, 'base64')
    const ciphertext = Buffer.from(block.ciphertext, 'base64')
    if (salt.length < 16 || salt.length > 64 || nonce.length !== 12 || tag.length !== 16 || ciphertext.length === 0) throw new Error('invalid encrypted block')
    const key = scryptSync(password, Buffer.from(block.salt, 'base64'), 32)
    const decipher = createDecipheriv('aes-256-gcm', key, nonce)
    decipher.setAuthTag(tag)
    const plain = Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString('utf8')
    const parsed: unknown = JSON.parse(plain)
    if (typeof parsed !== 'object' || parsed === null) throw new Error('credentials is not an object')
    const out = Object.create(null) as Record<string, string>
    for (const [k, v] of Object.entries(parsed as Record<string, unknown>)) {
      if (typeof k !== 'string' || typeof v !== 'string') throw new Error('credential value is not a string')
      out[k] = v
    }
    return out
  } catch {
    throw new IpcError('auth', '密码验证失败，未修改任何数据')
  }
}

async function createExport(includeEncryptedKeys: boolean, password?: string): Promise<DataExport> {
  const base = repo.exportDataSnapshot()
  /*
    ★ 账号**元数据**在这里补上(密文走下面那条凭证通道)。不在
    `repo.exportDataSnapshot` 里做,是为了不让 `db/repo.ts` 反向依赖
    `db/provider-accounts.ts` —— 那会是一个循环 import,见
    `ipc/provider-accounts-transfer.ts` 的文件头。
  */
  const providerAccounts = exportProviderAccounts()
  const data: DataExport = {
    ...base,
    ...(providerAccounts.length === 0 ? {} : { providerAccounts })
  }
  if (includeEncryptedKeys) {
    if (password === undefined || password.length < 8) throw new IpcError('auth', '加密导出密码至少需要 8 个字符')
    if (!getHost().secrets.available()) throw new IpcError('auth', '凭证加密存储不可用，无法导出加密密钥')
    const values: Record<string, string> = {}
    for (const ref of credentialRefs()) {
      const value = await getHost().secrets.get(ref)
      if (value !== null && value !== '') values[ref] = value
    }
    data.encryptedCredentials = encryptCredentials(values, password)
  }
  return data
}

function compareCounts(data: DataExport): { newCount: number; overwriteCount: number; skippedCount: number } {
  let newCount = 0
  let overwriteCount = 0
  let skippedCount = 0
  for (const w of data.workspaces) {
    const local = store.getWorkspace(w.id)
    const decision = dataMergeDecision(local, w)
    if (decision === 'new') newCount++
    else if (decision === 'overwrite') overwriteCount++
    else skippedCount++
  }
  for (const item of data.sessions) {
    const local = store.getSession(item.session.id)
    const decision = dataMergeDecision(local, item.session)
    if (decision === 'new') newCount++
    else if (decision === 'overwrite') overwriteCount++
    else skippedCount++
  }
  for (const p of data.providers) {
    const local = store.listProviders().find((x) => x.id === p.id)
    const decision = dataMergeDecision(local, p)
    if (decision === 'new') newCount++
    else if (decision === 'overwrite') overwriteCount++
    else skippedCount++
  }
  for (const a of data.aliases) {
    const local = store.listAliases().find((x) => x.providerId === a.providerId && x.alias === a.alias)
    const decision = dataMergeDecision(local, a)
    if (decision === 'new') newCount++
    else if (decision === 'overwrite') overwriteCount++
    else skippedCount++
  }
  for (const c of data.mcpServers) {
    const local = store.listMcpServers().find((x) => x.id === c.id)
    const decision = dataMergeDecision(local, c)
    if (decision === 'new') newCount++
    else if (decision === 'overwrite') overwriteCount++
    else skippedCount++
  }
  for (const c of data.searchProviders) {
    const local = repo.listStoredSearchProviders().find((x) => x.id === c.id)
    const decision = dataMergeDecision(local, c)
    if (decision === 'new') newCount++
    else if (decision === 'overwrite') overwriteCount++
    else skippedCount++
  }
  return { newCount, overwriteCount, skippedCount }
}

function parseExport(path: string): DataExport {
  if (!existsSync(path)) throw new IpcError('unknown', '导入文件不存在')
  const stat = statSync(path)
  if (stat.size > MAX_IMPORT_BYTES) throw new IpcError('unknown', '导入文件过大')
  let value: unknown
  try { value = JSON.parse(readFileSync(path, 'utf8')) } catch { throw new IpcError('unknown', '导入文件不是有效 JSON') }
  if (!isDataExport(value)) throw new IpcError('unknown', '导入文件格式或版本不受支持')
  if (value.version > DATA_EXPORT_VERSION) throw new IpcError('unknown', '导入文件版本高于当前应用')
  return value
}

interface CredentialRollbackState {
  /** The encrypted bytes are the source of truth for the Electron host. */
  blob: Uint8Array | undefined
  /** Plaintext is only retained briefly to repair simple/in-memory hosts. */
  plaintext: string | null
  plaintextReadable: boolean
}

/**
 * Capture the credentials an import is allowed to touch before opening the
 * SQLite transaction. Some injected/test hosts keep secrets in an in-memory map
 * rather than the credentials table, so a database rollback alone is insufficient.
 */
async function snapshotCredentialRollback(refs: readonly string[]): Promise<Map<string, CredentialRollbackState>> {
  const secrets = getHost().secrets
  const snapshot = new Map<string, CredentialRollbackState>()
  for (const ref of refs) {
    let plaintext: string | null = null
    let plaintextReadable = true
    try {
      plaintext = await secrets.get(ref)
    } catch {
      // A corrupt/foreign credential blob can be restored byte-for-byte even
      // when it cannot be decrypted. Keep that fact so rollback does not
      // accidentally delete a credential we could not inspect.
      plaintextReadable = false
    }
    snapshot.set(ref, {
      blob: repo.getCredential(ref),
      plaintext,
      plaintextReadable
    })
  }
  return snapshot
}

async function restoreCredentialRollback(snapshot: Map<string, CredentialRollbackState>): Promise<void> {
  if (snapshot.size === 0) return
  // Restore the encrypted database blobs first. This is what the production
  // Electron host reads, and it also makes the operation deterministic if a
  // custom host has no remove() hook.
  repo.tx(() => {
    for (const [ref, state] of snapshot) {
      if (state.blob === undefined) repo.removeCredential(ref)
      else repo.putCredential(ref, state.blob)
    }
  })

  const secrets = getHost().secrets
  for (const [ref, state] of snapshot) {
    try {
      if (state.plaintext !== null && state.plaintextReadable) {
        // Re-seeding the old plaintext repairs an in-memory/test host and is
        // harmless for Electron (it simply encrypts it again).
        await secrets.set(ref, state.plaintext)
      } else if (state.blob === undefined) {
        // If the snapshot had no encrypted blob, any credential written by the
        // failed import must be removed. This remains true even when reading
        // the pre-import value failed (for example, a corrupt NCK1 or legacy
        // safeStorage value): there was no database credential to preserve.
        // The optional hook is implemented by Electron and nodeHost. Older
        // injected hosts can still rely on the database restoration above.
        await secrets.remove?.(ref)
      }
    } catch (err) {
      // Rollback is best effort at this boundary. Keep the original import
      // error as the user-facing cause, but leave an auditable diagnostic.
      console.error('[storage] 凭证回滚失败:', ref, err)
    }
  }
}

// ── public storage handlers ────────────────────────────────────────────────

export function getStats(): StorageStats {
  return repo.storageStats(dataDirectory(), attachmentDirectory(), readBackupStatus().lastBackupAt)
}

/**
 * UI 的 `storage:getStats` 走这条。
 *
 * ★ 只有一件差别:附件目录的字节数用异步、有界的遍历,目录很大时让出事件
 *   循环;库/WAL/会话/消息口径与 `getStats` 逐字相同。同步的 `getStats` 保留
 *   给 `vacuum` 之类已经在同步路径里的调用点。
 */
export function getStatsAsync(): Promise<StorageStats> {
  return repo.getStatsAsync(dataDirectory(), attachmentDirectory(), readBackupStatus().lastBackupAt)
}

export async function openDataDirectory(): Promise<void> {
  const path = dataDirectory()
  const message = await shell.openPath(path)
  if (message) throw new IpcError('unknown', `打开数据目录失败: ${message}`)
}

export function vacuum(): StorageStats {
  vacuumDatabase()
  return getStats()
}

export async function exportData(req: { includeEncryptedKeys?: boolean; password?: string }): Promise<{ path: string; encrypted: boolean; bytes: number } | null> {
  const result = await dialog.showSaveDialog({
    title: '导出 NextCoWork 数据',
    defaultPath: join(dataDirectory(), `nextcowork-data-${new Date().toISOString().slice(0, 10)}.json`),
    filters: [{ name: 'JSON', extensions: ['json'] }]
  })
  if (result.canceled || !result.filePath) return null
  const data = await createExport(req.includeEncryptedKeys === true, req.password)
  const bytes = jsonBytes(data)
  atomicWrite(result.filePath, bytes)
  return { path: result.filePath, encrypted: data.encryptedCredentials !== undefined, bytes: bytes.length }
}

export async function importPreview(ownerId = 0): Promise<ImportPreview | null> {
  // Starting a new chooser invalidates this window's previous confirmation.
  pendingImports.delete(ownerId)
  const result = await dialog.showOpenDialog({ title: '导入 NextCoWork 数据', properties: ['openFile'], filters: [{ name: 'JSON', extensions: ['json'] }] })
  if (result.canceled || !result.filePaths[0]) return null
  const path = result.filePaths[0]
  const data = parseExport(path)
  const counts = compareCounts(data)
  pendingImports.set(ownerId, data)
  return {
    path,
    version: data.version,
    workspaceCount: data.workspaces.length,
    sessionCount: data.sessions.length,
    messageCount: data.sessions.reduce((n, s) => n + s.messages.length, 0),
    providerCount: data.providers.length,
    aliasCount: data.aliases.length,
    mcpServerCount: data.mcpServers.length,
    skippedCount: counts.skippedCount,
    overwriteCount: counts.overwriteCount,
    newCount: counts.newCount,
    hasEncryptedCredentials: data.encryptedCredentials !== undefined
  }
}

export async function importApply(req: { password?: string }, ownerId = 0): Promise<ImportApplyResult> {
  const data = pendingImports.get(ownerId)
  if (data === undefined) throw new IpcError('unknown', '没有待确认的导入预览')
  let sourceCredentials: Record<string, string> = {}
  if (data.encryptedCredentials !== undefined && typeof data.encryptedCredentials !== 'boolean') {
    if (req.password === undefined) throw new IpcError('auth', '该导出包含加密密钥，需要输入密码')
    // 先验证密码，再开始事务；无效密码不会修改任何记录。
    sourceCredentials = decryptCredentials(data.encryptedCredentials, req.password)
  }
  const applied = await txAsync(async () => {
    const credentialPlan = credentialImportPlan(data)
    const credentialValues = mapImportedCredentials(credentialPlan, sourceCredentials)
    const credentialRollback = await snapshotCredentialRollback([
      ...Object.keys(credentialValues), ...credentialPlan.removals
    ])
    try {
      const merged = repo.mergeDataExport(data)
      // 账号依赖已经写好的 provider，且与配置和数据库凭证一起回滚。
      mergeProviderAccounts(data)
      const secrets = getHost().secrets
      for (const ref of credentialPlan.removals) {
        if (secrets.remove !== undefined) await secrets.remove(ref)
        else repo.removeCredential(ref)
      }
      for (const [ref, value] of Object.entries(credentialValues)) {
        await secrets.set(ref, value)
      }
      return { result: merged, settings: store.getSettings(), workspaces: store.listWorkspaces() }
    } catch (error) {
      // 宿主内存凭证也在同一所有者内恢复；随后 txAsync 回滚 SQL 再释放队列。
      // 不能恢复整库旧快照：它会抹掉已经独立提交的其它事务。
      await restoreCredentialRollback(credentialRollback)
      throw error
    }
  })
  pendingImports.delete(ownerId)
  windows.emitToAll('settings:changed', applied.settings)
  windows.emitToAll('workspace:changed', { workspaces: applied.workspaces })
  windows.emitToAll('sessions:changed', { kind: 'reset' })
  return applied.result
}

export async function chooseBackupDirectory(): Promise<string | null> {
  const result = await dialog.showOpenDialog({ title: '选择备份目录', properties: ['openDirectory', 'createDirectory'] })
  if (result.canceled || !result.filePaths[0]) return null
  const path = resolve(result.filePaths[0])
  if (isDangerousBackupPath(path, dataDirectory(), databaseFilePath())) {
    throw new IpcError('unknown', '备份目录不能位于 NextCoWork 数据目录内或包含数据目录')
  }
  try {
    mkdirSync(path, { recursive: true })
    if (lstatSync(path).isSymbolicLink()) throw new Error('备份目录不能是符号链接')
    // Re-check after creation: a parent symlink can make a lexical path look
    // external while its real target is inside the managed tree.
    if (isDangerousBackupPath(path, dataDirectory(), databaseFilePath())) throw new Error('备份目录与数据目录重叠')
    const probe = join(path, `.nextcowork-write-test-${process.pid}`)
    writeFileSync(probe, '')
    unlinkSync(probe)
  } catch (err) {
    if (err instanceof IpcError) throw err
    throw new IpcError('unknown', '备份目录不存在、不可写或与数据目录重叠')
  }
  store.updateSettings({ data: { backupDirectory: path } })
  windows.emitToAll('settings:changed', store.getSettings())
  return path
}

export function getBackupStatus(): BackupStatus {
  return toBackupStatus()
}

function ensureBackupDirectory(): string {
  const path = store.getSettings().data.backupDirectory
  if (!path) throw new IpcError('unknown', '请先选择备份目录')
  if (!isAbsolute(path)) throw new IpcError('unknown', '备份目录路径无效')
  const normalized = normalizeLocalBackupDirectory(path)
  if (normalized === null) throw new IpcError('unknown', '备份目录路径无效或与 NextCoWork 数据目录重叠')
  try {
    mkdirSync(normalized, { recursive: true })
    const lst = lstatSync(normalized)
    if (lst.isSymbolicLink()) throw new Error('备份目录不能是符号链接')
    if (isDangerousBackupPath(normalized, dataDirectory(), databaseFilePath())) throw new Error('备份目录与数据目录重叠')
    const st = statSync(normalized)
    if (!st.isDirectory()) throw new Error('不是目录')
    const probe = join(normalized, `.nextcowork-write-test-${process.pid}-${Date.now()}`)
    writeFileSync(probe, '')
    unlinkSync(probe)
  } catch {
    throw new IpcError('unknown', '备份目录不存在或不可写')
  }
  return normalized
}

/**
 * 造一份备份。
 *
 * ## 主进程在这里只做「接线」
 *
 * 整库读进内存、整库散列、把归档拼成一个 Buffer —— 这三件按库大小线性的重活
 * 全部交给 `db/backup-worker.ts` 的 worker 线程。它们的产物要么直接落盘
 * (归档)、要么是一个小结果(散列、字节数、计数),没有一样需要经过主进程的堆。
 *
 * ## 清单数字必须和归档里的那份库同源
 *
 * 顺序是:**先**用 SQLite 的异步备份 API 把当前库复制成一个私有快照,**再**从
 * 那份快照里读行数、settings 行、`user_version`。反过来先读活库、再复制的话,
 * `await` 期间账户切换或新消息写入就会让清单计数和库内容对不上 —— 而恢复端的
 * `validateBackupDatabase` 是拿裸 `COUNT(*)` 和 `settings.json` 逐项对账的,
 * 表现是**每一个新建的备份都恢复不了**。
 *
 * ★ 清单里**不再**枚举全库会话(那要走一遍 `getHistory`,按会话数乘出去):
 *   计数就是数据库的 `COUNT(*)`,和 `validateBackupDatabase` 的口径逐字一致。
 */
export async function createBackup(req: { manual?: boolean } = { manual: true }): Promise<BackupStatus> {
  if (backupRunning) return toBackupStatus()
  backupRunning = true
  let tempDir: string | null = null
  try {
    assertNoImportJob('备份')
    const dir = ensureBackupDirectory()
    const dbPath = databaseFilePath()
    if (dbPath === null) throw new IpcError('unknown', '备份需要文件数据库')
    // 在线 backup 会包含 WAL；不要在主线程同步 checkpoint 整库。
    if (!existsSync(dbPath)) throw new IpcError('unknown', '数据库文件不可用')
    const credentialKeyPath = join(dataDirectory(), CREDENTIAL_KEY_FILENAME)
    let credentialKey: Buffer | null = null
    if (existsSync(credentialKeyPath)) {
      const keyStat = lstatSync(credentialKeyPath)
      if (!keyStat.isFile() || keyStat.isSymbolicLink()) {
        throw new IpcError('unknown', '凭证主密钥路径不是普通文件，无法创建可恢复的备份')
      }
      chmodSync(credentialKeyPath, 0o600)
      credentialKey = readFileSync(credentialKeyPath)
    }
    if (credentialKey !== null && credentialKey.length !== 32) {
      throw new IpcError('unknown', '凭证主密钥文件损坏，无法创建可恢复的备份')
    }

    // 私有临时目录:快照只在对用户可见的目标路径上出现,失败时整棵删掉。
    tempDir = mkdtempSync(join(tmpdir(), 'nextcowork-backup-'))
    const snapshotPath = join(tempDir, 'database.sqlite')
    const snapshot = await runBackupSnapshot({ sourcePath: dbPath, snapshotPath })

    if (snapshot.hasProgramCredentials && credentialKey === null) {
      throw new IpcError('unknown', '凭证主密钥文件缺失，无法创建可恢复的备份')
    }
    /*
      从**快照里**读设置,不读活库的 `store.getSettings()`:恢复端拿归档里的
      settings.json 和 database.sqlite 里那一行对账,两份必须来自同一个时刻。
      缺行时按默认值,和 `validateBackupDatabase` 的兜底一致。
    */
    const settings = snapshot.settingsJson === ''
      ? jsonBytes(DEFAULT_SETTINGS)
      : Buffer.from(snapshot.settingsJson, 'utf8')
    const manifest: BackupManifest = {
      format: 'nextcowork-backup',
      formatVersion: BACKUP_FORMAT_VERSION,
      appVersion: app.getVersion(),
      createdAt: Date.now(),
      // 用快照自己的 user_version,不是当前连接的:校验端读的是归档里这个文件头。
      schemaVersion: snapshot.schemaVersion,
      databaseSha256: snapshot.databaseSha256,
      sessionCount: snapshot.sessionCount,
      messageCount: snapshot.messageCount,
      // 数据库快照含**全部账户作用域**的密文,这里也必须按整表计数。
      encryptedCredentials: snapshot.encryptedCredentials,
      ...(credentialKey === null ? {} : { credentialKeySha256: sha256(credentialKey) })
    }
    /*
      全局 `settings.json`（目前只有 hooks）。

      ★ zip 里的条目名是 `global-settings.json` 而不是磁盘上那个 `settings.json`：
      这个包里**已经**有一个叫 `settings.json` 的条目，装的是 AppSettings 的 dump
      （恢复时还要和 database.sqlite 对账）。两者同名会让读包的人分不清，
      而改已有那个条目名就得动 `BACKUP_FORMAT_VERSION`。

      ★ 文件不存在是常态（用户一条钩子都没配过）—— 那就不放进包里，
      恢复端也按「没有钩子」处理。
    */
    const globalSettings = readGlobalSettingsBytes()
    const baseName = req.manual === true ? `nextcowork-${new Date().toISOString().replaceAll(':', '-')}` : 'nextcowork-auto'
    let name = `${baseName}${BACKUP_EXT}`
    if (req.manual === true) {
      let n = 1
      while (existsSync(join(dir, name))) name = `${baseName}-${String(n++)}${BACKUP_EXT}`
    }
    const target = join(dir, name)
    // worker 在 target 同目录写 `.tmp` 再 rename —— 跨文件系统 rename 会 EXDEV,
    // 而同一目录内一定是原子的。归档因此从不出现在目标名字上的一半状态。
    await runBackupArchive({
      snapshotPath,
      archivePath: target,
      entries: [
        { name: 'manifest.json', data: jsonBytes(manifest) },
        { name: 'database.sqlite', fromPath: snapshotPath },
        { name: 'settings.json', data: settings },
        ...(credentialKey === null ? [] : [{ name: CREDENTIAL_KEY_FILENAME, data: credentialKey }]),
        ...(globalSettings === null ? [] : [{ name: 'global-settings.json', data: globalSettings }])
      ],
      expected: { sessionCount: snapshot.sessionCount, messageCount: snapshot.messageCount }
    })
    if (req.manual !== true) {
      // 自动备份只管理自己的固定文件；手动备份永不被清理。
      for (const entry of readdirSync(dir)) {
        if (AUTO_BACKUP_RE.test(entry) && entry !== name) { try { unlinkSync(join(dir, entry)) } catch { /* 保留失败文件 */ } }
      }
    }
    writeBackupStatus({ lastBackupAt: manifest.createdAt, lastBackupPath: target, lastError: null })
    return toBackupStatus()
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    const old = readBackupStatus()
    writeBackupStatus({ ...old, lastError: message })
    throw err
  } finally {
    if (tempDir !== null) {
      try { rmSync(tempDir, { recursive: true, force: true }) } catch { /* 临时快照删不掉不影响结果 */ }
    }
    backupRunning = false
  }
}

function stableJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`
  if (typeof value === 'object' && value !== null) {
    const record = value as Record<string, unknown>
    return `{${Object.keys(record).sort().map((key) => `${JSON.stringify(key)}:${stableJson(record[key])}`).join(',')}}`
  }
  return JSON.stringify(value)
}

function normalizedSettings(value: unknown): unknown {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return structuredClone(DEFAULT_SETTINGS)
  }
  return mergeSettings(DEFAULT_SETTINGS, value as Parameters<typeof mergeSettings>[1])
}

/**
 * 全局 `<appData>/settings.json`（目前只有 hooks）的备份 / 恢复。
 *
 * ★ 它和数据库是两份独立的存储，但对用户来说恢复是**一件事**：只还原一半的话，
 *   会话回来了而钩子没了，界面上还没有任何地方会提这件事。
 */
function globalSettingsFilePath(): string {
  return join(dataDirectory(), GLOBAL_SETTINGS_FILENAME)
}

/** 不存在返回 null —— 用户一条钩子都没配过是常态，不是错误。 */
function readGlobalSettingsBytes(): Buffer | null {
  const path = globalSettingsFilePath()
  try {
    return existsSync(path) ? readFileSync(path) : null
  } catch {
    return null
  }
}

/**
 * ★ 包里没有这个条目时**不动**磁盘上现有的那份：备份里没有（旧版本的包、
 *   或者当时确实没配过）不等于用户想删掉它。
 */
function restoreGlobalSettings(data: Buffer | null): void {
  if (data === null) return
  try {
    atomicWrite(globalSettingsFilePath(), data)
    clearGlobalSettingsCache(dataDirectory())
  } catch (err) {
    // 钩子没还原成功不该让整次恢复回滚 —— 数据库才是主体。
    getHost().logger.warn(`[backup] 全局设置恢复失败：${err instanceof Error ? err.message : String(err)}`)
  }
}

function parseBackup(path: string): {
  manifest: BackupManifest
  database: Buffer
  settings: unknown
  globalSettings: Buffer | null
  credentialKey: Buffer | null
} {
  if (!isAbsolute(path) || !existsSync(path)) throw new IpcError('unknown', '备份文件不存在')
  try {
    if (statSync(path).size > MAX_BACKUP_BYTES) throw new IpcError('unknown', '备份文件过大')
  } catch (err) {
    if (err instanceof IpcError) throw err
    throw new IpcError('unknown', '无法读取备份文件')
  }
  const entries = readZip(readFileSync(path))
  const manifestRaw = entries.get('manifest.json')
  const dbRaw = entries.get('database.sqlite')
  const settingsRaw = entries.get('settings.json')
  if (!manifestRaw || !dbRaw || !settingsRaw) throw new IpcError('unknown', '备份缺少必要文件')
  let manifest: unknown
  try { manifest = JSON.parse(manifestRaw.toString('utf8')) } catch { throw new IpcError('unknown', '备份 manifest 损坏') }
  if (typeof manifest !== 'object' || manifest === null) throw new IpcError('unknown', '备份 manifest 无效')
  const m = manifest as Partial<BackupManifest>
  const latestSchema = MIGRATIONS.reduce((max, migration) => Math.max(max, migration.version), 0)
  if (
    m.format !== 'nextcowork-backup' ||
    (m.formatVersion !== 1 && m.formatVersion !== BACKUP_FORMAT_VERSION) ||
    typeof m.appVersion !== 'string' ||
    m.appVersion.length === 0 ||
    m.appVersion.length > 256 ||
    typeof m.createdAt !== 'number' ||
    !Number.isFinite(m.createdAt) ||
    m.createdAt < 0 ||
    typeof m.databaseSha256 !== 'string' ||
    !/^[a-f0-9]{64}$/.test(m.databaseSha256) ||
    typeof m.schemaVersion !== 'number' ||
    !Number.isInteger(m.schemaVersion) ||
    m.schemaVersion < MIN_BACKUP_SCHEMA_VERSION ||
    m.schemaVersion > latestSchema ||
    typeof m.sessionCount !== 'number' ||
    !Number.isInteger(m.sessionCount) ||
    m.sessionCount < 0 ||
    typeof m.messageCount !== 'number' ||
    !Number.isInteger(m.messageCount) ||
    m.messageCount < 0 ||
    typeof m.encryptedCredentials !== 'boolean' ||
    (m.credentialKeySha256 !== undefined &&
      (typeof m.credentialKeySha256 !== 'string' || !/^[a-f0-9]{64}$/.test(m.credentialKeySha256)))
  ) throw new IpcError('unknown', '备份格式或数据库版本不受支持')
  if (sha256(dbRaw) !== m.databaseSha256) throw new IpcError('unknown', '备份数据库校验和不匹配')
  // SQLite header stores user_version as a big-endian uint32 at bytes 60..63.
  if (dbRaw.length < 64 || dbRaw.toString('ascii', 0, 15) !== 'SQLite format 3') throw new IpcError('unknown', '备份数据库文件无效')
  const schemaVersion = dbRaw.readUInt32BE(60)
  if (schemaVersion !== m.schemaVersion) throw new IpcError('unknown', '备份数据库版本与 manifest 不一致')
  let settings: unknown
  try { settings = JSON.parse(settingsRaw.toString('utf8')) } catch { throw new IpcError('unknown', '备份设置损坏') }
  if (typeof settings !== 'object' || settings === null || Array.isArray(settings)) {
    throw new IpcError('unknown', '备份设置结构无效')
  }
  try {
    // This also accepts older settings files that predate `data`; the shared
    // merger supplies the current defaults while rejecting non-object input.
    mergeSettings(DEFAULT_SETTINGS, settings as Parameters<typeof mergeSettings>[1])
  } catch {
    throw new IpcError('unknown', '备份设置结构无效')
  }
  const validation = validateBackupDatabase(dbRaw, m, settings)
  const credentialKey = entries.get(CREDENTIAL_KEY_FILENAME) ?? null
  if (m.formatVersion === BACKUP_FORMAT_VERSION) {
    if (credentialKey !== null && credentialKey.length !== 32) {
      throw new IpcError('unknown', '备份凭证主密钥长度无效')
    }
    if (validation.hasProgramCredentials && credentialKey === null) {
      throw new IpcError('unknown', '备份包含程序加密凭证但缺少主密钥')
    }
    if (validation.hasProgramCredentials && m.credentialKeySha256 === undefined) {
      throw new IpcError('unknown', '备份包含程序加密凭证但缺少主密钥校验值')
    }
    if (
      m.credentialKeySha256 !== undefined &&
      (credentialKey === null || sha256(credentialKey) !== m.credentialKeySha256)
    ) {
      throw new IpcError('unknown', '备份凭证主密钥校验失败')
    }
  } else if (validation.hasProgramCredentials) {
    // v1 备份从未携带主密钥,因此其中不可能合法出现 NCK1 密文。
    throw new IpcError('unknown', '旧版备份缺少程序加密凭证所需的主密钥')
  }
  // 旧版本的备份里没有这些条目 —— 缺席按「没有对应文件」处理,不是格式损坏。
  return {
    manifest: m as BackupManifest,
    database: dbRaw,
    settings,
    globalSettings: entries.get('global-settings.json') ?? null,
    credentialKey
  }
}

/**
 * Cross-check manifest claims against the SQLite payload itself.  A checksum
 * proves that the database was not changed after the manifest was written,
 * but it does not prove that the manifest's human-readable counts are true.
 * Deserialize into an isolated in-memory handle so no application state is
 * touched while previewing an untrusted archive.
 */
function validateBackupDatabase(
  raw: Buffer,
  manifest: Partial<BackupManifest>,
  expectedSettings: unknown
): { hasProgramCredentials: boolean } {
  let temp: DatabaseSync | null = null
  let validationDir: string | null = null
  try {
    // A database whose header is in WAL mode cannot reliably be deserialized
    // into `:memory:`: SQLite may still try to open a sibling -wal file and
    // fail with "unable to open database file". Validate an isolated copy in
    // a private temporary directory instead. It may create WAL/SHM siblings,
    // none of which can touch the live application database.
    validationDir = mkdtempSync(join(tmpdir(), 'nextcowork-backup-validate-'))
    const validationPath = join(validationDir, 'database.sqlite')
    writeFileSync(validationPath, raw, { flag: 'wx' })
    temp = new DatabaseSync(validationPath, { enableForeignKeyConstraints: true })
    const sessions = Number((temp.prepare('SELECT COUNT(*) AS n FROM sessions').get() as Record<string, unknown>)['n'] ?? -1)
    const messages = Number((temp.prepare('SELECT COUNT(*) AS n FROM messages').get() as Record<string, unknown>)['n'] ?? -1)
    const credentialRows = temp.prepare('SELECT blob FROM credentials').all() as Array<Record<string, unknown>>
    const credentials = credentialRows.length
    const hasProgramCredentials = credentialRows.some((row) => {
      const blob = row['blob']
      return blob instanceof Uint8Array && isProgramEncrypted(blob)
    })
    if (sessions !== manifest.sessionCount || messages !== manifest.messageCount) {
      throw new Error('manifest count mismatch')
    }
    if (
      (manifest.formatVersion === BACKUP_FORMAT_VERSION && (credentials > 0) !== manifest.encryptedCredentials) ||
      (manifest.formatVersion === 1 && manifest.encryptedCredentials === true && credentials === 0)
    ) {
      throw new Error('manifest credential flag mismatch')
    }
    const settingsRow = temp.prepare('SELECT json FROM settings WHERE id = 1').get() as Record<string, unknown> | undefined
    let databaseSettings: unknown = DEFAULT_SETTINGS
    if (settingsRow !== undefined) {
      try {
        databaseSettings = JSON.parse(String(settingsRow['json']))
      } catch {
        throw new Error('settings row is not valid JSON')
      }
    }
    if (stableJson(normalizedSettings(databaseSettings)) !== stableJson(normalizedSettings(expectedSettings))) {
      throw new Error('settings.json 与 database.sqlite 不一致')
    }
    return { hasProgramCredentials }
  } catch (err) {
    if (err instanceof IpcError) throw err
    throw new IpcError('unknown', `备份数据库内容校验失败: ${err instanceof Error ? err.message : String(err)}`)
  } finally {
    try { temp?.close() } catch { /* best effort */ }
    if (validationDir !== null) {
      try { rmSync(validationDir, { recursive: true, force: true }) } catch { /* best effort */ }
    }
  }
}

export async function restoreBackup(req: { confirm?: boolean }, ownerId = 0): Promise<RestoreResult | null> {
  if (req.confirm !== true) {
    // A canceled/replaced chooser must not leave an older file confirmable.
    pendingRestores.delete(ownerId)
    const result = await dialog.showOpenDialog({ title: '从备份恢复', properties: ['openFile'], filters: [{ name: 'NextCoWork 备份', extensions: ['ncwbackup'] }] })
    if (result.canceled || !result.filePaths[0]) return null
    const path = result.filePaths[0]
    const parsed = parseBackup(path)
    const preview: RestorePreview = {
      path,
      manifest: parsed.manifest,
      sessionCount: parsed.manifest.sessionCount,
      messageCount: parsed.manifest.messageCount,
      settingsIncluded: true
    }
    pendingRestores.set(ownerId, { path, preview })
    return { restored: false, preview }
  }
  if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new IpcError('unknown', '有运行中的 Agent 或历史操作，请等待完成后再恢复')
  assertNoImportJob('恢复')
  const target = pendingRestores.get(ownerId)
  if (target === undefined) throw new IpcError('unknown', '没有待确认的恢复预览')
  const parsed = parseBackup(target.path)
  const dbPath = databaseFilePath()
  if (dbPath === null) throw new IpcError('unknown', '当前数据库不是文件库')
  const safety = `${dbPath}.restore-safety-${Date.now()}`
  const keyPath = join(dirname(dbPath), CREDENTIAL_KEY_FILENAME)
  const previousCredentialKey = existsSync(keyPath) ? readFileSync(keyPath) : null
  const localBackupState = captureLocalBackupState()
  // 先停并等同步退出。否则它可能在关库后触发,或把恢复前账户的响应写进恢复后的库。
  await pauseConfigSyncForDatabaseReplacement()
  // 会话运行时缓存的草稿/队列属于旧库:丢掉而不是写回,恢复后的库按需重读
  sessionRuntime.reset()
  checkpointDatabase()
  copyFileSync(dbPath, safety)
  try {
    closeDatabase()
    atomicWrite(dbPath, parsed.database)
    if (parsed.credentialKey !== null) {
      atomicWrite(keyPath, parsed.credentialKey)
      chmodSync(keyPath, 0o600)
    }
    // 清掉旧 WAL/SHM，防止旧日志覆盖恢复后的文件。
    for (const suffix of ['-wal', '-shm']) { try { unlinkSync(`${dbPath}${suffix}`) } catch { /* 不存在 */ } }
    openDatabase(dirname(dbPath))
    // v1 包里没有 credential.key,里面若有旧 safeStorage 行就趁当前系统仍能解时升级。
    migrateLegacyCredentials()
    // 先恢复归档所属账户作用域,再把本机路径偏好写到正确那份设置里。
    resumeStoredAccountAfterDatabaseReplacement()
    // The archive may have been produced on another device. Restore the
    // current device's backup directory and sanitize its status immediately
    // after reopening the replacement database.
    restoreLocalBackupState(localBackupState)
    /*
      全局 `settings.json`（钩子）。★ 放在数据库换完之后、广播之前：它和数据库
      是两份独立的存储，但恢复必须是一件事 —— 只还原一半的话，用户拿回了会话
      却丢了钩子，而界面上没有任何地方会提。

      ★ 包里没有这个条目是**正常**的（旧版本的备份、或者用户一条钩子都没配过），
      这时**不动**磁盘上现有的那份：备份里没有不等于用户想删掉它。
    */
    restoreGlobalSettings(parsed.globalSettings)
    pendingRestores.delete(ownerId)
    windows.emitToAll('settings:changed', store.getSettings())
    windows.emitToAll('workspace:changed', { workspaces: store.listWorkspaces() })
    windows.emitToAll('sessions:changed', { kind: 'reset' })
    return { restored: true, backupStatus: toBackupStatus() }
  } catch (err) {
    try {
      closeDatabase()
      for (const suffix of ['-wal', '-shm']) { try { unlinkSync(`${dbPath}${suffix}`) } catch { /* 不存在 */ } }
      atomicWrite(dbPath, readFileSync(safety))
      if (previousCredentialKey === null) {
        try { unlinkSync(keyPath) } catch { /* 原来就不存在 */ }
      } else {
        atomicWrite(keyPath, previousCredentialKey)
        chmodSync(keyPath, 0o600)
      }
      for (const suffix of ['-wal', '-shm']) { try { unlinkSync(`${dbPath}${suffix}`) } catch { /* 不存在 */ } }
      openDatabase(dirname(dbPath))
      resumeStoredAccountAfterDatabaseReplacement()
      restoreLocalBackupState(localBackupState)
    } catch (rollbackError) {
      console.error('[storage] 恢复回滚失败', rollbackError)
    }
    throw err
  } finally {
    try { unlinkSync(safety) } catch { /* 安全快照只用于失败回滚 */ }
  }
}

function isManagedSessionAttachment(path: string): boolean {
  const root = resolve(attachmentDirectory())
  const target = resolve(path)
  if (target === root || !isWithin(root, target)) return false
  // Current non-session scopes have their own owners/cleanup rules. Everything
  // else inside attachments is either the current sessions subtree or a
  // pre-scope legacy session layout and is safe for session-orphan cleanup.
  /*
    ★ `config-profiles` 必须在排除名单里。账户作用域的主题库落在
    `attachments/config-profiles/<hash>/themes/…` —— 它是**用户的主题文件**,
    不属于会话,按会话孤儿清掉就是「换个账户登录,壁纸全没了」。
  */
  for (const scope of ['themes', 'exports', PROFILE_DIRECTORY_SEGMENT]) {
    if (isWithin(join(root, scope), target)) return false
  }
  return true
}

/** 删除受管理目录时保留用户选定的备份目录（无论它嵌套在哪一侧）。 */
function removeManagedPath(path: string, protectedPath: string | null): void {
  let stat
  try { stat = lstatSync(path) } catch (err) {
    if ((err as NodeJS.ErrnoException)?.code === 'ENOENT') return
    throw new IpcError('unknown', `读取 ${path} 失败: ${String(err)}`)
  }

  // A symlink is a leaf. Never pass it to a recursive remover and never use
  // statSync here: both can follow a link into an external directory.
  if (stat.isSymbolicLink()) {
    if (protectedPath !== null && isWithin(protectedPath, path)) return
    try { unlinkSync(path) } catch (err) {
      if ((err as NodeJS.ErrnoException)?.code !== 'ENOENT') throw new IpcError('unknown', `删除 ${path} 失败: ${String(err)}`)
    }
    return
  }

  if (protectedPath !== null && isWithin(protectedPath, path)) {
    // 备份目录位于待删目录之上/就是待删目录：整棵都不能动。
    return
  }
  if (protectedPath !== null && isWithin(path, protectedPath)) {
    // 备份目录是待删目录的子树：只清理其余兄弟，保留这条子树。
    try {
      if (!stat.isDirectory()) return
      for (const entry of readdirSync(path, { withFileTypes: true })) {
        removeManagedPath(join(path, entry.name), protectedPath)
      }
    } catch (err) {
      throw new IpcError('unknown', `删除 ${path} 失败: ${String(err)}`)
    }
    return
  }
  try {
    if (stat.isDirectory()) rmSync(path, { recursive: true, force: true })
    else unlinkSync(path)
  } catch (err) { throw new IpcError('unknown', `删除 ${path} 失败: ${String(err)}`) }
}

interface StagedManagedPath {
  original: string
  staged: string
}

/**
 * Move a managed path into a same-filesystem staging tree before deleting it.
 * Renames are atomic; if any later path cannot be staged, every prior rename
 * can be reversed without having partially erased the user's database.
 */
function stageManagedPath(
  path: string,
  root: string,
  stagingRoot: string,
  protectedPath: string | null,
  staged: StagedManagedPath[]
): void {
  let stat
  try { stat = lstatSync(path) } catch (err) {
    if ((err as NodeJS.ErrnoException)?.code === 'ENOENT') return
    throw new IpcError('unknown', `读取 ${path} 失败: ${String(err)}`)
  }

  if (protectedPath !== null && isWithin(protectedPath, path)) return
  if (
    protectedPath !== null &&
    isWithin(path, protectedPath) &&
    stat.isDirectory() &&
    !stat.isSymbolicLink()
  ) {
    for (const entry of readdirSync(path)) {
      stageManagedPath(join(path, entry), root, stagingRoot, protectedPath, staged)
    }
    return
  }

  const rel = relative(resolve(root), resolve(path))
  if (rel === '' || rel.startsWith('..') || isAbsolute(rel)) {
    throw new IpcError('unknown', '拒绝暂存数据目录边界之外的路径')
  }
  const destination = join(stagingRoot, rel)
  mkdirSync(dirname(destination), { recursive: true })
  try {
    renameSync(path, destination)
    staged.push({ original: path, staged: destination })
  } catch (err) {
    // ★ 原始 errno 必须挂在 cause 上传出去 —— `clearLocalData` 靠它区分
    // 「被 Chromium 占着、可以推迟」和「真的出了问题、必须回滚」。
    throw Object.assign(new IpcError('unknown', `暂存 ${path} 失败: ${String(err)}`), { cause: err })
  }
}

function rollbackStagedPaths(staged: readonly StagedManagedPath[]): void {
  for (const entry of [...staged].reverse()) {
    try {
      if (!existsSync(entry.staged)) continue
      mkdirSync(dirname(entry.original), { recursive: true })
      renameSync(entry.staged, entry.original)
    } catch (err) {
      console.error('[storage] 删除回滚失败:', entry.original, err)
    }
  }
}

function walkFiles(root: string): string[] {
  let rootStat
  try { rootStat = lstatSync(root) } catch (err) {
    if ((err as NodeJS.ErrnoException)?.code === 'ENOENT') return []
    return []
  }
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) return []
  const out: string[] = []
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name)
    // Dirent.isDirectory() is intentionally not enough for a hostile tree:
    // inspect the link itself and treat symlinks as leaves.
    let stat
    try { stat = lstatSync(path) } catch { continue }
    if (stat.isSymbolicLink()) out.push(path)
    else if (stat.isDirectory()) out.push(...walkFiles(path))
    else out.push(path)
  }
  return out
}

/**
 * Current session files live below `attachments/sessions`. Before attachment
 * scopes were introduced, files/directories could live directly below the
 * attachment root. Scan both layouts while explicitly excluding the theme and
 * export subtrees, whose authority is not the messages table.
 */
function reclaimableSessionFiles(): string[] {
  const root = attachmentDirectory()
  let names: string[]
  try { names = readdirSync(root) } catch { return [] }
  return names.flatMap((name) => {
    if (name === 'themes' || name === 'exports') return []
    const path = join(root, name)
    let stat
    try { stat = lstatSync(path) } catch { return [] }
    if (stat.isSymbolicLink() || !stat.isDirectory()) return [path]
    return walkFiles(path)
  })
}

/**
 * 一次扫描，得出三类可回收对象。preview 与实际清理**共用它** ——
 * 两边各写一遍判据的话，预览说要删 3 个、实际删了 5 个，而没有任何测试会红。
 *
 * ## 判据的演变
 *
 * 原判据是「磁盘上有、表里没有 → 删」。它在附件根下只有会话附件时是对的。
 * 加了 draft/committed 之后要补两类：
 *
 * - **`.tmp` 残片**：上传写到一半崩掉留下的。它们永远不该存在，无条件回收。
 * - **超期草稿**：用户传了图但一直没发送。它们**在表里**，所以旧判据永远
 *   碰不到它们 —— 那不是「被误删」，而是反过来：**永远不会被回收**，
 *   一张没发出去的图会在磁盘上留到卸载。
 *
 * ## 未超期的草稿绝不动
 *
 * 这是这个函数里最重要的一条。用户挑好图去倒杯水，回来时图必须还在。
 * 宽限期给到 7 天，宁可多占几 MB。
 */
interface ReclaimScan {
  /** 要删的文件（孤儿 + 残片 + 超期草稿） */
  files: string[]
  /** 要删的行（文件已不存在的 + 超期草稿的） */
  rowIds: string[]
  /** row 与文件的对应关系，用来避免把同一附件在统计中算两次。 */
  rowPaths: Map<string, string>
  bytes: number
}

function reclaimAttachmentCount(scan: ReclaimScan): number {
  const rowPaths = new Set([...scan.rowPaths.values()].map((path) => resolve(path)))
  const standaloneFiles = scan.files.filter((path) => !rowPaths.has(resolve(path))).length
  return scan.rowIds.length + standaloneFiles
}

function scanReclaimable(now: number): ReclaimScan {
  // V5 后附件根下还有 themes/exports 等其它 scope。候选扫描显式排除它们，
  // 同时覆盖 sessions/ 和升级前留在附件根下的旧会话目录。
  const referenced = new Set(
    repo.attachmentRows()
      .filter((row) => isManagedSessionAttachment(row.path))
      .map((row) => resolve(row.path))
  )

  const files: string[] = []
  let bytes = 0
  const add = (p: string): void => {
    files.push(p)
    try { bytes += lstatSync(p).size } catch { /* 统计不精确不影响回收 */ }
  }

  /*
    ★ themes/ 与 exports/ 绝不进入候选集。主题由 `theme.ts` 的 index.json
    管理、导出资产也不属于会话历史；把整个附件根不加区分地扫一遍会误删。
  */
  for (const path of reclaimableSessionFiles()) {
    // 上传中断留下的残片。以 `.` 开头 + `.tmp` 结尾，`ncw://` 也寻址不到它们
    if (basename(path).startsWith('.') && path.endsWith('.tmp')) { add(path); continue }
    // 真孤儿：磁盘上有、表里没有
    if (!referenced.has(resolve(path))) add(path)
  }

  const rowIds: string[] = []
  const rowPaths = new Map<string, string>()

  // 超期草稿：文件与行一起收
  for (const row of repo.listStaleDraftAttachments(now - DRAFT_ATTACHMENT_TTL_MS)) {
    if (row.scope !== 'session' || !isManagedSessionAttachment(row.path)) continue
    if (!files.includes(row.path)) {
      try { lstatSync(row.path); add(row.path) } catch { /* 文件已不存在 */ }
    }
    rowIds.push(row.id)
    rowPaths.set(row.id, row.path)
  }

  // 表里有、磁盘无 —— 只收行
  for (const row of repo.attachmentRows()) {
    // attachmentRows() 是兼容旧调用点的精简视图；路径边界仍由这里负责。
    if (row.scope !== 'session' || !isManagedSessionAttachment(row.path)) continue
    let present = true
    try { lstatSync(row.path) } catch { present = false }
    if (!present && !rowIds.includes(row.id)) {
      rowIds.push(row.id)
      rowPaths.set(row.id, row.path)
    }
  }

  return { files, rowIds, rowPaths, bytes }
}

interface PhysicalCleanup {
  deleted: number
  undeletable: string[]
}

/**
 * 数据库删除后回收已经没有任何引用的会话附件。
 *
 * 只接受 `attachments/sessions` 下的路径，并在删除前再次查询引用集合；
 * 因此外部绝对路径、主题/导出附件以及仍被其它会话引用的文件都会保留。
 */
function removeUnreferencedManagedFiles(paths: readonly string[]): PhysicalCleanup {
  const referenced = new Set(
    repo.attachmentRows()
      .filter((row) => isManagedSessionAttachment(row.path))
      .map((row) => resolve(row.path))
  )
  const undeletable: string[] = []
  let deleted = 0
  const seen = new Set<string>()
  for (const raw of paths) {
    if (!isManagedSessionAttachment(raw)) continue
    const path = resolve(raw)
    let stat
    try { stat = lstatSync(path) } catch { continue }
    if (seen.has(path) || referenced.has(path)) continue
    seen.add(path)
    try {
      // Unlink a regular file or the symlink itself; never recurse into an
      // unexpected directory and never follow a link to its target.
      if (!stat.isFile() && !stat.isSymbolicLink()) { undeletable.push(path); continue }
      unlinkSync(path)
      deleted++
    } catch {
      undeletable.push(path)
    }
  }
  return { deleted, undeletable: [...new Set(undeletable)] }
}

/**
 * Reclaim the physical files that belonged to a deleted session.  SQLite's
 * cascade removes the attachment rows, but it cannot remove files outside
 * the database.  Keep this helper in the storage boundary so session IPC and
 * the bulk cleanup paths share the same path and reference checks.
 */
export function removeSessionAttachmentFiles(paths: readonly string[]): PhysicalCleanup {
  return removeUnreferencedManagedFiles(paths)
}

function attachmentCleanupPreview(): CleanupPreview {
  const scan = scanReclaimable(Date.now())
  return {
    kind: 'attachments',
    sessionCount: 0,
    messageCount: 0,
    attachmentCount: reclaimAttachmentCount(scan),
    bytes: scan.bytes,
    undeletable: likelyUndeletable(scan.files)
  }
}

function likelyUndeletable(paths: readonly string[], sessionAttachmentsOnly = true): string[] {
  const result: string[] = []
  for (const raw of paths) {
    if (sessionAttachmentsOnly && !isManagedSessionAttachment(raw)) continue
    try { lstatSync(raw) } catch { continue }
    try {
      // 删除权限由父目录决定；这里只做预览，不把文件内容或路径交给 renderer。
      accessSync(dirname(raw), constants.W_OK)
    } catch {
      result.push(raw)
    }
  }
  return [...new Set(result)]
}

/** Actual bytes that the cleanup boundary is permitted to unlink. */
function managedAttachmentBytes(rows: readonly repo.AttachmentReferenceRow[]): number {
  const seen = new Set<string>()
  let bytes = 0
  for (const row of rows) {
    if (!isManagedSessionAttachment(row.path)) continue
    const path = resolve(row.path)
    if (seen.has(path)) continue
    seen.add(path)
    try {
      const stat = lstatSync(path)
      if (stat.isFile() || stat.isSymbolicLink()) bytes += stat.size
    } catch { /* missing files release no physical bytes */ }
  }
  return bytes
}

function withActualAttachmentBytes(
  preview: CleanupPreview,
  rows: readonly repo.AttachmentReferenceRow[]
): CleanupPreview {
  const recorded = rows.reduce((total, row) => total + Math.max(0, row.size), 0)
  return {
    ...preview,
    // Repository preview includes message JSON plus recorded attachment sizes.
    // Replace the latter with real, in-boundary files so missing or external
    // references are never advertised as disk space that will be freed.
    bytes: Math.max(0, preview.bytes - recorded) + managedAttachmentBytes(rows),
    undeletable: likelyUndeletable(rows.map((row) => row.path))
  }
}

function managedPathSize(path: string): number {
  let stat
  try { stat = lstatSync(path) } catch { return 0 }
  if (!stat.isDirectory() || stat.isSymbolicLink()) return stat.size
  let total = stat.size
  try {
    for (const entry of readdirSync(path)) total += managedPathSize(join(path, entry))
  } catch { /* a changing/unreadable subtree remains a best-effort estimate */ }
  return total
}

/**
 * Collapse a managed tree to the paths clearLocalData will actually remove.
 * The recursive case is only needed for a legacy protected backup symlink
 * inside the data tree; normal backup directories are required to be disjoint.
 */
function removableManagedEntries(path: string, protectedPath: string | null): string[] {
  let stat
  try { stat = lstatSync(path) } catch { return [] }
  if (stat.isSymbolicLink()) {
    return protectedPath !== null && isWithin(protectedPath, path) ? [] : [path]
  }
  if (protectedPath !== null && isWithin(protectedPath, path)) return []
  if (protectedPath !== null && isWithin(path, protectedPath) && stat.isDirectory()) {
    try {
      return readdirSync(path).flatMap((name) => removableManagedEntries(join(path, name), protectedPath))
    } catch {
      return [path]
    }
  }
  return [path]
}

function localDataPreview(): CleanupPreview {
  const base = repo.cleanupPreview('local-data')
  const protectedPath = externalBackupPath(store.getSettings().data.backupDirectory)
  const targets = managedLocalPaths(profileRootDirectory(), dataDirectory()).flatMap((path) =>
    removableManagedEntries(path, protectedPath)
  )
  const dbPath = databaseFilePath()
  if (dbPath !== null) {
    for (const path of [dbPath, `${dbPath}-wal`, `${dbPath}-shm`]) {
      targets.push(...removableManagedEntries(path, protectedPath))
    }
  }
  const unique = [...new Set(targets.map((path) => resolve(path)))]
  return {
    ...base,
    bytes: unique.reduce((total, path) => total + managedPathSize(path), 0),
    undeletable: likelyUndeletable(unique, false)
  }
}

export function cleanupPreview(req: { kind: 'attachments' | 'age' | 'history' | 'local-data'; age?: CleanupAge }): CleanupPreview {
  if (req.kind === 'attachments') return attachmentCleanupPreview()
  if (req.kind === 'age') {
    const cutoff = cutoffForAge(Date.now(), req.age ?? 3)
    const ids = repo.sessionIdsBefore(cutoff)
    const rows = repo.attachmentRowsForSessions(ids)
    return withActualAttachmentBytes(
      repo.cleanupPreview('age', cutoff),
      rows
    )
  }
  if (req.kind === 'local-data') return localDataPreview()
  const preview = repo.cleanupPreview(req.kind)
  if (req.kind === 'history') {
    return withActualAttachmentBytes(preview, repo.allSessionAttachmentRows())
  }
  return preview
}

export function cleanupAttachments(): CleanupResult {
  if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new IpcError('unknown', '有运行中的 Agent 或历史操作，请等待完成后再清理')
  assertNoImportJob('清理')

  // ★ 预览与执行走同一次扫描，不是两次 —— 两次之间文件可能变化，
  //   而用户看到的数字必须就是实际发生的事。
  const scan = scanReclaimable(Date.now())
  const undeletable: string[] = []
  const failedPaths = new Set<string>()
  const rowPaths = new Set([...scan.rowPaths.values()].map((path) => resolve(path)))
  let deleted = 0

  for (const path of scan.files) {
    try {
      unlinkSync(path)
      // 文件同时有待删数据库行时，它们合起来仍是一条附件。
      if (!rowPaths.has(resolve(path))) deleted++
    } catch {
      failedPaths.add(resolve(path))
      undeletable.push(path)
    }
  }
  for (const id of scan.rowIds) {
    const path = scan.rowPaths.get(id)
    // 文件删不掉时保留行，下一次清理仍有完整信息可重试。
    if (path !== undefined && failedPaths.has(resolve(path))) continue
    repo.removeAttachmentRow(id)
    deleted++
  }

  return {
    kind: 'attachments',
    sessionCount: 0,
    messageCount: 0,
    attachmentCount: reclaimAttachmentCount(scan),
    bytes: scan.bytes,
    undeletable: [...new Set(undeletable)],
    deleted
  }
}

export function cleanupByAge(req: { age: CleanupAge }): CleanupResult {
  if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new IpcError('unknown', '有运行中的 Agent 或历史操作，请等待完成后再清理')
  assertNoImportJob('清理')
  const cutoff = cutoffForAge(Date.now(), req.age)
  const ids = repo.sessionIdsBefore(cutoff)
  const rows = repo.attachmentRowsForSessions(ids)
  const preview = withActualAttachmentBytes(repo.cleanupPreview('age', cutoff), rows)
  const paths = rows.map((row) => row.path)
  const result = repo.deleteByAge(cutoff)
  const physical = removeUnreferencedManagedFiles(paths)
  result.bytes = preview.bytes
  result.undeletable = physical.undeletable
  if (result.sessionCount > 0) windows.emitToAll('sessions:changed', { kind: 'reset' })
  return result
}

export function clearHistory(): CleanupResult {
  if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new IpcError('unknown', '有运行中的 Agent 或历史操作，请等待完成后再清理')
  assertNoImportJob('清理')
  const rows = repo.allSessionAttachmentRows()
  const preview = withActualAttachmentBytes(repo.cleanupPreview('history'), rows)
  const paths = rows.map((row) => row.path)
  const result = repo.deleteAllHistory()
  const physical = removeUnreferencedManagedFiles(paths)
  result.bytes = preview.bytes
  result.undeletable = physical.undeletable
  windows.emitToAll('sessions:changed', { kind: 'reset' })
  return result
}

/** 删除本应用管理的目录/文件；不会递归删除数据根目录本身。 */
export function clearLocalData(req: { confirm: boolean }): { deleted: boolean } {
  if (!req.confirm) throw new IpcError('unknown', '必须明确确认删除本机数据')
  if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new IpcError('unknown', '有运行中的 Agent 或历史操作，请等待完成后再删除')
  assertNoImportJob('删除')
  const dataRoot = dataDirectory()
  /*
    ★ 删除边界是 **profile 根**,不是数据根。清单横跨两层(`data/` 下是我们的库和
    文件树,根层是 Chromium 的 profile),而 `stageManagedPath` 的边界断言只认一个
    根 —— 取父目录,两边才同时在界内。定在数据根上的话,第一条 Chromium 路径就会
    让整次删除抛错回滚,而不是少删一点。
  */
  const profileRoot = profileRootDirectory()
  const configuredBackup = store.getSettings().data.backupDirectory
  // A malformed legacy setting must not accidentally protect a path relative
  // to the process working directory.  Only a user-selected absolute path is
  // an external backup location worth preserving.
  const externalBackup = externalBackupPath(configuredBackup)
  const dbPath = databaseFilePath()
  const stagingRoot = join(profileRoot, `.delete-safety-${process.pid}-${Date.now()}`)
  const staged: StagedManagedPath[] = []
  /**
   * 被 Chromium 占住、这一轮搬不动的 profile 路径。它们**从未被 rename**,
   * 所以不进 `staged`,也就不参与回滚 —— 原地没动过的东西没什么可还原的。
   */
  const deferred: string[] = []
  let databaseClosed = false
  try {
    // Move ordinary files first, then close and move SQLite as the commit
    // point. Nothing is physically erased until every target is staged.
    for (const path of managedLocalPaths(profileRoot, dataRoot)) {
      /*
        ★ 受保护的备份目录若嵌在这棵子树里就不能推迟 —— 补删是在下一次启动、
        在一个不知道备份设置的早期时机做的 `rmSync`,推迟等于把那份备份也判了死刑。
        这种(合法遗留的软链在 profile 目录里)情形只会出现在极端配置下,让它照旧
        抛错回滚即可。
      */
      const deferrable =
        isDeferrableLocalPath(path) && (externalBackup === null || !isWithin(path, externalBackup))
      if (!deferrable) {
        stageManagedPath(path, profileRoot, stagingRoot, externalBackup, staged)
        continue
      }
      /*
        Chromium 的 profile 目录在主进程活着时句柄一直开着,Windows 上 rename 必 EPERM。
        让这一条把整次删除拖垮是不可接受的(那正是这个功能在 Windows 上从来没成功过的
        原因),改为记账、下次启动补删 —— 见 ./pending-delete.ts。
        ★ 只吞「被占用」这一类错误;其它错误照旧抛出并整体回滚。
      */
      try {
        stageManagedPath(path, profileRoot, stagingRoot, externalBackup, staged)
      } catch (err) {
        if (!isLockedError(err)) throw err
        deferred.push(path)
      }
    }
    if (dbPath !== null) {
      checkpointDatabase()
      closeDatabase()
      databaseClosed = true
      for (const path of [dbPath, `${dbPath}-wal`, `${dbPath}-shm`]) {
        stageManagedPath(path, profileRoot, stagingRoot, externalBackup, staged)
      }
    }
    // The safety tree exists only long enough to make staging reversible.
    // It is inside the managed data root and never includes an external backup
    // target or the shared Claude CLI directory.
    removeManagedPath(stagingRoot, null)
  } catch (err) {
    rollbackStagedPaths(staged)
    try { removeManagedPath(stagingRoot, null) } catch { /* rollback diagnostics were already logged */ }
    if (databaseClosed && dbPath !== null) {
      try { openDatabase(dirname(dbPath)) } catch (openError) {
        console.error('[storage] 删除失败后重新打开数据库失败:', openError)
      }
    }
    throw err
  }
  if (deferred.length > 0) {
    // 记账失败只记日志:删除本身已经提交了,不能因为写不下一张清单就把它报成失败。
    try {
      recordPendingDelete(profileRoot, deferred)
    } catch (err) {
      console.error('[storage] 写入补删清单失败，以下路径将残留:', deferred, err)
    }
  }
  // 保留 Claude CLI 共享目录和外部备份目录；应用退出后下次启动会重建数据库。
  app.quit()
  return { deleted: true }
}

/** 启动时按到期补做一次自动备份，不使用 React timer。 */
export function scheduleAutomaticBackup(): void {
  const settings = store.getSettings()
  if (settings.data.backupFrequency === 'manual' || !settings.data.backupDirectory) return
  const last = readBackupStatus().lastBackupAt ?? 0
  const interval = settings.data.backupFrequency === 'daily' ? 86_400_000 : 7 * 86_400_000
  if (Date.now() - last < interval) return
  setImmediate(() => { void createBackup({ manual: false }).catch((err) => console.warn('[storage] 自动备份失败', err)) })
}
