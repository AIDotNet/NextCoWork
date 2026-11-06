/**
 * `@` 打开的文件选择列表。
 *
 * ★ **键盘由 `Composer` 处理,不在这里**。上下键和 Enter 必须在 `<textarea>` 的
 * `onKeyDown` 里就被截住 —— 焦点始终在输入框上(移走焦点会中断输入法组词),
 * 所以这个组件收不到那些按键。它只负责画,以及鼠标那条路。
 *
 * ★ **不复用 `ui/Menu`**:那个是「点触发器 → 弹面板 → 焦点进面板」的下拉菜单,
 * 而这里的焦点必须留在输入框里,选中项是靠 `aria-activedescendant` 表达的。
 * 两套交互模型,共用一个组件只会让两边都别扭。
 */
import { useEffect, useRef, type ReactNode } from 'react'
import type { FileSuggestion } from '../../../../shared/domain/file-tree'
import { useI18n } from '../../i18n'
import { iconFor } from '../../lib/file-icon'
import { cn } from '../../lib/cn'

export function MentionPopup({
  id,
  items,
  active,
  loading,
  onPick,
  onHover
}: {
  /** 给 `<textarea>` 的 `aria-controls` / `aria-activedescendant` 用 */
  id: string
  items: readonly FileSuggestion[]
  active: number
  loading: boolean
  onPick: (item: FileSuggestion) => void
  onHover: (index: number) => void
}): ReactNode {
  const { t } = useI18n()
  const listRef = useRef<HTMLUListElement>(null)

  /*
    选中项滚进可视区。★ `block: 'nearest'` —— 用默认的 `'start'` 会在
    按住下键时把列表整个甩到底,而用户只是想往下挪一行。
  */
  useEffect(() => {
    const el = listRef.current?.querySelector(`[data-index="${String(active)}"]`)
    el?.scrollIntoView({ block: 'nearest' })
  }, [active])

  return (
    <div
      data-testid="mention-popup"
      className="absolute bottom-full left-0 z-30 mb-2 w-[min(420px,100%)] overflow-hidden rounded-panel border-0 bg-surface-menu shadow-menu"
    >
      {items.length === 0 ? (
        <div className="px-3 py-2.5 text-[12px] text-fg-faint">
          {loading ? t('common.loading') : t('mention.empty')}
        </div>
      ) : (
        <ul ref={listRef} id={id} role="listbox" className="max-h-[260px] overflow-y-auto py-1">
          {items.map((item, i) => {
            const { Icon, className } = iconFor(item.name, 'file')
            // 目录部分单独显示 —— 同名文件在一个仓库里很常见,只给文件名分不出来
            const dir = item.path.slice(0, Math.max(item.path.lastIndexOf('/'), 0))
            return (
              <li key={item.path}>
                <button
                  type="button"
                  id={`${id}-${String(i)}`}
                  data-index={i}
                  role="option"
                  aria-selected={i === active}
                  /*
                    ★ `onMouseDown` + `preventDefault`,不是 `onClick`:
                    点击会先让 `<textarea>` 失焦,而失焦的那一瞬间弹层就该关掉了 ——
                    于是 click 永远等不到。preventDefault 把失焦整个挡住,
                    焦点和光标位置都原地不动。
                  */
                  onMouseDown={(e) => {
                    e.preventDefault()
                    onPick(item)
                  }}
                  onMouseEnter={() => onHover(i)}
                  className={cn(
                    'flex w-full items-center gap-2 px-2.5 py-1.5 text-left',
                    i === active && 'bg-tint'
                  )}
                >
                  <Icon size={14} className={cn('shrink-0', className)} />
                  <span className="shrink-0 text-[12.5px] text-fg">{item.name}</span>
                  {dir !== '' && (
                    <span className="min-w-0 flex-1 truncate text-right text-[11px] text-fg-faint">
                      {dir}
                    </span>
                  )}
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
