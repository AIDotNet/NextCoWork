/*
 * 对话内生图 / 改图的宿主通道 —— `generate_image` 工具与「哪家供应商、哪把密钥」
 * 之间的那条缝。
 *
 * 需求:设置页「图片生成」tab 里配好的生图供应商,在对话里要真的能用:
 * 1. 文生图:用户说「画一张」,模型调 `generate`;
 * 2. 改图:用户说「把这张改成…」,模型调 `edit`。源图可以是会话附件
 *    (`ncw://`,经 `resolveImage` 走**与上游请求同一条**安全校验)、
 *    转录里内联的 data URL、工作区里的一张图或一个 http(s) 图片 URL
 *    (后两者由工具侧先落成 data URL 传来,见 `tool/builtin/image.ts`)。
 *
 * ★★ 用哪个模型是**用户在图片页点名的**(`AppSettings.imageModel` 那一对),
 * 必选、不设自动档:只用这一个绑定,失败就是失败。原先「按 priority 逐家兜底」
 * 的行为已随这个决定移除 —— 跨家兜底的症状是「我明明点名了 A,画出来的却是
 * B 家的」,而两家计费不同;没选过模型则 `available()` 为 false,工具不下发。
 *
 * ★ 形状同 `scheduling` / `shells` 两条桥:内核只认 `ImageGenBridge` 这个窄接口,
 * 「store 在哪、密钥怎么读、dataRef 怎么解析、请求怎么发」全部由注入给出 ——
 * 内核因此保持零 electron、拿假实现就能单测(见 `SessionDeps` 上那段同款说明)。
 *
 * ★★ **故意不做的事:**
 * - 不做按张计费的额度控制 —— 生图按张/分辨率计价,`TokenRates` 表达不了
 *   (定价种子表里没有对应行,见 doubao seedream 那几行的注释),现阶段信任
 *   用户的「对话生图」开关(`AppSettings.imageGenerationEnabled`)与权限档位。
 *   ★ 它**不**受输入框「联网搜索」开关管 —— 那颗只管网页搜索与抓取
 *   (`permission-gate.ts` 的 `NETWORK_SWITCH_TOOLS`);生图的出网去的是用户自己
 *   在设置里配的供应商,和对话请求本身是同一性质。
 * - 不做流式进度。一张图一次返回,中间没有模型可读的中间态,进度只会是假的。
 *   (一次要多张时会**按张**回调 `onImage` —— 那是真实的「这一张到了」,不是
 *   单张内部的假进度,所以不违背这一条。)
 * - 多张不走上游的 `n` 参数,而是**逐张并发**请求 —— 见 `run` 里那段理由。
 *   代价是按张计费的张数 = 请求数,上限由 `MAX_IMAGE_COUNT` 卡住。
 * - 不看供应商的 chat 协议(`openai-chat` / `anthropic` …):生图/改图走事实标准
 *   `{baseUrl}/images/generations|edits`(OpenAI 兼容,xAI 文档同样是这个形状)。
 *   配错的上游会以 HTTP 状态的形式落进失败原因,不会打崩对话。
 * - 不拿 `capabilities.visionInput` 当改图的闸 —— 目录里那一位对生图模型普遍
 *   没标(豆包 seedream 就是),拿它过滤的症状是「明明能改图的模型说没有候选」。
 *   能不能改由上游自己答(4xx 落进 reasons)。
 */
import { extOfMime, imageMimeOfBytes, MAX_ATTACHMENT_BYTES } from '../../shared/domain/attachment'
import type { ProviderCredential } from '../../shared/domain/credential'
import { clampImageCount } from '../../shared/domain/image-count'
import { selectModelBinding } from '../../shared/domain/model-selection'
import { isImageModelAlias, type ModelAlias, type UpstreamProvider } from '../../shared/domain/provider'
import { ssrfRisk } from './tool/builtin/ssrf'

export interface GeneratedImage {
  /**
   * 必须收在 `ToolOutputImage['mime']` 的四个取值里 —— 转录里 `output.images`
   * 的类型就是它,宽成 `string` 会在工具结果组装处(`image.ts`)编译期炸。
   */
  mime: 'image/png' | 'image/jpeg' | 'image/gif' | 'image/webp'
  /** `data:<mime>;base64,` 内联 data URL —— 与 `browser_screenshot` 同一条约定 */
  dataRef: string
}

/** 改图的源。`dataRef` 可以是**未解析的** `ncw://` —— 解析在 `edit()` 入口做一次。 */
export interface ImageSource {
  mime: string
  dataRef: string
}

export interface ImageGenResult {
  /** 成功的那几张,按请求里的格子序号排列(失败的格子不占位) */
  images: GeneratedImage[]
  /** 实际使用的上游模型名 —— 回给模型,它要告诉用户是哪个模型画的 */
  model: string
  providerName: string
  /** 这次一共要了几张(`ImageGenOptions.count` 钳过之后的值) */
  requested: number
  /**
   * 失败格子的原因(不带供应商名)。**全失败时不会走到这里** —— 那种情况整体抛错。
   *
   * 需求:多张里挂了一两张时,已到手的图必须留下(用户已经在卡片上看见它们了),
   * 同时模型得知道「要 4 张只拿到 3 张、为什么」,否则它会宣布「画好了 4 张」。
   */
  failures: string[]
}

/** 单次调用的附加选项。缺省 = 一张、不逐张回调(旧调用方的行为不变)。 */
export interface ImageGenOptions {
  /** 要几张,钳在 1..MAX_IMAGE_COUNT(`shared/domain/image-count.ts`) */
  count?: number
  /**
   * 每成功一张**立刻**回调 —— 工具据此把这一张推给生成期的卡片。
   * `index` 是格子序号(0 起),与完成顺序无关。
   */
  onImage?: (index: number, image: GeneratedImage) => void
}

export interface ImageGenBridge {
  /**
   * 这一刻能不能生图:「对话生图」开关开着,且设置点名的那个绑定解析得到 ——
   * **同步、不碰凭证**,给工具的 `isEnabled` 用。
   *
   * ★ 只回答「模型解析得到吗」,不回答「密钥对不对」:后者要读 secrets(异步),
   * 而 `isEnabled` 是每轮工具快照里的同步谓词。没配 key 的症状是工具照常下发、
   * 调用时得到一句 `no API key` 的失败 —— 那句话本身就是可行动的反馈。
   */
  available(): boolean
  generate(prompt: string, signal: AbortSignal, options?: ImageGenOptions): Promise<ImageGenResult>
  /** 改图。`source` 解析失败(跨会话附件、坏 data URL)在进选路之前就抛。 */
  edit(prompt: string, source: ImageSource, signal: AbortSignal, options?: ImageGenOptions): Promise<ImageGenResult>
}

export interface ImageGenDeps {
  providers(): readonly UpstreamProvider[]
  aliases(): readonly ModelAlias[]
  /**
   * 用户点名的生图模型 —— `AppSettings.imageModel` 那一对,**每次现读**
   * (`runtime.ts` 从 `store.getSettings()` 现取,桥不缓存:设置的唯一权威在
   * 主进程,缓存一份的表现是改了设置对话里还用旧模型)。
   * `null` = 没选过,工具整体不下发。钉住 providerId 不做跨家回退。
   */
  preferredModel(): { alias: string; providerId: string | undefined } | null
  /**
   * 「对话生图」开关(`AppSettings.imageGenerationEnabled`),同样**每次现读**。
   * `false` = 工具不下发、调用被拒 —— 与「没选模型」是两回事,错误信息也分开说,
   * 否则用户关掉开关后会被引导去「重选模型」。
   */
  enabled(): boolean
  credential(ref: string): Promise<ProviderCredential | null>
  fetch: typeof fetch
  /**
   * `dataRef` → 可外发的 data URL。**实现是 `upstream/images.ts` 的
   * `resolveImageDataRef`**(会话归属、路径围栏、大小、魔数那套校验),
   * 由 `runtime.ts` 装配时绑好 —— 这里只认函数,便于用假实现单测。
   */
  resolveImage(source: ImageSource, signal: AbortSignal): Promise<ImageSource>
}

/**
 * 单次调用的上限。
 *
 * ★ 生图/改图比文本慢得多(高质量档十几秒起),但**必须有**:没有 deadline 的话,
 * 一个不回包的上游会让这次工具调用永远挂着,用户只能整场对话重启。
 * 120 秒盖得住各家的高质量档,又短到不至于像卡死。
 */
const GENERATE_TIMEOUT_MS = 120_000

/** URL 回包里下载图片的上限时间 —— 图已经生成了,这一步只是取回来 */
const FETCH_IMAGE_TIMEOUT_MS = 30_000

/**
 * `/images/edits` 的三种请求形状,**按序试**。
 *
 * 需求:改图接口两族互不兼容 —— OpenAI 官方是 `multipart/form-data`
 * (SDK 的 `images.edit()` 明确不收 JSON),xAI 明确**只收** `application/json`
 * 并警告 OpenAI SDK 的 `edit()` 用不了;国内兼容网关(百炼等)两种都有。
 * 只实现一种的症状是「换一家就改不了图」,而错误是一句读不出所以然的 400。
 *
 * multipart 里再分 `image[]` 与 `image` 两个字段名:gpt-image 一族收 `image[]`,
 * 老的 dall-e-2 / 部分网关收 `image`。反正 4xx 不计费,多试一次是便宜的。
 */
type EditShape = 'multipart-array' | 'multipart-single' | 'json'
const EDIT_SHAPES: readonly EditShape[] = ['multipart-array', 'multipart-single', 'json']

/**
 * 只有这些状态值得**换一种形状**再试 —— 它们说的是「请求形状/内容被拒」。
 * 401/403/429/5xx 是供应商级故障,换形状只会重复同一个失败,直接记为本次失败。
 * 404/405 是路由不存在,同样与形状无关,故不在表内。
 */
const SHAPE_REJECTED: ReadonlySet<number> = new Set([400, 406, 415, 422])

interface Candidate {
  provider: UpstreamProvider
  alias: ModelAlias
}

/**
 * 点名的那一个生图绑定,解析不到就是 `null`。
 *
 * 需求:「对话生图使用的模型」必选、只认这一个 —— 详见文件头那段。
 * `selectModelBinding` 的钉住语义(供应商不匹配就返回空,不换一家)正是这里要的,
 * 和「药丸上显示的那家 = 请求发去的那家」共用同一段代码(见 model-selection.ts 文件头)。
 *
 * ★ 解析不到的三种情形统一答 `null`:没选过 / 选的绑定被删或停用 / 点名的已经不是
 * 图片模型(能力被改过)。上层把 `null` 翻译成「工具不下发」+ 一句指向设置页的错误,
 * 不在这里细分 —— 三条路的用户动作是同一个:去图片页重选。
 */
function candidateOf(deps: ImageGenDeps): Candidate | null {
  const preferred = deps.preferredModel()
  if (preferred === null) return null
  const alias = selectModelBinding(deps.aliases(), deps.providers(), preferred.alias, preferred.providerId)
  if (alias === undefined || !isImageModelAlias(alias)) return null
  // selectModelBinding 已经验过「供应商 enabled」,这里只补齐它不负责的那半:实体
  const provider = deps.providers().find((p) => p.id === alias.providerId)
  return provider === undefined ? null : { provider, alias }
}

/** 错误细节压成一行、截断 —— 它要和多家的原因拼在一起给模型读 */
function clip(text: string, max = 200): string {
  const flat = text.replace(/\s+/gu, ' ').trim()
  return flat.length <= max ? flat : `${flat.slice(0, max)}…`
}

/**
 * 字节 → 内联 data URL 图。认不出格式答 `null`。
 *
 * ★ 魔数判读复用 shared 的 `imageMimeOfBytes`(和附件/工作区读图同一条判据):
 * 两族 API 的 images 响应都不带 mime 字段,而猜错的代价是图在界面上显示成裂图
 * (浏览器按 data URL 的 mime 决定解码方式)。
 * ★★ 认不出**不再假定 png**:200 回一份 HTML/JSON 时,旧写法会把非图字节包成
 * `data:image/png` 报成功,用户看到一张裂图而模型宣布「画好了」。现在一律
 * 当这一家/这一次失败,错误里说清「不是可识别的图片」。
 */
function imageOfBytes(bytes: Uint8Array): GeneratedImage | null {
  const mime = imageMimeOfBytes(bytes)
  if (mime === null) return null
  return { mime, dataRef: `data:${mime};base64,${Buffer.from(bytes).toString('base64')}` }
}

/** 字节超上限/格式认不出,统一的失败文案 —— 调用点把上下文(哪家/哪个 URL)补进去 */
function imageFromBytes(bytes: Uint8Array): GeneratedImage {
  const image = imageOfBytes(bytes)
  if (image === null) throw new Error('the response is not a recognized image (jpg/png/gif/webp)')
  return image
}

/**
 * 已解析源的 data URL → 字节。
 *
 * ★ 解析过的源**只可能是** `data:<mime>;base64,`(`resolveImageDataRef` 的两条
 * 分支都产出这个形状),所以这里用 `indexOf` 而不是再写一个宽容解析器 ——
 * 形状变了的话,下面那句错误会当场出现,而不是静默发一个空文件出去。
 */
function bytesOfSource(source: ImageSource): Uint8Array<ArrayBuffer> {
  const marker = ';base64,'
  const at = source.dataRef.indexOf(marker)
  if (!source.dataRef.startsWith('data:') || at < 0) {
    throw new Error(`Unsupported image source (expected a base64 data URL, got ${clip(source.dataRef, 60)})`)
  }
  // ★ 外面再套一层 `new Uint8Array(...)`:Buffer 的底层是 ArrayBufferLike(可能是
  // SharedArrayBuffer),而 `Blob` 的 BlobPart 只收 ArrayBuffer 后端的视图 ——
  // 直接传 Buffer 在这里类型上就过不去,拷贝一次顺带也把 Buffer 的语义切掉了
  return new Uint8Array(Buffer.from(source.dataRef.slice(at + marker.length), 'base64'))
}

/**
 * 从 `images` 系接口的响应里取第一张图。
 *
 * ★ `b64_json` 与 `url` 两条都要接:请求体**刻意不带 `response_format`** ——
 * 它在 gpt-image 一代上不受支持(可能换来一个 400),dall-e / xAI 默认回 `url`,
 * gpt-image 默认回 `b64_json`。两条都接的话,发不发那个参数都对,
 * 而少一个参数就少一种「换了家供应商就说参数不合法」的失败面。
 *
 * ★ `url` 分支走 `downloadImage`:那个地址是**上游回包里的**,不是用户填的 ——
 * 被投毒的上游可以指着 `http://169.254.169.254/…` 让我们本机去取,跳转同理。
 * (第一跳的 `baseUrl` 是用户自己配的,不在此列。)
 */
async function firstImage(
  payload: unknown,
  deps: ImageGenDeps,
  signal: AbortSignal
): Promise<GeneratedImage | null> {
  const data = (payload as { data?: unknown } | null)?.data
  if (!Array.isArray(data) || data.length === 0) return null
  const first: unknown = data[0]
  if (typeof first !== 'object' || first === null) return null
  const entry = first as { b64_json?: unknown; url?: unknown }
  if (typeof entry.b64_json === 'string' && entry.b64_json !== '') {
    const bytes = Buffer.from(entry.b64_json, 'base64')
    // ★ 与下载分支同一条上限:base64 回包同样能装下任意大图,而它接下来要进
    //   转录、SQLite 和每一轮的上游请求
    if (bytes.length > MAX_ATTACHMENT_BYTES) {
      throw new Error(`the generated image exceeds the ${sizeLimitText()} limit`)
    }
    return imageFromBytes(bytes)
  }
  if (typeof entry.url !== 'string' || entry.url === '') return null
  return downloadImage(deps.fetch, entry.url, signal)
}

/** 一次「候选家」的结果:成功带图,否则给人读的原因(不带供应商名,调用方加) */
type Attempt = { image: GeneratedImage } | { reason: string }

/**
 * 从 http(s) URL 下载一张图,返回内联 data URL。**两个消费方共用这一个实现:**
 * 上游回包里的 `url`(`firstImage`)和模型入参给的图片 URL(`tool/builtin/image.ts`)。
 * 复制第二份的症状是安全校验只修一份 —— 漏的那份就是一条读内网的路。
 *
 * ★★ 跳转**自己跟**,`redirect: 'manual'` 是这一段的全部意义:用默认的 'follow' 的话,
 * 首跳过一次 `ssrfRisk` 形同虚设 —— 公网地址一个 302 就指到
 * `http://169.254.169.254/…`(与 `web.ts` 那段同一条教训,那边注释里记了实测)。
 *
 * ★ 跨域跳转**允许**(与 `web.ts` 拒跨域的取舍不同):图片 URL 常是 CDN 签名地址,
 * 图床/短链跳到另一个域是常态,拒了会把正常用户挡在门外。放宽的只是「下一个域名」,
 * 不是「下一个网络位置」—— 每一跳都重新过 `ssrfRisk`,内网/环回/file 一律拒。
 */
export async function downloadImage(
  fetchFn: typeof fetch,
  rawUrl: string,
  signal: AbortSignal
): Promise<GeneratedImage> {
  let target: URL
  try {
    target = new URL(rawUrl)
  } catch {
    throw new Error(`Invalid image URL: ${clip(rawUrl, 120)}`)
  }
  for (let hop = 0; ; hop += 1) {
    const risk = ssrfRisk(target)
    if (risk !== null) throw new Error(`Refused to download the image: ${risk}`)
    const res = await fetchFn(target, {
      redirect: 'manual',
      signal: AbortSignal.any([signal, AbortSignal.timeout(FETCH_IMAGE_TIMEOUT_MS)])
    })
    const location = res.status >= 300 && res.status < 400 ? res.headers.get('location') : null
    if (location !== null) {
      // 读完/取消掉这一跳的 body,否则连接不释放(redirect 分支没有消费 body)
      await res.body?.cancel().catch(() => {})
      if (hop >= MAX_REDIRECTS) throw new Error(`Downloading the image failed: too many redirects (${String(MAX_REDIRECTS)})`)
      try {
        target = new URL(location, target)
      } catch {
        throw new Error('Downloading the image failed: the redirect target could not be parsed')
      }
      continue
    }
    if (!res.ok) throw new Error(`Downloading the image failed: HTTP ${String(res.status)}`)
    return imageFromBytes(await readCapped(res))
  }
}

/** 跟随跳转的上限 —— 防一个循环重定向把这次调用挂到超时 */
const MAX_REDIRECTS = 5

/** 32MB 上限的显示用法,和附件/工作区读图保持同一条线 */
function sizeLimitText(): string {
  return `${String(MAX_ATTACHMENT_BYTES / 1024 / 1024)} MB`
}

/**
 * 按上限读响应体。
 *
 * ★ content-length 只是预检,流式读的硬截才是真的:一个不说长度、无限吐字节的
 * 响应会把内存吃光。超限**读到就断**,不等读完 —— 拒收和收下 4GB 再删是两回事。
 */
async function readCapped(res: Response): Promise<Uint8Array> {
  const declared = Number(res.headers.get('content-length') ?? '')
  if (Number.isFinite(declared) && declared > MAX_ATTACHMENT_BYTES) {
    throw new Error(`Downloading the image failed: it exceeds the ${sizeLimitText()} limit`)
  }
  const body = res.body
  if (body === null) return new Uint8Array(0)
  const chunks: Uint8Array[] = []
  let total = 0
  const reader = body.getReader()
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    if (value === undefined) continue
    total += value.byteLength
    if (total > MAX_ATTACHMENT_BYTES) {
      await reader.cancel().catch(() => {})
      throw new Error(`Downloading the image failed: it exceeds the ${sizeLimitText()} limit`)
    }
    chunks.push(value)
  }
  const out = new Uint8Array(total)
  let at = 0
  for (const chunk of chunks) {
    out.set(chunk, at)
    at += chunk.byteLength
  }
  return out
}

function isHttpRejection(status: number): boolean {
  return SHAPE_REJECTED.has(status)
}

export function imageGenBridgeFor(deps: ImageGenDeps): ImageGenBridge {
  /**
   * 单次尝试(就是用户点名的那一家)。`source === undefined` = 文生图,
   * 否则改图(形状按 `EDIT_SHAPES` 试)。
   *
   * ★ 把「从拿到密钥到解析出图」整体包成一个结果,调用方只管翻成一句带供应商名的
   * 失败 —— 生图链路上「没配 key、地址改错、形状不兼容、协议不匹配」都常见,
   * 任何一种都不该把整场对话打死。
   */
  async function attempt(
    provider: UpstreamProvider,
    alias: ModelAlias,
    prompt: string,
    source: ImageSource | undefined,
    signal: AbortSignal
  ): Promise<Attempt> {
    const cred = await deps.credential(provider.credentialRef)
    if (cred === null) return { reason: 'no API key' }
    /*
      ★ 签名凭证(腾讯 TC3 / AWS SigV4)在这条路上**明确失败**,不给一个半段 token。
      生图接口按定义是 Bearer 的(`/images/generations`),签名凭证不适用;
      静默拿它的某个字段拼头只会得到一个读不懂的 401。
    */
    if (cred.kind === 'signature') {
      return { reason: 'this provider uses a signature credential, which image generation does not support' }
    }
    const token = cred.kind === 'api-key' ? cred.apiKey : cred.accessToken
    const base = provider.baseUrl.replace(/\/+$/u, '')

    if (source === undefined) {
      const response = await deps.fetch(`${base}/images/generations`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          authorization: `Bearer ${token}`
        },
        body: JSON.stringify({ model: alias.upstreamModel, prompt, n: 1 }),
        signal: AbortSignal.any([signal, AbortSignal.timeout(GENERATE_TIMEOUT_MS)])
      })
      if (!response.ok) {
        const text = await response.text().catch(() => '')
        return { reason: `HTTP ${String(response.status)} ${clip(text)}`.trim() }
      }
      const payload: unknown = await response.json().catch(() => null)
      const image = await firstImage(payload, deps, signal)
      return image === null ? { reason: 'response carried no image' } : { image }
    }

    /*
      改图:三种形状按序试。4xx(表内)换下一种;表外状态(401/429/5xx/404…)
      是供应商级/路由级故障,直接把这一次的状态当作原因返回,别再重复两次。
    */
    let last = 'edit request was not attempted'
    for (const shape of EDIT_SHAPES) {
      signal.throwIfAborted()
      const headers: Record<string, string> = { authorization: `Bearer ${token}` }
      let body: BodyInit
      if (shape === 'json') {
        headers['content-type'] = 'application/json'
        body = JSON.stringify({
          model: alias.upstreamModel,
          prompt,
          image: { url: source.dataRef, type: 'image_url' }
        })
      } else {
        const form = new FormData()
        form.append('model', alias.upstreamModel)
        form.append('prompt', prompt)
        form.append(
          shape === 'multipart-array' ? 'image[]' : 'image',
          new Blob([bytesOfSource(source)], { type: source.mime }),
          `image${extOfMime(source.mime)}`
        )
        // ★ content-type 不能手写:multipart 的 boundary 由 FormData 自己带
        body = form
      }
      const response = await deps.fetch(`${base}/images/edits`, {
        method: 'POST',
        headers,
        body,
        signal: AbortSignal.any([signal, AbortSignal.timeout(GENERATE_TIMEOUT_MS)])
      })
      if (response.ok) {
        const payload: unknown = await response.json().catch(() => null)
        const image = await firstImage(payload, deps, signal)
        return image === null ? { reason: 'response carried no image' } : { image }
      }
      const text = await response.text().catch(() => '')
      last = `HTTP ${String(response.status)} ${clip(text)}`.trim()
      if (!isHttpRejection(response.status)) break
    }
    return { reason: last }
  }

  async function run(
    prompt: string,
    source: ImageSource | undefined,
    signal: AbortSignal,
    options: ImageGenOptions | undefined
  ): Promise<ImageGenResult> {
    /*
      ★ 开关先于一切(连源图都不解析):`isEnabled` 挡住了正常路径,这里兜的是
      「这一轮下发之后用户在设置里关掉了生图」的窗口 —— 关掉之后不该再发出任何请求。
    */
    if (!deps.enabled()) {
      throw new Error(
        'Image generation is turned off. The user can turn it on in Settings > Models > Image generation.'
      )
    }
    /*
      ★ 源图解析放在解析模型**之前**:解析失败(跨会话附件、畸形 data URL、文件丢失)
      和用哪个模型无关,先解析能在它自己的错误信息里说清是哪一条,
      而不是把它混进「生图请求失败」里。
    */
    const resolved = source === undefined ? undefined : await deps.resolveImage(source, signal)
    const candidate = candidateOf(deps)
    if (candidate === null) {
      throw new Error(
        'No image model is selected. Choose one in Settings > Models > Image generation.'
      )
    }
    /*
      ★ 中断原样抛:用户按了停止不是「这次失败了」,吞掉它报成工具失败会让
      「停止」按钮看起来失效(定义工具的 `execute` 也靠 `isAbortError` 分流)。
    */
    const { provider, alias } = candidate
    signal.throwIfAborted()
    const count = clampImageCount(options?.count ?? 1)

    /** 一格的结局:成功带图,失败带原因(和原错误,给 cause 用)。只有中断会抛出去。 */
    const slot = async (index: number): Promise<Attempt | { reason: string; cause: unknown }> => {
      try {
        const outcome = await attempt(provider, alias, prompt, resolved, signal)
        // 到手即推:卡片上这一格的占位立刻换成图,不等同批里最慢的那一张
        if ('image' in outcome) options?.onImage?.(index, outcome.image)
        return outcome
      } catch (err) {
        /*
          ★★ 只有**外层** signal 确认中止时才算中断,不看 `isAbortError(err)`:
          内层 `AbortSignal.timeout` 超时抛的 TimeoutError,其 message 是
          "…was aborted due to timeout",`isAbortError` 的 `/abort/i` 会误判成
          用户中断 —— 那样一次上游卡死会把整场对话中止掉,而用户什么都没按。
          超时/解析失败按「这一次失败了」带供应商名抛出,不换下一家
          (点名了就只用那一个,见文件头)。
        */
        if (signal.aborted) throw err
        // ★ 留住原错误当 cause:原错误的类型/栈在诊断里比这句拼出来的消息有用
        //   (超时是 TimeoutError、解析是 ImageInputError,光看字符串分不出来)
        return { reason: err instanceof Error ? err.message : String(err), cause: err }
      }
    }

    /*
      需求:一次要多张。**逐张并发发 n=1 的请求**,不把 `n` 透传给上游:
      dall-e-3 等模型明确拒收 `n > 1`(400),国内网关对 `n` 的支持参差不齐 ——
      透传的症状是「换一家供应商,要多张就整体失败」。逐张请求对所有兼容
      `/images/*` 的上游都成立,而且每张独立成败,能逐张推给卡片。
      ★ 用 `Promise.all` 而不是 `allSettled`:`slot` 自己把非中断失败收成结果,
      唯一会冒出来的拒绝就是中断 —— 那正要原样上抛。
    */
    const outcomes = await Promise.all(Array.from({ length: count }, (_, index) => slot(index)))
    const images: GeneratedImage[] = []
    const failures: Array<{ reason: string; cause?: unknown }> = []
    for (const outcome of outcomes) {
      if ('image' in outcome) images.push(outcome.image)
      else failures.push(outcome)
    }
    if (images.length > 0) {
      return {
        images,
        model: alias.upstreamModel,
        providerName: provider.name,
        requested: count,
        failures: failures.map((f) => f.reason)
      }
    }
    // 全部失败:和单张时一样整体抛错、带供应商名。多格同因时只报第一条 —— 重复四遍同一句没有信息量
    const first = failures[0]
    throw new Error(
      `${provider.name}: ${first?.reason ?? 'no image was produced'}`,
      first?.cause === undefined ? undefined : { cause: first.cause }
    )
  }

  return {
    available: () => deps.enabled() && candidateOf(deps) !== null,
    generate: (prompt, signal, options) => run(prompt, undefined, signal, options),
    edit: (prompt, source, signal, options) => run(prompt, source, signal, options)
  }
}
