import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { acquireServiceRoot } from '../root'

let base = ''
beforeEach(() => { base = mkdtempSync(join(tmpdir(), 'ncw-service-root-')) })
afterEach(() => { rmSync(base, { recursive: true, force: true }) })
const dir = (): string => join(base, 'data')

describe('service data root', () => {
  it('creates a persistent identity once and a fresh serverEpoch per start', () => {
    const first = acquireServiceRoot(dir())
    const { deviceId, dataEpoch, scopeEpoch, serverEpoch } = first.identity
    first.release()
    const second = acquireServiceRoot(dir())
    expect(second.identity).toMatchObject({ deviceId, dataEpoch, scopeEpoch, configScope: 'local' })
    expect(second.identity.serverEpoch).not.toBe(serverEpoch)
    second.release()
  })

  it('refuses a second live owner and allows a new one after release', () => {
    const first = acquireServiceRoot(dir())
    expect(() => acquireServiceRoot(dir())).toThrowError(expect.objectContaining({ code: 'conflict' }))
    first.release()
    first.release()
    acquireServiceRoot(dir()).release()
  })

  it('takes over the lock of a crashed owner but not a live one', () => {
    const first = acquireServiceRoot(dir(), { pid: 4242 })
    // 模拟崩溃:不 release,锁文件还在。
    expect(() => acquireServiceRoot(dir(), { isAlive: () => true })).toThrowError(expect.objectContaining({ code: 'conflict' }))
    const second = acquireServiceRoot(dir(), { isAlive: (pid) => pid !== 4242 })
    // 旧持有者醒来 release 不能删掉新锁。
    first.release()
    expect(() => acquireServiceRoot(dir(), { isAlive: () => true })).toThrowError(expect.objectContaining({ code: 'conflict' }))
    second.release()
  })

  it('treats an unreadable lock as busy instead of stealing it', () => {
    acquireServiceRoot(dir()).release()
    writeFileSync(join(dir(), 'service.lock'), '{ not json')
    expect(() => acquireServiceRoot(dir(), { isAlive: () => false })).toThrowError(expect.objectContaining({ code: 'conflict' }))
  })

  it('refuses to start with a corrupt identity and leaves no lock behind', () => {
    acquireServiceRoot(dir()).release()
    const file = join(dir(), 'device-identity.json')
    writeFileSync(file, JSON.stringify({ ...JSON.parse(readFileSync(file, 'utf8')), deviceId: '' }))
    expect(() => acquireServiceRoot(dir())).toThrowError(expect.objectContaining({ code: 'internal' }))
    writeFileSync(file, 'garbage')
    expect(() => acquireServiceRoot(dir())).toThrowError(expect.objectContaining({ code: 'internal' }))
    // 锁已释放:修好身份文件之后能正常启动。
    rmSync(file)
    acquireServiceRoot(dir()).release()
  })
})
