import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { z } from 'zod'
import { closeDatabase, openDatabase, stmt } from '../../../main/db'
import { getDeviceCommand, interruptStaleDeviceCommands, acceptDeviceCommand, transitionDeviceCommand } from '../../../main/db/device-commands'
import { DeviceExecutionContext, DeviceRequestContext } from '../../../main/services/request-context'
import { DeviceProtocolError } from '../../../shared/device/errors'
import type { DeviceBinding } from '../../../shared/device/identity'
import type { DeviceRequest } from '../../../shared/device/contract'
import { DeviceRpcRouter } from '../rpc'

const binding: DeviceBinding = { deviceId: 'device', serverEpoch: 'server', dataEpoch: 'data', controllerId: 'controller',
  clientSessionId: 'session', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }
let root = ''
let revoked = false
const request = (method: string, params: DeviceRequest['params'], key?: string): DeviceRequest =>
  ({ v: 1, type: 'request', id: 'request', binding, method, params, ...(key ? { idempotencyKey: key } : {}) })
const context = (req: DeviceRequest): DeviceRequestContext => new DeviceRequestContext({
  controllerId: binding.controllerId, currentBinding: () => binding, revoked: () => revoked, allows: () => true }, req)
const scope = () => binding

beforeEach(() => { revoked = false; closeDatabase(); root = mkdtempSync(join(tmpdir(), 'ncw-rpc-exec-')); openDatabase(root, { durability: 'full' }) })
afterEach(() => { closeDatabase(); rmSync(root, { recursive: true, force: true }) })

function router(handler: () => Promise<{ ok: boolean }>) {
  const instance = new DeviceRpcRouter()
  instance.execution('tasks.start', z.object({ name: z.string() }), z.object({ ok: z.boolean() }), handler)
  return instance
}

describe('accepted-command execution', () => {
  it('accepts, runs and records the terminal result exactly once for a replayed key', async () => {
    let runs = 0
    const r = router(async () => { runs++; return { ok: true } })
    const req = request('tasks.start', { name: 'a' }, 'k1')
    const first = await r.dispatch(context(req), req, scope) as Record<string, unknown>
    expect(first).toMatchObject({ status: 'succeeded', replayed: false, result: { ok: true } })
    const again = await r.dispatch(context(req), req, scope) as Record<string, unknown>
    expect(again).toMatchObject({ status: 'succeeded', replayed: true })
    expect(runs).toBe(1)
    const other = request('tasks.start', { name: 'b' }, 'k1')
    await expect(r.dispatch(context(other), other, scope)).rejects.toMatchObject({ code: 'conflict' })
  })

  it('records a failure with a sanitised error instead of leaking internals', async () => {
    const r = router(async () => { throw new Error('ENOENT /Users/secret/path.sqlite') })
    const req = request('tasks.start', { name: 'a' }, 'k2')
    const result = await r.dispatch(context(req), req, scope) as { status: string; result: { error: { code: string; message: string } } }
    expect(result.status).toBe('failed')
    expect(JSON.stringify(result)).not.toContain('secret')
    expect(result.result.error.code).toBe('internal')
  })

  it('does not abort an accepted task when the controller is revoked mid-flight', async () => {
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })
    const r = router(async () => { await gate; return { ok: true } })
    const req = request('tasks.start', { name: 'a' }, 'k3')
    const pending = r.dispatch(context(req), req, scope)
    await new Promise((resolve) => setTimeout(resolve, 10))
    revoked = true
    release()
    const result = await pending as { status: string }
    expect(result.status).toBe('succeeded')
    // 控制端已被撤销,不能再经它读;直接看落盘的回执。
    expect(stmt('SELECT status FROM device_commands WHERE idempotency_key=?').get('k3')?.status).toBe('succeeded')
  })

  it('rejects invalid params and missing keys before leaving any receipt', async () => {
    const r = router(async () => ({ ok: true }))
    const bad = request('tasks.start', { nope: 1 }, 'k4')
    await expect(r.dispatch(context(bad), bad, scope)).rejects.toMatchObject({ code: 'invalid_request' })
    const noKey = request('tasks.start', { name: 'a' })
    await expect(r.dispatch(context(noKey), noKey, scope)).rejects.toMatchObject({ code: 'invalid_request' })
    expect(getDeviceCommand(context(bad), 'k4')).toBeNull()
  })

  it('a failing handler error code is not retried under the same key', async () => {
    let runs = 0
    const r = router(async () => { runs++; throw new DeviceProtocolError('conflict', 'busy session') })
    const req = request('tasks.start', { name: 'a' }, 'k5')
    const first = await r.dispatch(context(req), req, scope) as { status: string; result: { error: { code: string } } }
    expect(first).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    await r.dispatch(context(req), req, scope)
    expect(runs).toBe(1)
  })
})

describe('restart recovery of unfinished commands', () => {
  it('marks never-started commands interrupted and half-run commands unknown', () => {
    const accept = (key: string) => {
      const req = request('tasks.start', { name: key }, key)
      acceptDeviceCommand(context(req), req, () => null)
      return req
    }
    accept('never-started')
    const running = accept('half-run')
    transitionDeviceCommand(new DeviceExecutionContext(binding, scope), 'half-run', 'accepted', 'running', null)
    const done = accept('done')
    const exec = new DeviceExecutionContext(binding, scope)
    transitionDeviceCommand(exec, 'done', 'accepted', 'running', null)
    transitionDeviceCommand(exec, 'done', 'running', 'succeeded', { ok: true })

    expect(interruptStaleDeviceCommands()).toBe(2)
    expect(getDeviceCommand(context(running), 'never-started')?.status).toBe('interrupted')
    expect(getDeviceCommand(context(running), 'half-run')?.status).toBe('unknown')
    expect(getDeviceCommand(context(done), 'done')?.status).toBe('succeeded')
    expect(interruptStaleDeviceCommands()).toBe(0)
  })
})
