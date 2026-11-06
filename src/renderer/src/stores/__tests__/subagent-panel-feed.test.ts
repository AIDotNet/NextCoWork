/**
 * 子代理的**双份投喂** —— 同一批事件同时喂给两个地方。
 *
 * 一次子 run 的事件,父会话和子会话要的是完全不同的两样东西:
 *
 * | 谁 | 喂什么 | 用来画什么 |
 * |---|---|---|
 * | 父会话 store | `applyChildEvents` | 那张 Task 卡片上的**遥测**(阶段、工具数、上下文占用) |
 * | 子会话 store | `applyEnvelope` | 右侧只读面板里那段**逐字转录** —— 和主智能体同一个函数,所以渲染也一模一样 |
 *
 * 而第二份**只在用户真的点开过那张卡片时**才喂(`openChildSession` 登记),
 * 不是每来一个 `subagent_start` 就建一份。一次编排能同时派出十几个后台子代理,
 * 其中绝大多数永远不会被点开 —— 无条件建的话,那是十几段完整转录白占在内存里。
 *
 * 所以这份文件的两条主用例是一对:开过面板 → 两边都动;没开过 → 只有卡片动,
 * 子会话的转录一个字都没有。第二条才是那句「免得每个后台子代理都白养一个」的验收。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent, RunSnapshot } from '../../../../shared/agent/event'
import { liveText } from '../../../../shared/agent/transcript'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(), attachRun: vi.fn(), abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}),
  onAgentEvent: vi.fn(() => () => {}),
  // 事件泵同时订阅「还有哪些 run 活着」、队列与后台汇报的广播,缺了任一条 `startAgentEventPump()` 当场抛
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
      return detail == null ? detail : { ...detail, hasMore: false }
    }),
  }
})
vi.mock('../../services/goal', () => ({ getGoal: vi.fn(async () => undefined), onGoalChanged: vi.fn(() => () => {}) }))

import type { AgentEventEnvelope } from '../../../../shared/ipc/contract'
import { attachRun, onAgentEvent } from '../../services/agent'
import { getSession } from '../../services/sessions'
import { adoptActiveRuns, openChildSession, releaseSession, sessionStore, startAgentEventPump } from '../session'

const PARENT_RUN = 'parent-run'
const PARENT_SESSION = 'parent-session'
const CHILD_RUN = 'child-run'
/** 权威来源是 `subagent_start.childSessionId`(主进程填的),渲染层从不自己拼 */
const CHILD_SESSION = `${PARENT_SESSION}:sub:${CHILD_RUN}`
const CALL_ID = 'task-1'

const started: AgentEvent = {
  type: 'subagent_start',
  callId: CALL_ID,
  childRunId: CHILD_RUN,
  childSessionId: CHILD_SESSION,
  description: '查配置读取处',
  subagentType: 'general-purpose',
  background: false,
  at: 10
}

/** 一批「子代理开始回话」的事件:父卡片看到阶段变了,子面板看到字出来了 */
const childEvents: AgentEvent[] = [
  { type: 'stream', delta: { type: 'message_start', model: 'deepseek-test' } },
  { type: 'stream', delta: { type: 'text_delta', index: 0, text: '我先看一眼 config.ts' } }
]

/** 空快照 —— 这份文件测的是实时那一半,历史补齐由 `session-restore.test.ts` 管 */
const emptySnapshot = (runId: string, sessionId: string): RunSnapshot => ({
  runId, sessionId, workspaceId: 'workspace', depth: 0, status: 'running',
  seq: 0, pendingInteractions: [], children: [], events: []
})

/** 把一批信封送进真正的事件泵 —— `drain` 是被测对象,不能绕过去 */
let deliver: (env: AgentEventEnvelope) => void = () => {}
let frames: FrameRequestCallback[] = []
let stopPump: (() => void) | null = null

/** 发一批信封,并把它攒的那一帧立刻跑掉 */
function send(env: AgentEventEnvelope): void {
  deliver(env)
  const queued = frames
  frames = []
  for (const cb of queued) cb(0)
}

beforeEach(() => {
  vi.clearAllMocks()
  /*
    ★ node 环境没有 rAF(这套测试跑在 node 下)。补一个**记下来、由 `send` 手动跑**
    的版本,而不是当场同步执行 —— 同步跑的话 `raf = requestAnimationFrame(drain)`
    会在 drain 结束**之后**把 raf 写回一个非 0 值,于是第二批事件永远等不到下一帧,
    测试看起来像「事件没送到」。攒帧是性能优化,不是被测语义,手动跑掉最省事。
  */
  frames = []
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => frames.push(cb))
  vi.stubGlobal('cancelAnimationFrame', () => {
    frames = []
  })

  vi.mocked(getSession).mockImplementation(async (id: string) => ({
    session: { id, workspaceId: 'workspace', title: 'T', model: 'deepseek-test', mode: 'normal',
      thinking: 'auto', rootPathAtCreation: '/workspace', status: 'running', archived: false,
      favorited: false, createdAt: 0, updatedAt: 1 },
    messages: []
  }))
  vi.mocked(attachRun).mockImplementation(async (runId: string) =>
    emptySnapshot(runId, runId === CHILD_RUN ? CHILD_SESSION : PARENT_SESSION))

  stopPump = startAgentEventPump()
  const listener = vi.mocked(onAgentEvent).mock.calls[0]?.[0]
  if (listener === undefined) throw new Error('事件泵没有订阅 —— 后面每条用例都会假绿')
  deliver = listener
})

afterEach(() => {
  stopPump?.()
  stopPump = null
  sessionStore(PARENT_SESSION).getState().applyEvents([{ type: 'run_end', status: 'aborted' }])
  sessionStore(CHILD_SESSION).getState().applyEvents([{ type: 'run_end', status: 'aborted' }])
  releaseSession(PARENT_SESSION)
  releaseSession(CHILD_SESSION)
  vi.unstubAllGlobals()
})

/** 建父会话、派一个子代理出去 —— 走的是 `drain` → `rememberChildRuns` 那条真路 */
async function dispatchChild(): Promise<void> {
  adoptActiveRuns([{ runId: PARENT_RUN, sessionId: PARENT_SESSION, workspaceId: 'workspace' }])
  const parent = sessionStore(PARENT_SESSION)
  await vi.waitFor(() => expect(parent.getState().activeRunId).toBe(PARENT_RUN))
  send({ runId: PARENT_RUN, seq: 1, events: [started] })
  await vi.waitFor(() => expect(parent.getState().transcript.subagents[CALL_ID]).toBeDefined())
}

describe('子代理事件 · 父卡片与右侧面板的双份投喂', () => {
  it('★ 点开过面板时,同一批事件既进卡片遥测,也进子会话的转录', async () => {
    await dispatchChild()

    openChildSession(CHILD_SESSION, CHILD_RUN)
    const child = sessionStore(CHILD_SESSION)
    // `openChildSession` 必须先把 activeRunId 指过去 —— `applyEnvelope` 拿它当门禁
    expect(child.getState().activeRunId).toBe(CHILD_RUN)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalledWith(CHILD_RUN, 0))

    send({ runId: CHILD_RUN, seq: 2, events: childEvents })

    // 父卡片:遥测动了
    const card = sessionStore(PARENT_SESSION).getState().transcript.subagents[CALL_ID]
    expect(card?.phase).toBe('thinking')
    expect(card?.lastEventAt).toBeDefined()

    // 子面板:逐字转录也到了,走的是和主智能体同一个 applyEnvelope
    expect(liveText(child.getState().transcript)).toBe('我先看一眼 config.ts')
  })

  /**
   * ★★ 没点开过 → 子会话一个字都不该长出来。
   *
   * 注意这里**故意先把子会话的 store 建出来**再发事件:门禁是
   * `openChildSession` 登记的那张转发表,不是「store 存不存在」。
   * 写成看 store 的话,任何一个碰过 `sessionStore(childSessionId)` 的地方
   * (比如以后某个列表页顺手取个标题)都会把十几段后台转录又养回来。
   */
  it('★ 没点开过面板时,只有卡片遥测在走 —— 子会话的转录是空的', async () => {
    await dispatchChild()

    const child = sessionStore(CHILD_SESSION)
    send({ runId: CHILD_RUN, seq: 2, events: childEvents })

    expect(sessionStore(PARENT_SESSION).getState().transcript.subagents[CALL_ID]?.phase).toBe('thinking')
    expect(liveText(child.getState().transcript)).toBe('')
    expect(child.getState().lastSeq).toBe(0)
  })

  /**
   * 子 run 结束时,转发表要跟着 `childRunIndex` 一起清掉。
   *
   * 不清的话,那条 runId 在主进程被回收、以后被复用时,新 run 的事件会喂进
   * 一段和它毫无关系的旧转录里。
   */
  it('子 run 结束后不再转发 —— 迟到的同 runId 信封两边都落不进去', async () => {
    await dispatchChild()
    openChildSession(CHILD_SESSION, CHILD_RUN)
    const child = sessionStore(CHILD_SESSION)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalledWith(CHILD_RUN, 0))

    // ★ `seq` 指的是**这批里最后一个**事件的序号(`hasSeqGap` 拿
    //   `seq - events.length + 1` 反推首条,要求它恰好等于 `lastSeq + 1`)。
    //   写成「批次号」的话整封会被判成断流、走 resync,断言看起来像事件没送到。
    send({ runId: CHILD_RUN, seq: 3, events: [...childEvents, { type: 'run_end', status: 'done' }] })
    expect(liveText(child.getState().transcript)).toBe('我先看一眼 config.ts')
    expect(sessionStore(PARENT_SESSION).getState().transcript.subagents[CALL_ID]?.status).toBe('done')

    /*
      迟到的那封两边都不该落:
      - `tool_start` 会把卡片的工具数 +1 —— 钉的是 `childRunIndex` 那一半
      - `text_delta` 会往面板里续字 —— 钉的是 `childSessionOfRun` 那一半
      两个 delete 写在同一个 if 里,这两条断言就是它俩各自的验收。
    */
    send({ runId: CHILD_RUN, seq: 5, events: [
      { type: 'tool_start', callId: 'late', toolName: 'Read', input: {} },
      { type: 'stream', delta: { type: 'text_delta', index: 0, text: '不该出现' } }
    ] })
    expect(liveText(child.getState().transcript)).not.toContain('不该出现')
    expect(sessionStore(PARENT_SESSION).getState().transcript.subagents[CALL_ID]?.toolCalls).toBe(0)
  })

  /** 早就跑完的子代理:库里那份就是全部,不该再 attach 一个已经回收的 run */
  it('子 run 已经不在索引里时,只开 store 不 attach', async () => {
    vi.mocked(attachRun).mockClear()
    openChildSession(CHILD_SESSION, 'long-gone-run')
    expect(attachRun).not.toHaveBeenCalled()
    expect(sessionStore(CHILD_SESSION).getState().activeRunId).toBeNull()
  })
})
