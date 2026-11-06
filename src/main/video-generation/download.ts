/**
 * 生成视频的落盘 —— **流式**,不走图片那条 32 MiB / Base64 的通路。
 *
 * ## 为什么必须单开一条
 *
 * 图片那侧的上传(`services/attachments.ts` 的 \`uploadAttachment`)拿的是**已经内联的
 * Uint8Array**,上限 32 MiB。那对图片是对的(量级由"一次 IPC 结构化克隆"决定),
 * 但视频有两个它兜不住的性质:**大**(1080p 十几秒就几十兆,4K 上几百兆)、
 * **在远端**(成品是上游给的 URL,要边下边写,而不是先整个收进内存)。
 *
 * 所以这里:临时文件 + 边读边限流/算校验和 + 原子 rename,全程不进 renderer。
 *
 * ## 三个必须做对的判据
 *
 * 1. **上限** `MAX_GENERATED_VIDEO_BYTES`:超了当场断,**不继续收**
 *    (收完再删和没拒收是两回事 —— 中间那几百兆已经落在磁盘上了);
 * 2. **格式** 容器签名(ftyp / EBML)认一次,**以字节为准**。上游回一个 HTML
 *    错误页时,存成 \`.mp4\` 会让用户拿到一个打不开的文件,而整条链路报"成功";
 * 3. **会话归属** 路径由 scope + ownerId 拼出来,不接受调用方给任意路径。
 *
 * ## 与 `ncw://` 的关系
 *
 * 落盘后登记成**与用户附件同一张表**里的一行(draft → 随消息提交),于是地址就是
 * 能直接播的 \`ncw://attachments/sessions/<会话>/<ULID>.mp4\` —— Range 请求由
 * 那个协议处理(见 \`net/attachment-protocol.ts\`)。
 */
import { createHash } from 'node:crypto'
import { closeSync, mkdirSync, openSync, readSync, renameSync, rmSync, writeSync } from 'node:fs'
import { basename, join } from 'node:path'
import type { VideoAssetRef } from '../../shared/domain/video-generation'
import { MAX_GENERATED_VIDEO_BYTES, videoExtOfMime, videoMimeOfBytes } from '../../shared/domain/video-generation'
import { buildNcwUrl, extOfMime } from '../../shared/domain/attachment'
import { attachmentRoot } from '../services/attachment-paths'
import * as repo from '../db/repo'
import { ulid } from '../../shared/util/id'

export interface VideoDownloadDeps {
  fetch: typeof fetch
  /** 这条视频属于哪个会话。地址的 owner 与读回时的唯一凭据 */
  sessionId: string
  now(): number
}

export interface StoredVideo {
  asset: VideoAssetRef
  /** 真正的落盘路径(诊断用,**不出主进程**) */
  path: string
}

const MAX_REDIRECTS = 5

/**
 * 从上游给的地址把视频下下来,流式写进本会话的附件目录。
 *
 * ★ `headers` 只发给**第一跳**。Google 的下载 URI 要带 key,而它可能 302 到一个
 * 匿名 CDN —— 把密钥跟着跳转发过去等于把它交给另一台主机。所以逐跳重算:
 * 同源才带,跨源就丢掉(与 `kernel/image-gen.ts` 的 `downloadImage` 同一条取舍)。
 */
export async function downloadVideoIntoSession(
  deps: VideoDownloadDeps,
  remote: { url: string; headers?: Record<string, string>; mime?: string },
  signal: AbortSignal
): Promise<StoredVideo> {
  /*
    ★ S3(`s3://…`,AWS Nova Reel 的输出)不接受匿名 HTTP —— 它要 SigV4 GET。
    这里明确拒绝,而不是把它当普通 URL 去 fetch 一个 `s3://`(那会失败在一个
    读不懂的地方)。
  */
  if (remote.url.startsWith('s3://')) {
    throw new Error('This provider returns its video in S3; configure the output bucket to enable downloads (not wired yet).')
  }
  let start: URL
  try {
    start = new URL(remote.url)
  } catch {
    throw new Error(`Invalid video URL: ${remote.url.slice(0, 120)}`)
  }
  if (start.protocol !== 'https:' && start.protocol !== 'http:') {
    throw new Error(`Refusing to download a video over ${start.protocol}`)
  }

  const id = ulid()
  let mime = remote.mime ?? 'video/mp4'
  const dir = join(attachmentRoot(), 'sessions', deps.sessionId)
  mkdirSync(dir, { recursive: true })
  let targetPath = join(dir, `${id}${videoExtOfMime(mime)}`)
  // 临时名以 `.` 开头 —— 协议层按段校验就够不着它,清理也按前缀认残片。
  const tmp = join(dir, `.${id}.part`)

  const hash = createHash('sha256')
  let total = 0
  let handle: number | null = null
  try {
    const origin = start.origin
    let current = start
    let headers: Record<string, string> = { ...(remote.headers ?? {}) }
    let res: Response | null = null
    for (let hop = 0; hop <= MAX_REDIRECTS; hop += 1) {
      res = await deps.fetch(current, { redirect: 'manual', headers, signal })
      const location = res.status >= 300 && res.status < 400 ? res.headers.get('location') : null
      if (location === null) break
      await res.body?.cancel().catch(() => {})
      if (hop === MAX_REDIRECTS) throw new Error('Downloading the video failed: too many redirects')
      current = new URL(location, current)
      // ★ 跨源就丢掉鉴权头 —— 见上面那段。
      if (current.origin !== origin) headers = {}
    }
    if (res === null) throw new Error('Downloading the video failed')
    if (!res.ok) throw new Error(`Downloading the video failed: HTTP ${String(res.status)}`)

    const declared = Number(res.headers.get('content-length') ?? '')
    if (Number.isFinite(declared) && declared > MAX_GENERATED_VIDEO_BYTES) {
      throw new Error(`The generated video exceeds the ${sizeText()} limit`)
    }
    const body = res.body
    if (body === null) throw new Error('Downloading the video failed: the response had no body')

    handle = openSync(tmp, 'w')
    const reader = body.getReader()
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break
      if (value === undefined) continue
      total += value.byteLength
      if (total > MAX_GENERATED_VIDEO_BYTES) {
        await reader.cancel().catch(() => {})
        throw new Error(`The generated video exceeds the ${sizeText()} limit`)
      }
      hash.update(value)
      writeSync(handle, value)
    }
    closeSync(handle)
    handle = null
    if (total === 0) throw new Error('The generated video was empty')

    /*
      ★★ 读完才知道容器是什么,而且**以字节为准**。上游回一个 HTML 错误页时,
      按声明的 mime 存成 `.mp4` 会让用户拿到一个打不开的文件,而链路报"成功"。
    */
    const actual = videoMimeOfBytes(readHead(tmp))
    if (actual === null) {
      throw new Error('The generated video is not a recognized container (mp4/webm/mov/mkv)')
    }
    if (actual !== mime) {
      mime = actual
      targetPath = join(dir, `${id}${extOfMime(mime)}`)
    }

    renameSync(tmp, targetPath)
    const now = deps.now()
    repo.putDraftAttachment({
      id,
      scope: 'session',
      ownerId: deps.sessionId,
      path: targetPath,
      size: total,
      checksum: hash.digest('hex'),
      displayName: `generated${extOfMime(mime)}`,
      createdAt: now
    })
    const url = buildNcwUrl({ scope: 'session', ownerId: deps.sessionId, fileName: basename(targetPath) })
    if (url === null) throw new Error('Unable to build an attachment URL for the generated video')
    return { asset: { url, mime, size: total }, path: targetPath }
  } catch (error) {
    if (handle !== null) {
      try { closeSync(handle) } catch { /* 已经关了 */ }
    }
    // ★ 失败清理临时文件 —— 一个 200MB 的半截文件留在磁盘上,而且没人会再引用它。
    try { rmSync(tmp, { force: true }) } catch { /* 尽力而为 */ }
    throw error
  }
}

/** 读前 16 字节判容器签名。 */
function readHead(path: string): Uint8Array {
  const fd = openSync(path, 'r')
  try {
    const buffer = new Uint8Array(16)
    const read = readSync(fd, buffer, 0, buffer.length, 0)
    return buffer.subarray(0, read)
  } finally {
    closeSync(fd)
  }
}

function sizeText(): string {
  return `${String(Math.round(MAX_GENERATED_VIDEO_BYTES / 1024 / 1024))} MB`
}
