/**
 * `ncw://` 静态资源协议 —— 渲染层直接引用本地附件,不再走 IPC 传字节。
 *
 * ## 为什么要一个自定义协议
 *
 * 在此之前,渲染层要显示一张本地图只能走 `theme:readImage` 那条路:把整个文件
 * 作为 `Uint8Array` 经结构化克隆送过去,渲染层再 `new Blob` → `createObjectURL`。
 * 代价是**每显示一次全量拷贝一次**,而且拿不到浏览器的图片解码流水线、磁盘缓存、
 * Range 请求 —— 视频连拖进度条都做不到。
 *
 * 换成协议之后这些全部由 Chromium 接管,附件的显示路径上不再有我们的代码。
 *
 * ## 两段注册,顺序不能反
 *
 * `registerAttachmentScheme()` 必须在 `app.whenReady()` **之前**调用。
 * 放到 ready 之后**不报错**,但 privileges 全部丢失 —— 表现是「图片有时能显示、
 * fetch 报 CORS、视频不能拖进度条」这类看起来彼此无关的散装故障。
 *
 * ## 安全
 *
 * 协议是**渲染层可以任意构造输入**的入口,与 `workspace:listDir` 同级别的不可信
 * 输入。防线是两层且都必须在:
 *
 * 1. `parseNcwUrl`(纯函数,`shared/domain/attachment.ts`)—— 解码后逐段校验
 * 2. `resolveAttachmentPath` 的 `isWithinRoot` —— 拼完再验一次落点
 *
 * 第二层不是冗余:第一层管的是「URL 长得对不对」,第二层管的是「拼出来的路径
 * 落在哪」。符号链接只有第二层能拦(而它拦不住 —— 见 `realpath` 那条注释)。
 */
import { nativeImage, net, protocol } from 'electron'
import { databaseDirectory } from '../db'
import { lstat, readFile, realpath } from 'node:fs/promises'
import { createReadStream } from 'node:fs'
import { Readable } from 'node:stream'
import { isAbsolute, join, relative, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import {
  NCW_HOST,
  NCW_PREVIEW_PARAM,
  NCW_PREVIEW_THUMB,
  NCW_SCHEME,
  attachmentRelPath,
  mimeOfExt,
  parseNcwUrl
} from '../../shared/domain/attachment'
import { configProfileDirectory } from '../db/config-profile'
import { ATTACHMENTS_DIR, attachmentRoot } from '../services/attachment-paths'
import { THUMBNAIL_CACHE_DIR, ThumbnailCache, type ThumbnailCodec } from './attachment-thumbnail'

/*
  附件根搬去了 `services/attachment-paths`(那边零 Electron)。这里**原样重新导出**,
  于是旧调用点(`ipc/*`、`host/index`、测试)的 `from '../net/attachment-protocol'`
  一行都不用改。★ 根本身仍必须与 `ipc/storage.ts` 的 `attachmentDirectory()`
  是同一个目录 —— 清理扫的就是它。
*/
export { ATTACHMENTS_DIR, attachmentRoot }

/**
 * ★ 与 `ipc/storage.ts` 的 `isWithin` 同源。
 *
 * 那边是私有函数,而这里是**独立的安全边界**,不能靠 import 一个恰好还没被
 * 重构掉的私有符号。等 storage 那条线稳定后再合并成一处 —— 在那之前,
 * 两份相同的实现比一份跨模块的耦合安全。
 */
function isWithinRoot(root: string, target: string): boolean {
  const rel = relative(resolve(root), resolve(target))
  return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel))
}

/**
 * URL → 绝对路径。**这是整条链路上唯一需要单测的部分**,所以它是纯函数:
 * root 由调用方给,不碰 `app`,不碰文件系统。
 *
 * 返回 null = 拒绝(handler 侧回 403/404)。不抛异常 —— 调用点只需要一个二选一。
 */
export function resolveAttachmentPath(root: string, rawUrl: string): string | null {
  const loc = parseNcwUrl(rawUrl)
  if (loc === null) return null

  const rel = attachmentRelPath(loc)
  if (rel === null) return null

  const target = resolve(join(root, rel))
  // ★ 第二层:拼完再验落点。第一层验的是 URL 的形状,这一层验的是结果。
  if (!isWithinRoot(root, target)) return null
  return target
}

/**
 * 在 `app.whenReady()` **之前**调用。
 *
 * - `standard`:走标准 URL 解析,才有正常的 host/pathname 语义
 * - `secure`:视同 https,否则被当不安全来源,CSP 与 fetch 双双拦下
 * - `supportFetchAPI`:渲染层可以 `fetch('ncw://…')` 拿 blob
 * - `stream`:★ 视频/音频的 Range 请求靠它,否则大文件只能整包读
 */
export function registerAttachmentScheme(): void {
  protocol.registerSchemesAsPrivileged([
    {
      scheme: NCW_SCHEME,
      privileges: {
        standard: true,
        secure: true,
        supportFetchAPI: true,
        stream: true,
        // 同源自用,不开放跨源
        corsEnabled: false
      }
    }
  ])
}

/** 在 `app.whenReady()` 之后调用 */
export function installAttachmentProtocol(): void {
  protocol.handle(NCW_SCHEME, (request) => handleAttachmentRequest(request, attachmentRootFor(request.url)))
}

/**
 * 这次请求该落在哪个根上。
 *
 * ★ **只有主题图跟着配置作用域走。** 主题库是会跨账户泄漏的那一份
 * (`index.json` 列着上一个账户传过的壁纸),所以账户作用域落
 * `<attachments>/config-profiles/<hash>/themes`;`local` 落 `<attachments>`,
 * 于是 `themes/<file>` 还是老位置。
 *
 * ★ 会话附件**必须**留在 `attachmentRoot()`:它们由 `ipc/storage.ts` 的
 * 占用统计与孤儿清理按那个根扫描,换根等于让那些文件从统计里消失,
 * 并且下一次清理会把它们当孤儿删掉。
 */
export function attachmentRootFor(rawUrl: string): string {
  return parseNcwUrl(rawUrl)?.scope === 'theme'
    ? configProfileDirectory(attachmentRoot())
    : attachmentRoot()
}

/** 请求里的预览档:没写 → null;写了且是唯一认得的那个值 → 'thumb';其余 → 'invalid' */
function previewOf(rawUrl: string): 'thumb' | 'invalid' | null {
  let values: string[]
  try {
    values = new URL(rawUrl).searchParams.getAll(NCW_PREVIEW_PARAM)
  } catch {
    return null
  }
  if (values.length === 0) return null
  return values.length === 1 && values[0] === NCW_PREVIEW_THUMB ? 'thumb' : 'invalid'
}

/**
 * Electron 自带的解码/缩放(无新依赖)。★ 同步解码,所以缓存那一侧保证同一时刻只跑一张。
 * 原图不大于这一档时返回 null —— 回原图就好,没有必要多存一份。
 */
const electronThumbnailCodec: ThumbnailCodec = {
  render(bytes, mime, edge) {
    const image = nativeImage.createFromBuffer(Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength))
    if (image.isEmpty()) return null
    const { width, height } = image.getSize()
    if (Math.max(width, height) <= edge) return null
    const resized = width >= height
      ? image.resize({ width: edge, quality: 'good' })
      : image.resize({ height: edge, quality: 'good' })
    return mime === 'image/png' ? resized.toPNG() : resized.toJPEG(85)
  }
}

let thumbnailCache: ThumbnailCache | null = null

/** 缩略缓存在**数据根**下、附件根之外:它不是附件,不进附件表、不进备份、不被孤儿清理当成附件 */
function defaultThumbnails(): ThumbnailCache {
  thumbnailCache ??= new ThumbnailCache(() => join(databaseDirectory(), THUMBNAIL_CACHE_DIR), electronThumbnailCodec)
  return thumbnailCache
}

export async function handleAttachmentRequest(
  request: Request,
  root: string,
  thumbnails: ThumbnailCache = defaultThumbnails()
): Promise<Response> {
  const preview = previewOf(request.url)
  // ★ 预览参数只认一个取值。别的值不当作「没写」放过去:那会让一个拼错的地址悄悄回原图
  if (preview === 'invalid') return new Response('bad request', { status: 400 })
  const target = resolveAttachmentPath(root, request.url)
  if (target === null) {
    // ★ 403 而不是 404:两者要能区分开。404 是「这个 id 的文件没了」(正常,
    //   走 onError 显示占位),403 是「有人在构造越界路径」(应当被注意到)。
    return new Response('forbidden', { status: 403 })
  }

  /*
    `resolveAttachmentPath` deliberately stays pure so URL-shape tests do not
    need a filesystem.  Before handing the path to `file://`, however, resolve
    both sides through symlinks.  Otherwise a file placed at
    `attachments/sessions/S1/image.png` that points at `/etc/passwd` would pass
    the lexical boundary check and Chromium would follow the link for us.
  */
  /*
    ★ 两个都**留到 `try` 之外**:下面的 Range 分支要用它们,而那个分支不在这个
    `try` 的作用域里。catch 分支一律 return,所以到这里它们必然已赋值
    —— TypeScript 的控制流分析认得这一点,不需要给初值(给了初值反而会触发
    "这个初始值从未被读过"的 lint)。
  */
  let canonicalTarget: string
  let targetSize: number
  let targetMtimeMs: number
  try {
    const rootStat = await lstat(root)
    // The application owns this directory.  A symlinked root would turn the
    // entire protocol into an alias for an arbitrary external tree.
    if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
      return new Response('not found', { status: 404 })
    }
    const canonicalRoot = await realpath(root)
    let resolvedTarget: string
    try {
      resolvedTarget = await realpath(target)
    } catch (err) {
      if ((err as NodeJS.ErrnoException)?.code === 'ELOOP') return new Response('forbidden', { status: 403 })
      return new Response('not found', { status: 404 })
    }
    if (!isWithinRoot(canonicalRoot, resolvedTarget)) {
      return new Response('forbidden', { status: 403 })
    }
    const targetStat = await lstat(resolvedTarget)
    if (!targetStat.isFile()) return new Response('not found', { status: 404 })
    targetSize = targetStat.size
    targetMtimeMs = targetStat.mtimeMs
    canonicalTarget = resolvedTarget
  } catch (err) {
    // Missing files are normal (the user may have removed an attachment from
    // disk).  Do not expose filesystem errors or turn them into a stack trace
    // in the renderer.
    if ((err as NodeJS.ErrnoException)?.code === 'ELOOP') {
      return new Response('forbidden', { status: 403 })
    }
    if ((err as NodeJS.ErrnoException)?.code === 'EACCES') {
      return new Response('not found', { status: 404 })
    }
    return new Response('not found', { status: 404 })
  }

  try {
    /*
      缩略档:原图的全部校验(形状、根内落点、realpath、普通文件)到这里都已做完,
      缩略缓存只拿那个规范路径。生成不了(不是 png/jpeg、本来就小、超限、解码失败)
      就往下回原图 —— 和没写这个参数时一模一样。
    */
    if (preview === 'thumb' && request.method === 'GET' && request.headers.get('range') === null) {
      const mime = mimeOfExt(target)
      if ((mime === 'image/png' || mime === 'image/jpeg') && parseNcwUrl(request.url)?.scope === 'session') {
        const thumb = await thumbnails.get({ path: canonicalTarget, size: targetSize, mtimeMs: targetMtimeMs, mime }).catch(() => null)
        if (thumb !== null) {
          try {
            const bytes = await readFile(thumb.path)
            return new Response(new Uint8Array(bytes), {
              status: 200,
              headers: {
                'Content-Type': thumb.mime,
                'Content-Length': String(bytes.byteLength),
                'Cache-Control': 'public, max-age=31536000, immutable'
              }
            })
          } catch { /* 缓存文件刚被淘汰:回原图 */ }
        }
      }
    }

    /*
      ★★ **视频走自己的 Range 实现,不再转发给 `net.fetch(file://)`。**
      原先这段注释说"Range 由 net.fetch 处理",但**请求头根本没被转发** ——
      `net.fetch(pathToFileURL(…))` 是一个全新的请求,原请求的 `Range` 不在里面,
      于是每次都回 200 全量。表现正是注释里点出的那个症状:`<video>` 能播、
      **拖不动进度条**(而且每次 seek 都要等整个文件)。

      所以这里显式实现单区间 Range:它是这一段唯一需要解析的协议细节,而其正确性
      有单测(`__tests__/attachment-protocol.test.ts`)钉住。多区间不支持 ——
      回 200 全量,那是 RFC 允许的降级。
    */
    const range = request.headers.get('range')
    if (range !== null) {
      const ranged = await rangedFileResponse(canonicalTarget, targetSize, mimeOfExt(target), range, request.method === 'HEAD')
      if (ranged !== null) return ranged
    }

    /*
      Ranges 之外的路径保持原样:转发给 net.fetch —— 大文件走流、不整包进内存。
      但 **Content-Type 仍要自己给**:file:// 的类型推断在各平台不一致,
      推不出来时浏览器会把图片当下载处理。
      ★ 现在还要显式声明 `Accept-Ranges: bytes`:没有它,`<video>` 根本不会尝试
      发 Range 请求(它先看这个头)。
    */
    if (request.method === 'HEAD') {
      return new Response(null, {
        status: 200,
        headers: {
          'Content-Type': mimeOfExt(target),
          'Content-Length': String(targetSize),
          'Accept-Ranges': 'bytes',
          'Cache-Control': 'public, max-age=31536000, immutable'
        }
      })
    }
    const res = await net.fetch(pathToFileURL(canonicalTarget).toString())
    if (!res.ok) return new Response('not found', { status: 404 })

    const headers = new Headers(res.headers)
    headers.set('Content-Type', mimeOfExt(target))
    headers.set('Accept-Ranges', 'bytes')
    /*
      ★ immutable 长缓存在这里是**安全的**,正因为磁盘文件名是 ULID:
      同一个 URL 的内容永不改变。若文件名可复用(比如用原始文件名),
      这个头会把旧图钉死在缓存里,换了图也刷不掉。
    */
    headers.set('Cache-Control', 'public, max-age=31536000, immutable')
    return new Response(res.body, { status: res.status, headers })
  } catch {
    // 文件被外部删除 / 权限不足 —— 对渲染层都是「这张图没了」
    return new Response('not found', { status: 404 })
  }
}

/**
 * 单区间 Range 响应;无法处理(多区间、语法不合法、越界)时返回 `null`,
 * 由调用方退回 200 全量。
 *
 * ★ `export` 是为了能直接单测 —— 这段逻辑的错法(拖不动进度条)只有真播放器
 * 才暴露,而集成测试跑不起来 Electron。
 *
 * ★ 三处容易写错、而错了就"看起来能播但拖不动":
 *   - `Accept-Ranges: bytes` 必须出现,否则浏览器根本不发 Range;
 *   - `Content-Range` 的区间是**闭区间**,且 end 要按"文件大小 − 1"夹住 ——
 *     `bytes=0-`(开放区间)与 `bytes=100-`(到结尾)是最常见的两种请求;
 *   - 后缀区间 `bytes=-500` 表示"最后 500 字节",**不是**"从第 500 字节起"。
 */
export async function rangedFileResponse(
  path: string,
  size: number,
  mime: string,
  header: string,
  headOnly: boolean
): Promise<Response | null> {
  const spec = header.trim().toLowerCase()
  if (!spec.startsWith('bytes=')) return null
  const value = spec.slice('bytes='.length)
  // 多区间不实现 —— 回 200 全量是 RFC 允许的降级,而实现它要拼 multipart/byteranges。
  if (value.includes(',')) return null

  const match = /^(\d*)-(\d*)$/u.exec(value.trim())
  if (match === null) return null
  const [, rawStart = '', rawEnd = ''] = match
  if (rawStart === '' && rawEnd === '') return null

  let start: number
  let end: number
  if (rawStart === '') {
    // `bytes=-N`:最后 N 个字节
    const length = Number(rawEnd)
    if (!Number.isFinite(length) || length <= 0) return null
    start = Math.max(0, size - length)
    end = size - 1
  } else {
    start = Number(rawStart)
    // ★ 开放区间 `bytes=100-` → 一直到结尾
    end = rawEnd === '' ? size - 1 : Number(rawEnd)
  }
  if (!Number.isFinite(start) || !Number.isFinite(end)) return null
  if (start > end || start >= size) {
    // 越界 → 416 并带上真实长度(RFC 要求),客户端据此知道自己该要哪一段
    return new Response(null, {
      status: 416,
      headers: { 'Content-Range': `bytes */${String(size)}`, 'Accept-Ranges': 'bytes' }
    })
  }
  end = Math.min(end, size - 1)
  const length = end - start + 1

  const headers = {
    'Content-Type': mime,
    'Content-Length': String(length),
    'Content-Range': `bytes ${String(start)}-${String(end)}/${String(size)}`,
    'Accept-Ranges': 'bytes',
    'Cache-Control': 'public, max-age=31536000, immutable'
  }
  // ★ HEAD 只要头 —— 给 body 的话某些客户端会挂在那里等一段它不打算读的数据。
  if (headOnly) return new Response(null, { status: 206, headers })

  const stream = createReadStream(path, { start, end })
  return new Response(Readable.toWeb(stream) as ReadableStream, { status: 206, headers })
}

/** 给 UI 用的常量:CSP 里需要放开的 scheme 串 */
export const NCW_CSP_SOURCE = `${NCW_SCHEME}:`
export { NCW_HOST }
