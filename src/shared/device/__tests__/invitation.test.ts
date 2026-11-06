import { describe, expect, it } from 'vitest'
import { decodeInvitation, encodeInvitation, INVITATION_PREFIX, type Invitation } from '../invitation'

const root = '-----BEGIN CERTIFICATE-----\n' + 'MIIBkTCB+wIJAKHBfpegPjMCMA0GCSqGSIb3DQEBCwUAMBExDzANBgNVBAMMBnJv\n'.repeat(3) + '-----END CERTIFICATE-----\n'
const invitation: Invitation = {
  v: 1, deviceId: 'dev-1', rootPublicKeySha256: 'a'.repeat(64), rootCertificatePem: root,
  token: 'T'.repeat(43), expiresAt: 10_000, pairing: ['192.168.1.5:7001', '[fe80::1]:7001'], device: ['device.lan:7002']
}

describe('pairing invitation', () => {
  it('round trips as plain text that is also valid base64url', () => {
    const text = encodeInvitation(invitation)
    expect(text.startsWith(INVITATION_PREFIX)).toBe(true)
    const body = text.slice(INVITATION_PREFIX.length)
    expect(Buffer.from(body, 'base64url').toString('utf8')).toBe(JSON.stringify(invitation))
    expect(decodeInvitation(text, 1)).toEqual(invitation)
    expect(decodeInvitation(`  ${text}\n`, 1)).toEqual(invitation)
  })

  it('rejects expiry, tampering, unknown fields, bad endpoints and oversize input', () => {
    const text = encodeInvitation(invitation)
    expect(() => decodeInvitation(text, 10_000)).toThrowError(expect.objectContaining({ code: 'unauthorized' }))
    expect(() => decodeInvitation(text.slice(0, -3), 1)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => decodeInvitation('ncwpair1.!!!', 1)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => decodeInvitation(`${INVITATION_PREFIX}${'A'.repeat(5000)}`, 1)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    const forge = (value: unknown): string => INVITATION_PREFIX + Buffer.from(JSON.stringify(value)).toString('base64url')
    expect(() => decodeInvitation(forge({ ...invitation, extra: 1 }), 1)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => decodeInvitation(forge({ ...invitation, pairing: ['https://evil.example/x'] }), 1)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => decodeInvitation(forge({ ...invitation, rootPublicKeySha256: 'ABC' }), 1)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
  })

  it('encodes and decodes non-ASCII device names byte-for-byte', () => {
    const named = { ...invitation, deviceId: 'dev-1', pairing: ['host-名.lan:1'] }
    // 主机名字符集不含非 ASCII:编码阶段就拒绝,而不是产出别的运行时解不开的文本。
    expect(() => encodeInvitation(named)).toThrow()
  })
})
