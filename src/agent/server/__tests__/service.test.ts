import { X509Certificate } from 'node:crypto'
import { afterEach, beforeAll, describe, expect, it } from 'vitest'
import { z } from 'zod'
import { connectController } from '../../../controller-client/connect'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { certificatePublicKeySha256, connectDirectDevice } from '../../../transport/node/direct'
import { listenDeviceServer, type DeviceServer } from '../../../transport/node/server'
import { testCertificates } from '../../../transport/node/__tests__/certificates'
import { DeviceGatewayConnection, type AuthenticatedDevicePeer } from '../gateway'
import { DeviceRpcRouter } from '../rpc'

const scope = { deviceId: 'device', serverEpoch: 'server', dataEpoch: 'data', configScope: 'local', scopeEpoch: 'scope' }
const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }

let certificates: Awaited<ReturnType<typeof testCertificates>>
let servers: DeviceServer[] = []
const transports: SecureTransport[] = []
beforeAll(async () => { certificates = await testCertificates() })
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  for (const server of servers.splice(0)) await server.close()
})

const fingerprint = (pem: string): string => certificatePublicKeySha256(new X509Certificate(pem))

async function start(options: { trusted?: string[]; maxConnectionsPerPeer?: number; revoked?: () => boolean } = {}) {
  const router = new DeviceRpcRouter()
  router.query('sessions.list', z.null(), z.array(z.string()), () => ['session-1'])
  const trusted = new Set(options.trusted ?? [fingerprint(certificates.client.certificatePem)])
  const gateways = new Set<DeviceGatewayConnection>()
  const server = await listenDeviceServer<AuthenticatedDevicePeer>({
    host: '127.0.0.1', port: 0, credentials: { caPem: certificates.caPem, ...certificates.server },
    ...(options.maxConnectionsPerPeer !== undefined && { maxConnectionsPerPeer: options.maxConnectionsPerPeer }),
    authenticate: ({ publicKeySha256 }) => trusted.has(publicKeySha256)
      ? { controllerId: 'controller', revoked: () => options.revoked?.() ?? false, allows: () => true }
      : null,
    accept: (transport, peer) => {
      const gateway: DeviceGatewayConnection = new DeviceGatewayConnection(transport, {
        peer, router, clock, currentScope: () => scope,
        capabilities: { maxFrameBytes: 1024 * 1024, features: ['tasks'] },
        onClosed: () => { gateways.delete(gateway) }
      })
      gateways.add(gateway)
    }
  })
  servers.push(server)
  return { server, url: `wss://127.0.0.1:${server.address().port}/device`, gateways }
}
function credentials(client = certificates.client) {
  return { caPem: certificates.caPem, ...client,
    trustedCaPublicKeySha256: certificatePublicKeySha256(new X509Certificate(certificates.caPem)) }
}
async function controller(url: string, client = certificates.client) {
  const transport = await connectDirectDevice(url, credentials(client))
  transports.push(transport)
  return connectController(transport, { expectedDeviceId: 'device', expectedControllerId: 'controller',
    clientSessionId: 'session', bindingEpoch: 0, nextId: () => 'req', clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: ['tasks'] } })
}

describe('direct WSS device service', () => {
  it('serves registered RPC to a paired controller over real mutual TLS', async () => {
    const { url } = await start()
    const { client, welcome } = await controller(url)
    expect(welcome.binding.controllerId).toBe('controller')
    await expect(client.query('sessions.list', null)).resolves.toEqual(['session-1'])
    await expect(client.query('app.anyIpc', null)).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  })

  it('rejects a CA-signed certificate that has no trust record, before any protocol state exists', async () => {
    const { url, gateways, server } = await start({ trusted: [] })
    await expect(controller(url)).rejects.toBeInstanceOf(Error)
    expect(gateways.size).toBe(0)
    expect(server.connections()).toBe(0)
  })

  it('rejects a certificate from another CA at the TLS handshake', async () => {
    const { url } = await start()
    const other = await testCertificates()
    await expect(controller(url, other.client)).rejects.toBeInstanceOf(Error)
  })

  it('refuses a revoked controller during the handshake', async () => {
    const { url } = await start({ revoked: () => true })
    await expect(controller(url)).rejects.toMatchObject({ code: 'unauthorized' })
  })

  it('enforces a per-controller connection budget and releases it on close', async () => {
    const { url, server } = await start({ maxConnectionsPerPeer: 1 })
    const first = await controller(url)
    expect(server.connections()).toBe(1)
    await expect(controller(url)).rejects.toBeInstanceOf(Error)
    first.client.detach()
    await expect.poll(() => server.connections()).toBe(0)
    const again = await controller(url)
    again.client.detach()
  })

  it('terminates every connection on close', async () => {
    const { url, server, gateways } = await start()
    const { client } = await controller(url)
    await server.close()
    servers = servers.filter((item) => item !== server)
    await expect.poll(() => gateways.size).toBe(0)
    await expect(client.query('sessions.list', null)).rejects.toBeInstanceOf(Error)
  })
})
