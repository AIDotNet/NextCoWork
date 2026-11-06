import { deviceBindingSchema, sameDeviceBinding, type DeviceBinding } from '../../shared/device/identity'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { DeviceRequest } from '../../shared/device/contract'
import type { DeviceValue } from '../../shared/device/value'

/** 由认证后的连接创建，禁止从请求体里的 actor/权限字段构造。 */
/**
 * 一条已认证连接的推送口。只有网关能造它:方法处理器拿到的是**当前这条连接**,
 * 不能凭请求里的字段去推给别的连接。
 */
export interface DeviceConnectionPort {
  /** 登记(或替换)一个订阅;连接关闭时自动释放。超出每连接上限抛 resource_limit。 */
  subscribe(key: string, dispose: () => void): void
  unsubscribe(key: string): void
  /** 推一帧事件。`previousCursor` 由调用方保证与上一帧衔接,控制端据此发现缺口并 resync。
   * 返回 `too_large` 表示这一帧超过协商上限、没有发送,调用方可拆小重试。 */
  push(event: { streamId: string; cursor: string; previousCursor: string | null; event: string; data: DeviceValue }): 'sent' | 'too_large' | 'closed'
}

export interface DeviceAuthority {
  controllerId: string
  /** 仅网关构造的请求上下文带有;测试/内部上下文可为空。 */
  connection?: DeviceConnectionPort
  currentBinding(): DeviceBinding
  revoked(): boolean
  allows(method: string): boolean
}

export class DeviceRequestContext {
  readonly binding: DeviceBinding
  private readonly method: string

  constructor(private readonly authority: DeviceAuthority, request: DeviceRequest) {
    this.binding = Object.freeze(deviceBindingSchema.parse(request.binding))
    this.method = request.method
    this.assertCurrent()
  }

  /** 发起这次请求的那条连接。没有时(内部上下文)订阅类方法必须拒绝。 */
  get connection(): DeviceConnectionPort {
    const connection = this.authority.connection
    if (connection === undefined) throw new DeviceProtocolError('internal', 'Request has no connection')
    return connection
  }

  assertRequest(request: DeviceRequest): void {
    this.assertCurrent()
    if (request.method !== this.method || !sameDeviceBinding(request.binding, this.binding)) {
      throw new DeviceProtocolError('stale_binding', 'Request does not belong to this authorization context')
    }
  }

  /** 异步操作提交前仍需调用，避免等待期间撤销或切作用域后继续写入。 */
  assertCurrent(): void {
    if (this.authority.revoked() || this.authority.controllerId !== this.binding.controllerId) {
      throw new DeviceProtocolError('unauthorized', 'Controller is not authorized')
    }
    if (!sameDeviceBinding(this.binding, this.authority.currentBinding())) {
      throw new DeviceProtocolError('stale_binding', 'Device binding changed')
    }
    if (!this.authority.allows(this.method)) throw new DeviceProtocolError('forbidden', 'Operation is not allowed')
  }
}

export type DeviceExecutionScope = Pick<DeviceBinding, 'deviceId' | 'serverEpoch' | 'dataEpoch' | 'configScope' | 'scopeEpoch'>

/**
 * 已接受任务由服务执行器持有，不能再以发起它的控制台连接是否存活作为提交条件。
 * 这是内部执行端口，不得从网络请求体直接构造或作为一个公开 RPC 暴露。
 */
export class DeviceExecutionContext {
  readonly binding: DeviceBinding

  constructor(binding: DeviceBinding, private readonly currentScope: () => DeviceExecutionScope) {
    this.binding = Object.freeze(deviceBindingSchema.parse(binding))
    this.assertCurrent()
  }

  assertCurrent(): void {
    const current = this.currentScope()
    const expected = this.binding
    if (current.deviceId !== expected.deviceId || current.serverEpoch !== expected.serverEpoch
      || current.dataEpoch !== expected.dataEpoch || current.configScope !== expected.configScope
      || current.scopeEpoch !== expected.scopeEpoch) {
      throw new DeviceProtocolError('stale_binding', 'Device execution scope changed')
    }
  }
}
