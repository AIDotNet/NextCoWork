/*
 * `SaveImage` —— 把对话里的一张图写进工作区。
 *
 * 需求:用户说「把这张图存到 assets/logo.png」时,Agent 手上只有一个地址:
 * 用户粘贴的图在上下文里附着 `[Image URL: ncw://…]`,`generate_image` 的回执逐张列出
 * `ncw://…`(见 `kernel/session-images.ts`)。`Write` 只写文本,把 base64 当文本写进去
 * 得到的是一个打不开的文件 —— 所以需要一个按地址取字节、原样落盘的工具。
 *
 * 它拥有的不变式:
 * - 取字节只有两条路:`ncw://`(以及内联 data URL)走 `ctx.sessionImages.read`
 *   = `resolveImageDataRef`,只认本会话、realpath 围栏、大小、魔数;http(s) 走
 *   `downloadImage`(逐跳 ssrfRisk、32MB、魔数)。地址本身不是权限,会话归属才是。
 * - 路径与 `Write` 同一条:`resolvePath` 归一化,Plan 模式的写入围栏(`restrictedWrite`)照样生效。
 * - **默认不覆盖**:已有文件时必须显式 `overwrite: true`。`Write` 的「先读后写」
 *   在二进制上无从谈起(`Read` 拒读二进制),换成显式开关守同一件事 —— 不让模型
 *   凭空盖掉一个它从没看过的文件。不覆盖时用 `exclusive` 写,由文件系统原子判定。
 * - 本地与 SSH 远程工作区都能写:落盘走 `ctx.host.fs.writeBytes`,远程时它就是 sftp。
 *
 * 故意不做:不转格式(PNG 写成 `.jpg` 时只在回执里提醒,不偷偷转码);不进撤销/审查
 * 记录(`recordChange` 记的是文本前后内容,二进制放进去只会让 diff 视图出乱码)。
 *
 * ★ `readOnly: false` + `destructive: true`:与 `Write` 同档 —— 它能写工作区、能覆盖文件,
 * 权限闸门因此按写工具对待它。
 */
import { z } from 'zod'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import type { ToolResult } from '../../../../shared/agent/tool'
import { MAX_ATTACHMENT_BYTES, mimeOfExt, NCW_SCHEME, normalizeImageMime } from '../../../../shared/domain/attachment'
import { defineTool } from '../define'
import { downloadImage } from '../../image-gen'
import type { ToolContext, ToolRegistration } from '../registry'
import { restrictedWrite } from './fs'
import { humanSize, relOf, resolvePath } from './paths'

const SaveImageInput = z.object({
  url: z
    .string()
    .min(1)
    .describe(
      'The image to save: an ncw:// image URL from this conversation (shown next to attached images and listed in ' +
        'generate_image results), or an http(s) URL of an image'
    ),
  file_path: z
    .string()
    .min(1)
    .describe(
      'Where to write the image. Absolute path; a relative path is resolved against the workspace root. ' +
        'Parent directories are created for you. Use an extension that matches the image format'
    ),
  overwrite: z
    .boolean()
    .optional()
    .describe('Replace an existing file at file_path. Defaults to false — an existing file makes the call fail instead')
})

/** 地址 → 字节。两条来源各走各自唯一的那条安全实现(见文件头)。 */
async function imageBytes(url: string, ctx: ToolContext): Promise<{ mime: string; bytes: Uint8Array } | ToolResult> {
  if (url.startsWith(`${NCW_SCHEME}://`) || url.startsWith('data:')) {
    // isEnabled 已经要求它存在;这里兜的是快照之后被摘掉的窄窗口
    if (ctx.sessionImages === undefined) return toolFail('Conversation images are not available in this environment.')
    return ctx.sessionImages.read(url, ctx.signal)
  }
  if (url.startsWith('http://') || url.startsWith('https://')) {
    const image = await downloadImage(ctx.host.fetch, url, ctx.signal)
    const at = image.dataRef.indexOf(';base64,')
    return { mime: image.mime, bytes: new Uint8Array(Buffer.from(image.dataRef.slice(at + ';base64,'.length), 'base64')) }
  }
  return toolFail(
    `Unsupported image URL: ${url.slice(0, 80)}. Pass an ncw:// image URL from this conversation or an http(s) image URL.`
  )
}

const saveImageTool: ToolRegistration = defineTool({
  internalId: 'SaveImage',
  description:
    'Saves an image from this conversation into a file, e.g. an image the user attached or one generate_image produced.\n\n' +
    'Usage:\n' +
    '- url is the ncw:// image URL shown next to an attached image ([Image URL: ncw://...]) or listed in a generate_image result; an http(s) image URL also works\n' +
    '- The image bytes are written as-is; no format conversion. Use a file extension that matches the image format\n' +
    '- An existing file is not replaced unless you pass overwrite: true\n' +
    '- Use this instead of Write for images — Write only writes text',
  schema: SaveImageInput,
  readOnly: false,
  destructive: true,
  // 事实描述:http(s) 来源会真的出网下载。不在「联网搜索」开关的名单里(那颗只管网页搜索与抓取)
  needsNetwork: true,
  // 写不了二进制的宿主、或没装配会话图片仓时整体不下发 —— 不画注定失败的承诺
  isEnabled: (ctx) => ctx.sessionImages !== undefined && ctx.host.fs.writeBytes !== undefined,
  async run(input, ctx): Promise<ToolResult> {
    const r = await resolvePath(ctx, input.file_path)
    if (!r.ok) return r.result
    const { fs } = ctx.host
    const rel = relOf(ctx, r.abs)
    const blocked = restrictedWrite(ctx, r.abs, rel)
    if (blocked !== undefined) return blocked
    if (fs.writeBytes === undefined) return toolFail('Writing binary files is not supported in this environment.')

    const overwrite = input.overwrite === true
    const existed = await fs.exists(r.abs)
    if (existed) {
      if ((await fs.stat(r.abs)).isDir) return toolFail(`${rel} is a directory; pass a file path including the file name.`)
      if (!overwrite) {
        return toolFail(`${rel} already exists. Pass overwrite: true to replace it, or choose another file_path.`)
      }
    }

    const image = await imageBytes(input.url, ctx)
    if ('output' in image) return image
    if (image.bytes.byteLength === 0 || image.bytes.byteLength > MAX_ATTACHMENT_BYTES) {
      return toolFail(`The image size (${humanSize(image.bytes.byteLength)}) is outside the allowed range; nothing was written.`)
    }

    await fs.mkdirp(r.abs)
    // 不覆盖时 exclusive:exists 与写之间被别人抢先创建,由文件系统拒绝而不是静默盖掉
    await fs.writeBytes(r.abs, image.bytes, { exclusive: !overwrite, mode: 0o644 })

    const format = image.mime.replace('image/', '').toUpperCase()
    const expected = normalizeImageMime(mimeOfExt(r.abs))
    const mismatch = expected !== null && expected !== normalizeImageMime(image.mime)
      ? ` Note: the image is ${format} but the file extension suggests ${expected.replace('image/', '').toUpperCase()}; the bytes were not converted.`
      : ''
    return toolOk(
      `${existed ? 'Overwrote' : 'Saved'} ${rel} (${format} image, ${humanSize(image.bytes.byteLength)}).${mismatch}`
    )
  }
})

export { saveImageTool }
