/**
 * `provider_accounts` 表的读写 —— **多账号的持久化收口**(schema 第 24 条)。
 *
 * ## 为了什么需求建的
 *
 * 一家 OAuth 供应商下面要挂多个登录身份,而「这个账号排第几、被限流到什么时候、
 * 额度还剩多少」这些是**元数据**,不是密文。密文照旧走 `credentials` 表
 * (`repo.putCredential` / `getCredential` / `removeCredential`),由
 * `providerAccountCredentialRef()` 派生出来的 ref 指过去。
 *
 * ## 它拥有哪条不变式
 *
 * **删账号 = 删行 + 删密文,在同一个事务里。** 只删行的表现是库里留下一条
 * 永远不会再被读到的密文(孤儿 token),而用户以为自己已经退出登录了。
 *
 * ## 为什么不写进 `db/repo.ts`
 *
 * 那个文件已经 3600 行(§15.3)。这一族函数自成一节、只被账号相关的三处调用,
 * 往里堆只会把它再往不可拆推一步。这里只从 repo 借两样东西:凭证删除和
 * 云同步脏标记 —— repo **不反向 import 这个文件**,所以没有循环。
 */
import { findPreset } from '../../shared/domain/presets'
import { parseCredential } from '../../shared/domain/credential'
import type {
  ProviderAccountLimit,
  ProviderAccountLimitSource,
  ProviderQuotaSnapshot
} from '../../shared/domain/provider-account'
import { providerAccountCredentialRef } from '../../shared/domain/provider-account'
import type { OAuthIssuerId } from '../../shared/domain/oauth-issuer'
import { OAUTH_ISSUER_IDS } from '../../shared/domain/oauth-issuer'
import type { UpstreamProvider } from '../../shared/domain/provider'
import { ulid } from '../../shared/util/id'
import { stmt, tx } from './index'
import { enqueueSyncMutation, removeCredential } from './repo'

/**
 * 库里那一行。
 *
 * ★ **没有 `auth` 字段** —— 登录态摘要(邮箱、套餐、过期没有)要解密凭证才知道,
 * 那是 `ipc/provider-accounts.ts` 在组装下发数据时补的。存一份进这张表的话,
 * 它会和凭证里的真值**慢慢分叉**:token 刷新改的是凭证,这张表不会跟着动,
 * 于是界面上那个邮箱停留在几个月前登录时的值。
 */
export interface ProviderAccountRow {
  id: string
  providerId: string
  issuer: OAuthIssuerId
  label?: string
  order: number
  enabled: boolean
  current: boolean
  /** 见 `schema.ts` 第 24 条里那条注释:从密文反范式出来的一个位 */
  needsReauth: boolean
  limit?: ProviderAccountLimit
  quota?: ProviderQuotaSnapshot
  createdAt: number
  updatedAt: number
}

function isIssuer(value: unknown): value is OAuthIssuerId {
  return OAUTH_ISSUER_IDS.includes(value as OAuthIssuerId)
}

/**
 * ★ 限流那四列**同进同出**:`limit_until` 为 NULL 即「没被限流」,此时其余三列
 * 无论存着什么都不该被读出来。分开判的话,一条只剩 `limit_reason` 的脏行会变成
 * 一个「没有到期时间的限流」—— 界面画出永不结束的倒计时。
 */
function limitFromRow(row: Record<string, unknown>): ProviderAccountLimit | undefined {
  const until = row['limit_until']
  if (until === null || until === undefined) return undefined
  const source = String(row['limit_source'] ?? 'http-429')
  return {
    until: Number(until),
    since: Number(row['limit_since'] ?? until),
    source: (['http-429', 'quota-exhausted', 'manual'] as const).includes(
      source as ProviderAccountLimitSource
    )
      ? (source as ProviderAccountLimitSource)
      : 'http-429',
    reason: String(row['limit_reason'] ?? '')
  }
}

/**
 * ★ 解析失败一律当「没有额度快照」,不抛。这一列是我们自己写进去的 JSON,
 * 但它同时也是**将来要改形状**的那一个(Codex 的头名还没实测定死,§11),
 * 而一个解不开的快照绝不该让整个账号列表读不出来 —— 那会表现为「登录态全没了」。
 */
function quotaFromRow(row: Record<string, unknown>): ProviderQuotaSnapshot | undefined {
  const raw = row['quota_json']
  if (raw === null || raw === undefined) return undefined
  try {
    const parsed: unknown = JSON.parse(String(raw))
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return undefined
    return parsed as ProviderQuotaSnapshot
  } catch {
    return undefined
  }
}

function rowToAccount(row: Record<string, unknown>): ProviderAccountRow | null {
  const issuer = String(row['issuer'])
  // 认不出 issuer 的行发不出请求也画不出界面(登录按钮的名字查的是穷尽表)。
  // 跳过而不是抛:一行坏数据不该让整家供应商的账号列表读不出来。
  if (!isIssuer(issuer)) return null
  const label = row['label']
  const limit = limitFromRow(row)
  const quota = quotaFromRow(row)
  return {
    id: String(row['id']),
    providerId: String(row['provider_id']),
    issuer,
    ...(label === null || label === undefined || String(label) === ''
      ? {}
      : { label: String(label) }),
    order: Number(row['sort_order'] ?? 0),
    enabled: Number(row['enabled'] ?? 1) !== 0,
    current: Number(row['is_current'] ?? 0) !== 0,
    needsReauth: Number(row['needs_reauth'] ?? 0) !== 0,
    ...(limit === undefined ? {} : { limit }),
    ...(quota === undefined ? {} : { quota }),
    createdAt: Number(row['created_at'] ?? 0),
    updatedAt: Number(row['updated_at'] ?? 0)
  }
}

/** 账号列表的写入点都要打一次脏标记,否则多账号登录态换台机器就没了(D10)。 */
function markDirty(providerId: string): void {
  enqueueSyncMutation('provider', providerId, {})
}

/** 按轮换顺序。`sort_order` 并列时按 id —— 顺序必须稳定,否则拖拽之后会抖。 */
export function listProviderAccounts(providerId?: string): ProviderAccountRow[] {
  const rows =
    providerId === undefined
      ? stmt('SELECT * FROM provider_accounts ORDER BY provider_id, sort_order, id').all()
      : stmt('SELECT * FROM provider_accounts WHERE provider_id = ? ORDER BY sort_order, id').all(
          providerId
        )
  return rows
    .map((row) => rowToAccount(row as Record<string, unknown>))
    .filter((row): row is ProviderAccountRow => row !== null)
}

export function getProviderAccount(id: string): ProviderAccountRow | undefined {
  const row = stmt('SELECT * FROM provider_accounts WHERE id = ?').get(id)
  if (row === undefined) return undefined
  return rowToAccount(row as Record<string, unknown>) ?? undefined
}

export function putProviderAccount(account: ProviderAccountRow): ProviderAccountRow {
  tx(() => {
    stmt(
      `INSERT INTO provider_accounts (
         id, provider_id, issuer, label, sort_order, enabled, is_current, needs_reauth,
         limit_until, limit_since, limit_source, limit_reason, quota_json,
         created_at, updated_at
       ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT (id) DO UPDATE SET
         provider_id = excluded.provider_id,
         issuer = excluded.issuer,
         label = excluded.label,
         sort_order = excluded.sort_order,
         enabled = excluded.enabled,
         is_current = excluded.is_current,
         needs_reauth = excluded.needs_reauth,
         limit_until = excluded.limit_until,
         limit_since = excluded.limit_since,
         limit_source = excluded.limit_source,
         limit_reason = excluded.limit_reason,
         quota_json = excluded.quota_json,
         updated_at = excluded.updated_at`
    ).run(
      account.id,
      account.providerId,
      account.issuer,
      account.label ?? null,
      account.order,
      account.enabled ? 1 : 0,
      account.current ? 1 : 0,
      account.needsReauth ? 1 : 0,
      account.limit?.until ?? null,
      account.limit?.since ?? null,
      account.limit?.source ?? null,
      account.limit?.reason ?? null,
      account.quota === undefined ? null : JSON.stringify(account.quota),
      account.createdAt,
      account.updatedAt
    )
    markDirty(account.providerId)
  })
  return account
}

/**
 * 删账号。★ **连密文一起删,同一个事务。** 见文件头那条不变式。
 *
 * ★ 走 `removeCredential` 而不是 `secrets.set(ref, '')`:后者要读主密钥、做一次
 * 无意义的加密,主密钥文件损坏时会让「退出登录」这个动作本身失败。
 * (和 `ipc/provider.ts` 的 `removeProvider`、`ipc/provider-auth.ts` 的 `signOut`
 * 是同一条理由。)
 */
export function removeProviderAccount(id: string): void {
  const account = getProviderAccount(id)
  if (account === undefined) return
  tx(() => {
    stmt('DELETE FROM provider_accounts WHERE id = ?').run(id)
    removeCredential(providerAccountCredentialRef(account.providerId, account.id))
    markDirty(account.providerId)
  })
}

/** 删掉一家供应商的全部账号(供应商本身被删时)。孤儿密文同样一起收。 */
export function removeProviderAccountsFor(providerId: string): void {
  tx(() => {
    for (const account of listProviderAccounts(providerId)) {
      stmt('DELETE FROM provider_accounts WHERE id = ?').run(account.id)
      removeCredential(providerAccountCredentialRef(providerId, account.id))
    }
    markDirty(providerId)
  })
}

/** 新账号排在最后。空表时是 0。 */
export function nextProviderAccountOrder(providerId: string): number {
  const rows = listProviderAccounts(providerId)
  return rows.reduce((max, row) => Math.max(max, row.order + 1), 0)
}

/**
 * 拖拽排序:**整批写、一个事务**。
 *
 * ★ 和 `repo.putSearchProviders` 同一条理由:半新半旧的顺序会让「先用哪个账号」
 * 在两次读取之间变来变去,而那时用户已经松手了,界面却还在抖。
 */
export function setProviderAccountOrder(providerId: string, accountIds: readonly string[]): void {
  const now = Date.now()
  tx(() => {
    accountIds.forEach((id, index) => {
      stmt('UPDATE provider_accounts SET sort_order = ?, updated_at = ? WHERE id = ? AND provider_id = ?')
        .run(index, now, id, providerId)
    })
    markDirty(providerId)
  })
}

export function setProviderAccountEnabled(id: string, enabled: boolean): void {
  const account = getProviderAccount(id)
  if (account === undefined) return
  tx(() => {
    stmt('UPDATE provider_accounts SET enabled = ?, updated_at = ? WHERE id = ?')
      .run(enabled ? 1 : 0, Date.now(), id)
    markDirty(account.providerId)
  })
}

/**
 * 同步「这条凭证是不是被上游拒了」。
 *
 * ★ 唯一的写入点是 `ipc/provider-auth.ts` 的 announce —— 它在凭证每次变化时
 * (登录成功、刷新成功、刷新被拒)解一次密文并把结果写回这一列。
 * 见 `schema.ts` 第 24 条里对这个反范式列的说明。
 *
 * ★ 值没变就不写:这条路径在每次 token 刷新后都会被调到,而一次无谓的 UPDATE
 * 会连带一次云同步脏标记和一次设置页重渲。
 */
export function setProviderAccountNeedsReauth(id: string, needsReauth: boolean): void {
  const account = getProviderAccount(id)
  if (account === undefined || account.needsReauth === needsReauth) return
  tx(() => {
    stmt('UPDATE provider_accounts SET needs_reauth = ?, updated_at = ? WHERE id = ?')
      .run(needsReauth ? 1 : 0, Date.now(), id)
    markDirty(account.providerId)
  })
}

/** 备注名。空串 = 清掉备注,回落到邮箱/上游 id(见 `accountDisplay`) */
export function setProviderAccountLabel(id: string, label: string): void {
  const account = getProviderAccount(id)
  if (account === undefined) return
  const trimmed = label.trim()
  tx(() => {
    stmt('UPDATE provider_accounts SET label = ?, updated_at = ? WHERE id = ?')
      .run(trimmed === '' ? null : trimmed, Date.now(), id)
    markDirty(account.providerId)
  })
}

/**
 * 落闸 / 解闸。`limit === null` = 解除。
 *
 * ★ **合并语义(取更晚的那个)不在这里做**,在 `AccountPool` 里 —— 这一层只负责
 * 把算好的结果写下去。理由:合并要读「现在的闸门」,而那是一个**判断**,
 * 判断放在能直测的纯函数里(`mergeLimit`),不放在 SQL 旁边。
 */
export function setProviderAccountLimit(id: string, limit: ProviderAccountLimit | null): void {
  const account = getProviderAccount(id)
  if (account === undefined) return
  tx(() => {
    stmt(
      `UPDATE provider_accounts
          SET limit_until = ?, limit_since = ?, limit_source = ?, limit_reason = ?, updated_at = ?
        WHERE id = ?`
    ).run(
      limit?.until ?? null,
      limit?.since ?? null,
      limit?.source ?? null,
      limit?.reason ?? null,
      Date.now(),
      id
    )
    markDirty(account.providerId)
  })
}

export function setProviderAccountQuota(id: string, quota: ProviderQuotaSnapshot): void {
  const account = getProviderAccount(id)
  if (account === undefined) return
  tx(() => {
    stmt('UPDATE provider_accounts SET quota_json = ?, updated_at = ? WHERE id = ?')
      .run(JSON.stringify(quota), Date.now(), id)
    markDirty(account.providerId)
  })
}

/**
 * 「当前账号」是**每家供应商唯一的**一个,所以先全清再置一个,同一个事务。
 *
 * ★ 两条 UPDATE 分开跑的话,中间那一刻是「这家一个当前账号都没有」——
 * 而 `syncLegacyMirror` 恰好在这时读到的话,会把旧槽清空:表现是回退旧版本后
 * 显示未登录,且只在极窄的时间窗内复现。
 */
export function setCurrentProviderAccount(providerId: string, accountId: string): void {
  const now = Date.now()
  tx(() => {
    stmt('UPDATE provider_accounts SET is_current = 0, updated_at = ? WHERE provider_id = ?')
      .run(now, providerId)
    stmt('UPDATE provider_accounts SET is_current = 1, updated_at = ? WHERE id = ? AND provider_id = ?')
      .run(now, accountId, providerId)
    markDirty(providerId)
  })
}

/**
 * 这家的「当前账号」。都没标时取顺序最靠前的那个。
 *
 * ★ 回落是必须的:删掉当前账号之后,表里会短暂地一个 `is_current` 都没有,
 * 而镜像槽、界面那个「当前」标都要有个答案。返回 undefined 的话,表现是
 * 旧槽被清空 —— 用户明明还有两个账号,回退旧版本却是未登录。
 */
export function currentProviderAccount(providerId: string): ProviderAccountRow | undefined {
  const rows = listProviderAccounts(providerId)
  return rows.find((row) => row.current) ?? rows[0]
}

/** 存量迁移要读写密文,而 db 层不认识 `host.secrets` —— 所以是注入的两个函数。 */
export interface AccountSeedSecrets {
  get(ref: string): Promise<string | null>
  set(ref: string, value: string): Promise<void>
}

/**
 * 把「升级之前已经登录的那一个账号」迁成账号 #1。
 *
 * ## 为什么不在迁移 SQL 里做
 *
 * SQL 读不到密文,判不出 `provider:<id>` 那一行装的是 OAuth 凭证还是一把 API Key。
 * 把 API Key 供应商也迁成「账号」会让它的界面整个换成账号列表 —— 而它只有一把 key。
 *
 * ## 幂等靠「这家已经有账号行就跳过」,不靠迁移版本号
 *
 * 版本号只在升级那一刻跑一次,管不到「用户是升级之后才第一次登录的」——
 * 那种情况下登录路径自己会建账号行,这里跳过即可。
 *
 * ## ★★ 旧槽 `provider:<id>` **保留不删**
 *
 * 它从此是「当前账号的镜像」(产品决策 D9):用户回退到旧版本时,那个版本只认
 * 旧槽,读得到就还是已登录状态。删掉的表现是**降级之后显示未登录**,
 * 而用户完全无法把这件事和"升级过一次"联系起来。
 *
 * @returns 这次新建了几条账号行(0 = 无事可做)
 */
export async function ensureProviderAccountsSeeded(
  providers: readonly UpstreamProvider[],
  secrets: AccountSeedSecrets,
  now: number
): Promise<number> {
  let created = 0
  for (const provider of providers) {
    const issuer = findPreset(provider.id)?.oauthIssuer
    // 这家根本不支持账号登录 —— API Key 供应商在这里原地不动,一个字节都不改
    if (issuer === undefined) continue
    if (listProviderAccounts(provider.id).length > 0) continue

    const raw = await secrets.get(provider.credentialRef)
    const credential = parseCredential(raw)
    // 没登录过、或者槽里装的是 API Key(GLM 那两家两种凭证都能用)—— 都不迁
    if (credential === null || credential.kind !== 'oauth') continue

    const id = ulid(now)
    /*
      ★ 先写密文再写行。反过来的话,中间崩一次会留下一条**指向空密文的账号行**,
      而它在界面上显示为「已登录」,发请求却报「还没有配置密钥」。
      反向的残留(有密文没有行)则是良性的:下次启动这里会再迁一遍。
    */
    await secrets.set(providerAccountCredentialRef(provider.id, id), raw ?? '')
    putProviderAccount({
      id,
      providerId: provider.id,
      issuer: credential.issuer,
      order: 0,
      enabled: true,
      current: true,
      // ★ 迁移要把凭证里那个位一起带过来:一条已经失效的登录迁过来之后
      //   如果标成"正常",池子会一直挑中它,而每次都是同一个 401。
      needsReauth: credential.needsReauth === true,
      createdAt: now,
      updatedAt: now
    })
    created += 1
  }
  return created
}
