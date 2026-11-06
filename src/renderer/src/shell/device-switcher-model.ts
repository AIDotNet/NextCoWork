import type { DeviceConnectionState, DevicesState } from '../../../shared/domain/device-connection'

/** 切换器要显示什么。纯函数,没有 React:vitest 在 node 环境里直接测它。 */
export interface DeviceSwitcherModel {
  /** 从没配对过设备时整块不画 —— 对只用本机的人这个入口是噪音。 */
  visible: boolean
  /** 当前是否指向远程设备。 */
  remote: boolean
  /** 当前设备的名字;`null` = 这台电脑(调用方翻译成本地化文案)。 */
  label: string | null
  state: DeviceConnectionState
  /** 选了远程设备却连不上:触发器要有醒目标记,因为此时设备类操作会被拒绝,不会落到本机。 */
  warn: boolean
}

export function deviceSwitcherModel(state: DevicesState | null): DeviceSwitcherModel {
  if (state === null) return { visible: false, remote: false, label: null, state: 'idle', warn: false }
  const selected = state.current.deviceId === null ? undefined : state.devices.find((device) => device.id === state.current.deviceId)
  const remote = state.current.deviceId !== null
  return {
    visible: state.devices.length > 0 || remote,
    remote,
    // 选中的设备已不在清单里(被移除)时也不显示成本机:名字未知就用 id,宁可让人看出异常。
    label: remote ? (selected?.label ?? state.current.deviceId) : null,
    state: state.current.state,
    warn: remote && (state.current.state === 'offline' || state.current.state === 'connecting')
  }
}
