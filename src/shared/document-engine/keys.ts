/**
 * DOM 键盘事件 → LibreOffice 键事件(`charCode` + `awt::Key` 键码 | 修饰位)。
 *
 * ## 为了什么需求建的
 *
 * 画布编辑要让快捷键与打字都像桌面 Office 一样工作,而引擎只认 LibreOffice 的键码
 * (`com.sun.star.awt.Key`,值取自 LibreOffice core 的 offapi/com/sun/star/awt/Key.idl)
 * 与修饰位(vcl/keycodes.hxx:SHIFT 0x1000、MOD1 0x2000、MOD2 0x4000、MOD3 0x8000)。
 * 这个换算是纯函数,单独测:错一个键码的表现是「Ctrl+Z 变成输入了一个 z」这类静默错误。
 *
 * ## 规则
 *
 * - **快捷键的字母 / 数字先看字符(`key`),字符不是拉丁字母 / 数字时才看物理键位(`code`)。**
 *   法语 AZERTY 上用户按的是印着 Z 的键(物理位置是 QWERTY 的 W),桌面系统按布局给出 Z,
 *   撤销照常生效 —— 只看 `code` 就会变成 Ctrl+W。俄语布局上 `key` 是 я,没有拉丁字母可认,
 *   这时退回键位,Ctrl+Z 仍是撤销(桌面 Office 在这类布局上也这样)。
 * - MOD1 = 主快捷键修饰(macOS 的 Cmd,其它平台的 Ctrl);MOD2 = Alt / Option;
 *   MOD3 = macOS 上的 Ctrl。
 * - 可打印字符且不是快捷键 → 只发 `charCode`(键码 0):这是一致性测试里实测可用的打字方式。
 *   macOS 的 Option+键、Windows / Linux 的 AltGr(Ctrl+Alt)产生的是字符,不是快捷键。
 * - 组字中(`isComposing` / `key === 'Process'`)一律返回 null:文字由组字事件送,这里再发
 *   一次就是重复输入。单独的修饰键也返回 null(引擎不需要,且会被当成一次输入)。
 *
 * ## 故意不做的
 *
 * - 不拦截浏览器 / 宿主自己的快捷键(关标签、命令面板):哪些键留给宿主由视图决定,
 *   这里只负责「如果交给引擎,该是什么值」。
 */

export const LOK_KEY = {
  NUM0: 256,
  A: 512,
  F1: 768,
  DOWN: 1024,
  UP: 1025,
  LEFT: 1026,
  RIGHT: 1027,
  HOME: 1028,
  END: 1029,
  PAGEUP: 1030,
  PAGEDOWN: 1031,
  RETURN: 1280,
  ESCAPE: 1281,
  TAB: 1282,
  BACKSPACE: 1283,
  SPACE: 1284,
  INSERT: 1285,
  DELETE: 1286,
  ADD: 1287,
  SUBTRACT: 1288,
  MULTIPLY: 1289,
  DIVIDE: 1290,
  POINT: 1291,
  COMMA: 1292,
  EQUAL: 1295,
  CONTEXTMENU: 1305,
  DECIMAL: 1309,
  QUOTELEFT: 1311,
  BRACKETLEFT: 1315,
  BRACKETRIGHT: 1316,
  SEMICOLON: 1317,
  QUOTERIGHT: 1318
} as const

export const LOK_MODIFIER = { SHIFT: 0x1000, MOD1: 0x2000, MOD2: 0x4000, MOD3: 0x8000 } as const

/** F 键上限:awt::Key 定义到 F26,浏览器最多报 F24 */
const MAX_FUNCTION_KEY = 24

/** 本函数读的 KeyboardEvent 字段。视图直接把 DOM 事件传进来即可 */
export interface DomKeyLike {
  key: string
  code: string
  shiftKey: boolean
  ctrlKey: boolean
  altKey: boolean
  metaKey: boolean
  isComposing?: boolean
}

export type KeyPlatform = 'mac' | 'other'

export interface LokKey {
  charCode: number
  keyCode: number
}

/** 以 `key` 识别的功能键(与键盘布局无关的命名键) */
const NAMED_KEYS: Readonly<Record<string, number>> = {
  ArrowDown: LOK_KEY.DOWN,
  ArrowUp: LOK_KEY.UP,
  ArrowLeft: LOK_KEY.LEFT,
  ArrowRight: LOK_KEY.RIGHT,
  Home: LOK_KEY.HOME,
  End: LOK_KEY.END,
  PageUp: LOK_KEY.PAGEUP,
  PageDown: LOK_KEY.PAGEDOWN,
  Enter: LOK_KEY.RETURN,
  Escape: LOK_KEY.ESCAPE,
  Tab: LOK_KEY.TAB,
  Backspace: LOK_KEY.BACKSPACE,
  Insert: LOK_KEY.INSERT,
  Delete: LOK_KEY.DELETE,
  ContextMenu: LOK_KEY.CONTEXTMENU
}

/** 以 `code`(物理键位)识别的符号键 / 小键盘键 */
const CODE_KEYS: Readonly<Record<string, number>> = {
  Space: LOK_KEY.SPACE,
  Minus: LOK_KEY.SUBTRACT,
  Equal: LOK_KEY.EQUAL,
  Comma: LOK_KEY.COMMA,
  Period: LOK_KEY.POINT,
  Slash: LOK_KEY.DIVIDE,
  Semicolon: LOK_KEY.SEMICOLON,
  Quote: LOK_KEY.QUOTERIGHT,
  Backquote: LOK_KEY.QUOTELEFT,
  BracketLeft: LOK_KEY.BRACKETLEFT,
  BracketRight: LOK_KEY.BRACKETRIGHT,
  NumpadAdd: LOK_KEY.ADD,
  NumpadSubtract: LOK_KEY.SUBTRACT,
  NumpadMultiply: LOK_KEY.MULTIPLY,
  NumpadDivide: LOK_KEY.DIVIDE,
  NumpadDecimal: LOK_KEY.DECIMAL,
  NumpadEnter: LOK_KEY.RETURN
}

const MODIFIER_ONLY = new Set(['Shift', 'Control', 'Alt', 'AltGraph', 'Meta', 'OS', 'CapsLock', 'NumLock', 'ScrollLock', 'Fn', 'Hyper', 'Super'])

/** 物理键位 → awt::Key。不认识返回 0 */
function keyCodeOf(event: DomKeyLike): number {
  const named = NAMED_KEYS[event.key]
  if (named !== undefined) return named
  // 字母 / 数字:先认字符(随键盘布局),认不出再认键位(见文件头「规则」)
  const letter = /^[a-zA-Z]$/.exec(event.key)?.[0] ?? /^Key([A-Z])$/.exec(event.code)?.[1]
  if (letter !== undefined) return LOK_KEY.A + letter.toUpperCase().charCodeAt(0) - 65
  const digit = /^[0-9]$/.exec(event.key)?.[0] ?? /^(?:Digit|Numpad)([0-9])$/.exec(event.code)?.[1]
  if (digit !== undefined) return LOK_KEY.NUM0 + Number(digit)
  const fn = /^F([0-9]{1,2})$/.exec(event.key)
  if (fn !== null) {
    const n = Number(fn[1])
    if (n >= 1 && n <= MAX_FUNCTION_KEY) return LOK_KEY.F1 + n - 1
  }
  return CODE_KEYS[event.code] ?? 0
}

/** `key` 是不是一个可打印字符(恰好一个码点,且不是控制字符) */
function printableCodePoint(key: string): number | null {
  const chars = [...key]
  if (chars.length !== 1) return null
  const cp = chars[0]?.codePointAt(0)
  if (cp === undefined || cp < 0x20 || (cp >= 0x7f && cp < 0xa0)) return null
  return cp
}

/**
 * 换算一次按键。返回 null = 不要交给引擎(组字中、单独修饰键、认不出的组合)。
 * 按下与抬起用同一个结果,分别作为 `press` / `release` 发送。
 */
export function lokKeyOf(event: DomKeyLike, platform: KeyPlatform): LokKey | null {
  if (event.isComposing === true || event.key === 'Process' || event.key === 'Dead') return null
  if (MODIFIER_ONLY.has(event.key)) return null
  const mac = platform === 'mac'
  // Windows / Linux 的 AltGr 在 DOM 里是 Ctrl+Alt 同时按下,产出的是字符
  const altGraph = !mac && event.ctrlKey && event.altKey
  const mod1 = mac ? event.metaKey : event.ctrlKey && !altGraph
  const mod3 = mac && event.ctrlKey
  // 非 macOS 上的 Win / Super 键组合归操作系统,不交给引擎
  if (!mac && event.metaKey) return null
  const cp = printableCodePoint(event.key)
  // macOS 的 Option+键产出字符(å、∑),是打字不是快捷键;其它平台的单独 Alt+键是快捷键(菜单加速键)
  const shortcut = mod1 || mod3 || (event.altKey && !mac && !altGraph)
  if (cp !== null && !shortcut) return { charCode: cp, keyCode: 0 }

  const base = keyCodeOf(event)
  if (base === 0) return null
  let modifiers = 0
  if (event.shiftKey) modifiers |= LOK_MODIFIER.SHIFT
  if (mod1) modifiers |= LOK_MODIFIER.MOD1
  if (event.altKey && !altGraph) modifiers |= LOK_MODIFIER.MOD2
  if (mod3) modifiers |= LOK_MODIFIER.MOD3
  return { charCode: 0, keyCode: base | modifiers }
}
