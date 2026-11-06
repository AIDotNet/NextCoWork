/**
 * 交互输入协议 —— 画布上的键盘、鼠标、输入法事件如何进入活动文档模型。
 *
 * ## 为了什么需求建的
 *
 * WPS 式编辑器要让用户直接在画布上打字、点选、用中文输入法,而输入必须落在**与 Agent
 * 修改同一个**活动模型里(计划 §4.2 / §7.2)。视图把 DOM 事件换算成引擎的键码与文档坐标
 * (twips),经会话管理器的串行队列交给 helper;helper 回报「哪里要重画、光标 / 选区在哪、
 * 这批输入有没有改模型」。这个文件只定义并收窄这两端的形状,不含任何换算或调度。
 *
 * ## 不变式
 *
 * - 输入是**不可信**的(来自插件视图):整批先校验,一条不合法整批拒绝,一条都不投递 ——
 *   否则「前半批进了模型、后半批被拒」时,视图无从知道模型停在哪一步。
 * - 回执同样不可信(来自原生 helper):形状不对返回 `null`,由调用方按引擎故障处理。
 *   ★ 不能「尽量解析」出一个 `modified: false`:漏记一次真实修改,Agent 就会基于旧内容
 *   改写,覆盖掉用户刚打的字,且零报错。
 * - 失效矩形解析不了时退化为整体重画(`all: true`),而不是丢掉那个矩形 —— 丢掉的表现是
 *   画布上残留旧字形。
 *
 * ## 故意不做的
 *
 * - 不接受任意 `.uno:` 命令串。功能区命令另走受审的有限注册表(计划 §5.1),不从这里放行。
 * - 不给输入分配 operationId / 回执表:键入没有「重试」语义,超时即按引擎故障处理。
 *   用户输入成为可查询的事务是计划 §5.2 的后续工作。
 */

import { parseCommandStates, type DocumentCommandId } from './commands'

/** 引擎对交互输入的支持。缺省 = 不支持画布编辑(只读预览)。 */
export interface DocumentInteraction {
  keyboard: boolean
  mouse: boolean
  textInput: boolean
  /**
   * 引擎收 `viewport`(客户端可见区域)。★ 旧版引擎不认识这种事件,会把**整批**拒掉 ——
   *   连同同一批里的按键;所以只在引擎声明了它时才发、才放行。
   */
  visibleArea?: boolean
}

/**
 * 一条输入事件。坐标是文档坐标(twips),键码是 LibreOffice 的 `awt::Key` 值 ——
 * 由视图换算,这里只校验范围。
 */
export type DocumentInputEvent =
  | { type: 'key'; action: 'press' | 'release'; charCode: number; keyCode: number }
  | { type: 'mouse'; action: 'down' | 'up' | 'move'; x: number; y: number; count: number; buttons: number; modifier: number }
  /**
   * compose = 输入法组字中(不进模型);commit = 提交最终文字。组字传空串 = 取消并结束组字。
   * 组字进行中引擎只收 text 事件,键鼠 / 切换部分会被整批拒绝(先提交或取消)。
   */
  | { type: 'text'; action: 'compose' | 'commit'; text: string }
  /** 切换当前工作表 / 幻灯片(之后的点选与键入落在这一部分上) */
  | { type: 'part'; part: number }
  /**
   * 客户端可见区域(twips)。引擎据此按真实可见高度翻页(PageDown);组字中途也可以发。
   * 注意:表格的可滚动范围**不**跟着它长(实测只跟着单元格光标长),那由画布自己留余量。
   */
  | { type: 'viewport'; x: number; y: number; width: number; height: number }

/** 文档坐标里的矩形(twips)。`part` 只在失效矩形上出现 */
export interface DocumentRect {
  x: number
  y: number
  width: number
  height: number
  part?: number
}

/**
 * 一批输入的回执。可选字段只在**变过**时出现:视图据「字段是否出现」决定要不要更新
 * 对应图层,出现 `null` 表示「现在没有」(例如光标离开了单元格编辑)。
 */
export interface DocumentInputResult {
  /** 这批输入是否改了模型(引擎按撤销栈判定)。只有 true 才推进 modelRevision */
  modified: boolean
  /**
   * 这批之后用户是否仍在输入法组字中。组字期间 Agent 的读写与保存会收到 `busy`
   * (拼音此刻就在模型里);视图若要离开(关页、切走),须先发一次空串组字取消它,
   * 否则 Agent 会一直被挡。缺省 = 引擎不回报。
   */
  composing?: boolean
  /**
   * 变过的功能区命令状态:命令 id → 值(加粗是否按下、当前字体 / 字号)。
   * 只在变过时出现;画布打开后的第一次空批次拉取会补发全量(helper 的 currentStates_)。
   */
  states?: Partial<Record<DocumentCommandId, string>>
  /** 表格:当前单元格的公式原文(公式栏)与地址(名称框)。只在变过时出现 */
  cellFormula?: string
  cellAddress?: string
  /** 表格:行高列宽变了,行列头要重取 */
  headersChanged?: boolean
  invalidations: { all: boolean; rects: DocumentRect[] }
  cursor?: DocumentRect | null
  cursorVisible?: boolean
  selection?: DocumentRect[]
  cellCursor?: DocumentRect | null
  documentSizeChanged?: boolean
  /** 用户视图当前的工作表 / 幻灯片(引擎量出来的,不只是 SET_PART 回调)。只在变过时出现 */
  part?: number
  /**
   * 工作表 / 幻灯片数变了(新建、删除、Agent 插入)。只在变过时出现。
   * 需求:缩略图栏与工作表标签据此重取 —— 否则删掉的幻灯片还留在栏里,点它就是越界。
   */
  parts?: number
}

// 与 ncw-office-runtime helper 的上限一致;宿主先挡,不让越界请求进到引擎队列
export const MAX_INPUT_EVENTS = 256
export const MAX_INPUT_TEXT_BYTES = 4096
const MAX_COORDINATE = 2 ** 30
const MAX_CODE_POINT = 0x10ffff
const MAX_KEY_CODE = 0xffff
const MAX_MODIFIER = 0xf000
// 回执侧的上限比 helper 自己的上限宽:helper 超过 64 个失效矩形就已退化为整体重画
const MAX_RESULT_RECTS = 256
const MAX_PART = 100_000

const textEncoder = new TextEncoder()

function recordOf(raw: unknown): Record<string, unknown> | null {
  return raw !== null && typeof raw === 'object' && !Array.isArray(raw) ? (raw as Record<string, unknown>) : null
}

function intIn(raw: unknown, min: number, max: number): raw is number {
  return typeof raw === 'number' && Number.isSafeInteger(raw) && raw >= min && raw <= max
}

/** helper 回报的 `capabilities.interaction` → 宿主形状。三项全 false 等于不支持,返回 undefined */
export function parseInteraction(raw: unknown): DocumentInteraction | undefined {
  const record = recordOf(raw)
  if (record === null) return undefined
  const interaction: DocumentInteraction = { keyboard: record.keyboard === true, mouse: record.mouse === true, textInput: record.textInput === true }
  if (record.visibleArea === true) interaction.visibleArea = true
  return interaction.keyboard || interaction.mouse || interaction.textInput ? interaction : undefined
}

export type InputValidation = { ok: true; events: DocumentInputEvent[] } | { ok: false; reason: string }

/**
 * 校验并收窄一批输入。空数组合法:视图用它取回输入之后才到的迟到事件(自动更正、重排)。
 * 引擎没声明的输入种类按 `unsupported_operation` 拒绝(reason 以它开头,调用方据此分错误码)。
 */
export function validateInputEvents(raw: unknown, interaction: DocumentInteraction): InputValidation {
  if (!Array.isArray(raw)) return { ok: false, reason: 'events must be an array' }
  if (raw.length > MAX_INPUT_EVENTS) return { ok: false, reason: `at most ${MAX_INPUT_EVENTS} input events per batch` }
  const events: DocumentInputEvent[] = []
  for (const [index, item] of raw.entries()) {
    const event = validateOne(item, interaction)
    if (typeof event === 'string') return { ok: false, reason: `event ${index}: ${event}` }
    events.push(event)
  }
  return { ok: true, events }
}

function validateOne(raw: unknown, interaction: DocumentInteraction): DocumentInputEvent | string {
  const e = recordOf(raw)
  if (e === null) return 'must be an object'
  switch (e.type) {
    case 'key': {
      if (!interaction.keyboard) return 'unsupported_operation: keyboard input'
      if (e.action !== 'press' && e.action !== 'release') return 'key action must be press or release'
      const charCode = e.charCode ?? 0
      const keyCode = e.keyCode ?? 0
      if (!intIn(charCode, 0, MAX_CODE_POINT)) return 'charCode is invalid'
      if (!intIn(keyCode, 0, MAX_KEY_CODE)) return 'keyCode is invalid'
      if (charCode === 0 && keyCode === 0) return 'key event needs a charCode or a keyCode'
      return { type: 'key', action: e.action, charCode, keyCode }
    }
    case 'mouse': {
      if (!interaction.mouse) return 'unsupported_operation: mouse input'
      if (e.action !== 'down' && e.action !== 'up' && e.action !== 'move') return 'mouse action must be down, up or move'
      if (!intIn(e.x, 0, MAX_COORDINATE) || !intIn(e.y, 0, MAX_COORDINATE)) return 'x and y must be document coordinates'
      const count = e.count ?? 1
      const buttons = e.buttons ?? 1
      const modifier = e.modifier ?? 0
      if (!intIn(count, 1, 3) || !intIn(buttons, 0, 7) || !intIn(modifier, 0, MAX_MODIFIER)) return 'count, buttons or modifier is invalid'
      return { type: 'mouse', action: e.action, x: e.x, y: e.y, count, buttons, modifier }
    }
    case 'text': {
      if (!interaction.textInput) return 'unsupported_operation: text input'
      if (e.action !== 'compose' && e.action !== 'commit') return 'text action must be compose or commit'
      if (typeof e.text !== 'string' || textEncoder.encode(e.text).byteLength > MAX_INPUT_TEXT_BYTES) return `text must be a string of at most ${MAX_INPUT_TEXT_BYTES} bytes`
      // 组字允许空串(取消组字);提交空串没有意义,而引擎会把它当成「提交当前组字」
      if (e.action === 'commit' && e.text === '') return 'commit text must not be empty'
      return { type: 'text', action: e.action, text: e.text }
    }
    case 'part': {
      if (!interaction.keyboard && !interaction.mouse) return 'unsupported_operation: part switching'
      // 范围上限由引擎核对(工作表 / 幻灯片数随编辑变化),这里只收窄形状
      if (!intIn(e.part, 0, MAX_PART)) return 'part must be a non-negative integer'
      return { type: 'part', part: e.part }
    }
    case 'viewport': {
      if (interaction.visibleArea !== true) return 'unsupported_operation: visible area'
      if (!intIn(e.x, 0, MAX_COORDINATE) || !intIn(e.y, 0, MAX_COORDINATE)) return 'x and y must be document coordinates'
      if (!intIn(e.width, 1, MAX_COORDINATE) || !intIn(e.height, 1, MAX_COORDINATE)) return 'width and height must be positive document lengths'
      return { type: 'viewport', x: e.x, y: e.y, width: e.width, height: e.height }
    }
    default:
      return 'unknown input event type'
  }
}

function rectOf(raw: unknown, withPart: boolean): DocumentRect | null {
  const r = recordOf(raw)
  if (r === null) return null
  if (!intIn(r.x, -MAX_COORDINATE, MAX_COORDINATE) || !intIn(r.y, -MAX_COORDINATE, MAX_COORDINATE)) return null
  if (!intIn(r.width, 0, MAX_COORDINATE) || !intIn(r.height, 0, MAX_COORDINATE)) return null
  const rect: DocumentRect = { x: r.x, y: r.y, width: r.width, height: r.height }
  if (withPart && r.part !== undefined) {
    if (!intIn(r.part, 0, MAX_PART)) return null
    rect.part = r.part
  }
  return rect
}

/** 可选的单个矩形:缺省 = 未变(undefined);null = 现在没有;形状不对 = 当作没有 */
function optionalRect(record: Record<string, unknown>, key: string): { value: DocumentRect | null } | undefined {
  if (!(key in record)) return undefined
  return { value: record[key] === null ? null : rectOf(record[key], false) }
}

/**
 * 收窄 helper 的输入回执。`modified` 不是布尔 / 失效信息缺失 → 返回 `null`(引擎故障)。
 * 其余字段尽量保留:光标 / 选区是纯展示,解析不了就当作「现在没有」,不影响模型账目。
 */
export function parseInputResult(raw: unknown): DocumentInputResult | null {
  const record = recordOf(raw)
  if (record === null || typeof record.modified !== 'boolean') return null
  const inv = recordOf(record.invalidations)
  if (inv === null || !Array.isArray(inv.rects)) return null
  let all = inv.all === true
  const rects: DocumentRect[] = []
  if (!all) {
    for (const item of inv.rects) {
      const rect = rectOf(item, true)
      // ★ 解析不了的失效矩形 → 整体重画,不能丢:丢掉的那块会一直显示旧内容
      if (rect === null || rects.length >= MAX_RESULT_RECTS) { all = true; rects.length = 0; break }
      rects.push(rect)
    }
  }
  const result: DocumentInputResult = { modified: record.modified, invalidations: { all, rects } }
  if (typeof record.composing === 'boolean') result.composing = record.composing
  const states = parseCommandStates(record.states)
  if (states !== undefined) result.states = states
  if (typeof record.cellFormula === 'string') result.cellFormula = record.cellFormula.slice(0, 32767)
  if (typeof record.cellAddress === 'string' && record.cellAddress.length <= 64) result.cellAddress = record.cellAddress
  if (record.headersChanged === true) result.headersChanged = true
  const cursor = optionalRect(record, 'cursor')
  if (cursor !== undefined) result.cursor = cursor.value
  if (typeof record.cursorVisible === 'boolean') result.cursorVisible = record.cursorVisible
  if (Array.isArray(record.selection)) {
    result.selection = record.selection.slice(0, MAX_RESULT_RECTS).map((item) => rectOf(item, false)).filter((rect): rect is DocumentRect => rect !== null)
  }
  const cellCursor = optionalRect(record, 'cellCursor')
  if (cellCursor !== undefined) result.cellCursor = cellCursor.value
  if (record.documentSizeChanged === true) result.documentSizeChanged = true
  if (intIn(record.part, 0, MAX_PART)) result.part = record.part
  if (intIn(record.parts, 1, MAX_PART)) result.parts = record.parts
  return result
}
