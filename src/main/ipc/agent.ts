/**
 * Agent IPC —— RunRegistry 与渲染层之间的那一层。
 *
 * 这个文件存在的唯一理由是**合批**(方案 §8):一个 token 一条 IPC 消息,
 * 每条都要付一次结构化克隆 + 一次主线程跳转。快速流式下这是肉眼可见的卡顿,
 * 而且卡的是主进程 —— 连带整个应用一起卡。
 *
 * ★ 合批本身、以及 run 的生命周期与会话续跑已经抽到 `main/services/agent.ts`
 * (它能在纯 Node 里 import 并运行)。这里剩下的两件事才是**彻头彻尾的 electron**:
 *
 *   1. **投递** —— 往哪个 webContents 推、推送前要不要先订阅;
 *   2. **授权** —— 谁能看/答一个 run 的交互、谁能停它(下面的 handler)。
 *
 * 方向是单向的:本文件向服务**安装**一个投递端口,服务永远不 import 本文件。
 * 「没有窗口时事件不推」是端口的两端都成立的性质,不是本文件的新逻辑。
 */
import type { WebContents } from 'electron'
import type { RunSnapshot } from '../../shared/agent/event'
import type { RunRequest } from '../../shared/agent/run-request'
import type { InteractionResponse, PendingInteraction } from '../../shared/agent/interaction'
import type { InterjectItem } from '../../shared/agent/interject'
import type { PermissionMode } from '../../shared/agent/permission'
import { interactions } from '../kernel/interaction-gate'
import { agentShells } from '../agent-shells'
import { IpcError } from './errors'
import { runs, SessionBusyError, type RunHandle } from '../kernel/run-registry'
import { detachForegroundSubagent, runAgent } from '../runtime'
import { store } from '../state/store'
import { windows, type WindowContext } from '../window/registry'
import { updateService } from '../update/update-service'
import {
  abortRun as abortRunInService,
  descendantRunIds,
  installAgentDelivery,
  installAgentStartGuard,
  prepareRunAttach,
  runHandle,
  runSnapshot,
  runTopic,
  startAgentRun,
  startChildRun as startChildRunInService,
  type AgentDeliveryPort,
  type AgentRunStart,
  type GlobalAgentEventMap,
  type RunDriver
} from '../services/agent'

// ═══════════════════════════════════════════════════════════════
// 投递端口
// ═══════════════════════════════════════════════════════════════

/**
 * 服务的事件频道 → `WindowRegistry` 的那几个入口。★ 端口上这五个频道
 * (`agent:activeRuns` / `session:queueChanged` / `session:subagentReport` /
 * `session:inputPersistence` / `imports:changed`)在 `shared/ipc/contract.ts` 里
 * 都是 GlobalEventChannel,所以一律走 `emitToAll`;分组推送只留给
 * `agent:event`(它按 run 主题定向)。
 */
const agentDelivery: AgentDeliveryPort = {
  hasSubscribers: (topic) => windows.hasSubscribers(topic),
  emitToTopic: (topic, channel, payload) => windows.emitToTopic(topic, channel, payload),
  /**
   * ★ 五个分支各写一遍,是为了让载荷钉在**字面量频道**上 —— `emitToAll` 的签名
   * 是「按频道取载荷」,少一个分支编译期就报出来。写成一个循环 + 一次宽泛断言
   * 会让「端口漏转发某个频道」变成一件运行时才发现的事(表现是那个角标永远不收敛)。
   */
  emitGlobal: (channel, payload) => {
    switch (channel) {
      case 'agent:activeRuns': return windows.emitToAll('agent:activeRuns', payload as GlobalAgentEventMap['agent:activeRuns'])
      case 'session:queueChanged': return windows.emitToAll('session:queueChanged', payload as GlobalAgentEventMap['session:queueChanged'])
      case 'session:subagentReport': return windows.emitToAll('session:subagentReport', payload as GlobalAgentEventMap['session:subagentReport'])
      case 'session:inputPersistence': return windows.emitToAll('session:inputPersistence', payload as GlobalAgentEventMap['session:inputPersistence'])
      case 'imports:changed': return windows.emitToAll('imports:changed', payload as GlobalAgentEventMap['imports:changed'])
    }
  },
  dropTopic: (topic) => windows.dropTopic(topic)
}

/**
 * 装一次,服务那边的事件就落在窗口上。
 *
 * ★ `beforeStart` 是桌面独有的闸门:必须装完客户端更新才能起新 run
 * (`update-service` 拖着 electron 的 `app`,所以它只能住在这一侧)。
 * 服务那边在每次启动前调它,抛错即拒绝。
 */
installAgentDelivery(agentDelivery)
installAgentStartGuard(() => {
  updateService.configure()
  if (!updateService.canStartNewRuns()) throw new IpcError('unknown', '必须安装客户端更新后才能开始新的任务')
})

// ═══════════════════════════════════════════════════════════════
// 启动 / 续跑(窗口那一步接在这里)
// ═══════════════════════════════════════════════════════════════

/**
 * ★ 订阅在前、启动在后(方案 §3 规则 2)。
 *
 * runId 由渲染层 mint 正是为了这个顺序成立:两件事都在这个同步块里做完,
 * 中间插不进任何一个事件(定时器和 IO 回调没法跟同步代码交错)。
 * 如果 runId 是 agent:run 的返回值,渲染层就只能在 await 之后才知道要订阅谁,
 * 而那时首批事件早发完了。
 *
 * 校验、忙则入队、脱离导入、launch 全在服务里,顺序与旧实现逐字一致;
 * 这里只把「这个窗口订阅这个 run」作为 `beforeLaunch` 交进去。
 */
export function startRun(req: RunRequest, ctx: WindowContext, driver: RunDriver = runAgent): AgentRunStart {
  return startAgentRun(req, driver, () => { windows.subscribe(runTopic(req.runId), ctx.sender) })
}

/**
 * HTTP 网关起的顶层 run。没有 webContents,但与桌面走同一个启动闸门和事件泵。
 *
 * ★ 与 `startRun` 的区别:会话忙时**不入队**,而是直接以 `conflict` 拒绝(网关映射成 409)。
 * 外部调用方拿的是 runId 去订阅事件,一句被悄悄排进队列的话它永远等不到对应的 run。
 * 忙判断与 `startAgentRun` 在同一个同步块里,中间插不进别的启动。
 */
export function startExternalRun(req: RunRequest, driver: RunDriver = runAgent): RunHandle {
  if (runs.activeTopLevelRun(req.sessionId) !== undefined) {
    throw new IpcError('conflict', 'Session already has an active run', 409)
  }
  let result: AgentRunStart
  try {
    result = startAgentRun(req, driver)
  } catch (err) {
    if (err instanceof SessionBusyError) throw new IpcError('conflict', err.message, 409)
    throw err
  }
  const handle = result.started ? runs.get(req.runId) : undefined
  if (handle === undefined) throw new IpcError('conflict', 'Session already has an active run', 409)
  return handle
}

/**
 * 主进程自己发起的顶层 run(续跑排队消息、后台汇报、目标检查)。
 *
 * ★ **不订阅任何窗口** —— 所以这里没有 `beforeLaunch`,也不需要 `ctx`。
 * 在看这条会话的窗口会从 `agent:activeRuns` 广播得知它,再经 `agent:attach` 补齐。
 *
 * ★ 实现**就是**服务那一个(重导出,不包一层):调用点有三条来源
 * (`session-runtime` 的续跑、目标检查、后台汇报),包一层只会多一个
 * 「同名不同物」的入口,而它一文不值。
 */

/**
 * 派一个子 run。★ **订阅继承自父 run** —— 不做的话一切正常、没有报错、
 * 只是界面上什么都不发生(子 run 的主题没有订阅者,泵整批丢弃)。
 *
 * ★ 顺序:先继承订阅,**再** launch。反过来的话,子 run 头几个事件
 * (它一定会先发一个 `context_usage`)会落进一个还没有订阅者的泵里,被整批丢掉。
 * 这和 `startRun` 里「订阅在前、启动在后」是同一条规矩。
 */
export function startChildRun(parent: RunHandle, req: RunRequest, driver: RunDriver = runAgent): RunHandle {
  return startChildRunInService(parent, req, driver, () => {
    windows.inherit(runTopic(parent.runId), runTopic(req.runId))
  })
}

// ═══════════════════════════════════════════════════════════════
// 重放与订阅
// ═══════════════════════════════════════════════════════════════

/**
 * 重放。⌘R 重载窗口、或第二个窗口来看同一个 run,都走这里。
 *
 * ★ 三步的顺序是有讲究的:
 * 1. **先 flush**(服务里做)—— 泵里攒着的事件已经在日志里了。不先冲掉,
 *    它们会既出现在快照里、又在稍后推给这个刚订阅的窗口,变成重复。
 * 2. 再订阅;
 * 3. 最后取快照。2、3 之间同样插不进事件。
 */
export function attachRun(req: { runId: string; sinceSeq: number }, ctx: WindowContext): RunSnapshot {
  const handle = runHandle(req.runId)
  // 已被 reap 掉、或渲染层记着一个上辈子的 runId。让它拿到明确的错误,
  // 而不是一个空快照 —— 空快照会被当成「run 存在但没事件」,UI 就永远转圈了。
  if (!handle) throw new IpcError('unknown', `run 不存在: ${req.runId}`)

  prepareRunAttach(handle)
  windows.subscribe(runTopic(req.runId), ctx.sender)
  for (const childId of descendantRunIds(handle)) windows.subscribe(runTopic(childId), ctx.sender)
  return runSnapshot(handle, req.sinceSeq)
}

/**
 * 这个窗口不再看这个 run 的正文。`attachRun` 的反向。
 *
 * ★ 只摘**调用方自己**的订阅,所以不需要额外授权:它不停 run、不碰别的窗口,
 * 最坏结果是调用方自己少收几条。run 已经被回收时照样摘 —— 主题可能还挂着。
 */
export function unwatchRun(req: { runId: string }, ctx: WindowContext): void {
  windows.unsubscribe(runTopic(req.runId), ctx.sender)
  const handle = runHandle(req.runId)
  if (handle === undefined) return
  for (const childId of descendantRunIds(handle)) windows.unsubscribe(runTopic(childId), ctx.sender)
}

/**
 * 窗口最小化 / 隐藏(`false`)或者露出来(`true`)。由 `main/index.ts` 的窗口生命周期调用。
 *
 * ★ 藏起来时先摘掉这个窗口的全部 run 正文订阅,再通知它 —— 顺序反过来的话,
 * 通知到达之前还会有一批正文推过去,落进一个已经不打算显示它们的渲染层。
 * 露出来时只通知:由渲染层按快照 + 历史重建它真正在看的那几个会话。
 */
export function setWindowContentVisible(sender: WebContents, visible: boolean): void {
  if (sender.isDestroyed()) return
  if (!visible) windows.unsubscribeRuns(sender)
  windows.emitTo(sender, 'window:visibility', { visible })
}

// ═══════════════════════════════════════════════════════════════
// 交互与 run 上的窗口授权
// ═══════════════════════════════════════════════════════════════

export function listInteractions(req: { runId?: string; sessionId?: string }, ctx: WindowContext): PendingInteraction[] {
  const handle = req.runId === undefined ? undefined : runHandle(req.runId)
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
  abortRunInService(req.runId, req.cascade)
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
  const handle = runHandle(req.runId)
  if (handle === undefined || handle.status !== 'running') return
  if (!windows.isSubscribed(runTopic(req.runId), ctx.sender)) {
    throw new IpcError('unknown', '当前窗口没有订阅这个运行')
  }
  handle.setInterject(req.items)
}

/**
 * 权限档位药丸切换时调用,让**这个正在跑的 run**接下来的工具审批立刻改用新档位
 * ——契约注释里那句「不必等到下一条新消息」就是靠这条频道兑现的。
 *
 * ★ run 不在、或已经跑完时静默返回,不抛错:同一类竞态见 `interjectRun` 的注释
 * (用户点下拉的瞬间 run 正好收尾是正常情况,不算失败)。
 *
 * ★ 订阅校验与 `interjectRun` 同源:runId 是渲染层 mint 的,光有 id 不构成授权。
 */
export function setRunPermissionMode(
  req: { runId: string; mode: PermissionMode },
  ctx: WindowContext
): void {
  const handle = runHandle(req.runId)
  if (handle === undefined || handle.status !== 'running') return
  if (!windows.isSubscribed(runTopic(req.runId), ctx.sender)) {
    throw new IpcError('unknown', '当前窗口没有订阅这个运行')
  }
  handle.setPermissionMode(req.mode)
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

/**
 * 把**一次工具调用**里那条正在跑的前台命令转去后台 —— 工具卡片上那颗「转后台」按钮。
 *
 * ★ 和 `stopToolCall` 同一套订阅校验,同一个「false 不是错误」的契约:
 * false = 它已经跑完了、这个环境转不了(SSH),或后台 shell 名额已满。
 */
export function backgroundToolCall(req: { runId: string; callId: string }, ctx: WindowContext): boolean {
  if (!windows.isSubscribed(runTopic(req.runId), ctx.sender)) {
    throw new IpcError('unknown', '当前窗口没有订阅这个运行')
  }
  return agentShells.detachCall(req.runId, req.callId)
}

/**
 * 把一个**前台**子代理转去后台 —— 子代理卡片上那颗「转后台」按钮。父代理不再等它,
 * 它跑完之后结论按后台子代理那条路送回主代理。
 *
 * ★ 订阅校验认子 run 自己的主题,也认父 run 的:子主题是从父主题继承来的
 * (`startChildRun`),而重载之后窗口重新挂上的是父 run。
 */
export function backgroundSubagent(req: { childRunId: string }, ctx: WindowContext): boolean {
  const parentRunId = runHandle(req.childRunId)?.parentRunId
  if (!windows.isSubscribed(runTopic(req.childRunId), ctx.sender)
    && (parentRunId === undefined || !windows.isSubscribed(runTopic(parentRunId), ctx.sender))) {
    throw new IpcError('unknown', '当前窗口没有订阅这个运行')
  }
  return detachForegroundSubagent(req.childRunId)
}

// ═══════════════════════════════════════════════════════════════
// 兼容重导出
// ═══════════════════════════════════════════════════════════════

/**
 * 这些原先就住在本文里,现在搬去服务 —— 但**入口名字一个都不变**:外部
 * (`ipc/index.ts`、`ipc/app.ts`、`ipc/storage.ts`、各测试)照旧从
 * `ipc/agent` import。搬家是内部实现细节,不该让调用点跟着改。
 */
export { launchDetachedRun } from '../services/agent'

export { activeRunIndex, broadcastActiveRuns, queueSessionInput, reapFinishedRuns, reportBackgroundChild, sessionRuntime, shutdownRuns } from '../services/agent'
export type { AgentRunStart, RunDriver } from '../services/agent'
