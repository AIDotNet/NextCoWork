/**
 * 缩略缓存:钉住「什么时候生成、什么时候回原图、缓存怎么限额」。
 * 解码器换成假的 —— 这里验的是缓存的规矩,不是 Electron 的缩放质量。
 */
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  THUMBNAIL_EDGE,
  THUMBNAIL_MAX_SOURCE_BYTES,
  ThumbnailCache,
  imageDimensions,
  type ThumbnailCodec,
  type ThumbnailSource
} from '../attachment-thumbnail'

let dir = ''

beforeEach(() => { dir = mkdtempSync(join(tmpdir(), 'ncw-thumb-')) })
afterEach(() => { rmSync(dir, { recursive: true, force: true }) })

/** 只有 IHDR 的 PNG 头 + 填充:尺寸由参数决定,总长 `size` 字节 */
function png(width: number, height: number, size = 4096): Buffer {
  const b = Buffer.alloc(size)
  b.writeUInt32BE(0x89504e47, 0); b.writeUInt32BE(0x0d0a1a0a, 4)
  b.writeUInt32BE(13, 8); b.writeUInt32BE(0x49484452, 12)
  b.writeUInt32BE(width, 16); b.writeUInt32BE(height, 20)
  return b
}

function source(name: string, bytes: Buffer): ThumbnailSource {
  const path = join(dir, name)
  writeFileSync(path, bytes)
  const st = statSync(path)
  return { path, size: st.size, mtimeMs: st.mtimeMs, mime: 'image/png' }
}

function codec(out = Buffer.from('thumb')): ThumbnailCodec & { render: ReturnType<typeof vi.fn> } {
  return { render: vi.fn(() => out) }
}

const cacheDir = (): string => join(dir, 'cache')

describe('ThumbnailCache', () => {
  it('★ 大图生成一次缩略并落盘,第二次直接命中缓存', async () => {
    const c = codec()
    const cache = new ThumbnailCache(cacheDir, c)
    const src = source('big.png', png(2560, 1440))

    const first = await cache.get(src)
    const second = await cache.get(src)

    expect(first).not.toBeNull()
    expect(second?.path).toBe(first?.path)
    expect(readFileSync(first!.path, 'utf8')).toBe('thumb')
    expect(c.render).toHaveBeenCalledTimes(1)
    expect(c.render.mock.calls[0]![2]).toBe(THUMBNAIL_EDGE)
    // 缓存放在给定目录里,文件名是哈希,不带原文件名
    expect(first!.path.startsWith(cacheDir())).toBe(true)
    expect(first!.path).not.toContain('big')
  })

  it('同一张图并发请求只解码一次', async () => {
    const c = codec()
    const cache = new ThumbnailCache(cacheDir, c)
    const src = source('big.png', png(2560, 1440))

    const [a, b] = await Promise.all([cache.get(src), cache.get(src)])

    expect(a?.path).toBe(b?.path)
    expect(c.render).toHaveBeenCalledTimes(1)
  })

  it('本来就不大于这一档的图回原图,不解码', async () => {
    const c = codec()
    const cache = new ThumbnailCache(cacheDir, c)

    expect(await cache.get(source('small.png', png(640, 480)))).toBeNull()
    expect(c.render).not.toHaveBeenCalled()
  })

  it('超过字节或像素上限的图回原图,不解码', async () => {
    const c = codec()
    const cache = new ThumbnailCache(cacheDir, c)

    expect(await cache.get({ ...source('a.png', png(2000, 2000)), size: THUMBNAIL_MAX_SOURCE_BYTES + 1 })).toBeNull()
    expect(await cache.get(source('huge.png', png(20000, 20000)))).toBeNull()
    expect(c.render).not.toHaveBeenCalled()
  })

  it('解码失败或缩出来不比原图小:回原图,也不留缓存文件', async () => {
    const throwing: ThumbnailCodec = { render: () => { throw new Error('bad image') } }
    expect(await new ThumbnailCache(cacheDir, throwing).get(source('x.png', png(2000, 2000)))).toBeNull()

    const bloated = codec(Buffer.alloc(8192))
    expect(await new ThumbnailCache(cacheDir, bloated).get(source('y.png', png(2000, 2000)))).toBeNull()

    expect(() => readdirSync(cacheDir())).toThrow()
  })

  it('原文件变了(大小/mtime)就是另一把键,不复用旧缩略', async () => {
    const c = codec()
    const cache = new ThumbnailCache(cacheDir, c)
    const src = source('big.png', png(2560, 1440))
    const first = await cache.get(src)

    writeFileSync(src.path, png(2560, 1440, 5000))
    const st = statSync(src.path)
    const second = await cache.get({ ...src, size: st.size, mtimeMs: st.mtimeMs + 1000 })

    expect(second?.path).not.toBe(first?.path)
    expect(c.render).toHaveBeenCalledTimes(2)
  })

  it('读到的字节数和校验时不一致:不生成', async () => {
    const c = codec()
    const src = source('big.png', png(2560, 1440))

    expect(await new ThumbnailCache(cacheDir, c).get({ ...src, size: src.size - 1 })).toBeNull()
    expect(c.render).not.toHaveBeenCalled()
  })

  it('★ 超过磁盘预算时按最近使用时间淘汰最旧的', async () => {
    const out = Buffer.alloc(1000, 1)
    // 预算 2500:放得下两张,第三张进来时要淘汰
    const cache = new ThumbnailCache(cacheDir, codec(out), 2500)
    const a = await cache.get(source('a.png', png(2000, 2000)))
    const b = await cache.get(source('b.png', png(2000, 2000)))
    // a 比 b 旧
    utimesSync(a!.path, new Date(1_000_000), new Date(1_000_000))
    utimesSync(b!.path, new Date(2_000_000), new Date(2_000_000))

    const c = await cache.get(source('c.png', png(2000, 2000)))

    const left = readdirSync(cacheDir()).map((name) => join(cacheDir(), name)).sort()
    expect(left).toEqual([b!.path, c!.path].sort())
  })
})

describe('imageDimensions', () => {
  it('PNG 从 IHDR 读', () => {
    expect(imageDimensions(png(1280, 720))).toEqual({ width: 1280, height: 720 })
  })

  it('JPEG 跳过 APP 段读 SOF', () => {
    const app1 = [0xff, 0xe1, 0x00, 0x06, 1, 2, 3, 4]
    const sof0 = [0xff, 0xc0, 0x00, 0x11, 0x08, 0x02, 0xd0, 0x05, 0x00]
    expect(imageDimensions(new Uint8Array([0xff, 0xd8, ...app1, ...sof0, 0, 0, 0]))).toEqual({ width: 1280, height: 720 })
  })

  it('认不出来返回 null', () => {
    expect(imageDimensions(new Uint8Array([1, 2, 3, 4]))).toBeNull()
    expect(imageDimensions(new Uint8Array([0xff, 0xd8, 0xff, 0xda, 0, 2]))).toBeNull()
  })
})
