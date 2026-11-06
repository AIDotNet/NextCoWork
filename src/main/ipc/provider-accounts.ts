/**
 * 供应商账号的 IPC —— **元数据(库)+ 登录态摘要(密文)在这里合成一份下发。**
 *
 * ## 为了什么需求建的
 *
 * 一家 OAuth 供应商下面挂多个登录身份(schema 第 24 条)。设置页要显示的每一行
 * 需要两个来源的信息:账号行(顺序、停用、限流到几点、额度快照)在
 * `provider_accounts` 表里,而邮箱 / 套餐 / 过没过期只存在于**密文**里。
 * 这个文件是唯一把两者拼起来的地方。
 *
 * ## 它拥有哪条不变式
 *
 * **下发的 `ProviderAccount` 里一个 token 字符都没有。** 和 `CredentialInfo`
 * 同一条规矩(`shared/domain/provider.ts` 的文件头):明文只经
 * `provider:revealCredential` 单次返回,列表刷新与广播永不携带。
 *
 * ## 故意不做什么
 *
 * - **不挑账号**。「这一刻该用哪个」归 `AccountPool`(`kernel/upstream/account-pool.ts`),
 *   那是请求热路径上的事;这里只服务界面。
 * - **不自己判限流到期**。到期与否由纯函数按主进程时钟算(`isAccountLimited`),
 *   这里只把 `limit.until` 原样下发,格式化成「还有 12 分钟」是渲染层的事。
 */
import type { CredentialInfo } from '../../shared/domain/provider'
import { bearerOf, parseCredential } from '../../shared/domain/credential'
import type { ProviderAccount } from '../../shared/domain/provider-account'
import { providerAccountCredentialRef } from '../../shared/domain/provider-account'
import { findPreset } from '../../shared/domain/presets'
import {
  codingPlanQuotaErrorMessage,
  codingPlanQuotaRequest,
  parseCodingPlanQuota
} from '../kernel/upstream/coding-plan-quota'
import { codingPlanFamilyFor } from '../../shared/domain/coding-plan'
import type { ProviderAccountRow } from '../db/provider-accounts'
import { ensureSeeded, getHost } from '../runtime'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { userAgent } from '../kernel/user-agent'
import { redactSecrets } from '../kernel/upstream/model-list'
import { IpcError } from './errors'
import { infoFor } from './provider'

/** 这家支持账号登录吗。★ 判据是**预设的 `oauthIssuer`**,和渲染层那条完全同源 */
function requireOAuthProvider(providerId: string): void {
  ensureSeeded()
  const provider = store.listProviders().find((p) => p.id === providerId)
  if (provider === undefined) throw new IpcError('unknown', `没有这个供应商:${providerId}`)
  if (findPreset(providerId)?.oauthIssuer === undefined) {
    throw new IpcError('unknown', `供应商「${provider.name}」不支持账号登录`)
  }
}

/**
 * 账号行 + 它那条凭证的摘要。
 *
 * ★ 读密文失败(主密钥损坏、文件被删)时 `auth` 缺席,**其余字段照常下发**:
 * 账号行还在,用户至少看得到「这里有个账号、点重新登录」。整条抛错的话,
 * 一条坏凭证会让整家供应商的账号列表在界面上凭空消失。
 */
async function withAuth(row: ProviderAccountRow): Promise<ProviderAccount> {
  let info: CredentialInfo | null
  try {
    info = infoFor(await getHost().secrets.get(providerAccountCredentialRef(row.providerId, row.id)))
  } catch {
    info = null
  }
  const { createdAt: _createdAt, updatedAt: _updatedAt, ...account } = row
  return {
    ...account,
    ...(info?.auth === undefined ? {} : { auth: info.auth })
  }
}

/**
 * 这家的账号列表(界面用的完整形态)。
 *
 * ★ 顺带把**反范式的 `needsReauth` 同步回账号行**:真值住在密文里,而账号池
 * 在热路径上只读行(见 `schema.ts` 第 24 条)。设置页每次打开都会走这里,
 * 于是那一列不会长期落后于凭证。
 */
export async function listProviderAccounts(providerId: string): Promise<ProviderAccount[]> {
  requireOAuthProvider(providerId)
  const rows = store.listProviderAccounts(providerId)
  const accounts = await Promise.all(rows.map((row) => withAuth(row)))
  for (const account of accounts) {
    const fromCredential = account.auth?.needsReauth
    // ★ 摘要拿不到时(读密文失败)**不动**那一列:把它写成 false 等于用一次
    //   读取失败抹掉一条真实的「登录已失效」。
    if (fromCredential === undefined) continue
    if (fromCredential !== account.needsReauth) {
      store.setProviderAccountNeedsReauth(account.id, fromCredential)
      account.needsReauth = fromCredential
    }
  }
  return accounts
}

/** 落库之后把整份列表推给可能开着设置页的窗口。★ 写频道也回它,两边同一个形状 */
export async function announceAccounts(providerId: string): Promise<ProviderAccount[]> {
  const accounts = await listProviderAccounts(providerId)
  windows.emitToAll('provider:accountsChanged', { providerId, accounts })
  return accounts
}

/**
 * `AccountPool` 落闸/解闸之后的广播口(由 `ipc/index.ts` 注入进 runtime)。
 *
 * ★ 这条路径上**不能抛**:它挂在一次正在跑的上游请求后面,而一次广播失败
 * 不该让用户的对话断掉。
 */
export function announceAccountsSafe(providerId: string): void {
  void announceAccounts(providerId).catch(() => {
    /* 设置页下次打开会自己拉一份;这次广播丢了不影响那条请求 */
  })
}

function requireAccount(providerId: string, accountId: string): ProviderAccountRow {
  requireOAuthProvider(providerId)
  const row = store.getProviderAccount(accountId)
  if (row === undefined || row.providerId !== providerId) {
    throw new IpcError('unknown', '这个账号不存在，可能已经在另一个窗口里被删掉了')
  }
  return row
}

export async function removeProviderAccount(
  providerId: string,
  accountId: string
): Promise<ProviderAccount[]> {
  const row = requireAccount(providerId, accountId)
  store.removeProviderAccount(row.id)
  /*
    ★★ 删掉的是当前账号时,镜像槽要跟着换人。不换的话旧槽里留着一条**已经没有
    账号行指向它**的凭证:界面上这家显示「还有 2 个账号」,而回退旧版本、或者
    任何走 `provider.credentialRef` 的旁路(三个辅助请求)用的还是被删掉那个。
  */
  await syncLegacyMirror(providerId)
  return announceAccounts(providerId)
}

export async function setProviderAccountEnabled(
  providerId: string,
  accountId: string,
  enabled: boolean
): Promise<ProviderAccount[]> {
  const row = requireAccount(providerId, accountId)
  store.setProviderAccountEnabled(row.id, enabled)
  return announceAccounts(providerId)
}

export async function setProviderAccountLabel(
  providerId: string,
  accountId: string,
  label: string
): Promise<ProviderAccount[]> {
  const row = requireAccount(providerId, accountId)
  store.setProviderAccountLabel(row.id, label)
  return announceAccounts(providerId)
}

export async function setCurrentProviderAccount(
  providerId: string,
  accountId: string
): Promise<ProviderAccount[]> {
  const row = requireAccount(providerId, accountId)
  store.setCurrentProviderAccount(providerId, row.id)
  await syncLegacyMirror(providerId)
  return announceAccounts(providerId)
}

/**
 * 拖拽排序。
 *
 * ★ 入参里没出现的账号**排在后面**,而不是被删掉或者顺序归零:两个窗口同时开着
 * 设置页时,另一个窗口刚加的账号不在这一份 `accountIds` 里 —— 按「没出现就归零」
 * 处理会把它悄悄插到队首。
 */
export async function reorderProviderAccounts(
  providerId: string,
  accountIds: readonly string[]
): Promise<ProviderAccount[]> {
  requireOAuthProvider(providerId)
  const known = new Set(store.listProviderAccounts(providerId).map((row) => row.id))
  const ordered = accountIds.filter((id) => known.has(id))
  const rest = [...known].filter((id) => !ordered.includes(id))
  store.setProviderAccountOrder(providerId, [...ordered, ...rest])
  return announceAccounts(providerId)
}

/**
 * 「立即解除限流」。
 *
 * ★ 用户点它的场景有两个:他刚升级了套餐(额度真的回来了),或者我们判早了。
 * 两种都只有他知道 —— 所以这里不做任何"确认一下是不是真的恢复了"的校验,
 * 那需要发一次请求,而那次请求本身可能又撞一个 429 并把闸门重新立起来。
 */
export async function clearProviderAccountLimit(
  providerId: string,
  accountId: string
): Promise<ProviderAccount[]> {
  const row = requireAccount(providerId, accountId)
  store.setProviderAccountLimit(row.id, null)
  return announceAccounts(providerId)
}

/**
 * 主动拉一次这个账号的订阅额度(GLM Coding Plan),快照落进账号行。
 *
 * ## 为了什么需求建的
 *
 * GLM Coding Plan 的对话响应**不带**额度头,`codex-quota.ts` 那条搭便车的路
 * 在这家不存在 —— 「还剩多少」只能显式问上游。入口是两处界面的「刷新额度」
 * (设置页账号行、聊天圆环菜单),不自动轮询:查询本身不耗套餐额度,但一个
 * 后台定时器会把「看一眼额度」变成一个永远在跑的网络面。
 *
 * ## 它依赖谁
 *
 * 鉴权头就是这把账号凭证(`bearerOf`):OAuth 登录的 `accessToken` 本来就是
 * 登录第四跳供应出来的**真 API Key**(`issuers/zcode-bigmodel.ts` 的
 * `apiKeyProvision`),粘贴的订阅 key 更是 —— 两种凭证在这里同构。
 */
export async function fetchProviderAccountQuota(
  providerId: string,
  accountId: string
): Promise<ProviderAccount[]> {
  const row = requireAccount(providerId, accountId)
  const provider = store.listProviders().find((p) => p.id === providerId)
  const family = codingPlanFamilyFor(providerId)
  if (family === null || provider === undefined) {
    throw new IpcError('unknown', `供应商「${provider?.name ?? providerId}」不支持订阅额度查询`)
  }
  const raw = await getHost().secrets.get(providerAccountCredentialRef(providerId, row.id))
  const credential = parseCredential(raw)
  if (credential === null) {
    throw new IpcError('unknown', '这个账号还没有可用的订阅密钥，请先登录或填入 API Key')
  }
  const request = codingPlanQuotaRequest(family, bearerOf(credential))

  let res: Response
  try {
    /*
      ★ 必须有超时(同 `ipc/provider.ts` 的 fetchModels):一个不回包的地址会让
      「刷新额度」的按钮永远转下去,用户唯一的出路是关掉应用。额度是个轻查询,
      15 秒(ZCode 同款)足够慢网络,又短到不至于让人以为卡死了。
    */
    res = await getHost().fetch(request.url, {
      headers: { ...request.headers, 'user-agent': userAgent() },
      signal: AbortSignal.timeout(15_000)
    })
  } catch (e) {
    const reason = e instanceof Error && e.name === 'TimeoutError' ? '超时(15 秒)' : redactSecrets(String(e))
    throw new IpcError('unknown', `连不上 ${request.url} —— ${reason}`)
  }

  if (!res.ok) {
    // ★ body 必须读完,否则连接不会被释放(和 `ipc/provider.ts` 那处同一个理由)
    const text = await res.text().catch(() => '')
    throw new IpcError('unknown', codingPlanQuotaErrorMessage(res.status, text))
  }

  // ★ body 先读成文本再解析:解析失败时要把**原文**记进日志,否则「格式变了」
  //   和「账号没开通」在界面上是同一句话(2026-09-29 CREDIT_LIMIT 事故的教训)。
  //   读完文本也顺带释放连接(同上面 `!res.ok` 那条的理由)。
  const body = await res.text().catch(() => '')
  let payload: unknown = null
  try {
    payload = JSON.parse(body) as unknown
  } catch {
    /* 保持 null —— parseCodingPlanQuota 会把它归为 unrecognized,原文在下面日志里 */
  }
  const parsed = parseCodingPlanQuota(payload, Date.now())
  if (parsed.kind === 'unrecognized') {
    console.error(`[coding-plan-quota] 额度响应无法识别(${parsed.detail}):`, body.slice(0, 800))
    throw new IpcError('unknown', '额度接口的响应无法识别，可能是上游改了格式')
  }
  if (parsed.kind === 'no-quota') {
    /*
      ★ 「信封合法但没有可展示的窗口」**不是失败**(同 ZCode 的 noQuotaLimits):
      未开通套餐、或窗口字段不够画,界面回落到各自的空态。这里**不写库** ——
      没有可写的快照,而把上一次的旧数清掉需要一个专门的清空通路;留着它 + 界面
      已有的「N 小时前的数据」标注,比新开一条写路径更诚实。
    */
    console.warn(`[coding-plan-quota] 账号没有可展示的额度(provider=${providerId}): ${parsed.detail}`)
    return announceAccounts(providerId)
  }
  store.setProviderAccountQuota(row.id, parsed.snapshot)
  return announceAccounts(providerId)
}

/**
 * 把**当前账号**的凭证镜像回旧槽 `provider:<id>`。
 *
 * ## ★★ 这个函数是「回退旧版本仍然能用」那条承诺的全部实现
 *
 * 旧版本(以及三个辅助请求那几个不接账号池的 `UpstreamRouter`)只认
 * `provider:<id>`。多账号上线之后,那个槽的语义从「唯一的凭证」变成
 * 「当前账号的一份副本」。不同步的表现分两种,都很难查:
 * - 降级之后显示**未登录**(槽里是空的);
 * - 或者更糟:槽里留着**上一个**账号的 token,于是辅助请求一直在用一个
 *   用户以为已经删掉的账号。
 *
 * ★ **全仓库只有这个函数写 `provider:<id>`**(登录/退出/删号/切换当前账号
 * 四条路径都调它)。多一处写入点就多一处会忘记同步的地方。
 *
 * ★ 一个账号都没有时**清空**旧槽:那正是「这家已经退出登录了」。
 */
export async function syncLegacyMirror(providerId: string): Promise<void> {
  const provider = store.listProviders().find((p) => p.id === providerId)
  if (provider === undefined) return
  const current = store.currentProviderAccount(providerId)
  const host = getHost()
  if (current === undefined) {
    // ★ 走 secrets.remove 而不是 set('') —— 删密文不需要主密钥(同 `signOut`)
    await host.secrets.remove?.(provider.credentialRef)
    return
  }
  const raw = await host.secrets.get(providerAccountCredentialRef(providerId, current.id))
  if (raw === null || raw === '') return
  // 已经一模一样就不写:这条路径在每次 token 刷新后都会被调到,而一次
  // 无谓的写入会连带一次云同步脏标记
  const existing = await host.secrets.get(provider.credentialRef)
  if (existing === raw) return
  await host.secrets.set(provider.credentialRef, raw)
}

/**
 * 凭证变了(登录成功、刷新成功、刷新被拒)之后的同步点。
 *
 * ★★ **这是那个反范式列 `needs_reauth` 的唯一写入点**(见 `schema.ts` 第 24 条)。
 * 漏掉的表现是:一条已经失效的登录永远不会被账号池跳过,于是每一次请求都挑中它、
 * 每一次都是同一个 401,而界面上那个红色「登录已失效」标记**确实是对的** ——
 * 两处看起来都没错,但请求就是发不出去。
 */
export async function syncAccountCredentialState(
  providerId: string,
  accountId: string
): Promise<void> {
  const raw = await getHost().secrets.get(providerAccountCredentialRef(providerId, accountId))
  const credential = parseCredential(raw)
  if (credential === null || credential.kind !== 'oauth') return
  store.setProviderAccountNeedsReauth(accountId, credential.needsReauth === true)
  await syncLegacyMirror(providerId)
}
