/**
 * 自部署中转服务。**盲转发**:只搬运端到端加密之后的字节,看不到业务内容,也无权限签发/验证设备身份。
 *
 * ## 它做什么
 * - 设备**出站**连到 `/v1/device`,用「注册凭据」证明自己是某个设备 id 的持有者(与设备配对凭据分离)。
 *   同一个设备 id 已在线时,第二个连接被**明确拒绝(409)**,不做含糊的接管 —— 抢占应由持有凭据的
 *   运维显式处理,否则凭据泄露就等于可以悄悄顶掉真设备。
 * - 控制端连到 `/v1/connect/<deviceId>`;中转为它建一个会话,通过设备那条出站连接转发。
 *   控制端不需要中转凭据 —— 设备在端到端握手里自己验证它,中转只负责限速保护。
 *
 * ## 它不做什么
 * - 不解析 payload、不持久化任何流量、不记录内容(日志只有设备 id 前缀与计数)。
 * - 不接受明文监听,除非调用方显式声明前面还有 TLS 终结(`behindTls`)。
 *
 * ## 资源保护
 * 每设备并发会话上限、每设备每分钟新会话上限、每来源 IP 并发上限、总设备数上限、
 * 单帧上限、发送缓冲上限(过大即断开该会话)、控制端会话空闲超时。
 */
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto'
import { createServer as createHttpServer, type IncomingMessage, type Server as HttpServer } from 'node:http'
import { createServer as createHttpsServer } from 'node:https'
import type { AddressInfo } from 'node:net'
import type { Duplex } from 'node:stream'
import { WebSocketServer, type RawData, type WebSocket } from 'ws'
import {
  RELAY_CLOSE, RELAY_CONNECT_PREFIX, RELAY_DATA, RELAY_DEVICE_ID_HEADER, RELAY_DEVICE_PATH, RELAY_OPEN, RELAY_PROTOCOL,
  decodeRelayFrame, encodeRelayFrame, RELAY_HEADER_BYTES
} from './frames'

const FRAME_MAX = 1024 * 1024 + 1024
const SEND_BUFFER_MAX = 8 * 1024 * 1024
const DEVICE_ID = /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/

export interface RelayOptions {
  host: string
  port: number
  /** TLS 证书与私钥(PEM)。生产必填。 */
  tls?: { certificatePem: string; privateKeyPem: string }
  /** 明确声明前面有 TLS 终结(反向代理)时才允许明文监听。 */
  behindTls?: boolean
  /** 校验设备注册凭据。返回 false = 拒绝。 */
  authenticateDevice(deviceId: string, token: string): boolean
  maxDevices?: number
  maxSessionsPerDevice?: number
  newSessionsPerMinutePerDevice?: number
  maxConnectionsPerIp?: number
  /** 控制端会话在没有任何帧的情况下保持多久,默认 15 分钟。 */
  sessionIdleMs?: number
  now?: () => number
}

export interface Relay {
  address(): { host: string; port: number }
  devices(): number
  sessions(): number
  close(): Promise<void>
}

/** 简单的内存注册表:token 只存 sha256,比较用常量时间。 */
export class RelayRegistry {
  private readonly hashes = new Map<string, Buffer>()
  register(deviceId: string, token: string): void {
    if (!DEVICE_ID.test(deviceId) || token.length < 32) throw new Error('Invalid relay registration')
    this.hashes.set(deviceId, createHash('sha256').update(token).digest())
  }
  revoke(deviceId: string): boolean { return this.hashes.delete(deviceId) }
  /** 从持久化的摘要恢复(文件里只有 sha256,没有明文 token)。 */
  registerHash(deviceId: string, hashHex: string): void {
    if (!DEVICE_ID.test(deviceId) || !/^[0-9a-f]{64}$/.test(hashHex)) throw new Error('Invalid relay registration')
    this.hashes.set(deviceId, Buffer.from(hashHex, 'hex'))
  }
  /** 供持久化:设备 id → sha256 hex。 */
  entries(): Record<string, string> {
    return Object.fromEntries([...this.hashes].map(([id, hash]) => [id, hash.toString('hex')]))
  }
  verify = (deviceId: string, token: string): boolean => {
    const expected = this.hashes.get(deviceId)
    const actual = createHash('sha256').update(token).digest()
    // 不存在的设备也做一次比较,不通过耗时泄露「这个设备 id 是否已注册」。
    return timingSafeEqual(expected ?? Buffer.alloc(32), actual) && expected !== undefined
  }
}

interface Session {
  id: string
  controller: WebSocket
  ip: string
  idleTimer: ReturnType<typeof setTimeout> | null
}
interface Device {
  id: string
  socket: WebSocket
  sessions: Map<string, Session>
  opened: number[]
}

export async function listenRelay(options: RelayOptions): Promise<Relay> {
  if (options.tls === undefined && options.behindTls !== true) {
    throw new Error('The relay refuses to listen in plaintext; provide TLS or set behindTls explicitly')
  }
  const maxDevices = options.maxDevices ?? 1024
  const maxSessions = options.maxSessionsPerDevice ?? 16
  const perMinute = options.newSessionsPerMinutePerDevice ?? 60
  const perIp = options.maxConnectionsPerIp ?? 32
  const idleMs = options.sessionIdleMs ?? 15 * 60_000
  const now = options.now ?? Date.now

  const devices = new Map<string, Device>()
  const connectionsByIp = new Map<string, number>()
  let sessionCount = 0

  const handler = (_request: IncomingMessage, response: import('node:http').ServerResponse): void => {
    response.writeHead(404, { connection: 'close' }).end()
  }
  const server: HttpServer = options.tls === undefined
    ? createHttpServer(handler)
    : createHttpsServer({ cert: options.tls.certificatePem, key: options.tls.privateKeyPem, minVersion: 'TLSv1.2' }, handler)
  server.on('tlsClientError', () => {})
  server.on('clientError', (_error, socket) => { socket.destroy() })
  const sockets = new WebSocketServer({
    noServer: true, perMessageDeflate: false, maxPayload: FRAME_MAX,
    handleProtocols: (protocols) => protocols.has(RELAY_PROTOCOL) ? RELAY_PROTOCOL : false
  })

  const refuse = (socket: Duplex, status: string): void => {
    try { socket.end(`HTTP/1.1 ${status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`) } catch { /* 已断开。 */ }
    socket.destroy()
  }
  const send = (socket: WebSocket, frame: Buffer): boolean => {
    if (socket.readyState !== socket.OPEN || socket.bufferedAmount + frame.length > SEND_BUFFER_MAX) return false
    socket.send(frame, { binary: true })
    return true
  }
  const endSession = (device: Device, session: Session, notifyDevice: boolean, closeController: boolean): void => {
    if (device.sessions.get(session.id) !== session) return
    device.sessions.delete(session.id)
    sessionCount--
    if (session.idleTimer !== null) clearTimeout(session.idleTimer)
    const left = (connectionsByIp.get(session.ip) ?? 1) - 1
    if (left <= 0) connectionsByIp.delete(session.ip)
    else connectionsByIp.set(session.ip, left)
    if (notifyDevice) send(device.socket, encodeRelayFrame(RELAY_CLOSE, session.id))
    if (closeController) session.controller.terminate()
  }
  const touch = (device: Device, session: Session): void => {
    if (session.idleTimer !== null) clearTimeout(session.idleTimer)
    session.idleTimer = setTimeout(() => endSession(device, session, true, true), idleMs)
  }

  const acceptDevice = (request: IncomingMessage, socket: Duplex, head: Buffer): void => {
    const deviceId = String(request.headers[RELAY_DEVICE_ID_HEADER] ?? '')
    const authorization = String(request.headers.authorization ?? '')
    const token = authorization.startsWith('Bearer ') ? authorization.slice(7) : ''
    if (!DEVICE_ID.test(deviceId) || token === '' || !options.authenticateDevice(deviceId, token)) { refuse(socket, '401 Unauthorized'); return }
    // 同一个设备 id 已经在线:明确拒绝,绝不悄悄顶掉旧连接。
    if (devices.has(deviceId)) { refuse(socket, '409 Conflict'); return }
    if (devices.size >= maxDevices) { refuse(socket, '503 Service Unavailable'); return }
    sockets.handleUpgrade(request, socket, head, (ws) => {
      const device: Device = { id: deviceId, socket: ws, sessions: new Map(), opened: [] }
      devices.set(deviceId, device)
      ws.on('message', (data: RawData, binary: boolean) => {
        const bytes = Array.isArray(data) ? Buffer.concat(data) : Buffer.from(data as ArrayBuffer)
        const frame = binary ? decodeRelayFrame(bytes) : null
        if (frame === null) { ws.terminate(); return }
        const session = device.sessions.get(frame.session)
        if (session === undefined) return // 会话已结束,迟到的帧直接丢弃
        if (frame.type === RELAY_CLOSE) { endSession(device, session, false, true); return }
        if (frame.type !== RELAY_DATA) { ws.terminate(); return }
        touch(device, session)
        if (!send(session.controller, frame.payload)) endSession(device, session, true, true)
      })
      ws.on('close', () => {
        if (devices.get(deviceId) === device) devices.delete(deviceId)
        for (const session of [...device.sessions.values()]) endSession(device, session, false, true)
      })
      ws.on('error', () => ws.terminate())
    })
  }

  const acceptController = (request: IncomingMessage, socket: Duplex, head: Buffer, deviceId: string): void => {
    const ip = request.socket.remoteAddress ?? 'unknown'
    const device = devices.get(deviceId)
    // 设备不在线与设备不存在对外同样是 404,不泄露「这个 id 是否注册过」。
    if (device === undefined) { refuse(socket, '404 Not Found'); return }
    const t = now()
    device.opened = device.opened.filter((time) => t - time < 60_000)
    if (device.sessions.size >= maxSessions || device.opened.length >= perMinute || (connectionsByIp.get(ip) ?? 0) >= perIp) {
      refuse(socket, '429 Too Many Requests')
      return
    }
    device.opened.push(t)
    connectionsByIp.set(ip, (connectionsByIp.get(ip) ?? 0) + 1)
    sockets.handleUpgrade(request, socket, head, (ws) => {
      const session: Session = { id: randomBytes(16).toString('hex'), controller: ws, ip, idleTimer: null }
      device.sessions.set(session.id, session)
      sessionCount++
      touch(device, session)
      if (!send(device.socket, encodeRelayFrame(RELAY_OPEN, session.id))) { endSession(device, session, false, true); return }
      ws.on('message', (data: RawData, binary: boolean) => {
        const bytes = Array.isArray(data) ? Buffer.concat(data) : Buffer.from(data as ArrayBuffer)
        if (!binary || bytes.length === 0 || bytes.length > FRAME_MAX - RELAY_HEADER_BYTES) { endSession(device, session, true, true); return }
        touch(device, session)
        if (!send(device.socket, encodeRelayFrame(RELAY_DATA, session.id, bytes))) endSession(device, session, true, true)
      })
      ws.on('close', () => endSession(device, session, true, false))
      ws.on('error', () => ws.terminate())
    })
  }

  server.on('upgrade', (request: IncomingMessage, socket: Duplex, head: Buffer) => {
    try {
      const path = request.url ?? ''
      if (path === RELAY_DEVICE_PATH) { acceptDevice(request, socket, head); return }
      if (path.startsWith(RELAY_CONNECT_PREFIX)) {
        const deviceId = path.slice(RELAY_CONNECT_PREFIX.length)
        if (DEVICE_ID.test(deviceId)) { acceptController(request, socket, head, deviceId); return }
      }
      refuse(socket, '404 Not Found')
    } catch { refuse(socket, '500 Internal Server Error') }
  })

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject)
    server.listen(options.port, options.host, () => { server.off('error', reject); resolve() })
  })
  server.on('error', () => {})

  return {
    address: () => { const a = server.address() as AddressInfo; return { host: a.address, port: a.port } },
    devices: () => devices.size,
    sessions: () => sessionCount,
    close: async () => {
      for (const client of sockets.clients) client.terminate()
      server.closeAllConnections()
      await new Promise<void>((resolve) => sockets.close(() => resolve()))
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  }
}
