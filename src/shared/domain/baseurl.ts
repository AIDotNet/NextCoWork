/**
 * Base URL 的归一化、拼接与体检 —— 方案 §5.2 / §7。
 *
 * ★ **`joinUpstreamUrl` 从 `main/kernel/upstream/canonical.ts` 搬到了这里,
 * 那边改成再导出。** 搬家的理由是 canonical.ts 自己写下的那句约束:
 * 「设置页必须把最终拼出的 URL 显示给用户确认……收口到这一个函数,是为了那一行
 * 显示和实际请求用的是同一份逻辑」—— 而渲染层 import 不到 `main/`。
 * 留在那边的话,那一行回显只能**再写一遍**同样的启发式,于是它和真实请求
 * 迟早会分叉,而分叉的表现是「界面显示的地址是对的,发出去的不是」。
 */
import type { UpstreamProtocol } from './provider'

/**
 * ⚠️ 协议 §8 的 URL 拼接规则是个**会咬人的启发式**,所以只写这一遍。
 *
 * 规则:`<baseUrl>` + `<path>`;若两边都带 `/v1`,只保留一份。
 *
 * 那个去重分支**实际上只服务 Anthropic 协议**(见 `REQUEST_PATH` 上面那张表:
 * OpenAI 族的 path 里根本没有 `/v1`)。它兜的是用户手填 `.../anthropic/v1` ——
 * 一个很自然、但会拼出 `/v1/v1/messages` 的写法。
 *
 * 因为规则本身不可能对所有 baseUrl 都正确,设置页必须把**最终拼出的 URL**
 * 显示给用户确认(方案 §5.2)。
 */
export function joinUpstreamUrl(baseUrl: string, path: string): string {
  const base = baseUrl.replace(/\/+$/, '')
  const p = path.startsWith('/') ? path : `/${path}`
  if (base.endsWith('/v1') && p.startsWith('/v1/')) return base + p.slice('/v1'.length)
  return base + p
}

/**
 * 每种协议实际要打的路径。编码器(`enc.path`,`router.ts:215`)和「最终 URL」回显
 * 共用这一份。
 *
 * ★★ **注意两族协议的版本段约定是反的,这不是笔误。**
 *
 * - **Anthropic**:base **不带** `/v1`,客户端自己补 `/v1/messages`。
 *   官方、智谱、Moonshot、DeepInfra、Fireworks 全是这个形状,Fireworks 的文档
 *   还专门写了 *"...(without the /v1 suffix)"*。
 * - **OpenAI 族**:版本段是 **base 的一部分**,路径只有 `/chat/completions`。
 *
 * 第二条一开始写错了(写成 `/v1/chat/completions`),而错的代价被调研报告
 * `…-agent-a47e66b04e7079eb0.md` §3.1 量出来了:**OpenAI 兼容层的版本段每家都不一样**,
 * 只有一小半是 `/v1`。
 *
 * | 形状 | 厂商 |
 * |---|---|
 * | `/v1` | OpenAI、Moonshot、MiniMax、混元、StepFun、百川、Mistral、xAI、Together、硅基流动、Requesty |
 * | 别的前缀 | Groq `/openai/v1`、Fireworks `/inference/v1`、**DeepInfra `/v1/openai`**、OpenRouter `/api/v1`、**智谱 `/api/paas/v4`**、**火山 `/api/v3`**、百炼 `/compatible-mode/v1`、**千帆 `/v2`**、Cohere `/compatibility/v1` |
 * | 尾斜杠 | **Gemini `/v1beta/openai/`** |
 *
 * 拼 `/v1/chat/completions` 只对第一行成立;加粗那五家会拼出
 * `…/api/paas/v4/v1/chat/completions` 这种**多一段版本**的地址,全部 404。
 * 而 `joinUpstreamUrl` 的去重分支只认**恰好以 `/v1` 结尾**,救不了它们。
 *
 * 所以这里不是「Gemini 一个例外」,是**一整族的约定**。改成 `/chat/completions`
 * 之后那五家和 Gemini 一起对了,去重分支也就只剩 Anthropic 那一条路在用 ——
 * 正是它当初被写出来的理由。
 *
 * 代价:用户手填一个**光秃秃的域名**(`https://api.openai.com`)时会拼出
 * 少了 `/v1` 的地址。这个由下面 `baseUrlWarnings` 的 `openai-missing-version` 兜住 ——
 * 预设填进来的地址都是带版本段的,手填才可能缺。
 */
export const REQUEST_PATH: Readonly<Record<UpstreamProtocol, string>> = {
  anthropic: '/v1/messages',
  'openai-chat': '/chat/completions',
  'openai-responses': '/responses'
}

/** 设置页那一行回显。和真实请求走同一个 `joinUpstreamUrl`。 */
export function previewUrl(baseUrl: string, protocol: UpstreamProtocol): string {
  return joinUpstreamUrl(baseUrl, REQUEST_PATH[protocol])
}

/**
 * 用户可能整条请求地址粘进来。这些后缀出现在末尾时削掉。
 *
 * ★ 表里**只有真实的端点名**。别往里加 `/v1` 或 `/api` 这类**路径前缀** ——
 * 方案 §5.2 点名的坑正是 `https://x.com/api/v1beta` 被当成完整请求地址削成
 * `https://x.com/api`,而那是个合法的、用户手打的 Base URL。
 */
const ENDPOINT_SUFFIXES: readonly string[] = [
  '/chat/completions',
  '/completions',
  '/responses',
  '/messages',
  '/embeddings',
  '/models'
]

/**
 * ★ Gemini 的 OpenAI 兼容层官方示例写的就是带尾斜杠的
 * `https://generativelanguage.googleapis.com/v1beta/openai/`,所以这里**不削**它 ——
 * 用户从 Google 文档里抄来什么,失焦后还是什么,不会觉得表单把地址改坏了。
 *
 * ⚠️ **保住尾斜杠这件事本身,对我们的拼接没有任何影响。**
 * 「剥掉尾斜杠会 404」那条说的是 OpenAI **SDK** 的 urljoin 语义;
 * `joinUpstreamUrl` 是字符串拼接,尾不尾斜杠拼出来一样。所以这一条例外
 * 买到的只有一样东西 —— **用户从 Google 文档里抄来什么,失焦后还是什么**。
 * 它是个体验决定,不是正确性修复,别把它当成 Gemini 能通的理由。
 *
 * Gemini 真正会 404 的那处**已经修好了**:曾经 `REQUEST_PATH` 把版本段写进
 * path,于是这里会拼出 `/v1beta/openai/v1/chat/completions`(多一段 `/v1`)。
 * 后来发现那不是 Gemini 一家的例外,而是整个 OpenAI 族的约定被写反了 ——
 * 见 `REQUEST_PATH` 上面那张各家前缀表。
 */
const KEEPS_TRAILING_SLASH = /\/v1beta\/openai\/?$/

/**
 * 失焦时跑一遍:整条请求地址 → Base URL。
 *
 * 拿不准的一律**原样返回** —— 这个函数的错误代价是不对称的:
 * 少削一段用户自己看得见也改得动,多削一段会把一个本来对的地址改错,
 * 而用户会以为是我们的请求实现有问题。
 */
export function normalizeBaseUrl(input: string): string {
  const trimmed = input.trim()
  if (trimmed === '') return ''

  /*
    ★ 只在**看着像主机名**时才补协议头。无条件补的话
    `new URL('https://这不是地址')` 是**成功**的(IDN 会被 punycode 编码),
    于是一句随手打的中文会变成一个像模像样的地址,
    而用户下一步只会看到一个莫名其妙的连接失败。
  */
  const host = trimmed.split(/[/?#]/)[0] ?? ''
  const looksLikeHost = host.includes('.') || /^(localhost|\[?::1\]?)(:\d+)?$/.test(host)
  const withScheme =
    /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) || !looksLikeHost
      ? trimmed
      : `${isLoopback(trimmed) ? 'http' : 'https'}://${trimmed}`

  let url: URL
  try {
    url = new URL(withScheme)
  } catch {
    return trimmed // 不是个 URL 就别动它,让「测试连接」去报错
  }

  // 查询串和锚点在 Base URL 里没有意义,而粘贴来的地址常带着它们
  url.search = ''
  url.hash = ''

  let path = url.pathname
  for (const suffix of ENDPOINT_SUFFIXES) {
    if (path.endsWith(suffix)) {
      path = path.slice(0, -suffix.length)
      break
    }
  }
  // Gemini 那个形状要**补**回尾斜杠,不只是「不削」—— 削掉端点后缀之后它本来就没了
  path = KEEPS_TRAILING_SLASH.test(path) ? `${path.replace(/\/+$/, '')}/` : path.replace(/\/+$/, '')

  url.pathname = path
  // URL 会把空路径序列化成尾斜杠,而 `https://a.com/` 和 `https://a.com` 该是一个东西
  return path === '' ? url.toString().replace(/\/$/, '') : url.toString()
}

const isLoopback = (s: string): boolean => /^(127\.0\.0\.1|localhost|\[?::1\]?)([:/]|$)/.test(s)

export interface BaseUrlWarning {
  kind: 'anthropic-v1-suffix' | 'openai-missing-version' | 'localhost' | 'insecure'
  message: string
}

/**
 * 表单下方那条提示。**返回警告不是拒绝** —— 这些形状都可能是对的,
 * 只是十有八九不是,而用户此刻还改得动。
 */
export function baseUrlWarnings(baseUrl: string, protocol: UpstreamProtocol): BaseUrlWarning[] {
  const out: BaseUrlWarning[] = []
  const base = baseUrl.replace(/\/+$/, '')
  if (base === '') return out

  /*
    ★ Anthropic 兼容端点一律**不带** `/v1` —— 客户端自己会拼 `/v1/messages`。
    用户手填 `.../anthropic/v1` 会得到 `/v1/v1/messages` → 404。
    Fireworks 的官方文档专门写了这句:"...without the /v1 suffix."

    注意判据是 `joinUpstreamUrl` 的**实际输出**,不是又猜一遍规则 ——
    那个函数对 `/v1` 结尾有专门的去重分支,重写一遍判定必然会和它分叉。
  */
  if (protocol === 'anthropic' && base.endsWith('/v1')) {
    out.push({
      kind: 'anthropic-v1-suffix',
      message: `Anthropic 兼容端点通常不带 /v1 结尾,客户端会自己补 /v1/messages。当前会请求 ${previewUrl(baseUrl, protocol)}`
    })
  }

  // 形状同 `normalizeBaseUrl`:catch 直接 return,所以不需要 `| null` 的初值
  let url: URL
  try {
    url = new URL(base)
  } catch {
    return out
  }

  /*
    ★ OpenAI 族的**版本段属于 base**(见 `REQUEST_PATH`)。所以一个光秃秃的域名
    几乎一定是漏了 —— 报告里 30 多家 OpenAI 兼容层,**没有一家**的 base 是裸域名。

    只在路径为空时提示,不去猜「像不像版本段」:`/api/paas/v4`、`/inference/v1`、
    `/compatible-mode/v1`、`/v1beta/openai/` 各不相同,猜一遍必然误报,
    而一条天天误报的提示等于没有提示。
  */
  if (protocol !== 'anthropic' && (url.pathname === '' || url.pathname === '/')) {
    out.push({
      kind: 'openai-missing-version',
      message: `OpenAI 兼容地址通常要带版本段(多数是 /v1)。当前会请求 ${previewUrl(baseUrl, protocol)}`
    })
  }

  /*
    ★ 一律写 127.0.0.1 而不是 localhost —— 和网关那条同一个理由:
    Node 17+ 不再重排 DNS 结果,`localhost` 可能解析到 `::1`,
    而这些本地运行时默认只监听 IPv4。表现是「浏览器能开、应用里 ECONNREFUSED」。
  */
  if (url.hostname === 'localhost') {
    out.push({
      kind: 'localhost',
      message: '建议改用 127.0.0.1:localhost 可能解析到 ::1,而多数本地运行时只监听 IPv4'
    })
  }

  if (url.protocol === 'http:' && !isLoopback(url.hostname)) {
    out.push({ kind: 'insecure', message: 'http 明文传输,API 密钥会暴露在链路上' })
  }

  return out
}
