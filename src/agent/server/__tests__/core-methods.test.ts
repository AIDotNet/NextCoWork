import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { DEMO_ALIAS, DEMO_ALIASES, DEMO_PROVIDER, demoHost } from '../../../main/kernel/upstream/demo'
import { closeDatabase } from '../../../main/db'
import { runs } from '../../../main/kernel/run-registry'
import { interactions } from '../../../main/kernel/interaction-gate'
import { store } from '../../../main/state/store'
import { connectController } from '../../../controller-client/connect'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import type { ControllerClient } from '../../../controller-client/client'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'

// 不是 Electron mock:服务端执行链一旦尝试加载它,测试就必须失败。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest, shutdownEnvironments, shutdownMcp } from '../../../main/runtime'
import { shutdownRuns } from '../../../main/services/agent'
import { registerCoreMethods } from '../methods/core'
import { DeviceEventHub } from '../events'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
let uninstall: (() => void) | null = null
const transports: SecureTransport[] = []
const options = { workspaceId: 'workspace', mode: 'code', thinking: 'off' as const, webSearch: false,
  permissionMode: 'full' as const, model: DEMO_ALIAS, skillIds: [] as string[] }

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-core-methods-'))
  mkdirSync(join(root, 'workspace'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0,
    features: ['tasks'], register: (router) => { uninstall = registerCoreMethods(router, new DeviceEventHub()) } })
  installHost(demoHost({ paths: { userData: () => join(root, 'data'), attachments: () => join(root, 'attachments'), temp: () => root } },
    { chunkDelayMs: 0 }))
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  store.putWorkspace({ id: 'workspace', name: 'Isolated', rootPath: join(root, 'workspace'), environment: { kind: 'local' },
    settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  runs.abortAll()
  await shutdownMcp()
  await shutdownEnvironments()
  await service?.stop().catch(() => {})
  service = null
  uninstall?.()
  uninstall = null
  shutdownRuns()
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function controller(controllerId = 'controller'): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text,
    { controllerId, label: 'Phone', confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId,
    expectedControllerId: controllerId, clientSessionId: `session-${controllerId}`, bindingEpoch: 0, nextId: () => `r${++n}`, clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: ['tasks'] } })
  return client
}
type Receipt = { status: string; replayed: boolean; result: Record<string, unknown> }

describe('core device methods over the real service', () => {
  it('creates a session, starts a real agent run and reads the committed transcript remotely', async () => {
    const client = await controller()
    const created = await client.command('sessions.create', { workspaceId: 'workspace' }, 'create-1') as Receipt
    expect(created.status).toBe('succeeded')
    const sessionId = created.result.sessionId as string

    const started = await client.command('agent.start', { runId: 'run-remote-1', sessionId, text: '看一下这个工程的结构', options },
      'start-1') as Receipt
    expect(started).toMatchObject({ status: 'succeeded', result: { started: true, runId: 'run-remote-1' } })
    // 控制端断开也不影响执行:这里直接等 run 结束,再从落盘转录读回。
    await vi.waitFor(() => expect(runs.get('run-remote-1')?.status).toBe('done'), { timeout: 15_000 })

    const page = await client.query('sessions.page', { sessionId, limit: 20 }) as { messages: Array<{ role: string }> }
    expect(page.messages.some((message) => message.role === 'assistant')).toBe(true)
    const list = await client.query('sessions.list', { workspaceId: 'workspace' }) as Array<{ id: string }>
    expect(list.map((item) => item.id)).toContain(sessionId)
    client.detach()
  }, 30_000)

  it('replays a start with the same key without a second run, and refuses reuse with other params', async () => {
    const client = await controller()
    const { result } = await client.command('sessions.create', { workspaceId: 'workspace' }, 'c') as Receipt
    const params = { runId: 'run-remote-2', sessionId: result.sessionId as string, text: 'hello', options }
    await client.command('agent.start', params, 'same-key')
    const replay = await client.command('agent.start', params, 'same-key') as Receipt
    expect(replay.replayed).toBe(true)
    await expect(client.command('agent.start', { ...params, text: 'different' }, 'same-key')).rejects.toMatchObject({ code: 'conflict' })
    await vi.waitFor(() => expect(runs.get('run-remote-2')?.status).toBe('done'), { timeout: 15_000 })
    client.detach()
  }, 30_000)

  it('streams live run events with contiguous cursors and replays the rest from the snapshot', async () => {
    // 放慢演示模型,让 run 在 attach 之后还有事件可推。
    installHost(demoHost({ paths: { userData: () => join(root, 'data'), attachments: () => join(root, 'attachments'), temp: () => root } },
      { chunkDelayMs: 25 }))
    const client = await controller()
    const { result } = await client.command('sessions.create', { workspaceId: 'workspace' }, 'c') as Receipt
    const sessionId = result.sessionId as string
    await client.command('agent.start', { runId: 'run-remote-4', sessionId, text: '看一下这个工程的结构', options }, 'start')
    const snapshot = await client.query('agent.attach', { runId: 'run-remote-4', sinceSeq: 0 }) as { seq: number; events: unknown[] }
    const received: Array<{ cursor: string; previousCursor: string | null; data: { events: Array<{ type: string }> } }> = []
    const off = client.follow('run:run-remote-4', String(snapshot.seq), (event) => received.push(event as never))
    await vi.waitFor(() => expect(received.flatMap((frame) => frame.data.events).some((event) => event.type === 'run_end')).toBe(true),
      { timeout: 15_000 })
    // 帧首尾相接:每帧的 previousCursor 就是上一帧的 cursor(第一帧接快照)。
    let expected = String(snapshot.seq)
    for (const frame of received) { expect(frame.previousCursor).toBe(expected); expected = frame.cursor }
    off()
    // 服务端日志会在 message_commit 之后清掉被取代的 delta,所以不能按条数相加;
    // 比的是「最后一个 cursor 就是日志的 seq」。
    const replay = await client.query('agent.attach', { runId: 'run-remote-4', sinceSeq: snapshot.seq }) as { seq: number }
    expect(received.at(-1)?.cursor).toBe(String(replay.seq))
    await expect(client.query('agent.attach', { runId: 'no-such-run', sinceSeq: 0 })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  }, 30_000)

  it('streams global activity to a watcher and stops after the connection closes', async () => {
    const client = await controller()
    const initial = await client.query('agent.watch', null) as { runs: unknown[] }
    expect(initial.runs).toEqual([])
    const events: string[] = []
    client.follow('global', '0', (event) => events.push(event.event))
    const { result } = await client.command('sessions.create', { workspaceId: 'workspace' }, 'c') as Receipt
    await client.command('agent.start', { runId: 'run-remote-5', sessionId: result.sessionId as string, text: 'hi', options }, 's')
    await vi.waitFor(() => expect(events).toContain('agent:activeRuns'), { timeout: 15_000 })
    client.detach()
    await vi.waitFor(() => expect(runs.get('run-remote-5')?.status).toBe('done'), { timeout: 15_000 })
  }, 30_000)

  it('lets exactly one of two controllers answer a pending interaction, and the run receives that answer', async () => {
    const first = await controller('controller-a')
    const second = await controller('controller-b')
    const handle = runs.create({ runId: 'run-ask', sessionId: 'sess-ask', workspaceId: 'workspace', depth: 0, input: [],
      mode: 'code', thinking: 'off', webSearch: false, permissionMode: 'ask', model: DEMO_ALIAS, skillIds: [] })
    const asked = interactions.request(handle, { kind: 'ask_user', questions: [{ header: 'Scope', question: 'Which one?',
      options: [{ label: 'A', description: '' }, { label: 'B', description: '' }], multiSelect: false, allowFreeform: false }] }, Date.now())
    const listed = await second.query('interactions.list', {}) as Array<{ id: string; kind: string }>
    expect(listed).toHaveLength(1)
    const id = listed[0]!.id
    const respond = (client: ControllerClient, label: string, key: string) =>
      client.command('interactions.respond', { response: { id, kind: 'ask_user', answers: [[label]] } }, key) as Promise<Receipt>
    const results = await Promise.all([respond(first, 'A', 'ka'), respond(second, 'B', 'kb')])
    expect(results.map((r) => r.status).sort()).toEqual(['failed', 'succeeded'])
    const loser = results.find((r) => r.status === 'failed')!
    expect(loser.result).toMatchObject({ error: { code: 'conflict' } })
    const winnerLabel = results[0]!.status === 'succeeded' ? 'A' : 'B'
    await expect(asked).resolves.toMatchObject({ kind: 'ask_user', answers: [[winnerLabel]] })
    expect(await first.query('interactions.list', {})).toEqual([])
    // 不合规的应答(题数不符)被拒绝,待决项保持不变 —— 先再挂一个验证。
    const again = interactions.request(handle, { kind: 'ask_user', questions: [{ header: 'S', question: 'Q?', options: [{ label: 'X', description: '' }],
      multiSelect: false, allowFreeform: false }] }, Date.now())
    const next = (await first.query('interactions.list', {}) as Array<{ id: string }>)[0]!.id
    const bad = await first.command('interactions.respond', { response: { id: next, kind: 'ask_user', answers: [['X'], ['Y']] } }, 'kbad') as Receipt
    expect(bad).toMatchObject({ status: 'failed', result: { error: { code: 'invalid_request' } } })
    expect(await first.query('interactions.list', {})).toHaveLength(1)
    runs.abort('run-ask', true, { by: 'user' })
    await expect(again).rejects.toBeTruthy()
    first.detach(); second.detach()
  }, 30_000)

  it('reads and updates settings through a whitelist, and manages workspaces with device-side validation', async () => {
    const client = await controller()
    const settings = await client.query('settings.get', null) as { locale: string; proxy?: unknown }
    expect(settings.locale).toBeTruthy()
    const changed = await client.command('settings.update', { patch: { locale: 'en-US', defaultPermissionMode: 'ask' } }, 's1') as Receipt
    expect(changed.status).toBe('succeeded')
    expect(await client.query('settings.get', null)).toMatchObject({ locale: 'en-US', defaultPermissionMode: 'ask' })
    // 白名单之外的字段(代理、shell …)与空 patch 都在落回执之前被拒绝。
    await expect(client.command('settings.update', { patch: { proxy: { mode: 'manual' } } }, 's2')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('settings.update', { patch: {} }, 's3')).rejects.toMatchObject({ code: 'invalid_request' })

    mkdirSync(join(root, 'second'))
    const created = await client.command('workspaces.create', { path: join(root, 'second') }, 'w1') as Receipt
    expect(created).toMatchObject({ status: 'succeeded', result: { created: true } })
    const id = created.result.workspaceId as string
    const again = await client.command('workspaces.create', { path: join(root, 'second', '..', 'second') }, 'w2') as Receipt
    expect(again.result).toMatchObject({ workspaceId: id, created: false })
    expect(await client.command('workspaces.create', { path: 'relative/dir' }, 'w3').catch((e: unknown) => e)).toMatchObject({ status: 'failed', result: { error: { code: 'invalid_request' } } })
    expect(await client.command('workspaces.create', { path: join(root, 'missing') }, 'w4')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })

    await client.command('workspaces.update', { id, name: 'Renamed', settings: { webSearch: true } }, 'w5')
    const list = await client.query('workspaces.list', null) as Array<{ id: string; name: string; settings: { webSearch: boolean } }>
    expect(list.find((item) => item.id === id)).toMatchObject({ name: 'Renamed', settings: { webSearch: true } })
    await client.command('workspaces.close', { id }, 'w6')
    expect((await client.query('workspaces.list', null) as Array<{ id: string }>).some((item) => item.id === id)).toBe(false)
    expect(await client.command('workspaces.close', { id }, 'w7')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    client.detach()
  }, 30_000)

  it('rejects unknown fields, foreign workspaces and unknown sessions', async () => {
    const client = await controller()
    const { result } = await client.command('sessions.create', { workspaceId: 'workspace' }, 'c') as Receipt
    const base = { runId: 'run-remote-3', sessionId: result.sessionId as string, text: 'x', options }
    // 内部字段不能由控制端指定。
    await expect(client.command('agent.start', { ...base, parentRunId: 'run:parent' }, 'k1')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('agent.start', { ...base, options: { ...options, depth: 2 } }, 'k2')).rejects.toMatchObject({ code: 'invalid_request' })
    const missing = await client.command('agent.start', { ...base, sessionId: 'missing-session' }, 'k3') as Receipt
    expect(missing).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    const foreign = await client.command('agent.start', { ...base, options: { ...options, workspaceId: 'elsewhere' } }, 'k4') as Receipt
    expect(foreign).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    await expect(client.query('sessions.page', { sessionId: 'missing', limit: 5 })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  }, 30_000)

  it('lists the model catalogue without base URLs or credential references', async () => {
    store.putProvider({ ...DEMO_PROVIDER, id: 'secretful', name: 'Secretful', baseUrl: 'https://internal.example/v1?key=LEAK', credentialRef: 'provider:secretful:LEAK' })
    const client = await controller()
    const catalogue = await client.query('models.list', null) as { providers: Array<Record<string, unknown>>; models: Array<{ alias: string }> }
    expect(catalogue.providers.map((provider) => provider.id)).toContain('secretful')
    expect(catalogue.models.some((model) => model.alias === DEMO_ALIAS)).toBe(true)
    const text = JSON.stringify(catalogue)
    for (const leaked of ['LEAK', 'internal.example', 'credentialRef', 'baseUrl']) expect(text).not.toContain(leaked)
    expect(Object.keys(catalogue.providers[0]!).sort()).toEqual(['enabled', 'id', 'name', 'priority', 'protocol'])
    client.detach()
  }, 30_000)
})
