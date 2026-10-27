/**
 * Agent IPC —— RunRegistry 与渲染层之间的那一层。
 *
 * 这个文件存在的唯一理由是**合批**(方案 §8):一个 token 一条 IPC 消息,
 * 每条都要付一次结构化克隆 + 一次主线程跳转。快速流式下这是肉眼可见的卡顿,
 * 而且卡的是主进程 —— 连带整个应用一起卡。
 *
 * 合批逻辑刻意**不**放进 RunRegistry:内核零 electron import,
 * 而「往哪个 webContents 推」是彻头彻尾的 electron 概念。
 */
import type { AgentEvent, ActiveRunEntry, RunSnapshot } from '../../shared/agent/event'
import type { RunRequest } from '../../shared/agent/run-request'
import type { InteractionResponse, PendingInteraction } from '../../shared/agent/interaction'
import type { InterjectItem } from '../../shared/agent/interject'
import { interactions } from '../kernel/interaction-gate'
import { agentShells } from '../agent-shells'
import { IpcError, toAgentError } from './errors'
import { RunHandle, runs } from '../kernel/run-registry'
import { ensureGoalRuntime, runAgent } from '../runtime'
import { pauseGoal, restoreGoal } from '../goal/runtime'
import { getActiveGoal } from '../goal/state'
import { store } from '../state/store'
import { runTopic, windows, type WindowContext } from '../window/registry'
import { updateService } from '../update/update-service'

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
    if (!windows.hasSubscribers(this.topic)) return

    windows.emitToTopic(this.topic, 'agent:event', {
      runId: this.handle.runId,
      seq: this.lastSeq,
      events
    })
  }
}

const pumps = new Map<string, RunPump>()

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
    return run === undefined || run.parentRunId !== undefined ? [] : [{
      runId: run.runId, sessionId: run.sessionId, workspaceId: run.workspaceId, status: run.status
    }]
  })
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
  windows.emitToAll('agent:activeRuns', { runs: activeRunIndex() })
}

// ═══════════════════════════════════════════════════════════════
// handlers
// ═══════════════════════════════════════════════════════════════

/**
 * 驱动一个 run 的东西。默认是真的 `AgentSession`(经 `runtime.runAgent`)。
 *
 * ★ 这个接缝之所以存在,是因为**假发射器与真 session 对 `RunHandle` 的用法完全一致** ——
 * 那正是「第 3 步就能看到端到端流式」成立的原因,现在它反过来还有用:
 * `agent-pump.test.ts` 传一个确定性的假发射器进来,把**泵**单独拎出来测。
 * 拿真 session 测合批窗口是碰运气 —— 它的事件时序取决于上游怎么切片。
 *
 * 做成参数而不是一个可写的模块变量:后者要在每个测试后复位,
 * 而忘了复位的那个测试会莫名其妙地跑起真上游来。
 */
export type RunDriver = (handle: RunHandle, req: RunRequest) => void | Promise<void>

/**
 * ★ 订阅在前、启动在后(方案 §3 规则 2)。
 *
 * runId 由渲染层 mint 正是为了这个顺序成立:两件事都在这个同步块里做完,
 * 中间插不进任何一个事件(定时器和 IO 回调没法跟同步代码交错)。
 * 如果 runId 是 agent:run 的返回值,渲染层就只能在 await 之后才知道要订阅谁,
 * 而那时首批事件早发完了。
 */
export function startRun(req: RunRequest, ctx: WindowContext, driver: RunDriver = runAgent): void {
  prepareRun(req)
  windows.subscribe(runTopic(req.runId), ctx.sender)
  launch(req, driver)
}

/** 需求：HTTP 没有 webContents，但必须与桌面经过同一启动闸门和事件泵。 */
export function startExternalRun(req: RunRequest, driver: RunDriver = runAgent): RunHandle {
  prepareRun(req)
  return launch(req, driver)
}

function prepareRun(req: RunRequest): void {
  updateService.configure()
  if (!updateService.canStartNewRuns()) throw new IpcError('unknown', '必须安装客户端更新后才能开始新的任务')
  if (req.inputGoalId !== undefined && getActiveGoal(req.sessionId)?.id !== req.inputGoalId) {
    throw new IpcError('unknown', 'The goal was cleared or replaced before this input was sent.')
  }
  // 需求：HTTP 与窗口可以同时发送，渲染层的单会话守卫已不足以防止转录交错。
  // 校验放在导入脱离之前，拒绝的请求不能改变导入会话的同步状态。
  if (runs.runningIn(req.workspaceId).some((handle) => handle.sessionId === req.sessionId
    && handle.depth === 0 && handle.parentRunId === undefined)) {
    throw new IpcError('conflict', 'Session already has an active run', 409)
  }
  detachImportedSession(req)
}

/**
 * ★★ 一经续聊,这条会话**永久脱离**导入来源的同步。
 *
 * 位置很关键:在 `launch` 之前、在任何 await 之前,而且只对**顶层** run 生效
 * (子代理 run 有自己的 sessionId,它不是用户在续聊)。
 *
 * 为什么必须这么早:同步器可能正好扫完、正要提交。晚一步标记,那次提交就会
 * 用源侧的旧转录 `replaceHistory` 掉用户刚发出去的话 —— 而用户看到的是
 * 自己的消息凭空消失。放在这里,`startRun` 的校验一旦通过(不会再 throw),
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
      windows.emitToAll('imports:changed', { sourceId: '', seq: Date.now() })
    }
  } catch {
    // 脱离标记失败不该阻止用户发消息。最坏的结果是下一轮同步报一次冲突,
    // 而冲突的处置是「保留本地」—— 方向仍然是安全的。
  }
}

/**
 * 建 handle、建泵、开跑。**不订阅** —— 订阅是窗口的事,而子 run 没有自己的窗口。
 *
 * 拆出来的理由就是这一句:`startRun` 和 `startChildRun` 的差别**只有订阅从哪儿来**,
 * 其余三步必须一模一样。写成两份的话,哪天有人只在 `startRun` 里加了一行,
 * 子 run 那条路径就少了那一行,而症状会出现在完全不相干的地方。
 */
function launch(req: RunRequest, driver: RunDriver): RunHandle {
  const handle = runs.create(req)
  pumps.set(req.runId, new RunPump(handle))
  const failed = (error: unknown): void => {
    if (handle.signal.aborted) handle.finish('aborted')
    else handle.finish('error', toAgentError(error))
  }
  try { void Promise.resolve(driver(handle, req)).catch(failed) }
  catch (error) { failed(error) }
  return handle
}

/**
 * 派一个子 run。
 *
 * ★ **订阅继承自父 run**,这是整条子代理链路上最容易漏、也最难查的一步:
 * 不做的话一切正常、没有报错、只是界面上什么都不发生 —— 因为
 * `RunPump.flush()` 在没有订阅者时会**整批丢弃**事件(见 `flush` 里那段注释)。
 * 单测里只断言「子 run 的 status 是 done」的话,这一步完全没接也是绿的,
 * 所以 `subagent-wiring.test.ts` 断言的是父窗口真的收到了带子 runId 的信封。
 *
 * ★ 顺序:先继承订阅,**再** launch。反过来的话,子 run 头几个事件
 * (它一定会先发一个 `context_usage`)会落进一个还没有订阅者的泵里,被整批丢掉。
 * 这和 `startRun` 里「订阅在前、启动在后」是同一条规矩。
 */
export function startChildRun(
  parent: RunHandle,
  req: RunRequest,
  driver: RunDriver = runAgent
): RunHandle {
  windows.inherit(runTopic(parent.runId), runTopic(req.runId))
  return launch(req, driver)
}

/**
 * 重放。⌘R 重载窗口、或第二个窗口来看同一个 run,都走这里。
 *
 * ★ 三步的顺序是有讲究的:
 * 1. **先 flush** —— 泵里攒着的事件已经在日志里了。不先冲掉,
 *    它们会既出现在快照里、又在稍后推给这个刚订阅的窗口,变成重复。
 * 2. 再订阅;
 * 3. 最后取快照。2、3 之间同样插不进事件。
 */
export function attachRun(req: { runId: string; sinceSeq: number }, ctx: WindowContext): RunSnapshot {
  const handle = runs.get(req.runId)
  // 已被 reap 掉、或渲染层记着一个上辈子的 runId。让它拿到明确的错误,
  // 而不是一个空快照 —— 空快照会被当成「run 存在但没事件」,UI 就永远转圈了。
  if (!handle) throw new IpcError('unknown', `run 不存在: ${req.runId}`)

  if (handle.depth === 0 && handle.parentRunId === undefined) {
    ensureGoalRuntime()
    restoreGoal(handle.sessionId)
  }
  pumps.get(req.runId)?.flush()
  windows.subscribe(runTopic(req.runId), ctx.sender)
  for (const childId of descendantRunIds(handle)) windows.subscribe(runTopic(childId), ctx.sender)
  return handle.snapshot(req.sinceSeq)
}

function descendantRunIds(handle: RunHandle): string[] {
  const result: string[] = []
  for (const id of handle.children) {
    result.push(id)
    const child = runs.get(id)
    if (child !== undefined) result.push(...descendantRunIds(child))
  }
  return result
}

export function listInteractions(req: { runId?: string; sessionId?: string }, ctx: WindowContext): PendingInteraction[] {
  const handle = req.runId === undefined ? undefined : runs.get(req.runId)
  const allowed = handle === undefined ? undefined : new Set([handle.runId, ...descendantRunIds(handle)])
  const session = req.sessionId === undefined ? undefined : store.getSession(req.sessionId)
  return interactions.list().filter((interaction) => {
    if (interaction.kind === 'goal_proposal' && session !== undefined
      && session.parentSessionId === undefined && interaction.sessionId === session.id) {
      // Session-scoped proposals survive run completion and renderer reload.
      windows.subscribe(runTopic(interaction.runId), ctx.sender)
      return true
    }
    if (req.sessionId !== undefined && req.runId === undefined) return false
    return windows.isSubscribed(runTopic(interaction.runId), ctx.sender)
      && (req.runId === undefined || allowed?.has(interaction.runId) === true)
  })
}

export function respondInteraction(response: InteractionResponse, ctx: WindowContext): void {
  const pending = interactions.get(response?.id)
  if (pending === undefined || !windows.isSubscribed(runTopic(pending.runId), ctx.sender)) {
    throw new IpcError('unknown', 'Interaction is no longer pending in this window')
  }
  interactions.respond(response)
}

export function abortRun(req: { runId: string; cascade: boolean }): void {
  const handle = runs.get(req.runId)
  if (handle !== undefined && handle.depth === 0) {
    pauseGoal(handle.sessionId)
    interactions.cancelGoalProposals(handle.sessionId)
  }
  runs.abort(req.runId, req.cascade, { by: 'user' })
}

/**
 * 插话入队。
 *
 * ★ **run 不在、或已经跑完时静默返回**,不抛错。渲染层是在用户点「插话」的
 * 瞬间发这条消息的,而那一刻 run 正好收尾是一个完全正常的竞态 ——
 * 报错会在界面上弹出一个「插话失败」,可实际上什么都没坏:条目还在队列里,
 * run 结束后的 `drainQueue` 会照常把它发出去。
 *
 * ★ 订阅校验与 `respondInteraction` 同源:没订阅这个 run 的窗口不能往它的
 * 转录里塞消息。这条 run 的 id 是渲染层 mint 的,光有 id 不构成授权。
 */
export function interjectRun(
  req: { runId: string; items: InterjectItem[] },
  ctx: WindowContext
): void {
  const handle = runs.get(req.runId)
  if (handle === undefined || handle.status !== 'running') return
  if (!windows.isSubscribed(runTopic(req.runId), ctx.sender)) {
    throw new IpcError('unknown', '当前窗口没有订阅这个运行')
  }
  handle.setInterject(req.items)
}

/**
 * 停掉**一次工具调用**里那条正在跑的命令 —— 工具卡片上那颗停止按钮。
 *
 * ★ 和 `abortRun` 是两件事:这条不碰 run,只让 `Bash` 那次调用提前收场并返回
 * 一条「用户停了这条命令」的工具失败。模型据此换路,回复继续写。
 *
 * ★ 订阅校验与 `interjectRun` 同源:runId 是渲染层 mint 的,光有 id 不构成授权,
 * 否则任何一个窗口都能掐掉别的窗口正在跑的命令。
 *
 * 返回 `false`(命令已经不在跑了)**不是错误**,见频道契约。
 */
export function stopToolCall(req: { runId: string; callId: string }, ctx: WindowContext): boolean {
  if (!windows.isSubscribed(runTopic(req.runId), ctx.sender)) {
    throw new IpcError('unknown', '当前窗口没有订阅这个运行')
  }
  return agentShells.stopCall(req.runId, req.callId)
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
}
