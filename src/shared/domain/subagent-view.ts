/**
 * 子代理的**展示投影** —— 转录里的卡片(`parts.tsx` 的 `SubagentNode`)、右上角活动胶囊
 * (`ActivityCapsule` 的子代理栏)、点开后的只读子会话,三处读同一个函数。
 *
 * ★★ **为什么必须只有一份。** 三处以前各自从 `SubagentState` 算自己的「状态 / 耗时 / 能不能停 /
 * 汇报到哪了」:卡片的耗时走 `elapsedOf`,胶囊用 `endedAt ?? now`(终态但缺 `endedAt` 时
 * 胶囊的计时一直在走);卡片的停止会在 abort 之后去主进程核实并收口,胶囊的停止只 abort
 * (run 早已被回收时胶囊里那一行永远「运行中」);卡片缺状态时兜底 done,胶囊直接按
 * `status` 读。同一个子代理在同一屏上说两种话,而每一处单看都「没错」。
 *
 * 规则(写进了 AGENTS.md):**状态只有一个真源(父转录的 `transcript.subagents`),
 * 派生只有一个函数(这里)**。组件只负责把这里的输出摆进版式,不许自己再判一遍。
 *
 * 纯函数,不读时钟 —— `now` 由调用方给,于是这组判定能被单测穷尽。
 */
import type { RunStatus } from '../agent/event'
import type { SubagentState } from '../agent/transcript'

/** 卡片 / 胶囊上的那一格状态。`pending` = `Task` 的参数还在流、`subagent_start` 还没到。 */
export type SubagentDisplayStatus = RunStatus | 'pending'

export type SubagentReportView = 'none' | 'pending' | 'injecting' | 'reported' | 'blocked'

export interface SubagentView {
  status: SubagentDisplayStatus
  running: boolean
  /** 已跑了多久。没有 `startedAt`(旧转录)时缺省 —— 没有事实就不画这一格 */
  elapsedMs?: number
  /** 在跑时是「当前工具 · 目标」,否则是子代理类型 */
  detail?: string
  /** 只有后台子代理才有「回传给主代理」这一步,前台恒为 `none` */
  report: SubagentReportView
  /** 能不能点开它的只读会话(旧转录没有 `childSessionId`) */
  canOpen: boolean
  /** 能不能停 */
  canStop: boolean
  /** 能不能转后台(只对还在跑的前台子代理成立) */
  canBackground: boolean
}

/**
 * @param state 父转录里的那一份;`undefined` = 表里还没有(参数还在流,或旧转录)
 * @param pending `Task` 的参数还在流。★ 不能拿「state 为空」代替:旧转录里的子代理块
 *   同样没有 state,而它们早就跑完了 —— 所以 state 缺省且不 pending 时兜底 done。
 */
export function subagentViewOf(state: SubagentState | undefined, now: number, pending = false): SubagentView {
  const status: SubagentDisplayStatus = state === undefined ? (pending ? 'pending' : 'done') : state.status
  const running = status === 'running'
  const background = state?.background === true
  return {
    status,
    running,
    ...(state?.startedAt === undefined ? {} : { elapsedMs: elapsedMsOf(state, now) }),
    ...detailOf(state, running),
    report: reportOf(state, status, background),
    canOpen: state?.childSessionId !== undefined,
    canStop: running && state !== undefined,
    canBackground: running && !background && state !== undefined
  }
}

/**
 * 终态但缺 `endedAt`(从库里回执重建的卡片、不带 `at` 的旧事件)时**用最后一次动静**收口,
 * 再不行就不画 —— 绝不拿 `now` 去算一个终态子代理的耗时,那会让它的计时一直走。
 */
function elapsedMsOf(state: SubagentState, now: number): number | undefined {
  const startedAt = state.startedAt
  if (startedAt === undefined) return undefined
  if (state.status === 'running') return Math.max(0, now - startedAt)
  const end = state.endedAt ?? state.lastEventAt
  return end === undefined ? undefined : Math.max(0, end - startedAt)
}

function detailOf(state: SubagentState | undefined, running: boolean): { detail?: string } {
  if (state === undefined) return {}
  if (running && state.currentTool !== undefined) {
    return { detail: [state.currentTool, state.currentTarget].filter((part) => part !== undefined && part !== '').join(' · ') }
  }
  return state.subagentType === undefined ? {} : { detail: state.subagentType }
}

/**
 * ★ 「汇报」**只对后台子代理成立**。前台子代理的结果就是它那条 `tool_result`,
 * 同步回到主代理,不存在「回传」这一步;以前的兜底给每一张跑完的前台卡片都挂了一句
 * 「结果已汇报给主代理」,把后台专属的信号抹平了。
 */
function reportOf(state: SubagentState | undefined, status: SubagentDisplayStatus, background: boolean): SubagentReportView {
  if (!background) return 'none'
  if (state?.reportStatus !== undefined) return state.reportStatus
  if (status === 'done') return 'pending'
  if (status === 'running' || status === 'pending') return 'none'
  return 'reported'
}

/** 按「在跑的在前、同一档里新开的在前」排。胶囊列表与任何以后的列表共用这一个序。 */
export function compareSubagentsByRecency(left: SubagentState, right: SubagentState): number {
  const live = Number(right.status === 'running') - Number(left.status === 'running')
  return live !== 0 ? live : (right.startedAt ?? 0) - (left.startedAt ?? 0)
}
