import { X509Certificate } from 'node:crypto'
import { beforeAll, describe, expect, it } from 'vitest'
import type { Dispose, SecureTransport } from '../../../controller-client/ports'
import { acceptE2eServer, connectE2eClient, E2E_OVERHEAD } from '../e2e'
import { certificatePublicKeySha256 } from '../direct'
import { testCertificates } from './certificates'

/** 内存里的一对传输,模拟不可信的承载:可以观察、篡改、丢弃、重放每一帧。 */
class Wire implements SecureTransport {
  other!: Wire
  frames = new Set<(frame: Uint8Array) => void>()
  closes = new Set<() => void>()
  ended = false
  /** 经过本端 send 的所有帧(中转能看到的全部内容)。 */
  seen: Buffer[] = []
  /** 返回要实际投递的帧;null = 丢弃;数组 = 投递多帧(重放)。 */
  tap: ((frame: Buffer, index: number) => Buffer | Buffer[] | null) | null = null
  private index = 0
  send(frame: Uint8Array): Promise<void> {
    if (this.ended) return Promise.reject(new Error('closed'))
    const original = Buffer.from(frame)
    this.seen.push(original)
    const out = this.tap ? this.tap(original, this.index++) : original
    for (const piece of out === null ? [] : Array.isArray(out) ? out : [out]) {
      queueMicrotask(() => { if (!this.other.ended) for (const listener of [...this.other.frames]) listener(new Uint8Array(piece)) })
    }
    return Promise.resolve()
  }
  onFrame(listener: (frame: Uint8Array) => void): Dispose { this.frames.add(listener); return () => { this.frames.delete(listener) } }
  onClose(listener: () => void): Dispose {
    if (this.ended) { listener(); return () => {} }
    this.closes.add(listener); return () => { this.closes.delete(listener) }
  }
  close(): void {
    if (this.ended) return
    this.ended = true
    for (const listener of [...this.closes]) listener()
    this.closes.clear()
    queueMicrotask(() => { if (!this.other.ended) this.other.close() })
  }
}
function pair(): { client: Wire; server: Wire } {
  const client = new Wire(), server = new Wire()
  client.other = server; server.other = client
  return { client, server }
}

let pki: Awaited<ReturnType<typeof testCertificates>>
let strangers: Awaited<ReturnType<typeof testCertificates>>
beforeAll(async () => { pki = await testCertificates(); strangers = await testCertificates() })

const clientKey = (): string => certificatePublicKeySha256(new X509Certificate(pki.client.certificatePem))
const server = (under: SecureTransport, trusted: string[] = [clientKey()], overrides = {}) => acceptE2eServer(under, {
  caPem: pki.caPem, certificatePem: pki.server.certificatePem, privateKeyPem: pki.server.privateKeyPem, timeoutMs: 500,
  authenticate: (key) => trusted.includes(key) ? { key } : null, ...overrides })
const client = (under: SecureTransport, overrides = {}) => connectE2eClient(under, {
  caPem: pki.caPem, certificatePem: pki.client.certificatePem, privateKeyPem: pki.client.privateKeyPem, timeoutMs: 500, ...overrides })
const collect = (transport: SecureTransport): Buffer[] => {
  const received: Buffer[] = []
  transport.onFrame((frame) => received.push(Buffer.from(frame)))
  return received
}
const settle = () => new Promise((resolve) => setTimeout(resolve, 20))

describe('end-to-end channel over an untrusted carrier', () => {
  it('authenticates both sides, exposes the trusted peer, and carries frames both ways without leaking plaintext', async () => {
    const { client: c, server: s } = pair()
    const [clientChannel, accepted] = await Promise.all([client(c), server(s)])
    expect(accepted.peer).toEqual({ key: clientKey() })
    const atServer = collect(accepted.transport)
    const atClient = collect(clientChannel)
    await clientChannel.send(new TextEncoder().encode('secret business request'))
    await accepted.transport.send(new TextEncoder().encode('secret business reply'))
    await settle()
    expect(atServer.map(String)).toEqual(['secret business request'])
    expect(atClient.map(String)).toEqual(['secret business reply'])
    // 承载上看到的全部字节里没有任何明文。
    const onWire = Buffer.concat([...c.seen, ...s.seen]).toString('latin1')
    expect(onWire).not.toContain('secret business')
  })

  it('refuses a device that does not hold a certificate from the pinned root', async () => {
    const { client: c, server: s } = pair()
    // 冒充者接受真控制端(用真根验证它),但出示的是另一个根签发的服务端证书。
    const impostor = acceptE2eServer(s, { caPem: pki.caPem, certificatePem: strangers.server.certificatePem,
      privateKeyPem: strangers.server.privateKeyPem, authenticate: () => ({}), timeoutMs: 500 }).catch((e: unknown) => e)
    await expect(client(c)).rejects.toMatchObject({ code: 'unauthorized' })
    await impostor
    expect(c.ended).toBe(true)
  })

  it('refuses an impostor that presents the real certificate without the private key', async () => {
    const { client: c, server: s } = pair()
    const impostor = acceptE2eServer(s, { caPem: pki.caPem, certificatePem: pki.server.certificatePem,
      privateKeyPem: strangers.server.privateKeyPem, authenticate: (key) => ({ key }), timeoutMs: 500 }).catch((e: unknown) => e)
    await expect(client(c)).rejects.toMatchObject({ code: 'unauthorized' })
    await impostor
  })

  it('refuses an unpaired or revoked controller before sending any server data', async () => {
    const { client: c, server: s } = pair()
    const outcomes = await Promise.allSettled([client(c), server(s, [])])
    expect(outcomes.map((o) => o.status)).toEqual(['rejected', 'rejected'])
    expect(s.seen).toEqual([]) // 服务端一个字节都没发
  })

  it('refuses a controller certificate issued by another root', async () => {
    const { client: c, server: s } = pair()
    const outcomes = await Promise.allSettled([
      client(c, { certificatePem: strangers.client.certificatePem, privateKeyPem: strangers.client.privateKeyPem }),
      server(s, [certificatePublicKeySha256(new X509Certificate(strangers.client.certificatePem))])
    ])
    expect(outcomes.map((o) => o.status)).toEqual(['rejected', 'rejected'])
  })

  it('refuses a controller that signs with a key other than its certificate key', async () => {
    const { client: c, server: s } = pair()
    const outcomes = await Promise.allSettled([
      client(c, { privateKeyPem: strangers.client.privateKeyPem }), server(s)
    ])
    // 服务端拒绝。客户端发完 Finish 就算握手结束(无法提前知道),但之后信道立刻被关闭,收不到任何数据。
    expect(outcomes[1]?.status).toBe('rejected')
    if (outcomes[0]?.status === 'fulfilled') {
      let closed = false
      outcomes[0].value.onClose(() => { closed = true })
      await settle()
      expect(closed).toBe(true)
      await expect(outcomes[0].value.send(new Uint8Array([1]))).rejects.toMatchObject({ code: 'disconnected' })
    }
  })

  it('refuses expired certificates on both sides', async () => {
    const far = Date.now() + 10 * 365 * 24 * 3600 * 1000
    const a = pair()
    expect((await Promise.allSettled([client(a.client, { now: () => far }), server(a.server)])).map((o) => o.status)).toEqual(['rejected', 'rejected'])
    const b = pair()
    expect((await Promise.allSettled([client(b.client), server(b.server, [clientKey()], { now: () => far })])).map((o) => o.status)).toEqual(['rejected', 'rejected'])
  })

  it('detects a swapped ephemeral key (man in the middle) through the transcript signatures', async () => {
    const { client: c, server: s } = pair()
    // 篡改 ClientHello 里的临时公钥一个字节。
    c.tap = (frame, index) => { if (index !== 0) return frame; const copy = Buffer.from(frame); copy[10] = (copy[10] ?? 0) ^ 0xff; return copy }
    const outcomes = await Promise.allSettled([client(c), server(s)])
    expect(outcomes.map((o) => o.status)).toEqual(['rejected', 'rejected'])
  })

  it('closes on a tampered, replayed, dropped or reordered data frame', async () => {
    const run = async (tap: Wire['tap']) => {
      const { client: c, server: s } = pair()
      const [ch, accepted] = await Promise.all([client(c), server(s)])
      const received = collect(accepted.transport)
      // 握手结束之后才开始篡改:tap 的序号从 0 起算数据帧。
      c.tap = tap === null ? null : (frame, index) => tap(frame, index)
      let closed = false
      accepted.transport.onClose(() => { closed = true })
      // 对端一发现异常就关闭,发送方后面的发送可能已经遇到关闭 —— 这正是被测行为,不算测试失败。
      for (const byte of [1, 2, 3]) await Promise.resolve(ch.send(new Uint8Array([byte]))).catch(() => undefined)
      await settle()
      return { received, closed }
    }
    const tampered = await run((frame, i) => { if (i !== 1) return frame; const x = Buffer.from(frame); x[x.length - 1] = (x[x.length - 1] ?? 0) ^ 1; return x })
    expect(tampered.closed).toBe(true); expect(tampered.received).toHaveLength(1)
    const replayed = await run((frame, i) => i === 0 ? [frame, frame] : frame)
    expect(replayed.closed).toBe(true); expect(replayed.received).toHaveLength(1)
    const dropped = await run((frame, i) => i === 1 ? null : frame)
    expect(dropped.closed).toBe(true); expect(dropped.received).toHaveLength(1)
    const reordered = await run((frame, i) => i === 0 ? null : frame)
    expect(reordered.closed).toBe(true); expect(reordered.received).toHaveLength(0)
    const clean = await run(null)
    expect(clean.closed).toBe(false); expect(clean.received).toHaveLength(3)
  })

  it('rejects oversized frames and closes together', async () => {
    const { client: c, server: s } = pair()
    const [ch, accepted] = await Promise.all([client(c), server(s)])
    await expect(ch.send(new Uint8Array(1024 * 1024))).rejects.toMatchObject({ code: 'resource_limit' })
    await expect(ch.send(new Uint8Array(1024 * 1024 - E2E_OVERHEAD))).resolves.toBeUndefined()
    let closed = false
    accepted.transport.onClose(() => { closed = true })
    ch.close()
    await settle()
    expect(closed).toBe(true)
    await expect(ch.send(new Uint8Array([1]))).rejects.toMatchObject({ code: 'disconnected' })
  })

  it('times out a peer that never answers the handshake and closes the carrier', async () => {
    const { client: c } = pair()
    await expect(client(c, { timeoutMs: 30 })).rejects.toMatchObject({ code: 'timeout' })
    expect(c.ended).toBe(true)
  })

  it('buffers frames that arrive before a consumer is attached', async () => {
    const { client: c, server: s } = pair()
    const [ch, accepted] = await Promise.all([client(c), server(s)])
    await ch.send(new Uint8Array([7])); await ch.send(new Uint8Array([8]))
    await settle()
    expect(collect(accepted.transport).map((frame) => frame[0])).toEqual([7, 8])
  })
})
