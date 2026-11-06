import { generateKeyPairSync, X509Certificate } from 'node:crypto'
import { request } from 'node:https'
import { DeviceProtocolError } from '../../shared/device/errors'
import { decodeInvitation, type Invitation } from '../../shared/device/invitation'
import { certificatePublicKeySha256 } from './direct'
import { PAIRING_PATH } from './pairing'

export interface PairedCredentials {
  deviceId: string
  /** 控制端私钥(PKCS8 PEM)。调用方必须存进安全存储,不得写入 renderer/URL。 */
  privateKeyPem: string
  certificatePem: string
  caPem: string
  trustedCaPublicKeySha256: string
  /** 邀请里的直连端点(host:port)。 */
  device: string[]
}

const CODES: Record<string, ConstructorParameters<typeof DeviceProtocolError>[0]> = {
  invalid_request: 'invalid_request', unauthorized: 'unauthorized', conflict: 'conflict', resource_limit: 'resource_limit'
}

function splitHostPort(value: string): [string, number] {
  const at = value.lastIndexOf(':')
  return [value.slice(0, at).replace(/^\[|\]$/g, ''), Number(value.slice(at + 1))]
}

/**
 * 控制端侧配对(桌面/CLI 共用):本地生成密钥,只提交公钥。
 *
 * - `confirmDevice` 在发出任何网络请求**之前**被调用 —— 用户必须看到并确认设备身份。
 * - 配对连接只信任邀请里的根证书(并核对它的公钥指纹),不用系统信任库,也不关闭校验。
 * - 返回的证书必须由该根签发、且公钥就是我们刚生成的那把,否则丢弃。
 */
export async function pairWithDevice(
  inviteText: string,
  options: {
    controllerId: string
    label: string
    now?: number
    confirmDevice(invitation: Invitation): boolean | Promise<boolean>
  }
): Promise<PairedCredentials> {
  const invitation = decodeInvitation(inviteText, options.now ?? Date.now())
  const root = new X509Certificate(invitation.rootCertificatePem)
  if (!root.ca || certificatePublicKeySha256(root) !== invitation.rootPublicKeySha256) {
    throw new DeviceProtocolError('unauthorized', 'Invitation root does not match its fingerprint')
  }
  if (!(await options.confirmDevice(invitation))) {
    throw new DeviceProtocolError('unauthorized', 'Pairing was not confirmed')
  }
  const keys = generateKeyPairSync('ec', { namedCurve: 'prime256v1' })
  const publicKey = keys.publicKey.export({ type: 'spki', format: 'der' })
  const body = JSON.stringify({ v: 1, token: invitation.token, controllerId: options.controllerId,
    label: options.label, publicKey: publicKey.toString('base64') })

  const [host, port] = splitHostPort(invitation.pairing[0]!)
  const reply = await new Promise<{ status: number; text: string }>((resolve, reject) => {
    const req = request({
      host, port, path: PAIRING_PATH, method: 'POST', minVersion: 'TLSv1.3', maxVersion: 'TLSv1.3',
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) },
      ca: root.toString(), rejectUnauthorized: true, timeout: 10_000, agent: false
    }, (res) => {
      const chunks: Buffer[] = []
      let size = 0
      res.on('data', (chunk: Buffer) => { size += chunk.length; if (size > 16_384) req.destroy(); else chunks.push(chunk) })
      res.on('end', () => resolve({ status: res.statusCode ?? 0, text: Buffer.concat(chunks).toString('utf8') }))
      res.on('error', () => reject(new DeviceProtocolError('disconnected', 'Pairing connection failed')))
    })
    req.on('error', () => reject(new DeviceProtocolError('disconnected', 'Pairing connection failed')))
    req.on('timeout', () => req.destroy())
    req.end(body)
  })

  let parsed: { error?: unknown; deviceId?: unknown; certificatePem?: unknown; caPem?: unknown } = {}
  try { parsed = JSON.parse(reply.text) as typeof parsed } catch { /* 下面按失败处理。 */ }
  if (reply.status !== 200) {
    throw new DeviceProtocolError(typeof parsed.error === 'string' ? CODES[parsed.error] ?? 'internal' : 'internal', 'Pairing was refused')
  }
  if (typeof parsed.deviceId !== 'string' || parsed.deviceId !== invitation.deviceId
    || typeof parsed.certificatePem !== 'string' || typeof parsed.caPem !== 'string') {
    throw new DeviceProtocolError('unauthorized', 'Pairing response does not match the invitation')
  }
  const certificate = new X509Certificate(parsed.certificatePem)
  if (!certificate.verify(root.publicKey) || !certificate.publicKey.export({ type: 'spki', format: 'der' }).equals(publicKey)
    || new X509Certificate(parsed.caPem).fingerprint256 !== root.fingerprint256) {
    throw new DeviceProtocolError('unauthorized', 'Issued certificate is not valid for this device')
  }
  return {
    deviceId: invitation.deviceId, privateKeyPem: keys.privateKey.export({ type: 'pkcs8', format: 'pem' }).toString(),
    certificatePem: parsed.certificatePem, caPem: root.toString(), trustedCaPublicKeySha256: invitation.rootPublicKeySha256,
    device: invitation.device
  }
}
