/**
 * 功能区命令 —— 画布工具栏能让引擎在用户光标 / 选区处执行的有限命令集。
 *
 * ## 为了什么需求建的
 *
 * WPS 式编辑器的功能区(加粗、字号、对齐、列表、样式、插入表格……)要改文档,但插件与模型
 * **不能**给出任意 `.uno:` 命令串:那等于把 LibreOffice 的整个命令面(宏、打开文件、另存为、
 * 打印到文件)交给第三方代码(计划 §5.1)。命令因此按产品概念命名(`format.bold`),宿主先按
 * 这张表收窄参数,引擎(ncw-office-runtime 的 `command-registry.hpp`)再映射成 UNO 命令并校验一遍。
 *
 * ## 不变式
 *
 * - **表是封闭的。** 不认识的 id 在宿主就拒,不送进引擎;引擎声明了但这里不认识的 id 也不暴露
 *   给视图(`parseCommandIds` 过滤),两边的表必须一起改。
 * - **参数按 id 的形状重建**,多出来的键一律丢掉 —— 透传的话,引擎升级后新认的键就成了后门。
 * - 表里只有改当前文档内容 / 格式的命令:没有保存(必须经宿主的路径与冲突检查)、没有宏。
 *
 * ## 故意不做的
 *
 * - 样式名是否存在由引擎核对(文档里真实的样式表只有引擎知道),这里只收窄形状。
 */

export const DOCUMENT_COMMANDS = [
  'edit.undo', 'edit.redo', 'edit.selectAll',
  'format.bold', 'format.italic', 'format.underline', 'format.strikethrough', 'format.superscript', 'format.subscript', 'format.clear',
  'format.fontName', 'format.fontSize', 'format.color', 'format.highlight',
  'paragraph.alignLeft', 'paragraph.alignCenter', 'paragraph.alignRight', 'paragraph.justify', 'paragraph.indent', 'paragraph.outdent',
  'list.bullets', 'list.numbering',
  'style.paragraph',
  'insert.pageBreak', 'insert.table',
  // 演示:文本框 / 形状直接插在当前幻灯片中央;幻灯片管理作用在用户当前的那一张上
  'insert.textBox', 'insert.rectangle', 'insert.ellipse',
  'slides.new', 'slides.duplicate', 'slides.delete', 'slides.moveUp', 'slides.moveDown', 'slides.layout',
  'cells.merge', 'cells.wrap', 'cells.formatCurrency', 'cells.formatPercent',
  // 表格的公式栏(写当前单元格)与名称框(跳到某格)
  'cells.enter', 'cells.goto'
] as const

export type DocumentCommandId = typeof DOCUMENT_COMMANDS[number]

const KNOWN: ReadonlySet<string> = new Set(DOCUMENT_COMMANDS)

export function isDocumentCommandId(raw: unknown): raw is DocumentCommandId {
  return typeof raw === 'string' && KNOWN.has(raw)
}

/** helper 声明的命令 → 宿主认得的那部分(去重、保持顺序)。一条都没有返回 undefined */
export function parseCommandIds(raw: unknown): DocumentCommandId[] | undefined {
  if (!Array.isArray(raw)) return undefined
  const ids = [...new Set(raw.filter(isDocumentCommandId))]
  return ids.length === 0 ? undefined : ids
}

export type CommandArgs = Record<string, string | number>

export type CommandValidation = { ok: true; command: DocumentCommandId; args?: CommandArgs } | { ok: false; reason: string }

const MAX_NAME_BYTES = 128
/** 单元格文字的上限(与引擎侧一致) */
const MAX_CELL_TEXT_BYTES = 32767
/**
 * 开放的幻灯片版式(LibreOffice AutoLayout 值):标题页、标题 + 内容、两栏内容、仅标题、空白、居中文字。
 * 与引擎的 kLayout* 一致;其余值落到界面上没有入口的版式(图表、竖排),两边都拒。
 */
export const SLIDE_LAYOUTS = { title: 0, titleContent: 1, twoContent: 3, titleOnly: 19, blank: 20, centeredText: 32 } as const
const LAYOUT_VALUES: ReadonlySet<number> = new Set(Object.values(SLIDE_LAYOUTS))
const CELL_RANGE = /^\$?[A-Z]{1,3}\$?[0-9]{1,7}(:\$?[A-Z]{1,3}\$?[0-9]{1,7})?$/
const encoder = new TextEncoder()

function recordOf(raw: unknown): Record<string, unknown> {
  return raw !== null && typeof raw === 'object' && !Array.isArray(raw) ? (raw as Record<string, unknown>) : {}
}

/** 字体名 / 样式名:非空、有上限、不含控制字符(它们会原样进 UNO 参数) */
function plainName(raw: unknown): string | null {
  if (typeof raw !== 'string' || raw === '' || encoder.encode(raw).byteLength > MAX_NAME_BYTES) return null
  for (const char of raw) {
    const code = char.codePointAt(0) ?? 0
    if (code < 0x20 || code === 0x7f) return null
  }
  return raw
}

function numberIn(raw: unknown, min: number, max: number, integer: boolean): number | null {
  if (typeof raw !== 'number' || !Number.isFinite(raw) || raw < min || raw > max) return null
  if (integer && !Number.isInteger(raw)) return null
  return raw
}

/**
 * 校验一条命令。`available` 是引擎为这份文档声明的命令(`capabilities.commands`):
 * 表里有、但这种文档不支持的(Word 里的「合并单元格」)按 `unsupported_operation` 拒。
 */
export function validateCommand(command: unknown, rawArgs: unknown, available: readonly string[]): CommandValidation {
  if (!isDocumentCommandId(command)) return { ok: false, reason: 'unsupported_operation: unknown command' }
  if (!available.includes(command)) return { ok: false, reason: `unsupported_operation: ${command} is not available for this document` }
  const args = recordOf(rawArgs)
  switch (command) {
    case 'format.fontName':
    case 'style.paragraph': {
      const name = plainName(args.name)
      if (name === null) return { ok: false, reason: `name must be a non-empty name of at most ${MAX_NAME_BYTES} bytes` }
      return { ok: true, command, args: { name } }
    }
    case 'format.fontSize': {
      const size = numberIn(args.size, 1, 999, false)
      if (size === null) return { ok: false, reason: 'size must be a number between 1 and 999' }
      return { ok: true, command, args: { size } }
    }
    case 'format.color':
    case 'format.highlight': {
      // -1 = 自动(去掉颜色);其余 0xRRGGBB
      const color = numberIn(args.color, -1, 0xffffff, true)
      if (color === null) return { ok: false, reason: 'color must be -1 or an integer 0xRRGGBB' }
      return { ok: true, command, args: { color } }
    }
    case 'cells.enter': {
      // 空串合法(清空单元格);以 = 开头的是公式,原样交给引擎
      const text = args.text
      if (typeof text !== 'string' || text.includes('\0') || encoder.encode(text).byteLength > MAX_CELL_TEXT_BYTES) {
        return { ok: false, reason: `text must be a string of at most ${MAX_CELL_TEXT_BYTES} bytes` }
      }
      return { ok: true, command, args: { text } }
    }
    case 'cells.goto': {
      const ref = args.ref
      if (typeof ref !== 'string' || !CELL_RANGE.test(ref)) return { ok: false, reason: 'ref must look like A1 or A1:C10' }
      return { ok: true, command, args: { ref } }
    }
    case 'slides.layout': {
      const layout = args.layout
      if (typeof layout !== 'number' || !LAYOUT_VALUES.has(layout)) return { ok: false, reason: `layout must be one of ${[...LAYOUT_VALUES].join(', ')}` }
      return { ok: true, command, args: { layout } }
    }
    case 'insert.table': {
      const rows = numberIn(args.rows, 1, 100, true)
      const columns = numberIn(args.columns, 1, 64, true)
      if (rows === null || columns === null) return { ok: false, reason: 'rows must be 1–100 and columns 1–64' }
      return { ok: true, command, args: { rows, columns } }
    }
    default:
      return { ok: true, command }
  }
}

/** 引擎回报的命令状态:命令 id → 值("true" / "false" / 字体名 / 字号)。只留认得的 id,值截断 */
export function parseCommandStates(raw: unknown): Partial<Record<DocumentCommandId, string>> | undefined {
  const out: Partial<Record<DocumentCommandId, string>> = {}
  let count = 0
  for (const [key, value] of Object.entries(recordOf(raw))) {
    if (!isDocumentCommandId(key) || typeof value !== 'string') continue
    out[key] = value.slice(0, 256)
    count += 1
  }
  return count === 0 ? undefined : out
}
