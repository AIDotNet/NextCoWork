import { describe, expect, it } from 'vitest'
import type { ContentPart } from '../../../shared/agent/message'
import type { ProviderStreamEvent } from '../../../shared/agent/stream'
import { applyEvents, emptyTranscript } from '../../../shared/agent/transcript'
import { BlockAccumulator } from '../block-accumulator'

/** 把一串事件喂进去,拿回收尾结果 */
function run(events: ProviderStreamEvent[]): ReturnType<BlockAccumulator['finalize']> {
  const acc = new BlockAccumulator()
  for (const e of events) acc.apply(e)
  return acc.finalize()
}

/** 一次完整的工具调用:start → delta* → end */
function call(index: number, callId: string, name: string, args: string): ProviderStreamEvent[] {
  return [
    { type: 'tool_call_start', index, callId, name },
    { type: 'tool_call_delta', index, callId, argsDelta: args },
    { type: 'tool_call_end', index, callId }
  ]
}

describe('文本与思考', () => {
  it('空流产出空结果', () => {
    expect(run([])).toEqual({ parts: [], calls: [] })
  })

  it('同一 index 的多个增量拼成一个块', () => {
    const { parts } = run([
      { type: 'text_delta', index: 0, text: '你好' },
      { type: 'text_delta', index: 0, text: ',世界' }
    ])
    expect(parts).toEqual([{ type: 'text', text: '你好,世界' }])
  })

  /**
   * ★ 「文本 → 工具调用 → 文本」是一条消息里的常见形状(方案 §4.2)。
   * 没有 index 就无法归位 —— 这条测的正是 index 真的被用上了。
   */
  it('不同 index 是不同的块', () => {
    const { parts } = run([
      { type: 'text_delta', index: 0, text: '先说一句' },
      { type: 'text_delta', index: 1, text: '再说一句' }
    ])
    expect(parts).toEqual([
      { type: 'text', text: '先说一句' },
      { type: 'text', text: '再说一句' }
    ])
  })

  /** 事件到达顺序在并行工具调用时会交错,而块的先后是**模型表达的顺序** */
  it('按 index 排序,不按到达顺序', () => {
    const { parts } = run([
      { type: 'text_delta', index: 2, text: 'C' },
      { type: 'text_delta', index: 0, text: 'A' },
      { type: 'text_delta', index: 1, text: 'B' }
    ])
    expect(parts.map((p) => (p.type === 'text' ? p.text : ''))).toEqual(['A', 'B', 'C'])
  })

  /** 空 text 块上行就是 400(`text content blocks must be non-empty`) */
  it('空文本块被丢掉', () => {
    expect(run([{ type: 'text_delta', index: 0, text: '' }]).parts).toEqual([])
  })

  it('思考块单独成 part', () => {
    const { parts } = run([
      { type: 'thinking_delta', index: 0, text: '让我想想' },
      { type: 'text_delta', index: 1, text: '答案是 42' }
    ])
    expect(parts).toEqual([
      { type: 'thinking', text: '让我想想' },
      { type: 'text', text: '答案是 42' }
    ])
  })

  /** 签名在 content_block_stop 时才到齐,而下一轮必须原样回传,丢了就报错 */
  it('思考块带上 opaque', () => {
    const { parts } = run([
      { type: 'thinking_delta', index: 0, text: '嗯' },
      { type: 'block_opaque', index: 0, opaque: { signature: 'sig-abc' } }
    ])
    expect(parts).toEqual([
      { type: 'thinking', text: '嗯', opaque: { signature: 'sig-abc' } }
    ])
  })

  it('没有 opaque 时不产出 opaque 字段', () => {
    const { parts } = run([{ type: 'thinking_delta', index: 0, text: '嗯' }])
    expect(parts[0]).not.toHaveProperty('opaque')
  })

  /**
   * ★ `redacted_thinking` 整块不透明,**一个 delta 都没有**(见 decode/anthropic.ts)。
   * 所以 opaque 事件必须能凭自己把块建出来,否则那一块就在这里蒸发,
   * 下一轮回传时 Anthropic 会认为思考链被篡改。
   */
  it('block_opaque 能凭自己建出块(redacted_thinking)', () => {
    const { parts } = run([{ type: 'block_opaque', index: 0, opaque: { redacted: 'zzz' } }])
    expect(parts).toEqual([{ type: 'thinking', text: '', opaque: { redacted: 'zzz' } }])
  })

  /** 正常思考块正文为空时没有信息量,但 redacted 的空正文**是**内容 —— 上一条测的就是这个差别 */
  it('既无正文又无 opaque 的思考块被丢掉', () => {
    expect(run([{ type: 'thinking_delta', index: 0, text: '' }]).parts).toEqual([])
  })
})

describe('畸形流不破坏已有块', () => {
  /**
   * ★ 上游把两种块塞进同一个 index 时,覆盖会**毁掉一个进行中的 tool_call**,
   * 凭空制造孤儿 —— 而孤儿 tool_use 下一轮就是 400。
   */
  it('文本增量落到 tool_call 块上时被丢弃,不覆盖', () => {
    const { parts, calls } = run([
      { type: 'tool_call_start', index: 0, callId: 'c1', name: 'read_file' },
      { type: 'text_delta', index: 0, text: '不该出现' },
      { type: 'tool_call_delta', index: 0, callId: 'c1', argsDelta: '{"p":"a.ts"}' },
      { type: 'tool_call_end', index: 0, callId: 'c1' }
    ])
    expect(calls).toEqual([{ ok: true, callId: 'c1', name: 'read_file', input: { p: 'a.ts' } }])
    expect(parts).toHaveLength(1)
    expect(parts[0]?.type).toBe('tool_call')
  })

  it('思考增量落到文本块上时被丢弃', () => {
    const { parts } = run([
      { type: 'text_delta', index: 0, text: '正文' },
      { type: 'thinking_delta', index: 0, text: '不该混进来' }
    ])
    expect(parts).toEqual([{ type: 'text', text: '正文' }])
  })

  it('block_opaque 落到文本块上时被忽略', () => {
    const { parts } = run([
      { type: 'text_delta', index: 0, text: '正文' },
      { type: 'block_opaque', index: 0, opaque: { signature: 's' } }
    ])
    expect(parts).toEqual([{ type: 'text', text: '正文' }])
  })

  /** 没见过 start 就来的 end:无从知道工具名。编一个出来更糟 —— 回传时不匹配任何 tool_use */
  it('没有 start 的 tool_call_end 被丢弃', () => {
    expect(run([{ type: 'tool_call_end', index: 0, callId: 'ghost' }])).toEqual({
      parts: [],
      calls: []
    })
  })

  it('tool_call_start 会替换同 index 的文本块', () => {
    const { parts, calls } = run([
      { type: 'text_delta', index: 0, text: '半截文本' },
      ...call(0, 'c1', 'echo', '{}')
    ])
    expect(parts).toEqual([{ type: 'tool_call', callId: 'c1', name: 'echo', input: {} }])
    expect(calls).toHaveLength(1)
  })

  it('未知事件类型不产生块', () => {
    const { parts } = run([
      { type: 'message_start', model: 'claude-sonnet-4' },
      { type: 'provider_retry', attempt: 1, delayMs: 100, reason: 'overloaded' },
      { type: 'text_delta', index: 0, text: 'ok' },
      { type: 'message_end', stopReason: 'end_turn', usage: { inputTokens: 1, outputTokens: 1 } }
    ])
    expect(parts).toEqual([{ type: 'text', text: 'ok' }])
  })
})

describe('工具调用', () => {
  it('闭合的调用同时产出 part 与待执行项', () => {
    const { parts, calls } = run(call(0, 'c1', 'read_file', '{"path":"a.ts"}'))
    expect(parts).toEqual([
      { type: 'tool_call', callId: 'c1', name: 'read_file', input: { path: 'a.ts' } }
    ])
    expect(calls).toEqual([{ ok: true, callId: 'c1', name: 'read_file', input: { path: 'a.ts' } }])
  })

  /** 部分上游一次性给全整个工具调用对象 —— adapter 契约说 delta 数量**可以为 0** */
  it('零个 delta 的调用参数是空对象', () => {
    const { calls } = run([
      { type: 'tool_call_start', index: 0, callId: 'c1', name: 'now' },
      { type: 'tool_call_end', index: 0, callId: 'c1' }
    ])
    expect(calls).toEqual([{ ok: true, callId: 'c1', name: 'now', input: {} }])
  })

  it('参数分多个 delta 到达时拼起来再解析', () => {
    const { calls } = run([
      { type: 'tool_call_start', index: 0, callId: 'c1', name: 'grep' },
      { type: 'tool_call_delta', index: 0, callId: 'c1', argsDelta: '{"q":' },
      { type: 'tool_call_delta', index: 0, callId: 'c1', argsDelta: '"foo"}' },
      { type: 'tool_call_end', index: 0, callId: 'c1' }
    ])
    expect(calls).toEqual([{ ok: true, callId: 'c1', name: 'grep', input: { q: 'foo' } }])
  })

  it('两个并行调用各自归位', () => {
    const { parts, calls } = run([
      { type: 'tool_call_start', index: 0, callId: 'c1', name: 'read_file' },
      { type: 'tool_call_start', index: 1, callId: 'c2', name: 'list_dir' },
      { type: 'tool_call_delta', index: 1, callId: 'c2', argsDelta: '{"d":"src"}' },
      { type: 'tool_call_delta', index: 0, callId: 'c1', argsDelta: '{"p":"a.ts"}' },
      { type: 'tool_call_end', index: 1, callId: 'c2' },
      { type: 'tool_call_end', index: 0, callId: 'c1' }
    ])
    expect(calls.map((c) => c.callId)).toEqual(['c1', 'c2'])
    expect(calls[0]).toMatchObject({ ok: true, input: { p: 'a.ts' } })
    expect(calls[1]).toMatchObject({ ok: true, input: { d: 'src' } })
    expect(parts).toHaveLength(2)
  })

  /**
   * ★ 本文件最重要的一对。参数解析失败时 `tool_call` 块**照样进转录**:
   * 少了 tool_call,紧接着那条 tool_result 就没有配对的 tool_use;
   * 少了 tool_result,这个 tool_use 就是孤儿。**两个方向都是 400。**
   */
  it('参数非法时 part 仍在,入参用空对象占位', () => {
    const { parts, calls } = run(call(0, 'c1', 'read_file', '{"path": '))
    expect(parts).toEqual([{ type: 'tool_call', callId: 'c1', name: 'read_file', input: {} }])
    expect(calls).toHaveLength(1)
    expect(calls[0]?.ok).toBe(false)
  })

  /** 只说「参数错了」模型无从下手,得把原文还给它 */
  it('参数非法时带回原文', () => {
    const { calls } = run(call(0, 'c1', 'read_file', '{"path": '))
    const c = calls[0]
    expect(c?.ok).toBe(false)
    if (c?.ok === false) {
      expect(c.raw).toBe('{"path": ')
      expect(c.reason).not.toBe('')
    }
  })

  it('每个 part 都有配对的待执行项,反之亦然', () => {
    const { parts, calls } = run([
      ...call(0, 'c1', 'a', '{}'),
      ...call(1, 'c2', 'b', '不是 JSON'),
      ...call(2, 'c3', 'c', '{"x":1}')
    ])
    const partIds = parts
      .filter((p): p is Extract<ContentPart, { type: 'tool_call' }> => p.type === 'tool_call')
      .map((p) => p.callId)
    expect(partIds).toEqual(calls.map((c) => c.callId))
    expect(partIds).toEqual(['c1', 'c2', 'c3'])
  })
})

describe('未闭合的工具调用一律丢弃', () => {
  /**
   * ★ 依据是 §4.2 的 adapter 契约:「**必须**发 tool_call_start … tool_call_end」。
   * 所以缺了 end 只有一个含义 —— 模型还在写这个调用,**它还不是一次调用**。
   *
   * 保留它的两条路都通向 400:参数是残缺 JSON,执行它等于按模型没写完的意图动手;
   * 不执行又留下一个孤儿 tool_use。
   */
  it('只有 start 的块不产出任何东西', () => {
    expect(
      run([
        { type: 'tool_call_start', index: 0, callId: 'c1', name: 'write_file' },
        { type: 'tool_call_delta', index: 0, callId: 'c1', argsDelta: '{"path":"a.ts","conte' }
      ])
    ).toEqual({ parts: [], calls: [] })
  })

  it('丢掉未闭合的,保留同一轮里已闭合的', () => {
    const { parts, calls } = run([
      ...call(0, 'c1', 'read_file', '{"p":"a.ts"}'),
      { type: 'tool_call_start', index: 1, callId: 'c2', name: 'write_file' },
      { type: 'tool_call_delta', index: 1, callId: 'c2', argsDelta: '{"p":' }
    ])
    expect(calls.map((c) => c.callId)).toEqual(['c1'])
    expect(parts).toHaveLength(1)
  })

  /** 中断时前面的文字是用户已经读到的,不能因为后面半截调用被丢就一起消失 */
  it('未闭合的调用不影响它前面的文本块', () => {
    const { parts } = run([
      { type: 'text_delta', index: 0, text: '我来读一下这个文件' },
      { type: 'tool_call_start', index: 1, callId: 'c1', name: 'read_file' }
    ])
    expect(parts).toEqual([{ type: 'text', text: '我来读一下这个文件' }])
  })
})

describe('finalize 是只读且幂等的', () => {
  /** 中断路径会在 session 的 catch 里调它,而那时 apply 可能停在任何位置 */
  it('重复调用结果相同', () => {
    const acc = new BlockAccumulator()
    for (const e of call(0, 'c1', 'echo', '{"m":"hi"}')) acc.apply(e)
    acc.apply({ type: 'text_delta', index: 1, text: '好了' })

    const first = acc.finalize()
    expect(acc.finalize()).toEqual(first)
    expect(acc.finalize()).toEqual(first)
  })

  it('收尾之后还能继续 apply', () => {
    const acc = new BlockAccumulator()
    acc.apply({ type: 'text_delta', index: 0, text: '半' })
    expect(acc.finalize().parts).toEqual([{ type: 'text', text: '半' }])

    acc.apply({ type: 'text_delta', index: 0, text: '截' })
    expect(acc.finalize().parts).toEqual([{ type: 'text', text: '半截' }])
  })
})

describe('思考卡片:用时与 token', () => {
  /** 模拟 session 的 `acc.apply(ev, clock.now())`:每条事件带主进程时钟 */
  function runAt(events: ReadonlyArray<readonly [ProviderStreamEvent, number]>): ContentPart[] {
    const acc = new BlockAccumulator()
    for (const [e, at] of events) acc.apply(e, at)
    return acc.finalize().parts
  }
  const end = (reasoningTokens?: number): ProviderStreamEvent => ({
    type: 'message_end',
    stopReason: 'end_turn',
    usage: { inputTokens: 10, outputTokens: 500, ...(reasoningTokens === undefined ? {} : { reasoningTokens }) }
  })
  const thinkingOf = (parts: ContentPart[]): Array<Extract<ContentPart, { type: 'thinking' }>> =>
    parts.filter((p): p is Extract<ContentPart, { type: 'thinking' }> => p.type === 'thinking')

  /**
   * ★ OpenAI Responses 这类上游先在服务端闷头推理 30 秒,最后才吐一小段摘要。
   * 按首个思考增量起算的话,卡片会写「思考 1 秒」。
   */
  it('起点取 message_start 而不是首个思考增量,服务端闷头推理的那段也算思考', () => {
    const parts = runAt([
      [{ type: 'message_start', model: 'm' }, 1_000],
      [{ type: 'thinking_delta', index: 0, text: '摘要' }, 31_000],
      [{ type: 'thinking_delta', index: 0, text: '续' }, 32_000],
      [{ type: 'text_delta', index: 1, text: '答' }, 33_000],
      [end(), 34_000]
    ])
    expect(thinkingOf(parts)[0]?.durationMs).toBe(31_000)
  })

  it('交错思考的第二块从上一个块的最后一个增量起算,止于自己最后一个增量', () => {
    const parts = runAt([
      [{ type: 'message_start', model: 'm' }, 0],
      [{ type: 'thinking_delta', index: 0, text: 'a' }, 100],
      [{ type: 'text_delta', index: 1, text: 'b' }, 500],
      [{ type: 'thinking_delta', index: 2, text: 'c' }, 900],
      [{ type: 'thinking_delta', index: 2, text: 'd' }, 1_400],
      [{ type: 'block_opaque', index: 2, opaque: { signature: 's' } }, 5_000]
    ])
    expect(thinkingOf(parts).map((p) => p.durationMs)).toEqual([100, 900])
  })

  it('不传时钟就不写 durationMs —— 与旧转录一样「没有事实就不画」', () => {
    expect(run([{ type: 'thinking_delta', index: 0, text: '嗯' }]).parts[0]).not.toHaveProperty('durationMs')
  })

  it('这次回复只有一块有正文的思考时,上游 reasoningTokens 归给它', () => {
    const parts = runAt([
      [{ type: 'message_start', model: 'm' }, 0],
      [{ type: 'thinking_delta', index: 0, text: '想' }, 10],
      [{ type: 'text_delta', index: 1, text: '答' }, 20],
      [end(321), 30]
    ])
    expect(thinkingOf(parts)[0]?.tokens).toBe(321)
  })

  /** 按长度摊派出来的「真值」比明说是约数更误导人 —— 交给渲染层按正文估算 */
  it('多块思考时不摊派 reasoningTokens', () => {
    const parts = runAt([
      [{ type: 'thinking_delta', index: 0, text: '一' }, 10],
      [{ type: 'thinking_delta', index: 1, text: '二' }, 20],
      [end(321), 30]
    ])
    expect(thinkingOf(parts).every((p) => p.tokens === undefined)).toBe(true)
  })

  it('上游没报 reasoningTokens 时不写 tokens', () => {
    const parts = runAt([[{ type: 'thinking_delta', index: 0, text: '想' }, 10], [end(), 20]])
    expect(thinkingOf(parts)[0]).not.toHaveProperty('tokens')
  })

  /**
   * ★ 渲染层的实时计时(`transcript.ts` 的 LiveBlock.startedAt/endedAt)和这里落盘的
   * durationMs 是两份实现。口径一旦分叉,提交那一刻卡片上的秒数就会跳 —— 这条把两边钉在一起。
   */
  it('与渲染层 reducer 的实时计时同口径,提交前后时长不跳', () => {
    const events: Array<readonly [ProviderStreamEvent, number]> = [
      [{ type: 'message_start', model: 'm' }, 1_000],
      [{ type: 'tool_call_start', index: 0, callId: 'c', name: 'read' }, 1_500],
      [{ type: 'tool_call_delta', index: 0, callId: 'c', argsDelta: '{}' }, 2_000],
      [{ type: 'thinking_delta', index: 1, text: '想' }, 4_000],
      [{ type: 'thinking_delta', index: 1, text: '完' }, 6_500],
      [{ type: 'text_delta', index: 2, text: '答' }, 7_000]
    ]
    const live = applyEvents(emptyTranscript(), events.map(([delta, at]) => ({ type: 'stream', delta, at })))
      .live.find((b) => b.kind === 'thinking')
    const committed = thinkingOf(runAt(events))[0]
    expect(live?.startedAt).toBeDefined()
    expect(committed?.durationMs).toBe((live?.endedAt ?? 0) - (live?.startedAt ?? 0))
    expect(committed?.durationMs).toBe(4_500)
  })
})
