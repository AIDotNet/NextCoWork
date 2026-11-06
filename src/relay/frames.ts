/**
 * 设备 ⇄ 中转 之间的多路复用帧。一条设备出站连接承载多个控制端会话。
 *
 *   type(1) | sessionId(16) | payload
 *
 * - OPEN  (中转→设备)  有控制端接入,会话 id 由中转随机生成;无 payload。
 * - DATA  (双向)       不透明字节 —— 对中转而言就是端到端加密握手/密文,它既不解析也不改。
 * - CLOSE (双向)       会话结束,无 payload。
 *
 * ★ 中转只认这个外层头。业务帧、证书、身份全在 payload 的端到端信道里。
 */
export const RELAY_OPEN = 1
export const RELAY_DATA = 2
export const RELAY_CLOSE = 3
export const SESSION_ID_BYTES = 16
export const RELAY_HEADER_BYTES = 1 + SESSION_ID_BYTES

export interface RelayFrame {
  type: number
  /** 小写 hex,32 字符。 */
  session: string
  payload: Buffer
}

export function encodeRelayFrame(type: number, session: string, payload: Uint8Array = new Uint8Array(0)): Buffer {
  const id = Buffer.from(session, 'hex')
  if (id.length !== SESSION_ID_BYTES) throw new Error('Invalid relay session id')
  return Buffer.concat([Buffer.from([type]), id, payload])
}

/** 解析失败返回 null —— 调用方据此关闭连接,而不是猜测。 */
export function decodeRelayFrame(bytes: Buffer): RelayFrame | null {
  if (bytes.length < RELAY_HEADER_BYTES) return null
  const type = bytes[0]
  if (type !== RELAY_OPEN && type !== RELAY_DATA && type !== RELAY_CLOSE) return null
  return { type, session: bytes.subarray(1, RELAY_HEADER_BYTES).toString('hex'), payload: bytes.subarray(RELAY_HEADER_BYTES) }
}

/** 设备注册头。注册凭据只用于向中转证明「我是这个设备 id 的持有者」,与设备配对无关。 */
export const RELAY_DEVICE_ID_HEADER = 'x-ncw-device-id'
export const RELAY_PROTOCOL = 'ncw-relay-v1'
export const RELAY_DEVICE_PATH = '/v1/device'
export const RELAY_CONNECT_PREFIX = '/v1/connect/'
