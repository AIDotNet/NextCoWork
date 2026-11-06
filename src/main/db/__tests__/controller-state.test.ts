import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { encodeDeviceValue } from '../../../shared/device/codec'
import { DeviceProtocolError } from '../../../shared/device/errors'
import { closeDatabase, db, openDatabase, stmt } from '../index'
import { readControllerState, saveControllerState } from '../controller-state'
import { DeviceRequestContext } from '../../services/request-context'
import type { DeviceRequest } from '../../../shared/device/contract'
import type { DeviceBinding } from '../../../shared/device/identity'
import type { DeviceValue } from '../../../shared/device/value'

let directory = ''
const binding: DeviceBinding = { deviceId: 'device', dataEpoch: 'data-1', serverEpoch: 'server-1',
  controllerId: 'phone', clientSessionId: 'client', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }
const request: DeviceRequest = { v: 1, type: 'request', id: 'request', method: 'state.save', binding, params: null }

function context(input = binding): DeviceRequestContext {
  return new DeviceRequestContext({ controllerId: input.controllerId, currentBinding: () => input,
    revoked: () => false, allows: () => true }, { ...request, binding: input })
}

/** 断言失败的是哪一种协议错误,而不是只对错误文案做正则匹配。 */
function failure(run: () => unknown): DeviceProtocolError {
  try {
    run()
  } catch (error) {
    if (error instanceof DeviceProtocolError) return error
    throw error
  }
  throw new Error('expected a DeviceProtocolError')
}

/** 直接读行,绕开访问器的解码 —— 用来断言"到底往 BLOB 里写了什么"。 */
function rawValue(key: string): unknown {
  return db().prepare(`SELECT value FROM device_controller_state
    WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=? AND view_key=?`)
    .get(binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId, key)?.['value']
}

/** 直接写行,绕开访问器的校验 —— 用来造"库被外部改过"的坏行。 */
type RawCell = number | string | Uint8Array | null
function writeRaw(fields: { revision?: RawCell; value?: RawCell; updatedAt?: RawCell }, key = 'draft.layout'): void {
  stmt(`INSERT INTO device_controller_state(device_id,data_epoch,config_scope,controller_id,view_key,
    revision,value,updated_at) VALUES(?,?,?,?,?,?,?,?)`).run(
    binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId, key,
    fields.revision ?? null, fields.value ?? null, fields.updatedAt ?? null)
}

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'ncw-controller-state-'))
  openDatabase(directory, { durability: 'full' })
})
afterEach(() => { closeDatabase(); rmSync(directory, { recursive: true, force: true }) })

describe('durable controller state', () => {
  it('creates on revision 0, bumps on CAS and keeps binary and explicit null values across a restart', () => {
    expect(readControllerState(context(), 'draft.layout')).toBeNull()

    const first = saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: { columns: [1, 2] } }, 10)
    expect(first).toEqual({ revision: 1, value: { columns: [1, 2] }, updatedAt: 10 })

    const bytes = new Uint8Array([0, 1, 255])
    const second = saveControllerState(context(), { key: 'draft.layout', expectedRevision: 1, value: bytes }, 20)
    expect(second.revision).toBe(2)
    expect(second.value).toEqual(bytes)
    expect(rawValue('draft.layout')).toEqual(encodeDeviceValue(bytes))

    // ★ 显式 null 是合法值,存成 1 字节的 nil,不会退化成「没有这一行」。
    const third = saveControllerState(context(), { key: 'draft.layout', expectedRevision: 2, value: null }, 30)
    expect(third).toEqual({ revision: 3, value: null, updatedAt: 30 })
    expect((rawValue('draft.layout') as Uint8Array).byteLength).toBe(1)

    closeDatabase()
    openDatabase(directory, { durability: 'full' })
    expect(readControllerState(context(), 'draft.layout')).toEqual({ revision: 3, value: null, updatedAt: 30 })
  })

  it('refuses a stale or future revision and never overwrites the stored row', () => {
    saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: 'one' }, 10)

    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 5, value: 'stale' }, 20)).code)
      .toBe('conflict')
    expect(readControllerState(context(), 'draft.layout')).toEqual({ revision: 1, value: 'one', updatedAt: 10 })

    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: 'recreate' }, 20)).code)
      .toBe('conflict')
    expect(readControllerState(context(), 'draft.layout')?.revision).toBe(1)

    expect(saveControllerState(context(), { key: 'draft.layout', expectedRevision: 1, value: 'two' }, 20).revision).toBe(2)
    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 1, value: 'three' }, 30)).code)
      .toBe('conflict')
    expect(readControllerState(context(), 'draft.layout')).toEqual({ revision: 2, value: 'two', updatedAt: 20 })
  })

  it('requires revision 0 for the first write and rejects unsafe revision numbers', () => {
    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 1, value: null })).code)
      .toBe('conflict')
    expect(readControllerState(context(), 'draft.layout')).toBeNull()

    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: -1, value: null })).code)
      .toBe('invalid_request')
    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 1.5, value: null })).code)
      .toBe('invalid_request')
    expect(failure(() => saveControllerState(context(), {
      key: 'draft.layout', expectedRevision: Number.MAX_SAFE_INTEGER + 1, value: null
    })).code).toBe('invalid_request')
    expect(readControllerState(context(), 'draft.layout')).toBeNull()
  })

  it('reports resource_limit instead of overflowing when the stored revision is already MAX_SAFE_INTEGER', () => {
    stmt(`INSERT INTO device_controller_state(device_id,data_epoch,config_scope,controller_id,view_key,
      revision,value,updated_at) VALUES(?,?,?,?,?,?,?,?)`).run(
      binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId, 'draft.layout',
      Number.MAX_SAFE_INTEGER, encodeDeviceValue('last'), 10
    )
    expect(readControllerState(context(), 'draft.layout')?.revision).toBe(Number.MAX_SAFE_INTEGER)
    expect(failure(() => saveControllerState(context(), {
      key: 'draft.layout', expectedRevision: Number.MAX_SAFE_INTEGER, value: 'next'
    }, 20)).code).toBe('resource_limit')
    expect(readControllerState(context(), 'draft.layout')?.value).toBe('last')
  })

  it('rejects a malformed view key or an unserializable value before writing', () => {
    for (const key of ['', 'has space', 'a'.repeat(129), 'draft/../layout']) {
      expect(failure(() => saveControllerState(context(), { key, expectedRevision: 0, value: null })).code)
        .toBe('invalid_request')
      expect(failure(() => readControllerState(context(), key)).code).toBe('invalid_request')
    }
    expect(failure(() => saveControllerState(context(), {
      key: 'draft.layout', expectedRevision: 0, value: undefined as unknown as DeviceValue
    })).code).toBe('invalid_request')
    expect(rawValue('draft.layout')).toBeUndefined()
  })

  it('isolates controller, device, data generation and account, but survives a reconnect of the same controller', () => {
    saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: 'phone-draft' }, 10)

    for (const patch of [{ controllerId: 'desktop' }, { deviceId: 'other' }, { dataEpoch: 'data-2' }, { configScope: 'account-2' }]) {
      expect(readControllerState(context({ ...binding, ...patch }), 'draft.layout')).toBeNull()
    }

    // ★ 同一个控制端重连:clientSessionId / bindingEpoch 变了,草稿必须还在。
    const reconnected = { ...binding, clientSessionId: 'new-client', bindingEpoch: 7, serverEpoch: 'server-2' }
    expect(readControllerState(context(reconnected), 'draft.layout')).toEqual({ revision: 1, value: 'phone-draft', updatedAt: 10 })

    // 另一个控制端可以用自己的 revision 0 建一条同名草稿,互不覆盖。
    const desktop = { ...binding, controllerId: 'desktop' }
    expect(saveControllerState(context(desktop), { key: 'draft.layout', expectedRevision: 0, value: 'desktop-draft' }, 20).revision).toBe(1)
    expect(readControllerState(context(), 'draft.layout')?.value).toBe('phone-draft')
    expect(readControllerState(context(desktop), 'draft.layout')?.value).toBe('desktop-draft')
  })

  it('refuses a revoked or disallowed control context and leaves the row untouched', () => {
    let revoked = false
    let allows = true
    const live = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => binding,
      revoked: () => revoked, allows: () => allows }, request)
    saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: 'kept' }, 10)

    revoked = true
    expect(failure(() => saveControllerState(live, { key: 'draft.layout', expectedRevision: 1, value: 'revoked' })).code)
      .toBe('unauthorized')
    expect(failure(() => readControllerState(live, 'draft.layout')).code).toBe('unauthorized')
    expect(readControllerState(context(), 'draft.layout')).toEqual({ revision: 1, value: 'kept', updatedAt: 10 })

    revoked = false
    allows = false
    expect(failure(() => saveControllerState(live, { key: 'draft.layout', expectedRevision: 1, value: 'denied' })).code)
      .toBe('forbidden')
    expect(readControllerState(context(), 'draft.layout')?.value).toBe('kept')
  })

  it('refuses the commit when the binding changes after validation', () => {
    let current = binding
    const live = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => current,
      revoked: () => false, allows: () => true }, request)
    // 校验(含 value 编码)通过之后、写库之前绑定被换掉 —— 事务内的复检必须拦住它。
    expect(failure(() => saveControllerState(live, {
      key: 'draft.layout',
      expectedRevision: 0,
      get value(): DeviceValue { current = { ...binding, dataEpoch: 'data-2' }; return 'raced' }
    })).code).toBe('stale_binding')
    expect(readControllerState(context(), 'draft.layout')).toBeNull()
  })

  it('refuses and rolls back the first insert when the binding changes after the row is written', () => {
    // ★ INSERT 分支必须和 UPDATE 一样在返回前 assertCurrent():
    //   这里让绑定在**第一条写语句执行之后**才变 —— 用 count 出来的次数控制时机,
    //   不碰 DeviceRequestContext 的内部状态。若缺那次复检,事务会带着旧作用域的行提交,
    //   而控制端已经在别的代际上了。
    let reads = 0
    let current = binding
    const live = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => {
      reads += 1
      // 第 1 次来自构造函数自身的 assertCurrent,第 2 次是 tx 之前的校验,
      // 第 3 次是事务内、写语句之前 —— 所以第 4 次才是"INSERT 之后"那次复检。
      if (reads === 4) current = { ...binding, dataEpoch: 'data-2' }
      return current
    }, revoked: () => false, allows: () => true }, request)

    expect(failure(() => saveControllerState(live, { key: 'draft.layout', expectedRevision: 0, value: 'raced' }, 10)).code)
      .toBe('stale_binding')
    expect(rawValue('draft.layout')).toBeUndefined()
    expect(readControllerState(context(), 'draft.layout')).toBeNull()
  })

  it('treats a stored row with a bad revision, timestamp or value as internal and never overwrites it', () => {
    // ★ `revision` 列是 NOT NULL,所以"这一列没写过"在库里表现为 NULL 只可能出现在
    //   `updated_at` 上;其余坏行是 SQLite 的动态类型放进来的:REAL(1.5)、TEXT('one')、
    //   以及版本 0(写路径永远不会落 0)。三种都不能被 `Number(...)` 折成合法版本。
    for (const broken of [
      { revision: 0, value: encodeDeviceValue('broken'), updatedAt: 10 },
      { revision: 1.5, value: encodeDeviceValue('broken'), updatedAt: 10 },
      { revision: 'one', value: encodeDeviceValue('broken'), updatedAt: 10 },
      { revision: 1, value: 'not-a-blob', updatedAt: 10 }
    ]) {
      db().prepare('DELETE FROM device_controller_state').run()
      writeRaw(broken)
      expect(failure(() => readControllerState(context(), 'draft.layout')).code).toBe('internal')
      // 坏行既不能当成版本 0 被"首次创建"覆盖,也不能被当成一个可信版本去写。
      expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: 'clean' }, 20)).code)
        .toBe('internal')
      expect(rawValue('draft.layout')).toEqual(broken.value)
    }

    // ★ NULL `updated_at` 会被 `Number(null)` 折成 0 —— 那是个"合法"时间戳,
    //   于是坏行会被当成刚写过。必须按 typeof 拒掉。
    db().prepare('DELETE FROM device_controller_state').run()
    writeRaw({ revision: 1, value: encodeDeviceValue('broken'), updatedAt: null })
    expect(failure(() => readControllerState(context(), 'draft.layout')).code).toBe('internal')
    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 1, value: 'clean' }, 20)).code)
      .toBe('internal')
  })

  it('requires an explicitly opened file database and FULL durability', () => {
    closeDatabase()
    expect(failure(() => readControllerState(context(), 'draft.layout')).code).toBe('internal')
    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: null })).code)
      .toBe('internal')
    // 拒绝路径不能顺手打开内存库;同一路径仍要能显式打开。
    openDatabase(directory)
    expect(Number(db().prepare('PRAGMA synchronous').get()?.['synchronous'])).toBe(1)
    expect(failure(() => saveControllerState(context(), { key: 'draft.layout', expectedRevision: 0, value: null })).message)
      .toMatch(/FULL/)
    expect(readControllerState(context(), 'draft.layout')).toBeNull()
    closeDatabase()
    openDatabase(directory, { durability: 'full' })
    expect(readControllerState(context(), 'draft.layout')).toBeNull()
  })
})
