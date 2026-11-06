/**
 * 插件视图侧的文档会话客户端 —— `nextcowork/view` 的 `openEngineDocument`。
 *
 * ## 为了什么需求建的
 *
 * 绑定了文档引擎的编辑器(Word / Excel / PPT / PDF)在视图里要:拿到版面、按 tile 要像素、
 * 把键鼠 / 输入法交给引擎、保存、在 Agent 改了文档时重画(计划 §7.1)。视图 iframe 只有
 * postMessage,这里把 `ncw:engine:*` 报文(`shared/document-engine/view-frame.ts`)包成
 * Promise API,每个插件不必各写一遍请求配对。
 *
 * ## 不变式
 *
 * - **只收父窗口发来的报文**(`event.source === window.parent`)。比 origin:宿主窗口的 origin
 *   在开发与打包下不同,而 source 在两种情况下都唯一地指向宿主。
 * - `dispose` 之后所有未完成的请求立即 reject,而不是永远挂着:Tab 关了、插件视图还有一个
 *   `await render()` 没回来,是常态。
 * - 视图拿不到路径与 token:打开哪个文件由宿主从 Tab 绑定决定。
 */
import type { DocumentCapabilities, DocumentRenderRequest } from '../../../shared/document-engine/protocol'
import type { DocumentInputEvent } from '../../../shared/document-engine/interaction'
import type { DocumentViewState } from '../../../shared/document-engine/view'
import type {
  EngineFrameError,
  EngineFrameMessage,
  EngineFrameRequest,
  EngineInputResult,
  EngineLayoutResult,
  EngineRenderedTile
} from '../../../shared/document-engine/view-frame'

export interface EngineDocumentOpened {
  /** 工作区相对路径(展示用) */
  path: string
  state: DocumentViewState
  capabilities: DocumentCapabilities
}

export interface EngineTile {
  width: number
  height: number
  generation: number
  modelRevision: number
  /** 非预乘 RGBA,可直接 `new ImageData(pixels, width, height)`(ImageData 要求底层是普通 ArrayBuffer) */
  pixels: Uint8ClampedArray<ArrayBuffer>
}

/** 带码的失败:`busy` 稍后重试、`stale_generation` 重取版面、`session_closed` 文档已关 */
export class EngineDocumentError extends Error {
  constructor(readonly code: string, message: string) {
    super(message)
    this.name = 'EngineDocumentError'
  }
}

export interface EngineDocument {
  /** 打开完成时 resolve;打不开时 reject(EngineDocumentError) */
  ready: Promise<EngineDocumentOpened>
  render: (request: DocumentRenderRequest) => Promise<EngineTile>
  input: (generation: number, events: DocumentInputEvent[]) => Promise<EngineInputResult>
  /** 功能区命令(`format.bold` 等,见 capabilities.commands),在用户光标 / 选区处执行 */
  command: (generation: number, command: string, args?: Record<string, string | number>) => Promise<EngineInputResult>
  /** 功能区下拉框的数据:引擎里的字体族名 / 文档的段落样式名 */
  list: (kind: 'fonts' | 'styles' | 'parts') => Promise<string[]>
  /** 表格当前工作表的行列头:可见区域(twips)里每行 / 列的结束位置与标签,第一项是区域起点 */
  headers: (area: { x: number; y: number; width: number; height: number }) => Promise<{ rows: [number, string][]; columns: [number, string][] }>
  /**
   * 每一次 input / command 的回执(不管是谁发起的)。画布据此重画、功能区据此更新按钮状态 ——
   * 两者各自只看自己发的请求的话,工具栏点了加粗,画布不知道要重画。返回退订函数。
   */
  onResult: (handler: (result: EngineInputResult) => void) => () => void
  layout: (part?: number) => Promise<EngineLayoutResult>
  state: () => Promise<DocumentViewState>
  save: () => Promise<DocumentViewState>
  /** 会话状态变了(Agent 修改、保存、崩溃)。返回退订函数 */
  onChange: (handler: (state: DocumentViewState) => void) => () => void
  dispose: () => void
}

/** 与父窗口之间的传输。测试替换它;生产用 `windowTransport()` */
export interface EngineTransport {
  post: (message: EngineFrameRequest) => void
  listen: (handler: (message: unknown) => void) => () => void
}

export function windowTransport(): EngineTransport {
  return {
    // 宿主窗口的 origin 在开发 / 打包下不同,所以 targetOrigin 只能是 '*';收方向靠 source 核对
    post: (message) => { globalThis.parent?.postMessage(message, '*') },
    listen: (handler) => {
      const listener = (event: MessageEvent): void => {
        if (event.source !== globalThis.parent) return
        handler(event.data)
      }
      globalThis.addEventListener('message', listener)
      return () => { globalThis.removeEventListener('message', listener) }
    }
  }
}

type FrameRequest = Extract<EngineFrameRequest, { type: 'ncw:engine:request' }>
/** 一次请求的方法与参数(按方法分布的联合,参数形状随方法走) */
type RequestSpec = FrameRequest extends infer R ? (R extends FrameRequest ? { method: R['method']; params: R['params'] } : never) : never

function asError(error: EngineFrameError): EngineDocumentError {
  return new EngineDocumentError(error.code, error.message)
}

/**
 * 打开这个视图绑定的文档。★ 每个视图只调一次:每次调用都会让宿主重开一次会话视图
 * (并关掉上一个)。React 里放进 useEffect,cleanup 里 `dispose()`。
 */
export function openEngineDocument(transport: EngineTransport = windowTransport()): EngineDocument {
  const pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()
  const changeHandlers = new Set<(state: DocumentViewState) => void>()
  const resultHandlers = new Set<(result: EngineInputResult) => void>()
  const announce = (result: EngineInputResult): EngineInputResult => {
    for (const handler of resultHandlers) handler(result)
    return result
  }
  let nextId = 1
  let disposed = false
  let settleReady: { resolve: (value: EngineDocumentOpened) => void; reject: (error: Error) => void } | null = null
  const ready = new Promise<EngineDocumentOpened>((resolve, reject) => { settleReady = { resolve, reject } })
  // 没人 await ready 时,打开失败不该变成一条未处理的 rejection
  ready.catch(() => undefined)

  const stop = transport.listen((raw) => {
    if (disposed || raw === null || typeof raw !== 'object') return
    const message = raw as EngineFrameMessage
    switch (message.type) {
      case 'ncw:engine:opened':
        settleReady?.resolve({ path: message.path, state: message.state, capabilities: message.capabilities })
        return
      case 'ncw:engine:openFailed':
        settleReady?.reject(asError(message.error))
        return
      case 'ncw:engine:changed':
        for (const handler of changeHandlers) handler(message.state)
        return
      case 'ncw:engine:reply': {
        const waiter = pending.get(message.id)
        if (waiter === undefined) return
        pending.delete(message.id)
        if (message.ok) waiter.resolve(message.result)
        else waiter.reject(asError(message.error))
        return
      }
      default:
    }
  })

  const request = <T>(spec: RequestSpec): Promise<T> => {
    if (disposed) return Promise.reject(new EngineDocumentError('session_closed', 'the document view was disposed'))
    const id = nextId++
    return new Promise<T>((resolve, reject) => {
      pending.set(id, { resolve: resolve as (value: unknown) => void, reject })
      transport.post({ type: 'ncw:engine:request', id, ...spec } as FrameRequest)
    })
  }

  transport.post({ type: 'ncw:engine:ready' })

  return {
    ready,
    render: async (renderRequest) => {
      const tile = await request<EngineRenderedTile>({ method: 'render', params: { request: renderRequest } })
      return { width: tile.width, height: tile.height, generation: tile.generation, modelRevision: tile.modelRevision, pixels: new Uint8ClampedArray(tile.pixels) }
    },
    input: async (generation, events) => announce(await request<EngineInputResult>({ method: 'input', params: { generation, events } })),
    command: async (generation, command, args) =>
      announce(await request<EngineInputResult>({ method: 'command', params: { generation, command, ...(args === undefined ? {} : { args }) } })),
    list: async (kind) => (await request<{ names: string[] }>({ method: 'list', params: { kind } })).names,
    headers: (area) => request<{ rows: [number, string][]; columns: [number, string][] }>({ method: 'headers', params: area }),
    onResult: (handler) => {
      resultHandlers.add(handler)
      return () => { resultHandlers.delete(handler) }
    },
    layout: (part) => request<EngineLayoutResult>({ method: 'layout', params: part === undefined ? {} : { part } }),
    state: () => request<DocumentViewState>({ method: 'state', params: {} }),
    save: () => request<DocumentViewState>({ method: 'save', params: {} }),
    onChange: (handler) => {
      changeHandlers.add(handler)
      return () => { changeHandlers.delete(handler) }
    },
    dispose: () => {
      if (disposed) return
      disposed = true
      stop()
      changeHandlers.clear()
      resultHandlers.clear()
      const closed = new EngineDocumentError('session_closed', 'the document view was disposed')
      for (const waiter of pending.values()) waiter.reject(closed)
      pending.clear()
      settleReady?.reject(closed)
    }
  }
}
