/**
 * 控制端 UI 状态(renderer 草稿之类)的持久化。M1 的方案 §"controller state"。
 *
 * ## 隔离键为什么**没有** `clientSessionId` / `bindingEpoch`
 *
 * 主键是 `(device_id, data_epoch, config_scope, controller_id, view_key)`。写进去的行
 * 属于**这台设备的这个账号作用域 + 这个控制端**,而不是"某一次连接":
 * 同一个控制端断线重连(`clientSessionId` 与 `bindingEpoch` 都会变)必须能读回自己
 * 刚写的草稿,否则表现是"每次重连草稿都清空"。
 *
 * ★ 反过来,**别的控制端、别的设备、别的账号一律看不见**:`controller_id` 在键里,
 *   `device_id` / `data_epoch` / `config_scope` 也在。这两个字的取舍是这张表存在的全部理由 ——
 *   少了 `controller_id` 就变成"多个控制端互相覆盖对方的草稿",少了 `data_epoch`
 *   就会把数据恢复前的旧草稿带到恢复后的新库里。
 *
 * ★ `scopeEpoch` **刻意不在键里**,但它**不等于** `dataEpoch`:前者是账号代际,重连 /
 *   重新登录就会变,后者是数据恢复代际。草稿按**账号 ID**(`config_scope`)留存 ——
 *   同一个账号重连、换代际之后还要能读回自己的草稿,所以键里用的是账号而不是代际。
 *   代际变化后的**旧写**由 `ctx.assertCurrent()` 拦下(写路径提交前复检),
 *   不靠键的宽度来防。
 *   (`interactions` 那张表把 `scopeEpoch` 放进键是因为待决项必须随账号代际失效;
 *   草稿没有这个要求,多加一列只会让同一个控制端换一次代际就读不回自己的草稿。)
 *
 * ## 并发
 *
 * 每个写都是**读校验 → 写**压在同一条 `tx` 里,并在提交前 `assertCurrent()`
 * (撤销 / 切作用域之后不得改写)。版本比较用 `expectedRevision`,首次不存在要求 0。
 * 不采用 `UPDATE … WHERE revision=?` 的单语句 CAS:这里要在"不存在"与"版本不符"
 * 之间给出**不同**的错误,而单语句 CAS 两者都是 `changes===0`。
 *
 * ## 持久性
 *
 * 所有写走 `requireDeviceDurability()` —— 控制端状态是"重连后必须还在"的承诺,
 * 不能落在 `synchronous=NORMAL` 的连接上。
 *
 * ## 事件
 *
 * 本批**不往 `domain outbox` 写事件**:草稿正文属于用户内容,而 `device_events` 会
 * 广播给每个控制端的缓冲区。**不广播正文 ≠ 不需要写回执** —— 调用方拿到的
 * `ControllerStateRecord`(含新 `revision`)就是回执,版本靠它继续做 CAS;
 * 少写的是那条会抄给所有控制端的事件,不是应答。
 */
import { decodeDeviceValue, encodeDeviceValue } from '../../shared/device/codec'
import { DeviceProtocolError } from '../../shared/device/errors'
import { deviceIdSchema } from '../../shared/device/identity'
import type { DeviceValue } from '../../shared/device/value'
import type { DeviceRequestContext } from '../services/request-context'
import { deviceScopeArgs, requireDeviceDatabase, requireDeviceDurability } from './device-events'
import { stmt, tx } from './index'

/** 一次控制器状态的读/写结果。`updatedAt` 是毫秒时间戳。 */
export interface ControllerStateRecord {
  revision: number
  value: DeviceValue
  updatedAt: number
}

export interface SaveControllerStateInput {
  /** 视图键。复用 `deviceIdSchema`:1..128 个字母数字加 `._:-`。 */
  key: string
  /** 0 = 首次创建;之后必须等于当前版本,否则 `conflict`。 */
  expectedRevision: number
  value: DeviceValue
}

/** `view_key` 与 `key` 同名同形状:两者都是控制端自己编的稳定标识。 */
function requireKey(key: unknown): string {
  const parsed = deviceIdSchema.safeParse(key)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid controller state key')
  return parsed.data
}

/**
 * 版本号必须是非负安全整数。★ 单独抽出来是为了给 `resource_limit` 而不是
 * `invalid_request`:`expectedRevision` 超过 `MAX_SAFE_INTEGER` 意味着控制端在按
 * 一个我们存不下的版本号发指令,把它当形状错误会掩盖"版本号已经溢出"这件事。
 */
function requireRevision(revision: unknown): number {
  if (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid controller state revision')
  }
  return revision
}

function requireNow(now: unknown): number {
  if (typeof now !== 'number' || !Number.isSafeInteger(now) || now < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid controller state timestamp')
  }
  return now
}

/**
 * 行 → 记录。`value` 必须重新解码校验,不能把库里那串字节直接当 `DeviceValue` 交出去:
 * 库被外部改过(或旧编码器写过别的形状)时,症状会出现在很远的地方(界面上字段全是 undefined)。
 *
 * ★ 三个字段逐个按**类型**校验,不靠 `Number(...)` 折:SQLite 是动态类型的,
 *   `Number(null)` 是 0、`Number('')` 也是 0,于是"这一列根本没写过"会被静默当成
 *   版本 0 —— 那正好是首次创建允许的值。下面任何一处不满足都抛 `internal`,
 *   绝不能让一条坏行被当成合法版本继续参与 CAS。
 *   (`revision` 合法值从 1 起:0 只有"还没有这条行"才代表,落盘的行不会是 0。)
 */
function decodeRecord(row: Record<string, unknown>): ControllerStateRecord {
  const revision = row.revision
  const updatedAt = row.updated_at
  if (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 1
    || typeof updatedAt !== 'number' || !Number.isSafeInteger(updatedAt) || updatedAt < 0
    || !(row.value instanceof Uint8Array)) {
    throw new DeviceProtocolError('internal', 'Invalid stored controller state')
  }
  return { revision, value: decodeDeviceValue(row.value), updatedAt }
}

/** 只认 `context.binding` —— 报文里没有也不该有 controllerId 这个入参。 */
function selectState(context: DeviceRequestContext, key: string): Record<string, unknown> | undefined {
  return stmt(`SELECT revision,value,updated_at FROM device_controller_state
    WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=? AND view_key=?`)
    .get(...deviceScopeArgs(context.binding), key)
}

/** 按当前绑定读一条。不属于本作用域(含别的控制端)的记录一律是 `null`。 */
export function readControllerState(context: DeviceRequestContext, key: string): ControllerStateRecord | null {
  requireDeviceDatabase()
  context.assertCurrent()
  const row = selectState(context, requireKey(key))
  return row === undefined ? null : decodeRecord(row)
}

/**
 * 按 `expectedRevision` 落一条。返回落盘后的记录(`revision` 已 +1)。
 *
 * 失败一律抛 `DeviceProtocolError`:`conflict`(已存在但版本不符 / 不存在但期望非 0)、
 * `resource_limit`(版本已经涨到 `MAX_SAFE_INTEGER`,加一就不再是安全整数)。
 */
export function saveControllerState(
  context: DeviceRequestContext,
  input: SaveControllerStateInput,
  now = Date.now()
): ControllerStateRecord {
  context.assertCurrent()
  const key = requireKey(input.key)
  const expected = requireRevision(input.expectedRevision)
  const at = requireNow(now)
  // ★ 先编码:形状不合法或超出帧预算的 value 在这里就炸,不会留下半条行。
  //   `null` 是合法 DeviceValue(编码成 1 字节 nil),因此"清空"不会退化成缺失。
  const bytes = encodeDeviceValue(input.value)
  requireDeviceDurability()
  return tx(() => {
    context.assertCurrent()
    const row = selectState(context, key)
    if (row === undefined) {
      if (expected !== 0) throw new DeviceProtocolError('conflict', 'Controller state does not exist')
      stmt(`INSERT INTO device_controller_state(device_id,data_epoch,config_scope,controller_id,view_key,
        revision,value,updated_at) VALUES(?,?,?,?,?,1,?,?)`)
        .run(...deviceScopeArgs(context.binding), key, bytes, at)
      // ★ 与 UPDATE 分支同规:`value` 的编码可能在这条语句之前才被求值(见下面那条
      //   测试),所以 INSERT 之后同样要复检,不能把"校验通过"当成提交许可。
      context.assertCurrent()
      return { revision: 1, value: decodeDeviceValue(bytes), updatedAt: at }
    }
    const current = decodeRecord(row)
    if (current.revision !== expected) throw new DeviceProtocolError('conflict', 'Controller state revision changed')
    if (current.revision >= Number.MAX_SAFE_INTEGER) {
      throw new DeviceProtocolError('resource_limit', 'Controller state revision is exhausted')
    }
    const revision = current.revision + 1
    stmt(`UPDATE device_controller_state SET revision=?,value=?,updated_at=?
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=? AND view_key=?`)
      .run(revision, bytes, at, ...deviceScopeArgs(context.binding), key)
    context.assertCurrent()
    return { revision, value: decodeDeviceValue(bytes), updatedAt: at }
  })
}
