import { describe, expect, it, vi } from 'vitest'
import { connectController, type ControllerHandshakeOptions } from '../connect'
import { ControllerClient } from '../client'
import type { ClientClock, SecureTransport } from '../ports'
import { decodeDeviceFrame, decodeDeviceValue, encodeDeviceFrame, encodeDeviceValue } from '../../shared/device/codec'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { ClientCapabilities } from '../../shared/device/capabilities'
import type { DeviceBinding } from '../../shared/device/identity'
import type {
  DeviceEvent, DeviceFrame, DeviceHello, DeviceRequest, DeviceResponse, DeviceWelcome
} from '../../shared/device/contract'

const binding: DeviceBinding = {
  deviceId: 'device-a', serverEpoch: 'server', dataEpoch: 'data', controllerId: 'controller-1',
  clientSessionId: 'client-1', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0
}
const capabilities: ClientCapabilities = { kind: 'test', maxFrameBytes: 4096, features: [] }

function welcome(overrides: Partial<DeviceBinding> = {}): DeviceWelcome {
  return { v: 1, type: 'welcome', protocol: 1, binding: { ...binding, ...overrides },
    capabilities: { maxFrameBytes: capabilities.maxFrameBytes, features: capabilities.features } }
}

/** 镜像 NodeWebSocketChannel 的语义：close 幂等、关闭后不再接收、关闭时先清空订阅者。 */
class Transport implements SecureTransport {
  frames = new Set<(frame: Uint8Array) => void>()
  closed = new Set<() => void>()
  sent: DeviceFrame[] = []
  closes = 0
  ended = false
  onSend?: (frame: DeviceFrame) => void
  send(frame: Uint8Array): void {
    let decoded: DeviceFrame | null = null
    try { decoded = decodeDeviceFrame(frame) } catch { /* 传输层不会为畸形帧回调业务逻辑。 */ }
    if (!decoded) return
    this.sent.push(decoded)
    this.onSend?.(decoded)
  }
  onFrame(listener: (frame: Uint8Array) => void) { this.frames.add(listener); return () => { this.frames.delete(listener) } }
  onClose(listener: () => void) {
    if (this.ended) { listener(); return () => {} }
    this.closed.add(listener)
    return () => { this.closed.delete(listener) }
  }
  close(): void {
    if (this.ended) return
    this.ended = true
    this.closes++
    const listeners = [...this.closed]
    this.closed.clear()
    for (const listener of listeners) listener()
  }
  push(frame: DeviceFrame): void { this.pushBytes(encodeDeviceFrame(frame)) }
  pushBytes(bytes: Uint8Array): void {
    if (this.ended) return
    for (const listener of [...this.frames]) listener(bytes)
  }
}

/** 只发 hello 不回包，也从不重传业务命令的传输：失败路径必须在这里留下零个 request。 */
function setup(overrides: Partial<ControllerHandshakeOptions> = {}) {
  const transport = new Transport()
  const timers = new Set<() => void>()
  const clock: ClientClock = { schedule: (callback) => { timers.add(callback); return () => { timers.delete(callback) } } }
  let id = 0
  const options: ControllerHandshakeOptions = {
    expectedDeviceId: 'device-a', expectedControllerId: 'controller-1', clientSessionId: 'client-1',
    bindingEpoch: 0, capabilities, nextId: () => `r-${++id}`, clock, ...overrides
  }
  return { transport, timers, options }
}

function success(request: DeviceRequest, data = 'ok'): DeviceResponse {
  return { v: 1, type: 'response', binding, id: request.id, ok: true, data }
}

describe('platform-independent controller handshake', () => {
  it('answers hello with the expected shape and hands a usable client back', async () => {
    const { transport, options, timers } = setup()
    const expected: DeviceHello = {
      v: 1, type: 'hello', versions: [1], clientSessionId: 'client-1', bindingEpoch: 0, capabilities
    }
    transport.onSend = (frame) => { if (frame.type === 'hello') transport.push(welcome()) }
    const { client, welcome: result } = await connectController(transport, options)
    expect(transport.sent).toEqual([expected])
    expect(result).toEqual(welcome())
    expect(timers.size).toBe(0)
    expect(transport.closes).toBe(0)
    // 握手登记的监听已让位给 client，同一帧不会有两个处理者。
    expect(transport.frames.size).toBe(1)
    expect(transport.closed.size).toBe(1)
    transport.onSend = (frame) => { if (frame.type === 'request') transport.push(success(frame)) }
    await expect(client.query('sessions.list', null)).resolves.toBe('ok')
    client.detach()
    expect(transport.frames.size).toBe(0)
    expect(transport.closed.size).toBe(0)
  })

  it('resolves on a welcome that arrives synchronously inside send', async () => {
    const { transport, options } = setup()
    let resolved = false
    transport.onSend = (frame) => { if (frame.type === 'hello') { transport.push(welcome()); resolved = true } }
    const pending = connectController(transport, options)
    expect(resolved).toBe(true)
    await expect(pending).resolves.toMatchObject({ welcome: { binding: { deviceId: 'device-a' } } })
  })

  it.each([
    ['device', { deviceId: 'device-b' }],
    ['controller', { controllerId: 'controller-2' }],
    ['client session', { clientSessionId: 'client-2' }],
    ['binding epoch', { bindingEpoch: 1 }]
  ])('rejects a welcome whose %s does not match the paired expectation', async (_label, overrides) => {
    const { transport, options } = setup()
    transport.onSend = (frame) => { if (frame.type === 'hello') transport.push(welcome(overrides)) }
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'unauthorized' })
    expect(transport.closes).toBe(1)
    expect(transport.frames.size).toBe(0)
    expect(transport.sent.filter((frame) => frame.type === 'request')).toEqual([])
  })

  it('rejects a welcome for another protocol version as version_mismatch', async () => {
    const { transport, options } = setup()
    // 非法 protocol 无法通过 schema 编码，这里就按传输层实际收到的字节注入。
    const bytes = encodeDeviceFrame(welcome())
    const value = decodeDeviceValue(bytes) as { protocol: number }
    value.protocol = 2
    transport.onSend = (frame) => { if (frame.type === 'hello') transport.pushBytes(encodeDeviceValue(value)) }
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'version_mismatch' })
    expect(transport.frames.size).toBe(0)
  })

  it('surfaces the peer handshake error and maps codes that do not apply here', async () => {
    const { transport, options } = setup()
    transport.onSend = (frame) => {
      if (frame.type === 'hello') {
        transport.push({ v: 1, type: 'handshake_error', error: { code: 'unauthorized', message: 'not paired', retryable: false } })
      }
    }
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'unauthorized', message: 'not paired' })

    const next = setup()
    next.transport.onSend = (frame) => {
      if (frame.type === 'hello') {
        next.transport.push({ v: 1, type: 'handshake_error', error: { code: 'resource_limit', message: 'busy', retryable: true } })
      }
    }
    await expect(connectController(next.transport, next.options)).rejects.toMatchObject({ code: 'protocol_error' })
    expect(next.transport.closes).toBe(1)
  })

  it('rejects malformed, wrong-direction and unexpected frames as protocol errors', async () => {
    const malformed = setup()
    malformed.transport.onSend = (frame) => { if (frame.type === 'hello') malformed.transport.pushBytes(new Uint8Array([0xc1])) }
    await expect(connectController(malformed.transport, malformed.options)).rejects.toMatchObject({ code: 'protocol_error' })
    expect(malformed.transport.closes).toBe(1)

    const request: DeviceRequest = { v: 1, type: 'request', id: 'request-1', binding, method: 'agent.run', params: null }
    const badWelcome = { ...welcome(), extra: 1 }
    const frames: [string, () => Uint8Array][] = [
      ['request', () => encodeDeviceFrame(request)],
      ['response', () => encodeDeviceFrame(success(request))],
      ['unknown marker', () => new Uint8Array([0xa6, 0x61, 0x62, 0x6f, 0x72, 0x74, 0x31])],
      ['oversized welcome', () => encodeDeviceValue(badWelcome)]
    ]
    for (const [label, bytes] of frames) {
      const { transport, options } = setup()
      transport.onSend = (sent) => { if (sent.type === 'hello') transport.pushBytes(bytes()) }
      await expect(connectController(transport, options), label).rejects.toMatchObject({ code: 'protocol_error' })
      expect(transport.closes).toBe(1)
      expect(transport.sent.filter((sent) => sent.type === 'request')).toEqual([])
    }
  })

  it('times out, closes the transport and leaves no listener or timer behind', async () => {
    const { transport, options, timers } = setup()
    const pending = connectController(transport, options)
    const rejected = expect(pending).rejects.toMatchObject({ code: 'timeout' })
    for (const callback of [...timers]) callback()
    await rejected
    expect(transport.closes).toBe(1)
    expect(transport.frames.size).toBe(0)
    expect(transport.closed.size).toBe(0)
    expect(timers.size).toBe(0)
    expect(transport.sent).toHaveLength(1)
    expect(transport.sent[0]?.type).toBe('hello')
    transport.push(welcome())
    expect(transport.frames.size).toBe(0)
  })

  it('treats a close during the handshake as disconnected', async () => {
    const { transport, options, timers } = setup()
    const pending = connectController(transport, options)
    const rejected = expect(pending).rejects.toMatchObject({ code: 'disconnected' })
    transport.close()
    await rejected
    expect(transport.closes).toBe(1)
    expect(transport.closed.size).toBe(0)
    expect(transport.frames.size).toBe(0)
    expect(timers.size).toBe(0)
  })

  it('cleans up when the transport is already closed while listeners are registered', async () => {
    const { transport, options, timers } = setup()
    let disposedClose = false
    transport.onClose = (listener) => { listener(); return () => { disposedClose = true } }
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'disconnected' })
    expect(disposedClose).toBe(true)
    expect(transport.sent).toEqual([])
    expect(transport.frames.size).toBe(0)
    expect(timers.size).toBe(0)
  })

  it('does not send hello when the scheduler throws or fires synchronously', async () => {
    const throwing = setup({ clock: { schedule: () => { throw new Error('clock unavailable') } } })
    await expect(connectController(throwing.transport, throwing.options)).rejects.toMatchObject({ code: 'internal' })
    expect(throwing.transport.sent).toEqual([])
    expect(throwing.transport.closes).toBe(1)
    expect(throwing.transport.frames.size).toBe(0)

    const immediate = setup({ clock: { schedule: (callback) => { callback(); return () => {} } } })
    await expect(connectController(immediate.transport, immediate.options)).rejects.toMatchObject({ code: 'timeout' })
    expect(immediate.transport.sent).toEqual([])
    expect(immediate.transport.closes).toBe(1)
    expect(immediate.transport.frames.size).toBe(0)
  })

  it('refuses invalid expectations before touching the transport', async () => {
    const { transport, options } = setup({ expectedDeviceId: '' })
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'invalid_request' })
    const bad = setup({ bindingEpoch: -1 })
    await expect(connectController(bad.transport, bad.options)).rejects.toMatchObject({ code: 'invalid_request' })
    const slow = setup({ timeoutMs: 0 })
    await expect(connectController(slow.transport, slow.options)).rejects.toMatchObject({ code: 'invalid_request' })
    expect(transport.sent).toEqual([])
    expect(transport.closes).toBe(0)
  })

  it('rejects a welcome buffered before hello and releases the late-returned disposer', async () => {
    const { transport, options, timers } = setup()
    const register = transport.onFrame.bind(transport)
    transport.onFrame = (listener) => {
      const dispose = register(listener)
      listener(encodeDeviceFrame(welcome()))
      return dispose
    }
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'protocol_error' })
    expect(transport.frames.size).toBe(0)
    expect(transport.closes).toBe(1)
    expect(transport.sent).toEqual([])
    expect(timers.size).toBe(0)
  })

  it('keeps an event pushed on the same turn as the welcome on the ordered channel', async () => {
    const { transport, options } = setup()
    const event: DeviceEvent = {
      v: 1, type: 'event', binding, streamId: 'run', cursor: 'c1', previousCursor: null, event: 'run.changed', data: null
    }
    transport.onSend = (frame) => { if (frame.type === 'hello') { transport.push(welcome()); transport.push(event) } }
    const { client } = await connectController(transport, options)
    expect(transport.closes).toBe(0)
    transport.onSend = (frame) => { if (frame.type === 'request') transport.push(success(frame)) }
    // 未 follow 的事件按 client 既有策略忽略，但不能因此拆掉刚刚建立的连接。
    await expect(client.query('sessions.list', null)).resolves.toBe('ok')
    expect(transport.sent.filter((frame) => frame.type === 'request')).toHaveLength(1)
    client.detach()
  })

  it('closes on a duplicate welcome after handoff without sending agent.abort', async () => {
    const { transport, options } = setup()
    transport.onSend = (frame) => { if (frame.type === 'hello') { transport.push(welcome()); transport.push(welcome()) } }
    const { client, welcome: result } = await connectController(transport, options)
    expect(result.binding.bindingEpoch).toBe(0)
    transport.push(welcome())
    await Promise.resolve()
    expect(transport.sent.map((frame) => frame.type)).toEqual(['hello'])
    expect(transport.sent.some((frame) => frame.type === 'request' && frame.method === 'agent.abort')).toBe(false)
    expect(transport.frames.size).toBe(0)
    expect(transport.closed.size).toBe(0)
    await expect(client.query('sessions.list', null)).rejects.toMatchObject({ code: 'disconnected' })
    client.detach()
  })

  it('enforces the negotiated frame limit before sending a large RPC', async () => {
    const { transport, options } = setup()
    transport.onSend = (frame) => { if (frame.type === 'hello') transport.push(welcome()) }
    const { client } = await connectController(transport, options)
    await expect(client.command('files.save', 'x'.repeat(5000), 'write-once')).rejects.toMatchObject({ code: 'resource_limit' })
    expect(transport.sent.map((frame) => frame.type)).toEqual(['hello'])
    client.detach()
  })

  it('does not hand the transport to the client when attach rejects', async () => {
    const { transport, options } = setup()
    const attach = vi.spyOn(ControllerClient.prototype, 'attach').mockImplementation(() => {
      throw new DeviceProtocolError('internal', 'attach failed')
    })
    transport.onSend = (frame) => { if (frame.type === 'hello') transport.push(welcome()) }
    await expect(connectController(transport, options)).rejects.toMatchObject({ code: 'internal' })
    expect(transport.closes).toBe(1)
    expect(transport.frames.size).toBe(0)
    expect(transport.closed.size).toBe(0)
    attach.mockRestore()
  })
})
