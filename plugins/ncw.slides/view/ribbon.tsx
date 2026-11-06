/**
 * 演示编辑器的功能区(WPS 式「开始 / 插入」两页)。
 *
 * 每个按钮都是一条功能区命令(`doc.command`),作用在用户当前的幻灯片 / 文本框 / 选区上;按下状态
 * 与可用状态来自引擎回执里的 `states`。零件与文字 / 表格共用(`plugins/office-common/ribbon-parts.tsx`),
 * 规则写在那边的文件头。
 *
 * 版式菜单的编号是 LibreOffice 的 AutoLayout 值,宿主与引擎的命令表都只收这六个
 * (`src/shared/document-engine/commands.ts` 的 SLIDE_LAYOUTS);引擎把当前幻灯片的版式报在
 * `states['slides.layout']` 里,菜单据此打勾。
 */
import { useState, type ReactNode } from 'react'
import { Button, Menu, MenuItem, Segmented } from 'nextcowork/ui'
import type { EngineCommandId } from 'nextcowork/view'
import {
  AlignCenter, AlignJustify, AlignLeft, AlignRight, Baseline, Bold, Circle, Highlighter, IndentDecrease, IndentIncrease,
  Italic, LayoutTemplate, List, ListOrdered, Plus, Redo2, Square, Strikethrough, Subscript, Superscript, Table, Type,
  Underline, Undo2
} from 'lucide-react'
import { ColorMenu, CommandButton, FontControls, Group, HIGHLIGHTS, InsertTableDialog, TEXT_COLORS, type CommandStates, type Run } from '../../office-common/ribbon-parts'
import { text } from './messages'

/** 与 SLIDE_LAYOUTS 同值(视图不能 import 宿主源码,这里抄一份;多了少了引擎会拒) */
const LAYOUTS: { value: number; label: string }[] = [
  { value: 0, label: text.layoutTitle },
  { value: 1, label: text.layoutTitleContent },
  { value: 3, label: text.layoutTwoContent },
  { value: 19, label: text.layoutTitleOnly },
  { value: 20, label: text.layoutBlank },
  { value: 32, label: text.layoutCentered }
]

/** 版式菜单(`slides.layout`),「开始」与「插入」两页都放 */
function LayoutMenu({ available, states, run }: { available: ReadonlySet<string>; states: CommandStates; run: Run }): ReactNode {
  if (!available.has('slides.layout')) return null
  return (
    <Menu label={text.layout} width={180} trigger={<span className="flex items-center gap-1 text-[12px]"><LayoutTemplate size={16} />{text.layout}</span>}>
      {() => LAYOUTS.map(({ value, label }) => (
        <MenuItem key={value} checked={states['slides.layout'] === String(value)} onSelect={() => { run('slides.layout', { layout: value }) }}>{label}</MenuItem>
      ))}
    </Menu>
  )
}

export function Ribbon({ available, states, run, listFonts }: {
  /** 引擎为这份文档声明的命令 */
  available: ReadonlySet<string>
  /** 引擎回报的命令状态 */
  states: CommandStates
  run: Run
  listFonts: () => Promise<string[]>
}): ReactNode {
  const [tab, setTab] = useState<'home' | 'insert'>('home')
  const [tableOpen, setTableOpen] = useState(false)

  const button = (id: EngineCommandId, label: string, icon: ReactNode): ReactNode =>
    <CommandButton id={id} label={label} icon={icon} available={available} states={states} run={run} />
  /** 带文字的按钮(插入页):同样只画引擎声明了的命令,引擎报 disabled 时置灰 */
  const labeled = (id: EngineCommandId, label: string, icon: ReactNode, onClick?: () => void): ReactNode =>
    available.has(id) && (
      <Button size="sm" icon={icon} disabled={states[id] === 'disabled'} onClick={onClick ?? (() => { run(id) })}>{label}</Button>
    )

  return (
    <div role="toolbar" aria-label={text.ribbon} className="flex shrink-0 flex-col gap-1 border-b border-hairline px-3 pb-1.5 pt-1">
      <Segmented
        value={tab}
        options={[{ value: 'home', label: text.tabHome }, { value: 'insert', label: text.tabInsert }]}
        onChange={setTab}
      />
      {tab === 'home' ? (
        <div className="flex flex-wrap items-center gap-2">
          <Group>
            {button('edit.undo', text.undo, <Undo2 size={16} />)}
            {button('edit.redo', text.redo, <Redo2 size={16} />)}
          </Group>
          <Group>
            {labeled('slides.new', text.newSlide, <Plus size={14} />)}
            <LayoutMenu available={available} states={states} run={run} />
          </Group>
          <Group>
            <FontControls available={available} states={states} run={run} listFonts={listFonts} labels={text} />
          </Group>
          <Group>
            {button('format.bold', text.bold, <Bold size={16} />)}
            {button('format.italic', text.italic, <Italic size={16} />)}
            {button('format.underline', text.underline, <Underline size={16} />)}
            {button('format.strikethrough', text.strikethrough, <Strikethrough size={16} />)}
            {button('format.superscript', text.superscript, <Superscript size={16} />)}
            {button('format.subscript', text.subscript, <Subscript size={16} />)}
            <ColorMenu command="format.color" colors={TEXT_COLORS} available={available} run={run} label={text.fontColor} noneLabel={text.automatic}
              trigger={<Baseline size={16} aria-label={text.fontColor} />} />
            <ColorMenu command="format.highlight" colors={HIGHLIGHTS} available={available} run={run} label={text.highlight} noneLabel={text.noHighlight}
              trigger={<Highlighter size={16} aria-label={text.highlight} />} />
          </Group>
          <Group>
            {button('paragraph.alignLeft', text.alignLeft, <AlignLeft size={16} />)}
            {button('paragraph.alignCenter', text.alignCenter, <AlignCenter size={16} />)}
            {button('paragraph.alignRight', text.alignRight, <AlignRight size={16} />)}
            {button('paragraph.justify', text.justify, <AlignJustify size={16} />)}
          </Group>
          <Group>
            {button('list.bullets', text.bullets, <List size={16} />)}
            {button('list.numbering', text.numbering, <ListOrdered size={16} />)}
            {button('paragraph.outdent', text.outdent, <IndentDecrease size={16} />)}
            {button('paragraph.indent', text.indent, <IndentIncrease size={16} />)}
          </Group>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <Group>
            {labeled('slides.new', text.newSlide, <Plus size={14} />)}
            <LayoutMenu available={available} states={states} run={run} />
          </Group>
          <Group>
            {labeled('insert.textBox', text.textBox, <Type size={14} />)}
            {labeled('insert.rectangle', text.rectangle, <Square size={14} />)}
            {labeled('insert.ellipse', text.ellipse, <Circle size={14} />)}
            {labeled('insert.table', text.table, <Table size={14} />, () => { setTableOpen(true) })}
          </Group>
        </div>
      )}
      <InsertTableDialog open={tableOpen} onClose={() => { setTableOpen(false) }} run={run} labels={text} />
    </div>
  )
}
