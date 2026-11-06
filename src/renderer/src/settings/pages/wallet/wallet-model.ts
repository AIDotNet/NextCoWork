/**
 * 钱包页的纯逻辑：金额格式化、充值入口的可用性判定、订单轮询的阶段。
 *
 * 需求：钱包页重写成「余额卡片 + 应用内充值」。这些判断从组件里
 * 抽出来放在这里（§9），因为它们正是最容易悄悄写错、又只能起 Electron 才看得见的部分：
 * 「为什么不能充」该说哪一句、订单到哪一步该停止轮询。
 *
 * 需求：钱包页不再展示消费记录，按天分组的 `groupUsageByDay` / `UsageDay` 与只服务于它的
 * `formatCost` / `formatCount` 随之删除 —— 留着就是没人调的死代码。要看明细去「使用统计」页。
 *
 * 故意不做的事：
 * - 不翻译。这里只产出**原因 / 阶段的枚举**，文案由组件按枚举取 i18n key（§6）。
 * - 不写死币种。币种是接口给的领域值（§6.5），格式化时原样交给 `Intl`。
 */
import type {
  RechargeOptions,
  RechargeOptionsState,
  RechargeOrderState
} from '../../../../../shared/domain/recharge'

/**
 * 网页钱包。只在「平台还没部署客户端充值接口」时作为退路出现（见 `rechargeGate`）。
 * 同 `AccountMenu.tsx` 的 `HELP_URL`：站点地址属于平台，换域名时改这一行。
 */
export const WEB_WALLET_URL = 'https://nextco.work/dashboard/wallet'

/**
 * 订单轮询节奏。
 * 需求：用户在浏览器里付完款切回来，余额要自己变；但 Stripe 收银页开着两小时都有效，
 * 不能无限轮询下去。3 秒一次、10 分钟封顶，之后交给「刷新状态」按钮。
 * 切回窗口（focus）时另外立即查一次 —— 那是用户最可能刚付完的时刻。
 */
export const ORDER_POLL_INTERVAL_MS = 3_000
export const ORDER_POLL_DEADLINE_MS = 10 * 60_000

/** 余额：千分位 + 两位小数（同 `AccountMenu.tsx` 的理由：`toFixed` 不分组，大额易读错）。 */
export function formatBalance(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
}

/**
 * 充值档位：货币样式、不带小数（「US$10」而不是「US$10.00」）。币种不合法时退回「10 USD」。
 */
export function formatAmount(value: number, currency: string, locale: string): string {
  try {
    return new Intl.NumberFormat(locale, {
      style: 'currency', currency, minimumFractionDigits: 0, maximumFractionDigits: 2
    }).format(value)
  } catch {
    return `${value} ${currency}`
  }
}

/**
 * 充值入口该画成什么。
 * ★ 只有 `ready` 才画「充值」按钮；其余每一种都是一句话解释「为什么不能充」（§5：
 * 后端接不了的操作就别画控件）。`unsupported` 额外给网页退路 —— 桌面端先于服务端发版时，
 * 用户仍然要能充上钱。
 */
export type RechargeGate =
  | { kind: 'loading' }
  | { kind: 'ready'; options: RechargeOptions }
  | { kind: 'blocked'; reason: RechargeGateReason }

export type RechargeGateReason =
  | 'role' | 'wallet' | 'disabled' | 'unsupported' | 'team-required' | 'signed-out' | 'network'

export function rechargeGate(state: RechargeOptionsState | null): RechargeGate {
  if (state === null) return { kind: 'loading' }
  if (state.kind === 'unavailable') return { kind: 'blocked', reason: state.reason }
  const { options } = state
  if (!options.canRecharge) return { kind: 'blocked', reason: options.blockedReason ?? 'wallet' }
  if (!options.rechargeEnabled || options.amounts.length === 0) return { kind: 'blocked', reason: 'disabled' }
  return { kind: 'ready', options }
}

/** 充值弹窗里订单所处的阶段（等待中的两种 + 五种停下来的结局）。 */
export type OrderPhase =
  | 'waiting' | 'paid'
  | 'completed' | 'cancelled' | 'failed' | 'refunded' | 'not-found' | 'signed-out'

/**
 * 把一次轮询结果折算成阶段；返回 null 表示「这次没查到有用的信息，保持原阶段继续轮询」。
 * ★ 网络失败 / 5xx / 未部署都返回 null 而不是停下：轮询期间偶发的一次失败不该把
 * 「等待支付」翻成错误页，用户此刻很可能正在浏览器里付款。
 */
export function orderPhaseOf(state: RechargeOrderState): OrderPhase | null {
  if (state.kind === 'not-found') return 'not-found'
  if (state.kind === 'unavailable') return state.reason === 'signed-out' ? 'signed-out' : null
  switch (state.order.status) {
    case 'Pending': return 'waiting'
    case 'Paid': return 'paid'
    case 'Completed': return 'completed'
    case 'Cancelled': return 'cancelled'
    case 'Failed': return 'failed'
    case 'Refunded': return 'refunded'
  }
}

/**
 * 阶段是否意味着停止轮询。
 * ★ `paid` **不是**终态：Stripe 已扣款但 Webhook 还没入账，此时停下来的话用户看到
 * 「已付款」却永远等不到「已到账」。
 */
export function isSettledPhase(phase: OrderPhase): boolean {
  return phase !== 'waiting' && phase !== 'paid'
}
