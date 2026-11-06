import { z } from 'zod'

export const CONFIG_SYNC_VERSION = 2 as const
/**
 * ★ 与服务端 `SyncCategories.All` 及库里 document/event 的 kind CHECK 约束必须一致:
 * 客户端多一个,服务端对该类的 push/pull 一律 400 invalidData。
 *
 * `usage` 是「使用统计」的按日汇总(需求:多台设备的统计合并展示)。它不是配置 ——
 * 快照里每台设备各占一片、只由自己改写,冲突由客户端自动合并,不走「保留本地 / 使用远端」,
 * 见 `main/ipc/config-sync-usage.ts`。
 */
export const SYNC_CATEGORIES = ['providers', 'preferences', 'connections', 'extensions', 'workspaces', 'automation', 'usage'] as const
export type SyncCategory = typeof SYNC_CATEGORIES[number]
export type SyncSelection = Record<SyncCategory, boolean>
export const DEFAULT_SYNC_SELECTION: SyncSelection = {
  providers: false, preferences: false, connections: false,
  extensions: false, workspaces: false, automation: false, usage: false
}
/** 一次 push / 预览最多每个 category 一条(服务端 `MaxPushMutations` 同样由 category 数推出,原先两边都写死 6)。 */
const SYNC_MAX_BATCH = SYNC_CATEGORIES.length
export const SYNC_MAX_CIPHERTEXT_BYTES = 8 * 1024 * 1024
export const SYNC_RESOURCE_CHUNK_BYTES = 256 * 1024
export const SYNC_PASSWORD_MIN_LENGTH = 8

export const SYNC_ERRORS = ['signedOut', 'locked', 'password', 'invalidData', 'unsupported', 'network', 'conflict', 'storage', 'accountChanged', 'deviceRevoked', 'migrationRequired', 'bindingRequired', 'busy'] as const
export type SyncErrorCode = typeof SYNC_ERRORS[number]
export class ConfigSyncError extends Error {
  constructor(readonly code: SyncErrorCode) { super(`configSync.${code}`); this.name = 'ConfigSyncError' }
}

const base64 = (min: number, max: number) => z.string().max(Math.ceil(max / 3) * 4).refine((value) => {
  if (!value.length || value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) return false
  const size = value.length / 4 * 3 - (value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0)
  return size >= min && size <= max
})
const counter = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER)
export const syncCipherSchema = z.object({
  nonce: base64(12, 12), tag: base64(16, 16), ciphertext: base64(1, SYNC_MAX_CIPHERTEXT_BYTES)
}).strict()
export type SyncCipher = z.infer<typeof syncCipherSchema>
export const syncKdfSchema = z.object({
  algorithm: z.literal('scrypt'), salt: base64(32, 32), n: z.literal(32768), r: z.literal(8), p: z.literal(1)
}).strict()
export const syncVaultSchema = z.object({
  version: z.literal(CONFIG_SYNC_VERSION), accountId: z.string().min(1).max(128), vaultId: z.uuid(),
  keyVersion: counter.positive(), wrappingVersion: counter.positive(), kdf: syncKdfSchema, wrappedKey: syncCipherSchema
}).strict()
export type SyncVault = z.infer<typeof syncVaultSchema>
export const syncVaultResponseSchema = z.object({ vault: syncVaultSchema.nullable(), legacyExists: z.boolean(), migrationRequired: z.boolean() }).strict()
export const syncEnvelopeSchema = z.object({
  version: z.literal(CONFIG_SYNC_VERSION), vaultId: z.uuid(), keyVersion: counter.positive(),
  kind: z.enum(SYNC_CATEGORIES), entityId: z.string().regex(/^[a-f0-9]{64}$/), mutationId: z.uuid(),
  operation: z.enum(['upsert', 'delete']), payload: syncCipherSchema, baseRevision: counter
}).strict()
export type SyncEnvelope = z.infer<typeof syncEnvelopeSchema>
export const syncEventSchema = syncEnvelopeSchema.extend({
  revision: counter.positive(), cursor: counter.positive(), deviceId: z.string().min(1).max(128)
}).strict()
export type SyncEvent = z.infer<typeof syncEventSchema>
export const syncPullSchema = z.object({ cursor: counter, hasMore: z.boolean(), events: z.array(syncEventSchema).max(100) }).strict()
export const syncPushSchema = z.object({
  accepted: z.array(z.object({ mutationId: z.uuid(), revision: counter.positive() }).strict()).max(SYNC_MAX_BATCH),
  conflicts: z.array(z.object({ mutationId: z.uuid(), remote: syncEventSchema.nullable() }).strict()).max(SYNC_MAX_BATCH)
}).strict()
export type SyncPushResult = z.infer<typeof syncPushSchema>
export const syncSelectionSchema = z.object({
  providers: z.boolean(), preferences: z.boolean(), connections: z.boolean(), extensions: z.boolean(), workspaces: z.boolean(), automation: z.boolean(),
  usage: z.boolean()
}).strict()
export const syncDocumentSchema = z.object({
  version: z.literal(CONFIG_SYNC_VERSION), kind: z.enum(SYNC_CATEGORIES), data: z.unknown()
}).strict()
export type SyncDocument = z.infer<typeof syncDocumentSchema>
export const syncDevicesSchema = z.object({ items: z.array(z.object({ deviceId: z.string().min(1).max(128), current: z.boolean(), revoked: z.boolean(), lastSeenAt: z.string().max(64) }).strict()).max(100) }).strict()
export type SyncDevice = z.infer<typeof syncDevicesSchema>['items'][number]
export const syncResourceResponseSchema = z.object({ keyVersion: counter.positive(), payload: syncCipherSchema }).strict()
export const syncPreviewResponseSchema = z.object({ events: z.array(syncEventSchema).max(SYNC_MAX_BATCH) }).strict()
export const syncResourceFileSchema = z.object({
  id: z.string().regex(/^[a-f0-9]{64}$/), size: counter.max(16 * 1024 * 1024),
  chunks: z.array(z.string().regex(/^[a-f0-9]{64}$/)).max(64)
}).strict()
export type SyncResourceFile = z.infer<typeof syncResourceFileSchema>
/**
 * `/legacy` 的分页导出。documents/conflicts 每页都是完整快照,events 按 cursor 翻页;
 * counts 是提交迁移时要原样回传的冻结水位。payload 一律 unknown —— 迁移只按 kind
 * 挑出仍被本客户端认识的记录,其余原样归档,绝不解释。
 */
export const syncLegacyExportSchema = z.object({
  cursor: counter,
  hasMore: z.boolean(),
  documents: z.array(z.object({
    workspaceId: counter, kind: z.string().max(64), entityId: z.string().max(512),
    payload: z.unknown(), revision: counter, updatedAt: z.string().max(64), updatedByDeviceId: z.string().max(128)
  }).strict()).max(100_000),
  events: z.array(z.object({
    cursor: counter, workspaceId: counter, kind: z.string().max(64), entityId: z.string().max(512),
    operation: z.string().max(16), payload: z.unknown(), revision: counter,
    createdAt: z.string().max(64), deviceId: z.string().max(128), mutationId: z.uuid()
  }).strict()).max(100),
  conflicts: z.array(z.object({
    workspaceId: counter, kind: z.string().max(64), entityId: z.string().max(512), mutationId: z.uuid(),
    localRevision: counter, remoteRevision: counter, localPayload: z.unknown(), remotePayload: z.unknown(),
    status: z.string().max(16), createdAt: z.string().max(64)
  }).strict()).max(100_000),
  counts: z.object({
    documents: counter, events: counter, conflicts: counter, secrets: counter,
    maxEventId: counter, maxRevision: counter
  }).strict()
}).strict()
export type SyncLegacyExport = z.infer<typeof syncLegacyExportSchema>
export interface SyncSetupRequest { password: string; remember: boolean }
export interface SyncConfigureRequest { selection: SyncSelection; importLocal: boolean; prefer: 'local' | 'remote' }
export interface SyncPasswordChange { password: string; revokeOtherDevices: boolean }
export interface SyncBinding { kind: 'workspace' | 'extension' | 'automation' | 'connection'; id: string; name: string }
export interface SyncControlState {
  phase: 'signedOut' | 'off' | 'passwordRequired' | 'locked' | 'review' | 'ready' | 'syncing' | 'error' | 'unsupported'
  selection: SyncSelection
  vaultConfigured: boolean
  remembered: boolean
  errorCode: SyncErrorCode | null
  bindings: SyncBinding[]
}

/** Legacy types remain readable only for explicit migration; v2 never sends their payloads. */
export type SyncConfigKind =
  | 'provider'
  | 'modelAlias'
  | 'mcpServer'
  | 'searchProvider'
  | 'appPersonalization'
  | 'appPreferences'
  | 'workspacePreferences'

export interface SyncMutation {
  mutationId: string
  accountId: string
  deviceId: string
  clientSeq: number
  kind: SyncConfigKind
  entityId: string
  operation: 'upsert' | 'delete' | 'resolve'
  payload: unknown
  baseRevision: number
  workspaceId?: string
}

export interface SyncConflict {
  id: string
  kind: SyncConfigKind
  entityId: string
  localRevision: number
  remoteRevision: number
  localPayload: unknown
  remotePayload: unknown
  status: 'pending' | 'resolved'
  createdAt: number
}

export interface SyncStatus {
  enabled: boolean
  accountId: string | null
  deviceId: string
  running: boolean
  pending: number
  conflicts: number
  lastSuccessAt: number | null
  lastError: string | null
  needsInitialReview: boolean
  control?: SyncControlState
}

export interface SyncPreview {
  items: Array<{ kind: SyncConfigKind; entityId: string; revision: number; updatedAt: string; updatedByDeviceId: string }>
  count: number
}
