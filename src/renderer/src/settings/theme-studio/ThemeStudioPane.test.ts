// @vitest-environment jsdom
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_SETTINGS } from '../../../../shared/domain/settings'
import { builtinProfiles, resolveProfile } from '../../../../shared/domain/theme-profile'
import { IMAGE_THEMES, type Appearance, type ThemeProfile } from '../../../../shared/domain/theme'
import { I18nProvider, type Locale } from '../../i18n'
import { useImageThemes } from '../../stores/imageTheme'
import { useThemeProfiles } from '../../stores/themeProfiles'
import { ThemeStudioPane } from './ThemeStudioPane'

const state = vi.hoisted(() => ({ appearance: 'dark' as Appearance }))
vi.mock('../../theme/useAppearance', () => ({ useAppearance: () => state.appearance }))
// The desktop shell has its own stores. Exercise the real library and every inspector here.
vi.mock('./DesktopPreview', () => ({ DesktopPreview: () => null }))

let container: HTMLDivElement
let root: Root
const profiles = builtinProfiles()

beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
  useImageThemes.setState({ uploaded: [] })
  useThemeProfiles.setState({ profiles, draft: null, baseline: '', fullPreview: false })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  vi.unstubAllGlobals()
})

async function render(profile: ThemeProfile, locale: Locale, appearance: Appearance = 'dark'): Promise<void> {
  state.appearance = appearance
  await act(async () => {
    useThemeProfiles.getState().edit(profile)
    root.render(createElement(I18nProvider, { initialLocale: locale, children: createElement(ThemeStudioPane, {
      settings: { ...DEFAULT_SETTINGS, locale, activeThemeProfileId: profile.id }, patch: vi.fn()
    }) }))
  })
}

async function click(label: string): Promise<void> {
  const button = [...container.querySelectorAll('button')].find((b) => b.textContent === label)
  expect(button, label).toBeDefined()
  await act(async () => button!.click())
}

describe('theme studio labels and library previews', () => {
  it.each(['zh-CN', 'en-US'] as const)('renders translated appearance and all editor options in %s', async (locale) => {
    await render(profiles.find((p) => p.id === 'builtin-image-misty-forest')!, locale)
    // Arc 的分段控件是 role="group" + aria-pressed(不是 radiogroup)
    expect(container.querySelector('[role="group"]')?.textContent).toContain(locale === 'zh-CN' ? '跟随系统' : 'Follow system')
    const groups = locale === 'zh-CN' ? ['壁纸', '色彩', '面板', '字体与动效', '可读性'] : ['Wallpaper', 'Colors', 'Surfaces', 'Type & motion', 'Readability']
    for (const group of groups) {
      await click(group)
      // Includes dynamically composed option keys, collapsed advanced tokens and accessibility labels.
      const copy = [container.textContent, ...[...container.querySelectorAll('[aria-label]')].map((e) => e.getAttribute('aria-label'))].join('\n')
      expect(copy).not.toMatch(/(?:preference|themeStudio)\.[\w.-]+/)
      if (locale === 'en-US') expect(copy).not.toMatch(/[\u3400-\u9fff]/)
    }
  })

  it.each(['dark', 'light'] as const)('uses each built-in palette for both the card and swatch in %s mode', async (appearance) => {
    const previews = new Set<string>()
    for (const id of ['builtin-default', 'builtin-color-celadon', 'builtin-color-claude', 'builtin-color-opulent', 'builtin-color-minimal']) {
      const profile = profiles.find((p) => p.id === id)!
      await render(profile, 'en-US', appearance)
      const colors = resolveProfile(profile, appearance).tokens
      const expected = document.createElement('div')
      expected.style.background = `linear-gradient(135deg, ${colors.canvas}, ${colors.accent})`
      expected.style.color = colors.accent
      const preview = container.querySelector<HTMLElement>('article button > div')!
      const swatch = container.querySelector<HTMLElement>('article .size-2')!
      expect(preview.style.background).toBe(expected.style.background)
      expect(swatch.style.backgroundColor).toBe(expected.style.color)
      previews.add(preview.style.background)
      await click('Colors')
      if (id === 'builtin-color-claude') expect(container.querySelector<HTMLInputElement>('[aria-label="Palette seed"]')?.value).toBe(appearance === 'dark' ? '#df7e45' : '#914a27')
    }
    expect(previews.size).toBe(5)
  })

  it('keeps image theme artwork and translates all built-in names', async () => {
    await render(profiles[0]!, 'en-US')
    await click('Built-in')
    expect(container.querySelector('nav')?.textContent).toContain('Celadon')
    expect(container.querySelector('nav')?.textContent).not.toMatch(/[\u3400-\u9fff]/)
    for (const image of IMAGE_THEMES) {
      if (image.source.kind !== 'builtin') continue
      const expected = document.createElement('div')
      expected.style.backgroundImage = image.source.css
      expect([...container.querySelectorAll<HTMLElement>('article button > div')].some((p) => p.style.backgroundImage === expected.style.backgroundImage)).toBe(true)
    }
  })

  it('keeps the built-in name localized after editing unrelated controls', async () => {
    await render(profiles.find((p) => p.id === 'builtin-color-celadon')!, 'en-US')
    await click('Readability')
    const checkbox = container.querySelector<HTMLInputElement>('input[type="checkbox"]')!
    await act(async () => checkbox.click())
    expect(container.querySelector<HTMLInputElement>('[aria-label="Theme name"]')?.value).toBe('Celadon')
  })

  it('preserves user-defined theme and image names when the UI is English', async () => {
    const custom = { ...profiles.find((p) => p.id === 'builtin-color-claude')!, id: 'user-theme', name: '我的工作主题', builtin: false }
    const image = { id: 'user-image', name: '我的壁纸.png', seed: '#aabbcc', source: { kind: 'uploaded' as const, assetId: 'user-image' } }
    useThemeProfiles.setState({ profiles: [...profiles, custom] })
    useImageThemes.setState({ uploaded: [image] })
    await render(custom, 'en-US')
    expect(container.querySelector<HTMLInputElement>('[aria-label="Theme name"]')?.value).toBe(custom.name)
    expect(container.querySelector('article')?.textContent).toContain(custom.name)
    expect([...container.querySelectorAll('option')].some((o) => o.textContent === image.name)).toBe(true)
  })
})
