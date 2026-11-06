/**
 * 画布换算的纯函数:twips ↔ CSS px、tile 网格、DOM 按键 → LibreOffice 键码。
 *
 * 钉住的需求:点击落点与光标对得上(缩放 / 高 DPI 下也一样);相邻 tile 不留缝不重叠;
 * 快捷键按键位认、文字按字符认,组字中的按键不重复送。
 */
import { describe, expect, it } from 'vitest'
import { LOK_KEY, LOK_MODIFIER, lokKeyOf, type DomKeyLike } from '../keys'
import { MAX_SHEET_REACH_TWIPS, cssPxToTwips, grownSheetReach, tileRequest, tilesCovering, tilesInView, twipsRectToCss, twipsToCssPx, type TileGrid } from '../viewport'

describe('twips and CSS px', () => {
  it('uses 15 twips per CSS px at 100% and scales with zoom', () => {
    expect(cssPxToTwips(96, 1)).toBe(1440)
    expect(cssPxToTwips(96, 2)).toBe(720)
    expect(twipsToCssPx(1440, 1.5)).toBe(144)
    // 负坐标(点在文档原点左上方的空白里)夹到 0:引擎拒绝负坐标
    expect(cssPxToTwips(-3, 1)).toBe(0)
  })

  it('round-trips a click to within one twip at awkward zooms', () => {
    for (const zoom of [0.33, 0.75, 1.1, 1.25, 3.7]) {
      for (const px of [0, 1, 17.5, 333, 1024.25]) {
        expect(Math.abs(twipsToCssPx(cssPxToTwips(px, zoom), zoom) - px)).toBeLessThanOrEqual(twipsToCssPx(0.5, zoom) + 1e-9)
      }
    }
  })

  it('keeps sub-pixel caret positions for overlays', () => {
    expect(twipsRectToCss({ x: 1591, y: 1418, width: 0, height: 276 }, 1)).toEqual({ left: 1591 / 15, top: 1418 / 15, width: 0, height: 276 / 15 })
  })

  it('refuses zoom values that would turn into infinite render requests', () => {
    expect(() => cssPxToTwips(10, 0)).toThrow(RangeError)
    expect(() => twipsToCssPx(10, Number.NaN)).toThrow(RangeError)
  })
})

describe('tile grid', () => {
  const grid: TileGrid = { zoom: 1, dpr: 2, tilePx: 256 }

  it('covers a tile of device pixels with the matching document area, sharper on high-DPI screens', () => {
    // 256 设备像素 / DPR 2 = 128 CSS px = 1920 twips
    expect(tileRequest({ col: 1, row: 2 }, grid)).toEqual({ x: 1920, y: 3840, tileWidth: 1920, tileHeight: 1920, width: 256, height: 256 })
    expect(tileRequest({ col: 0, row: 0 }, { ...grid, dpr: 1 }).tileWidth).toBe(3840)
  })

  it('shares every edge between neighbouring tiles at fractional scales, so there is no gap or overlap', () => {
    for (const g of [{ zoom: 1.1, dpr: 1.25, tilePx: 256 }, { zoom: 0.37, dpr: 3, tilePx: 200 }, { zoom: 2.9, dpr: 1.5, tilePx: 512 }]) {
      for (let i = 0; i < 40; i++) {
        const a = tileRequest({ col: i, row: i }, g)
        const b = tileRequest({ col: i + 1, row: i + 1 }, g)
        expect(a.x + a.tileWidth).toBe(b.x)
        expect(a.y + a.tileHeight).toBe(b.y)
      }
    }
  })

  it('finds exactly the tiles an invalidation touches, clipped to the document', () => {
    const doc = { width: 12240, height: 15840 }
    // 1920 twips 一块:x 1900..1940 跨第 0、1 列,y 只在第 0 行
    expect(tilesCovering({ x: 1900, y: 10, width: 40, height: 20 }, grid, doc)).toEqual([{ col: 0, row: 0 }, { col: 1, row: 0 }])
    // 右边界是开区间:正好止于 1920 的矩形不碰第 1 列
    expect(tilesCovering({ x: 0, y: 0, width: 1920, height: 1 }, grid, doc)).toEqual([{ col: 0, row: 0 }])
    // 文档之外 / 空矩形 = 不画
    expect(tilesCovering({ x: 20000, y: 0, width: 100, height: 100 }, grid, doc)).toEqual([])
    expect(tilesCovering({ x: 0, y: 0, width: 0, height: 100 }, grid, doc)).toEqual([])
  })

  it('lists every tile a scrolled viewport shows, including the partly visible edge column', () => {
    const doc = { width: 100_000, height: 100_000 }
    // 128 CSS px 一块;视口 left 100..400 → 第 0..3 列;top 0..128 → 第 0 行
    const tiles = tilesInView({ left: 100, top: 0, width: 300, height: 128 }, grid, doc)
    expect(tiles.map((t) => t.col)).toEqual([0, 1, 2, 3])
    expect(new Set(tiles.map((t) => t.row))).toEqual(new Set([0]))
  })

  it('refuses a grid that would ask the engine for an unbounded render', () => {
    expect(() => tileRequest({ col: 0, row: 0 }, { zoom: 1, dpr: 0, tilePx: 256 })).toThrow(RangeError)
    expect(() => tileRequest({ col: 0, row: 0 }, { zoom: 1, dpr: 1, tilePx: 4096 })).toThrow(RangeError)
    expect(() => tileRequest({ col: -1, row: 0 }, grid)).toThrow(RangeError)
  })
})

describe('lokKeyOf', () => {
  const key = (init: Partial<DomKeyLike> & Pick<DomKeyLike, 'key' | 'code'>): DomKeyLike => ({ shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, ...init })

  it('types printable characters as characters, including shifted and non-Latin ones', () => {
    expect(lokKeyOf(key({ key: 'a', code: 'KeyA' }), 'other')).toEqual({ charCode: 97, keyCode: 0 })
    expect(lokKeyOf(key({ key: 'A', code: 'KeyA', shiftKey: true }), 'other')).toEqual({ charCode: 65, keyCode: 0 })
    expect(lokKeyOf(key({ key: ' ', code: 'Space' }), 'mac')).toEqual({ charCode: 32, keyCode: 0 })
    expect(lokKeyOf(key({ key: 'ж', code: 'Semicolon' }), 'other')).toEqual({ charCode: 0x436, keyCode: 0 })
  })

  it('sends named keys as key codes with their modifiers', () => {
    expect(lokKeyOf(key({ key: 'Enter', code: 'Enter' }), 'other')).toEqual({ charCode: 0, keyCode: LOK_KEY.RETURN })
    expect(lokKeyOf(key({ key: 'Enter', code: 'Enter', shiftKey: true }), 'other')).toEqual({ charCode: 0, keyCode: LOK_KEY.RETURN | LOK_MODIFIER.SHIFT })
    expect(lokKeyOf(key({ key: 'ArrowLeft', code: 'ArrowLeft', altKey: true }), 'mac')).toEqual({ charCode: 0, keyCode: LOK_KEY.LEFT | LOK_MODIFIER.MOD2 })
    expect(lokKeyOf(key({ key: 'F5', code: 'F5' }), 'other')).toEqual({ charCode: 0, keyCode: LOK_KEY.F1 + 4 })
  })

  it('maps the primary shortcut modifier per platform and reads shortcut letters from the layout', () => {
    // 与一致性测试里实测的撤销同值:MOD1 | Z
    expect(lokKeyOf(key({ key: 'z', code: 'KeyZ', ctrlKey: true }), 'other')).toEqual({ charCode: 0, keyCode: 537 | 0x2000 })
    expect(lokKeyOf(key({ key: 'z', code: 'KeyZ', metaKey: true }), 'mac')).toEqual({ charCode: 0, keyCode: 537 | 0x2000 })
    // macOS 的 Ctrl 是 MOD3,不是主修饰
    expect(lokKeyOf(key({ key: 'a', code: 'KeyA', ctrlKey: true }), 'mac')).toEqual({ charCode: 0, keyCode: LOK_KEY.A | LOK_MODIFIER.MOD3 })
    // 法语 AZERTY:印着 Z 的键在 QWERTY 的 W 位置。按布局认成 Ctrl+Z(撤销),不是 Ctrl+W
    expect(lokKeyOf(key({ key: 'z', code: 'KeyW', ctrlKey: true }), 'other')).toEqual({ charCode: 0, keyCode: 537 | LOK_MODIFIER.MOD1 })
    // 俄语布局:字符是 я,没有拉丁字母可认,退回键位 → 仍是 Ctrl+Z
    expect(lokKeyOf(key({ key: 'я', code: 'KeyZ', ctrlKey: true }), 'other')).toEqual({ charCode: 0, keyCode: 537 | LOK_MODIFIER.MOD1 })
    expect(lokKeyOf(key({ key: '1', code: 'Digit1', ctrlKey: true, shiftKey: true }), 'other')).toEqual({ charCode: 0, keyCode: (LOK_KEY.NUM0 + 1) | LOK_MODIFIER.SHIFT | LOK_MODIFIER.MOD1 })
  })

  it('treats AltGr and macOS Option characters as typing, not shortcuts', () => {
    expect(lokKeyOf(key({ key: '@', code: 'KeyQ', ctrlKey: true, altKey: true }), 'other')).toEqual({ charCode: 64, keyCode: 0 })
    expect(lokKeyOf(key({ key: 'å', code: 'KeyA', altKey: true }), 'mac')).toEqual({ charCode: 0xe5, keyCode: 0 })
    // 其它平台单独的 Alt+字母是菜单加速键
    expect(lokKeyOf(key({ key: 'f', code: 'KeyF', altKey: true }), 'other')).toEqual({ charCode: 0, keyCode: (LOK_KEY.A + 5) | LOK_MODIFIER.MOD2 })
  })

  it('drops keys the engine must not see: composition, lone modifiers, OS keys and unknown shortcuts', () => {
    // ★ 组字中的按键若也送一次,候选字会被输入两遍
    expect(lokKeyOf(key({ key: 'Process', code: 'KeyN' }), 'other')).toBeNull()
    expect(lokKeyOf(key({ key: 'n', code: 'KeyN', isComposing: true }), 'mac')).toBeNull()
    expect(lokKeyOf(key({ key: 'Shift', code: 'ShiftLeft', shiftKey: true }), 'other')).toBeNull()
    expect(lokKeyOf(key({ key: 'Meta', code: 'MetaLeft', metaKey: true }), 'mac')).toBeNull()
    expect(lokKeyOf(key({ key: 'd', code: 'KeyD', metaKey: true }), 'other')).toBeNull()
    expect(lokKeyOf(key({ key: '\\', code: 'Backslash', ctrlKey: true }), 'other')).toBeNull()
  })
})

describe('grownSheetReach', () => {
  it('leaves one more screen past the farthest visible area, grows as you scroll and never shrinks', () => {
    const start = { width: 0, height: 0 }
    const first = grownSheetReach(start, { x: 0, y: 0, width: 15000, height: 9000 })
    expect(first).toEqual({ width: 30000, height: 18000 })
    const scrolled = grownSheetReach(first, { x: 0, y: 9000, width: 15000, height: 9000 })
    expect(scrolled).toEqual({ width: 30000, height: 27000 })
    // 往回滚:不缩,且原样返回同一个对象(setState 不重渲染)
    expect(grownSheetReach(scrolled, { x: 0, y: 0, width: 15000, height: 9000 })).toBe(scrolled)
  })

  it('stops at the last row Calc can have', () => {
    expect(grownSheetReach({ width: 0, height: 0 }, { x: 0, y: MAX_SHEET_REACH_TWIPS, width: 100, height: 100 }).height).toBe(MAX_SHEET_REACH_TWIPS)
  })
})
