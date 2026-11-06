/**
 * 文字编辑器的功能区(WPS 式「开始 / 插入」两页)。
 *
 * ## 为了什么需求建的
 *
 * 用户要像在 WPS 里一样,用工具栏改字体、字号、加粗、对齐、列表、样式,插入表格与分页。
 * 每个按钮都是一条功能区命令(`doc.command`,命令表由宿主与引擎共同审查),在用户的光标 /
 * 选区处执行;按下状态来自引擎回执里的 `states`。按钮、字体框、色板等零件与表格 / 演示共用
 * (`plugins/office-common/ribbon-parts.tsx`),规则写在那边的文件头。
 */
import { useEffect, useState, type ReactNode } from 'react'
import { Button, Segmented, Select } from 'nextcowork/ui'
import type { EngineCommandId } from 'nextcowork/view'
import {
  AlignCenter, AlignJustify, AlignLeft, AlignRight, Baseline, Bold, Eraser, Highlighter, IndentDecrease, IndentIncrease,
  Italic, List, ListOrdered, Redo2, SeparatorHorizontal, Strikethrough, Subscript, Superscript, Table, Underline, Undo2
} from 'lucide-react'
import { ColorMenu, CommandButton, FontControls, Group, HIGHLIGHTS, InsertTableDialog, TEXT_COLORS, withCurrent, type CommandStates, type Run } from '../../office-common/ribbon-parts'
import { text } from './messages'

export function Ribbon({ available, states, run, listFonts, listStyles }: {
  /** 引擎为这份文档声明的命令 */
  available: ReadonlySet<string>
  /** 引擎回报的命令状态(加粗是否按下、当前字体 / 字号) */
  states: CommandStates
  run: Run
  listFonts: () => Promise<string[]>
  listStyles: () => Promise<string[]>
}): ReactNode {
  const [tab, setTab] = useState<'home' | 'insert'>('home')
  const [styles, setStyles] = useState<string[]>([])
  const [tableOpen, setTableOpen] = useState(false)

  useEffect(() => {
    // 样式框的数据只取一次;取不到就只显示当前值
    if (available.has('style.paragraph')) listStyles().then(setStyles, () => undefined)
  }, [available, listStyles])

  const button = (id: EngineCommandId, label: string, icon: ReactNode): ReactNode =>
    <CommandButton id={id} label={label} icon={icon} available={available} states={states} run={run} />

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
          {available.has('style.paragraph') && (
            <Group>
              <Select
                ariaLabel={text.style}
                className="w-36"
                value={states['style.paragraph'] ?? ''}
                options={withCurrent(styles, states['style.paragraph'])}
                onValueChange={(name) => { run('style.paragraph', { name }) }}
              />
            </Group>
          )}
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
            {button('format.clear', text.clearFormat, <Eraser size={16} />)}
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
            {available.has('insert.table') && (
              <Button size="sm" icon={<Table size={14} />} onClick={() => { setTableOpen(true) }}>{text.table}</Button>
            )}
            {available.has('insert.pageBreak') && (
              <Button size="sm" icon={<SeparatorHorizontal size={14} />} onClick={() => { run('insert.pageBreak') }}>{text.pageBreak}</Button>
            )}
          </Group>
        </div>
      )}
      <InsertTableDialog open={tableOpen} onClose={() => { setTableOpen(false) }} run={run} labels={text} />
    </div>
  )
}
