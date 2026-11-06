import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
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

// Git 方法的整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest } from '../../../main/runtime'
import { headlessHost } from '../../../main/host/headless'
import { registerGitMethods } from '../methods/git'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
const identity = { GIT_AUTHOR_NAME: 'Test', GIT_AUTHOR_EMAIL: 't@example.com', GIT_COMMITTER_NAME: 'Test', GIT_COMMITTER_EMAIL: 't@example.com' }
let root = ''
let repo = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
const saved: Record<string, string | undefined> = {}
type Receipt = { status: string; result: Record<string, unknown> }
const git = (...args: string[]): string => execFileSync('git', args, { cwd: repo, encoding: 'utf8', env: { ...process.env, ...identity } })

beforeEach(async () => {
  for (const [key, value] of Object.entries(identity)) { saved[key] = process.env[key]; process.env[key] = value }
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-git-methods-'))
  repo = join(root, 'repo')
  mkdirSync(repo)
  git('init', '-q', '-b', 'main')
  writeFileSync(join(repo, 'a.txt'), 'one\n')
  git('add', '.')
  git('commit', '-q', '-m', 'initial')
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => registerGitMethods(router) })
  installHost(headlessHost())
  store.putWorkspace({ id: 'repo', name: 'Repo', rootPath: repo, environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  mkdirSync(join(root, 'plain'))
  store.putWorkspace({ id: 'plain', name: 'Plain', rootPath: join(root, 'plain'), environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  for (const [key, value] of Object.entries(saved)) { if (value === undefined) delete process.env[key]; else process.env[key] = value }
  rmSync(root, { recursive: true, force: true })
})

async function controller(): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId: 'controller', label: 'g', confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: 'controller',
    clientSessionId: 'git', bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

describe('remote git', () => {
  it('reads overview, branches and history, and walks a change through diff → stage → commit', async () => {
    const client = await controller()
    expect(await client.query('git.overview', { workspaceId: 'repo' })).toMatchObject({ available: true })
    expect((await client.query('git.branches', { workspaceId: 'repo' }) as Array<{ name: string; current: boolean }>)).toEqual(
      [expect.objectContaining({ name: 'main', current: true })])
    expect((await client.query('git.commits', { workspaceId: 'repo', limit: 5 }) as Array<{ subject?: string; message?: string }>)).toHaveLength(1)

    writeFileSync(join(repo, 'a.txt'), 'one\ntwo\n')
    const unstaged = await client.query('git.diff', { workspaceId: 'repo', path: 'a.txt', staged: false })
    expect(JSON.stringify(unstaged)).toContain('+two')
    expect((await client.command('git.stage', { workspaceId: 'repo', paths: ['a.txt'] }, 'g1') as Receipt).status).toBe('succeeded')
    expect(JSON.stringify(await client.query('git.diff', { workspaceId: 'repo', path: 'a.txt', staged: true }))).toContain('+two')
    const committed = await client.command('git.commit', { workspaceId: 'repo', message: 'add two' }, 'g2') as Receipt
    expect(committed.status).toBe('succeeded')
    expect(git('log', '-1', '--format=%s').trim()).toBe('add two')
    // 同一个 key 重放不会再提交一次。
    await client.command('git.commit', { workspaceId: 'repo', message: 'add two' }, 'g2')
    expect(git('rev-list', '--count', 'HEAD').trim()).toBe('2')

    writeFileSync(join(repo, 'a.txt'), 'changed\n')
    await client.command('git.stage', { workspaceId: 'repo', paths: ['a.txt'] }, 'g3')
    expect((await client.command('git.unstage', { workspaceId: 'repo', paths: ['a.txt'] }, 'g4') as Receipt).status).toBe('succeeded')
    expect(git('diff', '--cached', '--name-only').trim()).toBe('')
    client.detach()
  })

  it('creates and switches branches', async () => {
    const client = await controller()
    const created = await client.command('git.createBranch', { workspaceId: 'repo', name: 'feature/x', checkout: true }, 'b1') as Receipt
    expect(created).toMatchObject({ status: 'succeeded', result: { name: 'feature/x' } })
    expect(git('branch', '--show-current').trim()).toBe('feature/x')
    expect((await client.command('git.checkout', { workspaceId: 'repo', branch: 'main' }, 'b2') as Receipt).status).toBe('succeeded')
    expect(git('branch', '--show-current').trim()).toBe('main')
    client.detach()
  })

  it('rejects option-like or malformed refs and unsafe paths before touching git', async () => {
    const client = await controller()
    for (const [index, branch] of ['--detach', '-f', 'a b', 'x~1', 'x^', 'a:b', 'x*'].entries()) {
      await expect(client.command('git.checkout', { workspaceId: 'repo', branch }, `c${index}`)).rejects.toMatchObject({ code: 'invalid_request' })
    }
    await expect(client.command('git.stage', { workspaceId: 'repo', paths: ['/etc/passwd'] }, 'p1')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('git.stage', { workspaceId: 'repo', paths: ['../x'] }, 'p2')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('git.diff', { workspaceId: 'repo', path: '/etc/hosts', staged: false })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('git.commits', { workspaceId: 'repo', limit: 501 })).rejects.toMatchObject({ code: 'invalid_request' })
    // 通过 schema 但 git 层不接受的名字:失败回执,且 git 状态没变。
    const dots = await client.command('git.createBranch', { workspaceId: 'repo', name: 'a..b', checkout: false }, 'd1') as Receipt
    expect(dots.status).toBe('failed')
    expect(git('branch', '--list').trim()).toBe('* main')
    client.detach()
  })

  it('reports failures without leaking stderr, absolute paths or remote URLs', async () => {
    const client = await controller()
    const blank = await client.command('git.commit', { workspaceId: 'repo', message: '   ' }, 'e1') as Receipt
    expect(blank).toMatchObject({ status: 'failed', result: { error: { message: 'Git operation failed' } } })
    const missing = await client.command('git.checkout', { workspaceId: 'repo', branch: 'no-such-branch' }, 'e2') as Receipt
    expect(missing.status).toBe('failed')
    expect(JSON.stringify([blank, missing])).not.toContain(root)
    await expect(client.query('git.overview', { workspaceId: 'nope' })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  })

  it('treats a non-repository workspace as unavailable rather than an error', async () => {
    const client = await controller()
    expect(await client.query('git.overview', { workspaceId: 'plain' })).toMatchObject({ available: false })
    expect(await client.query('git.commits', { workspaceId: 'plain' })).toEqual([])
    client.detach()
  })
})
