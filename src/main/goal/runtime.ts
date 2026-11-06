/** Session goals: Stop-hook decisions, transcript markers and deferred check-ins. */
import type { AgentError } from '../../shared/agent/error'
import { agentError } from '../../shared/agent/error'
import type { AgentMessage, ContentPart } from '../../shared/agent/message'
import type { SendOptions } from '../../shared/agent/run-request'
import type { ActiveGoal, GoalChange, GoalClearReason, GoalOrigin } from '../../shared/domain/goal'
import { GOAL_CONDITION_MAX, normalizeGoalCondition } from '../../shared/domain/goal'
import type { HookRunReport } from '../../shared/domain/hook'
import { ulid } from '../../shared/util/id'
import type { SessionDeps, TurnEndInput, TurnEndResult } from '../kernel/agent-session'
import type { ModelProposedGoals } from '../../shared/domain/settings'
import type { RunHandle } from '../kernel/run-registry'
import type { InteractionGate } from '../kernel/interaction-gate'
import type { WorkspaceEnvironment } from '../environment/contract'
import { runHookEvent } from '../hooks'
import { clearRuntimeHooks, runtimeHooksFor } from '../hook-registry'
import {
  clearActiveGoal, getActiveGoal, goalHookId, goalRevision, goalSignal, goalWasInitialized, listActiveGoals,
  markGoalInitialized, mountGoalHook, onGoalChanged, setActiveGoal, unmountGoalHook, updateActiveGoal
} from './state'
import {
  STOP_HOOK_FEEDBACK_PREFIX, goalCheckinMessage, goalContinuationMessage, goalKickoffMessage,
  type BackgroundTaskLine
} from './prompt'
import { mergeGoalStatusMessage, restorableGoalCondition } from './restore'

export const GOAL_IDLE_STREAK_CAP = 8
export const GOAL_IDLE_CHECKIN_CAP = 3
export const GOAL_CHECKIN_MIN_DELAY_MS = 60_000
const DEFAULT_CHECKIN_MS = 30 * 60_000

type GoalStatus = Extract<ContentPart, { type: 'goal_status' }>

export interface GoalHost {
  now(): number
  history(sessionId: string): readonly AgentMessage[]
  /**
   * 目标重载恢复要的那一小段:只需含**最后一条带 `goal_status` 的助手消息**
   * (`restorableGoalCondition` 只看它)。缺省时退回 `history`。
   *
   * ★ 需求:打开长会话时 `restoreGoal` 走 `history` 会把整段转录全部解析一遍,
   *   主进程同步卡住,首页历史迟迟回不来。
   */
  goalHistory?(sessionId: string): readonly AgentMessage[]
  commit(sessionId: string, message: AgentMessage): void
  exists(sessionId: string): boolean
  tokens(sessionId: string): number
  log(line: string): void
}

let host: GoalHost | undefined
let publish: ((change: GoalChange, sourceRunId?: string) => boolean) | undefined
/**
 * 空闲检查由谁来真的开跑。装上之后(`ipc/index.ts` 接 `main/session-runtime.ts`)
 * 检查在主进程直接起一轮,不再转交给「订阅着那个 run 的窗口」—— 原先没有窗口在看
 * 这条会话时,检查就投递失败,只能一直退避重试。
 */
let launcher: ((sessionId: string, parts: ContentPart[], options: SendOptions, goalId: string) => boolean) | undefined
const pendingStatuses = new Map<string, GoalStatus[]>()
/** Only out-of-band markers, never a second copy of the whole conversation. */
const durableStatuses = new Map<string, Map<string, GoalStatus[]>>()
const checkinFailures = new Map<string, { attempts: number; retryAt: number }>()
const contexts = new Map<string, TurnEndContext>()
const timers = new Map<string, ReturnType<typeof setTimeout>>()

export function installGoalHost(next: GoalHost): void { host = next }
export function setGoalChangeListener(listener: NonNullable<typeof publish>): void { publish = listener }
export function setGoalLauncher(next: typeof launcher): void { launcher = next }

const now = (): number => host?.now() ?? Date.now()
function log(event: string, fields: Record<string, string | number>): void {
  host?.log(`[goal] ${event} ${JSON.stringify(fields)}`)
}

onGoalChanged((sessionId, goal) => {
  if (goal === undefined) {
    clearGoalTimer(sessionId)
    checkinFailures.delete(sessionId)
    contexts.delete(sessionId)
  }
  publish?.({ sessionId, ...(goal === undefined ? {} : { goal }) })
})

/** Called at the persistence boundary; never changes the model's context projection. */
export function prepareGoalMessage(sessionId: string, message: AgentMessage): AgentMessage {
  if (message.role !== 'assistant') return message
  const statuses = durableStatuses.get(sessionId)?.get(message.id)
  const pending = pendingStatuses.get(sessionId) ?? []
  if (statuses === undefined && pending.length === 0) return message
  pendingStatuses.delete(sessionId)
  return mergeGoalStatusMessage({ ...message, parts: [...message.parts, ...pending] },
    statuses === undefined ? undefined : { ...message, parts: statuses })
}

function recordStatus(sessionId: string, part: GoalStatus): void {
  const status: GoalStatus = { ...part, id: part.id ?? ulid(now()), createdAt: part.createdAt ?? now() }
  const last = host?.history(sessionId).findLast((message) => message.role === 'assistant')
  // A user clear may happen while tool results are last; still attach only to an assistant.
  if (last?.role !== 'assistant') {
    pendingStatuses.set(sessionId, [...(pendingStatuses.get(sessionId) ?? []), status])
    return
  }
  const message = prepareGoalMessage(sessionId, { ...last, parts: [...last.parts, status] })
  host?.commit(sessionId, message)
  const byMessage = durableStatuses.get(sessionId) ?? new Map<string, GoalStatus[]>()
  byMessage.set(message.id, message.parts.filter((part): part is GoalStatus => part.type === 'goal_status'))
  durableStatuses.set(sessionId, byMessage)
  publish?.({ sessionId, goal: getActiveGoal(sessionId), message })
}

/**
 * Idempotent restoration, with no kickoff, run launch, or idle timer.
 *
 * ★ 历史**只在第一次**才读。原先写成默认参数 `messages = host.history(...)`,而默认参数
 *   在函数体之前求值 —— 已初始化过的会话每次 `sessions:getPage` / `goal:get` 也照样把
 *   整段转录读出来解析一遍再丢掉,长会话每打开、每往上翻一页都要付这笔钱。
 */
export function restoreGoal(sessionId: string, messages?: readonly AgentMessage[]): ActiveGoal | undefined {
  if (goalWasInitialized(sessionId)) return getActiveGoal(sessionId)
  markGoalInitialized(sessionId)
  const history = messages ?? host?.goalHistory?.(sessionId) ?? host?.history(sessionId) ?? []
  const condition = restorableGoalCondition(history)
  if (condition !== null) activateGoal({ sessionId, condition, origin: 'restored', now: now() })
  return getActiveGoal(sessionId)
}

export function activateGoal(input: {
  sessionId: string
  condition: string
  origin: GoalOrigin
  now: number
  tokensAtStart?: number
  log?: (line: string) => void
}): ContentPart[] | undefined {
  const condition = normalizeGoalCondition(input.condition)
  if (condition === '' || condition.length > GOAL_CONDITION_MAX) return undefined
  const previous = getActiveGoal(input.sessionId)
  if (previous !== undefined) log('goal_cleared', {
    reason: 'superseded', iterations: previous.iterations, origin: previous.origin,
    durationMs: Math.max(0, input.now - previous.setAt)
  })
  // Keep the current run's coordination port when replacing its goal.
  const context = contexts.get(input.sessionId)
  const goal: ActiveGoal = {
    id: ulid(input.now), condition, origin: input.origin, iterations: 0, setAt: input.now,
    tokensAtStart: input.tokensAtStart ?? host?.tokens(input.sessionId) ?? 0,
    tokens: 0, checkinCount: 0, idleCheckinCount: 0
  }
  setActiveGoal(input.sessionId, goal)
  if (context !== undefined) contexts.set(input.sessionId, context)
  log('goal_set', { promptLength: condition.length, origin: input.origin, via: input.origin })
  if (input.origin === 'restored') return undefined
  recordStatus(input.sessionId, { type: 'goal_status', met: false, set: true, condition, origin: input.origin, iterations: 0 })
  return [{ type: 'text', text: goalKickoffMessage(condition) }]
}

export function deactivateGoal(sessionId: string, reason: GoalClearReason, _log?: (line: string) => void): GoalStatus | undefined {
  const goal = clearActiveGoal(sessionId, reason)
  if (goal === undefined) return undefined
  const durationMs = Math.max(0, now() - goal.setAt)
  log('goal_cleared', { reason, iterations: goal.iterations, durationMs, origin: goal.origin })
  const part: GoalStatus = {
    type: 'goal_status', met: false, cleared: true, condition: goal.condition,
    iterations: goal.iterations, durationMs,
    tokens: reason === 'user_clear' ? goalTokens(sessionId, goal) : goal.tokens ?? 0
  }
  // Shutdown is not a user clear: leave the durable active marker for next launch.
  if (reason === 'user_clear') recordStatus(sessionId, part)
  return part
}

function goalTokens(sessionId: string, goal: ActiveGoal): number {
  return Math.max(0, (host?.tokens(sessionId) ?? goal.tokensAtStart) - goal.tokensAtStart)
}

export interface TurnEndContext {
  environment: WorkspaceEnvironment
  workspaceId: string
  runId?: string
  model: string
  modelProviderId?: string
  /** Frozen together at run start; only the built-in goal hook uses this pair. */
  evaluatorModel?: string
  evaluatorModelProviderId?: string
  log: (line: string) => void
  backgroundWork?: () => readonly BackgroundTaskLine[]
  isIdle?: () => boolean
  /** Returns false if the session cannot accept an internal message right now. */
  inject?: (parts: ContentPart[], goalId: string) => boolean
  options?: SendOptions
  checkinIntervalMs?: number
  runHooks?: typeof runHookEvent
}

export function goalSourceRunId(sessionId: string): string | undefined { return contexts.get(sessionId)?.runId }

export function bindGoalRun(sessionId: string, context: TurnEndContext, userMessage: boolean): void {
  checkinFailures.delete(sessionId)
  contexts.set(sessionId, context)
  clearGoalTimer(sessionId)
  const goal = getActiveGoal(sessionId)
  if (goal === undefined) return
  if (userMessage) updateActiveGoal(sessionId, { idleCheckinCount: 0 })
  mountGoalHook(sessionId)
}

/** A manual abort/error pauses wake-ups but retains the condition for the next user turn. */
export function pauseGoal(sessionId: string): void {
  clearGoalTimer(sessionId)
  checkinFailures.delete(sessionId)
  contexts.delete(sessionId)
  // Invalidate a check-in already in IPC or an interjection mailbox without clearing the condition.
  if (getActiveGoal(sessionId) !== undefined) updateActiveGoal(sessionId, { id: ulid(now()) })
  unmountGoalHook(sessionId)
}

export function releaseGoalRun(sessionId: string): void {
  if (getActiveGoal(sessionId)?.deferredSince === undefined) {
    clearGoalTimer(sessionId)
    contexts.delete(sessionId)
    unmountGoalHook(sessionId)
  }
}

export function endGoalSession(sessionId: string): void {
  deactivateGoal(sessionId, 'session_ended')
  pauseGoal(sessionId)
  pendingStatuses.delete(sessionId)
  durableStatuses.delete(sessionId)
  clearRuntimeHooks(sessionId)
}

export function stopAllGoals(): void {
  for (const sessionId of new Set([...listActiveGoals().keys(), ...contexts.keys(), ...timers.keys()])) endGoalSession(sessionId)
  pendingStatuses.clear()
  durableStatuses.clear()
  checkinFailures.clear()
}

export function resetGoalRuntimeForTest(): void {
  stopAllGoals()
  host = undefined
  publish = undefined
  launcher = undefined
}

export function goalCheckinInterval(count: number, baseMs = DEFAULT_CHECKIN_MS): number {
  const base = Number.isFinite(baseMs) && baseMs > 0 ? baseMs : DEFAULT_CHECKIN_MS
  return Math.max(GOAL_CHECKIN_MIN_DELAY_MS, base * 2 ** Math.min(Math.max(0, count), 2))
}

export function configuredGoalCheckinMs(value = process.env.CLAUDE_CODE_GOAL_CHECKIN_MINUTES): number {
  const minutes = Number(value)
  return Number.isFinite(minutes) && minutes > 0 ? Math.max(GOAL_CHECKIN_MIN_DELAY_MS, minutes * 60_000) : DEFAULT_CHECKIN_MS
}

function clearGoalTimer(sessionId: string): void {
  const timer = timers.get(sessionId)
  if (timer !== undefined) clearTimeout(timer)
  timers.delete(sessionId)
}

function checkinDue(goal: ActiveGoal, context: TurnEndContext): number {
  return (goal.lastDeferralPassAt ?? goal.deferredSince ?? now())
    + goalCheckinInterval(goal.checkinCount, context.checkinIntervalMs ?? configuredGoalCheckinMs())
}

function armCheckin(sessionId: string): void {
  clearGoalTimer(sessionId)
  const goal = getActiveGoal(sessionId)
  const context = contexts.get(sessionId)
  if (goal?.deferredSince === undefined || context === undefined || context.inject === undefined) return
  const capped = (goal.idleCheckinCount ?? 0) >= GOAL_IDLE_CHECKIN_CAP
  const delay = capped
    ? goalCheckinInterval(goal.checkinCount, context.checkinIntervalMs ?? configuredGoalCheckinMs())
    : Math.max(GOAL_CHECKIN_MIN_DELAY_MS,
      (checkinFailures.get(sessionId)?.retryAt ?? checkinDue(goal, context)) - now())
  const timer = setTimeout(() => {
    timers.delete(sessionId)
    const current = getActiveGoal(sessionId)
    if (current?.id !== goal.id || current.deferredSince === undefined) return
    if (host !== undefined && !host.exists(sessionId)) { endGoalSession(sessionId); return }
    try {
      if (context.isIdle?.() === true && (current.idleCheckinCount ?? 0) < GOAL_IDLE_CHECKIN_CAP) {
        const parts = checkinParts(current, context, true)
        if (context.inject?.(parts, current.id) === true) {
          checkinFailures.delete(sessionId)
          advanceCheckin(sessionId, current, 'idle_timer')
        } else deferCheckinDelivery(sessionId, current, context)
      }
    } catch (error) {
      context.log(`[goal] check-in failed: ${error instanceof Error ? error.message : String(error)}`)
      deferCheckinDelivery(sessionId, current, context)
    }
    armCheckin(sessionId)
  }, delay)
  timer.unref?.()
  timers.set(sessionId, timer)
}

function deferCheckinDelivery(sessionId: string, goal: ActiveGoal, context: TurnEndContext): void {
  if (getActiveGoal(sessionId)?.id !== goal.id) return
  const attempts = (checkinFailures.get(sessionId)?.attempts ?? 0) + 1
  checkinFailures.set(sessionId, {
    attempts, retryAt: now() + goalCheckinInterval(goal.checkinCount + attempts,
      context.checkinIntervalMs ?? configuredGoalCheckinMs())
  })
  log('goal_checkin_delivery_failed', { attempts, deferredMs: now() - (goal.deferredSince ?? now()) })
}

function checkinParts(goal: ActiveGoal, context: TurnEndContext, idle: boolean): ContentPart[] {
  return [{ type: 'text', text: goalCheckinMessage({
    condition: goal.condition, deferredMinutes: (now() - (goal.deferredSince ?? now())) / 60_000,
    tasks: context.backgroundWork?.() ?? [],
    final: idle && (goal.idleCheckinCount ?? 0) + 1 >= GOAL_IDLE_CHECKIN_CAP
  }) }]
}

function advanceCheckin(sessionId: string, goal: ActiveGoal, trigger: 'idle_timer' | 'turn_end'): void {
  if (getActiveGoal(sessionId)?.id !== goal.id) return
  const idleCheckinCount = (goal.idleCheckinCount ?? 0) + (trigger === 'idle_timer' ? 1 : 0)
  updateActiveGoal(sessionId, { checkinCount: goal.checkinCount + 1, lastDeferralPassAt: now(), idleCheckinCount })
  log('goal_checkin_injected', {
    trigger, deferredMs: now() - (goal.deferredSince ?? now()), checkinCount: goal.checkinCount + 1, idleCheckinCount
  })
}

/** Deliver to an idle session: started in-process when a launcher is installed, otherwise via the owning renderer. */
export function wakeGoal(sessionId: string, parts: ContentPart[], goalId: string): boolean {
  if (getActiveGoal(sessionId)?.id !== goalId) return false
  const context = contexts.get(sessionId)
  if (context?.options === undefined) return false
  if (launcher !== undefined) {
    const delivered = launcher(sessionId, parts, context.options, goalId)
    // 状态照常广播给所有窗口;检查本身不再经过任何窗口
    if (delivered) publish?.({ sessionId, goal: getActiveGoal(sessionId) })
    return delivered
  }
  return publish?.({ sessionId, goal: getActiveGoal(sessionId), input: { goalId, parts, options: context.options } }, context.runId) ?? false
}

export async function handleTurnEnd(input: TurnEndInput, ctx: TurnEndContext): Promise<TurnEndResult | undefined> {
  if (input.isSubagent || input.signal.aborted) return undefined
  const goalBefore = getActiveGoal(input.sessionId)
  const background = ctx.backgroundWork?.() ?? []
  let deferred = false
  if (goalBefore !== undefined && background.length > 0) {
    deferred = true
    unmountGoalHook(input.sessionId)
    updateActiveGoal(input.sessionId, {
      deferredSince: goalBefore.deferredSince ?? now(), tokens: goalTokens(input.sessionId, goalBefore)
    })
    if (!contexts.has(input.sessionId)) contexts.set(input.sessionId, ctx)
    armCheckin(input.sessionId)
    log('goal_evaluated', { outcome: 'deferred', iterations: goalBefore.iterations, origin: goalBefore.origin, activeAgents: background.length, durationMs: 0 })
  } else if (goalBefore !== undefined) {
    if (goalBefore.deferredSince !== undefined) updateActiveGoal(input.sessionId, {
      deferredSince: undefined, checkinCount: 0, lastDeferralPassAt: undefined
    })
    clearGoalTimer(input.sessionId)
    mountGoalHook(input.sessionId)
  }

  const goalId = goalHookId(input.sessionId)
  const signal = goalSignal(input.sessionId)
  const started = now()
  const reports = await (ctx.runHooks ?? runHookEvent)({
    event: 'Stop', environment: ctx.environment, sessionId: input.sessionId, runId: input.runId,
    workspaceId: ctx.workspaceId, messages: input.messages,
    fallbackModel: ctx.model, fallbackModelProviderId: ctx.modelProviderId,
    extra: { status: 'done', stop_hook_active: input.stopHookActive ?? input.stoppedTurnStreak > 0 },
    signal: input.signal,
    literalPrompts: [goalId],
    hookSignals: signal === undefined ? undefined : { [goalId]: AbortSignal.any([signal, input.signal]) },
    runtimeHooks: runtimeHooksFor(input.sessionId).map((hook) => hook.id === goalId && hook.type === 'prompt'
      ? { ...hook, model: ctx.evaluatorModel ?? '', modelProviderId: ctx.evaluatorModelProviderId }
      : hook)
  })
  if (input.signal.aborted) return undefined
  const goal = getActiveGoal(input.sessionId)
  // A verdict belongs to the identity evaluated, not to a replacement with the same text.
  const unchanged = goal !== undefined && goal.id === goalBefore?.id
  const report = unchanged && !deferred ? goalReportOf(input.sessionId, reports) : undefined
  const verdict = report?.promptVerdict
  const reason = report?.reason ?? ''
  const otherBlock = reports.find((r) => r.hookId !== goalId && (r.outcome === 'blocked' || r.decision === 'deny'))
  let status: GoalStatus | undefined

  if (unchanged && goal !== undefined && verdict !== undefined && verdict !== 'skipped') {
    const iterations = goal.iterations + 1
    const tokens = goalTokens(input.sessionId, goal)
    status = { type: 'goal_status', id: ulid(now()), createdAt: now(), met: verdict === 'met',
      ...(verdict === 'impossible' ? { failed: true } : {}), condition: goal.condition,
      reason, iterations, durationMs: Math.max(0, now() - goal.setAt), tokens }
    updateActiveGoal(input.sessionId, { iterations, lastReason: reason, tokens })
    log('goal_evaluated', { outcome: verdict, durationMs: now() - started, iterations, origin: goal.origin, activeAgents: 0 })
    if (verdict === 'met' || verdict === 'impossible') {
      deactivateGoal(input.sessionId, verdict)
      return { kind: 'finish', goalStatus: status, note: `goal ${verdict}` }
    }
  } else if (unchanged && verdict === 'skipped') {
    log('goal_evaluated', { outcome: 'error', durationMs: now() - started, iterations: goal.iterations, origin: goal.origin, activeAgents: 0 })
  }

  let inject: ContentPart[] | undefined
  if (otherBlock !== undefined) inject = [{ type: 'text', text: `${STOP_HOOK_FEEDBACK_PREFIX}\n${otherBlock.reason || 'A Stop hook blocked this run from finishing.'}` }]
  else if (status !== undefined && goal !== undefined) inject = [{ type: 'text', text: goalContinuationMessage(goal.condition, reason || 'insufficient evidence in transcript') }]
  else if (deferred && unchanged && goal !== undefined && now() >= checkinDue(goal, ctx)) {
    inject = checkinParts(goal, ctx, false)
    advanceCheckin(input.sessionId, goal, 'turn_end')
    armCheckin(input.sessionId)
  }

  if (inject === undefined) return deferred ? { kind: 'finish', acceptPendingInput: true, note: 'goal deferred' } : undefined
  if (input.stoppedTurnStreak >= GOAL_IDLE_STREAK_CAP) {
    log('goal_idle_streak_stopped', { streak: input.stoppedTurnStreak, iterations: goal?.iterations ?? 0 })
    return { kind: 'finish', acceptPendingInput: true, goalStatus: status,
      warning: idleStreakWarning(input.stoppedTurnStreak, goal !== undefined), note: 'idle streak cap reached' }
  }
  return { kind: 'continue', inject, goalStatus: status }
}

function idleStreakWarning(streak: number, hasGoal: boolean): AgentError {
  return agentError('unknown', 'Stopped after repeated tool-free turns.', {
    messageKey: hasGoal ? 'goal.warn.idleStreak' : 'hooks.warn.idleStreak', messageParams: { streak }, retryable: false
  })
}

export function goalReportOf(sessionId: string, reports: readonly HookRunReport[]): HookRunReport | undefined {
  return reports.find((report) => report.hookId === goalHookId(sessionId))
}

/** The tool awaits dispatch only. A human decision never becomes a tool-result message. */
export function goalProposalsFor(input: {
  handle: RunHandle
  context: TurnEndContext
  gate: InteractionGate
  interactive: boolean
  planning: () => boolean
  setting: () => ModelProposedGoals
}): Pick<SessionDeps, 'canProposeGoal' | 'proposeGoal'> {
  const { handle, context, gate } = input
  const sessionId = handle.sessionId
  const canProposeGoal = (): boolean => handle.depth === 0 && input.interactive
    && !handle.signal.aborted && handle.status === 'running' && !input.planning()
    && input.setting() !== 'disabled' && !gate.hasGoalProposal(sessionId)
  const set = (condition: string, origin: GoalOrigin): void => {
    const kickoff = activateGoal({ sessionId, condition, origin, now: now() })
    const goal = getActiveGoal(sessionId)
    if (goal === undefined || kickoff === undefined) return
    bindGoalRun(sessionId, context, false)
    context.inject?.(kickoff, goal.id)
  }
  return {
    canProposeGoal,
    proposeGoal: (condition, askUser) => {
      if (!canProposeGoal()) return Promise.reject(new Error('Goal proposals are unavailable in this session.'))
      const ask = askUser || input.setting() !== 'auto'
      log('goal_proposed', { promptLength: condition.length, askUser: String(ask) })
      if (!ask) {
        set(condition, 'proposal_direct')
        return Promise.resolve('set')
      }
      const revision = goalRevision(sessionId)
      void gate.request(handle, { kind: 'goal_proposal', sessionId, condition }, now(), {
        onChange: () => { publish?.({ sessionId, goal: getActiveGoal(sessionId) }) }
      }).then((response) => {
        if (response.kind !== 'goal_proposal') return
        log('goal_proposal_decided', { promptLength: condition.length, askUser: 'true', decision: response.approved ? 'approved' : 'declined' })
        if (!response.approved || handle.signal.aborted || input.planning()
          || input.setting() === 'disabled' || goalRevision(sessionId) !== revision
          || (host !== undefined && !host.exists(sessionId))) return
        set(condition, 'proposal_approved')
      }).catch(() => {
        log('goal_proposal_decided', { promptLength: condition.length, askUser: 'true', decision: 'cancelled' })
      })
      return Promise.resolve('pending')
    }
  }
}
