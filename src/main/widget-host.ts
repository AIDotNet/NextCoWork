/**
 * widget 的**主进程侧能力** —— 预先声明的动作、`complete()`、持久化状态。
 *
 * 渲染层的 `WidgetFrame` 收到 iframe 的请求后经 IPC 打到这里(`ipc/widget.ts`)。
 * 三条不变式:
 *
 * 1. **动作的定义只从转录里取。** 渲染层只给 `(sessionId, callId, actionId, params)`,
 *    工具名与入参由这里从那次工具调用的卡片里读出来、再代参 —— iframe 和渲染层都
 *    改不了"要跑什么"。
 * 2. **非只读动作第一次一定问。** 即使工作区开着自动档:widget 是模型写的,按钮背后
 *    是它预先写好的命令,用户此前没为它点过头。确认框由宿主画、显示真实的工具和入参;
 *    确认过一次的 (卡片, 动作, 绑定后的入参) 在进程内记住。
 * 3. **输出不进模型上下文。** 结果只回到那张 widget。
 */
import { matchPermissionRules } from '../shared/agent/permission-rule'
import { truncateToolOutput, userMessage } from '../shared/agent/message'
import { auxiliaryThinkingLevel, resolveModelThinking } from '../shared/domain/model-runtime'
import {
  WIDGET_LIMITS,
  bindWidgetAction,
  type WidgetAction,
  type WidgetActionPreflight,
  type WidgetActionResult
} from '../shared/domain/widget'
import { ulid } from '../shared/util/id'
import { runHookEvent } from './hooks'
import { evaluate, NETWORK_SWITCH_TOOLS } from './kernel/permission-gate'
import { readLocalSettings } from './kernel/local-settings'
import type { ToolContext } from './kernel/tool/registry'
import { getEnvironments, getHost, getTools, getWidgetUpstream } from './runtime'
import { store } from './state/store'

const ACTION_TIMEOUT_MS = 5 * 60_000
const COMPLETE_TIMEOUT_MS = 90_000

/** 某次工具调用产出的那张 widget 卡片。先找结果,找不到(交互模式还挂着)再看调用入参。 */
export function findWidgetCard(sessionId: string, callId: string): { title: string; actions: WidgetAction[] } | undefined {
  const history = store.getHistory(sessionId)
  for (let m = history.length - 1; m >= 0; m--) {
    for (const part of history[m]?.parts ?? []) {
      if (part.type === 'tool_result' && part.callId === callId) {
        const card = part.output.card
        return card?.kind === 'widget' ? { title: card.title, actions: card.actions ?? [] } : undefined
      }
    }
  }
  /*
    交互模式的工具还没返回,结果不在转录里 —— 动作定义取调用入参。
    ★ 入参来自模型,但它已经过了工具 schema 的同一套校验才走到"挂起"这一步;
    这里只认 `visualize_show_widget` 这一个名字(编辑工具不能开交互)。
  */
  for (let m = history.length - 1; m >= 0; m--) {
    for (const part of history[m]?.parts ?? []) {
      if (part.type !== 'tool_call' || part.callId !== callId) continue
      const tool = getTools().resolveByExternalName(part.name)
      if (tool?.internalId !== 'visualize_show_widget') return undefined
      const input = part.input as { title?: unknown; actions?: unknown } | null
      return {
        title: typeof input?.title === 'string' ? input.title : '',
        actions: Array.isArray(input?.actions) ? (input.actions as WidgetAction[]) : []
      }
    }
  }
  return undefined
}

/** 用户确认过的动作。键 = 会话 + 卡片 + 绑定后的入参,入参一变就要重新问。 */
const confirmed = new Set<string>()

function confirmKey(sessionId: string, callId: string, tool: string, input: unknown): string {
  return `${sessionId}\u0000${callId}\u0000${tool}\u0000${JSON.stringify(input)}`
}

export async function runWidgetAction(request: {
  sessionId: string
  callId: string
  actionId: string
  params: Record<string, unknown>
  confirmed: boolean
}): Promise<WidgetActionPreflight | ({ kind: 'result' } & WidgetActionResult)> {
  const session = store.getSession(request.sessionId)
  if (session === undefined) return { kind: 'deny', reason: 'The conversation no longer exists.' }
  const card = findWidgetCard(request.sessionId, request.callId)
  const action = card?.actions.find((entry) => entry.id === request.actionId)
  if (action === undefined) return { kind: 'deny', reason: `No action "${request.actionId}" was declared for this widget.` }

  let input: Record<string, unknown>
  try {
    input = bindWidgetAction(action, request.params)
  } catch (err) {
    return { kind: 'deny', reason: err instanceof Error ? err.message : String(err) }
  }
  const tool = getTools().byInternalId(action.tool) ?? getTools().resolveByExternalName(action.tool)
  if (tool === undefined) return { kind: 'deny', reason: `The tool ${action.tool} is not available.` }

  const workspace = store.getWorkspace(session.workspaceId)
  if (workspace === undefined) return { kind: 'deny', reason: 'The workspace no longer exists.' }

  const { environment, release } = getEnvironments().acquire(session.workspaceId)
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), ACTION_TIMEOUT_MS)
  timer.unref?.()
  const runId = `widget_${ulid()}`
  try {
    environment.assertReady()
    const host = getHost()

    // ── 判定:同一张表(档位 → deny 规则 → 钩子 → ask/allow 规则),外加不变式 2 ──
    const gate = evaluate({
      mode: workspace.settings.permissionMode,
      readOnly: tool.readOnly,
      destructive: tool.destructive,
      needsNetwork: NETWORK_SWITCH_TOOLS.has(tool.internalId),
      webSearch: workspace.settings.webSearch
    })
    if (gate.kind === 'deny') return { kind: 'deny', reason: gate.reason }
    const scope = environment.remote ? { path: environment.path, namespace: environment.key } : undefined
    const local = await readLocalSettings(environment.remote ? environment.fs : host.fs, environment.rootPath, host.logger, scope)
    const denyRule = matchPermissionRules(local.permissions.deny, tool.internalId, input)
    if (denyRule !== null) return { kind: 'deny', reason: `Blocked by the workspace rule \`${denyRule}\`.` }

    const reports = await runHookEvent({
      event: 'PreToolUse',
      environment,
      sessionId: request.sessionId,
      runId,
      workspaceId: session.workspaceId,
      tool: { internalId: tool.internalId, externalName: tool.externalName, input },
      signal: controller.signal
    })
    const blocked = reports.find((report) => report.outcome === 'blocked' || report.decision === 'deny')
    if (blocked !== undefined) return { kind: 'deny', reason: blocked.reason ?? 'A PreToolUse hook blocked this action.' }

    const key = confirmKey(request.sessionId, request.callId, tool.internalId, input)
    const askRule = matchPermissionRules(local.permissions.ask, tool.internalId, input)
    const allowRule = matchPermissionRules(local.permissions.allow, tool.internalId, input)
    const mustAsk =
      askRule !== null ||
      reports.some((report) => report.decision === 'ask') ||
      (!tool.readOnly && allowRule === null) ||
      (gate.kind === 'ask' && allowRule === null)
    if (mustAsk && !confirmed.has(key)) {
      if (!request.confirmed) return { kind: 'ask', toolName: tool.internalId, input }
      confirmed.add(key)
    }

    // ── 执行 ──
    const callId = `widget_${ulid()}`
    const ctx: ToolContext = {
      sessionId: request.sessionId,
      workspaceId: session.workspaceId,
      workspaceRoot: environment.rootPath,
      signal: controller.signal,
      permissionMode: workspace.settings.permissionMode,
      // ★ 0 而不是 1:深度 1 会让 Read/Bash 的某些子代理限制误伤。会挂起的工具在声明时已被拒
      depth: 0,
      callId,
      runId,
      host: {
        fs: environment.fs,
        spawn: environment.spawn,
        path: environment.path,
        remote: environment.remote,
        platform: environment.platform,
        fetch: host.fetch,
        clock: host.clock,
        logger: host.logger
      },
      emit: () => {}
    }
    const result = await tool.execute(input, ctx)
    const output = truncateToolOutput(result.output.content).content
    return { kind: 'result', ok: !result.isError, output }
  } catch (err) {
    const message = controller.signal.aborted ? 'The action timed out.' : err instanceof Error ? err.message : String(err)
    return { kind: 'result', ok: false, output: message }
  } finally {
    clearTimeout(timer)
    controller.abort()
    release()
  }
}

/**
 * widget 的一次模型调用:没有工具、不进对话、模型跟着这个会话走。
 * 允不允许由渲染层问用户(每张卡第一次),这里只管调用与上限。
 */
export async function completeForWidget(request: {
  sessionId: string
  prompt: string
  system?: string
  maxTokens?: number
}): Promise<string> {
  const session = store.getSession(request.sessionId)
  if (session === undefined) throw new Error('The conversation no longer exists.')
  if (request.prompt.length > WIDGET_LIMITS.completePrompt) throw new Error('The prompt is too long.')
  if ((request.system?.length ?? 0) > WIDGET_LIMITS.completeSystem) throw new Error('The system prompt is too long.')
  const upstream = getWidgetUpstream()
  const alias = upstream.resolveModel(session.model, session.modelProviderId)
  if (alias === undefined) throw new Error('No model is available for this conversation.')
  const maxOutputTokens = Math.min(alias.maxOutputTokens, Math.max(64, Math.min(request.maxTokens ?? 1024, 4096)))
  const thinkingLevel = auxiliaryThinkingLevel('off', alias)
  const reasoning =
    resolveModelThinking(thinkingLevel, alias.thinkingConfig, maxOutputTokens, alias.reasoningEfforts) ??
    { mode: 'toggle' as const, enabled: false, explicit: true }

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), COMPLETE_TIMEOUT_MS)
  timer.unref?.()
  let text = ''
  try {
    for await (const event of upstream.stream(
      {
        model: session.model,
        ...(session.modelProviderId === undefined ? {} : { modelProviderId: session.modelProviderId }),
        system:
          request.system ??
          'You are a helper called from an interactive widget inside a chat app. Answer the request directly and concisely.',
        messages: [userMessage(ulid(), [{ type: 'text', text: request.prompt }], Date.now())],
        tools: [],
        maxOutputTokens,
        thinkingLevel,
        reasoning
      },
      controller.signal,
      { workspaceId: session.workspaceId, sessionId: request.sessionId, runId: `widget_complete_${ulid()}` }
    )) {
      if (event.type === 'error') throw new Error('The model request failed.')
      if (event.type === 'text_delta') text += event.text
    }
  } catch (err) {
    throw new Error(controller.signal.aborted ? 'The model request timed out.' : err instanceof Error ? err.message : String(err), { cause: err })
  } finally {
    clearTimeout(timer)
    controller.abort()
  }
  return text
}

// ── 持久化状态:一个会话一行 kv,callId → 状态 ──

const stateKey = (sessionId: string): string => `session.widget.${sessionId}`
/** 一个会话里所有 widget 状态加起来的上限。超了丢最旧的那几张 —— 它们多半早就滚出屏幕了 */
const SESSION_STATE_BUDGET = 256 * 1024

type StateDoc = Record<string, { at: number; state: unknown }>

export function getWidgetState(sessionId: string, callId: string): unknown {
  const doc = store.getKv<StateDoc>(stateKey(sessionId), {})
  return doc[callId]?.state ?? null
}

export function setWidgetState(sessionId: string, callId: string, state: unknown): void {
  if (store.getSession(sessionId) === undefined) throw new Error('The conversation no longer exists.')
  const json = JSON.stringify(state ?? null)
  if (json.length > WIDGET_LIMITS.state) throw new Error(`Widget state is limited to ${String(WIDGET_LIMITS.state)} characters.`)
  const doc = { ...store.getKv<StateDoc>(stateKey(sessionId), {}) }
  doc[callId] = { at: Date.now(), state: JSON.parse(json) as unknown }
  const entries = Object.entries(doc).sort((a, b) => b[1].at - a[1].at)
  let total = 0
  const kept: StateDoc = {}
  for (const [id, entry] of entries) {
    total += JSON.stringify(entry).length
    if (total > SESSION_STATE_BUDGET && id !== callId) continue
    kept[id] = entry
  }
  store.setKv(stateKey(sessionId), kept)
}
