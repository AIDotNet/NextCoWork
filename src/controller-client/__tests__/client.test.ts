import { describe, expect, it, vi } from 'vitest'
import { ControllerClient } from '../client'
import type { ClientClock, SecureTransport } from '../ports'
import { decodeDeviceFrame, encodeDeviceFrame } from '../../shared/device/codec'
import type { DeviceBinding } from '../../shared/device/identity'
import type { DeviceEvent, DeviceFrame, DeviceRequest, DeviceResponse } from '../../shared/device/contract'

const binding: DeviceBinding = { deviceId: 'a', serverEpoch: 'server', dataEpoch: 'data', controllerId: 'phone',
  clientSessionId: 'app', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }

class Transport implements SecureTransport {
  frames = new Set<(frame: Uint8Array) => void>()
  closed = new Set<() => void>()
  sent: DeviceRequest[] = []
  onSend?: (request: DeviceRequest) => void
  send(frame: Uint8Array): void { const request = decodeDeviceFrame(frame) as DeviceRequest; this.sent.push(request); this.onSend?.(request) }
  onFrame(listener: (frame: Uint8Array) => void) { this.frames.add(listener); return () => { this.frames.delete(listener) } }
  onClose(listener: () => void) { this.closed.add(listener); return () => { this.closed.delete(listener) } }
  close(): void { for (const listener of this.closed) listener() }
  push(frame: DeviceFrame): void { for (const listener of this.frames) listener(encodeDeviceFrame(frame)) }
}
function setup() {
  let id = 0
  const timers = new Set<() => void>()
  const clock: ClientClock = { schedule: (callback) => { timers.add(callback); return () => { timers.delete(callback) } } }
  const resync = vi.fn()
  const client = new ControllerClient({ nextId: () => `r-${++id}`, clock, onResyncRequired: resync })
  const transport = new Transport()
  client.attach(binding, transport)
  return { client, transport, timers, resync }
}
function success(request: DeviceRequest, data = 'ok'): DeviceResponse {
  return { v: 1, type: 'response', binding: request.binding, id: request.id, ok: true, data }
}
function event(cursor: string, previousCursor: string | null): DeviceEvent {
  return { v: 1, type: 'event', binding, streamId: 'run', cursor, previousCursor, event: 'run.changed', data: null }
}

describe('platform-independent controller', () => {
  it('registers a response before even a synchronous send can answer', async () => {
    const { client, transport, timers } = setup()
    transport.onSend = (request) => transport.push(success(request))
    await expect(client.command('agent.run', { text: 'hello' }, 'key-1')).resolves.toBe('ok')
    expect(transport.sent[0]?.idempotencyKey).toBe('key-1')
    expect(timers.size).toBe(0)
    client.detach()
  })

  it('switches without aborting the old device or rerouting pending writes', async () => {
    const { client, transport, timers } = setup()
    const pending = client.command('files.save', { content: 'A' }, 'save-a')
    const rejected = expect(pending).rejects.toMatchObject({ code: 'result_unknown', idempotencyKey: 'save-a' })
    const late = [...transport.frames][0]!
    const next = new Transport()
    client.attach({ ...binding, deviceId: 'b', bindingEpoch: 1 }, next)
    await rejected
    late(encodeDeviceFrame(success(transport.sent[0]!)))
    expect(transport.sent.map((request) => request.method)).toEqual(['files.save'])
    expect(next.sent).toEqual([])
    expect(transport.frames.size).toBe(0)
    expect(timers.size).toBe(0)
    client.detach()
  })

  it('does not retry an uncertain command on timeout', async () => {
    const { client, transport, timers } = setup()
    const pending = client.command('agent.run', null, 'same-key')
    const rejected = expect(pending).rejects.toMatchObject({ code: 'result_unknown', idempotencyKey: 'same-key' })
    for (const callback of [...timers]) callback()
    await rejected
    transport.push(success(transport.sent[0]!))
    expect(transport.sent).toHaveLength(1)
    client.detach()
  })

  it('requires resync after a cursor gap and ignores duplicate or further stale events', () => {
    const { client, transport, resync } = setup()
    const listener = vi.fn()
    client.follow('run', 'cursor-0', listener)
    transport.push(event('cursor-1', 'cursor-0'))
    transport.push(event('cursor-1', 'cursor-0'))
    transport.push(event('cursor-3', 'cursor-2'))
    transport.push(event('cursor-4', 'cursor-3'))
    expect(listener).toHaveBeenCalledTimes(1)
    expect(resync).toHaveBeenCalledExactlyOnceWith('run')
    client.follow('run', 'cursor-4', listener)
    transport.push(event('cursor-5', 'cursor-4'))
    expect(listener).toHaveBeenCalledTimes(2)
    client.detach()
  })

  it('rejects a frame from the wrong device and clears connection state', async () => {
    const { client, transport } = setup()
    const pending = client.query('sessions.list', null)
    const rejected = expect(pending).rejects.toMatchObject({ code: 'disconnected' })
    transport.push({ ...success(transport.sent[0]!), binding: { ...binding, deviceId: 'wrong' } })
    await rejected
    expect(transport.frames.size).toBe(0)
  })

  it('resyncs if a reducer throws rather than silently accepting the next cursor', () => {
    const { client, transport, resync } = setup()
    const listener = vi.fn(() => { throw new Error('cannot apply event') })
    client.follow('run', 'c0', listener)
    transport.push(event('c1', 'c0'))
    transport.push(event('c2', 'c1'))
    expect(listener).toHaveBeenCalledTimes(1)
    expect(resync).toHaveBeenCalledExactlyOnceWith('run')
    client.detach()
  })

  it('rejects contradictory duplicate metadata but keeps a replacement subscription alive', () => {
    const { client, transport, resync } = setup()
    const oldDispose = client.follow('run', 'c0', () => {})
    const listener = vi.fn()
    const dispose = client.follow('run', 'c0', listener)
    oldDispose() // 旧视图的 cleanup 不能拆掉新视图。
    transport.push(event('c1', 'c0'))
    transport.push(event('c1', 'different-parent'))
    expect(listener).toHaveBeenCalledTimes(1)
    expect(resync).toHaveBeenCalledExactlyOnceWith('run')
    dispose()
    client.detach()
  })

  it('does not reuse wire request ids when an injected generator resets', async () => {
    const client = new ControllerClient({ nextId: () => 'reset-id', clock: { schedule: () => () => {} } })
    const old = new Transport()
    client.attach(binding, old)
    const first = client.query('files.read', { path: 'a' }).catch(() => null)
    const next = new Transport()
    client.attach(binding, next)
    await first
    const second = client.query('files.read', { path: 'b' })
    next.push(success(old.sent[0]!, 'stale'))
    expect(next.sent[0]!.id).not.toBe(old.sent[0]!.id)
    next.push(success(next.sent[0]!, 'fresh'))
    await expect(second).resolves.toBe('fresh')
    client.detach()
  })

  it('cleans listeners when a closed transport calls back during registration', () => {
    const client = new ControllerClient({ nextId: () => 'id', clock: { schedule: () => () => {} } })
    const transport = new Transport()
    let disposed = false
    transport.onClose = (listener) => { listener(); return () => { disposed = true } }
    client.attach(binding, transport)
    expect(disposed).toBe(true)
    expect(transport.frames.size).toBe(0)
  })

  it('reports a scheduler failure before sending as internal, not result unknown', async () => {
    const transport = new Transport()
    const client = new ControllerClient({ nextId: () => 'id', clock: { schedule: () => { throw new Error('clock unavailable') } } })
    client.attach(binding, transport)
    await expect(client.command('agent.run', null, 'key')).rejects.toMatchObject({ code: 'internal' })
    expect(transport.sent).toEqual([])
    client.detach()
  })

  it('bounds outstanding requests and releases every timer on suspend', async () => {
    const { client, timers } = setup()
    const pending = Array.from({ length: 64 }, () => client.query('sessions.list', null).catch((error: unknown) => error))
    await expect(client.query('sessions.list', null)).rejects.toMatchObject({ code: 'resource_limit' })
    client.detach()
    await Promise.all(pending)
    expect(timers.size).toBe(0)
    await expect(client.query('sessions.list', null)).rejects.toMatchObject({ code: 'disconnected' })
  })
})
