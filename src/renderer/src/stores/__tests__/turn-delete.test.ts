/**
 * 「删除这一轮」的跨度语义。
 *
 * ★ 这里量的是一件在界面上**看不出来**的事:一轮问答在存储里不是两条消息,
 * 而是 `user → assistant(tool_call) → user(tool_result 回执) → assistant …`
 * 这样一长串。删除只砍掉可见的那两条,留下来的 tool_result 就失去了配对的
 * tool_call —— 那对 Anthropic 形状是非法请求,而它要等到**下一次发消息**
 * 才炸,那时早已看不出是这次删除干的。所以每个用例都断言残留消息的完整 id 序列。
 */
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../shared/agent/message'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  onAgentEvent: vi.fn(() => () => {})
}))

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
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
  replaceHistory: vi.fn(async () => undefined),
    editSessionMessage: vi.fn(async () => undefined),
    deleteSessionTurn: vi.fn(async () => undefined),
    deleteSessionReply: vi.fn(async () => undefined),
  }
})

import { deleteSessionReply, deleteSessionTurn, replaceHistory } from '../../services/sessions'
import { releaseSession, sessionStore } from '../session'

const mockReplaceHistory = vi.mocked(replaceHistory)
const mockDeleteTurn = vi.mocked(deleteSessionTurn)
const mockDeleteReply = vi.mocked(deleteSessionReply)
/** 什么都没往主进程送:既没有按 id 删,也没有整段改写 */
function expectNothingPersisted(): void {
  expect(mockDeleteTurn).not.toHaveBeenCalled()
  expect(mockDeleteReply).not.toHaveBeenCalled()
  expect(mockReplaceHistory).not.toHaveBeenCalled()
}

/** 一轮带工具调用的问答:提问 → 调工具 → 回执 → 收尾发言。 */
const turn = (n: string) => [
  userMessage(`u${n}`, [{ type: 'text', text: `问题 ${n}` }], 1),
  assistantMessage(`a${n}`, [{ type: 'tool_call', callId: `c${n}`, name: 'Read', input: {} }], 2),
  toolResultMessage(`r${n}`, [{ type: 'tool_result', callId: `c${n}`, output: { content: 'ok' }, isError: false }], 3),
  assistantMessage(`b${n}`, [{ type: 'text', text: `回答 ${n}` }], 4)
]

const created: string[] = []
function seeded(id: string, messages: ReturnType<typeof turn>): ReturnType<typeof sessionStore> {
  created.push(id)
  const store = sessionStore(id)
  store.setState((s) => ({ transcript: { ...s.transcript, messages } }))
  return store
}

beforeEach(() => { vi.clearAllMocks() })

afterEach(() => {
  for (const id of created) {
    sessionStore(id).setState({ activeRunId: null })
    releaseSession(id)
  }
  created.length = 0
})

describe('删除一整轮', () => {
  it('连同工具回执一起删,不留下失去配对的 tool_result', async () => {
    const s = seeded('d-mid', [...turn('1'), ...turn('2')])

    await s.getState().deleteTurn('u1')

    expect(s.getState().transcript.messages.map((m) => m.id))
      .toEqual(['u2', 'a2', 'r2', 'b2'])
  })

  it('删中间一轮时保留它后面的对话 —— 删除不是截断', async () => {
    const s = seeded('d-keep-tail', [...turn('1'), ...turn('2'), ...turn('3')])

    await s.getState().deleteTurn('u2')

    expect(s.getState().transcript.messages.map((m) => m.id))
      .toEqual(['u1', 'a1', 'r1', 'b1', 'u3', 'a3', 'r3', 'b3'])
  })

  it('删末轮时清掉 usage/error —— 它们说的是一个已经不存在的回合', async () => {
    const s = seeded('d-tail', [...turn('1'), ...turn('2')])
    s.setState((state) => ({
      transcript: {
        ...state.transcript,
        usage: { inputTokens: 10, outputTokens: 20 },
        error: { code: 'unknown', message: '炸了', retryable: false }
      }
    }))

    await s.getState().deleteTurn('u2')

    expect(s.getState().transcript.usage).toBeUndefined()
    expect(s.getState().transcript.error).toBeUndefined()
  })

  it('删非末轮时不动 usage —— 那是最后一轮的读数,与被删的这轮无关', async () => {
    const s = seeded('d-mid-usage', [...turn('1'), ...turn('2')])
    s.setState((state) => ({
      transcript: { ...state.transcript, usage: { inputTokens: 10, outputTokens: 20 } }
    }))

    await s.getState().deleteTurn('u1')

    expect(s.getState().transcript.usage).toEqual({ inputTokens: 10, outputTokens: 20 })
  })

  /*
    删除之后**上下文圆环上的数**要跟着作废 —— 这里量的是一件用户会直接看见的事:
    把消息删光,圆环却还挂着「81K / 272K」,因为那个分子是上一次请求报回来的
    输入 token,而删除不产生请求。它和 usage 的判据不同:usage 是最后一轮的账单
    (删中间一轮与它无关),窗口占用则被任何一次删除改写。
  */
  it('删任意一轮都清掉窗口占用读数 —— 它描述的那段历史已经不存在了', async () => {
    const s = seeded('d-context-mid', [...turn('1'), ...turn('2')])
    s.setState((state) => ({
      transcript: {
        ...state.transcript,
        lastInputTokens: 81_000,
        contextUsage: { used: 81_000, window: 272_000, shouldCompact: false }
      }
    }))

    await s.getState().deleteTurn('u1')

    expect(s.getState().transcript.lastInputTokens).toBeUndefined()
    expect(s.getState().transcript.contextUsage).toBeUndefined()
  })

  it('删光所有消息后圆环回到「还不知道」,而不是停在删除前的读数', async () => {
    const s = seeded('d-context-all', [...turn('1')])
    s.setState((state) => ({ transcript: { ...state.transcript, lastInputTokens: 81_000 } }))

    await s.getState().deleteTurn('u1')

    expect(s.getState().transcript.messages).toHaveLength(0)
    expect(s.getState().transcript.lastInputTokens).toBeUndefined()
  })

  it('把删除结果落盘 —— 只改内存的话,重开会话删掉的内容会原样回来', async () => {
    const s = seeded('d-persist', [...turn('1'), ...turn('2')])

    await s.getState().deleteTurn('u1')

    // ★ 按 id 交给主进程在完整历史上删 —— 渲染层手里可能只有一页,不能拿它整段改写
    expect(mockDeleteTurn).toHaveBeenCalledWith('d-persist', 'u1')
    expect(mockReplaceHistory).not.toHaveBeenCalled()
  })

  it('运行中拒绝删除 —— 主进程正往这段历史里追加消息', async () => {
    const s = seeded('d-running', [...turn('1')])
    s.setState({ activeRunId: 'run-1' })

    await s.getState().deleteTurn('u1')

    expect(s.getState().transcript.messages).toHaveLength(4)
    expectNothingPersisted()
  })

  it('id 对不上时是空操作,不会误删别的东西', async () => {
    const s = seeded('d-missing', [...turn('1')])

    await s.getState().deleteTurn('u-nope')

    expect(s.getState().transcript.messages).toHaveLength(4)
    expectNothingPersisted()
  })

  it('传的是助手消息 id 时也是空操作 —— 锚点只能是提问', async () => {
    const s = seeded('d-assistant-anchor', [...turn('1')])

    await s.getState().deleteTurn('b1')

    expect(s.getState().transcript.messages).toHaveLength(4)
    expectNothingPersisted()
  })
})

describe('只删一条回复', () => {
  it('提问留着,回复连同工具回执一起删', async () => {
    const s = seeded('r-mid', [...turn('1'), ...turn('2')])

    await s.getState().deleteReply('a1', 'b1')

    expect(s.getState().transcript.messages.map((m) => m.id))
      .toEqual(['u1', 'u2', 'a2', 'r2', 'b2'])
    expect(mockDeleteReply).toHaveBeenCalledWith('r-mid', 'a1', 'b1')
  })

  it('回复以 tool_call 收尾时吃掉紧随其后的回执,不留失配的 tool_result', async () => {
    const s = seeded('r-dangling', [
      userMessage('u1', [{ type: 'text', text: '问题' }], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'c1', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'c1', output: { content: 'ok' }, isError: false }], 3),
      ...turn('2')
    ])

    await s.getState().deleteReply('a1', 'a1')

    expect(s.getState().transcript.messages.map((m) => m.id))
      .toEqual(['u1', 'u2', 'a2', 'r2', 'b2'])
  })

  it('后台汇报引出的回复也能删,汇报本身留着', async () => {
    const report = { ...userMessage('rep', [{ type: 'text', text: '子代理结果' }], 5), internal: true }
    const s = seeded('r-report', [
      ...turn('1'),
      report,
      assistantMessage('x1', [{ type: 'text', text: '收到汇报' }], 6)
    ])

    await s.getState().deleteReply('x1', 'x1')

    expect(s.getState().transcript.messages.map((m) => m.id))
      .toEqual(['u1', 'a1', 'r1', 'b1', 'rep'])
  })

  it('删末尾那条回复时清掉 error —— 它说的是已经不存在的回复', async () => {
    const s = seeded('r-tail', [...turn('1')])
    s.setState((state) => ({
      transcript: { ...state.transcript, error: { code: 'unknown', message: '炸了', retryable: false } }
    }))

    await s.getState().deleteReply('a1', 'b1')

    expect(s.getState().transcript.messages.map((m) => m.id)).toEqual(['u1'])
    expect(s.getState().transcript.error).toBeUndefined()
  })

  it('锚点不是助手消息、或顺序颠倒时是空操作', async () => {
    const s = seeded('r-bad', [...turn('1')])

    await s.getState().deleteReply('u1', 'b1')
    await s.getState().deleteReply('b1', 'a1')

    expect(s.getState().transcript.messages).toHaveLength(4)
    expectNothingPersisted()
  })

  it('运行中拒绝删除', async () => {
    const s = seeded('r-running', [...turn('1')])
    s.setState({ activeRunId: 'run-1' })

    await s.getState().deleteReply('a1', 'b1')

    expect(s.getState().transcript.messages).toHaveLength(4)
    expectNothingPersisted()
  })
})
