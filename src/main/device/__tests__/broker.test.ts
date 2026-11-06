import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { INVOKE_BOUNDARIES } from '../../../shared/ipc/boundaries'
import type { InvokeChannel } from '../../../shared/ipc/contract'
import { closeDatabase } from '../../db'

// 路由决策与转发整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device broker imported Electron') })
import { installHost, resetRuntimeForTest } from '../../runtime'
import { store } from '../../state/store'
import { headlessHost } from '../../host/headless'
import { registerConfigMethods } from '../../../agent/server/methods/config'
import { registerFileMethods } from '../../../agent/server/methods/files'
import { listWorkspaceDir } from '../../services/workspace-listing'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { DEVICE_ROUTES, DeviceBroker, decideRoute } from '../broker'
import { NodeDeviceConnector } from '../connector'
import { DeviceManager } from '../manager'
import { DeviceRegistry, type SecretPort } from '../registry'

let root = ''
let service: DeviceService | null = null
let manager: DeviceManager | null = null
const secrets = new Map<string, string>()
const port: SecretPort = {
  get: async (ref) => secrets.get(ref) ?? null,
  set: async (ref, value) => { secrets.set(ref, value) },
  remove: async (ref) => { secrets.delete(ref) }
}

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  secrets.clear()
  root = mkdtempSync(join(tmpdir(), 'ncw-device-broker-'))
  mkdirSync(join(root, 'ws', 'src'), { recursive: true })
  writeFileSync(join(root, 'ws', 'README.md'), '# remote readme\n')
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => { registerConfigMethods(router); registerFileMethods(router) } })
  installHost(headlessHost())
  store.putWorkspace({ id: 'ws', name: 'ws', rootPath: join(root, 'ws'), environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  const registry = new DeviceRegistry(join(root, 'controller'), port)
  manager = new DeviceManager(registry, new NodeDeviceConnector(registry))
})
afterEach(async () => {
  manager?.close()
  manager = null
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function pairAndSelect(): Promise<DeviceManager> {
  const text = service!.createInvitation().text
  await manager!.pair(text, { label: 'Home', confirmedFingerprint: manager!.inspectInvitation(text).fingerprint })
  await manager!.select(service!.identity.deviceId)
  return manager!
}

describe('route decision', () => {
  it('handles everything locally when no device is selected', () => {
    for (const channel of Object.keys(INVOKE_BOUNDARIES) as InvokeChannel[]) expect(decideRoute(channel, null)).toBe('local')
  })

  it('keeps controller-local and composed channels local, and only forwards routed device channels', () => {
    for (const [channel, boundary] of Object.entries(INVOKE_BOUNDARIES) as Array<[InvokeChannel, string]>) {
      const decision = decideRoute(channel, 'dev')
      if (boundary !== 'device') expect(decision, channel).toBe('local')
      else expect(decision, channel).toBe(DEVICE_ROUTES[channel] === undefined ? 'refuse' : 'remote')
    }
    expect(decideRoute('device:select', 'dev')).toBe('local')
    expect(decideRoute('app:getBootstrap', 'dev')).toBe('local')
    expect(decideRoute('settings:get', 'dev')).toBe('remote')
    expect(decideRoute('sessions:get', 'dev')).toBe('refuse')
    expect(decideRoute('not:aChannel' as InvokeChannel, 'dev')).toBe('refuse')
  })

  it('only routes channels that really are device-side', () => {
    for (const channel of Object.keys(DEVICE_ROUTES) as InvokeChannel[]) expect(INVOKE_BOUNDARIES[channel], channel).toBe('device')
  })
})

describe('forwarding to the selected device', () => {
  it('returns the device\'s data in the desktop shape, not the local machine\'s', async () => {
    const selected = await pairAndSelect()
    const broker = new DeviceBroker(selected)
    // 本机处理函数故意返回「错的」数据:选了设备后它必须不被调用。
    const local = vi.fn(async () => { throw new Error('ran on this computer') })

    const workspaces = await broker.dispatch('workspace:list', undefined, local as never)
    expect(workspaces.map((w) => w.id)).toEqual(['ws'])
    expect(workspaces[0]).toMatchObject({ id: 'ws', name: 'ws' })
    // 与桌面同一函数的输出逐字段相同(经 JSON 往返)。
    expect(await broker.dispatch('workspace:listDir', { workspaceId: 'ws', path: '' }, local as never))
      .toEqual(JSON.parse(JSON.stringify(await listWorkspaceDir({ workspaceId: 'ws', path: '' }))))
    const file = await broker.dispatch('workspace:readFile', { workspaceId: 'ws', path: 'README.md' }, local as never)
    expect(JSON.stringify(file)).toContain('remote readme')
    expect(await broker.dispatch('settings:get', undefined, local as never)).toMatchObject({ locale: expect.any(String) })
    expect(local).not.toHaveBeenCalled()
  })

  it('refuses unrouted device actions without running them on this computer', async () => {
    const broker = new DeviceBroker(await pairAndSelect())
    const local = vi.fn(async () => 'ran locally' as never)
    await expect(broker.dispatch('sessions:get', { sessionId: 's' }, local)).rejects.toMatchObject({ code: 'forbidden' })
    expect(local).not.toHaveBeenCalled()
  })

  it('fails as disconnected when the device is offline instead of falling back to this computer', async () => {
    const selected = await pairAndSelect()
    const broker = new DeviceBroker(selected)
    await service!.stop()
    service = null
    await vi.waitFor(() => expect(selected.current().state).toBe('offline'), { timeout: 5000 })
    const local = vi.fn(async () => [] as never)
    await expect(broker.dispatch('workspace:list', undefined, local)).rejects.toMatchObject({ code: 'disconnected' })
    expect(local).not.toHaveBeenCalled()
  }, 30_000)

  it('runs locally again after going back to this computer', async () => {
    const selected = await pairAndSelect()
    const broker = new DeviceBroker(selected)
    selected.useLocal()
    const local = vi.fn(async () => [] as never)
    expect(await broker.dispatch('workspace:list', undefined, local)).toEqual([])
    expect(local).toHaveBeenCalledTimes(1)
  })
})
