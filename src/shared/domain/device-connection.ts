/**
 * 设备页(控制端本地)用到的类型。渲染层与主进程共用,不含任何 Node/Electron 依赖,
 * 也**不含**证书、私钥这类凭据 —— 它们只在主进程的安全存储里。
 */
export type DeviceConnectionState = 'idle' | 'connecting' | 'online' | 'offline'

export interface DeviceSummary {
  id: string
  label: string
  /** 直连端点(host:port)。 */
  endpoints: string[]
  pairedAt: number
  /** 备用中转的地址(wss://…);`null` = 只走直连。中转的信任根证书不进渲染层。 */
  relay: string | null
  /** 本端是否选择了这台设备。 */
  selected: boolean
  /** 只有被选中的设备有连接状态;其余为 `idle`。离线时界面标明只读,不回退本机。 */
  state: DeviceConnectionState
}

export interface DeviceInvitationPreview {
  deviceId: string
  /** 设备信任根公钥指纹(小写 hex)。配对前必须展示给用户核对。 */
  fingerprint: string
  endpoints: string[]
  expiresAt: number
}

export interface DeviceSelection {
  /** `null` = 本机。 */
  deviceId: string | null
  /** 每次切换递增;迟到的结果按它判断是否过期。 */
  epoch: number
  state: DeviceConnectionState
}

export interface DevicesState {
  devices: DeviceSummary[]
  current: DeviceSelection
}
