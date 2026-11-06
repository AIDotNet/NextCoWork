/**
 * 配置/会话导出、备份和清理共用的领域类型与纯校验函数。
 *
 * 这些类型刻意不包含 Electron、SQLite 或文件系统对象，方便主进程单测和
 * 渲染层预览共用。敏感凭证只允许出现在 encryptedCredentials 字段中，普通
 * JSON 的类型里没有明文凭证字段。
 */
import type { AgentMessage } from '../agent/message'
import type { ModelAlias, UpstreamProvider } from './provider'
import { isModelCatalogOverride } from './provider'
import { isMaxOutputTokens, isShellPreference, isUpstreamIdleTimeoutSeconds, MODEL_PROPOSED_GOALS, type AppSettings } from './settings'
import type { SearchProviderConfig } from './search'
import type { Session } from './session'
import type { Workspace } from './workspace'
import type { McpServerConfig } from './mcp'
import { PERMISSION_MODES } from '../agent/permission'
import { THINKING_LEVELS } from '../agent/run-request'
import { MODE_ID_RE } from './mode'
import { PROXY_SCHEMES } from './proxy'
import { isOpenTargetPreference } from './open-target'
import { isSubagentThinking } from './subagent-thinking'

export const DATA_EXPORT_TYPE = 'nextcowork-data-export' as const
export const DATA_EXPORT_VERSION = 1
export const BACKUP_FORMAT_VERSION = 2

export interface ExportSession {
  session: Session
  messages: AgentMessage[]
  /**
   * 旧版导出里的上下文检查点。★ 只为能导入老备份而保留在类型上,导入时忽略:
   * 压缩现在是转录里的一条边界消息(`compact_boundary`),随 `messages` 一起走。
   */
  contextCheckpoints?: unknown[]
}

export interface DataExport {
  type: typeof DATA_EXPORT_TYPE
  version: number
  /** ISO string in current exports; numeric timestamps are accepted from legacy exports. */
  exportedAt: string | number
  /** Legacy database schema marker, retained for import compatibility. */
  schemaVersion?: number
  settings: AppSettings
  workspaces: Workspace[]
  sessions: ExportSession[]
  providers: UpstreamProvider[]
  aliases: ModelAlias[]
  /**
   * OAuth 供应商的账号**元数据**(schema 第 24 条)。
   *
   * ★★ **这里没有任何密文** —— token 和其它凭证一样住 `encryptedCredentials`,
   * 键是 `provider:<id>#<accountId>`。这一段只是「有哪几个账号、排第几、
   * 停用没、备注名叫什么」。
   *
   * ★ **可选**:缺席 = 这份导出早于多账号(或者导出时一个 OAuth 账号都没有)。
   *   判成非法的表现是**整份旧备份导不进来**,而错误信息只会说「文件格式不对」。
   *
   * ★ 限流闸门与额度快照**不导出**(见 `ProviderAccountExport` 的字段列表):
   *   它们是「此时此地这台机器观察到的上游状态」,几小时后就不成立了。
   *   带过去的表现是:刚导入的另一台机器上,一个好端端的账号显示「限流中」,
   *   而用户完全无从知道那是一份从别处搬来的旧判断。
   */
  providerAccounts?: ProviderAccountExport[]
  mcpServers: McpServerConfig[]
  searchProviders: SearchProviderConfig[]
  /** IDs of globally disabled skills; skill files themselves are never exported. */
  disabledSkillIds: string[]
  /** Workspace-local skill selection is already part of each Workspace. */
  encryptedCredentials?: EncryptedCredentials | boolean
  /** Legacy user supplied model catalogue, ignored by current import code. */
  userModelCatalog?: unknown
}

/** 见 `DataExport.providerAccounts`:元数据,无密文、无限流、无额度 */
export interface ProviderAccountExport {
  id: string
  providerId: string
  issuer: string
  label?: string
  order: number
  enabled: boolean
  current: boolean
  updatedAt: number
}

export interface EncryptedCredentials {
  algorithm: 'scrypt-aes-256-gcm'
  salt: string
  nonce: string
  tag: string
  /** base64-encoded encrypted JSON map */
  ciphertext: string
}

export interface BackupManifest {
  format: 'nextcowork-backup'
  formatVersion: number
  appVersion: string
  createdAt: number
  /** SQLite PRAGMA user_version，用于阻止把未来版本数据库直接替换进来。 */
  schemaVersion: number
  databaseSha256: string
  sessionCount: number
  messageCount: number
  encryptedCredentials: boolean
  /** v2:存在 NCK1 密文时必须有,用于把数据库与它唯一对应的主密钥绑定成一个恢复单元。 */
  credentialKeySha256?: string
}

export interface BackupStatus {
  directory: string | null
  lastBackupAt: number | null
  lastBackupPath: string | null
  lastError: string | null
  running: boolean
}

export interface ImportPreview {
  path: string
  version: number
  workspaceCount: number
  sessionCount: number
  messageCount: number
  providerCount: number
  aliasCount: number
  mcpServerCount: number
  skippedCount: number
  overwriteCount: number
  newCount: number
  hasEncryptedCredentials: boolean
}

export interface ImportApplyResult {
  imported: number
  overwritten: number
  skipped: number
  settingsImported: boolean
  workspacesImported: number
  sessionsImported: number
  messagesImported: number
}

/**
 * Decide how one imported record should be merged with the local record.
 *
 * The export format intentionally keeps `updatedAt` optional for records that
 * predate the timestamp field.  When either side cannot provide a finite
 * timestamp, the safe rule is to keep the local value.  Keeping this decision
 * in the shared domain layer makes the preview counts and the SQLite merge
 * use exactly the same conflict semantics.
 */
export type DataMergeDecision = 'new' | 'overwrite' | 'skip'

export function dataMergeDecision(local: unknown, incoming: unknown): DataMergeDecision {
  if (local === undefined) return 'new'
  const localAt = typeof local === 'object' && local !== null
    ? (local as Record<string, unknown>)['updatedAt']
    : undefined
  const incomingAt = typeof incoming === 'object' && incoming !== null
    ? (incoming as Record<string, unknown>)['updatedAt']
    : undefined
  if (
    typeof localAt === 'number' && Number.isFinite(localAt) &&
    typeof incomingAt === 'number' && Number.isFinite(incomingAt) &&
    incomingAt > localAt
  ) return 'overwrite'
  return 'skip'
}

export interface RestorePreview {
  path: string
  manifest: BackupManifest
  sessionCount: number
  messageCount: number
  settingsIncluded: boolean
}

export interface RestoreResult {
  restored: boolean
  preview?: RestorePreview
  backupStatus?: BackupStatus
}

export interface CleanupPreview {
  kind: 'attachments' | 'age' | 'history' | 'local-data'
  sessionCount: number
  messageCount: number
  attachmentCount: number
  bytes: number
  undeletable: string[]
}

export interface CleanupResult extends CleanupPreview {
  deleted: number
}

/** `'7d'` / `'15d'` 表示天数；数字表示日历月数。 */
export type CleanupAge = '7d' | '15d' | 1 | 3 | 6 | 12

/** 截止时间：当前时刻往前 N 天，或往前 N 个月（按日历月而不是固定 90 天）。 */
export function cutoffForAge(now: number, age: CleanupAge): number {
  const date = new Date(now)
  if (typeof age === 'string') {
    // 按日历日回退，跨夏令时也保留同一时分秒。
    date.setDate(date.getDate() - (age === '7d' ? 7 : 15))
    return date.getTime()
  }
  const months = age
  const day = date.getDate()
  // Move through day 1 first. Native Date#setMonth overflows at month ends
  // (May 31 - 3 months becomes March 3), which would delete several extra
  // days. Clamp back to the last real day of the target calendar month.
  date.setDate(1)
  date.setMonth(date.getMonth() - months)
  const lastDay = new Date(
    date.getFullYear(),
    date.getMonth() + 1,
    0,
    date.getHours(),
    date.getMinutes(),
    date.getSeconds(),
    date.getMilliseconds()
  ).getDate()
  date.setDate(Math.min(day, lastDay))
  return date.getTime()
}

/** 对可导出的 JSON 进行结构化 schema 检查。 */
export function isDataExport(value: unknown): value is DataExport {
  if (typeof value !== 'object' || value === null) return false
  const v = value as Record<string, unknown>
  if (
    v.type !== DATA_EXPORT_TYPE ||
    !isIntegerAtLeast(v.version, 1) ||
    !((typeof v.exportedAt === 'string' && Number.isFinite(Date.parse(v.exportedAt))) ||
      (typeof v.exportedAt === 'number' && Number.isFinite(v.exportedAt))) ||
    !isAppSettings(v.settings) ||
    !Array.isArray(v.workspaces) ||
    !Array.isArray(v.sessions) ||
    !Array.isArray(v.providers) ||
    !Array.isArray(v.aliases) ||
    !Array.isArray(v.mcpServers) ||
    !Array.isArray(v.searchProviders) ||
    !Array.isArray(v.disabledSkillIds) ||
    !v.disabledSkillIds.every(isNonEmptyString) ||
    (v.encryptedCredentials !== undefined &&
      typeof v.encryptedCredentials !== 'boolean' && !isEncryptedCredentials(v.encryptedCredentials))
  ) return false

  // Duplicate primary keys make a merge order-dependent. Reject them before
  // anything reaches SQLite, while still allowing unknown future fields on
  // each record.
  const workspaces = v.workspaces as unknown[]
  const sessions = v.sessions as unknown[]
  const providers = v.providers as unknown[]
  const aliases = v.aliases as unknown[]
  const mcpServers = v.mcpServers as unknown[]
  const searchProviders = v.searchProviders as unknown[]
  if (!workspaces.every(isWorkspace) || !uniqueBy(workspaces, (x) => (x as Workspace).id)) return false
  if (!sessions.every(isExportSession) || !uniqueBy(sessions, (x) => (x as { session: Session }).session.id)) return false
  if (!providers.every(isProvider) || !uniqueBy(providers, (x) => (x as UpstreamProvider).id)) return false
  if (!aliases.every(isModelAlias) || !uniqueBy(aliases, (x) => `${(x as ModelAlias).providerId}\u0000${(x as ModelAlias).alias}`)) return false
  if (!mcpServers.every(isMcpServer) || !uniqueBy(mcpServers, (x) => (x as McpServerConfig).id)) return false
  if (!searchProviders.every(isSearchProvider) || !uniqueBy(searchProviders, (x) => (x as SearchProviderConfig).id)) return false
  /*
    ★ 账号是**后加的可选段**:缺席 = 旧备份,照常放行(见 `DataExport.providerAccounts`)。
    在场就必须逐条合法且 id 不重复 —— 重复 id 会让合并变成顺序相关的,
    而那种错只在「同一个 id 的两条记录内容不同」时才显形。
  */
  if (has(v, 'providerAccounts')) {
    if (!Array.isArray(v.providerAccounts)) return false
    const accounts = v.providerAccounts as unknown[]
    if (!accounts.every(isProviderAccountExport)) return false
    if (!uniqueBy(accounts, (x) => (x as ProviderAccountExport).id)) return false
  }
  return true
}

/** 见 `DataExport.providerAccounts`。★ 宽容未知字段(将来加列不该让旧客户端拒收整份文件) */
function isProviderAccountExport(value: unknown): value is ProviderAccountExport {
  if (!isRecord(value)) return false
  return (
    isNonEmptyString(value.id) &&
    isNonEmptyString(value.providerId) &&
    isNonEmptyString(value.issuer) &&
    optionalString(value, 'label') &&
    isFiniteNumber(value.order) &&
    isBoolean(value.enabled) &&
    isBoolean(value.current) &&
    isFiniteNumber(value.updatedAt)
  )
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0
}

function isModeId(value: unknown): value is string {
  return typeof value === 'string' && MODE_ID_RE.test(value.trim().toLowerCase())
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function isIntegerAtLeast(value: unknown, minimum: number): value is number {
  return isFiniteNumber(value) && Number.isInteger(value) && value >= minimum
}

function isBoolean(value: unknown): value is boolean {
  return typeof value === 'boolean'
}

function has(value: Record<string, unknown>, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(value, key)
}

function optionalString(value: Record<string, unknown>, key: string): boolean {
  return !has(value, key) || typeof value[key] === 'string'
}

function optionalBoolean(value: Record<string, unknown>, key: string): boolean {
  return !has(value, key) || isBoolean(value[key])
}

function optionalIntegerAtLeast(
  value: Record<string, unknown>,
  key: string,
  minimum: number,
): boolean {
  return !has(value, key) || isIntegerAtLeast(value[key], minimum)
}

function stringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
}

function enumValue<T extends string>(value: unknown, values: readonly T[]): value is T {
  return typeof value === 'string' && values.includes(value as T)
}

function isAppSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  const v = value
  // `data` was added after the first settings format. It is deliberately
  // optional here so old exports can still be imported and merged with the
  // current defaults.
  if (
    !enumValue(v.theme, ['system', 'light', 'dark']) ||
    !enumValue(v.locale, ['zh-CN', 'en-US']) ||
    !isColorThemeChoice(v.colorTheme) ||
    !isImageThemeChoice(v.imageTheme) ||
    !enumValue(v.defaultPermissionMode, PERMISSION_MODES) ||
    (has(v, 'permissionReviewerModel') && typeof v.permissionReviewerModel !== 'string') ||
    !optionalString(v, 'permissionReviewerModelProviderId') ||
    // ★ 目标判定那一对和 `modelProposedGoals` 都是后加的:缺席 = 旧存档,必须放行,
    //   否则每一份改动之前的导出都会在导入时被整份拒绝。
    (has(v, 'goalEvaluatorModel') && typeof v.goalEvaluatorModel !== 'string') ||
    !optionalString(v, 'goalEvaluatorModelProviderId') ||
    (has(v, 'modelProposedGoals') && !enumValue(v.modelProposedGoals, MODEL_PROPOSED_GOALS)) ||
    // ★ 压缩模型那一对和档位同理:缺席 = 这份导出早于这两栏,必须放行并与默认值合并
    //   (默认就是「跟随会话模型 / 跟随会话档位」,也就是这两栏出现之前的行为)。
    (has(v, 'compactModel') && typeof v.compactModel !== 'string') ||
    !optionalString(v, 'compactModelProviderId') ||
    (has(v, 'compactThinking') && !isSubagentThinking(v.compactThinking)) ||
    // ★ 输入框补全那五项同理:缺席 = 旧导出,放行并与默认值合并;在场就必须类型正确
    (has(v, 'promptSuggestions') && typeof v.promptSuggestions !== 'boolean') ||
    (has(v, 'completionModel') && typeof v.completionModel !== 'string') ||
    !optionalString(v, 'completionModelProviderId') ||
    (has(v, 'localCompletion') && typeof v.localCompletion !== 'boolean') ||
    (has(v, 'inlineCompletion') && typeof v.inlineCompletion !== 'boolean') ||
    (has(v, 'inlineCompletionModel') && typeof v.inlineCompletionModel !== 'string') ||
    !optionalString(v, 'inlineCompletionModelProviderId') ||
    typeof v.defaultModel !== 'string' ||
    !optionalString(v, 'defaultModelProviderId') ||
    // ★ 生图模型那一对同理:缺席 = 旧导出,放行并与默认值合并(默认是「没选过」,
    //   `generate_image` 不下发,正是这一栏出现之前的行为)。
    (has(v, 'imageModel') && typeof v.imageModel !== 'string') ||
    !optionalString(v, 'imageModelProviderId') ||
    // 生图开关同理:缺席 = 旧导出,放行(默认开启);在场就必须是布尔
    (has(v, 'imageGenerationEnabled') && typeof v.imageGenerationEnabled !== 'boolean') ||
    (has(v, 'contextManagement') && !isContextManagementSettings(v.contextManagement)) ||
    // 实验块是后加的:缺席 = 旧导出,放行(默认全关);在场就必须形状正确
    (has(v, 'experimental') && !isExperimentalSettings(v.experimental)) ||
    !isSubagentSettings(v.subagent) ||
    !isGatewaySettings(v.gateway) ||
    !isNotificationSettings(v.notifications) ||
    !isProxySettings(v.proxy)
  ) return false
  if (has(v, 'data') && !isDataSettings(v.data)) return false
  // `shell` is newer than the first settings format, so a missing value must
  // stay importable — legacy exports are merged with the current defaults.
  // When it is present it has to be a known shell: an unknown one would only
  // be discarded by the merger, so silently accepting it hides a bad export.
  if (has(v, 'shell') && !isShellPreference(v.shell)) return false
  // 默认打开方式同理:缺席 = 旧存档放行,在场就必须是合法形状。
  if (has(v, 'defaultOpenTarget') && !isOpenTargetPreference(v.defaultOpenTarget)) return false
  // 后加的字段:缺席 = 旧存档,放行(和 default defaults 合并);在场就必须在范围内,
  // 否则会被 merger 丢弃 —— 静默接受一个坏值会掩盖一份损坏的导出。
  if (has(v, 'upstreamIdleTimeoutSeconds') &&
      !isUpstreamIdleTimeoutSeconds(v.upstreamIdleTimeoutSeconds)) return false
  if (has(v, 'maxOutputTokens') && !isMaxOutputTokens(v.maxOutputTokens)) return false
  // 账号轮换开关同理:缺席 = 这份导出早于多账号,放行并回落到默认(开启)。
  if (has(v, 'providerAccountRotation') && typeof v.providerAccountRotation !== 'boolean') return false
  if (has(v, 'themeStudio') && !isThemeStudioSettings(v.themeStudio)) return false
  if (has(v, 'activeThemeProfileId') && v.activeThemeProfileId !== null &&
      (typeof v.activeThemeProfileId !== 'string' || !/^[a-zA-Z0-9_-]{1,80}$/.test(v.activeThemeProfileId))) return false
  return true
}

function isThemeStudioSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  const v = value
  return (
    typeof v.name === 'string' &&
    (v.wallpaperAssetId === null || typeof v.wallpaperAssetId === 'string') &&
    enumValue(v.render, ['blur', 'overlay']) &&
    isFiniteNumber(v.opacity) && v.opacity >= 0 && v.opacity <= 1 &&
    isFiniteNumber(v.blur) && v.blur >= 0 && v.blur <= 120 &&
    isFiniteNumber(v.brightness) && v.brightness >= 0.4 && v.brightness <= 1.6 &&
    isFiniteNumber(v.saturation) && v.saturation >= 0 && v.saturation <= 2 &&
    isFiniteNumber(v.positionX) && v.positionX >= 0 && v.positionX <= 100 &&
    isFiniteNumber(v.positionY) && v.positionY >= 0 && v.positionY <= 100 &&
    isFiniteNumber(v.sidebarOpacity) && v.sidebarOpacity >= 0 && v.sidebarOpacity <= 1 &&
    isFiniteNumber(v.panelOpacity) && v.panelOpacity >= 0 && v.panelOpacity <= 1 &&
    isFiniteNumber(v.mask) && v.mask >= 0 && v.mask <= 1 &&
    enumValue(v.uiFont, ['system', 'system-rounded', 'system-serif']) &&
    enumValue(v.uiScale, ['small', 'standard', 'large']) &&
    enumValue(v.motion, ['standard', 'soft', 'reduced', 'off']) &&
    isBoolean(v.guardrails) &&
    isRecord(v.overrides) &&
    Object.values(v.overrides).every((c) => typeof c === 'string')
  )
}

function isExperimentalSettings(value: unknown): boolean {
  return isRecord(value) && optionalBoolean(value, 'workflow') && optionalBoolean(value, 'workflowConfirm')
}

function isContextManagementSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  /*
    ★ 只校验 `autoCompact`。`experimentalMode` 已随压缩重写删除,而**老备份里它还在** ——
    继续要求它存在的话,一份 v25 之前导出的备份会整份过不了校验,导入端拒绝的是
    整个文件而不是这一个字段(症状见 schema.ts 里 V11 那段注释)。多出来的键
    由 `mergeSettings` 按未知字段丢掉。
  */
  return isBoolean(value.autoCompact)
}

function isColorThemeChoice(value: unknown): boolean {
  if (!isRecord(value)) return false
  return isNonEmptyString(value.id) && isFiniteNumber(value.seed) && typeof value.custom === 'string'
}

function isImageThemeChoice(value: unknown): boolean {
  if (!isRecord(value)) return false
  return (value.id === null || typeof value.id === 'string') && enumValue(value.render, ['blur', 'overlay'])
}

function isSubagentSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  return (
    typeof value.model === 'string' &&
    // ★ 思考档位是后加的:缺席 = 这份导出早于这一栏,放行并回落到默认('inherit');
    //   在场就必须是已知档位 —— 坏值会被 merger 丢掉,静默接受一份坏导出只会掩盖它坏了。
    (value.thinking === undefined || isSubagentThinking(value.thinking)) &&
    optionalString(value, 'modelProviderId') &&
    isIntegerAtLeast(value.perSessionLimit, 1) &&
    isIntegerAtLeast(value.globalLimit, 0)
  )
}

function isGatewaySettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  return isBoolean(value.enabled) && isIntegerAtLeast(value.preferredPort, 0) && value.preferredPort <= 65535 && isBoolean(value.failover)
}

function isNotificationSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  return isBoolean(value.taskComplete) && isBoolean(value.permissionApproval) && isBoolean(value.planApproval)
}

function isDataSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  return (value.backupDirectory === null || typeof value.backupDirectory === 'string') && enumValue(value.backupFrequency, ['manual', 'daily', 'weekly'])
}

function isProxySettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  // Accept the pre-split `{ enabled, url }` representation. The settings
  // merger migrates it to the current shape on import/read.
  if (has(value, 'url')) {
    return typeof value.url === 'string' && optionalBoolean(value, 'enabled')
  }
  return (
    isBoolean(value.enabled) &&
    enumValue(value.mode, ['system', 'manual']) &&
    enumValue(value.scheme, PROXY_SCHEMES) &&
    typeof value.host === 'string' &&
    isIntegerAtLeast(value.port, 0) && value.port <= 65535 &&
    isBoolean(value.authEnabled) &&
    typeof value.authUser === 'string' &&
    typeof value.bypass === 'string'
  )
}

function isWorkspace(value: unknown): value is Workspace {
  if (!isRecord(value)) return false
  return (
    isNonEmptyString(value.id) &&
    typeof value.name === 'string' &&
    typeof value.rootPath === 'string' &&
    optionalBoolean(value, 'unavailable') &&
    isWorkspaceSettings(value.settings) &&
    isIntegerAtLeast(value.createdAt, 0) &&
    isIntegerAtLeast(value.lastOpenedAt, 0) &&
    optionalTimestamp(value, 'updatedAt')
  )
}

function isWorkspaceSettings(value: unknown): boolean {
  if (!isRecord(value)) return false
  return (
    enumValue(value.permissionMode, PERMISSION_MODES) &&
    typeof value.defaultModel === 'string' &&
    optionalString(value, 'defaultModelProviderId') &&
    isModeId(value.defaultMode) &&
    enumValue(value.defaultThinking, THINKING_LEVELS) &&
    isBoolean(value.webSearch) &&
    // ★ 必须是 optionalBoolean:改动之前导出的备份里没有这一项,用 isBoolean
    //   会让整份 DataExport 在导入时被拒(isWorkspaceSettings → isWorkspace → 全份失败)。
    optionalBoolean(value, 'maxContext') &&
    // 压缩覆盖那三项同理:缺席 = 旧备份,放行。★ `compactModel` 允许 `null` ——
    // 那是「显式跟随会话模型」,和「没配」是两个答案(见 workspace.ts 的三态)。
    (value.compactModel === undefined || value.compactModel === null || typeof value.compactModel === 'string') &&
    optionalString(value, 'compactModelProviderId') &&
    // `compactThinking` 的 `null` 是「改回跟随全局设置」写进来的值(浅合并清不掉键)。
    (value.compactThinking === undefined || value.compactThinking === null || isSubagentThinking(value.compactThinking)) &&
    stringArray(value.activeSkillIds) &&
    (value.skillSelectionMode === undefined || enumValue(value.skillSelectionMode, ['all', 'explicit'] as const))
  )
}

function isSession(value: unknown): value is Session {
  if (!isRecord(value)) return false
  return (
    isNonEmptyString(value.id) &&
    typeof value.workspaceId === 'string' &&
    typeof value.title === 'string' &&
    (value.titleSource === undefined || enumValue(value.titleSource, ['default', 'generated', 'manual'])) &&
    typeof value.model === 'string' &&
    optionalString(value, 'modelProviderId') &&
    isModeId(value.mode) &&
    enumValue(value.thinking, THINKING_LEVELS) &&
    typeof value.rootPathAtCreation === 'string' &&
    enumValue(value.status, ['idle', 'running']) &&
    isBoolean(value.archived) &&
    isBoolean(value.favorited) &&
    isIntegerAtLeast(value.createdAt, 0) &&
    isIntegerAtLeast(value.updatedAt, 0)
  )
}

function isExportSession(value: unknown): value is ExportSession {
  if (!isRecord(value) || !isSession(value.session) || !Array.isArray(value.messages)) return false
  const messages = value.messages as unknown[]
  if (!messages.every(isAgentMessage) || !uniqueBy(messages, (message) => (message as AgentMessage).id)) return false
  return value.contextCheckpoints === undefined || Array.isArray(value.contextCheckpoints)
}

function isAgentMessage(value: unknown): value is AgentMessage {
  if (!isRecord(value)) return false
  return (
    isNonEmptyString(value.id) &&
    enumValue(value.role, ['user', 'assistant']) &&
    Array.isArray(value.parts) &&
    value.parts.every(isContentPart) &&
    isIntegerAtLeast(value.createdAt, 0) &&
    value.schemaVersion === 1 &&
    optionalBoolean(value, 'internal')
  )
}

function isContentPart(value: unknown): boolean {
  if (!isRecord(value) || typeof value.type !== 'string') return false
  switch (value.type) {
    case 'text':
    case 'thinking':
      return typeof value.text === 'string' && (!has(value, 'opaque') || isJsonValue(value.opaque))
    case 'tool_call':
      return isNonEmptyString(value.callId) && isNonEmptyString(value.name) && isJsonValue(value.input)
    case 'tool_result':
      return isNonEmptyString(value.callId) && isToolOutput(value.output) && isBoolean(value.isError)
        && (!has(value, 'subagent') || isSubagentResult(value.subagent))
    case 'subagent':
      return isNonEmptyString(value.callId) && isNonEmptyString(value.childRunId) && optionalString(value, 'summary')
    case 'image':
      return isNonEmptyString(value.mime) && isNonEmptyString(value.dataRef)
    case 'error':
      return isAgentError(value.error)
    case 'goal_status':
      // ★ 只有 `met` 和 `condition` 是必填：一条没有条件的目标标记在恢复时
      //   会重建出一个空条件的目标，而空条件的判定器稳定地判未达成。
      return isBoolean(value.met) && typeof value.condition === 'string'
        && optionalBoolean(value, 'failed') && optionalBoolean(value, 'cleared') && optionalBoolean(value, 'set')
        && optionalString(value, 'reason')
        && (!has(value, 'id') || isNonEmptyString(value.id))
        && (!has(value, 'createdAt') || isIntegerAtLeast(value.createdAt, 0))
        && (!has(value, 'origin') || enumValue(value.origin, ['user', 'proposal_direct', 'proposal_approved', 'restored']))
        && (!has(value, 'iterations') || isIntegerAtLeast(value.iterations, 0))
        && (!has(value, 'durationMs') || isIntegerAtLeast(value.durationMs, 0))
        && (!has(value, 'tokens') || isIntegerAtLeast(value.tokens, 0))
    case 'compact_boundary':
      // ★ `summary` 必填:边界之前的历史从此不再发给模型,摘要丢了就是整段失忆。
      return enumValue(value.trigger, ['auto', 'manual'])
        && isIntegerAtLeast(value.preTokens, 0) && isIntegerAtLeast(value.postTokens, 0)
        && typeof value.summary === 'string' && optionalString(value, 'instructions')
        && (!has(value, 'restoredFiles') || (Array.isArray(value.restoredFiles) && value.restoredFiles.every((f) => typeof f === 'string')))
        // 写摘要的那个模型:后加的字段,缺席 = 这条边界压缩于它出现之前,放行。
        && (!has(value, 'summaryModel') || isCompactSummaryModel(value.summaryModel))
    default:
      return false
  }
}

/**
 * 压缩边界上「写这份摘要的是谁」。
 *
 * ★ `thinking` 只校验成字符串枚举中的一个 —— 它是**当时实际下发的档位**,是一条历史事实,
 *   不是待执行的配置;拿今天的模型声明去判它合不合法会把一份老存档整份拒掉。
 */
function isCompactSummaryModel(value: unknown): boolean {
  if (!isRecord(value)) return false
  return isNonEmptyString(value.model)
    && optionalString(value, 'modelProviderId')
    && enumValue(value.thinking, THINKING_LEVELS)
    && optionalBoolean(value, 'fellBack')
}

function isSubagentResult(value: unknown): boolean {
  if (!isRecord(value)) return false
  return isNonEmptyString(value.childRunId)
    && optionalString(value, 'summary')
    && optionalBoolean(value, 'background')
    && (!has(value, 'status') || enumValue(value.status, ['running', 'done', 'error', 'aborted']))
    && (!has(value, 'reportStatus') || enumValue(value.reportStatus, ['none', 'pending', 'injecting', 'reported', 'blocked']))
}

function isToolOutput(value: unknown): boolean {
  if (!isRecord(value)) return false
  const imagesValid = !has(value, 'images') || (
    Array.isArray(value.images) && value.images.every((image) =>
      isRecord(image) &&
      enumValue(image.mime, ['image/png', 'image/jpeg', 'image/webp', 'image/gif']) &&
      isNonEmptyString(image.dataRef) &&
      image.dataRef.startsWith(`data:${image.mime};base64,`)
    )
  )
  return typeof value.content === 'string' && imagesValid && optionalBoolean(value, 'truncated') && (!has(value, 'originalBytes') || isIntegerAtLeast(value.originalBytes, 0))
}

function isAgentError(value: unknown): boolean {
  if (!isRecord(value)) return false
  const codes = ['auth', 'rate_limit', 'context_length', 'network', 'aborted', 'tool_failed', 'provider', 'cache_unsupported', 'no_healthy_provider', 'unknown'] as const
  return (
    enumValue(value.code, codes) &&
    typeof value.message === 'string' &&
    isBoolean(value.retryable) &&
    (!has(value, 'status') || isIntegerAtLeast(value.status, 0)) &&
    (!has(value, 'retryAfterMs') || isIntegerAtLeast(value.retryAfterMs, 0))
  )
}

function isJsonValue(value: unknown): boolean {
  if (value === null || typeof value === 'string' || typeof value === 'boolean' || typeof value === 'number') {
    return typeof value !== 'number' || Number.isFinite(value)
  }
  if (Array.isArray(value)) return value.every(isJsonValue)
  if (!isRecord(value)) return false
  return Object.values(value).every(isJsonValue)
}

export function isProvider(value: unknown): value is UpstreamProvider {
  if (!isRecord(value)) return false
  if (
    !isNonEmptyString(value.id) ||
    typeof value.name !== 'string' ||
    !enumValue(value.protocol, ['anthropic', 'openai-chat', 'openai-responses']) ||
    typeof value.baseUrl !== 'string' ||
    !isNonEmptyString(value.credentialRef) ||
    !isIntegerAtLeast(value.priority, 0) ||
    !isBoolean(value.enabled) ||
    !optionalTimestamp(value, 'updatedAt')
  ) return false
  if (!has(value, 'protocolOptions')) return true
  if (!isRecord(value.protocolOptions)) return false
  if (!has(value.protocolOptions, 'anthropic')) return true
  const anthropic = value.protocolOptions.anthropic
  // Provider JSON predates protocol-specific options, and future versions may
  // add new TTL values.  Import/read paths deliberately accept a missing or
  // unknown cacheTtl and let the persistence normalizer map it (including
  // legacy `off`) to the mandatory 5m default so old exports remain importable.
  // Keep rejecting a malformed `anthropic` container itself: that is a shape
  // error, not a forward-compatible value.
  return isRecord(anthropic)
}

export function isModelAlias(value: unknown): value is ModelAlias {
  if (!isRecord(value)) return false
  if (
    !isNonEmptyString(value.alias) ||
    !isNonEmptyString(value.providerId) ||
    !isNonEmptyString(value.upstreamModel) ||
    !isCapabilities(value.capabilities) ||
    !isIntegerAtLeast(value.contextWindow, 1) ||
    !isIntegerAtLeast(value.maxOutputTokens, 1) ||
    !optionalIntegerAtLeast(value, 'priority', 0) ||
    !optionalString(value, 'displayName') ||
    !optionalTimestamp(value, 'updatedAt')
  ) return false
  if (has(value, 'modality') && !enumValue(value.modality, ['text', 'image', 'video', 'speech', 'transcription'])) return false
  if (has(value, 'enabled') && !isBoolean(value.enabled)) return false
  if (has(value, 'protocolOverride') && !enumValue(value.protocolOverride, ['anthropic', 'openai-chat', 'openai-responses'])) return false
  if (has(value, 'thinkingConfig') && !isThinkingConfig(value.thinkingConfig)) return false
  if (has(value, 'reasoningEfforts') && (!Array.isArray(value.reasoningEfforts) || !value.reasoningEfforts.every((effort) => ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].includes(String(effort))))) return false
  if (has(value, 'catalogOverrides') && (!Array.isArray(value.catalogOverrides) || !value.catalogOverrides.every(isModelCatalogOverride))) return false
  if (has(value, 'requestAdapter') && !isRequestAdapter(value.requestAdapter)) return false
  if (has(value, 'source') && !isRecord(value.source)) return false
  if (isRecord(value.source) && (!isNonEmptyString(value.source.url) || typeof value.source.fetchedAt !== 'string' || !optionalString(value.source, 'verifiedAt'))) return false
  return true
}

function isCapabilities(value: unknown): boolean {
  if (!isRecord(value)) return false
  const required = ['tools', 'vision', 'thinking', 'caching']
  return required.every((key) => isBoolean(value[key])) &&
    ['textInput', 'visionInput', 'fileInput', 'videoInput', 'audioInput', 'textOutput', 'imageOutput', 'videoOutput', 'audioOutput', 'webSearch', 'structuredOutput', 'streaming', 'batch'].every((key) => optionalBoolean(value, key))
}

function isThinkingConfig(value: unknown): boolean {
  if (!isRecord(value)) return false
  if (!enumValue(value.mode, ['unsupported', 'always', 'toggle', 'effort', 'budget']) || !isBoolean(value.defaultEnabled) || (has(value, 'defaultEffort') && !enumValue(value.defaultEffort, ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'])) || (has(value, 'defaultBudgetTokens') && !isIntegerAtLeast(value.defaultBudgetTokens, 0)) || !optionalString(value, 'parameterPath')) return false
  if (has(value, 'anthropicAdaptive') && (!isBoolean(value.anthropicAdaptive) || (value.anthropicAdaptive && value.mode !== 'effort'))) return false
  if ((has(value, 'enabledValue') && !isJsonValue(value.enabledValue)) || (has(value, 'disabledValue') && !isJsonValue(value.disabledValue))) return false
  const parameterPath = typeof value.parameterPath === 'string' ? value.parameterPath.trim() : ''
  const leaf = parameterPath.split('.').at(-1)
  if (value.mode === 'budget' && (leaf === 'enable_thinking' || leaf === 'thinking_mode' || parameterPath === 'thinking.enabled' || parameterPath === 'reasoning_split')) return false
  if (value.mode === 'toggle' && (parameterPath === 'thinking_budget' || parameterPath.endsWith('.budget_tokens'))) return false
  if (has(value, 'effortMap')) {
    if (!isRecord(value.effortMap)) return false
    for (const [effort, mapped] of Object.entries(value.effortMap)) {
      if (!['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].includes(effort) || !isJsonValue(mapped)) return false
    }
  }
  return true
}

function isRequestAdapter(value: unknown): boolean {
  if (!isRecord(value) || !enumValue(value.preset, ['auto', 'anthropic', 'openai-chat', 'openai-responses', 'custom']) || !Array.isArray(value.patches)) return false
  return value.patches.every((patch) => {
    if (!isRecord(patch) || !enumValue(patch.op, ['add', 'replace', 'remove']) || typeof patch.path !== 'string') return false
    return !has(patch, 'value') || isJsonValue(patch.value)
  })
}

function isMcpServer(value: unknown): value is McpServerConfig {
  if (!isRecord(value) || !isNonEmptyString(value.id) || typeof value.name !== 'string' || !isBoolean(value.enabled) || !optionalString(value, 'description') || !optionalTimestamp(value, 'updatedAt')) return false
  if (value.transport === 'stdio') return typeof value.command === 'string' && stringArray(value.args) && stringArray(value.envNames) && optionalString(value, 'cwd')
  if (value.transport === 'sse' || value.transport === 'streamable-http') return typeof value.url === 'string' && stringArray(value.headerNames)
  return false
}

function isSearchProvider(value: unknown): value is SearchProviderConfig {
  if (!isRecord(value)) return false
  return isNonEmptyString(value.id) && isBoolean(value.enabled) && isIntegerAtLeast(value.priority, 0) && optionalTimestamp(value, 'updatedAt')
}

function optionalTimestamp(value: Record<string, unknown>, key: string): boolean {
  return !has(value, key) || (isIntegerAtLeast(value[key], 0) && value[key] <= 9_999_999_999_999)
}

function uniqueBy(values: readonly unknown[], keyOf: (value: unknown) => string): boolean {
  const seen = new Set<string>()
  for (const value of values) {
    const key = keyOf(value)
    if (seen.has(key)) return false
    seen.add(key)
  }
  return true
}

function isEncryptedCredentials(value: unknown): value is EncryptedCredentials {
  if (!isRecord(value)) return false
  return value.algorithm === 'scrypt-aes-256-gcm' &&
    isBase64(value.salt, 16, 64) &&
    isBase64(value.nonce, 12, 12) &&
    isBase64(value.tag, 16, 16) &&
    isBase64(value.ciphertext, 1, 128 * 1024 * 1024)
}

/** Base64 validation without importing Node-only Buffer into the renderer. */
function isBase64(value: unknown, minBytes: number, maxBytes: number): boolean {
  if (typeof value !== 'string' || value.length === 0 || value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) return false
  const padding = value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0
  const bytes = (value.length / 4) * 3 - padding
  return bytes >= minBytes && bytes <= maxBytes
}
