/**
 * 一颗图标按钮,外加「操作完短暂亮一下就复位」的那对状态。
 *
 * ★ 抽出来是因为复制、导出这类操作**没有任何持久后果可看** —— 反馈全靠按钮
 * 自己变一下,而「变多久」必须处处一致,否则同一个复制动作在回合操作条和
 * 计划预览头部看起来会像两件不同的事。
 *
 * ★ **这一颗刻意没有换成 Arc 的 Button。** 它挂在每一回合的操作条上,长对话里有几百颗;
 *   Arc Button 每个实例都带 Motion 的按压动画、标签变形和一个 ResizeObserver。实测
 *   `thread-isolation.test.ts`(200 回合)从 5.5s 涨到 7.3s,全量测试在负载下直接超时。
 */
import { useEffect, useState, type ReactNode } from 'react'
import { cn } from '../../lib/cn'

/** 反馈留多久。与代码块的「已复制」同一个数。 */
const FLASH_MS = 2200

/** `failed` 不是错误处理,只是「这一下没成」的同款短反馈。 */
export type TransientStatus = 'idle' | 'done' | 'failed'

/** 置成非 idle 之后自己回落。返回的 setter 是稳定的,可以直接进依赖数组。 */
export function useTransientStatus(resetMs: number = FLASH_MS): [
  TransientStatus,
  (next: TransientStatus) => void
] {
  const [status, setStatus] = useState<TransientStatus>('idle')
  useEffect(() => {
    if (status === 'idle') return
    const timer = setTimeout(() => setStatus('idle'), resetMs)
    return () => clearTimeout(timer)
  }, [status, resetMs])
  return [status, setStatus]
}

export function ActionIconButton({
  children,
  label,
  text,
  tone = 'plain',
  testId,
  disabled = false,
  onClick
}: {
  children: ReactNode
  label: string
  /** 给出文案就渲染成「图标 + 字」—— 确认态靠它把代价说出来。 */
  text?: string
  tone?: 'plain' | 'warn' | 'danger'
  testId: string
  disabled?: boolean
  onClick: () => void
}): ReactNode {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      data-testid={testId}
      disabled={disabled}
      onClick={onClick}
      className={cn(
        'flex items-center gap-1 rounded-[6px] p-1 transition-colors hover:bg-tint-hover',
        'disabled:cursor-default disabled:opacity-40 disabled:hover:bg-transparent',
        text !== undefined && 'px-1.5',
        tone === 'danger' ? 'text-danger' : tone === 'warn' ? 'text-fg' : 'hover:text-fg-muted'
      )}
    >
      {children}
      {text !== undefined && <span className="text-[11px]">{text}</span>}
    </button>
  )
}
