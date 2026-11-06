/**
 * 第 2 层:运行中的插件工具经 `tool.progress` RPC 推实时卡片 → 转发到内核 `ctx.emit`。
 *
 * 走完整链路:contributedTools 的 execute 登记 liveToolEmits → 插件在 tool.execute
 * 期间调 tool.progress → manager 按 callId 找回 emit、核对 pluginId、消毒卡片 → emit。
 */
import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PluginManager, type PluginRuntime } from '../manager'
import { PLUGIN_TIMEOUT } from '../../../shared/plugin/protocol'
import type { ToolProgress } from '../../../shared/agent/tool'
import type { ToolContext } from '../../kernel/tool/registry'

const MANIFEST = {
  name: 'demo',
  publisher: 'acme',
  displayName: 'Demo',
  description: 'progress fixture',
  version: '1.0.0',
  engines: { nextcowork: '^1.0.0' },
  main: './dist/extension.js',
  activationEvents: ['onTool:make_thing'],
  permissions: [],
  contributes: {
    tools: [{ name: 'make_thing', title: '%t%' }],
    cardViews: [{ viewType: 'task.card', path: './dist/card.html' }]
  }
}

const roots: string[] = []
afterEach(async () => {
  for (const r of roots.splice(0)) await fs.rm(r, { recursive: true, force: true })
})

// 最小 ToolContext:execute 只用到 callId / signal / emit。
function ctx(callId: string, signal: AbortSignal, emit: (p: ToolProgress) => void): ToolContext {
  return { callId, signal, emit, workspaceRoot: '/w', permissionMode: 'default', depth: 0, runId: 'r', host: {} } as unknown as ToolContext
}

async function makeManager(runtime: PluginRuntime, interactive = false): Promise<PluginManager> {
  const root = await fs.mkdtemp(join(tmpdir(), 'ncw-progress-'))
  roots.push(root)
  const dir = join(root, 'acme.demo')
  await fs.mkdir(join(dir, 'dist'), { recursive: true })
  await fs.writeFile(join(dir, 'package.json'), JSON.stringify(MANIFEST))
  await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')
  await fs.writeFile(join(dir, 'dist', 'card.html'), '<!doctype html>')
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
  await manager.setEnabled('acme.demo', true)
  await manager.wake('acme.demo')
  // 激活后插件注册它的工具(真实里由 tools.register RPC 完成)
  await manager.handleRequest('acme.demo', {
    id: 1,
    method: 'tools.register',
    params: { name: 'make_thing', description: 'd', inputSchema: { type: 'object' }, readOnly: true, destructive: false, needsNetwork: false, interactive }
  })
  return manager
}

describe('tool.progress · 实时卡片转发', () => {
  it('运行中推的 declarative 卡片经消毒后到达 ctx.emit', async () => {
    const runtime: PluginRuntime = {
      spawn: async () => {},
      dispose: () => {},
      disposeAll: () => {},
      invoke: async (pluginId, invocation) => {
        if (invocation.kind === 'tool.execute') {
          const callId = (invocation.payload as { callId: string }).callId
          // 插件在执行中推一张卡(含一个越界 tone,应被消毒丢弃)
          await manager.handleRequest(pluginId, {
            id: 9,
            method: 'tool.progress',
            params: { callId, message: '待确认', card: { kind: 'declarative', blocks: [{ type: 'status', label: '待批', tone: 'nope' }] } }
          })
          return { content: [{ text: 'done' }] }
        }
        return {}
      }
    }
    const manager = await makeManager(runtime)
    const reg = manager.contributedTools().find((t) => t.internalId.endsWith('make_thing'))
    expect(reg).toBeDefined()

    const emit = vi.fn()
    const result = await reg!.execute({}, ctx('call-1', new AbortController().signal, emit))

    expect(emit).toHaveBeenCalledTimes(1)
    expect(emit.mock.calls[0]![0]).toEqual({
      callId: 'call-1',
      message: '待确认',
      card: { kind: 'declarative', blocks: [{ type: 'status', label: '待批' }] } // tone 'nope' 被丢
    })
    expect(result.output.content).toBe('done')
  })

  it('★ 别的插件拿这次的 callId 推进度 → 被 pluginId 门挡下,emit 不触发', async () => {
    const runtime: PluginRuntime = {
      spawn: async () => {},
      dispose: () => {},
      disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind === 'tool.execute') {
          const callId = (invocation.payload as { callId: string }).callId
          // 冒充另一个插件 id 推进度
          await manager.handleRequest('evil.plugin', {
            id: 9,
            method: 'tool.progress',
            params: { callId, message: '注入' }
          }).catch(() => undefined)
          return { content: [{ text: 'done' }] }
        }
        return {}
      }
    }
    const manager = await makeManager(runtime)
    const reg = manager.contributedTools().find((t) => t.internalId.endsWith('make_thing'))
    const emit = vi.fn()
    await reg!.execute({}, ctx('call-1', new AbortController().signal, emit))
    expect(emit).not.toHaveBeenCalled()
  })

  it('工具结束后 callId 从 liveToolEmits 撤除 —— 迟到的进度被忽略', async () => {
    let lateCallId = ''
    const runtime: PluginRuntime = {
      spawn: async () => {},
      dispose: () => {},
      disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind === 'tool.execute') {
          lateCallId = (invocation.payload as { callId: string }).callId
          return { content: [{ text: 'done' }] }
        }
        return {}
      }
    }
    const manager = await makeManager(runtime)
    const reg = manager.contributedTools().find((t) => t.internalId.endsWith('make_thing'))
    const emit = vi.fn()
    await reg!.execute({}, ctx('call-late', new AbortController().signal, emit))
    // execute 已返回,liveToolEmits 应已撤除;此刻再推进度必须无效
    await manager.handleRequest('acme.demo', { id: 9, method: 'tool.progress', params: { callId: lateCallId, message: '晚了' } })
    expect(emit).not.toHaveBeenCalled()
  })
})

describe('deliverCardAction · 交互动作回传', () => {
  // 一个会挂起等 tool.action 的交互工具:tool.execute 期间登记 onAction 场景由 runtime 模拟。
  function interactiveRuntime(): { runtime: PluginRuntime; actions: Array<{ callId: string; actionId: string }>; release: () => void } {
    const actions: Array<{ callId: string; actionId: string }> = []
    let releaseExec: (() => void) | undefined
    const runtime: PluginRuntime = {
      spawn: async () => {},
      dispose: () => {},
      disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind === 'tool.execute') {
          // 挂起,直到测试放行(模拟等用户点按钮)
          await new Promise<void>((resolve) => { releaseExec = resolve })
          return { content: [{ text: 'done' }] }
        }
        if (invocation.kind === 'tool.action') {
          const p = invocation.payload as { callId: string; actionId: string }
          actions.push({ callId: p.callId, actionId: p.actionId })
          return {}
        }
        return {}
      }
    }
    return { runtime, actions, release: () => releaseExec?.() }
  }

  it('运行中的工具收到 tool.action(pluginId + callId 匹配)', async () => {
    const { runtime, actions, release } = interactiveRuntime()
    const manager = await makeManager(runtime, true)
    const reg = manager.contributedTools().find((t) => t.internalId.endsWith('make_thing'))
    // 启动工具(不 await,它会挂起)
    const running = reg!.execute({}, ctx('call-x', new AbortController().signal, () => {}))
    // 让 execute 的 invoke 先跑起来登记 liveToolEmits
    await Promise.resolve()
    await manager.deliverCardAction('acme.demo', 'call-x', 'approve')
    expect(actions).toEqual([{ callId: 'call-x', actionId: 'approve' }])
    release()
    await running
  })

  it('★ 别的插件 / 陌生 callId 的动作被 pluginId 门挡下', async () => {
    const { runtime, actions, release } = interactiveRuntime()
    const manager = await makeManager(runtime, true)
    const reg = manager.contributedTools().find((t) => t.internalId.endsWith('make_thing'))
    const running = reg!.execute({}, ctx('call-y', new AbortController().signal, () => {}))
    await Promise.resolve()
    await manager.deliverCardAction('evil.plugin', 'call-y', 'approve') // 冒充插件
    await manager.deliverCardAction('acme.demo', 'no-such-call', 'approve') // 陌生 callId
    expect(actions).toEqual([])
    release()
    await running
  })

  it('交互式工具用放宽后的超时,而不是常规 TOOL_MS', async () => {
    const seen: number[] = []
    const runtime: PluginRuntime = {
      spawn: async () => {},
      dispose: () => {},
      disposeAll: () => {},
      invoke: async (_pluginId, invocation, timeoutMs) => {
        if (invocation.kind === 'tool.execute') seen.push(timeoutMs)
        return { content: [{ text: 'done' }] }
      }
    }
    const manager = await makeManager(runtime, true)
    const reg = manager.contributedTools().find((t) => t.internalId.endsWith('make_thing'))
    await reg!.execute({}, ctx('call-z', new AbortController().signal, () => {}))
    expect(seen).toEqual([PLUGIN_TIMEOUT.INTERACTIVE_TOOL_MS])
    expect(PLUGIN_TIMEOUT.INTERACTIVE_TOOL_MS).toBeGreaterThan(PLUGIN_TIMEOUT.TOOL_MS)
  })
})
