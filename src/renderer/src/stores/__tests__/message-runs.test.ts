/**
 * 「这条消息是哪个 run 产出的」必须在**流式路径上**就记下来,不能只靠
 * 下一次 hydrate 从 SQLite 回填。
 *
 * 这张映射是底部那张改动审查卡的唯一入口:`Thread` 把 `messageRuns[message.id]`
 * 当成回合的 runId 传下去,`TurnChangeReview` 拿不到 runId 就整卡 return null。
 * 回填是它以前的唯一来源,于是「这一轮改了 11 个文件」的卡片要等重开应用
 * (或切走会话再切回)才出现,而且全程零报错。逐轮用量、逐轮模型名同一条路。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, userMessage } from '../../../../shared/agent/message'
import type { SendOptions } from '../../../../shared/agent/run-request'
import { threadRows } from '../../views/chat/thread-content'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(async () => ({ started: true })), attachRun: vi.fn(), abortRun: vi.fn(), onAgentEvent: vi.fn(() => () => {})
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

import { getSession } from '../../services/sessions'
import { releaseSession, sessionStore } from '../session'

const OPTS: SendOptions = {
  workspaceId: 'workspace', depth: 0, mode: 'normal', thinking: 'auto',
  webSearch: false, permissionMode: 'ask', model: 'deepseek-test', skillIds: []
}

// 每个用例一个新会话 id:store 是模块级注册表,同名会话会把上一个用例
// 留下的转录(含还没收尾的 run)带进下一个。
let seq = 0
let session = ''

beforeEach(() => {
  vi.clearAllMocks()
  seq += 1
  session = `message-runs-${seq}`
  vi.mocked(getSession).mockResolvedValue({
    session: { id: session, workspaceId: 'workspace', title: 'Test', model: 'deepseek-test',
      mode: 'normal', thinking: 'auto', rootPathAtCreation: '/workspace', status: 'idle',
      archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
    messages: []
  })
})

afterEach(() => {
  releaseSession(session)
})

describe('message → run ownership while a turn streams', () => {
  it('records ownership from the envelope so the finished turn has a runId at once', async () => {
    const store = sessionStore(session)
    await vi.waitFor(() => expect(getSession).toHaveBeenCalled())
    await store.getState().send('改几个文件', OPTS)
    const runId = store.getState().activeRunId
    expect(runId).not.toBeNull()

    const answer = assistantMessage('a1', [{ type: 'text', text: '改完了' }], 1)
    store.getState().applyEnvelope({
      runId: runId ?? '',
      seq: 2,
      events: [{ type: 'message_commit', message: answer }, { type: 'run_end', status: 'done' }]
    })

    const { transcript } = store.getState()
    expect(transcript.messageRuns?.['a1']).toBe(runId)
    // 卡片真正读到的是这个:回合行上的 runId,不是 store 里那张表本身。
    const row = threadRows(transcript.messages, [], false, transcript.messageRuns ?? {})
      .findLast((r) => r.kind === 'assistant')
    expect(row?.kind === 'assistant' ? row.runId : undefined).toBe(runId)
  })

  it('keeps the mapping of earlier turns when the next one starts', async () => {
    const store = sessionStore(session)
    await vi.waitFor(() => expect(getSession).toHaveBeenCalled())
    await store.getState().send('第一轮', OPTS)
    const first = store.getState().activeRunId ?? ''
    store.getState().applyEnvelope({
      runId: first,
      seq: 2,
      events: [
        { type: 'message_commit', message: assistantMessage('a1', [{ type: 'text', text: '一' }], 1) },
        { type: 'run_end', status: 'done' }
      ]
    })

    await store.getState().send('第二轮', OPTS)
    const second = store.getState().activeRunId ?? ''
    store.getState().applyEnvelope({
      runId: second,
      seq: 1,
      events: [{ type: 'message_commit', message: assistantMessage('a2', [{ type: 'text', text: '二' }], 3) }]
    })

    expect(store.getState().transcript.messageRuns).toMatchObject({ a1: first, a2: second })
  })

  it('invents no ownership when no run is active', async () => {
    const store = sessionStore(session)
    // 「不知道属于哪个 run」和「属于某个 run」在界面上是两件事:前者不显示卡片,
    // 后者会去拉一个查不到的改动集。老转录(第 12 条迁移之前)走的就是前者。
    store.getState().applyEvents([
      { type: 'message_commit', message: userMessage('u-old', [{ type: 'text', text: '老消息' }], 0) }
    ])
    expect(store.getState().transcript.messageRuns ?? {}).toEqual({})
  })
})
