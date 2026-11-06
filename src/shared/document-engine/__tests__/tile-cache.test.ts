/**
 * 画布 tile 账目(`tile-cache.ts`)。
 *
 * 钉住的需求:只要可见且缺失 / 过期的块;失效只标旧、不擦;回来晚了的结果仍会被重画;
 * 同一块不重复在途;换缩放 / 引擎重启全部作废;超预算先丢最久没用的、可见块不丢。
 */
import { describe, expect, it } from 'vitest'
import { TileCache, type TileConfig } from '../tile-cache'

const CONFIG: TileConfig = { grid: { zoom: 1, dpr: 1, tilePx: 256 }, generation: 1, part: 0 }
// 256 px × 15 twips = 3840 twips 一块
const DOC = { width: 3840 * 4, height: 3840 * 4 }
const t = (col: number, row: number): { col: number; row: number } => ({ col, row })

function filled(cache: TileCache<string>, tiles: { col: number; row: number }[], config = CONFIG): void {
  for (const { tile, ticket } of cache.plan(tiles, 100)) cache.put(tile, ticket, config, `px${tile.col}${tile.row}`, 10, tiles)
}

describe('TileCache', () => {
  it('plans only visible tiles that are missing, in visible order, and never twice while in flight', () => {
    const cache = new TileCache<string>(1_000)
    cache.configure(CONFIG)
    const visible = [t(1, 0), t(0, 0), t(2, 0)]
    expect(cache.plan(visible, 2).map((p) => p.tile)).toEqual([t(1, 0), t(0, 0)])
    // 前两块在途:只剩第三块
    expect(cache.plan(visible, 2).map((p) => p.tile)).toEqual([t(2, 0)])
    expect(cache.plan(visible, 2)).toEqual([])
  })

  it('keeps showing old pixels after an invalidation and re-plans exactly the touched tiles', () => {
    const cache = new TileCache<string>(1_000)
    cache.configure(CONFIG)
    const visible = [t(0, 0), t(1, 0), t(0, 1)]
    filled(cache, visible)
    // 一个字的失效矩形只碰第 (1,0) 块
    cache.invalidate({ all: false, rects: [{ x: 4000, y: 100, width: 200, height: 200 }] }, DOC)
    expect(cache.get(t(1, 0))).toMatchObject({ data: 'px10', stale: true })
    expect(cache.get(t(0, 0))?.stale).toBe(false)
    expect(cache.plan(visible, 10).map((p) => p.tile)).toEqual([t(1, 0)])
    // 别的工作表的失效碰不到这张表
    cache.invalidate({ all: false, rects: [{ x: 0, y: 0, width: 100, height: 100, part: 3 }] }, DOC)
    expect(cache.get(t(0, 0))?.stale).toBe(false)
  })

  it('stores a result that was overtaken by a newer invalidation but still asks for it again', () => {
    const cache = new TileCache<string>(1_000)
    cache.configure(CONFIG)
    const visible = [t(0, 0)]
    const [request] = cache.plan(visible, 1)
    // 请求在途时用户又打了一个字
    cache.invalidate({ all: false, rects: [{ x: 10, y: 10, width: 10, height: 10 }] }, DOC)
    expect(cache.put(t(0, 0), request?.ticket ?? 0, CONFIG, 'old', 10, visible)).toBe(true)
    expect(cache.get(t(0, 0))).toMatchObject({ data: 'old', stale: true })
    const [again] = cache.plan(visible, 1)
    expect(again?.tile).toEqual(t(0, 0))
    cache.put(t(0, 0), again?.ticket ?? 0, CONFIG, 'new', 10, visible)
    expect(cache.get(t(0, 0))).toMatchObject({ data: 'new', stale: false })
  })

  it('invalidates everything on a full repaint, and throws the cache away when zoom, DPR or generation change', () => {
    const cache = new TileCache<string>(1_000)
    cache.configure(CONFIG)
    filled(cache, [t(0, 0), t(1, 1)])
    cache.invalidate({ all: true, rects: [] }, DOC)
    expect(cache.get(t(1, 1))?.stale).toBe(true)
    expect(cache.configure({ ...CONFIG })).toBe(false)
    const zoomed = { ...CONFIG, grid: { ...CONFIG.grid, zoom: 1.25 } }
    expect(cache.configure(zoomed)).toBe(true)
    expect(cache.size).toBe(0)
    // 旧配置下发出的请求回来了:丢掉,不能把旧缩放的像素画到新网格上
    expect(cache.put(t(0, 0), 0, CONFIG, 'old zoom', 10, [])).toBe(false)
    expect(cache.configure({ ...zoomed, generation: 2 })).toBe(true)
  })

  it('evicts least recently used tiles over budget but never a visible one', () => {
    const cache = new TileCache<string>(30)
    cache.configure(CONFIG)
    filled(cache, [t(0, 0), t(1, 0), t(2, 0)])
    cache.get(t(0, 0))
    const visible = [t(3, 0)]
    const [request] = cache.plan(visible, 1)
    cache.put(t(3, 0), request?.ticket ?? 0, CONFIG, 'px30', 10, visible)
    // 预算 30 字节放得下三块:最久没用的 (1,0) 被丢,刚用过的 (0,0) 与可见的 (3,0) 留下
    expect(cache.bytes).toBe(30)
    expect(cache.get(t(1, 0))).toBeUndefined()
    expect(cache.get(t(0, 0))).toBeDefined()
    expect(cache.get(t(3, 0))).toBeDefined()
  })

  it('lets a failed tile be planned again', () => {
    const cache = new TileCache<string>(1_000)
    cache.configure(CONFIG)
    const visible = [t(0, 0)]
    cache.plan(visible, 1)
    cache.fail(t(0, 0), CONFIG)
    expect(cache.plan(visible, 1).map((p) => p.tile)).toEqual([t(0, 0)])
  })
})
