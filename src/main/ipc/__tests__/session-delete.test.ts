import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunRequest } from '../../../shared/agent/run-request'

vi.mock('../../window/registry', () => ({ windows: { emitToAll: vi.fn() } }))
vi.mock('../storage', () => ({ removeSessionAttachmentFiles: vi.fn(() => ({ deleted: 0, undeletable: [] })) }))
vi.mock('../attachment', () => ({ uploadAttachment: vi.fn() }))

import { closeDatabase } from '../../db'
import { store } from '../../state/store'
import { runs } from '../../kernel/run-registry'
import { IpcError } from '../errors'
import { DELETE_STALE_GRACE_MS, DELETE_STOP_GRACE_MS, SESSION_DELETE_RUNNING_KEY, deleteSession } from '../sessions'

/**
 * 「会话偶尔删不掉,重启后就能删」。
 *
 * 删除的守卫曾经只问注册表「这棵子树忙不忙」,于是任何一条残留(中断了却没收尾的
 * run、收尾时没放掉的会话互斥)都会把删除永久挡住。钉住三件事:
 * - 残留不挡删除(等一小段宽限,然后强制清掉);
 * - 真在跑的 Agent 不静默失败,抛一个渲染层认得出的 conflict;
 * - 「停止并删除」先中断(连子代理),再删 —— 驱动卡死也能删。
 */

const req = (over: Partial<RunRequest>): RunRequest => ({
  runId: 'r1',
  sessionId: 'parent',
  workspaceId: 'ws',
  depth: 0,
  input: [],
  mode: 'normal',
  thinking: 'auto',
  webSearch: false,
  permissionMode: 'ask',
  model: 'test',
  skillIds: [],
  ...over
})

beforeEach(() => {
  closeDatabase()
  runs.clearForTest()
  store.createSession({ id: 'parent', workspaceId: 'ws', title: 'Parent', model: 'alias', mode: 'code' })
  store.ensureSession({ id: 'child', workspaceId: 'ws', parentSessionId: 'parent', model: 'alias', mode: 'code', thinking: 'auto', rootPathAtCreation: '' })
})

afterEach(() => {
  vi.useRealTimers()
  runs.clearForTest()
  closeDatabase()
})

describe('deleteSession', () => {
  it('空闲会话直接删,子会话跟着走', async () => {
    await deleteSession({ sessionId: 'parent' })
    expect(store.getSession('parent')).toBeUndefined()
    expect(store.getSession('child')).toBeUndefined()
  })

  it('真在跑的 Agent:抛带 messageKey 的 conflict,会话保留', async () => {
    runs.create(req({ runId: 'live', sessionId: 'parent' }))
    const error = await deleteSession({ sessionId: 'parent' }).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(IpcError)
    expect(error).toMatchObject({ code: 'conflict', localized: { messageKey: SESSION_DELETE_RUNNING_KEY } })
    expect(store.getSession('parent')).toBeDefined()
  })

  it('★ 父 run 已结束、后台子代理还在跑(渲染层看不到)也会被认出来,不是静默失败', async () => {
    const parent = runs.create(req({ runId: 'p', sessionId: 'parent' }))
    parent.finish('done')
    runs.create(req({ runId: 'c', sessionId: 'child', parentRunId: 'p', parentSessionId: 'parent', depth: 1 }))
    await expect(deleteSession({ sessionId: 'parent' })).rejects.toMatchObject({ code: 'conflict' })
  })

  it('别的会话在跑不挡这一条', async () => {
    store.createSession({ id: 'other', workspaceId: 'ws', title: 'Other', model: 'alias', mode: 'code' })
    runs.create(req({ runId: 'elsewhere', sessionId: 'other' }))
    await deleteSession({ sessionId: 'parent' })
    expect(store.getSession('parent')).toBeUndefined()
  })

  it('停止并删除:中断连同子代理,驱动正常收尾后删除', async () => {
    const parent = runs.create(req({ runId: 'p', sessionId: 'parent' }))
    const child = runs.create(req({ runId: 'c', sessionId: 'child', parentRunId: 'p', parentSessionId: 'parent', depth: 1 }))
    // 模拟正常驱动:收到中断就收尾
    for (const h of [parent, child]) h.signal.addEventListener('abort', () => { queueMicrotask(() => h.finish('aborted')) })

    await deleteSession({ sessionId: 'parent', stopRunning: true })
    expect(parent.status).toBe('aborted')
    expect(child.status).toBe('aborted')
    expect(store.getSession('parent')).toBeUndefined()
  })

  it('★ 停止并删除:驱动卡死(中断后永不收尾)也能删,残留被强制清掉', async () => {
    vi.useFakeTimers()
    const stuck = runs.create(req({ runId: 'stuck', sessionId: 'parent' }))
    runs.retainSessionForRun(stuck)

    const pending = deleteSession({ sessionId: 'parent', stopRunning: true })
    await vi.advanceTimersByTimeAsync(DELETE_STOP_GRACE_MS + 100)
    await pending
    expect(stuck.signal.aborted).toBe(true)
    expect(stuck.status).toBe('aborted')
    expect(runs.isSessionBusy('parent')).toBe(false)
    expect(store.getSession('parent')).toBeUndefined()
  })

  it('★ 残留:已中断却没收尾的 run + 没放掉的互斥,不需要用户确认,宽限之后照删', async () => {
    vi.useFakeTimers()
    const stale = runs.create(req({ runId: 'stale', sessionId: 'parent' }))
    runs.retainSessionForRun(stale)
    stale.abort({ by: 'user' })
    const ended = runs.create(req({ runId: 'ended', sessionId: 'child', parentRunId: 'stale', parentSessionId: 'parent', depth: 1 }))
    runs.retainSessionForRun(ended)
    ended.finish('done') // run 结束了,互斥却一直没放(驱动收尾没走完)

    const pending = deleteSession({ sessionId: 'parent' })
    await vi.advanceTimersByTimeAsync(DELETE_STALE_GRACE_MS + 100)
    await pending
    expect(store.getSession('parent')).toBeUndefined()
    expect(runs.isSessionBusy('parent')).toBe(false)
    expect(runs.isSessionBusy('child')).toBe(false)
  })

  it('残留自己在宽限期内收尾的话,不强制', async () => {
    const stale = runs.create(req({ runId: 'stale', sessionId: 'parent' }))
    const release = runs.retainSessionForRun(stale)
    stale.abort({ by: 'user' })
    setTimeout(() => { stale.finish('aborted'); release() }, 30)
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    await deleteSession({ sessionId: 'parent' })
    expect(warn).not.toHaveBeenCalledWith(expect.stringContaining('强制清掉'))
    warn.mockRestore()
    expect(store.getSession('parent')).toBeUndefined()
  })
})
