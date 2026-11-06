/**
 * 贡献点的**合并规则** —— 纯函数,主进程与渲染层共用,可在 node 环境直测。
 *
 * 这个文件回答的是「内置项与插件项怎么排在一起」,而不是「怎么画」。
 * 分开是因为排序规则里有几条是**安全规则**,它们必须能被单测钉住:
 *
 * 1. 插件项**永远排在同 group 的内置项之后**。即使清单里写 `"group": "create@1"`,
 *    宿主也把它 clamp 到内置项之后 —— 装十个插件也抢不走「新建对话」第一位。
 * 2. 单插件在单个菜单**最多 3 项**,超出的折叠进二级菜单。
 * 3. 图标来自**白名单枚举**,不是任意字符串更不是 SVG:前者让插件没法把整个
 *    lucide 打进 bundle,后者挡掉「用一个看起来像系统图标的图标钓鱼」。
 */
import type { PluginMenuContribution } from './manifest'

// ─────────────────────────── 图标白名单 ───────────────────────────

/**
 * 插件能用的图标名。**闭集**,渲染层有一张同名的 `Record<MenuIconName, LucideIcon>`。
 *
 * 选这一批的标准是「菜单里真的会用到」:文件类、编辑类、运行类、视图类各几个。
 * 不够用时加一个名字进来是一行改动,而把口子开成「任意 lucide 名」就等于
 * 让渲染层静态引入整个图标库(1MB+),或者引入动态 import 的一整套加载路径。
 */
export const MENU_ICON_NAMES = [
  'file', 'file-text', 'files', 'folder', 'image',
  'pen-tool', 'pencil', 'eye', 'search', 'terminal',
  'message-square', 'globe', 'git-branch', 'clock', 'settings',
  'play', 'square', 'plus', 'download', 'upload',
  'package', 'puzzle', 'sparkles', 'wrench', 'bug',
  'chart', 'table', 'link', 'bookmark', 'shield',
  // 网页应用(`contributes.webApps`)那一类常用的媒体/娱乐图标:
  // 少了它们,一个视频站插件在侧边栏上只能挂拼图块。
  'tv', 'film', 'music', 'gamepad'
] as const

export type MenuIconName = (typeof MENU_ICON_NAMES)[number]

const ICON_SET: ReadonlySet<string> = new Set<string>(MENU_ICON_NAMES)

/** 认不出的图标名回落到 `puzzle`(拼图块 = 「这是个插件」),不是留空。 */
export function normalizeMenuIcon(value: string | undefined): MenuIconName {
  return value !== undefined && ICON_SET.has(value) ? (value as MenuIconName) : 'puzzle'
}

// ─────────────────────────── 菜单项 ───────────────────────────

/** 宿主这一版认得的菜单挂载点。不在表里的贡献进 unsupported 诊断。 */
export const MENU_IDS = [
  'tabBar/new',
  'tabBar/context',
  'explorer/context',
  'explorer/new',
  'sidebar/nav',
  'chat/composer',
  'commandPalette'
] as const

export type MenuId = (typeof MENU_IDS)[number]

export function isMenuId(value: string): value is MenuId {
  return (MENU_IDS as readonly string[]).includes(value)
}

/** 内置菜单的分组。分隔线由 **group 边界自动生成**,不是数据里的 `separatorBefore`。 */
export const MENU_GROUPS = ['view', 'create', 'tools', 'plugin'] as const
export type MenuGroup = (typeof MENU_GROUPS)[number]

export interface TabMenuItem {
  /** 稳定 id:内置 `builtin.chat`,插件 `<pluginId>:<commandId>` */
  id: string
  /** ★ key 不是文案。内置的是 `TranslationKey`,插件的是 `plugin.<id>.<key>` */
  titleKey: string
  icon: MenuIconName
  accelerator?: string
  group: MenuGroup
  /** 组内次序。插件项会被 clamp 到内置项之后,见 `mergeMenuItems` */
  order: number
  /** 在哪几格出现。省略 = 三格都出 */
  panes?: readonly string[]
  /** 贡献者。`undefined` = 内置 */
  pluginId?: string
  /** 这一项当 `when` 求值为假时不出现 */
  when?: string
  /**
   * 命令声明的品牌图标(`iconFile` 经主进程读出的 data URL)。
   *
   * ★ `undefined` = 走 `icon` 名字查 `MENU_ICON`。名字闭集的防钓鱼理由见
   * `renderer/shell/icons.tsx`;`iconFile` 这条并行通道的放宽边界也记在那里。
   */
  iconUrl?: string
  action:
    | { kind: 'openTab'; tabKind: string }
    | { kind: 'command'; commandId: string }
}

/** 插件项在组内的起始序号。内置项的 order 一律小于它。 */
export const PLUGIN_ORDER_BASE = 1000

/** 单个插件在单个菜单里最多直出几项,超出折叠成二级菜单。 */
export const MAX_ITEMS_PER_PLUGIN = 3

export interface MergedMenu {
  /** 直接铺在菜单里的那些,已按 group → order 排好 */
  items: TabMenuItem[]
  /**
   * 折叠进二级菜单的那些:`pluginId → 该插件溢出的项`。
   *
   * ★ 溢出的**不丢弃**。丢弃意味着插件作者写了 5 项、装上只看见 3 项,
   * 而且没有任何地方告诉他另外两项去哪了。
   */
  overflow: { pluginId: string; items: TabMenuItem[] }[]
}

/**
 * 把内置项与插件项合成一份菜单。
 *
 * ★ **clamp 发生在这里,不在渲染层。** 渲染层只按数组顺序铺;把「插件排不到
 * 内置前面」这条规则留给渲染层,等于把一条安全规则托付给一个没人会为它写
 * 测试的地方。
 */
export function mergeMenuItems(
  builtin: readonly TabMenuItem[],
  contributed: readonly TabMenuItem[]
): MergedMenu {
  // 每个 group 里内置项的最大 order —— 插件项从它之后开始排。
  const builtinMax = new Map<string, number>()
  for (const item of builtin) {
    builtinMax.set(item.group, Math.max(builtinMax.get(item.group) ?? 0, item.order))
  }

  const perPlugin = new Map<string, TabMenuItem[]>()
  for (const item of contributed) {
    const pluginId = item.pluginId ?? ''
    const list = perPlugin.get(pluginId) ?? []
    list.push(item)
    perPlugin.set(pluginId, list)
  }

  const items: TabMenuItem[] = [...builtin]
  const overflow: MergedMenu['overflow'] = []

  for (const [pluginId, list] of perPlugin) {
    const clamped = list.map((item) => ({
      ...item,
      // ★ 即使写了 `create@1`,也落在该 group 内置项之后。
      order: Math.max(item.order, (builtinMax.get(item.group) ?? 0) + PLUGIN_ORDER_BASE)
    }))
    items.push(...clamped.slice(0, MAX_ITEMS_PER_PLUGIN))
    const rest = clamped.slice(MAX_ITEMS_PER_PLUGIN)
    if (rest.length > 0) overflow.push({ pluginId, items: rest })
  }

  items.sort(byGroupThenOrder)
  return { items, overflow }
}

const GROUP_ORDER = new Map(MENU_GROUPS.map((group, index) => [group, index]))

export function byGroupThenOrder(a: TabMenuItem, b: TabMenuItem): number {
  const ga = GROUP_ORDER.get(a.group) ?? Number.MAX_SAFE_INTEGER
  const gb = GROUP_ORDER.get(b.group) ?? Number.MAX_SAFE_INTEGER
  if (ga !== gb) return ga - gb
  if (a.order !== b.order) return a.order - b.order
  // 同组同序时按 id —— 目录遍历顺序在不同平台上不一样,不定序会让菜单抖。
  return a.id.localeCompare(b.id)
}

/** 相邻两项跨了 group 边界吗 —— 渲染层据此画分隔线,而不是数 `i === 3`。 */
export function needsSeparator(previous: TabMenuItem | undefined, current: TabMenuItem): boolean {
  return previous !== undefined && previous.group !== current.group
}

/**
 * 把清单里的 `"group": "create@20"` 拆成 group + order。
 *
 * 认不出的 group 一律归到 `plugin`(而不是丢掉这一项):插件写了个错别字,
 * 菜单项应该出现在「插件」那一组里,而不是凭空消失。
 */
export function parseMenuGroup(raw: string | undefined): { group: MenuGroup; order: number } {
  if (raw === undefined || raw === '') return { group: 'plugin', order: PLUGIN_ORDER_BASE }
  const at = raw.lastIndexOf('@')
  const name = at === -1 ? raw : raw.slice(0, at)
  const order = at === -1 ? Number.NaN : Number(raw.slice(at + 1))
  const group = (MENU_GROUPS as readonly string[]).includes(name) ? (name as MenuGroup) : 'plugin'
  return { group, order: Number.isFinite(order) ? order : PLUGIN_ORDER_BASE }
}

/** 一条清单里的菜单贡献 → 一个可渲染的菜单项。 */
export function toMenuItem(
  pluginId: string,
  contribution: PluginMenuContribution,
  titleKey: string,
  icon: string | undefined
): TabMenuItem {
  const { group, order } = parseMenuGroup(contribution.group)
  return {
    id: `${pluginId}:${contribution.command}`,
    titleKey,
    icon: normalizeMenuIcon(icon),
    group,
    order,
    pluginId,
    ...(contribution.when === undefined ? {} : { when: contribution.when }),
    action: { kind: 'command', commandId: contribution.command }
  }
}

// ─────────────────────────── `when` 求值 ───────────────────────────

export type WhenContext = Record<string, string | number | boolean | undefined>

/**
 * 最小 `when` 求值器 —— 只支持 `==` `!=` `&&` `||` `!` `in` 和括号。
 *
 * ★ **不引表达式引擎,也不用 `new Function`。** 后者会把菜单条件变成一条
 * 任意代码执行通道(`when` 来自插件清单,是不可信输入);前者对这点语法量
 * 是杀鸡用牛刀,而且多一个需要跟着升级的依赖。
 *
 * ★ **读不懂就返回 `false`**,不是 `true`。读不懂时显示菜单项意味着一条
 * 本该被条件挡住的操作出现在了不该出现的地方 —— 宁可少一项。
 */
export function evaluateWhen(expression: string | undefined, context: WhenContext): boolean {
  if (expression === undefined || expression.trim() === '') return true
  try {
    const tokens = tokenizeWhen(expression)
    const parser = new WhenParser(tokens, context)
    const value = parser.parseOr()
    return parser.done() ? value : false
  } catch {
    return false
  }
}

type WhenToken = { kind: 'op'; value: string } | { kind: 'atom'; value: string }

function tokenizeWhen(input: string): WhenToken[] {
  const tokens: WhenToken[] = []
  let i = 0
  while (i < input.length) {
    const ch = input[i] as string
    if (/\s/.test(ch)) { i += 1; continue }
    if (input.startsWith('&&', i) || input.startsWith('||', i) || input.startsWith('==', i) || input.startsWith('!=', i)) {
      tokens.push({ kind: 'op', value: input.slice(i, i + 2) })
      i += 2
      continue
    }
    if (ch === '(' || ch === ')' || ch === '!') { tokens.push({ kind: 'op', value: ch }); i += 1; continue }
    if (ch === "'" || ch === '"') {
      const end = input.indexOf(ch, i + 1)
      if (end === -1) throw new Error('unterminated string')
      tokens.push({ kind: 'atom', value: input.slice(i + 1, end) })
      i = end + 1
      continue
    }
    let j = i
    while (j < input.length && /[^\s()!=&|]/.test(input[j] as string)) j += 1
    if (j === i) throw new Error(`unexpected character: ${ch}`)
    tokens.push({ kind: 'atom', value: input.slice(i, j) })
    i = j
  }
  return tokens
}

class WhenParser {
  private index = 0
  constructor(private readonly tokens: WhenToken[], private readonly context: WhenContext) {}

  done(): boolean { return this.index >= this.tokens.length }

  parseOr(): boolean {
    let left = this.parseAnd()
    while (this.peekOp('||')) { this.index += 1; left = this.parseAnd() || left }
    return left
  }

  private parseAnd(): boolean {
    let left = this.parseComparison()
    while (this.peekOp('&&')) { this.index += 1; left = this.parseComparison() && left }
    return left
  }

  private parseComparison(): boolean {
    if (this.peekOp('!')) { this.index += 1; return !this.parseComparison() }
    if (this.peekOp('(')) {
      this.index += 1
      const value = this.parseOr()
      if (!this.peekOp(')')) throw new Error('missing )')
      this.index += 1
      return value
    }
    const left = this.nextAtom()
    if (this.peekOp('==') || this.peekOp('!=')) {
      const op = (this.tokens[this.index] as { value: string }).value
      this.index += 1
      const right = this.nextAtom()
      const equal = String(this.resolve(left)) === String(this.resolveLiteral(right))
      return op === '==' ? equal : !equal
    }
    if (left === 'in') throw new Error('unexpected in')
    /*
      ★ 光秃秃一个名字时**只查 context,不回落成字面量**。回落的话
      `when: "isDrity"`(拼错了)会解析成非空字符串、于是恒为真 ——
      一条本该有条件的菜单项变成无条件显示,而这正是 `when` 唯一要防的事。
    */
    const value = this.context[left]
    return value !== undefined && value !== false && value !== '' && value !== 0
  }

  private nextAtom(): string {
    const token = this.tokens[this.index]
    if (token === undefined || token.kind !== 'atom') throw new Error('expected an atom')
    this.index += 1
    return token.value
  }

  private peekOp(op: string): boolean {
    const token = this.tokens[this.index]
    return token !== undefined && token.kind === 'op' && token.value === op
  }

  /** 左值先当 context key 解析,查不到再当字面量。 */
  private resolve(name: string): string | number | boolean | undefined {
    if (name in this.context) return this.context[name]
    return this.resolveLiteral(name)
  }

  private resolveLiteral(raw: string): string | number | boolean {
    if (raw === 'true') return true
    if (raw === 'false') return false
    const n = Number(raw)
    return Number.isFinite(n) && raw.trim() !== '' ? n : raw
  }
}
