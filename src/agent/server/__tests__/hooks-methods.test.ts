import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { closeDatabase } from '../../../main/db'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'

// 钩子方法(含桌面的 ipc/hooks.ts)整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest } from '../../../main/runtime'
import { store } from '../../../main/state/store'
import { headlessHost } from '../../../main/host/headless'
import { registerHookMethods } from '../methods/hooks'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }
type Hook = { id: string; scope: string; enabled: boolean; command?: string; sourcePath: string }
const settingsFile = (): string => join(root, 'ws', '.next-cowork', 'settings.local.json')

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-hooks-methods-'))
  mkdirSync(join(root, 'ws', '.next-cowork'), { recursive: true })
  writeFileSync(settingsFile(), JSON.stringify({ hooks: { PreToolUse: [{ id: 'HOOK1', matcher: 'Bash', command: 'echo guard', enabled: true, timeout: 5 }] } }))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => registerHookMethods(router) })
  installHost(headlessHost())
  store.putWorkspace({ id: 'ws', name: 'ws', rootPath: join(root, 'ws'), environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function controller(): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId: 'controller', label: 'h', confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: 'controller',
    clientSessionId: 'hk', bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

describe('remote hooks', () => {
  it('lists hooks with device paths rewritten, and toggles one without changing its content', async () => {
    const client = await controller()
    const listed = await client.query('hooks.list', { workspaceId: 'ws' }) as Hook[]
    expect(listed.find((h) => h.id === 'HOOK1')).toMatchObject({ scope: 'project', enabled: true, command: 'echo guard', sourcePath: '<workspace>/.next-cowork/settings.local.json' })
    expect(JSON.stringify(listed)).not.toContain(realpathSync(root))

    expect(await client.command('hooks.setEnabled', { scope: 'project', workspaceId: 'ws', id: 'HOOK1', enabled: false }, 'h1') as Receipt).toMatchObject({ status: 'succeeded' })
    expect((await client.query('hooks.list', { workspaceId: 'ws' }) as Hook[]).find((h) => h.id === 'HOOK1')?.enabled).toBe(false)
    const onDisk = JSON.parse(readFileSync(settingsFile(), 'utf8')) as { hooks: { PreToolUse: Array<{ command: string; matcher: string }> } }
    expect(onDisk.hooks.PreToolUse[0]).toMatchObject({ command: 'echo guard', matcher: 'Bash' })
    await client.command('hooks.setEnabled', { scope: 'project', workspaceId: 'ws', id: 'HOOK1', enabled: true }, 'h2')
    expect((await client.query('hooks.list', { workspaceId: 'ws' }) as Hook[]).find((h) => h.id === 'HOOK1')?.enabled).toBe(true)
    client.detach()
  }, 30_000)

  it('reports unknown hooks and workspaces honestly, and refuses malformed input', async () => {
    const client = await controller()
    expect(await client.command('hooks.setEnabled', { scope: 'project', workspaceId: 'ws', id: 'NOPE', enabled: false }, 'e1')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    expect(await client.command('hooks.setEnabled', { scope: 'global', id: 'HOOK1', enabled: false }, 'e2')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    expect(await client.command('hooks.setEnabled', { scope: 'project', id: 'HOOK1', enabled: false }, 'e3')).toMatchObject({ status: 'failed', result: { error: { code: 'invalid_request' } } })
    await expect(client.query('hooks.list', { workspaceId: 'nope' })).rejects.toMatchObject({ code: 'not_found' })
    for (const [index, id] of ['../x', 'a/b', '', 'x'.repeat(129)].entries()) {
      await expect(client.command('hooks.setEnabled', { scope: 'project', workspaceId: 'ws', id, enabled: true }, `n${index}`)).rejects.toMatchObject({ code: 'invalid_request' })
    }
    await expect(client.command('hooks.setEnabled', { scope: 'project', workspaceId: 'ws', id: 'HOOK1', enabled: true, command: 'rm -rf /' }, 'x1')).rejects.toMatchObject({ code: 'invalid_request' })
    client.detach()
  }, 30_000)

  it('does not expose creating, editing, deleting or test-running hooks over the network', async () => {
    const client = await controller()
    for (const method of ['hooks.save', 'hooks.upsert', 'hooks.delete', 'hooks.remove', 'hooks.test']) {
      await expect(client.query(method, { scope: 'project', workspaceId: 'ws' })).rejects.toMatchObject({ code: 'not_found' })
    }
    expect(JSON.parse(readFileSync(settingsFile(), 'utf8')).hooks.PreToolUse).toHaveLength(1)
    client.detach()
  }, 30_000)
})
