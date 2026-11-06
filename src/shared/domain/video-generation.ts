/**
 * 视频生成的领域词汇 —— 动作、能力、任务状态、媒体约束。
 *
 * ★★ **能力和客户端可执行能力是两件事。**
 *
 * 目录里一个型号可能"官方支持首尾帧",但我们的适配器还没写那条请求形状、
 * 或者这家需要客户白名单才拿得到权限。把两者合成一个布尔的话,设置页会给出
 * 一个**选了就会失败**的选项 —— 用户点"生成"、等五分钟、拿到一句上游的 400,
 * 却无从知道这条能力我们根本没接。所以:
 *
 * - `VideoCapabilities` 是**事实**(官方文档怎么写的);
 * - `VideoProfile.actions` 是**我们真的实现并核对过的动作**。
 *
 * 设置页只允许选 `actions` 非空、且 profile 与绑定对得上的条目;型号徽章按
 * `capabilities` 画,于是"支持但我们还没接"是看得见的一行字,而不是一次失败。
 *
 * ★ **纯数据 + 纯函数。** 这个文件被渲染层(表单校验)、内核(工具入参校验)和
 * 主进程(适配器)同时读,所以不许 import 任何一侧的东西。
 */
import type { ProviderVideoGeneration } from './provider'

/**
 * 一次视频请求的动作。**判据是「上游用哪条请求形状」,不是「用户想干什么」。**
 *
 * 编辑和延长在多数家是两个端点(`/edits` / `/extensions`),但它们共享同一条
 * 约束 —— 输入是一段**已有的视频**,而不是一张起始帧。首帧生视频(`image`)
 * 也是"给一段素材",但它落在生成端点里,且约束完全不同(图片 vs 视频)。
 */
export type VideoAction = 'generate' | 'image' | 'frames' | 'edit' | 'extend'

/** 动作的稳定顺序 —— 表单、徽章、快照都照它排,免得同一组能力在两处换序。 */
export const VIDEO_ACTIONS: readonly VideoAction[] = ['generate', 'image', 'frames', 'edit', 'extend']

/** 适配器标识。落库、进 profile、经 IPC 传 —— 字符串字面量联合,坏值当场拒绝。 */
export type VideoAdapterId =
  | 'google-veo'
  | 'google-omni'
  | 'xai-video'
  | 'ark-video'
  | 'dashscope-video'
  | 'kling-video'
  | 'minimax-video'
  | 'bigmodel-video'
  | 'siliconflow-video'
  | 'runway-video'
  | 'luma-video'
  | 'luma-legacy-video'
  | 'fal-queue'
  | 'replicate-predictions'
  | 'tencent-vclm'
  | 'aws-bedrock-video'

export const VIDEO_ADAPTER_IDS: readonly VideoAdapterId[] = [
  'google-veo',
  'google-omni',
  'xai-video',
  'ark-video',
  'dashscope-video',
  'kling-video',
  'minimax-video',
  'bigmodel-video',
  'siliconflow-video',
  'runway-video',
  'luma-video',
  'luma-legacy-video',
  'fal-queue',
  'replicate-predictions',
  'tencent-vclm',
  'aws-bedrock-video'
]

export function isVideoAdapterId(value: unknown): value is VideoAdapterId {
  return typeof value === 'string' && (VIDEO_ADAPTER_IDS as readonly string[]).includes(value)
}

/**
 * 一次请求要交给上游的媒介。
 *
 * ★ `videoUrl` 与 `videoBytes` 分开,是为了让"只收公网 URL"和"我们要先下下来
 * 再转字节"这两件事在**类型上**就分得开 —— 后者只对已验证的公网 URL 执行,
 * 绝不接受本机路径。
 */
export type VideoActionKind =
  | 'prompt'
  | 'imageUrl'
  | 'imageBytes'
  | 'videoUrl'
  | 'videoBytes'

/**
 * 官方**声明**的能力。画徽章、做默认选择、给用户看"这家到底能不能干这个"。
 * 它**不**承诺我们的适配器已经实现 —— 那由 `VideoProfile.actions` 回答。
 */
export interface VideoCapabilities {
  /** 纯文本 → 视频 */
  textToVideo: boolean
  /** 图片(起始帧)→ 视频 */
  imageToVideo: boolean
  /** 首帧 + 尾帧 → 视频 */
  firstLastFrame: boolean
  /** 编辑已有视频 */
  editVideo: boolean
  /** 延长已有视频 */
  extendVideo: boolean
  /** 输出带原生音轨 */
  nativeAudio?: boolean
  /**
   * 编辑/延长只接受**该家自己生成**的视频(而非任意上传的视频)。
   * ★ Veo / Luma 旧版就是这样 —— 界面上必须说清,否则用户会拿一段别人的片子
   * 去延长,拿到一句读不出所以然的 400。
   */
  videoInputLimitedToOwnOutput?: boolean
  /** 编辑/延长的输入视频有时效(如 Veo 的 2 天) */
  videoInputMaxAgeMs?: number
}

/** 图片输入的上限。来自各家官方文档,不符时**本地**拒绝,不换来一句上游 400。 */
export interface VideoImageLimits {
  maxBytes?: number
  minWidth?: number
  minHeight?: number
  maxWidth?: number
  maxHeight?: number
  mimes?: readonly string[]
}

export interface VideoVideoLimits {
  maxBytes?: number
  minDurationSeconds?: number
  maxDurationSeconds?: number
  mimes?: readonly string[]
}

/**
 * 数值约束 —— 每个 profile 一份。
 *
 * ★★ **不给"默认值"兜底,只给"合法集合"。** 一个上游接受 4–15 秒的模型,
 * 我们替他挑 8 秒是可以的(那是**显式**的 profile 默认);但用户说了 3 秒时
 * **必须当场拒绝**,不能悄悄改成 4 秒 —— 按秒计费的东西,静默改档等于
 * 换了一个价格给他。
 */
export interface VideoParameterSpec {
  /**
   * 时长(秒)的合法取值。空 = 该动作不接受时长参数。
   *
   * ★ 元素类型是 `number | string`,因为**各家线形不一样**:xAI / MiniMax 收整数
   *   秒,而 Luma 收字符串 `'5s'` / `'9s'`。用 number 的话 Luma 那两条只能编一个
   *   假的 5 再在适配器里拼后缀 —— 于是"合法集合"这份声明就不再是请求里真正发出去
   *   的那几个值,校验也跟着失真。
   */
  durations?: readonly (number | string)[]
  /** 时长区间(与 `durations` 二选一,取其一即可) */
  durationRange?: { min: number; max: number }
  /** 时长默认值。必须落在合法集合/区间里 */
  defaultDuration?: number | string
  aspectRatios?: readonly string[]
  defaultAspectRatio?: string
  resolutions?: readonly string[]
  defaultResolution?: string
  /** 是否支持固定随机种子 */
  seed?: boolean
  /** 是否支持原生音频开关 */
  audio?: boolean
}

/**
 * 一个「供应商 × 型号 × API 版本」的可执行档案。
 *
 * ★ 创建时**冻结** profile id:任务落库后,即使我们后来升级了请求形状,
 * 恢复查询的旧任务仍按它创建时那份 profile 解读响应。
 */
export interface VideoProfile {
  /** 稳定标识。落库、进 tool 描述、进 IPC —— 改它等于换一套档案 */
  id: string
  /** 人读的名字,只用于诊断 */
  label: string
  adapter: VideoAdapterId
  /** 我们**真的实现并核对过**的动作 */
  actions: readonly VideoAction[]
  capabilities: VideoCapabilities
  /** 输入媒介,按动作细分 */
  inputs?: Partial<Record<VideoAction, readonly VideoActionKind[]>>
  /** 每个动作的参数约束 */
  parameters?: Partial<Record<VideoAction, VideoParameterSpec>>
  imageLimits?: VideoImageLimits
  videoLimits?: VideoVideoLimits
  /** 该 profile 是否提供独立的取消操作;缺席 = 不支持 */
  cancel?: boolean
  /** 上游结果的保质期(毫秒)。过期后只能重新生成 */
  resultTtlMs?: number
  /**
   * `google-veo` / `minimax` / `luma` 这类一家多版本必须各写一条;
   * 这条记的是"哪一代",仅供诊断与 UI 说明。
   */
  apiVersion?: string
  /** 官方来源与核对日期。**没有它就不该标 verified** */
  source?: { url: string; verifiedAt: string }
  verification: 'documented' | 'unverified'
}

/** 适配器渲染请求/解析响应时需要的那点上下文。不含密钥 —— 密钥只在适配器调用栈里。 */
export interface VideoProfileContext {
  profile: VideoProfile
  /** 上游模型名(不是我们的别名) */
  upstreamModel: string
}

// ─────────────────────────────────────────────────────────────
// 任务状态
// ─────────────────────────────────────────────────────────────

/**
 * ★★ **云端进度与本地取回是两条轨道,不能合成一个枚举。**
 *
 * 合成一个的话,"云端成功了但下载失败"就没有名字 —— 而它恰恰是最常见的一种
 * 中间态(结果 URL 有 1 小时/24 小时保质期,下载会失败)。落进 `failed` 的话,
 * 用户看到的是"生成失败",而后台其实**已经付过费、视频就在上游躺着**;
 * 重试按钮也会去**重新生成**,再付一次钱。
 */
export type VideoCloudStatus =
  | 'submitting'
  /** 请求发出去了但拿不准上游收没收到。★ **绝不允许自动重发** */
  | 'unknown'
  | 'queued'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'canceled'

export type VideoRetrievalStatus =
  | 'waiting'
  | 'downloading'
  | 'ready'
  | 'retryable_error'
  | 'expired'
  /**
   * 停住不再推进。★ 与 `retryable_error` 是两件事:
   * 那个是"这次取回/查询失败了,还会再试";这个是**必须人工介入**才能继续 ——
   * 账户被切换、Key 被删、连接被移除。混成一个的话,界面会给一个"重试"按钮,
   * 而点了它照样会失败(因为它缺的是凭据,不是网络)。
   */
  | 'paused'

export interface VideoAssetRef {
  /** `ncw://` 稳定地址。本地取回完成后才有 */
  url: string
  mime: string
  size: number
  /** 封面/缩略图(可选) */
  posterUrl?: string
}

/**
 * 落库的完整任务。**这是后台生命周期唯一的真源** —— 渲染层不在易失 progress
 * 上再维护一份,否则重启后两份会算出不同的结果。
 */
export interface VideoJob {
  id: string
  /** 配置作用域,恢复时用来判断"这条是不是我该继续查的" */
  configProfile: string
  workspaceId: string
  sessionId: string
  /** 发起它的那次 run / 工具调用。用于把回执挂回转录 */
  originRunId?: string
  originCallId?: string
  cloud: VideoCloudStatus
  retrieval: VideoRetrievalStatus
  cancel: 'none' | 'requested' | 'unsupported' | 'failed'
  /** 创建时冻结的档案与路由快照 */
  providerId: string
  profileId: string
  model: string
  /** 创建时的地址/地域/输出配置。省略仅兼容旧任务,新任务不随供应商编辑而改路由。 */
  connection?: ProviderVideoGeneration
  /** 上游自己的任务 id(`request_id` / `task_id` / `video_id` …) */
  upstreamId?: string
  /**
   * 查询/取消这条任务时要带上的**路由段**。
   *
   * ★ 只有商城类适配器(fal)需要它:那里任务身份是"endpoint + request_id"一对,
   * 而 endpoint 段不在响应里、只在提交时用的地址里。**必须冻结存下来** ——
   * 用当前模型别名现推的话,用户改过绑定之后会查到**另一条队列**的状态。
   */
  route?: string
  /** 凭据槽的**非明文**指纹:换了 Key/账户后不能拿新的去查旧任务 */
  credentialFingerprint?: string
  /** 聚合出的用户可读阶段文案 key 的入参(不落长文本) */
  progress?: { percent?: number; stage?: string }
  assets: readonly VideoAssetRef[]
  error?: string
  /** 上游结果的过期时刻 */
  expiresAt?: number
  createdAt: number
  updatedAt: number
  /** 单调递增,渲染层据此做增量合并 */
  revision: number
}

/** 给渲染层的安全投影。**没有密钥、没有绝对路径、没有原始第三方响应**。 */
export interface VideoJobView {
  id: string
  sessionId: string
  cloud: VideoCloudStatus
  retrieval: VideoRetrievalStatus
  cancel: VideoJob['cancel']
  providerId: string
  model: string
  percent?: number
  stage?: string
  videos: readonly VideoAssetRef[]
  error?: string
  createdAt: number
  updatedAt: number
  revision: number
}

export function videoJobView(job: VideoJob): VideoJobView {
  return {
    id: job.id,
    sessionId: job.sessionId,
    cloud: job.cloud,
    retrieval: job.retrieval,
    cancel: job.cancel,
    providerId: job.providerId,
    model: job.model,
    ...(job.progress?.percent === undefined ? {} : { percent: job.progress.percent }),
    ...(job.progress?.stage === undefined ? {} : { stage: job.progress.stage }),
    videos: job.assets,
    ...(job.error === undefined ? {} : { error: job.error }),
    createdAt: job.createdAt,
    updatedAt: job.updatedAt,
    revision: job.revision
  }
}

/** 终局(不再需要轮询)吗。`retryable_error` **不算** —— 它还要重试取回。 */
export function isTerminalCloud(status: VideoCloudStatus): boolean {
  return status === 'succeeded' || status === 'failed' || status === 'canceled'
}

/** 还需要占用一个后台 worker 吗。 */
export function needsWorker(job: Pick<VideoJob, 'cloud' | 'retrieval'>): boolean {
  if (job.cloud === 'submitting' || job.cloud === 'unknown') return true
  if (isTerminalCloud(job.cloud)) {
    return job.cloud === 'succeeded' && (job.retrieval === 'waiting' || job.retrieval === 'downloading' || job.retrieval === 'retryable_error')
  }
  return true
}

// ─────────────────────────────────────────────────────────────
// 媒体约束
// ─────────────────────────────────────────────────────────────

/**
 * 生成视频的独立上限,初始 512 MiB。
 *
 * ★★ **不是** `MAX_ATTACHMENT_BYTES`。那个 32 MiB 管的是用户拖进输入框的文件,
 * 它的量级由"一次 IPC 结构化克隆"决定;而这里的字节**从不经过 IPC**,走的是
 * 主进程流式落盘。把两者合成一个数只有两种结果:要么用户图片上限被抬到 512 MiB
 * (IPC 和 SQLite 都受不了),要么生成的视频被 32 MiB 卡死(4K 十秒就超)。
 */
export const MAX_GENERATED_VIDEO_BYTES = 512 * 1024 * 1024

/** 视频容器 → mime。扩展名与 mime 都认,但**字节优先**。 */
const VIDEO_MIME_BY_EXT: Readonly<Record<string, string>> = {
  mp4: 'video/mp4',
  m4v: 'video/mp4',
  webm: 'video/webm',
  mov: 'video/quicktime',
  mkv: 'video/x-matroska'
}

const VIDEO_EXT_BY_MIME: Readonly<Record<string, string>> = {
  'video/mp4': '.mp4',
  'video/webm': '.webm',
  'video/quicktime': '.mov',
  'video/x-matroska': '.mkv'
}

export function videoMimeOfExt(pathOrName: string): string | null {
  const i = pathOrName.lastIndexOf('.')
  if (i < 0) return null
  return VIDEO_MIME_BY_EXT[pathOrName.slice(i + 1).toLowerCase()] ?? null
}

export function videoExtOfMime(mime: string): string {
  return VIDEO_EXT_BY_MIME[mime.trim().toLowerCase()] ?? '.mp4'
}

export function isVideoMime(mime: string): boolean {
  return mime.trim().toLowerCase().startsWith('video/')
}

/**
 * 容器签名 → mime。认不出答 `null`。
 *
 * ★★ **不能靠扩展名。** 上游回包里 200 加一份 HTML 错误页是常态,把它存成
 * `.mp4` 的话,用户拿到的是一个打不开的文件,而整条链路从头到尾报"成功"。
 * ★ 与图片那侧同一条判据(`imageMimeOfBytes`),但分成两个函数:视频的容器头
 * 与图片完全不同,而任何"通用二进制识别"最后都会长成一个不认识任何格式的
 * 大 `if`。
 */
export function videoMimeOfBytes(bytes: Uint8Array): string | null {
  if (bytes.length < 12) return null
  // ISO BMFF(MP4 / MOV):`....ftyp` + brand
  if (bytes[4] === 0x66 && bytes[5] === 0x74 && bytes[6] === 0x79 && bytes[7] === 0x70) {
    const brand = String.fromCharCode(bytes[8]!, bytes[9]!, bytes[10]!, bytes[11]!)
    if (brand.startsWith('qt')) return 'video/quicktime'
    return 'video/mp4'
  }
  // Matroska / WebM:EBML 头 1A 45 DF A3
  if (bytes[0] === 0x1a && bytes[1] === 0x45 && bytes[2] === 0xdf && bytes[3] === 0xa3) {
    return 'video/webm'
  }
  return null
}

// ─────────────────────────────────────────────────────────────
// 公网视频 URL(编辑/延长的唯一入口)
// ─────────────────────────────────────────────────────────────

/**
 * 编辑/延长的输入**只接受公网 http(s) 视频 URL**。
 *
 * ★★ 这不是"暂时没做上传",而是产品决定:`ncw://`、本机路径和内联 data URL
 * 一律拒绝,因为要支持它们就得先把字节交给上游 —— 而"把用户本机的视频传到
 * 某个公网临时存储"是另一个功能(以及另一个隐私问题)。
 */
export function isPublicVideoUrl(value: string): boolean {
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

/** 提交回执的稳定前缀 —— 模型据此知道"还没好",UI 据此认出这是提交而非成品。 */
export const VIDEO_SUBMITTED_NOTE = 'Video generation submitted; it is not finished yet.'
