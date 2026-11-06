/**
 * `WebFetch` 的目标地址筛查 —— **纯函数**,零 IO,所以能把边界铺满。
 *
 * ## 防的是什么
 *
 * 模型给一个 URL,我们就去请求它。这台机器上能访问、而外网访问不到的东西,
 * 恰恰是最敏感的那一批:
 *
 * - `http://169.254.169.254/latest/meta-data/iam/…` —— 云厂商的实例元数据端点,
 *   一个 GET 就能拿到临时凭证。这是 SSRF 里最经典也最值钱的那个目标。
 * - `http://127.0.0.1:<port>` —— 用户本机跑着的调试端口、数据库管理面板、
 *   没有鉴权的内部服务。
 * - `http://192.168.x.x` / `10.x.x.x` —— 家里或公司内网的路由器、NAS、打印机。
 *
 * 而这个 URL 可能根本不是用户给的:它可以来自一个被投毒的网页、一条 Skill 正文、
 * 一个 MCP 工具的返回值。所以这道筛查不能指望「模型不会去请求奇怪的地址」。
 *
 * ## 为什么必须自己解析,而不是只看字符串
 *
 * `http://2130706433/` 和 `http://0177.0.0.1/` 都是 `127.0.0.1`。好消息是
 * WHATWG 的 `URL` 会把它们**全部规范化成点分十进制**(IPv4 映射的 IPv6 会规范化成
 * `[::ffff:7f00:1]` 这种十六进制形式),所以只要基于 `url.hostname` 判断,
 * 那一整类编码花招就自动没了 —— 但前提是**判断 IPv6 时要真的去解析它**,
 * 而不是拿字符串跟 `'::1'` 比。
 *
 * ## 这道闸拦不住什么(照实记下来)
 *
 * **DNS 重绑定**:`evil.com` 第一次解析成公网 IP、第二次解析成 `127.0.0.1`。
 * 字面量筛查看不见它,要靠调用方自己在拿到解析结果后再过一遍 `isPrivateAddress`
 * (`resolvedAddressRisk` 就是干这个的),而那一层和真正发请求之间仍然有时间窗。
 * 彻底解决要把连接固定到已校验的 IP 上(自定义 agent + `lookup`),不在这一批。
 *
 * ## 主机名判断可以被调用方关掉
 *
 * `ssrfRisk` 第二个参数 `{ allowPrivateAddresses: true }` 会跳过环回/私网/本地
 * 域名后缀那一段判断,只留协议白名单和凭证拦截。`browser.ts`、`web.ts`(即
 * `WebFetch`、浏览器工具那一族)按工作区所有者的要求传了这个选项,因此不再
 * 拦截本机和内网地址——具体动机和风险写在 `SsrfRiskOptions` 上。`search/builtin/*`
 * 没有传,继续拦截。
 */

import { promises as dns } from 'node:dns'

/** 4 个 8 位段 → 一个 32 位无符号数。不是合法 IPv4 就返回 `null`。 */
function parseIpv4(host: string): number | null {
  const parts = host.split('.')
  if (parts.length !== 4) return null
  let v = 0
  for (const p of parts) {
    if (!/^\d{1,3}$/.test(p)) return null
    const n = Number(p)
    if (n > 255) return null
    v = v * 256 + n
  }
  return v
}

/** IPv6 文本 → 8 个 16 位段。带 `::` 的缩写会展开。不合法返回 `null`。 */
function parseIpv6(text: string): number[] | null {
  const halves = text.split('::')
  if (halves.length > 2) return null

  const toGroups = (s: string): number[] | null => {
    if (s === '') return []
    const out: number[] = []
    for (const g of s.split(':')) {
      // 末尾可以是点分十进制(`::ffff:127.0.0.1`),占两个 16 位段
      if (g.includes('.')) {
        const v4 = parseIpv4(g)
        if (v4 === null) return null
        out.push((v4 >>> 16) & 0xffff, v4 & 0xffff)
        continue
      }
      if (!/^[0-9a-fA-F]{1,4}$/.test(g)) return null
      out.push(parseInt(g, 16))
    }
    return out
  }

  const head = toGroups(halves[0] ?? '')
  if (head === null) return null
  if (halves.length === 1) return head.length === 8 ? head : null

  const tail = toGroups(halves[1] ?? '')
  if (tail === null) return null
  const fill = 8 - head.length - tail.length
  if (fill < 0) return null
  return [...head, ...Array(fill).fill(0), ...tail]
}

/**
 * 这个地址是不是「不该让模型去请求」的那一类。
 *
 * 入参可以是 `url.hostname` 给出的任何形式:点分十进制、`[...]` 包着的 IPv6,
 * 或者一个域名(域名一律返回 false —— 域名要交给调用方另外判断)。
 */
export function isPrivateAddress(host: string): boolean {
  const bare = host.startsWith('[') && host.endsWith(']') ? host.slice(1, -1) : host

  const v4 = parseIpv4(bare)
  if (v4 !== null) return isPrivateV4(v4)

  const v6 = parseIpv6(bare)
  if (v6 === null) return false

  // ::ffff:a.b.c.d —— IPv4 映射地址,按它内嵌的那个 v4 判
  if (v6.slice(0, 5).every((g) => g === 0) && v6[5] === 0xffff) {
    return isPrivateV4(((v6[6] ?? 0) << 16) | (v6[7] ?? 0))
  }

  if (v6.every((g) => g === 0)) return true // ::
  if (v6.slice(0, 7).every((g) => g === 0) && v6[7] === 1) return true // ::1
  const first = v6[0] ?? 0
  if ((first & 0xfe00) === 0xfc00) return true // fc00::/7 唯一本地
  if ((first & 0xffc0) === 0xfe80) return true // fe80::/10 链路本地
  if ((first & 0xff00) === 0xff00) return true // ff00::/8 组播
  return false
}

function isPrivateV4(v: number): boolean {
  const a = (v >>> 24) & 0xff
  const b = (v >>> 16) & 0xff
  const c = (v >>> 8) & 0xff
  if (a === 0) return true // 0.0.0.0/8
  if (a === 10) return true // 私网
  if (a === 127) return true // 环回
  if (a === 100 && b >= 64 && b <= 127) return true // 100.64/10 运营商级 NAT
  if (a === 169 && b === 254) return true // ★ 链路本地 —— 云元数据 169.254.169.254
  if (a === 172 && b >= 16 && b <= 31) return true // 私网
  if (a === 192 && b === 0 && c === 0) return true // 192.0.0/24 IETF 协议专用(★ 是 /24,不是 /16)
  if (a === 192 && b === 168) return true // 私网
  if (a === 198 && (b === 18 || b === 19)) return true // 基准测试专用
  if (a >= 224) return true // 组播 + 保留(含 255.255.255.255)
  return false
}

/**
 * 只在本机 / 内网有意义的域名后缀。
 *
 * ★ 还要挡**单标签主机名**(不含点的,像 `router`、`nas`)—— 那种名字只可能靠
 * 内网的 DNS 搜索域解析出来,一定指向内网设备。
 */
const LOCAL_SUFFIXES = ['.localhost', '.local', '.internal', '.intranet', '.home.arpa', '.lan']

export interface SsrfRiskOptions {
  /**
   * 需求：工作区所有者在 2026-09-20 明确要求 `WebFetch` / 浏览器工具能够触达
   * 用户自己机器上的本地服务（localhost、内网设备），并在确认过下面这条风险后
   * 仍要求继续——传 `true` 时跳过本函数里「主机是不是环回/私网地址」这一段判断。
   *
   * ★ 风险如实记下：这个 URL 不一定是用户自己敲的，也可能来自一段被投毒的网页
   * 正文、一条 Skill 正文、一个 MCP 工具的返回值。放开之后，被投毒的内容可以
   * 诱导模型去请求本机没鉴权的调试端口、内网设备，或云厂商的实例元数据端点
   * （`169.254.169.254`）—— 且不会再被这里拦下并报错。
   *
   * 协议白名单和 URL 内嵌凭证（`user:pass@`）的拦截和「是不是内网」无关，不受
   * **这个**开关影响：凭证拦截任何调用方都躲不过去；协议白名单的唯一例外是下面
   * 另一个开关 `allowFileUrls`（2026-09-22 新增，仅浏览器工具传）——原先这里写的是
   * 「任何调用方都躲不过去」，那句话在协议这一半上已经不成立，所以改成指名例外，
   * 免得后来者以为协议闸是铁板一块、去别处重复实现一遍。
   *
   * 内置搜索（`search/builtin/*`）抓的是任意搜索结果域名，暴露面比用户主动指挥的
   * 浏览器工具/WebFetch 大得多，所以它没有传这个选项，继续拦截私网地址。
   */
  allowPrivateAddresses?: boolean

  /**
   * 需求：工作区所有者要求浏览器工具能打开 `file://` 本地页面（2026-09-22，
   * 与放开内网地址是同一批要求）——本地生成的 HTML 报告要能直接进右侧工作台看。
   * 传 `true` 时协议白名单**只**额外放行 `file:`；`javascript:`、`data:`、`ftp:`
   * 等其余协议照旧拒绝。
   *
   * ★ 风险如实记下：`file://` 让模型指定的任意本地文件被渲染进隔离 webview，
   * 页面内容随后经 `browser_snapshot` / 截图回到上下文 —— 相当于绕过 Read 的
   * 提示把任意本地文件读进对话。地址一样可能不是用户给的（被投毒的正文、
   * MCP 返回值）。目前只传给 `browser.ts`（浏览器工具那一族）；`WebFetch`
   * （`web.ts`）与内置搜索不传，`file://` 在那边仍然指向 Read。
   */
  allowFileUrls?: boolean
}

/** 有风险就返回给模型看的说明,没有就返回 `null`。 */
export function ssrfRisk(url: URL, options: SsrfRiskOptions = {}): string | null {
  const isFile = url.protocol === 'file:'
  const fileAllowed = isFile && options.allowFileUrls === true
  if (url.protocol !== 'http:' && url.protocol !== 'https:' && !fileAllowed) {
    return (
      `Only ${options.allowFileUrls === true ? 'http, https and file' : 'http and https'} are supported, ` +
      `not "${url.protocol}". ` +
      (options.allowFileUrls === true
        ? ''
        : `To read a local file use Read — do not reach for file:// style addresses.`)
    )
  }

  /*
    ★ URL 里带用户名密码的话,凭证会随请求发出去。模型不该有能力把一份凭证
    塞进一次它自己发起的请求里 —— 那是一条现成的外发通道。
    ★ 不受 allowFileUrls 影响:file://user:pass@ 同样拒。
  */
  if (url.username !== '' || url.password !== '') {
    return 'The URL must not carry a username or password. Remove the "user:pass@" part and try again.'
  }

  /*
    ★ file:// 没有「主机」可言 —— 环回/私网那套判断对它无意义(hostname 是空串,
    走到下面会被 refuseLocal('') 错拒)。能走到这一行说明调用方已经用
    allowFileUrls 明确要了 file://,直接放行。
  */
  if (isFile) return null

  if (options.allowPrivateAddresses === true) return null

  const host = url.hostname.toLowerCase()

  if (host === 'localhost' || isPrivateAddress(host)) return refuseLocal(host)
  if (LOCAL_SUFFIXES.some((s) => host.endsWith(s))) return refuseLocal(host)
  if (!host.includes('.') && !host.startsWith('[')) return refuseLocal(host)

  return null
}

/**
 * 对域名做一次尽力而为的 DNS 层筛查:域名解析出来的地址也得是公网的。
 *
 * `ssrfRisk` 负责 URL 字面量，不能发现 `public.example` 解析到 `127.0.0.1`
 * 的情况。`browser.ts`/`web.ts` 目前都传了 `allowPrivateAddresses: true`（见
 * `SsrfRiskOptions`），私网地址本身已经放行，所以它们不再调用这个函数 ——
 * 但函数本体保留、测试保留：一旦某个调用方需要「域名允许，但解析到内网就该拦」
 * 这种更细的策略，可以直接复用，不必重写。
 */
export async function resolvedAddressRisk(
  hostname: string,
  timeoutMs = 1500,
  lookup: (host: string, options: { all: true }) => Promise<Array<{ address: string }>> = dns.lookup
): Promise<string | null> {
  let addresses: Array<{ address: string }>
  try {
    addresses = await Promise.race([
      lookup(hostname, { all: true }),
      new Promise<never>((_, reject) => setTimeout(() => reject(new Error('dns timeout')), timeoutMs))
    ])
  } catch {
    return null
  }
  const privateAddress = addresses.find((entry) => isPrivateAddress(entry.address))
  if (privateAddress === undefined) return null
  return (
    `Refusing to reach "${hostname}": it resolves to a loopback or private-network address ` +
    `(${privateAddress.address}).`
  )
}

function refuseLocal(host: string): string {
  return (
    `Refusing to reach "${host}": this is a loopback or private-network address, and network tools may ` +
    `only reach the public internet. Addresses like this usually sit in front of unauthenticated debug ` +
    `ports, internal devices, or a cloud provider's instance metadata endpoint — not something a ` +
    `model-initiated request should touch. If a local service really is the target, ask the user to open ` +
    `it themselves, or to explicitly ask you to use Bash for it.`
  )
}
