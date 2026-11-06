/*
 * `generate_video` —— 对话里「生成 / 编辑 / 延长一段视频」的落地点。
 *
 * ## 与 `generate_image` 最根本的不同:它是**异步**的
 *
 * 生图是"调用 → 等十几秒 → 拿到图"。视频要几分钟,而且期间用户会停止、切会话、
 * 甚至关掉应用 —— 云端任务还在跑、还在计费。所以这个工具的语义是:
 *
 * - `generate` / `image` / `frames` / `edit` / `extend`:**提交**,立刻返回一个
 *   **任务回执**(不是成品)。回执里明确写着"还没完成",模型据此不该向用户
 *   宣布"视频做好了";
 * - `status`:查本会话某个任务。完成后返回成品与**稳定地址**;
 * - `cancel`:取消一个已提交的任务。**与"停止聊天"、与开关都无关** ——
 *   一个已经计过费的任务不该因为我们停止对话就查不到。
 *
 * ## 三处刻意不做的事
 *
 * 1. **不接受 `model` / `provider` 覆盖。** 视频模型由用户在设置页点名
 *    (`AppSettings.videoModel`),工具不能改。理由与生图同构、代价更大:
 *    一个按秒计费的调用跑到另一家去,账单和结果都对不上。
 * 2. **不跨家回退。** 点名的那条失败就是失败 —— "我明明选了 A、出片的却是 B"
 *    在视频上尤其贵。
 * 3. **不紧密轮询。** 描述里明说"不要反复查";一次调用最多提交一次,
 *    进度由那张卡片自己跟(后台 manager 按供应商节奏轮询)。
 *
 * ## 权限档:与生图同一档
 *
 * `readOnly: true` 的含义是"**不写工作区**":它不碰任何工作区文件,产出只挂在
 * 工具结果与会话附件上(与 `browser_screenshot` / `visualize_show_widget` 同档)。
 * 会话产物和已提交的云端任务由视频设置管理,不是"写文件"。往工作区写是
 * `SaveVideo` 的事 —— 那个是 `readOnly: false`。
 *
 * ★ `needsNetwork: true` 是事实描述(它真出网),但它**不进**「联网搜索」名单
 * (`permission-gate.ts` 的 `NETWORK_SWITCH_TOOLS` 只管网页搜索与抓取);
 * 管它的是视频生成**自己的**开关。
 */
import { z } from 'zod'
import type { ToolResult } from '../../../../shared/agent/tool'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import { isPublicVideoUrl, VIDEO_SUBMITTED_NOTE } from '../../../../shared/domain/video-generation'
import { MAX_ATTACHMENT_BYTES, NCW_SCHEME, mimeOfExt } from '../../../../shared/domain/attachment'
import { defineTool } from '../define'
import type { ToolContext, ToolRegistration } from '../registry'
import { resolvePath } from './paths'

/** `image` 参数取这个字面量 = 「对话里最近一张图」(与文件路径区分开的关键词) */
const LATEST = 'latest'

const VIDEO_ACTIONS = ['generate', 'image', 'frames', 'edit', 'extend', 'status', 'cancel'] as const

const GenerateVideoInput = z.object({
  action: z
    .enum(VIDEO_ACTIONS)
    .describe(
      'What to do. "generate" = text-to-video; "image" = animate one image; "frames" = interpolate between a first and a last ' +
        'frame; "edit" / "extend" = change or continue an existing video; "status" = check a job from this conversation; ' +
        '"cancel" = ask to cancel a job. The model, provider and its credentials come from Settings > Models > Video generation.'
    ),
  prompt: z
    .string()
    .optional()
    .describe('What the video should show. Required for every action except "status" and "cancel".'),
  image: z
    .string()
    .min(1)
    .optional()
    .describe(
      'First frame (for "image" and "frames"): "latest" for the most recent image in this conversation, an ncw:// image URL, ' +
        'a workspace image path, or an http(s) image URL.'
    ),
  last_frame: z
    .string()
    .min(1)
    .optional()
    .describe('Last frame (for "frames"): same forms as `image`. Both frames are required for frame interpolation.'),
  video_url: z
    .string()
    .min(1)
    .optional()
    .describe(
      'Source video for "edit" / "extend". **Must be a public http(s) URL** — local paths, ncw:// URLs and inline data are ' +
        'rejected on purpose, because supporting them would mean uploading the user\'s file somewhere.'
    ),
  duration: z
    .union([z.number(), z.string()])
    .optional()
    .describe('Video length. Only certain values are accepted; the exact set depends on the model (see the tool result if it is rejected).'),
  aspect_ratio: z.string().min(1).optional().describe('Aspect ratio, e.g. "16:9". Only values the chosen model accepts are allowed.'),
  resolution: z.string().min(1).optional().describe('Resolution, e.g. "720p" or "1920x1080". Only values the chosen model accepts are allowed.'),
  seed: z.number().int().min(0).optional().describe('Fixed random seed, when the model supports one.'),
  job_id: z
    .string()
    .min(1)
    .optional()
    .describe('Job id from a previous call in this conversation. Required for "status" and "cancel".')
})

/**
 * 「最近一张图」与工作区图片 → 可外发的素材。
 *
 * ★ 与生图那边逐字同构,但**收窄了输出**:这里只要"能交给上游的一段媒介",
 * 而它要么是 `ncw://`(由桥按会话归属解析),要么是公网 URL。本机工作区图片
 * 走 `downloadImage` 那条路之前**不落盘** —— 直接转成 data URL 交给桥,
 * 由桥决定这一家收不收字节(只收 URL 的家会明确拒绝,见 video-generation.ts)。
 */
async function imageSource(raw: string, ctx: ToolContext): Promise<{ mime: string; dataRef: { kind: 'url'; url: string } | { kind: 'bytes'; bytes: Uint8Array } } | string> {
  if (raw === LATEST) {
    const latest = latestImageRef(ctx)
    if (latest === null) {
      return 'There is no image in this conversation yet. Attach one, or pass a workspace path or an http(s) URL.'
    }
    return await resolveImageRef(latest.dataRef, ctx)
  }
  if (raw.startsWith(`${NCW_SCHEME}://`) || raw.startsWith('data:')) {
    return await resolveImageRef(raw, ctx)
  }
  if (raw.startsWith('http://') || raw.startsWith('https://')) {
    /*
      ★ 公网图片交给上游**原样**用 URL(而不是先下下来) —— 那省一跳,
      而且是唯一能保证"上游看到的字节和用户给的一样"的做法。失败由上游报。
    */
    return { mime: 'image/png', dataRef: { kind: 'url', url: raw } }
  }
  // 工作区路径 → 读成字节(上限与附件同一条;超过就明确拒,不截断)
  const resolved = await resolvePath(ctx, raw)
  if (!resolved.ok) return resolved.result.output.content
  const { fs } = ctx.host
  if (!(await fs.exists(resolved.abs))) return `File does not exist: ${raw}`
  const stat = await fs.stat(resolved.abs)
  if (stat.isDir) return `${raw} is a directory, not an image file`
  if (stat.size <= 0 || stat.size > MAX_ATTACHMENT_BYTES) {
    return `Image file size (${String(stat.size)} bytes) is outside the allowed range of 1–${String(MAX_ATTACHMENT_BYTES)} bytes`
  }
  const bytes = await fs.readFileBytes(resolved.abs, stat.size + 1)
  if (bytes.length !== stat.size) return `Image file changed while it was being read: ${raw}`
  const mime = mimeOfExt(raw)
  return { mime, dataRef: { kind: 'bytes', bytes: new Uint8Array(bytes) } }
}

/**
 * `ncw://` / 内联 data URL 图 → **字节**。
 *
 * ★★ 这里必须读成字节,而不是把地址原样交给上游。原因很实在:`ncw://` 是我们
 * 自己的私有协议,**上游不认识它**;把它当图片地址发过去只会换来一句 400。
 * 而会话图片本来就没有公网地址(见 `video-generation.ts` 的
 * "不帮用户上传到第三方图床"),所以内联是唯一可行的那条路。
 *
 * ★ 走 `ctx.sessionImages.read` —— 它背后是 `resolveImageDataRef` 那套
 * **与发上游请求同源**的校验(只认本会话、realpath 围栏、大小、魔数)。
 * 不收的话就明确说清(纯内核测试没装配钟),而不是发一个坏 body。
 */
async function resolveImageRef(
  dataRef: string,
  ctx: ToolContext
): Promise<{ mime: string; dataRef: { kind: 'bytes'; bytes: Uint8Array } } | string> {
  const store = ctx.sessionImages
  if (store === undefined) return 'Conversation images are not available in this environment.'
  try {
    const read = await store.read(dataRef, ctx.signal)
    return { mime: read.mime, dataRef: { kind: 'bytes', bytes: read.bytes } }
  } catch (error) {
    return error instanceof Error ? error.message : String(error)
  }
}

/** 往回找对话里最近的一张图(和生图那边同一条扫法:从后往前)。 */
function latestImageRef(ctx: ToolContext): { mime: string; dataRef: string } | null {
  const messages = ctx.messages
  if (messages === undefined) return null
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    const parts = messages[i]?.parts ?? []
    for (let j = parts.length - 1; j >= 0; j -= 1) {
      const part = parts[j]
      if (part === undefined) continue
      if (part.type === 'image') return { mime: part.mime, dataRef: part.dataRef }
      if (part.type === 'tool_result') {
        const image = part.output.images?.[0]
        if (image !== undefined) return { mime: image.mime, dataRef: image.dataRef }
      }
    }
  }
  return null
}

const generateVideoTool: ToolRegistration = defineTool({
  internalId: 'generate_video',
  description:
    'Generate, edit, extend or check a video using the video model the user picked in Settings > Models > Video generation.\n\n' +
    `This is asynchronous. A create call returns **${VIDEO_SUBMITTED_NOTE}** — you get a job id, not a video. ` +
    'Do not tell the user the video is ready until a "status" call reports one; do not poll it in a tight loop (the chat card tracks progress on its own).\n\n' +
    'Actions:\n' +
    '- generate: text-to-video. Pass `prompt`.\n' +
    '- image: animate one image. Pass `prompt` and `image` ("latest" for the most recent image in this conversation, an ncw:// URL, a workspace path, or an http(s) URL).\n' +
    '- frames: interpolate. Pass `prompt`, `image` (first frame) and `last_frame`.\n' +
    '- edit / extend: pass `prompt` and `video_url` (a **public** http(s) URL — local files and ncw:// URLs are rejected on purpose).\n' +
    '- status: pass `job_id` from an earlier call in this conversation.\n' +
    '- cancel: pass `job_id`. Cancelling is best-effort and may not stop billing; the result is still collected if it finishes.\n\n' +
    'Only durations, aspect ratios and resolutions the chosen model actually accepts are allowed; anything else is rejected before a paid request is made. ' +
    'The model and provider are not parameters — the user chooses them in settings, and the tool never falls back to another one.',
  schema: GenerateVideoInput,
  readOnly: true,
  destructive: false,
  // 事实描述:提交、查询、取消都真出网。它**不受**输入框那颗「联网搜索」开关管 ——
  // 管它的是视频生成自己的开关(理由见文件头)。
  needsNetwork: true,
  isEnabled: (ctx) => ctx.videoGen?.unavailableReason() === null,
  async run(input, ctx): Promise<ToolResult> {
    if (ctx.videoGen === undefined) {
      // isEnabled 挡的是正常路径;这里兜的是「快照之后桥被摘掉」的窄窗口。
      return toolFail('Video generation is not available in this environment.')
    }
    const bridge = ctx.videoGen

    // ── status / cancel:对**已有任务**的操作,不看当前选中的模型 ──
    if (input.action === 'status') {
      if (input.job_id === undefined) return toolFail('status needs the job_id from an earlier call.')
      const job = bridge.status(input.job_id)
      if (job === undefined) return toolFail(`No video job "${input.job_id}" in this conversation.`)
      return toolOk(describeJob(job))
    }
    if (input.action === 'cancel') {
      if (input.job_id === undefined) return toolFail('cancel needs the job_id from an earlier call.')
      const result = await bridge.cancel(input.job_id)
      if (!result.ok) return toolFail(result.reason)
      return toolOk(
        result.state === 'already-done'
          ? `Job ${input.job_id} had already finished, so there was nothing to cancel.`
          : `Cancellation requested for job ${input.job_id}. It may still finish and be billed; if it does, the video will still be collected.`
      )
    }

    // ── 新建类动作:先要 prompt ──
    const prompt = input.prompt?.trim() ?? ''
    if (prompt === '') return toolFail('A prompt is required for this action.')

    // ── 编辑/延长:只收公网 URL,且**当场**说清为什么收窄 ──
    let video: { url: string } | undefined
    if (input.action === 'edit' || input.action === 'extend') {
      const url = input.video_url?.trim() ?? ''
      if (url === '') {
        return toolFail(`${input.action === 'extend' ? 'Extending' : 'Editing'} a video needs video_url.`)
      }
      if (!isPublicVideoUrl(url)) {
        return toolFail(
          'video_url must be a public http(s) video URL. Local paths, ncw:// URLs and inline data are rejected on purpose — ' +
            'upload the video somewhere reachable and pass that URL.'
        )
      }
      video = { url }
    }

    // ── 帧:两个都给了才是首尾帧;只给一个要看该模型的规则 ──
    /*
      ★ 收窄写成一个显式的判别:imageSource 返回"素材或一句失败原因(字符串)",
      而那两个分支在这里必须**分开**处理 —— 把失败原因当素材交给上游的后果是
      一个空引用,而不是一句能读的报错。
    */
    const resolvedImage = input.image === undefined ? undefined : await imageSource(input.image, ctx)
    if (typeof resolvedImage === 'string') return toolFail(resolvedImage)
    const resolvedLast = input.last_frame === undefined ? undefined : await imageSource(input.last_frame, ctx)
    if (typeof resolvedLast === 'string') return toolFail(resolvedLast)
    const image = resolvedImage
    const lastFrame = resolvedLast
    if (input.action === 'image' && image === undefined) {
      return toolFail('Image-to-video needs `image` (use "latest" for the most recent image in this conversation).')
    }
    if (input.action === 'frames' && (image === undefined || lastFrame === undefined)) {
      /*
        ★ 两个帧缺一个时**直接说清缺哪个**,而不是自动降级成单帧生成 ——
        降级的话用户要的是"从 A 变到 B",拿到的却是"从 A 开始随便动",
        而工具回执还会说成功。
      */
      return toolFail('Frame interpolation needs both `image` (first frame) and `last_frame`.')
    }

    const outcome = await bridge.submit({
      action: input.action,
      prompt,
      ...(image === undefined ? {} : { image }),
      ...(lastFrame === undefined ? {} : { lastFrame }),
      ...(video === undefined ? {} : { video }),
      ...(input.duration === undefined ? {} : { duration: input.duration }),
      ...(input.aspect_ratio === undefined ? {} : { aspectRatio: input.aspect_ratio }),
      ...(input.resolution === undefined ? {} : { resolution: input.resolution }),
      ...(input.seed === undefined ? {} : { seed: input.seed })
    })
    if (!outcome.ok) return toolFail(outcome.reason)

    /*
      ★★ 回执必须**明确说"还没完成"**,而且要说清怎么查。少了这句话,模型几乎
      一定会下一句就向用户宣布"视频生成好了" —— 而那时候云端连渲染都还没开始。
    */
    return toolOk(
      `${VIDEO_SUBMITTED_NOTE} Job ${outcome.job.id} was accepted by ${outcome.providerName} using ${outcome.job.model}. ` +
        'The chat card will show progress and attach the video when it is ready. ' +
        'You can check it later with action "status" and this job_id; do not poll it in a tight loop.',
      { videos: [] }
    )
  }
})

/** 把一个任务视图翻成给模型读的一段话。**只报告事实,不宣布没发生的事。** */
function describeJob(job: {
  id: string
  cloud: string
  retrieval: string
  model: string
  cancel: string
  percent?: number
  stage?: string
  videos: readonly { url: string; mime: string; size: number }[]
  error?: string
}): string {
  switch (job.cloud) {
    case 'succeeded':
      if (job.retrieval === 'ready' && job.videos.length > 0) {
        return (
          `Job ${job.id} finished on ${job.model}. The video is attached below and its URL is:\n` +
          job.videos.map((video) => `- ${video.url}`).join('\n')
        )
      }
      /*
        ★★ 这一支就是"云端成功、取回失败" —— 它**不能**被说成生成失败,
        否则用户会重试一次,再付一次钱。说清事实:视频已经生成好了,
        只是还没存到本地,重试取回不会重新生成。
      */
      return (
        `Job ${job.id} finished generating on ${job.model}, but storing it locally failed` +
        (job.error === undefined ? '' : ` (${job.error})`) +
        '. The video itself is ready; retrying the download will not generate another one.'
      )
    case 'failed':
      return `Job ${job.id} failed on ${job.model}${job.error === undefined ? '' : `: ${job.error}`}.`
    case 'canceled':
      return `Job ${job.id} was canceled.`
    default: {
      const bits = [`Job ${job.id} is still ${job.cloud}`]
      if (job.stage !== undefined) bits.push(`(${job.stage})`)
      if (job.percent !== undefined) bits.push(`${String(job.percent)}%`)
      if (job.cancel === 'requested') bits.push('— a cancellation has been requested; it may still finish and be billed')
      if (job.cancel === 'unsupported') bits.push('— this provider has no cancel operation, so it will run to completion')
      return `${bits.join(' ')}.`
    }
  }
}

export { generateVideoTool }
