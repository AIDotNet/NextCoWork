import type { AgentMessage } from '../../../shared/agent/message'
import type { ContextPreview } from '../../../shared/agent/context-management'
import type { ContextPreviewRequest } from '../../../shared/ipc/contract'
import { invoke } from './ipc'

/**
 * 手动压缩上下文。返回**边界消息本身**,以及压缩后估算的输入 token。
 *
 * 需求:压缩的产物是转录里的一条消息(带 `compact_boundary` 块),不再是一张单独的
 * 检查点表。调用方拿到这条消息不必自己插进界面 —— 主进程已经把它 commit 进转录,
 * 事件泵会推过来;这里的返回值只用来做「压完剩多少」这类即时反馈。
 */
export function compactContext(sessionId: string, instructions?: string): Promise<{ message: AgentMessage; inputTokens: number }> {
  return invoke('context:compact', { sessionId, ...(instructions === undefined ? {} : { instructions }) })
}

/**
 * 还没发过请求时的占用归因 —— 主进程装配一次但不发出去。
 *
 * ★ 在**菜单打开时**拉,不在挂载时拉:它每次都要把系统提示词和整份工具清单
 * 重新估一遍,而绝大多数会话从头到尾都不会有人点开那个圆环。
 */
export function previewContext(req: ContextPreviewRequest): Promise<ContextPreview | undefined> {
  return invoke('context:preview', req)
}
