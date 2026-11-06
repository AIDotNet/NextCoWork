/**
 * 运行中状态的**收敛** —— `agent:activeRuns` 那条广播的验收。
 *
 * 为什么需要这份用例:三处「运行中」指示(外层工作区 Tab、内层对话 Tab、
 * 侧边栏会话行)全都读 `useRunIndex`,而那份索引原先只能靠 `run_end` 事件摘条目。
 * 事件流按 run 订阅定向推送,两类 run 的结束永远送不到这个窗口:
 * 定时任务起的(没人订阅过,主进程整批丢弃)、⌘R 重载后还没打开的会话
 * (`drain` 查得到 runId 却拿不到 store)。症状是 Agent 早就跑完、角标还在转,
 * 而且全程零报错 —— 所以它只能靠这种「权威集合一到就对齐」的用例守住。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunSnapshot } from '../../../../shared/agent/event'
import { assistantMessage } from '../../../../shared/agent/message'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(), attachRun: vi.fn(), abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}),
  onAgentEvent: vi.fn(() => () => {}),
  onActiveRuns: vi.fn(() => () => {})
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
    replaceHistory: vi.fn(async () => {})
  }
})
vi.mock('../../services/goal', () => ({ getGoal: vi.fn(async () => undefined), onGoalChanged: vi.fn(() => () => {}) }))

import { abortRun, attachRun } from '../../services/agent'
import { getSession } from '../../services/sessions'
import { adoptActiveRuns, releaseSession, sessionStore, syncActiveRuns, useRunIndex } from '../session'

const entry = { runId: 'ghost-run', sessionId: 'ghost-session', workspaceId: 'workspace' }
const answer = assistantMessage('answer', [{ type: 'text', text: '跑完了' }], 1)

const endedSnapshot: RunSnapshot = {
  ...entry, depth: 0, status: 'done', seq: 2, pendingInteractions: [], children: [],
  events: [{ type: 'message_commit', message: answer }, { type: 'run_end', status: 'done', at: 20 }]
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getSession).mockResolvedValue({
    session: { id: entry.sessionId, workspaceId: entry.workspaceId, title: 'Ghost', model: 'deepseek-test',
      mode: 'normal', thinking: 'auto', rootPathAtCreation: '/workspace', status: 'idle',
      archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
    messages: [answer]
  })
  vi.mocked(attachRun).mockResolvedValue(endedSnapshot)
})

afterEach(() => {
  syncActiveRuns([])
  releaseSession(entry.sessionId)
})

describe('active run index convergence', () => {
  it('drops a run the main process no longer reports, even with no session store open', () => {
    adoptActiveRuns([entry])
    expect(useRunIndex.getState()).toEqual([entry])

    syncActiveRuns([])

    expect(useRunIndex.getState()).toEqual([])
  })

  it('leaves the conversation idle when its run ended without any event reaching this window', async () => {
    adoptActiveRuns([entry])
    const store = sessionStore(entry.sessionId)
    await vi.waitFor(() => expect(store.getState().activeRunId).toBe(entry.runId))

    syncActiveRuns([])

    await vi.waitFor(() => expect(store.getState().activeRunId).toBeNull())
    expect(store.getState().transcript.status).toBe('done')
    expect(useRunIndex.getState()).toEqual([])
  })

  it('still settles the conversation when the ended run was already reaped by the main process', async () => {
    adoptActiveRuns([entry])
    const store = sessionStore(entry.sessionId)
    await vi.waitFor(() => expect(store.getState().activeRunId).toBe(entry.runId))
    // attach 失败 = 主进程已经回收了这个 run。界面仍然必须离开运行态。
    vi.mocked(attachRun).mockRejectedValue(new Error('run 不存在: ghost-run'))

    syncActiveRuns([])

    await vi.waitFor(() => expect(store.getState().activeRunId).toBeNull())
    expect(useRunIndex.getState()).toEqual([])
  })

  it('★ a run reaped before its attach lands settles from the database instead of spinning forever', async () => {
    // 主进程按 TTL/预算回收了已结束的 run:attach 只能拿到「run 不存在」
    vi.mocked(attachRun).mockRejectedValue(new Error('run 不存在: ghost-run'))
    adoptActiveRuns([entry])
    const store = sessionStore(entry.sessionId)

    await vi.waitFor(() => expect(store.getState().activeRunId).toBeNull())
    await vi.waitFor(() => expect(store.getState().transcript.messages.map((m) => m.id)).toEqual(['answer']))
    expect(store.getState().transcript.status).toBe('done')
    expect(useRunIndex.getState()).toEqual([])
  })

  it('★ stop settles the conversation when the run_end never reaches this window (#187)', async () => {
    const running: RunSnapshot = { ...endedSnapshot, status: 'running', seq: 0, events: [] }
    vi.mocked(attachRun).mockResolvedValue(running)
    adoptActiveRuns([entry])
    const store = sessionStore(entry.sessionId)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(store.getState().activeRunId).toBe(entry.runId)

    // 主进程那边其实早就收尾了,只是这个窗口漏了 run_end —— abort 是空操作
    vi.mocked(attachRun).mockResolvedValue(endedSnapshot)
    vi.useFakeTimers({ toFake: ['setTimeout'] })
    try {
      await store.getState().stop()
      expect(abortRun).toHaveBeenCalledWith(entry.runId, true)
      expect(store.getState().activeRunId).toBe(entry.runId)
      // 宽限期之内不动:正常情况下真正的 run_end 会先到
      await vi.advanceTimersByTimeAsync(6_000)
      expect(store.getState().activeRunId).toBe(entry.runId)
      await vi.advanceTimersByTimeAsync(1_000)
      expect(store.getState().activeRunId).toBeNull()
    } finally {
      vi.useRealTimers()
    }
    expect(store.getState().transcript.status).toBe('done')
  })

  it('adopts a run started in another window without dropping the ones already known', () => {
    const other = { runId: 'other-run', sessionId: 'other-session', workspaceId: 'workspace' }
    adoptActiveRuns([entry])

    syncActiveRuns([entry, other])

    expect(useRunIndex.getState()).toEqual([entry, other])
    releaseSession(other.sessionId)
  })
})
