/**
 * 文档引擎编辑器画布的 IPC 封装(`documentEngine:*`)。
 *
 * 需求:办公插件的画布经宿主访问文档会话(计划 §7.1)。频道字符串只在这里出现;
 * 调用方是 `views/plugins/document-engine-channel.ts`,它替插件视图转发请求。
 * 形状与不变式见 `shared/document-engine/view.ts`。
 */
import type {
  DocumentViewChanged,
  DocumentViewCommandRequest,
  DocumentViewHeaders,
  DocumentViewHeadersRequest,
  DocumentViewList,
  DocumentViewListRequest,
  DocumentViewInputRequest,
  DocumentViewInputResult,
  DocumentViewLayout,
  DocumentViewLayoutRequest,
  DocumentViewOpenRequest,
  DocumentViewOpened,
  DocumentViewRenderRequest,
  DocumentViewRenderResult,
  DocumentViewState
} from '../../../shared/document-engine/view'
import type { Unsubscribe } from '../../../shared/ipc/contract'
import { invoke, on } from './ipc'

export function openDocumentView(request: DocumentViewOpenRequest): Promise<DocumentViewOpened> {
  return invoke('documentEngine:open', request)
}

export function renderDocumentView(request: DocumentViewRenderRequest): Promise<DocumentViewRenderResult> {
  return invoke('documentEngine:render', request)
}

export function inputDocumentView(request: DocumentViewInputRequest): Promise<DocumentViewInputResult> {
  return invoke('documentEngine:input', request)
}

export function commandDocumentView(request: DocumentViewCommandRequest): Promise<DocumentViewInputResult> {
  return invoke('documentEngine:command', request)
}

export function listDocumentView(request: DocumentViewListRequest): Promise<DocumentViewList> {
  return invoke('documentEngine:list', request)
}

export function headersDocumentView(request: DocumentViewHeadersRequest): Promise<DocumentViewHeaders> {
  return invoke('documentEngine:headers', request)
}

export function layoutDocumentView(request: DocumentViewLayoutRequest): Promise<DocumentViewLayout> {
  return invoke('documentEngine:layout', request)
}

export function documentViewState(token: string): Promise<DocumentViewState> {
  return invoke('documentEngine:state', { token })
}

export function saveDocumentView(token: string): Promise<DocumentViewState> {
  return invoke('documentEngine:save', { token })
}

export function closeDocumentView(token: string): Promise<void> {
  return invoke('documentEngine:close', { token })
}

/** ★ 返回值必须进 useEffect 的 cleanup(AGENTS §1 第 4 条) */
export function onDocumentViewChanged(handler: (change: DocumentViewChanged) => void): Unsubscribe {
  return on('documentEngine:changed', handler)
}
