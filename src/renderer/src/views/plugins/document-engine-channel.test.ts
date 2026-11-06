/**
 * 主窗口替插件画布转发会话请求(`document-engine-channel.ts`)。
 *
 * 钉住的需求:token 不出主窗口;iframe 重载 / 切 Tab 不漏会话视图;像素转移而不拷贝;
 * 错误码原样带给视图;迟到的旧状态不把脏标记翻回去。
 */
import { describe, expect, it } from 'vitest'
import type { DocumentViewChanged, DocumentViewOpened, DocumentViewState } from '../../../../shared/document-engine/view'
import type { EngineFrameMessage } from '../../../../shared/document-engine/view-frame'
import { createEngineChannel, type EngineServices } from './document-engine-channel'

const BINDING = { workspaceId: 'ws', path: 'a.docx', pluginId: 'ncw.writer', viewType: 'ncw.writer' }
const state = (over: Partial<DocumentViewState> = {}): DocumentViewState => ({ status: 'ready', generation: 1, modelRevision: 0, savedRevision: 0, dirty: false, seq: 1, ...over })

/** 假服务:打开可以挂起(模拟起引擎很慢),记下每次关闭 */
function fakeServices(): { api: EngineServices; closed: string[]; emit: (change: DocumentViewChanged) => void; resolveOpen: (token: string) => void; rejectOpen: (error: Error) => void; calls: unknown[] } {
  const closed: string[] = []
  const calls: unknown[] = []
  let listener: ((change: DocumentViewChanged) => void) | null = null
  const pending: { resolve: (value: DocumentViewOpened) => void; reject: (error: Error) => void }[] = []
  const api: EngineServices = {
    openDocumentView: () => new Promise((resolve, reject) => { pending.push({ resolve, reject }) }),
    renderDocumentView: async (request) => {
      calls.push(request)
      // IPC 送来的 Uint8Array 可能是更大缓冲的一段视图
      const backing = new Uint8Array(64)
      return { width: 2, height: 2, format: 'rgba', bytes: backing.subarray(8, 24), generation: 1, modelRevision: 3 }
    },
    inputDocumentView: async (request) => {
      calls.push(request)
      throw new Error('[busy] cannot input while composing')
    },
    commandDocumentView: async (request) => { calls.push(request); return { modified: true, invalidations: { all: true, rects: [] }, states: { 'format.bold': 'true' }, generation: 1, modelRevision: 1 } },
    listDocumentView: async (request) => { calls.push(request); return { names: ['Heading 1'] } },
    headersDocumentView: async (request) => { calls.push(request); return { rows: [[0, ''], [255, '1']], columns: [[0, ''], [1275, 'A']] } },
    layoutDocumentView: async (request) => { calls.push(request); return { generation: 1, modelRevision: 0, layout: { width: 1 } } },
    documentViewState: async () => state({ seq: 1 }),
    saveDocumentView: async () => state({ seq: 5, savedRevision: 2, modelRevision: 2 }),
    closeDocumentView: async (token) => { closed.push(token) },
    onDocumentViewChanged: (handler) => {
      listener = handler
      return () => { listener = null }
    }
  }
  return {
    api,
    closed,
    calls,
    emit: (change) => { listener?.(change) },
    resolveOpen: (token) => { pending.shift()?.resolve({ token, path: 'a.docx', state: state(), capabilities: { format: 'docx', engineVersion: 'x', operations: [], canSave: true, canExport: [], canUndo: false, macros: { list: false, run: false } } }) },
    rejectOpen: (error) => { pending.shift()?.reject(error) }
  }
}

const flush = (): Promise<void> => new Promise((resolve) => { setTimeout(resolve, 0) })

function harness(): { fake: ReturnType<typeof fakeServices>; posted: { message: EngineFrameMessage; transfer?: Transferable[] }[]; states: DocumentViewState[]; channel: ReturnType<typeof createEngineChannel> } {
  const fake = fakeServices()
  const posted: { message: EngineFrameMessage; transfer?: Transferable[] }[] = []
  const states: DocumentViewState[] = []
  const channel = createEngineChannel({
    binding: BINDING,
    services: fake.api,
    post: (message, transfer) => { posted.push(transfer === undefined ? { message } : { message, transfer }) },
    onState: (s) => { states.push(s) }
  })
  return { fake, posted, states, channel }
}

describe('createEngineChannel', () => {
  it('opens on ready without ever handing the token to the view, then relays requests with it', async () => {
    const { fake, posted, channel } = harness()
    channel.handle({ type: 'ncw:engine:ready' })
    fake.resolveOpen('secret-token')
    await flush()
    expect(posted[0]?.message).toMatchObject({ type: 'ncw:engine:opened', path: 'a.docx' })
    expect(JSON.stringify(posted)).not.toContain('secret-token')
    channel.handle({ type: 'ncw:engine:request', id: 3, method: 'layout', params: { part: 1 } })
    await flush()
    expect(fake.calls).toEqual([{ token: 'secret-token', part: 1 }])
    expect(posted[1]?.message).toEqual({ type: 'ncw:engine:reply', id: 3, ok: true, result: { generation: 1, modelRevision: 0, layout: { width: 1 } } })
  })

  it('relays ribbon commands and list queries with the token, and drops argument values that are not strings or numbers', async () => {
    const { fake, posted, channel } = harness()
    channel.handle({ type: 'ncw:engine:ready' })
    fake.resolveOpen('tok')
    await flush()
    channel.handle({ type: 'ncw:engine:request', id: 1, method: 'command', params: { generation: 1, command: 'format.fontSize', args: { size: 20, evil: { nested: true } } } })
    channel.handle({ type: 'ncw:engine:request', id: 2, method: 'list', params: { kind: 'styles' } })
    // 只有字体 / 样式 / 部分名三种列表:别的查询(正文)画布拿不到
    channel.handle({ type: 'ncw:engine:request', id: 3, method: 'list', params: { kind: 'text' } })
    channel.handle({ type: 'ncw:engine:request', id: 4, method: 'headers', params: { x: 0, y: 0, width: 100, height: 100 } })
    channel.handle({ type: 'ncw:engine:request', id: 5, method: 'headers', params: { x: 0, y: 0, width: '100', height: 100 } })
    await flush()
    expect(fake.calls).toEqual([
      { token: 'tok', generation: 1, command: 'format.fontSize', args: { size: 20 } },
      { token: 'tok', kind: 'styles' },
      { token: 'tok', x: 0, y: 0, width: 100, height: 100 }
    ])
    expect(posted.filter((p) => p.message.type === 'ncw:engine:reply').map((p) => (p.message as { id: number }).id)).toEqual([1, 2, 4])
  })

  it('transfers tile pixels as a standalone buffer holding exactly the tile', async () => {
    const { fake, posted, channel } = harness()
    channel.handle({ type: 'ncw:engine:ready' })
    fake.resolveOpen('t')
    await flush()
    channel.handle({ type: 'ncw:engine:request', id: 1, method: 'render', params: { request: { x: 0, y: 0, tileWidth: 10, tileHeight: 10, width: 2, height: 2 } } })
    await flush()
    const reply = posted[1]
    const result = (reply?.message as { result: { pixels: ArrayBuffer } }).result
    expect(result.pixels.byteLength).toBe(16)
    expect(reply?.transfer).toEqual([result.pixels])
  })

  it('passes engine error codes through, and refuses requests before the document is open', async () => {
    const { fake, posted, channel } = harness()
    channel.handle({ type: 'ncw:engine:request', id: 1, method: 'state', params: {} })
    await flush()
    expect(posted[0]?.message).toMatchObject({ ok: false, error: { code: 'session_closed' } })
    channel.handle({ type: 'ncw:engine:ready' })
    fake.resolveOpen('t')
    await flush()
    channel.handle({ type: 'ncw:engine:request', id: 2, method: 'input', params: { generation: 1, events: [] } })
    await flush()
    expect(posted.at(-1)?.message).toEqual({ type: 'ncw:engine:reply', id: 2, ok: false, error: { code: 'busy', message: 'cannot input while composing' } })
  })

  it('reports an open failure with its code', async () => {
    const { fake, posted, channel } = harness()
    channel.handle({ type: 'ncw:engine:ready' })
    fake.rejectOpen(new Error('[engine_unavailable] plugin is disabled'))
    await flush()
    expect(posted[0]?.message).toEqual({ type: 'ncw:engine:openFailed', error: { code: 'engine_unavailable', message: 'plugin is disabled' } })
  })

  it('closes the previous view when the iframe reloads, and a late open after dispose is closed at once', async () => {
    const { fake, channel } = harness()
    channel.handle({ type: 'ncw:engine:ready' })
    fake.resolveOpen('first')
    await flush()
    // iframe 自己重载:同一个通道又收到一次 ready
    channel.handle({ type: 'ncw:engine:ready' })
    expect(fake.closed).toEqual(['first'])
    channel.dispose()
    // 起引擎很慢,打开回执在切走之后才到:这个 token 没人要了
    fake.resolveOpen('late')
    await flush()
    expect(fake.closed).toEqual(['first', 'late'])
  })

  it('forwards only its own session changes and never lets an older state overwrite a newer one', async () => {
    const { fake, posted, states, channel } = harness()
    channel.handle({ type: 'ncw:engine:ready' })
    fake.resolveOpen('mine')
    await flush()
    fake.emit({ token: 'someone-else', state: state({ seq: 9 }) })
    fake.emit({ token: 'mine', state: state({ seq: 4, modelRevision: 2, dirty: true }) })
    expect(posted.filter((p) => p.message.type === 'ncw:engine:changed')).toHaveLength(1)
    channel.handle({ type: 'ncw:engine:request', id: 1, method: 'save', params: {} })
    await flush()
    // 保存回执(seq 5)之后才到的旧推送(seq 3)不能把脏标记翻回去
    fake.emit({ token: 'mine', state: state({ seq: 3, dirty: true }) })
    expect(states.map((s) => [s.seq, s.dirty])).toEqual([[1, false], [4, true], [5, false]])
  })

  it('ignores messages that are not engine requests or have a malformed shape', async () => {
    const { fake, posted, channel } = harness()
    channel.handle({ type: 'ncw:doc:ready' })
    channel.handle({ type: 'ncw:engine:request', id: -1, method: 'state' })
    channel.handle({ type: 'ncw:engine:request', id: 1, method: 'apply', params: {} })
    channel.handle({ type: 'ncw:engine:request', id: 2, method: 'input', params: { events: 'x' } })
    channel.handle('ncw:engine:ready')
    await flush()
    expect(posted).toEqual([])
    expect(fake.calls).toEqual([])
  })
})
