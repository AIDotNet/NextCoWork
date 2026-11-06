/**
 * Windows 内置 SSH 客户端(`ssh2`)的主机信任与取消正确性。
 *
 * ★ 被替换掉的那行是 `hostVerifier: () => true` —— 它对**任何**主机密钥都点头,包括
 * "密钥换了一台机器"。后果不是报错:客户端会接着把密码交给那台机器,而这正是用户看不见的
 * 那一步。所以下面钉的是四件事:
 *
 *   1. 真的会验指纹,而且**先于认证** —— 指纹对不上时一句密码都不发。
 *   2. 同一个指纹只问一次:首次确认后写 pin,第二次连接直接连上。
 *   3. 取消 / 断开撤掉挂着的确认,迟到的回答(以及迟到的「批准」)不生效。
 *   4. `supports()` 只收真的能连的形状,不让 key / interactive / identityFile / ProxyJump
 *      掉进这条只会发密码的链路。
 *
 * 用一个真的 `ssh2` 服务端当对手:主机密钥是现生成的,指纹是自己算的,认证回调记账,
 * 所以「密码有没有被发出去」是**观测**到的,不是推断的。不碰用户 known_hosts、不碰真机 sshd。
 */
import { randomBytes } from 'node:crypto'
import { describe, expect, it, vi } from 'vitest'
import { Server, utils, type AuthContext } from 'ssh2'
import type { SshConnectionProfile } from '../../../shared/domain/environment'
import { nodeHost } from '../../kernel/host'
import { SshAuthBroker } from '../ssh/askpass'
import { BundledSshClient, hostKeyFingerprint, hostKeyRef } from '../ssh/bundled-client'

const hostKey = utils.generateKeyPairSync('ed25519')
/** OpenSSH 公钥行的第二段就是 blob 的 base64,和 ssh2 传给 `hostVerifier` 的 `key` 同一份字节。 */
const hostKeyFingerprintExpected = (): string => hostKeyFingerprint(Buffer.from(hostKey.public.split(' ')[1]!, 'base64'))

const PASSWORD = 's3cret'
const profile: SshConnectionProfile = { id: 'bundled-test', kind: 'ssh', name: 'bundled', enabled: true, platform: 'linux',
  revision: 1, createdAt: 0, updatedAt: 0, target: { kind: 'manual', host: '127.0.0.1', port: 22, username: 'token' } }

function profileTo(port: number, overrides: Partial<SshConnectionProfile> = {}): SshConnectionProfile {
  return { ...profile, target: { kind: 'manual', host: '127.0.0.1', port, username: 'token' }, ...overrides }
}

function memorySecrets(initial: Record<string, string> = {}): { get(ref: string): Promise<string | null>; set(ref: string, value: string): Promise<void>; available(): boolean; store: Map<string, string> } {
  const store = new Map(Object.entries(initial))
  return {
    store,
    get: (ref) => Promise.resolve(store.get(ref) ?? null),
    set: (ref, value) => { store.set(ref, value); return Promise.resolve() },
    available: () => true
  }
}

/** 只做密码认证的 `ssh2` 服务端。`seen()` 数的是**每一次密码尝试**,也就是"密码真的发出去了"。 */
async function fakeSshd(): Promise<{ port: number; seen(): number; close(): Promise<void> }> {
  const seen = { count: 0 }
  const server = new Server({ hostKeys: [hostKey.private] }, (connection) => {
    /**
     * 客户端拒绝主机密钥时 ssh2 会回一条 DISCONNECT(KEY_EXCHANGE_FAILED),服务端把它
     * `emit('error')`。不接住的话 vitest 会把它算成"未捕获异常"—— 而那**正是**要测的结局。
     */
    connection.on('error', () => {})
    connection.on('authentication', (context: AuthContext) => {
      if (context.method === 'password') {
        seen.count++
        if (context.password === PASSWORD) context.accept()
        else context.reject()
      } else if (context.method === 'none') context.reject(['password'])
      else context.reject()
    })
  })
  return await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const address = server.address()
      if (!address || typeof address === 'string') { reject(new Error('no loopback port')); return }
      resolve({ port: address.port, seen: () => seen.count, close: () => new Promise<void>((done) => server.close(() => done())) })
    })
  })
}

const signal = (): AbortSignal => new AbortController().signal

describe('supports', () => {
  it('只接受手动填写、且这条链路真的做得成的形状', () => {
    expect(BundledSshClient.supports(profile)).toBe(true)
    for (const authMethod of ['auto', 'password', 'ask', undefined] as const) {
      expect(BundledSshClient.supports({ ...profile, authMethod }), `authMethod=${String(authMethod)} 可以走内置客户端`).toBe(true)
    }
  })

  it('key / interactive 继续走系统 OpenSSH,不能悄悄降级成密码认证', () => {
    expect(BundledSshClient.supports({ ...profile, authMethod: 'key' })).toBe(false)
    expect(BundledSshClient.supports({ ...profile, authMethod: 'interactive' })).toBe(false)
  })

  /** identityFile / proxyJump 只有系统 ssh 做得成;收下它们就是把它们降级成密码 */
  it('identityFile 与 proxyJump 交给系统 OpenSSH', () => {
    expect(BundledSshClient.supports({ ...profile, target: { kind: 'manual', host: 'h', port: 22, username: 'u', identityFile: '/home/u/.ssh/id_ed25519' } })).toBe(false)
    expect(BundledSshClient.supports({ ...profile, target: { kind: 'manual', host: 'h', port: 22, username: 'u', proxyJump: 'jump' } })).toBe(false)
  })

  it('配置型别名、空主机、坏端口都拒绝', () => {
    expect(BundledSshClient.supports({ ...profile, target: { kind: 'config', host: 'alias' } })).toBe(false)
    expect(BundledSshClient.supports({ ...profile, target: { kind: 'manual', host: '', port: 22, username: 'u' } })).toBe(false)
    expect(BundledSshClient.supports({ ...profile, target: { kind: 'manual', host: 'h', port: 0, username: 'u' } })).toBe(false)
    expect(BundledSshClient.supports({ ...profile, target: { kind: 'manual', host: 'h', port: 22.5, username: 'u' } })).toBe(false)
  })
})

describe('主机密钥指纹', () => {
  /** 指纹必须和 `ssh-keygen -lf` 逐字一致:sha256 的 base64,无 `=` 填充、无 `SHA256:` 前缀 */
  it('算出的是 ssh-keygen 那种 SHA256 指纹', () => {
    expect(hostKeyFingerprintExpected()).toMatch(/^[A-Za-z0-9+/]{43}$/u)
    expect(hostKeyFingerprint(Buffer.from(hostKey.public.split(' ')[1]!, 'base64'))).toBe(hostKeyFingerprintExpected())
  })

  it('槽位按连接 + host:port 分开', () => {
    expect(hostKeyRef('c1', '10.0.0.7', 22)).not.toBe(hostKeyRef('c1', '10.0.0.8', 22))
    expect(hostKeyRef('c1', '10.0.0.7', 22)).not.toBe(hostKeyRef('c1', '10.0.0.7', 2222))
    expect(hostKeyRef('c1', 'h', 22)).not.toBe(hostKeyRef('c2', 'h', 22))
  })
})

describe('连接时真的验主机密钥', () => {
  it('首次:确认后连上,并把真实指纹写进 secrets', async () => {
    const server = await fakeSshd()
    const secrets = memorySecrets()
    const asked: string[] = []
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD,
      { secrets, confirm: async (prompt) => { asked.push(prompt); return true } })
    try {
      await client.connect(signal())
      expect(asked).toHaveLength(1)
      expect(asked[0], '确认框里必须展示真实指纹').toContain(`SHA256:${hostKeyFingerprintExpected()}`)
      expect(asked[0]).toContain('yes/no/[fingerprint]')
      expect(asked[0], '主机确认读不出也不该带上密码').not.toContain(PASSWORD)
      expect(secrets.store.get(hostKeyRef(profile.id, '127.0.0.1', server.port)), '只有明确的「是」才 pin').toBe(hostKeyFingerprintExpected())
    } finally { await client.close(); await server.close() }
  })

  it('已 pin 且一致:不弹窗、直接连上', async () => {
    const server = await fakeSshd()
    const secrets = memorySecrets({ [hostKeyRef(profile.id, '127.0.0.1', server.port)]: hostKeyFingerprintExpected() })
    const confirm = vi.fn(async () => true)
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD, { secrets, confirm })
    try {
      await client.connect(signal())
      expect(confirm, '已 pin 就不该再问').not.toHaveBeenCalled()
    } finally { await client.close(); await server.close() }
  })

  /** 用户点「取消」:连不上,而且**一句密码都不发** */
  it('取消确认:连接失败且不发送密码', async () => {
    const server = await fakeSshd()
    const secrets = memorySecrets()
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD, { secrets, confirm: async () => false })
    try {
      await expect(client.connect(signal())).rejects.toThrow('environment:host-key')
      expect(server.seen(), '主机密钥没确认就不能走到认证').toBe(0)
      expect(secrets.store.get(hostKeyRef(profile.id, '127.0.0.1', server.port))).toBeUndefined()
    } finally { await client.close(); await server.close() }
  })

  /** 指纹对不上:fail closed,**不给**确认按钮,也不回退发密码 */
  it('已 pin 的指纹变了:直接拒绝,不问、不发密码、不覆盖', async () => {
    const server = await fakeSshd()
    const ref = hostKeyRef(profile.id, '127.0.0.1', server.port)
    const stale = hostKeyFingerprint(randomBytes(48))
    const secrets = memorySecrets({ [ref]: stale })
    const confirm = vi.fn(async () => true)
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD, { secrets, confirm })
    try {
      /**
       * ★ 错误码必须是 `host-key`。ssh2 把"拒了密钥"和"服务器把密钥换了"收成同一条
       * `KEY_EXCHANGE_FAILED`;要是让它掉进 `connection-failed`,用户看到的会是
       * 「无法连接服务器,请检查网络」—— 恰好把这个改动存在的意义盖掉。
       */
      await expect(client.connect(signal())).rejects.toThrow('environment:host-key')
      expect(confirm, '变更的密钥不该给用户一个"确认"按钮').not.toHaveBeenCalled()
      expect(server.seen(), '密钥不符时绝不能把密码发出去').toBe(0)
      expect(secrets.store.get(ref), '不能静默覆盖旧指纹').toBe(stale)
    } finally { await client.close(); await server.close() }
  })

  /** secrets 读失败也要 fail closed —— 读不到已 pin 的指纹,就不能当"首次使用"放行 */
  it('secrets.get 抛错:判否,不发密码', async () => {
    const server = await fakeSshd()
    const failing = { get: () => Promise.reject(new Error('keychain locked')), set: () => Promise.resolve(), available: () => true }
    const confirm = vi.fn(async () => true)
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD, { secrets: failing, confirm })
    try {
      await expect(client.connect(signal())).rejects.toThrow('environment:host-key')
      expect(confirm, '读不到 pin 也不能当成首次使用去弹窗').not.toHaveBeenCalled()
      expect(server.seen()).toBe(0)
    } finally { await client.close(); await server.close() }
  })

  it('写入主机 pin 失败时不继续发密码', async () => {
    const server = await fakeSshd()
    const secrets = { ...memorySecrets(), set: async () => { throw new Error('storage unavailable') } }
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD,
      { secrets, confirm: async () => true })
    try {
      await expect(client.connect(signal())).rejects.toThrow('environment:host-key')
      expect(server.seen()).toBe(0)
    } finally { await client.close(); await server.close() }
  })

  it('密码等待途中被 abort，迟到的密码不能进入认证', async () => {
    const server = await fakeSshd()
    const controller = new AbortController()
    let answer!: (value: string) => void
    const password = vi.fn(() => new Promise<string>((resolve) => { answer = resolve }))
    const client = new BundledSshClient(profileTo(server.port), password,
      { secrets: memorySecrets(), confirm: async () => true })
    try {
      const connecting = client.connect(controller.signal)
      await vi.waitFor(() => expect(password).toHaveBeenCalled())
      controller.abort()
      answer(PASSWORD)
      await expect(connecting).rejects.toThrow('cancelled')
      expect(server.seen()).toBe(0)
    } finally { await client.close(); await server.close() }
  })

  it('缺 verifier:默认拒绝,连不上也不发密码', async () => {
    const server = await fakeSshd()
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD)
    try {
      await expect(client.connect(signal())).rejects.toThrow()
      expect(server.seen()).toBe(0)
    } finally { await client.close(); await server.close() }
  })

  /** 迟到的「批准」不能生效:用户在确认途中断开 */
  it('确认途中被 abort:迟到的批准不算数', async () => {
    const server = await fakeSshd()
    const controller = new AbortController()
    let approve!: (value: boolean) => void
    const confirm = vi.fn(() => new Promise<boolean>((resolve) => { approve = resolve }))
    const client = new BundledSshClient(profileTo(server.port), async () => PASSWORD, { secrets: memorySecrets(), confirm })
    try {
      const connecting = client.connect(controller.signal)
      await vi.waitFor(() => expect(confirm).toHaveBeenCalledTimes(1))
      controller.abort()
      approve(true)
      await expect(connecting).rejects.toThrow('cancelled')
      expect(server.seen(), '连接已经没了,迟到的批准不能把它做完').toBe(0)
    } finally { await client.close(); await server.close() }
  })

  /** 取消密码框是"用户放弃",不是"连不上" —— 界面不该为它弹一条连接错误 */
  it('密码框被取消:报 cancelled,不是连接失败', async () => {
    const server = await fakeSshd()
    const client = new BundledSshClient(profileTo(server.port), async () => { throw new Error('cancelled') },
      { secrets: memorySecrets(), confirm: async () => true })
    try {
      await expect(client.connect(signal())).rejects.toThrow('environment:cancelled')
    } finally { await client.close(); await server.close() }
  })
})

describe('broker 的取消正确性', () => {
  /**
   * ★ `close()` 之后,界面上那条确认必须**立刻**消失。否则用户在断开之后点「批准」,
   * 批准会回给一段已经结束的握手 —— 而内置客户端那条链路连着的是密码认证。
   */
  it('close() 撤掉这次尝试挂着的全部问答', async () => {
    const seen: string[] = []
    const broker = new SshAuthBroker(nodeHost().secrets, (_sender, request) => seen.push(request.id))
    const session = await broker.open(profile, 1, { executable: process.execPath })
    // 走 session 自己那对 ask/askHostKey —— 就是 provider 交给内置客户端的那两个
    const password = session.ask("token@127.0.0.1's password: ", false)
    const trust = session.askHostKey('The authenticity of host ... (yes/no/[fingerprint])? ')
    await vi.waitFor(() => expect(seen).toHaveLength(2))
    const passwordRejected = expect(password).rejects.toThrow('cancelled')
    await session.close()
    await expect(trust, '断开之后不能再挂着主机确认').resolves.toBe(false)
    await passwordRejected
    await expect(broker.respond(1, { id: seen[1]!, value: 'yes' }), '迟到的回答打不到任何东西').rejects.toThrow('approval-expired')
  })

  it('askHostKey 只认明确的「yes」', async () => {
    const requests: Array<{ id: string }> = []
    const broker = new SshAuthBroker(nodeHost().secrets, (_sender, request) => requests.push(request))
    const session = await broker.open(profile, 1, { executable: process.execPath })
    try {
      const refused = broker.askHostKey(1, profile, 'trust?')
      await vi.waitFor(() => expect(requests).toHaveLength(1))
      await broker.respond(1, { id: requests[0]!.id, value: 'no' })
      await expect(refused).resolves.toBe(false)

      const accepted = broker.askHostKey(1, profile, 'trust?')
      await vi.waitFor(() => expect(requests).toHaveLength(2))
      await broker.respond(1, { id: requests[1]!.id, value: 'yes' })
      await expect(accepted).resolves.toBe(true)
    } finally { await session.close() }
  })

  /** 主机确认是 `host-key`,不带「使用已保存的凭据」—— 它读不出、也不该复用存下的密码 */
  it('主机确认不能被当成密码槽位', async () => {
    const seen: Array<{ kind: string; canRemember: boolean; hasSaved: boolean }> = []
    const broker = new SshAuthBroker(nodeHost().secrets, (_sender, request) => seen.push(request))
    const session = await broker.open(profile, 1, { executable: process.execPath })
    try {
      const trust = session.askHostKey('trust?')
      await vi.waitFor(() => expect(seen).toHaveLength(1))
      expect(seen[0]).toMatchObject({ kind: 'host-key', canRemember: false, hasSaved: false })
      broker.cancelWindow(1)
      await expect(trust, '撤掉窗口的问答时,主机确认也要被判否').resolves.toBe(false)
    } finally { await session.close() }
  })
})
