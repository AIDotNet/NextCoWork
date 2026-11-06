import { createHash, X509Certificate } from 'node:crypto'
import WebSocket from 'ws'
import type { SecureTransport } from '../../controller-client/ports'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'
import { NodeWebSocketChannel } from './channel'

export const DEVICE_WEBSOCKET_PROTOCOL = 'ncw-device-v1'

export interface NodeDeviceCredentials {
  caPem: string
  /** 配对时确认的信任根 SPKI 摘要，不从本次网络响应里自行信任。 */
  trustedCaPublicKeySha256: string
  certificatePem: string
  privateKeyPem: string
}

export function certificatePublicKeySha256(certificate: X509Certificate): string {
  return createHash('sha256').update(certificate.publicKey.export({ type: 'spki', format: 'der' })).digest('hex')
}

/** 不开放 redirect/明文/关闭校验选项，避免客户端证书被送到未经验证的新地址。 */
export async function connectDirectDevice(url: string, credentials: NodeDeviceCredentials,
  options: { signal?: AbortSignal } = {}): Promise<SecureTransport> {
  if (options.signal?.aborted) throw new DeviceProtocolError('disconnected', 'Device connection was cancelled')
  const endpoint = new URL(url)
  if (endpoint.protocol !== 'wss:' || endpoint.username || endpoint.password || endpoint.hash || endpoint.search) {
    throw new DeviceProtocolError('invalid_request', 'Device endpoint must be a credential-free wss URL')
  }
  const authority = new X509Certificate(credentials.caPem)
  if (!authority.ca || certificatePublicKeySha256(authority) !== credentials.trustedCaPublicKeySha256) {
    throw new DeviceProtocolError('unauthorized', 'Device trust root does not match its pinned identity')
  }
  return new Promise<SecureTransport>((resolve, reject) => {
    const socket = new WebSocket(endpoint, DEVICE_WEBSOCKET_PROTOCOL, {
      // 使用解析后的单张根证书，忽略输入 PEM 里可能拼接的额外信任根。
      ca: authority.toString(), cert: credentials.certificatePem, key: credentials.privateKeyPem,
      rejectUnauthorized: true, minVersion: 'TLSv1.3', maxVersion: 'TLSv1.3',
      followRedirects: false, perMessageDeflate: false,
      handshakeTimeout: 10_000, maxPayload: DEVICE_FRAME_MAX_BYTES
    })
    let settled = false
    const fail = (): void => {
      if (settled) return
      settled = true
      options.signal?.removeEventListener('abort', fail)
      socket.removeListener('open', opened)
      socket.removeListener('close', fail)
      // error listener 保留到 socket 回收，terminate 可能随后再发一次 error。
      socket.terminate()
      reject(new DeviceProtocolError('disconnected', 'Secure device connection failed'))
    }
    const opened = (): void => {
      if (settled) return
      if (socket.protocol !== DEVICE_WEBSOCKET_PROTOCOL) { fail(); return }
      const channel = new NodeWebSocketChannel(socket)
      settled = true
      options.signal?.removeEventListener('abort', fail)
      socket.removeListener('error', fail)
      socket.removeListener('close', fail)
      resolve(channel)
    }
    socket.on('error', fail)
    socket.once('close', fail)
    socket.once('open', opened)
    options.signal?.addEventListener('abort', fail, { once: true })
    if (options.signal?.aborted) fail()
  })
}
