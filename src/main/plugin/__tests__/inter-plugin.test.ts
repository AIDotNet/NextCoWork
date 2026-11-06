/**
 * 第 5 层:插件间通信 —— 导出 API broker + 事件总线 + 依赖准入门。
 *
 * 用两个插件驱动:acme.a(声明依赖 acme.b + `plugins` 能力)与 acme.b(导出 API、
 * 订阅事件)。全程走 manager.handleRequest,核对路由与各道门。
 */
import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { PluginManager, type PluginRuntime } from '../manager'
import type { PluginInvocation } from '../../../shared/plugin/protocol'

const roots: string[] = []
afterEach(async () => {
  for (const r of roots.splice(0)) await fs.rm(r, { recursive: true, force: true })
})

function manifest(over: Record<string, unknown>): Record<string, unknown> {
  return {
    publisher: 'acme',
    displayName: 'x',
    description: 'x',
    version: '1.0.0',
    engines: { nextcowork: '^1.0.0' },
    main: './dist/extension.js',
    permissions: [],
    activationEvents: ['onStartup'],
    ...over
  }
}

/** 记录所有反向调用,并让 api.call 返回一个固定结果。 */
function recordingRuntime(): { runtime: PluginRuntime; calls: Array<{ pluginId: string; invocation: PluginInvocation }> } {
  const calls: Array<{ pluginId: string; invocation: PluginInvocation }> = []
  const runtime: PluginRuntime = {
    spawn: async () => {},
    dispose: () => {},
    disposeAll: () => {},
    invoke: async (pluginId, invocation) => {
      calls.push({ pluginId, invocation })
      if (invocation.kind === 'api.call') return { value: `B:${(invocation.payload as { method: string }).method}` }
      return {}
    }
  }
  return { runtime, calls }
}

async function setup(runtime: PluginRuntime): Promise<PluginManager> {
  const root = await fs.mkdtemp(join(tmpdir(), 'ncw-interplugin-'))
  roots.push(root)
  for (const [id, over] of [
    ['acme.a', { name: 'a', permissions: ['plugins'], dependencies: { 'acme.b': '^1.0.0' } }],
    ['acme.b', { name: 'b', permissions: ['plugins'] }]
  ] as const) {
    const dir = join(root, id)
    await fs.mkdir(join(dir, 'dist'), { recursive: true })
    await fs.writeFile(join(dir, 'package.json'), JSON.stringify(manifest(over)))
    await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')
  }
  const kv = new Map<string, unknown>()
  const manager = new PluginManager({
    host: { logger: { warn() {}, info() {}, error() {} } } as never,
    runtime,
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
  for (const id of ['acme.a', 'acme.b']) {
    manager.grant(id, ['plugins'])
    await manager.setEnabled(id, true)
    await manager.wake(id)
  }
  return manager
}

describe('plugins.invoke · 导出 API broker', () => {
  it('调声明过的依赖 → 转发到目标的 api.call,带回返回值', async () => {
    const { runtime, calls } = recordingRuntime()
    const manager = await setup(runtime)
    calls.length = 0
    const res = await manager.handleRequest('acme.a', {
      id: 1,
      method: 'plugins.invoke',
      params: { target: 'acme.b', method: 'foo', args: [1, 2] }
    })
    expect(res).toMatchObject({ ok: true, data: { value: 'B:foo' } })
    const apiCall = calls.find((c) => c.invocation.kind === 'api.call')
    expect(apiCall?.pluginId).toBe('acme.b')
    expect(apiCall?.invocation.payload).toMatchObject({ method: 'foo', args: [1, 2], from: 'acme.a' })
  })

  it('★ 调未声明为依赖的插件 → 拒绝,不发 api.call', async () => {
    const { runtime, calls } = recordingRuntime()
    const manager = await setup(runtime)
    calls.length = 0
    const res = await manager.handleRequest('acme.a', {
      id: 1,
      method: 'plugins.invoke',
      params: { target: 'acme.b_other', method: 'foo', args: [] }
    })
    expect(res).toMatchObject({ ok: true, data: { value: null } })
    expect(calls.some((c) => c.invocation.kind === 'api.call')).toBe(false)
  })

  it('★ 没有 plugins 能力 → permission_denied', async () => {
    const { runtime } = recordingRuntime()
    const manager = await setup(runtime)
    manager.revoke('acme.a', ['plugins'])
    const res = await manager.handleRequest('acme.a', {
      id: 1,
      method: 'plugins.invoke',
      params: { target: 'acme.b', method: 'foo', args: [] }
    })
    expect(res).toMatchObject({ ok: false })
    if (res.ok) return
    expect(res.error.code).toBe('permission_denied')
  })
})

describe('事件总线', () => {
  it('订阅者收到 emit(不含发出者),禁用后不再收到', async () => {
    const { runtime, calls } = recordingRuntime()
    const manager = await setup(runtime)
    // B 订阅 topic,A 也订阅同一个 topic(用来验证发出者不会收到自己的事件)
    await manager.handleRequest('acme.b', { id: 1, method: 'plugins.subscribeEvent', params: { topic: 'task.updated' } })
    await manager.handleRequest('acme.a', { id: 2, method: 'plugins.subscribeEvent', params: { topic: 'task.updated' } })
    calls.length = 0
    await manager.handleRequest('acme.a', { id: 3, method: 'plugins.emitEvent', params: { topic: 'task.updated', payload: { n: 1 } } })
    const events = calls.filter((c) => c.invocation.kind === 'plugins.event')
    expect(events.map((e) => e.pluginId)).toEqual(['acme.b']) // 只投给 B,不回给发出者 A
    expect(events[0]?.invocation.payload).toMatchObject({ topic: 'task.updated', payload: { n: 1 }, from: 'acme.a' })

    // 禁用 B → 订阅被清 → 再 emit 无投递
    await manager.setEnabled('acme.b', false)
    calls.length = 0
    await manager.handleRequest('acme.a', { id: 4, method: 'plugins.emitEvent', params: { topic: 'task.updated', payload: {} } })
    expect(calls.some((c) => c.invocation.kind === 'plugins.event')).toBe(false)
  })
})
