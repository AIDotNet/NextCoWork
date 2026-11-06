/**
 * 中转服务命令行(可被测试直接调用,不读 process.argv/exit)。
 *
 *   serve     启动中转
 *     --registrations <file>   设备注册表(JSON:设备 id → 凭据的 sha256,**不含明文**)
 *     --host <addr>            默认 127.0.0.1;对外服务必须显式给出
 *     --port <n>               默认 8443
 *     --tls-cert <pem> --tls-key <pem>   TLS 证书与私钥文件
 *     --behind-tls             前面已有 TLS 终结(反向代理)时才允许明文监听
 *   register  为设备创建注册凭据并写入注册表,**只在这一次**打印明文凭据
 *     --registrations <file> --device <id>
 *   revoke    移除一个设备的注册
 *     --registrations <file> --device <id>
 *
 * ★ 注册凭据只证明「这个连接有权占用这个设备 id」,与设备配对凭据完全分离。
 */
import { chmodSync, existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { randomBytes } from 'node:crypto'
import { listenRelay, RelayRegistry, type Relay } from './server'

export class RelayUsageError extends Error {}

export interface RelayCliIo { out(line: string): void; err(line: string): void }

interface Parsed {
  command: 'serve' | 'register' | 'revoke'
  flags: Map<string, string>
  bare: Set<string>
}

const VALUE_FLAGS = new Set(['--registrations', '--host', '--port', '--tls-cert', '--tls-key', '--device'])
const BARE_FLAGS = new Set(['--behind-tls'])

export function parseRelayArgs(argv: readonly string[]): Parsed {
  const command = argv[0]
  if (command !== 'serve' && command !== 'register' && command !== 'revoke') throw new RelayUsageError('command must be serve, register or revoke')
  const flags = new Map<string, string>()
  const bare = new Set<string>()
  for (let i = 1; i < argv.length; i++) {
    const flag = argv[i]!
    if (BARE_FLAGS.has(flag)) { bare.add(flag); continue }
    if (!VALUE_FLAGS.has(flag)) throw new RelayUsageError(`Unknown option: ${flag}`)
    const value = argv[i + 1]
    if (value === undefined || value.startsWith('--')) throw new RelayUsageError(`${flag} needs a value`)
    flags.set(flag, value)
    i++
  }
  if (!flags.has('--registrations')) throw new RelayUsageError('--registrations is required')
  if (command !== 'serve' && !flags.has('--device')) throw new RelayUsageError('--device is required')
  return { command, flags, bare }
}

function loadRegistry(path: string): RelayRegistry {
  const registry = new RelayRegistry()
  if (!existsSync(path)) return registry
  let data: unknown
  try { data = JSON.parse(readFileSync(path, 'utf8')) } catch { throw new RelayUsageError('registrations file is not valid JSON') }
  if (data === null || typeof data !== 'object' || Array.isArray(data)) throw new RelayUsageError('registrations file has the wrong shape')
  for (const [id, hash] of Object.entries(data)) {
    if (typeof hash !== 'string') throw new RelayUsageError('registrations file has the wrong shape')
    try { registry.registerHash(id, hash) } catch { throw new RelayUsageError(`registrations file has an invalid entry for ${id.slice(0, 16)}`) }
  }
  return registry
}

/** 原子写,权限 0600:文件里虽只有摘要,仍不应对其他用户可读。 */
function saveRegistry(path: string, registry: RelayRegistry): void {
  const temp = `${path}.${randomBytes(4).toString('hex')}.tmp`
  writeFileSync(temp, JSON.stringify(registry.entries(), null, 2), { mode: 0o600 })
  chmodSync(temp, 0o600)
  renameSync(temp, path)
}

/** serve 返回运行中的中转(带 `reload()` 重新读取注册表);register/revoke 同步完成并返回 null。 */
export async function runRelayCli(argv: readonly string[], io: RelayCliIo): Promise<(Relay & { reload(): void }) | null> {
  const { command, flags, bare } = parseRelayArgs(argv)
  const file = flags.get('--registrations')!
  const registry = loadRegistry(file)
  if (command === 'register') {
    const token = randomBytes(32).toString('base64url')
    registry.register(flags.get('--device')!, token)
    saveRegistry(file, registry)
    io.out(token)
    return null
  }
  if (command === 'revoke') {
    const removed = registry.revoke(flags.get('--device')!)
    if (removed) saveRegistry(file, registry)
    io.out(removed ? 'revoked' : 'not registered')
    return null
  }
  const cert = flags.get('--tls-cert')
  const key = flags.get('--tls-key')
  if ((cert === undefined) !== (key === undefined)) throw new RelayUsageError('--tls-cert and --tls-key must be given together')
  const portText = flags.get('--port') ?? '8443'
  if (!/^[0-9]{1,5}$/.test(portText) || Number(portText) > 65535) throw new RelayUsageError('--port must be a port number')
  // 校验永远读「当前」的表:`reload()`(SIGHUP)换表之后,新注册/撤销无需重启即生效。
  const state = { registry }
  const relay = await listenRelay({
    host: flags.get('--host') ?? '127.0.0.1', port: Number(portText),
    ...(cert !== undefined && key !== undefined && { tls: { certificatePem: readFileSync(cert, 'utf8'), privateKeyPem: readFileSync(key, 'utf8') } }),
    ...(bare.has('--behind-tls') && { behindTls: true }),
    authenticateDevice: (deviceId, token) => state.registry.verify(deviceId, token)
  })
  const address = relay.address()
  io.out(`relay listening on ${address.host}:${address.port}`)
  return Object.assign(relay, { reload: () => { state.registry = loadRegistry(file) } })
}
