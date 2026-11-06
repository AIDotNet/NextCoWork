/**
 * 领域对象的读写。**`state/store.ts` 唯一的后端** —— 那边的访问器逐个转发到这里,
 * 签名一个都没改,所以全应用的调用点一处都不用动。
 *
 * 函数名刻意和 `store` 的访问器一一对应:换实现时能逐行对照,
 * 而不是要先在脑子里做一次映射。
 *
 * ## 一处**行为**上的变化,值得单独说
 *
 * Map 版的 `getWorkspace` 返回的是表里那个**活对象**,改它就等于改了库;
 * 这里返回的是一次 `JSON.parse` 的产物,改它谁也不影响。
 * 方向是安全的(意外的写入变成无效果,而不是意外生效),而且和 `getSettings`
 * 一直以来的 `structuredClone` 语义终于一致了。已核对现有两个调用点
 * (`ipc/workspace.ts` 的 `updateWorkspace` / `listDir`)都是展开取值,不改返回对象。
 */
import type { AgentMessage, ContentPart } from '../../shared/agent/message'
import { isTurnStart } from '../../shared/agent/history-edit'
import type { RunUsage } from '../../shared/agent/transcript'
import type { ModelPricing, RunCost } from '../../shared/domain/pricing'
import type { ContextSearchHit } from '../../shared/agent/context-management'
import { parseNcwUrl } from '../../shared/domain/attachment'
import type { McpServerConfig } from '../../shared/domain/mcp'
import { mcpSecretRef } from '../../shared/domain/mcp'
import type { ModelAlias, UpstreamProvider } from '../../shared/domain/provider'
import { normalizeUpstreamProvider, providerCredentialRef } from '../../shared/domain/provider'
import type { ModelCatalogDefinition } from '../../shared/domain/model-catalog'
import { isModelCatalogDefinition } from '../../shared/domain/model-catalog'
import type { SearchProviderConfig, SearchProviderId } from '../../shared/domain/search'
import { defaultProviderConfigs, searchSecretRef } from '../../shared/domain/search'
import type { AppSettings, AppSettingsPatch } from '../../shared/domain/settings'
import { DEFAULT_SETTINGS, mergeSettings } from '../../shared/domain/settings'
import { dataMergeDecision, type CleanupPreview, type CleanupResult, type DataExport, type ExportSession, type ImportApplyResult } from '../../shared/domain/data'
import type { Session, SessionDetail, SessionListItem, SearchHit } from '../../shared/domain/session'
import { isDefaultSessionTitle } from '../../shared/domain/session'
import type { SessionMode, ThinkingLevel } from '../../shared/agent/run-request'
import { normalizeModeId } from '../../shared/domain/mode'
import type { Workspace } from '../../shared/domain/workspace'
import type { ChangeKind, ChangeSetState, ReviewChangeSet, ReviewFileDiff, ReviewFileEntry } from '../../shared/domain/review'
import { countLineDiff } from '../../shared/domain/line-diff'
import type { ScheduledRun, ScheduledTask } from '../../shared/domain/scheduled'
import type { ConnectionProfile, EnvironmentRef } from '../../shared/domain/environment'
import { normalizeEnvironmentRef } from '../../shared/domain/environment'
import { EnvironmentError } from '../environment/errors'
import type {
  UsageAttemptRecord,
  UsageCostTotal,
  UsageDimensionStat,
  UsageRequestLogsPage,
  UsageRequestLogsQuery,
  UsageSummary,
  UsageWindow
} from '../../shared/domain/usage'
import { fileStats, stmt, tx } from './index'
import { MANAGED_PROVIDER_ID, type ProviderFileStore } from './provider-file'
import {
  clearMessageFts,
  deleteOrphanSessionFts,
  deleteSessionFts,
  renameSessionFts,
  searchableMessageText,
  writeMessageFts
} from './message-text'
export { searchableMessageText } from './message-text'
import {
  ConfigProfileError,
  credentialScopePrefix,
  currentConfigScope,
  logicalCredentialRef,
  physicalCredentialRef,
  workspaceScope,
  workspaceScopeVisible
} from './config-profile'

import { ulid } from '../../shared/util/id'
import { AsyncLocalStorage } from 'node:async_hooks'
import { createHash } from 'node:crypto'
import { existsSync, lstatSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { basename, join } from 'node:path'
import type { SyncCategory, SyncConfigKind, SyncConflict, SyncMutation } from '../../shared/domain/config-sync'

const SYNC_ACCOUNT_KEY = 'config-sync.account'
const SYNC_DEVICE_KEY = 'config-sync.device'
const syncApplying = new AsyncLocalStorage<boolean>()

export { tx } from './index'

/** 列里存的是 JSON 文本;`String()` 是给 `SQLOutputValue` 那个联合类型收窄用的。 */
const parse = <T>(json: unknown): T => JSON.parse(String(json)) as T

/** ── 基础配置同步 outbox ─────────────────────────────────────────────── */
function syncAccount(): { accountId: string; deviceId: string; enabled: boolean } | undefined {
  const raw = getKv<unknown>(SYNC_ACCOUNT_KEY, null)
  if (typeof raw !== 'object' || raw === null) return undefined
  const value = raw as Record<string, unknown>
  return typeof value.accountId === 'string' && typeof value.deviceId === 'string'
    ? { accountId: value.accountId, deviceId: value.deviceId, enabled: value.enabled === true } : undefined
}

/**
 * 当前启用同步的账户;未登录或未启用时为 null。
 * 需求:使用统计只合并**这个账户**同步来的其它设备数据(`usage_daily_remote` 按账户隔离)。
 */
export function getActiveSyncAccountId(): string | null {
  const account = syncAccount()
  return account !== undefined && account.enabled ? account.accountId : null
}

export function ensureSyncDeviceId(): string {
  const current = getKv<unknown>(SYNC_DEVICE_KEY, null)
  if (typeof current === 'string' && current.length >= 16) return current
  const next = ulid()
  setKv(SYNC_DEVICE_KEY, next)
  return next
}

/**
 * ★ `enabled` **默认 false**。v1 那套明文 outbox 已经停用(见 `enqueueSyncMutation`),
 * 而调用点里只要有一处忘了显式传 true,默认 true 就会让「未启用同步」的账户
 * 悄悄开始往 sync_account 里写行。显式开启只发生在 `startConfigSync` 那一处。
 */
export function configureSyncAccount(accountId: string | null, enabled = false): void {
  tx(() => {
    if (accountId === null) {
      removeKv(SYNC_ACCOUNT_KEY)
      return
    }
    const now = Date.now()
    const deviceId = ensureSyncDeviceId()
    setKv(SYNC_ACCOUNT_KEY, { accountId, deviceId, enabled })
    stmt(`INSERT INTO sync_account (account_id, device_id, enabled, created_at, updated_at)
          VALUES (?, ?, ?, ?, ?) ON CONFLICT(account_id) DO UPDATE SET device_id=excluded.device_id, enabled=excluded.enabled, updated_at=excluded.updated_at`)
      .run(accountId, deviceId, enabled ? 1 : 0, now, now)
  })
}

/** 首次登录时把当前设备的可同步配置排入 outbox；会话数据绝不在这里读取。 */
export function enqueueInitialSyncSnapshot(accountId: string): void {
  const account = syncAccount()
  if (account?.accountId !== accountId) return
  const pending = Number((stmt('SELECT COUNT(*) AS n FROM sync_outbox WHERE account_id = ?').get(accountId) as Record<string, unknown>)['n'] ?? 0)
  if (pending > 0) return
  for (const provider of listProviders()) {
    if (provider.id === 'nextcowork') continue
    const { credentialRef: _credentialRef, ...payload } = provider
    enqueueSyncMutation('provider', provider.id, payload)
  }
  for (const alias of listAliases()) if (alias.providerId !== 'nextcowork') enqueueSyncMutation('modelAlias', `${alias.providerId}/${alias.alias}`, alias)
  for (const server of listMcpServers()) if (server.workspaceId === undefined) enqueueSyncMutation('mcpServer', server.id, server)
  for (const search of listStoredSearchProviders()) enqueueSyncMutation('searchProvider', search.id, search)
  const settings = getSettings()
  const { personalization: _personalization, shell: _shell, ...preferences } = settings
  enqueueSyncMutation('appPreferences', 'global', { ...preferences, data: { ...settings.data, backupDirectory: null } })
  enqueueSyncMutation('appPersonalization', 'global', settings.personalization)
  for (const workspace of listWorkspaces()) {
    enqueueSyncMutation('workspacePreferences', workspace.id, workspacePreferences(workspace))
  }
}

export function setInitialSyncCompleted(accountId: string, completed = true): void {
  stmt('UPDATE sync_account SET initial_sync_completed = ?, updated_at = ? WHERE account_id = ?')
    .run(completed ? 1 : 0, Date.now(), accountId)
}

/** v2 脏标记的 kv 键。**带作用域**,所以它自己不会随着切换被搬走。 */
export function configDirtyKey(scope: string): string {
  return `config-sync.v2.dirty.${scope}`
}

/** 标记某个作用域的配置自上次加密快照以来变过。v2 靠它决定下一次要不要重算指纹。 */
export function setConfigDirty(scope: string = currentConfigScope(), dirty = true): void {
  setKv(configDirtyKey(scope), dirty)
}

export function getConfigDirty(scope: string = currentConfigScope()): boolean {
  return getKv<unknown>(configDirtyKey(scope), false) === true
}

function categoryForSyncKind(kind: SyncConfigKind): SyncCategory {
  if (kind === 'provider' || kind === 'modelAlias') return 'providers'
  if (kind === 'mcpServer' || kind === 'searchProvider') return 'connections'
  if (kind === 'workspacePreferences') return 'workspaces'
  return kind === 'appPersonalization' ? 'preferences' : 'preferences'
}

function configCategoryDirtyKey(scope: string, category: SyncCategory): string {
  return `config-sync.v2.dirty.${scope}.${category}`
}

export function setConfigCategoryDirty(
  category: SyncCategory,
  scope: string = currentConfigScope(),
  dirty = true
): void {
  setKv(configCategoryDirtyKey(scope, category), dirty)
  if (dirty) setConfigDirty(scope, true)
}

export function getConfigCategoryDirty(
  category: SyncCategory,
  scope: string = currentConfigScope()
): boolean {
  return getKv<unknown>(configCategoryDirtyKey(scope, category), false) === true
}

/**
 * ★★ **v1 明文 outbox 从这里起不再产生任何一行。**
 *
 * 原来这里把整份 payload(`JSON.stringify(payload)`)写进 `sync_outbox` ——
 * 供应商的 baseUrl、别名、设置、工作区偏好,全是明文落盘。v2 的同步走
 * `config-sync` 引擎的加密快照,不需要、也不允许再有一份明文副本。
 *
 * 现在它只做一件事:打一个**脏标记**。函数名和签名**一个字都没改** ——
 * 它是全应用十几个写入点(putProvider / putAlias / updateSettings / …)唯一的
 * 收口,改签名就等于把「哪个写入点忘了告诉同步层」重新变成一件要靠人记的事。
 *
 * 三个前提缺一不可,否则连标记都不打:
 * - 不在同步应用过程中(`withSyncApply`:远端推回来的改动不是本地改动);
 * - 已配置同步账户(`configureSyncAccount`);
 * - 且那一份是**启用**状态 —— 退出登录 / 关掉同步之后配置照样能改,
 *   而那时候打标记等于给一个不存在的同步会话记账。
 *
 * ★ 已经在库里的 v1 行**不动**:它们是历史,迁移的输入,只读。
 * `listPendingSyncMutations` / `ack*` 因此原样留着。
 */
export function enqueueSyncMutation(_kind: SyncConfigKind, _entityId: string, _payload: unknown, _operation: SyncMutation['operation'] = 'upsert', _workspaceId?: string): void {
  if (syncApplying.getStore() === true) return
  const account = syncAccount()
  if (account === undefined || !account.enabled) return
  setConfigCategoryDirty(categoryForSyncKind(_kind), account.accountId)
}

export function withSyncApply<T>(fn: () => T): T {
  return syncApplying.run(true, fn)
}

/**
 * 远端快照应用会跨 `secrets.set()` 的 Promise 边界。用同步版包它会在第一个 await
 * 立刻撤掉围栏,后半段写入被当成本地改动再次上传,两台设备从此互相回声。
 */
export async function withSyncApplyAsync<T>(fn: () => Promise<T>): Promise<T> {
  return await syncApplying.run(true, fn)
}

export function saveSyncConflict(conflict: Omit<SyncConflict, 'status'>): void {
  stmt(`INSERT INTO sync_conflict (id, account_id, kind, entity_id, local_revision, remote_revision, local_payload, remote_payload, created_at)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(id) DO UPDATE SET remote_revision=excluded.remote_revision, remote_payload=excluded.remote_payload`)
    .run(conflict.id, syncAccount()?.accountId ?? '', conflict.kind, conflict.entityId, conflict.localRevision, conflict.remoteRevision, JSON.stringify(conflict.localPayload), JSON.stringify(conflict.remotePayload), conflict.createdAt)
}

export function listPendingSyncMutations(accountId: string, limit = 50): SyncMutation[] {
  return stmt(`SELECT mutation_id, account_id, device_id, client_seq, kind, entity_id, operation, payload, base_revision, workspace_id
    FROM sync_outbox WHERE account_id = ? AND acked_at IS NULL AND next_attempt_at <= ? ORDER BY client_seq LIMIT ?`)
    .all(accountId, Date.now(), Math.min(Math.max(limit, 1), 100)).map((row) => {
      const r = row as Record<string, unknown>
      return { mutationId: String(r.mutation_id), accountId: String(r.account_id), deviceId: String(r.device_id), clientSeq: Number(r.client_seq), kind: String(r.kind) as SyncConfigKind, entityId: String(r.entity_id), operation: String(r.operation) as SyncMutation['operation'], payload: parse(r.payload), baseRevision: Number(r.base_revision), ...(r.workspace_id == null ? {} : { workspaceId: String(r.workspace_id) }) }
    })
}

export function ackSyncMutation(accountId: string, mutationId: string): void {
  stmt('UPDATE sync_outbox SET acked_at = ?, last_error = NULL WHERE account_id = ? AND mutation_id = ?').run(Date.now(), accountId, mutationId)
}

export function setSyncRevision(accountId: string, kind: string, entityId: string, revision: number): void {
  stmt(`INSERT INTO sync_revisions(account_id, kind, entity_id, revision) VALUES (?, ?, ?, ?)
    ON CONFLICT(account_id, kind, entity_id) DO UPDATE SET revision = MAX(sync_revisions.revision, excluded.revision)`)
    .run(accountId, kind, entityId, revision)
}

export function rebasePendingSyncMutations(accountId: string, kind: string, entityId: string, revision: number, afterSeq: number): void {
  stmt('UPDATE sync_outbox SET base_revision = ? WHERE account_id = ? AND kind = ? AND entity_id = ? AND client_seq > ? AND acked_at IS NULL')
    .run(revision, accountId, kind, entityId, afterSeq)
}

export function updateSyncServerCursor(accountId: string, cursor: number): void {
  stmt('UPDATE sync_account SET last_server_cursor = MAX(last_server_cursor, ?), updated_at = ? WHERE account_id = ?')
    .run(cursor, Date.now(), accountId)
}

export function failSyncMutations(accountId: string, mutationIds: readonly string[], error: string): void {
  for (const id of mutationIds) stmt('UPDATE sync_outbox SET attempt_count = attempt_count + 1, next_attempt_at = ?, last_error = ? WHERE account_id = ? AND mutation_id = ?').run(Date.now() + 5000, error.slice(0, 500), accountId, id)
}

export function syncAccountState(accountId: string): Record<string, unknown> | undefined {
  const row = stmt('SELECT * FROM sync_account WHERE account_id = ?').get(accountId)
  return row === undefined ? undefined : row as Record<string, unknown>
}

export function updateSyncCursor(accountId: string, cursor: number, error: string | null = null): void {
  stmt('UPDATE sync_account SET last_pull_cursor = ?, last_server_cursor = ?, last_success_at = ?, last_error = ?, updated_at = ? WHERE account_id = ?').run(cursor, cursor, error === null ? Date.now() : null, error, Date.now(), accountId)
}

export function listSyncConflicts(accountId: string): SyncConflict[] {
  return stmt('SELECT * FROM sync_conflict WHERE account_id = ? AND status = \'pending\' ORDER BY created_at').all(accountId).map((row) => {
    const r = row as Record<string, unknown>
    return { id: String(r.id), kind: String(r.kind) as SyncConfigKind, entityId: String(r.entity_id), localRevision: Number(r.local_revision), remoteRevision: Number(r.remote_revision), localPayload: parse(r.local_payload), remotePayload: parse(r.remote_payload), status: 'pending', createdAt: Number(r.created_at) }
  })
}

export function markSyncConflictResolved(accountId: string, id: string): void {
  stmt('UPDATE sync_conflict SET status = \'resolved\', resolved_at = ? WHERE account_id = ? AND id = ?')
    .run(Date.now(), accountId, id)
}

// ── settings ────────────────────────────────────────────────────────────────

/**
 * ★ 读出来的值是**合并到 `DEFAULT_SETTINGS` 上**的,不是直接返回。
 *
 * 这样旧版本存下的行缺了新字段时,拿到的是新字段的默认值,而不是 `undefined`
 * 顺着 IPC 流到界面上变成一个空下拉框。下一步就要用到:方案 §7 要把
 * `gateway.failover` 改名成 `routing.failover`,那之后所有已经存在的行都缺 `routing`。
 *
 * `mergeSettings` 是逐字段展开的(还带一张编译期哨兵表),所以它顺带也把
 * **已经删掉的字段**挡在外面 —— 它只认识 `AppSettings` 上真实存在的键。
 */
export function getSettings(): AppSettings {
  const row = stmt('SELECT json FROM settings WHERE id = 1').get()
  if (row === undefined) return structuredClone(DEFAULT_SETTINGS)
  let patch: AppSettingsPatch = {}
  try {
    const value: unknown = JSON.parse(String(row['json']))
    // 设置行是用户本机可编辑的数据。损坏的 JSON、null、数组和标量都
    // 应当回到默认设置，而不是让整个 bootstrap 失败。
    if (typeof value === 'object' && value !== null && !Array.isArray(value)) {
      patch = value as AppSettingsPatch
    }
  } catch (err) {
    console.warn('[db] 设置记录损坏，已回退默认值:', err)
  }
  return mergeSettings(DEFAULT_SETTINGS, patch)
}

export function updateSettings(patch: AppSettingsPatch): AppSettings {
  // 读-改-写要在一个事务里:否则两个 handler 同时改不同字段时,后写的那个
  // 拿的是改之前的快照,会把前一个的改动原样覆盖回去。
  return tx(() => {
    const next = mergeSettings(getSettings(), patch)
    stmt(
      'INSERT INTO settings (id, json) VALUES (1, ?) ON CONFLICT (id) DO UPDATE SET json = excluded.json'
    ).run(JSON.stringify(next))
    const { personalization: _personalization, shell: _shell, ...preferences } = next
    enqueueSyncMutation('appPreferences', 'global', { ...preferences, data: { ...next.data, backupDirectory: null } })
    enqueueSyncMutation('appPersonalization', 'global', next.personalization)
    return next
  })
}

// ── workspaces ──────────────────────────────────────────────────────────────

function workspacePreferences(workspace: Workspace): Pick<Workspace, 'id' | 'name' | 'settings' | 'createdAt'> {
  return { id: workspace.id, name: workspace.name, settings: workspace.settings, createdAt: workspace.createdAt }
}

export function listConnectionProfiles(): ConnectionProfile[] {
  return stmt('SELECT json FROM connection_profiles ORDER BY id').all().map((row) => parse<ConnectionProfile>(row['json']))
}

export function getConnectionProfile(id: string): ConnectionProfile | undefined {
  const row = stmt('SELECT json FROM connection_profiles WHERE id = ?').get(id)
  return row === undefined ? undefined : parse<ConnectionProfile>(row['json'])
}

export function putConnectionProfile(profile: ConnectionProfile): ConnectionProfile {
  stmt('INSERT INTO connection_profiles (id, json) VALUES (?, ?) ON CONFLICT(id) DO UPDATE SET json = excluded.json')
    .run(profile.id, JSON.stringify(profile))
  return profile
}

export function removeConnectionProfile(id: string): void {
  tx(() => {
    if (listWorkspaces().some((workspace) => {
      const ref = normalizeEnvironmentRef(workspace.environment)
      return ref.kind === 'connection' && ref.connectionId === id
    })) throw new EnvironmentError('connection-in-use')
    stmt('DELETE FROM connection_profiles WHERE id = ?').run(id)
    /*
      ★ 前缀也要带**本作用域**。不带的话这句会按逻辑前缀扫全表,
      把另一个账户里同名 id 的连接密钥一起删掉 —— 症状是「我在 A 账户删了条 SSH 连接,
      B 账户的却连不上了」,而两个账户之间根本没有可见的联系。
    */
    const prefix = `${credentialScopePrefix()}connection:${id}:`
    stmt('DELETE FROM credentials WHERE substr(ref, 1, length(?)) = ?').run(prefix, prefix)
  })
}

/**
 * 当前作用域的工作区列表。
 *
 * ★ 收窄在 **SQL 的 WHERE**,不是读回来再 filter —— 后者每一个调用点都要重记一遍
 * 「这里也要过滤」,而漏掉任何一处的表现都是「另一个账户的工作区出现在列表里」。
 */
export function listWorkspaces(): Workspace[] {
  return stmt('SELECT json FROM workspaces WHERE owner = ? ORDER BY last_opened_at DESC')
    .all(currentConfigScope())
    .map((r) => normalizeWorkspace(parse<Workspace>(r['json'])))
}

export function getWorkspace(id: string): Workspace | undefined {
  const row = stmt('SELECT owner, json FROM workspaces WHERE id = ?').get(id)
  if (row === undefined) return undefined
  // 别的账户的工作区**当作不存在**。返回它再让调用点各自判断归属,是必然会有漏网的一版。
  return String(row['owner']) === currentConfigScope() ? normalizeWorkspace(parse<Workspace>(row['json'])) : undefined
}

function normalizeWorkspace(workspace: Workspace): Workspace {
  const defaultMode = normalizeModeId(workspace.settings.defaultMode)
  return defaultMode === workspace.settings.defaultMode
    ? workspace
    : { ...workspace, settings: { ...workspace.settings, defaultMode } }
}

/**
 * 写工作区。
 *
 * ★ 撞上**别的账户**已有的 id 时拒绝,而不是顶替。id 是渲染层先生成/导入来的,
 * 顶替意味着一次「新建工作区」可能悄无声息地把另一个账户的那一份覆盖掉 ——
 * 而这件事在界面上完全看不出来(两边看起来都只是「我的工作区」)。
 */
export function putWorkspace(w: Workspace): Workspace {
  const normalized = normalizeWorkspace(w)
  const owner = workspaceScope(normalized.id)
  if (owner !== null && owner !== currentConfigScope()) throw new ConfigProfileError('workspaceOwned')
  tx(() => { stmt(
    `INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES (?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET last_opened_at = excluded.last_opened_at, json = excluded.json`
  ).run(normalized.id, normalized.lastOpenedAt, JSON.stringify(normalized), owner ?? currentConfigScope())
    enqueueSyncMutation('workspacePreferences', normalized.id, workspacePreferences(normalized))
  })
  return normalized
}

export function removeWorkspace(id: string): void {
  if (!workspaceScopeVisible(id)) return
  tx(() => {
    stmt('DELETE FROM workspaces WHERE id = ? AND owner = ?').run(id, currentConfigScope())
    enqueueSyncMutation('workspacePreferences', id, { id }, 'delete')
  })
}

// ── sessions / messages / runs ─────────────────────────────────────────────

export interface SessionCreateInput {
  id?: string
  workspaceId: string
  /** 非空 = 这是一次子代理 run 的转录,不进任何面向用户的枚举。见 `Session.parentSessionId`。 */
  parentSessionId?: string
  origin?: 'chat' | 'scheduled'
  title?: string
  model?: string
  modelProviderId?: string
  mode?: SessionMode
  thinking?: ThinkingLevel
  rootPathAtCreation?: string
  createdAt?: number
  /** 提炼会话的源会话。见 `Session.skillSource`。 */
  skillSource?: { sessionId: string }
}

function sessionFromRow(row: Record<string, unknown>): Session {
  let parsed: Partial<Session> = {}
  try {
    parsed = parse<Partial<Session>>(row['json'])
  } catch {
    // 旧/损坏的 json 仍可由提列字段恢复，避免整个侧边栏消失。
  }
  return {
    id: String(row['id']),
    workspaceId: String(row['workspace_id']),
    /*
      ★ 只认真列,不走 `parsed` 兜底。json 里那份是 `sessionRowJson` 顺手写进去的
      副本,而列是过滤和级联唯一读的地方 —— 两者不一致时必须以列为准,
      否则一条 json 损坏的行会重新出现在侧边栏里。
    */
    ...(row['parent_session_id'] === null || row['parent_session_id'] === undefined
      ? {} : { parentSessionId: String(row['parent_session_id']) }),
    ...(parsed.origin === 'scheduled' ? { origin: 'scheduled' as const } : { origin: 'chat' as const }),
    title: String(row['title'] ?? parsed.title ?? '新对话'),
    ...(parsed.titleSource === 'default' || parsed.titleSource === 'generated' || parsed.titleSource === 'manual'
      ? { titleSource: parsed.titleSource } : {}),
    model: String(row['model'] ?? parsed.model ?? ''),
    /*
      ★ 只活在 json 里,**没有提列** —— 照 `titleSource` 的先例。
      提列的判据是「要不要被 WHERE / ORDER BY / 级联删除读到」,而这个字段
      一样都不沾(用量统计走 `usage_records` 自己的 provider_id 真列)。
      为它加一列只会多制造一对「列和 json 可能不一致」,而上面那段注释
      正说明了这种不一致要用硬规则去压。
    */
    ...(typeof parsed.modelProviderId === 'string' ? { modelProviderId: parsed.modelProviderId } : {}),
    // 同上,只活在 json 里。严格收窄:json 是可以被导入/手改的,形状不对就当没有。
    ...(typeof parsed.skillSource === 'object' && parsed.skillSource !== null && typeof parsed.skillSource.sessionId === 'string'
      ? { skillSource: { sessionId: parsed.skillSource.sessionId } } : {}),
    mode: normalizeModeId(row['mode'] ?? parsed.mode),
    thinking: (row['thinking'] ?? parsed.thinking ?? 'auto') as ThinkingLevel,
    rootPathAtCreation: String(row['root_path_at_creation'] ?? parsed.rootPathAtCreation ?? ''),
    // running 永不从磁盘恢复；启动和读取都视为 idle。
    status: 'idle',
    archived: Number(row['archived'] ?? (parsed.archived ? 1 : 0)) !== 0,
    favorited: Number(row['favorited'] ?? (parsed.favorited ? 1 : 0)) !== 0,
    createdAt: Number(row['created_at'] ?? parsed.createdAt ?? Date.now()),
    updatedAt: Number(row['updated_at'] ?? parsed.updatedAt ?? Date.now())
  }
}

function sessionRowJson(s: Session): string {
  return JSON.stringify({ ...s, status: s.status === 'running' ? 'idle' : s.status })
}

/** 创建或补齐一条会话。运行链路和渲染层都可安全调用。 */
export function createSession(input: SessionCreateInput): Session {
  const now = input.createdAt ?? Date.now()
  const session: Session = {
    id: input.id ?? ulid(now),
    workspaceId: input.workspaceId,
    ...(input.parentSessionId === undefined ? {} : { parentSessionId: input.parentSessionId }),
    origin: input.origin ?? 'chat',
    title: input.title?.trim() || '新对话',
    titleSource: isDefaultSessionTitle(input.title ?? '') ? 'default' : 'manual',
    model: input.model ?? '',
    ...(input.modelProviderId === undefined ? {} : { modelProviderId: input.modelProviderId }),
    mode: normalizeModeId(input.mode),
    thinking: input.thinking ?? 'auto',
    rootPathAtCreation: input.rootPathAtCreation ?? '',
    ...(input.skillSource === undefined ? {} : { skillSource: { sessionId: input.skillSource.sessionId } }),
    status: 'idle',
    archived: false,
    favorited: false,
    createdAt: now,
    updatedAt: now
  }
  return putSession(session)
}

/** 不存在时创建，存在时返回现有记录；用于 renderer 先生成 id 的 Tab。 */
export function ensureSession(input: SessionCreateInput): Session {
  if (input.id !== undefined) {
    const existing = getSession(input.id)
    if (existing !== undefined) {
      /*
        ★ 唯一一处「命中已有行还要写」的例外:这一行是在加上
        `parent_session_id` 之前建的(或者由一份旧存档导入),而它决定这条转录
        进不进侧边栏。不补的话,一条续跑的子代理会话会永远留在列表里,
        且没有任何入口能修正它。其余字段照旧不覆盖 —— 用户改过的标题归用户。
      */
      if (input.parentSessionId !== undefined && existing.parentSessionId === undefined) {
        return putSession({ ...existing, parentSessionId: input.parentSessionId })
      }
      return existing
    }
  }
  return createSession(input)
}

/**
 * 会话 → 当前作用域能不能看见它。
 *
 * ★ 归属判据是**它挂的那个工作区**,而不是会话自己的某个字段:
 * 工作区已经在 `workspaces.owner` 上有了权威归属,给会话再加一份就是
 * 「同一个事实存两处」,而它们迟早会不一致。
 *
 * 返回 `false` 而不是抛错:跨账户访问在界面上就该是「查不到」,不是一条
 * 说明「它存在但你没权限」的错误 —— 后者本身就是一次信息泄露。
 */
export function sessionScopeVisible(id: string): boolean {
  const row = stmt('SELECT workspace_id FROM sessions WHERE id = ?').get(id)
  return row !== undefined && workspaceScopeVisible(String(row['workspace_id']))
}

export function getSession(id: string): Session | undefined {
  const row = stmt('SELECT * FROM sessions WHERE id = ?').get(id)
  if (row === undefined) return undefined
  const session = sessionFromRow(row as Record<string, unknown>)
  return workspaceScopeVisible(session.workspaceId) ? session : undefined
}

export function putSession(session: Session): Session {
  const previous = stmt('SELECT title FROM sessions WHERE id = ?').get(session.id)
  const normalized: Session = {
    ...session,
    /*
      ★ 自环护栏。`parent_session_id = id` 会让 `sessionSubtreeIds` 的递归 CTE
      在这一行上原地打转 —— `UNION` 去重能终止它,但整条级联删除会静默地
      只删这一行。畸形导入和将来某个写错的派生都从这里挡掉。
    */
    ...(session.parentSessionId === session.id ? { parentSessionId: undefined } : {}),
    status: 'idle',
    title: session.title.trim() || '新对话',
    mode: normalizeModeId(session.mode)
  }
  stmt(
    `INSERT INTO sessions
       (id, workspace_id, parent_session_id, title, model, mode, thinking, root_path_at_creation, status,
        archived, favorited, created_at, updated_at, json)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'idle', ?, ?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET
       workspace_id = excluded.workspace_id,
       parent_session_id = excluded.parent_session_id,
       title = excluded.title,
       model = excluded.model,
       mode = excluded.mode,
       thinking = excluded.thinking,
       root_path_at_creation = excluded.root_path_at_creation,
       status = 'idle',
       archived = excluded.archived,
       favorited = excluded.favorited,
       created_at = excluded.created_at,
       updated_at = excluded.updated_at,
       json = excluded.json`
  ).run(
    normalized.id,
    normalized.workspaceId,
    normalized.parentSessionId ?? null,
    normalized.title,
    normalized.model,
    normalized.mode,
    normalized.thinking,
    normalized.rootPathAtCreation,
    normalized.archived ? 1 : 0,
    normalized.favorited ? 1 : 0,
    normalized.createdAt,
    normalized.updatedAt,
    sessionRowJson(normalized)
  )
  // Message bodies are indexed separately in FTS5. Keep the denormalized
  // title column in sync when a session is renamed (or imported) so searches
  // by conversation title do not continue returning the old title until the
  // next message is committed.
  if (previous !== undefined && previous['title'] !== normalized.title) {
    renameSessionFts(stmt, normalized.id, normalized.title)
  }
  return normalized
}

/**
 * 侧边栏的那张列表。
 *
 * ★ `parent_session_id IS NULL` 是**子代理转录的过滤口**:它们的行确实在表里
 * (外键要求如此),但它们不是用户的对话 —— 少了这个条件,每派一个子代理
 * 侧边栏就多一条永远叫「新对话」的条目(标题生成只对 depth 0 触发)。
 */
export function listSessions(workspaceId: string, archived?: boolean): SessionListItem[] {
  // 看不见的工作区就是空列表。侧边栏据此收起,而不是显示另一个账户的对话。
  if (!workspaceScopeVisible(workspaceId)) return []
  const rows = archived === undefined
    ? stmt('SELECT id, title, updated_at, archived FROM sessions WHERE workspace_id = ? AND parent_session_id IS NULL ORDER BY updated_at DESC, id DESC').all(workspaceId)
    : stmt('SELECT id, title, updated_at, archived FROM sessions WHERE workspace_id = ? AND parent_session_id IS NULL AND archived = ? ORDER BY updated_at DESC, id DESC').all(workspaceId, archived ? 1 : 0)
  return rows.map((row) => {
    const r = row as Record<string, unknown>
    const id = String(r['id'])
    const full = getSession(id)
    return {
      id,
      title: String(r['title']),
      updatedAt: Number(r['updated_at']),
      archived: Number(r['archived']) !== 0,
      favorited: Boolean(Number(full?.favorited ?? 0)),
      running: false,
      ...(full?.skillSource === undefined ? {} : { skillExtraction: true as const })
    }
  }).filter((item) => getSession(item.id)?.origin !== 'scheduled')
}

export function getSessionDetail(id: string): SessionDetail | undefined {
  const session = getSession(id)
  if (session === undefined) return undefined
  return {
    session,
    messages: getHistory(id) as AgentMessage[],
    messageRuns: messageRunsOf(id),
    runUsage: runUsageOf(id),
    runModel: runModelOf(id)
  }
}

/**
 * 消息 → 产出它的 run。第 12 条迁移之前的消息没有归属,直接不出现在这张表里
 * (而不是给一个占位 id ——「不知道」和「属于某个 run」在界面上是两件事:
 * 前者不显示用量,后者会去查一个查不到的 run 然后显示 0 token)。
 */
export function messageRunsOf(sessionId: string, messageIds?: readonly string[]): Record<string, string> {
  const runs: Record<string, string> = {}
  // CROSS JOIN keeps the page IDs first, so SQLite probes the primary key instead of scanning the session index.
  const rows = messageIds === undefined
    ? stmt('SELECT id, run_id FROM messages WHERE session_id = ? AND run_id IS NOT NULL').all(sessionId)
    : stmt(`SELECT m.id, m.run_id FROM json_each(?) AS page
            CROSS JOIN messages AS m ON m.id = page.value
            WHERE m.session_id = ? AND m.run_id IS NOT NULL`)
      .all(JSON.stringify(messageIds), sessionId)
  for (const row of rows) {
    const r = row as Record<string, unknown>
    runs[String(r['id'])] = String(r['run_id'])
  }
  return runs
}

/**
 * 一条会话里每个 run 的累计用量,直接从落盘的 `usage_records` 聚合。
 *
 * ★ **失败的尝试也算进来。** 一次被限流打回的请求照样把 prompt 发上去了、
 * 照样计了费;把它们排除掉,界面上的数字就会比账单小,而差额没有任何地方交代。
 * 这也和 `usageSummary` 的口径一致 —— 两处报同一笔账,不能一处含重试一处不含。
 *
 * ★ 思考 Token 用 `COALESCE(...,0)` 求和是安全的:它是 `outputTokens` 的子集,
 * 只用于展示,不参与任何合计。拿不到独立计数的供应商聚出来就是 0,
 * 而 0 在这里的含义正是「没有单独报」—— 展示层照样不显示它。
 *
 * ★★ **只有耗时那一列把失败尝试排除在外**,和上面的 token 口径**故意不一致**。
 * 理由是这两个数回答的不是同一个问题:token 回答「这一轮花了多少钱」,失败的
 * 尝试照样花了;而 `upstream_ms` 回答「模型输出有多快」,它是平均 TPS 的分母。
 * 一次 30s 超时打回、没产出一个 token 的尝试算进分母,界面上就会把「重试过一次」
 * 显示成「这家模型慢得像卡住了」。渲染层实时那份也只在 `message_end` 上累加 ——
 * 失败的尝试根本发不出 message_end,两条路径这样才是同一个口径。
 */
export function runUsageOf(sessionId: string): Record<string, RunUsage> {
  const usage: Record<string, RunUsage> = {}
  const rows = stmt(
    `SELECT run_id,
            SUM(input_tokens)          AS input_tokens,
            SUM(output_tokens)         AS output_tokens,
            SUM(cache_read_tokens)     AS cache_read_tokens,
            SUM(cache_write_tokens)    AS cache_write_tokens,
            SUM(cache_write_1h_tokens) AS cache_write_1h_tokens,
            SUM(COALESCE(thinking_tokens, 0)) AS thinking_tokens,
            SUM(CASE WHEN ok = 1 THEN latency_ms ELSE 0 END) AS upstream_ms,
            SUM(cost_micros)         AS cost_micros,
            COUNT(cost_micros)       AS priced_count,
            COUNT(*)                 AS attempt_count,
            COUNT(DISTINCT currency) AS currency_count,
            MIN(currency)            AS currency
       FROM usage_records
      WHERE session_id = ?
      GROUP BY run_id`
  ).all(sessionId)
  for (const row of rows) {
    const r = row as Record<string, unknown>
    const reasoning = Number(r['thinking_tokens'] ?? 0)
    const upstreamMs = Number(r['upstream_ms'] ?? 0)
    usage[String(r['run_id'])] = {
      inputTokens: Number(r['input_tokens'] ?? 0),
      outputTokens: Number(r['output_tokens'] ?? 0),
      cacheReadInputTokens: Number(r['cache_read_tokens'] ?? 0),
      cacheCreationInputTokens: Number(r['cache_write_tokens'] ?? 0),
      cacheCreation1hInputTokens: Number(r['cache_write_1h_tokens'] ?? 0),
      ...(reasoning > 0 ? { reasoningTokens: reasoning } : {}),
      // 0 = 这一轮没有一次成功的请求。留 undefined 让展示层直接不画 TPS,
      // 而不是让它去除以零。
      ...(upstreamMs > 0 ? { upstreamMs } : {}),
      ...costOf(r)
    }
  }
  return usage
}

/**
 * 这一轮的花费 —— **只在算得齐的时候才给**。
 *
 * ★★ 有一条尝试 `cost_micros IS NULL`(模型不在价目表),`SUM()` 会安静地把它
 * 当 0 跳过,给出一个偏低却完全合理的总额。所以这里拿 `COUNT(cost_micros)`
 * (不计 NULL)和 `COUNT(*)` 对一下:对不上就显式返回 `cost: null`,界面据此不画
 * 那一行，同时会话总额也不会把其余已知金额误报成完整总额。
 * 这和渲染层 `addCost` 的「一次算不出就整轮算不出」是同一条规矩 —— 实时那份和
 * 落盘这份必须同口径,否则切走再切回来金额会变。
 *
 * ★ 币种多于一种同样返回 null:没有汇率源,¥ 和 $ 加起来是个看着合理的错数。
 * 一轮里故障切换到了另一个国别的供应商就会这样。
 *
 * ★ 口径跟着上面的 token 走:**失败的尝试也算**。它一样把 prompt 发上去了、
 * 一样计了费,排除掉界面就会比账单小,而差额没有任何地方交代。
 */
function costOf(r: Record<string, unknown>): { cost?: RunCost | null } {
  const currency = r['currency']
  if (Number(r['priced_count'] ?? 0) !== Number(r['attempt_count'] ?? 0)) return { cost: null }
  if (Number(r['currency_count'] ?? 0) !== 1) return { cost: null }
  if (currency !== 'USD' && currency !== 'CNY') return { cost: null }
  return { cost: { micros: Number(r['cost_micros'] ?? 0), currency } }
}

/**
 * 一条会话里每个 run 发送时选中的模型别名,从 `usage_records.alias` 回填。
 *
 * ★ 同一个 run 的多次尝试(重试/故障切换)共享同一个 `req.model`(`router.ts`
 * 里 `alias: req.model`),所以这一列在同一 `run_id` 分组内恒定 —— `MIN()`
 * 只是凑 SQL 聚合语法,取哪一行结果都一样。这是**发送时选的别名**,不是
 * 回包里的真实上游模型名(那个是 `upstream_model` 列,定价用它查)。
 */
export function runModelOf(sessionId: string): Record<string, string> {
  const model: Record<string, string> = {}
  const rows = stmt(
    `SELECT run_id, MIN(alias) AS alias
       FROM usage_records
      WHERE session_id = ?
      GROUP BY run_id`
  ).all(sessionId)
  for (const row of rows) {
    const r = row as Record<string, unknown>
    model[String(r['run_id'])] = String(r['alias'])
  }
  return model
}

/**
 * 库里**全部**会话,子代理转录也算。
 *
 * ★ 这里**不能**加 `parent_session_id IS NULL`。它服务的是备份 manifest
 * (`ipc/storage.ts` 的 `createBackup`),而 `validateBackupDatabase` 是拿
 * **裸** `SELECT COUNT(*) FROM sessions` 跟 manifest 对账的 —— 两边不等就
 * `manifest count mismatch`,后果是**每一个新建的备份都恢复不了**。
 * 「导出给人看的那份」用下面那个函数。
 */
export function listAllSessionDetails(): ExportSession[] {
  return sessionDetailsOf(stmt('SELECT id FROM sessions ORDER BY updated_at DESC, id DESC').all())
}

/** 数据导出用:只给顶层对话。子代理转录是父对话的实现细节,导出它没有意义。 */
export function listExportableSessionDetails(): ExportSession[] {
  return sessionDetailsOf(
    stmt('SELECT id FROM sessions WHERE parent_session_id IS NULL ORDER BY updated_at DESC, id DESC').all()
  )
}

function sessionDetailsOf(rows: readonly unknown[]): ExportSession[] {
  return rows.flatMap((row) => {
    const id = String((row as Record<string, unknown>)['id'])
    const session = getSession(id)
    // ★ 刻意不走 `getSessionDetail`:它比这里多查两样(run 归属与逐轮用量),
    // 而导出格式一个都不含 —— 备份要遍历**全库**的会话,白搭的每会话两次查询
    // 在这条路径上是按会话数乘出去的。
    return session === undefined ? [] : [{
      session,
      messages: [...getHistory(id)]
    }]
  })
}

export function renameSession(id: string, title: string): void {
  const current = getSession(id)
  if (current === undefined) throw new Error(`会话不存在: ${id}`)
  putSession({ ...current, title: title.trim() || '新对话', titleSource: 'manual', updatedAt: Date.now() })
}

export function setSessionArchived(id: string, archived: boolean): void {
  const current = getSession(id)
  if (current === undefined) throw new Error(`会话不存在: ${id}`)
  putSession({ ...current, archived, updatedAt: Date.now() })
}

export function setSessionFavorited(id: string, favorited: boolean): void {
  const current = getSession(id)
  if (current === undefined) throw new Error(`会话不存在: ${id}`)
  putSession({ ...current, favorited, updatedAt: Date.now() })
}

function upsertFts(session: Session, message: AgentMessage): void {
  writeMessageFts(stmt, message.id, session.id, session.title, searchableMessageText(message.parts))
}

/**
 * 消息里的图片引用落库。
 *
 * ## 两类 dataRef,两条路
 *
 * - **`ncw://` URL** —— 由本应用的上传服务产出。这类附件**已经有一条 draft 行**了
 *   (上传那一刻就登记了),这里要做的是把它升为 `committed` 并补上
 *   `session_id` / `message_id` —— 那两列有外键,而上传时会话/消息都还不存在。
 *   **绝不能再 INSERT 一条**:同一个文件两条记录,清理时会按其中一条判活、
 *   另一条永远是"文件不存在"的死记录。
 *
 * - **绝对路径** —— Agent 产出的图、历史数据、从别处引用的文件。它们不受
 *   附件服务管理,沿用原来的「按 `messageId:index` 登记一条」的方式。
 *
 * ★ 从 URL 反推主键**不需要知道附件根在哪**:磁盘文件名就是
 * `<attachmentId><ext>`,去掉扩展名就是 id。这让 repo 层不必依赖 `electron`。
 */
interface ManagedAttachmentCandidate {
  row: AttachmentRow
  index: number
}

function attachmentRowsForMessage(messageId: string): AttachmentRow[] {
  return stmt('SELECT * FROM attachments WHERE message_id = ? ORDER BY id').all(messageId).map(toAttachmentRow)
}

/**
 * Attach a managed file to one message without moving an existing reference.
 *
 * Upload de-duplication intentionally returns one id while an attachment is
 * still a draft. Once that draft is committed, a second message may refer to
 * the same URL. The old one-row implementation updated `message_id` in place,
 * silently detaching the first message. Reuse an existing row for this message
 * when possible, claim an uncommitted draft otherwise, and create a stable
 * reference row for every additional message/part.
 */
function commitManagedAttachment(
  candidate: AttachmentRow,
  session: Session,
  message: AgentMessage,
  index: number,
  usedIds: Set<string>,
  currentRows: AttachmentRow[]
): string {
  const reusable = currentRows.find(
    (row) => !usedIds.has(row.id) && row.scope === 'session' && row.ownerId === session.id && row.path === candidate.path
  )
  if (reusable !== undefined) {
    if (reusable.status !== 'committed' || reusable.sessionId !== session.id) {
      stmt(
        `UPDATE attachments SET status = 'committed', session_id = ?, owner_id = ?
         WHERE id = ? AND scope = 'session' AND owner_id = ? AND message_id = ?`
      ).run(session.id, session.id, reusable.id, session.id, message.id)
    }
    usedIds.add(reusable.id)
    return reusable.id
  }

  const canClaimDraft =
    !usedIds.has(candidate.id) &&
    candidate.scope === 'session' &&
    candidate.ownerId === session.id &&
    candidate.status === 'draft' &&
    candidate.sessionId === null &&
    candidate.messageId === null
  if (canClaimDraft) {
    stmt(
      `UPDATE attachments SET status = 'committed', message_id = ?, session_id = ?, owner_id = ?
       WHERE id = ? AND scope = 'session' AND owner_id = ? AND status = 'draft'
         AND message_id IS NULL AND session_id IS NULL`
    ).run(message.id, session.id, session.id, candidate.id, session.id)
    usedIds.add(candidate.id)
    return candidate.id
  }

  // A deterministic id makes message replay idempotent. If a malicious or
  // legacy record already occupies it, include the source id and increment
  // until the existing row is either the same reference or a free key.
  const baseId = `${message.id}:managed:${String(index)}`
  let id = baseId
  let suffix = 0
  while (true) {
    const existing = getAttachmentRow(id)
    if (existing === undefined) break
    if (
      existing.scope === 'session' &&
      existing.ownerId === session.id &&
      existing.messageId === message.id &&
      existing.path === candidate.path
    ) {
      usedIds.add(id)
      return id
    }
    suffix++
    id = `${baseId}:${candidate.id}:${String(suffix)}`
  }

  stmt(
    `INSERT INTO attachments
       (id, session_id, message_id, path, size, checksum, scope, status, owner_id, display_name, created_at)
     VALUES (?, ?, ?, ?, ?, ?, 'session', 'committed', ?, ?, ?)`
  ).run(
    id,
    session.id,
    message.id,
    candidate.path,
    candidate.size,
    candidate.checksum,
    session.id,
    candidate.displayName,
    message.createdAt
  )
  usedIds.add(id)
  return id
}

function recordMessageAttachments(session: Session, message: AgentMessage): void {
  const imageParts = message.parts.filter((p): p is Extract<ContentPart, { type: 'image' }> => p.type === 'image')
  /*
    需求:`generate_image` 的产出落成本会话的 draft 附件,地址存在 tool_result 的
    `output.images` 里。它们必须和用户附件一样在这里转 committed —— 否则 7 天后
    被当成过期草稿回收,转录里的 `ncw://` 地址从此是一张裂图、模型点名也解析不到。
    ★ 只收 `ncw://`:截图等内联 data URL 不是文件,绝不能掉进下面「外部路径」那一支。
    ★ 序号接在 image part 之后:`managed:<index>` 与外部 `<messageId>:<index>` 的既有 id
    因此一个都不变,重放旧消息仍然幂等。
  */
  const toolImageRefs = message.parts.flatMap((p) =>
    p.type === 'tool_result' ? (p.output.images ?? []).map((image) => image.dataRef).filter((ref) => parseNcwUrl(ref) !== null) : []
  )
  /*
    ★★ 视频产物走**和图片完全同一条**生命周期。少了这一段,生成的视频会:
      - 停在 draft,七天后的草稿回收把它删掉,转录里的 `ncw://` 从此播不了;
      - 更糟的是下面那句"删除不在保留集合里的附件"会在**消息重放**时
        把这一行删掉 —— 而视频已经下载过一次,再下要重新向上游要(多半已过期)。
    所以这里必须收,而且序号接在图片之后(既有 id 一个都不变,重放仍幂等)。

    ★ 与图片的唯一区别:视频**没有内联那一条路**(`url` 永远是 `ncw://`,
    见 `ToolOutputVideo`),所以它不进 `external` 那一支 —— 那一支会把一个
    `https://…` 当本机绝对路径去 `statSync`,而那是另一个 bug 的入口。
  */
  const toolVideoRefs = message.parts.flatMap((p) =>
    p.type === 'tool_result' ? (p.output.videos ?? []).map((video) => video.url).filter((ref) => parseNcwUrl(ref) !== null) : []
  )

  // 受管理的(ncw://)与外部的(绝对路径)分开处理
  const managed: ManagedAttachmentCandidate[] = []
  const external: Array<{ part: Extract<ContentPart, { type: 'image' }>; index: number }> = []
  const addManaged = (dataRef: string, index: number): void => {
    const loc = parseNcwUrl(dataRef)
    if (loc === null) return

    // A renderer can construct an otherwise valid ncw:// URL for another
    // session. Never let that URL re-home an attachment into this message;
    // theme/export assets likewise do not belong to transcript data.
    if (loc.scope !== 'session' || loc.ownerId !== session.id) return

    const candidateId = attachmentIdOfFileName(loc.fileName)
    const row = getAttachmentRow(candidateId)
    if (
      row !== undefined &&
      row.scope === 'session' &&
      row.ownerId === session.id &&
      basename(row.path) === loc.fileName
    ) {
      managed.push({ row, index })
      return
    }

    // Be tolerant of old/migrated rows whose primary key and file basename
    // diverged. Owner and basename are both required before claiming a row.
    const fallback = findAttachmentByOwnerAndFileName(session.id, loc.fileName)
    if (fallback !== undefined) managed.push({ row: fallback, index })
  }
  imageParts.forEach((part, index) => {
    if (parseNcwUrl(part.dataRef) === null) {
      external.push({ part, index })
      return
    }
    addManaged(part.dataRef, index)
  })
  toolImageRefs.forEach((ref, offset) => { addManaged(ref, imageParts.length + offset) })
  // ★ 视频接在图片之后,序号连续 —— 既有条目的 id 因此一个都不变。
  toolVideoRefs.forEach((ref, offset) => { addManaged(ref, imageParts.length + toolImageRefs.length + offset) })

  const currentRows = attachmentRowsForMessage(message.id)
  const usedIds = new Set<string>()
  const managedIds = managed.map(({ row, index }) =>
    commitManagedAttachment(row, session, message, index, usedIds, currentRows)
  )

  for (const { part, index } of external) {
    const path = part.dataRef
    let size = 0
    let checksum: string | null = null
    try {
      const st = statSync(path)
      if (st.isFile()) {
        size = st.size
        // 附件通常很小，保存校验和能在清理/诊断时区分「同名被替换」；
        // 读取失败时仍保留引用，不能把一条合法消息写入变成失败。
        try { checksum = createHash('sha256').update(readFileSync(path)).digest('hex') } catch { /* 仅缺校验和 */ }
      }
    } catch {
      // 引用可能来自另一台设备；保留记录，清理时会报告文件不存在。
    }
    const id = `${message.id}:${String(index)}`
    stmt(
      `INSERT INTO attachments (id, session_id, message_id, path, size, checksum, scope, status, owner_id, created_at)
       VALUES (?, ?, ?, ?, ?, ?, 'session', 'committed', ?, ?)
       ON CONFLICT (id) DO UPDATE SET session_id = excluded.session_id,
         message_id = excluded.message_id, path = excluded.path, size = excluded.size,
         checksum = excluded.checksum, created_at = excluded.created_at`
    ).run(id, session.id, message.id, path, size, checksum, session.id, message.createdAt)
  }

  const externalIds = external.map(({ index }) => `${message.id}:${String(index)}`)
  const keepIds = [...new Set([...managedIds, ...externalIds])]
  if (keepIds.length === 0) {
    stmt('DELETE FROM attachments WHERE message_id = ?').run(message.id)
  } else {
    stmt(`DELETE FROM attachments WHERE message_id = ? AND id NOT IN (${keepIds.map(() => '?').join(',')})`).run(message.id, ...keepIds)
  }
}

/** `01J8X.png` → `01J8X`。磁盘文件名的主干就是附件主键 */
function attachmentIdOfFileName(fileName: string): string {
  const i = fileName.lastIndexOf('.')
  return i <= 0 ? fileName : fileName.slice(0, i)
}

/**
 * message_commit 的唯一落盘入口；同一 message id 重放时幂等。
 *
 * ★ `run_id` 用 `COALESCE(excluded.run_id, messages.run_id)` 合并,不能直接
 * `= excluded.run_id`。`replaceHistory` 会把整段历史原样重写一遍(编辑消息、
 * 与渲染层对账都走它),而它手上只有 `AgentMessage`,没有 run 归属 ——
 * 直接赋值的话,用户改一个错别字就会把这条会话**所有**历史轮次的用量读数抹成空。
 */
function writeMessage(session: Session, message: AgentMessage, ordinal: number, runId?: string): void {
  const existing = stmt('SELECT session_id FROM messages WHERE id = ?').get(message.id) as Record<string, unknown> | undefined
  if (existing !== undefined && String(existing['session_id']) !== session.id) {
    throw new Error(`消息 ${message.id} 已属于另一个会话`)
  }
  stmt(
    `INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at, run_id, internal)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET ordinal = excluded.ordinal, parts = excluded.parts, role = excluded.role,
       schema_version = excluded.schema_version, created_at = excluded.created_at,
       run_id = COALESCE(excluded.run_id, messages.run_id), internal = excluded.internal`
  ).run(
    message.id,
    session.id,
    ordinal,
    message.role,
    JSON.stringify(message.parts),
    message.schemaVersion,
    message.createdAt,
    runId ?? null,
    message.internal === true ? 1 : 0
  )
  upsertFts(session, message)
  recordMessageAttachments(session, message)
}

/**
 * 只换一条消息的 parts —— **前提是它还是读出来时的那一份**(比较并替换)。
 *
 * 需求:旧转录里内联的 base64 图在被读到时转存成附件,消息里换成 `ncw://` 地址。
 * 转存的那一小段时间里用户可能编辑、删除了这条消息,或者导入同步改写了它:
 * 那时这次替换必须放弃,而不是拿一份旧快照盖回去。返回是否真的写了。
 *
 * ★ 走 `writeMessage`:附件登记(draft → committed、清掉不再引用的行)与全文索引
 * 和正常提交完全同一条路。ordinal、run 归属、时间都不动。
 */
export function replaceMessagePartsIfUnchanged(
  sessionId: string,
  messageId: string,
  expectedParts: string,
  parts: ContentPart[]
): boolean {
  return tx(() => {
    const session = getSession(sessionId)
    if (session === undefined) return false
    const row = stmt(
      'SELECT id, role, parts, schema_version, created_at, internal, ordinal FROM messages WHERE id = ? AND session_id = ?'
    ).get(messageId, sessionId) as Record<string, unknown> | undefined
    if (row === undefined || String(row['parts']) !== expectedParts) return false
    writeMessage(session, { ...messageOfRow(row), parts }, Number(row['ordinal']))
    return true
  })
}

export function commitMessage(sessionId: string, message: AgentMessage, runId?: string): void {
  tx(() => {
    const session = getSession(sessionId)
    if (session === undefined) throw new Error(`会话不存在: ${sessionId}`)
    const existing = stmt('SELECT session_id, ordinal FROM messages WHERE id = ?').get(message.id) as Record<string, unknown> | undefined
    if (existing !== undefined && String(existing['session_id']) !== sessionId) {
      throw new Error(`消息 ${message.id} 已属于另一个会话`)
    }
    const ordinal = existing === undefined
      ? Number((stmt('SELECT COALESCE(MAX(ordinal), -1) AS n FROM messages WHERE session_id = ?').get(sessionId) as Record<string, unknown>)['n'] ?? -1) + 1
      : Number(existing['ordinal'])
    writeMessage(session, message, ordinal, runId)
    /*
      ★ 用户消息一提交就留下**消费回执**,和消息在**同一个事务**里 —— 见 schema 第 34 条。

      这是「这条输入已经执行过」的唯一持久事实,它必须和消息同生:分开两次写的话,
      中间任何一次失败(或者进程被杀)都会留下一条已落盘却「没被消费过」的消息,
      而队列清理、重启恢复都会据此再执行一遍。

      assistant / tool 消息不记:它们不是「用户交进来、可能被重复投递」的输入。
    */
    if (message.role === 'user') {
      stmt('INSERT OR IGNORE INTO session_input_receipts (session_id, message_id, committed_at) VALUES (?, ?, ?)')
        .run(sessionId, message.id, message.createdAt)
    }
    putSession({ ...session, updatedAt: Math.max(session.updatedAt, message.createdAt) })
  })
}

/**
 * 给定的 messageId 里,**哪些已经有消费回执**。
 *
 * ★ 队列判定唯一的收口:插话队列、定时任务、重试路径问的都是同一个问题,
 *   答案只可能来自这里。返回数组而不是 Set —— SQL 层不替调用方决定形状。
 *
 * ★ 只查**指定会话**的行。跨会话的同名 messageId 读不到:别的会话的回执
 *   不构成「这次输入提交过」的证据,读串了就等于放行了一条本该被拒的输入。
 *
 * ★ 参数化 + `json_each`,不把 ids 拼进 SQL 字符串(那会给每一种长度在
 *   `db/index.ts` 的 prepared 缓存里留一条永不失效的语句,见 `sessionSubtreeIds`)。
 *   不解析任何正文。**不做静默截断** —— 实际队列最多 20 条,但给了多少就查多少,
 *   少查就是少判,也就是可能再执行一次。
 */
export function committedInputIds(sessionId: string, ids: readonly string[]): string[] {
  const unique = [...new Set(ids)]
  if (unique.length === 0) return []
  return stmt(
    `SELECT message_id AS id FROM session_input_receipts
      WHERE session_id = ? AND message_id IN (SELECT value FROM json_each(?))`
  ).all(sessionId, JSON.stringify(unique)).map((row) => String((row as Record<string, unknown>)['id']))
}

export function getHistory(sessionId: string): readonly AgentMessage[] {
  return stmt('SELECT id, role, parts, schema_version, created_at, internal FROM messages WHERE session_id = ? ORDER BY ordinal, id').all(sessionId).map((row) => {
    const r = row as Record<string, unknown>
    const parts: ContentPart[] = (() => {
      try { return parse<ContentPart[]>(r['parts']) } catch { return [] }
    })()
    return {
      id: String(r['id']),
      role: String(r['role']) as AgentMessage['role'],
      parts,
      createdAt: Number(r['created_at']),
      schemaVersion: Number(r['schema_version'] ?? 1) as 1,
      ...(Number(r['internal'] ?? 0) !== 0 ? { internal: true } : {})
    }
  })
}

/**
 * 一条会话里**最后一条助手消息**。子代理交差的正文就是它 ——
 * 为了取这一条去 `getHistory` 解析整段子转录,在长子任务上是成百上千条消息的白读。
 */
export function lastAssistantMessage(sessionId: string): AgentMessage | undefined {
  const row = stmt(
    `SELECT id, role, parts, schema_version, created_at, internal FROM messages
     WHERE session_id = ? AND role = 'assistant' ORDER BY ordinal DESC, id DESC LIMIT 1`
  ).get(sessionId) as Record<string, unknown> | undefined
  if (row === undefined) return undefined
  const parts: ContentPart[] = (() => {
    try { return parse<ContentPart[]>(row['parts']) } catch { return [] }
  })()
  return {
    id: String(row['id']),
    role: 'assistant',
    parts,
    createdAt: Number(row['created_at']),
    schemaVersion: Number(row['schema_version'] ?? 1) as 1,
    ...(Number(row['internal'] ?? 0) !== 0 ? { internal: true } : {})
  }
}

/** 一条会话有几条消息 —— 元数据卡片要的就是这个数,不必为它把整段转录读出来 */
export function countMessages(sessionId: string): number {
  const row = stmt('SELECT COUNT(*) AS n FROM messages WHERE session_id = ?').get(sessionId) as Record<string, unknown> | undefined
  return Number(row?.['n'] ?? 0)
}

/**
 * 最后一条**带目标标记**(`goal_status` part)的助手消息 —— 目标重载恢复只看它。
 *
 * ★ 需求:打开一条长会话时,`restoreGoal` 原先要 `getHistory` 把整段转录(连同每次工具输出)
 *   全部 `JSON.parse` 一遍,只为找最后一条标记;主进程同步卡在这里,首页历史就迟迟回不来。
 *   这里先用 LIKE 在原文上筛(不解析),再逐条解析确认 —— `_` 是 LIKE 通配、文本里
 *   也可能恰好出现这串字符,所以筛出来的只是候选,真有那个 part 才算。
 * ★ 和 `restorableGoalCondition` 等价:它从后往前找最后一条助手消息里的最后一个标记,
 *   而那条消息正是这里返回的这一条。
 */
export function lastGoalStatusMessage(sessionId: string): AgentMessage | undefined {
  const rows = stmt(
    `SELECT id, role, parts, schema_version, created_at, internal FROM messages
     WHERE session_id = ? AND role = 'assistant' AND parts LIKE '%"type":"goal_status"%'
     ORDER BY ordinal DESC, id DESC`
  ).iterate(sessionId) as IterableIterator<Record<string, unknown>>
  for (const row of rows) {
    const message = messageOfRow(row)
    if (message.parts.some((part) => part.type === 'goal_status')) return message
  }
  return undefined
}

function messageOfRow(r: Record<string, unknown>): AgentMessage {
  const parts: ContentPart[] = (() => {
    try { return parse<ContentPart[]>(r['parts']) } catch { return [] }
  })()
  return {
    id: String(r['id']),
    role: String(r['role']) as AgentMessage['role'],
    parts,
    createdAt: Number(r['created_at']),
    schemaVersion: Number(r['schema_version'] ?? 1) as 1,
    ...(Number(r['internal'] ?? 0) !== 0 ? { internal: true } : {})
  }
}

/**
 * 这条会话里用户**亲手发的**最近几条消息,新的在前。输入框「建议下一步」读的就是它。
 *
 * ★ 不复用 `getHistoryPage`:一轮里几百次工具调用会把前面几条提问整个挤出一页。
 * ★ `role = 'user'` 不等于「用户说的话」—— 工具结果也以 user 角色落库;
 *   SQL 先用 `json_each` 挑出带 text 部件的那些(便宜),再交给 `isTurnStart` 认定。
 */
export function recentSessionPrompts(sessionId: string, limit: number): AgentMessage[] {
  const rows = stmt(
    `SELECT id, role, parts, schema_version, created_at, internal FROM messages
     WHERE session_id = ? AND role = 'user' AND internal = 0
       AND EXISTS (SELECT 1 FROM json_each(messages.parts) WHERE json_extract(value, '$.type') = 'text')
     ORDER BY ordinal DESC, id DESC LIMIT ?`
  ).all(sessionId, limit) as Record<string, unknown>[]
  return rows.map(messageOfRow).filter(isTurnStart)
}

/**
 * 最后一条**非内部**的助手消息。`lastAssistantMessage` 不挑 internal,而那类消息
 * (目标 kickoff 之类)不是给用户看的回复,拿它去猜用户下一句是在猜一段他没读过的话。
 */
export function lastVisibleAssistantMessage(sessionId: string): AgentMessage | undefined {
  const row = stmt(
    `SELECT id, role, parts, schema_version, created_at, internal FROM messages
     WHERE session_id = ? AND role = 'assistant' AND internal = 0 ORDER BY ordinal DESC, id DESC LIMIT 1`
  ).get(sessionId) as Record<string, unknown> | undefined
  return row === undefined ? undefined : messageOfRow(row)
}

/**
 * 这个工作区里用户发过的指令,新的在前、去重,给输入框的历史补全用。
 *
 * ★ 两道上限是给**主进程**的:node:sqlite 是同步的,一个攒了几年对话的工作区不能让
 *   一次输入框挂载卡住整个应用。先限最近 `sessions` 个顶层会话(子代理会话不是用户打的),
 *   再限 `scan` 行。
 * ★ 斜杠命令在库里是**展开后**的整份模板(`applyCommand` 在发送时展开),所以超长、
 *   多行的一律不要 —— 用户从没打过它们,补出来只会是一大段陌生文字。
 */
export function recentWorkspacePrompts(
  workspaceId: string,
  { sessions = 200, scan = 2000, limit = 500 }: { sessions?: number; scan?: number; limit?: number } = {}
): string[] {
  const rows = stmt(
    `SELECT m.id, m.role, m.parts, m.schema_version, m.created_at, m.internal FROM messages m
     WHERE m.session_id IN (
         SELECT id FROM sessions WHERE workspace_id = ? AND parent_session_id IS NULL
         ORDER BY updated_at DESC LIMIT ?)
       AND m.role = 'user' AND m.internal = 0
     ORDER BY m.created_at DESC, m.ordinal DESC LIMIT ?`
  ).all(workspaceId, sessions, scan) as Record<string, unknown>[]
  const seen = new Set<string>()
  const out: string[] = []
  for (const row of rows) {
    const message = messageOfRow(row)
    if (!isTurnStart(message)) continue
    const text = message.parts.flatMap((p) => (p.type === 'text' ? [p.text] : [])).join('').trim()
    if (text === '' || text.length > 500 || text.split('\n').length > 3 || seen.has(text)) continue
    seen.add(text)
    out.push(text)
    if (out.length >= limit) break
  }
  return out
}

/** 页首往前补到一轮开头时,最多再多读这么多条 —— 一轮里几百次工具调用也不该让一页无界 */
const PAGE_TURN_EXTENSION_MAX = 500

/**
 * 转录的一页:`beforeMessageId` 之前(缺省 = 最新)的 `limit` 条,**从一轮的开头切起**。
 *
 * ★ 页首必须是一条可见提问:从一轮中间切开的话,页首那条 tool_result 找不到它的 tool_call,
 * 卡片画不出来。所以先按条数取,再往前补到最近的一轮开头(有上限)。
 * ★ 只解析这一页的 parts —— 不为一页显示去解析整段会话。
 */
export function getHistoryPage(
  sessionId: string,
  limit: number,
  beforeMessageId?: string
): { messages: AgentMessage[]; hasMore: boolean } {
  let upper = Number.MAX_SAFE_INTEGER
  if (beforeMessageId !== undefined) {
    const anchor = stmt('SELECT ordinal FROM messages WHERE id = ? AND session_id = ?').get(beforeMessageId, sessionId) as Record<string, unknown> | undefined
    if (anchor === undefined) return { messages: [], hasMore: false }
    upper = Number(anchor['ordinal'])
  }
  const rows = stmt(
    `SELECT id, role, parts, schema_version, created_at, internal, ordinal FROM messages
     WHERE session_id = ? AND ordinal < ? ORDER BY ordinal DESC, id DESC LIMIT ?`
  ).all(sessionId, upper, Math.max(1, Math.floor(limit))) as Array<Record<string, unknown>>
  // Keep newest-first until the boundary is found, avoiding hundreds of queries and array unshifts in a long turn.
  const messages = rows.map(messageOfRow)
  let first = rows.at(-1)
  const oldest = messages.at(-1)
  if (first !== undefined && oldest !== undefined && !isTurnStart(oldest)) {
    const previousRows = stmt(
      `SELECT id, role, parts, schema_version, created_at, internal, ordinal FROM messages
       WHERE session_id = ? AND ordinal < ? ORDER BY ordinal DESC, id DESC LIMIT ?`
    ).iterate(sessionId, Number(first['ordinal']), PAGE_TURN_EXTENSION_MAX) as IterableIterator<Record<string, unknown>>
    for (const previous of previousRows) {
      const message = messageOfRow(previous)
      messages.push(message)
      first = previous
      if (isTurnStart(message)) break
    }
  }
  const hasMore = first !== undefined && stmt(
    'SELECT 1 FROM messages WHERE session_id = ? AND ordinal < ? LIMIT 1'
  ).get(sessionId, Number(first['ordinal'])) !== undefined
  return { messages: messages.reverse(), hasMore }
}

/** 权威历史对账；未变化的消息保留正文、索引、附件和 run 归属。 */
export function replaceHistory(sessionId: string, messages: readonly AgentMessage[]): void {
  tx(() => {
    const session = getSession(sessionId)
    if (session === undefined) throw new Error(`会话不存在: ${sessionId}`)

    const existing = new Map(
      stmt('SELECT id, ordinal, role, parts, schema_version, created_at, internal FROM messages WHERE session_id = ?').all(sessionId)
        .map((row) => [String(row['id']), row] as const)
    )
    const ids = new Set<string>()
    let renumber = false
    const entries = messages.map((message, ordinal) => {
      if (ids.has(message.id)) throw new Error(`消息 ${message.id} 在转录中重复`)
      ids.add(message.id)
      const previous = existing.get(message.id)
      if (previous === undefined) {
        const owner = stmt('SELECT session_id FROM messages WHERE id = ?').get(message.id)
        if (owner !== undefined && String(owner['session_id']) !== sessionId) {
          throw new Error(`消息 ${message.id} 已属于另一个会话`)
        }
      }
      if (previous !== undefined && Number(previous['ordinal']) !== ordinal) renumber = true
      const changed = previous === undefined || previous['role'] !== message.role
        || previous['parts'] !== JSON.stringify(message.parts)
        || Number(previous['schema_version']) !== message.schemaVersion
        || Number(previous['created_at']) !== message.createdAt
        || Number(previous['internal'] ?? 0) !== (message.internal === true ? 1 : 0)
      return { message, ordinal, changed }
    })
    const removed = [...existing.keys()].filter((id) => !ids.has(id))
    const updatedAt = messages.reduce((latest, message) => Math.max(latest, message.createdAt), session.updatedAt)
    if (!renumber && removed.length === 0 && entries.every((entry) => !entry.changed)) {
      if (updatedAt !== session.updatedAt) putSession({ ...session, updatedAt })
      return
    }

    if (renumber) stmt('UPDATE messages SET ordinal = -ordinal - 1 WHERE session_id = ?').run(sessionId)
    for (const id of removed) stmt('DELETE FROM messages WHERE id = ?').run(id)
    if (removed.length > 0) {
      deleteOrphanSessionFts(stmt, sessionId)
    }
    for (const { message, ordinal, changed } of entries) {
      if (changed) writeMessage(session, message, ordinal)
      else if (renumber) stmt('UPDATE messages SET ordinal = ? WHERE id = ?').run(ordinal, message.id)
    }
    putSession({ ...session, updatedAt })
  })
}

export function setRunRecord(id: string, sessionId: string, status: string, startedAt: number, endedAt?: number): void {
  stmt(
    `INSERT INTO runs (id, session_id, status, started_at, ended_at)
     VALUES (?, ?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET status = excluded.status, ended_at = excluded.ended_at`
  ).run(id, sessionId, status, startedAt, endedAt ?? null)
}

/* ───────────────────────── 改动快照(审查卡 / 撤销·恢复) ───────────────────────── */

/** 单文件超过这个字节数就不入库内容(禁 diff/undo),防一份大文件把两份全文塞进 SQLite。 */
const SNAPSHOT_MAX_BYTES = 1024 * 1024

const sha256 = (text: string): string => createHash('sha256').update(text, 'utf8').digest('hex')

/** 一个文件在采集侧的原始改动(runtime 从 change-recorder 的 PendingChange 映射而来)。 */
export interface FileChangeInput {
  relPath: string
  absPath: string
  before: string | null
  after: string
  changeKind: 'created' | 'modified'
  inWorkspace: boolean
}

/** 撤销/恢复要用的完整快照行(含全文)。 */
export interface FileSnapshotRecord {
  path: string
  absPath: string
  changeKind: ChangeKind
  before: string | null
  after: string | null
  beforeHash: string | null
  afterHash: string
  oversize: boolean
  inWorkspace: boolean
}

/**
 * 落盘一轮的文件改动集。**可叠加**:子代理的 child run 先收尾、把它的改动落到
 * 顶层 rootRunId 下;父 run 后收尾时再叠加自己的 —— 同一文件在两个 run 都被改过时,
 * **保留最早的 before、推进到最新的 after**(与 change-recorder 的轮内合并同义,
 * 只是这里跨父子)。`created` 一旦置位不被后续 modify 翻转。
 */
export function saveFileChangeSet(input: {
  rootRunId: string
  sourceRunId: string
  sessionId: string
  workspaceId: string
  changes: readonly FileChangeInput[]
  at: number
}): void {
  if (input.changes.length === 0) return
  tx(() => {
    stmt(
      `INSERT INTO file_change_sets (run_id, session_id, workspace_id, state, created_at, updated_at)
       VALUES (?, ?, ?, 'applied', ?, ?)
       ON CONFLICT (run_id) DO NOTHING`
    ).run(input.rootRunId, input.sessionId, input.workspaceId, input.at, input.at)

    for (const change of input.changes) {
      const existing = stmt(
        `SELECT before_content, before_hash, change_kind, oversize FROM file_snapshots WHERE run_id = ? AND file_path = ?`
      ).get(input.rootRunId, change.relPath) as Record<string, unknown> | undefined

      // 已有行:沿用它记的 before / change_kind(最早那次);新行:用本次采集的。
      const before = existing !== undefined ? (existing['before_content'] as string | null) : change.before
      const beforeHash =
        existing !== undefined
          ? (existing['before_hash'] as string | null)
          : change.before === null
            ? null
            : sha256(change.before)
      const changeKind = existing !== undefined ? String(existing['change_kind']) : change.changeKind
      const priorOversize = existing !== undefined && Number(existing['oversize']) === 1

      const after = change.after
      const afterHash = sha256(after)
      const oversize =
        priorOversize ||
        (before !== null && Buffer.byteLength(before, 'utf8') > SNAPSHOT_MAX_BYTES) ||
        Buffer.byteLength(after, 'utf8') > SNAPSHOT_MAX_BYTES
      // 超限的文件不做 O(n·m) 的行 diff(可能是几 MB),计数留 0。
      const { additions, deletions } = oversize ? { additions: 0, deletions: 0 } : countLineDiff(before, after)

      stmt(
        `INSERT INTO file_snapshots
           (run_id, file_path, source_run_id, abs_path, change_kind,
            before_content, after_content, before_hash, after_hash,
            additions, deletions, oversize, in_workspace, created_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT (run_id, file_path) DO UPDATE SET
           source_run_id  = excluded.source_run_id,
           after_content  = excluded.after_content,
           after_hash     = excluded.after_hash,
           additions      = excluded.additions,
           deletions      = excluded.deletions,
           oversize       = excluded.oversize`
      ).run(
        input.rootRunId,
        change.relPath,
        input.sourceRunId,
        change.absPath,
        changeKind,
        oversize ? null : before,
        oversize ? null : after,
        beforeHash,
        afterHash,
        additions,
        deletions,
        oversize ? 1 : 0,
        change.inWorkspace ? 1 : 0,
        input.at
      )
    }

    // 集合级 +X −Y 与文件数从当前全部快照行重算(叠加后也对)。
    const agg = stmt(
      `SELECT COUNT(*) AS fc, COALESCE(SUM(additions), 0) AS a, COALESCE(SUM(deletions), 0) AS d
       FROM file_snapshots WHERE run_id = ?`
    ).get(input.rootRunId) as Record<string, unknown>
    stmt(`UPDATE file_change_sets SET file_count = ?, additions = ?, deletions = ?, updated_at = ? WHERE run_id = ?`).run(
      Number(agg['fc'] ?? 0),
      Number(agg['a'] ?? 0),
      Number(agg['d'] ?? 0),
      input.at,
      input.rootRunId
    )
  })
}

/** 一轮的改动集摘要(不含全文)—— 审查卡与右侧 tab 列表用。空集返回 undefined。 */
export function getFileChangeSet(runId: string): ReviewChangeSet | undefined {
  const set = stmt(
    `SELECT run_id, session_id, workspace_id, state, file_count, additions, deletions
     FROM file_change_sets WHERE run_id = ?`
  ).get(runId) as Record<string, unknown> | undefined
  if (set === undefined) return undefined
  const files = stmt(
    `SELECT file_path, change_kind, additions, deletions, oversize, in_workspace
     FROM file_snapshots WHERE run_id = ? ORDER BY file_path`
  )
    .all(runId)
    .map((row): ReviewFileEntry => {
      const r = row as Record<string, unknown>
      return {
        path: String(r['file_path']),
        changeKind: String(r['change_kind']) as ChangeKind,
        additions: Number(r['additions'] ?? 0),
        deletions: Number(r['deletions'] ?? 0),
        oversize: Number(r['oversize']) === 1,
        inWorkspace: Number(r['in_workspace']) === 1
      }
    })
  return {
    runId: String(set['run_id']),
    sessionId: String(set['session_id']),
    workspaceId: String(set['workspace_id']),
    state: String(set['state']) as ChangeSetState,
    fileCount: Number(set['file_count'] ?? 0),
    additions: Number(set['additions'] ?? 0),
    deletions: Number(set['deletions'] ?? 0),
    files
  }
}

/** 单文件的 before/after 全文,diff 预览用。 */
export function getFileSnapshotDiff(runId: string, path: string): ReviewFileDiff | undefined {
  const row = stmt(
    `SELECT file_path, change_kind, before_content, after_content, oversize
     FROM file_snapshots WHERE run_id = ? AND file_path = ?`
  ).get(runId, path) as Record<string, unknown> | undefined
  if (row === undefined) return undefined
  return {
    path: String(row['file_path']),
    changeKind: String(row['change_kind']) as ChangeKind,
    before: (row['before_content'] as string | null) ?? '',
    after: (row['after_content'] as string | null) ?? '',
    oversize: Number(row['oversize']) === 1
  }
}

/** 撤销/恢复要用的完整快照行(含 before/after 全文与 hash)。 */
export function listFileSnapshots(runId: string): FileSnapshotRecord[] {
  return stmt(
    `SELECT file_path, abs_path, change_kind, before_content, after_content,
            before_hash, after_hash, oversize, in_workspace
     FROM file_snapshots WHERE run_id = ? ORDER BY file_path`
  )
    .all(runId)
    .map((row): FileSnapshotRecord => {
      const r = row as Record<string, unknown>
      return {
        path: String(r['file_path']),
        absPath: String(r['abs_path']),
        changeKind: String(r['change_kind']) as ChangeKind,
        before: (r['before_content'] as string | null) ?? null,
        after: (r['after_content'] as string | null) ?? null,
        beforeHash: (r['before_hash'] as string | null) ?? null,
        afterHash: String(r['after_hash']),
        oversize: Number(r['oversize']) === 1,
        inWorkspace: Number(r['in_workspace']) === 1
      }
    })
}

/** 改动集是否存在(handler 校验用)。 */
export function getFileChangeSetState(runId: string): ChangeSetState | undefined {
  const row = stmt(`SELECT state FROM file_change_sets WHERE run_id = ?`).get(runId) as Record<string, unknown> | undefined
  return row === undefined ? undefined : (String(row['state']) as ChangeSetState)
}

/** 翻转整轮的撤销态(applied ⇄ reverted)。 */
export function setChangeSetState(runId: string, state: ChangeSetState, at: number): void {
  stmt(`UPDATE file_change_sets SET state = ?, updated_at = ? WHERE run_id = ?`).run(state, at, runId)
}

/**
 * 一条会话 + 它派生出来的全部子代理转录(任意深度),root 在前。
 *
 * ★ 固定 SQL、单 root,多 root 由调用方在 JS 里循环。动态 `IN (?,?,…)` 会给
 * **每一种长度**在 `db/index.ts` 的 prepared 缓存里留一条永不失效的语句。
 *
 * ★ `UNION` 而不是 `UNION ALL`:畸形数据造出的环会被去重终止,而 `UNION ALL`
 * 会把同步的 `DatabaseSync` 转死 —— 也就是把整个主进程转死。
 * `putSession` 里那道自环护栏是第一层,这是第二层。
 */
export function sessionSubtreeIds(rootId: string): string[] {
  return stmt(
    `WITH RECURSIVE subtree(id) AS (
       SELECT id FROM sessions WHERE id = ?
       UNION
       SELECT s.id FROM sessions s JOIN subtree t ON s.parent_session_id = t.id
     )
     SELECT id FROM subtree`
  ).all(rootId).map((row) => String((row as Record<string, unknown>)['id']))
}

/**
 * 删一条会话,连同它派生出来的全部子代理转录。返回真正删掉的那些 id。
 *
 * ★ 级联写在 repo 而不是 `state/store.ts`:`deleteByAge` 和 `deleteAllHistory`
 * 都是在**这个文件内部**调 `deleteSession` 的,级联放外面等于让那两条路径
 * 继续留下子转录 —— 而子转录在过滤之后是**不朽的**:没有任何枚举看得见它,
 * 也没有任何清理路径遍历得到它。
 */
export function deleteSession(id: string): string[] {
  return tx(() => {
    const ids = sessionSubtreeIds(id)
    // 先叶后根。sessions 之间没有真外键(理由见 schema.ts 第 10 条),
    // 顺序其实无所谓;保持和 CASCADE 同向,便于将来真加外键时不用重排。
    for (const sid of [...ids].reverse()) {
      deleteSessionFts(stmt, sid)
      stmt('DELETE FROM sessions WHERE id = ?').run(sid)
      // 草稿附件没有 session_id（外键要求上传时会话可以尚未创建），
      // 因此不能只依赖 CASCADE；owner_id 是它们的会话归属。
      stmt("DELETE FROM attachments WHERE scope = 'session' AND owner_id = ?").run(sid)
      removeKv(`session.input.${sid}`)
      // 对话里 widget 的持久化状态(`ipc/widget.ts`)。同 session.input:kv 没有外键,得手删
      removeKv(`session.widget.${sid}`)
      /*
        ★★ 导入来的会话留 tombstone,**不删映射行**。

        删掉的话,下一轮自动同步看不到任何记录,于是把这条会话原地复活 ——
        而它是用户明确删掉的。墓碑放在这里(而不是 `ipc/sessions.ts`)是因为
        这个函数是**唯一**的会话删除出口:单条删除、按时长清理、子树级联
        全都经过它。拦在 UI 那一层的话,按时长清理会绕过去。
      */
      suppressImportMappingsByTarget(sid, 'session', Date.now())
    }
    return ids
  })
}

/**
 * 启动期凭证格式迁移专用:**列出全部物理行**,包括当前没激活的账户作用域
 * 和平台全局 token。业务代码不该用它 —— 绕过 `physicalCredentialRef` 就可能
 * 把 A 账户的密钥写给 B 账户。
 */
export function listAllCredentialBlobs(): Array<{ ref: string; blob: Uint8Array }> {
  return stmt('SELECT ref, blob FROM credentials ORDER BY ref').all().flatMap((row) => {
    const blob = row['blob']
    return blob instanceof Uint8Array ? [{ ref: String(row['ref']), blob }] : []
  })
}

/** 启动期迁移专用:按物理 ref 原位替换密文,不做账户作用域转换。 */
export function putCredentialBlobAtPhysicalRef(ref: string, blob: Uint8Array): void {
  stmt('UPDATE credentials SET blob = ? WHERE ref = ?').run(blob, ref)
}

/**
 * 导出用的清单。
 *
 * ★★ **只返回当前作用域的密文,并且把物理键还原成逻辑 ref。**
 *
 * 两条都缺一不可:
 * - 不按作用域收窄 → 导出的备份里会带上**别的账户**的供应商密钥;
 * - 不还原逻辑名 → 导出的备份里每个 ref 都顶着一段账户 id 前缀,
 *   再导入到别处时那些前缀会被当成 ref 的一部分,密钥就再也对不上了。
 *
 * 平台/全局键(`nextcowork:client-*`、`config-sync:*`)**不进这个列表**:
 * 它们是设备身份,不是用户配置,备份里带出去等于把登录凭证复制一份。
 */
export function listCredentials(): Array<{ ref: string; blob: Uint8Array }> {
  const scope = currentConfigScope()
  const out: Array<{ ref: string; blob: Uint8Array }> = []
  for (const row of stmt('SELECT ref, blob FROM credentials ORDER BY ref').all()) {
    const r = row as Record<string, unknown>
    const blob = r['blob']
    if (!(blob instanceof Uint8Array)) continue
    // `local` 作用域下这一句同时是过滤器:裸 ref 里混着的全局键会被它挡掉。
    const logical = logicalCredentialRef(String(r['ref']), scope)
    if (logical === null) continue
    out.push({ ref: logical, blob })
  }
  return out
}

/** 仅返回数据库中实际存过的搜索配置；不要和目录默认值混用。 */
export function listStoredSearchProviders(): SearchProviderConfig[] {
  return stmt('SELECT json FROM search_providers ORDER BY priority, id')
    .all()
    .map((r) => parse<SearchProviderConfig>(r['json']))
}

/** 返回当前可导出的结构快照；凭证需由 storage handler 另行解密/加密。 */
export function exportDataSnapshot(): Omit<DataExport, 'encryptedCredentials'> {
  const settings = getSettings()
  // 备份目录是设备本地路径，不能随普通导出迁移到另一台机器；频率仍是
  // 用户偏好，可以安全导出。导入时也会保留当前设备的目录。
  settings.data = { ...settings.data, backupDirectory: null }
  settings.shell = 'system'
  return {
    type: 'nextcowork-data-export',
    version: 1,
    exportedAt: new Date().toISOString(),
    settings,
    workspaces: listWorkspaces(),
    sessions: listExportableSessionDetails(),
    providers: listProviders().filter((p) => p.id !== 'nextcowork'),
    aliases: listAliases().filter((a) => a.providerId !== 'nextcowork'),
    mcpServers: listMcpServers().filter((server) => server.workspaceId === undefined),
    // 导出的是实际配置行，不把目录里的默认项伪造成用户配置。
    searchProviders: listStoredSearchProviders(),
    disabledSkillIds: (() => {
      const raw = getKv<unknown>('skills.disabled', [])
      return Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : []
    })()
  }
}

/** 在一个 SQLite 事务里逐项合并导入数据。 */
export function mergeDataExport(data: DataExport): ImportApplyResult {
  return tx(() => {
    let imported = 0
    let overwritten = 0
    let skipped = 0
    let workspacesImported = 0
    let sessionsImported = 0
    let messagesImported = 0

    const localSettings = getSettings()
    // 设置没有可靠的 updatedAt，导入文件由用户明确确认，按整块导入。
    updateSettings({
      ...data.settings,
      shell: localSettings.shell,
      data: {
        ...data.settings.data,
        // 这个路径只属于当前设备，普通导入不能把另一台机器的路径写进来。
        backupDirectory: localSettings.data.backupDirectory
      }
    })
    const settingsImported = JSON.stringify(localSettings) !== JSON.stringify(getSettings())

    for (const w of data.workspaces) {
      const local = getWorkspace(w.id)
      const decision = dataMergeDecision(local, w)
      if (decision !== 'skip') {
        const incoming = normalizeEnvironmentRef(w.environment)
        const environment: EnvironmentRef = local ? normalizeEnvironmentRef(local.environment)
          : incoming.kind === 'local' ? incoming : { kind: 'unbound' }
        const rootPath = local?.rootPath ?? w.rootPath
        putWorkspace({ ...w, rootPath, environment, unavailable: environment.kind !== 'local' || !existsSync(rootPath) })
        imported++
        workspacesImported++
        if (decision === 'overwrite') overwritten++
      } else skipped++
    }
    for (const p of data.providers) {
      if (p.id === 'nextcowork') continue
      const local = listProviders().find((x) => x.id === p.id)
      const decision = dataMergeDecision(local, p)
      if (decision !== 'skip') {
        // `credentialRef` is not trusted import data. Preserve a legacy ref
        // already owned by this local provider; otherwise derive the same ref
        // as the provider IPC write boundary.
        putProvider({
          ...p,
          credentialRef: local?.credentialRef ?? providerCredentialRef(p.id)
        })
        imported++
        if (decision === 'overwrite') overwritten++
      } else skipped++
    }
    for (const a of data.aliases) {
      if (a.providerId === 'nextcowork') continue
      const local = listAliases().find((x) => x.providerId === a.providerId && x.alias === a.alias)
      const decision = dataMergeDecision(local, a)
      if (decision !== 'skip') { putAlias(a); imported++; if (decision === 'overwrite') overwritten++ } else skipped++
    }
    for (const c of data.mcpServers) {
      const local = getMcpServer(c.id)
      if (c.workspaceId !== undefined || local?.workspaceId !== undefined) { skipped++; continue }
      const decision = dataMergeDecision(local, c)
      if (decision !== 'skip') { putMcpServer(c); imported++; if (decision === 'overwrite') overwritten++ } else skipped++
    }
    for (const c of data.searchProviders) {
      const local = listStoredSearchProviders().find((x) => x.id === c.id)
      const decision = dataMergeDecision(local, c)
      if (decision !== 'skip') { putSearchProvider(c); imported++; if (decision === 'overwrite') overwritten++ } else skipped++
    }
    setKv('skills.disabled', data.disabledSkillIds)

    for (const item of data.sessions) {
      /*
        ★ 子代理转录一律丢弃,不计进 imported/skipped —— 它们不是用户实体。

        新导出根本不含它们(`listExportableSessionDetails`),这一条挡的是**旧文件**:
        那时候子会话既没有 `parentSessionId`、又确实被导了出去,原样写回库
        就等于用一次导入把「侧边栏里一堆新对话」这个 bug 完整还原。
        父转录里的子代理收据只带 `childRunId`(进程内路由键,重启即失效),
        不引用会话 id,所以丢掉它们不会在任何地方留下悬空引用。
      */
      if (item.session.parentSessionId !== undefined || item.session.id.includes(':sub:')) continue
      const local = getSession(item.session.id)
      const decision = dataMergeDecision(local, item.session)
      if (decision === 'skip') { skipped++; continue }
      if (decision === 'overwrite') overwritten++
      putSession(item.session)
      replaceHistory(item.session.id, item.messages)
      // 旧导出里的 `contextCheckpoints` 刻意不导:压缩已改成转录里的边界消息,检查点表不再被读取。
      imported++
      sessionsImported++
      messagesImported += item.messages.length
    }
    return { imported, overwritten, skipped, settingsImported, workspacesImported, sessionsImported, messagesImported }
  })
}

export function referencedAttachmentPaths(): string[] {
  return stmt('SELECT DISTINCT path FROM attachments').all().map((r) => String((r as Record<string, unknown>)['path']))
}

export interface AttachmentReferenceRow {
  id: string
  path: string
  size: number
  sessionId: string | null
  messageId: string | null
  ownerId: string | null
  scope: string
  status: string
}

export function attachmentRows(): AttachmentReferenceRow[] {
  return stmt('SELECT id, path, size, session_id, message_id, owner_id, scope, status FROM attachments').all().map((row) => {
    const r = row as Record<string, unknown>
    return {
      id: String(r['id']),
      path: String(r['path']),
      size: Number(r['size'] ?? 0),
      sessionId: r['session_id'] == null ? null : String(r['session_id']),
      messageId: r['message_id'] == null ? null : String(r['message_id']),
      ownerId: r['owner_id'] == null ? null : String(r['owner_id']),
      scope: String(r['scope'] ?? 'session'),
      status: String(r['status'] ?? 'committed')
    }
  })
}

/**
 * 返回指定会话的附件；包括尚未提交、只用 owner_id 归属的草稿。
 *
 * ★ 展开子代理转录,而且是在**这里**展开,不在调用方。
 * `deleteSession` 是级联的,所以「会消失的附件」天然包含子会话那部分;
 * 而 `cleanupPreview` 算字节数、`storage.ts` 的 `withActualAttachmentBytes`
 * 换算真实大小、`sessionAttachmentPaths` 抢在级联之前记路径 —— 这三处**必须**
 * 用同一个集合,否则预览的数字和实际删掉的量对不上,或者磁盘上留下永久孤儿文件。
 * 收在这个函数里,四个调用点一行都不用改,也不会有人漏掉其中一个。
 */
export function attachmentRowsForSessions(sessionIds: readonly string[]): AttachmentReferenceRow[] {
  if (sessionIds.length === 0) return []
  /*
    ★ 传进来的 id **原样保留**,不能只用子树查询的结果:草稿附件可以早于
    `sessions` 行存在(用户新建对话、传了图、还没点发送),那种 id 在表里查不到,
    而它正是这个函数最要紧的一类归属。子树只负责**多**认一些,不负责筛。
  */
  const wanted = new Set([...sessionIds, ...sessionIds.flatMap((id) => sessionSubtreeIds(id))])
  return attachmentRows().filter((row) =>
    row.scope === 'session' &&
    ((row.sessionId !== null && wanted.has(row.sessionId)) ||
      (row.ownerId !== null && wanted.has(row.ownerId)))
  )
}

/** Paths captured before a session cascade removes its attachment rows. */
export function sessionAttachmentPaths(sessionId: string): string[] {
  return attachmentRowsForSessions([sessionId]).map((row) => row.path)
}

/** 当前所有会话附件（调用方负责做路径边界校验）。 */
export function allSessionAttachmentRows(): AttachmentReferenceRow[] {
  return attachmentRows().filter((row) => row.scope === 'session')
}

/**
 * 按会话更新时间取待删 ID，避免 storage 层重复拼接 SQL。
 *
 * ★ 只取顶层。子代理转录不是「一条对话」,不该出现在「删除 N 天前的会话」
 * 那个数字里 —— 它们由 `deleteSession` 跟着父一起走。
 */
export function sessionIdsBefore(cutoff: number): string[] {
  return stmt('SELECT id FROM sessions WHERE updated_at < ? AND parent_session_id IS NULL').all(cutoff)
    .map((row) => String((row as Record<string, unknown>)['id']))
}

export function removeAttachmentRow(id: string): void {
  stmt('DELETE FROM attachments WHERE id = ?').run(id)
}

/** Number of attachment rows that still point at a physical path. */
export function attachmentReferenceCount(path: string, excludeId?: string): number {
  const row = excludeId === undefined
    ? stmt('SELECT COUNT(*) AS n FROM attachments WHERE path = ?').get(path)
    : stmt('SELECT COUNT(*) AS n FROM attachments WHERE path = ? AND id <> ?').get(path, excludeId)
  return Number((row as Record<string, unknown> | undefined)?.['n'] ?? 0)
}

// ─── 上传附件(scope/status,迁移 5) ──────────────────────────────────────────

export interface AttachmentRow {
  id: string
  scope: string
  ownerId: string | null
  sessionId: string | null
  messageId: string | null
  path: string
  size: number
  checksum: string
  status: string
  /** 用户看到的原始名。迁移 6 之前的行是 null,读的时候退回 basename(path) */
  displayName: string | null
  createdAt: number
}

function toAttachmentRow(row: unknown): AttachmentRow {
  const r = row as Record<string, unknown>
  return {
    id: String(r['id']),
    scope: String(r['scope'] ?? 'session'),
    ownerId: r['owner_id'] == null ? null : String(r['owner_id']),
    sessionId: r['session_id'] == null ? null : String(r['session_id']),
    messageId: r['message_id'] == null ? null : String(r['message_id']),
    path: String(r['path']),
    size: Number(r['size'] ?? 0),
    checksum: String(r['checksum'] ?? ''),
    status: String(r['status'] ?? 'committed'),
    displayName: r['display_name'] == null ? null : String(r['display_name']),
    createdAt: Number(r['created_at'] ?? 0)
  }
}

/**
 * 上传落盘后登记。★ 插入的是 **`draft`** —— 文件已经在磁盘上,但还没进任何消息。
 *
 * ★ **只写 `owner_id`,不写 `session_id`。** 后者上有指向 `sessions` 的外键,
 * 而上传发生在发送**之前** —— 新建对话还没发第一条消息时,`sessions` 表里
 * 没有那一行,写进去直接 FOREIGN KEY constraint failed。
 * `session_id` 由 `commitAttachmentsByPath` 在消息提交时补上,那一刻会话必然存在。
 */
export function putDraftAttachment(a: {
  id: string
  scope: string
  ownerId?: string
  path: string
  size: number
  checksum: string
  displayName?: string
  createdAt: number
}): void {
  stmt(
    `INSERT INTO attachments (id, session_id, message_id, path, size, checksum, scope, status, owner_id, display_name, created_at)
     VALUES (?, NULL, NULL, ?, ?, ?, ?, 'draft', ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET path = excluded.path, size = excluded.size`
  ).run(
    a.id,
    a.path,
    a.size,
    a.checksum,
    a.scope,
    a.ownerId ?? null,
    a.displayName ?? null,
    a.createdAt
  )
}

/**
 * 同 scope + 同 owner 内按内容去重。
 *
 * ★ **不做全局去重**:跨会话共用一个物理文件之后,删掉其中一个会话就会
 * 波及另一个仍在引用它的会话。要正确处理就得引用计数,而那点磁盘空间
 * 不值得引入一套引用计数的正确性负担。
 */
export function findAttachmentByChecksum(
  checksum: string,
  scope: string,
  ownerId?: string
): AttachmentRow | undefined {
  // 优先复用重传后的新副本,否则旧的失效记录会让后续每次上传都再次落盘。
  const row =
    ownerId === undefined
      ? stmt(
          `SELECT * FROM attachments WHERE checksum = ? AND scope = ? AND owner_id IS NULL ORDER BY rowid DESC LIMIT 1`
        ).get(checksum, scope)
      : stmt(
          `SELECT * FROM attachments WHERE checksum = ? AND scope = ? AND owner_id = ? ORDER BY rowid DESC LIMIT 1`
        ).get(checksum, scope, ownerId)
  return row == null ? undefined : toAttachmentRow(row)
}

/**
 * 附件行 → 是否属于当前作用域。
 *
 * 判据是**它挂的那个会话**:附件没有自己的归属字段,而 `session_id` 就是它
 * 实际归属的那个会话。
 *
 * `session_id` 为空的行原先一律走 `workspaceScopeVisible('')`(即只有 `local`
 * 可见),理由与 `workspaceScopeVisible` 对 `''` 的处理相同。★ 但 `scope='session'`
 * 的草稿行不适用那条:它的归属早就写在 `owner_id` 上了(`session_id` 有外键,
 * 要等消息提交才能填,见 `putDraftAttachment`),所以改用 `draftOwnerScopeVisible`
 * ——与 `listDraftAttachments` 同一条规则,原先那条对其余 scope 保持不变。
 *
 * 不满足会怎样:登录账户后草稿附件「列得出来、却删不掉」——`listDraftAttachments`
 * 按 owner 判定所以返回它,`getAttachmentRow` 按 `''` 判定所以拿不到,
 * `removeAttachment` 于是静默 return。表现是用户删掉输入框里的图,切一下会话
 * 图片又回来了,全程零报错;`prepareWorkspaceUpload` 也会因此一律抛 `unbound`。
 */
function attachmentScopeVisible(row: AttachmentRow): boolean {
  if (row.sessionId !== null && row.sessionId !== undefined) return sessionScopeVisible(row.sessionId)
  const owner = row.ownerId
  if (row.scope !== 'session' || owner === null || owner === undefined || owner === '') {
    return workspaceScopeVisible('')
  }
  return draftOwnerScopeVisible(owner)
}

/**
 * 草稿附件(`session_id` 还没填)的归属判定:看 `owner_id` 那个会话。
 *
 * ★ 「会话行还不存在」不算不可见:草稿附件恰恰是在会话行落库**之前**上传的
 * (渲染层先铸会话 id,会话行要等首条消息提交才建)。判成不可见的话,新对话里
 * 传的图既恢复不了也删不掉。没有会话行的草稿也不构成跨账户泄露 —— 它还没挂到
 * 任何工作区,自然不属于任何账户。
 */
function draftOwnerScopeVisible(sessionId: string): boolean {
  const row = stmt('SELECT workspace_id FROM sessions WHERE id = ?').get(sessionId)
  return row === undefined || workspaceScopeVisible(String(row['workspace_id']))
}

export function getAttachmentRow(id: string): AttachmentRow | undefined {
  const row = stmt('SELECT * FROM attachments WHERE id = ?').get(id)
  if (row == null) return undefined
  const attachment = toAttachmentRow(row)
  return attachmentScopeVisible(attachment) ? attachment : undefined
}

/**
 * Compatibility lookup for migrated attachment rows whose id no longer
 * matches the basename encoded in an ncw:// URL.  The owner and session scope
 * are part of the predicate; basename alone is never sufficient because two
 * sessions may legitimately contain files with the same name.
 */
export function findAttachmentByOwnerAndFileName(ownerId: string, fileName: string): AttachmentRow | undefined {
  // `ownerId` 就是会话 id(见上面的说明),所以归属判定和会话同一条。
  if (!sessionScopeVisible(ownerId)) return undefined
  const rows = stmt(
    `SELECT * FROM attachments
       WHERE scope = 'session' AND owner_id = ? AND status IN ('draft', 'committed')
       ORDER BY created_at DESC, id DESC`
  ).all(ownerId)
  for (const row of rows) {
    const parsed = toAttachmentRow(row)
    if (basename(parsed.path) === fileName) return parsed
  }
  return undefined
}

/**
 * 某个会话下所有还没发出去的附件 —— 重启后恢复草稿附件区要用。
 *
 * ★ 会话**存在但不在本作用域**时返回空数组。草稿附件是**按会话 id 直接查**的,
 * 不经过 `getSession`,所以它是唯一一条绕过归属判定的入口 —— 少了这一句,
 * 跨账户随便给一个会话 id 就能列出别人的待发附件。
 *
 * ★ 但「会话行还不存在」不算不可见,理由见 `draftOwnerScopeVisible` ——
 * 那条判定现在由它和 `attachmentScopeVisible` 共用:两处各写一份的时候,
 * 「列得出来却删不掉」正是它们漂开之后的症状。
 */
export function listDraftAttachments(sessionId: string): AttachmentRow[] {
  if (!draftOwnerScopeVisible(sessionId)) return []
  return stmt(
    `SELECT * FROM attachments WHERE owner_id = ? AND status = 'draft' ORDER BY created_at`
  )
    .all(sessionId)
    .map(toAttachmentRow)
}

/**
 * 可回收的草稿附件:`status='draft'` 且已过宽限期。
 *
 * ★ 这就是新的孤儿判据。旧判据是「不在表里就删」,它在附件根下只有会话附件时
 * 是对的;现在根下还有 theme/export 和**尚未发送的草稿**,那条判据会静默删掉它们。
 */
export function listStaleDraftAttachments(cutoff: number): AttachmentRow[] {
  return stmt(`SELECT * FROM attachments WHERE status = 'draft' AND created_at < ?`)
    .all(cutoff)
    .map(toAttachmentRow)
}

/**
 * 消息提交时把草稿升为已提交,并**这时才**填 `session_id` / `message_id` ——
 * 那两列上有外键,而上传发生在会话与消息都还不存在的时候。
 */
export function commitAttachmentsByIds(
  ids: readonly string[],
  messageId: string,
  sessionId: string
): void {
  if (ids.length === 0) return
  tx(() => {
    const currentRows = attachmentRowsForMessage(messageId)
    const usedIds = new Set<string>()
    ids.forEach((id, index) => {
      const row = getAttachmentRow(id)
      if (
        row === undefined ||
        row.scope !== 'session' ||
        row.ownerId !== sessionId
      ) return

      // Keep an already-associated row in place. A row committed to another
      // message is never moved; create a reference below instead.
      const current = currentRows.find(
        (candidate) => !usedIds.has(candidate.id) && candidate.path === row.path
      )
      if (current !== undefined) {
        usedIds.add(current.id)
        return
      }

      if (
        !usedIds.has(row.id) &&
        row.status === 'draft' &&
        row.messageId === null &&
        row.sessionId === null
      ) {
        stmt(
          `UPDATE attachments SET status = 'committed', message_id = ?, session_id = ?, owner_id = ?
           WHERE id = ? AND scope = 'session' AND owner_id = ? AND status = 'draft'
             AND message_id IS NULL AND session_id IS NULL`
        ).run(messageId, sessionId, sessionId, row.id, sessionId)
        usedIds.add(row.id)
        return
      }

      const baseId = `${messageId}:managed:${String(index)}`
      let referenceId = baseId
      let suffix = 0
      while (true) {
        const existing = getAttachmentRow(referenceId)
        if (existing === undefined) break
        if (
          existing.scope === 'session' &&
          existing.ownerId === sessionId &&
          existing.messageId === messageId &&
          existing.path === row.path
        ) {
          usedIds.add(referenceId)
          return
        }
        suffix++
        referenceId = `${baseId}:${row.id}:${String(suffix)}`
      }
      stmt(
        `INSERT INTO attachments
           (id, session_id, message_id, path, size, checksum, scope, status, owner_id, display_name, created_at)
         VALUES (?, ?, ?, ?, ?, ?, 'session', 'committed', ?, ?, ?)`
      ).run(
        referenceId,
        sessionId,
        messageId,
        row.path,
        row.size,
        row.checksum,
        sessionId,
        row.displayName,
        row.createdAt
      )
      usedIds.add(referenceId)
    })
  })
}

/** 同上,但按磁盘路径定位。测试与迁移用 */
export function commitAttachmentsByPath(
  paths: readonly string[],
  messageId: string,
  sessionId: string
): void {
  if (paths.length === 0) return
  tx(() => {
    const rows = stmt(
      `SELECT * FROM attachments
         WHERE path IN (${paths.map(() => '?').join(',')})
           AND scope = 'session' AND owner_id = ?
         ORDER BY created_at, id`
    ).all(...paths, sessionId).map(toAttachmentRow)
    const currentRows = attachmentRowsForMessage(messageId)
    const usedIds = new Set<string>()
    rows.forEach((row, index) => {
      const current = currentRows.find(
        (candidate) => !usedIds.has(candidate.id) && candidate.path === row.path
      )
      if (current !== undefined) {
        usedIds.add(current.id)
        return
      }
      if (
        row.status === 'draft' &&
        row.messageId === null &&
        row.sessionId === null
      ) {
        stmt(
          `UPDATE attachments SET status = 'committed', message_id = ?, session_id = ?, owner_id = ?
           WHERE id = ? AND scope = 'session' AND owner_id = ? AND status = 'draft'
             AND message_id IS NULL AND session_id IS NULL`
        ).run(messageId, sessionId, sessionId, row.id, sessionId)
        usedIds.add(row.id)
        return
      }
      // A committed row belongs to another message. Reuse the same reference
      // id convention as commitAttachmentsByIds so repeated migrations remain
      // idempotent and the physical file gets reference-counted by path.
      const baseId = `${messageId}:managed:${String(index)}`
      let referenceId = baseId
      let suffix = 0
      while (true) {
        const existing = getAttachmentRow(referenceId)
        if (existing === undefined) break
        if (
          existing.scope === 'session' &&
          existing.ownerId === sessionId &&
          existing.messageId === messageId &&
          existing.path === row.path
        ) {
          usedIds.add(referenceId)
          return
        }
        suffix++
        referenceId = `${baseId}:${row.id}:${String(suffix)}`
      }
      stmt(
        `INSERT INTO attachments
           (id, session_id, message_id, path, size, checksum, scope, status, owner_id, display_name, created_at)
         VALUES (?, ?, ?, ?, ?, ?, 'session', 'committed', ?, ?, ?)`
      ).run(
        referenceId,
        sessionId,
        messageId,
        row.path,
        row.size,
        row.checksum,
        sessionId,
        row.displayName,
        row.createdAt
      )
      usedIds.add(referenceId)
    })
  })
}

export function searchAll(q: string, workspaceId?: string, limit = 50): SearchHit[] {
  const trimmed = q.trim()
  if (trimmed === '') return []
  // 指定了工作区却看不见它,那就是空结果 —— 和 `listSessions` 同一个判据。
  if (workspaceId !== undefined && !workspaceScopeVisible(workspaceId)) return []
  const safeLimit = Math.max(1, Math.min(200, Math.floor(limit)))
  // 以短语查询避免用户输入的 MATCH 运算符破坏 FTS 语法。
  // 四条 SQL 都带 `s.parent_session_id IS NULL`:子代理转录不是用户的对话,
  // 搜出来只会给出一条点不开、也没有上下文的命中。FTS 与 LIKE 兜底都要,
  // 漏掉兜底那条的话,这个泄漏会在旧库/极端 Unicode 上悄悄复活。
  const phrase = `"${trimmed.replaceAll('"', '""')}"`
  try {
    const rows = workspaceId === undefined
      ? stmt(`SELECT f.session_id, f.message_id, s.workspace_id, s.title, snippet(messages_fts, 3, '<mark>', '</mark>', '…', 18) AS snippet, m.created_at
              FROM messages_fts f JOIN sessions s ON s.id = f.session_id JOIN messages m ON m.id = f.message_id
              WHERE s.parent_session_id IS NULL AND messages_fts MATCH ? ORDER BY m.created_at DESC LIMIT ?`).all(phrase, safeLimit)
      : stmt(`SELECT f.session_id, f.message_id, s.workspace_id, s.title, snippet(messages_fts, 3, '<mark>', '</mark>', '…', 18) AS snippet, m.created_at
              FROM messages_fts f JOIN sessions s ON s.id = f.session_id JOIN messages m ON m.id = f.message_id
              WHERE s.workspace_id = ? AND s.parent_session_id IS NULL AND messages_fts MATCH ? ORDER BY m.created_at DESC LIMIT ?`).all(workspaceId, phrase, safeLimit)
    return rows.map((row) => {
      const r = row as Record<string, unknown>
      return { sessionId: String(r['session_id']), workspaceId: String(r['workspace_id']), messageId: String(r['message_id']), title: String(r['title']), snippet: String(r['snippet'] ?? ''), createdAt: Number(r['created_at']) }
    }).filter(visibleHit)
  } catch {
    // FTS 对极端 Unicode/旧数据库失败时，退回 LIKE，搜索仍可用。
    const like = `%${trimmed}%`
    const rows = workspaceId === undefined
      ? stmt(`SELECT m.session_id, m.id AS message_id, s.workspace_id, s.title, substr(m.parts, 1, 240) AS snippet, m.created_at
              FROM messages m JOIN sessions s ON s.id = m.session_id WHERE s.parent_session_id IS NULL AND m.parts LIKE ? ORDER BY m.created_at DESC LIMIT ?`).all(like, safeLimit)
      : stmt(`SELECT m.session_id, m.id AS message_id, s.workspace_id, s.title, substr(m.parts, 1, 240) AS snippet, m.created_at
              FROM messages m JOIN sessions s ON s.id = m.session_id WHERE s.workspace_id = ? AND s.parent_session_id IS NULL AND m.parts LIKE ? ORDER BY m.created_at DESC LIMIT ?`).all(workspaceId, like, safeLimit)
    return rows.map((row) => { const r = row as Record<string, unknown>; return { sessionId: String(r['session_id']), workspaceId: String(r['workspace_id']), messageId: String(r['message_id']), title: String(r['title']), snippet: String(r['snippet'] ?? ''), createdAt: Number(r['created_at']) } }).filter(visibleHit)
  }
}

/**
 * 全局搜索(没有指定工作区)的那条路径也要按作用域收窄。
 *
 * ★ 收在**这里**而不是每个调用点:`searchAll` 的那条 SQL 一条都没有按归属过滤,
 * 靠调用方自己筛的话,漏掉任何一处就是「在 A 账户里搜到了 B 账户的对话内容」——
 * 那是这个功能里泄露代价最高的一条。行数本来就被 `LIMIT` 压着,多一次
 * 主键查(工作区归属)不构成负担。
 */
const visibleHit = (hit: SearchHit): boolean => workspaceScopeVisible(hit.workspaceId)

/** 为上下文窗口提供当前会话范围的历史检索，复用 messages_fts，避免跨任务串入。 */
export function searchSessionHistory(sessionId: string, q: string, limit = 5): ContextSearchHit[] {
  const trimmed = q.trim()
  if (trimmed === '') return []
  // 非本作用域的会话一律 0 条:上下文检索是给正在跑的 run 喂资料的路径,
  // 它比界面上的搜索更不该跨账户。
  if (!sessionScopeVisible(sessionId)) return []
  const safeLimit = Math.max(1, Math.min(10, Math.floor(limit)))
  const phrase = `"${trimmed.replaceAll('"', '""')}"`
  try {
    const rows = stmt(`SELECT f.message_id, m.role, m.created_at,
              snippet(messages_fts, 3, '', '', '…', 10) AS snippet
              FROM messages_fts f JOIN messages m ON m.id = f.message_id
              WHERE f.session_id = ? AND messages_fts MATCH ?
              ORDER BY m.created_at DESC LIMIT ?`).all(sessionId, phrase, safeLimit)
    return rows.map((row) => {
      const r = row as Record<string, unknown>
      return {
        messageId: String(r['message_id']),
        role: String(r['role']) as ContextSearchHit['role'],
        createdAt: Number(r['created_at']),
        snippet: String(r['snippet'] ?? '').slice(0, 600)
      }
    })
  } catch {
    const like = `%${trimmed}%`
    const rows = stmt(`SELECT id AS message_id, role, created_at, substr(parts, 1, 600) AS snippet
              FROM messages WHERE session_id = ? AND parts LIKE ?
              ORDER BY created_at DESC LIMIT ?`).all(sessionId, like, safeLimit)
    return rows.map((row) => {
      const r = row as Record<string, unknown>
      return {
        messageId: String(r['message_id']),
        role: String(r['role']) as ContextSearchHit['role'],
        createdAt: Number(r['created_at']),
        snippet: String(r['snippet'] ?? '').slice(0, 600)
      }
    })
  }
}

function messageBytes(): { count: number; bytes: number } {
  /*
    ★ 在 SQL 里求和,不把每一行的 `parts` 拉进 JS 再 `Buffer.byteLength`。
    `cleanupPreview('history')` 和 `storage:getStats` 都走这里,而 `parts` 是
    整条消息的 JSON —— 有历史的库把这个字符串搬到 V8 堆上,光这一次就要多占
    与库同量级的内存,换来一个本来一行 SQL 就能给的数。

    `LENGTH(CAST(parts AS BLOB))` 而不是 `LENGTH(parts)`:后者对 TEXT 数的是
    字符数,中文消息会被算成 UTF-8 字节数的三分之一左右。
  */
  const row = stmt('SELECT COUNT(*) AS n, COALESCE(SUM(LENGTH(CAST(parts AS BLOB))), 0) AS bytes FROM messages').get() as Record<string, unknown>
  return { count: Number(row['n'] ?? 0), bytes: Number(row['bytes'] ?? 0) }
}

export function deleteAllHistory(): CleanupResult {
  return tx(() => {
    const p = cleanupPreview('history')
    clearMessageFts(stmt)
    stmt('DELETE FROM sessions').run()
    // committed 行随 sessions 级联；没有 session_id 的会话草稿按 scope
    // 显式删除。主题/导出附件不属于对话历史，必须保留。
    stmt("DELETE FROM attachments WHERE scope = 'session'").run()
    stmt('DELETE FROM runs').run()
    // 改动快照随 sessions 级联,但沿本函数「关系表逐张显式清」的纪律一并删,
    // 顺序按 FK 依赖:先叶(file_snapshots)后根(file_change_sets)。
    stmt('DELETE FROM file_snapshots').run()
    stmt('DELETE FROM file_change_sets').run()
    // Drafts and queued inputs are stored in kv because they can exist before
    // a session row is created. Clearing history must remove those otherwise
    // unreachable rows as well; deleting only the relational tables leaves
    // stale text that can reappear when an id is reused.
    stmt("DELETE FROM kv WHERE key LIKE 'session.input.%'").run()
    stmt("DELETE FROM kv WHERE key LIKE 'session.widget.%'").run()
    /*
      ★ 「清空对话历史」同样要留墓碑,而且必须整表一次改完 —— 这条路径**不**
      走 `deleteSession`(它是一次性 DELETE,没有逐条循环),所以上面那处墓碑
      在这里帮不上忙。漏掉的表现:用户清空历史,三十秒后自动同步把几百条
      对话原样搬回来。
    */
    stmt(
      `UPDATE import_mappings SET sync_state = 'suppressed', target_id = '', updated_at = ?
        WHERE entity_kind = 'session'`
    ).run(Date.now())
    return { ...p, deleted: p.sessionCount + p.messageCount + p.attachmentCount }
  })
}

/** 测试隔离：只清会话相关表，不触碰设置、供应商和工作区。 */
export function clearSessionDataForTest(): void {
  tx(() => {
    clearMessageFts(stmt)
    stmt('DELETE FROM runs').run()
    stmt('DELETE FROM file_snapshots').run()
    stmt('DELETE FROM file_change_sets').run()
    stmt('DELETE FROM attachments').run()
    stmt('DELETE FROM messages').run()
    stmt('DELETE FROM sessions').run()
    stmt("DELETE FROM kv WHERE key LIKE 'session.input.%'").run()
    stmt("DELETE FROM kv WHERE key LIKE 'session.widget.%'").run()
  })
}

export function cleanupPreview(kind: 'attachments' | 'age' | 'history' | 'local-data', cutoff?: number): CleanupPreview {
  if (kind === 'attachments') {
    const rows = stmt(`SELECT id, path, size FROM attachments WHERE message_id IS NULL OR session_id IS NULL`).all()
    return { kind, sessionCount: 0, messageCount: 0, attachmentCount: rows.length, bytes: rows.reduce((n, r) => n + Number((r as Record<string, unknown>)['size'] ?? 0), 0), undeletable: [] }
  }
  if (kind === 'age') {
    /*
      ★ 两个集合,不是一个:

      `sessionCount` 数**顶层** —— 确认框里那句话是「将删除 N 条对话」,
      而子代理转录不是一条对话,算进去就是虚报。
      `messageCount` / `bytes` 数**整棵子树** —— 这些是真正会消失的字节。
      漏掉子会话的消息,预览数字就和 `deleteByAge` 实际删掉的量对不上,
      而那个差额没有任何地方会解释。
    */
    const topIds = sessionIdsBefore(cutoff ?? 0)
    const ids = topIds.flatMap((id) => sessionSubtreeIds(id))
    // attachmentRowsForSessions 内部也会展开子树;传顶层即可,而且必须和
    // storage.ts 的 withActualAttachmentBytes 走同一个函数(见那边的注释)。
    const attachmentRows = attachmentRowsForSessions(topIds)
    let messageCount = 0
    let messageBytes = 0
    for (const id of ids) {
      for (const row of stmt('SELECT parts FROM messages WHERE session_id = ?').all(id)) {
        messageCount += 1
        messageBytes += Buffer.byteLength(String((row as Record<string, unknown>)['parts'] ?? ''), 'utf8')
      }
    }
    return { kind, sessionCount: topIds.length, messageCount, attachmentCount: attachmentRows.length, bytes: messageBytes + attachmentRows.reduce((n, row) => n + row.size, 0), undeletable: [] }
  }
  // 只有 sessionCount 过滤:清历史确实会连子转录一起删掉,所以字节数照全量算。
  const c = stmt(`SELECT COUNT(*) AS n FROM sessions WHERE parent_session_id IS NULL`).get() as Record<string, unknown>
  const m = messageBytes()
  const attachments = kind === 'history' ? allSessionAttachmentRows() : attachmentRows()
  return { kind, sessionCount: Number(c.n ?? 0), messageCount: m.count, attachmentCount: attachments.length, bytes: m.bytes + attachments.reduce((n, row) => n + row.size, 0), undeletable: [] }
}

export function deleteByAge(cutoff: number): CleanupResult {
  return tx(() => {
    const p = cleanupPreview('age', cutoff)
    const ids = sessionIdsBefore(cutoff)
    for (const id of ids) deleteSession(id)
    return { ...p, deleted: p.sessionCount + p.messageCount + p.attachmentCount }
  })
}

export function storageStats(dataDirectory: string, attachmentDirectory: string, lastBackupAt: number | null): import('../../shared/domain/settings').StorageStats {
  const files = fileStats()
  const c = messageBytes()
  // 「本机有几条对话」数的是顶层;子代理转录占的字节仍然照实计入下面的 messageBytes()。
  const s = stmt('SELECT COUNT(*) AS n FROM sessions WHERE parent_session_id IS NULL').get() as Record<string, unknown>
  const a = stmt('SELECT COUNT(*) AS n, COALESCE(SUM(size),0) AS bytes FROM attachments').get() as Record<string, unknown>
  let attachmentBytes = 0
  try {
    // 统计的是附件目录的实际占用，而不是 attachments 表里记录的大小：
    // 表中可能有外部绝对路径或已经丢失的文件，不能把它们冒充本机目录空间。
    // 先 lstat，再递归；符号链接按链接自身的大小计入，绝不跟随到外部目录。
    const walk = (dir: string): number => {
      let dirStat
      try { dirStat = lstatSync(dir) } catch { return 0 }
      if (!dirStat.isDirectory() || dirStat.isSymbolicLink()) return dirStat.size
      let total = 0
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const p = join(dir, entry.name)
        try {
          const st = lstatSync(p)
          total += st.isDirectory() && !st.isSymbolicLink() ? walk(p) : st.size
        } catch { /* 文件在扫描期间消失 */ }
      }
      return total
    }
    attachmentBytes = walk(attachmentDirectory)
  } catch { /* 目录不存在或不可读 */ }
  return {
    dbBytes: files.dbBytes,
    walBytes: files.walBytes,
    conversationBytes: Number(c.bytes ?? 0),
    attachmentBytes,
    conversationCount: Number(s.n ?? 0),
    messageCount: Number(c.count ?? 0),
    attachmentCount: Number(a.n ?? 0),
    dataDirectory,
    lastBackupAt
  }
}

/**
 * `getStats` 的**异步**版本,供 UI 的 `storage:getStats` IPC 用。
 *
 * ★ 和同步版的区别只有一件:附件目录的字节数走异步、有界的遍历
 *   (`lstat`/`readdir` promise 版,不跟符号链接),而不是把整棵目录同步读
 *   一遍。目录大的时候前者能让出事件循环,后者会在主进程上卡住。
 *
 * 数字口径与 `getStats` 完全一致:库/WAL 用 `fileStats`,会话/消息/字节直接
 * 查库(见 `messageBytes`)。**不做缓存、不用 0 顶替** —— 「暂时读不出来」和
 * 「真的是 0」在界面上是两件事,拿旧值或 0 冒充实时是另一种误导。
 */
export function getStatsAsync(
  dataDirectory: string,
  attachmentDirectory: string,
  lastBackupAt: number | null
): Promise<import('../../shared/domain/settings').StorageStats> {
  const files = fileStats()
  const c = messageBytes()
  const s = stmt('SELECT COUNT(*) AS n FROM sessions WHERE parent_session_id IS NULL').get() as Record<string, unknown>
  const a = stmt('SELECT COUNT(*) AS n, COALESCE(SUM(size),0) AS bytes FROM attachments').get() as Record<string, unknown>
  return collectDirectoryBytesBounded(attachmentDirectory, ATTACHMENT_SCAN_MAX_ENTRIES).then((attachmentBytes) => ({
    dbBytes: files.dbBytes,
    walBytes: files.walBytes,
    conversationBytes: Number(c.bytes ?? 0),
    attachmentBytes,
    conversationCount: Number(s.n ?? 0),
    messageCount: Number(c.count ?? 0),
    attachmentCount: Number(a.n ?? 0),
    dataDirectory,
    lastBackupAt
  }))
}

/** 一次统计最多看多少个目录条目 —— 有界,不把大目录读成全盘扫描。 */
const ATTACHMENT_SCAN_MAX_ENTRIES = 200_000

class AttachmentScanBudgetError extends Error {
  constructor() { super('附件目录条目超过统计预算，结果不完整，请稍后重试'); this.name = 'AttachmentScanBudgetError' }
}

/**
 * 异步、有界、不跟符号链接地数一棵树的字节数。
 *
 * 符号链接(以及任何非目录条目)只按它自己的 `lstat().size` 计,绝不递归进
 * 链接目标 —— 手动放进数据目录的一个链接不该让统计去读一个外部目录。
 */
async function collectDirectoryBytesBounded(root: string, budget: number): Promise<number> {
  const { lstat, opendir } = await import('node:fs/promises')
  let scanned = 0
  const walk = async (dir: string): Promise<number> => {
    let entryStat
    try { entryStat = await lstat(dir) } catch { return 0 }
    if (entryStat.isSymbolicLink() || !entryStat.isDirectory()) return entryStat.size
    let total = 0
    let entries
    try { entries = await opendir(dir) } catch { return total }
    // 增量读取，不把整个大目录的名字先分配进主线程堆；退出迭代时自动关闭目录句柄。
    for await (const entry of entries) {
      if (scanned >= budget) throw new AttachmentScanBudgetError()
      scanned += 1
      const child = join(dir, entry.name)
      try {
        const stat = await lstat(child)
        if (stat.isDirectory() && !stat.isSymbolicLink()) total += await walk(child)
        else total += stat.size
      } catch (error) {
        if (error instanceof AttachmentScanBudgetError) throw error
        // 扫描期间消失的条目不计入，预算到期不能冒充精确结果或零字节。
      }
    }
    return total
  }
  return walk(root)
}

// ── 上游供应商 / 模型别名 ────────────────────────────────────────────────────

/**
 * ★★ 用户自己添加的供应商 / 别名 / 密钥放在 `providers.json`(见 `provider-file.ts`),
 * 只有托管的 `nextcowork` 仍在库里。**只有显式装上文件存储才切换**(`useProviderFileStore`,
 * 由 `main/index.ts` 在开库之后做):没装(单元测试、旧调用方)时下面每个函数仍是
 * 原来的纯库实现,行为一个字不变。
 *
 * 文件模式下库里残留的非托管行(旧版本留下的备份、从旧归档恢复出来的)一律**不读**,
 * 否则同一个 id 会同时有两个来源。`model_aliases` 对 `providers` 有外键,所以文件里供应商的
 * 别名也不可能放进库 —— 它们跟着供应商条目走。
 */
let providerFile: ProviderFileStore | null = null

export function useProviderFileStore(store: ProviderFileStore | null): void {
  providerFile = store
}

export function providerFileStore(): ProviderFileStore | null {
  return providerFile
}

/** 文件模式下,这个 provider id 是否归文件管(托管供应商永远归库)。 */
function fileOwns(providerId: string): boolean {
  return providerFile !== null && providerId !== MANAGED_PROVIDER_ID
}

/**
 * 逻辑凭证 ref(`provider:<id>`)对应的、由文件管理的供应商 id;不归文件管就是 null。
 * `host.secrets` 靠它决定一把密钥走文件(明文)还是走库(NCK1 密文)。
 */
export function fileCredentialProviderId(ref: string): string | null {
  if (providerFile === null || !ref.startsWith('provider:')) return null
  const id = ref.slice('provider:'.length)
  /*
    ★★ 账号密文 `provider:<id>#<accountId>` 共用 `provider:` 前缀,但**不归文件管**,
    一律走库(NCK1)。providers.json 一家只有一个凭证槽,放行的表现是:写入落进
    `pendingCredentials` 再也不落盘,读回来恒为 null —— 设置页旧槽显示「已登录」,
    账号行没有邮箱(「未命名账号」),一发消息就报「还没有配置密钥」。
  */
  if (id.includes('#')) return null
  return id !== '' && id !== MANAGED_PROVIDER_ID ? id : null
}

/**
 * 迁移专用:库里**原始**的非托管供应商 / 别名行,绕开文件模式的过滤。
 * 业务代码不要用它 —— 文件模式下这些行不是权威来源。
 */
export function listLegacyDbProviders(): UpstreamProvider[] {
  return stmt('SELECT json FROM providers ORDER BY priority, id')
    .all()
    .map((r) => normalizeUpstreamProvider(parse<UpstreamProvider>(r['json'])))
    .filter((p) => p.id !== MANAGED_PROVIDER_ID)
}

export function listLegacyDbAliases(): ModelAlias[] {
  return stmt('SELECT json FROM model_aliases ORDER BY provider_id, alias')
    .all()
    .map((r) => parse<ModelAlias>(r['json']))
    .filter((a) => a.providerId !== MANAGED_PROVIDER_ID)
}

/**
 * 迁移成功之后清掉库里的旧行(别名由外键级联)。`credentialRefs` 只清**迁移成功**的那几把
 * (当前作用域),没能解密的密文原样留着,方便事后找回。
 */
export function purgeLegacyDbProviders(providerIds: readonly string[], credentialRefs: readonly string[]): void {
  tx(() => {
    for (const id of providerIds) {
      if (id !== MANAGED_PROVIDER_ID) stmt('DELETE FROM providers WHERE id = ?').run(id)
    }
    for (const ref of credentialRefs) stmt('DELETE FROM credentials WHERE ref = ?').run(physicalCredentialRef(ref))
  })
}

/**
 * 把整个库一致地拷到另一个文件(`VACUUM INTO`,WAL 下也是完整快照)。
 * 迁移会删旧行,这是回滚用的那份。目标已存在会抛错 —— 调用方先判断。
 */
export function backupDatabaseTo(path: string): void {
  stmt('VACUUM INTO ?').run(path)
}

export function listProviders(): UpstreamProvider[] {
  // priority 相同时用 id 兜底,保证顺序是确定的 —— 故障切换按这个顺序挑候选,
  // 「今天先切到 A、明天先切到 B」比切错还难查
  const rows = stmt('SELECT json FROM providers ORDER BY priority, id')
    .all()
    .map((r) => normalizeUpstreamProvider(parse<UpstreamProvider>(r['json'])))
  if (providerFile === null) return rows
  return [...rows.filter((p) => p.id === MANAGED_PROVIDER_ID), ...providerFile.listProviders()]
    .sort((a, b) => a.priority - b.priority || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
}

export function putProvider(p: UpstreamProvider): UpstreamProvider {
  const normalized = normalizeUpstreamProvider(p)
  if (fileOwns(normalized.id)) {
    const saved = providerFile!.putProvider(normalized)
    const { credentialRef: _credentialRef, ...cloud } = normalized
    enqueueSyncMutation('provider', normalized.id, cloud)
    return saved
  }
  tx(() => { stmt(
    `INSERT INTO providers (id, priority, json) VALUES (?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET priority = excluded.priority, json = excluded.json`
  ).run(normalized.id, normalized.priority, JSON.stringify(normalized))
    if (normalized.id !== 'nextcowork') {
      const { credentialRef: _credentialRef, ...cloud } = normalized
      enqueueSyncMutation('provider', normalized.id, cloud)
    }
  })
  return normalized
}

/**
 * 别名**不用**在这里手动删。`model_aliases.provider_id` 上的
 * `ON DELETE CASCADE` 接着(见 `schema.ts`),前提是连接开了 `foreign_keys`
 * —— `index.ts` 的 `openAt()` 用构造参数打开的。文件里的供应商,别名本来就在它的条目里。
 */
export function removeProvider(id: string): void {
  if (fileOwns(id)) {
    providerFile!.removeProvider(id)
    enqueueSyncMutation('provider', id, { id }, 'delete')
    return
  }
  tx(() => { stmt('DELETE FROM providers WHERE id = ?').run(id); if (id !== 'nextcowork') enqueueSyncMutation('provider', id, { id }, 'delete') })
}

export function listAliases(): ModelAlias[] {
  const rows = stmt('SELECT json FROM model_aliases ORDER BY provider_id, alias')
    .all()
    .map((r) => parse<ModelAlias>(r['json']))
  if (providerFile === null) return rows
  return [...rows.filter((a) => a.providerId === MANAGED_PROVIDER_ID), ...providerFile.listAliases()]
    .sort((a, b) => (a.providerId < b.providerId ? -1 : a.providerId > b.providerId ? 1 : a.alias < b.alias ? -1 : a.alias > b.alias ? 1 : 0))
}

export function putAlias(a: ModelAlias): ModelAlias {
  if (fileOwns(a.providerId)) {
    providerFile!.putAlias(a)
    enqueueSyncMutation('modelAlias', `${a.providerId}/${a.alias}`, a)
    return a
  }
  tx(() => { stmt(
    `INSERT INTO model_aliases (provider_id, alias, json) VALUES (?, ?, ?)
     ON CONFLICT (provider_id, alias) DO UPDATE SET json = excluded.json`
  ).run(a.providerId, a.alias, JSON.stringify(a)); if (a.providerId !== 'nextcowork') enqueueSyncMutation('modelAlias', `${a.providerId}/${a.alias}`, a) })
  return a
}

export function removeAlias(providerId: string, alias: string): void {
  if (fileOwns(providerId)) {
    providerFile!.removeAlias(providerId, alias)
    enqueueSyncMutation('modelAlias', `${providerId}/${alias}`, { providerId, alias }, 'delete')
    return
  }
  tx(() => { stmt('DELETE FROM model_aliases WHERE provider_id = ? AND alias = ?').run(providerId, alias); if (providerId !== 'nextcowork') enqueueSyncMutation('modelAlias', `${providerId}/${alias}`, { providerId, alias }, 'delete') })
}

// ── scheduled tasks / runs ───────────────────────────────────────────────────

export function listScheduledTasks(workspaceId?: string): ScheduledTask[] {
  const rows = workspaceId === undefined
    ? stmt('SELECT json FROM scheduled_tasks ORDER BY enabled DESC, next_run_at IS NULL, next_run_at ASC, json').all()
    : stmt('SELECT json FROM scheduled_tasks WHERE workspace_id = ? ORDER BY enabled DESC, next_run_at IS NULL, next_run_at ASC, json').all(workspaceId)
  return rows.map((row) => parse<ScheduledTask>(row['json']))
}

export function getScheduledTask(id: string): ScheduledTask | undefined {
  const row = stmt('SELECT json FROM scheduled_tasks WHERE id = ?').get(id)
  return row === undefined ? undefined : parse<ScheduledTask>(row['json'])
}

export function putScheduledTask(task: ScheduledTask): ScheduledTask {
  stmt(`INSERT INTO scheduled_tasks (id, workspace_id, enabled, next_run_at, created_at, updated_at, json)
    VALUES (?, ?, ?, ?, ?, ?, ?)
    ON CONFLICT(id) DO UPDATE SET workspace_id=excluded.workspace_id, enabled=excluded.enabled,
      next_run_at=excluded.next_run_at, updated_at=excluded.updated_at, json=excluded.json`)
    .run(task.id, task.workspaceId, task.enabled ? 1 : 0, task.nextRunAt, task.createdAt, task.updatedAt, JSON.stringify(task))
  return task
}

export function deleteScheduledTask(id: string): void {
  stmt('DELETE FROM scheduled_tasks WHERE id = ?').run(id)
}

export function listScheduledRuns(taskId?: string, limit = 100): ScheduledRun[] {
  const rows = taskId === undefined
    ? stmt('SELECT * FROM scheduled_runs ORDER BY scheduled_at DESC, id DESC LIMIT ?').all(Math.max(1, limit))
    : stmt('SELECT * FROM scheduled_runs WHERE task_id = ? ORDER BY scheduled_at DESC, id DESC LIMIT ?').all(taskId, Math.max(1, limit))
  return rows.map(toScheduledRun)
}

function toScheduledRun(row: Record<string, unknown>): ScheduledRun {
  return {
    id: String(row['id']), taskId: String(row['task_id']), sessionId: String(row['session_id'] ?? ''),
    trigger: String(row['trigger']) as ScheduledRun['trigger'], status: String(row['status']) as ScheduledRun['status'],
    scheduledAt: Number(row['scheduled_at']),
    ...(row['started_at'] == null ? {} : { startedAt: Number(row['started_at']) }),
    ...(row['ended_at'] == null ? {} : { endedAt: Number(row['ended_at']) }),
    ...(row['summary'] == null ? {} : { summary: String(row['summary']) }),
    ...(row['error'] == null ? {} : { error: String(row['error']) })
  }
}

export function getScheduledRun(id: string): ScheduledRun | undefined {
  return listScheduledRuns(undefined, 10_000).find((run) => run.id === id)
}

/**
 * 某个会话属于哪一次定时执行。
 *
 * ★ 走 SQL 而不是照抄 `getScheduledRun` 那句全表 10000 行再 find:这条查询在
 * **每次 Agent 建定时任务时**都要跑一遍(算链深度),而那条路径上多扫一万行
 * 不会有任何可见症状,只会慢。`session_id` 唯一,取最新一条即可。
 */
export function getScheduledRunBySession(sessionId: string): ScheduledRun | undefined {
  if (sessionId === '') return undefined
  const row = stmt('SELECT * FROM scheduled_runs WHERE session_id = ? ORDER BY scheduled_at DESC, id DESC LIMIT 1').get(sessionId)
  return row === undefined ? undefined : toScheduledRun(row)
}

export function deleteScheduledRun(id: string): void {
  stmt('DELETE FROM scheduled_runs WHERE id = ?').run(id)
}

export function putScheduledRun(run: ScheduledRun): ScheduledRun {
  stmt(`INSERT INTO scheduled_runs (id, task_id, session_id, trigger, status, scheduled_at, started_at, ended_at, summary, error)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    ON CONFLICT(id) DO UPDATE SET task_id=excluded.task_id, session_id=excluded.session_id, trigger=excluded.trigger,
      status=excluded.status, scheduled_at=excluded.scheduled_at, started_at=excluded.started_at,
      ended_at=excluded.ended_at, summary=excluded.summary, error=excluded.error`)
    .run(run.id, run.taskId, run.sessionId, run.trigger, run.status, run.scheduledAt, run.startedAt ?? null, run.endedAt ?? null, run.summary ?? null, run.error ?? null)
  return run
}

// ── kv ──────────────────────────────────────────────────────────────────────

export function getKv<T>(key: string, fallback: T): T {
  const row = stmt('SELECT json FROM kv WHERE key = ?').get(key)
  if (row === undefined) return fallback
  // `?? fallback` 保住 Map 版的语义:存进去的 null 读出来也是兜底值,
  // 而不是一个会顺着 IPC 流到界面上的 null
  return parse<T>(row['json']) ?? fallback
}

export function setKv(key: string, value: unknown): void {
  // `JSON.stringify(undefined)` 返回的是 undefined 而不是字符串,会撞上 NOT NULL。
  // 归一成 null,读回来正好走上面那条兜底。
  stmt(
    'INSERT INTO kv (key, json) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET json = excluded.json'
  ).run(key, JSON.stringify(value ?? null))
}

export function removeKv(key: string): void {
  stmt('DELETE FROM kv WHERE key = ?').run(key)
}

// ── User model catalogue ───────────────────────────────────────────────────

/**
 * Only user-created definitions are persisted. Built-in definitions ship in
 * source control and must not be copied into SQLite: otherwise an application
 * upgrade could never add or correct a built-in row for an existing user.
 */
const USER_MODEL_CATALOG_KEY = 'model-catalog.custom'

export function listUserModelCatalog(): ModelCatalogDefinition[] {
  const raw = getKv<unknown>(USER_MODEL_CATALOG_KEY, [])
  if (!Array.isArray(raw)) return []
  return raw.filter(isModelCatalogDefinition)
}

export function putUserModelCatalog(model: ModelCatalogDefinition): ModelCatalogDefinition {
  if (!isModelCatalogDefinition(model)) throw new Error('自定义模型目录记录格式无效。')
  return tx(() => {
    const rows = listUserModelCatalog()
    const key = model.id.trim().toLowerCase()
    const index = rows.findIndex((row) => row.id.trim().toLowerCase() === key)
    if (index === -1) rows.push(model)
    else rows[index] = model
    setKv(USER_MODEL_CATALOG_KEY, rows)
    return model
  })
}

export function removeUserModelCatalog(id: string): void {
  tx(() => {
    const rows = listUserModelCatalog()
    const key = id.trim().toLowerCase()
    const next = rows.filter((row) => row.id.trim().toLowerCase() !== key)
    if (next.length === rows.length) return
    setKv(USER_MODEL_CATALOG_KEY, next)
  })
}

// ── synced pricing (model_pricing, is_seed = 0) ───────────────────────────

/**
 * 同步写进来的价目行。`is_seed = 0` 是它们和仓库种子的分界 —— 种子不入库
 * (升级才能纠正已有用户的价),这张表里**只有**用户确认过的同步价。
 */
export function listSyncedPricing(): ModelPricing[] {
  return stmt(
    `SELECT provider_id, model_id, display_name, currency, modality, tiers, windows,
            effective_from, effective_until, source, fetched_at
       FROM model_pricing WHERE is_seed = 0 ORDER BY model_id, COALESCE(effective_from, '')`
  )
    .all()
    .map((row) => ({
      providerId: asNullableString(row['provider_id']),
      modelId: String(row['model_id']),
      displayName: String(row['display_name']),
      currency: String(row['currency']) as ModelPricing['currency'],
      modality: String(row['modality']) as ModelPricing['modality'],
      tiers: parse<ModelPricing['tiers']>(row['tiers']) ?? [],
      ...(row['windows'] === null ? {} : { windows: parse<NonNullable<ModelPricing['windows']>>(row['windows']) ?? [] }),
      ...(row['effective_from'] === null ? {} : { effectiveFrom: String(row['effective_from']) }),
      ...(row['effective_until'] === null ? {} : { effectiveUntil: String(row['effective_until']) }),
      source: String(row['source']),
      fetchedAt: String(row['fetched_at'])
    }))
}

/** 同一 `(provider, model)` 的旧同步行整组替换,保证「采用同步价」之后不留旧区间 */
export function replaceSyncedPricing(rows: readonly ModelPricing[]): void {
  tx(() => {
    for (const r of rows) {
      stmt('DELETE FROM model_pricing WHERE is_seed = 0 AND model_id = ? AND COALESCE(provider_id, \'\') = ?').run(r.modelId, r.providerId ?? '')
      stmt(
        `INSERT INTO model_pricing
           (provider_id, model_id, display_name, currency, modality, tiers, windows, effective_from, effective_until, source, fetched_at, is_seed)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0)`
      ).run(
        r.providerId,
        r.modelId,
        r.displayName,
        r.currency,
        r.modality,
        JSON.stringify(r.tiers),
        r.windows === undefined ? null : JSON.stringify(r.windows),
        r.effectiveFrom ?? null,
        r.effectiveUntil ?? null,
        r.source,
        r.fetchedAt
      )
    }
  })
}

export function clearSyncedPricing(): void {
  stmt('DELETE FROM model_pricing WHERE is_seed = 0').run()
}

// ── usage records ──────────────────────────────────────────────────────────

const asNullableString = (value: unknown): string | null =>
  value === null || value === undefined ? null : String(value)

function usageRecordFromRow(row: Record<string, unknown>): UsageAttemptRecord {
  return {
    id: String(row['id']),
    at: Number(row['at']),
    runId: String(row['run_id']),
    workspaceId: String(row['workspace_id'] ?? ''),
    sessionId: String(row['session_id'] ?? ''),
    attempt: Number(row['attempt'] ?? 1),
    providerId: String(row['provider_id']),
    providerName: String(row['provider_name'] || row['provider_id']),
    protocol: String(row['protocol'] ?? 'unknown') as UsageAttemptRecord['protocol'],
    endpoint: String(row['endpoint'] ?? ''),
    alias: String(row['alias']),
    upstreamModel: String(row['upstream_model']),
    responseModel: asNullableString(row['response_model']),
    inputTokens: Number(row['input_tokens'] ?? 0),
    outputTokens: Number(row['output_tokens'] ?? 0),
    cacheReadTokens: Number(row['cache_read_tokens'] ?? 0),
    cacheWriteTokens: Number(row['cache_write_tokens'] ?? 0),
    cacheWrite1hTokens: Number(row['cache_write_1h_tokens'] ?? 0),
    thinkingTokens:
      row['thinking_tokens'] === null || row['thinking_tokens'] === undefined
        ? null
        : Number(row['thinking_tokens']),
    thinkingTokensEstimated: Number(row['thinking_tokens_estimated'] ?? 0) !== 0,
    latencyMs: Number(row['latency_ms']),
    timeToFirstTokenMs:
      row['time_to_first_token_ms'] === null || row['time_to_first_token_ms'] === undefined
        ? null
        : Number(row['time_to_first_token_ms']),
    ok: Number(row['ok']) !== 0,
    httpStatus:
      row['http_status'] === null || row['http_status'] === undefined
        ? null
        : Number(row['http_status']),
    errorKind: asNullableString(row['error_kind']) as UsageAttemptRecord['errorKind'],
    errorMessage: asNullableString(row['error_message']),
    stopReason: asNullableString(row['stop_reason']) as UsageAttemptRecord['stopReason'],
    costMicros:
      row['cost_micros'] === null || row['cost_micros'] === undefined
        ? null
        : Number(row['cost_micros']),
    currency: asNullableString(row['currency']) as UsageAttemptRecord['currency'],
    pricingTier:
      row['pricing_tier'] === null || row['pricing_tier'] === undefined
        ? null
        : Number(row['pricing_tier']),
    pricingWindow: asNullableString(row['pricing_window']),
    toolCalls: Number(row['tool_calls'] ?? 0),
    toolErrors: Number(row['tool_errors'] ?? 0)
  }
}

/**
 * The initial write boundary for one upstream-attempt ledger row. Token,
 * routing, latency, and pricing fields are frozen here. Tool execution happens
 * after the upstream stream closes, so only its two outcome counters are
 * finalized later by `updateUsageToolsForRun`.
 */
export function recordUsageAttempt(record: UsageAttemptRecord): void {
  stmt(
    `INSERT INTO usage_records
       (id, at, run_id, workspace_id, session_id, attempt,
        provider_id, provider_name, protocol, endpoint, alias, upstream_model, response_model,
        input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
        cache_write_1h_tokens, thinking_tokens, thinking_tokens_estimated,
        latency_ms, time_to_first_token_ms, ok, http_status, error_kind, error_message,
        stop_reason, cost_micros, currency, pricing_tier, pricing_window, tool_calls, tool_errors)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).run(
    record.id,
    record.at,
    record.runId,
    record.workspaceId,
    record.sessionId,
    record.attempt,
    record.providerId,
    record.providerName,
    record.protocol,
    record.endpoint,
    record.alias,
    record.upstreamModel,
    record.responseModel,
    record.inputTokens,
    record.outputTokens,
    record.cacheReadTokens,
    record.cacheWriteTokens,
    record.cacheWrite1hTokens,
    record.thinkingTokens,
    record.thinkingTokensEstimated ? 1 : 0,
    record.latencyMs,
    record.timeToFirstTokenMs,
    record.ok ? 1 : 0,
    record.httpStatus,
    record.errorKind,
    record.errorMessage,
    record.stopReason,
    record.costMicros,
    record.currency,
    record.pricingTier,
    record.pricingWindow,
    record.toolCalls,
    record.toolErrors
  )
}

/**
 * Attach post-response tool outcomes to the successful tool-use attempt that
 * produced them. Retries share a run id, while only the winning attempt can
 * reach tool execution; restricting by stop_reason keeps a later final text
 * turn from receiving counters that belong to an earlier tool-use turn.
 */
export function updateUsageToolsForRun(
  runId: string,
  toolCalls: number,
  toolErrors: number
): boolean {
  const result = stmt(
    `UPDATE usage_records
        SET tool_calls = ?, tool_errors = ?
      WHERE id = (
        SELECT id
          FROM usage_records
         WHERE run_id = ? AND ok = 1 AND stop_reason = 'tool_use'
         ORDER BY at DESC, id DESC
         LIMIT 1
      )`
  ).run(toolCalls, toolErrors, runId)
  return Number(result.changes) > 0
}

function usageWindowWhere(window: UsageWindow): { sql: string; params: number[] } {
  const clauses = ['at < ?']
  const params = [window.to]
  if (window.from !== undefined) {
    clauses.unshift('at >= ?')
    params.unshift(window.from)
  }
  return { sql: clauses.join(' AND '), params }
}

function usageCosts(window: UsageWindow): UsageCostTotal[] {
  const where = usageWindowWhere(window)
  return stmt(
    `SELECT currency, SUM(cost_micros) AS micros
       FROM usage_records
      WHERE ${where.sql} AND cost_micros IS NOT NULL AND currency IS NOT NULL
      GROUP BY currency
      ORDER BY currency`
  )
    .all(...where.params)
    .map((row) => ({
      currency: String(row['currency']) as UsageCostTotal['currency'],
      micros: Number(row['micros'] ?? 0)
    }))
}

export function getUsageSummary(window: UsageWindow): UsageSummary {
  const where = usageWindowWhere(window)
  const row = stmt(
    `SELECT
       COUNT(*) AS request_count,
       COALESCE(SUM(ok), 0) AS success_count,
       COALESCE(SUM(input_tokens), 0) AS input_tokens,
       COALESCE(SUM(output_tokens), 0) AS output_tokens,
       COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens,
       COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens,
       COALESCE(SUM(cache_write_1h_tokens), 0) AS cache_write_1h_tokens,
       COALESCE(SUM(thinking_tokens), 0) AS thinking_tokens,
       COALESCE(SUM(CASE WHEN thinking_tokens_estimated <> 0 THEN 1 ELSE 0 END), 0)
         AS estimated_thinking_request_count,
       AVG(latency_ms) AS average_latency_ms,
       AVG(time_to_first_token_ms) AS average_ttft_ms,
       COALESCE(SUM(tool_calls), 0) AS tool_calls,
       COALESCE(SUM(tool_errors), 0) AS tool_errors
     FROM usage_records
     WHERE ${where.sql}`
  ).get(...where.params) ?? {}

  const requestCount = Number(row['request_count'] ?? 0)
  const successCount = Number(row['success_count'] ?? 0)
  const inputTokens = Number(row['input_tokens'] ?? 0)
  const outputTokens = Number(row['output_tokens'] ?? 0)
  const cacheReadTokens = Number(row['cache_read_tokens'] ?? 0)
  const cacheWriteTokens = Number(row['cache_write_tokens'] ?? 0)
  const cacheDenominator = inputTokens + cacheReadTokens

  return {
    requestCount,
    successCount,
    failedCount: requestCount - successCount,
    inputTokens,
    outputTokens,
    cacheReadTokens,
    cacheWriteTokens,
    cacheWrite1hTokens: Number(row['cache_write_1h_tokens'] ?? 0),
    thinkingTokens: Number(row['thinking_tokens'] ?? 0),
    estimatedThinkingRequestCount: Number(row['estimated_thinking_request_count'] ?? 0),
    totalTokens: inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens,
    cacheHitRate: cacheDenominator === 0 ? null : cacheReadTokens / cacheDenominator,
    averageLatencyMs:
      row['average_latency_ms'] === null || row['average_latency_ms'] === undefined
        ? null
        : Number(row['average_latency_ms']),
    averageTimeToFirstTokenMs:
      row['average_ttft_ms'] === null || row['average_ttft_ms'] === undefined
        ? null
        : Number(row['average_ttft_ms']),
    toolCalls: Number(row['tool_calls'] ?? 0),
    toolErrors: Number(row['tool_errors'] ?? 0),
    costs: usageCosts(window)
  }
}

function usageLogWhere(query: UsageRequestLogsQuery): {
  sql: string
  params: Array<string | number>
} {
  const window = usageWindowWhere(query)
  const clauses = [window.sql]
  const params: Array<string | number> = [...window.params]
  const search = query.query?.trim()
  if (search) {
    clauses.push(
      `instr(lower(provider_name || ' ' || provider_id || ' ' || alias || ' ' || upstream_model || ' ' || run_id), lower(?)) > 0`
    )
    params.push(search)
  }
  if (query.status === 'success') clauses.push('ok = 1')
  else if (query.status === 'failed') clauses.push('ok = 0')
  return { sql: clauses.join(' AND '), params }
}

/** 需求：费用浮层逐模型核算整条会话；不分页，否则长会话会漏算旧请求。 */
export function getSessionUsageAttempts(sessionId: string): UsageAttemptRecord[] {
  return stmt('SELECT * FROM usage_records WHERE session_id = ? ORDER BY at, id')
    .all(sessionId)
    .map(usageRecordFromRow)
}

export function getUsageRequestLogs(query: UsageRequestLogsQuery): UsageRequestLogsPage {
  const limit = Math.max(1, Math.min(200, Math.trunc(query.limit ?? 50)))
  const offset = Math.max(0, Math.trunc(query.offset ?? 0))
  const where = usageLogWhere(query)
  const total = Number(
    stmt(`SELECT COUNT(*) AS count FROM usage_records WHERE ${where.sql}`)
      .get(...where.params)?.['count'] ?? 0
  )
  const items = stmt(
    `SELECT * FROM usage_records
      WHERE ${where.sql}
      ORDER BY at DESC, id DESC
      LIMIT ? OFFSET ?`
  )
    .all(...where.params, limit, offset)
    .map(usageRecordFromRow)
  return { items, total, offset, limit }
}

type UsageDimension = 'provider' | 'model'

function getUsageDimensionStats(
  window: UsageWindow,
  dimension: UsageDimension
): UsageDimensionStat[] {
  const where = usageWindowWhere(window)
  const idColumn = dimension === 'provider' ? 'provider_id' : 'upstream_model'
  const labelExpression =
    dimension === 'provider'
      ? `COALESCE(NULLIF(MAX(provider_name), ''), provider_id)`
      : 'upstream_model'
  const rows = stmt(
    `SELECT
       ${idColumn} AS dimension_id,
       ${labelExpression} AS dimension_label,
       COUNT(*) AS request_count,
       COALESCE(SUM(ok), 0) AS success_count,
       COALESCE(SUM(input_tokens), 0) AS input_tokens,
       COALESCE(SUM(output_tokens), 0) AS output_tokens,
       COALESCE(SUM(cache_read_tokens), 0) AS cache_read_tokens,
       COALESCE(SUM(cache_write_tokens), 0) AS cache_write_tokens,
       COALESCE(SUM(thinking_tokens), 0) AS thinking_tokens,
       AVG(latency_ms) AS average_latency_ms,
       AVG(time_to_first_token_ms) AS average_ttft_ms,
       COALESCE(SUM(tool_calls), 0) AS tool_calls,
       COALESCE(SUM(tool_errors), 0) AS tool_errors
     FROM usage_records
     WHERE ${where.sql}
     GROUP BY ${idColumn}
     ORDER BY request_count DESC, dimension_label
     LIMIT 200`
  ).all(...where.params)

  const stats = new Map<string, UsageDimensionStat>()
  for (const row of rows) {
    const id = String(row['dimension_id'])
    stats.set(id, {
      id,
      label: String(row['dimension_label']),
      requestCount: Number(row['request_count'] ?? 0),
      successCount: Number(row['success_count'] ?? 0),
      inputTokens: Number(row['input_tokens'] ?? 0),
      outputTokens: Number(row['output_tokens'] ?? 0),
      cacheReadTokens: Number(row['cache_read_tokens'] ?? 0),
      cacheWriteTokens: Number(row['cache_write_tokens'] ?? 0),
      thinkingTokens: Number(row['thinking_tokens'] ?? 0),
      averageLatencyMs:
        row['average_latency_ms'] === null || row['average_latency_ms'] === undefined
          ? null
          : Number(row['average_latency_ms']),
      averageTimeToFirstTokenMs:
        row['average_ttft_ms'] === null || row['average_ttft_ms'] === undefined
          ? null
          : Number(row['average_ttft_ms']),
      toolCalls: Number(row['tool_calls'] ?? 0),
      toolErrors: Number(row['tool_errors'] ?? 0),
      costs: []
    })
  }

  const costRows = stmt(
    `SELECT ${idColumn} AS dimension_id, currency, SUM(cost_micros) AS micros
       FROM usage_records
      WHERE ${where.sql} AND cost_micros IS NOT NULL AND currency IS NOT NULL
      GROUP BY ${idColumn}, currency
      ORDER BY ${idColumn}, currency`
  ).all(...where.params)
  for (const row of costRows) {
    const target = stats.get(String(row['dimension_id']))
    if (target === undefined) continue
    target.costs.push({
      currency: String(row['currency']) as UsageCostTotal['currency'],
      micros: Number(row['micros'] ?? 0)
    })
  }
  return [...stats.values()]
}

export function getUsageProviderStats(window: UsageWindow): UsageDimensionStat[] {
  return getUsageDimensionStats(window, 'provider')
}

export function getUsageModelStats(window: UsageWindow): UsageDimensionStat[] {
  return getUsageDimensionStats(window, 'model')
}

// ── MCP 服务器 ──────────────────────────────────────────────────────────────

/**
 * 顺序按 id —— MCP 服务器的顺序**没有语义**(每台带来的工具都平铺进同一个
 * ToolRegistry,不存在「先试这台」),但列表页需要一个稳定顺序,
 * 否则每次读回来行序都可能不同,看起来像在自己跳。
 */
export function listMcpServers(): McpServerConfig[] {
  return stmt('SELECT json FROM mcp_servers ORDER BY id')
    .all()
    .map((r) => parse<McpServerConfig>(r['json']))
}

export function getMcpServer(id: string): McpServerConfig | undefined {
  const row = stmt('SELECT json FROM mcp_servers WHERE id = ?').get(id)
  return row === undefined ? undefined : parse<McpServerConfig>(row['json'])
}

export function putMcpServer(c: McpServerConfig): McpServerConfig {
  tx(() => {
    const previous = getMcpServer(c.id)
    stmt(
    `INSERT INTO mcp_servers (id, json) VALUES (?, ?)
     ON CONFLICT (id) DO UPDATE SET json = excluded.json`
  ).run(c.id, JSON.stringify(c))
    if (c.workspaceId === undefined) enqueueSyncMutation('mcpServer', c.id, c)
    else if (previous && previous.workspaceId === undefined) enqueueSyncMutation('mcpServer', c.id, { id: c.id }, 'delete')
  })
  return c
}

/**
 * ★ 配置和它的密钥**一起删,在一个事务里**。
 *
 * 只删配置的话,`credentials` 里那行密文会永远留着 —— 而且它是**孤儿**:
 * 键名存在刚被删掉的那条配置里,再没有任何东西知道该怎么清理它。
 * 更糟的是下次建一个同 id 的服务器会**默默继承**上一个的 token,
 * 症状是「我明明没填 Authorization,它却连上了」。
 *
 * 两个 kind 都删,而不是先读出配置再判断该删哪个:少一次读,
 * 而且配置读不出来(行已经不在了)时仍然清得干净。
 */
export function removeMcpServer(id: string): void {
  tx(() => {
    const previous = getMcpServer(id)
    stmt('DELETE FROM mcp_servers WHERE id = ?').run(id)
    // ★ 走 removeCredential(带作用域),不是裸 DELETE:同 id 的服务器在别的
    //   账户里可能也有一份,裸 DELETE 会把那一条的密钥一起清掉。
    removeCredential(mcpSecretRef(id, 'env'))
    removeCredential(mcpSecretRef(id, 'headers'))
    if (previous?.workspaceId === undefined) enqueueSyncMutation('mcpServer', id, { id }, 'delete')
  })
}

// ── 搜索服务 ────────────────────────────────────────────────────────────────

/**
 * ★ 返回的是**目录表里的八家全部**,不是库里存着的那几行。
 *
 * 库里只存「用户动过的那几家」——一家都没配过时表是空的。而设置页要列出八家
 * 供用户挑,`web_search` 也要知道完整的优先级序。让每个调用点各自去和
 * `defaultProviderConfigs()` 做一次左连接,是三份必然会分叉的实现;
 * 收在这里一次做完。
 *
 * 顺序:**按 `priority` 升序**,平局用目录顺序兜底。
 *
 * 平局是真会出现的:界面拖拽走 `reorderProviders`,它把八家整批重编号,
 * 所以正常路径下没有重复。但只写了其中两家(测试、导入、将来某条迁移)之后,
 * 没存过的那几家仍然带着目录序号当 priority,和新编号撞得上。
 * 撞了就用目录顺序兜底 —— 关键是**确定**:「今天先试 A、明天先试 B」
 * 比顺序不合心意难查得多。
 */
export function listSearchProviders(): SearchProviderConfig[] {
  const stored = new Map(
    stmt('SELECT json FROM search_providers')
      .all()
      .map((r) => {
        const c = parse<SearchProviderConfig>(r['json'])
        return [c.id, c] as const
      })
  )
  return defaultProviderConfigs()
    .map((d, catalogIndex) => ({ cfg: stored.get(d.id) ?? d, catalogIndex }))
    .sort((a, b) => a.cfg.priority - b.cfg.priority || a.catalogIndex - b.catalogIndex)
    .map((x) => x.cfg)
}

export function putSearchProvider(c: SearchProviderConfig): SearchProviderConfig {
  tx(() => { stmt(
    `INSERT INTO search_providers (id, priority, json) VALUES (?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET priority = excluded.priority, json = excluded.json`
  ).run(c.id, c.priority, JSON.stringify(c)); enqueueSyncMutation('searchProvider', c.id, c) })
  return c
}

/** Remove a user override; the built-in catalog remains available locally. */
export function removeSearchProvider(id: SearchProviderId): void {
  tx(() => {
    stmt('DELETE FROM search_providers WHERE id = ?').run(id)
    clearSearchCredential(id)
    enqueueSyncMutation('searchProvider', id, { id }, 'delete')
  })
}

/**
 * 拖拽排序的落点:**整批写,一个事务**。
 *
 * 逐行写的话,中途失败会留下一半新序一半旧序 —— 而 priority 不是描述性的,
 * 它决定调用顺序,两个 0 或者一个空档都会让「先试哪家」变得不确定。
 */
export function putSearchProviders(list: readonly SearchProviderConfig[]): void {
  tx(() => {
    for (const c of list) putSearchProvider(c)
  })
}

/** Key 也一起删,理由同 `removeMcpServer` —— 同 id 重配时不该继承上一次的 Key。 */
export function clearSearchCredential(id: SearchProviderId): void {
  removeCredential(searchSecretRef(id))
}

// ── credentials ─────────────────────────────────────────────────────────────

/**
 * ★ 存取的是**密文字节**,这里不认识明文也不该认识 ——
 * 加解密只在 `main/host/index.ts` 那两个函数之间发生(方案 §9)。
 *
 * 读回来的是 `Uint8Array`;NCK1 解密与旧 safeStorage 兼容都留给 host ——
 * 数据库层不 import electron 或 crypto。
 *
 * ★★ **这四个函数是账户隔离的唯一收口。** 逻辑 `ref` 在这里被换成物理键
 * (`config-profile.physicalCredentialRef`):`local` 沿用裸 ref(兼容这一列
 * 存在之前的所有行),账户作用域加 `<accountId>\u0000` 前缀,
 * 而平台账户的显式键(`nextcowork:client-*` / `config-sync:*`)在任何作用域下
 * 都是同一个物理键。
 *
 * 于是「A、B 两个账户给同一个 providerId 配了不同的 Key」在库里天然就是两行,
 * 任何调用点都不可能读串 —— 而这正是上一版把密钥放在 `providers.credentialRef`
 * 里指过去时做不到的事(那时的 ref 是**逻辑名**,换账户就指向同一行)。
 */
export function getCredential(ref: string): Uint8Array | undefined {
  const row = stmt('SELECT blob FROM credentials WHERE ref = ?').get(physicalCredentialRef(ref))
  if (row === undefined) return undefined
  const blob = row['blob']
  return blob instanceof Uint8Array ? blob : undefined
}

export function putCredential(ref: string, blob: Uint8Array): void {
  tx(() => {
    stmt(
      'INSERT INTO credentials (ref, blob) VALUES (?, ?) ON CONFLICT (ref) DO UPDATE SET blob = excluded.blob'
    ).run(physicalCredentialRef(ref), blob)
    // Provider credentials are part of the encrypted `providers` cloud snapshot.
    // Global platform/sync tokens use other prefixes and therefore never dirty it.
    if (ref.startsWith('provider:')) enqueueSyncMutation('provider', ref.slice('provider:'.length), {})
  })
}

/**
 * 只删**当前作用域**那一行(见 `getCredential` 的说明)。
 *
 * ★ 平台显式键(`nextcowork:client-*` / `config-sync:*`)照删不误 ——
 * 退出登录正是靠这一句清掉 access / refresh token。它们本来就不带前缀,
 * 所以「只删当前作用域」对它们而言恰好等于「删它们自己」。
 */
export function removeCredential(ref: string): void {
  const fileProviderId = fileCredentialProviderId(ref)
  if (fileProviderId !== null) {
    providerFile!.removeCredential(fileProviderId)
    enqueueSyncMutation('provider', fileProviderId, {}, 'delete')
    return
  }
  tx(() => {
    stmt('DELETE FROM credentials WHERE ref = ?').run(physicalCredentialRef(ref))
    if (ref.startsWith('provider:')) enqueueSyncMutation('provider', ref.slice('provider:'.length), {}, 'delete')
  })
}

// ── 导入映射与批次(schema.ts 第 16 条) ──────────────────────────────────────

/**
 * 这一节是「导入两次仍一份」的全部实现。
 *
 * ★ 刻意**不**复用 `mergeDataExport`:那个函数为 NextCoWork 自己的整库备份而写,
 * 会合并全套设置并按时间戳覆盖。外部来源要的是逐项、可重试、可脱离的语义,
 * 两者只是名字里都有「导入」。
 */

export type ImportEntityKind =
  | 'workspace'
  | 'session'
  | 'message'
  | 'skill'
  | 'agent'
  | 'command'
  | 'instructions'
  | 'mcp'
  | 'provider'
  | 'alias'
  | 'hook'

/** linked = 跟随源;detached = 用户续聊/编辑过,永久不再跟随;suppressed = 本地删过的 tombstone。 */
export type ImportSyncStateValue = 'linked' | 'detached' | 'conflict' | 'source-missing' | 'suppressed'

export interface ImportSourceRow {
  sourceId: string
  kind: string
  configDir: string
  origin: string
  syncEnabled: boolean
  categories: string[]
  projectKeys: string[]
  lastCheckAt?: number
  lastSyncAt?: number
  status: string
  diagnostics: unknown[]
  createdAt: number
  updatedAt: number
}

function importSourceFromRow(row: Record<string, unknown>): ImportSourceRow {
  const list = (raw: unknown): string[] => {
    try {
      const value = parse<unknown>(raw)
      return Array.isArray(value) ? value.filter((x): x is string => typeof x === 'string') : []
    } catch {
      return []
    }
  }
  return {
    sourceId: String(row['source_id']),
    kind: String(row['kind']),
    configDir: String(row['config_dir'] ?? ''),
    origin: String(row['origin'] ?? 'auto'),
    syncEnabled: Number(row['sync_enabled'] ?? 0) !== 0,
    categories: list(row['categories']),
    projectKeys: list(row['project_keys']),
    ...(row['last_check_at'] === null || row['last_check_at'] === undefined
      ? {} : { lastCheckAt: Number(row['last_check_at']) }),
    ...(row['last_sync_at'] === null || row['last_sync_at'] === undefined
      ? {} : { lastSyncAt: Number(row['last_sync_at']) }),
    status: String(row['status'] ?? 'off'),
    diagnostics: (() => {
      try {
        const value = parse<unknown>(row['diagnostics'])
        return Array.isArray(value) ? value : []
      } catch {
        return []
      }
    })(),
    createdAt: Number(row['created_at'] ?? 0),
    updatedAt: Number(row['updated_at'] ?? 0)
  }
}

export function listImportSources(): ImportSourceRow[] {
  return stmt('SELECT * FROM import_sources ORDER BY created_at')
    .all()
    .map((row) => importSourceFromRow(row as Record<string, unknown>))
}

export function getImportSource(sourceId: string): ImportSourceRow | undefined {
  const row = stmt('SELECT * FROM import_sources WHERE source_id = ?').get(sourceId)
  return row === undefined ? undefined : importSourceFromRow(row as Record<string, unknown>)
}

export function putImportSource(source: ImportSourceRow): ImportSourceRow {
  stmt(
    `INSERT INTO import_sources (
       source_id, kind, config_dir, origin, sync_enabled, categories, project_keys,
       last_check_at, last_sync_at, status, diagnostics, created_at, updated_at
     ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (source_id) DO UPDATE SET
       kind = excluded.kind,
       config_dir = excluded.config_dir,
       origin = excluded.origin,
       sync_enabled = excluded.sync_enabled,
       categories = excluded.categories,
       project_keys = excluded.project_keys,
       last_check_at = excluded.last_check_at,
       last_sync_at = excluded.last_sync_at,
       status = excluded.status,
       diagnostics = excluded.diagnostics,
       updated_at = excluded.updated_at`
  ).run(
    source.sourceId,
    source.kind,
    source.configDir,
    source.origin,
    source.syncEnabled ? 1 : 0,
    JSON.stringify(source.categories),
    JSON.stringify(source.projectKeys),
    source.lastCheckAt ?? null,
    source.lastSyncAt ?? null,
    source.status,
    JSON.stringify(source.diagnostics),
    source.createdAt,
    source.updatedAt
  )
  return getImportSource(source.sourceId) ?? source
}

export interface ImportMappingRow {
  sourceId: string
  /** '' = 全局作用域,否则是 workspace id。 */
  scopeKey: string
  entityKind: ImportEntityKind
  sourceItemId: string
  targetId: string
  targetPath: string
  targetWorkspaceId: string
  sourceFingerprint: string
  targetFingerprint: string
  transformerVersion: number
  syncState: ImportSyncStateValue
  meta: Record<string, unknown>
  createdAt: number
  updatedAt: number
}

function importMappingFromRow(row: Record<string, unknown>): ImportMappingRow {
  return {
    sourceId: String(row['source_id']),
    scopeKey: String(row['scope_key'] ?? ''),
    entityKind: String(row['entity_kind']) as ImportEntityKind,
    sourceItemId: String(row['source_item_id']),
    targetId: String(row['target_id'] ?? ''),
    targetPath: String(row['target_path'] ?? ''),
    targetWorkspaceId: String(row['target_workspace_id'] ?? ''),
    sourceFingerprint: String(row['source_fingerprint'] ?? ''),
    targetFingerprint: String(row['target_fingerprint'] ?? ''),
    transformerVersion: Number(row['transformer_version'] ?? 1),
    syncState: String(row['sync_state'] ?? 'linked') as ImportSyncStateValue,
    meta: (() => {
      try {
        const value = parse<unknown>(row['meta'])
        return value !== null && typeof value === 'object' ? (value as Record<string, unknown>) : {}
      } catch {
        return {}
      }
    })(),
    createdAt: Number(row['created_at'] ?? 0),
    updatedAt: Number(row['updated_at'] ?? 0)
  }
}

export function getImportMapping(
  sourceId: string,
  scopeKey: string,
  entityKind: ImportEntityKind,
  sourceItemId: string
): ImportMappingRow | undefined {
  const row = stmt(
    `SELECT * FROM import_mappings
      WHERE source_id = ? AND scope_key = ? AND entity_kind = ? AND source_item_id = ?`
  ).get(sourceId, scopeKey, entityKind, sourceItemId)
  return row === undefined ? undefined : importMappingFromRow(row as Record<string, unknown>)
}

export function listImportMappings(
  sourceId: string,
  entityKind?: ImportEntityKind
): ImportMappingRow[] {
  const rows = entityKind === undefined
    ? stmt('SELECT * FROM import_mappings WHERE source_id = ?').all(sourceId)
    : stmt('SELECT * FROM import_mappings WHERE source_id = ? AND entity_kind = ?').all(sourceId, entityKind)
  return rows.map((row) => importMappingFromRow(row as Record<string, unknown>))
}

/** 本地实体反查它是不是导入来的 —— 续聊/编辑要在这一跳上标 detached。 */
export function findImportMappingsByTarget(targetId: string, entityKind: ImportEntityKind): ImportMappingRow[] {
  return stmt('SELECT * FROM import_mappings WHERE target_id = ? AND entity_kind = ?')
    .all(targetId, entityKind)
    .map((row) => importMappingFromRow(row as Record<string, unknown>))
}

export function putImportMapping(mapping: ImportMappingRow): void {
  stmt(
    `INSERT INTO import_mappings (
       source_id, scope_key, entity_kind, source_item_id, target_id, target_path,
       target_workspace_id, source_fingerprint, target_fingerprint, transformer_version,
       sync_state, meta, created_at, updated_at
     ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (source_id, scope_key, entity_kind, source_item_id) DO UPDATE SET
       target_id = excluded.target_id,
       target_path = excluded.target_path,
       target_workspace_id = excluded.target_workspace_id,
       source_fingerprint = excluded.source_fingerprint,
       target_fingerprint = excluded.target_fingerprint,
       transformer_version = excluded.transformer_version,
       sync_state = excluded.sync_state,
       meta = excluded.meta,
       updated_at = excluded.updated_at`
  ).run(
    mapping.sourceId,
    mapping.scopeKey,
    mapping.entityKind,
    mapping.sourceItemId,
    mapping.targetId,
    mapping.targetPath,
    mapping.targetWorkspaceId,
    mapping.sourceFingerprint,
    mapping.targetFingerprint,
    mapping.transformerVersion,
    mapping.syncState,
    JSON.stringify(mapping.meta),
    mapping.createdAt,
    mapping.updatedAt
  )
}

/**
 * 扫描缓存的一行。★ 这不是「导入过什么」的记录(那是 `import_mappings`),
 * 而是「上次扫描时从这个文件里读出了什么」—— 没导入的文件也有。
 */
export interface ImportScanCacheRow {
  sourceId: string
  sourcePath: string
  changeToken: string
  sessionId: string
  /** '' = 只读了头部,没算过全文哈希。 */
  contentHash: string
  cwd: string
  title: string
  model: string
  modelProvider: string
  /** partial 为真时是下界。 */
  messages: number
  partial: boolean
  sourceUpdatedAt: number
  scannedAt: number
}

function importScanCacheFromRow(row: Record<string, unknown>): ImportScanCacheRow {
  return {
    sourceId: String(row['source_id'] ?? ''),
    sourcePath: String(row['source_path'] ?? ''),
    changeToken: String(row['change_token'] ?? ''),
    sessionId: String(row['session_id'] ?? ''),
    contentHash: String(row['content_hash'] ?? ''),
    cwd: String(row['cwd'] ?? ''),
    title: String(row['title'] ?? ''),
    model: String(row['model'] ?? ''),
    modelProvider: String(row['model_provider'] ?? ''),
    messages: Number(row['messages'] ?? 0),
    partial: Number(row['partial'] ?? 0) === 1,
    sourceUpdatedAt: Number(row['source_updated_at'] ?? 0),
    scannedAt: Number(row['scanned_at'] ?? 0)
  }
}

/** 一个来源的全部缓存行,按 `source_path` 建索引供扫描循环查。 */
export function listImportScanCache(sourceId: string): Map<string, ImportScanCacheRow> {
  const rows = stmt('SELECT * FROM import_scan_cache WHERE source_id = ?').all(sourceId)
  const map = new Map<string, ImportScanCacheRow>()
  for (const row of rows) {
    const parsed = importScanCacheFromRow(row as Record<string, unknown>)
    map.set(parsed.sourcePath, parsed)
  }
  return map
}

/**
 * 整批写回。★ 一次事务,不是一行一个 —— 一轮扫描上千行,逐行提交在 WAL 上
 * 是上千次写放大,那正是这套缓存要省掉的开销。
 */
export function putImportScanCache(rows: readonly ImportScanCacheRow[]): void {
  if (rows.length === 0) return
  tx(() => {
    const write = stmt(
      `INSERT INTO import_scan_cache (
         source_id, source_path, change_token, session_id, content_hash, cwd, title,
         model, model_provider, messages, partial, source_updated_at, scanned_at
       ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT (source_id, source_path) DO UPDATE SET
         change_token = excluded.change_token,
         session_id = excluded.session_id,
         content_hash = excluded.content_hash,
         cwd = excluded.cwd,
         title = excluded.title,
         model = excluded.model,
         model_provider = excluded.model_provider,
         messages = excluded.messages,
         partial = excluded.partial,
         source_updated_at = excluded.source_updated_at,
         scanned_at = excluded.scanned_at`
    )
    for (const row of rows) {
      write.run(
        row.sourceId, row.sourcePath, row.changeToken, row.sessionId, row.contentHash,
        row.cwd, row.title, row.model, row.modelProvider, row.messages,
        row.partial ? 1 : 0, row.sourceUpdatedAt, row.scannedAt
      )
    }
  })
}

/**
 * 清掉本轮没再见到的行 —— 源侧文件删了,缓存不该无限长大。
 *
 * ★ 按 `scanned_at` 判定而不是传一份「见过的路径」清单:一轮扫描上千条路径,
 * 拼进 `NOT IN (...)` 会撞上 SQLite 的变量上限,而时间戳比较没有这个问题。
 */
export function pruneImportScanCache(sourceId: string, scannedAt: number): number {
  const result = stmt('DELETE FROM import_scan_cache WHERE source_id = ? AND scanned_at < ?')
    .run(sourceId, scannedAt)
  return Number(result.changes ?? 0)
}

/**
 * 把某个本地目标对应的全部映射切到新状态。
 *
 * ★ **无条件更新,不读后写** —— 「接受一次本地 run」和「同步器正要提交」
 * 可能落在同一毫秒,读后写会让其中一方的判断基于已经过期的快照。
 * 这条 UPDATE 在单条 SQL 里完成,调用方把它放进自己那个事务即可。
 */
export function markImportMappingsByTarget(
  targetId: string,
  entityKind: ImportEntityKind,
  syncState: ImportSyncStateValue,
  now: number
): number {
  const result = stmt(
    `UPDATE import_mappings SET sync_state = ?, updated_at = ?
      WHERE target_id = ? AND entity_kind = ? AND sync_state <> ?`
  ).run(syncState, now, targetId, entityKind, syncState)
  return Number(result.changes ?? 0)
}

/**
 * 本地删除 → tombstone。★ 目标 id 一并清空:那条记录指向的行已经不存在了,
 * 留着它会让「打开聊天」在历史里显示成可点击。
 */
export function suppressImportMappingsByTarget(
  targetId: string,
  entityKind: ImportEntityKind,
  now: number
): void {
  stmt(
    `UPDATE import_mappings SET sync_state = 'suppressed', target_id = '', updated_at = ?
      WHERE target_id = ? AND entity_kind = ?`
  ).run(now, targetId, entityKind)
}

/** 消息级映射跟着会话走 —— 会话没了,它们的 target 也没了。 */
export function deleteImportMessageMappings(sessionTargetIds: readonly string[]): void {
  for (const id of sessionTargetIds) {
    stmt(
      `DELETE FROM import_mappings
        WHERE entity_kind = 'message' AND json_extract(meta, '$.sessionId') = ?`
    ).run(id)
  }
}

export interface ImportBatchRow {
  id: string
  sourceId: string
  sourceKind: string
  trigger: string
  phase: string
  startedAt: number
  endedAt?: number
  counts: Record<string, number>
}

function importBatchFromRow(row: Record<string, unknown>): ImportBatchRow {
  return {
    id: String(row['id']),
    sourceId: String(row['source_id']),
    sourceKind: String(row['source_kind']),
    trigger: String(row['trigger']),
    phase: String(row['phase']),
    startedAt: Number(row['started_at'] ?? 0),
    ...(row['ended_at'] === null || row['ended_at'] === undefined
      ? {} : { endedAt: Number(row['ended_at']) }),
    counts: (() => {
      try {
        const value = parse<unknown>(row['counts'])
        return value !== null && typeof value === 'object' ? (value as Record<string, number>) : {}
      } catch {
        return {}
      }
    })()
  }
}

export function createImportBatch(batch: ImportBatchRow): void {
  stmt(
    `INSERT INTO import_batches (id, source_id, source_kind, trigger, phase, started_at, ended_at, counts)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?)`
  ).run(
    batch.id,
    batch.sourceId,
    batch.sourceKind,
    batch.trigger,
    batch.phase,
    batch.startedAt,
    batch.endedAt ?? null,
    JSON.stringify(batch.counts)
  )
}

export function updateImportBatch(
  id: string,
  phase: string,
  counts: Record<string, number>,
  endedAt?: number
): void {
  stmt('UPDATE import_batches SET phase = ?, counts = ?, ended_at = ? WHERE id = ?').run(
    phase,
    JSON.stringify(counts),
    endedAt ?? null,
    id
  )
}

/**
 * 上次进程没跑完就退出了。★ 标成 interrupted 而不是 failed ——
 * 已提交的那部分是**真的成功了**,按映射重扫就能接着走;
 * 报成 failed 会让用户以为要从头再来一遍。
 */
export function markInterruptedImportBatches(): number {
  const result = stmt(
    `UPDATE import_batches SET phase = 'interrupted'
      WHERE phase IN ('scanning', 'ready', 'importing')`
  ).run()
  return Number(result.changes ?? 0)
}

export interface ImportBatchItemRow {
  batchId: string
  seq: number
  category: string
  title: string
  sourcePath: string
  result: string
  targetKind?: string
  targetId?: string
  targetWorkspaceId?: string
  diagnostics: unknown[]
}

export function appendImportBatchItem(item: ImportBatchItemRow): void {
  stmt(
    `INSERT INTO import_batch_items (
       batch_id, seq, category, title, source_path, result,
       target_kind, target_id, target_workspace_id, diagnostics
     ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (batch_id, seq) DO UPDATE SET
       result = excluded.result, diagnostics = excluded.diagnostics`
  ).run(
    item.batchId,
    item.seq,
    item.category,
    item.title,
    item.sourcePath,
    item.result,
    item.targetKind ?? null,
    item.targetId ?? null,
    item.targetWorkspaceId ?? null,
    JSON.stringify(item.diagnostics)
  )
}

export function listImportBatches(offset: number, limit: number): { rows: ImportBatchRow[]; total: number } {
  const total = Number(
    (stmt('SELECT COUNT(*) AS n FROM import_batches').get() as Record<string, unknown>)['n'] ?? 0
  )
  const rows = stmt('SELECT * FROM import_batches ORDER BY started_at DESC, id DESC LIMIT ? OFFSET ?')
    .all(limit, offset)
    .map((row) => importBatchFromRow(row as Record<string, unknown>))
  return { rows, total }
}

export function listImportBatchItems(
  batchId: string,
  offset: number,
  limit: number
): { rows: ImportBatchItemRow[]; total: number } {
  const total = Number(
    (stmt('SELECT COUNT(*) AS n FROM import_batch_items WHERE batch_id = ?').get(batchId) as Record<string, unknown>)['n'] ?? 0
  )
  const rows = stmt('SELECT * FROM import_batch_items WHERE batch_id = ? ORDER BY seq LIMIT ? OFFSET ?')
    .all(batchId, limit, offset)
    .map((row) => {
      const r = row as Record<string, unknown>
      return {
        batchId: String(r['batch_id']),
        seq: Number(r['seq']),
        category: String(r['category']),
        title: String(r['title'] ?? ''),
        sourcePath: String(r['source_path'] ?? ''),
        result: String(r['result']),
        ...(r['target_kind'] === null || r['target_kind'] === undefined
          ? {} : { targetKind: String(r['target_kind']) }),
        ...(r['target_id'] === null || r['target_id'] === undefined
          ? {} : { targetId: String(r['target_id']) }),
        ...(r['target_workspace_id'] === null || r['target_workspace_id'] === undefined
          ? {} : { targetWorkspaceId: String(r['target_workspace_id']) }),
        diagnostics: (() => {
          try {
            const value = parse<unknown>(r['diagnostics'])
            return Array.isArray(value) ? value : []
          } catch {
            return []
          }
        })()
      }
    })
  return { rows, total }
}

/** 目标会话是否还在 —— 历史里那颗「打开」按钮据此决定可不可点。 */
export function sessionExists(id: string): boolean {
  return stmt('SELECT 1 FROM sessions WHERE id = ?').get(id) !== undefined
}
