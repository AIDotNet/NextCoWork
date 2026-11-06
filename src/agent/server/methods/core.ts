/**
 * 第一批业务方法:会话读取、发起/停止 Agent、会话队列。
 *
 * 全部通过 `src/main/services/*` 与 `store`,没有 Electron。参数 schema 在
 * `shared/device/methods.ts`(网络白名单)。mutation 一律是 `execution`:先落「已接受」
 * 回执,再由服务端执行,控制端断开不会中止已接受的任务。
 */
import { randomUUID } from 'node:crypto'
import { z } from 'zod'
import { runs } from '../../../main/kernel/run-registry'
import { UploadManager } from '../../../main/services/workspace-upload'
import {
  abortRun, activeRunIndex, broadcastActiveRuns, installAgentDelivery, prepareRunAttach, queueSessionInput, runHandle, runSnapshot, runTopic, startAgentRun
} from '../../../main/services/agent'
import { getDeviceCommand } from '../../../main/db/device-commands'
import { store } from '../../../main/state/store'
import type { RunRequest, SendOptions } from '../../../shared/agent/run-request'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  agentAbortParams, agentAttachParams, agentDetachParams, agentStartParams, agentWatchParams, agentStartResult, commandsGetParams, DEVICE_METHODS,
  sendOptionsSchema, sessionQueueParams, sessionsCreateParams, sessionsCreateResult, sessionsListParams, sessionsPageParams, type DeviceSendOptions
} from '../../../shared/device/methods'
import { normalizeModeId } from '../../../shared/domain/mode'
import type { DeviceValue } from '../../../shared/device/value'
import { registerCatalogMethods } from './catalog'
import { registerConfigMethods } from './config'
import { registerFileMethods } from './files'
import { registerGitMethods } from './git'
import { registerHookMethods } from './hooks'
import { registerInteractionMethods } from './interactions'
import { registerLeaseMethods } from './leases'
import { registerMcpMethods } from './mcp'
import { registerModelMethods } from './models'
import { registerProviderMethods } from './providers'
import { registerSessionMetadataMethods } from './sessions'
import type { LeaseManager } from '../lease'
import { registerStateMethods } from './state'
import { createTerminalManager, registerTerminalMethods } from './terminals'
import { followRunEvents, type DeviceEventHub } from '../events'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const nullable = z.custom<DeviceValue>((value) => value !== undefined)

/** 业务结果里可能带 undefined / 类实例;经 JSON 规范化后才是合法的设备值。 */
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

function sendOptions(options: DeviceSendOptions): SendOptions {
  return {
    workspaceId: options.workspaceId, depth: 0, mode: options.mode, thinking: options.thinking,
    webSearch: options.webSearch, permissionMode: options.permissionMode, model: options.model,
    skillIds: options.skillIds,
    ...(options.maxContext !== undefined && { maxContext: options.maxContext }),
    ...(options.modelProviderId !== undefined && { modelProviderId: options.modelProviderId }),
    ...(options.skillSelectionMode !== undefined && { skillSelectionMode: options.skillSelectionMode })
  }
}

/** 会话必须存在且属于声明的工作区 —— 控制端报的 id 不构成授权。 */
function requireSession(sessionId: string, workspaceId?: string) {
  const session = store.getSession(sessionId)
  if (session === undefined || (workspaceId !== undefined && session.workspaceId !== workspaceId)) {
    throw new DeviceProtocolError('not_found', 'Session does not exist')
  }
  return session
}

/**
 * 登记核心方法,并把 `hub` 装成 Agent 的事件投递端口。返回卸载函数(服务停止时调用)。
 * ★ 事件投递只是「推送」:没有订阅者 run 照跑,控制端回来用 `agent.attach` 重放。
 */
export function registerCoreMethods(router: DeviceRpcRouter, hub: DeviceEventHub, leases?: LeaseManager,
  onRevoke?: (listener: (controllerId: string) => void) => void): () => void {
  installAgentDelivery(hub)
  registerInteractionMethods(router)
  registerConfigMethods(router)
  // 未完成上传的临时文件由它清理(空闲过期或停服)。
  const uploads = new UploadManager({ newId: () => `up-${randomUUID()}` })
  registerFileMethods(router, uploads)
  // 控制端被撤销:它名下未完成的上传(含临时文件)立即清理,不等空闲过期。
  onRevoke?.((controllerId) => { uploads.dropOwner(controllerId) })
  registerGitMethods(router)
  registerStateMethods(router)
  registerModelMethods(router)
  registerProviderMethods(router)
  registerMcpMethods(router)
  registerCatalogMethods(router)
  registerHookMethods(router)
  registerSessionMetadataMethods(router)
  let offTerminals = (): void => {}
  if (leases !== undefined) {
    registerLeaseMethods(router, leases)
    // 终端的输入走租约闸,所以只有在有租约管理器时才开放。
    offTerminals = registerTerminalMethods(router, createTerminalManager(), leases)
  }
  // 「谁在跑」是应用级事实:run 开始/结束都广播给所有 watcher(桌面在 registerIpc 里接的同一条)。
  const offActiveChange = runs.onActiveChange(broadcastActiveRuns)
  router.query(DEVICE_METHODS.commandsGet, commandsGetParams, nullable, (context, { key }) =>
    plain(getDeviceCommand(context, key)))

  router.query(DEVICE_METHODS.sessionsList, sessionsListParams, anyValue, (_context, params) => {
    if (store.getWorkspace(params.workspaceId) === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
    const running = new Set(runs.activeRunIds().map((id) => runs.get(id)?.sessionId))
    return plain(store.listSessions(params.workspaceId, params.archived).map((item) => ({ ...item, running: running.has(item.id) })))
  })

  router.query(DEVICE_METHODS.sessionsPage, sessionsPageParams, anyValue, (_context, params) => {
    const page = store.getSessionPage(params.sessionId, params.limit, params.beforeMessageId)
    if (page === undefined) throw new DeviceProtocolError('not_found', 'Session does not exist')
    return plain(page)
  })

  router.execution(DEVICE_METHODS.sessionsCreate, sessionsCreateParams, sessionsCreateResult, (_execution, params) => {
    const workspace = store.getWorkspace(params.workspaceId)
    if (workspace === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
    if (params.sessionId !== undefined) {
      // 同 id 已存在但属于别的工作区 = 冲突,不能借 ensure 的「已存在即返回」读到它。
      const existing = store.getSession(params.sessionId)
      if (existing !== undefined && existing.workspaceId !== params.workspaceId) {
        throw new DeviceProtocolError('conflict', 'Session id is already in use')
      }
    }
    const session = store.ensureSession({
      ...(params.sessionId !== undefined && { id: params.sessionId }),
      workspaceId: params.workspaceId, ...(params.title !== undefined && { title: params.title }),
      mode: normalizeModeId(params.mode ?? workspace.settings.defaultMode), rootPathAtCreation: workspace.rootPath
    })
    return { sessionId: session.id }
  })

  router.execution(DEVICE_METHODS.agentStart, agentStartParams, agentStartResult, (_execution, params) => {
    const session = requireSession(params.sessionId, params.options.workspaceId)
    if (store.getWorkspace(session.workspaceId) === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
    if (runs.get(params.runId) !== undefined) throw new DeviceProtocolError('conflict', 'Run id is already in use')
    const request: RunRequest = {
      ...sendOptions(sendOptionsSchema.parse(params.options)),
      runId: params.runId, sessionId: params.sessionId, input: [{ type: 'text', text: params.text }]
    }
    const started = startAgentRun(request)
    return started.started ? { started: true as const, runId: params.runId } : { started: false as const, queued: true as const }
  })

  router.execution(DEVICE_METHODS.agentAbort, agentAbortParams, z.strictObject({ aborted: z.boolean() }), (_execution, params) => {
    if (runs.get(params.runId) === undefined) return { aborted: false }
    abortRun(params.runId, params.cascade ?? true)
    return { aborted: true }
  })

  router.execution(DEVICE_METHODS.sessionQueue, sessionQueueParams, anyValue, (_execution, params) => {
    requireSession(params.sessionId)
    const op = params.op
    const result = queueSessionInput({
      sessionId: params.sessionId,
      op: op.kind === 'enqueue'
        ? { kind: 'enqueue', text: op.text, options: sendOptions(op.options), attachments: [] }
        : op
    })
    return plain(result)
  })

  router.query(DEVICE_METHODS.agentAttach, agentAttachParams, anyValue, (context, params) => {
    const handle = runHandle(params.runId)
    if (handle === undefined) throw new DeviceProtocolError('not_found', 'Run does not exist')
    // 与桌面一致的三步:先冲掉泵里攒着的事件、再订阅、最后取快照。三步同步,中间插不进事件。
    prepareRunAttach(handle)
    const snapshot = runSnapshot(handle, params.sinceSeq)
    followRunEvents(hub, context.connection, runTopic(params.runId), `run:${params.runId}`, snapshot.seq)
    return plain(snapshot)
  })

  router.query(DEVICE_METHODS.agentDetach, agentDetachParams, z.strictObject({ detached: z.literal(true) }), (context, params) => {
    context.connection.unsubscribe(`run:${params.runId}`)
    return { detached: true }
  })

  router.query(DEVICE_METHODS.agentWatch, agentWatchParams, anyValue, (context) => {
    let cursor = 0
    const connection = context.connection
    connection.subscribe('global', hub.subscribeGlobal((channel, payload) => {
      const previous = cursor
      cursor += 1
      connection.push({ streamId: 'global', cursor: String(cursor), previousCursor: String(previous), event: channel,
        data: plain(payload) })
    }))
    return plain({ runs: activeRunIndex() })
  })

  return () => { uploads.shutdown(); offTerminals(); offActiveChange(); installAgentDelivery(null) }
}
