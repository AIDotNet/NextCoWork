/**
 * 控制端的设备管理(桌面主进程用,无 Electron):配对、清单、选择、切换、移除。
 *
 * 配对分两步,方便界面在发起网络请求之前让用户核对设备指纹:
 *  1. `inspectInvitation(text)` 只解码并校验邀请,返回要展示的指纹/端点,不联网;
 *  2. `pair(text, { confirmedFingerprint })` 再次解码,指纹必须与用户确认过的一致才继续。
 *
 * 「移除连接」只删本机凭据;「撤销配对」是设备侧的操作(设备页另有入口),两者不混。
 * 当前所选设备离线时 `run` 一律失败为 `disconnected`,不会回退到本机(见 DeviceSwitcher)。
 */
import type { ControllerClient } from '../../controller-client/client'
import { DeviceSwitcher, type DeviceConnector } from '../../controller-client/switcher'
import type { DeviceInvitationPreview, DeviceSelection, DeviceSummary, DevicesState } from '../../shared/domain/device-connection'
import { DeviceProtocolError } from '../../shared/device/errors'
import { decodeInvitation } from '../../shared/device/invitation'
import { pairWithDevice } from '../../transport/node/pairing-client'
import type { DeviceRegistry } from './registry'

export type { DeviceInvitationPreview, DeviceSelection, DeviceSummary, DevicesState }

export class DeviceManager {
  private readonly switcher: DeviceSwitcher

  constructor(
    private readonly registry: DeviceRegistry,
    connector: DeviceConnector,
    private readonly onChange: (state: DevicesState) => void = () => {},
    private readonly now: () => number = Date.now
  ) {
    this.switcher = new DeviceSwitcher(connector, () => this.emit())
  }

  list(): DeviceSummary[] {
    const snapshot = this.switcher.snapshot()
    const selected = this.registry.selected()
    return this.registry.list().map((device) => ({
      id: device.id, label: device.label, endpoints: device.endpoints, pairedAt: device.pairedAt, relay: device.relay?.url ?? null,
      selected: selected === device.id,
      state: snapshot.deviceId === device.id ? snapshot.state : 'idle'
    }))
  }

  /** 当前世代与连接状态;`deviceId === null` 表示使用本机。 */
  current(): DeviceSelection { return this.switcher.snapshot() }

  state(): DevicesState { return { devices: this.list(), current: this.current() } }

  inspectInvitation(text: string): DeviceInvitationPreview {
    const invitation = decodeInvitation(text, this.now())
    return { deviceId: invitation.deviceId, fingerprint: invitation.rootPublicKeySha256, endpoints: invitation.device, expiresAt: invitation.expiresAt }
  }

  async pair(text: string, options: { label: string; confirmedFingerprint: string }): Promise<DeviceSummary> {
    const label = options.label.trim()
    if (label === '' || label.length > 128) throw new DeviceProtocolError('invalid_request', 'Device name must be 1-128 characters')
    const credentials = await pairWithDevice(text, {
      controllerId: this.registry.controllerId(), label, now: this.now(),
      // 用户核对过的指纹必须与邀请里的一致:界面展示与实际信任的是同一把根。
      confirmDevice: (invitation) => invitation.rootPublicKeySha256 === options.confirmedFingerprint.toLowerCase()
    })
    await this.registry.add(credentials, label)
    this.emit()
    return this.list().find((device) => device.id === credentials.deviceId)!
  }

  /** 切到某台设备并记住选择;连接失败时选择仍被记住(界面标明离线),不回退本机。 */
  async select(id: string): Promise<void> {
    this.registry.select(id)
    await this.switcher.select(id)
  }

  /** 回到本机。 */
  useLocal(): void {
    this.registry.select(null)
    this.switcher.deselect()
    this.emit()
  }

  /** 启动时按记住的选择重连。离线只是离线,不改选本机。 */
  async restore(): Promise<void> {
    const id = this.registry.selected()
    if (id === null) return
    try { await this.switcher.select(id) } catch { /* 状态已是 offline,界面据此展示。 */ }
  }

  /**
   * 设置/清除备用中转。只影响**之后**的连接(不打断当前连接);直连永远优先。
   * 中转只转发密文,不能替代配对:设备仍需用配对时的证书做端到端认证。
   */
  setRelay(id: string, relay: { url: string; caPem?: string } | null): DeviceSummary {
    this.registry.setRelay(id, relay)
    this.emit()
    return this.list().find((device) => device.id === id)!
  }

  async remove(id: string): Promise<boolean> {
    if (this.switcher.snapshot().deviceId === id) this.switcher.deselect()
    const removed = await this.registry.remove(id)
    this.emit()
    return removed
  }

  run<T>(work: (client: ControllerClient) => Promise<T>): Promise<T> { return this.switcher.run(work) }

  close(): void { this.switcher.close() }

  private emit(): void {
    try { this.onChange(this.state()) } catch { /* 观察者异常不得影响设备操作。 */ }
  }
}
