/**
 * 设备服务装配:数据根 → 数据库 → 信任记录 → WSS 监听 → 网关。
 *
 * 本文件不含业务:业务方法由调用方通过 `register(router, identity)` 登记。
 * ★ 停止服务只释放连接、监听、数据库和锁 —— 运行中的 Agent 任务由各自的执行器收尾
 * (`onStop`),不因为控制端断开而被中止。
 */
import type { ClientClock } from '../../controller-client/ports'
import { closeDatabase, openDatabase } from '../../main/db'
import { interruptStaleDeviceCommands } from '../../main/db/device-commands'
import { createEnrollment, findControllerByFingerprint, isControllerRevoked, revokeController } from '../../main/db/device-trust'
import { encodeInvitation } from '../../shared/device/invitation'
import { listenPairingServer, type PairingServer } from '../../transport/node/pairing'
import { listenDeviceServer, type DeviceServer } from '../../transport/node/server'
import { acceptE2eServer, E2E_OVERHEAD } from '../../transport/node/e2e'
import { connectDeviceToRelay, type DeviceRelayConnection } from '../../transport/node/relay-client'
import type { SecureTransport } from '../../controller-client/ports'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { ServerCapabilities } from '../../shared/device/capabilities'
import { DeviceGatewayConnection } from './gateway'
import { LeaseManager } from './lease'
import { DeviceRpcRouter } from './rpc'
import { createPairingHandler } from './pairing'
import { loadOrCreatePki } from './pki'
import { acquireServiceRoot, type ServiceIdentity, type ServiceRoot } from './root'

export interface DeviceServiceOptions {
  dataDir: string
  host: string
  port: number
  /** 配对端点端口;0 = 随机。 */
  pairingPort: number
  /**
   * 控制端用来连接的主机名/IP:同时写进服务端证书 SAN 和邀请。默认只含 `host`。
   * 监听通配地址(0.0.0.0 / ::)时必须显式给出 —— 通配地址不是任何控制端能连的地址。
   */
  advertiseHosts?: string[]
  /**
   * 登记业务方法。只有这里登记的方法能被网络调用。`services` 是服务持有的共享状态:
   * `leases` 是输入租约;`onRevoke` 登记「控制端被撤销时」要立即清理自己名下资源的回调
   * (租约由服务自己释放,上传等由登记者清理)。
   */
  register?(router: DeviceRpcRouter, identity: ServiceIdentity,
    services: { leases: LeaseManager; onRevoke(listener: (controllerId: string) => void): void }): void
  /** 停止服务时收尾自己的执行器(Agent、终端等)。在释放数据库之前调用。 */
  onStop?(): Promise<void> | void
  /**
   * 进入排空:调用方在这里关掉「开始新工作」的入口(Agent 启动闸门等)。
   * 已有的连接与已在跑的任务不受影响。
   */
  onDrain?(): void
  maxFrameBytes?: number
  features?: ServerCapabilities['features']
  clock?: ClientClock
  now?: () => number
}

export interface DeviceService {
  readonly identity: ServiceIdentity
  address(): { host: string; port: number }
  pairingAddress(): { host: string; port: number }
  /** 签发一次性邀请(5 分钟)。文本只出现这一次,库里只存摘要。 */
  createInvitation(): { text: string; expiresAt: number }
  connections(): number
  /** 撤销配对并立即断开该控制端的所有连接。返回是否有记录被撤销。 */
  revoke(controllerId: string): boolean
  /**
   * 优雅升级的第一步:不再接受新配对、不再签发邀请、`onDrain` 关掉新任务入口;
   * 已配对控制端的现有连接与重连照常工作,已在跑的任务继续到结束(控制端仍可审批、停止、观看)。
   * 幂等。之后由调用方在任务排空时 `stop()`。
   */
  drain(): Promise<void>
  draining(): boolean
  /**
   * 经中转对外可达:设备**出站**连到中转(不开入站端口),每个控制端会话先做端到端握手
   * (用同一个设备根与信任记录认证),再接入同一个网关。中转看不到任何业务内容。
   * 返回连接句柄;`stop()` 会一并关闭。中转注册凭据与设备配对凭据无关。
   */
  attachRelay(relayUrl: string, options: { token: string; caPem?: string; onState?(state: 'connected' | 'disconnected' | 'rejected'): void }): DeviceRelayConnection
  stop(): Promise<void>
}

const realClock: ClientClock = {
  schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) }
}

export async function startDeviceService(options: DeviceServiceOptions): Promise<DeviceService> {
  const now = options.now ?? Date.now
  const advertise = options.advertiseHosts ?? [options.host]
  if (advertise.length === 0 || advertise.some((host) => ['0.0.0.0', '::', '[::]', ''].includes(host))) {
    throw new DeviceProtocolError('invalid_request', 'Listening on a wildcard address requires explicit advertise hosts')
  }
  const root: ServiceRoot = acquireServiceRoot(options.dataDir, { now })
  let databaseOpen = false
  const live = new Map<string, Set<DeviceGatewayConnection>>()
  let listener: DeviceServer
  let pairing: PairingServer | undefined
  let pki: Awaited<ReturnType<typeof loadOrCreatePki>>
  let adopt: (transport: SecureTransport, controllerId: string, maxFrameBytes: number) => void
  const relays = new Set<DeviceRelayConnection>()
  // 租约是内存态:交接目标必须是仍有效(未撤销)的已配对控制端。
  const leases = new LeaseManager({ now, isActiveController: (id) => !isControllerRevoked(id) })
  const revokeListeners = new Set<(controllerId: string) => void>()
  try {
    openDatabase(options.dataDir, { durability: 'full' })
    databaseOpen = true
    // 上一个进程留下的未完成命令:没开始的标 interrupted,执行到一半的标 unknown。
    interruptStaleDeviceCommands(now())
    const router = new DeviceRpcRouter()
    options.register?.(router, root.identity, { leases, onRevoke: (listener) => { revokeListeners.add(listener) } })
    const capabilities = { maxFrameBytes: options.maxFrameBytes ?? 1024 * 1024, features: options.features ?? [] }
    const clock = options.clock ?? realClock
    /**
     * 把一条**已认证**的传输接入网关。直连(TLS 客户端证书)与中转(端到端握手)两条路径
     * 在这里汇合,之后的授权、撤销、限流、方法分发完全相同。
     */
    adopt = (transport, controllerId, maxFrameBytes) => {
      const set = live.get(controllerId) ?? new Set<DeviceGatewayConnection>()
      live.set(controllerId, set)
      const gateway: DeviceGatewayConnection = new DeviceGatewayConnection(transport, {
        peer: {
          controllerId,
          // 每次请求都查库:撤销对已建立的连接立即生效。
          revoked: () => isControllerRevoked(controllerId),
          allows: () => true
        },
        router, clock, capabilities: { ...capabilities, maxFrameBytes },
        currentScope: () => ({
          deviceId: root.identity.deviceId, serverEpoch: root.identity.serverEpoch,
          dataEpoch: root.identity.dataEpoch, configScope: root.identity.configScope,
          scopeEpoch: root.identity.scopeEpoch
        }),
        onClosed: () => {
          set.delete(gateway)
          if (set.size === 0 && live.get(controllerId) === set) live.delete(controllerId)
        }
      })
      set.add(gateway)
    }
    pki = await loadOrCreatePki(options.dataDir, advertise, now())
    const credentials = { caPem: pki.caPem, certificatePem: pki.certificatePem, privateKeyPem: pki.privateKeyPem }
    pairing = await listenPairingServer({
      host: options.host, port: options.pairingPort, credentials,
      handle: createPairingHandler({ deviceId: root.identity.deviceId, authority: pki.authority, now })
    })
    listener = await listenDeviceServer<{ controllerId: string }>({
      host: options.host, port: options.port, credentials,
      authenticate: ({ publicKeySha256 }) => {
        const controller = findControllerByFingerprint(publicKeySha256)
        return controller === null ? null : { controllerId: controller.controllerId }
      },
      accept: (transport, peer) => adopt(transport, peer.controllerId, capabilities.maxFrameBytes)
    })
  } catch (error) {
    await pairing?.close().catch(() => {})
    if (databaseOpen) closeDatabase()
    root.release()
    throw error
  }

  let stopped: Promise<void> | null = null
  let drained: Promise<void> | null = null
  return {
    identity: root.identity,
    address: () => listener.address(),
    pairingAddress: () => pairing!.address(),
    createInvitation: () => {
      if (drained !== null) throw new DeviceProtocolError('busy', 'Service is draining; pairing is closed')
      const { token, expiresAt } = createEnrollment(now())
      const endpoints = (port: number): string[] =>
        advertise.map((host) => `${host.includes(':') && !host.startsWith('[') ? `[${host}]` : host}:${port}`)
      const text = encodeInvitation({
        v: 1, deviceId: root.identity.deviceId, rootPublicKeySha256: pki.rootPublicKeySha256,
        rootCertificatePem: pki.caPem, token, expiresAt,
        pairing: endpoints(pairing!.address().port),
        device: endpoints(listener.address().port)
      })
      return { text, expiresAt }
    },
    connections: () => listener.connections(),
    revoke: (controllerId) => {
      const revoked = revokeController(controllerId, now())
      // 被撤销的控制端不能继续握着任何输入租约。
      leases.releaseAll(controllerId)
      // 其余子系统(上传等)的清理:一个监听器失败不能让撤销本身或其他清理被跳过。
      for (const listener of [...revokeListeners]) { try { listener(controllerId) } catch { /* 撤销已生效。 */ } }
      for (const gateway of [...(live.get(controllerId) ?? [])]) gateway.close()
      return revoked
    },
    drain: () => {
      drained ??= (async () => {
        // 先关新任务入口,再关配对端点:任一步失败不能让另一步被跳过。
        const failures: unknown[] = []
        try { options.onDrain?.() } catch (error) { failures.push(error) }
        try { await pairing!.close() } catch (error) { failures.push(error) }
        if (failures.length > 0) throw new AggregateError(failures, 'Device service could not drain cleanly')
      })()
      return drained
    },
    draining: () => drained !== null,
    attachRelay: (relayUrl, relayOptions) => {
      // 内层帧要给端到端的头与认证标签留出空间。
      const relayFrameBytes = Math.min(options.maxFrameBytes ?? DEVICE_FRAME_MAX_BYTES, DEVICE_FRAME_MAX_BYTES - E2E_OVERHEAD)
      const connection = connectDeviceToRelay(relayUrl, {
        deviceId: root.identity.deviceId, token: relayOptions.token,
        ...(relayOptions.caPem !== undefined && { caPem: relayOptions.caPem }),
        ...(relayOptions.onState !== undefined && { onState: relayOptions.onState }),
        onSession: (carrier) => {
          // 握手失败(未配对/已撤销/伪造)只会关闭这一条承载,不留下任何协议状态。
          acceptE2eServer(carrier, {
            caPem: pki.caPem, certificatePem: pki.certificatePem, privateKeyPem: pki.privateKeyPem,
            authenticate: (publicKeySha256) => {
              const controller = findControllerByFingerprint(publicKeySha256)
              return controller === null ? null : { controllerId: controller.controllerId }
            }
          }).then(({ transport, peer }) => adopt(transport, peer.controllerId, relayFrameBytes), () => {})
        }
      })
      relays.add(connection)
      return connection
    },
    stop: () => {
      stopped ??= (async () => {
        const failures: unknown[] = []
        // 每一步独立执行:前一步失败不能让数据库和锁泄漏。
        for (const step of [() => { for (const relay of relays) relay.close() }, () => pairing!.close(), () => listener.close(), () => options.onStop?.(), () => closeDatabase(), () => root.release()]) {
          try { await step() } catch (error) { failures.push(error) }
        }
        if (failures.length > 0) throw new AggregateError(failures, 'Device service did not stop cleanly')
      })()
      return stopped
    }
  }
}
