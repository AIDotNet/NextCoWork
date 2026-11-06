/**
 * 插件的 IPC 面 —— 渲染层与插件系统之间的全部通道。
 *
 * ★ **和插件自己那条 RPC 是两条完全不同的通道。** 这一条是给**主窗口**用的
 * (受信任的渲染层,走 `shared/ipc/contract.ts` 的三张白名单);插件那条在
 * `plugin/host-window.ts`,独立协议、独立白名单、独立能力校验。
 * 两者唯一的交点是 `PluginManager`,而它对两边的信任级别是不一样的。
 */
import { app, clipboard, dialog, shell } from 'electron'
import { randomUUID } from 'node:crypto'
import { join } from 'node:path'
import type { ResolvedTheme } from '../../shared/domain/settings'
import { pluginAppearance, setPluginRuntimeDir } from '../plugin/protocol'
import type { PluginPermission } from '../../shared/plugin/permission'
import { isRunnable, type InstalledPlugin, type PluginActivity, type PluginCatalog } from '../../shared/plugin/state'
import { PLUGIN_API_VERSION } from '../../shared/plugin/api-version'
import { PLUGINS_DIR, PluginManager } from '../plugin/manager'
import type { PluginInteractionRequest } from '../../shared/plugin/ui-request'
import type { PluginScmAdapter } from '../plugin/rpc'
import { ElectronPluginRuntime, pluginPreloadPath, pluginRuntimeDir } from '../plugin/host-window'
import { NativeComponentError } from '../plugin/native-installer'
import { recentActivity } from '../plugin/diagnostics'
import { setFileChangeListener } from '../kernel/tool/builtin/change-recorder'
import { setPluginSkillRootsProvider } from '../kernel/skill/load'
import { store } from '../state/store'
import {
  checkoutGitBranch,
  commitGit,
  createGitBranch,
  getGitDiff,
  getGitOverview,
  listGitBranches,
  listGitCommits,
  stageGitPaths
} from './git'
import {
  getHost,
  getTools,
  installPluginContextProvider,
  installPluginInterceptor,
  installPluginToolPreparation,
  installPluginToolProvider
} from '../runtime'
import { windows } from '../window/registry'
import { terminalHost } from '../terminal-host'
import { IpcError } from './errors'
import { DocumentSessionManager } from '../document-engine/manager'
import { DocumentEngineProviderRegistry, splitProviderId } from '../document-engine/provider-registry'
import { NativeDocumentEngineProvider } from '../document-engine/native-host'
import { PluginDocuments } from '../plugin/document-rpc'
import { DocumentViewChannel } from '../plugin/document-view'
import { notifyDocumentViewsChanged, setDocumentViewChannel } from './document-engine'
import { currentConfigScope } from '../db/config-profile'
import { engineContributionRefusal, helperSpawnRefusal } from './plugin-engines'

let manager: PluginManager | null = null
let documentLeaseTimer: ReturnType<typeof setInterval> | undefined

/**
 * 当前这套插件系统在用的文档会话表。
 *
 * ★ 留在模块级而不是关在 `startPlugins()` 里,是因为**退出与切账户还要用它**:
 * 插件系统关掉之后仍然可能有「provider 已经撤销、但会话还挂着」的干净会话,
 * 那些 helper 没有任何人再去收(见 `shutdownPlugins` 的收尾)。
 */
let documentSessions: DocumentSessionManager | null = null

/** 没起来时返回一份空 catalog —— 界面不该因为插件系统没初始化就打不开。 */
function emptyCatalog(): PluginCatalog {
  return { plugins: [], hostVersion: app.getVersion(), apiVersion: PLUGIN_API_VERSION }
}

export function pluginManager(): PluginManager | null {
  return manager
}

/**
 * 插件看得见的那一面 git —— **复用 `ipc/git.ts`,一行 git 命令都不在这里拼**。
 *
 * 需求:插件要能读仓库状态做判断(「有未提交改动就别发布」)、在用户批准后提交。
 * 不满足会怎样:`ncw.scm.status()` 抛 `no handler` —— 而 `scm.read` / `scm.write`
 * 两条能力在枚举里躺了一整版,任何勾选过它们的用户都被许诺了一件做不到的事。
 *
 * ★ 为什么不直接让 `rpc.ts` 去 import `ipc/git.ts`:那会把「插件能力层」钉死在
 * Electron 的 IPC 层上(`git.ts` 会抛 `IpcError`、认得 `store`),而 `rpc.ts` 现在
 * 只依赖 `KernelHost`,整层可以脱开 electron 直测。适配器在这里把两边的错误语义
 * 也对齐了:git 那边的 `IpcError` 在这里变成插件协议里的失败,不会穿过去。
 *
 * ★ 「开不出来」(远程工作区 / 没装 git / 不是仓库)不是异常,`getGitOverview`
 * 返回 `available: false`。这里把它翻译成一条**明确的拒绝**,而不是一份空状态 ——
 * 空状态会让插件以为「这是个干净的仓库」,然后据此做出错误的决定。
 *
 * ★ **每个工作区一份 scm 适配器**(只留最近用到的那个)。
 * 原因不是性能:工作区类 RPC 现在会**冻结**「这次调用的工作区」(见 manager 的
 * `resolveCallWorkspace`),而适配器若不跟着那份选择走,症状就是「插件在 A 的
 * 工具调用里 stage 了 B 的文件」。缓存键取 id + root —— 同一个 id 被重新指向
 * 另一个根时,旧适配器必须作废,否则它还会对着老仓库跑 git。
 * 只留一份(进新条目就清表),所以两个工作区交替调用时会各重建一次 ——
 * 适配器本身全是闭包,真正的 IO 每次都现查,重建不改变任何行为。
 */
const scmAdapters = new Map<string, PluginScmAdapter>()

function scmAdapterFor(workspaceId: string): PluginScmAdapter {
  if (workspaceId === '') throw new Error('no workspace is open')
  const workspace = store.listWorkspaces().find((item) => item.id === workspaceId)
  if (workspace === undefined || (workspace.environment?.kind ?? 'local') === 'connection') {
    // 远程 / 已关闭的工作区在本机没有可跑的 git —— 明确拒绝,不给一份空状态。
    throw new Error(`git is unavailable here: workspace ${workspaceId} is not a local workspace`)
  }
  const cacheKey = `${workspaceId}\u0000${workspace.rootPath}`
  const cached = scmAdapters.get(cacheKey)
  if (cached !== undefined) return cached
  const adapter = buildScmAdapter(workspaceId)
  scmAdapters.clear()
  scmAdapters.set(cacheKey, adapter)
  return adapter
}

function buildScmAdapter(workspaceId: string): PluginScmAdapter {
  const requireOverview = async (): Promise<Extract<Awaited<ReturnType<typeof getGitOverview>>, { available: true }>> => {
    if (workspaceId === '') throw new Error('no workspace is open')
    const overview = await getGitOverview({ workspaceId })
    if (!overview.available) throw new Error(`git is unavailable here: ${overview.reason}`)
    return overview
  }
  return {
    status: async () => {
      const overview = await requireOverview()
      return {
        branch: overview.branch,
        staged: overview.files.filter((f) => f.staged).map((f) => f.path),
        unstaged: overview.files.filter((f) => f.unstaged || f.untracked).map((f) => f.path)
      }
    },
    diff: async ({ path, staged }) => {
      await requireOverview()
      const result = await getGitDiff({ workspaceId, path, staged })
      return { diff: result.text, binary: result.binary, truncated: result.truncated }
    },
    log: async ({ limit }) => {
      await requireOverview()
      const commits = await listGitCommits({ workspaceId, limit })
      return { commits: commits.map((c) => ({ hash: c.hash, subject: c.subject, author: c.author, at: c.timestamp })) }
    },
    branches: async () => {
      await requireOverview()
      const branches = await listGitBranches({ workspaceId })
      return { current: branches.find((b) => b.current)?.name ?? '', branches: branches.map((b) => b.name) }
    },
    stage: async (paths) => {
      await requireOverview()
      await stageGitPaths({ workspaceId, paths })
    },
    commit: async (message) => {
      await requireOverview()
      const commit = await commitGit({ workspaceId, message })
      return { hash: commit.hash }
    },
    createBranch: async (name, checkout) => {
      await requireOverview()
      await createGitBranch({ workspaceId, name, checkout })
    },
    checkout: async (branch) => {
      await requireOverview()
      await checkoutGitBranch({ workspaceId, branch })
    }
  }
}

/**
 * 主题变了,播给订阅过的插件。
 *
 * ★ 调用点有两个 —— `ipc/app.ts` 的 `registerThemeBridge`(跟随系统时系统切换)
 * 和 `ipc/settings.ts`(用户手动改档位)。两处都已经在 `emitToAll('theme:changed')`,
 * 这一行紧跟其后。**漏掉任意一处**的表现都是「某一种切换方式下插件不跟随」,
 * 而用户不会意识到这两条是不同的路径。
 *
 * 插件系统没起来(manager 为 null)时什么都不做 —— 主题切换不该因此报错。
 */
export function notifyPluginsThemeChanged(appearance: ResolvedTheme): void {
  manager?.notifyAppearanceChanged(appearance)
}

/** catalog 里这个插件的投影。插件系统没起来时 catalog 是空的,于是这里拿到 `undefined`。 */
function catalogPlugin(pluginId: string): InstalledPlugin | undefined {
  return manager?.catalog().plugins.find((item) => item.id === pluginId)
}

/**
 * provider 建立之前的启动门 —— 判据与理由写在 `plugin-engines.ts` 的文件头。
 *
 * 返回拒的理由串(`null` = 放行)。**没有插件系统 / 插件不在表里也返回理由**,不返回
 * 放行:这里少判一种情况,`ensure()` 就会去建一个不该存在的 provider。
 */
function providerRefusal(providerId: string): string | null {
  const parts = splitProviderId(providerId)
  if (parts === null) return `invalid engine id: ${providerId}`
  const plugin = catalogPlugin(parts.pluginId)
  if (plugin === undefined) return `plugin ${parts.pluginId} is not installed`
  // 同一句判据也写在 `lookupPlugin` 里(那一处回答「能不能当引擎用」)。两处都要:少了
  // 这里的,`ensureProvider` 会在插件已被停用时放行;少了那一处的,登记表会持有一个
  // 已经不可用的插件。
  if (!isRunnable(plugin)) return `plugin ${parts.pluginId} is not enabled`
  return engineContributionRefusal(plugin, parts.engineId, process.platform, process.arch)
}

/**
 * helper **真正 spawn 之前**的那一次现查 —— 按实际入口路径收窄,理由同上。
 *
 * ★ 不是 provider 建立时那一次的重复:provider 是缓存的,而插件可能在缓存建立之后被
 * 禁用 / 撤权(`revokePermissions` 只对必选能力停用插件)/ 升级替换(见
 * `plugin-engines.ts` 文件头第 2 条)。
 */
function helperRefusal(providerId: string, entry: string): string | null {
  const parts = splitProviderId(providerId)
  if (parts === null) return `invalid engine id: ${providerId}`
  const plugin = catalogPlugin(parts.pluginId)
  if (plugin === undefined) return `plugin ${parts.pluginId} is not installed`
  return helperSpawnRefusal(plugin, entry)
}

export async function startPlugins(): Promise<void> {
  if (manager !== null) return
  const host = getHost()
  /*
    ★ 视图运行时的位置要在**起任何插件之前**告诉协议层:一个 `onStartup` 的
    插件可能在下一行就打开它的视图,而那时 import map 已经发出去了 ——
    晚注入的症状是那一次打开的视图报 `Failed to resolve module specifier "react"`,
    重开一次却好了,典型的「偶现」。
  */
  setPluginRuntimeDir(
    pluginRuntimeDir({ packaged: app.isPackaged, resourcesPath: process.resourcesPath, appPath: app.getAppPath() })
  )
  const runtime = new ElectronPluginRuntime(
    (pluginId, request) => {
      const current = manager
      if (current === null) {
        return Promise.resolve({ id: request.id, ok: false as const, error: { code: 'internal_error' as const, message: 'plugin system is not running' } })
      }
      return current.handleRequest(pluginId, request)
    },
    pluginPreloadPath({ packaged: app.isPackaged, resourcesPath: process.resourcesPath, appPath: app.getAppPath() })
  )

  /*
    需求：插件 API 与原生 helper 共用同一份会话表，不能各自创建一份 DocumentSessionManager。
    私有目录放在当前账户的数据树下；lookup 用惰性闭包查最新 catalog，禁用/升级后不会
    继续把旧 provider 当作可用引擎。
  */
  const documentRoot = join(host.paths.userData(), 'document-engine')
  const sessions = new DocumentSessionManager({
    privateDir: join(documentRoot, 'sessions'),
    // 编辑器画布要知道 Agent 改了、存了、崩了(定向推送,见 `./document-engine.ts`)
    onChange: (snapshot) => { notifyDocumentViewsChanged(snapshot) }
  })
  documentSessions = sessions
  const providers = new DocumentEngineProviderRegistry({
    sessions,
    workRoot: join(documentRoot, 'helpers'),
    platform: process.platform,
    arch: process.arch,
    lookupPlugin: (pluginId) => {
      const plugin = manager?.catalog().plugins.find((item) => item.id === pluginId)
      if (plugin === undefined || !isRunnable(plugin)) return null
      return { pluginId: plugin.id, root: plugin.path, manifest: plugin.manifest }
    },
    /*
      ★ 生产环境的 provider 换掉登记表里那份默认实现,**只为了在 `resolveEntry` 外面多
      包一层启动门**:`provider` 是缓存的,而插件可能在它建立之后被禁用、被撤权或被升级,
      真正起进程的时刻是 `NativeDocumentEngineProvider.open` —— 那是唯一拿得到「这次
      到底要跑哪个文件」的地方(见 `plugin-engines.ts` 文件头第 2 条)。

      ★ 其余字段与登记表的默认实现**保持同一形状**(默认实现见
      `document-engine/provider-registry.ts` 的构造函数)。这里不是自定义 provider 的地方:
      要改引擎行为应该改那边,而不是让两份实现慢慢分叉。
    */
    createProvider: (options) => new NativeDocumentEngineProvider({
      id: options.id,
      formats: options.formats,
      workRoot: options.workRoot,
      ...(options.args === undefined ? {} : { args: options.args }),
      resolveEntry: async () => {
        const entry = await options.resolveEntry()
        const refusal = helperRefusal(options.id, entry)
        if (refusal !== null) {
          host.logger.warn(`[documents] refused to launch ${options.id}: ${refusal}`)
          throw new NativeComponentError(`document engine helper may not be launched: ${refusal}`)
        }
        const owner = splitProviderId(options.id)?.pluginId
        if (owner === undefined || await manager?.approveNativeHelper(owner, entry) !== true) {
          throw new NativeComponentError('document engine helper execution was declined')
        }
        // 审批期间可能撤权或替换入口，真正 spawn 前再核一次身份和文件摘要。
        const verified = await options.resolveEntry()
        if (verified !== entry || helperRefusal(options.id, verified) !== null) throw new NativeComponentError('document engine changed during approval')
        return verified
      }
    })
  })
  const documents = new PluginDocuments({
    sessions,
    engineVersion: (id) => catalogPlugin(id)?.manifest.version ?? null,
    ensureProvider: (id) => {
      /*
        ★ provider 建立之前就要过启动门。放在这里而不是 `lookupPlugin` 里,是因为这一层
        知道**请求的是哪一个引擎贡献**(providerId 里带着 engineId),判定能精确到那一条
        贡献。拒掉的效果:`PluginDocuments.open` 会继续试下一个候选引擎,一个都没有时
        报 `engine_unavailable` —— 而不是把 helper 起起来再失败。
      */
      const refusal = providerRefusal(id)
      if (refusal !== null) {
        host.logger.warn(`[documents] engine ${id} is not usable: ${refusal}`)
        return null
      }
      return providers.ensure(id)
    },
    /*
      ★ **只 retire 这个插件自己**的引擎(providerId 前缀是它的 id)。

      这里以前把 `manifest.dependencies` 里的插件也一并 retire 了 —— 而依赖的语义是
      「我用了它」,不是「我拥有它」。办公插件族(writer / sheets / slides)共用同一个
      `ncw.office-runtime`,于是禁用其中一个会把**另外两个**正在编辑的文档会话连着
      helper 一起收掉;症状是「禁用一个插件,别的插件里开着的表格全没了」,而那两个
      插件之间看起来毫无关系。

      这个插件自己的 lease / 视图由 `releasePlugin` 收(`document-rpc.ts`);引擎插件
      自身的 disable / uninstall 会走它自己的 `retireEngines`,不需要邻居代劳。
    */
    retireEngines: async (id) => { await providers.retire(id) },
    accountScope: () => currentConfigScope()
  })
  /*
    编辑器画布的通道。★ 与插件 RPC 用**同一个** `documents` / `sessions`:画布、插件逻辑、
    Agent 工具改的是同一个活动模型,换一份实例就是两个模型。
  */
  setDocumentViewChannel(new DocumentViewChannel({
    documents,
    sessions,
    accountScope: () => currentConfigScope(),
    lookupPlugin: (pluginId) => {
      const plugin = catalogPlugin(pluginId)
      return plugin !== undefined && isRunnable(plugin) ? plugin.manifest : null
    },
    lookupWorkspaceRoot: (workspaceId) => {
      // 与插件的 resolveWorkspace 同一口径:远程工作区的路径在本机不存在
      const workspace = store.listWorkspaces().find((item) => item.id === workspaceId)
      if (workspace === undefined || (workspace.environment?.kind ?? 'local') === 'connection') return null
      return workspace.rootPath
    }
  }))
  documentLeaseTimer = setInterval(() => { void documents.sweepIdle().catch((error: unknown) => { host.logger.warn(`[documents] idle lease cleanup failed: ${String(error)}`) }) }, 60_000)
  documentLeaseTimer.unref?.()

  manager = new PluginManager({
    host,
    runtime,
    documents,
    resolveWorkspace: (id) => {
      const workspace = store.listWorkspaces().find((item) => item.id === id)
      if (workspace === undefined || (workspace.environment?.kind ?? 'local') === 'connection') return null
      return { id: workspace.id, rootPath: workspace.rootPath }
    },
    pluginRoot: join(host.paths.userData(), PLUGINS_DIR),
    hostVersion: app.getVersion(),
    getKv: (key, fallback) => store.getKv(key, fallback),
    setKv: (key, value) => { store.setKv(key, value) },
    currentAppearance: () => pluginAppearance(),
    currentWorkspace: () => {
      /*
        ★ 「当前工作区」优先取**渲染层上报的那一个**(`workspace:setActive`,
        按窗口记在 `windows` 里)。插件的路径类能力全部以它为根 —— 没有工作区时
        是空串,而 `narrowWorkspacePath` 对空根一律拒绝。这比回落到临时目录
        诚实:插件宁可一步都走不动,也不要往一个谁也不会看的目录里写东西
        然后报告「已完成」(同 `runtime.ts` 的 `workspaceRootFor`)。

        ★★ **以前这里取的是 `listWorkspaces()[0]`** —— 按 last_opened_at 倒序的
        第一条。那个时间戳记的是「何时最后一次**打开**」,在已经开着的几个工作区
        之间切 Tab 完全不动它。于是用户停在 B、点「新建绘图」,文件落进了 A。
        上报值拿不到时(窗口还没握手完、或者这个窗口一个工作区都没开)才退回
        那个近似值 —— 它至少不会是个随机答案。

        ★★★ **远程工作区(environment.kind === 'connection')一律排除**,
        空根交给 narrow 去拒。插件宿主的 fs 是**本地** node fs,远程工作区的
        rootPath(比如 SSH 机器上的 /root)在本机不存在 —— 不排除的话,
        写入会在「往上找存在祖先」时一路爬到文件系统根,报一句指向不了
        任何东西的 `path cannot be resolved`。而更糟的另一条路 —— 静默把文件
        写进**另一个**本地工作区 —— 绝对不能发生:用户看着 A 工作区点的新建,
        文件却出现在 B 里。所以上报值指向远程工作区时**也返回空根**,
        而不是接着往下找一个本地的顶上。

        远程工作区里插件暂时一步都走不动,这是**当前能力边界**,不是 bug;
        等插件 fs 支持远程连接时,把这段判断去掉即可。
      */
      const isLocal = (w: { environment?: { kind?: string } }): boolean =>
        (w.environment?.kind ?? 'local') !== 'connection'
      const all = store.listWorkspaces()
      const reported = windows.activeWorkspaceOfFocused()
      if (reported !== undefined) {
        const active = all.find((w) => w.id === reported)
        if (active !== undefined) {
          return isLocal(active) ? { id: active.id, rootPath: active.rootPath } : { id: '', rootPath: '' }
        }
      }
      const workspace = all.find(isLocal)
      return { id: workspace?.id ?? '', rootPath: workspace?.rootPath ?? '' }
    },
    approve: async (pluginId, summary) => {
      /*
        ★ **只剩跑命令还走这里。** 写文件 / 删文件原本也逐次弹这个框,
        而那一问是冗余的:调用走到 `rpc.ts` 之前,`manager.ts` 的能力门
        已经查过 `workspace.write` 在不在该插件的 `granted` 集合里,
        那个集合是用户在插件详情页显式批过、并落盘在 kv `plugins.state`
        里的。同一个问题问两遍,表现就是「每新建一张白板都要批一次」。

        `exec` 留着,因为它是另一个风险量级 —— 而且它此前一直被
        `allowedCommands: []` 堵死在参数门,这一版才刚刚变得可达。

        这个框仍然是**占位实现**:它拼的是裸英文句子,而本仓约定主进程
        只传 l10n key。真正的修法是接进渲染层的 `InteractionGate`、
        复用「以后都允许」那套规则(计划 P2),不在这一版里。
      */
      const { response } = await dialog.showMessageBox({
        type: 'question',
        buttons: ['Allow', 'Deny'],
        defaultId: 1,
        cancelId: 1,
        message: `Plugin ${pluginId} wants to run a command`,
        detail: summary.detail
      })
      return response === 0
    },
    /*
      ★ 插件删文件的唯一落点。**失败不降级为永久删除** —— `rpc.ts` 那边
      拿到抛错就把整条调用拒掉。撤掉逐次确认框之后,可恢复性只剩这一层。
    */
    trash: async (absolutePath) => { await shell.trashItem(absolutePath) },
    /*
      ★ URL 门在 `rpc.ts` 的 `normalizeExternalUrl`(只放行 https),这里只负责落地。
      两件事分开是因为门要能脱开 electron 直测,而 `shell.openExternal` 不能。
    */
    openExternal: async (url) => { await shell.openExternal(url) },
    clipboard: {
      readText: () => clipboard.readText(),
      writeText: (text) => clipboard.writeText(text)
    },
    scmFor: (workspaceId) => scmAdapterFor(workspaceId),
    emitChanged: () => { windows.emitToAll('plugins:changed', undefined) },
    publishMessages: (pluginId, locale, dict) => {
      /*
        ★ 同一个「插件 + 语言」**替换**而不是追加。插件每次重新装载都会再报一遍
        自己的 bundle(启用 / 禁用 / 重装 / 升级各一次),追加的话这个数组会一直
        长,而 `listPlugins` 每次都要遍历它。
      */
      const existing = pendingMessages.findIndex((item) => item.pluginId === pluginId && item.locale === locale)
      if (existing === -1) pendingMessages.push({ pluginId, locale, dict })
      else pendingMessages[existing] = { pluginId, locale, dict }
      windows.emitToAll('plugins:changed', undefined)
    },
    requestPermissions: async (pluginId, permissions, reasonKey) => {
      /*
        ★ 运行期申请。`reasonKey` 是**l10n key**,不是文案 —— 主进程新增代码
        一律只传 key(计划 §8.3)。这里暂时把 key 原样显示在系统对话框上;
        接进渲染层的授权面板之后它会被 `t()` 渲染成人话。
      */
      const { response } = await dialog.showMessageBox({
        type: 'question',
        buttons: ['Allow', 'Deny'],
        defaultId: 1,
        cancelId: 1,
        message: `Plugin ${pluginId} is asking for: ${permissions.join(', ')}`,
        detail: reasonKey
      })
      return response === 0
    },
    showMessage: (pluginId, kind, messageKey, params) => {
      windows.emitToAll('plugins:message', { pluginId, kind, messageKey, params })
    },
    /*
      ★ 只广播,**不在这里开 Tab**。Tab 是渲染层的概念(放哪一格、哪个 dock
      group、同一个文件已经开着时要不要复用),主进程要开的话就得把那整套布局
      规则抄一份 —— 而抄出来的那份一定会和 `stores/tabs.ts` 分叉。
    */
    openCustomEditor: (pluginId, viewType, path) => {
      windows.emitToAll('plugins:openCustomEditor', { pluginId, viewType, path })
    },
    // 网页 / 视图 Tab 同理:只广播,落点由 `stores/tabs.ts` 决定。
    openTab: (pluginId, target) => {
      windows.emitToAll('plugins:openTab', { pluginId, target })
    },
    /*
      `tabs.openTerminal` 的落地。清单级参数门(allowedCommands / env 体量)
      已经在 manager 那边过了,这里只剩要碰工作区存储与 TerminalHost 的三件事:

      1. 工作区必须存在 —— 不存在按 `declined` 拒,插件拿不到一个模糊的失败;
      2. ★ **远程工作区一律拒**(`reason: 'remote'`):env 无法直接注入远程 pty,
         而行内 export 的方案会把 API Key 回显进终端回滚缓冲。这是一期能力
         边界,不是 bug;
      3. 先备好一次性启动 spec(terminalId 由主进程铸),再广播开 Tab ——
         顺序反了的话,渲染层 create 会命中一个不存在的 spec,起一个裸 shell。

      spec 是**一次性**的:渲染层带着同一个 terminalId 来 create 时消费掉,
      复用会话重连不重放。见 `terminal-host.ts` 的 launchSpecs。
    */
    launchTerminal: (pluginId, spec) => {
      const workspace = store.listWorkspaces().find((w) => w.id === spec.workspaceId)
      if (workspace === undefined) return { opened: false, reason: 'declined' }
      if ((workspace.environment?.kind ?? 'local') === 'connection') return { opened: false, reason: 'remote' }
      const terminalId = randomUUID()
      terminalHost.setLaunchSpec(terminalId, {
        workspaceId: spec.workspaceId,
        env: spec.env,
        argv: [spec.command, ...spec.args]
      })
      windows.emitToAll('plugins:openTab', {
        pluginId,
        target: {
          kind: 'terminal',
          terminalId,
          workspaceId: spec.workspaceId,
          ...(spec.title === undefined ? {} : { title: spec.title })
        }
      })
      return { opened: true }
    },
    requestInteraction: (pluginId, request) => askRenderer(pluginId, request),
    emitProgress: (pluginId, progress) => {
      windows.emitToAll('plugins:progress', { pluginId, ...progress })
    },
    onToolsChanged: () => {
      /*
        插件工具变了 —— 下一次装配会重新问一遍 provider(见 `runtime.ts` 的
        `registerToolProvider`),所以这里只需要让界面知道。
      */
      windows.emitToAll('plugins:changed', undefined)
    },
    /*
      ★ catalog 投影用同一个进程内 namer 单例 `getTools()` 预留 externalName ——
      插件工具真正 register 也走它(经 `installPluginToolProvider`),记忆化保证同名。
    */
    reserveName: (internalId) => getTools().reserveName(internalId),
    unpublishMessages: (pluginId) => {
      for (let i = pendingMessages.length - 1; i >= 0; i -= 1) {
        if (pendingMessages[i]?.pluginId === pluginId) pendingMessages.splice(i, 1)
      }
      windows.emitToAll('plugins:changed', undefined)
    }
  })

  await manager.start()

  /*
    声明了 `onStartup` 的已启用插件,此刻唤醒。

    ★ **只唤醒声明了的**,不是全部:`activationEvents` 是插件自己说「我什么时候
      需要活」。全量唤醒等于把这套机制整个作废,每个装了的插件开机就各起一个
      隐藏窗口 —— 用户只是装了它,没让它常驻。

    ★ **即发即忘**:启动不该被任何一个插件卡住。失败的 wake 已经把原因写进
      了 `record.diagnostics`,插件详情页能看到;这里不再打扰启动流程。
  */
  for (const plugin of manager.catalog().plugins) {
    if (!plugin.enabled) continue
    if (!plugin.manifest.activationEvents.includes('onStartup')) continue
    void manager.wake(plugin.id).catch(() => undefined)
  }

  /*
    ★ 两条接线都**在 start 之后**:装载期可能已经发现了坏包,而那些插件
      不该出现在工具表里。provider 每次装配现问一遍,所以这里只接一次。
  */
  installPluginToolProvider(() => manager?.contributedTools() ?? [])
  installPluginToolPreparation(() => manager?.prepareContributedTools() ?? Promise.resolve())
  installPluginInterceptor((input) => manager?.intercept(input) ?? Promise.resolve({}))
  installPluginContextProvider((input) => manager?.provideContext(input) ?? Promise.resolve(''))
  /*
    插件自带的 Skill:把「现在有哪几个包内 skill 目录」这个问题接给管理器。

    ★ 和上面三条同一个形状 —— 注册一个**每次现问**的函数,而不是把当前值推过去。
    推值的话,用户启用/禁用插件之后 skill 目录不会跟着变,而那件事没有任何
    地方会提示他(症状:禁用了插件,模型下一轮还在用它的 skill)。

    ★ 也是为了断环:内核的扫描器不能 import 这一层(`ipc/plugins` →
    `plugin/manager` → `kernel/skill/load` → `ipc/plugins`)。环在 ESM 里不报错,
    只会让某一方在初始化时拿到 undefined。
  */
  setPluginSkillRootsProvider(() => manager?.contributedSkillRoots() ?? [])
  /*
    ★ 工作区变更的**事实来源**接在这里:Agent 每次写盘都过
    `kernel/tool/builtin/change-recorder.ts`,那是它唯一的收口。

    它不是文件系统 watcher,覆盖范围写在 `workspace.subscribeChanges` 的协议
    注释里 —— 外部编辑器、git checkout 改的文件不会到这儿。工作区外的写入
    直接丢:插件的路径视角只有工作区相对路径,给它一条区外路径它无从理解。
  */
  setFileChangeListener((change) => {
    if (!change.inWorkspace) return
    manager?.notifyWorkspaceChanged([{ path: change.relPath, kind: change.kind }])
  })
}

/**
 * 插件文案的暂存区。
 *
 * ★ 为什么不直接推给渲染层:`plugins:list` 的响应里已经带着每个插件的清单,
 * 而文案要跟着 catalog 一起到达 —— 分两条路送的话,菜单会先以 key 的样子
 * 出现一帧,再变成文字。所以这里攒着,由 `listPlugins` 一次带出去。
 *
 * ★★ 这段曾经是**死代码**:数组只进不出(`drainPluginMessages` 全仓无人调用),
 * 于是包里的 l10n 永远到不了渲染层,菜单一直显示 `excalidraw.new` 这种
 * 命令 id。修的时候注意:**读的时候不能清空** —— `plugins:list` 会被反复调用
 * (每次 catalog 变动渲染层都重取一次),清掉之后第二次就只剩 key 了。
 */
const pendingMessages: { pluginId: string; locale: string; dict: Record<string, string> }[] = []

/**
 * 向渲染层发起一次交互,等用户回答。
 *
 * ## 为什么不用 `dialog.showMessageBox`
 *
 * 系统对话框拼的是**裸文本**,既不跟随主题也不跟随语言(本文件里那两个遗留的
 * `approve` / `requestPermissions` 框就是这个样子,注释里记着「计划 P2 接进
 * 渲染层」)。插件的每一句话都是 l10n key,只有渲染层 `t()` 得出来。
 *
 * ## 超时与取消
 *
 * ★ 没有窗口、或者 `PLUGIN_INTERACTION_TIMEOUT_MS` 内没人回 → resolve 成
 * **取消值**,不是 reject:用户没理会一个弹窗不是故障,而一个 reject 会在插件
 * 那边变成一条它无从处理的异常。超时也必须有 —— 没有的话,窗口关掉的那一刻
 * 这个 Promise 会永远挂着,而它背后可能是一个 `interactive` 工具调用。
 */
function askRenderer(pluginId: string, request: PluginInteractionRequest): Promise<unknown> {
  const requestId = `${pluginId}:${String(nextInteractionId++)}`
  const cancelled = request.kind === 'confirm' ? false : null
  return new Promise<unknown>((resolve) => {
    const finish = (value: unknown): void => {
      if (!pendingInteractions.delete(requestId)) return
      clearTimeout(timer)
      resolve(value)
    }
    const timer = setTimeout(() => { finish(cancelled) }, PLUGIN_INTERACTION_TIMEOUT_MS)
    timer.unref?.()
    pendingInteractions.set(requestId, finish)
    windows.emitToAll('plugins:interaction', { requestId, pluginId, request })
  })
}

/** 等着用户回答的那几条。`requestId → 收下回执的函数`。 */
const pendingInteractions = new Map<string, (value: unknown) => void>()
let nextInteractionId = 1

/**
 * 一次交互最多等多久。
 *
 * ★ 比 `PLUGIN_TIMEOUT.REQUEST_MS`(30s)长:那一条管的是「宿主替插件做一件事
 * 要多久」,而这一条在等**人**。30 秒读不完一句话再决定是常态。
 * 但它必须有上限 —— 交互式工具调用的硬闸是 5 分钟,这里取同一档。
 */
const PLUGIN_INTERACTION_TIMEOUT_MS = 5 * 60 * 1000

/**
 * 渲染层把用户的回答送回来。
 *
 * ★ 认不出的 requestId **静默丢弃**:窗口重载、插件已被禁用、超时之后才点 ——
 * 三种都是竞态,不是故障,报错只会在用户什么都没做错时弹一个红框。
 */
export function replyPluginInteraction(req: { requestId: string; value: unknown }): void {
  pendingInteractions.get(req.requestId)?.(req.value)
}

export async function shutdownPlugins(guarded = false): Promise<void> {
  // 需求：无外部预检的调用也必须先检查 dirty，失败时不能摘接线、丢掉仍可保存的 manager。
  if (!guarded) {
    await acquireDocumentGuard()
    guarded = manager !== null
  }
  if (documentLeaseTimer !== undefined) {
    clearInterval(documentLeaseTimer)
    documentLeaseTimer = undefined
  }
  // 画布通道随会话表一起下线:之后的画布请求报 engine_unavailable,而不是打到一个正在收尾的会话表上
  setDocumentViewChannel(null)
  /*
    ★ 挂起的交互全部按取消收口。不收的话,这些 Promise 会连同它们背后的
    插件调用一起永远挂着 —— 而应用正在退出,没有任何人会再来回答。
  */
  for (const finish of [...pendingInteractions.values()]) finish(null)
  pendingInteractions.clear()
  // 变更通知的接线也要摘掉,理由同下面那两条:收尾期间的一次写盘不该再去问一个正在销毁的 manager。
  setFileChangeListener(null)
  /*
    ★ 先摘接线再关。反过来的话,收尾期间进来的一次工具装配会问一个
    正在销毁的 manager —— 而那次失败会以「工具不存在」的名义出现在转录里。
  */
  installPluginInterceptor(null)
  installPluginContextProvider(null)
  /*
    ★ skill 的接线也摘掉,同一条理由。留着的话,退出期间一次 `listSkills`
    (设置页还开着就可能发生)会去问一个正在销毁的 manager。
    摘掉之后 provider 回落成「没有插件 skill」—— 那在这一刻正是事实。
  */
  setPluginSkillRootsProvider(null)
  const current = manager
  try {
    /*
      ★ `guarded` 由调用方给,判据只有一条:它**已经**自己拿过
      `acquireDocumentGuard()` 了(退出 / 切账户那两条路都是)。
      不能在这里重新拿一次:拿过之后 `manager.guardDocumentClose()` 会直接抛,而它抛在
      哪个 allSettled 里,插件与 helper 就一起收不掉了(理由见 `acquireDocumentGuard`)。
    */
    await current?.shutdown(guarded)
  } finally {
    manager = null
    /*
      需求:插件系统关掉之后不能再留下任何 helper 进程 —— 登记表只 retire「有主人的
      provider」,而 provider 被升级替换、会话视图已经关掉的那些干净会话没有 lease,
      谁也轮不到它们去收。切账户尤其致命:那些 helper 的私有工作目录属于**上一个账户**。

      ★ **只收干净会话。** 脏会话一律留着:宁可多留一个进程,也绝不用 `force` 去覆盖
      用户没保存的文档(`closeAll` 的 `onlyClean` 就是为这条存在的)。
      包在 `try/finally` 里是因为上面那一步可能抛(会话收尾失败):抛出去之后这几条
      干净会话仍然必须被收掉,而原来的错误不能被这个收尾覆盖掉。
    */
    const sessions = documentSessions
    documentSessions = null
    if (sessions !== null) {
      await sessions.closeAll(undefined, { onlyClean: true }).catch((error: unknown) => {
        console.warn(`[documents] closing leftover document sessions failed: ${String(error)}`)
      })
    }
  }
}

/**
 * 退出 / 切账户之前的**文档安全预检**。
 *
 * 需求:在**关窗与切库之前**就问一次「现在能不能收文档会话」—— 它做三件事:挡住新的
 * `documents.*` 调用、把在途请求排空、并且在还有未保存内容时**拒绝**。拿到返回值 =
 * 三步都过了。
 *
 * ★ 为什么必须在这里,而不是等收尾时各层自己 assert:收尾那一层是
 * `QuitFlow` 的 `Promise.allSettled`(见 `quit-flow.ts` 的 `drainAsync`),而它**吞掉
 * 所有失败**。一个「脏 → assert 抛 → 被吞」的收尾,表现是窗口关完了、进程退了、
 * 用户的改动没了,而全程零报错 —— 这正是这条预检要堵的那一类。所以判据必须发生在
 * **不可逆的那一步(关窗)之前**,判负时由调用方作废整个退出。
 *
 * ★ 拿到之后必须收口:
 *   - 退出真的开始 → `shutdownPlugins(true)` 接手,闸门一直举到进程结束;
 *   - 退出被作废(渲染层顶住 unload)→ 调用方**必须调用返回的函数**解除。
 *     不解除的症状是:界面上什么都没变,但任何文档都打不开,直到重启。
 *
 * 返回 `null` = 没有插件系统(没起来过 / 已经关掉),也就没有任何文档会话 ——
 * 调用方按「无需闸门」继续。
 */
export async function acquireDocumentGuard(discardChanges = false): Promise<(() => void) | null> {
  const current = manager
  if (current === null) return null
  return await current.guardDocumentClose(discardChanges)
}

/**
 * 全部已装插件 + 它们各自的文案。
 *
 * ★ 文案**挂进 catalog**,不另开一条 IPC:菜单项在插件被激活之前就要画出来,
 * 而菜单本身就是激活事件的来源。分两条路送的话,菜单会先以 key 的样子出现
 * 一帧再变成文字。
 *
 * ★ 这里**不清空** `pendingMessages`:这个函数每次 catalog 变动都会被调一次
 * (渲染层收到 `plugins:changed` 就重取),清掉的话第二次返回的就没有文案了,
 * 菜单又变回一排 key。
 */
export function listPlugins(): PluginCatalog {
  const catalog = manager?.catalog() ?? emptyCatalog()
  if (pendingMessages.length === 0) return catalog

  const byPlugin = new Map<string, Record<string, Record<string, string>>>()
  for (const { pluginId, locale, dict } of pendingMessages) {
    const locales = byPlugin.get(pluginId) ?? {}
    locales[locale] = dict
    byPlugin.set(pluginId, locales)
  }

  return {
    ...catalog,
    plugins: catalog.plugins.map((plugin) => {
      const messages = byPlugin.get(plugin.id)
      return messages === undefined ? plugin : { ...plugin, messages }
    })
  }
}

export async function setPluginEnabled(req: { pluginId: string; enabled: boolean }): Promise<PluginCatalog> {
  await manager?.setEnabled(req.pluginId, req.enabled)
  return listPlugins()
}

export async function uninstallPlugin(req: { pluginId: string }): Promise<PluginCatalog> {
  await manager?.uninstall(req.pluginId)
  return listPlugins()
}

export async function pickPluginPackage(): Promise<{ path: string; name: string } | null> {
  const result = await dialog.showOpenDialog({
    // 目录与 ZIP 两条来源共用一个选择器 —— 作者在 `dev --watch` 时装目录,
    // 用户从市场之外装包时装 ZIP。
    properties: ['openFile', 'openDirectory'],
    filters: [{ name: 'Plugin package', extensions: ['zip'] }]
  })
  const path = result.filePaths[0]
  if (result.canceled || path === undefined) return null
  return { path, name: path.split(/[\\/]/).pop() ?? path }
}

export async function installPlugin(req: { path: string }): Promise<PluginCatalog> {
  if (manager === null) throw new IpcError('unknown', 'plugin system is not running')
  /*
    ★ key 里带上路径,不是一个固定的 `'local'`:多窗口、或者接连装两个
    本地包时,两条进度会挤在同一个 key 上互相覆盖 —— 表现是先装的那条
    进度条被后装的接管,看起来像"卡住之后忽然跳完了"。
  */
  const key = `local:${req.path}`
  // 本地包没有下载阶段。校验 + 解压通常是毫秒级,但大包解压得出来也要几秒,
  // 而在那几秒里右上角那颗按钮以前是完全没有反馈的。
  emitInstallProgress(key, 'installing')
  try {
    await manager.install(req.path)
  } catch (error) {
    emitInstallProgress(key, 'failed', { messageKey: 'plugins.installPackageFailed' })
    throw new IpcError('unknown', (error as Error).message)
  }
  const catalog = listPlugins()
  emitInstallProgress(key, 'done')
  return catalog
}

/**
 * 装一个插件走到哪一步了 —— 市场安装与本地安装共用。
 *
 * ★ 全局广播而不是定向推:装插件是全局副作用,别的窗口的市场页也该看到
 * 那颗按钮在跑。形状同 `provider-auth.ts` 的 `emitPhase()` —— 可选字段一律
 * 展开进去,不给 `undefined`(它穿过结构化克隆会变成「有这个键但没有值」)。
 */
export function emitInstallProgress(
  key: string,
  phase: 'preparing' | 'downloading' | 'installing' | 'done' | 'failed',
  extra: { received?: number; total?: number; messageKey?: string } = {}
): void {
  windows.emitToAll('plugins:installProgress', {
    key,
    phase,
    ...(extra.received === undefined ? {} : { received: extra.received }),
    ...(extra.total === undefined ? {} : { total: extra.total }),
    ...(extra.messageKey === undefined ? {} : { messageKey: extra.messageKey })
  })
}

export function grantPluginPermissions(req: { pluginId: string; permissions: PluginPermission[] }): PluginCatalog {
  manager?.grant(req.pluginId, req.permissions)
  return listPlugins()
}

export function revokePluginPermissions(req: { pluginId: string; permissions: PluginPermission[] }): PluginCatalog {
  manager?.revoke(req.pluginId, req.permissions)
  return listPlugins()
}

export function pluginActivity(req: { pluginId?: string }): PluginActivity[] {
  return recentActivity(req.pluginId)
}

export async function confirmPluginClose(req: { path?: string }): Promise<{ safe: boolean }> {
  // 插件系统没起来 = 没有插件编辑器 = 关起来是安全的。
  return { safe: manager === null ? true : await manager.saveBeforeClose(req) }
}

export function getPluginConfiguration(req: { pluginId: string }): Record<string, boolean | string | number> {
  return manager?.configuration(req.pluginId) ?? {}
}

export function setPluginConfiguration(req: {
  pluginId: string
  key: string
  value: boolean | string | number | null
}): Record<string, boolean | string | number> {
  manager?.setConfiguration(req.pluginId, req.key, req.value)
  return getPluginConfiguration(req)
}

export async function runPluginCommand(req: { pluginId: string; commandId: string; args?: Record<string, unknown> }): Promise<void> {
  if (manager === null) throw new IpcError('unknown', 'plugin system is not running')
  try {
    await manager.runCommand(req.pluginId, req.commandId, req.args)
  } catch (error) {
    throw new IpcError('unknown', (error as Error).message)
  }
}

/**
 * 用户点了侧边栏上某个网页应用的入口。
 *
 * ★ 为什么要绕一趟主进程,而不是渲染层拿着 catalog 里的 URL 直接开 Tab:
 *
 * 1. **激活事件**(`onWebApp:<id>`)只有主进程知道怎么发 —— 一个 webapp 插件
 *    可能同时带着 skills / themes,它们要在第一次打开时才装上去;
 * 2. 「这个插件此刻能不能用」(禁用 / 待批准 / 装载失败)的判定只有一处
 *    真源(`isRunnable`),让渲染层各判一次迟早会分叉。
 *
 * 校验与广播都在 `manager` 里(同 `tabs.openWebApp` RPC 那条路),这里只是
 * 把用户的点击翻译成同一次请求。
 */
export async function openPluginWebApp(req: { pluginId: string; webAppId: string }): Promise<void> {
  if (manager === null) throw new IpcError('unknown', 'plugin system is not running')
  const opened = await manager.openWebApp(req.pluginId, req.webAppId)
  if (!opened) throw new IpcError('unknown', `plugin ${req.pluginId} has no web app ${req.webAppId}`)
}

/**
 * 自定义编辑器视图报上来的脏标记,由宿主代发(视图 iframe 够不着插件那条 RPC)。
 *
 * ★ 不抛错:插件已经被禁用 / 卸载时这条到不了任何地方,而那是竞态不是故障 ——
 * 为它弹一个错误,等于在用户关掉插件之后再骂他一句。
 */
export function setPluginEditorDirty(req: {
  pluginId: string
  documentId: string
  path: string
  dirty: boolean
}): void {
  manager?.setEditorDirty(req.pluginId, req.documentId, req.path, req.dirty)
}

/**
 * 打开自定义编辑器 Tab 之前的唤醒(见 manager.activateCustomEditor 的注释)。
 * 返回而不是抛:唤醒失败走「降级态」而不是一次报错 —— 渲染层拿 false 就
 * 不去渲染注定 403 的 iframe。
 */
export async function activatePluginEditor(req: { pluginId: string; viewType: string }): Promise<{ activated: boolean }> {
  if (manager === null) return { activated: false }
  return { activated: await manager.activateCustomEditor(req.pluginId, req.viewType) }
}

/**
 * 用户点了实时工具卡片上的按钮。**即发即忘**:动作到不了(工具已结束 / callId
 * 不属于该插件)时静默丢弃 —— 一次没送到的点击不该弹错,那是竞态,不是故障。
 */
export async function deliverPluginCardAction(req: {
  pluginId: string
  callId: string
  actionId: string
  value?: unknown
}): Promise<void> {
  await manager?.deliverCardAction(req.pluginId, req.callId, req.actionId, req.value)
}
