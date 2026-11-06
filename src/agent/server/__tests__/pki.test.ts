import { X509Certificate, generateKeyPairSync } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync, unlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { issueControllerCertificate, loadOrCreatePki, parseControllerPublicKey, SERVER_VALIDITY_MS } from '../pki'
import { listenPairingServer } from '../../../transport/node/pairing'
import { request } from 'node:https'

let dir = ''
beforeEach(() => { dir = mkdtempSync(join(tmpdir(), 'ncw-pki-')) })
afterEach(() => { rmSync(dir, { recursive: true, force: true }) })
const spki = (curve = 'prime256v1'): Uint8Array =>
  new Uint8Array(generateKeyPairSync('ec', { namedCurve: curve }).publicKey.export({ type: 'spki', format: 'der' }))

describe('device PKI', () => {
  it('keeps the root across restarts and reissues only the server certificate when hosts change', async () => {
    const now = Date.now()
    const first = await loadOrCreatePki(dir, ['127.0.0.1'], now)
    const again = await loadOrCreatePki(dir, ['127.0.0.1'], now)
    expect(again.certificatePem).toBe(first.certificatePem)
    const moved = await loadOrCreatePki(dir, ['127.0.0.1', 'device.lan'], now)
    expect(moved.caPem).toBe(first.caPem)
    expect(moved.certificatePem).not.toBe(first.certificatePem)
    expect(new X509Certificate(moved.certificatePem).verify(new X509Certificate(first.caPem).publicKey)).toBe(true)
    // 临近到期 → 同一个根下续签。
    const late = await loadOrCreatePki(dir, ['127.0.0.1', 'device.lan'], now + SERVER_VALIDITY_MS - 1000)
    expect(late.certificatePem).not.toBe(moved.certificatePem)
    expect(late.rootPublicKeySha256).toBe(first.rootPublicKeySha256)
  })

  it('refuses to regenerate a root when half of it is missing', async () => {
    await loadOrCreatePki(dir, ['127.0.0.1'], Date.now())
    unlinkSync(join(dir, 'pki', 'ca.key'))
    await expect(loadOrCreatePki(dir, ['127.0.0.1'], Date.now())).rejects.toMatchObject({ code: 'internal' })
    expect(readFileSync(join(dir, 'pki', 'ca.pem'), 'utf8')).toContain('CERTIFICATE')
  })

  it('issues a client-auth leaf for exactly the submitted key, and only accepts P-256', async () => {
    const pki = await loadOrCreatePki(dir, ['127.0.0.1'], Date.now())
    const key = parseControllerPublicKey(spki())
    const certificate = new X509Certificate(await issueControllerCertificate(pki.authority, 'phone', key.der, Date.now()))
    expect(Buffer.from(certificate.publicKey.export({ type: 'spki', format: 'der' })).equals(Buffer.from(key.der))).toBe(true)
    expect(certificate.ca).toBe(false)
    expect(certificate.keyUsage).toContain('1.3.6.1.5.5.7.3.2')
    expect(() => parseControllerPublicKey(spki('secp384r1'))).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => parseControllerPublicKey(new Uint8Array(10))).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
  })
})

describe('pairing endpoint', () => {
  it('rate limits repeated failures per address without invoking the handler again', async () => {
    const pki = await loadOrCreatePki(dir, ['127.0.0.1'], Date.now())
    let calls = 0
    const server = await listenPairingServer({
      host: '127.0.0.1', port: 0, credentials: pki, maxFailuresPerAddress: 3,
      handle: async () => { calls++; throw Object.assign(new Error('x'), {}) }
    })
    const post = (path = '/pair', body = '{}') => new Promise<number>((resolve, reject) => {
      const req = request({ host: '127.0.0.1', port: server.address().port, path, method: 'POST', ca: pki.caPem,
        headers: { 'content-length': Buffer.byteLength(body) } }, (res) => { res.resume(); res.on('end', () => resolve(res.statusCode ?? 0)) })
      req.on('error', reject)
      req.end(body)
    })
    try {
      expect(await post('/other')).toBe(404)
      expect([await post(), await post(), await post()]).toEqual([500, 500, 500])
      expect(await post()).toBe(429)
      expect(calls).toBe(3)
    } finally { await server.close() }
  })
})
