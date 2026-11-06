import { describe, expect, it } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../shared/agent/message'
import type { ReasoningReplay } from '../../../../shared/domain/provider'
import { encodeUpstream } from '../codec'
import { encodeOpenAIChat, toOpenAIChatMessages } from '../encode/openai-chat'
import { encodeOpenAIResponses } from '../encode/openai-responses'
import { REQUEST, reasoningItem } from './openai-fixtures'

/** DeepSeek 的回传方言:目录条目显式声明 text-required,这里对齐那条事实。 */
const REPLAY_DEEPSEEK = { userId: 'ws-test', cacheTtl: '5m' as const, reasoningReplay: 'text-required' as const }

const history = [
  ...REQUEST.messages,
  assistantMessage('a', [
    { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-chat', field: 'reasoning_content' } },
    { type: 'text', text: '查询中' },
    { type: 'tool_call', callId: 'call-1', name: 'Echo', input: { text: 'hello' } },
    { type: 'tool_call', callId: 'call-2', name: 'Echo', input: {} }
  ], 0),
  toolResultMessage('t', [
    { type: 'tool_result', callId: 'call-1', isError: false, output: { content: 'hello' } },
    { type: 'tool_result', callId: 'call-2', isError: true, output: { content: 'denied' } }
  ], 0)
]

describe('OpenAI request encoders', () => {
  it.each(['openai-chat', 'openai-responses'] as const)('%s ignores mandatory Anthropic cache options', (protocol) => {
    for (const cacheTtl of ['5m', '1h'] as const) {
      const body = encodeUpstream(protocol, REQUEST, 'gpt-test', 'k', { userId: 'ws-test', cacheTtl }).body
      expect(body).not.toHaveProperty('cache_control')
      expect(body).not.toHaveProperty('metadata')
      expect(JSON.stringify(body)).not.toContain('cache_control')
    }
  })

  it('encodes the complete Chat tool conversation, preserving reasoning across later user turns', () => {
    const before = structuredClone(history)
    const encoded = encodeOpenAIChat({ ...REQUEST, messages: [...history, userMessage('u2', [{ type: 'text', text: '继续' }], 0)] }, 'deepseek-reasoner', 'test-key')
    expect(encoded.path).toBe('/chat/completions')
    expect(encoded.headers).toEqual({ 'content-type': 'application/json', authorization: 'Bearer test-key' })
    expect(encoded.body).toMatchObject({ model: 'deepseek-reasoner', stream: true, stream_options: { include_usage: true }, max_tokens: 8192,
      tools: [{ type: 'function', function: { name: 'Echo', parameters: REQUEST.tools[0]?.inputSchema } }],
      messages: [
        { role: 'system', content: REQUEST.system }, { role: 'user', content: '你好' },
        { role: 'assistant', content: '查询中', reasoning_content: '检查参数。', tool_calls: [
          { id: 'call-1', type: 'function', function: { name: 'Echo', arguments: '{"text":"hello"}' } },
          { id: 'call-2', type: 'function', function: { name: 'Echo', arguments: '{}' } }
        ] },
        { role: 'tool', tool_call_id: 'call-1', content: 'hello' },
        { role: 'tool', tool_call_id: 'call-2', content: 'denied' }, { role: 'user', content: '继续' }
      ]
    })
    expect(history).toEqual(before)
  })

  it('encodes browser screenshots as model-visible image inputs after tool receipts', () => {
    const messages = [
      assistantMessage('shot-a', [{ type: 'tool_call', callId: 'shot-1', name: 'browser_screenshot', input: {} }], 0),
      toolResultMessage('shot-r', [{
        type: 'tool_result',
        callId: 'shot-1',
        isError: false,
        output: {
          content: '截图',
          images: [{ mime: 'image/png', dataRef: 'data:image/png;base64,AQID' }]
        }
      }], 0)
    ]

    expect(toOpenAIChatMessages(messages)).toMatchObject([
      { role: 'assistant' },
      { role: 'tool', tool_call_id: 'shot-1', content: '截图' },
      { role: 'user', content: [{ type: 'image_url', image_url: { url: 'data:image/png;base64,AQID' } }] }
    ])
    const responses = encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }
    expect(responses.input).toContainEqual({
      type: 'function_call_output',
      call_id: 'shot-1',
      output: [
        { type: 'input_text', text: '截图' },
        { type: 'input_image', image_url: 'data:image/png;base64,AQID', detail: 'auto' }
      ]
    })
  })

  it.each(['gpt-5', 'gpt-5.4', 'openai/gpt-5', 'o3', 'o4-mini'])('uses max_completion_tokens for %s', (model) => {
    const body = encodeOpenAIChat(REQUEST, model, 'k').body
    expect(body).toHaveProperty('max_completion_tokens', 8192)
    expect(body).not.toHaveProperty('max_tokens')
  })

  it('omits UI-only and foreign thinking blocks and preserves empty reasoning with tool calls', () => {
    const messages = [assistantMessage('a', [
      { type: 'thinking', text: 'private', opaque: { signature: 'sig' } },
      { type: 'thinking', text: '', opaque: { protocol: 'openai-chat', field: 'reasoning_content' } },
      { type: 'error', error: { code: 'provider', message: 'UI only', retryable: false } },
      { type: 'tool_call', callId: 'c', name: 'Echo', input: {} }
    ], 0)]
    expect(encodeOpenAIChat({ ...REQUEST, system: '', messages }, 'glm-test', 'k').body).toMatchObject({ messages: [
      { role: 'assistant', content: null, reasoning_content: '' }
    ] })
    expect(JSON.stringify(encodeOpenAIChat({ ...REQUEST, messages }, 'glm-test', 'k').body)).not.toContain('private')
  })

  it('encodes Responses stateless reasoning and tool receipts using call_id, not item id', () => {
    const messages = structuredClone(history)
    messages[1]!.parts[0] = { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-responses', item: reasoningItem } }
    const encoded = encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k')
    expect(encoded.path).toBe('/responses')
    expect(encoded.body).toMatchObject({ model: 'gpt-test', instructions: REQUEST.system,
      stream: true, store: false, include: ['reasoning.encrypted_content'], max_output_tokens: 8192,
      tools: [{ type: 'function', name: 'Echo', strict: false }]
    })
    const input = (encoded.body as { input: unknown[] }).input
    expect(input[1]).toEqual(reasoningItem)
    expect(input).toContainEqual({ type: 'function_call', call_id: 'call-1', name: 'Echo', arguments: '{"text":"hello"}' })
    expect(input).toContainEqual({ type: 'function_call_output', call_id: 'call-1', output: 'hello' })
    expect(JSON.stringify(encoded.body)).not.toContain('reasoning_content')
  })

  it('strips output-only fields from echoed reasoning items', () => {
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '检查参数。', opaque: {
      protocol: 'openai-responses',
      item: { ...reasoningItem, status: 'completed', object: 'reasoning' }
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input
    expect(input[0]).toEqual(reasoningItem)
    expect(JSON.stringify(input)).not.toContain('status')
  })

  it.each([65, 428])('omits reasoning with a %i-character id without changing the transcript or tool receipts', (length) => {
    const item = { ...reasoningItem, id: 'r'.repeat(length) }
    const messages = structuredClone(history)
    messages[1]!.parts.unshift({ type: 'thinking', text: '', opaque: { protocol: 'openai-responses', item } })
    messages[1]!.parts[1] = { type: 'thinking', text: '检查参数。', opaque: { protocol: 'openai-responses', item: reasoningItem } }
    const before = structuredClone(messages)
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input

    expect(input).toEqual([
      { role: 'user', content: [{ type: 'input_text', text: '你好' }] },
      reasoningItem,
      { role: 'assistant', content: '查询中' },
      { type: 'function_call', call_id: 'call-1', name: 'Echo', arguments: '{"text":"hello"}' },
      { type: 'function_call', call_id: 'call-2', name: 'Echo', arguments: '{}' },
      { type: 'function_call_output', call_id: 'call-1', output: 'hello' },
      { type: 'function_call_output', call_id: 'call-2', output: 'denied' }
    ])
    expect(messages).toEqual(before)
  })

  it.each(['opaque-only', 'text-required'] as const)('preserves a reasoning id at the 64-character limit for %s', (reasoningReplay) => {
    const item = { ...reasoningItem, id: `rs_${'a'.repeat(61)}` }
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '检查参数。', opaque: {
      protocol: 'openai-responses', item
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k', {
      ...REPLAY_DEEPSEEK, reasoningReplay
    }).body as { input: unknown[] }).input
    expect(input).toEqual([item])
  })

  it.each([true, false])('removes an oversized id while preserving text-required reasoning (has content: %s)', (hasContent) => {
    const content = [{ type: 'reasoning_text', text: '原始推理正文' }]
    const item = { type: 'reasoning', id: 'r'.repeat(428), ...(hasContent ? { content, encrypted_content: 'enc' } : {}) }
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '转录里的完整推理', opaque: {
      protocol: 'openai-responses', item
    } }], 0)]
    const before = structuredClone(messages)
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'deepseek-flash', 'k', REPLAY_DEEPSEEK).body as { input: unknown[] }).input

    expect(input).toEqual([hasContent
      ? { type: 'reasoning', content, encrypted_content: 'enc' }
      : { type: 'reasoning', content: [{ type: 'reasoning_text', text: '转录里的完整推理' }] }
    ])
    expect(messages).toEqual(before)
  })

  /*
    ★ DeepSeek 思考模式的硬校验:带 tools 时历史每一轮的 reasoning_text 必须回传,
    缺失即 400。opaque 缺席 / 协议键对不上时,思考全文只存在于 part.text ——
    这组用例钉住「正文必须回传」和「有载体的 item 不许被改写」两条边界。
    ★★ 这两条边界只对 `text-required` 成立:默认那支是官方 OpenAI,它恰好**禁止**
    正文(见下面那组用例)。所以这里显式传方言,不依赖默认值。
  */
  it('replays thinking text as a reasoning item when opaque is missing or from another protocol', () => {
    const messages = [assistantMessage('a', [
      { type: 'thinking', text: '先想清楚', opaque: { protocol: 'openai-chat', field: 'reasoning_content' } },
      { type: 'thinking', text: '再动手', opaque: { signature: 'anthropic-sig' } },
      { type: 'thinking', text: '断流那次没留下 opaque' },
      { type: 'tool_call', callId: 'c', name: 'Echo', input: {} }
    ], 0)]
    const before = structuredClone(messages)
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'deepseek-flash', 'k', REPLAY_DEEPSEEK).body as { input: unknown[] }).input
    expect(input).toContainEqual({ type: 'reasoning', content: [{ type: 'reasoning_text', text: '先想清楚' }] })
    expect(input).toContainEqual({ type: 'reasoning', content: [{ type: 'reasoning_text', text: '再动手' }] })
    expect(input).toContainEqual({ type: 'reasoning', content: [{ type: 'reasoning_text', text: '断流那次没留下 opaque' }] })
    expect(input).toContainEqual({ type: 'function_call', call_id: 'c', name: 'Echo', arguments: '{}' })
    expect(messages).toEqual(before)
  })

  it('fills content from the transcript when the replayed item lost its text', () => {
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '全文还在', opaque: {
      protocol: 'openai-responses', item: { type: 'reasoning', id: 'rs-9' }
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'deepseek-flash', 'k', REPLAY_DEEPSEEK).body as { input: unknown[] }).input
    expect(input[0]).toEqual({ type: 'reasoning', id: 'rs-9', content: [{ type: 'reasoning_text', text: '全文还在' }] })
  })

  /*
    ★★★ 官方 OpenAI 的输入侧约束:reasoning item 的 `content` 上限是 **0**,带非空
    正文整轮 400(`Invalid 'input[N].content': array too long ... array_above_max_length`)。
    出事的 item 会留在转录里被每轮重放 —— 只要有一个,这个会话之后每轮都发不出去。
  */
  it('官方 GPT 默认剥掉回传物里的推理正文,只留上游签发的载体', () => {
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '转录里的正文', opaque: {
      protocol: 'openai-responses',
      item: { type: 'reasoning', id: 'rs-1', summary: [{ type: 'summary_text', text: '摘要' }],
        content: [{ type: 'reasoning_text', text: '先看参数是否齐全。' }], encrypted_content: 'enc' }
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input
    expect(input[0]).toEqual({
      type: 'reasoning', id: 'rs-1', summary: [{ type: 'summary_text', text: '摘要' }], encrypted_content: 'enc'
    })
    // 上游给的正文与转录里的正文都不许上行:合成出来的 item 一样带正文、一样 400
    expect(JSON.stringify(input)).not.toContain('先看参数是否齐全。')
    expect(JSON.stringify(input)).not.toContain('转录里的正文')
  })

  it('官方 GPT 下没有 opaque 的思考块一个字节都不上行(原先会合成一个带正文的 item)', () => {
    const messages = [assistantMessage('a', [
      { type: 'thinking', text: '断流那次没留下 opaque', opaque: { protocol: 'openai-chat', field: 'reasoning_content' } },
      { type: 'tool_call', callId: 'c', name: 'Echo', input: {} }
    ], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input
    expect(input).toEqual([{ type: 'function_call', call_id: 'c', name: 'Echo', arguments: '{}' }])
  })

  it('官方 GPT 下没有 id 的载体不上行(输入侧 id 必填,发了就是每轮 400)', () => {
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '正文', opaque: {
      protocol: 'openai-responses', item: { type: 'reasoning' }
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input
    expect(input).toEqual([])
  })

  it('text-required(DeepSeek)下带正文的载体一个字节不改', () => {
    const item = { type: 'reasoning', id: 'rs-1', content: [{ type: 'reasoning_text', text: '先看参数。' }] }
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '先看参数。', opaque: {
      protocol: 'openai-responses', item
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'deepseek-flash', 'k', REPLAY_DEEPSEEK).body as { input: unknown[] }).input
    expect(input[0]).toEqual(item)
  })

  it('★★★ 回传方言经 encodeUpstream 落到请求体(路由器 → 编码器那段接线)', () => {
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '正文', opaque: {
      protocol: 'openai-responses', item: { type: 'reasoning', id: 'rs-1' }
    } }], 0)]
    const body = (replay: ReasoningReplay) => encodeUpstream('openai-responses', { ...REQUEST, messages }, 'gpt-test', 'k', {
      userId: 'ws-test', cacheTtl: '5m', reasoningReplay: replay
    }).body as { input: unknown[] }
    expect(body('text-required').input[0]).toEqual({ type: 'reasoning', id: 'rs-1', content: [{ type: 'reasoning_text', text: '正文' }] })
    expect(body('opaque-only').input[0]).toEqual({ type: 'reasoning', id: 'rs-1' })
  })

  it('keeps an item that already carries reasoning bytes untouched even when the transcript has text', () => {
    const messages = [assistantMessage('a', [{ type: 'thinking', text: '检查参数。', opaque: {
      protocol: 'openai-responses', item: { type: 'reasoning', id: 'rs-1', encrypted_content: 'enc' }
    } }], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input
    expect(input[0]).toEqual({ type: 'reasoning', id: 'rs-1', encrypted_content: 'enc' })
    // ★ 带引号匹配字段名,`encrypted_content` 自己也含 "content" 子串
    expect(JSON.stringify(input)).not.toContain('"content"')
  })

  it('does not invent reasoning for thinking blocks that have no text at all', () => {
    const messages = [assistantMessage('a', [
      { type: 'thinking', text: '', opaque: { signature: 'anthropic-sig' } },
      { type: 'tool_call', callId: 'c', name: 'Echo', input: {} }
    ], 0)]
    const input = (encodeOpenAIResponses({ ...REQUEST, messages }, 'gpt-test', 'k').body as { input: unknown[] }).input
    expect(input[0]).toEqual({ type: 'function_call', call_id: 'c', name: 'Echo', arguments: '{}' })
    expect(JSON.stringify(input)).not.toContain('reasoning')
  })

  it('encodes vision blocks and omits tools for tool-free requests', () => {
    const request = { ...REQUEST, tools: [], messages: [userMessage('u', [
      { type: 'text', text: '看图' }, { type: 'image', mime: 'image/png', dataRef: 'data:image/png;base64,aGVsbG8=' }
    ], 0)] }
    expect(encodeOpenAIChat(request, 'm', 'k').body).toMatchObject({ messages: [
      { role: 'system' }, { role: 'user', content: [{ type: 'text', text: '看图' }, { type: 'image_url', image_url: { url: 'data:image/png;base64,aGVsbG8=' } }] }
    ] })
    expect(encodeOpenAIResponses(request, 'm', 'k').body).toMatchObject({ input: [
      { role: 'user', content: [{ type: 'input_text', text: '看图' }, { type: 'input_image', image_url: 'data:image/png;base64,aGVsbG8=' }] }
    ] })
    for (const encode of [encodeOpenAIChat, encodeOpenAIResponses]) {
      expect(encode(request, 'm', 'k').body).not.toHaveProperty('tools')
      expect(encode(request, 'm', 'k').body).not.toHaveProperty('tool_choice')
    }
  })
})
