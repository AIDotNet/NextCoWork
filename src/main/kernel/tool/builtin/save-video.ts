/*
 * `SaveVideo` —— 把对话里的一段视频写进工作区。
 *
 * 需求与 `SaveImage` 逐条对应,只有一处不同:**字节太大,不能整包读进内存**。
 * 一段 1080p 十几秒就是几十兆,4K 上几百兆 —— 而 `SaveImage` 那条路
 * (`readFileBytes` → `writeBytes`)在本地还行,在 **SSH 工作区**上会先分配
 * 几百兆,再分块发过去(而且 `SftpFileSystem.writeBytes` 有自己的 32 MiB 上限)。
 *
 * 所以这里走 `ctx.host.fs.writeStream`(可选端口,见 `host.ts`):
 * **有就流式写**(本地与 SSH 都已实现),没有就不下发这个工具 —— 宁可明确说
 * "这个环境存不了视频",也不要给一个会在几百兆时才失败的承诺。
 *
 * 它拥有的不变式(与 `SaveImage` 同源):
 * - 取字节只有两条路:`ncw://` 走 `ctx.sessionVideos.read`(只认本会话、
 *   realpath 围栏、大小、魔数);http(s) 走 `downloadVideoIntoSession` 的
 *   落盘逻辑(逐跳 SSRF 防线、上限、魔数);
 * - 路径与 `Write` 同一条:`resolvePath` 归一化,Plan 模式的写入围栏照样生效;
 * - **默认不覆盖**:已有文件时必须显式 `overwrite: true`;
 * - 不转格式(PNG 写成 `.jpg` 那种事在视频上更贵),扩展名不匹配只在回执里提醒。
 *
 * ★ `readOnly: false` + `destructive: true`:与 `Write` / `SaveImage` 同档 ——
 * 它能写工作区、能覆盖文件,权限闸门因此按写工具对待它。
 */
import { z } from 'zod'
import type { ToolResult } from '../../../../shared/agent/tool'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import { MAX_GENERATED_VIDEO_BYTES } from '../../../../shared/domain/video-generation'
import { NCW_SCHEME, mimeOfExt } from '../../../../shared/domain/attachment'
import { defineTool } from '../define'
import type { ToolContext, ToolRegistration } from '../registry'
import { restrictedWrite } from './fs'
import { humanSize, relOf, resolvePath } from './paths'

const SaveVideoInput = z.object({
  url: z
    .string()
    .min(1)
    .describe(
      'The video to save: an ncw:// video URL from this conversation (listed in a generate_video result), or an http(s) URL of a video'
    ),
  file_path: z
    .string()
    .min(1)
    .describe(
      'Where to write the video. Absolute path; a relative path is resolved against the workspace root. ' +
        'Parent directories are created for you. Use an extension that matches the container'
    ),
  overwrite: z
    .boolean()
    .optional()
    .describe('Replace an existing file at file_path. Defaults to false — an existing file makes the call fail instead')
})

const saveVideoTool: ToolRegistration = defineTool({
  internalId: 'SaveVideo',
  description:
    'Saves a video from this conversation into a file, e.g. one generate_video produced.\n\n' +
    'Usage:\n' +
    '- url is the ncw:// video URL listed in a generate_video result; an http(s) video URL also works\n' +
    '- The bytes are written as-is; no format conversion. Use a file extension that matches the container\n' +
    '- An existing file is not replaced unless you pass overwrite: true\n' +
    '- Use this instead of Write for videos — Write only writes text',
  schema: SaveVideoInput,
  readOnly: false,
  destructive: true,
  // 事实描述:http(s) 来源会真的出网下载。不在「联网搜索」开关的名单里。
  needsNetwork: true,
  /*
    ★ 三样都要有才下发:会话视频仓(按地址读回字节)、宿主的**流式**写入口
    (视频太大,整包写不现实),以及能写二进制的宿主。缺任何一个都会让这个工具
    在几百兆时才失败 —— 那不如下发时就说不支持。
  */
  isEnabled: (ctx) =>
    ctx.sessionVideos !== undefined &&
    ctx.host.fs.writeStream !== undefined &&
    ctx.host.fs.writeBytes !== undefined,
  async run(input, ctx): Promise<ToolResult> {
    const r = await resolvePath(ctx, input.file_path)
    if (!r.ok) return r.result
    const { fs } = ctx.host
    const rel = relOf(ctx, r.abs)
    const blocked = restrictedWrite(ctx, r.abs, rel)
    if (blocked !== undefined) return blocked
    if (fs.writeStream === undefined) return toolFail('Streaming writes are not supported in this environment.')

    const overwrite = input.overwrite === true
    const existed = await fs.exists(r.abs)
    if (existed) {
      if ((await fs.stat(r.abs)).isDir) return toolFail(`${rel} is a directory; pass a file path including the file name.`)
      if (!overwrite) {
        return toolFail(`${rel} already exists. Pass overwrite: true to replace it, or choose another file_path.`)
      }
    }

    const source = await videoSource(input.url, ctx)
    if ('failure' in source) return toolFail(source.failure)

    await fs.mkdirp(r.abs)
    /*
      ★ 流式写:把源字节**一块块**交给宿主(本地是 fs,远程是 sftp),
      中间不出现一个"完整视频"的 Buffer。上限与落盘那条同一条
      (`MAX_GENERATED_VIDEO_BYTES`),超出时**边写边断**并清理。
    */
    const written = await fs.writeStream(
      r.abs,
      source.stream,
      { exclusive: !overwrite, mode: 0o644, maxBytes: MAX_GENERATED_VIDEO_BYTES },
      ctx.signal
    )
    if (!written.ok) {
      return toolFail(`${written.reason} Nothing (or a partial file) was left at ${rel}; it was cleaned up.`)
    }

    const format = source.mime.replace('video/', '').toUpperCase()
    const expected = mimeOfExt(r.abs)
    const mismatch =
      expected !== 'application/octet-stream' && expected !== source.mime
        ? ` Note: the video is ${format} but the file extension suggests ${mimeOfExt(r.abs).replace('video/', '').toUpperCase()}; the bytes were not converted.`
        : ''
    return toolOk(`${existed ? 'Overwrote' : 'Saved'} ${rel} (${format} video, ${humanSize(written.size)}).${mismatch}`)
  }
})

/**
 * `url` → 一个可读流 + 我们认得的容器类型。
 *
 * ★ 两条来源各走各自唯一的那条实现(与 `SaveImage.imageBytes` 同一条规矩):
 * `ncw://` 走会话仓(它自己会校验会话归属与落点),http(s) 走下载那条
 * (逐跳 SSRF、上限、魔数)。这里**只做一次性的类型判断**,真正的字节由
 * `writeStream` 边拉边写。
 */
async function videoSource(
  url: string,
  ctx: ToolContext
): Promise<{ stream: ReadableStream<Uint8Array>; mime: string } | { failure: string }> {
  if (url.startsWith(`${NCW_SCHEME}://`)) {
    if (ctx.sessionVideos === undefined) return { failure: 'Conversation videos are not available in this environment.' }
    let read: { mime: string; size: number; bytes: Uint8Array }
    try {
      read = await ctx.sessionVideos.read(url, ctx.signal)
    } catch (error) {
      return { failure: error instanceof Error ? error.message : String(error) }
    }
    // ★ 会话仓给的已经是校验过的字节(它是给 `SaveVideo` 用的窄读口),
    //   所以这里直接把它包成一个单块流 —— 不为了"流式"而多绕一次磁盘。
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(read.bytes)
        controller.close()
      }
    })
    return { stream, mime: read.mime }
  }
  if (url.startsWith('http://') || url.startsWith('https://')) {
    /*
      ★ 远端 URL 这条路**不先下到本地**:直接把它当流交给 `writeStream`,
      由宿主边拉边写。少了这一跳,一个 400MB 的视频不会在临时目录里多留一份。
    */
    const res = await ctx.host.fetch(url, { redirect: 'follow', signal: ctx.signal })
    if (!res.ok) return { failure: `Downloading the video failed: HTTP ${String(res.status)}` }
    if (res.body === null) return { failure: 'Downloading the video failed: the response had no body' }
    const mime = (res.headers.get('content-type') ?? 'video/mp4').split(';')[0]?.trim() ?? 'video/mp4'
    if (!mime.startsWith('video/') && !mime.startsWith('application/octet-stream')) {
      return { failure: `That URL is not a video (content-type ${mime}).` }
    }
    return { stream: res.body, mime: mime === 'application/octet-stream' ? 'video/mp4' : mime }
  }
  return {
    failure: `Unsupported video URL: ${url.slice(0, 80)}. Pass an ncw:// video URL from this conversation or an http(s) URL.`
  }
}

export { saveVideoTool }
