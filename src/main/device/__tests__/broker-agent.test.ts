import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent } from '../../../shared/agent/event'
import type { RunRequest } from '../../../shared/agent/run-request'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { DEMO_ALIAS, DEMO_ALIASES, DEMO_PROVIDER, demoHost } from '../../kernel/upstream/demo'
import { closeDatabase } from '../../db'
import { runs } from '../../kernel/run-registry'

// 远程 run 的事件桥与 agent 频道路由:整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device agent bridge imported Electron') })
import { installHost, resetRuntimeForTest, shutdownEnvironments, shutdownMcp } from '../../runtime'
import { shutdownRuns } from '../../services/agent'
import { store } from '../../state/store'
import { DeviceEventHub } from '../../../agent/server/events'
import { registerCoreMethods } from '../../../agent/server/methods/core'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { AgentBridge } from '../agent-bridge'
import { DeviceBroker, type WindowTopics } from '../broker'
import { NodeDeviceConnector } from '../connector'
import { TerminalBridge } from '../terminal-bridge'
import { DeviceManager } from '../manager'
import { DeviceRegistry, type SecretPort } from '../registry'

type Envelope = { runId: string; seq: number; events: AgentEvent[] }
let root = ''
let service: DeviceService | null = null
let uninstall: () => void = () => {}
let manager: DeviceManager | null = null
let bridge: AgentBridge | null = null
let envelopes: Envelope[] = []
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
const hostPaths = () => ({ paths: { userData: () => join(root, 'data'), attachments: () => join(root, 'attachments'), temp: () => root } })

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  secrets.clear()
  envelopes = []
  subscriptions = []
  root = mkdtempSync(join(tmpdir(), 'ncw-broker-agent-'))
  mkdirSync(join(root, 'workspace'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: ['tasks'],
    register: (router) => { uninstall = registerCoreMethods(router, new DeviceEventHub()) } })
  installHost(demoHost(hostPaths(), { chunkDelayMs: 25 }))
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  store.putWorkspace({ id: 'workspace', name: 'Isolated', rootPath: join(root, 'workspace'), environment: { kind: 'local' },
    settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  const registry = new DeviceRegistry(join(root, 'controller'), port)
  manager = new DeviceManager(registry, new NodeDeviceConnector(registry))
  bridge = new AgentBridge({ emit: (_topic, envelope) => { envelopes.push(envelope) }, run: (work) => manager!.run(work) })
})
afterEach(async () => {
  bridge?.closeAll()
  manager?.close()
  manager = null
  await service?.stop().catch(() => {})
  service = null
  uninstall()
  uninstall = () => {}
  runs.abortAll()
  await shutdownMcp()
  shutdownRuns()
  await shutdownEnvironments()
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function broker(): Promise<DeviceBroker> {
  const text = service!.createInvitation().text
  await manager!.pair(text, { label: 'Home', confirmedFingerprint: manager!.inspectInvitation(text).fingerprint })
  await manager!.select(service!.identity.deviceId)
  return new DeviceBroker(manager!, { agent: bridge!, terminals: new TerminalBridge({ emitData: () => {}, emitExit: () => {}, run: (work) => manager!.run(work), warn: () => {} }) })
}
const request = (runId: string, sessionId: string, extra: Partial<RunRequest> = {}): RunRequest => ({
  runId, sessionId, workspaceId: 'workspace', depth: 0, input: [{ type: 'text', text: '看一下这个工程的结构' }], mode: 'code',
  thinking: 'off', webSearch: false, permissionMode: 'full', model: DEMO_ALIAS, skillIds: [], ...extra
})
const allEvents = (): AgentEvent[] => envelopes.flatMap((envelope) => envelope.events)
/** 每个信封的首条序号 = 上一个信封的 seq + 1(渲染层据此判断有没有断档)。 */
function expectContiguous(): void {
  let last = 0
  for (const envelope of envelopes) {
    expect(envelope.seq - envelope.events.length + 1, JSON.stringify(envelopes.map((e) => [e.seq, e.events.length]))).toBe(last + 1)
    last = envelope.seq
  }
}
async function newSession(b: DeviceBroker): Promise<string> {
  return (await b.dispatch('sessions:create', { workspaceId: 'workspace' }, never)).id
}

describe('agent channels forwarded to the selected device', () => {
  it('starts a run on the device and delivers its events to the window as contiguous agent:event envelopes', async () => {
    const b = await broker()
    const sessionId = await newSession(b)
    expect(await b.dispatch('agent:run', request('run-bridge-1', sessionId), never, window)).toEqual({ started: true })
    expect(subscriptions).toContain('+run:run-bridge-1')
    await vi.waitFor(() => expect(allEvents().some((event) => event.type === 'run_end')).toBe(true), { timeout: 20_000 })
    expect(envelopes.every((envelope) => envelope.runId === 'run-bridge-1')).toBe(true)
    expectContiguous()
    // 事件是设备上那次 run 真实产生的:库里也有它落盘的转录。
    expect(runs.get('run-bridge-1')?.status).toBe('done')
    const page = await b.dispatch('sessions:getPage', { sessionId, limit: 20 }, never)
    expect(page.messages.some((message) => message.role === 'assistant')).toBe(true)
  }, 40_000)

  it('re-attaches from the last forwarded seq on a gap and keeps the envelopes contiguous', async () => {
    const b = await broker()
    const sessionId = await newSession(b)
    await b.dispatch('agent:run', request('run-bridge-2', sessionId), never, window)
    await vi.waitFor(() => expect(envelopes.length).toBeGreaterThan(0), { timeout: 10_000 })
    await bridge!.resync('run:run-bridge-2') // 连接层报告缺口
    await bridge!.resync('run:no-such-run') // 未跟随的 run:忽略
    await bridge!.resync('terminal:x') // 不是 run 流:忽略
    await vi.waitFor(() => expect(allEvents().some((event) => event.type === 'run_end')).toBe(true), { timeout: 20_000 })
    expectContiguous()
  }, 40_000)

  it('attach and unwatch follow and stop following a run, and closeAll invalidates every follow', async () => {
    const b = await broker()
    const sessionId = await newSession(b)
    await manager!.run((client) => client.command('agent.start', { runId: 'run-bridge-3', sessionId, text: 'hello',
      options: { workspaceId: 'workspace', mode: 'code', thinking: 'off', webSearch: false, permissionMode: 'full', model: DEMO_ALIAS, skillIds: [] } }, 'k1'))
    const snapshot = await b.dispatch('agent:attach', { runId: 'run-bridge-3', sinceSeq: 0 }, never, window)
    expect(snapshot).toMatchObject({ runId: 'run-bridge-3', sessionId })
    expect(subscriptions).toContain('+run:run-bridge-3')
    await b.dispatch('agent:unwatch', { runId: 'run-bridge-3' }, never, window)
    expect(subscriptions).toContain('-run:run-bridge-3')
    const before = envelopes.length
    await vi.waitFor(() => expect(runs.get('run-bridge-3')?.status).toBe('done'), { timeout: 20_000 })
    expect(envelopes.length).toBe(before) // 摘掉跟随之后不再有信封
    await b.dispatch('agent:attach', { runId: 'run-bridge-3', sinceSeq: 0 }, never, window)
    bridge!.closeAll()
    const after = envelopes.length
    await new Promise((resolve) => setTimeout(resolve, 100))
    expect(envelopes.length).toBe(after)
  }, 40_000)

  it('refuses runs the device cannot represent (attachments, sub-agents, plan execution) and starts nothing', async () => {
    const b = await broker()
    const sessionId = await newSession(b)
    const withImage = request('run-refused-1', sessionId, { input: [{ type: 'text', text: 'x' }, { type: 'image', mediaType: 'image/png', data: 'AAAA' } as never] })
    await expect(b.dispatch('agent:run', withImage, never, window)).rejects.toMatchObject({ code: 'forbidden' })
    await expect(b.dispatch('agent:run', request('run-refused-2', sessionId, { parentRunId: 'p', depth: 1 }), never, window)).rejects.toMatchObject({ code: 'forbidden' })
    await expect(b.dispatch('agent:run', request('run-refused-3', sessionId, { input: [] }), never, window)).rejects.toMatchObject({ code: 'forbidden' })
    expect(subscriptions).toEqual([])
    for (const id of ['run-refused-1', 'run-refused-2', 'run-refused-3']) expect(runs.get(id)).toBeUndefined()
  }, 30_000)

  it('reports a start that the device queued behind a running session instead of inventing a queue snapshot', async () => {
    const b = await broker()
    const sessionId = await newSession(b)
    await b.dispatch('agent:run', request('run-bridge-4', sessionId), never, window)
    await expect(b.dispatch('agent:run', request('run-bridge-5', sessionId), never, window)).rejects.toMatchObject({ code: 'conflict' })
    expect(subscriptions).toContain('-run:run-bridge-5')
    expect(runs.get('run-bridge-5')).toBeUndefined()
  }, 40_000)

  it('aborts a run through the device and answers the failure for an unknown run honestly', async () => {
    const b = await broker()
    const sessionId = await newSession(b)
    await b.dispatch('agent:run', request('run-bridge-6', sessionId), never, window)
    expect(await b.dispatch('agent:abort', { runId: 'run-bridge-6', cascade: true }, never)).toBeUndefined()
    await vi.waitFor(() => expect(['aborted', 'done', 'error']).toContain(runs.get('run-bridge-6')?.status), { timeout: 15_000 })
    expect(await b.dispatch('agent:listInteractions', { runId: 'run-bridge-6' }, never)).toEqual([])
    await expect(b.dispatch('agent:listInteractions', { sessionId }, never)).rejects.toMatchObject({ code: 'forbidden' })
    await expect(b.dispatch('agent:respondInteraction', { id: 'nope', kind: 'goal_proposal', approved: true }, never)).rejects.toMatchObject({ code: 'conflict' })
  }, 40_000)

  it('refuses agent channels it has no bridge for rather than running them on this computer', async () => {
    const b = await broker()
    const bare = new DeviceBroker(manager!)
    await expect(bare.dispatch('agent:attach', { runId: 'r', sinceSeq: 0 }, never, window)).rejects.toMatchObject({ code: 'forbidden' })
    await expect(b.dispatch('agent:interject', { runId: 'r', items: [] }, never)).rejects.toMatchObject({ code: 'forbidden' })
  }, 30_000)
})
