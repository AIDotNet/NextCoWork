import { DEVICE_FRAME_MAX_BYTES, DEVICE_PROTOCOL_VERSION, clientCapabilitiesSchema, type ClientCapabilities } from '../shared/device/capabilities'
import { decodeDeviceValue, encodeDeviceFrame } from '../shared/device/codec'
import { deviceHandshakeErrorSchema, deviceWelcomeSchema, type DeviceHello, type DeviceWelcome } from '../shared/device/contract'
import { DeviceProtocolError, type DeviceErrorCode } from '../shared/device/errors'
import { deviceIdSchema } from '../shared/device/identity'
import type { DeviceValue } from '../shared/device/value'
import { ControllerClient } from './client'
import type { ClientClock, Dispose, SecureTransport } from './ports'

/** 与传输层握手超时（10s）保持一致的默认值。 */
const DEFAULT_HANDSHAKE_TIMEOUT_MS = 10_000
const MAX_HANDSHAKE_TIMEOUT_MS = 600_000
/** 握手窗口最多暂存的帧数；与 DEVICE_FRAME_MAX_BYTES 一起保证这里只做有界缓存。 */
const HANDSHAKE_QUEUE_MAX_FRAMES = 64

export interface ControllerHandshakeOptions {
  /** 期望身份来自配对信任；welcome 自报一致才继续，绝不用对端自报值覆盖期望值。 */
  expectedDeviceId: string
  expectedControllerId: string
  clientSessionId: string
  bindingEpoch: number
  capabilities: ClientCapabilities
  nextId(): string
  clock: ClientClock
  timeoutMs?: number
  onResyncRequired?: (streamId: string) => void
  onObserverError?: (error: unknown) => void
}

export interface ControllerHandshakeResult {
  client: ControllerClient
  welcome: DeviceWelcome
}

/** 对端自报的握手错误码只在本地白名单内映射；握手阶段不适用的一律降级为 protocol_error。 */
function mapHandshakeErrorCode(code: DeviceErrorCode): DeviceErrorCode {
  switch (code) {
    case 'version_mismatch': return 'version_mismatch'
    case 'unauthorized':
    case 'forbidden':
    case 'stale_binding': return 'unauthorized'
    case 'timeout': return 'timeout'
    case 'disconnected': return 'disconnected'
    case 'internal': return 'internal'
    default: return 'protocol_error'
  }
}

interface HandshakeSlot {
  dispose: Dispose | null
  released: boolean
}

function deviceIdOption(label: string, value: unknown): string {
  const parsed = deviceIdSchema.safeParse(value)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', `Invalid ${label}`)
  return parsed.data
}

/**
 * 在已认证传输之上完成设备协议握手，成功时把连接所有权交给 ControllerClient。
 * 这里不读取任何凭据、不重试业务命令，失败/超时一律关闭传输并撤掉全部监听与计时器。
 */
export async function connectController(
  transport: SecureTransport,
  options: ControllerHandshakeOptions
): Promise<ControllerHandshakeResult> {
  const expectedDeviceId = deviceIdOption('expected device id', options.expectedDeviceId)
  const expectedControllerId = deviceIdOption('expected controller id', options.expectedControllerId)
  const clientSessionId = deviceIdOption('client session id', options.clientSessionId)
  if (!Number.isSafeInteger(options.bindingEpoch) || options.bindingEpoch < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid binding epoch')
  }
  const capabilities = clientCapabilitiesSchema.safeParse(options.capabilities)
  if (!capabilities.success) throw new DeviceProtocolError('invalid_request', 'Invalid client capabilities')
  const timeoutMs = options.timeoutMs ?? DEFAULT_HANDSHAKE_TIMEOUT_MS
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > MAX_HANDSHAKE_TIMEOUT_MS) {
    throw new DeviceProtocolError('invalid_request', 'Invalid handshake timeout')
  }
  if (typeof options.nextId !== 'function' || typeof options.clock?.schedule !== 'function') {
    throw new DeviceProtocolError('invalid_request', 'Invalid controller client options')
  }
  if (typeof transport?.send !== 'function' || typeof transport.onFrame !== 'function'
    || typeof transport.onClose !== 'function' || typeof transport.close !== 'function') {
    throw new DeviceProtocolError('invalid_request', 'Invalid secure transport')
  }
  const bindingEpoch = options.bindingEpoch

  return await new Promise<ControllerHandshakeResult>((resolve, reject) => {
    let settled = false
    let helloSent = false
    let closed = false
    let frameReleased = false
    let flushing = false
    let overflowed = false
    let sink: ((frame: Uint8Array) => void) | null = null
    let frameListener: Dispose | null = null
    let queuedBytes = 0
    const queue: Uint8Array[] = []
    const closeSlot: HandshakeSlot = { dispose: null, released: false }
    const timerSlot: HandshakeSlot = { dispose: null, released: false }

    const observe = (action: () => void): void => {
      try { action() } catch (error) {
        try { options.onObserverError?.(error) } catch { /* 观察者异常不能破坏握手状态。 */ }
      }
    }
    const hold = (slot: HandshakeSlot, dispose: Dispose): void => {
      // 监听登记本身可能同步回调（对端已关闭）；此时不能把 disposer 挂在槽里。
      if (slot.released) observe(dispose)
      else slot.dispose = dispose
    }
    const release = (slot: HandshakeSlot): void => {
      slot.released = true
      const dispose = slot.dispose
      slot.dispose = null
      if (dispose) observe(dispose)
    }
    /** 唯一帧分发点的 off 入口：清掉暂存与 sink，并撤销传输上的监听；并发调用只生效一次。 */
    const dropFrameListener = (): void => {
      frameReleased = true
      const dispose = frameListener
      frameListener = null
      sink = null
      queue.length = 0
      queuedBytes = 0
      if (dispose) observe(dispose)
    }

    const deliver = (bytes: Uint8Array): void => {
      const listener = sink
      if (!listener) return
      observe(() => listener(bytes))
    }
    const enqueue = (bytes: Uint8Array): void => {
      if (queue.length >= HANDSHAKE_QUEUE_MAX_FRAMES || queuedBytes + bytes.byteLength > DEVICE_FRAME_MAX_BYTES) {
        // 有界队列溢出：宁可断开连接让上层按既有策略失败，也不乱序或静默丢帧。
        overflowed = true
        handoff.close()
        return
      }
      const copy = bytes.slice()
      queue.push(copy)
      queuedBytes += copy.byteLength
    }
    const flush = (): void => {
      flushing = true
      try {
        while (queue.length > 0) {
          if (overflowed || !sink) break
          const next = queue.shift()
          if (!next) break
          queuedBytes -= next.byteLength
          deliver(next)
        }
      } finally {
        flushing = false
      }
    }

    const fail = (error: DeviceProtocolError): void => {
      if (settled) return
      settled = true
      release(closeSlot)
      release(timerSlot)
      closed = true
      dropFrameListener()
      observe(() => transport.close())
      reject(error)
    }

    const succeed = (welcome: DeviceWelcome): void => {
      if (settled) return
      settled = true
      flushing = true
      // 先撤下握手用的关闭订阅与计时器；帧分发点留到 client 接管之后，保证紧随 welcome 的帧不乱序。
      release(closeSlot)
      release(timerSlot)
      let client: ControllerClient
      try {
        client = new ControllerClient({
          nextId: options.nextId,
          clock: options.clock,
          maxFrameBytes: Math.min(capabilities.data.maxFrameBytes, welcome.capabilities.maxFrameBytes),
          onResyncRequired: options.onResyncRequired,
          onObserverError: options.onObserverError
        })
        client.attach(welcome.binding, handoff)
      } catch {
        failAttach()
        return
      }
      flush()
      if (overflowed || closed) {
        // attach 期间的关闭同样不能被误报为握手成功。
        observe(() => client.detach())
        reject(new DeviceProtocolError(overflowed ? 'resource_limit' : 'disconnected', 'Handshake transport closed during handoff'))
        return
      }
      resolve({ client, welcome })
    }

    const failAttach = (): void => {
      closed = true
      dropFrameListener()
      observe(() => transport.close())
      reject(new DeviceProtocolError('internal', 'Failed to attach the controller client'))
    }

    const handshakeFrame = (bytes: Uint8Array): void => {
      let value: DeviceValue
      try {
        value = decodeDeviceValue(bytes)
      } catch {
        fail(new DeviceProtocolError('protocol_error', 'Malformed handshake frame'))
        return
      }
      if (typeof value !== 'object' || value === null || Array.isArray(value) || value instanceof Uint8Array) {
        fail(new DeviceProtocolError('protocol_error', 'Handshake frame must be a map'))
        return
      }
      if (value.type === 'handshake_error') {
        const parsed = deviceHandshakeErrorSchema.safeParse(value)
        if (!parsed.success) {
          fail(new DeviceProtocolError('protocol_error', 'Malformed handshake error'))
          return
        }
        fail(new DeviceProtocolError(mapHandshakeErrorCode(parsed.data.error.code), parsed.data.error.message))
        return
      }
      if (value.type !== 'welcome') {
        fail(new DeviceProtocolError('protocol_error', 'Unexpected frame during handshake'))
        return
      }
      if (value.v !== DEVICE_PROTOCOL_VERSION || value.protocol !== DEVICE_PROTOCOL_VERSION) {
        fail(new DeviceProtocolError('version_mismatch', `Server does not speak device protocol version ${DEVICE_PROTOCOL_VERSION}`))
        return
      }
      const parsed = deviceWelcomeSchema.safeParse(value)
      if (!parsed.success) {
        fail(new DeviceProtocolError('protocol_error', 'Invalid welcome frame'))
        return
      }
      const { binding } = parsed.data
      if (binding.deviceId !== expectedDeviceId || binding.controllerId !== expectedControllerId
        || binding.clientSessionId !== clientSessionId || binding.bindingEpoch !== bindingEpoch) {
        fail(new DeviceProtocolError('unauthorized', 'Welcome binding does not match the paired identity'))
        return
      }
      succeed(parsed.data)
    }

    const dispatch = (bytes: Uint8Array): void => {
      if (closed || frameReleased) return
      if (!settled) {
        if (!helloSent) { fail(new DeviceProtocolError('protocol_error', 'Peer answered before hello was sent')); return }
        handshakeFrame(bytes)
        return
      }
      if (flushing || !sink) enqueue(bytes)
      else deliver(bytes)
    }

    // 保留唯一的帧分发点：握手期间解析 welcome，成功后原地转交给 client 的接收器。
    const handoff: SecureTransport = {
      send: (frame) => transport.send(frame),
      onFrame: (listener) => {
        if (closed || frameReleased) return () => {}
        sink = listener
        return () => {
          if (sink !== listener) return
          dropFrameListener()
        }
      },
      onClose: (listener) => transport.onClose(() => { closed = true; listener() }),
      close: () => { closed = true; dropFrameListener(); transport.close() }
    }

    const onTimeout = (): void => { fail(new DeviceProtocolError('timeout', 'Handshake timed out')) }
    const onClose = (): void => { fail(new DeviceProtocolError('disconnected', 'Handshake transport closed')) }

    try {
      const disposeFrame = transport.onFrame(dispatch)
      // 登记期间可能同步失败，返回的 disposer 仍必须被消费。
      if (frameReleased) observe(disposeFrame)
      else frameListener = disposeFrame
    } catch {
      fail(new DeviceProtocolError('internal', 'Handshake listener registration failed'))
      return
    }
    // 关闭或超时可能在登记过程中同步触发：此时不再订阅、不再计时、不再发送 hello。
    if (settled) return
    try {
      hold(closeSlot, transport.onClose(onClose))
    } catch {
      fail(new DeviceProtocolError('internal', 'Handshake listener registration failed'))
      return
    }
    // 关闭订阅同样可能同步回调。
    if (settled) { release(closeSlot); return }
    try {
      hold(timerSlot, options.clock.schedule(onTimeout, timeoutMs))
    } catch {
      fail(new DeviceProtocolError('internal', 'Handshake scheduler failed before sending'))
      return
    }
    // 调度器也可能同步回调超时，留下的计时器必须撤销。
    if (settled) { release(timerSlot); return }

    const hello: DeviceHello = {
      v: DEVICE_PROTOCOL_VERSION, type: 'hello', versions: [DEVICE_PROTOCOL_VERSION],
      clientSessionId, bindingEpoch, capabilities: capabilities.data
    }
    let bytes: Uint8Array
    try {
      bytes = encodeDeviceFrame(hello)
    } catch {
      fail(new DeviceProtocolError('internal', 'Handshake frame could not be encoded'))
      return
    }
    try {
      // 先登记回程再发送，测试/本机传输可以同步回 welcome。
      helloSent = true
      void Promise.resolve(transport.send(bytes)).catch(() => {
        fail(new DeviceProtocolError('disconnected', 'Handshake transport failed'))
      })
    } catch {
      fail(new DeviceProtocolError('disconnected', 'Handshake transport failed'))
    }
  })
}
