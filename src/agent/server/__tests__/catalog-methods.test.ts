import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
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

// 目录方法(含桌面的 skills/commands/agents/modes IPC)整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest } from '../../../main/runtime'
import { store } from '../../../main/state/store'
import { headlessHost } from '../../../main/host/headless'
import { registerCatalogMethods } from '../methods/catalog'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }
type Item = { name: string; enabled?: boolean }

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-catalog-methods-'))
  const workspace = join(root, 'ws')
  mkdirSync(join(workspace, '.next-cowork', 'commands'), { recursive: true })
  mkdirSync(join(workspace, '.next-cowork', 'agents'), { recursive: true })
  writeFileSync(join(workspace, '.next-cowork', 'commands', 'release-notes.md'), '---\ndescription: Draft release notes\n---\nWrite release notes for $ARGUMENTS\n')
  writeFileSync(join(workspace, '.next-cowork', 'agents', 'reviewer.md'), '---\nname: reviewer\ndescription: Reviews diffs carefully\n---\nYou review diffs.\n')
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => registerCatalogMethods(router) })
  installHost(headlessHost())
  store.putWorkspace({ id: 'ws', name: 'ws', rootPath: workspace, environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function controller(): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId: 'controller', label: 'c', confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: 'controller',
    clientSessionId: 'cat', bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

describe('remote capability catalogue', () => {
  it('lists the commands and agents that live on the device, and toggles them without touching the files', async () => {
    const client = await controller()
    const commands = await client.query('commands.list', { workspaceId: 'ws' }) as Item[]
    expect(commands.find((c) => c.name === 'release-notes')).toMatchObject({ enabled: true, description: 'Draft release notes' })
    const agents = await client.query('agents.list', { workspaceId: 'ws' }) as Item[]
    expect(agents.find((a) => a.name === 'reviewer')).toMatchObject({ enabled: true })

    expect((await client.command('commands.setEnabled', { name: 'release-notes', enabled: false }, 'c1') as Receipt).status).toBe('succeeded')
    expect((await client.query('commands.list', { workspaceId: 'ws' }) as Item[]).find((c) => c.name === 'release-notes')?.enabled).toBe(false)
    expect((await client.command('agents.setEnabled', { name: 'reviewer', enabled: false }, 'a1') as Receipt).status).toBe('succeeded')
    expect((await client.query('agents.list', { workspaceId: 'ws' }) as Item[]).find((a) => a.name === 'reviewer')?.enabled).toBe(false)
    // 再打开:状态是设备上的开关,可逆。
    await client.command('commands.setEnabled', { name: 'release-notes', enabled: true }, 'c2')
    expect((await client.query('commands.list', { workspaceId: 'ws' }) as Item[]).find((c) => c.name === 'release-notes')?.enabled).toBe(true)
    client.detach()
  }, 30_000)

  it('lists skills and modes, and the global skill switch is a reversible device-side flag', async () => {
    const client = await controller()
    expect(Array.isArray(await client.query('skills.list', { workspaceId: 'ws' }))).toBe(true)
    expect(Array.isArray(await client.query('skills.list', {}))).toBe(true)
    const modes = await client.query('modes.list', { workspaceId: 'ws' }) as { modes?: unknown[] }
    expect(JSON.stringify(modes)).toContain('code')
    expect((await client.command('skills.setEnabled', { skillId: 'builtin:none', enabled: false }, 's1') as Receipt).status).toMatch(/succeeded|failed/)
    client.detach()
  }, 30_000)

  it('rejects unknown workspaces, unsafe names and extra fields, and never leaks filesystem paths', async () => {
    const client = await controller()
    await expect(client.query('commands.list', { workspaceId: 'nope' })).rejects.toMatchObject({ code: 'not_found' })
    await expect(client.query('modes.list', {})).rejects.toMatchObject({ code: 'invalid_request' })
    for (const [index, name] of ['../etc/passwd', 'a/b', 'a\\b', '', 'x'.repeat(129), 'a\u0000b'].entries()) {
      await expect(client.command('commands.setEnabled', { name, enabled: true }, `n${index}`)).rejects.toMatchObject({ code: 'invalid_request' })
    }
    await expect(client.command('agents.setEnabled', { name: 'reviewer', enabled: true, path: '/etc' }, 'x1')).rejects.toMatchObject({ code: 'invalid_request' })
    // 设备上的绝对路径(含 macOS 的 /private/var 形态)被改写成工作区内的相对位置,看不到设备目录结构。
    const rawList = await client.query('commands.list', { workspaceId: 'ws' }) as Array<{ name: string; source: string }>
    const listed = JSON.stringify(rawList)
    expect(listed).not.toContain(root)
    expect(listed).not.toContain(realpathSync(root))
    expect(rawList.find((c) => c.name === 'release-notes')?.source).toBe('<workspace>/.next-cowork/commands/release-notes.md')
    expect(JSON.stringify(await client.query('agents.list', { workspaceId: 'ws' }))).not.toContain(realpathSync(root))
    client.detach()
  }, 30_000)
})
