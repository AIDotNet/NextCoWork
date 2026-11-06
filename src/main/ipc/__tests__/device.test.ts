import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// 控制端私钥走系统钥匙串:这里用「可逆但不是明文」的假实现,能证明落盘的不是明文私钥。
const fake = vi.hoisted(() => ({ dir: '', available: true }))
vi.mock('electron', () => ({
  app: { getPath: () => fake.dir },
  safeStorage: {
    isEncryptionAvailable: () => fake.available,
    encryptString: (plain: string) => Buffer.from(Buffer.from(plain, 'utf8').reverse()),
    decryptString: (encrypted: Buffer) => Buffer.from(encrypted).reverse().toString('utf8')
  }
}))

import { closeDatabase } from '../../db'
import { installHost, resetRuntimeForTest } from '../../runtime'
import { headlessHost } from '../../host/headless'
import { registerConfigMethods } from '../../../agent/server/methods/config'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { closeDeviceManager, getDevicesState, inspectDeviceInvitation, pairDevice, removeDevice, routeInvoke, selectDevice, setDeviceRelay, useLocalDevice } from '../device'

let root = ''
let service: DeviceService | null = null

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  closeDeviceManager()
  fake.available = true
  root = mkdtempSync(join(tmpdir(), 'ncw-device-ipc-'))
  fake.dir = join(root, 'electron-user-data')
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => registerConfigMethods(router) })
  installHost(headlessHost())
})
afterEach(async () => {
  closeDeviceManager()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

const invitation = (): string => service!.createInvitation().text

describe('device page IPC handlers', () => {
  it('starts empty on this computer, pairs after fingerprint confirmation, switches, goes local and removes', async () => {
    expect(getDevicesState()).toMatchObject({ devices: [], current: { deviceId: null, state: 'idle' } })

    const text = invitation()
    const preview = inspectDeviceInvitation({ text })
    const paired = await pairDevice({ text, label: 'Home server', confirmedFingerprint: preview.fingerprint })
    expect(paired.devices).toHaveLength(1)
    expect(paired.devices[0]).toMatchObject({ id: service!.identity.deviceId, label: 'Home server', selected: false })
    expect(JSON.stringify(paired)).not.toMatch(/PRIVATE KEY|CERTIFICATE/)

    const online = await selectDevice({ id: paired.devices[0]!.id })
    expect(online.current).toMatchObject({ deviceId: paired.devices[0]!.id, state: 'online' })
    expect(online.devices[0]).toMatchObject({ selected: true, state: 'online' })

    expect(useLocalDevice().current).toMatchObject({ deviceId: null, state: 'idle' })
    const removed = await removeDevice({ id: paired.devices[0]!.id })
    expect(removed.devices).toEqual([])
    await expect(selectDevice({ id: paired.devices[0]!.id })).rejects.toMatchObject({ code: 'not_found' })
  })

  it('stores the controller key in the keychain store under the Electron profile, never in clear', async () => {
    const text = invitation()
    await pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })
    const dir = join(fake.dir, 'controller')
    const secrets = readFileSync(join(dir, 'secrets.json'), 'utf8')
    const list = readFileSync(join(dir, 'devices.json'), 'utf8')
    for (const file of [secrets, list]) expect(file).not.toContain('PRIVATE KEY')
    expect(Buffer.from(JSON.parse(secrets).entries[`controller-device-key:${service!.identity.deviceId}`], 'base64').reverse().toString('utf8')).toContain('PRIVATE KEY')
  })

  it('refuses to pair when the system keychain is unavailable instead of saving the key unprotected', async () => {
    fake.available = false
    const text = invitation()
    await expect(pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })).rejects.toThrow(/keychain/)
    expect(getDevicesState().devices).toEqual([])
  })

  it('keeps the selection and reports offline when the selected device goes away', async () => {
    const text = invitation()
    const { devices } = await pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })
    await selectDevice({ id: devices[0]!.id })
    await service!.stop()
    service = null
    await vi.waitFor(() => expect(getDevicesState().current.state).toBe('offline'), { timeout: 5000 })
    expect(getDevicesState().devices[0]).toMatchObject({ selected: true, state: 'offline' })
    // 再次选择同一台离线设备:返回离线状态而不是抛错,也不会改选本机。
    const again = await selectDevice({ id: devices[0]!.id })
    expect(again.current).toMatchObject({ deviceId: devices[0]!.id, state: 'offline' })
  }, 30_000)
})

describe('every invoke goes through the route', () => {
  it('runs locally while no device manager exists, so a broken device list cannot affect other channels', async () => {
    const local = vi.fn(async () => [] as never)
    expect(await routeInvoke('workspace:list', undefined, local)).toEqual([])
    expect(local).toHaveBeenCalledTimes(1)
  })

  it('forwards routed channels to the selected device and refuses the rest, without running either locally', async () => {
    const text = invitation()
    const { devices } = await pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })
    await selectDevice({ id: devices[0]!.id })
    const local = vi.fn(async () => { throw new Error('ran on this computer') })
    expect(await routeInvoke('workspace:list', undefined, local as never)).toEqual([]) // 设备上没有工作区
    await expect(routeInvoke('sessions:get', { sessionId: 's' }, local as never)).rejects.toMatchObject({ code: 'forbidden' })
    // 设备页自己的频道永远在本机,否则离线时用户就没法回到本机。
    const back = vi.fn(async () => 'local' as never)
    expect(await routeInvoke('device:useLocal', undefined, back)).toBe('local')
    expect(local).not.toHaveBeenCalled()
    useLocalDevice()
    expect(await routeInvoke('sessions:get', { sessionId: 's' }, back)).toBe('local')
  })

  it('stays locked to the device while it is offline, but the way back to this computer still works', async () => {
    const text = invitation()
    const { devices } = await pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })
    await selectDevice({ id: devices[0]!.id })
    await service!.stop()
    service = null
    await vi.waitFor(() => expect(getDevicesState().current.state).toBe('offline'), { timeout: 5000 })
    const local = vi.fn(async () => [] as never)
    await expect(routeInvoke('workspace:list', undefined, local)).rejects.toMatchObject({ code: 'disconnected' })
    expect(local).not.toHaveBeenCalled()
    // 逃生口:设备页的频道不走远程,「改用这台电脑」在离线时照样可用。
    await expect(routeInvoke('device:useLocal', undefined, async () => useLocalDevice())).resolves.toMatchObject({ current: { deviceId: null } })
    expect(await routeInvoke('workspace:list', undefined, local)).toEqual([])
    expect(local).toHaveBeenCalledTimes(1)
  }, 30_000)

  it('does not restore a remote selection after a restart: the app comes back on this computer', async () => {
    const text = invitation()
    const { devices } = await pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })
    await selectDevice({ id: devices[0]!.id })
    closeDeviceManager() // 模拟重启:管理器丢弃,清单留在磁盘
    const state = getDevicesState()
    expect(state.devices).toHaveLength(1)
    expect(state.current).toMatchObject({ deviceId: null, state: 'idle' })
    expect(state.devices[0]).toMatchObject({ selected: false, state: 'idle' })
    const local = vi.fn(async () => [] as never)
    await routeInvoke('workspace:list', undefined, local)
    expect(local).toHaveBeenCalledTimes(1)
  })
})

describe('relay setting', () => {
  it('stores a wss relay (with an optional trust root kept out of the renderer state), rejects unsafe URLs, and clears it', async () => {
    const text = invitation()
    const { devices } = await pairDevice({ text, label: 'Home', confirmedFingerprint: inspectDeviceInvitation({ text }).fingerprint })
    const id = devices[0]!.id
    expect(devices[0]!.relay).toBeNull()
    const pem = '-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----'
    const withRelay = setDeviceRelay({ id, url: ' wss://relay.example:8443 ', caPem: pem })
    expect(withRelay.devices[0]!.relay).toBe('wss://relay.example:8443')
    expect(JSON.stringify(withRelay)).not.toContain('CERTIFICATE')
    expect(() => setDeviceRelay({ id, url: 'ws://relay.example' })).toThrow()
    expect(() => setDeviceRelay({ id: 'unknown', url: 'wss://relay.example' })).toThrow()
    expect(getDevicesState().devices[0]!.relay).toBe('wss://relay.example:8443') // 拒绝的设置没有改动已有值
    expect(setDeviceRelay({ id, url: null }).devices[0]!.relay).toBeNull()
    expect(setDeviceRelay({ id, url: '   ' }).devices[0]!.relay).toBeNull()
  })
})
