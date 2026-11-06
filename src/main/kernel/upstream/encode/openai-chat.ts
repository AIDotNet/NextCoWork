import type { AgentMessage, ContentPart } from '../../../../shared/agent/message'
import { fileRefMarkdown } from '../../../../shared/agent/message'
import { REQUEST_PATH } from '../../../../shared/domain/baseurl'
import type { CanonicalRequest } from '../canonical'
import type { EncodedRequest } from './anthropic'

type ChatContent = { type: 'text'; text: string } | { type: 'image_url'; image_url: { url: string } }
interface ChatMessage {
  role: 'system' | 'user' | 'assistant' | 'tool'
  content: string | ChatContent[] | null
  tool_call_id?: string
  tool_calls?: Array<{ id: string; type: 'function'; function: { name: string; arguments: string } }>
  reasoning_content?: string
}

/** Only replay reasoning from this protocol, never an Anthropic signature or Responses item. */
function isChatReasoning(part: Extract<ContentPart, { type: 'thinking' }>): boolean {
  const opaque = part.opaque as { protocol?: unknown; field?: unknown } | undefined
  return opaque?.protocol === 'openai-chat' && opaque.field === 'reasoning_content'
}

export function toOpenAIChatMessages(messages: readonly AgentMessage[]): ChatMessage[] {
  const out: ChatMessage[] = []
  for (const message of messages) {
    const content: ChatContent[] = []
    const calls: NonNullable<ChatMessage['tool_calls']> = []
    let reasoning: string | undefined
    // Tool receipts must immediately follow the assistant call, before any user reminders.
    for (const part of message.parts) {
      // UI-only: no content or message on the wire.(边界的摘要在同一条消息的 text 块里)
      if (part.type === 'goal_status' || part.type === 'compact_boundary') continue
      if (part.type === 'tool_result') {
        out.push({ role: 'tool', tool_call_id: part.callId, content: part.output.content })
        // Chat Completions tool messages cannot carry image parts. Keep every receipt first,
        // then append screenshots in the user message emitted below so tool-call adjacency holds.
        for (const image of part.output.images ?? []) {
          content.push({ type: 'image_url', image_url: { url: image.dataRef } })
        }
      } else if (part.type === 'text' && part.text !== '') {
        content.push({ type: 'text', text: part.text })
      } else if (part.type === 'image') {
        content.push({ type: 'image_url', image_url: { url: part.dataRef } })
      } else if (part.type === 'file_ref') {
        // ★ 不读文件、不传字节 —— 只把路径当 markdown 链接告诉模型,它自己用工具去读。
        content.push({ type: 'text', text: fileRefMarkdown(part) })
      } else if (part.type === 'tool_call' && message.role === 'assistant') {
        calls.push({
          id: part.callId,
          type: 'function',
          function: { name: part.name, arguments: JSON.stringify(part.input ?? {}) }
        })
      } else if (part.type === 'thinking' && message.role === 'assistant' && isChatReasoning(part)) {
        reasoning = (reasoning ?? '') + part.text
      }
    }
    if (content.length === 0 && calls.length === 0) continue
    out.push({
      role: message.role,
      content: content.length === 0 ? null
        : content.every((p) => p.type === 'text') ? content.map((p) => (p as { text: string }).text).join('')
          : content,
      ...(calls.length === 0 ? {} : { tool_calls: calls }),
      ...(reasoning === undefined ? {} : { reasoning_content: reasoning })
    })
  }
  return out
}

export function encodeOpenAIChat(req: CanonicalRequest, model: string, apiKey: string): EncodedRequest {
  const messages = toOpenAIChatMessages(req.messages)
  if (req.system !== '') messages.unshift({ role: 'system', content: req.system })
  // o-series/GPT-5+ require max_completion_tokens. Compatible providers (DeepSeek,
  // GLM, etc.) still use max_tokens; model request patches can override either.
  const modernTokens = /(?:^|\/)(?:o[1-9](?:$|[-.])|gpt-(?:[5-9]|\d{2,})(?:$|[-.]))/i.test(model)
  return {
    path: REQUEST_PATH['openai-chat'],
    headers: { 'content-type': 'application/json', authorization: `Bearer ${apiKey}` },
    body: {
      model,
      messages,
      stream: true,
      stream_options: { include_usage: true },
      [modernTokens ? 'max_completion_tokens' : 'max_tokens']: req.maxOutputTokens,
      ...(req.temperature === undefined ? {} : { temperature: req.temperature }),
      ...(req.stopSequences?.length ? { stop: req.stopSequences } : {}),
      ...(req.tools.length === 0 ? {} : {
        tools: req.tools.map((tool) => ({
          type: 'function',
          function: { name: tool.externalName, description: tool.description, parameters: tool.inputSchema }
        })),
        tool_choice: 'auto'
      })
    }
  }
}
