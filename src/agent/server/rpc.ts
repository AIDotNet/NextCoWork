import type { z } from 'zod'
import type { DeviceRequest } from '../../shared/device/contract'
import { encodeDeviceFrame } from '../../shared/device/codec'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'
import { toDeviceError } from '../../main/services/errors'
import { assertDeviceValue, type DeviceValue } from '../../shared/device/value'
import { acceptDeviceCommand, transitionDeviceCommand } from '../../main/db/device-commands'
import { tx } from '../../main/db'
import { requireDeviceDurability } from '../../main/db/device-events'
import { DeviceExecutionContext, type DeviceExecutionScope, type DeviceRequestContext } from '../../main/services/request-context'

interface RegisteredMethod {
  kind: 'query' | 'transaction' | 'execution'
  invoke(context: DeviceRequestContext, params: DeviceValue, operationId?: string): DeviceValue | Promise<DeviceValue>
  /** execution 专用:接受回执之前先校验参数;失败不留下回执。 */
  validate?(params: DeviceValue): unknown
  /** execution 专用:回执被接受之后由服务端自己执行,不依赖发起连接是否还在。 */
  execute?(context: DeviceExecutionContext, params: unknown, operationId: string): Promise<DeviceValue>
}

/** 显式登记的业务方法才可调用；IPC 边界分类表不是网络白名单。 */
export class DeviceRpcRouter {
  private readonly methods = new Map<string, RegisteredMethod>()

  query<Input, Output>(name: string, input: z.ZodType<Input>, output: z.ZodType<Output>,
    handler: (context: DeviceRequestContext, params: Input) => Output | Promise<Output>): void {
    this.register(name, { kind: 'query', invoke: async (context, params) => {
      const parsed = input.safeParse(params)
      if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid method parameters')
      const result = await handler(context, parsed.data)
      context.assertCurrent()
      return checkedOutput(output, result)
    } })
  }

  /** 仅允许同步、事务内的数据库变更。进程/网络/Agent 启动必须由独立执行器处理。 */
  transaction<Input, Output>(name: string, input: z.ZodType<Input>, output: z.ZodType<Output>,
    handler: (context: DeviceRequestContext, params: Input, operationId: string) => Output): void {
    this.register(name, { kind: 'transaction', invoke: (context, params, operationId) => {
      const parsed = input.safeParse(params)
      if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid method parameters')
      if (operationId === undefined) throw new DeviceProtocolError('internal', 'Mutation has no operation identity')
      return checkedOutput(output, handler(context, parsed.data, operationId))
    } })
  }

  /**
   * 需要进程/网络/Agent 启动的命令。流程:校验参数 → 同事务落「已接受」回执 →
   * 以 `DeviceExecutionContext` 执行(连接断开、控制端被撤销都不会中止已接受的任务)→
   * 写终态。重放同一个 key 只返回已有回执,绝不重复执行;执行中崩溃的回执由
   * `interruptStaleDeviceCommands` 在下次启动时标成 interrupted / unknown。
   */
  execution<Input, Output>(name: string, input: z.ZodType<Input>, output: z.ZodType<Output>,
    handler: (context: DeviceExecutionContext, params: Input, operationId: string) => Output | Promise<Output>): void {
    this.register(name, {
      kind: 'execution',
      invoke: () => { throw new DeviceProtocolError('internal', 'Execution methods are dispatched separately') },
      validate: (params) => {
        const parsed = input.safeParse(params)
        if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid method parameters')
        return parsed.data
      },
      execute: async (context, params, operationId) => checkedOutput(output, await handler(context, params as Input, operationId))
    })
  }

  async dispatch(context: DeviceRequestContext, request: DeviceRequest,
    currentScope: () => DeviceExecutionScope, maxFrameBytes = DEVICE_FRAME_MAX_BYTES): Promise<DeviceValue> {
    context.assertRequest(request)
    if (!Number.isSafeInteger(maxFrameBytes) || maxFrameBytes < 4096 || maxFrameBytes > DEVICE_FRAME_MAX_BYTES) {
      throw new DeviceProtocolError('invalid_request', 'Invalid RPC response limit')
    }
    const checkReply = (data: DeviceValue): DeviceValue => {
      const frame = encodeDeviceFrame({ v: 1, type: 'response', id: request.id, binding: request.binding, ok: true, data })
      if (frame.byteLength > maxFrameBytes) throw new DeviceProtocolError('resource_limit', 'RPC response exceeds the negotiated limit')
      return data
    }
    const method = this.methods.get(request.method)
    if (!method) throw new DeviceProtocolError('not_found', 'Method is not registered')
    if (method.kind === 'query') {
      if (request.idempotencyKey !== undefined) throw new DeviceProtocolError('invalid_request', 'Queries do not accept a command key')
      return checkReply(await method.invoke(context, request.params))
    }
    // 必须先检查显式文件库与耐久性，不能由 tx() 悄悄打开内存兜底库。
    requireDeviceDurability()
    if (method.kind === 'execution') return checkReply(await this.runExecution(method, context, request, currentScope))
    // 接受回执、业务写入和终态同一事务，不能让已完成的配置变更在崩溃后看起来未执行。
    return tx(() => {
      const accepted = acceptDeviceCommand(context, request, (operationId) => {
        const result = method.invoke(context, request.params, operationId)
        assertDeviceValue(result) // Promise/类实例不可从同步事务逃逸。
        return result
      })
      let receipt = accepted.receipt
      if (!accepted.replayed) {
        const execution = new DeviceExecutionContext(context.binding, currentScope)
        transitionDeviceCommand(execution, receipt.key, 'accepted', 'running', receipt.result)
        receipt = transitionDeviceCommand(execution, receipt.key, 'running', 'succeeded', receipt.result)
      }
      context.assertCurrent()
      // 在提交事务之前确认回执能编码/发送；否则业务写入和回执一起回滚。
      return checkReply({ ...receipt, replayed: accepted.replayed })
    })
  }

  private async runExecution(method: RegisteredMethod, context: DeviceRequestContext, request: DeviceRequest,
    currentScope: () => DeviceExecutionScope): Promise<DeviceValue> {
    const params = method.validate!(request.params)
    const accepted = acceptDeviceCommand(context, request, () => null)
    if (accepted.replayed) return { ...accepted.receipt, replayed: true }
    // 从这里开始不再看 context:接受之后的执行不依赖控制端存活。
    const execution = new DeviceExecutionContext(context.binding, currentScope)
    const key = accepted.receipt.key
    transitionDeviceCommand(execution, key, 'accepted', 'running', null)
    let receipt
    try {
      const result = await method.execute!(execution, params, accepted.receipt.operationId)
      receipt = transitionDeviceCommand(execution, key, 'running', 'succeeded', result)
    } catch (error) {
      try {
        receipt = transitionDeviceCommand(execution, key, 'running', 'failed', { error: toDeviceError(error) as unknown as DeviceValue })
      } catch {
        // 连终态都写不下:保持 running,下次启动会把它标成 unknown,而不是谎报成功或失败。
        throw error
      }
    }
    return { ...receipt, replayed: false }
  }

  private register(name: string, method: RegisteredMethod): void {
    if (!/^[a-z][A-Za-z0-9]*(?:[.:][A-Za-z][A-Za-z0-9]*)+$/.test(name) || name.length > 128) {
      throw new DeviceProtocolError('invalid_request', 'Invalid method name')
    }
    if (this.methods.has(name)) throw new DeviceProtocolError('conflict', 'Method is already registered')
    this.methods.set(name, method)
  }
}

function checkedOutput<Output>(schema: z.ZodType<Output>, output: unknown): DeviceValue {
  if (output !== null && (typeof output === 'object' || typeof output === 'function')
    && typeof (output as { then?: unknown }).then === 'function') {
    // 异步续体仍会被数据库事务 owner 闸拒绝；消费拒绝，避免再制造 unhandled rejection。
    void Promise.resolve(output).catch(() => {})
    throw new DeviceProtocolError('internal', 'A synchronous mutation returned a Promise')
  }
  const parsed = schema.safeParse(output)
  if (!parsed.success) throw new DeviceProtocolError('internal', 'Invalid method result')
  assertDeviceValue(parsed.data)
  return parsed.data
}
