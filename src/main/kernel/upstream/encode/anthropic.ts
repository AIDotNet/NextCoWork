/**
 * CanonicalRequest → Anthropic Messages API 请求体。
 *
 * 方案 §5.1 的 `encodeUpstream` 三份之一。内核只用右半边(encode + decode),
 * 网关四组全用。
 */
import type { AgentMessage, ContentPart } from '../../../../shared/agent/message'
import { fileRefMarkdown } from '../../../../shared/agent/message'
import type { ToolInfo } from '../../../../shared/agent/tool'
import {
  normalizeAnthropicCacheTtl,
  type AnthropicCacheTtl,
  type ReasoningReplay
} from '../../../../shared/domain/provider'
import type { CanonicalRequest } from '../canonical'

interface AnthropicMessage {
  role: 'user' | 'assistant'
  content: unknown[]
}

/**
 * ★ thinking 块的 signature 从 `ContentPart.opaque` 里取。
 *
 * 形状由 decode 侧写入(见 decode/anthropic.ts),这里只认这一个键。
 * 两侧都只碰 `opaque.signature`,内核其余部分对它一无所知 —— 这正是
 * 「透传逃生舱」的含义:我们负责搬运,不负责理解。
 */
function signatureOf(opaque: unknown): string | undefined {
  if (typeof opaque !== 'object' || opaque === null) return undefined
  const sig = (opaque as { signature?: unknown }).signature
  return typeof sig === 'string' ? sig : undefined
}

function redactedOf(opaque: unknown): string | undefined {
  if (typeof opaque !== 'object' || opaque === null) return undefined
  const d = (opaque as { redacted?: unknown }).redacted
  return typeof d === 'string' ? d : undefined
}

/**
 * 一个 ContentPart → 一个 Anthropic 内容块;返回 null = 这个 part 不上行。
 *
 * 「不上行」的三种情况都不是遗漏,是**故意的**,见每一处的理由。
 */
function toBlock(p: ContentPart): unknown | null {
  switch (p.type) {
    case 'text':
      // ★ 空 text 块是 400(`text content blocks must be non-empty`)。
      // 它很容易产生:中断在第一个 delta 之前、或模型直接以 tool_use 开场。
      return p.text === '' ? null : { type: 'text', text: p.text }

    case 'thinking': {
      const redacted = redactedOf(p.opaque)
      if (redacted !== undefined) return { type: 'redacted_thinking', data: redacted }
      const signature = signatureOf(p.opaque)
      // ★ 没有 signature 的 thinking 块**不能回传** —— Anthropic 会拒
      // (`thinking blocks require a signature`)。丢掉整块比带一个假签名安全:
      // 丢掉只是少了一段推理上下文,带假签名是 400,整轮请求都废了。
      if (signature === undefined || p.text === '') return null
      return { type: 'thinking', thinking: p.text, signature }
    }

    case 'tool_call':
      return { type: 'tool_use', id: p.callId, name: p.name, input: p.input ?? {} }

    case 'tool_result': {
      // ★ `output.card` remains UI-only. `images` is the one explicit binary path because
      // browser_screenshot must be visible to the model rather than rendered as base64 text.
      const images = (p.output.images ?? []).map((image) => ({
        type: 'image',
        source: {
          type: 'base64',
          media_type: image.mime,
          data: image.dataRef.slice(`data:${image.mime};base64,`.length)
        }
      }))
      return {
        type: 'tool_result',
        tool_use_id: p.callId,
        content: images.length === 0
          ? p.output.content
          : [{ type: 'text', text: p.output.content }, ...images],
        is_error: p.isError
      }
    }

    case 'subagent':
      // 步骤 11:子代理的摘要作为 ToolResult 返回,所以到这里时它已经是
      // tool_result 了。这个 part 只存在于 UI 那一轨(可展开节点)。
      return null

    case 'image': {
      // The router resolves ncw:// attachments into an outgoing-only data URL.
      const prefix = `data:${p.mime};base64,`
      if (!p.dataRef.startsWith(prefix)) return null
      return { type: 'image', source: { type: 'base64', media_type: p.mime, data: p.dataRef.slice(prefix.length) } }
    }

    case 'file_ref':
      // ★ 不读文件、不传字节 —— 只把路径当 markdown 链接告诉模型,它自己用工具去读。
      return { type: 'text', text: fileRefMarkdown(p) }

    case 'error':
      // 错误只属于 UI 那一轨。把它回传给模型,模型就会开始为我们的 bug 道歉。
      return null

    case 'goal_status':
      // 同上:目标的成绩单是我们自己盖的章。回传给模型,它会开始为
      // 「上一轮没达成」辩解 —— 既没用,又占窗口。
      return null

    case 'compact_boundary':
      // 压缩边界只是一枚书签:摘要正文就在同一条消息的 text 块里,模型读那一份。
      // 把这里的 summary 也编码上去,等于把摘要发两遍。
      return null

    default:
      return null
  }
}

/**
 * AgentMessage[] → Anthropic messages[]。
 *
 * 两条规则都是踩出来的:
 * 1. **空 content 的消息是 400**,所以整条跳过;
 * 2. **相邻同角色消息要合并**。这在我们的模型里很常见:一轮工具调用产生
 *    「assistant(tool_use) → user(tool_result) → assistant(text)」,但并行工具
 *    或中断补偿会产生两条连续的 user 消息。Anthropic 要求严格交替。
 */
export function toAnthropicMessages(messages: readonly AgentMessage[]): AnthropicMessage[] {
  const out: AnthropicMessage[] = []
  for (const m of messages) {
    const content = m.parts.map(toBlock).filter((b): b is object => b !== null)
    if (content.length === 0) continue

    const last = out[out.length - 1]
    if (last !== undefined && last.role === m.role) last.content.push(...content)
    else out.push({ role: m.role, content })
  }
  return out
}

export function toAnthropicTools(tools: readonly ToolInfo[]): unknown[] {
  return tools.map((t) => ({
    // ★ 下发的是 externalName(≤64 且字符受限),不是 internalId(方案 §4.3)。
    // 反向解析在 ToolRegistry.resolveByExternalName。
    name: t.externalName,
    description: t.description,
    input_schema: t.inputSchema
  }))
}

export interface EncodedRequest {
  /** 相对路径,由 joinUpstreamUrl 与 baseUrl 拼接 */
  path: string
  headers: Record<string, string>
  body: unknown
}

/**
 * 编码期的上游事实 —— 由路由器判定,随请求下发给编码器。
 *
 * 原先叫 `AnthropicEncodeOptions`,但它早就不只服务 Anthropic:经 `codec.ts` 同时
 * 传给三家编码器,`reasoningReplay` 进来之后再叫那个名字就是撒谎了。
 * `applyAnthropicRequestOptions` 保持原名 —— 那个函数是真的只做 Anthropic 边界。
 */
export interface UpstreamEncodeOptions {
  /** Stable opaque workspace identifier; never a path, API key, name, or email. */
  userId: string
  cacheTtl: AnthropicCacheTtl
  /**
   * Responses 协议回传历史思考的方言,判定见 `reasoningReplayFor`。
   * 缺席 = `opaque-only`;其余协议不读它。
   */
  reasoningReplay?: ReasoningReplay
}

type AnthropicCacheControl = { type: 'ephemeral'; ttl?: '1h' }

function cacheControl(ttl: AnthropicCacheTtl): AnthropicCacheControl {
  // Anthropic's default is 5 minutes. Omitting `ttl: "5m"` also keeps older
  // Anthropic-compatible relays working while 1h must be explicit.
  return ttl === '1h' ? { type: 'ephemeral', ttl: '1h' } : { type: 'ephemeral' }
}

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined
}

/**
 * Remove adapter-supplied breakpoints before applying the Provider policy.
 * Anthropic currently accepts cache_control directly on system content blocks
 * and tool definitions, so only those direct children need to be inspected.
 */
function withoutCacheBreakpoints(value: unknown): unknown {
  if (!Array.isArray(value)) return value
  return value.map((item) => {
    const block = record(item)
    if (block === undefined || !Object.hasOwn(block, 'cache_control')) return item
    const { cache_control: _ignored, ...rest } = block
    return rest
  })
}

/**
 * Apply request-scoped Anthropic identity and the Provider-owned cache policy.
 *
 * This function is intentionally reusable after model-level request patches.
 * Those patches may customise ordinary wire parameters, but they must not be
 * able to replace metadata.user_id, disable mandatory caching, or change a
 * Provider's TTL. Reapplying the policy at the final wire boundary also keeps
 * the explicit breakpoint and top-level automatic cache on exactly the same TTL.
 */
export function applyAnthropicRequestOptions(
  body: unknown,
  options: UpstreamEncodeOptions
): Record<string, unknown> {
  const source = record(body)
  if (source === undefined) throw new TypeError('Anthropic 请求体必须是一个对象')

  const next = structuredClone(source)
  const userId = typeof options.userId === 'string' ? options.userId : ''
  const cacheTtl = normalizeAnthropicCacheTtl(options.cacheTtl)
  const caching = cacheControl(cacheTtl)

  // Preserve unrelated adapter metadata, but make the reserved identity field
  // authoritative. A patch may add a harmless relay-specific metadata key; it
  // may not replace the workspace id with a name, path, or another tenant.
  next.metadata = { ...(record(next.metadata) ?? {}), user_id: userId }

  // Provider settings are authoritative. Clear adapter-supplied breakpoints
  // before restoring mandatory caching with the same TTL at every boundary.
  if (Object.hasOwn(next, 'system')) next.system = withoutCacheBreakpoints(next.system)
  if (Object.hasOwn(next, 'tools')) next.tools = withoutCacheBreakpoints(next.tools)
  next.cache_control = { ...caching }

  // A non-empty system prefix is the preferred stable breakpoint. The normal
  // encoder supplies a string and therefore retains the required single-block
  // shape. If an adapter supplied a block array, marking its final block keeps
  // the same tools → system → messages prefix semantics.
  if (typeof next.system === 'string' && next.system !== '') {
    next.system = [{ type: 'text', text: next.system, cache_control: { ...caching } }]
    return next
  }
  if (Array.isArray(next.system) && next.system.length > 0) {
    for (let index = next.system.length - 1; index >= 0; index--) {
      const block = record(next.system[index])
      if (block === undefined) continue
      next.system[index] = { ...block, cache_control: { ...caching } }
      return next
    }
  }

  // With no system prompt, the final tool is the last stable block in the
  // Anthropic cache order. If neither exists, the top-level automatic marker
  // above is sufficient for the growing conversation history.
  if (Array.isArray(next.tools) && next.tools.length > 0) {
    for (let index = next.tools.length - 1; index >= 0; index--) {
      const tool = record(next.tools[index])
      if (tool === undefined) continue
      next.tools[index] = { ...tool, cache_control: { ...caching } }
      break
    }
  }
  return next
}

export function encodeAnthropic(
  req: CanonicalRequest,
  upstreamModel: string,
  apiKey: string
): EncodedRequest
export function encodeAnthropic(
  req: CanonicalRequest,
  upstreamModel: string,
  apiKey: string,
  options: UpstreamEncodeOptions
): EncodedRequest
export function encodeAnthropic(
  req: CanonicalRequest,
  upstreamModel: string,
  apiKey: string,
  options?: UpstreamEncodeOptions
): EncodedRequest {
  // Keep the three-argument form source-compatible for older gateway callers.
  // Production routing always supplies a validated workspace id. Older callers
  // still get mandatory 5m caching with an empty (non-production) metadata value.
  const userId = typeof options?.userId === 'string' ? options.userId : ''
  const cacheTtl = normalizeAnthropicCacheTtl(options?.cacheTtl)
  const body: Record<string, unknown> = {
    model: upstreamModel,
    max_tokens: req.maxOutputTokens,
    messages: toAnthropicMessages(req.messages),
    stream: true
  }
  const tools = req.tools.length > 0 ? toAnthropicTools(req.tools) : []

  if (req.system !== '') body.system = req.system
  if (tools.length > 0) body.tools = tools
  if (req.stopSequences?.length) body.stop_sequences = req.stopSequences

  if (req.thinkingBudget !== undefined) {
    body.thinking = { type: 'enabled', budget_tokens: req.thinkingBudget }
    // ★ 开启 thinking 时 max_tokens 必须 > budget_tokens,否则 400。
    // 这个约束不写在这里,就会以「高思考档位下必然报错」的形式出现在用户面前。
    if (req.maxOutputTokens <= req.thinkingBudget) {
      body.max_tokens = req.thinkingBudget + 4096
    }
    // 且 thinking 开启时不接受 temperature
  } else if (req.temperature !== undefined) {
    body.temperature = req.temperature
  }

  return {
    path: '/v1/messages',
    headers: {
      'content-type': 'application/json',
      // Anthropic 用 x-api-key,不是 Authorization: Bearer
      'x-api-key': apiKey,
      'anthropic-version': '2023-06-01'
    },
    // Identity metadata and cache policy are independent and applied together
    // only at the protocol boundary. Router applies this once more after model
    // request patches so neither invariant can be overridden there.
    body: applyAnthropicRequestOptions(body, { userId, cacheTtl })
  }
}
