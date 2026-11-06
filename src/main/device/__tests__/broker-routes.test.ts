import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { closeDatabase } from '../../db'

// 路由表里新增的目录类与 Git 频道:整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device broker imported Electron') })
import { installHost, resetRuntimeForTest } from '../../runtime'
import { store } from '../../state/store'
import { headlessHost } from '../../host/headless'
import { registerCoreMethods } from '../../../agent/server/methods/core'
import { DeviceEventHub } from '../../../agent/server/events'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { getGitOverview, listGitBranches } from '../../ipc/git'
import { DeviceBroker } from '../broker'
import { NodeDeviceConnector } from '../connector'
import { DeviceManager } from '../manager'
import { DeviceRegistry, type SecretPort } from '../registry'

const identity = { GIT_AUTHOR_NAME: 'T', GIT_AUTHOR_EMAIL: 't@example.test', GIT_COMMITTER_NAME: 'T', GIT_COMMITTER_EMAIL: 't@example.test' }
const saved: Record<string, string | undefined> = {}
let root = ''
let repo = ''
let service: DeviceService | null = null
let manager: DeviceManager | null = null
let uninstall: () => void = () => {}
const secrets = new Map<string, string>()
const port: SecretPort = {
  get: async (ref) => secrets.get(ref) ?? null,
  set: async (ref, value) => { secrets.set(ref, value) },
  remove: async (ref) => { secrets.delete(ref) }
}
const git = (...args: string[]): string => execFileSync('git', args, { cwd: repo, encoding: 'utf8', env: { ...process.env, ...identity } })

beforeEach(async () => {
  for (const [key, value] of Object.entries(identity)) { saved[key] = process.env[key]; process.env[key] = value }
  resetRuntimeForTest()
  closeDatabase()
  secrets.clear()
  root = mkdtempSync(join(tmpdir(), 'ncw-broker-routes-'))
  repo = join(root, 'repo')
  mkdirSync(join(repo, '.next-cowork', 'commands'), { recursive: true })
  writeFileSync(join(repo, '.next-cowork', 'commands', 'ship.md'), '---\ndescription: Ship it\n---\nShip $ARGUMENTS\n')
  writeFileSync(join(repo, '.next-cowork', 'settings.local.json'), JSON.stringify({ hooks: { PreToolUse: [{ id: 'H1', matcher: 'Bash', command: 'echo hi', enabled: true, timeout: 5 }] } }))
  git('init', '-q', '-b', 'main')
  writeFileSync(join(repo, 'a.txt'), 'one\n')
  git('add', 'a.txt')
  git('commit', '-q', '-m', 'initial')
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => { uninstall = registerCoreMethods(router, new DeviceEventHub()) } })
  installHost(headlessHost())
  store.putWorkspace({ id: 'repo', name: 'Repo', rootPath: repo, environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  const registry = new DeviceRegistry(join(root, 'controller'), port)
  manager = new DeviceManager(registry, new NodeDeviceConnector(registry))
})
afterEach(async () => {
  manager?.close()
  uninstall()
  uninstall = () => {}
  manager = null
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  for (const [key, value] of Object.entries(saved)) { if (value === undefined) delete process.env[key]; else process.env[key] = value }
  rmSync(root, { recursive: true, force: true })
})

async function broker(): Promise<DeviceBroker> {
  const text = service!.createInvitation().text
  await manager!.pair(text, { label: 'Home', confirmedFingerprint: manager!.inspectInvitation(text).fingerprint })
  await manager!.select(service!.identity.deviceId)
  return new DeviceBroker(manager!)
}
const never = (async () => { throw new Error('ran on this computer') }) as never

describe('catalogue channels forwarded to the selected device', () => {
  it('returns commands, hooks, modes, agents, skills and MCP servers in the desktop shape with device paths rewritten', async () => {
    const b = await broker()
    const commands = await b.dispatch('commands:listAll', { workspaceId: 'repo' }, never)
    expect(commands.find((c) => c.name === 'ship')).toMatchObject({ description: 'Ship it', enabled: true })
    expect(JSON.stringify(commands)).not.toContain(realpathSync(root))
    const hooks = await b.dispatch('hooks:list', { workspaceId: 'repo' }, never)
    expect(hooks.find((h) => h.id === 'H1')).toMatchObject({ scope: 'project', enabled: true, sourcePath: '<workspace>/.next-cowork/settings.local.json' })
    expect(JSON.stringify(await b.dispatch('modes:list', { workspaceId: 'repo' }, never))).toContain('code')
    expect(Array.isArray(await b.dispatch('agents:list', { workspaceId: 'repo' }, never))).toBe(true)
    expect(Array.isArray(await b.dispatch('skills:list', {}, never))).toBe(true)
    expect(await b.dispatch('mcp:list', undefined, never)).toEqual([])
  }, 30_000)
})

describe('git channels forwarded to the selected device', () => {
  it('reads overview and branches identical to what the device computes', async () => {
    const b = await broker()
    expect(await b.dispatch('git:getOverview', { workspaceId: 'repo' }, never)).toEqual(JSON.parse(JSON.stringify(await getGitOverview({ workspaceId: 'repo' }))))
    expect(await b.dispatch('git:listBranches', { workspaceId: 'repo' }, never)).toEqual(JSON.parse(JSON.stringify(await listGitBranches({ workspaceId: 'repo' }))))
    const commits = await b.dispatch('git:listCommits', { workspaceId: 'repo', limit: 5 }, never)
    expect(commits).toHaveLength(1)
    expect(commits[0]).toMatchObject({ subject: 'initial' })
  }, 30_000)

  it('writes through the device: stage, commit, branch and checkout really change the repository', async () => {
    const b = await broker()
    writeFileSync(join(repo, 'b.txt'), 'two\n')
    expect(await b.dispatch('git:stage', { workspaceId: 'repo', paths: ['b.txt'] }, never)).toBeUndefined()
    expect(git('diff', '--cached', '--name-only').trim()).toBe('b.txt')
    const commit = await b.dispatch('git:commit', { workspaceId: 'repo', message: 'add b' }, never)
    expect(commit).toMatchObject({ subject: 'add b' })
    expect(git('log', '-1', '--format=%s').trim()).toBe('add b')
    const branch = await b.dispatch('git:createBranch', { workspaceId: 'repo', name: 'feature', checkout: false }, never)
    expect(branch).toMatchObject({ name: 'feature' })
    await b.dispatch('git:checkoutBranch', { workspaceId: 'repo', branch: 'feature' }, never)
    expect(git('rev-parse', '--abbrev-ref', 'HEAD').trim()).toBe('feature')
    writeFileSync(join(repo, 'c.txt'), 'three\n')
    await b.dispatch('git:stage', { workspaceId: 'repo', paths: ['c.txt'] }, never)
    await b.dispatch('git:unstage', { workspaceId: 'repo', paths: ['c.txt'] }, never)
    expect(git('diff', '--cached', '--name-only').trim()).toBe('')
  }, 30_000)

  it('reports a device-side failure with the device\'s error code instead of pretending success, and never runs it locally', async () => {
    const b = await broker()
    await expect(b.dispatch('git:checkoutBranch', { workspaceId: 'repo', branch: 'does-not-exist' }, never)).rejects.toBeInstanceOf(Error)
    await expect(b.dispatch('git:commit', { workspaceId: 'repo', message: 'nothing staged' }, never)).rejects.toBeInstanceOf(Error)
    await expect(b.dispatch('git:stage', { workspaceId: 'nope', paths: ['a.txt'] }, never)).rejects.toMatchObject({ code: 'not_found' })
    // pull / push / 生成提交信息没有设备方法:选了设备后拒绝,而不是在本机跑。
    await expect(b.dispatch('git:push', { workspaceId: 'repo' }, never)).rejects.toMatchObject({ code: 'forbidden' })
    await expect(b.dispatch('git:pull', { workspaceId: 'repo' }, never)).rejects.toMatchObject({ code: 'forbidden' })
  }, 30_000)

  it('does not resend a write after the device went offline: the caller gets disconnected', async () => {
    const b = await broker()
    await service!.stop()
    service = null
    await vi.waitFor(() => expect(manager!.current().state).toBe('offline'), { timeout: 5000 })
    writeFileSync(join(repo, 'd.txt'), 'four\n')
    await expect(b.dispatch('git:stage', { workspaceId: 'repo', paths: ['d.txt'] }, never)).rejects.toMatchObject({ code: 'disconnected' })
    expect(git('diff', '--cached', '--name-only').trim()).toBe('')
  }, 30_000)
})

describe('session channels forwarded to the selected device', () => {
  it('creates a session on the device, returns the full desktop Session, and lists and pages it', async () => {
    const b = await broker()
    const created = await b.dispatch('sessions:create', { workspaceId: 'repo', title: 'Remote chat', sessionId: 'sess-remote-1' }, never)
    expect(created).toMatchObject({ id: 'sess-remote-1', workspaceId: 'repo', title: 'Remote chat' })
    // 真的写在设备的库里(此测试里设备与本进程共用同一个库,所以能直接核对)。
    expect(store.getSession('sess-remote-1')).toMatchObject({ title: 'Remote chat' })
    const listed = await b.dispatch('sessions:list', { workspaceId: 'repo' }, never)
    expect(listed.map((s) => s.id)).toContain('sess-remote-1')
    const page = await b.dispatch('sessions:getPage', { sessionId: 'sess-remote-1', limit: 20 }, never)
    expect(page).toMatchObject({ session: { id: 'sess-remote-1' }, messages: [], hasMore: false })
  }, 30_000)

  it('surfaces device-side failures with their error codes and does not create anything locally', async () => {
    const b = await broker()
    await expect(b.dispatch('sessions:create', { workspaceId: 'nope' }, never)).rejects.toMatchObject({ code: 'not_found' })
    await expect(b.dispatch('sessions:getPage', { sessionId: 'missing', limit: 10 }, never)).rejects.toMatchObject({ code: 'not_found' })
    // 其余会话操作还没有设备方法:选了设备后拒绝,而不是改本机会话。
    // 删除会级联清掉转录与附件、没有回收站:不经网络开放,选了设备后拒绝,而不是改本机会话。
    await expect(b.dispatch('sessions:delete', { sessionId: 'sess-x' }, never)).rejects.toMatchObject({ code: 'forbidden' })
  }, 30_000)

  it('writes session metadata through the device and rejects unknown sessions, sub-agent sessions and unknown models', async () => {
    const b = await broker()
    await b.dispatch('sessions:create', { workspaceId: 'repo', sessionId: 'sess-meta-1', title: 'Before' }, never)
    expect(await b.dispatch('sessions:rename', { sessionId: 'sess-meta-1', title: '  After  ' }, never)).toBeUndefined()
    expect(store.getSession('sess-meta-1')?.title).toBe('After')
    await b.dispatch('sessions:setFavorited', { sessionId: 'sess-meta-1', favorited: true }, never)
    expect((await b.dispatch('sessions:list', { workspaceId: 'repo' }, never)).find((s) => s.id === 'sess-meta-1')).toMatchObject({ favorited: true })
    await b.dispatch('sessions:setArchived', { sessionId: 'sess-meta-1', archived: true }, never)
    expect((await b.dispatch('sessions:list', { workspaceId: 'repo', archived: false }, never)).some((s) => s.id === 'sess-meta-1')).toBe(false)
    expect((await b.dispatch('sessions:list', { workspaceId: 'repo', archived: true }, never)).some((s) => s.id === 'sess-meta-1')).toBe(true)
    await b.dispatch('sessions:setMode', { sessionId: 'sess-meta-1', mode: 'plan' }, never)
    expect(store.getSession('sess-meta-1')?.mode).toBe('plan')

    await expect(b.dispatch('sessions:rename', { sessionId: 'missing', title: 'x' }, never)).rejects.toMatchObject({ code: 'not_found' })
    await expect(b.dispatch('sessions:rename', { sessionId: 'sess-meta-1', title: '   ' }, never)).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(b.dispatch('sessions:setModel', { sessionId: 'sess-meta-1', model: 'no-such-model' }, never)).rejects.toMatchObject({ code: 'not_found' })
    store.putSession({ ...store.getSession('sess-meta-1')!, id: 'sess-sub', parentSessionId: 'sess-meta-1' })
    await expect(b.dispatch('sessions:rename', { sessionId: 'sess-sub', title: 'x' }, never)).rejects.toMatchObject({ code: 'not_found' })
  }, 30_000)
})

describe('workspace and file writes forwarded to the selected device', () => {
  it('writes a file with revision protection and returns the full desktop file, and mutates files through the device', async () => {
    const b = await broker()
    const file = await b.dispatch('workspace:readFile', { workspaceId: 'repo', path: 'a.txt' }, never) as { revision: string; content?: string }
    expect(file.content).toBe('one\n')
    const written = await b.dispatch('workspace:writeFile', { workspaceId: 'repo', path: 'a.txt', content: 'two\n', revision: file.revision }, never) as { revision: string; content?: string }
    expect(written.content).toBe('two\n')
    expect(written.revision).not.toBe(file.revision)
    expect(git('show', ':a.txt').trim()).toBe('one') // 只改了工作区文件,没碰索引
    // 用旧 revision 再写 = 冲突,设备上的内容不被覆盖。
    await expect(b.dispatch('workspace:writeFile', { workspaceId: 'repo', path: 'a.txt', content: 'stale\n', revision: file.revision }, never)).rejects.toMatchObject({ code: 'conflict' })
    expect((await b.dispatch('workspace:readFile', { workspaceId: 'repo', path: 'a.txt' }, never) as { content?: string }).content).toBe('two\n')

    await b.dispatch('workspace:mutateFile', { workspaceId: 'repo', path: 'notes.md', operation: 'create-file' }, never)
    expect((await b.dispatch('workspace:listDir', { workspaceId: 'repo', path: '' }, never)).entries.map((entry) => entry.name)).toContain('notes.md')
    await expect(b.dispatch('workspace:mutateFile', { workspaceId: 'repo', path: '../escape.md', operation: 'create-file' }, never)).rejects.toMatchObject({ code: 'invalid_request' })
  }, 30_000)

  it('updates and closes a workspace on the device, answering the updated workspace in the desktop shape', async () => {
    const b = await broker()
    mkdirSync(join(root, 'second'))
    store.putWorkspace({ id: 'second', name: 'Second', rootPath: join(root, 'second'), environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 2, lastOpenedAt: 2 })
    const updated = await b.dispatch('workspace:update', { id: 'second', name: 'Renamed', settings: { webSearch: true, defaultThinking: undefined } }, never)
    expect(updated).toMatchObject({ id: 'second', name: 'Renamed', settings: { webSearch: true } })
    await expect(b.dispatch('workspace:update', { id: 'nope', name: 'x' }, never)).rejects.toMatchObject({ code: 'not_found' })
    expect(await b.dispatch('workspace:close', { id: 'second' }, never)).toBeUndefined()
    expect(store.getWorkspace('second')).toBeUndefined()
    await expect(b.dispatch('workspace:close', { id: 'second' }, never)).rejects.toMatchObject({ code: 'not_found' })
    // 创建工作区要在设备上选目录(桌面是本机原生对话框),没有对应的远程入口:拒绝而不是在本机选。
    await expect(b.dispatch('workspace:createSsh', { browseId: 'b', path: '/x', requestId: 'r' }, never)).rejects.toMatchObject({ code: 'forbidden' })
  }, 30_000)
})
