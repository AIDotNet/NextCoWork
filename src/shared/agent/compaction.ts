/**
 * 压缩边界 —— 「转录里哪一段还发给模型」的唯一判据。
 *
 * 需求:上下文压缩按 Claude Code 的模型重写。压缩的产物是转录里的一条
 * `internal` user 消息,它以一个 `compact_boundary` 块打头,后面跟着续接语 + 摘要 +
 * 重附的文件。**最后一个边界之前**的消息从此不再发给模型,之后的原样发。
 *
 * 不变式:主进程装配请求(`AgentSession`)、手动 /compact(`ipc/context.ts`)、
 * 渲染层画分隔线,读的都是这里 —— 三处各写一份「从哪切」,就会复现旧检查点表那种
 * 「界面说压过了、请求里却还是全量」的错位,而且零报错。
 *
 * 故意不做的:不看消息 id、不看时间戳、不看 token 数。边界就是一条消息,
 * 删轮 / 编辑重跑把它删掉,它描述的压缩自然随之失效 —— 这正是换掉检查点表的理由。
 */
import type { AgentMessage, ContentPart } from './message'

export type CompactBoundary = Extract<ContentPart, { type: 'compact_boundary' }>

/** 这条消息是不是压缩边界;是的话返回边界块。 */
export function compactBoundaryOf(message: AgentMessage): CompactBoundary | undefined {
  for (const part of message.parts) {
    if (part.type === 'compact_boundary') return part
  }
  return undefined
}

/** 最后一个边界所在的下标;没有压缩过返回 -1。 */
export function lastCompactBoundaryIndex(messages: readonly AgentMessage[]): number {
  for (let i = messages.length - 1; i >= 0; i--) {
    const message = messages[i]
    if (message !== undefined && compactBoundaryOf(message) !== undefined) return i
  }
  return -1
}

/**
 * 发给模型的那一段:最后一个边界(含)之后的全部消息。
 *
 * ★ 边界消息自己**必须保留**:摘要就住在它的 text 块里。`compact_boundary` 块
 * 本身由编码器丢掉(同 `goal_status`),所以这里不必剥。
 */
export function messagesForModel(messages: readonly AgentMessage[]): AgentMessage[] {
  const at = lastCompactBoundaryIndex(messages)
  return at < 0 ? [...messages] : messages.slice(at)
}
