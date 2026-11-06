import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WebContents } from 'electron'
import type { AgentEvent } from '../../../shared/agent/event'
import type { RunRequest } from '../../../shared/agent/run-request'
import { applyEvents, emptyTranscript, liveText, type TranscriptState } from '../../../shared/agent/transcript'
import { hasSeqGap, type AgentEventEnvelope } from '../../../shared/ipc/contract'
import { runFake } from '../../kernel/fake-emitter'
import { runs } from '../../kernel/run-registry'
import { windows, type WindowContext } from '../../window/registry'
import { abortRun, activeRunIndex, attachRun, listInteractions, respondInteraction, sessionRuntime, setWindowContentVisible, startRun, unwatchRun } from '../agent'
import { interactions } from '../../kernel/interaction-gate'

/**
 * 合批泵的验收(原步骤 3 的验收),跑在无头 Node 里。
 *
 * 方案 §12 给步骤 3 的完成标志是「React 里看到流式文字滚动」,而它真正证明的是
 * **驱动 → RunHandle → 合批泵 → 信封 → 转录投影**这条链每个接缝都对得上。
 * 那条链上只有最后一跳(structuredClone + React 渲染)需要 Electron,
 * 前面全部可以在这里断言 —— 而 bug 恰恰都在前面。
 *
 * ★ 生产驱动已经换成真 `AgentSession`,所以这里把假发射器作为**显式的第三个参数**
 * 传进 `startRun`。这不是为了绕开真实现,而是因为这份测试量的是**泵**:
 * 合批窗口、seq 连续性、退订后的丢批、attach 重放。拿真 session 来量,
 * 时序就取决于上游怎么切片 —— 断言会变成碰运气,而失败时你还分不清
 * 是泵错了还是上游那一轮恰好只发了一个 delta。
 * 端到端那一半在 `agent-run.test.ts` 里,走的是**默认驱动**。
 *
 * ⚠️ 这个文件与 vitest.config.ts 注释里说的「kernel 零 electron import」是**两回事**:
 * `ipc/agent.ts` 属于 electron 那一侧,它能在这里跑起来是因为
 * `window/registry.ts` 对 electron 只有 `import type`(编译期擦除),
 * 且 `runtime.ts` 整条链也是干净的。哪天有人在那两处任一加一个**值**导入,
 * 挂的会是这个文件,而那是正确的报警。
 */

class FakeWebContents {
  readonly sent: Array<{ channel: string; payload: unknown }> = []
  private destroyed = false
  constructor(readonly id: number) {}
  once(): void {}
  isDestroyed(): boolean {
    return this.destroyed
  }
  send(channel: string, payload: unknown): void {
    this.sent.push({ channel, payload })
  }

  /**
   * WindowRegistry 判「帧还活不活」靠的是这里,不是 send 抛不抛 ——
   * 真实的 `webFrameMain.send` 自己把异常吞了(见 window/registry.ts)。
   */
  get mainFrame(): { isDestroyed: () => boolean; detached: boolean; send: (channel: string, payload: unknown) => void } {
    return {
      isDestroyed: () => this.isDestroyed(),
      detached: false,
      send: (channel, payload) => this.send(channel, payload)
    }
  }
  destroy(): void {
    this.destroyed = true
  }
  /** 只取 agent:event 的信封,按到达顺序 */
  envelopes(): AgentEventEnvelope[] {
    return this.sent
      .filter((m) => m.channel === 'agent:event')
      .map((m) => m.payload as AgentEventEnvelope)
  }
}

let nextId = 100
function fakeWindow(): { wc: FakeWebContents; ctx: WindowContext } {
  const wc = new FakeWebContents(nextId++)
  return { wc, ctx: { id: wc.id, kind: 'main', sender: wc as unknown as WebContents } }
}

let runSeq = 0
const req = (over: Partial<RunRequest> = {}): RunRequest => ({
  runId: `run-${++runSeq}`,
  sessionId: 's1',
  workspaceId: 'w1',
  depth: 0,
  input: [{ type: 'text', text: '看一下工程结构' }],
  mode: 'normal',
  thinking: 'auto',
  webSearch: false,
  permissionMode: 'ask',
  model: 'fake-model',
  skillIds: [],
  ...over
})

/**
 * 渲染层的行为,原样照搬 session store 的 applyEnvelope ——
 * 用的是**同两个函数**(hasSeqGap / applyEvents),不是它们的副本。
 */
class RendererSim {
  transcript: TranscriptState = emptyTranscript()
  lastSeq = 0
  gaps = 0

  consume(env: AgentEventEnvelope): void {
    if (hasSeqGap(env, this.lastSeq)) {
      this.gaps++
      return
    }
    this.transcript = applyEvents(this.transcript, env.events)
    this.lastSeq = env.seq
  }

  /** attach 补齐:★ 绝不对重放事件再查一次连续性(重放的 seq 本就不连续) */
  applySnapshot(events: readonly AgentEvent[], seq: number): void {
    this.transcript = applyEvents(this.transcript, events)
    this.lastSeq = seq
  }
}

/** 把假发射器跑完(它的 sleep 全是 setTimeout,假时钟能一路推到底) */
async function runToCompletion(): Promise<void> {
  await vi.advanceTimersByTimeAsync(60_000)
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  runs.abortAll()
  runs.clearForTest()
})

describe('步骤 3 端到端 · 假发射器 → 合批泵 → 信封 → 转录', () => {
  it('only subscribed windows can read and answer a pending interaction, including after reattach', async () => {
    const owner = fakeWindow()
    const viewer = fakeWindow()
    const request = req()
    startRun(request, owner.ctx, () => {})
    const handle = runs.get(request.runId)!
    const pending = interactions.request(handle, { kind: 'ask_user', questions: [{ header: 'Choose', question: 'Choose one',
      options: [{ label: 'A' }, { label: 'B' }], multiSelect: false, allowFreeform: false }] }, 1)
    const [interaction] = listInteractions({ runId: request.runId }, owner.ctx)
    expect(interaction).toBeDefined()
    const response = { id: interaction!.id, kind: 'ask_user' as const, answers: [['B']] }
    expect(listInteractions({}, viewer.ctx)).toEqual([])
    expect(() => respondInteraction(response, viewer.ctx)).toThrow('this window')
    const restored = attachRun({ runId: request.runId, sinceSeq: 0 }, viewer.ctx)
    expect(restored.pendingInteractions).toEqual([interaction])
    respondInteraction(response, viewer.ctx)
    expect(await pending).toEqual(response)
    expect(listInteractions({}, owner.ctx)).toEqual([])
    handle.finish('done')
  })

  /**
   * ★ 第二个窗口那句话**不并发、不订阅**,而是排进这条会话的队列,等正在跑的那一轮
   * 收尾后由主进程续上(`main/session-runtime.ts`)。原先这里直接抛「会话忙」,
   * 用户按了回车却要重打一遍。
   */
  it('不同窗口不能为同一会话并行启动 run:第二句排进队列,且从不订阅', () => {
    const owner = fakeWindow()
    const second = fakeWindow()
    const firstRequest = req({ sessionId: 'shared' })
    startRun(firstRequest, owner.ctx, () => {})
    const secondRequest = req({ sessionId: 'shared', input: [{ type: 'text', text: '第二句' }] })
    const result = startRun(secondRequest, second.ctx, () => {})
    expect(result.started).toBe(false)
    expect(result.started === false && result.queue.queued.map((q) => q.text)).toEqual(['第二句'])
    expect(runs.get(secondRequest.runId)).toBeUndefined()
    expect(windows.isSubscribed(`run:${secondRequest.runId}`, second.ctx.sender)).toBe(false)
    sessionRuntime.queue('shared', { kind: 'drop', id: result.started === false ? result.queue.queued[0]!.id : '' })
  })

  it('run_end 到驱动收尾完成之间仍然挡住下一轮', async () => {
    let finishDriver!: () => void
    const owner = fakeWindow()
    const firstRequest = req({ sessionId: 'settling' })
    startRun(firstRequest, owner.ctx, (handle) => {
      handle.finish('done')
      return new Promise<void>((resolve) => { finishDriver = resolve })
    })
    expect(() => startRun(req({ sessionId: 'settling' }), owner.ctx, () => {})).toThrow(/会话/)
    finishDriver()
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
    expect(() => startRun(req({ sessionId: 'settling' }), owner.ctx, () => {})).not.toThrow()
  })

  it('★ 流式跑完:信封 seq 全程连续,渲染层一次 attach 都不需要', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    await runToCompletion()

    const sim = new RendererSim()
    for (const env of wc.envelopes()) sim.consume(env)

    // 这是整个步骤 3 的核心断言:发送端按「seq = 本批最后一个」造信封,
    // 接收端按 envelopeFirstSeq 反推,一路对得上。
    expect(sim.gaps).toBe(0)
    expect(sim.lastSeq).toBe(runs.get(r.runId)?.seq)
    expect(sim.transcript.status).toBe('done')
  })

  it('★ 快速突发时合批 —— 这才是「一个 token 一条 IPC」的解药', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    const handle = runs.get(r.runId)
    expect(handle).toBeDefined()
    if (!handle) return

    const before = wc.envelopes().length

    // 不推进时钟 = 同一帧内到达。真上游就是这个形状:一个 TCP 包里
    // 常常带着几十个 delta,而假发射器 28ms 一个字符是**故意慢**的
    // (为了看起来像打字),快不过 16ms 的窗口 —— 所以它验证不了合批。
    for (let i = 0; i < 200; i++) {
      handle.emit({ type: 'stream', delta: { type: 'text_delta', index: 0, text: 'x' } })
    }
    await vi.advanceTimersByTimeAsync(20)

    const envelopes = wc.envelopes().slice(before)
    const total = envelopes.reduce((n, e) => n + e.events.length, 0)
    expect(total).toBe(200)
    // 200 个事件不该变成 200 条 IPC:每条都要付一次结构化克隆 + 一次主线程跳转
    expect(envelopes.length).toBeLessThanOrEqual(5)
    // 也不该攒成一个巨批 —— MAX_BATCH 是防「工具吐几万行时克隆卡死」的闸
    expect(Math.max(...envelopes.map((e) => e.events.length))).toBeLessThanOrEqual(64)
  })

  it('★ 结构性事件立即 flush —— 攒着它,工具卡片就晚一帧才出现(方案 §8)', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    const handle = runs.get(r.runId)
    expect(handle).toBeDefined()
    if (!handle) return

    const before = wc.envelopes().length

    // 三个 delta 攒在 buf 里(可合批,且还没到时间窗)
    for (const ch of ['甲', '乙', '丙']) {
      handle.emit({ type: 'stream', delta: { type: 'text_delta', index: 0, text: ch } })
    }
    expect(wc.envelopes().length).toBe(before) // 还没推

    // 结构性事件一到,连同攒着的 delta 一起立刻推出去 —— 不等 16ms
    handle.emit({ type: 'tool_start', callId: 'c1', toolName: 'list_files', input: {} })

    const envelopes = wc.envelopes().slice(before)
    expect(envelopes).toHaveLength(1)
    const events = envelopes[0]?.events ?? []
    expect(events.map((e) => e.type)).toEqual(['stream', 'stream', 'stream', 'tool_start'])
    // 且它在批的末位 —— 这样批内 seq 依然连续
    expect(envelopes[0]?.seq).toBe(handle.seq)
  })

  it('转录重建出完整内容:两段文字 + 一次工具调用', async () => {
    const { wc, ctx } = fakeWindow()
    startRun(req(), ctx, runFake)
    await runToCompletion()

    const sim = new RendererSim()
    for (const env of wc.envelopes()) sim.consume(env)
    const t = sim.transcript

    expect(t.messages).toHaveLength(1)
    // 提交之后活跃块清空 —— 屏幕上不该同时有「已提交的正文」和「还在流的正文」
    expect(t.live).toEqual([])
    expect(liveText(t)).toBe('')

    const parts = t.messages[0]?.parts ?? []
    expect(parts.map((p) => p.type)).toEqual(['text', 'tool_call', 'tool_result', 'text'])

    const tools = Object.values(t.tools)
    expect(tools).toHaveLength(1)
    expect(tools[0]?.status).toBe('ok')
    // 进度是易失的,工具结束后必须清掉
    expect(tools[0]?.progress).toBeUndefined()

    expect(t.model).toBe('fake-model')
    expect(t.usage?.outputTokens).toBe(214)
    expect(t.contextUsage?.used).toBe(1501)
  })

  it('★ 中断:点停止后 run 真的停,终局是 aborted 而不是 error', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    await vi.advanceTimersByTimeAsync(200) // 流到一半

    abortRun({ runId: r.runId, cascade: true })
    await runToCompletion()

    const sim = new RendererSim()
    for (const env of wc.envelopes()) sim.consume(env)

    expect(sim.gaps).toBe(0)
    expect(sim.transcript.status).toBe('aborted')
    // AbortError 是正常路径,不该冒充故障弹提示
    expect(sim.transcript.error).toBeUndefined()
    // 中断点在第一个文本块中间:提交还没发生,活跃块里应该有半截文字
    expect(sim.transcript.messages).toHaveLength(0)
    expect(liveText(sim.transcript).length).toBeGreaterThan(0)
  })

  it('★ ⌘R 重载:attach 重放出的转录与全程在线的一模一样', async () => {
    const { wc: live, ctx: liveCtx } = fakeWindow()
    const r = req()
    startRun(r, liveCtx, runFake)
    await runToCompletion()

    // 全程在线的那个窗口
    const online = new RendererSim()
    for (const env of live.envelopes()) online.consume(env)

    // 刚重载的窗口:什么都没看过,从 sinceSeq=0 补齐
    const { wc: reloaded, ctx: reloadedCtx } = fakeWindow()
    const snap = attachRun({ runId: r.runId, sinceSeq: 0 }, reloadedCtx)
    const recovered = new RendererSim()
    recovered.applySnapshot(snap.events, snap.seq)

    // 重放经过了裁剪(提交顶掉了它之前的 delta),事件条数必然更少 ——
    // 但**重建出来的转录必须一致**。这正是「裁剪对重建无损」的含义。
    expect(snap.events.length).toBeLessThan(online.lastSeq)
    expect(recovered.transcript.messages).toEqual(online.transcript.messages)
    expect(recovered.transcript.tools).toEqual(online.transcript.tools)
    expect(recovered.transcript.status).toBe('done')
    expect(recovered.lastSeq).toBe(online.lastSeq)
    expect(reloaded.envelopes()).toEqual([]) // 已结束的 run 不会再推
  })

  it('★ 运行中重载:补齐之后继续收增量,不重复也不缺口', async () => {
    const { ctx: liveCtx } = fakeWindow()
    const r = req()
    startRun(r, liveCtx, runFake)
    await vi.advanceTimersByTimeAsync(300) // 还在流

    const { wc: reloaded, ctx: reloadedCtx } = fakeWindow()
    const snap = attachRun({ runId: r.runId, sinceSeq: 0 }, reloadedCtx)
    expect(snap.status).toBe('running')

    const sim = new RendererSim()
    sim.applySnapshot(snap.events, snap.seq)

    // attach 之后到达的增量必须紧接着快照的 seq —— 这是 flush→subscribe→snapshot
    // 那个顺序存在的全部理由:反过来的话这里要么重复要么缺口。
    await runToCompletion()
    for (const env of reloaded.envelopes()) sim.consume(env)

    expect(sim.gaps).toBe(0)
    expect(sim.transcript.status).toBe('done')
    expect(sim.transcript.messages).toHaveLength(1)
  })

  it('无人订阅时整批丢弃,但 run 继续跑 —— 回来还能 attach 到完整转录', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    await vi.advanceTimersByTimeAsync(200)

    // 窗口没了(关闭 / 崩溃)
    windows.unsubscribe(`run:${r.runId}`, ctx.sender as unknown as WebContents)
    const before = wc.envelopes().length
    await runToCompletion()

    expect(wc.envelopes().length).toBe(before) // 推送停了
    expect(runs.get(r.runId)?.status).toBe('done') // 但 run 跑完了

    const { ctx: back } = fakeWindow()
    const snap = attachRun({ runId: r.runId, sinceSeq: 0 }, back)
    const sim = new RendererSim()
    sim.applySnapshot(snap.events, snap.seq)
    expect(sim.transcript.messages).toHaveLength(1)
    expect(sim.transcript.status).toBe('done')
  })

  it('★ attach 的顺序必须是 flush → subscribe → snapshot,反过来就是重复投递', async () => {
    const { ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    const handle = runs.get(r.runId)
    expect(handle).toBeDefined()
    if (!handle) return

    // ★ 关键前提:泵的 buf 里**必须真的攒着东西**,否则 flush 是空操作、
    // 顺序换了也看不出区别。假发射器 28ms 一个字符、快不过 16ms 的窗口,
    // 所以推进过时钟之后 buf 总是空的 —— 只能像这样手动制造这个瞬间。
    for (const ch of ['甲', '乙', '丙']) {
      handle.emit({ type: 'stream', delta: { type: 'text_delta', index: 0, text: ch } })
    }

    // 就在这一刻,重载的窗口来 attach
    const { wc: reloaded, ctx: reloadedCtx } = fakeWindow()
    const snap = attachRun({ runId: r.runId, sinceSeq: 0 }, reloadedCtx)
    await vi.advanceTimersByTimeAsync(20)

    // 攒着的那三条已经在快照里了。若先订阅再 flush,它们会**再**作为信封
    // 推给这个刚订阅的窗口 —— 同样的内容进两遍,屏幕上就是「甲乙丙甲乙丙」。
    const sim = new RendererSim()
    sim.applySnapshot(snap.events, snap.seq)
    for (const env of reloaded.envelopes()) sim.consume(env)

    expect(sim.gaps).toBe(0)
    expect(liveText(sim.transcript)).toBe('甲乙丙')
  })

  it('attach 一个不存在的 run 抛明确错误,而不是给空快照', () => {
    const { ctx } = fakeWindow()
    // 空快照会被 UI 当成「run 存在但还没事件」—— 于是永远转圈
    expect(() => attachRun({ runId: '不存在', sinceSeq: 0 }, ctx)).toThrow(/不存在/)
  })

  it('★ 信封能过 structuredClone —— webContents.send 就是这么送的', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    await runToCompletion()

    // 这是无头测试**唯一漏掉**的那一跳:真 IPC 会对 payload 做结构化克隆,
    // 而它拒绝函数、Symbol、class 实例、getter。事件里混进任何一个,
    // 表现是主进程抛 "An object could not be cloned" —— 一条与业务毫无关系的报错。
    // 在这里断言,就不必靠人点一次「发送」才发现。
    const envelopes = wc.envelopes()
    expect(envelopes.length).toBeGreaterThan(0)
    for (const env of envelopes) {
      expect(structuredClone(env)).toEqual(env)
    }

    // 中断路径带 AgentError,单独走一遍
    const r2 = req()
    const { wc: wc2, ctx: ctx2 } = fakeWindow()
    startRun(r2, ctx2, runFake)
    await vi.advanceTimersByTimeAsync(200)
    abortRun({ runId: r2.runId, cascade: true })
    await runToCompletion()
    for (const env of wc2.envelopes()) {
      expect(structuredClone(env)).toEqual(env)
    }
  })

  it('★ 快照也能过 structuredClone —— attach 是 invoke,同样要跨边界', async () => {
    const { ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    await runToCompletion()

    const snap = attachRun({ runId: r.runId, sinceSeq: 0 }, ctx)
    expect(structuredClone(snap)).toEqual(snap)
  })
})

/**
 * 没人在看就不推正文 —— Stage 2 的主进程一半。
 *
 * run 照跑,只是这个窗口不再收(也不再累积)它的正文;「有个审批在等你」改走全局广播,
 * 没有正文订阅时它是应用里唯一能看出来的地方。
 */
describe('正文订阅跟着「有没有人在看」走', () => {
  const ASK = { kind: 'ask_user' as const, questions: [{ header: 'Choose', question: 'Choose one',
    options: [{ label: 'A' }, { label: 'B' }], multiSelect: false, allowFreeform: false }] }

  it('agent:unwatch 只摘调用方自己的订阅,run 照跑,别的窗口照收', async () => {
    const owner = fakeWindow()
    const other = fakeWindow()
    const r = req()
    startRun(r, owner.ctx, runFake)
    attachRun({ runId: r.runId, sinceSeq: 0 }, other.ctx)

    unwatchRun({ runId: r.runId }, owner.ctx)
    const ownerBefore = owner.wc.envelopes().length
    await runToCompletion()

    expect(windows.isSubscribed(`run:${r.runId}`, owner.ctx.sender)).toBe(false)
    expect(owner.wc.envelopes()).toHaveLength(ownerBefore)
    expect(other.wc.envelopes().length).toBeGreaterThan(0)
    expect(runs.get(r.runId)?.status).toBe('done')
  })

  it('★ 窗口藏起来:先摘掉它全部的 run 订阅再通知它;之后一条正文都不再推给它', async () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, runFake)
    await vi.advanceTimersByTimeAsync(50)

    setWindowContentVisible(ctx.sender, false)
    const notice = wc.sent.filter((m) => m.channel === 'window:visibility')
    expect(notice.at(-1)?.payload).toEqual({ visible: false })
    const before = wc.envelopes().length
    await runToCompletion()

    expect(windows.isSubscribed(`run:${r.runId}`, ctx.sender)).toBe(false)
    expect(wc.envelopes()).toHaveLength(before)
    // run 不受影响,露出来之后按快照补回
    expect(runs.get(r.runId)?.status).toBe('done')
    setWindowContentVisible(ctx.sender, true)
    expect(wc.sent.filter((m) => m.channel === 'window:visibility').at(-1)?.payload).toEqual({ visible: true })
  })

  it('★ 待处理的审批进全局索引:请求时出现、回答后消失,不依赖任何正文订阅', () => {
    const { wc, ctx } = fakeWindow()
    const r = req()
    startRun(r, ctx, () => {})
    unwatchRun({ runId: r.runId }, ctx)
    const handle = runs.get(r.runId)!

    const pending = interactions.request(handle, ASK, 1)
    pending.catch(() => {})
    expect(activeRunIndex().find((e) => e.runId === r.runId)?.pendingInteractions).toBe(1)
    const broadcast = wc.sent.filter((m) => m.channel === 'agent:activeRuns').at(-1)?.payload as { runs: Array<{ runId: string; pendingInteractions?: number }> }
    expect(broadcast.runs.find((e) => e.runId === r.runId)?.pendingInteractions).toBe(1)

    const [interaction] = interactions.list().filter((i) => i.runId === r.runId)
    interactions.respond({ id: interaction!.id, kind: 'ask_user', answers: [['A']] })
    expect(activeRunIndex().find((e) => e.runId === r.runId)?.pendingInteractions).toBeUndefined()
    handle.finish('done')
  })
})

