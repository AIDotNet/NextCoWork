/**
 * 转录按页读 —— 渲染层这一半。
 *
 * 打开一条会话只读最近的一页(`HISTORY_PAGE_SIZE`),往上翻时 `loadEarlier` 再往前取一页、
 * 接在前面;刷新时把已经翻出来的那几页一并重取,不把它们收回去。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentMessage } from '../../../../shared/agent/message'
import { assistantMessage, userMessage } from '../../../../shared/agent/message'
import type { SessionPage } from '../../../../shared/domain/session'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(async () => ({ started: true })), attachRun: vi.fn(), abortRun: vi.fn(),
  interjectRun: vi.fn(async () => {}), unwatchRun: vi.fn(async () => {}), onAgentEvent: vi.fn(() => () => {})
}))
vi.mock('../../services/app', () => ({ getSessionInput: vi.fn(async () => null), persistSessionDraft: vi.fn() }))
vi.mock('../../services/sessions', () => ({ getSessionPage: vi.fn() }))
vi.mock('../../services/goal', () => ({ getGoal: vi.fn(async () => undefined), onGoalChanged: vi.fn(() => () => {}) }))

import { getSessionPage } from '../../services/sessions'
import { HISTORY_INITIAL_PAGE_SIZE, HISTORY_PAGE_SIZE, refreshHydratedSessions, releaseSession, sessionStore } from '../session'

const turn = (n: number): AgentMessage[] => [
  userMessage(`u${n}`, [{ type: 'text', text: `问题 ${n}` }], n * 10),
  assistantMessage(`a${n}`, [{ type: 'tool_call', callId: `c${n}`, name: 'Read', input: { path: `${n}.ts` } }], n * 10 + 1),
  userMessage(`r${n}`, [{ type: 'tool_result', callId: `c${n}`, output: { content: '…' }, isError: false }], n * 10 + 2),
  assistantMessage(`b${n}`, [{ type: 'text', text: `回答 ${n}` }], n * 10 + 3)
]

const page = (messages: AgentMessage[], hasMore: boolean): SessionPage => ({
  session: { id: 'paged' } as never, messages, hasMore, messageRuns: {}, runUsage: {}, runModel: {}
})

const ids = (messages: readonly AgentMessage[]): string[] => messages.map((m) => m.id)

beforeEach(() => { vi.clearAllMocks() })
afterEach(() => { releaseSession('paged') })

describe('按页读转录', () => {
  it('★ 打开时只读最近一页,并如实标出「前面还有」', async () => {
    vi.mocked(getSessionPage).mockResolvedValueOnce(page(turn(2), true))
    const store = sessionStore('paged')

    await vi.waitFor(() => expect(ids(store.getState().transcript.messages)).toEqual(ids(turn(2))))
    // 首屏只读一小页;往上翻才按整页取
    expect(getSessionPage).toHaveBeenCalledWith('paged', HISTORY_INITIAL_PAGE_SIZE)
    expect(store.getState().historyHasMore).toBe(true)
  })

  it('★ 首页回来之前不算「读过了」—— 视图据此画骨架而不是问候语', async () => {
    let resolve!: (value: SessionPage) => void
    vi.mocked(getSessionPage).mockReturnValueOnce(new Promise((r) => { resolve = r }))
    const store = sessionStore('paged')
    expect(store.getState().historyLoaded).toBe(false)

    resolve(page(turn(1), false))
    await vi.waitFor(() => expect(store.getState().historyLoaded).toBe(true))
    // 转录与「读过了」同一次落地:不存在「已读完但转录还空着」的那一帧
    expect(ids(store.getState().transcript.messages)).toEqual(ids(turn(1)))
  })

  it('读失败不把未知历史当成空会话，重试成功后才结束加载态', async () => {
    vi.mocked(getSessionPage).mockRejectedValueOnce(new Error('boom'))
    const error = vi.spyOn(console, 'error').mockImplementation(() => {})
    const store = sessionStore('paged')
    await vi.waitFor(() => expect(store.getState().historyError).toBe('boom'))
    expect(store.getState().historyLoaded).toBe(false)
    expect(store.getState().transcript.messages).toEqual([])
    let resolve!: (value: SessionPage) => void
    vi.mocked(getSessionPage).mockReturnValueOnce(new Promise((r) => { resolve = r }))
    const retry = store.getState().retryHistory()
    expect(store.getState().historyError).toBeNull()
    expect(store.getState().historyLoaded).toBe(false)
    resolve(page(turn(1), false))
    await retry
    expect(store.getState().historyLoaded).toBe(true)
    expect(store.getState().historyError).toBeNull()
    expect(ids(store.getState().transcript.messages)).toEqual(ids(turn(1)))
    error.mockRestore()
  })

  it('尚未落盘的新会话可以正常进入空态', async () => {
    vi.mocked(getSessionPage).mockRejectedValueOnce(new Error('会话不存在: paged'))
    const store = sessionStore('paged')
    await vi.waitFor(() => expect(store.getState().historyLoaded).toBe(true))
    expect(store.getState().historyError).toBeNull()
    expect(store.getState().transcript.messages).toEqual([])
  })

  it('关闭后重开时，旧请求的回包不能结束新请求的加载态', async () => {
    let resolveOld!: (value: SessionPage) => void
    let resolveNew!: (value: SessionPage) => void
    vi.mocked(getSessionPage).mockReturnValueOnce(new Promise((r) => { resolveOld = r }))
    const previous = sessionStore('paged')
    releaseSession('paged')
    vi.mocked(getSessionPage).mockReturnValueOnce(new Promise((r) => { resolveNew = r }))
    const current = sessionStore('paged')
    expect(current).not.toBe(previous)
    resolveOld(page(turn(1), false))
    await Promise.resolve()
    await Promise.resolve()
    expect(current.getState().historyLoaded).toBe(false)
    expect(current.getState().transcript.messages).toEqual([])
    resolveNew(page(turn(2), false))
    await vi.waitFor(() => expect(current.getState().historyLoaded).toBe(true))
    expect(ids(current.getState().transcript.messages)).toEqual(ids(turn(2)))
  })

  it('首次加载期间历史被更新时，继续等待更新后的那一页', async () => {
    let resolveOld!: (value: SessionPage) => void
    let resolveNew!: (value: SessionPage) => void
    vi.mocked(getSessionPage)
      .mockReturnValueOnce(new Promise((r) => { resolveOld = r }))
      .mockReturnValueOnce(new Promise((r) => { resolveNew = r }))
    const store = sessionStore('paged')
    const refresh = refreshHydratedSessions({ kind: 'history', sessionIds: ['paged'] })
    resolveOld(page(turn(1), false))
    await vi.waitFor(() => expect(getSessionPage).toHaveBeenCalledTimes(2))
    expect(store.getState().historyLoaded).toBe(false)
    expect(store.getState().transcript.messages).toEqual([])
    resolveNew(page(turn(2), false))
    await refresh
    expect(store.getState().historyLoaded).toBe(true)
    expect(ids(store.getState().transcript.messages)).toEqual(ids(turn(2)))
  })

  it('★ 往上翻:前一页接在前面,工具卡片跟着认出来,翻到头不再显示', async () => {
    vi.mocked(getSessionPage).mockResolvedValueOnce(page(turn(2), true))
    const store = sessionStore('paged')
    await vi.waitFor(() => expect(store.getState().historyHasMore).toBe(true))

    vi.mocked(getSessionPage).mockResolvedValueOnce(page(turn(1), false))
    await store.getState().loadEarlier()

    expect(getSessionPage).toHaveBeenLastCalledWith('paged', HISTORY_PAGE_SIZE, 'u2')
    expect(ids(store.getState().transcript.messages)).toEqual([...ids(turn(1)), ...ids(turn(2))])
    expect(store.getState().transcript.tools['c1']).toBeDefined()
    expect(store.getState().historyHasMore).toBe(false)
    expect(store.getState().loadingEarlier).toBe(false)

    // 没有更早的了:再点一次什么都不做
    await store.getState().loadEarlier()
    expect(getSessionPage).toHaveBeenCalledTimes(2)
  })

  it('刷新时把已经翻出来的那几页一并重取,不收回去', async () => {
    vi.mocked(getSessionPage).mockResolvedValueOnce(page(turn(2), true))
    const store = sessionStore('paged')
    await vi.waitFor(() => expect(store.getState().historyHasMore).toBe(true))
    const loaded = Array.from({ length: 70 }, (_, i) => turn(i + 1)).flat()
    store.setState((s) => ({ transcript: { ...s.transcript, messages: loaded } }))

    vi.mocked(getSessionPage).mockResolvedValueOnce(page(loaded, false))
    await refreshHydratedSessions({ kind: 'history', sessionIds: ['paged'] })

    expect(getSessionPage).toHaveBeenLastCalledWith('paged', loaded.length)
  })
})
