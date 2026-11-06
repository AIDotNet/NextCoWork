/**
 * 插件清单 —— `package.json`,字段名尽量与 VS Code 同名。
 *
 * ## 为什么不用 zod
 *
 * 仓库里 zod 只出现在**主进程**(`defineTool` 的入参 schema)。这份清单类型
 * 渲染层也要用(插件详情页要列能力、贡献点),从 shared 引 zod 会把整个
 * 校验器拖进渲染 bundle,而渲染层一次都不需要**校验**清单 —— 它只读已经
 * 校验过的结果。所以这里是手写归一化器,和 `shared/domain/local-settings.ts`
 * 同一个路数。
 *
 * ## 校验的取向:**装载期严格,运行期宽容**
 *
 * 一份读不懂的清单**整份拒绝**,不做部分接受 —— 半份生效的插件会在某个
 * 贡献点上凭空消失,而诊断里只会写「清单有问题」,症状和原因对不上。
 * 但拒绝的方式是**返回错误,永不 throw**:一个坏插件不该让插件系统起不来
 * (同 `kernel/skill/load.ts` 的「一切失败变 diagnostics」)。
 */
import {
  PLUGIN_PERMISSIONS,
  isPluginPermission,
  type PluginPermission
} from './permission'
/*
  ★ 复用 Skill 那边的名字规则,不在这里另写一条。

  插件贡献的 skill 最终和用户自己装的走**同一个扫描器**(`kernel/skill/load.ts`),
  那边拿 `SKILL_NAME_RE` 卡目录名。这里松一点的话,清单能过、装得上,
  而扫描时被静默跳过 —— 作者看到的是「我声明了但它不在」。
*/
import { SKILL_NAME_RE } from '../domain/skill'
import { isDocumentFormat, type DocumentFormat } from '../document-engine/protocol'
import { parseNativeComponents, type NativeComponent } from './native-component'

/**
 * 插件包里放 skill 的那层目录。
 *
 * ★ 写成常量而不是字面量,是因为它同时是**三方约定**:清单校验(这里)、
 * 打包器的复制清单(`packages/plugin-cli`)、以及服务端上架时的解包校验
 * (CoWork 的 `PluginEndpoints.InspectPackage`)。三处改不齐的症状是
 * 「本地装得上、发布之后装不上」,而那要等到用户装失败才会被发现。
 */
export const SKILL_CONTRIBUTION_DIR = 'skills'

/** 与后端 `NameRegex` 同一形状。 */
export const PLUGIN_NAME_RE = /^[a-z0-9][a-z0-9-]{0,63}$/
/** `publisher.name`。 */
export const PLUGIN_ID_RE = /^[a-z0-9][a-z0-9-]{0,63}\.[a-z0-9][a-z0-9-]{0,63}$/
/** 与后端 `SemVerRegex` 复用同一形状(不含 build metadata 的宽松版)。 */
export const PLUGIN_VERSION_RE = /^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$/
/** 贡献点上的文案必须是这个形状 —— 引用 l10n bundle,不是裸文案。 */
export const L10N_REF_RE = /^%[A-Za-z0-9_][A-Za-z0-9_.-]*%$/

/**
 * 激活事件白名单。
 *
 * ★ `onStartup` 在枚举里,但**市场审核默认驳回**:每个激活的插件是一个
 * renderer 进程,它是这套架构下唯一的内存风险源。留在枚举里是因为确实有
 * 极少数插件需要它(比如注册一个全局状态栏读数),不留的话作者只能用
 * 一个匹配所有文件的 `onWorkspaceContains` glob 去伪装,那更糟 ——
 * 伪装出来的意图审核看不见。
 */
export const ACTIVATION_EVENT_PREFIXES = [
  'onCommand:',
  'onView:',
  'onCustomEditor:',
  'onTool:',
  'onWorkspaceContains:',
  /** 用户打开了某个网页应用(`contributes.webApps[].id`) */
  'onWebApp:',
  /** 用户在输入框里用了某条斜杠命令 */
  'onSlashCommand:'
] as const

export const ACTIVATION_EVENT_LITERALS = ['onStartup'] as const

/**
 * 这份清单描述的是哪一类插件。见 `PluginManifest.kind`。
 *
 * `webapp` 是**零代码**的那一类:只有 `contributes.webApps`,宿主不为它起任何
 * 隔离上下文。它的存在是为了让「把一个网站带进来」这种最朴素的需求不必付
 * 「写 JS + 打包 + 常驻一个进程」的全额成本。
 */
export type PluginKind = 'extension' | 'webapp'

export interface PluginCommandContribution {
  command: string
  /** `%key%`,不是文案 */
  title: string
  icon?: string
  /**
   * 命令自己的品牌图标(包内相对路径,`.svg` / `.png`)。
   *
   * 需求:claude-code / codex 这类插件要求用**官方 logo** 出现在 `+` 菜单里,
   * 而名字闭集(`MENU_ICON_NAMES`)里没有、也不该有品牌字形。`iconFile` 是
   * 并行的第二条路:文件来自**用户已安装的这个包**、由主进程读出后转 data URL
   * 随 catalog 下发(渲染层 CSP 的 `img-src` 已含 `data:`),只出现在带该插件
   * 署名的菜单条目上。见 `renderer/shell/icons.tsx` 头注释里对这条放宽的完整说明。
   */
  iconFile?: string
}

export interface PluginMenuContribution {
  command: string
  /** `<group>@<order>`,照抄 VS Code */
  group?: string
  when?: string
}

export interface PluginCustomEditorContribution {
  viewType: string
  displayName: string
  selector: { filenamePattern: string }[]
  priority?: 'default' | 'option'
  /**
   * 这个编辑器用 `contributes.views` 里哪一个视图(按 id)。
   *
   * 需求:办公插件一个包要为 Word/Excel/PPT 各配一个视图,而宿主原先固定取
   * `views[0]`(见 `views/plugins/CustomEditorView.tsx`)。★ 可选且缺省时行为不变 ——
   * 改成必填会让已经装着的编辑器插件当场打不开文件。
   */
  viewId?: string
  /**
   * 这个编辑器绑定哪个文档引擎。写本插件 `documentEngines[].id`,或者
   * `<依赖插件 id>/<引擎 id>`(依赖必须在 `dependencies` 里)。
   *
   * 给了它,视图走会话通道(`document-engine/protocol.ts`),不再走一次性
   * 搬运整份文件的 `ncw:doc:*`。缺省 = 老路,旧插件零改动。
   */
  documentEngine?: string
}

/**
 * 插件提供的文档引擎 —— 由包内原生组件承载。
 *
 * 需求:办公插件族共用一个引擎插件(携带 LibreOffice),前端插件只声明
 * 「我用哪个引擎」。引擎能打开哪些格式在这里**静态声明**,真正能做什么
 * 由会话打开时的 capability 回答。
 */
export interface PluginDocumentEngineContribution {
  /** 同 `PLUGIN_NAME_RE` 形状 */
  id: string
  /** 承载它的 `nativeComponents[].id`,必须是本插件自己声明的 */
  component: string
  formats: DocumentFormat[]
}

export interface PluginViewContribution {
  id: string
  title: string
  icon?: string
  /** 视图 HTML 在包内的相对路径 */
  path: string
  /**
   * 这个视图挂在哪。缺省 `editor`。
   *
   * - `editor` —— 自定义编辑器的 UI(必须绑定一个文件才有意义,路径由 Tab 给);
   * - `sidebar` —— 侧边栏里的常驻面板;
   * - `panel` —— 可以开在任意一格的内层 Tab。
   *
   * ★ 缺省必须是 `editor`:这个字段存在之前,`contributes.views[0]` 就**是**
   * 「自定义编辑器的 UI」(`views/plugins/CustomEditorView.tsx` 直接取它)。
   * 缺省改成别的,会让已经装着的编辑器类插件当场打不开文件。
   */
  location?: PluginViewLocation
}

export type PluginViewLocation = 'editor' | 'sidebar' | 'panel'

const VIEW_LOCATIONS: readonly PluginViewLocation[] = ['editor', 'sidebar', 'panel']

/**
 * 一个「网页应用」—— 清单里写死地址,装上就是一个入口。
 *
 * ## 这个贡献点为什么存在
 *
 * 最朴素的一种插件是「把哔哩哔哩带进来」:没有逻辑、没有工具,就是一个图标
 * 加一个网址。在它之前,这种插件同样必须写 JS 入口、打包,并且让宿主为它起
 * 一个隐藏的 BrowserWindow —— 为一件零逻辑的事付全额成本,而作者八成会在
 * 「`main` 必须是单文件 ESM」那一步就放弃。
 *
 * ★ URL **写死在清单里**,所以不需要 `tabs.browser` 能力:用户在安装界面上
 * 已经看到了它要开哪个站。动态地址(`tabs.openBrowser`)才需要能力 + 逐 URL 门。
 */
export interface PluginWebAppContribution {
  /** 稳定 id(同 `PLUGIN_NAME_RE` 形状)。落盘的 Tab 按它定位 */
  id: string
  /** `%key%`,不是文案 */
  title: string
  icon?: string
  /** https 地址。装载时就过 URL 门,过不了的整份清单拒绝 */
  url: string
  /** 打开在哪:内层 Tab / 外层功能 Tab / 右侧面板。缺省 `tab` */
  open?: PluginWebAppOpen
  /** 要不要在侧边栏出一个常驻入口。缺省 `sidebar` */
  entry?: 'sidebar' | 'none'
}

export type PluginWebAppOpen = 'tab' | 'feature' | 'right'

const WEB_APP_OPENS: readonly PluginWebAppOpen[] = ['tab', 'feature', 'right']

/**
 * 对话输入框里的一条斜杠命令。
 *
 * ★ `command` 必填:一条斜杠命令背后就是一条**已经声明过的命令**,与菜单贡献
 * 同一条路(`contributes.menus` → command)。另给它一条注册通道的话,同一件事
 * 就有了两个注册点,而作者只会接上其中一个,另一个静默失效。
 */
export interface PluginSlashCommandContribution {
  /** 用户输入的那个词(`/xxx`) */
  name: string
  /** 触发哪条命令。必须在 `contributes.commands` 里 */
  command: string
  /** `%key%` */
  title: string
  /** `%key%`,可选 */
  description?: string
}

/** 斜杠命令的名字 —— 它会**原样出现在输入框里**,所以不许有空格和大写。 */
export const SLASH_COMMAND_RE = /^[a-z0-9][a-z0-9-]{0,31}$/

/**
 * 工具卡片的形态 —— 决定折叠态图标与展开渲染器。
 *
 * ★ 字符串**内联**在这里,不 import `domain/tool-presenter` 的 `ToolShape`:
 * manifest 是纯清单解析,不该为一个枚举把渲染域拖进 import 图。两处取值必须一致
 * (渲染层拿这个字符串去 `SHAPE_ICON` / `DETAIL_RENDERERS` 查表)。
 */
export type PluginToolShape =
  | 'reasoning'
  | 'read'
  | 'mutate'
  | 'search'
  | 'command'
  | 'network'
  | 'orchestration'
  | 'external'

const TOOL_SHAPES: readonly PluginToolShape[] = [
  'reasoning',
  'read',
  'mutate',
  'search',
  'command',
  'network',
  'orchestration',
  'external'
]

export interface PluginToolContribution {
  name: string
  title: string
  icon?: string
  /** 折叠态卡片形态。缺省按 `external` 走。见 `PluginToolShape`。 */
  shape?: PluginToolShape
  /**
   * 折叠态标题 / 摘要模板。值是 `%l10nKey%`,渲染层 `t()` 之后再做 `{param}` 插值。
   *
   * ★ 只影响**展示**,不改工具行为。缺参时渲染层回退到静态标题,不漏花括号
   * (流式态入参可能是半截 JSON)。
   */
  card?: { title?: string; summary?: string }
}

/**
 * 工具返回 `frame` 卡片时,渲染层据 `viewType` 找到这张卡的 HTML。
 *
 * ★ 与 `views`(侧栏常驻视图)**分开声明**:卡片是内联、随聊天流滚动、
 * 只读数据单向推入、可回收的,生命周期与常驻视图不同,不复用同一贡献点。
 */
export interface PluginCardViewContribution {
  viewType: string
  /** 卡片 HTML 在包内的相对路径 */
  path: string
}

export interface PluginKeybindingContribution {
  command: string
  key: string
  when?: string
}

export interface PluginConfigurationProperty {
  type: 'boolean' | 'string' | 'number' | 'enum'
  title: string
  default?: boolean | string | number
  enum?: string[]
  description?: string
}

export interface PluginConfigurationContribution {
  title: string
  properties: Record<string, PluginConfigurationProperty>
}

export interface PluginContributes {
  commands: PluginCommandContribution[]
  menus: Record<string, PluginMenuContribution[]>
  customEditors: PluginCustomEditorContribution[]
  views: PluginViewContribution[]
  /** 清单里写死地址的网页应用。见 `PluginWebAppContribution` */
  webApps: PluginWebAppContribution[]
  tools: PluginToolContribution[]
  /** 工具返回 `frame` 卡片时的 HTML 落点,按 viewType 索引 */
  cardViews: PluginCardViewContribution[]
  keybindings: PluginKeybindingContribution[]
  /** 对话输入框里的 `/xxx`。每条背后必须是一条已声明的命令 */
  slashCommands: PluginSlashCommandContribution[]
  /**
   * 包内自带的 Skill,每条一个目录:`skills/<name>/SKILL.md`。
   *
   * 宿主在插件**启用**时把这些目录的绝对路径交给 Skill 扫描器,禁用或卸载时
   * 即时撤回(见 `main/plugin/manager.ts` 的 `contributedSkillRoots`)。
   * 名字与描述来自 `SKILL.md` 的 frontmatter,不在这里重复声明 ——
   * 声明两遍必然会分叉,而分叉之后没有任何一侧是权威的。
   */
  skills: { path: string }[]
  /** 包内的子代理定义目录/文件。与 skills 同形 */
  agents: { path: string }[]
  /** 包内的模式定义目录/文件。与 skills 同形 */
  modes: { path: string }[]
  themes: { path: string }[]
  configuration?: PluginConfigurationContribution
  /**
   * 文档引擎。**可选字段**(不写就不出现),理由同 `views[].location`:
   * 补一个空数组会让所有老清单的解析结果都变样,回归对比挂在无意义的差异上。
   */
  documentEngines?: PluginDocumentEngineContribution[]
  /**
   * 认得字段名、但这一版**不实现**的贡献点原样留着。
   *
   * ★ 留着不是为了将来好改,是为了**现在能报诊断**:装载时按这份列表给出
   * 「这个贡献点还没实现」的具体提示,而不是让插件作者对着「没有反应」发呆
   * (见 `main/plugin/unsupported.ts`)。
   */
  unsupported: string[]
}

export interface PluginManifest {
  /** `publisher.name` —— 全局唯一,插件的身份 */
  id: string
  name: string
  publisher: string
  /**
   * 这份清单描述的是哪一类插件。缺省 `extension`(老清单零改动)。
   *
   * - `extension` —— 有代码:`main` 必填,宿主为它起一个隔离上下文;
   * - `webapp` —— **没有代码**:只有 `contributes.webApps`,宿主一个进程都不起。
   *
   * ★ 为什么不是「`main` 变可选」:那样「有没有代码」会变成一件要在激活、休眠、
   * `runCommand`、工具装配、关停五处各判断一次的事。`kind` 让它在**装载那一刻**
   * 就分流,后面每一处只是一次早返回。
   */
  kind: PluginKind
  displayName: string
  description: string
  version: string
  license?: string
  icon?: string
  categories: string[]
  keywords: string[]
  /**
   * 简单 range:`^x.y.z` / `~x.y.z` / `>=x.y.z` / 精确。
   *
   * ★ 比的是**插件 API 版本**(`shared/plugin/api-version.ts`),不是应用版本。
   * 两者曾被当成同一个,结果是按官方模板写的插件装上一律「装载失败」。
   */
  engines: string
  /**
   * 单文件 ESM 的相对路径。
   *
   * ★ `kind: 'webapp'` 时是**空串**:那类插件没有代码可跑。写了 `main` 的
   * webapp 清单会被整份拒绝,而不是忽略掉那个字段 —— 忽略的话作者会以为
   * 他的代码在跑,然后对着一个永远不执行的 `activate()` 排查。
   */
  main: string
  /** l10n 目录的相对路径 */
  l10n?: string
  activationEvents: string[]
  permissions: PluginPermission[]
  optionalPermissions: PluginPermission[]
  hostPermissions: string[]
  /**
   * `process.exec` 的命令白名单 —— 裸可执行名,不带路径也不带 `.exe` 之类的后缀。
   * 空数组 = 一条命令都不许跑(即便 `permissions` 里有 `process`)。
   */
  allowedCommands: string[]
  /**
   * 依赖的其它插件:`pluginId` → 版本 range(与 `engines` 同一套 range 语法)。
   *
   * ★ 声明依赖是**跨插件通信的准入门**:`ncw.plugins.connect(pluginId)` 只能连
   * 这里声明过的插件(外加 `plugins` 能力)。同时决定激活顺序 —— 依赖先醒。
   */
  dependencies: Record<string, string>
  /**
   * 包内携带的原生组件(例如办公插件自带的 LibreOffice 引擎)。
   * 可选:绝大多数插件没有原生代码,不写就不出现。见 `native-component.ts`。
   */
  nativeComponents?: NativeComponent[]
  contributes: PluginContributes
}

export interface ManifestError {  /** 出问题的字段路径,给插件作者看的 */
  field: string
  message: string
}

export type ManifestParseResult =
  | { ok: true; manifest: PluginManifest; warnings: ManifestError[] }
  | { ok: false; errors: ManifestError[] }

/** 单份清单最多认多少个贡献项 —— 防一份被塞了几千项的清单把装载拖住。 */
const MAX_CONTRIBUTIONS_PER_KIND = 100

/**
 * 解析并校验一份 `package.json`。**永不 throw。**
 *
 * `known` 传的是宿主这一版认得的菜单 id / 贡献点名字;不认得的不报错、
 * 进 `unsupported`,由装载方转成诊断。
 */
export function parsePluginManifest(raw: unknown): ManifestParseResult {
  const errors: ManifestError[] = []
  const warnings: ManifestError[] = []
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    return { ok: false, errors: [{ field: '', message: 'manifest must be a JSON object' }] }
  }
  const r = raw as Record<string, unknown>

  const name = str(r.name)
  if (!PLUGIN_NAME_RE.test(name)) errors.push({ field: 'name', message: 'must match ^[a-z0-9][a-z0-9-]{0,63}$' })
  const publisher = str(r.publisher)
  if (!PLUGIN_NAME_RE.test(publisher)) errors.push({ field: 'publisher', message: 'must match ^[a-z0-9][a-z0-9-]{0,63}$' })

  const version = str(r.version)
  if (!PLUGIN_VERSION_RE.test(version)) errors.push({ field: 'version', message: 'must be a semver string' })

  const displayName = str(r.displayName) || name
  const description = str(r.description)

  /*
    ★ `kind` 必须**先解析**:后面 `main` 的校验规则、以及 `contributes` 里
    哪些贡献点算合法,全都由它分流。
  */
  const kindRaw = str(r.kind)
  const kind: PluginKind = kindRaw === '' ? 'extension' : kindRaw === 'webapp' ? 'webapp' : 'extension'
  if (kindRaw !== '' && kindRaw !== 'extension' && kindRaw !== 'webapp') {
    errors.push({ field: 'kind', message: `unknown plugin kind "${kindRaw}"; expected "extension" or "webapp"` })
  }

  const engines = str((r.engines as Record<string, unknown> | undefined)?.nextcowork)
  if (engines === '') errors.push({ field: 'engines.nextcowork', message: 'is required' })
  else if (parseRange(engines) === null) errors.push({ field: 'engines.nextcowork', message: `unsupported range: ${engines}` })

  const main = str(r.main)
  if (kind === 'webapp') {
    /*
      ★ 写了 `main` 的 webapp 清单**整份拒绝**,不是忽略那个字段。
      忽略的话作者会以为自己的代码在跑,然后对着一个永远不执行的 `activate()`
      排查打包、排查路径 —— 而真正的原因是「这类插件根本不跑代码」。
    */
    if (main !== '') errors.push({ field: 'main', message: 'a "webapp" plugin runs no code; remove "main"' })
  } else if (main === '') errors.push({ field: 'main', message: 'is required' })
  else if (!isSafeRelativePath(main)) errors.push({ field: 'main', message: 'must be a relative path inside the package' })
  else if (!main.endsWith('.js')) errors.push({ field: 'main', message: 'must be a single-file ESM .js bundle' })

  const l10n = str(r.l10n)
  if (l10n !== '' && !isSafeRelativePath(l10n)) errors.push({ field: 'l10n', message: 'must be a relative path inside the package' })

  const icon = str(r.icon)
  if (icon !== '' && !isSafeRelativePath(icon)) errors.push({ field: 'icon', message: 'must be a relative path inside the package' })

  const activationEvents = strList(r.activationEvents).filter((event) => {
    if (isActivationEvent(event)) return true
    errors.push({ field: 'activationEvents', message: `unknown activation event: ${event}` })
    return false
  })

  const permissions = permissionList(r.permissions, 'permissions', errors)
  const optionalPermissions = permissionList(r.optionalPermissions, 'optionalPermissions', errors)

  const hostPermissions = strList(r.hostPermissions).filter((pattern) => {
    if (isHostPattern(pattern)) return true
    errors.push({ field: 'hostPermissions', message: `must look like https://host/path* : ${pattern}` })
    return false
  })
  /*
    ★ 没声明 `net` 却写了 `hostPermissions` 是**警告不是错误**:它通常是
    作者把 `net` 从 `permissions` 挪进 `optionalPermissions` 时留下的,
    拒绝整份清单太重;但不提醒的话,他会发现请求全被拒而找不到原因。
  */
  if (hostPermissions.length > 0 && !permissions.includes('net') && !optionalPermissions.includes('net')) {
    warnings.push({ field: 'hostPermissions', message: 'has no effect without the "net" permission' })
  }

  /*
    ★ 和 `hostPermissions` 对称:那个回答「能连哪些域名」,这个回答
    「能跑哪些可执行文件」。`process` 能力本身只表示「可以跑命令」,
    具体跑什么必须由清单逐条列出 —— 没有这张表就一条都不许跑
    (`capabilities.ts` 的 `narrowCommand`)。

    ★ 存的是**去掉目录和 Windows 可执行后缀之后的 stem**,因为参数门就是
    按 stem 比对的。作者写 `/usr/bin/git` 或 `git.exe` 的话,归一之后
    比对得上、但清单上写着的和实际生效的不是一个东西 —— 与其默默替他改,
    不如在这里就报错,否则他只会看到「命令不在白名单里」而对不上原因。
  */
  const allowedCommands = strList(r.allowedCommands).filter((command) => {
    if (isCommandStem(command)) return true
    errors.push({ field: 'allowedCommands', message: `must be a bare executable name without a path: ${command}` })
    return false
  }).slice(0, MAX_CONTRIBUTIONS_PER_KIND)
  // 同 hostPermissions:声明了却没有对应能力是**警告**,不是整份清单作废。
  if (allowedCommands.length > 0 && !permissions.includes('process') && !optionalPermissions.includes('process')) {
    warnings.push({ field: 'allowedCommands', message: 'has no effect without the "process" permission' })
  }

  const contributes = parseContributes(r.contributes, errors, kind)

  // 依赖:pluginId → 版本 range(同 engines 的 range 语法)。自依赖是错误。
  const dependencies: Record<string, string> = {}
  const depsRaw = r.dependencies
  if (depsRaw !== null && typeof depsRaw === 'object' && !Array.isArray(depsRaw)) {
    for (const [depId, range] of Object.entries(depsRaw as Record<string, unknown>)) {
      const depRange = str(range)
      if (!PLUGIN_ID_RE.test(depId)) {
        errors.push({ field: `dependencies.${depId}`, message: 'not a valid plugin id (publisher.name)' })
        continue
      }
      if (depId === `${publisher}.${name}`) {
        errors.push({ field: `dependencies.${depId}`, message: 'a plugin cannot depend on itself' })
        continue
      }
      if (parseRange(depRange) === null) {
        errors.push({ field: `dependencies.${depId}`, message: `unsupported range: ${depRange}` })
        continue
      }
      dependencies[depId] = depRange
    }
  }

  /*
    原生组件。需求见 `native-component.ts` 文件头:插件包里自带的引擎必须逐条声明
    平台、入口与摘要。★ webapp 不能带 —— 零代码插件带原生可执行文件,等于把
    「不跑代码」这件事变成一句假话。
  */
  const nativeComponents = parseNativeComponents(r.nativeComponents, errors, isSafeRelativePath)
  if (kind === 'webapp' && r.nativeComponents !== undefined) {
    errors.push({ field: 'nativeComponents', message: 'a "webapp" plugin runs no code; it cannot ship native components' })
  }
  checkDocumentEngineRefs(contributes, nativeComponents, dependencies, errors)

  if (errors.length > 0) return { ok: false, errors }
  return {
    ok: true,
    warnings,
    manifest: {
      id: `${publisher}.${name}`,
      name,
      publisher,
      kind,
      displayName,
      description,
      version,
      ...(str(r.license) === '' ? {} : { license: str(r.license) }),
      ...(icon === '' ? {} : { icon }),
      categories: strList(r.categories).slice(0, 8),
      keywords: strList(r.keywords).slice(0, 16),
      engines,
      main,
      ...(l10n === '' ? {} : { l10n }),
      activationEvents,
      permissions,
      optionalPermissions,
      hostPermissions,
      allowedCommands,
      dependencies,
      ...(r.nativeComponents === undefined ? {} : { nativeComponents }),
      contributes
    }
  }
}

/**
 * 文档引擎的交叉引用 —— 只有 `nativeComponents` 与 `dependencies` 都解析完才能判。
 *
 * ★ 三条都是**装载期拒绝**,不是运行期报错:引用悬空的编辑器装上之后,用户第一次
 * 打开 .docx 才看到「引擎不存在」,而那时他已经以为插件能用了。
 */
function checkDocumentEngineRefs(
  contributes: PluginContributes,
  nativeComponents: readonly NativeComponent[],
  dependencies: Readonly<Record<string, string>>,
  errors: ManifestError[]
): void {
  const engines = contributes.documentEngines ?? []
  for (const engine of engines) {
    if (!nativeComponents.some((component) => component.id === engine.component)) {
      errors.push({ field: `contributes.documentEngines.${engine.id}.component`, message: `must reference a declared nativeComponents id: ${engine.component}` })
    }
  }
  for (const editor of contributes.customEditors) {
    const ref = editor.documentEngine
    if (ref === undefined) continue
    const slash = ref.indexOf('/')
    if (slash === -1) {
      if (!engines.some((engine) => engine.id === ref)) {
        errors.push({ field: `contributes.customEditors.${editor.viewType}.documentEngine`, message: `no documentEngines entry with id ${ref}` })
      }
      continue
    }
    /*
      ★ 跨插件引用必须在 `dependencies` 里:依赖声明是安装器「同次装上引擎插件」
      和激活顺序的唯一依据。不在里面的话,前端插件能装上,引擎却从没被安装过。
    */
    const pluginId = ref.slice(0, slash)
    const engineId = ref.slice(slash + 1)
    if (!PLUGIN_ID_RE.test(pluginId) || !PLUGIN_NAME_RE.test(engineId)) {
      errors.push({ field: `contributes.customEditors.${editor.viewType}.documentEngine`, message: `must be "<engineId>" or "<publisher.name>/<engineId>": ${ref}` })
    } else if (dependencies[pluginId] === undefined) {
      errors.push({ field: `contributes.customEditors.${editor.viewType}.documentEngine`, message: `${pluginId} must be listed in dependencies` })
    }
  }
}

// ─────────────────────────── 贡献点 ───────────────────────────

/** 这一版实现了的 `contributes` 键。不在表里的进 `unsupported`,报诊断。 */
export const SUPPORTED_CONTRIBUTION_KEYS = [
  'commands',
  'menus',
  'customEditors',
  'views',
  'webApps',
  'tools',
  'cardViews',
  'keybindings',
  'slashCommands',
  'skills',
  'agents',
  'modes',
  'themes',
  'configuration',
  'documentEngines'
] as const

/**
 * `kind: 'webapp'` 允许出现的贡献点。
 *
 * ★ 其余的一律**报错而不是忽略**:一个没有代码的插件声明 `tools` / `commands`,
 * 意味着它注册了一个永远没有处理函数的东西 —— 用户点下去什么也不会发生,
 * 而日志里一个字都没有。这正是「静默不生效」那类最难查的失败。
 */
const WEBAPP_ALLOWED_CONTRIBUTIONS: readonly string[] = ['webApps', 'themes', 'skills', 'agents', 'modes']

/**
 * 工具卡片模板 `{ title?, summary? }` 的解析。
 *
 * 返回 `'error'` 表示已推诊断、调用方应跳过这条工具;`undefined` 表示没声明卡片模板。
 * title/summary 若存在必须是 `%l10nKey%`(与工具 title 同规则)。
 */
function parseToolCard(
  raw: unknown,
  toolName: string,
  errors: ManifestError[]
): { title?: string; summary?: string } | undefined | 'error' {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const c = raw as Record<string, unknown>
  const title = str(c.title)
  const summary = str(c.summary)
  if (title !== '' && !L10N_REF_RE.test(title)) {
    errors.push({ field: `contributes.tools.${toolName}.card.title`, message: 'must be a %l10nKey% reference' })
    return 'error'
  }
  if (summary !== '' && !L10N_REF_RE.test(summary)) {
    errors.push({ field: `contributes.tools.${toolName}.card.summary`, message: 'must be a %l10nKey% reference' })
    return 'error'
  }
  if (title === '' && summary === '') return undefined
  return { ...(title === '' ? {} : { title }), ...(summary === '' ? {} : { summary }) }
}

function parseContributes(raw: unknown, errors: ManifestError[], kind: PluginKind): PluginContributes {
  const out: PluginContributes = {
    commands: [],
    menus: {},
    customEditors: [],
    views: [],
    webApps: [],
    tools: [],
    cardViews: [],
    keybindings: [],
    slashCommands: [],
    skills: [],
    agents: [],
    modes: [],
    themes: [],
    unsupported: []
  }
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return out
  const r = raw as Record<string, unknown>

  for (const key of Object.keys(r)) {
    if (!(SUPPORTED_CONTRIBUTION_KEYS as readonly string[]).includes(key)) { out.unsupported.push(key); continue }
    // webapp 只能贡献「不需要代码」的那几种,见 `WEBAPP_ALLOWED_CONTRIBUTIONS`
    if (kind === 'webapp' && !WEBAPP_ALLOWED_CONTRIBUTIONS.includes(key)) {
      errors.push({
        field: `contributes.${key}`,
        message: `a "webapp" plugin runs no code, so it cannot contribute ${key}`
      })
    }
  }

  for (const item of objList(r.commands)) {
    const command = str(item.command)
    const title = str(item.title)
    if (command === '') { errors.push({ field: 'contributes.commands', message: 'command id is required' }); continue }
    if (!L10N_REF_RE.test(title)) { errors.push({ field: `contributes.commands.${command}.title`, message: 'must be a %l10nKey% reference, not literal copy' }); continue }
    const iconFile = str(item.iconFile)
    /*
      ★ 形状在这里查(包内相对路径 + 扩展名),**存在**在安装器查
      (`assertPackageFiles`,与 `icon` / `main` 同一分工)。扩展名收窄是因为
      这个文件会被主进程读成 data URL 塞进 `<img>`:svg/png 之外的东西
      (html 等)哪怕写进 img 不执行脚本,也没有任何正当用途。
    */
    if (iconFile !== '') {
      if (!isSafeRelativePath(iconFile)) {
        errors.push({ field: `contributes.commands.${command}.iconFile`, message: 'must be a relative path inside the package' }); continue
      }
      if (!/\.(svg|png)$/i.test(iconFile)) {
        errors.push({ field: `contributes.commands.${command}.iconFile`, message: 'must be an .svg or .png file' }); continue
      }
    }
    out.commands.push({ command, title, ...(str(item.icon) === '' ? {} : { icon: str(item.icon) }), ...(iconFile === '' ? {} : { iconFile }) })
  }

  const menus = r.menus
  if (menus !== null && typeof menus === 'object' && !Array.isArray(menus)) {
    for (const [menuId, value] of Object.entries(menus as Record<string, unknown>)) {
      const items: PluginMenuContribution[] = []
      for (const item of objList(value)) {
        const command = str(item.command)
        if (command === '') continue
        items.push({
          command,
          ...(str(item.group) === '' ? {} : { group: str(item.group) }),
          ...(str(item.when) === '' ? {} : { when: str(item.when) })
        })
      }
      if (items.length > 0) out.menus[menuId] = items.slice(0, MAX_CONTRIBUTIONS_PER_KIND)
    }
  }

  for (const item of objList(r.customEditors)) {
    const viewType = str(item.viewType)
    const displayName = str(item.displayName)
    if (viewType === '') { errors.push({ field: 'contributes.customEditors', message: 'viewType is required' }); continue }
    if (!L10N_REF_RE.test(displayName)) { errors.push({ field: `contributes.customEditors.${viewType}.displayName`, message: 'must be a %l10nKey% reference' }); continue }
    const selector = objList(item.selector)
      .map((s) => str(s.filenamePattern))
      .filter((pattern) => pattern !== '')
      .map((filenamePattern) => ({ filenamePattern }))
    if (selector.length === 0) { errors.push({ field: `contributes.customEditors.${viewType}.selector`, message: 'needs at least one filenamePattern' }); continue }
    const priority = str(item.priority)
    const viewId = str(item.viewId)
    const documentEngine = str(item.documentEngine)
    out.customEditors.push({
      viewType,
      displayName,
      selector,
      ...(priority === 'option' ? { priority: 'option' as const } : { priority: 'default' as const }),
      // 不写就不落字段,理由同 views[].location:老清单解析结果一个字节都不变
      ...(viewId === '' ? {} : { viewId }),
      ...(documentEngine === '' ? {} : { documentEngine })
    })
  }

  for (const item of objList(r.views)) {
    const id = str(item.id)
    const title = str(item.title)
    const path = str(item.path)
    if (id === '') { errors.push({ field: 'contributes.views', message: 'view id is required' }); continue }
    if (!L10N_REF_RE.test(title)) { errors.push({ field: `contributes.views.${id}.title`, message: 'must be a %l10nKey% reference' }); continue }
    if (!isSafeRelativePath(path)) { errors.push({ field: `contributes.views.${id}.path`, message: 'must be a relative path inside the package' }); continue }
    const location = str(item.location)
    if (location !== '' && !(VIEW_LOCATIONS as readonly string[]).includes(location)) {
      errors.push({ field: `contributes.views.${id}.location`, message: `unknown location: ${location}` }); continue
    }
    out.views.push({
      id,
      title,
      path,
      ...(str(item.icon) === '' ? {} : { icon: str(item.icon) }),
      // 不写 location 时**不落这个字段**,而不是补一个 'editor':两者行为一样,
      // 但落了之后老清单的解析结果就变了,回归对比会挂在一个无意义的差异上。
      ...(location === '' ? {} : { location: location as PluginViewLocation })
    })
  }

  /*
    ★ `customEditors[].viewId` 在 views 解析**之后**才能判(两张表的解析顺序是
    customEditors 在前)。指向不存在的视图时拒绝:不拒的话宿主只能退回
    `views[0]`,于是 Excel 文件被 Word 的界面打开,而且零报错。
  */
  for (const editor of out.customEditors) {
    if (editor.viewId === undefined) continue
    const view = out.views.find((v) => v.id === editor.viewId)
    if (view === undefined) {
      errors.push({ field: `contributes.customEditors.${editor.viewType}.viewId`, message: `no contributes.views entry with id ${editor.viewId}` })
    } else if ((view.location ?? 'editor') !== 'editor') {
      errors.push({ field: `contributes.customEditors.${editor.viewType}.viewId`, message: `view ${editor.viewId} must have location "editor"` })
    }
  }

  /*
    文档引擎。需求见 `PluginDocumentEngineContribution`。格式必须在
    `DOCUMENT_FORMATS` 白名单里 —— 声明一个宿主不承诺的格式,等于让文件选择器
    把 .doc 交给一个从未被验收过的路径。
  */
  if (r.documentEngines !== undefined) {
    const engines: PluginDocumentEngineContribution[] = []
    for (const item of objList(r.documentEngines)) {
      const id = str(item.id)
      if (!PLUGIN_NAME_RE.test(id)) { errors.push({ field: 'contributes.documentEngines', message: `engine id must match ^[a-z0-9][a-z0-9-]{0,63}$: ${id}` }); continue }
      if (engines.some((engine) => engine.id === id)) { errors.push({ field: `contributes.documentEngines.${id}`, message: 'duplicate engine id' }); continue }
      const component = str(item.component)
      if (component === '') { errors.push({ field: `contributes.documentEngines.${id}.component`, message: 'is required' }); continue }
      const rawFormats = Array.isArray(item.formats) ? item.formats : []
      const bad = rawFormats.find((format) => !isDocumentFormat(format))
      if (rawFormats.length === 0 || bad !== undefined) {
        errors.push({ field: `contributes.documentEngines.${id}.formats`, message: `must list supported formats (docx, docm, xlsx, xlsm, pptx, pptm, pdf)${bad === undefined ? '' : `; got ${String(bad)}`}` })
        continue
      }
      engines.push({ id, component, formats: [...new Set(rawFormats as DocumentFormat[])] })
    }
    out.documentEngines = engines
  }

  /*
    网页应用。需求见 `PluginWebAppContribution` 的文件内注释:
    「装上就是一个图标加一个网址」这类插件必须能零代码表达。
  */
  for (const item of objList(r.webApps)) {
    const id = str(item.id)
    const title = str(item.title)
    const url = str(item.url)
    if (!PLUGIN_NAME_RE.test(id)) { errors.push({ field: 'contributes.webApps', message: `web app id must match ^[a-z0-9][a-z0-9-]{0,63}$: ${id}` }); continue }
    if (!L10N_REF_RE.test(title)) { errors.push({ field: `contributes.webApps.${id}.title`, message: 'must be a %l10nKey% reference' }); continue }
    /*
      ★ URL 在**装载期**就判,不留到打开那一刻。留到运行期的话,一个写错地址的
      插件会安静地装上、在侧边栏占一个位置,点下去才什么都不发生 ——
      而作者收不到任何提示。
    */
    if (!isWebAppUrl(url)) { errors.push({ field: `contributes.webApps.${id}.url`, message: `must be an https:// URL: ${url}` }); continue }
    const open = str(item.open)
    if (open !== '' && !(WEB_APP_OPENS as readonly string[]).includes(open)) {
      errors.push({ field: `contributes.webApps.${id}.open`, message: `unknown open target: ${open}` }); continue
    }
    const entry = str(item.entry)
    if (entry !== '' && entry !== 'sidebar' && entry !== 'none') {
      errors.push({ field: `contributes.webApps.${id}.entry`, message: `unknown entry: ${entry}` }); continue
    }
    out.webApps.push({
      id,
      title,
      url,
      ...(str(item.icon) === '' ? {} : { icon: str(item.icon) }),
      ...(open === '' ? {} : { open: open as PluginWebAppOpen }),
      ...(entry === '' ? {} : { entry: entry as 'sidebar' | 'none' })
    })
  }

  for (const item of objList(r.tools)) {
    const name = str(item.name)
    const title = str(item.title)
    if (name === '') { errors.push({ field: 'contributes.tools', message: 'tool name is required' }); continue }
    if (!L10N_REF_RE.test(title)) { errors.push({ field: `contributes.tools.${name}.title`, message: 'must be a %l10nKey% reference' }); continue }
    const shape = str(item.shape)
    if (shape !== '' && !(TOOL_SHAPES as readonly string[]).includes(shape)) {
      errors.push({ field: `contributes.tools.${name}.shape`, message: `unknown shape: ${shape}` }); continue
    }
    const card = parseToolCard(item.card, name, errors)
    if (card === 'error') continue
    out.tools.push({
      name,
      title,
      ...(str(item.icon) === '' ? {} : { icon: str(item.icon) }),
      ...(shape === '' ? {} : { shape: shape as PluginToolShape }),
      ...(card === undefined ? {} : { card })
    })
  }

  for (const item of objList(r.cardViews)) {
    const viewType = str(item.viewType)
    const path = str(item.path)
    if (viewType === '') { errors.push({ field: 'contributes.cardViews', message: 'viewType is required' }); continue }
    if (!isSafeRelativePath(path)) { errors.push({ field: `contributes.cardViews.${viewType}.path`, message: 'must be a relative path inside the package' }); continue }
    out.cardViews.push({ viewType, path })
  }

  for (const item of objList(r.keybindings)) {
    const command = str(item.command)
    const key = str(item.key)
    if (command === '' || key === '') continue
    out.keybindings.push({ command, key, ...(str(item.when) === '' ? {} : { when: str(item.when) }) })
  }

  /*
    斜杠命令。`command` 必须是**本清单里已经声明过的**命令 —— 指向一条不存在的
    命令时,用户在输入框里能打出它、选中它、然后什么都不发生。
  */
  for (const item of objList(r.slashCommands)) {
    const name = str(item.name)
    const command = str(item.command)
    const title = str(item.title)
    const description = str(item.description)
    if (!SLASH_COMMAND_RE.test(name)) { errors.push({ field: 'contributes.slashCommands', message: `slash command name must match ${String(SLASH_COMMAND_RE)}: ${name}` }); continue }
    if (!out.commands.some((c) => c.command === command)) {
      errors.push({ field: `contributes.slashCommands.${name}.command`, message: `must reference a command declared in contributes.commands: ${command}` }); continue
    }
    if (!L10N_REF_RE.test(title)) { errors.push({ field: `contributes.slashCommands.${name}.title`, message: 'must be a %l10nKey% reference' }); continue }
    if (description !== '' && !L10N_REF_RE.test(description)) {
      errors.push({ field: `contributes.slashCommands.${name}.description`, message: 'must be a %l10nKey% reference' }); continue
    }
    out.slashCommands.push({ name, command, title, ...(description === '' ? {} : { description }) })
  }

  /*
    ★ skill 目录必须落在包内的 `skills/<名字>` 下,而不是「包内任意相对路径」。

    三条理由,每条都对应一种只在别人机器上出现的失败:

    1. **打包器只拷固定的那几个目录。** `plugin-cli` 的 `package` 命令复制的是
       `package.json / dist / l10n / assets / skills / themes`。声明在
       `my-stuff/foo` 的 skill 在作者本机一切正常(目录真的在),而发布出去的
       ZIP 里**没有这个目录** —— 用户装上之后凭空少一条,作者复现不出来。
       在这里拒掉,他在打包之前就知道。
    2. **宿主要能反推名字。** skill 名来自 `SKILL.md` 的 frontmatter,缺省回落到
       **目录名**。`skills/<name>` 这个形状保证了「目录名」是一个确定的东西;
       允许 `a/b/c` 的话,回落该取哪一段就成了一个没人记得住的约定。
    3. **用户要找得到它。** 详情页给的是包内路径,而七个插件七种放法对翻目录的
       人毫无帮助。

    只允许两段。`skills` 本身(想把整个目录当成一条 skill)也拒:那样 `SKILL.md`
    会落在 `skills/SKILL.md`,比「一个子目录一条 skill」的扫描约定正好少一层,
    症状是扫出来 0 条、且没有任何报错。
  */
  for (const item of objList(r.skills)) {
    const path = str(item.path)
    if (!isSafeRelativePath(path)) {
      errors.push({ field: 'contributes.skills', message: `not a package-relative path: ${path}` }); continue
    }
    const segments = path.replace(/\/+$/, '').split('/')
    if (segments.length !== 2 || segments[0] !== SKILL_CONTRIBUTION_DIR) {
      errors.push({
        field: 'contributes.skills',
        message: `must be "${SKILL_CONTRIBUTION_DIR}/<name>": the packager only ships ${SKILL_CONTRIBUTION_DIR}/, so a skill declared elsewhere is silently absent from the published package (got ${path})`
      }); continue
    }
    if (!SKILL_NAME_RE.test(segments[1] ?? '')) {
      errors.push({
        field: 'contributes.skills',
        message: `skill directory name must match ${String(SKILL_NAME_RE)} — it reaches the model verbatim as the skill name (got ${path})`
      }); continue
    }
    out.skills.push({ path })
  }

  // agents / modes 与 skills 同形:包内一个目录或文件,由宿主的既有加载器去读。
  for (const item of objList(r.agents)) {
    const path = str(item.path)
    if (isSafeRelativePath(path)) out.agents.push({ path })
    else errors.push({ field: 'contributes.agents', message: `not a package-relative path: ${path}` })
  }

  for (const item of objList(r.modes)) {
    const path = str(item.path)
    if (isSafeRelativePath(path)) out.modes.push({ path })
    else errors.push({ field: 'contributes.modes', message: `not a package-relative path: ${path}` })
  }

  for (const item of objList(r.themes)) {
    const path = str(item.path)
    if (isSafeRelativePath(path)) out.themes.push({ path })
    else errors.push({ field: 'contributes.themes', message: `not a package-relative path: ${path}` })
  }

  const configuration = r.configuration
  if (configuration !== null && typeof configuration === 'object' && !Array.isArray(configuration)) {
    const c = configuration as Record<string, unknown>
    const title = str(c.title)
    if (!L10N_REF_RE.test(title)) {
      errors.push({ field: 'contributes.configuration.title', message: 'must be a %l10nKey% reference' })
    } else {
      const properties: Record<string, PluginConfigurationProperty> = {}
      const rawProps = c.properties
      if (rawProps !== null && typeof rawProps === 'object' && !Array.isArray(rawProps)) {
        for (const [key, value] of Object.entries(rawProps as Record<string, unknown>)) {
          if (value === null || typeof value !== 'object' || Array.isArray(value)) continue
          const v = value as Record<string, unknown>
          const type = str(v.type)
          const propTitle = str(v.title)
          if (!['boolean', 'string', 'number', 'enum'].includes(type)) continue
          if (!L10N_REF_RE.test(propTitle)) {
            errors.push({ field: `contributes.configuration.properties.${key}.title`, message: 'must be a %l10nKey% reference' })
            continue
          }
          properties[key] = {
            type: type as PluginConfigurationProperty['type'],
            title: propTitle,
            ...(v.default === undefined ? {} : { default: v.default as boolean | string | number }),
            ...(Array.isArray(v.enum) ? { enum: strList(v.enum) } : {})
          }
        }
      }
      out.configuration = { title, properties }
    }
  }

  return out
}

// ─────────────────────────── 版本范围 ───────────────────────────

export interface SemVer { major: number; minor: number; patch: number }

export function parseSemVer(value: string): SemVer | null {
  const m = PLUGIN_VERSION_RE.exec(value)
  if (m === null) return null
  return { major: Number(m[1]), minor: Number(m[2]), patch: Number(m[3]) }
}

interface Range { op: '^' | '~' | '>=' | '='; version: SemVer }

/**
 * 只认四种形状:`^x.y.z` / `~x.y.z` / `>=x.y.z` / `x.y.z`。
 *
 * ★ **刻意不支持复合 range**(`>=1.0 <2.0`、`||`)。range 语法是 npm
 * 生态里最容易写错、也最难向用户解释的一块;而插件真正需要表达的只有
 * 「我要这个大版本」。认不出来的一律拒绝上架,比装上之后行为不可预测好。
 */
export function parseRange(value: string): Range | null {
  const trimmed = value.trim()
  for (const op of ['>=', '^', '~'] as const) {
    if (trimmed.startsWith(op)) {
      const version = parseSemVer(trimmed.slice(op.length).trim())
      return version === null ? null : { op, version }
    }
  }
  const version = parseSemVer(trimmed)
  return version === null ? null : { op: '=', version }
}

function compare(a: SemVer, b: SemVer): number {
  return a.major - b.major || a.minor - b.minor || a.patch - b.patch
}

/** 宿主版本满足这个 range 吗。range 读不懂时返回 `false`(不是 true)。 */
export function satisfiesEngine(range: string, hostVersion: string): boolean {
  const parsed = parseRange(range)
  const host = parseSemVer(hostVersion)
  if (parsed === null || host === null) return false
  const { op, version } = parsed
  if (op === '=') return compare(host, version) === 0
  if (op === '>=') return compare(host, version) >= 0
  if (compare(host, version) < 0) return false
  if (op === '^') {
    /*
      0.x 的 `^` 按 npm 的规矩只锁到 minor —— 而插件 API 在 1.0 之前
      明确可以 break,这条正好是我们要的语义。
    */
    return version.major === 0 ? host.major === 0 && host.minor === version.minor : host.major === version.major
  }
  return host.major === version.major && host.minor === version.minor
}

/**
 * 市场那一版比本机这一版新吗 —— 「要不要提示更新」的唯一判据。
 *
 * ★ 导出的是**谓词**而不是 `compare`:调用点关心的是「有没有更新」,不是
 * 「差几个数」。让它们自己去拼 `parseSemVer` + 比较,等于把下面这条
 * 预发布规则复制 N 份,而它一定会被漏掉其中一份。
 *
 * ★★ **`parseSemVer` 是丢预发布标签的**(它只取 x.y.z),而 `PLUGIN_VERSION_RE`
 * 接受 `-beta.1`。于是本机 `1.0.0-beta` 和市场 `1.0.0` 比出来相等 —— 而那
 * 恰恰是最该提示更新的一种情况:用户手上拿的是个预览版。核心版本相等时
 * 单独补一条:本机带预发布、市场不带 = 有更新。
 *
 * ★ 读不懂的版本号返回 `false`(不是 true),同 `satisfiesEngine` 的规矩 ——
 * 宁可不提示,也不要提示一次点下去必然失败的更新。
 */
export function hasNewerVersion(current: string, latest: string | null | undefined): boolean {
  if (typeof latest !== 'string') return false
  const mine = parseSemVer(current)
  const theirs = parseSemVer(latest)
  if (mine === null || theirs === null) return false
  const diff = compare(theirs, mine)
  if (diff !== 0) return diff > 0
  return current.includes('-') && !latest.includes('-')
}

// ─────────────────────────── 小工具 ───────────────────────────

export function isActivationEvent(event: string): boolean {
  if ((ACTIVATION_EVENT_LITERALS as readonly string[]).includes(event)) return true
  return ACTIVATION_EVENT_PREFIXES.some((prefix) => event.startsWith(prefix) && event.length > prefix.length)
}

/**
 * 包内相对路径。
 *
 * ★ 挡的不只是 `..`:绝对路径、盘符、反斜杠、URL scheme 全在这里挡掉。
 * 这条是**第一道**防线,主进程侧还会再做一次 realpath 归一
 * (软链是这一层看不出来的)。
 */
export function isSafeRelativePath(value: string): boolean {
  if (value === '' || value.length > 512) return false
  if (value.startsWith('/') || value.startsWith('\\')) return false
  if (/^[a-zA-Z]:/.test(value)) return false
  if (value.includes('\\')) return false
  if (value.includes('\0')) return false
  return !value.split('/').some((segment) => segment === '..')
}

/** `https://host/path*`。★ 不接受 `http:`,不接受 `*` 当主机名。 */
export function isHostPattern(value: string): boolean {
  if (!value.startsWith('https://')) return false
  const rest = value.slice('https://'.length)
  const host = rest.split('/')[0] ?? ''
  if (host === '' || host === '*' || host.includes('*')) return false
  return /^[a-z0-9.-]+(?::\d{1,5})?$/i.test(host)
}

/**
 * `contributes.webApps[].url` 的一条合法取值。
 *
 * ★ 与 `isHostPattern` 分开:那个判的是**模式**(可以带 `*`),这个判的是一个
 * **具体地址**。拿模式那套去判地址的话,`https://a.com/*` 会被当成合法 URL
 * 塞进 webview 的 src —— 浏览器会老老实实去请求一个带星号的路径。
 *
 * ★ 只认 https、不认 URL 里的凭据:webapp 的登录态复用工作区浏览器分区,
 * 明文 http 意味着那份 cookie 会以明文出现在网络上。
 */
export function isWebAppUrl(value: string): boolean {
  if (value === '' || value.length > 2048) return false
  let parsed: URL
  try { parsed = new URL(value) } catch { return false }
  if (parsed.protocol !== 'https:') return false
  if (parsed.username !== '' || parsed.password !== '') return false
  return parsed.hostname !== '' && !parsed.hostname.includes('*')
}

/**
 * `allowedCommands` 的一条合法取值 —— **裸可执行名**。
 *
 * ★ 拒绝路径分隔符和 `.exe`/`.cmd`/`.bat`/`.ps1` 后缀,而不是把它们剥掉:
 * 参数门(`capabilities.ts` 的 `narrowCommand`)比对的是归一之后的 stem,
 * 默默替作者剥掉的话,清单上写着的和实际生效的就成了两个东西。
 */
export function isCommandStem(value: string): boolean {
  if (value === '' || value.length > 64) return false
  if (/[/\\]/.test(value)) return false
  if (/\.(exe|cmd|bat|ps1)$/i.test(value)) return false
  return /^[a-z0-9._+-]+$/i.test(value)
}

/** 一个具体 URL 命中 `hostPermissions` 里的某一条吗。 */
export function matchesHostPermission(patterns: readonly string[], url: string): boolean {
  let parsed: URL
  try { parsed = new URL(url) } catch { return false }
  if (parsed.protocol !== 'https:') return false
  return patterns.some((pattern) => {
    if (!isHostPattern(pattern)) return false
    const rest = pattern.slice('https://'.length)
    const slash = rest.indexOf('/')
    const host = slash === -1 ? rest : rest.slice(0, slash)
    const path = slash === -1 ? '/*' : rest.slice(slash)
    if (parsed.host.toLowerCase() !== host.toLowerCase()) return false
    if (path === '/*' || path === '*') return true
    if (path.endsWith('*')) return parsed.pathname.startsWith(path.slice(0, -1))
    return parsed.pathname === path
  })
}

function str(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function strList(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  const out: string[] = []
  for (const entry of value) {
    const s = str(entry)
    if (s !== '' && !out.includes(s)) out.push(s)
    if (out.length >= MAX_CONTRIBUTIONS_PER_KIND) break
  }
  return out
}

function objList(value: unknown): Record<string, unknown>[] {
  if (!Array.isArray(value)) return []
  return value
    .filter((entry): entry is Record<string, unknown> => entry !== null && typeof entry === 'object' && !Array.isArray(entry))
    .slice(0, MAX_CONTRIBUTIONS_PER_KIND)
}

function permissionList(raw: unknown, field: string, errors: ManifestError[]): PluginPermission[] {
  const out: PluginPermission[] = []
  for (const entry of strList(raw)) {
    if (isPluginPermission(entry)) {
      if (!out.includes(entry)) out.push(entry)
    } else {
      errors.push({ field, message: `unknown permission "${entry}"; expected one of ${PLUGIN_PERMISSIONS.join(', ')}` })
    }
  }
  return out
}
