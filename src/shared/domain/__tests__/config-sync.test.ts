import { describe, expect, it } from 'vitest'
import { DEFAULT_SYNC_SELECTION, SYNC_CATEGORIES, syncDocumentSchema, syncEnvelopeSchema, syncKdfSchema } from '../config-sync'
import { MODEL_SYNC_FIELDS, PROVIDER_SYNC_FIELDS, SETTINGS_SYNC_FIELDS, SYNC_REGISTRY, portableProvider } from '../config-sync-registry'
import { IMPORTED_ALIAS_DEFAULTS, type ModelAlias, type UpstreamProvider } from '../provider'

describe('encrypted configuration contract', () => {
  it('starts with every category explicitly disabled', () => {
    expect(Object.keys(DEFAULT_SYNC_SELECTION)).toEqual([...SYNC_CATEGORIES])
    expect(Object.values(DEFAULT_SYNC_SELECTION).every((value) => value === false)).toBe(true)
  })
  it('keeps the command shell device-local rather than syncing it between systems', () => {
    expect(SETTINGS_SYNC_FIELDS.shell).toBe('device')
    expect(SYNC_REGISTRY.preferences.deviceFields).toContain('shell')
  })
  it('preserves all provider fields except device-local references', () => {
    const provider: UpstreamProvider = { id: 'custom', name: 'Provider', protocol: 'anthropic', baseUrl: 'https://example.com', credentialRef: 'private-ref', priority: 3, enabled: true, protocolOptions: { anthropic: { cacheTtl: '1h' } }, videoGeneration: { adapter: 'google-veo', baseUrl: 'https://generativelanguage.googleapis.com', region: 'us-central1' } }
    const copy = portableProvider(provider)
    expect(Object.keys(copy).sort()).toEqual(Object.keys(PROVIDER_SYNC_FIELDS).filter((key) => key !== 'credentialRef').sort())
    expect(JSON.stringify(copy)).not.toContain('private-ref')
    expect(copy.protocolOptions).toEqual(provider.protocolOptions)
    expect(copy.protocolOptions).not.toBe(provider.protocolOptions)
  })
  it('never includes read-only effective protocol in portable model fields', () => {
    expect(MODEL_SYNC_FIELDS).not.toHaveProperty('runtimeProtocol')
    expect(MODEL_SYNC_FIELDS.protocolOverride).toBe('copy')
  })
  it('keeps advanced model configuration through document serialization', () => {
    const model: ModelAlias = { alias: 'model', providerId: 'custom', upstreamModel: 'upstream', ...IMPORTED_ALIAS_DEFAULTS, protocolOverride: 'openai-responses', priority: 5, enabled: false, displayName: 'Custom', modality: 'text', thinkingConfig: { mode: 'effort', defaultEnabled: true, defaultEffort: 'high', effortMap: { high: 'deep' }, standardWire: true }, reasoningEfforts: ['low', 'high'], requestAdapter: { preset: 'custom', patches: [{ op: 'add', path: 'extra', value: { flag: true } }] }, source: { url: 'https://example.com', fetchedAt: '2026-09-15' }, catalogOverrides: ['thinkingConfig', 'capabilities.tools'], video: { profileId: 'google-veo-3.1' } }
    expect(Object.keys(model).sort()).toEqual(Object.keys(MODEL_SYNC_FIELDS).sort())
    const document = { version: 2, kind: 'providers', data: model }
    expect(syncDocumentSchema.parse(JSON.parse(JSON.stringify(document)))).toEqual(document)
  })
  it('rejects future versions, plaintext envelopes and unbounded KDF work', () => {
    expect(syncDocumentSchema.safeParse({ version: 3, kind: 'providers', data: {} }).success).toBe(false)
    expect(syncEnvelopeSchema.safeParse({ version: 2, kind: 'providers', payload: { apiKey: 'secret' } }).success).toBe(false)
    expect(syncKdfSchema.safeParse({ algorithm: 'scrypt', salt: Buffer.alloc(32).toString('base64'), n: 2 ** 30, r: 8, p: 1 }).success).toBe(false)
  })
})
