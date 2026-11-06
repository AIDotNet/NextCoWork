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
import {
  closeDocumentView,
  commandDocumentView,
  documentViewState,
  headersDocumentView,
  listDocumentView,
  inputDocumentView,
  layoutDocumentView,
  openDocumentView,
  renderDocumentView,
  saveDocumentView
} from './document-engine'
import {
  EMPTY_OUTER,
  innerTabKey,
  outerTabKey,
  store
} from '../state/store'
import { windows, type WindowContext } from '../window/registry'
import { agentShells } from '../agent-shells'
import { refreshScheduler, runScheduledTaskNow } from '../scheduled/scheduler'
import { scheduledWrites, setScheduledChangeListener } from '../scheduled/bridge'
import { setSchedulerRefresh } from '../scheduled/refresh'
import { applyWindowControl, pushMaximized } from '../window/title-bar'
import { shutdownTerminals, terminalHost } from '../terminal-host'
import { checkForUpdates, copyImage, copyText, getBootstrap, openExternal, openSessionWindow, registerThemeBridge, requestQuit, saveImageFile, saveTextFile } from './app'
import {
  cancelWorkspaceUpload,
  completeWorkspaceUpload,
  listSessionAttachments,
  pickAttachments,
  prepareWorkspaceUpload,
  removeAttachment,
  uploadAttachment
} from './attachment'
import { abortRun, attachRun, backgroundSubagent, backgroundToolCall, broadcastActiveRuns, interjectRun, listInteractions, queueSessionInput, reportBackgroundChild, respondInteraction, sessionRuntime, setRunPermissionMode, startChildRun, startRun, stopToolCall, unwatchRun } from './agent'
import { runs } from '../kernel/run-registry'
import * as connections from './connections'
import { assertLocalBrowserWorkspace } from '../browser/manager'
import { getBrowserAutomationBridge } from '../browser/runtime'
import { getEnvironments } from '../runtime'
import { getHost, getInlineCompletionService, getPromptSuggestionGenerator } from '../runtime'
import { getActiveGoal } from '../goal/state'
import { createComposerIpc } from './composer'
import { getTools, installBackgroundChildHandler, installChildRunLauncher, setAccountsChangeListener, setCredentialChangeListener, setReviewChangeListener, setSessionChangeListener, setSkillWrittenListener, setVideoJobChangeListener } from '../runtime'
import { setGoalLauncher } from '../goal/runtime'
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
  addOrReauthAccount,
  announceCredentialRef,
  cancelOAuth,
  signOut,
  startOAuth,
  submitOAuthCode
} from './provider-auth'
import {
  announceAccountsSafe,
  clearProviderAccountLimit,
  fetchProviderAccountQuota,
  listProviderAccounts,
  removeProviderAccount,
  reorderProviderAccounts,
  setCurrentProviderAccount,
  setProviderAccountEnabled,
  setProviderAccountLabel
} from './provider-accounts'
import {
  listUserModelCatalog,
  removeUserModelCatalog,
  upsertUserModelCatalog
} from './model-catalog'
import { applyModelCatalogSync, previewModelCatalogSync } from './model-catalog-sync'
import { applyPricingSync, clearSyncedPricing, listSyncedPricing, previewPricingSync } from './pricing'
import {
  announceVideoJob,
  cancelVideoJob,
  getVideoJob,
  listVideoJobs,
  retryVideoRetrieval,
  saveVideoJobFile
} from './video'
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
import { getReferralCenter } from './referral'
import { createRechargeCheckout, getRechargeOptions, getRechargeOrder } from './recharge'
import {
  confirmInitialConfigSync,
  getConfigSyncPreview,
  getConfigSyncStatus,
  getConfigSyncConflicts,
  resolveConfigSyncConflict,
  setConfigSyncUsage,
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
  getGitDiffSides,
  getGitOverview,
  listGitBranches,
  listGitCommits,
  pullGit,
  pushGit,
  stageGitPaths,
  unstageGitPaths
} from './git'
import { updateService } from '../update/update-service'
import { activateWrittenSkills, installMarketSkill, installZip, listMarketCategories, listMarketSkills, listSkills, marketSkillDetail, setSkillGlobalEnabled, setSkillWorkspaceActive, uninstallSkill, skillDiagnostics } from './skills'
import { pickSkillZip } from './skills-dialog'
import { commandDiagnostics, listAllCommands, listCommands, setCommandEnabled } from './commands'
import { agentDiagnostics, generateAgent, listAgents, setAgentEnabled } from './agents'
import { listModes } from './modes'
import { deleteResource, getResource, saveResource } from './markdown-resource'
import { deleteHook, hookDiagnostics, listHooks, registerHookDiagnosticsBridge, saveHook, setHookEnabledIpc, testHookIpc } from './hooks'
import { clearGoal, getGoal, registerGoalBridge, setGoal } from './goal'
import { getDevicesState, inspectDeviceInvitation, pairDevice, removeDevice, routeInvoke, routeSend, selectDevice, setDeviceRelay, useLocalDevice } from './device'
import { deleteImage, importImage, listImages, migrateLegacyThemesDir, readImage, saveImage, sweepOrphans, listProfiles, saveProfile, deleteProfile, renameProfile, initializeThemeLibrary } from './theme'
import {
  closeWorkspace,
  listWorkspaceDir as listDir,
  listWorkspaces,
  pickWorkspace,
  updateWorkspace
} from './workspace'
import { copyWorkspacePath, listOpenTargets, listWorkspaceRecovery, mutateWorkspaceDocument as mutateWorkspaceFile, openWorkspaceFileWith, readWorkspaceDocument as readWorkspaceFile, revealWorkspaceDocument as revealWorkspaceFile, saveWorkspaceFileAs, writeWorkspaceDocument as writeWorkspaceFile } from './workspace-files'
import { getReviewChangeSet, getReviewFileDiff, precheckReviewUndo, redoReviewChangeSet, undoReviewChangeSet } from './review'
import { forgetFileIndex, searchWorkspaceFiles } from './workspace-search'
import { compactContext, previewContext } from './context'
import { widgetComplete, widgetGetState, widgetRunAction, widgetSaveFile, widgetSetState, widgetSubmit } from './widget'
import {
  branchSession,
  createSession,
  createSkillExtractionSession,
  deleteReply,
  deleteTurn,
  duplicateSession,
  deleteSession,
  editMessage,
  getLastAssistant,
  getSession,
  getSessionPage,
  getSessionSummary,
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
  getStatsAsync,
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

/**
 * 输入框补全。依赖在这里注入,`composer.ts` 本身不碰 electron / runtime(便于单测)。
 * 生成器**现取**:换账号作用域时 runtime 会丢掉旧单例,这里不能抓住一个过期的。
 */
const composer = createComposerIpc({
  settings: () => store.getSettings(),
  session: (id) => store.getSession(id),
  hasActiveGoal: (id) => getActiveGoal(id) !== undefined,
  recentSessionPrompts: (id, limit) => store.recentSessionPrompts(id, limit),
  lastVisibleAssistantMessage: (id) => store.lastVisibleAssistantMessage(id),
  recentWorkspacePrompts: (id) => store.recentWorkspacePrompts(id),
  suggest: (input, signal) => getPromptSuggestionGenerator().suggest(input, signal),
  complete: (input, signal) => getInlineCompletionService().complete(input, signal),
  logger: { warn: (msg) => getHost().logger.warn(msg) }
})

const handlers: HandlerMap = {
  // ── 已实现 ──
  'app:getBootstrap': (_req, ctx) => {
    // A renderer reload can arrive before the tab/input debounce expires.
    /*
      ★ **这一段的失败只影响一句提示,不能顶掉首屏。** 刷不下去(磁盘满、没写权限)
      是「有草稿 / 布局还没保住」,而不是「读不到库」—— 把后者也变成首屏失败
      会把一次可恢复的写入问题说成应用起不来。真读库的异常在下面那两行照旧抛,
      由渲染层的握手失败分支显示:两者的用户动作完全不同。
    */
    let persistenceWarning = false
    try {
      flushPendingPersists()
    } catch (error) {
      persistenceWarning = true
      console.warn('[ipc] pending UI state could not be flushed before bootstrap', error)
    }
    initializeThemeLibrary()
    const boot = getBootstrap(ctx.kind)
    // 只在真的没刷成功时才带上这个字段 —— 渲染层据此弹一条持久错误 toast
    return persistenceWarning ? { ...boot, persistenceWarning } : boot
  },
  'app:openExternal': ({ url }) => openExternal(url),
  'app:checkForUpdates': () => checkForUpdates(),
  'app:updateCheck': () => updateService.check(),
  'app:updateDownload': () => updateService.download(),
  'app:updateInstall': () => updateService.install(),
  'app:updateGetState': () => updateService.getState(),
  'app:copyText': ({ text }) => copyText(text),
  'device:getState': () => getDevicesState(),
  'device:inspectInvitation': (req) => inspectDeviceInvitation(req),
  'device:pair': (req) => pairDevice(req),
  'device:select': (req) => selectDevice(req),
  'device:useLocal': () => useLocalDevice(),
  'device:remove': (req) => removeDevice(req),
  'device:setRelay': (req) => setDeviceRelay(req),
  'app:copyImage': ({ base64 }) => copyImage(base64),
  'app:saveTextFile': (req) => saveTextFile(req),
  'app:saveImageFile': (req) => saveImageFile(req),
  'app:openSessionWindow': (req) => openSessionWindow(req),
  'clientAuth:getState': () => getClientAuthState(),
  'clientAuth:startLogin': () => startClientLogin(),
  'clientAuth:selectTeam': (req) => selectClientTeam(req.teamId),
  'clientAuth:useOffline': () => useOffline(),
  'clientAuth:signOut': () => signOutClient(),
  'clientAuth:getUser': () => getClientUser(),
  'clientAuth:getUsage': (req) => getClientUsage(req),
  'referral:get': () => getReferralCenter(),
  'recharge:getOptions': () => getRechargeOptions(),
  'recharge:checkout': (req) => createRechargeCheckout(req.amount),
  'recharge:getOrder': (req) => getRechargeOrder(req.orderNo),
  'configSync:getStatus': () => getConfigSyncStatus(),
  'configSync:setup': (req) => setupConfigSync(req),
  'configSync:getConflicts': () => getConfigSyncConflicts(),
  'configSync:getPreview': () => getConfigSyncPreview(),
  'configSync:confirmInitial': () => confirmInitialConfigSync(),
  'configSync:resolve': (req) => resolveConfigSyncConflict(req.id, req.useRemote),
  'configSync:setUsage': ({ enabled }) => setConfigSyncUsage(enabled),
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
  'workspace:listOpenTargets': () => listOpenTargets(),
  'workspace:openWith': (req) => openWorkspaceFileWith(req),
  'workspace:copyPath': (req) => copyWorkspacePath(req),
  'workspace:saveFileAs': (req) => saveWorkspaceFileAs(req),
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
  'git:getDiffSides': (req) => getGitDiffSides(req),
  'git:stage': (req) => stageGitPaths(req),
  'git:unstage': (req) => unstageGitPaths(req),
  'git:commit': (req) => commitGit(req),
  'git:checkoutBranch': (req) => checkoutGitBranch(req),
  'git:createBranch': (req) => createGitBranch(req),
  'git:pull': (req) => pullGit(req),
  'git:push': (req) => pushGit(req),
  'git:generateCommitMessage': (req) => generateGitCommitMessage(req),
  'composer:suggestNext': (req, ctx) => composer.suggestNext(req, ctx),
  'composer:history': (req) => composer.history(req),
  'composer:complete': (req, ctx) => composer.complete(req, ctx),
  'composer:cancel': (req, ctx) => composer.cancel(req, ctx),
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
  'session:getInput': ({ sessionId }) => sessionRuntime.getInput(sessionId),
  'session:queue': (req) => queueSessionInput(req),
  'session:reportBackground': (req) => reportBackgroundChild(req),

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
  'sessions:getPage': (req) => getSessionPage(req),
  'sessions:getSummary': (req) => getSessionSummary(req),
  'sessions:lastAssistant': (req) => getLastAssistant(req),
  'sessions:editMessage': (req) => editMessage(req),
  'sessions:deleteTurn': (req) => deleteTurn(req),
  'sessions:deleteReply': (req) => deleteReply(req),
  'sessions:create': (req) => createSession(req),
  'sessions:duplicate': (req) => duplicateSession(req),
  'sessions:branch': (req) => branchSession(req),
  'sessions:createSkillExtraction': (req) => createSkillExtractionSession(req),
  'sessions:rename': (req) => renameSession(req),
  'sessions:setMode': (req) => setMode(req),
  'sessions:setModel': (req) => setModel(req),
  'sessions:setArchived': (req) => setArchived(req),
  'sessions:setFavorited': (req) => setFavorited(req),
  'sessions:delete': (req) => deleteSession(req),
  'conversations:searchAll': (req) => searchAll(req),
  'context:compact': (req) => compactContext(req),
  'context:preview': (req) => previewContext(req),
  'storage:getStats': () => getStatsAsync(),
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
  'agent:unwatch': (req, ctx) => unwatchRun(req, ctx),
  'agent:abort': (req) => abortRun(req),
  'agent:interject': (req, ctx) => interjectRun(req, ctx),
  'agent:setPermissionMode': (req, ctx) => setRunPermissionMode(req, ctx),
  'agent:respondInteraction': respondInteraction,
  'agent:listInteractions': listInteractions,
  'widget:getState': (req) => widgetGetState(req),
  'widget:setState': (req) => { widgetSetState(req) },
  'widget:submit': (req, ctx) => { widgetSubmit(req, ctx) },
  'widget:runAction': (req) => widgetRunAction(req),
  'widget:complete': (req) => widgetComplete(req),
  'widget:saveFile': (req) => widgetSaveFile(req),
  'agent:listTools': ({ workspaceId }) => {
    if (store.getWorkspace(workspaceId) === undefined) throw new Error('Workspace does not exist')
    return getTools().info()
  },
  // ── Agent 的 shell:只停这一条命令,run 继续 ──
  'shell:stopToolCall': (req, ctx) => stopToolCall(req, ctx),
  'shell:backgroundToolCall': (req, ctx) => backgroundToolCall(req, ctx),
  // ── 后台 shell:右上角活动面板。按 id 操作,与模型的 BashOutput / KillShell 同一张表 ──
  'shell:listBackground': ({ workspaceId }) => agentShells.list().filter((shell) => shell.workspaceId === workspaceId),
  'shell:peekBackground': ({ id }) => agentShells.peek(id) ?? null,
  'shell:killBackground': ({ id }) => agentShells.peek(id) === undefined ? null : agentShells.kill(id),
  'agent:backgroundSubagent': (req, ctx) => backgroundSubagent(req, ctx),
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
  // ── 文档引擎编辑器画布(见 `./document-engine.ts`)──
  'documentEngine:open': (req, ctx) => openDocumentView(req, ctx),
  'documentEngine:render': (req, ctx) => renderDocumentView(req, ctx),
  'documentEngine:input': (req, ctx) => inputDocumentView(req, ctx),
  'documentEngine:command': (req, ctx) => commandDocumentView(req, ctx),
  'documentEngine:list': (req, ctx) => listDocumentView(req, ctx),
  'documentEngine:headers': (req, ctx) => headersDocumentView(req, ctx),
  'documentEngine:layout': (req, ctx) => layoutDocumentView(req, ctx),
  'documentEngine:state': (req, ctx) => documentViewState(req, ctx),
  'documentEngine:save': (req, ctx) => saveDocumentView(req, ctx),
  'documentEngine:close': (req, ctx) => closeDocumentView(req, ctx),

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
  // ── 供应商账号(OAuth 多账号,schema 第 24 条) ──
  // ★ 写频道一律回整份列表,渲染层因此不需要自己合并(见 contract 那段注释)
  'provider:listAccounts': ({ providerId }) => listProviderAccounts(providerId),
  'provider:addAccount': ({ providerId }) => addOrReauthAccount(providerId),
  'provider:reauthAccount': ({ providerId, accountId }) => addOrReauthAccount(providerId, accountId),
  'provider:removeAccount': ({ providerId, accountId }) => removeProviderAccount(providerId, accountId),
  'provider:setAccountEnabled': ({ providerId, accountId, enabled }) =>
    setProviderAccountEnabled(providerId, accountId, enabled),
  'provider:setAccountLabel': ({ providerId, accountId, label }) =>
    setProviderAccountLabel(providerId, accountId, label),
  'provider:setCurrentAccount': ({ providerId, accountId }) =>
    setCurrentProviderAccount(providerId, accountId),
  'provider:reorderAccounts': ({ providerId, accountIds }) =>
    reorderProviderAccounts(providerId, accountIds),
  'provider:clearAccountLimit': ({ providerId, accountId }) =>
    clearProviderAccountLimit(providerId, accountId),
  'provider:fetchQuota': ({ providerId, accountId }) =>
    fetchProviderAccountQuota(providerId, accountId),
  // Agent 上游已支持三种协议;独立连接测试入口仍待接入。
  'provider:test': todo('provider:test', '步骤 13(独立连接测试入口)'),
  'model:update': (req) => updateModel(req),
  'model:rename': ({ providerId, alias, nextAlias }) => renameModel(providerId, alias, nextAlias),
  'model:remove': ({ providerId, alias }) => removeModel(providerId, alias),
  // ── 视频生成任务(见 contract 里那段:它不走 agent:event)──
  'video:listBySession': (req) => listVideoJobs(req),
  'video:get': (req) => getVideoJob(req),
  'video:cancel': (req) => cancelVideoJob(req),
  'video:retryRetrieval': (req) => retryVideoRetrieval(req),
  'video:saveFile': (req) => saveVideoJobFile(req),
  'pricing:listSynced': () => listSyncedPricing(),
  'pricing:syncPreview': () => previewPricingSync(),
  'pricing:syncApply': ({ modelIds }) => applyPricingSync(modelIds),
  'pricing:clearSynced': () => clearSyncedPricing(),
  'modelCatalog:list': () => listUserModelCatalog(),
  'modelCatalog:syncPreview': () => previewModelCatalogSync(),
  'modelCatalog:syncApply': ({ ids }) => applyModelCatalogSync(ids),
  'modelCatalog:upsert': (req) => upsertUserModelCatalog(req),
  'modelCatalog:remove': ({ id }) => removeUserModelCatalog(id),
  'usage:getSummary': (window) => store.getUsageSummary(window),
  'usage:getRequestLogs': (query) => store.getUsageRequestLogs(query),
  'usage:getSessionAttempts': ({ sessionId }) => store.getSessionUsageAttempts(sessionId),
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
 * Flush before reads and app quit so pending UI state survives renderer reloads.
 *
 * ★ **每个键独立成败。** 一条写不进去(磁盘满、目录没写权限)不能把后面那些
 * 还等着落盘的草稿一起连坐 —— 它们各自的写入面是独立的,而「第一条失败就
 * 中断」的实际后果是:用户退出应用时,排在坏键后面的**全部** Tab 布局与草稿
 * 一起丢了。所以这里逐个 `try`,**只有写成功才 delete**。
 *
 * ★ 失败**照旧抛给调用方**(单个可原错误,多条 AggregateError):显式保存 /
 * 退出这条路上,「没写成功」必须让上层知道 —— 静默吞掉等于把一次丢数据
 * 描述成成功。留在 `pendingPersists` 里的那些键都是没落盘的,退出流程重试
 * 或者下一次读同一把键时的 flush 还能再试一次。
 */
export function flushPendingPersists(onlyKey?: string): void {
  const errors: unknown[] = []
  for (const [key, { timer, value }] of pendingPersists) {
    if (onlyKey !== undefined && key !== onlyKey) continue
    clearTimeout(timer)
    try {
      store.setKv(key, value)
    } catch (error) {
      // 定时器已经清了,这个键得留在表里 —— 它此刻是这份值唯一的去处
      errors.push(error)
      continue
    }
    pendingPersists.delete(key)
  }
  // 草稿的防抖住在会话运行时里(它和队列共用一个键,必须整份写)。
  // 它自己已经做到「先试所有条目再抛」,这里的失败同样不能挡住上面那些断言。
  if (onlyKey === undefined) {
    try {
      sessionRuntime.flush()
    } catch (error) {
      errors.push(error)
    }
  }
  if (errors.length === 1) throw errors[0]
  if (errors.length > 1) throw new AggregateError(errors, 'Some pending UI state could not be persisted')
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
  'session:persistDraft': ({ sessionId, draft, immediate }) =>
    sessionRuntime.setDraft(sessionId, draft, immediate),

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
      // 选了远程设备时,device 类频道在这里被转发或拒绝(绝不悄悄改在本机跑);没选则原样本机处理。
      const ctx = windows.of(event.sender)
      const data = await routeInvoke(channel, req as InvokeReq<K>, () => fn(req as InvokeReq<K>, ctx), ctx)
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

/** send 没有回程信封可以报错，所以每条监听都必须在主进程侧兜住异常。 */
function safeListen<K extends SendChannel>(channel: K, fn: SendHandler<K>): void {
  ipcMain.on(channel, (event, payload) => {
    try {
      // 选了远程设备时,device 类 send 频道在这里转给设备或丢弃(不落到本机的同 id 终端)。
      const ctx = windows.of(event.sender)
      routeSend(channel, payload as IpcSendMap[K], () => fn(payload, ctx))
    } catch (err) {
      console.error(`[ipc] send ${channel} 失败:`, err)
    }
  })
}

/**
 * 只登记启动界面所需的迁移频道与窗口控制。
 *
 * ★ **它必须能在 `registerIpc()` 之前单独调用。** 闸门存在的那段时间里数据库还
 * 没打开,而 `registerIpc()` 会拉起导入服务、扫孤儿文件、迁移主题目录 —— 那些
 * 全都要库。所以启动路径上先调这一个,闸门放行之后才调完整的 `registerIpc()`。
 *
 * ★ 这些 handler 都不碰库（迁移约束见 `./data-migration`，窗口控制只操作当前窗口），
 * 所以「先登记它们」本身是安全的。
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

/*
  需求：Windows/Linux 在完整 IPC 就绪前也会显示启动骨架或诊断页；它们仍处于无标题栏
  窗口中，所以窗口控制必须和迁移频道一起提前可用，否则错误页连关闭按钮都点不动。
*/
const STARTUP_SEND_CHANNELS = ['window:control'] as const satisfies readonly SendChannel[]
const STARTUP_SEND_CHANNEL_SET: ReadonlySet<string> = new Set<string>(STARTUP_SEND_CHANNELS)

/**
 * 只登记启动界面所需的迁移频道与窗口控制。
 *
 * ★ **它必须能在 `registerIpc()` 之前单独调用。** 闸门存在的那段时间里数据库还
 * 没打开,而 `registerIpc()` 会拉起导入服务、扫孤儿文件、迁移主题目录 —— 那些
 * 全都要库。所以启动路径上先调这一个,闸门放行之后才调完整的 `registerIpc()`。
 *
 * ★ 这些 handler 都不碰库（迁移约束见 `./data-migration`，窗口控制只操作当前窗口），
 * 所以「先登记它们」本身是安全的。
 */
export function registerMigrationIpc(): void {
  for (const channel of MIGRATION_CHANNELS) {
    safeHandle(channel, handlers[channel] as Handler<InvokeChannel>)
  }
  for (const channel of STARTUP_SEND_CHANNELS) {
    safeListen(channel, sendHandlers[channel] as SendHandler<SendChannel>)
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
    // 启动页需要的窗口控制已经提前登记；重复监听会让一次点击执行两次。
    if (STARTUP_SEND_CHANNEL_SET.has(channel)) continue
    safeListen(channel, sendHandlers[channel] as SendHandler<SendChannel>)
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
    ★ 后台子代理的结果回传、目标的空闲检查:都由主进程的会话运行时直接投递,
    不再转交给「恰好在看这条会话的那个窗口」—— 没人在看时它们也得发生。
  */
  installBackgroundChildHandler((report, status) => sessionRuntime.childFinished(report, status))
  setGoalLauncher((sessionId, parts, options, goalId) => sessionRuntime.wakeGoal(sessionId, parts, options, goalId))
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
  // 提炼会话写完 SKILL.md → 在该工作区自动启用。失败只记日志:文件已经写好,
  // 用户仍可以去 Skill 页手动打开,不值得为此让 run 收尾报错。
  setSkillWrittenListener(async (change) => {
    try {
      await activateWrittenSkills(change)
    } catch (error: unknown) {
      console.warn('[skills] 提炼会话写入的 Skill 自动启用失败', error)
    }
  })
  // 刷新 token 之后（含刷失败标记 needsReauth）把新的登录态推给设置页
  setCredentialChangeListener(announceCredentialRef)
  /*
    账号被限流落闸 / 闸门解除 / 额度快照更新之后,把整份账号列表推给设置页。

    ★ 这条和上面那条是两件事:上面那条由凭证刷新触发,这条由**一次上游请求失败**
    触发。少了它,用户要关掉再打开设置页才知道「为什么刚才换号了」——
    而那时闸门可能已经自己到期,他什么都看不到。
  */
  setAccountsChangeListener(announceAccountsSafe)
  /*
    视频任务状态变化的广播口。★ 和上面 `setMcpChangeListener` / `setSessionChangeListener`
    是同一种接线:视频 manager 活在 runtime 里,而广播要 `windows.emitToAll`
    (`ipc/video.ts` 拖着 electron)—— runtime 静态 import 它就会破那条
    「零 electron import」铁律、断掉无头链路(见 runtime.ts 文件头)。
  */
  setVideoJobChangeListener(announceVideoJob)
  /*
    定时任务写入后的重排口。★ 同一种接线:方向必须是「ipc 依赖 runtime」——
    `scheduler.ts` import 了 `runAgent`(runtime),若让 `scheduled/bridge.ts` 直接
    import `scheduler.ts`,就会冒出 `bridge → scheduler → runtime → bridge` 的值环。
    装在这里之后 bridge 只认 `scheduled/refresh.ts` 那个叶子插槽。
  */
  setSchedulerRefresh(refreshScheduler)
  /*
    定时任务写入后的**广播**口。★ 同一种接线、同一条理由(见 `scheduled/refresh.ts`
    的文件头):`bridge.ts` 是内核侧要能在无头 Node 里跑的叶子,而广播要
    `windows.emitToAll` —— 那个模块拖着 electron,所以由这一侧装进去。
  */
  setScheduledChangeListener((change) => windows.emitToAll('scheduled:changed', change))
  // 同一种接线:`agent-shells.ts` 不认识窗口,广播由这一侧装进去
  agentShells.onChange(() => windows.emitToAll('shell:backgroundChanged', { shells: agentShells.list() }))

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
export { setWindowContentVisible, shutdownRuns } from './agent'
export {
  prepareStoredAccountScope,
  reconcileMigratedWorkspacesForStoredAccount,
  shutdownClientAuth
} from './client-auth'
export { shutdownTerminals }
