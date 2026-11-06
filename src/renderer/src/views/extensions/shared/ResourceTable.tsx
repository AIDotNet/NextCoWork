/**
 * 四个 Tab 共用的资源列表。
 *
 * ★ 做成泛型而不是各写一份:命令、子代理、钩子的列表长得一样(名字 / 描述 /
 *   作用域 / 开关 / 操作),差别只在「行从哪来」和「点开之后是什么」。
 *   复制三份的下场是某一份修了空态而另外两份没修 —— 这类不一致不会报错,
 *   只会让人觉得「这个界面有点怪」。
 */
import { Lock } from 'lucide-react'
import type { ReactNode } from 'react'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { Switch } from '../../../components/arc/switch/switch'
import { cn } from '../../../lib/cn'
import { useI18n } from '../../../i18n'

export interface ResourceRow {
  name: string
  description: string
  /** `builtin` 的行只读 —— 它没有源文件，改不了也删不了。 */
  scope: string
  enabled: boolean
  /** 名字后面那截灰字，比如命令的 argument-hint。 */
  suffix?: string
  /**
   * 名字前面那个小圆点的**颜色值**（子代理的 `color:`）。
   *
   * ★ 收的是算好的 CSS 颜色,不是色名:这张表四个 Tab 共用,让它认识
   *   「子代理的八个色名」等于把一个 kind 专属的概念塞进公共组件。
   */
  color?: string
}

/** 全局 / 本工作区 / 内置 的小徽章。 */
function ScopeBadge({ scope }: { scope: string }): ReactNode {
  const { t } = useI18n()
  const label =
    scope === 'project' ? t('ext.scope.project') : scope === 'builtin' ? t('ext.scope.builtin') : t('ext.scope.global')
  return (
    <span className="shrink-0 rounded-pill bg-tint px-2 py-0.5 text-[10px] text-fg-muted">{label}</span>
  )
}

export function ResourceTable<T extends ResourceRow>({
  rows,
  onOpen,
  onToggle,
  emptyTitle,
  emptyHint,
  icon,
  openReadOnly = false,
  hideToggle = false
}: {
  rows: readonly T[]
  onOpen: (row: T) => void
  onToggle: (row: T, enabled: boolean) => void
  emptyTitle: string
  emptyHint: string
  icon: ReactNode
  openReadOnly?: boolean
  hideToggle?: boolean
}): ReactNode {
  const { t } = useI18n()

  if (rows.length === 0) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center">
        <EmptyState icon={icon} title={emptyTitle} description={emptyHint} />
      </div>
    )
  }

  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-4 pb-4">
      <ul className="flex flex-col gap-1">
        {rows.map((row) => {
          const readOnly = row.scope === 'builtin'
          return (
            <li key={`${row.scope}:${row.name}`} className="flex items-center gap-3 rounded-lg px-3 py-2 hover:bg-tint">
              <button
                type="button"
                disabled={readOnly && !openReadOnly}
                onClick={() => onOpen(row)}
                className={cn('min-w-0 flex-1 text-left', readOnly && !openReadOnly ? 'cursor-default' : 'cursor-pointer')}
              >
                <span className="flex items-center gap-1.5">
                  {row.color !== undefined && (
                    <span
                      aria-hidden
                      style={{ backgroundColor: row.color }}
                      className="size-2 shrink-0 rounded-full"
                    />
                  )}
                  <span className={cn('truncate text-[13px]', row.enabled ? 'text-fg' : 'text-fg-faint')}>
                    {row.name}
                  </span>
                  {row.suffix !== undefined && (
                    <span className="shrink-0 text-[11px] text-fg-faint">{row.suffix}</span>
                  )}
                  {readOnly && <Lock size={11} className="shrink-0 text-fg-faint" />}
                </span>
                <span className="mt-0.5 block truncate text-[11px] text-fg-faint">{row.description}</span>
              </button>
              <ScopeBadge scope={row.scope} />
              {!hideToggle && <Switch
                checked={row.enabled}
                disabled={readOnly}
                onCheckedChange={(v) => onToggle(row, v)}
                aria-label={t('ext.toggleLabel', { name: row.name })}
              />}
            </li>
          )
        })}
      </ul>
    </div>
  )
}
