import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../shared/agent/message'
import type { AgentMessage } from '../../../../shared/agent/message'
import type { SessionDetail } from '../../../../shared/domain/session'
import type { RunSnapshot } from '../../../../shared/agent/event'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(), attachRun: vi.fn(), abortRun: vi.fn(), onAgentEvent: vi.fn(() => () => {})
}))
vi.mock('../../services/app', () => ({ getSessionInput: vi.fn(async () => null), persistSessionDraft: vi.fn() }))
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

import { attachRun } from '../../services/agent'
import { getSessionInput } from '../../services/app'
import { getSession } from '../../services/sessions'
import { adoptActiveRuns, refreshHydratedSessions, releaseSession, sessionStore } from '../session'

const reference = { runId: 'history-run', sessionId: 'tool-history-session', workspaceId: 'workspace' }
const history: AgentMessage[] = [
  userMessage('question', [{ type: 'text', text: 'Inspect the project' }], 1),
  assistantMessage('calls', [
    { type: 'tool_call', callId: 'read', name: 'Read', input: { file_path: '/workspace/Program.cs' } },
    { type: 'tool_call', callId: 'list', name: 'LS', input: { path: '/workspace' } }
  ], 2),
  toolResultMessage('results', [
    { type: 'tool_result', callId: 'read', output: { content: 'var app = builder.Build();' }, isError: false },
    { type: 'tool_result', callId: 'list', output: { content: 'Not allowed' }, isError: true }
  ], 3),
  assistantMessage('answer', [{ type: 'text', text: 'Project inspected.' }], 4)
]

function detail(messages = history): SessionDetail {
  return { session: { id: reference.sessionId, workspaceId: reference.workspaceId, title: 'Project architecture', model: 'model',
    mode: 'normal', thinking: 'off', rootPathAtCreation: '/workspace', status: 'idle', archived: false, favorited: false, createdAt: 1, updatedAt: 4 }, messages }
}

beforeEach(() => { vi.clearAllMocks(); vi.mocked(getSession).mockResolvedValue(detail()) })
afterEach(() => {
  sessionStore(reference.sessionId).getState().applyEvents([{ type: 'run_end', status: 'aborted' }])
  releaseSession(reference.sessionId)
})

function checkResults(): void {
  const tools = sessionStore(reference.sessionId).getState().transcript.tools
  expect(tools['read']).toMatchObject({ name: 'Read', status: 'ok', output: { content: 'var app = builder.Build();' } })
  expect(tools['list']).toMatchObject({ name: 'LS', status: 'error', output: { content: 'Not allowed' } })
}

describe('tool results after history refresh', () => {
  it('restores results on first load and after closing/reopening a completed session', async () => {
    sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    expect(releaseSession(reference.sessionId)).toBe(true)
    sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
  })

  it('a session-change refresh keeps completed outputs, timing and clears old progress', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    store.getState().applyEvents([
      { type: 'tool_start', callId: 'read', toolName: 'Read', input: { file_path: '/workspace/Program.cs' }, at: 10 },
      { type: 'tool_end', callId: 'read', output: { content: 'var app = builder.Build();' }, isError: false, at: 20 },
      { type: 'run_end', status: 'done' }
    ])
    await refreshHydratedSessions()
    checkResults()
    expect(store.getState().transcript.tools['read']).toMatchObject({ startedAt: 10, endedAt: 20 })
  })

  it('history replacement clears removed tool results instead of reusing cached data', async () => {
    sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    vi.mocked(getSession).mockResolvedValue(detail([]))
    await refreshHydratedSessions()
    expect(sessionStore(reference.sessionId).getState().transcript.tools).toEqual({})
  })

  it('does not reload unrelated histories for metadata or deletion events', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    const before = store.getState().transcript
    vi.mocked(getSession).mockClear()
    await refreshHydratedSessions({ kind: 'metadata', sessionIds: [reference.sessionId] })
    await refreshHydratedSessions({ kind: 'deleted', sessionIds: ['another-workspace-session'] })
    expect(getSession).not.toHaveBeenCalled()
    expect(store.getState().transcript).toBe(before)
  })

  it('reloads only affected cached sessions and ignores unopened histories', async () => {
    sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    vi.mocked(getSession).mockClear()
    await refreshHydratedSessions({ kind: 'history', sessionIds: ['unopened', reference.sessionId, reference.sessionId] })
    expect(getSession).toHaveBeenCalledTimes(1)
    expect(getSession).toHaveBeenCalledWith(reference.sessionId)
  })

  it('keeps another workspace snapshot unchanged for message events but refreshes both on reset', async () => {
    const otherDetail = detail()
    otherDetail.session = { ...otherDetail.session, id: 'other-workspace-history', workspaceId: 'other-workspace' }
    vi.mocked(getSession).mockImplementation(async (sessionId) => sessionId === otherDetail.session.id ? otherDetail : detail())
    sessionStore(reference.sessionId)
    const other = sessionStore(otherDetail.session.id)
    try {
      await vi.waitFor(checkResults)
      await vi.waitFor(() => expect(other.getState().transcript.messages).toEqual(history))
      const snapshot = other.getState().transcript
      vi.mocked(getSession).mockClear()
      await refreshHydratedSessions({ kind: 'messages', workspaceId: reference.workspaceId, sessionIds: [reference.sessionId] })
      expect(getSession).toHaveBeenCalledTimes(1)
      expect(getSession).toHaveBeenCalledWith(reference.sessionId)
      expect(other.getState().transcript).toBe(snapshot)
      vi.mocked(getSession).mockClear()
      await refreshHydratedSessions({ kind: 'reset' })
      expect(getSession).toHaveBeenCalledTimes(2)
      expect(getSession).toHaveBeenCalledWith(otherDetail.session.id)
    } finally {
      releaseSession(otherDetail.session.id)
    }
  })

  it('does not invalidate a locally active run even if a stale deletion arrives', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    store.setState({ activeRunId: reference.runId })
    const snapshot = store.getState().transcript
    vi.mocked(getSession).mockClear()
    try {
      await refreshHydratedSessions({ kind: 'messages', sessionIds: [reference.sessionId] })
      await refreshHydratedSessions({ kind: 'history', sessionIds: [reference.sessionId] })
      await refreshHydratedSessions({ kind: 'deleted', sessionIds: [reference.sessionId] })
      await refreshHydratedSessions({ kind: 'reset' })
      expect(getSession).not.toHaveBeenCalled()
      expect(store.getState().transcript).toBe(snapshot)
      expect(store.getState().activeRunId).toBe(reference.runId)
    } finally {
      store.setState({ activeRunId: null })
    }
  })

  it('discards a late history response after deletion without rehydrating the deleted id', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    let resolve!: (value: SessionDetail) => void
    vi.mocked(getSession).mockImplementationOnce(() => new Promise((done) => { resolve = done }))
    const pending = refreshHydratedSessions({ kind: 'history', sessionIds: [reference.sessionId] })
    await refreshHydratedSessions({ kind: 'deleted', sessionIds: [reference.sessionId] })
    resolve(detail())
    await pending
    expect(store.getState().transcript.messages).toEqual([])
    const calls = vi.mocked(getSession).mock.calls.length
    releaseSession(reference.sessionId)
    sessionStore(reference.sessionId)
    expect(getSession).toHaveBeenCalledTimes(calls)
    await refreshHydratedSessions({ kind: 'reset' })
    checkResults()
  })

  it('coalesces invalidations during a read and applies only the trailing snapshot', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    const before = store.getState().transcript
    let resolve!: (value: SessionDetail) => void
    vi.mocked(getSession).mockClear()
    vi.mocked(getSession).mockImplementationOnce(() => new Promise((done) => { resolve = done }))
    vi.mocked(getSession).mockResolvedValueOnce(detail([]))
    const first = refreshHydratedSessions({ kind: 'history', sessionIds: [reference.sessionId] })
    const second = refreshHydratedSessions({ kind: 'history', sessionIds: [reference.sessionId] })
    const third = refreshHydratedSessions({ kind: 'history', sessionIds: [reference.sessionId] })
    expect(getSession).toHaveBeenCalledTimes(1)
    expect(store.getState().transcript).toBe(before)
    resolve(detail())
    await Promise.all([first, second, third])
    expect(getSession).toHaveBeenCalledTimes(2)
    expect(store.getState().transcript.messages).toEqual([])
  })

  it('does not restore a deleted draft into a newly created cache with the same id', async () => {
    let resolve!: (value: Awaited<ReturnType<typeof getSessionInput>>) => void
    const pending = new Promise<Awaited<ReturnType<typeof getSessionInput>>>((done) => { resolve = done })
    vi.mocked(getSessionInput).mockReturnValueOnce(pending)
    const previous = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    previous.setState({ draft: 'Local draft' })
    await refreshHydratedSessions({ kind: 'deleted', sessionIds: [reference.sessionId] })
    expect(previous.getState().draft).toBe('')
    releaseSession(reference.sessionId)
    const replacement = sessionStore(reference.sessionId)
    resolve({ v: 1, draft: 'Deleted draft', queued: [], savedAt: 1 })
    await pending
    expect(replacement.getState().draft).toBe('')
    await refreshHydratedSessions({ kind: 'reset' })
  })

  it('does not deliver a released store snapshot into a new store with the same id', async () => {
    sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    let resolve!: (value: SessionDetail) => void
    vi.mocked(getSession).mockImplementationOnce(() => new Promise((done) => { resolve = done }))
    const pending = refreshHydratedSessions()
    releaseSession(reference.sessionId)
    vi.mocked(getSession).mockResolvedValue(detail([]))
    const replacement = sessionStore(reference.sessionId)
    resolve(detail())
    await pending
    expect(replacement.getState().transcript.messages).toEqual([])
  })

  it('a refresh response cannot overwrite a newer active run', async () => {
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(checkResults)
    let resolve!: (detail: SessionDetail) => void
    vi.mocked(getSession).mockImplementationOnce(() => new Promise((r) => { resolve = r }))
    const refresh = refreshHydratedSessions()
    store.setState({ activeRunId: reference.runId })
    store.getState().applyEvents([{ type: 'tool_start', callId: 'current', toolName: 'LS', input: {}, at: 50 }])
    resolve(detail([]))
    await refresh
    checkResults()
    expect(store.getState().transcript.tools['current']?.status).toBe('running')
  })

  it('reattaching to a new run also restores tool results from earlier turns', async () => {
    vi.mocked(attachRun).mockResolvedValue({ ...reference, depth: 0, seq: 2, status: 'running', children: [], pendingInteractions: [], events: [
      { type: 'tool_start', callId: 'current', toolName: 'LS', input: { path: '/next' }, at: 50 }
    ] } satisfies RunSnapshot)
    adoptActiveRuns([reference])
    const store = sessionStore(reference.sessionId)
    await vi.waitFor(() => expect(store.getState().lastSeq).toBe(2))
    checkResults()
    expect(store.getState().transcript.tools['current']).toMatchObject({ name: 'LS', status: 'running', startedAt: 50 })
  })
})
