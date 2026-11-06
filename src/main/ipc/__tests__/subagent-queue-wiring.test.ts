/**
 * 并发满了 → **排队**,不是失败。这份测试盯的是整条接线上的那个结论。
 *
 * ## 为什么另起一个文件
 *
 * `subagent-wiring.test.ts` 那份假上游只会派**一次** `Task`(它钉的是订阅继承),
 * 而这里每条用例都要一轮里派好几个、还要人为把名额占满。把那份假上游改成
 * 能派 N 个,会让它原本那些断言多出一堆与其无关的分支。
 *
 * ## 名额是怎么占满的
 *
 * 直接往注册表里塞一条「永远在跑」的子 run(`occupy()`)——
 * 队列数的就是 `runs.activeSubagentRunIds()`,来路不重要。
 * 比起「派一个永不结束的真子代理」,这么做不必和 `monitorChildRun` 的收尾纠缠,
 * 而且放行的时机完全由用例说了算。
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { WebContents } from 'electron'
import type { AgentEvent } from '../../../shared/agent/event'
import type { RunRequest } from '../../../shared/agent/run-request'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import type { AgentEventEnvelope } from '../../../shared/ipc/contract'
import { runs } from '../../kernel/run-registry'
import { DEMO_ALIAS, DEMO_ALIASES, DEMO_PROVIDER, demoHost, renderDemoSse } from '../../kernel/upstream/demo'
import {
  ensureSeeded,
  installChildRunLauncher,
  installHost,
  queuedSubagentCountForTest,
  resetRuntimeForTest
} from '../../runtime'
import { store } from '../../state/store'
import type { WindowContext } from '../../window/registry'
import { backgroundSubagent, startChildRun, startRun } from '../agent'

const CHILD_MARK = 'QUEUE-MARK'
const PARENT_SESSION = 'queue-parent-session'

/** 这一轮父代理要派几个子代理,以及派的是不是后台的 */
let dispatchCount = 1
let background = false
/** 父代理收到的每一份 `tool_result` 文本 —— 「已排队」那句话就是在这里被看见的 */
let toolResults: string[] = []
/** 非空 = 子代理的上游回包挂在这个 Promise 上,放行前子代理一直在跑 */
let holdChild: Promise<void> | undefined

class FakeWebContents {
  readonly sent: Array<{ channel: string; payload: unknown }> = []
  constructor(readonly id: number) {}
  once(): void {}
  isDestroyed(): boolean { return false }
  send(channel: string, payload: unknown): void {
    this.sent.push({ channel, payload })
  }

  /**
   * WindowRegistry 判「帧还活不活」靠的是这里,不是 send 抛不抛 ——
   * 真实的 `webFrameMain.send` 自己把异常吞了(见 window/registry.ts)。
   */
  get mainFrame(): { isDestroyed: () => boolean; detached: boolean; send: (channel: string, payload: unknown) => void } {
    return {
      isDestroyed: () => this.isDestroyed(),
      detached: false,
      send: (channel, payload) => this.send(channel, payload)
    }
  }
  envelopes(): AgentEventEnvelope[] {
    return this.sent.filter((m) => m.channel === 'agent:event').map((m) => m.payload as AgentEventEnvelope)
  }
}

let nextId = 700
function fakeWindow(): { wc: FakeWebContents; ctx: WindowContext } {
  const wc = new FakeWebContents(nextId++)
  return { wc, ctx: { id: wc.id, kind: 'main', sender: wc as unknown as WebContents } }
}

interface UpstreamBody {
  model?: string
  messages?: Array<{ role: string; content?: Array<Record<string, unknown>> }>
  tools?: Array<{ name: string }>
}

/** 父代理一轮派 `dispatchCount` 个子代理;子代理直接交报告。 */
function fakeUpstream(): typeof fetch {
  let seq = 0
  return (_input, init) => {
    seq += 1
    const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as UpstreamBody
    const messages = body.messages ?? []
    const model = body.model ?? DEMO_ALIAS
    const texts = messages.flatMap((m) =>
      (m.content ?? []).filter((b) => b.type === 'text').map((b) => (typeof b.text === 'string' ? b.text : ''))
    )
    if (texts.some((t) => t.includes(CHILD_MARK))) {
      const report = (): Response => sse([{ kind: 'text', text: '子代理报告完毕。' }], model, 'end_turn')
      // 「跑很久的前台子代理」:放行之前一直挂着(转后台的用例靠它)
      if (holdChild !== undefined) return holdChild.then(report)
      return Promise.resolve(report())
    }

    const results = (messages[messages.length - 1]?.content ?? []).filter((b) => b.type === 'tool_result')
    if (results.length > 0) {
      for (const r of results) {
        toolResults.push(typeof r.content === 'string' ? r.content : JSON.stringify(r.content))
      }
      return Promise.resolve(sse([{ kind: 'text', text: '父代理收工。' }], model, 'end_turn'))
    }

    const name = (body.tools ?? []).map((t) => t.name).find((n) => n.toLowerCase() === 'task')
    if (name === undefined) throw new Error('工具表里没有 Task')
    /*
      ★ 一轮里的多个 Task 是 `Promise.all` 并行跑的(`agent-session.ts`),
      所以前台派发同样会真实撞上限额 —— 这正是要钉的那条路径。
    */
    return Promise.resolve(
      sse(
        Array.from({ length: dispatchCount }, (_v, i) => ({
          kind: 'tool_use' as const,
          id: `toolu_${String(seq)}_${String(i)}`,
          name,
          input: {
            description: `第 ${String(i + 1)} 个`,
            prompt: `${CHILD_MARK} 任务 ${String(i + 1)}`,
            subagent_type: 'general-purpose',
            ...(background ? { run_in_background: true } : {})
          }
        })),
        model,
        'tool_use'
      )
    )
  }
}

function sse(
  blocks: Array<
    { kind: 'text'; text: string } | { kind: 'tool_use'; id: string; name: string; input: Record<string, unknown> }
  >,
  model: string,
  stopReason: 'end_turn' | 'tool_use'
): Response {
  return new Response(renderDemoSse({ blocks, stopReason }, model, 20), {
    status: 200,
    headers: { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' }
  })
}

let runSeq = 0
const req = (over: Partial<RunRequest> = {}): RunRequest => ({
  runId: `queue-wire-${++runSeq}`,
  sessionId: PARENT_SESSION,
  workspaceId: 'w1',
  depth: 0,
  input: [{ type: 'text', text: '帮我同时查三件事' }],
  mode: 'normal',
  thinking: 'off',
  webSearch: false,
  permissionMode: 'auto',
  model: DEMO_ALIAS,
  skillIds: [],
  ...over
})

/**
 * 占掉一个全局名额,返回放行的办法。
 *
 * 占位者是一条货真价实的、`parentRunId` 不为空的 running run —— 队列数的就是这个。
 */
function occupy(id: string): () => void {
  runs.create(req({ runId: `${id}-owner`, sessionId: `${id}-owner-session` }))
  const child = runs.create(req({ runId: id, sessionId: `${id}-session`, parentRunId: `${id}-owner`, parentSessionId: `${id}-owner-session`, depth: 1 }))
  return () => child.finish('done')
}

async function waitForEnd(runId: string, timeoutMs = 15_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const h = runs.get(runId)
    if (h === undefined || h.status !== 'running') return
    if (Date.now() > deadline) throw new Error(`run ${runId} 在 ${String(timeoutMs)}ms 内没有结束`)
    await new Promise((r) => setTimeout(r, 5))
  }
}

const allEvents = (wc: FakeWebContents): AgentEvent[] => wc.envelopes().flatMap((e) => e.events)

beforeEach(() => {
  dispatchCount = 1
  background = false
  toolResults = []
  holdChild = undefined
  resetRuntimeForTest()
  store.putWorkspace({ id: 'w1', name: 'Local test workspace', rootPath: '', environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  store.ensureSession({ id: PARENT_SESSION, workspaceId: 'w1', rootPathAtCreation: '' })
  store.setHistory(PARENT_SESSION, [])
  installHost(demoHost({ fetch: fakeUpstream() }, { chunkDelayMs: 0 }))
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  ensureSeeded()
  store.updateSettings({ subagent: { model: '', modelProviderId: undefined } })
  installChildRunLauncher(startChildRun)
})

afterEach(() => {
  runs.abortAll()
  resetRuntimeForTest()
})

describe('并发满了就排队', () => {
  /**
   * ★★ 这份改动的全部意义就在这一条。
   *
   * 以前:上限 1 时,一轮里派 3 个,后两个当场拿到 `toolFail`——
   * 界面上两张红色失败卡,而模型收到的是「子代理失败了」,这次派发的意图直接丢了。
   * 现在:三个都要跑完,只是**依次**跑。
   */
  it('★ 每会话上限 1 时连派 3 个:三个都跑完,而且是按派发顺序启动的', async () => {
    store.updateSettings({ subagent: { perSessionLimit: 1, globalLimit: 4 } })
    dispatchCount = 3
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const started = allEvents(wc).filter((e) => e.type === 'subagent_start')
    expect(started).toHaveLength(3)
    // 启动顺序 == 派发顺序。一个都不能少,一个都不能是失败卡。
    expect(started.map((e) => (e.type === 'subagent_start' ? e.description : ''))).toEqual([
      '第 1 个', '第 2 个', '第 3 个'
    ])
    const ended = allEvents(wc).filter((e) => e.type === 'subagent_end')
    expect(ended).toHaveLength(3)
    expect(ended.every((e) => e.type === 'subagent_end' && e.status === 'done')).toBe(true)

    // 真的是**依次**跑的:任何时刻名下最多一个在跑
    expect(toolResults.join('\n')).not.toContain('limit')
    expect(queuedSubagentCountForTest()).toBe(0)
  })

  it('排队期间在父卡片上报排位 —— 用易失的 tool_progress,不写进转录', async () => {
    store.updateSettings({ subagent: { perSessionLimit: 1, globalLimit: 4 } })
    dispatchCount = 2
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const progress = allEvents(wc).filter((e) => e.type === 'tool_progress')
    expect(progress.some((e) => e.type === 'tool_progress' && e.progress.message?.includes('排队'))).toBe(true)
  })

  it('全局上限为 0 时仍然当场拒绝 —— 那不是「满了」,是「关了」', async () => {
    store.updateSettings({ subagent: { perSessionLimit: 4, globalLimit: 0 } })
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const ended = allEvents(wc).find((e) => e.type === 'tool_end')
    expect(ended?.type === 'tool_end' ? ended.isError : false).toBe(true)
    expect(JSON.stringify(ended)).toContain('disabled')
    expect(allEvents(wc).some((e) => e.type === 'subagent_start')).toBe(false)
    // ★ 绝不入队:它永远等不到空位,挂起就是骗人
    expect(queuedSubagentCountForTest()).toBe(0)
  })

  it('父 run 被停掉时,排在队列里的那个跟着走 —— 不留等待者', async () => {
    store.updateSettings({ subagent: { perSessionLimit: 4, globalLimit: 1 } })
    const release = occupy('busy')
    const { ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    // 等它真的排上队(名额被占着,所以这一步只可能是排队)
    for (let i = 0; i < 200 && queuedSubagentCountForTest() === 0; i++) {
      await new Promise((res) => setTimeout(res, 5))
    }
    expect(queuedSubagentCountForTest()).toBe(1)

    runs.abort(r.runId, true)
    await waitForEnd(r.runId)

    /*
      ★ `abortable()` 只 race 掉工具调用,底层 promise 会继续跑 —— 所以「父 run 结束了」
      并不能证明等待者走了。这一行才是:漏挂 abort/run_end 监听的话它会一直是 1。
    */
    expect(queuedSubagentCountForTest()).toBe(0)
    release()
  })
})

describe('后台派发撞上并发上限', () => {
  it('★ 立刻回话说「已排队」,不占着父代理这一轮', async () => {
    store.updateSettings({ subagent: { perSessionLimit: 4, globalLimit: 1 } })
    background = true
    const release = occupy('busy')
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    // 名额还占着,所以子 run 一秒都没跑过 —— 但父代理这一轮已经收工了
    expect(toolResults.join('\n')).toContain('queued')
    expect(allEvents(wc).some((e) => e.type === 'subagent_start')).toBe(false)
    expect(runs.get(r.runId)?.status).toBe('done')

    /*
      ★ 父 run 结束 = 这条排队中的后台派发被取消。落盘的那张 Task 回执必须跟着改成
      终态 —— 否则界面上那张卡片会永远停在「运行中」,而它其实从来没有启动过。
    */
    const receipt = store
      .getHistory(PARENT_SESSION)
      .flatMap((m) => m.parts)
      .find((part) => part.type === 'tool_result' && part.subagent !== undefined)
    expect(receipt?.type === 'tool_result' ? receipt.subagent?.status : undefined).toBe('aborted')
    release()
  })
})

describe('前台子代理被用户转去后台', () => {
  it('★ 父代理不再等它、当场收工;子代理照跑,结束时按后台收尾', async () => {
    let releaseChild = (): void => {}
    holdChild = new Promise((resolve) => { releaseChild = resolve })
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    let childRunId: string | undefined
    for (let i = 0; i < 400 && childRunId === undefined; i++) {
      const start = allEvents(wc).find((e) => e.type === 'subagent_start')
      childRunId = start?.type === 'subagent_start' ? start.childRunId : undefined
      if (childRunId === undefined) await new Promise((res) => setTimeout(res, 5))
    }
    expect(childRunId).toBeDefined()

    expect(backgroundSubagent({ childRunId: childRunId! }, ctx)).toBe(true)
    // 第二次点:已经是后台了,不是错误,只是没有可转的
    expect(backgroundSubagent({ childRunId: childRunId! }, ctx)).toBe(false)

    await waitForEnd(r.runId)
    expect(runs.get(r.runId)?.status).toBe('done')
    expect(toolResults.join('\n')).toContain('moved this subagent to the background')
    // 父代理收工时子代理还在跑
    expect(runs.get(childRunId!)?.status).toBe('running')
    expect(runs.get(childRunId!)?.backgroundTask?.type).toBe('general-purpose')

    releaseChild()
    await waitForEnd(childRunId!)
    for (let i = 0; i < 200; i++) {
      const receipt = store.getHistory(PARENT_SESSION).flatMap((m) => m.parts)
        .find((part) => part.type === 'tool_result' && part.subagent !== undefined)
      if (receipt?.type === 'tool_result' && receipt.subagent?.status === 'done') break
      await new Promise((res) => setTimeout(res, 5))
    }
    const receipt = store.getHistory(PARENT_SESSION).flatMap((m) => m.parts)
      .find((part) => part.type === 'tool_result' && part.subagent !== undefined)
    // ★ 按后台收尾:回执是后台的、跑完了、等着交回主代理
    expect(receipt?.type === 'tool_result' ? receipt.subagent : undefined).toMatchObject({
      background: true, status: 'done', reportStatus: 'pending'
    })
  })

  it('已经跑完的子代理转不了 —— false,不是错误', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx)
    await waitForEnd(r.runId)
    const start = allEvents(wc).find((e) => e.type === 'subagent_start')
    expect(start?.type).toBe('subagent_start')
    expect(backgroundSubagent({ childRunId: start?.type === 'subagent_start' ? start.childRunId : '' }, ctx)).toBe(false)
  })
})
