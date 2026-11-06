import { z } from 'zod'
import { ConfigSyncError, DEFAULT_SYNC_SELECTION, SYNC_CATEGORIES, SYNC_ERRORS, syncEnvelopeSchema, syncEventSchema, syncSelectionSchema, syncVaultSchema, type SyncCategory, type SyncEnvelope, type SyncEvent, type SyncVault } from '../../shared/domain/config-sync'
import { getKv, setKv } from './repo'

const counter = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER)
const digest = z.string().regex(/^[a-f0-9]{64}$/).nullable()
const categorySchema = z.object({
  revision: counter, cursor: counter, digest,
  outgoing: syncEnvelopeSchema.nullable(), outgoingDigest: digest,
  conflict: syncEventSchema.nullable(), pendingApply: syncEventSchema.nullable(),
  reviewed: z.boolean(), blockedDigest: digest
}).strict()
const localStateSchema = z.object({
  version: z.literal(2), accountId: z.string().min(1).max(128), vault: syncVaultSchema.nullable(),
  selection: syncSelectionSchema, confirmed: z.boolean(), remembered: z.boolean(),
  migrationRequired: z.boolean(), legacyExists: z.boolean(),
  categories: z.object({ providers: categorySchema, preferences: categorySchema, connections: categorySchema, extensions: categorySchema, workspaces: categorySchema, automation: categorySchema, usage: categorySchema }).strict(),
  lastSuccessAt: counter.nullable(), errorCode: z.enum(SYNC_ERRORS).nullable(),
  bindings: z.array(z.object({ kind: z.enum(['workspace', 'extension', 'automation', 'connection']), id: z.string().max(512), name: z.string().max(512) }).strict()).max(5000)
}).strict()
export type SyncLocalState = z.infer<typeof localStateSchema>
export type SyncCategoryState = z.infer<typeof categorySchema>
const stateKey = (account: string): string => `config-sync.v2.account.${Buffer.from(account).toString('base64url')}`
export const emptySyncCategory = (): SyncCategoryState => ({ revision: 0, cursor: 0, digest: null, outgoing: null, outgoingDigest: null, conflict: null, pendingApply: null, reviewed: false, blockedDigest: null })
export function newSyncState(accountId: string): SyncLocalState {
  return {
    version: 2, accountId, vault: null, selection: { ...DEFAULT_SYNC_SELECTION }, confirmed: false,
    remembered: false, migrationRequired: false, legacyExists: false,
    categories: Object.fromEntries(SYNC_CATEGORIES.map((kind) => [kind, emptySyncCategory()])) as Record<SyncCategory, SyncCategoryState>,
    lastSuccessAt: null, errorCode: null, bindings: []
  }
}
/**
 * 需求:`usage` category 是后加的。老版本写下的状态里 selection / categories 都没有这个键,
 * 而 schema 是 strict 的 —— 不补的话 readSyncState 直接 invalidData,整块云同步进错误态。
 * 补的值:已经在同步供应商的账户默认一并合并使用统计(数据页可关),同步状态从零开始。
 */
function withUsageCategory(raw: unknown): unknown {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return raw
  const state = raw as Record<string, unknown>
  const selection = state['selection']
  const categories = state['categories']
  const next: Record<string, unknown> = { ...state }
  if (typeof selection === 'object' && selection !== null && !('usage' in selection)) {
    next['selection'] = { ...selection, usage: (selection as Record<string, unknown>)['providers'] === true }
  }
  if (typeof categories === 'object' && categories !== null && !('usage' in categories)) {
    next['categories'] = { ...categories, usage: emptySyncCategory() }
  }
  return next
}
export function readSyncState(accountId: string): SyncLocalState {
  const raw = getKv<unknown>(stateKey(accountId), null)
  if (raw === null) return newSyncState(accountId)
  const parsed = localStateSchema.safeParse(withUsageCategory(raw))
  if (!parsed.success || parsed.data.accountId !== accountId || (parsed.data.vault !== null && parsed.data.vault.accountId !== accountId)) throw new ConfigSyncError('invalidData')
  return parsed.data
}
export function writeSyncState(state: SyncLocalState): void {
  const parsed = localStateSchema.safeParse(state)
  if (!parsed.success || (state.vault !== null && state.vault.accountId !== state.accountId)) throw new ConfigSyncError('invalidData')
  // Only ciphertext, keyed fingerprints and control metadata belong in this table.
  setKv(stateKey(state.accountId), parsed.data)
}
export function mutateSyncState(accountId: string, mutate: (state: SyncLocalState) => void): SyncLocalState {
  const next = readSyncState(accountId)
  mutate(next)
  writeSyncState(next)
  return next
}
export function syncStateWithVault(state: SyncLocalState, vault: SyncVault): SyncLocalState {
  if (state.accountId !== vault.accountId) throw new ConfigSyncError('accountChanged')
  if (state.vault?.vaultId !== vault.vaultId || state.vault.keyVersion !== vault.keyVersion) {
    // A new space/key has different HMAC identities. Old mutations must never be replayed there.
    state.categories = newSyncState(state.accountId).categories
    state.confirmed = false
    state.bindings = []
  }
  state.vault = vault
  return state
}
export function sealSyncOutgoing(accountId: string, kind: SyncCategory, envelope: SyncEnvelope, fingerprint: string): void {
  mutateSyncState(accountId, (state) => {
    if (!state.confirmed || !state.selection[kind]) throw new ConfigSyncError('locked')
    if (envelope.kind !== kind || envelope.vaultId !== state.vault?.vaultId || envelope.keyVersion !== state.vault.keyVersion) throw new ConfigSyncError('invalidData')
    const category = state.categories[kind]
    if (category.outgoing !== null) throw new ConfigSyncError('busy')
    category.outgoing = envelope
    category.outgoingDigest = fingerprint
  })
}
export function acknowledgeSyncOutgoing(accountId: string, kind: SyncCategory, mutationId: string, revision: number): void {
  mutateSyncState(accountId, (state) => {
    const category = state.categories[kind]
    if (category.outgoing?.mutationId !== mutationId || revision <= category.revision) throw new ConfigSyncError('invalidData')
    category.revision = revision
    category.digest = category.outgoingDigest
    category.outgoing = null
    category.outgoingDigest = null
    category.conflict = null
  })
}
export function stageSyncEvent(accountId: string, event: SyncEvent): void {
  mutateSyncState(accountId, (state) => {
    if (!state.confirmed || !state.selection[event.kind]) throw new ConfigSyncError('locked')
    if (event.vaultId !== state.vault?.vaultId || event.keyVersion !== state.vault.keyVersion) throw new ConfigSyncError('invalidData')
    const category = state.categories[event.kind]
    if (event.cursor < category.cursor || event.revision < category.revision) throw new ConfigSyncError('invalidData')
    category.pendingApply = event
  })
}
export function finishSyncEvent(accountId: string, kind: SyncCategory, mutationId: string, fingerprint: string): void {
  mutateSyncState(accountId, (state) => {
    const category = state.categories[kind]
    const event = category.pendingApply
    if (event?.mutationId !== mutationId) throw new ConfigSyncError('invalidData')
    category.revision = event.revision
    category.cursor = event.cursor
    category.digest = fingerprint
    category.pendingApply = null
    category.conflict = null
  })
}
