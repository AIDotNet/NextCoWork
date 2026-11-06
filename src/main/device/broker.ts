/**
 * 控制端 IPC → 所选设备的路由决策(切换事务的「数据面」)。纯逻辑 + 管理器,无 Electron。
 *
 * 规则(对应计划 M4「离线不回退本机写入」):
 *  - `controller-local` / `composed` 频道永远在本机处理(窗口、剪贴板、设备页本身……);
 *  - 选中了设备(`deviceId !== null`)时,`device` 类频道**只能**走设备:
 *      · 在路由表里的 → 转发给设备(设备离线则 `disconnected`,绝不改在本机跑);
 *      · 不在路由表里的 → 明确拒绝,**不会**悄悄在本机执行 —— 否则本机库会被当成「所选设备」写入;
 *  - 没选设备 → 全部本机处理(现状)。
 *
 * ★ 路由表只收「设备方法的入参/出参与桌面频道逐字段一致」的频道(由测试对比真实输出),
 *   一条一条加,不按前缀批量放行。
 * ★ 写操作(`kind: 'command'`)走设备的幂等命令:每次调用一个新 key,**不自动重发** ——
 *   断线后结果未知就如实报错,由用户决定是否再试(自动换 key 重发会让同一次点击执行两遍)。
 * ★ 设备返回值是已认证连接上来的数据,但**没有再做形状校验**;调用方按桌面类型使用它。
 */
import { randomUUID } from 'node:crypto'
import { DeviceProtocolError, type DeviceErrorCode } from '../../shared/device/errors'
import type { DeviceValue } from '../../shared/device/value'
import type { InvokeChannel, InvokeReq, InvokeRes, IpcSendMap, SendChannel } from '../../shared/ipc/contract'
import { INVOKE_BOUNDARIES, SEND_BOUNDARIES } from '../../shared/ipc/boundaries'
import type { ControllerClient } from '../../controller-client/client'
import type { AgentBridge } from './agent-bridge'
import type { TerminalBridge } from './terminal-bridge'
import { runTopic } from '../services/agent'
import { terminalTopic } from '../window/registry'

/** 窗口侧的主题订阅(`agent:event` 只推给订阅了该 run 的窗口)。没有窗口上下文时是空操作。 */
export interface WindowTopics {
  subscribe(topic: string): void
  unsubscribe(topic: string): void
}
const NO_WINDOW: WindowTopics = { subscribe: () => {}, unsubscribe: () => {} }

interface RouteContext<K extends InvokeChannel> {
  client: ControllerClient
  req: InvokeReq<K>
  bridge: AgentBridge
  terminals: TerminalBridge
  window: WindowTopics
}

/** 事件/输出桥。自定义路由要用到它们;没有桥(例如只测转发)时这些路由一律拒绝,不会落回本机。 */
export interface DeviceBridges { agent: AgentBridge; terminals: TerminalBridge }

interface Route<K extends InvokeChannel> {
  method: string
  params(req: InvokeReq<K>): DeviceValue
  /** 写操作:走幂等命令,结果从回执里取。省略 = 只读查询。 */
  kind?: 'command'
  /** 把设备回执里的结果转成桌面频道的返回值(默认原样)。 */
  result?(value: DeviceValue): InvokeRes<K>
  /** 结果需要再问设备一次才能凑齐桌面返回值时用(例如创建后读回完整对象)。优先于 `result`。 */
  finish?(client: ControllerClient, value: DeviceValue, req: InvokeReq<K>): Promise<InvokeRes<K>>
  /** 只读查询的结果后处理(例如按桌面入参再筛一遍)。 */
  map?(value: DeviceValue, req: InvokeReq<K>): InvokeRes<K>
  /** 整条自定义处理(需要事件桥/窗口订阅的 agent 频道)。有它就不看 method/kind。 */
  custom?(context: RouteContext<K>): Promise<InvokeRes<K>>
}

type RouteTable = { [K in InvokeChannel]?: Route<K> }

const withWorkspace = (req: { workspaceId: string }): DeviceValue => ({ workspaceId: req.workspaceId })
const optionalWorkspace = (req: { workspaceId?: string }): DeviceValue => req.workspaceId === undefined ? {} : { workspaceId: req.workspaceId }
const noResult = (): undefined => undefined
/** 桌面的 Partial<Settings> 里可能带 `undefined` 值;设备的 schema 是严格对象,不接受它们。 */
const stripUndefined = (value: object): DeviceValue =>
  Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)) as DeviceValue

export const DEVICE_ROUTES: RouteTable = {
  'settings:get': { method: 'settings.get', params: () => null },
  'workspace:list': { method: 'workspaces.list', params: () => null },
  'workspace:listDir': { method: 'files.list', params: (req) => ({ workspaceId: req.workspaceId, path: req.path }) },
  'workspace:readFile': { method: 'files.read', params: (req) => ({ workspaceId: req.workspaceId, path: req.path }) },
  // 工作区与文件写入。写文件必须带读到的 revision(设备端冲突保护);设备只回新 revision/大小,桌面要完整文件,所以写后读回。
  'workspace:update': {
    method: 'workspaces.update', kind: 'command',
    params: (req) => ({ id: req.id, ...(req.name !== undefined && { name: req.name }), ...(req.settings !== undefined && { settings: stripUndefined(req.settings) }) }),
    finish: async (client, _value, req) => {
      const list = await client.query('workspaces.list', null) as unknown as Array<{ id: string }>
      const found = list.find((workspace) => workspace.id === req.id)
      if (found === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
      return found as never
    }
  },
  'workspace:close': { method: 'workspaces.close', kind: 'command', params: (req) => ({ id: req.id }), result: noResult },
  'workspace:writeFile': {
    method: 'files.write', kind: 'command',
    params: (req) => ({ workspaceId: req.workspaceId, path: req.path, content: req.content, revision: req.revision, ...(req.encoding !== undefined && { encoding: req.encoding }) }),
    finish: async (client, _value, req) => await client.query('files.read', { workspaceId: req.workspaceId, path: req.path }) as never
  },
  'workspace:mutateFile': {
    method: 'files.mutate', kind: 'command',
    params: (req) => ({ workspaceId: req.workspaceId, path: req.path, operation: req.operation,
      ...(req.destination !== undefined && { destination: req.destination }), ...(req.overwrite !== undefined && { overwrite: req.overwrite }) })
  },
  'sessions:list': { method: 'sessions.list', params: (req) => ({ workspaceId: req.workspaceId, ...(req.archived !== undefined && { archived: req.archived }) }) },
  'sessions:getPage': {
    method: 'sessions.page',
    params: (req) => ({ sessionId: req.sessionId, limit: req.limit, ...(req.beforeMessageId !== undefined && { beforeMessageId: req.beforeMessageId }) })
  },
  // 创建是写操作:设备只回 sessionId,桌面要完整的 Session,所以创建后再读一页取回(limit 1,只要 session 字段)。
  'sessions:create': {
    method: 'sessions.create', kind: 'command',
    params: (req) => ({ workspaceId: req.workspaceId, ...(req.sessionId !== undefined && { sessionId: req.sessionId }),
      ...(req.title !== undefined && { title: req.title }), ...(req.mode !== undefined && { mode: req.mode }) }),
    finish: async (client, value) => {
      const created = value as { sessionId?: unknown }
      if (typeof created.sessionId !== 'string') throw new DeviceProtocolError('internal', 'The device did not return the new session')
      const page = await client.query('sessions.page', { sessionId: created.sessionId, limit: 1 }) as { session: never }
      return page.session
    }
  },

  // 会话元数据读取:纯库查询,与桌面逐字段一致(桌面那两条也只读库,不碰目标运行时)。
  'sessions:getSummary': { method: 'sessions.summary', params: (req) => ({ sessionId: req.sessionId }) },
  'sessions:lastAssistant': { method: 'sessions.lastAssistant', params: (req) => ({ sessionId: req.sessionId }) },
  // 会话元数据写入(可逆的小改动;删除不经网络开放,所以选了设备后仍被拒绝)
  'sessions:rename': { method: 'sessions.rename', kind: 'command', params: (req) => ({ sessionId: req.sessionId, title: req.title }), result: noResult },
  'sessions:setArchived': { method: 'sessions.setArchived', kind: 'command', params: (req) => ({ sessionId: req.sessionId, archived: req.archived }), result: noResult },
  'sessions:setFavorited': { method: 'sessions.setFavorited', kind: 'command', params: (req) => ({ sessionId: req.sessionId, favorited: req.favorited }), result: noResult },
  'sessions:setMode': { method: 'sessions.setMode', kind: 'command', params: (req) => ({ sessionId: req.sessionId, mode: req.mode }), result: noResult },
  'sessions:setModel': {
    method: 'sessions.setModel', kind: 'command', result: noResult,
    params: (req) => ({ sessionId: req.sessionId, model: req.model, ...(req.modelProviderId !== undefined && { modelProviderId: req.modelProviderId }) })
  },

  // 目录类(设备上扫描的技能/命令/子代理/模式/钩子;路径已被设备改写为 <workspace>/… 形式)
  'skills:list': { method: 'skills.list', params: optionalWorkspace },
  'commands:listAll': { method: 'commands.list', params: optionalWorkspace },
  'agents:list': { method: 'agents.list', params: optionalWorkspace },
  'modes:list': { method: 'modes.list', params: withWorkspace },
  'hooks:list': { method: 'hooks.list', params: optionalWorkspace },
  'mcp:list': { method: 'mcp.list', params: () => null },

  // Git:读 + 本地写(不含 pull/push/生成提交信息 —— 它们要网络、凭据或模型,设备侧没有对应方法)
  'git:getOverview': { method: 'git.overview', params: withWorkspace },
  'git:listBranches': { method: 'git.branches', params: withWorkspace },
  'git:listCommits': { method: 'git.commits', params: (req) => ({ workspaceId: req.workspaceId, ...(req.limit !== undefined && { limit: req.limit }) }) },
  'git:getDiff': { method: 'git.diff', params: (req) => ({ workspaceId: req.workspaceId, path: req.path, staged: req.staged }) },
  'git:stage': { method: 'git.stage', kind: 'command', params: (req) => ({ workspaceId: req.workspaceId, paths: req.paths }), result: noResult },
  'git:unstage': { method: 'git.unstage', kind: 'command', params: (req) => ({ workspaceId: req.workspaceId, paths: req.paths }), result: noResult },
  'git:commit': { method: 'git.commit', kind: 'command', params: (req) => ({ workspaceId: req.workspaceId, message: req.message }) },
  'git:checkoutBranch': { method: 'git.checkout', kind: 'command', params: (req) => ({ workspaceId: req.workspaceId, branch: req.branch }), result: noResult },
  'git:createBranch': { method: 'git.createBranch', kind: 'command', params: (req) => ({ workspaceId: req.workspaceId, name: req.name, checkout: req.checkout }) },

  // Agent:发起、续看(事件桥)、停止、审批。附件/子代理/计划执行等内部字段设备侧没有对应入口,一律拒绝而不是丢字段。
  'agent:run': {
    method: 'agent.start', params: () => null,
    custom: async ({ client, req, bridge, window }) => {
      const internal = req.parentRunId !== undefined || req.planExecution !== undefined || req.inputInternal === true
        || req.inputGoalId !== undefined || req.agentType !== undefined || req.depth !== 0
      const texts = req.input.map((part) => part.type === 'text' ? part.text : null)
      if (internal || texts.length === 0 || texts.some((text) => text === null)) {
        throw new DeviceProtocolError('forbidden', 'This kind of run (attachments, sub-agents or plan execution) is not available on the selected device yet')
      }
      window.subscribe(runTopic(req.runId)) // 先订阅再启动:启动后产生的第一批事件不能漏
      const receipt = await client.command('agent.start', {
        runId: req.runId, sessionId: req.sessionId, text: texts.join('\n'),
        options: {
          workspaceId: req.workspaceId, mode: req.mode, thinking: req.thinking, webSearch: req.webSearch, permissionMode: req.permissionMode,
          model: req.model, skillIds: req.skillIds,
          ...(req.maxContext !== undefined && { maxContext: req.maxContext }),
          ...(req.modelProviderId !== undefined && { modelProviderId: req.modelProviderId }),
          ...(req.skillSelectionMode !== undefined && { skillSelectionMode: req.skillSelectionMode })
        }
      }, `ncw-${randomUUID()}`) as Receipt
      let started: { started?: unknown }
      try { started = settle(receipt) as { started?: unknown } } catch (error) { window.unsubscribe(runTopic(req.runId)); throw error }
      if (started.started !== true) {
        window.unsubscribe(runTopic(req.runId))
        // 设备上这条会话正在跑:这句话已被设备排进它的队列。桌面需要的队列快照设备没有回,所以如实报告而不是编一份。
        throw new DeviceProtocolError('conflict', 'The session is already running on the device; your message was queued there')
      }
      await bridge.attach(client, req.runId, 0, true)
      return { started: true }
    }
  },
  'agent:attach': {
    method: 'agent.attach', params: () => null,
    custom: async ({ client, req, bridge, window }) => {
      window.subscribe(runTopic(req.runId))
      try { return await bridge.attach(client, req.runId, req.sinceSeq, false) } catch (error) { window.unsubscribe(runTopic(req.runId)); throw error }
    }
  },
  'agent:unwatch': {
    method: 'agent.detach', params: () => null,
    custom: async ({ client, req, bridge, window }) => { window.unsubscribe(runTopic(req.runId)); await bridge.unwatch(client, req.runId) }
  },
  'agent:abort': { method: 'agent.abort', kind: 'command', params: (req) => ({ runId: req.runId, cascade: req.cascade }), result: noResult },
  'agent:respondInteraction': { method: 'interactions.respond', kind: 'command', params: (req) => ({ response: req as unknown as DeviceValue }), result: noResult },
  'agent:listInteractions': {
    method: 'interactions.list', params: () => null,
    custom: async ({ client, req }) => {
      // 设备只能按 run 过滤;只给 sessionId 会拿到别的会话的审批,所以拒绝而不是返回超集。
      if (req.runId === undefined && req.sessionId !== undefined) throw new DeviceProtocolError('forbidden', 'Listing interactions by session is not available on the selected device yet')
      return await client.query('interactions.list', req.runId === undefined ? {} : { runId: req.runId }) as never
    }
  },

  // 终端:设备上的真实 PTY(创建者持有输入租约,桥负责续租与输出推送)。设备终端不需要「审批」那一步,
  // 所以 prepare 直接给 ready;SSH 工作区终端设备端一律拒绝,错误原样抛出。
  'terminal:create': {
    method: 'terminals.create', params: () => null,
    custom: ({ client, req, terminals, window }) => terminals.create(client, req, { subscribe: (id) => window.subscribe(terminalTopic(id)) })
  },
  'terminal:prepare': {
    method: 'terminals.create', params: () => null,
    custom: async ({ client, req, terminals, window }) => ({ kind: 'ready' as const, terminal: await terminals.create(client, req, { subscribe: (id) => window.subscribe(terminalTopic(id)) }) })
  },
  'terminal:approve': {
    method: 'terminals.create', params: () => null,
    custom: () => Promise.reject(new DeviceProtocolError('forbidden', 'Device terminals do not use approval'))
  },
  'terminal:kill': { method: 'terminals.kill', params: () => null, custom: async ({ client, req, terminals }) => { await terminals.kill(client, req.id) } },
  'terminal:list': { method: 'terminals.list', params: () => null, custom: ({ client, req, terminals }) => terminals.list(client, req.workspaceId) },
  'terminal:getBuffer': {
    method: 'terminals.attach', params: () => null,
    custom: async ({ client, req, terminals, window }) => {
      window.subscribe(terminalTopic(req.id))
      return await terminals.getBuffer(client, req.id)
    }
  }
}

/** send 类频道(没有回程):选了设备后转给设备,失败只记日志。 */
export const DEVICE_SEND_ROUTES = {
  'terminal:write': (terminals: TerminalBridge, payload: { id: string; data: string }) => terminals.write(payload.id, payload.data),
  'terminal:resize': (terminals: TerminalBridge, payload: { id: string; cols: number; rows: number }) => terminals.resize(payload.id, payload.cols, payload.rows)
} as const

export type RouteDecision = 'local' | 'remote' | 'refuse'

/** 只做决策,不执行。导出以便单测每个频道的归属。 */
export function decideRoute(channel: InvokeChannel, selectedDeviceId: string | null): RouteDecision {
  if (selectedDeviceId === null) return 'local'
  // 未登记的频道不按「本机」处理:只有明确归为本机侧的才放行(分类表本身对已知频道是穷尽的)。
  const boundary: string | undefined = INVOKE_BOUNDARIES[channel]
  if (boundary === 'controller-local' || boundary === 'composed') return 'local'
  return DEVICE_ROUTES[channel] === undefined ? 'refuse' : 'remote'
}

export interface BrokerPort {
  current(): { deviceId: string | null }
  run<T>(work: (client: ControllerClient) => Promise<T>): Promise<T>
}

interface Receipt { status?: unknown; result?: unknown }

export class DeviceBroker {
  constructor(private readonly port: BrokerPort, private readonly bridges?: DeviceBridges) {}

  /**
   * send 类频道(终端输入/改尺寸,没有回程)。选了设备时:有路由就转给设备,没有就**丢弃并记日志**
   * (不会落到本机 —— 本机没有这个终端,而落到同 id 的本机终端会把按键写进错误的 shell)。
   */
  dispatchSend<K extends SendChannel>(channel: K, payload: IpcSendMap[K], local: () => void): void {
    const selected = this.port.current().deviceId
    if (selected === null) { local(); return }
    const boundary: string | undefined = SEND_BOUNDARIES[channel]
    if (boundary === 'controller-local' || boundary === 'composed') { local(); return }
    const route = (DEVICE_SEND_ROUTES as Record<string, ((terminals: TerminalBridge, payload: never) => Promise<void>) | undefined>)[channel]
    if (route === undefined || this.bridges === undefined) { console.warn(`[device] send ${channel} is not available on the selected device`); return }
    void route(this.bridges.terminals, payload as never)
  }

  /**
   * 处理一次 invoke。`local` 只在决策为本机时才会被调用 ——
   * 其余情况下它**不会**被执行(包括设备离线、被拒绝)。
   * `window` 是发起这次调用的窗口(agent 频道据此订阅/退订该 run 的事件)。
   */
  async dispatch<K extends InvokeChannel>(channel: K, req: InvokeReq<K>, local: () => Promise<InvokeRes<K>> | InvokeRes<K>,
    window: WindowTopics = NO_WINDOW): Promise<InvokeRes<K>> {
    const decision = decideRoute(channel, this.port.current().deviceId)
    if (decision === 'local') return local()
    if (decision === 'refuse') throw refused()
    const route = DEVICE_ROUTES[channel] as Route<K>
    if (route.custom !== undefined) {
      const custom = route.custom
      const bridges = this.bridges
      if (bridges === undefined) throw refused()
      return await this.port.run((client) => custom({ client, req, bridge: bridges.agent, terminals: bridges.terminals, window }))
    }
    const params = route.params(req)
    if (route.kind !== 'command') {
      const value = await this.port.run((client) => client.query(route.method, params))
      return (route.map === undefined ? value : route.map(value, req)) as InvokeRes<K>
    }
    const receipt = await this.port.run((client) => client.command(route.method, params, `ncw-${randomUUID()}`)) as Receipt
    const value = settle(receipt)
    if (route.finish !== undefined) {
      const finish = route.finish
      return await this.port.run((client) => finish(client, value, req))
    }
    return (route.result === undefined ? value : route.result(value)) as InvokeRes<K>
  }
}

/** 回执 → 结果。只有 `succeeded` 才算成功;失败带设备给的错误码,其余状态(中断/未知)如实报告。 */
function settle(receipt: Receipt): DeviceValue {
  if (receipt.status === 'succeeded') return (receipt.result ?? null) as DeviceValue
  if (receipt.status === 'failed') {
    const error = (receipt.result as { error?: { code?: unknown; message?: unknown } } | null)?.error
    const code = typeof error?.code === 'string' ? error.code as DeviceErrorCode : 'internal'
    throw new DeviceProtocolError(code, typeof error?.message === 'string' ? error.message : 'The device rejected the request')
  }
  throw new DeviceProtocolError('internal', `The device did not complete the request (${String(receipt.status)})`)
}

function refused(): DeviceProtocolError {
  return new DeviceProtocolError('forbidden', 'This action is not available on the selected device yet')
}
