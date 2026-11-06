/**
 * 凭证解析与刷新 —— **router 和「凭证是不是还有效」之间的那一层。**
 *
 * ★ 独立出来而不是内联进 `router.ts` 的 `attempt()`:那个函数已经有三层
 * (候选循环 / 重试循环 / generator),把刷新逻辑连同它的并发去重和写回塞进去,
 * 会变成四层且没法单独测。这里只依赖 `KernelHost`,拿一个假 fetch 就能直测。
 */
import { agentError, type AgentError } from '../../../shared/agent/error'
import {
  parseCredential,
  serializeCredential,
  type OAuthCredential,
  type ProviderCredential
} from '../../../shared/domain/credential'
import { postToken } from '../oauth/flow'
import { oauthSpecOf, type OAuthIdentity, type OAuthProviderSpec } from '../oauth/registry'
import type { KernelHost } from '../host'

/**
 * 提前多久就开始刷。
 *
 * ★ 不能是 0:请求发出去要时间,一把「还有 3 秒过期」的 token 到达上游时已经死了,
 * 而那个 401 会被当成「凭证无效」而不是「该刷新了」。
 */
const SKEW_MS = 60_000

/** 刷新失败。**带着一个已经分好类的 AgentError**,router 直接把它当结果用 */
export class CredentialAuthError extends Error {
  constructor(readonly error: AgentError) {
    super(error.message)
    this.name = 'CredentialAuthError'
  }
}

/**
 * 该不该现在就刷。
 *
 * ★★ `expiresAt` 为 `null` = **不知道什么时候过期**,一律答「先别刷」。
 * 反过来把未知当成「已过期」的话,每一次请求前都会先刷一遍 —— 那不只是慢,
 * 对 refresh token 会轮换的家来说还是一条自己给自己制造并发的路。
 * 未知时真正的兜底是 401 之后的 `refreshNow`(`router.ts` 的 401 重试),
 * 那条路径覆盖的本来就是「凭证明明没过期却 401」这类情况。
 */
function expiringSoon(cred: OAuthCredential, now: number): boolean {
  return cred.expiresAt !== null && cred.expiresAt - now < SKEW_MS
}

export class CredentialResolver {
  /**
   * ★★ **这个 Map 是本文件存在的最重要理由。**
   *
   * OAuth 的 refresh token 是**轮换**的 —— 用一次就作废,响应里给一把新的。
   * 而一个带 3 个子代理的 run 会在同一毫秒发出 3 个请求:不去重的话就是 3 次
   * 并发刷新,第一次成功、后两次拿着已经作废的 refresh token 得到 `invalid_grant`。
   * 如果那两个失败也去写库,**用户会被我们自己的并发踢下线** —— 而现象是
   * 「用着用着突然要求重新登录」,查起来极痛苦。
   *
   * 按 **ref** 去重而不是 providerId:ref 才是凭证的身份。
   */
  private readonly inflight = new Map<string, Promise<OAuthCredential>>()

  constructor(
    private readonly host: KernelHost,
    private readonly onChanged?: (ref: string) => void
  ) {}

  /** 读凭证;是 OAuth 且快过期了就先刷一次。没配过返回 null(不是错误) */
  async resolve(ref: string, signal: AbortSignal): Promise<ProviderCredential | null> {
    signal.throwIfAborted()
    const cred = parseCredential(await this.host.secrets.get(ref))
    if (cred === null || cred.kind === 'api-key') return cred
    /*
      ★ 签名凭证(腾讯 TC3 / AWS SigV4)**没有可刷新的东西** —— 它是一对长期
      AccessKey/SecretKey,不存在 OAuth 那种"用一次就轮换"的续期。原样返回,
      由视频适配器那边按它重算签名。少了这一句,它会被送进 `expiringSoon`/
      `refreshOnce` 那些按 OAuth 形状写的分支,在那里读 `expiresAt` 得到
      undefined —— 而 undefined 永远不"快过期",于是表现为"看起来能过、
      但每次请求都拿一段 JSON 当 Bearer 发出去"。
    */
    if (cred.kind === 'signature') return cred
    if (!expiringSoon(cred, this.host.clock.now())) return cred
    return this.refreshOnce(ref, cred)
  }

  /**
   * 不看 `expiresAt`,强制刷一次。
   *
   * ★ 给 401 之后的那一次重试用:主动过期检查覆盖不了**本机时钟偏**和
   * **服务端主动吊销**这两种情况,而它们的表现都是一个「凭证明明没过期却 401」。
   */
  async refreshNow(ref: string, signal: AbortSignal): Promise<OAuthCredential> {
    signal.throwIfAborted()
    const cred = parseCredential(await this.host.secrets.get(ref))
    if (cred === null || cred.kind !== 'oauth') {
      throw new CredentialAuthError(
        agentError('auth', '这条凭证不是账号登录，无法刷新', { retryable: false })
      )
    }
    return this.refreshOnce(ref, cred)
  }

  /*
    ★★ **`signal` 到这里为止,不再往下传。**
    调用方的 signal 只该用来取消「等待」,不该取消一次**已经在飞的刷新**:
    刷新请求一发出去,旧的 refresh token 就被上游消耗掉了 —— 中途放弃等于
    扔掉响应里那把新的、而旧的已经作废,用户被登出,原因是我们自己取消了一次请求。
    所以下面用一个独立的超时兜底,而不是把 signal 接进去。
  */
  private refreshOnce(ref: string, cred: OAuthCredential): Promise<OAuthCredential> {
    const running = this.inflight.get(ref)
    // ★ 第二个调用者等**同一个** Promise,不发第二次请求
    if (running !== undefined) return running

    const p = this.doRefresh(ref, cred).finally(() => {
      this.inflight.delete(ref)
    })
    this.inflight.set(ref, p)
    return p
  }

  private async doRefresh(ref: string, cred: OAuthCredential): Promise<OAuthCredential> {
    const spec = oauthSpecOf(cred.issuer)

    // ★ 先取出来再判,而不是 `spec.refresh!` —— 非空断言等于把这处的正确性
    //   从编译器手里拿回自己手上,而它并不比编译器可靠
    const hook = spec.refresh
    const identity =
      hook === undefined
        ? await this.standardRefresh(ref, cred, spec)
        : await this.customRefresh(hook, cred)

    if (identity === null) {
      throw this.markReauth(ref, cred, '刷新回来的凭证不完整，请重新登录')
    }

    const next: OAuthCredential = {
      ...cred,
      accessToken: identity.accessToken,
      // ★ 轮换后的 refresh token 必须存下来。上游有的家刷新时不回新的,那就沿用旧的
      refreshToken: identity.refreshToken,
      expiresAt: identity.expiresAt,
      accountId: identity.accountId,
      ...(identity.email === undefined ? {} : { email: identity.email }),
      ...(identity.planType === undefined ? {} : { planType: identity.planType }),
      refreshedAt: this.host.clock.now()
    }
    delete next.needsReauth

    /*
      ★★ **写回必须在返回之前完成。** 轮换后的 refresh token 只存在于这一次响应里 ——
      进程如果崩在写回之前,库里那把旧的已经被上游作废了,用户下次启动直接掉线。
    */
    await this.host.secrets.set(ref, serializeCredential(next))
    this.onChanged?.(ref)
    return next
  }

  /** 标准 `grant_type=refresh_token`。**没有声明 `refresh` 钩子的家走的都是这里。** */
  private async standardRefresh(
    ref: string,
    cred: OAuthCredential,
    spec: OAuthProviderSpec
  ): Promise<OAuthIdentity | null> {
    let result: Awaited<ReturnType<typeof postToken>>
    try {
      result = await postToken(
        spec,
        this.host.fetch,
        {
          contentType: 'form',
          body: {
            grant_type: 'refresh_token',
            refresh_token: cred.refreshToken,
            client_id: spec.clientId,
            // ★ `scope` 现在是可选的 —— 没声明的家**一个字都不能写**。
            //   直接写 `scope: spec.scope` 的话,URLSearchParams 会把它编成
            //   字面量 `scope=undefined` 发出去,而那个 400 不会提到 scope。
            ...(spec.scope === undefined ? {} : { scope: spec.scope })
          },
          /*
            ★★ 这家的私货头在**刷新**这一跳上一样要带(见 `registry.ts` 的
            `oauthHeaders`)。漏掉的表现不是登不上,而是**能登录、第二天刷新 403**
            —— 一个隔一天才出现、且错误信息里不提任何头名的症状。
          */
          ...(spec.oauthHeaders === undefined ? {} : { headers: spec.oauthHeaders })
        },
        AbortSignal.timeout(30_000)
      )
    } catch (err) {
      /*
        ★★ 网络失败**一个字节都不动库里那条记录**,而且是 `retryable: true`。
        离线时删掉 refresh token,等于因为一次断网强迫用户重新登录一遍。
      */
      throw new CredentialAuthError(
        agentError('network', `刷新登录凭证失败：${err instanceof Error ? err.message : String(err)}`, {
          retryable: true
        })
      )
    }

    if (!result.ok) {
      /*
        ★ 5xx 是上游自己的问题,按网络故障处理并保留凭证 ——
        授权服务器抽风时把用户登出,是拿别人的故障惩罚自己的用户。
      */
      if (result.status >= 500) {
        throw new CredentialAuthError(
          agentError('network', `授权服务器暂时不可用（HTTP ${result.status}）`, { retryable: true })
        )
      }
      throw this.markReauth(ref, cred, `登录已失效（HTTP ${result.status}），请重新登录`)
    }

    return spec.identity(result.json, this.host.clock.now())
  }

  /**
   * 这家自己实现的刷新(不是标准 OAuth 刷新的那些)。
   *
   * ★★ **任何非 `CredentialAuthError` 的异常一律按网络故障处理,不动库。**
   * 钩子里通常要自己发一两个请求,而它抛出来的多半是 fetch 的错。把它当成
   * 「登录失效」会因为一次断网就把用户踢下线;反过来,钩子如果**确知**是失效
   * (上游明确拒了),它自己抛 `CredentialAuthError`,这里原样放行。
   */
  private async customRefresh(
    hook: NonNullable<OAuthProviderSpec['refresh']>,
    cred: OAuthCredential
  ): Promise<OAuthIdentity | null> {
    try {
      return await hook(cred, {
        fetch: this.host.fetch,
        // ★ 和标准路径同一条规矩:调用方的 signal 不下传,只用独立超时兜底
        signal: AbortSignal.timeout(30_000),
        now: this.host.clock.now()
      })
    } catch (err) {
      if (err instanceof CredentialAuthError) throw err
      throw new CredentialAuthError(
        agentError('network', `刷新登录凭证失败：${err instanceof Error ? err.message : String(err)}`, {
          retryable: true
        })
      )
    }
  }

  /**
   * 上游明确拒了。
   *
   * ★ **保留 refreshToken,不删。** 万一是上游抖动,用户手动重新登录会覆盖它;
   * 删了的话连「当时存的是什么」这条排查线索都没有了。只打一个 `needsReauth` 标,
   * 让界面说人话。
   */
  private markReauth(ref: string, cred: OAuthCredential, message: string): CredentialAuthError {
    void this.host.secrets
      .set(ref, serializeCredential({ ...cred, needsReauth: true }))
      .then(() => this.onChanged?.(ref))
      .catch(() => {
        /* 主密钥文件不可写时记不进去 —— 那也不该盖掉下面这个更要紧的错误 */
      })
    return new CredentialAuthError(agentError('auth', message, { retryable: false }))
  }
}
