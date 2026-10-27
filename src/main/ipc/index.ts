/**
 * IPC 路由 —— 契约的主进程一侧。
 *
 * 两条编译期保证:
 * 1. `handlers: HandlerMap` 是**完备映射**。往 IpcInvokeMap 加一个频道却忘了写 handler,
 *    `npm run typecheck` 当场拦下 —— 而不是等到运行时收到一句
 *    「No handler registered for 'x'」。
 * 2. handler 的入参/返回类型直接由频道推出,不需要在两侧各写一遍。
 *
 * 一条运行期保证:**异常绝不穿过 ipcMain.handle**(方案 §3 规则 3)。
 */
import { ipcMain } from 'electron'
import type {
  InvokeChannel,
  InvokeReq,
  InvokeRes,
  IpcResult,
  IpcSendMap,
  SendChannel
} from '../../shared/ipc/contract'
import { INVOKE_CHANNELS, SEND_CHANNELS } from '../../shared/ipc/contract'
import {
  checkPluginUpdates,
  installMarketPlugin,
  listMarketPluginCategories,
  listMarketPlugins,
  marketPluginDetail,
  updateAllPlugins
} from './plugin-market'
import {
  activatePluginEditor,
  confirmPluginClose,
  getPluginConfiguration,
  grantPluginPermissions,
  installPlugin,
  listPlugins,
  pickPluginPackage,
  pluginActivity,
  revokePluginPermissions,
  runPluginCommand,
  deliverPluginCardAction,
  openPluginWebApp,
  replyPluginInteraction,
  setPluginEditorDirty,
  setPluginConfiguration,
  setPluginEnabled,
  uninstallPlugin
} from './plugins'
import type { SessionInputState } from '../../shared/domain/queued-input'
import { isValidSessionInput } from '../../shared/domain/queued-input'
import {
  EMPTY_OUTER,
  innerTabKey,
  outerTabKey,
  sessionInputKey,
  store
} from '../state/store'
import { windows, type WindowContext } from '../window/registry'
import { runScheduledTaskNow } from '../scheduled/scheduler'
import { scheduledWrites } from '../scheduled/bridge'
import { applyWindowControl, pushMaximized } from '../window/title-bar'
import { shutdownTerminals, terminalHost } from '../terminal-host'
import { checkForUpdates, copyText, getBootstrap, openExternal, openSessionWindow, registerThemeBridge, requestQuit, saveTextFile } from './app'
import {
  cancelWorkspaceUpload,
  completeWorkspaceUpload,
  listSessionAttachments,
  pickAttachments,
  prepareWorkspaceUpload,
  removeAttachment,
  uploadAttachment
} from './attachment'
import { abortRun, attachRun, broadcastActiveRuns, interjectRun, listInteractions, respondInteraction, startChildRun, startRun, stopToolCall } from './agent'
import { runs } from '../kernel/run-registry'
import * as connections from './connections'
import { assertLocalBrowserWorkspace } from '../browser/manager'
import { getBrowserAutomationBridge } from '../browser/runtime'
import { getEnvironments } from '../runtime'
import { getTools, installChildRunLauncher, setCredentialChangeListener, setReviewChangeListener, setSessionChangeListener } from '../runtime'
import { NotImplementedError, toAgentError } from './errors'
import { getGatewayAddresses, getGatewayKey, getGatewayStatus, resetGatewayHealth, setGatewayEnabled } from './gateway'
import {
  fetchModels,
  getCredentialInfo,
  revealCredential,
  listModels,
  listProviders,
  removeProvider,
  setAliases,
  setCredential,
  upsertProvider,
  updateModel,
  renameModel,
  removeModel
} from './provider'
import { exportProviders, importProviders } from './provider-transfer'
import { collectImportableProviders } from '../imports/provider-import'
import {
  announceCredentialRef,
  cancelOAuth,
  signOut,
  startOAuth,
  submitOAuthCode
} from './provider-auth'
import {
  listUserModelCatalog,
  removeUserModelCatalog,
  upsertUserModelCatalog
} from './model-catalog'
import {
  getMcpSecretsInfo,
  listMcpServers,
  registerMcpBridge,
  removeMcpServer,
  setMcpSecrets,
  testMcpConnection,
  upsertMcpServer
} from './mcp'
import { getSettings, updateSettings } from './settings'
import {
  clearSearchCredential,
  listSearchProviders,
  reorderSearchProviders,
  setSearchCredential,
  setSearchEnabled,
  testBuiltinSearch,
  testSearchProvider
} from './websearch'
import { clearProxyPassword, getProxyPasswordInfo, setProxyPassword } from '../net/proxy'
import {
  applyImports,
  cancelImport,
  chooseImportSource,
  detectImports,
  getImportState,
  importJobStatus,
  listImportHistory,
  listImportHistoryItems,
  listPreviewItems,
  previewImports,
  resolveImportConflict,
  syncImportsNow,
  updateImportSyncSettings
} from './import'
import { initImports, setImportChangeListener, setImportSessionNotifier, setImportWorkspaceNotifier } from '../imports/service'
import { getClientAuthState, startClientLogin, selectClientTeam, useOffline, signOutClient, getClientUser, getClientUsage } from './client-auth'
import {
  confirmInitialConfigSync,
  getConfigSyncPreview,
  getConfigSyncStatus,
  getConfigSyncConflicts,
  resolveConfigSyncConflict,
  setupConfigSync
} from './config-sync'
import { browserManager, setBrowserChangeListener } from '../browser/manager'
import { clearBrowserProfileState, exportBrowserCookies, importBrowserCookies } from '../browser/session'
import {
  checkoutGitBranch,
  commitGit,
  createGitBranch,
  generateGitCommitMessage,
  getGitDiff,
  getGitOverview,
  listGitBranches,
  listGitCommits,
  pullGit,
  pushGit,
  stageGitPaths,
  unstageGitPaths
} from './git'
import { updateService } from '../update/update-service'
import { installMarketSkill, installZip, listMarketCategories, listMarketSkills, listSkills, marketSkillDetail, pickSkillZip, setSkillGlobalEnabled, setSkillWorkspaceActive, uninstallSkill, skillDiagnostics } from './skills'
import { commandDiagnostics, listAllCommands, listCommands, setCommandEnabled } from './commands'
import { agentDiagnostics, generateAgent, listAgents, setAgentEnabled } from './agents'
import { listModes } from './modes'
import { deleteResource, getResource, saveResource } from './markdown-resource'
import { deleteHook, hookDiagnostics, listHooks, registerHookDiagnosticsBridge, saveHook, setHookEnabledIpc, testHookIpc } from './hooks'
import { clearGoal, getGoal, registerGoalBridge, setGoal } from './goal'
import { deleteImage, importImage, listImages, migrateLegacyThemesDir, readImage, saveImage, sweepOrphans, listProfiles, saveProfile, deleteProfile, renameProfile, initializeThemeLibrary } from './theme'
import {
  closeWorkspace,
  listWorkspaceDir as listDir,
  listWorkspaces,
  pickWorkspace,
  updateWorkspace
} from './workspace'
import { listWorkspaceRecovery, mutateWorkspaceDocument as mutateWorkspaceFile, readWorkspaceDocument as readWorkspaceFile, revealWorkspaceDocument as revealWorkspaceFile, writeWorkspaceDocument as writeWorkspaceFile } from './workspace-files'
import { getReviewChangeSet, getReviewFileDiff, precheckReviewUndo, redoReviewChangeSet, undoReviewChangeSet } from './review'
import { forgetFileIndex, searchWorkspaceFiles } from './workspace-search'
import { compactContext, listContextCheckpoints, previewContext, updateContextCheckpoint } from './context'
import {
  branchSession,
  createSession,
  duplicateSession,
  deleteSession,
  getSession,
  listSessions,
  renameSession,
  replaceHistory,
  searchAll,
  setArchived,
  setFavorited,
  setMode,
  setModel
} from './sessions'
import {
  chooseBackupDirectory,
  cleanupAttachments,
  cleanupByAge,
  cleanupPreview,
  clearHistory,
  clearLocalData,
  createBackup,
  exportData,
  getBackupStatus,
  getStats,
  importApply,
  importPreview,
  openDataDirectory,
  restoreBackup,
  scheduleAutomaticBackup,
  vacuum
} from './storage'
import {
  getMigrationState,
  openMigrationDataDirectory,
  retryMigration,
  skipMigration,
  undoMigration
} from './data-migration'

type Handler<K extends InvokeChannel> = (
  req: InvokeReq<K>,
  ctx: WindowContext
) => InvokeRes<K> | Promise<InvokeRes<K>>

type HandlerMap = { [K in InvokeChannel]: Handler<K> }

type SendHandler<K extends SendChannel> = (payload: IpcSendMap[K], ctx: WindowContext) => void

type SendHandlerMap = { [K in SendChannel]: SendHandler<K> }

/**
 * 契约里已登记、但对应子系统还没到实施顺序那一步的频道。
 * 刻意**不留空**:每一个都指名道姓写出它属于哪一步,
 * 这样「契约完整 / 实现进度」两件事在同一个文件里就能对上。
 */
function todo<K extends InvokeChannel>(channel: K, step: string): Handler<K> {
  return () => {
    throw new NotImplementedError(channel, step)
  }
}

// ═══════════════════════════════════════════════════════════════
// invoke
// ═══════════════════════════════════════════════════════════════

const handlers: HandlerMap = {
  // ── 已实现 ──
  'app:getBootstrap': (_req, ctx) => {
    // A renderer reload can arrive before the tab/input debounce expires.
    flushPendingPersists()
    initializeThemeLibrary()
    return getBootstrap(ctx.kind)
  },
  'app:openExternal': ({ url }) => openExternal(url),
  'app:checkForUpdates': () => checkForUpdates(),
  'app:updateCheck': () => updateService.check(),
  'app:updateDownload': () => updateService.download(),
  'app:updateInstall': () => updateService.install(),
  'app:updateGetState': () => updateService.getState(),
  'app:copyText': ({ text }) => copyText(text),
  'app:saveTextFile': (req) => saveTextFile(req),
  'app:openSessionWindow': (req) => openSessionWindow(req),
  'clientAuth:getState': () => getClientAuthState(),
  'clientAuth:startLogin': () => startClientLogin(),
  'clientAuth:selectTeam': (req) => selectClientTeam(req.teamId),
  'clientAuth:useOffline': () => useOffline(),
  'clientAuth:signOut': () => signOutClient(),
  'clientAuth:getUser': () => getClientUser(),
  'clientAuth:getUsage': (req) => getClientUsage(req),
  'configSync:getStatus': () => getConfigSyncStatus(),
  'configSync:setup': (req) => setupConfigSync(req),
  'configSync:getConflicts': () => getConfigSyncConflicts(),
  'configSync:getPreview': () => getConfigSyncPreview(),
  'configSync:confirmInitial': () => confirmInitialConfigSync(),
  'configSync:resolve': (req) => resolveConfigSyncConflict(req.id, req.useRemote),
  'settings:get': () => getSettings(),
  'settings:update': (patch) => updateSettings(patch),
  'theme:importImage': () => importImage(),
  'theme:saveImage': (req) => saveImage(req),
  'theme:listImages': () => listImages(),
  'theme:readImage': (req) => readImage(req),
  'theme:deleteImage': (req) => deleteImage(req),
  'theme:listProfiles': () => { initializeThemeLibrary(); return listProfiles() },
  'theme:saveProfile': (req) => saveProfile(req),
  'theme:deleteProfile': (req) => deleteProfile(req.id),
  'theme:renameProfile': (req) => renameProfile(req),
  'workspace:list': () => listWorkspaces(),
  'workspace:prepare': (req, ctx) => connections.prepareWorkspace(req, ctx),
  'workspace:commitActivation': (req, ctx) => connections.commitWorkspaceActivation(req, ctx),
  'workspace:releaseActivation': ({ ticket }, ctx) => connections.releaseWorkspaceActivation(ticket, ctx),
  'workspace:createSsh': (req, ctx) => connections.createSshWorkspace(req, ctx),
  'connection:list': () => connections.listConnections(),
  'connection:upsert': (req) => connections.upsertConnection(req),
  'connection:remove': ({ id }) => connections.removeConnection(id),
  'connection:connect': (req, ctx) => connections.connectForBrowse(req, ctx),
  'connection:disconnect': ({ id }) => getEnvironments().disconnect(id),
  'connection:cancel': ({ requestId }, ctx) => connections.cancelConnectionRequest(requestId, ctx),
  'connection:browse': (req, ctx) => connections.browseConnection(req, ctx),
  'connection:closeBrowse': ({ browseId }, ctx) => connections.closeBrowse(browseId, ctx),
  'connection:respond': (req, ctx) => connections.respondSshAuthentication(req, ctx),
  'connection:pickFile': () => connections.pickSshFile(),
  'workspace:pick': () => pickWorkspace(),
  'workspace:update': (req) => updateWorkspace(req),
  'workspace:close': ({ id }) => {
    browserManager.closeWorkspace(id)
    forgetFileIndex(id)
    return closeWorkspace(id)
  },
  /*
    ★ 只记在内存里的窗口级状态,没有任何副作用 —— 不落库、不广播。
    读它的是插件的路径类能力(`ipc/plugins.ts` 的 `currentWorkspace`)。
  */
  'workspace:setActive': ({ workspaceId }, ctx) => {
    windows.setActiveWorkspace(ctx.id, workspaceId)
  },
  'workspace:listDir': (req) => listDir(req),
  'workspace:searchFiles': (req) => searchWorkspaceFiles(req),
  'workspace:readFile': (req) => readWorkspaceFile(req),
  'workspace:writeFile': (req) => writeWorkspaceFile(req),
  'workspace:mutateFile': (req) => mutateWorkspaceFile(req),
  'workspace:revealFile': (req) => revealWorkspaceFile(req),
  'workspace:listRecovery': (req) => listWorkspaceRecovery(req),
  'review:getChangeSet': (req) => getReviewChangeSet(req),
  'review:getFileDiff': (req) => getReviewFileDiff(req),
  'review:precheckUndo': (req) => precheckReviewUndo(req),
  'review:undo': (req) => undoReviewChangeSet(req),
  'review:redo': (req) => redoReviewChangeSet(req),
  'browser:list': ({ workspaceId }) => browserManager.list(workspaceId),
  'browser:open': ({ workspaceId, url, title, profileId, clientTabId }) => {
    assertLocalBrowserWorkspace(workspaceId)
    return browserManager.open({ workspaceId, url, title, profileId, clientTabId, source: 'user' })
  },
  'browser:navigate': ({ workspaceId, tabId, url }) => {
    assertLocalBrowserWorkspace(workspaceId)
    const tab = browserManager.get(tabId)
    if (tab?.workspaceId !== workspaceId) throw new Error('浏览器标签不属于当前工作区')
    return browserManager.navigate(tabId, url)
  },
  'browser:close': ({ workspaceId, tabId }) => {
    const tab = browserManager.get(tabId)
    if (tab?.workspaceId !== workspaceId) throw new Error('浏览器标签不属于当前工作区')
    return browserManager.close(tabId)
  },
  'browser:bind': async ({ workspaceId, tabId, webContentsId }) => {
    assertLocalBrowserWorkspace(workspaceId)
    if (!Number.isInteger(webContentsId) || webContentsId <= 0) throw new Error('浏览器 webContentsId 无效')
    await getBrowserAutomationBridge().bindIab({ workspaceId, tabId, webContentsId })
  },
  'browser:profiles': () => browserManager.listProfiles(),
  'browser:createProfile': ({ name, domains, startUrl }) => {
    const profile = browserManager.createProfile(name, domains, startUrl)
    windows.emitToAll('browser:profilesChanged', browserManager.listProfiles())
    return profile
  },
  'browser:deleteProfile': async ({ id }) => {
    const profile = browserManager.listProfiles().find((item) => item.id === id)
    if (profile === undefined) throw new Error('Profile 不存在')
    if (profile.isDefault) return browserManager.deleteProfile(id)
    await Promise.all([
      ...store.listWorkspaces().map((workspace) => clearBrowserProfileState(workspace.id, id)),
      getBrowserAutomationBridge().clearProfile(id)
    ])
    browserManager.deleteProfile(id)
    windows.emitToAll('browser:profilesChanged', browserManager.listProfiles())
  },
  'browser:exportCookies': ({ workspaceId, profileId }, ctx) => {
    assertLocalBrowserWorkspace(workspaceId)
    const profile = browserManager.listProfiles().find((item) => item.id === profileId)
    if (profile === undefined) throw new Error('Profile 不存在')
    return exportBrowserCookies(ctx.sender, workspaceId, profile)
  },
  'browser:importCookies': ({ workspaceId, profileId }, ctx) => {
    assertLocalBrowserWorkspace(workspaceId)
    if (!browserManager.listProfiles().some((item) => item.id === profileId)) throw new Error('Profile 不存在')
    return importBrowserCookies(ctx.sender, workspaceId, profileId)
  },
  'browser:clearProfileState': async ({ workspaceId, profileId }) => {
    assertLocalBrowserWorkspace(workspaceId)
    const profile = browserManager.listProfiles().find((item) => item.id === profileId)
    if (profile === undefined) throw new Error('Profile 不存在')
    if (profile.isDefault) throw new Error('默认浏览器不能清除登录态')
    await Promise.all([
      clearBrowserProfileState(workspaceId, profileId),
      getBrowserAutomationBridge().clearProfile(profileId, workspaceId)
    ])
  },
  'git:getOverview': (req) => getGitOverview(req),
  'git:listBranches': (req) => listGitBranches(req),
  'git:listCommits': (req) => listGitCommits(req),
  'git:getDiff': (req) => getGitDiff(req),
  'git:stage': (req) => stageGitPaths(req),
  'git:unstage': (req) => unstageGitPaths(req),
  'git:commit': (req) => commitGit(req),
  'git:checkoutBranch': (req) => checkoutGitBranch(req),
  'git:createBranch': (req) => createGitBranch(req),
  'git:pull': (req) => pullGit(req),
  'git:push': (req) => pushGit(req),
  'git:generateCommitMessage': (req) => generateGitCommitMessage(req),
  'scheduled:listTasks': ({ workspaceId }) => store.listScheduledTasks(workspaceId),
  'scheduled:getTask': ({ id }) => store.getScheduledTask(id) ?? null,
  // 写入的三件套(落库 → 重排 → 广播)收口在 `scheduled/bridge.ts`,与 Agent 工具共用同一份
  'scheduled:create': (input) => scheduledWrites.create(input),
  'scheduled:update': ({ id, patch }) => scheduledWrites.update(id, patch),
  'scheduled:delete': ({ id }) => {
    scheduledWrites.remove(id)
  },
  'scheduled:setEnabled': ({ id, enabled }) => scheduledWrites.setEnabled(id, enabled),
  'scheduled:runNow': ({ id }) => {
    return runScheduledTaskNow(id)
  },
  'scheduled:listRuns': ({ taskId, limit }) => store.listScheduledRuns(taskId, limit),
  'scheduled:getRun': ({ id }) => store.getScheduledRun(id) ?? null,
  'scheduled:deleteRun': ({ id }) => {
    store.deleteScheduledRun(id)
    windows.emitToAll('scheduled:changed', { kind: 'run', runId: id })
  },
  'tabs:getInner': ({ workspaceId }) => {
    const key = innerTabKey(workspaceId)
    flushPendingPersists(key)
    return store.getInnerTabs(workspaceId)
  },
  'session:getInput': ({ sessionId }) => readSessionInput(sessionId),

  // ── 附件(读取走 ncw:// 协议,不占 IPC) ──
  'attachment:upload': (req) => uploadAttachment(req),
  'attachment:pick': (req) => pickAttachments(req),
  'attachment:prepareWorkspaceUpload': (req, ctx) => prepareWorkspaceUpload(req, ctx),
  'attachment:completeWorkspaceUpload': ({ ticket }, ctx) => completeWorkspaceUpload(ticket, ctx),
  'attachment:cancelWorkspaceUpload': ({ ticket }, ctx) => cancelWorkspaceUpload(ticket, ctx),
  'attachment:remove': (req) => removeAttachment(req),
  'attachment:listBySession': (req) => listSessionAttachments(req),

  // ── 会话 / SQLite ──
  'sessions:list': (req) => listSessions(req),
  'sessions:get': (req) => getSession(req),
  'sessions:replaceHistory': (req) => replaceHistory(req),
  'sessions:create': (req) => createSession(req),
  'sessions:duplicate': (req) => duplicateSession(req),
  'sessions:branch': (req) => branchSession(req),
  'sessions:rename': (req) => renameSession(req),
  'sessions:setMode': (req) => setMode(req),
  'sessions:setModel': (req) => setModel(req),
  'sessions:setArchived': (req) => setArchived(req),
  'sessions:setFavorited': (req) => setFavorited(req),
  'sessions:delete': (req) => deleteSession(req),
  'conversations:searchAll': (req) => searchAll(req),
  'context:list': (req) => listContextCheckpoints(req),
  'context:updateCheckpoint': (req) => updateContextCheckpoint(req),
  'context:compact': (req) => compactContext(req),
  'context:preview': (req) => previewContext(req),
  'storage:getStats': () => getStats(),
  'storage:vacuum': () => vacuum(),
  'storage:openDataDirectory': () => openDataDirectory(),
  'storage:export': (req) => exportData(req),
  'storage:importPreview': (_req, ctx) => importPreview(ctx.sender.id),
  'storage:importApply': (req, ctx) => importApply(req, ctx.sender.id),
  'storage:chooseBackupDirectory': () => chooseBackupDirectory(),
  'storage:getBackupStatus': () => getBackupStatus(),
  'storage:createBackup': (req) => createBackup(req),
  'storage:restoreBackup': (req, ctx) => restoreBackup(req, ctx.sender.id),
  'storage:cleanupPreview': (req) => cleanupPreview(req),
  'storage:cleanupAttachments': () => cleanupAttachments(),
  'storage:cleanupByAge': (req) => cleanupByAge(req),
  'storage:clearHistory': () => clearHistory(),
  'storage:clearLocalData': (req) => clearLocalData(req),

  /*
    ── 启动迁移闸门(见 db/startup-migration.ts) ──

    ★ 这四条**刻意不 import 任何别的 ipc 模块**,`./data-migration` 只依赖
    `electron` 的 shell 和 `db/index.ts` 的纯路径函数。理由写在那个文件头:
    闸门存在的那段时间里数据库还没打开,谁在这里顺手摸一次库,启动就变成
    先开内存兜底库、再 `openDatabase()` 抛错,而报错完全指不到这里。
  */
  'dataMigration:getState': () => getMigrationState(),
  'dataMigration:retry': () => retryMigration(),
  'dataMigration:skip': () => skipMigration(),
  'dataMigration:undoMerge': () => undoMigration(),
  'dataMigration:openDataDirectory': () => openMigrationDataDirectory(),

  // ── 从其他 AI 应用导入(设置 › 导入) ──
  'imports:detect': (req) => detectImports(req),
  'imports:chooseSource': (req) => chooseImportSource(req),
  'imports:getState': (req) => getImportState(req),
  'imports:preview': (req) => previewImports(req),
  'imports:previewItems': (req) => listPreviewItems(req),
  'imports:apply': (req) => applyImports(req),
  'imports:status': (req) => importJobStatus(req),
  'imports:cancel': (req) => cancelImport(req),
  'imports:history': (req) => listImportHistory(req),
  'imports:historyItems': (req) => listImportHistoryItems(req),
  'imports:updateSync': (req) => updateImportSyncSettings(req),
  'imports:syncNow': (req) => syncImportsNow(req),
  'imports:resolveConflict': (req) => resolveImportConflict(req),

  // ── 步骤 3–5:RunRegistry / AgentSession / 交互 ──
  'agent:run': (req, ctx) => startRun(req, ctx),
  'agent:attach': (req, ctx) => attachRun(req, ctx),
  'agent:abort': (req) => abortRun(req),
  'agent:interject': (req, ctx) => interjectRun(req, ctx),
  'agent:respondInteraction': respondInteraction,
  'agent:listInteractions': listInteractions,
  'agent:listTools': ({ workspaceId }) => {
    if (store.getWorkspace(workspaceId) === undefined) throw new Error('Workspace does not exist')
    return getTools().info()
  },
  // ── Agent 的 shell:只停这一条命令,run 继续 ──
  'shell:stopToolCall': (req, ctx) => stopToolCall(req, ctx),
  // ── 步骤 8:终端 ──
  'terminal:create': (req, ctx) => terminalHost.create(req, ctx.sender),
  'terminal:prepare': (req, ctx) => terminalHost.prepare(req, ctx.sender),
  'terminal:approve': ({ id, approved }, ctx) => terminalHost.approve(id, approved, ctx.sender),
  'terminal:kill': ({ id }, ctx) => terminalHost.kill(id, ctx.sender),
  'terminal:list': ({ workspaceId }, ctx) => terminalHost.list(workspaceId, ctx.sender),
  'terminal:getBuffer': ({ id }, ctx) => terminalHost.attach(id, ctx.sender),

  // ── 步骤 10:MCP ──
  'mcp:list': () => listMcpServers(),
  'mcp:upsert': (cfg) => upsertMcpServer(cfg),
  'mcp:remove': ({ id }) => removeMcpServer(id),
  'mcp:testConnection': ({ id }) => testMcpConnection(id),
  'mcp:setSecrets': ({ id, values }) => setMcpSecrets(id, values),
  'mcp:getSecretsInfo': ({ id }) => getMcpSecretsInfo(id),

  // ── 搜索服务 ──
  'websearch:list': () => listSearchProviders(),
  'websearch:setEnabled': ({ id, enabled }) => setSearchEnabled(id, enabled),
  'websearch:reorder': ({ ids }) => reorderSearchProviders(ids),
  'websearch:setCredential': ({ id, apiKey }) => setSearchCredential(id, apiKey),
  'websearch:clearCredential': ({ id }) => clearSearchCredential(id),
  'websearch:test': ({ id }) => testSearchProvider(id),
  'websearch:testBuiltin': () => testBuiltinSearch(),

  // ── 网络代理 ──
  'proxy:setPassword': ({ password }) => setProxyPassword(password),
  'proxy:clearPassword': () => clearProxyPassword(),
  'proxy:getPasswordInfo': () => getProxyPasswordInfo(),

  // ── Skill(渐进披露:提示词里只有目录,正文经 `Skill` 工具取)──
  // ── 插件 ──
  'plugins:list': () => listPlugins(),
  'plugins:setEnabled': (req) => setPluginEnabled(req),
  'plugins:uninstall': (req) => uninstallPlugin(req),
  'plugins:pickPackage': () => pickPluginPackage(),
  'plugins:installPackage': (req) => installPlugin(req),
  'plugins:grantPermissions': (req) => grantPluginPermissions(req),
  'plugins:revokePermissions': (req) => revokePluginPermissions(req),
  'plugins:activity': (req) => pluginActivity(req),
  'plugins:runCommand': (req) => runPluginCommand(req),
  'plugins:activateEditor': (req) => activatePluginEditor(req),
  'plugins:cardAction': (req) => deliverPluginCardAction(req),
  'plugins:interactionReply': (req) => { replyPluginInteraction(req) },
  'plugins:openWebApp': (req) => openPluginWebApp(req),
  'plugins:setEditorDirty': (req) => { setPluginEditorDirty(req) },
  'plugins:confirmClose': (req) => confirmPluginClose(req),
  'plugins:marketList': (req) => listMarketPlugins(req),
  'plugins:marketCategories': () => listMarketPluginCategories(),
  'plugins:marketDetail': (req) => marketPluginDetail(req),
  'plugins:installMarket': (req) => installMarketPlugin(req),
  'plugins:checkUpdates': (req) => checkPluginUpdates(req),
  'plugins:updateAll': () => updateAllPlugins(),
  'plugins:getConfiguration': (req) => getPluginConfiguration(req),
  'plugins:setConfiguration': (req) => setPluginConfiguration(req),

  'skills:list': (req) => listSkills(req),
  'skills:pickZip': () => pickSkillZip(),
  'skills:installZip': (req) => installZip(req),
  'skills:installMarket': (req) => installMarketSkill(req),
  'skills:uninstall': (req) => uninstallSkill(req),
  'skills:marketList': (req) => listMarketSkills(req),
  'skills:marketCategories': () => listMarketCategories(),
  'skills:marketDetail': (req) => marketSkillDetail(req),
  'skills:diagnostics': (req) => skillDiagnostics(req),
  'skills:setGlobalEnabled': (req) => setSkillGlobalEnabled(req),
  'skills:setWorkspaceActive': (req) => setSkillWorkspaceActive(req),

  // ── 斜杠命令(`.next-cowork/commands/*.md` + 内置 `/init`)──
  'commands:list': (req) => listCommands(req),
  'commands:diagnostics': (req) => commandDiagnostics(req),

  // ── 扩展面板:命令 / 子代理的管理与编辑 ──
  'commands:listAll': (req) => listAllCommands(req),
  'commands:setEnabled': (req) => setCommandEnabled(req),
  'agents:list': (req) => listAgents(req),
  'agents:diagnostics': (req) => agentDiagnostics(req),
  'agents:setEnabled': (req) => setAgentEnabled(req),
  'agents:generate': (req) => generateAgent(req),
  'modes:list': (req) => listModes(req),
  'resource:get': (req) => getResource(req),
  'resource:save': (req) => saveResource(req),
  'resource:delete': (req) => deleteResource(req),

  // ── 钩子（本阶段只管存，事件触发点还没接） ──
  'hooks:list': (req) => listHooks(req),
  'hooks:diagnostics': (req) => hookDiagnostics(req),
  'hooks:save': (req) => saveHook(req),
  'hooks:delete': (req) => deleteHook(req),
  'hooks:setEnabled': (req) => setHookEnabledIpc(req),
  'hooks:test': (req) => testHookIpc(req),

  // ── 会话目标 ──
  'goal:get': (req, ctx) => getGoal(req, ctx),
  'goal:set': (req) => setGoal(req),
  'goal:clear': (req) => { clearGoal(req) },

  // ── 步骤 4 / 13:上游与网关 ──
  'provider:list': () => listProviders(),
  'providers:listImportable': ({ sourceKind, pickedDir }) => collectImportableProviders(sourceKind, pickedDir),
  'provider:listModels': ({ providerId }) => listModels(providerId),
  'provider:upsert': (req) => upsertProvider(req),
  'provider:remove': ({ id }) => removeProvider(id),
  'provider:fetchModels': ({ providerId }) => fetchModels(providerId),
  'provider:setAliases': ({ providerId, models }) => setAliases(providerId, models),
  'provider:setCredential': ({ providerId, apiKey }) => setCredential(providerId, apiKey),
  'provider:getCredentialInfo': ({ providerId }) => getCredentialInfo(providerId),
  'provider:revealCredential': ({ providerId }) => revealCredential(providerId),
  'provider:export': (req) => exportProviders(req),
  'provider:import': (req) => importProviders(req),
  'provider:startOAuth': ({ providerId }) => startOAuth(providerId),
  'provider:cancelOAuth': ({ providerId }) => cancelOAuth(providerId),
  'provider:submitOAuthCode': ({ providerId, code }) => submitOAuthCode(providerId, code),
  'provider:signOut': ({ providerId }) => signOut(providerId),
  // Agent 上游已支持三种协议;独立连接测试入口仍待接入。
  'provider:test': todo('provider:test', '步骤 13(独立连接测试入口)'),
  'model:update': (req) => updateModel(req),
  'model:rename': ({ providerId, alias, nextAlias }) => renameModel(providerId, alias, nextAlias),
  'model:remove': ({ providerId, alias }) => removeModel(providerId, alias),
  'modelCatalog:list': () => listUserModelCatalog(),
  'modelCatalog:upsert': (req) => upsertUserModelCatalog(req),
  'modelCatalog:remove': ({ id }) => removeUserModelCatalog(id),
  'usage:getSummary': (window) => store.getUsageSummary(window),
  'usage:getRequestLogs': (query) => store.getUsageRequestLogs(query),
  'usage:getProviderStats': (window) => store.getUsageProviderStats(window),
  'usage:getModelStats': (window) => store.getUsageModelStats(window),
  /*
   * ★ 概览这两条查询前先刷一次汇总。后台定时器五分钟一轮,少了这一步,
   * 「刚聊完就打开统计页」会看到一张停在几分钟前的图 —— 而图上没有任何
   * 迹象说明它是旧的,用户只会觉得刚才那次对话没被记上。
   * refreshUsageRollup 幂等,与定时器重叠调用只是重复功,不会算错。
   */
  'usage:getDailySeries': (window) => {
    store.refreshUsageRollup()
    return store.getUsageDailySeries(window)
  },
  'usage:getActivityStats': () => {
    store.refreshUsageRollup()
    return store.getUsageActivityStats()
  },
  'gateway:getStatus': () => getGatewayStatus(),
  'gateway:getKey': () => getGatewayKey(),
  'gateway:getAddresses': () => getGatewayAddresses(),
  'gateway:setEnabled': ({ enabled }) => setGatewayEnabled(enabled),
  'gateway:resetHealth': ({ providerId }) => resetGatewayHealth(providerId)
}

// ═══════════════════════════════════════════════════════════════
// send —— 高频无返回(协议 §3.2)
// ═══════════════════════════════════════════════════════════════

/**
 * Tab 布局落盘防抖 500ms(方案 §9):拖动排序时每帧都在变,
 * 切个 Tab 不能同步命中磁盘。
 */
const PERSIST_DEBOUNCE_MS = 500
const pendingPersists = new Map<string, { timer: NodeJS.Timeout; value: unknown }>()

/**
 * @param immediate 跳过防抖直接落盘。用于**离散低频且丢不起**的写入
 *   (入队/插话/删除一条排队消息)——它们的频率低到不值得防抖,
 *   而丢失代价远高于「最后一次拖动的 Tab 顺序」。
 */
function persistDebounced(key: string, value: unknown, immediate = false): void {
  const prev = pendingPersists.get(key)
  if (prev) clearTimeout(prev.timer)

  if (immediate) {
    // ★ 必须连 pending 一起清掉:否则先 immediate 写了新值,
    //   之前那个还挂着的定时器过一会儿会把**旧值**盖回去。
    pendingPersists.delete(key)
    store.setKv(key, value)
    return
  }

  const timer = setTimeout(() => {
    pendingPersists.delete(key)
    store.setKv(key, value)
  }, PERSIST_DEBOUNCE_MS)
  // 连值一起存住:退出时要**写掉**它,不是丢掉它
  pendingPersists.set(key, { timer, value })
}

/**
 * 读回未发出的输入。**校验在主进程侧做**,渲染层拿到的要么可用,要么是 null。
 *
 * ★ 读到失效存档时**顺手删键** —— 这就是「30 天兜底清扫」的全部实现。
 * 单独跑一个扫描任务是过度设计:一份存档只有在被读的时候才有意义,
 * 而没人读的键留在 kv 里除了占几 KB 没有别的影响。
 */
function readSessionInput(sessionId: string): SessionInputState | null {
  const key = sessionInputKey(sessionId)
  flushPendingPersists(key)
  const raw = store.getKv<unknown>(key, null)
  if (raw === null) return null
  if (!isValidSessionInput(raw, Date.now())) {
    store.setKv(key, null)
    return null
  }
  return raw
}

/** Flush before reads and app quit so pending UI state survives renderer reloads. */
export function flushPendingPersists(onlyKey?: string): void {
  for (const [key, { timer, value }] of pendingPersists) {
    if (onlyKey !== undefined && key !== onlyKey) continue
    clearTimeout(timer)
    store.setKv(key, value)
    pendingPersists.delete(key)
  }
}

const sendHandlers: SendHandlerMap = {
  'window:ready': ({ kind }, ctx) => {
    windows.markReady(ctx.sender, kind)
    // 自绘的中间那颗按钮首帧就得画对字形。这一条**同步**回推,而渲染层那个订阅块
    // 里所有 on(...) 都排在 announceReady() 之前(App.tsx),所以它不会落进空窗。
    pushMaximized(ctx.sender)
    // 握手第一条腿(协议 §7)。一行日志,但它是「渲染层真的跑起来了」
    // 在主进程侧唯一的可观测证据 —— 渲染层的 console 不进这个 stdout。
    console.log(`[ipc] 窗口就绪 · kind=${kind}`)
  },
  'window:control': ({ action }, ctx) => applyWindowControl(ctx.sender, action),
  // 用户在「有未保存的改动」对话框里选完了(保存或丢弃),重新走一次退出。
  // 这一轮里渲染层的 `allowUnload` 已经是 true,不会再顶住 unload —— 见 contract。
  'app:quitConfirmed': () => requestQuit(),
  'tabs:persistOuter': ({ kind, state }) => persistDebounced(outerTabKey(kind), state),
  'tabs:persistInner': ({ workspaceId, state }) =>
    persistDebounced(innerTabKey(workspaceId), state),
  'session:persistInput': ({ sessionId, state, immediate }) =>
    persistDebounced(sessionInputKey(sessionId), state, immediate),

  'terminal:write': ({ id, data }, ctx) => terminalHost.write(id, data, ctx.sender),
  'terminal:resize': ({ id, cols, rows }, ctx) => terminalHost.resize(id, cols, rows, ctx.sender)
}

// ═══════════════════════════════════════════════════════════════
// 注册
// ═══════════════════════════════════════════════════════════════

/**
 * ★ 异常绝不穿过 ipcMain.handle。Electron 会把它 stringify 成
 * `Error: Error invoking remote method 'x': ...`,§4.11 那套 code 分类全丢,
 * UI 就没法决定「跳设置页 / 提示 compact / 显示重试倒计时」。
 */
function safeHandle<K extends InvokeChannel>(channel: K, fn: Handler<K>): void {
  ipcMain.handle(channel, async (event, req): Promise<IpcResult<InvokeRes<K>>> => {
    try {
      const data = await fn(req as InvokeReq<K>, windows.of(event.sender))
      return { ok: true, data }
    } catch (err) {
      const error = toAgentError(err)
      // 未实现的频道也是 code:'unknown'(UI 侧「弹个 toast」的处理是对的),
      // 但它不是故障,不该打堆栈 —— 后面还有十几步没实现,
      // 每步都往控制台糊一屏堆栈的话,真正的错误就沉底了。
      if (err instanceof NotImplementedError) console.warn(`[ipc] ${error.message}`)
      else if (
        error.code === 'unknown' &&
        !(channel === 'sessions:get' && /会话不存在|不存在该会话|session.*not found/i.test(error.message))
      ) {
        console.error(`[ipc] ${channel} 失败:`, err)
      }
      return { ok: false, error }
    }
  })
}

/**
 * 只登记启动迁移闸门那几条频道。
 *
 * ★ **它必须能在 `registerIpc()` 之前单独调用。** 闸门存在的那段时间里数据库还
 * 没打开,而 `registerIpc()` 会拉起导入服务、扫孤儿文件、迁移主题目录 —— 那些
 * 全都要库。所以启动路径上先调这一个,闸门放行之后才调完整的 `registerIpc()`。
 *
 * ★ 这几条 handler 自己也不碰库(见 `./data-migration` 的文件头),所以「先登记
 * 它们」这件事本身是安全的。
 */
/**
 * 闸门那几条频道。`registerMigrationIpc()` 单独登记它们,`registerIpc()` 跳过它们。
 *
 * ★ 必须显式列出来,不能靠 `startsWith('dataMigration:')` 之类的模式 —— 模式匹配
 * 会在下一个人加频道时**静默**把他那条漏掉(漏掉的后果是「未实现的频道」,
 * 而不是「注册冲突」,后者至少会当场报错)。
 */
const MIGRATION_CHANNELS = [
  'dataMigration:getState',
  'dataMigration:retry',
  'dataMigration:skip',
  'dataMigration:undoMerge',
  'dataMigration:openDataDirectory'
] as const satisfies readonly InvokeChannel[]

const MIGRATION_CHANNEL_SET: ReadonlySet<string> = new Set<string>(MIGRATION_CHANNELS)

/**
 * 只登记启动迁移闸门那几条频道。
 *
 * ★ **它必须能在 `registerIpc()` 之前单独调用。** 闸门存在的那段时间里数据库还
 * 没打开,而 `registerIpc()` 会拉起导入服务、扫孤儿文件、迁移主题目录 —— 那些
 * 全都要库。所以启动路径上先调这一个,闸门放行之后才调完整的 `registerIpc()`。
 *
 * ★ 这几条 handler 自己也不碰库(见 `./data-migration` 的文件头),所以「先登记
 * 它们」这件事本身是安全的。
 */
export function registerMigrationIpc(): void {
  for (const channel of MIGRATION_CHANNELS) {
    safeHandle(channel, handlers[channel] as Handler<InvokeChannel>)
  }
}

export function registerIpc(): void {
  for (const channel of Object.keys(INVOKE_CHANNELS) as InvokeChannel[]) {
    /*
      ★ 闸门那几条可能已经由 `registerMigrationIpc()` 登记过,而
      `ipcMain.handle` 对同一个频道注册两次会直接抛错 —— 那会是一条
      「启动迁移屏跑完之后应用起不来」的故障。这里跳过,而不是让调用方记得别重复调。
    */
    if (MIGRATION_CHANNEL_SET.has(channel)) continue
    safeHandle(channel, handlers[channel] as Handler<InvokeChannel>)
  }

  for (const channel of Object.keys(SEND_CHANNELS) as SendChannel[]) {
    ipcMain.on(channel, (event, payload) => {
      // send 没有回程信封可以报错。**必须**兜住异常,
      // 否则一个畸形载荷就是主进程里的 uncaught exception。
      try {
        const fn = sendHandlers[channel] as SendHandler<SendChannel>
        fn(payload, windows.of(event.sender))
      } catch (err) {
        console.error(`[ipc] send ${channel} 失败:`, err)
      }
    })
  }

  registerThemeBridge()
  registerMcpBridge()
  registerGoalBridge()
  registerHookDiagnosticsBridge()
  connections.registerConnectionBridge()
  const browserAutomation = getBrowserAutomationBridge()
  setBrowserChangeListener((change) => {
    browserAutomation.reconcile(change.workspaceId, change.tabs)
    windows.emitToAll('browser:changed', change)
  })
  browserAutomation.setCuaListener((event) => windows.emitToAll('browser:cua', event))
  /*
    ★ 子 run 的启动器。方向是 **ipc 依赖 runtime,runtime 永不依赖 ipc** ——
    反过来写会把 electron 拖进 runtime 的 import 图,`agent-run.test.ts`
    那条无头链路当场就断(见 runtime.ts 文件头)。和 `registerMcpBridge`
    里那次 `setMcpChangeListener` 是同一种接线。
  */
  installChildRunLauncher(startChildRun)
  /*
    ★ 运行中角标的**权威来源**。渲染层那份 run 索引原先只靠 `agent:event` 里的
    `run_end` 收敛,而那条流没有订阅者时会被整批丢弃 —— 定时任务起的 run、
    ⌘R 重载后还没打开的会话都属于这一类,于是它们的角标永远停在「运行中」。
    这里补一条与订阅无关的全量广播,RunRegistry 一变就发。
  */
  runs.onActiveChange(broadcastActiveRuns)
  setSessionChangeListener((change) => {
    windows.emitToAll('sessions:changed', change)
  })
  setReviewChangeListener((change) => {
    windows.emitToAll('review:changed', change)
  })
  // 刷新 token 之后（含刷失败标记 needsReauth）把新的登录态推给设置页
  setCredentialChangeListener(announceCredentialRef)

  /*
    导入服务的接线,和上面 `setMcpChangeListener` 是同一种:
    `imports/*` 不 import `window/registry`,方向保持「ipc 依赖服务,服务不依赖 ipc」。
  */
  setImportChangeListener((payload) => windows.emitToAll('imports:changed', payload))
  setImportSessionNotifier((workspaceId, sessionIds) => {
    // ★ 按工作区合并一条,不逐会话广播 —— 一次导入一百个会话就是一百次全量刷新。
    windows.emitToAll('sessions:changed', { kind: 'metadata', workspaceId, sessionIds })
  })
  /*
    ★ 导入建了工作区要广播,否则左上角那个切换器里看不到新项目 ——
    渲染层那份列表只靠这个事件更新(`ipc/workspace.ts` 的 `announce` 是同一句)。
    整个 job 结束后只发一次,它带的是全量列表。
  */
  setImportWorkspaceNotifier(() => {
    windows.emitToAll('workspace:changed', { workspaces: store.listWorkspaces() })
  })
  // 上次没跑完的批次标成 interrupted,而不是留一个永远转圈的进度条。
  initImports()

  // 自动备份只在启动时按到期判断一次，不依赖渲染层计时器。
  scheduleAutomaticBackup()

  // ★ 必须排在 sweepOrphans 之前:搬完才知道哪些文件还在。
  //   反过来的话,sweep 扫的是空的新目录,而旧目录里的图一张都不在索引对应的位置上。
  migrateLegacyThemesDir()

  // 扫掉两相导入中途放弃留下的孤儿图片。放在这里是因为**此刻 pending 必然是空的**,
  // 所以「不在索引里」就等于「没人要」—— 换成运行期任何一个时刻都不成立。
  sweepOrphans()
}

export { EMPTY_OUTER }
export { shutdownRuns } from './agent'
export {
  prepareStoredAccountScope,
  reconcileMigratedWorkspacesForStoredAccount,
  shutdownClientAuth
} from './client-auth'
export { shutdownTerminals }
