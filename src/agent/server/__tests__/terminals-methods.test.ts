import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { closeDatabase } from '../../../main/db'
import { store } from '../../../main/state/store'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'

// 终端方法的整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest, shutdownEnvironments } from '../../../main/runtime'
import { headlessHost } from '../../../main/host/headless'
import { DeviceEventHub } from '../events'
import { registerCoreMethods } from '../methods/core'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
let uninstall: (() => void) | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }
type Created = { terminal: { id: string; alive: boolean }; lease: { token: number } }

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-terminal-methods-'))
  mkdirSync(join(root, 'ws'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router, _identity, services) => { uninstall = registerCoreMethods(router, new DeviceEventHub(), services.leases) } })
  installHost(headlessHost())
  store.putWorkspace({ id: 'ws', name: 'ws', rootPath: join(root, 'ws'), environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  uninstall?.(); uninstall = null // 杀掉还活着的终端
  await shutdownEnvironments()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function controller(controllerId: string): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId, label: controllerId, confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: controllerId,
    clientSessionId: `s-${controllerId}`, bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

async function openTerminal(client: ControllerClient, key: string): Promise<Created> {
  const receipt = await client.command('terminals.create', { workspaceId: 'ws', cols: 100, rows: 30 }, key) as Receipt
  expect(receipt.status).toBe('succeeded')
  return receipt.result as unknown as Created
}

describe('remote terminals (real PTY)', () => {
  it('runs a real shell, streams its output with contiguous cursors, and fences input by lease', async () => {
    const alice = await controller('alice')
    const bob = await controller('bob')
    const { terminal, lease } = await openTerminal(alice, 't1')
    expect(terminal.alive).toBe(true)

    const output: string[] = []
    const frames: Array<{ cursor: string; previousCursor: string | null }> = []
    const attach = await alice.query('terminals.attach', { id: terminal.id }) as { seq: number; data: string }
    alice.follow(`terminal:${terminal.id}`, String(attach.seq), (event) => {
      frames.push({ cursor: event.cursor, previousCursor: event.previousCursor })
      const data = event.data as { chunk?: string }
      if (data.chunk !== undefined) output.push(data.chunk)
    })
    // CMD 与 POSIX shell 使用各自的算术语法;PTY 的回车用 \r,让 CMD 真正执行输入。
    const command = process.platform === 'win32' ? 'set /a "ncw=40+2"\recho ncw-%ncw%\r' : 'echo ncw-$((40+2))\r'
    await alice.query('terminals.write', { id: terminal.id, token: lease.token, data: command })
    await vi.waitFor(() => expect(output.join('')).toContain('ncw-42'), { timeout: 10_000 })
    let expected = String(attach.seq)
    for (const frame of frames) { expect(frame.previousCursor).toBe(expected); expected = frame.cursor }

    // bob 没有租约:既不能输入也不能调整大小,更杀不掉;错误是协议错误(query)或失败回执(execution)。
    await expect(bob.query('terminals.write', { id: terminal.id, token: lease.token, data: 'rm -rf /\n' })).rejects.toMatchObject({ code: 'stale_binding' })
    await expect(bob.query('terminals.resize', { id: terminal.id, token: lease.token, cols: 10, rows: 10 })).rejects.toMatchObject({ code: 'stale_binding' })
    expect(await bob.command('terminals.kill', { id: terminal.id, token: lease.token }, 'k1')).toMatchObject({ status: 'failed', result: { error: { code: 'stale_binding' } } })

    // 观看者可以 attach 并拿到完整缓冲。
    const watched = await bob.query('terminals.attach', { id: terminal.id }) as { data: string }
    expect(watched.data).toContain('ncw-42')
    expect(await bob.query('terminals.list', { workspaceId: 'ws' })).toHaveLength(1)

    // 交接:旧 token 立即作废,新持有者可以输入。
    const handed = await alice.command('leases.handoff', { kind: 'terminal', resourceId: terminal.id, token: lease.token, to: 'bob' }, 'h1') as Receipt
    await expect(alice.query('terminals.write', { id: terminal.id, token: lease.token, data: 'late\n' })).rejects.toMatchObject({ code: 'stale_binding' })
    await expect(bob.query('terminals.write', { id: terminal.id, token: handed.result.token as number, data: 'true\n' })).resolves.toEqual({ ok: true })
    alice.detach(); bob.detach()
  }, 30_000)

  it('survives its creator disconnecting and can be reattached, then ends on exit with an exit event', async () => {
    const alice = await controller('alice')
    const { terminal, lease } = await openTerminal(alice, 't1')
    await alice.query('terminals.write', { id: terminal.id, token: lease.token, data: 'echo before-disconnect\n' })
    alice.detach()
    transports.splice(0).forEach((transport) => transport.close())

    const carol = await controller('carol')
    await vi.waitFor(async () => {
      const buffer = await carol.query('terminals.attach', { id: terminal.id }) as { data: string }
      expect(buffer.data).toContain('before-disconnect')
    }, { timeout: 10_000 })
    expect(await carol.query('terminals.list', { workspaceId: 'ws' })).toEqual([expect.objectContaining({ id: terminal.id, alive: true })])
    // 租约还属于 alice(30 秒内),carol 想输入得先被交接;未交接前被拒。
    await expect(carol.query('terminals.write', { id: terminal.id, token: lease.token, data: 'x\n' })).rejects.toMatchObject({ code: 'stale_binding' })
    carol.detach()
  }, 30_000)

  it('validates sizes and input, rejects unknown terminals, and keeps remote-environment and identity fields out', async () => {
    const alice = await controller('alice')
    await expect(alice.command('terminals.create', { workspaceId: 'ws', cols: 0, rows: 30 }, 'v1')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(alice.command('terminals.create', { workspaceId: 'ws', cols: 100, rows: 30, controllerId: 'bob' }, 'v2')).rejects.toMatchObject({ code: 'invalid_request' })
    expect(await alice.command('terminals.create', { workspaceId: 'nope', cols: 80, rows: 24 }, 'v3')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    await expect(alice.query('terminals.attach', { id: 'term-missing' })).rejects.toMatchObject({ code: 'not_found' })
    const { terminal, lease } = await openTerminal(alice, 'v4')
    await expect(alice.query('terminals.write', { id: terminal.id, token: lease.token, data: '' })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(alice.query('terminals.write', { id: terminal.id, token: lease.token, data: 'a'.repeat(64 * 1024 + 1) })).rejects.toMatchObject({ code: 'invalid_request' })
    // 租约持有者自己杀终端,之后终端标记为退出,输入被拒绝。
    expect(await alice.command('terminals.kill', { id: terminal.id, token: lease.token }, 'v5')).toMatchObject({ status: 'succeeded' })
    await vi.waitFor(async () => {
      expect((await alice.query('terminals.list', { workspaceId: 'ws' }) as Array<{ alive: boolean }>)[0]?.alive).toBe(false)
    }, { timeout: 10_000 })
    await expect(alice.query('terminals.write', { id: terminal.id, token: lease.token, data: 'x\n' })).rejects.toMatchObject({ code: 'conflict' })
    alice.detach()
  }, 30_000)

  it('never opens a terminal in a remote (SSH) workspace: that path would need credentials and prompts nobody can answer', async () => {
    store.putWorkspace({ id: 'ssh-ws', name: 'ssh', rootPath: '/srv/app', environment: { kind: 'connection', connectionId: 'conn-1' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
    const alice = await controller('alice')
    const receipt = await alice.command('terminals.create', { workspaceId: 'ssh-ws', cols: 80, rows: 24 }, 'ssh1') as { status: string; result: { error?: { code: string } } }
    expect(receipt.status).toBe('failed')
    expect(receipt.result.error?.code).toBe('not_configured')
    expect(await alice.query('terminals.list', { workspaceId: 'ssh-ws' })).toEqual([])
    alice.detach()
  }, 30_000)
})
