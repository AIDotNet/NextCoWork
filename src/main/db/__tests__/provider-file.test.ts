import { mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync, existsSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { providerCredentialRef, type ModelAlias, type UpstreamProvider } from '../../../shared/domain/provider'
import { PROVIDER_FILE_NAME, ProviderFileStore } from '../provider-file'

let dir: string
let path: string
let store: ProviderFileStore

const provider = (over: Partial<UpstreamProvider> = {}): UpstreamProvider => ({
  id: 'gw',
  name: 'Gateway',
  protocol: 'openai-chat',
  baseUrl: 'http://127.0.0.1:8080/v1',
  credentialRef: providerCredentialRef('gw'),
  priority: 50,
  enabled: true,
  ...over
})

const alias = (over: Partial<ModelAlias> = {}): ModelAlias => ({
  alias: 'gpt-4o',
  providerId: 'gw',
  upstreamModel: 'gpt-4o',
  capabilities: { tools: true, vision: true, thinking: false, caching: true },
  contextWindow: 128_000,
  maxOutputTokens: 4096,
  ...over
})

const read = (): { providers: Record<string, Record<string, unknown>> } =>
  JSON.parse(readFileSync(path, 'utf8')) as { providers: Record<string, Record<string, unknown>> }

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'provider-file-'))
  path = join(dir, PROVIDER_FILE_NAME)
  store = new ProviderFileStore(path)
})

afterEach(() => {
  store.stopWatching()
  rmSync(dir, { recursive: true, force: true })
})

describe('ProviderFileStore: read', () => {
  it('treats a missing file as empty and does not create it', () => {
    expect(store.load()).toEqual({ ok: true })
    expect(store.listProviders()).toEqual([])
    expect(existsSync(path)).toBe(false)
  })

  it('fills defaults for a minimal hand-written entry', () => {
    writeFileSync(path, JSON.stringify({ providers: { astra: { baseUrl: 'http://x/v1', protocol: 'openai-chat', apiKey: ' sk-1 ', models: { 'gpt-x': {} } } } }))
    store.load()
    const [p] = store.listProviders()
    expect(p).toMatchObject({ id: 'astra', name: 'astra', priority: 50, enabled: true, credentialRef: 'provider:astra' })
    expect(store.getCredential('astra')).toBe('sk-1')
    const [a] = store.listAliases()
    expect(a).toMatchObject({ alias: 'gpt-x', providerId: 'astra', upstreamModel: 'gpt-x', contextWindow: 200_000, maxOutputTokens: 8192 })
  })

  it('orders by priority then id', () => {
    writeFileSync(path, JSON.stringify({ providers: {
      b: { baseUrl: 'u', protocol: 'anthropic', priority: 10 },
      a: { baseUrl: 'u', protocol: 'anthropic', priority: 10 },
      c: { baseUrl: 'u', protocol: 'anthropic', priority: 1 }
    } }))
    store.load()
    expect(store.listProviders().map((p) => p.id)).toEqual(['c', 'a', 'b'])
  })

  it('skips invalid entries with a diagnostic instead of failing the file', () => {
    writeFileSync(path, JSON.stringify({ providers: {
      ok: { baseUrl: 'u', protocol: 'anthropic' },
      nourl: { protocol: 'anthropic' },
      badproto: { baseUrl: 'u', protocol: 'gemini' },
      nextcowork: { baseUrl: 'u', protocol: 'anthropic' },
      'has space': { baseUrl: 'u', protocol: 'anthropic' }
    } }))
    expect(store.load().ok).toBe(true)
    expect(store.listProviders().map((p) => p.id)).toEqual(['ok'])
    expect(store.problems.map((d) => d.providerId).sort()).toEqual(['badproto', 'has space', 'nextcowork', 'nourl'])
  })

  it('reads oauth credentials from the credential object', () => {
    const cred = { kind: 'oauth', issuer: 'chatgpt', accessToken: 'a', refreshToken: 'r', expiresAt: null, accountId: 'acc' }
    writeFileSync(path, JSON.stringify({ providers: { o: { baseUrl: 'u', protocol: 'openai-responses', credential: cred } } }))
    store.load()
    expect(JSON.parse(store.getCredential('o') ?? '{}')).toMatchObject({ kind: 'oauth', accessToken: 'a' })
  })
})

describe('ProviderFileStore: contract with the Astra gateway adapter', () => {
  // Byte-for-byte what NextCoWorkClientAdapter (Astra.Clients) writes: compact JSON, no priority/enabled.
  const astraEntry =
    '{"name":"Astra","protocol":"openai-chat","baseUrl":"http://127.0.0.1:17321/v1","apiKey":"astra-nextcowork-1",' +
    '"models":{"gpt-5.1":{"upstreamModel":"gpt-5.1","displayName":"GPT 5.1","contextWindow":400000,"maxOutputTokens":32000,' +
    '"capabilities":{"vision":true,"visionInput":true}},"glm-5":{"upstreamModel":"glm-5"}}}'

  it('reads the entry and fills NextCoWork defaults', () => {
    writeFileSync(path, `{"providers":{"astra":${astraEntry}}}`)
    store.load()
    expect(store.problems).toEqual([])
    expect(store.listProviders()[0]).toMatchObject({
      id: 'astra', name: 'Astra', protocol: 'openai-chat', baseUrl: 'http://127.0.0.1:17321/v1', priority: 50, enabled: true
    })
    expect(store.getCredential('astra')).toBe('astra-nextcowork-1')
    const aliases = store.listAliases()
    expect(aliases.map((a) => a.alias)).toEqual(['glm-5', 'gpt-5.1'])
    expect(aliases.find((a) => a.alias === 'gpt-5.1')).toMatchObject({
      upstreamModel: 'gpt-5.1', displayName: 'GPT 5.1', contextWindow: 400_000, maxOutputTokens: 32_000,
      capabilities: { vision: true, visionInput: true, tools: true }
    })
  })

  it('saving the unchanged provider, alias and key back does not rewrite the entry Astra wrote', () => {
    const text = `{"providers":{"astra":${astraEntry}}}`
    writeFileSync(path, text)
    store.load()
    // What the app does when the user merely opens and saves: same effective content.
    for (const p of store.listProviders()) store.putProvider(p)
    for (const a of store.listAliases()) store.putAlias(a)
    store.setCredential('astra', 'astra-nextcowork-1')
    expect(readFileSync(path, 'utf8')).toBe(text)
  })

  it('disabling in Astra (removing providers.astra) removes the provider, its models and its key live', () => {
    writeFileSync(path, `{"providers":{"mine":{"baseUrl":"u","protocol":"anthropic"},"astra":${astraEntry}}}`)
    store.load()
    writeFileSync(path, '{"providers":{"mine":{"baseUrl":"u","protocol":"anthropic"}}}')
    store.load()
    expect(store.listProviders().map((p) => p.id)).toEqual(['mine'])
    expect(store.listAliases()).toEqual([])
    expect(store.getCredential('astra')).toBeNull()
  })
})

describe('ProviderFileStore: write', () => {
  it('writes provider, alias and key into one entry with 0600 permissions', () => {
    store.load()
    store.putProvider(provider())
    store.setCredential('gw', 'sk-secret')
    store.putAlias(alias())
    const entry = read().providers['gw']!
    expect(entry).toMatchObject({ name: 'Gateway', protocol: 'openai-chat', apiKey: 'sk-secret', priority: 50, enabled: true })
    expect(entry['credentialRef']).toBeUndefined()
    expect(Object.keys(entry['models'] as object)).toEqual(['gpt-4o'])
    if (process.platform !== 'win32') expect(statSync(path).mode & 0o777).toBe(0o600)
  })

  it('stashes a key that arrives before its provider', () => {
    store.load()
    store.setCredential('gw', 'sk-early')
    expect(existsSync(path)).toBe(false)
    store.putProvider(provider())
    expect(read().providers['gw']!['apiKey']).toBe('sk-early')
  })

  it('does not touch the file when the effective content is unchanged', () => {
    writeFileSync(path, '{"providers":{"gw":{"baseUrl":"http://127.0.0.1:8080/v1","protocol":"openai-chat","name":"Gateway","x-custom":1}}}')
    store.load()
    const before = readFileSync(path, 'utf8')
    store.putProvider(provider())
    expect(readFileSync(path, 'utf8')).toBe(before)
  })

  it('keeps other entries and unknown fields byte-stable when one entry changes', () => {
    writeFileSync(path, JSON.stringify({
      topLevelNote: 'keep me',
      providers: {
        astra: { baseUrl: 'http://a/v1', protocol: 'openai-chat', apiKey: 'k', models: { m: {} }, 'x-astra': true },
        gw: { baseUrl: 'http://g/v1', protocol: 'openai-chat' }
      }
    }))
    store.load()
    store.putProvider(provider({ name: 'Renamed' }))
    const doc = read() as unknown as Record<string, unknown> & { providers: Record<string, unknown> }
    expect(doc['topLevelNote']).toBe('keep me')
    expect(doc.providers['astra']).toEqual({ baseUrl: 'http://a/v1', protocol: 'openai-chat', apiKey: 'k', models: { m: {} }, 'x-astra': true })
    expect((doc.providers['gw'] as Record<string, unknown>)['name']).toBe('Renamed')
  })

  it('removes a provider together with its models and key', () => {
    store.load()
    store.putProvider(provider())
    store.setCredential('gw', 'k')
    store.putAlias(alias())
    store.removeProvider('gw')
    expect(read().providers).toEqual({})
    expect(store.listAliases()).toEqual([])
    expect(store.getCredential('gw')).toBeNull()
  })

  it('clears optional fields that were removed', () => {
    store.load()
    store.putProvider(provider({ protocolOptions: { anthropic: { cacheTtl: '1h' } } }))
    expect(read().providers['gw']!['protocolOptions']).toBeDefined()
    store.putProvider(provider())
    expect(read().providers['gw']!['protocolOptions']).toBeUndefined()
  })

  it('rejects an alias for an unknown provider and the managed provider id', () => {
    store.load()
    expect(() => store.putAlias(alias({ providerId: 'nope' }))).toThrow('没有这个供应商')
    expect(() => store.putProvider(provider({ id: 'nextcowork' }))).toThrow('托管')
  })

  it('refuses to overwrite a file that does not parse, and recovers once it is fixed', () => {
    store.load()
    store.putProvider(provider())
    writeFileSync(path, '{ "providers": { broken')
    expect(store.load().ok).toBe(false)
    expect(store.listProviders().map((p) => p.id)).toEqual(['gw']) // last good copy stays live
    expect(() => store.putProvider(provider({ name: 'X' }))).toThrow('解析失败')
    expect(readFileSync(path, 'utf8')).toBe('{ "providers": { broken')
    writeFileSync(path, JSON.stringify({ providers: { gw: { baseUrl: 'u', protocol: 'anthropic' } } }))
    expect(store.load().ok).toBe(true)
    expect(store.error).toBeUndefined()
  })

  it('picks up an external edit made between the last load and a write', () => {
    store.load()
    store.putProvider(provider())
    const doc = read()
    doc.providers['astra'] = { baseUrl: 'http://a/v1', protocol: 'openai-chat' }
    writeFileSync(path, JSON.stringify(doc))
    store.putProvider(provider({ name: 'Mine' }))
    expect(Object.keys(read().providers).sort()).toEqual(['astra', 'gw'])
  })
})

describe('ProviderFileStore: importLegacy', () => {
  it('creates the file from legacy data', () => {
    store.load()
    const data = { providers: [provider()], aliases: [alias()], credentials: { gw: 'sk-legacy' } }
    expect(store.importLegacy(data)).toBe(1)
    expect(read().providers['gw']).toMatchObject({ apiKey: 'sk-legacy' })
    expect(store.listAliases()).toHaveLength(1)
  })

  it('only adds providers the file does not have and never touches existing entries', () => {
    const existing = '{"providers":{"gw":{"baseUrl":"http://mine/v1","protocol":"anthropic","apiKey":"mine"}}}'
    writeFileSync(path, existing)
    store.load()
    const data = {
      providers: [provider(), provider({ id: 'other', name: 'Other' })],
      aliases: [],
      credentials: { gw: 'sk-legacy', other: 'sk-other' }
    }
    expect(store.importLegacy(data)).toBe(1)
    const doc = read()
    expect(doc.providers['gw']).toEqual({ baseUrl: 'http://mine/v1', protocol: 'anthropic', apiKey: 'mine' })
    expect(doc.providers['other']).toMatchObject({ apiKey: 'sk-other' })
  })

  it('is a no-op (and writes nothing) when everything is already present', () => {
    writeFileSync(path, '{"providers":{"gw":{"baseUrl":"u","protocol":"anthropic"}}}')
    store.load()
    const before = readFileSync(path, 'utf8')
    expect(store.importLegacy({ providers: [provider()], aliases: [], credentials: {} })).toBe(0)
    expect(readFileSync(path, 'utf8')).toBe(before)
  })

  it('refuses to import into a file that does not parse', () => {
    writeFileSync(path, '{ broken')
    store.load()
    expect(() => store.importLegacy({ providers: [provider()], aliases: [], credentials: {} })).toThrow('解析失败')
  })
})

describe('ProviderFileStore: watching', () => {
  it('keeps watching atomic replacements through a directory alias', async () => {
    const actual = join(dir, 'actual')
    const linked = join(dir, 'linked')
    mkdirSync(actual)
    symlinkSync(actual, linked, 'junction')
    path = join(linked, PROVIDER_FILE_NAME)
    store = new ProviderFileStore(path)
    store.load()
    store.putProvider(provider())
    const seen: string[][] = []
    store.onExternalChange((c) => seen.push(c.providers.map((p) => p.id)))
    store.startWatching()

    const doc = read()
    doc.providers['astra'] = { baseUrl: 'http://a/v1', protocol: 'openai-chat' }
    const replacement = join(actual, 'providers.tmp')
    writeFileSync(replacement, JSON.stringify(doc))
    renameSync(replacement, join(actual, PROVIDER_FILE_NAME))
    await vi.waitFor(() => expect(seen).toEqual([['astra', 'gw']]), { timeout: 10_000 })
  })

  it('notifies on an external edit but not on its own writes', async () => {
    vi.useRealTimers()
    store.load()
    store.putProvider(provider())
    const seen: string[][] = []
    store.onExternalChange((c) => seen.push(c.providers.map((p) => p.id)))
    store.startWatching()

    store.putProvider(provider({ name: 'Own write' }))
    await new Promise((r) => setTimeout(r, 500))
    expect(seen).toEqual([])

    const doc = read()
    doc.providers['astra'] = { baseUrl: 'http://a/v1', protocol: 'openai-chat' }
    writeFileSync(path, JSON.stringify(doc))
    await vi.waitFor(() => expect(seen).toEqual([['astra', 'gw']]), { timeout: 10_000 })
  })

  it('keeps the last good state when an external edit breaks the file', async () => {
    store.load()
    store.putProvider(provider())
    const seen: unknown[] = []
    store.onExternalChange((c) => seen.push(c))
    store.startWatching()
    writeFileSync(path, '{ nope')
    await vi.waitFor(() => expect(store.error).toBeDefined(), { timeout: 10_000 })
    expect(seen).toEqual([])
    expect(store.listProviders().map((p) => p.id)).toEqual(['gw'])
  })
})
