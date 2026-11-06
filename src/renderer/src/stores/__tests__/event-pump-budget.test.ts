/**
 * 事件泵的**有界预算**。
 *
 * ★ 要防的那件事:窗口隐藏时不再派发 rAF,而 `pending` 只增不减 —— 一个跑很久的
 * 后台 run 能把渲染进程撑爆,恢复可见那一刻还要一次性吞下几万条。所以这里钉三样:
 *
 * 1. **条数上限**:一帧内攒够就立刻派发,不等帧;
 * 2. **字符上限**:单个超长 `text_delta` 也能触发派发(条数上限挡不住它);
 * 3. **可见性 / 兜底定时器**:即使一帧都没有,超时也强制派发;隐藏期间照常落地。
 *
 * 溢出这一路走的就是平常那条 `drain()` —— 按 runId 投递、seq 断流由
 * `applyEnvelope` 的检查和 attach 补齐收敛。这里用「溢出后事件真的进了 store」
 * 来钉「没有只留下半个信封」。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent, RunSnapshot } from '../../../../shared/agent/event'
import type { AgentEventEnvelope } from '../../../../shared/ipc/contract'
import { liveText } from '../../../../shared/agent/transcript'
import { assistantMessage } from '../../../../shared/agent/message'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(), attachRun: vi.fn(), abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}),
  onAgentEvent: vi.fn(() => () => {}),
  onActiveRuns: vi.fn(() => () => {}),
  onSessionQueueChanged: vi.fn(() => () => {}),
  onSubagentReport: vi.fn(() => () => {}),
  onWindowVisibility: vi.fn(() => () => {}),
  unwatchRun: vi.fn(async () => {})
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
      return detail == null ? detail : { ...(detail as object), hasMore: false }
    }),
  }
})
vi.mock('../../services/goal', () => ({ getGoal: vi.fn(async () => undefined), onGoalChanged: vi.fn(() => () => {}) }))

import { attachRun, onAgentEvent } from '../../services/agent'
import { getSession } from '../../services/sessions'
import { adoptActiveRuns, releaseSession, retainSessionView, sessionStore, startAgentEventPump } from '../session'

const RUN = 'budget-run'
const SESSION = 'budget-session'

let deliver: (env: AgentEventEnvelope) => void = () => {}
/** 受控的 rAF 队列 —— 窗口「隐藏」= 这里永远不跑 */
let frames: FrameRequestCallback[] = []
/** 受控的定时器:**带 id**,因为 clearTimeout 必须真的能取消(tested 行为之一) */
let timers = new Map<number, () => void>()
let timerSeq = 0
let stopPump: (() => void) | null = null
/** 这条会话「有一个视图挂着」—— 真实应用里是 ChatView。没有它,空闲回收的定时器会混进下面的定时器账本 */
let releaseView: (() => void) | null = null

function runFrames(): void {
  const queued = frames
  frames = []
  for (const cb of queued) cb(0)
}
function runTimers(): void {
  const queued = [...timers.values()]
  timers.clear()
  for (const cb of queued) cb()
}
function textEnvelope(seq: number, text: string): AgentEventEnvelope {
  return { runId: RUN, seq, events: [{ type: 'stream', delta: { type: 'text_delta', index: 0, text } }] }
}

beforeEach(() => {
  vi.clearAllMocks()
  frames = []
  timers = new Map()
  timerSeq = 0
  // ★ rAF 与 setTimeout 都改成**手工触发**:这样「窗口隐藏」(不跑帧)就是这条用例
  //   里一个明确的动作,而不是靠环境碰运气。
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { frames.push(cb); return frames.length })
  vi.stubGlobal('cancelAnimationFrame', () => { frames = [] })
  vi.stubGlobal('setTimeout', (cb: () => void) => {
    timerSeq += 1
    timers.set(timerSeq, cb)
    return timerSeq
  })
  vi.stubGlobal('clearTimeout', (id: number) => { timers.delete(id) })

  vi.mocked(getSession).mockImplementation(async (id: string) => ({
    session: { id, workspaceId: 'workspace', title: 'T', model: 'm', mode: 'normal', thinking: 'auto',
      rootPathAtCreation: '/w', status: 'running', archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
    messages: []
  }))
  vi.mocked(attachRun).mockImplementation(async (runId: string): Promise<RunSnapshot> => ({
    runId, sessionId: SESSION, workspaceId: 'workspace', depth: 0, status: 'running',
    seq: 0, pendingInteractions: [], children: [], events: []
  }))

  stopPump = startAgentEventPump()
  const listener = vi.mocked(onAgentEvent).mock.calls[0]?.[0]
  if (listener === undefined) throw new Error('事件泵没有订阅 —— 后面每条用例都会假绿')
  deliver = listener
})

afterEach(() => {
  stopPump?.()
  stopPump = null
  releaseView?.()
  releaseView = null
  sessionStore(SESSION).getState().applyEvents([{ type: 'run_end', status: 'aborted' }])
  releaseSession(SESSION)
  vi.unstubAllGlobals()
})

async function attachSession(): Promise<void> {
  adoptActiveRuns([{ runId: RUN, sessionId: SESSION, workspaceId: 'workspace' }])
  sessionStore(SESSION)
  releaseView = retainSessionView(SESSION)
  await vi.waitFor(() => expect(sessionStore(SESSION).getState().activeRunId).toBe(RUN))
}

describe('事件泵预算', () => {
  it('条数越界:不等帧,立刻派发(窗口隐藏也照常落地)', async () => {
    await attachSession()
    // 窗口隐藏:一帧都不会跑。攒够上限必须自己走
    for (let index = 0; index < 300; index++) deliver(textEnvelope(index + 1, 'x'))
    // 一次都没跑帧,内容却已经落进 store —— 说明溢出那一支真的派发了
    expect(frames.length).toBeGreaterThan(0)
    expect(liveText(sessionStore(SESSION).getState().transcript)).toContain('x')
  })

  it('字符越界:一条超长 text_delta 就触发派发(条数上限挡不住它)', async () => {
    await attachSession()
    deliver(textEnvelope(1, 'a'.repeat(1_200_000)))
    expect(liveText(sessionStore(SESSION).getState().transcript).length).toBe(1_200_000)
  })

  it('提交的整段消息也受字符预算约束', async () => {
    await attachSession()
    const message = assistantMessage('large-commit', [{ type: 'text', text: 'a'.repeat(1_200_000) }], 1)
    deliver({ runId: RUN, seq: 1, events: [{ type: 'message_commit', message }] })
    expect(sessionStore(SESSION).getState().transcript.messages.some((item) => item.id === message.id)).toBe(true)
    expect(frames.length + timers.size).toBe(0)
  })

  it('提前派发撤销旧帧和定时器，隐藏时不积累回调', async () => {
    await attachSession()
    deliver(textEnvelope(1, 'small'))
    expect(frames.length + timers.size).toBe(2)
    deliver(textEnvelope(2, 'a'.repeat(1_200_000)))
    expect(frames.length + timers.size).toBe(0)
    deliver({ runId: RUN, seq: 3, events: [{ type: 'run_end', status: 'done' }] })
    expect(sessionStore(SESSION).getState().activeRunId).toBeNull()
    expect(frames.length + timers.size).toBe(0)
  })

  it('兜底定时器:隐藏期间到点强制派发一次', async () => {
    await attachSession()
    deliver(textEnvelope(1, 'hidden'))
    // rAF 被「隐藏」住了,内容还没进 store
    expect(liveText(sessionStore(SESSION).getState().transcript)).toBe('')
    runTimers()
    expect(liveText(sessionStore(SESSION).getState().transcript)).toBe('hidden')
  })

  it('恢复可见:积压的信封按帧一次性应用,不留下半个', async () => {
    await attachSession()
    for (let index = 0; index < 200; index++) deliver(textEnvelope(index + 1, `${index % 10}`))
    runFrames()
    runTimers()
    const text = liveText(sessionStore(SESSION).getState().transcript)
    expect(text.length).toBe(200)
    // 每条都在(顺序也按 seq),没有被拆开的信封
    expect(text.endsWith('9')).toBe(true)
  })

  it('run_end 落在溢出批次里也照样收敛:会话离开运行态', async () => {
    await attachSession()
    for (let index = 0; index < 300; index++) deliver(textEnvelope(index + 1, 'x'))
    deliver({ runId: RUN, seq: 301, events: [{ type: 'run_end', status: 'done' } as AgentEvent] })
    runFrames()
    runTimers()
    expect(sessionStore(SESSION).getState().activeRunId).toBeNull()
  })

  it('cleanup 退订后不再有定时器/帧在跑', async () => {
    await attachSession()
    deliver(textEnvelope(1, 'x'))
    expect(frames.length + timers.size).toBeGreaterThan(0)
    stopPump?.()
    stopPump = null
    expect(frames.length + timers.size).toBe(0)
  })
})
