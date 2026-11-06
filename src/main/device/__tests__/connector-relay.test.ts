import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { closeDatabase } from '../../db'

// 经中转连接设备(整条链不得加载 Electron)。
vi.mock('electron', () => { throw new Error('Device relay path imported Electron') })
import { resetRuntimeForTest } from '../../runtime'
import { startDeviceService, type DeviceService } from '../../../agent/server/service'
import { listenRelay, RelayRegistry, type Relay } from '../../../relay/server'
import { DeviceProtocolError } from '../../../shared/device/errors'
import { testCertificates } from '../../../transport/node/__tests__/certificates'
import { newRelayToken } from '../../../transport/node/relay-client'
import { NodeDeviceConnector } from '../connector'
import { DeviceManager } from '../manager'
import { DeviceRegistry, type SecretPort } from '../registry'

let tls: Awaited<ReturnType<typeof testCertificates>>
let root = ''
let relay: Relay | null = null
let service: DeviceService | null = null
let manager: DeviceManager | null = null
const relayRegistry = new RelayRegistry()
const secrets = new Map<string, string>()
const port: SecretPort = {
  get: async (ref) => secrets.get(ref) ?? null,
  set: async (ref, value) => { secrets.set(ref, value) },
  remove: async (ref) => { secrets.delete(ref) }
}
const relayUrl = (): string => `wss://127.0.0.1:${relay!.address().port}`
/** 直连一律失败,强制走中转;配对本身用真实的配对端点。 */
const noDirect = async (): Promise<never> => { throw new DeviceProtocolError('disconnected', 'direct path is down') }

beforeAll(async () => { tls = await testCertificates() })
beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  secrets.clear()
  root = mkdtempSync(join(tmpdir(), 'ncw-device-relay-'))
  relay = await listenRelay({ host: '127.0.0.1', port: 0, tls: { certificatePem: tls.server.certificatePem, privateKeyPem: tls.server.privateKeyPem },
    authenticateDevice: relayRegistry.verify })
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [] })
  const token = newRelayToken()
  relayRegistry.register(service.identity.deviceId, token)
  service.attachRelay(relayUrl(), { token, caPem: tls.caPem })
  await vi.waitFor(() => expect(relay!.devices()).toBe(1), { timeout: 5000 })
  const registry = new DeviceRegistry(join(root, 'controller'), port)
  manager = new DeviceManager(registry, new NodeDeviceConnector(registry, { dial: noDirect }))
})
afterEach(async () => {
  manager?.close()
  manager = null
  await service?.stop().catch(() => {})
  await relay?.close()
  service = null; relay = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})
afterAll(() => { relayRegistry.revoke('') })

async function pair(): Promise<string> {
  const text = service!.createInvitation().text
  const summary = await manager!.pair(text, { label: 'Home', confirmedFingerprint: manager!.inspectInvitation(text).fingerprint })
  return summary.id
}

describe('connecting through a self-hosted relay', () => {
  it('falls back to the relay when every direct endpoint fails, with end-to-end authentication, and runs real requests', async () => {
    const id = await pair()
    manager!.setRelay(id, { url: relayUrl(), caPem: tls.caPem })
    await manager!.select(id)
    expect(manager!.current()).toMatchObject({ deviceId: id, state: 'online' })
    expect(relay!.sessions()).toBe(1)
    await expect(manager!.run((client) => client.query('nope.nothing', null))).rejects.toMatchObject({ code: 'not_found' })
    expect(manager!.list()[0]).toMatchObject({ relay: relayUrl() })
  }, 30_000)

  it('does not use the relay when none is configured, and reports the direct failure', async () => {
    const id = await pair()
    await expect(manager!.select(id)).rejects.toMatchObject({ code: 'disconnected' })
    expect(manager!.current().state).toBe('offline')
    expect(relay!.sessions()).toBe(0)
  }, 30_000)

  it('prefers the direct path: the relay stays unused while direct works', async () => {
    const registry = new DeviceRegistry(join(root, 'controller-direct'), port)
    const direct = new DeviceManager(registry, new NodeDeviceConnector(registry))
    try {
      const text = service!.createInvitation().text
      const paired = await direct.pair(text, { label: 'Home', confirmedFingerprint: direct.inspectInvitation(text).fingerprint })
      direct.setRelay(paired.id, { url: relayUrl(), caPem: tls.caPem })
      await direct.select(paired.id)
      expect(direct.current().state).toBe('online')
      expect(relay!.sessions()).toBe(0)
    } finally { direct.close() }
  }, 30_000)

  it('refuses a relay whose certificate it does not trust, and keeps working through a trusted one', async () => {
    const id = await pair()
    manager!.setRelay(id, { url: relayUrl() }) // 自签证书、没给信任根:TLS 校验失败
    await expect(manager!.select(id)).rejects.toBeTruthy()
    manager!.setRelay(id, { url: relayUrl(), caPem: (await testCertificates()).caPem }) // 另一把根:同样不信任
    await expect(manager!.select(id)).rejects.toBeTruthy()
    relayRegistry.revoke(service!.identity.deviceId) // 中转已不认这台设备(不影响设备自己的在线连接)
    manager!.setRelay(id, { url: relayUrl(), caPem: tls.caPem })
    // 设备连接仍在中转上,所以这里仍能连上 —— 注册凭据只管设备出站,不管控制端会话。
    await manager!.select(id)
    expect(manager!.current().state).toBe('online')
  }, 40_000)

  it('cannot connect through the relay after the device revokes this controller', async () => {
    const id = await pair()
    manager!.setRelay(id, { url: relayUrl(), caPem: tls.caPem })
    const registry = new DeviceRegistry(join(root, 'controller'), port)
    expect(service!.revoke(registry.controllerId())).toBe(true)
    await expect(manager!.select(id)).rejects.toBeTruthy()
    expect(manager!.current().state).toBe('offline')
  }, 30_000)
})

describe('relay settings in the device list', () => {
  it('accepts only credential-free wss URLs, can be cleared, and survives restarts and re-pairing', async () => {
    const id = await pair()
    for (const url of ['http://relay.example', 'ws://relay.example', 'wss://user:pw@relay.example', 'wss://relay.example/?token=x', 'wss://relay.example/#frag', 'not a url', '']) {
      expect(() => manager!.setRelay(id, { url }), url).toThrow()
    }
    expect(manager!.list()[0]!.relay).toBeNull()
    manager!.setRelay(id, { url: 'wss://relay.example:8443' })
    expect(manager!.list()[0]!.relay).toBe('wss://relay.example:8443')

    const file = readFileSync(join(root, 'controller', 'devices.json'), 'utf8')
    expect(file).toContain('relay.example')
    expect(file).not.toContain('PRIVATE KEY')

    // 重启:同一份清单读回来。
    const reopened = new DeviceRegistry(join(root, 'controller'), port)
    expect(reopened.get(id)?.relay?.url).toBe('wss://relay.example:8443')
    // 重新配对(同一台设备再次写入清单)不丢中转。
    await reopened.add((await reopened.credentials(id))!, 'Renamed')
    expect(reopened.get(id)).toMatchObject({ label: 'Renamed', relay: { url: 'wss://relay.example:8443' } })
    expect(new DeviceRegistry(join(root, 'controller'), port).get(id)?.relay?.url).toBe('wss://relay.example:8443')
    manager!.setRelay(id, null)
    expect(manager!.list()[0]!.relay).toBeNull()
    expect(() => manager!.setRelay('unknown-device', null)).toThrow(/not paired/)
  }, 30_000)
})
