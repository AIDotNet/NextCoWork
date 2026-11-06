/**
 * 让「设置 › 连接 › 网络」那一页**真的作用于出站请求**。
 *
 * ## 在这之前它什么也不做
 *
 * `host/index.ts` 的表格里曾经写着「设置页的代理对模型请求生效,靠的就是
 * `net.fetch`」—— 那句话是错的,并且错得很隐蔽:`net.fetch` 确实走 Chromium
 * 网络栈,可 Chromium 默认跟随的是**系统**代理,从来没有人把 `settings.proxy`
 * 交给它。用户在设置页填了地址、开了开关,请求照旧直连,而界面上一切正常。
 * 这个文件是那句注释的兑现。
 *
 * ## ★★ 「关闭」曾经比「什么都不做」更糟
 *
 * 兑现那句注释的第一版有个反向的错:`proxyConfigFor` 在开关关着时返回
 * `{ mode: 'direct' }`。而上一段刚说过 —— Chromium **默认就跟随系统代理**。
 * 于是那一版做的事情是:把「用户没开这个开关」翻译成「用户要求无视系统代理」,
 * 主动关掉了一个本来好好的默认行为。
 *
 * 症状离原因非常远:开箱即用状态下每一个出站请求都绕过系统代理,而设置页上
 * 那个开关关着、界面一切正常。实测撞到的是换取 OAuth 凭证时的
 * `HTTP 403 unsupported_country_region_territory` —— 一句完全不提代理的话。
 *
 * 现在「关闭」= `{ mode: 'system' }`,理由与「开了但地址没填完仍走直连」的
 * 例外都写在 `shared/domain/proxy.ts` 的 `proxyConfigFor` 上。
 *
 * ## 为什么一次 `setProxy` 就够
 *
 * 全应用的出站请求只有一条路:`KernelHost.fetch` → `net.fetch` → `defaultSession`。
 * 模型请求、MCP 的 http/sse 传输、六家搜索适配器,全都从那里出去
 * (`search/types.ts` 文件头的第一条铁律就是「HTTP 一律走 deps.fetch」)。
 * 所以设置 `defaultSession` 一处,三者一起跟着走 —— 不需要逐个子系统去接。
 *
 * **例外:MCP 的 stdio 传输不受此约束。** 那是子进程,它自己怎么联网由它自己
 * (和它继承到的环境变量)决定,Chromium 的代理设置管不到。这不是疏漏,是边界。
 *
 * ## 子进程要用这份配置时:问 `resolveProxyForHost`
 *
 * 需求:SSH 也要默认跟随系统代理。上一段那条边界仍然成立(ssh 是子进程,
 * `setProxy` 管不到它),所以换的是另一条路 —— 让子进程侧**来问**「连这台主机
 * 该走哪个代理」,答案仍由同一个 `defaultSession` 给出。这样手动代理、系统代理、
 * PAC 脚本、直连白名单四件事只有一份实现,不会出现「设置页改了,ssh 那边还是老的」。
 *
 * ## Agent 的 shell 命令走 `shellProxyEnv`
 *
 * 需求:Agent 通过 Bash 工具 / 后台 shell / 本地钩子起的命令(`npm install`、
 * `curl`、`git clone`…)默认也要走同一份代理。CLI 只认 `HTTP_PROXY` 一类环境变量,
 * 而 Electron 从 Finder/Dock 启动时 `process.env` 里没有它们 —— 所以由这里把
 * 「当前该走哪个代理」翻成那撮环境变量(`shellProxyEnv`),经 `KernelHost.childEnv`
 * 注入子进程。答案仍从 `defaultSession` 来,四件事仍然只有一份实现。
 */
import { app, session } from 'electron'
import type { ProxyDialTarget, ProxyPasswordInfo, ProxySettings } from '../../shared/domain/proxy'
import {
  childProxyEnv,
  composeProxyUrl,
  DIRECT_BYPASS,
  isLocalNetworkHost,
  LOCAL_ONLY_BYPASS,
  normalizeBypassList,
  parseResolvedProxy,
  proxyConfigFor
} from '../../shared/domain/proxy'
import { removeCredential } from '../db/repo'
import { getHost } from '../runtime'

/** 代理密码在密钥环里的 ref。**只有这个文件读它**,没有任何 IPC 频道能取回明文。 */
export const PROXY_PASSWORD_REF = 'proxy:password'

/** 最近一次生效的配置 —— `app.on('login')` 要用它取用户名,以及判断认证开没开 */
let current: ProxySettings | null = null

/**
 * 这一套凭证试过没有。
 *
 * ★ **失败不重试第二次。** Chromium 在代理拒绝之后会再调一次这个回调,
 * 密码错的话就成了一个无限循环 —— 而不少企业代理接在 AD 上,连错几次
 * 会把**账号**锁掉。用户为此付出的代价远大于「多试一次万一成功了」。
 * 所以第二次直接 `cb()`(等于放弃认证),让请求以 407 失败,用户看得见。
 *
 * 存的是「上一次交出去的那把钥匙」的标识,`applyProxy` 换配置时清空:
 * 改了地址或用户名意味着这是一套新凭证,值得再试一次。
 */
let triedKey: string | null = null

/**
 * 子进程代理环境变量的缓存(见 `shellProxyEnv`)。★ 存的是 **Promise 本身**,不是
 * 结果:并发的前台 + 后台命令同时冷启动时不会各自去 `resolveProxy` 一遍(PAC 脚本下
 * 那次调用不便宜),而且第一个调用方拿到什么,后面的调用方拿到的就是什么,不会出现
 * 两条命令走着不同代理的窗口。`applyProxy` 换配置时置空。
 *
 * ★ 已知的洞:**跟随系统**时,系统代理在应用运行中被改掉不会经过 `applyProxy`,
 *   缓存要等到下次改设置或重启才刷新。这是这条路的代价,不是疏忽 —— Electron
 *   没有系统代理变化的事件可听,不肯为它加轮询。
 */
let childEnvCache: Promise<Record<string, string>> | null = null

function credentialKey(p: ProxySettings): string {
  return `${composeProxyUrl(p)}|${p.authUser}`
}

/**
 * 把设置推给 Chromium。**每次代理设置变化都要调一次**,包括改回「关闭」——
 * 只在开启时调的话,关掉开关之后上一次的 `proxyRules` 还留在 session 上,
 * 表现是「我关了代理,它还在走代理」。
 */
export async function applyProxy(p: ProxySettings): Promise<void> {
  current = p
  // 换了一套配置 = 换了一套凭证,上面那个「只试一次」的闸重新开一次
  triedKey = null
  // 子进程那撮代理变量跟着作废 —— 不清的话,改完设置之后新起的命令还走老路
  childEnvCache = null

  const cfg = proxyConfigFor(p)
  await session.defaultSession.setProxy(cfg)
  /*
    ★ 已经建立的连接不受新代理设置影响 —— Chromium 的连接池会把它们复用下去。
    不清的话,改完代理之后正在进行的那一轮对话仍旧走老路径,而用户以为已经切了。
  */
  session.defaultSession.closeAllConnections()

  getHost().logger.info(
    `[proxy] ${
      !p.enabled || p.mode === 'system'
        ? '跟随系统'
        : (cfg.proxyRules ?? '(地址没填完,按直连处理)')
    }`
  )
}

/**
 * 「连 host:port 该走哪个代理」。`null` = 直连。
 *
 * 需求:ssh 子进程也要跟随同一份代理设置(见文件头最后一段),而它没法被
 * `setProxy` 管到,只能反过来问。
 *
 * ★ 问的是 `defaultSession` 而不是自己读 `current`:手动代理的规则串、内置直连
 * 白名单、系统设置里的 PAC 脚本全在它那边,在这里再判一遍必然和设置页分叉 ——
 * 表现为「设置页说走代理,ssh 却直连」,两边看起来都没错。
 * 代价是得编一个 URL(Chromium 那个接口按 URL 解析,而 ssh 不是 http):
 * 用 `https://` 是因为 PAC 脚本按协议分支时,CONNECT 隧道归在 https 这一支。
 *
 * ★ 凭据**只在解析结果正是用户手填的那台代理时**才带上。系统代理或 PAC 指到的
 * 是另一台机器,把用户填给自己代理的账号密码送过去就是凭据外泄。
 */
export async function resolveProxyForHost(hostname: string, port: number): Promise<ProxyDialTarget | null> {
  // 局域网/回环一律直连 —— 理由与「判不出来就交回给代理规则」都写在 isLocalNetworkHost 上
  if (isLocalNetworkHost(hostname)) return null
  const authority = hostname.includes(':') ? `[${hostname}]` : hostname
  const endpoint = parseResolvedProxy(await session.defaultSession.resolveProxy(`https://${authority}:${String(port)}`))
  if (endpoint === null) return null
  const p = current
  if (p === null || !p.enabled || p.mode !== 'manual' || !p.authEnabled || p.authUser === '') return endpoint
  if (p.host !== endpoint.host || (p.port !== 0 && p.port !== endpoint.port)) return endpoint
  const password = await getHost().secrets.get(PROXY_PASSWORD_REF).catch(() => null)
  return { ...endpoint, username: p.authUser, password: password ?? '' }
}

/**
 * 探针 URL。`example.com` 是 IANA 保留的示例域名,永远在外网、永远不该命中任何
 * 直连名单。环境变量表达不了 PAC 那种按目标分流的规则,所以只能探这一次。
 */
const SHELL_PROXY_PROBE_URL = 'https://example.com/'

/**
 * Agent 子进程要继承的代理环境变量(`HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` /
 * `NO_PROXY`,凭据除外 —— 见 `childProxyEnv` 文件内那条 ★★)。
 *
 * ★ 仍然问 `defaultSession`,不自己读 `current`:手动代理、系统设置、PAC 脚本三种
 *   情况下「该不该走代理、走哪台」只有它有完整答案 —— 与 `resolveProxyForHost`
 *   同一条理由。PAC 只对特定域名放行的配置会探得 DIRECT、于是什么也不注入;
 *   那是环境变量这种载体力所能及的边界,不是这里能修的。
 *
 * ★ **失败绝不抛。** 这条函数站在每一次 Bash 调用的必经之路上,抛了等于 session
 *   抽风一次、Agent 的 shell 工具整个死掉。降级成「按继承环境跑」,日志里留一句。
 */
export function shellProxyEnv(): Promise<Record<string, string>> {
  childEnvCache ??= resolveShellProxyEnv()
  return childEnvCache
}

async function resolveShellProxyEnv(): Promise<Record<string, string>> {
  try {
    const endpoint = parseResolvedProxy(await session.defaultSession.resolveProxy(SHELL_PROXY_PROBE_URL))
    if (endpoint === null) return {}
    // 手动代理:白名单沿用设置页那份(用户填的在前、内置表在后,与 proxyBypassRules 同序);
    // 跟随系统:系统的排除列表读不到,只保回环 —— 理由在 LOCAL_ONLY_BYPASS 上
    const p = current
    return childProxyEnv(endpoint, process.env, {
      bypass: p !== null && p.enabled && p.mode === 'manual'
        ? [...normalizeBypassList(p.bypass), ...DIRECT_BYPASS]
        : LOCAL_ONLY_BYPASS,
      lowercase: process.platform !== 'win32'
    })
  } catch (error) {
    getHost().logger.warn('[proxy] 解析子进程代理变量失败,本次按继承环境执行', error)
    return {}
  }
}

/**
 * 装上代理认证的应答。**只在 app ready 之后调一次**(`main/index.ts`)。
 *
 * 这个回调是全局的,`event.preventDefault()` 之后 Electron 就不会再弹它自己那个
 * 系统登录框了 —— 所以 `authInfo.isProxy` 那一判必须在最前面:网站自己的
 * 401 Basic 认证也会走同一个事件,吞掉它等于让所有需要登录的网页静默失败。
 */
export function installProxyAuth(): void {
  app.on('login', (event, _webContents, _request, authInfo, callback) => {
    if (!authInfo.isProxy) return
    const p = current
    if (p === null || !p.enabled || p.mode !== 'manual' || !p.authEnabled) return

    event.preventDefault()

    const key = credentialKey(p)
    if (triedKey === key) {
      // 上面那把锁:这套凭证已经被拒过一次了,不再送第二次
      getHost().logger.warn('[proxy] 代理认证被拒,不再重试(避免账号被锁)')
      callback()
      return
    }
    triedKey = key

    void getHost()
      .secrets.get(PROXY_PASSWORD_REF)
      .then((password) => {
        callback(p.authUser, password ?? '')
      })
      .catch((err: unknown) => {
        // 密钥环读不出来 —— 放弃认证而不是卡住,请求会以 407 失败,用户看得见
        getHost().logger.error('[proxy] 读取代理密码失败', err)
        callback()
      })
  })
}

/**
 * 「保存密码」。写完**立刻重推一次配置** —— 不推的话,新密码要等到下次
 * 改代理设置或者重启才被 `app.on('login')` 用上,而用户刚点完保存就会去
 * 试一次请求,看到的还是旧密码那次失败。顺带把「只试一次」的闸重新开一次。
 */
export async function setProxyPassword(password: string): Promise<ProxyPasswordInfo> {
  const host = getHost()
  const trimmed = password.trim()
  if (trimmed === '') throw new Error('密码不能是空的。要清除请用「清除」。')

  await host.secrets.set(PROXY_PASSWORD_REF, trimmed)
  triedKey = null
  return { hasKey: true, encryptionAvailable: host.secrets.available() }
}

export async function clearProxyPassword(): Promise<ProxyPasswordInfo> {
  removeCredential(PROXY_PASSWORD_REF)
  triedKey = null
  return { hasKey: false, encryptionAvailable: getHost().secrets.available() }
}

/** ★ 只回「有没有」,回不了「是什么」—— 没有任何一条频道能把明文取出去 */
export async function getProxyPasswordInfo(): Promise<ProxyPasswordInfo> {
  const host = getHost()
  const v = await host.secrets.get(PROXY_PASSWORD_REF)
  return { hasKey: v !== null && v !== '', encryptionAvailable: host.secrets.available() }
}
