/**
 * 会话目标的运行时 —— 状态、转录标记、回合末判定接线与空闲唤醒。
 *
 * ★ 两处 IO 全部注入:转录走假的 `GoalHost`(Map + 一条提交记录),
 *   判定走假的 `runHooks`。这里没有一次真网络请求,也没有一个真子进程 ——
 *   时间全部走 `vi.useFakeTimers()`,没有一处真的等 30 分钟。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, userMessage, type AgentMessage, type ContentPart } from '../../shared/agent/message'
import type { RunRequest } from '../../shared/agent/run-request'
import type { HookDefinition, HookRunReport } from '../../shared/domain/hook'
import type { ModelProposedGoals } from '../../shared/domain/settings'
import type { HookEventContext } from '../hooks'
import { registerRuntimeHook, runtimeHooksFor } from '../hook-registry'
import type { TurnEndInput } from '../kernel/agent-session'
import { InteractionGate } from '../kernel/interaction-gate'
import { RunHandle } from '../kernel/run-registry'
import type { WorkspaceEnvironment } from '../environment/contract'
import { STOP_HOOK_FEEDBACK_PREFIX, goalContinuationMessage, goalKickoffMessage } from '../goal/prompt'
import { mergeGoalStatusHistory } from '../goal/restore'
import { getActiveGoal, goalHookId, goalSignal, resetGoalsForTest } from '../goal/state'
import {
  GOAL_CHECKIN_MIN_DELAY_MS, GOAL_IDLE_CHECKIN_CAP, GOAL_IDLE_STREAK_CAP, activateGoal, bindGoalRun,
  deactivateGoal, goalCheckinInterval, goalProposalsFor, handleTurnEnd, installGoalHost, pauseGoal,
  prepareGoalMessage, resetGoalRuntimeForTest, restoreGoal, type GoalHost, type TurnEndContext
} from '../goal/runtime'

const S = 'sess-1'
const WS = 'ws-1'
const CONDITION = '让 bun test 全绿（转录里有这次运行的结果）'
const HALF_HOUR = 30 * 60_000

type GoalStatusPart = Extract<ContentPart, { type: 'goal_status' }>

/** 注入的宿主。转录是一张 Map,「提交」是就地替换同 id 的那一条消息。 */
interface Host {
  histories: Map<string, AgentMessage[]>
  commits: Array<{ sessionId: string; message: AgentMessage }>
  logs: string[]
}

function installFakeHost(): Host {
  const rig: Host = { histories: new Map(), commits: [], logs: [] }
  const host: GoalHost = {
    now: () => Date.now(),
    history: (sessionId) => rig.histories.get(sessionId) ?? [],
    commit: (sessionId, message) => {
      rig.commits.push({ sessionId, message })
      const history = rig.histories.get(sessionId) ?? []
      const index = history.findIndex((item) => item.id === message.id)
      if (index >= 0) history[index] = message
      else history.push(message)
      rig.histories.set(sessionId, history)
    },
    exists: (sessionId) => rig.histories.has(sessionId),
    tokens: () => 0,
    log: (line) => rig.logs.push(line)
  }
  installGoalHost(host)
  return rig
}

let rig: Host

beforeEach(() => {
  rig = installFakeHost()
})

afterEach(() => {
  // 清理必须发生在假时钟还装着的时候:定时器表里存的是假句柄。
  resetGoalRuntimeForTest()
  resetGoalsForTest()
  vi.useRealTimers()
})

function statusParts(message: AgentMessage | undefined): GoalStatusPart[] {
  return (message?.parts ?? []).filter((part): part is GoalStatusPart => part.type === 'goal_status')
}

function marker(over: Partial<GoalStatusPart> = {}): GoalStatusPart {
  return { type: 'goal_status', met: false, condition: CONDITION, reason: '还红着', ...over }
}

/** 一条带正文的助手消息,末尾可选地挂一个目标标记。 */
function assistantWith(id: string, status?: GoalStatusPart): AgentMessage {
  return assistantMessage(id, status === undefined
    ? [{ type: 'text', text: '好' }]
    : [{ type: 'text', text: '好' }, status], 2)
}

function seed(sessionId: string, ...messages: AgentMessage[]): void {
  rig.histories.set(sessionId, [...messages])
}

const textOf = (parts: readonly ContentPart[]): string =>
  parts.map((part) => (part.type === 'text' ? part.text : '')).join('\n')

function turnEnd(over: Partial<TurnEndInput> = {}): TurnEndInput {
  return {
    sessionId: S,
    workspaceId: WS,
    runId: 'run-1',
    messages: [],
    isSubagent: false,
    toolCallsThisRun: 0,
    stoppedTurnStreak: 0,
    // 每次都是新的:一条用例里的中断不该漏到下一条
    signal: new AbortController().signal,
    ...over
  }
}

function makeContext(over: Partial<TurnEndContext> = {}): TurnEndContext {
  return {
    environment: {} as unknown as WorkspaceEnvironment,
    workspaceId: WS,
    runId: 'run-1',
    model: 'main-model',
    log: () => {},
    ...over
  }
}

/** 回合末判定的桩。`calls` 里是每一次真的递出去的那份输入。 */
function stubHooks(handler: (params: HookEventContext, call: number) => readonly HookRunReport[] = () => []): {
  runHooks: NonNullable<TurnEndContext['runHooks']>
  calls: HookEventContext[]
} {
  const calls: HookEventContext[] = []
  return {
    calls,
    runHooks: (params) => {
      calls.push(params)
      return Promise.resolve([...handler(params, calls.length)])
    }
  }
}

const goalReport = (over: Partial<HookRunReport> = {}): HookRunReport => ({
  hookId: goalHookId(S),
  scope: 'project',
  exitCode: 0,
  stdout: '',
  stderr: '',
  durationMs: 1,
  outcome: 'blocked',
  decision: 'deny',
  ...over
})

/** 只推微任务:提案的批准回调全在微任务队列里,不需要任何定时器。 */
async function flushMicrotasks(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve()
}

async function untilHolds(predicate: () => boolean): Promise<void> {
  for (let i = 0; i < 50 && !predicate(); i++) await Promise.resolve()
  expect(predicate()).toBe(true)
}

describe('目标标记 · 落在最后一条助手消息上', () => {
  it('每次设定都换一个新身份 —— 条件一字不改也算换了一个目标', () => {
    const kickoff = activateGoal({ sessionId: S, condition: `  ${CONDITION}  `, origin: 'user', now: 1_000 })
    const first = getActiveGoal(S)
    expect(first).toMatchObject({ condition: CONDITION, origin: 'user', iterations: 0, setAt: 1_000 })
    expect(textOf(kickoff ?? [])).toBe(goalKickoffMessage(CONDITION))

    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 2_000 })
    const second = getActiveGoal(S)
    expect(second?.id).not.toBe(first?.id)
    expect(second?.iterations).toBe(0)
  })

  it('设定与清除的标记追加到最后一条助手消息上,不新增消息', () => {
    seed(S, userMessage('u1', [{ type: 'text', text: '开始' }], 1), assistantWith('a1'))
    const ids = rig.histories.get(S)!.map((message) => message.id)

    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const afterSet = rig.histories.get(S)!
    expect(afterSet.map((message) => message.id)).toEqual(ids)
    const set = statusParts(afterSet[1])
    expect(set).toHaveLength(1)
    expect(set[0]).toMatchObject({ set: true, met: false, condition: CONDITION, origin: 'user', iterations: 0 })
    // 正文一个字节没动
    expect(afterSet[1]?.parts[0]).toEqual({ type: 'text', text: '好' })

    const cleared = deactivateGoal(S, 'user_clear')
    expect(cleared).toMatchObject({ met: false, cleared: true, condition: CONDITION })
    const afterClear = rig.histories.get(S)!
    expect(afterClear.map((message) => message.id)).toEqual(ids)
    expect(statusParts(afterClear[1])).toHaveLength(2)
    expect(statusParts(afterClear[1]).some((part) => part.cleared === true)).toBe(true)
    expect(afterClear[1]?.parts[0]).toEqual({ type: 'text', text: '好' })
    expect(getActiveGoal(S)).toBeUndefined()
  })

  it('转录末尾没有助手消息时先入队,下一次 prepareGoalMessage 才落上去', () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    // 一条助手消息都没有 —— 这一刻没有任何东西可以挂
    expect(rig.commits).toHaveLength(0)

    const prepared = prepareGoalMessage(S, assistantMessage('a1', [{ type: 'text', text: '我开始了' }], 5))
    expect(prepared.parts[0]).toEqual({ type: 'text', text: '我开始了' })
    expect(statusParts(prepared)).toHaveLength(1)
    expect(statusParts(prepared)[0]).toMatchObject({ set: true, met: false, condition: CONDITION })

    // ★ 队列只放一次:下一条助手消息不会再被补一遍(同一条标记补两遍 = UI 上两条「已设定」)
    expect(statusParts(prepareGoalMessage(S, assistantMessage('a2', [{ type: 'text', text: '又一条' }], 6)))).toHaveLength(0)

    // 非助手消息读都不读,原样返回
    const user = userMessage('u9', [{ type: 'text', text: '喂' }], 6)
    expect(prepareGoalMessage(S, user)).toBe(user)
  })

  it('★ 合并保留只存在于最新转录里的带外标记(用户中途清掉目标那条)', () => {
    const run = [assistantWith('a1')]
    const latest = [assistantWith('a1', marker({ cleared: true, id: 'g-clear', createdAt: 9 }))]
    const merged = mergeGoalStatusHistory(run, latest)
    expect(merged[0]?.parts).toEqual(latest[0]?.parts)
    expect(statusParts(merged[0]).map((part) => part.cleared)).toEqual([true])

    // 反过来也要保住:run 那侧有标记、latest 那侧还没有(带外清除先到、流提交后到)
    const withSet = [assistantWith('a1', marker({ set: true, id: 'g-set', createdAt: 3 }))]
    const kept = mergeGoalStatusHistory(withSet, [assistantWith('a1')])
    expect(statusParts(kept[0]).map((part) => part.id)).toEqual(['g-set'])

    // 最新转录里根本没有的那条消息:原样返回,不复制也不丢标记
    const untouched = assistantWith('a2', marker({ id: 'g-old', createdAt: 1 }))
    expect(mergeGoalStatusHistory([...run, untouched], latest)[1]).toBe(untouched)
  })
})

describe('restoreGoal · 重载恢复', () => {
  it('幂等:origin=restored、iterations=0、不 kickoff、不挂定时器', () => {
    vi.useFakeTimers()
    seed(S, userMessage('u1', [{ type: 'text', text: '开始' }], 1),
      assistantWith('a1', marker({ id: 'g1', createdAt: 3 })))

    const goal = restoreGoal(S)
    expect(goal).toMatchObject({ condition: CONDITION, origin: 'restored', iterations: 0 })
    expect(rig.commits).toHaveLength(0)
    expect(vi.getTimerCount()).toBe(0)

    const again = restoreGoal(S)
    expect(again?.id).toBe(goal?.id)
    expect(rig.commits).toHaveLength(0)
    expect(vi.getTimerCount()).toBe(0)
  })

  const terminal: Array<[string, Partial<GoalStatusPart>]> = [
    ['cleared', { cleared: true }],
    ['met', { met: true }],
    ['impossible', { failed: true }]
  ]
  it.each(terminal)('最后一条是 %s 时不恢复', (_label, over) => {
    seed(S, assistantWith('a1', marker({ ...over, id: 'g1', createdAt: 3 })))
    expect(restoreGoal(S)).toBeUndefined()
    expect(getActiveGoal(S)).toBeUndefined()
    expect(rig.commits).toHaveLength(0)
  })

  it('★ 只在第一次读库,且优先走只含最后一条标记的 goalHistory —— 打开长会话不再解析整段转录', () => {
    const history = vi.fn(() => [] as AgentMessage[])
    const goalHistory = vi.fn(() => [assistantWith('a9', marker({ id: 'g9', createdAt: 9 }))])
    installGoalHost({ now: () => Date.now(), history, goalHistory, commit: () => {}, exists: () => true, tokens: () => 0, log: () => {} })

    expect(restoreGoal(S)).toMatchObject({ condition: CONDITION, origin: 'restored' })
    restoreGoal(S)
    restoreGoal(S)
    expect(goalHistory).toHaveBeenCalledTimes(1)
    expect(history).not.toHaveBeenCalled()
  })
})

describe('handleTurnEnd · 判定接线', () => {
  it('子 run 直接跳过:一次判定都不跑,目标一动不动', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const hooks = stubHooks(() => [goalReport({ promptVerdict: 'met' })])
    const result = await handleTurnEnd(turnEnd({ isSubagent: true }), makeContext({ runHooks: hooks.runHooks }))
    expect(result).toBeUndefined()
    expect(hooks.calls).toHaveLength(0)
    expect(getActiveGoal(S)).toMatchObject({ iterations: 0 })
  })

  it('判成 not_met:轮数 +1、记住理由,并把续跑提示注入回去', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const commitsBefore = rig.commits.length
    const hooks = stubHooks(() => [goalReport({ promptVerdict: 'not_met', reason: '还有两个红的' })])

    const result = await handleTurnEnd(turnEnd(), makeContext({ runHooks: hooks.runHooks }))
    expect(result?.kind).toBe('continue')
    expect(result?.inject?.[0]).toEqual({ type: 'text', text: goalContinuationMessage(CONDITION, '还有两个红的') })
    expect(textOf(result?.inject ?? [])).toContain(STOP_HOOK_FEEDBACK_PREFIX)
    expect(result?.goalStatus).toMatchObject({ met: false, condition: CONDITION, reason: '还有两个红的', iterations: 1 })
    expect(getActiveGoal(S)).toMatchObject({ iterations: 1, lastReason: '还有两个红的' })
    // 标记由内核那条 trace 附加,运行时自己不写 —— 写两遍就是两条
    expect(rig.commits).toHaveLength(commitsBefore)
  })

  it('判成 met:收尾并清掉目标,空转 streak 到顶也拦不住', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const signal = goalSignal(S)
    const hooks = stubHooks(() => [goalReport({ outcome: 'ok', decision: 'allow', promptVerdict: 'met', reason: 'bun test 全绿了' })])

    const result = await handleTurnEnd(
      turnEnd({ stoppedTurnStreak: GOAL_IDLE_STREAK_CAP }),
      makeContext({ runHooks: hooks.runHooks })
    )
    expect(result).toMatchObject({ kind: 'finish', note: 'goal met' })
    expect(result?.goalStatus).toMatchObject({ met: true, reason: 'bun test 全绿了', iterations: 1 })
    expect(result?.warning).toBeUndefined()
    expect(getActiveGoal(S)).toBeUndefined()
    // 还没回来的那次判定跟着目标一起取消
    expect(signal?.aborted).toBe(true)
  })

  it('判成 impossible:同样收尾,但标记是 failed', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const hooks = stubHooks(() => [goalReport({ promptVerdict: 'impossible', reason: '那个包不存在' })])
    const result = await handleTurnEnd(turnEnd(), makeContext({ runHooks: hooks.runHooks }))
    expect(result).toMatchObject({ kind: 'finish', note: 'goal impossible' })
    expect(result?.goalStatus).toMatchObject({ met: false, failed: true, iterations: 1 })
    expect(getActiveGoal(S)).toBeUndefined()
  })

  it('判成 skipped(超时/出错):不算一轮,也不清目标', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const hooks = stubHooks(() => [goalReport({ outcome: 'timeout', promptVerdict: 'skipped', reason: 'timeout' })])
    const result = await handleTurnEnd(turnEnd(), makeContext({ runHooks: hooks.runHooks }))
    expect(result).toBeUndefined()
    expect(getActiveGoal(S)).toMatchObject({ iterations: 0 })
    expect(getActiveGoal(S)?.lastReason).toBeUndefined()
  })

  it('★ 空转到顶:not_met 也收尾,但目标留着,只给一条警告', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const hooks = stubHooks(() => [goalReport({ promptVerdict: 'not_met', reason: '还红着' })])
    const result = await handleTurnEnd(turnEnd({ stoppedTurnStreak: GOAL_IDLE_STREAK_CAP }), makeContext({ runHooks: hooks.runHooks }))

    expect(result?.kind).toBe('finish')
    expect(result?.inject).toBeUndefined()
    expect(result?.warning).toMatchObject({ code: 'unknown', retryable: false, messageKey: 'goal.warn.idleStreak', messageParams: { streak: GOAL_IDLE_STREAK_CAP } })
    expect(result?.goalStatus).toMatchObject({ met: false, iterations: 1 })
    // ★ 轮数不是上限:停下来的是「反复空转」,不是「跑了很久」
    expect(getActiveGoal(S)).toMatchObject({ iterations: 1 })
  })

  it('★ 判定期间目标被换掉(条件一字不改):那份裁决作废,新目标不受影响', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const stale = goalSignal(S)
    const hooks = stubHooks(() => {
      activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_500 })
      return [goalReport({ promptVerdict: 'met', reason: '旧目标达成' })]
    })

    const result = await handleTurnEnd(turnEnd(), makeContext({ runHooks: hooks.runHooks }))
    expect(result).toBeUndefined()
    expect(getActiveGoal(S)).toMatchObject({ condition: CONDITION, iterations: 0 })
    expect(getActiveGoal(S)?.lastReason).toBeUndefined()
    // 手里那一次判定必须被取消:它判的是已经不存在的那个目标
    expect(stale?.aborted).toBe(true)
    expect(hooks.calls[0]?.hookSignals?.[goalHookId(S)]?.aborted).toBe(true)
  })

  it('★ 判定期间目标被用户清掉:那份裁决同样作废', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const hooks = stubHooks(() => {
      deactivateGoal(S, 'user_clear')
      return [goalReport({ promptVerdict: 'met', reason: '清掉之前那一刻' })]
    })
    const result = await handleTurnEnd(turnEnd(), makeContext({ runHooks: hooks.runHooks }))
    expect(result).toBeUndefined()
    expect(getActiveGoal(S)).toBeUndefined()
  })

  it('★ 判定期间 input 被中断:裁决丢掉,目标原封不动', async () => {
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const controller = new AbortController()
    const hooks = stubHooks(() => {
      controller.abort()
      return [goalReport({ promptVerdict: 'not_met', reason: '还红着' })]
    })
    const result = await handleTurnEnd(turnEnd({ signal: controller.signal }), makeContext({ runHooks: hooks.runHooks }))
    expect(result).toBeUndefined()
    expect(getActiveGoal(S)).toMatchObject({ iterations: 0 })
  })

  it('★ 后台在跑:不计轮数,goal 钩子从这一轮输入里摘掉,别的 Stop 钩子照跑', async () => {
    const other: HookDefinition = {
      id: 'other-stop', type: 'command', event: 'Stop', command: 'exit 2', enabled: true, timeoutMs: 1_000
    }
    registerRuntimeHook(S, other)
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: 1_000 })
    const hooks = stubHooks(() => [{
      hookId: 'other-stop', scope: 'project', exitCode: 2, stdout: '', stderr: '', durationMs: 3,
      outcome: 'blocked', decision: 'deny', reason: 'lint 还红着'
    }])

    const result = await handleTurnEnd(turnEnd(), makeContext({
      backgroundWork: () => [{ taskId: 't1', type: 'Task', description: '后台子代理' }],
      runHooks: hooks.runHooks
    }))

    expect(result?.kind).toBe('continue')
    expect(result?.inject?.[0]).toEqual({ type: 'text', text: `${STOP_HOOK_FEEDBACK_PREFIX}\nlint 还红着` })
    expect(getActiveGoal(S)).toMatchObject({ iterations: 0 })
    expect(getActiveGoal(S)?.deferredSince).toBeTypeOf('number')
    // 推迟期间判定器不该被叫醒:它读的转录里那一轮本来就还没结束
    expect(hooks.calls[0]?.runtimeHooks?.map((hook) => hook.id)).toEqual(['other-stop'])
    expect(runtimeHooksFor(S).map((hook) => hook.id)).toEqual(['other-stop'])
  })
})

describe('空闲唤醒', () => {
  it('backs off failed deliveries without charging the three-injection allowance', async () => {
    vi.useFakeTimers()
    const context = await deferGoal([])
    const deliver = vi.fn(() => false)
    context.inject = deliver
    await vi.advanceTimersByTimeAsync(HALF_HOUR)
    expect(deliver).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(GOAL_CHECKIN_MIN_DELAY_MS)
    expect(deliver).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(60 * 60_000 - GOAL_CHECKIN_MIN_DELAY_MS)
    expect(deliver).toHaveBeenCalledTimes(2)
    expect(getActiveGoal(S)?.idleCheckinCount).toBe(0)
    expect(getActiveGoal(S)?.checkinCount).toBe(0)
    expect(rig.logs.some((line) => line.includes('goal_checkin_delivery_failed'))).toBe(true)
  })

  it('returns to evaluation when background work has finished', async () => {
    vi.useFakeTimers()
    const context = await deferGoal([])
    context.backgroundWork = () => []
    context.runHooks = stubHooks(() => [goalReport({ promptVerdict: 'not_met', reason: 'need final verification' })]).runHooks
    const result = await handleTurnEnd(turnEnd(), context)
    expect(result?.kind).toBe('continue')
    expect(getActiveGoal(S)).toMatchObject({ iterations: 1, checkinCount: 0 })
    expect(getActiveGoal(S)?.deferredSince).toBeUndefined()
    expect(vi.getTimerCount()).toBe(0)
  })
  function checkinContext(injections: Array<{ text: string; goalId: string }>): TurnEndContext {
    return makeContext({
      backgroundWork: () => [{ taskId: 't1', type: 'Task', description: '后台子代理' }],
      isIdle: () => true,
      checkinIntervalMs: HALF_HOUR,
      inject: (parts, goalId) => {
        injections.push({ text: textOf(parts), goalId })
        return true
      },
      runHooks: stubHooks().runHooks
    })
  }

  async function deferGoal(injections: Array<{ text: string; goalId: string }>): Promise<TurnEndContext> {
    seed(S, assistantWith('a1'))
    activateGoal({ sessionId: S, condition: CONDITION, origin: 'user', now: Date.now() })
    const context = checkinContext(injections)
    const result = await handleTurnEnd(turnEnd(), context)
    expect(result).toMatchObject({ kind: 'finish', note: 'goal deferred' })
    return context
  }

  it('间隔:首次 30 分钟,之后翻倍、封顶 120 分钟;配置低于下限的抬到 1 分钟', () => {
    expect(goalCheckinInterval(0, HALF_HOUR)).toBe(HALF_HOUR)
    expect(goalCheckinInterval(1, HALF_HOUR)).toBe(60 * 60_000)
    expect(goalCheckinInterval(2, HALF_HOUR)).toBe(120 * 60_000)
    expect(goalCheckinInterval(3, HALF_HOUR)).toBe(120 * 60_000)
    expect(goalCheckinInterval(0, 1_000)).toBe(GOAL_CHECKIN_MIN_DELAY_MS)
    expect(goalCheckinInterval(0)).toBe(HALF_HOUR)
  })

  it('★ 空闲注入 30 / 60 / 120 分钟,第三次带「不再自动唤醒」的尾巴,到顶后不再注入', async () => {
    vi.useFakeTimers()
    const injections: Array<{ text: string; goalId: string }> = []
    await deferGoal(injections)
    expect(vi.getTimerCount()).toBe(1)

    await vi.advanceTimersByTimeAsync(HALF_HOUR)
    expect(injections).toHaveLength(1)
    expect(injections[0]?.text).toContain(CONDITION)
    expect(injections[0]?.text).not.toContain('idle check-ins paused')

    await vi.advanceTimersByTimeAsync(60 * 60_000)
    expect(injections).toHaveLength(2)
    expect(injections[1]?.text).not.toContain('idle check-ins paused')

    await vi.advanceTimersByTimeAsync(120 * 60_000)
    expect(injections).toHaveLength(3)
    expect(injections[2]?.text).toContain('idle check-ins paused until your next message')
    expect(injections.every((item) => item.goalId === getActiveGoal(S)?.id)).toBe(true)

    // ★ 到顶之后只是挂空定时器:再等 4 小时也不会多注入一次
    await vi.advanceTimersByTimeAsync(240 * 60_000)
    expect(injections).toHaveLength(3)
    expect(getActiveGoal(S)).toMatchObject({ idleCheckinCount: GOAL_IDLE_CHECKIN_CAP, checkinCount: 3 })
  })

  it('清除目标时定时器跟着走 —— 不留下一条会唤醒已死目标的闹钟', async () => {
    vi.useFakeTimers()
    const injections: Array<{ text: string; goalId: string }> = []
    await deferGoal(injections)
    expect(vi.getTimerCount()).toBe(1)

    deactivateGoal(S, 'user_clear')
    expect(getActiveGoal(S)).toBeUndefined()
    expect(vi.getTimerCount()).toBe(0)

    await vi.advanceTimersByTimeAsync(240 * 60_000)
    expect(injections).toEqual([])
  })

  it('reset 之后一条定时器都不剩', async () => {
    vi.useFakeTimers()
    const injections: Array<{ text: string; goalId: string }> = []
    await deferGoal(injections)
    expect(vi.getTimerCount()).toBe(1)

    resetGoalRuntimeForTest()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('会话已经不在时,定时器到点自己收尾,不注入', async () => {
    vi.useFakeTimers()
    const injections: Array<{ text: string; goalId: string }> = []
    await deferGoal(injections)

    rig.histories.delete(S)
    await vi.advanceTimersByTimeAsync(HALF_HOUR)
    expect(injections).toEqual([])
    expect(getActiveGoal(S)).toBeUndefined()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('手动暂停保留目标,但不再自动唤醒', async () => {
    vi.useFakeTimers()
    const injections: Array<{ text: string; goalId: string }> = []
    const context = await deferGoal(injections)
    expect(runtimeHooksFor(S)).toEqual([])

    pauseGoal(S)
    expect(getActiveGoal(S)).toMatchObject({ condition: CONDITION, origin: 'user' })
    expect(getActiveGoal(S)?.deferredSince).toBeTypeOf('number')
    expect(runtimeHooksFor(S)).toEqual([])
    expect(vi.getTimerCount()).toBe(0)

    await vi.advanceTimersByTimeAsync(24 * 60 * 60_000)
    expect(injections).toEqual([])
    void context
  })

  it('★ 用户开口那一轮把空闲计数归零并重新挂上钩子(下一次回合末接着判)', async () => {
    vi.useFakeTimers()
    const injections: Array<{ text: string; goalId: string }> = []
    const context = await deferGoal(injections)

    await vi.advanceTimersByTimeAsync(HALF_HOUR)
    expect(getActiveGoal(S)).toMatchObject({ idleCheckinCount: 1 })
    // 推迟期间钩子是摘掉的 —— 后台还在跑,判定没有意义
    expect(runtimeHooksFor(S)).toEqual([])

    bindGoalRun(S, context, false)
    expect(getActiveGoal(S)).toMatchObject({ idleCheckinCount: 1 })
    expect(runtimeHooksFor(S).map((hook) => hook.id)).toEqual([goalHookId(S)])

    bindGoalRun(S, context, true)
    expect(getActiveGoal(S)).toMatchObject({ idleCheckinCount: 0 })
    expect(runtimeHooksFor(S).map((hook) => hook.id)).toEqual([goalHookId(S)])
    // 旧定时器已经清掉,由下一次回合末重新算
    expect(vi.getTimerCount()).toBe(0)

    await handleTurnEnd(turnEnd(), context)
    expect(vi.getTimerCount()).toBe(1)
  })
})

describe('goalProposalsFor · 模型自提目标', () => {
  /**
   * 两个入口 `goalProposalsFor` 都是必给的,但它在 `SessionDeps` 那一侧是可选的
   * (`Pick<SessionDeps, …>`)—— 这里收窄一次,免得每条断言后面挂一个 `!`。
   */
  interface ProposalApi {
    canProposeGoal: () => boolean
    proposeGoal: (condition: string, askUser: boolean) => Promise<'set' | 'pending'>
  }

  interface ProposalRig {
    handle: RunHandle
    gate: InteractionGate
    injected: Array<{ text: string; goalId: string }>
    input: Parameters<typeof goalProposalsFor>[0]
    api: ProposalApi
    state: { planning: boolean; setting: ModelProposedGoals }
  }

  function proposals(over: Partial<RunRequest> = {}): ProposalRig {
    // 会话得真的在:批准回调会核一次「这条会话还在不在」
    seed(S, assistantWith('a1'))
    const handle = new RunHandle({
      runId: 'run-1', sessionId: S, workspaceId: WS, depth: 0, input: [], mode: 'normal',
      thinking: 'off', webSearch: false, permissionMode: 'ask', model: 'main-model', skillIds: [], ...over
    })
    const gate = new InteractionGate()
    const injected: Array<{ text: string; goalId: string }> = []
    const state = { planning: false, setting: 'auto' as ModelProposedGoals }
    const input = {
      handle,
      gate,
      context: makeContext({
        inject: (parts, goalId) => {
          injected.push({ text: textOf(parts), goalId })
          return true
        },
        runHooks: stubHooks().runHooks
      }),
      interactive: true,
      planning: () => state.planning,
      setting: () => state.setting
    }
    return { handle, gate, injected, input, state, api: goalProposalsFor(input) as ProposalApi }
  }

  it('★ 五道门:子 run / 非交互 / plan / disabled / 已有待批', async () => {
    const child = proposals({ depth: 1 })
    expect(child.api.canProposeGoal()).toBe(false)
    await expect(child.api.proposeGoal(CONDITION, false)).rejects.toThrow('unavailable')

    const nonInteractive = proposals()
    nonInteractive.input.interactive = false
    expect(nonInteractive.api.canProposeGoal()).toBe(false)

    const planning = proposals()
    planning.state.planning = true
    expect(planning.api.canProposeGoal()).toBe(false)

    const disabled = proposals()
    disabled.state.setting = 'disabled'
    expect(disabled.api.canProposeGoal()).toBe(false)

    const pending = proposals()
    expect(await pending.api.proposeGoal(CONDITION, true)).toBe('pending')
    expect(pending.api.canProposeGoal()).toBe(false)
    await expect(pending.api.proposeGoal(CONDITION, true)).rejects.toThrow('unavailable')
  })

  it('已经收尾或已经中断的 run 不能再提', () => {
    const aborted = proposals()
    aborted.handle.abort({ by: 'user' })
    expect(aborted.api.canProposeGoal()).toBe(false)

    const finished = proposals()
    finished.handle.finish('done')
    expect(finished.api.canProposeGoal()).toBe(false)
  })

  it('★ auto 且模型断言「用户就是这么说的」:直接设立,并立刻发 kickoff', async () => {
    const rig2 = proposals()
    expect(await rig2.api.proposeGoal(CONDITION, false)).toBe('set')
    // 没有弹窗,一个待决项都没有
    expect(rig2.gate.list()).toEqual([])
    const goal = getActiveGoal(S)
    expect(goal).toMatchObject({ condition: CONDITION, origin: 'proposal_direct', iterations: 0 })
    expect(rig2.injected).toHaveLength(1)
    expect(rig2.injected[0]?.goalId).toBe(goal?.id)
    expect(rig2.injected[0]?.text).toBe(goalKickoffMessage(CONDITION))
  })

  it('★ alwaysAsk:模型说「不用问」也强制走审批', async () => {
    const rig2 = proposals()
    rig2.state.setting = 'alwaysAsk'
    expect(await rig2.api.proposeGoal(CONDITION, false)).toBe('pending')
    expect(rig2.gate.list()).toMatchObject([{ kind: 'goal_proposal', sessionId: S, condition: CONDITION }])
    expect(getActiveGoal(S)).toBeUndefined()
    expect(rig2.injected).toEqual([])
  })

  it('★ 只等派发:人还没答就返回 pending,批准之后才装上目标', async () => {
    const rig2 = proposals()
    rig2.state.setting = 'alwaysAsk'
    expect(await rig2.api.proposeGoal(CONDITION, true)).toBe('pending')
    const pending = rig2.gate.list()[0]!
    expect(getActiveGoal(S)).toBeUndefined()
    expect(rig2.injected).toEqual([])

    rig2.gate.respond({ id: pending.id, kind: 'goal_proposal', approved: true })
    await untilHolds(() => getActiveGoal(S) !== undefined)
    const goal = getActiveGoal(S)
    expect(goal).toMatchObject({ condition: CONDITION, origin: 'proposal_approved', iterations: 0 })
    expect(rig2.injected).toHaveLength(1)
    expect(rig2.injected[0]?.goalId).toBe(goal?.id)
    expect(rig2.injected[0]?.text).toBe(goalKickoffMessage(CONDITION))
  })

  it('★ 被拒:不设目标,也不发 kickoff', async () => {
    const rig2 = proposals()
    expect(await rig2.api.proposeGoal(CONDITION, true)).toBe('pending')
    rig2.gate.respond({ id: rig2.gate.list()[0]!.id, kind: 'goal_proposal', approved: false })
    await flushMicrotasks()
    expect(getActiveGoal(S)).toBeUndefined()
    expect(rig2.injected).toEqual([])
    expect(rig2.gate.list()).toEqual([])
  })

  it('★ run 正常收尾之后仍然可以被批准 —— 提案不占住 run', async () => {
    const rig2 = proposals()
    expect(await rig2.api.proposeGoal(CONDITION, true)).toBe('pending')
    const pending = rig2.gate.list()[0]!
    rig2.handle.finish('done')
    expect(rig2.gate.hasGoalProposal(S)).toBe(true)

    rig2.gate.respond({ id: pending.id, kind: 'goal_proposal', approved: true })
    await untilHolds(() => getActiveGoal(S) !== undefined)
    expect(getActiveGoal(S)).toMatchObject({ condition: CONDITION, origin: 'proposal_approved' })
    expect(rig2.injected).toHaveLength(1)
  })

  it('can approve a proposal after the previous goal completed automatically', async () => {
    const proposal = proposals()
    activateGoal({ sessionId: S, condition: 'previous goal', origin: 'user', now: Date.now() })
    expect(await proposal.api.proposeGoal(CONDITION, true)).toBe('pending')
    deactivateGoal(S, 'met')
    proposal.handle.finish('done')
    proposal.gate.respond({ id: proposal.gate.list()[0]!.id, kind: 'goal_proposal', approved: true })
    await untilHolds(() => getActiveGoal(S)?.condition === CONDITION)
    expect(getActiveGoal(S)?.origin).toBe('proposal_approved')
    expect(proposal.injected).toHaveLength(1)
  })

  it('★ 审批期间设置被关掉 / 目标被手动清掉:手里那张批准条已经过期', async () => {
    const off = proposals()
    expect(await off.api.proposeGoal(CONDITION, true)).toBe('pending')
    off.state.setting = 'disabled'
    off.gate.respond({ id: off.gate.list()[0]!.id, kind: 'goal_proposal', approved: true })
    await flushMicrotasks()
    expect(getActiveGoal(S)).toBeUndefined()
    expect(off.injected).toEqual([])

    const cleared = proposals()
    expect(await cleared.api.proposeGoal(CONDITION, true)).toBe('pending')
    // 用户同时手动设了一个目标又清掉 —— 版本号前进,旧批准条跟着作废
    activateGoal({ sessionId: S, condition: '另一个条件', origin: 'user', now: 1_000 })
    deactivateGoal(S, 'user_clear')
    cleared.gate.respond({ id: cleared.gate.list()[0]!.id, kind: 'goal_proposal', approved: true })
    await flushMicrotasks()
    expect(getActiveGoal(S)).toBeUndefined()
    expect(cleared.injected).toEqual([])
  })
})
