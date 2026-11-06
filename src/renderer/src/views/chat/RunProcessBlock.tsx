import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { SubagentState, ToolCallState } from '../../../../shared/agent/transcript'
import { formatDuration } from '../../../../shared/agent/duration'
import type { TimelineItem } from '../../../../shared/domain/tool-timeline'
import { cn } from '../../lib/cn'
import { useI18n } from '../../i18n'
import { SurfaceReveal } from '../../components/ui/Surface'
import { ToolTimeline } from './ToolTimeline'
import { ROW_CLASS, RowChevron } from './row'
import { useFoldAnchor } from './useFoldAnchor'

/**
 * The compact summary for a completed agent turn. The answer is rendered by
 * the caller after this block, so opening the row is always an explicit choice
 * to inspect the preceding thinking and tool activity.
 *
 * Expand/collapse goes through `SurfaceReveal` (0.22s on `--ease-panel`) — the
 * old `{open && …}` conditional mount had no transition at all: expanding dropped
 * the whole list in one frame and collapsing deleted it, which read as a rendering
 * glitch next to every other reveal in the transcript. Unmount timing is unchanged
 * (content is removed only after the exit finishes), so group-expand choices lose
 * nothing they did not already lose before.
 */
export function RunProcessBlock({
  items,
  tools,
  subagents,
  durationMs,
  defaultOpen = false,
  children,
  entering = false,
  ref
}: {
  items: readonly TimelineItem[]
  tools: Readonly<Record<string, ToolCallState>>
  subagents?: Readonly<Record<string, SubagentState>>
  durationMs?: number
  defaultOpen?: boolean
  /** The complete content before the final answer, including explanatory prose. */
  children?: ReactNode
  /**
   * True on the frame where the run-end collapse **swapped this row in** — fades
   * the row in over 180ms so the swap reads as "folded into this row" instead of
   * the whole process area teleporting into a title line. History load never
   * passes it, so the first frame there lands already opaque (same reasoning as
   * `AnimatePresence initial={false}` in ToolTimeline).
   */
  entering?: boolean
  /**
   * Fold-anchor hook: the caller (Thread) keeps this node under observation as
   * the top of the collapsing region — see `useFoldAnchor`. React 19: `ref` is a
   * plain prop, merged with our own below.
   */
  ref?: React.Ref<HTMLDivElement>
}): ReactNode {
  const { t } = useI18n()
  const [open, setOpen] = useState(defaultOpen)
  /*
    需求:手动收起这行时,下面的结论正文不许跟着往上跳。
    不满足会怎样:几百像素的高度一次归零,视口相对内容上蹿 —— 表现为
    「点一下收起,答案冷不防窜了半屏」。折叠点就是这行自己的顶端,
    所以 foldTop 不用另传,`useFoldAnchor` 默认就取 anchor 自己。
  */
  const anchorRef = useRef<HTMLDivElement>(null)
  const setRoot = (node: HTMLDivElement | null): void => {
    anchorRef.current = node
    if (typeof ref === 'function') ref(node)
    else if (ref !== null && ref !== undefined) (ref as React.RefObject<HTMLDivElement | null>).current = node
  }
  useFoldAnchor(anchorRef, !open)

  useEffect(() => {
    setOpen(defaultOpen)
  }, [defaultOpen])

  const label = durationMs === undefined
    ? t('chat.run.process')
    : t('chat.run.elapsed', { duration: formatDuration(durationMs) })

  return (
    <div
      ref={setRoot}
      data-testid="run-process-block"
      data-open={open}
      className={cn('flex flex-col', entering && 'run-fold-enter')}
    >
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className={cn(ROW_CLASS, 'text-[13px] text-fg-faint')}
      >
        <span className="min-w-0 truncate">{label}</span>
        <RowChevron open={open} />
      </button>

      <SurfaceReveal open={open}>
        <div className="scroll-thin flex max-h-[min(60vh,560px)] flex-col gap-0.5 overflow-y-auto pr-1">
          {children ?? <ToolTimeline items={items} tools={tools} subagents={subagents} />}
        </div>
      </SurfaceReveal>
    </div>
  )
}
