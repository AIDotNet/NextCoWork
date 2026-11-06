import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, userMessage } from '../../../shared/agent/message'
import { compactBoundaryOf } from '../../../shared/agent/compaction'
import type { RunRequest } from '../../../shared/agent/run-request'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { closeDatabase } from '../../db'
import { runs } from '../../kernel/run-registry'
import { store } from '../../state/store'

const state = vi.hoisted(() => ({
  wait: Promise.resolve(),
  calls: 0,
  fail: false
}))

vi.mock('../../runtime', () => ({
  getTools: () => ({ byInternalId: () => undefined }),
  getHost: () => ({ clock: { now: () => 1 }, logger: { warn: () => {} } }),
  getRouter: () => ({
    resolveModel: () => ({
      alias: 'test', providerId: 'test', upstreamModel: 'test',
      contextWindow: 128_000, maxOutputTokens: 8192,
      capabilities: { thinking: false, tools: true, vision: false, caching: false }
    }),
    async *stream() {
      state.calls++
      await state.wait
      if (state.fail) throw new Error('summary failed')
      yield { type: 'text_delta', index: 0, text: 'Summary of the original conversation.' }
    }
  })
}))

import { compactContext } from '../context'

const request = (sessionId: string): RunRequest => ({
  runId: 'next-run', sessionId, workspaceId: 'workspace', depth: 0,
  input: [], mode: 'code', thinking: 'auto', webSearch: false,
  permissionMode: 'full', model: 'test', skillIds: []
})

function pauseSummary(): () => void {
  let release!: () => void
  state.wait = new Promise<void>((resolve) => { release = resolve })
  return release
}

beforeEach(() => {
  closeDatabase()
  runs.clearForTest()
  state.calls = 0
  state.fail = false
  state.wait = Promise.resolve()
  store.putWorkspace({ id: 'workspace', name: 'Workspace', rootPath: '',
    createdAt: 1, lastOpenedAt: 1, settings: { ...DEFAULT_WORKSPACE_SETTINGS } })
  store.createSession({ id: 'session', workspaceId: 'workspace', model: 'test' })
  store.commitMessage('session', userMessage('question', [{ type: 'text', text: 'Original question' }], 1))
  store.commitMessage('session', assistantMessage('answer', [{ type: 'text', text: 'Original answer' }], 2))
})

afterEach(() => {
  runs.clearForTest()
  closeDatabase()
})

describe('manual compaction session coordination', () => {
  it('blocks a new run and a second compaction until the summary is committed', async () => {
    const release = pauseSummary()
    const pending = compactContext({ sessionId: 'session' })
    await vi.waitFor(() => expect(state.calls).toBe(1))
    expect(() => runs.create(request('session'))).toThrow(/会话/)
    await expect(compactContext({ sessionId: 'session' })).rejects.toThrow(/会话/)
    release()
    const result = await pending
    expect(compactBoundaryOf(result.message)).toBeDefined()
    expect(runs.isSessionBusy('session')).toBe(false)
    expect(() => runs.create(request('session'))).not.toThrow()
  })

  it('refuses to append a stale boundary when a non-run writer changed the history', async () => {
    const release = pauseSummary()
    const pending = compactContext({ sessionId: 'session' })
    await vi.waitFor(() => expect(state.calls).toBe(1))
    store.commitMessage('session', userMessage('new-question', [{ type: 'text', text: 'New input' }], 3))
    release()
    await expect(pending).rejects.toThrow(/内容已变化/)
    expect(store.getHistory('session').map((message) => message.id)).toEqual(['question', 'answer', 'new-question'])
    expect(store.getHistory('session').some((message) => compactBoundaryOf(message) !== undefined)).toBe(false)
    expect(runs.isSessionBusy('session')).toBe(false)
  })

  it('cannot compact a session with a running agent and does not call the upstream', async () => {
    runs.create(request('session'))
    await expect(compactContext({ sessionId: 'session' })).rejects.toThrow(/会话/)
    expect(state.calls).toBe(0)
  })

  it('releases the session after a summary failure or a missing session', async () => {
    state.fail = true
    await expect(compactContext({ sessionId: 'session' })).rejects.toThrow('summary failed')
    expect(runs.isSessionBusy('session')).toBe(false)
    await expect(compactContext({ sessionId: 'missing' })).rejects.toThrow(/不存在/)
    expect(runs.isSessionBusy('missing')).toBe(false)
  })
})
