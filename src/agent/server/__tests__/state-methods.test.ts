import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'
import { registerStateMethods } from '../methods/state'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }

beforeEach(async () => {
  root = mkdtempSync(join(tmpdir(), 'ncw-state-methods-'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0,
    features: [], register: (router) => registerStateMethods(router) })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  rmSync(root, { recursive: true, force: true })
})

async function controller(controllerId: string): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text,
    { controllerId, label: controllerId, confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId,
    expectedControllerId: controllerId, clientSessionId: `s-${controllerId}`, bindingEpoch: 0, nextId: () => `r${++n}`, clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

describe('controller state over the wire', () => {
  it('versions writes with CAS and isolates each controller', async () => {
    const phone = await controller('phone')
    const tablet = await controller('tablet')
    expect(await phone.query('controllerState.read', { key: 'layout' })).toBeNull()

    const first = await phone.command('controllerState.save', { key: 'layout', expectedRevision: 0, value: { tab: 1 } }, 's1') as Receipt
    expect(first).toMatchObject({ status: 'succeeded', result: { revision: 1, value: { tab: 1 } } })
    // 同样的前置版本再写一次(另一个 key)= 过期写,冲突;原值保持。
    const stale = await phone.command('controllerState.save', { key: 'layout', expectedRevision: 0, value: { tab: 9 } }, 's2').catch((e: unknown) => e)
    expect(stale).toMatchObject({ code: 'conflict' })
    expect(await phone.query('controllerState.read', { key: 'layout' })).toMatchObject({ revision: 1, value: { tab: 1 } })
    const second = await phone.command('controllerState.save', { key: 'layout', expectedRevision: 1, value: { tab: 2 } }, 's3') as Receipt
    expect(second.result).toMatchObject({ revision: 2 })

    // 另一个控制端看不见,也写不到这一份。
    expect(await tablet.query('controllerState.read', { key: 'layout' })).toBeNull()
    await tablet.command('controllerState.save', { key: 'layout', expectedRevision: 0, value: { tab: 'tablet' } }, 't1')
    expect(await phone.query('controllerState.read', { key: 'layout' })).toMatchObject({ value: { tab: 2 } })
    phone.detach(); tablet.detach()
  })

  it('rejects malformed keys and values before leaving a receipt', async () => {
    const phone = await controller('phone')
    await expect(phone.query('controllerState.read', { key: 'bad key!' })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(phone.command('controllerState.save', { key: 'k', expectedRevision: -1, value: 1 }, 'b1')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(phone.command('controllerState.save', { key: 'k', value: 1 }, 'b2')).rejects.toMatchObject({ code: 'invalid_request' })
    phone.detach()
  })
})

describe('notification subscription registration over the wire', () => {
  const save = (client: ControllerClient, key: string, expectedRevision: number, ref = 'apns-registration-0001') =>
    client.command('notifications.save', { registrationRef: ref, platform: 'ios', enabled: true, expectedRevision }, key)

  it('registers, updates and removes one subscription per controller, never claiming delivery is configured', async () => {
    const phone = await controller('phone')
    expect(await phone.query('notifications.get', null)).toBeNull()
    const created = await save(phone, 'n1', 0) as Receipt
    expect(created.result).toMatchObject({ platform: 'ios', enabled: true, revision: 1, deliveryConfigured: false })
    expect(JSON.stringify(created)).not.toContain('apns-registration-0001') // 引用本身不回显
    await expect(save(phone, 'n2', 0)).rejects.toMatchObject({ code: 'conflict' })
    const updated = await save(phone, 'n3', 1, 'apns-registration-0002') as Receipt
    expect(updated.result).toMatchObject({ revision: 2 })
    expect((await phone.command('notifications.remove', { expectedRevision: 2 }, 'n4') as Receipt).result).toEqual({ removed: true })
    expect(await phone.query('notifications.get', null)).toBeNull()
    // 墓碑:删除后重新登记仍要从 0 开始,但版本不回到 1(挡住旧版本的迟到指令)。
    const again = await save(phone, 'n5', 0) as Receipt
    expect(again.result).toMatchObject({ revision: 4 })
    await expect(phone.command('notifications.remove', { expectedRevision: 2 }, 'n6')).rejects.toMatchObject({ code: 'conflict' })
    phone.detach()
  })

  it('rejects address-shaped references and unsupported platforms', async () => {
    const phone = await controller('phone')
    for (const ref of ['https://push.example/token', 'mailto:a@b.c', 'data:abc', 'a b']) {
      await expect(save(phone, `u-${ref.length}`, 0, ref)).rejects.toMatchObject({ code: 'invalid_request' })
    }
    await expect(phone.command('notifications.save', { registrationRef: 'ok-ref-1', platform: 'windows', enabled: true, expectedRevision: 0 }, 'p1'))
      .rejects.toMatchObject({ code: 'invalid_request' })
    phone.detach()
  })
})
