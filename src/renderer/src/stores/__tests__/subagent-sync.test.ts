/**
 * 子代理状态的**收敛** —— `agent:activeRuns` 广播里 `subagents` 那一半的验收。
 *
 * 症状:漏掉一条子 run 的结束事件(窗口藏着、父会话的 store 当时不在),转录里那张卡片和
 * 右上角胶囊永远「运行中」,点开子会话却是「已完成」。主进程的权威集合一到,三处必须对齐。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentMessage } from '../../../../shared/agent/message'
import type { SubagentState } from '../../../../shared/agent/transcript'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(), attachRun: vi.fn(), abortRun: vi.fn(async () => {}),
  interjectRun: vi.fn(async () => {}),
  onAgentEvent: vi.fn(() => () => {}),
  onActiveRuns: vi.fn(() => () => {}),
  unwatchRun: vi.fn(async () => {})
}))
vi.mock('../../services/app', () => ({
  getSessionInput: vi.fn(async () => null), persistSessionDraft: vi.fn()
}))
vi.mock('../../services/sessions', () => {
  const getSession = vi.fn()
  return {
    getSession,
    getSessionPage: vi.fn(async (sessionId: string) => {
      const detail = await getSession(sessionId)
      return detail == null ? detail : { ...(detail as object), hasMore: false }
    }),
    replaceHistory: vi.fn(async () => {})
  }
})
vi.mock('../../services/goal', () => ({ getGoal: vi.fn(async () => undefined), onGoalChanged: vi.fn(() => () => {}) }))

import { abortRun, attachRun } from '../../services/agent'
import { getSession } from '../../services/sessions'
import { releaseSession, sessionStore, stopSubagent, syncActiveSubagents } from '../session'

const PARENT = 'parent-session'
const card: SubagentState = { callId: 'task-1', childRunId: 'child-1', status: 'running', toolCalls: 3, toolErrors: 0, startedAt: 1 }

function receipt(status: 'running' | 'done'): AgentMessage {
  return {
    id: 'm-receipt', role: 'tool', createdAt: 1,
    parts: [{ type: 'tool_result', callId: 'task-1', output: { content: 'ok' }, isError: false,
      subagent: { childRunId: 'child-1', status, background: true, summary: 'all done' } }]
  } as unknown as AgentMessage
}

function sessionDetail(messages: AgentMessage[]): unknown {
  return {
    session: { id: PARENT, workspaceId: 'w', title: 'Parent', model: 'm', mode: 'normal', thinking: 'auto',
      rootPathAtCreation: '/w', status: 'idle', archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
    messages
  }
}

async function parentWithRunningCard(): Promise<ReturnType<typeof sessionStore>> {
  const store = sessionStore(PARENT)
  await vi.waitFor(() => expect(store.getState().historyLoaded).toBe(true))
  store.setState((s) => ({ transcript: { ...s.transcript, subagents: { 'task-1': card } } }))
  return store
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getSession).mockResolvedValue(sessionDetail([]) as never)
})

afterEach(() => {
  syncActiveSubagents([])
  releaseSession(PARENT)
})

describe('subagent convergence', () => {
  it('settles a card whose child the main process no longer reports, using the durable receipt', async () => {
    const store = await parentWithRunningCard()
    // 子 run 已被回收:attach 不到,终态只能从库里的回执来
    vi.mocked(attachRun).mockRejectedValue(new Error('run 不存在'))
    vi.mocked(getSession).mockResolvedValue(sessionDetail([receipt('done')]) as never)

    syncActiveSubagents([])

    await vi.waitFor(() => expect(store.getState().transcript.subagents['task-1']?.status).toBe('done'))
    expect(store.getState().transcript.subagents['task-1']?.summary).toBe('all done')
  })

  it('leaves a card alone while the main process still reports its child', async () => {
    const store = await parentWithRunningCard()
    syncActiveSubagents([{ runId: 'child-1', parentRunId: 'p', sessionId: PARENT, workspaceId: 'w', status: 'running' }])
    await new Promise((resolve) => setTimeout(resolve, 400))
    expect(attachRun).not.toHaveBeenCalled()
    expect(store.getState().transcript.subagents['task-1']?.status).toBe('running')
  })

  it('falls back to aborted when neither the run nor the receipt can say how it ended', async () => {
    const store = await parentWithRunningCard()
    vi.mocked(attachRun).mockRejectedValue(new Error('run 不存在'))
    vi.mocked(getSession).mockResolvedValue(sessionDetail([receipt('running')]) as never)

    syncActiveSubagents([])

    await vi.waitFor(() => expect(store.getState().transcript.subagents['task-1']?.status).toBe('aborted'))
  })

  it('stopping from anywhere aborts and then settles against the main process', async () => {
    const store = await parentWithRunningCard()
    vi.mocked(attachRun).mockResolvedValue({
      runId: 'child-1', sessionId: 'child-session', workspaceId: 'w', depth: 1, status: 'aborted', seq: 1,
      pendingInteractions: [], children: [], events: [{ type: 'run_end', status: 'aborted', at: 9 }]
    })

    stopSubagent('child-1')

    await vi.waitFor(() => expect(store.getState().transcript.subagents['task-1']?.status).toBe('aborted'))
    expect(abortRun).toHaveBeenCalledWith('child-1', true)
  })
})
