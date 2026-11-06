import { existsSync, mkdtempSync, readFileSync, rmSync, symlinkSync, truncateSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { closeDatabase, openDatabase, stmt } from '../../db'
import { backupDatabaseFile, readServiceOwnership, verifyDatabaseBackup } from '../../db/takeover'
import { beginTakeover, commitTakeover, readJournal, rollbackTakeover } from '../takeover'

let dir = ''
const dead = (): boolean => false
const alive = (): boolean => true

function seed(rows: string[]): void {
  closeDatabase()
  openDatabase(dir)
  for (const key of rows) stmt('INSERT OR REPLACE INTO kv (key, json) VALUES (?, ?)').run(key, JSON.stringify({ key }))
  closeDatabase()
}
function keys(): string[] {
  closeDatabase()
  openDatabase(dir)
  const found = stmt('SELECT key FROM kv ORDER BY key').all().map((row) => String(row.key))
  closeDatabase()
  return found
}

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'ncw-takeover-'))
  seed(['a', 'b', 'c'])
  writeFileSync(join(dir, 'credential.key'), 'MASTER-KEY-BYTES', { mode: 0o600 })
})
afterEach(() => { closeDatabase(); rmSync(dir, { recursive: true, force: true }) })

describe('in-place takeover', () => {
  it('makes a verified consistent backup and leaves the source untouched', () => {
    const journal = beginTakeover(dir, { isAlive: dead })
    expect(journal.phase).toBe('verified')
    expect(journal.sidecars).toEqual(['credential.key'])
    expect(readFileSync(join(dir, journal.backupDir, 'credential.key'), 'utf8')).toBe('MASTER-KEY-BYTES')
    expect(readFileSync(join(dir, 'credential.key'), 'utf8')).toBe('MASTER-KEY-BYTES') // 主密钥原样保留
    expect(verifyDatabaseBackup(join(dir, 'nextcowork.db'), join(dir, journal.backupDir, 'nextcowork.db'))).toBeNull()
    expect(keys()).toEqual(['a', 'b', 'c'])
    expect(readJournal(dir)?.id).toBe(journal.id)
  })

  it('refuses while the desktop app or another service is alive, but not for stale locks', () => {
    symlinkSync(`host-${process.pid}`, join(dir, 'SingletonLock'))
    expect(() => beginTakeover(dir, { isAlive: alive })).toThrowError(expect.objectContaining({ code: 'conflict' }))
    expect(existsSync(join(dir, 'takeover-backups'))).toBe(false) // 拒绝时什么都没做
    expect(() => beginTakeover(dir, { isAlive: dead })).not.toThrow() // 陈旧锁(进程已不在)不挡路
    rmSync(join(dir, 'SingletonLock'))
    writeFileSync(join(dir, 'service.lock'), JSON.stringify({ pid: 4242, token: 'x'.repeat(16), startedAt: 1 }))
    expect(() => beginTakeover(dir, { isAlive: alive })).toThrowError(expect.objectContaining({ code: 'conflict' }))
    writeFileSync(join(dir, 'service.lock'), '{ unreadable')
    expect(() => beginTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('rejects a data root without a desktop database', () => {
    rmSync(join(dir, 'nextcowork.db'))
    expect(() => beginTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'not_found' }))
  })

  it('is re-entrant: a valid verified backup is reused, a missing one is redone', () => {
    const first = beginTakeover(dir, { isAlive: dead })
    expect(beginTakeover(dir, { isAlive: dead }).id).toBe(first.id)
    rmSync(join(dir, first.backupDir), { recursive: true })
    const redone = beginTakeover(dir, { isAlive: dead })
    expect(redone.id).not.toBe(first.id)
    expect(existsSync(join(dir, redone.backupDir, 'nextcowork.db'))).toBe(true)
  })

  it('commits by creating the service identity and an append-only ownership mark, idempotently', () => {
    beginTakeover(dir, { isAlive: dead })
    const committed = commitTakeover(dir, { isAlive: dead })
    expect(committed.phase).toBe('committed')
    expect(existsSync(join(dir, 'device-identity.json'))).toBe(true)
    expect(existsSync(join(dir, 'service.lock'))).toBe(false) // 锁已释放,服务随后可以启动
    expect(commitTakeover(dir, { isAlive: dead }).id).toBe(committed.id)
    openDatabase(dir)
    expect(readServiceOwnership()).toMatchObject({ deviceId: committed.deviceId, journalId: committed.id, backupRef: committed.backupDir })
    closeDatabase()
    expect(keys()).toEqual(['a', 'b', 'c']) // 旧数据一行不少
    expect(() => beginTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('refuses to commit without a verified backup', () => {
    expect(() => commitTakeover(dir)).toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('rolls back before commit by restoring the backup, including sidecars and dropping stale WAL files', () => {
    const journal = beginTakeover(dir, { isAlive: dead })
    seed(['a', 'b', 'c', 'written-after-backup'])
    writeFileSync(join(dir, 'credential.key'), 'CHANGED')
    writeFileSync(join(dir, 'nextcowork.db-wal'), 'stale')
    const rolled = rollbackTakeover(dir, { isAlive: dead })
    expect(rolled.phase).toBe('rolled-back')
    expect(keys()).toEqual(['a', 'b', 'c'])
    expect(readFileSync(join(dir, 'credential.key'), 'utf8')).toBe('MASTER-KEY-BYTES')
    expect(existsSync(join(dir, 'nextcowork.db-wal'))).toBe(false)
    expect(readJournal(dir)?.id).toBe(journal.id)
    expect(() => rollbackTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'not_found' }))
  })

  it('after commit, rollback needs an explicit loss acknowledgement and keeps a copy of the post-takeover data', () => {
    beginTakeover(dir, { isAlive: dead })
    const committed = commitTakeover(dir, { isAlive: dead })
    seed(['a', 'b', 'c', 'written-by-service'])
    expect(() => rollbackTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'forbidden' }))
    expect(keys()).toContain('written-by-service') // 没确认就什么都没动

    const rolled = rollbackTakeover(dir, { isAlive: dead, acknowledgeDataLoss: true })
    expect(keys()).toEqual(['a', 'b', 'c'])
    expect(existsSync(join(dir, 'device-identity.json'))).toBe(false)
    expect(existsSync(join(dir, 'pki'))).toBe(false)
    // 接管之后写入的数据没有被销毁,另存在备份目录里。
    const copy = join(dir, rolled.postTakeoverCopy!)
    expect(existsSync(copy)).toBe(true)
    closeDatabase()
    openDatabase(dir)
    expect(readServiceOwnership()).toBeNull()
    closeDatabase()
    expect(rolled.id).toBe(committed.id)
  })

  it('refuses to roll back while a service is running', () => {
    beginTakeover(dir, { isAlive: dead })
    writeFileSync(join(dir, 'service.lock'), JSON.stringify({ pid: 4242, token: 'x'.repeat(16), startedAt: 1 }))
    expect(() => rollbackTakeover(dir, { isAlive: alive })).toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('refuses to continue on a corrupt journal', () => {
    writeFileSync(join(dir, 'takeover.journal.json'), '{ nope')
    expect(() => beginTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'internal' }))
    writeFileSync(join(dir, 'takeover.journal.json'), JSON.stringify({ v: 1, id: 'x', phase: 'weird', startedAt: 1, backupDir: 'b', sidecars: [] }))
    expect(() => rollbackTakeover(dir, { isAlive: dead })).toThrowError(expect.objectContaining({ code: 'internal' }))
  })
})

describe('backup verification', () => {
  it('detects a backup that no longer matches the source and a damaged backup file', () => {
    const backup = join(dir, 'copy.db')
    backupDatabaseFile(join(dir, 'nextcowork.db'), backup)
    expect(verifyDatabaseBackup(join(dir, 'nextcowork.db'), backup)).toBeNull()
    seed(['a', 'b', 'c', 'd'])
    expect(verifyDatabaseBackup(join(dir, 'nextcowork.db'), backup)).toBe('backup row count differs for kv')
    truncateSync(backup, 100)
    expect(verifyDatabaseBackup(join(dir, 'nextcowork.db'), backup)).toMatch(/could not be opened|integrity/)
  })
})
