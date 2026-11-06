import { createServer, type Server } from 'node:https'
import { X509Certificate } from 'node:crypto'
import type { TLSSocket } from 'node:tls'
import { afterEach, beforeAll, describe, expect, it } from 'vitest'
import { WebSocketServer } from 'ws'
import { connectDirectDevice, certificatePublicKeySha256, DEVICE_WEBSOCKET_PROTOCOL } from '../direct'
import { ControllerClient } from '../../../controller-client/client'
import { connectController } from '../../../controller-client/connect'
import { decodeDeviceFrame, encodeDeviceFrame } from '../../../shared/device/codec'
import type { SecureTransport } from '../../../controller-client/ports'
import { testCertificates } from './certificates'

let certificates: Awaited<ReturnType<typeof testCertificates>>
const resources: { server: Server; sockets: WebSocketServer }[] = []
const clients: SecureTransport[] = []
beforeAll(async () => { certificates = await testCertificates() })
afterEach(async () => {
  for (const client of clients.splice(0)) client.close()
  for (const { server, sockets } of resources.splice(0)) {
    for (const client of sockets.clients) client.terminate()
    await new Promise<void>((resolve) => sockets.close(() => resolve()))
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
})
async function endpoint() {
  const server = createServer({ key: certificates.server.privateKeyPem, cert: certificates.server.certificatePem,
    ca: certificates.caPem, requestCert: true, rejectUnauthorized: true, minVersion: 'TLSv1.3', maxVersion: 'TLSv1.3' })
  server.on('tlsClientError', () => {})
  const sockets = new WebSocketServer({ server, perMessageDeflate: false,
    handleProtocols: (protocols) => protocols.has(DEVICE_WEBSOCKET_PROTOCOL) ? DEVICE_WEBSOCKET_PROTOCOL : false })
  resources.push({ server, sockets })
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', () => resolve()) })
  const address = server.address()
  if (!address || typeof address === 'string') throw new Error('Missing listener address')
  return { url: `wss://127.0.0.1:${address.port}/device`, sockets }
}
function credentials() {
  return { caPem: certificates.caPem, ...certificates.client,
    trustedCaPublicKeySha256: certificatePublicKeySha256(new X509Certificate(certificates.caPem)) }
}

describe('pinned TLS 1.3 device transport', () => {
  it('round trips real binary RPC after mutual authentication', async () => {
    const { url, sockets } = await endpoint()
    let peerAuthorized = false
    let protocol = ''
    sockets.on('connection', (socket, request) => {
      const tls = request.socket as TLSSocket
      peerAuthorized = tls.authorized
      protocol = tls.getProtocol() ?? ''
      socket.on('message', (bytes) => {
        const frame = decodeDeviceFrame(new Uint8Array(bytes as Buffer))
        if (frame.type !== 'request') throw new Error('Unexpected message')
        socket.send(encodeDeviceFrame({ v: 1, type: 'response', id: frame.id, binding: frame.binding,
          ok: true, data: { source: 'device' } }))
      })
    })
    const transport = await connectDirectDevice(url, credentials())
    clients.push(transport)
    const controller = new ControllerClient({ nextId: () => 'req', clock: { schedule: (callback, ms) => {
      const timer = setTimeout(callback, ms); return () => clearTimeout(timer)
    } } })
    controller.attach({ deviceId: 'device', controllerId: 'controller', clientSessionId: 'session', serverEpoch: 'server',
      dataEpoch: 'data', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }, transport)
    await expect(controller.query('sessions.list', null)).resolves.toEqual({ source: 'device' })
    expect(peerAuthorized).toBe(true)
    expect(protocol).toBe('TLSv1.3')
    controller.detach()
  })

  it('completes hello/welcome over the real TLS channel before accepting RPC', async () => {
    const { url, sockets } = await endpoint()
    const methods: string[] = []
    sockets.on('connection', (socket) => {
      let ready = false
      socket.on('message', (bytes) => {
        const frame = decodeDeviceFrame(new Uint8Array(bytes as Buffer))
        if (frame.type === 'hello' && !ready) {
          ready = true
          socket.send(encodeDeviceFrame({ v: 1, type: 'welcome', protocol: 1,
            binding: { deviceId: 'device', controllerId: 'controller', clientSessionId: frame.clientSessionId,
              serverEpoch: 'server', dataEpoch: 'data', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: frame.bindingEpoch },
            capabilities: { maxFrameBytes: 1024 * 1024, features: ['tasks'] } }))
        } else if (frame.type === 'request' && ready) {
          methods.push(frame.method)
          socket.send(encodeDeviceFrame({ v: 1, type: 'response', id: frame.id, binding: frame.binding, ok: true, data: 'handshake-complete' }))
        } else socket.terminate()
      })
    })
    const transport = await connectDirectDevice(url, credentials())
    clients.push(transport)
    const { client, welcome } = await connectController(transport, {
      expectedDeviceId: 'device', expectedControllerId: 'controller', clientSessionId: 'native-ready', bindingEpoch: 3,
      capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: ['tasks'] }, nextId: () => 'req',
      clock: { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
    })
    expect(welcome.binding.clientSessionId).toBe('native-ready')
    await expect(client.query('sessions.list', null)).resolves.toBe('handshake-complete')
    expect(methods).toEqual(['sessions.list'])
    client.detach()
  })

  it('refuses a mismatched pin and insecure endpoint before opening a connection', async () => {
    const { url } = await endpoint()
    await expect(connectDirectDevice(url, { ...credentials(), trustedCaPublicKeySha256: '0'.repeat(64) })).rejects.toMatchObject({ code: 'unauthorized' })
    await expect(connectDirectDevice(url.replace('wss:', 'ws:'), credentials())).rejects.toMatchObject({ code: 'invalid_request' })
  })

  it('cancels an unfinished connection without sending a business command', async () => {
    const { url } = await endpoint()
    const abort = new AbortController()
    const connected = connectDirectDevice(url, credentials(), { signal: abort.signal })
    abort.abort()
    await expect(connected).rejects.toMatchObject({ code: 'disconnected' })
    await expect(connectDirectDevice(url, credentials(), { signal: abort.signal })).rejects.toMatchObject({ code: 'disconnected' })
    await expect(connectDirectDevice(`${url}?token=not-allowed`, credentials())).rejects.toMatchObject({ code: 'invalid_request' })
  })

  it('does not accept an untrusted controller certificate', async () => {
    const { url } = await endpoint()
    const untrusted = await testCertificates()
    await expect(connectDirectDevice(url, { ...credentials(), ...untrusted.client })).rejects.toBeInstanceOf(Error)
  })
})
