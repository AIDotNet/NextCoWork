import { EventEmitter } from 'node:events'
import type { WebContents } from 'electron'
import { describe, expect, it, vi } from 'vitest'
import { nodeHost } from '../kernel/host'
import { localEnvironment } from '../environment/local'
import { EnvironmentError } from '../environment/errors'
import type { TerminalDriver } from '../environment/contract'
import { TerminalHost } from '../terminal-host'

vi.mock('../window/registry', () => ({ terminalTopic: (id: string) => id, windows: { subscribe: vi.fn(), emitToTopic: vi.fn() } }))
vi.mock('../runtime', () => ({ getEnvironments: () => { throw new Error('Unexpected production environment') } }))

function fixture() {
  let now = 1
  let ready = true
  const listeners = new Set<(event: { exitCode: number }) => void>()
  const driver: TerminalDriver = { write: vi.fn(), resize: vi.fn(), kill: vi.fn(),
    onData: () => ({ dispose() {} }), onExit: (listener) => { listeners.add(listener); return { dispose() {} } } }
  const environment = { ...localEnvironment(nodeHost(), '/workspace'), remote: true, key: 'server:1', description: 'server',
    openTerminal: vi.fn(async () => driver), assertReady: () => { if (!ready) throw new EnvironmentError('disconnected') } }
  environment.path.resolveWithin = async (_root, path) => path
  environment.fs.stat = async () => ({ isDir: true, isFile: false, isSymbolicLink: false, mode: 0o755, size: 0, mtimeMs: 0 })
  const acquire = () => ({ environment, release: vi.fn() })
  const host = new TerminalHost(acquire, () => now)
  const owner = (id: number) => Object.assign(new EventEmitter(), { id, isDestroyed: () => false }) as WebContents
  const sender = owner(1)
  const other = owner(2)
  const request = { id: 'tab', workspaceId: 'workspace', cols: 80, rows: 24 }
  const intent = async () => {
    const prepared = await host.prepare(request, sender)
    if (prepared.kind !== 'approval') throw new Error('Expected approval')
    return prepared.intent
  }
  return { host, environment, sender, other, request, intent, driver, listeners, advance: () => { now += 61_000 }, disconnect: () => { ready = false } }
}

describe('SSH terminal page approval', () => {
  it('does not spawn before an explicit per-page approval', async () => {
    const state = fixture()
    await expect(state.host.create(state.request, state.sender)).rejects.toThrow('approval-required')
    const intent = await state.intent()
    await expect(state.host.create({ ...state.request, approval: intent.id }, state.sender)).rejects.toThrow('approval-required')
    state.host.approve(intent.id, false, state.sender)
    expect(state.environment.openTerminal).not.toHaveBeenCalled()
    state.host.shutdown()
  })
  it('consumes a grant once, coalesces creation, and attaches only a living owned session', async () => {
    const state = fixture()
    const intent = await state.intent()
    const approval = state.host.approve(intent.id, true, state.sender)!
    const request = { ...state.request, approval }
    await Promise.all([state.host.create(request, state.sender), state.host.create(request, state.sender)])
    expect(state.environment.openTerminal).toHaveBeenCalledTimes(1)
    expect((await state.host.prepare(state.request, state.sender)).kind).toBe('ready')
    await expect(state.host.create(request, state.other)).rejects.toThrow('approval-required')
    expect(() => state.host.attach('tab', state.other)).toThrow('approval-required')
    expect(() => state.host.write('tab', 'command', state.other)).toThrow('approval-required')
    state.listeners.forEach((listener) => listener({ exitCode: 0 }))
    await expect(state.host.create(request, state.sender)).rejects.toThrow('approval-required')
    expect((await state.host.prepare(state.request, state.sender)).kind).toBe('approval')
    state.host.shutdown()
  })
  it('rejects expired, cross-page, cross-workspace and disconnected grants', async () => {
    const state = fixture()
    const intent = await state.intent()
    expect(() => state.host.approve(intent.id, true, state.other)).toThrow('approval-expired')
    const approval = state.host.approve(intent.id, true, state.sender)!
    await expect(state.host.create({ ...state.request, id: 'other', approval }, state.sender)).rejects.toThrow('approval-required')
    await expect(state.host.create({ ...state.request, workspaceId: 'other', approval }, state.sender)).rejects.toThrow('approval-required')
    state.advance()
    await expect(state.host.create({ ...state.request, approval }, state.sender)).rejects.toThrow('approval-expired')
    const fresh = await state.intent()
    const next = state.host.approve(fresh.id, true, state.sender)!
    state.disconnect()
    await expect(state.host.create({ ...state.request, approval: next }, state.sender)).rejects.toThrow('disconnected')
    expect(state.environment.openTerminal).not.toHaveBeenCalled()
    state.host.shutdown()
  })
  it('invalidates pending approvals on window destruction', async () => {
    const state = fixture()
    const intent = await state.intent()
    state.sender.emit('destroyed')
    expect(() => state.host.approve(intent.id, true, state.sender)).toThrow('approval-expired')
    expect(state.environment.openTerminal).not.toHaveBeenCalled()
    state.host.shutdown()
  })
  it.each(['page', 'window', 'shutdown'])('kills a late PTY after %s closure', async (closure) => {
    const state = fixture()
    const opened = Promise.withResolvers<void>()
    const pending = Promise.withResolvers<TerminalDriver>()
    state.environment.openTerminal.mockImplementation(async () => { opened.resolve(); return pending.promise })
    const intent = await state.intent()
    const approval = state.host.approve(intent.id, true, state.sender)!
    const creation = state.host.create({ ...state.request, approval }, state.sender)
    const rejected = expect(creation).rejects.toThrow('cancelled')
    await opened.promise
    if (closure === 'page') state.host.kill(state.request.id, state.sender)
    else if (closure === 'window') state.sender.emit('destroyed')
    else state.host.shutdown()
    pending.resolve(state.driver)
    await rejected
    expect(state.driver.kill).toHaveBeenCalledTimes(1)
    expect(state.host.list(state.request.workspaceId, state.sender)).toEqual([])
    state.host.shutdown()
  })
  it('does not coalesce a different cwd into an approved creation', async () => {
    const state = fixture()
    const intent = await state.intent()
    const approval = state.host.approve(intent.id, true, state.sender)!
    const creation = state.host.create({ ...state.request, approval }, state.sender)
    await expect(state.host.create({ ...state.request, approval, cwd: '/other' }, state.sender)).rejects.toThrow('approval-required')
    await creation
    state.host.shutdown()
  })
})
/** 本地环境下的启动 spec 消费 —— 插件终端(`tabs.openTerminal`)的机制。 */
describe('terminal launch specs', () => {
  function localFixture() {
    const now = 1
    const listeners = new Set<(event: { exitCode: number }) => void>()
    const driver: TerminalDriver = { write: vi.fn(), resize: vi.fn(), kill: vi.fn(),
      onData: () => ({ dispose() {} }), onExit: (listener) => { listeners.add(listener); return { dispose() {} } } }
    const environment = { ...localEnvironment(nodeHost(), '/workspace'),
      openTerminal: vi.fn(async (_options: { cwd: string; cols: number; rows: number; env?: Record<string, string> }): Promise<TerminalDriver> => driver),
      assertReady: () => {} }
    environment.path.resolveWithin = async (_root, path) => path
    environment.fs.stat = async () => ({ isDir: true, isFile: false, isSymbolicLink: false, mode: 0o755, size: 0, mtimeMs: 0 })
    const acquire = () => ({ environment, release: vi.fn() })
    const host = new TerminalHost(acquire, () => now)
    const sender = Object.assign(new EventEmitter(), { id: 1, isDestroyed: () => false }) as WebContents
    const request = { id: 'tab', workspaceId: 'workspace', cols: 80, rows: 24 }
    return { host, environment, sender, request, driver, listeners }
  }

  it('★ 命中 spec 的 create 把 env 注入 spawn,并把启动行写进 pty', async () => {
    const state = localFixture()
    state.host.setLaunchSpec('tab', { workspaceId: 'workspace', env: { ANTHROPIC_BASE_URL: 'https://relay' }, argv: ['claude'] })
    await state.host.create(state.request, state.sender)
    expect(state.environment.openTerminal).toHaveBeenCalledWith(expect.objectContaining({ env: { ANTHROPIC_BASE_URL: 'https://relay' } }))
    expect(state.driver.write).toHaveBeenCalledWith('claude\n')
    state.host.shutdown()
  })

  it('★ spec 是一次性的:活会话上的再次 create 不重放启动行', async () => {
    const state = localFixture()
    state.host.setLaunchSpec('tab', { workspaceId: 'workspace', env: {}, argv: ['claude'] })
    await state.host.create(state.request, state.sender)
    await state.host.create(state.request, state.sender)
    expect(state.environment.openTerminal).toHaveBeenCalledTimes(1)
    expect(state.driver.write).toHaveBeenCalledTimes(1)
    state.host.shutdown()
  })

  it('argv 里的空格和单引号被安全转义后才落进启动行', async () => {
    const state = localFixture()
    state.host.setLaunchSpec('tab', { workspaceId: 'workspace', env: {}, argv: ['codex', '-m', "gpt 5 it's"] })
    await state.host.create(state.request, state.sender)
    expect(state.driver.write).toHaveBeenCalledWith(`codex -m ${`'gpt 5 it'\\''s'`}\n`)
    state.host.shutdown()
  })

  it('★ 远程环境不消费 spec:env 注入不进去,启动行也绝不写(双重丢失中至少保住秘密)', async () => {
    const state = localFixture()
    ;(state.environment as { remote: boolean }).remote = true
    state.host.setLaunchSpec('tab', { workspaceId: 'workspace', env: { KEY: 'secret' }, argv: ['claude'] })
    const intent = await (async () => {
      const prepared = await state.host.prepare(state.request, state.sender)
      if (prepared.kind !== 'approval') throw new Error('Expected approval')
      return prepared.intent
    })()
    const grant = state.host.approve(intent.id, true, state.sender)!
    await state.host.create({ ...state.request, approval: grant }, state.sender)
    const options = state.environment.openTerminal.mock.calls[0]?.[0]
    expect(options?.env?.KEY).toBeUndefined()
    expect(state.driver.write).not.toHaveBeenCalled()
    state.host.shutdown()
  })

  it('shutdown 清掉未消费的 spec', async () => {
    const state = localFixture()
    state.host.setLaunchSpec('tab', { workspaceId: 'workspace', env: {}, argv: ['claude'] })
    state.host.shutdown()
    await state.host.create(state.request, state.sender)
    expect(state.driver.write).not.toHaveBeenCalled()
  })
})
