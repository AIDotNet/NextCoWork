/**
 * Agent 运行服务 —— run 的生命周期与会话续跑。
 *
 * 从 `ipc/agent.ts` 里抽出来:那一层本来的职责只有「合批与推送」(见它文件头),
 * 但它顺带把**运行本身**也攥在手里,于是「主进程自己起一轮、没人看着也照样跑完」
 * 这件事离不开 electron —— 控制台重连、无头进程都起不来。
 *
 * 所以这里接手真正属于运行的那一半:泵与合批、launch、回收定时器、
 * 活跃 run 索引、子 run 的派发、shutdown 收尾,以及 `SessionRuntime` 的装配。
 * 窗口的那一半(授权、订阅、往哪个 webContents 推)留在 `ipc/agent.ts`,
 * 由它显式**安装一个投递端口**接进来。
 *
 * ## 端口
 *
 * ★ 没有装端口时本文件**照常跑**:事件没有订阅者就整批不推(见 `RunPump.flush`),
 * 而 run、转录与权威持久化一切照旧。这正是「Node 里能直接 import 并运行」的含义 ——
 * 事件推送是可选的,run 本身不是。
 *
 * ★ 端口是**安装式**的,不是构造参数:子 run 的启动器(`runtime` 的插槽)按设计
 * 永远不 import 这一层,而 `SessionRuntime` 又必须在模块加载时就装配好、好让
 * `ipc/index.ts` 直接读 `sessionRuntime`。安装式让「谁先加载」不再有顺序要求。
 *
 * ★ 本文件**零 electron import** —— 端口不碰窗口,`RunDriver` 的默认值是真驱动
 * `runtime.runAgent`(那条链按设计也是零 electron,见 runtime.ts 文件头)。
 */
import type { ActiveRunEntry, AgentEvent, RunSnapshot } from '../../shared/agent/event'
import type { RunRequest, SendOptions } from '../../shared/agent/run-request'
import type { SessionInputPersistence, SessionQueueOp, SessionQueueResult, SessionQueueSnapshot, SubagentReportStatus } from '../../shared/domain/queued-input'
import { isValidSessionInput, partsToAttachments } from '../../shared/domain/queued-input'
import { ulid } from '../../shared/util/id'
import { interactions } from '../kernel/interaction-gate'
import { agentShells } from '../agent-shells'
import { IpcError, toAgentError } from '../ipc/errors'
import { FINISHED_RUN_CACHE_BYTES, FINISHED_RUN_TTL_MS, type RunHandle, runs } from '../kernel/run-registry'
import { ensureGoalRuntime, persistSubagentReportStatus, readBackgroundReport, runAgent } from '../runtime'
import { pauseGoal, restoreGoal } from '../goal/runtime'
import { getActiveGoal } from '../goal/state'
import { SessionRuntime } from '../session-runtime'
import { sessionInputKey, store } from '../state/store'

/**
 * 16ms ≈ 一帧。方案 §8 给的是 16–33ms:再快没意义(渲染层反正等 rAF),
 * 再慢就能看出打字延迟了。
 */
const FLUSH_MS = 16
/** 单批上限。工具吐出几万行时,时间窗内能攒出一个大到卡住克隆的批。 */
const MAX_BATCH = 64

/**
 * ★ 只有这三种 delta 值得攒。其余事件都是**结构性**的 ——
 * 消息提交、工具起止、待决交互、run 结束 —— 它们要么改变 UI 结构,
 * 要么用户正等着它出现。攒起来只会让界面显得迟钝,省不下几条消息。
 *
 * 这同时实现了方案 §8 的「内容块边界立即 flush」:块边界事件本身就不可合批。
 */
function isCoalescable(e: AgentEvent): boolean {
  if (e.type !== 'stream') return false
  const t = e.delta.type
  return t === 'text_delta' || t === 'thinking_delta' || t === 'tool_call_delta'
}

/**
 * 一个 run 一个泵。
 *
 * ★ 批内 seq 必须连续 —— 信封只带最后一个 seq,渲染层用
 * `envelopeFirstSeq()` 反推第一个。所以**攒进 buf 的事件一个都不能丢**,
 * 哪怕当前没有窗口在看。想省这个开销就得整批跳过(见 flush 里的注释)。
 *
 * 这条不变式的另一半在 `shared/ipc/contract.ts` 的 `hasSeqGap()` —— 改这里的
 * `seq: this.lastSeq` 就必须同时改那边,否则渲染层每收一批就 attach 一次。
 */
class RunPump {
  private buf: AgentEvent[] = []
  private lastSeq = 0
  private timer: NodeJS.Timeout | null = null
  private readonly topic: string

  constructor(private readonly handle: RunHandle) {
    this.topic = runTopic(handle.runId)
    handle.on((event, seq) => this.push(event, seq))
  }

  private push(event: AgentEvent, seq: number): void {
    this.buf.push(event)
    this.lastSeq = seq

    if (!isCoalescable(event) || this.buf.length >= MAX_BATCH) {
      this.flush()
      if (event.type === 'run_end') pumps.delete(this.handle.runId)
      /*
        ★ 「有个审批在等你」走全局角标,不走正文:没有窗口在看这条会话时正文一条都不推,
        而那时恰恰最该让用户知道。子代理的审批同样要算到它所属的顶层会话上。
      */
      if (event.type === 'interaction_request' || event.type === 'interaction_resolved') broadcastActiveRuns()
      return
    }
    if (this.timer === null) this.timer = setTimeout(() => this.flush(), FLUSH_MS)
  }

  flush(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer)
      this.timer = null
    }
    if (this.buf.length === 0) return

    const events = this.buf
    this.buf = []

    // 没人订阅时**整批丢弃**,而不是逐条丢:run 继续跑,日志继续记,
    // 窗口回来时走 attach 重放。这就是 hasSubscribers 存在的意义 ——
    // 停的是推送,不是 run。
    if (!hasSubscribersSafely(this.topic)) return

    notifyDelivery(() => delivery.emitToTopic(this.topic, 'agent:event', {
      runId: this.handle.runId,
      seq: this.lastSeq,
      events
    }))
  }
}

const pumps = new Map<string, RunPump>()

/**
 * run 主题串。★ 与 `window/registry.ts` 的 `runTopic` 是同一个形状,但这里
 * **不能 import 它** —— 那份 registry 拖着 electron 的值 import(`BrowserWindow`),
 * 而本文件要能在纯 Node 里跑。端口就按这个串去问订阅、按这个串推。
 */
export function runTopic(runId: string): string {
  return `run:${runId}`
}

// ═══════════════════════════════════════════════════════════════
// 投递端口
// ═══════════════════════════════════════════════════════════════

/** `agent:event` 信封的形状。★ 与 `shared/ipc/contract.ts` 的 `AgentEventEnvelope` 同构 */
export interface AgentEventEnvelopePort {
  runId: string
  seq: number
  events: AgentEvent[]
}

/** 全局事件频道 —— 与 `shared/ipc/contract.ts` 的 `GlobalEventChannel` 取子集 */
export type GlobalAgentChannel =
  | 'agent:activeRuns'
  | 'session:queueChanged'
  | 'session:subagentReport'
  | 'session:inputPersistence'
  | 'imports:changed'

export interface GlobalAgentEventMap {
  'agent:activeRuns': { runs: ActiveRunEntry[] }
  'session:queueChanged': SessionQueueSnapshot
  'session:subagentReport': { sessionId: string; callId: string; status: SubagentReportStatus }
  'session:inputPersistence': SessionInputPersistence
  'imports:changed': { sourceId: string; jobId?: string; seq: number }
}

/**
 * 往窗口送事件的那个东西。桌面侧由 `ipc/agent.ts` 安装,转发给 `WindowRegistry`;
 * 没有任何窗口(控制台、无头)时**不装**,事件只停推送。
 *
 * ★ 参数是**类型化**的频道与载荷,不是 `any` —— 端口两边靠类型对齐,
 * 而不是靠调用点记得传对字段。
 */
export interface AgentDeliveryPort {
  hasSubscribers(topic: string): boolean
  /** 定向推:只发给订阅了这个 run 主题的窗口 */
  emitToTopic<K extends 'agent:event'>(topic: string, channel: K, payload: AgentEventEnvelopePort): void
  /** 全局推:「谁在跑」「队列欠着什么」是应用级事实,与订阅无关 */
  emitGlobal<K extends GlobalAgentChannel>(channel: K, payload: GlobalAgentEventMap[K]): void
  /** 主题不会再有人听了(run 被回收):把订阅表那一格删掉,别随 run 数增长 */
  dropTopic(topic: string): void
}

/**
 * 默认端口:一个窗口都没有。
 *
 * ★ 「没有订阅者」不是错误状态,而是一个合法宿主 —— 控制台重连、无头进程、
 * 测试都属于这一类。此时 run 照跑、转录照写,只是没有任何一条 IPC 出去。
 */
const silentDelivery: AgentDeliveryPort = {
  hasSubscribers: () => false,
  emitToTopic: () => {},
  emitGlobal: () => {},
  dropTopic: () => {}
}

let delivery: AgentDeliveryPort = silentDelivery
let beforeStart: (() => void) | null = null

/** 生命周期闸门独立于事件订阅；断开控制端不能顺带解除升级/停服限制。 */
export function installAgentStartGuard(guard: (() => void) | null): void {
  beforeStart = guard
}

/** 控制端断开/观察者异常不能打断权威执行或已经提交的写入。 */
function notifyDelivery(send: () => void): void {
  try { send() } catch (error) {
    console.warn('[agent] delivery failed; execution continues', error)
  }
}

function hasSubscribersSafely(topic: string): boolean {
  try { return delivery.hasSubscribers(topic) } catch (error) {
    console.warn('[agent] subscription check failed; execution continues', error)
    return false
  }
}

/**
 * 装/换投递端口。桌面在 `registerIpc` 之前装一次;测试可以装自己的探针。
 * 传 `null` 恢复静默 —— 端口是进程级单例,用例之间必须能复位。
 */
export function installAgentDelivery(port: AgentDeliveryPort | null): void {
  delivery = port ?? silentDelivery
}

// ═══════════════════════════════════════════════════════════════
// run 索引
// ═══════════════════════════════════════════════════════════════

/**
 * 顶层活跃 run 的权威快照。
 *
 * 需求:bootstrap 的首帧(`app:getBootstrap`)和之后的 `agent:activeRuns` 广播
 * 说的是同一件事,所以口径只能有一份 —— 两边各写一遍 `parentRunId` 这个判据的话,
 * 漏掉的那一边会把子代理也算成一条会话级的 run,表现为工作区 Tab 上多一颗
 * 永远不灭的圆点(子 run 的结束在渲染层根本没有人在收)。
 */
export function activeRunIndex(): ActiveRunEntry[] {
  return runs.activeRunIds().flatMap((id) => {
    const run = runs.get(id)
    if (run === undefined || run.parentRunId !== undefined) return []
    const pending = pendingInteractionCount(run)
    return [{
      runId: run.runId, sessionId: run.sessionId, workspaceId: run.workspaceId, status: run.status,
      ...(pending === 0 ? {} : { pendingInteractions: pending })
    }]
  })
}

/** 一个顶层 run 连同它派出的子代理,一共有几条审批 / 提问在等用户 */
function pendingInteractionCount(handle: RunHandle): number {
  let count = handle.pendingInteractions.length
  for (const childId of descendantRunIds(handle)) count += runs.get(childId)?.pendingInteractions.length ?? 0
  return count
}

/**
 * 把权威集合播给**所有**窗口。`registerIpc` 里挂一次,run 起止时自动触发。
 *
 * ★ 这条不是 `agent:event` 的替代,两者各管一头:事件流按订阅定向推(转录内容
 * 只给在看的那个窗口),而「谁在跑」是全局事实,所有窗口的角标都读它。
 * 没有这条广播,没订阅过的 run(定时任务起的、⌘R 之后还没打开的会话)
 * 结束时渲染层收不到任何东西,角标就一直转着 —— 且全程零报错。
 */
export function broadcastActiveRuns(): void {
  notifyDelivery(() => delivery.emitGlobal('agent:activeRuns', { runs: activeRunIndex() }))
}

// ═══════════════════════════════════════════════════════════════
// 启动
// ═══════════════════════════════════════════════════════════════

/**
 * 驱动一个 run 的东西。默认是真的 `AgentSession`(经 `runtime.runAgent`)。
 *
 * ★ 这个接缝之所以存在,是因为**假发射器与真 session 对 `RunHandle` 的用法完全一致** ——
 * 那正是「第 3 步就能看到端到端流式」成立的原因,现在它反过来还有用:
 * 假发射器把**泵**单独拎出来测。拿真 session 测合批窗口是碰运气 ——
 * 它的事件时序取决于上游怎么切片。
 *
 * 做成参数而不是一个可写的模块变量:后者要在每个测试后复位,
 * 而忘了复位的那个测试会莫名其妙地跑起真上游来。
 */
export type RunDriver = (handle: RunHandle, req: RunRequest) => void | Promise<void>

/**
 * `agent:run` 的结果。`started: false` = 这条会话其实已经有一个 run 在跑
 * (排队的消息刚续上、或者另一个窗口先发了),这句话被**放进了队列**,
 * 回执里是新的队列 —— 渲染层据此撤掉乐观消息,而不是把它当成一次失败。
 */
export type AgentRunStart = { started: true } | { started: false; queue: SessionQueueResult }

/**
 * 起一个用户发起的顶层 run。
 *
 * ★ 顺序是契约,不是风格:`beforeLaunch` 在校验之后、`launch` 之前跑
 * —— 桌面用它在 driver 发出第一个事件之前把窗口的订阅挂上
 * (方案 §3 规则 2「订阅在前、启动在后」)。runId 由渲染层 mint 正是为了让
 * 这两件事落在同一个同步块里,中间插不进任何一个事件。
 *
 * 没有控制端订阅也是正常运行方式，不能以是否安装推送端口决定能否执行。
 * 连接与显示故障由控制台自行报告；服务端仍保留转录和可恢复的运行快照。
 */
export function startAgentRun(
  req: RunRequest,
  driver: RunDriver = runAgent,
  beforeLaunch?: () => void
): AgentRunStart {
  beforeStart?.()
  if (req.inputGoalId !== undefined && getActiveGoal(req.sessionId)?.id !== req.inputGoalId) {
    throw new IpcError('unknown', 'The goal was cleared or replaced before this input was sent.')
  }
  /*
    ★ 渲染层以为这条会话空着,主进程这边却已经有一轮在跑 —— 排队的消息刚被续上,
    或者另一个窗口先发了。用户按的是回车,要的是「这句话会被发出去」,
    所以把它排进队列,而不是报一个「会话忙」让他重打一遍。
    只对用户自己的输入这样做;内部输入(汇报、目标检查)各有自己的投递路径。
  */
  if (req.inputInternal !== true && req.inputGoalId === undefined && req.parentRunId === undefined
    && runs.activeTopLevelRun(req.sessionId) !== undefined) {
    return {
      started: false,
      queue: sessionRuntime.queue(req.sessionId, {
        kind: 'enqueue',
        text: req.input.flatMap((part) => part.type === 'text' ? [part.text] : []).join('\n\n'),
        options: sendOptionsOf(req),
        attachments: partsToAttachments(req.input)
      })
    }
  }
  runs.assertCanCreate(req)
  detachImportedSession(req)
  beforeLaunch?.()
  launch(req, driver)
  return { started: true }
}

/**
 * 主进程自己发起的顶层 run(续跑排队消息、后台汇报、目标检查)。
 *
 * ★ **不订阅任何窗口。** 在看这条会话的窗口会从 `agent:activeRuns` 广播得知它,
 * 再经 `agent:attach` 补齐;没人在看就一条正文都不推。闸门与 `startAgentRun` 完全一致。
 */
export function launchDetachedRun(req: RunRequest, driver: RunDriver = runAgent): void {
  beforeStart?.()
  if (req.inputGoalId !== undefined && getActiveGoal(req.sessionId)?.id !== req.inputGoalId) {
    throw new IpcError('unknown', 'The goal was cleared or replaced before this input was sent.')
  }
  runs.assertCanCreate(req)
  detachImportedSession(req)
  launch(req, driver)
}

function sendOptionsOf(req: RunRequest): SendOptions {
  const {
    runId: _runId, sessionId: _sessionId, input: _input, inputMessageId: _inputMessageId,
    inputInternal: _inputInternal, inputGoalId: _inputGoalId, ...options
  } = req
  return options
}

/**
 * 「下一轮什么时候开跑」的唯一决定者。见 `main/session-runtime.ts` 文件头。
 */
export const sessionRuntime = new SessionRuntime({
  now: () => Date.now(),
  newId: () => ulid(),
  readInput: (sessionId) => {
    const key = sessionInputKey(sessionId)
    const raw = store.getKv<unknown>(key, null)
    if (raw === null) return null
    // ★ 读到失效存档时顺手删键 —— 这就是「30 天兜底清扫」的全部实现
    if (!isValidSessionInput(raw, Date.now())) {
      store.setKv(key, null)
      return null
    }
    return raw
  },
  writeInput: (sessionId, state) => store.setKv(sessionInputKey(sessionId), state),
  /** 已落盘的用户消息消费回执 —— 清队列失败或压缩历史之后不能再把这些输入重发一遍 */
  committedInputIds: (sessionId, ids) => store.committedInputIds(sessionId, ids),
  activeRun: (sessionId) => runs.activeTopLevelRun(sessionId),
  isBusy: (sessionId) => runs.isSessionBusy(sessionId),
  launch: (req) => launchDetachedRun(req),
  emitQueue: (snapshot) => notifyDelivery(() => delivery.emitGlobal('session:queueChanged', snapshot)),
  emitReport: (sessionId, callId, status) => notifyDelivery(() => delivery.emitGlobal('session:subagentReport', { sessionId, callId, status })),
  /**
   * 草稿 / 队列落盘成没成。★ 走 `notifyDelivery`:广播失败不该把一次**已经写成功**的
   * 落盘报成失败,更不该反过来掩盖写盘本身的异常(那两个方向都由 runtime 那边定)。
   */
  onPersistenceChange: (change) => notifyDelivery(() => delivery.emitGlobal('session:inputPersistence', change)),
  readReport: (sessionId, callId) => readBackgroundReport(sessionId, callId),
  persistReportStatus: (sessionId, callId, status) => persistSubagentReportStatus(sessionId, callId, status),
  log: (message, error) => console.warn(message, ...(error === undefined ? [] : [error]))
})

export function queueSessionInput(req: { sessionId: string; op: SessionQueueOp }): SessionQueueResult {
  return sessionRuntime.queue(req.sessionId, req.op)
}

export function reportBackgroundChild(req: { sessionId: string; callId: string; options?: SendOptions }): { status: SubagentReportStatus } {
  return { status: sessionRuntime.reportManually(req.sessionId, req.callId, req.options) }
}

/**
 * 让一次窗口重放拿到「先 flush、再订阅、后快照」的顺序。
 *
 * ★ 三步的顺序是有讲究的(见旧 `attachRun` 的注释):
 * 1. **先 flush** —— 泵里攒着的事件已经在日志里了。不先冲掉,它们会既出现在
 *    快照里、又在稍后推给这个刚订阅的窗口,变成重复。
 * 2. 再订阅(窗口那一侧的事,不在本文件);
 * 3. 最后取快照。2、3 之间同样插不进事件。
 */
export function flushRun(runId: string): void {
  pumps.get(runId)?.flush()
}

/**
 * 重放收尾:目标是顶层 run 时苏醒目标运行时,再 flush 掉泵里攒着的事件。
 *
 * ★ 只碰「运行侧」这两件事(目标运行时、泵)。订阅与快照留在窗口那一侧
 * (`ipc/agent.ts` 的 `attachRun`),窗口授权一步都不放宽。
 */
export function prepareRunAttach(handle: RunHandle): void {
  if (handle.depth === 0 && handle.parentRunId === undefined) {
    ensureGoalRuntime()
    restoreGoal(handle.sessionId)
  }
  flushRun(handle.runId)
}

/**
 * ★★ 一经续聊,这条会话**永久脱离**导入来源的同步。
 *
 * 位置很关键:在 `launch` 之前、在任何 await 之前,而且只对**顶层** run 生效
 * (子代理 run 有自己的 sessionId,它不是用户在续聊)。
 *
 * 为什么必须这么早:同步器可能正好扫完、正要提交。晚一步标记,那次提交就会
 * 用源侧的旧转录 `replaceHistory` 掉用户刚发出去的话 —— 而用户看到的是
 * 自己的消息凭空消失。放在这里,`startAgentRun` 的校验一旦通过(不会再 throw),
 * 脱离标记就已经落库了。
 *
 * ★ 校验失败时**不**脱离:上面那两行 throw 出去的 run 从来没有开始过,
 * 把它算成一次续聊会让一条还没被碰过的会话失去同步。
 */
function detachImportedSession(req: RunRequest): void {
  if (req.parentSessionId !== undefined) return // 子代理不是用户续聊
  if (req.sessionId === '') return
  try {
    if (store.detachImportedSession(req.sessionId) > 0) {
      notifyDelivery(() => delivery.emitGlobal('imports:changed', { sourceId: '', seq: Date.now() }))
    }
  } catch {
    // 脱离标记失败不该阻止用户发消息。最坏的结果是下一轮同步报一次冲突,
    // 而冲突的处置是「保留本地」—— 方向仍然是安全的。
  }
}

/**
 * 建 handle、建泵、开跑。**不订阅** —— 订阅是窗口的事,而子 run 没有自己的窗口。
 *
 * 拆出来的理由就是这一句:`startAgentRun` 和 `startChildRun` 的差别**只有订阅从哪儿来**,
 * 其余三步必须一模一样。写成两份的话,哪天有人只在 `startAgentRun` 里加了一行,
 * 子 run 那条路径就少了那一行,而症状会出现在完全不相干的地方。
 */
function launch(req: RunRequest, driver: RunDriver): RunHandle {
  const handle = runs.create(req)
  const release = runs.retainSessionForRun(handle)
  pumps.set(req.runId, new RunPump(handle))
  // 在 driver 发出第一个事件之前登记,插话回执(`message_commit`)一条都不漏
  sessionRuntime.runStarted(handle, req)
  /*
    ★ 续跑的入口是**释放互斥之后**,不是 `run_end`:后者比收尾落盘早,
    在那之后立刻起下一轮只会撞上 `SessionBusyError`。
  */
  let settled = false
  let abandonTimer: ReturnType<typeof setTimeout> | undefined
  const settle = (): void => {
    if (abandonTimer !== undefined) clearTimeout(abandonTimer)
    handle.signal.removeEventListener('abort', watchAbort)
    // ★ 只收一次:被强行收口过的 run,驱动日后真醒过来也不能再放一次别人的锁、再续一次队列
    if (settled) return
    settled = true
    release()
    try { sessionRuntime.runSettled(handle) } catch (error) { console.warn(`[session] settle failed: ${handle.runId}`, error) }
    reapFinishedRuns()
  }
  const failed = (error: unknown): void => {
    if (handle.signal.aborted) handle.finish('aborted')
    else handle.finish('error', toAgentError(error))
  }
  /*
    ★ 停止的兜底(#187)。正常路径上 abort 之后驱动几毫秒内就会自己 `finish('aborted')`
    并收尾;但只要驱动里有**任何一处**不认 signal 的 await 卡住,run 就永远停在
    `running` —— 界面计时一直走、停止按钮按了没反应、会话互斥也一直攥着,排队的话
    永远发不出去。宽限期一过还没收到 `run_end`,就由这里替它收口:发 run_end、放锁。
    驱动那边置 `abandoned`,醒过来之后的迟到写入由 runtime 丢弃。
  */
  function watchAbort(): void {
    abandonTimer = setTimeout(() => {
      abandonTimer = undefined
      if (settled || handle.status !== 'running') return
      console.warn(`[run] ${handle.runId} 停止后 ${String(ABORT_SETTLE_GRACE_MS / 1000)} 秒仍未收尾,强制结束`)
      handle.abandoned = true
      handle.finish('aborted')
      settle()
    }, ABORT_SETTLE_GRACE_MS)
    abandonTimer.unref?.()
  }
  if (handle.signal.aborted) watchAbort()
  else handle.signal.addEventListener('abort', watchAbort, { once: true })
  ensureReapTimer()
  try { void Promise.resolve(driver(handle, req)).catch(failed).finally(settle) }
  catch (error) { try { failed(error) } finally { settle() } }
  return handle
}

/**
 * 按了停止之后,给驱动自己收尾的时间。正常收尾是毫秒级的(工具、上游、等待都挂着
 * 同一个 signal);超过它就说明某处的 await 不认 signal,由 `launch` 强行收口。
 */
export const ABORT_SETTLE_GRACE_MS = 5_000

/** 定期回收的间隔。TTL 是 2 分钟,半分钟扫一次足够,且扫描本身只是遍历一张小表 */
const REAP_INTERVAL_MS = 30_000
let reapTimer: ReturnType<typeof setInterval> | null = null

/**
 * 回收已结束的 run(条件见 `RunRegistry.reap`)。还有挂在它名下、等人点的交互
 * (例如会话级的目标提议,run 结束后仍然有效)时钉住不收。
 *
 * 回收之后再看这条会话:活跃索引里早就没有它,渲染层按库里的终态显示;晚到的 attach
 * 拿到「run 不存在」,渲染层据此退回库里的历史(见 `stores/session.ts` 的 `resync`)。
 */
export function reapFinishedRuns(now = Date.now()): number {
  const pinned = new Set(interactions.list().map((interaction) => interaction.runId))
  return runs.reap({
    now,
    ttlMs: FINISHED_RUN_TTL_MS,
    budgetBytes: FINISHED_RUN_CACHE_BYTES,
    pinned: (h) => pinned.has(h.runId),
    onReap: (h) => {
      pumps.delete(h.runId)
      notifyDelivery(() => delivery.dropTopic(runTopic(h.runId)))
    }
  })
}

function ensureReapTimer(): void {
  if (reapTimer !== null) return
  reapTimer = setInterval(() => { reapFinishedRuns() }, REAP_INTERVAL_MS)
  // 不能拖住进程退出(测试进程、app 退出)
  reapTimer.unref?.()
}

/**
 * 派一个子 run。
 *
 * ★ **订阅继承自父 run**(`beforeLaunch` 由桌面那一侧传进来做这一步),
 * 这是整条子代理链路上最容易漏、也最难查的一步:不做的话一切正常、没有报错、
 * 只是界面上什么都不发生 —— 因为 `RunPump.flush()` 在没有订阅者时会
 * **整批丢弃**事件(见 `flush` 里那段注释)。单测里只断言「子 run 的 status 是 done」
 * 的话,这一步完全没接也是绿的。
 *
 * ★ 顺序:先继承订阅,**再** launch。反过来的话,子 run 头几个事件
 * (它一定会先发一个 `context_usage`)会落进一个还没有订阅者的泵里,被整批丢掉。
 * 这和 `startAgentRun` 里「订阅在前、启动在后」是同一条规矩。
 */
export function startChildRun(
  parent: RunHandle,
  req: RunRequest,
  driver: RunDriver = runAgent,
  beforeLaunch?: (parent: RunHandle) => void
): RunHandle {
  runs.assertCanCreate(req)
  beforeLaunch?.(parent)
  return launch(req, driver)
}

/**
 * 一个 run 连同它派出的全部后代 runId(深度优先)。窗口授权、订阅继承、
 * attach 的补齐都要按这份集合来 —— 子 run 的主题各自独立(见 `runTopic`)。
 */
export function descendantRunIds(handle: RunHandle): string[] {
  const result: string[] = []
  for (const id of handle.children) {
    result.push(id)
    const child = runs.get(id)
    if (child !== undefined) result.push(...descendantRunIds(child))
  }
  return result
}

/** 一次 run 请求 → 下一次可以复用的档位。窗口那一侧起 run 时用它拼排队回执。 */
export { sendOptionsOf }

/** 只读的 handle 取用口 —— 窗口那一侧的授权与 attach 都要按 runId 找 handle */
export function runHandle(runId: string): RunHandle | undefined {
  return runs.get(runId)
}

/** 只读的快照取用口,配合 `prepareRunAttach` 的三步顺序使用 */
export function runSnapshot(handle: RunHandle, sinceSeq: number): RunSnapshot {
  return handle.snapshot(sinceSeq)
}

/** 用户按停止:顶层 run 还要先把目标挂起、撤掉它名下的目标提议 */
export function abortRun(runId: string, cascade: boolean): void {
  const handle = runs.get(runId)
  if (handle !== undefined && handle.depth === 0) {
    pauseGoal(handle.sessionId)
    interactions.cancelGoalProposals(handle.sessionId)
  }
  runs.abort(runId, cascade, { by: 'user' })
}

/** app 退出前:停掉所有 run,冲掉所有泵。留着的 setTimeout 会拖住退出。 */
export function shutdownRuns(): void {
  runs.abortAll({ by: 'shutdown' })
  /*
    ★ 后台 shell 跟着一起收。它们**不属于任何一个 run**(这正是后台的意义),
    所以 `abortAll` 一个也带不走 —— 而 `detached` 的进程组不随主进程消失:
    不杀的话,用户重启应用后端口还占着,界面上再也找不到是谁占的。
  */
  agentShells.shutdown()
  for (const pump of pumps.values()) pump.flush()
  pumps.clear()
  // 还挂在防抖里的草稿要写掉,不是丢掉
  sessionRuntime.flush()
  if (reapTimer !== null) clearInterval(reapTimer)
  reapTimer = null
}
