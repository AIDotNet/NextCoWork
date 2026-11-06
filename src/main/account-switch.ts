/**
 * 换账户时的一次性收尾 —— **这是「切换」这件事唯一被允许发生的地方**。
 *
 * ## 为什么必须集中在一处
 *
 * 库里的配置分成两堆:`config_profiles` 那张快照表搬得动的(供应商、别名、MCP、
 * 设置、定时任务、白名单 kv),和**进程内、文件系统里、Chromium 里**搬不动的
 * (终端子进程、远端连接、MCP 连接、Skill 注册表缓存、浏览器 Tab、主题库)。
 *
 * 只做前一半的症状是「切了账户,但模型列表还是上一个账户的」—— 而且**它看起来
 * 是对的**,直到有人点进去用。所以这两半必须在同一个函数里挨着,
 * 谁漏了就在同一屏里看得出来。
 *
 * ## 顺序不是随意的
 *
 * 1. **串行化**:切换期间再来一次直接拒(`busy`)。两次切换交错会留下一个
 *    「归档了 A、恢复了一半 B」的库。
 * 2. **在改任何东西之前**确认没有正在跑的 run,以及没有未保存的文档会话(文档闸门)。
 *    一个正在跑的 run 手里攥着当前账户的 provider 配置与密钥引用,而它下一次取密钥是在
 *    **切完之后** —— 那时读到的已经是另一个账户的了。这不是理论风险,是必然发生。
 *    ★ 不去「取消」它:假 cancel 会让它带着上一个账户的密钥继续跑完,
 *    而这正是要防的那件事。
 *    ★ 文档闸门(`acquireDocumentGuard`)同样必须在**拆任何东西之前**拿:它在脏的时候
 *    直接拒掉这次切换,而那一步必须留下一个完全可用的应用,不能只留下半个被拆过的进程。
 * 3. **拆掉进程内那堆**:先子进程,后连接,最后是缓存。
 * 4. **停掉旧账户的插件系统**:它的 enabled / granted 存在当前账户的 kv,文档会话带着
 *    当前账户的作用域 —— 切库之后既保存不回去、也解释不通。
 * 5. **切库**(`switchConfigProfile`,一个 SQLite 事务)。
 * 6. **重新种**:空账户要有一套能用的默认值,否则首屏是一个空的应用。
 * 7. **拉起新账户的插件,再广播**:不然窗口里残留的还是 A 的界面与 A 的插件开关。
 */
import { ConfigSyncError } from '../shared/domain/config-sync'
import { setConfigCategoryDirty } from './db/repo'
import { runs } from './kernel/run-registry'
import { browserManager } from './browser/manager'
import { terminalHost } from './terminal-host'
import { listResolvedModels } from './state/model-bindings'
import { store } from './state/store'
import { windows } from './window/registry'
import {
  claimMigratedLocalWorkspaces,
  configScopeForAccount,
  currentConfigScope,
  importLocalConfigProfile,
  migrateLegacyLocalProvidersToCurrentAccount,
  switchConfigProfile
} from './db/config-profile'
import {
  getConfigSyncStatus,
  startConfigSync,
  stopConfigSync,
  stopConfigSyncAndWait
} from './ipc/config-sync'
import { listMcpStatuses, refreshRuntimeForConfigScope, shutdownEnvironments } from './runtime'
import { acquireDocumentGuard, pluginManager, shutdownPlugins, startPlugins } from './ipc/plugins'
import { broadcastThemeLibrary } from './ipc/theme'
import { listSearchProviders } from './ipc/websearch'

let switchInFlight = false
let gatewayReconciler: (() => Promise<void>) | undefined

/** 需求：账户模块不反向加载 HTTP/Agent IPC；应用装配时注入监听设置的刷新口。 */
export function setAccountGatewayReconciler(reconcile: () => Promise<void>): void {
  gatewayReconciler = reconcile
}

/** 切换正在进行中。IPC 层用它把「正在登录/正在切」的按钮按下去。 */
export function isAccountSwitchInFlight(): boolean {
  return switchInFlight
}

/**
 * 就地把当前作用域让给 `accountId`(`null` = 未登录那一份)。
 *
 * ★ 同账户的**配置切换**是恒等操作:刷新 token、换 Team、重读登录态都会走到这里,
 * 而那些都不该触发一次整表归档。唯一例外是启动迁移留下的待认领清单——它只会
 * 在首次命中时建立工作区副本，随后标记为完成，不能因此重新打开整表归档。
 *
 * ★ 抛的都是 `ConfigSyncError`,渲染层按 `code` 查 i18n ——
 * 这里不拼任何一句给用户看的中文。
 */
export async function prepareAccountSwitch(accountId: string | null): Promise<void> {
  const target = configScopeForAccount(accountId)
  if (target === currentConfigScope()) {
    // 需求：同账户启动恢复也必须完成迁移工作区重连；否则不会进下面的切库分支，
    // 表现为迁移完成后账户仍看不见会话。
    const relinked = accountId === null ? 0 : claimMigratedLocalWorkspaces(accountId)
    if (accountId !== null && relinked > 0) {
      setConfigCategoryDirty('workspaces', accountId, true)
      broadcastScopeChanged()
    }
    return
  }
  if (switchInFlight) throw new ConfigSyncError('busy')

  switchInFlight = true
  /**
   * 切库前拿到的文档闸门 —— 在这一层 `finally` 里还掉,见下面那段注释。
   * `null` = 没有插件系统可闸(没起来过 / 已经关掉)。
   */
  let releaseDocuments: (() => void) | null = null
  try {
    /*
      ★★ 判据是「还在跑的 run」,不是「有没有 run」。一个刚建好、还没发请求的 run
      同样会在切完之后去取密钥,而它拿到的会是新账户的。

      ★ 这里**故意**抛错而不是等它跑完。等 = 一次登录被一个长跑任务挂住,
      而用户看到的是「登录按钮没反应」;抛 = 一句能读的错误,他停下来再登一次。
    */
    if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new ConfigSyncError('busy')

    /*
      需求:文档会话必须在**切库之前**排空 —— 落在这一点上有两件事,缺一不可:

      1. **脏文档直接拒绝这次切换**(`ConfigSyncError('busy')` 给渲染层)。文档会话属于
         当前账户,切过去之后它们既保存不回原作用域,也没有合理的归属;而自动保存就是
         在没有用户点头的情况下改他的文件 —— 计划 §5 明写「不自动覆盖用户文档」。
         所以这里宁可让用户先保存再登,也不替他决定。
      2. 干净会话连同它的原生 helper 一起收掉。留着的话,那些 helper 的私有工作目录
         和打开的文档句柄还带着**上一个账户**的作用域,新账户打开同一个文件就成了两个
         session 抢一个文件。

      ★ 位置钉在 `stopConfigSyncAndWait()` 与 `terminalHost.shutdown()` **之前**:拒掉这次
      切换之后应用必须还是完整可用的(终端没被杀、MCP 连接没被拆),而上面那条 `runs`
      判据就是同一个道理。拿到闸门 = 三步都过了(挡新调用 / 排空 / 无脏会话)。

      ★ 抛出的原因可能是引擎层的任意错误(`DocumentEngineError` 等),而渲染层的错误面
      只认 `configSync.<code>`(见文件头),所以这里翻成 `busy`,真相留在日志里。
    */
    try {
      releaseDocuments = await acquireDocumentGuard()
    } catch (error) {
      console.warn(`[account] 还有未保存的文档,已取消这次账户切换:${String(error)}`)
      throw new ConfigSyncError('busy')
    }

    // 旧账户的同步可能正等网络响应。先中断并等它退出,否则响应回来时当前
    // `physicalCredentialRef` 已经指向新账户,会把 A 的 key 写进 B。
    await stopConfigSyncAndWait()

    // ── 拆掉进程内那堆 ────────────────────────────────────────────────
    // 终端是连到远端机器的 shell;远端连接握着上一账户的凭据引用。
    terminalHost.shutdown()
    await shutdownEnvironments()
    // MCP 连接里跑着上一账户的 token,而且它们持有解密后密钥的副本。
    await refreshRuntimeForConfigScope()

    /*
      账户作用域不止在库里:插件的 enabled / granted 存在当前账户的 kv,文档会话带着
      accountScope,plugins 根目录与 helper 工作目录也都由账户数据树派生。所以旧账户的
      插件系统必须在切库之前停掉,切完再按新账户的 kv 重新装载 —— 不重启的话,新账户
      第一帧看到的是上一个账户的插件开关,而 helper 还带着旧账户的私有目录活着。
    */
    const pluginsWereRunning = pluginManager() !== null
    if (pluginsWereRunning) {
      /*
        `guarded = true`:闸门刚在上面拿到并刚交给它。这里再让 manager 自己拿一次的话,
        它会直接抛,而插件与 helper 就一个都收不掉了。
      */
      await shutdownPlugins(true)
    }
    try {
      // ── 切库(配置表整表归档 + 恢复,一个事务) ────────────────────────
      switchConfigProfile(accountId)
      // 需求：启动数据整理先于开库，迁入会话会暂时挂在 local 工作区。此处只按撤销
      // 清单复制并重连本次迁入的行；不这样做，登录后的会话列表会空白且没有任何报错。
      const relinked = accountId === null ? 0 : claimMigratedLocalWorkspaces(accountId)
      if (accountId !== null && relinked > 0) setConfigCategoryDirty('workspaces', accountId, true)
      // 账户隔离上线前的 provider/model/key 都在 local。只归属给首个登录账户一次,
      // 不自动搬工作区或个性化设置;local 原件保留,后续账户不再重复复制。
      if (accountId !== null && migrateLegacyLocalProvidersToCurrentAccount()) {
        setConfigCategoryDirty('providers', accountId, true)
      }
      // 新作用域的默认供应商 / 默认工作区 —— 空账户的第一个画面不该是空的。
      await refreshRuntimeForConfigScope()

      // ── 进程外那两处 ─────────────────────────────────────────────────
      browserManager.resetForConfigScopeChange()
    } catch (error) {
      /*
        需求:切换失败**不能**把插件系统留在关掉的状态。那对用户来说比切换失败本身糟
        得多:插件页空着、文档引擎全不可用,而且没有任何东西告诉他重启一下就好。
        这里按当前(大概率仍是旧)作用域尽力拉回来,失败也不改变原来那个错误。
      */
      if (pluginsWereRunning) await startPlugins().catch(() => undefined)
      throw error
    }
    if (pluginsWereRunning) {
      await startPlugins()
      /*
        ★ 插件 catalog 是**账户作用域**的一份投影,而渲染层只在 `plugins:changed` 时
        重取(`stores/plugins.ts` 的 `load()`)。少了这一条,切完账户之后插件页显示的还是
        上一个账户的开关状态,直到用户手动重启。
      */
      windows.emitToAll('plugins:changed', undefined)
    }

    broadcastScopeChanged()
  } finally {
    // 闸门必须还掉:它是「挡住所有 documents.* 调用」,不还的话切完账户文档功能就废了
    // (旧 manager 已经关掉,新 manager 有自己的闸门状态,所以还的是上面那一个)。
    releaseDocuments?.()
    // 需求：HTTP 在切换期间拒绝新请求；完成后采用当前账户的监听设置，不能沿用旧账户的远程开关。
    try {
      await gatewayReconciler?.()
    } finally {
      switchInFlight = false
    }
  }
}

/**
 * 登录态落到「这个账户」之后要接上的同步。
 *
 * ★ 单独一个函数、而不是塞进 `prepareAccountSwitch`:那一个在没有账户
 * (退出登录)时也要能跑,而这个只在有用户时才有意义。
 */
export function startSyncForAccount(accountId: string | null): void {
  if (accountId === null) {
    stopConfigSync()
    return
  }
  startConfigSync(accountId)
}

/**
 * 显式把未登录那份配置导入当前账户。**只由设置页那一颗按钮触发。**
 *
 * 放在这里而不是 `ipc/` 里:它和切换共用同一套前置条件 —— 导入会整表改写
 * 当前作用域的配置,而一个正在跑的 run 会读到改到一半的那份。
 */
export async function importLocalConfigIntoCurrentAccount(): Promise<void> {
  if (switchInFlight) throw new ConfigSyncError('busy')
  if (runs.activeRunIds().length > 0 || runs.hasSessionOperations()) throw new ConfigSyncError('busy')
  switchInFlight = true
  try {
    importLocalConfigProfile()
    await refreshRuntimeForConfigScope()
    browserManager.resetForConfigScopeChange()
    broadcastScopeChanged()
  } finally {
    // 需求：导入本地配置也会替换网关设置，监听范围必须与恢复后的设置一致。
    try {
      await gatewayReconciler?.()
    } finally {
      switchInFlight = false
    }
  }
}

/**
 * 让每一个窗口手里的缓存作废。
 *
 * ★ 渲染层**没有**「重新读一遍全部配置」的入口:它靠这些事件逐块失效。
 * 少发一条的表现是那一块停在 A 账户的值上 —— 而它不会自己好,
 * 要等用户重启应用。
 */
function broadcastScopeChanged(): void {
  windows.emitToAll('settings:changed', store.getSettings())
  windows.emitToAll('provider:changed', { providers: store.listProviders(), models: listResolvedModels() })
  windows.emitToAll('modelCatalog:changed', { custom: store.listUserModelCatalog() })
  windows.emitToAll('workspace:changed', { workspaces: store.listWorkspaces() })
  // `reset` 而不是某个工作区:侧边栏与内层 Tab 全部重来。
  windows.emitToAll('sessions:changed', { kind: 'reset' })
  windows.emitToAll('mcp:changed', { servers: mcpStatusesSafe() })
  windows.emitToAll('connection:changed', undefined)
  windows.emitToAll('skills:changed', undefined)
  windows.emitToAll('commands:changed', undefined)
  windows.emitToAll('agents:changed', undefined)
  windows.emitToAll('hooks:changed', undefined)
  windows.emitToAll('scheduled:changed', { kind: 'task' })
  windows.emitToAll('browser:profilesChanged', browserManager.listProfiles())
  broadcastThemeLibrary()
  windows.emitToAll('configSync:changed', getConfigSyncStatus())
  // 搜索那一条要现读密钥才能给出「哪几家配好了」,所以它在最后且不挡前面的广播。
  void listSearchProviders()
    .then((providers) => windows.emitToAll('websearch:changed', { providers }))
    .catch(() => undefined)
}

/** 运行时还没建起来时不要为了读状态把它拉起来。 */
function mcpStatusesSafe(): ReturnType<typeof listMcpStatuses> {
  try {
    return listMcpStatuses()
  } catch {
    return []
  }
}
