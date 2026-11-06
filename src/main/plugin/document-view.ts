/**
 * 编辑器画布 ↔ 文档会话的通道(主进程侧)。
 *
 * ## 为了什么需求建的
 *
 * 办公插件的视图要画出引擎渲染的真实页面、把用户的键鼠 / 输入法交给引擎、保存,并且
 * 与 Agent 工具改的是**同一个**活动模型(计划 §7.1)。视图 iframe 没有 preload,它的请求
 * 经主窗口受信渲染层转进来,到这里时身份只剩「哪个窗口」+「打开时发的 token」。
 *
 * ## 不变式
 *
 * 1. **token 绑窗口。** 打开时记下发起窗口,之后别的窗口拿着同一个 token 一律按会话不存在
 *    拒绝(与 `DocumentSessionManager.require` 同一口径:不告诉对方「存在但不是你的」)。
 * 2. **账户作用域在打开时冻结。** 切账户之后旧 token 全部作废 —— 否则切过去的新账户能继续
 *    画、继续往上一个账户的文档里打字。
 * 3. **离开时收干净。** Tab 关闭(`close`)、窗口销毁或重载(`closeWindow`)都释放会话视图;
 *    释放前先发一次空串组字:用户组字到一半就关了 Tab 的话,引擎里的组字状态没人结束,
 *    Agent 之后的每一次读写都会收到 busy(helper 的 `requireNoComposition`)。
 *
 * ## 故意不做的
 *
 * - 不做 tile 调度与缓存:那是视图的状态(`shared/document-engine/viewport.ts` 给它算网格)。
 * - 不经插件 RPC 的租约:画布跟 Tab 走,见 `DocumentEditorView` 的注释。
 * - 不依赖 electron:窗口身份是一个数字,推送由调用方(`ipc/document-engine.ts`)落地,
 *   这一层因此能脱开 Electron 直测。
 */
import { randomUUID } from 'node:crypto'
import { DocumentEngineError } from '../../shared/document-engine/protocol'
import type { DocumentSessionSnapshot } from '../../shared/document-engine/session'
import {
  viewStateOf,
  type DocumentViewChanged,
  type DocumentViewCommandRequest,
  type DocumentViewHeaders,
  type DocumentViewHeadersRequest,
  type DocumentViewList,
  type DocumentViewListRequest,
  type DocumentViewInputRequest,
  type DocumentViewInputResult,
  type DocumentViewLayout,
  type DocumentViewLayoutRequest,
  type DocumentViewOpenRequest,
  type DocumentViewOpened,
  type DocumentViewRenderRequest,
  type DocumentViewRenderResult,
  type DocumentViewState,
  type DocumentViewTokenRequest
} from '../../shared/document-engine/view'
import type { PluginManifest } from '../../shared/plugin/manifest'
import type { DocumentSessionManager } from '../document-engine/manager'
import { PluginCapabilityError, type DocumentEditorView, type PluginDocuments } from './document-rpc'

export interface DocumentViewChannelOptions {
  documents: Pick<PluginDocuments, 'openEditorView' | 'saveEditorView' | 'closeEditorView'>
  sessions: Pick<DocumentSessionManager, 'snapshot' | 'capabilities' | 'render' | 'input' | 'query' | 'command'>
  accountScope: () => string
  /** 可运行插件的清单;插件不在 / 被停用返回 null */
  lookupPlugin: (pluginId: string) => PluginManifest | null
  /** 本地工作区的根;远程工作区与不存在的工作区返回 null(插件的文档能力只在本地工作区) */
  lookupWorkspaceRoot: (workspaceId: string) => string | null
  newToken?: () => string
}

interface ViewRecord {
  token: string
  windowId: number
  view: DocumentEditorView
}

/** 统一的「会话不存在」:不区分「不是你的」与「已经没了」,理由见文件头第 1 条 */
function noSuchView(): DocumentEngineError {
  return new DocumentEngineError('session_closed', 'no such document view')
}

export class DocumentViewChannel {
  private readonly views = new Map<string, ViewRecord>()
  private readonly newToken: () => string

  constructor(private readonly options: DocumentViewChannelOptions) {
    this.newToken = options.newToken ?? randomUUID
  }

  async open(windowId: number, request: DocumentViewOpenRequest): Promise<DocumentViewOpened> {
    const manifest = this.options.lookupPlugin(request.pluginId)
    if (manifest === null) throw new DocumentEngineError('engine_unavailable', `plugin ${request.pluginId} is not available`)
    const workspaceRoot = this.options.lookupWorkspaceRoot(request.workspaceId)
    if (workspaceRoot === null) throw new DocumentEngineError('unsupported_environment', 'document editors need a local workspace')
    const opened = await this.options.documents.openEditorView(request.pluginId, manifest, request.viewType, request.path, {
      workspaceId: request.workspaceId,
      workspaceRoot
    })
    const token = this.newToken()
    this.views.set(token, { token, windowId, view: opened.view })
    return { token, path: opened.view.path, state: viewStateOf(opened.snapshot), capabilities: opened.capabilities }
  }

  async render(windowId: number, request: DocumentViewRenderRequest): Promise<DocumentViewRenderResult> {
    const record = this.require(windowId, request.token)
    return await this.options.sessions.render({ sessionId: record.view.sessionId, scope: scopeOf(record.view), request: request.request })
  }

  async input(windowId: number, request: DocumentViewInputRequest): Promise<DocumentViewInputResult> {
    const record = this.require(windowId, request.token)
    return await this.options.sessions.input({ sessionId: record.view.sessionId, scope: scopeOf(record.view), generation: request.generation, events: request.events })
  }

  async command(windowId: number, request: DocumentViewCommandRequest): Promise<DocumentViewInputResult> {
    const record = this.require(windowId, request.token)
    return await this.options.sessions.command({
      sessionId: record.view.sessionId,
      scope: scopeOf(record.view),
      generation: request.generation,
      command: request.command,
      ...(request.args === undefined ? {} : { args: request.args })
    })
  }

  /**
   * 功能区下拉框与标签栏的数据。只放行字体、段落样式、部分名(工作表 / 幻灯片)三种:
   * 画布不读正文(那是 Agent 工具的事)。
   */
  async list(windowId: number, request: DocumentViewListRequest): Promise<DocumentViewList> {
    const record = this.require(windowId, request.token)
    const kind = request.kind === 'styles' ? 'styles' : request.kind === 'parts' ? 'outline' : 'fonts'
    const query = await this.options.sessions.query({ sessionId: record.view.sessionId, scope: scopeOf(record.view), request: { kind } })
    const result = (query.result ?? {}) as Record<string, unknown>
    const raw = kind === 'fonts' ? result.fonts : kind === 'styles' ? result.paragraphStyles : result.partNames
    return { names: Array.isArray(raw) ? raw.filter((name): name is string => typeof name === 'string').slice(0, 5000) : [] }
  }

  /** 表格行列头(可见区域,twips)。形状在这里收窄:引擎回的每一项必须是 [非负整数, 短字符串] */
  async headers(windowId: number, request: DocumentViewHeadersRequest): Promise<DocumentViewHeaders> {
    const record = this.require(windowId, request.token)
    const query = await this.options.sessions.query({
      sessionId: record.view.sessionId,
      scope: scopeOf(record.view),
      request: { kind: 'headers', x: request.x, y: request.y, width: request.width, height: request.height }
    })
    const result = (query.result ?? {}) as Record<string, unknown>
    const pairs = (raw: unknown): [number, string][] => (Array.isArray(raw) ? raw : [])
      .filter((item): item is [number, string] => Array.isArray(item) && Number.isSafeInteger(item[0]) && item[0] >= 0 && typeof item[1] === 'string' && item[1].length <= 16)
      .slice(0, 2000)
    return { rows: pairs(result.rows), columns: pairs(result.columns) }
  }

  async layout(windowId: number, request: DocumentViewLayoutRequest): Promise<DocumentViewLayout> {
    const record = this.require(windowId, request.token)
    const query = await this.options.sessions.query({
      sessionId: record.view.sessionId,
      scope: scopeOf(record.view),
      request: { kind: 'layout', ...(request.part === undefined ? {} : { part: request.part }) }
    })
    return { generation: query.generation, modelRevision: query.modelRevision, layout: query.result }
  }

  state(windowId: number, request: DocumentViewTokenRequest): DocumentViewState {
    const record = this.require(windowId, request.token)
    return viewStateOf(this.options.sessions.snapshot(record.view.sessionId, scopeOf(record.view)))
  }

  async save(windowId: number, request: DocumentViewTokenRequest): Promise<DocumentViewState> {
    const record = this.require(windowId, request.token)
    return viewStateOf(await this.options.documents.saveEditorView(record.view))
  }

  /** Tab 关闭。未知 token 静默忽略:重复关闭 / 窗口已经先一步收掉都是竞态,不是故障 */
  async close(windowId: number, request: DocumentViewTokenRequest): Promise<void> {
    const record = this.views.get(request.token)
    if (record === undefined || record.windowId !== windowId) return
    await this.release(record)
  }

  /** 窗口销毁或重载:它打开的全部视图一起释放。单个释放失败不挡住其余的 */
  async closeWindow(windowId: number): Promise<void> {
    const records = [...this.views.values()].filter((record) => record.windowId === windowId)
    await Promise.allSettled(records.map((record) => this.release(record)))
  }

  /**
   * 会话状态变了 → 该通知哪些窗口的哪些视图。由调用方把结果推给对应窗口。
   * ★ 按会话 id 匹配,不按路径:同一个文件在两个 Tab 里打开时两个视图都要收到。
   */
  changed(snapshot: DocumentSessionSnapshot): { windowId: number; payload: DocumentViewChanged }[] {
    const state = viewStateOf(snapshot)
    const out: { windowId: number; payload: DocumentViewChanged }[] = []
    for (const record of this.views.values()) {
      if (record.view.sessionId !== snapshot.sessionId) continue
      out.push({ windowId: record.windowId, payload: { token: record.token, state } })
    }
    return out
  }

  private require(windowId: number, token: string): ViewRecord {
    const record = this.views.get(token)
    if (record === undefined || record.windowId !== windowId) throw noSuchView()
    // 需求:切账户后旧 token 作废(文件头第 2 条)
    if (this.options.accountScope() !== record.view.accountScope) throw noSuchView()
    return record
  }

  private async release(record: ViewRecord): Promise<void> {
    // ★ 先删登记再释放:释放要排队等引擎,期间同一个 token 的新请求不该还能进来
    this.views.delete(record.token)
    /*
      ★ 同一会话还有别的画布(同一文件开在两个 Tab)时不取消:helper 只有一个用户视图,
      两个画布共用它的光标与组字状态,这里取消会打断另一个 Tab 里正在进行的组字。
    */
    const shared = [...this.views.values()].some((other) => other.view.sessionId === record.view.sessionId)
    if (!shared) await this.cancelComposition(record.view)
    await this.options.documents.closeEditorView(record.view).catch((error: unknown) => {
      // 释放失败只可能是账户已切 / 会话已被别处收掉,两者都不需要用户做什么
      if (!(error instanceof PluginCapabilityError)) throw error
    })
  }

  /** 文件头第 3 条。尽力而为:引擎不支持输入、会话已崩溃都直接跳过 */
  private async cancelComposition(view: DocumentEditorView): Promise<void> {
    const scope = scopeOf(view)
    try {
      if (this.options.sessions.capabilities(view.sessionId, scope).interaction?.textInput !== true) return
      const { generation } = this.options.sessions.snapshot(view.sessionId, scope)
      await this.options.sessions.input({ sessionId: view.sessionId, scope, generation, events: [{ type: 'text', action: 'compose', text: '' }] })
    } catch {
      // 取消不了说明引擎已不可用,组字状态随引擎一起没了
    }
  }
}

function scopeOf(view: DocumentEditorView): { accountScope: string; workspaceId: string } {
  return { accountScope: view.accountScope, workspaceId: view.workspaceId }
}
