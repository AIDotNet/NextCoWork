/**
 * 会话运行时 —— 「下一轮什么时候开跑」搬到主进程之后的验收。
 *
 * 这里钉的语义原先住在渲染层的 `stores/session.ts`(那边的旧用例逐条搬了过来):
 * 排队、插话、续跑、后台子代理汇报、目标空闲检查。搬家的理由只有一条 ——
 * **没有窗口在看这条会话时,这些事也得发生**,而渲染层只有在有人看着时才收得到 `run_end`。
 *
 * 依赖全部是假的:一张假 run 表、一个 Map 当 kv。断言量的是运行时的决定,
 * 不是 IPC 或数据库。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentEvent, RunStatus } from '../../shared/agent/event'
import type { InterjectItem } from '../../shared/agent/interject'
import { userMessage } from '../../shared/agent/message'
import type { RunRequest, SendOptions } from '../../shared/agent/run-request'
import type {
  QueuedInput,
  SessionInputState,
  SessionQueueSnapshot,
  SubagentReportStatus
} from '../../shared/domain/queued-input'
import { SESSION_INPUT_VERSION } from '../../shared/domain/queued-input'
import {
  DRAFT_DEBOUNCE_MS,
  DEFERRED_RETRY_MS,
  SessionRuntime,
  type BackgroundReport,
  type SessionRunHandle
} from '../session-runtime'

const OPTS: SendOptions = {
  workspaceId: 'w1',
  depth: 0,
  mode: 'normal',
  thinking: 'auto',
  webSearch: false,
  permissionMode: 'ask',
  model: 'demo-model',
  skillIds: []
}

class FakeRun implements SessionRunHandle {
  status: RunStatus = 'running'
  readonly controller = new AbortController()
  readonly interjects: InterjectItem[][] = []
  readonly internal: InterjectItem[] = []
  private readonly listeners = new Set<(event: AgentEvent, seq: number) => void>()
  constructor(readonly runId: string, readonly sessionId: string, readonly req: RunRequest, readonly depth = 0) {}
  get signal(): AbortSignal { return this.controller.signal }
  on(listener: (event: AgentEvent, seq: number) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }
  emit(event: AgentEvent): void { for (const listener of this.listeners) listener(event, 0) }
  setInterject(items: readonly InterjectItem[]): void { this.interjects.push([...items]) }
  enqueueInternal(item: InterjectItem): void { this.internal.push(item) }
  /** 生产路径:run 把信箱里的内部输入注入成 user 消息,同 id 的 message_commit 就是消费回执 */
  consumeInternal(): void {
    for (const item of this.internal) {
      this.emit({ type: 'message_commit', message: userMessage(item.id, item.parts, 1) })
    }
  }
}

/** 一个最小的「主进程」:run 表、会话互斥、kv、广播 */
class Harness {
  readonly kv = new Map<string, SessionInputState>()
  readonly active = new Map<string, FakeRun>()
  /** run_end 之后、收尾落盘之前那一段:会话仍被占着 */
  readonly settling = new Set<string>()
  readonly launched: RunRequest[] = []
  readonly queues: SessionQueueSnapshot[] = []
  readonly reports: Array<{ sessionId: string; callId: string; status: SubagentReportStatus }> = []
  readonly persisted = new Map<string, SubagentReportStatus>()
  readonly logs: string[] = []
  receipts = new Map<string, BackgroundReport & { reportStatus?: SubagentReportStatus }>()
  refuse: Error | null = null
  /** 写盘一律抛这个错(sticky)—— 模拟落盘失败 */
  writeFails: Error | null = null
  /** 从第 N 次写盘开始抛(1-based)。0 = 不启用;用来让前几次成功、某一次恰好失败 */
  writeFailFrom = 0
  /** 已经尝试过多少次写盘 */
  writeCalls = 0
  /** 广播抛这个错 */
  failEmit: Error | null = null
  /** 已提交用户消息的消费回执(sessionId → messageId) */
  readonly committed = new Map<string, Set<string>>()
  /** 读消费回执时抛这个错;验证「读不到就不据它删条目」 */
  failCommitted: Error | null = null
  /** 未落盘输入的状态提示,按发生顺序 */
  readonly persistence: Array<{ sessionId: string; state: 'retrying' | 'blocked' | 'saved' }> = []
  private id = 0
  readonly runtime: SessionRuntime

  constructor() {
    this.runtime = new SessionRuntime({
      now: () => 1_000 + this.id,
      newId: () => `id-${++this.id}`,
      readInput: (sessionId) => this.kv.get(sessionId) ?? null,
      writeInput: (sessionId, state) => {
        this.writeCalls += 1
        if (this.writeFails !== null || (this.writeFailFrom > 0 && this.writeCalls >= this.writeFailFrom)) {
          throw this.writeFails ?? new Error('write failed')
        }
        this.kv.set(sessionId, state)
      },
      committedInputIds: (sessionId, ids) => {
        if (this.failCommitted !== null) throw this.failCommitted
        const set = this.committed.get(sessionId)
        return set === undefined ? [] : ids.filter((id) => set.has(id))
      },
      activeRun: (sessionId) => this.active.get(sessionId),
      isBusy: (sessionId) => this.active.has(sessionId) || this.settling.has(sessionId),
      launch: (req) => this.start(req),
      emitQueue: (snapshot) => {
        if (this.failEmit !== null) throw this.failEmit
        this.queues.push(snapshot)
      },
      emitReport: (sessionId, callId, status) => { this.reports.push({ sessionId, callId, status }) },
      readReport: (sessionId, callId) => this.receipts.get(`${sessionId}/${callId}`),
      persistReportStatus: (sessionId, callId, status) => { this.persisted.set(`${sessionId}/${callId}`, status) },
      onPersistenceChange: (change) => { this.persistence.push(change) },
      log: (message) => { this.logs.push(message) }
    })
  }

  /** run 提交一条用户消息之后,持久消费回执就该在(与生产 repo.commitMessage 同一时刻)。 */
  markCommitted(sessionId: string, messageId: string): void {
    const set = this.committed.get(sessionId) ?? new Set<string>()
    set.add(messageId)
    this.committed.set(sessionId, set)
  }

  /** 用户在窗口里发出的一轮(`agent:run`) */
  send(sessionId: string, text: string, options: SendOptions = OPTS): FakeRun {
    return this.start({ ...options, runId: `user-${++this.id}`, sessionId, input: [{ type: 'text', text }] })
  }

  start(req: RunRequest): FakeRun {
    if (this.refuse !== null) throw this.refuse
    if (this.active.has(req.sessionId) || this.settling.has(req.sessionId)) throw new Error('会话忙')
    const run = new FakeRun(req.runId, req.sessionId, req)
    this.active.set(req.sessionId, run)
    this.launched.push(req)
    this.runtime.runStarted(run, req)
    // 生产路径:run 把这一轮输入落成 user 消息时会发同 id 的 message_commit —— 内部输入靠它确认
    if (req.inputInternal === true) {
      run.emit({ type: 'message_commit', message: userMessage(req.inputMessageId ?? '', req.input ?? [], 1) })
    }
    return run
  }

  /** run_end 发出,但收尾还没落盘 —— 会话仍被占着 */
  end(run: FakeRun, status: RunStatus = 'done'): void {
    run.emit({ type: 'run_end', status })
    run.status = status
    this.active.delete(run.sessionId)
    this.settling.add(run.sessionId)
  }

  /** driver 的 finally 跑完,互斥释放 */
  settle(run: FakeRun): void {
    this.settling.delete(run.sessionId)
    this.runtime.runSettled(run)
  }

  finish(run: FakeRun, status: RunStatus = 'done'): void {
    this.end(run, status)
    this.settle(run)
  }

  queued(sessionId: string): QueuedInput[] {
    return this.runtime.getInput(sessionId)?.queued ?? []
  }

  enqueue(sessionId: string, text: string, options: SendOptions = OPTS, attachments: QueuedInput['attachments'] = []): void {
    this.runtime.queue(sessionId, { kind: 'enqueue', text, options, attachments })
  }
}

let h: Harness
beforeEach(() => { h = new Harness() })
afterEach(() => { vi.useRealTimers() })

const inputText = (req: RunRequest | undefined): string =>
  (req?.input ?? []).flatMap((part) => part.type === 'text' ? [part.text] : []).join('|')

describe('排队续跑', () => {
  it('生成中再发一条:进队列,不并发第二个 run,立刻落盘', () => {
    h.send('s', '第一条')
    h.enqueue('s', '第二条')

    expect(h.launched).toHaveLength(1)
    expect(h.queued('s').map((q) => q.text)).toEqual(['第二条'])
    expect(h.kv.get('s')?.queued.map((q) => q.text)).toEqual(['第二条'])
    expect(h.queues.at(-1)?.queued.map((q) => q.text)).toEqual(['第二条'])
  })

  it('★ run 正常结束、**收尾落盘之后**才续跑:run_end 那一刻会话还占着', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '第二条')

    h.end(first)
    expect(h.launched).toHaveLength(1)

    h.settle(first)
    expect(h.launched).toHaveLength(2)
    expect(inputText(h.launched[1])).toBe('第二条')
    expect(h.queued('s')).toEqual([])
  })

  it('★★ 没有任何窗口参与也会续上 —— 搬到主进程的全部理由', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '第二条')
    h.enqueue('s', '第三条')

    h.finish(first)
    const second = h.active.get('s')!
    h.finish(second)

    expect(h.launched.map(inputText)).toEqual(['第一条', '第二条', '第三条'])
  })

  it('续跑复用该条目入队当时的档位,排队期间改档位不影响已入队条目', () => {
    const first = h.send('s', '第一条', { ...OPTS, model: 'sonnet', permissionMode: 'full' })
    h.enqueue('s', '第二条', OPTS)
    h.enqueue('s', '第三条', { ...OPTS, model: 'haiku' })
    expect(h.queued('s').map((q) => q.options.model)).toEqual(['demo-model', 'haiku'])

    h.finish(first)
    expect(h.launched[1]).toMatchObject({ model: 'demo-model', permissionMode: 'ask' })
  })

  it('队列附件在续跑时重新出现在 input 里 —— 不是只剩文字', () => {
    const IMG = 'ncw://attachments/sessions/s/01J8A.png'
    const first = h.send('s', '第一条')
    h.enqueue('s', '带图的', OPTS, [{ kind: 'image', name: '01J8A.png', url: IMG }])

    h.finish(first)
    expect(h.launched[1]?.input).toEqual([
      { type: 'text', text: '带图的' },
      { type: 'image', mime: 'image/png', dataRef: IMG }
    ])
  })

  it('用户按停止 / 报错之后不自动续跑;手动继续走同一条路径', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '排队的')

    h.finish(first, 'aborted')
    expect(h.launched).toHaveLength(1)
    expect(h.queued('s').map((q) => q.text)).toEqual(['排队的'])

    h.runtime.queue('s', { kind: 'resume' })
    expect(h.launched).toHaveLength(2)
    expect(h.queued('s')).toEqual([])
  })

  it('报错后同样不自动续跑 —— 否则连着错 N 次烧 N 轮 token', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '排队的')
    h.finish(first, 'error')
    expect(h.launched).toHaveLength(1)
  })

  it('★ 冷启动不自动执行:从 kv 读回的队列,只有用户继续才会动', () => {
    h.kv.set('s', {
      v: SESSION_INPUT_VERSION, draft: '重启前写了一半', savedAt: 1,
      queued: [{ id: 'q-1', text: '重启前排队的', attachments: [], status: 'pending', options: OPTS, enqueuedAt: 1 }]
    })

    expect(h.runtime.getInput('s')?.queued.map((q) => q.text)).toEqual(['重启前排队的'])
    expect(h.launched).toEqual([])

    h.runtime.queue('s', { kind: 'resume' })
    expect(inputText(h.launched[0])).toBe('重启前排队的')
  })

  it('启动被拒:条目原样放回队首,不能凭空消失', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '第二条')
    h.refuse = new Error('需要先安装更新')

    h.finish(first)

    expect(h.queued('s').map((q) => q.text)).toEqual(['第二条'])
  })
})

describe('插话', () => {
  it('promote 后优先于先入队的 pending,且不捎带它', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '排队甲')
    h.enqueue('s', '排队乙')
    const 乙 = h.queued('s')[1]!
    h.runtime.queue('s', { kind: 'promote', id: 乙.id })

    // 正在跑:插话推给 run 的信箱,而不是等它跑完
    h.finish(first)

    expect(inputText(h.launched[1])).toBe('排队乙')
    expect(h.queued('s').map((q) => q.text)).toEqual(['排队甲'])
  })

  it('再点一次取消插话,并清掉 promotedAt;信箱收到一份空全集', () => {
    const run = h.send('s', '第一条')
    h.enqueue('s', '排队甲')
    const 甲 = h.queued('s')[0]!

    h.runtime.queue('s', { kind: 'promote', id: 甲.id })
    expect(h.queued('s')[0]?.status).toBe('promoted')
    h.runtime.queue('s', { kind: 'promote', id: 甲.id })

    expect(h.queued('s')[0]?.status).toBe('pending')
    expect(h.queued('s')[0]?.promotedAt).toBeUndefined()
    expect(run.interjects).toEqual([[{ id: 甲.id, parts: [{ type: 'text', text: '排队甲' }] }], []])
  })

  it('多条 promote 合并成一次输入,按插话顺序而非入队顺序', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '甲')
    h.enqueue('s', '乙')
    const [甲, 乙] = h.queued('s')
    h.runtime.queue('s', { kind: 'promote', id: 乙!.id })
    h.runtime.queue('s', { kind: 'promote', id: 甲!.id })

    h.finish(first)
    expect(inputText(h.launched[1])).toBe('乙\n\n甲')
    expect(h.queued('s')).toEqual([])
  })

  it('★ 运行中插话:只推 promoted;同 id 的 message_commit 才算送达,之后不会在续跑里再发一次', () => {
    const run = h.send('s', '第一条')
    h.enqueue('s', '甲')
    h.enqueue('s', '插一句')
    const 条目 = h.queued('s')[1]!

    h.runtime.queue('s', { kind: 'promote', id: 条目.id })
    expect(run.interjects.at(-1)).toEqual([{ id: 条目.id, parts: [{ type: 'text', text: '插一句' }] }])
    // 主进程确认注入之前不能移走 —— run 半路挂了消息就没了
    expect(h.queued('s')).toHaveLength(2)

    run.emit({ type: 'message_commit', message: userMessage(条目.id, [{ type: 'text', text: '插一句' }], 1) })
    expect(h.queued('s').map((q) => q.text)).toEqual(['甲'])

    h.finish(run)
    expect(h.launched.map(inputText)).toEqual(['第一条', '甲'])
  })

  it('空闲时点插话 = 立即发送', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '排队的')
    h.finish(first, 'aborted')

    h.runtime.queue('s', { kind: 'promote', id: h.queued('s')[0]!.id })
    expect(h.launched).toHaveLength(2)
  })

  it('编辑改文本但不动档位快照与插话顺序;信箱收到新文本', () => {
    const run = h.send('s', '第一条')
    h.enqueue('s', '原文', { ...OPTS, model: 'sonnet' })
    const item = h.queued('s')[0]!
    h.runtime.queue('s', { kind: 'promote', id: item.id })
    const at = h.queued('s')[0]?.promotedAt

    h.runtime.queue('s', { kind: 'edit', id: item.id, text: '改过的' })

    const after = h.queued('s')[0]!
    expect(after.text).toBe('改过的')
    expect(after.options.model).toBe('sonnet')
    expect(after.promotedAt).toBe(at)
    expect(run.interjects.at(-1)).toEqual([{ id: item.id, parts: [{ type: 'text', text: '改过的' }] }])
  })

  it('撤回到输入框交回文本;删除只影响目标条目', () => {
    h.send('s', '第一条')
    h.enqueue('s', '甲')
    h.enqueue('s', '乙')
    const [甲, 乙] = h.queued('s')

    const taken = h.runtime.queue('s', { kind: 'take', id: 甲!.id })
    expect(taken.text).toBe('甲')
    h.runtime.queue('s', { kind: 'drop', id: 乙!.id })
    expect(h.queued('s')).toEqual([])
  })

  it('改权限档位:未消费的条目跟着换,其余档位不动', () => {
    h.send('s', '第一条')
    h.enqueue('s', '甲', { ...OPTS, model: 'sonnet' })
    h.runtime.queue('s', { kind: 'retagPermission', mode: 'full' })
    expect(h.queued('s')[0]?.options).toMatchObject({ permissionMode: 'full', model: 'sonnet' })
  })
})

describe('草稿与队列共用一个键', () => {
  it('★ 迟到的草稿防抖不会把已经续跑掉的条目写回去', () => {
    vi.useFakeTimers()
    const first = h.send('s', '第一条')
    h.enqueue('s', '第二条')
    h.runtime.setDraft('s', '写了一半', false)

    h.finish(first)
    expect(h.kv.get('s')?.queued).toEqual([])

    vi.advanceTimersByTime(DRAFT_DEBOUNCE_MS)
    expect(h.kv.get('s')).toMatchObject({ draft: '写了一半', queued: [] })
  })

  it('退出前 flush:挂在防抖里的草稿被写掉,而不是丢掉', () => {
    vi.useFakeTimers()
    h.runtime.setDraft('s', '最后半句', false)
    expect(h.kv.has('s')).toBe(false)

    h.runtime.flush()
    expect(h.kv.get('s')?.draft).toBe('最后半句')
  })

  it('版本号全局单调:条目被丢掉再重建之后,新快照仍然比旧的新', () => {
    h.send('s', '第一条')
    h.enqueue('s', '甲')
    const before = h.queues.at(-1)!.rev
    h.runtime.queue('s', { kind: 'drop', id: h.queued('s')[0]!.id })
    h.enqueue('s', '乙')
    expect(h.queues.at(-1)!.rev).toBeGreaterThan(before)
  })
})

describe('后台子代理汇报', () => {
  const report = (over: Partial<BackgroundReport> = {}): BackgroundReport => ({
    sessionId: 's', callId: 'task-bg', childRunId: 'run:sub:1', subagentType: 'general-purpose',
    text: '三处读取,都在 config.ts,另有两处在 legacy/loader.ts。', summary: '三处读取,都在 config.ts', ...over
  })

  it('★★ 父会话空闲:用上一次的档位起一轮 internal,正文是全文,界面那一轨只挂摘要', () => {
    h.finish(h.send('s', '派个后台任务'))

    h.runtime.childFinished(report(), 'done')

    const req = h.launched[1]!
    expect(req.inputInternal).toBe(true)
    expect(req.model).toBe('demo-model')
    const text = req.input.find((p) => p.type === 'text')
    expect(text?.type === 'text' && text.text).toContain('legacy/loader.ts')
    expect(req.input.find((p) => p.type === 'subagent')).toMatchObject({ callId: 'task-bg', summary: '三处读取,都在 config.ts' })
    expect(h.persisted.get('s/task-bg')).toBe('reported')
    expect(h.reports.map((r) => r.status)).toEqual(['injecting', 'reported'])
  })

  it('父会话还在跑:先进信箱标 injecting,同 id 的 message_commit 之后才算 reported', () => {
    const run = h.send('s', '还在跑')
    h.runtime.childFinished(report(), 'done')

    expect(h.launched).toHaveLength(1)
    expect(run.internal).toHaveLength(1)
    expect(run.internal[0]?.internal).toBe(true)
    // 入信箱 ≠ 送达:run 还没把它注入成 user 消息
    expect(h.reports.at(-1)?.status).toBe('injecting')
    expect(h.persisted.get('s/task-bg')).toBe('pending')

    run.consumeInternal()
    expect(h.persisted.get('s/task-bg')).toBe('reported')
    expect(h.reports.map((r) => r.status)).toEqual(['injecting', 'reported'])
  })

  it('回传中崩溃重启:落盘仍可重试,不会永久停在 injecting', () => {
    h.send('s', '还在跑')
    h.runtime.childFinished(report(), 'done')
    const saved = { ...report(), reportStatus: h.persisted.get('s/task-bg')! }
    expect(saved.reportStatus).toBe('pending')

    const restarted = new Harness()
    restarted.receipts.set('s/task-bg', saved)
    expect(restarted.runtime.reportManually('s', 'task-bg', OPTS)).toBe('reported')
    expect(restarted.launched).toHaveLength(1)
    expect(inputText(restarted.launched[0])).toContain('legacy/loader.ts')
  })

  it('旧版本残留的 injecting 可以手动重试,本进程仍在投递的不能重复', () => {
    h.receipts.set('s/task-bg', { ...report(), reportStatus: 'injecting' })
    const run = h.send('s', '还在跑')
    expect(h.runtime.reportManually('s', 'task-bg', OPTS)).toBe('injecting')
    expect(run.internal).toHaveLength(1)
    expect(h.runtime.reportManually('s', 'task-bg', OPTS)).toBe('injecting')
    expect(run.internal).toHaveLength(1)
    run.consumeInternal()
    expect(h.persisted.get('s/task-bg')).toBe('reported')
  })

  it('★ 入了信箱但正常收尾时还没被消费:接回来重投,且排在排队的用户消息之前', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '排队的')
    h.runtime.childFinished(report(), 'done')
    expect(first.internal).toHaveLength(1)

    // 收尾时没人确认消费 —— 报告不能就这么丢了
    h.finish(first)
    expect(h.launched[1]?.inputInternal).toBe(true)
    expect(h.queued('s').map((q) => q.text)).toEqual(['排队的'])

    // 汇报那一轮收尾,用户那条才续上
    h.finish(h.active.get('s')!)
    expect(inputText(h.launched[2])).toBe('排队的')
  })

  it('已确认消费的不再重投', () => {
    const first = h.send('s', '第一条')
    h.runtime.childFinished(report(), 'done')
    first.consumeInternal()
    expect(h.persisted.get('s/task-bg')).toBe('reported')

    h.finish(first)
    expect(h.launched).toHaveLength(1)
    expect(h.reports.map((r) => r.status)).toEqual(['injecting', 'reported'])
  })

  it('★ run 被停止:没被消费的报告退回 pending,不自动开新对话', () => {
    h.receipts.set('s/task-bg', report())
    const first = h.send('s', '第一条')
    h.runtime.childFinished(report(), 'done')
    expect(first.internal).toHaveLength(1)

    h.finish(first, 'aborted')
    expect(h.launched).toHaveLength(1)
    expect(h.persisted.get('s/task-bg')).toBe('pending')

    // 报告没有丢:用户点「处理」仍能汇报
    h.runtime.reportManually('s', 'task-bg', OPTS)
    expect(h.launched[1]?.inputInternal).toBe(true)
  })

  it('报错收尾同理:没被消费的报告退回 pending', () => {
    const first = h.send('s', '第一条')
    h.runtime.childFinished(report(), 'done')

    h.finish(first, 'error')
    expect(h.launched).toHaveLength(1)
    expect(h.persisted.get('s/task-bg')).toBe('pending')
  })

  it('手动处理在 injecting 期间不重复:同一份报告只入一次信箱', () => {
    h.receipts.set('s/task-bg', report())
    const run = h.send('s', '还在跑')
    h.runtime.childFinished(report(), 'done')
    expect(run.internal).toHaveLength(1)

    expect(h.runtime.reportManually('s', 'task-bg', OPTS)).toBe('injecting')
    expect(run.internal).toHaveLength(1)
    expect(h.launched).toHaveLength(1)
  })

  it('★ launch 期间同步提交的确认不会错过:登记先于 launch,收尾后不留残余', () => {
    h.finish(h.send('s', '派个后台任务'))
    h.runtime.childFinished(report(), 'done')
    // Harness.start 在 launch 返回前就发了同 id 的 message_commit —— done(true) 必须已经发生
    expect(h.persisted.get('s/task-bg')).toBe('reported')

    h.finish(h.active.get('s')!)
    expect(h.launched).toHaveLength(2)
    expect(h.reports.map((r) => r.status)).toEqual(['injecting', 'reported'])
  })

  it('launch 被拒:不留未确认状态,之后收尾不会被重复接回', () => {
    h.finish(h.send('s', '派个后台任务'))
    h.refuse = new Error('需要先安装更新')
    h.runtime.childFinished(report(), 'done')
    expect(h.persisted.get('s/task-bg')).toBe('pending')
    expect(h.reports.map((r) => r.status)).toEqual(['injecting', 'pending'])

    h.refuse = null
    h.finish(h.send('s', '随便聊聊'))
    expect(h.launched).toHaveLength(2)
    expect(h.persisted.get('s/task-bg')).toBe('pending')
    expect(h.reports.map((r) => r.status)).toEqual(['injecting', 'pending'])
  })

  it('★ 父会话正在收尾(run_end 已发、互斥未放):等收尾完再起,且排在排队的用户消息之前', () => {
    const run = h.send('s', '第一条')
    h.enqueue('s', '排队的')
    h.end(run)

    h.runtime.childFinished(report(), 'done')
    expect(h.launched).toHaveLength(1)

    h.settle(run)
    expect(h.launched[1]?.inputInternal).toBe(true)
    // 用户那条留给汇报那一轮收尾时再续
    expect(h.queued('s').map((q) => q.text)).toEqual(['排队的'])
    h.finish(h.active.get('s')!)
    expect(inputText(h.launched[2])).toBe('排队的')
  })

  it('★★ 本进程没见过这条会话发消息(重启之后):置 blocked,不是静默 return', () => {
    h.runtime.childFinished(report(), 'done')
    expect(h.launched).toEqual([])
    expect(h.persisted.get('s/task-bg')).toBe('blocked')
  })

  it('手动处理:用调用方给的兜底档位照发;取不到全文就退回摘要', () => {
    h.receipts.set('s/task-bg', { ...report({ text: '' }), reportStatus: 'blocked' })

    expect(h.runtime.reportManually('s', 'task-bg', { ...OPTS, model: 'fallback-model' })).toBe('reported')

    const req = h.launched[0]!
    expect(req.model).toBe('fallback-model')
    const text = req.input.find((p) => p.type === 'text')
    expect(text?.type === 'text' && text.text).toContain('三处读取,都在 config.ts')
  })

  it('已经汇报过的不再汇报', () => {
    h.receipts.set('s/task-bg', { ...report(), reportStatus: 'reported' })
    expect(h.runtime.reportManually('s', 'task-bg', OPTS)).toBe('reported')
    expect(h.launched).toEqual([])
  })

  it('子代理被中断(用户停止 / 应用退出):不自动开一轮新对话,留给用户处理', () => {
    h.finish(h.send('s', '派个后台任务'))
    h.runtime.childFinished(report(), 'aborted')
    expect(h.launched).toHaveLength(1)
    expect(h.reports.at(-1)?.status).toBe('pending')
  })

  it('历史操作(手动压缩)占着会话、又没有 run 会收尾:靠有界重试投递', () => {
    vi.useFakeTimers()
    h.finish(h.send('s', '派个后台任务'))
    h.settling.add('s')

    h.runtime.childFinished(report(), 'done')
    expect(h.launched).toHaveLength(1)

    h.settling.delete('s')
    vi.advanceTimersByTime(DEFERRED_RETRY_MS)
    expect(h.launched[1]?.inputInternal).toBe(true)
  })
})

describe('目标空闲检查', () => {
  it('目标检查不参与后台报告重投:被内核丢弃且未提交的检查不能重开 run', () => {
    const run = h.send('s', '还在跑')
    expect(h.runtime.wakeGoal('s', [{ type: 'text', text: '旧目标检查' }], OPTS, 'old-goal')).toBe(true)
    expect(run.internal).toHaveLength(1)
    h.finish(run)
    expect(h.launched).toHaveLength(1)
  })

  it('★ 空闲:主进程直接起一轮 internal + goalId,不经过任何窗口', () => {
    const parts = [{ type: 'text' as const, text: '目标检查' }]
    expect(h.runtime.wakeGoal('s', parts, OPTS, 'goal-1')).toBe(true)

    expect(h.launched[0]).toMatchObject({ sessionId: 's', input: parts, inputInternal: true, inputGoalId: 'goal-1' })
  })

  it('目标已经失效(启动被拒):返回 false,由目标运行时自己退避', () => {
    h.refuse = new Error('The goal was cleared or replaced')
    expect(h.runtime.wakeGoal('s', [{ type: 'text', text: '检查' }], OPTS, 'goal-1')).toBe(false)
  })

  it('记住的档位不带计划执行 —— 复用到一次检查上等于把同一份计划再执行一遍', () => {
    h.finish(h.start({ ...OPTS, runId: 'r1', sessionId: 's', input: [], planExecution: { path: 'plan.md' } as never }))
    h.runtime.childFinished({ sessionId: 's', callId: 'c', childRunId: 'r1:sub:1', text: '完成' }, 'done')
    expect(h.launched[1]?.planExecution).toBeUndefined()
  })
})

/**
 * 落盘失败时的诚实性:写不进去就不能回 `accepted:true`,也不能让一条没保存的
 * 消息被续跑发出去;内存里那份要留着,等下一次成功的写补上。
 */
describe('队列写盘失败', () => {
  it('★ enqueue 写失败:抛错、不是 accepted、不广播、也不启动未落盘的那条', () => {
    const run = h.send('s', '第一条')
    h.writeFails = new Error('磁盘满了')

    expect(() => h.enqueue('s', '第二条')).toThrow('磁盘满了')

    expect(h.launched).toHaveLength(1)
    expect(h.queues).toHaveLength(0)
    expect(run.interjects).toEqual([])
  })

  it('enqueue 写失败后内存仍是失败前的旧队列;重试成功只多出一条', () => {
    h.send('s', '第一条')
    h.enqueue('s', '甲')
    const before = h.queued('s').map((q) => q.text)
    h.writeFails = new Error('磁盘满了')

    expect(() => h.enqueue('s', '乙')).toThrow()

    expect(h.queued('s').map((q) => q.text)).toEqual(before)

    h.writeFails = null
    h.enqueue('s', '乙')
    expect(h.queued('s').map((q) => q.text)).toEqual(['甲', '乙'])
    expect(h.kv.get('s')?.queued.map((q) => q.text)).toEqual(['甲', '乙'])
    expect(h.launched).toHaveLength(1)
  })

  it('edit 写失败:文本与档位快照都回到编辑前', () => {
    h.send('s', '第一条')
    h.enqueue('s', '原文', { ...OPTS, model: 'sonnet' })
    const item = h.queued('s')[0]!
    h.writeFails = new Error('磁盘满了')

    expect(() => h.runtime.queue('s', { kind: 'edit', id: item.id, text: '改过的' })).toThrow()

    const after = h.queued('s')[0]!
    expect(after.text).toBe('原文')
    expect(after.options.model).toBe('sonnet')
  })

  it('drop 写失败:条目回到队列', () => {
    h.send('s', '第一条')
    h.enqueue('s', '甲')
    h.enqueue('s', '乙')
    const [甲] = h.queued('s')
    h.writeFails = new Error('磁盘满了')

    expect(() => h.runtime.queue('s', { kind: 'drop', id: 甲!.id })).toThrow()

    expect(h.queued('s').map((q) => q.text)).toEqual(['甲', '乙'])
  })

  it('retag 写失败:权限档位回到改前,不留下假的新档位', () => {
    h.send('s', '第一条')
    h.enqueue('s', '甲', { ...OPTS, model: 'sonnet' })
    h.writeFails = new Error('磁盘满了')

    expect(() => h.runtime.queue('s', { kind: 'retagPermission', mode: 'full' })).toThrow()

    expect(h.queued('s')[0]?.options.permissionMode).toBe('ask')
  })
})

describe('续跑写盘失败', () => {
  it('★ drain 移交队列失败:不启动、队列未丢,内存里原样还在', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '第二条')
    h.writeFailFrom = h.writeCalls + 1

    expect(() => h.finish(first)).toThrow()

    expect(h.launched).toHaveLength(1)
    expect(h.queued('s').map((q) => q.text)).toEqual(['第二条'])
  })

  it('launch 失败后把条目放回队首的那次写再失败:抛出去,队列仍在内存里', () => {
    const first = h.send('s', '第一条')
    h.enqueue('s', '第二条')
    h.refuse = new Error('需要先安装更新')
    h.writeFailFrom = h.writeCalls + 1

    expect(() => h.finish(first)).toThrow()

    expect(h.launched).toHaveLength(1)
    expect(h.queued('s').map((q) => q.text)).toEqual(['第二条'])
  })
})

describe('草稿写盘失败', () => {
  it('★ 防抖写失败不炸出 uncaught;flush 能把这份草稿补上', () => {
    vi.useFakeTimers()
    h.writeFails = new Error('磁盘满了')
    h.runtime.setDraft('s', '写了一半', false)

    // 定时器回调自己吞下异常(记日志),而不是变成 uncaught
    expect(() => vi.advanceTimersByTime(DRAFT_DEBOUNCE_MS)).not.toThrow()
    expect(h.kv.has('s')).toBe(false)
    expect(h.logs.some((m) => m.includes('could not persist draft'))).toBe(true)

    h.writeFails = null
    h.runtime.flush()
    expect(h.kv.get('s')?.draft).toBe('写了一半')
  })

  it('immediate 写失败抛给调用方,留下的草稿由 flush 重试', () => {
    h.writeFails = new Error('磁盘满了')
    expect(() => h.runtime.setDraft('s', '正文', true)).toThrow('磁盘满了')
    expect(h.kv.has('s')).toBe(false)

    h.writeFails = null
    h.runtime.flush()
    expect(h.kv.get('s')?.draft).toBe('正文')
  })

  it('flush 遇到一条失败:抛出去,但这份草稿的 dirty 留着还能再试;其他条目不受牵连', () => {
    vi.useFakeTimers()
    h.runtime.setDraft('s', '会话 s 的草稿', false)
    h.runtime.setDraft('t', '会话 t 的草稿', false)
    h.writeFails = new Error('磁盘满了')

    // 两条都试过才抛:第一条失败不能挡住后面的草稿(AggregateError 的 message 不重复每条原因)。
    expect(() => h.runtime.flush()).toThrow(/could not be saved/)
    expect(h.kv.has('s')).toBe(false)
    expect(h.kv.has('t')).toBe(false)
    expect(h.persistence).toEqual([
      { sessionId: 's', state: 'blocked' },
      { sessionId: 't', state: 'blocked' }
    ])

    h.writeFails = null
    h.runtime.flush()
    expect(h.kv.get('s')?.draft).toBe('会话 s 的草稿')
    expect(h.kv.get('t')?.draft).toBe('会话 t 的草稿')
    expect(h.persistence.at(-1)).toEqual({ sessionId: 't', state: 'saved' })
  })

  it('防抖还没触发就 flush:草稿写掉(与旧行为一致)', () => {
    vi.useFakeTimers()
    h.runtime.setDraft('s', '半句', false)
    h.runtime.flush()
    expect(h.kv.get('s')?.draft).toBe('半句')
  })
})

describe('已提交输入的消费回执', () => {
  it('★★ 清队列写失败后,同一条插话不会被续跑再发一次', () => {
    const run = h.send('s', '第一条')
    h.enqueue('s', '甲')
    h.enqueue('s', '插一句')
    const 条目 = h.queued('s')[1]!
    h.runtime.queue('s', { kind: 'promote', id: 条目.id })

    // run 注入成功:消息落盘 → 消费回执先于清队列的写
    h.markCommitted('s', 条目.id)
    h.writeFailFrom = h.writeCalls + 1
    run.emit({ type: 'message_commit', message: userMessage(条目.id, [{ type: 'text', text: '插一句' }], 1) })

    // 那一刻写盘失败,内存里**不能**把它放回队列
    expect(h.queued('s').map((q) => q.text)).toEqual(['甲'])
    expect(h.logs.some((m) => m.includes('could not reap committed input'))).toBe(true)

    h.writeFailFrom = 0
    h.finish(run)
    // 续跑只发「甲」,绝不把已经执行过的那句再发一遍
    expect(h.launched.map(inputText)).toEqual(['第一条', '甲'])
  })

  it('★ 重启后旧队列行不推翻已提交的消息:回执在,条目就不再被发出去', () => {
    h.kv.set('s', {
      v: SESSION_INPUT_VERSION, draft: '', savedAt: 1,
      queued: [
        { id: 'done-1', text: '上一轮已经发过的', attachments: [], status: 'pending', options: OPTS, enqueuedAt: 1 },
        { id: 'live-1', text: '还没发过的', attachments: [], status: 'pending', options: OPTS, enqueuedAt: 2 }
      ]
    })
    h.markCommitted('s', 'done-1')

    h.runtime.queue('s', { kind: 'resume' })

    expect(h.launched.map(inputText)).toEqual(['还没发过的'])
    // 已提交那条既没被发出去,也不再留在持久队列里(下次冷启动不会又捞出来)
    expect(h.kv.get('s')?.queued.map((q) => q.id) ?? []).toEqual([])
    expect(h.kv.get('s')?.queued.some((q) => q.id === 'done-1')).toBe(false)
  })

  it('读不到消费回执时不据它删条目 —— 宁可多留一条,也不能凭失败的查询丢掉输入', () => {
    h.kv.set('s', {
      v: SESSION_INPUT_VERSION, draft: '', savedAt: 1,
      queued: [{ id: 'q-1', text: '还没发过的', attachments: [], status: 'pending', options: OPTS, enqueuedAt: 1 }]
    })
    const run = h.send('s', '第一条')
    h.failCommitted = new Error('回执表暂不可用')

    // 运行中引入这条插话:回执查询失败,但它必须照常进 run 的信箱、也留在队列里
    h.runtime.queue('s', { kind: 'promote', id: 'q-1' })

    expect(h.logs.some((m) => m.includes('could not read committed inputs'))).toBe(true)
    expect(run.interjects.at(-1)).toEqual([{ id: 'q-1', parts: [{ type: 'text', text: '还没发过的' }] }])
    expect(h.queued('s').map((q) => q.id)).toEqual(['q-1'])
  })
})

describe('广播失败', () => {
  it('★ emitQueue 抛错:已落盘的那条不算失败,也不能重复入队', () => {
    h.send('s', '第一条')
    h.failEmit = new Error('没有窗口在听')

    expect(() => h.enqueue('s', '甲')).not.toThrow()
    expect(h.kv.get('s')?.queued.map((q) => q.text)).toEqual(['甲'])
    expect(h.queued('s').map((q) => q.text)).toEqual(['甲'])
    expect(h.logs.some((m) => m.includes('could not broadcast queue'))).toBe(true)

    h.failEmit = null
    h.enqueue('s', '乙')
    expect(h.queued('s').map((q) => q.text)).toEqual(['甲', '乙'])
  })
})
