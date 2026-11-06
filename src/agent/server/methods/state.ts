/**
 * 控制端状态与推送订阅登记。数据库层(`db/controller-state.ts`、`db/device-notifications.ts`)
 * 已经负责隔离键、版本 CAS 与墓碑;这里只是把它们接成 RPC。
 *
 * 都是 `transaction` 类:同步、事务内,回执与业务写入同事务提交。
 * ★ 通知只做「登记」:`deliveryConfigured` 恒为 false,不接任何推送供应商,不发网络请求。
 */
import { z } from 'zod'
import { readControllerState, saveControllerState } from '../../../main/db/controller-state'
import {
  getNotificationSubscription, removeNotificationSubscription, saveNotificationSubscription
} from '../../../main/db/device-notifications'
import {
  controllerStateReadParams, controllerStateSaveParams, DEVICE_METHODS, notificationsRemoveParams,
  notificationsSaveParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

export function registerStateMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.controllerStateRead, controllerStateReadParams, anyValue,
    (context, { key }) => plain(readControllerState(context, key)))

  router.transaction(DEVICE_METHODS.controllerStateSave, controllerStateSaveParams, anyValue,
    (context, params) => plain(saveControllerState(context, params)))

  router.query(DEVICE_METHODS.notificationsGet, z.null(), anyValue,
    (context) => plain(getNotificationSubscription(context)))

  router.transaction(DEVICE_METHODS.notificationsSave, notificationsSaveParams, anyValue,
    (context, params) => plain(saveNotificationSubscription(context, params)))

  router.transaction(DEVICE_METHODS.notificationsRemove, notificationsRemoveParams, z.strictObject({ removed: z.boolean() }),
    (context, { expectedRevision }) => ({ removed: removeNotificationSubscription(context, expectedRevision) }))
}
