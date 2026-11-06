/**
 * 服务数据根:单实例锁 + 持久身份。
 *
 * ## 身份文件
 *
 * `device-identity.json` 记录 `deviceId` / `dataEpoch` / `scopeEpoch` / `configScope`。
 * ★ 它损坏或缺字段时**拒绝启动**,而不是重新生成:新的 `deviceId` 会让所有已配对控制端
 * 把这台设备当成陌生设备,而新的 `dataEpoch` 会让它们以为数据被整体替换 —— 静默重置
 * 比启动失败更糟。
 *
 * `serverEpoch` 不落盘:每次进程启动重新生成,控制端据此知道「服务重启过」。
 *
 * ## 单根锁
 *
 * 一个数据根同一时刻只能有一个服务进程(否则两个进程同写一个 SQLite/回执表)。
 * 用独占创建(`wx`)的锁文件,内容含 pid 与随机 token;持有者崩溃留下的陈旧锁用
 * pid 存活检测接管。接管走「rename 走旧锁 → 复核内容 → 独占创建新锁」,
 * 复核发现被换成了别人的新锁时放弃,宁可报「忙」也不抢别人的锁。
 */
import { randomBytes, randomUUID } from 'node:crypto'
import { closeSync, fsyncSync, mkdirSync, openSync, readFileSync, renameSync, rmSync, writeSync } from 'node:fs'
import { join } from 'node:path'
import { z } from 'zod'
import { DeviceProtocolError } from '../../shared/device/errors'
import { deviceIdSchema, scopeIdSchema } from '../../shared/device/identity'

const IDENTITY_FILE = 'device-identity.json'
const LOCK_FILE = 'service.lock'

const identitySchema = z.strictObject({
  v: z.literal(1),
  deviceId: deviceIdSchema,
  dataEpoch: deviceIdSchema,
  configScope: scopeIdSchema,
  scopeEpoch: deviceIdSchema,
  createdAt: z.number().int().nonnegative()
})
export type PersistedIdentity = z.infer<typeof identitySchema>

const lockSchema = z.strictObject({ pid: z.number().int().positive(), token: z.string().min(16), startedAt: z.number().int().nonnegative() })

export interface ServiceIdentity extends PersistedIdentity {
  /** 本次进程生命周期的 epoch,不落盘。 */
  serverEpoch: string
}

export interface ServiceRoot {
  readonly dataDir: string
  readonly identity: ServiceIdentity
  /** 释放锁。幂等;只删自己持有的那把。 */
  release(): void
}

export interface AcquireOptions {
  now?: () => number
  pid?: number
  /** 注入以便测试;默认用 `process.kill(pid, 0)`。 */
  isAlive?: (pid: number) => boolean
}

function processAlive(pid: number): boolean {
  try {
    process.kill(pid, 0)
    return true
  } catch (error) {
    // EPERM = 进程存在但不属于我们;只有 ESRCH 才是确定不存在。
    return (error as NodeJS.ErrnoException).code !== 'ESRCH'
  }
}

function writeDurably(path: string, text: string, flag: 'w' | 'wx'): void {
  const fd = openSync(path, flag, 0o600)
  try {
    writeSync(fd, text)
    fsyncSync(fd)
  } finally {
    closeSync(fd)
  }
}

function readLock(path: string): z.infer<typeof lockSchema> | null {
  try {
    const parsed = lockSchema.safeParse(JSON.parse(readFileSync(path, 'utf8')))
    return parsed.success ? parsed.data : null
  } catch {
    return null
  }
}

function busy(): DeviceProtocolError {
  return new DeviceProtocolError('conflict', 'Another service already owns this data root')
}

function takeLock(dataDir: string, options: Required<AcquireOptions>): () => void {
  const path = join(dataDir, LOCK_FILE)
  const mine = { pid: options.pid, token: randomBytes(16).toString('hex'), startedAt: options.now() }
  const body = JSON.stringify(mine)
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      writeDurably(path, body, 'wx')
      return () => {
        // 只删自己的锁:陈旧接管之后原持有者醒来不能误删新锁。
        if (readLock(path)?.token === mine.token) rmSync(path, { force: true })
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error
    }
    const existing = readLock(path)
    // 内容读不懂(半写/损坏)按「忙」处理:无法证明它是陈旧的。
    if (existing === null || options.isAlive(existing.pid)) throw busy()
    const stale = `${path}.stale.${randomBytes(6).toString('hex')}`
    try { renameSync(path, stale) } catch { throw busy() }
    const moved = readLock(stale)
    rmSync(stale, { force: true })
    if (moved?.token !== existing.token) throw busy() // 换成了别人的新锁。
  }
  throw busy()
}

function loadIdentity(dataDir: string, now: number): PersistedIdentity {
  const path = join(dataDir, IDENTITY_FILE)
  let raw: string | null = null
  try { raw = readFileSync(path, 'utf8') } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
  }
  if (raw !== null) {
    let json: unknown
    try { json = JSON.parse(raw) } catch { json = undefined }
    const parsed = identitySchema.safeParse(json)
    if (!parsed.success) {
      throw new DeviceProtocolError('internal', 'Device identity file is corrupt; refusing to regenerate it')
    }
    return parsed.data
  }
  const created: PersistedIdentity = {
    v: 1, deviceId: `dev-${randomUUID()}`, dataEpoch: `data-${randomUUID()}`, configScope: 'local',
    scopeEpoch: `scope-${randomUUID()}`, createdAt: now
  }
  const temp = `${path}.${randomBytes(6).toString('hex')}.tmp`
  writeDurably(temp, JSON.stringify(created), 'wx')
  renameSync(temp, path)
  return created
}

/** 取得数据根:建目录、拿锁、读/建身份。任何一步失败都不留下锁。 */
export function acquireServiceRoot(dataDir: string, input: AcquireOptions = {}): ServiceRoot {
  const options: Required<AcquireOptions> = {
    now: input.now ?? Date.now, pid: input.pid ?? process.pid, isAlive: input.isAlive ?? processAlive
  }
  mkdirSync(dataDir, { recursive: true, mode: 0o700 })
  const release = takeLock(dataDir, options)
  try {
    const identity: ServiceIdentity = { ...loadIdentity(dataDir, options.now()), serverEpoch: `server-${randomUUID()}` }
    let released = false
    return {
      dataDir, identity,
      release: () => { if (!released) { released = true; release() } }
    }
  } catch (error) {
    release()
    throw error
  }
}
