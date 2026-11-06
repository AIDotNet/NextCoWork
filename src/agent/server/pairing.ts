import { z } from 'zod'
import { completeEnrollment, revokeController } from '../../main/db/device-trust'
import { DeviceProtocolError } from '../../shared/device/errors'
import { deviceIdSchema } from '../../shared/device/identity'
import { issueControllerCertificate, parseControllerPublicKey, type DeviceAuthority } from './pki'

export const pairingRequestSchema = z.strictObject({
  v: z.literal(1),
  token: z.string().min(32).max(256),
  controllerId: deviceIdSchema,
  label: z.string().min(1).max(128),
  /** 控制端自己生成的 EC P-256 公钥(SPKI DER,base64)。私钥永远不出现在这里。 */
  publicKey: z.string().min(1).max(512)
})
export type PairingRequest = z.infer<typeof pairingRequestSchema>

export interface PairingResponse {
  v: 1
  deviceId: string
  certificatePem: string
  caPem: string
}

/** 配对端点的业务:校验邀请 → 登记公钥 → 签发客户端证书。 */
export function createPairingHandler(options: { deviceId: string; authority: DeviceAuthority; now?: () => number }) {
  const now = options.now ?? Date.now
  return async (body: unknown): Promise<PairingResponse> => {
    const parsed = pairingRequestSchema.safeParse(body)
    if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Invalid pairing request')
    const request = parsed.data
    let spki: Uint8Array
    try { spki = Uint8Array.from(atob(request.publicKey), (char) => char.charCodeAt(0)) }
    catch { throw new DeviceProtocolError('invalid_request', 'Invalid controller public key') }
    const key = parseControllerPublicKey(spki)
    completeEnrollment({
      token: request.token, controllerId: request.controllerId, fingerprintSha256: key.fingerprintSha256, label: request.label
    }, now())
    try {
      const certificatePem = await issueControllerCertificate(options.authority, request.controllerId, key.der, now())
      return { v: 1, deviceId: options.deviceId, certificatePem, caPem: options.authority.caCertificatePem }
    } catch (error) {
      // 已登记却没拿到证书的控制端不该留在信任记录里。
      revokeController(request.controllerId, now())
      throw error
    }
  }
}
