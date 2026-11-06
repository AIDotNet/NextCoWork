/**
 * 工具审批的**策略工厂** —— 「这一次工具调用该不该放行,走哪条路放行」。
 *
 * ★★ **它为什么从 `runtime.ts` 搬出来。** 这段逻辑(闸门判定 / 本地规则 /
 * PreToolUse 与 Notification 钩子 / 插件拦截器 / AI 审核器 / 弹窗问人)是一整块
 * 实质职责,和「谁来跑一次 run、谁来组装内核」是两件事。搬到这里之后它只认
 * `ApprovalDeps` 这个注入的窄口,runtime 那边只剩一句组装 —— 而这一段不再
 * 把 runtime 的 3250 行撑得更长。
 *
 * ★ **顺序即语义**(别处不要再解释一遍):
 * 联网开关 → 本地 `deny` → 本地 `ask`(压过下面两步)→ 档位 → 本地 `allow` →
 * AI 审核 → 问人。联网必须排第一 —— 一条写进 `.next-cowork/settings.local.json`
 * 的 allow 规则,不该能把用户亲手关掉的开关重新打开。
 *
 * 判定顺序的实现细节在 `kernel/permission-decision.ts`,那里一条一条写明了
 * 为什么是这个次序。
 */
import type { RunRequest } from '../../shared/agent/run-request'
import { userMessage } from '../../shared/agent/message'
import { matchPermissionRules, suggestPermissionRule } from '../../shared/agent/permission-rule'
import { auxiliaryThinkingLevel } from '../../shared/domain/model-runtime'
import { ulid } from '../../shared/util/id'
import type { KernelHost } from './host'
import type { ApproveFn } from './agent-session'
import type { RunHandle } from './run-registry'
import type { WorkspaceEnvironment } from '../environment/contract'
import { NETWORK_SWITCH_TOOLS, evaluate } from './permission-gate'
import { decideAfterHooks, decideBeforeHooks } from './permission-decision'
import { addLocalPermissionRule, readLocalSettings } from './local-settings'
import type { InteractionGate } from './interaction-gate'
import type { GoalEvaluatorPort } from '../goal/evaluate'
import type { CanonicalRequest } from './upstream/canonical'
import type { HookEventContext } from '../hooks'
import type { HookRunReport } from '../../shared/domain/hook'

/** 插件工具拦截器的输入/输出。★ 与 `runtime.ts` 的 `PluginInterceptor` 结构相同。 */
export type ApprovalPluginInterceptor = (input: {
  toolName: string
  toolInput: unknown
  readOnly: boolean
  destructive: boolean
}) => Promise<{ deny?: string; ask?: boolean }>

/**
 * 审批策略需要、但住在 runtime 那一侧的能力。**注入的窄口,零 runtime import。**
 *
 * 访问器全部**现取**(函数而不是快照值):权限档位是 `handle` 上的活值,本地
 * 规则文件每轮都可能被用户改,审核器模型也在设置页里随时可调。
 */
export interface ApprovalDeps {
  /** 上游 —— 审核器要 `stream` + `resolveModel`。生产是 `getRouter()`。 */
  upstream(): GoalEvaluatorPort
  /** 运行期钩子执行器(`runHookEvent`)。PreToolUse / Notification 走它。 */
  runHooks: (context: HookEventContext) => Promise<HookRunReport[]>
  /** 待决交互闸门(弹窗问人)。 */
  interactions: InteractionGate
  /** 解析一个工作区的模式定义(Plan 工作流要用活值)。 */
  modeFor(workspaceId: string, mode: string): { id: string; tools?: readonly string[] }
  /** 审核器模型那一对(按 run 冻结的来源)。 */
  reviewer(): { model: string; modelProviderId?: string }
  /** 宿主:`clock` / `fs` / `logger`。 */
  host(): KernelHost
  /** 插件拦截器(可空)。 */
  pluginInterceptor(): ApprovalPluginInterceptor | null
}

type ReviewResult = 'allow' | 'deny' | 'unknown'

/**
 * “为我批准”的 AI 审核器。它是一个无工具、短输出的独立请求，不能继承
 * 当前 agent 的权限，也不能通过审核器再次调用工具。任何网络/解析/模型错误
 * 都返回 unknown，由调用方回退到人工审批，避免“审核器挂了却放行”。
 */
async function reviewSensitiveOperation(
  deps: ApprovalDeps,
  req: RunRequest,
  reviewerModel: string,
  reviewerModelProviderId: string | undefined,
  toolName: string,
  input: unknown,
  signal: AbortSignal
): Promise<ReviewResult> {
  const host = deps.host()
  reviewerModel = reviewerModel.trim()
  if (reviewerModel === '') return 'unknown'
  const reviewerAlias = deps.upstream().resolveModel(reviewerModel, reviewerModelProviderId)
  if (reviewerAlias === undefined) {
    host.logger.warn(`[permission-review] configured model is unavailable: ${reviewerModel}`)
    return 'unknown'
  }
  let payload: string
  try {
    payload = JSON.stringify(input) ?? '{}'
  } catch {
    return 'unknown'
  }
  const prompt = [
    'You are a safety reviewer for an AI computer-use agent.',
    'Decide whether the proposed tool call is safe and consistent with the user task.',
    'Reply with exactly one JSON object and no markdown: {"decision":"allow"} or {"decision":"deny"}.',
    `User task: ${req.input.filter((part) => part.type === 'text').map((part) => part.text).join('\n').slice(0, 8000)}`,
    `Tool: ${toolName}`,
    `Input: ${payload.slice(0, 12000)}`
  ].join('\n')
  const now = host.clock.now()
  const reviewRequest: CanonicalRequest = {
    model: reviewerModel,
    ...(reviewerModelProviderId === undefined ? {} : { modelProviderId: reviewerModelProviderId }),
    system: 'Be conservative. Deny destructive, irreversible, credential-related, or ambiguous actions.',
    messages: [userMessage(ulid(now), [{ type: 'text', text: prompt }], now)],
    tools: [],
    maxOutputTokens: 128,
    /*
      需求:审核请求要短要快,能不思考就不思考。★ 但**不能硬发 `'off'`**:
      `gpt-6-*` 这类 effort 模型的 `reasoningEfforts` 不含 `'none'`,`thinking-adapter`
      会直接抛「该模型不支持关闭推理」—— 这里 catch 到之后返回 `unknown`,于是
      「为我批准」在这些模型上**每一次都退回人工审批**,而用户只会觉得这个功能没生效。
      `auxiliaryThinkingLevel` 在关不掉的模型上降到最低可用档,绝不抛(压缩、目标判定、
      会话标题共用这一个规则)。
    */
    thinkingLevel: auxiliaryThinkingLevel('off', reviewerAlias)
  }
  let text = ''
  try {
    for await (const event of deps.upstream().stream(reviewRequest, signal, {
      workspaceId: req.workspaceId,
      runId: `${req.runId}:permission-review`,
      sessionId: req.sessionId
    })) {
      if (event.type === 'text_delta') text += event.text
      if (event.type === 'error') return 'unknown'
    }
  } catch {
    host.logger.warn(`[permission-review] model request failed for ${reviewerModel}`)
    return 'unknown'
  }
  const normalized = text.trim()
  // 兼容模型常见的 ```json 包裹、前后解释文字，以及中文“允许/拒绝”。
  // 只接受明确的 decision 字段或整句明确答案；含糊内容仍然回退人工审批。
  const candidates = [normalized]
  const fenced = normalized.match(/```(?:json)?\s*([\s\S]*?)\s*```/iu)?.[1]
  if (fenced !== undefined) candidates.push(fenced.trim())
  const embedded = normalized.match(/\{\s*["']decision["']\s*:\s*["'](?:allow|deny)["']\s*\}/iu)?.[0]
  if (embedded !== undefined) candidates.push(embedded)
  for (const candidate of candidates) {
    try {
      const parsed = JSON.parse(candidate) as { decision?: unknown }
      if (parsed.decision === 'allow') return 'allow'
      if (parsed.decision === 'deny') return 'deny'
    } catch {
      // Try the next representation.
    }
  }
  if (/\bdecision\s*[:=]\s*["']?allow\b|(?:^|\s)(?:allow|allowed|approve|approved|允许|同意)(?:[.!。]|$)/iu.test(normalized)) return 'allow'
  if (/\bdecision\s*[:=]\s*["']?deny\b|(?:^|\s)(?:deny|denied|拒绝|禁止)(?:[.!。]|$)/iu.test(normalized)) return 'deny'
  host.logger.warn(`[permission-review] model returned an unrecognized decision for ${reviewerModel}`)
  return 'unknown'
}

/**
 * 造出这一次 run 的 `approve` 回调。
 *
 * ★ 审核器按 run 冻结,避免用户改设置后同一轮请求前后使用不同审核器。
 * 别名和供应商必须在**同一刻**冻结:一个冻结一个现取的话,用户中途换了供应商
 * 就会拼出「旧别名 + 新供应商」,而这一轮的审核器是谁将无从解释。
 *
 * ★ 权限档位**不**在此列 —— 读 `handle.permissionMode`(`RunHandle` 上的活值)。
 * 理由:权限档位调宽是用户在这个 run 还没结束时主动做的选择,冻结它的代价是
 * 「切到完全访问却要等下一句话」。
 */
export function createApprovalFn(
  req: RunRequest,
  handle: RunHandle,
  environment: WorkspaceEnvironment,
  deps: ApprovalDeps
): ApproveFn {
  const { model: reviewerModel, modelProviderId: reviewerModelProviderId } = deps.reviewer()

  // 契约要求返回 Promise;策略本身是同步的纯函数
  return async ({ tool, callId, input }) => {
    const mode = deps.modeFor(req.workspaceId, req.mode)
    const planWorkflowEnabled = mode.id === 'plan' || mode.tools?.includes('EnterPlanMode') === true
    const trustedPlanFileTool = planWorkflowEnabled
      && ['EnterPlanMode', 'Write', 'Edit', 'ExitPlanMode'].includes(tool.internalId)
    const outcome = evaluate({
      mode: handle.permissionMode,
      // Plan mode fences Write/Edit to one generated .plan file, so its workflow does not prompt twice.
      readOnly: tool.readOnly || trustedPlanFileTool,
      destructive: tool.destructive,
      needsNetwork: NETWORK_SWITCH_TOOLS.has(tool.internalId),
      webSearch: req.webSearch
    })
    const host = deps.host()
    environment.assertReady()
    const root = environment.rootPath
    const filesystem = environment.remote ? environment.fs : host.fs
    const scope = environment.remote ? { path: environment.path, namespace: environment.key } : undefined
    // 规则用 internalId 匹配 —— externalName 会被注册表截断去重,跨会话不稳定。
    const local = await readLocalSettings(filesystem, root, host.logger, scope)

    /*
      ★ 判定顺序在 `kernel/permission-decision.ts`，那里一条一条写明了为什么是这个次序。
      拆成「钩子之前 / 钩子之后」两段，是因为跑钩子会 fork 进程：拿到 early deny 就
      直接 return，物理上到不了跑钩子那一步。
    */
    const early = decideBeforeHooks(outcome, matchPermissionRules(local.permissions.deny, tool.internalId, input))
    if (early.kind === 'deny') return { kind: 'deny', reason: early.reason }
    /*
      PreToolUse 钩子。★ **顺序即语义**，这个位置是设计的一部分：

      - 排在 `deny` 桶**之后** —— `deny` 是「连档位都放宽不了」的那一层
        （见 `shared/domain/local-settings.ts` 文件头），一条钩子不该能把它打开。
      - 排在 `ask` / `allow` 桶**之前** —— 钩子的 deny 必须压得过一条 allow 规则，
        否则用户点过一次「以后都允许」，就等于永久绕开了所有安全钩子。

      钩子失败（超时 / 起不来 / 非 0 非 2 退出）**不阻断**，理由见 `hook/run.ts`
      文件头那段 fail-open。
    */
    const hookReports = await deps.runHooks({
      event: 'PreToolUse',
      environment,
      sessionId: req.sessionId,
      runId: req.runId,
      tool: { internalId: tool.internalId, externalName: tool.externalName, input },
      signal: handle.signal
    })
    const blockedBy = hookReports.find((r) => r.outcome === 'blocked' || r.decision === 'deny')
    /*
      插件拦截器和钩子**并到同一次表决里**,而不是另起一道门。

      ★ 它**只能收紧**:`deny` 与 `ask` 会合进 `HookVerdict`,而插件返回的
      `allow` 在 `plugin/manager.ts` 里就已经被当成弃权丢掉了 —— 让一个第三方
      插件把审批弹窗关掉,等于把整条权限链的最后一道门交给它。

      ★ 超时 = 弃权(fail-open),同 `hook/run.ts` 的取向:一个卡住的插件
      不该把所有工具调用堵死。
    */
    const interceptor = deps.pluginInterceptor()
    const pluginVerdict = interceptor === null
      ? {}
      : await interceptor({
        toolName: tool.internalId,
        toolInput: input,
        readOnly: tool.readOnly,
        destructive: tool.destructive
      }).catch(() => ({}))
    const verdict = decideAfterHooks({
      gate: outcome,
      hook: {
        ...(blockedBy === undefined
          ? pluginVerdict.deny === undefined ? {} : { deny: pluginVerdict.deny }
          : { deny: blockedBy.reason ?? 'A PreToolUse hook blocked this call.' }),
        allow: hookReports.some((r) => r.decision === 'allow'),
        ask: hookReports.some((r) => r.decision === 'ask') || pluginVerdict.ask === true
      },
      askRule: matchPermissionRules(local.permissions.ask, tool.internalId, input),
      allowRule: matchPermissionRules(local.permissions.allow, tool.internalId, input),
      autoReview: handle.permissionMode === 'auto' && tool.destructive
    })
    if (verdict.kind === 'deny') return { kind: 'deny', reason: verdict.reason }
    if (verdict.kind === 'allow') return { kind: 'allow_once' }
    if (verdict.kind === 'review') {
      const review = await reviewSensitiveOperation(deps, req, reviewerModel, reviewerModelProviderId, tool.externalName, input, handle.signal)
      if (review === 'allow') return { kind: 'allow_once' }
      if (review === 'deny') return { kind: 'deny', reason: 'The configured AI reviewer denied this potentially unsafe operation.' }
    }

    const suggestedRule = suggestPermissionRule(tool.internalId, input)

    /*
      ★★ 子代理到此为止 —— **它没有人可问**。

      下面那句 `interactions.request` 没有任何超时(设计如此:审批只该由人或中断
      来结束)。而一个子代理的待决项在界面上根本走不到用户面前:`InteractionPanel`
      挂在父 run 的 `activeRunId` 上,父 run 一结束它就卸载;`listInteractions`
      在父 handle 被回收后返回空数组;`applyChildEvent` 又把 `interaction_request`
      当未知事件丢掉。三条路全断,于是这次 await 永远不会结算 ——
      子代理就停在那次工具调用上,卡片上只剩一个不动的「运行中」。
      这是真实发生过的死锁(跑满一小时、六百多次工具调用之后毫无进展)。

      所以这里**当场拒绝**,而不是挂起。拒绝进转录、模型看得见,它可以换个做法
      或者把这一步交回给主代理 —— 这正是 `Task` 的工具描述里已经承诺过的语义。

      ★ 位置在钩子与本地规则**之后**:一条 `allow` 规则仍然应该让子代理跑起来,
      变的只是「没有规则时,从无限等待变成一句说得清的拒绝」。
      与 `ToolRegistry.snapshot({ noInteraction })` 是对称的两道闸:那道摘掉
      主动提问的工具,这道挡住权限审批,缺一处就还是能挂起。
    */
    if (req.parentRunId !== undefined) {
      return {
        kind: 'deny',
        reason:
          `This tool needs the user's approval, and a subagent cannot ask for it. ` +
          `Either do this step without ${tool.externalName}, or report back that the parent agent must run it. ` +
          (root === ''
            ? ''
            : `To allow it unattended in future runs, the user can add "${suggestedRule}" to permissions.allow in .next-cowork/settings.local.json.`)
      }
    }

    /*
      Notification 钩子 —— 「有个弹窗在等你」的那一刻。典型用法是
      `osascript -e 'display notification …'`。
      ★ fire-and-forget：不 await、不看结果。审批弹窗已经在等人了，
      再为一条通知脚本多等几秒只会让用户更晚看到它。
    */
    void deps.runHooks({
      event: 'Notification',
      environment,
      sessionId: req.sessionId,
      runId: req.runId,
      tool: { internalId: tool.internalId, externalName: tool.externalName, input },
      extra: { notification: { kind: 'tool_permission', toolName: tool.externalName } },
      signal: handle.signal
    }).catch(() => undefined)

    const response = await deps.interactions.request(handle, {
      kind: 'tool_permission', callId, toolName: tool.externalName,
      input, readOnly: tool.readOnly, destructive: tool.destructive,
      ...(root === '' ? {} : { suggestedRule })
    }, deps.host().clock.now())
    if (response.kind !== 'tool_permission') return { kind: 'deny' }
    const decision = response.decision
    if (decision.kind !== 'allow_always') return decision
    // 落盘失败不该把用户刚点下的「允许」变成「拒绝」—— 这一次照常放行,只是没记住。
    const saved = await addLocalPermissionRule(filesystem, root, 'allow', suggestedRule, host.logger, scope)
    if (!saved.ok) host.logger.warn(`[permission] 未能记住规则 ${suggestedRule}: ${saved.reason}`)
    return { kind: 'allow_once' }
  }
}
