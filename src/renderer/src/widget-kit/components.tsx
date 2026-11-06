/**
 * TSX widget 专用的**展示与布局**组件 —— 宿主 `components/ui/**` 里没有的那一半。
 *
 * ## 为什么布局走 props 而不是让模型写 Tailwind class
 *
 * `ui.css` 里只有 Tailwind **扫描到的** class(`plugin-ui/ui.css` 的 `@source`)。
 * 模型临时写的 `className="grid-cols-5"` 多半根本不存在,而失效是无声的 ——
 * 布局塌成一列,模型看不到。所以间距、列数这些数值一律是 props,经内联样式落地;
 * 这里自己用到的 class 都写成字面量,扫描器看得见。
 *
 * ## 风格约束
 *
 * widget 直接长在对话里(外面没有卡片框),所以这里**没有一个组件自带外描边**。
 * 需要分组时用 `Panel`(一层浅底,不画线)。
 */
import { Children, useState, type CSSProperties, type ReactNode } from 'react'
import { cn } from '../lib/cn'

type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'

/** 间距刻度:1 = 4px。和宿主的 Tailwind 间距同一个单位。 */
function space(step: number | undefined, fallback: number): string {
  return `${String((step ?? fallback) * 4)}px`
}

const TONE_TEXT: Record<Tone, string> = {
  neutral: 'text-fg-muted',
  info: 'text-accent',
  success: 'text-success',
  warning: 'text-warning',
  danger: 'text-danger'
}

const TONE_SOFT: Record<Tone, string> = {
  neutral: 'bg-tint text-fg-muted',
  info: 'bg-accent-soft text-accent',
  success: 'bg-success/12 text-success',
  warning: 'bg-warning/14 text-warning',
  danger: 'bg-danger/12 text-danger'
}

// ── 布局 ──

interface FlexProps {
  children?: ReactNode
  /** 间距,1 = 4px */
  gap?: number
  align?: 'start' | 'center' | 'end' | 'stretch' | 'baseline'
  justify?: 'start' | 'center' | 'end' | 'between'
  wrap?: boolean
  className?: string
  style?: CSSProperties
}

const JUSTIFY: Record<NonNullable<FlexProps['justify']>, CSSProperties['justifyContent']> = {
  start: 'flex-start',
  center: 'center',
  end: 'flex-end',
  between: 'space-between'
}

const ALIGN: Record<NonNullable<FlexProps['align']>, CSSProperties['alignItems']> = {
  start: 'flex-start',
  center: 'center',
  end: 'flex-end',
  stretch: 'stretch',
  baseline: 'baseline'
}

/** 竖排。默认间距 12px。 */
export function Stack({ children, gap, align = 'stretch', justify = 'start', className, style }: FlexProps): ReactNode {
  return (
    <div
      className={className}
      style={{ display: 'flex', flexDirection: 'column', gap: space(gap, 3), alignItems: ALIGN[align], justifyContent: JUSTIFY[justify], ...style }}
    >
      {children}
    </div>
  )
}

/** 横排。默认垂直居中、间距 8px。 */
export function Row({ children, gap, align = 'center', justify = 'start', wrap = false, className, style }: FlexProps): ReactNode {
  return (
    <div
      className={className}
      style={{
        display: 'flex',
        flexDirection: 'row',
        gap: space(gap, 2),
        alignItems: ALIGN[align],
        justifyContent: JUSTIFY[justify],
        flexWrap: wrap ? 'wrap' : 'nowrap',
        ...style
      }}
    >
      {children}
    </div>
  )
}

/**
 * 网格。给 `cols` 是固定列数;给 `minWidth` 是"每格至少这么宽、能放几列放几列"
 * (窄窗口下自动折行,优先用它)。
 */
export function Grid({
  children,
  cols,
  minWidth,
  gap,
  className,
  style
}: {
  children?: ReactNode
  cols?: number
  minWidth?: number
  gap?: number
  className?: string
  style?: CSSProperties
}): ReactNode {
  const template =
    minWidth !== undefined
      ? `repeat(auto-fill, minmax(${String(minWidth)}px, 1fr))`
      : `repeat(${String(cols ?? 2)}, minmax(0, 1fr))`
  return (
    <div className={className} style={{ display: 'grid', gridTemplateColumns: template, gap: space(gap, 3), ...style }}>
      {children}
    </div>
  )
}

/** 一层浅底的分组。**不画边框** —— widget 外面已经没有卡片框,这里也别造一个。 */
export function Panel({ children, padding, className, style }: { children?: ReactNode; padding?: number; className?: string; style?: CSSProperties }): ReactNode {
  return (
    <div className={cn('rounded-card bg-tint', className)} style={{ padding: space(padding, 3), ...style }}>
      {children}
    </div>
  )
}

export function Divider(): ReactNode {
  return <div className="h-px w-full bg-hairline" role="separator" />
}

// ── 文字 ──

export function Heading({ children, level = 2, className }: { children?: ReactNode; level?: 1 | 2 | 3; className?: string }): ReactNode {
  const size = level === 1 ? 'text-[20px]' : level === 2 ? 'text-[16px]' : 'text-[14px]'
  const Tag = level === 1 ? 'h1' : level === 2 ? 'h2' : 'h3'
  return <Tag className={cn('m-0 font-semibold text-fg', size, className)}>{children}</Tag>
}

export function Text({
  children,
  tone = 'default',
  size = 'md',
  mono = false,
  className
}: {
  children?: ReactNode
  tone?: 'default' | 'muted' | 'faint' | Tone
  size?: 'sm' | 'md' | 'lg'
  mono?: boolean
  className?: string
}): ReactNode {
  const color = tone === 'default' ? 'text-fg' : tone === 'muted' ? 'text-fg-muted' : tone === 'faint' ? 'text-fg-faint' : TONE_TEXT[tone]
  const fontSize = size === 'sm' ? 'text-[12px]' : size === 'lg' ? 'text-[15px]' : 'text-[13px]'
  return <span className={cn(color, fontSize, mono && 'font-mono', 'leading-relaxed', className)}>{children}</span>
}

export function Code({ children, className }: { children?: ReactNode; className?: string }): ReactNode {
  return (
    <pre className={cn('m-0 overflow-x-auto rounded-card bg-surface-sunken px-3 py-2 font-mono text-[12px] leading-relaxed text-fg', className)}>
      {children}
    </pre>
  )
}

export function Kbd({ children }: { children?: ReactNode }): ReactNode {
  return <kbd className="rounded-[4px] bg-tint-strong px-1.5 py-px font-mono text-[11px] text-fg-muted">{children}</kbd>
}

// ── 状态 ──

export function Badge({ children, tone = 'neutral' }: { children?: ReactNode; tone?: Tone }): ReactNode {
  return (
    <span className={cn('inline-flex items-center rounded-pill px-2 py-0.5 text-[11px] font-medium leading-4', TONE_SOFT[tone])}>
      {children}
    </span>
  )
}

/** 一段提示。左侧一道色条,没有外框。 */
export function Callout({ children, title, tone = 'info' }: { children?: ReactNode; title?: ReactNode; tone?: Tone }): ReactNode {
  return (
    <div className={cn('flex gap-3 rounded-card px-3 py-2.5', TONE_SOFT[tone])}>
      <div className="w-[3px] shrink-0 self-stretch rounded-full bg-current opacity-70" />
      <div className="flex min-w-0 flex-col gap-1">
        {title !== undefined && <div className="text-[13px] font-semibold">{title}</div>}
        <div className="text-[13px] leading-relaxed text-fg">{children}</div>
      </div>
    </div>
  )
}

/**
 * 一个数值格:说明 + 大字 + 可选的变化量。
 * `delta` 以 `+` / `-` 开头时自动着色(涨绿跌红);要反过来(成本类)给 `invert`。
 */
export function Stat({
  label,
  value,
  delta,
  hint,
  tone,
  invert = false
}: {
  label: ReactNode
  value: ReactNode
  delta?: string
  hint?: ReactNode
  tone?: Tone
  invert?: boolean
}): ReactNode {
  const up = delta?.trim().startsWith('+') === true
  const down = delta?.trim().startsWith('-') === true || delta?.trim().startsWith('−') === true
  const deltaTone: Tone = tone ?? (up ? (invert ? 'danger' : 'success') : down ? (invert ? 'success' : 'danger') : 'neutral')
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <div className="truncate text-[12px] text-fg-muted">{label}</div>
      <div className="flex items-baseline gap-2">
        <div className="text-[22px] font-semibold tabular-nums leading-tight text-fg">{value}</div>
        {delta !== undefined && <div className={cn('text-[12px] font-medium tabular-nums', TONE_TEXT[deltaTone])}>{delta}</div>}
      </div>
      {hint !== undefined && <div className="text-[11px] text-fg-faint">{hint}</div>}
    </div>
  )
}

/** 一排指标。默认每格至少 140px、自动折行。 */
export function StatGrid({ children, minWidth = 140 }: { children?: ReactNode; minWidth?: number }): ReactNode {
  return (
    <Grid minWidth={minWidth} gap={4}>
      {children}
    </Grid>
  )
}

// ── 表格 ──

export interface TableColumn<Row> {
  key: string
  label: ReactNode
  align?: 'left' | 'right' | 'center'
  /** 自定义单元格。缺省按 `row[key]` 显示 */
  render?: (row: Row, index: number) => ReactNode
  width?: number | string
}

/**
 * 数据表。只有横向细线,没有外框与竖线;数字列给 `align: 'right'`。
 * 行多时给 `maxHeight`,表头会吸顶。
 */
export function Table<Row extends Record<string, unknown>>({
  columns,
  rows,
  maxHeight,
  onRowClick,
  empty
}: {
  columns: Array<TableColumn<Row>>
  rows: Row[]
  maxHeight?: number
  onRowClick?: (row: Row, index: number) => void
  empty?: ReactNode
}): ReactNode {
  return (
    <div className="w-full overflow-auto" style={maxHeight === undefined ? undefined : { maxHeight }}>
      <table className="w-full border-collapse text-[13px]">
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.key}
                className="sticky top-0 border-b border-hairline bg-canvas px-2 py-1.5 text-[12px] font-medium text-fg-muted"
                style={{ textAlign: column.align ?? 'left', width: column.width }}
              >
                {column.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 && (
            <tr>
              <td colSpan={columns.length} className="px-2 py-4 text-center text-fg-faint">
                {empty ?? '—'}
              </td>
            </tr>
          )}
          {rows.map((row, index) => (
            <tr
              key={index}
              className={cn('border-b border-hairline last:border-b-0', onRowClick !== undefined && 'cursor-pointer hover:bg-tint')}
              onClick={onRowClick === undefined ? undefined : () => { onRowClick(row, index) }}
            >
              {columns.map((column) => (
                <td
                  key={column.key}
                  className="px-2 py-1.5 tabular-nums text-fg"
                  style={{ textAlign: column.align ?? 'left' }}
                >
                  {column.render !== undefined ? column.render(row, index) : formatCell(row[column.key])}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function formatCell(value: unknown): ReactNode {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'number') return Number.isInteger(value) ? value.toLocaleString() : value.toLocaleString(undefined, { maximumFractionDigits: 2 })
  if (typeof value === 'string' || typeof value === 'boolean') return String(value)
  return JSON.stringify(value)
}

// ── Tabs ──

export interface TabItem {
  id: string
  label: ReactNode
  content: ReactNode
}

/**
 * 一组 Tab。不受控时自己记当前项;要随卡片持久化,配 `useWidgetState` 受控用。
 */
export function Tabs({
  items,
  value,
  defaultValue,
  onChange
}: {
  items: TabItem[]
  value?: string
  defaultValue?: string
  onChange?: (id: string) => void
}): ReactNode {
  const [inner, setInner] = useState(defaultValue ?? items[0]?.id ?? '')
  const active = value ?? inner
  const select = (id: string): void => {
    setInner(id)
    onChange?.(id)
  }
  const current = items.find((item) => item.id === active) ?? items[0]
  return (
    <div className="flex flex-col gap-3">
      <div className="flex gap-1 border-b border-hairline" role="tablist">
        {items.map((item) => (
          <button
            key={item.id}
            type="button"
            role="tab"
            aria-selected={item.id === current?.id}
            onClick={() => { select(item.id) }}
            className={cn(
              '-mb-px cursor-pointer border-0 border-b-2 bg-transparent px-2.5 py-1.5 text-[13px] transition-colors',
              item.id === current?.id ? 'border-accent text-fg' : 'border-transparent text-fg-muted hover:text-fg'
            )}
          >
            {item.label}
          </button>
        ))}
      </div>
      <div role="tabpanel">{current?.content}</div>
    </div>
  )
}

// ── 列表 ──

/** 一列条目:左侧主文字,右侧次要信息。 */
export function List({ children }: { children?: ReactNode }): ReactNode {
  return <div className="flex flex-col">{Children.toArray(children)}</div>
}

export function ListItem({
  title,
  description,
  trailing,
  onClick
}: {
  title: ReactNode
  description?: ReactNode
  trailing?: ReactNode
  onClick?: () => void
}): ReactNode {
  return (
    <div
      className={cn('flex items-center gap-3 rounded-[8px] px-2 py-2', onClick !== undefined && 'cursor-pointer hover:bg-tint')}
      onClick={onClick}
    >
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
        <div className="truncate text-[13px] text-fg">{title}</div>
        {description !== undefined && <div className="truncate text-[12px] text-fg-muted">{description}</div>}
      </div>
      {trailing !== undefined && <div className="shrink-0 text-[12px] text-fg-muted">{trailing}</div>}
    </div>
  )
}
