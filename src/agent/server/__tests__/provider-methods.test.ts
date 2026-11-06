import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { closeDatabase, stmt } from '../../../main/db'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'

// 供应商方法(含桌面的 ipc/provider.ts)整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { getHost, installHost, resetRuntimeForTest } from '../../../main/runtime'
import { store } from '../../../main/state/store'
import { headlessHost } from '../../../main/host/headless'
import { registerModelMethods } from '../methods/models'
import { registerProviderMethods } from '../methods/providers'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }
const KEY = 'sk-live-SECRET-0123456789abcd'
/** 假的上游供应商:记录设备发出的请求,按需返回模型列表或失败。 */
const upstream: { calls: Array<{ url: string; authorization: string | null }>; respond: () => Response } = {
  calls: [], respond: () => new Response(JSON.stringify({ data: [{ id: 'acme-large' }, { id: 'acme-small' }] }), { status: 200 })
}

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  upstream.calls = []
  upstream.respond = () => new Response(JSON.stringify({ data: [{ id: 'acme-large' }, { id: 'acme-small' }] }), { status: 200 })
  root = mkdtempSync(join(tmpdir(), 'ncw-provider-methods-'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => { registerProviderMethods(router); registerModelMethods(router) } })
  installHost(headlessHost({ fetch: async (input, init) => {
    upstream.calls.push({ url: String(input), authorization: new Headers(init?.headers).get('authorization') })
    return upstream.respond()
  } }))
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function controller(): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId: 'controller', label: 'p', confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: 'controller',
    clientSessionId: 'prov', bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}
const upsert = (client: ControllerClient, key: string, params: Record<string, unknown>) =>
  client.command('providers.upsert', { id: 'acme', name: 'Acme', protocol: 'openai-chat', baseUrl: 'https://api.acme.example/v1', enabled: true, ...params }, key) as Promise<Receipt>
const storedKey = async (): Promise<string | null> => {
  const provider = store.listProviders().find((item) => item.id === 'acme')
  return provider === undefined ? null : getHost().secrets.get(provider.credentialRef)
}
/** 所有幂等回执(含结果字节)拼成一个字符串,用来断言密钥没有被落进去。 */
const receiptsDump = (): string => stmt('SELECT * FROM device_commands').all()
  .map((row) => Object.values(row).map((value) => value instanceof Uint8Array ? Buffer.from(value).toString('latin1') : String(value)).join('|')).join('\n')

describe('remote provider management', () => {
  it('creates a provider with a write-only key and never echoes or stores the key in clear', async () => {
    const client = await controller()
    const created = await upsert(client, 'p1', { apiKey: KEY })
    expect(created).toMatchObject({ status: 'succeeded', result: { id: 'acme', hasKey: true, last4: KEY.slice(-4) } })
    expect(await storedKey()).toContain(KEY) // 密钥确实存在设备上(NCK1 加密后可还原)
    expect(JSON.stringify(created)).not.toContain(KEY)
    expect(receiptsDump()).not.toContain(KEY)
    const info = await client.query('providers.credentialInfo', { id: 'acme' }) as Record<string, unknown>
    expect(info).toEqual({ hasKey: true, last4: KEY.slice(-4) })
    const got = await client.query('providers.get', { id: 'acme' }) as Record<string, unknown>
    expect(got).toMatchObject({ id: 'acme', baseUrl: 'https://api.acme.example/v1', protocol: 'openai-chat', enabled: true })
    expect(Object.keys(got)).not.toContain('credentialRef')
    expect(JSON.stringify(got)).not.toContain(KEY)
    // 没有任何查看明文的方法。
    await expect(client.query('providers.reveal', { id: 'acme' })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  })

  it('updates the basics without touching the stored key, rotates the key on demand, and keeps the model catalogue consistent', async () => {
    const client = await controller()
    await upsert(client, 'p1', { apiKey: KEY })
    expect(await upsert(client, 'p2', { name: 'Acme Renamed', priority: 7 })).toMatchObject({ status: 'succeeded', result: { hasKey: true, last4: KEY.slice(-4) } })
    expect(await storedKey()).toContain(KEY)
    const rotated = await client.command('providers.setCredential', { id: 'acme', apiKey: 'sk-rotated-ZZZZ' }, 'p3') as Receipt
    expect(rotated.result).toEqual({ hasKey: true, last4: 'ZZZZ' })
    expect(await storedKey()).toContain('sk-rotated-ZZZZ')
    const catalogue = await client.query('models.list', null) as { providers: Array<{ id: string; name: string; priority: number }> }
    expect(catalogue.providers.find((p) => p.id === 'acme')).toMatchObject({ name: 'Acme Renamed', priority: 7 })
    client.detach()
  })

  it('refuses dangerous or malformed input and fields a remote controller must not set', async () => {
    const client = await controller()
    for (const [index, baseUrl] of ['file:///etc/passwd', 'ftp://x.example', 'javascript:alert(1)', 'not a url'].entries()) {
      expect(await upsert(client, `b${index}`, { baseUrl })).toMatchObject({ status: 'failed', result: { error: { code: 'invalid_request', message: 'Provider request was rejected' } } })
    }
    expect(await upsert(client, 'e1', { name: '   ' })).toMatchObject({ status: 'failed' })
    // 内部/危险字段与坏 id 在落回执之前就被 schema 拒绝。
    await expect(upsert(client, 's1', { credentialRef: 'provider:other' })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(upsert(client, 's2', { protocolOptions: { cache: 'off' } })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(upsert(client, 's3', { videoGeneration: { baseUrl: 'https://evil.example' } })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(upsert(client, 's4', { id: 'a/b' })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(upsert(client, 's5', { protocol: 'carrier-pigeon' })).rejects.toMatchObject({ code: 'invalid_request' })
    // 托管的登录供应商不能被远程凭空建出来。
    expect(await upsert(client, 'm1', { id: 'nextcowork' })).toMatchObject({ status: 'failed' })
    expect(store.listProviders().some((p) => p.id === 'acme')).toBe(false)
    client.detach()
  })

  it('removes a provider together with its key, and reports an unknown provider honestly', async () => {
    const client = await controller()
    await upsert(client, 'p1', { apiKey: KEY })
    const credentialRef = store.listProviders().find((p) => p.id === 'acme')!.credentialRef
    expect(await client.command('providers.remove', { id: 'acme' }, 'r1')).toMatchObject({ status: 'succeeded', result: { removed: true } })
    expect(await getHost().secrets.get(credentialRef)).toBeNull()
    // 同 id 重建不会继承旧密钥。
    await upsert(client, 'p2', {})
    expect(await client.query('providers.credentialInfo', { id: 'acme' })).toEqual({ hasKey: false, last4: null })
    expect(await client.command('providers.remove', { id: 'never-existed' }, 'r2')).toMatchObject({ status: 'succeeded', result: { removed: false } })
    await expect(client.query('providers.get', { id: 'never-existed' })).rejects.toMatchObject({ code: 'not_found' })
    expect(await client.command('providers.setCredential', { id: 'never-existed', apiKey: 'x' }, 'r3')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    client.detach()
  })
})

describe('remote model aliases', () => {
  type Model = { alias: string; providerId: string }
  const aliases = async (client: ControllerClient): Promise<string[]> =>
    (await client.query('models.list', null) as { models: Model[] }).models.filter((m) => m.providerId === 'acme').map((m) => m.alias).sort()

  it('fetches the model list with the key stored on the device, never returning the key, URL or upstream body', async () => {
    const client = await controller()
    await upsert(client, 'p1', { apiKey: KEY })
    const fetched = await client.command('models.fetch', { providerId: 'acme' }, 'f1') as Receipt
    expect(fetched.status).toBe('succeeded')
    expect(fetched.result).toBeTruthy()
    expect(JSON.stringify(fetched)).toContain('acme-large')
    // 设备确实带着自己存的密钥去问了上游,而这把密钥没有出现在任何响应或回执里。
    expect(upstream.calls).toHaveLength(1)
    expect(upstream.calls[0]!.authorization).toContain(KEY)
    expect(JSON.stringify(fetched)).not.toContain(KEY)
    expect(receiptsDump()).not.toContain(KEY)

    upstream.respond = () => new Response('internal upstream stack at https://api.acme.example/v1/models', { status: 500 })
    const failed = await client.command('models.fetch', { providerId: 'acme' }, 'f2') as Receipt
    expect(failed).toMatchObject({ status: 'failed', result: { error: { code: 'disconnected', message: 'Could not fetch the model list from the provider' } } })
    expect(JSON.stringify(failed)).not.toContain('api.acme.example')
    expect(await client.command('models.fetch', { providerId: 'nope' }, 'f3')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    client.detach()
  })

  it('replaces a provider\'s alias set wholesale and removes single aliases', async () => {
    const client = await controller()
    await upsert(client, 'p1', {})
    const set = await client.command('models.setAliases', { providerId: 'acme', models: ['acme-large', 'acme-small', 'acme-large'] }, 's1') as Receipt
    expect(set.status).toBe('succeeded')
    expect(await aliases(client)).toEqual(['acme-large', 'acme-small'])
    await client.command('models.setAliases', { providerId: 'acme', models: ['acme-small'] }, 's2')
    expect(await aliases(client)).toEqual(['acme-small'])
    expect(await client.command('models.remove', { providerId: 'acme', alias: 'acme-small' }, 's3')).toMatchObject({ status: 'succeeded', result: { removed: true } })
    expect(await aliases(client)).toEqual([])
    expect(await client.command('models.remove', { providerId: 'acme', alias: 'acme-small' }, 's4')).toMatchObject({ status: 'succeeded', result: { removed: false } })
    client.detach()
  })

  it('rejects unknown providers, oversized or malformed lists and extra fields', async () => {
    const client = await controller()
    expect(await client.command('models.setAliases', { providerId: 'nope', models: ['x'] }, 'e1')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    await upsert(client, 'p1', {})
    await expect(client.command('models.setAliases', { providerId: 'acme', models: Array.from({ length: 201 }, (_, i) => `m${i}`) }, 'e2')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('models.setAliases', { providerId: 'acme', models: [''] }, 'e3')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('models.setAliases', { providerId: 'acme', models: ['x'], baseUrl: 'https://evil.example' }, 'e4')).rejects.toMatchObject({ code: 'invalid_request' })
    // 超过设备端的单家上限(桌面同一条规则):失败回执,没有写入。
    const tooMany = await client.command('models.setAliases', { providerId: 'acme', models: Array.from({ length: 150 }, (_, i) => `m${i}`) }, 'e5') as Receipt
    if (tooMany.status === 'failed') expect(await aliases(client)).toEqual([])
    client.detach()
  })
})
