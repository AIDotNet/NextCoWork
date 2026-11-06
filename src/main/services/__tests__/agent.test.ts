/**
 * 运行服务的**直接导入**验收:普通 Node 里 import 它,run 就跑得起来。
 *
 * 这份文件顶着两条约束,缺一条它就只是在重复 `ipc/agent*` 那些用例:
 *
 * 1. `electron` 被 mock 成**一加载就抛**。服务里任何一个值 import 把 electron
 *    拖进来,这里当场红 —— 这正是这次重构存在的理由:运行生命周期不该离不开窗口。
 * 2. **全程没有任何窗口、没有任何订阅者**。事件没人看时只停推送,run、转录与
 *    权威持久化一条都不能少,续跑也要照旧发生。断言落在库里的转录与 `RunRegistry`
 *    的状态上,而不是落在 IPC 上。
 *
 * ⚠️ 这里不 import `ipc/agent.ts`,也不 import `window/registry.ts` ——
 * 它们属于 electron 那一侧,加载它们就等于把本文件要证明的那件事抹掉。
 */
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent } from '../../../shared/agent/event'
import type { RunRequest } from '../../../shared/agent/run-request'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { closeDatabase, openDatabase } from '../../db'
import { FINISHED_RUN_TTL_MS, type RunHandle, runs } from '../../kernel/run-registry'
import { DEMO_ALIAS, DEMO_ALIASES, DEMO_PROVIDER, demoHost } from '../../kernel/upstream/demo'
import { store } from '../../state/store'
import { installHost, resetRuntimeForTest, shutdownEnvironments, shutdownMcp } from '../../runtime'

// 不是 Electron mock:只要执行链尝试加载它,测试就必须失败。
vi.mock('electron', () => { throw new Error('Agent service imported Electron') })

import {
  ABORT_SETTLE_GRACE_MS,
  abortRun,
  activeRunIndex,
  broadcastActiveRuns,
  flushRun,
  installAgentDelivery,
  installAgentStartGuard,
  launchDetachedRun,
  queueSessionInput,
  reapFinishedRuns,
  runHandle,
  sessionRuntime,
  shutdownRuns,
  startAgentRun,
  type AgentDeliveryPort,
  type AgentEventEnvelopePort,
  type GlobalAgentChannel
} from '../agent'

/** 一个没有任何窗口的宿主:装上它只是为了让「推了什么」变成可断言的东西 */
class ProbeDelivery implements AgentDeliveryPort {
  readonly envelopes: AgentEventEnvelopePort[] = []
  readonly globals: Array<{ channel: GlobalAgentChannel; payload: unknown }> = []
  readonly droppedTopics: string[] = []
  readonly observedTopics: string[] = []
  /** 有没有人在看 —— 默认「有」,好证明事件真的从端口出去了 */
  subscribers = true

  hasSubscribers(topic: string): boolean {
    this.observedTopics.push(topic)
    return this.subscribers
  }

  emitToTopic(_topic: string, _channel: 'agent:event', payload: AgentEventEnvelopePort): void {
    this.envelopes.push(payload)
  }

  emitGlobal(channel: GlobalAgentChannel, payload: unknown): void {
    this.globals.push({ channel, payload })
  }

  dropTopic(topic: string): void {
    this.droppedTopics.push(topic)
  }

  countOf(channel: GlobalAgentChannel): number {
    return this.globals.filter((g) => g.channel === channel).length
  }
}

let probe: ProbeDelivery
let root = ''
let runSeq = 0

const req = (over: Partial<RunRequest> = {}): RunRequest => ({
  runId: `svc-${++runSeq}`,
  sessionId: 'svc-session',
  workspaceId: 'svc-workspace',
  depth: 0,
  input: [{ type: 'text', text: '看一下这个工程的结构' }],
  mode: 'normal',
  thinking: 'off',
  webSearch: false,
  permissionMode: 'full',
  model: DEMO_ALIAS,
  skillIds: [],
  ...over
})

/** 一个确定性的假驱动:吐几个 delta 就收尾。量服务,不量上游怎么切片。 */
const fakeDriver = (handle: RunHandle): void => {
  for (const text of ['甲', '乙', '丙']) {
    handle.emit({ type: 'stream', delta: { type: 'text_delta', index: 0, text } })
  }
  handle.finish('done')
}

const allEnvelopes = (): AgentEventEnvelopePort[] => probe.envelopes
const agentEvents = (): AgentEvent[] => probe.envelopes.flatMap((env) => env.events)

beforeEach(() => {
  installAgentStartGuard(null)
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-agent-service-'))
  const workspace = join(root, 'workspace')
  mkdirSync(workspace)
  // FULL 耐久度:测试要断言的是「权威持久化继续」,不是内存里那份副本
  openDatabase(join(root, 'data'), { durability: 'full' })
  installHost(demoHost({
    paths: { userData: () => join(root, 'data'), attachments: () => join(root, 'attachments'), temp: () => root }
  }, { chunkDelayMs: 0 }))
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  store.putWorkspace({
    id: 'svc-workspace', name: 'Service workspace', rootPath: workspace,
    environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1
  })
  probe = new ProbeDelivery()
  installAgentDelivery(probe)
})

afterEach(async () => {
  installAgentStartGuard(null)
  installAgentDelivery(null)
  shutdownRuns()
  sessionRuntime.reset()
  runs.abortAll()
  runs.clearForTest()
  await shutdownMcp()
  await shutdownEnvironments()
  resetRuntimeForTest()
  closeDatabase()
  if (root !== '') rmSync(root, { recursive: true, force: true })
  root = ''
})

describe('服务在纯 Node 里直接可用', () => {
  it('每个 run 都推信封,且泵的 seq 与 handle 完全一致', async () => {
    const request = req({ runId: 'svc-pump-1' })
    expect(startAgentRun(request, fakeDriver).started).toBe(true)
    await vi.waitFor(() => expect(runs.get(request.runId)?.status).toBe('done'))

    const env = allEnvelopes().find((e) => e.runId === request.runId)
    expect(env).toBeDefined()
    // 「订阅在前、启动在后」在端口上也成立:端口的 hasSubscribers 是推之前的判据
    expect(probe.observedTopics).toContain(`run:${request.runId}`)
    // 三个 delta 加 run_end 一条不少 —— 没人看时也不许丢
    expect(agentEvents().map((e) => e.type)).toEqual(['stream', 'stream', 'stream', 'run_end'])
    // 批内 seq 连续的另一半:最后一个信封的 seq 就是 handle 的最后一个序号
    expect(env?.seq).toBe(runs.get(request.runId)?.seq)
    for (const event of agentEvents()) expect(structuredClone(event)).toEqual(event)
  })

  it('没有订阅者时整批丢弃,但 run 照跑完 —— 停的是推送,不是 run', async () => {
    probe.subscribers = false
    const request = req({ runId: 'svc-nobody' })
    expect(startAgentRun(request, fakeDriver).started).toBe(true)
    await vi.waitFor(() => expect(runs.get(request.runId)?.status).toBe('done'))

    expect(allEnvelopes()).toEqual([])            // 一条正文都没推
    // ★ 「谁在跑」是全局事实,与订阅无关 —— 这条广播正是与订阅解耦的那一半,
    //   也是角标不会永远转着的原因。它在 run 起止时各来一次,且一条正文都不带。
    broadcastActiveRuns()
    expect(probe.countOf('agent:activeRuns')).toBeGreaterThan(0)
    const last = probe.globals.filter((g) => g.channel === 'agent:activeRuns').at(-1)
    expect(last?.payload).toEqual({ runs: [], subagents: [] })
    // 这一轮结束了,权威索引里就没有它了
    expect(activeRunIndex().find((e) => e.runId === request.runId)).toBeUndefined()
  })

  it('★ 无观察者时续跑照旧发生,最后收敛到「没有活跃 run」', async () => {
    probe.subscribers = false
    const sessionId = 'svc-queue'
    let releaseFirst!: () => void
    const firstDriver = (handle: RunHandle): void | Promise<void> =>
      new Promise<void>((resolve) => { releaseFirst = () => { handle.finish('done'); resolve() } })

    const first = req({ runId: 'svc-queue-1', sessionId, input: [{ type: 'text', text: '第一句' }] })
    expect(startAgentRun(first, firstDriver).started).toBe(true)

    // 第一轮还在跑:第二句**入队**,而不是并行起第二个 run
    const second = req({ runId: 'svc-queue-2', sessionId, input: [{ type: 'text', text: '第二句' }] })
    const queued = startAgentRun(second, firstDriver)
    expect(queued.started).toBe(false)
    expect(queued.started === false && queued.queue.queued.map((q) => q.text)).toEqual(['第二句'])

    /*
      ★ 第一轮收尾 → 主进程自己把排队那句续上(`runSettled` → `drain`)。
      续跑用的 runId 是**新 mint 的**,不是渲染层那个 —— 所以这里断言的是
      「会话又忙起来了」,而不是某个具体 runId。续跑走的是默认驱动(真上游),
      因为它是主进程自己发起的一轮,没有窗口、也没有假驱动可言。
    */
    releaseFirst()
    await vi.waitFor(() => expect(runs.isSessionBusy(sessionId)).toBe(true), { timeout: 5000 })
    await vi.waitFor(() => expect(runs.isSessionBusy(sessionId)).toBe(false), { timeout: 30_000 })

    expect(probe.countOf('session:queueChanged')).toBeGreaterThan(0)
    expect(JSON.stringify(store.getHistory(sessionId))).toContain('第二句')

    // 轮询最终收敛:权威索引里一条都不剩(哪怕全程没有任何窗口在看)
    await vi.waitFor(() => expect(activeRunIndex()).toEqual([]))
  }, 40_000)

  it('没有装端口(host 没接窗口)时,续跑/后台那条路照旧 —— 服务不依赖投递', async () => {
    installAgentDelivery(null)
    const request = req({ runId: 'svc-silent' })
    // ★ 没有窗口的宿主走的是 detached 这条:它本来就不推正文,不需要端口
    launchDetachedRun(request, fakeDriver)
    await vi.waitFor(() => expect(runs.get(request.runId)?.status).toBe('done'))
    expect(runs.get(request.runId)?.seq).toBeGreaterThan(0)
  })

  it('没有投递端口也能正常启动，运行状态仍可重连读取', async () => {
    installAgentDelivery(null)
    const request = req({ runId: 'svc-no-port' })
    expect(startAgentRun(request, fakeDriver).started).toBe(true)
    await vi.waitFor(() => expect(runs.isSessionBusy(request.sessionId)).toBe(false))
    expect(runHandle(request.runId)?.status).toBe('done')
  })

  it('卸载投递端口不会解除服务启动闸门', () => {
    installAgentStartGuard(() => { throw new Error('service draining') })
    installAgentDelivery(null)
    expect(() => startAgentRun(req(), fakeDriver)).toThrow('service draining')
    expect(() => launchDetachedRun(req(), fakeDriver)).toThrow('service draining')
    expect(activeRunIndex()).toEqual([])
  })

  it('推送或订阅查询抛错时继续执行，快照保留终态', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    try {
      probe.emitToTopic = () => { throw new Error('control socket closed') }
      probe.emitGlobal = () => { throw new Error('control socket closed') }
      const first = req({ runId: 'svc-broken-delivery' })
      expect(startAgentRun(first, fakeDriver).started).toBe(true)
      broadcastActiveRuns()
      await vi.waitFor(() => expect(runs.isSessionBusy(first.sessionId)).toBe(false))
      expect(runHandle(first.runId)?.status).toBe('done')
      probe.hasSubscribers = () => { throw new Error('control registry closed') }
      const second = req({ runId: 'svc-broken-registry' })
      expect(startAgentRun(second, fakeDriver).started).toBe(true)
      await vi.waitFor(() => expect(runs.isSessionBusy(second.sessionId)).toBe(false))
      expect(runHandle(second.runId)?.status).toBe('done')
      expect(warn).toHaveBeenCalled()
    } finally { warn.mockRestore() }
  })

  it('TTL 到了照样回收,并顺手把主题摘掉(端口被告知)', async () => {
    const request = req({ runId: 'svc-ttl' })
    startAgentRun(request, fakeDriver)
    await vi.waitFor(() => expect(runs.isSessionBusy(request.sessionId)).toBe(false))
    const endedAt = runs.get(request.runId)?.endedAt
    expect(endedAt).toBeDefined()

    expect(reapFinishedRuns((endedAt ?? 0) + 1000)).toBe(0)
    expect(runHandle(request.runId)).toBeDefined()

    expect(reapFinishedRuns((endedAt ?? 0) + FINISHED_RUN_TTL_MS)).toBe(1)
    expect(runHandle(request.runId)).toBeUndefined()
    expect(probe.droppedTopics).toContain(`run:${request.runId}`)
  })

  it('★ 退订/卸载端口不会顺带停服务;只有显式的 shutdownRuns 才收尾', async () => {
    const flush = vi.spyOn(sessionRuntime, 'flush')
    const request = req({ runId: 'svc-shutdown' })
    startAgentRun(request, fakeDriver)
    await vi.waitFor(() => expect(runs.isSessionBusy(request.sessionId)).toBe(false))

    // 「最后一个观察者走了」:端口摘掉,推送停了 —— 但服务照旧在,没被拆掉
    installAgentDelivery(null)
    expect(flush).not.toHaveBeenCalled()
    expect(runHandle(request.runId)).toBeDefined()

    // 只有显式的生命周期入口才把泵冲掉、草稿写掉、定时器停掉
    shutdownRuns()
    expect(flush).toHaveBeenCalledTimes(1)
    flush.mockRestore()
  })

  it('★ 停止后驱动一直不收尾:宽限期一过强制结束并放开会话(#187)', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    try {
      const request = req({ runId: 'svc-stuck', sessionId: 'svc-stuck' })
      // 一个不认 signal、永远不 settle 的驱动 —— 卡在某处没挂 signal 的 await 上
      expect(startAgentRun(request, () => new Promise<void>(() => {})).started).toBe(true)
      abortRun(request.runId, true)

      vi.advanceTimersByTime(ABORT_SETTLE_GRACE_MS - 1)
      expect(runHandle(request.runId)?.status).toBe('running')
      expect(runs.isSessionBusy(request.sessionId)).toBe(true)

      vi.advanceTimersByTime(1)
      const handle = runHandle(request.runId)
      expect(handle?.status).toBe('aborted')
      expect(handle?.abandoned).toBe(true)
      expect(runs.isSessionBusy(request.sessionId)).toBe(false)
      expect(activeRunIndex().find((e) => e.runId === request.runId)).toBeUndefined()
    } finally {
      warn.mockRestore()
      vi.useRealTimers()
    }
  })

  it('停止后驱动按时收尾:不触发强制收口', async () => {
    const request = req({ runId: 'svc-stop-ok', sessionId: 'svc-stop-ok' })
    const driver = (handle: RunHandle): Promise<void> => new Promise<void>((resolve) => {
      handle.signal.addEventListener('abort', () => { handle.finish('aborted'); resolve() }, { once: true })
    })
    expect(startAgentRun(request, driver).started).toBe(true)
    abortRun(request.runId, true)
    await vi.waitFor(() => expect(runs.isSessionBusy(request.sessionId)).toBe(false))
    expect(runHandle(request.runId)?.status).toBe('aborted')
    expect(runHandle(request.runId)?.abandoned).toBe(false)
  })

  it('run 的闸门仍然在:同一会话忙的时候再发一句先进队列', async () => {
    const sessionId = 'svc-busy'
    const first = req({ runId: 'svc-busy-1', sessionId })
    startAgentRun(first, () => { /* 一直跑着 */ })
    const second = req({ runId: 'svc-busy-2', sessionId, input: [{ type: 'text', text: '插一句' }] })
    const result = startAgentRun(second, fakeDriver)
    expect(result.started).toBe(false)
    expect(runHandle(second.runId)).toBeUndefined()
    expect(queueSessionInput({ sessionId, op: { kind: 'resume' } }).accepted).toBe(true)
  })
})

/**
 * ★★ 全文件的重点:真驱动、真库、真演示上游,而**一个窗口都没有**。
 *
 * 上面那些用例用的是假驱动 —— 它们量服务;这一条量的是「服务装的是不是真上游」:
 * runtime 的装配、别名解析、工具注册、演示上游的挂载点,任何一处接错,红的都是这里。
 * 断言落在**重开之后的库**上,因为「控制台重连要读的」正是那一份。
 */
describe('直接导入服务即可运行真 Agent(无 Electron、无窗口)', () => {
  it('跑完一轮:工具结果落库,重开数据库后一字不差', async () => {
    // 不传 driver —— 走的是服务的默认驱动,即 runtime.runAgent
    const request = req({ runId: 'svc-real-1', sessionId: 'svc-real' })
    expect(startAgentRun(request).started).toBe(true)

    await vi.waitFor(() => expect(runs.get(request.runId)?.status).not.toBe('running'), { timeout: 20_000 })
    expect(runs.get(request.runId)?.status).toBe('done')

    const messages = store.getHistory('svc-real')
    const parts = messages.flatMap((m) => m.parts)
    expect(parts.map((p) => p.type)).toEqual(['text', 'text', 'tool_call', 'tool_result', 'text'])
    expect(parts[parts.length - 1]).toMatchObject({ type: 'text' })
    const last = parts[parts.length - 1]
    expect(last?.type === 'text' && last.text).toContain('演示值:text')

    // 权威持久化:关库重开还在 —— 不是渲染层的内存副本
    closeDatabase()
    openDatabase(join(root, 'data'), { durability: 'full' })
    expect(store.getHistory('svc-real')).toEqual(messages)
  }, 30_000)
})

/** 让「谁也不推」这条路径也被走一遍:端口只答「没人订阅」 */
describe('端口只答订阅、不推事件', () => {
  it('hasSubscribers 为假时,信封一条都不产生,但快照仍可取', async () => {
    probe.subscribers = false
    const request = req({ runId: 'svc-readonly-port' })
    startAgentRun(request, fakeDriver)
    await vi.waitFor(() => expect(runs.get(request.runId)?.status).toBe('done'))
    flushRun(request.runId)
    expect(allEnvelopes()).toEqual([])
    expect(runHandle(request.runId)?.snapshot(0).status).toBe('done')
  })
})
