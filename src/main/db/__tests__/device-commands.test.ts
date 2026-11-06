import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { closeDatabase, db, openDatabase, stmt } from '../index'
import { acceptDeviceCommand, getDeviceCommand, transitionDeviceCommand } from '../device-commands'
import { appendDeviceEvent, listDeviceEvents } from '../device-events'
import { DeviceExecutionContext, DeviceRequestContext } from '../../services/request-context'
import type { DeviceBinding } from '../../../shared/device/identity'
import type { DeviceRequest } from '../../../shared/device/contract'

let directory = ''
const binding: DeviceBinding = { deviceId: 'device', dataEpoch: 'data-1', serverEpoch: 'server-1',
  controllerId: 'phone', clientSessionId: 'client', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }
const request: DeviceRequest = { v: 1, type: 'request', id: 'request', method: 'agent.run', binding,
  params: { text: 'one message' }, idempotencyKey: 'same-command' }
function context(input = request, current = () => input.binding) {
  return new DeviceRequestContext({ controllerId: input.binding.controllerId, currentBinding: current,
    revoked: () => false, allows: () => true }, input)
}

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'ncw-device-ledger-'))
  openDatabase(directory, { durability: 'full' })
})
afterEach(() => { closeDatabase(); rmSync(directory, { recursive: true, force: true }) })

describe('durable device commands', () => {
  it('accepts once before enqueue and returns the persisted receipt after reconnect/restart', () => {
    let enqueued = 0
    const first = acceptDeviceCommand(context(), request, () => { enqueued++; return { accepted: true, image: new Uint8Array([1, 2]) } }, 10)
    expect(first.replayed).toBe(false)
    closeDatabase(); openDatabase(directory, { durability: 'full' })
    const reconnected = { ...request, id: 'new-request', binding: { ...binding, clientSessionId: 'new-client', serverEpoch: 'server-2' } }
    const second = acceptDeviceCommand(context(reconnected), reconnected, () => { enqueued++; return null }, 20)
    expect(second).toEqual({ replayed: true, receipt: first.receipt })
    expect(enqueued).toBe(1)
    expect(listDeviceEvents(binding)).toHaveLength(1)
  })

  it('keeps receipt and outbox atomic with the business queue write', () => {
    expect(() => acceptDeviceCommand(context(), request, () => {
      stmt('INSERT INTO kv(key,json) VALUES(?,?)').run('device-test-queue', '1')
      throw new Error('queue failed')
    })).toThrow('queue failed')
    expect(getDeviceCommand(context(), 'same-command')).toBeNull()
    expect(listDeviceEvents(binding)).toEqual([])
    expect(stmt('SELECT json FROM kv WHERE key=?').get('device-test-queue')).toBeUndefined()
  })

  it('rejects reuse for different parameters or preconditions without a second enqueue', () => {
    acceptDeviceCommand(context(), request, () => null)
    const alteredRequests: DeviceRequest[] = [
      { ...request, params: { text: 'another' } },
      { ...request, params: { text: 'one message', expectedRevision: 2 } }
    ]
    for (const altered of alteredRequests) {
      expect(() => acceptDeviceCommand(context(altered), altered, () => { throw new Error('must not enqueue') })).toThrow(/Idempotency/)
    }
  })

  it('isolates controller, device, data generation and account', () => {
    acceptDeviceCommand(context(), request, () => 'original')
    for (const patch of [{ controllerId: 'other' }, { deviceId: 'other' }, { dataEpoch: 'data-2' }, { configScope: 'account-2' }]) {
      const input = { ...request, binding: { ...binding, ...patch } }
      expect(getDeviceCommand(context(input), 'same-command')).toBeNull()
      expect(listDeviceEvents(input.binding)).toEqual([])
      expect(acceptDeviceCommand(context(input), input, () => 'independent').replayed).toBe(false)
    }
  })

  it('rejects mutation when the scope changes inside acceptance and rolls back', () => {
    let current = binding
    const ctx = context(request, () => current)
    expect(() => acceptDeviceCommand(ctx, request, () => {
      current = { ...binding, scopeEpoch: 'changed' }
      return null
    })).toThrow(/binding changed/)
    expect(getDeviceCommand(context(), 'same-command')).toBeNull()
  })

  it('does not borrow another method or device authorization context', () => {
    for (const altered of [{ ...request, method: 'files.delete' }, { ...request, binding: { ...binding, deviceId: 'other' } }]) {
      expect(() => acceptDeviceCommand(context(), altered, () => null)).toThrow(/authorization context/)
    }
    expect(listDeviceEvents(binding)).toEqual([])
  })

  it('rechecks revocation before committing and prevents synchronous acceptance reentry', () => {
    let revoked = false
    const ctx = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => binding,
      revoked: () => revoked, allows: () => true }, request)
    expect(() => acceptDeviceCommand(ctx, request, () => { revoked = true; return null })).toThrow(/authorized/)
    expect(() => acceptDeviceCommand(context(), request, () => acceptDeviceCommand(context(), request, () => null) as never)).toThrow(/progress/)
    expect(getDeviceCommand(context(), 'same-command')).toBeNull()
  })

  it('does not acknowledge commands in the implicit in-memory fallback database', () => {
    closeDatabase()
    expect(() => acceptDeviceCommand(context(), request, () => null)).toThrow(/explicitly opened/)
    expect(() => getDeviceCommand(context(), 'same-command')).toThrow(/explicitly opened/)
    // 拒绝路径不能顺手打开内存库；同一路径应仍能正常显式打开。
    openDatabase(directory, { durability: 'full' })
    expect(acceptDeviceCommand(context(), request, () => null).replayed).toBe(false)
  })

  it('requires FULL durability without altering the existing desktop default', () => {
    closeDatabase(); openDatabase(directory)
    expect(Number(db().prepare('PRAGMA synchronous').get()?.synchronous)).toBe(1)
    expect(() => acceptDeviceCommand(context(), request, () => null)).toThrow(/FULL/)
    expect(listDeviceEvents(binding)).toEqual([])
  })

  it('transitions by expected status and commits each event exactly once', () => {
    const ctx = context()
    acceptDeviceCommand(ctx, request, () => ({ queued: true }))
    const execution = new DeviceExecutionContext(binding, () => binding)
    transitionDeviceCommand(execution, 'same-command', 'accepted', 'running', null)
    transitionDeviceCommand(execution, 'same-command', 'running', 'interrupted', { reason: 'restart' })
    expect(() => transitionDeviceCommand(execution, 'same-command', 'running', 'succeeded', null)).toThrow(/changed/)
    expect(() => transitionDeviceCommand(execution, 'same-command', 'interrupted', 'running', null)).toThrow(/transition/)
    expect(acceptDeviceCommand(ctx, request, () => { throw new Error('must not restart') }).receipt.status).toBe('interrupted')
    const events = listDeviceEvents(binding)
    expect(events).toHaveLength(3)
    expect(listDeviceEvents(binding, events[0]!.cursor, 1)).toHaveLength(1)
  })

  it('allows an accepted task to finish after its controller is revoked, but not after a data restore', () => {
    let revoked = false
    let current = binding
    const requester = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => current,
      revoked: () => revoked, allows: () => true }, request)
    const execution = new DeviceExecutionContext(binding, () => current)
    acceptDeviceCommand(requester, request, () => null)
    transitionDeviceCommand(execution, 'same-command', 'accepted', 'running', null)
    revoked = true
    current = { ...binding, clientSessionId: 'another-console', bindingEpoch: 99 }
    expect(() => requester.assertCurrent()).toThrow(/authorized/)
    expect(transitionDeviceCommand(execution, 'same-command', 'running', 'succeeded', { result: 'done' }).status).toBe('succeeded')
    current = { ...current, dataEpoch: 'restored-data' }
    expect(() => execution.assertCurrent()).toThrow(/scope changed/)
  })

  it('does not permit outbox writes outside a business transaction or unsafe cursors', () => {
    expect(() => appendDeviceEvent(binding, 'command.changed', null, 1)).toThrow(/transaction/)
    expect(() => listDeviceEvents(binding, '9223372036854775808')).toThrow(/cursor/)
    expect(() => listDeviceEvents(binding, '0', 100_000)).toThrow(/limit/)
  })
})
