/**
 * 文档坐标(twips)与画布坐标(CSS px / 设备像素)之间的换算,以及 tile 网格。
 *
 * ## 为了什么需求建的
 *
 * 办公画布向引擎要的是「文档里这块区域(twips)画成多少设备像素」,用户点的是 CSS px,
 * 引擎回报的光标 / 选区 / 失效矩形又是 twips(计划 §4.2 最后一条:换算写成可测纯函数)。
 * 这些换算一旦各处自己写一遍,缩放或高 DPI 屏上就会出现「点的位置和光标落点差一两个字」、
 * 「tile 之间有一像素缝或重叠」这类只在特定缩放下出现的问题。所以全部集中在这里。
 *
 * ## 不变式
 *
 * - 1 英寸 = 1440 twips = 96 CSS px,所以 100% 缩放下 1 CSS px = 15 twips。
 * - **相邻 tile 共用同一条边**:第 i 块的右边界就是第 i+1 块的左边界(都由 `tileEdge`
 *   算出),不会各自四舍五入出一条缝。代价是每块覆盖的 twips 可能差 1,画出来的缩放比
 *   有不到 0.1% 的差异 —— 远小于一个设备像素,看不出来;缝是看得出来的。
 * - tile 的**像素**尺寸固定(`tilePx`,设备像素),高 DPI 屏上一块 tile 覆盖的文档面积
 *   随 DPR 变小,而不是把低分辨率位图拉伸 —— 拉伸的表现是 Retina 屏上字发虚。
 *
 * ## 故意不做的
 *
 * - 不做调度(可见优先、LRU、离屏取消):那是视图的状态,属于 P3 的 tile 调度器,
 *   这里只回答「哪几块」「每块是哪片文档」。
 * - 不处理 Writer 页间距 / Calc 行列头:版面由引擎的 layout 查询给出,这里只做线性换算。
 */

export const TWIPS_PER_INCH = 1440
export const CSS_PX_PER_INCH = 96
export const TWIPS_PER_CSS_PX = TWIPS_PER_INCH / CSS_PX_PER_INCH

// 与宿主的渲染像素上限一致(manager.ts 的 MAX_RENDER_DIMENSION、helper 的 kMaxRenderPixels)
export const MAX_TILE_PX = 2048
const MIN_ZOOM = 0.05
const MAX_ZOOM = 16
const MAX_DPR = 8

/** 一张 tile 网格:缩放、设备像素比、每块 tile 的边长(设备像素)共同决定 */
export interface TileGrid {
  /** 文档缩放,1 = 100% */
  zoom: number
  /** window.devicePixelRatio */
  dpr: number
  /** 每块 tile 的边长(设备像素),整数 */
  tilePx: number
}

/** 一块 tile 的位置 */
export interface TileIndex {
  col: number
  row: number
}

/** 交给引擎的渲染请求(与 `DocumentRenderRequest` 同形,不含 part) */
export interface TileRequest {
  x: number
  y: number
  tileWidth: number
  tileHeight: number
  width: number
  height: number
}

/** twips 矩形 */
export interface TwipsRect {
  x: number
  y: number
  width: number
  height: number
}

/** CSS px 矩形(相对文档原点,即已经加上滚动偏移) */
export interface CssRect {
  left: number
  top: number
  width: number
  height: number
}

function assertZoom(zoom: number): void {
  if (!Number.isFinite(zoom) || zoom < MIN_ZOOM || zoom > MAX_ZOOM) throw new RangeError(`zoom must be between ${MIN_ZOOM} and ${MAX_ZOOM}`)
}

/**
 * 校验网格参数。★ 这些值来自视图(缩放滑块、devicePixelRatio),0 或 NaN 会让下面的除法
 * 产出 Infinity,进而发出一个 tileWidth 为 Infinity 的渲染请求 —— 被宿主拒掉还好,
 * 若先被 Math.round 成巨大整数,就是一次要画整个文档的请求。
 */
export function assertTileGrid(grid: TileGrid): void {
  assertZoom(grid.zoom)
  if (!Number.isFinite(grid.dpr) || grid.dpr <= 0 || grid.dpr > MAX_DPR) throw new RangeError(`dpr must be in (0, ${MAX_DPR}]`)
  if (!Number.isInteger(grid.tilePx) || grid.tilePx < 1 || grid.tilePx > MAX_TILE_PX) throw new RangeError(`tilePx must be an integer in [1, ${MAX_TILE_PX}]`)
}

/** CSS px → twips(取整,且不小于 0:文档坐标没有负值,引擎会拒绝负坐标) */
export function cssPxToTwips(px: number, zoom: number): number {
  assertZoom(zoom)
  return Math.max(0, Math.round((px * TWIPS_PER_CSS_PX) / zoom))
}

/** twips → CSS px(不取整:光标 / 选区覆盖层要亚像素定位,取整会让光标在字间抖动) */
export function twipsToCssPx(twips: number, zoom: number): number {
  assertZoom(zoom)
  return (twips * zoom) / TWIPS_PER_CSS_PX
}

/** twips 矩形 → CSS px 矩形(覆盖层用) */
export function twipsRectToCss(rect: TwipsRect, zoom: number): CssRect {
  return { left: twipsToCssPx(rect.x, zoom), top: twipsToCssPx(rect.y, zoom), width: twipsToCssPx(rect.width, zoom), height: twipsToCssPx(rect.height, zoom) }
}

/** 一块 tile 覆盖多少 twips(浮点;真正的边界由 `tileEdge` 取整) */
function twipsPerTile(grid: TileGrid): number {
  return (grid.tilePx * TWIPS_PER_CSS_PX) / (grid.zoom * grid.dpr)
}

/** 第 i 条 tile 边界的 twips 坐标。相邻 tile 共用它,见文件头不变式 */
function tileEdge(i: number, span: number): number {
  return Math.floor(i * span)
}

/** 包含 twips 坐标 `t` 的那块 tile 的序号 */
function tileAt(t: number, span: number): number {
  const i = Math.max(0, Math.floor(t / span))
  // floor(i*span) 取整后,边界可能正好落在 t 上:那时 t 属于下一块
  return tileEdge(i + 1, span) <= t ? i + 1 : i
}

/** 一块 tile 对应的渲染请求 */
export function tileRequest(tile: TileIndex, grid: TileGrid): TileRequest {
  assertTileGrid(grid)
  if (!Number.isInteger(tile.col) || !Number.isInteger(tile.row) || tile.col < 0 || tile.row < 0) throw new RangeError('tile index must be non-negative integers')
  const span = twipsPerTile(grid)
  const x = tileEdge(tile.col, span)
  const y = tileEdge(tile.row, span)
  return {
    x,
    y,
    // span ≥ 15/(16*8) > 0.1,所以相邻两条取整边界之差可能是 0:至少给 1 twip,引擎拒绝 0 尺寸
    tileWidth: Math.max(1, tileEdge(tile.col + 1, span) - x),
    tileHeight: Math.max(1, tileEdge(tile.row + 1, span) - y),
    width: grid.tilePx,
    height: grid.tilePx
  }
}

/**
 * 与 twips 矩形相交的全部 tile(按行优先)。失效矩形 → 要重画哪几块;视口 → 要画哪几块。
 * `limit` 是文档尺寸(twips):超出文档的 tile 不要,空文档返回空数组。
 */
export function tilesCovering(rect: TwipsRect, grid: TileGrid, limit: { width: number; height: number }): TileIndex[] {
  assertTileGrid(grid)
  const right = Math.min(rect.x + rect.width, limit.width)
  const bottom = Math.min(rect.y + rect.height, limit.height)
  const left = Math.max(0, rect.x)
  const top = Math.max(0, rect.y)
  if (right <= left || bottom <= top) return []
  const span = twipsPerTile(grid)
  const firstCol = tileAt(left, span)
  const firstRow = tileAt(top, span)
  // 右 / 下边界是开区间:减 1 twip 找最后一个被覆盖的点所在的块
  const lastCol = tileAt(right - 1, span)
  const lastRow = tileAt(bottom - 1, span)
  const tiles: TileIndex[] = []
  for (let row = firstRow; row <= lastRow; row++) {
    for (let col = firstCol; col <= lastCol; col++) tiles.push({ col, row })
  }
  return tiles
}

/** 可见区域(CSS px,已含滚动偏移)覆盖的 tile */
export function tilesInView(view: CssRect, grid: TileGrid, limit: { width: number; height: number }): TileIndex[] {
  assertTileGrid(grid)
  const x = cssPxToTwips(view.left, grid.zoom)
  const y = cssPxToTwips(view.top, grid.zoom)
  // 右 / 下边向外取整:少算一个 twip 就可能漏掉视口边上那一列 tile,表现为滚动时边缘露白
  const right = Math.ceil(((view.left + view.width) * TWIPS_PER_CSS_PX) / grid.zoom)
  const bottom = Math.ceil(((view.top + view.height) * TWIPS_PER_CSS_PX) / grid.zoom)
  return tilesCovering({ x, y, width: right - x, height: bottom - y }, grid, limit)
}

/**
 * 表格可滚动范围的上限(twips):Calc 最多 1048576 行,默认行高 255 twips ≈ 2.7 亿。
 * 列方向(16384 列 × 默认列宽 1275)远小于它,共用一个上限。
 */
export const MAX_SHEET_REACH_TWIPS = 2 ** 28

/**
 * 表格画布的可滚动范围:已到达的最远可见区域之外再留一屏。
 *
 * ★ 引擎报的表格尺寸只跟着**单元格光标**长(空表约 A1:R51;实测登记可见区域也不长),
 *   按它画的话滚轮滚到底就停住,用户看不到也点不到下面的格子。所以画布自己留余量:
 *   滚到哪儿长到哪儿;只长不缩,往回滚时滚动条不跳。点到余量里的格子,光标过去,引擎的尺寸随之变大。
 * 没变时原样返回同一个对象(调用方用它做 setState,不引发重渲染)。
 */
export function grownSheetReach(reach: { width: number; height: number }, visible: TwipsRect): { width: number; height: number } {
  const width = Math.min(MAX_SHEET_REACH_TWIPS, Math.max(reach.width, visible.x + 2 * visible.width))
  const height = Math.min(MAX_SHEET_REACH_TWIPS, Math.max(reach.height, visible.y + 2 * visible.height))
  return width === reach.width && height === reach.height ? reach : { width, height }
}
