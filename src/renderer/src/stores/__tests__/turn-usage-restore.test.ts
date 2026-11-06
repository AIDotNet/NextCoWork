/**
 * Per-turn token readouts must survive a restart.
 *
 * The live `transcript.usage` only ever describes the run that is currently
 * streaming, and it dies with the renderer process. The durable answer comes
 * from SQLite (`runUsage` keyed by run, `messageRuns` mapping each message to
 * the run that produced it), and these tests pin the two ways it used to get
 * lost: never loaded on hydrate, and dropped again on the next turn.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, userMessage } from '../../../../shared/agent/message'
import type { SendOptions } from '../../../../shared/agent/run-request'

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

const SESSION = 'usage-session'
const user = userMessage('u1', [{ type: 'text', text: 'Question' }], 0)
const answer = assistantMessage('a1', [{ type: 'text', text: 'Answer' }], 1)
// upstreamMs 是平均 TPS 的分母,和 token 一样得挺过重启 —— 少了它,老对话的速度读数会集体消失
const usage = { inputTokens: 120, outputTokens: 40, cacheReadInputTokens: 900, upstreamMs: 2_500 }
const OPTS: SendOptions = {
  workspaceId: 'workspace', depth: 0, mode: 'normal', thinking: 'auto',
  webSearch: false, permissionMode: 'ask', model: 'deepseek-test', skillIds: []
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getSession).mockResolvedValue({
    session: { id: SESSION, workspaceId: 'workspace', title: 'Test', model: 'deepseek-test',
      mode: 'normal', thinking: 'auto', rootPathAtCreation: '/workspace', status: 'idle',
      archived: false, favorited: false, createdAt: 0, updatedAt: 1 },
    messages: [user, answer],
    messageRuns: { a1: 'run-1' },
    runUsage: { 'run-1': usage }
  })
})

afterEach(() => {
  releaseSession(SESSION)
})

describe('per-turn usage after a restart', () => {
  it('loads the persisted per-run usage when the session is first opened', async () => {
    const store = sessionStore(SESSION)
    await vi.waitFor(() => expect(store.getState().transcript.messages).toHaveLength(2))
    expect(store.getState().transcript.runUsage).toEqual({ 'run-1': usage })
    expect(store.getState().transcript.messageRuns).toEqual({ a1: 'run-1' })
  })

  it('keeps history usage visible while the next turn streams', async () => {
    const store = sessionStore(SESSION)
    await vi.waitFor(() => expect(store.getState().transcript.runUsage).toBeDefined())

    // Starting a turn resets the run-scoped parts of the transcript. The ledger
    // of earlier turns belongs to the whole conversation, like `messages` — if
    // it is cleared here, every older turn's readout blanks the moment the user
    // sends the next message, while the live one still looks fine.
    await store.getState().send('Next question', OPTS)

    expect(store.getState().transcript.runUsage).toEqual({ 'run-1': usage })
    expect(store.getState().transcript.messageRuns).toEqual({ a1: 'run-1' })
    expect(store.getState().transcript.usage).toBeUndefined()
  })
})
