/**
 * `documents.*` 在 PluginManager 里的作用域派生(`resolveDocumentScope`)。
 *
 * 需求:Agent 在后台工作区里跑的工具,改的必须是那个工作区的文档,哪怕用户此刻在看
 * 另一个。所以带 callId 的文档调用按**内核给这次工具调用的 workspaceId** 定作用域,
 * 且 callId 必须正在跑、属于这个插件。文档实现用替身,只看它收到了什么作用域。
 */
import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import type { PluginManifest } from '../../../shared/plugin/manifest'
import type { PluginMethod } from '../../../shared/plugin/protocol'
import type { ToolContext } from '../../kernel/tool/registry'
import type { DocumentCallScope, PluginDocumentsBridge } from '../document-rpc'
import { PluginManager, type PluginRuntime } from '../manager'

const MANIFEST = {
  name: 'writer',
  publisher: 'acme',
  displayName: 'Writer',
  description: 'document scope fixture',
  version: '1.0.0',
  engines: { nextcowork: '^1.0.0' },
  main: './dist/extension.js',
  activationEvents: ['onTool:edit_doc'],
  permissions: ['workspace.read', 'workspace.write'],
  contributes: { tools: [{ name: 'edit_doc', title: '%t%' }] }
}

const roots: string[] = []
const managers: PluginManager[] = []
afterEach(async () => {
  for (const manager of managers.splice(0)) await manager.shutdown()
  for (const r of roots.splice(0)) await fs.rm(r, { recursive: true, force: true })
})

interface Seen { method: PluginMethod; scope: DocumentCallScope }

function ctx(callId: string, workspaceId: string | undefined): ToolContext {
  return {
    callId, signal: new AbortController().signal, emit: () => {}, workspaceRoot: '/w', permissionMode: 'default', depth: 0, runId: 'r', host: {},
    ...(workspaceId === undefined ? {} : { workspaceId })
  } as unknown as ToolContext
}

async function makeManager(
  runtime: PluginRuntime,
  seen: Seen[],
  workspaces: Record<string, string> = { bg: '/ws/background' },
  documentOverrides: Partial<PluginDocumentsBridge> = {}
): Promise<PluginManager> {
  const root = await fs.mkdtemp(join(tmpdir(), 'ncw-doc-scope-'))
  roots.push(root)
  const dir = join(root, 'acme.writer')
  await fs.mkdir(join(dir, 'dist'), { recursive: true })
  await fs.writeFile(join(dir, 'package.json'), JSON.stringify(MANIFEST))
  await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')
  const kv = new Map<string, unknown>()
  const documents: PluginDocumentsBridge = {
    handle: async (_pluginId: string, _manifest: PluginManifest, method: PluginMethod, _params: unknown, scope: DocumentCallScope) => {
      seen.push({ method, scope })
      return { data: {}, summary: method }
    },
    releasePlugin: async () => undefined,
    ...documentOverrides
  }
  const manager = new PluginManager({
    host: { logger: { warn() {}, info() {}, error() {} } } as never,
    runtime,
    pluginRoot: root,
    hostVersion: '1.0.0',
    apiVersion: '1.0.0',
    getKv: (key, fallback) => (kv.has(key) ? (kv.get(key) as typeof fallback) : fallback),
    setKv: (key, value) => { kv.set(key, value) },
    currentWorkspace: () => ({ id: 'fg', rootPath: '/ws/foreground' }),
    currentAppearance: () => 'dark' as const,
    approve: async () => true,
    trash: async () => {},
    openExternal: async () => {},
    clipboard: { readText: async () => '', writeText: async () => {} },
    scmFor: () => { throw new Error('scm adapter is not wired in this test') },
    openTab: () => {},
    // 文档作用域用例不启动终端，但依赖契约仍要完整，不能为测试放宽生产接口。
    launchTerminal: () => ({ opened: false, reason: 'declined' }),
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
    documents,
    resolveWorkspace: (id) => (workspaces[id] === undefined ? null : { id, rootPath: workspaces[id] })
  })
  await manager.start()
  managers.push(manager)
  manager.grant('acme.writer', ['workspace.read', 'workspace.write'])
  await manager.setEnabled('acme.writer', true)
  await manager.wake('acme.writer')
  await manager.handleRequest('acme.writer', {
    id: 1,
    method: 'tools.register',
    params: { name: 'edit_doc', description: 'd', inputSchema: { type: 'object' }, readOnly: false, destructive: false, needsNetwork: false, interactive: false }
  })
  return manager
}

/** 工具执行期间,以 `asPlugin` 的身份带着这次的 callId 发一条文档请求;失败信封记进 outcome */
function runtimeCalling(get: () => PluginManager, asPlugin: string, outcome: { error?: unknown }): PluginRuntime {
  return {
    spawn: async () => {},
    dispose: () => {},
    disposeAll: () => {},
    invoke: async (_pluginId, invocation) => {
      if (invocation.kind === 'tool.execute') {
        const callId = (invocation.payload as { callId: string }).callId
        const response = await get().handleRequest(asPlugin, { id: 7, method: 'documents.open', params: { path: 'a.docx', callId } })
        if (!response.ok) outcome.error = response.error
        return { content: [{ text: 'done' }] }
      }
      return {}
    }
  }
}

function tool(manager: PluginManager): NonNullable<ReturnType<PluginManager['contributedTools']>[number]> {
  const reg = manager.contributedTools().find((t) => t.internalId.endsWith('edit_doc'))
  if (reg === undefined) throw new Error('tool not registered')
  return reg
}

describe('documents.* lifecycle', () => {
  it('discards only after explicit host confirmation and holds the close guard afterwards', async () => {
    let dirty = true
    let discards = 0
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => {}, invoke: async () => ({}) }, [], {}, {
      assertCanRelease: async () => { if (dirty) throw new Error('unsaved document') },
      discardAll: async () => { discards += 1; dirty = false }
    })
    try {
      await expect(manager.guardDocumentClose()).rejects.toThrow('unsaved')
      expect(discards).toBe(0)
      const release = await manager.guardDocumentClose(true)
      expect(discards).toBe(1)
      release()
    } finally { dirty = false }
  })

  it('checks only the plugin being replaced and leaves the old installation intact on refusal', async () => {
    let dirty = true
    const checked: (string | undefined)[] = []
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => {}, invoke: async () => ({}) }, [], {}, {
      assertCanRelease: async (id) => { checked.push(id); if (dirty && id === 'acme.writer') throw new Error('unsaved document') }
    })
    const source = await fs.mkdtemp(join(tmpdir(), 'ncw-doc-install-'))
    roots.push(source)
    await fs.mkdir(join(source, 'dist'))
    await fs.writeFile(join(source, 'dist', 'extension.js'), 'export function activate(){}')
    await fs.writeFile(join(source, 'package.json'), JSON.stringify({ ...MANIFEST, name: 'other', activationEvents: [], contributes: {} }))
    try {
      await manager.install(source)
      expect(checked).toEqual(['acme.other'])
      await fs.writeFile(join(source, 'package.json'), JSON.stringify({ ...MANIFEST, version: '1.1.0', activationEvents: [], contributes: {} }))
      await expect(manager.install(source)).rejects.toThrow('unsaved')
      const installed = manager.catalog().plugins.find((item) => item.id === 'acme.writer')
      expect(installed?.manifest.version).toBe('1.0.0')
      const manifest = JSON.parse(await fs.readFile(join(installed!.path, 'package.json'), 'utf8')) as { version: string }
      expect(manifest.version).toBe('1.0.0')
    } finally { dirty = false }
  })

  it('refuses disable and uninstall before deactivation when documents are unsaved', async () => {
    let dirty = true
    const released: string[] = []
    const disposed: string[] = []
    const manager = await makeManager({ spawn: async () => {}, dispose: (id) => { disposed.push(id) }, disposeAll: () => {}, invoke: async () => ({}) }, [], {}, {
      assertCanRelease: async () => { if (dirty) throw new Error('unsaved document') },
      releasePlugin: async (id) => { released.push(id) }
    })
    try {
      await expect(manager.setEnabled('acme.writer', false)).rejects.toThrow('unsaved')
      await expect(manager.uninstall('acme.writer')).rejects.toThrow('unsaved')
      expect(manager.catalog().plugins[0]).toMatchObject({ enabled: true, status: 'active' })
      expect(disposed).toEqual([])
      expect(released).toEqual([])
    } finally { dirty = false }
    await manager.setEnabled('acme.writer', false)
    expect(released).toEqual(['acme.writer'])
    // 既有 deactivate 与 disable 都调用幂等 dispose；这里只确认成功后才发生销毁。
    expect(disposed).toEqual(['acme.writer', 'acme.writer'])
  })

  it('revokes access immediately even when dirty documents refuse ordinary disable', async () => {
    let dirty = true
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => {}, invoke: async () => ({}) }, [], {}, {
      assertCanRelease: async () => { if (dirty) throw new Error('unsaved document') }
    })
    try {
      manager.revoke('acme.writer', ['workspace.write'])
      expect(manager.catalog().plugins[0]?.enabled).toBe(false)
      expect(await manager.handleRequest('acme.writer', { id: 3, method: 'documents.save', params: { sessionId: 's' } })).toMatchObject({ ok: false, error: { code: 'permission_denied' } })
      // 让撤权的异步收尾完成；它不能因为 dirty 拒绝形成未处理的 Promise rejection。
      await new Promise<void>((resolve) => { setImmediate(resolve) })
    } finally { dirty = false }
  })

  it('blocks document RPC while a close guard is held and resumes after a veto', async () => {
    const seen: Seen[] = []
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => {}, invoke: async () => ({}) }, seen)
    const release = await manager.guardDocumentClose()
    const request = { id: 9, method: 'documents.open', params: { path: 'a.docx' } }
    try {
      expect(await manager.handleRequest('acme.writer', request)).toMatchObject({ ok: false, error: { code: 'rejected' } })
      expect(seen).toEqual([])
    } finally { release() }
    expect(await manager.handleRequest('acme.writer', request)).toMatchObject({ ok: true })
  })

  it('keeps the host usable when shutdown is refused for unsaved documents', async () => {
    let dirty = true
    let disposed = false
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => { disposed = true }, invoke: async () => ({}) }, [], {}, {
      assertCanRelease: async () => { if (dirty) throw new Error('unsaved document') }
    })
    try {
      await expect(manager.shutdown()).rejects.toThrow('unsaved')
      expect(disposed).toBe(false)
      expect(await manager.handleRequest('acme.writer', { id: 1, method: 'documents.getState', params: {} })).toMatchObject({ ok: true })
    } finally { dirty = false }
  })
})

describe('documents.* scope', () => {
  it('rejects an aborted call even if its tool has not returned yet', async () => {
    const seen: Seen[] = []
    const controller = new AbortController()
    let response: unknown
    const runtime: PluginRuntime = {
      spawn: async () => {}, dispose: () => {}, disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind !== 'tool.execute') return {}
        controller.abort()
        response = await manager.handleRequest('acme.writer', { id: 8, method: 'documents.open', params: { path: 'a.docx', callId: 'cancelled' } })
        return { content: [{ text: 'done' }] }
      }
    }
    const manager = await makeManager(runtime, seen)
    await tool(manager).execute({}, { ...ctx('cancelled', 'bg'), signal: controller.signal })
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    expect(seen).toEqual([])
  })

  it('notifies only the workspace subscription matching a successful document save', async () => {
    const events: unknown[] = []
    const runtime: PluginRuntime = {
      spawn: async () => {}, dispose: () => {}, disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind === 'event') events.push(invocation.payload)
        if (invocation.kind === 'tool.execute') {
          await manager.handleRequest('acme.writer', { id: 9, method: 'documents.save', params: { sessionId: 's', callId: 'bg-save' } })
          return { content: [{ text: 'done' }] }
        }
        return {}
      }
    }
    const manager = await makeManager(runtime, [], undefined, {
      handle: async () => ({ data: {}, summary: 'saved', changed: { path: 'a.docx', kind: 'modified' } })
    })
    await manager.handleRequest('acme.writer', { id: 1, method: 'workspace.subscribeChanges', params: {} })
    await tool(manager).execute({}, ctx('bg-save', 'bg'))
    expect(events).toEqual([])
    await manager.handleRequest('acme.writer', { id: 2, method: 'documents.save', params: { sessionId: 's' } })
    expect(events).toEqual([{ event: 'workspace.changed', changes: [{ path: 'a.docx', kind: 'modified' }] }])
  })

  it('scopes a tool-call document request to the run\'s workspace, not the foreground one', async () => {
    const seen: Seen[] = []
    const outcome: { error?: unknown } = {}
    let manager: PluginManager | null = null
    manager = await makeManager(runtimeCalling(() => manager!, 'acme.writer', outcome), seen)
    await tool(manager).execute({}, ctx('call-1', 'bg'))
    expect(outcome.error).toBeUndefined()
    // scope 现在还带着这次调用的 signal 与副作用钩子(见 DocumentCallScope),只断言作用域那两个字段。
    expect(seen).toHaveLength(1)
    expect(seen[0]?.method).toBe('documents.open')
    expect(seen[0]?.scope).toMatchObject({ workspaceId: 'bg', workspaceRoot: '/ws/background' })
    expect(seen[0]?.scope.signal).toBeInstanceOf(AbortSignal)
  })

  it('infers the single live tool workspace for document requests without a callId', async () => {
    const seen: Seen[] = []
    let response: unknown
    const runtime: PluginRuntime = {
      spawn: async () => {}, dispose: () => {}, disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind !== 'tool.execute') return {}
        response = await manager.handleRequest('acme.writer', { id: 10, method: 'documents.open', params: { path: 'a.docx' } })
        return { content: [{ text: 'done' }] }
      }
    }
    const manager = await makeManager(runtime, seen)
    await tool(manager).execute({}, ctx('unambiguous-document', 'bg'))
    expect(response).toMatchObject({ ok: true })
    expect(seen[0]?.scope).toMatchObject({ workspaceId: 'bg', workspaceRoot: '/ws/background' })
  })

  it('refuses unscoped document requests during concurrent tools instead of guessing the foreground', async () => {
    const seen: Seen[] = []
    const responses: unknown[] = []
    let started = 0
    let release: () => void = () => {}
    const both = new Promise<void>((resolve) => { release = resolve })
    const runtime: PluginRuntime = {
      spawn: async () => {}, dispose: () => {}, disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind !== 'tool.execute') return {}
        if (++started === 2) release()
        await both
        responses.push(await manager.handleRequest('acme.writer', { id: 11, method: 'documents.open', params: { path: 'a.docx' } }))
        return { content: [{ text: 'done' }] }
      }
    }
    const manager = await makeManager(runtime, seen, { bg: '/ws/background', second: '/ws/second' })
    await Promise.all([tool(manager).execute({}, ctx('document-first', 'bg')), tool(manager).execute({}, ctx('document-second', 'second'))])
    expect(responses).toHaveLength(2)
    for (const response of responses) expect(response).toMatchObject({ ok: false, error: { code: 'rejected', message: expect.stringContaining('[call_scope]') } })
    expect(seen).toEqual([])
  })

  it('does not fall back to the foreground when an inferred tool has been aborted', async () => {
    const seen: Seen[] = []
    const controller = new AbortController()
    let response: unknown
    const runtime: PluginRuntime = {
      spawn: async () => {}, dispose: () => {}, disposeAll: () => {},
      invoke: async (_pluginId, invocation) => {
        if (invocation.kind !== 'tool.execute') return {}
        controller.abort()
        response = await manager.handleRequest('acme.writer', { id: 12, method: 'documents.open', params: { path: 'a.docx' } })
        return { content: [{ text: 'done' }] }
      }
    }
    const manager = await makeManager(runtime, seen)
    await tool(manager).execute({}, { ...ctx('aborted-inferred', 'bg'), signal: controller.signal })
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    expect(seen).toEqual([])
  })

  it('uses the foreground workspace for requests without a callId', async () => {
    const seen: Seen[] = []
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => {}, invoke: async () => ({}) }, seen)
    await manager.handleRequest('acme.writer', { id: 3, method: 'documents.open', params: { path: 'a.docx' } })
    expect(seen[0]?.scope).toMatchObject({ workspaceId: 'fg', workspaceRoot: '/ws/foreground' })
  })

  it('★ rejects another plugin reusing this call\'s callId instead of falling back to the foreground', async () => {
    const seen: Seen[] = []
    const outcome: { error?: unknown } = {}
    let manager: PluginManager | null = null
    manager = await makeManager(runtimeCalling(() => manager!, 'evil.plugin', outcome), seen)
    await tool(manager).execute({}, ctx('call-1', 'bg'))
    expect(seen).toEqual([])
    expect(outcome.error).toBeDefined()
  })

  it('rejects a callId that is no longer running', async () => {
    const seen: Seen[] = []
    const manager = await makeManager({ spawn: async () => {}, dispose: () => {}, disposeAll: () => {}, invoke: async () => ({ content: [{ text: 'ok' }] }) }, seen)
    await tool(manager).execute({}, ctx('call-done', 'bg'))
    const response = await manager.handleRequest('acme.writer', { id: 4, method: 'documents.open', params: { path: 'a.docx', callId: 'call-done' } })
    expect(response).toMatchObject({ ok: false, error: { code: 'rejected' } })
    expect(seen).toEqual([])
  })

  it('refuses tool calls from a remote workspace, which resolveWorkspace does not return', async () => {
    const seen: Seen[] = []
    const outcome: { error?: unknown } = {}
    let manager: PluginManager | null = null
    manager = await makeManager(runtimeCalling(() => manager!, 'acme.writer', outcome), seen, {})
    await tool(manager).execute({}, ctx('call-1', 'remote'))
    expect(seen).toEqual([])
    expect(outcome.error).toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('[unsupported_environment]') })
  })
})
