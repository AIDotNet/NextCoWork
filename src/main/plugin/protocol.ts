/**
 * `ncw-plugin://` 协议 —— 插件包里的文件、宿主页面、API 垫片,全部经这里出。
 *
 * ## 三类响应
 *
 * | 路径 | 内容 | 从哪来 |
 * |---|---|---|
 * | `/__host.html` | 宿主页面 | **生成的**,不在包里 |
 * | `/__runtime.js` | `nextcowork` 模块的实现 | **生成的**,闭包里写死 pluginId |
 * | 其余 | 包内文件 | `<userData>/plugins/<id>/` |
 *
 * ★ 前两个必须是生成的:插件不能提供自己的宿主页面(那等于自己写 CSP),
 * 也不能提供 API 垫片(那等于自己声明自己的身份)。**身份由协议层写死** ——
 * URL 的 host 段就是 pluginId,而 URL 是 Chromium 给的,不是插件传的参数。
 *
 * ## CSP 由 handler **强制注入**
 *
 * 不写在页面的 `<meta>` 里:meta 版 CSP 是页面内容的一部分,而页面内容里
 * 有插件的代码。响应头版本插件改不了。
 *
 * `connect-src` 不给外网是**关键设计,不是疏漏**:它让「插件能访问哪些域名」
 * 变成主进程可校验、可审计、可关闭的一条通道(`ncw.net.fetch`)。
 */
import { net, protocol, session } from 'electron'
import { randomUUID } from 'node:crypto'
import { lstat, realpath } from 'node:fs/promises'
import { isAbsolute, join, relative, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { isNativePackagePath } from '../../shared/plugin/native-component'

export const PLUGIN_SCHEME = 'ncw-plugin'

/**
 * 插件宿主窗口的 session 分区。放这里(而不是 `host-window.ts`)是因为
 * `installPluginProtocol` 也要用 —— 常量住在最低层,谁引谁都不会成环。
 */
export const PLUGIN_HOST_PARTITION = 'persist:plugin-host'

/** 在 `app.whenReady()` **之前**调用 —— 之后调不报错,但 privileges 全丢。 */
export function registerPluginScheme(): void {
  protocol.registerSchemesAsPrivileged([
    {
      scheme: PLUGIN_SCHEME,
      privileges: {
        standard: true,
        secure: true,
        supportFetchAPI: true,
        // 每个插件一个 origin,彼此之间不跨源
        corsEnabled: false,
        stream: true
      }
    }
  ])
}

/**
 * CSP。
 *
 * ★ **`nonce` 是必需的,不是可选加固。** 宿主页面(`/__host.html`)靠两段
 * **内联**脚本启动:importmap 把裸模块名 `nextcowork` 映射到 `/__runtime.js`,
 * 紧随其后的 module 脚本调 `__bootstrap`。而 `script-src 'self'` 只放行
 * **外部**脚本 —— 内联的一律拦掉(importmap 同样受 `script-src` 管)。
 *
 * 症状极难定位:控制台只有一句「Refused to execute inline script」,而主进程
 * 那边表现为「插件点了没反应 / 启用后不自启动」—— `__bootstrap` 从不执行,
 * `bridge.ready()` 从不调用,`spawn()` 里的 `await ready` 就永远不 resolve。
 *
 * 所以给内联脚本发 nonce。**每个响应新生成**,不用固定值:固定值等于换个写法
 * 的 `'unsafe-inline'`,任何能往页面里写内容的人都带得上它。
 */
function csp(nonce: string): string {
  return [
    "default-src 'none'",
    `script-src 'self' 'nonce-${nonce}' 'wasm-unsafe-eval'`,
    "style-src 'self' 'unsafe-inline'",
    "font-src 'self' data:",
    "img-src 'self' data: blob:",
    'media-src blob:',
    'worker-src blob:',
    // ★ 仍然禁外网:所有网络请求必须走 ncw.net.fetch,由主进程逐 URL 校验
    "connect-src 'self' data: blob:",
    `frame-ancestors ${mainWindowOrigin()}`
  ].join('; ')
}

/**
 * 主窗口的**真实** origin —— 给 `frame-ancestors` 用。
 *
 * ★ 这里原来写死的是 `ncw://main`,而那个 scheme **全仓从未注册过**:
 * 主窗口 dev 时是 `ELECTRON_RENDERER_URL`(http://localhost:<port>),
 * 打包后是 `loadFile` 的 `file://`。两者都匹配不上 `ncw://main`,于是
 * **插件视图的 iframe 在两种模式下都被拒**。
 *
 * 症状分两段,而且第二段会把人带偏:先是「Framing 'ncw-plugin://…' violates
 * …」(那是**主窗口**的 CSP 少了 frame-src),把它修好之后才轮到这一条 ——
 * 同样是一片空白,但报的是 frame-ancestors。所以两处必须一起改。
 *
 * 这里返回 origin 而不是写死端口:dev 端口是 vite 分配的,写死等于换台机器就坏。
 *
 * ★ **导出是因为它现在有两个使用者**:插件视图与内置 widget 的外壳
 * (`net/widget-protocol.ts`)。两边的 `frame-ancestors` 必须给出同一个答案 ——
 * 而这个答案依赖 `ELECTRON_RENDERER_URL` 与 `loadFile` 的分支,复制一份必然会
 * 在有人改窗口加载方式时只改一处。剩下那一处症状同样是"一片空白",报的是
 * frame-ancestors,和当初那个 `ncw://main` 的坑一模一样。
 */
export function mainWindowOrigin(): string {
  const devUrl = process.env['ELECTRON_RENDERER_URL']
  if (devUrl !== undefined && devUrl !== '') {
    try {
      return new URL(devUrl).origin
    } catch {
      // 环境变量被写坏了 —— 退到生产那一支,而不是放开成 `*`
    }
  }
  // 打包后主窗口走 loadFile。`file:` 是 scheme-source,匹配 file:// 下的祖先。
  return 'file:'
}

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.wasm': 'application/wasm'
}

function mimeOf(path: string): string {
  const dot = path.lastIndexOf('.')
  return (dot === -1 ? undefined : MIME[path.slice(dot).toLowerCase()]) ?? 'application/octet-stream'
}

function headers(contentType: string, nonce?: string): Headers {
  const out = new Headers()
  out.set('Content-Type', contentType)
  out.set('Content-Security-Policy', csp(nonce ?? randomNonce()))
  // 插件包里的东西不该被别的 origin 嵌进去,也不该被当成下载执行
  out.set('X-Content-Type-Options', 'nosniff')
  out.set('Cache-Control', 'no-store')
  return out
}

/**
 * 一次响应用一次的 nonce。
 *
 * ★ 用 `crypto.randomUUID()` 而不是 Math.random:后者对「猜出下一个值」
 * 不设防,而 nonce 的全部意义就是让**别人写的内容**带不上它。
 */
function randomNonce(): string {
  return randomUUID()
}

/**
 * 宿主页面 —— **一个空壳**。
 *
 * 它只做两件事:把 `nextcowork` 这个裸模块名映射到生成的垫片(import map),
 * 然后动态 import 插件的入口。插件代码里的 `import * as ncw from 'nextcowork'`
 * 因此能解析 —— 和 VS Code 里 `vscode` 被标成 external 是同一套做法。
 *
 * ★ **两段脚本都带 nonce**,而 `nonce` 必须和响应头里那个是**同一个值** ——
 * 分两次生成的话 CSP 依然拦得住它们。见上面 `csp()` 的说明。
 */
function hostHtml(pluginId: string, main: string, nonce: string): string {
  const entry = JSON.stringify(`./${main.replace(/^\.\//, '')}`)
  const attr = `nonce="${nonce}"`
  return `<!doctype html>
<html><head><meta charset="utf-8">
<script ${attr} type="importmap">
{"imports":{"nextcowork":"/__runtime.js"}}
</script>
</head><body>
<script ${attr} type="module">
import { __bootstrap } from '/__runtime.js'
__bootstrap(${entry}, ${JSON.stringify(pluginId)})
</script>
</body></html>`
}

/**
 * `nextcowork` 模块的运行期实现。
 *
 * ★ **pluginId 写在这段生成的代码里**,不是从调用方传进来的参数。
 * 插件可以读它、改自己那份拷贝,但每一条真正出去的消息都由 preload 经
 * 一条固定的 IPC 频道发出,而主进程认的是**发送者的 webContents**,
 * 不是消息里的任何字段。所以伪造身份这件事在这一层根本没有着力点。
 */
function runtimeJs(pluginId: string): string {
  return `const bridge = globalThis.__ncwPluginBridge
let nextId = 1
const pending = new Map()
const handlers = { activate: null, deactivate: null, commands: new Map(), tools: new Map(), appearance: new Set(), interceptor: null, contextProvider: null, workspaceChanges: new Set() }
const toolActions = new Map()
const exposedApiMethods = new Map()
const eventSubscribers = new Map()
/* execId → 这次流式命令登记的回调;exit 到达时连同 done 的 resolver 一起清掉。 */
const execStreams = new Map()
const execExits = new Map()
let api = null

function call(method, params) {
  const id = nextId++
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    bridge.request({ id, method, params: params ?? {} }).then((response) => {
      const entry = pending.get(id)
      if (entry === undefined) return
      pending.delete(id)
      if (response.ok) entry.resolve(response.data)
      else entry.reject(Object.assign(new Error(response.error.message), { code: response.error.code }))
    }, (error) => {
      const entry = pending.get(id)
      if (entry === undefined) return
      pending.delete(id)
      entry.reject(error)
    })
  })
}

export class Disposable {
  constructor(callOnDispose) { this._dispose = callOnDispose }
  static from(...items) { return new Disposable(() => { for (const item of items) item.dispose() }) }
  dispose() { this._dispose?.() }
}

export const version = '1'
export const extensionId = ${JSON.stringify(pluginId)}

export const env = {
  appInfo: () => call('env.appInfo'),
  openExternal: (url) => call('env.openExternal', { url }),
  clipboard: {
    readText: () => call('env.clipboardRead').then((r) => r.text),
    writeText: (text) => call('env.clipboardWrite', { text })
  }
}

export const permissions = {
  contains: (p) => call('permissions.contains', { permissions: Array.isArray(p) ? p : [p] }).then((r) => r.granted),
  request: (p, reasonKey) => call('permissions.request', { permissions: p, reasonKey }).then((r) => r.granted),
  remove: (p) => call('permissions.remove', { permissions: p })
}

export const workspace = {
  folders: (options) => call('workspace.folders', options ?? {}).then((r) => r.folders),
  fs: {
    readFile: (path, encoding, options) => call('workspace.readFile', { path, encoding, ...(options ?? {}) }),
    writeFile: (path, data, options) => call('workspace.writeFile', { path, data, ...(options ?? {}) }),
    delete: (path, options) => call('workspace.deleteFile', { path, ...(options ?? {}) }),
    stat: (path, options) => call('workspace.stat', { path, ...(options ?? {}) })
  },
  findFiles: (glob, limit, options) => call('workspace.findFiles', { glob, limit, ...(options ?? {}) }).then((r) => r.paths),
  /*
    工作区文件变更。**不是文件系统 watcher** —— 只覆盖经由应用发生的变更
    (编辑器保存、Agent 工具写入、插件自己的写入)。外部编辑器、git checkout
    改的文件不会到这里,见协议里 workspace.subscribeChanges 的说明。
  */
  onDidChangeFiles(handler, options) {
    const first = handlers.workspaceChanges.size === 0
    handlers.workspaceChanges.add(handler)
    if (first) void call('workspace.subscribeChanges', { globs: options?.globs })
    return new Disposable(() => {
      handlers.workspaceChanges.delete(handler)
      if (handlers.workspaceChanges.size === 0) void call('workspace.unsubscribeChanges')
    })
  }
}

export const process_ = {
  exec: (command, args, options) => call('process.exec', { command, args: args ?? [], ...(options ?? {}) }),
  /*
    流式跑命令。返回一个**句柄对象**,不是子进程 —— 插件拿到的永远是数据
    (见文件头「剥成数据回传」)。输出经宿主的 event 通道回来,在这里分发给
    这次调用登记的回调。
  */
  execStream(command, args, options) {
    const handlers_ = { onOutput: options?.onOutput, onExit: options?.onExit }
    const started = call('process.execStream', { command, args: args ?? [], cwd: options?.cwd, timeoutMs: options?.timeoutMs, callId: options?.callId })
      .then((r) => { execStreams.set(r.execId, handlers_); return r.execId })
    return {
      /* 还没拿到 execId 就调 abort 是常态(用户点得快)—— 等它到手再发。 */
      abort: () => started.then((execId) => call('process.execAbort', { execId })).then(() => undefined),
      done: started.then((execId) => new Promise((resolve) => { execExits.set(execId, resolve) }))
    }
  }
}
export { process_ as process }

export const net = {
  /*
    init.callId 是**取消凭据**,不是工作区作用域 —— 见 shared/plugin/protocol.ts
    里 net.fetch 的说明:带上它,用户在工具里点停止时已经发出的请求会被真正中断。
    ★ 这段住在模板字符串里,注释里不能出现反引号(见 configuration 那段)。
  */
  fetch: (url, init) => call('net.fetch', { url, ...(init ?? {}) })
}
export const storage = {
  global: { get: (key) => call('storage.get', { scope: 'global', key }).then((r) => r.value), set: (key, value) => call('storage.set', { scope: 'global', key, value }), keys: () => call('storage.keys', { scope: 'global' }).then((r) => r.keys) },
  workspace: { get: (key, options) => call('storage.get', { scope: 'workspace', key, ...(options ?? {}) }).then((r) => r.value), set: (key, value, options) => call('storage.set', { scope: 'workspace', key, value, ...(options ?? {}) }), keys: (options) => call('storage.keys', { scope: 'workspace', ...(options ?? {}) }).then((r) => r.keys) }
}

export const secrets = {
  get: (key) => call('secrets.get', { key }).then((r) => r.value),
  set: (key, value) => call('secrets.set', { key, value })
}

export const window_ = {
  showMessage: (kind, messageKey, params) => call('window.showMessage', { kind, messageKey, params }),
  showQuickPick: (items, placeholderKey) => call('window.showQuickPick', { items, placeholderKey }).then((r) => r.id),
  showInputBox: (options) => call('window.showInputBox', options ?? {}).then((r) => r.value),
  showConfirm: (options) => call('window.showConfirm', options ?? {}).then((r) => r.confirmed),
  /*
    进度条。给的是**包住一段异步工作**的形状,而不是裸的 start/end 两条 ——
    裸的那种一旦中间抛异常,进度条就永远留在状态栏上转,而没有人再去关它。
  */
  async withProgress(options, task) {
    const id = options?.id ?? 'p' + String(nextId++)
    await call('window.progressStart', { id, titleKey: options?.titleKey ?? '' })
    try {
      return await task({
        report: (update) => { void call('window.progressUpdate', { id, ...(update ?? {}) }) }
      })
    } finally {
      await call('window.progressEnd', { id })
    }
  },
  setStatusBarItem: (id, textKey, options) => call('window.setStatusBarItem', { id, textKey, ...(options ?? {}) })
}
export { window_ as window }

export const commands = {
  registerCommand(commandId, handler) {
    handlers.commands.set(commandId, handler)
    void call('commands.register', { commandId })
    return new Disposable(() => { handlers.commands.delete(commandId); void call('commands.unregister', { commandId }) })
  },
  executeCommand: (commandId, args) => call('commands.execute', { commandId, args }).then((r) => r.value)
}

export const tools = {
  registerTool(name, tool) {
    handlers.tools.set(name, tool)
    void call('tools.register', { name, description: tool.description ?? '', inputSchema: tool.inputSchema ?? { type: 'object' }, readOnly: tool.readOnly === true, destructive: tool.destructive === true, needsNetwork: tool.needsNetwork === true, interactive: tool.interactive === true })
    return new Disposable(() => { handlers.tools.delete(name); void call('tools.unregister', { name }) })
  }
}

export const tabs = {
  openCustomEditor: (viewType, path) => call('tabs.openCustomEditor', { viewType, path }),
  openWebApp: (webAppId) => call('tabs.openWebApp', { webAppId }).then((r) => r.opened),
  openBrowser: (url, options) => call('tabs.openBrowser', { url, ...(options ?? {}) }).then((r) => r.opened),
  openTerminal: (options) => call('tabs.openTerminal', options ?? {}).then((r) => ({ opened: r.opened === true, ...(r.reason === undefined ? {} : { reason: r.reason }) }))
}

/*
  配置项。**只读** —— 设置是用户的意思,不是插件的(见 shared 协议里
  「configuration.get」的说明)。

  ★ 这段曾经整个缺席:主进程早就实现了「configuration.get」,d.ts 也声明了
  「ncw.configuration.get()」,但垫片里没有这个命名空间 —— 插件侧拿到的是
  undefined,一调就 TypeError。症状是「读配置这件事在文档里有、在代码里没有」。
  ★ 注释里不写反引号:这段住在模板字符串里,一个反引号就把模板提前闭合,
  整个文件从「能编译」变成「语法错误」,而报错位置在几百行之外。
*/
export const configuration = {
  get: () => call('configuration.get').then((r) => r.values)
}

/*
  自定义编辑器的脏标记。

  ★ 同样曾经缺席,而它的后果更重:宿主的「plugins:confirmClose」靠这张表回答
  「关掉这个 Tab 之前有没有没存的东西」,表永远空 = 关 Tab 从不提示,
  用户的改动静默消失。
*/
export const customEditors = {
  setDirty: (documentId, path, dirty) => call('customEditors.setDirty', { documentId, path, dirty })
}

/*
  Agent 集成。

  ★ 主进程侧的拦截器 / 上下文提供者**早就实现了**(还有测试),但垫片里没有
  「agent」命名空间,也没有处理反向 invocation 的分支 —— 于是「agent.intercept」
  和「agent.context」两条能力在权限表里躺着,而没有任何插件能用到它们。
*/
export const agent = {
  registerToolInterceptor(handler) {
    handlers.interceptor = handler
    void call('agent.registerInterceptor')
    return new Disposable(() => { handlers.interceptor = null })
  },
  registerContextProvider(handler) {
    handlers.contextProvider = handler
    void call('agent.registerContextProvider')
    return new Disposable(() => { handlers.contextProvider = null })
  }
}

export const scm = {
  status: (options) => call('scm.status', options ?? {}),
  diff: (path, options) => call('scm.diff', { path, ...(options ?? {}) }),
  log: (options) => call('scm.log', options ?? {}).then((r) => r.commits),
  branches: (options) => call('scm.branches', options ?? {}),
  stage: (paths, options) => call('scm.stage', { paths, ...(options ?? {}) }),
  commit: (message, options) => call('scm.commit', { message, ...(options ?? {}) }).then((r) => r.hash),
  createBranch: (name, options) => call('scm.createBranch', { name, ...(options ?? {}) }),
  checkout: (name, options) => call('scm.checkout', { name, ...(options ?? {}) })
}

// 需求：已接通的会话 RPC 必须能从 nextcowork 调用；callId 由每次调用显式传入，不能用全局当前工具。
export const documents = {
  open: (params) => call('documents.open', params),
  apply: (params) => call('documents.apply', params),
  save: (params) => call('documents.save', params),
  exportDocument: (params) => call('documents.export', params),
  getState: (params) => call('documents.getState', params),
  query: (params) => call('documents.query', params),
  getOperation: (params) => call('documents.getOperation', params),
  close: (params) => call('documents.close', params)
}

export const plugins = {
  exposeApi(methods) {
    const names = Object.keys(methods ?? {})
    for (const name of names) exposedApiMethods.set(name, methods[name])
    void call('plugins.expose', { methods: [...exposedApiMethods.keys()] })
    return new Disposable(() => {
      for (const name of names) exposedApiMethods.delete(name)
      void call('plugins.expose', { methods: [...exposedApiMethods.keys()] })
    })
  },
  connect(target) {
    return new Proxy({}, {
      get(_t, method) {
        if (typeof method !== 'string') return undefined
        return (...args) => call('plugins.invoke', { target, method, args }).then((r) => r.value)
      }
    })
  },
  events: {
    emit: (topic, payload) => call('plugins.emitEvent', { topic, payload }),
    on(topic, handler) {
      let set = eventSubscribers.get(topic)
      if (set === undefined) { set = new Set(); eventSubscribers.set(topic, set); void call('plugins.subscribeEvent', { topic }) }
      set.add(handler)
      return new Disposable(() => {
        const s = eventSubscribers.get(topic)
        if (s === undefined) return
        s.delete(handler)
        if (s.size === 0) { eventSubscribers.delete(topic); void call('plugins.unsubscribeEvent', { topic }) }
      })
    }
  }
}

export const diagnostics = { log: (level, message) => call('diagnostics.log', { level, message }) }

/**
 * 宿主的深浅色。无需权限 —— 不含用户数据。
 *
 * 只在第一次注册 onDidChange 时向主进程订阅:重复 subscribe 对主进程
 * 是同一笔(它记的是布尔),多发一次 IPC 没有意义。
 *
 * 注意这段在模板字符串里,注释里不能出现反引号 —— 它会把模板提前闭合,
 * 而报错会落在几十行之外、看起来毫不相干的地方。
 */
export const appearance = {
  get: () => call('appearance.get').then((r) => r.appearance),
  onDidChange(handler) {
    const first = handlers.appearance.size === 0
    handlers.appearance.add(handler)
    if (first) void call('appearance.subscribe')
    return new Disposable(() => { handlers.appearance.delete(handler) })
  }
}

export function __bootstrap(entry, id) {
  bridge.onInvoke(async (invocation) => {
    const { kind, payload } = invocation
    if (kind === 'activate') {
      const module = await import(entry)
      api = module
      const context = { extensionId: id, subscriptions: [] }
      globalThis.__ncwContext = context
      await module.activate?.(context)
      return {}
    }
    if (kind === 'deactivate') {
      await api?.deactivate?.()
      for (const item of globalThis.__ncwContext?.subscriptions ?? []) { try { item.dispose?.() } catch {} }
      return {}
    }
    if (kind === 'command.run') {
      const handler = handlers.commands.get(payload.commandId)
      if (handler === undefined) throw new Error('no such command: ' + payload.commandId)
      return { value: await handler(payload.args) }
    }
    if (kind === 'tool.execute') {
      const tool = handlers.tools.get(payload.name)
      if (tool === undefined) throw new Error('no such tool: ' + payload.name)
      const progress = (update) => {
        const u = update ?? {}
        void call('tool.progress', { callId: payload.callId, message: u.message, card: u.card })
      }
      const onAction = (handler) => { toolActions.set(payload.callId, handler) }
      try {
        return await tool.invoke({ input: payload.input, callId: payload.callId, session: payload.session, progress, onAction })
      } finally {
        toolActions.delete(payload.callId)
      }
    }
    if (kind === 'tool.action') {
      const handler = toolActions.get(payload.callId)
      if (handler !== undefined) handler({ actionId: payload.actionId, value: payload.value })
      return {}
    }
    if (kind === 'api.call') {
      const fn = exposedApiMethods.get(payload.method)
      if (fn === undefined) throw new Error('no such api method: ' + payload.method)
      return { value: await fn(...(payload.args ?? [])) }
    }
    /*
      工具拦截器 / 上下文提供者。★ 这三个分支曾经**完全没有** —— 主进程一直在发
      这三种 invocation,而垫片落到最后一行抛「unsupported invocation」,于是
      agent.intercept / agent.context 两条能力对插件作者来说是不存在的。

      没注册处理函数时回一个**空裁决**(而不是抛):宿主那边按「弃权」处理,
      fail-open,一个没实现拦截器的插件不该把所有工具调用堵死。
    */
    if (kind === 'interceptor.willInvoke') {
      if (handlers.interceptor === null) return {}
      return (await handlers.interceptor(payload)) ?? {}
    }
    if (kind === 'interceptor.didInvoke') {
      // didInvoke 是通知型的:插件看一眼结果,回什么都不改变已经发生的事。
      if (handlers.interceptor?.didInvoke !== undefined) { try { await handlers.interceptor.didInvoke(payload) } catch {} }
      return {}
    }
    if (kind === 'context.provide') {
      if (handlers.contextProvider === null) return ''
      return (await handlers.contextProvider(payload)) ?? ''
    }
    if (kind === 'plugins.event') {
      const set = eventSubscribers.get(payload.topic)
      if (set !== undefined) for (const handler of set) { try { handler(payload.payload, payload.from) } catch {} }
      return {}
    }
    /*
      宿主播来的事件。**一个插件的处理函数抛了,不能影响别的处理函数,也不能
      让这次 invocation 变成失败** —— 事件是通知,不是请求,没有「失败」这个
      语义可以回给宿主。所以逐个 try。
    */
    if (kind === 'event') {
      if (payload.event === 'appearance.changed') {
        for (const handler of handlers.appearance) {
          try { handler(payload.appearance) } catch {}
        }
      }
      if (payload.event === 'workspace.changed') {
        for (const handler of handlers.workspaceChanges) {
          try { handler(payload.changes ?? []) } catch {}
        }
      }
      if (payload.event === 'process.output') {
        const entry = execStreams.get(payload.execId)
        try { entry?.onOutput?.({ stream: payload.stream, chunk: payload.chunk, truncated: payload.truncated === true }) } catch {}
      }
      if (payload.event === 'process.exit') {
        const entry = execStreams.get(payload.execId)
        execStreams.delete(payload.execId)
        const result = { code: payload.code, timedOut: payload.timedOut === true }
        try { entry?.onExit?.(result) } catch {}
        const resolve = execExits.get(payload.execId)
        execExits.delete(payload.execId)
        resolve?.(result)
      }
      return {}
    }
    throw new Error('unsupported invocation: ' + kind)
  })
  bridge.ready(id)
}
`
}

/**
 * 视图垫片 —— **注入进每一个插件视图 HTML** 的一小段脚本。
 *
 * ## 它补的是哪一段
 *
 * 宿主(`renderer/src/shell/PluginViewFrame.tsx`)已经在往视图 iframe
 * postMessage 一条 `ncw:theme`,带着 24 个 color token、`appearance`、`motion`。
 * 但视图 iframe 和宿主**不同源**,它拿不到宿主的样式;而 `__runtime.js` 那套
 * API 垫片只经 `__host.html` 的 importmap 加载,**只服务于隐藏的插件宿主页面** ——
 * 视图这边一行都够不着。结果是那条消息发出去之后没有任何人接,
 * 每个插件作者都得自己写一遍 origin 校验 + 写变量的样板。
 *
 * ## 为什么是注入,不是让插件自己 import
 *
 * 同 `__host.html` 的理由(见文件头):垫片一旦由插件提供,插件就能决定
 * 自己看见什么主题 —— 而且更现实的是,**没人会记得引它**,于是「插件不跟随
 * 主题」会以每个插件各犯一次的方式反复出现。注入是唯一能让它默认成立的做法。
 *
 * ## 约定
 *
 * - CSS 变量写成 `--ncw-<token>`(如 `--ncw-canvas`)。**不用宿主内部的
 *   `--color-*`**:那是宿主的实现细节,而这里是给插件作者看的公开名字。
 * - `document.documentElement` 上同步设 `data-theme` / `data-theme-motion`,
 *   纯 CSS 的插件不写一行 JS 也能用属性选择器分支。
 * - `globalThis.__ncwTheme` 是**同步可读**的当前值 —— 插件首帧就要选对颜色,
 *   而等一条 message 到达至少是下一个 tick,那一帧会闪。
 * - 变化时派发 `ncw:theme` CustomEvent(detail 同 `__ncwTheme`)。
 *
 * ★ `event.origin !== location.origin` 的必须拒绝:视图是能被插件自己
 * 导航的,而 `window.parent` 之外还有别的窗口能拿到这个 frame 的引用。
 * 只认与自己同源的那条(宿主发送时 targetOrigin 就写死成这个 origin)。
 */
function viewShimJs(): string {
  return `(() => {
  const root = document.documentElement
  const state = { appearance: ${JSON.stringify(resolveAppearance())}, motion: 'full', tokens: {} }
  globalThis.__ncwTheme = state
  const apply = (message) => {
    state.appearance = message.appearance === 'light' ? 'light' : 'dark'
    state.motion = typeof message.motion === 'string' ? message.motion : 'full'
    const tokens = message.tokens
    if (tokens !== null && typeof tokens === 'object') {
      state.tokens = tokens
      for (const key of Object.keys(tokens)) {
        const value = tokens[key]
        if (typeof value !== 'string') continue
        root.style.setProperty('--ncw-' + key, value)
        /*
          ★ 同一个值**再写一个 \`--color-<token>\`**。

          那是宿主内部的名字,本不该出现在插件面前 —— 但 \`nextcowork/ui\` 下发的
          那份 CSS 是拿宿主组件原样生成的,里面的 \`bg-canvas\` 读的就是
          \`--color-canvas\`。不写这一份,用 \`nextcowork/ui\` 的视图会永远停在
          静态默认值上:用户换了强调色、调了对比度,宿主变了而插件视图纹丝不动。

          为什么不在那份 CSS 里做 \`--color-x: var(--ncw-x, …)\` 的映射:自引用
          会让自定义属性成环,**两边一起失效**,整个视图无色且零报错。
        */
        root.style.setProperty('--color-' + key, value)
      }
    }
    root.dataset.theme = state.appearance
    root.dataset.themeMotion = state.motion
    globalThis.dispatchEvent(new CustomEvent('ncw:theme', { detail: state }))
  }
  globalThis.addEventListener('message', (event) => {
    if (event.origin !== location.origin) return
    const data = event.data
    if (data === null || typeof data !== 'object' || data.type !== 'ncw:theme') return
    apply(data)
  })
  root.dataset.theme = state.appearance
})()`
}

/**
 * 把垫片插进视图 HTML 的 `<head>` 里。**纯函数,单测冲它来。**
 *
 * 注入三样,顺序就是下面 `tag` 的拼接顺序,而且顺序是有讲究的:
 *
 * 1. **import map** —— 必须在任何 `<script type="module">` **之前**。晚了的话
 *    浏览器已经开始解析模块依赖,map 直接被忽略(控制台一句
 *    "An import map is added after module script load was triggered"),
 *    症状是插件视图报 `Failed to resolve module specifier "react"`。
 * 2. **主题垫片** —— 见下面 `viewShimJs` 的说明。
 * 3. **`ui.css`** —— `nextcowork/ui` 那批控件的样式。插件不写任何引用就能拿到
 *    正确外观;交给插件自己引的话,「忘了引」的症状是有布局没颜色,
 *    而那看起来像是宿主坏了。
 *
 * ★ 插在 `<head>` **开头**,不是末尾、更不是 `</body>` 前:宿主是在 iframe 的
 * `load` 事件里发第一条 `ncw:theme` 的,而 `load` 晚于全部解析 —— 只要垫片
 * 在文档里出现过,监听器就一定先于那条消息登记。但插件自己的 `<script>` 可能
 * 同步读 `__ncwTheme`,所以要抢在它们前面。
 *
 * ★ 插件**自带** import map 的情况:我们这份仍然排在前面。Chromium 129+
 * 支持多份 import map(Electron 44 = Chromium 140),先出现的那份优先 ——
 * 也就是插件覆盖不掉 `react` / `nextcowork/ui` 的指向。这是**有意**的:
 * 那几个名字指向宿主下发的实例,插件换掉它等于自带第二份 React,
 * 而那的症状是 "Invalid hook call",几乎不可能从现象反推到原因。
 *
 * 没有 `<head>` 的畸形文档(插件手写的片段)走 `<html>` 之后;再没有就顶在
 * 最前面 —— 浏览器会把它收进隐式的 head。**任何一种情况都不能静默跳过注入**,
 * 那等于这个插件的视图永远不跟随主题,而且不报错。
 */
export function injectViewShim(html: string, nonce: string): string {
  const tag =
    `<script nonce="${nonce}" type="importmap">${JSON.stringify({ imports: RUNTIME_IMPORTS })}</script>` +
    `<script nonce="${nonce}">${viewShimJs()}</script>` +
    `<link rel="stylesheet" href="${RUNTIME_STYLESHEET}">`
  const head = /<head[^>]*>/i.exec(html)
  if (head !== null) {
    const at = head.index + head[0].length
    return html.slice(0, at) + tag + html.slice(at)
  }
  const htmlTag = /<html[^>]*>/i.exec(html)
  if (htmlTag !== null) {
    const at = htmlTag.index + htmlTag[0].length
    return html.slice(0, at) + tag + html.slice(at)
  }
  return tag + html
}

/**
 * 插件视图运行时:**URL 路径 → 产物文件名**。
 *
 * ## 为什么是宿主下发,而不是插件各打各的
 *
 * 插件自己打也能跑(CSP 放行 `'self'` 的外部脚本),代价有三条,且都要装了
 * 好几个插件之后才显形:每个视图一份 React(N 个 iframe = N 份运行时);
 * 控件各自重画,宿主一改尺寸配色所有插件同时变歪且无人收到通知;
 * 插件锁死在它打包那天的 React 版本上。
 *
 * ## `__` 前缀
 *
 * 和 `__host.html` / `__runtime.js` 同一套命名:这些路径**先于**包内文件匹配,
 * 所以插件包里同名的文件永远取不到。前缀是为了让这种遮蔽显而易见 ——
 * 没有前缀的话,一个正好叫 `ui.js` 的插件文件会莫名其妙变成宿主的那份。
 *
 * ★ 数据驱动:新增一个下发模块 = 这张表加一行 + 下面 `RUNTIME_IMPORTS` 加一行。
 */
const RUNTIME_FILES: Readonly<Record<string, string>> = {
  '/__react-core.js': 'react-core.js',
  '/__react.js': 'react.js',
  '/__react-dom.js': 'react-dom.js',
  '/__react-dom-client.js': 'react-dom-client.js',
  '/__jsx-runtime.js': 'jsx-runtime.js',
  '/__jsx-dev-runtime.js': 'jsx-dev-runtime.js',
  '/__ui.js': 'ui.js',
  '/__ui.css': 'ui.css',
  '/__view.js': 'view.js'
}

/**
 * 注入进每个插件视图的 import map:**裸模块名 → 上面那张表里的 URL**。
 *
 * ★ `react-dom/client` 和 `react` 必须落在**同一个核**(`react-core.js`)上 ——
 * 两份 React 实例的症状是 "Invalid hook call",而报错位置指向插件自己的组件,
 * 几乎不可能从现象反推到「宿主发了两份 React」。
 */
const RUNTIME_IMPORTS: Readonly<Record<string, string>> = {
  react: '/__react.js',
  'react-dom': '/__react-dom.js',
  'react-dom/client': '/__react-dom-client.js',
  'react/jsx-runtime': '/__jsx-runtime.js',
  'react/jsx-dev-runtime': '/__jsx-dev-runtime.js',
  'nextcowork/ui': '/__ui.js',
  'nextcowork/view': '/__view.js'
}

const RUNTIME_STYLESHEET = '/__ui.css'

/**
 * 运行时产物目录。由 `ipc/plugins.ts` 注入 —— 同 `PluginRootResolver` 的理由:
 * **协议层不认识 app**,它只认自己收到的 URL。
 *
 * 空串 = 还没注入(或者构建产物不在)。那种情况下这几个 URL 回 404,
 * 而不是去读一个拼错的路径。
 */
let runtimeDir = ''

export function setPluginRuntimeDir(dir: string): void {
  runtimeDir = dir
}

/**
 * 同一份运行时目录,给内置 widget 外壳(`net/widget-protocol.ts`)读。
 * ★ 只读出去、不另设一份:两处各注入一次的话,迟早有一处漏注入,
 * 表现为 TSX widget 一片空白而插件视图正常。
 */
export function pluginRuntimeDirectory(): string {
  return runtimeDir
}

/** `<userData>/plugins/<id>` 的解析器。由 `host-window.ts` 注入,协议层不认识 app。 */
export type PluginRootResolver = (pluginId: string) => { root: string; main: string } | undefined
let resolveRoot: PluginRootResolver = () => undefined

export function setPluginRootResolver(resolver: PluginRootResolver): void {
  resolveRoot = resolver
}

/**
 * 当前深浅色。**注入进视图垫片的初值**,让插件视图的第一帧就是对的。
 *
 * ★ 为什么要这个初值:宿主那条 `ncw:theme` 是在 iframe 的 `load` 事件里发的,
 * 最快也是解析完之后的下一个 tick —— 在那之前插件自己的脚本已经跑过了。
 * 没有初值的话,深色环境下的插件视图会先画一帧浅色再跳过去。
 *
 * ★ 走 setter 而不是直接 import:同 `PluginRootResolver` 的理由 ——
 * **协议层不认识 app、也不认识 store**。它只认自己收到的 URL。
 */
let resolveAppearance: () => 'light' | 'dark' = () => 'dark'

export function setPluginAppearanceResolver(resolver: () => 'light' | 'dark'): void {
  resolveAppearance = resolver
}

/**
 * 插件系统别处也要读这个值(`ipc/plugins.ts` 的 `appearance.get`)。
 *
 * ★ 从这里读、而不是各自 `import { resolveTheme } from '../ipc/app'`:
 * 后者会让 `ipc/app.ts` 和 `ipc/plugins.ts` 互相 import —— 而 app.ts 正要
 * 反向调 `notifyPluginsThemeChanged`。协议层谁都不依赖,放这儿不成环。
 */
export function pluginAppearance(): 'light' | 'dark' {
  return resolveAppearance()
}

/** 在 `app.whenReady()` 之后调用。 */
export function installPluginProtocol(): void {
  /*
    ★ **两个 session 都要注册,缺一个是装不出来的。**

    顶层的 `protocol.handle` 只挂在**默认 session**上;而插件宿主窗口跑在
    `persist:plugin-host` 分区里(ElectronPluginRuntime 给它单独的 partition,
    隔离插件的网络与存储)。分区 session 里没有这个处理器的话,
    `loadURL('ncw-plugin://…')` 直接 `ERR_FAILED(-2)` —— 没有更多解释,
    页面一个字节都到不了,而这个错误和「协议根本没注册」在表现上完全一样。

    `session.fromPartition` 在窗口创建之前就可以调,session 对象会复用。
    `registerSchemesAsPrivileged` 是全局的,不受影响。
  */
  const handler = (request: Request): Promise<Response> => handlePluginRequest(request, resolveRoot)
  protocol.handle(PLUGIN_SCHEME, handler)
  session.fromPartition(PLUGIN_HOST_PARTITION).protocol.handle(PLUGIN_SCHEME, handler)
}

export async function handlePluginRequest(
  request: Request,
  resolver: PluginRootResolver
): Promise<Response> {
  let url: URL
  try { url = new URL(request.url) } catch { return new Response('bad request', { status: 400 }) }

  const pluginId = url.hostname
  const entry = resolver(pluginId)
  if (entry === undefined) return new Response('forbidden', { status: 403 })

  const path = decodeURIComponent(url.pathname)
  if (path === '/__host.html' || path === '/') {
    /*
      ★ nonce **在这里生成一次**,同时给响应头和页面里的两段内联脚本 ——
      各生成各的等于没加:页面里那个值和 CSP 里那个对不上,CSP 照样拦。
    */
    const nonce = randomNonce()
    return new Response(hostHtml(pluginId, entry.main, nonce), {
      status: 200,
      headers: headers(MIME['.html'] as string, nonce)
    })
  }
  if (path === '/__runtime.js') {
    return new Response(runtimeJs(pluginId), { status: 200, headers: headers(MIME['.js'] as string) })
  }

  /*
    视图运行时(React / nextcowork/ui / nextcowork/view)。

    ★ 匹配放在**包内文件之前**:插件包里恰好同名的文件取不到宿主这份。
    反过来的话,一个自带 `ui.js` 的插件会把所有视图的控件实现换掉,
    而 import map 指向的还是这个路径 —— 那是一条能装下任意代码的后门。

    ★ 这些产物**不属于任何一个插件**,但仍然要求 `resolver(pluginId)` 认得
    调用方(上面已经查过)—— 没装过的插件 origin 连 403 都过不去,
    自然也拿不到运行时。
  */
  const runtimeFile = RUNTIME_FILES[path]
  if (runtimeFile !== undefined) {
    if (runtimeDir === '') return new Response('not found', { status: 404 })
    const target = join(runtimeDir, runtimeFile)
    try {
      const response = await net.fetch(pathToFileURL(target).toString())
      if (!response.ok) return new Response('not found', { status: 404 })
      return new Response(response.body, { status: 200, headers: headers(mimeOf(runtimeFile)) })
    } catch {
      return new Response('not found', { status: 404 })
    }
  }

  const target = resolveInsidePackage(entry.root, path)
  // ★ 403 而不是 404:两者要能区分开。404 是「这个文件没了」,403 是
  //   「有人在构造越界路径」—— 后者应当被注意到。
  if (target === null) return new Response('forbidden', { status: 403 })

  try {
    const rootStat = await lstat(entry.root)
    if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) return new Response('not found', { status: 404 })
    const canonicalRoot = await realpath(entry.root)
    const canonicalTarget = await realpath(target).catch(() => null)
    if (canonicalTarget === null) return new Response('not found', { status: 404 })
    /*
      ★ 第二层:realpath 之后再验一次落点。第一层验的是「路径长得对不对」,
      这一层验的是「跟着软链走完之后落在哪」。缺了它,包里一个指向
      `~/.ssh/id_rsa` 的软链会被原样读出来 —— 而词法检查完全看不出来。
    */
    if (!isWithin(canonicalRoot, canonicalTarget)) return new Response('forbidden', { status: 403 })
    // ★ 软链之后再拦一次原生区:包里一个视图文件软链到 `native/` 下,词法检查看不出来
    if (isNativePackagePath(relative(canonicalRoot, canonicalTarget))) return new Response('forbidden', { status: 403 })
    const targetStat = await lstat(canonicalTarget)
    if (!targetStat.isFile()) return new Response('not found', { status: 404 })

    const response = await net.fetch(pathToFileURL(canonicalTarget).toString())
    if (!response.ok) return new Response('not found', { status: 404 })
    const contentType = mimeOf(path)
    /*
      ★ **HTML 要落地读完再发,不能像其它类型那样流式透传** —— 垫片必须
      插进正文里。只有 HTML 走这条路:图片、字体、wasm 仍然流式,
      一个几 MB 的 .wasm 不该为了这件事先进一次内存。

      nonce **在这里生成一次**,同时给响应头和注入的那段脚本。分两次生成
      等于没注入:CSP 里那个值和标签上那个对不上,脚本照样被拦,
      而症状是「插件视图颜色永远不对」加控制台一句 Refused to execute。
    */
    if (contentType === MIME['.html']) {
      const nonce = randomNonce()
      return new Response(injectViewShim(await response.text(), nonce), {
        status: 200,
        headers: headers(contentType, nonce)
      })
    }
    return new Response(response.body, { status: response.status, headers: headers(contentType) })
  } catch {
    return new Response('not found', { status: 404 })
  }
}

/** 纯函数,单测就是冲它来的。返回 null = 拒绝。 */
export function resolveInsidePackage(root: string, pathname: string): string | null {
  if (pathname.includes('\0')) return null
  const rel = pathname.replace(/^\/+/, '')
  if (rel === '') return null
  if (rel.split('/').some((segment) => segment === '..' || segment === '.')) return null
  /*
    需求:插件包里 `native/` 下是原生引擎(办公插件自带的 LibreOffice helper 与动态库),
    只能由主进程校验后 spawn,**绝不能**当静态资源发给 iframe。不拦的话,任何一个
    视图都能 fetch 走引擎二进制,协议层还会把它当 octet-stream 发出去(见
    `shared/plugin/native-component.ts` 文件头第 1 条)。
  */
  if (isNativePackagePath(rel)) return null
  const target = resolve(join(root, rel))
  return isWithin(root, target) ? target : null
}

function isWithin(root: string, target: string): boolean {
  const rel = relative(resolve(root), resolve(target))
  return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel))
}
