/**
 * 插件生命周期 —— 装载、激活、休眠、禁用、卸载,以及 RPC 的四道门。
 *
 * ## 这个文件不 import electron
 *
 * 依赖方向是 `shared ← kernel ← runtime ← ipc ← index`,而插件宿主窗口
 * 显然是 electron 的东西。解法是把「怎么跑插件代码」抽成 `PluginRuntime`:
 * 这里只说「激活它」,由 `host-window.ts` 用 BrowserWindow + iframe 实现,
 * 测试里换成一个假的。
 *
 * 好处不只是可测:它把**策略**(谁能激活、什么时候休眠、哪条 RPC 放行)
 * 和**机制**(iframe、MessagePort、CSP)分在两个文件里,而前者是会被反复
 * 修改的那一半。
 *
 * ## 失败一律变诊断,永不 throw 到调用方
 *
 * 一个装不上的插件不该让插件系统起不来,更不该让应用起不来 ——
 * 同 `kernel/skill/load.ts` 的取向。所有失败落在 `InstalledPlugin.diagnostics`
 * 上,插件详情页逐条展示。
 */
import { join } from 'node:path'
import { promises as fs } from 'node:fs'
import type { KernelHost } from '../kernel/host'
import {
  PLUGIN_CONTEXT_LIMIT,
  PLUGIN_METHOD_PERMISSION,
  PLUGIN_TIMEOUT,
  isPluginMethod,
  type PluginErrorCode,
  type PluginInvocation,
  type PluginMethod,
  type PluginRequest,
  type PluginResponse
} from '../../shared/plugin/protocol'
import {
  canRequest,
  grantPermissions,
  hasPermission,
  permissionEscalated,
  revokePermissions,
  sortPermissions,
  type PluginPermission,
  type PluginPermissionState
} from '../../shared/plugin/permission'
import type { PluginManifest } from '../../shared/plugin/manifest'
import { matchesHostPermission } from '../../shared/plugin/manifest'
import { PLUGIN_API_VERSION, engineCompatibility } from '../../shared/plugin/api-version'
import type {
  InstalledPlugin,
  PluginCatalog,
  PluginDiagnostic,
  PluginStatus,
  PluginStatusBarItem
} from '../../shared/plugin/state'
import type {
  PluginInteractionRequest,
  PluginProgressUpdate,
  PluginTabTarget
} from '../../shared/plugin/ui-request'
import { installPluginDirectory, installPluginZip, readInstalledManifest } from './installer'
import { explainUnsupported, inactiveContributions } from './unsupported'
import { recordActivity, clearActivity } from './diagnostics'
import { abortable } from '../kernel/abort'
import { CapabilityError, invokeCapability, prepareExec, type CapabilityContext, type CapabilityOutcome, type PluginScmAdapter } from './rpc'
import { matchesPathScope, narrowCommand, narrowLaunchEnv, narrowWorkspacePath, wrapPluginContext } from './capabilities'
import { isValidPluginToolName, pluginToolId, toolRegistrationFor, type PluginToolDeclaration } from './tools'
import { sanitizeToolCard } from '../../shared/agent/tool-card'
import type { ToolProgress } from '../../shared/agent/tool'
import type { ToolRegistration } from '../kernel/tool/registry'
import type { PluginSkillRoot } from '../kernel/skill/load'
import { SUPPORTED_LOCALE_FILES, localeOfFile } from './locale-files'
import { PluginCapabilityError, type DocumentCallScope, type PluginDocumentsBridge } from './document-rpc'

/** 插件目录名。`ipc/storage.ts:148` 的删除清单里已经有它。 */
export const PLUGINS_DIR = 'plugins'

/** 空闲多久休眠。计划 §2.3:懒激活 + 空闲 5 分钟休眠。 */
const IDLE_SLEEP_MS = 5 * 60 * 1000

/** 一次 quickPick 最多给几项 —— 再多用户也挑不动,而且那通常意味着该换个 UI。 */
const MAX_QUICK_PICK_ITEMS = 100

/** 单插件同时最多挂几条进度。理由同状态栏格数上限。 */
const MAX_PROGRESS_PER_PLUGIN = 3

/** 全宿主同时最多几条流式命令 —— 它们各占一个子进程。 */
const MAX_CONCURRENT_EXEC_STREAMS = 8

/** 流式输出合批的间隔。50ms 人眼看不出延迟,却能把上百次 invoke 压成几次。 */
const EXEC_STREAM_BATCH_MS = 50

/** 一条流式命令最多往插件转发多少字符。超了只推一条 `truncated`,不再转发正文。 */
const MAX_EXEC_STREAM_CHARS = 512 * 1024

/**
 * 被中断 / 超时的流式命令回给插件的退出码。
 *
 * ★ 124 是 `timeout(1)` 的约定,`node-spawn.ts` 的超时分支用的也是它 ——
 * 两处取同一个值,插件作者只需要认识一个数。
 */
const TIMEOUT_EXIT_CODE = 124

/**
 * deadline 到点时的 abort 标记。
 *
 * ★ 取消这个动作有两个来源 —— **deadline 到点**和**插件被禁用 / shutdown** ——
 * 而插件要看到的是两句不同的话(「超时了」vs「这次调用已经被取消了」)。
 * 用同一个共享的 reason 对象区分:只有到点的那一次是它。
 */
const TIMEOUT_ABORT_REASON = Object.freeze({ kind: 'plugin_timeout' })

function isTimeoutAbort(signal: AbortSignal): boolean {
  return signal.aborted && signal.reason === TIMEOUT_ABORT_REASON
}

/**
 * **按工作区定位**的方法:`callId` 在这几个上有含义,其余方法上它只是普通数据。
 *
 * ★ 这份表就是「哪些 RPC 会被受信作用域覆盖」的答案,而且它必须和 `protocol.ts`
 * 里挂了 `PluginScopeParam` 的方法一一对应 —— 两边不一致的表现是「某个方法
 * 明明在协议上写着可以带 callId,却按焦点工作区走」,而那是一类**静默**的错误
 * (它照样成功,只是写错了地方)。所以这里只列 `protocol.ts` 声明了 callId 的那些。
 */
const WORKSPACE_SCOPED_METHODS: ReadonlySet<PluginMethod> = new Set<PluginMethod>([
  'workspace.folders',
  'workspace.readFile',
  'workspace.writeFile',
  'workspace.deleteFile',
  'workspace.stat',
  'workspace.findFiles',
  'process.exec',
  'process.execStream',
  'scm.status',
  'scm.diff',
  'scm.log',
  'scm.branches',
  'scm.stage',
  'scm.commit',
  'scm.createBranch',
  'scm.checkout',
  'storage.get',
  'storage.set',
  'storage.keys'
])

/**
 * 解析 `callId` 的**许可表**。
 *
 * ★ 它就是 `WORKSPACE_SCOPED_METHODS` 加上 `net.fetch` —— 后者要 callId 是为了
 * **取消**(net.fetch 绑定工具取消),并不是因为它按工作区定位:网络请求与工作区
 * 无关,它的作用域是 `hostPermissions`。
 *
 * ★ 单独一张表而不是「往工作区表里塞一个 net.fetch」,是因为两者回答的问题不同:
 * 把 `net.fetch` 放进工作区表会让 `resolveCallWorkspace` 去给它算一个工作区、
 * 在工作区被切走时把它判 `[stale_scope]` —— 那不是这次加固要的东西。
 *
 * ★ 两处解析 callId 的地方(作用域、signal 继承)都只认这张表 —— 于是
 * `diagnostics.log` / `tool.progress` 这些方法上同名的普通字段永远不会被当成 scope。
 */
const CALL_SCOPED_METHODS: ReadonlySet<PluginMethod> = new Set<PluginMethod>([
  ...WORKSPACE_SCOPED_METHODS,
  'net.fetch'
])

/**
 * 这个方法会不会按 `callId` 定位到产生它的那次工具调用。
 *
 * ★ `documents.*` 是单独一类:它们的作用域走 `resolveDocumentScope`(另一套解析,
 * 同一个 callId 语义),所以不放进上面那张表 —— 但 signal 继承是同一条规矩:
 * 一次 `documents.save` 只要属于某次工具调用,就该跟着那次调用的 abort 一起停。
 */
function isGlobalStorageRequest(method: PluginMethod, params: unknown): boolean {
  return (method === 'storage.get' || method === 'storage.set' || method === 'storage.keys')
    && params !== null && typeof params === 'object' && !Array.isArray(params)
    && (params as Record<string, unknown>).scope === 'global'
}

function resolvesCallId(method: PluginMethod, params: unknown): boolean {
  // 全局 KV 与本地工作区无关，远程、无工作区和并发工具都必须能够读取它。
  return !isGlobalStorageRequest(method, params) && (CALL_SCOPED_METHODS.has(method) || method.startsWith('documents.'))
}

/**
 * 挂在**派生的调用 signal** 上的「关系」标记 —— `child.abort(TOOL_CANCELLED_REASON)`。
 *
 * ★ 为什么不能像 deadline 那样用一个共享对象:每一次调用的派生 controller 都要
 * 听**两条**上游 —— 宿主自己的 deadline,和产生它的那次工具调用。工具被中止是
 * 「用户按了停止」,不是「RPC 超时」,两者对插件是两句不同的话;用共享标记的话
 * 任何一条上游 abort 都会被读成超时。
 */
const TOOL_CANCELLED_REASON = Object.freeze({ kind: 'tool_cancelled' })

/**
 * 一次调用里**已经开始**的副作用。
 *
 * ★ 它替代了原先那张「哪些方法有副作用」的静态表:表回答不了「这一次到底走到
 * 哪了」。审批迟到、在文档队列里排太久、路径门被拒 —— 这些都在动手**之前**
 * 就失败了,重试是安全的;只有越过 `markSideEffectStarted` 之后的取消才必须
 * 报 `[result_unknown]`。分不开的后果是**两向都错**:老老实实在排队时被取消的
 * 调用被误报成「可能已执行」,而一次真的写完了的调用反倒被当成可安全重试。
 */
interface SideEffectTracker {
  started: boolean
}

/** 持久化的那一小块:用户的决定。清单本身每次从盘上重读。 */
interface PersistedPlugin {
  enabled: boolean
  granted: PluginPermission[]
  /** 上一次批准时的必选能力 —— 升级后拿它比对,判断有没有扩权 */
  approvedRequired: PluginPermission[]
  installedAt: number
  updatedAt: number
  /**
   * 这个插件是从市场哪一条装来的。**只有市场安装才有值。**
   *
   * ★ 它回答的不是「叫什么」,而是「这个插件跟不跟市场走」。本地 ZIP /
   * 目录装进来的那份没有 slug —— 用户手上可能是他自己改过的构建,
   * 一次「全部更新」把它换成市场版,而他从没说过这个插件要跟市场走。
   *
   * ★ 不存进 `InstalledPlugin`:渲染层不需要知道 slug,更新检测整个在
   * 主进程里完成,推给渲染层的是算好的 `PluginUpdate`。
   */
  slug?: string
}

type PersistedState = Record<string, PersistedPlugin>

const KV_KEY = 'plugins.state'

/** 「怎么把插件代码跑起来」。实现在 `host-window.ts`,测试里换成假的。 */
export interface PluginRuntime {
  /** 建一个隔离上下文并等它握手完成。超时/崩溃都必须 reject。 */
  spawn(plugin: { id: string; root: string; main: string }): Promise<void>
  /** 宿主 → 插件的反向调用。 */
  invoke(pluginId: string, invocation: PluginInvocation, timeoutMs: number): Promise<unknown>
  /** 销毁这个插件的隔离上下文。幂等。 */
  dispose(pluginId: string): void
  disposeAll(): void
  /**
   * 这个插件的宿主窗口此刻**真的还活着**吗(存在且没被销毁)。
   *
   * ★ 崩溃(`render-process-gone`)不会回头改 manager 的 `record.status` —— 于是
   * status 可能停在 `active`,而窗口早没了。`wake` 用它在早返回前核实一次:不在了
   * 就往下走重新 spawn(spawn 会先 dispose 掉死窗口)。可选:测试的假 runtime 不实现
   * 时按「还活着」处理,保持旧行为。
   */
  isRunning?(pluginId: string): boolean
}

export interface PluginManagerDeps {
  host: KernelHost
  runtime: PluginRuntime
  /** 全局插件根。工作区级的走 `<ws>/.next-cowork/plugins/`,由调用方另给 */
  pluginRoot: string
  /** 应用版本。**不参与 `engines` 判定**(那是 `apiVersion` 的事),只进 catalog 与市场请求 */
  hostVersion: string
  /**
   * 这个宿主实现的插件 API 版本。缺省 `PLUGIN_API_VERSION`。
   *
   * ★ 可注入而不是直接读常量:`engines` 判定是装载期最关键的一条分支,
   * 测试必须能在不跟着改常量的前提下钉住它的三种结局(ok / deprecated / incompatible)。
   */
  apiVersion?: string
  getKv: <T>(key: string, fallback: T) => T
  setKv: (key: string, value: unknown) => void
  /** 当前工作区。路径类能力全部以它为根 */
  currentWorkspace: () => { id: string; rootPath: string }
  /** 活动文档桥。旧测试 / 旧宿主可省略；调用 documents.* 时省略即报宿主未接线。 */
  documents?: PluginDocumentsBridge
  /** 按受信 workspaceId 解析本地工作区；远程或已关闭工作区返回 null。 */
  resolveWorkspace?: (workspaceId: string) => { id: string; rootPath: string } | null
  /** 宿主当前的深浅色。插件只读,不挂权限(见 `PLUGIN_METHOD_PERMISSION`) */
  currentAppearance: () => 'light' | 'dark'
  /**
   * 跑命令前问用户。**只剩 `exec`** —— 写/删已经由能力门独自把关,
   * 见 `rpc.ts` 里 `CapabilityContext.approve` 的注释。
   */
  approve: (pluginId: string, summary: { kind: 'exec'; detail: string }) => Promise<boolean>
  /** 把文件挪进系统废纸篓(`shell.trashItem`)。插件删文件唯一的落点。 */
  trash: (absolutePath: string) => Promise<void>
  /** 交给系统浏览器打开(`shell.openExternal`)。URL 门在 `rpc.ts` 里,这里只负责落地。 */
  openExternal: (url: string) => Promise<void>
  /** 剪贴板。能力 `clipboard` 由能力门把关。Electron 这一版的 clipboard 是 Promise 形状的。 */
  clipboard: { readText: () => Promise<string>; writeText: (text: string) => Promise<void> }
  /**
   * 给某个工作区造一个 scm 适配器。
   *
   * ★ 按 workspaceId 现造而不是常驻一个:工作区是会切的,而一个记着旧 root 的
   * 适配器的症状是「插件报告的分支不是我正在看的这个仓库的」。
   */
  scmFor: (workspaceId: string) => PluginScmAdapter
  /** catalog 变了,推 `plugins:changed` */
  emitChanged: () => void
  /** 插件的 l10n bundle 注册到渲染层 */
  publishMessages: (pluginId: string, locale: string, dict: Record<string, string>) => void
  unpublishMessages: (pluginId: string) => void
  /**
   * 运行期申请能力。**只会在 `canRequest` 通过之后被调用** ——
   * 清单上界之外的申请根本到不了这里(见 `handleHostMethod`)。
   */
  requestPermissions: (pluginId: string, permissions: readonly PluginPermission[], reasonKey: string) => Promise<boolean>
  /** 贡献的工具变了 —— 下一次装配要重新问一遍 provider */
  onToolsChanged: () => void
  /**
   * 预留一个插件工具的 externalName —— **不注册**,只查权威名字。委托给
   * `ToolRegistry.reserveName`(记忆化 namer),catalog 投影用。
   *
   * ★ 插件工具只有被激活后跑 `tools.register` 才真正 register,而工具卡片
   * 贡献要在激活**之前**就画出来,渲染层此刻已需要 externalName 去 join。
   * 因 namer 记忆化,此处预留的名字与将来 register 分配的**同一个**,不失配。
   */
  reserveName: (internalId: string) => string
  /**
   * 插件要给用户看一条消息。
   *
   * ★ 传的是 **key + params**,不是句子。渲染层 `t()` 之后才是人话 ——
   * 主进程侧一律不产出用户可见的裸文本(计划 §8.3)。
   */
  showMessage: (
    pluginId: string,
    kind: 'info' | 'warn' | 'error',
    messageKey: string,
    params: Record<string, string | number>
  ) => void
  /**
   * 为某个文件打开这个插件的自定义编辑器 Tab。
   *
   * ★ 主进程这一侧**只做校验与转发**,不认识 Tab:Tab 是渲染层的概念,
   * 而「哪一格、放在哪个 dock group」那些决定全在 `stores/tabs.ts` 里。
   * 这里把它当成一条广播出去,与 `showMessage` 同一形状。
   */
  openCustomEditor: (pluginId: string, viewType: string, path: string) => void
  /**
   * 打开一个网页 / 视图 Tab。
   *
   * ★ 形状与理由同 `openCustomEditor`:主进程**只校验与转发**,不认识 Tab ——
   * 「放哪一格、哪个 dock group、已经开着要不要复用」那些决定全在
   * `stores/tabs.ts` 里,抄一份到主进程一定会和它分叉。
   */
  openTab: (pluginId: string, target: PluginTabTarget) => void
  /**
   * 在指定工作区根目录开一个终端 Tab 并启动命令(`tabs.openTerminal` 的落地)。
   *
   * ★ 分工:manager 那边只做**清单级参数门**(allowedCommands / env 体量),
   * 「工作区存不存在、是不是远程、spec 备在哪」这些要碰工作区存储与
   * TerminalHost 的事全在这一侧 —— 测试里的假 deps 因此不必起任何 pty。
   *
   * 返回 `opened: false` 而不是抛:同 `openWebApp`,「这个工作区开不了」
   * 是调用方(插件)要拿去给用户的一句话,不是一次系统故障。
   */
  launchTerminal: (
    pluginId: string,
    spec: { workspaceId: string; command: string; args: string[]; env: Record<string, string>; title?: string }
  ) => { opened: boolean; reason?: 'remote' | 'declined' }
  /**
   * 向用户发起一次交互(选项 / 输入 / 确认),等他回答。
   *
   * ★ 主进程**不画 UI**:这里只把请求广播出去,由渲染层用既有的
   * `components/ui/**` 画,再把回执送回来。放主进程画的话,它就得用
   * `dialog.showMessageBox` 拼裸文本 —— 那既不跟随主题,也不跟随语言
   * (`ipc/plugins.ts` 里那两个遗留的系统框就是这个样子)。
   *
   * 用户直接关掉 = 取消值(`null` / `false`),不是错误。
   */
  requestInteraction: (pluginId: string, request: PluginInteractionRequest) => Promise<unknown>
  /** 进度条的推送与撤销。禁用插件时宿主会把它那几条一起撤掉。 */
  emitProgress: (pluginId: string, progress: PluginProgressUpdate) => void
}

/*
  ★ `PluginTabTarget` / `PluginInteractionRequest` 住在 `shared/plugin/ui-request.ts`,
  不在这个文件里:它们要穿过 IPC 到渲染层,而 `shared/ipc/contract.ts` 引不到
  `main/**`(单向依赖)。放这边的话,contract 那边就只能各抄一份形状。
*/

interface PluginRecord {
  manifest: PluginManifest
  root: string
  scope: 'global' | 'workspace'
  status: PluginStatus
  enabled: boolean
  granted: PluginPermission[]
  approvedRequired: PluginPermission[]
  diagnostics: PluginDiagnostic[]
  installedAt: number
  updatedAt: number
  /** 从市场哪一条装来的,本地包装的没有。见 `PersistedPlugin.slug` */
  slug?: string
  /** 最后一次被用到的时刻 —— 休眠判定用它 */
  touchedAt: number
  /** 这个插件注册过的命令 / 工具,禁用时要级联清掉 */
  commands: Set<string>
  tools: Map<string, PluginToolDeclaration>
  /** 注册过拦截器 / 上下文提供者吗 */
  interceptor: boolean
  contextProvider: boolean
  /** 订阅了主题变化吗 —— 只给订阅了的推,没订阅的不必为此醒着 */
  appearanceSubscriber: boolean
  /** 状态栏那几格。禁用时清空 —— 插件没了,它的读数不该还挂在那儿 */
  statusBar: Map<string, PluginStatusBarItem>
  /** 有未保存改动的自定义编辑器文档:`documentId → 文件路径` */
  dirtyDocuments: Map<string, string>
  /**
   * 命令的品牌图标(`iconFile`)读出的 data URL,按 commandId 索引。
   * 装载时读一次;catalog 投影(`project`)原样带走 —— 菜单在激活前就要画。
   */
  commandIcons: Record<string, string>
  /** 本插件对外导出的 API 方法名(`ncw.plugins.exposeApi`)。禁用时清空。 */
  exposedApi: Set<string>
  /** 本插件订阅了的事件 topic —— 禁用时要从全局订阅表里摘掉。 */
  subscribedTopics: Set<string>
  /**
   * 工作区变更订阅的 glob 前缀。`undefined` = 没订阅,`[]` = 订阅全部。
   *
   * ★ 两者必须分得开:空数组当成「没订阅」的话,`onDidChangeFiles(h)`
   * (不带 glob,意思是「全都要」)会一条都收不到。
   */
  watchGlobs?: string[]
  /** 文档变更通知绑定订阅时的工作区，后台写入不能污染前台订阅。 */
  watchWorkspaceId?: string
  /** 这一刻它挂着的进度条 id —— 禁用 / 休眠时要全部撤掉,不留下转不停的条。 */
  progress: Set<string>
}

export class PluginManager {
  private readonly records = new Map<string, PluginRecord>()
  private sleepTimer: NodeJS.Timeout | undefined
  /** 覆盖安装期间压住 `plugins:changed` —— 见 `install()` 里的说明 */
  private suppressChanged = false
  /**
   * 正在执行的插件工具的 `ctx.emit`,按 callId 索引。工具运行期间存在,结束即删。
   *
   * ★ 带 `pluginId`:`tool.progress` RPC 要核对调用方就是这次工具调用的主人,
   * 否则一个插件能拿别人工具的 callId 往它的卡片上推东西(callId 虽不易猜,但
   * 「不易猜」不是授权)。
   *
   * ★ 带 `workspaceId`:这是**受信作用域**的完整一份,供
   * `resolveCallWorkspace` 把工作区类 RPC 钉在产生它的那次调用上。只给 workspaceId
   * 不够 —— 解析它要经 `deps.resolveWorkspace`,而后者可能认不出(远程/已关闭),
   * 那时必须**拒绝**,不能回退到当前聚焦的工作区。
   */
  private readonly liveToolEmits = new Map<string, {
    pluginId: string
    workspaceId?: string
    /** 宿主原始 `ToolContext.signal` —— 用户点停止时到达这里,见 handleRequest 的转发。 */
    signal: AbortSignal
    emit: (progress: ToolProgress) => void
  }>()
  /**
   * 正在进行的 RPC 调用,按 `pluginId → 一组 { controller, deadline 定时器 }`。
   *
   * ★ 存在的理由是**禁用 / 卸载 / shutdown 要能取消这个插件未完成的调用**。
   * 只靠超时的话,一次卡在网络上的调用会活过插件的生命周期 —— 它的宿主窗口
   * 已经销毁了,而主进程还在为它把响应读完、甚至把文件写完。
   *
   * ★ 定时器**跟 controller 一起记在这里**。分开记的话,取消这条路要清定时器
   * 就得再去别处找它 —— 而"忘了清"的症状是 30 秒后一条对着空气的 abort。
   *
   * 普通 RPC 与 `process.execStream` 分开记:后者的 abort 语义要回一条
   * `process.exit`(见 `finishExec`),由它自己的表管;这里只管「一次性调用」。
   */
  private readonly liveRequests = new Map<string, Set<{ controller: AbortController; timer: NodeJS.Timeout }>>()
  /** 需求：先排空文档请求再检查 dirty；收尾期间的新写入不能越过这次检查。 */
  private documentsClosing = false
  /** 事件总线的订阅表:topic → 订阅它的 pluginId 集合(第 5 层)。 */
  private readonly eventSubscribers = new Map<string, Set<string>>()
  /**
   * 正在跑的流式命令:`execId → { 主人, 中断句柄 }`。
   *
   * ★ 带 `pluginId` 的理由同 `liveToolEmits`:`process.execAbort` 要核对调用方
   * 就是这条命令的主人,否则一个插件能掐断另一个插件正在跑的构建。
   */
  /**
   * 正在跑的流式命令:`execId → { 主人, 中断句柄, 收尾 }`。
   *
   * ★ 带 `pluginId` 的理由同 `liveToolEmits`:`process.execAbort` 要核对调用方
   * 就是这条命令的主人,否则一个插件能掐断另一个插件正在跑的构建。
   *
   * ★ `controller` **只**接两个来源:`process.execAbort` 和插件禁用 / shutdown。
   * 启动它的那条 RPC 的 30 秒期限不进这里 —— 那条命令可能跑几分钟(见
   * `startExecStream` 的说明)。它仍然跟随产生它的那次工具调用的 abort,由
   * `detach` 表示的那个 listener 负责。
   */
  private readonly execStreams = new Map<string, {
    pluginId: string
    controller: AbortController
    /** 摘掉挂在工具 signal 上的 listener —— 命令无论怎么结束都要调一次。 */
    detach: () => void
  }>()
  private nextExecId = 1
  /**
   * 工作区在**同一次插件激活周期内**的代号,按 **workspaceId** 记。
   *
   * ★ 它回答的是「这次调用开始时冻结的那个工作区,到现在还是同一个根吗」。
   * 同一个 id 被重新指向另一个 root(切库、重建工作区)时代号要变 —— 变了就
   * 说明这次调用**不能再写**了:它拿到的是旧作用域。
   *
   * ★★ **必须是按 id 的一张表,不能是一个全局计数器。** 全局计数的话,一次指向
   * A 的调用和一次指向 B 的调用会互相把代号翻掉 —— 于是两个都正常、互不相干的
   * 并发调用会各自收到一条 `[stale_scope]`,而工作区谁都没动过。
   */
  private readonly workspaceGenerations = new Map<string, { rootPath: string; generation: number }>()
  private nextWorkspaceGeneration = 0

  constructor(private readonly deps: PluginManagerDeps) {}

  /** 冻结这一次调用的作用域代号。同一个 id + 同一个根拿到的是同一个值。 */
  private markWorkspaceGeneration(workspace: { id: string; rootPath: string }): number {
    const current = this.workspaceGenerations.get(workspace.id)
    if (current !== undefined && current.rootPath === workspace.rootPath) return current.generation
    const generation = ++this.nextWorkspaceGeneration
    this.workspaceGenerations.set(workspace.id, { rootPath: workspace.rootPath, generation })
    return generation
  }

  private workspaceGenerationOf(workspaceId: string): number | undefined {
    return this.workspaceGenerations.get(workspaceId)?.generation
  }

  /** 只有**中间态**走这条(目前是 `disable()`);终态一律直接 `deps.emitChanged()` */
  private notifyChanged(): void {
    if (this.suppressChanged) return
    this.deps.emitChanged()
  }

  /**
   * 扫描插件目录,读清单,恢复用户的决定。**不激活任何东西。**
   *
   * ★ 激活由激活事件驱动(`onCommand` / `onView` / `onTool` …)。启动时
   * 一律不激活,是因为「每个激活的插件 = 一个 renderer 进程」这件事让
   * 启动期激活成为这套架构下唯一的内存风险源。
   */
  async start(): Promise<void> {
    const persisted = this.deps.getKv<PersistedState>(KV_KEY, {})
    await fs.mkdir(this.deps.pluginRoot, { recursive: true }).catch(() => undefined)
    const entries = await fs.readdir(this.deps.pluginRoot, { withFileTypes: true }).catch(() => [])
    for (const entry of entries) {
      // 安装中断留下的 staging / backup 残片:清掉,别当成插件读。
      if (!entry.isDirectory()) continue
      if (entry.name.includes('.installing-') || entry.name.includes('.backup-')) {
        await fs.rm(join(this.deps.pluginRoot, entry.name), { recursive: true, force: true }).catch(() => undefined)
        continue
      }
      await this.load(join(this.deps.pluginRoot, entry.name), 'global', persisted[entry.name])
    }
    this.sleepTimer = setInterval(() => { this.sweepIdle() }, 60_000)
    this.sleepTimer.unref?.()
    this.persist()
  }

  /** 需求：退出/切账户在关窗与切库前排空文档请求；否决退出时可恢复文档调用。 */
  async guardDocumentClose(discardChanges = false): Promise<() => void> {
    if (this.documentsClosing) throw new CapabilityError('rejected', 'document sessions are closing')
    this.documentsClosing = true
    try {
      // 需求：只有宿主拿到用户的明确丢弃确认才走此分支，普通退出永远只做 dirty 检查。
      if (discardChanges) await this.deps.documents?.discardAll?.()
      await withTimeout(this.deps.documents?.assertCanRelease?.() ?? Promise.resolve(), PLUGIN_TIMEOUT.REQUEST_MS)
      return () => { this.documentsClosing = false }
    } catch (error) {
      this.documentsClosing = false
      throw error
    }
  }

  async shutdown(guarded = false): Promise<void> {
    if (!guarded) await this.guardDocumentClose()
    else if (!this.documentsClosing) throw new CapabilityError('rejected', 'document close guard is not held')
    try {
      // 需求：安全检查在销毁插件之前完成，失败时仍可回到原插件保存文档。
      if (this.sleepTimer !== undefined) clearInterval(this.sleepTimer)
      /*
        ★ 收尾 = 所有插件的在途调用(含流式命令)一起取消。这里不是「顺手」:
        `runtime.disposeAll()` 之后宿主窗口全没了,而一个还在读网络的调用会让
        主进程在退出路径上多留一会儿 —— 更糟的是它可能把一次写做完。
      */
      this.abortAllRequests()
      for (const [id, record] of this.records) {
        if (record.status === 'active') await this.deactivate(id).catch(() => undefined)
        await this.deps.documents?.releasePlugin(id)
      }
      this.deps.runtime.disposeAll()
    } catch (error) {
      this.documentsClosing = false
      throw error
    }
  }

  /** 需求：原生 helper 沿用 process 的行为审批，不能只有能力授权就绕过既有执行确认。 */
  async approveNativeHelper(pluginId: string, entry: string): Promise<boolean> {
    const record = this.records.get(pluginId)
    if (record === undefined || !record.enabled || !record.granted.includes('process')) return false
    const approved = await this.deps.approve(pluginId, { kind: 'exec', detail: entry })
    return approved && this.records.get(pluginId) === record && record.enabled && record.granted.includes('process')
  }

  catalog(): PluginCatalog {
    return {
      hostVersion: this.deps.hostVersion,
      apiVersion: this.apiVersion(),
      plugins: [...this.records.entries()]
        .map(([id, record]) => this.project(id, record))
        .sort((a, b) => a.manifest.displayName.localeCompare(b.manifest.displayName))
    }
  }

  private apiVersion(): string {
    return this.deps.apiVersion ?? PLUGIN_API_VERSION
  }
  private project(id: string, record: PluginRecord): InstalledPlugin {
    const permissions: PluginPermissionState = {
      required: record.manifest.permissions,
      optional: record.manifest.optionalPermissions,
      granted: record.granted
    }
    const pending = record.manifest.permissions.filter((p) => !record.approvedRequired.includes(p))
    /*
      ★ 工具的 externalName 投影从**清单**(contributes.tools)算,不从运行期
      `record.tools` —— 后者只有激活后才有,而卡片贡献要在激活前就画出来。
      `reserveName` 记忆化,与将来真正 register 时分配的名字一致(见 deps 注释)。
    */
    const tools = record.manifest.contributes.tools.map((t) => ({
      name: t.name,
      externalName: this.deps.reserveName(pluginToolId(id, t.name))
    }))
    const commandIcons = record.commandIcons
    return {
      id,
      manifest: record.manifest,
      status: record.status,
      scope: record.scope,
      path: record.root,
      enabled: record.enabled,
      permissions,
      diagnostics: record.diagnostics,
      unsupported: record.manifest.contributes.unsupported,
      installedAt: record.installedAt,
      updatedAt: record.updatedAt,
      statusBar: [...record.statusBar.values()],
      ...(tools.length > 0 ? { tools } : {}),
      ...(Object.keys(commandIcons).length > 0 ? { commandIcons } : {}),
      ...(pending.length > 0 ? { pendingPermissions: pending } : {})
    }
  }

  /**
   * 读清单里每条命令的 `iconFile`,转成 data URL 随 catalog 下发。
   *
   * ★ **读不出来不是装载失败**。图标是装饰,一个被升级/迁移弄丢的 svg 不该把
   * 整个插件顶成 `error` —— 丢的是这一条命令的图标,菜单回落到名字闭集,
   * 诊断里留一条 warn 供作者排查。与 `publishLocaleBundles` 同一取向。
   *
   * ★ 尺寸上限在这里落:装载时读进内存、再随每次 catalog 广播整体复制,
   * 不设限的话一个「图标」就是一条常驻的内存税。
   */
  private async readCommandIcons(
    manifest: PluginManifest,
    root: string,
    diagnostics: PluginDiagnostic[]
  ): Promise<Record<string, string>> {
    const out: Record<string, string> = {}
    const maxBytes = 32 * 1024
    for (const command of manifest.contributes.commands) {
      if (command.iconFile === undefined) continue
      try {
        const bytes = await fs.readFile(join(root, command.iconFile))
        if (bytes.byteLength > maxBytes) {
          diagnostics.push({ path: `contributes.commands.${command.command}.iconFile`, message: `icon file is larger than ${maxBytes} bytes; ignored`, level: 'warn' })
          continue
        }
        const mime = command.iconFile.toLowerCase().endsWith('.svg') ? 'image/svg+xml' : 'image/png'
        out[command.command] = `data:${mime};base64,${bytes.toString('base64')}`
      } catch (error) {
        diagnostics.push({ path: `contributes.commands.${command.command}.iconFile`, message: `icon file could not be read: ${(error as Error).message}`, level: 'warn' })
      }
    }
    return out
  }

  // ─────────────────────────── 装载 ───────────────────────────

  private async load(
    root: string,
    scope: 'global' | 'workspace',
    persisted: PersistedPlugin | undefined
  ): Promise<void> {
    const diagnostics: PluginDiagnostic[] = []
    let manifest: PluginManifest
    try {
      manifest = await readInstalledManifest(root)
    } catch (error) {
      /*
        ★ 读不懂的清单**不进 records**:一个没有 id 的东西没法被启用、禁用、
        卸载,放进表里只会变成一行点什么都没反应的条目。日志里说明白就够。
      */
      this.deps.host.logger.warn(`[plugin] ${root}: ${(error as Error).message}`)
      return
    }

    for (const key of manifest.contributes.unsupported) {
      diagnostics.push({ path: `contributes.${key}`, message: explainUnsupported(key), level: 'warn' })
    }
    /*
      ★ 「认得、装进来了,但这一版还没接上」的那几个也要出诊断。
      不出的话就是**静默不生效** —— 作者写了 `contributes.skills`、包也装干净了,
      然后模型从来不知道有这条 Skill,而日志里一个字都没有。
    */
    for (const inactive of inactiveContributions({
      skills: manifest.contributes.skills.length,
      agents: manifest.contributes.agents.length,
      modes: manifest.contributes.modes.length,
      themes: manifest.contributes.themes.length,
      slashCommands: manifest.contributes.slashCommands.length,
      'views.location': manifest.contributes.views.filter((v) => (v.location ?? 'editor') !== 'editor').length,
      documentEngines: manifest.contributes.documentEngines?.length ?? 0
    })) {
      diagnostics.push({ ...inactive, level: 'info' })
    }

    let status: PluginStatus = 'idle'
    /*
      ★ 比的是**插件 API 版本**,不是应用版本。
      理由与那个「所有插件都被判成红色」的 bug 见 `shared/plugin/api-version.ts`
      的文件头 —— 这里原本拿 `this.deps.hostVersion`(= `app.getVersion()`,现在是
      2.x)去比 `^0.2.0`,恒为 false。`hostVersion` 仍然保留在 deps 与 catalog 里:
      市场请求要用它做灰度,那是另一个问题。
    */
    const compatibility = engineCompatibility(manifest.engines, this.apiVersion())
    if (compatibility === 'incompatible') {
      diagnostics.push({
        path: 'engines.nextcowork',
        message: `requires ${manifest.engines}, this host implements plugin API ${this.apiVersion()}`,
        level: 'error'
      })
      status = 'error'
    } else if (compatibility === 'deprecated') {
      // 弃用但照常跑 —— 这批插件在此之前根本装不上,兼容它们不会破坏任何既有行为。
      diagnostics.push({
        path: 'engines.nextcowork',
        message: `"${manifest.engines}" targets a retired plugin API; update it to ^${this.apiVersion()}`,
        level: 'warn'
      })
    }

    const approvedRequired = persisted?.approvedRequired ?? []
    /*
      ★ 升级后必选能力变大 → `pending-approval`,**不批不激活**。
      这一条是「装完之后自动更新悄悄扩权」那条路上的闸门。
    */
    if (status !== 'error' && persisted !== undefined && permissionEscalated(approvedRequired, manifest.permissions)) {
      status = 'pending-approval'
      diagnostics.push({
        path: 'permissions',
        message: `this version asks for new permissions: ${manifest.permissions.filter((p) => !approvedRequired.includes(p)).join(', ')}`,
        level: 'warn'
      })
    }

    const now = Date.now()
    const enabled = persisted?.enabled ?? false
    /*
      ★ 「没启用」就是 `disabled`,不是 `idle`。两者在界面上是两句不同的话
      (「已禁用」vs「已启用,等着被叫醒」),而 `idle + enabled:false` 这种
      组合会让开关显示成关、状态显示成开 —— 用户看不出到底哪个是真的。
      装载失败与待批准优先级更高:它们和用户的开关无关。
    */
    this.records.set(manifest.id, {
      manifest,
      root,
      scope,
      status: status === 'error' || status === 'pending-approval' ? status : enabled ? 'idle' : 'disabled',
      enabled,
      granted: sortPermissions(persisted?.granted ?? []),
      approvedRequired,
      diagnostics,
      installedAt: persisted?.installedAt ?? now,
      updatedAt: now,
      ...(persisted?.slug === undefined ? {} : { slug: persisted.slug }),
      touchedAt: 0,
      commands: new Set(),
      tools: new Map(),
      commandIcons: await this.readCommandIcons(manifest, root, diagnostics),
      interceptor: false,
      contextProvider: false,
      appearanceSubscriber: false,
      statusBar: new Map(),
      dirtyDocuments: new Map(),
      exposedApi: new Set(),
      subscribedTopics: new Set(),
      progress: new Set()
    })

    await this.publishLocaleBundles(manifest, root, diagnostics)
  }

  /**
   * 把包里的 l10n bundle 注册进渲染层的运行期 catalog。
   *
   * ★ 在**装载时**做,不是激活时:菜单项、命令名这些在插件还没被激活之前
   * 就要显示出来(菜单本身就是激活事件的来源)。激活时才注册的话,
   * 用户看到的是一排 key,点一下才变成文字。
   */
  private async publishLocaleBundles(
    manifest: PluginManifest,
    root: string,
    diagnostics: PluginDiagnostic[]
  ): Promise<void> {
    if (manifest.l10n === undefined) return
    for (const file of SUPPORTED_LOCALE_FILES) {
      const path = join(root, manifest.l10n, file)
      try {
        const raw = await fs.readFile(path, 'utf8')
        const parsed = JSON.parse(raw) as Record<string, unknown>
        const dict: Record<string, string> = {}
        for (const [key, value] of Object.entries(parsed)) {
          if (typeof value !== 'string') continue
          // 包里写的是 `cmd.new`,注册进去的是 `plugin.<id>.cmd.new` —— 前缀由宿主拼。
          dict[`plugin.${manifest.id}.${key}`] = value
        }
        this.deps.publishMessages(manifest.id, localeOfFile(file), dict)
      } catch (error) {
        diagnostics.push({
          path: `${manifest.l10n}/${file}`,
          message: `l10n bundle could not be read: ${(error as Error).message}`,
          level: 'warn'
        })
      }
    }
  }

  // ─────────────────────────── 安装 / 卸载 ───────────────────────────

  /**
   * @param expectedSha256
   * 市场给的**权威摘要**。目录安装没有这个东西(本地开发),ZIP 安装必须有 ——
   * 没有它的话,「摘要对得上」只证明了下载到的文件和它自己一致。
   *
   * @param slug
   * 市场那一条的 slug,只有市场安装传。见 `PersistedPlugin.slug`。
   */
  async install(path: string, expectedSha256?: string, slug?: string): Promise<void> {
    if (this.documentsClosing) throw new CapabilityError('rejected', 'document sessions are closing')
    this.documentsClosing = true
    try {
      await this.installAfterDocuments(path, expectedSha256, slug)
    } finally {
      this.documentsClosing = false
    }
  }

  private async installAfterDocuments(path: string, expectedSha256?: string, slug?: string): Promise<void> {
    const stat = await fs.stat(path)
    // 需求：安装器识别目标清单后、替换目录前做定向检查，不能让 A 的脏文档挡住 B 的安装。
    const beforeReplace = async (manifest: PluginManifest): Promise<void> => {
      await this.deps.documents?.assertCanRelease?.(manifest.id)
      await this.deps.documents?.releasePlugin(manifest.id)
    }
    const installed = stat.isDirectory()
      ? await installPluginDirectory(path, this.deps.pluginRoot, beforeReplace)
      : await installPluginZip(path, this.deps.pluginRoot, expectedSha256, beforeReplace)
    /*
      ★★ **旧配置必须在 `disable()` 之前读出来。**

      `disable()` 结尾会 `persist()` 一次,把 `enabled: false` 写回 KV。
      在它之后再 `getKv`,读到的「旧配置」里开关已经是关的了,于是
      `load()`(见 289 行 `persisted?.enabled ?? false`)把插件定成 `disabled` ——
      更新完版本号对了,插件被静默关掉,用户还得自己再去开一次。

      今天碰不到,是因为市场卡片在已安装时是 disabled 的、只有本地 picker
      能覆盖安装,没人试过。**更新功能一上线,这就是每次更新的必经之路。**
    */
    const persisted = this.deps.getKv<PersistedState>(KV_KEY, {})[installed.manifest.id]
    /*
      ★ 安装期间不播 `plugins:changed`。

      `disable()` 自己会播一条 —— 而这一刻插件确实是禁用的,只不过再过
      几十毫秒它就被新版本重新装载了。播出去的结果是列表闪一帧「已禁用」,
      而那一帧会被当成「更新把我的插件关掉了」报回来(讽刺的是上面那个
      bug 的表现和它一模一样,修好之后更没人分得清哪个是真的)。
    */
    this.suppressChanged = true
    try {
      if (this.records.has(installed.manifest.id)) {
        await this.deps.documents?.releasePlugin(installed.manifest.id)
        await this.disableAfterDocuments(installed.manifest.id)
      }
      await this.load(installed.target, 'global', persisted)
    } finally {
      this.suppressChanged = false
    }
    /*
      ★ slug 在 `load()` **之后**写,不混进 `persisted` 参数里。

      那个参数「有没有值」本身是有含义的 —— `load()` 的扩权闸门(279 行)
      以 `persisted !== undefined` 为前提。为了捎带一个 slug 就把 undefined
      伪造成 `{ slug: … }`,会让**首次**安装一个带必选能力的插件直接落进
      `pending-approval`,而它本该走正常的首次安装。

      ★ 本地覆盖安装时**清掉**旧 slug:用户手上这一份已经不是市场那一份了
      (多半是他自己改过的构建),不该再被「全部更新」悄悄换回去。
    */
    const record = this.records.get(installed.manifest.id)
    if (record !== undefined) {
      if (slug === undefined) delete record.slug
      else record.slug = slug
    }
    this.persist()
    this.deps.emitChanged()
  }

  /** 这个插件上一次批准时的必选能力。判断「更新会不会扩权」要的是它,不是 `granted` */
  approvedRequiredOf(pluginId: string): PluginPermission[] {
    return [...(this.records.get(pluginId)?.approvedRequired ?? [])]
  }

  /** 这个插件是从市场哪一条装来的。本地包装的返回 `undefined` */
  slugOf(pluginId: string): string | undefined {
    return this.records.get(pluginId)?.slug
  }

  async uninstall(pluginId: string): Promise<void> {
    const record = this.records.get(pluginId)
    if (record === undefined) return
    /*
      ★ 顺序:先下线(注销贡献 + 销毁上下文),再删目录。
      反过来的话,一次正在进行的调用会读到一个已经消失的包 —— 同
      `mcp/manager.ts:264` 的「下线先于关闭」。
    */
    await this.disable(pluginId)
    this.deps.unpublishMessages(pluginId)
    clearActivity(pluginId)
    this.records.delete(pluginId)
    const persisted = this.deps.getKv<PersistedState>(KV_KEY, {})
    delete persisted[pluginId]
    this.deps.setKv(KV_KEY, persisted)
    await fs.rm(record.root, { recursive: true, force: true }).catch(() => undefined)
    this.deps.emitChanged()
  }

  // ─────────────────────────── 启用 / 禁用 ───────────────────────────

  async setEnabled(pluginId: string, enabled: boolean): Promise<void> {
    const record = this.records.get(pluginId)
    if (record === undefined) return
    if (!enabled) { await this.disable(pluginId); return }
    if (record.status === 'error') return
    /*
      ★ 待批准的插件**启用不了**。这不是 UI 上的一个禁用态 —— 它必须在这里
      也挡住,否则任何一条绕过界面的路径(IPC 直调、命令面板)都能把一个
      扩了权的版本悄悄跑起来。
    */
    if (record.manifest.permissions.some((p) => !record.approvedRequired.includes(p))) {
      record.status = 'pending-approval'
      this.deps.emitChanged()
      return
    }
    record.enabled = true
    record.status = 'idle'
    this.persist()
    this.deps.emitChanged()
  }

  private async disable(pluginId: string): Promise<void> {
    // 需求：未装文档桥的旧宿主保留原来的同步下线时机，不能因 await undefined 延迟撤权。
    if (this.deps.documents === undefined) return this.disableAfterDocuments(pluginId)
    if (this.documentsClosing) throw new CapabilityError('rejected', 'document sessions are closing')
    this.documentsClosing = true
    try {
      // 需求：禁用不等于丢弃。先排空在途操作并拒绝未保存内容，再撤销插件及引擎。
      await this.deps.documents?.assertCanRelease?.(pluginId)
      await this.deps.documents?.releasePlugin(pluginId)
      await this.disableAfterDocuments(pluginId)
    } finally {
      this.documentsClosing = false
    }
  }

  private async disableAfterDocuments(pluginId: string): Promise<void> {
    const record = this.records.get(pluginId)
    if (record === undefined) return
    if (record.status === 'active' || record.status === 'activating') {
      await this.deactivate(pluginId).catch(() => undefined)
    }
    record.enabled = false
    record.status = 'disabled'
    /*
      ★ **下线先于销毁**(同 `mcp/manager.ts:264`)。反过来的话,注册表里
      还留着几个指向已销毁上下文的工具,模型下一轮照样会去调它们 ——
      而那次调用的失败信息里不会有任何线索指向「这个插件刚被禁用了」。
    */
    record.commands.clear()
    record.tools.clear()
    record.statusBar.clear()
    record.dirtyDocuments.clear()
    record.interceptor = false
    record.contextProvider = false
    record.appearanceSubscriber = false
    // 第 5 层:清导出的 API + 从全局事件订阅表里摘掉自己(不然会往一个已销毁的
    // 上下文投事件,每次 emit 都白发一次 invoke)。
    record.exposedApi.clear()
    for (const topic of record.subscribedTopics) this.eventSubscribers.get(topic)?.delete(pluginId)
    record.subscribedTopics.clear()
    /*
      ★ 进度条和工作区订阅也要跟着撤。
      漏掉进度条的症状是状态栏上留着一条**永远转下去**的进度 —— 它的主人已经
      不在了,没有任何人会再去调 `progressEnd`,而用户唯一的出路是重启。
    */
    for (const id of record.progress) this.deps.emitProgress(pluginId, { id, done: true })
    record.progress.clear()
    record.watchGlobs = undefined
    /*
      ★ **这个插件没做完的调用在这里被取消。** 只靠超时是不够的:它的宿主窗口
      下一步就没了,而一次卡在网络上的调用会活过插件的生命周期 —— 主进程替一个
      已经不存在的插件把响应读完,甚至把文件写完。取消放在 `deactivate` **之后**:
      先给插件一次在 `deactivate` 里收尾的机会,再收走它剩下的在途调用。
    */
    this.abortRequestsOf(pluginId)
    this.abortExecStreamsOf(pluginId)
    this.abortToolCallsOf(pluginId)
    this.deps.onToolsChanged()
    this.deps.runtime.dispose(pluginId)
    this.persist()
    // ★ 覆盖安装途中这一条要压住 —— 那一刻的「已禁用」只存在几十毫秒
    this.notifyChanged()
  }

  grant(pluginId: string, permissions: readonly PluginPermission[]): void {
    const record = this.records.get(pluginId)
    if (record === undefined) return
    const next = grantPermissions(
      { required: record.manifest.permissions, optional: record.manifest.optionalPermissions, granted: record.granted },
      permissions
    )
    record.granted = [...next.granted]
    // 必选能力一旦被批过,就记进 approvedRequired —— 下次升级拿它比对扩权。
    for (const p of record.manifest.permissions) {
      if (record.granted.includes(p) && !record.approvedRequired.includes(p)) record.approvedRequired.push(p)
    }
    if (record.status === 'pending-approval' && record.manifest.permissions.every((p) => record.approvedRequired.includes(p))) {
      record.status = record.enabled ? 'idle' : 'disabled'
    }
    this.persist()
    this.deps.emitChanged()
  }

  revoke(pluginId: string, permissions: readonly PluginPermission[]): void {
    const record = this.records.get(pluginId)
    if (record === undefined) return
    const next = revokePermissions(
      { required: record.manifest.permissions, optional: record.manifest.optionalPermissions, granted: record.granted },
      permissions
    )
    record.granted = [...next.granted]
    record.approvedRequired = record.approvedRequired.filter((p) => record.granted.includes(p))
    /*
      ★ 撤掉必选能力 = 这个插件不能再跑。留着跑意味着它的每一次调用都会被
      能力门拒掉,而作者和用户都看不出为什么 —— 不如直接停下来。
    */
    if (record.manifest.permissions.some((p) => !record.granted.includes(p))) {
      // 需求：撤权不是普通禁用，必须同步拒绝新调用；未保存模型保留，不能借 dirty 延迟撤权。
      record.enabled = false
      void this.disable(pluginId).catch(async (error: unknown) => {
        record.diagnostics.push({ path: 'documents', message: `permissions revoked; document state retained: ${String(error)}`, level: 'warn' })
        await this.disableAfterDocuments(pluginId)
      }).catch((error: unknown) => {
        this.deps.host.logger.warn(`[plugins] revoked plugin cleanup failed: ${String(error)}`)
      })
      this.persist()
      this.deps.emitChanged()
      return
    }
    this.persist()
    this.deps.emitChanged()
  }

  // ─────────────────────────── 激活 ───────────────────────────

  /**
   * 按激活事件唤醒。已经活着就只是「碰一下」(重置休眠计时)。
   *
   * 返回 `false` = 这个事件没有叫醒任何东西(没装、没启用、待批准、装载失败)。
   */
  async activate(event: string): Promise<boolean> {
    let woke = false
    for (const [id, record] of this.records) {
      if (!record.enabled || record.status === 'error' || record.status === 'pending-approval') continue
      if (!record.manifest.activationEvents.includes(event) && !record.manifest.activationEvents.includes('onStartup')) continue
      woke = (await this.wake(id)) || woke
    }
    return woke
  }

  async wake(pluginId: string, chain: ReadonlySet<string> = new Set()): Promise<boolean> {
    const record = this.records.get(pluginId)
    if (record === undefined || !record.enabled) return false
    if (record.status === 'error' || record.status === 'pending-approval' || record.status === 'disabled') return false
    /*
      ★ **零代码插件永远不 spawn。** `kind: 'webapp'` 的包没有 `main`,
      它的全部内容就是清单里那几条 `webApps`。走下去的话 `spawn` 会去加载一个
      不存在的入口,然后把这个插件标成 error —— 而它其实好好的。

      返回 `true`(不是 false):调用方问的是「它能用吗」,而它能用。
      返回 false 会让 `openWebApp` 以为这个插件坏了。
    */
    if (record.manifest.kind === 'webapp') {
      record.touchedAt = Date.now()
      return true
    }
    record.touchedAt = Date.now()
    // status 说 active,但宿主窗口可能已崩(`render-process-gone` 不会回头改 manager 的
    // 状态)。真的还活着才早返回;不在了就往下走重新 spawn —— spawn 会先 dispose 掉
    // 那个死窗口。这修的是「插件崩过一次之后,点它的命令永远报 plugin host is not running」。
    if (record.status === 'active' && this.deps.runtime.isRunning?.(pluginId) !== false) return true
    if (record.status === 'activating') return true

    /*
      ★ 依赖先醒(第 5 层):`ncw.plugins.connect(dep)` 要能立刻拿到一个已在跑的 dep。
      环用 `chain` 挡:A→B→A 时,回到 A 那一步 chain 已含 A,跳过、不再递归。
      best-effort —— 依赖醒不了(没装/被禁/自己也炸)不阻断本插件激活,由 connect
      在调用时返回失败,而不是让整条激活挂掉。
    */
    const nextChain = new Set(chain).add(pluginId)
    for (const depId of Object.keys(record.manifest.dependencies)) {
      if (nextChain.has(depId)) continue
      await this.wake(depId, nextChain).catch(() => undefined)
    }

    record.status = 'activating'
    this.deps.emitChanged()
    try {
      await this.deps.runtime.spawn({ id: pluginId, root: record.root, main: record.manifest.main })
      await this.deps.runtime.invoke(pluginId, { id: 0, kind: 'activate', payload: {} }, PLUGIN_TIMEOUT.ACTIVATE_MS)
      record.status = 'active'
      this.deps.emitChanged()
      return true
    } catch (error) {
      record.status = 'error'
      record.diagnostics.push({
        path: record.manifest.main,
        message: `activate() failed: ${(error as Error).message}`,
        level: 'error'
      })
      this.deps.runtime.dispose(pluginId)
      this.deps.emitChanged()
      return false
    }
  }

  private async deactivate(pluginId: string): Promise<void> {
    const record = this.records.get(pluginId)
    if (record === undefined) return
    try {
      await this.deps.runtime.invoke(pluginId, { id: 0, kind: 'deactivate', payload: {} }, PLUGIN_TIMEOUT.ACTIVATE_MS)
    } catch {
      // deactivate 失败不阻断销毁 —— 一个卡住的插件不该让禁用按钮失灵。
    }
    record.status = record.enabled ? 'idle' : 'disabled'
    /*
      ★ 订阅跟着宿主页面一起没 —— `dispose()` 之后那个 webContents 就不在了。
      不清的话,休眠过的插件会被当成「还订阅着」,每次主题变化都触发一次
      唤醒;而用户并没有在用它。
    */
    record.appearanceSubscriber = false
    this.deps.runtime.dispose(pluginId)
  }

  /** 空闲 5 分钟休眠。**这是这套架构唯一的内存回收路径。** */
  private sweepIdle(): void {
    const now = Date.now()
    for (const [id, record] of this.records) {
      if (record.status !== 'active') continue
      if (now - record.touchedAt < IDLE_SLEEP_MS) continue
      void this.deactivate(id).then(() => {
        const current = this.records.get(id)
        if (current !== undefined && current.status === 'idle') current.status = 'asleep'
        this.deps.emitChanged()
      }).catch(() => undefined)
    }
  }

  // ─────────────────────────── 命令 ───────────────────────────

  async runCommand(pluginId: string, commandId: string, args?: Record<string, unknown>): Promise<void> {
    const record = this.records.get(pluginId)
    if (record === undefined) throw new Error(`unknown plugin: ${pluginId}`)
    if (!record.manifest.contributes.commands.some((c) => c.command === commandId)) {
      throw new Error(`plugin ${pluginId} does not contribute ${commandId}`)
    }
    // ★ 命令本身就是激活事件 —— 点菜单是插件第一次被叫醒的最常见方式。
    if (!(await this.activateFor(`onCommand:${commandId}`, pluginId))) {
      /*
        ★ 把**最近一条诊断**带上。`wake` 失败时已经把真正的原因(activate()
        抛了什么、宿主页面为什么没握手)写进了 `record.diagnostics`,
        只抛一句笼统的「could not be activated」等于把那条信息锁在插件详情页里,
        而用户在控制台看到的是一句指向不了任何东西的话。
      */
      const why = record.diagnostics.at(-1)?.message
      throw new Error(
        `plugin ${pluginId} could not be activated${why === undefined ? '' : `: ${why}`}`
      )
    }
    await this.deps.runtime.invoke(
      pluginId,
      /*
        args 是宿主**调用点上下文**(目前只有 `+` 菜单附带的 workspaceId),
        不是插件的入参通道 —— 命令的语义由清单声明,这里只透传来源信息。
      */
      { id: 0, kind: 'command.run', payload: { commandId, ...(args === undefined ? {} : { args }) } },
      PLUGIN_TIMEOUT.COMMAND_MS
    )
  }

  /**
   * 打开一个自定义编辑器 Tab 之前的唤醒。
   *
   * 需求:插件视图的静态文件经 `ncw-plugin://` 协议服务,而协议层的 roots 表
   * **只在 spawn 时写入** —— 插件没醒过,iframe 的第一个请求就是 403
   * "forbidden"。`onCustomEditor:` 这个激活事件此前没有任何派发点(只有
   * `onStartup` 和 `onCommand:` 真的会触发),所以只声明它的插件(编辑器类
   * 插件的本分写法)永远打不开。
   *
   * 返回 `false` = 没醒成(wake 已把原因写进 diagnostics,渲染层据此留在
   * 降级态,不渲染注定 403 的 iframe)。
   */
  async activateCustomEditor(pluginId: string, viewType: string): Promise<boolean> {
    const record = this.records.get(pluginId)
    if (record === undefined) return false
    if (!record.manifest.contributes.customEditors.some((editor) => editor.viewType === viewType)) return false
    return this.activateFor(`onCustomEditor:${viewType}`, pluginId)
  }

  /**
   * 用户点了某个网页应用的入口(侧边栏 / 命令面板)。
   *
   * 需求:「插件 = 打开哔哩哔哩」这条路的落点。校验和转发都在这里,与插件自己
   * 调 `tabs.openWebApp` 走的是**同一段**代码 —— 两条路各写一份的话,
   * 「插件能开但用户点不开」这种差异只会在某一条路上被发现。
   *
   * 返回 `false` = 这个插件没有这个 webApp(或者它此刻不可用),由调用方
   * 翻译成一次失败;不抛,同 `activateCustomEditor` 的取向。
   */
  /**
   * 自定义编辑器视图报上来的脏标记(由宿主代发,见 `ipc/plugins.ts`)。
   *
   * ★ 和插件自己调 `customEditors.setDirty` 写的是**同一张表**:同一个文件
   * 可能一边由视图报脏、一边由插件代码报干净,两张表会让「该不该挽留」
   * 取决于先问哪一张。
   */
  setEditorDirty(pluginId: string, documentId: string, path: string, dirty: boolean): void {
    const record = this.records.get(pluginId)
    if (record === undefined || documentId === '') return
    if (dirty) record.dirtyDocuments.set(documentId, path)
    else record.dirtyDocuments.delete(documentId)
  }

  async openWebApp(pluginId: string, webAppId: string): Promise<boolean> {    const record = this.records.get(pluginId)
    if (record === undefined || !record.enabled) return false
    if (record.status === 'error' || record.status === 'pending-approval') return false
    const webApp = record.manifest.contributes.webApps.find((w) => w.id === webAppId)
    if (webApp === undefined) return false
    /*
      ★ 激活事件照发:一个 webapp 插件可能同时贡献 skills / themes,而那些要在
      第一次用到时才挂上去。对 `kind: 'webapp'` 来说 `wake` 是一次空操作
      (见那里的早返回),所以这一行不会为它起任何进程。
    */
    await this.activateFor(`onWebApp:${webAppId}`, pluginId).catch(() => false)
    this.deps.openTab(pluginId, {
      kind: 'webapp',
      webAppId: webApp.id,
      url: webApp.url,
      title: webApp.title,
      ...(webApp.icon === undefined ? {} : { icon: webApp.icon }),
      open: webApp.open ?? 'tab'
    })
    return true
  }

  /**
   * 主题变了 —— 通知订阅过的插件。
   *
   * ★ **只推给活着且订阅过的**。休眠的插件不唤醒:用户没在用它,为了一次
   * 颜色变化把它叫醒(再起一个隐藏窗口)不值当;它下次醒来自己
   * `appearance.get()` 拿到的就是新值。
   *
   * ★ **即发即忘,且不 await**。主题切换是一次界面动作,不该被任何一个
   * 插件的处理函数拖住 —— 同 `ipc/plugins.ts` 里 onStartup 唤醒那段的理由。
   */
  notifyAppearanceChanged(appearance: 'light' | 'dark'): void {
    for (const [pluginId, record] of this.records) {
      if (record.status !== 'active' || !record.appearanceSubscriber) continue
      void this.deps.runtime
        .invoke(pluginId, { id: 0, kind: 'event', payload: { event: 'appearance.changed', appearance } }, PLUGIN_TIMEOUT.COMMAND_MS)
        .catch(() => undefined)
    }
  }

  /**
   * 一条流式命令结束了 —— 撤登记 + 给插件推一条 exit。
   *
   * ★ 成功、失败、被中断、超时**四条路都要走到这里**。漏掉任何一条的症状是
   * 插件那边 `await handle.done` 永远不 resolve,而它可能正挂在一次工具调用里。
   */
  private finishExec(pluginId: string, execId: string, code: number, timedOut: boolean): void {
    const running = this.execStreams.get(execId)
    if (!this.execStreams.delete(execId)) return
    running?.detach()
    void this.deps.runtime
      .invoke(pluginId, { id: 0, kind: 'event', payload: { event: 'process.exit', execId, code, timedOut } }, PLUGIN_TIMEOUT.COMMAND_MS)
      .catch(() => undefined)
  }

  /**
   * 起一条流式命令,并把它挂到**它自己的**那张表上。
   *
   * ★ 这里是「流式命令生命期独立于启动 RPC」这条不变量的唯一落点,所以把它从
   * `handleHostMethod` 里抽出来:`execStreams` 里的 `controller` 只做两件事 ——
   * `process.execAbort` 和插件禁用 / shutdown。启动 RPC 的 30 秒期限**不进来**。
   *
   * ★ 但长命令仍然要有主人:
   * - `toolSignal`(`ctx.toolSignal`,产生这次调用的那次工具的 abort)→ 用户点
   *   停止时立刻杀,并且与 `controller` 一起进 spawn 的 signal;
   * - `timeoutMs` → 命令自己的上限(与 `process.exec` 同一口径)。
   *
   * ★ `finishExec` 只从 spawn 的 resolve / reject 进来,而 spawn 拿到的三条
   * abort 来源(controller、toolSignal、timeoutMs)都会让它 reject —— 所以
   * 「真的结束了」和「被取消了」都只推一条 `process.exit`,`detach` 保证那条
   * 工具 signal 上的 listener 一定被摘掉。
   */
  private startExecStream(
    execId: string,
    pluginId: string,
    prepared: { command: string; line: string; cwd: string; shell: string; timeoutMs: number },
    toolSignal: AbortSignal | undefined
  ): void {
    const controller = new AbortController()
    const toolOnAbort = (): void => { controller.abort() }
    const detachTool = (): void => { toolSignal?.removeEventListener('abort', toolOnAbort) }    /*
      工具已经中止了(用户点得比命令起得还快)→ 直接 abort,别 spawn。
      这不是「取消一条已经跑起来的命令」,那条命令根本没起 —— 它的 exit 走
      spawn 的 reject 分支。
    */
    if (toolSignal !== undefined) {
      if (toolSignal.aborted) controller.abort()
      else toolSignal.addEventListener('abort', toolOnAbort, { once: true })
    }
    const pump = new OutputPump((stream, text, truncated) => {
      void this.deps.runtime
        .invoke(pluginId, { id: 0, kind: 'event', payload: { event: 'process.output', execId, stream, chunk: text, truncated } }, PLUGIN_TIMEOUT.COMMAND_MS)
        .catch(() => undefined)
    })
    this.execStreams.set(execId, { pluginId, controller, detach: detachTool })
    void this.deps.host
      .spawn(prepared.line, {
        cwd: prepared.cwd,
        signal: controller.signal,
        timeoutMs: prepared.timeoutMs,
        shell: prepared.shell,
        onOutput: ({ stream, text }) => { pump.push(stream, text) }
      })
      .then(
        (result) => { pump.flush(); this.finishExec(pluginId, execId, result.code, false) },
        // 中断 / 超时走这里:插件仍然要收到一条 exit,否则它的 `done` 永远挂着。
        () => { pump.flush(); this.finishExec(pluginId, execId, TIMEOUT_EXIT_CODE, true) }
      )
  }

  /**
   * 工作区里有文件变了 —— 分发给订阅了的插件。
   *
   * ## ★ 这不是文件系统 watcher,而且**故意**不是
   *
   * 仓库里没有递归 watcher,理由见 `ipc/workspace-search.ts`(大仓库上开销大、
   * macOS 上给不出可靠的重命名事件)。为插件单开一个,成本和风险都远大于它的
   * 收益。所以这条通道的事实来源是**宿主自己知道的那些写入**:编辑器保存、
   * Agent 工具写文件、插件自己的 `workspace.writeFile` / `deleteFile`。
   *
   * **覆盖不到**:外部编辑器、`git checkout`、终端里的 `mv`。这一条写在协议、
   * d.ts 和文档里 —— 不写清楚的话,作者会把它当 watcher 用,然后在
   * 「为什么我在 VS Code 里改了没反应」上耗掉一天。
   */
  notifyWorkspaceChanged(changes: readonly { path: string; kind: 'created' | 'modified' | 'deleted' }[], workspaceId?: string): void {
    if (changes.length === 0) return
    for (const [pluginId, record] of this.records) {
      // 睡着的插件**不叫醒**:用户没在用它,一次文件保存不值得为它起一个进程
      // (同 `notifyAppearanceChanged` 的取向)。它醒来时自己重新读就是了。
      if (record.status !== 'active' || record.watchGlobs === undefined) continue
      if (workspaceId !== undefined && record.watchWorkspaceId !== workspaceId) continue
      const mine = changes.filter((change) => matchesPathScope(record.watchGlobs, change.path))
      if (mine.length === 0) continue
      void this.deps.runtime
        .invoke(pluginId, { id: 0, kind: 'event', payload: { event: 'workspace.changed', changes: mine } }, PLUGIN_TIMEOUT.COMMAND_MS)
        .catch(() => undefined)
    }
  }

  private async activateFor(event: string, pluginId: string): Promise<boolean> {    const record = this.records.get(pluginId)
    if (record === undefined) return false
    if (!record.manifest.activationEvents.includes(event) && !record.manifest.activationEvents.includes('onStartup')) {
      /*
        清单里没声明这个激活事件,但用户点了它贡献的菜单 —— 这是清单写漏了。
        **仍然激活**(用户的意图很明确),同时记一条诊断给作者。
      */
      record.diagnostics.push({
        path: 'activationEvents',
        message: `"${event}" is missing from activationEvents; the host activated the plugin anyway`,
        level: 'warn'
      })
    }
    return this.wake(pluginId)
  }

  // ─────────────────────────── RPC 四道门 ───────────────────────────

  /**
   * 插件 → 主进程的一次调用。**这是整个插件系统的收口点。**
   *
   * 四道门的顺序在 `shared/plugin/protocol.ts` 的文件头里写着,
   * 这里逐条对应。每一条都会落一行活动日志,包括被拒的那些 ——
   * 被拒的那些恰恰是用户最想看见的。
   */
  async handleRequest(pluginId: string, request: PluginRequest): Promise<PluginResponse> {
    const started = Date.now()
    const record = this.records.get(pluginId)
    const fail = (code: PluginErrorCode, message: string): PluginResponse => {
      recordActivity({
        ts: started,
        pluginId,
        method: String(request.method),
        summary: message,
        verdict: code === 'timeout' ? 'timeout' : code === 'permission_denied' || code === 'rejected' ? 'denied' : 'error',
        durationMs: Date.now() - started
      })
      return { id: request.id, ok: false, error: { code, message } }
    }

    if (record === undefined) return fail('internal_error', 'plugin is not installed')
    if (!record.enabled || record.status === 'pending-approval') {
      return fail('permission_denied', 'plugin is not enabled')
    }

    // ① 白名单 —— 先回答「有没有这个方法」,再回答「你能不能用」。
    if (!isPluginMethod(request.method)) return fail('unknown_method', `unknown method: ${String(request.method)}`)
    const method = request.method

    // ② 能力
    const needed = PLUGIN_METHOD_PERMISSION[method]
    if (needed !== null) {
      const declared = [...record.manifest.permissions, ...record.manifest.optionalPermissions].includes(needed)
      if (!declared) return fail('permission_denied', `the manifest does not declare "${needed}"`)
      const state: PluginPermissionState = {
        required: record.manifest.permissions,
        optional: record.manifest.optionalPermissions,
        granted: record.granted
      }
      if (!hasPermission(state, needed)) return fail('permission_denied', `"${needed}" has not been granted`)
    }

    record.touchedAt = Date.now()

    /*
      ★ **真实 deadline + 取消,而且必须真的在限时内返回。**
      旧的 `withTimeout(invokeCapability(...))` 只有一条 `Promise.race`:到点回一条
      timeout,**而那次工作还在跑**(网络读一半、子进程还活着、审批弹窗还开着)。

      这条 controller 接住两件事 —— 到点取消,和插件被禁用 / 卸载 / shutdown 时
      取消 —— 但**光有 abort 不够**:审批(`await deps.approve`)、文件 IO、宿主
      方法都不认 signal,它们会一直 await 下去。所以下面每一条派发都用 `abortable`
      把「这次调用自己的 signal」变成一次拒绝 —— 到点返回必须在**时限内**发生,
      而不是等那个不理会 signal 的 await 自己结束。

      ★ 清理(`untrackRequest` + 摘掉下游 listener)只有**一处**,在最外层的
      `finally`:出口有成功 / 宿主方法 / 能力 / 抛错四条,抄四份的话漏掉哪一份的
      症状都是「30 秒后 abort 一个早就没人等的 controller」和一个永远涨的
      `liveRequests`。
    */
    let toolSignal: AbortSignal | undefined
    try { toolSignal = this.toolSignalForRequest(pluginId, method, request.params) }
    catch (error) {
      if (error instanceof CapabilityError) return fail(error.code, error.message)
      return fail('internal_error', error instanceof Error ? error.message : String(error))
    }
    const controller = new AbortController()
    const deadlineTimer = setTimeout(() => {
      // reason 带标记:只有 deadline 到点算「超时」,插件被禁用 / shutdown 是「被取消」。
      controller.abort(TIMEOUT_ABORT_REASON)
    }, PLUGIN_TIMEOUT.REQUEST_MS)
    deadlineTimer.unref?.()
    this.trackRequest(pluginId, controller, deadlineTimer)

    /*
      需求：**工作区相关(或恰好一次工具在跑)的调用要继承产生它的那次工具调用的
      宿主 signal** —— 用户点停止时,已经跑出去的 spawn / fetch 必须立刻断,而不只
      是「阻止下一次调用」。修法是把工具 signal 转发进这条调用的 controller。

      ★ **不是** `controller = 工具的 signal`(那会一 abort 就全局作废、无法分辨
      来源):这里用一条**派生的** controller,工具的 abort 只 abort 它,宿主自己
      的 deadline / 禁用仍走 `controller`。两个上游都 abort 同一个派生 signal,
      `checkActive` 按 reason 分出「超时」还是「被取消」。

      ★ **只绑一个 live tool 时才继承。** 多个工具调用并发(同一插件)时插件没有
      指明 callId,`resolveCallWorkspace` 会拒;这里也不替它挑一个 —— 绑错工具
      的后果是 A 的 stop 掐断 B 的调用,正是需求里点名不能发生的事。
    */
    const derived = new AbortController()
    const toolOnAbort = (): void => { derived.abort(TOOL_CANCELLED_REASON) }
    if (toolSignal !== undefined) {
      // 工具已经停止 → 这次调用一开始就是中止的(checkActive 会立刻拒)。
      if (toolSignal.aborted) toolOnAbort()
      else toolSignal.addEventListener('abort', toolOnAbort, { once: true })
    }
    controller.signal.addEventListener('abort', toolOnAbort, { once: true })
    /*
      ★ **清理监听器。** 不摘的话,一条 30 秒就结束的调用会在那个工具的 signal 上
      （长命,挂在整轮 run 上)留一个永远不被调用的 listener,每次停止时逐个空跑。
    */
    const detachSignal = (): void => {
      controller.signal.removeEventListener('abort', toolOnAbort)
      toolSignal?.removeEventListener('abort', toolOnAbort)
    }

    /*
      能力上下文**在分派之前**就造好。

      ★ 它原本造在 `handleHostMethod` 之后,而 `process.execStream` 这类
      「既要走参数门与审批、又要按 execId 往回推事件」的方法两边都需要它:
      门在 `rpc.ts`(纯函数、可直测),事件在这里(只有 manager 拿得到 runtime)。
      造两份 ctx 的话,两条路的 `allowedCommands` / 工作区根迟早会不一致。
      构造本身很便宜(全是闭包,scm 适配器也是用到才造)。

      ★★ **工作区在这一次调用开始时就冻结。** 带 `callId` 的工作区类方法按产生
      它的那次工具调用定位(见 `resolveCallWorkspace`);没有 callId 时按发起时的
      当前工作区 —— 旧语义。两条路都算出一个「这次调用的工作区」,再把它的作用域
      代号冻进 `checkActive`:调用中途那个工作区变成另一个根了,后续的复检就拒,
      而不是把文件写进另一个工作区。
    */
    let workspace: { id: string; rootPath: string }
    try {
      workspace = this.resolveCallWorkspace(pluginId, method, request.params)
    } catch (error) {
      // 作用域解析失败 = 这次调用不该发生。把上游监听摘掉,这条调用不留痕迹。
      detachSignal()
      this.untrackRequest(pluginId, controller, deadlineTimer)
      if (error instanceof CapabilityError) return fail(error.code, error.message)
      return fail('internal_error', (error as Error).message)
    }
    const generation = this.markWorkspaceGeneration(workspace)
    const workspaceId = workspace.id
    const checkActive = (): void => {
      /*
        ★ 两条上游分开看,因为两者的「谁说的」不同:
        - `controller.signal` 带 **reason**(deadline 到点是 `TIMEOUT_ABORT_REASON`);
        - `derived.signal` 是「这条调用自己」的 signal,工具中止只 abort 它,
          而它的 reason 一定是 `TOOL_CANCELLED_REASON` —— 拿它判超时永远为假。
        只传 controller 会漏掉「工具被停止」这一路;只传 derived 会把超时误判成取消。
      */
      this.assertCallAlive(pluginId, record, controller.signal, derived.signal.aborted)
      // 需求：工作区切走之后,仍在跑的那次调用不能再写 —— 它拿到的是旧作用域。
      if (this.workspaceGenerationOf(workspaceId) !== generation) {
        throw new CapabilityError('rejected', '[stale_scope] the call workspace changed while the call was running')
      }
    }
    /*
      ★★ **副作用从哪一刻起不可撤销,由真正动手的那一行决定,不是由方法名决定。**
      `markSideEffectStarted` 由 `rpc.ts` 在写 / 删 / spawn / scm / 文档引擎之前
      调用一次;在那之前被取消(审批还挂着、路径门被拒)走的都是普通取消 / 超时。
    */
    const sideEffect: SideEffectTracker = { started: false }
    const ctx: CapabilityContext = {
      pluginId,
      manifest: record.manifest,
      host: this.deps.host,
      workspaceRoot: workspace.rootPath,
      workspaceId: workspace.id,
      signal: derived.signal,
      ...(toolSignal === undefined ? {} : { toolSignal }),
      checkActive,
      markSideEffectStarted: () => { sideEffect.started = true },
      /*
        ★ 这里原本写死成 `[]`,而 `narrowCommand` 见到空白名单一律拒 ——
        于是**任何**插件的 `process.exec` 都在参数门被静默拒死,永远走不到
        审批那一步。不是接线漏了:`allowedCommands` 这个清单字段当时压根
        没有实现,`rpc.ts` 那句「清单里可选的命令白名单」指的是一个不存在
        的东西。字段补上之后这里才有得可传。
      */
      allowedCommands: record.manifest.allowedCommands,
      approve: (summary) => this.deps.approve(pluginId, summary),
      trash: (absolutePath) => this.deps.trash(absolutePath),
      openExternal: (url) => this.deps.openExternal(url),
      clipboard: this.deps.clipboard,
      /*
        ★ 适配器**用到时才造**,并且绑定这次调用**冻结的那个工作区** —— 插件传不进
        workspaceId,它说了不算。

        ★ 工作区相关方法(scm.* 全在 `WORKSPACE_SCOPED_METHODS` 里)的冻结工作区
        已经由 `resolveCallWorkspace` 按可信 `callId` 定位过:带 callId 就是产生它
        的那次工具调用的工作区,没有 callId 而恰好一次工具在跑也按那一次推断,
        多次在跑直接拒。所以这里**不需要**再解析一次 callId —— 那只会是同一份值的
        第二份来源,而两份来源迟早分叉。
      */
      scm: () => this.deps.scmFor(workspace.id),
      kv: this.kvFor(pluginId)
    }

    /*
      ★ **两个出口(宿主方法 / 能力)统一分类。** 它们都有副作用点、都要在
      deadline 到点时结算、都要按「是否已越过副作用点」区分 result_unknown。
      只有一处 `classifyAndFail` 才不会出现「同样是被取消,一条报 timeout、
      另一条报 rejected」那种按分派路径变化的现象。
    */
    const classifyAndFail = (error: unknown): PluginResponse => {
      const message = error instanceof Error ? error.message : String(error)
      const timedOut = isTimeoutAbort(controller.signal) || message === 'plugin_timeout'
      const cancelled = controller.signal.aborted || derived.signal.aborted || timedOut
      /*
        ★ **不能再报告「可安全重试」。** 越过副作用点之后被取消,重试会把副作用做
        第二遍 —— 而且外部写 / 文档引擎那类**已经开始的工作取消不了**(我们只是
        不等了),重试就是双份。给一条明确的「结果未知」,让重试由人决定。
        也**不声称可回滚**:这里没有回滚,说清楚「不知道做没做」比编一句能撤销诚实。

        ★ 副作用点**之前**的取消(比如审批还挂着)走普通取消 / 超时 —— 那种失败
        重试是安全的,报 result_unknown 会平白吓住插件作者。
      */
      const unknown = sideEffect.started
      if (unknown && cancelled) {
        return fail(
          timedOut ? 'timeout' : 'rejected',
          '[result_unknown] the call was cancelled after it may have taken effect; verify the workspace before retrying'
        )
      }
      // 参数门 / 行为门的拒绝:原样带出去(它们有各自明确的错误码)。
      if (timedOut) return fail('timeout', 'the call timed out')
      if (cancelled || message === 'the call was cancelled') return fail('rejected', 'the call was cancelled')
      if (error instanceof CapabilityError) return fail(error.code, error.message)
      return fail('internal_error', message)
    }

    /*
      ★ **注册类方法在能力门之后、参数门之前单独处理。**

      它们和 `workspace.readFile` 那一类不同:后者是「替插件做一件事」,
      而这些是「插件在宿主这边登记一个东西」——它们改的是宿主自己的表
      (工具注册表、命令表、授权状态),不碰文件系统也不走权限链。
      混进 `invokeCapability` 会让那个函数同时背两种职责,而它已经是
      整个系统里最需要一眼看懂的一段了。

      ★ `abortable` 让这条出口即使卡在一个不理会 signal 的 await 上(比如
      `permissions.request` 的授权弹窗),到点也会真的返回;兜底时按「副作用点之前」
      分类 —— 登记类方法还没动手,报普通取消而不是 result_unknown。
    */
    let hostHandled: { data: unknown; summary: string } | undefined
    let capabilityOutcome: CapabilityOutcome | undefined
    let failure: PluginResponse | undefined
    try {
      hostHandled = await abortable(() => this.handleHostMethod(pluginId, record, method, request.params, ctx), derived.signal)
      if (hostHandled === undefined) {
        // ③ 参数门 + ④ 行为门都在 handler 里 —— 它们需要文件系统与权限链。
        capabilityOutcome = await abortable(() => invokeCapability(method as PluginMethod, request.params as never, ctx), derived.signal)
      }
    } catch (error) {
      failure = classifyAndFail(error)
    } finally {
      /*
        ★ **这里,也只有这里,收掉这条调用的全部痕迹:**
        - deadline 定时器与 `liveRequests` 登记(漏了 = 30 秒后一条对着空
          controller 的 abort + 一个永远涨的表);
        - 挂在工具 signal 与派生 signal 上的两个 listener(漏了 = 一条 30 秒的
          调用在整轮 run 上都留一个再也不用的 listener,用户每次点停止都空跑一遍)。
        两条出口(宿主方法 / 能力)在同一个 `finally` 里收 —— 抄两份的话迟早只改
        一份。`process.execStream` 起的命令**不在**这里收:它在自己的表里,
        生命期以 `finishExec` 结束。
      */
      detachSignal()
      this.untrackRequest(pluginId, controller, deadlineTimer)
    }
    if (failure !== undefined) return failure

    if (hostHandled !== undefined) {
      recordActivity({
        ts: started,
        pluginId,
        method,
        summary: hostHandled.summary,
        verdict: 'ok',
        durationMs: Date.now() - started
      })
      return { id: request.id, ok: true, data: hostHandled.data }
    }

    const outcome = capabilityOutcome as CapabilityOutcome
    recordActivity({
      ts: started,
      pluginId,
      method,
      summary: outcome.summary,
      verdict: 'ok',
      durationMs: Date.now() - started
    })
    /*
      ★ 插件自己的写入也要进变更流,否则订阅者之间是**半聋**的:
      A 插件改了一个文件,监听同一份文件的 B 插件收不到 —— 而同样的改动由
      Agent 做出来时它收得到。同一件事有两种结果,是最难查的那类问题。

      通知放在**成功之后**:写失败了没有任何东西变过。

      ★ **通知带回这次调用冻结的那个工作区。** 不带的后果是一次后台调用改了
      A 的文件,而订阅 B 的插件收到了一条 `workspace.changed` —— 它按自己
      订阅时的根去解释这条相对路径,于是又指到了另一个文件。
    */
    if (method === 'workspace.writeFile' || method === 'workspace.deleteFile') {
      const path = (request.params as { path?: unknown } | null)?.path
      if (typeof path === 'string') {
        this.notifyWorkspaceChanged([
          { path, kind: method === 'workspace.deleteFile' ? 'deleted' : 'modified' }
        ], ctx.workspaceId)
      }
    }
    return { id: request.id, ok: true, data: outcome.data }
  }

  /**
   * 这次 RPC 该继承哪一条工具 signal。
   *
   * 规则与 `resolveCallWorkspace` 的作用域推导同源:**只有能唯一确定那次工具调用
   * 时才返回**,否则返回 `undefined`(不继承)。
   *
   * - 非工作区类方法:不解析 —— `callId` 在那里只是普通数据(`diagnostics.log`
   *   的同名字段绝不能把它打挂,也不能被当成 scope)。
   * - 带 `callId`:本插件正在跑的那次调用(不存在 / 别人的 / 已 abort → 不继承,
   *   调用本身也会在作用域那一关被拒)。
   * - 不带 `callId`:本插件**恰好一次**工具调用在跑 → 用那一次;多次 → 不继承
   *   (不替它赌一个,见 `resolveCallWorkspace` 的规则 4)。
   * - 没有工具调用在跑:不继承。UI 代码(菜单 / 视图)的调用属于用户自己的动作,
   *   它们本来就没有工具 signal 可言。
   */
  private toolSignalForRequest(pluginId: string, method: PluginMethod, rawParams: unknown): AbortSignal | undefined {
    if (!resolvesCallId(method, rawParams)) return undefined
    const callId = rawParams !== null && typeof rawParams === 'object' && !Array.isArray(rawParams)
      ? (rawParams as Record<string, unknown>).callId
      : undefined
    if (callId !== undefined) {
      if (typeof callId !== 'string' || callId === '') throw new CapabilityError('invalid_argument', 'callId is invalid')
      const live = this.liveToolEmits.get(callId)
      if (live === undefined || live.pluginId !== pluginId || live.signal.aborted) {
        throw new CapabilityError('rejected', '[call_scope] the tool call is not active or not owned by this plugin')
      }
      return live.signal
    }
    const mine = [...this.liveToolEmits.values()].filter((live) => live.pluginId === pluginId)
    if (mine.length > 1) throw new CapabilityError('rejected', '[call_scope] multiple tool calls are running; pass the callId')
    return mine[0]?.signal
  }

  // ─────────────────────── 调用的存活与作用域 ───────────────────────

  /** 登记一次在途 RPC(连同它的 deadline 定时器);与 `untrackRequest` 成对。 */
  private trackRequest(pluginId: string, controller: AbortController, timer: NodeJS.Timeout): void {
    let set = this.liveRequests.get(pluginId)
    if (set === undefined) { set = new Set(); this.liveRequests.set(pluginId, set) }
    set.add({ controller, timer })
  }

  private untrackRequest(pluginId: string, controller: AbortController, timer: NodeJS.Timeout): void {
    // ★ 先清定时器再决定表怎么动:这条调用一旦结束,它那条 deadline 就不再有任何意义。
    clearTimeout(timer)
    const set = this.liveRequests.get(pluginId)
    if (set === undefined) return
    for (const entry of set) {
      if (entry.controller === controller) set.delete(entry)
    }
    if (set.size === 0) this.liveRequests.delete(pluginId)
  }

  /**
   * 取消这个插件此刻所有在途 RPC。禁用 / 卸载各调一次。
   *
   * ★ 只摘**它自己那一条**(按 pluginId)。顺手清空整张表的话,一个插件被禁用
   * 会把别的插件正在跑的调用一起掐掉 —— 而它们之间看起来毫无关系。
   *
   * ★ 顺手把每条调用的 deadline 定时器一起收掉。留着的话,它们在 30 秒后各自
   * 对着一个**早就没人等的** controller 再 abort 一次 —— 无害,但那是 30 秒的假活。
   */
  private abortRequestsOf(pluginId: string): void {
    const set = this.liveRequests.get(pluginId)
    if (set === undefined) return
    for (const entry of set) {
      clearTimeout(entry.timer)
      entry.controller.abort()
    }
    this.liveRequests.delete(pluginId)
  }

  /** shutdown:所有插件的在途调用 + 所有流式命令一起取消。 */
  private abortAllRequests(): void {
    for (const set of this.liveRequests.values()) {
      for (const entry of set) {
        clearTimeout(entry.timer)
        entry.controller.abort()
      }
    }
    this.liveRequests.clear()
    for (const running of this.execStreams.values()) running.controller.abort()
    for (const [execId, running] of [...this.execStreams]) this.finishExec(running.pluginId, execId, TIMEOUT_EXIT_CODE, true)
    this.execStreams.clear()
    this.liveToolEmits.clear()
  }

  /**
   * 取消这个插件正在跑的**流式命令**。
   *
   * ★ 一次性 RPC 由 `abortRequestsOf` 收,流式命令在**另一张表**里
   * (`execStreams`)—— 它的生命期不以某一条 RPC 结束,而以子进程退出结束。
   * 不单独收的话,一个被禁用的插件所起的构建会一直跑到它自己结束:它既没有主人
   * 再读输出,也没有人能 `execAbort`(那条 RPC 会被能力门拒掉,插件已经禁用了)。
   *
   * ★ 收尾**复用** `finishExec`:成功、失败、被中断、超时四条路都要走到它,
   * 漏掉任何一条的症状都是插件那边 `await handle.done` 永远不 resolve。
   */
  private abortExecStreamsOf(pluginId: string): void {
    for (const [execId, running] of [...this.execStreams]) {
      if (running.pluginId !== pluginId) continue
      running.controller.abort()
      this.finishExec(pluginId, execId, TIMEOUT_EXIT_CODE, true)
    }
  }

  /** 工具调用结束(或被中止)时,把它名下的登记一起撤掉。 */
  private abortToolCallsOf(pluginId: string): void {
    for (const [callId, live] of this.liveToolEmits) {
      if (live.pluginId !== pluginId) continue
      this.liveToolEmits.delete(callId)
    }
  }

  /**
   * 「这次调用还算数吗」的判据本体。
   *
   * ★ 单独一个函数,是因为它有三类调用点:调用开始前的冻结、审批之后的复检、
   * 以及网络重定向的每一跳。抄三份的话,「哪几种情况下该停」迟早只在其中一条
   * 路上成立 —— 而那条路不会有测试为它失败。
   */
  private assertCallAlive(pluginId: string, record: PluginRecord, signal: AbortSignal, toolCancelled = false): void {
    if (signal.aborted || toolCancelled) {
      /*
        ★ **两种 abort 要分开报。** 到点的 reason 是 `TIMEOUT_ABORT_REASON`;插件被
        禁用 / 卸载 / shutdown 走的是普通 abort。混成一句「超时」的话,同一个原因
        (用户关了插件)会因为「abort 在哪一步暴露出来」而给出两种不同的错误码:
        停在审批里的那条报 timeout,已经 spawn 出去的那条报 rejected。插件作者
        按其中一种写处理逻辑,另一种就会落空。

        ★ `toolCancelled`(产生这次调用的工具被用户停止)同样是**取消**,不是超时;
        但它来自另一条 signal,所以由调用方显式传进来。
      */
      throw isTimeoutAbort(signal)
        ? new CapabilityError('rejected', 'plugin_timeout')
        : new CapabilityError('rejected', 'the call was cancelled')
    }
    if (this.documentsClosing) throw new CapabilityError('rejected', 'document sessions are closing')
    if (this.records.get(pluginId) !== record) throw new CapabilityError('rejected', 'the plugin is no longer installed')
    if (!record.enabled || record.status === 'pending-approval') throw new CapabilityError('rejected', 'plugin is not enabled')
    record.touchedAt = Date.now()
  }

  /**
   * 这一次调用该按哪个工作区做 —— **受信作用域**的派生点。
   *
   * ## 规则
   *
   * 1. **不在工作区类方法里**:不解析。`callId` 在别的方法上是普通数据(工具
   *    注册的表里没有它),去读它只会让一次 `storage.set` 因为一个同名字段而失败。
   * 2. 带 `callId`:必须是**本插件正在跑**的一次工具调用,且它的工作区此刻在
   *    本地可用。不成立就拒 —— **绝不回退到当前聚焦的工作区**(那正是「用户在
   *    A 干活、文件写进 B」那条路)。
   * 3. 不带 `callId`,而这个插件恰有**一次**工具调用在跑:按那一次推断。这是
   *    安全的:插件无从选择,而它正在跑的那次调用只有一个工作区可言。
   * 4. 不带 `callId`,而**多次**在跑:拒绝。挑一个等于替插件赌一个工作区。
   * 5. 没有任何工具调用在跑:按发起时的当前工作区 —— 菜单 / 命令面板 / 视图里
   *    的 UI 代码走这一条,与旧行为一致。
   */
  private resolveCallWorkspace(pluginId: string, method: PluginMethod, rawParams: unknown): { id: string; rootPath: string } {
    if (!WORKSPACE_SCOPED_METHODS.has(method) || isGlobalStorageRequest(method, rawParams)) return this.deps.currentWorkspace()
    return this.resolveScopedWorkspace(pluginId, rawParams)
  }

  private resolveScopedWorkspace(pluginId: string, rawParams: unknown): { id: string; rootPath: string } {
    const callId = rawParams !== null && typeof rawParams === 'object' && !Array.isArray(rawParams)
      ? (rawParams as Record<string, unknown>).callId
      : undefined
    if (callId !== undefined) {
      if (typeof callId !== 'string' || callId === '') throw new CapabilityError('invalid_argument', 'callId is invalid')
      const live = this.liveToolEmits.get(callId)
      if (live === undefined || live.pluginId !== pluginId || live.signal.aborted) {
        throw new CapabilityError('rejected', '[call_scope] the tool call is not active or not owned by this plugin')
      }
      return this.workspaceOfToolCall(live.workspaceId)
    }
    const mine = [...this.liveToolEmits.values()].filter((live) => live.pluginId === pluginId)
    const only = mine[0]
    // 恰好一次在跑,而插件无从选择 —— 按那一次推断是安全的。
    // 没有 workspaceId 的调用(整体运行没有本地工作区)= 明确拒绝,不回退焦点。
    if (mine.length === 1 && only !== undefined) {
      if (only.signal.aborted) throw new CapabilityError('rejected', '[call_scope] the tool call is not active')
      return this.workspaceOfToolCall(only.workspaceId)
    }
    if (mine.length > 1) {
      throw new CapabilityError('rejected', '[call_scope] multiple tool calls are running; pass the callId to choose a workspace')
    }
    return this.deps.currentWorkspace()
  }

  /**
   * 工具调用冻结下来的工作区 → 现在的本地工作区。
   *
   * ★ 判据用的是**工具调用开始时内核给的 id**,不是「重新读一次焦点」——
   * 用户此刻在看哪个工作区和这次调用无关。解析 id 只用来回答「它还在、还是
   * 本地的吗」,不在了就拒,绝不回退。
   */
  private workspaceOfToolCall(workspaceId: string | undefined): { id: string; rootPath: string } {
    if (workspaceId === undefined || workspaceId === '') {
      throw new CapabilityError('invalid_argument', '[unsupported_environment] the tool call has no local workspace')
    }
    const resolved = this.deps.resolveWorkspace?.(workspaceId) ?? null
    if (resolved === null) {
      throw new CapabilityError('invalid_argument', '[unsupported_environment] the workspace is not available locally')
    }
    return { id: resolved.id, rootPath: resolved.rootPath }
  }

  /**
   * 从受信工具上下文派生 documents.* 的工作区作用域。
   *
   * 需求：后台 Agent 的文档请求必须跟随产生 callId 的那次工具调用，而不是跟随用户
   * 此刻聚焦的工作区；callId 结束或属于别的插件时绝不能回退到前台。
   * 不满足会怎样：用户在 A 工作区运行任务、切到 B 后，插件会静默把修改写进 B，
   * 或者恶意插件借一个猜中的 callId 读到另一轮任务的文件。
   */
  private resolveDocumentScope(pluginId: string, rawParams: unknown): DocumentCallScope {
    const workspace = this.resolveScopedWorkspace(pluginId, rawParams)
    return { workspaceId: workspace.id, workspaceRoot: workspace.rootPath }
  }

  /**
   * 「在宿主这边登记一个东西」类的方法。
   *
   * 返回 `undefined` = 这个方法不归我管,交给 `invokeCapability`。
   * 用 `undefined` 而不是布尔,是为了让「处理了但没有返回值」和「没处理」
   * 在类型上分得开 —— 否则 `tools.register` 这种返回空对象的会被当成没处理。
   */
  private async handleHostMethod(
    pluginId: string,
    record: PluginRecord,
    method: PluginMethod,
    rawParams: unknown,
    ctx: CapabilityContext
  ): Promise<{ data: unknown; summary: string } | undefined> {
    if (method.startsWith('documents.')) {
      if (this.documentsClosing) throw new CapabilityError('rejected', 'document sessions are closing')
      const documents = this.deps.documents
      if (documents === undefined) throw new CapabilityError('internal_error', 'document bridge is not configured')
      /*
        ★ `documents.*` 是**有限时**的一次调用,不是可以无限挂着的写。

        路径:`handleHostMethod` 这一层只负责过桥与把结果翻成 `CapabilityError`;
        真正在**动手之前**检查「这次调用还算不算数」的地方在 `document-rpc.ts`
        (串行队列轮到它时,以及导出/保存那两处副作用点)。这里做两件事:

        1. 加 `markSideEffectStarted` 钩子 —— 队列里可能已经排了几百毫秒,过期
           之后才轮到的动作不该再执行(见 `document-rpc.ts` 的 EARLY 检查)。
        2. 把 `ctx.signal` 传进去 —— 队列/引擎卡住时,派生 signal 一 abort,
           `abortable` 就让这次 RPC 在 deadline 内返回,而不是永远 await。
      */
      const scope = this.resolveDocumentScope(pluginId, rawParams)
      const documentScope = { ...scope, signal: ctx.signal, markSideEffectStarted: ctx.markSideEffectStarted }
      try {
        const result = await documents.handle(pluginId, record.manifest, method, rawParams, documentScope)
        // 需求：只有成功写盘才通知，并保留请求最初的后台工作区而不是重新读焦点。
        if (result.changed !== undefined) this.notifyWorkspaceChanged([result.changed], scope.workspaceId)
        return result
      } catch (error) {
        if (error instanceof PluginCapabilityError) {
          const code = error.code === 'invalid_argument' || error.code === 'rejected' || error.code === 'internal_error'
            ? error.code
            : 'internal_error'
          throw new CapabilityError(code, error.message)
        }
        throw error
      }
    }
    switch (method) {
      case 'permissions.contains': {
        const p = rawParams as { permissions: PluginPermission[] }
        const state: PluginPermissionState = {
          required: record.manifest.permissions,
          optional: record.manifest.optionalPermissions,
          granted: record.granted
        }
        return { data: { granted: hasPermission(state, p.permissions) }, summary: `contains ${p.permissions.join(',')}` }
      }

      case 'permissions.request': {
        const p = rawParams as { permissions: PluginPermission[]; reasonKey: string }
        const state: PluginPermissionState = {
          required: record.manifest.permissions,
          optional: record.manifest.optionalPermissions,
          granted: record.granted
        }
        /*
          ★ **铁律**:要的东西必须在 `permissions ∪ optionalPermissions` 之内,
          之外的**直接拒绝、不弹窗**。弹了就意味着用户有机会点「允许」,
          而那一刻他看到的授权界面和市场里那份清单已经对不上了。
        */
        if (!canRequest(state, p.permissions)) {
          return { data: { granted: false }, summary: `request denied (outside the manifest): ${p.permissions.join(',')}` }
        }
        if (hasPermission(state, p.permissions)) return { data: { granted: true }, summary: 'already granted' }
        const approved = await this.deps.requestPermissions(pluginId, p.permissions, p.reasonKey)
        if (approved) this.grant(pluginId, p.permissions)
        return { data: { granted: approved }, summary: `request ${p.permissions.join(',')}` }
      }

      case 'permissions.remove': {
        const p = rawParams as { permissions: PluginPermission[] }
        this.revoke(pluginId, p.permissions)
        return { data: {}, summary: `remove ${p.permissions.join(',')}` }
      }

      case 'appearance.get':
        return { data: { appearance: this.deps.currentAppearance() }, summary: 'appearance' }

      case 'appearance.subscribe':
        record.appearanceSubscriber = true
        return { data: {}, summary: 'subscribe appearance' }

      case 'commands.register': {
        const p = rawParams as { commandId: string }
        /*
          ★ 只认**清单里声明过**的命令 id。不认的话,一个插件可以在运行期
          注册任意命令 id —— 而菜单、快捷键、命令面板都按 id 分发,
          那等于让它能劫持别人的命令。
        */
        if (!record.manifest.contributes.commands.some((c) => c.command === p.commandId)) {
          return { data: {}, summary: `ignored undeclared command ${p.commandId}` }
        }
        record.commands.add(p.commandId)
        return { data: {}, summary: `register ${p.commandId}` }
      }

      case 'commands.unregister': {
        const p = rawParams as { commandId: string }
        record.commands.delete(p.commandId)
        return { data: {}, summary: `unregister ${p.commandId}` }
      }

      case 'tools.register': {
        const p = rawParams as PluginToolDeclaration
        if (!isValidPluginToolName(p.name)) {
          return { data: {}, summary: `rejected tool name ${p.name}` }
        }
        // 同命令:只认清单里声明过的工具名,否则市场审核看到的能力上界就不作数了。
        if (!record.manifest.contributes.tools.some((t) => t.name === p.name)) {
          return { data: {}, summary: `ignored undeclared tool ${p.name}` }
        }
        record.tools.set(p.name, p)
        this.deps.onToolsChanged()
        return { data: {}, summary: `register tool ${p.name}` }
      }

      case 'tools.unregister': {
        const p = rawParams as { name: string }
        record.tools.delete(p.name)
        this.deps.onToolsChanged()
        return { data: {}, summary: `unregister tool ${p.name}` }
      }

      case 'agent.registerInterceptor':
        record.interceptor = true
        return { data: {}, summary: 'register interceptor' }

      case 'agent.registerContextProvider':
        record.contextProvider = true
        return { data: {}, summary: 'register context provider' }

      case 'window.setStatusBarItem': {
        const p = rawParams as { id: string; textKey: string | null; tooltipKey?: string; command?: string }
        if (typeof p.id !== 'string' || p.id === '' || p.id.length > 64) {
          return { data: {}, summary: 'rejected status bar id' }
        }
        if (p.textKey === null) {
          record.statusBar.delete(p.id)
          this.deps.emitChanged()
          return { data: {}, summary: `clear status bar ${p.id}` }
        }
        /*
          ★ 三条收窄,每条都对着一种「状态栏被当成广告位」的用法:

          1. **格数有上限**。一个插件挂二十格,状态栏就没别人的位置了。
          2. **`textKey` 必须是 key**,不是文案 —— 见 `PluginStatusBarItem`。
             这里只校验形状(不含空格、不太长);它查不到时会显示成 key 本身,
             那正是我们想要的「看得见的缺失」。
          3. **`command` 必须是它自己贡献过的**。不校验的话,点一下状态栏
             就能触发别的插件的命令。
        */
        if (record.statusBar.size >= 3 && !record.statusBar.has(p.id)) {
          return { data: {}, summary: 'status bar item limit reached' }
        }
        if (/\s/.test(p.textKey) || p.textKey.length > 128) {
          return { data: {}, summary: 'rejected status bar textKey (must be an l10n key)' }
        }
        const command = p.command !== undefined && record.manifest.contributes.commands.some((c) => c.command === p.command)
          ? p.command
          : undefined
        record.statusBar.set(p.id, {
          id: p.id,
          pluginId,
          textKey: p.textKey,
          ...(p.tooltipKey === undefined ? {} : { tooltipKey: p.tooltipKey }),
          ...(command === undefined ? {} : { command })
        })
        this.deps.emitChanged()
        return { data: {}, summary: `status bar ${p.id}` }
      }

      case 'customEditors.register': {
        const p = rawParams as { viewType: string }
        // 同命令与工具:只认清单里声明过的 viewType。
        if (!record.manifest.contributes.customEditors.some((e) => e.viewType === p.viewType)) {
          return { data: {}, summary: `ignored undeclared viewType ${p.viewType}` }
        }
        return { data: {}, summary: `register editor ${p.viewType}` }
      }

      case 'customEditors.setDirty': {
        const p = rawParams as { documentId: string; path: string; dirty: boolean }
        if (typeof p.documentId !== 'string' || p.documentId === '') return { data: {}, summary: 'rejected documentId' }
        if (p.dirty) record.dirtyDocuments.set(p.documentId, typeof p.path === 'string' ? p.path : '')
        else record.dirtyDocuments.delete(p.documentId)
        return { data: {}, summary: `${p.dirty ? 'dirty' : 'clean'} ${p.path}` }
      }

      case 'tabs.openCustomEditor': {
        const p = rawParams as { viewType: string; path: string }
        // 同 `customEditors.register`:只认清单里声明过的 viewType。不认的话,
        // 一个插件可以请求打开**别人**的编辑器。
        if (!record.manifest.contributes.customEditors.some((e) => e.viewType === p.viewType)) {
          return { data: {}, summary: `ignored undeclared viewType ${String(p.viewType)}` }
        }
        /*
          ★ 路径在这里就收窄,不留到渲染层。渲染层拿到的是一条广播,
          它没有「这个插件能碰哪个根」的上下文 —— 把判断留给它等于把这道门
          交给一个看不见能力状态的地方。
        */
        const narrowed = narrowWorkspacePath(this.deps.currentWorkspace().rootPath, p.path)
        if (!narrowed.ok) return { data: {}, summary: `rejected path: ${narrowed.reason}` }
        this.deps.openCustomEditor(pluginId, p.viewType, p.path)
        return { data: {}, summary: `open ${p.viewType} ${p.path}` }
      }

      /*
        ───────── 打开网页 / 视图 ─────────

        需求:插件最常见的一种形态就是「把一个网站或一块自有 UI 带进来」——
        B 站、文档站、内部看板。此前插件**一条通往浏览器的路都没有**:应用内
        浏览器(InnerTab 的 browser)对它不可见,连交给系统浏览器的
        `env.openExternal` 都没有 handler。

        三条方法的门各不相同,所以分三个 case,而不是一个带 kind 的大 case:
        webapp 查清单、browser 查 hostPermissions、view 查视图声明。
      */
      case 'tabs.openWebApp': {
        const p = rawParams as { webAppId: string }
        const webApp = record.manifest.contributes.webApps.find((w) => w.id === p.webAppId)
        // 同 `tabs.openCustomEditor`:只认自己清单里声明过的条目,不然一个插件
        // 可以请求打开别人的东西。
        if (webApp === undefined) return { data: { opened: false }, summary: `ignored undeclared webApp ${String(p.webAppId)}` }
        this.deps.openTab(pluginId, {
          kind: 'webapp',
          webAppId: webApp.id,
          url: webApp.url,
          title: webApp.title,
          ...(webApp.icon === undefined ? {} : { icon: webApp.icon }),
          open: webApp.open ?? 'tab'
        })
        return { data: { opened: true }, summary: `open webApp ${webApp.id}` }
      }

      case 'tabs.openBrowser': {
        const p = rawParams as { url: string; open?: 'tab' | 'feature' | 'right' }
        /*
          ★ 能力门(`tabs.browser`)已经过了,这里是**参数门**:地址必须命中
          `hostPermissions`。少了这一道,一个声明「我只访问 bilibili.com」的插件
          可以在应用内打开任何网站 —— 而用户在安装界面上看到的域名只有那一个。
        */
        if (typeof p.url !== 'string' || !matchesHostPermission(record.manifest.hostPermissions, p.url)) {
          return { data: { opened: false }, summary: 'rejected url (not in hostPermissions)' }
        }
        this.deps.openTab(pluginId, { kind: 'browser', url: p.url, open: p.open ?? 'tab' })
        return { data: { opened: true }, summary: `open browser ${hostOf(p.url)}` }
      }

      /*
        ───────── 打开终端 ─────────

        与 `process.exec` 共用 `narrowCommand` 这一份白名单门 —— 能开进交互式
        终端的命令不比能一次性执行的更宽,两处各挂一份清单迟早分叉。
        「工作区存不存在、是不是远程」manager 判不了(它看不到工作区存储),
        交给 `deps.launchTerminal`;那边的拒绝同样以 `opened: false` 回,
        插件拿 reason 去给用户一句话。
      */
      case 'tabs.openTerminal': {
        const p = rawParams as { workspaceId: string; command: string; args?: string[]; env?: Record<string, string>; title?: string }
        if (typeof p.workspaceId !== 'string' || p.workspaceId === '') {
          return { data: { opened: false, reason: 'declined' as const }, summary: 'rejected terminal: no workspace' }
        }
        const narrowed = narrowCommand(record.manifest.allowedCommands, p.command, p.args ?? [])
        if (!narrowed.ok) return { data: { opened: false, reason: 'declined' as const }, summary: `rejected terminal command: ${narrowed.reason}` }
        const env = narrowLaunchEnv(p.env)
        if (!env.ok) return { data: { opened: false, reason: 'declined' as const }, summary: `rejected terminal env: ${env.reason}` }
        const title = typeof p.title === 'string' && p.title.length > 0 && p.title.length <= 128 && p.title.startsWith('%') && p.title.endsWith('%')
          ? p.title
          : undefined
        const result = this.deps.launchTerminal(pluginId, {
          workspaceId: p.workspaceId,
          command: narrowed.value.command,
          args: narrowed.value.args,
          env: env.value,
          ...(title === undefined ? {} : { title })
        })
        return {
          data: result,
          summary: result.opened ? `open terminal: ${narrowed.value.command}` : `terminal declined: ${result.reason ?? 'unknown'}`
        }
      }

      case 'configuration.get':
        return { data: { values: this.configuration(pluginId) }, summary: 'configuration.get' }

      case 'window.showMessage': {
        const p = rawParams as { kind: 'info' | 'warn' | 'error'; messageKey: string; params?: Record<string, string | number> }
        /*
          ★ 传的是 **key + params**,不是渲染好的句子。主进程新增代码一律只传 key
          (计划 §8.3),由渲染层 `t()` 渲染 —— 否则通知里那句话永远是插件作者
          写死的那一种语言。
        */
        this.deps.showMessage(pluginId, p.kind, p.messageKey, p.params ?? {})
        return { data: {}, summary: `${p.kind}: ${p.messageKey}` }
      }

      /*
        ───────── 要用户回答的三种交互 ─────────

        需求:插件要问一句话才能继续(选哪个分支、给个名字、这步危险不危险)。
        此前 `window.showQuickPick` 在协议表、d.ts、垫片里都存在,**唯独没有
        handler** —— 一调就是 `internal_error: no handler`。

        ★ 三条共用一个「广播请求 → 等回执」的机制(`deps.requestInteraction`),
        取消一律是取消值而不是错误:用户没理会一个弹窗不是故障。
      */
      case 'window.showQuickPick': {
        const p = rawParams as { items: { id: string; labelKey: string }[]; placeholderKey?: string }
        const items = (Array.isArray(p.items) ? p.items : [])
          .filter((item) => item !== null && typeof item === 'object' && typeof item.id === 'string' && typeof item.labelKey === 'string')
          .slice(0, MAX_QUICK_PICK_ITEMS)
        if (items.length === 0) return { data: { id: null }, summary: 'quickPick with no valid items' }
        const picked = await this.deps.requestInteraction(pluginId, {
          kind: 'quickPick',
          items,
          ...(p.placeholderKey === undefined ? {} : { placeholderKey: p.placeholderKey })
        })
        // 回执必须是**给出去的那几个 id 之一**。渲染层是可信的,但这条断言让
        // 「回执错位」成为不可能,而不是变成一个只在特定时序下出现的怪现象。
        const id = typeof picked === 'string' && items.some((item) => item.id === picked) ? picked : null
        return { data: { id }, summary: `quickPick → ${id ?? '(cancelled)'}` }
      }

      case 'window.showInputBox': {
        const p = rawParams as { titleKey: string; placeholderKey?: string; initial?: string; password?: boolean }
        if (typeof p.titleKey !== 'string' || p.titleKey === '') return { data: { value: null }, summary: 'rejected input titleKey' }
        const answer = await this.deps.requestInteraction(pluginId, {
          kind: 'input',
          titleKey: p.titleKey,
          ...(p.placeholderKey === undefined ? {} : { placeholderKey: p.placeholderKey }),
          ...(typeof p.initial === 'string' ? { initial: p.initial.slice(0, 4096) } : {}),
          ...(p.password === true ? { password: true } : {})
        })
        const value = typeof answer === 'string' ? answer : null
        // ★ 摘要里**不写内容**:这条可能是一个 token(`password: true` 尤其)。
        return { data: { value }, summary: `input ${p.titleKey} → ${value === null ? '(cancelled)' : `${value.length} chars`}` }
      }

      case 'window.showConfirm': {
        const p = rawParams as { titleKey: string; detailKey?: string; danger?: boolean }
        if (typeof p.titleKey !== 'string' || p.titleKey === '') return { data: { confirmed: false }, summary: 'rejected confirm titleKey' }
        const answer = await this.deps.requestInteraction(pluginId, {
          kind: 'confirm',
          titleKey: p.titleKey,
          ...(p.detailKey === undefined ? {} : { detailKey: p.detailKey }),
          ...(p.danger === true ? { danger: true } : {})
        })
        // ★ 只有**明确的 true** 才算确认:超时、窗口关掉、回执畸形一律不算。
        const confirmed = answer === true
        return { data: { confirmed }, summary: `confirm ${p.titleKey} → ${String(confirmed)}` }
      }

      case 'window.progressStart': {
        const p = rawParams as { id: string; titleKey: string }
        if (typeof p.id !== 'string' || p.id === '' || p.id.length > 64) return { data: {}, summary: 'rejected progress id' }
        /*
          ★ 条数有上限,同状态栏格子的理由:一个插件挂二十条进度,界面上就没有
          别人的位置了。超出的**静默忽略**而不是替换 —— 替换会让先开的那条永远
          结束不了(它的 id 已经被挤掉,`progressEnd` 找不到它)。
        */
        if (record.progress.size >= MAX_PROGRESS_PER_PLUGIN && !record.progress.has(p.id)) {
          return { data: {}, summary: 'progress limit reached' }
        }
        record.progress.add(p.id)
        this.deps.emitProgress(pluginId, { id: p.id, titleKey: typeof p.titleKey === 'string' ? p.titleKey : '' })
        return { data: {}, summary: `progress start ${p.id}` }
      }

      case 'window.progressUpdate': {
        const p = rawParams as { id: string; fraction?: number; messageKey?: string }
        // 没 start 过的 id 直接丢:进度条是「这次工作」的投影,凭空出现的一条
        // 没有对应的工作,也没有人会去结束它。
        if (!record.progress.has(p.id)) return { data: {}, summary: 'progress update for an unknown id' }
        this.deps.emitProgress(pluginId, {
          id: p.id,
          ...(typeof p.fraction === 'number' && Number.isFinite(p.fraction) ? { fraction: Math.max(0, Math.min(1, p.fraction)) } : {}),
          ...(typeof p.messageKey === 'string' ? { messageKey: p.messageKey } : {})
        })
        return { data: {}, summary: `progress update ${p.id}` }
      }

      case 'window.progressEnd': {
        const p = rawParams as { id: string }
        record.progress.delete(p.id)
        this.deps.emitProgress(pluginId, { id: p.id, done: true })
        return { data: {}, summary: `progress end ${p.id}` }
      }

      case 'workspace.subscribeChanges': {
        const p = rawParams as { globs?: string[] }
        const globs = (Array.isArray(p.globs) ? p.globs : [])
          .filter((glob): glob is string => typeof glob === 'string' && glob !== '')
          .slice(0, 32)
        record.watchGlobs = globs
        record.watchWorkspaceId = ctx.workspaceId
        return { data: {}, summary: `subscribe changes ${globs.length === 0 ? '(all)' : globs.join(',')}` }
      }

      case 'workspace.unsubscribeChanges':
        record.watchGlobs = undefined
        return { data: {}, summary: 'unsubscribe changes' }

      /*
        ───────── 流式跑命令 ─────────

        需求:构建 / 测试 / 打包这类命令跑几十秒,而 `process.exec` 的形状让插件
        在那几十秒里一个字都拿不到,只能在结束后一次性倒出来 —— 做不出进度,
        也没法在出第一条错误时就停。

        ★ 门与 `process.exec` **共用同一段** `prepareExec`(参数门 + cwd 收窄 +
        引号化 + 审批)。抄第二份的代价是「哪些命令算被批准过」在两条路上分叉。

        ★ 放在 manager 而不是 `rpc.ts`:输出要按 execId 经 `kind: 'event'` 推回
        插件,而只有这里拿得到 runtime。
      */
      case 'process.execStream': {
        const p = rawParams as { command: string; args: string[]; cwd?: string; timeoutMs?: number }
        const prepared = await prepareExec(ctx, p)
        if (this.execStreams.size >= MAX_CONCURRENT_EXEC_STREAMS) {
          throw new CapabilityError('rejected', 'too many concurrent streaming commands')
        }
        /*
          ★ **流式命令的生命期独立于启动它的那条 RPC 的 30 秒期限。**

          它是「一条跑几十秒的命令」,而启动它的只是一次普通 RPC —— 把两者绑在
          一起的话,每个 `npm run build` 都会在 30 秒时被 `execAbort` 的效果截断,
          而插件作者看不见任何原因(那个 controller 是宿主的)。

          但独立 ≠ 没人管:它仍然**跟随**(见 `startExecStream`)
          1. 产生这次调用的工具调用的 abort(用户点停止 —— 长命令要真的停下来);
          2. 插件被禁用 / 卸载(走 `controller`);
          3. 自己的 `timeoutMs`;
          并且**四条结束路径都要推一条 `process.exit`**,否则插件那边 `await done` 永远挂着。

          这里的 `controller` 只保留 `execAbort` / 插件禁用两个来源 ——
          `ctx.signal` 上那条 tool 的 abort 单独挂(`toolSignal`),不经过它。
        */
        const execId = `exec-${String(this.nextExecId++)}`
        // ★ 这里起的是一个**真的子进程** —— 越过它之后被取消就是 result_unknown。
        ctx.markSideEffectStarted?.()
        this.startExecStream(execId, pluginId, prepared, ctx.toolSignal)
        return { data: { execId }, summary: `execStream ${prepared.command}` }
      }

      case 'process.execAbort': {
        const p = rawParams as { execId: string }
        const running = this.execStreams.get(p.execId)
        // 不是自己的 execId 一律当成不存在 —— 否则一个插件能掐断另一个插件的命令。
        if (running === undefined || running.pluginId !== pluginId) {
          return { data: {}, summary: `execAbort ignored (unknown execId)` }
        }
        running.controller.abort()
        return { data: {}, summary: `execAbort ${p.execId}` }
      }

      case 'commands.execute': {
        const p = rawParams as { commandId: string; args?: unknown }
        /*
          ★ **只能执行自己贡献的命令。** 允许跨插件执行等于给了一条绕过所有
          能力门的路:A 插件没有 `process` 能力,但它可以去调 B 插件那条
          「跑构建」的命令。宿主自己的命令同理不开放 —— 那是 UI 层的事,
          不是插件能力的一部分。
        */
        if (!record.manifest.contributes.commands.some((c) => c.command === p.commandId)) {
          return { data: { value: null }, summary: `refused cross-plugin command ${p.commandId}` }
        }
        /*
          ★ 这条 RPC 只是把工作**交给插件里的命令处理器**,而那次工作我们看不见也
          取消不了(可能已经在改文件、起进程)。越过它之后的取消报 result_unknown,
          免得插件把一条已经跑了一半的命令再做一遍。
        */
        ctx.markSideEffectStarted?.()
        const value = await this.deps.runtime.invoke(
          pluginId,
          { id: 0, kind: 'command.run', payload: { commandId: p.commandId, args: p.args } },
          PLUGIN_TIMEOUT.COMMAND_MS
        )
        return { data: { value }, summary: `execute ${p.commandId}` }
      }

      case 'tool.progress': {
        const p = rawParams as { callId: string; message?: string; card?: unknown }
        const live = this.liveToolEmits.get(p.callId)
        // callId 不在跑 / 不是这个插件的 → 静默丢弃(工具可能刚结束,竞态正常)。
        if (live === undefined || live.pluginId !== pluginId) {
          return { data: {}, summary: `tool.progress ignored (stale callId)` }
        }
        const cardViewTypes = new Set(record.manifest.contributes.cardViews.map((v) => v.viewType))
        const card = sanitizeToolCard(p.card, cardViewTypes)
        live.emit({ callId: p.callId, message: p.message ?? '', ...(card === undefined ? {} : { card }) })
        return { data: {}, summary: `tool.progress${card === undefined ? '' : ' +card'}` }
      }

      case 'plugins.expose': {
        const p = rawParams as { methods: string[] }
        record.exposedApi = new Set((Array.isArray(p.methods) ? p.methods : []).filter((m) => typeof m === 'string'))
        return { data: {}, summary: `expose ${record.exposedApi.size} api(s)` }
      }

      case 'plugins.invoke': {
        const p = rawParams as { target: string; method: string; args: unknown[] }
        // ★ 准入门:目标必须在**本插件清单**的 dependencies 里声明过。permission `plugins`
        //   已在 handleRequest 那层查过 —— 这里查的是「能连谁」的那道独立门。
        if (!Object.hasOwn(record.manifest.dependencies, p.target)) {
          return { data: { value: null }, summary: `refused: ${p.target} not a declared dependency` }
        }
        const target = this.records.get(p.target)
        if (target === undefined || !target.enabled || target.status === 'error' || target.status === 'pending-approval') {
          return { data: { value: null }, summary: `target ${p.target} unavailable` }
        }
        await this.wake(p.target) // 依赖此刻可能在睡 —— 叫醒它
        if (target.status !== 'active') return { data: { value: null }, summary: `target ${p.target} not running` }
        /*
          ★ 同 `commands.execute`:真正的动作发生在**别的插件的上下文**里,我们
          既看不见也取消不了。取消之后必须报 result_unknown。
        */
        ctx.markSideEffectStarted?.()
        const result = await this.deps.runtime.invoke(
          p.target,
          { id: 0, kind: 'api.call', payload: { method: p.method, args: Array.isArray(p.args) ? p.args : [], from: pluginId } },
          PLUGIN_TIMEOUT.COMMAND_MS
        )
        return { data: { value: (result as { value?: unknown } | undefined)?.value ?? null }, summary: `invoke ${p.target}.${p.method}` }
      }

      case 'plugins.emitEvent': {
        const p = rawParams as { topic: string; payload: unknown }
        const subs = this.eventSubscribers.get(p.topic)
        if (subs !== undefined) {
          for (const subId of subs) {
            if (subId === pluginId) continue // 不回给自己
            const sub = this.records.get(subId)
            if (sub === undefined || sub.status !== 'active') continue // 只投给在跑的订阅者
            void this.deps.runtime
              .invoke(subId, { id: 0, kind: 'plugins.event', payload: { topic: p.topic, payload: p.payload, from: pluginId } }, PLUGIN_TIMEOUT.COMMAND_MS)
              .catch(() => undefined)
          }
        }
        return { data: {}, summary: `emit ${p.topic}` }
      }

      case 'plugins.subscribeEvent': {
        const p = rawParams as { topic: string }
        let subs = this.eventSubscribers.get(p.topic)
        if (subs === undefined) { subs = new Set(); this.eventSubscribers.set(p.topic, subs) }
        subs.add(pluginId)
        record.subscribedTopics.add(p.topic)
        return { data: {}, summary: `subscribe ${p.topic}` }
      }

      case 'plugins.unsubscribeEvent': {
        const p = rawParams as { topic: string }
        this.eventSubscribers.get(p.topic)?.delete(pluginId)
        record.subscribedTopics.delete(p.topic)
        return { data: {}, summary: `unsubscribe ${p.topic}` }
      }

      default:
        return undefined
    }
  }

  // ─────────────────────────── Agent 侧贡献 ───────────────────────────

  /**
   * 这一刻所有启用插件贡献的工具。`registerToolProvider` 每次装配都会问一遍 ——
   * 所以禁用一个插件之后,它的工具在**下一次**装配时就没了,不需要额外下线。
   */
  /**
   * 现在生效的插件 Skill 目录,**绝对路径**,按 `pluginId` 排序。
   *
   * ## 需求
   *
   * 插件包里自带的 `skills/<name>/` 要出现在模型的 Skill 目录里,而且**随插件的
   * 启用状态即时增减** —— 禁用一个插件,它带来的 skill 下一轮就该不在了。
   *
   * ## 为什么返回路径而不是返回解析好的 Skill
   *
   * 解析 `SKILL.md`(frontmatter、描述上限、正文消毒、名字合法性)整套逻辑在
   * `kernel/skill/load.ts` 里,而且那套规则**必须**和用户自己装的 skill 完全一致:
   * 两套解析器的结果只要差一点,就会出现「同一个 SKILL.md 放在插件里能用、
   * 放在 ~/.nextcowork/skills 里不能用」这种没人能解释的现象。
   * 所以这里只回答「读哪几个目录」,判定留给唯一那个判定者。
   *
   * ## 这几道筛子分别挡什么
   *
   * - `enabled` / `status`:与 `contributedTools()` 逐条相同。一个停在
   *   `pending-approval` 的插件不该往模型上下文里塞东西 —— 那正是用户还没点同意的东西。
   * - 排序:同名 skill 的赢家由**顺序**决定(先来的赢,见 `scanPluginRoots`)。
   *   不排的话,赢家取决于 Map 的插入顺序,也就是取决于用户当初的安装顺序 ——
   *   同样两个插件,在两台机器上可能给出不同的结果。
   */
  contributedSkillRoots(): PluginSkillRoot[] {
    const out: PluginSkillRoot[] = []
    for (const [pluginId, record] of [...this.records.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
      if (!record.enabled || record.status === 'error' || record.status === 'pending-approval') continue
      for (const skill of record.manifest.contributes.skills) {
        out.push({ pluginId, dir: join(record.root, skill.path) })
      }
    }
    return out
  }

  /**
   * 需求：模型装配工具表之前激活已启用的工具插件；声明 onTool 的插件
   * 还没有运行期注册表项，若等到工具调用时才唤醒，它永远不会进模型工具清单。
   * 每轮只唤醒声明了 onTool 的插件，休眠后下一轮能重新注册。
   */
  async prepareContributedTools(): Promise<void> {
    for (const [id, record] of this.records) {
      if (!record.enabled || record.status === 'error' || record.status === 'pending-approval') continue
      const tool = record.manifest.contributes.tools.find((item) =>
        record.manifest.activationEvents.includes(`onTool:${item.name}`))
      if (tool !== undefined) await this.wake(id)
    }
  }

  contributedTools(): ToolRegistration[] {
    const out: ToolRegistration[] = []
    for (const [pluginId, record] of this.records) {
      if (!record.enabled || record.status === 'error' || record.status === 'pending-approval') continue
      // frame 卡片只能指向该插件自己声明的 cardViews —— 每个插件算一次。
      const cardViewTypes = new Set(record.manifest.contributes.cardViews.map((v) => v.viewType))
      for (const declaration of record.tools.values()) {
        out.push(
          toolRegistrationFor(pluginId, declaration, async (name, input, callId, signal, emit, workspaceId) => {
            // 工具调用本身也是一次「碰一下」,免得跑着跑着被休眠扫走。
            record.touchedAt = Date.now()
            /*
              登记这次调用的 emit 与工作区作用域,让 `documents.*` 与工作区类 RPC
              都找得到它;结束即撤。

              ★ 只记 **id**,root 在每次 RPC 时经 `deps.resolveWorkspace` 现查
              (和 `documents.*` 同一口径)。冻结 root 看起来更稳,但工具回调这一层
              拿不到它,而自己读一次当前焦点会是**错的**那个值 —— 那正是这条机制
              要修的东西。现查的代价只有一个:同一个 id 被重新指向另一个根时,
              调用会写到新根上,而那个工作区此刻确实是它。
            */
            this.liveToolEmits.set(callId, {
              pluginId,
              ...(workspaceId === undefined ? {} : { workspaceId }),
              signal,
              emit
            })
            // 交互式工具会挂起等用户点按钮,60s 太短 —— 放宽到宿主硬上限(fork B)。
            const timeoutMs = declaration.interactive === true ? PLUGIN_TIMEOUT.INTERACTIVE_TOOL_MS : PLUGIN_TIMEOUT.TOOL_MS
            const invocation = this.deps.runtime.invoke(
              pluginId,
              { id: 0, kind: 'tool.execute', payload: { name, input, callId } },
              timeoutMs
            )
            /*
              ★ 用户点停止 → 给插件发一条 `tool.abort`,然后**只等 2 秒宽限**。
              等不到就按工具失败返回、run 继续 —— 一个不理会中断的插件不该
              把整条 run 钉死在那里。
            */
            const onAbort = (): void => {
              void this.deps.runtime.invoke(
                pluginId,
                { id: 0, kind: 'tool.abort', payload: { name, callId } },
                PLUGIN_TIMEOUT.ABORT_GRACE_MS
              ).catch(() => undefined)
            }
            signal.addEventListener('abort', onAbort, { once: true })
            try {
              return await invocation
            } finally {
              signal.removeEventListener('abort', onAbort)
              this.liveToolEmits.delete(callId)
            }
          }, cardViewTypes)
        )
      }
    }
    return out
  }

  /**
   * 用户点了实时卡片上的按钮 —— 把动作送给**仍在运行**的那次工具调用(第 2 层)。
   *
   * ★ 按 `liveToolEmits` 核对:callId 必须正在跑、且属于这个插件,否则静默丢弃。
   * 这挡住两件事:给已结束的工具发动作(竞态),以及借别人的 callId 往别的插件
   * 塞输入(和 `tool.progress` 同一道 pluginId 门)。
   *
   * 即发即忘:一次 `tool.action` 反向调用把动作交给插件垫片,由它路由到工具注册的
   * `onAction`。失败不回传 —— 挂起的工具要么收到别的动作、要么被 abort、要么超时。
   */
  async deliverCardAction(pluginId: string, callId: string, actionId: string, value?: unknown): Promise<void> {
    const live = this.liveToolEmits.get(callId)
    if (live === undefined || live.pluginId !== pluginId) return
    await this.deps.runtime
      .invoke(pluginId, { id: 0, kind: 'tool.action', payload: { callId, actionId, value } }, PLUGIN_TIMEOUT.ABORT_GRACE_MS)
      .catch(() => undefined)
  }

  /**
   * 工具拦截器的合并表决。
   *
   * ★ **只能收紧,不能放宽** —— 这一条对应 `kernel/permission-decision.ts`
   * 的优先级本体:`deny` 压过一切,`ask` 压过 `allow`。插件返回 `allow`
   * 在这里被**当成弃权**,因为让一个第三方插件把审批弹窗关掉,等于把
   * 整条权限链的最后一道门交给它。
   *
   * ★ 超时 = 弃权(fail-open,只记诊断),同 `kernel/hook/run.ts`:
   * 一个卡住的插件不该把所有工具调用堵死。
   */
  async intercept(input: {
    toolName: string
    toolInput: unknown
    readOnly: boolean
    destructive: boolean
  }): Promise<{ deny?: string; ask?: boolean }> {
    let ask = false
    for (const [pluginId, record] of this.records) {
      if (!record.interceptor || !record.enabled || record.status !== 'active') continue
      try {
        const verdict = (await this.deps.runtime.invoke(
          pluginId,
          { id: 0, kind: 'interceptor.willInvoke', payload: input },
          PLUGIN_TIMEOUT.INTERCEPTOR_MS
        )) as { decision?: string; reasonKey?: string } | undefined
        if (verdict?.decision === 'deny') return { deny: verdict.reasonKey ?? `denied by ${pluginId}` }
        if (verdict?.decision === 'ask') ask = true
        // `allow` 落在这里 —— 什么都不做,也就是弃权。
      } catch (error) {
        record.diagnostics.push({
          path: 'agent.registerToolInterceptor',
          message: `willInvoke did not answer in time: ${(error as Error).message}`,
          level: 'warn'
        })
      }
    }
    return ask ? { ask: true } : {}
  }

  /**
   * 本轮要注入的上下文。
   *
   * 强制包裹 + 双重上限(单次 / 每轮)。**不是系统提示词** —— 插件永远拿不到
   * 往系统提示词里塞东西的杠杆,理由见计划 §3.5。
   */
  async provideContext(input: { prompt: string }): Promise<string> {
    const chunks: string[] = []
    let total = 0
    for (const [pluginId, record] of this.records) {
      if (!record.contextProvider || !record.enabled || record.status !== 'active') continue
      if (total >= PLUGIN_CONTEXT_LIMIT.PER_TURN) break
      try {
        const text = (await this.deps.runtime.invoke(
          pluginId,
          { id: 0, kind: 'context.provide', payload: input },
          PLUGIN_TIMEOUT.CONTEXT_MS
        )) as string | undefined
        if (typeof text !== 'string' || text === '') continue
        const wrapped = wrapPluginContext(
          pluginId,
          text,
          Math.min(PLUGIN_CONTEXT_LIMIT.PER_CALL, PLUGIN_CONTEXT_LIMIT.PER_TURN - total)
        )
        if (wrapped === '') continue
        total += wrapped.length
        chunks.push(wrapped)
      } catch {
        // 超时/抛错 = 这一轮少一段上下文,不是一次失败的 run。
      }
    }
    return chunks.join('\n\n')
  }

  // ─────────────────────────── 关 Tab 前的挽留 ───────────────────────────

  /**
   * 关掉这个文件之前,先让还脏着的插件编辑器把它存了。
   *
   * ★ **不做这一步,关 Tab 会静默丢图。** 内置文档的挽留走
   * `stores/documents.ts` 的 `confirmDocumentChanges`,而那个函数只问
   * `documents` store —— 插件编辑器的改动根本不在那张表里。
   *
   * ★ 这里的语义是「**存**,不是问」:问一次需要一个能显示插件文档的对话框,
   * 而宿主不知道那份文档长什么样。让插件自己存下来,失败才拦住关闭 ——
   * 用户既不会丢东西,也不会为一个能自动处理的情况多点一次。
   *
   * 返回 `false` = 有东西没存成,**不要关**。
   */
  async saveBeforeClose(input: { path?: string }): Promise<boolean> {
    let ok = true
    for (const [pluginId, record] of this.records) {
      if (record.status !== 'active' || record.dirtyDocuments.size === 0) continue
      for (const [documentId, path] of [...record.dirtyDocuments]) {
        // `path` 省略 = 问这个插件的全部脏文档(关工作区、退出应用走这条)
        if (input.path !== undefined && path !== input.path) continue
        try {
          await this.deps.runtime.invoke(
            pluginId,
            { id: 0, kind: 'customEditor.save', payload: { documentId, path } },
            PLUGIN_TIMEOUT.COMMAND_MS
          )
          record.dirtyDocuments.delete(documentId)
        } catch (error) {
          /*
            存不下来 —— 拦住关闭,并留一条诊断。**不能默默放行**:
            那正是「关 Tab 丢图」那条路。
          */
          ok = false
          record.diagnostics.push({
            path,
            message: `the editor could not save before closing: ${(error as Error).message}`,
            level: 'error'
          })
        }
      }
    }
    if (!ok) this.deps.emitChanged()
    return ok
  }

  // ─────────────────────────── 设置项 ───────────────────────────

  /**
   * 这个插件此刻的设置值 = 清单里的默认值 + 用户改过的那些。
   *
   * ★ **不扩 `AppSettings` 那个 blob**(计划 §6.2):那份设置是全应用的,
   * 每一次写都要整份重写、整份同步。插件设置放独立 kv,一个插件的开关
   * 改了不会让另一台机器把整份应用设置拉一遍。
   *
   * ★ 读的时候**以清单为准**:用户改过、后来插件升级把那一项删掉了 ——
   * 残留的值不该还出现在结果里,否则插件会读到一个它已经不认识的键。
   */
  configuration(pluginId: string): Record<string, boolean | string | number> {
    const record = this.records.get(pluginId)
    if (record === undefined) return {}
    const properties = record.manifest.contributes.configuration?.properties ?? {}
    const stored = this.deps.getKv<Record<string, boolean | string | number>>(`plugins.config.${pluginId}`, {})
    const out: Record<string, boolean | string | number> = {}
    for (const [key, property] of Object.entries(properties)) {
      const value = stored[key]
      out[key] = isAssignable(property.type, value) ? value : (property.default ?? defaultFor(property.type))
    }
    return out
  }

  /** 用户在详情页里改了一项。类型对不上的直接丢掉,不写进 kv。 */
  setConfiguration(pluginId: string, key: string, value: boolean | string | number | null): void {
    const record = this.records.get(pluginId)
    const property = record?.manifest.contributes.configuration?.properties[key]
    if (record === undefined || property === undefined) return
    const stored = this.deps.getKv<Record<string, boolean | string | number>>(`plugins.config.${pluginId}`, {})
    // null = 恢复默认值。删掉那一条,而不是写一个 null 进去 —— 后者会让
    // 「用户没设过」和「用户设成了空」变成同一件事。
    if (value === null) delete stored[key]
    else if (isAssignable(property.type, value)) stored[key] = value
    else return
    this.deps.setKv(`plugins.config.${pluginId}`, stored)
    this.deps.emitChanged()
  }

  private kvFor(pluginId: string): CapabilityContext['kv'] {    const key = `plugins.kv.${pluginId}`
    const read = (): Record<string, string> => this.deps.getKv<Record<string, string>>(key, {})
    return {
      get: (k) => read()[k] ?? null,
      set: (k, value) => {
        const current = read()
        if (value === null) delete current[k]
        else current[k] = value
        this.deps.setKv(key, current)
      },
      keys: () => Object.keys(read()),
      usedBytes: () => JSON.stringify(read()).length
    }
  }

  private persist(): void {
    const state: PersistedState = {}
    for (const [id, record] of this.records) {
      state[id] = {
        enabled: record.enabled,
        granted: record.granted,
        approvedRequired: record.approvedRequired,
        installedAt: record.installedAt,
        updatedAt: record.updatedAt,
        ...(record.slug === undefined ? {} : { slug: record.slug })
      }
    }
    this.deps.setKv(KV_KEY, state)
  }
}

function isAssignable(type: string, value: unknown): value is boolean | string | number {
  if (type === 'boolean') return typeof value === 'boolean'
  if (type === 'number') return typeof value === 'number' && Number.isFinite(value)
  return typeof value === 'string'
}

function defaultFor(type: string): boolean | string | number {
  return type === 'boolean' ? false : type === 'number' ? 0 : ''
}

/** 活动日志里只记主机名,不记整条 URL —— 查询串里可能有 token。 */
function hostOf(url: string): string {
  try { return new URL(url).host } catch { return '(invalid url)' }
}

/**
 * 流式输出的合批器。
 *
 * ★ 存在的理由是**一次 `npm install` 能在一秒里触发上百次 data 事件**。
 * 逐条往插件推等于一秒上百次跨进程 invoke,而插件那边根本分辨不出区别 ——
 * 它只会看到界面变卡。50ms 一批,人眼看不出延迟。
 *
 * ★ 总量有上限:超了之后**仍然收数据**(不收会让子进程阻塞在 write 上,
 * 见 `node-spawn.ts` 的同款说明),只是改推一条 `truncated` 标记,不再转发正文。
 */
class OutputPump {
  private buffers: Record<'stdout' | 'stderr', string> = { stdout: '', stderr: '' }
  private timer: NodeJS.Timeout | undefined
  private sent = 0
  private truncated = false

  constructor(private readonly emit: (stream: 'stdout' | 'stderr', text: string, truncated: boolean) => void) {}

  push(stream: 'stdout' | 'stderr', text: string): void {
    if (this.sent >= MAX_EXEC_STREAM_CHARS) {
      if (!this.truncated) {
        this.truncated = true
        this.emit(stream, '', true)
      }
      return
    }
    this.buffers[stream] += text
    this.sent += text.length
    if (this.timer !== undefined) return
    this.timer = setTimeout(() => { this.flush() }, EXEC_STREAM_BATCH_MS)
    this.timer.unref?.()
  }

  flush(): void {
    if (this.timer !== undefined) { clearTimeout(this.timer); this.timer = undefined }
    for (const stream of ['stdout', 'stderr'] as const) {
      const text = this.buffers[stream]
      if (text === '') continue
      this.buffers[stream] = ''
      this.emit(stream, text, false)
    }
  }
}

async function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T> {
  let timer: NodeJS.Timeout | undefined
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => { reject(new Error('plugin_timeout')) }, ms)
      })
    ])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}
