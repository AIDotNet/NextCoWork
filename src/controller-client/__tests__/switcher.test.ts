import { describe, expect, it } from 'vitest'
import type { ControllerClient } from '../client'
import { DeviceSwitcher, type CancelSignal, type ConnectedDevice, type DeviceConnector, type SwitchSnapshot } from '../switcher'

interface Deferred<T> { promise: Promise<T>; resolve(value: T): void; reject(error: unknown): void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((a, b) => { resolve = a; reject = b })
  return { promise, resolve, reject }
}

class FakeDevice implements ConnectedDevice {
  closed = false
  detached = false
  private lost = new Set<() => void>()
  readonly client: ControllerClient
  constructor(readonly name: string) {
    // 在构造函数里建:字段初始化器里读 this.name 时参数属性还没赋值。
    this.client = { detach: () => { this.detached = true }, tag: name } as unknown as ControllerClient
  }
  onLost(listener: () => void): () => void { this.lost.add(listener); return () => { this.lost.delete(listener) } }
  close(): void { this.closed = true }
  drop(): void { for (const listener of [...this.lost]) listener() }
}

class FakeConnector implements DeviceConnector {
  readonly pending: Array<{ deviceId: string; signal: CancelSignal; gate: Deferred<ConnectedDevice> }> = []
  connect(deviceId: string, signal: CancelSignal): Promise<ConnectedDevice> {
    const gate = deferred<ConnectedDevice>()
    this.pending.push({ deviceId, signal, gate })
    return gate.promise
  }
}
const tagOf = (client: ControllerClient): string => (client as unknown as { tag: string }).tag

describe('device switch transaction', () => {
  it('connects, becomes online and reports state changes', async () => {
    const connector = new FakeConnector()
    const seen: SwitchSnapshot[] = []
    const switcher = new DeviceSwitcher(connector, (snapshot) => seen.push(snapshot))
    const selecting = switcher.select('phone-host')
    expect(switcher.snapshot()).toEqual({ deviceId: 'phone-host', epoch: 1, state: 'connecting' })
    connector.pending[0]!.gate.resolve(new FakeDevice('A'))
    await selecting
    expect(switcher.snapshot()).toEqual({ deviceId: 'phone-host', epoch: 1, state: 'online' })
    expect(seen.map((s) => s.state)).toEqual(['connecting', 'online'])
    await expect(switcher.run(async (client) => tagOf(client))).resolves.toBe('A')
  })

  it('refuses calls while connecting, offline or idle — it never falls back to another device or the local machine', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    await expect(switcher.run(async () => 'x')).rejects.toMatchObject({ code: 'disconnected' })
    const selecting = switcher.select('A')
    await expect(switcher.run(async () => 'x')).rejects.toMatchObject({ code: 'disconnected' })
    connector.pending[0]!.gate.reject(new Error('unreachable'))
    await expect(selecting).rejects.toThrow('unreachable')
    expect(switcher.snapshot().state).toBe('offline')
    await expect(switcher.run(async () => 'x')).rejects.toMatchObject({ code: 'disconnected' })
  })

  it('drops a late response that belongs to the previous device after switching', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    const first = switcher.select('A'); connector.pending[0]!.gate.resolve(new FakeDevice('A')); await first

    const slowSave = deferred<string>()
    const saving = switcher.run(() => slowSave.promise) // 发给设备 A 的保存,还没返回
    const second = switcher.select('B'); connector.pending[1]!.gate.resolve(new FakeDevice('B')); await second
    slowSave.resolve('saved-on-A')
    await expect(saving).rejects.toMatchObject({ code: 'stale_binding' })
    // 失败的迟到调用同样被标成过期,而不是把 A 的错误当成 B 的错误。
    const failing = deferred<string>()
    const failed = switcher.run(() => failing.promise)
    const third = switcher.select('C'); connector.pending[2]!.gate.resolve(new FakeDevice('C')); await third
    failing.reject(new Error('A exploded'))
    await expect(failed).rejects.toMatchObject({ code: 'stale_binding' })
    await expect(switcher.run(async (client) => tagOf(client))).resolves.toBe('C')
  })

  it('cancels a superseded connection attempt and closes a connection that arrives late anyway', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    const first = switcher.select('A')
    const second = switcher.select('B')
    expect(connector.pending[0]!.signal.aborted).toBe(true)
    expect(connector.pending[1]!.signal.aborted).toBe(false)
    const lateA = new FakeDevice('A')
    connector.pending[0]!.gate.resolve(lateA) // 无视取消,仍然连上了
    await expect(first).rejects.toMatchObject({ code: 'stale_binding' })
    expect(lateA.closed).toBe(true)
    connector.pending[1]!.gate.resolve(new FakeDevice('B'))
    await second
    expect(switcher.snapshot()).toMatchObject({ deviceId: 'B', state: 'online', epoch: 2 })
    await expect(switcher.run(async (client) => tagOf(client))).resolves.toBe('B')
  })

  it('a superseded attempt that fails does not mark the new device offline', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    const first = switcher.select('A')
    const second = switcher.select('B')
    connector.pending[0]!.gate.reject(new Error('A is down'))
    await expect(first).rejects.toMatchObject({ code: 'stale_binding' })
    expect(switcher.snapshot().state).toBe('connecting')
    connector.pending[1]!.gate.resolve(new FakeDevice('B'))
    await second
    expect(switcher.snapshot().state).toBe('online')
  })

  it('detaches and closes the previous device on switch and marks the device offline when its connection is lost', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    const a = new FakeDevice('A'), b = new FakeDevice('B')
    const first = switcher.select('A'); connector.pending[0]!.gate.resolve(a); await first
    const second = switcher.select('B')
    expect(a.detached).toBe(true); expect(a.closed).toBe(true)
    connector.pending[1]!.gate.resolve(b); await second
    a.drop() // 旧设备迟到的断线通知不能影响当前设备
    expect(switcher.snapshot().state).toBe('online')
    b.drop()
    expect(switcher.snapshot()).toMatchObject({ deviceId: 'B', state: 'offline' })
    await expect(switcher.run(async () => 1)).rejects.toMatchObject({ code: 'disconnected' })
    // 重连同一台设备:世代递增,旧的失败结果不会泄漏进来。
    const again = switcher.select('B'); connector.pending[2]!.gate.resolve(new FakeDevice('B2')); await again
    await expect(switcher.run(async (client) => tagOf(client))).resolves.toBe('B2')
  })

  it('close is idempotent, invalidates in-flight work and refuses further selection', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    const first = switcher.select('A'); const device = new FakeDevice('A'); connector.pending[0]!.gate.resolve(device); await first
    const slow = deferred<string>()
    const running = switcher.run(() => slow.promise)
    switcher.close(); switcher.close()
    expect(device.closed).toBe(true)
    slow.resolve('late')
    await expect(running).rejects.toMatchObject({ code: 'stale_binding' })
    await expect(switcher.select('A')).rejects.toMatchObject({ code: 'disconnected' })
    expect(switcher.snapshot().state).toBe('idle')
  })

  it('deselect returns to idle, invalidates in-flight work and cancels a pending connection, but the switcher stays usable', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector)
    const first = switcher.select('A'); const device = new FakeDevice('A'); connector.pending[0]!.gate.resolve(device); await first
    const slow = deferred<string>()
    const running = switcher.run(() => slow.promise)
    switcher.deselect()
    expect(device.closed).toBe(true)
    expect(switcher.snapshot()).toMatchObject({ deviceId: null, state: 'idle' })
    slow.resolve('late')
    await expect(running).rejects.toMatchObject({ code: 'stale_binding' })
    await expect(switcher.run(async () => 'x')).rejects.toMatchObject({ code: 'disconnected' })

    const second = switcher.select('B')
    switcher.deselect()
    expect(connector.pending[1]!.signal.aborted).toBe(true)
    connector.pending[1]!.gate.resolve(new FakeDevice('B'))
    await expect(second).rejects.toMatchObject({ code: 'stale_binding' })
    expect(switcher.snapshot()).toMatchObject({ deviceId: null, state: 'idle' })

    const third = switcher.select('C'); connector.pending[2]!.gate.resolve(new FakeDevice('C')); await third
    expect(switcher.snapshot()).toMatchObject({ deviceId: 'C', state: 'online' })
  })

  it('an observer that throws cannot change the outcome of a switch', async () => {
    const connector = new FakeConnector()
    const switcher = new DeviceSwitcher(connector, () => { throw new Error('ui crashed') })
    const selecting = switcher.select('A'); connector.pending[0]!.gate.resolve(new FakeDevice('A'))
    await expect(selecting).resolves.toBeUndefined()
    expect(switcher.snapshot().state).toBe('online')
  })
})
