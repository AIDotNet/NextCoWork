/*
 * `generate_image` —— 对话里「画一张 / 改这张」的落地点。
 *
 * 需求:设置页「图片生成」tab 配好的生图供应商,要在对话里真的能用:
 * 1. 文生图:只给 `prompt`;
 * 2. 改图:`image: "latest"`(对话里最近一张图——用户刚上传的附件、或上一轮
 *    自己生成的那张)、`image: <ncw:// 会话图片地址>`、`image: <工作区图片路径>`,
 *    或 `image: <http(s) 图片 URL>`。
 * 3. 一次多张:`n`(1–4)。用户要「几个备选 / 几种风格」时一次调用出齐,
 *    而不是让模型连调四次 —— 四次调用是四张各自折叠的卡片,也没法并排挑。
 *    每到手一张就经 `ctx.emit` 推一张给生成期的卡片(`ToolProgress.image`)。
 * 4. 生成图要能被**点名**:每张图经 `ctx.sessionImages` 落成本会话附件,
 *    `output.images` 存 `ncw://` 地址,回执逐张列出 —— 模型拿它改图、或交给
 *    `SaveImage` 写进工作区(见 `kernel/session-images.ts`)。
 *
 * 这个工具只负责「把 prompt 和源图递出去、把图接回来」;用哪个模型、哪把密钥、
 * `/images/edits` 的形状兼容、`ncw://` 附件的安全解析全在桥里
 * (`kernel/image-gen.ts` + `upstream/images.ts`),所以这里零 store、零凭证接触。
 *
 * ★ `isEnabled` 是**不做防御式 UI** 的那半:没选生图模型(设置里 `imageModel`
 * 空着或解析不到)时整个工具不下发,模型看不到一个注定失败的承诺
 * (而不是下发了再回一句「没配」)。
 * 反过来,配了模型但没配 key 时**照常下发** —— 密钥要读 secrets(异步),
 * 同步谓词答不了,而调用时那句 `no API key` 本身就是可行动的反馈
 * (理由全文见 `ImageGenBridge.available` 上那段)。
 *
 * ★ `readOnly: true`:它不写工作区、不改任何工作区状态,产出只挂在这条工具结果上
 * —— 与 `browser_screenshot` / `visualize_show_widget` 同一档(代价是权限闸门
 * 对它一路放行)。生成图落进的是**应用自己的会话附件存储**(与用户粘贴的图同一处,
 * 随会话删除),不是工作区,所以这一档不变;写进工作区是 `SaveImage` 的事,它不是只读。
 * 原先还有「联网开关仍然管得住它」这层兜底;用户把「联网搜索」
 * 收窄成只管网页搜索与抓取之后,管住它的是它**自己的**开关
 * (`AppSettings.imageGenerationEnabled`,经桥的 `available()` / `enabled()` 生效)。
 */
import { z } from 'zod'
import type { AgentMessage, ToolOutputImage } from '../../../../shared/agent/message'
import type { ToolResult } from '../../../../shared/agent/tool'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import { imageMimeOfBytes, MAX_ATTACHMENT_BYTES, mimeOfExt, NCW_SCHEME } from '../../../../shared/domain/attachment'
import { MAX_IMAGE_COUNT } from '../../../../shared/domain/image-count'
import { defineTool } from '../define'
import { downloadImage, type ImageGenOptions, type ImageSource } from '../../image-gen'
import type { ToolContext, ToolRegistration } from '../registry'
import { resolvePath } from './paths'

/** `image` 参数取这个字面量 = 「对话里最近一张图」(与文件路径区分开的关键词) */
const LATEST = 'latest'

const GenerateImageInput = z.object({
  prompt: z.string().min(1).describe('What to draw — a detailed natural-language description of the image'),
  image: z
    .string()
    .min(1)
    .optional()
    .describe(
      'Which image to edit: "latest" for the most recent image in this conversation, an ncw:// image URL shown in ' +
        'this conversation, a workspace file path, or an http(s) URL of an image. Omit it to generate a new image from the prompt alone.'
    ),
  n: z
    .number()
    .int()
    .min(1)
    .max(MAX_IMAGE_COUNT)
    .optional()
    .describe(
      `How many images to produce in this one call (1–${String(MAX_IMAGE_COUNT)}, default 1). ` +
        'Use more than 1 only when the user wants several options or variations — each image is billed separately.'
    )
})

/**
 * 往回找对话里最近的一张图。
 *
 * 需求:模型**看不到**图片的 dataRef(图是以图像块发给它的,转录里的引用字符串
 * 不进它的上下文),所以它没法「点名」某张图 —— 只能由我们替它认最近那张。
 * 用户说「把这张改成黑白」时,「这张」几乎总是刚发来的附件或刚生成的那张。
 * (现在 `ncw://` 图会在上下文里附一行地址、生成图的地址写在回执里,模型**能**点名了;
 * `latest` 仍保留,因为「刚才那张」是最常见的说法,且旧转录里的内联图没有地址。)
 *
 * ★ 两种来源都要认:消息里的 `image` part(用户附件,可能是 `ncw://` 未解析引用)
 * 和工具结果里的 `output.images`(截图是内联 data URL;生成图是 `ncw://`,旧转录里是内联)。
 * 从**后往前**扫 —— 取到的就是「刚才那张」。
 */
function latestImage(messages: readonly AgentMessage[] | undefined): ImageSource | null {
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

/**
 * 工作区里的一张图 → data URL。
 *
 * ★ 路径必须过 `resolvePath`(和 `Read` 同一条归一化/围栏,理由见 `fs.ts` 文件头);
 * 字节用**魔数**认 mime(`imageMimeOfBytes`),不信扩展名 —— 改过名的 `.png` 里
 * 装着 JPEG 时,自报的 mime 会让上游按错的类型解码。
 * ★ 大小上限沿用附件那条 32MB:比这更大的图上游多半也拒,先在这儿说人话。
 */
async function readWorkspaceImage(rawPath: string, ctx: ToolContext): Promise<ImageSource> {
  const resolved = await resolvePath(ctx, rawPath)
  if (!resolved.ok) throw new Error(resolved.result.output.content)
  const { fs } = ctx.host
  if (!(await fs.exists(resolved.abs))) throw new Error(`File does not exist: ${rawPath}`)
  const stat = await fs.stat(resolved.abs)
  if (stat.isDir) throw new Error(`${rawPath} is a directory, not an image file`)
  if (stat.size <= 0 || stat.size > MAX_ATTACHMENT_BYTES) {
    throw new Error(
      `Image file size (${String(stat.size)} bytes) is outside the allowed range of 1–${String(MAX_ATTACHMENT_BYTES)} bytes`
    )
  }
  const bytes = await fs.readFileBytes(resolved.abs, stat.size + 1)
  if (bytes.length !== stat.size) throw new Error(`Image file changed while it was being read: ${rawPath}`)
  const mime = imageMimeOfBytes(bytes)
  if (mime === null) throw new Error(`Unrecognized image format: ${rawPath} (jpg/png/gif/webp only)`)
  return { mime, dataRef: `data:${mime};base64,${Buffer.from(bytes).toString('base64')}` }
}

const generateImageTool: ToolRegistration = defineTool({
  internalId: 'generate_image',
  description:
    'Generate an image from a text prompt, or edit an existing one, using the image provider configured in this app ' +
    '(Settings > Models > Image generation). The resulting image is returned inline in the tool result so the user can see it, ' +
    'and the result text lists an ncw:// URL for each image.\n' +
    '- Generate: pass only `prompt`.\n' +
    '- Edit: pass `image` as "latest" to modify the most recent image in this conversation, an ncw:// image URL, a workspace file path, or an image URL.\n' +
    '- Several options: pass `n` (up to 4) to get multiple images from one call instead of calling the tool repeatedly.\n' +
    'A URL that cannot be downloaded is an error — the tool will not fall back to generating a new image.\n' +
    'Use it whenever the user asks to draw, paint, illustrate, render, generate, or modify a picture, photo, or artwork. ' +
    'It does not write into the workspace — use SaveImage with the ncw:// URL when the user wants the image saved as a file.',
  schema: GenerateImageInput,
  readOnly: true,
  destructive: false,
  // 事实描述:生图/改图真出网(POST 生图接口、下载 URL 源图)。它**不再**让「联网搜索」
  // 开关管住这个工具 —— 那颗开关只按 NETWORK_SWITCH_TOOLS 名单判;生图的启停看
  // `AppSettings.imageGenerationEnabled`(见文件头 readOnly 那段)
  needsNetwork: true,
  isEnabled: (ctx) => ctx.imageGen?.available() === true,
  async run(input, ctx): Promise<ToolResult> {
    if (ctx.imageGen === undefined) {
      // isEnabled 已经挡住正常路径;这里兜的是「快照之后桥被摘掉」的窄窗口 ——
      // 必须是 toolFail:报成功却没有图,模型会向用户宣布「已经画好了」
      return toolFail('Image generation is not available in this environment.')
    }

    let source: ImageSource | undefined
    if (input.image !== undefined) {
      if (input.image === LATEST) {
        const latest = latestImage(ctx.messages)
        // 没有图可改时当场说清,而不是硬发一个没有 image 的 edits 请求 ——
        // 那种 400 读不出和「对话里根本没有图」的关系
        if (latest === null) {
          return toolFail('There is no image in this conversation to edit. Ask the user to attach one, or pass a workspace file path or an image URL.')
        }
        source = latest
      } else if (input.image.startsWith(`${NCW_SCHEME}://`)) {
        /*
          需求:模型把上下文里看到的 `ncw://` 地址原样传回来,改的就是那一张。
          这里**不读文件** —— 源图原样交给 `edit()`,由桥的 `resolveImage`
          (= `resolveImageDataRef`)做会话归属与路径围栏;在这儿先读一遍等于多一条
          绕过那套校验的路。mime 只是按扩展名给的猜测,解析时以字节为准。
        */
        source = { mime: mimeOfExt(input.image), dataRef: input.image }
      } else if (input.image.startsWith('http://') || input.image.startsWith('https://')) {
        /*
          ★ 需求:模型传了图片 URL 就是要改**这一张** —— 下载失败 / 404 / 不是图片
          时必须失败,**绝不回落成文生图**。回落的症状是用户说「把这张图改成黑白」,
          模型却悄悄画了一张全新的图、还向用户宣布改好了;而 URL 死了这件事
          (图床过期、链接贴错)就此永远传不到用户耳朵里。
          下载走 `downloadImage`:与「上游回包里的 url」共用同一条实现
          (逐跳 ssrfRisk、跳转手动跟、32MB 上限、魔数认格式)。
          这里直接抛出去 —— `defineTool` 把普通错误翻成 toolFail 带上原因,
          中断照旧上抛(`isAbortError` 分流),不会被这句话吞掉。
        */
        source = await downloadImage(ctx.host.fetch, input.image, ctx.signal)
      } else {
        source = await readWorkspaceImage(input.image, ctx)
      }
    }

    const count = input.n ?? 1
    let delivered = 0
    const options: ImageGenOptions = {
      count,
      /*
        需求:生成期卡片逐张把占位换成真图。`message` 只写 `2/4` 这种语言无关的计数 ——
        它会原样出现在工具行右端,主进程这里写任何一种语言的句子,切了界面语言都不会跟着变。
      */
      onImage: (index, image) => {
        delivered += 1
        ctx.emit({
          callId: ctx.callId,
          message: `${String(delivered)}/${String(count)}`,
          fraction: delivered / count,
          image: { index, image }
        })
      }
    }
    const result =
      source === undefined
        ? await ctx.imageGen.generate(input.prompt, ctx.signal, options)
        : await ctx.imageGen.edit(input.prompt, source, ctx.signal, options)
    const action = source === undefined ? 'Generated' : 'Edited'
    const got = result.images.length
    const noun = got === 1 ? 'image' : 'images'
    const attached = got === 1 ? 'The image is attached below.' : 'The images are attached below.'
    /*
      ★ 部分失败**必须写进回执**:要 4 张拿到 3 张时,不写的话模型只看得见 3 张图,
      却会按用户的原话宣布「画好了 4 张」。原因去重后附上,同一条限流报四遍没有信息量。
    */
    const failed = result.failures.length === 0
      ? ''
      : ` ${String(result.failures.length)} of ${String(result.requested)} requested images failed: ${[...new Set(result.failures)].join('; ')}`
    const stored = await storeImages(result.images, ctx)
    return toolOk(
      `${action} ${String(got)} ${noun} with ${result.model} via ${result.providerName}. ${attached}${failed}${stored.note}`,
      { images: stored.images }
    )
  }
})

/**
 * 生成图 → 本会话附件,并写出回执里那段地址清单。
 *
 * 需求:模型要能在后续调用里点名这几张图(改图的 `image`、`SaveImage` 的 `url`)。
 * 它看不到 dataRef,所以地址必须**写进回执正文** —— 只换 `output.images` 的话,
 * 地址存在转录里,模型却一个字也读不到。
 *
 * ★ 存不下来的那张**退回内联 data URL**,不让整次调用失败:图已经生成、已经计费、
 * 用户已经在卡片上看见了。回执里写明哪几张没有地址、为什么 —— 否则模型会对着
 * 一张没有地址的图编一个出来。没装配仓(纯内核测试)时一律内联、不列地址。
 */
async function storeImages(
  images: readonly ToolOutputImage[],
  ctx: ToolContext
): Promise<{ images: ToolOutputImage[]; note: string }> {
  const store = ctx.sessionImages
  if (store === undefined || images.length === 0) return { images: [...images], note: '' }
  const out: ToolOutputImage[] = []
  const urls: string[] = []
  const reasons: string[] = []
  for (const image of images) {
    try {
      const saved = await store.save(image)
      out.push(saved)
      urls.push(saved.dataRef)
    } catch (error) {
      out.push(image)
      reasons.push(error instanceof Error ? error.message : String(error))
    }
  }
  const list = urls.length === 0
    ? ''
    : `\nImage URLs (pass one as \`image\` to edit it, or to SaveImage to write it into the workspace):\n${urls.map((url) => `- ${url}`).join('\n')}`
  const lost = reasons.length === 0
    ? ''
    : `\n${String(reasons.length)} image(s) could not be stored and have no URL: ${[...new Set(reasons)].join('; ')}`
  return { images: out, note: `${list}${lost}` }
}

export { generateImageTool }
