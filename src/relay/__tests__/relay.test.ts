import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { registerStateMethods } from '../../agent/server/methods/state'
import { startDeviceService, type DeviceService } from '../../agent/server/service'
import { connectController } from '../../controller-client/connect'
import type { ControllerClient } from '../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../controller-client/ports'
import { connectE2eClient } from '../../transport/node/e2e'
import { pairWithDevice, type PairedCredentials } from '../../transport/node/pairing-client'
import { connectDeviceToRelay, connectViaRelay, newRelayToken } from '../../transport/node/relay-client'
import { testCertificates } from '../../transport/node/__tests__/certificates'
import { listenRelay, RelayRegistry, type Relay } from '../server'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let tls: Awaited<ReturnType<typeof testCertificates>>
let root = ''
let relay: Relay | null = null
let service: DeviceService | null = null
const registry = new RelayRegistry()
const transports: SecureTransport[] = []
const cleanups: Array<() => void> = []

beforeAll(async () => { tls = await testCertificates() })
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-relay-')) })
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  for (const cleanup of cleanups.splice(0)) cleanup()
  await service?.stop().catch(() => {})
  await relay?.close()
  service = null; relay = null
  rmSync(root, { recursive: true, force: true })
})

const startRelay = (options: Partial<Parameters<typeof listenRelay>[0]> = {}) =>
  listenRelay({ host: '127.0.0.1', port: 0, tls: { certificatePem: tls.server.certificatePem, privateKeyPem: tls.server.privateKeyPem },
    authenticateDevice: registry.verify, ...options })
const relayUrl = (): string => `wss://127.0.0.1:${relay!.address().port}`

async function startService(): Promise<{ token: string; connection: ReturnType<DeviceService['attachRelay']> }> {
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => registerStateMethods(router) })
  const token = newRelayToken()
  registry.register(service.identity.deviceId, token)
  const connection = service.attachRelay(relayUrl(), { token, caPem: tls.caPem })
  await waitFor(() => relay!.devices() === 1)
  return { token, connection }
}
async function waitFor(check: () => boolean, ms = 3000): Promise<void> {
  const end = Date.now() + ms
  while (!check()) {
    if (Date.now() > end) throw new Error('condition was not met in time')
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
}
const pair = (id = 'controller'): Promise<PairedCredentials> =>
  pairWithDevice(service!.createInvitation().text, { controllerId: id, label: id, confirmDevice: () => true })

async function viaRelay(credentials: PairedCredentials, controllerId = 'controller'): Promise<ControllerClient> {
  const carrier = await connectViaRelay(relayUrl(), service!.identity.deviceId, { caPem: tls.caPem })
  transports.push(carrier)
  const secure = await connectE2eClient(carrier, { caPem: credentials.caPem, certificatePem: credentials.certificatePem,
    privateKeyPem: credentials.privateKeyPem, timeoutMs: 2000 })
  let n = 0
  const { client } = await connectController(secure, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: controllerId,
    clientSessionId: `relay-${controllerId}`, bindingEpoch: 0, nextId: () => `r${++n}`, clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

describe('self-hosted relay with end-to-end encryption', () => {
  it('lets a paired controller use the device through the relay, with no inbound device port involved', async () => {
    relay = await startRelay()
    await startService()
    const credentials = await pair()
    const client = await viaRelay(credentials)
    expect(await client.query('controllerState.read', { key: 'layout' })).toBeNull()
    const saved = await client.command('controllerState.save', { key: 'layout', expectedRevision: 0, value: { via: 'relay' } }, 'k1') as { status: string }
    expect(saved.status).toBe('succeeded')
    expect(await client.query('controllerState.read', { key: 'layout' })).toMatchObject({ revision: 1, value: { via: 'relay' } })
    expect(relay.sessions()).toBe(1)
    client.detach()
    await waitFor(() => relay!.sessions() === 0)
  })

  it('refuses an unpaired controller at the end-to-end handshake without creating gateway state', async () => {
    relay = await startRelay()
    await startService()
    const stranger = await testCertificates()
    await expect(viaRelay({ deviceId: service!.identity.deviceId, caPem: stranger.caPem, certificatePem: stranger.client.certificatePem,
      privateKeyPem: stranger.client.privateKeyPem, trustedCaPublicKeySha256: '', device: [] })).rejects.toBeInstanceOf(Error)
    await waitFor(() => relay!.sessions() === 0)
    expect(service!.connections()).toBe(0)
  })

  it('drops a live relay session when the controller is revoked, and refuses it afterwards', async () => {
    relay = await startRelay()
    await startService()
    const credentials = await pair()
    const client = await viaRelay(credentials)
    await client.query('controllerState.read', { key: 'k' })
    expect(service!.revoke('controller')).toBe(true)
    await expect(client.query('controllerState.read', { key: 'k' })).rejects.toBeInstanceOf(Error)
    await expect(viaRelay(credentials)).rejects.toBeInstanceOf(Error)
  })

  it('reports not_found while the device is offline and does not reveal whether the id exists', async () => {
    relay = await startRelay()
    await expect(connectViaRelay(relayUrl(), 'dev-unknown', { caPem: tls.caPem })).rejects.toMatchObject({ code: 'not_found' })
  })

  it('rejects a wrong registration credential and a second claimant of the same device id, without retry storms', async () => {
    relay = await startRelay()
    const { connection, token } = await startService()
    const states: string[] = []
    const impostor = connectDeviceToRelay(relayUrl(), { deviceId: service!.identity.deviceId, token: newRelayToken(), caPem: tls.caPem,
      onSession: () => {}, onState: (state) => states.push(state), minBackoffMs: 10 })
    cleanups.push(() => impostor.close())
    await waitFor(() => states.includes('rejected'))
    await new Promise((resolve) => setTimeout(resolve, 100))
    expect(states.filter((state) => state === 'rejected')).toHaveLength(1)
    expect(relay.devices()).toBe(1)
    // 持有正确凭据的第二个连接同样被拒绝(409),真设备不受影响。
    const stateTwo: string[] = []
    const second = connectDeviceToRelay(relayUrl(), { deviceId: service!.identity.deviceId, token, caPem: tls.caPem,
      onSession: () => {}, onState: (state) => stateTwo.push(state), minBackoffMs: 10 })
    cleanups.push(() => second.close())
    await waitFor(() => stateTwo.includes('rejected'))
    expect(connection.connected()).toBe(true)
  })

  it('limits concurrent sessions per device', async () => {
    relay = await startRelay({ maxSessionsPerDevice: 1 })
    await startService()
    const credentials = await pair()
    const first = await viaRelay(credentials)
    await expect(connectViaRelay(relayUrl(), service!.identity.deviceId, { caPem: tls.caPem })).rejects.toMatchObject({ code: 'busy' })
    first.detach()
  })

  it('refuses to listen in plaintext unless TLS termination is declared', async () => {
    await expect(listenRelay({ host: '127.0.0.1', port: 0, authenticateDevice: () => true })).rejects.toThrow(/plaintext/)
    const behind = await listenRelay({ host: '127.0.0.1', port: 0, behindTls: true, authenticateDevice: () => true })
    await behind.close()
  })

  it('a device reconnects by itself after the relay restarts', async () => {
    relay = await startRelay()
    const port = relay.address().port
    const token = newRelayToken()
    registry.register('dev-reconnect', token)
    const states: string[] = []
    const connection = connectDeviceToRelay(relayUrl(), { deviceId: 'dev-reconnect', token, caPem: tls.caPem,
      onSession: () => {}, onState: (state) => states.push(state), minBackoffMs: 10, maxBackoffMs: 50 })
    cleanups.push(() => connection.close())
    const connected = (): number => states.filter((state) => state === 'connected').length
    // 等设备端自己报告 connected,而不是只看中转登记了设备:中转在客户端收到 open 之前就已登记。
    await waitFor(() => connected() === 1)
    await relay.close()
    await waitFor(() => states.includes('disconnected'))
    relay = await startRelay({ port })
    await waitFor(() => connected() >= 2, 5000)
    expect(relay.devices()).toBe(1)
  })
})
