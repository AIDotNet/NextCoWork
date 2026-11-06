/**
 * 会话运行时 —— 「这条会话下一轮什么时候开跑」的**唯一**决定者。
 *
 * 需求:Agent 在主进程跑,而「排队消息续跑」「后台子代理把结果交回主代理」
 * 「目标空闲时的检查」原先都要靠渲染层收到 `run_end` 之后再调一次 `send()`。
 * 没有窗口在看这条会话(切走了、最小化了、窗口重载到一半)时,这三件事就
 * 一件也不会发生 —— 而为了让它们发生,渲染层又不得不一直订阅着每个会话的
 * 全部正文。所以这三件事收到这里:主进程自己决定,渲染层只负责显示。
 *
 * 不满足会怎样:用户排了三条消息就去开会,回来发现第一轮跑完之后什么都没再发生;
 * 或者渲染层为了不漏掉 `run_end`,把每个开过的会话的全部转录都留在内存里。
 *
 * ## 三条不变式
 *
 * 1. **一条会话同一时刻只有一个 run。** 续跑必须等到上一个 run 的 driver
 *    `finally` 跑完(收尾落盘、释放会话互斥)才开始 —— `run_end` 事件**不是**
 *    这个时刻,它比收尾早(见 `RunRegistry.retainSessionForRun`)。所以入口是
 *    `runSettled`,由 `ipc/agent.ts` 在释放互斥之后调用,而不是监听 `run_end`。
 * 2. **只有正常结束才自动续跑。** `aborted` 是用户按了停止,`error` 自动续跑
 *    往往连着错 N 次、烧 N 轮 token。队列原样留着,等用户点「继续」。
 * 3. **冷启动不自动执行。** 进程刚起来时从 kv 读回的队列,只有在用户的下一个
 *    动作(发消息、插话、继续)之后才会动。
 *
 * ★ 本文件零 electron import,依赖全部注入 —— 无头测试直接驱动它。
 */
import type { AgentEvent, RunStatus } from '../shared/agent/event'
import type { ContentPart } from '../shared/agent/message'
import type { InterjectItem } from '../shared/agent/interject'
import type { RunRequest, SendOptions } from '../shared/agent/run-request'
import {
  QUEUE_MAX_ITEMS,
  QUEUE_MAX_TEXT,
  SESSION_INPUT_VERSION,
  batchToParts,
  isLive,
  makeQueuedInput,
  mergeBatch,
  pickNextBatch,
  type QueuedInput,
  type SessionInputState,
  type SessionQueueOp,
  type SessionQueueResult,
  type SessionQueueSnapshot,
  type SubagentReportStatus
} from '../shared/domain/queued-input'

/** 运行时只用到 `RunHandle` 的这一小块 —— 测试拿假对象就能驱动。 */
export interface SessionRunHandle {
  readonly runId: string
  readonly sessionId: string
  readonly depth: number
  readonly status: RunStatus
  readonly signal: AbortSignal
  on(listener: (event: AgentEvent, seq: number) => void): () => void
  setInterject(items: readonly InterjectItem[]): void
  enqueueInternal(item: InterjectItem): void
}

/** 一个后台子代理交回来的东西。 */
export interface BackgroundReport {
  /** 父会话 */
  sessionId: string
  callId: string
  childRunId: string
  subagentType?: string
  /** 子代理最后一条助手消息的**全文**;取不到时为空串 */
  text: string
  /** 卡片上那截预览(前 240 字)。只挂在界面那一轨的 part 上 */
  summary?: string
}

export interface SessionRuntimeDeps {
  now(): number
  newId(): string
  readInput(sessionId: string): SessionInputState | null
  writeInput(sessionId: string, state: SessionInputState): void
  /** 已落盘的用户消息消费回执；清队列失败或压缩历史后也不能重发它们。 */
  committedInputIds(sessionId: string, ids: readonly string[]): readonly string[]
  /** 这条会话此刻正在跑的**顶层** run */
  activeRun(sessionId: string): SessionRunHandle | undefined
  /** 有 run 或历史操作占着这条会话 —— 包括 `run_end` 之后、收尾落盘之前那一段 */
  isBusy(sessionId: string): boolean
  /** 启动一个不绑定任何窗口的顶层 run。被拒绝(会话忙、需要更新、目标已失效)时抛错 */
  launch(req: RunRequest): void
  emitQueue(snapshot: SessionQueueSnapshot): void
  emitReport(sessionId: string, callId: string, status: SubagentReportStatus): void
  /** 从库里的 Task 回执恢复一个子代理的交付物 —— 重启之后的手动汇报走这条 */
  readReport(sessionId: string, callId: string): (BackgroundReport & { reportStatus?: SubagentReportStatus }) | undefined
  persistReportStatus(sessionId: string, callId: string, status: SubagentReportStatus): void
  /**
   * 未落盘输入的状态变化。可选:没有订阅者(无头服务)时不传。
   * ★ 只报状态,不报异常正文 —— 界面据此提示,而磁盘路径/SQL 细节不出这一层。
   */
  onPersistenceChange?(change: SessionInputPersistence): void
  log(message: string, error?: unknown): void
}

/** 未落盘输入的当前状态;渲染层只按它决定提示哪一种。 */
export interface SessionInputPersistence {
  sessionId: string
  state: 'retrying' | 'blocked' | 'saved'
}

/** 草稿按键级频率变化,走防抖;队列的增删改是离散动作,立即落盘。 */
export const DRAFT_DEBOUNCE_MS = 500
/** 会话被历史操作(手动压缩等)占着时,内部输入隔多久再试一次 */
export const DEFERRED_RETRY_MS = 1000
/** 最多重试多少次 —— 一个永远不释放的互斥不该让定时器永远转下去 */
export const DEFERRED_MAX_RETRIES = 120
/** 记住多少条会话的「上一次发送档位」。只有几百字节一条,但不该无界 */
export const LAST_OPTIONS_MAX = 256

const NO_SUMMARY = 'The background subagent finished without a summary.'

interface Entry {
  draft: string
  queued: QueuedInput[]
  timer: ReturnType<typeof setTimeout> | null
  /**
   * ★ 内存里的这一份还没成功落盘。写失败(或防抖还没写)时留着 —— `flush` 靠它
   * 重试,`evictIfIdle` 也不淘汰它,否则一条没保存成功的草稿会随条目一起消失。
   */
  dirty: boolean
}

/** 离散操作前只拷会变的状态;`timer` 是调度对象,不是数据,不拷。 */
type EntrySnapshot = Pick<Entry, 'draft' | 'queued' | 'dirty'>

/** 一次变更:只给要改的字段,没给的保持原样(`draft` 不因 `queued` 操作被抹掉)。 */
type EntryPatch = Partial<EntrySnapshot>

/** 一条等会话空出来再投递的内部输入(后台汇报、目标检查)。 */
interface DeferredInput {
  parts: ContentPart[]
  options: SendOptions
  goalId?: string
  done?: (ok: boolean) => void
}

type Delivery = 'interjected' | 'launched' | 'deferred' | 'failed'

export class SessionRuntime {
  private readonly entries = new Map<string, Entry>()
  private readonly lastOptions = new Map<string, SendOptions>()
  private readonly deferred = new Map<string, DeferredInput[]>()
  private readonly retries = new Map<string, ReturnType<typeof setTimeout>>()
  /** 本进程里已经开始汇报的 `会话 + callId`。持久化的 reportStatus 管跨重启,这里管并发 */
  private readonly reporting = new Set<string>()
  /**
   * ★ 还没被 run 消费的内部输入:runId → inputMessageId → 输入。
   * 「入信箱 / launch 成功」不等于送达 —— 只有 run 把它注入成**同 id** 的 user 消息
   * (发出 `message_commit`)才算;run 收尾时还没确认的,从这里接回去重投或退回。
   */
  private readonly unconfirmed = new Map<string, Map<string, DeferredInput>>()
  /**
   * 未落盘输入的状态,以及它此刻有没有人在等一次成功写(flush)。
   *
   * ★ 两者分开是必要的:反复尝试都失败时,状态会从 `retrying` 落到 `blocked`,
   *   而界面必须能看到这一次降级;只发一次 `retrying` 会让「一直在失败」看起来
   *   和「正在重试」完全一样。
   */
  private readonly persistence = new Map<string, 'retrying' | 'blocked'>()
  private readonly flushWaiters = new Set<string>()
  /**
   * ★ 全局单调递增,而不是每会话各数各的:会话条目在空了之后会被丢掉,
   * 重新建出来时每会话计数会回到 0,渲染层就会把新快照当成旧的丢掉。
   */
  private rev = 0

  constructor(private readonly deps: SessionRuntimeDeps) {}

  // ═══════════════════════════════════════════════════════════════
  // 草稿与队列的持久化
  // ═══════════════════════════════════════════════════════════════

  /** 读回未发出的输入。没有可恢复的内容时返回 null(与旧的 `session:getInput` 同义) */
  getInput(sessionId: string): SessionInputState | null {
    const entry = this.load(sessionId)
    if (entry.draft === '' && entry.queued.length === 0) {
      this.evictIfIdle(sessionId, entry)
      return null
    }
    return this.stateOf(entry)
  }

  /**
   * 草稿由渲染层编辑,这里只负责落盘。
   *
   * ★ 草稿和队列落在**同一个 kv 键**里,所以每一次写都要写「当前的整份」:
   * 渲染层整份覆盖的话,一次迟到的草稿防抖会把主进程刚刚消费掉的队列写回去,
   * 同一条消息于是被续跑两次。
   */
  setDraft(sessionId: string, draft: string, immediate: boolean): void {
    const entry = this.load(sessionId)
    entry.draft = draft
    entry.dirty = true
    if (immediate) {
      // 写失败会抛给调用方;draft 与 dirty 都还留着,flush 能重试。
      this.write(sessionId, entry)
      return
    }
    if (entry.timer !== null) clearTimeout(entry.timer)
    entry.timer = setTimeout(() => {
      entry.timer = null
      try {
        this.write(sessionId, entry)
      } catch (error) {
        // 防抖回调是后台定时器,抛出去就是 uncaught;这里记下来,留给 flush 重试。
        // entry.dirty 仍是 true,这份草稿不会因为这一次失败被当成已保存。
        this.deps.log(`[session] could not persist draft: ${sessionId}`, error)
      }
    }, DRAFT_DEBOUNCE_MS)
  }

  /**
   * 退出、整库替换之前:把还 dirty 的条目写掉,而不是丢掉。
   *
   * ★ 遍历条件是「没成功落盘」而不是「挂着防抖定时器」—— 防抖回调写失败之后
   * timer 已经是 null,但那份草稿仍然只在内存里。
   */
  flush(): void {
    const errors: unknown[] = []
    const entering = [...this.entries].filter(([, entry]) => entry.dirty).map(([sessionId]) => sessionId)
    for (const sessionId of entering) this.flushWaiters.add(sessionId)
    try {
      for (const [sessionId, entry] of [...this.entries]) {
        if (entry.timer !== null) {
          clearTimeout(entry.timer)
          entry.timer = null
        }
        if (!entry.dirty) continue
        try { this.write(sessionId, entry) } catch (error) { errors.push(error) }
      }
    } finally {
      for (const sessionId of entering) this.flushWaiters.delete(sessionId)
      for (const sessionId of entering) {
        const entry = this.entries.get(sessionId)
        if (entry === undefined || !entry.dirty) continue
        this.reportPersistence(sessionId, 'blocked')
      }
    }
    // 先尝试所有会话,再向显式保存/退出调用方报告失败,不能牵连后面的草稿。
    if (errors.length === 1) throw errors[0]
    if (errors.length > 1) throw new AggregateError(errors, 'Some session inputs could not be saved')
  }

  /** 数据库整个换掉之后:内存里那份是旧库的,丢掉重读。不写回。 */
  reset(): void {
    for (const entry of this.entries.values()) {
      if (entry.timer !== null) clearTimeout(entry.timer)
    }
    for (const timer of this.retries.values()) clearTimeout(timer)
    this.entries.clear()
    this.deferred.clear()
    this.retries.clear()
    this.reporting.clear()
    this.unconfirmed.clear()
    this.lastOptions.clear()
  }

  // ═══════════════════════════════════════════════════════════════
  // 队列
  // ═══════════════════════════════════════════════════════════════

  queue(sessionId: string, op: SessionQueueOp): SessionQueueResult {
    const entry = this.load(sessionId)
    switch (op.kind) {
      case 'enqueue': {
        // ★ 软上限:超过就不是队列了,是便签本。**拒绝入队**,由调用方保留草稿 ——
        // 静默丢弃会让用户以为消息进了队列。
        if (entry.queued.length >= QUEUE_MAX_ITEMS) return { ...this.snapshot(sessionId, entry), accepted: false }
        const attempted: QueuedInput[] = [
          ...entry.queued,
          makeQueuedInput(this.deps.newId(), op.text.slice(0, QUEUE_MAX_TEXT), op.options, this.deps.now(), op.attachments)
        ]
        // ★ 先写盘再确认:写失败会回滚内存并抛错,绝不回 accepted:true,
        // 也不去 drain —— 否则一条没落盘的消息会被续跑发出去。
        const result = this.mutate(sessionId, entry, { queued: attempted })
        // 渲染层以为还在跑,其实那一轮已经收尾了 —— 这时入队就等于「接着发」。
        if (this.deps.activeRun(sessionId) === undefined) this.drain(sessionId)
        return result
      }
      case 'promote': {
        const item = entry.queued.find((q) => q.id === op.id)
        if (item === undefined) return { ...this.snapshot(sessionId, entry), accepted: false }
        /*
          ★ 不能直接用 now:毫秒分辨率下两次点击可能同值,而排序的正确性不该依赖
          「两次点击不会落在同一毫秒」。取 max(now, 已有最大值 + 1) 保证严格单调。
          ★ 取消引入时清掉 promotedAt:再次引入应当排到已引入者的队尾。
        */
        const maxAt = entry.queued.reduce((m, q) => Math.max(m, q.promotedAt ?? 0), 0)
        const at = Math.max(this.deps.now(), maxAt + 1)
        const attempted = entry.queued.map((q) => q.id !== op.id
          ? q
          : q.status === 'promoted'
            ? { ...withoutPromotedAt(q), status: 'pending' as const }
            : { ...q, status: 'promoted' as const, promotedAt: at })
        const result = this.mutate(sessionId, entry, { queued: attempted })
        // 空闲态被点插话 = 立即发送;运行中才是插话的正题,推给 run 的信箱
        if (this.deps.activeRun(sessionId) === undefined) this.drain(sessionId)
        else this.syncInterject(sessionId)
        return result
      }
      case 'edit': {
        // ★ 不动档位快照,也不动 promotedAt —— 编辑不改变加塞顺序
        const attempted = entry.queued.map((q) => q.id === op.id ? { ...q, text: op.text.slice(0, QUEUE_MAX_TEXT) } : q)
        const result = this.mutate(sessionId, entry, { queued: attempted })
        // run 信箱里存的是旧文本,必须重发覆盖
        this.syncInterject(sessionId)
        return result
      }
      case 'drop': {
        const attempted = entry.queued.filter((q) => q.id !== op.id)
        const result = this.mutate(sessionId, entry, { queued: attempted })
        this.syncInterject(sessionId)
        return result
      }
      case 'take': {
        const item = entry.queued.find((q) => q.id === op.id)
        if (item === undefined) return { ...this.snapshot(sessionId, entry), accepted: false }
        const attempted = entry.queued.filter((q) => q.id !== op.id)
        const result = this.mutate(sessionId, entry, { queued: attempted })
        this.syncInterject(sessionId)
        return { ...result, text: item.text }
      }
      case 'retagPermission': {
        const attempted = entry.queued.map((q) => q.options.permissionMode === op.mode
          ? q
          : { ...q, options: { ...q.options, permissionMode: op.mode } })
        return this.mutate(sessionId, entry, { queued: attempted })
      }
      case 'retagMode': {
        const attempted = entry.queued.map((q) => q.options.mode === op.mode
          ? q
          : { ...q, options: { ...q.options, mode: op.mode } })
        return this.mutate(sessionId, entry, { queued: attempted })
      }
      case 'resume': {
        this.drain(sessionId)
        return { ...this.snapshot(sessionId, this.load(sessionId)), accepted: true }
      }
    }
  }

  /**
   * run 结束后发出排队的下一批。返回是否真的起了一个新 run。
   *
   * ★ **先确认档位在,再动队列。** 反过来的话,没有档位时条目已经被移出队列,
   * 那几句话就凭空消失了。启动失败同样把条目放回队首。
   */
  private drain(sessionId: string): boolean {
    if (this.deps.isBusy(sessionId)) return false
    const entry = this.load(sessionId)
    this.removeCommittedInputs(sessionId, entry)
    if (entry.dirty) this.write(sessionId, entry)
    const batch = pickNextBatch(entry.queued)
    if (batch.length === 0) {
      this.evictIfIdle(sessionId, entry)
      return false
    }
    // ★ 档位取**最早被引入**那条的快照:用户按他当时看到的设置写下这句话。
    const options = batch[0]?.options ?? this.lastOptions.get(sessionId)
    if (options === undefined) return false

    const merged = mergeBatch(batch)
    // 超限被排除的条目退回 pending —— 留在 promoted 会让下一轮又把它们排到最前
    const deferredIds = new Set(merged.deferredIds)
    const sent = batch.filter((q) => !deferredIds.has(q.id))
    const sentIds = new Set(sent.map((q) => q.id))
    const before = entry.queued
    const attempted = before
      .filter((q) => !sentIds.has(q.id))
      .map((q) => deferredIds.has(q.id) ? { ...withoutPromotedAt(q), status: 'pending' as const } : q)
    /*
      ★ 去掉这批之前先落盘。写失败就恢复 `before`(那一刻队列还在)并把错误抛给
      调用方 —— 一条都没启动,而内存里也不能留下「已经移出队列」的假状态。
    */
    try {
      this.commit(sessionId, entry, { queued: attempted })
    } catch (error) {
      entry.queued = before
      throw error
    }

    try {
      this.deps.launch({
        ...options,
        runId: this.deps.newId(),
        sessionId,
        input: batchToParts(merged),
        inputMessageId: this.deps.newId()
      })
      return true
    } catch (error) {
      this.deps.log(`[session] queued input could not start: ${sessionId}`, error)
      // 没发出去就原样放回 —— 宁可多等一轮,也不能让用户排的话消失。
      // ★ 上一次提交把队列清空后条目可能已被淘汰,按 id 取回活的那份再放回。
      const live = this.load(sessionId)
      live.queued = [...sent, ...live.queued.filter((q) => !sentIds.has(q.id))]
      // ★ 放回本身也要落盘。这一步再失败就抛出去(由 runSettled 的调用方记日志),
      // 不能静默算作回放成功 —— 否则重启后这几句话就没了。
      this.commit(sessionId, live)
      return false
    }
  }

  /**
   * 把当前全部 promoted 条目推给正在跑的 run,由它在下一个轮次边界注入。
   *
   * ★ 全量替换:取消引入就是重发一份不含它的全集(空数组也有意义)。
   * ★ 只推 promoted,pending 一条都不捎带 —— 否则任何人排队都会打断当前执行。
   */
  private syncInterject(sessionId: string): void {
    const active = this.deps.activeRun(sessionId)
    if (active === undefined) return
    const entry = this.load(sessionId)
    this.removeCommittedInputs(sessionId, entry)
    const items = pickNextBatch(entry.queued)
      .filter((q) => q.status === 'promoted')
      .map((q) => ({ id: q.id, parts: batchToParts(mergeBatch([q])) }))
      .filter((item) => item.parts.length > 0)
    active.setInterject(items)
  }

  /**
   * run 把插话注入成用户消息时复用了条目 id,所以一条同 id 的 `message_commit`
   * 就是「已送达」的回执 —— 这时才移出队列,run 半路挂了消息也不会丢。
   *
   * 消费已经是事实，清队列写失败也不能把条目放回可执行队列。内存保留移除并标脏；
   * 冷启动再以持久消费回执过滤，不能以旧队列行推翻已提交的用户消息。
   */
  private reapCommitted(sessionId: string, messageId: string): void {
    const entry = this.entries.get(sessionId)
    if (entry === undefined || !entry.queued.some((q) => q.id === messageId)) return
    const before = entry.queued
    try {
      this.commit(sessionId, entry, { queued: before.filter((q) => q.id !== messageId) })
    } catch (error) {
      /*
        ★ **不回滚成 `before`。** 消息已经提交了,这条输入已经执行过;
        放回队列等于让下一次 `drain` 把同一句话再跑一遍。写失败时内存里保持
        「已移除」并标脏(commit → apply 已经置 dirty),由 flush 或下一次
        `removeCommittedInputs` 补写;盘上那份旧队列靠持久消费回执挡住。
      */
      this.deps.log(`[session] could not reap committed input: ${sessionId}`, error)
    }
  }

  /**
   * 按持久消费回执过滤队列中的条目(冷启动、以及每一次 drain/syncInterject 之前)。
   *
   * ★ 这一条才是「已执行的输入绝不会再发一次」的硬保证:清队列的写可能失败,
   *   转录也可能被压缩/改写,而回执按 `(sessionId, messageId)` 独立留痕。
   *   读到回执却没有待清理队列时必须**写回** —— 否则下次冷启动又会把它们捞出来。
   */
  private removeCommittedInputs(sessionId: string, entry: Entry): void {
    if (entry.queued.length === 0) return
    let committed: readonly string[]
    try {
      committed = this.deps.committedInputIds(sessionId, entry.queued.map((q) => q.id))
    } catch (error) {
      // 读不到回执时**不**据此删条目:宁可多留一条,也不能凭失败的查询丢掉输入。
      this.deps.log(`[session] could not read committed inputs: ${sessionId}`, error)
      return
    }
    if (committed.length === 0) return
    const done = new Set(committed)
    const queued = entry.queued.filter((q) => !done.has(q.id))
    if (queued.length === entry.queued.length) return
    entry.queued = queued
    // 写失败只标脏并提示:内存里已经不含它们,不会再被本进程发出去。
    try { this.write(sessionId, entry) } catch { /* write 已置 dirty 并记日志 */ }
  }

  // ═══════════════════════════════════════════════════════════════
  // run 生命周期
  // ═══════════════════════════════════════════════════════════════

  /** 每个 run 启动时调用(`ipc/agent.ts` 的 `launch`)。只关心顶层 run。 */
  runStarted(handle: SessionRunHandle, req: RunRequest): void {
    if (handle.depth !== 0) return
    this.rememberOptions(req.sessionId, optionsOf(req))
    handle.on((event) => {
      if (event.type === 'message_commit' && event.message.role === 'user') {
        // 内部输入:同 id 的 user message_commit 是「已被消费」的回执,这时才 done(true)
        this.confirmInternal(handle.runId, event.message.id)
        this.reapCommitted(handle.sessionId, event.message.id)
      }
    })
  }

  /**
   * driver 的 `finally` 跑完、会话互斥已经释放之后调用。
   *
   * 等着投递的内部输入(后台汇报、目标检查)先走;它起了新 run 的话,
   * 排队的用户消息留给**那个** run 收尾时再续。
   */
  runSettled(handle: SessionRunHandle): void {
    if (handle.depth !== 0) return
    this.reclaimUnconfirmed(handle)
    if (this.flushDeferred(handle.sessionId)) return
    if (handle.status === 'done') this.drain(handle.sessionId)
  }

  /**
   * run 收尾:接回它名下还没被消费的内部输入。
   *
   * 正常结束 → 按原投递顺序放回 deferred **队首**,优先于排队的用户消息继续投递;
   * aborted / error(或 signal 已中止)→ 不自动续跑,done(false) 把报告退回 pending,
   * 由用户在卡片上自己点「处理」。
   */
  private reclaimUnconfirmed(handle: SessionRunHandle): void {
    const pending = this.unconfirmed.get(handle.runId)
    if (pending === undefined) return
    this.unconfirmed.delete(handle.runId)
    if (handle.status !== 'done' || handle.signal.aborted) {
      for (const input of pending.values()) input.done?.(false)
      return
    }
    if (pending.size === 0) return
    const list = this.deferred.get(handle.sessionId) ?? []
    this.deferred.set(handle.sessionId, [...pending.values(), ...list])
  }

  // ═══════════════════════════════════════════════════════════════
  // 内部输入:目标检查、后台子代理汇报
  // ═══════════════════════════════════════════════════════════════

  /**
   * 目标空闲检查。返回 false = 这次没送出去(目标已经换了、需要先装更新),
   * 由目标运行时按它自己的退避再试。
   */
  wakeGoal(sessionId: string, parts: ContentPart[], options: SendOptions, goalId: string): boolean {
    return this.deliverInternal(sessionId, { parts, options, goalId }) !== 'failed'
  }

  /**
   * 后台子代理跑完。`aborted` 不自动汇报:那基本是用户按了停止(级联到子代理)
   * 或者应用在退出 —— 此时自动开一轮新的对话,正好违背了「停下」这个意图。
   * 回执停在 pending,由用户自己点「处理」。
   */
  childFinished(report: BackgroundReport, status: RunStatus): void {
    if (status === 'aborted') {
      this.deps.emitReport(report.sessionId, report.callId, 'pending')
      return
    }
    this.report(report)
  }

  /**
   * 用户在卡片上点「处理」—— 重启之后、或者自动那一趟被挡住(blocked)之后。
   * `fallback` 是渲染层按工作区默认值拼的档位:本进程没见过这条会话发消息时,
   * 汇报靠它才发得出去。
   */
  reportManually(sessionId: string, callId: string, fallback?: SendOptions): SubagentReportStatus {
    const receipt = this.deps.readReport(sessionId, callId)
    if (receipt === undefined) return 'none'
    const status = receipt.reportStatus ?? 'pending'
    if (status !== 'pending' && status !== 'blocked' && status !== 'injecting') return status
    // 旧版本留下的 injecting 可以重试;本进程真正投递中的报告仍由 reporting 去重。
    return this.report(receipt, fallback)
  }

  private report(report: BackgroundReport, fallback?: SendOptions): SubagentReportStatus {
    const key = `${report.sessionId}\u0000${report.callId}`
    if (this.reporting.has(key)) return 'injecting'
    const options = this.lastOptions.get(report.sessionId) ?? fallback
    /*
      ★ 连档位都没有时置 blocked,而**不是**静默 return:界面上那颗「处理」按钮
      和「结果待汇报给主代理」都还在,一个按下去什么都不发生的按钮不是诚实的界面。
    */
    if (options === undefined) {
      this.setReportStatus(report.sessionId, report.callId, 'blocked')
      return 'blocked'
    }
    this.reporting.add(key)
    this.setReportStatus(report.sessionId, report.callId, 'injecting')
    /*
      ★★ 发给主代理的是**全文**,不是卡片上那截 240 字的摘要。拿摘要当正文的话,
      一份「改了八个文件、逐条说明」的报告到主代理手上只剩开头一句 —— 不报错,只是做错。
      取不到全文才退回摘要:残缺的汇报也好过没有汇报。
    */
    const body = report.text.trim() !== '' ? report.text.trim() : report.summary ?? NO_SUMMARY
    const parts: ContentPart[] = [
      {
        type: 'text',
        text: `Background subagent result (${report.subagentType ?? 'subagent'}, ${report.childRunId}):\n\n${body}\n\nReview this result and continue the conversation if action is needed.`
      },
      /*
        ★ 第二个 part 只给界面看(两个编码器都丢弃它):让这条 internal 消息被认成
        「某个后台子代理的结果回传」,画一行可展开的汇报行。挂短摘要,不挂全文 ——
        全文已经在 text part 里落盘了,再存一份就是同一段话出现两次。
      */
      { type: 'subagent', callId: report.callId, childRunId: report.childRunId,
        ...(report.summary === undefined ? {} : { summary: report.summary }) }
    ]
    let outcome: SubagentReportStatus = 'injecting'
    this.deliverInternal(report.sessionId, {
      parts,
      options,
      done: (ok) => {
        // 持久化的回执管跨重启的「只汇报一次」;这里只挡本进程里的并发
        this.reporting.delete(key)
        outcome = ok ? 'reported' : 'pending'
        this.setReportStatus(report.sessionId, report.callId, outcome)
      }
    })
    return outcome
  }

  private setReportStatus(sessionId: string, callId: string, status: SubagentReportStatus): void {
    try {
      // injecting 是运行期状态:未确认消费前落盘仍为 pending,崩溃重启后才能手动重试。
      this.deps.persistReportStatus(sessionId, callId, status === 'injecting' ? 'pending' : status)
    } catch (error) {
      this.deps.log(`[session] could not persist report status: ${sessionId}`, error)
    }
    this.deps.emitReport(sessionId, callId, status)
  }

  /**
   * 投递一条内部输入:run 在跑就进它的信箱,会话正在收尾就排到收尾之后,
   * 空闲就开一个新 run。两条路都只是「发出去了」—— `done(true)` 要等同 id 的
   * `message_commit` 回执,由 `confirmInternal` 兑现。
   */
  private deliverInternal(sessionId: string, input: DeferredInput): Delivery {
    const active = this.deps.activeRun(sessionId)
    if (active !== undefined && active.status === 'running' && !active.signal.aborted) {
      // ★ 先登记再入信箱:入队成功不算送达
      const id = this.deps.newId()
      this.trackInternal(active.runId, id, input)
      active.enqueueInternal({
        id,
        parts: input.parts,
        internal: true,
        ...(input.goalId === undefined ? {} : { goalId: input.goalId })
      })
      return 'interjected'
    }
    if (this.deps.isBusy(sessionId)) {
      const list = this.deferred.get(sessionId) ?? []
      list.push(input)
      this.deferred.set(sessionId, list)
      this.scheduleRetry(sessionId)
      return 'deferred'
    }
    return this.launchInternal(sessionId, input) ? 'launched' : 'failed'
  }

  private launchInternal(sessionId: string, input: DeferredInput): boolean {
    // ★ 在 launch 之前 mint 并登记:run 可能同步提交输入,回执早于本函数返回
    const runId = this.deps.newId()
    const inputMessageId = this.deps.newId()
    this.trackInternal(runId, inputMessageId, input)
    try {
      this.deps.launch({
        ...input.options,
        runId,
        sessionId,
        input: input.parts,
        inputMessageId,
        inputInternal: true,
        ...(input.goalId === undefined ? {} : { inputGoalId: input.goalId })
      })
      return true
    } catch (error) {
      // 启动被拒:清掉自己的未确认项再 done(false),免得之后收尾被重复接回
      this.untrackInternal(runId, inputMessageId)
      this.deps.log(`[session] internal input could not start: ${sessionId}`, error)
      input.done?.(false)
      return false
    }
  }

  /** 按 runId → inputMessageId 登记一条等回执的内部输入。 */
  private trackInternal(runId: string, inputMessageId: string, input: DeferredInput): void {
    // 目标检查没有送达回调;过期项仍由内核丢弃,不能因这次报告确认改动而重新启动。
    if (input.done === undefined) return
    let pending = this.unconfirmed.get(runId)
    if (pending === undefined) {
      pending = new Map()
      this.unconfirmed.set(runId, pending)
    }
    pending.set(inputMessageId, input)
  }

  /** 同 id 的 user message_commit 到了:摘掉表项(空了删 run 键)并 done(true)。 */
  private confirmInternal(runId: string, inputMessageId: string): void {
    const pending = this.unconfirmed.get(runId)
    const input = pending?.get(inputMessageId)
    if (pending === undefined || input === undefined) return
    pending.delete(inputMessageId)
    if (pending.size === 0) this.unconfirmed.delete(runId)
    input.done?.(true)
  }

  private untrackInternal(runId: string, inputMessageId: string): void {
    const pending = this.unconfirmed.get(runId)
    if (pending === undefined) return
    pending.delete(inputMessageId)
    if (pending.size === 0) this.unconfirmed.delete(runId)
  }

  /** 会话空出来了:投递第一条等着的内部输入。起了新 run 返回 true */
  private flushDeferred(sessionId: string): boolean {
    const list = this.deferred.get(sessionId)
    if (list === undefined || list.length === 0) return false
    if (this.deps.isBusy(sessionId)) {
      this.scheduleRetry(sessionId)
      return false
    }
    this.clearRetry(sessionId)
    while (list.length > 0) {
      const next = list.shift()
      if (next === undefined) break
      const result = this.deliverInternal(sessionId, next)
      if (result === 'launched') {
        // 剩下的留给这个新 run:它在跑时投递会进它的信箱,收尾后再从这里继续
        if (list.length === 0) this.deferred.delete(sessionId)
        else this.scheduleRetry(sessionId)
        return true
      }
      if (result === 'deferred') return false
    }
    this.deferred.delete(sessionId)
    return false
  }

  /**
   * 历史操作(手动压缩)占着会话时不会有 `runSettled` 来叫醒等着的输入,
   * 所以另挂一个有上限的重试。
   */
  private scheduleRetry(sessionId: string): void {
    if (this.retries.has(sessionId)) return
    const tick = (attempt: number): void => {
      const timer = setTimeout(() => {
        this.retries.delete(sessionId)
        const list = this.deferred.get(sessionId)
        if (list === undefined || list.length === 0) return
        if (!this.deps.isBusy(sessionId)) {
          this.flushDeferred(sessionId)
          return
        }
        if (this.deps.activeRun(sessionId) !== undefined) {
          // run 在跑:把等着的输入直接塞进它的信箱
          this.flushIntoActive(sessionId)
          return
        }
        if (attempt + 1 >= DEFERRED_MAX_RETRIES) {
          this.deps.log(`[session] gave up delivering internal input: ${sessionId}`)
          this.deferred.delete(sessionId)
          for (const item of list) item.done?.(false)
          return
        }
        tick(attempt + 1)
      }, DEFERRED_RETRY_MS)
      ;(timer as { unref?: () => void }).unref?.()
      this.retries.set(sessionId, timer)
    }
    tick(0)
  }

  private flushIntoActive(sessionId: string): void {
    const list = this.deferred.get(sessionId) ?? []
    this.deferred.delete(sessionId)
    for (const item of list) this.deliverInternal(sessionId, item)
  }

  private clearRetry(sessionId: string): void {
    const timer = this.retries.get(sessionId)
    if (timer === undefined) return
    clearTimeout(timer)
    this.retries.delete(sessionId)
  }

  private rememberOptions(sessionId: string, options: SendOptions): void {
    this.lastOptions.delete(sessionId)
    this.lastOptions.set(sessionId, options)
    if (this.lastOptions.size <= LAST_OPTIONS_MAX) return
    const oldest = this.lastOptions.keys().next().value
    if (oldest !== undefined) this.lastOptions.delete(oldest)
  }

  // ═══════════════════════════════════════════════════════════════
  // 条目
  // ═══════════════════════════════════════════════════════════════

  private load(sessionId: string): Entry {
    const existing = this.entries.get(sessionId)
    if (existing !== undefined) return existing
    const saved = this.deps.readInput(sessionId)
    const entry: Entry = {
      draft: saved?.draft ?? '',
      queued: (saved?.queued ?? []).filter(isLive),
      timer: null,
      dirty: false
    }
    this.entries.set(sessionId, entry)
    return entry
  }

  private stateOf(entry: Entry): SessionInputState {
    return { v: SESSION_INPUT_VERSION, draft: entry.draft, queued: entry.queued.filter(isLive), savedAt: this.deps.now() }
  }

  private snapshot(sessionId: string, entry: Entry): SessionQueueSnapshot {
    return { sessionId, queued: entry.queued.filter(isLive), rev: this.rev }
  }

  /**
   * 队列变了:先落盘,成功之后才递增 rev 并广播,返回回执。
   *
   * ★ 顺序不能反。`write` 失败时会抛,调用方据此回滚内存并抛给上层 ——
   * 若先递增 rev,一条没保存的消息会拿到一个「比谁都新」的回执;
   * 若广播抛在写盘之后,那盘上其实已经写好了,不能被报成失败。
   */
  private commit(sessionId: string, entry: Entry, next?: EntryPatch): SessionQueueResult {
    this.apply(entry, next)
    this.write(sessionId, entry)
    this.rev += 1
    const snapshot = { sessionId, queued: entry.queued.filter(isLive), rev: this.rev }
    try {
      this.deps.emitQueue(snapshot)
    } catch (error) {
      // 已经落盘成功:广播失败只是这一次没送到界面,下一份快照会补上,不能报成失败。
      this.deps.log(`[session] could not broadcast queue: ${sessionId}`, error)
    }
    return { ...snapshot, accepted: true }
  }

  /**
   * 离散的队列操作:先快照、再尝试 commit,失败就把内存回滚到操作前并重新抛出。
   *
   * ★ 快照只拷会变的状态,不含 `timer`(调度对象,不是数据)。`queued` 在
   * 各个操作里都是 immutable 映射,拷一个数组引用就够了。
   */
  private mutate(sessionId: string, entry: Entry, next: EntryPatch): SessionQueueResult {
    const before: EntrySnapshot = { draft: entry.draft, queued: entry.queued, dirty: entry.dirty }
    try {
      return this.commit(sessionId, entry, next)
    } catch (error) {
      this.apply(entry, before)
      throw error
    }
  }

  private apply(entry: Entry, next?: EntryPatch): void {
    if (next === undefined) return
    if (next.draft !== undefined) entry.draft = next.draft
    if (next.queued !== undefined) entry.queued = next.queued
    entry.dirty = next.dirty ?? true
  }

  /**
   * 落盘。**写失败必须重抛** —— 调用方靠这个异常回滚内存、拒绝回 accepted。
   * `dirty` 只有写成功才清:这条会话在没读写盘之前一直留着,`flush` 还能重试。
   */
  private write(sessionId: string, entry: Entry): void {
    if (entry.timer !== null) {
      clearTimeout(entry.timer)
      entry.timer = null
    }
    try {
      this.deps.writeInput(sessionId, this.stateOf(entry))
    } catch (error) {
      entry.dirty = true
      this.deps.log(`[session] could not persist input: ${sessionId}`, error)
      /*
        ★ 有 flush 在跑时**先不报**:那一次可能接着就成功了,而每次按键都降级成
        `blocked` 会让「正在重试」和「一直失败」在界面上没有区别。flush 收尾时
        仍 dirty 才报 blocked（见 `flush`）。
      */
      if (!this.flushWaiters.has(sessionId)) this.reportPersistence(sessionId, 'retrying')
      throw error
    }
    entry.dirty = false
    // 只在之前报过问题时发一次恢复,避免每次按键都推一条「已保存」。
    if (this.persistence.has(sessionId)) this.reportPersistence(sessionId, 'saved')
    this.evictIfIdle(sessionId, entry)
  }

  /** 只报状态变化;同一状态不重复推,`saved` 把该会话从表里摘掉。 */
  private reportPersistence(sessionId: string, state: SessionInputPersistence['state']): void {
    if (state === 'saved') this.persistence.delete(sessionId)
    else this.persistence.set(sessionId, state)
    try {
      this.deps.onPersistenceChange?.({ sessionId, state })
    } catch (error) {
      // 提示送不出去不能反过来影响输入本身。
      this.deps.log(`[session] persistence notice failed: ${sessionId}`, error)
    }
  }

  /** 空条目不常驻:开过几百个会话,这张表也只留着真有未发输入的那几个。 */
  private evictIfIdle(sessionId: string, entry: Entry): void {
    // ★ dirty 的条目不能丢:内存里是唯一一份还没保存的输入,淘汰等于把它弄丢。
    if (entry.timer === null && !entry.dirty && entry.draft === '' && entry.queued.length === 0) {
      this.entries.delete(sessionId)
    }
  }
}

function withoutPromotedAt(q: QueuedInput): QueuedInput {
  const { promotedAt: _promotedAt, ...rest } = q
  return rest
}

/**
 * 一次 run 的请求 → 下一次可以复用的档位。
 *
 * ★ 去掉 `planExecution` 与 `agentType`:前者是「这一轮执行哪份已批准的计划」,
 * 复用到一次后台汇报上就是把同一份计划再执行一遍;后者只属于子代理。
 */
function optionsOf(req: RunRequest): SendOptions {
  const {
    runId: _runId, sessionId: _sessionId, input: _input, inputMessageId: _inputMessageId,
    inputInternal: _inputInternal, inputGoalId: _inputGoalId, planExecution: _planExecution,
    agentType: _agentType, ...options
  } = req
  return options
}
