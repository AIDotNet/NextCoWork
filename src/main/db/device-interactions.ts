/**
 * 持久待决交互(审批 / 提问 / 计划 / 插件 / 目标)。方案 §6 的 `interactions` 行。
 *
 * ## 谁能写这条表
 *
 * ★ **`createDeviceInteraction()` / `cancelDeviceInteraction()` 不是网络 API 的授权入口。**
 *   它们是**设备端内部执行器**的接口:内核挂起一次往返时登记待决项,中断时撤销它。
 *   网络来的只有 `respondDeviceInteraction()`(且答案由已认证的 `DeviceRequestContext`
 *   推导处理者身份),以及两个只读查询。
 *
 *   create/cancel 收的是内部 `InteractionScope`，不能直接注册成网络 handler。
 *   广播分区 `'*'` 避免与真实控制端 ID 冲突，但它不是授权机制；网络访问必须
 *   由 Gateway 的认证、schema 与逐方法权限校验决定。
 *
 * ## 一次性转换
 *
 * 多个已授权控制端共享同一条待决项并**竞争**同一份回答。裁决不是"先到先得",
 * 而是把判断和写入压进同一条原子语句:
 *
 * ```sql
 * UPDATE device_interactions SET status='resolved' … WHERE … AND status='pending'
 * ```
 *
 * `changes !== 1` 就说明别人已经先一步把状态翻过去了 —— 抛 `conflict`,**不覆盖**。
 * 于是"第二次回答即使答案一模一样也失败"是这个结构自带的性质,不需要额外的比较逻辑。
 * 网络层的幂等(requestId / idempotencyKey 重放返回同一个结果)以后由 command ledger
 * 包装这一层,**不在这里复制第二套逻辑**。
 *
 * ## 答案与事件同事务
 *
 * `interaction.resolved` / `interaction.requested` / `interaction.cancelled` 写在
 * **同一个** 事务里(见 `appendDeviceEvent` 的事务断言)。事件是"可以推给控制端了"的
 * 唯一依据,所以它不能先于状态、也不能在状态回滚后残留。
 */
import { createHash } from 'node:crypto'
import { decodeDeviceValue, encodeDeviceValue } from '../../shared/device/codec'
import { DeviceProtocolError } from '../../shared/device/errors'
import { deviceIdSchema } from '../../shared/device/identity'
import type { DeviceValue } from '../../shared/device/value'
import {
  createInteractionInputSchema, DEVICE_INTERACTION_BROADCAST_CONTROLLER, DEVICE_INTERACTION_KINDS,
  DEVICE_INTERACTION_STATUSES, interactionScopeSchema, respondInteractionInputSchema,
  type DeviceInteractionKind, type DeviceInteractionStatus, type InteractionScope
} from '../../shared/device/interactions'
import type { DeviceRequestContext } from '../services/request-context'
import { appendDeviceEvent, requireDeviceDatabase, requireDeviceDurability } from './device-events'
import { stmt, tx } from './index'

export interface DeviceInteractionRecord {
  id: string
  runId: string
  kind: DeviceInteractionKind
  payload: DeviceValue
  /** payload 的 sha256(十六进制)。回答方回传它,用来拒绝"答错了题"。 */
  payloadDigest: string
  status: DeviceInteractionStatus
  /**
   * 未回答时为 `null`。★ 反过来不成立:答案本身可以就是 `null` —— 它是被 msgpack
   * 编码成 1 字节的 `nil` 存进 BLOB 的,不会退化成 SQL NULL。分辨"没回答"与
   * "回答就是空"看的是 `status`,不是这一列。
   */
  answer: DeviceValue | null
  resolvedBy: string | null
  createdAt: number
  updatedAt: number
  expiresAt: number | null
}

const COLUMNS =
  'id,run_id,kind,payload,payload_digest,status,answer,resolved_by,created_at,updated_at,expires_at'
const KINDS: readonly string[] = DEVICE_INTERACTION_KINDS
const STATUSES: readonly string[] = DEVICE_INTERACTION_STATUSES
/** 单页上限。与 `listDeviceEvents` 的 4096 不同 —— 待决项是"人要看一遍"的集合。 */
const PAGE_LIMIT_MAX = 500

/** payload 摘要。与 `acceptDeviceCommand` 的 params_digest 同一个形状(sha256 十六进制)。 */
export function interactionPayloadDigest(payload: DeviceValue): string {
  return createHash('sha256').update(encodeDeviceValue(payload)).digest('hex')
}

function requireScope(scope: InteractionScope): InteractionScope {
  const parsed = interactionScopeSchema.safeParse(scope)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid interaction scope')
  return parsed.data
}

function requireId(id: unknown): string {
  const parsed = deviceIdSchema.safeParse(id)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid interaction id')
  return parsed.data
}

function requireNow(now: unknown): number {
  if (typeof now !== 'number' || !Number.isSafeInteger(now) || now < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid interaction timestamp')
  }
  return now
}

/**
 * 行 → 记录。`payload` / `answer` 必须重新解码校验,不能把 SQLite 里那串字节直接
 * 当成 `DeviceValue` 交出去 —— 库被人改过、或者上一版编码器写过别的形状时,
 * 症状会出现在很远的地方(界面渲染出的字段全是 undefined)。
 */
function decodeRecord(row: Record<string, unknown>): DeviceInteractionRecord {
  if (!KINDS.includes(String(row.kind)) || !STATUSES.includes(String(row.status))) {
    throw new DeviceProtocolError('internal', 'Invalid stored device interaction')
  }
  if (!(row.payload instanceof Uint8Array)) {
    throw new DeviceProtocolError('internal', 'Invalid stored interaction payload')
  }
  if (row.answer !== null && !(row.answer instanceof Uint8Array)) {
    throw new DeviceProtocolError('internal', 'Invalid stored interaction answer')
  }
  return {
    id: String(row.id),
    runId: String(row.run_id),
    kind: row.kind as DeviceInteractionKind,
    payload: decodeDeviceValue(row.payload),
    payloadDigest: String(row.payload_digest),
    status: row.status as DeviceInteractionStatus,
    answer: row.answer instanceof Uint8Array ? decodeDeviceValue(row.answer) : null,
    resolvedBy: row.resolved_by === null ? null : String(row.resolved_by),
    createdAt: Number(row.created_at),
    updatedAt: Number(row.updated_at),
    expiresAt: row.expires_at === null ? null : Number(row.expires_at)
  }
}

function selectByScope(scope: InteractionScope, id: string): Record<string, unknown> | undefined {
  return stmt(`SELECT ${COLUMNS} FROM device_interactions
    WHERE device_id=? AND data_epoch=? AND config_scope=? AND scope_epoch=? AND id=?`)
    .get(scope.deviceId, scope.dataEpoch, scope.configScope, scope.scopeEpoch, id)
}

/**
 * 登记一条待决项。**内部执行器专用**,不是网络授权入口(见文件头)。
 *
 * 重复 id 抛 `conflict`,**绝不覆盖**已有行 —— 覆盖会把"已经有控制端看过的那道题"
 * 换掉,而对方手里握着的 `payloadDigest` 还是旧的,症状是"回答一直说内容变了"。
 *
 * `expiresAt` 允许 `null`(永不自动过期);给了值就必须在当前时刻之后。
 */
export function createDeviceInteraction(
  scope: InteractionScope,
  input: { id: string; runId: string; kind: DeviceInteractionKind; payload: DeviceValue; expiresAt?: number | null },
  now = Date.now()
): DeviceInteractionRecord {
  const parsedScope = requireScope(scope)
  const at = requireNow(now)
  const parsed = createInteractionInputSchema.safeParse(input)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid interaction input')
  const { id, runId, kind, expiresAt } = parsed.data
  if (expiresAt !== null && expiresAt !== undefined && expiresAt <= at) {
    throw new DeviceProtocolError('invalid_request', 'Interaction expiry must be in the future')
  }
  // 先编码:形状不合法或超出帧预算的 payload 在这里就炸,不会留下半条行。
  const bytes = encodeDeviceValue(parsed.data.payload)
  const digest = createHash('sha256').update(bytes).digest('hex')
  requireDeviceDurability()
  return tx(() => {
    // 主键不含 scopeEpoch；旧代际同 id 也不能覆盖或漏成原生 SQL 错误。
    if (stmt('SELECT 1 FROM device_interactions WHERE device_id=? AND data_epoch=? AND config_scope=? AND id=?')
      .get(parsedScope.deviceId, parsedScope.dataEpoch, parsedScope.configScope, id) !== undefined) {
      throw new DeviceProtocolError('conflict', 'Interaction already exists')
    }
    stmt(`INSERT INTO device_interactions(device_id,data_epoch,config_scope,id,scope_epoch,run_id,kind,
      payload,payload_digest,status,answer,resolved_by,created_at,updated_at,expires_at)
      VALUES(?,?,?,?,?,?,?,?,?,'pending',NULL,NULL,?,?,?)`).run(
      parsedScope.deviceId, parsedScope.dataEpoch, parsedScope.configScope, id, parsedScope.scopeEpoch,
      runId, kind, bytes, digest, at, at, expiresAt ?? null
    )
    // controller_id='*' 是内部广播标记,不是某个控制端;网络指定不出它。
    appendDeviceEvent(
      { ...parsedScope, controllerId: DEVICE_INTERACTION_BROADCAST_CONTROLLER },
      'interaction.requested',
      { id, runId, kind, scopeEpoch: parsedScope.scopeEpoch },
      at
    )
    return {
      id, runId, kind, payload: decodeDeviceValue(bytes), payloadDigest: digest, status: 'pending' as const,
      answer: null, resolvedBy: null, createdAt: at, updatedAt: at, expiresAt: expiresAt ?? null
    }
  })
}

/**
 * 按**当前绑定**读一条。只认 context 里的设备 / dataEpoch / 账号 / scopeEpoch;
 * 报文里的 id 不等于权限,别的代际看不见。不存在返回 `null`(不是"别人的待决项")。
 */
export function getDeviceInteraction(context: DeviceRequestContext, id: string): DeviceInteractionRecord | null {
  requireDeviceDatabase()
  context.assertCurrent()
  const row = selectByScope(context.binding, requireId(id))
  return row === undefined ? null : decodeRecord(row)
}

/**
 * 待决列表:`pending` 且未过期,按时间确定排序(同一毫秒时用 id 打破平局)。
 * limit 有界 1..500 —— 界面上它是"一次要人看完的一屏",不是可无限翻的日志。
 */
export function listDeviceInteractions(
  context: DeviceRequestContext,
  limit = 100,
  now = Date.now()
): DeviceInteractionRecord[] {
  requireDeviceDatabase()
  context.assertCurrent()
  if (typeof limit !== 'number' || !Number.isSafeInteger(limit) || limit < 1 || limit > PAGE_LIMIT_MAX) {
    throw new DeviceProtocolError('resource_limit', 'Invalid interaction page limit')
  }
  const at = requireNow(now)
  return stmt(`SELECT ${COLUMNS} FROM device_interactions
    WHERE device_id=? AND data_epoch=? AND config_scope=? AND scope_epoch=?
      AND status='pending' AND (expires_at IS NULL OR expires_at>?)
    ORDER BY created_at, id LIMIT ?`).all(
    context.binding.deviceId, context.binding.dataEpoch, context.binding.configScope,
    context.binding.scopeEpoch, at, limit
  ).map(decodeRecord)
}

/**
 * 回答一条待决项。成功即**一次性**转换 pending → resolved,并记下处理者。
 *
 * 拒绝的情况一律抛 `DeviceProtocolError`:不在本作用域(含已过期、已被别人处理、
 * payload 变了)是 `conflict` 或 `not_found`。**不自动批准、不执行答案** ——
 * 这里的职责边界就是"把回答落盘并通知",执行由发起方在内核侧自己接着做;
 * 一个会顺手执行副作用的访问器没法安全地在事务里重试。
 */
export function respondDeviceInteraction(
  context: DeviceRequestContext,
  input: { id: string; payloadDigest: string; answer: DeviceValue },
  now = Date.now()
): DeviceInteractionRecord {
  context.assertCurrent()
  const parsed = respondInteractionInputSchema.safeParse(input)
  if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid interaction response')
  const id = requireId(parsed.data.id)
  const at = requireNow(now)
  const bytes = encodeDeviceValue(parsed.data.answer)
  const controllerId = context.binding.controllerId
  requireDeviceDurability()
  return tx(() => {
    context.assertCurrent()
    const row = selectByScope(context.binding, id)
    if (row === undefined) throw new DeviceProtocolError('not_found', 'Interaction does not exist in this scope')
    const record = decodeRecord(row)
    if (record.status !== 'pending') throw new DeviceProtocolError('conflict', 'Interaction is no longer pending')
    if (record.expiresAt !== null && record.expiresAt <= at) throw new DeviceProtocolError('conflict', 'Interaction has expired')
    // 摘要不符 = 回答的是另一道题(或题目在回答者看到之后被换过),不落答案。
    if (record.payloadDigest !== parsed.data.payloadDigest) {
      throw new DeviceProtocolError('conflict', 'Interaction payload changed')
    }
    // ★ 判题与写入压在同一条语句里:两个控制端同时到这里,只有一个 changes===1。
    const update = stmt(`UPDATE device_interactions SET status='resolved',answer=?,resolved_by=?,updated_at=?
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND scope_epoch=? AND id=? AND status='pending'`).run(
      bytes, controllerId, at, context.binding.deviceId, context.binding.dataEpoch,
      context.binding.configScope, context.binding.scopeEpoch, id
    )
    if (Number(update.changes) !== 1) {
      throw new DeviceProtocolError('conflict', 'Interaction was resolved by another controller')
    }
    context.assertCurrent()
    /*
      事件只带最小字段:id 和处理者。题目正文和答案留在库里按 id 取 ——
      广播事件会进每个控制端的 outbox,把审批内容抄一份进去等于把它同时写进
      所有设备的缓冲区,而那条路径上没有"这条还该不该看"的判断。
    */
    appendDeviceEvent(
      { ...context.binding, controllerId: DEVICE_INTERACTION_BROADCAST_CONTROLLER },
      'interaction.resolved',
      { id, resolvedBy: controllerId },
      at
    )
    return { ...record, status: 'resolved', answer: decodeDeviceValue(bytes), resolvedBy: controllerId, updatedAt: at }
  })
}

/**
 * 撤销一条待决项。**内部执行器专用**(中断、上下文被删、run 终止)。
 *
 * 已终态返回 `false` 且**不动答案**:取消一个已经被回答的审批必须是无声的失败,
 * 抛出的话调用方(那是中断路径)就得为一个不影响结果的分支写处理代码。
 */
export function cancelDeviceInteraction(scope: InteractionScope, id: string, now = Date.now()): boolean {
  const parsedScope = requireScope(scope)
  const target = requireId(id)
  const at = requireNow(now)
  requireDeviceDurability()
  return tx(() => {
    const row = selectByScope(parsedScope, target)
    if (row === undefined) return false
    if (decodeRecord(row).status !== 'pending') return false
    const update = stmt(`UPDATE device_interactions SET status='cancelled',updated_at=?
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND scope_epoch=? AND id=? AND status='pending'`).run(
      at, parsedScope.deviceId, parsedScope.dataEpoch, parsedScope.configScope, parsedScope.scopeEpoch, target
    )
    if (Number(update.changes) !== 1) return false
    appendDeviceEvent(
      { ...parsedScope, controllerId: DEVICE_INTERACTION_BROADCAST_CONTROLLER },
      'interaction.cancelled',
      { id: target },
      at
    )
    return true
  })
}
