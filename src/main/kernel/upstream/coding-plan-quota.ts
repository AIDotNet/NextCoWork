/**
 * GLM Coding Plan(智谱 / Z.AI 订阅)的额度查询 —— **主动拉取**的那一份。
 *
 * ## 为了什么需求建的
 *
 * `codex-quota.ts` 那份额度是「响应头搭便车」,但 GLM Coding Plan 的对话响应
 * **没有**带额度头,搭不了 —— 用户要看「5 小时 / 每周还剩多少」就必须显式
 * 发一次查询。产品形状和 Codex 那条保持同一句话:不后台轮询,只在用户点
 * 「刷新额度」时发;代价是数据会旧,界面照 `capturedAt` 说明。
 *
 * ## 数据源(2026-09-29 逆向 ZCode.app v3.11 定案)
 *
 * ```
 * GET {https://bigmodel.cn | https://api.z.ai}/api/monitor/usage/quota/limit
 * Authorization: <订阅 API Key>          ← 裸值,ZCode 的 createBigModelUsageHeaders
 *                                          原样放 key,不加 Bearer
 * → {"success":true,"code":0|200,"data":{"level":"glm-coding-pro","limits":[…]}}
 * 401 → {"code":1001,"msg":"Header中未收到Authorization参数…","success":false}
 *       (2026-09-29 无鉴权探针实测)
 * ```
 *
 * `limits[]` 的挑选规则照抄 ZCode 渲染层(`styles-*.js` 的 `JH` / `Bft`),
 * 它们是**服务端的窗口编号**,不要按字面猜:
 * - 5 小时窗口:`type ∈ {TOKENS_LIMIT, CREDIT_LIMIT} && unit===3 && number===5`
 * - 每周窗口:  `type ∈ {TOKENS_LIMIT, CREDIT_LIMIT} && unit===6`
 *
 * ★★ **type 必须认 `CREDIT_LIMIT`**,这不是防御性放宽:ZCode 的 `Bft` 把
 * `TOKENS_LIMIT` 和 `CREDIT_LIMIT` 判成同一类「token 型」额度,而 BigModel 的
 * 套餐是**积分制** —— 2026-09-29 真实账号实测,响应里的窗口正是 `CREDIT_LIMIT`,
 * 只认 `TOKENS_LIMIT` 的第一版把两条窗口全丢了,界面报「响应无法识别」。
 * `TIME_LIMIT`(工具月额度)是另一类,仍不认(理由见下)。
 *
 * `percentage` 字段是**已用**百分比(ZCode 的剩余 % = 100 - percentage)。
 *
 * ## 故意不做什么
 *
 * - **不认 `TIME_LIMIT` / 其它 unit 的窗口**。ZCode 还有一条「MCP 工具月额度」
 *   (`TIME_LIMIT`,unit 5 number 1),但把它塞进 `ProviderQuotaWindow` 需要编造一个
 *   `windowMinutes`(界面按 300 / 10080 认 5 小时 / 每周),而「编一个看起来精确的数」
 *   比「不显示」更糟。将来要加,先给 `ProviderQuotaWindow` 加槽位判别,不是硬塞。
 * - **不碰 ZCode 自有的 MCP 额度**(`/api/v1/mcp/usage`):那条要 ZCode 自家
 *   OAuth 的 JWT,我们的凭证体系里没有它,不做一个必然 404 的分支。
 * - **纯函数,不发请求**:入参只有 `(family, key)` / `(payload, now)`,
 *   IO 在 `ipc/provider-accounts.ts`,和 `model-list.ts` 的分工一样。
 */
import type { CodingPlanFamily } from '../../../shared/domain/coding-plan'
import { codingPlanQuotaUrl } from '../../../shared/domain/coding-plan'
import type { ProviderQuotaSnapshot, ProviderQuotaWindow } from '../../../shared/domain/provider-account'

/**
 * 三种解析结局 —— **「没买套餐」不是错误**。
 *
 * ZCode 把「信封合法但没有任何可展示的窗口」当成正常态渲染
 * (`chat.planUsage.noQuotaLimits`:「接口暂未返回可展示的额度项」),
 * 只有信封本身读不懂才算失败。第一版把两者合成一个 null,症状是:
 * 未开通套餐的账号点刷新弹「可能是上游改了格式」,把用户往排查格式错误的路上带。
 * `detail` 是给日志看的现场(见到的 type / 缺的字段),不进用户界面。
 */
export type CodingPlanQuotaParse =
  | { kind: 'ok'; snapshot: ProviderQuotaSnapshot }
  | { kind: 'no-quota'; detail: string }
  | { kind: 'unrecognized'; detail: string }

/**
 * 一条额度窗口请求。★ 头只有 `authorization` 一个:多发的头(ZCode 还带
 * `bigmodel-organization` 等)是**团队套餐**才需要的,个人订阅发了反而多一处
 * 和上游行为绑定的面。
 */
export interface CodingPlanQuotaRequest {
  url: string
  headers: Record<string, string>
}

/** 「这家是哪一家的套餐」的判定在 `shared/domain/coding-plan.ts`,这里只管形状 */
export function codingPlanQuotaRequest(
  family: CodingPlanFamily,
  apiKey: string
): CodingPlanQuotaRequest {
  return {
    url: codingPlanQuotaUrl(family),
    headers: { authorization: apiKey }
  }
}

/** 数值夹紧:上游报过 >100 的读数(超额宽限),进度条会溢出圆角 */
function clampPercent(n: number): number {
  return Math.max(0, Math.min(100, n))
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

/**
 * 从一条 limit 里算**已用**百分比。
 *
 * ★ 先信 `percentage`(上游直接给的已用 %),缺了再用 `remaining / number` 反推
 *   —— 两条路都没有就返回 `null`,**绝不编 0**:`0` 是「这个窗口一点没用过」,
 *   而一个刚重置完的窗口和一个读不懂的窗口在界面上必须是两个东西
 *   (同 `codex-quota.ts` 的「三个字段同进同出」)。
 */
function usedPercentOf(limit: Record<string, unknown>): number | null {
  const percentage = finiteNumber(limit['percentage'])
  if (percentage !== null) return clampPercent(percentage)
  const remaining = finiteNumber(limit['remaining'])
  const total = finiteNumber(limit['number'])
  if (remaining !== null && total !== null && total > 0) {
    return clampPercent(100 - (remaining / total) * 100)
  }
  return null
}

/**
 * 三样凑不齐就整条窗口不要 —— 见 codex-quota.ts 那条「同进同出」的完整理由。
 * ★ `nextResetTime` 上游给的就是**绝对毫秒**(ZCode 原样 `new Date(value)`),
 *   不需要像 Codex 那样拿 `now` 折算,所以这里不收时钟。
 */
function windowFrom(
  limit: Record<string, unknown>,
  windowMinutes: 300 | 10_080
): ProviderQuotaWindow | null {
  const usedPercent = usedPercentOf(limit)
  const nextResetTime = finiteNumber(limit['nextResetTime'])
  if (usedPercent === null || nextResetTime === null || nextResetTime <= 0) return null
  return { usedPercent, windowMinutes, resetsAt: nextResetTime }
}

/**
 * 「token 型」额度窗口的 type 集合 —— **逐字对应 ZCode 渲染层的 `Bft`/`Rft`**:
 * `TOKENS_LIMIT`(Z.AI 用)和 `CREDIT_LIMIT`(BigModel 积分制用)在 ZCode 里
 * 是同一个匹配类,查谁就看整个集合,不是相等判断。照抄一处事实,不自创第三个集合。
 */
const TOKEN_LIKE_TYPES: ReadonlySet<string> = new Set(['TOKENS_LIMIT', 'CREDIT_LIMIT'])

/** 一条 limit 的诊断摘要:log 里能看出「见到了什么、缺了什么」,不出用户界面 */
function limitDigest(raw: unknown): string {
  if (typeof raw !== 'object' || raw === null) return String(typeof raw)
  const limit = raw as Record<string, unknown>
  const keys = Object.keys(limit).slice(0, 12).join(',')
  return `type=${String(limit['type'])} unit=${String(limit['unit'])} number=${String(limit['number'])} percentage=${String(limit['percentage'])} nextResetTime=${String(limit['nextResetTime'])} keys=[${keys}]`
}

/**
 * 解析额度响应,三种结局(见 `CodingPlanQuotaParse` 的注释)。
 *
 * 信封判定照 ZCode 的 `isSuccessfulBigModelEnvelope`:`success !== false` 且
 * `code` 缺席 / 0 / 200 都算成功 —— 两家的监控 API 混用 0 和 200,只认一个的
 * 表现是「有一家永远显示获取失败」。
 */
export function parseCodingPlanQuota(payload: unknown, now: number): CodingPlanQuotaParse {
  if (typeof payload !== 'object' || payload === null) {
    return { kind: 'unrecognized', detail: `响应不是 JSON 对象(${typeof payload})` }
  }
  const envelope = payload as Record<string, unknown>
  if (envelope['success'] === false) {
    return { kind: 'unrecognized', detail: `上游拒绝:${String(envelope['msg'] ?? '')}` }
  }
  const code = envelope['code']
  if (code !== undefined && code !== 0 && code !== 200) {
    return { kind: 'unrecognized', detail: `业务错误 code=${String(code)} msg=${String(envelope['msg'] ?? '')}` }
  }

  const data = envelope['data']
  if (typeof data !== 'object' || data === null) {
    return { kind: 'no-quota', detail: 'data 缺席(可能这个账号没有有效编程套餐)' }
  }
  const rawLimits = (data as Record<string, unknown>)['limits']
  if (!Array.isArray(rawLimits)) {
    return { kind: 'no-quota', detail: 'data.limits 缺席' }
  }

  let fiveHour: ProviderQuotaWindow | null = null
  let weekly: ProviderQuotaWindow | null = null
  for (const raw of rawLimits) {
    if (typeof raw !== 'object' || raw === null) continue
    const limit = raw as Record<string, unknown>
    if (typeof limit['type'] !== 'string' || !TOKEN_LIKE_TYPES.has(limit['type'])) continue
    if (fiveHour === null && limit['unit'] === 3 && limit['number'] === 5) {
      fiveHour = windowFrom(limit, 300)
      continue
    }
    if (weekly === null && limit['unit'] === 6) {
      weekly = windowFrom(limit, 10_080)
    }
  }
  if (fiveHour === null && weekly === null) {
    /*
      ★ limits 在场却一条都没匹配上,大概率是「没有套餐」或「窗口字段不够画」
      —— 两者界面都是空态,但**必须把见到的东西记下来**:这就是「上游改了格式」
      和「账号本来就没开通」在日志里唯一能区分开的方式(2026-09-29 那次
      CREDIT_LIMIT 事故,日志里一条 type 都没有,只能重新解包 ZCode 才定位)。
    */
    return { kind: 'no-quota', detail: `${rawLimits.length} 条窗口无一可用: ${rawLimits.map(limitDigest).join(' | ').slice(0, 600)}` }
  }
  return {
    kind: 'ok',
    snapshot: {
      ...(fiveHour === null ? {} : { primary: fiveHour }),
      ...(weekly === null ? {} : { secondary: weekly }),
      capturedAt: now
    }
  }
}

/**
 * 非 2xx / 网络失败时给人看的句子。★ 只翻「我们知道的」:body 里上游的原话
 * (`msg`)经常带请求 id 一类的排障信息,拼在后面,别替它概括。
 */
export function codingPlanQuotaErrorMessage(status: number, body: string): string {
  if (status === 401) return '订阅密钥无效或已过期，请重新登录或更换 API Key'
  const brief = body.trim().slice(0, 200)
  return brief === '' ? `额度接口返回了 ${status}` : `额度接口返回了 ${status}:${brief}`
}
