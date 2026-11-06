/**
 * 「刚刚有一轮成功跑完」—— 输入框建议下一步的触发信号。
 *
 * ★★ 只认**这个挂载亲眼看着结束**的那一轮。`runEndedAt` 会在不少不是「刚跑完」的时候变化:
 *   - 挂载那一刻就已经有值(切 Tab 回来、草稿铸出会话 id 后的重挂载)—— 那一轮早结束了,
 *     用户已经看过、也许已经打过字了;
 *   - `restoreDetachedParent` 从快照里**直接写入** `runEndedAt`,而 `activeRunId` 全程是 null ——
 *     那是一轮没人在场的 run。
 *   两种都去发请求的话,就是「切个 Tab 花一次钱」「恢复个会话冒出一句不知所云的建议」。
 *   所以要求:挂载后见过 `running === true`(挂载时正在跑也算),然后它结束了。
 */
import { useRef } from 'react'
import type { RunStatus } from '../../../../shared/agent/event'

export interface SuggestWatch {
  mountEndedAt: number | undefined
  sawRunning: boolean
  firedFor: number | undefined
}

export function initSuggestWatch(runEndedAt: number | undefined, running: boolean): SuggestWatch {
  return { mountEndedAt: runEndedAt, sawRunning: running, firedFor: undefined }
}

/** 推进一步。`ended` 有值 = 这一刻刚刚看着一轮结束了(每轮只报一次)。 */
export function stepSuggestWatch(
  w: SuggestWatch,
  now: { running: boolean; runEndedAt: number | undefined }
): { watch: SuggestWatch; ended?: number } {
  const sawRunning = w.sawRunning || now.running
  if (now.running || !sawRunning || now.runEndedAt === undefined
    || now.runEndedAt === w.mountEndedAt || now.runEndedAt === w.firedFor) {
    return { watch: sawRunning === w.sawRunning ? w : { ...w, sawRunning } }
  }
  return { watch: { ...w, sawRunning: false, firedFor: now.runEndedAt }, ended: now.runEndedAt }
}

export interface SuggestEligibility {
  status: RunStatus
  queued: number
  goalActive: boolean
  planMode: boolean
  /** `undefined` = 还没读到(当作不合格);`null` = 普通会话;字符串 = Skill 提炼会话 */
  skillSourceId: string | null | undefined
  sessionId: string | null
}

/**
 * 这一轮结束之后该不该猜下一句。
 *
 * ★ 每一条「不该」都对应一种「猜了也白猜」:报错 / 被停(用户要先看发生了什么)、
 *   还有排队消息(马上又开跑)、目标挂着(判定器会再踢一轮)、plan 模式(下一步是审批计划)、
 *   Skill 提炼会话(它不是用户在对话)。
 */
export function suggestEligible(e: SuggestEligibility): boolean {
  return e.status === 'done' && e.queued === 0 && !e.goalActive && !e.planMode
    && e.skillSourceId === null && e.sessionId !== null
}

/** 返回最近一次「合格地看着跑完」的那个 `runEndedAt`;没有就是 `undefined`。 */
export function useSuggestTrigger(args: SuggestEligibility & { running: boolean; runEndedAt: number | undefined }): number | undefined {
  const watch = useRef<SuggestWatch | null>(null)
  const trigger = useRef<number | undefined>(undefined)
  watch.current ??= initSuggestWatch(args.runEndedAt, args.running)
  const { watch: next, ended } = stepSuggestWatch(watch.current, args)
  watch.current = next
  if (ended !== undefined && suggestEligible(args)) trigger.current = ended
  return trigger.current
}
