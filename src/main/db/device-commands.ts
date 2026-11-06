import { createHash, randomUUID } from 'node:crypto'
import { encodeDeviceValue, decodeDeviceValue } from '../../shared/device/codec'
import { deviceRequestSchema, type DeviceRequest } from '../../shared/device/contract'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { DeviceValue } from '../../shared/device/value'
import type { DeviceExecutionContext, DeviceRequestContext } from '../services/request-context'
import { appendDeviceEvent, deviceScopeArgs, requireDeviceDatabase, requireDeviceDurability, type DeviceStorageScope } from './device-events'
import { stmt, tx } from './index'

export type DeviceCommandStatus = 'accepted' | 'running' | 'succeeded' | 'failed' | 'unknown' | 'interrupted'
export interface DeviceCommandReceipt {
  operationId: string
  key: string
  status: DeviceCommandStatus
  result: DeviceValue
  createdAt: number
  updatedAt: number
}
const states: readonly string[] = ['accepted', 'running', 'succeeded', 'failed', 'unknown', 'interrupted']

function receipt(row: Record<string, unknown>): DeviceCommandReceipt {
  if (row.status === 'accepting') throw new DeviceProtocolError('busy', 'Command acceptance is still in progress')
  if (!states.includes(String(row.status)) || !(row.result instanceof Uint8Array)) {
    throw new DeviceProtocolError('internal', 'Invalid stored command receipt')
  }
  return { operationId: String(row.operation_id), key: String(row.idempotency_key), status: row.status as DeviceCommandStatus,
    result: decodeDeviceValue(row.result), createdAt: Number(row.created_at), updatedAt: Number(row.updated_at) }
}
function lookup(scope: DeviceStorageScope, key: string): Record<string, unknown> | undefined {
  return stmt(`SELECT * FROM device_commands WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=? AND idempotency_key=?`)
    .get(...deviceScopeArgs(scope), key)
}

/** callback 只允许事务内的业务接受/入队，不得在其中启动网络或外部进程。 */
export function acceptDeviceCommand(
  context: DeviceRequestContext,
  input: DeviceRequest,
  accept: (operationId: string) => DeviceValue,
  now = Date.now()
): { replayed: boolean; receipt: DeviceCommandReceipt } {
  context.assertCurrent()
  const request = deviceRequestSchema.parse(input)
  context.assertRequest(request)
  if (request.idempotencyKey === undefined) throw new DeviceProtocolError('invalid_request', 'Mutations require an idempotency key')
  const key = request.idempotencyKey
  // params 包含方法级 expectedRevision 等前置条件；连接/requestId 不参与摘要。
  const digest = createHash('sha256').update(encodeDeviceValue({
    method: request.method, params: request.params
  })).digest('hex')
  requireDeviceDurability()
  return tx(() => {
    context.assertCurrent()
    const existing = lookup(context.binding, key)
    if (existing) {
      if (existing.method !== request.method || existing.params_digest !== digest) {
        throw new DeviceProtocolError('conflict', 'Idempotency key was used for a different command')
      }
      return { replayed: true, receipt: receipt(existing) }
    }
    const operationId = randomUUID()
    // 先占 key，防止 callback 同步重入同一个命令。accepting 不可跨事务可见。
    stmt(`INSERT INTO device_commands(device_id,data_epoch,config_scope,controller_id,idempotency_key,method,
      params_digest,operation_id,status,result,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?, 'accepting',NULL,?,?)`)
      .run(...deviceScopeArgs(context.binding), key, request.method, digest, operationId, now, now)
    const result = accept(operationId)
    const bytes = encodeDeviceValue(result)
    context.assertCurrent()
    stmt(`UPDATE device_commands SET status='accepted',result=? WHERE operation_id=?`).run(bytes, operationId)
    appendDeviceEvent(context.binding, 'command.accepted', { operationId, key }, now)
    return { replayed: false, receipt: { operationId, key, status: 'accepted' as const, result: decodeDeviceValue(bytes), createdAt: now, updatedAt: now } }
  })
}

export function getDeviceCommand(context: DeviceRequestContext, key: string): DeviceCommandReceipt | null {
  requireDeviceDatabase()
  context.assertCurrent()
  const row = lookup(context.binding, key)
  return row ? receipt(row) : null
}

const transitions: Record<DeviceCommandStatus, readonly DeviceCommandStatus[]> = {
  accepted: ['running', 'failed', 'interrupted'],
  running: ['succeeded', 'failed', 'unknown', 'interrupted'],
  succeeded: [], failed: [], unknown: [], interrupted: []
}

/** 终态及其事件同事务提交；未知结果不能被后续重试重新解释成 accepted。 */
export function transitionDeviceCommand(context: DeviceExecutionContext, key: string, expected: DeviceCommandStatus,
  next: DeviceCommandStatus, result: DeviceValue, now = Date.now()): DeviceCommandReceipt {
  context.assertCurrent()
  if (!transitions[expected]?.includes(next)) throw new DeviceProtocolError('conflict', 'Invalid command state transition')
  requireDeviceDurability()
  return tx(() => {
    const row = lookup(context.binding, key)
    if (!row) throw new DeviceProtocolError('not_found', 'Command does not exist')
    const current = receipt(row)
    if (current.status !== expected) throw new DeviceProtocolError('conflict', 'Command state changed')
    const bytes = encodeDeviceValue(result)
    context.assertCurrent()
    stmt(`UPDATE device_commands SET status=?,result=?,updated_at=? WHERE operation_id=? AND status=?`)
      .run(next, bytes, now, current.operationId, expected)
    appendDeviceEvent(context.binding, 'command.changed', { operationId: current.operationId, key, status: next }, now)
    return { ...current, status: next, result: decodeDeviceValue(bytes), updatedAt: now }
  })
}

/**
 * 服务启动时收尾上一次进程留下的未完成命令(任何控制端的、任何作用域的)。
 *
 * ★ `accepted` 说明**还没开始执行** → `interrupted`(确定没发生,控制端可以用新 key 重试);
 *   `running` 说明执行到一半进程没了 → `unknown`(可能已部分/全部生效,**不能**当成失败重试,
 *   也不能当成成功)。两者都是终态,绝不会被后续重放重新解释。
 * 返回被收尾的命令数。
 */
export function interruptStaleDeviceCommands(now = Date.now()): number {
  requireDeviceDurability()
  return tx(() => {
    const rows = stmt(`SELECT device_id,data_epoch,config_scope,controller_id,idempotency_key,operation_id,status
      FROM device_commands WHERE status IN ('accepted','running')`).all()
    const reason = encodeDeviceValue({ reason: 'service_restarted' })
    for (const row of rows) {
      const next: DeviceCommandStatus = row.status === 'running' ? 'unknown' : 'interrupted'
      stmt(`UPDATE device_commands SET status=?,result=?,updated_at=? WHERE operation_id=? AND status=?`)
        .run(next, reason, now, String(row.operation_id), String(row.status))
      appendDeviceEvent({ deviceId: String(row.device_id), dataEpoch: String(row.data_epoch),
        configScope: String(row.config_scope), controllerId: String(row.controller_id) },
      'command.changed', { operationId: String(row.operation_id), key: String(row.idempotency_key), status: next }, now)
    }
    return rows.length
  })
}
