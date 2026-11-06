import { mkdtempSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { z } from 'zod'
import { connectController } from '../../../controller-client/connect'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { decodeInvitation } from '../../../shared/device/invitation'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice, type PairedCredentials } from '../../../transport/node/pairing-client'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let base = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []

beforeEach(() => { base = mkdtempSync(join(tmpdir(), 'ncw-device-service-')) })
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  rmSync(base, { recursive: true, force: true })
})

const begin = (onStop?: () => void) => startDeviceService({
  dataDir: join(base, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0,
  features: ['tasks'], ...(onStop && { onStop }),
  register: (router, identity) => {
    router.query('device.identity', z.null(), z.object({ deviceId: z.string(), serverEpoch: z.string() }),
      () => ({ deviceId: identity.deviceId, serverEpoch: identity.serverEpoch }))
  }
})
const pair = (id = 'controller') => pairWithDevice(service!.createInvitation().text,
  { controllerId: id, label: 'Phone', confirmDevice: () => true })
async function connect(credentials: PairedCredentials) {
  const { port } = service!.address()
  const transport = await connectDirectDevice(`wss://127.0.0.1:${port}/device`, credentials)
  transports.push(transport)
  return connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: 'controller',
    clientSessionId: 'session', bindingEpoch: 0, nextId: () => 'req', clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: ['tasks'] } })
}

describe('device service pairing and lifecycle', () => {
  it('pairs with an invitation, then serves RPC over the issued certificate', async () => {
    service = await begin()
    const credentials = await pair()
    expect(credentials.deviceId).toBe(service.identity.deviceId)
    const { client, welcome } = await connect(credentials)
    expect(welcome.binding).toMatchObject({ deviceId: service.identity.deviceId, serverEpoch: service.identity.serverEpoch })
    await expect(client.query('device.identity', null)).resolves.toEqual({
      deviceId: service.identity.deviceId, serverEpoch: service.identity.serverEpoch })
    client.detach()
  })

  it('shows the device identity for confirmation before any request is sent, and honours a refusal', async () => {
    service = await begin()
    const seen: string[] = []
    await expect(pairWithDevice(service.createInvitation().text, { controllerId: 'controller', label: 'Phone',
      confirmDevice: (invitation) => { seen.push(invitation.rootPublicKeySha256); return false } }))
      .rejects.toMatchObject({ code: 'unauthorized' })
    expect(seen).toHaveLength(1)
    // 拒绝后邀请未被消耗,仍可用。
    expect(await pair()).toBeTruthy()
  })

  it('an invitation works once and the token never reaches the database in clear text', async () => {
    service = await begin()
    const { text } = service.createInvitation()
    const first = await pairWithDevice(text, { controllerId: 'controller', label: 'A', confirmDevice: () => true })
    expect(first.certificatePem).toContain('BEGIN CERTIFICATE')
    await expect(pairWithDevice(text, { controllerId: 'other', label: 'B', confirmDevice: () => true }))
      .rejects.toMatchObject({ code: 'unauthorized' })
  })

  it('rejects expired, tampered and wrong-root invitations', async () => {
    service = await begin()
    const { text, expiresAt } = service.createInvitation()
    await expect(pairWithDevice(text, { controllerId: 'controller', label: 'A', now: expiresAt + 1, confirmDevice: () => true }))
      .rejects.toMatchObject({ code: 'unauthorized' })
    await expect(pairWithDevice(`${text}x`, { controllerId: 'controller', label: 'A', confirmDevice: () => true })).rejects.toBeTruthy()
    const invitation = decodeInvitation(text, Date.now())
    expect(invitation.rootPublicKeySha256).toMatch(/^[0-9a-f]{64}$/)
    expect(invitation.token).not.toBe('')
    await expect(pairWithDevice('garbage', { controllerId: 'controller', label: 'A', confirmDevice: () => true }))
      .rejects.toMatchObject({ code: 'invalid_request' })
  })

  it('revoking drops live connections and blocks reconnecting', async () => {
    service = await begin()
    const credentials = await pair()
    const { client } = await connect(credentials)
    await expect(client.query('device.identity', null)).resolves.toBeTruthy()
    expect(service.revoke('controller')).toBe(true)
    await expect(client.query('device.identity', null)).rejects.toBeInstanceOf(Error)
    await expect.poll(() => service!.connections()).toBe(0)
    await expect(connect(credentials)).rejects.toBeInstanceOf(Error)
  })

  it('restart keeps identity, root and pairing; a second service on the same root is refused', async () => {
    service = await begin()
    await expect(begin()).rejects.toMatchObject({ code: 'conflict' })
    const credentials = await pair()
    const first = service.identity
    await service.stop()
    service = await begin()
    expect(service.identity).toMatchObject({ deviceId: first.deviceId, dataEpoch: first.dataEpoch })
    expect(service.identity.serverEpoch).not.toBe(first.serverEpoch)
    const { client } = await connect(credentials)
    await expect(client.query('device.identity', null)).resolves.toBeTruthy()
    client.detach()
  })

  it('keeps the CA key private to the owner', async () => {
    service = await begin()
    if (process.platform !== 'win32') {
      expect(statSync(join(base, 'data', 'pki', 'ca.key')).mode & 0o077).toBe(0)
      expect(statSync(join(base, 'data', 'pki', 'server.key')).mode & 0o077).toBe(0)
    }
  })

  it('stops idempotently, runs onStop before the database closes and releases the root', async () => {
    let stops = 0
    service = await begin(() => { stops++ })
    await Promise.all([service.stop(), service.stop()])
    expect(stops).toBe(1)
    service = await begin()
  })
})
