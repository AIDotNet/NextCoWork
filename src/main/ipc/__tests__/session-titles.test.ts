import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { userMessage } from '../../../shared/agent/message'

vi.mock('../../window/registry', () => ({ windows: { emitToAll: vi.fn() } }))
vi.mock('../storage', () => ({ removeSessionAttachmentFiles: vi.fn(() => ({ deleted: 0, undeletable: [] })) }))
vi.mock('../attachment', () => ({ uploadAttachment: vi.fn() }))

import { closeDatabase } from '../../db'
import { putSession } from '../../db/repo'
import { runs, type RunHandle } from '../../kernel/run-registry'
import { store } from '../../state/store'
import { windows } from '../../window/registry'
import { SESSION_DELETE_RUNNING_KEY, createSession, deleteSession, renameSession, replaceHistory } from '../sessions'
import { removeSessionAttachmentFiles } from '../storage'

beforeEach(() => { closeDatabase(); vi.clearAllMocks() })
afterEach(() => closeDatabase())

describe('session title IPC', () => {
  it('a late tab registration cannot overwrite an already-started conversation', () => {
    const session = store.ensureSession({ id: 'session', workspaceId: 'workspace', model: 'selected-model' })
    store.renameSession(session.id, 'Already titled')
    store.commitMessage(session.id, userMessage('first', [{ type: 'text', text: 'First message' }], 1))
    const result = createSession({ sessionId: session.id, workspaceId: session.workspaceId, title: '新对话' })
    expect(result).toMatchObject({ title: 'Already titled', titleSource: 'manual', model: 'selected-model' })
    expect(store.getHistory(session.id)).toHaveLength(1)
  })

  it('broadcasts the persisted title so every window can update its tabs', () => {
    const session = createSession({ workspaceId: 'workspace' })
    renameSession({ sessionId: session.id, title: '  My conversation  ' })
    expect(windows.emitToAll).toHaveBeenLastCalledWith('sessions:changed', {
      kind: 'metadata', sessionIds: [session.id],
      workspaceId: 'workspace', renamed: { sessionId: session.id, title: 'My conversation' }
    })
    expect(store.getSession(session.id)?.titleSource).toBe('manual')
  })

  it('scopes transcript edits and reports the actual deleted subtree', async () => {
    store.createSession({ id: 'parent', workspaceId: 'workspace', title: 'Parent' })
    store.createSession({ id: 'child', workspaceId: 'workspace', parentSessionId: 'parent' })
    store.createSession({ id: 'other', workspaceId: 'workspace', title: 'Other' })
    replaceHistory({ sessionId: 'parent', messages: [] })
    expect(windows.emitToAll).toHaveBeenLastCalledWith('sessions:changed', {
      kind: 'history', sessionIds: ['parent'], workspaceId: 'workspace'
    })
    await deleteSession({ sessionId: 'parent' })
    expect(windows.emitToAll).toHaveBeenLastCalledWith('sessions:changed', {
      kind: 'deleted', sessionIds: ['parent', 'child'], workspaceId: 'workspace',
      replacement: { workspaceId: 'workspace', id: 'other', title: 'Other' }
    })
    expect(store.getSession('child')).toBeUndefined()
    expect(store.getSession('other')).toBeDefined()
  })

  it.each([['first', 'middle'], ['middle', 'last'], ['last', 'middle']])('selects the adjacent replacement when deleting %s', async (deleted, replacement) => {
    for (const [index, id] of ['first', 'middle', 'last'].entries()) {
      const session = store.createSession({ id, workspaceId: 'workspace', title: id })
      putSession({ ...session, updatedAt: 3 - index })
    }
    await deleteSession({ sessionId: deleted })
    expect(windows.emitToAll).toHaveBeenLastCalledWith('sessions:changed', {
      kind: 'deleted', workspaceId: 'workspace', sessionIds: [deleted],
      replacement: { workspaceId: 'workspace', id: replacement, title: replacement }
    })
  })

  it('does not invent a replacement when no conversations remain', async () => {
    store.createSession({ id: 'last', workspaceId: 'workspace' })
    await deleteSession({ sessionId: 'last' })
    expect(windows.emitToAll).toHaveBeenLastCalledWith('sessions:changed', {
      kind: 'deleted', workspaceId: 'workspace', sessionIds: ['last']
    })
  })

  it('does not broadcast deletion or remove files if the database delete fails', async () => {
    store.createSession({ id: 'retained', workspaceId: 'workspace' })
    const remove = vi.spyOn(store, 'deleteSession').mockImplementation(() => { throw new Error('database unavailable') })
    try {
      await expect(deleteSession({ sessionId: 'retained' })).rejects.toThrow('database unavailable')
      expect(windows.emitToAll).not.toHaveBeenCalled()
      expect(removeSessionAttachmentFiles).not.toHaveBeenCalled()
      expect(store.getSession('retained')).toBeDefined()
    } finally {
      remove.mockRestore()
    }
  })

  it('blocks deletion and history edits while manual compaction owns a descendant session', async () => {
    store.createSession({ id: 'parent', workspaceId: 'workspace' })
    store.createSession({ id: 'child', workspaceId: 'workspace', parentSessionId: 'parent' })
    const release = runs.acquireSessionOperation('child')
    try {
      // 未确认时:compaction 算「真在忙」,抛给渲染层弹「停止并删除」
      await expect(deleteSession({ sessionId: 'parent' })).rejects.toMatchObject({
        code: 'conflict', localized: { messageKey: SESSION_DELETE_RUNNING_KEY }
      })
      expect(() => replaceHistory({ sessionId: 'child', messages: [] })).toThrow(/历史操作/)
      expect(store.getSession('parent')).toBeDefined()
      expect(store.getSession('child')).toBeDefined()
    } finally { release() }
    await expect(deleteSession({ sessionId: 'parent' })).resolves.toBeUndefined()
    expect(store.getSession('parent')).toBeUndefined()
  })

  it('blocks deletion only for a run inside the deleted subtree, not for unrelated runs', async () => {
    store.createSession({ id: 'retained', workspaceId: 'workspace' })
    store.createSession({ id: 'busy', workspaceId: 'workspace' })
    store.createSession({ id: 'busy-child', workspaceId: 'workspace', parentSessionId: 'busy' })
    store.createSession({ id: 'busy-grandchild', workspaceId: 'workspace', parentSessionId: 'busy-child' })
    const start = (runId: string, sessionId: string): RunHandle => runs.create({
      runId, sessionId, workspaceId: 'workspace', depth: 0, input: [], mode: 'normal', thinking: 'auto',
      webSearch: false, permissionMode: 'ask', model: 'test', skillIds: []
    })
    try {
      const run = start('run-1', 'busy')
      // 无关会话在跑 —— 不再全局禁止删除
      await expect(deleteSession({ sessionId: 'retained' })).resolves.toBeUndefined()
      expect(store.getSession('retained')).toBeUndefined()
      // 被删会话自己在跑 —— 拦下
      await expect(deleteSession({ sessionId: 'busy' })).rejects.toThrow('Agent')
      expect(store.getSession('busy')).toBeDefined()
      expect(windows.emitToAll).not.toHaveBeenCalledWith('sessions:changed', expect.objectContaining({ kind: 'deleted', sessionIds: expect.arrayContaining(['busy']) }))
      expect(removeSessionAttachmentFiles).toHaveBeenCalledTimes(1)
      run.finish('done')
      // run 落在被删会话的子代理转录上 —— 同样拦下(级联会连它一起删)
      const child = start('run-2', 'busy-child')
      await expect(deleteSession({ sessionId: 'busy' })).rejects.toThrow('Agent')
      expect(store.getSession('busy-child')).toBeDefined()
      child.finish('done')
      start('run-3', 'busy-grandchild')
      await expect(deleteSession({ sessionId: 'busy' })).rejects.toThrow('Agent')
      expect(store.getSession('busy-grandchild')).toBeDefined()
    } finally {
      runs.clearForTest()
    }
  })
})
