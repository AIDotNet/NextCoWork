import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { z } from 'zod'
import { closeDatabase, openDatabase, stmt } from '../../../main/db'
import { getDeviceCommand } from '../../../main/db/device-commands'
import { listDeviceEvents } from '../../../main/db/device-events'
import { readControllerState, saveControllerState } from '../../../main/db/controller-state'
import { DeviceRequestContext } from '../../../main/services/request-context'
import type { DeviceBinding } from '../../../shared/device/identity'
import type { DeviceRequest } from '../../../shared/device/contract'
import { DeviceRpcRouter } from '../rpc'

const binding: DeviceBinding = { deviceId: 'device', serverEpoch: 'server', dataEpoch: 'data', controllerId: 'controller',
  clientSessionId: 'session', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }
let root = ''
function request(method: string, params: DeviceRequest['params'], key?: string): DeviceRequest {
  return { v: 1, type: 'request', id: 'request', binding, method, params, ...(key ? { idempotencyKey: key } : {}) }
}
function context(req: DeviceRequest, revoked = () => false): DeviceRequestContext {
  return new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => binding,
    revoked, allows: (method) => method === req.method }, req)
}
beforeEach(() => { closeDatabase(); root = mkdtempSync(join(tmpdir(), 'ncw-rpc-')); openDatabase(root, { durability: 'full' }) })
afterEach(() => { closeDatabase(); rmSync(root, { recursive: true, force: true }) })

describe('explicit device RPC registry', () => {
  it('rejects unknown methods, invalid inputs and duplicate registration', async () => {
    const router = new DeviceRpcRouter()
    router.query('settings.get', z.null(), z.string(), () => 'ok')
    expect(() => router.query('settings.get', z.null(), z.string(), () => 'other')).toThrow(/already registered/)
    const unknown = request('app.arbitraryIpc', null)
    await expect(router.dispatch(context(unknown), unknown, () => binding)).rejects.toMatchObject({ code: 'not_found' })
    const invalid = request('settings.get', 'invalid')
    await expect(router.dispatch(context(invalid), invalid, () => binding)).rejects.toMatchObject({ code: 'invalid_request' })
  })

  it('rechecks authorization after asynchronous reads', async () => {
    const router = new DeviceRpcRouter()
    let revoked = false
    router.query('settings.get', z.null(), z.string(), async () => { revoked = true; return 'private' })
    const req = request('settings.get', null)
    await expect(router.dispatch(context(req, () => revoked), req, () => binding)).rejects.toMatchObject({ code: 'unauthorized' })
  })

  it('handles numeric CAS in method params and deduplicates before applying a stale revision', async () => {
    const router = new DeviceRpcRouter()
    const input = z.strictObject({ key: z.string(), expectedRevision: z.number().int().min(0), value: z.string() })
    const output = z.strictObject({ revision: z.number(), value: z.string(), updatedAt: z.number() })
    router.transaction('controller.save', input, output, (ctx, params) => {
      const stored = saveControllerState(ctx, params, 10)
      return { ...stored, value: String(stored.value) }
    })
    const req = request('controller.save', { key: 'draft', expectedRevision: 0, value: 'hello' }, 'write-once')
    const ctx = context(req)
    const first = await router.dispatch(ctx, req, () => binding)
    expect(first).toMatchObject({ status: 'succeeded', replayed: false, result: { revision: 1 } })
    // expectedRevision=0 已经过时，但相同命令先返回原回执，不能再次调用CAS handler。
    expect(await router.dispatch(ctx, { ...req, id: 'reconnect' }, () => binding)).toMatchObject({ replayed: true, result: { revision: 1 } })
    expect(readControllerState(ctx, 'draft')).toMatchObject({ revision: 1, value: 'hello' })
    expect(listDeviceEvents(binding)).toHaveLength(3)
    const other = { ...req, idempotencyKey: 'different-command' }
    await expect(router.dispatch(context(other), other, () => binding)).rejects.toMatchObject({ code: 'conflict' })
  })

  it('rolls back the business write, receipt and events when output validation fails', async () => {
    const router = new DeviceRpcRouter()
    router.transaction('settings.save', z.null(), z.string(), () => {
      stmt('INSERT INTO kv(key,json) VALUES(?,?)').run('rpc-bad-output', '1')
      return 42 as never
    })
    const req = request('settings.save', null, 'bad-write')
    const ctx = context(req)
    await expect(router.dispatch(ctx, req, () => binding)).rejects.toMatchObject({ code: 'internal' })
    expect(stmt('SELECT 1 FROM kv WHERE key=?').get('rpc-bad-output')).toBeUndefined()
    expect(getDeviceCommand(ctx, 'bad-write')).toBeNull()
    expect(listDeviceEvents(binding)).toEqual([])
  })

  it('rolls back before commit when a result exceeds the negotiated frame limit', async () => {
    const router = new DeviceRpcRouter()
    router.transaction('settings.save', z.null(), z.string(), () => {
      stmt('INSERT INTO kv(key,json) VALUES(?,?)').run('rpc-oversized', '1')
      return 'x'.repeat(5000)
    })
    const req = request('settings.save', null, 'oversized-write')
    const ctx = context(req)
    await expect(router.dispatch(ctx, req, () => binding, 4096)).rejects.toMatchObject({ code: 'resource_limit' })
    expect(stmt('SELECT 1 FROM kv WHERE key=?').get('rpc-oversized')).toBeUndefined()
    expect(getDeviceCommand(ctx, 'oversized-write')).toBeNull()
    expect(listDeviceEvents(binding)).toEqual([])
  })

  it('refuses missing mutation keys and never opens an implicit in-memory database', async () => {
    const router = new DeviceRpcRouter()
    router.transaction('settings.save', z.null(), z.null(), () => null)
    const noKey = request('settings.save', null)
    await expect(router.dispatch(context(noKey), noKey, () => binding)).rejects.toMatchObject({ code: 'invalid_request' })
    closeDatabase()
    const req = request('settings.save', null, 'write-key')
    await expect(router.dispatch(context(req), req, () => binding)).rejects.toThrow(/explicitly opened/)
    expect(() => openDatabase(root, { durability: 'full' })).not.toThrow()
  })
})
