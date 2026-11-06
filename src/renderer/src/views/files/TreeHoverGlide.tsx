/**
 * 文件树的悬停底色 —— 一块跟着指针在行与行之间**滑动**的底,而不是每一行各自亮灭。
 *
 * 需求:对照 beUI File Tree 的 `SharedLayoutBg`(悬停项底色随 spring 滑过去)。
 *
 * ★ 没照搬它 `layoutId` + 每行一份 pill 的做法:那要给每一行包一层 `motion` 组件,而这棵树
 *   一个目录就能摊出上千行;悬停状态放在父组件里还会让每一次换行都重渲整棵树。
 *   这里全树只有**一个**元素,自己监听容器上的 `pointerover`、自己持有位置 state ——
 *   换行时只有这一块重渲染,行本身一概不动。
 *
 * ★ 必须是容器(`role="tree"`,`position: relative`)的**第一个子元素**:行是
 *   `position: relative` 且排在它后面,按文档顺序画在它上面;挪到后面就会盖住文字。
 *
 * 动效档位走 `useMotionLevel`(Motion 不受 theme.css 那段全局 CSS 管,见那个文件的头注释)。
 */
import { motion } from 'motion/react'
import { useEffect, useState, type ReactNode } from 'react'
import { motionScale, useMotionLevel } from '../../theme/useMotionLevel'

interface Box {
  top: number
  height: number
  /**
   * 这一块是**刚从隐藏变可见**的:指针从树外进来时要直接落在那一行,
   * 而不是从上一次离开的位置一路滑过来(那一下看着像底色在追人)。
   */
  fresh: boolean
}

export function TreeHoverGlide({ container }: {
  /** 树容器;行在它里面,`offsetTop` 相对它计算 */
  container: HTMLElement | null
}): ReactNode {
  const scale = motionScale(useMotionLevel())
  const [box, setBox] = useState<Box | null>(null)

  useEffect(() => {
    if (container === null) return
    const onOver = (event: PointerEvent): void => {
      const row = event.target instanceof Element ? event.target.closest('[role="treeitem"]') : null
      if (!(row instanceof HTMLElement) || !container.contains(row)) return
      const top = row.offsetTop
      const height = row.offsetHeight
      // 同一行里挪指针会一直触发 pointerover(子元素之间),位置没变就别 setState
      setBox((current) => current !== null && current.top === top && current.height === height
        ? current
        : { top, height, fresh: current === null })
    }
    const onLeave = (): void => setBox(null)
    container.addEventListener('pointerover', onOver)
    container.addEventListener('pointerleave', onLeave)
    return () => {
      container.removeEventListener('pointerover', onOver)
      container.removeEventListener('pointerleave', onLeave)
    }
  }, [container])

  return (
    <motion.div
      aria-hidden
      initial={false}
      animate={box === null ? { opacity: 0 } : { opacity: 1, y: box.top, height: box.height }}
      transition={
        // ★ 归零档必须是普通 `duration: 0`:弹簧配 0 时长会退化成永不收敛的抖动(同 `Segmented`)
        scale === 0
          ? { duration: 0 }
          : {
              // 不回弹:行高只有 26px,一点过冲就会压到相邻那一行,看着像悬停错了行
              y: box?.fresh === true ? { duration: 0 } : { type: 'spring', bounce: 0, duration: 0.24 * scale },
              height: { duration: 0 },
              opacity: { duration: 0.12 * scale }
            }
      }
      className="pointer-events-none absolute inset-x-0 top-0 rounded-[8px] bg-tint-hover"
    />
  )
}
