import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'
import { useDraft } from './useDraft'

/**
 * 多行文本框。**和 `TextInput` 相反,草稿态在里面** —— 那边故意不含草稿,
 * 因为它的两类调用点(逐键过滤的搜索框 / 提交才落库的代理地址)要的东西正相反;
 * 而多行框至今没有一个「逐键」的调用点:它承的是白名单、工作描述、全局提示词
 * 这类整段写完才有意义的东西。理由展开在 `useDraft` 里。
 *
 * `bg-surface-field` / `.selectable` 与 `TextInput` 同源,见那边的注释。
 */
export function TextArea({
  value,
  onCommit,
  rows = 4,
  placeholder,
  maxLength,
  disabled = false,
  ariaLabel,
  className
}: {
  value: string
  /** 失焦时调用,且只在草稿确实变了时调 */
  onCommit: (v: string) => void
  rows?: number
  placeholder?: string
  /**
   * 硬闸门在落库那侧(`shared/domain/settings.ts` 的 `PERSONALIZATION_MAX`)。
   * 这里给的是**手感**:让粘贴一大段进来的人当场看到被截,而不是提交之后
   * 才发现存进去的和框里看到的不一样。
   */
  maxLength?: number
  disabled?: boolean
  ariaLabel: string
  className?: string
}): ReactNode {
  const { draft, setDraft, onFocus, commit } = useDraft(value)

  return (
    <textarea
      value={draft}
      rows={rows}
      disabled={disabled}
      maxLength={maxLength}
      aria-label={ariaLabel}
      placeholder={placeholder}
      spellCheck={false}
      onFocus={onFocus}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => commit(onCommit)}
      className={cn(
        'apple-field app-no-drag selectable w-full resize-none rounded-[8px] border border-border',
        'bg-surface-field px-2.5 py-2 text-[13px] leading-[1.6] text-fg outline-none',
        'transition-colors placeholder:text-fg-faint focus:border-fg-faint disabled:opacity-40',
        className
      )}
    />
  )
}
