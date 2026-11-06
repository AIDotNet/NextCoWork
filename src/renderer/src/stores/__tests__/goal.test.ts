/**
 * `applyGoalChange` —— 目标状态跨到渲染层的唯一入口。
 *
 * 这个函数要做的事只有两件,而两件都容易悄悄坏:
 *
 * 1. **状态更新必须留在状态里。** 把标记写成一条 `message_commit` 事件是「顺手」
 *    的做法(事件泵本来就是修转录的那条路),代价是它会把正在流的活跃块一起清掉 ——
 *    用户正在看的半句话凭空消失,而屏幕上不会有任何异常。所以这里同时断言
 *    「标记合进了那条已有的助手消息」和「`transcript.live` 一个字没动」。
 *
 * 2. **渲染层绝不替目标开跑。** 空闲时的目标检查由主进程的会话运行时直接起那一轮
 *    (`main/goal/runtime.ts` 的 `wakeGoal`,语义由 `main/__tests__/session-runtime.test.ts`
 *    钉住)—— 没有窗口在看这条会话时它也得发生。这里一条变更既不该建 store,
 *    也不该发出任何东西,哪怕它带着(旧主进程才会发的)`input`。
 *
 * 失败模式都是「不报错,只是做错」,所以每条断言都钉在具体的那一格状态上。
 *
 * `services/*` 整个替掉:它们背后是 `window.nextcowork`,node 环境里不存在。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent } from '../../../../shared/agent/event'
import type { AgentMessage, ContentPart } from '../../../../shared/agent/message'
import { assistantMessage, visibleText } from '../../../../shared/agent/message'
import { liveText } from '../../../../shared/agent/transcript'
import type { SendOptions } from '../../../../shared/agent/run-request'
import type { ActiveGoal } from '../../../../shared/domain/goal'
import { SESSION_INPUT_VERSION, makeQueuedInput } from '../../../../shared/domain/queued-input'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  // ★ 必须返回 promise:store 里是 `await interjectRun(...)` / `void ... .catch(...)`
  interjectRun: vi.fn(async () => {}),
  onAgentEvent: vi.fn(() => () => {})
}))

vi.mock('../../services/app', () => ({
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: vi.fn()
}))

vi.mock('../../services/sessions', () => {
  const getSession = vi.fn(async (_sessionId: string): Promise<unknown> => null)
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

vi.mock('../../services/goal', () => ({
  getGoal: vi.fn(),
  onGoalChanged: vi.fn(() => () => {})
}))

import { startRun } from '../../services/agent'
import { getGoal } from '../../services/goal'
import { getSession } from '../../services/sessions'
import { getSessionInput } from '../../services/app'
import { applyGoalChange, releaseSession, sessionStore, useRunIndex } from '../session'

const mockStartRun = vi.mocked(startRun)
const mockGetGoal = vi.mocked(getGoal)

/** 标注成 `SendOptions`,不要写 `as const` —— 队列条目要的是可变数组(同 session.test.ts) */
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

const GOAL: ActiveGoal = {
  id: 'goal-1',
  condition: '`bun test` 退出码为 0',
  origin: 'user',
  iterations: 0,
  setAt: 1,
  tokensAtStart: 0,
  checkinCount: 0
}

/** 已经提交在转录里的那条助手消息 —— 目标标记要盖在它身上 */
const ANSWERED = assistantMessage('a-1', [{ type: 'text', text: '先跑一遍测试' }], 1)
/** 带外到达的标记:同一个消息 id,但多一枚 `goal_status` */
const MARKER: AgentMessage = assistantMessage('a-1', [
  { type: 'text', text: '先跑一遍测试' },
  { type: 'goal_status', id: 'gs-1', met: true, condition: GOAL.condition, createdAt: 2 }
], 1)

const CHECKIN: ContentPart[] = [{ type: 'text', text: '目标检查:转录里还没看到测试输出。' }]

const textDelta = (text: string): AgentEvent => ({
  type: 'stream',
  delta: { type: 'text_delta', index: 0, text }
})

/**
 * 让挂着的 IPC promise 链走完。本文件里每一条「发了 / 没发」的断言都靠它 ——
 * 「没发」那一侧不会是空断言,因为同一条链在别的用例里被证明会真的发出去。
 */
const settle = async (): Promise<void> => {
  for (let i = 0; i < 3; i += 1) await new Promise((resolve) => setTimeout(resolve, 0))
}

const created: string[] = []

/** store 是模块级单例,用例之间必须自己收干净,否则顺序一换就红。 */
async function session(id: string): Promise<ReturnType<typeof sessionStore>> {
  created.push(id)
  const store = sessionStore(id)
  // 懒创建会顺手发起回填(输入 + 历史);不等它落地,后面的播种就可能被它盖掉
  await settle()
  return store
}

beforeEach(() => {
  vi.clearAllMocks()
  mockStartRun.mockResolvedValue({ started: true })
  mockGetGoal.mockResolvedValue(undefined)
  vi.mocked(getSessionInput).mockResolvedValue(null)
  // 回填历史会读一次库。给一份最小的详情,免得每次都在日志里留一条解析失败
  vi.mocked(getSession).mockResolvedValue({ session: { id: 'stub' } as never, messages: [] })
})

afterEach(() => {
  useRunIndex.setState([], true)
  for (const id of created) {
    sessionStore(id).setState({ activeRunId: null })
    releaseSession(id)
  }
  created.length = 0
})

describe('状态更新:留在状态里', () => {
  it('★★ 标记合并进已有的助手消息,正在流的活跃块一个字不动', async () => {
    const store = await session('g-merge')
    store.setState((s) => ({ transcript: { ...s.transcript, messages: [ANSWERED] } }))
    store.getState().applyEvents([textDelta('还在写')])
    expect(liveText(store.getState().transcript)).toBe('还在写')

    applyGoalChange({ sessionId: 'g-merge', message: MARKER, goal: GOAL })

    const messages = store.getState().transcript.messages
    // 追加而不是合并的话,同一条回复会在屏幕上出现两次
    expect(messages.map((m) => m.id)).toEqual(['a-1'])
    expect(visibleText(messages[0]!)).toBe('先跑一遍测试')
    expect(messages[0]!.parts.filter((p) => p.type === 'goal_status')).toHaveLength(1)
    // ★ 走 `message_commit` 那条路的话,活跃块会被一起清掉,这里就空了
    expect(liveText(store.getState().transcript)).toBe('还在写')
    expect(store.getState().goal).toEqual(GOAL)
    expect(store.getState().goalVersion).toBe(1)
  })

  it('标记指向一条转录里还没有的消息时,作为新消息补进去', async () => {
    const store = await session('g-append')

    applyGoalChange({ sessionId: 'g-append', message: MARKER, goal: GOAL })

    expect(store.getState().transcript.messages.map((m) => m.id)).toEqual(['a-1'])
    expect(store.getState().goalVersion).toBe(1)
  })

  it('★★ 之后又补来一条**旧的** message_commit,也不能把刚盖上的标记洗掉', async () => {
    const store = await session('g-old-commit')
    applyGoalChange({ sessionId: 'g-old-commit', message: MARKER, goal: GOAL })
    expect(store.getState().transcript.messages).toHaveLength(1)

    // 同一个 id 的那条提交是不带标记的 —— 整条替换就把它抹了
    store.getState().applyEvents([{ type: 'message_commit', message: ANSWERED }])

    const messages = store.getState().transcript.messages
    expect(messages.map((m) => m.id)).toEqual(['a-1'])
    expect(messages[0]!.parts.filter((p) => p.type === 'goal_status')).toHaveLength(1)
  })

  it('没有 message 时只动目标本身,消息流原样不动', async () => {
    const store = await session('g-state-only')
    store.setState((s) => ({ transcript: { ...s.transcript, messages: [ANSWERED] } }))

    applyGoalChange({ sessionId: 'g-state-only', goal: GOAL })

    expect(store.getState().goal).toEqual(GOAL)
    expect(store.getState().transcript.messages.map((m) => m.id)).toEqual(['a-1'])
  })
})

describe('目标检查不经过渲染层', () => {
  it('★ 带 input 的变更也不发:检查由主进程起,渲染层只更新目标状态', async () => {
    mockGetGoal.mockResolvedValue(GOAL)
    const store = await session('g-input')
    store.getState().setDraft('写了一半的草稿')

    applyGoalChange({ sessionId: 'g-input', goal: GOAL, input: { goalId: GOAL.id, parts: CHECKIN, options: OPTS } })
    await settle()

    expect(mockStartRun).not.toHaveBeenCalled()
    expect(store.getState().goal).toEqual(GOAL)
    expect(store.getState().draft).toBe('写了一半的草稿')
    expect(store.getState().transcript.messages).toEqual([])
  })

  it('★ 没打开过的会话不为一次目标变更建 store —— 关掉的会话不该被它重新拉进内存', async () => {
    applyGoalChange({ sessionId: 'g-closed', goal: GOAL, input: { goalId: GOAL.id, parts: CHECKIN, options: OPTS } })
    await settle()

    expect(mockStartRun).not.toHaveBeenCalled()
    // 建过 store 的话,懒创建会顺手发起历史回填
    expect(vi.mocked(getSession)).not.toHaveBeenCalledWith('g-closed')
  })
})

describe('不自动开跑', () => {
  it('★ 只有状态变更时绝不开跑,重启回填的队列与草稿原地不动', async () => {
    vi.mocked(getSessionInput).mockResolvedValue({
      v: SESSION_INPUT_VERSION,
      draft: '重启前写了一半',
      queued: [makeQueuedInput('q-1', '重启前排队的', OPTS, Date.now())],
      savedAt: Date.now()
    })
    const store = await session('g-restore')
    expect(store.getState().draft).toBe('重启前写了一半')
    expect(store.getState().queuedInputs.map((q) => q.text)).toEqual(['重启前排队的'])

    applyGoalChange({ sessionId: 'g-restore', message: MARKER, goal: GOAL })
    await settle()

    // 队列非空却没被抽走 —— 进程死亡是一次异常中断,续跑交还给用户
    expect(mockStartRun).not.toHaveBeenCalled()
    expect(store.getState().queuedInputs.map((q) => q.text)).toEqual(['重启前排队的'])
    expect(store.getState().draft).toBe('重启前写了一半')
  })
})
