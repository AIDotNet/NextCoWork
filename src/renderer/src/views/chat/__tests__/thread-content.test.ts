import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { describe, expect, it, vi } from 'vitest'
import type { AgentMessage } from '../../../../../shared/agent/message'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../../shared/agent/message'
import { emptyTranscript, toolsFromMessages, type TranscriptState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { Thread } from '../Thread'
import type { CompactBoundary } from '../../../../../shared/agent/compaction'
import {
  assistantSegments,
  assistantText,
  isAssistantTextBlock,
  lastTurnIndex,
  promptOf,
  threadRows,
  type ThreadRow
} from '../thread-content'

describe('thread content grouping', () => {
  it('joins consecutive assistant tool messages across hidden tool receipts', () => {
    const messages = [
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'read', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'read', output: { content: 'ok' }, isError: false }], 3),
      assistantMessage('a2', [{ type: 'tool_call', callId: 'bash', name: 'Bash', input: {} }], 4),
      toolResultMessage('r2', [{ type: 'tool_result', callId: 'bash', output: { content: 'ok' }, isError: false }], 5),
      assistantMessage('a3', [{ type: 'text', text: 'Done' }], 6)
    ]
    const rows = threadRows(messages, [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant'])
    expect(rows[1]?.kind === 'assistant' ? rows[1].blocks.length : 0).toBe(3)
  })

  /**
   * ★★ 后台子代理的结果回传是一条 `internal` 消息 —— 它以前在界面上**整条不存在**。
   * 于是主代理毫无来由地开口,讲一件几百轮之前派出去的事,而用户看不到任何输入。
   *
   * 认它的标记是那个 `subagent` part:两个上游编码器都把它丢掉,所以拿它当
   * 界面标记不会多给模型一个字。
   */
  it('★★ 后台汇报单独成一行,而不是整条消息消失', () => {
    const report = {
      ...userMessage('rep', [
        { type: 'text' as const, text: 'Background subagent result (code-analyst, run-9):\n\n查完了' },
        { type: 'subagent' as const, callId: 'task-1', childRunId: 'run-9', summary: '查完了' }
      ], 3),
      internal: true
    }
    const rows = threadRows([
      userMessage('u', [{ type: 'text', text: '开始' }], 1),
      assistantMessage('a1', [{ type: 'text', text: '好' }], 2),
      report,
      assistantMessage('a2', [{ type: 'text', text: '收到后台结果' }], 4)
    ], [], false)

    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant', 'subagent-report', 'assistant'])
    const line = rows[2]
    expect(line?.kind === 'subagent-report' ? line.callId : undefined).toBe('task-1')
    expect(line?.kind === 'subagent-report' ? line.summary : undefined).toBe('查完了')
  })

  /**
   * ★ 汇报行**推进 `preceding`**,和可见消息一样。不推进的话,它前后两个
   * assistant 行会共用同一个 `reply:${preceding}` key(`assistant()` 只看
   * `rows.at(-1)`,隔着一行就不复用了)—— 同 key 两行,React 会把两轮的内容搅在一起。
   */
  it('★ 汇报行两侧的 assistant 行 key 不相同', () => {
    const report = {
      ...userMessage('rep', [
        { type: 'text' as const, text: 'report' },
        { type: 'subagent' as const, callId: 'task-1', childRunId: 'run-9' }
      ], 3),
      internal: true
    }
    const rows = threadRows([
      userMessage('u', [{ type: 'text', text: '开始' }], 1),
      assistantMessage('a1', [{ type: 'text', text: '好' }], 2),
      report,
      assistantMessage('a2', [{ type: 'text', text: '收到' }], 4)
    ], [], false)
    const keys = rows.filter((row) => row.kind === 'assistant').map((row) => row.key)
    expect(new Set(keys).size).toBe(keys.length)
  })

  /*
    需求:后台汇报引出的回复前面没有提问,删除键以前因为拿不到提问而整颗消失。
    删除的跨度改由行自己给出 —— 跨过工具回执,但不越过汇报。
  */
  it('assistant 行带上自己的消息跨度,汇报之后的那一行也有', () => {
    const report = {
      ...userMessage('rep', [
        { type: 'text' as const, text: 'report' },
        { type: 'subagent' as const, callId: 'task-1', childRunId: 'run-9' }
      ], 5),
      internal: true
    }
    const rows = threadRows([
      userMessage('u', [{ type: 'text', text: '开始' }], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'c1', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'c1', output: { content: 'ok' }, isError: false }], 3),
      assistantMessage('a2', [{ type: 'text', text: '好' }], 4),
      report,
      assistantMessage('a3', [{ type: 'text', text: '收到' }], 6)
    ], [], false)
    const spans = rows.flatMap((row) => row.kind === 'assistant' ? [row.span] : [])
    expect(spans).toEqual([{ from: 'a1', to: 'a2' }, { from: 'a3', to: 'a3' }])
  })

  /*
    计划回执行:一次审批落槌的记录。以前它不是「一行」,而是整段对话之后
    无条件追加的一张卡 —— 所以永远贴在输入框上面,关不掉。
  */
  const planProposal = (id: string, callId: string, at: number) =>
    assistantMessage(id, [{ type: 'tool_call', callId, name: 'PlanFile', input: {} }], at)
  const planReceipt = (id: string, callId: string, planId: string, action: string, at: number) =>
    toolResultMessage(id, [{
      type: 'tool_result',
      callId,
      output: { content: `${JSON.stringify({ type: 'plan_file', planId, path: `.plan/${planId}.md`, action })}\n写好了` },
      isError: false
    }], at)

  it('★★ 计划回执停在提出计划的那一轮,而不是整段末尾', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '做个计划' }], 1),
      planProposal('a1', 'plan-1', 2),
      planReceipt('r1', 'plan-1', 'p1', 'approve_current', 3),
      userMessage('u2', [{ type: 'text', text: '实施已批准的计划。' }], 4),
      assistantMessage('a2', [{ type: 'text', text: '开工' }], 5)
    ], [], false)

    expect(rows.map((row) => row.kind))
      .toEqual(['user', 'assistant', 'plan-receipt', 'user', 'assistant'])
    const line = rows[2]
    expect(line?.kind === 'plan-receipt' ? line.receipt.planId : undefined).toBe('p1')
  })

  /**
   * ★★ **这是「卡片还是贴在底部」的真实成因。** 点「在当前会话执行」时渲染进程
   * 当场就发出了新一轮的提问,而计划工具那条结果还在主进程里往库里写 —— 落盘
   * 顺序反过来,回执就排到了新一轮**后面**。锚在提出计划那条消息上就不受影响。
   */
  it('★★ 回执比新一轮的提问晚落盘时,卡片仍停在提出计划的那一轮', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '做个计划' }], 1),
      planProposal('a1', 'plan-1', 2),
      userMessage('u2', [{ type: 'text', text: '实施已批准的计划。' }], 3),
      planReceipt('r1', 'plan-1', 'p1', 'approve_current', 4),
      assistantMessage('a2', [{ type: 'text', text: '开工' }], 5)
    ], [], false)

    expect(rows.map((row) => row.kind))
      .toEqual(['user', 'assistant', 'plan-receipt', 'user', 'assistant'])
  })

  /** 老转录里找不到发起那条消息,就退回装着回执的那条 —— 位置不理想,总比没有强。 */
  it('找不到发起消息时退回回执自己的位置', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '做个计划' }], 1),
      assistantMessage('a1', [{ type: 'text', text: '计划如下' }], 2),
      planReceipt('r1', 'missing-call', 'p1', 'approve_current', 3),
      assistantMessage('a2', [{ type: 'text', text: '开工' }], 4)
    ], [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant', 'plan-receipt', 'assistant'])
  })

  /** 同一个计划先「要求修改」后「已批准」,只留最后一条 —— 中间那次已被推翻。 */
  it('★ 同一个计划只留最后一条回执', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '做个计划' }], 1),
      planProposal('a1', 'plan-1', 2),
      planReceipt('r1', 'plan-1', 'p1', 'request_revision', 3),
      planProposal('a2', 'plan-2', 4),
      planReceipt('r2', 'plan-2', 'p1', 'approve_current', 5),
      assistantMessage('a3', [{ type: 'text', text: '开工' }], 6)
    ], [], false)

    const receipts = rows.filter((row) => row.kind === 'plan-receipt')
    expect(receipts).toHaveLength(1)
    expect(receipts[0]?.kind === 'plan-receipt' ? receipts[0].receipt.action : undefined).toBe('approve_current')
    // 留下的那条锚在**第二次**提计划上;被丢掉的那条不再切开前后两段回复。
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant', 'plan-receipt', 'assistant'])
  })

  /** 两个不同的计划各留各的,互不顶替。 */
  it('不同计划的回执各自成行', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '做计划' }], 1),
      planProposal('a1', 'plan-1', 2),
      planReceipt('r1', 'plan-1', 'p1', 'approve_current', 3),
      planProposal('a2', 'plan-2', 4),
      planReceipt('r2', 'plan-2', 'p2', 'reject', 5),
      assistantMessage('a3', [{ type: 'text', text: '好' }], 6)
    ], [], false)
    expect(rows.filter((row) => row.kind === 'plan-receipt')).toHaveLength(2)
  })

  /** 和汇报行同理:不推进 `preceding` 的话,两侧 assistant 行会共用一个 key。 */
  it('★ 计划回执行两侧的 assistant 行 key 不相同', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '做个计划' }], 1),
      planProposal('a1', 'plan-1', 2),
      planReceipt('r1', 'plan-1', 'p1', 'approve_current', 3),
      assistantMessage('a2', [{ type: 'text', text: '执行' }], 4)
    ], [], false)
    const keys = rows.filter((row) => row.kind === 'assistant').map((row) => row.key)
    expect(new Set(keys).size).toBe(keys.length)
  })

  /** 别的工具输出压根不是 JSON,不该被当成回执。 */
  it('非计划的工具结果不产生回执行', () => {
    const rows = threadRows([
      userMessage('u1', [{ type: 'text', text: '看看' }], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'read', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'read', output: { content: 'plain text' }, isError: false }], 3),
      assistantMessage('a2', [{ type: 'text', text: '好' }], 4)
    ], [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant'])
  })

  /** 没有 `subagent` part 的 internal 消息照旧整条隐藏 —— 那些是纯协调消息 */
  it('普通 internal 消息仍然不出现在消息流里', () => {
    const rows = threadRows([
      userMessage('u', [{ type: 'text', text: '开始' }], 1),
      { ...userMessage('sys', [{ type: 'text', text: '内部协调' }], 2), internal: true },
      assistantMessage('a1', [{ type: 'text', text: '好' }], 3)
    ], [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant'])
  })

  it('tags a merged assistant turn with the run that produced it', () => {
    const messages = [
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'read', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'read', output: { content: 'ok' }, isError: false }], 3),
      assistantMessage('a2', [{ type: 'text', text: 'Done' }], 4)
    ]
    // The whole turn belongs to one run, so either committed message can carry it.
    const rows = threadRows(messages, [], false, { a1: 'run-1', a2: 'run-1' })
    expect(rows[1]?.kind === 'assistant' ? rows[1].runId : undefined).toBe('run-1')
  })

  it('carries a run recorded only on the tail of a partially migrated turn', () => {
    const messages = [
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a1', [{ type: 'tool_call', callId: 'read', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'read', output: { content: 'ok' }, isError: false }], 3),
      assistantMessage('a2', [{ type: 'text', text: 'Done' }], 4)
    ]
    expect(threadRows(messages, [], false, { a2: 'run-1' })[1])
      .toMatchObject({ kind: 'assistant', runId: 'run-1' })
  })

  it('leaves a turn from before the migration without a run', () => {
    const messages = [
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a', [{ type: 'text', text: 'Done' }], 2)
    ]
    // Undefined means "unknown", which renders no usage at all — a placeholder
    // run id would instead render a confident, wrong zero.
    const rows = threadRows(messages, [], false)
    expect(rows[1]?.kind === 'assistant' ? rows[1].runId : 'set').toBeUndefined()
  })

  it('keeps the live reply keys aligned with the eventual committed parts', () => {
    const user = userMessage('u', [{ type: 'text', text: 'Inspect' }], 1)
    const live = threadRows([user], [{ index: 0, kind: 'text', text: 'Working' }], true)
    const committed = threadRows([user, assistantMessage('a', [{ type: 'text', text: 'Working' }], 2)], [], false)
    const liveBlock = live[1]?.kind === 'assistant' ? live[1].blocks[0] : undefined
    const committedBlock = committed[1]?.kind === 'assistant' ? committed[1].blocks[0] : undefined
    expect(liveBlock?.key).toBe(committedBlock?.key)
  })

  it('marks thinking as finished once a later text block starts streaming', () => {
    const user = userMessage('u', [{ type: 'text', text: 'Inspect' }], 1)
    const rows = threadRows([user], [
      { index: 0, kind: 'thinking', text: 'Reasoning' },
      { index: 1, kind: 'text', text: 'Inspecting repository...' }
    ], true)
    const row = rows[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return

    expect(row.blocks[0]?.streaming).toBe(false)
    expect(row.blocks[1]?.streaming).toBe(true)
    expect(assistantSegments(row.blocks, 'tool')[0]).toMatchObject({
      kind: 'process',
      items: [{ kind: 'thinking', streaming: false }]
    })
  })

  it('keeps a user-to-answer time range for historical duration fallback', () => {
    const rows = threadRows([
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1_000),
      assistantMessage('a', [{ type: 'text', text: 'Done' }], 4_500)
    ], [], false)
    const answer = rows[1]
    expect(answer?.kind).toBe('assistant')
    if (answer?.kind !== 'assistant') return
    expect(answer.startedAt).toBe(1_000)
    expect(answer.endedAt).toBe(4_500)
  })

  it('does not render tool receipts as user content and preserves prose order', () => {
    const user = userMessage('u', [{ type: 'text', text: 'Inspect' }], 1)
    const blocks = threadRows([user, assistantMessage('a', [
      { type: 'tool_call', callId: 'read', name: 'Read', input: {} },
      { type: 'text', text: 'Done' }
    ], 2)], [], false)[1]
    expect(blocks?.kind).toBe('assistant')
    if (blocks?.kind !== 'assistant') return
    const segments = assistantSegments(blocks.blocks, 'tool')
    expect(segments.map((segment) => segment.kind)).toEqual(['process', 'block'])
  })

  /**
   * Task 的参数还在流的那几秒:卡片必须已经是子代理卡,而不是一张过会儿
   * 会被整个换掉的通用工具卡。`pending` 是它和「旧转录里那些没有 state 的
   * 子代理块」的唯一区别 —— 后者早就跑完了。
   */
  it('renders a streaming Task as a pending subagent node, not a generic tool card', () => {
    const user = userMessage('u', [{ type: 'text', text: 'Inspect' }], 1)
    const rows = threadRows([user], [
      { index: 0, kind: 'tool_use', callId: 'call-task', name: 'Task', text: '{"description":"查调用点","subagent' }
    ], true)
    const row = rows[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return
    expect(assistantSegments(row.blocks, 'tool')[0]).toMatchObject({
      kind: 'process',
      items: [{ kind: 'subagent', callId: 'call-task', summary: '查调用点', pending: true }]
    })
  })

  it('recognizes the final non-empty assistant text block', () => {    const row = threadRows([
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a', [{ type: 'text', text: 'Done' }], 2)
    ], [], false)[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return
    expect(isAssistantTextBlock(row.blocks[0]!)).toBe(true)
  })

  it('does not treat a thinking block as the final answer', () => {
    const row = threadRows([
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a', [{ type: 'thinking', text: 'Working' }], 2)
    ], [], false)[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return
    expect(isAssistantTextBlock(row.blocks[0]!)).toBe(false)
  })
})

describe('assistant turn plain text', () => {
  it('copies prose only, leaving thinking and tool calls out', () => {
    const row = threadRows([
      userMessage('u', [{ type: 'text', text: 'Inspect' }], 1),
      assistantMessage('a', [
        { type: 'thinking', text: 'Let me look around first' },
        { type: 'text', text: 'Checked the config.' },
        { type: 'tool_call', callId: 'read', name: 'Read', input: {} }
      ], 2),
      toolResultMessage('r', [{ type: 'tool_result', callId: 'read', output: { content: 'ok' }, isError: false }], 3),
      assistantMessage('a2', [{ type: 'text', text: 'It is fine.' }], 4)
    ], [], false)[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return
    expect(assistantText(row.blocks)).toBe('Checked the config.\n\nIt is fine.')
  })

  it('reads a still-streaming reply', () => {
    const row = threadRows([userMessage('u', [{ type: 'text', text: 'Hi' }], 1)],
      [{ index: 0, kind: 'text', text: 'Half a sen' }], true)[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return
    expect(assistantText(row.blocks)).toBe('Half a sen')
  })

  it('is empty for a turn that only ran tools', () => {
    const row = threadRows([
      userMessage('u', [{ type: 'text', text: 'Run it' }], 1),
      assistantMessage('a', [{ type: 'tool_call', callId: 'bash', name: 'Bash', input: {} }], 2)
    ], [], false)[1]
    expect(row?.kind).toBe('assistant')
    if (row?.kind !== 'assistant') return
    expect(assistantText(row.blocks)).toBe('')
  })
})

describe('completed turn rendering', () => {
  // 需求：250 回合的 DOM 压力样本不能因全套测试的 worker 争用误报超时；样本规模保持不变。
  it('retains the process subtree and user expansion when another turn starts or is removed', async () => {
    const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
    Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
    vi.stubGlobal('window', dom.window)
    vi.stubGlobal('document', dom.window.document)
    vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
    const container = document.getElementById('root')!
    const root = createRoot(container)
    const messages = [
      userMessage('first-user', [{ type: 'text', text: 'Inspect' }], 1),
      ...Array.from({ length: 250 }, (_, index) => [
        assistantMessage(`step-${index}`, [
          { type: 'text', text: `Intermediate ${index}` },
          { type: 'tool_call', callId: `call-${index}`, name: 'Read', input: {} }
        ], index * 2 + 2),
        toolResultMessage(`result-${index}`, [{ type: 'tool_result', callId: `call-${index}`, output: { content: 'ok' }, isError: false }], index * 2 + 3)
      ]).flat(),
      assistantMessage('answer', [{ type: 'text', text: 'Final answer' }], 1000)
    ]
    const transcript = { ...emptyTranscript(), messages, tools: toolsFromMessages(messages), status: 'done' as const }
    const render = async (current: TranscriptState, runId: string | null = null): Promise<void> => {
      await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
        createElement(Thread, { transcript: current, runId, lastSeq: 0, queued: 0, model: undefined, providerName: undefined }) })))
    }
    try {
      await render(transcript)
      const process = container.querySelector('[data-testid="run-process-block"]')!
      const answer = [...container.querySelectorAll('p')].find((element) => element.textContent === 'Final answer')
      expect(process.getAttribute('data-open')).toBe('false')
      expect(container.textContent).not.toContain('Intermediate')
      const next = { ...transcript, messages: [...messages, userMessage('next-user', [{ type: 'text', text: 'Continue' }], 1001)], status: 'running' as const }
      await render(next, 'next-run')
      expect(container.querySelector('[data-testid="run-process-block"]')).toBe(process)
      expect(container.textContent).not.toContain('Intermediate')
      expect([...container.querySelectorAll('p')].find((element) => element.textContent === 'Final answer')).toBe(answer)
      await act(async () => (process.querySelector('button') as HTMLButtonElement).click())
      const intermediate = [...container.querySelectorAll('p')].find((element) => element.textContent === 'Intermediate 0')
      expect(intermediate).toBeDefined()
      await render({ ...next, status: 'error' })
      await render(transcript)
      expect(container.querySelector('[data-testid="run-process-block"]')).toBe(process)
      expect(process.getAttribute('data-open')).toBe('true')
      expect([...container.querySelectorAll('p')].find((element) => element.textContent === 'Intermediate 0')).toBe(intermediate)
      for (const status of ['error', 'aborted'] as const) {
        const failedMessages = [
          userMessage(`${status}-user`, [{ type: 'text', text: 'Inspect' }], 1),
          ...messages.slice(1, 3),
          messages.at(-1)!
        ]
        await render({ ...transcript, messages: failedMessages, status })
        expect(container.querySelector('[data-testid="run-process-block"]')).toBeNull()
        await render({ ...transcript, messages: [
          ...failedMessages, userMessage(`${status}-retry`, [{ type: 'text', text: 'Retry' }], 1001)
        ], status: 'running' }, `${status}-retry-run`)
        expect(container.querySelector('[data-testid="run-process-block"]')).toBeNull()
        expect(container.textContent).toContain('Intermediate 0')
      }
    } finally {
      await act(async () => root.unmount())
      dom.window.close()
      vi.unstubAllGlobals()
    }
  }, 15_000)
})

/**
 * 压缩分隔线的插入规则。
 *
 * 需求:压缩边界现在是转录里的一条 `internal` user 消息(带 `compact_boundary` 块),
 * 不再是另一张表里的检查点。所以这一组钉的是「**一条消息**怎么变成一条线」:
 * 它自己不占一行、不推进 key、不计进折叠数。
 *
 * ★ 这一组里有两条「破了也不报错」的:`promptOf` 拿到分隔线会让最新一轮的
 * 「重新生成 / 删除这一轮」**无声消失**;`lastTurnIndex` 退回按数组末尾比,
 * 手动压缩后状态行整块不渲染。两者都不抛异常,只能靠断言钉住。
 */
describe('compaction dividers', () => {
  const boundary = (id: string, over: Partial<CompactBoundary> = {}): AgentMessage => ({
    ...userMessage(id, [
      { type: 'compact_boundary', trigger: 'auto', preTokens: 200_000, postTokens: 8_000, summary: '## 意图\n…', ...over },
      { type: 'text', text: '这是之前对话的摘要…' }
    ], 3),
    internal: true
  })

  const user = userMessage('u1', [{ type: 'text', text: 'Inspect' }], 1)
  const answer = assistantMessage('a1', [{ type: 'text', text: 'Done' }], 2)

  /** ★ 本组最值钱的一条:自动压缩最常见的位置就是刚发出的那条提问之后 */
  it('★ 边界在提问之后时线落在提问与回答之间,且提问仍查得到', () => {
    const rows = threadRows([user, boundary('c1'), answer], [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'divider', 'assistant'])
    // 隔着一条分隔线,`rows[index - 1]` 拿到的是线 —— 必须向上跳过去
    expect(promptOf(rows, 2)?.id).toBe('u1')
  })

  /** ★ 手动压缩的边界就是整段最后一条,那时分隔线是数组末尾 */
  it('★ 线落在末尾时,末轮仍是那个助手回合,且不补空回合', () => {
    const rows = threadRows([user, answer, boundary('c1', { trigger: 'manual' })], [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant', 'divider'])
    expect(lastTurnIndex(rows)).toBe(1)
  })

  /**
   * ★ 边界常常落在一轮**内部**(工具循环里到阈值)。它是 internal 消息,
   * 跟着可见性过滤一起跳过去的话,「工具回合之间压缩」那条线一条都画不出来,
   * 而且不报错:模型那边确实只拿到了边界之后的内容,界面却看不出这里断过。
   *
   * ★ **线切在边界上,哪怕这把刀落在一轮内部。** 这里曾经是「一轮绝不切开、
   * 线推迟到轮末」—— 而工具循环里根本没有轮末:一整个会话可以是 1 条提问 +
   * 50 条工具回执,线于是一路挂到整段最底部,压在状态行下面不动窝。
   */
  it('★ 边界落在一轮内部时把那一轮切成两行', () => {
    const messages = [
      user,
      assistantMessage('a1', [{ type: 'tool_call', callId: 'r', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'r', output: { content: 'ok' }, isError: false }], 3),
      boundary('c1'),
      assistantMessage('a2', [{ type: 'text', text: 'Done' }], 4)
    ]
    const rows = threadRows(messages, [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'assistant', 'divider', 'assistant'])
    // 线**上方**只留压缩覆盖到的那部分,下方是压缩之后才发生的事
    expect(rows[1]?.kind === 'assistant' ? rows[1].blocks.length : 0).toBe(1)
    expect(rows[3]?.kind === 'assistant' ? rows[3].blocks.length : 0).toBe(1)
    // 切开之后「重新生成 / 删除这一轮」仍要找得回那条提问
    expect(promptOf(rows, 3)?.id).toBe('u1')
  })

  /**
   * ★ **切开不等于重挂 —— 这是选「切开」而不是「推迟」的前提。**
   *
   * 行 key 取的是创建那一瞬的 `preceding`,而工具回执和边界消息都不推进它:
   * 所以线下方那一行从流式创建到提交拿的是同一个值,key 一个字节不变。
   * 变了就是整棵子树重挂:markdown 重渲、工具组展开状态丢失、滚动跳一下。
   */
  it('★ 线下方那一行的 key 在提交前后不变', () => {
    const head = [
      user,
      assistantMessage('a1', [{ type: 'tool_call', callId: 'r', name: 'Read', input: {} }], 2),
      toolResultMessage('r1', [{ type: 'tool_result', callId: 'r', output: { content: 'ok' }, isError: false }], 3),
      boundary('c1')
    ]
    const live = threadRows(head, [{ index: 0, kind: 'text', text: 'Done' }], true)
    const committed = threadRows(
      [...head, assistantMessage('a2', [{ type: 'text', text: 'Done' }], 4)], [], false
    )
    expect(live.map((row) => row.kind)).toEqual(['user', 'assistant', 'divider', 'assistant'])
    expect(live.at(-1)?.key).toBe(committed.at(-1)?.key)
    // 块 key 也要对得上,否则重挂的是块而不是行 —— 症状一样
    const blockKeys = (row: ThreadRow | undefined): string[] =>
      row?.kind === 'assistant' ? row.blocks.map((b) => b.key) : []
    expect(blockKeys(live.at(-1))).toEqual(blockKeys(committed.at(-1)))
  })

  it('两次压缩各画一条,按消息顺序', () => {
    const rows = threadRows([user, boundary('c1'), answer, boundary('c2')], [], false)
    expect(rows.map((row) => row.kind)).toEqual(['user', 'divider', 'assistant', 'divider'])
    expect(rows.flatMap((row) => (row.kind === 'divider' ? [row.key] : []))).toEqual(['compaction:c1', 'compaction:c2'])
  })

  it('foldedCount 从上一条分隔线起算,不是从头数', () => {
    const messages = [
      userMessage('u1', [{ type: 'text', text: 'a' }], 1),
      assistantMessage('a1', [{ type: 'text', text: 'b' }], 2),
      boundary('c1'),
      userMessage('u2', [{ type: 'text', text: 'c' }], 3),
      assistantMessage('a2', [{ type: 'text', text: 'd' }], 4),
      boundary('c2')
    ]
    const folded = threadRows(messages, [], false)
      .flatMap((row) => (row.kind === 'divider' ? [row.foldedCount] : []))
    expect(folded).toEqual([2, 2])
  })

  /** 边界块原样交给分隔线 —— 摘要、重附文件、前后读数都住在它上面 */
  it('分隔行带着边界块本身,不是它的一份拷贝', () => {
    const message = boundary('c1', { trigger: 'manual', restoredFiles: ['src/a.ts'] })
    const row = threadRows([user, message], [], false).find((r) => r.kind === 'divider')
    expect(row?.kind === 'divider' ? row.boundary.trigger : undefined).toBe('manual')
    expect(row?.kind === 'divider' ? row.boundary.restoredFiles : undefined).toEqual(['src/a.ts'])
    expect(row?.kind === 'divider' ? row.messageId : undefined).toBe('c1')
  })

  /** 没有边界时产出必须与加这个功能之前逐字节相同 */
  it('没有压缩边界时不产出任何分隔线', () => {
    expect(threadRows([user, answer], [], false).map((row) => row.kind)).toEqual(['user', 'assistant'])
  })
})

/**
 * 两条 DOM 级回归 —— 单测层面的 rows 抓不到「按钮无声消失」。
 * 分隔线插进消息流之后,`promptOf` 和 `isLast` 的两处静默回归都表现为
 * **某块 UI 不再渲染**,而 rows 本身看起来完全正常。
 */
describe('compaction dividers · DOM', () => {
  async function renderThread(messages: AgentMessage[]): Promise<{
    container: HTMLElement
    cleanup: () => Promise<void>
  }> {
    const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
    Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
    vi.stubGlobal('window', dom.window)
    vi.stubGlobal('document', dom.window.document)
    vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('requestAnimationFrame', () => 1)
    vi.stubGlobal('cancelAnimationFrame', () => {})
    vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
    const container = document.getElementById('root')!
    const transcript = {
      ...emptyTranscript(),
      messages,
      tools: toolsFromMessages(messages),
      status: 'done' as const
    }
    const root = createRoot(container)
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
      createElement(Thread, { transcript, runId: null, lastSeq: 0, queued: 0, model: undefined, providerName: undefined }) })))
    return {
      container,
      cleanup: async () => {
        await act(async () => root.unmount())
        dom.window.close()
        vi.unstubAllGlobals()
      }
    }
  }

  const ask = userMessage('u1', [{ type: 'text', text: 'Inspect' }], 1)
  const reply = assistantMessage('a1', [{ type: 'text', text: 'Done' }], 2)
  const line = (id: string): AgentMessage => ({
    ...userMessage(id, [
      { type: 'compact_boundary', trigger: 'auto', preTokens: 200_000, postTokens: 8_000, summary: 'summary' },
      { type: 'text', text: 'summary' }
    ], 3),
    internal: true
  })

  it('★ 线插在提问与回答之间时,「重新生成」仍在', async () => {
    const { container, cleanup } = await renderThread([ask, line('c1'), reply])
    try {
      expect(container.querySelector('[data-testid="compaction-divider"]')).not.toBeNull()
      expect(container.querySelector('[data-testid="turn-regenerate"]')).not.toBeNull()
    } finally {
      await cleanup()
    }
  })

  it('★ 线落在整段末尾时,状态行那一块仍在', async () => {
    const { container, cleanup } = await renderThread([ask, reply, line('c1')])
    try {
      expect(container.querySelector('[data-testid="compaction-divider"]')).not.toBeNull()
      expect(container.querySelector('[data-testid="assistant-feedback"]')).not.toBeNull()
    } finally {
      await cleanup()
    }
  })
})
