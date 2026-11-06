/**
 * 办公编辑器插件(文字 / 表格 / 演示)共用的功能区零件。
 *
 * 需求:三个编辑器的功能区有一半是同一件事 —— 按命令画按钮、按引擎状态显示按下、字体字号框、
 * 颜色色板。各抄一份的话,改一处按钮行为就要改三处,迟早分叉(计划 §7.3 的 office-plugin-common)。
 * 这个目录不是一个插件,由各插件的 build.mjs 经相对路径打进各自的视图 bundle。
 *
 * 规则(三个编辑器一致):
 * - 只画引擎声明了的命令(`capabilities.commands`):没有后端能力的按钮是一次必失败的承诺。
 * - 颜色色板里的十六进制是**文档颜色**(写进文件的数据),不是界面配色。
 * - 文案由调用方传进来(各插件自己的中英文案表),这里不带任何字符串。
 */
import { useEffect, useState, type ReactNode } from 'react'
import { Button, Dialog, IconButton, Menu, MenuItem, NumberInput, Select } from 'nextcowork/ui'
import type { EngineCommandId } from 'nextcowork/view'

export type Run = (command: EngineCommandId, args?: Record<string, string | number>) => void
export type CommandStates = Partial<Record<EngineCommandId, string>>

export const FONT_SIZES = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72]
// 文档颜色(见文件头)
export const TEXT_COLORS = [0x000000, 0xc00000, 0xff0000, 0xffc000, 0x00b050, 0x0070c0, 0x7030a0, 0x7f7f7f]
export const HIGHLIGHTS = [0xffff00, 0x00ff00, 0x00ffff, 0xff00ff, 0xd9d9d9]

export const hex = (color: number): string => `#${color.toString(16).padStart(6, '0')}`

export function Swatch({ color }: { color: number }): ReactNode {
  return <span className="inline-block h-3.5 w-3.5 rounded-sm border border-hairline" style={{ backgroundColor: hex(color) }} />
}

/**
 * 一个功能区按钮:命令不可用时不画;按下状态读 states。
 * 引擎报 "disabled"(没有可撤销的、只剩一张幻灯片时删除、没选中对象时加粗)就置灰 ——
 * 点了也什么都不发生的按钮会被当成坏了。
 * 状态是 "true" / "false" 的才是开关(加粗、对齐……),给读屏报按下状态;撤销这类只报
 * "enabled" / "disabled" 的是普通按钮。
 */
export function CommandButton({ id, label, icon, available, states, run }: {
  id: EngineCommandId
  label: string
  icon: ReactNode
  available: ReadonlySet<string>
  states: CommandStates
  run: Run
}): ReactNode {
  if (!available.has(id)) return null
  const state = states[id]
  const toggle = state === 'true' || state === 'false'
  return (
    <IconButton label={label} active={state === 'true'} pressed={toggle ? state === 'true' : undefined} disabled={state === 'disabled'} onClick={() => { run(id) }}>
      {icon}
    </IconButton>
  )
}

export function Group({ children }: { children: ReactNode }): ReactNode {
  return <div className="flex items-center gap-0.5 border-r border-hairline pr-2 last:border-r-0">{children}</div>
}

/** 当前值不在列表里(文档用了本机没有的字体)时也要显示出来,否则下拉框是空的 */
export function withCurrent(names: readonly string[], current: string | undefined): { value: string; label: string }[] {
  return [...(current !== undefined && current !== '' && !names.includes(current) ? [current] : []), ...names].map((name) => ({ value: name, label: name }))
}

/** 字体框 + 字号框。字体列表只取一次;取不到就只显示当前值 */
export function FontControls({ available, states, run, listFonts, labels }: {
  available: ReadonlySet<string>
  states: CommandStates
  run: Run
  listFonts: () => Promise<string[]>
  labels: { font: string; fontSize: string }
}): ReactNode {
  const [fonts, setFonts] = useState<string[]>([])
  useEffect(() => {
    if (available.has('format.fontName')) listFonts().then(setFonts, () => undefined)
  }, [available, listFonts])
  const fontSize = states['format.fontSize'] ?? ''
  return (
    <>
      {available.has('format.fontName') && (
        <Select
          ariaLabel={labels.font}
          className="w-40"
          value={states['format.fontName'] ?? ''}
          options={withCurrent(fonts, states['format.fontName'])}
          onValueChange={(name) => { run('format.fontName', { name }) }}
        />
      )}
      {available.has('format.fontSize') && (
        <Select
          ariaLabel={labels.fontSize}
          className="w-20"
          value={fontSize}
          options={withCurrent(FONT_SIZES.map(String), fontSize)}
          onValueChange={(size) => { run('format.fontSize', { size: Number(size) }) }}
        />
      )}
    </>
  )
}

/** 「插入表格」对话框(文字与演示共用):行 1–100、列 1–64,与引擎的参数上限一致 */
export function InsertTableDialog({ open, onClose, run, labels }: {
  open: boolean
  onClose: () => void
  run: Run
  labels: { insertTable: string; rows: string; columns: string; cancel: string; insert: string }
}): ReactNode {
  const [rows, setRows] = useState(3)
  const [columns, setColumns] = useState(3)
  return (
    <Dialog
      title={labels.insertTable}
      open={open}
      onClose={onClose}
      width={320}
      footer={(
        <>
          <Button onClick={onClose}>{labels.cancel}</Button>
          <Button variant="accent" onClick={() => { onClose(); run('insert.table', { rows, columns }) }}>{labels.insert}</Button>
        </>
      )}
    >
      <div className="flex items-center gap-3 text-[12px]">
        <label className="flex items-center gap-2">{labels.rows}<NumberInput ariaLabel={labels.rows} value={rows} min={1} max={100} onCommit={setRows} /></label>
        <label className="flex items-center gap-2">{labels.columns}<NumberInput ariaLabel={labels.columns} value={columns} min={1} max={64} onCommit={setColumns} /></label>
      </div>
    </Dialog>
  )
}

/** 颜色菜单(字体颜色 / 突出显示):第一项是「自动 / 无」(-1) */
export function ColorMenu({ command, colors, available, run, trigger, label, noneLabel }: {
  command: 'format.color' | 'format.highlight'
  colors: readonly number[]
  available: ReadonlySet<string>
  run: Run
  trigger: ReactNode
  label: string
  noneLabel: string
}): ReactNode {
  if (!available.has(command)) return null
  return (
    <Menu label={label} width={180} trigger={trigger}>
      {() => (
        <>
          <MenuItem onSelect={() => { run(command, { color: -1 }) }}>{noneLabel}</MenuItem>
          {colors.map((color) => (
            <MenuItem key={color} icon={<Swatch color={color} />} onSelect={() => { run(command, { color }) }}>{hex(color)}</MenuItem>
          ))}
        </>
      )}
    </Menu>
  )
}
