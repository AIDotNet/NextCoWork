/**
 * 设置 › 钱包页（余额、应用内充值）的文案。
 *
 * 需求：钱包页重写并接入应用内充值，照 `usage.ts` / `rewards.ts` 的先例单独建文件，
 * 不再往 `index.tsx` 那三千行里堆（§6.3）。旧钱包块用过的 `auth.*` 键仍被账户页使用，
 * 所以没有挪过来，这里只放钱包页自己的。
 *
 * 需求：钱包页不再展示消费记录，`wallet.usage.*` 与只服务于它的 `wallet.loading` 一并删除
 * （连带 WalletPage 那块的取数与渲染）；`index.test.ts` 只校验 zh/en 键对齐，不留残键也不会挂。
 *
 * ★ `walletEn` **不要**标 `Record<keyof typeof walletZh, string>` —— 带插值的条目值
 * 是函数不是 string，标了整张表都不匹配。zh/en 的键对齐由 `index.test.ts` 逐键比对守着。
 *
 * 不翻译的东西：币种代码（`USD` 来自接口）、Team 名、模型名、订单号（§6.5）。
 * 服务端带回来的拒绝原因是服务端原话，经 `wallet.recharge.rejectedDetail` 原样插进句子。
 */

/**
 * 带参数文案的入参类型。★ 必须显式标出来：这个文件拿不到 `index.tsx` 里
 * `Messages` 的上下文，不标的话参数是 implicit any，spread 进 `ZH` 时整张表都不再匹配。
 */
type Params = Record<string, string | number>

export const walletZh = {
  'wallet.balance': '可用余额',
  'wallet.teamLabel': '当前 Team',
  'wallet.cash': '现金余额',
  'wallet.gift': '赠送余额',
  'wallet.consumed': '累计消费',
  'wallet.unavailable': '当前 Team 暂无可用钱包。',
  'wallet.refresh': '刷新钱包',
  'wallet.recharge': '充值',
  'wallet.openWeb': '前往网页充值',
  'wallet.blocked.role': '只有 Team 所有者或管理员可以充值。',
  'wallet.blocked.wallet': '钱包当前不可用，暂时无法充值。',
  'wallet.blocked.disabled': '平台暂未开放充值。',
  'wallet.blocked.unsupported': '当前平台版本暂不支持在应用内充值，可以前往网页充值。',
  'wallet.blocked.team-required': '请先在账户页选择一个 Team。',
  'wallet.blocked.signed-out': '登录已失效，请重新登录后再充值。',
  'wallet.blocked.network': '充值信息加载失败，请刷新重试。',

  'wallet.recharge.title': '充值',
  'wallet.recharge.description': '金额会充入当前 Team 的钱包，支付由 Stripe 安全处理。',
  'wallet.recharge.descriptionTeam': ({ team }: Params) => `金额会充入「${team}」的钱包，支付由 Stripe 安全处理。`,
  'wallet.recharge.amountLabel': '选择充值金额',
  'wallet.recharge.pay': ({ amount }: Params) => `前往支付 ${amount}`,
  'wallet.recharge.creating': '正在创建订单…',
  'wallet.recharge.rejected': '创建支付失败，请稍后重试。',
  'wallet.recharge.rejectedDetail': ({ message }: Params) => `创建支付失败：${message}`,
  'wallet.recharge.unavailable.signed-out': '登录已失效，请重新登录后再充值。',
  'wallet.recharge.unavailable.unsupported': '当前平台版本暂不支持在应用内充值，可以前往网页充值。',
  'wallet.recharge.unavailable.team-required': '请先在账户页选择一个 Team。',
  'wallet.recharge.unavailable.network': '网络异常，创建支付失败，请重试。',

  'wallet.order.orderNo': ({ orderNo }: Params) => `订单号 ${orderNo}`,
  'wallet.order.waitingTitle': '等待支付完成',
  'wallet.order.waitingHint': '已在浏览器中打开 Stripe 收银台。完成支付后这里会自动更新；也可以先关闭此窗口，稍后刷新余额。',
  'wallet.order.paidTitle': '已收到付款',
  'wallet.order.paidHint': '正在入账，通常只需几秒。',
  'wallet.order.completedTitle': '充值成功',
  'wallet.order.completedHint': ({ amount }: Params) => `${amount} 已充入钱包。`,
  'wallet.order.cancelledTitle': '支付已取消',
  'wallet.order.failedTitle': '支付失败',
  'wallet.order.refundedTitle': '这笔订单已退款',
  'wallet.order.endedHint': '钱包余额没有变化，可以重新选择金额发起充值。',
  'wallet.order.notFoundTitle': '无法查询这笔订单',
  'wallet.order.notFoundHint': '可能已切换 Team。支付结果可以在网页钱包中查看。',
  'wallet.order.signedOutTitle': '登录已失效',
  'wallet.order.signedOutHint': '重新登录后刷新钱包即可查看支付结果。',
  'wallet.order.timeoutTitle': '暂未收到支付结果',
  'wallet.order.timeoutHint': '如果已经完成支付，请稍后刷新状态。',
  'wallet.order.checkAgain': '刷新状态',
  'wallet.order.retry': '重新选择金额'
}

export const walletEn = {
  'wallet.balance': 'Available balance',
  'wallet.teamLabel': 'Current team',
  'wallet.cash': 'Cash',
  'wallet.gift': 'Gift credit',
  'wallet.consumed': 'Total spent',
  'wallet.unavailable': 'This team has no available wallet.',
  'wallet.refresh': 'Refresh wallet',
  'wallet.recharge': 'Top up',
  'wallet.openWeb': 'Top up on the web',
  'wallet.blocked.role': 'Only team owners or admins can top up.',
  'wallet.blocked.wallet': 'The wallet is unavailable right now, so it cannot be topped up.',
  'wallet.blocked.disabled': 'Top-ups are not open on the platform yet.',
  'wallet.blocked.unsupported': 'The platform does not support in-app top-ups yet. You can top up on the web instead.',
  'wallet.blocked.team-required': 'Choose a team on the Account page first.',
  'wallet.blocked.signed-out': 'Your session has expired. Sign in again to top up.',
  'wallet.blocked.network': 'Could not load top-up options. Refresh to try again.',

  'wallet.recharge.title': 'Top up',
  'wallet.recharge.description': 'Funds go to the current team’s wallet. Payment is handled securely by Stripe.',
  'wallet.recharge.descriptionTeam': ({ team }: Params) => `Funds go to the “${team}” wallet. Payment is handled securely by Stripe.`,
  'wallet.recharge.amountLabel': 'Choose an amount',
  'wallet.recharge.pay': ({ amount }: Params) => `Pay ${amount}`,
  'wallet.recharge.creating': 'Creating order…',
  'wallet.recharge.rejected': 'Could not create the payment. Please try again later.',
  'wallet.recharge.rejectedDetail': ({ message }: Params) => `Could not create the payment: ${message}`,
  'wallet.recharge.unavailable.signed-out': 'Your session has expired. Sign in again to top up.',
  'wallet.recharge.unavailable.unsupported': 'The platform does not support in-app top-ups yet. You can top up on the web instead.',
  'wallet.recharge.unavailable.team-required': 'Choose a team on the Account page first.',
  'wallet.recharge.unavailable.network': 'Network error while creating the payment. Please try again.',

  'wallet.order.orderNo': ({ orderNo }: Params) => `Order ${orderNo}`,
  'wallet.order.waitingTitle': 'Waiting for payment',
  'wallet.order.waitingHint': 'Stripe checkout is open in your browser. This updates automatically once you pay, or close this window and refresh your balance later.',
  'wallet.order.paidTitle': 'Payment received',
  'wallet.order.paidHint': 'Crediting your wallet. This usually takes a few seconds.',
  'wallet.order.completedTitle': 'Top-up complete',
  'wallet.order.completedHint': ({ amount }: Params) => `${amount} has been added to your wallet.`,
  'wallet.order.cancelledTitle': 'Payment cancelled',
  'wallet.order.failedTitle': 'Payment failed',
  'wallet.order.refundedTitle': 'This order was refunded',
  'wallet.order.endedHint': 'Your balance has not changed. You can choose an amount and try again.',
  'wallet.order.notFoundTitle': 'Could not look up this order',
  'wallet.order.notFoundHint': 'You may have switched teams. Check the result in your web wallet.',
  'wallet.order.signedOutTitle': 'Session expired',
  'wallet.order.signedOutHint': 'Sign in again and refresh your wallet to see the result.',
  'wallet.order.timeoutTitle': 'No payment result yet',
  'wallet.order.timeoutHint': 'If you already paid, refresh the status in a moment.',
  'wallet.order.checkAgain': 'Refresh status',
  'wallet.order.retry': 'Choose another amount'
}
