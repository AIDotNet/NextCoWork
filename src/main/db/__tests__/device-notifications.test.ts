import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DeviceProtocolError } from '../../../shared/device/errors'
import { closeDatabase, db, openDatabase } from '../index'
import {
  getNotificationSubscription, removeNotificationSubscription, saveNotificationSubscription
} from '../device-notifications'
import { DeviceRequestContext } from '../../services/request-context'
import type { DeviceRequest } from '../../../shared/device/contract'
import type { DeviceBinding } from '../../../shared/device/identity'

let directory = ''
const binding: DeviceBinding = { deviceId: 'device', dataEpoch: 'data-1', serverEpoch: 'server-1',
  controllerId: 'phone', clientSessionId: 'client', configScope: 'local', scopeEpoch: 'scope', bindingEpoch: 0 }
const request: DeviceRequest = { v: 1, type: 'request', id: 'request', method: 'notifications.save', binding, params: null }
const ref = 'apns-registration-0001'

function context(input = binding): DeviceRequestContext {
  return new DeviceRequestContext({ controllerId: input.controllerId, currentBinding: () => input,
    revoked: () => false, allows: () => true }, { ...request, binding: input })
}

function failure(run: () => unknown): DeviceProtocolError {
  try {
    run()
  } catch (error) {
    if (error instanceof DeviceProtocolError) return error
    throw error
  }
  throw new Error('expected a DeviceProtocolError')
}

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'ncw-device-notifications-'))
  openDatabase(directory, { durability: 'full' })
})
afterEach(() => { closeDatabase(); rmSync(directory, { recursive: true, force: true }) })

describe('durable notification subscriptions', () => {
  it('registers, updates and removes a subscription under revision control', () => {
    expect(getNotificationSubscription(context())).toBeNull()

    const created = saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0
    }, 10)
    expect(created).toEqual({ platform: 'ios', enabled: true, revision: 1, updatedAt: 10, deliveryConfigured: false })

    const updated = saveNotificationSubscription(context(), {
      registrationRef: 'apns-registration-0002', platform: 'android', enabled: false, expectedRevision: 1
    }, 20)
    expect(updated).toEqual({ platform: 'android', enabled: false, revision: 2, updatedAt: 20, deliveryConfigured: false })

    // 版本不符一律 conflict,而且不动已存的那条。
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 1
    }, 30)).code).toBe('conflict')
    expect(getNotificationSubscription(context())).toEqual(updated)

    // 删除同样要版本相符:1 已经过时,2 才删得掉。
    expect(failure(() => removeNotificationSubscription(context(), 1, 40)).code).toBe('conflict')
    expect(removeNotificationSubscription(context(), 2, 50)).toBe(true)
    expect(getNotificationSubscription(context())).toBeNull()
    // ★ 行没有被删掉,而是留成墓碑(deleted=1):revision 继续 +1,已注销的登记引用被清空。
    const tombstone = db().prepare(`SELECT revision,deleted,enabled,registration_ref FROM device_notification_subscriptions
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=?`)
      .get(binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId)
    expect(tombstone).toEqual({ revision: 3, deleted: 1, enabled: 0, registration_ref: '' })
  })

  it('keeps the revision across delete and recreate so a late command cannot resurrect it', () => {
    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)
    expect(removeNotificationSubscription(context(), 1, 20)).toBe(true)

    // 墓碑期间读到的永远是 null,而且重复删除 expectedRevision 0 得到 false(不是错误)。
    expect(getNotificationSubscription(context())).toBeNull()
    expect(removeNotificationSubscription(context(), 0, 30)).toBe(false)

    // 重新登记必须从 0 开始,但版本沿用墓碑 +1 —— 不回到 1(ABA)。
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 1
    }, 40)).code).toBe('conflict')
    const recreated = saveNotificationSubscription(context(), {
      registrationRef: 'apns-registration-0002', platform: 'ios', enabled: true, expectedRevision: 0
    }, 50)
    expect(recreated).toEqual({ platform: 'ios', enabled: true, revision: 3, updatedAt: 50, deliveryConfigured: false })
    // deleted 被清掉了,registration_ref 换成新的。
    expect(db().prepare(`SELECT deleted,registration_ref FROM device_notification_subscriptions
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=?`)
      .get(binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId))
      .toEqual({ deleted: 0, registration_ref: 'apns-registration-0002' })

    // ★ 迟到指令来自**旧版本**:1(删除前的 active)与 2(墓碑自己)都必须 conflict,
    //   不能碰刚刚重建的这条(当前版本 3)。
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: false, expectedRevision: 1
    }, 60)).code).toBe('conflict')
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: false, expectedRevision: 2
    }, 60)).code).toBe('conflict')
    expect(failure(() => removeNotificationSubscription(context(), 2, 60)).code).toBe('conflict')
    expect(failure(() => removeNotificationSubscription(context(), 1, 60)).code).toBe('conflict')
    // 而重建之后预期的"首次登记拿 0"也不再成立:active 行的版本是 3。
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0
    }, 60)).code).toBe('conflict')
    expect(getNotificationSubscription(context())?.revision).toBe(3)
  })

  it('does not erase the registration reference of a tombstone into any view and reports the row as null', () => {
    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)
    removeNotificationSubscription(context(), 1, 20)
    // 墓碑读出来是 null,而且删掉的引用不会以任何形式回传。
    expect(getNotificationSubscription(context())).toBeNull()
    expect(JSON.stringify(db().prepare('SELECT * FROM device_notification_subscriptions').all())).not.toContain(ref)
  })

  it('does not expose the registration reference in any view', () => {
    const view = saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'android', enabled: true, expectedRevision: 0
    }, 10)
    expect(Object.keys(view).sort()).toEqual(['deliveryConfigured', 'enabled', 'platform', 'revision', 'updatedAt'])
    expect(JSON.stringify(view)).not.toContain(ref)
    expect(JSON.stringify(getNotificationSubscription(context()))).not.toContain(ref)
    // 引用本身仍然落在库里(投递侧将来要靠它去安全存储取真 token)。
    const stored = db().prepare(`SELECT registration_ref FROM device_notification_subscriptions
      WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=?`)
      .get(binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId)
    expect(stored?.['registration_ref']).toBe(ref)
  })

  it('always reports delivery as unconfigured and never writes a domain outbox event', () => {
    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)
    expect(getNotificationSubscription(context())?.deliveryConfigured).toBe(false)
    saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: false, expectedRevision: 1
    }, 20)
    expect(getNotificationSubscription(context())?.deliveryConfigured).toBe(false)
    // 登记引用与订阅状态都不是领域事件:outbox 必须一条都没有。
    expect(db().prepare('SELECT count(*) AS n FROM device_events').get()?.['n']).toBe(0)
  })

  it('rejects a URL or address-shaped registration reference before it reaches the table', () => {
    // ★ 含冒号(即使没有 `//`)、`/`、`@`、空格、空串一律拒。
    //   `data:abc` / `mailto:abc` 这两条正是旧注释声称"拒 URL"、而 `deviceIdSchema`
    //   的字符集含 `:` 时**实际会放行**的 scheme 样式值,所以必须单独钉住。
    for (const registrationRef of [
      'https://push.example/abc', 'mailto:a@example.com', 'mailto:abc', 'data:abc',
      'a/b', 'has space', '', '.leading-dot', 'bad:scheme', 'a'.repeat(129)
    ]) {
      expect(failure(() => saveNotificationSubscription(context(), {
        registrationRef, platform: 'ios', enabled: true, expectedRevision: 0
      })).code).toBe('invalid_request')
    }
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'web' as never, enabled: true, expectedRevision: 0
    })).code).toBe('invalid_request')
    expect(getNotificationSubscription(context())).toBeNull()
    expect(db().prepare('SELECT count(*) AS n FROM device_notification_subscriptions').get()?.['n']).toBe(0)
  })

  it('rejects a stored row whose revision or timestamp is null instead of reading it as zero', () => {
    type RawCell = number | string | Uint8Array | null
    const write = (revision: RawCell, updatedAt: RawCell, enabled: RawCell): void => {
      db().prepare('DELETE FROM device_notification_subscriptions').run()
      db().prepare(`INSERT INTO device_notification_subscriptions(device_id,data_epoch,config_scope,
        controller_id,registration_ref,platform,enabled,revision,updated_at,deleted) VALUES(?,?,?,?,?,?,?,?,?,0)`)
        .run(binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId,
          ref, 'ios', enabled, revision, updatedAt)
    }

    // 全部是坏行:NULL 会被 `Number(null)` 折成 0 —— 那正好是"首次登记"允许的版本。
    write(null, 10, 1)
    expect(failure(() => getNotificationSubscription(context())).code).toBe('internal')
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0
    }, 20)).code).toBe('internal')
    // 绝不能据坏 revision 覆盖数据。
    expect(db().prepare('SELECT revision FROM device_notification_subscriptions').get()?.['revision']).toBeNull()

    for (const broken of [
      { revision: 1.5, updatedAt: 10, enabled: 1 },   // 非整数 revision
      { revision: 1, updatedAt: null, enabled: 1 },    // NULL 时间戳
      { revision: 1, updatedAt: 10, enabled: 2 },     // enabled 不是 0/1
      { revision: 1, updatedAt: 10, enabled: null }   // NULL enabled
    ]) {
      write(broken.revision, broken.updatedAt, broken.enabled)
      expect(failure(() => getNotificationSubscription(context())).code).toBe('internal')
      expect(failure(() => removeNotificationSubscription(context(), 1, 20)).code).toBe('internal')
    }
  })

  it('reports a stored row with a non-positive revision as internal', () => {
    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)
    // `deleted` 有 CHECK(deleted IN (0,1)),`platform` 也有 CHECK —— 两者都写不进坏值,
    // 所以这里用 revision 造坏行:它只受"整数"约束,0 与负数都进得去
    //(写路径永远不会落 0),而解码必须拒掉它们,不能把"还没有这条行"的版本当成可信基线。
    for (const revision of [0, -1]) {
      db().prepare(`UPDATE device_notification_subscriptions SET revision=?
        WHERE device_id=? AND data_epoch=? AND config_scope=? AND controller_id=?`)
        .run(revision, binding.deviceId, binding.dataEpoch, binding.configScope, binding.controllerId)
      expect(failure(() => getNotificationSubscription(context())).code).toBe('internal')
      expect(failure(() => saveNotificationSubscription(context(), {
        registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0
      }, 20)).code).toBe('internal')
    }
  })

  it('refuses and rolls back the first insert when the binding changes after the row is written', () => {
    // ★ 首次 INSERT 分支同样要在返回前 assertCurrent()。这里让绑定在第一条写语句
    //   执行之后才变 —— 用 count 出来的次数控制时机,不碰 DeviceRequestContext 的内部状态。
    //   若缺那次复检,登记引用会带着旧作用域提交,而控制端已经在别的代际上了。
    let reads = 0
    let current = binding
    const live = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => {
      reads += 1
      if (reads === 4) current = { ...binding, dataEpoch: 'data-2' }
      return current
    }, revoked: () => false, allows: () => true }, request)

    expect(failure(() => saveNotificationSubscription(live, {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0
    }, 10)).code).toBe('stale_binding')
    expect(db().prepare('SELECT count(*) AS n FROM device_notification_subscriptions').get()?.['n']).toBe(0)
    expect(getNotificationSubscription(context())).toBeNull()
  })

  it('isolates controller, device, data generation and account, but survives a reconnect of the same controller', () => {
    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)
    for (const patch of [{ controllerId: 'desktop' }, { deviceId: 'other' }, { dataEpoch: 'data-2' }, { configScope: 'account-2' }]) {
      expect(getNotificationSubscription(context({ ...binding, ...patch }))).toBeNull()
    }
    // 重连(clientSessionId / bindingEpoch 变化)仍读回自己登记过的这条。
    const reconnected = { ...binding, clientSessionId: 'new-client', bindingEpoch: 4, serverEpoch: 'server-2' }
    expect(getNotificationSubscription(context(reconnected))?.revision).toBe(1)

    // 另一个控制端有独立的登记槽,删自己的不会碰到别人的。
    const desktop = { ...binding, controllerId: 'desktop' }
    saveNotificationSubscription(context(desktop), { registrationRef: 'fcm-registration-9', platform: 'android', enabled: true, expectedRevision: 0 }, 20)
    expect(removeNotificationSubscription(context(desktop), 1, 30)).toBe(true)
    expect(getNotificationSubscription(context(desktop))).toBeNull()
    expect(getNotificationSubscription(context())?.revision).toBe(1)
  })

  it('refuses a revoked or disallowed control context and leaves the subscription untouched', () => {
    let revoked = false
    let allows = true
    const live = new DeviceRequestContext({ controllerId: binding.controllerId, currentBinding: () => binding,
      revoked: () => revoked, allows: () => allows }, request)
    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)

    revoked = true
    expect(failure(() => saveNotificationSubscription(live, {
      registrationRef: ref, platform: 'ios', enabled: false, expectedRevision: 1
    })).code).toBe('unauthorized')
    expect(failure(() => getNotificationSubscription(live)).code).toBe('unauthorized')
    expect(failure(() => removeNotificationSubscription(live, 1)).code).toBe('unauthorized')

    revoked = false
    allows = false
    expect(failure(() => removeNotificationSubscription(live, 1)).code).toBe('forbidden')
    expect(getNotificationSubscription(context())).toEqual({ platform: 'ios', enabled: true, revision: 1, updatedAt: 10, deliveryConfigured: false })
  })

  it('compares the revision on delete and reports a stale client distinctly from an empty slot', () => {
    // 没登记过:期望 0 = 本来就没什么可删;期望非 0 = 控制端状态过时。
    expect(removeNotificationSubscription(context(), 0)).toBe(false)
    expect(failure(() => removeNotificationSubscription(context(), 1)).code).toBe('conflict')

    saveNotificationSubscription(context(), { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0 }, 10)
    expect(failure(() => removeNotificationSubscription(context(), 0)).code).toBe('conflict')
    expect(failure(() => removeNotificationSubscription(context(), 2)).code).toBe('conflict')
    expect(getNotificationSubscription(context())?.revision).toBe(1)

    expect(removeNotificationSubscription(context(), 1, 20)).toBe(true)
    expect(getNotificationSubscription(context())).toBeNull()
    // 已经是墓碑:再删一次 expectedRevision 0 仍然是 false("本来就没什么可删"),
    // 而不是把墓碑的版本当成一条可删的 active 行。
    expect(removeNotificationSubscription(context(), 0, 30)).toBe(false)
  })

  it('requires an explicitly opened file database and FULL durability', () => {
    closeDatabase()
    expect(failure(() => getNotificationSubscription(context())).code).toBe('internal')
    expect(failure(() => saveNotificationSubscription(context(), {
      registrationRef: ref, platform: 'ios', enabled: true, expectedRevision: 0
    })).code).toBe('internal')
    // 拒绝路径不能顺手打开内存库;同一路径仍要能显式打开。
    openDatabase(directory)
    expect(Number(db().prepare('PRAGMA synchronous').get()?.['synchronous'])).toBe(1)
    expect(failure(() => removeNotificationSubscription(context(), 0)).message).toMatch(/FULL/)
    expect(getNotificationSubscription(context())).toBeNull()
    closeDatabase()
    openDatabase(directory, { durability: 'full' })
    expect(getNotificationSubscription(context())).toBeNull()
  })
})
