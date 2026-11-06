import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { decodeDeviceValue } from '../../../shared/device/codec'
import { closeDatabase, db, openDatabase } from '../index'
import {
  cancelDeviceInteraction, createDeviceInteraction, getDeviceInteraction,
  interactionPayloadDigest, listDeviceInteractions, respondDeviceInteraction
} from '../device-interactions'
import { listDeviceEvents } from '../device-events'
import { DeviceRequestContext } from '../../services/request-context'
import type { DeviceRequest } from '../../../shared/device/contract'
import type { DeviceBinding } from '../../../shared/device/identity'
import type { DeviceValue } from '../../../shared/device/value'

let directory = ''
const scope = { deviceId: 'device', dataEpoch: 'data-1', configScope: 'local', scopeEpoch: 'scope' }
const binding: DeviceBinding = {
  ...scope, serverEpoch: 'server-1', controllerId: 'desktop-a', clientSessionId: 'client', bindingEpoch: 0
}
const payload: DeviceValue = { toolName: 'Bash', input: { command: 'rm -rf build' }, readOnly: false }

function request(input: DeviceBinding): DeviceRequest {
  return { v: 1, type: 'request', id: 'request', method: 'interaction.respond', binding: input, params: null }
}

function context(current: DeviceBinding = binding): DeviceRequestContext {
  return new DeviceRequestContext({
    controllerId: current.controllerId, currentBinding: () => current,
    revoked: () => false, allows: () => true
  }, request(current))
}

/** 广播事件落在保留的 `'*'` 分区上,不属于任何真实控制端。 */
function broadcastEvents() {
  return listDeviceEvents({ ...scope, controllerId: '*' })
}

/** 直接读行,绕开访问器的解码 —— 用来断言"答案到底有没有落盘"。 */
function rawRow(id: string): Record<string, unknown> | undefined {
  return db().prepare('SELECT * FROM device_interactions WHERE id=?').get(id)
}

beforeEach(() => {
  closeDatabase()
  directory = mkdtempSync(join(tmpdir(), 'ncw-device-interactions-'))
  openDatabase(directory, { durability: 'full' })
})
afterEach(() => { closeDatabase(); rmSync(directory, { recursive: true, force: true }) })

describe('durable device interactions', () => {
  it('lets exactly one of two authorized controllers win, even when both send the same answer', () => {
    const created = createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10)
    expect(created).toEqual({
      id: 'approval-1', runId: 'run-1', kind: 'permission', payload, payloadDigest: interactionPayloadDigest(payload),
      status: 'pending', answer: null, resolvedBy: null, createdAt: 10, updatedAt: 10, expiresAt: null
    })

    const desktop = context(binding)
    const phone = context({ ...binding, controllerId: 'phone', clientSessionId: 'phone-client' })
    // 两个控制端都在同一个账号作用域里读得到同一条待决项。
    expect(getDeviceInteraction(phone, 'approval-1')?.status).toBe('pending')
    expect(listDeviceInteractions(desktop)).toHaveLength(1)

    const answer: DeviceValue = { decision: 'allow_once' }
    const digest = interactionPayloadDigest(payload)
    const resolved = respondDeviceInteraction(desktop, { id: 'approval-1', payloadDigest: digest, answer }, 20)
    expect(resolved).toMatchObject({ status: 'resolved', answer, resolvedBy: 'desktop-a', updatedAt: 20 })

    // 第二次:同一答案也必须是 conflict —— 一次性转换,不是"重复提交幂等"。
    expect(() => respondDeviceInteraction(phone, { id: 'approval-1', payloadDigest: digest, answer }, 21))
      .toThrow(/no longer pending/)
    expect(decodeAnswer('approval-1')).toEqual(answer)
    expect(rawRow('approval-1')?.['resolved_by']).toBe('desktop-a')
    // 事件:requested 一条 + resolved 一条,顺序与处理者都可核对。
    expect(broadcastEvents().map((event) => event.event)).toEqual(['interaction.requested', 'interaction.resolved'])
    expect(broadcastEvents().at(-1)?.data).toEqual({ id: 'approval-1', resolvedBy: 'desktop-a' })
  })

  it('isolates by device, data generation, account scope and scope generation', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'question', payload }, 10)
    const patches = [
      { deviceId: 'other-device' }, { dataEpoch: 'data-2' },
      { configScope: 'account-2' }, { scopeEpoch: 'scope-2' }
    ]
    for (const patch of patches) {
      const other = { ...binding, ...patch }
      expect(getDeviceInteraction(context(other), 'approval-1')).toBeNull()
      expect(listDeviceInteractions(context(other))).toEqual([])
      expect(() => respondDeviceInteraction(context(other), {
        id: 'approval-1', payloadDigest: interactionPayloadDigest(payload), answer: { decision: 'allow_once' }
      })).toThrow(/not exist/)
    }
    // 原作用域没有被上面那些失败的回答动过。
    expect(getDeviceInteraction(context(), 'approval-1')?.status).toBe('pending')
    expect(broadcastEvents()).toHaveLength(1)
  })

  it('refuses an expired interaction without writing an answer or an outbox event', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'plan', payload, expiresAt: 1_050 }, 1_000)
    expect(listDeviceInteractions(context(), 100, 1_040)).toHaveLength(1)
    expect(listDeviceInteractions(context(), 100, 1_050)).toEqual([])

    const input = { id: 'approval-1', payloadDigest: interactionPayloadDigest(payload), answer: { approved: true } }
    expect(() => respondDeviceInteraction(context(), input, 1_100)).toThrow(/expired/)
    expect(rawRow('approval-1')?.['status']).toBe('pending')
    expect(rawRow('approval-1')?.['answer']).toBeNull()
    expect(broadcastEvents()).toHaveLength(1)
  })

  it('refuses a response whose payload digest does not match the stored question', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10)
    const other = interactionPayloadDigest({ toolName: 'Write', input: { command: 'x' }, readOnly: false })
    expect(() => respondDeviceInteraction(context(), { id: 'approval-1', payloadDigest: other, answer: null }, 20))
      .toThrow(/payload changed/)
    expect(rawRow('approval-1')?.['answer']).toBeNull()
    // digest 不符就是不落答案,连「空答案」都不算回答。
    expect(listDeviceInteractions(context(), 100, 20)).toHaveLength(1)
    expect(broadcastEvents()).toHaveLength(1)
  })

  it('stores an explicit null answer as a real answer rather than "not answered"', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'question', payload }, 10)
    const resolved = respondDeviceInteraction(context(), {
      id: 'approval-1', payloadDigest: interactionPayloadDigest(payload), answer: null
    }, 20)
    expect(resolved).toMatchObject({ status: 'resolved', answer: null, resolvedBy: 'desktop-a' })
    expect(rawRow('approval-1')?.['answer']).toBeInstanceOf(Uint8Array)
  })

  it('rejects a revoked or disallowed control context and leaves the interaction pending', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10)
    const input = { id: 'approval-1', payloadDigest: interactionPayloadDigest(payload), answer: { decision: 'deny' } }
    // 撤销发生在 context 建好之后:构造器自己就会拒绝一个已经撤销的控制端。
    let revoked = false
    let allows = true
    const live = new DeviceRequestContext({
      controllerId: binding.controllerId, currentBinding: () => binding,
      revoked: () => revoked, allows: () => allows
    }, request(binding))
    revoked = true
    expect(() => respondDeviceInteraction(live, input, 20)).toThrow(/authorized/)
    expect(() => getDeviceInteraction(live, 'approval-1')).toThrow(/authorized/)
    revoked = false
    allows = false
    expect(() => respondDeviceInteraction(live, input, 20)).toThrow(/not allowed/)
    expect(rawRow('approval-1')?.['status']).toBe('pending')
    expect(broadcastEvents()).toHaveLength(1)
  })

  it('does not let cancellation overwrite a resolved answer', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10)
    const answer: DeviceValue = { decision: 'allow_once' }
    respondDeviceInteraction(context(), { id: 'approval-1', payloadDigest: interactionPayloadDigest(payload), answer }, 20)
    expect(cancelDeviceInteraction(scope, 'approval-1', 30)).toBe(false)
    expect(decodeAnswer('approval-1')).toEqual(answer)
    expect(rawRow('approval-1')?.['status']).toBe('resolved')
    expect(broadcastEvents()).toHaveLength(2)

    // 撤销一条真的还挂着的:只转一次,再撤销返回 false。
    createDeviceInteraction(scope, { id: 'approval-2', runId: 'run-1', kind: 'permission', payload }, 40)
    expect(cancelDeviceInteraction(scope, 'approval-2', 50)).toBe(true)
    expect(cancelDeviceInteraction(scope, 'approval-2', 60)).toBe(false)
    expect(rawRow('approval-2')?.['answer']).toBeNull()
    expect(broadcastEvents().map((event) => event.event)).toEqual([
      'interaction.requested', 'interaction.resolved', 'interaction.requested', 'interaction.cancelled'
    ])
  })

  it('refuses to overwrite an existing interaction id and validates create input', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10)
    expect(() => createDeviceInteraction(scope, {
      id: 'approval-1', runId: 'run-2', kind: 'goal', payload: { other: true }
    }, 20)).toThrow(/already exists/)
    expect(rawRow('approval-1')?.['run_id']).toBe('run-1')
    expect(getDeviceInteraction(context(), 'approval-1')?.payload).toEqual(payload)

    expect(() => createDeviceInteraction(scope, {
      id: 'approval-1', runId: 'run-1', kind: 'permission', payload, expiresAt: 10
    }, 10)).toThrow(/future/)
    expect(() => createDeviceInteraction({ ...scope, deviceId: '**' }, {
      id: 'approval-9', runId: 'run-1', kind: 'permission', payload
    })).toThrow(/scope/)
    expect(() => createDeviceInteraction(scope, {
      id: 'approval-9', runId: 'run-1', kind: 'unknown' as never, payload
    })).toThrow(/input/)
    expect(broadcastEvents()).toHaveLength(1)
  })

  it('requires durable file storage and does not overwrite an id from an older scope epoch', () => {
    createDeviceInteraction(scope, { id: 'same-id', runId: 'run-1', kind: 'question', payload }, 10)
    expect(() => createDeviceInteraction({ ...scope, scopeEpoch: 'next' }, {
      id: 'same-id', runId: 'run-2', kind: 'question', payload
    }, 20)).toThrow(/already exists/)
    closeDatabase()
    expect(() => getDeviceInteraction(context(), 'same-id')).toThrow(/explicitly opened/)
    openDatabase(directory)
    expect(() => respondDeviceInteraction(context(), {
      id: 'same-id', payloadDigest: interactionPayloadDigest(payload), answer: null
    }, 20)).toThrow(/FULL/)
    expect(rawRow('same-id')?.status).toBe('pending')
  })

  it('bounds the page limit and returns a deterministic pending page', () => {
    for (const id of ['c', 'a', 'b']) {
      createDeviceInteraction(scope, { id, runId: 'run-1', kind: 'plugin', payload }, 100)
    }
    expect(() => listDeviceInteractions(context(), 0)).toThrow(/limit/)
    expect(() => listDeviceInteractions(context(), 501)).toThrow(/limit/)
    expect(() => listDeviceInteractions(context(), 1.5)).toThrow(/limit/)
    // 同一毫秒创建的三条按 id 打破平局,顺序不依赖插入顺序。
    expect(listDeviceInteractions(context(), 3).map((row) => row.id)).toEqual(['a', 'b', 'c'])
    expect(listDeviceInteractions(context(), 2).map((row) => row.id)).toEqual(['a', 'b'])
    expect(listDeviceInteractions(context(), 500)).toHaveLength(3)
  })

  it('rolls the resolved state back when the same-transaction outbox write fails', () => {
    createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10)
    db().exec('DROP TABLE device_events')
    expect(() => respondDeviceInteraction(context(), {
      id: 'approval-1', payloadDigest: interactionPayloadDigest(payload), answer: { decision: 'allow_once' }
    }, 20)).toThrow()
    expect(rawRow('approval-1')?.['status']).toBe('pending')
    expect(rawRow('approval-1')?.['answer']).toBeNull()
    expect(rawRow('approval-1')?.['resolved_by']).toBeNull()
  })

  it('rolls the requested row back when the same-transaction outbox write fails', () => {
    db().exec('DROP TABLE device_events')
    expect(() => createDeviceInteraction(scope, { id: 'approval-1', runId: 'run-1', kind: 'permission', payload }, 10))
      .toThrow()
    expect(rawRow('approval-1')).toBeUndefined()
  })
})

function decodeAnswer(id: string): DeviceValue {
  const record = db().prepare('SELECT answer FROM device_interactions WHERE id=?').get(id)
  const bytes = record?.['answer']
  if (!(bytes instanceof Uint8Array)) throw new Error('expected a stored answer')
  return decodeDeviceValue(bytes)
}
