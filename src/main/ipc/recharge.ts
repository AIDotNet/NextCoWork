/**
 * 钱包充值 —— `/api/client/recharge/*` 的唯一调用点。
 *
 * 需求：设置页「钱包」要能在应用内选档位、发起充值。门户那条
 * `POST /api/portal/recharge/checkout` 认网页 Cookie 并校验 Origin，桌面端令牌带
 * `client_id`，会被 CoWork `ClientOAuth.AllowsRequest` 的白名单判 401；平台因此开了
 * 一组客户端镜像（复用 `wallet:read`，老会话不必重新登录），这个文件把它接进来。
 *
 * ★ **失败不抛，一律翻译成 `shared/domain/recharge.ts` 里的原因。** 同 `ipc/referral.ts`：
 *   - 401 → 会话没了，要重新登录；
 *   - 404 → **桌面端比服务端先发版**（线上还没部署这组路由），重试永远不会好，必须单列；
 *   - 409 `team_context_required` → 会话还没绑 Team；
 *   - 其他 4xx/503 带业务 message（「只有 Team Owner 或 Admin 可以充值」「Stripe 尚未配置」）
 *     → 原话带回去给用户看；
 *   - 网络 / 其余 5xx → 可重试。
 * ★ **403 不是「未登录」**：这组路由的 403 是业务拒绝（角色不够），令牌失效在 CoWork
 *   的 `OnTokenValidated` 里就被判成 401 了。把 403 归到 signed-out，表现是 Member
 *   身份的用户点充值后被提示「请重新登录」，而重新登录永远不会好。
 *
 * ★ **Stripe 收银页在这里打开，地址不回渲染层。** 只放行 `https://checkout.stripe.com`
 * （同 CoWork 服务端与门户 `recharge-form.tsx` 的校验）—— 地址来自网络，是外部输入。
 */
import { shell } from 'electron'
import type {
  RechargeCheckoutResult,
  RechargeOptionsState,
  RechargeOrderState,
  RechargeOrderStatus,
  RechargeUnavailableReason
} from '../../shared/domain/recharge'
import { getHost } from '../runtime'
import { getClientAccessToken, getClientAuthState } from './client-auth'

/** 同 `ipc/referral.ts`、`ipc/skills.ts`：站点地址各模块各持一份常量。 */
const API_ORIGIN = 'https://nextco.work'

/**
 * 读接口 10 秒（同 `ACCOUNT_TIMEOUT_MS`，`net.fetch` 自己没有 deadline）。
 * checkout 给 30 秒：服务端要先向 Stripe 验价、再建 Session，两次外部往返（门户同样给 30 秒）。
 */
const READ_TIMEOUT_MS = 10_000
const CHECKOUT_TIMEOUT_MS = 30_000

const ORDER_STATUSES: Record<RechargeOrderStatus, true> = {
  Pending: true, Paid: true, Completed: true, Failed: true, Cancelled: true, Refunded: true
}

type Reply =
  | { ok: true; payload: Record<string, unknown> }
  | { ok: false; status: number | null; body: Record<string, unknown> }

async function request(path: string, init: { method: 'GET' | 'POST'; body?: unknown; timeoutMs: number }): Promise<Reply | RechargeUnavailableReason> {
  if (getClientAuthState().mode !== 'authenticated') return 'signed-out'
  const access = await getClientAccessToken()
  if (access === null || access === '') return 'signed-out'
  let response: Response
  try {
    response = await getHost().fetch(`${API_ORIGIN}${path}`, {
      method: init.method,
      headers: {
        Authorization: `Bearer ${access}`,
        ...(init.body === undefined ? {} : { 'content-type': 'application/json' })
      },
      ...(init.body === undefined ? {} : { body: JSON.stringify(init.body) }),
      signal: AbortSignal.timeout(init.timeoutMs)
    })
  } catch {
    return 'network'
  }
  const body = await readJson(response)
  const record = isRecord(body) ? body : {}
  if (!response.ok) return { ok: false, status: response.status, body: record }
  const payload = isRecord(record['data']) ? record['data'] : record
  return { ok: true, payload }
}

/** 把失败的 HTTP 回复分到四种原因之一；返回 null 表示「服务端给了业务拒绝」。 */
function classify(status: number | null, body: Record<string, unknown>): RechargeUnavailableReason | null {
  if (status === 401) return 'signed-out'
  if (status === 404) return 'unsupported'
  if (status === 409 && body['error'] === 'team_context_required') return 'team-required'
  // 带业务 message 的 4xx/503 才算「拒绝」；裸 5xx / 没有 message 的归到可重试。
  if (status !== null && status >= 400 && typeof body['message'] === 'string' && body['message'] !== '') return null
  return 'network'
}

export async function getRechargeOptions(): Promise<RechargeOptionsState> {
  const reply = await request('/api/client/recharge/options', { method: 'GET', timeoutMs: READ_TIMEOUT_MS })
  if (typeof reply === 'string') return { kind: 'unavailable', reason: reply }
  if (!reply.ok) return { kind: 'unavailable', reason: classify(reply.status, reply.body) ?? 'network' }
  const p = reply.payload
  const amounts = asArray(p['amounts']).map(asNumber).filter((n) => n > 0).sort((a, b) => a - b)
  const blocked = p['blockedReason']
  return {
    kind: 'ready',
    options: {
      currency: asString(p['currency']) || 'USD',
      amounts,
      rechargeEnabled: p['rechargeEnabled'] === true,
      canRecharge: p['canRecharge'] === true,
      blockedReason: blocked === 'role' || blocked === 'wallet' ? blocked : null
    }
  }
}

export async function createRechargeCheckout(amount: number): Promise<RechargeCheckoutResult> {
  if (!Number.isFinite(amount) || amount <= 0) return { kind: 'rejected', message: null }
  const reply = await request('/api/client/recharge/checkout', { method: 'POST', body: { amount }, timeoutMs: CHECKOUT_TIMEOUT_MS })
  if (typeof reply === 'string') return { kind: 'unavailable', reason: reply }
  if (!reply.ok) {
    const reason = classify(reply.status, reply.body)
    return reason === null ? { kind: 'rejected', message: asString(reply.body['message']) } : { kind: 'unavailable', reason }
  }
  const orderNo = asString(reply.payload['orderNo'])
  const checkoutUrl = asString(reply.payload['checkoutUrl'])
  if (orderNo === '' || !isStripeCheckout(checkoutUrl)) {
    console.warn('[recharge] checkout 返回了不可用的订单或收银地址')
    return { kind: 'rejected', message: null }
  }
  try {
    await shell.openExternal(checkoutUrl)
  } catch (error) {
    console.warn('[recharge] 打开 Stripe 收银页失败:', error)
    return { kind: 'rejected', message: null }
  }
  return {
    kind: 'opened',
    orderNo,
    amount: asNumber(reply.payload['amount']) || amount,
    currency: asString(reply.payload['currency']) || 'USD'
  }
}

export async function getRechargeOrder(orderNo: string): Promise<RechargeOrderState> {
  // 订单号是服务端生成的 `RC` + 日期 + hex；限定字符集，免得拼进路径的是任意串。
  if (!/^[A-Za-z0-9]{1,64}$/.test(orderNo)) return { kind: 'not-found' }
  const reply = await request(`/api/client/recharge/orders/${orderNo}`, { method: 'GET', timeoutMs: READ_TIMEOUT_MS })
  if (typeof reply === 'string') return { kind: 'unavailable', reason: reply }
  if (!reply.ok) {
    // 404 在这里有两种含义：路由不存在（服务端没部署）或订单不可见。前者在拿到订单号之前
    // 就已经被 checkout 挡住了 —— 能走到轮询说明路由在，所以这里的 404 一律是「订单不可见」。
    if (reply.status === 404) return { kind: 'not-found' }
    return { kind: 'unavailable', reason: classify(reply.status, reply.body) ?? 'network' }
  }
  const status = asString(reply.payload['status'])
  return {
    kind: 'ready',
    order: {
      orderNo: asString(reply.payload['orderNo']) || orderNo,
      status: status in ORDER_STATUSES ? status as RechargeOrderStatus : 'Pending',
      amount: asNumber(reply.payload['amount']),
      currency: asString(reply.payload['currency']) || 'USD'
    }
  }
}

/** 错误回复常常没有 JSON body（网关 502 之类）；解析失败按 null 处理，由 `classify` 归到可重试。 */
async function readJson(response: Response): Promise<unknown> {
  try {
    return await response.json() as unknown
  } catch {
    return null
  }
}

function isStripeCheckout(value: string): boolean {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && url.hostname === 'checkout.stripe.com'
  } catch {
    return false
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

/** decimal 可能被序列化成字符串，两种都收。 */
function asNumber(value: unknown): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value)
    if (Number.isFinite(parsed)) return parsed
  }
  return 0
}
