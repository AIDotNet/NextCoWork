import { decodeDeviceFrame, encodeDeviceFrame } from '../shared/device/codec'
import type { DeviceEvent, DeviceRequest } from '../shared/device/contract'
import { deviceBindingSchema, deviceIdSchema, sameDeviceBinding, type DeviceBinding } from '../shared/device/identity'
import { DeviceProtocolError, type DeviceErrorCode } from '../shared/device/errors'
import type { DeviceValue } from '../shared/device/value'
import type { ClientClock, Dispose, SecureTransport } from './ports'
import { DEVICE_FRAME_MAX_BYTES } from '../shared/device/capabilities'

export class DeviceRequestError extends DeviceProtocolError {
  constructor(code: DeviceErrorCode, message: string, readonly requestId: string, readonly idempotencyKey?: string) {
    super(code, message)
    this.name = 'DeviceRequestError'
  }
}

interface Pending {
  binding: DeviceBinding
  key?: string
  resolve(value: DeviceValue): void
  reject(error: DeviceRequestError): void
  cancelTimer: Dispose
}
interface Connection {
  binding: DeviceBinding
  transport: SecureTransport
  dispose: Dispose[]
}
interface Stream {
  cursor: string | null
  lastPrevious?: string | null
  stale: boolean
  listener(event: DeviceEvent): void
}

/** 只处理已认证连接上的请求/绑定/游标；不依赖窗口，也不在重连时重放命令。 */
export class ControllerClient {
  private connection: Connection | null = null
  private requestSequence = 0
  private readonly pending = new Map<string, Pending>()
  private readonly streams = new Map<string, Stream>()

  constructor(private readonly options: {
    nextId(): string
    clock: ClientClock
    maxFrameBytes?: number
    onResyncRequired?: (streamId: string) => void
    onObserverError?: (error: unknown) => void
  }) {
    const limit = options.maxFrameBytes ?? DEVICE_FRAME_MAX_BYTES
    if (!Number.isSafeInteger(limit) || limit < 4096 || limit > DEVICE_FRAME_MAX_BYTES) {
      throw new DeviceProtocolError('invalid_request', 'Invalid negotiated frame limit')
    }
  }

  attach(binding: DeviceBinding, transport: SecureTransport): void {
    const checked = deviceBindingSchema.parse(binding)
    this.detach()
    const connection: Connection = { binding: checked, transport, dispose: [] }
    this.connection = connection
    const retain = (dispose: Dispose): void => {
      if (this.connection === connection) connection.dispose.push(dispose)
      else this.observe(dispose)
    }
    try {
      retain(transport.onFrame((frame) => {
        if (this.connection === connection) this.receive(frame, connection)
      }))
      if (this.connection !== connection) return
      retain(transport.onClose(() => {
        if (this.connection === connection) this.detach()
      }))
    } catch (error) {
      if (this.connection === connection) this.detach()
      throw error
    }
  }

  detach(): void {
    const connection = this.connection
    this.connection = null
    this.streams.clear()
    const pendingRequests = [...this.pending]
    this.pending.clear()
    for (const [id, pending] of pendingRequests) {
      this.observe(pending.cancelTimer)
      pending.reject(new DeviceRequestError(pending.key ? 'result_unknown' : 'disconnected',
        pending.key ? 'Connection ended; query the command receipt before retrying' : 'Connection ended', id, pending.key))
    }
    for (const dispose of connection?.dispose ?? []) this.observe(dispose)
    if (connection) this.observe(() => connection.transport.close())
  }

  query(method: string, params: DeviceValue, timeoutMs = 30_000): Promise<DeviceValue> {
    return this.request(method, params, undefined, timeoutMs)
  }

  /** key 必须由调用方保存；断线后用同一个 key 查询回执，不能自动换 key 重发。 */
  command(method: string, params: DeviceValue, idempotencyKey: string, timeoutMs = 30_000): Promise<DeviceValue> {
    return this.request(method, params, idempotencyKey, timeoutMs)
  }

  /** 在取得服务端快照/订阅回执后登记其游标；这里不替代服务端订阅授权。 */
  follow(streamId: string, cursor: string | null, listener: (event: DeviceEvent) => void): Dispose {
    deviceIdSchema.parse(streamId)
    if (cursor !== null && (cursor.length === 0 || cursor.length > 128)) throw new DeviceProtocolError('invalid_request', 'Invalid stream cursor')
    if (!this.connection) throw new DeviceProtocolError('disconnected', 'No active device connection')
    if (!this.streams.has(streamId) && this.streams.size >= 128) throw new DeviceProtocolError('resource_limit', 'Too many streams')
    const stream: Stream = { cursor, stale: false, listener }
    this.streams.set(streamId, stream)
    return () => { if (this.streams.get(streamId) === stream) this.streams.delete(streamId) }
  }

  private async request(method: string, params: DeviceValue, key: string | undefined, timeoutMs: number): Promise<DeviceValue> {
    const connection = this.connection
    if (!connection) throw new DeviceProtocolError('disconnected', 'No active device connection')
    if (this.pending.size >= 64) throw new DeviceProtocolError('resource_limit', 'Too many pending requests')
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 600_000) throw new DeviceProtocolError('invalid_request', 'Invalid request timeout')
    if (key !== undefined) deviceIdSchema.parse(key)
    if (this.requestSequence >= Number.MAX_SAFE_INTEGER) throw new DeviceProtocolError('resource_limit', 'Request sequence exhausted')
    const prefix = deviceIdSchema.parse(this.options.nextId()).slice(0, 96)
    // 调用方的 ID 生成器即使在重连时重置，也不能让旧回包命中新请求。
    const id = `${prefix}:${(++this.requestSequence).toString(36)}`
    if (this.pending.has(id)) throw new DeviceProtocolError('conflict', 'Duplicate request id')
    const request: DeviceRequest = {
      v: 1, type: 'request', id, binding: { ...connection.binding }, method, params,
      ...(key === undefined ? {} : { idempotencyKey: key })
    }
    const bytes = encodeDeviceFrame(request)
    if (bytes.byteLength > (this.options.maxFrameBytes ?? DEVICE_FRAME_MAX_BYTES)) {
      throw new DeviceProtocolError('resource_limit', 'Request exceeds the negotiated frame limit')
    }
    return new Promise<DeviceValue>((resolve, reject) => {
      const pending: Pending = { binding: connection.binding, key, resolve, reject, cancelTimer: () => {} }
      this.pending.set(id, pending)
      const fail = (code: DeviceErrorCode, message: string): void => {
        if (this.pending.get(id) !== pending) return
        this.pending.delete(id)
        this.observe(pending.cancelTimer)
        reject(new DeviceRequestError(code, message, id, key))
      }
      try {
        const cancel = this.options.clock.schedule(() => {
          fail(key === undefined ? 'timeout' : 'result_unknown', key === undefined ? 'Request timed out' : 'Command result is unknown; query its receipt')
        }, timeoutMs)
        pending.cancelTimer = cancel
        if (this.pending.get(id) !== pending) { this.observe(cancel); return }
      } catch {
        fail('internal', 'Request scheduler failed before sending')
        return
      }
      try {
        // 先登记回程再发送，测试/本机传输可以同步回包。
        if (this.pending.get(id) !== pending || this.connection !== connection) return
        void Promise.resolve(connection.transport.send(bytes)).catch(() => {
          fail(key === undefined ? 'disconnected' : 'result_unknown', 'Transport failed')
        })
      } catch {
        fail(key === undefined ? 'disconnected' : 'result_unknown', 'Transport failed')
      }
    })
  }

  private receive(bytes: Uint8Array, connection: Connection): void {
    let frame: ReturnType<typeof decodeDeviceFrame>
    try {
      if (bytes.byteLength > (this.options.maxFrameBytes ?? DEVICE_FRAME_MAX_BYTES)) {
        throw new DeviceProtocolError('resource_limit', 'Response exceeds the negotiated frame limit')
      }
      frame = decodeDeviceFrame(bytes)
      if (frame.type !== 'response' && frame.type !== 'event') throw new DeviceProtocolError('protocol_error', 'Unexpected frame direction')
      if (!sameDeviceBinding(frame.binding, connection.binding)) throw new DeviceProtocolError('stale_binding', 'Frame belongs to another device binding')
    } catch {
      this.detach()
      return
    }
    if (frame.type === 'response') {
      const pending = this.pending.get(frame.id)
      if (!pending || !sameDeviceBinding(pending.binding, frame.binding)) return
      this.pending.delete(frame.id)
      this.observe(pending.cancelTimer)
      if (frame.ok) pending.resolve(frame.data)
      else pending.reject(new DeviceRequestError(frame.error.code, frame.error.message, frame.id, pending.key))
      return
    }
    const stream = this.streams.get(frame.streamId)
    if (!stream || stream.stale) return
    const resync = (): void => {
      stream.stale = true
      this.observe(() => this.options.onResyncRequired?.(frame.streamId))
    }
    if (stream.cursor === frame.cursor) {
      // 快照游标没有前驱信息；已实际交付的事件则必须保持同一前驱。
      if (stream.lastPrevious !== undefined && stream.lastPrevious !== frame.previousCursor) resync()
      return
    }
    if (frame.previousCursor !== stream.cursor) { resync(); return }
    const before = stream.cursor
    stream.cursor = frame.cursor
    stream.lastPrevious = frame.previousCursor
    try { stream.listener(frame) } catch (error) {
      stream.cursor = before
      if (this.streams.get(frame.streamId) === stream) resync()
      this.observe(() => this.options.onObserverError?.(error))
    }
  }

  private observe(callback: () => void): void {
    try { callback() } catch (error) {
      try { this.options.onObserverError?.(error) } catch { /* 观察者异常不能破坏连接状态。 */ }
    }
  }
}
