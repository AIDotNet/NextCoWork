/**
 * 钱包充值的跨边界数据形状（主进程 `ipc/recharge.ts` ⇄ 设置页「钱包」）。
 *
 * 需求：设置页的钱包要能在应用内发起充值。平台的门户充值接口只认网页 Cookie，
 * CoWork 为桌面端开了一组镜像（`/api/client/recharge/*`，挂在 `wallet:read` 下）。
 * 这个文件是那组接口在桌面端的唯一形状定义。
 *
 * 不变式：
 * - **档位以服务端为唯一真源**（`amounts`）。客户端写死一份，服务端改档位后
 *   用户选出来的金额会被 checkout 以 400 拒掉，而界面上看起来一切正常。
 * - **金额一律是数字**，不在这一层格式化；币种跟 `currency` 走（协议字段，不翻译）。
 * - **Stripe 收银页的地址不过渲染层**。主进程校验完 `https://checkout.stripe.com`
 *   就直接交给系统浏览器 —— 渲染层拿到的只有订单号，它无从打开任意地址。
 *
 * 故意不做的事：不在桌面端做支付结果的最终裁决。订单状态由 Stripe Webhook 在服务端
 * 推进，这里只轮询、只展示；余额以 `/api/client/account` 返回的为准。
 */

/**
 * 拿不到数据的原因。四种原因在界面上是四句不同的话、四种不同的下一步：
 * 重新登录 / 等平台部署 / 先选 Team / 重试。
 */
export type RechargeUnavailableReason =
  /** 本地模式或登录已失效（401/403）。 */
  | 'signed-out'
  /** 平台还没部署这组接口（404）：桌面端比服务端先发版时就是这一种，重试永远不会好。 */
  | 'unsupported'
  /** 会话还没绑定 Team（409 `team_context_required`）。 */
  | 'team-required'
  /** 网络失败 / 5xx：可以重试。 */
  | 'network'

/** 为什么「不能充」：与 CoWork `GetClientOptionsAsync` 的 `blockedReason` 一一对应。 */
export type RechargeBlockedReason =
  /** 当前 Team 里不是 Owner / Admin。 */
  | 'role'
  /** 钱包不存在、被冻结或不是 USD。 */
  | 'wallet'

export interface RechargeOptions {
  currency: string
  /** 可选档位（整数美元，升序）。 */
  amounts: number[]
  /** 平台是否开了 Stripe 充值。false 时档位照样展示，但不能下单。 */
  rechargeEnabled: boolean
  /** 当前会话 Team 能否充值。 */
  canRecharge: boolean
  blockedReason: RechargeBlockedReason | null
}

export type RechargeOptionsState =
  | { kind: 'ready'; options: RechargeOptions }
  | { kind: 'unavailable'; reason: RechargeUnavailableReason }

export type RechargeCheckoutResult =
  /** 收银页已在系统浏览器打开；渲染层据 `orderNo` 轮询结果。 */
  | { kind: 'opened'; orderNo: string; amount: number; currency: string }
  | { kind: 'unavailable'; reason: RechargeUnavailableReason }
  /**
   * 服务端明确拒绝（400/403/409/503 带业务 message，如「只有 Team Owner 或 Admin 可以充值」）。
   * `message` 是服务端给的原话，没有时为 null，由渲染层换成通用文案。
   */
  | { kind: 'rejected'; message: string | null }

/** 与 CoWork `RechargeStatus` 的名字一一对应。 */
export type RechargeOrderStatus = 'Pending' | 'Paid' | 'Completed' | 'Failed' | 'Cancelled' | 'Refunded'

export interface RechargeOrder {
  orderNo: string
  status: RechargeOrderStatus
  amount: number
  currency: string
}

export type RechargeOrderState =
  | { kind: 'ready'; order: RechargeOrder }
  /** 订单对当前会话不可见（404 —— 换了 Team 或订单不存在）。 */
  | { kind: 'not-found' }
  | { kind: 'unavailable'; reason: RechargeUnavailableReason }
