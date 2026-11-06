import type { ControllerClient } from './client'
import type { Dispose } from './ports'
import { DeviceProtocolError } from '../shared/device/errors'

/**
 * 设备切换事务。控制端同一时刻只「指向」一台设备;切换时:
 *
 * - 世代(`epoch`)加一。所有进行中的调用都绑定了发起时的世代,**完成时世代变了就丢弃结果**
 *   (`stale_binding`)—— 迟到的保存/响应不会被当成当前设备的结果,更不会写到另一台设备上。
 * - 还在建立的旧连接被取消(`AbortSignal`);就算它无视取消、之后才连上,也会被立刻关闭,不被采用。
 * - 当前设备不在线时,调用**一律失败为 `disconnected`**,绝不悄悄改道到本机执行或另一台设备。
 *   界面据 `state` 把离线标明为只读。
 *
 * 纯逻辑,无宿主 API:桌面、手机共用。
 */
/**
 * 取消令牌。刻意不用 `AbortController`:这个包的类型环境不含宿主 API(要原样在手机端复用)。
 * 需要 `AbortSignal` 的连接器(Node 的直连)在自己的适配层里把它桥接过去即可。
 */
export interface CancelSignal {
  readonly aborted: boolean
  /** 被取消时调用一次;已经取消则立即调用。返回取消订阅。 */
  onAbort(listener: () => void): Dispose
}

class CancelToken implements CancelSignal {
  private done = false
  private readonly listeners = new Set<() => void>()
  get aborted(): boolean { return this.done }
  onAbort(listener: () => void): Dispose {
    if (this.done) { listener(); return () => {} }
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }
  abort(): void {
    if (this.done) return
    this.done = true
    const listeners = [...this.listeners]
    this.listeners.clear()
    for (const listener of listeners) { try { listener() } catch { /* 其余监听者仍需通知。 */ } }
  }
}

export interface ConnectedDevice {
  readonly client: ControllerClient
  /** 连接在没有主动关闭的情况下断掉(网络、对端重启、被撤销)。 */
  onLost(listener: () => void): Dispose
  close(): void
}

export interface DeviceConnector {
  connect(deviceId: string, signal: CancelSignal): Promise<ConnectedDevice>
}

export type SwitchState = 'idle' | 'connecting' | 'online' | 'offline'

export interface SwitchSnapshot {
  deviceId: string | null
  /** 每次 `select` 递增;所有结果都按它判断是否过期。 */
  epoch: number
  state: SwitchState
}

export class DeviceSwitcher {
  private deviceId: string | null = null
  private epoch = 0
  private state: SwitchState = 'idle'
  private connected: ConnectedDevice | null = null
  private abort: CancelToken | null = null
  private stopLost: Dispose | null = null
  private closed = false

  constructor(private readonly connector: DeviceConnector, private readonly onChange: (snapshot: SwitchSnapshot) => void = () => {}) {}

  snapshot(): SwitchSnapshot {
    return { deviceId: this.deviceId, epoch: this.epoch, state: this.state }
  }

  /** 切到某台设备(也用于对当前设备重连)。后一次 `select` 使前一次作废。 */
  async select(deviceId: string): Promise<void> {
    if (this.closed) throw new DeviceProtocolError('disconnected', 'The switcher is closed')
    const epoch = ++this.epoch
    this.releaseCurrent()
    this.deviceId = deviceId
    this.state = 'connecting'
    const abort = new CancelToken()
    this.abort = abort
    this.emit()

    let device: ConnectedDevice
    try {
      device = await this.connector.connect(deviceId, abort)
    } catch (error) {
      if (epoch !== this.epoch) throw new DeviceProtocolError('stale_binding', 'The device selection was superseded')
      this.abort = null
      this.state = 'offline'
      this.emit()
      throw error
    }
    if (epoch !== this.epoch || this.closed) {
      // 迟到的连接:不采用,立刻关闭。
      try { device.close() } catch { /* 已经关闭。 */ }
      throw new DeviceProtocolError('stale_binding', 'The device selection was superseded')
    }
    this.abort = null
    this.connected = device
    this.stopLost = device.onLost(() => {
      if (epoch !== this.epoch || this.connected !== device) return
      this.connected = null
      this.stopLost = null
      this.state = 'offline'
      this.emit()
    })
    this.state = 'online'
    this.emit()
  }

  /**
   * 在当前设备上做一件事。结果只在「发起它的那个世代仍然是当前」时才返回,
   * 否则抛 `stale_binding` —— 调用方不得把它当成当前设备的数据。
   */
  async run<T>(work: (client: ControllerClient) => Promise<T>): Promise<T> {
    const epoch = this.epoch
    const device = this.connected
    if (this.state !== 'online' || device === null) {
      throw new DeviceProtocolError('disconnected', 'The selected device is not connected')
    }
    let result: T
    try {
      result = await work(device.client)
    } catch (error) {
      if (epoch !== this.epoch) throw new DeviceProtocolError('stale_binding', 'The device selection changed during the call')
      throw error
    }
    if (epoch !== this.epoch) throw new DeviceProtocolError('stale_binding', 'The device selection changed during the call')
    return result
  }

  /**
   * 回到「不指向任何设备」(本机)。与 `close()` 不同:切换器仍可用,之后还能 `select`。
   * 进行中的连接被取消、世代递增,所以迟到的结果不会被采用。
   */
  deselect(): void {
    if (this.closed) return
    this.epoch++
    this.releaseCurrent()
    this.state = 'idle'
    this.deviceId = null
    this.emit()
  }

  /** 断开当前连接并停用。幂等。 */
  close(): void {
    if (this.closed) return
    this.closed = true
    this.epoch++
    this.releaseCurrent()
    this.state = 'idle'
    this.deviceId = null
    this.emit()
  }

  private releaseCurrent(): void {
    this.abort?.abort()
    this.abort = null
    this.stopLost?.()
    this.stopLost = null
    const device = this.connected
    this.connected = null
    if (device !== null) { try { device.client.detach(); device.close() } catch { /* 已经关闭。 */ } }
  }

  private emit(): void {
    try { this.onChange(this.snapshot()) } catch { /* 观察者异常不得改变切换结果。 */ }
  }
}
