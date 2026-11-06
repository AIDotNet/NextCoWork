import { randomBytes } from 'node:crypto'
import { afterEach, describe, expect, it } from 'vitest'
import { closeDatabase, db } from '..'
import { acknowledgeSyncOutgoing, finishSyncEvent, mutateSyncState, readSyncState, sealSyncOutgoing, stageSyncEvent, syncStateWithVault, writeSyncState } from '../config-sync-state'
import { encryptSyncDocument, syncDigest, wrapSyncKey } from '../../config-sync-crypto'

afterEach(() => closeDatabase())
describe('account-scoped encrypted synchronization state', () => {
  it('is closed until explicitly confirmed and never reuses another account queue', async () => {
    const key = randomBytes(32)
    const vault = await wrapSyncKey('a', 'secure password test', key)
    writeSyncState(syncStateWithVault(readSyncState('a'), vault))
    const doc = { version: 2 as const, kind: 'providers' as const, data: { apiKey: 'plain-key-never-in-outbox' } }
    const event = encryptSyncDocument(key, vault, doc, 0)
    const fingerprint = syncDigest(key, doc)
    expect(() => sealSyncOutgoing('a', 'providers', event, fingerprint)).toThrow('configSync.locked')
    mutateSyncState('a', (state) => { state.confirmed = true; state.selection.providers = true })
    sealSyncOutgoing('a', 'providers', event, fingerprint)
    expect(readSyncState('a').categories.providers.outgoing).toEqual(event)
    expect(readSyncState('b').categories.providers.outgoing).toBeNull()
    expect(JSON.stringify(db().prepare("SELECT json FROM kv WHERE key LIKE 'config-sync.v2.%'").all())).not.toContain('plain-key-never-in-outbox')
    mutateSyncState('a', (state) => { state.selection.providers = false })
    expect(() => stageSyncEvent('a', { ...event, deviceId: 'other', cursor: 1, revision: 1 })).toThrow('configSync.locked')
  })
  it('advances only after an accepted mutation and a completely applied inbox', async () => {
    const key = randomBytes(32)
    const vault = await wrapSyncKey('a', 'secure password test', key)
    writeSyncState(syncStateWithVault(readSyncState('a'), vault))
    mutateSyncState('a', (state) => { state.confirmed = true; state.selection.providers = true })
    const doc = { version: 2 as const, kind: 'providers' as const, data: { models: [] } }
    const outgoing = encryptSyncDocument(key, vault, doc, 0)
    const fingerprint = syncDigest(key, doc)
    sealSyncOutgoing('a', 'providers', outgoing, fingerprint)
    expect(() => acknowledgeSyncOutgoing('a', 'providers', 'wrong', 1)).toThrow()
    expect(readSyncState('a').categories.providers.outgoing).toEqual(outgoing)
    acknowledgeSyncOutgoing('a', 'providers', outgoing.mutationId, 1)
    expect(readSyncState('a').categories.providers.outgoing).toBeNull()
    const incoming = { ...encryptSyncDocument(key, vault, doc, 1), deviceId: 'other', cursor: 7, revision: 2 }
    stageSyncEvent('a', incoming)
    expect(readSyncState('a').categories.providers.cursor).toBe(0)
    expect(readSyncState('a').categories.providers.pendingApply).toEqual(incoming)
    finishSyncEvent('a', 'providers', incoming.mutationId, fingerprint)
    expect(readSyncState('a').categories.providers.cursor).toBe(7)
    expect(readSyncState('a').categories.providers.revision).toBe(2)
  })
  it('invalidates stale queues on vault/key changes but not a password rewrap', async () => {
    const key = randomBytes(32)
    const vault = await wrapSyncKey('a', 'secure password test', key)
    writeSyncState(syncStateWithVault(readSyncState('a'), vault))
    mutateSyncState('a', (state) => { state.confirmed = true; state.selection.providers = true; state.categories.providers.cursor = 100 })
    expect(syncStateWithVault(readSyncState('a'), { ...vault, wrappingVersion: 2 }).categories.providers.cursor).toBe(100)
    const rotated = syncStateWithVault(readSyncState('a'), { ...vault, keyVersion: 2, wrappingVersion: 2 })
    expect(rotated.categories.providers.cursor).toBe(0)
    expect(rotated.confirmed).toBe(false)
    expect(() => syncStateWithVault(readSyncState('b'), vault)).toThrow('configSync.accountChanged')
  })
  it('reads a state written before the usage category existed and opts provider-syncing accounts in', async () => {
    const vault = await wrapSyncKey('a', 'secure password test', randomBytes(32))
    const state = syncStateWithVault(readSyncState('a'), vault)
    state.confirmed = true
    state.selection.providers = true
    state.categories.providers.revision = 4
    // 模拟旧版本落盘的形状:selection / categories 里都没有 usage 键
    const { usage: _selection, ...legacySelection } = state.selection
    const { usage: _category, ...legacyCategories } = state.categories
    db().prepare('INSERT OR REPLACE INTO kv (key, json) VALUES (?, ?)').run(
      `config-sync.v2.account.${Buffer.from('a').toString('base64url')}`,
      JSON.stringify({ ...state, selection: legacySelection, categories: legacyCategories })
    )
    const read = readSyncState('a')
    expect(read.selection.usage).toBe(true)
    expect(read.categories.usage.revision).toBe(0)
    expect(read.categories.providers.revision).toBe(4)
  })
})
