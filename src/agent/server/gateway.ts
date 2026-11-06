import type { ClientClock, Dispose, SecureTransport } from '../../controller-client/ports'
import { performance } from 'node:perf_hooks'
import { DEVICE_FRAME_MAX_BYTES, DEVICE_PROTOCOL_VERSION, serverCapabilitiesSchema, type ServerCapabilities } from '../../shared/device/capabilities'
import { decodeDeviceFrame, encodeDeviceFrame } from '../../shared/device/codec'
import type { DeviceFrame, DeviceHello, DeviceRequest } from '../../shared/device/contract'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { DeviceValue } from '../../shared/device/value'
import { deviceBindingSchema, deviceIdSchema, type DeviceBinding } from '../../shared/device/identity'
import { toDeviceError } from '../../main/services/errors'
import { DeviceRequestContext, type DeviceConnectionPort, type DeviceExecutionScope } from '../../main/services/request-context'
import type { DeviceRpcRouter } from './rpc'

const MAX_SUBSCRIPTIONS = 64

/** 必须来自已认证的传输与服务端信任记录，不得由 hello 中的字段创建。 */
export interface AuthenticatedDevicePeer {
  controllerId: string
  revoked(): boolean
  allows(method: string): boolean
}

export interface DeviceGatewayOptions {
  peer: AuthenticatedDevicePeer
  currentScope(): DeviceExecutionScope
  capabilities: ServerCapabilities
  router: DeviceRpcRouter
  clock: ClientClock
  handshakeTimeoutMs?: number
  maxInFlight?: number
  /** 单连接速率预算；服务进程另需限制总连接数。 */
  requestsPerSecond?: number
  burstRequests?: number
  now?: () => number
  onClosed?: () => void
}

/** 一条已认证连接的协议状态机；close 只释放连接，不停止设备中的 Agent。 */
export class DeviceGatewayConnection {
  private closed = false
  private binding: DeviceBinding | null = null
  private frameLimit: number
  private readonly controllerId: string
  private readonly capabilities: ServerCapabilities
  private readonly maxInFlight: number
  private readonly requestsPerSecond: number
  private readonly burstRequests: number
  private tokens: number
  private lastTokenAt: number
  private readonly now: () => number
  private readonly pending = new Set<string>()
  private readonly disposers: Dispose[] = []
  private cancelHandshake: Dispose | null = null
  private readonly subscriptions = new Map<string, Dispose>()
  private readonly port: DeviceConnectionPort = {
    subscribe: (key, dispose) => this.subscribe(key, dispose),
    unsubscribe: (key) => this.unsubscribe(key),
    push: (event) => this.pushEvent(event)
  }

  constructor(private readonly transport: SecureTransport, private readonly options: DeviceGatewayOptions) {
    this.controllerId = deviceIdSchema.parse(options.peer.controllerId)
    this.capabilities = serverCapabilitiesSchema.parse(options.capabilities)
    this.frameLimit = this.capabilities.maxFrameBytes
    this.maxInFlight = options.maxInFlight ?? 32
    this.requestsPerSecond = options.requestsPerSecond ?? 100
    this.burstRequests = options.burstRequests ?? 200
    this.tokens = this.burstRequests
    this.now = options.now ?? (() => performance.now())
    this.lastTokenAt = this.now()
    const timeout = options.handshakeTimeoutMs ?? 10_000
    if (!Number.isSafeInteger(this.maxInFlight) || this.maxInFlight < 1 || this.maxInFlight > 64
      || !Number.isSafeInteger(this.requestsPerSecond) || this.requestsPerSecond < 1 || this.requestsPerSecond > 1000
      || !Number.isSafeInteger(this.burstRequests) || this.burstRequests < 1 || this.burstRequests > 2000
      || !Number.isFinite(this.lastTokenAt)
      || !Number.isSafeInteger(timeout) || timeout < 1 || timeout > 60_000) {
      throw new DeviceProtocolError('invalid_request', 'Invalid gateway limits')
    }
    const retain = (dispose: Dispose): void => {
      if (this.closed) { try { dispose() } catch { /* 已关闭。 */ } }
      else this.disposers.push(dispose)
    }
    try {
      retain(transport.onClose(() => this.close()))
      if (this.closed) return
      retain(transport.onFrame((bytes) => this.receive(bytes)))
      if (this.closed || this.binding !== null) return
      const cancel = options.clock.schedule(() => this.close(), timeout)
      if (this.closed || this.binding !== null) cancel()
      else this.cancelHandshake = cancel
    } catch (error) {
      this.close()
      throw error
    }
  }

  close(): void {
    if (this.closed) return
    this.closed = true
    const subscriptions = [...this.subscriptions.values()]
    this.subscriptions.clear()
    const actions = [this.cancelHandshake, ...subscriptions, ...this.disposers.splice(0), () => this.transport.close(), this.options.onClosed]
    this.cancelHandshake = null
    this.pending.clear()
    for (const action of actions) { try { action?.() } catch { /* 每个资源都必须独立释放。 */ } }
  }

  private receive(bytes: Uint8Array): void {
    if (this.closed) return
    const now = this.now()
    if (!Number.isFinite(now)) { this.close(); return }
    this.tokens = Math.min(this.burstRequests, this.tokens + Math.max(0, now - this.lastTokenAt) * this.requestsPerSecond / 1000)
    this.lastTokenAt = Math.max(this.lastTokenAt, now)
    if (this.tokens < 1) { this.close(); return }
    this.tokens--
    let frame: DeviceFrame
    try {
      if (bytes.byteLength > this.frameLimit || bytes.byteLength > DEVICE_FRAME_MAX_BYTES) {
        throw new DeviceProtocolError('resource_limit', 'Frame is too large')
      }
      frame = decodeDeviceFrame(bytes)
    } catch { this.close(); return }
    if (this.binding === null) {
      if (frame.type !== 'hello') { this.close(); return }
      this.hello(frame)
      return
    }
    if (frame.type !== 'request') { this.close(); return }
    if (this.pending.has(frame.id) || this.pending.size >= this.maxInFlight) { this.close(); return }
    this.pending.add(frame.id)
    void this.request(frame).catch(() => this.close()).finally(() => this.pending.delete(frame.id))
  }

  private hello(frame: DeviceHello): void {
    try {
      if (this.options.peer.revoked()) throw new DeviceProtocolError('unauthorized', 'Controller is not paired')
      if (!frame.versions.includes(DEVICE_PROTOCOL_VERSION)) throw new DeviceProtocolError('version_mismatch', 'No compatible device protocol')
      this.binding = Object.freeze(deviceBindingSchema.parse({
        ...this.options.currentScope(), controllerId: this.controllerId,
        clientSessionId: frame.clientSessionId, bindingEpoch: frame.bindingEpoch
      }))
      this.frameLimit = Math.min(frame.capabilities.maxFrameBytes, this.capabilities.maxFrameBytes)
      this.cancelHandshake?.()
      this.cancelHandshake = null
      void this.send({ v: 1, type: 'welcome', protocol: DEVICE_PROTOCOL_VERSION,
        binding: this.binding, capabilities: this.capabilities }).catch(() => this.close())
    } catch (error) {
      void this.send({ v: 1, type: 'handshake_error', error: toDeviceError(error) })
        .catch(() => {}).finally(() => this.close())
    }
  }

  private async request(request: DeviceRequest): Promise<void> {
    const binding = this.binding!
    try {
      const context = new DeviceRequestContext({
        controllerId: this.controllerId,
        connection: this.port,
        currentBinding: () => ({ ...binding, ...this.options.currentScope() }),
        revoked: () => this.closed || this.options.peer.revoked(),
        allows: (method) => this.options.peer.allows(method)
      }, request)
      const data = await this.options.router.dispatch(context, request, this.options.currentScope, this.frameLimit)
      context.assertCurrent()
      await this.send({ v: 1, type: 'response', id: request.id, binding, ok: true, data })
    } catch (error) {
      // 永远使用服务端的绑定，不回显请求中伪造的身份。
      await this.send({ v: 1, type: 'response', id: request.id, binding, ok: false, error: toDeviceError(error) })
    }
  }

  private subscribe(key: string, dispose: Dispose): void {
    if (this.closed) { try { dispose() } catch { /* 连接已关闭。 */ } throw new DeviceProtocolError('disconnected', 'Connection is closed') }
    const previous = this.subscriptions.get(key)
    if (previous === undefined && this.subscriptions.size >= MAX_SUBSCRIPTIONS) {
      try { dispose() } catch { /* 拒绝的订阅要释放。 */ }
      throw new DeviceProtocolError('resource_limit', 'Too many subscriptions on this connection')
    }
    this.subscriptions.set(key, dispose)
    if (previous !== undefined) { try { previous() } catch { /* 被替换的订阅独立释放。 */ } }
  }

  private unsubscribe(key: string): void {
    const dispose = this.subscriptions.get(key)
    this.subscriptions.delete(key)
    if (dispose !== undefined) { try { dispose() } catch { /* 释放失败不影响连接。 */ } }
  }

  /**
   * 推一帧事件。撤销之后绝不再推(并断开);帧超过协商上限时跳过 ——
   * 控制端会在下一帧发现游标缺口并 resync,而不是收到被截断的数据。
   */
  private pushEvent(event: { streamId: string; cursor: string; previousCursor: string | null; event: string; data: DeviceValue }): 'sent' | 'too_large' | 'closed' {
    const binding = this.binding
    if (this.closed || binding === null) return 'closed'
    if (this.options.peer.revoked()) { this.close(); return 'closed' }
    let bytes: Uint8Array
    try { bytes = encodeDeviceFrame({ v: 1, type: 'event', binding, ...event }) } catch { return 'too_large' }
    if (bytes.byteLength > this.frameLimit) return 'too_large'
    void (async () => { try { await this.transport.send(bytes) } catch { this.close() } })()
    return 'sent'
  }

  private async send(frame: DeviceFrame): Promise<void> {
    if (this.closed) return
    const bytes = encodeDeviceFrame(frame)
    if (bytes.byteLength > this.frameLimit) { this.close(); return }
    await this.transport.send(bytes)
  }
}
