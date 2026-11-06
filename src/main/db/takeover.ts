/**
 * 原地接管所需的数据库操作。所有 SQL 留在 `db/` 里;编排(journal、阶段、文件)在
 * `main/migration/takeover.ts`。
 *
 * - **备份**用 `VACUUM INTO`:SQLite 在一个读事务里产出一份一致的快照,WAL 里未检查点的页也包含在内,
 *   不是「复制文件」那种可能撕裂的拷贝。
 * - **验证**不只看能打开:`PRAGMA integrity_check` 必须是 `ok`,且关键表的行数与源库逐一相等
 *   (备份期间源库若在写入会不相等,这时应当失败重来,而不是接受一份对不上的备份)。
 */
import { DatabaseSync } from 'node:sqlite'
import { DeviceProtocolError } from '../../shared/device/errors'
import { requireDeviceDurability } from './device-events'
import { stmt, tx } from './index'

/** 备份验证时逐表核对行数的表(存在才核对)。 */
const COUNTED_TABLES = ['sessions', 'messages', 'workspaces', 'kv', 'credentials'] as const

export function backupDatabaseFile(sourcePath: string, destinationPath: string): void {
  const source = new DatabaseSync(sourcePath, { readOnly: true })
  try {
    source.prepare('VACUUM INTO ?').run(destinationPath)
  } finally {
    source.close()
  }
}

function tableCounts(database: DatabaseSync): Record<string, number> {
  const counts: Record<string, number> = {}
  for (const table of COUNTED_TABLES) {
    const exists = database.prepare("SELECT 1 AS present FROM sqlite_master WHERE type='table' AND name=?").get(table)
    if (exists === undefined) continue
    counts[table] = Number(database.prepare(`SELECT COUNT(*) AS n FROM ${table}`).get()?.n)
  }
  return counts
}

/** 返回 null = 验证通过;否则是失败原因(不含路径/SQL 细节)。 */
export function verifyDatabaseBackup(sourcePath: string, backupPath: string): string | null {
  let backup: DatabaseSync | null = null
  let source: DatabaseSync | null = null
  try {
    backup = new DatabaseSync(backupPath, { readOnly: true })
    const check = backup.prepare('PRAGMA integrity_check').all().map((row) => String(Object.values(row)[0]))
    if (check.length !== 1 || check[0] !== 'ok') return 'backup failed its integrity check'
    source = new DatabaseSync(sourcePath, { readOnly: true })
    const before = tableCounts(source)
    const after = tableCounts(backup)
    for (const [table, count] of Object.entries(before)) {
      if (after[table] !== count) return `backup row count differs for ${table}`
    }
    return null
  } catch {
    return 'backup could not be opened'
  } finally {
    backup?.close()
    source?.close()
  }
}

export interface ServiceOwnership {
  deviceId: string
  journalId: string
  backupRef: string
  takenOverAt: number
}

/** 记录接管(单行,已有则拒绝覆盖 —— 重复接管必须先显式回退)。 */
export function recordServiceOwnership(info: ServiceOwnership): void {
  requireDeviceDurability()
  tx(() => {
    if (stmt('SELECT 1 AS present FROM service_ownership WHERE id = 1').get() !== undefined) {
      throw new DeviceProtocolError('conflict', 'This data root is already owned by a service')
    }
    stmt('INSERT INTO service_ownership (id, device_id, journal_id, backup_ref, taken_over_at) VALUES (1,?,?,?,?)')
      .run(info.deviceId, info.journalId, info.backupRef, info.takenOverAt)
  })
}

export function readServiceOwnership(): ServiceOwnership | null {
  const row = stmt('SELECT device_id, journal_id, backup_ref, taken_over_at FROM service_ownership WHERE id = 1').get()
  if (row === undefined) return null
  return { deviceId: String(row.device_id), journalId: String(row.journal_id), backupRef: String(row.backup_ref), takenOverAt: Number(row.taken_over_at) }
}
