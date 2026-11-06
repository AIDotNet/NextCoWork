import { describe, expect, it } from 'vitest'
import type { DevicesState } from '../../../../shared/domain/device-connection'
import { deviceSwitcherModel } from '../device-switcher-model'

const device = (id: string, label: string) => ({ id, label, endpoints: ['10.0.0.2:7443'], pairedAt: 1, relay: null, selected: false, state: 'idle' as const })
const state = (deviceId: string | null, connection: DevicesState['current']['state'], ids: string[] = ['d1']): DevicesState => ({
  devices: ids.map((id) => device(id, `Device ${id}`)), current: { deviceId, epoch: 1, state: connection }
})

describe('device switcher model', () => {
  it('stays hidden until the state is known and while nothing is paired', () => {
    expect(deviceSwitcherModel(null).visible).toBe(false)
    expect(deviceSwitcherModel(state(null, 'idle', [])).visible).toBe(false)
  })

  it('shows this computer without a warning once a device is paired but not selected', () => {
    expect(deviceSwitcherModel(state(null, 'idle'))).toEqual({ visible: true, remote: false, label: null, state: 'idle', warn: false })
  })

  it('names the selected device and only warns while it is offline or still connecting', () => {
    expect(deviceSwitcherModel(state('d1', 'online'))).toMatchObject({ remote: true, label: 'Device d1', warn: false })
    expect(deviceSwitcherModel(state('d1', 'offline'))).toMatchObject({ remote: true, warn: true })
    expect(deviceSwitcherModel(state('d1', 'connecting'))).toMatchObject({ warn: true })
  })

  it('never presents a remote selection as this computer, even when the device left the list', () => {
    const model = deviceSwitcherModel(state('gone', 'offline', []))
    expect(model).toMatchObject({ visible: true, remote: true, label: 'gone', warn: true })
  })
})
