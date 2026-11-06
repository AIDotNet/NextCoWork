/**
 * 全局搜索 / 命令面板 —— 点侧边栏「搜索」打开,目前只覆盖会话全文搜索
 * (`conversations:searchAll`,后端早就现成,只是从没接过 UI)。
 *
 * 四条约束抄自 Dialog.tsx,而不是复用 Dialog 组件本身 —— 命令面板没有
 * title/footer,输入框本身就是唯一的「头部」,套 Dialog 现成的标题栏布局
 * 反而要挖空迁就:
 * 1. 根节点 `app-no-drag`
 * 2. `z-100`
 * 3. Esc 先看 `defaultPrevented`,并 `stopPropagation`(捕获阶段,抢在
 *    设置浮层那个 document 级监听前面)
 * 4. 点遮罩关闭绑 `click` 不绑 `pointerdown`
 *
 * 内容用 cmdk 的无头组件(Command/Input/List/Group/Item),不用 cmdk 自带的
 * `Command.Dialog`——那是 Radix Dialog 包出来的,自己会插一遍 portal / 焦点
 * 陷阱 / Esc 处理,和上面这四条是两套逻辑,在这个自绘窗口铬(app-drag、
 * WindowControls 悬浮层……)上容易因为两边都想接管而打架。
 */
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandLoading } from 'cmdk'
import { MessageSquare, Search } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import type { SearchHit } from '../../../shared/domain/session'
import { searchAll } from '../services/sessions'
import { cn } from '../lib/cn'
import { usePresence } from '../lib/usePresence'
import { useFocusTrap } from '../components/ui/useFocusTrap'
import { useI18n, type TranslationKey } from '../i18n'
import { prettyAccelerator } from '../lib/accelerator'
import { MENU_ICON } from './icons'
import type { Command as AppCommand } from './commands'

const PALETTE_MS = 220
const DEBOUNCE_MS = 200

/**
 * FTS5 `snippet()` 只用字面量 `<mark>`/`</mark>` 当分隔符,其余文本原样透传 ——
 * 直接当 HTML 渲染就是把用户自己的消息内容(可能含 `<img onerror=...>` 这类
 * 文本)当代码执行。这里手工按分隔符切开,非标记部分一律当纯文本子节点,
 * 不经过任何 HTML 解析。
 */
function renderSnippet(snippet: string): ReactNode[] {
  const parts = snippet.split(/(<mark>|<\/mark>)/g)
  const nodes: ReactNode[] = []
  let marking = false
  parts.forEach((chunk, i) => {
    if (chunk === '<mark>') {
      marking = true
      return
    }
    if (chunk === '</mark>') {
      marking = false
      return
    }
    if (chunk === '') return
    nodes.push(
      marking ? (
        <mark key={i} className="rounded-[3px] bg-accent/25 text-fg not-italic">
          {chunk}
        </mark>
      ) : (
        <span key={i}>{chunk}</span>
      )
    )
  })
  return nodes
}

export function SearchPalette({
  open,
  onClose,
  workspaceId,
  commands = [],
  onSelectSession
}: {
  open: boolean
  onClose: () => void
  /** 只在当前工作区内搜 —— 和 Sidebar 的 `onSelectSession` 假设的作用域一致 */
  workspaceId: string | null
  /**
   * 命令(内置 + 插件),见 `shell/commands.ts`。
   *
   * ★ 这一栏在**没有工作区、没有输入**的时候也显示 —— 会话搜索需要一个
   * 工作区,命令不需要。原来那两个空态分支会把整个面板变成一句「先打开一个
   * 工作区」,而命令面板在那一刻恰恰是用户最可能想用的东西(比如打开设置)。
   */
  commands?: readonly AppCommand[]
  onSelectSession: (sessionId: string) => void
}): ReactNode {
  const { t } = useI18n()
  const panelRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const presence = usePresence(open, PALETTE_MS, true)
  useFocusTrap(panelRef, open && presence.mounted, inputRef)

  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<SearchHit[]>([])
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!open) return
    setQuery('')
    setHits([])
    setLoading(false)
  }, [open])

  useEffect(() => {
    const trimmed = query.trim()
    if (!open || workspaceId === null || trimmed === '') {
      setHits([])
      setLoading(false)
      return
    }
    let alive = true
    setLoading(true)
    const timer = setTimeout(() => {
      void searchAll(trimmed, workspaceId, 20)
        .then((results) => {
          if (!alive) return
          setHits(results)
          setLoading(false)
        })
        .catch((err: unknown) => {
          console.error('[search] 全局搜索失败', err)
          if (!alive) return
          setHits([])
          setLoading(false)
        })
    }, DEBOUNCE_MS)
    return () => {
      alive = false
      clearTimeout(timer)
    }
  }, [query, workspaceId, open])

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent): void => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      e.preventDefault()
      e.stopPropagation()
      onClose()
    }
    document.addEventListener('keydown', onKey, true)
    return () => document.removeEventListener('keydown', onKey, true)
  }, [open, onClose])

  if (!presence.mounted) return null

  const visible = presence.shown
  const select = (hit: SearchHit): void => {
    onSelectSession(hit.sessionId)
    onClose()
  }

  /*
    ★ 命令按标题**在这里过滤**,不交给 cmdk:面板整体是 `shouldFilter={false}`
    (会话那一栏由后端 FTS 排序,不能让前端再筛一遍)。两栏用两套筛选规则
    是对的 —— 它们的数据来源根本不同。
  */
  const needle = query.trim().toLowerCase()
  const matchedCommands = commands.filter(
    (command) => needle === '' || t(command.titleKey as TranslationKey).toLowerCase().includes(needle)
  )

  return createPortal(
    <div
      className={cn(
        'app-no-drag fixed inset-0 z-100 flex justify-center px-[10px] pt-[12vh]',
        'transition-opacity duration-220 ease-panel motion-reduce:transition-none',
        visible ? 'opacity-100' : 'pointer-events-none opacity-0'
      )}
      role="dialog"
      aria-modal="true"
      aria-label={t('search.title')}
    >
      <div className="absolute inset-0 bg-scrim/20" onClick={onClose} />

      <div
        ref={panelRef}
        style={{ width: 560 }}
        className={cn(
          'relative flex h-fit max-h-[60vh] w-full flex-col overflow-hidden',
          'apple-sheet outline-none',
          'transition-[opacity,transform,scale] duration-220 ease-panel motion-reduce:transition-none motion-reduce:transform-none motion-reduce:scale-100',
          visible ? 'scale-100 opacity-100' : 'scale-[.98] opacity-0'
        )}
      >
        <Command shouldFilter={false} loop label={t('search.title')} className="flex min-h-0 flex-1 flex-col">
          <div className="flex shrink-0 items-center gap-2.5 border-b border-border px-4 py-3">
            <Search size={16} className="shrink-0 text-icon" />
            <CommandInput
              ref={inputRef}
              value={query}
              onValueChange={setQuery}
              placeholder={t('search.placeholder')}
              className="min-w-0 flex-1 bg-transparent text-[14px] text-fg outline-none placeholder:text-fg-faint"
            />
          </div>
          <CommandList className="scroll-thin min-h-0 flex-1 overflow-y-auto p-1.5">
            {matchedCommands.length > 0 && (
              <CommandGroup
                heading={t('search.group.commands')}
                className="[&_[cmdk-group-heading]]:px-2.5 [&_[cmdk-group-heading]]:pt-1.5 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:text-[10px] [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wide [&_[cmdk-group-heading]]:text-fg-faint"
              >
                {matchedCommands.map((command) => {
                  const Icon = MENU_ICON[command.icon]
                  return (
                    <CommandItem
                      key={command.id}
                      value={command.id}
                      onSelect={() => {
                        onClose()
                        void command.run()
                      }}
                      className="flex cursor-pointer items-center gap-2.5 rounded-[8px] px-2.5 py-2 aria-selected:bg-accent/15"
                    >
                      <Icon size={15} className="shrink-0 text-icon" />
                      <span className="min-w-0 flex-1 truncate text-[13px] text-fg">
                        {t(command.titleKey as TranslationKey)}
                      </span>
                      {command.accelerator !== undefined && (
                        <span className="shrink-0 text-[11px] text-fg-faint">{prettyAccelerator(command.accelerator)}</span>
                      )}
                    </CommandItem>
                  )
                })}
              </CommandGroup>
            )}
            {workspaceId === null ? (
              <div className="px-3 py-6 text-center text-[12.5px] text-fg-faint">{t('search.noWorkspace')}</div>
            ) : query.trim() === '' ? (
              <div className="px-3 py-6 text-center text-[12.5px] text-fg-faint">{t('search.hint')}</div>
            ) : loading ? (
              <CommandLoading className="px-3 py-6 text-center text-[12.5px] text-fg-faint">
                {t('search.loading')}
              </CommandLoading>
            ) : hits.length === 0 ? (
              <CommandEmpty className="px-3 py-6 text-center text-[12.5px] text-fg-faint">
                {t('search.empty')}
              </CommandEmpty>
            ) : (
              <CommandGroup
                heading={t('search.group.sessions')}
                className="[&_[cmdk-group-heading]]:px-2.5 [&_[cmdk-group-heading]]:pt-1.5 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:text-[10px] [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:tracking-wide [&_[cmdk-group-heading]]:text-fg-faint"
              >
                {hits.map((hit) => (
                  <CommandItem
                    key={hit.messageId}
                    value={hit.messageId}
                    onSelect={() => select(hit)}
                    className="flex cursor-pointer items-start gap-2.5 rounded-[8px] px-2.5 py-2 aria-selected:bg-accent/15"
                  >
                    <MessageSquare size={15} className="mt-0.5 shrink-0 text-icon" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[13px] text-fg">{hit.title}</span>
                      <span className="block truncate text-[12px] text-fg-faint">{renderSnippet(hit.snippet)}</span>
                    </span>
                  </CommandItem>
                ))}
              </CommandGroup>
            )}
          </CommandList>
        </Command>
      </div>
    </div>,
    document.body
  )
}
