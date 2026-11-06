/**
 * 需求：用户消息气泡下统一展示正文文件引用和独立文件附件，路径相同只占一张卡片。
 * 只投影转录的显示数据，不修改发送给模型的正文、parts，也不推测引用来源。
 */
import type { ContentPart } from '../../../../shared/agent/message'
import { parseMentions } from '../../../../shared/domain/file-mention'

export function userMessageFileRefs(
  text: string,
  parts: readonly ContentPart[]
): Array<{ name: string; path: string }> {
  const refs = new Map<string, { name: string; path: string }>()
  // 需求：正文引用先出现，同路径的 file_ref 只补缺失项；否则下方出现重复卡片。
  for (const segment of parseMentions(text)) {
    if (segment.kind === 'mention' && !refs.has(segment.path)) {
      refs.set(segment.path, { name: segment.name, path: segment.path })
    }
  }
  for (const part of parts) {
    if (part.type === 'file_ref' && !refs.has(part.path)) {
      refs.set(part.path, { name: part.name, path: part.path })
    }
  }
  return [...refs.values()]
}
