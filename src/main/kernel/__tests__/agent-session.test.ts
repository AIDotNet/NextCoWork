import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import { agentError } from '../../../shared/agent/error'
import type { AgentEvent } from '../../../shared/agent/event'
import type { InterjectItem } from '../../../shared/agent/interject'
import type { AgentMessage, ContentPart } from '../../../shared/agent/message'
import { assistantMessage, orphanedToolCalls, userMessage } from '../../../shared/agent/message'
import { applyEvents, emptyTranscript } from '../../../shared/agent/transcript'
import type { RunRequest } from '../../../shared/agent/run-request'
import { DEFAULT_MAX_OUTPUT_TOKENS, MAX_TURNS } from '../../../shared/agent/run-request'
import type { ProviderStreamEvent } from '../../../shared/agent/stream'
import type { ToolResult, ToolSource } from '../../../shared/agent/tool'
import { toolOk } from '../../../shared/agent/tool'
import type { ModelAlias } from '../../../shared/domain/provider'
import {
  AgentSession,
  type ApproveFn,
  type SessionDeps,
  type SessionUpstream
} from '../agent-session'
import { nodeHost } from '../host'
import { COMPACT_SYSTEM, MAX_CONSECUTIVE_COMPACT_FAILURES } from '../compaction/compact'
import { INHERIT_THINKING } from '../../../shared/domain/subagent-thinking'
import { localEnvironment } from '../../environment/local'
import { collect, RunHandle } from '../run-registry'
import { ToolRegistry, type ToolContext } from '../tool/registry'
import type { CanonicalRequest, UpstreamRequestContext } from '../upstream/canonical'

// ─────────────────────────── 夹具 ───────────────────────────

const ALIAS: ModelAlias = {
  alias: 'claude-sonnet-4',
  providerId: 'p1',
  upstreamModel: 'claude-sonnet-4-20250514',
  capabilities: { tools: true, vision: true, thinking: true, caching: true },
  contextWindow: 200_000,
  maxOutputTokens: 8192
}

function req(over: Partial<RunRequest> = {}): RunRequest {
  return {
    runId: 'run-1',
    sessionId: 'sess-1',
    workspaceId: 'ws-1',
    depth: 0,
    input: [{ type: 'text', text: '你好' }],
    mode: 'normal',
    thinking: 'off',
    webSearch: false,
    permissionMode: 'ask',
    model: 'claude-sonnet-4',
    skillIds: [],
    ...over
  }
}

/** 安静的 host —— 会话在若干路径上会 warn/error,测试里不需要看 */
function quietHost(): ReturnType<typeof nodeHost> {
  return nodeHost({
    logger: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() }
  })
}

interface FakeUpstream extends SessionUpstream {
  /** 每一轮实际发出去的请求 —— 断言工具列表、消息累积都靠它 */
  readonly requests: CanonicalRequest[]
  /** 每一轮独立传递的运行上下文 —— 不应混进 CanonicalRequest。 */
  readonly contexts: UpstreamRequestContext[]
}

/**
 * 按轮次脚本化的假上游。
 *
 * ★ 这个十几行的假对象就是 `SessionUpstream` 存在的理由(见 agent-session.ts):
 * 整个循环能在不搭供应商、不搭凭证、不搭健康表的情况下跑完。
 */
function fakeUpstream(
  turns: ProviderStreamEvent[][],
  opts: { models?: ModelAlias[] } = {}
): FakeUpstream {
  const requests: CanonicalRequest[] = []
  const contexts: UpstreamRequestContext[] = []
  let i = 0
  return {
    requests,
    contexts,
    listModels: () => opts.models ?? [ALIAS],
    resolveModel: (model, providerId) => (opts.models ?? [ALIAS]).find(
      (m) => m.alias === model && (providerId === undefined || m.providerId === providerId)
    ),
    async *stream(
      r,
      _signal,
      context
    ): AsyncIterable<ProviderStreamEvent> {
      requests.push(r)
      contexts.push(context)
      // 脚本用尽就重复最后一段；需要时测试可验证运行跨过旧轮次默认值。
      const script = turns[Math.min(i, turns.length - 1)]
      i++
      for (const ev of script ?? []) yield ev
    }
  }
}

const END = (stopReason: 'end_turn' | 'tool_use' = 'end_turn'): ProviderStreamEvent => ({
  type: 'message_end',
  stopReason,
  usage: { inputTokens: 10, outputTokens: 5 }
})

/** 说一句话就收工 */
function says(text: string): ProviderStreamEvent[] {
  return [
    { type: 'message_start', model: 'claude-sonnet-4' },
    { type: 'text_delta', index: 0, text },
    END()
  ]
}

/** 调一次工具 */
function callsTool(callId: string, name: string, args = '{}'): ProviderStreamEvent[] {
  return [
    { type: 'message_start', model: 'claude-sonnet-4' },
    { type: 'tool_call_start', index: 0, callId, name },
    { type: 'tool_call_delta', index: 0, callId, argsDelta: args },
    { type: 'tool_call_end', index: 0, callId },
    END('tool_use')
  ]
}

interface Registered {
  internalId: string
  readOnly?: boolean
  destructive?: boolean
  needsNetwork?: boolean
  source?: ToolSource
  execute?: (input: unknown, ctx: ToolContext) => Promise<ToolResult>
}

function registry(...tools: Registered[]): ToolRegistry {
  const r = new ToolRegistry()
  for (const t of tools) {
    r.register({
      internalId: t.internalId,
      description: `${t.internalId} 工具`,
      inputSchema: { type: 'object' },
      readOnly: t.readOnly ?? true,
      destructive: t.destructive ?? false,
      needsNetwork: t.needsNetwork ?? false,
      source: t.source ?? { kind: 'builtin' },
      execute: t.execute ?? ((input) => Promise.resolve(toolOk(JSON.stringify(input))))
    })
  }
  return r
}

interface Ran {
  events: AgentEvent[]
  history: readonly AgentMessage[]
  handle: RunHandle
  upstream: FakeUpstream
}

async function runSession(o: {
  upstream: FakeUpstream
  tools?: ToolRegistry
  request?: RunRequest
  history?: readonly AgentMessage[]
  approve?: ApproveFn
  onToolUsage?: SessionDeps['onToolUsage']
  allowedTools?: SessionDeps['allowedTools']
  /** 设置 › 通用 › Agent 的输出额度;缺省 = 没配置,按 `DEFAULT_MAX_OUTPUT_TOKENS` 走 */
  maxOutputTokens?: number
  resumeDelaysMs?: readonly number[]
}): Promise<Ran> {
  const request = o.request ?? req()
  const handle = new RunHandle(request)
  const session = new AgentSession(
    {
      host: quietHost(),
      upstream: o.upstream,
      tools: o.tools ?? registry(),
      workspaceRoot: '/ws',
      ...(o.history !== undefined ? { history: o.history } : {}),
      ...(o.approve !== undefined ? { approve: o.approve } : {}),
      ...(o.onToolUsage !== undefined ? { onToolUsage: o.onToolUsage } : {}),
      ...(o.allowedTools !== undefined ? { allowedTools: o.allowedTools } : {}),
      ...(o.maxOutputTokens !== undefined ? { maxOutputTokens: o.maxOutputTokens } : {}),
      // 缺省关掉断流续跑:它只在明确要测的那几条用例里打开,别的用例不该因为
      // 上游脚本里出现一个 error 事件就凭空多跑五轮。
      resumeDelaysMs: o.resumeDelaysMs ?? []
    },
    handle,
    request
  )
  // ★ 订阅在前、启动在后 —— 与渲染层的时序一致(方案 §3 规则 2)
  const events = collect(handle)
  await session.run()
  return { events: await events, history: session.history, handle, upstream: o.upstream }
}

// 便捷断言
const kinds = (events: AgentEvent[]): string[] => events.map((e) => e.type)
const commits = (events: AgentEvent[]): AgentMessage[] =>
  events.flatMap((e) => (e.type === 'message_commit' ? [e.message] : []))
const partsOf = (ms: readonly AgentMessage[]): ContentPart[] => ms.flatMap((m) => m.parts)
const runEnd = (events: AgentEvent[]): Extract<AgentEvent, { type: 'run_end' }> => {
  const e = events.at(-1)
  if (e?.type !== 'run_end') throw new Error(`最后一个事件不是 run_end,而是 ${e?.type}`)
  return e
}

/**
 * ★ 全套测试共用的那条不变式:**每个 tool_call 都必须有配对的 tool_result**。
 *
 * Anthropic 要求 tool_use 在紧随的 user 消息里配对;违反它下一轮就是 400,
 * 而报错会指向消息数组,看起来像 adapter 的 bug(方案 §4.8)。
 */
function expectNoOrphans(history: readonly AgentMessage[]): void {
  const opened = new Set<string>()
  const closed = new Set<string>()
  for (const p of partsOf(history)) {
    if (p.type === 'tool_call') opened.add(p.callId)
    else if (p.type === 'tool_result') closed.add(p.callId)
  }
  expect([...opened].filter((c) => !closed.has(c)), '孤儿 tool_call').toEqual([])
  expect([...closed].filter((c) => !opened.has(c)), '无主 tool_result').toEqual([])
}

// ─────────────────────────── 最简一轮 ───────────────────────────

describe('stalled operation cancellation', () => {
  it('stops a stalled upstream without waiting for another stream event', async () => {
    let releaseStream!: () => void
    let waiting = false
    const blocked = new Promise<void>((resolve) => { releaseStream = resolve })
    const upstream = fakeUpstream([])
    upstream.stream = async function* () {
      yield { type: 'message_start', model: ALIAS.alias }
      yield { type: 'text_delta', index: 0, text: 'partial response' }
      waiting = true
      await blocked
      yield { type: 'text_delta', index: 0, text: 'late response' }
      yield END()
    }
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession({
      host: quietHost(), upstream, tools: registry(), workspaceRoot: '/ws'
    }, handle, request)
    const running = session.run()

    try {
      await vi.waitFor(() => expect(waiting).toBe(true), { timeout: 200, interval: 1 })
      handle.abort({ by: 'user' })
      await vi.waitFor(() => expect(handle.status).toBe('aborted'), { timeout: 200, interval: 1 })
      await running
      expect(partsOf(session.history)).toContainEqual({ type: 'text', text: 'partial response' })
      expect(handle.since(0).filter((event) => event.type === 'run_end')).toHaveLength(1)
    } finally {
      releaseStream()
      await running
    }
    expect(partsOf(session.history)).not.toContainEqual({ type: 'text', text: 'late response' })
  })

  it.each(['approval', 'tool'] as const)('stops a stalled %s and ignores its late result', async (stage) => {
    let releaseOperation!: () => void
    let waiting = false
    const blocked = new Promise<void>((resolve) => { releaseOperation = resolve })
    const execute = vi.fn(async () => {
      if (stage === 'tool') {
        waiting = true
        await blocked
      }
      return toolOk('late result')
    })
    const approve: ApproveFn = async () => {
      if (stage === 'approval') {
        waiting = true
        await blocked
      }
      return { kind: 'allow_once' }
    }
    const upstream = fakeUpstream([callsTool('blocked-call', 'Blocked')])
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession({
      host: quietHost(), upstream, tools: registry({ internalId: 'Blocked', execute }),
      workspaceRoot: '/ws', approve
    }, handle, request)
    const running = session.run()

    try {
      await vi.waitFor(() => expect(waiting).toBe(true), { timeout: 200, interval: 1 })
      handle.abort({ by: 'user' })
      await vi.waitFor(() => expect(handle.status).toBe('aborted'), { timeout: 200, interval: 1 })
      await running
      expectNoOrphans(session.history)
      expect(partsOf(session.history)).toContainEqual(expect.objectContaining({
        type: 'tool_result', callId: 'blocked-call', isError: true,
        output: { content: expect.stringContaining('interrupted') }
      }))
    } finally {
      releaseOperation()
      await running
    }
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(execute).toHaveBeenCalledTimes(stage === 'tool' ? 1 : 0)
    expect(handle.since(0).filter((event) => event.type === 'run_end')).toHaveLength(1)
    expect(partsOf(session.history)).not.toContainEqual(expect.objectContaining({
      type: 'tool_result', output: { content: 'late result' }
    }))
  })
})

describe('单轮对话', () => {
  it('说一句话就正常收尾', async () => {
    const { events, history } = await runSession({ upstream: fakeUpstream([says('你好呀')]) })

    expect(runEnd(events).status).toBe('done')
    expect(history).toHaveLength(2)
    expect(history[0]).toMatchObject({ role: 'user', parts: [{ type: 'text', text: '你好' }] })
    expect(history[1]).toMatchObject({
      role: 'assistant',
      parts: [{ type: 'text', text: '你好呀' }]
    })
  })

  it('使用 RunRequest 提供的用户消息 ID提交首条输入', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([says('收到')]),
      request: req({ inputMessageId: 'input-message-1' })
    })

    expect(history[0]?.id).toBe('input-message-1')
    expect(commits(events)[0]?.id).toBe('input-message-1')
  })

  it('将 RunRequest 的运行标识原样放在独立上下文中传给上游', async () => {
    const workspaceId = 'ws-opaque-tenant-123'
    const runId = 'run-opaque-456'
    const sessionId = 'session-opaque-789'
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('收到')]),
      request: req({ workspaceId, runId, sessionId })
    })

    expect(upstream.contexts).toEqual([{ workspaceId, runId, sessionId }])
    expect(upstream.requests[0]).not.toHaveProperty('workspaceId')
    expect(upstream.requests[0]).not.toHaveProperty('runId')
    expect(upstream.requests[0]).not.toHaveProperty('sessionId')
  })

  it('每个上游事件都原样转发给 UI', async () => {
    const { events } = await runSession({ upstream: fakeUpstream([says('嗨')]) })
    const streamed = events.flatMap((e) => (e.type === 'stream' ? [e.delta.type] : []))
    expect(streamed).toEqual(['message_start', 'text_delta', 'message_end'])
  })

  /** ★ 压力条要在这一轮真的挤爆**之前**就让用户看见(方案 §4.12) */
  it('context_usage 在第一个 delta 之前发出', async () => {
    const { events } = await runSession({ upstream: fakeUpstream([says('嗨')]) })
    const usage = kinds(events).indexOf('context_usage')
    const firstStream = kinds(events).indexOf('stream')
    expect(usage).toBeGreaterThanOrEqual(0)
    expect(usage).toBeLessThan(firstStream)
  })

  it('context_usage 带上窗口大小与压缩判断', async () => {
    const { events } = await runSession({ upstream: fakeUpstream([says('嗨')]) })
    const u = events.find((e) => e.type === 'context_usage')
    expect(u).toMatchObject({ window: 200_000, shouldCompact: false })
    expect(u?.type === 'context_usage' && u.used).toBeGreaterThan(0)
  })

  /** 空 input = 续跑(排队消息之外的场景) */
  it('空 input 不追加用户消息', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([says('接着说')]),
      request: req({ input: [] }),
      history: [userMessage('old', [{ type: 'text', text: '之前的问题' }], 1)]
    })
    expect(history.map((m) => m.id)).toEqual(['old', expect.any(String)])
  })

  it('已有转录被带进请求', async () => {
    const past = [
      userMessage('u0', [{ type: 'text', text: '第一句' }], 1),
      assistantMessage('a0', [{ type: 'text', text: '第一答' }], 2)
    ]
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('第二答')]),
      history: past
    })
    expect(upstream.requests[0]?.messages.map((m) => m.id)).toEqual([
      'u0',
      'a0',
      expect.any(String)
    ])
  })

  /** ★ session 不持久化,只把结果留在 history 里 —— 但不能改调用方那份数组 */
  it('不修改传入的 history', async () => {
    const past: AgentMessage[] = [userMessage('u0', [{ type: 'text', text: 'x' }], 1)]
    const { history } = await runSession({ upstream: fakeUpstream([says('y')]), history: past })
    expect(past).toHaveLength(1)
    expect(history.length).toBeGreaterThan(1)
  })

  it('一个字都没吐时不提交空消息', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([[{ type: 'message_start', model: 'm' }, END()]])
    })
    expect(runEnd(events).status).toBe('done')
    expect(history).toHaveLength(1) // 只有用户那条
  })
})

// ─────────────────────────── 工具循环 ───────────────────────────

describe('think → tool → observe 循环', () => {
  it('调用工具后带着结果再跑一轮', async () => {
    const { events, history, upstream } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo', '{"m":"hi"}'), says('工具说了 hi')]),
      tools: registry({ internalId: 'echo' })
    })

    expect(runEnd(events).status).toBe('done')
    expect(history.map((m) => m.role)).toEqual(['user', 'assistant', 'user', 'assistant'])
    expect(history[2]?.parts[0]).toMatchObject({
      type: 'tool_result',
      callId: 'c1',
      isError: false
    })
    expect(upstream.requests).toHaveLength(2)
    expectNoOrphans(history)
  })

  it('工具结果回到了第二轮的请求里', async () => {
    const { upstream } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo', '{"m":"hi"}'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    const second = upstream.requests[1]?.messages ?? []
    expect(
      second.flatMap((m) => m.parts).some((p) => p.type === 'tool_result' && p.callId === 'c1')
    ).toBe(true)
  })

  it('工具拿到解析后的入参', async () => {
    const seen: unknown[] = []
    await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo', '{"m":"hi","n":3}'), says('好')]),
      tools: registry({
        internalId: 'echo',
        execute: (input) => {
          seen.push(input)
          return Promise.resolve(toolOk('ok'))
        }
      })
    })
    expect(seen).toEqual([{ m: 'hi', n: 3 }])
  })

  it('多个工具调用并行执行,结果仍按调用顺序合成一条消息', async () => {
    let active = 0
    let maxActive = 0
    const exec =
      (tag: string) =>
      async (): Promise<ReturnType<typeof toolOk>> => {
        active++
        maxActive = Math.max(maxActive, active)
        await Promise.resolve()
        active--
        return toolOk(tag)
      }

    const { history } = await runSession({
      upstream: fakeUpstream([
        [
          { type: 'tool_call_start', index: 0, callId: 'c1', name: 'a' },
          { type: 'tool_call_end', index: 0, callId: 'c1' },
          { type: 'tool_call_start', index: 1, callId: 'c2', name: 'b' },
          { type: 'tool_call_end', index: 1, callId: 'c2' },
          END('tool_use')
        ],
        says('都做完了')
      ]),
      tools: registry(
        { internalId: 'a', execute: exec('a') },
        { internalId: 'b', execute: exec('b') }
      )
    })

    expect(maxActive).toBe(2)
    const results = history[2]?.parts ?? []
    expect(results).toHaveLength(2)
    expect(results.every((p) => p.type === 'tool_result')).toBe(true)
    expectNoOrphans(history)
  })

  it('tool_start / tool_end 事件成对出现', async () => {
    const { events } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    expect(kinds(events).filter((k) => k.startsWith('tool_'))).toEqual(['tool_start', 'tool_end'])
  })

  /** ★ 审批弹窗、工具卡片、转录三条轨道上必须是同一个名字,用户才能把它们对上号 */
  it('tool_start 用的是 externalName,与转录里的名字一致', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    const start = events.find((e) => e.type === 'tool_start')
    const callPart = partsOf(history).find((p) => p.type === 'tool_call')
    expect(start?.type === 'tool_start' && start.toolName).toBe('echo')
    expect(callPart?.type === 'tool_call' && callPart.name).toBe('echo')
  })

  /**
   * ★ 上一条里 internalId 恰好等于 externalName,分不出两者。这条用一个真实的
   * MCP 长名字(`mcp__…__create_pull_request_review_comment` 超 64 字符)把它们拉开:
   * 三条轨道上出现的都必须是**模型看得见的那个名字**,否则审批弹窗显示的工具
   * 和转录里记的工具对不上号。
   */
  it('internalId 与 externalName 不同时,三处用的都是 externalName', async () => {
    const internalId = 'mcp__github-enterprise-internal__create_pull_request_review_comment'
    const tools = registry({ internalId, source: { kind: 'mcp', serverId: 'gh' } })
    const external = tools.snapshot()[0]?.externalName ?? ''
    expect(external).not.toBe(internalId)
    expect(external.length).toBeLessThanOrEqual(64)

    const { events, history, upstream } = await runSession({
      upstream: fakeUpstream([callsTool('c1', external), says('好')]),
      tools
    })

    expect(upstream.requests[0]?.tools[0]?.externalName).toBe(external)
    const start = events.find((e) => e.type === 'tool_start')
    expect(start?.type === 'tool_start' && start.toolName).toBe(external)
    const callPart = partsOf(history).find((p) => p.type === 'tool_call')
    expect(callPart?.type === 'tool_call' && callPart.name).toBe(external)
    expect(history[2]?.parts[0]).toMatchObject({ isError: false })
  })

  it('工具进度经 tool_progress 发出,且不进转录', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'slow'), says('好')]),
      tools: registry({
        internalId: 'slow',
        execute: (_i, ctx) => {
          ctx.emit({ callId: ctx.callId, message: '读取中', fraction: 0.5 })
          return Promise.resolve(toolOk('done'))
        }
      })
    })
    expect(events.some((e) => e.type === 'tool_progress')).toBe(true)
    expect(JSON.stringify(history)).not.toContain('读取中')
  })

  /** ★ ctx.signal 必须真的是 run 的 signal:只断 SSE 不断工具会留下僵尸进程(方案 §4.3) */
  it('工具拿到的是 run 的 signal', async () => {
    let sameSignal = false
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession(
      {
        host: quietHost(),
        upstream: fakeUpstream([callsTool('c1', 'probe'), says('好')]),
        tools: registry({
          internalId: 'probe',
          execute: (_i, ctx) => {
            sameSignal = ctx.signal === handle.signal
            return Promise.resolve(toolOk('ok'))
          }
        }),
        workspaceRoot: '/ws'
      },
      handle,
      request
    )
    await session.run()
    expect(sameSignal).toBe(true)
  })

  it('工具上下文带着权限档位与深度', async () => {
    let ctx: ToolContext | undefined
    await runSession({
      upstream: fakeUpstream([callsTool('c1', 'probe'), says('好')]),
      request: req({ permissionMode: 'full', depth: 1 }),
      tools: registry({
        internalId: 'probe',
        execute: (_i, c) => {
          ctx = c
          return Promise.resolve(toolOk('ok'))
        }
      })
    })
    expect(ctx).toMatchObject({ permissionMode: 'full', depth: 1, workspaceRoot: '/ws' })
  })
})

// ─────────────────────────── 工具错误 ───────────────────────────

describe('工具错误进转录并继续循环(方案 §4.11)', () => {
  it('模型编了个不存在的工具名', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'nonexistent'), says('哦')]),
      tools: registry({ internalId: 'echo' })
    })

    expect(runEnd(events).status).toBe('done')
    const result = history[2]?.parts[0]
    expect(result).toMatchObject({ type: 'tool_result', callId: 'c1', isError: true })
    expect(result?.type === 'tool_result' && result.output.content).toContain('nonexistent')
    expectNoOrphans(history)
  })

  /** ★ 只说「参数错了」模型无从下手 —— 得把它自己发出的原文还给它 */
  it('参数不是合法 JSON 时把原文回给模型', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo', '{"m": '), says('我改一下')]),
      tools: registry({ internalId: 'echo' })
    })

    expect(runEnd(events).status).toBe('done')
    const result = history[2]?.parts[0]
    expect(result?.type === 'tool_result' && result.output.content).toContain('{"m": ')
    expectNoOrphans(history)
  })

  it('工具抛异常时收敛成工具错误,run 不挂', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'boom'), says('那算了')]),
      tools: registry({
        internalId: 'boom',
        execute: () => Promise.reject(new Error('磁盘满了'))
      })
    })

    expect(runEnd(events).status).toBe('done')
    const result = history[2]?.parts[0]
    expect(result).toMatchObject({ isError: true })
    expect(result?.type === 'tool_result' && result.output.content).toContain('磁盘满了')
    expectNoOrphans(history)
  })

  it('工具自己返回 isError 时照样回填并继续', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'fails'), says('知道了')]),
      tools: registry({
        internalId: 'fails',
        execute: () => Promise.resolve({ output: { content: '文件不存在' }, isError: true })
      })
    })
    expect(runEnd(events).status).toBe('done')
    expect(history[2]?.parts[0]).toMatchObject({ isError: true })
  })

  it('将工具调用数和真实失败数回填到产生调用的用量记录', async () => {
    const onToolUsage = vi.fn<NonNullable<SessionDeps['onToolUsage']>>()
    const script: ProviderStreamEvent[] = [
      { type: 'message_start', model: 'claude-sonnet-4' },
      { type: 'tool_call_start', index: 0, callId: 'c1', name: 'ok' },
      { type: 'tool_call_delta', index: 0, callId: 'c1', argsDelta: '{}' },
      { type: 'tool_call_end', index: 0, callId: 'c1' },
      { type: 'tool_call_start', index: 1, callId: 'c2', name: 'fails' },
      { type: 'tool_call_delta', index: 1, callId: 'c2', argsDelta: '{}' },
      { type: 'tool_call_end', index: 1, callId: 'c2' },
      END('tool_use')
    ]

    await runSession({
      upstream: fakeUpstream([script, says('知道了')]),
      tools: registry(
        { internalId: 'ok' },
        {
          internalId: 'fails',
          execute: () => Promise.resolve({ output: { content: '失败' }, isError: true })
        }
      ),
      onToolUsage
    })

    expect(onToolUsage).toHaveBeenCalledTimes(1)
    expect(onToolUsage).toHaveBeenCalledWith({
      runId: 'run-1',
      toolCalls: 2,
      toolErrors: 1
    })
  })

  /** 每一种工具错误都要发 tool_end,否则 UI 上那张卡片会永远转圈 */
  it('工具错误也发 tool_end', async () => {
    for (const script of [
      callsTool('c1', 'nope'),
      callsTool('c1', 'echo', '坏 JSON')
    ]) {
      const { events } = await runSession({
        upstream: fakeUpstream([script, says('好')]),
        tools: registry({ internalId: 'echo' })
      })
      const end = events.find((e) => e.type === 'tool_end')
      expect(end).toMatchObject({ callId: 'c1', isError: true })
    }
  })
})

// ─────────────────────────── 权限审批 ───────────────────────────

describe('审批接缝(方案 §4.5 / §4.6)', () => {
  it('没有 approve 时默认放行', async () => {
    const exec = vi.fn(() => Promise.resolve(toolOk('ran')))
    await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({ internalId: 'echo', execute: exec })
    })
    expect(exec).toHaveBeenCalledTimes(1)
  })

  /** ★ 拒绝要让模型**看见** —— 系统提示词里「被拒绝时不要试图绕开」的前提是它知道被拒了 */
  it('拒绝时不执行,理由回给模型', async () => {
    const exec = vi.fn(() => Promise.resolve(toolOk('不该跑')))
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'rm'), says('那我不删了')]),
      tools: registry({ internalId: 'rm', readOnly: false, destructive: true, execute: exec }),
      approve: () => Promise.resolve({ kind: 'deny', reason: '用户不同意删除' })
    })

    expect(exec).not.toHaveBeenCalled()
    expect(runEnd(events).status).toBe('done')
    const result = history[2]?.parts[0]
    expect(result?.type === 'tool_result' && result.output.content).toContain('用户不同意删除')
    expect(result).toMatchObject({ isError: true })
    expectNoOrphans(history)
  })

  it('没给理由时也有一句默认说明', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'rm'), says('好')]),
      tools: registry({ internalId: 'rm' }),
      approve: () => Promise.resolve({ kind: 'deny' })
    })
    const result = history[2]?.parts[0]
    expect(result?.type === 'tool_result' && result.output.content).toContain('denied')
  })

  /** 用原值执行等于无视用户的修改 */
  it('allow_edited 用改过的入参执行', async () => {
    const seen: unknown[] = []
    await runSession({
      upstream: fakeUpstream([callsTool('c1', 'bash', '{"cmd":"rm -rf /"}'), says('好')]),
      tools: registry({
        internalId: 'bash',
        execute: (input) => {
          seen.push(input)
          return Promise.resolve(toolOk('ok'))
        }
      }),
      approve: () => Promise.resolve({ kind: 'allow_edited', input: { cmd: 'ls' } })
    })
    expect(seen).toEqual([{ cmd: 'ls' }])
  })

  it('审批请求带齐 runId / callId / 工具与入参', async () => {
    const approve = vi.fn<ApproveFn>(() => Promise.resolve({ kind: 'allow_once' }))
    await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo', '{"m":"x"}'), says('好')]),
      tools: registry({ internalId: 'echo' }),
      approve
    })
    expect(approve).toHaveBeenCalledWith(
      expect.objectContaining({
        runId: 'run-1',
        callId: 'c1',
        input: { m: 'x' },
        tool: expect.objectContaining({ externalName: 'echo' })
      })
    )
  })

  /** 审批发生在 tool_start 之后:UI 要先画出卡片,再在上面盖一个待决框 */
  it('tool_start 先于审批发出', async () => {
    const seenAt: string[] = []
    await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({
        internalId: 'echo',
        execute: () => {
          seenAt.push('execute')
          return Promise.resolve(toolOk('ok'))
        }
      }),
      approve: () => {
        seenAt.push('approve')
        return Promise.resolve({ kind: 'allow_once' })
      }
    })
    expect(seenAt).toEqual(['approve', 'execute'])
  })
})

// ─────────────────────────── 会话模式 ───────────────────────────

describe('运行时工具白名单落在工具层', () => {
  it('模式白名单外的工具不出现在请求里', async () => {
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('这是我的方案')]),
      request: req({ mode: 'plan' }),
      allowedTools: ['read_file'],
      tools: registry(
        { internalId: 'read_file', readOnly: true },
        { internalId: 'write_file', readOnly: false }
      )
    })
    expect(upstream.requests[0]?.tools.map((t) => t.externalName)).toEqual(['read_file'])
  })

  it('code 模式没有白名单时保留写工具', async () => {
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('好')]),
      tools: registry(
        { internalId: 'read_file', readOnly: true },
        { internalId: 'write_file', readOnly: false }
      )
    })
    expect(upstream.requests[0]?.tools.map((t) => t.externalName)).toEqual([
      'read_file',
      'write_file'
    ])
  })

  it('模型调用白名单外工具时得到未知工具错误', async () => {
    const exec = vi.fn(() => Promise.resolve(toolOk('不该跑')))
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'write_file'), says('好吧')]),
      request: req({ mode: 'plan' }),
      allowedTools: ['read_file'],
      tools: registry({ internalId: 'write_file', readOnly: false, execute: exec })
    })
    expect(exec).not.toHaveBeenCalled()
    expect(history[2]?.parts[0]).toMatchObject({ isError: true })
  })

  /** ★ execute 是闭包,过不了结构化克隆 —— 请求体里不该带着它 */
  it('请求里的工具不带 execute', async () => {
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    expect(upstream.requests[0]?.tools[0]).not.toHaveProperty('execute')
    expect(() => structuredClone(upstream.requests[0]?.tools)).not.toThrow()
  })
})

describe('从异常中断的转录恢复', () => {
  it('在继续发送用户消息前补齐历史孤儿 tool_result，并保留可解释的错误原因', async () => {
    const now = 100
    const history = [
      userMessage('u0', [{ type: 'text', text: '执行任务' }], now),
      assistantMessage('a0', [{ type: 'tool_call', callId: 'call-orphan', name: 'read', input: {} }], now + 1)
    ]

    const { events, history: recovered, upstream } = await runSession({
      history,
      upstream: fakeUpstream([says('已恢复')]),
      request: req({ input: [{ type: 'text', text: '继续' }] })
    })

    expect(recovered.map((m) => m.role)).toEqual(['user', 'assistant', 'user', 'user', 'assistant'])
    expect(recovered[2]?.parts[0]).toMatchObject({
      type: 'tool_result',
      callId: 'call-orphan',
      isError: true
    })
    expect(recovered[2]?.parts[0]?.type === 'tool_result'
      && recovered[2].parts[0].output.content).toContain('previous run ended')
    expect(upstream.requests[0]?.messages.map((m) => m.role)).toEqual(['user', 'assistant', 'user', 'user'])
    expect(events).toContainEqual(expect.objectContaining({
      type: 'tool_end', callId: 'call-orphan', isError: true
    }))
    expectNoOrphans(recovered)
  })
})

// ─────────────────────────── 中断:方案 §4.8 第 4 件 ───────────────────────────

describe('中断收尾', () => {
  /** 工具遵守 ctx.signal:一被触发就抛 AbortError(这正是真工具该有的样子) */
  const abortsDuringExecute =
    (handle: () => RunHandle) =>
    (_input: unknown, ctx: ToolContext): Promise<never> => {
      handle().abort({ by: 'user' })
      expect(ctx.signal.aborted).toBe(true)
      return Promise.reject(new DOMException('aborted', 'AbortError'))
    }

  async function runWithAbortingTool(o: {
    upstream: FakeUpstream
    tools: (handle: () => RunHandle) => ToolRegistry
    onToolUsage?: SessionDeps['onToolUsage']
  }): Promise<Ran> {
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession(
      {
        host: quietHost(),
        upstream: o.upstream,
        tools: o.tools(() => handle),
        workspaceRoot: '/ws',
        ...(o.onToolUsage !== undefined ? { onToolUsage: o.onToolUsage } : {})
      },
      handle,
      request
    )
    const events = collect(handle)
    await session.run()
    return { events: await events, history: session.history, handle, upstream: o.upstream }
  }

  /**
   * ★★ 本文件的核心断言 —— 方案 §4.8 五件事里的**第 4 件**。
   *
   * 漏掉它下一轮请求就是 400,而报错会指向消息数组,看起来像 adapter 的 bug。
   * 「这是手写 Agent 循环最常见的自伤。」
   */
  it('工具执行途中中断,未闭合的 tool_call 被补上 tool_result', async () => {
    const { events, history } = await runWithAbortingTool({
      upstream: fakeUpstream([callsTool('c1', 'slow')]),
      tools: (h) => registry({ internalId: 'slow', execute: abortsDuringExecute(h) })
    })

    expect(runEnd(events).status).toBe('aborted')
    expectNoOrphans(history)

    /**
     * 形状钉死:**只有一条**助手消息。收尾时若忘了把 `pending` 清空,
     * 半截回复会被提交两次 —— 转录里出现两个同 callId 的 tool_call,
     * 孤儿检查看不出来(它只比对集合),但下一轮上行就是重复的 tool_use。
     */
    expect(history.map((m) => m.role)).toEqual(['user', 'assistant', 'user'])

    const result = partsOf(history).find((p) => p.type === 'tool_result')
    expect(result).toMatchObject({ callId: 'c1', isError: true })
    expect(result?.type === 'tool_result' && result.output.content).toContain('interrupted')
  })

  /** 没有这条,UI 上那张工具卡片会停在「执行中」直到下次重载 */
  it('补的 tool_result 也发 tool_end', async () => {
    const { events } = await runWithAbortingTool({
      upstream: fakeUpstream([callsTool('c1', 'slow')]),
      tools: (h) => registry({ internalId: 'slow', execute: abortsDuringExecute(h) })
    })
    const end = events.find((e) => e.type === 'tool_end')
    expect(end).toMatchObject({ callId: 'c1', isError: true })
  })

  /**
   * ★ `executeAll` 用 finally 而不是只在成功路径上提交,测的就是这条:
   * 中断发生在第 2 个工具执行途中时,第 1 个**已经真的做完了** ——
   * 不落进转录,它就会被当成孤儿补上一条「已中断」,做过的事被记成没做,
   * 而模型下一轮会据此重做一遍。
   */
  it('中断时已完成的工具结果保留原值,只有真没跑完的那个被标为中断', async () => {
    const onToolUsage = vi.fn<NonNullable<SessionDeps['onToolUsage']>>()
    const { history } = await runWithAbortingTool({
      upstream: fakeUpstream([
        [
          { type: 'tool_call_start', index: 0, callId: 'c1', name: 'fast' },
          { type: 'tool_call_end', index: 0, callId: 'c1' },
          { type: 'tool_call_start', index: 1, callId: 'c2', name: 'slow' },
          { type: 'tool_call_end', index: 1, callId: 'c2' },
          END('tool_use')
        ]
      ]),
      tools: (h) =>
        registry(
          { internalId: 'fast', execute: () => Promise.resolve(toolOk('第一个工具真的做完了')) },
          { internalId: 'slow', execute: abortsDuringExecute(h) }
        ),
      onToolUsage
    })

    expectNoOrphans(history)
    const results = partsOf(history).filter((p) => p.type === 'tool_result')
    expect(results).toHaveLength(2)
    expect(results[0]).toMatchObject({ callId: 'c1', isError: false })
    expect(results[0]?.type === 'tool_result' && results[0].output.content).toBe(
      '第一个工具真的做完了'
    )
    expect(results[1]).toMatchObject({ callId: 'c2', isError: true })
    expect(onToolUsage).toHaveBeenCalledWith({
      runId: 'run-1',
      toolCalls: 2,
      toolErrors: 1
    })
  })

  /** 用户已经在屏幕上读到的字,不能因为他点了停止就凭空消失 */
  it('流中途中断时半截回复仍进转录', async () => {
    const request = req()
    const handle = new RunHandle(request)
    const upstream: SessionUpstream = {
      listModels: () => [ALIAS],
      resolveModel: (model) => (model === ALIAS.alias ? ALIAS : undefined),
      async *stream(): AsyncIterable<ProviderStreamEvent> {
        yield { type: 'message_start', model: 'm' }
        yield { type: 'text_delta', index: 0, text: '我正在写一段' }
        yield { type: 'text_delta', index: 0, text: '很长的回复' }
        handle.abort({ by: 'user' })
        throw new DOMException('aborted', 'AbortError')
      }
    }
    const session = new AgentSession(
      { host: quietHost(), upstream, tools: registry(), workspaceRoot: '/ws' },
      handle,
      request
    )
    const events = collect(handle)
    await session.run()

    expect(runEnd(await events).status).toBe('aborted')
    expect(session.history.at(-1)).toMatchObject({
      role: 'assistant',
      parts: [{ type: 'text', text: '我正在写一段很长的回复' }]
    })
  })

  /**
   * ★ BlockAccumulator 丢掉未闭合的 tool_call,所以中断收尾**不会制造新的孤儿** ——
   * 半截参数的调用既不该执行(等于按模型没写完的意图动手),也不该留在转录里。
   */
  it('中断时半截的 tool_call 不进转录,也不产生孤儿', async () => {
    const request = req()
    const handle = new RunHandle(request)
    const upstream: SessionUpstream = {
      listModels: () => [ALIAS],
      resolveModel: (model) => (model === ALIAS.alias ? ALIAS : undefined),
      async *stream(): AsyncIterable<ProviderStreamEvent> {
        yield { type: 'text_delta', index: 0, text: '我来删掉它' }
        yield { type: 'tool_call_start', index: 1, callId: 'c1', name: 'rm' }
        yield { type: 'tool_call_delta', index: 1, callId: 'c1', argsDelta: '{"path":"/et' }
        handle.abort({ by: 'user' })
        throw new DOMException('aborted', 'AbortError')
      }
    }
    const session = new AgentSession(
      { host: quietHost(), upstream, tools: registry({ internalId: 'rm' }), workspaceRoot: '/ws' },
      handle,
      request
    )
    const events = collect(handle)
    await session.run()

    expect(runEnd(await events).status).toBe('aborted')
    expectNoOrphans(session.history)
    expect(partsOf(session.history).some((p) => p.type === 'tool_call')).toBe(false)
    expect(partsOf(session.history).some((p) => p.type === 'text')).toBe(true)
  })

  /** 用户点了停止,就不该看到一个错误弹窗 —— 哪怕上游抛的不像 AbortError */
  it('中断时上游抛出不像 AbortError 的错误,仍算 aborted', async () => {
    const request = req()
    const handle = new RunHandle(request)
    const upstream: SessionUpstream = {
      listModels: () => [ALIAS],
      resolveModel: (model) => (model === ALIAS.alias ? ALIAS : undefined),
      async *stream(): AsyncIterable<ProviderStreamEvent> {
        yield { type: 'text_delta', index: 0, text: 'x' }
        handle.abort({ by: 'user' })
        // undici 把中断包成这个样子
        throw new TypeError('fetch failed')
      }
    }
    const session = new AgentSession(
      { host: quietHost(), upstream, tools: registry(), workspaceRoot: '/ws' },
      handle,
      request
    )
    const events = collect(handle)
    await session.run()
    expect(runEnd(await events).status).toBe('aborted')
  })

  it('已经收尾的 run 不会被再收一次', async () => {
    const { events } = await runWithAbortingTool({
      upstream: fakeUpstream([callsTool('c1', 'slow')]),
      tools: (h) => registry({ internalId: 'slow', execute: abortsDuringExecute(h) })
    })
    expect(kinds(events).filter((k) => k === 'run_end')).toHaveLength(1)
  })
})

// ─────────────────────────── 流式错误与边界 ───────────────────────────

describe('错误与边界', () => {
  /**
   * 路由器把总失败表达成一个**终止事件**而不是异常(重试与故障切换都在它里面,
   * 方案 §5.3)—— 所以 session 收到 error 事件就直接收尾,不自己重试。
   */
  it('上游 error 事件让 run 以 error 收尾', async () => {
    const { events } = await runSession({
      upstream: fakeUpstream([
        [
          { type: 'text_delta', index: 0, text: '开了个头' },
          {
            type: 'error',
            error: { code: 'rate_limit', message: '429', retryable: true, status: 429 }
          }
        ]
      ])
    })
    const end = runEnd(events)
    expect(end.status).toBe('error')
    expect(end.error).toMatchObject({ code: 'rate_limit' })
  })

  /**
   * ★ error part 进转录,但它只属于 UI 那一轨 —— 编码器对它返回 null,
   * 下一轮上行时会被丢掉。重载后 attach 回来的转录里失败仍然看得见,
   * 不是一个消失了的 toast。
   */
  it('失败时半截回复与错误都进转录', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([
        [
          { type: 'text_delta', index: 0, text: '开了个头' },
          { type: 'error', error: { code: 'network', message: '断了', retryable: true } }
        ]
      ])
    })
    expect(history.at(-1)?.parts).toEqual([
      { type: 'text', text: '开了个头' },
      { type: 'error', error: { code: 'network', message: '断了', retryable: true } }
    ])
  })

  it('上游直接抛异常时 run 以 error 收尾,不逃出 run()', async () => {
    const upstream: SessionUpstream = {
      listModels: () => [ALIAS],
      resolveModel: (model) => (model === ALIAS.alias ? ALIAS : undefined),
      // eslint-disable-next-line require-yield
      async *stream(): AsyncIterable<ProviderStreamEvent> {
        throw new Error('上游炸了')
      }
    }
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession(
      { host: quietHost(), upstream, tools: registry(), workspaceRoot: '/ws' },
      handle,
      request
    )
    const events = collect(handle)
    await expect(session.run()).resolves.toBeUndefined()
    const end = runEnd(await events)
    expect(end.status).toBe('error')
    expect(end.error?.message).toContain('上游炸了')
  })

  /** 继续循环会原样重发一次同样的请求 —— 收尾比空转诚实 */
  it('stopReason 是 tool_use 却没有已闭合的调用时正常收尾', async () => {
    const { events, upstream } = await runSession({
      upstream: fakeUpstream([
        [
          { type: 'text_delta', index: 0, text: '我要调用工具' },
          { type: 'tool_call_start', index: 1, callId: 'c1', name: 'echo' },
          END('tool_use')
        ],
        says('不该有第二轮')
      ]),
      tools: registry({ internalId: 'echo' })
    })
    expect(runEnd(events).status).toBe('done')
    expect(upstream.requests).toHaveLength(1)
  })

  /** Runs may continue beyond the legacy 25-turn default until the model ends. */
  it('超过旧轮次上限后仍继续,直到模型正常收尾', async () => {
    const turns = Array.from({ length: MAX_TURNS + 2 }, (_, i) => callsTool(`c${i}`, 'echo'))
    turns.push(says('完成'))
    const { events, upstream, history } = await runSession({
      upstream: fakeUpstream(turns),
      tools: registry({ internalId: 'echo' })
    })

    const end = runEnd(events)
    expect(end.status).toBe('done')
    expect(upstream.requests.length).toBe(MAX_TURNS + 3)
    expect(history.at(-1)?.role).toBe('assistant')
    expectNoOrphans(history)
  })

  it('正文请求默认封顶 32000,不跟随模型更大的协议上限', async () => {
    const model = { ...ALIAS, maxOutputTokens: 64_000 }
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('好')], { models: [model] })
    })
    expect(upstream.requests[0]?.maxOutputTokens).toBe(DEFAULT_MAX_OUTPUT_TOKENS)
  })

  /**
   * ★ 输出额度只认全局设置项。模型目录里声明的输出上限**不再收窄它** ——
   * 那条收窄以前存在,现在换成了「超过上下文窗口才收窄」(见 resolveMaxOutputTokens)。
   * 改回去的症状:设置里写 32000,请求里发 16384,长回答被上游截断。
   */
  it('模型目录声明的输出上限不再压住全局设置值', async () => {
    const model = { ...ALIAS, maxOutputTokens: 4096 }
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('好')], { models: [model] }),
      maxOutputTokens: 32_000
    })
    expect(upstream.requests[0]?.maxOutputTokens).toBe(32_000)
  })

  it('设置里的值高于默认时按设置走', async () => {
    const { upstream } = await runSession({
      upstream: fakeUpstream([says('好')], { models: [ALIAS] }),
      maxOutputTokens: 64_000
    })
    expect(upstream.requests[0]?.maxOutputTokens).toBe(64_000)
  })

  /**
   * ★ 查不到别名时**不在这里报错**:让路由器的 noCandidateError 成为唯一的权威错误。
   * 两处都报的话,用户会随机收到信息量少的那一条。
   */
  it('模型别名查不到时照常发请求,由路由器去报错', async () => {
    const { events, upstream } = await runSession({
      upstream: fakeUpstream([says('好')], { models: [] }),
      request: req({ model: '不存在的别名' })
    })
    expect(runEnd(events).status).toBe('done')
    expect(upstream.requests[0]?.model).toBe('不存在的别名')
    expect(upstream.requests[0]?.maxOutputTokens).toBe(DEFAULT_MAX_OUTPUT_TOKENS)
  })

  /**
   * ★ 不能无条件调 truncateToolOutput:内容没超限时它返回一个干净的 { content },
   * 会把工具自己标好的 truncated / originalBytes 抹掉(工具在更靠近数据的地方截断,标记更准)。
   */
  it('未超限的工具输出原样保留自己的截断标记', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'reader'), says('好')]),
      tools: registry({
        internalId: 'reader',
        execute: () =>
          Promise.resolve(toolOk('前 100 行', { truncated: true, originalBytes: 999_999 }))
      })
    })
    expect(history[2]?.parts[0]).toMatchObject({
      output: { content: '前 100 行', truncated: true, originalBytes: 999_999 }
    })
  })

  /** 不要让一个返回 40MB 文件的工具冲垮 IPC 队列(方案 §4.3) */
  it('超限的工具输出被截断并标记', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'huge'), says('好')]),
      tools: registry({
        internalId: 'huge',
        execute: () => Promise.resolve(toolOk('x'.repeat(200_000)))
      })
    })
    const p = history[2]?.parts[0]
    expect(p?.type === 'tool_result' && p.output.content.length).toBeLessThan(200_000)
    expect(p).toMatchObject({ output: { truncated: true } })
  })
})

// ─────────────────────────── 工具快照的时点 ───────────────────────────

describe('每轮取一次工具快照(方案 §4.4)', () => {
  const MCP: ToolSource = { kind: 'mcp', serverId: 's1' }

  /**
   * ★ 名字按**本轮下发的那份快照**解析,不按实时注册表。
   *
   * 具体后果就是方案 §4.4 那条:「若该源某个工具**正在执行**,不能打断它」——
   * MCP server 在本轮进行中断开,已经下发过的调用照样跑得完。
   */
  it('本轮进行中来源断开,已下发的调用照样跑得完', async () => {
    const ran = vi.fn(() => Promise.resolve(toolOk('第二个工具还是跑了')))
    let tools: ToolRegistry
    // eslint-disable-next-line prefer-const
    tools = registry(
      {
        internalId: 'disconnect',
        source: MCP,
        // 第一个工具执行时,整个 MCP server 断开
        execute: () => {
          expect(tools.unregisterBySource(MCP)).toBe(2)
          return Promise.resolve(toolOk('server 断了'))
        }
      },
      { internalId: 'after', source: MCP, execute: ran }
    )

    const { history } = await runSession({
      upstream: fakeUpstream([
        [
          { type: 'tool_call_start', index: 0, callId: 'c1', name: 'disconnect' },
          { type: 'tool_call_end', index: 0, callId: 'c1' },
          { type: 'tool_call_start', index: 1, callId: 'c2', name: 'after' },
          { type: 'tool_call_end', index: 1, callId: 'c2' },
          END('tool_use')
        ],
        says('好')
      ]),
      tools
    })

    expect(ran).toHaveBeenCalledTimes(1)
    expect(tools.size).toBe(0)
    expectNoOrphans(history)
  })

  /** 下一轮重新取快照,所以断开的 server 到下一轮就真的不见了(§4.4) */
  it('下一轮重新取快照,下线的工具不再下发', async () => {
    let tools: ToolRegistry
    // eslint-disable-next-line prefer-const
    tools = registry(
      { internalId: 'local' },
      {
        internalId: 'remote',
        source: MCP,
        execute: () => {
          tools.unregisterBySource(MCP)
          return Promise.resolve(toolOk('ok'))
        }
      }
    )

    const { upstream } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'remote'), says('好')]),
      tools
    })

    expect(upstream.requests[0]?.tools.map((t) => t.externalName)).toEqual(['local', 'remote'])
    expect(upstream.requests[1]?.tools.map((t) => t.externalName)).toEqual(['local'])
  })

  /** 下一轮它就不在列表里了,模型再调就是一个**工具错误**,不是崩溃 */
  it('下线之后模型仍去调它,得到的是工具错误', async () => {
    let tools: ToolRegistry
    // eslint-disable-next-line prefer-const
    tools = registry({
      internalId: 'remote',
      source: MCP,
      execute: () => {
        tools.unregisterBySource(MCP)
        return Promise.resolve(toolOk('ok'))
      }
    })

    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'remote'), callsTool('c2', 'remote'), says('哦')]),
      tools
    })

    expect(runEnd(events).status).toBe('done')
    expect(history[2]?.parts[0]).toMatchObject({ callId: 'c1', isError: false })
    expect(history[4]?.parts[0]).toMatchObject({ callId: 'c2', isError: true })
    expectNoOrphans(history)
  })
})

// ─────────────────────────── 模型声明运行时约束 ───────────────────────────

describe('模型声明运行时约束', () => {
  it('不支持工具的模型不会收到注册表工具定义', async () => {
    const model: ModelAlias = {
      ...ALIAS,
      capabilities: { ...ALIAS.capabilities, tools: false }
    }
    const upstream = fakeUpstream([says('纯文本回答')], { models: [model] })
    const result = await runSession({
      upstream,
      tools: registry({ internalId: 'echo' })
    })
    expect(runEnd(result.events).status).toBe('done')
    expect(upstream.requests[0]?.tools).toEqual([])
  })

  it('图片输入在 HTTP 前按 Vision 能力拒绝', async () => {
    const model: ModelAlias = {
      ...ALIAS,
      capabilities: { ...ALIAS.capabilities, vision: false, visionInput: false }
    }
    const upstream = fakeUpstream([says('不应请求')], { models: [model] })
    const result = await runSession({
      upstream,
      request: req({ input: [{ type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/themes/a.png' }] })
    })
    expect(runEnd(result.events)).toMatchObject({
      status: 'error',
      error: { code: 'provider', retryable: false }
    })
    expect(runEnd(result.events).error?.message).toContain('Vision')
    expect(upstream.requests).toEqual([])
  })

  it('ThinkConfig 把 higher 解析为可移植的 xhigh', async () => {
    const model: ModelAlias = {
      ...ALIAS,
      thinkingConfig: {
        mode: 'effort',
        defaultEnabled: true,
        defaultEffort: 'medium',
        parameterPath: 'reasoning_effort'
      }
    }
    const upstream = fakeUpstream([says('好')], { models: [model] })
    await runSession({ upstream, request: req({ thinking: 'higher' }) })
    expect(upstream.requests[0]?.reasoning).toMatchObject({
      mode: 'effort', enabled: true, explicit: true, effort: 'xhigh'
    })
    expect(upstream.requests[0]).not.toHaveProperty('thinkingBudget')
  })

  it('输入加预留输出超过上下文窗口时不调用上游', async () => {
    const model: ModelAlias = { ...ALIAS, contextWindow: 1_200, maxOutputTokens: 1_000 }
    const upstream = fakeUpstream([says('不应请求')], { models: [model] })
    const result = await runSession({ upstream })
    expect(runEnd(result.events)).toMatchObject({ status: 'error', error: { code: 'context_length' } })
    expect(upstream.requests).toEqual([])
  })

  /*
    ── 有效上下文窗口 ──
    ★ 这一组钉的是「哪个数在管压缩」。协议窗口(会不会被上游 400)和有效窗口
    (自愿的花钱上限)从这次改动起是两条线,而它们只在这里被同时断言。
  */
  it('关着最大上下文时,超大窗口的模型按 272K 计压力', async () => {
    const model: ModelAlias = { ...ALIAS, contextWindow: 1_050_000 }
    const upstream = fakeUpstream([says('好')], { models: [model] })
    const { events } = await runSession({ upstream, request: req({ maxContext: false }) })
    expect(events.find((e) => e.type === 'context_usage')).toMatchObject({ window: 272_000 })
  })

  it('开着最大上下文时放开到模型的协议窗口', async () => {
    const model: ModelAlias = { ...ALIAS, contextWindow: 1_050_000 }
    const upstream = fakeUpstream([says('好')], { models: [model] })
    const { events } = await runSession({ upstream, request: req({ maxContext: true }) })
    expect(events.find((e) => e.type === 'context_usage')).toMatchObject({ window: 1_050_000 })
  })

  it('请求里没有 maxContext(旧队列条目 / 旧 run)按关着走', async () => {
    const model: ModelAlias = { ...ALIAS, contextWindow: 1_050_000 }
    const upstream = fakeUpstream([says('好')], { models: [model] })
    const { events } = await runSession({ upstream })
    expect(events.find((e) => e.type === 'context_usage')).toMatchObject({ window: 272_000 })
  })

  /*
    ★ 一条用例同时钉住两件事:有效窗口真的在管压缩(shouldCompact 起来了),
    协议窗口真的没被开关污染(请求照常发出,没有 context_length)。
    历史约 250K token —— 越过阈值(272K − 20K 摘要输出预留 − 13K 缓冲 = 239K),
    但离 1.05M 还远得很。(阈值公式换成 CC 那套之后这个数跟着上调,理由同一条:
    要的是「刚好越过阈值」,而不是某个写死的字面量。)
  */
  it('历史越过 272K 的压缩阈值时要压,但不报 context_length', async () => {
    const model: ModelAlias = { ...ALIAS, contextWindow: 1_050_000 }
    const upstream = fakeUpstream([says('好')], { models: [model] })
    const big = 'x'.repeat(1_000_000)
    const { events } = await runSession({
      upstream,
      request: req({ maxContext: false }),
      history: [userMessage('old', [{ type: 'text', text: big }], 1)]
    })
    expect(events.find((e) => e.type === 'context_usage')).toMatchObject({
      window: 272_000,
      shouldCompact: true
    })
    expect(runEnd(events).error?.code).not.toBe('context_length')
    expect(upstream.requests.length).toBeGreaterThan(0)
  })

  it('开启 Web Search 但模型既无内置搜索也无工具能力时明确失败', async () => {
    const model: ModelAlias = {
      ...ALIAS,
      capabilities: { ...ALIAS.capabilities, tools: false, webSearch: false }
    }
    const upstream = fakeUpstream([says('不应请求')], { models: [model] })
    const result = await runSession({ upstream, request: req({ webSearch: true }) })
    expect(runEnd(result.events).error?.message).toContain('Web Search')
    expect(upstream.requests).toEqual([])
  })
})

// ─────────────────────────── 转录不变式 ───────────────────────────

describe('转录不变式', () => {
  /** message_commit 是落盘边界(方案 §4.2),而 history 是同一份东西 */
  it('history 与 message_commit 事件一一对应', async () => {
    const { events, history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    // ★ 一一对应,**包括首条用户输入** —— 它也走 commit。渲染层的转录是
    // 事件流的投影,漏掉它用户就看不见自己刚发的那句话。
    expect(commits(events).map((m) => m.id)).toEqual(history.map((m) => m.id))
  })

  /** 转录要过 IPC 的结构化克隆 —— 带闭包就在那里炸,而那时离这里很远 */
  it('转录可结构化克隆', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo', '{"m":"hi"}'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    expect(() => structuredClone(history)).not.toThrow()
  })

  it('每条消息都有非空 parts', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    for (const m of history) expect(m.parts.length, m.id).toBeGreaterThan(0)
  })

  it('消息 id 互不相同', async () => {
    const { history } = await runSession({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      tools: registry({ internalId: 'echo' })
    })
    expect(new Set(history.map((m) => m.id)).size).toBe(history.length)
  })
})

// ─────────────────────────── 插话 ───────────────────────────

describe('插话', () => {
  /**
   * 「用户在工具跑着的时候点了插话」—— 这是这套机制唯一真实的时序。
   * 用工具的 execute 当注入时机,是因为它正好落在**一轮之内**:
   * 上游已经答完、工具结果还没落盘,与用户盯着转圈的那几秒完全对应。
   */
  async function runWithInterjection(o: {
    upstream: FakeUpstream
    items: (handle: RunHandle) => InterjectItem[]
  }): Promise<Ran> {
    const request = req()
    const handle = new RunHandle(request)
    const tools = registry({
      internalId: 'echo',
      execute: (input) => {
        handle.setInterject(o.items(handle))
        return Promise.resolve(toolOk(JSON.stringify(input)))
      }
    })
    const session = new AgentSession(
      { host: quietHost(), upstream: o.upstream, tools, workspaceRoot: '/ws' },
      handle,
      request
    )
    const events = collect(handle)
    await session.run()
    return { events: await events, history: session.history, handle, upstream: o.upstream }
  }

  const TEXT: InterjectItem[] = [{ id: 'q1', parts: [{ type: 'text', text: '先看缓存命中率' }] }]

  /**
   * ★★ 本组的核心断言:插话**在同一个 run 里**被发出去了。
   *
   * 改造前 promote 是纯本地排序,这条消息要等整个 run 跑完才进第二个 run ——
   * 用户点了「插话」却什么都没发生,正是那个 bug 的样子。
   */
  it('在下一轮请求里带上插话消息', async () => {
    const { upstream } = await runWithInterjection({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      items: () => TEXT
    })
    const second = upstream.requests[1]
    expect(second).toBeDefined()
    expect(second?.messages.some((m) => m.id === 'q1')).toBe(true)
  })

  /**
   * ★ 顺序是**硬约束**,不是美观问题:用户消息插在 tool_call 与 tool_result
   * 之间的话,Anthropic 会以「tool_result 必须紧邻」返回 400。
   */
  it('插在工具结果之后,而不是 tool_call 与 tool_result 之间', async () => {
    const { history } = await runWithInterjection({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      items: () => TEXT
    })
    const ids = history.map((m) => m.id)
    const injected = ids.indexOf('q1')
    const toolResult = history.findIndex((m) =>
      m.parts.some((p) => p.type === 'tool_result' && p.callId === 'c1')
    )
    expect(injected).toBeGreaterThan(-1)
    expect(injected).toBe(toolResult + 1)
  })

  /** id 相等是渲染层收敛队列的唯一判据(见 shared/agent/interject.ts) */
  it('用条目自己的 id 作为消息 id,并提交给渲染层', async () => {
    const { events } = await runWithInterjection({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      items: () => TEXT
    })
    const injected = commits(events).find((m) => m.id === 'q1')
    expect(injected?.role).toBe('user')
  })

  /**
   * ★ 读取即消费。留一份在信箱里,下一个轮次边界会把同一条消息再注入一遍 ——
   * 两条 id 相同的用户消息进同一份转录,上游看到的是自相矛盾的历史。
   */
  it('只注入一次', async () => {
    // 三轮工具调用 = 三个轮次边界,但只在第一次 execute 时投信
    let delivered = false
    const { history, handle } = await runWithInterjection({
      upstream: fakeUpstream([
        callsTool('c1', 'echo'),
        callsTool('c2', 'echo'),
        says('好')
      ]),
      items: () => {
        if (delivered) return []
        delivered = true
        return TEXT
      }
    })
    expect(history.filter((m) => m.id === 'q1')).toHaveLength(1)
    expect(handle.takeInterject()).toEqual([])
  })

  /** 空 parts 提交上去,下一轮就是 `all messages must have non-empty content` */
  it('丢弃 parts 为空的条目', async () => {
    const { history } = await runWithInterjection({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      items: () => [{ id: 'q-empty', parts: [] }]
    })
    expect(history.some((m) => m.id === 'q-empty')).toBe(false)
  })

  /** 多条按投递顺序注入 —— 用户排的顺序就是他要说话的顺序 */
  it('多条插话按顺序注入', async () => {
    const { history } = await runWithInterjection({
      upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
      items: () => [
        { id: 'q1', parts: [{ type: 'text', text: '一' }] },
        { id: 'q2', parts: [{ type: 'text', text: '二' }] }
      ]
    })
    const ids = history.map((m) => m.id)
    expect(ids.indexOf('q1')).toBeLessThan(ids.indexOf('q2'))
  })
})

/**
 * 用户拖/选进来的文件引用,路径写成什么形式发给模型。
 *
 * ★ 测的是**两个入口给出同一种写法**:工作区内 → `src/a.ts`,工作区外 → 绝对路径。
 * 不一致的后果是模型把同一个文件当成两个 —— 它从 grep 回执里看到 `src/a.ts`,
 * 从附件里看到 `/Users/…/src/a.ts`,于是读两遍、改两遍。
 *
 * 用真实临时目录而不是 `/ws`:判定要经过 realpath(macOS 上 `/var` 与 `/private/var`),
 * 假根一律判成「工作区外」,那样这组测试会全绿地什么都没测到。
 */
describe('file_ref 路径归一化', () => {
  it('does not treat a client or foreign server reference as a remote workspace file', () => {
    const request = req({ input: [{ type: 'file_ref', path: '/project/file.txt', name: 'file.txt', source: { kind: 'local' } }] })
    const source = { kind: 'workspace' as const, workspaceId: request.workspaceId, environment: { kind: 'connection' as const, connectionId: 'server-a' }, rootPath: '/project', connectionRevision: 1 }
    const deps: SessionDeps = { host: quietHost(), upstream: fakeUpstream([says('ok')]), tools: registry(), workspaceRoot: '/project', fileReferenceSource: source }
    expect(() => new AgentSession(deps, new RunHandle(request), request)).toThrow('conflict')
    const foreign = { ...request, input: [{ type: 'file_ref' as const, path: '/project/file.txt', name: 'file.txt', source: { ...source, environment: { kind: 'connection' as const, connectionId: 'server-b' } } }] }
    expect(() => new AgentSession(deps, new RunHandle(foreign), foreign)).toThrow('conflict')
    const matching = { ...request, input: [{ type: 'file_ref' as const, path: '/project/file.txt', name: 'file.txt', source }] }
    expect(() => new AgentSession(deps, new RunHandle(matching), matching)).not.toThrow()
  })

  /**
   * ★ 历史与新鲜输入的分界:新鲜输入对不上 → 抛(上面那条);历史对不上 → 隔离,不抛。
   *
   * 历史是已经发生的事实,会话可能在两台服务器之间改过绑定。在构造函数里抛的后果是
   * 整条会话永久卡死 —— 每一次 run 都在同一处抛,除了手改历史没有修复入口。
   */
  it('★ 历史里对不上的引用不会让整条会话永久卡死', () => {
    const request = req({ input: [{ type: 'text', text: '继续' }] })
    const source = { kind: 'workspace' as const, workspaceId: request.workspaceId, environment: { kind: 'connection' as const, connectionId: 'server-a' }, rootPath: '/srv/app', connectionRevision: 1 }
    const stale = { ...source, environment: { kind: 'connection' as const, connectionId: 'server-b' } }
    const history = [userMessage('h1', [{ type: 'file_ref', path: 'src/a.ts', name: 'a.ts', source: stale }], 0)]
    const workspace = { ...localEnvironment(nodeHost(), '/srv/app'), remote: true }
    const deps: SessionDeps = { host: quietHost(), upstream: fakeUpstream([says('ok')]), tools: registry(),
      workspaceRoot: '/srv/app', fileReferenceSource: source, workspace, history }
    expect(() => new AgentSession(deps, new RunHandle(request), request)).not.toThrow()
  })

  /**
   * ★ 这条盯的是**静默**的那一侧:本地 run 原先完全不校验历史引用。
   *
   * 历史引用早已被归一成工作区相对写法,所以 server-a 的 `src/a.ts` 进本地 run 后
   * 指向本机同名文件。同一套部署目录在两边都存在时,模型读到一个无关文件,且没有
   * 任何一处报错 —— 正是「两台服务器上存在相同路径」那个场景。
   */
  it('★ 历史里来自远端的引用,不会以本机同名文件的身份进入模型上下文', async () => {
    const request = req({ input: [{ type: 'text', text: '继续' }] })
    const handle = new RunHandle(request)
    const upstream = fakeUpstream([says('好')])
    const remote = { kind: 'workspace' as const, workspaceId: request.workspaceId, environment: { kind: 'connection' as const, connectionId: 'server-a' }, rootPath: '/srv/app', connectionRevision: 1 }
    const history = [userMessage('h1', [{ type: 'file_ref', path: 'src/a.ts', name: 'a.ts', source: remote }], 0)]
    const session = new AgentSession({ host: quietHost(), upstream, tools: registry(), workspaceRoot: root, history }, handle, request)
    await session.run()
    const parts = (upstream.requests[0]?.messages ?? []).flatMap((message) => message.parts)
    expect(parts.some((part) => part.type === 'file_ref'), '远端引用不得带着可用路径进入上下文').toBe(false)
    expect(parts.some((part) => part.type === 'text' && part.text.includes('a.ts') && part.text.includes('different workspace environment'))).toBe(true)
  })

  let base = ''
  let root = ''
  let outside = ''

  beforeAll(() => {
    base = mkdtempSync(join(tmpdir(), 'nextcowork-fileref-'))
    root = join(base, 'ws')
    outside = join(base, 'outside')
    mkdirSync(join(root, 'src'), { recursive: true })
    mkdirSync(outside, { recursive: true })
    writeFileSync(join(root, 'src', 'a.ts'), '')
    writeFileSync(join(outside, 'notes.md'), '')
  })

  afterAll(() => {
    rmSync(base, { recursive: true, force: true })
  })

  function runIn(input: ContentPart[]): Promise<Ran> {
    const request = req({ input })
    const handle = new RunHandle(request)
    const upstream = fakeUpstream([says('好')])
    const session = new AgentSession(
      { host: quietHost(), upstream, tools: registry(), workspaceRoot: root },
      handle,
      request
    )
    const events = collect(handle)
    return session.run().then(async () => ({
      events: await events,
      history: session.history,
      handle,
      upstream
    }))
  }

  const refs = (ms: readonly AgentMessage[]): ContentPart[] =>
    partsOf(ms).filter((p) => p.type === 'file_ref')

  it('★ 工作区内的文件 → 工作区相对路径,与工具回执同一种写法', async () => {
    const { history } = await runIn([
      { type: 'file_ref', path: join(root, 'src', 'a.ts'), name: 'a.ts' }
    ])
    expect(refs(history)).toEqual([{ type: 'file_ref', path: 'src/a.ts', name: 'a.ts' }])
  })

  it('★ 工作区外的文件 → 绝对路径,模型没有别的办法定位它', async () => {
    const { history } = await runIn([
      { type: 'file_ref', path: join(outside, 'notes.md'), name: 'notes.md' }
    ])
    const [ref] = refs(history)
    expect(ref).toMatchObject({ type: 'file_ref', name: 'notes.md' })
    expect(ref?.type === 'file_ref' && ref.path.endsWith('/outside/notes.md')).toBe(true)
    expect(ref?.type === 'file_ref' && ref.path.startsWith('/')).toBe(true)
  })

  it('归一化后的路径就是发给模型的那一个(转录与请求不分叉)', async () => {
    const { upstream } = await runIn([
      { type: 'file_ref', path: join(root, 'src', 'a.ts'), name: 'a.ts' }
    ])
    const sent = upstream.requests[0]?.messages.flatMap((m) => m.parts) ?? []
    expect(sent).toContainEqual({ type: 'file_ref', path: 'src/a.ts', name: 'a.ts' })
  })

  it('其他 part 一个字节都不动', async () => {
    const { history } = await runIn([
      { type: 'text', text: `看看 ${join(root, 'src', 'a.ts')}` },
      { type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/sessions/S1/X.png' }
    ])
    expect(partsOf(history)[0]).toEqual({ type: 'text', text: `看看 ${join(root, 'src', 'a.ts')}` })
    expect(partsOf(history)[1]).toEqual({
      type: 'image',
      mime: 'image/png',
      dataRef: 'ncw://attachments/sessions/S1/X.png'
    })
  })

  /** ★ 生成中排队的那条走的是另一个提交点 —— 它漏掉的话两个入口又不一致了 */
  it('插队进来的消息同样归一化', async () => {
    const request = req()
    const handle = new RunHandle(request)
    const tools = registry({
      internalId: 'echo',
      execute: (input) => {
        handle.setInterject([
          {
            id: 'q1',
            parts: [{ type: 'file_ref', path: join(root, 'src', 'a.ts'), name: 'a.ts' }]
          }
        ])
        return Promise.resolve(toolOk(JSON.stringify(input)))
      }
    })
    const session = new AgentSession(
      {
        host: quietHost(),
        upstream: fakeUpstream([callsTool('c1', 'echo'), says('好')]),
        tools,
        workspaceRoot: root
      },
      handle,
      request
    )
    await session.run()

    const injected = session.history.find((m) => m.id === 'q1')
    expect(injected?.parts).toEqual([{ type: 'file_ref', path: 'src/a.ts', name: 'a.ts' }])
  })
})

/**
 * 重开一段压缩过的会话:上下文从**转录里最后一条边界**切起。
 *
 * ★ 原先这里钉的是「恢复检查点时按 source 分流」—— 机械压缩也落检查点,构造函数
 * 那句「取 windowIndex 最大的」会捞到它,于是一句「折叠了 N 条消息」被当成对话摘要
 * 送给模型。检查点表已随重写删除,那条风险**在结构上消失了**:边界消息自带摘要正文,
 * 没有第二种东西可捞。
 *
 * 现在要守的是另一条,而且同样零报错:边界之前的历史绝不能再进请求。
 * 漏了的话界面画着分隔线、请求里却还是全量 —— 也就是这次重写要修的那个病。
 */
describe('压缩过的会话从边界切起', () => {
  function boundaryMessage(id: string, summary: string): AgentMessage {
    const parts: ContentPart[] = [
      { type: 'compact_boundary', trigger: 'auto', preTokens: 190_000, postTokens: 9_000, summary },
      { type: 'text', text: `之前对话的摘要:${summary}` }
    ]
    return { ...userMessage(id, parts, 0), internal: true, parts }
  }

  async function assembled(history: readonly AgentMessage[]): Promise<string> {
    const request = req()
    const handle = new RunHandle(request)
    const upstream = fakeUpstream([says('ok')])
    const session = new AgentSession(
      { host: quietHost(), upstream, tools: registry(), workspaceRoot: '/ws', history },
      handle,
      request
    )
    await session.run()
    return JSON.stringify(upstream.requests[0]?.messages ?? [])
  }

  const EARLY: readonly AgentMessage[] = [
    userMessage('h1', [{ type: 'text', text: '第一阶段:重构登录模块' }], 0),
    assistantMessage('h2', [{ type: 'text', text: '好的' }], 0)
  ]

  it('摘要正文进请求', async () => {
    expect(await assembled([...EARLY, boundaryMessage('c1', '用户在重构登录模块')]))
      .toContain('用户在重构登录模块')
  })

  it('★ 边界之前的历史一条都不进请求', async () => {
    const sent = await assembled([...EARLY, boundaryMessage('c1', '用户在重构登录模块')])
    expect(sent).not.toContain('第一阶段:重构登录模块')
  })

  /** 压过两次时以**最后**一条为准 —— 第一段摘要已经被第二次压缩读进去了。 */
  it('★ 压过两次时只从最后一条边界切起', async () => {
    const sent = await assembled([
      ...EARLY,
      boundaryMessage('c1', '第一次压缩的摘要'),
      userMessage('h3', [{ type: 'text', text: '第二阶段' }], 0),
      boundaryMessage('c2', '第二次压缩的摘要')
    ])
    expect(sent).toContain('第二次压缩的摘要')
    expect(sent).not.toContain('第一次压缩的摘要')
    expect(sent).not.toContain('第二阶段')
  })

  /** 没压过的会话必须逐字退回旧行为:整段历史照发。 */
  it('没有边界时整段历史照发', async () => {
    expect(await assembled(EARLY)).toContain('第一阶段:重构登录模块')
  })
})

/**
 * 断流自动续跑(`RESUME_DELAYS_MS` / `canResume`)。
 *
 * 背景:`UpstreamRouter` 只在**还没吐第一个内容字节之前**重试或切换供应商
 * (router.ts §5.3 那条 `sawContent` 边界)。一旦流吐到一半被掐断,它把错误原样交上来,
 * 而 session 以前对任何 `streamError` 一律 `finish('error')` —— 整个 run 当场死掉。
 * 主代理死了还有人能重新提问,**子代理没有人**:`Task` 直接把失败汇报给父代理。
 *
 * 这里钉的是两件事:续跑**真的发生**,以及它**只在该发生的时候**发生。
 */
describe('断流自动续跑', () => {
  /** 吐了一半就断:有 message_start、有正文,没有 message_end */
  const cutOff = (text: string, error = agentError('network', '连接「X」失败:net::ERR_CONNECTION_CLOSED')):
    ProviderStreamEvent[] => [
    { type: 'message_start', model: 'claude-sonnet-4' },
    { type: 'text_delta', index: 0, text },
    { type: 'error', error }
  ]

  const texts = (ms: readonly AgentMessage[]): string[] =>
    partsOf(ms).flatMap((part) => (part.type === 'text' ? [part.text] : []))

  it('★ 断了一次就自己重来一轮,断掉那半截不进转录', async () => {
    const upstream = fakeUpstream([cutOff('我先看一眼 con'), says('完整回答')])
    const ran = await runSession({ upstream, resumeDelaysMs: [0] })

    expect(runEnd(ran.events).status).toBe('done')
    // ★★ 半截那一份**一个字都不能留**。留着的话历史会以一条 assistant 消息结尾
    //    (Anthropic prefill 语义 + 尾部空白 400),对话里还多一个割裂的气泡。
    expect(texts(ran.history)).toEqual(['你好', '完整回答'])
    expect(upstream.requests).toHaveLength(2)
    // 两次请求体逐字相同 —— 续跑重发的是**同一个请求**,不是在半截回答上接着写
    expect(JSON.stringify(upstream.requests[0])).toBe(JSON.stringify(upstream.requests[1]))
  })

  /**
   * 续跑期间界面上要有话说。
   *
   * ★ 复用 `provider_retry` 而不是新造事件:主状态行、子代理卡片、转录 reducer
   * 三处都已经在画它。这条用例同时钉住「别改成别的事件类型」——
   * 改了的话那三处会一起静音,而静音的重试和卡死在界面上长得一模一样。
   */
  it('续跑期间发 provider_retry,带着第几次和原因', async () => {
    const upstream = fakeUpstream([cutOff('半截'), cutOff('又半截'), says('第三次才成')])
    const ran = await runSession({ upstream, resumeDelaysMs: [0, 0] })

    const notices = ran.events.flatMap((e) =>
      e.type === 'stream' && e.delta.type === 'provider_retry' ? [e.delta] : [])
    expect(notices.map((n) => n.attempt)).toEqual([1, 2])
    expect(notices[0]?.reason).toContain('ERR_CONNECTION_CLOSED')
    expect(runEnd(ran.events).status).toBe('done')
  })

  /** 退避表用尽就认输 —— 尝试次数正好是 1 + 退避表长度,不多不少 */
  it('★ 续跑次数用尽后照常报错,不无限重来', async () => {
    // fakeUpstream 的脚本用尽会重复最后一段,所以这一段就是每一次的结局
    const upstream = fakeUpstream([cutOff('每次都断')])
    const ran = await runSession({ upstream, resumeDelaysMs: [0, 0, 0] })

    expect(runEnd(ran.events).status).toBe('error')
    expect(runEnd(ran.events).error?.code).toBe('network')
    expect(upstream.requests).toHaveLength(4)
    // 失败那一份的半截正文 + error part 仍然要进转录:重载之后失败还看得见,
    // 不是一个消失了的 toast(见 turn() 里 error part 那段注释)
    expect(texts(ran.history)).toContain('每次都断')
    expect(partsOf(ran.history)).toContainEqual(expect.objectContaining({ type: 'error' }))
  })

  /**
   * ★★ 边界:**只认 `code === 'network'`**。
   *
   * 放宽到全部 `retryable` 的话,`rate_limit` 和 5xx 会在 router 那条**信息更全**的
   * 重试路(认 `Retry-After`、会切 provider、有健康表)外面再套一个盲循环,
   * 最坏情况是 5 × 3 次尝试的长时间空转。
   */
  it.each([
    ['auth', agentError('auth', '密钥无效')],
    ['provider 4xx', agentError('provider', '请求不合法', { status: 400 })],
    ['rate_limit(router 已经按 Retry-After 退过了)', agentError('rate_limit', '超出速率限制')]
  ])('%s 一次都不续跑', async (_label, error) => {
    const upstream = fakeUpstream([cutOff('半截', error), says('不该被请求到')])
    const ran = await runSession({ upstream, resumeDelaysMs: [0, 0, 0, 0, 0] })

    expect(runEnd(ran.events).status).toBe('error')
    expect(upstream.requests).toHaveLength(1)
  })

  /** `resumeDelaysMs: []` = 关掉续跑。这是旧行为,也是本文件其余用例的默认档。 */
  it('退避表为空时保持原来的行为', async () => {
    const upstream = fakeUpstream([cutOff('半截'), says('不该被请求到')])
    const ran = await runSession({ upstream, resumeDelaysMs: [] })

    expect(runEnd(ran.events).status).toBe('error')
    expect(upstream.requests).toHaveLength(1)
  })

  /**
   * ★ 停止按钮要能穿透退避睡眠。
   *
   * 没有这条的话,用户在一次 45 秒的退避里按停止会等满 45 秒 ——
   * 而「按了没反应」正是这整轮改动想根除的那种体感。
   */
  it('★ 退避睡眠中被中断:立刻收尾,不再发第二次请求', async () => {
    const upstream = fakeUpstream([cutOff('半截')])
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession({
      host: quietHost(), upstream, tools: registry(), workspaceRoot: '/ws',
      resumeDelaysMs: [10_000]
    }, handle, request)
    const running = session.run()

    await vi.waitFor(() => expect(upstream.requests).toHaveLength(1), { timeout: 200, interval: 1 })
    handle.abort({ by: 'user' })
    await running

    expect(handle.status).toBe('aborted')
    expect(upstream.requests).toHaveLength(1)
  })
})

/**
 * 自动压缩(Claude Code 式)。
 *
 * 背景是一个线上故障:圆环上写着「211K / 200K,已超出」,自动压缩却一次都没触发,
 * 一路涨到 624K。两个数不同源 —— 圆环读上游在 `message_end` 里报回的真值,判据读
 * 发出去之前的 chars/4 本地估算。代码 / JSON / 工具输出的真实分词接近 3 chars/token,
 * 于是同一份请求在判据里只有 ~152K,恰好压在旧阈值之下,而 `validateModelRuntime`
 * 的 context_length 硬校验读的也是这个偏低的数,所以连报错都不会有。
 *
 * 现在判据读 `knownInputTokens` = **上一次上游真值 + 其后新增消息的估算**
 * (`AgentSession.contextTokens`,同 CC 的 `tokenCountWithEstimation`),真值取的是
 * `promptTokensOf`(含 cache_read / cache_creation —— Anthropic 的 `input_tokens`
 * 不含它们,只看它的话一段被缓存住的长对话会显示成「几乎没占上下文」)。
 *
 * 这一组钉四件事:真值**回流**并纠正了判据、压缩**真的**把历史切掉了、
 * 摘要请求**和正文同模型**、以及失败时**能熔断**。
 */
describe('自动压缩', () => {
  const AUTO_COMPACT = { autoCompact: true }
  /** 估算约 100K —— 阈值(200K − 8192 − 13K ≈ 178.8K)的一半多点,不靠真值就不会压。 */
  const BIG = 'x'.repeat(400_000)
  const NOTE = '## 意图\n用户在重构登录模块'
  /** 「换个便宜模型压缩」的那一条绑定。 */
  const CHEAP: ModelAlias = {
    alias: 'cheap-model', providerId: 'p2', upstreamModel: 'cheap-model',
    capabilities: { tools: true, vision: false, thinking: false, caching: false },
    contextWindow: 200_000, maxOutputTokens: 8192
  }
  /**
   * `gpt-6-*` 那一族:effort 模型,`reasoningEfforts` 里**没有 `none`** ——
   * 也就是「关不掉推理」的那一类,本次修的 bug 就发生在它身上。
   */
  const GPT6: ModelAlias = {
    alias: 'gpt-6-astra', providerId: 'p3', upstreamModel: 'gpt-6-astra',
    capabilities: { tools: true, vision: true, thinking: true, caching: true },
    contextWindow: 200_000, maxOutputTokens: 8192,
    thinkingConfig: { mode: 'effort', defaultEnabled: true },
    reasoningEfforts: ['low', 'medium', 'high', 'xhigh', 'max']
  }

  const ends = (inputTokens: number, stopReason: 'end_turn' | 'tool_use'): ProviderStreamEvent => ({
    type: 'message_end',
    stopReason,
    usage: { inputTokens, outputTokens: 5 }
  })

  /** 调一次工具(好让循环进入第二轮),并让上游报回指定的真实输入用量。 */
  const turnReporting = (inputTokens: number, callId: string): ProviderStreamEvent[] => [
    { type: 'message_start', model: 'claude-sonnet-4' },
    { type: 'tool_call_start', index: 0, callId, name: 'echo' },
    { type: 'tool_call_delta', index: 0, callId, argsDelta: '{}' },
    { type: 'tool_call_end', index: 0, callId },
    ends(inputTokens, 'tool_use')
  ]

  /**
   * 摘要请求单独应答的假上游 —— 和正文请求的区别只在 `system`。
   * `fail` 让摘要请求返回一个错误,用来逼出熔断那一条。
   */
  function withSummaryUpstream(
    turns: ProviderStreamEvent[][],
    opts: { note?: string; fail?: boolean; model?: ModelAlias } = {}
  ): FakeUpstream & { summaries: CanonicalRequest[] } {
    // 配了压缩模型的用例要让路由器**查得到**那条绑定,否则会走回落那一支。
    const base = fakeUpstream(turns, opts.model === undefined ? {} : { models: [ALIAS, opts.model] })
    const summaries: CanonicalRequest[] = []
    return {
      ...base,
      summaries,
      async *stream(r, signal, context): AsyncIterable<ProviderStreamEvent> {
        if (r.system === COMPACT_SYSTEM) {
          summaries.push(r)
          yield { type: 'message_start', model: 'claude-sonnet-4' }
          if (opts.fail === true) {
            yield { type: 'error', error: agentError('provider', 'summary failed', { retryable: true }) }
            return
          }
          yield { type: 'text_delta', index: 0, text: opts.note ?? NOTE }
          yield { type: 'message_end', stopReason: 'end_turn', usage: { inputTokens: 10, outputTokens: 5 } }
          return
        }
        yield* base.stream(r, signal, context)
      }
    }
  }

  function historyWithBlob(): AgentMessage[] {
    return [
      userMessage('h0', [{ type: 'text', text: '最初的任务' }], 0),
      assistantMessage('h1', [{ type: 'tool_call', callId: 'c0', name: 'echo', input: {} }], 0),
      userMessage('h2', [{ type: 'tool_result', callId: 'c0', output: { content: BIG }, isError: false }], 0),
      ...Array.from({ length: 6 }, (_, i) =>
        i % 2 === 0
          ? assistantMessage(`h${i + 3}`, [{ type: 'text', text: `回应 ${i}` }], 0)
          : userMessage(`h${i + 3}`, [{ type: 'text', text: `追问 ${i}` }], 0)
      )
    ]
  }

  async function run(o: {
    upstream: FakeUpstream
    history: readonly AgentMessage[]
    contextManagement?: SessionDeps['contextManagement']
    compaction?: SessionDeps['compaction']
    workspaceCompaction?: SessionDeps['workspaceCompaction']
  }): Promise<Ran> {
    const request = req()
    const handle = new RunHandle(request)
    const session = new AgentSession(
      {
        host: quietHost(),
        upstream: o.upstream,
        tools: registry({ internalId: 'echo' }),
        workspaceRoot: '/ws',
        history: o.history,
        /*
          本组的数值夹具(「估算约 100K,阈值 ≈178.8K」)按 **8192 的输出预留**标定,
          所以这里把设置项钉在模型原来的协议上限上。不钉的话默认 32K 预留会让
          几条大历史的用例在 `validateModelRuntime` 那道硬校验上先行退出 ——
          一个请求都发不出去,而本组要测的是压缩,不是那道校验。
        */
        maxOutputTokens: 8192,
        contextManagement: o.contextManagement ?? AUTO_COMPACT,
        ...(o.compaction === undefined ? {} : { compaction: o.compaction }),
        ...(o.workspaceCompaction === undefined ? {} : { workspaceCompaction: o.workspaceCompaction }),
        resumeDelaysMs: []
      },
      handle,
      request
    )
    const events = collect(handle)
    await session.run()
    return { events: await events, history: session.history, handle, upstream: o.upstream }
  }

  const statuses = (events: AgentEvent[]): string[] =>
    events.flatMap((e) => (e.type === 'context_status' ? [e.status.phase] : []))
  const sent = (upstream: FakeUpstream, turn: number): string =>
    JSON.stringify(upstream.requests[turn]?.messages ?? [])

  /**
   * ★★ 本组的主用例 —— 就是那个截图里的场景。
   *
   * 第一轮:本地估 ~100K,判据说还宽裕,请求照发;上游回报这一份其实是 200K。
   * 第二轮:同样估 ~100K,但判据这时读的是真值 200K,超过阈值 178.8K,压缩触发。
   */
  it('★ 上游报回的真值远高于估算时,下一轮必须触发自动压缩', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    const { events } = await run({ upstream, history: historyWithBlob() })

    // 第一轮没压:大块原样发出去了(这也是「不读真值就不压」的反面证据)
    expect(sent(upstream, 0)).toContain(BIG)
    expect(upstream.summaries.length).toBe(1)
    expect(statuses(events)).toEqual(['compacting', 'compacted'])
  })

  /**
   * ★ 压缩**真的**把历史切掉了 —— 这是整次重写的目的:边界之后的请求里只剩摘要。
   * 原先机械压缩只把工具输出换成占位符,骨架和所有消息头都还在,省不下几个 token。
   */
  it('★ 压完之后早期历史不再进请求,只剩摘要', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    await run({ upstream, history: historyWithBlob() })

    const after = sent(upstream, upstream.requests.length - 1)
    expect(after).toContain('用户在重构登录模块')
    expect(after).not.toContain(BIG)
    expect(after).not.toContain('最初的任务')
    // 切掉整轮之后不能留下孤儿 tool_use —— 那是下一轮 400 的唯一来源
    expect(orphanedToolCalls((upstream.requests.at(-1)?.messages ?? []) as AgentMessage[])).toEqual([])
  })

  /** ★ 边界消息进转录并落盘,否则重开会话就回到压缩前,而界面上什么都看不到。 */
  it('★ 压缩产出一条带 compact_boundary 的消息,留在转录里', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    const { history } = await run({ upstream, history: historyWithBlob() })

    const boundary = history.flatMap((m) => m.parts).find((p) => p.type === 'compact_boundary')
    expect(boundary).toMatchObject({ trigger: 'auto', summary: NOTE })
    // 完整转录仍在 —— 压缩改变的是「发给模型的范围」,不是聊天记录
    expect(history.some((m) => m.parts.some((p) => p.type === 'tool_result'))).toBe(true)
  })

  /**
   * ★ 摘要**默认**和正文走同一家、同一个模型:它读的是同一段对话,
   * 漂到另一家既换了口径也换了账单。只有用户显式配了压缩模型才会换(见下面两条)。
   */
  it('★ 摘要请求与正文同模型,且不带工具', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    await run({ upstream, history: historyWithBlob() })

    expect(upstream.summaries[0]?.model).toBe('claude-sonnet-4')
    expect(upstream.summaries[0]?.tools).toEqual([])
  })

  /**
   * ★★ 本次改动的核心回归:配置了压缩模型时,**只有摘要**漂过去,正文仍发会话模型。
   * 两者搞混的表现是「我只是想让压缩便宜点,结果整条对话都换模型了」。
   */
  it('★★ 配置了压缩模型时,摘要发给它、正文仍发会话模型', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')], { model: CHEAP })
    await run({
      upstream,
      history: historyWithBlob(),
      compaction: { model: 'cheap-model', modelProviderId: 'p2', thinking: INHERIT_THINKING }
    })

    expect(upstream.summaries[0]?.model).toBe('cheap-model')
    expect(upstream.summaries[0]?.modelProviderId).toBe('p2')
    expect(upstream.requests.every((r) => r.model === 'claude-sonnet-4')).toBe(true)
  })

  /**
   * ★★ 这条盯的就是用户报的那个 bug。
   *
   * 压缩模型是 `gpt-6-*` 那一族(effort 模型,`reasoningEfforts` 里没有 `none`)时,
   * 过去这里硬发 `thinkingLevel: 'off'` —— `thinking-adapter` 对这种组合直接抛
   * 「该模型不支持关闭推理」,router 包成不可重试的错,压缩每次必败、三次后熔断,
   * 上下文再也压不下去(全程零报错)。现在档位按压缩模型归一化到最低可用档。
   */
  it('★★ 压缩模型关不掉推理时,下发的是它认的最低档而不是 off', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')], { model: GPT6 })
    const { events } = await run({
      upstream,
      history: historyWithBlob(),
      compaction: { model: 'gpt-6-astra', modelProviderId: 'p3', thinking: 'off' }
    })

    expect(upstream.summaries[0]?.thinkingLevel).toBe('low')
    expect(statuses(events)).toEqual(['compacting', 'compacted'])
  })

  /**
   * ★ 过期配置(供应商被删 / 别名改名)**不能把压缩打死** —— 那正是
   * 「上下文一路涨到上游报超长」的老症状。静默回落会话模型,并在边界上留个记号。
   */
  it('★ 配置的压缩模型解析不到时回落会话模型,压缩照常成功', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    const { events, history } = await run({
      upstream,
      history: historyWithBlob(),
      compaction: { model: 'deleted-alias', modelProviderId: 'gone', thinking: INHERIT_THINKING }
    })

    expect(upstream.summaries[0]?.model).toBe('claude-sonnet-4')
    expect(statuses(events)).toEqual(['compacting', 'compacted'])
    const boundary = history.flatMap((m) => m.parts).find((p) => p.type === 'compact_boundary')
    expect(boundary).toMatchObject({ summaryModel: { model: 'claude-sonnet-4', fellBack: true } })
  })

  /** 工作区那一档压过全局 —— 圆环菜单里那两栏存在的全部理由。 */
  it('工作区的覆盖压过全局设置', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')], { model: CHEAP })
    await run({
      upstream,
      history: historyWithBlob(),
      compaction: { model: 'cheap-model', modelProviderId: 'p2', thinking: INHERIT_THINKING },
      // null = 这个工作区显式退回会话模型
      workspaceCompaction: { compactModel: null }
    })

    expect(upstream.summaries[0]?.model).toBe('claude-sonnet-4')
  })

  /**
   * ★ 反面:上游报回来的数**小于**估算时,判据取较大的那个,逐字退回纯估算。
   *
   * 不少中转按「未命中缓存的那部分」报 `input_tokens` 且不给 `cache_read`。
   * 照单全收的话,自动压缩会被这种上游整个关掉 —— 正是我们在修的那个故障,
   * 从另一头再进来一次。
   */
  it('★ 上游报回的真值偏小时不放松判据', async () => {
    const upstream = withSummaryUpstream([turnReporting(10, 'c1'), says('好')])
    const { events } = await run({ upstream, history: historyWithBlob() })

    expect(upstream.summaries.length).toBe(0)
    expect(statuses(events)).toEqual([])
  })

  /** 一次请求都还没完成时没有真值可用 —— 第一轮的判据必须就是纯估算。 */
  it('第一轮没有真值可用,判据即纯估算', async () => {
    const upstream = withSummaryUpstream([says('好')])
    const { events } = await run({ upstream, history: historyWithBlob() })

    expect(events.find((e) => e.type === 'context_usage')).toMatchObject({ shouldCompact: false })
    expect(sent(upstream, 0)).toContain(BIG)
  })

  /** 总闸关掉就一次都不压 —— 哪怕判据为真。 */
  it('autoCompact 关掉时不压缩', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    const { events } = await run({
      upstream, history: historyWithBlob(), contextManagement: { autoCompact: false }
    })

    expect(upstream.summaries.length).toBe(0)
    expect(statuses(events)).toEqual([])
  })

  /**
   * ★★ 收敛:摘要请求确定性失败时必须**熔断**。
   *
   * 不封顶的话(模型没权限、摘要恒为空、上游长期 5xx),判据每一轮都为真,
   * 于是每一轮白发一次摘要请求 —— 界面上全程正常,只有账单在涨。
   * 这里让摘要请求一直失败,断言它最多试到上限,之后相位变 `disabled`。
   */
  it('★★ 摘要连续失败到上限后熔断,不再重试', async () => {
    /*
      ★ 这里的真值取 185K 而不是 200K:压缩失败之后这一轮**照发**,而 200K + 8192 的
      输出预留会越过 200K 的协议窗口,被 `validateModelRuntime` 当场判 `context_length`,
      run 死在第二轮 —— 那样熔断计数永远数不到 3,这条用例测的就不是它要测的东西了。
      185K 在阈值(≈178.8K)之上、硬校验(≈191.8K)之下,正好是「该压、压不动、但还发得出去」。
    */
    const upstream = withSummaryUpstream(
      [turnReporting(185_000, 'c1'), turnReporting(185_000, 'c2'), turnReporting(185_000, 'c3'),
       turnReporting(185_000, 'c4'), says('好')],
      { fail: true }
    )
    const { events } = await run({ upstream, history: historyWithBlob() })

    expect(upstream.summaries.length).toBe(MAX_CONSECUTIVE_COMPACT_FAILURES)
    expect(statuses(events).at(-1)).toBe('disabled')
  })

  /** 熔断之后判据本身不变 —— 压力条照常报「该压了」,只是我们不再空转。 */
  it('熔断之后 context_usage 仍然如实报 shouldCompact', async () => {
    const upstream = withSummaryUpstream(
      [turnReporting(185_000, 'c1'), turnReporting(185_000, 'c2'), turnReporting(185_000, 'c3'), says('好')],
      { fail: true }
    )
    const { events } = await run({ upstream, history: historyWithBlob() })

    expect(events.filter((e) => e.type === 'context_usage').at(-1)).toMatchObject({ shouldCompact: true })
  })

  /**
   * ★★ 相位要**活过转录 reducer**:界面上看到的是 reducer 的产物,不是 session 发了什么。
   * 只断言 session 发出去的那一条的话,reducer 里哪天多一句覆盖它照样绿。
   */
  it('★★ compacted 穿过转录 reducer 之后清掉压缩前的读数', async () => {
    const upstream = withSummaryUpstream([turnReporting(200_000, 'c1'), says('好')])
    const { events } = await run({ upstream, history: historyWithBlob() })

    const projected = applyEvents(emptyTranscript(), events)
    expect(projected.contextStatus?.phase).toBe('compacted')
    /*
      ★ 压缩前那次请求报回来的 200K 必须**不在**了,否则圆环会一直写着「已超出」。
      这里读到的 10 是压缩**之后**那一轮上游报回来的真值 —— 正是我们想让用户看到的数。
    */
    expect(projected.lastInputTokens).toBe(10)
  })
})
