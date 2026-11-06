/**
 * 单区间 Range —— **视频拖动进度条**靠的就是这一段。
 *
 * ★★ 这条逻辑的错法有个很坏的性质:视频**照样能播**,只是拖不动、每次 seek 都
 * 从头下载。所以它必须在这里被钉住,而不是等一个真播放器去发现。
 *
 * ★ 三个 RFC 细节各自都能单独把人绊倒:
 *   - `Accept-Ranges: bytes` 不出现,浏览器根本不会发 Range 请求;
 *   - `Content-Range` 是**闭区间**,`bytes=0-`(开放)与 `bytes=-N`(后缀)是
 *     最常见的两种请求形态,而后者的语义与前者**相反**;
 *   - 越界要回 416 并带上真实长度,客户端据此才能重试正确的那一段。
 */
import { describe, expect, it } from 'vitest'
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { rangedFileResponse } from '../attachment-protocol'

/** 造一个 1000 字节的文件,内容按字节位置可验证。 */
function fixture(): { dir: string; file: string; cleanup: () => void } {
  const dir = mkdtempSync(join(tmpdir(), 'ncw-range-'))
  const file = join(dir, 'v.mp4')
  const bytes = new Uint8Array(1000)
  for (let i = 0; i < bytes.length; i += 1) bytes[i] = i % 256
  writeFileSync(file, bytes)
  return { dir, file, cleanup: () => { rmSync(dir, { recursive: true, force: true }) } }
}

const mime = 'video/mp4'

describe('单区间 Range', () => {
  it('bytes=0- → 206 + 全量 + 正确的 Content-Range(闭区间)', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=0-', false)
      expect(res?.status).toBe(206)
      expect(res?.headers.get('Content-Range')).toBe('bytes 0-999/1000')
      expect(res?.headers.get('Content-Length')).toBe('1000')
      expect(res?.headers.get('Accept-Ranges')).toBe('bytes')
      const body = new Uint8Array(await (res as Response).arrayBuffer())
      expect(body.length).toBe(1000)
      expect(body[0]).toBe(0)
      expect(body[999]).toBe(999 % 256)
    } finally { f.cleanup() }
  })

  it('bytes=100-199 → 只回那 100 个字节,且起点对得上', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=100-199', false)
      expect(res?.status).toBe(206)
      expect(res?.headers.get('Content-Range')).toBe('bytes 100-199/1000')
      expect(res?.headers.get('Content-Length')).toBe('100')
      const body = new Uint8Array(await (res as Response).arrayBuffer())
      expect(body.length).toBe(100)
      expect(body[0]).toBe(100)
      expect(body[99]).toBe(199)
    } finally { f.cleanup() }
  })

  it('bytes=500- → 一直到结尾', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=500-', false)
      expect(res?.headers.get('Content-Range')).toBe('bytes 500-999/1000')
      expect(res?.headers.get('Content-Length')).toBe('500')
    } finally { f.cleanup() }
  })

  it('★ bytes=-200 → **最后** 200 字节(后缀区间语义与 bytes=200- 相反)', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=-200', false)
      expect(res?.headers.get('Content-Range')).toBe('bytes 800-999/1000')
      expect(res?.headers.get('Content-Length')).toBe('200')
    } finally { f.cleanup() }
  })

  it('超出末尾的 end 被夹到 size-1(客户端常多要几个字节)', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=900-5000', false)
      expect(res?.headers.get('Content-Range')).toBe('bytes 900-999/1000')
      expect(res?.headers.get('Content-Length')).toBe('100')
    } finally { f.cleanup() }
  })

  it('起点越界 → 416 且带真实长度(客户端据此重试)', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=2000-2100', false)
      expect(res?.status).toBe(416)
      expect(res?.headers.get('Content-Range')).toBe('bytes */1000')
    } finally { f.cleanup() }
  })

  it('多区间不支持 → 返回 null,由调用方退回 200 全量(而不是回一个坏的部分响应)', async () => {
    const f = fixture()
    try {
      expect(await rangedFileResponse(f.file, 1000, mime, 'bytes=0-10,20-30', false)).toBeNull()
    } finally { f.cleanup() }
  })

  it('语法不合法 → null(不抛)', async () => {
    const f = fixture()
    try {
      expect(await rangedFileResponse(f.file, 1000, mime, 'items=0-10', false)).toBeNull()
      expect(await rangedFileResponse(f.file, 1000, mime, 'bytes=abc-def', false)).toBeNull()
      expect(await rangedFileResponse(f.file, 1000, mime, 'bytes=-', false)).toBeNull()
    } finally { f.cleanup() }
  })

  it('HEAD 只要头,不要 body(否则某些客户端会挂在那儿等)', async () => {
    const f = fixture()
    try {
      const res = await rangedFileResponse(f.file, 1000, mime, 'bytes=0-99', true)
      expect(res?.status).toBe(206)
      expect(res?.body).toBeNull()
      expect(res?.headers.get('Content-Length')).toBe('100')
    } finally { f.cleanup() }
  })
})
