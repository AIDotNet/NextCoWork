/**
 * 编辑器画布的 IPC 面:`documentEngine:*` 频道 → `DocumentViewChannel`。
 *
 * 需求(计划 §7.1):主窗口里的办公画布经受信渲染层访问文档会话,与插件逻辑 / Agent 工具
 * 共用同一个 `DocumentSessionManager`。通道本身(token、账户、释放)在 `plugin/document-view.ts`,
 * 这里只做 Electron 落地的三件事:
 *
 * 1. 把请求所在的窗口换成 id 交给通道(token 绑窗口);
 * 2. **窗口销毁或重载时释放它打开的全部视图。** 重载不走 React 的 cleanup,渲染层来不及
 *    `close`;不在这里收的话,每按一次 ⌘R 就多一个永远释放不了的会话视图,干净会话的
 *    helper 进程也就永远不退;
 * 3. 会话变更定向推给打开它的那个窗口(`documentEngine:changed`),不广播。
 *
 * ★ 错误翻译成 `[code] message` 的 IpcError:渲染层据方括号里的码决定怎么办(busy 稍后重试、
 * stale_generation 重取版面、session_closed 重开),而 `toAgentError` 只认得 IpcError 的分类。
 *
 * 通道实例由 `ipc/plugins.ts` 在插件系统启动时装上、关闭时摘掉 —— 它依赖那边的会话表与
 * 插件文档门,两处必须是同一份(计划 §7.1:两条入口不能各自 new 一个 SessionManager)。
 */
import type { WebContents } from 'electron'
import { DocumentEngineError } from '../../shared/document-engine/protocol'
import type { DocumentSessionSnapshot } from '../../shared/document-engine/session'
import type {
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
  DocumentViewState,
  DocumentViewTokenRequest
} from '../../shared/document-engine/view'
import { PluginCapabilityError } from '../plugin/document-rpc'
import type { DocumentViewChannel } from '../plugin/document-view'
import type { WindowContext } from '../window/registry'
import { windows } from '../window/registry'
import { IpcError } from './errors'

let channel: DocumentViewChannel | null = null
/** 打开过画布的窗口:id → webContents。推送与生命周期挂钩都用它 */
const tracked = new Map<number, WebContents>()

/** 插件系统启动 / 关闭时由 `ipc/plugins.ts` 调用 */
export function setDocumentViewChannel(next: DocumentViewChannel | null): void {
  channel = next
  if (next === null) tracked.clear()
}

/** 会话状态变了 → 推给打开了它的那些窗口。由会话管理器的 onChange 调用 */
export function notifyDocumentViewsChanged(snapshot: DocumentSessionSnapshot): void {
  if (channel === null) return
  for (const { windowId, payload } of channel.changed(snapshot)) {
    const sender = tracked.get(windowId)
    if (sender === undefined || sender.isDestroyed()) continue
    windows.emitTo(sender, 'documentEngine:changed', payload)
  }
}

function requireChannel(): DocumentViewChannel {
  if (channel === null) throw new IpcError('unknown', '[engine_unavailable] the plugin system is not running')
  return channel
}

/** 见文件头「★ 错误翻译」 */
function translate(error: unknown): IpcError {
  if (error instanceof IpcError) return error
  if (error instanceof DocumentEngineError) return new IpcError('unknown', `[${error.code}] ${error.message}`)
  // 插件文档门的错误已经带 `[code]`(引擎错误)或是参数错误
  if (error instanceof PluginCapabilityError) return new IpcError('unknown', /^\[[a-z_]+\]/.test(error.message) ? error.message : `[invalid_operation] ${error.message}`)
  return new IpcError('unknown', `[io] ${error instanceof Error ? error.message : String(error)}`)
}

async function guarded<T>(work: () => T | Promise<T>): Promise<T> {
  try {
    return await work()
  } catch (error) {
    throw translate(error)
  }
}

/**
 * 第一次在这个窗口打开画布时挂上收尾钩子。
 * ★ `did-navigate` 只在主框架导航(含重载)时触发,插件 iframe 的加载不会触发它 ——
 *   用 `did-start-loading` 的话,每个画布 iframe 一加载就会把同窗口的全部视图收掉。
 */
function track(ctx: WindowContext): void {
  if (tracked.has(ctx.id)) return
  const sender = ctx.sender
  tracked.set(ctx.id, sender)
  const release = (): void => {
    tracked.delete(ctx.id)
    // 两个钩子一起摘:重载后下一次打开会重新挂,不摘的话每重载一次就多挂一对
    sender.removeListener('did-navigate', release)
    sender.removeListener('destroyed', release)
    void channel?.closeWindow(ctx.id).catch((error: unknown) => {
      console.warn(`[documents] releasing document views of window ${String(ctx.id)} failed: ${String(error)}`)
    })
  }
  sender.once('destroyed', release)
  sender.on('did-navigate', release)
}

export function openDocumentView(req: DocumentViewOpenRequest, ctx: WindowContext): Promise<DocumentViewOpened> {
  return guarded(async () => {
    const opened = await requireChannel().open(ctx.id, req)
    track(ctx)
    return opened
  })
}

export function renderDocumentView(req: DocumentViewRenderRequest, ctx: WindowContext): Promise<DocumentViewRenderResult> {
  return guarded(() => requireChannel().render(ctx.id, req))
}

export function inputDocumentView(req: DocumentViewInputRequest, ctx: WindowContext): Promise<DocumentViewInputResult> {
  return guarded(() => requireChannel().input(ctx.id, req))
}

export function commandDocumentView(req: DocumentViewCommandRequest, ctx: WindowContext): Promise<DocumentViewInputResult> {
  return guarded(() => requireChannel().command(ctx.id, req))
}

export function listDocumentView(req: DocumentViewListRequest, ctx: WindowContext): Promise<DocumentViewList> {
  return guarded(() => requireChannel().list(ctx.id, req))
}

export function headersDocumentView(req: DocumentViewHeadersRequest, ctx: WindowContext): Promise<DocumentViewHeaders> {
  return guarded(() => requireChannel().headers(ctx.id, req))
}

export function layoutDocumentView(req: DocumentViewLayoutRequest, ctx: WindowContext): Promise<DocumentViewLayout> {
  return guarded(() => requireChannel().layout(ctx.id, req))
}

export function documentViewState(req: DocumentViewTokenRequest, ctx: WindowContext): Promise<DocumentViewState> {
  return guarded(() => requireChannel().state(ctx.id, req))
}

export function saveDocumentView(req: DocumentViewTokenRequest, ctx: WindowContext): Promise<DocumentViewState> {
  return guarded(() => requireChannel().save(ctx.id, req))
}

export function closeDocumentView(req: DocumentViewTokenRequest, ctx: WindowContext): Promise<void> {
  // 插件系统已经关了:会话随它一起收掉了,这里没有要做的事
  if (channel === null) return Promise.resolve()
  return guarded(() => requireChannel().close(ctx.id, req))
}
