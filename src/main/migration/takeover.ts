/**
 * 原地接管:把一份桌面数据根交给独立设备服务,**不复制、不上传**,并且可验证、可重入、可回退。
 *
 * ## 阶段(journal 逐步落盘,崩溃后重跑从记录的阶段继续)
 *
 *   begin   : 检查旧进程 → `VACUUM INTO` 一致性备份 + 复制凭据主密钥/供应商文件 → 验证备份
 *             journal: backed-up → verified
 *   commit  : 取得同根锁(创建服务身份)→ 在库里追加 ownership 标记 → journal: committed
 *   rollback: 提交前:恢复备份,什么都没丢;提交后:必须显式确认「接管之后写入的数据不在备份里」,
 *             并先把当前库另存一份,再恢复。journal: rolled-back
 *
 * ## 不变量
 * - 旧桌面进程仍在运行(Chromium 的 SingletonLock 指向活进程)或已有服务持锁 → **拒绝**,不抢。
 * - 只**追加**:迁移期间不改动任何旧表;凭据主密钥**原样保留**,绝不新造 key 覆盖旧密文。
 * - 备份必须通过完整性检查和逐表行数核对,否则不进入下一阶段(并清理这份坏备份)。
 * - 提交后不允许旧版直接共用这份数据(旧版不认识新锁):回退是「停服 + 恢复备份」,不是「再开旧版」。
 *
 * 本文件零 Electron;所有 SQL 在 `db/takeover.ts`。
 */
import { randomUUID } from 'node:crypto'
import {
  closeSync, copyFileSync, existsSync, fsyncSync, lstatSync, mkdirSync, openSync, readFileSync, readlinkSync, renameSync, rmSync, writeSync
} from 'node:fs'
import { join } from 'node:path'
import { acquireServiceRoot } from '../../agent/server/root'
import { DeviceProtocolError } from '../../shared/device/errors'
import { closeDatabase, openDatabase } from '../db'
import { backupDatabaseFile, readServiceOwnership, recordServiceOwnership, verifyDatabaseBackup } from '../db/takeover'

export type TakeoverPhase = 'backed-up' | 'verified' | 'committed' | 'rolled-back'

export interface TakeoverJournal {
  v: 1
  id: string
  phase: TakeoverPhase
  startedAt: number
  /** 备份目录(相对数据根)。 */
  backupDir: string
  /** 一并备份的旁路文件名(相对数据根)。 */
  sidecars: string[]
  deviceId?: string
  committedAt?: number
  rolledBackAt?: number
  /** 提交后回退时,回退之前那份库的另存位置(相对数据根)。 */
  postTakeoverCopy?: string
}

export interface TakeoverOptions {
  now?: () => number
  isAlive?: (pid: number) => boolean
}

const DB_FILE = 'nextcowork.db'
const JOURNAL_FILE = 'takeover.journal.json'
const BACKUP_ROOT = 'takeover-backups'
/** 与数据库同目录、丢了就解不开旧密文的文件:必须一并备份,回退时一并恢复。 */
const SIDECAR_CANDIDATES = ['credential.key', 'providers.json']

const processAlive = (pid: number): boolean => {
  try { process.kill(pid, 0); return true } catch (error) { return (error as NodeJS.ErrnoException).code !== 'ESRCH' }
}

function writeJson(path: string, value: unknown): void {
  const temp = `${path}.${randomUUID().slice(0, 8)}.tmp`
  const fd = openSync(temp, 'wx', 0o600)
  try { writeSync(fd, JSON.stringify(value, null, 2)); fsyncSync(fd) } finally { closeSync(fd) }
  renameSync(temp, path)
}

export function readJournal(dataDir: string): TakeoverJournal | null {
  const path = join(dataDir, JOURNAL_FILE)
  if (!existsSync(path)) return null
  let parsed: unknown
  try { parsed = JSON.parse(readFileSync(path, 'utf8')) } catch { throw new DeviceProtocolError('internal', 'Takeover journal is corrupt; refusing to continue') }
  const journal = parsed as Partial<TakeoverJournal>
  if (journal.v !== 1 || typeof journal.id !== 'string' || typeof journal.backupDir !== 'string' || typeof journal.startedAt !== 'number'
    || !['backed-up', 'verified', 'committed', 'rolled-back'].includes(String(journal.phase)) || !Array.isArray(journal.sidecars)) {
    throw new DeviceProtocolError('internal', 'Takeover journal is corrupt; refusing to continue')
  }
  return journal as TakeoverJournal
}
const saveJournal = (dataDir: string, journal: TakeoverJournal): void => writeJson(join(dataDir, JOURNAL_FILE), journal)

/** 旧桌面(Chromium SingletonLock → `host-pid`)或另一个服务(service.lock)还活着就拒绝。 */
export function assertNoLiveOwner(dataDir: string, isAlive: (pid: number) => boolean): void {
  const lock = join(dataDir, 'SingletonLock')
  try {
    if (lstatSync(lock).isSymbolicLink()) {
      const pid = Number(readlinkSync(lock).split('-').at(-1))
      if (Number.isInteger(pid) && pid > 0 && isAlive(pid)) throw new DeviceProtocolError('conflict', 'The desktop app is still running on this data root')
    }
  } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw new DeviceProtocolError('conflict', 'Cannot determine whether the desktop app is running')
  }
  const serviceLock = join(dataDir, 'service.lock')
  if (existsSync(serviceLock)) {
    let pid = 0
    try { pid = Number((JSON.parse(readFileSync(serviceLock, 'utf8')) as { pid?: unknown }).pid) } catch { /* 读不懂按「忙」处理。 */ }
    if (!Number.isInteger(pid) || pid <= 0 || isAlive(pid)) throw new DeviceProtocolError('conflict', 'A service is running on this data root')
  }
}

function copyOnce(from: string, to: string): void {
  copyFileSync(from, to)
}

/** 阶段 1:检查 + 备份 + 验证。已有未完成的 journal 时从它继续(重入)。 */
export function beginTakeover(dataDir: string, options: TakeoverOptions = {}): TakeoverJournal {
  const now = options.now ?? Date.now
  const isAlive = options.isAlive ?? processAlive
  const dbPath = join(dataDir, DB_FILE)
  if (!existsSync(dbPath)) throw new DeviceProtocolError('not_found', 'No desktop database was found in this data root')
  assertNoLiveOwner(dataDir, isAlive)

  const existing = readJournal(dataDir)
  if (existing?.phase === 'committed') throw new DeviceProtocolError('conflict', 'This data root has already been taken over')
  if (existing?.phase === 'verified') {
    // 重入:备份必须仍然有效,否则当作没做过。
    const backup = join(dataDir, existing.backupDir, DB_FILE)
    if (existsSync(backup) && verifyDatabaseBackup(dbPath, backup) === null) return existing
  }

  const id = randomUUID()
  const backupDir = join(BACKUP_ROOT, id)
  const absolute = join(dataDir, backupDir)
  mkdirSync(absolute, { recursive: true, mode: 0o700 })
  const sidecars = SIDECAR_CANDIDATES.filter((name) => existsSync(join(dataDir, name)))
  const journal: TakeoverJournal = { v: 1, id, phase: 'backed-up', startedAt: now(), backupDir, sidecars }
  try {
    backupDatabaseFile(dbPath, join(absolute, DB_FILE))
    for (const name of sidecars) copyOnce(join(dataDir, name), join(absolute, name))
    saveJournal(dataDir, journal)
    const problem = verifyDatabaseBackup(dbPath, join(absolute, DB_FILE))
    if (problem !== null) throw new DeviceProtocolError('internal', problem)
  } catch (error) {
    // 坏备份不留着冒充好备份;journal 退回上一个有效状态(没有则删除)。
    rmSync(absolute, { recursive: true, force: true })
    if (existing === null || existing.phase === 'rolled-back') rmSync(join(dataDir, JOURNAL_FILE), { force: true })
    else saveJournal(dataDir, existing)
    throw error
  }
  const verified: TakeoverJournal = { ...journal, phase: 'verified' }
  saveJournal(dataDir, verified)
  return verified
}

/** 阶段 2:创建服务身份、追加 ownership 标记。幂等:已提交返回原 journal。 */
export function commitTakeover(dataDir: string, options: TakeoverOptions = {}): TakeoverJournal {
  const now = options.now ?? Date.now
  const journal = readJournal(dataDir)
  if (journal === null || journal.phase === 'rolled-back') throw new DeviceProtocolError('conflict', 'No verified takeover is pending')
  if (journal.phase === 'committed') return journal
  if (journal.phase !== 'verified') throw new DeviceProtocolError('conflict', 'The backup has not been verified yet')

  const root = acquireServiceRoot(dataDir, { now, ...(options.isAlive !== undefined && { isAlive: options.isAlive }) })
  try {
    openDatabase(dataDir, { durability: 'full' })
    try {
      const owned = readServiceOwnership()
      if (owned === null) {
        recordServiceOwnership({ deviceId: root.identity.deviceId, journalId: journal.id, backupRef: journal.backupDir, takenOverAt: now() })
      } else if (owned.journalId !== journal.id) {
        throw new DeviceProtocolError('conflict', 'This data root is owned by a different takeover')
      }
    } finally {
      closeDatabase()
    }
  } finally {
    root.release()
  }
  const committed: TakeoverJournal = { ...journal, phase: 'committed', deviceId: root.identity.deviceId, committedAt: now() }
  saveJournal(dataDir, committed)
  return committed
}

/**
 * 回退。提交前:恢复备份。提交后:必须 `acknowledgeDataLoss`,因为接管之后写入的数据不在备份里 ——
 * 当前库会先另存为 `post-takeover.db`,不是直接覆盖销毁。
 */
export function rollbackTakeover(dataDir: string, options: TakeoverOptions & { acknowledgeDataLoss?: boolean } = {}): TakeoverJournal {
  const now = options.now ?? Date.now
  const isAlive = options.isAlive ?? processAlive
  const journal = readJournal(dataDir)
  if (journal === null || journal.phase === 'rolled-back') throw new DeviceProtocolError('not_found', 'There is no takeover to roll back')
  if (journal.phase === 'committed' && options.acknowledgeDataLoss !== true) {
    throw new DeviceProtocolError('forbidden', 'Data written after the takeover is not in the backup; confirm the loss explicitly to roll back')
  }
  assertNoLiveOwner(dataDir, isAlive)
  const backup = join(dataDir, journal.backupDir, DB_FILE)
  if (!existsSync(backup)) throw new DeviceProtocolError('not_found', 'The pre-takeover backup is missing')
  // 回退前再验一次备份本身的完整性(这里没有源库可比对行数,只看完整性)。
  if (verifyDatabaseBackup(backup, backup) !== null) throw new DeviceProtocolError('internal', 'The pre-takeover backup failed its integrity check')

  let postTakeoverCopy: string | undefined
  const dbPath = join(dataDir, DB_FILE)
  if (journal.phase === 'committed' && existsSync(dbPath)) {
    postTakeoverCopy = join(journal.backupDir, 'post-takeover.db')
    backupDatabaseFile(dbPath, join(dataDir, postTakeoverCopy))
  }
  // 原子替换:先写同目录临时文件再 rename;陈旧的 WAL/SHM 属于被替换掉的库,必须一并清掉。
  const temp = `${dbPath}.restore`
  copyFileSync(backup, temp)
  renameSync(temp, dbPath)
  rmSync(`${dbPath}-wal`, { force: true })
  rmSync(`${dbPath}-shm`, { force: true })
  for (const name of journal.sidecars) copyFileSync(join(dataDir, journal.backupDir, name), join(dataDir, name))
  if (journal.phase === 'committed') {
    // 接管创建的服务身份与证书只对接管后的数据有意义。
    rmSync(join(dataDir, 'device-identity.json'), { force: true })
    rmSync(join(dataDir, 'pki'), { recursive: true, force: true })
  }
  const next: TakeoverJournal = { ...journal, phase: 'rolled-back', rolledBackAt: now(), ...(postTakeoverCopy !== undefined && { postTakeoverCopy }) }
  saveJournal(dataDir, next)
  return next
}
