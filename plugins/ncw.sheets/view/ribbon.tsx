/**
 * 表格编辑器的功能区(「开始」页)。
 *
 * 每个按钮都是一条功能区命令(`doc.command`),作用在用户当前的单元格 / 选区上;按下状态来自
 * 引擎回执里的 `states`。零件与文字 / 演示共用(`plugins/office-common/ribbon-parts.tsx`),
 * 规则写在那边的文件头。
 */
import type { ReactNode } from 'react'
import type { EngineCommandId } from 'nextcowork/view'
import {
  AlignCenter, AlignJustify, AlignLeft, AlignRight, Baseline, Bold, DollarSign, Italic, Percent, Redo2, Strikethrough,
  TableCellsMerge, Underline, Undo2, WrapText
} from 'lucide-react'
import { ColorMenu, CommandButton, FontControls, Group, TEXT_COLORS, type CommandStates, type Run } from '../../office-common/ribbon-parts'
import { text } from './messages'

export function Ribbon({ available, states, run, listFonts }: {
  available: ReadonlySet<string>
  states: CommandStates
  run: Run
  listFonts: () => Promise<string[]>
}): ReactNode {
  const button = (id: EngineCommandId, label: string, icon: ReactNode): ReactNode =>
    <CommandButton id={id} label={label} icon={icon} available={available} states={states} run={run} />
  return (
    <div role="toolbar" aria-label={text.ribbon} className="flex shrink-0 flex-wrap items-center gap-2 border-b border-hairline px-3 py-1.5">
      <Group>
        {button('edit.undo', text.undo, <Undo2 size={16} />)}
        {button('edit.redo', text.redo, <Redo2 size={16} />)}
      </Group>
      <Group>
        <FontControls available={available} states={states} run={run} listFonts={listFonts} labels={text} />
      </Group>
      <Group>
        {button('format.bold', text.bold, <Bold size={16} />)}
        {button('format.italic', text.italic, <Italic size={16} />)}
        {button('format.underline', text.underline, <Underline size={16} />)}
        {button('format.strikethrough', text.strikethrough, <Strikethrough size={16} />)}
        <ColorMenu command="format.color" colors={TEXT_COLORS} available={available} run={run} label={text.fontColor} noneLabel={text.automatic}
          trigger={<Baseline size={16} aria-label={text.fontColor} />} />
      </Group>
      <Group>
        {button('paragraph.alignLeft', text.alignLeft, <AlignLeft size={16} />)}
        {button('paragraph.alignCenter', text.alignCenter, <AlignCenter size={16} />)}
        {button('paragraph.alignRight', text.alignRight, <AlignRight size={16} />)}
        {button('paragraph.justify', text.justify, <AlignJustify size={16} />)}
      </Group>
      <Group>
        {button('cells.merge', text.merge, <TableCellsMerge size={16} />)}
        {button('cells.wrap', text.wrap, <WrapText size={16} />)}
        {button('cells.formatCurrency', text.currency, <DollarSign size={16} />)}
        {button('cells.formatPercent', text.percent, <Percent size={16} />)}
      </Group>
    </div>
  )
}
