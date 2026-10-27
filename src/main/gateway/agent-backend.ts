/**
 * 需求：HTTP 与桌面共用会话、RunRegistry、模型路由和交互门；不建立第二份
 * Agent 生命周期。Electron 相关的启动/取消/广播由装配点注入。
 */
import type { InteractionResponse, PendingInteraction } from '../../shared/agent/interaction'
import type { RunRequest } from '../../shared/agent/run-request'
import type { GatewayStatus } from '../../shared/domain/provider'
import type { Session, SessionDetail, SessionListItem } from '../../shared/domain/session'
import type { Workspace } from '../../shared/domain/workspace'
import { normalizeModeId } from '../../shared/domain/mode'
import { ulid } from '../../shared/util/id'
import { interactions } from '../kernel/interaction-gate'
import { runs, type RunHandle } from '../kernel/run-registry'
import { getRouter } from '../runtime'
import { store } from '../state/store'
import { HttpError } from './validation'
import { requestFor, type GatewayBackend, type RunInput, type SessionInput } from './backend'

export class AgentBackend implements GatewayBackend {
  constructor(private readonly host: {
    /** 桌面与 HTTP 共用启动闸门，包含强制更新、导入脱离和事件泵。 */
    start(request: RunRequest): RunHandle
    /** 保留现有目标暂停与子代理级联中断规则。 */
    abort(runId: string): void
    /** 状态由监听器持有，不能在 adapter 内复制。 */
    status(): GatewayStatus
    /** 新建会话立刻进入桌面列表。 */
    created(session: Session): void
  }) {}

  status(): GatewayStatus { return this.host.status() }
  workspaces(): Workspace[] { return store.listWorkspaces() }
  models(): Array<{ id: string; providerId: string }> {
    return getRouter().listModels().map((model) => ({ id: model.alias, providerId: model.providerId }))
  }

  private workspace(id: string): Workspace {
    const workspace = store.getWorkspace(id)
    if (workspace === undefined) throw new HttpError(404, 'not_found', 'Workspace not found')
    return workspace
  }

  sessions(workspaceId: string): SessionListItem[] {
    this.workspace(workspaceId)
    const active = new Set(runs.activeRunIds().map((id) => runs.get(id)?.sessionId))
    return store.listSessions(workspaceId).map((session) => ({ ...session, running: active.has(session.id) }))
  }

  session(sessionId: string): SessionDetail {
    const detail = store.getSessionDetail(sessionId)
    if (detail === undefined) throw new HttpError(404, 'not_found', 'Session not found')
    this.workspace(detail.session.workspaceId)
    return detail
  }

  createSession(input: SessionInput): Session {
    const workspace = this.workspace(input.workspaceId)
    const session = store.ensureSession({
      workspaceId: workspace.id,
      ...(input.title === undefined ? {} : { title: input.title }),
      mode: normalizeModeId(workspace.settings.defaultMode),
      model: workspace.settings.defaultModel,
      ...(workspace.settings.defaultModelProviderId === undefined ? {} : { modelProviderId: workspace.settings.defaultModelProviderId }),
      thinking: workspace.settings.defaultThinking,
      rootPathAtCreation: workspace.rootPath
    })
    this.host.created(session)
    return session
  }

  start(input: RunInput): RunHandle {
    const detail = this.session(input.sessionId)
    if (detail.session.parentSessionId !== undefined) throw new HttpError(400, 'invalid_request', 'Start a top-level conversation session')
    const workspace = this.workspace(detail.session.workspaceId)
    if (workspace.unavailable === true) throw new HttpError(409, 'workspace_unavailable', 'Workspace is unavailable')
    const request = requestFor(input, detail, workspace, ulid())
    if (getRouter().resolveModel(request.model, request.modelProviderId) === undefined) {
      throw new HttpError(400, 'invalid_model', 'Choose an available model and provider from /v1/models')
    }
    try { return this.host.start(request) }
    catch (error) {
      // 需求：启动校验失败要在 HTTP 层可区分，异步执行失败仍通过 run_end 表达。
      if (error !== null && typeof error === 'object' && 'code' in error && error.code === 'conflict') {
        throw new HttpError(409, 'session_busy', 'Session already has an active run')
      }
      throw new HttpError(503, 'unavailable', error instanceof Error ? error.message : 'Cannot start Agent')
    }
  }

  run(runId: string): RunHandle {
    const handle = runs.get(runId)
    if (handle === undefined || store.getWorkspace(handle.workspaceId) === undefined) {
      throw new HttpError(404, 'not_found', 'Run not found')
    }
    return handle
  }

  abort(runId: string): void { this.run(runId); this.host.abort(runId) }

  interactions(runId: string): PendingInteraction[] {
    const ids = new Set<string>()
    const visit = (id: string): void => {
      if (ids.has(id)) return
      ids.add(id)
      const handle = runs.get(id)
      if (handle !== undefined) for (const child of handle.children) visit(child)
    }
    this.run(runId)
    visit(runId)
    return interactions.list().filter((pending) => ids.has(pending.runId))
  }

  respond(response: InteractionResponse): void {
    const pending = interactions.get(response.id)
    if (pending === undefined) throw new HttpError(409, 'interaction_expired', 'Interaction is no longer pending')
    this.run(pending.runId)
    try { interactions.respond(response) }
    catch (error) { throw new HttpError(400, 'invalid_response', error instanceof Error ? error.message : 'Invalid interaction response') }
  }
}
