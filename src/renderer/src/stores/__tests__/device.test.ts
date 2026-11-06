import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { DevicesState } from '../../../../shared/domain/device-connection'

const mocks = vi.hoisted(() => ({
  getDevicesState: vi.fn(), onDevicesChanged: vi.fn(), pairDevice: vi.fn(), selectDevice: vi.fn(), useLocalDevice: vi.fn(), removeDevice: vi.fn()
}))
vi.mock('../../services/device', () => mocks)
beforeEach(() => { vi.resetModules(); vi.clearAllMocks() })

const device = (state: DevicesState['current']['state']) => ({ id: 'd1', label: 'Home', endpoints: ['10.0.0.2:7443'], pairedAt: 1, relay: null, selected: true, state })
const snapshot = (state: DevicesState['current']['state']): DevicesState => ({ devices: [device(state)], current: { deviceId: 'd1', epoch: 1, state } })

describe('device store', () => {
  it('keeps a newer broadcast when an older initial read arrives late', async () => {
    let resolveInitial!: (state: DevicesState) => void
    mocks.getDevicesState.mockReturnValue(new Promise<DevicesState>((resolve) => { resolveInitial = resolve }))
    const { useDeviceStore } = await import('../device')
    const pending = useDeviceStore.getState().load()
    const broadcast = mocks.onDevicesChanged.mock.calls[0]![0] as (state: DevicesState) => void
    broadcast(snapshot('offline'))
    resolveInitial(snapshot('online'))
    await pending
    expect(useDeviceStore.getState().state?.current.state).toBe('offline')
  })

  it('reports a failed read instead of showing an empty list, and recovers on the next read', async () => {
    mocks.getDevicesState.mockRejectedValueOnce(new Error('list unreadable'))
    const { useDeviceStore } = await import('../device')
    await useDeviceStore.getState().load()
    expect(useDeviceStore.getState()).toMatchObject({ state: null, error: 'list unreadable' })
    mocks.getDevicesState.mockResolvedValueOnce(snapshot('online'))
    await useDeviceStore.getState().load()
    expect(useDeviceStore.getState()).toMatchObject({ error: null, state: { current: { state: 'online' } } })
  })

  it('replaces its state with exactly what the main process returned and subscribes only once', async () => {
    mocks.getDevicesState.mockResolvedValue(snapshot('idle'))
    mocks.selectDevice.mockResolvedValue(snapshot('connecting'))
    mocks.useLocalDevice.mockResolvedValue({ devices: [{ ...device('idle'), selected: false }], current: { deviceId: null, epoch: 2, state: 'idle' } })
    const { useDeviceStore } = await import('../device')
    await useDeviceStore.getState().load()
    await useDeviceStore.getState().load()
    expect(mocks.onDevicesChanged).toHaveBeenCalledTimes(1)
    await useDeviceStore.getState().select('d1')
    expect(useDeviceStore.getState().state?.current.state).toBe('connecting')
    await useDeviceStore.getState().useLocal()
    expect(useDeviceStore.getState().state?.current).toMatchObject({ deviceId: null, state: 'idle' })
  })

  it('lets a failed action reject to the caller without clobbering the last good state', async () => {
    mocks.getDevicesState.mockResolvedValue(snapshot('online'))
    mocks.pairDevice.mockRejectedValue(new Error('Pairing was not confirmed'))
    const { useDeviceStore } = await import('../device')
    await useDeviceStore.getState().load()
    await expect(useDeviceStore.getState().pair('ncwpair1.x', 'Home', 'f'.repeat(64))).rejects.toThrow('Pairing was not confirmed')
    expect(useDeviceStore.getState().state?.current.state).toBe('online')
  })
})
