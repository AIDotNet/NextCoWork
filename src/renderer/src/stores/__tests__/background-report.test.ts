/**
 * 后台子代理的结果回传 —— **渲染层这一半**。
 *
 * 汇报本身在主进程(`main/session-runtime.ts`,全文 vs 摘要、internal 标记、
 * 没有档位时置 blocked,都由 `main/__tests__/session-runtime.test.ts` 钉住):
 * 子代理跑完时没有窗口在看那条会话,结果也得交回主代理。
 *
 * 渲染层只剩两件事,这里钉的就是它们:
 * 1. 从库里恢复出一张「跑完了、还没汇报」的卡片**不会**自己触发汇报 ——
 *    冷启动不自动开跑,也不该有第二个决定者;
 * 2. 用户点「处理」时,把兜底档位原样交给主进程,并采用它回答的状态。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent } from '../../../../shared/agent/event'
import type { SendOptions } from '../../../../shared/agent/run-request'
import { userMessage } from '../../../../shared/agent/message'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(async () => ({ started: true })), attachRun: vi.fn(), abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}), onAgentEvent: vi.fn(() => () => {}),
  reportBackground: vi.fn()
}))
vi.mock('../../services/app', () => ({
  getSessionInput: vi.fn(async () => null), persistSessionDraft: vi.fn()
}))
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

import { reportBackground, startRun } from '../../services/agent'
import { replaceHistory } from '../../services/sessions'
import { releaseSession, reportBackgroundChild, sessionStore } from '../session'

const CALL_ID = 'task-bg'
const CHILD_RUN = 'parent-run:sub:1'

const options = (): SendOptions => ({
  workspaceId: 'workspace', depth: 0, mode: 'normal', thinking: 'auto',
  webSearch: false, maxContext: false, permissionMode: 'ask',
  model: 'deepseek-test', modelProviderId: 'deepseek', skillIds: []
})

/** 一个「跑完了、还没汇报」的后台子代理 —— 重启后恢复出来就是这个形状。 */
const restored: AgentEvent[] = [
  { type: 'subagent_start', callId: CALL_ID, childRunId: CHILD_RUN,
    childSessionId: `s:sub:${CHILD_RUN}`, description: '查配置读取处',
    subagentType: 'general-purpose', background: true },
  { type: 'subagent_end', callId: CALL_ID, childRunId: CHILD_RUN, status: 'done', summary: '三处读取' }
]

let sessions: string[] = []

beforeEach(() => { vi.clearAllMocks() })
afterEach(() => {
  for (const id of sessions) releaseSession(id)
  sessions = []
})

function restoredSession(name: string): string {
  const sessionId = `session-${name}`
  sessions.push(sessionId)
  sessionStore(sessionId).getState().applyEvents(restored)
  return sessionId
}

describe('后台子代理回传 · 渲染层', () => {
  it('★★ 恢复出待汇报的卡片不会自己汇报:不发 IPC、不起 run、不整段改写历史', async () => {
    const sessionId = restoredSession('restored')
    await Promise.resolve()

    expect(sessionStore(sessionId).getState().transcript.subagents[CALL_ID]?.reportStatus).toBe('pending')
    expect(reportBackground).not.toHaveBeenCalled()
    expect(startRun).not.toHaveBeenCalled()
    expect(replaceHistory).not.toHaveBeenCalled()
  })

  it.each(['injecting', 'reported', 'blocked'] as const)('迟到的子代理结束事件不能把 %s 打回 pending', (status) => {
    const sessionId = restoredSession(`late-end-${status}`)
    const store = sessionStore(sessionId)
    store.getState().setSubagentReportStatus(CALL_ID, status)
    store.getState().applyEvents([restored[1]!])
    expect(store.getState().transcript.subagents[CALL_ID]?.reportStatus).toBe(status)

    store.getState().applyChildEvents(CHILD_RUN, [{ type: 'run_end', status: 'done' }])
    expect(store.getState().transcript.subagents[CALL_ID]?.reportStatus).toBe(status)
  })

  it.each(['injecting', 'reported'] as const)('重放 Task 起止和旧回执不能覆盖实时 %s 状态', (status) => {
    const sessionId = restoredSession(`replay-${status}`)
    const store = sessionStore(sessionId)
    store.getState().setSubagentReportStatus(CALL_ID, status)
    store.getState().applyEvents(restored)
    expect(store.getState().transcript.subagents[CALL_ID]?.reportStatus).toBe(status)

    store.getState().applyEvents([{ type: 'message_commit', message: userMessage('old-receipt', [{
      type: 'tool_result', callId: CALL_ID, output: { content: 'Started in the background' }, isError: false,
      subagent: { childRunId: CHILD_RUN, background: true, status: 'running' }
    }], 1) }])
    expect(store.getState().transcript.subagents[CALL_ID]?.reportStatus).toBe(status)
  })

  it('★ 点「处理」:兜底档位交给主进程,采用它回答的状态', async () => {
    vi.mocked(reportBackground).mockResolvedValue({ status: 'reported' })
    const sessionId = restoredSession('manual')

    await reportBackgroundChild(sessionId, CALL_ID, options())

    expect(reportBackground).toHaveBeenCalledWith(sessionId, CALL_ID, options())
    expect(sessionStore(sessionId).getState().transcript.subagents[CALL_ID]?.reportStatus).toBe('reported')
    // 渲染层不再整段写回历史 —— 回执的持久化在主进程
    expect(replaceHistory).not.toHaveBeenCalled()
  })

  it('主进程说 blocked(谁都没有档位):卡片照实显示,不是一颗按了没反应的按钮', async () => {
    vi.mocked(reportBackground).mockResolvedValue({ status: 'blocked' })
    const sessionId = restoredSession('blocked')

    await reportBackgroundChild(sessionId, CALL_ID)

    expect(reportBackground).toHaveBeenCalledWith(sessionId, CALL_ID, undefined)
    expect(sessionStore(sessionId).getState().transcript.subagents[CALL_ID]?.reportStatus).toBe('blocked')
  })
})
