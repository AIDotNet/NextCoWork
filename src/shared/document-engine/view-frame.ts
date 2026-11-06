/**
 * 插件画布 iframe ↔ 主窗口之间的报文(`ncw:engine:*`)。
 *
 * ## 为了什么需求建的
 *
 * 办公插件的视图跑在 `ncw-plugin://<id>` 的 iframe 里,没有 preload,只能 postMessage 给主窗口;
 * 主窗口(`views/plugins/document-engine-channel.ts`)替它转给主进程。两端(宿主渲染层与视图
 * 运行时 `plugin-ui/engine-document.ts`)共用这里的报文定义与收窄,形状只写一份。
 *
 * ## 不变式
 *
 * - **报文里没有路径、没有 token。** 打开哪个文件由主窗口从 Tab 绑定决定,token 也只留在
 *   主窗口;视图拿到的能力上界因此正好是「这个 Tab 的那个文件」(同 `ncw:doc:*` 的立场)。
 * - 视图发来的一切都不可信:方法名是有限集合,参数形状在这里先收窄一遍,细节范围再由
 *   主进程的 `validateInputEvents` / `validateRenderRequest` 核对。
 * - 渲染结果的像素以 `ArrayBuffer` **转移**给视图,不拷贝:一块 512² 的 tile 是 1 MiB,
 *   滚动时每秒几十块,拷贝会让主线程掉帧。
 */
import type { DocumentCapabilities, DocumentRenderRequest } from './protocol'
import type { DocumentInputEvent, DocumentInputResult } from './interaction'
import type { DocumentViewState } from './view'

export const ENGINE_METHODS = ['render', 'input', 'command', 'list', 'headers', 'layout', 'state', 'save'] as const
export type EngineMethod = typeof ENGINE_METHODS[number]

/** 视图 → 主窗口 */
export type EngineFrameRequest =
  | { type: 'ncw:engine:ready' }
  | { type: 'ncw:engine:request'; id: number; method: 'render'; params: { request: DocumentRenderRequest } }
  | { type: 'ncw:engine:request'; id: number; method: 'input'; params: { generation: number; events: DocumentInputEvent[] } }
  | { type: 'ncw:engine:request'; id: number; method: 'command'; params: { generation: number; command: string; args?: Record<string, string | number> } }
  | { type: 'ncw:engine:request'; id: number; method: 'list'; params: { kind: 'fonts' | 'styles' | 'parts' } }
  | { type: 'ncw:engine:request'; id: number; method: 'headers'; params: { x: number; y: number; width: number; height: number } }
  | { type: 'ncw:engine:request'; id: number; method: 'layout'; params: { part?: number } }
  | { type: 'ncw:engine:request'; id: number; method: 'state' | 'save'; params: Record<string, never> }

/** 失败回执里的错误:`code` 是 `DocumentErrorCode` 的字符串(busy / stale_generation / session_closed …) */
export interface EngineFrameError {
  code: string
  message: string
}

/** 渲染结果:像素是转移过来的 ArrayBuffer(非预乘 RGBA,每行 width*4 字节) */
export interface EngineRenderedTile {
  width: number
  height: number
  generation: number
  modelRevision: number
  pixels: ArrayBuffer
}

export interface EngineLayoutResult {
  generation: number
  modelRevision: number
  layout: unknown
}

export type EngineInputResult = DocumentInputResult & { generation: number; modelRevision: number }

/** 主窗口 → 视图 */
export type EngineFrameMessage =
  | { type: 'ncw:engine:opened'; path: string; state: DocumentViewState; capabilities: DocumentCapabilities }
  | { type: 'ncw:engine:openFailed'; error: EngineFrameError }
  | { type: 'ncw:engine:reply'; id: number; ok: true; result: unknown }
  | { type: 'ncw:engine:reply'; id: number; ok: false; error: EngineFrameError }
  | { type: 'ncw:engine:changed'; state: DocumentViewState }

const MAX_REQUEST_ID = Number.MAX_SAFE_INTEGER

function recordOf(raw: unknown): Record<string, unknown> | null {
  return raw !== null && typeof raw === 'object' && !Array.isArray(raw) ? (raw as Record<string, unknown>) : null
}

/**
 * 收窄视图发来的报文。认不出返回 null(调用方静默丢弃 —— 页面里别的脚本也会 postMessage,
 * 不是每一条都是给我们的)。只核形状,不核数值范围(那是主进程的事,只写一处)。
 */
export function parseEngineFrameRequest(raw: unknown): EngineFrameRequest | null {
  const message = recordOf(raw)
  if (message === null) return null
  if (message.type === 'ncw:engine:ready') return { type: 'ncw:engine:ready' }
  if (message.type !== 'ncw:engine:request') return null
  const id = message.id
  if (typeof id !== 'number' || !Number.isSafeInteger(id) || id < 0 || id > MAX_REQUEST_ID) return null
  const params = recordOf(message.params) ?? {}
  switch (message.method) {
    case 'render': {
      const request = recordOf(params.request)
      if (request === null) return null
      return { type: 'ncw:engine:request', id, method: 'render', params: { request: request as unknown as DocumentRenderRequest } }
    }
    case 'input': {
      if (typeof params.generation !== 'number' || !Array.isArray(params.events)) return null
      return { type: 'ncw:engine:request', id, method: 'input', params: { generation: params.generation, events: params.events as DocumentInputEvent[] } }
    }
    case 'command': {
      if (typeof params.generation !== 'number' || typeof params.command !== 'string') return null
      const args = params.args === undefined ? undefined : recordOf(params.args)
      if (args === null) return null
      // 只留字符串 / 数字值;细节范围由主进程的 `validateCommand` 核对
      const clean: Record<string, string | number> = {}
      for (const [key, value] of Object.entries(args ?? {})) if (typeof value === 'string' || typeof value === 'number') clean[key] = value
      return { type: 'ncw:engine:request', id, method: 'command', params: { generation: params.generation, command: params.command, ...(args === undefined ? {} : { args: clean }) } }
    }
    case 'list':
      if (params.kind !== 'fonts' && params.kind !== 'styles' && params.kind !== 'parts') return null
      return { type: 'ncw:engine:request', id, method: 'list', params: { kind: params.kind } }
    case 'headers': {
      const { x, y, width, height } = params
      if (typeof x !== 'number' || typeof y !== 'number' || typeof width !== 'number' || typeof height !== 'number') return null
      return { type: 'ncw:engine:request', id, method: 'headers', params: { x, y, width, height } }
    }
    case 'layout': {
      if (params.part !== undefined && typeof params.part !== 'number') return null
      return { type: 'ncw:engine:request', id, method: 'layout', params: params.part === undefined ? {} : { part: params.part } }
    }
    case 'state':
    case 'save':
      return { type: 'ncw:engine:request', id, method: message.method, params: {} }
    default:
      return null
  }
}

/**
 * 从宿主抛出的错误里取出 `[code] message`(主进程 `ipc/document-engine.ts` 的翻译格式)。
 * 取不出码的一律当 `io`:视图据码决定重试与否,未知错误不该被当成「稍后重试就好」。
 */
export function engineFrameError(error: unknown): EngineFrameError {
  const text = error instanceof Error ? error.message : String(error)
  const match = /^\[([a-z_]+)\]\s*(.*)$/s.exec(text)
  if (match === null) return { code: 'io', message: text }
  return { code: match[1] ?? 'io', message: match[2] ?? '' }
}
