namespace Ncw.Agent.Plugins;

/// <summary>
/// 每个插件引擎启动时先跑的一段 JS:把 C# 绑定的宿主函数(全局的 <c>__n_*</c>,只有方法表里的才存在)收进闭包、从全局里删掉,
/// 再在上面搭出 <c>nextcowork</c> 对象(2.x <c>nextcowork.d.ts</c> 的非 UI 部分)。插件只能看见 <c>nextcowork</c> 模块和 <c>setTimeout</c> / <c>console</c>,
/// 看不见原始绑定;而即使看得见,每个绑定自己都先过权限门(<see cref="PluginHostMethods"/>)。
/// <para>
/// 两类绑定:异步方法(<c>__n_&lt;name&gt;(paramsJson) → Promise&lt;envelopeJson&gt;</c>,信封是 <c>{ok,data}</c> / <c>{ok:false,code,message}</c>,所以权限拒绝等业务失败是插件看得懂的
/// <c>Error.code</c>,而不是引擎里一句泛泛的「宿主函数失败」)和同步注册类(命令 / 工具 / 钩子 / 进度 / 日志)。
/// 宿主进插件的唯一入口是 <c>__ncw_dispatch</c>(<c>command</c> / <c>tool</c> / <c>interceptor</c> / <c>context</c> / <c>dispose</c>)。
/// </para>
/// </summary>
internal static class PluginPrelude
{
    public const string Source = """
(function () {
'use strict';
const g = globalThis;
const id = g.__ncw_id;
const apiVersion = g.__ncw_api;
delete g.__ncw_id;
delete g.__ncw_api;

function take(name) {
  const f = g[name];
  try { delete g[name]; } catch (e) { /* harmless if it cannot be removed: every binding checks permissions itself */ }
  return f;
}

const natives = {};
for (const method of JSON.parse(take('__n_methods'))) natives[method] = take('__n_' + method.replaceAll('.', '_'));
const nLog = take('__n_log');
const nRegisterCommand = take('__n_registerCommand');
const nRegisterTool = take('__n_registerTool');
const nUnregister = take('__n_unregister');
const nSetHook = take('__n_setHook');
const nProgress = take('__n_progress');
const nCancelTimers = take('__n_cancelTimers');
const nCloseActions = take('__n_closeActions');

// Jaunt 要求一次执行结束时不能还有没完成的异步宿主操作(否则引擎进入故障态)。插件里没 await 的宿主调用、没触发的定时器、等动作的泵都是这样的:
// 所以每一次进入插件的调用(dispatch)在返回之前把它们收齐——定时器取消、动作通道关闭、其余等它们跑完。
const pending = new Set();
function track(promise) {
  pending.add(promise);
  const done = () => { pending.delete(promise); };
  promise.then(done, done);
  return promise;
}
async function drain() {
  while (pending.size > 0) await Promise.allSettled([...pending]);
}

function fail(code, message) {
  const e = new Error(message);
  e.code = code;
  return e;
}

function rpc(name, params) {
  const fn = natives[name];
  if (typeof fn !== 'function') return Promise.reject(fail('unknown_method', 'unknown host method: ' + name));
  return track((async () => {
    const r = JSON.parse(await fn(JSON.stringify(params === undefined ? null : params)));
    if (!r.ok) throw fail(r.code, r.message);
    return r.data;
  })());
}

class Disposable {
  constructor(callOnDispose) { this._f = callOnDispose; this._d = false; }
  static from(...items) { return new Disposable(() => { for (const i of items) { if (i && typeof i.dispose === 'function') i.dispose(); } }); }
  dispose() { if (this._d) return; this._d = true; const f = this._f; this._f = undefined; if (typeof f === 'function') f(); }
}

const context = { extensionId: id, subscriptions: [] };
g.__ncw_ctx = context;

// ───────────── 命令 / 工具 / 钩子(插件里的处理函数留在 JS,C# 只知道名字和声明)─────────────
const commandHandlers = new Map();
const toolImpls = new Map();
let interceptor = null;
let contextProvider = null;

const commands = {
  registerCommand(commandId, handler) {
    if (typeof commandId !== 'string' || typeof handler !== 'function') throw new TypeError('registerCommand(commandId: string, handler: function)');
    if (!nRegisterCommand(commandId)) return new Disposable(() => {});
    commandHandlers.set(commandId, handler);
    return new Disposable(() => { if (commandHandlers.get(commandId) === handler) { commandHandlers.delete(commandId); nUnregister('command', commandId); } });
  },
  async executeCommand(commandId, args) {
    const handler = commandHandlers.get(commandId);
    if (typeof handler !== 'function') throw fail('not_found', 'this plugin has no registered command ' + commandId);
    return await handler(args);
  },
};

const tools = {
  registerTool(name, tool) {
    if (typeof name !== 'string' || tool === null || typeof tool !== 'object' || typeof tool.invoke !== 'function') throw new TypeError('registerTool(name: string, tool: { description, inputSchema, invoke })');
    const declaration = {
      description: String(tool.description ?? ''),
      inputSchema: tool.inputSchema ?? { type: 'object', properties: {} },
      readOnly: tool.readOnly === true,
      destructive: tool.destructive === true,
      needsNetwork: tool.needsNetwork === true,
      interactive: tool.interactive === true,
    };
    if (!nRegisterTool(name, JSON.stringify(declaration))) return new Disposable(() => {});
    toolImpls.set(name, tool);
    return new Disposable(() => { if (toolImpls.get(name) === tool) { toolImpls.delete(name); nUnregister('tool', name); } });
  },
};

const agent = {
  registerToolInterceptor(handler) {
    if (typeof handler !== 'function') throw new TypeError('registerToolInterceptor(handler: function)');
    if (!nSetHook('interceptor', true)) return new Disposable(() => {});
    interceptor = handler;
    return new Disposable(() => { if (interceptor === handler) { interceptor = null; nSetHook('interceptor', false); } });
  },
  registerContextProvider(handler) {
    if (typeof handler !== 'function') throw new TypeError('registerContextProvider(handler: function)');
    if (!nSetHook('context', true)) return new Disposable(() => {});
    contextProvider = handler;
    return new Disposable(() => { if (contextProvider === handler) { contextProvider = null; nSetHook('context', false); } });
  },
};

// ───────────── 实时卡片的动作泵:用户点按钮 → plugins.cardAction → 这里的 onAction 处理函数 ─────────────
const pumps = new Map();
function startPump(callId, handler) {
  const known = pumps.get(callId);
  if (known) { known.handler = handler; return; }
  const pump = { handler };
  pumps.set(callId, pump);
  track((async () => {
    for (;;) {
      let raw;
      try { raw = await natives['internal.nextAction'](JSON.stringify({ callId })); } catch (e) { break; }
      const r = JSON.parse(raw);
      if (!r.ok || r.data === null) break;
      try { pump.handler(r.data); } catch (e) { nLog('warn', 'a card action handler threw: ' + (e && e.message)); }
    }
  })());
}

g.__ncw_dispatch = async function (kind, name, argsJson, callId, fn) {
  const arg = argsJson === '' || argsJson === undefined ? undefined : JSON.parse(argsJson);
  let result;
  try {
  switch (kind) {
    case 'activate':
      if (typeof fn === 'function') await fn(context);
      break;
    case 'deactivate':
      if (typeof fn === 'function') await fn();
      break;
    case 'command': {
      const handler = commandHandlers.get(name);
      if (typeof handler !== 'function') throw fail('not_found', 'the command ' + name + ' is not registered');
      result = await handler(arg);
      break;
    }
    case 'tool': {
      const tool = toolImpls.get(name);
      if (!tool) throw fail('not_found', 'the tool ' + name + ' is not registered');
      result = await tool.invoke({
        input: arg,
        callId,
        progress: (update) => { try { nProgress(callId, JSON.stringify(update === undefined ? {} : update)); } catch (e) { /* progress is volatile */ } },
        onAction: (handler) => { if (typeof handler === 'function') startPump(callId, handler); },
      });
      break;
    }
    case 'interceptor':
      result = interceptor ? await interceptor(arg) : undefined;
      break;
    case 'context':
      result = contextProvider ? await contextProvider(arg) : undefined;
      break;
    case 'dispose': {
      const items = context.subscriptions.splice(0);
      for (const item of items.reverse()) { try { if (item && typeof item.dispose === 'function') item.dispose(); } catch (e) { nLog('warn', 'dispose threw: ' + (e && e.message)); } }
      break;
    }
    default:
      throw fail('unknown_method', 'unknown dispatch kind ' + kind);
  }
  } finally {
    nCancelTimers();
    timers.clear();
    if (callId) { nCloseActions(callId); pumps.delete(callId); }
    await drain();
  }
  return JSON.stringify(result === undefined ? null : result);
};

// ───────────── 宿主 API(只有绑定过的方法才能走通;没绑定的是 unknown_method)─────────────
const storage = {
  global: {
    get: async (key) => (await rpc('storage.get', { scope: 'global', key })).value,
    set: (key, value) => rpc('storage.set', { scope: 'global', key, value }).then(() => undefined),
    keys: async () => (await rpc('storage.keys', { scope: 'global' })).keys,
  },
  workspace: {
    get: async (key, options) => (await rpc('storage.get', { scope: 'workspace', key })).value,
    set: (key, value, options) => rpc('storage.set', { scope: 'workspace', key, value }).then(() => undefined),
    keys: async (options) => (await rpc('storage.keys', { scope: 'workspace' })).keys,
  },
};

const secrets = {
  get: async (key) => (await rpc('secrets.get', { key })).value,
  set: (key, value) => rpc('secrets.set', { key, value }).then(() => undefined),
};

const workspace = {
  folders: async () => (await rpc('workspace.folders', {})).folders,
  fs: {
    readFile: (path, encoding) => rpc('workspace.readFile', { path, encoding }),
    writeFile: (path, data, options) => rpc('workspace.writeFile', { path, data, encoding: options && options.encoding, revision: options && options.revision }),
    delete: (path) => rpc('workspace.deleteFile', { path }).then(() => undefined),
    stat: (path) => rpc('workspace.stat', { path }),
  },
  findFiles: async (glob, limit) => (await rpc('workspace.findFiles', { glob, limit })).paths,
};

const nodeProcess = {
  exec: (command, args, options) => rpc('process.exec', { command, args: args === undefined ? [] : args, cwd: options && options.cwd, timeoutMs: options && options.timeoutMs }),
};

const net = {
  fetch: (url, init) => rpc('net.fetch', { url, method: init && init.method, headers: init && init.headers, body: init && init.body }),
};

const permissions = {
  contains: async (permission) => (await rpc('permissions.contains', { permissions: Array.isArray(permission) ? permission : [permission] })).granted,
  request: async (permissionList, reasonKey) => (await rpc('permissions.request', { permissions: permissionList, reasonKey })).granted,
  remove: (permissionList) => rpc('permissions.remove', { permissions: permissionList }).then(() => undefined),
};

const configuration = { get: async () => (await rpc('configuration.get', {})).values };

const env = { appInfo: () => rpc('env.appInfo', {}) };

const appearance = {
  get: async () => (await rpc('appearance.get', {})).appearance,
  onDidChange: () => new Disposable(() => {}),
};

const windowApi = {
  showMessage: (kind, messageKey, params) => rpc('window.showMessage', { kind, messageKey, params }).then(() => undefined),
  showQuickPick: async (items, placeholderKey) => (await rpc('window.showQuickPick', { items, placeholderKey })).id,
  showInputBox: async (options) => (await rpc('window.showInputBox', options)).value,
  showConfirm: async (options) => (await rpc('window.showConfirm', options)).confirmed,
  setStatusBarItem: (itemId, textKey, options) => rpc('window.setStatusBarItem', { id: itemId, textKey }).then(() => undefined),
  withProgress: async (options, task) => await task({ report: () => {} }),
};

const diagnostics = { log: (level, message) => rpc('diagnostics.log', { level, message }).then(() => undefined) };

// ───────────── 计时器:只在有一次调用正在执行时才会触发(调用结束后挂着的定时器随调用一起结束)─────────────
let timerSeq = 0;
const timers = new Map();
g.setTimeout = function (fn, ms, ...rest) {
  if (typeof fn !== 'function') throw new TypeError('setTimeout(fn: function, ms?: number)');
  const handle = ++timerSeq;
  timers.set(handle, true);
  track(natives['internal.sleep'](JSON.stringify({ ms: Number(ms) || 0 })).then(() => {
    if (!timers.delete(handle)) return;
    try { fn(...rest); } catch (e) { nLog('error', 'a timer callback threw: ' + (e && e.message)); }
  }, () => { timers.delete(handle); }));
  return handle;
};
g.clearTimeout = function (handle) { timers.delete(handle); };

function render(value) {
  if (typeof value === 'string') return value;
  if (value instanceof Error) return value.stack || value.message;
  try { return JSON.stringify(value); } catch (e) { return String(value); }
}
const format = (items) => items.map(render).join(' ');
g.console = {
  log: (...a) => nLog('info', format(a)),
  info: (...a) => nLog('info', format(a)),
  debug: (...a) => nLog('info', format(a)),
  warn: (...a) => nLog('warn', format(a)),
  error: (...a) => nLog('error', format(a)),
};

g.__ncw = {
  version: apiVersion,
  extensionId: id,
  Disposable,
  env,
  permissions,
  storage,
  secrets,
  workspace,
  process: nodeProcess,
  net,
  configuration,
  window: windowApi,
  agent,
  commands,
  tools,
  diagnostics,
  appearance,
};
})();
""";

    /// <summary>虚拟模块 <c>nextcowork</c>(清单里 <c>main</c> 打包时它是 external,运行期由宿主注入;身份是写死的,插件改不了)。</summary>
    public const string ModuleSource = """
const n = globalThis.__ncw;
export const { version, extensionId, Disposable, env, permissions, storage, secrets, workspace, process, net, configuration, window, agent, commands, tools, diagnostics, appearance } = n;
export default n;
""";
}
