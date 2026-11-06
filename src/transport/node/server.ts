import { createHash, X509Certificate } from 'node:crypto'
import { createServer, type Server } from 'node:https'
import type { IncomingMessage } from 'node:http'
import type { AddressInfo } from 'node:net'
import type { Duplex } from 'node:stream'
import type { TLSSocket } from 'node:tls'
import { WebSocketServer } from 'ws'
import type { SecureTransport } from '../../controller-client/ports'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'
import { NodeWebSocketChannel } from './channel'
import { DEVICE_WEBSOCKET_PROTOCOL } from './direct'

export const DEVICE_WEBSOCKET_PATH = '/device'

export interface DeviceServerCredentials {
  /** 只用于验证控制端客户端证书的信任根；服务端不会接受链外证书。 */
  caPem: string
  certificatePem: string
  privateKeyPem: string
}

/** TLS 层已验证链之后得到的对端证书身份；信任记录以公钥指纹为键，证书续签不改变身份。 */
export interface DeviceTlsPeer {
  /** 叶子证书公钥 SPKI DER 的 sha256（小写 hex）。 */
  publicKeySha256: string
}

export interface DeviceServerOptions<Peer> {
  host: string
  port: number
  credentials: DeviceServerCredentials
  /**
   * 查服务端信任记录。返回 null（或抛错）= 未配对/已撤销，连接在升级前被拒绝，
   * 不会产生任何协议状态。身份只来自这里，绝不来自 hello 报文。
   */
  authenticate(peer: DeviceTlsPeer): Peer | null
  /** 对每条已认证的连接交出传输；调用方负责创建网关并在其 close 时释放。 */
  accept(transport: SecureTransport, peer: Peer): void
  /** 总连接上限（含尚未完成升级的 TLS 连接），默认 64。 */
  maxConnections?: number
  /** 单个控制端证书的并发连接上限，默认 8。 */
  maxConnectionsPerPeer?: number
}

export interface DeviceServer {
  address(): { host: string; port: number }
  connections(): number
  /** 停止监听并终止所有连接；Agent 任务不依赖这些连接，不会被停止。 */
  close(): Promise<void>
}

/** 直连 WSS 服务端：TLS1.3 + 必须的客户端证书 + 信任记录查询 + 连接预算。 */
export async function listenDeviceServer<Peer>(options: DeviceServerOptions<Peer>): Promise<DeviceServer> {
  const maxConnections = options.maxConnections ?? 64
  const maxPerPeer = options.maxConnectionsPerPeer ?? 8
  if (!Number.isSafeInteger(maxConnections) || maxConnections < 1 || maxConnections > 4096
    || !Number.isSafeInteger(maxPerPeer) || maxPerPeer < 1 || maxPerPeer > maxConnections) {
    throw new DeviceProtocolError('invalid_request', 'Invalid device server limits')
  }
  const server: Server = createServer({
    key: options.credentials.privateKeyPem, cert: options.credentials.certificatePem, ca: options.credentials.caPem,
    requestCert: true, rejectUnauthorized: true, minVersion: 'TLSv1.3', maxVersion: 'TLSv1.3'
  }, (_request, response) => {
    // 只有 WebSocket 升级；其余 HTTP 请求不暴露任何内容。
    response.writeHead(404, { connection: 'close' }).end()
  })
  server.maxConnections = maxConnections
  // 握手失败是常态（无证书、错证书），不能变成 uncaught。
  server.on('tlsClientError', () => {})
  server.on('clientError', (_error, socket) => { socket.destroy() })

  const sockets = new WebSocketServer({
    noServer: true, perMessageDeflate: false, maxPayload: DEVICE_FRAME_MAX_BYTES,
    handleProtocols: (protocols) => protocols.has(DEVICE_WEBSOCKET_PROTOCOL) ? DEVICE_WEBSOCKET_PROTOCOL : false
  })
  const perPeer = new Map<string, number>()
  let active = 0
  let stopping = false

  const reject = (socket: Duplex, status: string): void => {
    try { socket.end(`HTTP/1.1 ${status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`) } catch { /* 已断开。 */ }
    socket.destroy()
  }

  server.on('upgrade', (request: IncomingMessage, socket: Duplex, head: Buffer) => {
    const tls = request.socket as TLSSocket
    if (stopping || !tls.authorized || request.url !== DEVICE_WEBSOCKET_PATH) { reject(socket, '403 Forbidden'); return }
    const certificate = tls.getPeerCertificate(false)
    if (!certificate?.raw) { reject(socket, '403 Forbidden'); return }
    let publicKeySha256: string
    try {
      publicKeySha256 = createHash('sha256')
        .update(new X509Certificate(certificate.raw).publicKey.export({ type: 'spki', format: 'der' })).digest('hex')
    } catch { reject(socket, '403 Forbidden'); return }
    let peer: Peer | null
    try { peer = options.authenticate({ publicKeySha256 }) } catch { peer = null }
    if (peer === null) { reject(socket, '403 Forbidden'); return }
    if (active >= maxConnections || (perPeer.get(publicKeySha256) ?? 0) >= maxPerPeer) {
      reject(socket, '503 Service Unavailable')
      return
    }
    // 先占名额再升级：升级是异步的，不占会被并发升级冲破上限。
    active++
    perPeer.set(publicKeySha256, (perPeer.get(publicKeySha256) ?? 0) + 1)
    let released = false
    const release = (): void => {
      if (released) return
      released = true
      active--
      const left = (perPeer.get(publicKeySha256) ?? 1) - 1
      if (left <= 0) perPeer.delete(publicKeySha256)
      else perPeer.set(publicKeySha256, left)
    }
    socket.once('close', release)
    try {
      sockets.handleUpgrade(request, socket, head, (ws) => {
        try {
          options.accept(new NodeWebSocketChannel(ws), peer as Peer)
        } catch {
          ws.terminate()
        }
      })
    } catch {
      release()
      socket.destroy()
    }
  })

  await new Promise<void>((resolve, rejectListen) => {
    server.once('error', rejectListen)
    server.listen(options.port, options.host, () => { server.off('error', rejectListen); resolve() })
  })
  // 监听后的运行期 error 不能终止进程。
  server.on('error', () => {})

  return {
    address: () => {
      const address = server.address() as AddressInfo
      return { host: address.address, port: address.port }
    },
    connections: () => active,
    close: async () => {
      stopping = true
      for (const client of sockets.clients) client.terminate()
      server.closeAllConnections()
      await new Promise<void>((resolve) => sockets.close(() => resolve()))
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  }
}
