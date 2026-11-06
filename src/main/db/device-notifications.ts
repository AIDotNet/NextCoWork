/**
 * 推送**订阅**的持久化。M1 只做安全存储基础:登记 / 更新 / 删除一个订阅,
 * **不接任何推送供应商,不发网络请求,不投递任何通知**。
 *
 * ## 隔离键为什么**没有** `clientSessionId` / `bindingEpoch`
 *
 * 主键是 `(device_id, data_epoch, config_scope, controller_id)` —— 一个控制端一行。
 * 同一个控制端重连(`clientSessionId` / `bindingEpoch` 变化)必须读回自己登记过的
 * 那条,否则每次重连都要重新登记一遍;而别的控制端、别的设备、别的账号看不见它,
 * 因为那三个维度都在键里。
 *
 * ## `registration_ref` 是不透明引用,不是收件地址,也不是授权证明
 *
 * ★ 它**不是** APNs / FCM device token,也**不是** URL。写入口用
 *   `registrationRefSchema`(`shared/device/notifications.ts`)约束形状:1..128 个
 *   `[A-Za-z0-9_.-]`、首字符字母数字 —— 冒号 / 斜杠 / `@` / 空格全拒,所以
 *   `https://…` / `mailto:…` / `data:abc` 这些**地址**形状的值在**进库之前**就被拒了。
 *   (这里**不用** `deviceIdSchema`:那是通用标识 schema,字符集含 `:`,会放行
 *   `mailto:abc` 这种 scheme 样式的值。)
 *
 * ★ 形状合规**不等于**授权:匹配这个 schema 不证明任何权限,它更不是凭证。
 *   它是将来推送网关按引用去安全存储里取真 token 的句柄,取 token 与发请求都由
 *   实现方做,这一层永远不碰。
 *
 * ★ 因此这里也**不往 `domain outbox` 写任何事件**:登记引用属于"往哪台设备推"的句柄,
 *   广播进每个控制端的缓冲区等于把它抄给所有控制端;而且它不是要被投递的领域内容。
 *
 * ## 删除是墓碑(`deleted=1`),不是删行
 *
 * 见 `schema.ts` 第 33 条。读路径把墓碑当 `null`;软删除只把行标掉、把
 * `registration_ref` 清空、`revision` +1 —— 版本因此只增不减,"建 → 删 → 再建"
 * 不会回到 1,旧 active 版本的迟到指令必然 `conflict`(ABA)。
 *
 * ## `deliveryConfigured` 永远是 `false`
 *
 * ★ 真实渠道尚未接线。这一层能承诺的只有"订阅登记落盘了",绝不能返回一个
 *   "推送已开启"的形状 —— 那会让人以为通知能到,而实际上一封都不会发出去。
 */
import { NOTIFICATION_PLATFORMS, NotificationSubscriptionInputSchema,
  type NotificationPlatform, type NotificationSubscriptionInput, type NotificationSubscriptionView } from '../../shared/device/notifications'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { DeviceRequestContext } from '../services/request-context'
import { deviceScopeArgs, requireDeviceDatabase, requireDeviceDurability } from './device-events'
import { stmt, tx } from './index'

const PLATFORMS: readonly string[] = NOTIFICATION_PLATFORMS

function requireRevision(revision: unknown): number {
  if (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid notification subscription revision')
  }
  return revision
}

function requireNow(now: unknown): number {
  if (typeof now !== 'number' || !Number.isSafeInteger(now) || now < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid notification subscription timestamp')
  }
  return now
}

/** 允许的投递平台(与 DDL 的 CHECK 同集合)。 */
function isPlatform(value: unknown): value is NotificationPlatform {
  return typeof value === 'string' && PLATFORMS.includes(value)
}

/**
 * 行 → 视图。
 *
 * ★ 四个字段逐个按**类型**校验,不靠 `Number(...)` 折:SQLite 是动态类型的,
 *   `Number(null)` 是 0、`Number('')` 也是 0,于是"这一列根本没写过"会被静默当成
 *   版本 0 / 时间 0、`enabled` 也会被当成 false。坏行一律抛 `internal`,
 *   绝不据一条坏 `revision` 去覆盖数据。
 *   `revision` 合法值从 1 起(0 只有"还没有这条行"才代表),`updated_at` 非负安全整数,
 *   `enabled` 只能是 0 或 1,`platform` 只认枚举。
 */
function decodeRow(row: Record<string, unknown>): NotificationSubscriptionView {
  const revision = row.revision
  const updatedAt = row.updated_at
  const enabled = row.enabled
  if (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 1
    || typeof updatedAt !== 'number' || !Number.isSafeInteger(updatedAt) || updatedAt < 0
    || (enabled !== 0 && enabled !== 1)
    || !isPlatform(row.platform)) {
    throw new DeviceProtocolError('internal', 'Invalid stored notification subscription')
  }
  return {
    platform: row.platform,
    enabled: enabled === 1,
    revision,
    updatedAt,
    // ★ 固定字面量:真实投递渠道尚未接线,这一层不谎报"已开启"。
    deliveryConfigured: false
  }
}

/**
 * 只认 `context.binding` —— 网络入参里没有也不该有 controllerId。
 * 墓碑行(`deleted=1`)也返回 —— 调用方要能看见它,好把版本延续下去。
 */
function selectSubscription(context: DeviceRequestContext): Record<string, unknown> | undefined {
  return stmt(`SELECT platform,enabled,revision,updated_at,deleted FROM device_notification_subscriptions
    WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=?`)
    .get(...deviceScopeArgs(context.binding))
}

function isTombstone(row: Record<string, unknown>): boolean {
  if (row.deleted !== 0 && row.deleted !== 1) {
    throw new DeviceProtocolError('internal', 'Invalid stored notification subscription')
  }
  return row.deleted === 1
}

/** 按当前绑定读这条订阅的形状。没登记过、已删除(墓碑)或不属于本作用域一律返回 `null`。 */
export function getNotificationSubscription(context: DeviceRequestContext): NotificationSubscriptionView | null {
  requireDeviceDatabase()
  context.assertCurrent()
  const row = selectSubscription(context)
  if (row === undefined || isTombstone(row)) return null
  return decodeRow(row)
}

/**
 * 登记或更新这条订阅。返回落盘后的视图(`revision` 已 +1)。
 *
 * `expectedRevision`:首次登记要求 0,更新必须等于当前版本,否则 `conflict`。
 * 版本回传时与原登记引用无关 —— 换一个 `registrationRef` 也是**同一条行**的更新,
 * 这正是不让同一控制端攒出多行"哪条才算数"的原因。
 *
 * ★ 墓碑行(`deleted=1`)不是"可以覆盖的旧值":重新登记**必须**从 `expectedRevision`
 *   0 开始,但沿用墓碑的 `revision + 1`(不回到 1)。于是墓碑之前那个 active 版本的
 *   迟到更新 / 删除,在新行上必然是 `conflict` —— 这就是软删除挡住的 ABA。
 */
export function saveNotificationSubscription(
  context: DeviceRequestContext,
  input: NotificationSubscriptionInput,
  now = Date.now()
): NotificationSubscriptionView {
  context.assertCurrent()
  const parsed = NotificationSubscriptionInputSchema.safeParse(input)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid notification subscription')
  const expected = requireRevision(parsed.data.expectedRevision)
  const at = requireNow(now)
  requireDeviceDurability()
  return tx(() => {
    context.assertCurrent()
    const row = selectSubscription(context)
    if (row === undefined || isTombstone(row)) {
      if (expected !== 0) {
        throw new DeviceProtocolError('conflict', 'Notification subscription does not exist')
      }
      // 墓碑:版本从墓碑+1 续下去;首次创建:从 1 起。
      const base = row === undefined ? 0 : decodeRow(row).revision
      if (base >= Number.MAX_SAFE_INTEGER) {
        throw new DeviceProtocolError('resource_limit', 'Notification subscription revision is exhausted')
      }
      const revision = base + 1
      stmt(`INSERT OR REPLACE INTO device_notification_subscriptions(device_id,data_epoch,config_scope,
        controller_id,registration_ref,platform,enabled,revision,updated_at,deleted)
        VALUES(?,?,?,?,?,?,?,?,?,0)`)
        .run(...deviceScopeArgs(context.binding), parsed.data.registrationRef,
          parsed.data.platform, parsed.data.enabled ? 1 : 0, revision, at)
      context.assertCurrent()
      return { platform: parsed.data.platform, enabled: parsed.data.enabled, revision, updatedAt: at, deliveryConfigured: false }
    }
    const current = decodeRow(row)
    if (current.revision !== expected) throw new DeviceProtocolError('conflict', 'Notification subscription revision changed')
    if (current.revision >= Number.MAX_SAFE_INTEGER) {
      throw new DeviceProtocolError('resource_limit', 'Notification subscription revision is exhausted')
    }
    const revision = current.revision + 1
    stmt(`UPDATE device_notification_subscriptions SET registration_ref=?,platform=?,enabled=?,revision=?,updated_at=?
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=?`)
      .run(parsed.data.registrationRef, parsed.data.platform, parsed.data.enabled ? 1 : 0, revision, at,
        ...deviceScopeArgs(context.binding))
    context.assertCurrent()
    return { platform: parsed.data.platform, enabled: parsed.data.enabled, revision, updatedAt: at, deliveryConfigured: false }
  })
}

/**
 * 删除**本控制端**这条订阅(`expectedRevision` 必须与当前版本相符)。
 *
 * ★ 软删除(墓碑),不是删行 —— 见 `schema.ts` 第 33 条与文件头。当前 active 行的
 *   版本必须等于 `expectedRevision`,CAS 成功后 `revision+1`、`deleted=1`、`enabled=0`、
 *   `registration_ref=''`(清掉敏感引用)、`updated_at=now`。版本只增不减,
 *   所以"建 → 删 → 再建"不会回到 1。
 *
 * ★ 四道判断缺一不可:
 *   - 别的控制端删不掉我的(`WHERE` 里带 `controller_id`,绑定决定);
 *   - 没行(或已经是墓碑)且 `expectedRevision === 0` = 返回 `false`(本就没什么可删);
 *     没行但期望非 0 = `conflict`(控制端以为有一条版本 N 的订阅,实际没有);
 *   - 有 active 行但版本不符 = `conflict`(有人先改过,不能静默吞掉);
 *   - 墓碑行**不能**再被删一次 —— 所以"重复删除 `expectedRevision` 0"也返回 `false`。
 */
export function removeNotificationSubscription(
  context: DeviceRequestContext,
  expectedRevision: number,
  now = Date.now()
): boolean {
  context.assertCurrent()
  const expected = requireRevision(expectedRevision)
  const at = requireNow(now)
  requireDeviceDurability()
  return tx(() => {
    context.assertCurrent()
    const row = selectSubscription(context)
    if (row === undefined || isTombstone(row)) {
      if (expected !== 0) throw new DeviceProtocolError('conflict', 'Notification subscription does not exist')
      return false
    }
    const current = decodeRow(row)
    if (current.revision !== expected) {
      throw new DeviceProtocolError('conflict', 'Notification subscription revision changed')
    }
    if (current.revision >= Number.MAX_SAFE_INTEGER) {
      throw new DeviceProtocolError('resource_limit', 'Notification subscription revision is exhausted')
    }
    const removed = stmt(`UPDATE device_notification_subscriptions
      SET revision=?,deleted=1,enabled=0,registration_ref='',updated_at=?
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=? AND revision=? AND deleted=0`)
      .run(current.revision + 1, at, ...deviceScopeArgs(context.binding), expected)
    if (Number(removed.changes) !== 1) throw new DeviceProtocolError('conflict', 'Notification subscription changed')
    context.assertCurrent()
    return true
  })
}
