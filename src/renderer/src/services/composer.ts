/**
 * 输入框补全的服务层 —— 组件不碰频道字符串,只调这里。
 *
 * ★ 这四条都是「拿不到就当没有」:调用方一律 `.catch(() => 空)`,任何失败都不该
 *   在输入框上变成一个错误提示(见 `main/ipc/composer.ts` 文件头)。
 */
import type { ComposerCompletionContext } from '../../../shared/domain/composer-completion'
import { invoke } from './ipc'

export function suggestNext(req: {
  requestId: string
  workspaceId: string
  sessionId: string
  triggerKey: number
  model: string
  modelProviderId?: string
}): Promise<{ requestId: string; text: string }> {
  return invoke('composer:suggestNext', req)
}

export function listPromptHistory(workspaceId: string): Promise<{ prompts: string[] }> {
  return invoke('composer:history', { workspaceId })
}

export function completeInline(req: {
  requestId: string
  workspaceId: string
  sessionId: string | null
  draft: string
  context: ComposerCompletionContext
}): Promise<{ requestId: string; text: string }> {
  return invoke('composer:complete', req)
}

export function cancelComposerRequest(requestId: string): Promise<void> {
  return invoke('composer:cancel', { requestId })
}
