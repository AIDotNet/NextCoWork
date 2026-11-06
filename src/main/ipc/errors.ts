/**
 * 把任意抛出物翻译成 AgentError。safeHandle 的唯一依赖。
 *
 * 存在的意义:§4.11 那套 code 分类是 UI 决定「跳设置页 / 提示 compact /
 * 显示重试倒计时 / 只弹个 toast」的**唯一依据**。翻译在一个地方做,
 * 就不会出现某个 handler 抛了个裸 Error、UI 拿到 code:'unknown' 却其实是 auth 失败。
 */
import { agentError, type AgentError, type AgentErrorCode } from '../../shared/agent/error'
import { isAbortError } from '../kernel/abort'
import { EnvironmentError } from '../environment/errors'
import { SessionBusyError } from '../kernel/run-registry'
import { DatabaseBusyError } from '../db'

/** 带分类的内部错误。handler 里主动拒绝时抛它,而不是裸 Error。 */
export class IpcError extends Error {
  constructor(
    readonly code: AgentErrorCode,
    message: string,
    readonly status?: number,
    readonly localized?: Pick<AgentError, 'messageKey' | 'messageParams'>
  ) {
    super(message)
    this.name = 'IpcError'
  }
}

/**
 * 尚未实现的频道。契约里已登记、但对应子系统还没到实施顺序的那一步 ——
 * 返回一个明确的错误,而不是让 ipcMain 因为「没有 handler」抛一句
 * 「No handler registered for 'x'」那种查不出所以然的话。
 */
export class NotImplementedError extends IpcError {
  constructor(channel: string, step: string) {
    super('unknown', `${channel} 尚未实现(方案实施顺序 ${step})`)
    this.name = 'NotImplementedError'
  }
}

export function toAgentError(err: unknown): AgentError {
  if (err instanceof SessionBusyError || err instanceof DatabaseBusyError) return agentError('conflict', err.message, { retryable: true })
  if (err instanceof EnvironmentError) return agentError('unknown', err.message, {
    retryable: false, environmentCode: err.code, environmentDetail: err.detail, messageKey: `environment.error.${err.code}`
  })
  if (err instanceof IpcError) {
    return agentError(err.code, err.message, { status: err.status, ...err.localized })
  }
  /**
   * ★ 中断的判断走 `kernel/abort.ts`,不在这里另写一份。
   *
   * 只看 `err.name === 'AbortError'` 会漏掉 undici 那个形状 ——
   * 它把中断包成 `TypeError: fetch failed`,真正的 AbortError 在 `cause` 上。
   * 漏掉的后果很具体:用户点了停止,却收到一个 `code:'unknown'` 的错误弹窗。
   *
   * ipc 依赖 kernel 是正确方向(kernel 零 electron、零 ipc 依赖)。
   */
  if (isAbortError(err)) return agentError('aborted', '已中断')
  if (err instanceof Error) return agentError('unknown', err.message)
  return agentError('unknown', String(err))
}
