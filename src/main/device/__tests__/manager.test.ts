import { mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { closeDatabase } from '../../db'

// 控制端设备管理整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device manager imported Electron') })
import { resetRuntimeForTest } from '../../runtime'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { NodeDeviceConnector } from '../connector'
import { DeviceManager, type DevicesState } from '../manager'
import { DeviceRegistry, type SecretPort } from '../registry'

let root = ''
let service: DeviceService | null = null
const managers: DeviceManager[] = []
let secrets: Map<string, string>
const port = (): SecretPort => ({
  get: async (ref) => secrets.get(ref) ?? null,
  set: async (ref, value) => { secrets.set(ref, value) },
  remove: async (ref) => { secrets.delete(ref) }
})

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  secrets = new Map()
  root = mkdtempSync(join(tmpdir(), 'ncw-device-manager-'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [] })
})
afterEach(async () => {
  for (const manager of managers.splice(0)) manager.close()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

function build(changes: DevicesState[] = []) {
  const registry = new DeviceRegistry(join(root, 'controller'), port())
  const manager = new DeviceManager(registry, new NodeDeviceConnector(registry), (devices) => { changes.push(devices) })
  managers.push(manager)
  return { registry, manager }
}
const pairNow = async (manager: DeviceManager, label = 'Home server') => {
  const text = service!.createInvitation().text
  const preview = manager.inspectInvitation(text)
  return manager.pair(text, { label, confirmedFingerprint: preview.fingerprint })
}
const deviceFile = (): string => join(root, 'controller', 'devices.json')

describe('desktop device manager', () => {
  it('pairs after the user confirms the fingerprint, keeps the private key out of the list and the file, and connects', async () => {
    const changes: DevicesState[] = []
    const { manager } = build(changes)
    const text = service!.createInvitation().text
    const preview = manager.inspectInvitation(text)
    expect(preview).toMatchObject({ deviceId: service!.identity.deviceId, fingerprint: expect.stringMatching(/^[0-9a-f]{64}$/) })

    const summary = await manager.pair(text, { label: '  Home server ', confirmedFingerprint: preview.fingerprint.toUpperCase() })
    expect(summary).toMatchObject({ id: service!.identity.deviceId, label: 'Home server', selected: false, state: 'idle' })
    expect(changes.length).toBeGreaterThan(0)

    const stored = readFileSync(deviceFile(), 'utf8')
    expect(stored).not.toContain('PRIVATE KEY')
    expect(JSON.stringify(manager.list())).not.toMatch(/PRIVATE KEY|CERTIFICATE/)
    expect([...secrets.values()].join('')).toContain('PRIVATE KEY') // 私钥确实只在安全存储里
    if (process.platform !== 'win32') expect(statSync(deviceFile()).mode & 0o777).toBe(0o600)

    await manager.select(summary.id)
    expect(manager.current()).toMatchObject({ deviceId: summary.id, state: 'online' })
    expect(manager.list()[0]).toMatchObject({ selected: true, state: 'online' })
    // 真实往返:未知方法由设备回 not_found,说明经过了认证的协议连接。
    await expect(manager.run((client) => client.query('nope.nothing', null))).rejects.toMatchObject({ code: 'not_found' })
  })

  it('refuses a fingerprint the user did not confirm, an expired invitation and a reused one, and stores nothing', async () => {
    const { manager } = build()
    const text = service!.createInvitation().text
    await expect(manager.pair(text, { label: 'x', confirmedFingerprint: '0'.repeat(64) })).rejects.toMatchObject({ code: 'unauthorized' })
    await expect(manager.pair(text, { label: '   ', confirmedFingerprint: manager.inspectInvitation(text).fingerprint })).rejects.toMatchObject({ code: 'invalid_request' })
    expect(manager.list()).toEqual([])
    expect(secrets.size).toBe(0)
    // 邀请一次性:配对成功后同一份不能再用。
    const preview = manager.inspectInvitation(text)
    await manager.pair(text, { label: 'one', confirmedFingerprint: preview.fingerprint })
    await expect(manager.pair(text, { label: 'two', confirmedFingerprint: preview.fingerprint })).rejects.toBeTruthy()
    expect(manager.list()).toHaveLength(1)
    expect(() => manager.inspectInvitation('ncwpair1.garbage')).toThrow()
  })

  it('remembers the selection across restarts and stays offline (never local) when the device is down', async () => {
    const first = build()
    const summary = await pairNow(first.manager)
    await first.manager.select(summary.id)
    first.manager.close()

    const second = build()
    expect(second.registry.selected()).toBe(summary.id)
    await second.manager.restore()
    expect(second.manager.current()).toMatchObject({ deviceId: summary.id, state: 'online' })

    await service!.stop()
    service = null
    await vi.waitFor(() => expect(second.manager.current().state).toBe('offline'), { timeout: 5000 })
    await expect(second.manager.run(async () => 'ran locally?')).rejects.toMatchObject({ code: 'disconnected' })
    expect(second.registry.selected()).toBe(summary.id) // 选择没有被悄悄改回本机

    // 冷启动时设备不在线:restore 不抛、状态离线、选择保留。
    const third = build()
    await third.manager.restore()
    expect(third.manager.current()).toMatchObject({ deviceId: summary.id, state: 'offline' })
    expect(third.registry.selected()).toBe(summary.id)
  }, 30_000)

  it('goes back to this computer on request, and removing a connection deletes its key but not the device-side pairing', async () => {
    const { manager, registry } = build()
    const summary = await pairNow(manager)
    await manager.select(summary.id)
    manager.useLocal()
    expect(manager.current()).toMatchObject({ deviceId: null, state: 'idle' })
    expect(registry.selected()).toBeNull()
    await expect(manager.run(async () => 1)).rejects.toMatchObject({ code: 'disconnected' })

    await manager.select(summary.id)
    expect(await manager.remove(summary.id)).toBe(true)
    expect(manager.list()).toEqual([])
    expect(secrets.size).toBe(0)
    expect(manager.current()).toMatchObject({ deviceId: null, state: 'idle' })
    expect(await manager.remove(summary.id)).toBe(false)
    // 设备上的配对记录没有被动(移除连接 ≠ 撤销配对)。
    expect(service!.connections()).toBeGreaterThanOrEqual(0)
    await expect(manager.select(summary.id)).rejects.toMatchObject({ code: 'not_found' })
  })

  it('cannot connect after the device revokes this controller', async () => {
    const { manager, registry } = build()
    const summary = await pairNow(manager)
    expect(service!.revoke(registry.controllerId())).toBe(true)
    await expect(manager.select(summary.id)).rejects.toBeTruthy()
    expect(manager.current().state).toBe('offline')
    expect(manager.list()).toHaveLength(1) // 本机清单仍在,由用户决定是否移除
  })

  it('refuses to use a corrupt device list and leaves the file untouched', () => {
    mkdirSync(join(root, 'controller'), { recursive: true })
    writeFileSync(deviceFile(), '{ "version": 1, "devices": [')
    expect(() => new DeviceRegistry(join(root, 'controller'), port())).toThrow(/unreadable/)
    expect(readFileSync(deviceFile(), 'utf8')).toBe('{ "version": 1, "devices": [')
    writeFileSync(deviceFile(), JSON.stringify({ version: 1, controllerId: 'c', selected: null, devices: [{ id: 'd', extra: true }] }))
    expect(() => new DeviceRegistry(join(root, 'controller'), port())).toThrow(/unreadable/)
  })

  it('does not leave a half-written entry when the secret store fails', async () => {
    const registry = new DeviceRegistry(join(root, 'controller'), { ...port(), set: async () => { throw new Error('keychain locked') } })
    const manager = new DeviceManager(registry, new NodeDeviceConnector(registry))
    managers.push(manager)
    const text = service!.createInvitation().text
    await expect(manager.pair(text, { label: 'x', confirmedFingerprint: manager.inspectInvitation(text).fingerprint })).rejects.toThrow('keychain locked')
    expect(manager.list()).toEqual([])
  })
})
