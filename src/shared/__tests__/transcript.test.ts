import { describe, expect, it } from 'vitest'
import type { AgentEvent } from '../agent/event'
import { assistantMessage, toolResultMessage, userMessage } from '../agent/message'
import type { TranscriptState } from '../agent/transcript'
import { applyChildEvent, applyEvent, applyEvents, emptyTranscript, hasRun, liveText, subagentsFromMessages } from '../agent/transcript'

/**
 * 转录投影的 bug 有个共同特征:**在界面上是间歇性的**。
 * 块按 index 错位要两个并行工具调用才现形,提交后残留活跃块要
 * 恰好在那一帧截图才看得见。所以这一组测的都是「肉眼查不出来」的东西。
 */

const t = (index: number, text: string): AgentEvent => ({
  type: 'stream',
  delta: { type: 'text_delta', index, text }
})

describe('applyEvent · 内容块', () => {
  it('同一 index 的 delta 累积成一段', () => {
    const s = applyEvents(emptyTranscript(), [t(0, '你'), t(0, '好'), t(0, '世界')])
    expect(s.live).toHaveLength(1)
    expect(s.live[0]?.text).toBe('你好世界')
  })

  it('不同 index 分成不同的块 —— 「文本 → 工具 → 文本」不能糊成一段', () => {
    const s = applyEvents(emptyTranscript(), [t(0, '前'), t(2, '后')])
    expect(s.live.map((b) => b.text)).toEqual(['前', '后'])
  })

  it('★ 块按 index 排序,与到达顺序无关', () => {
    // 并行工具调用时,两个块的首个 delta 可能乱序到达。
    // 不排序的话,UI 上就是「结论出现在前言之前」——
    // 一个只在并发时出现、截图都难抓的错乱。
    const s = applyEvents(emptyTranscript(), [t(2, '第三'), t(0, '第一'), t(1, '第二')])
    expect(s.live.map((b) => b.index)).toEqual([0, 1, 2])
    expect(s.live.map((b) => b.text)).toEqual(['第一', '第二', '第三'])
  })

  it('thinking 与 text 是不同 kind,不混进正文', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'thinking_delta', index: 0, text: '内心戏' } },
      t(1, '正文')
    ])
    expect(s.live.map((b) => b.kind)).toEqual(['thinking', 'text'])
    // ★ liveText 只取 text —— 把思考内容漏进正文是**用户可见的**信息泄漏
    expect(liveText(s)).toBe('正文')
  })

  it('工具调用参数按 index 累积,不落进 text 块', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'tool_call_start', index: 1, callId: 'c1', name: 'read' } },
      { type: 'stream', delta: { type: 'tool_call_delta', index: 1, callId: 'c1', argsDelta: '{"p"' } },
      { type: 'stream', delta: { type: 'tool_call_delta', index: 1, callId: 'c1', argsDelta: ':1}' } },
      { type: 'stream', delta: { type: 'tool_call_end', index: 1, callId: 'c1' } }
    ])
    expect(s.live).toHaveLength(1)
    expect(s.live[0]).toMatchObject({ kind: 'tool_use', callId: 'c1', name: 'read', text: '{"p":1}' })
    expect(liveText(s)).toBe('')
  })
})

describe('applyEvent · 提交边界', () => {
  it('★ 提交后活跃块必须清空 —— 否则全文重影', () => {
    let s = applyEvents(emptyTranscript(), [t(0, '你好')])
    expect(s.live).toHaveLength(1)

    s = applyEvent(s, {
      type: 'message_commit',
      message: assistantMessage('m1', [{ type: 'text', text: '你好' }], 1)
    })
    expect(s.live).toEqual([])
    expect(s.messages).toHaveLength(1)
  })

  it('提交之后新的 delta 起一段新的活跃块', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'message_commit',
      message: assistantMessage('m1', [{ type: 'text', text: '第一条' }], 1)
    })
    s = applyEvent(s, t(0, '第二条'))
    expect(s.messages).toHaveLength(1)
    expect(liveText(s)).toBe('第二条')
  })

  it('同一个消息 ID 的确认提交替换乐观消息,不会产生重复气泡', () => {
    const optimistic = userMessage('u1', [{ type: 'text', text: '发送中' }], 1)
    const confirmed = userMessage('u1', [{ type: 'text', text: '发送中' }], 2)
    const s = applyEvent({ ...emptyTranscript(), messages: [optimistic] }, {
      type: 'message_commit',
      message: confirmed
    })

    expect(s.messages).toEqual([confirmed])
  })

  /**
   * ★★ `message_start` 也清活跃块 —— 这一句是给**断流续跑**用的。
   *
   * 正常路径上它是无操作:上一条消息的 `message_commit` 已经清过了,所以
   * `message_start` 到达时 `live` 必空(下一条用例钉的就是这件事)。
   *
   * 续跑路径上它是全部:session 层断流重来时,失败那一次**没有 commit** ——
   * 它吐的半截文字只有这一句能抹掉。漏掉的话,第二次的 `text_delta index:0`
   * 会**续写**在半截文字后面,界面上是一段前后接不上的乱码。
   *
   * ⌘R 重载后的重放同理:`run-registry` 的 `trimSupersededDeltas` 只在
   * `message_commit` 时裁剪 delta,被丢弃那次的 delta 会原样留在日志里重放一遍。
   */
  it('★ message_start 清掉没提交过的半截内容 —— 断流续跑不留重影', () => {
    let s = applyEvents(emptyTranscript(), [t(0, '我先看一眼 con')])
    s = applyEvent(s, { type: 'stream', delta: { type: 'message_start', model: 'm' } })
    expect(s.live).toEqual([])

    s = applyEvent(s, t(0, '完整回答'))
    expect(liveText(s)).toBe('完整回答')
  })

  /** 正常路径上这一句什么也不做 —— 已提交的消息一条都不能被它动到 */
  it('提交之后来的 message_start 不改变任何东西', () => {
    let s = applyEvents(emptyTranscript(), [t(0, '你好')])
    s = applyEvent(s, {
      type: 'message_commit',
      message: assistantMessage('m1', [{ type: 'text', text: '你好' }], 1)
    })
    const before = s
    s = applyEvent(s, { type: 'stream', delta: { type: 'message_start', model: 'm' } })

    expect(s.messages).toEqual(before.messages)
    expect(s.live).toEqual([])
  })
})

describe('applyEvent · 工具状态', () => {
  const start: AgentEvent = {
    type: 'tool_start',
    callId: 'c1',
    toolName: 'read_file',
    input: { path: 'a.ts' }
  }

  it('start → progress → end 的状态流转', () => {
    let s = applyEvent(emptyTranscript(), start)
    expect(s.tools['c1']?.status).toBe('running')

    s = applyEvent(s, {
      type: 'tool_progress',
      callId: 'c1',
      progress: { callId: 'c1', message: '读取中' }
    })
    expect(s.tools['c1']?.progress).toBe('读取中')

    s = applyEvent(s, {
      type: 'tool_end',
      callId: 'c1',
      output: { content: 'ok' },
      isError: false
    })
    expect(s.tools['c1']?.status).toBe('ok')
    expect(s.tools['c1']?.output?.content).toBe('ok')
    // 结束后进度必须清掉,否则卡片上一直挂着「读取中」
    expect(s.tools['c1']?.progress).toBeUndefined()
  })

  it('isError 映射成 error 状态', () => {
    let s = applyEvent(emptyTranscript(), start)
    s = applyEvent(s, { type: 'tool_end', callId: 'c1', output: { content: '炸了' }, isError: true })
    expect(s.tools['c1']?.status).toBe('error')
  })

  it('★ tool_progress 携带的实时卡片进入 ToolCallState.card,tool_end 时清掉', () => {
    let s = applyEvent(emptyTranscript(), start)
    s = applyEvent(s, {
      type: 'tool_progress',
      callId: 'c1',
      progress: { callId: 'c1', message: '等待确认', card: { kind: 'declarative', blocks: [{ type: 'status', label: '待批' }] } }
    })
    expect(s.tools['c1']?.card).toEqual({ kind: 'declarative', blocks: [{ type: 'status', label: '待批' }] })
    // 结束后实时卡片必须清掉,让展示切到 output.card(结果快照)
    s = applyEvent(s, { type: 'tool_end', callId: 'c1', output: { content: 'ok' }, isError: false })
    expect(s.tools['c1']?.card).toBeUndefined()
  })

  it('★ 生图逐张进度按格子序号累加进 partialImages(乱序到达也各归各格),tool_end 时清掉', () => {
    const img = (tag: string): { mime: 'image/png'; dataRef: string } => ({ mime: 'image/png', dataRef: `data:image/png;base64,${tag}` })
    let s = applyEvent(emptyTranscript(), start)
    s = applyEvent(s, { type: 'tool_progress', callId: 'c1', progress: { callId: 'c1', message: '1/3', image: { index: 2, image: img('CC') } } })
    s = applyEvent(s, { type: 'tool_progress', callId: 'c1', progress: { callId: 'c1', message: '2/3', image: { index: 0, image: img('AA') } } })
    // 不带图的普通进度不能把已到手的图冲掉
    s = applyEvent(s, { type: 'tool_progress', callId: 'c1', progress: { callId: 'c1', message: '2/3' } })
    expect(s.tools['c1']?.partialImages).toEqual({ 0: img('AA'), 2: img('CC') })
    expect(s.tools['c1']?.progress).toBe('2/3')
    s = applyEvent(s, { type: 'tool_end', callId: 'c1', output: { content: 'ok', images: [img('AA'), img('CC')] }, isError: false })
    expect(s.tools['c1']?.partialImages).toBeUndefined()
    expect(s.tools['c1']?.output?.images).toHaveLength(2)
  })

  it('★ 没见过 start 的 tool_end 不能丢 —— 重放裁剪后可能真的只剩 end', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'tool_end',
      callId: 'ghost',
      output: { content: 'x' },
      isError: false
    })
    expect(s.tools['ghost']?.status).toBe('ok')
  })

  it('没见过 start 的 progress 被忽略,不凭空造一条工具记录', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'tool_progress',
      callId: 'ghost',
      progress: { callId: 'ghost', message: 'x' }
    })
    expect(s.tools).toEqual({})
  })
})

describe('applyEvent · 终局与元信息', () => {
  it('run_end 带过来的状态与错误都落到 state 上', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'run_end',
      status: 'error',
      error: { code: 'rate_limit', message: '太快了', retryable: true }
    })
    expect(s.status).toBe('error')
    expect(s.error?.code).toBe('rate_limit')
  })

  it('run_end 保存结束时间，供回合级耗时展示', () => {
    const s = applyEvent(emptyTranscript(), { type: 'run_end', status: 'done', at: 4_200 })
    expect(s.runEndedAt).toBe(4_200)
  })

  it('中断是终局状态之一,且不算 error', () => {
    const s = applyEvent(emptyTranscript(), { type: 'run_end', status: 'aborted' })
    expect(s.status).toBe('aborted')
    expect(s.error).toBeUndefined()
  })

  it('message_start / message_end / context_usage 填元信息', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'message_start', model: 'claude-fable-5-1' } },
      {
        type: 'stream',
        delta: {
          type: 'message_end',
          stopReason: 'end_turn',
          usage: { inputTokens: 10, outputTokens: 20 }
        }
      },
      { type: 'context_usage', used: 30, window: 200_000, shouldCompact: false }
    ])
    expect(s.model).toBe('claude-fable-5-1')
    expect(s.usage?.outputTokens).toBe(20)
    expect(s.contextUsage?.used).toBe(30)
  })

  it('累加本次运行的 API 用量，流式文本与上下文估算不改变 token 数', () => {
    const first: AgentEvent = { type: 'stream', delta: { type: 'message_end', stopReason: 'tool_use',
      usage: { inputTokens: 1135, outputTokens: 144, cacheReadInputTokens: 500, reasoningTokens: 100 } } }
    const before = applyEvent(emptyTranscript(), first)
    const streaming = applyEvents(before, [
      { type: 'stream', delta: { type: 'message_start', model: 'model' } },
      t(0, '很长的输出'.repeat(100)),
      { type: 'context_usage', used: 9999, window: 100000, shouldCompact: false }
    ])
    expect(streaming.usage).toEqual(before.usage)
    const after = applyEvent(streaming, { type: 'stream', delta: { type: 'message_end', stopReason: 'end_turn',
      usage: { inputTokens: 2000, outputTokens: 256, cacheCreationInputTokens: 50 } } })
    expect(after.usage).toEqual({ inputTokens: 3135, outputTokens: 400, cacheReadInputTokens: 500,
      cacheCreationInputTokens: 50, reasoningTokens: 100 })
    expect(before.usage?.outputTokens).toBe(144)
    expect(applyEvent(after, { type: 'run_end', status: 'done' }).usage).toEqual(after.usage)
    expect(applyEvent(emptyTranscript(), first).usage?.outputTokens).toBe(144)
  })

  /**
   * 平均 TPS 的分母。一轮里有几次上游请求就累几段,**工具执行的那段不在内** ——
   * 这正是不拿旁边那个「用时」当分母的原因。
   */
  it('把每次 message_end 报的请求耗时累成这一轮的 upstreamMs', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'message_end', stopReason: 'tool_use',
        usage: { inputTokens: 100, outputTokens: 40 }, latencyMs: 2_000 } },
      // 中间跑了 30 秒工具,一个字节都不该进 upstreamMs
      { type: 'tool_start', callId: 'c1', toolName: 'Bash', input: {}, at: 1_000 },
      { type: 'tool_end', callId: 'c1', output: { content: 'ok' }, isError: false, at: 31_000 },
      { type: 'stream', delta: { type: 'message_end', stopReason: 'end_turn',
        usage: { inputTokens: 200, outputTokens: 60 }, latencyMs: 3_000 } }
    ])
    expect(s.usage).toEqual({ inputTokens: 300, outputTokens: 100, upstreamMs: 5_000 })
  })

  it('上游没报耗时时不留 upstreamMs —— 展示层据此不画 TPS,而不是画个 0', () => {
    const s = applyEvent(emptyTranscript(), { type: 'stream', delta: {
      type: 'message_end', stopReason: 'end_turn', usage: { inputTokens: 1, outputTokens: 2 } } })
    expect(s.usage).not.toHaveProperty('upstreamMs')
  })

  /**
   * 花费的累加。★ 三种状态(字段不在 / null / 有值)各自的含义见 `stream.ts`
   * 里 message_end 上那段表 —— 下面四个用例一一对着它。
   */
  const end = (usage: { inputTokens: number; outputTokens: number },
    cost?: { micros: number; currency: 'USD' | 'CNY' } | null): AgentEvent =>
    ({ type: 'stream', delta: { type: 'message_end', stopReason: 'end_turn', usage,
      ...(cost === undefined ? {} : { cost }) } })

  it('把每次请求的钱累成这一轮的总额', () => {
    const s = applyEvents(emptyTranscript(), [
      end({ inputTokens: 100, outputTokens: 40 }, { micros: 1_200, currency: 'USD' }),
      end({ inputTokens: 200, outputTokens: 60 }, { micros: 3_400, currency: 'USD' })
    ])
    expect(s.usage?.cost).toEqual({ micros: 4_600, currency: 'USD' })
  })

  /*
    ★★ 这一条是整个改动里最要紧的断言。第一次查不到价、第二次查到了,如果只把
    第二次的钱报上去,界面会显示一个偏低、却完全合理的总额 —— 没有人会发现它
    少了一截。所以 null 必须粘住。
  */
  it('有一次算不出价,整轮就锁成算不出,后面再算得出也不回头', () => {
    const s = applyEvents(emptyTranscript(), [
      end({ inputTokens: 100, outputTokens: 40 }, null),
      end({ inputTokens: 200, outputTokens: 60 }, { micros: 3_400, currency: 'USD' })
    ])
    expect(s.usage?.cost).toBeNull()
    // token 照常累加 —— 锁掉的只是钱
    expect(s.usage?.inputTokens).toBe(300)
  })

  it('一轮里混了两种币种也锁成算不出 —— 没有汇率源,加起来是个看着合理的错数', () => {
    const s = applyEvents(emptyTranscript(), [
      end({ inputTokens: 100, outputTokens: 40 }, { micros: 1_200, currency: 'USD' }),
      end({ inputTokens: 200, outputTokens: 60 }, { micros: 9_000, currency: 'CNY' })
    ])
    expect(s.usage?.cost).toBeNull()
  })

  it('事件不带 cost 字段 = 没接计价,保持原样,不留一个 null', () => {
    const s = applyEvents(emptyTranscript(), [
      end({ inputTokens: 100, outputTokens: 40 }, { micros: 1_200, currency: 'USD' }),
      end({ inputTokens: 200, outputTokens: 60 })
    ])
    expect(s.usage?.cost).toEqual({ micros: 1_200, currency: 'USD' })
    expect(applyEvent(emptyTranscript(), end({ inputTokens: 1, outputTokens: 2 })).usage)
      .not.toHaveProperty('cost')
  })

  it('未知/未接管的事件原样返回,不炸也不吞状态', () => {
    const before = applyEvents(emptyTranscript(), [t(0, 'x')])
    const after = applyEvent(before, {
      type: 'interaction_resolved',
      id: 'ghost',
      outcome: { status: 'aborted' }
    })
    expect(after).toEqual(before)
  })

  it('subagent_start 建立可观测状态', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'subagent_start',
      callId: 'c1',
      childRunId: 'r2',
      description: '查找配置读取处',
      subagentType: 'researcher',
      color: 'purple',
      model: 'model-a',
      background: true,
      at: 100
    })

    expect(s.subagents.c1).toEqual({
      callId: 'c1',
      childRunId: 'r2',
      status: 'running',
      description: '查找配置读取处',
      subagentType: 'researcher',
      color: 'purple',
      model: 'model-a',
      background: true,
      phase: 'background',
      toolCalls: 0,
      toolErrors: 0,
      startedAt: 100,
      // 「距上次事件多久」的基准。开始那一刻就是第一次事件
      lastEventAt: 100
    })
  })

  it('subagent_update 累积工具、上下文和 token 用量', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2'
    })
    s = applyEvent(s, {
      type: 'subagent_update',
      callId: 'c1',
      childRunId: 'r2',
      phase: 'tool',
      currentTool: 'read_file',
      toolCalls: 2,
      toolErrors: 1,
      usage: { inputTokens: 10, outputTokens: 4 },
      contextUsage: { used: 100, window: 1000, shouldCompact: false }
    })
    s = applyEvent(s, {
      type: 'subagent_update',
      callId: 'c1',
      childRunId: 'r2',
      usage: { inputTokens: 3, outputTokens: 2, reasoningTokens: 1 }
    })

    expect(s.subagents.c1).toMatchObject({
      phase: 'tool', currentTool: 'read_file', toolCalls: 2, toolErrors: 1,
      contextUsage: { used: 100, window: 1000, shouldCompact: false },
      usage: { inputTokens: 13, outputTokens: 6, reasoningTokens: 1 }
    })

    s = applyEvent(s, {
      type: 'subagent_update',
      callId: 'c1',
      childRunId: 'r2',
      phase: 'thinking',
      currentTool: undefined,
      toolCalls: 2,
      toolErrors: 1
    })
    expect(s.subagents.c1?.currentTool).toBeUndefined()
  })

  it('subagent_end 保存终态、摘要和结束时间', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2', at: 100
    })
    s = applyEvent(s, {
      type: 'subagent_end', callId: 'c1', childRunId: 'r2', status: 'done',
      summary: '配置在 src/config.ts', at: 250
    })

    expect(s.subagents.c1).toMatchObject({
      status: 'done', summary: '配置在 src/config.ts', endedAt: 250, lastEventAt: 250
    })
    expect(s.subagents.c1?.currentTool).toBeUndefined()
  })

  /*
    ★★ 回归护栏。`subagent_end` 曾经硬写 `phase: 'finishing'`,于是**每一张**终态
    卡片都显示「收尾中」—— 跑完的、失败的、被停掉的、卡死了被人掐掉的,一模一样。
    那一栏在终态下等于零信息,而它恰恰是排查时唯一想知道的那一格:它停在哪一步。
  */
  it('subagent_end 不覆盖 phase —— 终态要留住「停在哪一步」', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2', at: 100
    })
    s = applyEvent(s, {
      type: 'subagent_update', callId: 'c1', childRunId: 'r2', phase: 'tool', currentTool: 'Bash'
    })
    s = applyEvent(s, {
      type: 'subagent_end', callId: 'c1', childRunId: 'r2', status: 'aborted', at: 250
    })

    expect(s.subagents.c1?.phase).toBe('tool')
  })

  it('lastEventAt 随每一个子事件推进', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2', at: 100
    })
    expect(s.subagents.c1?.lastEventAt).toBe(100)
    s = applyEvent(s, {
      type: 'subagent_update', callId: 'c1', childRunId: 'r2', phase: 'tool', at: 180
    })
    expect(s.subagents.c1?.lastEventAt).toBe(180)
    s = applyEvent(s, {
      type: 'subagent_update', callId: 'c1', childRunId: 'r2', toolCalls: 3, at: 240
    })
    expect(s.subagents.c1?.lastEventAt).toBe(240)
  })

  it('applyChildEvent 把子 run 的工具和 run_end 投影到 Task 卡片', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2'
    })
    s = applyChildEvent(s, 'r2', {
      type: 'tool_start', callId: 'tc1', toolName: 'read_file', input: {}
    })
    s = applyChildEvent(s, 'r2', {
      type: 'tool_end', callId: 'tc1', output: { content: 'ok' }, isError: true
    })
    s = applyChildEvent(s, 'r2', {
      type: 'run_end', status: 'error', error: { code: 'network', message: '上游连接失败', retryable: true }, at: 500
    })

    expect(s.subagents.c1).toMatchObject({
      toolCalls: 1, toolErrors: 1, status: 'error', endedAt: 500,
      error: { code: 'network', message: '上游连接失败' }
    })
  })

  it('★ 子代理的退避重试投影到卡片上 —— 主对话的状态行看不到它', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2'
    })
    s = applyChildEvent(s, 'r2', {
      type: 'stream',
      delta: { type: 'provider_retry', attempt: 2, delayMs: 8000, reason: 'exceeded rate limit' }
    })
    expect(s.subagents.c1?.notice).toEqual({ kind: 'retry', attempt: 2, reason: 'exceeded rate limit' })

    // 上游开口了 = 这次退避成功了。提示必须消失,否则卡片会一直挂着一句
    // 「正在重试」直到跑完 —— 那比不显示更误导
    s = applyChildEvent(s, 'r2', { type: 'stream', delta: { type: 'message_start', model: 'm' } })
    expect(s.subagents.c1?.notice).toBeUndefined()
    expect(s.subagents.c1?.phase).toBe('thinking')
  })

  it('子代理终态清掉重试提示 —— 错误框里已经把话说全了', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2'
    })
    s = applyChildEvent(s, 'r2', {
      type: 'stream',
      delta: { type: 'provider_switch', from: 'p1', to: 'p2', reason: 'exceeded rate limit' }
    })
    expect(s.subagents.c1?.notice).toEqual({ kind: 'switch', to: 'p2', reason: 'exceeded rate limit' })

    s = applyChildEvent(s, 'r2', {
      type: 'run_end', status: 'error',
      error: { code: 'rate_limit', message: 'exceeded rate limit', retryable: true }, at: 500
    })
    expect(s.subagents.c1?.notice).toBeUndefined()
    expect(s.subagents.c1?.status).toBe('error')
  })

  it('后台子 run 父回合已结束时，从子消息提交补出最终摘要', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2', background: true
    })
    s = applyChildEvent(s, 'r2', {
      type: 'message_commit',
      message: assistantMessage('child-m1', [{ type: 'text', text: '后台调查完成' }], 1)
    })

    expect(s.subagents.c1?.summary).toBe('后台调查完成')
  })

  it('父汇总遥测与继承的同一子事件只计算一次', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'subagent_start', callId: 'c1', childRunId: 'r2'
    })
    // Parent monitor sees child seq=1 first.
    s = applyEvent(s, {
      type: 'subagent_update', callId: 'c1', childRunId: 'r2',
      phase: 'tool', currentTool: 'read_file', toolCalls: 1, childSeq: 1,
      usage: { inputTokens: 10, outputTokens: 2 }
    })
    // The inherited raw child event is the same seq and must be ignored.
    s = applyChildEvent(s, 'r2', {
      type: 'tool_start', callId: 'tc1', toolName: 'read_file', input: {}
    }, 1)
    expect(s.subagents.c1).toMatchObject({ toolCalls: 1, usage: { inputTokens: 10, outputTokens: 2 } })
  })

  it('从已保存的 Task 工具回执重建子 Agent 卡片和入参信息', () => {
    const task = assistantMessage('task-message', [{
      type: 'tool_call',
      callId: 'c1',
      name: 'Task',
      input: { description: '查配置', subagent_type: 'researcher' }
    }], 1)
    const result = toolResultMessage('task-result', [{
      type: 'tool_result',
      callId: 'c1',
      output: { content: '后台已启动' },
      isError: false,
      subagent: { childRunId: 'r2', status: 'done', summary: '后台结果', background: true }
    }], 2)

    expect(subagentsFromMessages([task, result]).c1).toMatchObject({
      childRunId: 'r2', status: 'done', description: '查配置', subagentType: 'researcher',
      summary: '后台结果', background: true
    })
  })

  it('★ 永不原地修改入参 —— zustand 靠引用变化决定重渲染', () => {
    const before = emptyTranscript()
    const after = applyEvent(before, t(0, 'x'))
    expect(before.live).toEqual([])
    expect(after).not.toBe(before)
    expect(after.live).not.toBe(before.live)
  })
})

/**
 * ★ 这一组守的是一个**只在空会话首屏出现**的错:状态行写着「生成中」,
 * 而用户还一个字都没发。
 *
 * 根因是 `emptyTranscript().status === 'running'` 被两种完全不同的处境共用 ——
 * 「run 起来了但第一个事件还没到」和「全新会话」。前者显示生成中是对的,
 * 后者是假的。所以这里每一条都成对出现:同一个转录,`running` 不同,答案就不同。
 */
describe('hasRun · 空会话首屏不该写着「生成中」', () => {
  it('★ 全新会话:status 虽然是 running,但没有 run', () => {
    const s = emptyTranscript()
    expect(s.status).toBe('running') // 前提:初值确实是 running,不然这组测试没意义
    expect(hasRun(s, false)).toBe(false)
  })

  it('★ 同一个空转录,activeRunId 在就是有 run —— attach 上去的那一瞬', () => {
    expect(hasRun(emptyTranscript(), true)).toBe(true)
  })

  it('第一个 delta 一到就有 run,即使此刻 running 还没翻过来', () => {
    const s = applyEvents(emptyTranscript(), [t(0, '你')])
    expect(hasRun(s, false)).toBe(true)
  })

  it('run 结束后转录留着消息,状态行要继续显示「已完成」', () => {
    const s = applyEvent(applyEvents(emptyTranscript(), [t(0, '你好')]), {
      type: 'message_commit',
      message: assistantMessage('m1', [{ type: 'text', text: '你好' }], 1)
    })
    expect(s.live).toEqual([])
    expect(s.messages).toHaveLength(1)
    expect(hasRun(s, false)).toBe(true)
  })
})

/**
 * ★ 这两个事件以前发了没人画,于是「上游繁忙、正在退避重试」和「卡死了」在界面上
 * 长得一模一样:一个转圈,几十秒不动。测的是它们确实到得了状态行,以及**确实是瞬时的** ——
 * 留着不清会让重试成功之后状态行永远挂着一句过期的「正在重试」。
 */
describe('applyEvent · 重试与切换提示', () => {
  it('provider_retry 带着上游原话进状态行', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'stream',
      delta: { type: 'provider_retry', attempt: 2, delayMs: 800, reason: 'Our servers are currently overloaded.' }
    })
    expect(s.notice).toEqual({ kind: 'retry', attempt: 2, reason: 'Our servers are currently overloaded.' })
  })

  it('provider_switch 说清换到了哪一家', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'stream',
      delta: { type: 'provider_switch', from: 'A', to: 'B', reason: '上游不可用' }
    })
    expect(s.notice).toEqual({ kind: 'switch', to: 'B', reason: '上游不可用' })
  })

  it('★ 内容一开始流就清掉 —— 重试成功后不该继续挂着旧提示和错误', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'stream',
      delta: { type: 'message_start', model: 'm' }
    })
    s = applyEvent(s, {
      type: 'stream',
      delta: { type: 'error', error: { code: 'network', message: 'boom', retryable: true } }
    })
    s = applyEvent(s, {
      type: 'stream',
      delta: { type: 'provider_retry', attempt: 1, delayMs: 0, reason: 'boom' }
    })
    expect(s.error?.message).toBe('boom')

    s = applyEvent(s, { type: 'stream', delta: { type: 'message_start', model: 'm' } })
    expect(s.notice).toBeUndefined()
    expect(s.error).toBeUndefined()
  })

  it('★ 出错也清掉 —— 错误框里会写全,状态行不必再挂一句过期的', () => {
    let s = applyEvent(emptyTranscript(), {
      type: 'stream',
      delta: { type: 'provider_retry', attempt: 1, delayMs: 0, reason: 'boom' }
    })
    s = applyEvent(s, {
      type: 'stream',
      delta: { type: 'error', error: { code: 'provider', message: 'boom', retryable: false } }
    })
    expect(s.notice).toBeUndefined()
    expect(s.error?.message).toBe('boom')
  })
})

/**
 * 压缩相关的分支。
 *
 * ★ 它们原先**一条覆盖都没有**,而它们恰好是界面上「压缩看不见」的最后一段接线:
 * 事件到了投影这里若被丢掉,症状是状态行一声不吭地少掉一句,而不是报错。
 *
 * ★ 检查点那一组用例随 `context_checkpoint` 事件一并删除:压缩边界现在是转录里的
 * 一条消息,由 `message_commit` 走常规路径进来,没有独立的事件要测。
 * 「同一段压缩只画一条线」这条不变式也因此不再需要用例去钉 —— 一条消息天然只有一条。
 */
describe('applyEvent · 上下文压缩', () => {
  it('context_status 原样落到 contextStatus', () => {
    const s = applyEvent(emptyTranscript(), {
      type: 'context_status',
      status: { phase: 'compacting', trigger: 'auto' }
    })
    expect(s.contextStatus).toEqual({ phase: 'compacting', trigger: 'auto' })
  })

  it('后一个相位覆盖前一个 —— 它是瞬时状态,不是流水', () => {
    let s = applyEvent(emptyTranscript(), { type: 'context_status', status: { phase: 'compacting' } })
    s = applyEvent(s, { type: 'context_status', status: { phase: 'failed' } })
    expect(s.contextStatus).toEqual({ phase: 'failed' })
  })

  /**
   * ★ 最值钱的一条。`lastInputTokens` 是压缩**之前**那次请求的上游真值(比如 624K);
   * 压完不清掉的话,它要等下一次上游回包才被覆盖 —— 表现为「已压缩」和圆环上
   * 那个爆表的读数同时挂在界面上,而且零报错。
   */
  it('★ compacted 清掉压缩前的 lastInputTokens', () => {
    let s: TranscriptState = { ...emptyTranscript(), lastInputTokens: 624_000 }
    s = applyEvent(s, { type: 'context_status', status: { phase: 'compacted', trigger: 'auto' } })
    expect(s.lastInputTokens).toBeUndefined()
    expect(s.contextStatus).toEqual({ phase: 'compacted', trigger: 'auto' })
  })

  it('其余相位不动 lastInputTokens —— 压缩失败时那一轮仍按原历史发出去了', () => {
    let s: TranscriptState = { ...emptyTranscript(), lastInputTokens: 624_000 }
    s = applyEvent(s, { type: 'context_status', status: { phase: 'failed', trigger: 'auto' } })
    expect(s.lastInputTokens).toBe(624_000)
  })
})

describe('applyEvent · 思考卡片的实时计时', () => {
  it('thinking 块起点取上一条内容事件的 at,终点随每个思考增量前移', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'message_start', model: 'm' }, at: 1_000 },
      { type: 'stream', delta: { type: 'thinking_delta', index: 0, text: '想' }, at: 31_000 },
      { type: 'stream', delta: { type: 'thinking_delta', index: 0, text: '完' }, at: 32_000 }
    ])
    expect(s.live[0]).toMatchObject({ kind: 'thinking', text: '想完', startedAt: 1_000, endedAt: 32_000 })
  })

  it('正文开始后思考块的终点不再前移 —— 否则正文输出期间思考时长还在涨', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'message_start', model: 'm' }, at: 0 },
      { type: 'stream', delta: { type: 'thinking_delta', index: 0, text: '想' }, at: 200 },
      { type: 'stream', delta: { type: 'text_delta', index: 1, text: '答' }, at: 900 },
      { type: 'stream', delta: { type: 'text_delta', index: 1, text: '案' }, at: 1_500 }
    ])
    expect(s.live[0]).toMatchObject({ kind: 'thinking', startedAt: 0, endedAt: 200 })
    expect(s.live[1]).not.toHaveProperty('startedAt')
  })

  it('续跑的 message_start 重置起点,不把被丢弃那次尝试的时间算进去', () => {
    const s = applyEvents(emptyTranscript(), [
      { type: 'stream', delta: { type: 'message_start', model: 'm' }, at: 0 },
      { type: 'stream', delta: { type: 'text_delta', index: 0, text: '断' }, at: 100 },
      { type: 'stream', delta: { type: 'message_start', model: 'm' }, at: 5_000 },
      { type: 'stream', delta: { type: 'thinking_delta', index: 0, text: '重想' }, at: 6_000 }
    ])
    expect(s.live[0]).toMatchObject({ kind: 'thinking', startedAt: 5_000, endedAt: 6_000 })
  })
})
