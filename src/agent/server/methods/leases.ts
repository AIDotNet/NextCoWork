/**
 * 输入租约的 RPC。身份取自已认证连接(`execution.binding.controllerId`),params 里没有任何身份字段。
 * 租约是内存态的临时授权:写操作仍走「已接受回执」,以便重放同一个 key 不会重复获取/交接。
 */
import { z } from 'zod'
import {
  DEVICE_METHODS, leaseResult, leasesAcquireParams, leasesHandoffParams, leasesHolderParams, leasesReleaseParams, leasesRenewParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { Lease, LeaseManager } from '../lease'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const view = (lease: Lease) => ({ resource: lease.resource, token: lease.token, expiresAt: lease.expiresAt })

export function registerLeaseMethods(router: DeviceRpcRouter, leases: LeaseManager): void {
  router.execution(DEVICE_METHODS.leasesAcquire, leasesAcquireParams, leaseResult,
    (execution, p) => view(leases.acquire(p.kind, p.resourceId, execution.binding.controllerId, p.ttlMs)))

  router.execution(DEVICE_METHODS.leasesRenew, leasesRenewParams, leaseResult,
    (execution, p) => view(leases.renew(p.kind, p.resourceId, execution.binding.controllerId, p.token, p.ttlMs)))

  router.execution(DEVICE_METHODS.leasesRelease, leasesReleaseParams, z.strictObject({ released: z.boolean() }),
    (execution, p) => ({ released: leases.release(p.kind, p.resourceId, execution.binding.controllerId, p.token) }))

  router.execution(DEVICE_METHODS.leasesHandoff, leasesHandoffParams, leaseResult,
    (execution, p) => view(leases.handoff(p.kind, p.resourceId, execution.binding.controllerId, p.token, p.to, p.ttlMs)))

  // 观看者可见「谁在输入」,看不到 token。
  router.query(DEVICE_METHODS.leasesHolder, leasesHolderParams, anyValue,
    (_context, p) => (leases.holderOf(p.kind, p.resourceId) ?? null) as DeviceValue)
}
