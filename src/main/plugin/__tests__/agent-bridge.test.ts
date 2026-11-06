/**
 * Agent 侧贡献点 —— 工具桥、拦截器表决、上下文注入。
 *
 * 这三样各守着一条**内核既有的不变式**,而它们一旦被插件破坏,症状都出现在
 * 离原因很远的地方:
 *
 * - 工具名不加前缀 → 两个插件的同名工具互相静默顶掉;
 * - 拦截器能返回 allow → 第三方插件把审批弹窗关掉;
 * - 上下文不包裹不截断 → 模型分不清哪段来自插件,而且一轮几万字。
 */
import { describe, expect, it, vi } from 'vitest'
import { nodeHost } from '../../kernel/host'
import { ToolRegistry } from '../../kernel/tool/registry'
import { pluginToolId, isValidPluginToolName, toolRegistrationFor } from '../tools'
import { PluginManager, type PluginRuntime } from '../manager'

describe('工具桥 · 命名', () => {
  it('★ 加插件前缀 —— 两个插件的同名工具不能互相顶掉', () => {
    const a = pluginToolId('acme.one', 'search')
    const b = pluginToolId('acme.two', 'search')
    expect(a).not.toBe(b)
  })

  it('★ 生成的 internalId 能直接当上游的工具名用', () => {
    // 上游把工具名限制在 ^[a-zA-Z0-9_-]{1,64}$;不合法会换来一个 400,
    // 而那个 400 拒的是**整个请求**,不是只丢掉这一个工具。
    expect(pluginToolId('acme.demo', 'read_diagram')).toMatch(/^[A-Za-z0-9_-]{1,64}$/)
  })

  it('工具名本身受限', () => {
    expect(isValidPluginToolName('read_diagram')).toBe(true)
    expect(isValidPluginToolName('read.diagram')).toBe(false)
    expect(isValidPluginToolName('')).toBe(false)
  })
})

describe('工具桥 · 执行', () => {
  const declaration = {
    name: 'read_diagram',
    description: 'reads a diagram',
    inputSchema: { type: 'object', properties: { path: { type: 'string' } } },
    readOnly: true,
    destructive: false,
    needsNetwork: false
  }

  const ctx = (signal: AbortSignal): Parameters<ReturnType<typeof toolRegistrationFor>['execute']>[1] => ({
    workspaceRoot: '/ws',
    signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {}
  })

  it('注册进注册表之后 source 带着 pluginId —— 成批下线靠它', () => {
    const registry = new ToolRegistry()
    registry.register(toolRegistrationFor('acme.demo', declaration, async () => 'ok'))
    const tool = registry.snapshot()[0]
    expect(tool?.source).toEqual({ kind: 'plugin', pluginId: 'acme.demo' })
  })

  it('★ unregisterBySource 一次摘干净', () => {
    const registry = new ToolRegistry()
    registry.register(toolRegistrationFor('acme.demo', declaration, async () => 'ok'))
    registry.register(toolRegistrationFor('acme.other', declaration, async () => 'ok'))
    registry.unregisterBySource({ kind: 'plugin', pluginId: 'acme.demo' })
    const ids = registry.snapshot().map((t) => t.internalId)
    expect(ids).toHaveLength(1)
    expect(ids[0]).toContain('acme_other')
  })

  it('字符串、content 数组、任意对象都归一得出结果', async () => {
    const controller = new AbortController()
    const run = async (value: unknown): Promise<string> => {
      const registration = toolRegistrationFor('acme.demo', declaration, async () => value)
      const result = await registration.execute({}, ctx(controller.signal))
      return JSON.stringify(result)
    }
    expect(await run('plain')).toContain('plain')
    expect(await run({ content: [{ text: 'a' }, { text: 'b' }] })).toContain('a\\nb')
    expect(await run({ content: [{ text: 'bad' }], isError: true })).toContain('bad')
    expect(await run({ weird: 1 })).toContain('weird')
  })

  it('插件抛错 → 工具失败,run 继续', async () => {
    const controller = new AbortController()
    const registration = toolRegistrationFor('acme.demo', declaration, () => Promise.reject(new Error('boom')))
    const result = await registration.execute({}, ctx(controller.signal))
    expect(JSON.stringify(result)).toContain('boom')
  })

  it('★ 中断原样抛 —— 不能伪装成工具失败,否则模型会在用户叫停后重试', async () => {
    const controller = new AbortController()
    controller.abort()
    const registration = toolRegistrationFor('acme.demo', declaration, () => Promise.reject(new Error('aborted')))
    await expect(registration.execute({}, ctx(controller.signal))).rejects.toThrow('aborted')
  })
})

// ─────────────────────────── 拦截器与上下文 ───────────────────────────

const MANIFEST = {
  name: 'demo',
  publisher: 'acme',
  displayName: 'Demo',
  description: 'demo',
  version: '1.0.0',
  engines: { nextcowork: '^1.0.0' },
  main: './dist/extension.js',
  activationEvents: ['onStartup'],
  permissions: ['agent.intercept', 'agent.context'],
  contributes: { tools: [{ name: 'read_diagram', title: '%tool.read%' }] }
}

async function makeActiveManager(invoke: PluginRuntime['invoke']): Promise<PluginManager> {
  const { promises: fs } = await import('node:fs')
  const { join } = await import('node:path')
  const { tmpdir } = await import('node:os')
  const root = await fs.mkdtemp(join(tmpdir(), 'ncw-plugin-agent-'))
  const dir = join(root, 'acme.demo')
  await fs.mkdir(join(dir, 'dist'), { recursive: true })
  await fs.writeFile(join(dir, 'package.json'), JSON.stringify(MANIFEST))
  await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')

  const kv = new Map<string, unknown>()
  const manager = new PluginManager({
    host: nodeHost(),
    runtime: { spawn: async () => {}, invoke, dispose: () => {}, disposeAll: () => {} },
    pluginRoot: root,
    hostVersion: '1.0.0',
    // 清单声明的是插件 API 版本，不是应用版本(见 shared/plugin/api-version.ts)
    apiVersion: '1.0.0',
    getKv: (key, fallback) => (kv.has(key) ? (kv.get(key) as typeof fallback) : fallback),
    setKv: (key, value) => { kv.set(key, value) },
    currentWorkspace: () => ({ id: 'ws', rootPath: root }),
    currentAppearance: () => 'dark' as const,
    approve: async () => true,
    trash: async () => {},
    openExternal: async () => {},
    clipboard: { readText: async () => '', writeText: async () => {} },
    // 用到 scm 的测试自己换掉它 —— 静默返回空状态会让断言在"没接上"时依然是绿的
    scmFor: () => { throw new Error('scm adapter is not wired in this test') },
    openTab: () => {},
    launchTerminal: () => ({ opened: true }),
    // 没有窗口可问 = 一律取消。用到交互的测试自己换掉它
    requestInteraction: async () => null,
    emitProgress: () => {},
    emitChanged: () => {},
    publishMessages: () => {},
    unpublishMessages: () => {},
    requestPermissions: async () => true,
    onToolsChanged: () => {},
    reserveName: (id) => id,
    showMessage: () => {},
    openCustomEditor: () => {}
  })
  await manager.start()
  manager.grant('acme.demo', ['agent.intercept', 'agent.context'])
  await manager.setEnabled('acme.demo', true)
  await manager.wake('acme.demo')
  await manager.handleRequest('acme.demo', { id: 1, method: 'agent.registerInterceptor', params: {} })
  await manager.handleRequest('acme.demo', { id: 2, method: 'agent.registerContextProvider', params: {} })
  return manager
}

describe('拦截器 · 只能收紧', () => {
  it('deny 传上去', async () => {
    const manager = await makeActiveManager(async (_id, invocation) =>
      invocation.kind === 'interceptor.willInvoke' ? { decision: 'deny', reasonKey: 'plugin.acme.demo.nope' } : {}
    )
    expect(await manager.intercept({ toolName: 'Bash', toolInput: {}, readOnly: false, destructive: true }))
      .toEqual({ deny: 'plugin.acme.demo.nope' })
  })

  it('ask 传上去', async () => {
    const manager = await makeActiveManager(async (_id, invocation) =>
      invocation.kind === 'interceptor.willInvoke' ? { decision: 'ask', reasonKey: 'k' } : {}
    )
    expect(await manager.intercept({ toolName: 'Bash', toolInput: {}, readOnly: false, destructive: true }))
      .toEqual({ ask: true })
  })

  it('★ allow 被当成弃权 —— 插件不能把审批弹窗关掉', async () => {
    const manager = await makeActiveManager(async (_id, invocation) =>
      invocation.kind === 'interceptor.willInvoke' ? { decision: 'allow' } : {}
    )
    expect(await manager.intercept({ toolName: 'Bash', toolInput: {}, readOnly: false, destructive: true }))
      .toEqual({})
  })

  it('★ 超时 = 弃权(fail-open),并留一条诊断', async () => {
    const manager = await makeActiveManager(async (_id, invocation) => {
      if (invocation.kind === 'interceptor.willInvoke') throw new Error('plugin_timeout')
      return {}
    })
    expect(await manager.intercept({ toolName: 'Bash', toolInput: {}, readOnly: false, destructive: true })).toEqual({})
    expect(manager.catalog().plugins[0]?.diagnostics.some((d) => d.message.includes('plugin_timeout'))).toBe(true)
  })
})

describe('上下文注入 · 有上限、强制包裹', () => {
  it('★ 强制包裹,模型能看出这一段来自谁', async () => {
    const manager = await makeActiveManager(async (_id, invocation) =>
      invocation.kind === 'context.provide' ? 'the diagram has 3 nodes' : {}
    )
    const text = await manager.provideContext({ prompt: 'hi' })
    expect(text).toContain('<plugin-context source="acme.demo">')
    expect(text).toContain('the diagram has 3 nodes')
  })

  it('★ 单次注入截断 —— 一个插件塞不满整轮预算', async () => {
    const manager = await makeActiveManager(async (_id, invocation) =>
      invocation.kind === 'context.provide' ? 'x'.repeat(100_000) : {}
    )
    const text = await manager.provideContext({ prompt: 'hi' })
    expect(text.length).toBeLessThan(2500)
  })

  it('抛错 = 这一轮少一段上下文,不是一次失败的 run', async () => {
    const manager = await makeActiveManager(async (_id, invocation) => {
      if (invocation.kind === 'context.provide') throw new Error('boom')
      return {}
    })
    expect(await manager.provideContext({ prompt: 'hi' })).toBe('')
  })
})

describe('工具注册 · 只认清单里声明过的', () => {
  it('★ 清单里没有的工具名注册不进来', async () => {
    const invoke = vi.fn(async () => ({}))
    const manager = await makeActiveManager(invoke)
    await manager.handleRequest('acme.demo', {
      id: 3,
      method: 'tools.register',
      params: { name: 'undeclared', description: 'x', inputSchema: {}, readOnly: true, destructive: false, needsNetwork: false }
    })
    expect(manager.contributedTools()).toHaveLength(0)
  })

  it('声明过的进得来,禁用之后立刻消失', async () => {
    const invoke = vi.fn(async () => ({}))
    const manager = await makeActiveManager(invoke)
    await manager.handleRequest('acme.demo', {
      id: 3,
      method: 'tools.register',
      params: { name: 'read_diagram', description: 'reads', inputSchema: {}, readOnly: true, destructive: false, needsNetwork: false }
    })
    expect(manager.contributedTools()).toHaveLength(1)
    await manager.setEnabled('acme.demo', false)
    expect(manager.contributedTools()).toHaveLength(0)
  })
})
