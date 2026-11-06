/**
 * 主窗口替插件画布转发文档会话请求 —— `DocumentEngineFrame` 的全部逻辑(纯 TS,可测)。
 *
 * ## 为了什么需求建的
 *
 * 办公插件视图 iframe 没有 preload,只能 postMessage;这里把 `ncw:engine:*` 报文翻成
 * `services/document-engine.ts` 的调用,再把回执 / 状态变化送回 iframe(计划 §7.1)。
 * 逻辑放在组件外,是为了不起 Electron、不起 DOM 就能测报文的每一条分支。
 *
 * ## 不变式
 *
 * - **token 不出主窗口。** 视图只知道「我的文档」,token 与 Tab 绑定都留在这里。
 * - 每次 `ready` 都重新打开:iframe 自己重载后是一个全新的页面,旧 token 先关掉再开,
 *   否则每次重载都多挂一个会话视图,直到窗口关闭才被主进程收掉。
 * - `dispose` 之后迟到的打开回执也要关掉那个 token(打开要起引擎,可能比切 Tab 慢几秒)。
 *   不关的症状:切走的 Tab 仍占着一个会话视图,文档关不掉。
 */
import type { DocumentViewChanged, DocumentViewOpenRequest, DocumentViewOpened, DocumentViewState } from '../../../../shared/document-engine/view'
import { engineFrameError, parseEngineFrameRequest, type EngineFrameMessage, type EngineFrameRequest } from '../../../../shared/document-engine/view-frame'
import type * as services from '../../services/document-engine'

export type EngineServices = Pick<
  typeof services,
  'openDocumentView' | 'renderDocumentView' | 'inputDocumentView' | 'commandDocumentView' | 'listDocumentView' | 'headersDocumentView' | 'layoutDocumentView' | 'documentViewState' | 'saveDocumentView' | 'closeDocumentView' | 'onDocumentViewChanged'
>

export interface EngineChannelOptions {
  /** 由 Tab 绑定派生,不来自视图 */
  binding: DocumentViewOpenRequest
  services: EngineServices
  /** 发给 iframe(调用方负责 targetOrigin);`transfer` 是要转移的缓冲 */
  post: (message: EngineFrameMessage, transfer?: Transferable[]) => void
  /** 会话状态变了(打开、Agent 修改、保存):宿主据此更新 Tab 的脏标记 */
  onState?: (state: DocumentViewState) => void
}

export interface EngineChannel {
  /** 喂一条来自 iframe 的原始报文(调用方已核对来源) */
  handle: (raw: unknown) => void
  dispose: () => void
}

export function createEngineChannel(options: EngineChannelOptions): EngineChannel {
  const { services: api, post } = options
  let token: string | null = null
  let disposed = false
  /** 当前这次打开的序号:连续两次 ready 时,只有最后一次的回执算数 */
  let opening = 0
  let latestSeq = -1

  const publish = (state: DocumentViewState): void => {
    // ★ 丢弃迟到的旧状态:保存回执与推送事件可能乱序到达,旧的覆盖新的会让脏标记闪回去
    if (state.seq < latestSeq) return
    latestSeq = state.seq
    options.onState?.(state)
  }

  const unsubscribe = api.onDocumentViewChanged((change: DocumentViewChanged) => {
    if (disposed || token === null || change.token !== token) return
    publish(change.state)
    post({ type: 'ncw:engine:changed', state: change.state })
  })

  const release = (stale: string): void => {
    void api.closeDocumentView(stale).catch(() => undefined)
  }

  const open = (): void => {
    if (token !== null) {
      release(token)
      token = null
    }
    const attempt = ++opening
    latestSeq = -1
    api.openDocumentView(options.binding).then(
      (opened: DocumentViewOpened) => {
        // 需求见文件头第三条:已经销毁或被更新的一次打开取代,这个 token 没人要了
        if (disposed || attempt !== opening) {
          release(opened.token)
          return
        }
        token = opened.token
        publish(opened.state)
        post({ type: 'ncw:engine:opened', path: opened.path, state: opened.state, capabilities: opened.capabilities })
      },
      (error: unknown) => {
        if (disposed || attempt !== opening) return
        post({ type: 'ncw:engine:openFailed', error: engineFrameError(error) })
      }
    )
  }

  const reply = (id: number, work: (current: string) => Promise<{ result: unknown; transfer?: Transferable[] }>): void => {
    const current = token
    if (current === null) {
      post({ type: 'ncw:engine:reply', id, ok: false, error: { code: 'session_closed', message: 'the document is not open' } })
      return
    }
    work(current).then(
      ({ result, transfer }) => { if (!disposed) post({ type: 'ncw:engine:reply', id, ok: true, result }, transfer) },
      (error: unknown) => { if (!disposed) post({ type: 'ncw:engine:reply', id, ok: false, error: engineFrameError(error) }) }
    )
  }

  const dispatch = (request: Extract<EngineFrameRequest, { type: 'ncw:engine:request' }>): void => {
    switch (request.method) {
      case 'render':
        reply(request.id, async (current) => {
          const tile = await api.renderDocumentView({ token: current, request: request.params.request })
          // 拷进一块独立的 ArrayBuffer 再转移:IPC 给的 Uint8Array 可能是更大缓冲的一段视图
          const pixels = tile.bytes.slice().buffer
          return {
            result: { width: tile.width, height: tile.height, generation: tile.generation, modelRevision: tile.modelRevision, pixels },
            transfer: [pixels]
          }
        })
        return
      case 'input':
        reply(request.id, async (current) => {
          const result = await api.inputDocumentView({ token: current, generation: request.params.generation, events: request.params.events })
          return { result }
        })
        return
      case 'command':
        reply(request.id, async (current) => ({ result: await api.commandDocumentView({ token: current, ...request.params }) }))
        return
      case 'list':
        reply(request.id, async (current) => ({ result: await api.listDocumentView({ token: current, kind: request.params.kind }) }))
        return
      case 'headers':
        reply(request.id, async (current) => ({ result: await api.headersDocumentView({ token: current, ...request.params }) }))
        return
      case 'layout':
        reply(request.id, async (current) => ({ result: await api.layoutDocumentView({ token: current, ...request.params }) }))
        return
      case 'state':
        reply(request.id, async (current) => {
          const state = await api.documentViewState(current)
          publish(state)
          return { result: state }
        })
        return
      case 'save':
        reply(request.id, async (current) => {
          const state = await api.saveDocumentView(current)
          publish(state)
          return { result: state }
        })
        return
    }
  }

  return {
    handle: (raw) => {
      if (disposed) return
      const request = parseEngineFrameRequest(raw)
      if (request === null) return
      if (request.type === 'ncw:engine:ready') open()
      else dispatch(request)
    },
    dispose: () => {
      if (disposed) return
      disposed = true
      unsubscribe()
      if (token !== null) release(token)
      token = null
    }
  }
}
