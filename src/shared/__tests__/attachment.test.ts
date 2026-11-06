/**
 * 附件路径与 URL 的单测。
 *
 * ★ **这个文件是协议安全边界的验收点。** `parseNcwUrl` 是渲染层可以任意构造
 * 输入的入口,所以这里的重点不是「正常路径能解析」,而是**穷举逃逸变体**:
 * 编码的、双重编码的、混合分隔符的、绝对路径的、空段的。
 * 端到端测试跑不了这几十种变体,纯函数可以。
 */
import { describe, expect, it } from 'vitest'
import {
  attachmentRelPath,
  buildNcwUrl,
  extOfMime,
  imageMimeOfBytes,
  isImageMime,
  isSafeFileName,
  mimeOfExt,
  ncwPreviewUrl,
  normalizeImageMime,
  parseNcwUrl
} from '../domain/attachment'

describe('ncwPreviewUrl', () => {
  it('会话附件里的 png/jpeg 换成缩略档,解析回去仍是同一个文件', () => {
    const url = 'ncw://attachments/sessions/S1/01J.png'
    expect(ncwPreviewUrl(url)).toBe(`${url}?preview=thumb`)
    expect(parseNcwUrl(ncwPreviewUrl(url))).toEqual(parseNcwUrl(url))
    expect(ncwPreviewUrl('ncw://attachments/sessions/S1/01J.jpg')).toBe('ncw://attachments/sessions/S1/01J.jpg?preview=thumb')
  })

  it('其余一律原样:内联图、gif/webp、主题图、已带查询参数的', () => {
    for (const ref of [
      'data:image/png;base64,AAAA',
      'ncw://attachments/sessions/S1/01J.gif',
      'ncw://attachments/sessions/S1/01J.webp',
      'ncw://attachments/themes/t.png',
      'ncw://attachments/sessions/S1/01J.png?preview=thumb',
      '/abs/path.png'
    ]) expect(ncwPreviewUrl(ref)).toBe(ref)
  })
})

describe('attachmentRelPath', () => {
  it('session 需要 ownerId', () => {
    expect(attachmentRelPath({ scope: 'session', ownerId: 'S1', fileName: 'a.png' })).toBe(
      'sessions/S1/a.png'
    )
    expect(attachmentRelPath({ scope: 'session', fileName: 'a.png' })).toBeNull()
  })

  it('theme 没有主人,带了 ownerId 是调用方搞错了 —— 不静默忽略', () => {
    expect(attachmentRelPath({ scope: 'theme', fileName: 'a.png' })).toBe('themes/a.png')
    expect(attachmentRelPath({ scope: 'theme', ownerId: 'X', fileName: 'a.png' })).toBeNull()
  })

  it('export 与 session 同构', () => {
    expect(attachmentRelPath({ scope: 'export', ownerId: 'R1', fileName: 'o.zip' })).toBe(
      'exports/R1/o.zip'
    )
  })

  it.each([
    ['..', 'a.png'],
    ['.', 'a.png'],
    ['', 'a.png'],
    ['a/b', 'a.png'],
    ['a\\b', 'a.png'],
    ['a\0b', 'a.png']
  ])('ownerId=%j 被拒', (ownerId, fileName) => {
    expect(attachmentRelPath({ scope: 'session', ownerId, fileName })).toBeNull()
  })

  it.each(['..', '.', '', 'a/b.png', 'a\\b.png', 'a\0.png'])('fileName=%j 被拒', (fileName) => {
    expect(attachmentRelPath({ scope: 'session', ownerId: 'S1', fileName })).toBeNull()
  })
})

describe('parseNcwUrl —— 逃逸防线', () => {
  it('正常三段', () => {
    expect(parseNcwUrl('ncw://attachments/sessions/S1/01J8.png')).toEqual({
      scope: 'session',
      ownerId: 'S1',
      fileName: '01J8.png'
    })
  })

  it('正常两段(theme)', () => {
    expect(parseNcwUrl('ncw://attachments/themes/01J8.png')).toEqual({
      scope: 'theme',
      fileName: '01J8.png'
    })
  })

  it('★ 百分号编码的 ../ 被拒 —— 解码在校验之前,解码后重新逐段验', () => {
    expect(parseNcwUrl('ncw://attachments/sessions/%2e%2e/x.png')).toBeNull()
    expect(parseNcwUrl('ncw://attachments/%2e%2e%2f%2e%2e%2fetc/passwd')).toBeNull()
    expect(parseNcwUrl('ncw://attachments/sessions/S1/%2e%2e%2f%2e%2e%2fid_rsa')).toBeNull()
  })

  it('★ 大写编码的 %2E 同样被拒 —— 十六进制大小写不能成为绕过', () => {
    expect(parseNcwUrl('ncw://attachments/sessions/%2E%2E/x.png')).toBeNull()
  })

  it('裸 ../ 被拒', () => {
    expect(parseNcwUrl('ncw://attachments/sessions/../../x.png')).toBeNull()
  })

  it('反斜杠分隔符被拒 —— Windows 上它也是分隔符', () => {
    expect(parseNcwUrl('ncw://attachments/sessions/%5c..%5cx.png')).toBeNull()
  })

  it('段数越界被拒', () => {
    expect(parseNcwUrl('ncw://attachments/sessions')).toBeNull()
    expect(parseNcwUrl('ncw://attachments/sessions/S1/sub/a.png')).toBeNull()
  })

  it('未知 scope 目录被拒', () => {
    expect(parseNcwUrl('ncw://attachments/secrets/S1/a.png')).toBeNull()
  })

  it('scope 与段数不匹配被拒 —— themes 不该带 ownerId', () => {
    expect(parseNcwUrl('ncw://attachments/themes/X/a.png')).toBeNull()
    expect(parseNcwUrl('ncw://attachments/sessions/a.png')).toBeNull()
  })

  it('错误的 host 被拒', () => {
    expect(parseNcwUrl('ncw://evil/sessions/S1/a.png')).toBeNull()
  })

  it('★ host 大小写不敏感 —— Chromium 会把 standard scheme 的 host 强制小写', () => {
    expect(parseNcwUrl('ncw://ATTACHMENTS/themes/a.png')).not.toBeNull()
  })

  it('错误的协议被拒', () => {
    expect(parseNcwUrl('file:///etc/passwd')).toBeNull()
    expect(parseNcwUrl('http://attachments/themes/a.png')).toBeNull()
  })

  it('畸形输入不崩', () => {
    expect(parseNcwUrl('')).toBeNull()
    expect(parseNcwUrl('not a url')).toBeNull()
    expect(parseNcwUrl('ncw://attachments/themes/%zz')).toBeNull()
  })

  it('空段被折叠而不是产生额外层级', () => {
    // `//` 在 filter 之后消失,剩下的仍是合法两段
    expect(parseNcwUrl('ncw://attachments//themes//a.png')).toEqual({
      scope: 'theme',
      fileName: 'a.png'
    })
  })
})

describe('buildNcwUrl ↔ parseNcwUrl 往返', () => {
  it.each([
    { scope: 'session' as const, ownerId: '01J8ABC', fileName: '01J8X.png' },
    { scope: 'theme' as const, fileName: '01J8Z.webp' },
    { scope: 'export' as const, ownerId: 'R1', fileName: 'out.zip' }
  ])('%j 往返一致', (loc) => {
    const url = buildNcwUrl(loc)
    expect(url).not.toBeNull()
    expect(parseNcwUrl(url as string)).toEqual(loc)
  })

  it('含空格与中文的文件名往返一致 —— 逐段编码,不整串编码', () => {
    const loc = { scope: 'session' as const, ownerId: 'S1', fileName: '截 图.png' }
    const url = buildNcwUrl(loc) as string
    expect(url.includes('/')).toBe(true)
    expect(parseNcwUrl(url)).toEqual(loc)
  })

  it('非法 locator 建不出 URL', () => {
    expect(buildNcwUrl({ scope: 'session', fileName: 'a.png' })).toBeNull()
    expect(buildNcwUrl({ scope: 'session', ownerId: '..', fileName: 'a.png' })).toBeNull()
  })
})

describe('mime', () => {
  it('未知 mime 落到 .bin 而不是空扩展名', () => {
    expect(extOfMime('application/x-unknown')).toBe('.bin')
    expect(extOfMime('image/png')).toBe('.png')
    expect(extOfMime('IMAGE/PNG')).toBe('.png')
  })

  it('扩展名反推 mime,大小写不敏感', () => {
    expect(mimeOfExt('/a/b/c.PNG')).toBe('image/png')
    expect(mimeOfExt('x.jpeg')).toBe('image/jpeg')
    expect(mimeOfExt('noext')).toBe('application/octet-stream')
  })

  it('isImageMime', () => {
    expect(isImageMime('image/webp')).toBe(true)
    expect(isImageMime('application/pdf')).toBe(false)
  })

  /**
   * 别名归并:预览可见的图片在发送时不能被白名单拒掉。
   * `image/jpg` / `image/pjpeg` / `image/x-png` 都是真实世界中存在的写法,
   * 大小写与 `; charset=` 参数也会出现。
   */
  it.each([
    ['image/jpg', 'image/jpeg'],
    ['image/pjpeg', 'image/jpeg'],
    [' IMAGE/JPEG ', 'image/jpeg'],
    ['image/jpeg; charset=binary', 'image/jpeg'],
    ['image/x-png', 'image/png'],
    ['IMAGE/PNG', 'image/png'],
    ['image/gif', 'image/gif'],
    ['image/webp', 'image/webp']
  ])('%j 归一到 %s', (mime, expected) => {
    expect(normalizeImageMime(mime)).toBe(expected)
  })

  it.each(['image/svg+xml', 'image/bmp', 'image/avif', 'application/pdf', ''])(
    '%j 不在图片白名单里',
    (mime) => {
      expect(normalizeImageMime(mime)).toBeNull()
    }
  )

  it('imageMimeOfBytes 认容器签名,不靠 MIME 也不靠扩展名', () => {
    expect(imageMimeOfBytes(Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]))).toBe('image/png')
    expect(imageMimeOfBytes(Uint8Array.from([255, 216, 255, 224]))).toBe('image/jpeg')
    expect(imageMimeOfBytes(Buffer.from('GIF87a'))).toBe('image/gif')
    expect(imageMimeOfBytes(Buffer.from('GIF89a'))).toBe('image/gif')
    expect(imageMimeOfBytes(Buffer.from('RIFF\0\0\0\0WEBP'))).toBe('image/webp')
  })

  it('imageMimeOfBytes 对空字节、截断签名与伪装不猜', () => {
    expect(imageMimeOfBytes(new Uint8Array())).toBeNull()
    expect(imageMimeOfBytes(Uint8Array.from([137, 80, 78, 71]))).toBeNull()
    expect(imageMimeOfBytes(Buffer.from('RIFF\0\0\0\0WAVE'))).toBeNull()
    expect(imageMimeOfBytes(Buffer.from('<svg/>'))).toBeNull()
  })

  it('JFIF 与 JPEG 使用同一种图片 MIME', () => {
    expect(mimeOfExt('photo.JFIF')).toBe('image/jpeg')
    expect(isImageMime(mimeOfExt('photo.JFIF'))).toBe(true)
  })

  it.each([
    ['image/jpg', '.jpg'],
    ['image/x-png', '.png']
  ])('extOfMime(%j) 先过归一化 => %s', (mime, expected) => {
    expect(extOfMime(mime)).toBe(expected)
  })
})

describe('isSafeFileName', () => {
  it('挡住三类危险名', () => {
    expect(isSafeFileName('01J8.png')).toBe(true)
    expect(isSafeFileName('..')).toBe(false)
    expect(isSafeFileName('a/b')).toBe(false)
    expect(isSafeFileName('a\0')).toBe(false)
  })
})
