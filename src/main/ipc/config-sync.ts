/**
 * 端到端加密配置同步 v2 的运行时编排。
 *
 * 服务端契约来自 `CoWork.Api/EncryptedConfigSyncEndpoints`:一个账户一个 vault、
 * 每个 category 一份完整密文快照。服务端只保存 AES-GCM 信封、HMAC 标识和版本号,
 * 没有密码、DEK 或配置明文。
 *
 * 本轮只接 `providers` category:供应商、模型及其 API Key/OAuth token。凭证明文
 * 只在 `captureProviderSyncData` 到 `encryptSyncDocument` 之间短暂存在于主进程内存;
 * 出站 HTTP、本地同步状态和服务端数据库里都只有密文。对端解密后经自己的
 * `host.secrets.set` 用那台设备的本地主密钥重新加密落库。
 *
 * ★ NextCoWork 平台登录 token 不在快照里。复制它等于复制登录身份,不是同步配置。
 * ★ 同步顺序是 push → pull。反过来会让未上传的本地改动被远端快照覆盖;
 *   先 push 可让服务端用 baseRevision 生成一个显式冲突,用户再决定保留哪边。
 *
 * 另接了 `usage` category(使用统计多设备合并)。它的节奏、顺序和冲突规则都和 providers
 * 不同,整段放在 `config-sync-usage.ts`,这里只在每个 tick 末尾调用一次、并提供开关入口。
 */
import { randomBytes } from 'node:crypto'
import { z } from 'zod'
import {
  ConfigSyncError,
  DEFAULT_SYNC_SELECTION,
  SYNC_RESOURCE_CHUNK_BYTES,
  syncLegacyExportSchema,
  syncPreviewResponseSchema,
  syncPullSchema,
  syncPushSchema,
  syncVaultResponseSchema,
  syncVaultSchema,
  type SyncCategory,
  type SyncControlState,
  type SyncErrorCode,
  type SyncEvent,
  type SyncLegacyExport,
  type SyncPreview,
  type SyncSetupRequest,
  type SyncStatus,
  type SyncVault
} from '../../shared/domain/config-sync'
import {
  SyncKeyring,
  decryptSyncDocument,
  encryptSyncDocument,
  encryptSyncResource,
  syncAuthKey,
  syncDigest,
  unwrapSyncKey,
  wrapSyncKey
} from '../config-sync-crypto'
import { syncRequest, type SyncRequestContext } from '../config-sync-http'
import {
  applyProviderSyncData,
  captureProviderSyncData,
  legacyProviderDocumentData,
  mergeProviderSyncData,
  parseProviderSyncData
} from '../config-sync-provider-snapshot'
import {
  acknowledgeSyncOutgoing,
  finishSyncEvent,
  mutateSyncState,
  readSyncState,
  sealSyncOutgoing,
  stageSyncEvent,
  syncStateWithVault
} from '../db/config-sync-state'
import { listResolvedModels } from '../state/model-bindings'
import * as repo from '../db/repo'
import { getHost } from '../runtime'
import { windows } from '../window/registry'
import { announceCredentialRef } from './provider-auth'
import {
  resetUsageSyncSchedule,
  runUsageSync,
  setUsageSyncEnabled,
  type UsageSyncContext
} from './config-sync-usage'

const PROVIDERS: SyncCategory = 'providers'
const okSchema = z.object({ ok: z.literal(true) }).strict()
const TICK_MS = 5000

interface ActiveSync {
  accountId: string
  generation: number
  abort: AbortController
  keyring: SyncKeyring
  vault: SyncVault | null
  deviceId: string
  deviceToken: string | null
  unsupported: boolean
  initializing: Promise<void> | null
}

let active: ActiveSync | null = null
let timer: NodeJS.Timeout | null = null
let running = false
let inFlight: Promise<void> | null = null
const backgroundOperations = new Set<Promise<void>>()
const directOperations = new Set<Promise<unknown>>()
let draining = false
let generation = 0

function trackOperation<T>(start: () => Promise<T>): Promise<T> {
  if (draining) return Promise.reject(new ConfigSyncError('accountChanged'))
  const operation = start()
  directOperations.add(operation)
  void operation.then(
    () => directOperations.delete(operation),
    () => directOperations.delete(operation)
  )
  return operation
}

function sessionIsCurrent(session: ActiveSync): boolean {
  return active === session && session.generation === generation && !session.abort.signal.aborted
}

function assertCurrent(session: ActiveSync): void {
  if (!sessionIsCurrent(session)) throw new ConfigSyncError('accountChanged')
}

function context(session: ActiveSync, authenticated = true): SyncRequestContext {
  return {
    host: getHost(),
    accountId: session.accountId,
    signal: session.abort.signal,
    assertCurrent: () => assertCurrent(session),
    deviceId: authenticated ? session.deviceId : repo.ensureSyncDeviceId(),
    deviceToken: authenticated ? session.deviceToken ?? '' : '',
    vault: authenticated ? session.vault : null
  }
}

function errorCode(error: unknown): SyncErrorCode {
  return error instanceof ConfigSyncError ? error.code : 'network'
}

function setError(session: ActiveSync, error: unknown): void {
  if (!sessionIsCurrent(session)) return
  const code = errorCode(error)
  if (code === 'unsupported') session.unsupported = true
  if (code === 'deviceRevoked') {
    session.keyring.lock()
    session.deviceToken = null
    void getHost().secrets.remove?.(tokenRef(session.accountId)).catch(() => undefined)
  }
  try {
    mutateSyncState(session.accountId, (state) => { state.errorCode = code })
  } catch {
    /* 退出封库途中不能为了记录错误再制造一个未捕获拒绝 */
  }
}

function controlState(session: ActiveSync | null): SyncControlState {
  if (session === null) {
    return {
      phase: 'signedOut',
      selection: { ...DEFAULT_SYNC_SELECTION },
      vaultConfigured: false,
      remembered: false,
      errorCode: null,
      bindings: []
    }
  }
  if (session.unsupported) {
    return {
      phase: 'unsupported',
      selection: { ...DEFAULT_SYNC_SELECTION },
      vaultConfigured: false,
      remembered: false,
      errorCode: 'unsupported',
      bindings: []
    }
  }
  let state
  try {
    state = readSyncState(session.accountId)
  } catch {
    return {
      phase: 'error',
      selection: { ...DEFAULT_SYNC_SELECTION },
      vaultConfigured: session.vault !== null,
      remembered: false,
      errorCode: 'invalidData',
      bindings: []
    }
  }
  let phase: SyncControlState['phase']
  if (session.vault === null) phase = 'off'
  else if (session.deviceToken === null) phase = 'passwordRequired'
  else if (state.errorCode === 'migrationRequired') phase = 'error'
  else {
    try {
      session.keyring.get(session.vault).fill(0)
      phase = state.confirmed ? (inFlight === null ? 'ready' : 'syncing') : 'review'
    } catch {
      phase = 'passwordRequired'
    }
  }
  return {
    phase,
    selection: state.selection,
    vaultConfigured: session.vault !== null,
    remembered: state.remembered,
    errorCode: state.errorCode,
    bindings: state.bindings
  }
}

function status(): SyncStatus {
  const session = active
  const control = controlState(session)
  let state: ReturnType<typeof readSyncState> | null = null
  if (session !== null) {
    try { state = readSyncState(session.accountId) } catch { /* reported by control */ }
  }
  const category = state?.categories.providers
  const pending = session === null
    ? 0
    : Number(category?.outgoing !== null && category?.outgoing !== undefined) +
      Number(repo.getConfigCategoryDirty(PROVIDERS, session.accountId))
  return {
    enabled: control.phase === 'ready' || control.phase === 'syncing' || control.phase === 'review',
    accountId: session?.accountId ?? null,
    deviceId: repo.ensureSyncDeviceId(),
    running,
    pending,
    conflicts: category?.conflict === null || category?.conflict === undefined ? 0 : 1,
    lastSuccessAt: state?.lastSuccessAt ?? null,
    lastError: control.errorCode === null ? null : `configSync.${control.errorCode}`,
    needsInitialReview: control.phase === 'review',
    control
  }
}

function announce(): void {
  windows.emitToAll('configSync:changed', status())
}

/** 仅供退出/后台 finally 路径:封库后不再为了算状态制造未捕获拒绝。 */
function announceSafe(): void {
  try { announce() } catch { /* 应用正在退出或替换数据库 */ }
}

function tokenRef(accountId: string): string {
  return `config-sync:device:${Buffer.from(accountId).toString('base64url')}`
}

function tokenIdentity(vault: SyncVault, deviceId: string): string {
  return `${vault.vaultId}/${vault.keyVersion}/${deviceId}`
}

function validSecret(value: string): boolean {
  try {
    const bytes = Buffer.from(value, 'base64')
    return bytes.length === 32 && bytes.toString('base64') === value
  } catch {
    return false
  }
}

async function loadDeviceRegistration(
  accountId: string,
  vault: SyncVault
): Promise<{ deviceId: string; token: string } | null> {
  const raw = await getHost().secrets.get(tokenRef(accountId))
  if (raw === null) return null
  try {
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null
    const record = parsed as Record<string, unknown>
    const deviceId = record['deviceId']
    const token = record['token']
    return typeof deviceId === 'string' && deviceId !== '' && deviceId.length <= 128 &&
      record['identity'] === tokenIdentity(vault, deviceId) &&
      typeof token === 'string' && validSecret(token)
      ? { deviceId, token }
      : null
  } catch {
    return null
  }
}

async function saveDeviceToken(
  accountId: string,
  vault: SyncVault,
  deviceId: string,
  token: string
): Promise<void> {
  await getHost().secrets.set(tokenRef(accountId), JSON.stringify({
    identity: tokenIdentity(vault, deviceId),
    deviceId,
    token
  }))
}

function requireSession(): ActiveSync {
  const session = active
  if (session === null) throw new ConfigSyncError('signedOut')
  assertCurrent(session)
  return session
}

function unlocked(session: ActiveSync): { vault: SyncVault; key: Buffer; deviceToken: string } {
  const vault = session.vault
  const deviceToken = session.deviceToken
  if (vault === null || deviceToken === null) throw new ConfigSyncError('locked')
  return { vault, key: session.keyring.get(vault), deviceToken }
}

async function fetchVault(session: ActiveSync): Promise<ReturnType<typeof syncVaultResponseSchema.parse>> {
  return syncRequest(context(session, false), '/vault', syncVaultResponseSchema, undefined, false)
}

async function bootstrap(session: ActiveSync): Promise<void> {
  try {
    const remote = await fetchVault(session)
    assertCurrent(session)
    session.vault = remote.vault
    mutateSyncState(session.accountId, (state) => {
      state.legacyExists = remote.legacyExists
      state.migrationRequired = remote.migrationRequired
      state.errorCode = remote.migrationRequired ? 'migrationRequired' : null
      if (remote.vault !== null) syncStateWithVault(state, remote.vault)
    })
    if (remote.vault !== null && !remote.migrationRequired) {
      const restored = await session.keyring.restore(remote.vault)
      const registration = restored
        ? await loadDeviceRegistration(session.accountId, remote.vault)
        : null
      assertCurrent(session)
      if (restored && registration !== null) {
        session.deviceId = registration.deviceId
        session.deviceToken = registration.token
      }
    }
    if (readSyncState(session.accountId).confirmed && session.deviceToken !== null) void tick()
  } catch (error) {
    setError(session, error)
  } finally {
    if (sessionIsCurrent(session)) announce()
  }
}

function stopInternal(emit: boolean): void {
  generation += 1
  if (timer !== null) clearInterval(timer)
  timer = null
  active?.abort.abort()
  active?.keyring.lock()
  active = null
  inFlight = null
  running = false
  // 新会话(换账户 / 重新解锁)第一次 tick 就同步一轮使用统计,不等上一个会话留下的节流窗口
  resetUsageSyncSchedule()
  if (emit) announce()
}

export function startConfigSync(nextAccountId: string): void {
  if (active?.accountId === nextAccountId) return
  stopInternal(false)
  repo.configureSyncAccount(nextAccountId, true)
  const session: ActiveSync = {
    accountId: nextAccountId,
    generation,
    abort: new AbortController(),
    keyring: new SyncKeyring(getHost().secrets),
    vault: null,
    deviceId: repo.ensureSyncDeviceId(),
    deviceToken: null,
    unsupported: false,
    initializing: null
  }
  active = session
  running = true
  timer = setInterval(() => { void tick().catch(() => undefined) }, TICK_MS)
  timer.unref?.()
  session.initializing = bootstrap(session).finally(() => {
    if (active === session) session.initializing = null
  })
  announce()
}

export function stopConfigSync(): void {
  stopInternal(true)
  try { repo.configureSyncAccount(null) } catch { /* 退出封库时不再碰库 */ }
}

/** 切账户专用:先掐网络请求,再等已经进入本地应用阶段的那一轮退出。 */
export async function stopConfigSyncAndWait(): Promise<void> {
  draining = true
  try {
    // vault 创建/首次合并/冲突决议可能已经被服务端接受,不能中途 abort 后
    // 让客户端忘记结果。先等这三类显式操作完成,期间 trackOperation 拒绝新操作。
    await Promise.allSettled([...directOperations])
    stopConfigSync()
    await Promise.allSettled([...backgroundOperations])
  } finally {
    draining = false
  }
}

/** 整库替换专用:排空显式操作后只停内存/网络,不改即将快照的数据库。 */
export async function pauseConfigSyncForDatabaseReplacement(): Promise<void> {
  draining = true
  try {
    await Promise.allSettled([...directOperations])
    stopInternal(false)
    await Promise.allSettled([...backgroundOperations])
  } finally {
    draining = false
  }
}

/** 退出专用:只停任务,不广播、不摸即将关闭的数据库。 */
export function shutdownConfigSync(): void {
  stopInternal(false)
}

export function getConfigSyncStatus(): SyncStatus {
  return status()
}

/**
 * 创建新 vault 或用密码解锁已有 vault。新 vault 的首设备在 `/vault` 同一事务注册;
 * 已有 vault 走 `/devices/register`,设备 token 只以本机程序密文保存。
 */
async function setupConfigSyncInternal(req: SyncSetupRequest): Promise<SyncStatus> {
  const session = requireSession()
  await session.initializing
  assertCurrent(session)
  const remote = await fetchVault(session)

  const baseDeviceId = repo.ensureSyncDeviceId()
  let deviceId = baseDeviceId
  let savedDeviceToken: string | null = null
  if (remote.vault !== null) {
    const registration = await loadDeviceRegistration(session.accountId, remote.vault)
    savedDeviceToken = registration?.token ?? null
    deviceId = registration?.deviceId ?? `${baseDeviceId}:${randomBytes(8).toString('hex')}`
  }
  let key: Buffer | null = null
  let vault: SyncVault
  let deviceToken: string
  let created = false

  try {
    if (remote.vault === null) {
      created = true
    key = randomBytes(32)
    const proposed = await wrapSyncKey(session.accountId, req.password, key)
    deviceToken = randomBytes(32).toString('base64')
    vault = await syncRequest(
      context(session, false),
      '/vault',
      syncVaultSchema,
      {
        vault: proposed,
        authKey: syncAuthKey(key, proposed),
        deviceId,
        deviceToken
      },
      false
    )
    } else {
      vault = remote.vault
      key = await unwrapSyncKey(session.accountId, req.password, vault)
      deviceToken = savedDeviceToken ?? randomBytes(32).toString('base64')
      await syncRequest(
        context(session, false),
        '/devices/register',
        okSchema,
        {
          vaultId: vault.vaultId,
          keyVersion: vault.keyVersion,
          authKey: syncAuthKey(key, vault),
          deviceId,
          deviceToken
        },
        false
      )
    }

    assertCurrent(session)
    await saveDeviceToken(session.accountId, vault, deviceId, deviceToken)
    await session.keyring.set(vault, key, req.remember)
    session.vault = vault
    session.deviceId = deviceId
    session.deviceToken = deviceToken
    /*
      账户还有 v1 明文云配置时,vault 落库那一刻就被服务端标成 pending,
      push/pull 一律拒绝 —— 必须先导出、加密归档、提交删除,才能开始 v2 同步。
      新建 vault(legacyExists)和解锁已有 vault(migrationRequired)都要走这一步。
    */
    if (remote.legacyExists || remote.migrationRequired) {
      await migrateLegacyCloudData(session, vault, key)
    }
    mutateSyncState(session.accountId, (state) => {
      const wasSameVault = state.vault?.vaultId === vault.vaultId &&
        state.vault.keyVersion === vault.keyVersion
      const wasConfirmed = state.confirmed
      syncStateWithVault(state, vault)
      // 使用统计默认一并同步;同一个已确认的 vault 重新解锁时保留用户在数据页做过的选择
      const usage = wasSameVault && wasConfirmed ? state.selection.usage : true
      state.selection = { ...DEFAULT_SYNC_SELECTION, providers: true, usage }
      state.confirmed = created ? true : wasSameVault && wasConfirmed
      state.remembered = req.remember
      state.legacyExists = remote.legacyExists
      state.migrationRequired = false
      state.errorCode = null
    })

    if (created) {
      repo.setConfigCategoryDirty(PROVIDERS, session.accountId, true)
    } else if (!readSyncState(session.accountId).confirmed) {
      const preview = await remoteProviderEvent(session)
      if (preview === null) {
        mutateSyncState(session.accountId, (state) => { state.confirmed = true })
        repo.setConfigCategoryDirty(PROVIDERS, session.accountId, true)
      }
    }
    if (readSyncState(session.accountId).confirmed) await tick()
    announce()
    return status()
  } finally {
    key?.fill(0)
    // 失败也要把 phase(如迁移阻塞/密码错误)推给设置页;封库途中则安静跳过。
    announceSafe()
  }
}

export function setupConfigSync(req: SyncSetupRequest): Promise<SyncStatus> {
  return trackOperation(() => setupConfigSyncInternal(req))
}

/**
 * v1 明文云配置 → v2 加密世界的一次性迁移。
 *
 * 顺序是「归档 → 本机合并 → 提交删除」,三步都可安全重试:
 * - 归档资源幂等(同 id 同密文先到者赢),重复上传无副作用;
 * - 本机合并是并集且本机同 id 优先,重跑结果不变;
 * - 提交被服务端用同快照水位核对,响应丢失后重试幂等。
 *
 * ★ 归档保存的是**完整原样导出**(含本客户端不再认识的类别),不是只留 providers:
 * 删除 v1 明文必须先证明「导出过的那份」被完整加密保存,否则就是销毁用户数据。
 */
async function migrateLegacyCloudData(
  session: ActiveSync,
  vault: SyncVault,
  key: Buffer
): Promise<void> {
  const events: SyncLegacyExport['events'] = []
  let documents: SyncLegacyExport['documents']
  let conflicts: SyncLegacyExport['conflicts']
  let counts: SyncLegacyExport['counts']
  let cursor = 0
  for (;;) {
    const page = await syncRequest(
      context(session),
      `/legacy?cursor=${cursor}&limit=100`,
      syncLegacyExportSchema
    )
    // documents/conflicts 每页都是完整快照(v1 已被冻结,跨页不会变),取最新一页。
    documents = page.documents
    conflicts = page.conflicts
    counts = page.counts
    events.push(...page.events)
    cursor = page.cursor
    if (!page.hasMore) break
  }

  const archive = Buffer.from(JSON.stringify({ documents, events, conflicts }), 'utf8')
  const archiveIds: string[] = []
  for (let offset = 0; offset < archive.length; offset += SYNC_RESOURCE_CHUNK_BYTES) {
    const chunk = archive.subarray(offset, Math.min(offset + SYNC_RESOURCE_CHUNK_BYTES, archive.length))
    const resource = encryptSyncResource(key, vault, Buffer.from(chunk))
    await syncRequest(context(session), `/resources/${resource.id}`, okSchema, {
      keyVersion: vault.keyVersion,
      payload: resource.payload
    })
    archiveIds.push(resource.id)
  }

  const legacy = legacyProviderDocumentData(documents)
  if (legacy.providers.length > 0 || legacy.aliases.length > 0) {
    const local = await captureProviderSyncData(getHost())
    await applyProviderSyncData(getHost(), mergeProviderSyncData(legacy, local))
  }

  await syncRequest(context(session), '/migration/commit', okSchema, {
    archiveIds,
    deleteLegacy: true,
    expected: {
      documents: counts.documents,
      events: counts.events,
      conflicts: counts.conflicts,
      maxEventId: counts.maxEventId,
      maxRevision: counts.maxRevision
    }
  })
}

async function remoteProviderEvent(session: ActiveSync): Promise<SyncEvent | null> {
  unlocked(session).key.fill(0)
  const response = await syncRequest(
    context(session),
    `/preview?kind=${PROVIDERS}`,
    syncPreviewResponseSchema
  )
  return response.events[0] ?? null
}

function previewOf(event: SyncEvent | null, session: ActiveSync): SyncPreview {
  if (event === null) return { items: [], count: 0 }
  const { vault, key } = unlocked(session)
  try {
    const document = decryptSyncDocument(key, vault, event)
    const data = parseProviderSyncData(document.data)
    const items: SyncPreview['items'] = [
      ...data.providers.map((provider) => ({
        kind: 'provider' as const,
        entityId: provider.id,
        revision: event.revision,
        updatedAt: '',
        updatedByDeviceId: event.deviceId
      })),
      ...data.aliases.map((alias) => ({
        kind: 'modelAlias' as const,
        entityId: `${alias.providerId}/${alias.alias}`,
        revision: event.revision,
        updatedAt: '',
        updatedByDeviceId: event.deviceId
      }))
    ]
    // count 也包含凭证,但 items 不为密钥生成可识别条目,避免泄露「哪家有 key」。
    return { items, count: items.length + Object.keys(data.credentials).length }
  } finally {
    key.fill(0)
  }
}

export async function getConfigSyncPreview(): Promise<SyncPreview> {
  const session = requireSession()
  return previewOf(await remoteProviderEvent(session), session)
}

async function confirmInitialConfigSyncInternal(): Promise<void> {
  const session = requireSession()
  const event = await remoteProviderEvent(session)
  if (event === null) {
    mutateSyncState(session.accountId, (state) => { state.confirmed = true })
    repo.setConfigCategoryDirty(PROVIDERS, session.accountId, true)
  } else {
    const { vault, key } = unlocked(session)
    let merged
    try {
      const remote = decryptSyncDocument(key, vault, event)
      const local = await captureProviderSyncData(getHost())
      merged = mergeProviderSyncData(remote.data, local)
    } finally {
      key.fill(0)
    }
    // 首次确认是「合并」:先用远端事件推进 revision/cursor,再把并集标脏上传。
    // 当前设备同 id 的配置与 key 优先,不会被一个旧云快照静默顶掉。
    mutateSyncState(session.accountId, (state) => { state.confirmed = true })
    try {
      await applyRemoteEvent(session, event, merged)
      repo.setConfigCategoryDirty(PROVIDERS, session.accountId, true)
    } catch (error) {
      mutateSyncState(session.accountId, (state) => { state.confirmed = false })
      throw error
    }
  }
  await tick()
  announce()
}

export function confirmInitialConfigSync(): Promise<void> {
  return trackOperation(confirmInitialConfigSyncInternal)
}

async function prepareOutgoing(session: ActiveSync): Promise<void> {
  const state = readSyncState(session.accountId)
  const category = state.categories.providers
  if (!state.confirmed || !state.selection.providers || category.outgoing !== null ||
      category.conflict !== null || !repo.getConfigCategoryDirty(PROVIDERS, session.accountId)) return

  // 先清再读:读取期间发生的新写入会重新置 true,不会被这一轮末尾误清掉。
  repo.setConfigCategoryDirty(PROVIDERS, session.accountId, false)
  let key: Buffer | null = null
  try {
    const data = await captureProviderSyncData(getHost())
    assertCurrent(session)
    const unlockedState = unlocked(session)
    key = unlockedState.key
    const document = { version: 2 as const, kind: PROVIDERS, data }
    const fingerprint = syncDigest(key, document)
    if (fingerprint === category.digest) return
    const envelope = encryptSyncDocument(key, unlockedState.vault, document, category.revision)
    sealSyncOutgoing(session.accountId, PROVIDERS, envelope, fingerprint)
  } catch (error) {
    repo.setConfigCategoryDirty(PROVIDERS, session.accountId, true)
    throw error
  } finally {
    key?.fill(0)
  }
}

async function pushOutgoing(session: ActiveSync): Promise<void> {
  const outgoing = readSyncState(session.accountId).categories.providers.outgoing
  if (outgoing === null) return
  const response = await syncRequest(context(session), '/push', syncPushSchema, {
    mutations: [outgoing]
  })
  const accepted = response.accepted.find((item) => item.mutationId === outgoing.mutationId)
  if (accepted !== undefined) {
    acknowledgeSyncOutgoing(session.accountId, PROVIDERS, outgoing.mutationId, accepted.revision)
    return
  }
  const conflict = response.conflicts.find((item) => item.mutationId === outgoing.mutationId)
  if (conflict?.remote === undefined || conflict.remote === null) throw new ConfigSyncError('conflict')
  mutateSyncState(session.accountId, (state) => {
    state.categories.providers.conflict = conflict.remote
  })
}

async function applyRemoteEvent(
  session: ActiveSync,
  event: SyncEvent,
  applyData?: unknown
): Promise<void> {
  const { vault, key } = unlocked(session)
  let document
  let fingerprint: string
  try {
    document = decryptSyncDocument(key, vault, event)
    if (document.kind !== PROVIDERS) throw new ConfigSyncError('invalidData')
    // 在动本地数据之前先做完整形状校验。
    parseProviderSyncData(document.data)
    fingerprint = syncDigest(key, document)
  } finally {
    key.fill(0)
  }

  assertCurrent(session)
  stageSyncEvent(session.accountId, event)
  assertCurrent(session)
  await applyProviderSyncData(getHost(), applyData ?? document.data)
  assertCurrent(session)
  finishSyncEvent(session.accountId, PROVIDERS, event.mutationId, fingerprint)
  windows.emitToAll('provider:changed', {
    providers: repo.listProviders(),
    models: listResolvedModels()
  })
  for (const provider of repo.listProviders()) announceCredentialRef(provider.credentialRef)
}

async function applyPending(session: ActiveSync): Promise<void> {
  const pending = readSyncState(session.accountId).categories.providers.pendingApply
  if (pending !== null) await applyRemoteEvent(session, pending)
}

function advanceOwnCursor(accountId: string, event: SyncEvent): void {
  mutateSyncState(accountId, (state) => {
    const category = state.categories.providers
    category.cursor = Math.max(category.cursor, event.cursor)
    category.revision = Math.max(category.revision, event.revision)
  })
}

async function pullRemote(session: ActiveSync): Promise<void> {
  for (;;) {
    const state = readSyncState(session.accountId)
    if (state.categories.providers.conflict !== null) return
    const response = await syncRequest(
      context(session),
      `/pull?cursor=${state.categories.providers.cursor}&limit=100&kind=${PROVIDERS}`,
      syncPullSchema
    )
    for (const event of response.events) {
      assertCurrent(session)
      const current = readSyncState(session.accountId).categories.providers
      if (event.cursor <= current.cursor) continue
      if (event.deviceId === session.deviceId && event.revision <= current.revision) {
        advanceOwnCursor(session.accountId, event)
        continue
      }
      if (current.outgoing !== null || repo.getConfigCategoryDirty(PROVIDERS, session.accountId)) {
        // 正常顺序下本地改动已在上面的 push 变成 accepted/conflict。走到这里说明
        // 写入恰好发生在网络等待期间;保留本地,下轮先 push,不让远端覆盖它。
        return
      }
      if (event.revision <= current.revision) {
        advanceOwnCursor(session.accountId, event)
        continue
      }
      await applyRemoteEvent(session, event)
    }
    if (!response.hasMore) return
  }
}

async function runTick(session: ActiveSync): Promise<void> {
  const control = controlState(session)
  if (control.phase !== 'ready' && control.phase !== 'syncing') return
  await applyPending(session)
  await prepareOutgoing(session)
  await pushOutgoing(session)
  await pullRemote(session)
  // 放在 providers 之后:usage 自己吞掉非会话级错误,但哪怕它抛了,供应商这一轮也已经做完
  await runUsageSync(usageContext(session))
  mutateSyncState(session.accountId, (state) => {
    state.lastSuccessAt = Date.now()
    state.errorCode = null
  })
  if (!repo.getConfigCategoryDirty(PROVIDERS, session.accountId)) {
    repo.setConfigDirty(session.accountId, false)
  }
}

/** 把当前会话的网络 / 密钥能力交给 usage 引擎;那边不直接碰 `ActiveSync`。 */
function usageContext(session: ActiveSync): UsageSyncContext {
  const { vault, key } = unlocked(session)
  key.fill(0)
  return {
    accountId: session.accountId,
    originId: repo.ensureSyncDeviceId(),
    vault,
    key: () => unlocked(session).key,
    request: <T>(path: string, schema: z.ZodType<T>, body?: unknown): Promise<T> =>
      syncRequest(context(session), path, schema, body),
    assertCurrent: () => assertCurrent(session)
  }
}

/**
 * 数据页「同步使用统计」开关。只开放 usage:其它 category 目前没有关闭路径,
 * 没有后端能力的开关就不该有入口。
 */
async function setConfigSyncUsageInternal(enabled: boolean): Promise<SyncStatus> {
  const session = requireSession()
  // ★ 等进行中的一轮跑完再改:那一轮可能正推着 usage 快照,中途清空进度会让它的确认对不上
  //   (acknowledge 找不到 outgoing → invalidData),表现为关掉开关的同时冒一条同步失败日志。
  if (inFlight !== null) await inFlight
  assertCurrent(session)
  setUsageSyncEnabled(session.accountId, enabled)
  if (enabled) void tick()
  announce()
  return status()
}

export function setConfigSyncUsage(enabled: boolean): Promise<SyncStatus> {
  return trackOperation(() => setConfigSyncUsageInternal(enabled))
}

async function tick(): Promise<void> {
  if (inFlight !== null) return inFlight
  const session = active
  if (session === null) return
  const operation = runTick(session)
    .catch((error) => setError(session, error))
    .finally(() => {
      backgroundOperations.delete(operation)
      if (active === session) inFlight = null
      if (sessionIsCurrent(session)) announceSafe()
    })
  backgroundOperations.add(operation)
  inFlight = operation
  announceSafe()
  return operation
}

export function getConfigSyncConflicts() {
  const session = active
  if (session === null) return []
  const category = readSyncState(session.accountId).categories.providers
  if (category.conflict === null || category.outgoing === null) return []
  return [{
    id: category.outgoing.mutationId,
    kind: 'provider' as const,
    entityId: PROVIDERS,
    localRevision: category.outgoing.baseRevision,
    remoteRevision: category.conflict.revision,
    // ★ IPC 只发元数据。明文快照(尤其 credentials)绝不为了冲突列表进渲染层。
    localPayload: {},
    remotePayload: {},
    status: 'pending' as const,
    createdAt: Date.now()
  }]
}

async function resolveConfigSyncConflictInternal(id: string, useRemote: boolean): Promise<void> {
  const session = requireSession()
  const state = readSyncState(session.accountId)
  const category = state.categories.providers
  const conflict = category.conflict
  const original = category.outgoing
  if (conflict === null || original === null || original.mutationId !== id) {
    throw new ConfigSyncError('conflict')
  }

  const { vault, key } = unlocked(session)
  let document
  try {
    document = useRemote
      ? decryptSyncDocument(key, vault, conflict)
      : { version: 2 as const, kind: PROVIDERS, data: await captureProviderSyncData(getHost()) }
    parseProviderSyncData(document.data)
    const mutation = encryptSyncDocument(key, vault, document, conflict.revision)
    const response = await syncRequest(context(session), '/resolve', syncPushSchema, {
      mutation,
      supersedes: original.mutationId
    })
    const accepted = response.accepted.find((item) => item.mutationId === mutation.mutationId)
    if (accepted === undefined) throw new ConfigSyncError('conflict')
    if (useRemote) await applyProviderSyncData(getHost(), document.data)
    const fingerprint = syncDigest(key, document)
    mutateSyncState(session.accountId, (next) => {
      const target = next.categories.providers
      target.revision = accepted.revision
      target.cursor = Math.max(target.cursor, conflict.cursor)
      target.digest = fingerprint
      target.outgoing = null
      target.outgoingDigest = null
      target.conflict = null
      target.pendingApply = null
    })
    repo.setConfigCategoryDirty(PROVIDERS, session.accountId, false)
    windows.emitToAll('provider:changed', { providers: repo.listProviders(), models: listResolvedModels() })
  } finally {
    key.fill(0)
  }
  await tick()
  announce()
}

export function resolveConfigSyncConflict(id: string, useRemote: boolean): Promise<void> {
  return trackOperation(() => resolveConfigSyncConflictInternal(id, useRemote))
}
