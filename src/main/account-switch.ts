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
 * 2. **在改任何东西之前**确认没有正在跑的 run。一个正在跑的 run 手里攥着
 *    当前账户的 provider 配置与密钥引用,而它下一次取密钥是在**切完之后** ——
 *    那时读到的已经是另一个账户的了。这不是理论风险,是必然发生。
 *    ★ 不去「取消」它:假 cancel 会让它带着上一个账户的密钥继续跑完,
 *    而这正是要防的那件事。
 * 3. **拆掉进程内那堆**:先子进程,后连接,最后是缓存。
 * 4. **切库**(`switchConfigProfile`,一个 SQLite 事务)。
 * 5. **重新种**:空账户要有一套能用的默认值,否则首屏是一个空的应用。
 * 6. **广播**:不然窗口里残留的还是 A 的界面。
 */
import { ConfigSyncError } from '../shared/domain/config-sync'
import { setConfigCategoryDirty } from './db/repo'
import { runs } from './kernel/run-registry'
import { browserManager } from './browser/manager'
import { terminalHost } from './terminal-host'
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
  try {
    /*
      ★★ 判据是「还在跑的 run」,不是「有没有 run」。一个刚建好、还没发请求的 run
      同样会在切完之后去取密钥,而它拿到的会是新账户的。

      ★ 这里**故意**抛错而不是等它跑完。等 = 一次登录被一个长跑任务挂住,
      而用户看到的是「登录按钮没反应」;抛 = 一句能读的错误,他停下来再登一次。
    */
    if (runs.activeRunIds().length > 0) throw new ConfigSyncError('busy')

    // 旧账户的同步可能正等网络响应。先中断并等它退出,否则响应回来时当前
    // `physicalCredentialRef` 已经指向新账户,会把 A 的 key 写进 B。
    await stopConfigSyncAndWait()

    // ── 拆掉进程内那堆 ────────────────────────────────────────────────
    // 终端是连到远端机器的 shell;远端连接握着上一账户的凭据引用。
    terminalHost.shutdown()
    await shutdownEnvironments()
    // MCP 连接里跑着上一账户的 token,而且它们持有解密后密钥的副本。
    await refreshRuntimeForConfigScope()

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

    broadcastScopeChanged()
  } finally {
    // 需求：HTTP 在切换期间拒绝新请求；完成后采用当前账户的监听设置，不能沿用旧账户的远程开关。
    await gatewayReconciler?.()
    switchInFlight = false
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
  if (runs.activeRunIds().length > 0) throw new ConfigSyncError('busy')
  switchInFlight = true
  try {
    importLocalConfigProfile()
    await refreshRuntimeForConfigScope()
    browserManager.resetForConfigScopeChange()
    broadcastScopeChanged()
  } finally {
    // 需求：导入本地配置也会替换网关设置，监听范围必须与恢复后的设置一致。
    await gatewayReconciler?.()
    switchInFlight = false
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
  windows.emitToAll('provider:changed', { providers: store.listProviders(), models: store.listAliases() })
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
