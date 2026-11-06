/**
 * 插件宿主的 Electron 实现 —— `PluginRuntime` 的那一半机制。
 *
 * ## ★ 与计划的一处偏离:**每个插件一个隐藏 BrowserWindow**,不是一个窗口 N 个 iframe
 *
 * 计划写的是「一个隐藏窗口 + 每插件一个 `ncw-plugin://<id>` 的 iframe」,
 * 理由是站点隔离会把它们分到不同 renderer 进程。那个理由成立,但同样的隔离
 * 用**独立窗口**也能得到,而且少了三样东西:
 *
 * 1. 宿主页面要在 iframe 与主进程之间转交 MessagePort —— 一段没有业务价值、
 *    却必须完全正确的握手代码;
 * 2. `allow-same-origin` 的取舍要在安全评审里解释(opaque origin 会让
 *    `localStorage` / `IndexedDB` 抛异常);
 * 3. 一个 iframe 崩溃时,宿主页面还活着但那一格已经死了 —— 需要额外的存活探测。
 *
 * 独立窗口里,**进程的生死就是插件的生死**:`render-process-gone` 是一个明确的
 * 信号,`destroy()` 是一个确定的收尾。代价是每个插件多一个 BrowserWindow 的
 * 固定开销,而这条被懒激活 + 空闲 5 分钟休眠压住了(见 `manager.ts`)。
 *
 * ## 身份从哪来
 *
 * `webContents.id → pluginId` 的映射**只在这里建立**,建立的时刻是窗口创建的
 * 那一刻。插件发上来的每一条消息都按 `event.sender.id` 反查身份 ——
 * 报文里有没有 pluginId 字段无关紧要,主进程一个字都不看。
 */
import { BrowserWindow, ipcMain, type IpcMainEvent, type IpcMainInvokeEvent } from 'electron'
import { join } from 'node:path'
import type { PluginInvocation, PluginRequest, PluginResponse } from '../../shared/plugin/protocol'
import type { PluginRuntime } from './manager'
import { setPluginRootResolver, PLUGIN_HOST_PARTITION } from './protocol'

/** 插件窗口用的独立分区 —— 与主窗口的存储完全隔离。值住在 `protocol.ts`。 */
const PARTITION = PLUGIN_HOST_PARTITION

/** preload 只暴露三件事:发请求、收反向调用、报告就绪。 */
const CHANNEL_REQUEST = 'plugin:rpc'
const CHANNEL_INVOKE = 'plugin:invoke'
const CHANNEL_INVOKE_RESULT = 'plugin:invokeResult'
const CHANNEL_READY = 'plugin:ready'

/**
 * 等宿主页面握手的最长时间。
 *
 * 宿主页面只是一个空壳(importmap + 一行 bootstrap),10 秒还没 `plugin:ready`
 * 就说明它起不来了 —— 而这个状态**必须**有个尽头:见 `spawn` 里那段说明,
 * 没有尽头的等待会让插件永久卡在 `activating`。
 */
const READY_TIMEOUT_MS = 10_000

interface HostEntry {
  window: BrowserWindow
  /** 建窗那一刻的 `webContents.id` —— dispose 时窗口可能已经 destroyed,不能再读一遍。 */
  webContentsId: number
  ready: Promise<void>
  resolveReady: () => void
  rejectReady: (error: Error) => void
  /** 反向调用的在途表:invocation id → settle */
  pending: Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void }>
  nextInvocationId: number
}

/**
 * 当前装着的那一份插件 IPC 接线的卸载函数。
 *
 * ★★ 这几条频道在进程里**只能有一份接线**。`ipcMain.handle` 对同一频道注册第二次会直接抛
 * (`Attempted to register a second handler for 'plugin:rpc'`),而切账户正是「关掉插件系统、
 * 再起一个新的 `ElectronPluginRuntime`」—— 旧实例的接线从没人摘,于是登录流程在
 * `prepareAccountSwitch → startPlugins` 这一步整条失败。两条 `ipcMain.on` 不会抛,但每切一次
 * 就叠一层,旧实例的闭包(连同它那张已经失效的 webContents 表)永远挂在监听器上。
 *
 * 新实例装接线时先把上一份摘掉:同一时刻只有一个插件系统在跑,最新的那个才是唯一该应答的。
 */
let uninstallCurrentIpc: (() => void) | null = null

export class ElectronPluginRuntime implements PluginRuntime {
  private readonly hosts = new Map<string, HostEntry>()
  private readonly byWebContents = new Map<number, string>()
  private readonly roots = new Map<string, { root: string; main: string }>()

  constructor(
    private readonly handleRequest: (pluginId: string, request: PluginRequest) => Promise<PluginResponse>,
    private readonly preloadPath: string
  ) {
    setPluginRootResolver((pluginId) => this.roots.get(pluginId))
    this.installIpc()
  }

  private installIpc(): void {
    uninstallCurrentIpc?.()
    ipcMain.handle(CHANNEL_REQUEST, async (event: IpcMainInvokeEvent, raw: unknown): Promise<PluginResponse> => {
      /*
        ★ 身份来自 `event.sender.id`,**不是报文里的任何字段**。
        认不出的发送者一律拒 —— 那意味着一个不是我们创建的窗口在用这条频道。
      */
      const pluginId = this.byWebContents.get(event.sender.id)
      const request = raw as PluginRequest
      const id = typeof request?.id === 'number' ? request.id : 0
      if (pluginId === undefined) {
        return { id, ok: false, error: { code: 'permission_denied', message: 'unrecognised sender' } }
      }
      return this.handleRequest(pluginId, { id, method: String(request?.method), params: request?.params })
    })

    const onReady = (event: IpcMainEvent): void => {
      const pluginId = this.byWebContents.get(event.sender.id)
      if (pluginId === undefined) return
      this.hosts.get(pluginId)?.resolveReady()
    }
    ipcMain.on(CHANNEL_READY, onReady)

    const onInvokeResult = (event: IpcMainEvent, raw: unknown): void => {
      const pluginId = this.byWebContents.get(event.sender.id)
      if (pluginId === undefined) return
      const result = raw as { id: number; ok: boolean; data?: unknown; error?: { message: string } }
      const entry = this.hosts.get(pluginId)
      const settle = entry?.pending.get(result.id)
      if (settle === undefined) return
      entry?.pending.delete(result.id)
      if (result.ok) settle.resolve(result.data)
      else settle.reject(new Error(result.error?.message ?? 'plugin error'))
    }
    ipcMain.on(CHANNEL_INVOKE_RESULT, onInvokeResult)

    const uninstall = (): void => {
      ipcMain.removeHandler(CHANNEL_REQUEST)
      ipcMain.removeListener(CHANNEL_READY, onReady)
      ipcMain.removeListener(CHANNEL_INVOKE_RESULT, onInvokeResult)
      if (uninstallCurrentIpc === uninstall) uninstallCurrentIpc = null
    }
    uninstallCurrentIpc = uninstall
  }

  async spawn(plugin: { id: string; root: string; main: string }): Promise<void> {
    this.dispose(plugin.id)
    this.roots.set(plugin.id, { root: plugin.root, main: plugin.main })

    let resolveReady = (): void => {}
    let rejectReady = (_: Error): void => {}
    const ready = new Promise<void>((resolvePromise, rejectPromise) => {
      resolveReady = resolvePromise
      rejectReady = rejectPromise
    })
    /*
      ★ 挂一个空的 catch,把这个 promise 标成「已处理」。

      没有它的时候:spawn 在 `await ready` **之前**就抛了(最常见是 `loadURL`
      失败 —— 宿主页面被 CSP 拦掉、协议没装、路径不对),而这个 promise 从此
      没人等。之后 `wake` 的 catch 里 `dispose` → `fail` → `rejectReady` 会把它
      reject 掉,Node 直接抛 UnhandledPromiseRejectionWarning —— 那条警告里只有
      「plugin host disposed」,**真正的失败原因一个字都没有**,于是排查方向全错。

      标成已处理不影响正常路径:`await ready` 该抛还是抛。
    */
    void ready.catch(() => undefined)

    const window = new BrowserWindow({
      show: false,
      webPreferences: {
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        /*
          ★ 不设这个,隐藏窗口的 timer 会被 Chromium 降频到大约一分钟一次。
          症状是「插件的定时器好像停了」,而没有任何错误。
        */
        backgroundThrottling: false,
        partition: PARTITION,
        preload: this.preloadPath
      }
    })

    const webContentsId = window.webContents.id
    const entry: HostEntry = {
      window,
      webContentsId,
      ready,
      resolveReady,
      rejectReady,
      pending: new Map(),
      nextInvocationId: 1
    }
    this.hosts.set(plugin.id, entry)
    this.byWebContents.set(webContentsId, plugin.id)

    /*
      ★ 进程没了 = 所有在途调用**立刻 reject**,不是等它们各自超时。
      等超时意味着一个崩掉的插件会让 agent 的工具调用挂满 60 秒。
    */
    window.webContents.on('render-process-gone', () => { this.fail(plugin.id, new Error('plugin process crashed')) })
    window.on('closed', () => { this.fail(plugin.id, new Error('plugin host closed')) })

    // 插件不许自己开窗口,也不许把宿主导航到别处。
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
    window.webContents.on('will-navigate', (event) => { event.preventDefault() })

    await window.loadURL(`${'ncw-plugin'}://${plugin.id}/__host.html`)
    /*
      ★ **必须带超时。** 原来这里是裸的 `await ready`:宿主页面只要没能把
      `plugin:ready` 发出来(脚本被 CSP 拦掉、模块解析失败、页面根本没执行),
      这个 await 就永远不 resolve —— 调用方那一侧表现为「点了没反应」,
      而且 `wake` 卡在 `status = 'activating'`,**之后每一次点击都会被
      「已经在激活」直接放行**,连超时都不会再触发。

      超时值对齐 `PLUGIN_TIMEOUT.ACTIVATE_MS` 的量级:宿主页面只是一个空壳,
      给它 10 秒还没握手就说明它起不来了。
    */
    await Promise.race([
      ready,
      new Promise<never>((_, reject) => {
        setTimeout(() => {
          reject(new Error('plugin host did not report ready; its page could not start'))
        }, READY_TIMEOUT_MS).unref?.()
      })
    ])
  }

  async invoke(pluginId: string, invocation: PluginInvocation, timeoutMs: number): Promise<unknown> {
    const entry = this.hosts.get(pluginId)
    if (entry === undefined || entry.window.isDestroyed()) throw new Error('plugin host is not running')
    const id = entry.nextInvocationId++
    const message: PluginInvocation = { ...invocation, id }
    return new Promise<unknown>((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        entry.pending.delete(id)
        rejectPromise(new Error('plugin_timeout'))
      }, timeoutMs)
      entry.pending.set(id, {
        resolve: (value) => { clearTimeout(timer); resolvePromise(value) },
        reject: (error) => { clearTimeout(timer); rejectPromise(error) }
      })
      entry.window.webContents.send(CHANNEL_INVOKE, message)
    })
  }

  /** 宿主窗口此刻是否真的活着 —— 崩溃后 entry 会残留但窗口已 destroyed。见 manager.wake。 */
  isRunning(pluginId: string): boolean {
    const entry = this.hosts.get(pluginId)
    return entry !== undefined && !entry.window.isDestroyed()
  }

  dispose(pluginId: string): void {
    const entry = this.hosts.get(pluginId)
    if (entry === undefined) return
    this.fail(pluginId, new Error('plugin host disposed'))
    this.byWebContents.delete(entry.webContentsId)
    this.hosts.delete(pluginId)
    /*
      ★ roots **不随 dispose 回收**。它是「包在盘上哪」的静态事实,不是运行态:
      回收的话,一个开着的插件视图在插件休眠(空闲 5 分钟)之后,懒加载的
      chunk 会开始 403 —— 症状是「编辑器开着放着,过一会儿代码高亮/图表
      就加载不出来了」,且零报错。运行期入口(__host.html + __runtime.js)
      即便仍可服务,没有 spawn 注入的 preload 也执行不了任何插件代码,
      不因多留这份路径而多出攻击面;卸载/覆盖安装时目录被替换或删除,
      协议层对不存在的路径本来就走 404。
    */
    if (!entry.window.isDestroyed()) entry.window.destroy()
  }

  disposeAll(): void {
    for (const id of [...this.hosts.keys()]) this.dispose(id)
  }

  /** 把在途调用全部 reject 掉,并让还在等握手的 spawn 立刻失败。 */
  private fail(pluginId: string, error: Error): void {
    const entry = this.hosts.get(pluginId)
    if (entry === undefined) return
    entry.rejectReady(error)
    for (const [, settle] of entry.pending) settle.reject(error)
    entry.pending.clear()
  }
}

/**
 * 插件 preload 的位置。
 *
 * ★ 它是**手写的 .cjs 资源文件**,不是打包产物(理由见那个文件的头)。
 * 所以取法和内置 Skill 那批资源一样:打包后在 `process.resourcesPath` 下,
 * 开发时在仓库的 `resources/` 下。
 */
export function pluginPreloadPath(input: { packaged: boolean; resourcesPath: string; appPath: string }): string {
  return input.packaged
    ? join(input.resourcesPath, PLUGIN_PRELOAD_FILE)
    : join(input.appPath, 'resources', PLUGIN_PRELOAD_FILE)
}

export const PLUGIN_PRELOAD_FILE = 'plugin-preload.cjs'

/**
 * 插件**视图运行时**产物目录(`react-core.js` / `ui.js` / `ui.css` / `view.js`)。
 *
 * 取法与上面那条 preload 完全一样,而且**有意抄它**:这两份是同一类资源 ——
 * 既不进 renderer bundle 也不进 main bundle,由 electron-builder 的
 * `extraResources` 原样拷过去。路径规则一旦分叉,打包后必然是其中一个 404,
 * 而开发时两个都好好的。
 *
 * ★ 产物由 `scripts/build-plugin-runtime.mjs` 生成,**不进版本库**。
 * 目录缺失时协议层对这几个 URL 回 404,插件那边看到的是
 * `import 'nextcowork/ui'` 失败 —— 所以 `dev` / `build` 前都会先跑一次生成。
 */
export function pluginRuntimeDir(input: { packaged: boolean; resourcesPath: string; appPath: string }): string {
  return input.packaged
    ? join(input.resourcesPath, PLUGIN_RUNTIME_DIR)
    : join(input.appPath, 'resources', PLUGIN_RUNTIME_DIR)
}

export const PLUGIN_RUNTIME_DIR = 'plugin-runtime'
