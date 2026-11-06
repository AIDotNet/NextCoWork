/**
 * 奖励中心的数据源 —— `GET /api/client/referral` 的唯一调用点。
 *
 * 需求：左下角账户菜单的「邀请好友」以前只能 `openExternal` 跳浏览器，因为邀请码
 * 只有网页会话 Cookie 能读（桌面端令牌被 CoWork 的路径白名单挡在门外）。平台这次
 * 补了一条客户端可读的镜像接口（复用 `profile:read`），这个文件把它接进来。
 *
 * ★ **失败不抛，一律翻译成 `unavailable` 的三种原因。** 这条请求的三种失败
 * 在界面上是三句不同的话、三种不同的下一步：
 *   - 401/403 → 会话没了，要重新登录；
 *   - 404     → **桌面端比服务端先发版**（线上还没部署这条路由）。这一支必须单列：
 *               否则新客户端在老服务端上会显示「网络错误，请重试」，而重试永远不会好；
 *   - 其他    → 网络/5xx，重试有意义。
 * 抛异常的话渲染层只能拿到一句 message，上面这三件事就分不开了。
 *
 * ★ **邀请链接在这一层拼成绝对地址。** 服务端只给 `/login?ref=xxx`（它在反代后面
 * 拿不到可靠的外部地址），站点常量留在主进程 —— 渲染层再写一遍 `https://nextco.work`
 * 的话，将来换域名就会漏掉那一处，表现是用户复制出去的链接指向旧站且完全不报错。
 */
import type {
  ReferralCenter,
  ReferralInvite,
  ReferralInviteStatus,
  ReferralReward,
  ReferralRewardSide,
  ReferralState
} from '../../shared/domain/referral'
import { getHost } from '../runtime'
import { getClientAccessToken, getClientAuthState } from './client-auth'

/** 同 `ipc/skills.ts`、`ipc/plugin-market.ts`：站点地址各模块各持一份常量。 */
const API_ORIGIN = 'https://nextco.work'

/** 同 `ipc/client-auth.ts` 的 `ACCOUNT_TIMEOUT_MS`：`net.fetch` 自己没有 deadline。 */
const REFERRAL_TIMEOUT_MS = 10_000

const INVITE_STATUSES: Record<ReferralInviteStatus, true> = {
  Pending: true, Qualified: true, Rewarded: true, Rejected: true, Expired: true
}

export async function getReferralCenter(): Promise<ReferralState> {
  if (getClientAuthState().mode !== 'authenticated') return { kind: 'unavailable', reason: 'signed-out' }
  const access = await getClientAccessToken()
  if (access === null || access === '') return { kind: 'unavailable', reason: 'signed-out' }

  let response: Response
  try {
    response = await getHost().fetch(`${API_ORIGIN}/api/client/referral`, {
      headers: { Authorization: `Bearer ${access}` },
      signal: AbortSignal.timeout(REFERRAL_TIMEOUT_MS)
    })
  } catch {
    return { kind: 'unavailable', reason: 'network' }
  }
  if (response.status === 401 || response.status === 403) return { kind: 'unavailable', reason: 'signed-out' }
  if (response.status === 404) return { kind: 'unavailable', reason: 'unsupported' }
  if (!response.ok) return { kind: 'unavailable', reason: 'network' }

  try {
    const body = await response.json() as { data?: unknown } | unknown
    const payload = (isRecord(body) && isRecord(body['data']) ? body['data'] : body) as unknown
    if (!isRecord(payload)) return { kind: 'unavailable', reason: 'network' }
    return { kind: 'ready', center: toCenter(payload) }
  } catch {
    return { kind: 'unavailable', reason: 'network' }
  }
}

function toCenter(payload: Record<string, unknown>): ReferralCenter {
  const code = asString(payload['code'])
  return {
    code,
    inviteUrl: absoluteInviteUrl(asString(payload['inviteUrl']), code),
    enabled: payload['enabled'] === true,
    inviterAmount: asNumber(payload['inviterAmount']),
    inviteeAmount: asNumber(payload['inviteeAmount']),
    giftValidDays: typeof payload['giftValidDays'] === 'number' ? payload['giftValidDays'] : null,
    qualifyMinPaidCalls: asNumber(payload['qualifyMinPaidCalls']),
    totalInvites: asNumber(payload['totalInvites']),
    rewardedInvites: asNumber(payload['rewardedInvites']),
    pendingInvites: asNumber(payload['pendingInvites']),
    earnedAmount: asNumber(payload['earnedAmount']),
    // 币种缺席时兜底 USD：平台今天只有这一种计价（`PLATFORM_CURRENCY`），
    // 但这是**协议字段**，别把兜底写成「永远 USD」。
    currency: asString(payload['currency']) || 'USD',
    invites: asArray(payload['invites']).map(toInvite),
    rewards: asArray(payload['rewards']).map(toReward)
  }
}

/**
 * 服务端给的是相对路径。没给 `inviteUrl`（老版本 / 字段缺失）时用邀请码自己拼 ——
 * 拼不出来就给站点首页，**不给空串**：空串会让「复制链接」复制出一个空剪贴板，
 * 而用户以为自己复制成功了。
 */
function absoluteInviteUrl(raw: string, code: string): string {
  const path = raw !== '' ? raw : code !== '' ? `/login?ref=${encodeURIComponent(code)}` : '/'
  try {
    return new URL(path, API_ORIGIN).href
  } catch {
    return API_ORIGIN
  }
}

function toInvite(value: unknown): ReferralInvite {
  const row = isRecord(value) ? value : {}
  const status = asString(row['status'])
  return {
    name: asString(row['name']),
    status: status in INVITE_STATUSES ? status as ReferralInviteStatus : 'Pending',
    amount: asNumber(row['amount']),
    at: asString(row['at']),
    rewardedAt: typeof row['rewardedAt'] === 'string' ? row['rewardedAt'] : null
  }
}

function toReward(value: unknown): ReferralReward {
  const row = isRecord(value) ? value : {}
  const side: ReferralRewardSide = row['side'] === 'invitee' ? 'invitee' : 'inviter'
  return {
    side,
    amount: asNumber(row['amount']),
    remainingAmount: asNumber(row['remainingAmount']),
    at: asString(row['at']),
    expireAt: typeof row['expireAt'] === 'string' ? row['expireAt'] : null,
    revoked: row['revoked'] === true
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

/** 金额列可能被序列化成字符串（decimal 的常见写法），两种都收。 */
function asNumber(value: unknown): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value)
    if (Number.isFinite(parsed)) return parsed
  }
  return 0
}
