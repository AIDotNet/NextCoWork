/**
 * widget 宿主能力的 IPC 封装 —— 只给 `views/chat/WidgetFrame.tsx` 用。
 * 每条频道的约束写在主进程 `ipc/widget.ts` / `widget-host.ts`。
 */
import type { InvokeRes } from '../../../shared/ipc/contract'
import { invoke } from './ipc'

export function getWidgetState(sessionId: string, callId: string): Promise<unknown> {
  return invoke('widget:getState', { sessionId, callId })
}

export function setWidgetState(sessionId: string, callId: string, state: unknown): Promise<void> {
  return invoke('widget:setState', { sessionId, callId, state })
}

export function submitWidget(callId: string, value: string | null): Promise<void> {
  return invoke('widget:submit', { callId, value })
}

export function runWidgetAction(request: {
  sessionId: string
  callId: string
  actionId: string
  params: Record<string, unknown>
  confirmed: boolean
}): Promise<InvokeRes<'widget:runAction'>> {
  return invoke('widget:runAction', request)
}

export function completeForWidget(request: { sessionId: string; prompt: string; system?: string; maxTokens?: number }): Promise<string> {
  return invoke('widget:complete', request)
}

export function saveWidgetFile(name: string, data: string, encoding: 'utf8' | 'base64'): Promise<{ path: string } | null> {
  return invoke('widget:saveFile', { name, data, encoding })
}
