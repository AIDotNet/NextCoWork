import { describe, expect, it, vi } from 'vitest'
import type { AgentEvent } from '../../../../shared/agent/event'
import type { AgentMessage } from '../../../../shared/agent/message'
import { orphanedToolCalls, visibleText } from '../../../../shared/agent/message'
import type { RunRequest } from '../../../../shared/agent/run-request'
import type { ModelAlias, UpstreamProtocol } from '../../../../shared/domain/provider'
import type { UnpricedUsageAttempt } from '../../../../shared/domain/usage'
import { applyEvents, emptyTranscript } from '../../../../shared/agent/transcript'
import { toolOk } from '../../../../shared/agent/tool'
import { AgentSession } from '../../agent-session'
import { nodeHost } from '../../host'
import { InteractionGate } from '../../interaction-gate'
import { RunHandle } from '../../run-registry'
import { ToolRegistry } from '../../tool/registry'
import { askUserTool } from '../../tool/builtin/interaction'
import { UpstreamRouter } from '../router'
import { chunk, functionItem, messageItem, reasoningItem, responseDone, sse } from './openai-fixtures'

const protocols = ['openai-chat', 'openai-responses'] as const
function callResponse(protocol: UpstreamProtocol, name = 'Echo', args = '{"text":"hello"}'): Response {
  return protocol === 'openai-chat' ? sse(
    chunk({ reasoning_content: '检查参数。' }), chunk({ tool_calls: [{ index: 0, id: 'call-1', type: 'function', function: { name, arguments: args } }] }),
    chunk({}, 'tool_calls'), { choices: [], usage: { prompt_tokens: 100, completion_tokens: 30,
      prompt_cache_hit_tokens: 60, completion_tokens_details: { reasoning_tokens: 20 } } }, '[DONE]'
  ) : sse(responseDone([reasoningItem, { ...functionItem, name, arguments: args }]))
}
function finalResponse(protocol: UpstreamProtocol): Response {
  return protocol === 'openai-chat' ? sse(chunk({ content: '完成' }, 'stop'), '[DONE]') : sse(responseDone([messageItem]))
}

/**
 * ★ `resumeDelaysMs` 缺省 `[]` = **关掉断流自动续跑**。
 *
 * 真实缺省是 `RESUME_DELAYS_MS`(1s/2s/5s/15s/45s),这份文件跑的是真 router + 真
 * transport,`fetch` 的脚本一用完就抛「Unexpected request」—— 那是个网络错误,
 * 于是每一条脚本不够长的用例都会凭空多睡 68 秒然后超时。续跑只在明确要测它的
 * 那两条用例里打开。
 */
function rig(protocol: typeof protocols[number], responses: Response[], resumeDelaysMs: readonly number[] = []) {
  const request: RunRequest = { runId: 'r1', sessionId: 's1', workspaceId: 'w1', depth: 0, input: [{ type: 'text', text: '开始' }],
    mode: 'normal', thinking: 'high', webSearch: false, permissionMode: 'ask', model: 'test', skillIds: [] }
  const alias: ModelAlias = { alias: 'test', providerId: 'p', upstreamModel: protocol === 'openai-chat' ? 'deepseek-test' : 'gpt-test',
    contextWindow: 200000, maxOutputTokens: 8192, capabilities: { tools: true, thinking: true, caching: true, vision: true },
    thinkingConfig: { mode: 'effort', defaultEnabled: true, defaultEffort: 'high' } }
  const bodies: Array<Record<string, unknown>> = []
  const urls: string[] = []
  const ledger: UnpricedUsageAttempt[] = []
  const host = nodeHost({ fetch: vi.fn(async (url, init) => {
    urls.push(String(url))
    bodies.push(JSON.parse(String(init?.body)))
    const response = responses.shift()
    if (response === undefined) throw new Error('Unexpected request')
    return response
  }), logger: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() } })
  const ready = host.secrets.set('key', 'local-test-key')
  const router = new UpstreamRouter(host, { providers: () => [{ id: 'p', name: 'p', protocol, baseUrl: 'https://upstream.test/api/v3', credentialRef: 'key', priority: 0, enabled: true }], aliases: () => [alias], failoverEnabled: () => false }, { baseDelayMs: 0, onUsageAttempt: (entry) => ledger.push(entry) })
  const tools = new ToolRegistry()
  const execute = vi.fn(async (input) => toolOk(JSON.stringify(input)))
  tools.register({ internalId: 'Echo', description: 'Test tool', inputSchema: { type: 'object' }, readOnly: false, destructive: true, needsNetwork: false, source: { kind: 'builtin' }, execute })
  tools.register(askUserTool)
  const gate = new InteractionGate()
  const handle = new RunHandle(request)
  const events: AgentEvent[] = []
  const saved: AgentMessage[] = []
  handle.on((event) => events.push(event))
  const session = new AgentSession({ host, upstream: router, tools, workspaceRoot: '/workspace',
    resumeDelaysMs,
    onMessageCommit: (message) => saved.push(structuredClone(message)),
    approve: async ({ tool, callId, input }) => {
      if (tool.readOnly) return { kind: 'allow_once' }
      const response = await gate.request(handle, { kind: 'tool_permission', callId, toolName: tool.externalName, input, readOnly: false, destructive: true }, Date.now())
      return response.kind === 'tool_permission' ? response.decision : { kind: 'deny' }
    }, interact: (draft) => gate.request(handle, draft, Date.now())
  }, handle, request)
  return { request, host, router, tools, gate, handle, events, saved, session, execute, bodies, urls, ledger, ready }
}

it('continues Responses tool rounds and reloaded history after an oversized reasoning id', async () => {
  // Matches the failing upstream shape: a 428-character id and encrypted reasoning without visible text.
  const oversized = { ...reasoningItem, id: `${'A'.repeat(427)}=`, summary: [], content: [] }
  const secondCall = { ...functionItem, id: 'fc-2', call_id: 'call-2', arguments: '{"text":"second"}' }
  const r = rig('openai-responses', [
    callResponse('openai-responses'),
    sse(responseDone([oversized, secondCall])),
    finalResponse('openai-responses'),
    finalResponse('openai-responses')
  ])
  await r.ready
  const running = r.session.run()
  for (const callId of ['call-1', 'call-2']) {
    await vi.waitFor(() => expect(r.gate.list()[0]).toMatchObject({ kind: 'tool_permission', callId }))
    r.gate.respond({ id: r.gate.list()[0]!.id, kind: 'tool_permission', decision: { kind: 'allow_once' } })
  }
  await running
  expect(r.handle.status).toBe('done')
  expect(r.execute).toHaveBeenCalledTimes(2)
  expect(orphanedToolCalls([...r.session.history])).toEqual([])
  expect(r.saved.flatMap((message) => message.parts)).toContainEqual(expect.objectContaining({
    type: 'thinking', opaque: { protocol: 'openai-responses', item: oversized }
  }))

  // Keep the original opaque item in storage; encoding must also repair existing conversations on retry.
  const history: AgentMessage[] = JSON.parse(JSON.stringify(r.saved))
  const before = structuredClone(history)
  const nextRequest = { ...r.request, runId: 'r2', input: [{ type: 'text' as const, text: '继续' }] }
  const nextHandle = new RunHandle(nextRequest)
  const next = new AgentSession({ host: r.host, upstream: r.router, tools: r.tools, workspaceRoot: '/workspace',
    history, resumeDelaysMs: [] }, nextHandle, nextRequest)
  await next.run()
  expect(nextHandle.status).toBe('done')
  expect(history).toEqual(before)
  expect(r.execute).toHaveBeenCalledTimes(2)
  expect(r.bodies).toHaveLength(4)
  expect(visibleText(next.history.at(-1)!)).toBe('完成')
  for (const body of [r.bodies[2], r.bodies[3]]) {
    const input = body?.input as Array<Record<string, unknown>>
    expect(input.filter((item) => item.type === 'reasoning')).toEqual([reasoningItem])
    expect(input).toContainEqual({ type: 'function_call_output', call_id: 'call-1', output: '{"text":"hello"}' })
    expect(input).toContainEqual({ type: 'function_call_output', call_id: 'call-2', output: '{"text":"second"}' })
  }
})

describe.each(protocols)('%s real router → agent → interaction → tool → continuation', (protocol) => {
  it('waits for approval, executes edited input, persists reasoning and sends a valid continuation', async () => {
    const r = rig(protocol, [callResponse(protocol), finalResponse(protocol), finalResponse(protocol)])
    await r.ready
    const running = r.session.run()
    await vi.waitFor(() => expect(r.gate.list()).toHaveLength(1))
    expect(r.execute).not.toHaveBeenCalled()
    expect(r.bodies).toHaveLength(1)
    const pending = r.gate.list()[0]!
    expect(r.handle.snapshot(0).pendingInteractions).toEqual([pending])
    r.gate.respond({ id: pending.id, kind: 'tool_permission', decision: { kind: 'allow_edited', input: { text: 'edited' } } })
    await running
    expect(r.handle.status).toBe('done')
    expect(r.execute).toHaveBeenCalledExactlyOnceWith({ text: 'edited' }, expect.anything())
    expect(orphanedToolCalls([...r.session.history])).toEqual([])
    expect(r.saved).toEqual(r.session.history)
    const transcript = applyEvents(emptyTranscript(), r.events)
    expect(transcript.status).toBe('done')
    expect(transcript.live).toEqual([])
    // Compare the displayed total to actual decoded API usage across the tool
    // call and continuation, including after committed deltas are pruned.
    expect(transcript.usage?.outputTokens).toBe(protocol === 'openai-chat' ? 30 : 60)
    expect(transcript.usage?.outputTokens).toBe(r.ledger.reduce((sum, entry) => sum + entry.outputTokens, 0))
    expect(applyEvents(emptyTranscript(), r.handle.snapshot(0).events).usage).toEqual(transcript.usage)
    expect(transcript.messages.flatMap((m) => m.parts).filter((p) => p.type === 'thinking')).toHaveLength(1)
    expect(visibleText(r.saved.at(-1)!)).toBe('完成')
    expect(r.ledger[0]).toMatchObject({ inputTokens: 40, cacheReadTokens: 60, thinkingTokens: 20, thinkingTokensEstimated: false, toolCalls: 1, ok: true })
    expect(r.urls).toEqual(Array(2).fill(`https://upstream.test/api/v3/${protocol === 'openai-chat' ? 'chat/completions' : 'responses'}`))
    if (protocol === 'openai-chat') {
      expect(r.bodies[0]).toMatchObject({ thinking: { type: 'enabled' }, reasoning_effort: 'high' })
      expect(r.bodies[1]?.messages).toContainEqual(expect.objectContaining({ role: 'assistant', reasoning_content: '检查参数。' }))
      expect(r.bodies[1]?.messages).toContainEqual({ role: 'tool', tool_call_id: 'call-1', content: '{"text":"edited"}' })
    } else {
      expect(r.bodies[0]).toHaveProperty('reasoning.effort', 'high')
      expect(r.bodies[1]?.input).toContainEqual(reasoningItem)
      expect(r.bodies[1]?.input).toContainEqual({ type: 'function_call_output', call_id: 'call-1', output: '{"text":"edited"}' })
    }
    // Reloaded transcript, then a new user turn: reasoning metadata survives JSON storage.
    const nextRequest = { ...r.request, runId: 'r2', input: [{ type: 'text' as const, text: '继续' }] }
    const nextHandle = new RunHandle(nextRequest)
    const next = new AgentSession({ host: r.host, upstream: r.router, tools: r.tools, workspaceRoot: '/workspace',
      history: JSON.parse(JSON.stringify(r.saved)) }, nextHandle, nextRequest)
    await next.run()
    expect(nextHandle.status).toBe('done')
    expect(JSON.stringify(r.bodies[2])).toContain(protocol === 'openai-chat' ? 'reasoning_content' : 'encrypted-reasoning')
  })

  it('returns a denial to the model without executing the tool', async () => {
    const r = rig(protocol, [callResponse(protocol), finalResponse(protocol)])
    await r.ready
    const running = r.session.run()
    await vi.waitFor(() => expect(r.gate.list()).toHaveLength(1))
    r.gate.respond({ id: r.gate.list()[0]!.id, kind: 'tool_permission', decision: { kind: 'deny' } })
    await running
    expect(r.execute).not.toHaveBeenCalled()
    expect(r.handle.status).toBe('done')
    expect(JSON.stringify(r.bodies[1])).toContain('denied')
  })

  it('cancels during approval and closes unexecuted calls for the next user turn', async () => {
    const r = rig(protocol, [callResponse(protocol)])
    await r.ready
    const running = r.session.run()
    await vi.waitFor(() => expect(r.gate.list()).toHaveLength(1))
    r.handle.abort({ by: 'user' })
    await running
    expect(r.handle.status).toBe('aborted')
    expect(r.gate.list()).toEqual([])
    expect(r.execute).not.toHaveBeenCalled()
    expect(orphanedToolCalls([...r.session.history])).toEqual([])
  })

  it('asks the user and sends the actual answer back as a tool result', async () => {
    const r = rig(protocol, [callResponse(protocol, 'AskUserQuestion', '{"questions":[{"header":"Choose","question":"Choose one","options":[{"label":"A"},{"label":"B"}],"multiSelect":false,"allowFreeform":false}]}'), finalResponse(protocol)])
    await r.ready
    const running = r.session.run()
    await vi.waitFor(() => expect(r.gate.list()[0]?.kind).toBe('ask_user'))
    r.gate.respond({ id: r.gate.list()[0]!.id, kind: 'ask_user', answers: [['B']] })
    await running
    expect(r.handle.status).toBe('done')
    expect(JSON.stringify(r.bodies[1])).toContain('answer')
    expect(r.execute).not.toHaveBeenCalled()
  })

  /** 流在工具参数说到一半时断掉:`{"text":` 是个不完整的 JSON */
  const truncated = (): Response => protocol === 'openai-chat' ? sse(chunk({ reasoning_content: 'partial', tool_calls: [
    { index: 0, id: 'c', function: { name: 'Echo', arguments: '{"text":' } }
  ] })) : sse({ type: 'response.output_item.added', output_index: 0, item: functionItem })

  it('never executes partial tool output on a truncated stream', async () => {
    const r = rig(protocol, [truncated()])
    await r.ready
    await r.session.run()
    expect(r.handle.status).toBe('error')
    expect(r.bodies).toHaveLength(1)
    // ★ 半截的工具调用**一次都不许执行** —— `{"text":` 解析出来的参数是残的,
    //   而 Echo 是 destructive。这一条和续跑开不开没有关系,下一条用例里同样成立。
    expect(r.execute).not.toHaveBeenCalled()
    expect(orphanedToolCalls([...r.session.history])).toEqual([])
  })

  /**
   * ★★ 同一个截断流,打开续跑之后应该自己重来一轮。
   *
   * 这是整条链的端到端验收:真 transport 把流读到一半就没了 → `streamOnce` 合成
   * `incompleteResponse`(code `network`,**router 一个 error 事件都没发过**,因为
   * 从它的角度看这是一次正常结束的 HTTP 响应)→ session 退避后原样重发。
   *
   * 三条断言各钉一件事:重发了(`bodies` 两份且逐字相同)、这一轮**真的救回来了**
   * (`done` + 「完成」)、被丢弃那次的半截工具调用**没有溜进执行**。
   * 第三条尤其要紧:续跑丢弃的是整个 accumulator,漏一点都会变成一次带残参数的
   * destructive 调用。
   */
  it('★ resumes a truncated stream and discards the partial tool call', async () => {
    const r = rig(protocol, [truncated(), finalResponse(protocol)], [0])
    await r.ready
    await r.session.run()
    expect(r.handle.status).toBe('done')
    expect(r.bodies).toHaveLength(2)
    expect(JSON.stringify(r.bodies[0])).toBe(JSON.stringify(r.bodies[1]))
    expect(visibleText(r.saved.at(-1)!)).toBe('完成')
    expect(r.execute).not.toHaveBeenCalled()
    expect(orphanedToolCalls([...r.session.history])).toEqual([])
    // 半截那一份一个字都不留:转录里没有第二个气泡,也没有 partial 的思考块
    expect(applyEvents(emptyTranscript(), r.events).messages.filter((m) => m.role === 'assistant')).toHaveLength(1)
    /*
      ★★ 第二次尝试必须**重新发一个 `message_start`** —— 渲染层清掉上一次半截
      文字的唯一时机就是它(`transcript.ts` 的 `case 'message_start'` 里那句
      `live: []`)。协议解析器哪天改成「只在第一条响应上发」,这里就会红,
      而界面上的症状是两段文字前后接不上的乱码 —— 一个截图都难抓的错乱。
    */
    expect(r.events.filter((e) => e.type === 'stream' && e.delta.type === 'message_start')).toHaveLength(2)
  })
})
