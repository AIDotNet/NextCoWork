import { describe, expect, it } from 'vitest'
import { BlockAccumulator } from '../../block-accumulator'
import { decodeOpenAIChat } from '../decode/openai-chat'
import { decodeOpenAIResponses } from '../decode/openai-responses'
import { openAIErrorToAgentError } from '../decode/openai-common'
import { chunk, collect, events, functionItem, messageItem, reasoningContentItem, reasoningItem, responseDone } from './openai-fixtures'
import type { ProviderStreamEvent } from '../../../../shared/agent/stream'

function accumulated(output: ProviderStreamEvent[]): ReturnType<BlockAccumulator['finalize']> {
  const accumulator = new BlockAccumulator()
  output.forEach((e) => accumulator.apply(e))
  return accumulator.finalize()
}

describe('OpenAI Chat Completions decoding', () => {
  it('separates DeepSeek reasoning_content, content and trailing cache/reasoning usage', async () => {
    const output = await collect(decodeOpenAIChat(events(
      chunk({ role: 'assistant', content: null, reasoning_content: '' }),
      chunk({ reasoning_content: '让我' }), chunk({ reasoning_content: '思考。', content: '你好' }),
      chunk({ content: '！' }), chunk({}, 'stop'),
      { choices: [], usage: { prompt_tokens: 100, completion_tokens: 30, prompt_cache_hit_tokens: 60, prompt_cache_miss_tokens: 40,
        completion_tokens_details: { reasoning_tokens: 20 } } }, '[DONE]'
    )))
    expect(accumulated(output).parts).toEqual([
      // tokens: 夹具里 usage 报了 reasoning_tokens 20,这次回复只有一块思考,归给它(见 block-accumulator)
      { type: 'thinking', text: '让我思考。', opaque: { protocol: 'openai-chat', field: 'reasoning_content' }, tokens: 20 },
      { type: 'text', text: '你好！' }
    ])
    expect(output.at(-1)).toEqual({ type: 'message_end', stopReason: 'end_turn', usage: {
      inputTokens: 40, outputTokens: 30, cacheReadInputTokens: 60, reasoningTokens: 20
    } })
  })

  it('assembles interleaved parallel tool arguments without colliding with reasoning or text', async () => {
    const output = await collect(decodeOpenAIChat(events(
      chunk({ reasoning_content: '查两处。', content: '开始查询。', tool_calls: [
        { index: 0, id: 'a', type: 'function', function: { name: 'Echo', arguments: '{"text":' } },
        { index: 1, id: 'b', type: 'function', function: { name: 'Echo', arguments: '{"text":"二' } }
      ] }),
      chunk({ tool_calls: [
        { index: 1, function: { arguments: '"}' } }, { index: 0, function: { arguments: '"一"}' } }
      ] }), chunk({}, 'tool_calls'), '[DONE]'
    )))
    const result = accumulated(output)
    expect(result.parts.map((p) => p.type)).toEqual(['thinking', 'text', 'tool_call', 'tool_call'])
    expect(result.calls).toEqual([
      { ok: true, callId: 'a', name: 'Echo', input: { text: '一' } },
      { ok: true, callId: 'b', name: 'Echo', input: { text: '二' } }
    ])
    expect(output.at(-1)).toMatchObject({ stopReason: 'tool_use' })
  })

  it('buffers fragmented tool metadata and closes argument-free calls', async () => {
    const output = await collect(decodeOpenAIChat(events(
      chunk({ tool_calls: [{ index: 0, id: 'call', function: { name: 'Ec' } }] }),
      chunk({ tool_calls: [{ index: 0, id: '-1', function: { name: 'ho', arguments: '{}' } }] }),
      chunk({ tool_calls: [{ index: 1, id: 'call-2', function: { name: 'NoArgs' } }] }),
      chunk({}, 'tool_calls'), '[DONE]'
    )))
    expect(accumulated(output).calls).toEqual([
      { ok: true, callId: 'call-1', name: 'Echo', input: {} },
      { ok: true, callId: 'call-2', name: 'NoArgs', input: {} }
    ])
  })

  it('accepts tool identity and function metadata in separate chunks', async () => {
    const output = await collect(decodeOpenAIChat(events(
      chunk({ tool_calls: [{ index: 0, id: 'call-1', type: 'function' }] }),
      chunk({ tool_calls: [{ index: 0, function: { name: 'Echo', arguments: '{"text":"ok"}' } }] }),
      chunk({}, 'tool_calls'), '[DONE]'
    )))
    expect(accumulated(output).calls).toEqual([{ ok: true, callId: 'call-1', name: 'Echo', input: { text: 'ok' } }])
  })

  it('accepts a frozen tool call that omits tool_calls[].index', async () => {
    // ★ 逐字复刻 Google 的 OpenAI 兼容层实测分片:一个调用一次性给全,没有 index,
    //   还多带一个我们不认识的 extra_content。以前这一帧被判成 invalid tool call,
    //   症状是「用 Gemini 只要调工具就整轮中止、正文为空」。
    const output = await collect(decodeOpenAIChat(events(
      chunk({ role: 'assistant', content: '' }),
      chunk({ tool_calls: [{
        extra_content: { google: { thought_signature: 'sig' } },
        function: { arguments: '{"text":"一"}', name: 'Echo' },
        id: 'call_297140', type: 'function'
      }] }),
      chunk({}, 'tool_calls'), '[DONE]'
    )))
    expect(accumulated(output).calls).toEqual([
      { ok: true, callId: 'call_297140', name: 'Echo', input: { text: '一' } }
    ])
    expect(output.at(-1)).toMatchObject({ stopReason: 'tool_use' })
  })

  it('keeps index-less parallel tool calls in separate slots', async () => {
    const output = await collect(decodeOpenAIChat(events(
      chunk({ tool_calls: [{ id: 'call-a', function: { name: 'Echo', arguments: '{"text":"一"}' } }] }),
      chunk({ tool_calls: [{ id: 'call-b', function: { name: 'Echo', arguments: '{"text":"二"}' } }] }),
      chunk({}, 'tool_calls'), '[DONE]'
    )))
    expect(accumulated(output).calls).toEqual([
      { ok: true, callId: 'call-a', name: 'Echo', input: { text: '一' } },
      { ok: true, callId: 'call-b', name: 'Echo', input: { text: '二' } }
    ])
  })

  it('accepts a complete JSON response with message.tool_calls', async () => {
    const output = await collect(decodeOpenAIChat(events({ model: 'deepseek-test', choices: [{ index: 0, finish_reason: 'tool_calls', message: {
      reasoning_content: '先查一下。', content: null,
      tool_calls: [{ id: 'a', type: 'function', function: { name: 'Echo', arguments: '{"text":"hi"}' } }]
    } }], usage: { prompt_tokens: 12, completion_tokens: 9 } })))
    expect(accumulated(output).calls[0]).toMatchObject({ callId: 'a', input: { text: 'hi' } })
    expect(output.at(-1)).toMatchObject({ type: 'message_end', usage: { inputTokens: 12, outputTokens: 9 } })
  })

  it.each([[], [chunk({ content: 'partial' })], [chunk({ content: 'partial' }), '[DONE]']])('does not turn a truncated stream into success: %j', async (...frames) => {
    const output = await collect(decodeOpenAIChat(events(...frames)))
    expect(output.at(-1)).toMatchObject({ type: 'error', error: { code: 'network' } })
    expect(output.some((e) => e.type === 'message_end')).toBe(false)
  })

  it('does not close partial tool calls on length or network termination', async () => {
    for (const finish of ['length', undefined]) {
      const frames = [chunk({ tool_calls: [{ index: 0, id: 'a', function: { name: 'Echo', arguments: '{"text":' } }] })]
      if (finish !== undefined) frames.push(chunk({}, finish))
      const output = await collect(decodeOpenAIChat(events(...frames)))
      expect(accumulated(output).calls).toEqual([])
      expect(output.at(-1)?.type).toBe(finish === 'length' ? 'message_end' : 'error')
    }
  })

  it('keeps malformed final JSON as a tool error for the agent to report', async () => {
    const output = await collect(decodeOpenAIChat(events(chunk({ tool_calls: [
      { index: 0, id: 'a', function: { name: 'Echo', arguments: '{bad' } }
    ] }, 'tool_calls'), '[DONE]')))
    expect(accumulated(output).calls[0]).toMatchObject({ ok: false, raw: '{bad' })
  })

  it('rejects malformed SSE, missing IDs and duplicate IDs', async () => {
    for (const frames of [
      [chunk({ content: 'partial' }), '{bad', chunk({}, 'stop')],
      [chunk({ tool_calls: [{ index: 0, function: { name: 'Echo', arguments: '{}' } }] }, 'tool_calls')],
      [chunk({ tool_calls: [0, 1].map((index) => ({ index, id: 'same', function: { name: 'Echo', arguments: '{}' } })) }, 'tool_calls')]
    ]) {
      const output = await collect(decodeOpenAIChat(events(...frames)))
      expect(output.at(-1)).toMatchObject({ type: 'error', error: { code: 'provider' } })
      expect(accumulated(output).calls).toEqual([])
    }
  })

  it('ignores extra choices and handles refusal', async () => {
    const output = await collect(decodeOpenAIChat(events({ model: 'm', choices: [
      { index: 1, delta: { content: 'not selected' }, finish_reason: 'stop' },
      { index: 0, delta: { refusal: 'Cannot help.' }, finish_reason: 'stop' }
    ] }, '[DONE]')))
    expect(accumulated(output).parts).toEqual([{ type: 'text', text: 'Cannot help.' }])
    expect(output.at(-1)).toMatchObject({ stopReason: 'refusal' })
  })

  it('drops stray content chunks sent after finish_reason instead of erroring', async () => {
    const output = await collect(decodeOpenAIChat(events(
      chunk({ content: '你好' }), chunk({}, 'stop'), chunk({ content: '多余的' }), '[DONE]'
    )))
    expect(accumulated(output).parts).toEqual([{ type: 'text', text: '你好' }])
    expect(output.at(-1)).toMatchObject({ type: 'message_end', stopReason: 'end_turn' })
  })

  it('classifies in-stream provider errors', async () => {
    const output = await collect(decodeOpenAIChat(events({ error: { code: 'rate_limit_exceeded', message: 'slow down' } })))
    expect(output).toEqual([{ type: 'error', error: { code: 'rate_limit', message: 'slow down', retryable: true } }])
  })
})

describe('OpenAI Responses decoding', () => {
  it('decodes summaries, encrypted reasoning and function arguments without repeating done snapshots', async () => {
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.created', response: { id: 'resp-1', model: 'gpt-test', status: 'in_progress' } },
      { type: 'response.output_item.added', output_index: 0, item: { type: 'reasoning', id: 'rs-1', summary: [] } },
      { type: 'response.reasoning_summary_text.delta', output_index: 0, summary_index: 0, delta: '检查' },
      { type: 'response.reasoning_summary_text.delta', output_index: 0, summary_index: 0, delta: '参数。' },
      { type: 'response.output_item.done', output_index: 0, item: reasoningItem },
      { type: 'response.output_item.added', output_index: 1, item: { ...functionItem, arguments: '' } },
      { type: 'response.function_call_arguments.delta', output_index: 1, delta: '{"text":' },
      { type: 'response.function_call_arguments.delta', output_index: 1, delta: '"hello"}' },
      { type: 'response.function_call_arguments.done', output_index: 1, arguments: functionItem.arguments },
      { type: 'response.output_item.done', output_index: 1, item: functionItem },
      responseDone([reasoningItem, functionItem])
    )))
    expect(accumulated(output).parts).toEqual([
      { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-responses', item: reasoningItem }, tokens: 20 },
      { type: 'tool_call', callId: 'call-1', name: 'Echo', input: { text: 'hello' } }
    ])
    expect(output.filter((e) => e.type === 'tool_call_start')).toHaveLength(1)
    expect(output.filter((e) => e.type === 'tool_call_end')).toHaveLength(1)
    expect(output.at(-1)).toMatchObject({ type: 'message_end', stopReason: 'tool_use', usage: {
      inputTokens: 40, cacheReadInputTokens: 60, outputTokens: 30, reasoningTokens: 20
    } })
  })

  it('supports completed-only and JSON responses', async () => {
    const terminal = responseDone([reasoningItem, messageItem]) as { response: unknown }
    for (const frame of [terminal, terminal.response]) {
      const output = await collect(decodeOpenAIResponses(events(frame)))
      expect(accumulated(output).parts.map((p) => p.type)).toEqual(['thinking', 'text'])
      expect(output.at(-1)).toMatchObject({ type: 'message_end', stopReason: 'end_turn' })
    }
  })

  it('retains encrypted-only reasoning and multiple text parts', async () => {
    const output = await collect(decodeOpenAIResponses(events(responseDone([
      { ...reasoningItem, summary: [] },
      { ...messageItem, content: [{ type: 'output_text', text: 'A' }, { type: 'output_text', text: 'B' }] }
    ]))))
    expect(accumulated(output).parts).toMatchObject([
      { type: 'thinking', text: '', opaque: { item: { encrypted_content: 'encrypted-reasoning' } } },
      { type: 'text', text: 'A' }, { type: 'text', text: 'B' }
    ])
  })

  it('does not execute calls from failed, truncated or token-limited responses', async () => {
    for (const tail of [[], [responseDone([functionItem], 'incomplete', 'max_output_tokens')], [
      { type: 'response.failed', response: { error: { code: 'server_error', message: 'failed' } } }
    ]]) {
      const output = await collect(decodeOpenAIResponses(events(
        { type: 'response.output_item.added', output_index: 0, item: functionItem }, ...tail
      )))
      expect(accumulated(output).calls).toEqual([])
      expect(output.at(-1)?.type).toBe(tail[0] !== undefined && (tail[0] as { type: string }).type === 'response.incomplete' ? 'message_end' : 'error')
    }
  })

  it('rejects changed argument snapshots and errors without an initial event', async () => {
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: functionItem },
      responseDone([{ ...functionItem, arguments: '{"different":true}' }])
    )))
    expect(output.at(-1)).toMatchObject({ type: 'error', error: { code: 'provider' } })
    expect(accumulated(output).calls).toEqual([])
    expect((await collect(decodeOpenAIResponses(events()))).at(-1)).toMatchObject({ type: 'error' })
  })

  // 网关把参数 parse 完重新 stringify、或干脆不给终局快照 —— 入参一个字没变,
  // 为它中止整轮是我们自己的问题
  it.each(['{ "text": "hello" }', ''])('accepts a re-serialized or absent argument snapshot (%s)', async (snapshot) => {
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { ...functionItem, arguments: '' } },
      { type: 'response.function_call_arguments.delta', output_index: 0, item_id: 'fc-1', delta: functionItem.arguments },
      responseDone([{ ...functionItem, arguments: snapshot }])
    )))
    expect(output.some((e) => e.type === 'error')).toBe(false)
    expect(accumulated(output).calls).toEqual([{ ok: true, callId: 'call-1', name: 'Echo', input: { text: 'hello' } }])
  })

  // 终局 output 重排顺序并重新生成 item id:按下标并槽会把 B 的参数追到 A 上
  it('keeps parallel calls apart when the terminal output is reordered with new item ids', async () => {
    const second = { type: 'function_call', id: 'fc-2', call_id: 'call-2', name: 'Echo', arguments: '{"text":"world"}' }
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { ...functionItem, arguments: '' } },
      { type: 'response.output_item.added', output_index: 1, item: { ...second, arguments: '' } },
      { type: 'response.function_call_arguments.done', output_index: 0, item_id: 'fc-1', arguments: functionItem.arguments },
      { type: 'response.function_call_arguments.done', output_index: 1, item_id: 'fc-2', arguments: second.arguments },
      responseDone([{ ...second, id: 'rewritten-2' }, { ...functionItem, id: 'rewritten-1' }])
    )))
    expect(output.some((e) => e.type === 'error')).toBe(false)
    expect(accumulated(output).calls).toEqual([
      { ok: true, callId: 'call-1', name: 'Echo', input: { text: 'hello' } },
      { ok: true, callId: 'call-2', name: 'Echo', input: { text: 'world' } }
    ])
  })

  // 规范的 incomplete_details.reason 枚举有四个值,以前只放行两个 ——
  // max_messages 和 steered 被当成协议违规中止整轮,而 steered 是正常终止
  it.each([
    ['max_output_tokens', 'max_tokens'], ['content_filter', 'refusal'],
    ['max_messages', 'end_turn'], ['steered', 'end_turn'], [undefined, 'end_turn']
  ] as const)('treats incomplete reason %s as %s instead of a protocol violation', async (reason, stopReason) => {
    const output = await collect(decodeOpenAIResponses(events(responseDone([messageItem], 'incomplete', reason))))
    expect(output.some((e) => e.type === 'error')).toBe(false)
    expect(output.at(-1)).toMatchObject({ type: 'message_end', stopReason })
  })

  it('decodes reasoning text and prefers it over the summary', async () => {
    const snapshot = await collect(decodeOpenAIResponses(events(responseDone([reasoningContentItem]))))
    expect(accumulated(snapshot).parts).toEqual([
      { type: 'thinking', text: '先看参数是否齐全。', opaque: { protocol: 'openai-responses', item: reasoningContentItem }, tokens: 20 }
    ])

    const streamed = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { type: 'reasoning', id: 'rs-1', summary: [] } },
      { type: 'response.reasoning_text.delta', output_index: 0, content_index: 0, delta: '先看参数' },
      { type: 'response.reasoning_text.delta', output_index: 0, content_index: 0, delta: '是否齐全。' },
      { type: 'response.reasoning_text.done', output_index: 0, content_index: 0, text: '先看参数是否齐全。' },
      { type: 'response.output_item.done', output_index: 0, item: reasoningContentItem },
      responseDone([reasoningContentItem])
    )))
    expect(accumulated(streamed).parts).toEqual([
      { type: 'thinking', text: '先看参数是否齐全。', opaque: { protocol: 'openai-responses', item: reasoningContentItem }, tokens: 20 }
    ])
  })

  it('rejects client-executed tool items but ignores hosted ones', async () => {
    const shell = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { type: 'local_shell_call', id: 'ls-1' } }
    )))
    expect(shell.at(-1)).toMatchObject({ type: 'error', error: {
      code: 'provider', messageParams: { detail: 'unsupported output item: local_shell_call' }
    } })

    const searchCall = { type: 'web_search_call', id: 'ws-1', status: 'completed' }
    const hosted = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: searchCall },
      { type: 'response.output_item.done', output_index: 1, item: messageItem },
      responseDone([searchCall, messageItem])
    )))
    expect(accumulated(hosted).parts).toEqual([{ type: 'text', text: '完成' }])
    expect(hosted.at(-1)).toMatchObject({ type: 'message_end', stopReason: 'end_turn' })
  })

  it('reconciles the terminal output by item id rather than array position', async () => {
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { type: 'reasoning', id: 'rs-1', summary: [] } },
      { type: 'response.output_item.done', output_index: 0, item: reasoningItem },
      { type: 'response.output_item.added', output_index: 1, item: { ...messageItem, content: [] } },
      { type: 'response.output_text.delta', output_index: 1, content_index: 0, delta: '完成' },
      // ★ 终局数组里没有 reasoning:下标 0 现在是 message,按位置对齐就会撞出「类型变了」
      responseDone([messageItem])
    )))
    expect(output.some((e) => e.type === 'error')).toBe(false)
    expect(accumulated(output).parts).toEqual([
      { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-responses', item: reasoningItem }, tokens: 20 },
      { type: 'text', text: '完成' }
    ])
  })

  it('tolerates an id the gateway regenerated in the terminal output', async () => {
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { type: 'reasoning', id: 'rs-1', summary: [] } },
      { type: 'response.reasoning_summary_text.delta', output_index: 0, summary_index: 0, delta: '检查参数。' },
      { type: 'response.output_item.done', output_index: 0, item: reasoningItem },
      // ★ 网关在终局给同一条 reasoning 换了个 id —— 以前这里抛 output item identity changed
      responseDone([{ ...reasoningItem, id: 'rs-regenerated' }, messageItem])
    )))
    expect(output.some((e) => e.type === 'error')).toBe(false)
    expect(accumulated(output).parts).toEqual([
      { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-responses', item: reasoningItem }, tokens: 20 },
      { type: 'text', text: '完成' }
    ])
  })

  it('opens a new slot when a gateway reuses output_index for another item type', async () => {
    const output = await collect(decodeOpenAIResponses(events(
      { type: 'response.output_item.added', output_index: 0, item: { type: 'reasoning', id: 'rs-1', summary: [] } },
      { type: 'response.reasoning_summary_text.delta', output_index: 0, summary_index: 0, delta: '检查参数。' },
      // ★ 同一个 output_index 又来一条 message —— 以前抛 output item type changed
      { type: 'response.output_item.added', output_index: 0, item: { ...messageItem, content: [] } },
      { type: 'response.output_text.delta', output_index: 0, item_id: 'msg-1', content_index: 0, delta: '完成' },
      responseDone([reasoningItem, messageItem])
    )))
    expect(output.some((e) => e.type === 'error')).toBe(false)
    expect(accumulated(output).parts).toEqual([
      { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-responses', item: reasoningItem }, tokens: 20 },
      { type: 'text', text: '完成' }
    ])
  })

  it('keeps streamed text when a done snapshot disagrees or omits the field', async () => {
    for (const done of [
      { type: 'response.output_text.done', output_index: 0, content_index: 0, text: '完全不同的收尾' },
      { type: 'response.output_text.done', output_index: 0, content_index: 0 }
    ]) {
      const output = await collect(decodeOpenAIResponses(events(
        { type: 'response.output_item.added', output_index: 0, item: { ...messageItem, content: [] } },
        { type: 'response.output_text.delta', output_index: 0, content_index: 0, delta: '已收到' },
        done, responseDone([messageItem])
      )))
      expect(output.some((e) => e.type === 'error')).toBe(false)
      expect(accumulated(output).parts).toEqual([{ type: 'text', text: '已收到' }])
    }
  })
})

describe('OpenAI error classification', () => {
  it.each([
    [401, 'invalid_api_key', 'auth', false], [403, 'forbidden', 'auth', false],
    [429, 'rate_limit_exceeded', 'rate_limit', true], [429, 'insufficient_quota', 'provider', false],
    [400, 'context_length_exceeded', 'context_length', false], [422, 'invalid_request_error', 'provider', false],
    [503, 'server_error', 'provider', true]
  ] as const)('maps HTTP %s / %s', (status, code, expected, retryable) => {
    expect(openAIErrorToAgentError(status, { error: { code, message: 'upstream detail' } }))
      .toEqual({ code: expected, message: 'upstream detail', retryable, status })
  })

  // 认不出形状时以前只说「Upstream request failed (HTTP 400)」—— 上游抱怨什么一个字都不说
  it.each([
    [{ detail: 'Model not supported' }, 'Model not supported'],
    [{ detail: { message: 'nested detail' } }, 'nested detail'],
    [{ message: 'bare message' }, 'bare message'],
    [{ error: 'plain error string' }, 'plain error string'],
    [{ unexpected: 'shape', nested: { n: 1 } }, '{"unexpected":"shape","nested":{"n":1}}'],
    ['<html>gateway</html>', '<html>gateway</html>']
  ])('extracts a usable message from %j', (body, message) => {
    expect(openAIErrorToAgentError(400, body).message).toBe(message)
  })

  it('falls back to the generic message only when there is no body at all', () => {
    expect(openAIErrorToAgentError(400, '').message).toBe('Upstream request failed (HTTP 400)')
    expect(openAIErrorToAgentError(400, {}).message).toBe('Upstream request failed (HTTP 400)')
  })
})
