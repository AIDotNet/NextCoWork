/**
 * 会话目标的**接线**验收 —— 走真 `runAgent` 装配 + 真 `goal:set` / `goal:get` /
 * `goal:clear`,只换掉一件东西:上游那条流。
 *
 * ## 这份文件在防什么
 *
 * `goal-runtime.test.ts` 把 `handleTurnEnd` 本身钉得很死,但它注入的是**假的
 * `runHooks`**,而且直接调运行时函数。于是下面这几件事**一行都没被执行过**:
 *
 * - `runAgent` 有没有真的把 `onTurnEnd` 装到主 run 上;
 * - `goalEvaluatorModel` / `...ProviderId` 有没有在本次 run 的**第一个 await 之前**
 *   成对冻结(中途改设置不该换掉正在跑的这一轮的判定器);
 * - 判成 `not_met` 之后主模型有没有**真的被再叫一轮**,判成 `met` 之后有没有
 *   真的收尾并清掉目标;
 * - 那些 `goal_status` 标记有没有**只落进转录、不落进上行请求**。
 *
 * 任何一处接错,那边照样全绿。`goal-evaluate.test.ts` 量的是判定器自己(裁转录、
 * 收敛失败),喂的是假的 `GoalEvaluatorPort` —— 它证明不了「判定请求在生产路径上
 * 真的会被发出去」。这一份不喂端口:判定走
 * `hooks.ts → evaluateGoal → getRouter().stream` 那条**生产**路,只有 `stream`
 * 被换成脚本。
 *
 * ## 换掉的只有 `getRouter().stream`
 *
 * 别名解析、工具注册表、权限闸门、Stop 钩子链、转录落盘一律是真的。
 * ★ 演示上游的那条 provider / 别名照常种进 store —— 判定器要用 `resolveModel`
 * 算上下文窗口,没有别名它会直接 `skipped`(reason `no_model`)。但**一个字节都
 * 出不了网**:`stream` 整个被换掉,`fetch` 那条路走不到;宿主仍然套着 `withDemo`,
 * 万一哪条请求漏过脚本,落到的也是演示上游而不是真网络。
 *
 * ## 真钩子、真子进程
 *
 * 最后一条用例注册的是一条**运行期命令型 Stop 钩子**,它真的起一个 POSIX shell、
 * 从 stdin 读那行 JSON、按 `stop_hook_active` 决定 exit 2 / exit 0 —— 于是
 * 「阻断 = 这一轮继续跑」这件事在接线层面被验了一次。
 */
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentMessage, ContentPart } from '../../../shared/agent/message'
import { assistantMessage, toolResultMessage, userMessage } from '../../../shared/agent/message'
import type { RunRequest } from '../../../shared/agent/run-request'
import type { ProviderStreamEvent, TokenUsage } from '../../../shared/agent/stream'
import type { ActiveGoal } from '../../../shared/domain/goal'
import type { HookDefinition } from '../../../shared/domain/hook'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { STOP_HOOK_FEEDBACK_PREFIX, GOAL_EVALUATOR_SYSTEM, goalKickoffMessage } from '../../goal/prompt'
import { getActiveGoal } from '../../goal/state'
import { registerRuntimeHook, runtimeHooksFor } from '../../hook-registry'
import { nodeHost } from '../../kernel/host'
import { interactions } from '../../kernel/interaction-gate'
import { runs } from '../../kernel/run-registry'
import { latestTodosFrom } from '../../../shared/agent/todo'
import type { CanonicalRequest } from '../../kernel/upstream/canonical'
import {
  DEMO_ALIAS,
  DEMO_ALIASES,
  DEMO_MODEL,
  DEMO_PROVIDER,
  DEMO_PROVIDER_ID,
  withDemo
} from '../../kernel/upstream/demo'
import { getRouter, getTools, installHost, resetRuntimeForTest, runAgent } from '../../runtime'
import { store } from '../../state/store'
import { clearGoal, getGoal, setGoal } from '../goal'

const WS = 'w1'
/** 判定模型那一栏用的第二个别名 —— 与演示别名**同一家**、另一个名字(用例 2)。 */
const JUDGE_ALIAS = 'judge'

type GoalStatus = Extract<ContentPart, { type: 'goal_status' }>

let tmp = ''
let root = ''

// ═══════════════════════════════════════════════════════════════
// 上游脚本
// ═══════════════════════════════════════════════════════════════

interface UpstreamCall {
  /** 判定请求由 `system === GOAL_EVALUATOR_SYSTEM` 认出来 —— 那正是生产里的判据 */
  kind: 'main' | 'evaluator'
  model: string
  modelProviderId: string | undefined
  request: CanonicalRequest
}

let calls: UpstreamCall[] = []
let mainTurn = 0
let evaluationTurn = 0
let onMain: (request: CanonicalRequest, turn: number) => AsyncGenerator<ProviderStreamEvent>
let onEvaluate: (request: CanonicalRequest, turn: number) => AsyncGenerator<ProviderStreamEvent>

const mainCalls = (): UpstreamCall[] => calls.filter((call) => call.kind === 'main')
const evaluationCalls = (): UpstreamCall[] => calls.filter((call) => call.kind === 'evaluator')

const usage = (): TokenUsage => ({ inputTokens: 12, outputTokens: 6 })

/** 一整轮:message_start → text_delta → message_end(end_turn)。 */
async function* textTurn(text: string, during?: () => void): AsyncGenerator<ProviderStreamEvent> {
  yield { type: 'message_start', model: DEMO_MODEL, providerId: DEMO_PROVIDER_ID }
  during?.()
  yield { type: 'text_delta', index: 0, text }
  yield { type: 'message_end', stopReason: 'end_turn', usage: usage() }
}

/** 判定器的一轮。`during` 在正文之前跑 —— 用例 3 就是靠它「判到一半把目标清掉」。 */
async function* verdictTurn(json: string, during?: () => void): AsyncGenerator<ProviderStreamEvent> {
  yield { type: 'message_start', model: DEMO_MODEL, providerId: DEMO_PROVIDER_ID }
  during?.()
  yield { type: 'text_delta', index: 0, text: json }
  yield { type: 'message_end', stopReason: 'end_turn', usage: usage() }
}

/**
 * 一轮工具调用:`tool_call_start → delta → end`,停因是 `tool_use`。
 *
 * `callId` 可以传:同一个 run 里调两次同一件工具,必须是两个 id —— 转录里的
 * `tool_call` / `tool_result` 按 callId 配对,而 `latestTodosFrom` 正是靠这份配对
 * 判断「最近一次**成功**的写入是哪一次」。默认值让既有调用一个字都不用改。
 */
async function* toolTurn(name: string, input: unknown, callId = 'toolu_goal_1'): AsyncGenerator<ProviderStreamEvent> {
  yield { type: 'message_start', model: DEMO_MODEL, providerId: DEMO_PROVIDER_ID }
  yield { type: 'text_delta', index: 0, text: '先把完成条件立起来。' }
  yield { type: 'tool_call_start', index: 1, callId, name }
  yield { type: 'tool_call_delta', index: 1, callId, argsDelta: JSON.stringify(input) }
  yield { type: 'tool_call_end', index: 1, callId }
  yield { type: 'message_end', stopReason: 'tool_use', usage: usage() }
}

/**
 * ★ 全文件唯一一处假东西。装的是**已经建好的那个路由器实例** ——
 * `runAgent` 拿到的 `upstream: getRouter()` 就是它,所以这条缝足够窄也足够准。
 */
function installScriptedUpstream(): void {
  vi.spyOn(getRouter(), 'stream').mockImplementation((request) => {
    const kind = request.system === GOAL_EVALUATOR_SYSTEM ? 'evaluator' : 'main'
    calls.push({ kind, model: request.model, modelProviderId: request.modelProviderId, request })
    return kind === 'evaluator' ? onEvaluate(request, ++evaluationTurn) : onMain(request, ++mainTurn)
  })
}

// ═══════════════════════════════════════════════════════════════
// 夹具
// ═══════════════════════════════════════════════════════════════

let runSeq = 0
const req = (sessionId: string, over: Partial<RunRequest> = {}): RunRequest => ({
  runId: `goal-wiring-${++runSeq}`,
  sessionId,
  workspaceId: WS,
  depth: 0,
  input: [{ type: 'text', text: '帮我看看这个工程' }],
  mode: 'normal',
  thinking: 'off',
  webSearch: false,
  permissionMode: 'ask',
  model: DEMO_ALIAS,
  skillIds: [],
  ...over
})

/**
 * 预置一条**非空**转录。
 *
 * 两个作用:(1) 标题生成器只在 `history.length === 0` 时才起 —— 不预置就会真的
 * 去打一次上游;(2) 「已设定」那条标记要挂在一条**已有的**助手消息上,
 * 否则它只能排队等下一次提交。
 */
function seedSession(sessionId: string): void {
  store.ensureSession({
    id: sessionId,
    workspaceId: WS,
    model: DEMO_ALIAS,
    mode: 'normal',
    thinking: 'off',
    rootPathAtCreation: root
  })
  store.setHistory(sessionId, [
    userMessage(`${sessionId}-u0`, [{ type: 'text', text: '先前的提问' }], 1),
    assistantMessage(`${sessionId}-a0`, [{ type: 'text', text: '先前的回答' }], 2)
  ])
}

/** 真的经 `goal:set`,拿到它给的那条 internal kickoff。 */
function setGoalOrThrow(sessionId: string, condition: string): { goal: ActiveGoal; kickoff: ContentPart[] } {
  const result = setGoal({ sessionId, condition })
  if (!result.ok) throw new Error(`目标没设上:${result.reason}`)
  return { goal: result.goal, kickoff: result.kickoff }
}

/**
 * 按渲染层的真实形状起一轮:`/goal` 拿到 kickoff 后,把它当 `input` 发出去,
 * 并带上 `inputInternal` + `inputGoalId`(见 `views/chat/ChatView.tsx`)。
 */
async function runGoalTurn(sessionId: string, condition: string): Promise<RunRequest> {
  const { goal, kickoff } = setGoalOrThrow(sessionId, condition)
  const request = req(sessionId, { input: kickoff, inputInternal: true, inputGoalId: goal.id })
  await runAgent(runs.create(request), request)
  return request
}

const markerSummary = (sessionId: string): Array<{ set: boolean; met: boolean; iterations: number }> =>
  statuses(sessionId).map((part) => ({ set: part.set === true, met: part.met, iterations: part.iterations ?? 0 }))

const statuses = (sessionId: string): GoalStatus[] =>
  store.getHistory(sessionId)
    .flatMap((message) => message.parts)
    .filter((part): part is GoalStatus => part.type === 'goal_status')

/** 落盘的那一份转录里有没有这个字面串 —— 声明成函数是为了断言失败时看得出查了什么。 */
const historyText = (sessionId: string): string => JSON.stringify(store.getHistory(sessionId))

/** 一份上行请求里的全部正文(判定器读的就是它)。 */
const wireText = (request: CanonicalRequest): string =>
  request.messages
    .flatMap((message: AgentMessage) => message.parts.map((part) => (part.type === 'text' ? part.text : '')))
    .join('\n')

beforeEach(() => {
  resetRuntimeForTest()
  tmp = mkdtempSync(join(tmpdir(), 'nextcowork-goal-wiring-'))
  root = join(tmp, 'ws')
  mkdirSync(root, { recursive: true })
  mkdirSync(join(tmp, 'userData'), { recursive: true })

  store.putWorkspace({
    id: WS,
    name: 'goal wiring',
    rootPath: root,
    environment: { kind: 'local' },
    settings: DEFAULT_WORKSPACE_SETTINGS,
    createdAt: 1,
    lastOpenedAt: 1
  })
  /*
    ★ 宿主指向**临时** userData 与临时工作区根:这台开发机上真的装着一条全局
    Stop 钩子时,这几条用例会飘 —— 而它们量的是接线,不该被环境左右。
  */
  installHost(withDemo(nodeHost({
    paths: {
      userData: () => join(tmp, 'userData'),
      attachments: () => join(tmp, 'attachments'),
      temp: () => tmpdir()
    }
  }), { chunkDelayMs: 0 }))

  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  seedJudgeAlias()
  // 设置是库里的状态,不随 resetRuntimeForTest 复位 —— 自己收干净,免得上一条的
  // 判定模型漏到下一句断言里。
  store.updateSettings({ goalEvaluatorModel: '', goalEvaluatorModelProviderId: undefined })

  calls = []
  mainTurn = 0
  evaluationTurn = 0
  onMain = (_request, turn) => textTurn(`(脚本)第 ${turn} 轮主模型回复`)
  onEvaluate = () => verdictTurn('{"ok":false,"reason":"转录里还没有证据"}')

  installScriptedUpstream()
})

afterEach(() => {
  vi.restoreAllMocks()
  runs.abortAll()
  resetRuntimeForTest()
  rmSync(tmp, { recursive: true, force: true })
})

/** 判定模型那一栏要用的第二个名字 —— 同一个供应商上的另一条别名。 */
function seedJudgeAlias(): void {
  const demo = DEMO_ALIASES[0]
  if (demo === undefined) throw new Error('DEMO_ALIASES 是空的,夹具不成立')
  store.putAlias({ ...demo, alias: JUDGE_ALIAS })
}

describe('会话目标 · 真 runAgent 装配 + 真 goal IPC', () => {
  it('★ 全链路:kickoff 起步 → 判 not_met → 主模型真的又跑一轮 → 判 met → 收尾清目标', async () => {
    const sid = 'goal-e2e'
    const condition = '让 goal-wiring 这份测试全绿'
    seedSession(sid)

    const set = setGoalOrThrow(sid, condition)
    expect(set.kickoff).toEqual([{ type: 'text', text: goalKickoffMessage(condition) }])
    // 设目标这一刻就盖了「已设定」—— 挂在预置的那条助手消息上,不新增消息
    expect(markerSummary(sid)).toEqual([{ set: true, met: false, iterations: 0 }])

    onMain = (_request, turn) => textTurn(`第 ${turn} 轮的主模型回复`)
    onEvaluate = (_request, turn) => turn === 1
      ? verdictTurn('{"ok":false,"reason":"转录里还没看到测试输出"}')
      : verdictTurn('{"ok":true,"reason":"测试已经全绿了"}')

    const request = req(sid, { input: set.kickoff, inputInternal: true, inputGoalId: set.goal.id })
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    // ★ 主模型被叫了**两次**:第二次是判定器判 not_met 之后真的回来的那一轮
    expect(mainCalls()).toHaveLength(2)
    expect(evaluationCalls()).toHaveLength(2)

    // 三个标记各就各位:设定 / 未达成(1 轮) / 达成(2 轮)
    expect(markerSummary(sid)).toEqual([
      { set: true, met: false, iterations: 0 },
      { set: false, met: false, iterations: 1 },
      { set: false, met: true, iterations: 2 }
    ])
    const notMet = statuses(sid).find((part) => part.met === false && part.set !== true)
    expect(notMet?.reason).toBe('转录里还没看到测试输出')

    /*
      ★ 「真的被叫回来」的硬证据:第二轮上行的消息里带着那条续跑注入。
      只看 `mainCalls().length === 2` 是不够的 —— 两次请求都可能是别的原因发出来的。
    */
    const second = mainCalls()[1]
    expect(second).toBeDefined()
    if (second === undefined) return
    expect(wireText(second.request)).toContain(STOP_HOOK_FEEDBACK_PREFIX)
    expect(wireText(second.request)).toContain('转录里还没看到测试输出')

    /*
      ★★ 标记只属于 UI 那一轨。`attachUiParts` 动的是 `messages`,请求体读的是
      `contextMessages`(见 `agent-session.ts` 的 `commit`)—— 混进去的话,
      上游会对着一堆自己不认识的内容块开始道歉。
    */
    for (const call of mainCalls()) {
      expect(JSON.stringify(call.request.messages), 'goal_status 混进了上行请求').not.toContain('goal_status')
    }

    // 达成即自动清掉,而且 `goal:get` 不会把它捞回来
    expect(getActiveGoal(sid)).toBeUndefined()
    expect(getGoal({ sessionId: sid })).toBeUndefined()
  })

  it('★ 判定模型与供应商在 run 开始时**成对冻结**:中途改设置不动这一轮,下一轮才换', async () => {
    const sid = 'goal-freeze'
    const condition = '结束前把这次判定跑完'
    seedSession(sid)
    store.updateSettings({ goalEvaluatorModel: JUDGE_ALIAS, goalEvaluatorModelProviderId: DEMO_PROVIDER_ID })

    // ★ 在**第一条主模型的流里**改设置。冻结发生在 runAgent 的第一个 await 之前,
    //   所以这一改只该影响下一个 run。
    onMain = (_request, turn) => turn === 1
      ? textTurn('第一轮', () => {
        store.updateSettings({ goalEvaluatorModel: DEMO_ALIAS, goalEvaluatorModelProviderId: DEMO_PROVIDER_ID })
      })
      : textTurn('第二轮')
    onEvaluate = () => verdictTurn('{"ok":true,"reason":"这一轮就算达成"}')

    await runGoalTurn(sid, condition)
    await runGoalTurn(sid, `${condition}(第二轮)`)

    expect(evaluationCalls().map((call) => call.model)).toEqual([JUDGE_ALIAS, DEMO_ALIAS])
    expect(evaluationCalls().map((call) => call.modelProviderId)).toEqual([DEMO_PROVIDER_ID, DEMO_PROVIDER_ID])
    // 两次判定都真的发出去了 —— 否则上面那两行可能只是在比两个空数组
    expect(mainCalls()).toHaveLength(2)
  })

  it('★ 判定途中用户清掉目标:met 不落章,清除标记活过 finally,目标不复活', async () => {
    const sid = 'goal-cleared'
    const condition = '被清掉的那个目标'
    seedSession(sid)
    const set = setGoalOrThrow(sid, condition)

    onMain = () => textTurn('先按目标做事。')
    onEvaluate = () => verdictTurn('{"ok":true,"reason":"其实已经好了"}', () => {
      // ★ 判定还没回话,用户先按了「清除」—— 这正是那条裁决最可能作废的一刻
      clearGoal({ sessionId: sid })
    })

    const request = req(sid, { input: set.kickoff, inputInternal: true, inputGoalId: set.goal.id })
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(1)

    const parts = statuses(sid)
    expect(parts.some((part) => part.met)).toBe(false)
    expect(parts.some((part) => part.failed === true)).toBe(false)
    // 清除标记确实落在转录里(否则 UI 上那份历史会看起来像从没清过)
    expect(parts.some((part) => part.cleared === true)).toBe(true)

    /*
      ★★ 这一条才是重点:清除标记必须活过 `runAgent` 收尾时的
      `mergeGoalStatusHistory(agentSession.history, latest)`。被合并掉的话,
      `restorableGoalCondition` 读最后一条标记会重新认出一个活跃目标 ——
      用户清掉的目标下次打开自己回来。
    */
    expect(historyText(sid)).toContain('"cleared":true')
    expect(getGoal({ sessionId: sid })).toBeUndefined()
    expect(getActiveGoal(sid)).toBeUndefined()
  })

  it('★ 主模型自己调 ProposeGoal(ask_user:false):工具下发并被执行,目标直接设立,kickoff 走 internal,随后判成 met', async () => {
    const sid = 'goal-propose'
    const condition = 'goal-wiring 这份测试全绿'
    seedSession(sid)

    const advertised: string[] = []
    let proposeOnWire: string | undefined
    onMain = (request, turn) => {
      advertised.push(...request.tools.map((tool) => tool.externalName))
      if (turn !== 1) return textTurn('条件已经立好了,继续干活。')
      // ★ 从**真正下发的那张表**里认工具,而不是写死 'ProposeGoal':
      //   外部名由 ToolNamer 分配,撞名时会带哈希后缀。
      const propose = request.tools.find((tool) => tool.description.includes('Propose a completion condition'))
      proposeOnWire = propose?.externalName
      return propose === undefined
        ? textTurn('(脚本)这张表里没有 ProposeGoal')
        : toolTurn(propose.externalName, { condition, ask_user: false })
    }
    onEvaluate = () => verdictTurn('{"ok":true,"reason":"工具已经执行过了"}')

    const request = req(sid)
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    // 「下发了」:这张表里确实有它,而且就是注册表算出来的那个外部名
    expect(proposeOnWire).toBe(getTools().byInternalId('ProposeGoal')?.externalName)
    expect(advertised).toContain(proposeOnWire)
    // 「是只读工具」:readOnly 的工具在 ask 档不弹审批,run 里一个待决交互都没有
    expect(getTools().byInternalId('ProposeGoal')?.readOnly).toBe(true)
    expect(interactions.list()).toEqual([])

    // 「被执行了」:工具回的是 ok,而且回的是「直接设立」那一支
    const toolResult = store.getHistory(sid)
      .flatMap((message) => message.parts)
      .find((part) => part.type === 'tool_result')
    expect(toolResult?.type === 'tool_result' && toolResult.isError).toBe(false)
    expect(toolResult?.type === 'tool_result' && toolResult.output.content).toContain('"status":"set"')

    // 「直接设立」的状态看得见:origin 是 proposal_direct,不是用户手设的那一支
    const setMarker = statuses(sid).find((part) => part.set === true)
    expect(setMarker).toMatchObject({ set: true, met: false, condition, origin: 'proposal_direct' })

    // kickoff 走的是 internal 通道 —— 它不该以用户的名义出现在对话里
    const internal = store.getHistory(sid).find((message) => message.role === 'user' && message.internal === true)
    expect(internal?.parts).toEqual([{ type: 'text', text: goalKickoffMessage(condition) }])
    const second = mainCalls()[1]
    expect(second).toBeDefined()
    if (second === undefined) return
    expect(wireText(second.request)).toContain(goalKickoffMessage(condition))

    // 判定的结论同样下来了:这一次是 met,目标收尾
    expect(evaluationCalls()).toHaveLength(1)
    expect(markerSummary(sid).at(-1)).toEqual({ set: false, met: true, iterations: 1 })
    expect(getActiveGoal(sid)).toBeUndefined()
  })

  it('★ 会话名下还有后台子 run(旧父 run 派出去的):判定整个推迟,判定器一次都不叫,目标留着', async () => {
    const sid = 'goal-background'
    const condition = '等后台那把活儿落地'
    seedSession(sid)

    /*
      ★ 一个**已经不在注册表里的旧父 run** 派出去、至今没回来的子 run。
      它靠 `parentSessionId` 挂在这条会话名下 —— 这正是
      `runs.activeBackgroundChildrenOfSession` 的判据,也是「跑着后台工作的是别人」
      这个事实的唯一来源。
    */
    const child = runs.create(req('goal-background-child', {
      runId: 'goal-child-run',
      parentSessionId: sid,
      parentRunId: 'goal-parent-run-already-gone',
      depth: 1
    }))
    child.backgroundTask = { type: 'Task', description: '后台子代理' }

    const set = setGoalOrThrow(sid, condition)
    onMain = () => textTurn('先看一眼现状。')

    const request = req(sid, { input: set.kickoff, inputInternal: true, inputGoalId: set.goal.id })
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(1)
    // ★ 判定器一条请求都没发:后台还在跑,这一轮读到的转录本来就是半截的
    expect(evaluationCalls()).toHaveLength(0)

    const goal = getActiveGoal(sid)
    expect(goal?.iterations).toBe(0)
    expect(goal?.deferredSince).toBeTypeOf('number')
    // 目标不会被推迟吃掉 —— 下一次用户开口时它接着判
    expect(getGoal({ sessionId: sid })).toMatchObject({ condition, iterations: 0 })
    expect(runtimeHookIds(sid)).toEqual([])
  })

  /**
   * ★ 真子进程、真阻断语义。
   *
   * 这条运行期命令型 Stop 钩子**只做一件事**:从 stdin 那行 JSON 里认出
   * `stop_hook_active`。第一次是 `false` → `exit 2`(阻断收尾 = 这一轮继续跑);
   * 第二次是 `true` → `exit 0`(放行)。
   *
   * ★ 脚本**只用 POSIX 的 `read` / `case`**,不碰文件、不删东西:它要证明的是
   *   「阻断 → 真的继续跑了一轮,并且把反馈递给了模型」,不是脚本本身有多能干。
   *
   * ★ 断言 `mainCalls()` 恰好两轮就是「第二次真的放行了」的证据:再阻断一次
   *   就会多跑一轮,而 `stop_hook_active` 没被解析出来的话它永远不会变成 true。
   */
  const posixIt = process.platform === 'win32' ? it.skip : it
  posixIt('★ 运行期命令型 Stop 钩子:第一次 exit 2 让这一轮继续,第二次看 stop_hook_active 放行', async () => {
    const sid = 'goal-stop-hook'
    seedSession(sid)
    registerRuntimeHook(sid, stopOnceHook())
    expect(runtimeHookIds(sid)).toEqual(['stop-once'])

    onMain = (_request, turn) => textTurn(`第 ${turn} 轮`)

    const request = req(sid)
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(2)
    // 没有目标,所以一次判定都不该有 —— 这条钩子和 goal 是两条独立的路
    expect(evaluationCalls()).toHaveLength(0)
    expect(getActiveGoal(sid)).toBeUndefined()

    const second = mainCalls()[1]
    expect(second).toBeDefined()
    if (second === undefined) return
    expect(wireText(second.request)).toContain(STOP_HOOK_FEEDBACK_PREFIX)
    expect(wireText(second.request)).toContain('keep working')
    expect(historyText(sid)).toContain('keep working')
  })
})

/**
 * 清单补报的**接线**验收 —— `runtime.ts` 里挂在主 run `onTurnEnd` 上的那段
 * `reconcileTodos` 有没有真的接上,以及它该让路的那几种情形。
 *
 * 与 `todo-derive.test.ts` 里那个 describe 的关系,和 `goal-runtime.test.ts` 与
 * 上面第一条用例的关系一样:那边钉的是纯函数,这边走真 `runAgent` —— 量的是
 * 「提醒真的到了模型眼前」「额度真的是每个 run 一份」「该让路时一次都没发」。
 */
describe('清单补报 · 真 runAgent 接线', () => {
  /** 补报注入里那句认得出它的正文(同为 internal 的目标 kickoff 不含这句)。 */
  const NOTE_MARK = 'reconcile the progress reported through'

  /**
   * 全局表算出来的 TodoWrite 外部名。★ 不写字面量:名字由 `ToolNamer` 分配,
   * 撞名时会带 8 位哈希后缀。它和本次 run 下发的那张表同名 —— 第一条用例把这件事钉住。
   */
  const globalTodoName = (): string => getTools().byInternalId('TodoWrite')?.externalName ?? 'TodoWrite'

  /** 从**真正下发的那张表**里认工具(和上面那条 ProposeGoal 用例用的是同一招)。 */
  const todoNameOf = (request: CanonicalRequest): string | undefined =>
    request.tools.find((tool) => tool.description.startsWith('Use this tool to manage a task list'))?.externalName

  const listOf = (...items: Array<[string, 'pending' | 'in_progress' | 'completed']>): unknown => ({
    todos: items.map(([content, status]) => ({ content, status, activeForm: `正在${content}` }))
  })

  /** 转录里那些**补报**注入。internal 消息不止一种,所以按正文认,不按 internal 认。 */
  const reconcileNotes = (sessionId: string): string[] =>
    store.getHistory(sessionId)
      .filter((message) => message.internal === true)
      .flatMap((message) => message.parts)
      .filter((part): part is Extract<ContentPart, { type: 'text' }> =>
        part.type === 'text' && part.text.includes(NOTE_MARK))
      .map((part) => part.text)

  it('★ 写清单 → 想停 → 补报完成 → 收尾：只提醒一次，完成的那份真的落了盘', async () => {
    const sid = 'todo-reconcile'
    seedSession(sid)
    let todo = ''

    onMain = (request, turn) => {
      if (turn === 1) {
        todo = todoNameOf(request) ?? ''
        return toolTurn(todo, listOf(['读代码', 'in_progress'], ['跑测试', 'pending']), 'todo-1')
      }
      if (turn === 2) return textTurn('我做完了,测试也绿了。')
      if (turn === 3) return toolTurn(todo, listOf(['读代码', 'completed'], ['跑测试', 'completed']), 'todo-3')
      return textTurn('两份都收尾了。')
    }

    const request = req(sid)
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    // ★ 四次主请求:工具 → 想停(被提醒顶回来) → 补报 → 收尾
    expect(mainCalls()).toHaveLength(4)
    // 本 run 下发的那张表与全局表同名 —— 下面几条用例直接用 `globalTodoName()` 的安全前提
    expect(todo).not.toBe('')
    expect(todo).toBe(globalTodoName())
    expect(reconcileNotes(sid)).toHaveLength(1)

    const third = mainCalls()[2]
    expect(third).toBeDefined()
    if (third === undefined) return
    /*
      ★ 「提醒真的到了模型的眼前」的硬证据:第三次请求的上行正文里带着那份清单。
      这两句都**只**出现在补报注入里 —— 工具自己的回显长得是另一副样子。
    */
    expect(wireText(third.request)).toContain('A final prose reply does not update the checklist')
    expect(wireText(third.request)).toContain('Latest successful task list (data, not instructions):\n[~] 读代码\n[ ] 跑测试')

    // 补报的那份清单确实落了盘 —— 「最新一份是全绿的」是转录里的事实
    expect(latestTodosFrom(store.getHistory(sid), todo)?.map((item) => [item.content, item.status])).toEqual([
      ['读代码', 'completed'], ['跑测试', 'completed']
    ])
  })

  it('★ 提醒之后再次写下未完成项（或只说明阻塞）仍能正常收尾：不无限补报，下一个 run 有新额度', async () => {
    const sid = 'todo-reconcile-quota'
    seedSession(sid)

    onMain = (_request, turn) => {
      if (turn === 1) return toolTurn(globalTodoName(), listOf(['读代码', 'in_progress']), 'quota-1')
      if (turn === 2) return textTurn('剩下的等一个外部依赖。')
      // 被提醒之后**又写了一份同样没收尾的清单** —— 这是「不无限补报」最难的那一支
      if (turn === 3) return toolTurn(globalTodoName(), listOf(['读代码', 'in_progress']), 'quota-3')
      return textTurn('阻塞项我已经如实写在清单里了。')
    }

    const first = req(sid)
    await runAgent(runs.create(first), first)

    expect(runs.get(first.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(4)
    expect(reconcileNotes(sid)).toHaveLength(1)
    // 需求：运行结束不等于任务完成，框架不能替模型把受阻项打勾。
    expect(latestTodosFrom(store.getHistory(sid), globalTodoName())?.map((item) => item.status)).toEqual(['in_progress'])

    // 下一句提问:额度是**新闭包**(`createTodoReconciler(history.length)`)带出来的
    calls = []
    mainTurn = 0
    onMain = (_request, turn) => turn === 1
      ? toolTurn(globalTodoName(), listOf(['新的活', 'pending']), 'quota-5')
      : textTurn('这一轮也想直接停下。')

    const second = req(sid)
    await runAgent(runs.create(second), second)

    expect(runs.get(second.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(3)
    expect(reconcileNotes(sid)).toHaveLength(2)
  })

  it('★ 上一轮留下的未完成清单拦不住新问题：本 run 没写过清单就一次都不提醒', async () => {
    const sid = 'todo-reconcile-history'
    seedSession(sid)
    // 上一次提问成功写下、且没收尾的清单 —— 它原样躺在转录里(成功过,所以它是「最新一份」)
    store.setHistory(sid, [
      ...store.getHistory(sid),
      assistantMessage(`${sid}-old`, [
        { type: 'tool_call', callId: 'old-todo', name: globalTodoName(), input: listOf(['上一轮的活', 'in_progress']) }
      ], 0),
      toolResultMessage(`${sid}-old-result`, [
        { type: 'tool_result', callId: 'old-todo', output: { content: 'ok' }, isError: false }
      ], 0)
    ])
    onMain = () => textTurn('这是个和清单无关的小问题。')

    const request = req(sid, { input: [{ type: 'text', text: '这个函数是 async 的吗' }] })
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    // ★ 一次请求都没多出来:提醒只看本 run 提交之后的那一段转录
    expect(mainCalls()).toHaveLength(1)
    expect(reconcileNotes(sid)).toEqual([])
  })

  it('第一次就写全 completed、或者那次写入本身被工具拒了 → 不提醒', async () => {
    const sid = 'todo-reconcile-clean'
    seedSession(sid)
    onMain = (_request, turn) => turn === 1
      ? toolTurn(globalTodoName(), listOf(['读代码', 'completed'], ['跑测试', 'completed']), 'clean-1')
      : textTurn('都做完了。')

    const first = req(sid)
    await runAgent(runs.create(first), first)

    expect(mainCalls()).toHaveLength(2)
    expect(reconcileNotes(sid)).toEqual([])

    /*
      第二次:清单被工具自己拒了(两个 in_progress)。★ 这样的调用**原样躺在转录里**
      —— `agent-session` 先 commit、后在 `safeParse` 里校验。盲取最近一条就会把工具
      拒绝过的东西当成当前进度,所以这里同样不该提醒。
    */
    const refused = 'todo-reconcile-refused'
    seedSession(refused)
    calls = []
    mainTurn = 0
    onMain = (_request, turn) => turn === 1
      ? toolTurn(globalTodoName(), listOf(['A', 'in_progress'], ['B', 'in_progress']), 'clean-3')
      : textTurn('这一轮我没写清单。')

    const second = req(refused)
    await runAgent(runs.create(second), second)

    expect(mainCalls()).toHaveLength(2)
    expect(reconcileNotes(refused)).toEqual([])
  })

  it('★ 目标判成 met 的强制收尾不被覆盖：清单补报一句都不加', async () => {
    const sid = 'todo-reconcile-goal'
    seedSession(sid)
    const set = setGoalOrThrow(sid, '把这次补报跑完')

    onMain = (_request, turn) => turn === 1
      ? toolTurn(globalTodoName(), listOf(['干活', 'in_progress']), 'goal-todo-1')
      : textTurn('目标达成了。')
    onEvaluate = () => verdictTurn('{"ok":true,"reason":"已经达成"}')

    const request = req(sid, { input: set.kickoff, inputInternal: true, inputGoalId: set.goal.id })
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    // 判定器判 met → `finish` 走的是 Goal 那一跳,清单补报一次都不该插进来
    expect(evaluationCalls()).toHaveLength(1)
    expect(mainCalls()).toHaveLength(2)
    expect(reconcileNotes(sid)).toEqual([])
    expect(getActiveGoal(sid)).toBeUndefined()
  })

  it('★ 没有 goal、但会话名下还有后台子 run 在跑 → 不催这份半截清单', async () => {
    const sid = 'todo-reconcile-background'
    seedSession(sid)
    /*
      ★ 一个**已经不在注册表里的旧父 run** 派出去、至今没回来的子 run —— 与上面
      `goal-background` 那条同一个夹具,理由也同一条:`parentSessionId` 是
      「跑着后台工作的是别人」这个事实的唯一来源。
    */
    const child = runs.create(req('todo-reconcile-child', {
      runId: 'todo-child-run',
      parentSessionId: sid,
      parentRunId: 'todo-parent-run-gone',
      depth: 1
    }))
    child.backgroundTask = { type: 'Task', description: '后台子代理' }

    onMain = (_request, turn) => turn === 1
      ? toolTurn(globalTodoName(), listOf(['读代码', 'in_progress']), 'bg-1')
      : textTurn('先把结果交出去。')

    const request = req(sid)
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(2)
    expect(reconcileNotes(sid)).toEqual([])
  })

  // 需求：Stop 钩子的阻断必须先交回模型，不能被同一回合的清单提醒覆盖。
  const posixIt = process.platform === 'win32' ? it.skip : it
  posixIt('先保留 Stop 钩子的续跑意见，放行之后才核对清单', async () => {
    const sid = 'todo-reconcile-stop-hook'
    seedSession(sid)
    registerRuntimeHook(sid, stopOnceHook())
    onMain = (_request, turn) => {
      if (turn === 1) return toolTurn(globalTodoName(), listOf(['干活', 'in_progress']), 'hook-todo-1')
      if (turn === 4) return toolTurn(globalTodoName(), listOf(['干活', 'completed']), 'hook-todo-4')
      return textTurn('准备结束。')
    }

    const request = req(sid)
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(5)
    expect(reconcileNotes(sid)).toHaveLength(1)
    const afterHook = mainCalls()[2]?.request
    const afterReminder = mainCalls()[3]?.request
    expect(afterHook).toBeDefined()
    expect(afterReminder).toBeDefined()
    if (afterHook === undefined || afterReminder === undefined) return
    expect(wireText(afterHook)).toContain(STOP_HOOK_FEEDBACK_PREFIX)
    expect(wireText(afterHook)).not.toContain(NOTE_MARK)
    expect(wireText(afterReminder)).toContain(NOTE_MARK)
  })

  // 需求：上游错误必须直接收尾，不能为了清单补报再次调用已经失败的供应商。
  it('上游报错后保留未完成清单，不再追加补报请求', async () => {
    const sid = 'todo-reconcile-error'
    seedSession(sid)
    onMain = async function* (_request, turn): AsyncGenerator<ProviderStreamEvent> {
      if (turn === 1) {
        yield* toolTurn(globalTodoName(), listOf(['干活', 'in_progress']), 'error-todo-1')
        return
      }
      yield { type: 'message_start', model: DEMO_MODEL, providerId: DEMO_PROVIDER_ID }
      yield { type: 'error', error: { code: 'provider', message: 'Scripted provider failure', retryable: false } }
    }

    const request = req(sid)
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('error')
    expect(mainCalls()).toHaveLength(2)
    expect(reconcileNotes(sid)).toEqual([])
    expect(latestTodosFrom(store.getHistory(sid), globalTodoName())?.[0]?.status).toBe('in_progress')
  })

  it('★ 用户中断之后不再有请求，也不写补报', async () => {
    const sid = 'todo-reconcile-abort'
    seedSession(sid)
    const request = req(sid)
    /*
      ★ 中断发生在**第二轮的流里**(`during` 在 `text_delta` 之前跑),这正是用户
      点停止最可能落在的那一刻。abort 的目标是 run 自己的 id —— 上游请求体里
      没有这个 id,所以闭包捕获的是下面这条 `RunRequest`。
    */
    onMain = (_request, turn) => turn === 1
      ? toolTurn(globalTodoName(), listOf(['读代码', 'in_progress']), 'abort-1')
      : textTurn('我想收尾了', () => { runs.abort(request.runId, false) })

    const handle = runs.create(request)
    await runAgent(handle, request)

    expect(handle.status).toBe('aborted')
    // ★ 中断那一轮走 `finalizeAbort`,根本到不了回合末判定 —— 第三次请求不存在
    expect(mainCalls()).toHaveLength(2)
    expect(reconcileNotes(sid)).toEqual([])
  })

  it('★ Plan 模式：TodoWrite 不在下发表上，清单这条路径整个不参与', async () => {
    const sid = 'todo-reconcile-plan'
    seedSession(sid)
    let first: CanonicalRequest | undefined
    onMain = (request, turn) => {
      first ??= request
      return textTurn(`第 ${turn} 轮`)
    }

    const request = req(sid, { mode: 'plan' })
    await runAgent(runs.create(request), request)

    expect(runs.get(request.runId)?.status).toBe('done')
    expect(mainCalls()).toHaveLength(1)
    expect(reconcileNotes(sid)).toEqual([])
    /*
      ★ 缺席的理由也断言出来:计划模式的白名单里没有 TodoWrite,模型**写不出**清单,
      所以「补一份从没写过的清单」这件事根本不存在;`req.mode === 'plan'` 那道闸是第二层。
    */
    expect(first?.tools.some((tool) => tool.description.startsWith('Use this tool to manage a task list'))).toBe(false)
  })
})

/** 运行期钩子表的当前内容(纯字符串,失败信息里一眼看得出接了没接)。 */
function runtimeHookIds(sessionId: string): string[] {
  return [...runtimeHooksFor(sessionId)].map((hook) => hook.id)
}

/**
 * 一条只看 `stop_hook_active` 的 Stop 钩子。
 *
 * ★ 只用 POSIX 的 `read` / `case` / `printf` / `exit` —— 没有重定向、没有 `rm`、
 *   没有任何会改到这台机器的东西。命令原文写死在这里,是为了让人一眼读完。
 */
function stopOnceHook(): HookDefinition {
  return {
    id: 'stop-once',
    type: 'command',
    event: 'Stop',
    command: [
      'read -r line || true',
      'case "$line" in',
      `  *'"stop_hook_active":false'*) printf '%s\\n' 'the transcript has no evidence yet — keep working' >&2; exit 2 ;;`,
      'esac',
      'exit 0'
    ].join('\n'),
    enabled: true,
    timeoutMs: 10_000
  }
}
