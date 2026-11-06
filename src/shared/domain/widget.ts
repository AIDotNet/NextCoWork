/**
 * 内置可视化 widget 的**契约常量** —— scheme、外壳地址、两边共用的消息名。
 *
 * 需求:这三个东西在**两个进程、两个 origin** 上各有一份使用者(main 侧
 * `net/widget-protocol.ts` 与 `net/widget-shell/runtime.ts`,渲染层
 * `shell/WidgetFrame.tsx`),而它们必须逐字一致。分别写死的话,漂移的表现是
 * "框出来了、里面永远是空的"这类零报错的故障 —— 与本仓库对
 * `ncw-plugin://` 那两处 CSP 的处理是同一个道理(`renderer/index.html` 里
 * 那条注释:"两处 CSP 都要有它,只改一处的话开发时正常、打包后图片全不显示,
 * 而且不报错")。
 *
 * ★ 这里**只放常量与类型**。who serves what、CSP 里允许哪些域名这类策略,
 * 留在各自的实现里:那些是"必须能一眼看出被谁改过"的东西。
 */

/** widget 外壳的 scheme。与 `ncw-plugin://` / `ncw://` 并列的第三条自定义 scheme。 */
export const WIDGET_SCHEME = 'ncw-widget'

/** 外壳页面的 host 段。协议层只认这一个 —— 它不是插件 id,不需要可变。 */
export const WIDGET_SHELL_HOST = 'shell'

export const WIDGET_SHELL_PATH = '/index.html'

/** iframe 的 `src`。渲染层用它,协议层用 host + path 判断该不该响应。 */
export const WIDGET_SHELL_URL = `${WIDGET_SCHEME}://${WIDGET_SHELL_HOST}${WIDGET_SHELL_PATH}`

/**
 * postMessage 的类型名。
 *
 * 前缀与插件卡片一致(`ncw:`),后缀分成两段:载体(widget)→ 方向。
 * 前三个是宿主 → 外壳,后两个是外壳 → 宿主。
 */
export const WIDGET_MESSAGE = {
  /** 宿主 → 外壳:一段(可能还没写完的)HTML + 是否已收尾 */
  content: 'ncw:widget:content',
  /** 宿主 → 外壳:主题变量(已算好的具体色值)与外观 */
  theme: 'ncw:widget:theme',
  /** 宿主 → 外壳:当前那条加载提示 */
  loading: 'ncw:widget:loading',
  /** 外壳 → 宿主:运行时已就绪,可以推了 */
  ready: 'ncw:widget:ready',
  /** 外壳 → 宿主:内容高度,宿主钳制后设给 iframe */
  height: 'ncw:widget:height',
  /** 宿主 → 外壳:这张卡的环境(状态 / 数据 / 交互 / 动作),见 `WidgetEnvironment` */
  env: 'ncw:widget:env',
  /** 外壳 → 宿主:请求一项宿主能力(`WidgetHostMethod`),带请求 id */
  call: 'ncw:widget:call',
  /** 宿主 → 外壳:上面那次请求的结果 */
  reply: 'ncw:widget:reply'
} as const

/** 外壳报上来的高度。`height` 是未钳制的原始值 —— 钳制由宿主做。 */
export interface WidgetHeightMessage {
  type: typeof WIDGET_MESSAGE.height
  height: number
}

/** 宿主推下去的内容帧。`final` 为真时外壳会执行脚本,只做一次。 */
export interface WidgetContentMessage {
  type: typeof WIDGET_MESSAGE.content
  html: string
  final: boolean
  /** 缺省 = html/svg(旧外壳的行为)。`tsx` 时 `html` 里是编译后的 CJS,只在 `final` 时下发 */
  format?: WidgetFormat
}

/**
 * `WIDGET_MESSAGE` 的形状 —— **给 widget 外壳的运行时用**。
 *
 * ★ 存在理由很具体:那份运行时是 iframe 里的一条**自包含**脚本
 * (`ncw-widget://shell/runtime.js`,由 main 段的第二个入口产出),它**不能**
 * 按值 import 这个文件 —— 一旦按值 import,rollup 会把这个模块提到两个入口
 * 共用的 chunk 里,而 iframe 永远取不到那个 chunk(协议只服务固定两个路径)。
 * 于是它在自己的文件里重写一遍那五个字符串,再用 `satisfies` 挂到这个类型上:
 * 编译期保证两边逐字一致,而产物里一行 import 都没有。
 *
 * 删掉这个类型,下一个人就会把运行时的 import 改回按值 —— 那一次改动**构建
 * 依然成功**,只是 widget 一片空白。
 */
export type WidgetMessageNames = typeof WIDGET_MESSAGE

// ─────────────────────────── TSX 模式 / 宿主能力 ───────────────────────────

/** widget 代码的三种格式。`tsx` 由主进程用 sucrase 转成 CJS 后随卡片下发。 */
export type WidgetFormat = 'html' | 'svg' | 'tsx'

/**
 * TSX widget 能 import 的模块 —— **白名单**,主进程编译时校验,iframe 里的
 * `require` 也只认这张表。
 *
 * ★ 每一项都对应 `ncw-widget://shell/` 下发的一份运行时产物(见
 * `net/widget-protocol.ts` 的 `RUNTIME_FILES`)。只改这里不改那边的症状是
 * 「编译通过、iframe 里一片空白、控制台一条 404」。
 */
export const WIDGET_MODULES = [
  'react',
  'react/jsx-runtime',
  'nextcowork/widget',
  'nextcowork/charts',
  'lucide-react'
] as const

export type WidgetModule = (typeof WIDGET_MODULES)[number]

/**
 * 预先声明的动作里一个参数的约束。
 *
 * ★ 只有**枚举**与**有界数字**两种,没有自由字符串 —— 参数会被代进工具入参
 * (典型是 `Bash` 的命令行),自由字符串等于让 widget 拼命令。两种约束下
 * 所有可能的取值在声明时就能被看见。
 */
export type WidgetActionParam =
  | { type: 'enum'; values: string[] }
  | { type: 'number'; min: number; max: number; integer?: boolean }

/**
 * 一个预先声明的动作:widget 只能按 `id` 触发,工具与入参在 `visualize_show_widget`
 * 那一次调用里就定死了。入参里字符串形如 `{{name}}` 的片段由 `params` 代入。
 */
export interface WidgetAction {
  id: string
  label: string
  tool: string
  input: Record<string, unknown>
  params?: Record<string, WidgetActionParam>
}

/** widget → 宿主能请求的事。每一项的语义见 `WidgetFrame.tsx` 的 `handleCall`。 */
export type WidgetHostMethod =
  | 'copy'
  | 'download'
  | 'openFile'
  | 'openUrl'
  | 'toast'
  | 'setState'
  | 'submit'
  | 'draft'
  | 'setContext'
  | 'runAction'
  | 'complete'

/** 各通道的长度上限(字符)。超了宿主直接拒绝,不截断 —— 截断后的 JSON 是坏的。 */
export const WIDGET_LIMITS = {
  state: 16 * 1024,
  submit: 32 * 1024,
  draft: 8 * 1024,
  context: 4 * 1024,
  toast: 200,
  copy: 1024 * 1024,
  download: 20 * 1024 * 1024,
  completePrompt: 16 * 1024,
  completeSystem: 4 * 1024,
  data: 64 * 1024
} as const

/**
 * 宿主 → 外壳的"这张卡此刻的环境":持久化的状态、模型推来的最新数据、
 * 是否在等提交、有哪些动作。挂载前推一次,之后任一项变化再推。
 */
export interface WidgetEnvironment {
  /** 这张卡的标识(模型给的 snake_case)。报错文案、`draft` 的默认措辞会用到 */
  title: string
  state: unknown
  data: unknown
  interactive: boolean
  /** 交互模式下用户是否已经提交过(提交后 `submit` 不再可用) */
  submitted: boolean
  actions: Array<{ id: string; label: string; params?: Record<string, WidgetActionParam> }>
}

/** `runAction` 的结果。`output` 是工具的文本结果,不进模型上下文。 */
export interface WidgetActionResult {
  ok: boolean
  output: string
}

/** 主进程判定一次动作该怎么走:直接跑、先问用户、或拒绝。 */
export type WidgetActionPreflight =
  | { kind: 'allow' }
  | { kind: 'ask'; toolName: string; input: unknown }
  | { kind: 'deny'; reason: string }

/**
 * 没给 `format` 时的推断。顺序重要:TSX 的特征比 HTML 更具体,先认它。
 * 放在 shared:生成期渲染层要用同一条规则决定"边写边画"还是"等写完再挂载"。
 */
export function inferWidgetFormat(code: string): WidgetFormat {
  const head = code.trimStart()
  if (/^(?:import\s|export\s+default\b)/.test(head) || /^\s*import\s[^;]*from\s+['"](?:react|nextcowork\/)/m.test(code)) {
    return 'tsx'
  }
  return /^<svg[\s>]/i.test(head) ? 'svg' : 'html'
}

/** 动作 `{{name}}` 占位符。导出给主进程与测试共用同一条正则。 */
export const WIDGET_PARAM_PATTERN = /\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}/g

/**
 * 把参数代进动作入参。参数不合法 → 抛错(调用方转成给 widget 的失败)。
 *
 * ★ 只代进**字符串叶子**;数字参数代进去仍是字符串的一部分。整串恰好是一个
 * 占位符时也保持字符串 —— 工具入参的类型由声明者决定,不由参数决定。
 */
export function bindWidgetAction(action: WidgetAction, params: Record<string, unknown>): Record<string, unknown> {
  const declared = action.params ?? {}
  for (const name of Object.keys(params)) {
    if (!Object.hasOwn(declared, name)) throw new Error(`Unknown parameter: ${name}`)
  }
  const values: Record<string, string> = {}
  for (const [name, spec] of Object.entries(declared)) {
    const value = params[name]
    if (spec.type === 'enum') {
      if (typeof value !== 'string' || !spec.values.includes(value)) throw new Error(`Parameter ${name} must be one of: ${spec.values.join(', ')}`)
      values[name] = value
    } else {
      if (typeof value !== 'number' || !Number.isFinite(value) || value < spec.min || value > spec.max || (spec.integer === true && !Number.isInteger(value))) {
        throw new Error(`Parameter ${name} must be a ${spec.integer === true ? 'integer' : 'number'} in [${String(spec.min)}, ${String(spec.max)}]`)
      }
      values[name] = String(value)
    }
  }
  const walk = (node: unknown): unknown => {
    if (typeof node === 'string') {
      return node.replace(WIDGET_PARAM_PATTERN, (match, name: string) => values[name] ?? match)
    }
    if (Array.isArray(node)) return node.map(walk)
    if (node !== null && typeof node === 'object') {
      return Object.fromEntries(Object.entries(node).map(([key, value]) => [key, walk(value)]))
    }
    return node
  }
  return walk(action.input) as Record<string, unknown>
}

/**
 * 把 widget 给的路径落到工作区里。只认工作区之内 —— `..` 跳出去的一律拒绝。
 * 纯字符串运算:渲染层没有 `path` 模块,也不需要真的去碰文件系统(打开时会再验一次存在)。
 */
export function resolveWorkspacePath(root: string, path: string): string | null {
  const sep = root.includes('\\') && !root.includes('/') ? '\\' : '/'
  const absolute = /^([a-zA-Z]:[\\/]|[\\/])/.test(path)
  const joined = absolute ? path : `${root}${sep}${path}`
  const parts: string[] = []
  for (const segment of joined.split(/[\\/]+/)) {
    if (segment === '' || segment === '.') continue
    if (segment === '..') {
      if (parts.length === 0) return null
      parts.pop()
    } else parts.push(segment)
  }
  const lead = /^[a-zA-Z]:/.test(joined) ? '' : sep
  const normalized = `${lead}${parts.join(sep)}`
  const base = root.replace(/[\\/]+$/, '')
  return normalized === base || normalized.startsWith(`${base}${sep}`) ? normalized : null
}
