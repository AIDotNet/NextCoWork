/**
 * 会话附件图片的缩略档(`ncw://…?preview=thumb`)—— 派生、可丢、单独限额的磁盘缓存。
 *
 * 需求:转录里连着几十张截图时,每个 `<img>` 都按原尺寸解码常驻渲染进程,而卡片只画几百像素高。
 * 缩略档把这份解码开销降到「长边 768px」一档;原图仍原样留在附件根,灯箱、另存、
 * 发给模型的永远是原图。
 *
 * 不变式:
 * 1. **只接受协议层验过的原图**:调用方(`attachment-protocol.ts`)先做完 URL 形状、根内落点、
 *    realpath 与「是普通文件」的校验,这里只拿那个规范路径。缓存文件名是哈希,没有任何一段
 *    来自请求。
 * 2. **缓存键 = 原文件身份 + 档位 + 格式版本**(规范路径、字节数、mtime、`THUMBNAIL_EDGE`、
 *    `THUMBNAIL_FORMAT_VERSION`)。附件文件名是 ULID、内容不变,同一个键永远对应同一张图。
 * 3. **不和原始附件混放**:缓存目录在附件根之外(`<数据根>/thumbnail-cache`),不进附件表、
 *    不进备份;总量超过预算时按最近使用时间淘汰,删了下次再生成。
 * 4. **解码有界**:同时只生成一张;源文件超过字节/像素上限、本来就不大于这一档、
 *    解码失败时一律返回 null —— 调用方回原图,不报错。
 */
import { createHash } from 'node:crypto'
import { mkdir, open, readdir, rename, rm, stat, utimes, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

export const THUMBNAIL_CACHE_DIR = 'thumbnail-cache'
/** 唯一的一档:卡片最多画 360px 高,2 倍屏下 768px 足够清楚 */
export const THUMBNAIL_EDGE = 768
/** 缩放/编码方式变了就加一,旧文件自然失配、按预算淘汰 */
export const THUMBNAIL_FORMAT_VERSION = 1
export const THUMBNAIL_MAX_SOURCE_BYTES = 32 * 1024 * 1024
export const THUMBNAIL_MAX_SOURCE_PIXELS = 50_000_000
export const THUMBNAIL_CACHE_BUDGET = 256 * 1024 * 1024
/** 淘汰到预算的这一比例,免得每多一张就扫一次目录 */
const EVICT_TARGET_RATIO = 0.8
/** 「不必缩」的结论在内存里记多少条 —— 超了整表清空,代价只是多读一次文件头 */
const PASSTHROUGH_MEMO_MAX = 4096
/** 读文件头判尺寸时最多读多少字节(JPEG 的 SOF 可能排在 EXIF 之后) */
const HEADER_PROBE_BYTES = 256 * 1024

export type ThumbnailMime = 'image/png' | 'image/jpeg'

export interface ThumbnailSource {
  /** 协议层校验过的规范路径(realpath 之后) */
  path: string
  size: number
  mtimeMs: number
  mime: ThumbnailMime
}

export interface ThumbnailCodec {
  /** 原图字节 → 长边不超过 `edge` 的同格式编码。解不出来返回 null */
  render(bytes: Uint8Array, mime: ThumbnailMime, edge: number): Uint8Array | null
}

export interface Thumbnail { path: string; mime: ThumbnailMime }

export class ThumbnailCache {
  private queue: Promise<unknown> = Promise.resolve()
  private readonly inflight = new Map<string, Promise<Thumbnail | null>>()
  private readonly passthrough = new Set<string>()
  /** 目录里的总字节数;null = 还没扫过 */
  private total: number | null = null

  constructor(
    private readonly directory: () => string,
    private readonly codec: ThumbnailCodec,
    private readonly budget = THUMBNAIL_CACHE_BUDGET
  ) {}

  /** 缩略档的路径;该回原图时返回 null */
  get(source: ThumbnailSource): Promise<Thumbnail | null> {
    if (source.size > THUMBNAIL_MAX_SOURCE_BYTES) return Promise.resolve(null)
    const key = cacheKey(source)
    if (this.passthrough.has(key)) return Promise.resolve(null)
    const pending = this.inflight.get(key)
    if (pending !== undefined) return pending
    const run = this.lookupOrCreate(key, source).finally(() => { this.inflight.delete(key) })
    this.inflight.set(key, run)
    return run
  }

  private async lookupOrCreate(key: string, source: ThumbnailSource): Promise<Thumbnail | null> {
    const target = join(this.directory(), `${key}${source.mime === 'image/png' ? '.png' : '.jpg'}`)
    try {
      const hit = await stat(target)
      if (hit.isFile() && hit.size > 0) {
        const now = new Date()
        // 最近使用时间 = mtime,淘汰按它排。失败无所谓
        await utimes(target, now, now).catch(() => undefined)
        return { path: target, mime: source.mime }
      }
    } catch { /* 没有缓存,往下生成 */ }
    // 同时只生成一张:解码是同步的,并发只会让主进程卡得更久
    const next = this.queue.then(() => this.create(key, target, source))
    this.queue = next.catch(() => undefined)
    return next
  }

  private async create(key: string, target: string, source: ThumbnailSource): Promise<Thumbnail | null> {
    const dims = imageDimensions(await readHead(source.path, HEADER_PROBE_BYTES))
    if (dims !== null && (dims.width * dims.height > THUMBNAIL_MAX_SOURCE_PIXELS || Math.max(dims.width, dims.height) <= THUMBNAIL_EDGE)) {
      this.rememberPassthrough(key)
      return null
    }
    const bytes = await readAll(source.path, source.size)
    // 读的这段时间里文件被换掉了:不缓存一份对不上身份的缩略
    if (bytes === null) return null
    // 让出一拍再做同步解码,排在它前面的 I/O 回调先走
    await new Promise<void>((resolve) => setImmediate(resolve))
    let encoded: Uint8Array | null
    try {
      encoded = this.codec.render(bytes, source.mime, THUMBNAIL_EDGE)
    } catch {
      encoded = null
    }
    if (encoded === null || encoded.byteLength === 0 || encoded.byteLength >= source.size) {
      this.rememberPassthrough(key)
      return null
    }
    const dir = this.directory()
    const tmp = join(dir, `.${key}.${String(process.pid)}.tmp`)
    try {
      await mkdir(dir, { recursive: true })
      await writeFile(tmp, encoded)
      await rename(tmp, target)
    } catch {
      await rm(tmp, { force: true }).catch(() => undefined)
      return null
    }
    await this.account(encoded.byteLength)
    return { path: target, mime: source.mime }
  }

  private rememberPassthrough(key: string): void {
    if (this.passthrough.size >= PASSTHROUGH_MEMO_MAX) this.passthrough.clear()
    this.passthrough.add(key)
  }

  /** 记账;超预算时按 mtime 从旧到新删,直到降回预算的 80% */
  private async account(added: number): Promise<void> {
    if (this.total !== null) {
      this.total += added
      if (this.total <= this.budget) return
    }
    const entries = await this.scan()
    this.total = entries.reduce((sum, entry) => sum + entry.size, 0)
    if (this.total <= this.budget) return
    entries.sort((a, b) => a.mtimeMs - b.mtimeMs)
    const goal = this.budget * EVICT_TARGET_RATIO
    for (const entry of entries) {
      if (this.total <= goal) break
      try {
        await rm(entry.path, { force: true })
        this.total -= entry.size
      } catch { /* 删不掉的留着,下次再试 */ }
    }
  }

  private async scan(): Promise<Array<{ path: string; size: number; mtimeMs: number }>> {
    const dir = this.directory()
    let names: string[]
    try {
      names = await readdir(dir)
    } catch {
      return []
    }
    const out: Array<{ path: string; size: number; mtimeMs: number }> = []
    for (const name of names) {
      const path = join(dir, name)
      try {
        const st = await stat(path)
        if (st.isFile()) out.push({ path, size: st.size, mtimeMs: st.mtimeMs })
      } catch { /* 并发删掉了 */ }
    }
    return out
  }
}

function cacheKey(source: ThumbnailSource): string {
  return createHash('sha256')
    .update(`${String(THUMBNAIL_FORMAT_VERSION)}\0${String(THUMBNAIL_EDGE)}\0${source.path}\0${String(source.size)}\0${String(Math.trunc(source.mtimeMs))}`)
    .digest('hex')
    .slice(0, 40)
}

async function readHead(path: string, limit: number): Promise<Uint8Array> {
  const handle = await open(path, 'r')
  try {
    const buffer = Buffer.alloc(limit)
    const { bytesRead } = await handle.read(buffer, 0, limit, 0)
    return buffer.subarray(0, bytesRead)
  } finally {
    await handle.close()
  }
}

/** 整个文件;实际字节数和协议层看到的不一致时返回 null */
async function readAll(path: string, expected: number): Promise<Uint8Array | null> {
  const handle = await open(path, 'r')
  try {
    const st = await handle.stat()
    if (st.size !== expected) return null
    const buffer = Buffer.alloc(expected)
    const { bytesRead } = await handle.read(buffer, 0, expected, 0)
    return bytesRead === expected ? buffer : null
  } finally {
    await handle.close()
  }
}

/**
 * PNG / JPEG 文件头里的像素尺寸;认不出来返回 null(交给解码器去判)。
 * 只为两件事:本来就小的图不必缩,超大的图不去解码。
 */
export function imageDimensions(bytes: Uint8Array): { width: number; height: number } | null {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  // PNG:8 字节签名之后第一个块必须是 IHDR
  if (bytes.length >= 24 && view.getUint32(0) === 0x89504e47 && view.getUint32(4) === 0x0d0a1a0a
    && view.getUint32(12) === 0x49484452) {
    return { width: view.getUint32(16), height: view.getUint32(20) }
  }
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8) return null
  let i = 2
  while (i + 4 <= bytes.length) {
    if (bytes[i] !== 0xff) return null
    const marker = bytes[i + 1]!
    // 填充字节
    if (marker === 0xff) { i += 1; continue }
    // 没有长度字段的独立标记
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) { i += 2; continue }
    if (marker === 0xd9 || marker === 0xda) return null
    const length = view.getUint16(i + 2)
    if (length < 2) return null
    const isSof = marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc
    if (isSof) {
      if (i + 9 > bytes.length) return null
      return { height: view.getUint16(i + 5), width: view.getUint16(i + 7) }
    }
    i += 2 + length
  }
  return null
}
