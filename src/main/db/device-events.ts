import { encodeDeviceValue, decodeDeviceValue } from '../../shared/device/codec'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { DeviceBinding } from '../../shared/device/identity'
import type { DeviceValue } from '../../shared/device/value'
import { db, databaseFilePath, stmt } from './index'

export type DeviceStorageScope = Pick<DeviceBinding, 'deviceId' | 'dataEpoch' | 'configScope' | 'controllerId'>

/** 关键状态与 outbox 的确认不能承诺超过数据库实际配置的耐久性。 */
export function requireDeviceDatabase(): void {
  if (databaseFilePath() === null) {
    throw new DeviceProtocolError('internal', 'Device persistence requires an explicitly opened file database')
  }
}

export function requireDeviceDurability(): void {
  requireDeviceDatabase()
  const row = db().prepare('PRAGMA synchronous').get()
  if (Number(row?.synchronous) < 2) {
    throw new DeviceProtocolError('internal', 'Durable device writes require synchronous FULL')
  }
}

export function deviceScopeArgs(scope: DeviceStorageScope): [string, string, string, string] {
  return [scope.deviceId, scope.dataEpoch, scope.configScope, scope.controllerId]
}

export interface StoredDeviceEvent {
  cursor: string
  event: string
  data: DeviceValue
  at: number
}

/** 只能在业务事务内追加；网络投递必须等外层 commit 成功。 */
export function appendDeviceEvent(scope: DeviceStorageScope, event: string, data: DeviceValue, at: number): string {
  requireDeviceDurability()
  if (!db().isTransaction) throw new DeviceProtocolError('internal', 'Outbox event requires a transaction')
  const insert = stmt(`INSERT INTO device_events(device_id,data_epoch,config_scope,controller_id,event_type,data,created_at)
    VALUES(?,?,?,?,?,?,?)`)
  insert.setReadBigInts(true)
  const result = insert.run(...deviceScopeArgs(scope), event, encodeDeviceValue(data), at)
  return String(result.lastInsertRowid)
}

/** 游标对外是不透明字符串；这里只在 SQL 边界解析，避免丢失 64 位精度。 */
export function listDeviceEvents(scope: DeviceStorageScope, after: string = '0', limit = 100): StoredDeviceEvent[] {
  requireDeviceDatabase()
  if (!/^(0|[1-9]\d{0,18})$/.test(after) || BigInt(after) > 9_223_372_036_854_775_807n) {
    throw new DeviceProtocolError('invalid_request', 'Invalid outbox cursor')
  }
  if (!Number.isSafeInteger(limit) || limit < 1 || limit > 4096) throw new DeviceProtocolError('resource_limit', 'Invalid event page limit')
  const query = stmt(`SELECT seq,event_type,data,created_at FROM device_events
    WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=? AND seq>? ORDER BY seq LIMIT ?`)
  query.setReadBigInts(true)
  return query.all(...deviceScopeArgs(scope), BigInt(after), limit).map((row) => {
    if (!(row.data instanceof Uint8Array)) throw new DeviceProtocolError('internal', 'Invalid stored event')
    return { cursor: String(row.seq), event: String(row.event_type), data: decodeDeviceValue(row.data), at: Number(row.created_at) }
  })
}
