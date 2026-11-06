/**
 * 输入框补全的三条频道:建议下一步(L2)、历史指令(L1)、打字续写(L3)。
 * 设计见 `docs/composer-autocomplete.md`。
 *
 * ★ **一切失败都是「没有建议」。** 补全是便利,不是功能 —— 这里不抛任何会在输入框上
 *   弹出来的错,拿不准就返回空串。渲染层同样把任何拒绝当成空串。
 *
 * ★ 依赖全部注入(`createComposerIpc(deps)`),这个文件不 import electron、不 import runtime:
 *   去重、取消、窗口销毁这几条最容易写错的时序规则,要能在 node 单测里直接跑。
 */
import type { AgentMessage } from '../../shared/agent/message'
import { visibleText } from '../../shared/agent/message'
import type { ComposerCompletionContext } from '../../shared/domain/composer-completion'
import type { AppSettings } from '../../shared/domain/settings'
import type { Session } from '../../shared/domain/session'
import type { InlineCompletionRequest } from '../inline-completion'
import { buildPromptSuggestionContext, type PromptSuggestionInput } from '../prompt-suggestion'

/** 只用到 `WindowContext` 的这两样;写成结构类型,单测不必伪造一个 WebContents。 */
export interface ComposerWindow {
  id: number
  sender: { once(event: 'destroyed', listener: () => void): unknown }
}

export interface SuggestNextRequest {
  requestId: string
  workspaceId: string
  sessionId: string
  /** 渲染层看到的那次 `runEndedAt`。主进程按 (sessionId, triggerKey) 去重 —— 两个窗口开着同一条会话只发一次 */
  triggerKey: number
  /** 输入框此刻的生效模型。设置里没点名补全模型时用它,不换家 */
  model: string
  modelProviderId?: string
}

export interface CompleteInlineRequest {
  requestId: string
  workspaceId: string
  sessionId: string | null
  draft: string
  context: ComposerCompletionContext
}

export interface ComposerIpcDeps {
  settings: () => AppSettings
  session: (id: string) => Session | undefined
  hasActiveGoal: (sessionId: string) => boolean
  recentSessionPrompts: (sessionId: string, limit: number) => AgentMessage[]
  lastVisibleAssistantMessage: (sessionId: string) => AgentMessage | undefined
  recentWorkspacePrompts: (workspaceId: string) => string[]
  suggest: (input: PromptSuggestionInput, signal: AbortSignal) => Promise<string>
  complete: (input: InlineCompletionRequest, signal: AbortSignal) => Promise<string>
  logger: { warn(msg: string): void }
  suggestTimeoutMs?: number
  inlineTimeoutMs?: number
}

const SUGGEST_TIMEOUT_MS = 20_000
const INLINE_TIMEOUT_MS = 4_000
const DRAFT_LIMIT = 2_000

function validId(id: unknown): id is string {
  return typeof id === 'string' && id.length >= 8 && id.length <= 128
}

interface SuggestionJob {
  triggerKey: number
  controller: AbortController
  promise: Promise<string>
  waiters: Set<string>
}

/**
 * L2 的单飞 + 合流。
 *
 * - 同一会话、同一个 triggerKey:**合流**,所有等待者拿同一个结果(多窗口、重复请求只花一次钱);
 * - 同一会话、新的 triggerKey:旧的作废(abort),它的等待者拿到 `''`;
 * - 等待者取消或窗口关掉:摘掉它;**最后一个**等待者离开时才真正 abort。
 */
export class SuggestionJobs {
  private readonly jobs = new Map<string, SuggestionJob>()
  private readonly waiterIndex = new Map<string, string>()

  constructor(private readonly timeoutMs: number = SUGGEST_TIMEOUT_MS) {}

  run(sessionId: string, triggerKey: number, waiter: string, work: (signal: AbortSignal) => Promise<string>): Promise<string> {
    const existing = this.jobs.get(sessionId)
    if (existing !== undefined && existing.triggerKey === triggerKey && !existing.controller.signal.aborted) {
      existing.waiters.add(waiter)
      this.waiterIndex.set(waiter, sessionId)
      return this.settle(existing, waiter)
    }
    if (existing !== undefined) this.abort(sessionId, existing)

    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), this.timeoutMs)
    ;(timer as { unref?: () => void }).unref?.()
    const job: SuggestionJob = {
      triggerKey,
      controller,
      waiters: new Set([waiter]),
      promise: work(controller.signal)
        .catch(() => '')
        .finally(() => {
          clearTimeout(timer)
          if (this.jobs.get(sessionId) === job) this.jobs.delete(sessionId)
          for (const w of job.waiters) if (this.waiterIndex.get(w) === sessionId) this.waiterIndex.delete(w)
        })
    }
    this.jobs.set(sessionId, job)
    this.waiterIndex.set(waiter, sessionId)
    return this.settle(job, waiter)
  }

  /** 摘掉一个等待者。返回 true = 它确实在等。 */
  cancel(waiter: string): boolean {
    const sessionId = this.waiterIndex.get(waiter)
    if (sessionId === undefined) return false
    this.waiterIndex.delete(waiter)
    const job = this.jobs.get(sessionId)
    if (job === undefined) return true
    job.waiters.delete(waiter)
    if (job.waiters.size === 0) this.abort(sessionId, job)
    return true
  }

  /** 窗口关掉:它的所有等待者一起摘掉。 */
  dropWindow(windowId: number): void {
    for (const waiter of [...this.waiterIndex.keys()]) if (waiter.startsWith(`${String(windowId)}:`)) this.cancel(waiter)
  }

  private abort(sessionId: string, job: SuggestionJob): void {
    job.controller.abort()
    if (this.jobs.get(sessionId) === job) this.jobs.delete(sessionId)
  }

  /** 等待者被摘掉(或整份作废)之后,拿到的必须是 `''`,而不是那份它已经不要的结果 */
  private async settle(job: SuggestionJob, waiter: string): Promise<string> {
    const text = await job.promise
    if (!job.waiters.has(waiter) || job.controller.signal.aborted) return ''
    return text
  }
}

/** L3 的单飞:每个(窗口, 会话)一个槽,新请求让旧的立刻作废并拿到 `''`。 */
class InlineSlots {
  private readonly slots = new Map<string, { requestId: string; controller: AbortController }>()

  acquire(slot: string, requestId: string): AbortController {
    this.slots.get(slot)?.controller.abort()
    const controller = new AbortController()
    this.slots.set(slot, { requestId, controller })
    return controller
  }

  release(slot: string, requestId: string): void {
    if (this.slots.get(slot)?.requestId === requestId) this.slots.delete(slot)
  }

  cancel(windowId: number, requestId: string): boolean {
    for (const [slot, entry] of this.slots) {
      if (slot.startsWith(`${String(windowId)}:`) && entry.requestId === requestId) {
        entry.controller.abort()
        this.slots.delete(slot)
        return true
      }
    }
    return false
  }

  dropWindow(windowId: number): void {
    for (const [slot, entry] of [...this.slots]) {
      if (slot.startsWith(`${String(windowId)}:`)) { entry.controller.abort(); this.slots.delete(slot) }
    }
  }
}

/**
 * 能不能为这条会话猜下一句。主进程这一侧**再判一次**渲染层判过的条件:
 * 渲染层的判断只是省一次 IPC,真正的口径在这里(频道可以被直接调用)。
 */
function suggestableSession(session: Session | undefined, workspaceId: string): session is Session {
  return session !== undefined
    && session.workspaceId === workspaceId
    && session.parentSessionId === undefined
    && session.skillSource === undefined
    && session.mode !== 'plan'
}

export interface ComposerIpc {
  suggestNext(req: SuggestNextRequest, ctx: ComposerWindow): Promise<{ requestId: string; text: string }>
  history(req: { workspaceId: string }): { prompts: string[] }
  complete(req: CompleteInlineRequest, ctx: ComposerWindow): Promise<{ requestId: string; text: string }>
  cancel(req: { requestId: string }, ctx: ComposerWindow): void
}

export function createComposerIpc(deps: ComposerIpcDeps): ComposerIpc {
  const jobs = new SuggestionJobs(deps.suggestTimeoutMs ?? SUGGEST_TIMEOUT_MS)
  const slots = new InlineSlots()
  const observed = new Set<number>()

  function observe(ctx: ComposerWindow): void {
    if (observed.has(ctx.id)) return
    observed.add(ctx.id)
    ctx.sender.once('destroyed', () => {
      observed.delete(ctx.id)
      jobs.dropWindow(ctx.id)
      slots.dropWindow(ctx.id)
    })
  }

  async function suggestNext(req: SuggestNextRequest, ctx: ComposerWindow): Promise<{ requestId: string; text: string }> {
    const requestId = validId(req?.requestId) ? req.requestId : ''
    const none = { requestId, text: '' }
    try {
      if (requestId === '' || typeof req.sessionId !== 'string' || typeof req.workspaceId !== 'string'
        || typeof req.triggerKey !== 'number' || !Number.isFinite(req.triggerKey)) return none
      const settings = deps.settings()
      if (!settings.promptSuggestions) return none
      const session = deps.session(req.sessionId)
      if (!suggestableSession(session, req.workspaceId)) return none
      // 目标还挂着 = 判定器马上会再踢一轮,这时冒出一句建议只会一闪即逝
      if (deps.hasActiveGoal(session.id)) return none
      const named = settings.completionModel !== ''
      const model = named ? settings.completionModel : typeof req.model === 'string' ? req.model : ''
      const modelProviderId = named
        ? settings.completionModelProviderId
        : typeof req.modelProviderId === 'string' ? req.modelProviderId : undefined
      if (model === '') return none
      const context = buildPromptSuggestionContext(
        deps.recentSessionPrompts(session.id, 20),
        deps.lastVisibleAssistantMessage(session.id)
      )
      if (context === null) return none
      observe(ctx)
      const text = await jobs.run(session.id, req.triggerKey, `${String(ctx.id)}:${requestId}`, (signal) =>
        deps.suggest({
          workspaceId: session.workspaceId,
          sessionId: session.id,
          model,
          ...(modelProviderId === undefined ? {} : { modelProviderId }),
          context
        }, signal))
      return { requestId, text }
    } catch (error) {
      deps.logger.warn(`[composer] 建议下一步失败:${error instanceof Error ? error.message : String(error)}`)
      return none
    }
  }

  function history(req: { workspaceId: string }): { prompts: string[] } {
    try {
      if (typeof req?.workspaceId !== 'string' || !deps.settings().localCompletion) return { prompts: [] }
      return { prompts: deps.recentWorkspacePrompts(req.workspaceId) }
    } catch (error) {
      deps.logger.warn(`[composer] 读取历史指令失败:${error instanceof Error ? error.message : String(error)}`)
      return { prompts: [] }
    }
  }

  async function complete(req: CompleteInlineRequest, ctx: ComposerWindow): Promise<{ requestId: string; text: string }> {
    const requestId = validId(req?.requestId) ? req.requestId : ''
    const none = { requestId, text: '' }
    if (requestId === '' || typeof req.workspaceId !== 'string' || typeof req.draft !== 'string'
      || req.draft.length === 0 || req.draft.length > DRAFT_LIMIT
      || (req.sessionId !== null && typeof req.sessionId !== 'string')) return none
    const settings = deps.settings()
    // ★ 两个条件都要:开关开着,而且**点名**了模型 —— 这一层没有「跟随会话」
    if (!settings.inlineCompletion || settings.inlineCompletionModel === '') return none

    const slot = `${String(ctx.id)}:${req.sessionId ?? '-'}`
    observe(ctx)
    const controller = slots.acquire(slot, requestId)
    const timer = setTimeout(() => controller.abort(), deps.inlineTimeoutMs ?? INLINE_TIMEOUT_MS)
    ;(timer as { unref?: () => void }).unref?.()
    try {
      let recentPrompts: string[] = []
      let lastAgentText = ''
      let sessionId: string | undefined
      if (req.sessionId !== null) {
        const session = deps.session(req.sessionId)
        if (session !== undefined && session.workspaceId === req.workspaceId && session.parentSessionId === undefined) {
          sessionId = session.id
          recentPrompts = deps.recentSessionPrompts(session.id, 3).map((m) => visibleText(m).trim()).filter((t) => t !== '').reverse()
          const last = deps.lastVisibleAssistantMessage(session.id)
          lastAgentText = last === undefined ? '' : visibleText(last)
        }
      }
      const context = sanitizeContext(req.context)
      const text = await deps.complete({
        workspaceId: req.workspaceId,
        ...(sessionId === undefined ? {} : { sessionId }),
        model: settings.inlineCompletionModel,
        ...(settings.inlineCompletionModelProviderId === undefined ? {} : { modelProviderId: settings.inlineCompletionModelProviderId }),
        context: { ...context, recentPrompts, lastAgentText },
        draft: req.draft
      }, controller.signal)
      return controller.signal.aborted ? none : { requestId, text }
    } catch (error) {
      if (!controller.signal.aborted) {
        deps.logger.warn(`[composer] 打字续写失败:${error instanceof Error ? error.message : String(error)}`)
      }
      return none
    } finally {
      clearTimeout(timer)
      slots.release(slot, requestId)
    }
  }

  function cancel(req: { requestId: string }, ctx: ComposerWindow): void {
    if (!validId(req?.requestId)) return
    if (!jobs.cancel(`${String(ctx.id)}:${req.requestId}`)) slots.cancel(ctx.id, req.requestId)
  }

  return { suggestNext, history, complete, cancel }
}

/** 渲染层递来的上下文只信形状:类型不对的项丢掉,数量与长度在 `formatInlineCompletionInput` 里再截。 */
function sanitizeContext(raw: unknown): Pick<InlineCompletionRequest['context'], 'project' | 'branch' | 'files' | 'commands'> {
  const v = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>
  const strings = (x: unknown): string[] => (Array.isArray(x) ? x.filter((s): s is string => typeof s === 'string').slice(0, 50) : [])
  return {
    project: typeof v.project === 'string' ? v.project : '',
    ...(typeof v.branch === 'string' && v.branch !== '' ? { branch: v.branch } : {}),
    files: strings(v.files),
    commands: strings(v.commands)
  }
}
