/**
 * 灯箱顶部两个角的**跨平台落点**。
 *
 * 守的坏不会报错,而且**在开发机上看不见**:Windows 上那三颗自绘的窗口按钮是
 * `z-200` 的常驻悬浮层,永远盖在灯箱(z-50)之上。灯箱的 ✕ 原先写死 `right-4`,
 * 正好落在窗口关闭键底下 —— 用户点「关掉这张图」,关掉的是整个应用,而两颗都是 ✕,
 * 他多半还以为是图自己关错了。macOS 那边是同一个错的方向相反(红绿灯压着
 * 「打开方式」那一簇)。
 *
 * 所以这里钉住的是**类名**(jsdom 不排版,量不出矩形):✕ 必须挂在
 * `right-window-controls` 上 —— 那个 token 就是顶栏给三颗窗口按钮让位的宽度,
 * 写死一个像素值会让两边再次撞上而不自知。见 `ImageLightbox.tsx` 文件头。
 *
 * ★ 这份用例跑在 node 环境(`vitest.config.ts`),`IS_MAC` 在 import 那一刻求值,
 *   这里是 **false**,也就是 Windows/Linux 那一档 —— 正是出问题的那一档。
 * ★ 根节点那条 `app-no-drag` 同理要钉:灯箱压在那条 34px 的 drag 区上,少了它
 *   顶部整条点不动,一按住整个窗口跟着鼠标跑(同 `SettingsOverlay`)。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../../../i18n'
import { ImageLightbox } from '../ImageLightbox'

const IMAGES = [
  { mime: 'image/png', dataRef: 'data:image/png;base64,AA' },
  { mime: 'image/png', dataRef: 'data:image/png;base64,BB' }
]

function stubDom(): { container: HTMLElement; close: () => void } {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  return { container: dom.window.document.getElementById('root')!, close: () => dom.window.close() }
}

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.unstubAllGlobals()
})

describe('灯箱 · 顶部两个角', () => {
  async function mount(): Promise<HTMLDivElement> {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    await act(async () => root.render(createElement(I18nProvider, {
      initialLocale: 'zh-CN',
      children: createElement(ImageLightbox, { images: IMAGES, startIndex: 0, onClose: () => {} })
    })))
    // 灯箱 portal 到 body,不在那个挂载容器里
    return document.body.querySelector('[data-testid="image-lightbox"]') as HTMLDivElement
  }

  it('★ 关闭键让开右上角那三颗窗口按钮,不写死 right-4', async () => {
    const lightbox = await mount()
    const close = lightbox.querySelector('[data-testid="lightbox-close"]')
    expect(close).not.toBeNull()
    expect(close?.className).toContain('right-window-controls')
    expect(close?.className).not.toContain('right-4')
  })

  it('★ 根节点声明 app-no-drag —— 否则顶部那一条点不动,还会拖着窗口跑', async () => {
    const lightbox = await mount()
    expect(lightbox.className).toContain('app-no-drag')
  })
})
