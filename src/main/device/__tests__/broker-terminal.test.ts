import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { closeDatabase } from '../../db'

// 远程终端桥:整条链(含真实 PTY)不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device terminal bridge imported Electron') })
import { installHost, resetRuntimeForTest, shutdownEnvironments } from '../../runtime'
import { store } from '../../state/store'
import { headlessHost } from '../../host/headless'
import { DeviceEventHub } from '../../../agent/server/events'
import { registerCoreMethods } from '../../../agent/server/methods/core'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { AgentBridge } from '../agent-bridge'
import { DeviceBroker, type WindowTopics } from '../broker'
import { NodeDeviceConnector } from '../connector'
import { DeviceManager } from '../manager'
import { DeviceRegistry, type SecretPort } from '../registry'
import { TerminalBridge } from '../terminal-bridge'

type Data = { id: string; seq: number; chunk: string }
let root = ''
let service: DeviceService | null = null
let uninstall: (() => void) | null = null
let manager: DeviceManager | null = null
let terminals: TerminalBridge | null = null
let data: Data[] = []
let exits: Array<{ id: string; code: number }> = []
let subscriptions: string[] = []
const secrets = new Map<string, string>()
const port: SecretPort = {
  get: async (ref) => secrets.get(ref) ?? null,
  set: async (ref, value) => { secrets.set(ref, value) },
  remove: async (ref) => { secrets.delete(ref) }
}
const window: WindowTopics = {
  subscribe: (topic) => { subscriptions.push(`+${topic}`) },
  unsubscribe: (topic) => { subscriptions.push(`-${topic}`) }
}
const never = (async () => { throw new Error('ran on this computer') }) as never
const output = (id: string): string => data.filter((item) => item.id === id).map((item) => item.chunk).join('')

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  secrets.clear()
  data = []; exits = []; subscriptions = []
  root = mkdtempSync(join(tmpdir(), 'ncw-broker-terminal-'))
  mkdirSync(join(root, 'ws'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router, _identity, services) => { uninstall = registerCoreMethods(router, new DeviceEventHub(), services.leases) } })
  installHost(headlessHost())
  store.putWorkspace({ id: 'ws', name: 'ws', rootPath: join(root, 'ws'), environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  const registry = new DeviceRegistry(join(root, 'controller'), port)
  manager = new DeviceManager(registry, new NodeDeviceConnector(registry))
  terminals = new TerminalBridge({
    emitData: (_id, payload) => { data.push(payload) }, emitExit: (_id, payload) => { exits.push(payload) },
    run: (work) => manager!.run(work), warn: () => {}
  })
})
afterEach(async () => {
  vi.useRealTimers()
  terminals?.closeAll()
  manager?.close()
  manager = null
  uninstall?.(); uninstall = null // 杀掉还活着的终端
  await shutdownEnvironments()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function broker(): Promise<DeviceBroker> {
  const text = service!.createInvitation().text
  await manager!.pair(text, { label: 'Home', confirmedFingerprint: manager!.inspectInvitation(text).fingerprint })
  await manager!.select(service!.identity.deviceId)
  const agent = new AgentBridge({ emit: () => {}, run: (work) => manager!.run(work) })
  return new DeviceBroker(manager!, { agent, terminals: terminals! })
}

describe('terminal channels forwarded to the selected device', () => {
  it('opens a real shell on the device under the renderer\'s tab id, streams its output and takes input', async () => {
    const b = await broker()
    const info = await b.dispatch('terminal:create', { workspaceId: 'ws', id: 'tab-1', cols: 100, rows: 30 }, never, window)
    expect(info).toMatchObject({ id: 'tab-1', workspaceId: 'ws', alive: true, cols: 100, rows: 30 })
    expect(subscriptions).toContain('+term:tab-1')

    b.dispatchSend('terminal:write', { id: 'tab-1', data: 'echo ncw-$((40+2))\n' }, () => { throw new Error('ran on this computer') })
    await vi.waitFor(() => expect(output('tab-1')).toContain('ncw-42'), { timeout: 15_000 })
    // 序号严格递增(渲染层按 seq 去重):快照块 + 之后每一批。
    const seqs = data.map((item) => item.seq)
    expect([...seqs].sort((x, y) => x - y)).toEqual(seqs)
    expect(new Set(seqs).size).toBe(seqs.length)

    const buffer = await b.dispatch('terminal:getBuffer', { id: 'tab-1' }, never, window)
    expect(buffer).toMatchObject({ id: 'tab-1', truncated: false })
    expect(buffer.data).toContain('ncw-42')
    expect((await b.dispatch('terminal:list', { workspaceId: 'ws' }, never)).map((t) => t.id)).toEqual(['tab-1'])

    b.dispatchSend('terminal:resize', { id: 'tab-1', cols: 120, rows: 40 }, () => { throw new Error('ran on this computer') })
    await vi.waitFor(async () => {
      expect((await b.dispatch('terminal:list', { workspaceId: 'ws' }, never))[0]).toMatchObject({ cols: 120, rows: 40 })
    }, { timeout: 10_000 })
  }, 40_000)

  it('reconnects to the same device terminal when the tab remounts, and ends with an exit event after kill', async () => {
    const b = await broker()
    await b.dispatch('terminal:create', { workspaceId: 'ws', id: 'tab-2', cols: 80, rows: 24 }, never, window)
    const again = await b.dispatch('terminal:prepare', { workspaceId: 'ws', id: 'tab-2', cols: 80, rows: 24 }, never, window)
    expect(again).toMatchObject({ kind: 'ready', terminal: { id: 'tab-2', alive: true } })
    expect(await b.dispatch('terminal:list', { workspaceId: 'ws' }, never)).toHaveLength(1) // 没有开第二个 shell

    expect(await b.dispatch('terminal:kill', { id: 'tab-2' }, never)).toBeUndefined()
    await vi.waitFor(() => expect(exits.some((item) => item.id === 'tab-2')).toBe(true), { timeout: 15_000 })
    expect((await b.dispatch('terminal:list', { workspaceId: 'ws' }, never))[0]).toMatchObject({ id: 'tab-2', alive: false })
  }, 40_000)

  it('refuses what a device terminal cannot honour, and never opens one in a remote (SSH) workspace', async () => {
    const b = await broker()
    await expect(b.dispatch('terminal:create', { workspaceId: 'ws', id: 'tab-3', cols: 80, rows: 24, cwd: '/etc' }, never, window)).rejects.toMatchObject({ code: 'forbidden' })
    await expect(b.dispatch('terminal:approve', { id: 'x', approved: true }, never)).rejects.toMatchObject({ code: 'forbidden' })
    store.putWorkspace({ id: 'ssh-ws', name: 'ssh', rootPath: '/srv/app', environment: { kind: 'connection', connectionId: 'c' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
    await expect(b.dispatch('terminal:create', { workspaceId: 'ssh-ws', id: 'tab-4', cols: 80, rows: 24 }, never, window)).rejects.toMatchObject({ code: 'not_configured' })
    await expect(b.dispatch('terminal:getBuffer', { id: 'unknown-tab' }, never, window)).rejects.toMatchObject({ code: 'not_found' })
    await expect(b.dispatch('terminal:kill', { id: 'unknown-tab' }, never)).rejects.toMatchObject({ code: 'not_found' })
  }, 30_000)

  it('keeps the input lease alive by renewing it well before the first 30 s expire', async () => {
    const b = await broker()
    await b.dispatch('terminal:create', { workspaceId: 'ws', id: 'tab-5', cols: 80, rows: 24 }, never, window)
    const deviceTerminal = (await manager!.run((client) => client.query('terminals.list', { workspaceId: 'ws' }))) as unknown as Array<{ id: string }>
    const holder = async () => (await manager!.run((client) => client.query('leases.holder', { kind: 'terminal', resourceId: deviceTerminal[0]!.id }))) as unknown as { expiresAt: number }
    const before = (await holder()).expiresAt
    // 真实等一个续租周期(10 s):续租把设备上的租约到期时间从「创建时的 30 s」推到 2 分钟之后。
    await new Promise((resolve) => setTimeout(resolve, 10_500))
    expect((await holder()).expiresAt).toBeGreaterThan(before + 60_000)
  }, 40_000)

  it('stops following and forgets terminals after closeAll, and drops send channels it cannot route', async () => {
    const b = await broker()
    await b.dispatch('terminal:create', { workspaceId: 'ws', id: 'tab-6', cols: 80, rows: 24 }, never, window)
    terminals!.closeAll()
    const before = data.length
    b.dispatchSend('terminal:write', { id: 'tab-6', data: 'echo after-close\n' }, never) // 映射已清:忽略
    await new Promise((resolve) => setTimeout(resolve, 300))
    expect(data.length).toBe(before)
    // 没有对应设备路由的 device 类 send 频道:丢弃,不落到本机。
    const local = vi.fn()
    b.dispatchSend('tabs:persistOuter' as never, {} as never, local)
    expect(local).not.toHaveBeenCalled()
    // 控制端本地的 send 频道照常本机处理。
    b.dispatchSend('window:ready' as never, undefined as never, local)
    expect(local).toHaveBeenCalledTimes(1)
  }, 30_000)

  it('runs send channels locally when no device is selected', async () => {
    const text = service!.createInvitation().text
    await manager!.pair(text, { label: 'Home', confirmedFingerprint: manager!.inspectInvitation(text).fingerprint })
    const agent = new AgentBridge({ emit: () => {}, run: (work) => manager!.run(work) })
    const b = new DeviceBroker(manager!, { agent, terminals: terminals! })
    const local = vi.fn()
    b.dispatchSend('terminal:write', { id: 'local-tab', data: 'x' }, local)
    expect(local).toHaveBeenCalledTimes(1)
  })
})
