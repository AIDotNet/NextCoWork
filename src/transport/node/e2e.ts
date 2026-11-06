/**
 * 端到端加密信道:在**不可信的承载**(中转服务)之上,让控制端与设备互相认证并加密全部业务帧。
 *
 * 中转只能看到连接元数据与密文大小;看不到、也改不了任何一帧业务数据。
 *
 * ## 握手(3 条消息,1 个往返半)
 *
 *   1. C→S  ClientHello  : 客户端临时公钥(P-256) + 随机数 + 客户端证书
 *   2. S→C  ServerHello  : 服务端临时公钥 + 随机数 + 服务端证书 + 服务端对转录的签名
 *   3. C→S  ClientFinish : 客户端对转录(含服务端签名)的签名
 *
 * - 客户端用**配对时钉住的设备根**验证服务端证书链、EKU(serverAuth)与有效期,再验签。
 *   换了一个中转/冒充设备拿不到设备私钥,签不出这个签名。
 * - 服务端用同一个根验证客户端证书(EKU clientAuth、有效期、验签),再**用公钥指纹查信任记录**
 *   (`authenticate`)—— 身份只来自这里,与 hello 报文里的任何字段无关。已撤销 = 握手失败。
 * - 会话密钥 = HKDF-SHA256(ECDH 共享密钥, salt=转录摘要),两个方向各一把 AES-256-GCM 密钥。
 *   临时密钥每次连接新生成 → 前向保密;长期私钥只用于签名。
 *
 * ## 数据帧
 *
 *   0x10 | 8 字节递增计数 | 密文+16 字节标签;AAD = 前 9 字节。
 *   接收方要求计数**严格连续**:中转丢帧、重放、乱序都会导致解密失败并关闭连接,而不是静默继续。
 *
 * ★ 内层帧上限 = 外层承载上限 - `E2E_OVERHEAD`。走中转时网关的 `maxFrameBytes` 要相应调小。
 */
import { createCipheriv, createDecipheriv, createECDH, createHash, createPrivateKey, hkdfSync, randomBytes, sign, verify, X509Certificate } from 'node:crypto'
import type { Dispose, SecureTransport } from '../../controller-client/ports'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'

export const E2E_OVERHEAD = 1 + 8 + 16
const CLIENT_HELLO = 1
const SERVER_HELLO = 2
const CLIENT_FINISH = 3
const DATA = 0x10
const EKU_SERVER = '1.3.6.1.5.5.7.3.1'
const EKU_CLIENT = '1.3.6.1.5.5.7.3.2'
const LABEL = 'ncw-e2e-v1'
const EARLY_MAX_FRAMES = 64

export interface E2eClientOptions {
  /** 配对时钉住的设备根证书(PEM)。 */
  caPem: string
  certificatePem: string
  privateKeyPem: string
  timeoutMs?: number
  now?: () => number
}
export interface E2eServerOptions<Peer> {
  caPem: string
  certificatePem: string
  privateKeyPem: string
  /** 用控制端证书公钥的 SPKI sha256 查信任记录;返回 null = 未配对/已撤销。 */
  authenticate(publicKeySha256: string): Peer | null
  timeoutMs?: number
  now?: () => number
}

const fail = (message = 'Secure channel handshake failed'): never => { throw new DeviceProtocolError('unauthorized', message) }
const u16 = (value: number): Buffer => { const b = Buffer.alloc(2); b.writeUInt16BE(value); return b }
const sha256 = (...parts: Buffer[]): Buffer => createHash('sha256').update(Buffer.concat(parts)).digest()

/** 读取 `type | eph(65) | nonce(32) | certLen(2) | cert` 公共头;返回各字段与下一个偏移。 */
function readHello(frame: Buffer, type: number): { eph: Buffer; nonce: Buffer; cert: Buffer; end: number } {
  if (frame.length < 1 + 65 + 32 + 2 || frame[0] !== type) return fail()
  const eph = frame.subarray(1, 66)
  const nonce = frame.subarray(66, 98)
  const certLength = frame.readUInt16BE(98)
  const end = 100 + certLength
  if (certLength === 0 || certLength > 4096 || frame.length < end) return fail()
  return { eph, nonce, cert: frame.subarray(100, end), end }
}

function validateCertificate(der: Buffer, caPem: string, usage: string, now: number): X509Certificate {
  let certificate: X509Certificate
  let ca: X509Certificate
  try { certificate = new X509Certificate(der); ca = new X509Certificate(caPem) } catch { return fail() }
  if (!ca.ca || !certificate.verify(ca.publicKey) || !certificate.checkIssued(ca)) return fail()
  if (certificate.ca || !(certificate.keyUsage ?? []).includes(usage)) return fail()
  if (now < certificate.validFromDate.getTime() || now > certificate.validToDate.getTime()) return fail()
  return certificate
}

const spkiSha256 = (certificate: X509Certificate): string =>
  createHash('sha256').update(certificate.publicKey.export({ type: 'spki', format: 'der' })).digest('hex')

function deriveKeys(shared: Buffer, salt: Buffer): { clientToServer: Buffer; serverToClient: Buffer } {
  const okm = Buffer.from(hkdfSync('sha256', shared, salt, LABEL, 64))
  return { clientToServer: okm.subarray(0, 32), serverToClient: okm.subarray(32, 64) }
}

/** 握手完成之后的加密信道。 */
class E2eChannel implements SecureTransport {
  private sendCounter = 0n
  private receiveCounter = 0n
  private ended = false
  private readonly listeners = new Set<(frame: Uint8Array) => void>()
  private readonly closeListeners = new Set<() => void>()
  private readonly early: Uint8Array[] = []
  private readonly stop: Dispose[] = []

  constructor(private readonly under: SecureTransport, private readonly sendKey: Buffer, private readonly receiveKey: Buffer,
    pending: Buffer[]) {
    this.stop.push(under.onClose(() => this.finish()))
    this.stop.push(under.onFrame((frame) => this.receive(Buffer.from(frame))))
    for (const frame of pending) this.receive(frame)
  }

  async send(frame: Uint8Array): Promise<void> {
    if (this.ended) throw new DeviceProtocolError('disconnected', 'Secure channel closed')
    if (frame.length === 0 || frame.length > DEVICE_FRAME_MAX_BYTES - E2E_OVERHEAD) {
      throw new DeviceProtocolError('resource_limit', 'Frame exceeds the end-to-end channel limit')
    }
    const counter = Buffer.alloc(8)
    counter.writeBigUInt64BE(this.sendCounter++)
    const header = Buffer.concat([Buffer.from([DATA]), counter])
    const cipher = createCipheriv('aes-256-gcm', this.sendKey, Buffer.concat([Buffer.alloc(4), counter]))
    cipher.setAAD(header)
    const body = Buffer.concat([cipher.update(frame), cipher.final(), cipher.getAuthTag()])
    await this.under.send(Buffer.concat([header, body]))
  }

  onFrame(listener: (frame: Uint8Array) => void): Dispose {
    this.listeners.add(listener)
    while (this.early.length > 0 && !this.ended) listener(this.early.shift()!)
    return () => { this.listeners.delete(listener) }
  }

  onClose(listener: () => void): Dispose {
    if (this.ended) { listener(); return () => {} }
    this.closeListeners.add(listener)
    return () => { this.closeListeners.delete(listener) }
  }

  close(): void {
    this.finish()
    this.under.close()
  }

  private receive(frame: Buffer): void {
    if (this.ended) return
    if (frame.length < 1 + 8 + 16 || frame[0] !== DATA) { this.close(); return }
    const counter = frame.readBigUInt64BE(1)
    // 严格连续:丢帧/重放/乱序一律视为被篡改。
    if (counter !== this.receiveCounter) { this.close(); return }
    let plain: Buffer
    try {
      const header = frame.subarray(0, 9)
      const decipher = createDecipheriv('aes-256-gcm', this.receiveKey, Buffer.concat([Buffer.alloc(4), frame.subarray(1, 9)]))
      decipher.setAAD(header)
      decipher.setAuthTag(frame.subarray(frame.length - 16))
      plain = Buffer.concat([decipher.update(frame.subarray(9, frame.length - 16)), decipher.final()])
    } catch { this.close(); return }
    this.receiveCounter++
    const bytes = new Uint8Array(plain)
    if (this.listeners.size === 0) {
      if (this.early.length >= EARLY_MAX_FRAMES) { this.close(); return }
      this.early.push(bytes)
      return
    }
    for (const listener of [...this.listeners]) {
      try { listener(bytes) } catch { this.close(); return }
    }
  }

  private finish(): void {
    if (this.ended) return
    this.ended = true
    for (const dispose of this.stop.splice(0)) { try { dispose() } catch { /* 已释放。 */ } }
    this.listeners.clear()
    this.early.length = 0
    const listeners = [...this.closeListeners]
    this.closeListeners.clear()
    for (const listener of listeners) { try { listener() } catch { /* 其余订阅者仍需通知。 */ } }
  }
}

/** 等待下一帧(握手期专用),带超时与关闭处理;握手结束后多收到的帧交还给信道。 */
class HandshakeReader {
  private readonly frames: Buffer[] = []
  private waiter: { resolve(frame: Buffer): void; reject(error: Error): void } | null = null
  private closed = false
  private readonly stop: Dispose[] = []

  constructor(under: SecureTransport, private readonly timeoutMs: number) {
    this.stop.push(under.onFrame((frame) => {
      const buffer = Buffer.from(frame)
      if (this.waiter) { const waiter = this.waiter; this.waiter = null; waiter.resolve(buffer) } else this.frames.push(buffer)
    }))
    this.stop.push(under.onClose(() => {
      this.closed = true
      if (this.waiter) { const waiter = this.waiter; this.waiter = null; waiter.reject(new DeviceProtocolError('disconnected', 'Connection closed during handshake')) }
    }))
  }

  next(): Promise<Buffer> {
    const queued = this.frames.shift()
    if (queued) return Promise.resolve(queued)
    if (this.closed) return Promise.reject(new DeviceProtocolError('disconnected', 'Connection closed during handshake'))
    return new Promise<Buffer>((resolve, reject) => {
      const timer = setTimeout(() => { this.waiter = null; reject(new DeviceProtocolError('timeout', 'Secure channel handshake timed out')) }, this.timeoutMs)
      this.waiter = {
        resolve: (frame) => { clearTimeout(timer); resolve(frame) },
        reject: (error) => { clearTimeout(timer); reject(error) }
      }
    })
  }

  /** 交出监听并带走握手期间已收到、尚未处理的帧。 */
  release(): Buffer[] {
    for (const dispose of this.stop.splice(0)) { try { dispose() } catch { /* 已释放。 */ } }
    return this.frames.splice(0)
  }
}

async function guarded<T>(under: SecureTransport, reader: HandshakeReader, work: () => Promise<T>): Promise<T> {
  try {
    return await work()
  } catch (error) {
    reader.release()
    under.close()
    throw error instanceof DeviceProtocolError ? error : new DeviceProtocolError('unauthorized', 'Secure channel handshake failed')
  }
}

/** 控制端:对设备发起握手。成功返回加密信道;任何失败都会关闭底层连接。 */
export async function connectE2eClient(under: SecureTransport, options: E2eClientOptions): Promise<SecureTransport> {
  const now = options.now ?? Date.now
  const reader = new HandshakeReader(under, options.timeoutMs ?? 10_000)
  return guarded(under, reader, async () => {
    const certificate = new X509Certificate(options.certificatePem)
    const ecdh = createECDH('prime256v1')
    ecdh.generateKeys()
    const hello = Buffer.concat([Buffer.from([CLIENT_HELLO]), ecdh.getPublicKey(), randomBytes(32),
      u16(certificate.raw.length), certificate.raw])
    // 协议版本由 LABEL(`ncw-e2e-v1`)绑定进转录摘要与密钥派生,版本不一致的两端握手必然失败。
    await under.send(hello)

    const reply = await reader.next()
    const server = readHello(reply, SERVER_HELLO)
    const signatureLength = reply[server.end]
    if (signatureLength === undefined || reply.length !== server.end + 1 + signatureLength) return fail()
    const signature = reply.subarray(server.end + 1)
    const serverBody = reply.subarray(0, server.end)
    const serverCertificate = validateCertificate(server.cert, options.caPem, EKU_SERVER, now())
    const transcript = sha256(Buffer.from(`${LABEL} server`), hello, serverBody)
    if (!verify('sha256', transcript, serverCertificate.publicKey, signature)) return fail()

    const finishDigest = sha256(Buffer.from(`${LABEL} client`), hello, reply)
    const finishSignature = sign('sha256', finishDigest, createPrivateKey(options.privateKeyPem))
    await under.send(Buffer.concat([Buffer.from([CLIENT_FINISH]), Buffer.from([finishSignature.length]), finishSignature]))

    let shared: Buffer
    try { shared = ecdh.computeSecret(server.eph) } catch { return fail() }
    const keys = deriveKeys(shared, sha256(hello, serverBody))
    return new E2eChannel(under, keys.clientToServer, keys.serverToClient, reader.release())
  })
}

/** 设备:接受一条控制端握手。返回加密信道与信任记录里查到的对端。 */
export async function acceptE2eServer<Peer>(under: SecureTransport, options: E2eServerOptions<Peer>):
Promise<{ transport: SecureTransport; peer: Peer }> {
  const now = options.now ?? Date.now
  const reader = new HandshakeReader(under, options.timeoutMs ?? 10_000)
  return guarded(under, reader, async () => {
    const hello = await reader.next()
    const client = readHello(hello, CLIENT_HELLO)
    if (hello.length !== client.end) return fail()
    const clientCertificate = validateCertificate(client.cert, options.caPem, EKU_CLIENT, now())
    // 信任记录:撤销/未配对在这里就失败,不会产生任何协议状态。
    const peer = options.authenticate(spkiSha256(clientCertificate))
    if (peer === null) return fail()

    const certificate = new X509Certificate(options.certificatePem)
    const ecdh = createECDH('prime256v1')
    ecdh.generateKeys()
    const serverBody = Buffer.concat([Buffer.from([SERVER_HELLO]), ecdh.getPublicKey(), randomBytes(32),
      u16(certificate.raw.length), certificate.raw])
    const signature = sign('sha256', sha256(Buffer.from(`${LABEL} server`), hello, serverBody), createPrivateKey(options.privateKeyPem))
    const reply = Buffer.concat([serverBody, Buffer.from([signature.length]), signature])
    await under.send(reply)

    const finish = await reader.next()
    if (finish[0] !== CLIENT_FINISH || finish[1] === undefined || finish.length !== 2 + finish[1]) return fail()
    const finishDigest = sha256(Buffer.from(`${LABEL} client`), hello, reply)
    if (!verify('sha256', finishDigest, clientCertificate.publicKey, finish.subarray(2))) return fail()

    let shared: Buffer
    try { shared = ecdh.computeSecret(client.eph) } catch { return fail() }
    const keys = deriveKeys(shared, sha256(hello, serverBody))
    return { transport: new E2eChannel(under, keys.serverToClient, keys.clientToServer, reader.release()), peer }
  })
}
