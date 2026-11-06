import { z } from 'zod'
import { DeviceProtocolError } from './errors'
import { deviceIdSchema } from './identity'

/**
 * 配对邀请:控制端扫码/粘贴得到的全部信息。
 *
 * ★ 邀请只带「去哪连 + 这台设备的信任根指纹 + 一次性 token」,**不携带任何私钥**,
 *   也不会自动执行任何操作 —— 控制端必须先把 `rootPublicKeySha256` 展示给用户确认,
 *   再提交自己的公钥。token 不是加密密钥,泄露的窗口 = 5 分钟且只能用一次。
 * ★ 序列化形态是 `ncwpair1.` + base64url(JSON),纯文本,可原样做成二维码。
 */
export const INVITATION_PREFIX = 'ncwpair1.'
const INVITATION_MAX_CHARS = 4096

const hostPort = z.string().min(3).max(260).regex(/^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+):[0-9]{1,5}$/)

export const invitationSchema = z.strictObject({
  v: z.literal(1),
  deviceId: deviceIdSchema,
  /** 设备根 CA 公钥(SPKI)sha256,小写 hex。 */
  rootPublicKeySha256: z.string().regex(/^[0-9a-f]{64}$/),
  /**
   * 设备根 CA 证书(公开信息)。配对连接只信任它,不依赖系统信任库;
   * 控制端必须核对它的公钥指纹等于 `rootPublicKeySha256`。
   */
  rootCertificatePem: z.string().min(100).max(1500).regex(/^-----BEGIN CERTIFICATE-----[A-Za-z0-9+/=\r\n]+-----END CERTIFICATE-----\r?\n?$/),
  token: z.string().min(32).max(256).regex(/^[A-Za-z0-9_-]+$/),
  expiresAt: z.number().int().nonnegative(),
  /** 配对端点与直连端点(host:port)。 */
  pairing: z.array(hostPort).min(1).max(8),
  device: z.array(hostPort).min(1).max(8)
})
export type Invitation = z.infer<typeof invitationSchema>

// 纯 JS 实现,不依赖 Buffer/atob/TextEncoder:这个文件要能被任何 JS 运行时
// (浏览器、React Native、Node)原样复用,而 controller-client 的类型环境不含宿主 API。
const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'

function utf8Bytes(text: string): number[] {
  const out: number[] = []
  for (const char of text) {
    const code = char.codePointAt(0)!
    if (code < 0x80) out.push(code)
    else if (code < 0x800) out.push(0xc0 | (code >> 6), 0x80 | (code & 63))
    else if (code < 0x10000) out.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 63), 0x80 | (code & 63))
    else out.push(0xf0 | (code >> 18), 0x80 | ((code >> 12) & 63), 0x80 | ((code >> 6) & 63), 0x80 | (code & 63))
  }
  return out
}

function utf8Text(bytes: number[]): string {
  let text = ''
  for (let i = 0; i < bytes.length;) {
    const first = bytes[i]!
    const extra = first < 0x80 ? 0 : first >= 0xc0 && first < 0xe0 ? 1 : first >= 0xe0 && first < 0xf0 ? 2 : first >= 0xf0 && first < 0xf8 ? 3 : -1
    if (extra < 0) throw new Error('bad utf8')
    let code = extra === 0 ? first : first & (0x3f >> extra)
    for (let k = 1; k <= extra; k++) {
      const next = bytes[i + k]
      if (next === undefined || (next & 0xc0) !== 0x80) throw new Error('bad utf8')
      code = (code << 6) | (next & 63)
    }
    // 拒绝过长编码、代理区和越界码点。
    if (code > 0x10ffff || (code >= 0xd800 && code < 0xe000) || code < [0, 0x80, 0x800, 0x10000][extra]!) throw new Error('bad utf8')
    text += String.fromCodePoint(code)
    i += extra + 1
  }
  return text
}

function toBase64Url(text: string): string {
  const bytes = utf8Bytes(text)
  let out = ''
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | ((bytes[i + 1] ?? 0) << 8) | (bytes[i + 2] ?? 0)
    out += B64[(n >> 18) & 63]! + B64[(n >> 12) & 63]!
    if (i + 1 < bytes.length) out += B64[(n >> 6) & 63]!
    if (i + 2 < bytes.length) out += B64[n & 63]!
  }
  return out
}

function fromBase64Url(text: string): string {
  if (!/^[A-Za-z0-9_-]*$/.test(text) || text.length % 4 === 1) throw new Error('not base64url')
  const bytes: number[] = []
  for (let i = 0; i < text.length; i += 4) {
    const chunk = text.slice(i, i + 4)
    let n = 0
    for (let k = 0; k < 4; k++) n = (n << 6) | (k < chunk.length ? B64.indexOf(chunk[k]!) : 0)
    bytes.push((n >> 16) & 255)
    if (chunk.length > 2) bytes.push((n >> 8) & 255)
    if (chunk.length > 3) bytes.push(n & 255)
  }
  return utf8Text(bytes)
}

export function encodeInvitation(invitation: Invitation): string {
  const parsed = invitationSchema.parse(invitation)
  return INVITATION_PREFIX + toBase64Url(JSON.stringify(parsed))
}

export function decodeInvitation(text: string, now: number): Invitation {
  const bad = (): never => { throw new DeviceProtocolError('invalid_request', 'Invalid pairing invitation') }
  const value = text.trim()
  if (value.length > INVITATION_MAX_CHARS || !value.startsWith(INVITATION_PREFIX)) return bad()
  let json: unknown
  try { json = JSON.parse(fromBase64Url(value.slice(INVITATION_PREFIX.length))) } catch { return bad() }
  const parsed = invitationSchema.safeParse(json)
  if (!parsed.success) return bad()
  if (parsed.data.expiresAt <= now) throw new DeviceProtocolError('unauthorized', 'Pairing invitation has expired')
  return parsed.data
}
