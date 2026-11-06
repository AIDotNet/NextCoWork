/**
 * 账号登录的编排 —— **整条链路上唯一碰 Electron 的一环。**
 *
 * 流程本身(PKCE、回环服务器、换 token、提账号身份)全在 `kernel/oauth/` 和
 * `net/oauth-loopback.ts` 里,零 Electron、可直测。这里只做四件事:
 * 查规格、把 `shell.openExternal` 和 `host.fetch` 注入进去、把结果落库、广播。
 */
import { openExternalUrl } from '../services/external-opener'
import type { CredentialInfo } from '../../shared/domain/provider'
import type { OAuthCredential } from '../../shared/domain/credential'
import { parseCredential, serializeCredential } from '../../shared/domain/credential'
import type { ProviderAccount } from '../../shared/domain/provider-account'
import {
  parseAccountCredentialRef,
  providerAccountCredentialRef
} from '../../shared/domain/provider-account'
import { findPreset } from '../../shared/domain/presets'
import { ulid } from '../../shared/util/id'
import type { ProviderAccountRow } from '../db/provider-accounts'
import { removeCredential } from '../db/repo'
import {
  OAuthAbandonedError,
  OAuthFailedError,
  runOAuthFlow,
  type OAuthDeviceHint,
  type OAuthPhase
} from '../kernel/oauth/flow'
import { oauthSpecOf, type OAuthProviderSpec } from '../kernel/oauth/registry'
import { PortBusyError } from '../net/oauth-loopback'
import { ensureSeeded, getHost } from '../runtime'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { IpcError } from './errors'
import { getCredentialInfo } from './provider'
import {
  announceAccounts,
  syncAccountCredentialState,
  syncLegacyMirror
} from './provider-accounts'

interface ActiveFlow {
  providerId: string
  abort: AbortController
  /** `manual-paste` 形态下,把用户粘回来的 code 交给正在等的那个 flow */
  submitCode?: (code: string) => void
}

/**
 * ★ **单飞。** 同一时刻只允许一条登录在跑,再次发起时**先掐掉上一条**。
 *
 * 不报「已有登录正在进行中」:那句话最常见的触发原因是用户手抖点了两下按钮,
 * 而他会因此以为登录失败了。掐掉重来是他期望的行为 —— 旧的那条本来也没人在等。
 */
let active: ActiveFlow | null = null

function resolveSpec(providerId: string): { spec: OAuthProviderSpec; credentialRef: string } {
  ensureSeeded()
  const provider = store.listProviders().find((p) => p.id === providerId)
  if (provider === undefined) throw new IpcError('unknown', `没有这个供应商:${providerId}`)

  /*
    ★ 「这家走不走 OAuth」查的是**预设**,不是凭证 —— 还没登录时凭证是空的,
    照凭证判会把一家 OAuth 供应商在未登录状态下认成 API Key 供应商,
    于是界面画出一个填了也没用的密钥框。
  */
  const issuer = findPreset(providerId)?.oauthIssuer
  if (issuer === undefined) {
    throw new IpcError('unknown', `供应商「${provider.name}」不支持账号登录`)
  }
  return { spec: oauthSpecOf(issuer), credentialRef: provider.credentialRef }
}

function emitPhase(
  providerId: string,
  phase: OAuthPhase,
  extra: { message?: string; needsPastedCode?: boolean; device?: OAuthDeviceHint } = {}
): void {
  windows.emitToAll('provider:authProgress', {
    providerId,
    phase,
    ...(extra.message === undefined ? {} : { message: extra.message }),
    ...(extra.needsPastedCode === true ? { needsPastedCode: true } : {}),
    /*
      ★ 配对码只在设备码流程的 `waiting` 上有值。摊平成两个可选字段而不是嵌一层
      对象,是因为 IPC 事件的 schema 是逐字段声明的(见 `shared/ipc/contract.ts`),
      嵌套对象要多一套校验,而这里只有两个字符串。
    */
    ...(extra.device === undefined
      ? {}
      : { userCode: extra.device.userCode, verificationUri: extra.device.verificationUri })
  })
}

async function announce(providerId: string): Promise<CredentialInfo> {
  const info = await getCredentialInfo(providerId)
  windows.emitToAll('provider:authChanged', { providerId, info })
  return info
}

/**
 * 把内部异常翻译成人话。
 *
 * ★ 端口占用单独一句:它是唯一一个**用户自己能解决**的失败,而通用的
 * 「登录失败」完全不指向那个解法(去关掉正在跑的 codex login)。
 */
function translate(err: unknown): IpcError {
  if (err instanceof PortBusyError) {
    return new IpcError(
      'unknown',
      `本机 ${err.port} 端口被占用 —— Codex CLI 的登录用的是同一个端口。` +
        '请先结束正在进行的 codex login，或关掉占用该端口的程序后重试。'
    )
  }
  if (err instanceof OAuthAbandonedError) {
    /*
      ★ 这句话里**不能写死时长**。粘贴那条是 5 分钟、回环那条也是 5 分钟,而设备码
      那条的时限由上游给(Kimi 是 30 分钟)—— 写死一个数字的表现是用户等了半小时
      看到一句「授权超时（5 分钟）」,一句自相矛盾的话。
    */
    return new IpcError('unknown', err.kind === 'timeout' ? '授权超时，请重试' : '已取消登录')
  }
  if (err instanceof OAuthFailedError) return new IpcError('auth', err.message)
  return new IpcError('unknown', err instanceof Error ? err.message : String(err))
}

export async function startOAuth(providerId: string): Promise<CredentialInfo> {
  const { spec, credentialRef } = resolveSpec(providerId)
  const cred = await runFlow(providerId, spec)
  /*
    ★ 落库在广播之前。反过来的话,渲染层收到 authChanged 去查 getCredentialInfo,
    读到的还是上一次的状态 —— 一次「登录成功但界面显示未登录」的假故障。

    ★ 主密钥文件不可写时 `secrets.set` 会抛(不做明文降级),异常照常上抛翻译成人话。
  */
  await getHost().secrets.set(credentialRef, serializeCredential(cred))
  /*
    ★★ **这条老路径也要建账号行。**

    `startOAuth` 的语义没变(「把这家登录成」),但多账号上线之后,只写旧槽
    等于登录出一个**账号列表里看不见的**登录态:设置页显示「还没有账号」,
    而请求确实发得出去 —— 两个都对,合起来毫无道理。
    `upsertAccountFromLogin` 对已有同一上游账号是覆盖,所以重复登录不会多出一行。
  */
  await upsertAccountFromLogin(providerId, cred)
  return announce(providerId)
}

/**
 * 新增一个账号 / 重新登录某个账号 —— **和 `startOAuth` 共用同一条流程**。
 *
 * ★ `accountId` 给了就是「重新登录这一条」(沿用它的行与顺序),没给就是新增。
 * 两者在 OAuth 这一侧一个字节都不差,区别只在落库时写哪一行。
 */
export async function addOrReauthAccount(
  providerId: string,
  accountId?: string
): Promise<ProviderAccount[]> {
  const { spec } = resolveSpec(providerId)
  const cred = await runFlow(providerId, spec)
  await upsertAccountFromLogin(providerId, cred, accountId)
  // 老界面还在订阅 authChanged(它只认旧槽)—— 一并推,免得它停在「未登录」上
  void announce(providerId).catch(() => undefined)
  return announceAccounts(providerId)
}

/**
 * 走一遍 OAuth。**上面两条的公共部分**,抽出来是因为它们除了落库之外完全一样,
 * 而这段里有四条带 ★ 的注释(单飞、CSRF、要不要粘、设备码配对码),
 * 复制一份就等于让它们将来分叉。
 */
async function runFlow(providerId: string, spec: OAuthProviderSpec): Promise<OAuthCredential> {
  active?.abort.abort()
  const abort = new AbortController()
  const flow: ActiveFlow = { providerId, abort }
  active = flow

  try {
    return await runOAuthFlow({
      spec,
      // ★ 用 host.fetch(Electron 的 net.fetch)而不是全局 fetch:它走 Chromium
      //   网络栈,于是设置页那份代理配置对换 token 这一步一样生效
      fetch: getHost().fetch,
      now: () => getHost().clock.now(),
      /*
        ★ **不复用 `ipc/app.ts` 的 openExternal。** 那条是渲染层能直接调的通道;
        授权 URL 带着我们自己生成的 state,不该从渲染层过一遍手 —— 那等于把
        CSRF 防线交给了它。这里直接用 shell,`src/main/ipc/` 本来就允许碰 Electron。
      */
      openBrowser: (url) => openExternalUrl(url),
      /*
        ★ 「这次要不要粘」是从 `spec.grant` 现算的,不是渲染层猜的。
        渲染层拿它决定 `waiting` 阶段画输入框还是画 spinner —— 猜错的表现是
        用户对着一个永远转下去的圈,而他手里正拿着那条回调地址无处可放。

        ★ 设备码那条的 `waiting` 带着配对码一起过来(第二个参数),原样转发。
      */
      onPhase: (phase, device) =>
        emitPhase(providerId, phase, {
          needsPastedCode:
            spec.grant.kind === 'authorization-code' &&
            spec.grant.redirect.kind === 'manual-paste',
          ...(device === undefined ? {} : { device })
        }),
      signal: abort.signal,
      awaitPastedCode: () =>
        new Promise<string>((resolve) => {
          flow.submitCode = resolve
          abort.signal.addEventListener('abort', () => resolve(''), { once: true })
        })
    })
  } catch (err) {
    const translated = translate(err)
    emitPhase(providerId, err instanceof OAuthAbandonedError ? 'cancelled' : 'failed', {
      message: translated.message
    })
    throw translated
  } finally {
    if (active === flow) active = null
  }
}

/**
 * 把刚登录成功的凭证落到某一行账号上。
 *
 * ★★ **同一个上游账号重复登录要合并,不能新增一行。**
 * 判据是凭证里的 `accountId`(上游给的那个,不是我们的 ULID)。不合并的表现是:
 * 用户重新登录了一次已经失效的账号,界面上于是出现**两行同一个账号** ——
 * 两条额度条读数一模一样,他会以为自己的额度翻倍了,而实际上一个都没多。
 *
 * ★ 显式指定 `accountId`(「重新登录这条」)时按那一行写,顺序、备注名全留着。
 */
async function upsertAccountFromLogin(
  providerId: string,
  cred: OAuthCredential,
  accountId?: string
): Promise<void> {
  const rows = store.listProviderAccounts(providerId)
  const target =
    (accountId === undefined ? undefined : rows.find((row) => row.id === accountId)) ??
    (await findRowByUpstreamAccount(providerId, rows, cred))
  const now = getHost().clock.now()
  const id = target?.id ?? ulid(now)

  /*
    ★ 先写密文再写行,和 `ensureProviderAccountsSeeded` 同一条顺序、同一个理由:
    反过来崩在中间会留下一条**指向空密文的账号行**,界面显示已登录、
    发请求却报「还没有配置密钥」。
  */
  await getHost().secrets.set(providerAccountCredentialRef(providerId, id), serializeCredential(cred))
  store.putProviderAccount({
    id,
    providerId,
    issuer: cred.issuer,
    ...(target?.label === undefined ? {} : { label: target.label }),
    order: target?.order ?? store.nextProviderAccountOrder(providerId),
    enabled: target?.enabled ?? true,
    // ★ 第一个账号自动成为当前账号 —— 否则旧槽镜像空着,回退旧版本就是未登录
    current: target?.current ?? rows.length === 0,
    // 刚登录成功 = 这条凭证是好的,把上一次的「登录已失效」清掉
    needsReauth: false,
    /*
      ★★ **重新登录要把限流闸门一起清掉。**
      不清的话,用户重新登录之后账号仍然是灰的,而他刚做完的事看起来毫无效果 ——
      何况新登录的这把 token 属于同一个上游账号,配额状态由上游说了算,
      我们手里那条几分钟前的判断已经不可信了。
    */
    ...(target?.quota === undefined ? {} : { quota: target.quota }),
    createdAt: target?.createdAt ?? now,
    updatedAt: now
  })
  await syncAccountCredentialState(providerId, id)
}

/**
 * 库里有没有一行是**同一个上游账号**。
 *
 * ★ 判据要解密每一行的凭证,所以是异步的。只在登录成功那一刻跑一次
 * (一家通常两三个账号),不在任何热路径上。
 *
 * ★ 邮箱是兜底:有的家每次登录换一个 `accountId`,只按它判会每次多出一行。
 */
async function findRowByUpstreamAccount(
  providerId: string,
  rows: readonly ProviderAccountRow[],
  cred: OAuthCredential
): Promise<ProviderAccountRow | undefined> {
  for (const row of rows) {
    const existing = parseCredential(
      await getHost().secrets.get(providerAccountCredentialRef(providerId, row.id))
    )
    if (existing === null || existing.kind !== 'oauth') continue
    if (existing.accountId === cred.accountId) return row
    if (
      existing.email !== undefined &&
      cred.email !== undefined &&
      existing.email === cred.email
    ) {
      return row
    }
  }
  return undefined
}

export function cancelOAuth(providerId: string): void {
  if (active?.providerId === providerId) active.abort.abort()
}

export function submitOAuthCode(providerId: string, code: string): Promise<CredentialInfo> {
  const flow = active
  if (flow?.providerId !== providerId || flow.submitCode === undefined) {
    throw new IpcError('unknown', '当前没有等待授权码的登录流程')
  }
  flow.submitCode(code)
  // 真正的终态由那条还在跑的 startOAuth 给,这里只回一个当前快照
  return getCredentialInfo(providerId)
}

/**
 * 退出登录 —— **退出的是「当前账号」,不是这家的全部账号。**
 *
 * ★★ 语义在多账号上线时变过一次,理由:界面上那颗按钮长在当前账号那一行上,
 * 而用户配了三个号时点它显然不该把三个一起登出。配了多个号时它等价于
 * 「删掉当前这一个」,剩下的按顺序顶上来(下一个自动成为当前账号)。
 *
 * ★★ 走 `db/repo` 的 `removeCredential`,**不是** `secrets.set(ref, '')` ——
 * 后者需要读取主密钥并做一次无意义的加密,主密钥文件损坏时会让退出失败;
 * 删除现有密文不需要加密能力。(和 `removeProvider` 里那条注释是同一个理由。)
 */
export async function signOut(providerId: string): Promise<CredentialInfo> {
  const { credentialRef } = resolveSpec(providerId)
  if (active?.providerId === providerId) active.abort.abort()

  const current = store.currentProviderAccount(providerId)
  if (current !== undefined) {
    // 账号行连带它的密文一起删(`db/provider-accounts.ts` 的不变式)
    store.removeProviderAccount(current.id)
    const next = store.currentProviderAccount(providerId)
    // ★ 剩下的账号里选一个顶上,否则这家会处在「有账号但没有当前账号」的状态,
    //   而旧槽镜像那一步会据此把槽清空 —— 表现是回退旧版本后显示未登录
    if (next !== undefined) store.setCurrentProviderAccount(providerId, next.id)
    await syncLegacyMirror(providerId)
    void announceAccounts(providerId).catch(() => undefined)
    return announce(providerId)
  }

  // 一个账号行都没有 = 这家还停在多账号之前的形态(或者本来就没登录过)
  removeCredential(credentialRef)
  return announce(providerId)
}

/**
 * 刷新 token 时凭证被改写(或被标成需要重新登录)——把这件事推给可能正开着设置页的用户。
 *
 * ★ 内核里的 `CredentialResolver` 拿不到 `windows`,所以是**注入回调**,
 * 和 `onUsageAttempt` 完全同一个套路。装配在 `ipc/index.ts`。
 *
 * ★★ 多账号上线之后它还多担一件事:**把反范式的 `needs_reauth` 写回账号行**。
 * 漏掉的表现是账号池永远跳不过一条已经失效的登录 —— 每次请求都挑中它、
 * 每次都是同一个 401,而界面上那个红色标记确实是对的。
 */
export function announceCredentialRef(credentialRef: string): void {
  const parsed = parseAccountCredentialRef(credentialRef)
  if (parsed !== null) {
    void syncAccountCredentialState(parsed.providerId, parsed.accountId)
      .then(() => announceAccounts(parsed.providerId))
      .catch(() => {
        /* 广播失败不该影响那条正在跑的请求 */
      })
    // 老界面订阅的是 authChanged(它只认旧槽),一并推一条
    void announce(parsed.providerId).catch(() => undefined)
    return
  }
  const provider = store.listProviders().find((p) => p.credentialRef === credentialRef)
  if (provider === undefined) return
  void announce(provider.id).catch(() => {
    /* 广播失败不该影响那条正在跑的请求 */
  })
}
