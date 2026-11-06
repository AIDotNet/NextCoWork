/**
 * 设备信任记录:谁被允许以控制端身份连进这台设备。
 *
 * 身份**只**来自这里 —— 网关的 `authenticate` 用 TLS 叶子证书指纹来查,hello 报文里
 * 自称的 `controllerId` 不参与。三条约束:
 *
 * 1. **邀请单次、限时、只存摘要。** `createEnrollment` 给出 256 位随机 token,库里只有
 *    sha256;`completeEnrollment` 用一条 `UPDATE … WHERE consumed_at IS NULL AND
 *    expires_at > now` 原子消费,并发两个控制端拿同一邀请只有一个 `changes===1`。
 * 2. **失败不泄露原因。** 不存在 / 过期 / 已用一律同一个 `unauthorized`。
 * 3. **撤销是墓碑。** `revoked_at` 置位但行保留;指纹 UNIQUE,同一把密钥撤销后不能
 *    原样重新配对。撤销对**已建立的连接**也立即生效(网关每次请求查 `isControllerRevoked`)。
 */
import { createHash, randomBytes } from 'node:crypto'
import { DeviceProtocolError } from '../../shared/device/errors'
import { deviceIdSchema } from '../../shared/device/identity'
import { requireDeviceDurability } from './device-events'
import { stmt, tx } from './index'

export const ENROLLMENT_TTL_MS = 5 * 60 * 1000
/** 同时有效的未消费邀请上限:邀请是稀缺的敏感物,不该被刷出一堆。 */
export const ENROLLMENT_MAX_OUTSTANDING = 8
const FINGERPRINT = /^[0-9a-f]{64}$/

export interface PairedController {
  controllerId: string
  label: string
  pairedAt: number
  revokedAt: number | null
}

function requireNow(now: unknown): number {
  if (typeof now !== 'number' || !Number.isSafeInteger(now) || now < 0) {
    throw new DeviceProtocolError('invalid_request', 'Invalid timestamp')
  }
  return now
}

const hashToken = (token: string): string => createHash('sha256').update(token, 'utf8').digest('hex')

/** 签发一个邀请。返回的 token 只此一次可见,调用方负责经受信渠道交给控制端。 */
export function createEnrollment(now: number, ttlMs = ENROLLMENT_TTL_MS): { token: string; expiresAt: number } {
  requireNow(now)
  if (!Number.isSafeInteger(ttlMs) || ttlMs < 1000 || ttlMs > ENROLLMENT_TTL_MS) {
    throw new DeviceProtocolError('invalid_request', 'Invalid enrollment lifetime')
  }
  requireDeviceDurability()
  return tx(() => {
    // 已过期或已消费的行没有价值,顺手清掉,表不会无限长。
    stmt('DELETE FROM device_enrollments WHERE expires_at <= ? OR consumed_at IS NOT NULL').run(now)
    const outstanding = Number(stmt('SELECT COUNT(*) AS n FROM device_enrollments').get()?.n)
    if (outstanding >= ENROLLMENT_MAX_OUTSTANDING) {
      throw new DeviceProtocolError('resource_limit', 'Too many pending enrollments')
    }
    const token = randomBytes(32).toString('base64url')
    const expiresAt = now + ttlMs
    stmt('INSERT INTO device_enrollments (token_hash, created_at, expires_at) VALUES (?,?,?)')
      .run(hashToken(token), now, expiresAt)
    return { token, expiresAt }
  })
}

/**
 * 用邀请登记一个控制端。邀请消费与控制端落库在同一事务:要么都发生,要么都不发生。
 * `fingerprintSha256` 是控制端提交的公钥对应叶子证书的指纹(小写 hex)。
 */
export function completeEnrollment(
  input: { token: string; controllerId: string; fingerprintSha256: string; label: string },
  now: number
): PairedController {
  requireNow(now)
  const controllerId = deviceIdSchema.safeParse(input.controllerId)
  if (!controllerId.success || !FINGERPRINT.test(input.fingerprintSha256)
    || typeof input.label !== 'string' || input.label.length < 1 || input.label.length > 128
    // eslint-disable-next-line no-control-regex -- 标签会显示在设备管理界面,控制字符会破坏它
    || /[\u0000-\u001f\u007f]/.test(input.label)
    || typeof input.token !== 'string' || input.token.length < 32 || input.token.length > 256) {
    throw new DeviceProtocolError('invalid_request', 'Invalid enrollment request')
  }
  requireDeviceDurability()
  return tx(() => {
    const consumed = stmt(`UPDATE device_enrollments SET consumed_at=?, consumed_by=?
      WHERE token_hash=? AND consumed_at IS NULL AND expires_at>?`)
      .run(now, controllerId.data, hashToken(input.token), now)
    if (Number(consumed.changes) !== 1) {
      throw new DeviceProtocolError('unauthorized', 'Enrollment is invalid or expired')
    }
    try {
      stmt('INSERT INTO device_controllers (controller_id, fingerprint_sha256, label, paired_at) VALUES (?,?,?,?)')
        .run(controllerId.data, input.fingerprintSha256, input.label, now)
    } catch (error) {
      if (/UNIQUE|constraint/i.test(String((error as { message?: unknown })?.message))) {
        // 事务回滚,邀请不被烧掉;控制端 id / 密钥已被占用需要换一个。
        throw new DeviceProtocolError('conflict', 'Controller identity is already registered')
      }
      throw error
    }
    return { controllerId: controllerId.data, label: input.label, pairedAt: now, revokedAt: null }
  })
}

function decode(row: Record<string, unknown>): PairedController {
  const { controller_id: id, label, paired_at: pairedAt, revoked_at: revokedAt } = row
  if (typeof id !== 'string' || typeof label !== 'string'
    || typeof pairedAt !== 'number' || !Number.isSafeInteger(pairedAt)
    || (revokedAt !== null && (typeof revokedAt !== 'number' || !Number.isSafeInteger(revokedAt)))) {
    throw new DeviceProtocolError('internal', 'Invalid stored controller')
  }
  return { controllerId: id, label, pairedAt, revokedAt }
}

/** 按 TLS 叶子证书指纹找**仍有效**的控制端;未配对与已撤销都是 null。 */
export function findControllerByFingerprint(fingerprintSha256: string): PairedController | null {
  if (!FINGERPRINT.test(fingerprintSha256)) return null
  const row = stmt(`SELECT controller_id,label,paired_at,revoked_at FROM device_controllers
    WHERE fingerprint_sha256=?`).get(fingerprintSha256)
  if (row === undefined) return null
  const controller = decode(row)
  return controller.revokedAt === null ? controller : null
}

/** 未知控制端按已撤销处理 —— 查不到就是没有授权。 */
export function isControllerRevoked(controllerId: string): boolean {
  const row = stmt('SELECT revoked_at FROM device_controllers WHERE controller_id=?').get(controllerId)
  return row === undefined || row.revoked_at !== null
}

export function listControllers(): PairedController[] {
  return stmt('SELECT controller_id,label,paired_at,revoked_at FROM device_controllers ORDER BY paired_at, controller_id')
    .all().map(decode)
}

/** 撤销配对。幂等:已撤销或不存在返回 false,不改动首次撤销时间。 */
export function revokeController(controllerId: string, now: number): boolean {
  requireNow(now)
  requireDeviceDurability()
  const result = stmt('UPDATE device_controllers SET revoked_at=? WHERE controller_id=? AND revoked_at IS NULL')
    .run(now, controllerId)
  return Number(result.changes) === 1
}
