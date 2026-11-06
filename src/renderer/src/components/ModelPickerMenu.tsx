/**
 * 模型选择弹层 —— 顶部一排供应商图标页签,搜索框,当前页签的模型列表,
 * 底部可选的「推理强度 / 上下文窗口」等二级设置。
 *
 * 输入框的模型药丸、设置页「通用 → Agent」的默认模型 / 默认子代理 / 压缩模型、
 * 图片模型、视频模型共用这一个。数据统一来自 `providerModelGroups`
 * (`shared/domain/model-groups.ts`),调用方只决定模态过滤和要不要底部设置。
 *
 * ## 为什么是 Popover 而不是之前的 DropdownMenu 两级菜单
 * 面板里有一个搜索框。DropdownMenu 的 typeahead 会吃掉字母键、Item 会抢焦点,
 * 和文本输入天然冲突。这里焦点**始终留在搜索框**,↑↓ / Enter / ⌘1–9 由搜索框
 * 的 keydown 驱动高亮行 —— 和命令面板同一套手感。
 *
 * ## 交互
 * - 页签:★ 收藏 | 每家可选的供应商。打开时落在当前模型所在的那一家。
 * - 有搜索词时跨所有供应商搜,每行右侧标出供应商名。
 * - ⌘1–⌘9(非 mac 为 Ctrl)直接选当前列表的第 N 个。
 * - ←/→(搜索框为空时)切页签。
 * - 每行的 ☆ 切换收藏,不关闭面板。
 * - 底部设置行:悬停或点击在右侧弹出选项(嵌套 Popover,Radix 的 DismissableLayer
 *   会把它当作父层的分支,点里面不会关掉主面板)。
 *
 * ★ `modal={false}`:菜单开着时页面照常能滚动(同之前的实现)。
 * ★ 设置浮层里的菜单要夹在内容列里(`data-menu-bounds`,见 `ui/Menu.tsx`)。
 */
import { Check, ChevronRight, Search, Star } from 'lucide-react'
import { Popover } from 'radix-ui'
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import type { ModelAlias, UpstreamProvider } from '../../../shared/domain/provider'
import {
  matchesModelQuery,
  modelDisplayName,
  type ProviderModelGroup
} from '../../../shared/domain/model-groups'
import { modelSelectionKey } from '../../../shared/domain/model-selection'
import { useI18n } from '../i18n'
import { cn } from '../lib/cn'
import { IS_MAC } from '../lib/platform'
import { useModelFavorites } from '../stores/model-favorites'
import { ProviderIcon } from './brand/ProviderIcon'

/** 底部的一行二级设置(推理强度 / 上下文窗口 …) */
export interface ModelPickerSetting {
  id: string
  label: string
  /** 行右侧显示的当前值 */
  value: string
  disabled?: boolean
  /** 禁用时的原因,作为 title 显示 */
  hint?: string
  options: readonly { value: string; label: string; note?: string; selected: boolean }[]
  onSelect: (value: string) => void
}

const FAVORITES = '__favorites__'

const PANEL = cn(
  'app-no-drag rounded-panel border-0 bg-surface-menu p-1.5 shadow-menu outline-none',
  'transition-[opacity,scale] duration-150 ease-panel starting:scale-[.98] starting:opacity-0',
  'motion-reduce:transition-none'
)

const ROW = cn(
  'flex w-full cursor-default select-none items-center gap-2 rounded-[8px] px-2.5 py-[7px]',
  'text-left text-[13px] text-fg outline-none'
)

interface Entry {
  model: ModelAlias
  provider: UpstreamProvider
  key: string
}

export function ModelPickerMenu({
  trigger,
  triggerClassName,
  className,
  ariaLabel,
  align = 'start',
  width = 320,
  loaded = true,
  emptyLabel,
  groups,
  value,
  topItem,
  settings,
  onSelect,
  onOpenChange
}: {
  trigger: ReactNode
  triggerClassName?: string
  /** 外层包裹 `div` 的类名;设置页的行内下拉要显式传 `'w-full'` */
  className?: string
  ariaLabel: string
  align?: 'start' | 'end'
  width?: number
  loaded?: boolean
  /** 一家可选的供应商都没有时的提示 */
  emptyLabel?: string
  /** 来自 `providerModelGroups`,保序 */
  groups: readonly ProviderModelGroup[]
  /** 当前选中的 `(供应商, 别名)`;`alias === ''` = 没选 */
  value: { providerId: string; alias: string }
  /** 列表最前面一项(比如「跟随对话」),只在无搜索词时显示 */
  topItem?: { label: string; selected: boolean; onSelect: () => void }
  settings?: readonly ModelPickerSetting[]
  onSelect: (providerId: string, alias: string) => void
  onOpenChange?: (open: boolean) => void
}): ReactNode {
  const { t } = useI18n()
  const triggerRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const [open, setOpen] = useState(false)
  const [bounds, setBounds] = useState<Element | null>(null)
  const [tab, setTab] = useState<string>('')
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const favorites = useModelFavorites((s) => s.keys)
  const toggleFavorite = useModelFavorites((s) => s.toggle)

  const tabs = useMemo(() => groups.map((g) => g.provider), [groups])
  const currentTab = tab === FAVORITES || tabs.some((p) => p.id === tab) ? tab : (tabs[0]?.id ?? FAVORITES)

  const entries: Entry[] = useMemo(() => {
    const all = (gs: readonly ProviderModelGroup[]): Entry[] =>
      gs.flatMap((g) => g.models.map((model) => ({
        model,
        provider: g.provider,
        key: modelSelectionKey(g.provider.id, model.alias)
      })))
    if (query.trim() !== '') return all(groups).filter((e) => matchesModelQuery(e.model, e.provider, query))
    if (currentTab === FAVORITES) {
      const byKey = new Map(all(groups).map((e) => [e.key, e]))
      return favorites.flatMap((k) => byKey.get(k) ?? [])
    }
    return all(groups.filter((g) => g.provider.id === currentTab))
  }, [groups, query, currentTab, favorites])

  const top = query.trim() === '' ? topItem : undefined
  const showTop = top !== undefined
  // 高亮下标:topItem 占 0(若显示),其后是 entries
  const rowCount = entries.length + (showTop ? 1 : 0)
  const crossProvider = query.trim() !== '' || currentTab === FAVORITES
  const selectedKey = value.alias === '' ? '' : modelSelectionKey(value.providerId, value.alias)

  const resetHighlight = (list: readonly Entry[], withTop: boolean): void => {
    const idx = list.findIndex((e) => e.key === selectedKey)
    setActive(idx >= 0 ? idx + (withTop ? 1 : 0) : withTop && top?.selected === true ? 0 : withTop ? 1 : 0)
  }

  const handleOpenChange = (next: boolean): void => {
    setOpen(next)
    if (next) {
      setBounds(triggerRef.current?.closest('[data-menu-bounds]') ?? null)
      setQuery('')
      // 打开时落在当前模型所在的那一家;没选模型时落在第一家
      const home = groups.some((g) => g.provider.id === value.providerId) ? value.providerId : (tabs[0]?.id ?? FAVORITES)
      setTab(home)
    }
    onOpenChange?.(next)
  }

  // 页签 / 搜索词变化后把高亮放回「当前选中」或第一行
  useEffect(() => {
    if (open) resetHighlight(entries, showTop)
  }, [open, currentTab, query])

  useEffect(() => {
    listRef.current?.querySelector(`[data-row="${active}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [active])

  const choose = (index: number): void => {
    if (top !== undefined && index === 0) {
      top.onSelect()
      setOpen(false)
      return
    }
    const entry = entries[index - (showTop ? 1 : 0)]
    if (entry === undefined) return
    onSelect(entry.provider.id, entry.model.alias)
    setOpen(false)
  }

  const tabOrder = [FAVORITES, ...tabs.map((p) => p.id)]
  const onKeyDown = (e: KeyboardEvent<HTMLElement>): void => {
    const mod = IS_MAC ? e.metaKey : e.ctrlKey
    if (mod && /^[1-9]$/.test(e.key)) {
      e.preventDefault()
      choose(Number(e.key) - 1 + (showTop ? 1 : 0))
      return
    }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      if (rowCount === 0) return
      setActive((a) => (a + (e.key === 'ArrowDown' ? 1 : rowCount - 1)) % rowCount)
      return
    }
    if (e.key === 'Enter') {
      e.preventDefault()
      choose(active)
      return
    }
    if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && query === '') {
      e.preventDefault()
      const i = tabOrder.indexOf(currentTab)
      const next = tabOrder[(i + (e.key === 'ArrowRight' ? 1 : tabOrder.length - 1)) % tabOrder.length]
      if (next !== undefined) setTab(next)
    }
  }

  const shortcut = (n: number): string => `${IS_MAC ? '⌘' : 'Ctrl+'}${n}`

  return (
    <div className={cn('relative flex', className ?? 'min-w-0')}>
      <Popover.Root open={open} onOpenChange={handleOpenChange} modal={false}>
        <Popover.Trigger asChild>
          <button
            ref={triggerRef}
            type="button"
            aria-label={ariaLabel}
            className={cn('app-no-drag disabled:opacity-40', triggerClassName)}
          >
            {trigger}
          </button>
        </Popover.Trigger>
        <Popover.Portal>
          <Popover.Content
            align={align}
            side="bottom"
            sideOffset={4}
            collisionPadding={8}
            collisionBoundary={bounds ?? undefined}
            style={{ width }}
            className={cn(PANEL, 'flex max-h-[min(520px,var(--radix-popover-content-available-height))] flex-col')}
            onOpenAutoFocus={(e) => {
              // 焦点直接进搜索框
              e.preventDefault()
              inputRef.current?.focus()
            }}
            onKeyDown={onKeyDown}
          >
            {!loaded ? (
              <Note>{t('common.loading')}</Note>
            ) : (
              <>
                {/* ── 页签:★ | 供应商 ── */}
                <div role="tablist" aria-label={ariaLabel} className="scroll-thin flex shrink-0 items-center gap-0.5 overflow-x-auto px-1 pb-1">
                  <TabButton
                    label={t('modelPicker.favorites')}
                    active={currentTab === FAVORITES && query === ''}
                    onClick={() => { setTab(FAVORITES); setQuery('') }}
                  >
                    <Star size={14} />
                  </TabButton>
                  {tabs.length > 0 && <span aria-hidden className="mx-1 h-4 w-px shrink-0 bg-hairline" />}
                  {groups.map((g) => (
                    <TabButton
                      key={g.provider.id}
                      label={g.available ? g.provider.name : `${g.provider.name} · ${t('modelPicker.providerUnavailable')}`}
                      active={currentTab === g.provider.id && query === ''}
                      dim={!g.available}
                      onClick={() => { setTab(g.provider.id); setQuery('') }}
                    >
                      <ProviderIcon
                        name={[g.provider.name, g.provider.id]}
                        size={15}
                        fallback={<span className="text-[11px] font-medium">{[...g.provider.name.trim()][0]?.toUpperCase() ?? '?'}</span>}
                      />
                    </TabButton>
                  ))}
                </div>

                {/* ── 搜索 ── */}
                <label className="mx-1 mb-1 flex shrink-0 items-center gap-2 rounded-[8px] px-2 py-1.5 text-fg-faint focus-within:bg-tint">
                  <Search size={14} aria-hidden className="shrink-0" />
                  <input
                    ref={inputRef}
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    placeholder={t('modelPicker.search')}
                    aria-label={t('modelPicker.search')}
                    className="min-w-0 flex-1 bg-transparent text-[13px] text-fg outline-none placeholder:text-fg-faint"
                  />
                </label>

                {/* ── 模型列表 ── */}
                <div ref={listRef} role="listbox" aria-label={ariaLabel} className="scroll-thin min-h-0 flex-1 overflow-y-auto">
                  {top !== undefined && (
                    <div
                      role="option"
                      aria-selected={top.selected}
                      data-row={0}
                      onPointerMove={() => setActive(0)}
                      onClick={() => choose(0)}
                      className={cn(ROW, active === 0 && 'bg-tint-strong')}
                    >
                      <span className="min-w-0 flex-1 truncate">{top.label}</span>
                      <Check size={14} aria-hidden className={cn('shrink-0 text-accent', !top.selected && 'invisible')} />
                    </div>
                  )}
                  {groups.length === 0 ? (
                    <Note>{emptyLabel}</Note>
                  ) : entries.length === 0 ? (
                    <Note>
                      {query.trim() !== ''
                        ? t('modelPicker.noMatch')
                        : currentTab === FAVORITES
                          ? t('modelPicker.noFavorites')
                          : t('modelPicker.providerUnavailable')}
                    </Note>
                  ) : (
                    entries.map((e, i) => {
                      const row = i + (showTop ? 1 : 0)
                      const selected = e.key === selectedKey
                      const starred = favorites.includes(e.key)
                      const name = modelDisplayName(e.model)
                      return (
                        <div
                          key={e.key}
                          role="option"
                          aria-selected={selected}
                          data-row={row}
                          title={name === e.model.alias ? e.model.alias : `${name} · ${e.model.alias}`}
                          onPointerMove={() => setActive(row)}
                          onClick={() => choose(row)}
                          className={cn(ROW, 'group/row', active === row && 'bg-tint-strong')}
                        >
                          <span className={cn('min-w-0 flex-1 truncate', selected && 'font-semibold')}>{name}</span>
                          {crossProvider && (
                            <span className="max-w-[40%] shrink-0 truncate text-[11px] text-fg-faint">{e.provider.name}</span>
                          )}
                          {selected && <Check size={13} aria-hidden className="shrink-0 text-accent" />}
                          {i < 9 && (
                            <kbd className="shrink-0 rounded-[5px] bg-tint px-1 font-sans text-[10.5px] text-fg-faint">
                              {shortcut(i + 1)}
                            </kbd>
                          )}
                          <button
                            type="button"
                            tabIndex={-1}
                            aria-label={starred ? t('modelPicker.unfavorite') : t('modelPicker.favorite')}
                            aria-pressed={starred}
                            title={starred ? t('modelPicker.unfavorite') : t('modelPicker.favorite')}
                            onClick={(ev) => { ev.stopPropagation(); toggleFavorite(e.key) }}
                            className={cn(
                              'shrink-0 rounded-[5px] p-0.5 hover:bg-tint-hover',
                              starred ? 'text-accent' : 'text-fg-faint opacity-60 group-hover/row:opacity-100'
                            )}
                          >
                            <Star size={13} fill={starred ? 'currentColor' : 'none'} />
                          </button>
                        </div>
                      )
                    })
                  )}
                </div>

                {/* ── 底部设置 ── */}
                {settings !== undefined && settings.length > 0 && (
                  <div className="mt-1 shrink-0 border-t border-hairline pt-1">
                    {settings.map((s) => <SettingRow key={s.id} setting={s} />)}
                  </div>
                )}
              </>
            )}
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
    </div>
  )
}

function TabButton({ label, active, dim = false, onClick, children }: {
  label: string
  active: boolean
  dim?: boolean
  onClick: () => void
  children: ReactNode
}): ReactNode {
  return (
    <button
      type="button"
      role="tab"
      tabIndex={-1}
      aria-selected={active}
      aria-label={label}
      title={label}
      onClick={onClick}
      className={cn(
        'relative flex size-7 shrink-0 items-center justify-center rounded-[7px] text-fg-muted transition-colors hover:bg-tint-hover',
        active && 'bg-tint-strong text-fg',
        dim && 'opacity-45'
      )}
    >
      {children}
      {active && <span aria-hidden className="absolute inset-x-2 -bottom-0.5 h-0.5 rounded-full bg-accent" />}
    </button>
  )
}

/** 底部一行设置:悬停 / 点击在右侧弹出选项 */
function SettingRow({ setting }: { setting: ModelPickerSetting }): ReactNode {
  const [open, setOpen] = useState(false)
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const cancelClose = (): void => {
    if (closeTimer.current !== null) clearTimeout(closeTimer.current)
    closeTimer.current = null
  }
  const scheduleClose = (): void => {
    cancelClose()
    closeTimer.current = setTimeout(() => setOpen(false), 150)
  }
  useEffect(() => cancelClose, [])
  const disabled = setting.disabled === true

  return (
    <Popover.Root open={open && !disabled} onOpenChange={setOpen} modal={false}>
      <Popover.Trigger asChild>
        <button
          type="button"
          disabled={disabled}
          title={disabled ? setting.hint : undefined}
          onPointerEnter={() => { cancelClose(); if (!disabled) setOpen(true) }}
          onPointerLeave={scheduleClose}
          className={cn(ROW, 'hover:bg-tint-strong disabled:opacity-40 data-[state=open]:bg-tint-strong')}
        >
          <span className="min-w-0 flex-1 truncate">{setting.label}</span>
          <span className="shrink-0 text-[12px] text-fg-faint">{setting.value}</span>
          <ChevronRight size={13} aria-hidden className="shrink-0 text-fg-faint" />
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          side="right"
          align="start"
          sideOffset={10}
          alignOffset={-6}
          collisionPadding={8}
          onOpenAutoFocus={(e) => e.preventDefault()}
          onPointerEnter={cancelClose}
          onPointerLeave={scheduleClose}
          className={cn(PANEL, 'min-w-[180px]')}
        >
          {setting.options.map((o) => (
            <button
              key={o.value}
              type="button"
              onClick={() => { setting.onSelect(o.value); setOpen(false) }}
              className={cn(ROW, 'hover:bg-tint-strong')}
            >
              <span className={cn('min-w-0 flex-1 truncate', o.selected && 'font-semibold')}>{o.label}</span>
              {o.note !== undefined && <span className="shrink-0 text-[11px] text-fg-faint">{o.note}</span>}
              <Check size={14} aria-hidden className={cn('shrink-0 text-accent', !o.selected && 'invisible')} />
            </button>
          ))}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}

function Note({ children }: { children: ReactNode }): ReactNode {
  return <div className="px-2.5 py-2 text-[12px] text-fg-faint">{children}</div>
}
