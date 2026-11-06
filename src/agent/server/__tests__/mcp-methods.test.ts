import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { closeDatabase, stmt } from '../../../main/db'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'
import type { DeviceValue } from '../../../shared/device/value'

// MCP 方法(含桌面的 ipc/mcp.ts)整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest, shutdownMcp } from '../../../main/runtime'
import { headlessHost } from '../../../main/host/headless'
import { registerMcpMethods } from '../methods/mcp'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
const TOKEN = 'tok-SECRET-123456'
/** 真的 stdio MCP 服务器:没有 FIX_TOKEN 就启动失败,所以「已连接」本身证明密钥被送进了子进程环境。 */
const FIXTURE = `
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
if (!process.env.FIX_TOKEN) { process.stderr.write('missing token at /secret/path ' + process.cwd()); process.exit(3) }
const server = new McpServer({ name: 'fixture', version: '1.0.0' })
server.tool('ping', async () => ({ content: [{ type: 'text', text: 'pong' }] }))
await server.connect(new StdioServerTransport())
`
let root = ''
let script = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }
type Status = { config: { id: string }; state: string; toolCount: number; tools: string[]; error?: string }

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-mcp-methods-'))
  mkdirSync(join(process.cwd(), '.tscache'), { recursive: true })
  // 放在仓库内,才能按裸说明符解析 node_modules 里的 SDK。
  script = join(process.cwd(), '.tscache', `mcp-fixture-${Date.now()}-${Math.random().toString(36).slice(2, 8)}.mjs`)
  writeFileSync(script, FIXTURE)
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router) => registerMcpMethods(router) })
  installHost(headlessHost())
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await shutdownMcp()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(script, { force: true })
  rmSync(root, { recursive: true, force: true })
})

async function controller(): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId: 'controller', label: 'm', confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: 'controller',
    clientSessionId: 'mcp', bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}
type Config = Record<string, DeviceValue>
const stdio = (over: Config = {}): Config => ({ transport: 'stdio', id: 'fix', name: 'Fixture', enabled: true,
  command: process.execPath, args: [script], envNames: ['FIX_TOKEN'], ...over })
const upsert = (client: ControllerClient, key: string, config: Config) =>
  client.command('mcp.upsert', { config }, key) as Promise<Receipt>
const find = async (client: ControllerClient): Promise<Status | undefined> =>
  (await client.query('mcp.list', null) as Status[]).find((s) => s.config.id === 'fix')
const receiptsDump = (): string => stmt('SELECT * FROM device_commands').all()
  .map((row) => Object.values(row).map((v) => v instanceof Uint8Array ? Buffer.from(v).toString('latin1') : String(v)).join('|')).join('\n')

describe('remote MCP management (real stdio server)', () => {
  it('connects a real stdio server only once its secret is supplied, and never lets the value come back', async () => {
    const client = await controller()
    // 还没有密钥:子进程自己退出,状态是 error;错误原文(含路径)不出设备。
    const first = await upsert(client, 'm1', stdio())
    expect(first.status).toBe('succeeded')
    const failed = await find(client)
    expect(failed?.state).toBe('error')
    expect(JSON.stringify(failed)).not.toContain('/secret/path')
    expect(failed?.error).toBe('Connection failed')

    const stored = await client.command('mcp.setSecrets', { id: 'fix', values: { FIX_TOKEN: TOKEN } }, 'm2') as Receipt
    expect(stored.result).toEqual({ storedNames: ['FIX_TOKEN'], encryptionAvailable: true })
    await vi.waitFor(async () => expect((await find(client))?.state).toBe('connected'), { timeout: 15_000 })
    const live = await find(client)
    expect(live?.toolCount).toBeGreaterThanOrEqual(1)
    expect(live?.tools.some((name) => name.includes('ping'))).toBe(true)

    // 值既不在任何响应里,也不在落盘的回执里;只有键名可读。
    for (const dump of [JSON.stringify(live), JSON.stringify(stored), JSON.stringify(await client.query('mcp.secretsInfo', { id: 'fix' })), receiptsDump()]) {
      expect(dump).not.toContain(TOKEN)
    }
    expect(await client.query('mcp.secretsInfo', { id: 'fix' })).toEqual({ storedNames: ['FIX_TOKEN'], encryptionAvailable: true })
    await expect(client.query('mcp.revealSecrets', { id: 'fix' })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  }, 40_000)

  it('writes the whole secret map at once and drops names the config never declared', async () => {
    const client = await controller()
    await upsert(client, 'm1', stdio())
    const result = await client.command('mcp.setSecrets', { id: 'fix', values: { FIX_TOKEN: TOKEN, UNDECLARED: 'x' } }, 'm2') as Receipt
    expect(result.result).toMatchObject({ storedNames: ['FIX_TOKEN'] })
    // 整张 map 替换:不带 FIX_TOKEN 的写入把它删掉(而不是累加保留)。
    const cleared = await client.command('mcp.setSecrets', { id: 'fix', values: { UNDECLARED: 'y' } }, 'm3') as Receipt
    expect(cleared.result).toMatchObject({ storedNames: [] })
    client.detach()
  }, 40_000)

  it('rejects unsafe or malformed configurations with fixed messages, before or after the receipt as appropriate', async () => {
    const client = await controller()
    const bad: Config[] = [
      { ...stdio(), id: 'bad id!' }, { ...stdio(), command: '   ' }, { ...stdio(), name: '   ' },
      { transport: 'sse', id: 'h1', name: 'H', enabled: true, url: 'file:///etc/passwd', headerNames: [] },
      { transport: 'streamable-http', id: 'h2', name: 'H', enabled: true, url: 'not a url', headerNames: [] }
    ]
    for (const [index, config] of bad.entries()) {
      const receipt = await upsert(client, `b${index}`, config).catch((error: unknown) => error)
      // 形状错误在 schema 层被拒(协议错误);语义错误(空命令、坏地址)是失败回执,且没有泄露输入。
      const text = JSON.stringify(receipt)
      expect(text).not.toContain('/etc/passwd')
      expect(text).not.toContain('not a url')
    }
    expect(await upsert(client, 'v1', stdio({ command: '   ' }))).toMatchObject({ status: 'failed', result: { error: { code: 'invalid_request', message: 'MCP request was rejected' } } })
    // 内部字段/坏键名在落回执之前就被拒绝。
    await expect(upsert(client, 's1', stdio({ envNames: ['bad name!'] }))).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(upsert(client, 's2', stdio({ env: { FIX_TOKEN: TOKEN } }))).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(upsert(client, 's3', { ...stdio(), transport: 'carrier-pigeon' })).rejects.toMatchObject({ code: 'invalid_request' })
    expect(await find(client)).toBeUndefined()
    client.detach()
  }, 40_000)

  it('tests, disables and removes a server together with its secrets', async () => {
    const client = await controller()
    await upsert(client, 'm1', stdio())
    await client.command('mcp.setSecrets', { id: 'fix', values: { FIX_TOKEN: TOKEN } }, 'm2')
    const tested = await client.command('mcp.test', { id: 'fix' }, 'm3') as Receipt
    expect(tested).toMatchObject({ status: 'succeeded', result: { state: 'connected' } })
    // 停用只断开不删配置。
    expect(await upsert(client, 'm4', stdio({ enabled: false }))).toMatchObject({ status: 'succeeded', result: { state: 'disconnected' } })
    expect((await find(client))?.state).toBe('disconnected')
    expect(await client.command('mcp.remove', { id: 'fix' }, 'm5')).toMatchObject({ status: 'succeeded', result: { removed: true } })
    expect(await find(client)).toBeUndefined()
    await expect(client.query('mcp.secretsInfo', { id: 'fix' })).rejects.toMatchObject({ code: 'not_found' })
    expect(await client.command('mcp.remove', { id: 'fix' }, 'm6')).toMatchObject({ status: 'succeeded', result: { removed: false } })
    expect(await client.command('mcp.test', { id: 'nope' }, 'm7')).toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    client.detach()
  }, 40_000)
})
