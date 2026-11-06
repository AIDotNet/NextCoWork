/**
 * 表格的公式栏(名称框 + 编辑栏)、行列头与工作表标签。
 *
 * ## 为了什么需求建的
 *
 * WPS 式表格必须有这三样:名称框显示 / 跳转当前单元格,编辑栏显示公式原文(不是计算结果)并能改,
 * 行号列标跟着画布滚动、与格子对齐,底部标签切换工作表。数据全部来自引擎:当前单元格的地址与公式
 * 在输入回执里(`cellAddress` / `cellFormula`),行高列宽由引擎的行列头查询给出 —— 视图按默认列宽
 * 自己推算的话,改过一次列宽行列头就和格子对不上。
 */
import { useState, type ReactNode } from 'react'
import { cn } from 'nextcowork/ui'
import { twipsToCssPx } from 'nextcowork/view'
import { text } from './messages'

/** 行列头条的厚度(CSS px):列标的高度 = 行号的宽度的一半左右,取 WPS 的比例 */
export const COLUMN_HEADER_HEIGHT = 22
export const ROW_HEADER_WIDTH = 44

const inputClass = 'h-7 rounded-[6px] border border-border bg-surface-input px-2 text-[12px] text-fg outline-none focus:border-accent'

/**
 * 公式栏。两个框都是「失焦 / 回车才提交」:编辑中途引擎推来的新值不覆盖用户正在打的字;
 * Esc 放弃编辑,回到引擎里的当前值。
 */
export function FormulaBar({ address, formula, onGoto, onEnter }: {
  address: string
  formula: string
  onGoto: (ref: string) => void
  onEnter: (value: string) => void
}): ReactNode {
  const [name, setName] = useState<string | null>(null)
  const [draft, setDraft] = useState<string | null>(null)
  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-3 py-1">
      <input
        aria-label={text.nameBox}
        className={cn(inputClass, 'w-24 font-mono')}
        value={name ?? address}
        onChange={(event) => { setName(event.target.value) }}
        onBlur={() => { setName(null) }}
        onKeyDown={(event) => {
          if (event.key === 'Escape') setName(null)
          if (event.key !== 'Enter' || name === null) return
          onGoto(name.trim().toUpperCase())
          setName(null)
        }}
      />
      <span className="text-[12px] italic text-fg-muted">fx</span>
      <input
        aria-label={text.formula}
        className={cn(inputClass, 'min-w-0 flex-1 font-mono')}
        value={draft ?? formula}
        onChange={(event) => { setDraft(event.target.value) }}
        onBlur={() => { setDraft(null) }}
        onKeyDown={(event) => {
          if (event.key === 'Escape') setDraft(null)
          if (event.key !== 'Enter' || draft === null) return
          event.preventDefault()
          onEnter(draft)
          setDraft(null)
        }}
      />
    </div>
  )
}

export type HeaderList = [number, string][]

/** 列标条:每一列的起点是上一项的结束位置;位置随画布的横向滚动平移 */
export function ColumnHeaders({ columns, zoom, scrollLeft, active }: { columns: HeaderList; zoom: number; scrollLeft: number; active: string }): ReactNode {
  return (
    <div aria-label={text.columnHeaders} className="relative min-w-0 flex-1 overflow-hidden border-b border-hairline bg-surface" style={{ height: COLUMN_HEADER_HEIGHT }}>
      {columns.slice(1).map(([end, label], index) => {
        const start = columns[index]?.[0] ?? 0
        return (
          <div
            key={label}
            className={cn('absolute top-0 flex h-full items-center justify-center border-r border-hairline text-[11px] text-fg-muted', label === active && 'bg-tint text-fg')}
            style={{ left: twipsToCssPx(start, zoom) - scrollLeft, width: twipsToCssPx(end - start, zoom) }}
          >
            {label}
          </div>
        )
      })}
    </div>
  )
}

/** 行号条:同上,随画布的纵向滚动平移 */
export function RowHeaders({ rows, zoom, scrollTop, active }: { rows: HeaderList; zoom: number; scrollTop: number; active: string }): ReactNode {
  return (
    <div aria-label={text.rowHeaders} className="relative shrink-0 overflow-hidden border-r border-hairline bg-surface" style={{ width: ROW_HEADER_WIDTH }}>
      {rows.slice(1).map(([end, label], index) => {
        const start = rows[index]?.[0] ?? 0
        return (
          <div
            key={label}
            className={cn('absolute left-0 flex w-full items-center justify-center border-b border-hairline text-[11px] text-fg-muted', label === active && 'bg-tint text-fg')}
            style={{ top: twipsToCssPx(start, zoom) - scrollTop, height: twipsToCssPx(end - start, zoom) }}
          >
            {label}
          </div>
        )
      })}
    </div>
  )
}

/** 工作表标签。切换由调用方经引擎完成(之后的点选与键入落在新表上) */
export function SheetTabs({ names, current, onSelect }: { names: string[]; current: number; onSelect: (index: number) => void }): ReactNode {
  return (
    <div role="tablist" aria-label={text.sheets} className="flex shrink-0 items-center gap-0.5 overflow-x-auto border-t border-hairline bg-surface px-2 py-1">
      {names.map((name, index) => (
        <button
          key={`${index}:${name}`}
          type="button"
          role="tab"
          aria-selected={index === current}
          className={cn('rounded-[6px] px-3 py-1 text-[12px] text-fg-muted hover:bg-tint-hover', index === current && 'bg-canvas text-fg')}
          onClick={() => { onSelect(index) }}
        >
          {name}
        </button>
      ))}
    </div>
  )
}
