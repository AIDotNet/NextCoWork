/**
 * 这份数据是不是已经被 NextCoWork 3.0 接管了。**在打开数据库之前**判断(启动时,建窗之前)。
 *
 * ## 为什么 2.x 必须认这件事
 *
 * 3.0 第一次启动时原地接管 2.x 的数据根:备份 → 在 `service_ownership` 里写所有权标记 → 跑 3.0 自己的
 * 迁移(版本号接着 2.x 的序列,38 起)。2.x 的迁移执行器只跑自己认识的版本、**跳过**它不认识的,
 * 所以一个旧版 2.x 照样会打开这份库、照样往里写 —— 两个版本按各自的语义写同一份数据,结果是坏数据。
 * 2.9 起,2.x 看到下面两种情况之一就不再打开这份数据(见 `main/index.ts` 的启动检查):
 *
 * 1. `migrations` 里有比 2.x 已知的最新迁移更新的版本 → 被更新的版本迁移过了(3.0 接管完就是这样)。
 * 2. 有所有权标记,但它不属于 2.x 自己的独立设备服务 → 3.0 写了标记、还没来得及跑迁移(接管中途)。
 *    2.x 的设备服务也会写这张表,但它写的 `device_id` 就是同一个数据根里 `device-identity.json` 的 id;
 *    3.0 的设备身份在另一个文件(`device.json`)里,两者对不上。设备服务接管的数据,2.x 桌面端的行为不变。
 *
 * ★ 任何读不出来的情况(没有库、库坏了、被占用)都返回 null:那不是「被 3.0 接管」,交给原来的启动路径处理
 *   (迁移闸门的错误页会说清楚)。这里只在**确定**是新版本的数据时才拦。
 *
 * 本文件零 Electron。
 */
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { DB_FILENAME } from './index'
import { MIGRATIONS } from './schema'

export type NewerOwner =
  | { reason: 'newer-schema'; newestMigration: number }
  | { reason: 'taken-over'; ownerDeviceId: string }

/** 2.x 认识的最新迁移版本。 */
export const LAST_V2_MIGRATION = Math.max(...MIGRATIONS.map((m) => m.version))

/** 2.x 独立设备服务写在数据根里的身份文件(`agent/server/root.ts`)。 */
const SERVICE_IDENTITY_FILE = 'device-identity.json'

function serviceDeviceId(dataRoot: string): string | null {
  try {
    const json = JSON.parse(readFileSync(join(dataRoot, SERVICE_IDENTITY_FILE), 'utf8')) as { deviceId?: unknown }
    return typeof json.deviceId === 'string' ? json.deviceId : null
  } catch {
    return null
  }
}

function tableExists(db: DatabaseSync, name: string): boolean {
  return db.prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?").get(name) !== undefined
}

export function detectNewerOwner(dataRoot: string, lastKnownMigration: number = LAST_V2_MIGRATION): NewerOwner | null {
  const path = join(dataRoot, DB_FILENAME)
  if (!existsSync(path)) return null
  let db: DatabaseSync | undefined
  try {
    // 只读打开:这一步绝不能改动一份可能已经属于 3.0 的库(也不建 WAL / shm 之外的任何东西)
    db = new DatabaseSync(path, { readOnly: true })
    if (tableExists(db, 'migrations')) {
      const row = db.prepare('SELECT MAX(version) AS v FROM migrations').get() as { v?: number | bigint | null } | undefined
      const newest = row?.v === null || row?.v === undefined ? 0 : Number(row.v)
      if (newest > lastKnownMigration) return { reason: 'newer-schema', newestMigration: newest }
    }
    if (tableExists(db, 'service_ownership')) {
      const owner = db.prepare('SELECT device_id FROM service_ownership WHERE id = 1').get() as { device_id?: unknown } | undefined
      if (owner !== undefined && typeof owner.device_id === 'string' && owner.device_id !== serviceDeviceId(dataRoot)) {
        return { reason: 'taken-over', ownerDeviceId: owner.device_id }
      }
    }
    return null
  } catch {
    return null
  } finally {
    try { db?.close() } catch { /* 已经关了 */ }
  }
}
