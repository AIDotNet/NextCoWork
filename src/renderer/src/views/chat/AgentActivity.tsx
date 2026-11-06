/**
 * Agent 运行中的微型状态原语：像素波负责传达“仍在工作”，微光文字负责传达阶段。
 *
 * 需求：思考块和底部状态行必须使用同一套活动反馈；这里只拥有视觉，不计时、
 * 不推导运行状态，也不替代各调用方已有的读屏文案。
 * （曾按需求把像素波全删、只留文字微光；但状态行的趣味词是几字即逝的短词，
 * 纯微光扫过看不出“在动”，所以那里把像素波加了回来 —— 思考块和工具状态
 * 仍然只留文字微光。`agent-activity-cell` 样式因此保留。）
 */
import { motion } from 'motion/react'
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'
import { useMotionLevel } from '../../theme/useMotionLevel'

const CELL_DELAYS = Array.from({ length: 9 }, (_, index) => {
  const row = Math.floor(index / 3)
  const column = index % 3
  return (column + Math.abs(row - 1)) * 90
})

export function AgentActivityGrid({ className }: { className?: string }): ReactNode {
  const level = useMotionLevel()
  const wave = level === 'standard' || level === 'soft'
  const gridClassName = cn('grid shrink-0 grid-cols-[repeat(3,3px)] gap-px', className)
  const cells = CELL_DELAYS.map((delay, index) => (
    <span
      key={index}
      className={cn(
        'size-[3px] rounded-[1px] bg-current',
        wave ? 'agent-activity-cell' : 'opacity-70'
      )}
      style={wave ? {
        animationDelay: `${delay}ms`,
        ...(level === 'soft' ? { animationDuration: '900ms' } : {})
      } : undefined}
    />
  ))

  // 需求：减弱动效仍要表达“活着”，但 CSS 动画会被主题层的 !important 压成 0；
  // 因此和 Spinner 一样用 Motion 做无位移的呼吸，关闭动效时则保持清晰静止。
  if (level === 'reduced') {
    return (
      <motion.span
        aria-hidden
        className={gridClassName}
        animate={{ opacity: [1, 0.45, 1] }}
        transition={{ duration: 1.6, repeat: Infinity, ease: 'easeInOut' }}
      >
        {cells}
      </motion.span>
    )
  }

  return <span aria-hidden className={gridClassName}>{cells}</span>
}

export function AgentShimmerText({
  children,
  className
}: {
  children: ReactNode
  className?: string
}): ReactNode {
  const level = useMotionLevel()
  // 系统和应用任一方要求减弱动效时都退成普通文字，不能把 0.01ms 的 CSS 动画
  // 留在透明文字上高速循环；症状会是状态文案高频抖动而不是静止。
  return (
    <span className={cn(level === 'standard' || level === 'soft' ? 'agent-shimmer-text' : undefined, className)}>
      {children}
    </span>
  )
}
