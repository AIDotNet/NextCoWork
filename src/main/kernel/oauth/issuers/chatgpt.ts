/**
 * ChatGPT(Codex)—— 规格表的第一行。
 *
 * 用户拿自己的 ChatGPT 订阅账号登录,请求走 `chatgpt.com/backend-api/codex`,
 * 吃的是订阅额度而不是 API 账单。协议仍然是 Responses,**没有新协议** ——
 * 变的只是凭证形态和这里这几个头。
 */
import type { OAuthCredential } from '../../../../shared/domain/credential'
import type { TransportContext, UpstreamTransport } from '../../upstream/transport'
import { sessionUuid } from '../../upstream/ids'
import type { OAuthIdentity, OAuthProviderSpec } from '../registry'
import { decodeJwtPayload, record, str } from './shared'

/**
 * ★★ **这个值是兼容性风险面。** 上游很可能对它做白名单校验(Codex CLI 自己发的是
 * `codex_cli_rs`)。改它之前必须重新实测 —— 改错的表现是 403 或者被静默降级,
 * 而错误信息里不会提到 originator。
 *
 * 和 `presets.ts` 那套「探针 + 假路径对照」是同一条文化:这类值只在拿到实测
 * 证据时才动,并且把实测结论和日期写在旁边。
 */
const ORIGINATOR = 'codex_cli_rs'

/**
 * 需求:codex 这条线的**出站请求要自报成 Codex CLI**,UA 与 originator 同族 ——
 * 在这之前业务请求发的是 `NextCoWork/<版本> (darwin; arm64)`,与 originator 自相矛盾。
 *
 * 实测依据(2026-09-27):npm `latest` 的 `@openai/codex` 是 **0.157.1** ——
 * 从 registry 拉了 `0.157.1-darwin-arm64` 平台包,二进制里留有同族的
 * `codex-doctor/0.157.1` 字面量,`codex_cli_rs` 仍在源码路径
 * `login/src/auth/default_client.rs` 下,client id 未变。Codex 源码
 * `codex-rs/login/src/auth/default_client.rs` 的 `get_codex_user_agent()` 拼的正是
 * `{originator}/{CARGO_PKG_VERSION} (OS 版本; 架构) …`,版本格式与这里一致。
 * 注:本机全局还装着 0.154.0 未升级,但 registry 上的 latest(0.157.1)才是
 * 大多数用户真实在跑的版本,UA 跟后者。
 *
 * ★ 版本号写死一个**真实存在过的**值,理由同 `zcode.ts` 那条:编一个不存在的版本号
 * 是在赌上游不做版本白名单,赌输了是一个不解释原因的 403。
 *
 * ★ 我们只发前段(`codex_cli_rs/<版本>`),**没有实测过**整串与前段的差别 ——
 * 这是按用户口径定的。若上游开始拒,先补 OS/架构段,别去改 originator。
 *
 * 两处用它:业务请求(`transport.headers`,压过 `router.ts` 那行通用 UA)、
 * OAuth 换 token 与刷新(`spec.oauthHeaders`)。授权 URL 那一跳是浏览器导航,
 * **不带**它 —— 见 `kernel/user-agent.ts` 文件头那张表的第 1 行。
 * 不满足会怎样:表现是 403 或被静默降级,而错误信息里不会出现 UA 一个字。
 */
const CODEX_USER_AGENT = 'codex_cli_rs/0.157.1'

/**
 * ★ 这是 Codex CLI 的**公开** client id(public client + PKCE,没有 client_secret ——
 * 桌面应用本来就藏不住密钥,PKCE 才是防线)。
 * 它和 `redirectPort` 是一对:redirect_uri 必须逐字节等于这个 client 注册的那个值。
 */
const CLIENT_ID = 'app_EMoamEEZ73f0CkXaXp7hrann'

/**
 * ★★ **1455 是注册死的,不能换。** 见 `registry.ts` 的 `loopback-fixed` 注释,
 * 以及 `net/oauth-loopback.ts` 里为什么端口被占用时要抛专门的错而不是换一个。
 */
const REDIRECT_PORT = 1455
const REDIRECT_PATH = '/auth/callback'

/** id_token 里那些 claim 挂在这个命名空间下 */
const CLAIM_NS = 'https://api.openai.com/auth'

/**
 * 读 id_token 的 payload。
 *
 * ★★ **不验签,这是有意的。** 这个 token 是我们自己发起的流程、用我们自己的 PKCE
 * verifier、经 TLS 从 token 端点**直接**拿回来的;我们从里面只读两样东西:
 * `accountId`(API 要求带的头的值)和显示用的 email / plan。我们没有把信任委托给
 * 第三方,也没有拿它当授权凭据 —— 真正的授权由上游对 access_token 校验。
 * 加一套 JWKS 拉取只会引入一个「网络不通就登不上」的新故障点,换不到任何实际保证。
 *
 * ★ 解不开一律返回空对象,不抛:登录能不能成的判据是**拿没拿到 accountId**,
 * 而那个判断在 `identity()` 里,一处就够。
 */
export function parseIdTokenClaims(idToken: string | undefined): {
  accountId?: string
  email?: string
  planType?: string
} {
  const claims = decodeJwtPayload(idToken)
  if (claims === undefined) return {}

  const ns = record(claims[CLAIM_NS]) ?? {}
  const accountId = str(ns['chatgpt_account_id'])
  const email = str(claims['email'])
  const planType = str(ns['chatgpt_plan_type'])
  return {
    ...(accountId === undefined ? {} : { accountId }),
    ...(email === undefined ? {} : { email }),
    ...(planType === undefined ? {} : { planType })
  }
}

function identity(json: unknown, now: number): OAuthIdentity | null {
  const body = record(json)
  if (body === undefined) return null
  const accessToken = str(body['access_token'])
  const refreshToken = str(body['refresh_token'])
  if (accessToken === undefined || refreshToken === undefined) return null

  const claims = parseIdTokenClaims(str(body['id_token']))
  /*
    ★★ **拿不到 accountId 就判整个登录失败。**
    没有它,`chatgpt-account-id` 头发不出去,第一次对话必然 403。
    让用户先看到一句「登录成功」、再在对话框里撞上一个看不懂的 403,
    比当场说「授权信息里没有账号 id」糟得多 —— 后者至少指向重新登录。
  */
  if (claims.accountId === undefined) return null

  /*
    ★ `expires_in` 是秒且是相对值,这里当场折成绝对毫秒时间戳。
    相对值一旦落盘就开始腐烂:重启之后没人知道那 3600 秒是从哪一刻算起的。
    缺这个字段时给一个保守的 1 小时 —— 猜短了最多多刷新一次,猜长了会带着
    一把已经过期的 token 去撞 401。
  */
  const expiresIn = typeof body['expires_in'] === 'number' ? body['expires_in'] : 3600
  return {
    accessToken,
    refreshToken,
    expiresAt: now + expiresIn * 1000,
    accountId: claims.accountId,
    ...(claims.email === undefined ? {} : { email: claims.email }),
    ...(claims.planType === undefined ? {} : { planType: claims.planType })
  }
}

function transport(cred: OAuthCredential, ctx: TransportContext): UpstreamTransport {
  return {
    headers: {
      'chatgpt-account-id': cred.accountId,
      'openai-beta': 'responses=experimental',
      originator: ORIGINATOR,
      // ★ 与 originator 同族;写在这里才会压过 router.ts 那行通用的 NextCoWork UA
      'user-agent': CODEX_USER_AGENT,
      session_id: sessionUuid(ctx.sessionId)
    },
    /*
      ★★ **`store` 和 `stream` 在这里被按回去,而不是在 encode 里设一次。**
      这条通道对 `store: true` 会拒,而用户的 `requestAdapter.patches` 能改到
      body 的任何一个字段 —— 一个从别处抄来的 patch 里带个 `"store": true`,
      就会让这家供应商整个不可用,且报错指向的是上游而不是那条 patch。
      放在最终线上边界 = 供应商自己的硬约束压过模型级自定义,和
      `applyAnthropicRequestOptions` 是同一条规矩(见 `router.ts` 那段英文注释)。

      ★★ **`max_output_tokens` 和 `temperature` 必须摘掉。** 这条通道对它们不是
      忽略,是直接 400 `Unsupported parameter: max_output_tokens`(2026-09 实测)。
      两者在 `encode/openai-responses.ts` 里写进 body 对 `api.openai.com` 完全正确,
      所以不能去那边删 —— 只能在这个凭证专属的边界上摘。
      `temperature` 是顺带一起摘的:实测撞到的是前者,但这条通道只服务推理模型,
      而推理模型在 Responses 上本来就不收 temperature,留着只是等下一个 400。

      ★ 代价:「输出上限」这个设置对本供应商不生效 —— 额度由订阅侧自己管,
      我们说了不算。上下文长度校验不受影响(那走 alias 的 contextWindow)。
    */
    body: (body) => {
      const { max_output_tokens: _max, temperature: _temperature, ...rest } = body as Record<string, unknown>
      return { ...rest, store: false, stream: true }
    }
  }
}

export const CHATGPT_OAUTH: OAuthProviderSpec = {
  id: 'chatgpt',
  label: 'ChatGPT',
  tokenUrl: 'https://auth.openai.com/oauth/token',
  clientId: CLIENT_ID,
  /*
    ★ 换 token 与第二天的刷新共用这一处 —— 只写进换码那一跳的私货头会漏掉刷新,
    表现是「能登录、第二天刷新 403」,而错误信息里不会出现任何一个头的名字
    (见 `registry.ts` 那个字段的注释,kimi / zcode 也是这么挂的)。
    ★ 授权 URL 是浏览器导航,不吃这个对象 —— 那一跳继续保持浏览器 UA。
  */
  oauthHeaders: { 'user-agent': CODEX_USER_AGENT },
  scope: 'openid profile email offline_access',
  grant: {
    kind: 'authorization-code',
    authorizeUrl: 'https://auth.openai.com/oauth/authorize',
    redirect: { kind: 'loopback-fixed', port: REDIRECT_PORT, path: REDIRECT_PATH }
  },
  /*
    ★ `id_token_add_organizations` 让 id_token 带上那个 `…/auth` 命名空间下的
    组织与账号 claim —— 也就是 `accountId` 的唯一来源。不带它,`identity()`
    会因为拿不到 accountId 而判登录失败。
  */
  extraAuthorizeParams: { id_token_add_organizations: 'true' },
  identity,
  transport
}
