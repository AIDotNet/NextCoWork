/**
 * 输入租约:终端 / 浏览器 / 文档 这类「同一时刻只该有一个人在输入」的共享资源。
 *
 * - **观看不需要租约**,多个控制端可以同时看;只有**输入**要求持有租约。
 * - 每次授予都带一个**单调递增的 fencing token**。输入方法必须带着它来,`check` 只认当前
 *   持有者的当前 token:交接或过期之后,旧持有者迟到的按键/点击带的是旧 token,会被拒绝,
 *   而不是插进新持有者的输入里。
 * - 租约会过期(默认 30 秒,续租延长)—— 持有者断线不会把资源永远锁死。别人只能在过期后接手;
 *   在此之前想换人,只能由持有者显式 `handoff`。
 * - 身份来自已认证连接(调用方传 controllerId),不来自请求参数。撤销配对时 `releaseAll`。
 * - 租约只在内存里:服务重启后全部失效,持有者需要重新获取 —— 这是故意的(重启后没有人该
 *   默认还握着一个终端)。
 */
import { DeviceProtocolError } from '../../shared/device/errors'

export const LEASE_KINDS = ['terminal', 'browser', 'document'] as const
export type LeaseKind = (typeof LEASE_KINDS)[number]

export const LEASE_DEFAULT_TTL_MS = 30_000
export const LEASE_MAX_TTL_MS = 5 * 60_000
const MAX_LEASES = 4096

export interface Lease {
  resource: string
  holder: string
  /** 单调递增;输入必须带它。 */
  token: number
  expiresAt: number
}

export interface LeaseOptions {
  now?: () => number
  /** 交接目标必须是仍然有效的已配对控制端。 */
  isActiveController(controllerId: string): boolean
}

const resourceKey = (kind: string, id: string): string => `${kind}:${id}`

export class LeaseManager {
  private readonly leases = new Map<string, Lease>()
  private counter = 0
  private readonly now: () => number

  constructor(private readonly options: LeaseOptions) {
    this.now = options.now ?? Date.now
  }

  private nextToken(): number {
    // 以时钟为下限:即使服务重启、计数器清零,新 token 也不会小于重启前发出的。
    this.counter = Math.max(this.counter + 1, this.now())
    return this.counter
  }

  private live(key: string): Lease | undefined {
    const lease = this.leases.get(key)
    if (lease === undefined) return undefined
    if (lease.expiresAt <= this.now()) { this.leases.delete(key); return undefined }
    return lease
  }

  private ttl(ttlMs: number | undefined): number {
    const value = ttlMs ?? LEASE_DEFAULT_TTL_MS
    if (!Number.isInteger(value) || value < 1000 || value > LEASE_MAX_TTL_MS) throw new DeviceProtocolError('invalid_request', 'Invalid lease duration')
    return value
  }

  /** 获取;自己已持有则续租并保持 token;被别人持有且未过期 → busy。 */
  acquire(kind: LeaseKind, id: string, controllerId: string, ttlMs?: number): Lease {
    const key = resourceKey(kind, id)
    const ttl = this.ttl(ttlMs)
    const current = this.live(key)
    if (current !== undefined) {
      if (current.holder !== controllerId) throw new DeviceProtocolError('busy', 'Another controller holds the input lease')
      current.expiresAt = this.now() + ttl
      return { ...current }
    }
    if (this.leases.size >= MAX_LEASES) throw new DeviceProtocolError('resource_limit', 'Too many active leases')
    const lease: Lease = { resource: key, holder: controllerId, token: this.nextToken(), expiresAt: this.now() + ttl }
    this.leases.set(key, lease)
    return { ...lease }
  }

  /** 续租:必须是当前持有者带着当前 token。 */
  renew(kind: LeaseKind, id: string, controllerId: string, token: number, ttlMs?: number): Lease {
    const lease = this.requireHolder(kind, id, controllerId, token)
    lease.expiresAt = this.now() + this.ttl(ttlMs)
    return { ...lease }
  }

  release(kind: LeaseKind, id: string, controllerId: string, token: number): boolean {
    const key = resourceKey(kind, id)
    const lease = this.live(key)
    if (lease === undefined) return false
    if (lease.holder !== controllerId || lease.token !== token) throw new DeviceProtocolError('stale_binding', 'The lease is no longer yours')
    this.leases.delete(key)
    return true
  }

  /** 持有者主动交接给另一个仍有效的控制端:旧 token 立即作废。 */
  handoff(kind: LeaseKind, id: string, from: string, token: number, to: string, ttlMs?: number): Lease {
    this.requireHolder(kind, id, from, token)
    if (to === from) throw new DeviceProtocolError('invalid_request', 'Cannot hand a lease to its holder')
    if (!this.options.isActiveController(to)) throw new DeviceProtocolError('not_found', 'The target controller is not paired')
    const key = resourceKey(kind, id)
    const lease: Lease = { resource: key, holder: to, token: this.nextToken(), expiresAt: this.now() + this.ttl(ttlMs) }
    this.leases.set(key, lease)
    return { ...lease }
  }

  /** 输入方法的闸:不是当前持有者或 token 过期/被取代 → 拒绝。 */
  check(kind: LeaseKind, id: string, controllerId: string, token: number): void {
    this.requireHolder(kind, id, controllerId, token)
  }

  /** 观看者可读:当前是否有人持有(不泄露 token)。 */
  holderOf(kind: LeaseKind, id: string): { holder: string; expiresAt: number } | null {
    const lease = this.live(resourceKey(kind, id))
    return lease === undefined ? null : { holder: lease.holder, expiresAt: lease.expiresAt }
  }

  /** 撤销配对 / 控制端被移除时调用。返回释放数量。 */
  releaseAll(controllerId: string): number {
    let released = 0
    for (const [key, lease] of this.leases) {
      if (lease.holder === controllerId) { this.leases.delete(key); released++ }
    }
    return released
  }

  size(): number { return this.leases.size }

  private requireHolder(kind: LeaseKind, id: string, controllerId: string, token: number): Lease {
    const lease = this.live(resourceKey(kind, id))
    if (lease === undefined || lease.holder !== controllerId || lease.token !== token) {
      throw new DeviceProtocolError('stale_binding', 'The input lease is not held or has been superseded')
    }
    return lease
  }
}
