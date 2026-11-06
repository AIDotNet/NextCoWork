/**
 * Kimi(Moonshot)Coding Plan —— 规格表里**第一条设备码流程**。
 *
 * ============================ 证据等级 ============================
 * 协议来自**官方一方源码**:`@moonshot-ai/kimi-code` 的 `packages/oauth`
 * (随 npm 包发布,v0.42.0)。不是逆向,不是文档推断 —— 下面每个端点、每个字段名
 * 都能在那份源码里逐字对上。
 *
 * **2026-09-14 我自己直连复现到的**(不需要代理,`auth.kimi.com` 直通):
 * - `POST auth.kimi.com/api/oauth/device_authorization`,body `client_id=<真>`
 *   → 200 `{device_code, user_code:"B7MB-FOW3", verification_uri:
 *   "https://www.kimi.com/code/authorize_device", verification_uri_complete,
 *   expires_in:1800, interval:5}`。
 * - 同一端点换一个**假** client_id → 401 `{"error":"invalid_client"}`。
 *   两条合起来才算数:证明端点活着,**并且它真的在校验 client_id**
 *   (只发真的那次,一个「什么都放行的桩」也会回 200)。
 * - `POST .../api/oauth/token`,`grant_type=urn:ietf:params:oauth:grant-type:device_code`
 *   + 刚拿到的 device_code → 400 `{"error":"authorization_pending"}`。
 *   即:轮询这条路是通的,状态机就是 RFC 8628 那套。
 * - 同一端点 `grant_type=refresh_token` + 编的 refresh_token → 400 `invalid_grant`。
 * - `api.kimi.com/coding/v1/{me,models,usages}` → 401,而同前缀的
 *   `/coding/v1/NOPE` → 404。**401 和 404 分得开**,说明前三个是真实存在的路由,
 *   不是一个把所有路径都回 401 的网关(这是本仓库 `presets.ts` 那套探针规矩)。
 *
 * ============================ 链路全貌 ============================
 * ```
 * ① POST auth.kimi.com/api/oauth/device_authorization   client_id
 *    → {device_code, user_code, verification_uri, verification_uri_complete,
 *       expires_in, interval}
 * ② 用户在浏览器里打开 verification_uri_complete(或手抄 user_code 去
 *    verification_uri 输),完成授权
 * ③ POST auth.kimi.com/api/oauth/token  client_id + device_code
 *         grant_type=urn:ietf:params:oauth:grant-type:device_code
 *    轮询,按 ① 给的 interval;错误码 authorization_pending / slow_down /
 *    expired_token / access_denied —— 状态机在 `flow.ts` 的设备码分支里
 *    → {access_token, refresh_token, expires_in, scope, token_type}
 * ④ GET  api.kimi.com/coding/v1/me   Authorization: Bearer <access_token>
 *    → {user_id, nickname, user_level_name, email?, …}  ← accountId 的唯一来源
 * ⑤ 发 AI 请求:协议 openai-chat,base `https://api.kimi.com/coding/v1`
 *    Authorization 由 encoder 写,这里只补 `User-Agent` + `X-Msh-*`
 * ```
 *
 * ★ 一条要记录的事实:我们用的是 **kimi-code CLI 的 client_id**,并且把
 * `X-Msh-Platform` 报成 `kimi_code_cli` —— 等于对上游自称是 kimi-code。
 * 仓库里已有两处同性质先例(`chatgpt.ts` 的 `codex_cli_rs`、`zcode.ts` 的 ZCode
 * appId)。写在这里是为了别让后人以为这是官方发给我们的客户端。
 */
import { hostname, release, type as osType, arch } from 'node:os'

import type { OAuthCredential } from '../../../../shared/domain/credential'
import type { TransportContext, UpstreamTransport } from '../../upstream/transport'
import { uuidFromSeed } from '../../upstream/ids'
import { OAuthFailedError } from '../errors'
import type { OAuthExchangeContext, OAuthIdentity, OAuthProviderSpec } from '../registry'
import { record, str } from './shared'

/** ★ kimi-code CLI 的公开 client id(public client,没有 client_secret)。 */
const CLIENT_ID = '17e5f671-d194-4dfb-9706-5516cb48c098'

/**
 * ★ 中国大陆区。kimi-code 还有一套全球区(`auth.kimi.ai` / `api.kimi.ai/coding/v1`,
 * **client_id 两区相同**)。这里只接大陆区,因为 `presets.ts` 里的 `kimi-coding`
 * 预设指的就是 `api.kimi.com`。要加全球区的话该照 zcode 那样拆成两条渠道
 * (两个 issuer id),而不是在这里加一个运行期开关 —— 凭证是按 issuer 存的,
 * 同一个 issuer 下换 host 会让已存的凭证指向一个它换不到 token 的服务器。
 */
const OAUTH_HOST = 'https://auth.kimi.com'
const API_BASE = 'https://api.kimi.com/coding/v1'

/**
 * ★★ **这两个值是兼容性风险面**,和 `chatgpt.ts` 的 `ORIGINATOR` 同一性质:
 * 上游很可能拿它们做客户端族判定。`kimi_code_cli` 和 `kimi-code-cli` 分别取自
 * 已发布的 `@moonshot-ai/kimi-code@0.42.0` 里的 `KIMI_CODE_PLATFORM` 和
 * `CLI_USER_AGENT_PRODUCT`(2026-09-14 从 npm tarball 里 grep 出来的)。
 *
 * ★ 版本号写死一个**真实存在过的**值。编一个不存在的版本号是在赌上游不做版本
 * 白名单,而赌输了的表现是一个不解释原因的 403。
 */
const KIMI_PLATFORM = 'kimi_code_cli'
const KIMI_UA_PRODUCT = 'kimi-code-cli'
const KIMI_VERSION = '0.42.0'

/**
 * 头值里的非 ASCII 一律剔掉。
 *
 * ★★ 这不是洁癖:主机名带中文(「张三的MacBook」)在国内是常态,而 `fetch` 往
 * 请求头里塞非 ASCII 会**当场抛 `TypeError`**。那个异常发生在登录的第一个请求上,
 * 信息里不会提到是哪个头 —— 表现就是「这台机器点登录没反应,换台机器就好了」。
 * kimi-code 自己也做了同一件事(`asciiHeader`),空了就回 `unknown`。
 */
function ascii(value: string): string {
  const cleaned = value.replace(/[^\x20-\x7e]/g, '').trim()
  return cleaned === '' ? 'unknown' : cleaned
}

/** kimi-code 的 `X-Msh-Device-Model`:`macOS 15.6 arm64` / `Windows … ` / `Linux …` */
function deviceModel(): string {
  const os = osType()
  if (os === 'Darwin') return `macOS ${release()} ${arch()}`
  if (os === 'Windows_NT') return `Windows ${release()} ${arch()}`
  return `${os} ${release()} ${arch()}`.trim()
}

/**
 * `X-Msh-Device-Id` —— 一个**跨重启稳定**的设备标识。
 *
 * ★★ kimi-code 的做法是 `randomUUID()` 落盘到 `<homeDir>/device_id`。这里**故意不
 * 落盘**,改成从机器固有信息哈希出来:issuer 模块必须是 Electron-free 的纯模块
 * (`registry.ts` 文件头那条规矩),拿不到 `app.getPath('userData')`;而为了一个
 * 设备 id 在这一层自己发明一个路径,等于在规格表里埋一个第二份「我们的数据放哪」
 * 的真相来源。
 *
 * ★ 稳定性才是这个字段的全部意义(上游用它数设备),随机性不是 —— 所以哈希出来的
 * 确定性值在语义上更对:重装应用之后仍然是同一台机器,而落盘的那个会变成新设备。
 */
const DEVICE_ID = uuidFromSeed(`nextcowork:kimi-device:${hostname()}:${osType()}:${arch()}`)

/**
 * ★★ 这套头要盖住**三条**路径:设备码申请、换 token、以及第二天的刷新。
 * 所以它挂在 `spec.oauthHeaders` 上(见 `registry.ts` 那个字段的注释),
 * 而不是塞进 `tokenRequest().headers` —— 后者只盖得住换码那一跳,漏掉的表现是
 * 「能登录、第二天刷新 403」,且错误信息里不会出现任何一个头的名字。
 */
export const KIMI_HEADERS: Readonly<Record<string, string>> = {
  'User-Agent': `${KIMI_UA_PRODUCT}/${KIMI_VERSION}`,
  'X-Msh-Platform': KIMI_PLATFORM,
  'X-Msh-Version': KIMI_VERSION,
  'X-Msh-Device-Name': ascii(hostname()),
  'X-Msh-Device-Model': ascii(deviceModel()),
  'X-Msh-Os-Version': ascii(release()),
  'X-Msh-Device-Id': DEVICE_ID
}

/**
 * `finishExchange` 之后、喂给 `identity()` 之前的中间形态。
 *
 * ★ 和 zcode 那条同一个理由:`identity()` 必须是**同步纯函数**(测试要能摆布
 * `now`),而这条链路要发两个请求才凑得齐一条凭证。异步的部分全在 `finishExchange`,
 * 同步的字段映射全在 `identity`。
 */
interface KimiExchange {
  accessToken: string
  refreshToken: string
  /** 上游给的相对秒数,原样带过来,由 `identity()` 当场折成绝对毫秒 */
  expiresIn: number
  accountId: string
  email?: string
  planType?: string
}

/** ④:取账号身份。**失败即登录失败** —— 见下面 `finishExchange` 里的理由 */
async function fetchUserInfo(
  ctx: OAuthExchangeContext,
  accessToken: string
): Promise<{ accountId: string; email?: string; planType?: string }> {
  const res = await ctx.fetch(`${API_BASE}/me`, {
    headers: {
      authorization: `Bearer ${accessToken}`,
      accept: 'application/json',
      ...KIMI_HEADERS
    },
    signal: ctx.signal
  })
  const text = await res.text()
  if (!res.ok) {
    throw new OAuthFailedError(`读取账号信息失败（HTTP ${res.status}）：${text.slice(0, 300)}`)
  }
  let json: unknown
  try {
    json = JSON.parse(text)
  } catch {
    throw new OAuthFailedError(`读取账号信息失败：响应不是 JSON —— ${text.slice(0, 300)}`)
  }
  const body = record(json)
  // ★ 有的部署会多包一层 `data`,两种都认
  const data = record(body?.['data']) ?? body
  const accountId = str(data?.['user_id']) ?? str(data?.['global_id'])
  if (accountId === undefined) {
    throw new OAuthFailedError('读取账号信息失败：响应里没有 user_id')
  }
  const email = str(data?.['email'])
  const planType = str(data?.['user_level_name']) ?? str(data?.['domain_name'])
  return {
    accountId,
    ...(email === undefined ? {} : { email }),
    ...(planType === undefined ? {} : { planType })
  }
}

/** ③ / 刷新的响应共用这一段:三个必需字段,少一个都不认 */
function readTokens(json: unknown, what: string): {
  accessToken: string
  refreshToken: string
  expiresIn: number
} {
  const body = record(json)
  const accessToken = str(body?.['access_token'])
  const refreshToken = str(body?.['refresh_token'])
  const expiresIn = body?.['expires_in']
  if (accessToken === undefined || refreshToken === undefined) {
    throw new OAuthFailedError(`${what}失败：响应里缺少 access_token 或 refresh_token`)
  }
  /*
    ★ kimi-code 自己也在这里挑剔(`tokenFromResponse`):`expires_in` 不是一个正数
    就判整个响应无效,而不是补一个 0 存进去。存了 0 的表现是**每一次请求前都判过期、
    每一次都去刷新** —— 一个看起来像「上游限流」的症状。
  */
  if (typeof expiresIn !== 'number' || !Number.isFinite(expiresIn) || expiresIn <= 0) {
    throw new OAuthFailedError(`${what}失败：响应里的 expires_in 不是一个有效的秒数`)
  }
  return { accessToken, refreshToken, expiresIn }
}

/** 刷新那一跳的表单 POST。`flow.ts` 的那个只管流程内的两跳,刷新在流程之外 */
async function postRefresh(ctx: OAuthExchangeContext, refreshToken: string): Promise<unknown> {
  const res = await ctx.fetch(`${OAUTH_HOST}/api/oauth/token`, {
    method: 'POST',
    headers: {
      'content-type': 'application/x-www-form-urlencoded',
      accept: 'application/json',
      ...KIMI_HEADERS
    },
    body: new URLSearchParams({
      client_id: CLIENT_ID,
      grant_type: 'refresh_token',
      refresh_token: refreshToken
    }).toString(),
    signal: ctx.signal
  })
  const text = await res.text()
  if (!res.ok) {
    throw new OAuthFailedError(`刷新令牌失败（HTTP ${res.status}）：${text.slice(0, 300)}`)
  }
  try {
    return JSON.parse(text)
  } catch {
    throw new OAuthFailedError(`刷新令牌失败：响应不是 JSON —— ${text.slice(0, 300)}`)
  }
}

function identity(json: unknown, now: number): OAuthIdentity | null {
  const x = record(json)
  if (x === undefined) return null
  const accessToken = str(x['accessToken'])
  const refreshToken = str(x['refreshToken'])
  const accountId = str(x['accountId'])
  const expiresIn = x['expiresIn']
  if (accessToken === undefined || refreshToken === undefined || accountId === undefined) {
    return null
  }
  const email = str(x['email'])
  const planType = str(x['planType'])
  return {
    accessToken,
    refreshToken,
    /*
      ★ 相对秒数在这里当场折成绝对毫秒时间戳。相对值一旦落盘就开始腐烂:
      重启之后没人知道那 3600 秒是从哪一刻算起的。
    */
    expiresAt: typeof expiresIn === 'number' ? now + expiresIn * 1000 : null,
    accountId,
    ...(email === undefined ? {} : { email }),
    ...(planType === undefined ? {} : { planType })
  }
}

export const KIMI_CODE_OAUTH: OAuthProviderSpec = {
  id: 'kimi-code',
  label: 'Kimi',
  tokenUrl: `${OAUTH_HOST}/api/oauth/token`,
  clientId: CLIENT_ID,
  /*
    ★ 设备码流程**没有 PKCE、没有 state、没有 redirect_uri**(RFC 8628 就是这么定的),
    `pkce: false` 在这里其实是多余的 —— 设备码分支根本不拼授权 URL。写出来是为了
    读代码的人不用先去 `flow.ts` 确认一遍。
  */
  pkce: false,
  grant: {
    kind: 'device-code',
    deviceAuthorizationUrl: `${OAUTH_HOST}/api/oauth/device_authorization`
  },
  oauthHeaders: KIMI_HEADERS,

  /**
   * ④ 拿账号身份。
   *
   * ★★ **这一跳失败就判整个登录失败**,和 zcode 那条「邮箱拿不到也不致命」正好相反:
   * 那边的邮箱只是设置页上的一个装饰,而这边的 `user_id` 是 `accountId` 的**唯一来源**
   * —— token 响应里没有任何身份字段,Kimi 的 access_token 也不是 JWT。
   * `parseCredential` 要求 accountId 非空,空了的表现是**登录成功、下一秒显示未登录**。
   */
  finishExchange: async (json, ctx) => {
    const tokens = readTokens(json, '换取令牌')
    const user = await fetchUserInfo(ctx, tokens.accessToken)
    const exchange: KimiExchange = {
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken,
      expiresIn: tokens.expiresIn,
      ...user
    }
    return exchange
  },

  identity,

  /**
   * 刷新。协议上就是标准的 `grant_type=refresh_token`,**但不能用
   * `CredentialResolver` 的 `standardRefresh`。**
   *
   * ★★ 理由是 `standardRefresh` 把 token 响应原样喂给 `identity()`,而 Kimi 的
   * token 响应里**没有任何身份字段** —— `identity()` 会因为拿不到 accountId 返回
   * `null`,而 `null` 的语义是「凭证已失效」。表现:**每刷新一次就把用户踢下线一次**。
   * (zcode 那条踩过同一个坑,注释在 `zcode.ts` 的 `refresh` 上。)
   *
   * ★ 这里沿用旧的 accountId / email / planType,不再打一次 `/me`:身份不会因为
   * 换了把 token 就变,而多一跳就多一个「刷新时网络抖一下就登出」的故障点。
   */
  refresh: async (cred: OAuthCredential, ctx: OAuthExchangeContext) => {
    const tokens = readTokens(await postRefresh(ctx, cred.refreshToken), '刷新令牌')
    return {
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken,
      expiresAt: ctx.now + tokens.expiresIn * 1000,
      accountId: cred.accountId,
      ...(cred.email === undefined ? {} : { email: cred.email }),
      ...(cred.planType === undefined ? {} : { planType: cred.planType })
    }
  },

  /*
    ★ 鉴权头**这里一个字都不写**:登录后端点被切到 `api.kimi.com/coding/v1`、
    协议是 `openai-chat`,`encode/openai-chat.ts` 首发就写 `authorization: Bearer`,
    401 重发路径 `transport.ts` 的 `authHeader` 对非 anthropic 也返回同一个头 ——
    两条路径本来就对。在这里再写一遍等于给同一件事开第二个真相来源。

    ★★ 带的是 `X-Msh-*` 那套设备头:kimi-code 对**每一个**上游请求都带它们
    (`createKimiDefaultHeaders`)。不带会怎样没有实测过 —— 但这整条链路的立场就是
    「按 kimi-code 的方式发请求」,少带一半头不叫按它的方式发。
  */
  transport: (_cred: OAuthCredential, _ctx: TransportContext): UpstreamTransport => ({
    headers: { ...KIMI_HEADERS },
    body: (body) => body
  })
}
