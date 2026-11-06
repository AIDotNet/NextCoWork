/**
 * 需求：HTTP 只描述传输，Agent、会话和凭证继续由现有应用拥有。
 * 此窄接口让真实 TCP 测试无需启动 Electron，也不复制一套 Agent 状态。
 */
import type { z } from 'zod'
import type { RunRequest } from '../../shared/agent/run-request'
import type { InteractionResponse, PendingInteraction } from '../../shared/agent/interaction'
import type { GatewayStatus } from '../../shared/domain/provider'
import type { Workspace } from '../../shared/domain/workspace'
import type { Session, SessionDetail, SessionListItem } from '../../shared/domain/session'
import type { RunHandle } from '../kernel/run-registry'
import type { runInput, sessionInput } from './validation'

export type RunInput = z.infer<typeof runInput>
export type SessionInput = z.infer<typeof sessionInput>

export interface GatewayBackend {
  status(): GatewayStatus
  workspaces(): Workspace[]
  models(): Array<{ id: string; providerId: string }>
  sessions(workspaceId: string): SessionListItem[]
  session(sessionId: string): SessionDetail
  createSession(input: SessionInput): Session
  start(input: RunInput): RunHandle
  run(runId: string): RunHandle
  abort(runId: string): void
  interactions(runId: string): PendingInteraction[]
  respond(response: InteractionResponse): void
}

/** 需求：外部调用不能指定子 run 字段；由会话/工作区元数据合成内部请求。 */
export function requestFor(input: RunInput, detail: SessionDetail, workspace: Workspace, runId: string): RunRequest {
  const session = detail.session
  const defaults = workspace.settings
  const model = input.model ?? (session.model.length > 0 ? session.model : defaults.defaultModel)
  // 显式换模型但没锁供应商时重新按路由优先级解析，不能沿用上个模型的供应商。
  const providerId = input.modelProviderId ?? (input.model === undefined
    ? session.modelProviderId ?? defaults.defaultModelProviderId
    : undefined)
  return {
    runId, sessionId: session.id, workspaceId: workspace.id, depth: 0,
    input: [{ type: 'text', text: input.text }],
    model, ...(providerId === undefined ? {} : { modelProviderId: providerId }),
    mode: input.mode ?? session.mode ?? defaults.defaultMode,
    thinking: input.thinking ?? session.thinking ?? defaults.defaultThinking,
    permissionMode: input.permissionMode ?? defaults.permissionMode,
    webSearch: input.webSearch ?? defaults.webSearch,
    maxContext: defaults.maxContext === true,
    skillIds: defaults.activeSkillIds,
    ...(defaults.skillSelectionMode === undefined ? {} : { skillSelectionMode: defaults.skillSelectionMode })
  }
}
