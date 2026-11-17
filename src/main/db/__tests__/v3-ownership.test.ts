import { existsSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DB_FILENAME } from '../index'
import { LAST_V2_MIGRATION, detectNewerOwner } from '../v3-ownership'

let root: string
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'nextcowork-v3own-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

/** 一份最小的库:migrations 表(到 `newest` 为止)+ 可选的所有权标记。 */
function makeDb(newest: number, owner?: string): void {
  const db = new DatabaseSync(join(root, DB_FILENAME))
  db.exec('CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)')
  const insert = db.prepare('INSERT INTO migrations VALUES (?, ?, ?)')
  for (let v = 1; v <= newest; v++) insert.run(v, `m${v}`, 0)
  db.exec('CREATE TABLE service_ownership (id INTEGER PRIMARY KEY CHECK (id = 1), device_id TEXT NOT NULL, journal_id TEXT NOT NULL, backup_ref TEXT NOT NULL, taken_over_at INTEGER NOT NULL)')
  if (owner !== undefined) db.prepare('INSERT INTO service_ownership VALUES (1, ?, ?, ?, ?)').run(owner, 'j1', 'backups/x', 1)
  db.close()
}

describe('detectNewerOwner', () => {
  it('knows the last 2.x migration (3.0 continues the sequence from the next number)', () => {
    expect(LAST_V2_MIGRATION).toBe(37)
  })

  it('leaves ordinary 2.x data alone, including a fresh profile with no database yet', () => {
    expect(detectNewerOwner(root)).toBeNull()
    makeDb(LAST_V2_MIGRATION)
    expect(detectNewerOwner(root)).toBeNull()
  })

  it('blocks data that a newer version has migrated (3.0 after its takeover)', () => {
    makeDb(39, 'v3-device')
    expect(detectNewerOwner(root)).toEqual({ reason: 'newer-schema', newestMigration: 39 })
  })

  it('blocks data 3.0 has marked as owned but not migrated yet (takeover interrupted)', () => {
    makeDb(LAST_V2_MIGRATION, 'v3-device')
    writeFileSync(join(root, 'device.json'), JSON.stringify({ deviceId: 'v3-device' }))
    expect(detectNewerOwner(root)).toEqual({ reason: 'taken-over', ownerDeviceId: 'v3-device' })
  })

  it('does not change anything for data handed to the 2.x standalone device service', () => {
    makeDb(LAST_V2_MIGRATION, 'service-device')
    writeFileSync(join(root, 'device-identity.json'), JSON.stringify({ deviceId: 'service-device' }))
    expect(detectNewerOwner(root)).toBeNull()
  })

  it('never blocks on a database it cannot read (that is the normal startup path\'s problem)', () => {
    writeFileSync(join(root, DB_FILENAME), 'not a sqlite file')
    expect(detectNewerOwner(root)).toBeNull()
  })

  it('opens the database read-only and leaves no journal files behind', () => {
    makeDb(39)
    const before = readdirSync(root).sort()
    detectNewerOwner(root)
    expect(readdirSync(root).sort()).toEqual(before)
    expect(existsSync(join(root, `${DB_FILENAME}-journal`))).toBe(false)
  })
})
