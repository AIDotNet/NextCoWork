/**
 * 把邀请海报的 SVG 光栅化成 PNG。
 *
 * 需求：海报要发到微信/朋友圈，那些地方不认 SVG。所以另存这一步必须给 PNG。
 *
 * ★ **这一层刻意很薄，所有版式判断都在 `invite-poster.ts`（纯函数、有单测）里。**
 * 这里碰的是 `Image` / `canvas` / `toDataURL`，在 vitest 的 node 环境里测不了；
 * 把拼图逻辑放进来等于让它永远没有测试。
 *
 * ★ **logo 是画在画布上的，不是嵌在 SVG 里。** 打包后的渲染层跑在 `file://`
 * 之类的来源上，`fetch()` 一个打包产物去转 base64 会被 CSP 的 `connect-src 'self'`
 * 挡掉（症状：海报能出，但左上角永远缺一块，控制台只有一条 CSP 报错）。
 * 而 `<img>` 加载同一个 URL 是允许的 —— 所以走「先画 SVG，再把 logo 画上去」。
 *
 * ★ **SVG 用 `encodeURIComponent` 进 data URL，不用 `btoa`。** 海报里有中文，
 * `btoa` 遇到非 Latin-1 直接抛 `InvalidCharacterError`，而那条报错完全不提中文。
 */
import { buildInvitePosterSvg, POSTER_HEIGHT, POSTER_LOGO_RECT, POSTER_WIDTH, type InvitePosterInput } from './invite-poster'

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image()
    image.onload = () => resolve(image)
    image.onerror = () => reject(new Error(`image load failed: ${src.slice(0, 48)}`))
    image.src = src
  })
}

/**
 * 生成海报并返回 PNG 的 base64（不带 data URL 前缀）。
 *
 * `logoUrl` 取不到时**照样出图**，只是左上角没有那枚标 —— 一张缺图标的海报
 * 仍然能用，为它整个失败不值当。
 */
export async function renderInvitePosterPng(input: InvitePosterInput, logoUrl: string | null): Promise<string> {
  const svg = buildInvitePosterSvg(input)
  const canvas = document.createElement('canvas')
  canvas.width = POSTER_WIDTH
  canvas.height = POSTER_HEIGHT
  const context = canvas.getContext('2d')
  if (context === null) throw new Error('canvas 2d context unavailable')

  const base = await loadImage(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`)
  context.drawImage(base, 0, 0, POSTER_WIDTH, POSTER_HEIGHT)

  if (logoUrl !== null) {
    const logo = await loadImage(logoUrl).catch(() => null)
    if (logo !== null) {
      const { x, y, size } = POSTER_LOGO_RECT
      context.drawImage(logo, x, y, size, size)
    }
  }

  return canvas.toDataURL('image/png').replace(/^data:image\/png;base64,/, '')
}
