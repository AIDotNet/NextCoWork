/**
 * 经中转连接的两端。
 *
 * - 设备:`connectDeviceToRelay` —— 设备**出站**连到中转(不开任何入站端口),为每个控制端会话产出一条
 *   虚拟的 `SecureTransport`,交给调用方做端到端握手。断线按退避自动重连。
 * - 控制端:`connectViaRelay` —— 连到 `/v1/connect/<deviceId>`,得到一条原始 `SecureTransport`,
 *   调用方在其上做端到端握手(`connectE2eClient`)。
 *
 * 这两条传输本身**不提供任何机密性或身份**:它们只是承载。机密性与认证全在 `e2e.ts`。
 */
import { randomBytes } from 'node:crypto'
import WebSocket, { type RawData } from 'ws'
import type { Dispose, SecureTransport } from '../../controller-client/ports'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'
import {
  RELAY_CLOSE, RELAY_CONNECT_PREFIX, RELAY_DATA, RELAY_DEVICE_ID_HEADER, RELAY_DEVICE_PATH, RELAY_OPEN, RELAY_PROTOCOL,
  decodeRelayFrame, encodeRelayFrame
} from '../../relay/frames'
import { NodeWebSocketChannel } from './channel'

const SOCKET_OPTIONS = {
  perMessageDeflate: false, followRedirects: false, handshakeTimeout: 10_000, maxPayload: DEVICE_FRAME_MAX_BYTES + 1024
} as const

/** 中转的 URL 必须是不带凭据/查询/片段的 wss;测试可显式给出信任根。 */
function relayUrl(base: string, path: string): URL {
  const url = new URL(path, base)
  if (url.protocol !== 'wss:' || url.username || url.password || url.search || url.hash) {
    throw new DeviceProtocolError('invalid_request', 'Relay endpoint must be a credential-free wss URL')
  }
  return url
}

/** 控制端 → 中转。返回一条原始承载传输;没有设备在线时失败为 not_found。 */
export function connectViaRelay(relay: string, deviceId: string, options: { caPem?: string; signal?: AbortSignal } = {}): Promise<SecureTransport> {
  if (options.signal?.aborted) return Promise.reject(new DeviceProtocolError('disconnected', 'Relay connection was cancelled'))
  const url = relayUrl(relay, `${RELAY_CONNECT_PREFIX}${encodeURIComponent(deviceId)}`)
  return new Promise<SecureTransport>((resolve, reject) => {
    const socket = new WebSocket(url, RELAY_PROTOCOL, { ...SOCKET_OPTIONS, rejectUnauthorized: true, ...(options.caPem && { ca: options.caPem }) })
    let settled = false
    const fail = (error: DeviceProtocolError): void => {
      if (settled) return
      settled = true
      options.signal?.removeEventListener('abort', onAbort)
      socket.terminate()
      reject(error)
    }
    const onAbort = (): void => fail(new DeviceProtocolError('disconnected', 'Relay connection was cancelled'))
    socket.on('unexpected-response', (_request, response) => {
      fail(new DeviceProtocolError(response.statusCode === 404 ? 'not_found' : response.statusCode === 429 ? 'busy' : 'disconnected',
        'Relay refused the connection'))
    })
    socket.on('error', () => fail(new DeviceProtocolError('disconnected', 'Relay connection failed')))
    socket.once('close', () => fail(new DeviceProtocolError('disconnected', 'Relay connection closed')))
    socket.once('open', () => {
      if (settled) return
      if (socket.protocol !== RELAY_PROTOCOL) { fail(new DeviceProtocolError('disconnected', 'Relay protocol mismatch')); return }
      settled = true
      options.signal?.removeEventListener('abort', onAbort)
      socket.removeAllListeners('close')
      socket.removeAllListeners('error')
      resolve(new NodeWebSocketChannel(socket))
    })
    options.signal?.addEventListener('abort', onAbort, { once: true })
  })
}

/** 设备侧的一个控制端会话:通过那条出站连接复用。 */
class RelaySessionChannel implements SecureTransport {
  private ended = false
  private readonly frameListeners = new Set<(frame: Uint8Array) => void>()
  private readonly closeListeners = new Set<() => void>()
  private readonly early: Uint8Array[] = []

  constructor(private readonly id: string, private readonly write: (frame: Buffer) => Promise<void>, private readonly onEnd: () => void) {}

  send(frame: Uint8Array): Promise<void> {
    if (this.ended) return Promise.reject(new DeviceProtocolError('disconnected', 'Relay session closed'))
    if (frame.length === 0 || frame.length > DEVICE_FRAME_MAX_BYTES) return Promise.reject(new DeviceProtocolError('resource_limit', 'Frame too large'))
    return this.write(encodeRelayFrame(RELAY_DATA, this.id, frame))
  }
  onFrame(listener: (frame: Uint8Array) => void): Dispose {
    this.frameListeners.add(listener)
    while (this.early.length > 0 && !this.ended) listener(this.early.shift()!)
    return () => { this.frameListeners.delete(listener) }
  }
  onClose(listener: () => void): Dispose {
    if (this.ended) { listener(); return () => {} }
    this.closeListeners.add(listener)
    return () => { this.closeListeners.delete(listener) }
  }
  close(): void {
    if (this.ended) return
    void this.write(encodeRelayFrame(RELAY_CLOSE, this.id)).catch(() => {})
    this.finish()
  }
  /** 来自中转的数据。 */
  deliver(bytes: Uint8Array): void {
    if (this.ended) return
    if (this.frameListeners.size === 0) {
      if (this.early.length >= 64) { this.close(); return }
      this.early.push(bytes)
      return
    }
    for (const listener of [...this.frameListeners]) { try { listener(bytes) } catch { this.close(); return } }
  }
  /** 中转通知会话结束,或本地主动关闭。 */
  finish(): void {
    if (this.ended) return
    this.ended = true
    this.frameListeners.clear()
    this.early.length = 0
    const listeners = [...this.closeListeners]
    this.closeListeners.clear()
    this.onEnd()
    for (const listener of listeners) { try { listener() } catch { /* 其余订阅者仍需通知。 */ } }
  }
}

export interface DeviceRelayOptions {
  deviceId: string
  /** 中转注册凭据(与设备配对凭据无关)。 */
  token: string
  /** 中转的 TLS 信任根(自部署自签名时给出);默认用系统信任库。 */
  caPem?: string
  /** 每个控制端会话;调用方在其上做端到端握手并接入网关。 */
  onSession(transport: SecureTransport): void
  /** 连接状态变化,仅用于诊断(不含凭据)。 */
  onState?(state: 'connected' | 'disconnected' | 'rejected'): void
  maxSessions?: number
  minBackoffMs?: number
  maxBackoffMs?: number
}

export interface DeviceRelayConnection {
  connected(): boolean
  sessions(): number
  /** 停止重连并关闭所有会话。 */
  close(): void
}

/**
 * 设备出站连接,带指数退避重连。★ 被中转 401/409 明确拒绝(凭据错误或设备 id 已被占用)时
 * **停止重试**并上报 `rejected`:反复撞一个明确的拒绝既没用,也会被当成攻击。
 */
export function connectDeviceToRelay(relay: string, options: DeviceRelayOptions): DeviceRelayConnection {
  const url = relayUrl(relay, RELAY_DEVICE_PATH)
  const minBackoff = options.minBackoffMs ?? 500
  const maxBackoff = options.maxBackoffMs ?? 30_000
  const maxSessions = options.maxSessions ?? 32
  let socket: WebSocket | null = null
  let stopped = false
  let attempt = 0
  let timer: ReturnType<typeof setTimeout> | null = null
  const sessions = new Map<string, RelaySessionChannel>()

  const schedule = (): void => {
    if (stopped) return
    const delay = Math.min(maxBackoff, minBackoff * 2 ** Math.min(attempt, 10)) * (0.5 + Math.random() / 2)
    attempt++
    timer = setTimeout(open, delay)
  }

  const open = (): void => {
    if (stopped) return
    const ws = new WebSocket(url, RELAY_PROTOCOL, {
      ...SOCKET_OPTIONS, rejectUnauthorized: true, ...(options.caPem && { ca: options.caPem }),
      headers: { [RELAY_DEVICE_ID_HEADER]: options.deviceId, authorization: `Bearer ${options.token}` }
    })
    socket = ws
    const write = (frame: Buffer): Promise<void> => new Promise((resolve, reject) => {
      if (ws.readyState !== WebSocket.OPEN) { reject(new DeviceProtocolError('disconnected', 'Relay connection closed')); return }
      ws.send(frame, { binary: true }, (error) => { if (error) reject(error); else resolve() })
    })
    ws.on('unexpected-response', (_request, response) => {
      const status = response.statusCode ?? 0
      response.resume()
      if (status === 401 || status === 409) { stopped = true; options.onState?.('rejected') }
      ws.terminate()
    })
    ws.on('open', () => { attempt = 0; options.onState?.('connected') })
    ws.on('message', (data: RawData, binary: boolean) => {
      const bytes = Array.isArray(data) ? Buffer.concat(data) : Buffer.from(data as ArrayBuffer)
      const frame = binary ? decodeRelayFrame(bytes) : null
      if (frame === null) { ws.terminate(); return }
      if (frame.type === RELAY_OPEN) {
        if (sessions.has(frame.session) || sessions.size >= maxSessions) { void write(encodeRelayFrame(RELAY_CLOSE, frame.session)).catch(() => {}); return }
        const channel = new RelaySessionChannel(frame.session, write, () => { sessions.delete(frame.session) })
        sessions.set(frame.session, channel)
        try { options.onSession(channel) } catch { channel.close() }
        return
      }
      const session = sessions.get(frame.session)
      if (session === undefined) return
      if (frame.type === RELAY_DATA) session.deliver(new Uint8Array(frame.payload))
      else if (frame.type === RELAY_CLOSE) session.finish()
    })
    const lost = (): void => {
      if (socket !== ws) return
      socket = null
      for (const session of [...sessions.values()]) session.finish()
      if (!stopped) { options.onState?.('disconnected'); schedule() }
    }
    ws.on('close', lost)
    ws.on('error', () => ws.terminate())
  }
  open()

  return {
    connected: () => socket?.readyState === WebSocket.OPEN,
    sessions: () => sessions.size,
    close: () => {
      stopped = true
      if (timer !== null) clearTimeout(timer)
      for (const session of [...sessions.values()]) session.finish()
      socket?.terminate()
      socket = null
    }
  }
}

/** 生成一个足够长的随机注册凭据(运维创建注册时用)。 */
export function newRelayToken(): string {
  return randomBytes(32).toString('base64url')
}
