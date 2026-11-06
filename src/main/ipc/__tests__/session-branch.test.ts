import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { buildNcwUrl } from '../../../shared/domain/attachment'
import { assistantMessage, toolResultMessage, userMessage, type AgentMessage } from '../../../shared/agent/message'

vi.mock('../../window/registry', () => ({ windows: { emitToAll: vi.fn() } }))
vi.mock('../storage', () => ({ removeSessionAttachmentFiles: vi.fn(() => ({ deleted: 0, undeletable: [] })) }))
vi.mock('../attachment', () => ({ uploadAttachment: vi.fn() }))

import { closeDatabase } from '../../db'
import { store } from '../../state/store'
import { uploadAttachment } from '../attachment'
import { IpcError } from '../errors'
import { runs } from '../../kernel/run-registry'
import { branchSession, createSkillExtractionSession, duplicateSession } from '../sessions'

/**
 * 「从这一轮分支」的主进程一侧。钉住的都是原实现里**不报错但结果错**的几件事:
 * 供应商丢失、生成图没搬家(新会话下次发送才炸)、连点建出多条、找不到消息时留下空壳。
 */

const ref = (ownerId: string, fileName: string): string =>
  buildNcwUrl({ scope: 'session', ownerId, fileName }) ?? ''

const text = (value: string): { type: 'text'; text: string } => ({ type: 'text', text: value })

let dir = ''

beforeEach(() => {
  closeDatabase()
  vi.clearAllMocks()
  dir = mkdtempSync(join(tmpdir(), 'ncw-branch-'))
  vi.mocked(uploadAttachment).mockImplementation((req) => ({ url: ref(req.ownerId ?? '', `copy-${req.displayName}`) }) as never)
})

afterEach(() => {
  closeDatabase()
  vi.restoreAllMocks()
  rmSync(dir, { recursive: true, force: true })
})

function seed(messages: AgentMessage[]): void {
  store.createSession({ id: 'src', workspaceId: 'ws', title: 'Source', model: 'alias', modelProviderId: 'provider-b', mode: 'code' })
  store.replaceHistory('src', messages)
}

const twoTurns = (): AgentMessage[] => [
  userMessage('u1', [text('first')], 1),
  assistantMessage('a1', [text('one')], 2),
  userMessage('u2', [text('second')], 3),
  assistantMessage('a2', [text('two')], 4)
]

describe('branchSession', () => {
  it('copies only up to the chosen turn, with fresh ids and the source provider', async () => {
    seed(twoTurns())
    const branched = await branchSession({ sessionId: 'src', uptoMessageId: 'u1', title: 'Source · branch' })
    expect(branched).toMatchObject({ title: 'Source · branch', model: 'alias', modelProviderId: 'provider-b', mode: 'code' })
    const history = store.getHistory(branched.id)
    expect(history.map((m) => m.parts)).toEqual([[text('first')], [text('one')]])
    expect(history.some((m) => m.id === 'u1' || m.id === 'a1')).toBe(false)
    expect(store.getHistory('src')).toHaveLength(4)
  })

  it('moves user images and generated tool images into the new session and tolerates a lost file', async () => {
    const aPath = join(dir, 'a.png')
    const bPath = join(dir, 'b.png')
    writeFileSync(aPath, 'a')
    writeFileSync(bPath, 'b')
    const rows: Record<string, string> = { 'a.png': aPath, 'b.png': bPath, 'lost.png': join(dir, 'lost.png') }
    vi.spyOn(store, 'getAttachmentRowByOwnerAndFileName').mockImplementation((_owner, fileName) =>
      rows[fileName] === undefined ? undefined : { path: rows[fileName], displayName: fileName } as never)
    seed([
      userMessage('u1', [
        { type: 'image', mime: 'image/png', dataRef: ref('src', 'a.png') },
        { type: 'image', mime: 'image/png', dataRef: ref('src', 'lost.png') }
      ], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'c1', name: 'generate_image', input: {} }], 2),
      toolResultMessage('r1', [{
        type: 'tool_result', callId: 'c1', isError: false,
        output: { content: 'ok', images: [{ mime: 'image/png', dataRef: ref('src', 'b.png') }] }
      }], 3),
      userMessage('u2', [{ type: 'image', mime: 'image/png', dataRef: ref('src', 'a.png') }], 4)
    ])

    const branched = await branchSession({ sessionId: 'src', uptoMessageId: 'u1', title: 'b' })
    const [prompt, , result] = store.getHistory(branched.id)
    expect(prompt?.parts).toEqual([
      { type: 'image', mime: 'image/png', dataRef: ref(branched.id, 'copy-a.png') },
      { type: 'image', mime: 'image/png', dataRef: ref('src', 'lost.png') }
    ])
    const toolImages = result?.parts[0]?.type === 'tool_result' ? result.parts[0].output.images : undefined
    expect(toolImages?.map((image) => image.dataRef)).toEqual([ref(branched.id, 'copy-b.png')])
    // 第二轮的 a.png 不在切点之内,不该被读/被搬
    expect(vi.mocked(uploadAttachment)).toHaveBeenCalledTimes(2)
  })

  it('collapses concurrent requests for the same turn into one new session', async () => {
    seed(twoTurns())
    const [first, second] = await Promise.all([
      branchSession({ sessionId: 'src', uptoMessageId: 'u2', title: 'b' }),
      branchSession({ sessionId: 'src', uptoMessageId: 'u2', title: 'b' })
    ])
    expect(second.id).toBe(first.id)
    expect(store.listSessions('ws').map((s) => s.id).sort()).toEqual([first.id, 'src'].sort())
    // 上一次完成之后再点,是一次新的分支
    const third = await branchSession({ sessionId: 'src', uptoMessageId: 'u2', title: 'b' })
    expect(third.id).not.toBe(first.id)
  })

  it('rejects an unknown turn without leaving an empty session behind', async () => {
    seed(twoTurns())
    await expect(branchSession({ sessionId: 'src', uptoMessageId: 'a1', title: 'b' })).rejects.toThrow('消息不存在')
    expect(store.listSessions('ws').map((s) => s.id)).toEqual(['src'])
  })
})

describe('duplicateSession', () => {
  it('keeps the whole transcript and the source provider', async () => {
    seed(twoTurns())
    const copy = await duplicateSession({ sessionId: 'src', title: 'Source copy' })
    expect(copy.modelProviderId).toBe('provider-b')
    expect(store.getHistory(copy.id)).toHaveLength(4)
  })
})

function extractionError(action: () => unknown): IpcError {
  try {
    action()
  } catch (error) {
    expect(error).toBeInstanceOf(IpcError)
    return error as IpcError
  }
  throw new Error('expected extraction to be rejected')
}

describe('createSkillExtractionSession', () => {
  it('copies the source model and remembers the source session', () => {
    seed(twoTurns())
    store.putSession({ ...store.getSession('src')!, mode: 'normal', thinking: 'high' })
    const extraction = createSkillExtractionSession({ sourceSessionId: 'src', title: 'Skill extraction' })
    expect(extraction).toMatchObject({
      workspaceId: 'ws',
      model: 'alias',
      modelProviderId: 'provider-b',
      mode: 'code',
      thinking: 'high',
      skillSource: { sessionId: 'src' }
    })
    expect(store.getHistory(extraction.id)).toEqual([])
    expect(store.getHistory('src')).toHaveLength(4)
  })

  it('rejects a missing or empty source', () => {
    expect(extractionError(() => createSkillExtractionSession({ sourceSessionId: 'missing', title: 'x' })).localized?.messageKey)
      .toBe('skills.extraction.sourceMissing')
    store.createSession({ id: 'empty', workspaceId: 'ws', title: 'Empty', model: 'alias', mode: 'code' })
    expect(extractionError(() => createSkillExtractionSession({ sourceSessionId: 'empty', title: 'x' })).localized?.messageKey)
      .toBe('skills.extraction.sourceEmpty')
  })

  it.each(['src', 'child', 'grandchild'])('refuses extraction while %s is running without creating a session', (activeId) => {
    seed(twoTurns())
    store.createSession({ id: 'child', workspaceId: 'ws', parentSessionId: 'src' })
    store.createSession({ id: 'grandchild', workspaceId: 'ws', parentSessionId: 'child' })
    vi.spyOn(runs, 'activeRunIds').mockReturnValue(['active'])
    vi.spyOn(runs, 'get').mockReturnValue({ sessionId: activeId } as ReturnType<typeof runs.get>)
    expect(extractionError(() => createSkillExtractionSession({ sourceSessionId: 'src', title: 'x' })).localized?.messageKey)
      .toBe('skills.extraction.sourceRunning')
    expect(store.listSessions('ws').map((s) => s.id)).toEqual(['src'])
  })

  it('rejects hidden child sources without creating a session', () => {
    seed(twoTurns())
    store.createSession({ id: 'child', workspaceId: 'ws', parentSessionId: 'src' })
    expect(extractionError(() => createSkillExtractionSession({ sourceSessionId: 'child', title: 'x' })).localized?.messageKey)
      .toBe('skills.extraction.sourceMissing')
    expect(store.listSessions('ws').map((s) => s.id)).toEqual(['src'])
  })

  it('rejects extracting an extraction session again', () => {
    seed(twoTurns())
    const extraction = createSkillExtractionSession({ sourceSessionId: 'src', title: 'Skill extraction' })
    expect(extractionError(() => createSkillExtractionSession({ sourceSessionId: extraction.id, title: 'nested' })).localized?.messageKey)
      .toBe('skills.extraction.nested')
  })
})
