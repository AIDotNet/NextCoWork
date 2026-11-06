/**
 * AccountPool —— **「现在该用这家的哪个账号」唯一的回答者。**
 *
 * ## 为了什么需求建的
 *
 * 一家 OAuth 供应商下面挂着多个登录身份(`db/provider-accounts.ts`),而额度是
 * 按账号算的。这个类把三件事收在一处:挑账号、把 429/配额耗尽翻译成一次
 * 「这个账号先别用」的落闸、以及闸门到期后自动放行。
 *
 * ## 它拥有哪条不变式
 *
 * **router 不自己判断账号可用性。** 它只问 `select()` 和 `reportFailure()`。
 * 两边各写一份判据的表现是:界面(读同一套 `shared/domain/provider-account.ts`
 * 纯函数)说账号 A 可用,请求却发给了 B —— 和 `model-selection.ts` 当年那个
 * 「药丸说 A、请求发给 B」同一个形状,同样零报错。
 *
 * ## 故意不做什么
 *
 * - **不发请求、不读密文、不碰 Electron。** 全部外部依赖收在 `AccountPoolPort` 里,
 *   于是一个假 Port 就能直测(`__tests__/account-pool.test.ts`)。
 * - **不算退避时长以外的重试策略。** 「这条流还要不要重试」仍归 router;
 *   这里只回答「这个账号还能不能用」。两者粒度不同:前者是一次请求的事,
 *   后者对**所有并发流**同时生效。
 */
import type { AgentError } from '../../../shared/agent/error'
import type {
  ProviderAccount,
  ProviderAccountLimit,
  ProviderQuotaSnapshot
} from '../../../shared/domain/provider-account'
import {
  earliestRecoveryAt,
  mergeLimit,
  nextUsableAccount,
  selectAccount
} from '../../../shared/domain/provider-account'

/**
 * 上游没给恢复时间时,账号被停用多久。
 *
 * ★★ **刻意不复用 `router.ts` 的 `DEFAULT_RATE_LIMIT_FLOOR_MS`(4 秒)。**
 * 那个数回答的是「这条流退避多久再重发」,量级是秒;这里回答的是「这个账号
 * 停用多久」,量级是分钟。共用一个常量的表现是:账号被禁用 4 秒又立刻撞回去,
 * 界面上那句「限流中」一闪而过,看起来像是功能没生效 —— 而真正的配额窗口
 * 根本没过去。
 *
 * ★ 5 分钟是**猜的下限**,不是任何一家的窗口长度。真实窗口有两个更好的来源:
 * 上游的 `Retry-After`(已在 `error.retryAfterMs` 里)和 Codex 的额度快照
 * (`reportQuota` 用 `resetsAt`)。这个数只在两者都没有时兜底。
 */
export const DEFAULT_ACCOUNT_LIMIT_MS = 5 * 60_000

/**
 * 配额类错误的词表 —— **用于把 402/403 认成「额度用尽」而不是「密钥无效」。**
 *
 * ★ 需求:上游对「订阅额度跑完」的回法并不统一。429 是最常见的,但也有家回
 * 403 + 一句 `quota exceeded`。不认的表现是:账号被判成 `auth` 错误,整个 run
 * 终止并让用户去重新登录 —— 而他的登录完全没问题,过一小时就自己好了。
 *
 * ★ 只在 402/403 上查这张表。在 401 上查是危险的:一个真正失效的 token
 * 被认成「限流」之后,界面会说「1 小时后恢复」,而它永远不会恢复。
 */
const QUOTA_PHRASES = [
  'quota',
  'insufficient_quota',
  'usage limit',
  'usage_limit',
  'rate limit',
  'exceeded your current',
  'out of credits',
  'credit balance'
] as const

const QUOTA_STATUSES = new Set([402, 403])

/** 这次失败是不是「这个账号的额度没了」。★ 判据是**已分类的 AgentError**,不是原始响应 */
export function isAccountQuotaFailure(error: AgentError): boolean {
  if (error.code === 'rate_limit') return true
  if (error.status === undefined || !QUOTA_STATUSES.has(error.status)) return false
  const message = error.message.toLowerCase()
  return QUOTA_PHRASES.some((phrase) => message.includes(phrase))
}

/**
 * 池子对外部世界的全部依赖。
 *
 * ★ `onChanged` 是**注入回调**,和 router 的 `onCredentialChanged` / `onUsageAttempt`
 * 同一个套路:内核拿不到窗口,而设置页要立刻看到「限流中」那个徽章亮起来。
 */
export interface AccountPoolPort {
  /** 这家的账号,**带登录态摘要**(可用性判定要读 `needsReauth`) */
  list(providerId: string): ProviderAccount[]
  setLimit(accountId: string, limit: ProviderAccountLimit | null): void
  setQuota(accountId: string, quota: ProviderQuotaSnapshot): void
  now(): number
  /** 账号轮换开关(设置 › 模型)。每次读,改完立刻对下一条请求生效 */
  rotationEnabled(): boolean
  onChanged?(providerId: string): void
}

export class AccountPool {
  constructor(private readonly port: AccountPoolPort) {}

  /**
   * 这一刻该用哪个账号。**`null` = 这家没有账号表(纯 API Key / 还没登录)**,
   * 调用方据此回落到 `provider.credentialRef` —— 那条路径逐字节等于多账号上线之前。
   *
   * ★ 全部账号都不可用时也返回 `null`,两者在 router 那边的处理不同:
   * 后者要报「全部账号限流」而不是「还没有配置密钥」,所以用 `hasAccounts()` 区分。
   */
  select(providerId: string): ProviderAccount | null {
    return selectAccount(this.port.list(providerId), this.port.now(), this.port.rotationEnabled())
  }

  /** 这家配过账号吗。★ 用来区分「没登录」和「全被限流」——两句话要说的完全不同 */
  hasAccounts(providerId: string): boolean {
    return this.port.list(providerId).length > 0
  }

  /**
   * 这次失败构不构成「换个账号」。构成就落闸并返回下一个可用账号。
   *
   * ★★ **落闸在「这条流还重不重试」之前发生**,和 router 里 `rateLimitGate` 那条
   * 注释是同一个道理:这次尝试可能是本条流的最后一次,但对**其它并发流**来说
   * 这个账号的额度照样是空的,那一句「先别用它」依旧成立。
   *
   * @returns 下一个可用账号;`null` = 这家没有别的账号可换了(或这次失败与额度无关)
   */
  reportFailure(account: ProviderAccount, error: AgentError): ProviderAccount | null {
    if (!isAccountQuotaFailure(error)) return null
    const now = this.port.now()
    const until = now + (error.retryAfterMs ?? DEFAULT_ACCOUNT_LIMIT_MS)
    this.applyLimit(account, {
      until,
      since: now,
      source: 'http-429',
      reason: error.message
    })
    /*
      ★★ **关掉轮换时落闸照落,但绝不返回下一个账号。**

      落闸是为了让界面说得出「为什么失败」——用户关掉的是自动换号,不是知情权。
      返回下一个账号则等于替他做了他刚明确拒绝的那件事。

      ★ 这一行还挡着一个**死循环**:router 把「换到了新账号」当成「这次不算重试」
      (换号不消耗 attempt),而轮换关掉时 `select()` 恒返回当前那一个 ——
      于是「失败 → 说能换 → 换出来还是同一个 → 再失败」会永远转下去。
      表现是整个 run 卡住、CPU 打满、一条错误都不输出(2026-09-22 被
      `router-accounts.test.ts` 的「关掉轮换后不换号」那条用例逮到,
      当场把 vitest worker 跑成 SIGABRT)。
    */
    if (!this.port.rotationEnabled()) return null
    return nextUsableAccount(this.port.list(account.providerId), now, account.id)
  }

  /**
   * 这个账号刚刚成功发出去一条请求。
   *
   * ★ 成功 = 配额确实回来了,清闸。留着的话后面那几条流会白等一场
   * (和 `router.recordSuccess` 里 `rateLimitGate.delete` 是同一条理由)。
   *
   * ★ **手动停用的那条闸门不清**:用户自己关的,只有用户能开(见 `mergeLimit`)。
   */
  reportSuccess(account: ProviderAccount): void {
    if (account.limit === undefined) return
    if (account.limit.source === 'manual') return
    this.port.setLimit(account.id, null)
    this.port.onChanged?.(account.providerId)
  }

  /**
   * 额度快照回来了(今天只有 Codex 有)。
   *
   * ★★ 跑满就**提前落闸到重置时刻**(产品决策 D12),不等下一次必然失败的 429。
   * 不这么做的表现是:额度条已经显示 100%,用户点发送,等了几秒拿到一个 429,
   * 然后才切号 —— 那几秒和那次失败完全可以避免。
   */
  reportQuota(account: ProviderAccount, quota: ProviderQuotaSnapshot): void {
    this.port.setQuota(account.id, quota)
    const now = this.port.now()
    const exhausted = [quota.primary, quota.secondary]
      .filter((w) => w !== undefined && w.usedPercent >= 100 && w.resetsAt > now)
      /* 两个窗口都跑满时取更晚的那个 —— 早的那个到了也还是发不出去 */
      .reduce<number | null>((latest, w) => Math.max(latest ?? 0, w!.resetsAt), null)
    if (exhausted === null) {
      this.port.onChanged?.(account.providerId)
      return
    }
    this.applyLimit(account, {
      until: exhausted,
      since: now,
      source: 'quota-exhausted',
      reason: ''
    })
  }

  /** 全被限流时最早什么时候能再试。给 router 拼那句「最早 HH:MM 恢复」用 */
  earliestRecoveryAt(providerId: string): number | null {
    return earliestRecoveryAt(this.port.list(providerId), this.port.now())
  }

  /**
   * ★ 合并语义(取更晚的那个)在这里、不在 SQL 旁边:它是一个**判断**,
   * 而判断要能被直测。理由与症状见 `shared/domain/provider-account.ts` 的 `mergeLimit`。
   */
  private applyLimit(account: ProviderAccount, incoming: ProviderAccountLimit): void {
    const merged = mergeLimit(account.limit, incoming)
    // 合并之后没变(旧闸门更晚)就不写库 —— 省的不是一次 UPDATE,
    // 是一次会让设置页整列表重渲的广播
    if (account.limit !== undefined && merged === account.limit) return
    this.port.setLimit(account.id, merged)
    this.port.onChanged?.(account.providerId)
  }
}
