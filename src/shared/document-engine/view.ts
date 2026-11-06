/**
 * 编辑器视图通道的形状 —— 主窗口里的办公画布如何经宿主访问**同一个**活动文档会话。
 *
 * ## 为了什么需求建的
 *
 * 办公插件的视图(iframe)要显示引擎画出的真实页面、把键鼠 / 输入法交给引擎、保存,
 * 而且必须与 Agent 工具操作同一个活动模型(计划 §7.1)。视图 iframe 没有 preload,
 * 只能经主窗口的受信渲染层转给主进程;这里定义主窗口 ↔ 主进程这一段的请求与事件。
 *
 * ## 不变式
 *
 * - **视图说不出文件名。** 打开请求里的 workspace / path / plugin / viewType 由主窗口从
 *   Tab 绑定里取,之后的每一次调用只带 `token`;主进程把 token 绑在发起打开的那个窗口上,
 *   别的窗口拿着同一个 token 也用不了。
 * - **不暴露会话内部标识。** 返回给渲染层的是修订号与状态,没有 sessionId、磁盘摘要、
 *   绝对路径 —— 渲染层不需要它们,给了就会有人拿去拼别的请求。
 *
 * ## 故意不做的
 *
 * - 不开放 apply / 正文查询:按语义改文档是 Agent 工具的事(经插件 RPC 的权限链),
 *   视图只做画布该做的事:画、输入、版面、保存。
 */
import type { DocumentCapabilities, DocumentRenderRequest, DocumentRenderResult } from './protocol'
import type { DocumentInputEvent, DocumentInputResult } from './interaction'
import type { DocumentSessionStatus } from './session'

/** 由 Tab 绑定派生的打开请求 */
export interface DocumentViewOpenRequest {
  workspaceId: string
  /** 工作区相对路径 */
  path: string
  pluginId: string
  viewType: string
}

/** 视图看得到的会话状态 */
export interface DocumentViewState {
  status: DocumentSessionStatus
  generation: number
  modelRevision: number
  savedRevision: number
  dirty: boolean
  /** 状态迁移序号:视图据此丢弃迟到的旧事件 */
  seq: number
}

export interface DocumentViewOpened {
  token: string
  /** 工作区相对路径(展示用) */
  path: string
  state: DocumentViewState
  capabilities: DocumentCapabilities
}

export interface DocumentViewTokenRequest {
  token: string
}

export interface DocumentViewRenderRequest {
  token: string
  request: DocumentRenderRequest
}

export type DocumentViewRenderResult = DocumentRenderResult & { generation: number; modelRevision: number }

export interface DocumentViewInputRequest {
  token: string
  /** 视图算坐标时用的那一代版面;引擎重启后旧坐标会指到别处,按 stale_generation 拒绝 */
  generation: number
  events: DocumentInputEvent[]
}

export type DocumentViewInputResult = DocumentInputResult & { generation: number; modelRevision: number }

/** 功能区命令:在用户光标 / 选区处执行(命令表见 `commands.ts`) */
export interface DocumentViewCommandRequest {
  token: string
  generation: number
  command: string
  args?: Record<string, string | number>
}

/** 功能区的下拉框数据:字体族名、段落样式名、工作表 / 幻灯片名(表格的标签栏) */
export interface DocumentViewListRequest {
  token: string
  kind: 'fonts' | 'styles' | 'parts'
}

/** 表格行列头请求:可见区域(twips,当前工作表) */
export interface DocumentViewHeadersRequest {
  token: string
  x: number
  y: number
  width: number
  height: number
}

/** 每行 / 列的结束位置(twips)与标签;第一项是区域起点 */
export interface DocumentViewHeaders {
  rows: [number, string][]
  columns: [number, string][]
}

export interface DocumentViewList {
  names: string[]
}

export interface DocumentViewLayoutRequest {
  token: string
  /** 表格的工作表 / 演示文稿的幻灯片;Writer 省略 */
  part?: number
}

/** 引擎 layout 查询的结果,原样透传(twips) */
export interface DocumentViewLayout {
  generation: number
  modelRevision: number
  layout: unknown
}

/** 主进程 → 打开这个视图的窗口:会话状态变了(Agent 改了、存了、崩了) */
export interface DocumentViewChanged {
  token: string
  state: DocumentViewState
}

/** 快照 → 视图状态。纯函数,主进程两处(打开回执、变更推送)共用 */
export function viewStateOf(snapshot: { status: DocumentSessionStatus; generation: number; modelRevision: number; savedRevision: number; seq: number }): DocumentViewState {
  return {
    status: snapshot.status,
    generation: snapshot.generation,
    modelRevision: snapshot.modelRevision,
    savedRevision: snapshot.savedRevision,
    dirty: snapshot.modelRevision !== snapshot.savedRevision,
    seq: snapshot.seq
  }
}
