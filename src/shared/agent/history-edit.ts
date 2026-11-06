/**
 * 按消息 id 改写转录 —— 编辑一条提问、从某条提问起重跑、删一整轮、只删一条回复。
 *
 * 需求:渲染层只持有转录的**一页**(最近的那段,见 `sessions:getPage`),它不能再拿
 * 「手上那份」整段去 `replaceHistory`:那个操作会删掉所有不在提交列表里的消息,
 * 页外的历史会被当成「用户删掉了」一并抹去。所以改写由主进程在**完整历史**上按 id 做,
 * 渲染层只在自己那一页上做同样的改动来更新显示。
 *
 * ★ 两边必须是**同一条规则**,所以它住在 shared:主进程用它改库,渲染层用它改显示。
 * 写成两份的话,迟早出现「库里删了三条、屏幕上删了两条」。
 */
import type { AgentMessage, ContentPart } from './message'
import { isToolResultOnly, userMessage } from './message'

/**
 * 一整轮的跨度 `[start, end)`。
 *
 * ★ **删除的单位是「跨度」,不是「一条消息」。** 一轮问答在存储里是
 * `user` → `assistant`(含 tool_call)→ `user`(tool_result 回执)→ `assistant` …
 * 只删可见的那两条,留下来的 tool_result 会失去配对的 tool_call —— 对 Anthropic 形状
 * 是**非法请求**,下一次发消息才会炸。所以跨度从这条提问起,一直吃到下一条可见提问之前。
 */
export function turnSpan(messages: readonly AgentMessage[], userMessageId: string): [number, number] | null {
  const start = messages.findIndex((m) => m.id === userMessageId && m.role === 'user')
  if (start < 0) return null
  let end = start + 1
  while (end < messages.length) {
    const m = messages[end]
    if (m !== undefined && m.role === 'user' && !isToolResultOnly(m)) break
    end += 1
  }
  return [start, end]
}

/**
 * 一条助手回复的跨度 `[start, end)`:`fromId..toId` 两条助手消息之间的全部,
 * 再**吃掉紧随其后的纯工具回执**(回复以 tool_call 收尾时它的 tool_result 在后面,
 * 留下来就是一条失去配对的 tool_result)。可见提问、后台汇报、压缩边界都不吃。
 */
export function replySpan(messages: readonly AgentMessage[], fromId: string, toId: string): [number, number] | null {
  const start = messages.findIndex((m) => m.id === fromId && m.role === 'assistant')
  const last = messages.findIndex((m) => m.id === toId && m.role === 'assistant')
  if (start < 0 || last < start) return null
  let end = last + 1
  while (end < messages.length) {
    const m = messages[end]
    if (m === undefined || !isToolResultOnly(m)) break
    end += 1
  }
  return [start, end]
}

/**
 * 编辑一条提问后的转录。`truncate` = 从这条起重跑(「重新生成」):这条和它之后的全部
 * 都不要了,新的提问由调用方作为一次新 run 发出去。
 *
 * ★ 附件与其它结构化 part 留着,只把可见文字换成一份规范的 text part ——
 * 否则旧文字的碎片会在编辑之后残留下来。
 */
export function editUserMessage(
  messages: readonly AgentMessage[],
  messageId: string,
  text: string,
  truncate: boolean
): AgentMessage[] | null {
  const index = messages.findIndex((m) => m.id === messageId && m.role === 'user')
  const original = index < 0 ? undefined : messages[index]
  if (original === undefined) return null
  if (truncate) return messages.slice(0, index)
  return messages.map((message, i) => i === index ? userMessage(original.id, editedParts(original, text), original.createdAt) : message)
}

/** 编辑之后这条提问的 parts:新文字 + 原来的非文字 part */
export function editedParts(original: AgentMessage, text: string): ContentPart[] {
  return [
    ...(text === '' ? [] : [{ type: 'text' as const, text }]),
    ...original.parts.filter((part) => part.type !== 'text')
  ]
}

export function removeSpan(messages: readonly AgentMessage[], span: [number, number]): AgentMessage[] {
  return [...messages.slice(0, span[0]), ...messages.slice(span[1])]
}

/**
 * 一页转录可以从哪里开始:一条可见提问(一轮的开头)。从一轮中间切开的话,
 * 页首那条 tool_result 找不到它的 tool_call,卡片就画不出来。
 */
export function isTurnStart(message: AgentMessage): boolean {
  return message.role === 'user' && !isToolResultOnly(message)
}
