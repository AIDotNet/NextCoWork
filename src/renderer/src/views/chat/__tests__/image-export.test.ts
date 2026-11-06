/**
 * 产物图的**取字节与取名**。这一组守的坏全都不报错:
 * - 文件名扩展名不跟 mime 走:字节写对了,但部分看图工具按扩展名挑解码器,表现是
 *   「存下来的图打不开」,而没人会想到问题在文件名上;
 * - `ncw://` 那条路被当成内联 base64:复制到剪贴板里的是一串 `ncw://…` 文本;
 * - 一次 `String.fromCharCode(...bytes)` 编码整张图:几 MB 的图当场爆栈,
 *   只在真去点那颗按钮时现形(小图测试照过)。
 *
 * ★ 环境是 node(`vitest.config.ts`),所以 `fetch` 自己给替身。这正是把这段逻辑
 * 从组件里抽出来的收益:不用起 Electron、也不用 jsdom 就能把这两条路径都走一遍。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { imageBase64, imageFileName } from '../image-export'

/** 20 字节的假图:前 8 位是 PNG 的魔数,主进程那道格式校验认的就是它。 */
const BYTES = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])
const BYTES_BASE64 = 'iVBORw0KGgoBAgMEBQYHCAkKCww='

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('imageFileName · 同一轮里每张图名字不同', () => {
  it('只给名字,扩展名留给主进程按字节补', () => {
    expect(imageFileName(1)).toBe('image-1')
    expect(imageFileName(2)).toBe('image-2')
    // 同一次调用里连着的四张不会互相覆盖,也不必凑一个假扩展名
    expect(new Set([1, 2, 3, 4].map((n) => imageFileName(n))).size).toBe(4)
  })
})

describe('imageBase64 · 两条取字节的路', () => {
  it('内联 data URL 直接取正文,不绕 fetch', async () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)
    expect(await imageBase64(`data:image/png;base64,${BYTES_BASE64}`)).toBe(BYTES_BASE64)
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('ncw:// 会话附件走 fetch,字节原样编码回 base64', async () => {
    const url = 'ncw://attachments/sessions/s1/01HZZZ.png'
    const fetchSpy = vi.fn(async (req: string) => {
      expect(req).toBe(url)
      return new Response(BYTES, { status: 200 })
    })
    vi.stubGlobal('fetch', fetchSpy)
    expect(await imageBase64(url)).toBe(BYTES_BASE64)
    expect(fetchSpy).toHaveBeenCalledTimes(1)
  })

  it('★ 几 MB 的图不爆栈:编码要分块,不能一次摊开整个数组', async () => {
    const big = new Uint8Array(300_000)
    big.fill(65)
    vi.stubGlobal('fetch', async () => new Response(big, { status: 200 }))
    // 摊开写法在这里是 `RangeError: Maximum call stack size exceeded`
    expect(await imageBase64('ncw://attachments/sessions/s1/big.png')).toBe(btoa('A'.repeat(300_000)))
  })

  it('附件没了(404)时抛出去 —— 调用点据此闪失败态,而不是把空图写进剪贴板', async () => {
    vi.stubGlobal('fetch', async () => new Response('not found', { status: 404 }))
    await expect(imageBase64('ncw://attachments/sessions/s1/gone.png')).rejects.toThrow()
  })

  it('不带 base64 的 data URL 走 fetch,不把正文当 base64 取', async () => {
    // 直接取正文的话,写下去的就是 `%3Csvg…` 这么一串坏字节
    const fetchSpy = vi.fn(async () => new Response(BYTES, { status: 200 }))
    vi.stubGlobal('fetch', fetchSpy)
    expect(await imageBase64('data:image/svg+xml,%3Csvg%20xmlns')).toBe(BYTES_BASE64)
    expect(fetchSpy).toHaveBeenCalledTimes(1)
  })
})
