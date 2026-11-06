/**
 * 没人在看的会话不留转录、不收正文 —— Stage 2 的渲染层一半。
 *
 * 钉三件事:
 * 1. **视图持有**:一个会话 store 只在有视图(对话视图 / 子代理只读面板)挂着时常驻;
 *    最后一个视图走了,宽限期过后放掉 —— 包括正在跑的那些,并摘掉它的正文订阅。
 *    run 照跑、运行中索引(角标)不动,再打开时按历史 + 快照重建。
 * 2. **窗口藏着**:不 attach、不收正文;露出来时把在看的会话整段重建。
 * 3. **「等你处理」**:没有正文时,运行中索引里的待处理数是应用里唯一能看出来的地方。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunSnapshot } from '../../../../shared/agent/event'
import type { AgentEventEnvelope } from '../../../../shared/ipc/contract'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(async () => ({ started: true })),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}),
  unwatchRun: vi.fn(async () => {}),
  onAgentEvent: vi.fn(() => () => {}),
  onActiveRuns: vi.fn(() => () => {}),
  onSessionQueueChanged: vi.fn(() => () => {}),
  onSubagentReport: vi.fn(() => () => {}),
  onWindowVisibility: vi.fn(() => () => {})
}))
vi.mock('../../services/app', () => ({
  getSessionInput: vi.fn(async () => null), persistSessionDraft: vi.fn()
}))
vi.mock('../../services/sessions', () => {
  const getSession = vi.fn()
  return {
    getSession,
    // 转录按页读(`getSessionPage`):委托给各用例摆好的整段历史,一页就是全部
    getSessionPage: vi.fn(async (sessionId: string) => {
      const detail = await getSession(sessionId)
      return detail == null ? detail : { ...detail, hasMore: false }
    }),
  replaceHistory: vi.fn(async () => {})
  }
})
vi.mock('../../services/goal', () => ({ getGoal: vi.fn(async () => undefined), onGoalChanged: vi.fn(() => () => {}) }))

import { attachRun, onAgentEvent, onWindowVisibility, unwatchRun } from '../../services/agent'
import { getSession } from '../../services/sessions'
import {
  SESSION_IDLE_RELEASE_MS,
  adoptActiveRuns,
  releaseSession,
  retainSessionView,
  sessionStore,
  startAgentEventPump,
  syncActiveRuns,
  useRunIndex
} from '../session'

const snapshot = (runId: string, sessionId: string, over: Partial<RunSnapshot> = {}): RunSnapshot => ({
  runId, sessionId, workspaceId: 'w1', depth: 0, status: 'running', seq: 0,
  pendingInteractions: [], children: [], events: [], ...over
})

let stopPump: (() => void) | null = null
const created: string[] = []

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(0), 0) as unknown as number)
  vi.stubGlobal('cancelAnimationFrame', (id: number) => clearTimeout(id))
  vi.mocked(getSession).mockImplementation(async (id: string) => ({ session: { id } as never, messages: [] }))
  vi.mocked(attachRun).mockImplementation(async (runId: string) => snapshot(runId, 'unknown'))
})

afterEach(() => {
  stopPump?.()
  stopPump = null
  useRunIndex.setState([], true)
  syncActiveRuns([])
  for (const id of created) releaseSession(id)
  created.length = 0
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

function open(id: string): ReturnType<typeof sessionStore> {
  created.push(id)
  return sessionStore(id)
}

describe('视图持有', () => {
  it('没有视图接的 store,宽限期之后放掉 —— 再取回来是全新的', () => {
    open('idle').setState({ draft: '没持久化的本地值' })

    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS - 1)
    expect(sessionStore('idle').getState().draft).toBe('没持久化的本地值')

    vi.advanceTimersByTime(1)
    expect(sessionStore('idle').getState().draft).toBe('')
  })

  it('视图挂着就一直留着;最后一个视图走了才开始计时', () => {
    open('viewed').setState({ draft: 'x' })
    const first = retainSessionView('viewed')
    const second = retainSessionView('viewed')

    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS * 3)
    first()
    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS * 3)
    expect(sessionStore('viewed').getState().draft).toBe('x')

    second()
    second() // 释放函数幂等:重复调用不会把别人的持有也减掉
    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS)
    expect(sessionStore('viewed').getState().draft).toBe('')
  })

  it('宽限期内又被持有(来回切 Tab):不放', () => {
    open('back').setState({ draft: 'x' })
    const release = retainSessionView('back')
    release()
    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS / 2)
    const again = retainSessionView('back')
    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS * 2)
    expect(sessionStore('back').getState().draft).toBe('x')
    again()
  })

  it('★★ 正在跑的会话也放:摘掉正文订阅,角标不动;再打开时按快照接回来', async () => {
    adoptActiveRuns([{ runId: 'r-live', sessionId: 'live', workspaceId: 'w1' }])
    const store = open('live')
    expect(store.getState().activeRunId).toBe('r-live')
    await vi.advanceTimersByTimeAsync(0)
    vi.mocked(attachRun).mockClear()

    vi.advanceTimersByTime(SESSION_IDLE_RELEASE_MS)

    expect(unwatchRun).toHaveBeenCalledWith('r-live')
    // run 属于主进程:运行中索引(三处角标)一个字都不动
    expect(useRunIndex.getState().map((run) => run.runId)).toEqual(['r-live'])

    const reopened = sessionStore('live')
    expect(reopened).not.toBe(store)
    expect(reopened.getState().activeRunId).toBe('r-live')
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalledWith('r-live', 0))
  })
})

describe('窗口藏着', () => {
  function pump(): { deliver: (env: AgentEventEnvelope) => void; setVisible: (visible: boolean) => void } {
    stopPump = startAgentEventPump()
    const deliver = vi.mocked(onAgentEvent).mock.calls[0]?.[0]
    const setVisible = vi.mocked(onWindowVisibility).mock.calls[0]?.[0]
    if (deliver === undefined || setVisible === undefined) throw new Error('事件泵没有订阅')
    return { deliver, setVisible }
  }

  it('★ 藏着时到达的正文不收,也不为新 run attach;露出来时整段重建', async () => {
    const { deliver, setVisible } = pump()
    const store = open('hidden')
    const release = retainSessionView('hidden')
    await vi.advanceTimersByTimeAsync(0)

    setVisible(false)
    // 藏着期间主进程续上了排队的下一轮:角标照常更新,但不 attach
    adoptActiveRuns([{ runId: 'r-next', sessionId: 'hidden', workspaceId: 'w1' }])
    deliver({ runId: 'r-next', seq: 1, events: [{ type: 'stream', delta: { type: 'text_delta', index: 0, text: '藏着的时候' } }] })
    await vi.advanceTimersByTimeAsync(100)

    expect(store.getState().activeRunId).toBe('r-next')
    expect(attachRun).not.toHaveBeenCalled()
    expect(store.getState().transcript.live).toEqual([])

    vi.mocked(attachRun).mockResolvedValueOnce(snapshot('r-next', 'hidden', {
      seq: 2,
      events: [{ type: 'stream', delta: { type: 'text_delta', index: 0, text: '完整的一段' } }]
    }))
    setVisible(true)
    // 从 0 重建,不是从 lastSeq 续 —— 藏着那段时间主进程的日志可能已经截掉了头
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalledWith('r-next', 0))
    release()
  })

  it('藏着期间收尾的 run:离开运行态,不 attach;露出来时从库里重读', async () => {
    const { setVisible } = pump()
    adoptActiveRuns([{ runId: 'r-end', sessionId: 'ending', workspaceId: 'w1' }])
    const store = open('ending')
    const release = retainSessionView('ending')
    await vi.advanceTimersByTimeAsync(0)
    vi.mocked(attachRun).mockClear()
    vi.mocked(getSession).mockClear()

    setVisible(false)
    syncActiveRuns([])
    await vi.advanceTimersByTimeAsync(1_000)

    expect(store.getState().activeRunId).toBeNull()
    expect(attachRun).not.toHaveBeenCalled()

    setVisible(true)
    await vi.waitFor(() => expect(getSession).toHaveBeenCalledWith('ending'))
    release()
  })
})

describe('「等你处理」', () => {
  it('★ 已在索引里的 run 跟着广播更新待处理数,应用内才有地方标出来', () => {
    syncActiveRuns([{ runId: 'r-ask', sessionId: 'asking', workspaceId: 'w1' }])
    expect(useRunIndex.getState()[0]?.pendingInteractions ?? 0).toBe(0)

    syncActiveRuns([{ runId: 'r-ask', sessionId: 'asking', workspaceId: 'w1', pendingInteractions: 1 }])
    expect(useRunIndex.getState()[0]?.pendingInteractions).toBe(1)

    syncActiveRuns([{ runId: 'r-ask', sessionId: 'asking', workspaceId: 'w1' }])
    expect(useRunIndex.getState()[0]?.pendingInteractions).toBe(0)
  })
})
