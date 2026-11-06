/**
 * ★ **运行中索引的生命周期** —— 方案 §8 那条「判断模型对不对的试金石」的落点。
 *
 * 原话:*关掉最后一个正在观看某个运行中会话的 Tab,run 依然活着,
 * 且外层工作区 Tab 上仍显示运行中角标。*
 *
 * 这条不变式在代码里是**两半**,而 bug 正好长在两半之间:
 *
 *   角标读 `runIndex`(全局,一个 Map + 一个 zustand 快照)
 *   输入框和状态行读会话 store 的 `activeRunId`(每会话一份)
 *
 * 两份状态各自都对,合起来才是「在跑」。所以这里量的从来不是单独一份 ——
 * 每个用例都**同时**断言两份,不一致就是红的。实测抓到过两个方向相反的错:
 *
 *   1. run 正常结束时只清了 `activeRunId`,索引里的条目留着 → 状态行写着
 *      「已完成」,旁边三颗橙色圆点还亮着(外层 Tab、内层 Tab、侧边栏各一颗)。
 *   2. 关工作区时把索引条目连同 store 一起删了 → run 还在主进程跑着,
 *      渲染层却已经不认识它了,事件泵按 runId 反查会话查不到,静默丢弃。
 *
 * 两个 bug 都不会让任何别的测试变红,也都不会抛异常 —— 只会在界面上显示错的东西。
 *
 * `services/agent` 整个替掉:它背后是 `window.nextcowork`,node 环境里不存在。
 * 替掉之后这个 store 就是纯状态机,正好是该在无头环境里测的部分。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent } from '../../../../shared/agent/event'
import type { AgentEventEnvelope } from '../../../../shared/ipc/contract'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}),
  // 队列的真源在主进程:这里替成一个回执工厂,由各用例自己摆出主进程的回答
  queueSessionInput: vi.fn(),
  onAgentEvent: vi.fn(() => () => {})
}))

// 接上一个主进程那边已经在跑的 run 时会读一次历史;这里没有历史可读
vi.mock('../../services/sessions', () => {
  const getSession = vi.fn(async (_sessionId: string): Promise<unknown> => { throw new Error('会话不存在') })
  return {
    getSession,
    // 转录按页读(`getSessionPage`):委托给各用例摆好的整段历史,一页就是全部
    getSessionPage: vi.fn(async (sessionId: string) => {
      const detail = await getSession(sessionId)
      return detail == null ? detail : { ...(detail as object), hasMore: false }
    }),
    replaceHistory: vi.fn(async () => {})
  }
})

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
  // ★ 未发出的输入落盘。这里替成空实现,断言的是 store 的状态机而不是 IPC ——
  //   持久化本身由 shared/__tests__/queued-input.test.ts 的纯函数覆盖。
  //   `getSessionInput` 必须返回 null:返回存档会让 hydrate 往刚建好的 store 里
  //   回填内容,于是每个用例的初始状态都不再是空的。
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: vi.fn()
}))

import type { QueuedInput, SessionQueueOp, SessionQueueResult } from '../../../../shared/domain/queued-input'
import { abortRun, attachRun, queueSessionInput, startRun } from '../../services/agent'
import type { SendOptions } from '../session'
import { adoptActiveRuns, releaseSession, resumeQueue, sessionStore, useRunIndex } from '../session'
import { useTabsStore } from '../tabs'

const mockStartRun = vi.mocked(startRun)
const mockAbortRun = vi.mocked(abortRun)
const mockQueue = vi.mocked(queueSessionInput)

/**
 * ★ **标注成 `SendOptions`,不要写 `as const`。** `as const` 会把 `skillIds`
 * 推成 `readonly []`,而 `RunRequest` 要的是可变数组 —— 于是这个文件里每一次
 * `send(...)` 都通不过 `tsc`,**但 vitest 全绿**(它只转译,不做类型检查)。
 * 类型标注在这儿顺带起了个断言的作用:发送参数的形状变了,这里立刻会红。
 */
const OPTS: SendOptions = {
  workspaceId: 'w1',
  depth: 0,
  mode: 'normal',
  thinking: 'auto',
  webSearch: false,
  permissionMode: 'ask',
  model: 'demo-model',
  skillIds: []
}

const runEnd: AgentEvent = { type: 'run_end', status: 'done' }
const textDelta = (text: string): AgentEvent => ({
  type: 'stream',
  delta: { type: 'text_delta', index: 0, text }
})

/** 信封的 `seq` 是**本批最后一个**事件的序号 —— 差一位就会被判成有缺口。 */
const envelope = (runId: string, events: AgentEvent[], firstSeq = 1): AgentEventEnvelope => ({
  runId,
  seq: firstSeq + events.length - 1,
  events
})

/** 当前索引里属于某会话的 run。角标就是这么算出来的(见 App.tsx)。 */
const indexed = (sessionId: string): string[] =>
  useRunIndex
    .getState()
    .filter((r) => r.sessionId === sessionId)
    .map((r) => r.runId)

/** store 是模块级单例,用例之间必须自己收干净,否则顺序一换就红。 */
const created: string[] = []
function session(id: string): ReturnType<typeof sessionStore> {
  created.push(id)
  return sessionStore(id)
}

beforeEach(() => {
  vi.clearAllMocks()
  mockStartRun.mockResolvedValue({ started: true })
  mockAbortRun.mockResolvedValue(undefined)
  mockQueue.mockImplementation(async (sessionId) => receipt(sessionId, []))
})

afterEach(() => {
  useRunIndex.setState([], true)
  for (const id of created) {
    sessionStore(id).setState({ activeRunId: null })
    releaseSession(id)
  }
  created.length = 0
  useTabsStore.setState({ byWorkspace: {} })
})

describe('run 的两份状态必须同起同落', () => {
  it('发送:索引里出现条目,同一时刻 activeRunId 也置上了', async () => {
    const s = session('s-start')
    await s.getState().send('你好', OPTS)

    const runId = s.getState().activeRunId
    expect(runId).not.toBeNull()
    expect(indexed('s-start')).toEqual([runId])
    // 索引项要带 workspaceId —— 外层工作区 Tab 的角标按它聚合
    expect(useRunIndex.getState()[0]?.workspaceId).toBe('w1')
  })

  it('★ run 正常结束:两份一起收 —— 这就是「已完成」旁边还亮着圆点的那个 bug', async () => {
    const s = session('s-done')
    await s.getState().send('你好', OPTS)
    const runId = s.getState().activeRunId ?? ''

    s.getState().applyEnvelope(envelope(runId, [textDelta('嗨'), runEnd]))

    expect(s.getState().activeRunId).toBeNull()
    expect(indexed('s-done')).toEqual([])
  })

  it('★ 走 applyEvents 那条路(attach 补齐)也一样收', async () => {
    const s = session('s-done2')
    await s.getState().send('你好', OPTS)

    s.getState().applyEvents([runEnd])

    expect(s.getState().activeRunId).toBeNull()
    expect(indexed('s-done2')).toEqual([])
  })

  it('run 没结束就一直挂着 —— 只有 run_end 才收', async () => {
    const s = session('s-live')
    await s.getState().send('你好', OPTS)
    const runId = s.getState().activeRunId

    s.getState().applyEnvelope(envelope(runId ?? '', [textDelta('生'), textDelta('成中')]))

    expect(s.getState().activeRunId).toBe(runId)
    expect(indexed('s-live')).toEqual([runId])
  })

  it('启动就失败:抛出去之前先把两份都收掉,别留一颗永远亮着的圆点', async () => {
    mockStartRun.mockRejectedValue(new Error('上游不可达'))
    const s = session('s-fail')

    await expect(s.getState().send('你好', OPTS)).rejects.toThrow('上游不可达')

    expect(s.getState().activeRunId).toBeNull()
    expect(s.getState().transcript.messages).toEqual([])
    expect(indexed('s-fail')).toEqual([])
  })

  it('启动尚未完成时先显示用户消息,并让主进程复用它的消息 ID', async () => {
    let release!: () => void
    mockStartRun.mockImplementation(() => new Promise((resolve) => { release = () => resolve({ started: true }) }))
    const s = session('s-optimistic')

    const sending = s.getState().send('马上显示', OPTS)
    const message = s.getState().transcript.messages[0]
    expect(message).toMatchObject({ role: 'user', parts: [{ type: 'text', text: '马上显示' }] })
    expect(mockStartRun).toHaveBeenCalledWith(expect.objectContaining({ inputMessageId: message?.id }))

    release()
    await sending
  })

  it('别的 run 的信封不动本会话的状态', async () => {
    const s = session('s-other')
    await s.getState().send('你好', OPTS)
    const runId = s.getState().activeRunId

    s.getState().applyEnvelope(envelope('别人的-run', [runEnd]))

    expect(s.getState().activeRunId).toBe(runId)
    expect(indexed('s-other')).toEqual([runId])
  })
})

describe('★ 试金石:run 属于主进程,不属于任何 Tab', () => {
  it('关掉最后一个观看它的 Tab(=放掉会话 store),run 依然在索引里', async () => {
    const s = session('s-watch')
    await s.getState().send('你好', OPTS)
    const runId = s.getState().activeRunId

    // 关 Tab 不会调 releaseSession(Tab 只是引用会话),这里直接调它 ——
    // 就算调了,也必须放不掉。
    expect(releaseSession('s-watch')).toBe(false)
    expect(indexed('s-watch')).toEqual([runId])
  })

  it('关掉整个工作区:空闲会话的转录放掉,正在跑的留着', async () => {
    const busy = session('s-busy')
    const idle = session('s-idle')
    await busy.getState().send('你好', OPTS)
    idle.getState().setDraft('写了一半的草稿')

    useTabsStore.getState().hydrate('w1', {
      tabs: [
        { id: 't1', kind: 'chat', title: '在跑', ref: { sessionId: 's-busy' } },
        { id: 't2', kind: 'chat', title: '空闲', ref: { sessionId: 's-idle' } }
      ],
      activeTabId: 't1'
    })

    useTabsStore.getState().forget('w1')

    // 空闲的那个真的放掉了:再取回来是个全新的 store,草稿没了
    expect(sessionStore('s-idle').getState().draft).toBe('')
    // 在跑的那个原封不动 —— 转录、activeRunId、索引条目都还在
    expect(sessionStore('s-busy').getState().activeRunId).toBe(busy.getState().activeRunId)
    expect(indexed('s-busy')).toHaveLength(1)
  })

  it('⌘R 重载后:store 还没建,但 adoptActiveRuns 补回来的 run 同样放不掉', () => {
    adoptActiveRuns([{ runId: 'r-reload', sessionId: 's-reload', workspaceId: 'w1' }])

    // 这个会话此刻**没有 store**(懒创建),所以「在不在跑」只能问索引
    expect(releaseSession('s-reload')).toBe(false)
    expect(indexed('s-reload')).toEqual(['r-reload'])
  })

  it('空闲会话放得掉,而且放掉后重新取是干净的', () => {
    const s = session('s-free')
    s.getState().setDraft('草稿')

    expect(releaseSession('s-free')).toBe(true)
    expect(sessionStore('s-free').getState().draft).toBe('')
  })
})

/** 主进程的一份队列回执。`rev` 全局递增,跟主进程一样 */
let rev = 0
function receipt(sessionId: string, queued: QueuedInput[], extra: Partial<SessionQueueResult> = {}): SessionQueueResult {
  rev += 1
  return { sessionId, queued, rev, accepted: true, ...extra }
}

function item(id: string, text: string, status: QueuedInput['status'] = 'pending'): QueuedInput {
  return { id, text, attachments: [], status, options: OPTS, enqueuedAt: 1 }
}

/**
 * ★ 队列的**真源在主进程**(`main/session-runtime.ts`,语义由那边的测试钉住)。
 * 渲染层这一半只有三件事:把意图原样交过去、按 `rev` 收回执、**自己绝不续跑** ——
 * 没有窗口在看这条会话时,续跑也得发生,所以它不能长在渲染层。
 */
describe('排队:渲染层只交意图,不自己续跑', () => {
  it('生成中再发一条:交给主进程入队,带上附件与当时的档位,不并发第二个 run', async () => {
    const IMG = 'ncw://attachments/sessions/s-queue/01J8A.png'
    mockQueue.mockImplementationOnce(async (sessionId) => receipt(sessionId, [item('q1', '第二条')]))
    const s = session('s-queue')
    await s.getState().send('第一条', OPTS)
    const runId = s.getState().activeRunId
    s.getState().setDraft('第二条')

    await s.getState().send('第二条', { ...OPTS, model: 'sonnet' }, [
      { type: 'text', text: '第二条' },
      { type: 'image', mime: 'image/png', dataRef: IMG }
    ])

    expect(mockStartRun).toHaveBeenCalledTimes(1)
    expect(mockQueue).toHaveBeenCalledWith('s-queue', {
      kind: 'enqueue',
      text: '第二条',
      options: { ...OPTS, model: 'sonnet' },
      attachments: [{ kind: 'image', name: '01J8A.png', url: IMG }]
    })
    expect(s.getState().queuedInputs.map((q) => q.text)).toEqual(['第二条'])
    expect(s.getState().draft).toBe('')
    expect(s.getState().activeRunId).toBe(runId)
    expect(indexed('s-queue')).toEqual([runId])
  })

  it('★ run 正常结束时渲染层不发下一条 —— 那是主进程的事', async () => {
    mockQueue.mockImplementationOnce(async (sessionId) => receipt(sessionId, [item('q1', '第二条')]))
    const s = session('s-no-drain')
    await s.getState().send('第一条', OPTS)
    await s.getState().send('第二条', OPTS)

    s.getState().applyEvents([runEnd])
    await Promise.resolve()

    expect(mockStartRun).toHaveBeenCalledTimes(1)
    expect(s.getState().activeRunId).toBeNull()
  })

  it('主进程那边已经排满:话还给输入框,不让它凭空消失', async () => {
    mockQueue.mockImplementationOnce(async (sessionId) => receipt(sessionId, [], { accepted: false }))
    const s = session('s-full')
    await s.getState().send('第一条', OPTS)
    s.getState().setDraft('第二条')

    await s.getState().send('第二条', OPTS)

    expect(s.getState().draft).toBe('第二条')
  })

  it('插话、编辑、删除、改档位、继续:原样交给主进程,并采用回执', async () => {
    const s = session('s-ops')
    s.setState({ queuedInputs: [item('a', '甲'), item('b', '乙')], queueRev: 0 })
    const ops: SessionQueueOp[] = [
      { kind: 'promote', id: 'b' },
      { kind: 'edit', id: 'a', text: '改过的' },
      { kind: 'drop', id: 'a' },
      { kind: 'retagPermission', mode: 'full' },
      { kind: 'retagMode', mode: 'code' },
      { kind: 'resume' }
    ]
    mockQueue.mockImplementation(async (sessionId) => receipt(sessionId, [item('b', '乙', 'promoted')]))

    s.getState().promoteInput('b')
    s.getState().editInput('a', '改过的')
    s.getState().dropInput('a')
    s.getState().retagQueuedPermission('full')
    s.getState().retagQueuedMode('code')
    resumeQueue('s-ops')

    await vi.waitFor(() => expect(mockQueue).toHaveBeenCalledTimes(ops.length))
    expect(mockQueue.mock.calls.map((call) => call[1])).toEqual(ops)
    await vi.waitFor(() => expect(s.getState().queuedInputs).toEqual([item('b', '乙', 'promoted')]))
  })

  it('撤回到输入框:主进程交回文本,接在草稿后面,不覆盖正在写的半句话', async () => {
    const s = session('s-recall')
    s.setState({ queuedInputs: [item('a', '排队的')], queueRev: 0 })
    s.getState().setDraft('写了一半')
    mockQueue.mockImplementationOnce(async (sessionId) => receipt(sessionId, [], { text: '排队的' }))

    s.getState().moveInputToDraft('a')

    await vi.waitFor(() => expect(s.getState().draft).toBe('写了一半\n\n排队的'))
    expect(s.getState().queuedInputs).toEqual([])
  })

  it('★ 回执与广播乱序:只收 rev 更新的那份', async () => {
    const s = session('s-rev')
    s.setState({ queuedInputs: [item('a', '甲')], queueRev: 5 })
    mockQueue.mockImplementationOnce(async (sessionId) => ({ sessionId, queued: [], rev: 4, accepted: true }))

    s.getState().dropInput('a')
    await vi.waitFor(() => expect(mockQueue).toHaveBeenCalledTimes(1))
    await Promise.resolve()

    // 比手上旧的回执被丢掉,而不是把队列回滚到过去
    expect(s.getState().queuedInputs.map((q) => q.text)).toEqual(['甲'])
  })

  it('★ 主进程说这条会话其实在跑:撤掉乐观消息,接上真正在跑的那个 run', async () => {
    const s = session('s-busy-main')
    vi.mocked(attachRun).mockResolvedValue({
      runId: 'r-main', sessionId: 's-busy-main', workspaceId: 'w1', depth: 0,
      status: 'running', seq: 0, events: [], pendingInteractions: [], children: []
    })
    mockStartRun.mockImplementationOnce(async () => {
      // 角标广播先到:本地还挂着乐观 runId,`adoptActiveRuns` 只登记索引、不接 store
      adoptActiveRuns([{ runId: 'r-main', sessionId: 's-busy-main', workspaceId: 'w1' }])
      return { started: false, queue: receipt('s-busy-main', [item('q1', '你好')]) }
    })

    await s.getState().send('你好', OPTS)

    expect(s.getState().transcript.messages).toEqual([])
    expect(s.getState().queuedInputs.map((q) => q.text)).toEqual(['你好'])
    expect(s.getState().activeRunId).toBe('r-main')
  })
})

describe('停止', () => {
  it('点停止只发 abort,状态等 run_end 回来才改 —— 抢先置空就看不到收尾过程', async () => {
    const s = session('s-stop')
    await s.getState().send('你好', OPTS)
    const runId = s.getState().activeRunId

    await s.getState().stop()

    expect(mockAbortRun).toHaveBeenCalledWith(runId, true)
    expect(s.getState().activeRunId).toBe(runId)
    expect(indexed('s-stop')).toEqual([runId])
  })

  it('空闲时点停止是空操作,不会误发 abort', async () => {
    const s = session('s-stop-idle')
    await s.getState().stop()
    expect(mockAbortRun).not.toHaveBeenCalled()
  })
})
