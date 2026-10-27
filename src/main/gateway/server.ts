/**
 * 需求：把桌面 Agent 作为可选 HTTP 服务开放给本机/远程客户端。监听边界与
 * Bearer 鉴权都在这里，业务只通过 GatewayBackend 访问，不依赖 Electron。
 * 默认仅本机；远程监听必须由设置显式开启，不通过 HTTP 自行开启。
 */
import { timingSafeEqual } from 'node:crypto'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { GatewayBackend } from './backend'
import { dispatch, json } from './routes'
import { serveDocs } from './docs'
import { HttpError } from './validation'

const MAX_BODY = 1024 * 1024

/** 需求：原先拒绝所有浏览器 Origin 以阻止外站操作；现在仅允许网关自己的文档同源调用。
 * HTTPS 反代需保留 Host，不信任客户端可伪造的 Forwarded/X-Forwarded-*。
 */
function validateBrowser(request: IncomingMessage, host: string, port: number): void {
  if (host === '127.0.0.1' && request.headers.host !== `127.0.0.1:${port}` && request.headers.host !== `localhost:${port}`) {
    throw new HttpError(403, 'forbidden_host', 'Use the loopback listening address')
  }
  const origin = request.headers.origin
  if (origin !== undefined && origin !== `http://${request.headers.host}` && origin !== `https://${request.headers.host}`) {
    throw new HttpError(403, 'forbidden_origin', 'Only same-origin browser requests are allowed')
  }
}

/** 需求：业务接口始终先鉴权再读取 body；公开文档不能扩大业务访问权限。 */
function authorize(request: IncomingMessage, key: string): void {
  const expected = Buffer.from(`Bearer ${key}`)
  const actual = Buffer.from(request.headers.authorization ?? '')
  if (actual.length !== expected.length || !timingSafeEqual(actual, expected)) {
    throw new HttpError(401, 'unauthorized', 'A valid Bearer API key is required')
  }
}

async function readBody(request: IncomingMessage): Promise<unknown> {
  if (request.method !== 'POST') return undefined
  if (Number(request.headers['content-length'] ?? 0) > MAX_BODY) throw new HttpError(413, 'body_too_large', 'Request exceeds 1 MiB')
  let size = 0
  const chunks: Buffer[] = []
  for await (const chunk of request) {
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk as string)
    size += bytes.length
    if (size > MAX_BODY) throw new HttpError(413, 'body_too_large', 'Request exceeds 1 MiB')
    chunks.push(bytes)
  }
  if (size === 0) return undefined
  if (request.headers['content-type']?.split(';')[0]?.trim().toLowerCase() !== 'application/json') {
    throw new HttpError(415, 'unsupported_media_type', 'Use Content-Type: application/json')
  }
  try { return JSON.parse(Buffer.concat(chunks).toString('utf8')) as unknown }
  catch { throw new HttpError(400, 'invalid_json', 'Request body is not valid JSON') }
}

export class GatewayServer {
  private server: Server | null = null
  private listeningPort: number | null = null

  constructor(private readonly backend: GatewayBackend, private readonly key: string, readonly host: '127.0.0.1' | '0.0.0.0', private readonly available: () => boolean = () => true) {
    if (key.length < 32) throw new Error('Gateway API key must contain at least 32 characters')
  }

  get port(): number | null { return this.listeningPort }

  /** 需求：端口冲突时按现有契约动态选端口，界面必须显示真实地址。 */
  async listen(preferredPort: number): Promise<void> {
    if (this.server !== null) throw new Error('Gateway already started')
    const server = createServer((request, response) => { void this.handle(request, response) })
    this.server = server
    server.requestTimeout = 30_000
    server.headersTimeout = 15_000
    const bind = (port: number): Promise<void> => new Promise((resolve, reject) => {
      const fail = (error: Error): void => { server.off('listening', ready); reject(error) }
      const ready = (): void => { server.off('error', fail); resolve() }
      server.once('error', fail)
      server.once('listening', ready)
      server.listen(port, this.host)
    })
    try { await bind(preferredPort) }
    catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EADDRINUSE') throw error
      await bind(0)
    }
    const address = server.address()
    if (address === null || typeof address === 'string') throw new Error('Gateway has no TCP address')
    this.listeningPort = address.port
  }

  /** 需求：禁用/退出必须同时释放 SSE 长连接和端口，不能让它们拖住应用退出。 */
  close(): Promise<void> {
    const server = this.server
    this.server = null
    this.listeningPort = null
    if (server === null) return Promise.resolve()
    return new Promise((resolve) => { server.close(() => resolve()); server.closeAllConnections() })
  }

  private async handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
    try {
      if (this.listeningPort === null || !this.available()) throw new HttpError(503, 'unavailable', 'Gateway is stopping or changing account')
      validateBrowser(request, this.host, this.listeningPort)
      if (request.url === undefined || !request.url.startsWith('/') || request.url.startsWith('//')) {
        throw new HttpError(400, 'invalid_request', 'Use a relative API path')
      }
      const url = new URL(request.url, 'http://127.0.0.1')
      if (serveDocs(request.method, url.pathname, response)) return
      authorize(request, this.key)
      const body = await readBody(request)
      // 需求：读 body 的 await 期间可能禁用了网关；退出后不能再启动一个 run。
      if (this.listeningPort === null || !this.available()) throw new HttpError(503, 'unavailable', 'Gateway is stopping or changing account')
      dispatch({ backend: this.backend, request, response, url, body })
    } catch (error) {
      if (response.headersSent) { response.destroy(); return }
      const failure = error instanceof HttpError ? error : new HttpError(500, 'internal_error', 'Agent API request failed')
      if (failure.status === 401) response.setHeader('WWW-Authenticate', 'Bearer')
      // 未读完的坏请求不复用连接，否则剩余 body 会拖住 keep-alive 资源。
      response.setHeader('Connection', 'close')
      json(response, failure.status, { error: { code: failure.code, message: failure.message } })
    }
  }
}
