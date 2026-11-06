/**
 * 账号列表的**纯逻辑** —— 徽章判定、倒计时拆分、拖拽落点。
 *
 * ## 为了什么需求建的
 *
 * 账号行上要显示四种状态(可用 / 限流中 + 倒计时 / 需重新登录 / 已停用)、
 * 一条倒计时、以及 Codex 的两条额度条。这些判断如果留在 `.tsx` 里就**测不到**:
 * `vitest.config.ts` 的 `include` 只收 `.ts`(见 `provider-auth.ts` 的文件头,
 * 同一个理由)。
 *
 * ## 它拥有哪条不变式
 *
 * **这里不产生任何一句用户可见的句子。** 返回的是 `kind` + 数字,文案由组件
 * 用 `t()` 拼(§6)。返回中文的话,英文界面上会出现半句中文,而这类问题
 * 只有切到英文才看得见。
 *
 * ## 故意不做什么
 *
 * - **不调 `Date.now()`**。每个函数都收 `now`:组件用一个 1 秒 tick 喂进来,
 *   测试喂固定值。内部取时间的话,倒计时没法测,而「跨过零点那一刻」正是
 *   最容易写错的地方。
 * - **不判「限流是不是已经解除」**。那个答案归主进程(两个进程不是同一个时钟源,
 *   见 `CredentialAuthInfo.expired` 的注释);这里只算「还剩多少」。
 */
import type { ProviderAccount, ProviderQuotaWindow } from '../../../../../shared/domain/provider-account'
import { isAccountLimited, sortAccounts } from '../../../../../shared/domain/provider-account'

/**
 * 这一行显示成什么样。
 *
 * ★ 顺序就是优先级,**不能换**:
 * 1. `disabled` 压过一切 —— 用户自己关的,那一刻其它状态对他没有意义;
 * 2. `needs-reauth` 压过 `limited` —— 前者要他动手(重新登录),后者只要等。
 *    反过来的话,一个失效的账号会显示「1 小时后恢复」,而它永远不会恢复。
 */
export type AccountBadge = 'disabled' | 'needs-reauth' | 'limited' | 'ready'

export function accountBadge(account: ProviderAccount, now: number): AccountBadge {
  if (!account.enabled) return 'disabled'
  if (account.needsReauth) return 'needs-reauth'
  if (isAccountLimited(account, now)) return 'limited'
  return 'ready'
}

/**
 * 倒计时拆成「几小时几分几秒」。
 *
 * ★ 返回数字而不是 `'1 小时 12 分'`:那句话要按 locale 拼(§6 规则 1)。
 * ★ `totalMs <= 0` 时全是 0 —— 到点之后组件停止 tick,由主进程的下一次广播
 *   给出真正的状态。这里不自己判「已经解除了」,理由见文件头。
 */
export interface Countdown {
  hours: number
  minutes: number
  seconds: number
  totalMs: number
}

export function countdownTo(until: number, now: number): Countdown {
  const totalMs = Math.max(0, until - now)
  const totalSeconds = Math.ceil(totalMs / 1000)
  return {
    hours: Math.floor(totalSeconds / 3600),
    minutes: Math.floor((totalSeconds % 3600) / 60),
    seconds: totalSeconds % 60,
    totalMs
  }
}

/**
 * 这一屏要不要跑那个 1 秒 tick。
 *
 * ★★ 需求:倒计时必须每秒刷,但**没有账号在限流时一秒都不该刷** ——
 * 设置页常年开着,一个永不停的 `setInterval` 会让这一片每秒重渲一次,
 * 而那几行里还嵌着额度条的动画。
 */
export function needsCountdownTick(accounts: readonly ProviderAccount[], now: number): boolean {
  return accounts.some((account) => account.enabled && isAccountLimited(account, now))
}

/**
 * 额度条的展示态。
 *
 * ★ `null` = **没有快照**,不是 0%。两者在界面上必须是两个东西:
 * 前者要说「发一条消息后更新」(数据只在请求时搭便车回来,产品决策 D6),
 * 后者是「这个窗口还没用过」。画成 0% 的话,一个刚登录的账号看起来额度全满,
 * 而它其实可能已经用光了。
 */
export interface QuotaBar {
  /** 0–100,已经夹紧过 */
  percent: number
  /** 300 → `'5h'`,10080 → `'week'`,其余按分钟数显示 */
  window: '5h' | 'week' | 'other'
  windowMinutes: number
  resetsAt: number
  /** ≥ 90% 时界面换告警色:这是「该换号了」的最后一个提示 */
  critical: boolean
}

export function quotaBar(window: ProviderQuotaWindow | undefined): QuotaBar | null {
  if (window === undefined) return null
  const percent = Math.max(0, Math.min(100, window.usedPercent))
  return {
    percent,
    window: window.windowMinutes === 300 ? '5h' : window.windowMinutes === 10_080 ? 'week' : 'other',
    windowMinutes: window.windowMinutes,
    resetsAt: window.resetsAt,
    critical: percent >= 90
  }
}

/**
 * 快照有多旧。★ 超过一天就在界面上标一句「N 小时前的数据」——
 * 额度只在发消息时更新,一个几天没用过的账号会显示一份很旧的数,
 * 而界面上没有任何迹象说明它旧。
 */
export const STALE_QUOTA_MS = 24 * 3600_000

export function isQuotaStale(capturedAt: number, now: number): boolean {
  return now - capturedAt > STALE_QUOTA_MS
}

/**
 * 拖拽之后的新顺序。**纯数组运算,不碰 DOM。**
 *
 * ★ `to` 允许等于 `length`(拖到列表最末尾那一下),所以不能写成
 * `to >= length → 不动`:那会让「拖到最后一位」这个最常见的操作看起来失灵。
 */
export function reorder(ids: readonly string[], from: number, to: number): string[] {
  if (from < 0 || from >= ids.length) return [...ids]
  const next = [...ids]
  const [moved] = next.splice(from, 1)
  if (moved === undefined) return [...ids]
  next.splice(Math.max(0, Math.min(next.length, to)), 0, moved)
  return next
}

/** 列表渲染顺序 = 轮换顺序。★ 借 `sortAccounts`,不自己再排一遍(两份排序会分叉) */
export function orderedAccounts(accounts: readonly ProviderAccount[]): ProviderAccount[] {
  return sortAccounts(accounts)
}

/**
 * 「下一次请求会用哪个账号」——列表顶部那句提示。
 *
 * ★★ **不在这里重写选择规则**,直接借主进程也在用的那个纯函数会更准,
 * 但它要 `rotation` 这个设置项;组件手里有,所以由调用方传进来。
 * 两边分家的表现是「界面说会用 A,请求发给了 B」——正是 `model-selection.ts`
 * 当年那个 bug 的形状。
 */
export function activeAccountId(
  accounts: readonly ProviderAccount[],
  now: number,
  rotation: boolean
): string | null {
  const sorted = sortAccounts(accounts)
  if (!rotation) return (sorted.find((a) => a.current) ?? sorted[0])?.id ?? null
  return sorted.find((a) => accountBadge(a, now) === 'ready')?.id ?? null
}
