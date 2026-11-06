/**
 * 文档引擎协议 —— 核心 Runtime、引擎插件、前端插件三方共用的**纯类型 + 纯函数**。
 *
 * ## 为了什么需求建的
 *
 * 办公插件(Word / Excel / PowerPoint / PDF)要在 NextCoWork 里做到「编辑器画布和
 * Agent 工具操作**同一个活动文档模型**」。现有 `ncw:doc:*` 文档通道只能搬运一整份
 * 文本或图片(见 `shell/PluginViewFrame.tsx`),对 OOXML / PDF 只会送空串 ——
 * 拿它改办公文件只能「整文件覆盖」,而那会把用户还没保存的输入、选区、撤销栈
 * 一起冲掉。所以这里定义的是**会话协议**:打开一次、之后所有修改都以带修订号的
 * 操作进入同一个 session。
 *
 * ## 这个文件拥有的不变式
 *
 * 1. **格式白名单是封闭的。** 首发只承诺 OOXML(含宏变体)与 PDF;`.doc/.xls/.ppt`
 *    与 ODF 不在表里 —— 不在表里就意味着引擎插件声明了也会被清单解析拒掉,
 *    而不是「声明了、打开时才发现不支持」。
 * 2. **操作是有限的判别联合,不是任意命令串。** 没有 `uno: string`、没有脚本 URL、
 *    没有原始 XML。给出任意命令通道等于让 Agent / 第三方插件绕开所有按操作划分的
 *    权限与校验。
 * 3. **每次修改都带 `expectedRevision`。** 调用方准备操作期间用户又改过文档,
 *    旧位置就作废,必须拒绝而不是按旧坐标写进去。
 *
 * ## 故意不做的
 *
 * - 不定义引擎内部的文档树形状:那由引擎插件按 capability 回答,核心不代言。
 * - 不依赖 Node / Electron:渲染层、插件 SDK 类型、主进程都要能 import 它。
 */
import type { DocumentInteraction } from './interaction'
import type { DocumentCommandId } from './commands'

// ─────────────────────────── 格式 ───────────────────────────

/** 首发承诺的文件格式。★ 改这张表就是改产品承诺,见文件头第 1 条。 */
export const DOCUMENT_FORMATS = ['docx', 'docm', 'xlsx', 'xlsm', 'pptx', 'pptm', 'pdf'] as const
export type DocumentFormat = (typeof DOCUMENT_FORMATS)[number]

export type DocumentKind = 'word' | 'spreadsheet' | 'presentation' | 'pdf'

/**
 * 格式 → 文档种类。`Record<DocumentFormat, …>` 让「加了格式忘了归类」在编译期就挂。
 */
export const DOCUMENT_FORMAT_KIND: Readonly<Record<DocumentFormat, DocumentKind>> = {
  docx: 'word',
  docm: 'word',
  xlsx: 'spreadsheet',
  xlsm: 'spreadsheet',
  pptx: 'presentation',
  pptm: 'presentation',
  pdf: 'pdf'
}

/**
 * 带宏的格式。
 *
 * 需求:宏文件打开时**不得自动运行**事件宏,执行只能来自显式的 `runMacro`。
 * 这张表让核心不用去问引擎「这个文件有没有宏」就能决定要不要挂宏相关的门。
 */
export const MACRO_FORMATS: ReadonlySet<DocumentFormat> = new Set<DocumentFormat>(['docm', 'xlsm', 'pptm'])

export function isDocumentFormat(value: unknown): value is DocumentFormat {
  return typeof value === 'string' && (DOCUMENT_FORMATS as readonly string[]).includes(value)
}

/**
 * 从路径推断格式。认不出返回 `null`(不猜)。
 *
 * ★ 按**扩展名**而不是内容嗅探:这一步只决定「交给哪个引擎」,真正的格式校验在
 * 引擎加载时做。在这里嗅探意味着渲染层也要读字节,而它不该碰文档字节。
 */
export function documentFormatOf(path: string): DocumentFormat | null {
  const name = path.replaceAll('\\', '/').split('/').pop() ?? ''
  const dot = name.lastIndexOf('.')
  if (dot <= 0) return null
  const ext = name.slice(dot + 1).toLowerCase()
  return isDocumentFormat(ext) ? ext : null
}

// ─────────────────────────── 能力 ───────────────────────────

/**
 * 引擎对某一格式**实际**能做什么。
 *
 * ★ 由引擎在会话打开时回报,不是清单里自称的。需求:UI 的按钮与 Agent 的工具
 * 都据此决定出不出现 —— 「画出来却点了没反应」的按钮和「宣称支持却静默忽略」的
 * 工具,是这类插件最常见的两种失败。
 */
export interface DocumentCapabilities {
  format: DocumentFormat
  /** 引擎自报的版本串(领域值,不翻译) */
  engineVersion: string
  /** 支持的操作 `kind`。不在里面的操作一律 `unsupported_operation`,不空成功 */
  operations: DocumentOperationKind[]
  canSave: boolean
  canExport: DocumentFormat[]
  canUndo: boolean
  /** 能否枚举 / 执行宏。只对 `MACRO_FORMATS` 有意义 */
  macros: { list: boolean; run: boolean }
  /**
   * 画布交互输入(键盘 / 鼠标 / 输入法)。缺省 = 只读预览,视图不得画出可编辑的光标。
   * 可选而非必填:旧 helper 与只读引擎(PDF 经 Draw 导入)不回报它,见 `interaction.ts`。
   */
  interaction?: DocumentInteraction
  /**
   * 功能区命令(见 `commands.ts`)。缺省 = 这份文档没有可用的功能区命令(只读、旧 helper)。
   * ★ 视图只画这里有的按钮:没有后端能力的按钮是一次必失败的承诺(AGENTS §5)。
   */
  commands?: DocumentCommandId[]
}

/**
 * 一次预览 tile 的请求。坐标与尺寸都由宿主限制后才会送进 native helper。
 *
 * 需求：文档预览和编辑画布必须复用同一个活动模型，但画布不能把任意大图交给
 * helper；因此协议只描述有限的 RGBA tile，不携带原始文档字节或 UI 状态。
 */
export interface DocumentRenderRequest {
  x: number
  y: number
  tileWidth: number
  tileHeight: number
  width: number
  height: number
  /**
   * 表格的工作表 / 演示文稿的幻灯片序号。Writer 没有 part，给了由引擎拒绝。
   * 需求：渲染非当前部分时不能先切换部分——那会改变用户正在编辑的那一张。
   */
  part?: number
}

export interface DocumentRenderResult {
  width: number
  height: number
  format: 'rgba'
  bytes: Uint8Array
}

// ─────────────────────────── 操作 ───────────────────────────

/**
 * 语义定位。引用由 `query` 返回,**绑定产生它的 generation**:引擎重启 / 重载之后
 * 旧引用必须失效,而不是指到新模型里另一个碰巧同名的东西上。
 */
export interface DocumentTargetRef {
  generation: number
  /** 引擎给的不透明引用 id。核心不解析它 */
  ref: string
}

/**
 * 有限操作集。首批覆盖四类文档最基础的写操作;新增操作 = 在这里加一支,
 * 引擎在 capability 里声明支持才可用。
 */
export type DocumentOperation =
  | { kind: 'text.replace'; target: DocumentTargetRef; text: string }
  | { kind: 'text.insert'; target: DocumentTargetRef; position: 'before' | 'after' | 'start' | 'end'; text: string }
  | { kind: 'style.apply'; target: DocumentTargetRef; style: string }
  | { kind: 'cells.set'; sheet: string; range: string; values: (string | number | boolean | null)[][] }
  | { kind: 'cells.formula'; sheet: string; cell: string; formula: string }
  | { kind: 'sheet.insert'; name: string; index?: number }
  | { kind: 'slide.insert'; index: number; layout?: string }
  | { kind: 'slide.move'; from: number; to: number }
  | { kind: 'object.delete'; target: DocumentTargetRef }
  | { kind: 'pdf.annotate'; page: number; rect: [number, number, number, number]; text: string }
  | { kind: 'pdf.formFill'; field: string; value: string }
  /*
    需求：Word 修改要能定位到「用户说的那段文字」，而引擎目前给不出稳定的语义 ref。
    这三支按**字面文字**定位（ncw-office-runtime helper 已对真实 LibreOffice 实测并读回核对），
    `expectedCount` 让调用方声明预期命中数，不符整批拒绝。
    不满足会怎样：宿主协议里没有这三支时 `parseCapabilities` 把它们过滤掉，Word 文档经宿主
    只能在文首/文末追加，Agent 改不了任何现有内容，且零报错。
  */
  | { kind: 'text.findReplace'; find: string; replace: string; matchCase?: boolean; expectedCount?: number }
  | { kind: 'paragraph.style'; find: string; style: string; matchCase?: boolean; expectedCount?: number }
  | { kind: 'paragraph.insert'; anchor: string; position: 'before' | 'after'; text: string; matchCase?: boolean }

export type DocumentOperationKind = DocumentOperation['kind']

/** 编译期对照表:加一种操作却忘了在这里登记,`satisfies` 当场报错。 */
const OPERATION_KINDS = {
  'text.replace': true,
  'text.insert': true,
  'style.apply': true,
  'cells.set': true,
  'cells.formula': true,
  'sheet.insert': true,
  'slide.insert': true,
  'slide.move': true,
  'object.delete': true,
  'pdf.annotate': true,
  'pdf.formFill': true,
  'text.findReplace': true,
  'paragraph.style': true,
  'paragraph.insert': true
} satisfies Record<DocumentOperationKind, true>

export function isDocumentOperationKind(value: unknown): value is DocumentOperationKind {
  return typeof value === 'string' && Object.hasOwn(OPERATION_KINDS, value)
}

/**
 * 单批操作的上限。
 *
 * ★ 看起来多余,但它挡的是一种真实故障:模型一次塞几千条操作,引擎在单线程事件
 * 循环里逐条执行,期间 UI 输入全部排队 —— 表现为编辑器「卡死」几十秒且零报错。
 */
export const MAX_OPERATIONS_PER_BATCH = 200
/** 单条文本 / 单元格值的字符上限。同理,防一次性把整本书塞进一个段落 */
export const MAX_OPERATION_TEXT = 1_000_000
/** `cells.set` 单次最多多少个格子 */
export const MAX_CELLS_PER_OPERATION = 100_000

export type OperationValidation = { ok: true; operations: DocumentOperation[] } | { ok: false; reason: string }

/**
 * 校验**不可信**的操作批次(来自模型或第三方插件)。
 *
 * 需求:工具的 `inputSchema` 宿主不校验(见 `plugin/tools.ts`),所以这里是
 * 操作进入会话队列前的唯一收口。只收窄形状与上限;「这个 ref 还有效吗」是引擎的事。
 */
export function validateOperations(raw: unknown, capabilities: Pick<DocumentCapabilities, 'operations'>): OperationValidation {
  if (!Array.isArray(raw)) return { ok: false, reason: 'operations must be an array' }
  if (raw.length === 0) return { ok: false, reason: 'operations must not be empty' }
  if (raw.length > MAX_OPERATIONS_PER_BATCH) return { ok: false, reason: `at most ${MAX_OPERATIONS_PER_BATCH} operations per batch` }
  const out: DocumentOperation[] = []
  for (const [index, item] of raw.entries()) {
    const checked = validateOne(item)
    if (typeof checked === 'string') return { ok: false, reason: `operations[${index}]: ${checked}` }
    // ★ 引擎没声明支持的操作**拒绝整批**,不是跳过这一条:跳过会让「一批改动」
    //   变成「改了一半」,而调用方收到的是成功。
    if (!capabilities.operations.includes(checked.kind)) {
      return { ok: false, reason: `operations[${index}]: unsupported_operation ${checked.kind}` }
    }
    out.push(checked)
  }
  return { ok: true, operations: out }
}

function validateOne(item: unknown): DocumentOperation | string {
  if (item === null || typeof item !== 'object' || Array.isArray(item)) return 'must be an object'
  const o = item as Record<string, unknown>
  if (!isDocumentOperationKind(o.kind)) return `unknown kind ${String(o.kind)}`
  switch (o.kind) {
    case 'text.replace': {
      const target = targetOf(o.target)
      if (target === null) return 'target is invalid'
      const text = textOf(o.text)
      return text === null ? 'text is invalid' : { kind: 'text.replace', target, text }
    }
    case 'text.insert': {
      const target = targetOf(o.target)
      if (target === null) return 'target is invalid'
      const text = textOf(o.text)
      if (text === null) return 'text is invalid'
      const position = o.position
      if (position !== 'before' && position !== 'after' && position !== 'start' && position !== 'end') return 'position is invalid'
      return { kind: 'text.insert', target, position, text }
    }
    case 'style.apply': {
      const target = targetOf(o.target)
      if (target === null) return 'target is invalid'
      const style = shortOf(o.style)
      return style === null ? 'style is invalid' : { kind: 'style.apply', target, style }
    }
    case 'cells.set': {
      const sheet = shortOf(o.sheet)
      const range = shortOf(o.range)
      if (sheet === null || range === null) return 'sheet and range are required'
      if (!Array.isArray(o.values) || o.values.length === 0) return 'values must be a non-empty 2D array'
      let cells = 0
      const values: (string | number | boolean | null)[][] = []
      for (const row of o.values) {
        if (!Array.isArray(row)) return 'values must be a 2D array'
        const next: (string | number | boolean | null)[] = []
        for (const cell of row) {
          cells += 1
          if (cells > MAX_CELLS_PER_OPERATION) return `at most ${MAX_CELLS_PER_OPERATION} cells per operation`
          if (cell === null || typeof cell === 'boolean') next.push(cell)
          else if (typeof cell === 'number' && Number.isFinite(cell)) next.push(cell)
          else if (typeof cell === 'string' && cell.length <= MAX_OPERATION_TEXT) next.push(cell)
          else return 'cell values must be string, finite number, boolean or null'
        }
        values.push(next)
      }
      return { kind: 'cells.set', sheet, range, values }
    }
    case 'cells.formula': {
      const sheet = shortOf(o.sheet)
      const cell = shortOf(o.cell)
      const formula = textOf(o.formula)
      if (sheet === null || cell === null || formula === null) return 'sheet, cell and formula are required'
      return { kind: 'cells.formula', sheet, cell, formula }
    }
    case 'sheet.insert': {
      const name = shortOf(o.name)
      if (name === null) return 'name is required'
      if (o.index !== undefined && !isIndex(o.index)) return 'index is invalid'
      return { kind: 'sheet.insert', name, ...(o.index === undefined ? {} : { index: o.index as number }) }
    }
    case 'slide.insert': {
      if (!isIndex(o.index)) return 'index is invalid'
      const layout = o.layout === undefined ? undefined : shortOf(o.layout)
      if (layout === null) return 'layout is invalid'
      return { kind: 'slide.insert', index: o.index, ...(layout === undefined ? {} : { layout }) }
    }
    case 'slide.move':
      if (!isIndex(o.from) || !isIndex(o.to)) return 'from and to must be indexes'
      return { kind: 'slide.move', from: o.from, to: o.to }
    case 'object.delete': {
      const target = targetOf(o.target)
      return target === null ? 'target is invalid' : { kind: 'object.delete', target }
    }
    case 'pdf.annotate': {
      if (!isIndex(o.page)) return 'page is invalid'
      const rect = o.rect
      if (!Array.isArray(rect) || rect.length !== 4 || !rect.every((n) => typeof n === 'number' && Number.isFinite(n))) return 'rect must be 4 finite numbers'
      const text = textOf(o.text)
      if (text === null) return 'text is invalid'
      return { kind: 'pdf.annotate', page: o.page, rect: [rect[0] as number, rect[1] as number, rect[2] as number, rect[3] as number], text }
    }
    case 'pdf.formFill': {
      const field = shortOf(o.field)
      const value = textOf(o.value)
      if (field === null || value === null) return 'field and value are required'
      return { kind: 'pdf.formFill', field, value }
    }
    case 'text.findReplace': {
      const find = lineOf(o.find)
      if (find === null) return 'find must be non-empty single-line text'
      // 替换为空串是合法的（删除命中的文字）；多行替换引擎不支持，拒掉而不是截断
      if (typeof o.replace !== 'string' || o.replace.length > MAX_OPERATION_TEXT || /[\r\n]/.test(o.replace)) return 'replace must be single-line text'
      const options = matchOptions(o)
      return typeof options === 'string' ? options : { kind: 'text.findReplace', find, replace: o.replace, ...options }
    }
    case 'paragraph.style': {
      const find = lineOf(o.find)
      if (find === null) return 'find must be non-empty single-line text'
      const style = shortOf(o.style)
      if (style === null) return 'style is invalid'
      const options = matchOptions(o)
      return typeof options === 'string' ? options : { kind: 'paragraph.style', find, style, ...options }
    }
    case 'paragraph.insert': {
      const anchor = lineOf(o.anchor)
      if (anchor === null) return 'anchor must be non-empty single-line text'
      if (o.position !== 'before' && o.position !== 'after') return 'position must be before or after'
      const text = textOf(o.text)
      if (text === null || text === '') return 'text is invalid'
      // ★ 锚点必须恰好命中一处由引擎判；这里不接受 expectedCount，免得调用方以为能插到多处
      if (o.expectedCount !== undefined) return 'paragraph.insert does not take expectedCount; the anchor must match exactly once'
      const options = matchOptions(o)
      if (typeof options === 'string') return options
      return { kind: 'paragraph.insert', anchor, position: o.position, text, ...(options.matchCase === undefined ? {} : { matchCase: options.matchCase }) }
    }
  }
}

/** 单行、非空、有界的查找串。换行拒掉：引擎的查找是按段落内文字进行的 */
function lineOf(raw: unknown): string | null {
  const value = textOf(raw)
  return value === null || value === '' || /[\r\n]/.test(value) ? null : value
}

/** 按文字定位的公共选项。`expectedCount` 缺省 = 不核对数量（引擎仍要求至少一处） */
function matchOptions(o: Record<string, unknown>): { matchCase?: boolean; expectedCount?: number } | string {
  if (o.matchCase !== undefined && typeof o.matchCase !== 'boolean') return 'matchCase must be a boolean'
  if (o.expectedCount !== undefined && !(isIndex(o.expectedCount) && o.expectedCount >= 1)) return 'expectedCount must be a positive integer'
  return {
    ...(o.matchCase === undefined ? {} : { matchCase: o.matchCase as boolean }),
    ...(o.expectedCount === undefined ? {} : { expectedCount: o.expectedCount as number })
  }
}

function targetOf(raw: unknown): DocumentTargetRef | null {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return null
  const t = raw as Record<string, unknown>
  if (!isIndex(t.generation)) return null
  const ref = shortOf(t.ref)
  return ref === null ? null : { generation: t.generation, ref }
}

function textOf(raw: unknown): string | null {
  return typeof raw === 'string' && raw.length <= MAX_OPERATION_TEXT ? raw : null
}

function shortOf(raw: unknown): string | null {
  return typeof raw === 'string' && raw !== '' && raw.length <= 512 && !raw.includes('\0') ? raw : null
}

function isIndex(raw: unknown): raw is number {
  return typeof raw === 'number' && Number.isInteger(raw) && raw >= 0 && raw <= 1_000_000
}

// ─────────────────────────── 结果与错误 ───────────────────────────

/**
 * 稳定错误码。UI 映射成 i18n,Agent 工具原样写进 content。
 *
 * ★ `result_unknown` 与其它失败**语义不同**:它表示操作可能已经生效(引擎在回执
 * 之前崩了 / 超时了)。调用方拿到它必须先查状态,绝不能自动重放 —— 重放一次宏
 * 或一次追加段落,就是做了两遍。
 */
export type DocumentErrorCode =
  | 'unsupported_format'
  | 'unsupported_operation'
  | 'unsupported_environment'
  | 'engine_unavailable'
  | 'engine_crashed'
  | 'invalid_operation'
  | 'stale_revision'
  | 'stale_generation'
  | 'disk_conflict'
  | 'session_closed'
  | 'timeout'
  | 'result_unknown'
  /**
   * 引擎此刻不能安全执行,**什么都没做**,稍后原样重试即可。目前唯一来源:用户正在输入法
   * 组字(拼音已在模型里,此时读会读到拼音、写会被下一次组字更新吞掉、存会把拼音写进文件)。
   * ★ 不是故障:会话不标崩溃、不记 result_unknown。
   */
  | 'busy'
  | 'macro_denied'
  | 'io'

export class DocumentEngineError extends Error {
  constructor(readonly code: DocumentErrorCode, message: string) {
    super(message)
    this.name = 'DocumentEngineError'
  }
}

/** 一次修改批次的回执。`saved` 与 `applied` 分开:改了 ≠ 存了。 */
export interface DocumentApplyResult {
  operationId: string
  appliedRevision: number
  dirty: boolean
  /** 引擎回报的保真警告(例如「该对象在保存时会被简化」)。原文是领域值 */
  warnings: string[]
  undoable: boolean
  /**
   * 与请求批次一一对应的逐条结果（按文字定位的操作带 `matches`）。可选：引擎不回报时省略。
   * 需求：Agent 要知道「替换了几处」才能判断是否改对了地方；只回一个 applied 会让它
   * 在零命中之外的错误数量上无从察觉。
   */
  results?: DocumentOperationResult[]
}

/** 一条操作的结果。只保留宿主认得、且有界的字段（引擎回执是不可信输入） */
export interface DocumentOperationResult {
  matches?: number
}

/** 从引擎回执里收窄出逐条结果；形状不对或条数与批次不符时整份丢弃，不拼半份 */
export function parseOperationResults(raw: unknown, expected: number): DocumentOperationResult[] | undefined {
  if (!Array.isArray(raw) || raw.length !== expected) return undefined
  return raw.map((item) => {
    const record = item !== null && typeof item === 'object' && !Array.isArray(item) ? (item as Record<string, unknown>) : {}
    return isIndex(record.matches) ? { matches: record.matches } : {}
  })
}

// ─────────────────────────── 查询 ───────────────────────────

/**
 * 只读查询。需求:Agent 改文档之前必须先读结构(Skill 的第一步),而读取不能走
 * 普通 `Read` 工具 —— OOXML 是二进制 zip,那条路只会报「二进制文件」。
 *
 * ★ 每种查询都有字符上限:查询结果要进模型上下文,一次把整本书读进来就是把
 * 上下文窗撑爆,而用户看到的只是「这一轮失败了」。
 */
export type DocumentQuery =
  | { kind: 'outline' }
  | { kind: 'text'; maxChars?: number }
  | { kind: 'cells'; sheet: string; range: string; maxChars?: number }
  /**
   * 版面：某一部分（工作表 / 幻灯片）的文档尺寸与 Writer 的页矩形，单位 twips。
   * 需求：画布要先知道页在哪、多大，才能按视口算出 render 的 twips 区域；没有它视图只能猜。
   * `part` 只对表格 / 演示文稿有意义，由引擎核对范围。
   */
  | { kind: 'layout'; part?: number }
  /** 引擎里可用的字体族名(功能区的字体框) */
  | { kind: 'fonts' }
  /** 文档的段落样式名(功能区的样式框;只对文字文档) */
  | { kind: 'styles' }
  /** 表格当前工作表在可见区域(twips)里的行列头:每行 / 列的结束位置与标签 */
  | { kind: 'headers'; x: number; y: number; width: number; height: number }

export const MAX_QUERY_CHARS = 200_000

export function validateQuery(raw: unknown): DocumentQuery | string {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return 'query must be an object'
  const q = raw as Record<string, unknown>
  const maxChars = q.maxChars
  if (maxChars !== undefined && !(typeof maxChars === 'number' && Number.isInteger(maxChars) && maxChars > 0 && maxChars <= MAX_QUERY_CHARS)) {
    return `maxChars must be an integer between 1 and ${MAX_QUERY_CHARS}`
  }
  const limit = maxChars === undefined ? {} : { maxChars: maxChars as number }
  switch (q.kind) {
    case 'outline':
      return { kind: 'outline' }
    case 'text':
      return { kind: 'text', ...limit }
    case 'cells': {
      if (typeof q.sheet !== 'string' || q.sheet === '' || q.sheet.length > 512) return 'sheet is required'
      if (typeof q.range !== 'string' || !/^\$?[A-Z]{1,3}\$?[0-9]{1,7}(:\$?[A-Z]{1,3}\$?[0-9]{1,7})?$/.test(q.range)) return 'range must look like A1 or A1:C10'
      return { kind: 'cells', sheet: q.sheet, range: q.range, ...limit }
    }
    case 'layout':
      if (q.part !== undefined && !isIndex(q.part)) return 'part must be a non-negative integer'
      return { kind: 'layout', ...(q.part === undefined ? {} : { part: q.part as number }) }
    case 'fonts':
      return { kind: 'fonts' }
    case 'styles':
      return { kind: 'styles' }
    case 'headers': {
      const box = ['x', 'y', 'width', 'height'].map((key) => q[key])
      if (!box.every((value) => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 && value <= 1_000_000_000)) return 'x, y, width and height must be twips coordinates'
      const [x, y, width, height] = box as number[]
      return { kind: 'headers', x: x ?? 0, y: y ?? 0, width: width ?? 0, height: height ?? 0 }
    }
    default:
      return `unknown query kind ${String(q.kind)}`
  }
}
