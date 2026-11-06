import type { IncomingMessage, ServerResponse } from 'node:http'
import { createServer, type Server } from 'node:https'
import type { AddressInfo } from 'node:net'
import { DeviceProtocolError } from '../../shared/device/errors'

export const PAIRING_PATH = '/pair'
const BODY_MAX_BYTES = 4096

export interface PairingServerOptions {
  host: string
  port: number
  /** 只出示服务端证书;这个端点不要求也不接受客户端证书。 */
  credentials: { certificatePem: string; privateKeyPem: string }
  /** 解析后的 JSON 请求体。抛 DeviceProtocolError 映射为 HTTP 状态;其他错误一律 500 且不带细节。 */
  handle(body: unknown): Promise<unknown>
  /** 失败预算:同一来源每个窗口内最多这么多次失败,默认 10;全局默认 30。 */
  maxFailuresPerAddress?: number
  maxFailuresGlobal?: number
  windowMs?: number
  now?: () => number
}

export interface PairingServer {
  address(): { host: string; port: number }
  close(): Promise<void>
}

const STATUS: Partial<Record<string, number>> = {
  invalid_request: 400, unauthorized: 401, conflict: 409, resource_limit: 429
}

/** 配对端点:单一路由、小请求体、失败限速。它只能签发身份,不能访问任何业务。 */
export async function listenPairingServer(options: PairingServerOptions): Promise<PairingServer> {
  const perAddress = options.maxFailuresPerAddress ?? 10
  const global = options.maxFailuresGlobal ?? 30
  const windowMs = options.windowMs ?? 60_000
  const now = options.now ?? Date.now
  const failures = new Map<string, number[]>()
  let globalFailures: number[] = []

  const recent = (list: number[], at: number): number[] => list.filter((time) => at - time < windowMs)
  const limited = (address: string, at: number): boolean => {
    globalFailures = recent(globalFailures, at)
    const mine = recent(failures.get(address) ?? [], at)
    if (mine.length === 0) failures.delete(address)
    else failures.set(address, mine)
    return mine.length >= perAddress || globalFailures.length >= global
  }
  const fail = (address: string, at: number): void => {
    globalFailures.push(at)
    failures.set(address, [...recent(failures.get(address) ?? [], at), at])
    // 地址表不无限增长:超过上限时丢掉最旧的来源。
    if (failures.size > 4096) failures.delete(failures.keys().next().value as string)
  }

  const reply = (response: ServerResponse, status: number, body: unknown): void => {
    const text = JSON.stringify(body)
    response.writeHead(status, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(text), connection: 'close' })
    response.end(text)
  }

  const readBody = (request: IncomingMessage): Promise<unknown> => new Promise((resolve, reject) => {
    const chunks: Buffer[] = []
    let size = 0
    request.on('data', (chunk: Buffer) => {
      size += chunk.length
      if (size > BODY_MAX_BYTES) { reject(new DeviceProtocolError('invalid_request', 'Request is too large')); request.destroy(); return }
      chunks.push(chunk)
    })
    request.on('end', () => {
      try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8'))) }
      catch { reject(new DeviceProtocolError('invalid_request', 'Invalid request body')) }
    })
    request.on('error', () => reject(new DeviceProtocolError('invalid_request', 'Invalid request body')))
  })

  const server: Server = createServer({
    key: options.credentials.privateKeyPem, cert: options.credentials.certificatePem,
    minVersion: 'TLSv1.3', maxVersion: 'TLSv1.3', requestCert: false
  }, (request, response) => {
    const address = request.socket.remoteAddress ?? 'unknown'
    const at = now()
    if (request.method !== 'POST' || request.url !== PAIRING_PATH) { reply(response, 404, { error: 'not_found' }); return }
    if (limited(address, at)) { reply(response, 429, { error: 'resource_limit' }); return }
    void (async () => {
      try {
        const result = await options.handle(await readBody(request))
        reply(response, 200, result)
      } catch (error) {
        fail(address, now())
        if (error instanceof DeviceProtocolError) {
          // 不回显内部消息:失败原因对外只有错误码,不区分邀请不存在/过期/已用。
          reply(response, STATUS[error.code] ?? 400, { error: error.code })
        } else reply(response, 500, { error: 'internal' })
      }
    })()
  })
  server.headersTimeout = 5000
  server.requestTimeout = 10_000
  server.maxConnections = 32
  server.on('tlsClientError', () => {})
  server.on('clientError', (_error, socket) => { socket.destroy() })
  await new Promise<void>((resolve, rejectListen) => {
    server.once('error', rejectListen)
    server.listen(options.port, options.host, () => { server.off('error', rejectListen); resolve() })
  })
  server.on('error', () => {})
  return {
    address: () => { const a = server.address() as AddressInfo; return { host: a.address, port: a.port } },
    close: async () => {
      server.closeAllConnections()
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  }
}
