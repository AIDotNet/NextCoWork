/**
 * 富内容悬停提示。
 *
 * ★ **这个组件不是给「一句话说明」用的 —— 那个用原生 `title` 就够了**
 * (`IconButton` 走的就是原生)。它解决的是「提示里有结构」的场景:
 * 转录区那个 token 用量浮层要分行列出 input / output / 缓存命中,
 * 原生 `title` 塞不下,于是那里手写了一个 `group-hover:opacity-100` 的版本。
 *
 * 手写那版有三个不是样式的问题:
 *
 * 1. **键盘用户永远看不到。** 只有 `:hover` 能触发,`Tab` 走到触发器上什么也
 *    不会发生。这条是可访问性缺陷,不是观感问题。
 * 2. **会被祖先的 `overflow` 裁掉。** 提示是触发器的绝对定位后代,转录区是
 *    `overflow-y-auto`,贴着可视区顶端的那条用量行一悬停,提示的上半截直接没了。
 * 3. **贴着窗口右边时会溢出。** `left-0` 是死的,`max-w-[min(360px,…)]` 只限制
 *    了宽度,没有把它推回视口内。
 *
 * 对应的三个解法就是下面的三段代码:portal 到 body(绕开 overflow,理由和
 * `Dialog.tsx` 文件头那段「必须 portal」完全一样 —— 那是几何问题不是层叠问题)、
 * `focus-within` 一并触发、打开时实测视口做碰撞翻转。
 *
 * ★ **延迟只加在「开」上,「关」是立刻。** 鼠标扫过一排元素时,50ms 的开启延迟
 *   让沿途的提示都不弹出来;而一旦移开就该马上消失 —— 关也加延迟的话,提示会
 *   黏在光标后面追着跑。
 *   原先所有浮层都不可交互；现在仅账单类长内容可选 interactive，允许跨过间隙
 *   进入可滚动卡片，其余浮层继续遵守移开立即关闭。
 */
import { AnimatePresence, motion } from 'motion/react'
import {
  useCallback,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode
} from 'react'
import { createPortal } from 'react-dom'
import { cn } from '../../lib/cn'
import { motionScale, useMotionLevel } from '../../theme/useMotionLevel'

/** 提示与触发器之间的空隙 */
const GAP = 8
/** 离视口边缘至少留这么多,避免贴死在边上 */
const EDGE = 12
const OPEN_DELAY_MS = 50

type Placement = 'top' | 'bottom'

export function Tooltip({
  content,
  children,
  align = 'start',
  className,
  contentClassName,
  interactive = false
}: {
  /** 提示体。给 `undefined` 就退化成纯粹的包裹层,不挂任何监听 */
  content: ReactNode
  children: ReactNode
  /** 水平对齐:跟触发器左缘对齐,还是居中 */
  align?: 'start' | 'center'
  className?: string
  contentClassName?: string
  /** 仅长内容滚动浮层启用；普通提示仍不可进入、移开即关。 */
  interactive?: boolean
}): ReactNode {
  const id = `tooltip-${useId()}`
  const scale = motionScale(useMotionLevel())
  const anchorRef = useRef<HTMLSpanElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  const timer = useRef<number | undefined>(undefined)
  const [open, setOpen] = useState(false)
  const [pos, setPos] = useState<{ left: number; top: number; placement: Placement } | null>(null)

  const show = useCallback((immediate = false) => {
    window.clearTimeout(timer.current)
    if (immediate) return setOpen(true)
    timer.current = window.setTimeout(() => setOpen(true), OPEN_DELAY_MS)
  }, [])

  const hide = useCallback(() => {
    window.clearTimeout(timer.current)
    setOpen(false)
  }, [])
  // 需求：长账单要能从触发器越过 8px 间隙进入浮层滚动；普通提示仍立即关闭。
  const leaveAnchor = (): void => {
    if (!interactive) return hide()
    // 需求：经过费用读数但没停留 50ms 时，取消待开启的浮层与账目查询。
    window.clearTimeout(timer.current)
    if (open) timer.current = window.setTimeout(hide, 150)
  }

  /*
    ★ 位置算在 `useLayoutEffect` 里 —— 浏览器绘制之前同步跑完,所以提示的第一帧
    就已经在正确的位置上。放 `useEffect` 会先在 (0,0) 画一帧,肉眼能看到它从
    左上角弹过来。

    (注意这和 `Segmented` 那边删掉测量的结论不矛盾:那里测的是**自己的布局**,
    Motion 有更好的时机;这里测的是**视口碰撞**,除了绘制前没有别的时机。)
  */
  useLayoutEffect(() => {
    if (!open) return setPos(null)
    const element = tipRef.current
    if (element === null) return
    const measure = (): void => {
      const anchor = anchorRef.current?.getBoundingClientRect()
      const tip = element.getBoundingClientRect()
      if (anchor === undefined) return

      // 上方放不下就翻到下方 —— 而不是硬塞进去被视口裁掉
      const fitsAbove = anchor.top - tip.height - GAP >= EDGE
      const placement: Placement = fitsAbove ? 'top' : 'bottom'
      const top = fitsAbove ? anchor.top - tip.height - GAP : anchor.bottom + GAP

      const rawLeft = align === 'center' ? anchor.left + anchor.width / 2 - tip.width / 2 : anchor.left
      // 夹回视口内。`Math.max` 放在外面:窗口窄到装不下提示时,宁可右边溢出
      // 也要保证左边可见 —— 从左往右读,左边被切等于整条读不了
      const left = Math.max(EDGE, Math.min(rawLeft, window.innerWidth - tip.width - EDGE))
      setPos((current) => current?.left === left && current.top === top && current.placement === placement
        ? current : { left, top, placement })
    }
    measure()
    // 需求：账目异步加载后卡片从一行变为多行，必须重测位置；否则会盖住输入框。
    if (!interactive) return
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [open, align, content, interactive])

  if (content === undefined || content === null) {
    return <span className={className}>{children}</span>
  }

  return (
    <>
      <span
        ref={anchorRef}
        className={cn('w-fit', className)}
        // ★ `focus`/`blur` 用捕获阶段:原生 focus 事件不冒泡,挂在包裹层上
        // 收不到内部按钮的焦点。React 的合成事件已经帮忙做成冒泡的了,
        // 所以这里写 onFocus 就够 —— 但键盘触发要**立刻**显示,不走延迟:
        // 延迟是为了过滤「路过」,而 Tab 过去是明确的意图。
        onPointerEnter={() => show()}
        onPointerLeave={leaveAnchor}
        onFocus={() => show(true)}
        onBlur={(event) => {
          if (interactive && tipRef.current?.contains(event.relatedTarget as Node)) return
          hide()
        }}
        aria-describedby={open ? id : undefined}
      >
        {children}
      </span>

      {createPortal(
        <AnimatePresence>
          {open && (
            <motion.div
              id={id}
              ref={tipRef}
              role="tooltip"
              tabIndex={interactive ? 0 : undefined}
              onPointerEnter={interactive ? () => window.clearTimeout(timer.current) : undefined}
              onPointerLeave={interactive ? hide : undefined}
              onBlur={interactive ? hide : undefined}
              initial={{ opacity: 0, y: pos?.placement === 'bottom' ? -4 : 4, scale: 0.98 }}
              animate={{ opacity: 1, y: 0, scale: 1 }}
              exit={{ opacity: 0, scale: 0.98 }}
              transition={{ duration: 0.14 * scale, ease: [0.32, 0.72, 0, 1] }}
              style={{
                // 位置还没算出来的那一帧先藏在视口外,别在左上角闪一下
                left: pos?.left ?? -9999,
                top: pos?.top ?? -9999
              }}
              className={cn(
                // ★ `z-[150]` 而不是 100 —— 见 theme.css 末尾 z 轴那段的第四档:
                // 「portal 到 body 的浮层被放进模态时的例外档」。提示挂在 body 上,
                // 和 Dialog 是同级兄弟,同为 z-100 时谁后挂谁在上,顺序不可控;
                // 抬到 150 才稳定压住弹窗。(`Select` 的 `inModal` 用的是同一档。)
                'fixed z-[150] w-max max-w-[min(360px,calc(100vw-48px))]',
                interactive ? 'pointer-events-auto' : 'pointer-events-none',
                'rounded-card border border-stroke bg-surface-raised px-3 py-2',
                'text-[11px] text-fg shadow-lg',
                contentClassName
              )}
            >
              {content}
            </motion.div>
          )}
        </AnimatePresence>,
        document.body
      )}
    </>
  )
}
