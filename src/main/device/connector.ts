/**
 * 桌面(Node)的设备连接器:从清单取凭据 → 直连 WSS(mTLS,钉死信任根) → 协议握手;
 * 直连全部失败且配置了自部署中转时,退到「中转承载 + 端到端握手」。
 * 实现 `DeviceConnector`,所以切换事务(`DeviceSwitcher`)对它一无所知,手机端可以换自己的实现。
 *
 * - 端点按邀请里的顺序逐个尝试;取消(切到别的设备)立即中止当前尝试。直连永远优先,中转只是备用。
 * - 握手身份用清单里的 deviceId 与本控制端 id 校验,不接受对端自报。
 * - 经中转时,中转只看到密文:控制端与设备在其上做端到端握手(设备根证书钉死、双向证书认证),
 *   中转无法冒充任何一端。内层帧上限比直连小一个 E2E 头(`E2E_OVERHEAD`)。
 * - 失败不重试业务命令;重连由上层决定(切换器的 `select`)。
 */
import { randomUUID } from 'node:crypto'
import { connectController } from '../../controller-client/connect'
import type { Dispose, SecureTransport } from '../../controller-client/ports'
import type { CancelSignal, ConnectedDevice, DeviceConnector } from '../../controller-client/switcher'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'
import { connectDirectDevice } from '../../transport/node/direct'
import { connectE2eClient, E2E_OVERHEAD } from '../../transport/node/e2e'
import { connectViaRelay } from '../../transport/node/relay-client'
import type { PairedCredentials } from '../../transport/node/pairing-client'
import type { DeviceRegistry } from './registry'

type Dial = (url: string, credentials: Parameters<typeof connectDirectDevice>[1], options: { signal?: AbortSignal }) => Promise<SecureTransport>
type DialRelay = (relay: string, deviceId: string, options: { caPem?: string; signal?: AbortSignal }) => Promise<SecureTransport>

export interface ConnectorOptions {
  /** 测试注入;默认真实直连。 */
  dial?: Dial
  /** 测试注入;默认真实中转。 */
  dialRelay?: DialRelay
}

export class NodeDeviceConnector implements DeviceConnector {
  private readonly dial: Dial
  private readonly dialRelay: DialRelay
  private readonly resyncListeners = new Set<(streamId: string) => void>()

  constructor(private readonly registry: DeviceRegistry, options: ConnectorOptions = {}) {
    this.dial = options.dial ?? connectDirectDevice
    this.dialRelay = options.dialRelay ?? connectViaRelay
  }

  /** 任一已建立的连接上某条事件流出现游标缺口(需要重新 attach 补齐)。返回取消订阅。 */
  onResync(listener: (streamId: string) => void): Dispose {
    this.resyncListeners.add(listener)
    return () => { this.resyncListeners.delete(listener) }
  }

  async connect(deviceId: string, signal: CancelSignal): Promise<ConnectedDevice> {
    const credentials = await this.registry.credentials(deviceId)
    if (credentials === undefined) throw new DeviceProtocolError('not_found', 'Device is not paired')
    const controller = new AbortController()
    const stopBridge = signal.onAbort(() => controller.abort())
    try {
      let lastError: unknown = new DeviceProtocolError('disconnected', 'Device is not reachable')
      for (const endpoint of credentials.device) {
        if (signal.aborted) throw new DeviceProtocolError('disconnected', 'Device connection was cancelled')
        let transport: SecureTransport
        try {
          transport = await this.dial(`wss://${endpoint}/device`, credentials, { signal: controller.signal })
        } catch (error) { lastError = error; continue }
        // 握手阶段失败(对端身份不对/被撤销)换端点也不会好,所以不在这里吞掉,直接上报。
        return connected(await this.handshake(transport, deviceId, DEVICE_FRAME_MAX_BYTES), transport)
      }
      const relay = this.registry.get(deviceId)?.relay
      if (relay === undefined) throw lastError
      if (signal.aborted) throw new DeviceProtocolError('disconnected', 'Device connection was cancelled')
      return await this.connectViaRelay(credentials, relay, controller.signal)
    } finally {
      stopBridge()
    }
  }

  private async connectViaRelay(credentials: PairedCredentials, relay: { url: string; caPem?: string }, signal: AbortSignal): Promise<ConnectedDevice> {
    const carrier = await this.dialRelay(relay.url, credentials.deviceId, { ...(relay.caPem !== undefined && { caPem: relay.caPem }), signal })
    let secure: SecureTransport
    try {
      secure = await connectE2eClient(carrier, { caPem: credentials.caPem, certificatePem: credentials.certificatePem, privateKeyPem: credentials.privateKeyPem })
    } catch (error) { carrier.close(); throw error }
    return connected(await this.handshake(secure, credentials.deviceId, DEVICE_FRAME_MAX_BYTES - E2E_OVERHEAD), secure)
  }

  private async handshake(transport: SecureTransport, deviceId: string, maxFrameBytes: number): Promise<ConnectedDevice['client']> {
    const { client } = await connectController(transport, {
      expectedDeviceId: deviceId, expectedControllerId: this.registry.controllerId(),
      clientSessionId: `ncw-${randomUUID()}`, bindingEpoch: 0, nextId: () => randomUUID(),
      onResyncRequired: (streamId) => { for (const listener of [...this.resyncListeners]) { try { listener(streamId) } catch { /* 其余监听者仍需通知。 */ } } },
      clock: { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } },
      capabilities: { kind: 'desktop', maxFrameBytes, features: ['tasks', 'interactions', 'resources', 'range', 'terminal'] }
    })
    return client
  }
}

function connected(client: ConnectedDevice['client'], transport: SecureTransport): ConnectedDevice {
  let closedByUs = false
  return {
    client,
    onLost(listener): Dispose {
      return transport.onClose(() => { if (!closedByUs) listener() })
    },
    close(): void { closedByUs = true; transport.close() }
  }
}
