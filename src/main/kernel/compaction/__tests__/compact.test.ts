/**
 * 压缩核心(`compactConversation`)的用例。
 *
 * 需求:这是上下文压缩**唯一**的实现,自动压缩和手动 /compact 共用它。
 * 它的失败模式几乎全是静默的 —— 摘要把最早一段丢了、PTL 重试把请求切成非法形状、
 * 压完又立刻压一次。所以这里钉的都是「破了也不报错」的那几条。
 *
 * 这个模块不碰数据库、不发事件、不知道 run(依赖全部注入),因此整组用例都是纯单测,
 * 不需要起 Electron —— 那正是把它从 `agent-session` 里抽出来的理由。
 */
import { describe, expect, it, vi } from 'vitest'
import { agentError } from '../../../../shared/agent/error'
import type { AgentMessage, ContentPart } from '../../../../shared/agent/message'
import { assistantMessage, orphanedToolCalls, userMessage } from '../../../../shared/agent/message'
import type { ProviderStreamEvent } from '../../../../shared/agent/stream'
import { compactBoundaryOf } from '../../../../shared/agent/compaction'
import {
  compactConversation,
  prepareForSummary,
  truncateHead,
  COMPACT_SYSTEM,
  MAX_PTL_RETRIES,
  type CompactInput,
  type SummaryRequest
} from '../compact'

const NOW = Date.UTC(2026, 8, 4, 12, 0, 0)
const SUMMARY = '<analysis>草稿</analysis>\n<summary>1. 意图: 重构登录模块</summary>'

function ask(id: string, text: string): AgentMessage {
  return userMessage(id, [{ type: 'text', text }], NOW)
}

function boundaryMessage(id: string, summary: string): AgentMessage {
  const parts: ContentPart[] = [
    { type: 'compact_boundary', trigger: 'auto', preTokens: 100, postTokens: 10, summary },
    { type: 'text', text: summary }
  ]
  return { ...userMessage(id, parts, NOW), internal: true, parts }
}

/** 一轮工具往返。`isError` 决定这次调用算不算成功。 */
function toolTurn(i: number, name = 'Read', input: unknown = { file_path: `/ws/f${i}.ts` }): AgentMessage[] {
  return [
    assistantMessage(`a${i}`, [{ type: 'tool_call', callId: `c${i}`, name, input }], NOW),
    userMessage(`r${i}`, [{ type: 'tool_result', callId: `c${i}`, output: { content: `内容 ${i}` }, isError: false }], NOW)
  ]
}

function streamOf(text: string): AsyncIterable<ProviderStreamEvent> {
  return (async function* () {
    yield { type: 'message_start', model: 'm' } as ProviderStreamEvent
    yield { type: 'text_delta', index: 0, text } as ProviderStreamEvent
    yield { type: 'message_end', stopReason: 'end_turn', usage: { inputTokens: 1, outputTokens: 1 } } as ProviderStreamEvent
  })()
}

function input(over: Partial<CompactInput> = {}): CompactInput {
  return {
    messages: [ask('u1', '帮我重构登录模块'), ...toolTurn(1)],
    trigger: 'auto',
    preTokens: 200_000,
    autoContinue: true,
    protocolWindow: 200_000,
    summaryMaxOutputTokens: 20_000,
    summaryThinking: 'off',
    summaryModel: { model: 'sum-model', thinking: 'off', fellBack: false },
    send: () => streamOf(SUMMARY),
    attachments: { tools: { file: new Set(['Read']) }, readFile: async () => undefined },
    newId: () => 'boundary-1',
    now: NOW,
    signal: new AbortController().signal,
    ...over
  }
}

describe('compactConversation', () => {
  it('产出一条 internal user 消息,第一个 part 是边界', async () => {
    const result = await compactConversation(input())
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.message.role).toBe('user')
    expect(result.message.internal).toBe(true)
    expect(result.message.parts[0]?.type).toBe('compact_boundary')
    expect(compactBoundaryOf(result.message)?.summary).toBe('1. 意图: 重构登录模块')
  })

  /**
   * ★ `<analysis>` 是给模型打草稿用的,**不能进上下文**:它通常和摘要一样长,
   * 留着等于把压缩省下的 token 吃回去一半,而且不会有任何报错。
   */
  it('★ 摘要里剥掉 analysis 草稿', async () => {
    const result = await compactConversation(input())
    expect(result.ok && result.message.parts.some((p) => p.type === 'text' && p.text.includes('草稿'))).toBe(false)
  })

  /**
   * ★ **自动压缩必须让模型接着干。** 少了这句续接语,模型压完会礼貌地复述一遍摘要
   * 然后问「要继续吗」,一个长任务就此停住 —— 而 run 还在跑,用户只看到它忽然不动了。
   */
  it('★ 自动压缩带「别再问」的续接语,手动压缩不带', async () => {
    const auto = await compactConversation(input({ autoContinue: true }))
    const manual = await compactConversation(input({ autoContinue: false, trigger: 'manual' }))
    const text = (r: typeof auto): string =>
      r.ok ? r.message.parts.flatMap((p) => (p.type === 'text' ? [p.text] : [])).join('\n') : ''
    expect(text(auto)).toContain('without asking the user any further questions')
    expect(text(manual)).not.toContain('without asking the user any further questions')
  })

  it('摘要请求走 COMPACT_SYSTEM,并把压缩指令接在对话末尾', async () => {
    const send = vi.fn((_r: SummaryRequest) => streamOf(SUMMARY))
    await compactConversation(input({ send }))
    const request = send.mock.calls[0]?.[0]
    expect(request?.system).toBe(COMPACT_SYSTEM)
    const last = request?.messages.at(-1)
    expect(last?.role).toBe('user')
    expect(JSON.stringify(last?.parts)).toContain('Your task is to create a detailed summary')
  })

  /**
   * ★ 末尾已经是 user 消息(工具回执)时压缩指令**并进那一条**,不另起一条:
   * 连续两条 user 消息有的上游会拒、有的会悄悄合并,行为不一致 —— 而不一致的那一半
   * 表现为「压缩在某一家供应商上永远失败」。
   */
  it('★ 末尾是工具回执时压缩指令并入同一条,不产生连续两条 user', async () => {
    const send = vi.fn((_r: SummaryRequest) => streamOf(SUMMARY))
    await compactConversation(input({ send }))
    const messages = send.mock.calls[0]?.[0].messages ?? []
    const roles = messages.map((m) => m.role)
    expect(roles.filter((r, i) => r === 'user' && roles[i + 1] === 'user')).toEqual([])
  })

  it('/compact 的补充指令进提示词,也记在边界上', async () => {
    const send = vi.fn((_r: SummaryRequest) => streamOf(SUMMARY))
    const result = await compactConversation(input({ send, trigger: 'manual', instructions: '重点保留权限那一段' }))
    expect(JSON.stringify(send.mock.calls[0]?.[0].messages)).toContain('重点保留权限那一段')
    expect(result.ok && compactBoundaryOf(result.message)?.instructions).toBe('重点保留权限那一段')
  })

  /**
   * ★★ 「刚压过又压一次」要挡住。边界之后只剩摘要本身时再压一次,等于拿摘要去
   * 摘要摘要 —— 每一轮多一次模型请求,而占用一个 token 都不会降。
   */
  it('★★ 边界之后没有真实对话时拒绝压缩', async () => {
    const result = await compactConversation(input({
      messages: [ask('u1', '早期'), boundaryMessage('b1', '之前的摘要')]
    }))
    expect(result.ok).toBe(false)
    expect(!result.ok && result.error.messageKey).toBe('chat.compaction.nothingToCompact')
  })

  it('模型写了个空摘要时判失败,不产出一条空边界', async () => {
    const result = await compactConversation(input({ send: () => streamOf('<analysis>只有草稿</analysis>') }))
    expect(result.ok).toBe(false)
    expect(!result.ok && result.error.messageKey).toBe('chat.compaction.emptySummary')
  })

  /**
   * ★ 上游报 prompt 太长 → 从最早一侧丢掉一段**再试**,最多 `MAX_PTL_RETRIES` 次。
   * 丢的只是这次摘要请求的输入,转录本身一条不动。
   */
  it('★ context_length 触发 PTL 重试,且有上限', async () => {
    const send = vi.fn((_r: SummaryRequest) => (async function* () {
      yield { type: 'error', error: agentError('context_length', 'prompt too long') } as ProviderStreamEvent
    })())
    const long = [ask('u1', '开始'), ...toolTurn(1), ask('u2', '继续'), ...toolTurn(2), ask('u3', '再继续'), ...toolTurn(3)]
    const result = await compactConversation(input({ send, messages: long }))
    expect(result.ok).toBe(false)
    expect(send.mock.calls.length).toBeGreaterThan(1)
    expect(send.mock.calls.length).toBeLessThanOrEqual(MAX_PTL_RETRIES + 1)
    // 每次重试的输入都更短
    const sizes = send.mock.calls.map((c) => c[0].messages.length)
    expect(sizes).toEqual([...sizes].sort((a, b) => b - a))
  })

  /** ★ 其它错误一次就停 —— 重试一次同样的请求只是再烧一次钱。 */
  it('★ 非 context_length 的失败不重试', async () => {
    const send = vi.fn((_r: SummaryRequest) => (async function* () {
      yield { type: 'error', error: agentError('provider', 'boom') } as ProviderStreamEvent
    })())
    await compactConversation(input({ send }))
    expect(send.mock.calls.length).toBe(1)
  })

  it('重附的文件路径记在边界上,内容跟在摘要后面', async () => {
    const result = await compactConversation(input({
      attachments: {
        tools: { file: new Set(['Read']) },
        readFile: async (path) => (path === '/ws/f1.ts' ? 'export const a = 1' : undefined)
      }
    }))
    expect(result.ok && compactBoundaryOf(result.message)?.restoredFiles).toEqual(['/ws/f1.ts'])
    expect(result.ok && JSON.stringify(result.message.parts)).toContain('export const a = 1')
  })

  /** 压缩前后的读数都落在边界上 —— 状态行那句「省下 N」读的就是它。 */
  it('边界记下压缩前后的占用', async () => {
    const result = await compactConversation(input({ preTokens: 624_000 }))
    const boundary = result.ok ? compactBoundaryOf(result.message) : undefined
    expect(boundary?.preTokens).toBe(624_000)
    expect(boundary?.postTokens).toBeGreaterThan(0)
    expect(boundary?.postTokens).toBeLessThan(624_000)
  })

  /**
   * ★ 档位和输出额度由**调用方**给(解析与归一化在 `binding.ts`),本模块只负责原样带上。
   * 原先这两样写死在这里(`'off'` + 20K),而 `gpt-6-*` 这类关不掉推理的模型会因为
   * 那个 `'off'` 被上游拒 —— 压缩每次必败,三次后熔断。
   */
  it('★ 摘要请求带上调用方给的档位与输出额度', async () => {
    const send = vi.fn((_r: SummaryRequest) => streamOf(SUMMARY))
    await compactConversation(input({ send, summaryThinking: 'low', summaryMaxOutputTokens: 12_345 }))
    expect(send.mock.calls[0]?.[0].thinkingLevel).toBe('low')
    expect(send.mock.calls[0]?.[0].maxOutputTokens).toBe(12_345)
  })

  it('边界记下写这份摘要的模型、档位,以及回落发生过', async () => {
    const result = await compactConversation(input({
      summaryThinking: 'low',
      summaryModel: { model: 'gpt-6-luna', modelProviderId: 'openai', thinking: 'low', fellBack: true }
    }))
    const boundary = result.ok ? compactBoundaryOf(result.message) : undefined
    expect(boundary?.summaryModel).toEqual({
      model: 'gpt-6-luna', modelProviderId: 'openai', thinking: 'low', fellBack: true
    })
  })

  /** 没回落就不写 `fellBack` —— 一个恒为 false 的字段会落在每一条边界上。 */
  it('没有回落时边界上不写 fellBack', async () => {
    const result = await compactConversation(input())
    const boundary = result.ok ? compactBoundaryOf(result.message) : undefined
    expect(boundary?.summaryModel).toEqual({ model: 'sum-model', thinking: 'off' })
  })

  /**
   * ★★ 输出额度现在跟随全局设置(默认 32K),而压缩模型可能只有 8K / 32K 窗口 ——
   * 那时「窗口 − 输出 − 指令」是负数。不封底的话 while 会一路 `truncateHead` 到
   * 只剩一条消息,然后拿那一条去写「整段对话的摘要」:压缩显示成功,摘要却只提到
   * 最后一句话,而且零报错。
   */
  it('★★ 输出额度吃光窗口时输入预算封底,不把请求裁成一条', async () => {
    const send = vi.fn((_r: SummaryRequest) => streamOf(SUMMARY))
    const long = [ask('u1', '开始'), ...toolTurn(1), ask('u2', '继续'), ...toolTurn(2), ask('u3', '再继续'), ...toolTurn(3)]
    await compactConversation(input({
      send, messages: long, protocolWindow: 8_000, summaryMaxOutputTokens: 32_000
    }))
    expect(send.mock.calls[0]?.[0].messages.length).toBeGreaterThan(1)
  })
})

describe('prepareForSummary', () => {
  /** 摘要不需要看图,而图片是最贵的输入。 */
  it('图片换成占位,思考块整条去掉', () => {
    const messages = [
      userMessage('u1', [{ type: 'image', mime: 'image/png', dataRef: 'ref' }], NOW),
      assistantMessage('a1', [
        { type: 'thinking', text: '草稿', opaque: { sig: 'x' } },
        { type: 'text', text: '好的' }
      ], NOW)
    ]
    const out = prepareForSummary(messages)
    expect(JSON.stringify(out)).not.toContain('thinking')
    expect(JSON.stringify(out)).toContain('[image]')
  })

  /** 摘要是压缩后模型唯一的记忆:托管图的地址丢了,它就再也点不了名(改图、SaveImage)。 */
  it('ncw 托管图的占位带上地址,内联图只留 [image]', () => {
    const url = 'ncw://attachments/sessions/s1/01ABC.png'
    const out = prepareForSummary([
      userMessage('u1', [
        { type: 'image', mime: 'image/png', dataRef: url },
        { type: 'image', mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' }
      ], NOW)
    ])
    expect(out[0]?.parts).toEqual([
      { type: 'text', text: `[image] [Image URL: ${url}]` },
      { type: 'text', text: '[image]' }
    ])
  })

  /** 剥完变成空消息的整条丢掉 —— 一条 parts 为空的消息有的上游直接判非法。 */
  it('只含思考块的消息被整条去掉', () => {
    const out = prepareForSummary([
      assistantMessage('a1', [{ type: 'thinking', text: '草稿', opaque: { sig: 'x' } }], NOW),
      assistantMessage('a2', [{ type: 'text', text: '好的' }], NOW)
    ])
    expect(out.map((m) => m.id)).toEqual(['a2'])
  })
})

describe('truncateHead', () => {
  const long = (): AgentMessage[] => [
    ask('u1', '开始'), ...toolTurn(1), ask('u2', '继续'), ...toolTurn(2), ask('u3', '再继续'), ...toolTurn(3)
  ]

  /**
   * ★★ 切点必须是一条**不含 tool_result 的 user 消息**。切在回执前面的话,剩下的
   * 开头就是一个没有配对 tool_call 的孤儿回执,上游直接判请求非法 ——
   * PTL 重试换来的是另一种失败,而错误信息会指向完全不同的方向。
   */
  it('★★ 切点落在一轮的开头,不留孤儿 tool_result', () => {
    const out = truncateHead(long())
    expect(out).toBeDefined()
    expect(out?.[0]?.role).toBe('user')
    expect(orphanedToolCalls(out ?? [])).toEqual([])
    expect(JSON.stringify(out?.[0]?.parts)).not.toContain('tool_result')
  })

  /** ★ 开头是上一次的边界(上一份摘要)时保留它:丢了它等于把更早那一段全忘了。 */
  it('★ 保留开头的边界消息,只丢它后面的', () => {
    const out = truncateHead([boundaryMessage('b1', '之前的摘要'), ...long()])
    expect(out?.[0]?.id).toBe('b1')
    expect(out?.length).toBeLessThan(long().length + 1)
  })

  it('丢不动时返回 undefined,而不是空数组', () => {
    expect(truncateHead([ask('u1', '只有一条')])).toBeUndefined()
    expect(truncateHead([])).toBeUndefined()
  })
})
