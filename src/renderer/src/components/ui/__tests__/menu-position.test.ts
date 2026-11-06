import { describe, expect, it } from 'vitest'
import { placeMenu, placeSubmenu, type TriggerRect } from '../menu-position'

/**
 * 这一组守的是方案 §7 第 1 条修掉的那个 bug:菜单原来是 `absolute`,
 * 落在 `overflow-y-auto` 的内容区靠下位置时会被**裁掉半截**。
 *
 * ★ 裁掉半截是个**看得见**的坏,所以它当初能被发现;
 * 而换成 `fixed` 之后新引入的坏全是**算错坐标** —— 面板出现在离触发器十万八千里的
 * 地方、或者刚好被推出视口。那些同样看得见,但只在特定窗口高度下才复现,
 * 手点很难覆盖到。所以边界情况在这里穷举,而不是靠开着应用拖窗口试。
 */

const vp = { width: 1440, height: 900 }
/** 视口正中的一个触发器:上下都宽敞,不该翻 */
const middle: TriggerRect = { top: 400, bottom: 432, left: 300, right: 580 }

describe('placeMenu · 竖直方向', () => {
  it('下方够用时挂在触发器下面,留 6px 空隙', () => {
    const p = placeMenu(middle, 200, 280, 'start', vp)
    expect(p.flipped).toBe(false)
    expect(p.top).toBe(432 + 6)
  })

  /** ★ 这就是原来会被裁掉的那种位置 */
  it('贴着视口底边、上面宽敞时向上翻,面板底缘落在触发器上方', () => {
    const low: TriggerRect = { top: 820, bottom: 852, left: 300, right: 580 }
    const p = placeMenu(low, 300, 280, 'start', vp)
    expect(p.flipped).toBe(true)
    expect(p.top + 300).toBe(820 - 6)
  })

  /**
   * ★ 判据是「上面更宽敞」而不是「下面放不下」。触发器在正中时上下一样挤,
   * 翻上去没有任何好处 —— 只会让面板每次出现在意料之外的一侧。
   */
  it('菜单很长但上下一样挤时不翻,改成限高内部滚动', () => {
    const p = placeMenu(middle, 5000, 280, 'start', vp)
    expect(p.flipped).toBe(false)
    expect(p.maxHeight).toBe(900 - 432 - 6 - 8)
    // 限了高就不该再被推出视口
    expect(p.top + p.maxHeight).toBeLessThanOrEqual(900 - 8)
  })

  it('翻上去之后同样限高,不会长到视口上边之外', () => {
    const low: TriggerRect = { top: 700, bottom: 732, left: 300, right: 580 }
    const p = placeMenu(low, 5000, 280, 'start', vp)
    expect(p.flipped).toBe(true)
    expect(p.top).toBeGreaterThanOrEqual(8)
  })

  /**
   * ★ 视口极矮(窗口被拖成一条)时,可用高度可能只剩十几像素。
   * 那时宁可让面板盖住触发器,也不能给出一条谁也点不中的缝。
   */
  it('视口极矮时保底给出可用高度,且仍然留在视口里', () => {
    const tiny = { width: 1440, height: 180 }
    const t: TriggerRect = { top: 120, bottom: 152, left: 300, right: 580 }
    const p = placeMenu(t, 400, 280, 'start', tiny)
    expect(p.maxHeight).toBeGreaterThanOrEqual(120)
    expect(p.top).toBeGreaterThanOrEqual(8)
  })

  it('视口比面板还矮时上界不会算成负数', () => {
    const t: TriggerRect = { top: 10, bottom: 40, left: 300, right: 580 }
    const p = placeMenu(t, 400, 280, 'start', { width: 1440, height: 100 })
    expect(p.top).toBe(8)
  })
})

describe('placeMenu · 水平方向', () => {
  it("align='start' 左缘对齐触发器左缘", () => {
    expect(placeMenu(middle, 200, 280, 'start', vp).left).toBe(300)
  })

  it("align='end' 右缘对齐触发器右缘", () => {
    expect(placeMenu(middle, 200, 280, 'end', vp).left).toBe(580 - 280)
  })

  /** 触发器贴右边框时,右对齐算出来的位置本身是合法的,不该被误夹 */
  it('触发器贴着视口右边时面板不越界', () => {
    const right: TriggerRect = { top: 400, bottom: 432, left: 1300, right: 1432 }
    const p = placeMenu(right, 200, 280, 'start', vp)
    expect(p.left + 280).toBeLessThanOrEqual(1440 - 8)
  })

  it('触发器贴着视口左边、右对齐会算出负数时夹回边距', () => {
    const left: TriggerRect = { top: 400, bottom: 432, left: 4, right: 60 }
    expect(placeMenu(left, 200, 280, 'end', vp).left).toBe(8)
  })

  it('视口比面板还窄时贴左边距,不给负数', () => {
    const p = placeMenu(middle, 200, 280, 'start', { width: 200, height: 900 })
    expect(p.left).toBe(8)
  })
})

/**
 * ★ 这一组钉的是**第二次**踩到的那个坑:面板没被裁、坐标也没算错,
 * 却飘到了它不该出现的那块面上 —— 设置浮层的模型选择器盖住了左边的导航。
 *
 * 判据不是「在视口里」,是「在自己那块面里」。两者的差别只有给了 bounds 才看得出来,
 * 所以每一条都拿同一个触发器跑两遍:不给 bounds 时复现 bug,给了才对。
 */
describe('placeMenu · 夹在自己那块面里', () => {
  /** 设置浮层的内容列:左边 192px 是导航,面板不许越过 */
  const content = { top: 100, left: 392, right: 1240, bottom: 820 }
  /** 模型页那颗药丸:窄(132px),且靠着内容列的右侧 */
  const pill: TriggerRect = { top: 130, bottom: 152, left: 448, right: 580 }

  it('★ 不给边界时会往左伸出内容列 —— 这就是那个 bug 的样子', () => {
    // 280 宽的面板右对齐到 580,左缘落在 300:比内容列左边缘(392)还靠左 92px
    expect(placeMenu(pill, 200, 280, 'end', vp).left).toBe(300)
  })

  it('给了边界就被推回来,左缘不越过内容列', () => {
    const p = placeMenu(pill, 200, 280, 'end', vp, content)
    expect(p.left).toBe(392 + 8)
    // 推回来之后仍然完整放得下,没有被压窄
    expect(p.left + 280).toBeLessThanOrEqual(1240)
  })

  it('右侧同样夹住:贴着内容列右缘的触发器不许把面板顶出去', () => {
    const right: TriggerRect = { top: 130, bottom: 152, left: 1180, right: 1236 }
    const p = placeMenu(right, 200, 280, 'start', vp, content)
    expect(p.left + 280).toBe(1240 - 8)
  })

  it('竖直方向照样按内容列算:贴着内容列底边时向上翻,而不是等到视口底边才翻', () => {
    // 触发器下方:视口还剩 900-800=100,内容列只剩 820-800=20。
    // 面板 80px —— 在视口里放得下(不翻),在内容列里放不下(该翻)。
    // ★ 面板高度必须挑在这两个数中间,否则两次调用都翻,这条就没在对照任何东西了
    const low: TriggerRect = { top: 768, bottom: 800, left: 448, right: 580 }
    expect(placeMenu(low, 80, 280, 'end', vp).flipped).toBe(false)

    const p = placeMenu(low, 80, 280, 'end', vp, content)
    expect(p.flipped).toBe(true)
    expect(p.top + 80).toBe(768 - 6)
  })

  it('★ 边界有一部分在视口外时,和视口取交集 —— 面板宁可盖住边界也不能跑出视口', () => {
    // 窗口被拖小之后内容列的下半截在视口外
    const spilling = { top: 100, left: 392, right: 1240, bottom: 2000 }
    const t: TriggerRect = { top: 400, bottom: 432, left: 448, right: 580 }
    const p = placeMenu(t, 5000, 280, 'end', { width: 1440, height: 600 }, spilling)
    expect(p.top + p.maxHeight).toBeLessThanOrEqual(600)
  })

  it('不给边界时和以前逐字一样(五个参数的旧调用点不受影响)', () => {
    const full = { top: 0, left: 0, right: vp.width, bottom: vp.height }
    for (const align of ['start', 'end'] as const) {
      expect(placeMenu(middle, 200, 280, align, vp)).toEqual(
        placeMenu(middle, 200, 280, align, vp, full)
      )
    }
  })
})

/** 文件树右键「打开方式 ›」的二级菜单。它常常就贴着窗口右缘(文件树在右侧面板)。 */
describe('placeSubmenu', () => {
  const row: TriggerRect = { top: 300, bottom: 328, left: 600, right: 820 }

  it('右边放得下时贴着那一行右侧弹出,第一项与那一行对齐(减去面板内边距)', () => {
    const p = placeSubmenu(row, 200, 220, vp)
    expect(p.flipped).toBe(false)
    expect(p.left).toBe(820 + 4)
    expect(p.top).toBe(300 - 4)
  })

  it('★ 右边放不下时翻到父菜单左侧 —— 参考截图里正是这种情况', () => {
    const nearEdge: TriggerRect = { top: 300, bottom: 328, left: 1200, right: 1420 }
    const p = placeSubmenu(nearEdge, 200, 220, vp)
    expect(p.flipped).toBe(true)
    expect(p.left + 220).toBe(1200 - 4)
  })

  it('靠近底边时整体往上推,而不是长出视口', () => {
    const low: TriggerRect = { top: 860, bottom: 888, left: 600, right: 820 }
    const p = placeSubmenu(low, 300, 220, vp)
    expect(p.top + 300).toBeLessThanOrEqual(900 - 8)
  })

  it('窗口窄到两边都放不下时夹回视口,宁可盖住父菜单也不出屏', () => {
    const narrow = { width: 360, height: 900 }
    const wide: TriggerRect = { top: 300, bottom: 328, left: 20, right: 340 }
    const p = placeSubmenu(wide, 200, 220, narrow)
    expect(p.left).toBeGreaterThanOrEqual(8)
    expect(p.left + 220).toBeLessThanOrEqual(360 - 8)
  })
})
