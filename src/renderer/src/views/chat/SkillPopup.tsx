import { AlertTriangle, Search, Sparkles, TerminalSquare } from 'lucide-react'
import { useEffect, useRef, type ReactNode } from 'react'
import type { CommandDefinition } from '../../../../shared/domain/command'
import type { SkillListItem } from '../../../../shared/domain/skill'
import { cn } from '../../lib/cn'

/**
 * `/` 弹层里的一行。命令和 Skill 在**同一个数组**里,因为方向键要在两者之间
 * 连续地走 —— 拆成两个列表各记一个 active 下标的话,「按 ↓ 从最后一条命令
 * 走到第一条 Skill」这件事就得在调用方手写一遍。
 */
export type SlashItem =
  | { kind: 'command'; key: string; command: CommandDefinition; description: string }
  | { kind: 'skill'; key: string; skill: SkillListItem }

export function SkillPopup({ id, items, active, loading, error, search, onSearch, onPick, onHover, onKeyDown, labels }: {
  id: string; items: SlashItem[]; active: number; loading: boolean; error: boolean; search?: string
  onSearch: (value: string) => void; onPick: (item: SlashItem) => void; onHover: (index: number) => void
  onKeyDown: (event: React.KeyboardEvent<HTMLElement>) => boolean
  labels: { search: string; loading: string; error: string; empty: string; commands: string; skills: string }
}): ReactNode {
  const searchRef = useRef<HTMLInputElement>(null)
  useEffect(() => { if (search !== undefined) searchRef.current?.focus() }, [search])
  return (
    <div className="absolute bottom-full left-2 z-20 mb-2 w-[min(380px,calc(100%-1rem))] overflow-hidden rounded-panel border-0 bg-surface-menu p-1.5 shadow-menu" onKeyDown={onKeyDown}>
      {search !== undefined && <div className="mb-1 flex items-center gap-2 px-2 py-1.5"><Search size={14} className="text-fg-faint" /><input ref={searchRef} value={search} onChange={(e) => onSearch(e.target.value)} placeholder={labels.search} aria-label={labels.search} className="min-w-0 flex-1 bg-transparent text-[12px] text-fg outline-none" /></div>}
      <div id={id} role="listbox" className="max-h-64 overflow-y-auto py-1">
        {loading ? <div className="px-3 py-4 text-center text-[12px] text-fg-faint">{labels.loading}</div> : error ? <div className="flex items-center gap-2 px-3 py-4 text-[12px] text-danger"><AlertTriangle size={14} />{labels.error}</div> : items.length === 0 ? <div className="px-3 py-4 text-center text-[12px] text-fg-faint">{labels.empty}</div> : items.map((item, i) => (
          <div key={item.key}>
            {/* 分组标题只画在「本节第一条」上 —— 两节都可能为空,写死两个标题会留下空标题 */}
            {(i === 0 || items[i - 1]?.kind !== item.kind) && (
              <div className="px-2 pt-1.5 pb-1 text-[10px] uppercase tracking-wide text-fg-faint">
                {item.kind === 'command' ? labels.commands : labels.skills}
              </div>
            )}
            <button id={`${id}-${i}`} type="button" role="option" aria-selected={i === active} className={cn('flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left', i === active && 'bg-tint')} onMouseDown={(e) => e.preventDefault()} onMouseEnter={() => onHover(i)} onClick={() => onPick(item)}>
              {item.kind === 'command'
                ? <TerminalSquare size={14} className="mt-0.5 shrink-0 text-accent" />
                : <Sparkles size={14} className="mt-0.5 shrink-0 text-accent" />}
              <span className="min-w-0">
                <span className="block truncate text-[12px] text-fg">
                  {item.kind === 'command' ? `/${item.command.name}` : item.skill.name}
                  {item.kind === 'command' && item.command.argumentHint !== undefined && (
                    <span className="ml-1 text-fg-faint">{item.command.argumentHint}</span>
                  )}
                </span>
                <span className="block truncate text-[11px] text-fg-faint">
                  {item.kind === 'command' ? item.description : item.skill.description}
                </span>
              </span>
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}
