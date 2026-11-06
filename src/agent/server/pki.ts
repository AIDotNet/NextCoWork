/**
 * 设备自有的小型 PKI:一个设备根 CA、一张服务端证书、按公钥签发的控制端证书。
 *
 * ★ **信任以公钥(SPKI)指纹为准,不是证书指纹。** 控制端证书在原信任根内续签时
 *   公钥不变,配对关系因此不丢;换了公钥就是另一个身份,必须重新配对。
 * ★ 根只由设备本机生成并签发;控制端只提交公钥,私钥从不离开控制端。
 * ★ 仅接受 EC P-256 公钥:这是 iOS/Android/桌面都原生支持的最小公共集合。
 *
 * CA 私钥以 0600 落在数据根的 `pki/` 下。它是这台设备最敏感的文件 —— 计划里的
 * `CredentialStore` 接入后应换成系统安全存储;这里不假装它已经是。
 */
import 'reflect-metadata'
import { createPublicKey, createHash, randomBytes, X509Certificate } from 'node:crypto'
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import {
  BasicConstraintsExtension, ExtendedKeyUsage, ExtendedKeyUsageExtension, KeyUsageFlags, KeyUsagesExtension,
  PublicKey, SubjectAlternativeNameExtension, X509CertificateGenerator
} from '@peculiar/x509'
import { DeviceProtocolError } from '../../shared/device/errors'

const ALGORITHM = { name: 'ECDSA', namedCurve: 'P-256', hash: 'SHA-256' } as const
const DAY = 86_400_000
const CA_SUBJECT = 'CN=NextCoWork device root'
export const CA_VALIDITY_MS = 3650 * DAY
export const SERVER_VALIDITY_MS = 365 * DAY
export const CLIENT_VALIDITY_MS = 365 * DAY
/** 服务端证书剩余不足这么久就在启动时续签(根不变)。 */
export const SERVER_RENEW_BEFORE_MS = 30 * DAY

export interface DeviceAuthority {
  caCertificatePem: string
  caPrivateKeyPem: string
}
export interface ServerIdentityCredentials {
  caPem: string
  certificatePem: string
  privateKeyPem: string
}

const pemOfKey = (der: ArrayBuffer): string =>
  `-----BEGIN PRIVATE KEY-----\n${Buffer.from(der).toString('base64').match(/.{1,64}/g)!.join('\n')}\n-----END PRIVATE KEY-----\n`

const serial = (): string => {
  // 正数、不超过 20 字节:首字节清最高位。
  const bytes = randomBytes(16)
  bytes[0] = bytes[0]! & 0x7f
  if (bytes[0] === 0) bytes[0] = 1
  return bytes.toString('hex')
}

async function importCaKey(pem: string): Promise<CryptoKey> {
  const der = Buffer.from(pem.replace(/-----[^-]+-----|\s/g, ''), 'base64')
  return crypto.subtle.importKey('pkcs8', der, ALGORITHM, false, ['sign'])
}

export async function createDeviceAuthority(now: number): Promise<DeviceAuthority> {
  const keys = await crypto.subtle.generateKey(ALGORITHM, true, ['sign', 'verify'])
  const certificate = await X509CertificateGenerator.createSelfSigned({
    serialNumber: serial(), name: CA_SUBJECT, keys, signingAlgorithm: ALGORITHM,
    notBefore: new Date(now - 60_000), notAfter: new Date(now + CA_VALIDITY_MS),
    extensions: [new BasicConstraintsExtension(true, 0, true),
      new KeyUsagesExtension(KeyUsageFlags.keyCertSign | KeyUsageFlags.cRLSign, true)]
  })
  return {
    caCertificatePem: certificate.toString('pem'),
    caPrivateKeyPem: pemOfKey(await crypto.subtle.exportKey('pkcs8', keys.privateKey))
  }
}

/** 公钥 SPKI 的 sha256(小写 hex)—— 配对与信任记录的身份。 */
export function publicKeyFingerprint(spkiDer: Uint8Array): string {
  return createHash('sha256').update(spkiDer).digest('hex')
}

/** 校验并规范化控制端提交的公钥:必须是 EC P-256 的 SPKI DER。 */
export function parseControllerPublicKey(spkiDer: Uint8Array): { der: Uint8Array; fingerprintSha256: string } {
  if (spkiDer.byteLength < 64 || spkiDer.byteLength > 256) {
    throw new DeviceProtocolError('invalid_request', 'Invalid controller public key')
  }
  try {
    const key = createPublicKey({ key: Buffer.from(spkiDer), format: 'der', type: 'spki' })
    if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
      throw new Error('unsupported key')
    }
    const der = new Uint8Array(key.export({ type: 'spki', format: 'der' }))
    return { der, fingerprintSha256: publicKeyFingerprint(der) }
  } catch {
    throw new DeviceProtocolError('invalid_request', 'Invalid controller public key')
  }
}

/** 给控制端公钥签发客户端证书。subject 只带 controllerId,身份判断仍以公钥指纹为准。 */
export async function issueControllerCertificate(
  authority: DeviceAuthority, controllerId: string, spkiDer: Uint8Array, now: number
): Promise<string> {
  const publicKey = new PublicKey(Uint8Array.from(spkiDer).buffer)
  const certificate = await X509CertificateGenerator.create({
    serialNumber: serial(), subject: `CN=${controllerId.replace(/[^A-Za-z0-9_.:-]/g, '_')}`,
    issuer: CA_SUBJECT, publicKey,
    signingKey: await importCaKey(authority.caPrivateKeyPem), signingAlgorithm: ALGORITHM,
    notBefore: new Date(now - 60_000), notAfter: new Date(now + CLIENT_VALIDITY_MS),
    extensions: [new BasicConstraintsExtension(false, undefined, true),
      new KeyUsagesExtension(KeyUsageFlags.digitalSignature, true),
      new ExtendedKeyUsageExtension([ExtendedKeyUsage.clientAuth], true)]
  })
  return certificate.toString('pem')
}

export async function issueServerCertificate(
  authority: DeviceAuthority, hosts: readonly string[], now: number
): Promise<{ certificatePem: string; privateKeyPem: string }> {
  const keys = await crypto.subtle.generateKey(ALGORITHM, true, ['sign', 'verify'])
  const names = hosts.map((value) => /^[0-9.]+$|:/.test(value)
    ? { type: 'ip' as const, value } : { type: 'dns' as const, value })
  const certificate = await X509CertificateGenerator.create({
    serialNumber: serial(), subject: 'CN=NextCoWork device', issuer: CA_SUBJECT,
    publicKey: keys.publicKey, signingKey: await importCaKey(authority.caPrivateKeyPem), signingAlgorithm: ALGORITHM,
    notBefore: new Date(now - 60_000), notAfter: new Date(now + SERVER_VALIDITY_MS),
    extensions: [new BasicConstraintsExtension(false, undefined, true),
      new KeyUsagesExtension(KeyUsageFlags.digitalSignature, true),
      new ExtendedKeyUsageExtension([ExtendedKeyUsage.serverAuth], true),
      ...(names.length > 0 ? [new SubjectAlternativeNameExtension(names)] : [])]
  })
  return {
    certificatePem: certificate.toString('pem'),
    privateKeyPem: pemOfKey(await crypto.subtle.exportKey('pkcs8', keys.privateKey))
  }
}

function writePrivate(path: string, text: string): void {
  const temp = `${path}.${randomBytes(6).toString('hex')}.tmp`
  writeFileSync(temp, text, { mode: 0o600 })
  chmodSync(temp, 0o600)
  renameSync(temp, path)
}

/**
 * 读取或首次创建数据根下的 PKI。`hosts` 变化(换了对外地址)或服务端证书临近到期时,
 * 在**同一个根**下重签服务端证书;根本身从不自动重建 —— 根变了所有控制端都得重新配对。
 */
export async function loadOrCreatePki(dataDir: string, hosts: readonly string[], now: number): Promise<ServerIdentityCredentials & {
  authority: DeviceAuthority; rootPublicKeySha256: string
}> {
  const dir = join(dataDir, 'pki')
  mkdirSync(dir, { recursive: true, mode: 0o700 })
  const ca = join(dir, 'ca.pem'), caKey = join(dir, 'ca.key'), cert = join(dir, 'server.pem'), key = join(dir, 'server.key')
  const hasCa = existsSync(ca), hasCaKey = existsSync(caKey)
  if (hasCa !== hasCaKey) throw new DeviceProtocolError('internal', 'Device PKI is incomplete; refusing to regenerate the root')
  let authority: DeviceAuthority
  if (hasCa) {
    authority = { caCertificatePem: readFileSync(ca, 'utf8'), caPrivateKeyPem: readFileSync(caKey, 'utf8') }
  } else {
    authority = await createDeviceAuthority(now)
    writePrivate(caKey, authority.caPrivateKeyPem)
    writeFileSync(ca, authority.caCertificatePem)
  }
  const wanted = [...hosts].sort()
  let current: { certificatePem: string; privateKeyPem: string } | null = null
  if (existsSync(cert) && existsSync(key)) {
    const certificatePem = readFileSync(cert, 'utf8')
    const parsed = new X509Certificate(certificatePem)
    const have = (parsed.subjectAltName ?? '').split(',').map((item) => item.trim().replace(/^(DNS|IP Address):/, '')).filter(Boolean).sort()
    const fresh = new Date(parsed.validTo).getTime() - now > SERVER_RENEW_BEFORE_MS
    if (fresh && parsed.verify(new X509Certificate(authority.caCertificatePem).publicKey)
      && JSON.stringify(have) === JSON.stringify(wanted)) {
      current = { certificatePem, privateKeyPem: readFileSync(key, 'utf8') }
    }
  }
  if (current === null) {
    current = await issueServerCertificate(authority, wanted, now)
    writePrivate(key, current.privateKeyPem)
    writeFileSync(cert, current.certificatePem)
  }
  return {
    authority, caPem: authority.caCertificatePem, ...current,
    rootPublicKeySha256: publicKeyFingerprint(new X509Certificate(authority.caCertificatePem).publicKey
      .export({ type: 'spki', format: 'der' }))
  }
}
