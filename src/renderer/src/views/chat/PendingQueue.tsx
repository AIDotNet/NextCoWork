/**
 * 插入消息队列 —— 输入框上方那段列表(截图 def5d081 / 23b2b6f5)。
 *
 * ## 它解决的是「排队 ≠ 只能等」
 *
 * Agent 执行期间用户继续发的消息会入队。原本队列只有一个数字(状态行的
 * `queued=2`),用户看不见自己排了什么、也无法调整顺序 —— 只能等它们按 FIFO
 * 一条条跑完。这个组件把队列摊开,并给出唯一的一个越级操作:**插话**。
 *
 * ## 三条布局硬约束(都来自截图,且都有性能/误触理由)
 *
 * 1. **行高固定 36px,任何状态都不改变它。** 文本单行截断,附件以计数后缀呈现
 *    而不是铺缩略图。队列区在输入框**上方**,任何高度变化都会把输入框往下顶,
 *    正在打字的用户会点空。
 * 2. **主操作常驻,不藏在 hover 里。** 排队场景用户就是冲「插话」来的,
 *    hover 才出现等于每次都要先找。
 * 3. **同一时刻至多一个编辑器。** `editingId` 抬升到这一层,而不是让每行
 *    自己存一个 `isEditing` —— 后者做不到「至多一个」这条不变式。
 */
import { ArrowUpToLine, CornerDownRight, MoreHorizontal, Pencil, Trash2, Undo2 } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { QueuedInput } from '../../../../shared/domain/queued-input'
import { Menu, MenuItem } from '../../components/ui/Menu'
import { cn } from '../../lib/cn'
import { useI18n } from '../../i18n'

export function PendingQueue({
  items,
  running,
  onPromote,
  onEdit,
  onDrop,
  onMoveToDraft,
  onResume,
  className
}: {
  items: QueuedInput[]
  /** 生成中 vs 已停下。决定折叠头的文案与是否给「继续执行」 */
  running: boolean
  onPromote: (id: string) => void
  onEdit: (id: string, text: string) => void
  onDrop: (id: string) => void
  onMoveToDraft: (id: string) => void
  onResume: () => void
  /**
   * 外层定位的覆盖位。默认那套(居中、760 上限、左右留白)是**它自己占一行**时的需求;
   * 与输入框上方那条清单缩成的小球共用一行时(见 ChatView 的 `composer-notices`),
   * 调用方传 `max-w-none px-0 pb-0` 抹掉 —— 同一份版式只由那一层决定,
   * 卡片本体不为「在行里」再分叉一套(与 `TaskChecklist.className` 同一个理由)。
   */
  className?: string
}): ReactNode {
  const [editingId, setEditingId] = useState<string | null>(null)
  const [collapsed, setCollapsed] = useState(false)

  // ★ 队列空了要把编辑态一起清掉,否则下次入队会莫名其妙直接展开成编辑器
  useEffect(() => {
    if (items.length === 0 && editingId !== null) setEditingId(null)
  }, [items.length, editingId])

  // ★ 空队列**整块不渲染**,不是渲染一个空框 —— 输入框要贴回原位
  if (items.length === 0) return null

  const promotedCount = items.filter((q) => q.status === 'promoted').length

  return (
    <div
      className={cn('mx-auto w-full max-w-[760px] px-6 pb-2', className)}
      data-testid="pending-queue"
      data-count={items.length}
      data-promoted={promotedCount}
    >
      <div className="overflow-hidden rounded-panel border border-stroke bg-surface/60">
        <QueueHeader
          count={items.length}
          promotedCount={promotedCount}
          running={running}
          collapsed={collapsed}
          onToggle={() => setCollapsed((c) => !c)}
          onResume={onResume}
        />

        {!collapsed && (
          <ul role="list" className="px-1 pb-1">
            {items.map((item) =>
              item.id === editingId ? (
                <li key={item.id}>
                  <PendingQueueEditor
                    initialText={item.text}
                    onCancel={() => setEditingId(null)}
                    onSave={(text) => {
                      onEdit(item.id, text)
                      setEditingId(null)
                    }}
                  />
                </li>
              ) : (
                <li key={item.id}>
                  <PendingQueueItem
                    item={item}
                    onPromote={() => onPromote(item.id)}
                    onStartEdit={() => setEditingId(item.id)}
                    onDrop={() => onDrop(item.id)}
                    onMoveToDraft={() => onMoveToDraft(item.id)}
                  />
                </li>
              )
            )}
          </ul>
        )}
      </div>
    </div>
  )
}

/**
 * 折叠头。
 *
 * ★ **非 running 时这里长出一个「继续执行」按钮** —— 中断/报错/进程重启之后
 * 队列不会自己动(那是有意的,见 store 里 `endedCleanly` 的理由),
 * 于是必须有一个地方让用户把它推下去。没有这个按钮,不自动续跑就变成了「卡死」。
 */
function QueueHeader({
  count,
  promotedCount,
  running,
  collapsed,
  onToggle,
  onResume
}: {
  count: number
  promotedCount: number
  running: boolean
  collapsed: boolean
  onToggle: () => void
  onResume: () => void
}): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex h-8 items-center gap-2 px-2.5">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={!collapsed}
        data-testid="queue-toggle"
        className="flex min-w-0 flex-1 items-center gap-1.5 text-left text-[11.5px] text-fg-faint transition-colors hover:text-fg-muted"
      >
        <ChevronIcon collapsed={collapsed} />
        {/* aria-live 只播报计数,不播报每条内容 —— 排三条消息不该念三段话 */}
        <span aria-live="polite" className="truncate">
          {t('chat.queue', { count })}
          {promotedCount > 0 && t('chat.queuePromoted', { count: promotedCount })}
        </span>
      </button>

      {!running && (
        <button
          type="button"
          onClick={onResume}
          data-testid="queue-resume"
          className="shrink-0 rounded-[6px] px-2 py-0.5 text-[11.5px] text-accent transition-colors hover:bg-accent/10"
        >
          {t('chat.continue')}
        </button>
      )}
    </div>
  )
}

function ChevronIcon({ collapsed }: { collapsed: boolean }): ReactNode {
  return (
    <svg
      width="10"
      height="10"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="3"
      className={cn('shrink-0 transition-transform', !collapsed && 'rotate-90')}
      aria-hidden
    >
      <path d="M9 18l6-6-6-6" />
    </svg>
  )
}

/**
 * 单行。**无自身 state** —— 编辑态由父级持有,这样「至多一个编辑器」
 * 是结构上成立的,而不是靠每个实例自觉。
 */
export function PendingQueueItem({
  item,
  onPromote,
  onStartEdit,
  onDrop,
  onMoveToDraft
}: {
  item: QueuedInput
  onPromote: () => void
  onStartEdit: () => void
  onDrop: () => void
  onMoveToDraft: () => void
}): ReactNode {
  const { t } = useI18n()
  const promoted = item.status === 'promoted'
  const imageCount = item.attachments.filter((a) => a.kind === 'image').length
  const fileCount = item.attachments.length - imageCount

  return (
    <div
      data-testid="queue-item"
      data-queue-id={item.id}
      data-queue-status={item.status}
      className={cn(
        // ★ h-9 = 36px 固定。promoted 用左侧竖条表达,**不加徽章** ——
        //   徽章会挤压本就只有一行的文本
        'group flex h-9 items-center gap-2 rounded-[8px] pr-1 pl-2 transition-colors hover:bg-tint-hover/40',
        promoted && 'bg-accent/8 border-l-2 border-l-accent pl-1.5'
      )}
    >
      <CornerDownRight
        size={13}
        className={cn('shrink-0', promoted ? 'text-accent' : 'text-fg-faint')}
        aria-hidden
      />

      {/* 双击进编辑 —— 与「⋯ → 编辑」同一个入口,只是更快 */}
      <button
        type="button"
        onDoubleClick={onStartEdit}
        className="min-w-0 flex-1 truncate text-left text-[12.5px] text-fg"
        title={item.text}
      >
        {item.text.trim() === '' ? <span className="text-fg-faint">{t('chat.emptyMessage')}</span> : item.text}
        {imageCount > 0 && <span className="text-fg-faint">{t('chat.images', { count: imageCount })}</span>}
        {fileCount > 0 && <span className="text-fg-faint">{t('chat.attachments', { count: fileCount })}</span>}
      </button>

      {/* ★ 常驻,不是 hover 才出现 */}
      <button
        type="button"
        onClick={onPromote}
        data-testid="queue-item-promote"
        aria-pressed={promoted}
        className={cn(
          'flex shrink-0 items-center gap-1 rounded-[6px] px-1.5 py-0.5 text-[11.5px] transition-colors',
          promoted
            ? 'text-accent hover:bg-accent/10'
            : 'text-fg-faint hover:bg-tint-hover hover:text-fg-muted'
        )}
        title={promoted ? t('chat.cancelInterrupt') : t('chat.interrupt')}
      >
        <ArrowUpToLine size={12} />
        {promoted ? t('chat.interrupted') : t('chat.interrupt')}
      </button>

      <button
        type="button"
        onClick={onDrop}
        data-testid="queue-item-drop"
        aria-label={t('common.delete')}
        className="shrink-0 rounded-[6px] p-1 text-fg-faint transition-colors hover:bg-tint-hover hover:text-danger"
      >
        <Trash2 size={13} />
      </button>

      <Menu
        width={180}
        label={t('chat.queueActions')}
        trigger={
          <span
            className="flex shrink-0 rounded-[6px] p-1 text-fg-faint transition-colors hover:bg-tint-hover hover:text-fg-muted"
            aria-label={t('common.more')}
          >
            <MoreHorizontal size={13} />
          </span>
        }
      >
        {(close) => (
          <>
            <MenuItem
              icon={<Pencil size={13} />}
              onSelect={() => {
                close()
                onStartEdit()
              }}
            >
              {t('chat.edit')}
            </MenuItem>
            <MenuItem
              icon={<Undo2 size={13} />}
              onSelect={() => {
                close()
                onMoveToDraft()
              }}
            >
              {t('chat.recall')}
            </MenuItem>
          </>
        )}
      </Menu>
    </div>
  )
}

/**
 * 展开编辑。
 *
 * ★ **显式取消/保存,不做 blur 即存。** 截图明确画了两个按钮;更重要的是
 * blur 即存之下,误改一个字再点走就永久生效了,而这条消息还没发出去 ——
 * 用户没有任何地方能看到「原文是什么」。
 */
export function PendingQueueEditor({
  initialText,
  onCancel,
  onSave
}: {
  initialText: string
  onCancel: () => void
  onSave: (text: string) => void
}): ReactNode {
  const { t } = useI18n()
  const [text, setText] = useState(initialText)
  const ref = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    const el = ref.current
    if (el === null) return
    el.focus()
    // 光标落到末尾而不是选中全文 —— 编辑排队消息通常是"接着写",不是"重写"
    el.setSelectionRange(el.value.length, el.value.length)
  }, [])

  const dirty = text !== initialText

  return (
    <div
      data-testid="queue-editor"
      className="my-1 rounded-card border border-stroke bg-surface-field p-2"
    >
      <textarea
        ref={ref}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.preventDefault()
            onCancel()
          }
          // Cmd/Ctrl+Enter 保存 —— 裸 Enter 留给换行,排队消息经常是多段的
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
            e.preventDefault()
            onSave(text)
          }
        }}
        className="scroll-thin selectable max-h-[240px] min-h-[88px] w-full resize-none bg-transparent px-1 text-[13px] leading-relaxed text-fg focus:outline-none"
      />
      <div className="flex items-center justify-end gap-1.5 pt-1">
        <button
          type="button"
          onClick={onCancel}
          className="rounded-[6px] px-2.5 py-1 text-[12px] text-fg-muted transition-colors hover:bg-tint-hover"
        >
          {t('common.cancel')}
        </button>
        <button
          type="button"
          onClick={() => onSave(text)}
          disabled={!dirty}
          data-testid="queue-editor-save"
          className="rounded-[6px] bg-accent px-2.5 py-1 text-[12px] font-medium text-accent-fg transition-opacity disabled:opacity-40"
        >
          {t('common.save')}
        </button>
      </div>
    </div>
  )
}
