/**
 * per-session store —— 方案 §8。
 *
 * 两件事在这里同时成立,而它们互为因果:
 *
 * 1. **流式文本必须住在这里,不能住在全局 store**。放全局的话,每个 token 都会
 *    让订阅了全局 store 的 Tab 栏、侧边栏跟着重渲染 —— 一秒钟几十次全树 diff。
 * 2. **懒创建、工作区关闭时销毁**。会话可能有几百个,每个都建 store 是白占内存。
 *
 * 加上主进程侧 16–33ms 合批和这里的 rAF 再缓冲,就是全部的流式性能方案。
 * 不做 ack/流控 —— 真背压是研究课题(方案 §10)。
 */
import { useMemo } from 'react'
import { create, type UseBoundStore, type StoreApi } from 'zustand'
import type { PermissionMode } from '../../../shared/agent/permission'
import type { SessionChange, SessionPage } from '../../../shared/domain/session'
import type { AgentEvent } from '../../../shared/agent/event'
import { mergeGoalStatusMessage, userMessage, type AgentMessage, type ContentPart } from '../../../shared/agent/message'
import { editUserMessage, editedParts, removeSpan, replySpan, turnSpan } from '../../../shared/agent/history-edit'
import type { ActiveGoal, GoalChange } from '../../../shared/domain/goal'
import { getGoal, onGoalChanged } from '../services/goal'
import type { SendOptions, SessionMode } from '../../../shared/agent/run-request'
import {
  applyEvents,
  applyChildEvent,
  emptyTranscript,
  hasRun,
  subagentsFromMessages,
  toolsFromMessages,
  type TranscriptState
} from '../../../shared/agent/transcript'
import type { QueuedInput, SessionQueueOp, SessionQueueSnapshot, SubagentReportStatus } from '../../../shared/domain/queued-input'
import { QUEUE_MAX_ITEMS, QUEUE_MAX_TEXT, partsToAttachments } from '../../../shared/domain/queued-input'
import type { AgentEventEnvelope } from '../../../shared/ipc/contract'
import { hasSeqGap } from '../../../shared/ipc/contract'
import { ulid } from '../../../shared/util/id'
import {
  abortRun,
  attachRun,
  interjectRun,
  onActiveRuns,
  onAgentEvent,
  onSessionQueueChanged,
  onSubagentReport,
  onWindowVisibility,
  queueSessionInput,
  reportBackground,
  setRunPermissionMode,
  startRun,
  unwatchRun
} from '../services/agent'
import { getSessionInput, persistSessionDraft } from '../services/app'
import { compactContext as compactSessionContext } from '../services/context'
import {
  deleteSessionReply,
  deleteSessionTurn,
  editSessionMessage,
  getSessionPage
} from '../services/sessions'

/**
 * ★ 类型本体已挪到 `shared/agent/run-request.ts` —— 队列条目要逐条冻结它,
 * 而 shared 不能反向依赖渲染层。这里 re-export 保持既有引用点不变。
 */
export type { SendOptions }

export interface SessionState {
  sessionId: string
  /** ★ 一个会话同一时刻只有一个 run(方案 §8)。null = 空闲 */
  activeRunId: string | null
  /** 已应用的最后一个 seq。防漂移就靠它 */
  lastSeq: number
  transcript: TranscriptState
  goal?: ActiveGoal
  goalVersion: number
  /**
   * 生成期间用户可以继续输入并入队(截图:「当前回复完成后按队列继续执行」)。
   *
   * ★ 从 `string[]` 升格为结构化条目:截图要求逐条插话/编辑/删除,
   * 而字符串数组里**两条内容相同的消息不可区分**。只含非终态条目。
   *
   * ★ **这是主进程那份队列的镜像,不是真源。** 入队、插话、续跑全在主进程
   * (`main/session-runtime.ts`)决定 —— 没有窗口在看这条会话时排队的消息也得续上。
   * 这里只按 `queueRev` 收更新的快照。
   */
  queuedInputs: QueuedInput[]
  /** 已应用的最后一份队列快照的版本号;回执与广播可能乱序,只收更新的 */
  queueRev: number
  /**
   * 手上这页之前库里还有更早的消息。转录只读最近的一页(`HISTORY_PAGE_SIZE`),
   * 更早的由 `loadEarlier` 按页往前取。
   */
  historyHasMore: boolean
  /**
   * 打开时那一页历史已经成功读回(库里本来就没有这条会话也算)。
   *
   * ★ 需求:长会话的首页要等一次 IPC,这段时间转录是空的 —— 只看 `messages` 的话
   * 视图会把它当成「全新会话」画成问候语 + 居中输入框,用户以为记录丢了。
   * 视图据此在读回来之前画骨架。
   */
  historyLoaded: boolean
  /** 读取失败不能当作空会话；保留错误直到重试或读到历史。 */
  historyError: string | null
  retryHistory: () => Promise<void>
  loadingEarlier: boolean
  /** 往前再取一页,接在手上那页前面 */
  loadEarlier: () => Promise<void>
  /**
   * 上一次发送用的档位/模型/模式。
   *
   * ★ 队列条目现在**各自带 `options`**,续跑读的是条目自己的快照,不是这个字段。
   * 它只剩两个用途:队列条目意外没有快照时的兜底,以及 UI 回显。
   *
   * ★★ **「重新生成」不读它** —— 那是用户此刻发起的新 run,该用此刻的药丸值。
   * 曾经读过,症状是:切了模型再点重新生成,跑的还是上一次那个模型。
   */
  lastOptions: SendOptions | null
  /** 输入框草稿 —— 切 Tab 不能丢,**进程重启也不能丢**(落盘,见 §8) */
  draft: string
  /** 手动压缩上下文进行中。圆环据此转圈,并挡住第二次双击。 */
  compacting: boolean
  /** 上一次手动压缩的失败原因,成功或再次发起时清掉。 */
  compactError: string | null

  send: (text: string, opts: SendOptions, parts?: ContentPart[], internal?: boolean, goalId?: string) => Promise<void>
  stop: () => Promise<void>
  /**
   * 手动压缩上下文(输入框那个圆环双击)。
   *
   * ★ **只在空闲时可用**:正在跑的那个 run 已经把自己的上下文投影冻在主进程里,
   * 此刻落检查点它不会读到,界面读数却已经跳了 —— 两边从此对不上。
   */
  compactContext: () => Promise<void>
  setDraft: (v: string) => void
  /** 把一段文字接在草稿后面(已有草稿时空一行),不覆盖用户正在写的内容。widget 的 `draft()` 走它 */
  appendDraft: (text: string) => void
  /**
   * widget 经 `host.setContext()` 发布、等着随下一条消息一起交给模型的状态摘要。
   * 键是 widget 的 callId:同一张卡再发布一次是替换,不是追加。
   *
   * ★ 只活在内存里、只在**直接起 run** 时取走:排队的消息由主进程续跑,带不上它,
   *   所以排队时它留着,等下一条直接发出的消息。
   */
  widgetContext: Array<{ key: string; title: string; text: string }>
  setWidgetContext: (key: string, title: string, text: string) => void
  removeWidgetContext: (key: string) => void
  /** 插话 —— toggle:pending ⇄ promoted。不发起任何请求 */
  promoteInput: (id: string) => void
  editInput: (id: string, text: string) => void
  /**
   * 权限档位药丸切换时调用:还没被消费的排队消息**改用新档位**,
   * 而不是继续背着入队那一刻冻结的旧档位;**以及**如果这个会话正有一个 run
   * 在跑,把新档位同步推给它(`agent:setPermissionMode`),让它接下来的工具
   * 调用立刻改用新档位,不必等这个 run 跑完、发一条新消息才生效。
   *
   * ★ 只改 `permissionMode` 这一个字段,不动 `options` 里的模型/思考强度/联网开关 ——
   * 那几个字段「逐条冻结」的既有设计(见 `queued-input.ts` 头注)仍然成立,
   * 这里是刻意为「审批档位」单独开的例外:用户切到完全访问,图的就是不想再被后面
   * 排着的每一条追问打断,那份意图理应立刻覆盖到整条队列和正在跑的这个 run,
   * 而不是只对新排的消息生效。
   */
  retagQueuedPermission: (mode: PermissionMode) => void
  /**
   * 方案批准执行时调用:还没被消费的排队消息**改回普通模式**,而不是继续
   * 背着「排队那一刻计划模式还没结束」时冻结的 `mode: 'plan'` ——
   * 否则这条消息出队续跑时会把用户刚退出的计划模式重新打开一轮只读审批,
   * 看起来就像点了「执行计划」根本没生效。和 `retagQueuedPermission` 一样,
   * 是「逐条冻结」原则下专门开的例外。
   */
  retagQueuedMode: (mode: SessionMode) => void
  /**
   * 编辑一条用户消息。`continueRun` = 从这条起重跑(界面上的「重新生成」)。
   *
   * ★ `options` 是**此刻**药丸上的档位/模型,不是这条消息当初发送时的那份 ——
   * 见实现里的注释。
   */
  editMessage: (id: string, text: string, continueRun: boolean, options: SendOptions) => Promise<void>
  /** 删掉一整轮问答(user 消息 + 它引出的全部回复与工具回执)。 */
  deleteTurn: (userMessageId: string) => Promise<void>
  /**
   * 只删一条助手回复(`fromId..toId` 两条 assistant 消息之间的全部,外加紧随其后的
   * 工具回执),**引出它的提问留着**。跨度来自 `ThreadRow.span`。
   */
  deleteReply: (fromId: string, toId: string) => Promise<void>
  dropInput: (id: string) => void
  /** 「⋯ → 撤回到输入框」:出队并回填草稿 */
  moveInputToDraft: (id: string) => void
  applyEnvelope: (env: AgentEventEnvelope) => void
  applyEvents: (events: AgentEvent[]) => void
  /** Apply a child run's telemetry to its parent Task card. */
  applyChildEvents: (childRunId: string, events: AgentEvent[], firstSeq?: number) => void
  /** Mark a detached child's result as being handed back to the main agent. */
  setSubagentReportStatus: (callId: string, status: NonNullable<TranscriptState['subagents'][string]['reportStatus']>) => void
}

type SessionStore = UseBoundStore<StoreApi<SessionState>>

function createSessionStore(sessionId: string): SessionStore {
  return create<SessionState>((set, get) => ({
    sessionId,
    activeRunId: null,
    lastSeq: 0,
    transcript: emptyTranscript(),
    goalVersion: 0,
    queuedInputs: [],
    queueRev: 0,
    historyHasMore: false,
    historyLoaded: false,
    historyError: null,
    retryHistory: () => hydrateHistory(sessionId, true),
    loadingEarlier: false,
    lastOptions: null,
    draft: '',
    widgetContext: [],
    compacting: false,
    compactError: null,

    async loadEarlier() {
      const s = get()
      const first = s.transcript.messages[0]
      if (!s.historyHasMore || s.loadingEarlier || first === undefined) return
      set({ loadingEarlier: true })
      try {
        const page = await getSessionPage(sessionId, HISTORY_PAGE_SIZE, first.id)
        set((state) => {
          // 等待期间转录被整段换过(刷新 / 删除):这一页接不上了,丢掉
          if (state.transcript.messages[0]?.id !== first.id) return { loadingEarlier: false }
          const known = new Set(state.transcript.messages.map((message) => message.id))
          const earlier = page.messages.filter((message) => !known.has(message.id))
          const messages = [...earlier, ...state.transcript.messages]
          return {
            loadingEarlier: false,
            historyHasMore: page.hasMore,
            transcript: {
              ...state.transcript,
              messages,
              // 新接上的那几轮的工具卡片与子代理卡片;已有的实时遥测原样保留
              tools: toolsFromMessages(messages, state.transcript.tools),
              subagents: subagentsFromMessages(messages, state.transcript.subagents),
              messageRuns: { ...page.messageRuns, ...state.transcript.messageRuns }
            }
          }
        })
      } catch (err) {
        set({ loadingEarlier: false })
        console.error('[agent] 读取更早的消息失败:', err)
      }
    },

    async send(text, opts, parts, internal = false, goalId) {
      const s = get()
      // ★ 不变式:一个会话同一时刻只有一个 run。用户连按两次回车就能并发起两个 run,
      // 共享同一份转录 → 消息交错。这几行就是那条不变式的全部实现。
      if (s.activeRunId !== null) {
        if (internal) {
          const reportParts = parts ?? [{ type: 'text' as const, text }]
          await interjectRun(s.activeRunId, [{ id: ulid(), parts: reportParts, internal: true, ...(goalId === undefined ? {} : { goalId }) }])
          return
        }
        // ★ 软上限:超过就不是队列了,是便签本。**拒绝入队并保留草稿** ——
        // 静默丢弃会让用户以为消息进了队列。
        if (s.queuedInputs.length >= QUEUE_MAX_ITEMS) return
        set({ draft: '' })
        persistDraft(sessionId, true)
        // ★ 逐条冻结 options:排队 5 分钟里改两次模型,三条消息该有三份档位。
        //   ★ 附件也必须一起存 —— 不存的话,生成期间带图发的那条消息
        //     续跑时会只剩文字,图静默消失,而用户明明看到自己发了图。
        const result = await queueSessionInput(sessionId, {
          kind: 'enqueue',
          text: text.slice(0, QUEUE_MAX_TEXT),
          options: opts,
          attachments: parts === undefined ? [] : partsToAttachments(parts)
        })
        applyQueueSnapshot(sessionId, result)
        // 主进程那边已经满了(另一个窗口刚排满):把话还给输入框,而不是让它凭空消失
        if (!result.accepted && get().draft === '') {
          set({ draft: text })
          persistDraft(sessionId, true)
        }
        return
      }

      // ★ 渲染层 mint runId,**订阅在前、启动在后**(方案 §3 规则 2)。
      // registerRun 把 runId → sessionId 的映射登记好,这样第一个事件回来时
      // 泵知道该投给谁 —— 而 startRun 的 await 还没返回。
      const runId = ulid()
      // 先把最终会发给主进程的 parts 固定下来。附件和纯文本都必须在这条
      // 乐观消息里完整呈现,否则用户会先看到与实际请求不同的内容。
      const input = parts ?? [{ type: 'text' as const, text }]
      const inputMessageId = ulid()
      const now = Date.now()
      const inputMessage = { ...userMessage(inputMessageId, [...input], now), ...(internal ? { internal: true } : {}) }
      registerRun(runId, sessionId, opts.workspaceId)
      // widget 的状态摘要随这条(用户亲手发的)消息走一次;内部消息不带
      const widgetContext = internal ? [] : s.widgetContext.map(({ title, text: body }) => ({ title, text: body }))
      set({
        ...(internal ? {} : { widgetContext: [] }),
        activeRunId: runId,
        lastSeq: 0,
        // ★ 只清「本轮」的部分。`messages` 和 `tools` 必须留着 —— 它们是**整段对话**,
        // 不是这一个 run 的产物。整个 `emptyTranscript()` 换上去的话,发第二条消息
        // 就会把第一轮的问答从屏幕上抹掉。
        //
        transcript: {
          ...emptyTranscript(),
          messages: [...s.transcript.messages, inputMessage],
          tools: s.transcript.tools,
          // Background children can outlive the parent turn. Keep their cards
          // visible when the user starts another parent turn in this session.
          subagents: s.transcript.subagents,
          // 历史轮次的账,和 `messages` 一样属于整段对话而不是这一个 run。
          ...conversationScoped(s.transcript),
          runStartedAt: now
        },
        lastOptions: opts,
        draft: internal ? s.draft : ''
      })
      if (!internal) persistDraft(sessionId, true)

      const rollback = (): void => {
        unregisterRun(runId)
        set((state) => ({
          activeRunId: null,
          // 主进程没有起这个 run,所以撤掉本地乐观副本。
          // 用 ID 删除而不是按末尾位置删除,避免并发的历史刷新改变数组顺序。
          transcript: {
            ...state.transcript,
            messages: state.transcript.messages.filter((message) => message.id !== inputMessageId),
            live: [],
            status: 'done',
            runStartedAt: undefined,
            runEndedAt: undefined
          }
        }))
      }
      let started: Awaited<ReturnType<typeof startRun>>
      try {
        started = await startRun({ ...opts, runId, sessionId, input, inputMessageId, ...(internal ? { inputInternal: true } : {}),
          ...(goalId === undefined ? {} : { inputGoalId: goalId }),
          ...(widgetContext.length === 0 ? {} : { widgetContext }) })
      } catch (err) {
        rollback()
        throw err
      }
      if (started.started) return
      /*
        ★ 主进程那边这条会话其实已经在跑(排队的消息刚被续上,或者另一个窗口先发了),
        这句话被放进了队列。撤掉乐观消息,接上真正在跑的那个 run —— 它的角标广播
        可能比这条回执先到,那时 `adoptActiveRuns` 因为本地还挂着乐观 runId 而跳过了它。
      */
      rollback()
      applyQueueSnapshot(sessionId, started.queue)
      const actual = [...runIndex.values()].find((run) => run.sessionId === sessionId)
      if (actual !== undefined && get().activeRunId === null) {
        set({ activeRunId: actual.runId })
        void ensureActiveRunRestored(sessionId, actual.runId)
      }
    },

    async stop() {
      const runId = get().activeRunId
      if (runId === null) return
      // 状态不在这里改 —— 等 run_end 事件回来。中断路径上主进程还要做收尾
      // (方案 §4.8 的五件事),UI 抢先置成 idle 就看不到那个过程了。
      await abortRun(runId, true)
    },

    async compactContext() {
      const s = get()
      if (s.activeRunId !== null || s.compacting) return
      set({ compacting: true, compactError: null })
      try {
        const { message, inputTokens } = await compactSessionContext(s.sessionId)
        set((st) => ({
          transcript: {
            ...st.transcript,
            /*
              ★ 本地先把边界消息接上,不等 hydrate。
              手动压缩走的是一条**没有 run** 的 IPC,不会有 `message_commit` 事件推回来;
              只依赖下一次全量回填的话,症状是点完 /compact 界面上什么都没变,
              用户会以为没生效并再点一次 —— 而每一次都是一次真实的摘要请求。
              按 id 去重是为了和随后的 hydrate 对上,不至于出现两条同样的分隔线。
            */
            messages: st.transcript.messages.some((m) => m.id === message.id)
              ? st.transcript.messages
              : [...st.transcript.messages, message],
            /*
              ★ 换成**估算值**,而不是等下一轮真实 usage 回来。
              等的话,用户压完看到读数纹丝不动,只会再点两次 ——
              而每一次都是一次真实的摘要请求。估算与真实的差距(系统提示词、
              工具 schema 不在内)远小于「界面看起来没反应」的代价。
            */
            lastInputTokens: inputTokens
          }
        }))
      } catch (err) {
        set({ compactError: err instanceof Error ? err.message : String(err) })
      } finally {
        set({ compacting: false })
      }
    },

    setDraft(v) {
      set({ draft: v })
      // ★ 按键级频率,走防抖。丢失窗口 ≤500ms,代价是半个词
      persistDraft(sessionId, false)
    },

    appendDraft(text) {
      if (text === '') return
      set((state) => ({ draft: state.draft === '' ? text : `${state.draft}\n\n${text}` }))
      persistDraft(sessionId, true)
    },

    setWidgetContext(key, title, text) {
      set((state) => {
        const rest = state.widgetContext.filter((entry) => entry.key !== key)
        return { widgetContext: text.trim() === '' ? rest : [...rest, { key, title, text }] }
      })
    },

    removeWidgetContext(key) {
      set((state) => ({ widgetContext: state.widgetContext.filter((entry) => entry.key !== key) }))
    },

    /**
     * 插话 —— **toggle**。用户点第二次的意图明确就是取消,报错或无操作都不对。
     * 排序、空闲时立即发送、推给正在跑的 run,全由主进程的队列决定。
     */
    promoteInput(id) {
      if (!get().queuedInputs.some((q) => q.id === id)) return
      runQueueOp(sessionId, { kind: 'promote', id })
    },

    editInput(id, text) {
      // ★ 不重置档位快照,也不重置 promotedAt(编辑不改变加塞顺序)
      runQueueOp(sessionId, { kind: 'edit', id, text: text.slice(0, QUEUE_MAX_TEXT) })
    },

    retagQueuedPermission(mode) {
      const s = get()
      if (s.activeRunId !== null) {
        void setRunPermissionMode(s.activeRunId, mode).catch((err: unknown) => {
          console.error('[agent] 同步权限档位失败:', err)
        })
      }
      if (s.queuedInputs.length === 0) return
      runQueueOp(sessionId, { kind: 'retagPermission', mode })
    },

    retagQueuedMode(mode) {
      if (get().queuedInputs.length === 0) return
      runQueueOp(sessionId, { kind: 'retagMode', mode })
    },

    async editMessage(id, text, continueRun, options) {
      const s = get()
      if (s.activeRunId !== null) return
      const original = s.transcript.messages.find((message) => message.id === id && message.role === 'user')
      if (original === undefined) return
      // Keep attachments and other structured parts, while replacing the
      // visible text as one canonical part so stale text fragments cannot
      // survive an edit.
      const parts: ContentPart[] = editedParts(original, text)
      const messages = editUserMessage(s.transcript.messages, id, text, continueRun)
      if (messages === null) return
      /*
        ★ 按 id 交给主进程在**完整历史**上改 —— 这里手上只有一页,拿这一页整段
        `replaceHistory` 会把页外的历史当成「删掉了」。本地这一页做同样的改动来更新显示。
      */
      await editSessionMessage(sessionId, id, text, continueRun)
      set((state) => ({
        transcript: {
          ...state.transcript,
          messages: editUserMessage(state.transcript.messages, id, text, continueRun) ?? messages,
          // 改写历史同样让窗口占用的读数过期,理由同 `deleteTurn` 里那段注释。
          // 截断重跑(`continueRun`)会立刻发起新请求把它填回来,不截断的纯文本编辑
          // 则等到下一次发送 —— 两种情况显示 `–` 都比显示一个对不上的数诚实。
          lastInputTokens: undefined,
          contextUsage: undefined,
          live: continueRun ? [] : state.transcript.live,
          /*
            需求:截断重跑之后,**留下来的那部分历史**里的工具卡片仍要认得出自己。

            ★ 这两行曾经是 `tools: {}` / `subagents: {}`,而它就是用户报的那条卡片的
            成因:一次生图跑完、之后编辑(或点「重新生成」)一条**更晚**的消息重跑,
            更早那张生成图卡片会退回「等待」+「生成中」。判据是 `tools[callId]` 还在
            不在 —— 不在时 `ToolCallCard` 把行状态读成 `pending`(「等待」),而
            `ImageGenDetail` 拿到的 `output` 是 `undefined`,图像卡的未结算分支就摆出
            加载格(「生成中」)。input 来自消息里的 `tool_call` part,所以提示词照常
            显示 —— 看上去像「这一张还没生成完」,而它其实早就落库了。

            ★ 而且它不会自己好:`loadHistory` 在 run 期间拒绝水合,run 正常结束后也
            没有人再水合一次,于是这份缺失一直留到重开这个会话。

            清空的**本意**只是「别把被切掉那一轮的工具状态留着」,`toolsFromMessages`
            正好只做这件事:留下的消息重建,切掉的 callId 自然不在结果里。把当前值当
            `live` 传进去是照 `loadHistory` 的口径 —— 已提交的 part 说了算,`live` 只
            补它没有的(耗时、后台子代理的遥测)。
          */
          tools: continueRun ? toolsFromMessages(messages, state.transcript.tools) : state.transcript.tools,
          subagents: continueRun ? subagentsFromMessages(messages, state.transcript.subagents) : state.transcript.subagents,
          ...(continueRun ? { status: 'done' as const, error: undefined, usage: undefined } : {})
        }
      }))
      if (continueRun) {
        /*
          ★★ 用**调用方此刻传进来的**档位,不是 `lastOptions`。
          「重新生成」/「编辑后续跑」是用户**此刻发起的一次新 run**,不是重放
          过去那次的意图 —— 而"换个模型再试一次"恰恰是按重新生成最主要的理由。
          这里读 `lastOptions` 的话,用户切了模型再点重新生成,跑的还是上一次
          那个模型,且界面上没有任何迹象说明为什么。
        */
        await get().send(text, options, parts)
      }
    },

    /**
     * 删掉一整轮。
     *
     * ★ **删除的单位是「跨度」,不是「一条消息」。** 一轮问答在存储里是
     * `user` → `assistant`(含 tool_call)→ `user`(其实是 tool_result 回执)
     * → `assistant` … 这样一长串。只删可见的那两条,留下来的 tool_result
     * 会失去与之配对的 tool_call —— 那对 Anthropic 形状是**非法请求**,
     * 下一次发消息才会炸,而那时早已看不出是这次删除干的。
     *
     * 所以跨度从这条 user 消息起,一直吃到**下一条可见的 user 消息之前**。
     * `isToolResultOnly` 正是「可见」的判据,与 Thread 的过滤同源。
     */
    async deleteTurn(userMessageId) {
      const s = get()
      if (s.activeRunId !== null) return
      const messages = s.transcript.messages
      const span = turnSpan(messages, userMessageId)
      if (span === null) return
      const next = removeSpan(messages, span)
      // 按 id 在完整历史上删,理由同 `editMessage`
      await deleteSessionTurn(sessionId, userMessageId)
      // 删到尾巴时,残留的 usage/error 说的是一个已经不存在的回合。
      const trailing = span[1] >= messages.length
      set((state) => ({
        transcript: {
          ...state.transcript,
          messages: next,
          /*
            ★ 这两个读数量的是「**这段历史**有多大」,不是某一轮的账 —— 删掉任意一轮
            之后它们描述的那段历史就不存在了,而圆环和压力条会原样继续显示那个旧数
            (把消息全删光也照样挂着「81K / 272K」)。所以不分末轮与否,一律清掉。

            ★ 和紧挨着的 `usage` 分开处理,不要合并进那个 `trailing` 分支:
            `usage` 是**最后一轮的账单**,删中间一轮跟它无关(见 turn-delete 用例);
            而窗口占用被**任何一次**删除改写。两者只是恰好都住在 transcript 上。

            ★ 清成 `undefined` 而不是就地重估。只按剩下的消息估会漏掉系统提示词和
            工具 schema(见 `estimateTools` 上的注释,挂几个 MCP 就是两三万 token),
            那是拿一个新的假数换掉旧的假数。`undefined` 让圆环显示 `–` ——
            「还不知道」,下一次真实请求回包时它自己就回来了。
          */
          lastInputTokens: undefined,
          contextUsage: undefined,
          // status 也要复位:被删掉的那一轮留下的「已停止 / 出错」说的是一个已经不存在的回复。
          ...(trailing ? { error: undefined, usage: undefined, runStartedAt: undefined, runEndedAt: undefined, status: 'done' as const } : {})
        }
      }))
    },

    /**
     * 只删一条助手回复,提问留着。
     *
     * ★ 末端要**吃掉紧随其后的工具回执**。回复以 tool_call 收尾(跑到一半被停、
     * 或者报错)时,它的 tool_result 在 `toId` 后面;留下来就是一条失去配对的
     * tool_result —— 与 `deleteTurn` 防的是同一种非法请求。
     *
     * ★ 只吃纯工具回执,别的一律停下:可见提问、后台汇报、压缩边界都是下一行的东西。
     */
    async deleteReply(fromId, toId) {
      const s = get()
      if (s.activeRunId !== null) return
      const messages = s.transcript.messages
      const span = replySpan(messages, fromId, toId)
      if (span === null) return
      const next = removeSpan(messages, span)
      await deleteSessionReply(sessionId, fromId, toId)
      const trailing = span[1] >= messages.length
      set((state) => ({
        transcript: {
          ...state.transcript,
          messages: next,
          // 窗口占用与末轮账单的处理口径同 `deleteTurn`,理由见那里的注释。
          lastInputTokens: undefined,
          contextUsage: undefined,
          // status 也要复位:被删掉的那一轮留下的「已停止 / 出错」说的是一个已经不存在的回复。
          ...(trailing ? { error: undefined, usage: undefined, runStartedAt: undefined, runEndedAt: undefined, status: 'done' as const } : {})
        }
      }))
    },

    dropInput(id) {
      // 删掉一条已引入的条目 = 从 run 的信箱里撤回它(主进程一并处理)
      runQueueOp(sessionId, { kind: 'drop', id })
    },

    moveInputToDraft(id) {
      if (!get().queuedInputs.some((q) => q.id === id)) return
      void queueSessionInput(sessionId, { kind: 'take', id }).then((result) => {
        applyQueueSnapshot(sessionId, result)
        if (result.text === undefined) return
        const taken = result.text
        // 已有草稿时接在后面,不覆盖 —— 覆盖会吞掉用户正在写的半句话
        set((state) => ({ draft: state.draft === '' ? taken : `${state.draft}\n\n${taken}` }))
        persistDraft(sessionId, true)
      }).catch((err: unknown) => {
        console.error('[agent] 撤回排队消息失败:', err)
      })
    },

    applyEnvelope(env) {
      const s = get()
      if (env.runId !== s.activeRunId) return
      if (env.seq <= s.lastSeq) return

      // ★ 防漂移(方案 §3 规则 1)。这个 ±1 不在这里手算 —— 它与合批泵
      // 造信封的那行是**一对**,两边对「seq 指哪个事件」的理解必须完全一致。
      if (hasSeqGap(env, s.lastSeq)) {
        void resync(sessionId, env.runId, s.lastSeq)
        return
      }

      const transcript = recordMessageRuns(
        archiveRunUsage(applyEvents(s.transcript, env.events), env.runId, env.events),
        env.runId,
        env.events
      )
      // 续跑、插话回执、后台汇报都由主进程的会话运行时处理;这里只管显示
      set({
        transcript,
        lastSeq: env.seq,
        ...settleRun(env.runId, env.events)
      })
    },

    applyEvents(events) {
      const runId = get().activeRunId
      const transcript = recordMessageRuns(
        archiveRunUsage(applyEvents(get().transcript, events), runId, events),
        runId,
        events
      )
      set({
        transcript,
        ...settleRun(runId, events)
      })
    },

    applyChildEvents(childRunId, events, firstSeq) {
      set((state) => {
        const transcript = events.reduce(
          (current, event, index) => applyChildEvent(
            current,
            childRunId,
            event,
            firstSeq === undefined ? undefined : firstSeq + index
          ),
          state.transcript
        )
        return { transcript }
      })
    },

    /**
     * 主进程汇报到哪一步了(`session:subagentReport`)。**只改本地显示** ——
     * 回执的持久化在主进程;渲染层再整段 `replaceHistory` 一遍的话,
     * 分页之后它手里根本没有整段历史。
     */
    setSubagentReportStatus(callId, status) {
      set((state) => {
        const current = state.transcript.subagents[callId]
        if (current === undefined || current.reportStatus === status) return state
        const messages = state.transcript.messages.map((message) => message.parts.some((part) => part.type === 'tool_result' && part.callId === callId && part.subagent !== undefined)
          ? {
              ...message,
              parts: message.parts.map((part) => part.type === 'tool_result' && part.callId === callId && part.subagent !== undefined
                ? { ...part, subagent: { ...part.subagent, reportStatus: status } }
                : part)
            }
          : message)
        return { transcript: { ...state.transcript, messages, subagents: { ...state.transcript.subagents, [callId]: { ...current, reportStatus: status } } } }
      })
    }
  }))
}

/**
 * 用户在后台子代理卡片上点「处理」—— 重启之后,或者自动那一趟被挡住(blocked)之后。
 *
 * ★ 汇报本身在主进程(`main/session-runtime.ts`):全文、档位、起 run 还是进信箱,
 * 都在那边决定。自动那一趟根本不经过这里 —— 子代理跑完时没有窗口在看也得交差。
 *
 * `fallback` 由 React 那一侧按工作区默认值拼出来:本进程没见过这条会话发消息时
 * (重启之后),主进程靠它才发得出去;连它都没有时主进程置 `blocked`,而不是让
 * 那颗按钮成为一个按下去什么都不发生的死键。
 */
export async function reportBackgroundChild(
  sessionId: string,
  callId: string,
  fallback?: SendOptions
): Promise<void> {
  try {
    const { status } = await reportBackground(sessionId, callId, fallback)
    stores.get(sessionId)?.getState().setSubagentReportStatus(callId, status)
  } catch (error) {
    console.error('[agent] background subagent report failed:', error)
  }
}

/**
 * run 结束时把实时累计用量并入会话级索引。这样连续发送多轮时，无需等待下一次
 * SQLite hydrate，输入框下方的会话累计也不会漏掉刚结束的历史轮次。
 */
function archiveRunUsage(
  transcript: TranscriptState,
  runId: string | null,
  events: readonly AgentEvent[]
): TranscriptState {
  if (runId === null || transcript.usage === undefined || !events.some((event) => event.type === 'run_end')) {
    return transcript
  }
  return {
    ...transcript,
    runUsage: { ...transcript.runUsage, [runId]: transcript.usage }
  }
}

/**
 * 把「这条消息是哪个 run 产出的」当场记进转录。
 *
 * ★ **这是流式路径上唯一的写入点**,漏掉它的症状很隐蔽:`messageRuns` 以前只在
 * `hydrateHistory` 里从 SQLite 回填,于是刚跑完的那一轮 `row.runId` 始终是
 * undefined —— `TurnChangeReview` 据此整卡 `return null`,表现为「这一轮明明改了
 * 11 个文件,底部的改动审查卡不出现,重开应用(触发一次 hydrate)才看见」,
 * 而且全程零报错。逐轮用量、逐轮模型名走的也是这条查表路径,同病同治。
 *
 * 口径跟库里那张表一致(`main/db/repo.ts` 的 `messageRunsOf`):一个 run 期间提交的
 * 消息全部归它,不分角色 —— 这样这份内存映射和下一次 hydrate 回来的那份不会打架。
 * `runId === null` 时**什么都不记**:宁可不显示,也不给一个猜出来的归属
 * (「不知道属于哪个 run」和「属于某个 run」在界面上是两件事)。
 */
function recordMessageRuns(
  transcript: TranscriptState,
  runId: string | null,
  events: readonly AgentEvent[]
): TranscriptState {
  if (runId === null) return transcript
  const messageRuns = { ...transcript.messageRuns }
  let added = false
  for (const event of events) {
    if (event.type !== 'message_commit') continue
    if (messageRuns[event.message.id] === runId) continue
    messageRuns[event.message.id] = runId
    added = true
  }
  return added ? { ...transcript, messageRuns } : transcript
}

/**
 * run 走到终局时的收尾:把会话置回空闲,**并把它从运行中索引里摘掉**。
 *
 * ★ **两件事必须一起做,所以这个函数不是纯的** —— 名字里的 settle 就是这个意思。
 * 「运行中」那颗圆点读的是 `runIndex`(全局、不随 Tab 开关消失),而输入框和状态行
 * 读的是会话 store 的 `activeRunId`。**两份状态,得同时收。**
 *
 * 曾经只收了后一半:`unregisterRun` 只写在 `send` 的 catch 里,于是
 * **只有启动失败的 run 会被摘掉**,正常跑完的那些圆点一直亮着 —— 外层工作区 Tab、
 * 内层对话 Tab、侧边栏会话行同时挂着三颗,直到关掉工作区才消。
 * 状态行明明写着「已完成」,旁边圆点还在转,是截图里一眼就看出来的那种错。
 *
 * 出队续跑不在这里,也不在渲染层:主进程的会话运行时在 run 收尾落盘之后决定。
 */
function settleRun(runId: string | null, events: readonly AgentEvent[]): Partial<SessionState> {
  if (!events.some((e) => e.type === 'run_end')) return {}
  if (runId !== null) unregisterRun(runId)
  return { activeRunId: null }
}

/**
 * 用户手动继续 —— 中断/报错/进程重启后队列不会自己动,由这里接手。
 * 与自动续跑走同一条路径(主进程的 `drain`),不存在第二套发送逻辑。
 */
export function resumeQueue(sessionId: string): void {
  runQueueOp(sessionId, { kind: 'resume' })
}

// ═══════════════════════════════════════════════════════════════
// 队列 —— 主进程那份的镜像
// ═══════════════════════════════════════════════════════════════

/**
 * 发一个队列操作,并把回执里的权威队列应用到本地。
 *
 * ★ 失败只记日志:条目原样留在主进程那边,下一次广播会把镜像拉回一致。
 */
function runQueueOp(sessionId: string, op: SessionQueueOp): void {
  void queueSessionInput(sessionId, op)
    .then((result) => applyQueueSnapshot(sessionId, result))
    .catch((err: unknown) => {
      console.error('[agent] 队列操作失败:', err)
    })
}

/**
 * 收一份队列快照。回执与 `session:queueChanged` 广播可能乱序到达,
 * 所以只收 `rev` 更新的那份。没建 store 的会话不收 —— 打开时 `hydrateInput` 会读到最新的。
 */
function applyQueueSnapshot(sessionId: string, snapshot: SessionQueueSnapshot): void {
  const store = stores.get(sessionId)
  if (store === undefined) return
  if (snapshot.rev <= store.getState().queueRev) return
  store.setState({ queuedInputs: snapshot.queued, queueRev: snapshot.rev })
}

// ═══════════════════════════════════════════════════════════════
// 持久化 —— 未发出的输入是全应用唯一没有第二份副本的数据(设计 §8)
// ═══════════════════════════════════════════════════════════════

/**
 * ★ 已发出的内容在转录里有据可查,**只有未发出的输入是唯一副本** ——
 * 进程一死就永久消失,用户连「我刚才写了什么」都无从追溯。
 * 这里只落草稿:队列由主进程独占,它自己落盘。
 */
function persistDraft(sessionId: string, immediate: boolean): void {
  const store = stores.get(sessionId)
  if (!store) return
  persistSessionDraft(sessionId, store.getState().draft, immediate)
}

/** 已发起过 hydrate 的会话。重复调用是无害的,但白费一次 IPC */
const hydrated = new Set<string>()

/**
 * 打开一条会话时读多少条(之后往上翻一次再取这么多)。服务端会把页首补到一轮的开头,
 * 所以实际条数可能略多。
 *
 * 需求:长会话不再把整段历史 —— 连同每次工具输出与截图 —— 一次性读进渲染层。
 */
export const HISTORY_PAGE_SIZE = 200
/**
 * 打开一条会话时**首屏**读多少条。
 *
 * ★ 比翻页那一档小:首屏只需要盖满视口,滚到顶会自动接着取(`Thread` 的顶部哨兵)。
 * 长会话里一条工具回执就可能是几十 KB,首屏读 200 条意味着主进程解析、IPC 结构化克隆、
 * 渲染层逐条建卡片全都按 200 条付钱,而用户第一眼只看得到最后几轮。
 */
export const HISTORY_INITIAL_PAGE_SIZE = 60
/** 刷新时把已经翻出来的那几页一并重取,但再多就不要了 */
export const HISTORY_PAGE_MAX = 2_000

/** 刷新一个已经翻过页的会话时取多少条 —— 不把用户翻出来的那几页收回去 */
function pageLimitFor(transcript: TranscriptState | undefined): number {
  return Math.min(HISTORY_PAGE_MAX, Math.max(HISTORY_INITIAL_PAGE_SIZE, transcript?.messages.length ?? 0))
}
const deletedHistory = new Set<string>()
const historyLoads = new Map<string, {
  store: SessionStore
  authoritative: boolean
  dirty: boolean
  promise: Promise<void>
}>()

/**
 * 回填草稿与队列。
 *
 * ★ **回填必须带守卫**:IPC 往返期间用户可能已经打字或已经发送了。
 * 无条件 `setState` 会用旧快照盖掉刚敲进去的内容 —— 这是持久化最常见的翻车方式。
 * 同 `tabs.ts` 那条 `persisted.tabs.length > 0` 守卫的精神。
 *
 * ★ **恢复后不自动续跑**:即便队列非空且空闲,主进程也不会自己续 ——
 * 用户重启应用时绝不期待它自己开始发消息。进程死亡本质上就是一次异常中断,
 * 与 aborted 同等对待,由界面上的「继续执行」交还给用户。
 *
 * ★ 队列与草稿的守卫不一样:队列以主进程为准,只要这里还没收到过带版本号的快照
 * (`queueRev === 0`)就直接采用;草稿只在输入框还空着、也没在发送时才回填。
 */
async function hydrateInput(sessionId: string): Promise<void> {
  const store = stores.get(sessionId)
  if (!store || deletedHistory.has(sessionId)) return
  try {
    const saved = await getSessionInput(sessionId)
    if (saved === null || stores.get(sessionId) !== store || deletedHistory.has(sessionId)) return
    const s = store.getState()
    const patch: Partial<SessionState> = {}
    if (s.queueRev === 0 && s.queuedInputs.length === 0) patch.queuedInputs = saved.queued
    if (s.draft === '' && s.activeRunId === null) patch.draft = saved.draft
    if (Object.keys(patch).length > 0) store.setState(patch)
  } catch (err) {
    // 回填失败不该拦住会话可用 —— 最坏结果是少一份草稿
    console.error('[agent] 恢复未发出的输入失败:', err)
  }
}

/**
 * 会话级(而非 run 级)的转录状态。
 *
 * ★ 每一处 `...emptyTranscript()` 都是在「只清本轮」,而这两张表和 `messages`
 * 一样是**整段对话**的属性 —— 漏带一处,症状是发下一条消息、或者重挂一次快照
 * 之后,上面所有历史轮次的用量读数**一起消失**,而当前这一轮是好的。
 * 抽成函数就是不想在三个地方各记一次。
 */
function conversationScoped(
  t: TranscriptState
): Pick<TranscriptState, 'runUsage' | 'runModel' | 'messageRuns' | 'lastInputTokens'> {
  return {
    ...(t.runUsage === undefined ? {} : { runUsage: t.runUsage }),
    ...(t.runModel === undefined ? {} : { runModel: t.runModel }),
    ...(t.messageRuns === undefined ? {} : { messageRuns: t.messageRuns }),
    // 上下文占用是**整段对话**的属性:新一轮还没发出请求之前,
    // 圆环该继续显示上一轮结束时的读数,而不是空着。
    ...(t.lastInputTokens === undefined ? {} : { lastInputTokens: t.lastInputTokens })
    /*
      ★ 压缩边界不在这张表里 —— 它现在**就是 `messages` 里的一条消息**(见
      `shared/agent/compaction.ts`),跟着消息走,天然活得比任何一轮久。
      原先这里带着 `contextCheckpoints`,是因为检查点住在另一张表上,漏带就会让
      所有压缩分隔线在发下条消息时一起消失;换成转录内边界之后这条约束自动成立。
      ★ `contextStatus` 是**本轮**的瞬时相位,故意不带:上一轮的「正在压缩…」
      跟到新一轮就是一句谎话。
    */
  }
}

/** 重启后从 SQLite 回填已提交消息；流式 run 期间只合并缺少的 id。 */
function hydrateHistory(sessionId: string, authoritative = false): Promise<void> {
  const store = stores.get(sessionId)
  if (!store || deletedHistory.has(sessionId)) return Promise.resolve()
  // Active-run restoration already reads the page. Do not issue another read that will be discarded.
  const activeRunId = store.getState().activeRunId
  if (activeRunId !== null) return ensureActiveRunRestored(sessionId, activeRunId)
  const pending = historyLoads.get(sessionId)
  if (pending?.store === store) {
    pending.authoritative ||= authoritative
    pending.dirty ||= authoritative
    return pending.promise
  }
  const request = { store, authoritative, dirty: false, promise: Promise.resolve() }
  historyLoads.set(sessionId, request)
  store.setState({ historyError: null })
  request.promise = (async () => {
    do {
      request.dirty = false
      await loadHistory(sessionId, request)
    } while (request.dirty && historyLoads.get(sessionId) === request && !deletedHistory.has(sessionId))
  })().finally(() => {
    if (historyLoads.get(sessionId) === request) historyLoads.delete(sessionId)
  })
  return request.promise
}

async function loadHistory(sessionId: string, request: NonNullable<ReturnType<typeof historyLoads.get>>): Promise<void> {
  const store = request.store
  const before = store.getState().transcript
  const current = (): boolean => historyLoads.get(sessionId) === request
    && stores.get(sessionId) === store && !deletedHistory.has(sessionId) && !request.dirty
  try {
    const version = store.getState().goalVersion
    // ★ 一页,不是整段:往上翻过的那几页一并重取,刷新不会把它们收起来(有上限)
    const detail = await getSessionPage(sessionId, pageLimitFor(before))
    if (!current()) return
    void getGoal(sessionId).then((goal) => {
      if (stores.get(sessionId) === store && !deletedHistory.has(sessionId)
        && store.getState().goalVersion === version) store.setState({ goal })
    }).catch(() => undefined)
    store.setState((s) => {
      // IPC 往返期间可能刚好启动了新的 run；不要用旧数据库快照覆盖
      // 正在流式显示的内容。
      if (s.activeRunId !== null || [...runIndex.values()].some((r) => r.sessionId === sessionId)) return s
      if ([...childRunIndex.values()].some((r) => r.sessionId === sessionId)) return mergeRestoredHistory(s, detail, before.messages)
      if (s.transcript !== before) {
        if (hasRun(s.transcript, false)) return { historyLoaded: true, historyError: null }
        request.dirty = true
        return s
      }

      // 权威刷新可以删除消息;普通回填须保留页外旧轮次的位置,而不是一律追加到末尾。
      const messages = request.authoritative
        ? detail.messages
        : mergeHistoryMessages(detail.messages, s.transcript.messages, s.historyLoaded && detail.hasMore)
      const retainedPrefix = s.historyLoaded && messages[0]?.id !== detail.messages[0]?.id
      return {
        ...s,
        historyHasMore: detail.hasMore && (!retainedPrefix || s.historyHasMore),
        // 与转录同一次 setState:中间不能有一帧「已读完但转录还空着」被画成问候语
        historyLoaded: true,
        historyError: null,
        transcript: {
          ...s.transcript,
          messages,
          live: [],
          tools: toolsFromMessages(messages, s.transcript.tools),
          subagents: subagentsFromMessages(messages, s.transcript.subagents),
          // 重启之后逐轮用量的唯一来源。内存里那份 `usage` 只说得清当前 run,
          // 这两张表说的是整段对话的账,来自 SQLite。
          runUsage: detail.runUsage ?? s.transcript.runUsage,
          runModel: detail.runModel ?? s.transcript.runModel,
          messageRuns: detail.messageRuns ?? s.transcript.messageRuns,
          // 权威账已进入 runUsage；清掉同一轮的实时副本，避免会话累计重复计算。
          usage: undefined,
          status: 'done',
          runStartedAt: undefined,
          runEndedAt: undefined
        }
      }
    })
  } catch (err) {
    if (!current()) return
    // 新 Tab 可能还没有主进程会话记录；真正发送时 runtime 会补齐。
    if (err instanceof Error && /会话不存在|不存在该会话|session.*not found/i.test(err.message)) {
      store.setState((s) => {
        if (s.activeRunId !== null
          || [...runIndex.values()].some((r) => r.sessionId === sessionId)
          || [...childRunIndex.values()].some((r) => r.sessionId === sessionId)) return s
        if (s.transcript !== before) return { historyLoaded: true, historyError: null }
        return {
          ...s,
          historyLoaded: true,
          historyError: null,
          transcript: {
            ...s.transcript,
            messages: [],
            live: [],
            tools: {},
            subagents: {},
            status: 'done',
            error: undefined,
            usage: undefined,
            contextUsage: undefined,
            runStartedAt: undefined,
            runEndedAt: undefined
          }
        }
      })
    } else {
      store.setState({ historyError: err instanceof Error ? err.message : String(err) })
      console.error('[agent] 加载会话历史失败:', err)
    }
  }
}

// ═══════════════════════════════════════════════════════════════
// 注册表 + 单一事件泵
// ═══════════════════════════════════════════════════════════════

const stores = new Map<string, SessionStore>()

export function sessionStore(sessionId: string): SessionStore {
  let s = stores.get(sessionId)
  if (!s) {
    s = createSessionStore(sessionId)
    stores.set(sessionId, s)
    // 没有视图来接的 store 不常驻 —— 见 `retainSessionView`
    scheduleIdleDrop(sessionId)
    const active = [...runIndex.values()].find((run) => run.sessionId === sessionId)
    if (active !== undefined) {
      s.setState({ activeRunId: active.runId })
      void ensureActiveRunRestored(sessionId, active.runId)
    }
  }
  if (!hydrated.has(sessionId)) {
    hydrated.add(sessionId)
    if (!deletedHistory.has(sessionId)) {
      void hydrateInput(sessionId)
      void hydrateHistory(sessionId)
    } else {
      // 已删除的会话不再读库 —— 没人会把它标成「读过了」,骨架就会一直挂着
      s.setState({ historyLoaded: true })
    }
  }
  return s
}

// ═══════════════════════════════════════════════════════════════
// 视图持有 —— 没人在看的会话不留转录
// ═══════════════════════════════════════════════════════════════

/**
 * 一个没有任何视图在用的会话 store,多久之后放掉。
 *
 * 需求:切走或关掉的会话不再常驻整段转录,也不再接收它的正文 —— Agent 照常在主进程跑,
 * 续跑、汇报、目标检查都不需要渲染层(见 `main/session-runtime.ts`)。
 * ★ 留一小段宽限而不是立刻放:来回切两个 Tab 是常态,每切一次就整段重读一遍历史不划算。
 */
export const SESSION_IDLE_RELEASE_MS = 8_000

/** 每个会话 store 此刻被几个挂着的视图(对话视图、子代理只读面板)用着 */
const viewRefs = new Map<string, number>()
const idleDrops = new Map<string, ReturnType<typeof setTimeout>>()

/**
 * 视图挂载时调用,返回卸载时调用的释放函数(幂等)。最后一个视图走了之后,
 * 宽限期内没有再被持有,store 就被放掉。
 */
export function retainSessionView(sessionId: string): () => void {
  viewRefs.set(sessionId, (viewRefs.get(sessionId) ?? 0) + 1)
  cancelIdleDrop(sessionId)
  let released = false
  return () => {
    if (released) return
    released = true
    const next = (viewRefs.get(sessionId) ?? 1) - 1
    if (next > 0) {
      viewRefs.set(sessionId, next)
      return
    }
    viewRefs.delete(sessionId)
    scheduleIdleDrop(sessionId)
  }
}

function scheduleIdleDrop(sessionId: string): void {
  if ((viewRefs.get(sessionId) ?? 0) > 0) return
  cancelIdleDrop(sessionId)
  idleDrops.set(sessionId, setTimeout(() => {
    idleDrops.delete(sessionId)
    dropSessionStore(sessionId)
  }, SESSION_IDLE_RELEASE_MS))
}

function cancelIdleDrop(sessionId: string): void {
  const timer = idleDrops.get(sessionId)
  if (timer === undefined) return
  clearTimeout(timer)
  idleDrops.delete(sessionId)
}

/**
 * 放掉一个没有视图在用的会话 store —— **包括正在跑的那些**,并摘掉它的正文订阅。
 *
 * ★ 和 `releaseSession` 不是一回事:那个拒绝放掉正在跑的会话,是因为当年续跑、插话回执、
 * 后台汇报都长在 store 上。现在它们在主进程,store 只剩「显示」这一个用途 ——
 * 没人在看就没有理由留着。run 照跑,运行中索引(角标)不动;再打开时由
 * `sessionStore` 按历史 + 快照重建,和 ⌘R 重载走的是同一条路。
 *
 * ★ 草稿不在这里写:每次按键都已经交给主进程,防抖也由主进程持有。
 */
export function dropSessionStore(sessionId: string): boolean {
  if ((viewRefs.get(sessionId) ?? 0) > 0) return false
  const store = stores.get(sessionId)
  if (store === undefined) return false
  const state = store.getState()
  // 手动压缩的那一来一回还在路上:等它落地再放,否则分隔线会落进一个已经没人要的 store
  if (state.compacting) {
    scheduleIdleDrop(sessionId)
    return false
  }
  cancelIdleDrop(sessionId)
  stores.delete(sessionId)
  hydrated.delete(sessionId)
  historyLoads.delete(sessionId)
  for (const [runId, panel] of childSessionOfRun) {
    if (panel === sessionId) childSessionOfRun.delete(runId)
  }
  const watched = new Set<string>()
  if (state.activeRunId !== null) watched.add(state.activeRunId)
  for (const [runId, entry] of childRunIndex) {
    if (entry.sessionId === sessionId) watched.add(runId)
  }
  // 只有没有别的 store 还要它的正文时才摘 —— 父会话的卡片可能还在看同一个子 run
  for (const runId of watched) {
    if (!runStillWanted(runId)) void unwatchRun(runId).catch(() => undefined)
  }
  return true
}

function runStillWanted(runId: string): boolean {
  for (const store of stores.values()) {
    if (store.getState().activeRunId === runId) return true
  }
  const child = childRunIndex.get(runId)
  if (child !== undefined && stores.has(child.sessionId)) return true
  const panel = childSessionOfRun.get(runId)
  return panel !== undefined && stores.has(panel)
}

/**
 * 子 run id → **它自己那个会话**的 id。右侧只读面板的实时流就挂在这张表上。
 *
 * ★ 只有面板挂载时(`openChildSession`)才写进来,**不是每次 `subagent_start` 都写** ——
 * 一次编排能同时派出十几个后台子代理,而其中绝大多数永远不会被点开。没人打开 =
 * 这里查不到 = `drain` 少做一次 `applyEnvelope`,也不会凭空多出十几段完整转录。
 *
 * 结束时由 `drain` 顺手删掉(和 `childRunIndex` 同一处)。
 */
const childSessionOfRun = new Map<string, string>()

/**
 * 打开一个子代理的只读会话。
 *
 * 两条路合流,缺一不可:
 * - **已提交的那半边**零代码 —— `runtime.ts` 的 `onMessageCommit` 对子 run 也逐条
 *   写库,所以 `sessionStore()` 的 `hydrateHistory` 在子代理**跑到一半时**就读得到。
 * - **还在飞的那半边**靠这里:把 `activeRunId` 指过去、登记转发表,再让
 *   `ensureActiveRunRestored` 走一次 `attachRun` 把「开始到现在」补齐。之后的新块
 *   由 `drain` 实时喂。
 *
 * `childRunId` 不在 `childRunIndex` 里 = 这个子代理早就跑完了,库里那份就是全部,
 * 不必 attach 一个已经回收的 run。
 */
export function openChildSession(childSessionId: string, childRunId?: string): void {
  const store = sessionStore(childSessionId)
  if (childRunId === undefined || !childRunIndex.has(childRunId)) return
  childSessionOfRun.set(childRunId, childSessionId)
  // ★ 先置 activeRunId 再 attach:`applyEnvelope` 和 `resync` 都拿它当门禁,
  //   顺序反了的话这段 await 期间到达的信封会被原地丢掉。
  if (store.getState().activeRunId !== childRunId) store.setState({ activeRunId: childRunId })
  void ensureActiveRunRestored(childSessionId, childRunId)
}

/**
 * 工作区退场时,放掉这个会话在渲染层占的内存(主要是整段转录)。
 * 不放的话,开过的几百个会话会一直挂着。
 *
 * ★ **正在跑的会话放不掉,返回 `false`。** 这是方案 §8 那条试金石的下半段:
 * *run 活在主进程的 RunRegistry 里,渲染层这边关掉几个 Tab、乃至关掉整个工作区,
 * 都不该让它消失。* 曾经这里是连 `runIndex` 里的条目一起删的 —— 那等于**渲染层
 * 单方面忘掉一个还在跑的 run**:事件泵按 runId 反查会话查不到,事件被静默丢弃,
 * 重新打开工作区看到的是一段停在半路、而且再也不会动的转录。
 *
 * 判「在不在跑」读的是 `runIndex`,不是某个 store 的 `activeRunId`:⌘R 重载后
 * `adoptActiveRuns` 只补了索引,那个会话的 store 还没有人碰过(它是懒创建的),
 * 此刻 `activeRunId` 是 null,但 run 千真万确活着。
 */
export function releaseSession(sessionId: string): boolean {
  for (const r of runIndex.values()) if (r.sessionId === sessionId) return false
  for (const r of childRunIndex.values()) if (r.sessionId === sessionId) return false
  const deleted = stores.delete(sessionId)
  if (deleted) {
    cancelIdleDrop(sessionId)
    hydrated.delete(sessionId)
    historyLoads.delete(sessionId)
  }
  return deleted
}

/**
 * 草稿 Tab 铸出真 sessionId 那一刻,把它在渲染层的家当搬过去。
 *
 * ★ **搬的是草稿文本,而这不是整洁癖。** 触发绑定的两件事之一是「贴第一个附件」,
 * 而绑定会改 `tab.ref.sessionId` → `views/registry.tsx` 的 key 跟着 `chatKey` 变
 * → ChatView 整棵子树重挂,输入框从新键的 store 里取 draft。不搬的话,用户
 * 刚打的半句话会在贴图那一瞬间凭空消失,而屏幕上没有任何东西解释发生了什么。
 *
 * 队列不用搬:草稿没有 run,`send` 在空闲态直接发,`queuedInputs` 必然是空的。
 *
 * ★ 旧存档要**立即**清(`immediate = true`)。留着的话,下一个恰好复用这个 tabId
 * 的草稿(重启后 Tab id 是从盘里读回来的,它会一直是同一个)会把这段文字捡回来,
 * 表现是一张本该干净的白纸上莫名其妙有半句上辈子的话。
 */
export function adoptDraftSession(draftKey: string, sessionId: string): void {
  if (draftKey === sessionId) return
  const draftStore = stores.get(draftKey)
  const draft = draftStore?.getState().draft ?? ''
  // 刚铸出来的 id,库里必然是空的:不必等首页回来才认定它是新会话(否则重挂那一下会闪一帧骨架)
  sessionStore(sessionId).setState(draft === '' ? { historyLoaded: true } : { draft, historyLoaded: true })
  persistSessionDraft(draftKey, '', true)
  releaseSession(draftKey)
}

/**
 * 主进程完成导入、恢复或清理后广播 `sessions:changed` 时调用。
 * 侧边栏列表会重新加载，但已经打开的 Tab 也必须同步数据库，否则
 * 删除历史后仍会继续显示旧 transcript。
 */
export async function refreshHydratedSessions(change?: SessionChange): Promise<void> {
  if (change?.kind === 'metadata' || change?.renamed !== undefined) return
  if (change?.kind === 'deleted') {
    for (const sessionId of change.sessionIds) {
      const store = stores.get(sessionId)
      if (store?.getState().activeRunId != null
        || [...runIndex.values()].some((run) => run.sessionId === sessionId)
        || [...childRunIndex.values()].some((run) => run.sessionId === sessionId)) continue
      deletedHistory.add(sessionId)
      historyLoads.delete(sessionId)
      store?.setState({ draft: '', queuedInputs: [], historyLoaded: true, historyError: null, transcript: { ...emptyTranscript(), status: 'done' } })
    }
    return
  }
  if (change?.kind === undefined || change.kind === 'reset') deletedHistory.clear()
  const ids = change?.kind === 'history' || change?.kind === 'messages'
    ? [...new Set(change.sessionIds)].filter((id) => stores.has(id))
    : [...stores.keys()]
  await Promise.all(ids.map(async (sessionId) => {
    if (stores.get(sessionId)?.getState().activeRunId != null
      || [...runIndex.values()].some((r) => r.sessionId === sessionId)
      || [...childRunIndex.values()].some((r) => r.sessionId === sessionId)) return
    await hydrateHistory(sessionId, true)
  }))
}

/**
 * 这个会话有没有被用过 —— 「点侧边栏的『新建对话』时能不能重用现成的那一个」的判据。
 *
 * ★ **不能拿 `sessionStore(id)` 来问。** 那个函数是懒创建的,用它探测会把每个
 * chat Tab 的 store 都建出来,正好把本文件开头第 2 条(懒创建、关工作区时销毁)
 * 反过来变成「开过的会话全都常驻」。所以这里直接查注册表:**查不到就是没碰过**。
 *
 * 判据用的是 `ChatView` 决定画哪一屏的那个 `hasRun` —— 于是「屏幕上显示着问候语
 * 的那些对话」和「这里认为可以重用的那些」是同一批,两边不会各说各话。
 *
 * 草稿和排队中的输入另算:输入框里躺着半句话的会话不算空,用户这时候点
 * 「新建对话」要的是干净的一屏,而不是回到自己刚才写了一半的地方。
 */
export function isSessionUntouched(sessionId: string): boolean {
  const store = stores.get(sessionId)
  if (store === undefined) return true
  const s = store.getState()
  return (
    !hasRun(s.transcript, s.activeRunId !== null) &&
    s.draft.trim() === '' &&
    s.queuedInputs.length === 0
  )
}

/**
 * 运行中 run 的索引 —— Tab 上那个「运行中」圆点的数据源。
 *
 * ★ **它是 RunRegistry 的投影,不是 UI 状态**(方案 §8)。判断模型对不对的
 * 试金石就在这:*关掉最后一个正在观看某个运行中会话的 Tab,run 依然活着,
 * 且外层工作区 Tab 上仍显示角标。* 角标读这张表,而这张表不关心有没有 Tab 开着。
 *
 * 单独一个 store 而不是塞进 `useWindowStore`:它每个 run 起止各变一次,
 * 而 `useWindowStore` 一动整条 Tab 栏就重渲染。
 */
export interface RunIndexEntry {
  runId: string
  sessionId: string
  workspaceId: string
  /** 这个 run(含子代理)正等着用户处理的审批 / 提问数。缺席 = 0 */
  pendingInteractions?: number
}

const runIndex = new Map<string, RunIndexEntry>()
/** Child run id → the parent session that owns the Task card. */
const childRunIndex = new Map<string, RunIndexEntry>()

/** 快照数组。zustand 靠引用比较,所以每次变更换一个新数组。 */
export const useRunIndex = create<RunIndexEntry[]>(() => [])

function publishRunIndex(): void {
  useRunIndex.setState([...runIndex.values()], true)
}

function registerRun(runId: string, sessionId: string, workspaceId: string): void {
  runIndex.set(runId, { runId, sessionId, workspaceId })
  publishRunIndex()
}
function unregisterRun(runId: string): void {
  runIndex.delete(runId)
  publishRunIndex()
}

function rememberChildRuns(
  parentRunId: string,
  events: readonly AgentEvent[],
  owner?: RunIndexEntry
): void {
  const parent = runIndex.get(parentRunId) ?? owner
  if (parent === undefined) return
  for (const event of events) {
    if (event.type !== 'subagent_start') continue
    childRunIndex.set(event.childRunId, {
      runId: event.childRunId,
      sessionId: parent.sessionId,
      workspaceId: parent.workspaceId
    })
  }
}

/**
 * 首屏把主进程还活着的 run 补回索引(`Bootstrap.activeRuns`)。
 * 冷启动是空的 —— 「永不恢复运行中状态」(方案 §9);非空只发生在 ⌘R 重载:
 * 主进程没重启,run 还在跑,而渲染层刚刚失忆。
 */
export function adoptActiveRuns(runs: readonly RunIndexEntry[]): void {
  for (const r of runs) {
    runIndex.set(r.runId, r)
    const store = stores.get(r.sessionId)
    if (store !== undefined) {
      const activeRunId = store.getState().activeRunId
      // 新一轮广播可能先于旧 run_end 的 rAF 批次到达;旧运行已不在权威索引时立即接管。
      if (activeRunId === r.runId || (activeRunId !== null && runIndex.has(activeRunId))) continue
      store.setState((state) => ({
        activeRunId: r.runId,
        lastSeq: 0,
        transcript: {
          ...emptyTranscript(),
          messages: state.transcript.messages,
          tools: state.transcript.tools,
          subagents: state.transcript.subagents,
          ...conversationScoped(state.transcript)
        }
      }))
      void ensureActiveRunRestored(r.sessionId, r.runId)
    }
  }
  publishRunIndex()
}

/**
 * 把这份投影对齐到主进程推来的**权威集合**(`agent:activeRuns`)。
 *
 * 需求:三处「运行中」指示(外层工作区 Tab、内层对话 Tab、侧边栏会话行)读的都是
 * `runIndex`,而 `runIndex` 原先**只能**靠 `run_end` 事件摘条目。那条事件流按 run
 * 订阅定向推送,有两类 run 的结束永远送不到这个窗口:
 *
 * - 定时任务起的 run —— 没有任何窗口订阅过它,主进程侧整批丢弃(`RunPump.flush`);
 * - ⌘R 重载后被 bootstrap 补回索引、但对应会话 store 还没建出来的那些 ——
 *   `drain()` 按 runId 查到会话却 `stores.get()` 拿不到 store,信封原地丢掉。
 *
 * 不满足会怎样:**Agent 早就跑完了,角标还在转**,状态行写着「已完成」而
 * 工作区 Tab 上的圆点一直亮到应用重启,全程零报错。
 *
 * ★ 「多出来的」和「少掉的」两个方向都要收:只补不摘的话这个函数解决不了上面那条,
 * 只摘不补的话第二个窗口里新起的 run 在这个窗口就永远不显示。
 */
export function syncActiveRuns(entries: readonly RunIndexEntry[]): void {
  const authoritative = new Set(entries.map((entry) => entry.runId))
  const stale = [...runIndex.values()].filter((entry) => !authoritative.has(entry.runId))
  for (const entry of stale) {
    runIndex.delete(entry.runId)
    // 角标是收了,但那个会话自己的 `activeRunId` 也得收 —— 否则输入框、状态行
    // 和停止按钮还停在运行态(这正是用户说的「三个状态没同步」的第三个)。
    void settleMissedRun(entry.sessionId, entry.runId)
  }
  /*
    ★ 已在索引里的 run 也要跟上「有几条在等你处理」—— 这是没有窗口在看那条会话时,
    应用里唯一能看出「它停下来等你了」的地方。
  */
  for (const entry of entries) {
    const current = runIndex.get(entry.runId)
    if (current !== undefined && (current.pendingInteractions ?? 0) !== (entry.pendingInteractions ?? 0)) {
      runIndex.set(entry.runId, { ...current, pendingInteractions: entry.pendingInteractions ?? 0 })
    }
  }
  // 补的方向与 bootstrap 完全一样,所以直接复用它 —— 它顺带把已经建出来的 store
  // 接回这个 run 并 attach 补齐,那段逻辑不该有第二份。
  // ★ 放在最后调:它自己会 `publishRunIndex()`,上面那几次删除搭它这一趟车,
  //   于是一次广播只换一个新数组、只触发一次重渲染。
  adoptActiveRuns(entries.map((entry) => runIndex.get(entry.runId) ?? entry))
}

/**
 * 正等着用户处理(审批 / 回答)的会话。应用内标一下,不强制切换。
 */
export function useAttentionSessionIds(): ReadonlySet<string> {
  const runs = useRunIndex()
  return useMemo(
    () => new Set(runs.filter((run) => (run.pendingInteractions ?? 0) > 0).map((run) => run.sessionId)),
    [runs]
  )
}

/**
 * 正常路径上,`run_end` 事件和这条广播是**同一刻**从主进程出发的,只是事件那边还要
 * 过一次 rAF 合批才落到 store 上。这段宽限就是留给它的:等它落地,下面那次
 * attach 就完全不必发生。
 *
 * ★ 不等的话,**每一次正常结束**都会多打一次 `agent:attach` —— 一次没人需要的
 * IPC 往返,外加一行「seq 不连续,attach 补齐」的告警,而其实一个事件都没丢。
 * 250ms 是「肉眼看不出、又远大于一帧」的量级;它只影响**修复**的延迟,
 * 角标本身在收到广播的那一瞬就已经灭了。
 */
const MISSED_RUN_GRACE_MS = 250

/**
 * 主进程说这个 run 已经结束,而这个会话一个结束事件都没收到时的收尾。
 *
 * 先走一次 `resync`:attach 能把最后那截事件(含 `run_end`)补回来,于是用量、
 * 停止时刻、改动审查卡这些**只存在于事件里**的东西不会凭空缺一块。
 * ★ attach 失败(run 已被主进程回收)时必须有下半段:那时没有任何事件可补,
 * 但界面仍然必须离开运行态 —— 宁可少一截尾巴,也不能留一个永远转圈的会话。
 */
async function settleMissedRun(sessionId: string, runId: string): Promise<void> {
  if (stores.get(sessionId)?.getState().activeRunId !== runId) return
  await new Promise<void>((resolve) => setTimeout(resolve, MISSED_RUN_GRACE_MS))
  // 宽限期内 store 可能已经被释放(关掉工作区),或者已经开始了下一轮 ——
  // 两种情况都不该再动它,所以在这里重新取一次,不复用上面那个引用。
  const store = stores.get(sessionId)
  if (store === undefined || store.getState().activeRunId !== runId) return
  /*
    ★ 窗口藏着:不去 attach。attach 会把这个窗口重新订阅回来,还会把藏着期间的整段正文
    一次性灌进 store —— 那正是藏起来要省掉的东西。先离开运行态,露出来时再从库里读一遍。
  */
  if (!windowVisible) {
    store.setState((s) => ({
      activeRunId: null,
      transcript: { ...s.transcript, live: [], status: s.transcript.status === 'running' ? 'done' : s.transcript.status }
    }))
    staleWhileHidden.add(sessionId)
    return
  }
  await resync(sessionId, runId, store.getState().lastSeq)
  if (store.getState().activeRunId !== runId) return
  store.setState((s) => ({
    activeRunId: null,
    transcript: { ...s.transcript, live: [], status: s.transcript.status === 'running' ? 'done' : s.transcript.status }
  }))
  // 事件补不回来时,库里那份已提交的转录就是最完整的版本。
  void hydrateHistory(sessionId, true)
}

export interface ActiveSubagentIndexEntry {
  runId: string
  parentRunId: string
  sessionId: string
  workspaceId: string
  status?: 'running' | 'done' | 'error' | 'aborted'
  startedAt?: number
}

/**
 * Restore child-topic routing after a renderer reload. Top-level active runs
 * are restored by `adoptActiveRuns`; this separate index deliberately does
 * not feed the running indicators because a child is not a conversation run.
 */
export function adoptActiveSubagents(entries: readonly ActiveSubagentIndexEntry[]): void {
  const detachedParents = new Map<string, ActiveSubagentIndexEntry>()
  for (const entry of entries) {
    childRunIndex.set(entry.runId, {
      runId: entry.runId,
      sessionId: entry.sessionId,
      workspaceId: entry.workspaceId
    })
    if (!runIndex.has(entry.parentRunId)) detachedParents.set(entry.parentRunId, entry)
  }

  // A parent that is still running will be restored by adoptActiveRuns once
  // its lazy session store is opened. Only ended parents need a detached
  // attach here so their Task card can be reconstructed immediately.
  for (const [parentRunId, entry] of detachedParents) {
    const store = sessionStore(entry.sessionId)
    if (store.getState().activeRunId !== null) continue
    void restoreDetachedParent(entry.sessionId, parentRunId, entry).then(() => {
      const children = entries.filter((child) => child.parentRunId === parentRunId)
      return Promise.all(children.map((child) => restoreChildSnapshot(child)))
    })
  }

  // Active parents are restored by the normal run path. Wait for that attach
  // before replaying each child's own log, otherwise the parent Task card may
  // not exist yet and the child telemetry would have nowhere to land.
  for (const entry of entries) {
    if (!detachedParents.has(entry.parentRunId)) {
      sessionStore(entry.sessionId)
      void ensureActiveRunRestored(entry.sessionId, entry.parentRunId).then(() => restoreChildSnapshot(entry))
    }
  }
}

/**
 * 用户点子代理卡片的停止时,主进程可能早就没有这个 run 了(它已跑完/被回收),
 * 此时 `agent:abort` 是无声的空操作,而父会话里那张卡片因为漏了结束事件还停在「运行中」。
 * 这里按主进程的实际状态把卡片收口:还在跑就什么都不动(真正的 run_end 会自己到),
 * 已经结束或查不到了就补一条结束。
 */
export async function settleSubagent(childRunId: string): Promise<void> {
  const owner = [...stores.values()].find((store) => Object.values(store.getState().transcript.subagents)
    .some((subagent) => subagent.childRunId === childRunId && subagent.status === 'running'))
  if (owner === undefined) return
  let status: 'done' | 'error' | 'aborted' = 'aborted'
  try {
    const entry = Object.values(owner.getState().transcript.subagents).find((subagent) => subagent.childRunId === childRunId)
    const snap = await attachRun(childRunId, entry?.childSeq ?? 0)
    owner.getState().applyChildEvents(childRunId, snap.events, snap.seq - snap.events.length + 1)
    if (snap.status === 'running') return
    status = snap.status
  } catch {
    // 查不到 = 已被回收,即已结束
  }
  const stuck = Object.values(owner.getState().transcript.subagents)
    .some((subagent) => subagent.childRunId === childRunId && subagent.status === 'running')
  if (stuck) owner.getState().applyChildEvents(childRunId, [{ type: 'run_end', status, at: Date.now() }])
}

async function restoreChildSnapshot(entry: ActiveSubagentIndexEntry): Promise<void> {
  try {
    const store = stores.get(entry.sessionId)
    if (store === undefined) return
    const existing = Object.values(store.getState().transcript.subagents)
      .find((subagent) => subagent.childRunId === entry.runId)
    // Parent telemetry already records the last child event it observed. Ask
    // for only the tail after that cursor to avoid replaying the same tool
    // calls and token usage a second time after a reload.
    const sinceSeq = existing?.childSeq ?? 0
    const snap = await attachRun(entry.runId, sinceSeq)
    const firstSeq = snap.seq - snap.events.length + 1
    store.getState().applyChildEvents(entry.runId, snap.events, firstSeq)
    if (snap.status !== 'running') childRunIndex.delete(entry.runId)
  } catch (err) {
    // The child can finish and be reaped between bootstrap and this attach.
    // The live route remains useful if a final envelope is still delivered.
    console.warn(`[agent] child attach failed: ${entry.runId}`, err)
  }
}

/** 按共同消息定位本地片段,保留页外的早期轮次;不能用时间戳代替数据库顺序。 */
function mergeHistoryMessages(
  history: readonly AgentMessage[],
  local: readonly AgentMessage[],
  localPrecedesHistory = false
): AgentMessage[] {
  const historyIds = new Set(history.map((message) => message.id))
  const localById = new Map(local.map((message) => [message.id, message]))
  const before = new Map<string, AgentMessage[]>()
  let pending: AgentMessage[] = []
  let anchored = false
  for (const message of localById.values()) {
    if (!historyIds.has(message.id)) {
      pending.push(message)
      continue
    }
    anchored = true
    before.set(message.id, pending)
    pending = []
  }
  if (!anchored && localPrecedesHistory) return [...localById.values(), ...history]
  const messages = history.flatMap((message) => [
    ...(before.get(message.id) ?? []), localById.get(message.id) ?? message
  ])
  return [...messages, ...pending]
}

/** 已落盘但不在当前历史窗口内的旧提交,不能被快照重新追加到尾部。 */
function restoredEvents(events: readonly AgentEvent[], state: SessionState, detached = false): AgentEvent[] {
  if (!state.historyLoaded) return [...events]
  const known = new Set(state.transcript.messages.map((message) => message.id))
  let lastKnown = -1
  for (let index = 0; index < events.length; index += 1) {
    const event = events[index]!
    if (event.type === 'message_commit' && known.has(event.message.id)) lastKnown = index
  }
  return events.filter((event, index) => event.type !== 'message_commit'
    || known.has(event.message.id) || (!detached && index > lastKnown))
}

/** Show durable messages immediately; live snapshots may take longer to attach. */
function mergeRestoredHistory(
  state: SessionState,
  page: SessionPage,
  loaded: readonly AgentMessage[] = state.transcript.messages
): Partial<SessionState> {
  const base = mergeHistoryMessages(page.messages, loaded, state.historyLoaded && page.hasMore)
  const messages = mergeHistoryMessages(base, state.transcript.messages)
  const retainedPrefix = state.historyLoaded && messages[0]?.id !== page.messages[0]?.id
  const runUsage = { ...page.runUsage, ...state.transcript.runUsage }
  // The active snapshot rebuilds this run's usage; including its persisted subtotal would count it twice.
  if (state.activeRunId !== null) delete runUsage[state.activeRunId]
  return {
    historyHasMore: page.hasMore && (!retainedPrefix || state.historyHasMore),
    historyLoaded: true,
    historyError: null,
    transcript: {
      ...state.transcript,
      messages,
      tools: toolsFromMessages(messages, state.transcript.tools),
      subagents: subagentsFromMessages(messages, state.transcript.subagents),
      runUsage,
      runModel: { ...page.runModel, ...state.transcript.runModel },
      messageRuns: { ...page.messageRuns, ...state.transcript.messageRuns },
      status: state.activeRunId === null ? 'done' : state.transcript.status
    }
  }
}

async function restoreDetachedParent(
  sessionId: string,
  parentRunId: string,
  owner: ActiveSubagentIndexEntry
): Promise<void> {
  const store = stores.get(sessionId)
  if (store === undefined) return
  store.setState({ historyError: null })
  const loaded = store.getState().transcript
  try {
    const detail = await getSessionPage(sessionId, pageLimitFor(loaded)).catch((err: unknown) => {
      if (stores.get(sessionId) === store) store.setState({ historyError: err instanceof Error ? err.message : String(err) })
      return undefined
    })
    if (stores.get(sessionId) !== store || store.getState().activeRunId !== null) return
    if (detail !== undefined) store.setState((s) => mergeRestoredHistory(s, detail, loaded.messages))
    const snap = await attachRun(parentRunId, 0)
    if (stores.get(sessionId) !== store) return
    store.setState((s) => {
      // A user may have started a new turn while the detached snapshot was in
      // flight. Preserve that live turn rather than replacing it with old data.
      if (s.activeRunId !== null) return s
      const base = {
        ...emptyTranscript(),
        messages: s.transcript.messages,
        tools: s.transcript.tools,
        subagents: s.transcript.subagents,
        ...conversationScoped(s.transcript)
      }
      const events = restoredEvents(snap.events, s, true)
      const transcript = recordMessageRuns(
        archiveRunUsage(applyEvents(base, events), parentRunId, events),
        parentRunId,
        events
      )
      const messages = transcript.messages
      return {
        transcript: {
          ...transcript,
          messages,
          tools: { ...transcript.tools, ...toolsFromMessages(messages, transcript.tools) },
          subagents: subagentsFromMessages(messages, transcript.subagents),
          ...(snap.startedAt === undefined ? {} : { runStartedAt: snap.startedAt }),
          ...(snap.endedAt === undefined ? {} : { runEndedAt: snap.endedAt })
        },
        lastSeq: snap.seq,
        activeRunId: null
      }
    })
    rememberChildRuns(parentRunId, snap.events, {
      runId: parentRunId,
      sessionId: owner.sessionId,
      workspaceId: owner.workspaceId
    })
  } catch (err) {
    // The child may finish and be reaped between bootstrap and this attach.
    // Its persisted parent history remains usable; leave the route in place
    // until the next child envelope tells us it has ended.
    console.warn(`[agent] detached parent attach failed: ${parentRunId}`, err)
  }
}

/**
 * 补齐:拿快照重建,而不是试图往回补那几条。
 *
 * ★ 重放出来的 seq **可能不连续**(主进程在 message_commit 处裁掉了被取代的 delta),
 * 所以这里绝不能对 snapshot.events 再跑一遍 gap 检查 —— 直接应用,
 * 然后把 lastSeq 置成 snapshot.seq。对它再查一次连续性会导致无限 resync。
 */
async function restoreActiveRun(sessionId: string, runId: string): Promise<void> {
  /*
    ★ 调用方一律是 `void ensureActiveRunRestored(...)`:这里漏出去的任何异常都会变成一次
    没人接的 rejection。读页失败就退化成「只按快照重建」,和读不到历史时一样。
  */
  const store = stores.get(sessionId)
  if (store === undefined) return
  const loaded = store.getState().transcript
  store.setState({ historyError: null })
  let detail: Awaited<ReturnType<typeof getSessionPage>> | undefined
  try {
    detail = await getSessionPage(sessionId, pageLimitFor(loaded))
  } catch (err) {
    if (stores.get(sessionId) === store) store.setState({ historyError: err instanceof Error ? err.message : String(err) })
    detail = undefined
  }
  if (stores.get(sessionId) !== store || store.getState().activeRunId !== runId) return
  if (detail !== undefined) store.setState((s) => mergeRestoredHistory(s, detail, loaded.messages))
  await resync(sessionId, runId, 0)
}

const restoreInFlight = new Map<string, Promise<void>>()

/**
 * 这个窗口此刻露不露在外面(主进程 `window:visibility`)。藏着的时候不 attach、
 * 不接正文 —— 主进程那边已经摘掉了订阅,这里也不能自己再订回去。
 * 露出来时由 `startAgentEventPump` 里的那条监听把在看的会话一次性重建。
 */
let windowVisible = true
/** 藏着期间收尾的会话:露出来时要从库里重读一遍(那时没有去 attach) */
const staleWhileHidden = new Set<string>()

function ensureActiveRunRestored(sessionId: string, runId: string): Promise<void> {
  if (!windowVisible) return Promise.resolve()
  const existing = restoreInFlight.get(runId)
  if (existing !== undefined) return existing
  const pending = restoreActiveRun(sessionId, runId).finally(() => {
    if (restoreInFlight.get(runId) === pending) restoreInFlight.delete(runId)
  })
  restoreInFlight.set(runId, pending)
  return pending
}

/**
 * 窗口藏起来 / 露出来。
 *
 * ★ 露出来时**整段重建**而不是从 `lastSeq` 续:藏着的那段时间里主进程的 run 日志可能已经
 * 越过 2000 条的硬上限被截掉头部,续出来的会是一段中间有洞的转录。重建走的是 ⌘R 重载那条路:
 * 库里的历史 + 从 0 开始的快照。
 */
function applyWindowVisibility(visible: boolean): void {
  if (visible === windowVisible) return
  windowVisible = visible
  if (!visible) {
    // 已经在路上的那几批丢掉:露出来时整段重建,不需要它们
    if (raf !== 0) cancelAnimationFrame(raf)
    if (flushTimer !== null) clearTimeout(flushTimer)
    raf = 0
    flushTimer = null
    pending = []
    pendingChars = 0
    return
  }
  for (const [sessionId, store] of stores) {
    const runId = store.getState().activeRunId
    if (runId !== null) void ensureActiveRunRestored(sessionId, runId)
    else if (staleWhileHidden.delete(sessionId)) void hydrateHistory(sessionId, true)
  }
  staleWhileHidden.clear()
  // 父 run 已经结束、还在跑的后台子代理:它们的卡片遥测要单独接回来
  for (const entry of childRunIndex.values()) {
    if (stores.has(entry.sessionId) && !runIndex.has(entry.runId)) {
      void restoreChildSnapshot({ ...entry, parentRunId: '' })
    }
  }
}

async function resync(sessionId: string, runId: string, sinceSeq: number): Promise<void> {
  const store = stores.get(sessionId)
  if (!store) return
  console.warn(`[agent] seq 不连续,attach 补齐 · run=${runId} since=${sinceSeq}`)
  try {
    const snap = await attachRun(runId, sinceSeq)
    const current = store.getState()
    if (current.activeRunId !== runId) return
    // 主进程的日志头部被截过:从某个 seq 续接可能正好续在洞上。改走整段重建(库里的历史 + 从 0 的快照)
    if (sinceSeq !== 0 && snap.logTrimmed === true) {
      await restoreActiveRun(sessionId, runId)
      return
    }
    // An incremental snapshot may overlap events received while attach was in
    // flight. Ask again from the new cursor instead of counting usage twice.
    if (sinceSeq !== 0 && current.lastSeq > sinceSeq && current.lastSeq < snap.seq) {
      await resync(sessionId, runId, current.lastSeq)
      return
    }
    store.setState((s) => {
      if (s.activeRunId !== runId) return s
      const base = sinceSeq === 0
        ? {
            ...emptyTranscript(),
            messages: s.transcript.messages,
            tools: s.transcript.tools,
            subagents: s.transcript.subagents,
            ...conversationScoped(s.transcript),
            ...(s.transcript.runStartedAt === undefined ? {} : { runStartedAt: s.transcript.runStartedAt })
          }
        : s.transcript
      const events = sinceSeq === 0 ? restoredEvents(snap.events, s) : snap.events
      const transcript = recordMessageRuns(
        archiveRunUsage(
          s.lastSeq >= snap.seq ? s.transcript : applyEvents(base, events),
          runId,
          events
        ),
        runId,
        events
      )
      const messages = transcript.messages
      return {
        transcript: {
          ...transcript, messages,
          tools: { ...transcript.tools, ...toolsFromMessages(messages, transcript.tools) },
          subagents: subagentsFromMessages(messages, transcript.subagents),
          ...(transcript.runStartedAt === undefined && snap.startedAt !== undefined
            ? { runStartedAt: snap.startedAt }
            : {}),
          ...(snap.endedAt !== undefined ? { runEndedAt: snap.endedAt } : {})
        },
        lastSeq: Math.max(s.lastSeq, snap.seq),
        activeRunId: snap.status === 'running' ? runId : null
      }
    })
    rememberChildRuns(runId, snap.events)
    if (snap.status !== 'running') unregisterRun(runId)
    // 插话信箱、续跑、送达回执都在主进程;重载之后不需要任何对账
  } catch (err) {
    console.error('[agent] attach 失败:', err)
    /*
      run 已经被主进程回收(结束满 TTL、或超出已结束 run 的缓存预算,见 `reapFinishedRuns`)。
      它的终态在库里:收尾这条会话、从库里重读。不收尾的话它会一直停在「运行中」。
    */
    if (err instanceof Error && /run 不存在/.test(err.message)) {
      let ended = false
      store.setState((s) => {
        if (s.activeRunId !== runId) return s
        ended = true
        return { activeRunId: null }
      })
      if (ended) {
        unregisterRun(runId)
        void hydrateHistory(sessionId, true)
      }
    }
  }
}

/**
 * 全应用**一个** IPC 监听器,按 runId 分发。
 *
 * 每个 store 各自订阅也能跑,但那样每条事件都要过 N 个回调,
 * 且退订责任分散到 N 个地方 —— 而漏退订正是 HMR 下监听器叠加的成因(方案 §3 规则 4)。
 */
let unsubscribe: (() => void) | null = null
let pending: AgentEventEnvelope[] = []
let pendingChars = 0
let raf = 0
let flushTimer: ReturnType<typeof setTimeout> | null = null

/**
 * 事件泵的有界预算。
 *
 * ★★ **为什么必须有界。** 缓冲对齐到的这一帧是这样拿的:`requestAnimationFrame`。
 * 窗口被隐藏(最小化、切到别的 Space、副屏熄了)时浏览器**不再派发 rAF**,于是
 * `drain` 永远等不到;而后台 run 还在往里推。做一个多小时的后台任务下来,这一个
 * 数组能把整个渲染进程撑爆 —— 而且恢复可见那一刻要一次性应用几万条事件,界面直接卡死。
 *
 * 三条路一起收:
 * 1. **条数上限**:一帧内攒够 `PUMP_MAX_ENVELOPES` 就直接派发(不等帧)。
 * 2. **字符上限**:单个 `text_delta` 可能几十 KB,纯按条数算挡不住;按每条事件的
 *    字符串增量累一个缓冲总量,越过 `PUMP_MAX_CHARS` 同样立即派发。
 * 3. **兜底定时器**:即使一帧都没有,超过 `PUMP_FLUSH_MS` 也强制派发一次 ——
 *    这也是「隐藏窗口里消息不至于一个都不落地」的那条保证。
 *
 * `PUMP_FLUSH_MS` 取 50ms(约三帧):比人眼一次「卡顿」的感知还短,却足够让
 * 隐藏窗口里的事件以有限的批大小持续落地,而不是攒到恢复可见时才一起爆。
 *
 * ★ 溢出**不丢信封**:立即派发就是平常那条 `drain()`,它按 runId 找会话、
 * 走 `applyEnvelope`;那里本来就有 seq 连续性检查,断流会去 attach 补齐
 * (见 `applyEnvelope` 的 `hasSeqGap` 分支)。半个信封永远不会被留在数组里 ——
 * 数组里存的都是完整信封,派发也是整条整条地取。
 */
const PUMP_MAX_ENVELOPES = 256
const PUMP_MAX_CHARS = 1_000_000
const PUMP_FLUSH_MS = 50

/** 达到预算即停止估算；提交的整段消息和工具结果也会占内存，不能只数 stream 增量。 */
function envelopeChars(env: AgentEventEnvelope): number {
  const pending: unknown[] = [env.events]
  const seen = new Set<object>()
  let sum = 0
  while (pending.length > 0 && sum < PUMP_MAX_CHARS) {
    const value = pending.pop()
    if (typeof value === 'string') { sum += value.length + 16; continue }
    if (typeof value !== 'object' || value === null) { sum += 16; continue }
    if (seen.has(value)) continue
    seen.add(value)
    if (value instanceof ArrayBuffer) { sum += value.byteLength; continue }
    if (ArrayBuffer.isView(value)) { sum += value.byteLength; continue }
    for (const child of Object.values(value)) {
      if (typeof child === 'string') sum += child.length + 16
      else if (typeof child === 'object' && child !== null) pending.push(child)
      else sum += 16
      if (sum >= PUMP_MAX_CHARS || pending.length >= 1024) return PUMP_MAX_CHARS
    }
  }
  return sum
}

/**
 * 请求下一帧派发,并挂上兜底定时器。
 *
 * 每批最多一帧和一个兜底定时器；提前派发时两者一并撤销，避免隐藏期间积累旧回调。
 */
function schedulePump(): void {
  if (raf === 0) raf = requestAnimationFrame(drain)
  if (flushTimer === null) {
    flushTimer = setTimeout(() => { flushTimer = null; drain() }, PUMP_FLUSH_MS)
  }
}

export function startAgentEventPump(): () => void {
  if (unsubscribe) return unsubscribe

  const off = onAgentEvent((env) => {
    // 藏着的时候还在路上的那几批:露出来时整段重建,这里不收
    if (!windowVisible) return
    pending.push(env)
    pendingChars += envelopeChars(env)
    /*
      ★ 越界立刻派发,而不是等帧 —— 但那还是**平常那条 `drain()`**:
      按 runId 找会话、走 `applyEnvelope` 的 seq 连续性检查,断流照旧 attach 补齐。
      溢出这一路不丢、不拆信封,数组里存的也始终是完整信封。
    */
    if (pending.length >= PUMP_MAX_ENVELOPES || pendingChars >= PUMP_MAX_CHARS
      || env.events.some((event) => event.type === 'run_end' || event.type === 'interaction_request')) {
      drain()
      return
    }
    schedulePump()
  })

  const offGoal = onGoalChanged(applyGoalChange)
  /*
    ★ 运行中角标的收敛靠这条,**和事件泵挂在同一个生命周期里**:
    它和 `agent:event` 描述的是同一件事的两面,分开起的话总有一处会忘了退订,
    而漏退订在 HMR 下就是监听器叠加(方案 §3 规则 4)。
  */
  const offRuns = onActiveRuns(syncActiveRuns)
  // 队列与后台汇报的真源在主进程;这里只把镜像跟上
  const offQueue = onSessionQueueChanged((snapshot) => applyQueueSnapshot(snapshot.sessionId, snapshot))
  const offReport = onSubagentReport(({ sessionId, callId, status }) => applySubagentReport(sessionId, callId, status))
  const offVisibility = onWindowVisibility(applyWindowVisibility)
  unsubscribe = () => {
    off()
    offGoal()
    offRuns()
    offQueue()
    offReport()
    offVisibility()
    if (raf !== 0) cancelAnimationFrame(raf)
    if (flushTimer !== null) clearTimeout(flushTimer)
    raf = 0
    flushTimer = null
    pending = []
    pendingChars = 0
    unsubscribe = null
  }
  return unsubscribe
}

function applySubagentReport(sessionId: string, callId: string, status: SubagentReportStatus): void {
  stores.get(sessionId)?.getState().setSubagentReportStatus(callId, status)
}

/**
 * 目标状态变了。State-only updates must not use message_commit: doing so would erase live text.
 *
 * ★ 空闲时的目标检查**不经过这里**:主进程的会话运行时直接起那一轮
 * (见 `main/goal/runtime.ts` 的 `wakeGoal`),没有窗口在看这条会话时它也得发生。
 * 所以这里不会、也不该再为一次变化去建 store 或者发消息。
 */
export function applyGoalChange(change: GoalChange): void {
  if (deletedHistory.has(change.sessionId)) return
  const target = stores.get(change.sessionId)
  if (target === undefined) return
  target.setState((state) => {
    const message = change.message
    const messages = message === undefined ? state.transcript.messages
      : state.transcript.messages.some((item) => item.id === message.id)
        ? state.transcript.messages.map((item) => item.id === message.id ? mergeGoalStatusMessage(item, message) : item)
        : [...state.transcript.messages, message]
    return { goal: change.goal, goalVersion: state.goalVersion + 1, transcript: { ...state.transcript, messages } }
  })
}

function drain(): void {
  if (raf !== 0) cancelAnimationFrame(raf)
  if (flushTimer !== null) clearTimeout(flushTimer)
  raf = 0
  flushTimer = null
  const batch = pending
  pending = []
  pendingChars = 0
  for (const env of batch) {
    const child = childRunIndex.get(env.runId)
    if (child !== undefined) {
      const firstSeq = env.seq - env.events.length + 1
      // 父会话那张卡片要的是遥测(工具数、阶段、上下文占用),喂的是 applyChildEvents
      stores.get(child.sessionId)?.getState().applyChildEvents(env.runId, env.events, firstSeq)
      /*
        ★ 同一批事件**再喂一份给子会话自己的 store** —— 右侧只读面板逐字流就是这一份,
        走的是和主智能体一模一样的 `applyEnvelope`(所以渲染也一模一样)。
        没人打开过面板时 `childSessionOfRun` 查不到,这里什么都不做。
      */
      const panel = childSessionOfRun.get(env.runId)
      if (panel !== undefined) stores.get(panel)?.getState().applyEnvelope(env)
      if (env.events.some((event) => event.type === 'run_end')) {
        childRunIndex.delete(env.runId)
        childSessionOfRun.delete(env.runId)
      }
      continue
    }
    const sessionId = runIndex.get(env.runId)?.sessionId
    if (sessionId === undefined) continue
    rememberChildRuns(env.runId, env.events)
    stores.get(sessionId)?.getState().applyEnvelope(env)
  }
}
