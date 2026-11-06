/**
 * 供应商**账号** —— 一家 OAuth 供应商下面挂的那几个登录身份。
 *
 * ## 为了什么需求建的
 *
 * Codex / Kimi / GLM 这些按订阅计费的家,额度是**按账号**算的:一个账号被限流,
 * 另一个账号的额度还在。在这个文件存在之前,一家 provider 只有一个凭证槽
 * (`shared/domain/credential.ts` 文件头那条不变式),于是「限流了就换个号接着干」
 * 这件事在数据模型上根本表达不出来 —— 用户唯一的办法是手动退出登录再登另一个,
 * 而限流恢复之后还要记得手动换回来。
 *
 * ## 它拥有哪条不变式
 *
 * **「现在该用哪个账号」只能由这里的纯函数回答。** 选择规则(顺序、可用性、
 * 限流到期)是一份**可直测的纯逻辑**,主进程的 `AccountPool` 和渲染层的账号列表
 * 读的是同一段代码。两边各写一遍的话,症状是「界面显示账号 A 可用,请求却发给了 B」
 * —— 和 `model-selection.ts` 当年那个「药丸说 A、请求发给 B」一模一样,且同样零报错。
 *
 * ## 故意不做什么
 *
 * - **不碰密文。** 这里只有 ref 的派生规则(`providerAccountCredentialRef`),
 *   token 本身一如既往只存在 `credentials` 表 + `host.secrets` 那条路上。
 * - **不翻译。** 显示名的回落链返回的是 `{ kind, text }`,句子由调用方翻
 *   (§6 规则 5:不翻译领域值,只翻它们周围的话)。
 * - **不读时钟。** 每个判定函数都收一个 `now` 参数。渲染层和主进程不是同一个时钟源,
 *   而「这个账号现在还被限流着吗」只能有一个答案(同 `CredentialAuthInfo.expired`)。
 */
import type { OAuthIssuerId } from './oauth-issuer'
import type { CredentialAuthInfo } from './provider'

/**
 * 这个账号为什么不能用、什么时候能用。
 *
 * ★ `until` 是**绝对毫秒时间戳,由主进程按 `host.clock` 算**。渲染层只拿它减一个
 * 本地 tick 画倒计时,不拿它和 `Date.now()` 比出「到底解没解除」—— 那个答案归主进程。
 */
export interface ProviderAccountLimit {
  until: number
  /** 什么时候被关进去的。界面上「已限流 23 分钟」用它,排查时也靠它对时间线 */
  since: number
  source: ProviderAccountLimitSource
  /**
   * 上游的原话(或我们的判定理由)。
   *
   * ★ 必须留着:同样是 429,「本轮 5 小时额度用尽」和「请求太频繁」对用户是
   * 完全不同的两件事,而我们的界面只画得出一句「限流中」。原话进 tooltip。
   */
  reason: string
}

/**
 * ★★ 三个来源必须分得开,因为**能不能手动解除**不一样:
 * - `http-429` / `quota-exhausted` 是上游说的,用户点「立即解除」是在赌我们判早了;
 * - `manual` 是用户自己关的,那条不该被下一次成功请求自动清掉。
 */
export type ProviderAccountLimitSource = 'http-429' | 'quota-exhausted' | 'manual'

/** 一个额度窗口(Codex 的 5 小时 / 一周各是一个)。 */
export interface ProviderQuotaWindow {
  /** 0–100。★ 解析侧已经夹紧过,这里不再防一次 */
  usedPercent: number
  /** 窗口长度(分钟)。300 ≈ 5 小时,10080 = 一周。界面据它决定标题叫什么 */
  windowMinutes: number
  /** 绝对毫秒。相对秒数在解析那一刻就折掉了(同 `OAuthCredential.expiresAt` 那条规矩) */
  resetsAt: number
}

/**
 * 额度快照 —— **搭便车搭到的那一份,不是实时值。**
 *
 * ★ `capturedAt` 不是装饰:这份数据只在用户发消息时更新(产品决策 D6,不主动探针、
 * 不后台轮询),所以界面必须说得出「这是什么时候的数」。少了它,一个三天没用过的
 * 账号会显示着三天前的 12%,看起来像是实时的。
 */
export interface ProviderQuotaSnapshot {
  primary?: ProviderQuotaWindow
  secondary?: ProviderQuotaWindow
  capturedAt: number
}

export interface ProviderAccount {
  /**
   * ★★ 本机生成的 ULID,**不是上游的账号 id**。
   *
   * 上游那个(ChatGPT 的 `chatgpt-account-id`)住在凭证里(`OAuthCredential.accountId`),
   * 发请求时要塞进头。两者混用的表现是一个 403,而错误信息里不会提到任何一个头名。
   */
  id: string
  providerId: string
  issuer: OAuthIssuerId
  /** 用户自己起的备注名。缺失时按 `accountDisplay` 的回落链显示 */
  label?: string
  /** 轮换顺序,小的先用。拖拽排序写回的就是它 */
  order: number
  /** 用户手动停用。★ 停用 ≠ 限流:前者不会自己恢复,界面上也是两个徽章 */
  enabled: boolean
  /** 是不是「当前账号」—— 决定镜像回旧槽的是谁,**不参与**轮换选择(见 `selectAccount`) */
  current: boolean
  /**
   * 刷新被上游明确拒过(`invalid_grant`),要用户重新登录。
   *
   * ★★ **这是从凭证里反范式出来的一个位,不是 `auth.needsReauth` 的别名。**
   * 「这一刻用哪个账号」发生在请求热路径上、必须同步,而凭证在密文里、解密是异步的。
   * 所以真值由 `ipc/provider-auth.ts` 在凭证每次变化时写进账号行,这里读的是那一份;
   * `auth` 那份只是展示用,可能在极短时间内与它不一致。
   * **判定一律以这个字段为准** —— 两处都读的话会出现「界面说要重登、请求照发」。
   */
  needsReauth: boolean
  /**
   * 登录态摘要。形状复用 `CredentialAuthInfo`:同一件事没有第二套类型,
   * 于是主进程那边算 `expired` / `needsReauth` 的代码一行都不用改。
   *
   * ★ **可以缺席**:账号池在热路径上拿不到它(见上面那条),而可用性判定
   * 不依赖它 —— 依赖的话,池子选出来的账号会永远是"不可用"。
   */
  auth?: CredentialAuthInfo
  limit?: ProviderAccountLimit
  /** 只有 `issuer === 'chatgpt'` 会有。其余家恒 undefined,界面据此不画额度条 */
  quota?: ProviderQuotaSnapshot
}

/**
 * 账号密文的 ref。
 *
 * ★★ 形状是 `provider:<id>#<accountId>` —— **前缀仍然是 `provider:`**,这不是巧合:
 * `db/repo.ts` 的 `putCredential` / `removeCredential` 按这个前缀决定要不要给云同步
 * 打脏标记,`config-profile.physicalCredentialRef` 按 `\u0000` 加账户作用域前缀
 * (两者不冲突,`#` 是普通字符)。换一个前缀等于把这两处的行为静默改掉,
 * 而表现是「多账号的登录态不进云同步」——一个要到换台机器才会发现的问题。
 *
 * ★ 和 `providerCredentialRef`(`provider.ts`)是同一族派生规则,只是多一段账号。
 * 旧 ref `provider:<id>` 继续存在,装的是**当前账号的镜像**(见 `ipc/provider-auth.ts`
 * 的 `syncLegacyMirror`)。
 */
export function providerAccountCredentialRef(providerId: string, accountId: string): string {
  return `provider:${providerId}#${accountId}`
}

/**
 * 从账号 ref 反查 providerId。**认不出返回 null。**
 *
 * ★ 调用点是凭证刷新之后的广播(`ipc/provider-auth.ts` 的 `announceCredentialRef`):
 * 那条回调拿到的是 ref,而要广播给渲染层的是 providerId。
 *
 * ★ 必须用 `indexOf('#')` 而不是 `split('#')[1]`:provider id 本身不含 `#`
 * (预设表和 `custom-provider.ts` 都只用字母数字和连字符),但**万一**将来有人
 * 放宽了那条规则,按第一个 `#` 切至少还能切对 providerId 那一半。
 */
export function parseAccountCredentialRef(
  ref: string
): { providerId: string; accountId: string } | null {
  if (!ref.startsWith('provider:')) return null
  const rest = ref.slice('provider:'.length)
  const hash = rest.indexOf('#')
  if (hash <= 0 || hash === rest.length - 1) return null
  return { providerId: rest.slice(0, hash), accountId: rest.slice(hash + 1) }
}

/** 这个账号的密文 ref。写成函数是为了让调用点不必自己拼(拼错的表现是「登录成功、发消息说没配置密钥」) */
export function accountCredentialRef(account: Pick<ProviderAccount, 'providerId' | 'id'>): string {
  return providerAccountCredentialRef(account.providerId, account.id)
}

/**
 * 界面上这个账号叫什么 —— **返回来源 + 原文,不返回句子。**
 *
 * ★ `kind` 交给调用方选 i18n key:`unknown` 那一档要翻成「未命名账号」,
 * 而其余三档是**领域值**(用户起的名、邮箱、上游 id),一个字都不该翻(§6 规则 5)。
 */
export function accountDisplay(
  account: Pick<ProviderAccount, 'label' | 'auth'>
): { kind: 'label' | 'email' | 'id' | 'unknown'; text: string } {
  const label = account.label
  if (label !== undefined && label.trim() !== '') return { kind: 'label', text: label.trim() }
  const email = account.auth?.email
  if (email !== undefined && email !== '') return { kind: 'email', text: email }
  const accountId = account.auth?.accountId
  /*
    ★ 只取尾 6 位。上游的 account id 是一长串,整条显示会把一行挤爆,而它在界面上
    唯一的用处是「区分这两个没有邮箱的账号」—— 尾段足够。
  */
  if (accountId !== undefined && accountId !== '') {
    return { kind: 'id', text: accountId.length > 6 ? accountId.slice(-6) : accountId }
  }
  return { kind: 'unknown', text: '' }
}

/** 这个账号此刻被限流着吗。★ 到期即自动为假 —— 解除不需要任何人去「解」它 */
export function isAccountLimited(account: Pick<ProviderAccount, 'limit'>, now: number): boolean {
  const limit = account.limit
  return limit !== undefined && limit.until > now
}

/**
 * 能不能拿这个账号去发请求。
 *
 * 三条判据,**每一条的恢复方式都不一样**,所以不能合成一个布尔字段存库:
 * - `enabled === false`:用户手动关的,只有用户能开;
 * - `needsReauth`:上游明确拒过刷新,只有重新登录能救(不会自己好,所以也不落闸);
 * - 限流未到期:时间到了自己就好。
 *
 * ★ 读的是账号行上那个反范式的 `needsReauth`,**不是 `auth?.needsReauth`** ——
 * 后者在热路径上根本拿不到(要解密),照它判的话池子会认为所有账号都不可用,
 * 表现是「配了账号却总说还没有配置密钥」。
 */
export function isAccountUsable(
  account: Pick<ProviderAccount, 'enabled' | 'needsReauth' | 'limit'>,
  now: number
): boolean {
  if (!account.enabled) return false
  if (account.needsReauth) return false
  return !isAccountLimited(account, now)
}

/** 轮换顺序:`order` 升序,并列时按 id 稳定排 —— 否则拖拽之后顺序会随读取顺序抖 */
export function sortAccounts(accounts: readonly ProviderAccount[]): ProviderAccount[] {
  return [...accounts].sort((a, b) => (a.order - b.order) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
}

/**
 * 这一刻该用哪个账号。**固定顺序 + 跳过不可用**(产品决策 D2)。
 *
 * ★★ **不看 `current`。** 「当前账号」是用户的一个显式选择,它决定的是镜像回旧槽
 * 的是谁;而限流之后轮到别人头上时,那个选择**不该被悄悄改掉** —— 否则用户会发现
 * 自己设的当前账号莫名其妙变成了另一个,而他从没点过。
 *
 * ★ `rotation === false`(用户关掉了账号轮换)时只认 `current`,失败也不切:
 * 那是他要的「就用这一个」。此时仍然返回它,哪怕它正被限流 —— 让那次请求带着
 * 真实的上游错误失败,比悄悄换一个号更符合他的预期。
 */
export function selectAccount(
  accounts: readonly ProviderAccount[],
  now: number,
  rotation = true
): ProviderAccount | null {
  const sorted = sortAccounts(accounts)
  if (sorted.length === 0) return null
  if (!rotation) {
    const current = sorted.find((a) => a.current) ?? sorted[0]
    return current ?? null
  }
  return sorted.find((a) => isAccountUsable(a, now)) ?? null
}

/**
 * 当前这个不行了,下一个是谁。
 *
 * ★ 从**整张表**重新挑而不是「取 order 比它大的第一个」:被跳过的那个账号可能
 * 排在后面(比如用户刚把它拖到末尾),而限流恢复之后靠前的那个应该立刻被选回来。
 * 传 `exclude` 只是为了在它的闸门刚落下、数据还没回读时不把它选出来。
 */
export function nextUsableAccount(
  accounts: readonly ProviderAccount[],
  now: number,
  excludeId: string
): ProviderAccount | null {
  return sortAccounts(accounts).find((a) => a.id !== excludeId && isAccountUsable(a, now)) ?? null
}

/**
 * 全被限流时,最早什么时候能再试。**`null` = 没有任何一个账号在等时间**
 * (都停用了、或者都要重新登录 —— 那两种等下去也不会好)。
 *
 * 用途是 D11 那句错误文案:「全部账号都在限流中,最早 HH:MM 恢复」。
 * 少了这个数,用户看到的是一句「所有供应商都不可用」,而他完全不知道该等多久。
 */
export function earliestRecoveryAt(accounts: readonly ProviderAccount[], now: number): number | null {
  let earliest: number | null = null
  for (const account of accounts) {
    if (!account.enabled) continue
    if (account.needsReauth) continue
    if (!isAccountLimited(account, now)) continue
    const until = account.limit?.until
    if (until === undefined) continue
    if (earliest === null || until < earliest) earliest = until
  }
  return earliest
}

/**
 * 两个闸门取**更晚**的那个。
 *
 * ★★ 需求:两条并发的流可能在同一秒内各自吃到一个 429,而它们拿到的
 * `Retry-After` 不一定一样(上游按不同窗口回)。后写的直接覆盖的表现是:
 * 先落的「1 小时后恢复」被后落的「5 分钟后恢复」盖掉,5 分钟后全体一起再撞一次,
 * 而这一次撞出来的往往是更长的冷却 —— 我们自己把冷却期越拖越长。
 *
 * ★ `manual`(用户手动停的)不参与这个合并:它由用户显式解除,不该被一次 429 续期。
 */
export function mergeLimit(
  existing: ProviderAccountLimit | undefined,
  incoming: ProviderAccountLimit
): ProviderAccountLimit {
  if (existing === undefined) return incoming
  if (existing.source === 'manual') return existing
  return existing.until >= incoming.until ? existing : incoming
}
