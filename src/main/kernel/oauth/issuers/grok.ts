/**
 * Grok Build(xAI 订阅)—— 规格表里的第二条设备码流程。
 *
 * 用户拿自己的 Grok 账号登录,请求走 `cli-chat-proxy.grok.com`,吃订阅额度而不是
 * API 账单。协议仍然是 **Responses**(和 Codex 那条同一个),变的只是凭证形态
 * 和下面这几个头 —— 见 `upstream/transport.ts` 文件头那段「为什么不加第四个协议值」。
 *
 * ## 证据(2026-09-14 实测,探针走 socks5 代理)
 *
 * 官方 CLI 是一个 Rust 二进制(`@xai-official/grok` 里按平台分发的 brotli 包),
 * 读不到一方源码。所以这一行的每个值都由**三条互不相干的证据**交叉确认:
 * ① 二进制的 `strings`、② `auth.x.ai` 的活探针、③ 两个第三方实现里的字面量。
 *
 * **OIDC 发现文档**(`GET https://auth.x.ai/.well-known/openid-configuration` → 200):
 * issuer `https://auth.x.ai`、device `/oauth2/device/code`、token `/oauth2/token`、
 * userinfo `/oauth2/userinfo`、grants 含 `urn:ietf:params:oauth:grant-type:device_code`、
 * PKCE 只有 S256、client 认证支持 `none`(public client)、id_token 签名 ES256。
 *
 * **client_id 的真假对照**(仓库判据,见 `presets.ts` 文件头):
 * - 真 `b1a00492-…` → 200,`user_code: PBCH-QSRW` / `expires_in: 1800` / `interval: 5`
 * - 伪造 UUID → 400 `invalid_client` / `Unknown or disabled client`
 * - 轮询 device token(没人去同意)→ 400 `authorization_pending`
 * - 伪造 refresh_token → 400 `invalid_grant`
 *
 * ## ★★ 为什么是设备码,不是授权码
 *
 * 官方 CLI 默认那条是授权码 + PKCE + 临时端口回环,`--device-auth` 是第二条。
 * 我们走第二条,理由是**证据强度**,不是偏好:
 *
 * `GET /oauth2/authorize` 无论带不带浏览器 UA、真假 client_id,**一律 403 +
 * Cloudflare 挑战页** —— 真浏览器没问题(CLI 就是开系统浏览器),但这条路上
 * `redirect_uri` 到底注册成什么形态**没法用探针验证**。二进制里那条
 * `http://127.0.0.1/callback` 属于**企业自带 IdP**(`GROK_OIDC_ISSUER`)那套文档,
 * 不是 auth.x.ai 这条消费者路径的证据。而 redirect_uri 猜错的表现,是一个
 * 不说明原因的 `invalid_grant`(见 `registry.ts` 的 `LoopbackHost` 注释)。
 *
 * 设备码那条则是端到端实测过的,且编排早就有了(Kimi 那条)。
 */
import type { OAuthCredential } from '../../../../shared/domain/credential'
import { OAuthFailedError } from '../errors'
import type { TransportContext, UpstreamTransport } from '../../upstream/transport'
import { sessionUuid } from '../../upstream/ids'
import type { OAuthExchangeContext, OAuthIdentity, OAuthProviderSpec } from '../registry'
import { decodeJwtPayload, record, str } from './shared'

const OAUTH_HOST = 'https://auth.x.ai'

/**
 * ★ Grok CLI 的**公开** client id(public client,没有 client_secret)。
 *
 * 三处对得上:`~/.grok/auth.json` 的 map key 逐字是
 * `https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828`、两个第三方实现里的
 * 常量、以及上面那条真假对照探针。
 */
const CLIENT_ID = 'b1a00492-073a-47ea-816f-4c329264a828'

/**
 * ★★ **scope 不能省。**
 *
 * 2026-09-14 实测:申请设备码时只发 `client_id` 照样 **200**,配对码一应俱全 ——
 * 但换回来那把令牌不带 `grok-cli:access`,表现是**登录一路成功、第一条消息 401**,
 * 而错误信息里一个字都不提 scope。同一端点发一个不存在的 scope 回的是 400
 * `invalid_scope`,说明它是真校验的。
 *
 * ★ 这一串**逐字**照 CLI 那条(第三方实现里有人加了 `conversations:*` /
 * `workspaces:*`,那是他们自己要用的;我们不发请求用不到的权限)。
 */
const SCOPE = 'openid profile email offline_access grok-cli:access api:access'

/**
 * ★★ **缺这个头是 426,不是 401。** 固定 Chat Proxy 对没有
 * `x-grok-client-version` 的请求回 `426 Upgrade Required` —— 一个和「凭证」
 * 毫无关系的状态码,照着它去查凭证会查一整天。
 *
 * 值取自 npm 上 `@xai-official/grok` 当前分发的那个版本(1.0.30)。
 * ★ 它和 `presets.ts` 那张表一样是**会腐烂**的:上游哪天对它做下限校验,
 * 表现就是整家供应商 426。改它之前先实测,并把日期写在这。
 */
const CLIENT_VERSION = '1.0.30'

/**
 * ★★ **诚实地报自己的名字,不冒充 `grok-shell`。**
 *
 * 这个头是可以填任意值的,但没有理由填别人的:dsh 那份取证文档里用自己的包名
 * 发真实请求拿到的是 **200**(2026-08-26),说明上游对它不做白名单。
 * 冒充官方 CLI 换不到任何东西,却会让上游的用量统计和限流认不出我们。
 */
const CLIENT_IDENTIFIER = 'nextcowork'

/**
 * 鉴权是**两维**的,401 的 `www-authenticate` 自己把话说清楚了(实测):
 * ```
 * auth_kind=none,   x_xai_token_auth=none         → upstream=Unauthenticated
 * auth_kind=bearer, x_xai_token_auth=none         → upstream=Unauthenticated
 * auth_kind=bearer, x_xai_token_auth=xai-grok-cli → upstream=PermissionDenied
 * ```
 * 第三行和前两行的 `upstream` **不一样** —— 说明这个头真的改了后端路由,
 * 不是一个被忽略的装饰。
 */
const TOKEN_AUTH = 'xai-grok-cli'

/**
 * 这套头盖住**三条** OAuth 路径:申请设备码、轮询换 token、以及第二天的刷新。
 *
 * ★ 实测这两个头在申请设备码那一跳上**不是必需**(只发 `client_id` 也 200)。
 * 照带是为了和官方 CLI 的形状一致 —— 上游哪天开始按它分流,我们不会是
 * 「只有刷新那一跳没带」的那种半残状态(见 `registry.ts` 的 `oauthHeaders`)。
 *
 * ★ `surface` 取 `ui`:我们确实会把配对码画在界面上并开浏览器,不是 headless。
 */
const GROK_OAUTH_HEADERS: Readonly<Record<string, string>> = {
  'x-grok-client-version': CLIENT_VERSION,
  'x-grok-client-surface': 'ui'
}

/**
 * 从一把 JWT 里挑出账号 id。
 *
 * ★★ **三级回退,而不是只读 `sub`。** 个人账号的 id_token 里 `sub` 就是答案;
 * 但团队/组织席位签出来的令牌走的是 `principal_id`(官方 CLI 在 auth.json 里
 * 存的 `user_id` 对团队席位填的正是它)。只读 `sub` 的表现是团队用户
 * **登录成功、下一秒显示未登录** —— `parseCredential` 要求 accountId 非空。
 *
 * ★ 不验签,理由和 `chatgpt.ts` 的 `parseIdTokenClaims` 逐字相同:这把 token 是
 * 我们自己发起的流程经 TLS 从 token 端点直接拿回来的,我们只从里面读显示用的
 * 字段和一个我们自己要回填的 id,真正的授权由上游对 access_token 校验。
 */
function claimsOf(token: string | undefined): { accountId?: string; email?: string } {
  const claims = decodeJwtPayload(token)
  if (claims === undefined) return {}
  const accountId = str(claims['principal_id']) ?? str(claims['sub'])
  const email = str(claims['email'])
  return {
    ...(accountId === undefined ? {} : { accountId }),
    ...(email === undefined ? {} : { email })
  }
}

/**
 * token 响应 → 凭证。
 *
 * ★ 账号身份的来源按优先级是 `id_token` → `access_token`。两者都是 JWT
 * (发现文档写着 id_token 是 ES256;access_token 自己也带 `principal_type` /
 * `principal_id` / `sub`),所以这里**不需要第二跳** —— 和 Kimi 那条正相反,
 * 那家的 access_token 不是 JWT、身份只能去 `/me` 要。
 */
function identity(json: unknown, now: number): OAuthIdentity | null {
  const body = record(json)
  if (body === undefined) return null
  const accessToken = str(body['access_token'])
  const refreshToken = str(body['refresh_token'])
  /*
    ★★ 没有 refresh_token 就判失败,而不是存一条刷不了的凭证。
    scope 里带了 `offline_access`,拿不到它说明授权没按我们要的形态发下来;
    存下去的表现是**今天能用、明天一早掉线**,而那时错误信息只会说「请重新登录」。
  */
  if (accessToken === undefined || refreshToken === undefined) return null

  const claims = { ...claimsOf(accessToken), ...claimsOf(str(body['id_token'])) }
  if (claims.accountId === undefined) return null

  /*
    ★ `expires_in` 是相对秒数,当场折成绝对毫秒 —— 相对值一旦落盘就开始腐烂
    (理由见 `chatgpt.ts` 那段)。缺字段时给一个保守的 1 小时。
  */
  const expiresIn = typeof body['expires_in'] === 'number' ? body['expires_in'] : 3600
  return {
    accessToken,
    refreshToken,
    expiresAt: now + expiresIn * 1000,
    accountId: claims.accountId,
    ...(claims.email === undefined ? {} : { email: claims.email })
  }
}

export const GROK_BUILD_OAUTH: OAuthProviderSpec = {
  id: 'grok-build',
  label: 'Grok',
  tokenUrl: `${OAUTH_HOST}/oauth2/token`,
  clientId: CLIENT_ID,
  scope: SCOPE,
  /*
    ★ 设备码流程没有 PKCE(RFC 8628 就是这么定的),写出来是为了读代码的人
    不用先去 `flow.ts` 确认一遍 —— 和 `kimi.ts` 那条同一个理由。
  */
  pkce: false,
  grant: {
    kind: 'device-code',
    deviceAuthorizationUrl: `${OAUTH_HOST}/oauth2/device/code`
  },
  oauthHeaders: GROK_OAUTH_HEADERS,

  identity,

  /**
   * 刷新。协议上就是标准的 `grant_type=refresh_token`(`standardRefresh` 发的
   * 那三个字段逐字够用),**但仍然不能走 `standardRefresh`。**
   *
   * ★★ 理由:`standardRefresh` 把响应原样喂给 `identity()`,而刷新响应里
   * **不保证有 `id_token`**(第三方实现里那句 `payload.id_token ?? xai.idToken`
   * 就是在兜这件事)。没有它时 `identity()` 只能去 access_token 里找,找不到就
   * 返回 `null` —— 而 `null` 在 `CredentialResolver` 里的语义是「凭证已失效」,
   * 表现是**每刷新一次就把用户踢下线一次**。(Kimi 和 zcode 都踩过这个坑。)
   *
   * ★ 所以这里先照常解析新令牌,身份字段**解得出就更新、解不出就沿用旧的** ——
   * 身份不会因为换了把 token 就变。
   */
  refresh: async (cred: OAuthCredential, ctx: OAuthExchangeContext) => {
    const res = await ctx.fetch(`${OAUTH_HOST}/oauth2/token`, {
      method: 'POST',
      headers: {
        'content-type': 'application/x-www-form-urlencoded',
        accept: 'application/json',
        ...GROK_OAUTH_HEADERS
      },
      body: new URLSearchParams({
        grant_type: 'refresh_token',
        refresh_token: cred.refreshToken,
        client_id: CLIENT_ID
      }).toString(),
      signal: ctx.signal
    })
    const text = await res.text()
    if (!res.ok) {
      /*
        ★ 抛而不是返回 null:两者在 `CredentialResolver` 里的结局都是要求重新登录,
        但抛出来的那条带着上游原话(`invalid_grant` / `Invalid or unknown refresh
        token`,2026-09-14 实测),而 null 那条只会说一句「凭证不完整」。
      */
      throw new OAuthFailedError(`刷新 Grok 登录失败（HTTP ${res.status}）：${text.slice(0, 300)}`)
    }
    let json: unknown
    try {
      json = JSON.parse(text)
    } catch {
      throw new OAuthFailedError('刷新 Grok 登录失败：响应不是 JSON')
    }
    const fresh = identity(json, ctx.now)
    if (fresh !== null) return fresh

    /*
      ★ 走到这里 = 令牌换到了,只是身份字段解不出来。这时**不能**返回 null,
      而是拿旧身份把新令牌兜住 —— 这正是上面那段注释说的那个坑。
    */
    const body = record(json)
    const accessToken = str(body?.['access_token'])
    if (accessToken === undefined) return null
    const expiresIn = typeof body?.['expires_in'] === 'number' ? body['expires_in'] : 3600
    return {
      accessToken,
      // ★ 上游不轮换 refresh token 时沿用旧的(`standardRefresh` 也是这个规矩)
      refreshToken: str(body?.['refresh_token']) ?? cred.refreshToken,
      expiresAt: ctx.now + expiresIn * 1000,
      accountId: cred.accountId,
      ...(cred.email === undefined ? {} : { email: cred.email }),
      ...(cred.planType === undefined ? {} : { planType: cred.planType })
    }
  },

  /**
   * 发请求时的装饰。
   *
   * ★★ `Authorization: Bearer` **不在这里写** —— 两个 OpenAI 族的 encode 本来
   * 写出来的就是它,值也正是 `cred.accessToken`(见 `transport.ts` 文件头)。
   * 在这儿再写一遍只会多一处将来会和 encode 分叉的地方。
   *
   * ★ `x-grok-session-id` / `x-grok-conv-id` 用 `sessionUuid` 折一道,而不是把
   * 我们的 ULID 原样发出去 —— 和 OpenCode Go 那条同一个理由。上游只要求一个
   * 稳定标识,我们没必要把自己的内部 id 形状告诉它。
   */
  transport: (cred: OAuthCredential, ctx: TransportContext): UpstreamTransport => {
    const session = sessionUuid(ctx.sessionId)
    return {
      headers: {
        'X-XAI-Token-Auth': TOKEN_AUTH,
        'x-grok-client-version': CLIENT_VERSION,
        'x-grok-client-identifier': CLIENT_IDENTIFIER,
        'x-grok-client-mode': 'interactive',
        'x-grok-user-id': cred.accountId,
        'x-grok-session-id': session,
        'x-grok-conv-id': session
      },
      /*
        ★ body 不动。这条通道没有 Codex 那种「`store: true` 会被拒」的硬约束 ——
        没拿到证据就不按字段,按错了的表现是一个我们自己制造的 400。
      */
      body: (b) => b
    }
  }
}
