import type { ProviderStreamEvent, StopReason } from '../../../../shared/agent/stream'
import type { SseEvent } from '../sse'
import { count, interruptedResponse, malformedResponse, OpenAIUsage, openAIErrorToAgentError, record, string } from './openai-common'

interface Call {
  index: number
  id: string
  name: string
  args: string
  started: boolean
}

/**
 * 上游这一条 `tool_calls` 该并进哪一格。**只在它没给 `index` 时用。**
 *
 * ★★ 三步,顺序不能换 —— 每一步都对应一种真实存在的上游形状:
 *
 * 1. **同 id 的调用已经存在** → 并进去。`id` 是上游自己给的标识,比位置权威;
 *    它还在分片累积(`started === false`)时可能只对上前缀,所以那时按前缀认。
 * 2. **position 那一格还可用** → 用它。空着是常态;里面那个调用**还没 started**
 *    也算可用 —— 那说明它的 id/name 正在累积,这一条就是它的下一片。
 * 3. 否则**另开一格**。省略 index 的上游把并行调用一条一条发,第二条的 position
 *    同样是 0 —— 并进去会把两个调用串成一个:参数被拼成非法 JSON,而且回传时
 *    少一个 tool_use id,下一轮直接 400。
 */
function slotForToolCall(calls: Map<number, Call>, id: string, position: number): number {
  if (id !== '') {
    for (const [key, call] of calls) {
      if (call.id !== '' && (call.id === id || (!call.started && id.startsWith(call.id)))) return key
    }
  }
  const held = calls.get(position)
  if (held === undefined || !held.started || held.id === '' || held.id === id) return position
  // 走到这里 calls 至少有一格(held 就在里面),所以 Math.max 收不到空集合。
  return Math.max(...calls.keys()) + 1
}

/** Chat Completions flattens text/reasoning and independently indexes tool calls. */
export async function* decodeOpenAIChat(events: AsyncIterable<SseEvent>): AsyncGenerator<ProviderStreamEvent> {
  const usage = new OpenAIUsage()
  const calls = new Map<number, Call>()
  let nextIndex = 0
  let textIndex: number | undefined
  let thinkingIndex: number | undefined
  let started = false
  let finish: string | undefined
  let refused = false

  function* startCall(call: Call): Generator<ProviderStreamEvent> {
    if (call.started || call.id === '' || call.name === '') return
    call.started = true
    yield { type: 'tool_call_start', index: call.index, callId: call.id, name: call.name }
    if (call.args !== '') yield { type: 'tool_call_delta', index: call.index, callId: call.id, argsDelta: call.args }
    call.args = ''
  }

  for await (const event of events) {
    if (event.data.trim() === '[DONE]') break
    if (event.data.trim() === '') continue
    let data: Record<string, unknown> | undefined
    try { data = record(JSON.parse(event.data)) } catch {
      yield { type: 'error', error: malformedResponse('invalid Chat Completions JSON') }
      return
    }
    if (data === undefined) continue
    if (data.error !== undefined || event.event === 'error') {
      yield { type: 'error', error: openAIErrorToAgentError(undefined, data) }
      return
    }
    usage.update(data.usage)
    if (!Array.isArray(data.choices)) continue
    const choice = data.choices.map(record).find((c, i) => c?.index === 0 || (i === 0 && c?.index === undefined))
    if (choice === undefined) continue // usage-only chunk, or another choice
    if (!started) {
      started = true
      yield { type: 'message_start', model: string(data.model) ?? '<unknown>' }
    }
    const delta = record(choice.delta) ?? record(choice.message)
    // Some relays keep streaming stray content after the finish_reason chunk (buffering races
    // in their multiplexer); drop it rather than aborting an otherwise-complete response.
    if (finish !== undefined && delta !== undefined && Object.keys(delta).length > 0) continue
    if (delta !== undefined) {
      const thinking = string(delta.reasoning_content)
      if (thinking !== undefined) {
        if (thinkingIndex === undefined) {
          thinkingIndex = nextIndex++
          yield { type: 'block_opaque', index: thinkingIndex, opaque: { protocol: 'openai-chat', field: 'reasoning_content' } }
        }
        if (thinking !== '') yield { type: 'thinking_delta', index: thinkingIndex, text: thinking }
      }
      const refusal = string(delta.refusal)
      if (refusal) refused = true
      const text = string(delta.content) ?? refusal
      if (text !== undefined && text !== '') {
        textIndex ??= nextIndex++
        yield { type: 'text_delta', index: textIndex, text }
      }
      if (Array.isArray(delta.tool_calls)) {
        for (const [position, raw] of delta.tool_calls.entries()) {
          const tool = record(raw)
          const fn = record(tool?.function)
          const id = string(tool?.id) ?? ''
          const name = string(fn?.name) ?? ''
          /*
            需求:上游**合法地**省略 `tool_calls[].index` 时,这一帧也要能拼出调用。
            OpenAI 规范里 `index` 只对**流式片段**是必需的;而 Google 的 OpenAI
            兼容层把每个调用一次性给全(id/name/arguments 同帧到齐),一个 index
            都不带 —— 它没写错,是我们以前只认「片段」那一种形状。

            不满足会怎样:第一次让 Gemini 调工具就得到
            「上游返回的响应格式不完整或不符合协议…(invalid tool call)」,
            正文一个字都没有,且 retryable:false,重试无用。
            实测分片形状与复现见
            `__tests__/decode-openai.test.ts` 的
            'accepts a frozen tool call that omits tool_calls[].index'。
          */
          const upstreamIndex = count(tool?.index) ?? slotForToolCall(calls, id, position)
          if (tool === undefined || (tool.function !== undefined && fn === undefined)
            || (tool.type !== undefined && tool.type !== 'function')) {
            yield { type: 'error', error: malformedResponse('invalid tool call') }
            return
          }
          let call = calls.get(upstreamIndex)
          if (call === undefined) {
            call = { index: nextIndex++, id: '', name: '', args: '', started: false }
            calls.set(upstreamIndex, call)
          }
          if (call.started && ((id !== '' && id !== call.id) || (name !== '' && name !== call.name))) {
            yield { type: 'error', error: malformedResponse('tool identity changed during streaming') }
            return
          }
          if (!call.started) {
            if (id !== call.id) call.id += id
            if (name !== call.name) call.name += name
          }
          const args = string(fn?.arguments) ?? ''
          if (call.started) {
            if (args !== '') yield { type: 'tool_call_delta', index: call.index, callId: call.id, argsDelta: args }
          } else {
            call.args += args
            if (args !== '') yield* startCall(call)
          }
        }
      }
    }
    finish = string(choice.finish_reason) ?? finish
  }

  // EOF without a finish_reason is never success, even if a relay sent [DONE].
  if (!started || finish === undefined) {
    yield { type: 'error', error: interruptedResponse() }
    return
  }
  const stopReason: StopReason = finish === 'length' ? 'max_tokens'
    : finish === 'content_filter' || refused ? 'refusal'
      : finish === 'tool_calls' || (finish === 'stop' && calls.size > 0) ? 'tool_use' : 'end_turn'
  if (stopReason === 'tool_use') {
    if (calls.size === 0 || [...calls.values()].some((call) => call.id === '' || call.name === '')
      || new Set([...calls.values()].map((call) => call.id)).size !== calls.size) {
      yield { type: 'error', error: malformedResponse('missing or duplicate tool call identity') }
      return
    }
    for (const call of calls.values()) {
      yield* startCall(call)
      yield { type: 'tool_call_end', index: call.index, callId: call.id }
    }
  }
  yield { type: 'message_end', stopReason, usage: usage.snapshot() }
}
