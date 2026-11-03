/**
 * RunRegistry —— 方案 §4.7:用它,而不是用 AsyncIterable 做事实来源。
 *
 * `AgentSession.run(): AsyncIterable<AgentEvent>` 写起来漂亮,但在 IPC 边界上是陷阱:
 *
 * 1. **异步迭代器只能被订阅一次** —— 主窗 + 快捷小窗看同一个 run 就没救了;
 * 2. **无法重放** —— 渲染层重载,run 还在主进程好好跑着(这正是主进程内核的全部意义),
 *    但 UI 失忆了;
 * 3. **背压反转** —— `webContents.send` 是 fire-and-forget 且无界的,
 *    生成器的背压作用于空气。
 *
 * ★ 本文件**零 electron import**。合批与推送是 `src/main/ipc/agent.ts` 的事,
 * 这里只管「事件发生了」。
 */
import type { AgentError } from '../../shared/agent/error'
import type { AgentEvent, RunSnapshot, RunStatus } from '../../shared/agent/event'
import type { PendingInteraction } from '../../shared/agent/interaction'
import type { InterjectItem } from '../../shared/agent/interject'
import type { PermissionMode } from '../../shared/agent/permission'
import type { RunRequest } from '../../shared/agent/run-request'

export type RunListener = (event: AgentEvent, seq: number) => void
export type Unsubscribe = () => void

/** 日志条目带自己的 seq —— 裁剪之后 seq 与下标不再一一对应(见 trim 的注释) */
interface LogEntry {
  seq: number
  event: AgentEvent
  /** `approxEventBytes` 的估算,进出日志时记账用 */
  bytes: number
}

/**
 * 硬上限,兜底用。正常情况下 trim 会在每个 message_commit 处把日志压得远小于它;
 * 这个数只防「一个 run 跑了一万轮工具却一条消息都没提交」的极端情况。
 */
const MAX_LOG_ENTRIES = 2000

/**
 * 单个 run 事件日志的字节预算(按字符粗算)。
 *
 * ★ 这是**回放**预算,不是模型上下文:模型看到的转录在 `AgentSession` 里,一个字都不受影响。
 * 截掉的只是渲染层重载时用来重建界面的那份副本;已提交的消息在库里,重建时从库里读
 * (快照会带 `logTrimmed`)。条数上限挡不住「几十条大工具输出」,所以要有这一道。
 */
export const RUN_LOG_MAX_BYTES = 16 * 1024 * 1024
/**
 * 已结束的 run 在内存里留多久。这段时间里重载窗口、晚到的 attach 还能拿到终态快照;
 * 过后再看这条会话走库里的终态(`sessions:getPage`),渲染层不会因此停在「运行中」。
 */
export const FINISHED_RUN_TTL_MS = 2 * 60 * 1000
/** 已结束 run 的日志总预算。超了不等 TTL,按结束时间从早到晚回收 */
export const FINISHED_RUN_CACHE_BYTES = 64 * 1024 * 1024

/**
 * 一条事件大约占多少(字符数 + 少量结构开销)。只用于预算,不求精确 ——
 * 精确的做法是 `JSON.stringify`,那要为每条事件多分配一整份字符串。
 */
export function approxEventBytes(value: unknown, depth = 0): number {
  if (typeof value === 'string') return value.length
  if (typeof value !== 'object' || value === null) return 8
  if (depth > 12) return 64
  let n = 16
  if (Array.isArray(value)) {
    for (const item of value) n += approxEventBytes(item, depth + 1)
  } else {
    for (const [key, item] of Object.entries(value)) n += key.length + approxEventBytes(item, depth + 1)
  }
  return n
}

export interface AbortReason {
  /** 'user' = 点了停止;'parent' = 父 run 级联下来;'shutdown' = app 退出 */
  by: 'user' | 'parent' | 'shutdown'
}

export class RunHandle {
  readonly runId: string
  readonly sessionId: string
  readonly workspaceId: string
  readonly parentRunId: string | undefined
  readonly parentSessionId: string | undefined
  readonly depth: number
  /** Metadata of a detached Task run, kept with its authoritative run handle. */
  backgroundTask?: { type: string; description: string }
  /**
   * 用户按了停止,驱动却在宽限期内一直没收尾,被 `launch` 强行收口了(#187)。
   *
   * ★ 置位之后这个 run 在注册表里已经是 `aborted`、会话互斥也已放开 —— 下一轮可能
   * 已经开跑。驱动哪天自己醒过来,它手里那份转录是**过期的**:不能再逐条落盘,
   * 也不能在收尾时整份覆盖库里的历史(那会抹掉下一轮的消息)。
   */
  abandoned = false

  status: RunStatus = 'running'
  readonly startedAt = Date.now()
  endedAt?: number
  /** 已分配的最后一个 seq。**永远单调递增**,不受裁剪影响 */
  seq = 0

  readonly pendingInteractions: PendingInteraction[] = []
  readonly children = new Set<string>()

  private readonly log: LogEntry[] = []
  /** 日志里现存条目的 `bytes` 之和 */
  private logBytes = 0
  /** 头部被条数/字节上限截掉过(见 `RunSnapshot.logTrimmed`) */
  private logTrimmed = false
  private readonly listeners = new Set<RunListener>()
  private readonly beforeFinishListeners = new Set<(status: RunStatus, error?: AgentError) => void>()
  private readonly controller = new AbortController()
  /**
   * 插话信箱 —— 渲染层放进来,`AgentSession` 在轮次边界取走(见 `takeInterject`)。
   *
   * ★ 为什么挂在 handle 上,而不是 `AgentSession` 上:handle 是 IPC 层**唯一**
   * 能按 runId 找到的那个对象(`runs.get`),而 session 实例连注册表都不进。
   * 想让 IPC 直接够到 session 就得再维护一张 runId → session 的表,
   * 那张表的生命周期与 handle 完全重合 —— 两份状态,同一条命,迟早对不上。
   */
  private interject: InterjectItem[] = []
  private readonly internalInterject = new Map<string, InterjectItem>()

  /**
   * 权限档位的**当前生效值**——初始等于 `RunRequest.permissionMode`(那份是
   * run 开始时的快照,定义见 `run-request.ts:143`,永远不变,给「重新生成」
   * 「目标续跑」这类要读「这轮当初是什么样」的地方用)。
   *
   * 需求:用户中途把界面上的权限药丸调宽,应该立刻对这个 run **接下来**的
   * 工具调用生效,而不必等到下一条新消息(旧行为,原因是 `approveWith` 直接读
   * `req.permissionMode`,而那是不可变快照)。这里额外开一个可变字段,由
   * `agent:setPermissionMode` 写入,`approveWith`/`childRequestFor` 改读它。
   * 不满足会怎样:用户已经点了「完全访问」,这一轮后续每一次工具调用依然
   * 弹出审批,和界面上刚选的档位对不上,只能等下一句话才生效。
   *
   * ★ 只影响**还没做出的**审批判定,不会回头解出已经在等用户点的那个弹窗——
   * 那个弹窗可能是因为钩子/工作区 ask 规则强制问人(`decideAfterHooks` 里
   * `forcedAsk` 压过档位),放宽档位不该替用户把它自动点掉。
   */
  private livePermissionMode: PermissionMode

  constructor(req: RunRequest) {
    this.runId = req.runId
    this.sessionId = req.sessionId
    this.workspaceId = req.workspaceId
    this.parentRunId = req.parentRunId
    this.parentSessionId = req.parentSessionId
    this.depth = req.depth
    this.livePermissionMode = req.permissionMode
  }

  /** 当前生效的权限档位——见 `livePermissionMode` 字段注释 */
  get permissionMode(): PermissionMode {
    return this.livePermissionMode
  }

  /** `agent:setPermissionMode` 的落点:切换发生在**这个 run 身上**,不改 `RunRequest` 快照 */
  setPermissionMode(mode: PermissionMode): void {
    this.livePermissionMode = mode
  }

  /** ★ 必传给每个工具的 ctx.signal(方案 §4.3)—— 只断 SSE 不断工具会留下僵尸进程 */
  get signal(): AbortSignal {
    return this.controller.signal
  }

  // ─── 发射 ───

  emit(event: AgentEvent): number {
    if (this.status !== 'running' && event.type !== 'run_end') {
      // run 结束后还有事件漏进来,说明某处的收尾漏了 —— 丢掉但不静默
      console.warn(`[run] ${this.runId} 已是 ${this.status},丢弃迟到事件 ${event.type}`)
      return this.seq
    }

    const seq = ++this.seq
    const bytes = approxEventBytes(event)
    this.log.push({ seq, event, bytes })
    this.logBytes += bytes

    // 待决交互表跟着事件走,attach 时才有东西可还原(方案 §4.6)
    if (event.type === 'interaction_request') {
      this.pendingInteractions.push(event.interaction)
    } else if (event.type === 'interaction_resolved') {
      const i = this.pendingInteractions.findIndex((p) => p.id === event.id)
      if (i >= 0) this.pendingInteractions.splice(i, 1)
    } else if (event.type === 'subagent_start') {
      this.children.add(event.childRunId)
    } else if (event.type === 'message_commit') {
      this.trimSupersededDeltas()
    } else if (event.type === 'tool_end') {
      this.trimSupersededProgress(event.callId)
    } else if (event.type === 'run_end') {
      this.status = event.status
      this.endedAt ??= event.at ?? Date.now()
    }

    if (this.log.length > MAX_LOG_ENTRIES) this.dropHead(this.log.length - MAX_LOG_ENTRIES)
    if (this.logBytes > RUN_LOG_MAX_BYTES) {
      // 从头数到够为止;最新那一条永远留着(它正要推给订阅者,快照里也得有它)
      let excess = this.logBytes - RUN_LOG_MAX_BYTES
      let n = 0
      while (n < this.log.length - 1 && excess > 0) excess -= this.log[n++]!.bytes
      this.dropHead(n)
    }

    for (const l of this.listeners) l(event, seq)
    if (event.type === 'run_end') this.listeners.clear()
    return seq
  }

  /** 这份日志现在大约占多少字节 —— 注册表回收已结束 run 时按它记账 */
  get retainedLogBytes(): number {
    return this.logBytes
  }

  private dropHead(n: number): void {
    if (n <= 0) return
    for (const entry of this.log.splice(0, n)) this.logBytes -= entry.bytes
    this.logTrimmed = true
  }

  private removeAt(i: number): void {
    const [entry] = this.log.splice(i, 1)
    if (entry !== undefined) this.logBytes -= entry.bytes
  }

  /**
   * 一次工具调用结束时,它之前的 `tool_progress` 全部作废 —— 渲染层的 reducer 在 `tool_end`
   * 处把过程文字、实时卡片、逐张到达的生图(`partialImages`)一并清掉,以 `output` 为准。
   * 所以清掉它们对重放是**无损**的;不清的话,一次生图的每张过程图都会在日志里多留一份。
   */
  private trimSupersededProgress(callId: string): void {
    for (let i = this.log.length - 2; i >= 0; i--) {
      const event = this.log[i]?.event
      if (event === undefined) continue
      if (event.type === 'tool_start' && event.callId === callId) break
      if (event.type === 'tool_progress' && event.callId === callId) this.removeAt(i)
    }
  }

  /**
   * ★ 裁剪策略:message_commit 一到,它**之前**的 stream delta 就是冗余的 ——
   * 提交的消息已经包含了那些 delta 拼出来的全部内容。
   *
   * 这让裁剪对「重放能否还原 UI」是**无损**的:重放拿到的是
   * 结构性事件 + 已提交的完整消息 + 最后一次提交之后的增量 delta,正好够重画。
   * 单纯从头砍的环形缓冲做不到这一点(会在转录中间留一个真的洞)。
   *
   * 代价是 seq 在日志里不再连续 —— 所以 LogEntry 要自带 seq,
   * 而重放用 filter 而不是 slice。
   */
  private trimSupersededDeltas(): void {
    // 刚 push 进来的 commit 在末位,所以从 length-2 起往回清。
    // 倒序遍历 + 就地 splice 是安全的:splice(i) 不影响任何 < i 的下标。
    for (let i = this.log.length - 2; i >= 0; i--) {
      const entry = this.log[i]
      if (entry === undefined) continue
      // 撞到上一个提交就停。这是**扫描边界,不是正确性守卫** ——
      // 更早的 delta 在那次提交时已经清过了,去掉这行结果一样,只是每次提交
      // 都要扫全表。(所以没有测试覆盖它,别去找。)
      if (entry.event.type === 'message_commit') break
      // 只清 delta。tool_start / tool_end / interaction_* 是**结构性**的,
      // 留着几乎不占地方,而重放时 UI 要靠它们重建工具卡片。
      // Model and API usage are not contained in the committed message. Keep
      // them so a renderer reload can reconstruct the same run totals.
      if (entry.event.type === 'stream'
        && entry.event.delta.type !== 'message_start'
        && entry.event.delta.type !== 'message_end') this.removeAt(i)
    }
  }

  // ─── 订阅(多消费者) ───

  on(listener: RunListener): Unsubscribe {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  beforeFinish(listener: (status: RunStatus, error?: AgentError) => void): Unsubscribe {
    this.beforeFinishListeners.add(listener)
    return () => this.beforeFinishListeners.delete(listener)
  }

  get listenerCount(): number {
    return this.listeners.size
  }

  // ─── 重放 ───

  /**
   * sinceSeq 之后的事件。渲染层重载后用它补齐。
   *
   * ★ 重放出来的 seq 可能不连续(见 trimSupersededDeltas)——
   * 渲染层**不能**对重放事件跑「seq !== lastSeq + 1」的补齐逻辑,
   * 应用完之后直接把 lastSeq 置成 snapshot.seq。
   */
  since(sinceSeq: number): AgentEvent[] {
    return this.log.filter((e) => e.seq > sinceSeq).map((e) => e.event)
  }

  snapshot(sinceSeq: number): RunSnapshot {
    return {
      runId: this.runId,
      sessionId: this.sessionId,
      workspaceId: this.workspaceId,
      ...(this.parentRunId !== undefined ? { parentRunId: this.parentRunId } : {}),
      depth: this.depth,
      status: this.status,
      startedAt: this.startedAt,
      ...(this.endedAt === undefined ? {} : { endedAt: this.endedAt }),
      seq: this.seq,
      events: this.since(sinceSeq),
      pendingInteractions: [...this.pendingInteractions],
      children: [...this.children],
      ...(this.logTrimmed ? { logTrimmed: true } : {})
    }
  }

  // ─── 插话信箱 ───

  /**
   * ★ **整份替换**,与 `agent:interject` 的契约一致。理由见那条频道的注释:
   * 取消/编辑/删除全都退化成「重发一次当前全集」,乱序到达也收敛。
   */
  setInterject(items: readonly InterjectItem[]): void {
    // Renderer queue replacement must not erase a background result or goal kickoff.
    if (items.length === 0 || items.some((item) => !item.internal)) {
      this.interject = items.filter((item) => !item.internal).map((item) => ({ id: item.id, parts: [...item.parts] }))
    }
    for (const item of items) {
      if (item.internal) this.enqueueInternal(item)
    }
  }

  enqueueInternal(item: InterjectItem): void {
    if (this.signal.aborted || this.status !== 'running') return
    this.internalInterject.set(item.id, { ...item, parts: [...item.parts], internal: true })
  }

  /**
   * 取走并清空。**take 而不是 get** —— 读取即消费是这里唯一能防重复注入的机制:
   * 留一份在信箱里,下一个轮次边界会把同一条消息再注入一遍,而两条 id 相同的
   * 用户消息进同一份转录,上游看到的是一个自相矛盾的历史。
   */
  takeInterject(): InterjectItem[] {
    const taken = [...this.interject, ...this.internalInterject.values()]
    this.interject = []
    this.internalInterject.clear()
    return taken
  }

  // ─── 中断 ───

  /**
   * ★ 这里只做方案 §4.8 五件事里的第 1、3、5 件(取消 SSE、取消工具、级联子 run)。
   * 第 2 件(拒绝待决交互)由 InteractionGate 挂在同一个 signal 上(步骤 5),
   * 第 4 件(给未闭合的 tool_call 补 tool_result)由 AgentSession 做(步骤 4)——
   * 因为只有它知道转录长什么样。**漏掉第 4 件下一轮请求就是 400。**
   */
  abort(_reason: AbortReason): void {
    if (this.status !== 'running') return
    this.controller.abort()
  }

  finish(status: RunStatus, error?: AgentError): void {
    if (this.status !== 'running') return
    this.endedAt = Date.now()
    for (const listener of this.beforeFinishListeners) listener(status, error)
    this.beforeFinishListeners.clear()
    this.emit(error
      ? { type: 'run_end', status, error, at: this.endedAt }
      : { type: 'run_end', status, at: this.endedAt })
  }
}

export class SessionBusyError extends Error {
  constructor(readonly sessionId: string) {
    super(`会话 ${sessionId} 有运行中的 Agent 或历史操作，请等待完成后再试`)
    this.name = 'SessionBusyError'
  }
}

export class RunRegistry {
  private readonly runs = new Map<string, RunHandle>()
  private readonly sessionOperations = new Map<string, { token: object; runId?: string }>()
  private readonly abortAllListeners = new Set<() => void>()
  private readonly activeListeners = new Set<() => void>()

  onAbortAll(listener: () => void): Unsubscribe {
    this.abortAllListeners.add(listener)
    return () => this.abortAllListeners.delete(listener)
  }

  /**
   * 「还有哪些顶层 run 活着」变了。
   *
   * 需求:渲染层的运行中指示必须能在**没订阅过那个 run** 的情况下也收敛
   * (定时任务的 run、⌘R 重载后还没打开的会话)。事件流做不到这件事 ——
   * 它按 run 主题定向推,没有订阅者时 `RunPump.flush()` 整批丢弃。
   * 不满足会怎样:run 早就结束了,外层工作区 Tab 上的圆点一直转到应用重启。
   *
   * ★ 只为**顶层** run 通知。子 run 的起止不改变这个集合(渲染层的角标本来就
   * 不数子 run,见 `adoptActiveSubagents`),一次编排派十几个子代理时,
   * 在这里不设防就是十几次内容完全相同的广播。
   */
  onActiveChange(listener: () => void): Unsubscribe {
    this.activeListeners.add(listener)
    return () => this.activeListeners.delete(listener)
  }

  private notifyActiveChange(): void {
    for (const listener of this.activeListeners) listener()
  }

  activeBackgroundChildrenOfSession(sessionId: string): RunHandle[] {
    return [...this.runs.values()].filter((handle) => handle.parentSessionId === sessionId
      && handle.backgroundTask !== undefined && handle.status === 'running' && !handle.signal.aborted)
  }

  isSessionBusy(sessionId: string): boolean {
    return this.sessionOperations.has(sessionId) || [...this.runs.values()]
      .some((handle) => handle.sessionId === sessionId && handle.status === 'running')
  }

  /** 这条会话此刻正在跑的**顶层** run(子代理有自己的派生会话,不算在内) */
  activeTopLevelRun(sessionId: string): RunHandle | undefined {
    for (const handle of this.runs.values()) {
      if (handle.sessionId === sessionId && handle.depth === 0 && handle.status === 'running') return handle
    }
    return undefined
  }

  hasSessionOperations(): boolean {
    return this.sessionOperations.size > 0
  }

  /** 检查必须早于订阅和导入脱离等副作用；真正 create 时仍再次检查。 */
  assertCanCreate(req: RunRequest): void {
    if (this.runs.has(req.runId)) throw new Error(`run 已存在: ${req.runId}`)
    if (this.isSessionBusy(req.sessionId)) throw new SessionBusyError(req.sessionId)
  }

  /** 手动压缩等跨 await 的历史操作，由主进程持有互斥，而不是依赖某个窗口的按钮。 */
  acquireSessionOperation(sessionId: string): Unsubscribe {
    if (this.isSessionBusy(sessionId)) throw new SessionBusyError(sessionId)
    return this.reserveSession(sessionId)
  }

  /** run_end 先于驱动的 finally；收尾落盘完成前不能让下一轮使用同一会话。 */
  retainSessionForRun(handle: RunHandle): Unsubscribe {
    if (this.runs.get(handle.runId) !== handle || this.sessionOperations.has(handle.sessionId)) {
      throw new SessionBusyError(handle.sessionId)
    }
    return this.reserveSession(handle.sessionId, handle.runId)
  }

  private reserveSession(sessionId: string, runId?: string): Unsubscribe {
    const token = {}
    this.sessionOperations.set(sessionId, { token, ...(runId === undefined ? {} : { runId }) })
    return () => {
      if (this.sessionOperations.get(sessionId)?.token === token) this.sessionOperations.delete(sessionId)
    }
  }

  create(req: RunRequest): RunHandle {
    this.assertCanCreate(req)

    const handle = new RunHandle(req)
    this.runs.set(req.runId, handle)

    if (req.parentRunId !== undefined) {
      this.runs.get(req.parentRunId)?.children.add(req.runId)
    } else {
      /*
        ★ 这个监听器**不影响 `reap()`**:`emit()` 在派发完 run_end 之后会
        `listeners.clear()`,所以 `listenerCount` 立刻回到 0。
        (`reap` 拿 listenerCount 当「还有没有人在看」的判据,常驻监听器会让
        已结束的 run 永远回收不掉,内存里那份事件日志跟着一起留下。)
      */
      handle.on((event) => {
        if (event.type === 'run_end') this.notifyActiveChange()
      })
      this.notifyActiveChange()
    }
    return handle
  }

  get(runId: string): RunHandle | undefined {
    return this.runs.get(runId)
  }

  /** 外层工作区 Tab 的运行中角标就是数这个 —— 数据源是 registry,不是任何 UI 状态 */
  runningIn(workspaceId: string): RunHandle[] {
    return [...this.runs.values()].filter(
      (r) => r.workspaceId === workspaceId && r.status === 'running'
    )
  }

  activeRunIds(): string[] {
    return [...this.runs.values()].filter((r) => r.status === 'running').map((r) => r.runId)
  }

  /**
   * 某个 run 名下**还在跑**的子 run。
   *
   * ★ 数的是「还在跑的」,不是 `handle.children.size` —— 后者是**累计**的
   * (`children` 只加不减),用它当并发闸门的话,一次 run 里派满 N 个子代理之后
   * 就再也派不出第五个了,哪怕它们早就全部结束。
   */
  activeChildrenOf(runId: string): RunHandle[] {
    const parent = this.runs.get(runId)
    if (!parent) return []
    return [...parent.children]
      .map((id) => this.runs.get(id))
      .filter((h): h is RunHandle => h !== undefined && h.status === 'running')
  }

  activeChildCount(runId: string): number {
    return this.activeChildrenOf(runId).length
  }

  /**
   * 全局仍在运行的子 run id。
   *
   * ★ 要 id 而不只是计数,是因为子代理并发队列的**死锁判据**要拿这些 id
   * 去和「正卡在队列里等名额的 run」求交集:占着名额的那些自己也在等,
   * 就没有任何人能腾出空位(见 `subagent-queue.ts`)。
   */
  activeSubagentRunIds(): string[] {
    return [...this.runs.values()]
      .filter((r) => r.parentRunId !== undefined && r.status === 'running')
      .map((r) => r.runId)
  }

  /** 全局仍在运行的子 run 数,用于应用级子代理并发上限。 */
  activeSubagentCount(): number {
    return this.activeSubagentRunIds().length
  }

  /** 级联中断(方案 §4.8 第 5 件):父 run 停,子 run 一起停 */
  abort(runId: string, cascade: boolean, reason: AbortReason = { by: 'user' }): void {
    const handle = this.runs.get(runId)
    if (!handle) return
    if (cascade) {
      for (const childId of handle.children) {
        this.abort(childId, true, { by: 'parent' })
      }
    }
    handle.abort(reason)
  }

  abortAll(reason: AbortReason = { by: 'shutdown' }): void {
    for (const listener of this.abortAllListeners) listener()
    for (const id of this.activeRunIds()) this.abort(id, false, reason)
  }

  /**
   * ⚠️ **只给测试。**清空整张表。
   *
   * ★ 为什么 `reap()` 不够:用例的 `afterEach` 走的是 `abortAll()`,而 `abort()`
   * **不置 status**(见上面那条注释)—— 于是每个用例都往这张进程内单例表里
   * 留下几条永远 `running` 的 run。以前这无所谓(并发闸门只是多拒一次,
   * 断言 refused 的用例照样绿);改成排队之后,残留的占位者会把下一个用例的
   * 派发**永久挂起**,表现为一串莫名其妙的超时。
   */
  clearForTest(): void {
    this.runs.clear()
    this.sessionOperations.clear()
  }

  /**
   * 回收已结束的 run。转录在 SQLite 里,内存里那份事件日志只是给重载/晚到的 attach 用的。
   *
   * 一个 run 只有同时满足这些才回收:
   * - 已结束,且驱动的收尾已经放开会话互斥(`retainSessionForRun` 那把锁);
   * - 没有监听者(`run_end` 之后监听者会被清空,还挂着的说明有人在等它);
   * - 没有还在跑的子 run —— 父 run 的 Task 卡片要靠它找子代理;
   * - 调用方没有钉住它(`pinned`:比如还有挂在它名下、等人点的交互);
   * - 已经结束满 `ttlMs`;或者已结束 run 的日志总量超过 `budgetBytes`,那就从最早结束的回收起,
   *   直到降回预算 —— 但上面几条照样要满足。
   *
   * 缺省参数(`ttlMs = 0`、不限预算)= 「能收的立刻全收」,与这个方法最初的语义一致。
   */
  reap(options: {
    now?: number
    ttlMs?: number
    budgetBytes?: number
    pinned?: (handle: RunHandle) => boolean
    /** 每回收一个调一次 —— 让外层摘掉挂在它身上的订阅等引用 */
    onReap?: (handle: RunHandle) => void
  } = {}): number {
    const now = options.now ?? Date.now()
    const ttlMs = options.ttlMs ?? 0
    const budget = options.budgetBytes ?? Number.POSITIVE_INFINITY
    const candidates: RunHandle[] = []
    let finishedBytes = 0
    for (const [id, h] of this.runs) {
      if (h.status === 'running') continue
      finishedBytes += h.retainedLogBytes
      if (h.listenerCount === 0
        && this.sessionOperations.get(h.sessionId)?.runId !== id
        && !this.hasRunningDescendant(id, new Set())
        && options.pinned?.(h) !== true) candidates.push(h)
    }
    candidates.sort((a, b) => (a.endedAt ?? 0) - (b.endedAt ?? 0))
    let n = 0
    for (const h of candidates) {
      const expired = now - (h.endedAt ?? 0) >= ttlMs
      if (!expired && finishedBytes <= budget) continue
      this.runs.delete(h.runId)
      finishedBytes -= h.retainedLogBytes
      n++
      options.onReap?.(h)
    }
    return n
  }

  /**
   * 子孙里还有没有在跑的。★ 要往下走到底:后台子代理的收尾(变更集归属、交差)要沿
   * `parentRunId` 一路爬到根,中间任何一层被回收,那条链就断在那里。
   */
  private hasRunningDescendant(runId: string, seen: Set<string>): boolean {
    if (seen.has(runId)) return false
    seen.add(runId)
    for (const childId of this.runs.get(runId)?.children ?? []) {
      const child = this.runs.get(childId)
      if (child === undefined) continue
      if (child.status === 'running' || this.hasRunningDescendant(childId, seen)) return true
    }
    return false
  }
}

export const runs = new RunRegistry()

// ═══════════════════════════════════════════════════════════════
// 测试专用适配器(方案 §4.7)
// ═══════════════════════════════════════════════════════════════

/** 收齐一个 run 的全部事件。步骤 4 的无头 vitest 快照就靠它。 */
export function collect(h: RunHandle): Promise<AgentEvent[]> {
  return new Promise((resolve) => {
    const acc: AgentEvent[] = h.since(0)
    if (h.status !== 'running') {
      resolve(acc)
      return
    }
    const off = h.on((event) => {
      acc.push(event)
      if (event.type === 'run_end') {
        off()
        resolve(acc)
      }
    })
  })
}

/**
 * ⚠️ 只给测试用。异步生成器的 `finally` **只在消费者调用 `.return()` 时才执行** ——
 * `for await` + `break` 会调用,手写 `.next()` 泵不会。所以生产路径一律用 `on()`。
 */
export async function* toAsyncIterable(h: RunHandle): AsyncIterable<AgentEvent> {
  const queue: AgentEvent[] = h.since(0)
  let notify: (() => void) | null = null
  let ended = h.status !== 'running'

  const off = h.on((event) => {
    queue.push(event)
    if (event.type === 'run_end') ended = true
    notify?.()
  })

  try {
    while (true) {
      while (queue.length > 0) {
        const next = queue.shift()
        if (next !== undefined) yield next
      }
      if (ended) return
      await new Promise<void>((r) => {
        notify = r
      })
      notify = null
    }
  } finally {
    off()
  }
}
