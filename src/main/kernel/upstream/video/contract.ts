/**
 * 视频适配器的**契约** —— 「把一次动作变成 HTTP 请求、把响应读回统一形状」。
 *
 * ★★ 与生图桥(`kernel/image-gen.ts`)最根本的区别:**这里没有"一次调用拿到结果"
 * 这回事。** 视频全是异步任务:创建 → 轮询 → 取结果,而且要跨进程重启。
 * 所以契约按这三步分开,而不是一个 `generate()` —— 合并的话,恢复逻辑就必须
 * 在适配器里再写一遍,于是每接一家都要把"任务 id 存在哪、状态字段叫什么"重写一次。
 *
 * ★ 适配器是**纯函数 + 注入的 fetch**:不碰 store、不碰 electron、不碰数据库。
 * 于是 14 家的请求形状全部可以在 node 环境的 vitest 里钉住 —— 那是这批代码
 * 唯一能在没有真实密钥的情况下被验证的方式。
 */

import type { ProviderCredential } from '../../../../shared/domain/credential'
import type {
  VideoAction,
  VideoAssetRef,
  VideoCloudStatus,
  VideoParameterSpec,
  VideoProfile
} from '../../../../shared/domain/video-generation'
import type { UpstreamProvider } from '../../../../shared/domain/provider'

/**
 * 一次动作的**规范化入参**。
 *
 * ★ 它是"我们已经校验过的东西":参数合法性由 `validateVideoRequest` 在这之前
 * 判完,适配器只管把它翻译成这家要的形状。这样 14 份适配器里没有一处需要重复
 * "3 秒不合法"这种判断 —— 而那种判断重复 14 遍必然会有几家写错。
 */
export interface VideoRequest {
  action: VideoAction
  prompt: string
  /** 上游模型名 */
  model: string
  /** 已解析好的起始帧(A 面是 NCW 图 → 字节,或一家只收 URL 的公网地址) */
  image?: VideoRemoteMedia
  lastFrame?: VideoRemoteMedia
  /** 编辑/延长的源视频。**只可能是公网 URL**(见 video-generation.ts) */
  video?: { url: string }
  duration?: number | string
  aspectRatio?: string
  resolution?: string
  seed?: number
  audio?: boolean
}

/**
 * 交给上游的一段媒介。
 *
 * ★ `bytes` 与 `url` 并列而不是"统一成 URL":有的家(fal 的 CDN、Replicate 的
 * data URL)接受内联字节,而多一次"先传上去换个链接"的往返既是延迟也是**另一件
 * 要设计的事**(谁保管、多久过期)。所以按各家的官方能力二选一。
 */
export interface VideoRemoteMedia {
  mime: string
  dataRef?:
    | { kind: 'url'; url: string }
    | { kind: 'bytes'; bytes: Uint8Array }
}

/** 创建后拿到的任务句柄。★ `upstreamId` 必须落库 —— 恢复全靠它。 */
export interface VideoCreateResult {
  upstreamId: string
  status: VideoCloudStatus
  /**
   * 查询/取消这条任务时**还需要**的那一段路径。
   *
   * ★★ 存在是因为**商城类适配器**(fal)的任务身份是"endpoint + request_id"一对:
   *   `queue.fal.run/fal-ai/veo3/requests/{id}/status` 里那个 endpoint 段不在
   *   响应里,只在**提交时**用的那个地址里。不把它存下来,恢复查询时就拼不出 URL,
   *   而"拿模型别名去当 endpoint"在用户改过绑定之后会指向另一条队列 ——
   *   症状是 404,或者更糟:查到**另一个模型**的状态。
   * ★ 对绝大多数家它是 undefined(任务 id 自己就够)。
   */
  route?: string
  /** 有些家创建时就给了预计时长(秒) */
  estimatedSeconds?: number
}

/** 查询结果。`assets` 只在成功时有值,且**只是 URL**,不是已下载的文件。 */
export interface VideoStatusResult {
  status: VideoCloudStatus
  /** 真实的百分比,没有就不给(不编一个假的进度) */
  percent?: number
  /** 阶段文案 key 的参数字段(领域值,不在这里翻译) */
  stage?: string
  /** 成品媒材地址(可能带鉴权头,见 `headers`) */
  assets?: readonly VideoRemoteAsset[]
  /** 失败原因(上游原话,截断过) */
  error?: string
  /** 上游报的结果过期时刻 */
  expiresAt?: number
}

export interface VideoRemoteAsset {
  url: string
  mime?: string
  /** 下载这个 URL 要带的头(如 Replicate 的 `Authorization`)。**只在这一跳带** */
  headers?: Record<string, string>
  /** 从响应里读到的字节数(有的话) */
  size?: number
  kind?: 'video' | 'poster'
}

export interface VideoAdapterContext {
  provider: UpstreamProvider
  profile: VideoProfile
  credential: ProviderCredential
  fetch: typeof fetch
  signal: AbortSignal
  /** 本次 HTTP 请求的超时(毫秒)。**不是整个生成的超时** —— 见文件头 */
  requestTimeoutMs: number
  /** 读一读有没有可用的翻墙/代理头;今天只有 AWS 与腾讯用到 */
  region?: string
  s3?: { bucket: string; prefix?: string; region?: string }
}

/**
 * 一家适配器要实现的东西。
 *
 * ★ `create` 必须是**可安全重试判断**的:返回值里带 `status: 'unknown'` 表示
 * "请求发出去了但拿不准上游收没收到",调用方据此**绝不自动重发**。
 */
export interface VideoAdapter {
  id: string
  create(ctx: VideoAdapterContext, request: VideoRequest): Promise<VideoCreateResult>
  /**
   * 查询任务。
   *
   * ★ `route` 是创建时存下来的那段路(见 `VideoCreateResult.route`)——
   *   fal 这类商城靠它拼 URL;其余家忽略它。**不从 `request.model` 现推**:
   *   用户可能改过绑定,那时别名指向的是另一条队列。
   */
  status(ctx: VideoAdapterContext, upstreamId: string, route?: string): Promise<VideoStatusResult>
  /** 上游提供了明确取消操作才有;缺席 = 这家不支持取消 */
  cancel?(ctx: VideoAdapterContext, upstreamId: string, route?: string): Promise<void>
  /**
   * 该动作的参数约束(有些家按动作给不同形状,而 profile 里那份是"目录级"的)。
   * 缺席 = 用 profile 上那份。
   */
  parametersFor?(profile: VideoProfile, action: VideoAction): VideoParameterSpec | undefined
}

/** 把上游的 4xx/5xx 变成一句**带状态码与原文**的失败原因。 */
export function httpFailure(status: number, body: string, limit = 300): string {
  const flat = body.replace(/\s+/gu, ' ').trim()
  const clipped = flat.length <= limit ? flat : `${flat.slice(0, limit)}…`
  return `HTTP ${String(status)} ${clipped}`.trim()
}

/**
 * 读一段有上限的响应体。
 *
 * ★★ 视频接口的**错误**响应也可能很大(有的家回一个带 base64 帧的 JSON),
 * 而这里只要头几百字节。无上限地 `.text()` 会让一个 4xx 把整个主进程的内存吃掉。
 */
export async function readTextCapped(res: Response, maxBytes = 64 * 1024): Promise<string> {
  const body = res.body
  if (body === null) return ''
  const reader = body.getReader()
  const chunks: Uint8Array[] = []
  let total = 0
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    if (value === undefined) continue
    total += value.byteLength
    if (total > maxBytes) {
      await reader.cancel().catch(() => {})
      break
    }
    chunks.push(value)
  }
  const out = new Uint8Array(total)
  let at = 0
  for (const chunk of chunks) {
    out.set(chunk, at)
    at += chunk.byteLength
  }
  return new TextDecoder().decode(out)
}

/** 安全 JSON 解析:坏响应答 `null`,由调用方翻成"这家回的东西读不懂"。 */
export async function readJson(res: Response): Promise<unknown> {
  const text = await readTextCapped(res)
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}

export function recordOf(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined
}

export function arrayOf(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

export function strOf(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

export function numOf(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/** 从任意嵌套里按点路径取值 —— 各家响应形状差别很大,这是唯一一个通用读取器。 */
export function dig(root: unknown, path: string): unknown {
  let current: unknown = root
  for (const key of path.split('.')) {
    const obj = recordOf(current)
    if (obj === undefined) return undefined
    current = obj[key]
  }
  return current
}

/** `assets` 为空时明确报错,不返回一个"成功但没有媒材"的结果。 */
export function requireAsset(assets: readonly VideoRemoteAsset[] | undefined, where: string): readonly VideoRemoteAsset[] {
  if (assets === undefined || assets.length === 0) {
    throw new Error(`${where}: the response reported success but carried no video URL`)
  }
  return assets
}

/**
 * 把一段媒介翻成**能放进 JSON body 的东西**。
 *
 * ★★ 两种形态是一道真实的分叉,不是实现细节:
 *   - `{ url }` —— 公网地址。**首选**,因为上游看到的就是用户给的那份字节。
 *   - `data:<mime>;base64,…` —— 内联。会话图片(`ncw://`)与工作区图片走这条:
 *     它们**没有**公网地址,而"帮用户上传到某个图床换个链接"是另一个功能
 *     (以及另一个隐私问题,见 `video-generation.ts` 的编辑/延长那段)。
 *
 * ★ 但**不是每家都收内联**。只收 CDN URL 的那些(Luma 旧版明确写了)必须
 * 明确拒绝并说清原因,而不是发一个它读不懂的 data URL 换回一句 400。
 */
export function mediaForBody(
  media: { mime: string; dataRef?: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } },
  opts: { inlineDataUrls: boolean; where: string }
): string {
  const ref = media.dataRef
  if (ref === undefined) throw new Error(`${opts.where}: the media carried no content`)
  if (ref.kind === 'url') return ref.url
  if (!opts.inlineDataUrls) {
    throw new Error(
      `${opts.where} requires a public http(s) URL for its images; this one has no public address ` +
        '(conversation images and workspace files are not uploaded anywhere on your behalf).'
    )
  }
  return `data:${media.mime};base64,${Buffer.from(ref.bytes).toString('base64')}`
}

/** 只认 url 的那些家的取法(拿不到就交给 `mediaForBody` 去报错)。 */
export function mediaUrlOnly(media: { dataRef?: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } }): { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } | undefined {
  return media.dataRef
}

export function assetOfRef(url: string, mime?: string): VideoRemoteAsset {
  return { url, ...(mime === undefined ? {} : { mime }) }
}

/** 供契约校验:一个已解析的素材地址。 */
export type VideoAssetUrl = VideoAssetRef['url']
