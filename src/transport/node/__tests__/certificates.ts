import 'reflect-metadata'
import { BasicConstraintsExtension, ExtendedKeyUsage, ExtendedKeyUsageExtension, KeyUsageFlags,
  KeyUsagesExtension, SubjectAlternativeNameExtension, X509CertificateGenerator } from '@peculiar/x509'

/** 测试证书仅在内存中生成，不写用户 keychain、不安装系统信任。 */
export async function testCertificates() {
  const algorithm = { name: 'ECDSA', namedCurve: 'P-256', hash: 'SHA-256' }
  const keys = await crypto.subtle.generateKey(algorithm, true, ['sign', 'verify'])
  const validity = { notBefore: new Date(Date.now() - 60_000), notAfter: new Date(Date.now() + 3_600_000) }
  const ca = await X509CertificateGenerator.createSelfSigned({
    ...validity, name: 'CN=NextCoWork test root', keys, signingAlgorithm: algorithm,
    extensions: [new BasicConstraintsExtension(true, 0, true),
      new KeyUsagesExtension(KeyUsageFlags.keyCertSign | KeyUsageFlags.cRLSign, true)]
  })
  const leaf = async (server: boolean) => {
    const pair = await crypto.subtle.generateKey(algorithm, true, ['sign', 'verify'])
    const certificate = await X509CertificateGenerator.create({
      ...validity, subject: server ? 'CN=localhost' : 'CN=test-controller', issuer: ca.subject,
      publicKey: pair.publicKey, signingKey: keys.privateKey, signingAlgorithm: algorithm,
      extensions: [new BasicConstraintsExtension(false, undefined, true),
        new KeyUsagesExtension(KeyUsageFlags.digitalSignature, true),
        new ExtendedKeyUsageExtension([server ? ExtendedKeyUsage.serverAuth : ExtendedKeyUsage.clientAuth], true),
        ...(server ? [new SubjectAlternativeNameExtension([{ type: 'dns', value: 'localhost' }, { type: 'ip', value: '127.0.0.1' }])] : [])]
    })
    const raw = Buffer.from(await crypto.subtle.exportKey('pkcs8', pair.privateKey)).toString('base64')
    return { certificatePem: certificate.toString('pem'),
      privateKeyPem: `-----BEGIN PRIVATE KEY-----\n${raw.match(/.{1,64}/g)!.join('\n')}\n-----END PRIVATE KEY-----\n` }
  }
  return { caPem: ca.toString('pem'), server: await leaf(true), client: await leaf(false) }
}
