/**
 * 这一版**刚刚变得可调用**的那几条 RPC,以及零代码插件的运行期不变式。
 *
 * ## 为什么值得单独一个文件
 *
 * 这些方法在此之前的状态是最坏的一种:协议表里有、`.d.ts` 里有、运行期垫片里
 * 也有 —— 唯独**没有 handler**,一调就是 `internal_error: method X has no handler`。
 * 作者照着文档写,得到的却是一条指向不了任何东西的错误。
 *
 * 所以这里每一条断言钉的都是「它真的接上了」,而不是「它的参数校验对不对」——
 * 后者在 `host.test.ts` 那套四道门里已经有了。
 */
import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PluginManager, type PluginManagerDeps, type PluginRuntime } from '../manager'
import type { PluginInvocation } from '../../../shared/plugin/protocol'

const MANIFEST = {
  publisher: 'acme',
  name: 'demo',
  displayName: 'Demo',
  description: 'demo',
  version: '1.0.0',
  engines: { nextcowork: '^1.0.0' },
  main: './dist/extension.js',
  activationEvents: ['onStartup'],
  permissions: ['clipboard', 'scm.read', 'tabs.browser'],
  hostPermissions: ['https://www.bilibili.com/*'],
  contributes: {
    webApps: [{ id: 'home', title: '%app.home%', url: 'https://www.bilibili.com/' }]
  }
}

const roots: string[] = []
afterEach(async () => {
  for (const root of roots.splice(0)) await fs.rm(root, { recursive: true, force: true })
})

function fakeRuntime(): PluginRuntime & { spawned: string[] } {
  const spawned: string[] = []
  return {
    spawned,
    async spawn(plugin) { spawned.push(plugin.id) },
    async invoke(_id: string, _invocation: PluginInvocation) { return {} },
    dispose() {},
    disposeAll() {}
  }
}

async function ready(
  overrides: Record<string, unknown> = {},
  depOverrides: Partial<PluginManagerDeps> = {}
): Promise<{
  manager: PluginManager
  runtime: ReturnType<typeof fakeRuntime>
  openExternal: ReturnType<typeof vi.fn>
  openTab: ReturnType<typeof vi.fn>
  clipboardBox: { text: string }
}> {
  const root = await fs.mkdtemp(join(tmpdir(), 'ncw-plugin-api-'))
  roots.push(root)
  const dir = join(root, 'acme.demo')
  await fs.mkdir(join(dir, 'dist'), { recursive: true })
  await fs.writeFile(join(dir, 'package.json'), JSON.stringify({ ...MANIFEST, ...overrides }))
  await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')

  const kv = new Map<string, unknown>()
  const runtime = fakeRuntime()
  const openExternal = vi.fn(async (_url: string) => {})
  const openTab = vi.fn()
  const clipboardBox = { text: 'existing clipboard' }
  const manager = new PluginManager({
    host: { logger: { warn() {}, info() {}, error() {} } } as never,
    runtime,
    pluginRoot: root,
    hostVersion: '9.9.9',
    apiVersion: '1.0.0',
    getKv: (key, fallback) => (kv.has(key) ? (kv.get(key) as typeof fallback) : fallback),
    setKv: (key, value) => { kv.set(key, value) },
    currentWorkspace: () => ({ id: 'ws', rootPath: root }),
    currentAppearance: () => 'dark' as const,
    approve: async () => true,
    trash: async () => {},
    openExternal,
    clipboard: {
      readText: async () => clipboardBox.text,
      writeText: async (text: string) => { clipboardBox.text = text }
    },
    scmFor: () => ({
      status: async () => ({ branch: 'main', staged: ['a.ts'], unstaged: [] }),
      diff: async () => ({ diff: 'diff --git', binary: false, truncated: false }),
      log: async () => ({ commits: [] }),
      branches: async () => ({ current: 'main', branches: ['main'] }),
      stage: async () => {},
      commit: async () => ({ hash: 'abc' }),
      createBranch: async () => {},
      checkout: async () => {}
    }),
    openTab,
    launchTerminal: () => ({ opened: true }),
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
    ...depOverrides
  })
  await manager.start()
  /*
    ★ 先批能力再启用。反过来的话,一个声明了必选能力的插件会落进
    `pending-approval`(「不批不激活」那道闸),而它此后每一条 RPC 都会以
    「plugin is not enabled」被拒 —— 那是闸门在正常工作,不是接线坏了。
  */
  const declared = (overrides.permissions ?? MANIFEST.permissions) as string[]
  manager.grant('acme.demo', declared as never)
  await manager.setEnabled('acme.demo', true)
  return { manager, runtime, openExternal, openTab, clipboardBox }
}

describe('曾经没有 handler 的那几条', () => {
  it('★ env.openExternal 真的落到宿主,而不是 internal_error', async () => {
    const { manager, openExternal } = await ready()
    const response = await manager.handleRequest('acme.demo', {
      id: 1,
      method: 'env.openExternal',
      params: { url: 'https://www.bilibili.com/' }
    })
    expect(response.ok, JSON.stringify(response)).toBe(true)
    expect(openExternal).toHaveBeenCalledWith('https://www.bilibili.com/')
  })

  it('openExternal 只放行 https —— file:/javascript: 能在本机撬开别的东西', async () => {
    const { manager, openExternal } = await ready()
    for (const url of ['file:///etc/passwd', 'javascript:alert(1)', 'http://example.com/']) {
      const response = await manager.handleRequest('acme.demo', { id: 2, method: 'env.openExternal', params: { url } })
      expect(response.ok, url).toBe(false)
    }
    expect(openExternal).not.toHaveBeenCalled()
  })

  it('★ 剪贴板读写接上了,读到的就是刚写进去的', async () => {
    const { manager } = await ready()
    const write = await manager.handleRequest('acme.demo', {
      id: 3,
      method: 'env.clipboardWrite',
      params: { text: 'hello from a plugin' }
    })
    expect(write.ok).toBe(true)
    const read = await manager.handleRequest('acme.demo', { id: 4, method: 'env.clipboardRead', params: {} })
    expect(read.ok && read.data).toEqual({ text: 'hello from a plugin' })
  })

  it('★ scm.status 接上了(此前 scm.read / scm.write 两条能力零方法)', async () => {
    const { manager } = await ready()
    const response = await manager.handleRequest('acme.demo', { id: 5, method: 'scm.status', params: {} })
    expect(response.ok && response.data).toEqual({ branch: 'main', staged: ['a.ts'], unstaged: [] })
  })

  it('scm 的写类没批准时被能力门拒 —— 读能力不等于写能力', async () => {
    const { manager } = await ready()
    const response = await manager.handleRequest('acme.demo', {
      id: 6,
      method: 'scm.commit',
      params: { message: 'chore: test' }
    })
    expect(response.ok).toBe(false)
    if (!response.ok) expect(response.error.code).toBe('permission_denied')
  })
})

describe('tabs.openWebApp / openBrowser', () => {
  it('★ 打开清单里声明过的网页应用 —— 这就是「插件 = 打开哔哩哔哩」那条路', async () => {
    const { manager, openTab } = await ready()
    const response = await manager.handleRequest('acme.demo', {
      id: 7,
      method: 'tabs.openWebApp',
      params: { webAppId: 'home' }
    })
    expect(response.ok && response.data).toEqual({ opened: true })
    expect(openTab).toHaveBeenCalledWith('acme.demo', {
      kind: 'webapp',
      webAppId: 'home',
      url: 'https://www.bilibili.com/',
      title: '%app.home%',
      open: 'tab'
    })
  })

  it('没声明过的 webAppId 不开,也不抛 —— 插件开不了别人的东西', async () => {
    const { manager, openTab } = await ready()
    const response = await manager.handleRequest('acme.demo', {
      id: 8,
      method: 'tabs.openWebApp',
      params: { webAppId: 'somebody-elses' }
    })
    expect(response.ok && response.data).toEqual({ opened: false })
    expect(openTab).not.toHaveBeenCalled()
  })

  it('★ 动态地址必须命中 hostPermissions —— 能力只说「可以开网页」,没说开哪些', async () => {
    const { manager, openTab } = await ready()
    const denied = await manager.handleRequest('acme.demo', {
      id: 9,
      method: 'tabs.openBrowser',
      params: { url: 'https://evil.example.com/' }
    })
    expect(denied.ok && denied.data).toEqual({ opened: false })
    expect(openTab).not.toHaveBeenCalled()

    const allowed = await manager.handleRequest('acme.demo', {
      id: 10,
      method: 'tabs.openBrowser',
      params: { url: 'https://www.bilibili.com/video/BV1' }
    })
    expect(allowed.ok && allowed.data).toEqual({ opened: true })
    expect(openTab).toHaveBeenCalledTimes(1)
  })

  it('没有 tabs.browser 能力时,动态地址在能力门就被拒', async () => {
    const { manager } = await ready({ permissions: ['clipboard'] })
    const response = await manager.handleRequest('acme.demo', {
      id: 11,
      method: 'tabs.openBrowser',
      params: { url: 'https://www.bilibili.com/' }
    })
    expect(response.ok).toBe(false)
    if (!response.ok) expect(response.error.code).toBe('permission_denied')
  })
})

describe('零代码插件的运行期不变式', () => {
  it('★★ webapp 插件**永不 spawn** —— 它没有代码,起进程只会把它标成 error', async () => {
    const { manager, runtime } = await ready({
      kind: 'webapp',
      main: undefined,
      permissions: [],
      activationEvents: ['onWebApp:home']
    })
    const woke = await manager.wake('acme.demo')
    // 「能用吗」的答案是能 —— 返回 false 会让 openWebApp 以为这个插件坏了
    expect(woke).toBe(true)
    expect(runtime.spawned).toEqual([])
  })

  it('用户点侧边栏入口 → 同一段校验与转发(和插件自己调是同一条路)', async () => {
    const { manager, openTab, runtime } = await ready({
      kind: 'webapp',
      main: undefined,
      permissions: [],
      activationEvents: ['onWebApp:home']
    })
    expect(await manager.openWebApp('acme.demo', 'home')).toBe(true)
    expect(openTab).toHaveBeenCalledTimes(1)
    expect(runtime.spawned).toEqual([])
    // 不存在的入口:返回 false 由调用方翻译成一次失败,而不是抛
    expect(await manager.openWebApp('acme.demo', 'nope')).toBe(false)
  })
})

describe('engines 用的是插件 API 版本', () => {
  it('★ 应用版本(9.9.9)与清单的 ^1.0.0 不匹配,但插件照样装得上', async () => {
    /*
      这条钉的是那个「所有按官方模板写的插件都是红的」的 bug:
      判定一旦回到 `app.getVersion()`,这个用例立刻变红。
    */
    const { manager } = await ready()
    const plugin = manager.catalog().plugins.find((item) => item.id === 'acme.demo')
    expect(plugin?.status).not.toBe('error')
    expect(manager.catalog().apiVersion).toBe('1.0.0')
    expect(manager.catalog().hostVersion).toBe('9.9.9')
  })
})
