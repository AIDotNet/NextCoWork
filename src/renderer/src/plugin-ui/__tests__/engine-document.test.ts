/**
 * 插件视图侧的文档会话客户端(`engine-document.ts`)。
 *
 * 钉住的需求:请求与回执按 id 配对(乱序到达也对得上)、错误带码、像素变成可直接喂 ImageData
 * 的数组、dispose 后没有永远挂着的 Promise,且只认父窗口来的报文(由传输层保证,这里用假传输)。
 */
import { describe, expect, it } from 'vitest'
import type { EngineFrameMessage, EngineFrameRequest } from '../../../../shared/document-engine/view-frame'
import { EngineDocumentError, openEngineDocument, type EngineTransport } from '../engine-document'

function fakeTransport(): { transport: EngineTransport; sent: EngineFrameRequest[]; deliver: (message: EngineFrameMessage) => void; listening: () => boolean } {
  const sent: EngineFrameRequest[] = []
  let handler: ((message: unknown) => void) | null = null
  return {
    sent,
    transport: {
      post: (message) => { sent.push(message) },
      listen: (next) => {
        handler = next
        return () => { handler = null }
      }
    },
    deliver: (message) => { handler?.(message) },
    listening: () => handler !== null
  }
}

const STATE = { status: 'ready' as const, generation: 1, modelRevision: 0, savedRevision: 0, dirty: false, seq: 1 }

describe('openEngineDocument', () => {
  it('asks the host to open on creation and resolves ready with what the host opened', async () => {
    const { transport, sent, deliver } = fakeTransport()
    const doc = openEngineDocument(transport)
    expect(sent).toEqual([{ type: 'ncw:engine:ready' }])
    deliver({ type: 'ncw:engine:opened', path: 'a.docx', state: STATE, capabilities: { format: 'docx', engineVersion: 'x', operations: [], canSave: true, canExport: [], canUndo: false, macros: { list: false, run: false } } })
    await expect(doc.ready).resolves.toMatchObject({ path: 'a.docx', state: { generation: 1 } })
  })

  it('pairs replies with requests by id even when they arrive out of order, and carries error codes', async () => {
    const { transport, sent, deliver } = fakeTransport()
    const doc = openEngineDocument(transport)
    const layout = doc.layout(2)
    const typing = doc.input(1, [{ type: 'text', action: 'compose', text: 'zh' }])
    const [, layoutReq, inputReq] = sent as Extract<EngineFrameRequest, { type: 'ncw:engine:request' }>[]
    expect(layoutReq).toMatchObject({ method: 'layout', params: { part: 2 } })
    deliver({ type: 'ncw:engine:reply', id: inputReq?.id ?? -1, ok: false, error: { code: 'busy', message: 'composing' } })
    deliver({ type: 'ncw:engine:reply', id: layoutReq?.id ?? -1, ok: true, result: { generation: 1, modelRevision: 0, layout: { width: 5 } } })
    await expect(layout).resolves.toMatchObject({ layout: { width: 5 } })
    const error = await typing.catch((e: unknown) => e)
    expect(error).toBeInstanceOf(EngineDocumentError)
    expect(error).toMatchObject({ code: 'busy' })
  })

  it('turns transferred pixels into a Uint8ClampedArray ready for ImageData', async () => {
    const { transport, sent, deliver } = fakeTransport()
    const doc = openEngineDocument(transport)
    const tile = doc.render({ x: 0, y: 0, tileWidth: 10, tileHeight: 10, width: 1, height: 1 })
    const pixels = new Uint8Array([1, 2, 3, 255]).buffer
    deliver({ type: 'ncw:engine:reply', id: (sent[1] as { id: number }).id, ok: true, result: { width: 1, height: 1, generation: 1, modelRevision: 0, pixels } })
    const result = await tile
    expect(result.pixels).toBeInstanceOf(Uint8ClampedArray)
    expect([...result.pixels]).toEqual([1, 2, 3, 255])
  })

  it('notifies change handlers and stops after unsubscribe', () => {
    const { transport, deliver } = fakeTransport()
    const doc = openEngineDocument(transport)
    const seen: number[] = []
    const stop = doc.onChange((state) => { seen.push(state.modelRevision) })
    deliver({ type: 'ncw:engine:changed', state: { ...STATE, modelRevision: 3 } })
    stop()
    deliver({ type: 'ncw:engine:changed', state: { ...STATE, modelRevision: 4 } })
    expect(seen).toEqual([3])
  })

  it('rejects everything still pending on dispose and refuses new requests, instead of hanging forever', async () => {
    const { transport, deliver, listening } = fakeTransport()
    const doc = openEngineDocument(transport)
    const pending = doc.save()
    doc.dispose()
    await expect(pending).rejects.toMatchObject({ code: 'session_closed' })
    await expect(doc.ready).rejects.toMatchObject({ code: 'session_closed' })
    await expect(doc.state()).rejects.toMatchObject({ code: 'session_closed' })
    expect(listening()).toBe(false)
    // 退订之后到的回执没有地方可去,也不会抛
    expect(() => { deliver({ type: 'ncw:engine:changed', state: STATE }) }).not.toThrow()
  })
})
