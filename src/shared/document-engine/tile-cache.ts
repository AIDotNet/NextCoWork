/**
 * 画布 tile 的缓存与调度账目 —— 哪些块要向引擎要、哪些块画的是旧内容、内存超了先丢谁。
 *
 * ## 为了什么需求建的
 *
 * 办公画布按块向引擎要像素(计划 §7.2):滚动时只要可见的块、先要可见的;引擎报失效区域时
 * 只重画被碰到的那几块;缩放 / 引擎重启后旧块全部作废。这些账目若写在 React 组件里就只能
 * 起 DOM 测,而它们恰恰是最容易出「滚动露白」「改了字画面不变」这类看不见的回归的地方。
 * 所以抽成纯数据结构,宿主画布(`plugin-ui/DocumentCanvas.tsx`)用它,单测直接冲它。
 *
 * ## 不变式
 *
 * - **旧块先留着显示,不先擦掉。** 失效只把块标成 stale,重画回来之前继续显示旧像素 ——
 *   先擦再画的表现是每打一个字那一行闪一下白。
 * - **回来晚了的结果不能冒充新的。** 请求发出之后该块又被失效过,回来的像素照样放进缓存
 *   (比空白好),但仍标 stale,调度器会再要一次。否则最后一次按键的效果可能永远画不出来。
 * - **同一块同时最多一个请求在途。** 引擎对同一块的第二个请求只是重复劳动,且回执顺序不保证。
 * - 配置(缩放、DPR、tile 边长、generation、part)一变,全部作废:同一个序号在新配置下
 *   指的是文档里另一片区域,拿旧像素去填就是把别处的内容画到这里。
 * - 内存按字节预算淘汰最久没用的块,**可见块永不淘汰**(淘汰了下一帧又要回来,来回抖)。
 */
import { tilesCovering, type TileGrid, type TileIndex, type TwipsRect } from './viewport'

export interface TileConfig {
  grid: TileGrid
  generation: number
  /** 工作表 / 幻灯片;Writer 固定 0 */
  part: number
}

export interface CachedTile<T> {
  tile: TileIndex
  data: T
  bytes: number
  /** 画的是被失效之前的内容,仍可显示,但要重画 */
  stale: boolean
}

interface Entry<T> extends CachedTile<T> {
  lastUsed: number
}

const keyOf = (tile: TileIndex): string => `${tile.col}:${tile.row}`

function sameConfig(a: TileConfig | null, b: TileConfig): boolean {
  return a !== null && a.generation === b.generation && a.part === b.part &&
    a.grid.zoom === b.grid.zoom && a.grid.dpr === b.grid.dpr && a.grid.tilePx === b.grid.tilePx
}

export class TileCache<T> {
  private config: TileConfig | null = null
  private readonly entries = new Map<string, Entry<T>>()
  /** 在途请求:块 → 发出时的失效纪元 */
  private readonly inFlight = new Map<string, number>()
  /** 每块最近一次被失效时的纪元;`allInvalidatedAt` 是整体失效 */
  private readonly invalidatedAt = new Map<string, number>()
  private allInvalidatedAt = -1
  private epoch = 0
  private clock = 0
  private total = 0

  constructor(private readonly budgetBytes: number) {}

  /** 换配置。返回 true = 配置变了、缓存已清空(调用方据此重画) */
  configure(config: TileConfig): boolean {
    if (sameConfig(this.config, config)) return false
    this.config = { grid: { ...config.grid }, generation: config.generation, part: config.part }
    this.entries.clear()
    this.inFlight.clear()
    this.invalidatedAt.clear()
    this.allInvalidatedAt = -1
    this.total = 0
    return true
  }

  get(tile: TileIndex): CachedTile<T> | undefined {
    const entry = this.entries.get(keyOf(tile))
    if (entry !== undefined) entry.lastUsed = ++this.clock
    return entry
  }

  /**
   * 引擎报了失效区域(twips)。`all` = 整体失效。只处理当前 part 的矩形 ——
   * 带着别的 part 的矩形(Calc 另一张表)碰不到这张表上的块。
   */
  invalidate(change: { all: boolean; rects: readonly (TwipsRect & { part?: number })[] }, limit: { width: number; height: number }): void {
    const config = this.config
    if (config === null) return
    this.epoch += 1
    if (change.all) {
      this.allInvalidatedAt = this.epoch
      for (const entry of this.entries.values()) entry.stale = true
      return
    }
    for (const rect of change.rects) {
      if (rect.part !== undefined && rect.part !== config.part) continue
      for (const tile of tilesCovering(rect, config.grid, limit)) {
        const key = keyOf(tile)
        this.invalidatedAt.set(key, this.epoch)
        const entry = this.entries.get(key)
        if (entry !== undefined) entry.stale = true
      }
    }
  }

  /**
   * 这一帧要向引擎要哪些块:可见的、(缺失或 stale)且不在途的,按 `visible` 的顺序,最多 `max` 块。
   * 返回的块已登记为在途,拿到结果后必须 `put` 或 `fail`。
   */
  plan(visible: readonly TileIndex[], max: number): { tile: TileIndex; ticket: number }[] {
    const out: { tile: TileIndex; ticket: number }[] = []
    if (this.config === null) return out
    for (const tile of visible) {
      if (out.length >= max) break
      const key = keyOf(tile)
      if (this.inFlight.has(key)) continue
      const entry = this.entries.get(key)
      if (entry !== undefined && !entry.stale) continue
      this.inFlight.set(key, this.epoch)
      out.push({ tile, ticket: this.epoch })
    }
    return out
  }

  /**
   * 一块渲染回来了。`config` 是**发请求时**的配置:期间配置变了,结果直接丢弃。
   * 返回 false = 被丢弃(配置已变)。
   */
  put(tile: TileIndex, ticket: number, config: TileConfig, data: T, bytes: number, visible: readonly TileIndex[]): boolean {
    const key = keyOf(tile)
    if (!sameConfig(this.config, config)) return false
    this.inFlight.delete(key)
    // 请求发出之后又失效过:照样收下(比空白好),但仍要重画(文件头第二条)
    // ticket = 发请求时的纪元;之后的失效纪元一定更大
    const stale = Math.max(this.invalidatedAt.get(key) ?? -1, this.allInvalidatedAt) > ticket
    const previous = this.entries.get(key)
    if (previous !== undefined) this.total -= previous.bytes
    this.entries.set(key, { tile, data, bytes, stale, lastUsed: ++this.clock })
    this.total += bytes
    this.evict(visible)
    return true
  }

  /** 请求失败:撤掉在途标记,下一帧会重新规划(调用方决定要不要退避) */
  fail(tile: TileIndex, config: TileConfig): void {
    if (sameConfig(this.config, config)) this.inFlight.delete(keyOf(tile))
  }

  get bytes(): number {
    return this.total
  }

  get size(): number {
    return this.entries.size
  }

  private evict(visible: readonly TileIndex[]): void {
    if (this.total <= this.budgetBytes) return
    const keep = new Set(visible.map(keyOf))
    const candidates = [...this.entries.entries()].filter(([key]) => !keep.has(key)).sort((a, b) => a[1].lastUsed - b[1].lastUsed)
    for (const [key, entry] of candidates) {
      if (this.total <= this.budgetBytes) break
      this.entries.delete(key)
      this.total -= entry.bytes
    }
  }
}
