/**
 * 菜单面板的落点计算 —— 方案 §7 第 1 条。
 *
 * ★ **单独一个 `.ts` 是为了能被测到。** `vitest.config.ts` 是 node 环境,
 * 且只收 `.test.ts`(`.tsx` 根本不匹配)—— 留在组件里的话
 * 「靠近视口底部要向上翻」这条逻辑一行测试都不会跑,而它恰恰是纯算术。
 *
 * 这个函数只认数字,不碰 DOM:调用方把 `getBoundingClientRect()` 和
 * `innerWidth/innerHeight` 喂进来。
 */

/** `DOMRect` 里这个函数用得到的那几个字段 */
export interface TriggerRect {
  top: number
  bottom: number
  left: number
  right: number
}

/**
 * 面板不许越过的边框(视口坐标)。**默认是视口,但那对浮层里的菜单是错的。**
 *
 * ★ 起因是模型页那两颗药丸:触发器只有 132px 宽,而面板 280px 且 `align='end'`
 * (右缘对齐触发器右缘)—— 于是面板往左伸出 148px,穿过设置浮层的内容列、
 * 盖在左边那条导航上。视口没越界,所以旧的夹取一点都没拦。
 *
 * 看着像「菜单飘出去了」,但坐标其实一个没算错:**是夹取的对象选错了。**
 * 菜单属于哪块面,就该被哪块面夹住。
 */
export interface MenuBounds {
  top: number
  left: number
  right: number
  bottom: number
}

export interface Placement {
  /** 视口坐标,直接给 `position: fixed` 用 */
  top: number
  left: number
  /** 面板超过这个高度就内部滚动,而不是长到视口外面去 */
  maxHeight: number
  /** 翻到触发器上方了。调用方拿它决定圆角/箭头方向之类,目前只用于测试可读性 */
  flipped: boolean
}

/** 面板与触发器之间的空隙。和原来的 `mt-1.5` 对齐 */
const GAP = 6
/** 离视口边缘至少留这么多,免得贴边看着像被裁了 */
const EDGE = 8
/**
 * 再挤也要给出的高度。
 *
 * ★ 不给下界的话,触发器贴着视口底边时算出来的可用高度可能只有十几像素 ——
 * 那不是「紧凑」,是一条谁也点不中的缝。宁可让面板盖住触发器一点。
 */
const MIN_HEIGHT = 120

const clamp = (v: number, lo: number, hi: number): number => Math.min(Math.max(v, lo), hi)

/**
 * ★ **向上翻的判据是「上面比下面宽敞」,不是「下面放不下」。**
 *
 * 只看「下面放不下」的话,一个很长的菜单在**视口正中**也会翻上去 ——
 * 而那时上下一样挤,翻了没有任何好处,只是让面板每次都出现在意料之外的一侧。
 * 所以两个条件都要:放不下 **且** 上面确实更宽敞。
 */
export function placeMenu(
  trigger: TriggerRect,
  panelHeight: number,
  width: number,
  align: 'start' | 'end',
  viewport: { width: number; height: number },
  /** 省略 = 用视口。见 `MenuBounds` */
  bounds?: MenuBounds
): Placement {
  /*
    ★ 和视口取交集,不是直接采信 `bounds`:调用方喂进来的是一个
    `getBoundingClientRect()`,而那块面自己可能有一部分在视口外(窗口被拖小、
    或者它本身在滚动)。只夹到它身上的话,面板会被推到看不见的地方 ——
    比「盖住导航」更糟,因为那是**打开了却找不到**。
  */
  const area = {
    top: Math.max(0, bounds?.top ?? 0),
    left: Math.max(0, bounds?.left ?? 0),
    right: Math.min(viewport.width, bounds?.right ?? viewport.width),
    bottom: Math.min(viewport.height, bounds?.bottom ?? viewport.height)
  }
  const below = area.bottom - trigger.bottom - GAP - EDGE
  const above = trigger.top - area.top - GAP - EDGE

  const flipped = panelHeight > below && above > below
  const maxHeight = Math.max(MIN_HEIGHT, flipped ? above : below)
  const height = Math.min(panelHeight, maxHeight)

  const wanted = flipped ? trigger.top - GAP - height : trigger.bottom + GAP
  /*
    ★ 最后再夹一次,而不是信上面算出来的 `top`:`MIN_HEIGHT` 那条下界本身就可能
    把面板顶出视口(视口极矮时)。夹的上界用 `Math.max(EDGE, …)`,
    这样连 `height + 2*EDGE` 都塞不下的极端情况也不会得到一个负的上界。
  */
  const loTop = area.top + EDGE
  const top = clamp(wanted, loTop, Math.max(loTop, area.bottom - height - EDGE))

  // align='end' = 面板右缘对齐触发器右缘(原来的 `right-0`)
  const wantedLeft = align === 'start' ? trigger.left : trigger.right - width
  const loLeft = area.left + EDGE
  const left = clamp(wantedLeft, loLeft, Math.max(loLeft, area.right - width - EDGE))

  return { top, left, maxHeight, flipped }
}

/** 子菜单与父菜单那一行之间的横向空隙。比 `GAP` 小:它贴着父面板,不是贴着触发器 */
const SIDE_GAP = 4
/** 父面板的 `p-1`:子菜单上移这么多,第一项才和父菜单那一行对齐 */
const PANEL_PADDING = 4

export interface SubmenuPlacement {
  top: number
  left: number
  maxHeight: number
  /** 右边放不下,翻到了父菜单左侧 */
  flipped: boolean
}

/**
 * 二级菜单(文件树右键「打开方式 ›」)的落点:贴着父菜单那一行的右侧弹出,
 * 第一项与那一行对齐。
 *
 * 需求:文件树在右侧面板里,右键菜单本身往往就贴着窗口右缘 —— 参考截图里
 * 子菜单正是翻到**左侧**出现的。所以判据是「右边放得下就右边,否则左边」,
 * 两边都放不下(窗口极窄)时夹回视口,宁可盖住父菜单也不要出屏。
 *
 * ★ 纵向只做「往上推」不做翻转:子菜单的第一项必须和触发它的那一行在同一高度,
 *   否则鼠标从那一行横移过去会先扫过别的父菜单项、把子菜单切走。
 */
export function placeSubmenu(
  anchor: TriggerRect,
  panelHeight: number,
  width: number,
  viewport: { width: number; height: number }
): SubmenuPlacement {
  const maxHeight = Math.max(MIN_HEIGHT, viewport.height - EDGE * 2)
  const height = Math.min(panelHeight, maxHeight)
  const right = anchor.right + SIDE_GAP
  const fitsRight = right + width <= viewport.width - EDGE
  const wantedLeft = fitsRight ? right : anchor.left - SIDE_GAP - width
  const left = clamp(wantedLeft, EDGE, Math.max(EDGE, viewport.width - width - EDGE))
  const top = clamp(anchor.top - PANEL_PADDING, EDGE, Math.max(EDGE, viewport.height - height - EDGE))
  return { top, left, maxHeight, flipped: !fitsRight }
}
