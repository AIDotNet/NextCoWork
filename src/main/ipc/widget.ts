/**
 * widget 的 IPC 入口 —— 渲染层 `WidgetFrame` 代 iframe 发起的那几件事。
 *
 * ★ iframe 自己碰不到 IPC(不透明源、没有 preload);这里收到的请求都来自宿主渲染层,
 * 而宿主只在逐字段校验过 iframe 的报文之后才转发。真正的约束在 `widget-host.ts`。
 */
import { app, dialog } from 'electron'
import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { WIDGET_LIMITS } from '../../shared/domain/widget'
import type { WindowContext } from '../window/registry'
import { windows } from '../window/registry'
import { interactions } from '../kernel/interaction-gate'
import { runTopic } from '../services/agent'
import { IpcError } from './errors'
import { completeForWidget, getWidgetState, runWidgetAction, setWidgetState } from '../widget-host'

export function widgetGetState(req: { sessionId: string; callId: string }): unknown {
  return getWidgetState(req.sessionId, req.callId)
}

export function widgetSetState(req: { sessionId: string; callId: string; state: unknown }): void {
  setWidgetState(req.sessionId, req.callId, req.state)
}

/**
 * 交互模式的提交。按 callId 找挂着的那一项 —— 渲染层给不出、也不该给待决项 id:
 * widget 只知道自己是哪一次调用。授权同 `respondInteraction`:这个窗口得订阅着那个 run。
 */
export function widgetSubmit(req: { callId: string; value: string | null }, ctx: WindowContext): void {
  const pending = interactions.widgetSubmitFor(req.callId)
  if (pending === undefined || !windows.isSubscribed(runTopic(pending.runId), ctx.sender)) {
    throw new IpcError('unknown', 'This widget is no longer waiting for a submission')
  }
  if (req.value !== null && (typeof req.value !== 'string' || req.value.length > WIDGET_LIMITS.submit)) {
    throw new IpcError('unknown', 'Submitted value is too large')
  }
  interactions.respond({ id: pending.id, kind: 'widget_submit', value: req.value })
}

export function widgetRunAction(req: {
  sessionId: string
  callId: string
  actionId: string
  params: Record<string, unknown>
  confirmed: boolean
}): ReturnType<typeof runWidgetAction> {
  return runWidgetAction({ ...req, params: req.params ?? {}, confirmed: req.confirmed === true })
}

export function widgetComplete(req: { sessionId: string; prompt: string; system?: string; maxTokens?: number }): Promise<string> {
  return completeForWidget(req)
}

/**
 * 把 widget 给的一份内容存成文件。路径由系统对话框选 —— 渲染层与 iframe 都给不了路径。
 */
export async function widgetSaveFile(req: { name: string; data: string; encoding: 'utf8' | 'base64' }): Promise<{ path: string } | null> {
  if (typeof req.data !== 'string' || req.data.length > WIDGET_LIMITS.download * (req.encoding === 'base64' ? 1.4 : 1)) {
    throw new IpcError('unknown', 'File is too large')
  }
  // 控制字符按码点筛(正则里写控制字符会被 lint 拦,也确实难读)
  const clean = [...String(req.name)]
    .map((ch) => (ch.charCodeAt(0) < 0x20 || '/\\:*?"<>|'.includes(ch) ? '_' : ch))
    .join('')
    .slice(0, 120) || 'widget-export.txt'
  const dot = clean.lastIndexOf('.')
  const ext = dot > 0 ? clean.slice(dot + 1).toLowerCase() : ''
  const result = await dialog.showSaveDialog({
    defaultPath: join(app.getPath('downloads'), clean),
    ...(ext === '' ? {} : { filters: [{ name: ext.toUpperCase(), extensions: [ext] }] })
  })
  if (result.canceled || !result.filePath) return null
  const bytes = req.encoding === 'base64' ? Buffer.from(req.data, 'base64') : Buffer.from(req.data, 'utf8')
  await writeFile(result.filePath, bytes)
  return { path: result.filePath }
}
