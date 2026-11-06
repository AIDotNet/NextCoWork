/**
 * 折叠期的滚动锚点补偿 ——「上面那块塌了,用户正在读的内容不许跟着跳」。
 *
 * 需求:三处收起(工具组完成后自动收、run 结束把过程段收进「用时」摘要行、
 * 手动收起摘要行)都发生在用户正在看**下方正文**的时候。高度骤减几百像素时
 * 浏览器只保证 `scrollTop` 数值不变,视口相对内容会向上蹿 —— 表现为流式打字
 * 或结论正文冷不防往上弹一下,且全程零报错。
 *
 * 贴底阅读的那半边**不归这里管**:`Thread` 的 `follow()`(ResizeObserver 每帧
 * 把视图按到底)和浏览器自身的夹取已经把视口钉住了,两边量出来的位移≈0,
 * 补偿伺服自然闲置 —— 这也是下面第 1、2 条中止条件能安全存在的原因。
 *
 * ★ 基线(折叠前的锚点底边)必须来自折叠**之前**:瞬时换装在 commit 当帧高度
 * 就已经变了,当帧才测等于测到结果、位移是 0。所以未折叠时每个 commit 都记
 * 一次,外加一条 `BASELINE_TAIL_MS` 的 rAF 尾流 —— 展开入场是 Motion 逐帧改
 * 高度、**不发 commit**,没有尾流的话基线会停在入场前,下次收起时把入场的
 * 位移当成折叠位移回补,表现为「点收起时视口莫名下滑一整段」。
 *
 * 动画期间逐帧补偿,不只补一帧:首帧位移≈0,单帧写法(被淘汰的
 * `WorkspaceBlock` 里那段)在动画曲线上永远等不到目标位移。
 *
 * 三条中止条件各对应一种「不该由我们写 scrollTop」的场景:
 *   1. 别人动了 scrollTop(浏览器夹取 / `Thread` 的 follow() / 用户滚轮)——
 *      再写就是跟它抢,表现为视口来回抖;
 *   2. 折叠点整个落在视口下方 —— 可见内容纹丝不动,补了反而把上面的历史拽下来;
 *   3. 到点(`SERVO_MS`,盖住 0.22s × soft 档 = 275ms 的退场)—— 之后纯属空转。
 *
 * ★ 一次运行里可能有多个锚点同时在补,但**实际不会重叠**:run 结束那一下
 * 前面有 `FOLD_HOLD_MS` 的停顿,工具组的折叠早在正文开始前就播完了。
 * 若真的首尾相接,两次回补会叠加成一次多余的位移 —— 这是已知代价,
 * 换成「每滚动容器一个伺服」的复杂度不值当。
 */
import { useEffect, useLayoutEffect, useRef } from 'react'

/**
 * 需求:折起来之前先让人把完成态(✓、耗时)看一眼再收。
 * 工具组自动收与 run 结束收束**共用这一个数** —— 两处节奏不同会让人感觉
 * 界面有两套脾气;400 与被淘汰的 `WorkspaceBlock` 量过的注意力切换时间一致。
 * 不满足会怎样:结果一到就折,完成态一帧都留不住,连续几个组完成时正文被
 * 连拽几次,表现为「工具跑完界面自己抖了几下」。
 */
export const FOLD_HOLD_MS = 400

/** 补偿伺服的运行窗口。0.22s × soft(1.25)= 275ms,再多留一截余量。 */
const SERVO_MS = 400
/** 折叠后基线尾流的时长。要盖过入场动画(同上 275ms),否则基线停在入场前。 */
const BASELINE_TAIL_MS = 400

/**
 * @param anchorRef 折叠时**底边会移动**的那段元素 —— 补偿钉住的就是它的底边。
 * @param folding 当前是否处于「收起」状态;false→true 的那一跳启动伺服。
 * @param foldTopRef 可选。折叠点(收缩区域)顶端所在的元素;不传就用 anchor 自己。
 *   只有 run 结束那次换装需要它:锚点是整个 turn(底边在折叠点下方很远),
 *   而「该不该补」取决于**折叠点**在不在视口里 —— 用户翻回本 turn 顶部阅读时
 *   折叠发生在视口下方,那次绝不能动视口。
 * @param enabled 记基线的开关。单项工具组永不自动收起(`isCompletedToolGroup`
 *   要求 >1),给它每帧记一次基线是纯浪费 —— 历史会话里几十个单项组会在
 *   流式期间各多读一次布局。
 */
export function useFoldAnchor(
  anchorRef: React.RefObject<HTMLElement | null>,
  folding: boolean,
  foldTopRef?: React.RefObject<HTMLElement | null>,
  enabled: boolean = true
): void {
  const baseline = useRef<number | null>(null)
  const prevFolding = useRef(false)
  const pending = useRef(0)
  const raf = useRef(0)

  useLayoutEffect(() => {
    const el = anchorRef.current
    if (el === null) return
    if (folding) {
      if (prevFolding.current) return
      // 折叠 commit 当帧:瞬时换装此时高度已变,差值就是这一下要补的整段;
      // Motion 退场此时还没动高度,差值≈0,交给后面的逐帧伺服。
      const base = baseline.current
      pending.current = base === null ? 0 : el.getBoundingClientRect().bottom - base
      runServo()
    } else if (enabled) {
      baseline.current = el.getBoundingClientRect().bottom
      runTail()
    }
    prevFolding.current = folding
    // 刻意不挂依赖数组:每次 commit 都要重跑(刷新基线 / 侦测折叠那一跳),
    // 而循环本身只在 false→true 那一跳启动一次,重复进入会被 prevFolding 挡住。
  })

  useEffect(() => () => cancelAnimationFrame(raf.current), [])

  const runTail = (): void => {
    cancelAnimationFrame(raf.current)
    const until = performance.now() + BASELINE_TAIL_MS
    const step = (): void => {
      if (prevFolding.current) return // 伺服接管,尾流让位
      const el = anchorRef.current
      if (el === null) return
      baseline.current = el.getBoundingClientRect().bottom
      if (performance.now() >= until) return
      raf.current = requestAnimationFrame(step)
    }
    raf.current = requestAnimationFrame(step)
  }

  const runServo = (): void => {
    cancelAnimationFrame(raf.current)
    let lastWrite: number | null = null
    let pinned: number | null = null
    const start = performance.now()
    const step = (): void => {
      const el = anchorRef.current
      const top = (foldTopRef?.current ?? el)?.getBoundingClientRect().top
      const scroller = el?.closest('[data-testid="thread"]')
      if (el === null || top === undefined || !(scroller instanceof HTMLElement)) return
      if (performance.now() - start >= SERVO_MS) return
      // 条件 1:别人动过 scrollTop。我们写过的值会原样读回来(±夹取),
      // 对不上就是夹取/follow()/滚轮介入了 —— 让路。
      if (lastWrite !== null && Math.abs(scroller.scrollTop - lastWrite) > 1) return
      // 条件 2:折叠点还在视口下方 —— 可见区没有因它移动,补了反而拽动历史。
      if (top > scroller.clientHeight) return
      if (pending.current !== 0) {
        scroller.scrollTop += pending.current
        lastWrite = scroller.scrollTop
        pending.current = 0
        pinned = el.getBoundingClientRect().bottom
        raf.current = requestAnimationFrame(step)
        return
      }
      const bottom = el.getBoundingClientRect().bottom
      if (pinned === null) {
        pinned = bottom
      } else {
        // 钉住的目标(pinned)一旦定下就不再更新:每帧量的是**累计**位移,
        // 逐帧增量会漏掉动画头一帧那 ≈0 的一段。
        const delta = bottom - pinned
        if (Math.abs(delta) >= 0.5) {
          scroller.scrollTop += delta
          lastWrite = scroller.scrollTop
        }
      }
      raf.current = requestAnimationFrame(step)
    }
    raf.current = requestAnimationFrame(step)
  }
}
