import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent, RunSnapshot } from '../../../../shared/agent/event'
import { assistantMessage, userMessage } from '../../../../shared/agent/message'
import { liveText } from '../../../../shared/agent/transcript'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(), attachRun: vi.fn(), abortRun: vi.fn(), onAgentEvent: vi.fn(() => () => {})
}))
vi.mock('../../services/app', () => ({
  getSessionInput: vi.fn(async () => null), persistSessionDraft: vi.fn()
}))
/*
  ★ `replaceHistory` 也得在:恢复一个「跑完了还没汇报」的后台子代理时,
  汇报那条路现在会走到底 —— 拿不到发消息的档位就置 `blocked` 并落盘,
  而以前它在拿不到档位时直接 return,一个 store 写入都没有。
*/
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

import { attachRun } from '../../services/agent'
import { getSession, getSessionPage } from '../../services/sessions'
import { adoptActiveRuns, adoptActiveSubagents, releaseSession, sessionStore, syncActiveRuns, useRunIndex } from '../session'

const user = userMessage('old-user', [{ type: 'text', text: 'Earlier question' }], 0)
const answer = assistantMessage('old-answer', [{ type: 'text', text: 'Earlier answer' }], 1)
const reference = { runId: 'restored-run', sessionId: 'restored-session', workspaceId: 'workspace' }
const snapshot = (overrides: Partial<RunSnapshot> = {}): RunSnapshot => ({
  ...reference, depth: 0, status: 'running', seq: 3, pendingInteractions: [], children: [],
  events: [
    { type: 'message_commit', message: answer },
    { type: 'stream', delta: { type: 'message_start', model: 'deepseek-test' } },
    { type: 'stream', delta: { type: 'text_delta', index: 0, text: 'Live reply' } }
  ], ...overrides
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getSession).mockResolvedValue({
    session: { id: reference.sessionId, workspaceId: reference.workspaceId, title: 'Test', model: 'deepseek-test',
      mode: 'normal', thinking: 'auto', rootPathAtCreation: '/workspace', status: 'running',
      archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
    messages: [user, answer]
  })
  vi.mocked(attachRun).mockResolvedValue(snapshot())
})

afterEach(() => {
  sessionStore(reference.sessionId).getState().applyEvents([{ type: 'run_end', status: 'aborted' }])
  releaseSession(reference.sessionId)
})

describe('active conversation recovery', () => {
  const usageEvent = (outputTokens: number): AgentEvent => ({ type: 'stream', delta: {
    type: 'message_end', stopReason: 'tool_use', usage: { inputTokens: 100, outputTokens }
  } })

  it('reads history once and displays it while the live snapshot is still pending', async () => {
    let resolve!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementationOnce(() => new Promise((r) => { resolve = r }))
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    expect(store.getState().historyLoaded).toBe(false)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())
    expect(getSession).toHaveBeenCalledTimes(1)
    expect(store.getState().historyLoaded).toBe(true)
    expect(store.getState().transcript.messages).toEqual([user, answer])
    expect(liveText(store.getState().transcript)).toBe('')
    resolve(snapshot())
    await vi.waitFor(() => expect(liveText(store.getState().transcript)).toBe('Live reply'))
    expect(store.getState().transcript.messages).toEqual([user, answer])
  })

  it.each([2, 3])('keeps %i earlier turns before a refreshed history tail, before and after attaching', async (loadedTurns) => {
    const turns = Array.from({ length: 4 }, (_, index) => [
      userMessage(`question-${index}`, [{ type: 'text', text: `Question ${index}` }], 10 - index),
      assistantMessage(`answer-${index}`, [{ type: 'text', text: `Answer ${index}` }], 10 - index)
    ])
    const messages = turns.flat()
    const detail = {
      session: { id: reference.sessionId } as never,
      messages: turns.slice(0, loadedTurns).flat(), hasMore: false, messageRuns: {}, runUsage: {}, runModel: {}
    }
    vi.mocked(getSessionPage).mockResolvedValueOnce(detail)
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().historyLoaded).toBe(true))

    vi.mocked(getSessionPage).mockResolvedValueOnce({ ...detail, messages: turns.slice(2).flat(), hasMore: true })
    let resolve!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementationOnce(() => new Promise((r) => { resolve = r }))
    adoptActiveRuns([reference])
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())
    try {
      expect(store.getState().transcript.messages).toEqual(messages)
      expect(store.getState().historyHasMore).toBe(false)
    } finally {
      resolve(snapshot({ seq: 4, events: turns[3]!.map((message) => ({ type: 'message_commit', message })) }))
      await vi.waitFor(() => expect(store.getState().lastSeq).toBe(4))
    }
    expect(store.getState().transcript.messages).toEqual(messages)
  })

  it.each(['history', 'snapshot'] as const)('keeps pages loaded while the %s restore is pending', async (phase) => {
    const recent = [
      userMessage('recent-user', [{ type: 'text', text: 'Recent question' }], 2),
      assistantMessage('recent-answer', [{ type: 'text', text: 'Recent answer' }], 3)
    ]
    const page = { session: { id: reference.sessionId } as never, messages: recent, hasMore: true,
      messageRuns: {}, runUsage: {}, runModel: {} }
    vi.mocked(getSessionPage).mockResolvedValueOnce(page)
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().historyLoaded).toBe(true))

    let resolveHistory = (): void => {}
    if (phase === 'history') {
      vi.mocked(getSessionPage).mockImplementationOnce(() => new Promise((resolve) => {
        resolveHistory = () => resolve(page)
      }))
    } else vi.mocked(getSessionPage).mockResolvedValueOnce(page)
    let resolveSnapshot!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementationOnce(() => new Promise((resolve) => { resolveSnapshot = resolve }))
    adoptActiveRuns([reference])
    if (phase === 'snapshot') await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())

    vi.mocked(getSessionPage).mockResolvedValueOnce({ ...page, messages: [user, answer], hasMore: false })
    await store.getState().loadEarlier()
    expect(store.getState().transcript.messages).toEqual([user, answer, ...recent])
    resolveHistory()
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())
    resolveSnapshot(snapshot({ seq: 4, events: recent.map((message) => ({ type: 'message_commit', message })) }))
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(4))
    expect(store.getState().transcript.messages).toEqual([user, answer, ...recent])
    expect(store.getState().historyHasMore).toBe(false)
  })

  it('does not append older snapshot commits outside the loaded page, but keeps new commits', async () => {
    const recent = userMessage('recent-user', [{ type: 'text', text: 'Recent question' }], 2)
    const newest = assistantMessage('newest-answer', [{ type: 'text', text: 'Live answer' }], 3)
    vi.mocked(getSessionPage).mockResolvedValueOnce({
      session: { id: reference.sessionId } as never, messages: [recent], hasMore: true,
      messageRuns: {}, runUsage: {}, runModel: {}
    })
    vi.mocked(attachRun).mockResolvedValueOnce(snapshot({ seq: 4,
      events: [user, answer, recent, newest].map((message) => ({ type: 'message_commit', message }))
    }))
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(4))
    expect(store.getState().transcript.messages).toEqual([recent, newest])
    expect(store.getState().historyHasMore).toBe(true)
  })

  it('starts an adopted run with its own sequence cursor rather than the previous turn cursor', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().historyLoaded).toBe(true))
    store.setState({ lastSeq: 500 })
    adoptActiveRuns([reference])
    await vi.waitFor(() => expect(liveText(store.getState().transcript)).toBe('Live reply'))
    expect(store.getState().lastSeq).toBe(3)
    expect(store.getState().transcript.messages).toEqual([user, answer])
  })

  it('attaches the next run when its broadcast arrives before the previous run end envelope', async () => {
    const previous = { ...reference, runId: 'previous-parent-run' }
    vi.mocked(attachRun).mockResolvedValueOnce(snapshot({ runId: previous.runId, seq: 500 }))
    adoptActiveRuns([previous])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(500))

    syncActiveRuns([reference])
    const nextRun = store.getState().activeRunId
    // 即便实现漏接新运行也结清旧状态,不能污染后面的测试。
    if (nextRun !== reference.runId) store.getState().applyEvents([{ type: 'run_end', status: 'done' }])
    expect(nextRun).toBe(reference.runId)
    await vi.waitFor(() => expect(attachRun).toHaveBeenLastCalledWith(reference.runId, 0))
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(3))
    store.getState().applyEnvelope({ runId: previous.runId, seq: 501, events: [{ type: 'run_end', status: 'done' }] })
    expect(store.getState().activeRunId).toBe(reference.runId)
    expect(liveText(store.getState().transcript)).toBe('Live reply')
  })

  it('rebuilds usage from a full snapshot without adding already received usage again', async () => {
    let resolve!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementation(() => new Promise((r) => { resolve = r }))
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())
    store.getState().applyEnvelope({ runId: reference.runId, seq: 1, events: [usageEvent(144)] })
    resolve(snapshot({ seq: 2, events: [usageEvent(144), usageEvent(256)] }))
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(2))
    expect(store.getState().transcript.usage).toEqual({ inputTokens: 200, outputTokens: 400 })
  })

  it('reattaches from the latest cursor when a usage snapshot overlaps live events', async () => {
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(3))
    let resolve!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementationOnce(() => new Promise((r) => { resolve = r }))
    vi.mocked(attachRun).mockResolvedValueOnce(snapshot({ seq: 5, events: [usageEvent(256)] }))
    // seq 5 reveals a gap and starts attach(3); seq 4 arrives during that request.
    store.getState().applyEnvelope({ runId: reference.runId, seq: 5, events: [usageEvent(256)] })
    store.getState().applyEnvelope({ runId: reference.runId, seq: 4, events: [usageEvent(144)] })
    resolve(snapshot({ seq: 5, events: [usageEvent(144), usageEvent(256)] }))
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(5))
    expect(attachRun).toHaveBeenLastCalledWith(reference.runId, 4)
    expect(store.getState().transcript.usage).toEqual({ inputTokens: 200, outputTokens: 400 })
  })

  it('restores a lazily opened conversation, merges persisted history, and ignores duplicate envelopes', async () => {
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(3))
    expect(attachRun).toHaveBeenCalledWith(reference.runId, 0)
    expect(store.getState().transcript.messages).toEqual([user, answer])
    expect(liveText(store.getState().transcript)).toBe('Live reply')
    store.getState().applyEnvelope({ runId: reference.runId, seq: 3, events: [
      { type: 'stream', delta: { type: 'text_delta', index: 0, text: 'Live reply' } }
    ] })
    expect(liveText(store.getState().transcript)).toBe('Live reply')
    expect(store.getState().activeRunId).toBe(reference.runId)
  })

  it('also attaches an already-created store when bootstrap adopts its active run', async () => {
    const store = sessionStore(reference.sessionId)
    adoptActiveRuns([reference])
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(3))
    expect(store.getState().activeRunId).toBe(reference.runId)
    expect(liveText(store.getState().transcript)).toBe('Live reply')
  })

  it.each(['done', 'error', 'aborted'] as const)('clears both running indicators if the task becomes %s while reattaching', async (status) => {
    vi.mocked(attachRun).mockResolvedValue(snapshot({ status, seq: 4, events: [
      ...snapshot().events, { type: 'run_end', status }
    ] }))
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().activeRunId).toBeNull())
    expect(store.getState().transcript.status).toBe(status)
    expect(useRunIndex.getState()).toEqual([])
  })

  it('preserves newer stream events when an older attach snapshot arrives late', async () => {
    let resolve!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementation(() => new Promise((r) => { resolve = r }))
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalled())
    store.getState().applyEnvelope({ runId: reference.runId, seq: 4, events: [
      ...snapshot().events, { type: 'stream', delta: { type: 'text_delta', index: 0, text: ' continued' } }
    ] })
    resolve(snapshot())
    await vi.waitFor(() => expect(store.getState().transcript.messages).toHaveLength(2))
    expect(store.getState().lastSeq).toBe(4)
    expect(liveText(store.getState().transcript)).toBe('Live reply continued')
  })

  it.each([false, true])('restores a detached parent without moving old replies below newer turns (load earlier: %s)', async (loadEarlier) => {
    const childRunId = 'old-parent-child'
    const recent = [
      userMessage('recent-user', [{ type: 'text', text: 'Recent question' }], 2),
      assistantMessage('recent-answer', [{ type: 'text', text: 'Recent answer' }], 3)
    ]
    const page = { session: { id: reference.sessionId } as never, messages: recent, hasMore: true,
      messageRuns: {}, runUsage: {}, runModel: {} }
    vi.mocked(getSessionPage).mockResolvedValueOnce(page).mockResolvedValueOnce(page)
    let resolveParent!: (value: RunSnapshot) => void
    vi.mocked(attachRun).mockImplementationOnce(() => new Promise((resolve) => { resolveParent = resolve }))
    vi.mocked(attachRun).mockResolvedValueOnce(snapshot({ runId: childRunId, depth: 1, status: 'done', seq: 1,
      events: [{ type: 'run_end', status: 'done' }]
    }))
    adoptActiveSubagents([{ runId: childRunId, parentRunId: reference.runId,
      sessionId: reference.sessionId, workspaceId: reference.workspaceId }])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(attachRun).toHaveBeenCalledWith(reference.runId, 0))
    if (loadEarlier) {
      vi.mocked(getSessionPage).mockResolvedValueOnce({ ...page, messages: [user, answer], hasMore: false })
      await store.getState().loadEarlier()
    }
    resolveParent(snapshot({ status: 'done', seq: 4, events: [
      { type: 'subagent_start', callId: 'old-task', childRunId, background: true },
      { type: 'message_commit', message: user },
      { type: 'message_commit', message: answer },
      { type: 'run_end', status: 'done' }
    ] }))
    await vi.waitFor(() => expect(store.getState().transcript.subagents['old-task']?.status).toBe('done'))
    expect(store.getState().transcript.messages).toEqual(loadEarlier ? [user, answer, ...recent] : recent)
    expect(store.getState().historyHasMore).toBe(!loadEarlier)
    expect(store.getState().activeRunId).toBeNull()
  })

  it('restores a background child and its final telemetry after renderer reload', async () => {
    const parent = { runId: 'detached-parent', sessionId: 'detached-session', workspaceId: 'workspace' }
    const child = { runId: 'detached-child', parentRunId: parent.runId, sessionId: parent.sessionId, workspaceId: parent.workspaceId }
    const parentSnapshot: RunSnapshot = {
      ...parent,
      depth: 0,
      status: 'done',
      seq: 2,
      pendingInteractions: [],
      children: [child.runId],
      events: [
        {
          type: 'subagent_start',
          callId: 'task-detached',
          childRunId: child.runId,
          description: '后台查配置',
          subagentType: 'researcher',
          background: true,
          at: 10
        },
        { type: 'run_end', status: 'done', at: 20 }
      ]
    }
    const childSnapshot: RunSnapshot = {
      ...child,
      depth: 1,
      status: 'done',
      seq: 2,
      pendingInteractions: [],
      children: [],
      events: [
        { type: 'message_commit', message: assistantMessage('detached-answer', [{ type: 'text', text: '后台结果' }], 30) },
        { type: 'run_end', status: 'done', at: 30 }
      ]
    }
    vi.mocked(getSession).mockResolvedValue({
      session: { id: parent.sessionId, workspaceId: parent.workspaceId, title: 'Detached', model: 'deepseek-test',
        mode: 'normal', thinking: 'auto', rootPathAtCreation: '/workspace', status: 'idle',
        archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
      messages: []
    })
    vi.mocked(attachRun).mockImplementation(async (runId) => runId === parent.runId ? parentSnapshot : childSnapshot)

    adoptActiveSubagents([child])
    const store = sessionStore(parent.sessionId)
    await vi.waitFor(() => expect(store.getState().transcript.subagents['task-detached']?.status).toBe('done'))

    expect(store.getState().transcript.subagents['task-detached']).toMatchObject({
      childRunId: child.runId,
      background: true,
      summary: '后台结果',
      startedAt: 10,
      endedAt: 30
    })
    expect(attachRun).toHaveBeenCalledWith(parent.runId, 0)
    expect(attachRun).toHaveBeenCalledWith(child.runId, 0)
    releaseSession(parent.sessionId)
  })
})
