/**
 * 插件 RPC 的**可信作用域 / deadline / 网络预算**。
 *
 * 三组东西,各自的失败形态都是**静默**的:
 *
 * 1. **作用域**:Agent 在后台工作区 A 里跑工具,而用户此刻看着 B。此前工作区类
 *    RPC 一律按「当前聚焦的工作区」定位,于是给 A 画的东西写进了 B。修法是把这类
 *    调用钉在产生它的那次工具调用上(`callId`),并且**绝不回退焦点**。
 * 2. **deadline / 取消**:审批是在等人,而 30 秒的 deadline 到点时旧实现只回一条
 *    timeout —— 迟到的 Allow 照样把命令 spawn 出去。修法是每 RPC 一个 controller
 *    真正接进工作路径,审批之后再复检一次。
 * 3. **网络预算**:重定向逐跳过同一道门(跨 host 拒),正文按**字节**截,
 *    reader 总是清理。
 *
 * 全部用内存替身:不新起进程、不真发请求、不碰用户的工作区。
 */
import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeAll, afterAll, describe, expect, it, vi } from 'vitest'
import { nodeHost, type KernelFs } from '../../kernel/host'
import { nodeFs } from '../../kernel/node-fs'
import { PLUGIN_TIMEOUT } from '../../../shared/plugin/protocol'
import type { ToolContext, ToolRegistration } from '../../kernel/tool/registry'
import { narrowFetchUrl } from '../capabilities'
import { PluginManager, type PluginRuntime } from '../manager'
import type { PluginScmAdapter } from '../rpc'
import type { DocumentCallScope, PluginDocumentsBridge } from '../document-rpc'

const WRITER = {
  name: 'writer',
  publisher: 'acme',
  displayName: 'Writer',
  description: 'scope fixture',
  version: '1.0.0',
  engines: { nextcowork: '^1.0.0' },
  main: './dist/extension.js',
  activationEvents: ['onTool:edit_doc'],
  permissions: ['workspace.read', 'workspace.write', 'process', 'net', 'scm.read'],
  optionalPermissions: ['storage'],
  allowedCommands: ['git'],
  hostPermissions: ['https://api.example.com/*', 'https://fc2.com/*', 'https://fdroid.org/*'],
  contributes: { tools: [{ name: 'edit_doc', title: '%t%' }] }
}

const EVIL = { ...WRITER, name: 'evil', displayName: 'Evil' }

const roots: string[] = []
const managers: PluginManager[] = []
afterEach(async () => {
  vi.useRealTimers()
  for (const manager of managers.splice(0)) await manager.shutdown().catch(() => undefined)
  for (const root of roots.splice(0)) await fs.rm(root, { recursive: true, force: true })
})

/**
 * 这些用例**不用假时钟**。
 *
 * ★ 原因是 `manager.start()` 里那个 `setInterval(sweepIdle, 60_000)` 会在假时钟下
 * 被一次 `advanceTimersByTime` 点燃,于是一个刚跑完的工具调用被判「空闲 5 分钟」
 * 而休眠 —— 症状是后面几条断言随机变红。取消这条路用**真实**时间验证(禁用插件),
 * deadline 那一条单独开假时钟、并且显式把真实时钟恢复回去。
 */
beforeAll(() => { vi.useRealTimers() })
afterAll(() => { vi.useRealTimers() })

/**
 * 一个工具调用的上下文。
 *
 * ★ 返回 `controller`,而 `signal` 是它自己那条 signal ——「用户点停止」这条路
 * (工具 signal → 已跑的 spawn/fetch 真被 abort)只有拿得到这个 controller 才验得了。
 * 调用方不关心取消时照样只读 `.ctx.signal`,把它当普通 ToolContext 用。
 */
function ctx(callId: string, workspaceId: string | undefined): { ctx: ToolContext; controller: AbortController } {
  const controller = new AbortController()
  return {
    controller,
    ctx: {
      callId,
      signal: controller.signal,
      emit: () => {},
      workspaceRoot: '/w',
      permissionMode: 'default',
      depth: 0,
      runId: 'r',
      host: {},
      ...(workspaceId === undefined ? {} : { workspaceId })
    } as unknown as ToolContext
  }
}

const signalOf = (callId: string, workspaceId: string | undefined): ToolContext => ctx(callId, workspaceId).ctx

/**
 * 这一个文件里的响应信封叫 `RpcResponse` —— **不要**叫 `Response`。
 * 那个名字会遮蔽全局的 `Response`,于是 `new Response('ok')` 与 `typeof fetch`
 * 都在一个看起来毫不相干的位置报类型错。
 */
interface RpcResponse { ok: boolean; error?: { code: string; message: string }; data?: unknown }
type InvokeFn = (pluginId: string, invocation: { kind: string; payload: unknown }, tool: { callId: string }) => Promise<unknown>

interface World {
  manager: PluginManager
  /** A 的根 —— 「后台那次工具调用」的工作区 */
  rootA: string
  /** B 的根 —— 用户此刻**聚焦**的那个 */
  rootB: string
  fetch: ReturnType<typeof vi.fn>
  spawn: ReturnType<typeof vi.fn>
  responses: RpcResponse[]
}

async function makeWorld(options: {
  /** 用户此刻聚焦的工作区。默认 B —— 这正是那个 bug 的场景。 */
  focused?: 'A' | 'B'
  /** 哪些 id 能解析成本地工作区。默认 A、B 都在。 */
  resolvable?: readonly string[]
  approve?: () => Promise<boolean>
  spawn?: ReturnType<typeof vi.fn>
  fetch?: ReturnType<typeof vi.fn>
  invoke?: InvokeFn
  /** 文件系统的包装 —— `writeFile` 生效之后的取消要靠它把文件真正落盘再观察。 */
  fs?: KernelFs
  /** 按 workspaceId 造的 scm 适配器;给了才注入(记录它收到的是哪个 id)。 */
  scmFor?: (workspaceId: string) => PluginScmAdapter
  /** 文档桥;给了才注入(用来验 documents.* 的排队 / deadline)。 */
  documents?: Partial<PluginDocumentsBridge>
} = {}): Promise<World> {
  const base = await fs.mkdtemp(join(tmpdir(), 'ncw-rpc-scope-'))
  roots.push(base)
  const pluginRoot = join(base, 'plugins')
  const rootA = join(base, 'ws-a')
  const rootB = join(base, 'ws-b')
  await fs.mkdir(join(rootA, 'nested'), { recursive: true })
  await fs.mkdir(rootB, { recursive: true })
  const rootsById: Record<string, string> = { A: rootA, B: rootB }

  for (const manifest of [WRITER, EVIL]) {
    const dir = join(pluginRoot, `acme.${manifest.name}`)
    await fs.mkdir(join(dir, 'dist'), { recursive: true })
    await fs.writeFile(join(dir, 'package.json'), JSON.stringify(manifest))
    await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')
  }

  const kv = new Map<string, unknown>()
  const responses: RpcResponse[] = []
  const resolvable = options.resolvable ?? ['A', 'B']
  const spawn = options.spawn ?? vi.fn(async () => ({ code: 0, stdout: '', stderr: '' }))
  const fetch = options.fetch ?? vi.fn(async () => new Response('ok', { status: 200 }))
  // ★ `typeof globalThis.fetch` —— 本地的 `fetch` 这个名字遮蔽了全局的那个,
  //   写 `typeof fetch` 得到的是这个 mock 自己的类型(又一次遮蔽)。
  const host = nodeHost({
    fetch: fetch as unknown as typeof globalThis.fetch,
    spawn: spawn as never,
    ...(options.fs === undefined ? {} : { fs: options.fs })
  })

  let manager: PluginManager | null = null
  const runtime: PluginRuntime = {
    spawn: async () => {},
    dispose: () => {},
    disposeAll: () => {},
    invoke: async (pluginId, invocation) => {
      const typed = invocation as { kind: string; payload: unknown }
      const callId = (typed.payload as { callId?: string } | null)?.callId ?? ''
      /*
        ★ `event` 也要交给调用方 —— 流式命令的 `process.exit` 走的就是它。
        只放行 `tool.execute` 的话,那两条用例会在一条**永远收不到事件**的路上
        通过,而它们想验的恰恰是那条事件。
      */
      if (options.invoke !== undefined && (typed.kind === 'tool.execute' || typed.kind === 'event')) {
        return await options.invoke(pluginId, typed, { callId })
      }
      if (typed.kind !== 'tool.execute') return {}
      // 缺省行为:工具执行期间以**这次的 callId** 写一个文件
      responses.push(await manager!.handleRequest(pluginId, {
        id: 1,
        method: 'workspace.writeFile',
        params: { path: `${callId}.txt`, data: callId, callId }
      }) as unknown as RpcResponse)
      return { content: [{ text: 'done' }] }
    }
  }

  manager = new PluginManager({
    host,
    runtime,
    pluginRoot,
    hostVersion: '1.0.0',
    apiVersion: '1.0.0',
    getKv: (key, fallback) => (kv.has(key) ? (kv.get(key) as typeof fallback) : fallback),
    setKv: (key, value) => { kv.set(key, value) },
    currentWorkspace: () => {
      const id = options.focused ?? 'B'
      return { id, rootPath: rootsById[id] as string }
    },
    currentAppearance: () => 'dark' as const,
    approve: options.approve ?? (async () => true),
    trash: async () => {},
    openExternal: async () => {},
    clipboard: { readText: async () => '', writeText: async () => {} },
    scmFor: options.scmFor ?? (() => { throw new Error('scm adapter is not wired in this test') }),
    openTab: () => {},
    launchTerminal: () => ({ opened: false, reason: 'declined' as const }),
    requestInteraction: async () => null,
    emitProgress: () => {},
    emitChanged: () => {},
    publishMessages: () => {},
    unpublishMessages: () => {},
    requestPermissions: async () => true,
    onToolsChanged: () => {},
    reserveName: (id) => id,
    showMessage: () => {},
    openCustomEditor: () => {},
    resolveWorkspace: (id) => (resolvable.includes(id) ? { id, rootPath: rootsById[id] as string } : null),
    ...(options.documents === undefined ? {} : { documents: { releasePlugin: async () => undefined, handle: async () => ({ data: {}, summary: 'doc' }), ...options.documents } })
  })
  await manager.start()
  managers.push(manager)

  for (const manifest of [WRITER, EVIL]) {
    const id = `acme.${manifest.name}`
    manager.grant(id, ['workspace.read', 'workspace.write', 'process', 'net', 'scm.read'])
    await manager.setEnabled(id, true)
    await manager.wake(id)
    await manager.handleRequest(id, {
      id: 1,
      method: 'tools.register',
      params: {
        name: 'edit_doc',
        description: 'd',
        inputSchema: { type: 'object' },
        readOnly: false,
        destructive: false,
        needsNetwork: false,
        interactive: false
      }
    })
  }

  return { manager, rootA, rootB, fetch, spawn, responses }
}

function toolFor(manager: PluginManager, pluginId: string): ToolRegistration {
  const internalId = `plugin__${pluginId.replaceAll('.', '_')}__edit_doc`
  const found = manager.contributedTools().find((item) => item.internalId === internalId)
  if (found === undefined) throw new Error(`tool not registered for ${pluginId}`)
  return found
}

const missing = async (path: string): Promise<void> => {
  await expect(fs.stat(path)).rejects.toThrow()
}

// ─────────────────────────── 作用域 ───────────────────────────

describe('workspace.* 的可信作用域', () => {
  it('★★ 工具调用在 A、用户聚焦 B:带 callId 的写入落在 A,不是 B', async () => {
    const world = await makeWorld()
    await toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-a', 'A'))

    expect(world.responses).toEqual([{ id: 1, ok: true, data: { revision: expect.any(Number) } }])
    expect(await fs.readFile(join(world.rootA, 'call-a.txt'), 'utf8')).toBe('call-a')
    // ★ 这一条就是那个 bug:B 里**不能**出现任何东西
    await missing(join(world.rootB, 'call-a.txt'))
  })

  it('★ 不带 callId 时仍按当前聚焦的工作区 —— 旧语义没有被改掉', async () => {
    const world = await makeWorld()
    const response = await world.manager.handleRequest('acme.writer', {
      id: 1,
      method: 'workspace.writeFile',
      params: { path: 'plain.txt', data: 'plain' }
    })
    expect(response.ok).toBe(true)
    expect(await fs.readFile(join(world.rootB, 'plain.txt'), 'utf8')).toBe('plain')
    await missing(join(world.rootA, 'plain.txt'))
  })

  it('★★ 同一个插件两次并发调用(A 与 B)互不干扰,各写各的工作区', async () => {
    let started = 0
    let releaseAll: () => void = () => {}
    const bothStarted = new Promise<void>((resolve) => { releaseAll = resolve })
    const world = await makeWorld({
      invoke: async (pluginId, _invocation, tool) => {
        started += 1
        if (started === 2) releaseAll()
        // 等两次工具调用都登记好 —— 这样两次 RPC 是真正并发的
        await bothStarted
        world.responses.push(await world.manager.handleRequest(pluginId, {
          id: 1,
          method: 'workspace.writeFile',
          params: { path: `${tool.callId}.txt`, data: tool.callId, callId: tool.callId }
        }) as unknown as RpcResponse)
        return { content: [{ text: 'done' }] }
      }
    })

    await Promise.all([
      toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-a', 'A')),
      toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-b', 'B'))
    ])

    expect(world.responses.every((r) => r.ok)).toBe(true)
    expect(await fs.readFile(join(world.rootA, 'call-a.txt'), 'utf8')).toBe('call-a')
    expect(await fs.readFile(join(world.rootB, 'call-b.txt'), 'utf8')).toBe('call-b')
    await missing(join(world.rootB, 'call-a.txt'))
    await missing(join(world.rootA, 'call-b.txt'))
  })

  it('★ 同一个插件两次并发调用、都不带 callId → 拒绝,不替它猜一个工作区', async () => {
    let started = 0
    let releaseAll: () => void = () => {}
    const bothStarted = new Promise<void>((resolve) => { releaseAll = resolve })
    const world = await makeWorld({
      invoke: async (pluginId) => {
        started += 1
        if (started === 2) releaseAll()
        await bothStarted
        // ★ 显式把 callId 从参数里去掉 —— 这才是「缺 scope」的那条路
        world.responses.push(await world.manager.handleRequest(pluginId, {
          id: 1,
          method: 'workspace.writeFile',
          params: { path: 'ambiguous.txt', data: 'x' }
        }) as unknown as RpcResponse)
        return { content: [{ text: 'done' }] }
      }
    })

    await Promise.all([
      toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-a', 'A')),
      toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-b', 'B'))
    ])

    expect(world.responses).toHaveLength(2)
    for (const response of world.responses) {
      expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
      expect(response.error?.message).toContain('[call_scope]')
    }
    await missing(join(world.rootA, 'ambiguous.txt'))
    await missing(join(world.rootB, 'ambiguous.txt'))
  })

  it('★ 恰好一次在跑、不带 callId → 按那一次推断(插件无从选择)', async () => {
    let release: () => void = () => {}
    const gate = new Promise<void>((resolve) => { release = resolve })
    const world = await makeWorld({
      invoke: async (pluginId) => {
        // 闸门只用来确保 RPC 发生在这次工具调用仍然 live 的时候
        await gate
        world.responses.push(await world.manager.handleRequest(pluginId, {
          id: 1,
          method: 'workspace.stat',
          params: { path: 'nested' }
        }) as unknown as RpcResponse)
        return { content: [{ text: 'done' }] }
      }
    })
    const running = toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-only', 'A'))
    release()
    await running

    // `nested/` 只存在于 A
    expect(world.responses[0]).toMatchObject({ ok: true, data: { kind: 'dir' } })
  })

  it('★ 别的插件借这个 callId 一律拒绝 —— 也不回退焦点', async () => {
    // writer 的工具调用挂着(不返回),evil 在这一次调用里拿 writer 的 callId 说话
    let release: () => void = () => {}
    const gate = new Promise<void>((resolve) => { release = resolve })
    const world = await makeWorld({
      invoke: async (pluginId, _invocation, _tool) => {
        if (pluginId === 'acme.writer') { await gate; return { content: [{ text: 'done' }] } }
        world.responses.push(await world.manager.handleRequest(pluginId, {
          id: 1,
          method: 'workspace.writeFile',
          params: { path: 'stolen.txt', data: 'x', callId: 'writer-call' }
        }) as unknown as RpcResponse)
        return { content: [{ text: 'done' }] }
      }
    })

    const writerRunning = toolFor(world.manager, 'acme.writer').execute({}, signalOf('writer-call', 'A'))
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    // evil 自己的工具调用也在跑(ctx 里带了 B),它拿的是**别人**的 callId
    await toolFor(world.manager, 'acme.evil').execute({}, signalOf('evil-call', 'B'))
    release()
    await writerRunning

    expect(world.responses).toEqual([
      { id: 1, ok: false, error: { code: 'rejected', message: expect.stringContaining('[call_scope]') } }
    ])
    await missing(join(world.rootA, 'stolen.txt'))
    await missing(join(world.rootB, 'stolen.txt'))
  })

  it('★ 已经结束的 callId 拒绝 —— 不回退焦点', async () => {
    const world = await makeWorld()
    await toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-done', 'A'))
    const response = await world.manager.handleRequest('acme.writer', {
      id: 7,
      method: 'workspace.writeFile',
      params: { path: 'late.txt', data: 'x', callId: 'call-done' }
    })
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    await missing(join(world.rootB, 'late.txt'))
    await missing(join(world.rootA, 'late.txt'))
  })

  it('★ 猜一个不存在的 callId → rejected;形状不对 → invalid_argument', async () => {
    const world = await makeWorld()
    const guessed = await world.manager.handleRequest('acme.writer', {
      id: 8,
      method: 'workspace.readFile',
      params: { path: 'a.txt', callId: 'guessed' }
    })
    expect(guessed).toMatchObject({ ok: false, error: { code: 'rejected' } })
    for (const callId of [42, '']) {
      const malformed = await world.manager.handleRequest('acme.writer', {
        id: 9,
        method: 'workspace.readFile',
        params: { path: 'a.txt', callId }
      })
      expect(malformed).toMatchObject({ ok: false, error: { code: 'invalid_argument' } })
    }
  })

  it('★ 工作区是远程 / 已关闭时拒绝 —— 不转去写另一个本地工作区', async () => {
    const world = await makeWorld({
      resolvable: ['B'],
      invoke: async (pluginId, _invocation, tool) => {
        world.responses.push(await world.manager.handleRequest(pluginId, {
          id: 1,
          method: 'workspace.writeFile',
          params: { path: 'remote.txt', data: 'x', callId: tool.callId }
        }) as unknown as RpcResponse)
        return { content: [{ text: 'done' }] }
      }
    })
    await toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-remote', 'A'))

    expect(world.responses[0]).toMatchObject({ ok: false, error: { code: 'invalid_argument' } })
    expect(world.responses[0]?.error?.message).toContain('[unsupported_environment]')
    await missing(join(world.rootB, 'remote.txt'))
  })

  it('★ 单纯是「没有工作区」时也拒绝', async () => {
    const world = await makeWorld({
      invoke: async (pluginId, _invocation, tool) => {
        world.responses.push(await world.manager.handleRequest(pluginId, {
          id: 1,
          method: 'workspace.writeFile',
          params: { path: 'nowhere.txt', data: 'x', callId: tool.callId }
        }) as unknown as RpcResponse)
        return { content: [{ text: 'done' }] }
      }
    })
    // 这次工具调用没有 workspaceId(整体运行没有工作区)
    await toolFor(world.manager, 'acme.writer').execute({}, signalOf('call-nowhere', undefined))
    expect(world.responses[0]).toMatchObject({ ok: false, error: { code: 'invalid_argument' } })
    await missing(join(world.rootB, 'nowhere.txt'))
  })

  it('★ 非工作区类方法上的同名字段不会把它打挂(只在工作区类方法上解析作用域)', async () => {
    const world = await makeWorld()
    const response = await world.manager.handleRequest('acme.writer', {
      id: 10,
      method: 'diagnostics.log',
      params: { level: 'info', message: 'hi', callId: 42 }
    })
    expect(response.ok).toBe(true)
  })
})

describe('global storage is independent of the workspace scope', () => {
  it.each(['remote', 'absent', 'empty'])('%s tool can read global storage without a local workspace', async (kind) => {
    const responses: unknown[] = []
    const world = await makeWorld({
      resolvable: ['B'],
      invoke: async (pluginId) => {
        responses.push(await world.manager.handleRequest(pluginId, { id: 50, method: 'storage.set', params: { scope: 'global', key: 'portable', value: 'saved' } }))
        responses.push(await world.manager.handleRequest(pluginId, { id: 51, method: 'storage.get', params: { scope: 'global', key: 'portable' } }))
        responses.push(await world.manager.handleRequest(pluginId, { id: 52, method: 'storage.keys', params: { scope: 'global' } }))
        return { content: [{ text: 'done' }] }
      }
    })
    world.manager.grant('acme.writer', ['storage'])
    const workspaceId = kind === 'remote' ? 'A' : kind === 'empty' ? '' : undefined
    await toolFor(world.manager, 'acme.writer').execute({}, ctx('global-storage', workspaceId).ctx)
    expect(responses).toEqual([
      { id: 50, ok: true, data: {} },
      { id: 51, ok: true, data: { value: 'saved' } },
      { id: 52, ok: true, data: { keys: ['plugin:acme.writer:global:portable'] } }
    ])
  })

  it('workspace storage still refuses remote or absent local tool scopes', async () => {
    const responses: unknown[] = []
    const world = await makeWorld({ resolvable: ['B'], invoke: async (pluginId) => {
      responses.push(await world.manager.handleRequest(pluginId, { id: 54, method: 'storage.get', params: { scope: 'workspace', key: 'missing' } }))
      return { content: [{ text: 'done' }] }
    } })
    world.manager.grant('acme.writer', ['storage'])
    await toolFor(world.manager, 'acme.writer').execute({}, ctx('storage-remote', 'A').ctx)
    await toolFor(world.manager, 'acme.writer').execute({}, ctx('storage-absent', undefined).ctx)
    expect(responses).toHaveLength(2)
    for (const response of responses) expect(response).toMatchObject({ ok: false, error: { code: 'invalid_argument', message: expect.stringContaining('[unsupported_environment]') } })
  })

  it('concurrent tools do not make an unscoped global storage request ambiguous', async () => {
    let started = 0
    let release: () => void = () => {}
    const both = new Promise<void>((resolve) => { release = resolve })
    const responses: unknown[] = []
    const world = await makeWorld({ invoke: async (pluginId) => {
      if (++started === 2) release()
      await both
      responses.push(await world.manager.handleRequest(pluginId, { id: 53, method: 'storage.get', params: { scope: 'global', key: 'missing' } }))
      return { content: [{ text: 'done' }] }
    } })
    world.manager.grant('acme.writer', ['storage'])
    await Promise.all([
      toolFor(world.manager, 'acme.writer').execute({}, ctx('global-first', 'A').ctx),
      toolFor(world.manager, 'acme.writer').execute({}, ctx('global-second', 'B').ctx)
    ])
    expect(responses).toEqual([
      { id: 53, ok: true, data: { value: null } }, { id: 53, ok: true, data: { value: null } }
    ])
  })
})

// ─────────────────────── deadline / 取消 ───────────────────────

describe('deadline 与取消', () => {
  it('★★ 审批迟到(插件已被禁用)→ 不 spawn', async () => {
    let resolveApproval: (value: boolean) => void = () => {}
    const world = await makeWorld({
      approve: () => new Promise<boolean>((resolve) => { resolveApproval = resolve })
    })
    const pending = world.manager.handleRequest('acme.writer', {
      id: 11,
      method: 'process.exec',
      params: { command: 'git', args: ['status'] }
    })
    // 等审批真的被问到
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    // 用户在这期间把插件禁用了(关插件 / 撤权走的就是这条路)
    await world.manager.setEnabled('acme.writer', false)
    // 迟到的「允许」
    resolveApproval(true)
    const response = await pending

    expect(response.ok, JSON.stringify(response)).toBe(false)
    expect(world.spawn).not.toHaveBeenCalled()
    /*
      ★★ **这是一次「被取消」,不是「超时」,更不是「结果未知」。**

      ★ 错误码上「取消」与「超时」必须分得开:同一个原因(用户关了插件)会因为
      「abort 在哪一步暴露出来」而给出两种不同的码 —— 插件作者按其中一种写处理
      逻辑,另一种就会落空。

      ★★ 而 `[result_unknown]` **绝不能出现**:审批是在等人,命令**一个字都没跑**。
      报「结果未知」会让插件把一个从没发生过的副作用当成可能已经做了 —— 这正是
      「用静态的方法表判副作用」那套的错处:它答不了「这一次走到哪了」。
    */
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    if (response.ok) throw new Error('expected a failed response')
    expect(response.error.message).not.toContain('[result_unknown]')
    expect(response.error.message).not.toContain('timed out')
  })

  it('★ deadline 到点 → 迟到批准不 spawn(用假时钟推进 30 秒)', async () => {
    let asked = false
    let resolveApproval: (value: boolean) => void = () => {}
    const world = await makeWorld({
      approve: () => { asked = true; return new Promise<boolean>((resolve) => { resolveApproval = resolve }) }
    })
    vi.useFakeTimers()
    try {
      const pending = world.manager.handleRequest('acme.writer', {
        id: 12,
        method: 'process.exec',
        params: { command: 'git', args: ['status'] }
      })
      await vi.advanceTimersByTimeAsync(0)
      expect(asked).toBe(true)
      // 到点:controller 被 abort,而审批还挂着。★ 这里只推进到「刚好过 deadline」,
      // 不让 sweepIdle 那 60 秒的定时器有机会把插件判成空闲休眠。
      await vi.advanceTimersByTimeAsync(PLUGIN_TIMEOUT.REQUEST_MS + 1)
      // 迟到的「允许」
      resolveApproval(true)
      const response = await pending

      expect(world.spawn).not.toHaveBeenCalled()
      expect(response).toMatchObject({ ok: false, error: { code: 'timeout' } })
    } finally {
      vi.useRealTimers()
    }
  })

  it('★★ spawn 真的收到这次调用的 signal,禁用插件时子进程被取消', async () => {
    const seen: AbortSignal[] = []
    const world = await makeWorld({
      spawn: vi.fn((_line: string, opts: { signal: AbortSignal }) => {
        seen.push(opts.signal)
        return new Promise((_resolve, reject) => {
          opts.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
        })
      })
    })
    const pending = world.manager.handleRequest('acme.writer', {
      id: 13,
      method: 'process.exec',
      params: { command: 'git', args: ['fetch'] }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(seen).toHaveLength(1)
    expect(seen[0]?.aborted).toBe(false)

    await world.manager.setEnabled('acme.writer', false)
    const response = await pending
    expect(seen[0]?.aborted).toBe(true)
    // ★ 有副作用的方法被取消之后**不能**报告「可安全重试」
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    if (response.ok) throw new Error('expected a failed response')
    expect(response.error.message).toContain('[result_unknown]')
  })

  it('★ net.fetch 拿到同一个 signal,禁用插件时请求被中止', async () => {
    const signals: AbortSignal[] = []
    const world = await makeWorld({
      fetch: vi.fn((_url: string, init: { signal: AbortSignal }) => {
        signals.push(init.signal)
        return new Promise((_resolve, reject) => {
          init.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
        })
      })
    })
    const pending = world.manager.handleRequest('acme.writer', {
      id: 14,
      method: 'net.fetch',
      params: { url: 'https://api.example.com/v1' }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(signals).toHaveLength(1)
    await world.manager.setEnabled('acme.writer', false)
    const response = await pending
    expect(signals[0]?.aborted).toBe(true)
    expect(response.ok).toBe(false)
    // ★ 只读方法被取消是普通的取消,不带 result_unknown
    if (response.ok) throw new Error('expected a failed response')
    expect(response.error.message).not.toContain('[result_unknown]')
  })

  it('★ 只取消被禁用的那个插件,别的插件的在途调用不受影响', async () => {
    const signals: AbortSignal[] = []
    const world = await makeWorld({
      fetch: vi.fn((_url: string, init: { signal: AbortSignal }) => {
        signals.push(init.signal)
        return new Promise((resolve) => {
          init.signal.addEventListener('abort', () => { resolve(new Response('', { status: 499 })) }, { once: true })
        })
      })
    })
    const writerCall = world.manager.handleRequest('acme.writer', {
      id: 15, method: 'net.fetch', params: { url: 'https://api.example.com/v1' }
    })
    const evilCall = world.manager.handleRequest('acme.evil', {
      id: 16, method: 'net.fetch', params: { url: 'https://api.example.com/v2' }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(signals).toHaveLength(2)

    await world.manager.setEnabled('acme.writer', false)
    await writerCall
    expect(signals[0]?.aborted).toBe(true)
    // evil 的那一条不受影响,仍在等它自己的响应
    expect(signals[1]?.aborted).toBe(false)
    // 收尾:放它一条响应,免得测试挂在那儿
    await world.manager.setEnabled('acme.evil', false)
    await evilCall
  })

  it('★ 撤权之后新调用被拒', async () => {
    const world = await makeWorld()
    world.manager.revoke('acme.writer', ['workspace.write'])
    const response = await world.manager.handleRequest('acme.writer', {
      id: 17,
      method: 'workspace.writeFile',
      params: { path: 'after-revoke.txt', data: 'x' }
    })
    expect(response.ok).toBe(false)
    await missing(join(world.rootB, 'after-revoke.txt'))
  })

  it('★★ 禁用插件时它正在跑的**流式命令**一起被取消,并收到一条 exit', async () => {
    const signals: AbortSignal[] = []
    const events: Array<{ event: string; execId?: string; timedOut?: boolean }> = []
    const world = await makeWorld({
      spawn: vi.fn((_line: string, opts: { signal: AbortSignal }) => {
        signals.push(opts.signal)
        return new Promise((_resolve, reject) => {
          opts.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
        })
      }),
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind === 'event') events.push(invocation.payload as { event: string })
        return {}
      }
    })
    const started = await world.manager.handleRequest('acme.writer', {
      id: 18,
      method: 'process.execStream',
      params: { command: 'git', args: ['status'] }
    })
    expect(started).toMatchObject({ ok: true })
    expect(signals).toHaveLength(1)

    await world.manager.setEnabled('acme.writer', false)
    // `finishExec` 的 exit 是**即发即忘**的(`void runtime.invoke`),断言前让它跑完
    await new Promise<void>((resolve) => { setImmediate(resolve) })

    expect(signals[0]?.aborted).toBe(true)
    // ★ 插件那边 `await handle.done` 必须被解开 —— 一条 124 / timedOut 的 exit
    expect(events).toContainEqual({ event: 'process.exit', execId: 'exec-1', code: 124, timedOut: true })
    // 而且它**不再**能被重复 abort(表已经收干净了)
    const after = await world.manager.handleRequest('acme.writer', {
      id: 19,
      method: 'process.execAbort',
      params: { execId: 'exec-1' }
    })
    // 插件已禁用 → 能力门先拒;关键是它没有把别的命令掐掉
    expect(after.ok).toBe(false)
  })

  it('★ 流式命令自己的 execAbort 仍然有效 —— 没有破坏既有机制', async () => {
    const signals: AbortSignal[] = []
    const events: Array<{ event: string; execId?: string; code?: number }> = []
    const world = await makeWorld({
      spawn: vi.fn((_line: string, opts: { signal: AbortSignal }) => {
        signals.push(opts.signal)
        return new Promise((_resolve, reject) => {
          opts.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
        })
      }),
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind === 'event') events.push(invocation.payload as { event: string })
        return {}
      }
    })
    await world.manager.handleRequest('acme.writer', {
      id: 20,
      method: 'process.execStream',
      params: { command: 'git', args: ['status'] }
    })
    const aborted = await world.manager.handleRequest('acme.writer', {
      id: 21,
      method: 'process.execAbort',
      params: { execId: 'exec-1' }
    })
    expect(aborted.ok).toBe(true)
    expect(signals[0]?.aborted).toBe(true)
    // 同上:exit 是即发即忘的
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(events).toContainEqual({ event: 'process.exit', execId: 'exec-1', code: 124, timedOut: true })
  })

  it('★★ 流式命令的生命期独立于启动它的那条 RPC 的 30 秒期限', async () => {
    let rejectSpawn: (error: Error) => void = () => {}
    const world = await makeWorld({
      spawn: vi.fn((_line: string, opts: { signal: AbortSignal }) => new Promise((_resolve, reject) => {
        rejectSpawn = reject
        opts.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
      }))
    })
    vi.useFakeTimers()
    try {
      const started = await world.manager.handleRequest('acme.writer', {
        id: 22,
        method: 'process.execStream',
        params: { command: 'git', args: ['status'] }
      })
      expect(started).toMatchObject({ ok: true })
      // 把启动它的那条 RPC 的期限放过去 —— 命令**不该**被这条期限截断。
      await vi.advanceTimersByTimeAsync(PLUGIN_TIMEOUT.REQUEST_MS + 1000)
      expect(world.spawn).toHaveBeenCalledTimes(1)
      const opts = (world.spawn.mock.calls[0] as unknown[])[1] as { signal: AbortSignal }
      expect(opts.signal.aborted).toBe(false)
      // 命令自己结束(或超时)时照常收尾 —— 这里手工让它 resolve,验证没有别的取消源。
      rejectSpawn(new Error('done'))
      await vi.advanceTimersByTimeAsync(0)
    } finally {
      vi.useRealTimers()
    }
  })

  it('★★ 运行工具 A 发起的 spawn / fetch 跟着工具 A 的 abort,而且只跟着 A', async () => {
    const seen: AbortSignal[] = []
    const startedTool: string[] = []
    const world = await makeWorld({
      spawn: vi.fn((_line: string, opts: { signal: AbortSignal }) => {
        seen.push(opts.signal)
        return new Promise((_resolve, reject) => {
          opts.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
        })
      })
    })
    const a = ctx('call-a', 'A')
    const b = ctx('call-b', 'B')
    const runA = toolFor(world.manager, 'acme.writer').execute({}, a.ctx)
    const runB = toolFor(world.manager, 'acme.writer').execute({}, b.ctx)
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(startedTool).toHaveLength(0)

    // 两次工具调用各自发一条 process.exec —— 它们并发,所以都必须带 callId。
    const aCall = world.manager.handleRequest('acme.writer', {
      id: 23, method: 'process.exec', params: { command: 'git', args: ['fetch'], callId: 'call-a' }
    })
    const bCall = world.manager.handleRequest('acme.writer', {
      id: 24, method: 'process.exec', params: { command: 'git', args: ['fetch'], callId: 'call-b' }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(seen).toHaveLength(2)
    expect(seen[0]?.aborted).toBe(false)
    expect(seen[1]?.aborted).toBe(false)

    // 用户停止工具 A —— A 的子进程必须真的被杀,B 的不许受影响。
    a.controller.abort()
    const aResponse = await aCall
    expect(seen[0]?.aborted).toBe(true)
    expect(seen[1]?.aborted).toBe(false)
    expect(aResponse).toMatchObject({ ok: false, error: { code: 'rejected' } })

    // 收尾:B 的那条也不能永远挂着。
    b.controller.abort()
    const bResponse = await bCall
    expect(seen[1]?.aborted).toBe(true)
    expect(bResponse).toMatchObject({ ok: false, error: { code: 'rejected' } })
    await Promise.all([runA, runB])
  })

  it('★★ 带 callId 的 net.fetch 在工具 abort 时请求真的被中止', async () => {
    const signals: AbortSignal[] = []
    const world = await makeWorld({
      fetch: vi.fn((_url: string, init: { signal: AbortSignal }) => {
        signals.push(init.signal)
        return new Promise((_resolve, reject) => {
          init.signal.addEventListener('abort', () => { reject(new Error('aborted')) }, { once: true })
        })
      })
    })
    const a = ctx('call-a', 'A')
    const running = toolFor(world.manager, 'acme.writer').execute({}, a.ctx)
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    const pending = world.manager.handleRequest('acme.writer', {
      id: 25, method: 'net.fetch', params: { url: 'https://api.example.com/v1', callId: 'call-a' }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    expect(signals).toHaveLength(1)
    expect(signals[0]?.aborted).toBe(false)

    a.controller.abort()
    const response = await pending
    expect(signals[0]?.aborted).toBe(true)
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    await running
  })

  it('★★ 审批挂着的时候 deadline 到点,handleRequest 必须立刻结算(不等那个 await)', async () => {
    let asked = false
    let resolveApproval: (value: boolean) => void = () => {}
    const world = await makeWorld({
      approve: () => { asked = true; return new Promise<boolean>((resolve) => { resolveApproval = resolve }) }
    })
    vi.useFakeTimers()
    try {
      let settled = false
      const pending = world.manager.handleRequest('acme.writer', {
        id: 26, method: 'process.exec', params: { command: 'git', args: ['status'] }
      }).then((value) => { settled = true; return value })
      await vi.advanceTimersByTimeAsync(0)
      expect(asked, '审批应当已经被问到').toBe(true)
      expect(settled).toBe(false)

      /*
        ★ **关键**:审批的那个 promise 此刻仍然挂着(`resolveApproval` 没被调用),
        但 `handleRequest` 必须在期限到点时自己结算 —— 这正是「deadline 必须让
        handleRequest 真正返回」那条要求,而不是「回一条 timeout、审批继续挂着」。
      */
      await vi.advanceTimersByTimeAsync(PLUGIN_TIMEOUT.REQUEST_MS + 1)
      expect(settled, 'deadline 到点后 handleRequest 必须已经结算').toBe(true)
      const response = await pending
      expect(response).toMatchObject({ ok: false, error: { code: 'timeout' } })
      if (response.ok) throw new Error('expected a failed response')
      // 副作用一个字都没发生,所以不是 result_unknown。
      expect(response.error.message).not.toContain('[result_unknown]')

      // 迟到的「允许」**绝不能**把它 spawn 出去。
      resolveApproval(true)
      await vi.advanceTimersByTimeAsync(0)
      expect(world.spawn).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })

  it('★★ 延迟批准(插件被禁用)→ 不是 result_unknown,也不 spawn', async () => {
    let resolveApproval: (value: boolean) => void = () => {}
    const world = await makeWorld({ approve: () => new Promise<boolean>((resolve) => { resolveApproval = resolve }) })
    const pending = world.manager.handleRequest('acme.writer', {
      id: 27, method: 'process.exec', params: { command: 'git', args: ['status'] }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    await world.manager.setEnabled('acme.writer', false)
    resolveApproval(true)
    const response = await pending
    expect(world.spawn).not.toHaveBeenCalled()
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    if (response.ok) throw new Error('expected a failed response')
    // ★ 审批迟到 = 副作用点之前 → 普通取消,不误报「可能已执行」。
    expect(response.error.message).not.toContain('[result_unknown]')
  })

  it('★★ writeFile 已经生效之后插件被禁用 → 必须报 result_unknown,不是成功也不是可重试', async () => {
    /*
      ★ 用一个**真的写盘、但写完之后挂住**的 writeFile:这样我们能在「文件确实
      改了」之后把插件禁用,让 response 走取消分类。写盘用 nodeFs 的真实现,
      挂住只是为了让取消发生在副作用**之后**。
    */
    let fileExists = false
    const gate = new Promise<void>(() => {})
    const fsOverride = {
      ...nodeFs(),
      writeFile: async (absPath: string, content: string): Promise<void> => {
        await nodeFs().writeFile(absPath, content)
        fileExists = true
        return gate
      }
    }
    const world = await makeWorld({ fs: fsOverride })
    const pending = world.manager.handleRequest('acme.writer', {
      id: 28,
      method: 'workspace.writeFile',
      params: { path: 'landed.txt', data: 'landed' }
    })
    // 让 writeFile 真的跑完落盘(它随后挂在 gate 上)
    await vi.waitFor(() => { expect(fileExists).toBe(true) }, { timeout: 2000 })
    await world.manager.setEnabled('acme.writer', false)
    const response = await pending

    // 文件**真的**写了 —— 所以「成功」与「可安全重试」都是假话。
    expect(await fs.readFile(join(world.rootB, 'landed.txt'), 'utf8')).toBe('landed')
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    if (response.ok) throw new Error('expected a failed response')
    expect(response.error.message).toContain('[result_unknown]')
  })

  it('★★ SCM 调用按可信 callId 绑定到工具 A 的工作区,不是用户聚焦的 B', async () => {
    const ids: string[] = []
    const adapter: PluginScmAdapter = {
      status: async () => ({ branch: 'main', staged: [], unstaged: [] }),
      diff: async () => ({ diff: '', binary: false, truncated: false }),
      log: async () => ({ commits: [] }),
      branches: async () => ({ current: 'main', branches: [] }),
      stage: async () => undefined,
      commit: async () => ({ hash: 'x' }),
      createBranch: async () => undefined,
      checkout: async () => undefined
    }
    const world = await makeWorld({
      scmFor: (workspaceId) => { ids.push(workspaceId); return adapter }
    })
    const a = ctx('call-a', 'A')
    const running = toolFor(world.manager, 'acme.writer').execute({}, a.ctx)
    await new Promise<void>((resolve) => { setImmediate(resolve) })

    // callId → A(即使此刻聚焦的是 B)
    const scoped = await world.manager.handleRequest('acme.writer', {
      id: 29, method: 'scm.status', params: { callId: 'call-a' }
    })
    expect(scoped.ok).toBe(true)
    expect(ids).toEqual(['A'])

    a.controller.abort()
    await running
  })

  it('★ documents.* 卡在桥里时,工具取消让 handleRequest 结算,并把这次调用的 signal 传下去', async () => {
    const scopes: DocumentCallScope[] = []
    let releaseBridge: () => void = () => {}
    const bridgeGate = new Promise<void>((resolve) => { releaseBridge = resolve })
    let releaseTool: () => void = () => {}
    const toolGate = new Promise<void>((resolve) => { releaseTool = resolve })
    const world = await makeWorld({
      // 工具调用一直挂着 —— 这样它的 callId 才在 liveToolEmits 里
      invoke: async () => { await toolGate; return { content: [{ text: 'done' }] } },
      documents: {
        handle: async (_pluginId, _manifest, _method, _params, scope) => {
          scopes.push(scope)
          await bridgeGate
          return { data: {}, summary: 'doc' }
        }
      }
    })
    const b = ctx('call-b', 'B')
    const running = toolFor(world.manager, 'acme.writer').execute({}, b.ctx)
    await new Promise<void>((resolve) => { setImmediate(resolve) })

    const pending = world.manager.handleRequest('acme.writer', {
      id: 31, method: 'documents.query', params: { sessionId: 's1', request: {}, callId: 'call-b' }
    })
    await new Promise<void>((resolve) => { setImmediate(resolve) })
    /*
      ★ 这里钉的是**宿主这一侧**的两条:
      1. 桥收到的是**这次调用的** signal(不是别的插件 / 别的调用的);
      2. 桥卡在一个不响应 signal 的 await 上时,工具一停止这条 RPC 也必须结算。
      ★ 「真正动手之前再查一次」那一条的落点在 `document-rpc.ts`(那里才有串行
        队列),由 `document-rpc.test.ts` 的用例钉住 —— 这里的桥是替身,查不了它。
    */
    expect(scopes).toHaveLength(1)
    expect(scopes[0]?.signal?.aborted).toBe(false)

    b.controller.abort()
    const response = await pending
    expect(response.ok).toBe(false)
    expect(scopes[0]?.signal?.aborted).toBe(true)

    releaseBridge()
    releaseTool()
    await running.catch(() => undefined)
  })
})

// ─────────────────────── net.fetch 的预算 ───────────────────────

function streamOf(chunks: Uint8Array[], onCancel?: () => void): ReadableStream<Uint8Array> {
  let index = 0
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (index >= chunks.length) { controller.close(); return }
      controller.enqueue(chunks[index] as Uint8Array)
      index += 1
    },
    cancel() { onCancel?.() }
  })
}

describe('net.fetch · callId 不能旁路取消', () => {
  it('refuses unknown and malformed cancellation callIds before fetching', async () => {
    const world = await makeWorld()
    for (const callId of ['finished-or-guessed', '', 42]) {
      const response = await world.manager.handleRequest('acme.writer', {
        id: 40, method: 'net.fetch', params: { url: 'https://api.example.com/v1', callId }
      })
      expect(response).toMatchObject({ ok: false, error: { code: typeof callId === 'string' && callId !== '' ? 'rejected' : 'invalid_argument' } })
    }
    expect(world.fetch).not.toHaveBeenCalled()
  })
})

describe('net.fetch · 重定向逐跳与正文预算', () => {
  const netFetch = (world: World, url: string): Promise<RpcResponse> =>
    world.manager.handleRequest('acme.writer', { id: 1, method: 'net.fetch', params: { url } }) as unknown as Promise<RpcResponse>

  it('★★ 跨 host 的重定向被拒 —— 远端不能替插件改掉 hostPermissions', async () => {
    const world = await makeWorld({
      fetch: vi.fn(async () => new Response('', { status: 302, headers: { location: 'https://attacker.example/steal' } }))
    })
    const response = await netFetch(world, 'https://api.example.com/v1')
    expect(response).toMatchObject({ ok: false, error: { code: 'invalid_argument' } })
    // 只请求过第一跳
    expect(world.fetch).toHaveBeenCalledTimes(1)
  })

  it('★ 跳到内网字面量的重定向同样被拒', async () => {
    const world = await makeWorld({
      fetch: vi.fn(async () => new Response('', { status: 302, headers: { location: 'https://127.0.0.1/latest' } }))
    })
    const response = await netFetch(world, 'https://api.example.com/v1')
    expect(response).toMatchObject({ ok: false, error: { code: 'invalid_argument' } })
    expect(world.fetch).toHaveBeenCalledTimes(1)
  })

  it('★ 同 host 的重定向最多跟 5 跳,超了拒掉', async () => {
    const world = await makeWorld({
      fetch: vi.fn(async () => new Response('', { status: 302, headers: { location: '/next' } }))
    })
    const response = await netFetch(world, 'https://api.example.com/start')
    expect(response).toMatchObject({ ok: false, error: { code: 'invalid_argument' } })
    expect(response.error?.message).toContain('too many redirects')
    // 第一跳 + 5 次跟随 = 6 次请求
    expect(world.fetch).toHaveBeenCalledTimes(6)
  })

  it('★ 同 host 的普通重定向跟着走,并把最终正文带回来', async () => {
    let call = 0
    const world = await makeWorld({
      fetch: vi.fn(async (url: string) => {
        call += 1
        if (call === 1) return new Response('', { status: 302, headers: { location: '/final' } })
        expect(url).toBe('https://api.example.com/final')
        return new Response('hello', { status: 200 })
      })
    })
    const response = await netFetch(world, 'https://api.example.com/start')
    expect(response).toMatchObject({ ok: true, data: { body: 'hello', status: 200 } })
  })

  it('★ 请求带 redirect: manual —— 不能交给 fetch 自动跟', async () => {
    const seen: Array<RequestInit | undefined> = []
    const world = await makeWorld({
      fetch: vi.fn(async (_url: string, init: RequestInit) => { seen.push(init); return new Response('ok', { status: 200 }) })
    })
    await netFetch(world, 'https://api.example.com/v1')
    expect(seen[0]?.redirect).toBe('manual')
  })

  it('★★ 超大响应按**字节**截断,而且 reader 被清理(不会一直读下去)', async () => {
    const chunk = new Uint8Array(1024)
    let cancelled = false
    let pulls = 0
    const endless = new ReadableStream<Uint8Array>({
      pull(controller) { pulls += 1; controller.enqueue(chunk) },
      cancel() { cancelled = true }
    })
    const world = await makeWorld({ fetch: vi.fn(async () => new Response(endless, { status: 200 })) })
    const response = await netFetch(world, 'https://api.example.com/big')

    expect(response.ok).toBe(true)
    const body = (response.data as { body: string }).body
    expect(body).toContain('(truncated)')
    expect(cancelled).toBe(true)
    // 8MB / 1KB ≈ 8192 次;远小于「无限」
    expect(pulls).toBeLessThan(8300)
  })

  it('★ reader 在正常读完时也被清理(cancel 是幂等的空操作)', async () => {
    const world = await makeWorld({
      fetch: vi.fn(async () => new Response(streamOf([new Uint8Array([104, 105])], () => undefined), { status: 200 }))
    })
    const response = await netFetch(world, 'https://api.example.com/v1')
    expect((response.data as { body: string }).body).toBe('hi')
  })

  it('★ 响应头上的条数与单条长度都有上限', async () => {
    const headers = new Headers()
    for (let i = 0; i < 200; i += 1) headers.set(`x-${String(i)}`, 'v')
    headers.set('x-long', 'y'.repeat(9000))
    const world = await makeWorld({
      fetch: vi.fn(async () => new Response('ok', { status: 200, headers }))
    })
    const response = await netFetch(world, 'https://api.example.com/v1')
    const got = (response.data as { headers: Record<string, string> }).headers
    expect(Object.keys(got).length).toBeLessThanOrEqual(64)
    expect(got['x-long']).toBeUndefined()
  })

  it('★ 非 https、内网、清单外的 URL 仍然在首跳就被拒', async () => {
    const world = await makeWorld()
    for (const url of ['http://api.example.com/x', 'https://127.0.0.1/x', 'https://other.example/x']) {
      const response = await netFetch(world, url)
      expect(response.ok, url).toBe(false)
    }
    expect(world.fetch).not.toHaveBeenCalled()
  })
})

describe('isPrivateHost 的 IPv6 判定', () => {
  const hosts = ['https://fc2.com/*', 'https://fdroid.org/*', 'https://fc00.example/*']

  it('★★ 以 fc/fd 开头的**域名**不再被误判成唯一本地地址', () => {
    expect(narrowFetchUrl(hosts, 'https://fc2.com/a').ok).toBe(true)
    expect(narrowFetchUrl(hosts, 'https://fdroid.org/a').ok).toBe(true)
    expect(narrowFetchUrl(hosts, 'https://fc00.example/a').ok).toBe(true)
  })

  it('★ 真正的 IPv6 唯一本地 / 链路本地 / 环回仍然挡掉', () => {
    for (const url of ['https://[fc00::1]/a', 'https://[fd12:3456::1]/a', 'https://[fe80::1]/a', 'https://[::1]/a']) {
      expect(narrowFetchUrl(hosts, url).ok, url).toBe(false)
    }
  })
})
