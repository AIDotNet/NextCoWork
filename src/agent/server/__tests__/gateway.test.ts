import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { connectController } from '../../../controller-client/connect'
import { decodeDeviceFrame, encodeDeviceFrame } from '../../../shared/device/codec'
import type { DeviceFrame, DeviceRequest } from '../../../shared/device/contract'
import { DeviceGatewayConnection } from '../gateway'
import { DeviceRpcRouter } from '../rpc'

class Endpoint implements SecureTransport {
  other!: Endpoint
  frames = new Set<(frame: Uint8Array) => void>()
  closes = new Set<() => void>()
  sent: DeviceFrame[] = []
  ended = false
  send(bytes: Uint8Array): void {
    if (this.ended) throw new Error('closed')
    this.sent.push(decodeDeviceFrame(bytes))
    for (const callback of [...this.other.frames]) callback(bytes)
  }
  onFrame(callback: (bytes: Uint8Array) => void) { this.frames.add(callback); return () => { this.frames.delete(callback) } }
  onClose(callback: () => void) {
    if (this.ended) { callback(); return () => {} }
    this.closes.add(callback); return () => { this.closes.delete(callback) }
  }
  close(): void {
    if (this.ended) return
    this.ended = true
    for (const callback of [...this.closes]) callback()
    this.closes.clear()
    if (!this.other.ended) this.other.close()
  }
}
const scope = { deviceId: 'device', serverEpoch: 'server', dataEpoch: 'data', configScope: 'local', scopeEpoch: 'scope' }
function setup(maxInFlight?: number, burstRequests?: number) {
  const server = new Endpoint(), client = new Endpoint()
  server.other = client; client.other = server
  const timers = new Set<() => void>()
  const clock: ClientClock = { schedule: (callback) => { timers.add(callback); return () => { timers.delete(callback) } } }
  const router = new DeviceRpcRouter()
  let revoked = false
  let allowed = true
  const gateway = new DeviceGatewayConnection(server, { router, clock, maxInFlight, burstRequests, now: () => 0,
    peer: { controllerId: 'controller', revoked: () => revoked, allows: () => allowed },
    currentScope: () => scope, capabilities: { maxFrameBytes: 4096, features: ['tasks'] } })
  return { server, client, gateway, router, timers, clock,
    revoke: () => { revoked = true }, forbid: () => { allowed = false } }
}
async function connect(state: ReturnType<typeof setup>) {
  return connectController(state.client, { expectedDeviceId: 'device', expectedControllerId: 'controller',
    clientSessionId: 'session', bindingEpoch: 0, nextId: () => 'req', clock: state.clock,
    capabilities: { kind: 'test', maxFrameBytes: 4096, features: ['tasks'] } })
}

describe('authenticated gateway connection', () => {
  it('performs a real protocol handshake and routes only registered methods', async () => {
    const state = setup()
    state.router.query('sessions.list', z.null(), z.array(z.string()), () => ['session-1'])
    const { client } = await connect(state)
    await expect(client.query('sessions.list', null)).resolves.toEqual(['session-1'])
    await expect(client.query('app.anyIpc', null)).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
    expect(state.client.frames.size).toBe(0)
    expect(state.server.frames.size).toBe(0)
    expect(state.timers.size).toBe(0)
  })

  it('checks current permissions and revocation for every request', async () => {
    const state = setup()
    state.router.query('settings.get', z.null(), z.string(), () => 'private')
    const { client } = await connect(state)
    state.forbid()
    await expect(client.query('settings.get', null)).rejects.toMatchObject({ code: 'forbidden' })
    state.revoke()
    await expect(client.query('settings.get', null)).rejects.toMatchObject({ code: 'unauthorized' })
    client.detach()
  })

  it('does not trust a request controllerId and masks internal exception details', async () => {
    const state = setup()
    let calls = 0
    state.router.query('settings.get', z.null(), z.string(), () => { calls++; throw new Error('secret=private-key') })
    const { client, welcome } = await connect(state)
    await expect(client.query('settings.get', null)).rejects.toMatchObject({ code: 'internal', message: 'Device operation failed' })
    const forged: DeviceRequest = { v: 1, type: 'request', id: 'forged', method: 'settings.get', params: null,
      binding: { ...welcome.binding, controllerId: 'other-controller' } }
    state.client.send(encodeDeviceFrame(forged))
    await Promise.resolve(); await Promise.resolve()
    expect(calls).toBe(1)
    expect(JSON.stringify(state.server.sent)).not.toContain('private-key')
    client.detach()
  })

  it('refuses business frames before hello and limits unfinished queries', async () => {
    const before = setup()
    before.client.send(encodeDeviceFrame({ v: 1, type: 'request', id: 'early', method: 'settings.get', params: null,
      binding: { ...scope, controllerId: 'controller', clientSessionId: 'session', bindingEpoch: 0 } }))
    expect(before.server.ended).toBe(true)
    expect(before.timers.size).toBe(0)
    const state = setup(1)
    let complete!: (value: string) => void
    state.router.query('settings.get', z.null(), z.string(), () => new Promise((resolve) => { complete = resolve }))
    const { client } = await connect(state)
    const first = client.query('settings.get', null).catch((error: unknown) => error)
    const second = client.query('settings.get', null).catch((error: unknown) => error)
    expect(state.server.ended).toBe(true)
    complete('late-result')
    await Promise.all([first, second])
    expect(state.server.sent.filter((frame) => frame.type === 'response')).toEqual([])
  })

  it('limits sequential request floods as well as concurrent requests', async () => {
    const state = setup(32, 2) // hello consumes one token.
    state.router.query('settings.get', z.null(), z.null(), () => null)
    const { client } = await connect(state)
    await expect(client.query('settings.get', null)).resolves.toBeNull()
    await expect(client.query('settings.get', null)).rejects.toMatchObject({ code: 'disconnected' })
    expect(state.server.ended).toBe(true)
    expect(state.timers.size).toBe(0)
  })

  it('closes idle handshake connections without leaking listeners or timers', () => {
    const state = setup()
    for (const timeout of [...state.timers]) timeout()
    expect(state.server.ended).toBe(true)
    expect(state.server.frames.size).toBe(0)
    expect(state.timers.size).toBe(0)
  })

  it('pushes events only on the owning connection, caps subscriptions, and releases them on close', async () => {
    const state = setup()
    const released: string[] = []
    let pushed: string[] = []
    state.router.query('streams.open', z.object({ key: z.string() }), z.null(), (context, { key }) => {
      context.connection.subscribe(key, () => { released.push(key) })
      if (key === 'a') pushed.push(context.connection.push({ streamId: 'stream-a', cursor: '1', previousCursor: '0', event: 'demo.event', data: 'x' }))
      return null
    })
    state.router.query('streams.close', z.object({ key: z.string() }), z.null(), (context, { key }) => { context.connection.unsubscribe(key); return null })
    const { client } = await connect(state)
    await client.query('streams.open', { key: 'a' })
    expect(pushed).toEqual(['sent'])
    expect(state.server.sent.some((frame) => frame.type === 'event' && frame.streamId === 'stream-a')).toBe(true)
    // 同 key 再订阅 = 替换,旧的被释放。
    await client.query('streams.open', { key: 'a' })
    expect(released).toEqual(['a'])
    await client.query('streams.close', { key: 'a' })
    expect(released).toEqual(['a', 'a'])
    pushed = []
    for (let i = 0; i < 64; i++) await client.query('streams.open', { key: `k${i}` })
    await expect(client.query('streams.open', { key: 'overflow' })).rejects.toMatchObject({ code: 'resource_limit' })
    expect(released).toContain('overflow') // 被拒绝的订阅也要释放,不能泄漏。
    const before = released.length
    client.detach()
    state.server.close()
    expect(released.length).toBeGreaterThanOrEqual(before + 64)
  })

  it('never pushes to a revoked controller and reports oversized frames instead of truncating', async () => {
    const state = setup()
    let outcome: string[] = []
    state.router.query('streams.try', z.null(), z.null(), (context) => {
      outcome.push(context.connection.push({ streamId: 's', cursor: '1', previousCursor: null, event: 'demo.big', data: 'x'.repeat(8000) }))
      return null
    })
    const { client } = await connect(state)
    await client.query('streams.try', null)
    expect(outcome).toEqual(['too_large'])
    outcome = []
    state.revoke()
    await expect(client.query('streams.try', null)).rejects.toBeTruthy()
    expect(outcome).toEqual([])
    client.detach()
  })
})
