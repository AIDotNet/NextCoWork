import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { connectController } from '../../../controller-client/connect'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { closeDatabase } from '../../../main/db'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'

// 独立服务的整条入口链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { resetRuntimeForTest } from '../../../main/runtime'
import { drainAndStop, parseArgs, runDeviceService, UsageError } from '../main'
import type { DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
const lines: string[] = []
const io = { out: (line: string) => { lines.push(line) }, err: (line: string) => { lines.push(line) } }

beforeEach(() => { resetRuntimeForTest(); closeDatabase(); root = mkdtempSync(join(tmpdir(), 'ncw-main-')); lines.length = 0 })
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

describe('device service command line', () => {
  it('parses flags strictly', () => {
    expect(parseArgs(['--data-dir', '/d', '--port', '7000', '--advertise', 'a.lan', '--advertise', '10.0.0.2', '--invite']))
      .toEqual({ dataDir: '/d', host: '127.0.0.1', port: 7000, pairingPort: 0, advertise: ['a.lan', '10.0.0.2'], invite: true })
    for (const bad of [[], ['--data-dir'], ['--data-dir', '/d', '--port', '99999'], ['--data-dir', '/d', '--port', 'x'],
      ['--data-dir', '/d', '--bogus'], ['--data-dir', '/d', '--host', '--invite']]) {
      expect(() => parseArgs(bad)).toThrow(UsageError)
    }
  })

  it('starts headless, prints an invitation only when asked, and serves a paired controller', async () => {
    service = await runDeviceService({ ...parseArgs(['--data-dir', join(root, 'data'), '--invite']) }, io)
    expect(lines[0]).toMatch(/^device dev-.* listening on 127\.0\.0\.1:\d+/)
    const invitation = lines.find((line) => line.startsWith('ncwpair1.'))!
    expect(invitation).toBeTruthy()
    const credentials = await pairWithDevice(invitation, { controllerId: 'controller', label: 'CLI', confirmDevice: () => true })
    const transport = await connectDirectDevice(`wss://127.0.0.1:${service.address().port}/device`, credentials)
    transports.push(transport)
    const { client } = await connectController(transport, { expectedDeviceId: service.identity.deviceId, expectedControllerId: 'controller',
      clientSessionId: 'cli', bindingEpoch: 0, nextId: () => 'r', clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
    await expect(client.query('sessions.list', { workspaceId: 'nope' })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  })

  it('prints no invitation without --invite', async () => {
    service = await runDeviceService(parseArgs(['--data-dir', join(root, 'data')]), io)
    expect(lines.some((line) => line.includes('ncwpair1.'))).toBe(false)
  })

  it('refuses a wildcard listener without advertised hosts and leaves the data root unlocked', async () => {
    await expect(runDeviceService(parseArgs(['--data-dir', join(root, 'data'), '--host', '0.0.0.0']), io))
      .rejects.toMatchObject({ code: 'invalid_request' })
    service = await runDeviceService(parseArgs(['--data-dir', join(root, 'data'), '--host', '0.0.0.0', '--advertise', '127.0.0.1']), io)
    expect(service.address().host).toBe('0.0.0.0')
  })

  it('drains for an upgrade: no new pairing or tasks, existing controllers keep working, stop waits for idle', async () => {
    service = await runDeviceService(parseArgs(['--data-dir', join(root, 'data')]), io)
    const credentials = await pairWithDevice(service.createInvitation().text, { controllerId: 'controller', label: 'CLI', confirmDevice: () => true })
    const transport = await connectDirectDevice(`wss://127.0.0.1:${service.address().port}/device`, credentials)
    transports.push(transport)
    let n = 0
    const { client } = await connectController(transport, { expectedDeviceId: service.identity.deviceId, expectedControllerId: 'controller',
      clientSessionId: 'cli', bindingEpoch: 0, nextId: () => `r${++n}`, clock, capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
    mkdirSync(join(root, 'ws'))
    const workspace = await client.command('workspaces.create', { path: join(root, 'ws') }, 'w') as { result: { workspaceId: string } }
    const workspaceId = workspace.result.workspaceId
    const session = await client.command('sessions.create', { workspaceId }, 's') as { result: { sessionId: string } }
    const start = (key: string) => client.command('agent.start', { runId: `run-${key}`, sessionId: session.result.sessionId, text: 'hi',
      options: { workspaceId, mode: 'code', thinking: 'off', webSearch: false, permissionMode: 'ask', model: 'm', skillIds: [] } }, key) as Promise<{ status: string; result: { error?: { code: string } } }>

    let idle = false
    const stopped = drainAndStop(service, { maxWaitMs: 5000, pollMs: 10, isIdle: () => idle })
    await vi.waitFor(() => expect(service!.draining()).toBe(true))
    // 新配对关闭:不能再签发邀请,配对端点也不再监听。
    expect(() => service!.createInvitation()).toThrowError(expect.objectContaining({ code: 'busy' }))
    // 新任务被拒绝并留下 busy 的失败回执;已有连接仍能读。
    expect(await start('d1')).toMatchObject({ status: 'failed', result: { error: { code: 'busy' } } })
    expect(await client.query('workspaces.list', null)).toBeTruthy()
    // 还没排空:服务仍在。
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(await client.query('settings.get', null)).toBeTruthy()
    idle = true
    await expect(stopped).resolves.toBe(true)
    await expect(client.query('settings.get', null)).rejects.toBeInstanceOf(Error)
  })

  it('does not force-stop when tasks outlive the drain deadline', async () => {
    service = await runDeviceService(parseArgs(['--data-dir', join(root, 'data')]), io)
    await expect(drainAndStop(service, { maxWaitMs: 30, pollMs: 5, isIdle: () => false })).resolves.toBe(false)
    expect(service.draining()).toBe(true)
    // 仍在运行,可以被显式 stop。
    await service.stop()
  })
})
