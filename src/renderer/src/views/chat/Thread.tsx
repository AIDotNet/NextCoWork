/**
 * 对话流。
 *
 * ★ **已提交的 parts 和还在流的 live 块走同一套块渲染器**(`parts.tsx`)。
 * 一个块从「流式中」变成「已提交」的那一瞬间不该跳动 —— 而两套渲染必然会跳。
 *
 * ★ **`isToolResultOnly` 的消息必须过滤掉。** 工具结果在内部格式里是一条
 * `role: 'user'` 的消息(Anthropic 形状,方案 §4.1),不滤掉的话每次工具调用
 * 之后都会多出一个空白的用户气泡 —— 而它长得完全像一个 bug,查起来却要
 * 一路翻到消息模型才明白。
 */
import { memo, useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Check, ChevronDown, Copy, Pencil, Trash2 } from 'lucide-react'
import type { AgentMessage, ContentPart } from '../../../../shared/agent/message'
import { isToolResultOnly } from '../../../../shared/agent/message'
import { formatTokensPerSecond, runDurationOf, tokensPerSecond } from '../../../../shared/agent/duration'
import { formatTokenCount } from '../../../../shared/agent/tokens'
import { formatCostMicros } from '../../../../shared/domain/pricing'
import type { LiveBlock, SubagentState, TranscriptState } from '../../../../shared/agent/transcript'
import { thinkingStatsOfLive, thinkingStatsOfPart } from '../../../../shared/agent/thinking-stats'
import { ProviderIcon } from '../../components/brand/ProviderIcon'
import { AgentMarkdown } from '../../components/markdown'
import { Tooltip } from '../../components/ui/Tooltip'
import { ActionIconButton, useTransientStatus } from '../../components/ui/ActionIconButton'
import { copyText } from '../../services/app'
import { cn } from '../../lib/cn'
import { useI18n } from '../../i18n'
import { agentErrorText } from '../../i18n/agent'
import { MessageImage } from './MessageImage'
import { MentionText } from './MentionText'
import { MessageFileRef } from './MessageFileRef'
import { userMessageFileRefs } from './user-message-attachments'
import { openFileReference } from './file-reference-actions'
import { SubagentNode, SubagentReportRow, ThinkingBlock, ToolCallCard } from './parts'
import { InteractionPanel } from './InteractionPanel'
import type { PlanToolReceipt } from '../../../../shared/domain/plan-file'
import { readWorkspaceFile } from '../../services/workspace-files'
import { StatusLine } from './StatusLine'
import type { CurrentContextLimits } from './context-pressure'
import { ToolTimeline } from './ToolTimeline'
import { RunProcessBlock } from './RunProcessBlock'
import { FOLD_HOLD_MS, useFoldAnchor } from './useFoldAnchor'
import { CompactionDivider } from './CompactionDivider'
import { SkillExtractionBanner } from './SkillExtractionBanner'
import { GoalStatusCard } from './GoalStatusCard'
import type { ActiveGoal } from '../../../../shared/domain/goal'
import { assistantSegments, assistantText, isAssistantTextBlock, isPinnedToolBlock, lastTurnIndex, liveTailRow, promptOf, threadHistoryRows, type AssistantBlock, type ThreadHistory, type ThreadRow } from './thread-content'
import { threadTurnGroups, turnNavigationItems, type ThreadTurnGroup } from './turn-navigation'
import { TranscriptTablesProvider, useTranscriptSubagents, useTranscriptTools } from './thread-tables'
import { TurnNavigationRail } from './TurnNavigationRail'
import { TurnActions, type TurnPrompt } from './TurnActions'
import { TurnChangeReview } from './TurnChangeReview'
import { decideWorkspace } from '../../../../shared/domain/tool-timeline'
import { TodoHistoryProvider } from './todo-history'

export const Thread = memo(function Thread({
  sessionId,
  transcript,
  runId,
  model,
  providerName,
  lastSeq,
  queued,
  compactError,
  goal,
  contextLimits,
  onEditMessage,
  onDeleteReply,
  onDeleteTurn,
  onBranchTurn,
  workspaceId,
  onOpenPlan,
  onExecutePlan,
  skillExtractionSourceId,
  historyHasMore = false,
  loadingEarlier = false,
  onLoadEarlier,
  readOnly = false
}: {
  sessionId?: string
  transcript: TranscriptState
  runId: string | null
  lastSeq: number
  queued: number
  /**
   * 手上这页之前库里还有更早的消息(转录只读最近的一页,见 `HISTORY_PAGE_SIZE`)。
   * 给了 `onLoadEarlier` 才画「加载更早的消息」,滚到顶也会自动取下一页。
   */
  historyHasMore?: boolean
  loadingEarlier?: boolean
  onLoadEarlier?: () => void
  /**
   * **别人的会话,只能看**(右侧的子代理面板)。关掉一切会写回去的东西:
   * 逐轮的重跑/删除/编辑、审批面板、后台任务中心里那颗「处理」。
   *
   * ★ 状态行**留着** —— 它一个 `<button>` 都没有,而「跑了多久、用了多少 token」
   * 正是打开这个面板的人要看的。
   */
  readOnly?: boolean
  /** 手动压缩的失败原因,由状态行显示。它在 store 里而不在 transcript 里。 */
  compactError?: string | null
  goal?: ActiveGoal
  /** 透给状态行:此刻药丸说了算的有效窗口与最大输出。语义见 `StatusLine` 的同名 prop。 */
  contextLimits?: CurrentContextLimits
  onEditMessage?: (id: string, text: string, continueRun: boolean) => Promise<void>
  /**
   * 删除一条助手回复,**引出它的提问留着**。传入的是这一行的消息跨度(`ThreadRow.span`)。
   */
  onDeleteReply?: (fromId: string, toId: string) => Promise<void>
  /** 删除一整轮:传入的是引出该轮的 user 消息 id,提问与它引出的回复一并删掉。 */
  onDeleteTurn?: (userMessageId: string) => Promise<void>
  /** 从这一轮分支出一条新会话。传入的同样是引出该轮的 user 消息 id。 */
  onBranchTurn?: (userMessageId: string) => Promise<void>
  workspaceId?: string
  onOpenPlan?: (path: string) => void
  onExecutePlan?: (ref: { planId: string; path: string }, source: 'current_session' | 'new_session') => void
  /**
   * 提炼会话的源会话 id。给了就在**触发消息下方**渲染说明卡
   * (`SkillExtractionBanner`)—— 用户要知道材料从哪来、会写到哪去,
   * 而触发消息本身只有一句标题。缺省 = 不是提炼会话,什么都不渲染。
   * 判定用「转录第一条消息的 id」定位触发语:它就是首条 user 消息,
   * 用户删掉那一轮时卡片随之消失,是合理的跟随行为。
   */
  skillExtractionSourceId?: string
  /** 助手消息上方那行 `供应商 / 模型`(截图:`RoutinAI / claude-fable-5-1`) */
  providerName: string | undefined
  /**
   * 最新一轮抬头显示的模型名 —— **发送那条消息时用户选中的别名**,由
   * `ChatView` 从 `lastOptions.model` 直接读出,不经过任何反查。只对
   * 最后一行成立;更早的历史轮次从 `transcript.runModel` 按各自的
   * `row.runId` 查(见下面的 `turnModel`)。
   */
  model: string | undefined
}): ReactNode {
  const { t } = useI18n()
  const { messages, live, error, usage, runModel } = transcript
  const running = runId !== null
  const visible = messages.filter((m) => !m.internal && !isToolResultOnly(m))
  const viewport = useRef<HTMLDivElement>(null)
  const content = useRef<HTMLDivElement>(null)
  const followBottom = useRef(true)
  // 手动展开是在读这条提问，不应被内容长高触发的贴底滚动带到消息末尾。
  // ★ 必须是稳定引用:它一路传给每个 ThreadTurn / HistoryRow,每次渲染换一个新函数,
  // 历史回合的 memo 就全部失效,流式期间每帧把整页回合重新协调一遍。
  const pauseFollowing = useCallback((): void => { followBottom.current = false }, [])
  /**
   * 上一次**我们自己**把 scrollTop 写到的位置;`-1` = 没有待确认的程序化滚动。
   * 一次程序化写入最多派发一个 scroll 事件,认领掉就把它清空。
   */
  const selfScrolled = useRef(-1)
  /** 上一次看到的几何量。判断「这一下是谁滚的」靠的是它的**变化方向**,不是离底距离。 */
  const lastSeen = useRef({ top: 0, height: 0 })
  const needsReply = live.length === 0 && visible.at(-1)?.role !== 'assistant'

  /*
    ★ `follow` 只在**挂载时**需要(下面那个 ResizeObserver 的 effect 依赖是 [])。
    提成 useCallback 是为了给那两条 effect 一个明明白白的空依赖表 —— 写成内联函数
    再让依赖表写 [] 会留下一条 eslint 看着像 bug、读代码的人要逐个变量核对的生命周期漏洞。
  */
  const follow = useCallback((): void => {
    const scroller = viewport.current
    if (scroller === null) return
    if (followBottom.current) {
      const bottom = Math.max(0, scroller.scrollHeight - scroller.clientHeight)
      // 已经在底就**不写**。写了也不会派发 scroll 事件,却会留下一个永远等不到
      // 确认的 token —— 而那个 token 会把用户「滚回底部」的那一下吃掉。
      if (scroller.scrollTop !== bottom) {
        scroller.scrollTop = bottom
        selfScrolled.current = scroller.scrollTop
      }
    }
    lastSeen.current = { top: scroller.scrollTop, height: scroller.scrollHeight }
  }, [])

  /*
    需求:往前接一页时,正在看的那段不许跳走。
    ★ 滚动容器是 `overflow-anchor: none`(贴底跟随要自己管),浏览器不会替我们锚定,
    所以记下「离底多远」,新的一页接到上面之后按同样的离底距离放回去。
  */
  const prependAnchor = useRef<number | null>(null)
  const firstMessageId = messages[0]?.id
  useLayoutEffect(() => {
    const scroller = viewport.current
    const distance = prependAnchor.current
    if (scroller === null || distance === null || loadingEarlier) return
    prependAnchor.current = null
    scroller.scrollTop = Math.max(0, scroller.scrollHeight - distance)
    selfScrolled.current = scroller.scrollTop
    lastSeen.current = { top: scroller.scrollTop, height: scroller.scrollHeight }
  }, [firstMessageId, loadingEarlier])
  const loadEarlier = useCallback((): void => {
    if (onLoadEarlier === undefined || !historyHasMore || loadingEarlier) return
    const scroller = viewport.current
    if (scroller !== null) prependAnchor.current = scroller.scrollHeight - scroller.scrollTop
    onLoadEarlier()
  }, [historyHasMore, loadingEarlier, onLoadEarlier])

  // Follow growing replies and newly arriving interactions only while the user
  // is near the bottom. Reading older messages must not trigger a forced jump.
  useEffect(() => {
    const scroller = viewport.current
    const body = content.current
    if (scroller === null || body === null) return
    const observer = new ResizeObserver(follow)
    observer.observe(body)
    observer.observe(scroller)
    follow()
    return () => observer.disconnect()
  }, [follow])

  const feedback = (
    <div className="flex flex-col gap-2.5" data-testid="assistant-feedback">
      {error && !messages.some((m) => m.parts.some((p) => p.type === 'error'
        && p.error.code === error.code && p.error.message === error.message)) && (
        <p role="alert" className="selectable rounded-card border border-danger/40 bg-danger/10 px-3 py-2 font-mono text-[12.5px] text-danger">
          {error.code}: {agentErrorText(error, t)}
        </p>
      )}
      {/* ★ 审批面板在只读态里必须消失:这个 run 的审批归它的**父**对话管,
          而子代理这条路现在根本不会发起审批(见 `runtime.ts` 的 `approveWith`)。 */}
      {!readOnly && (runId !== null || sessionId !== undefined) && <InteractionPanel key={sessionId ?? runId}
        runId={runId ?? undefined} sessionId={sessionId} workspaceId={workspaceId} onOpenPlan={onOpenPlan} onExecute={onExecutePlan} />}
      <StatusLine transcript={transcript} running={running} waitingForResponse={running && needsReply}
        lastSeq={lastSeq} queued={queued} compactError={compactError} goal={goal}
        {...(contextLimits === undefined ? {} : { contextLimits })} />
    </div>
  )

  /*
    ★★ **历史与 live 分开算 —— 这是长会话每 token 不重扫全部历史的关键。**

    `threadHistoryRows` 只读 `messages` / `messageRuns`,所以它被 `useMemo` 锁在
    「历史变了」这一档上;流式期间每来一个 token 变的只有 `live`,历史行数组
    **引用不变**。下游 `threadTurnGroups`、以及那些按行 memo 的 `<HistoryRow>`
    因此一个都不重算 —— 以前是每个 token 重新分组、重算导航预览、逐轮 diff。

    ★ 佣金不能省过头:`live` 一变,`liveTailRow` 仍然要按原来的规则决定「顶上
    末尾那个空回合」还是「另起一行」,提交前后 key 一个字节不变(有用例钉着)。
  */
  const history = useMemo<ThreadHistory>(() => threadHistoryRows(messages, transcript.messageRuns), [messages, transcript.messageRuns])
  const rows = useMemo<ThreadRow[]>(() => live.length === 0 ? history.rows : (() => {
    const tail = liveTailRow(history, live, running)
    return tail.replaced ? [...history.rows.slice(0, -1), tail.row] : [...history.rows, tail.row]
  })(), [history, live, running])
  /*
    需求:提炼说明卡要贴在**触发消息下方**(用户的阅读顺序:一句话 → 这条会话
    在干什么),不是钉在视图顶部。定位用转录第一条消息的 id —— 它就是触发语;
    那一轮被删掉时卡片跟着消失,不额外做防御。
  */
  const extractionTriggerId = skillExtractionSourceId === undefined ? undefined : messages[0]?.id
  const lastTurn = lastTurnIndex(rows)
  /*
    ★★ **只重建最后一个回合。** 历史行的分组是纯函数(`threadTurnGroups`),只依赖
    `history.rows`;流式每来一个 token,唯一会变的只有末尾那一顿(要么顶掉历史
    末尾那个空回合,要么追加一行)。所以历史分组 memo 一次,尾部那一组在这一层
    按同一套规则改写 —— 其余几个回合对象**引用不变**,`ThreadTurn` 的 memo 才不会
    被一个 token 全部打掉。

    对照组是直接 `threadTurnGroups(rows)`:`rows` 每 token 换一次身份,于是一千
    个历史回合的分组、以及每轮的导航预览一起重算。
  */
  const historyTurns = useMemo(() => threadTurnGroups(history.rows), [history])
  const turns = useMemo<ThreadTurnGroup[]>(() => {
    if (live.length === 0) return historyTurns
    const tail = liveTailRow(history, live, running)
    const last = historyTurns.at(-1)
    if (last === undefined) return threadTurnGroups([tail.row])
    const lastRows = tail.replaced
      ? [...last.rows.slice(0, -1), { row: tail.row, index: last.rows.at(-1)?.index ?? 0 }]
      : [...last.rows, { row: tail.row, index: (last.rows.at(-1)?.index ?? -1) + 1 }]
    return [...historyTurns.slice(0, -1), { ...last, rows: lastRows }]
  }, [historyTurns, history, live, running])
  /*
    ★ 导航刻度里每个已有回合的预览是**按组对象缓存**的(见 `turn-navigation.ts`),
    所以这里每 token 只为一个组真正重算。
  */
  const navigationItems = useMemo(() => turnNavigationItems(turns, t('chat.navigation.untitled')), [turns, t])
  return (
    <div className="relative min-h-0 flex-1">
    <div ref={viewport} className="scroll-thin fade-top h-full min-h-0 flex-1 overflow-y-auto [overflow-anchor:none]" data-testid="thread"
      onScroll={() => {
        const el = viewport.current
        if (el === null) return
        const top = el.scrollTop
        const height = el.scrollHeight
        const seen = lastSeen.current
        /*
          ★ **不能拿「离底距离」反推「用户是不是滚走了」。**

          scroll 事件是异步派发的:`follow()` 在 ResizeObserver 回调里写 scrollTop,
          事件要等下一帧的 scroll steps 才送到 —— 而按 HTML「更新渲染」的步骤,
          **scroll steps 排在 ResizeObserver steps 前面**。只要这中间又有东西把内容
          撑高(mermaid 图渲完、图片解码完、代码高亮回来),处理器量到的就是
          「离底 2700px」,读成「用户滚上去了」→ 后面所有 `follow()` 全部空转,
          滚动条永久停在半路。带 mermaid 的会话里内容会断断续续长好几秒、
          派发上百次 scroll 事件,撞中几乎是必然的。

          改成看**变化方向**,它不受内容长高影响:
          - `follow()` 只会把视图往**下**带;
          - 浏览器的夹取只在内容**变矮**时把 scrollTop 往上拽。
          所以「位置往上走了、而且高度没变矮」这件事只有用户做得到。
          反过来要恢复跟随,则要求高度没动 —— 那一下才确定是用户自己滚的。
        */
        if (top === selfScrolled.current) {
          selfScrolled.current = -1
        } else if (top < seen.top && height >= seen.height) {
          followBottom.current = false
        } else if (height === seen.height) {
          followBottom.current = height - top - el.clientHeight < 80
        }
        lastSeen.current = { top, height }
        // 用户自己滚到了顶:接上更早的一页(程序化滚动不触发)
        if (top < 40 && followBottom.current === false) loadEarlier()
      }}>
      <div ref={content} className={cn('mx-auto flex w-full max-w-[760px] flex-col gap-5 px-6 py-6', navigationItems.length > 1 && 'pl-10')}>
        {historyHasMore && onLoadEarlier !== undefined && (
          <button
            type="button"
            onClick={loadEarlier}
            disabled={loadingEarlier}
            data-testid="load-earlier"
            className="self-center rounded-pill px-3 py-1 text-[12px] text-fg-muted transition-colors hover:bg-tint-hover hover:text-fg disabled:opacity-60"
          >
            {loadingEarlier ? t('chat.loadingEarlier') : t('chat.loadEarlier')}
          </button>
        )}
        {/*
          需求:消息里那张 TodoWrite 卡片要标出「这次更新改了什么」,而上一份清单只有
          整条转录能回答。provider 挂在这里(而不是把 `messages` 一路传进卡片)是因为
          它只被一个渲染器用到 —— 见 `todo-history.tsx` 文件头。
        */}
        <TodoHistoryProvider messages={messages}>
        <TranscriptTablesProvider tools={transcript.tools} subagents={transcript.subagents}>
        {turns.map((turn) => {
          /**
           * ★ 判据是「本组**含**末轮那一行」,不是「本组最后一行是末轮」——
           * 手动压缩的线就落在末轮之后,那时本组最后一行是分隔线,而要挂
           * 状态行/反馈的仍是上面那个助手回合(见 `lastTurnIndex` 的注释)。
           */
          const isLast = turn.rows.some((entry) => entry.index === lastTurn)
          /*
            ★★ **只有最后一轮拿到会变的那几样。** 历史回合的 props 在流式期间
            一个都不变,`ThreadTurn` 的 memo 才不会因为某个只在末轮才有意义的
            ReactNode(feedback)在每 token 新建而失效 —— 那正是「一千个历史回合
            跟着一个 token 一起 reconcile」的成因。
          */
          const last = isLast
            ? {
                model,
                status: transcript.status,
                runStartedAt: transcript.runStartedAt,
                runEndedAt: transcript.runEndedAt,
                liveUsage: runId === null ? usage : undefined,
                feedback
              }
            : undefined
          return (
            <ThreadTurn
              key={turn.key}
              group={turn}
              lastTurnIndex={lastTurn}
              running={running}
              readOnly={readOnly}
              {...(workspaceId === undefined ? {} : { workspaceId })}
              {...(sessionId === undefined ? {} : { sessionId })}
              {...(extractionTriggerId === undefined ? {} : { extractionTriggerId })}
              {...(skillExtractionSourceId === undefined ? {} : { skillExtractionSourceId })}
              providerName={providerName}
              runModel={runModel}
              runUsage={transcript.runUsage}
              {...(onEditMessage === undefined ? {} : { onEditMessage })}
              {...(onDeleteReply === undefined ? {} : { onDeleteReply })}
              {...(onDeleteTurn === undefined ? {} : { onDeleteTurn })}
              {...(onBranchTurn === undefined ? {} : { onBranchTurn })}
              {...(onOpenPlan === undefined ? {} : { onOpenPlan })}
              pauseFollowing={pauseFollowing}
              {...(last === undefined ? {} : { last })}
            />
          )
        })}
        </TranscriptTablesProvider>
        </TodoHistoryProvider>
      </div>
    </div>
    <TurnNavigationRail items={navigationItems} viewportRef={viewport} contentRef={content} />
    </div>
  )
})

/**
 * 只属于**末轮**、会随流式变化的那几样。
 *
 * ★ 这个对象只在最后一轮构造。历史回合拿到的 `last` 是 `undefined`,所以
 * 它们那份 props 在流式期间**逐字段引用稳定**,memo 才有意义。
 */
interface LastTurnLive {
  model: string | undefined
  status: TranscriptState['status']
  runStartedAt?: number
  runEndedAt?: number
  liveUsage: TranscriptState['usage']
  feedback: ReactNode
}

/**
 * 一整轮 —— 一个 `<section>` 加上它下面那几行。
 *
 * ★ memo 的价值全看 props 稳不稳,所以这里刻意**不接收 `rows` / `blocks` 数组
 * 以外会每帧变化的东西**:两张会话级表走 context 取(见 `thread-tables.tsx`),
 * 末轮那几样收进一个只在末轮构造的 `last`,回调由上层 `useCallback` 锁住。
 */
const ThreadTurn = memo(function ThreadTurn({
  group,
  lastTurnIndex,
  running,
  readOnly,
  ...rest
}: {
  group: ThreadTurnGroup
  /** 末轮那一行的**全局**下标;末轮不一定是本组最后一行(末尾可能挂着压缩线)。 */
  lastTurnIndex: number
  running: boolean
  readOnly: boolean
  workspaceId?: string
  sessionId?: string
  extractionTriggerId?: string
  skillExtractionSourceId?: string
  providerName: string | undefined
  runModel: TranscriptState['runModel']
  runUsage: TranscriptState['runUsage']
  onEditMessage?: (id: string, text: string, continueRun: boolean) => Promise<void>
  onDeleteReply?: (fromId: string, toId: string) => Promise<void>
  onDeleteTurn?: (userMessageId: string) => Promise<void>
  onBranchTurn?: (userMessageId: string) => Promise<void>
  onOpenPlan?: (path: string) => void
  pauseFollowing: () => void
  last?: LastTurnLive
}): ReactNode {
  const { workspaceId, sessionId, extractionTriggerId, skillExtractionSourceId, providerName, runModel, runUsage, onEditMessage, onDeleteReply, onDeleteTurn, onBranchTurn, onOpenPlan, pauseFollowing, last } = rest
  const subagents = useTranscriptSubagents()
  // 一次算出本组的行序列,免得逐行 `promptOf` 各自重建一份
  const rows = group.rows.map((entry) => entry.row)
  return (
    <section data-turn-navigation-id={group.navigationId} className="flex flex-col gap-5">
      {group.rows.map(({ row, index }, position) => (
        <HistoryRow
          key={row.key}
          row={row}
          prompt={row.kind === 'assistant' ? promptOf(rows, position) : undefined}
          isLast={index === lastTurnIndex}
          running={running}
          readOnly={readOnly}
          {...(workspaceId === undefined ? {} : { workspaceId })}
          {...(sessionId === undefined ? {} : { sessionId })}
          {...(extractionTriggerId === undefined ? {} : { extractionTriggerId })}
          {...(skillExtractionSourceId === undefined ? {} : { skillExtractionSourceId })}
          {...(providerName === undefined ? {} : { providerName })}
          {...(runModel === undefined ? {} : { runModel })}
          {...(runUsage === undefined ? {} : { runUsage })}
          {...(onEditMessage === undefined ? {} : { onEditMessage })}
          {...(onDeleteReply === undefined ? {} : { onDeleteReply })}
          {...(onDeleteTurn === undefined ? {} : { onDeleteTurn })}
          {...(onBranchTurn === undefined ? {} : { onBranchTurn })}
          {...(onOpenPlan === undefined ? {} : { onOpenPlan })}
          pauseFollowing={pauseFollowing}
          {...(index === lastTurnIndex && last !== undefined ? { last } : {})}
          subagentState={row.kind === 'subagent-report' ? subagents[row.callId] : undefined}
        />
      ))}
    </section>
  )
})

/** 一行 —— 四种行各自的分支,和拆分之前逐字一致。 */
const HistoryRow = memo(function HistoryRow({
  row,
  prompt,
  isLast,
  running,
  readOnly,
  workspaceId,
  sessionId,
  extractionTriggerId,
  skillExtractionSourceId,
  providerName,
  runModel,
  runUsage,
  onEditMessage,
  onDeleteReply,
  onDeleteTurn,
  onBranchTurn,
  onOpenPlan,
  pauseFollowing,
  last,
  subagentState
}: {
  row: ThreadRow
  prompt?: TurnPrompt
  isLast: boolean
  running: boolean
  readOnly: boolean
  workspaceId?: string
  sessionId?: string
  extractionTriggerId?: string
  skillExtractionSourceId?: string
  providerName?: string
  runModel?: TranscriptState['runModel']
  runUsage?: TranscriptState['runUsage']
  onEditMessage?: (id: string, text: string, continueRun: boolean) => Promise<void>
  onDeleteReply?: (fromId: string, toId: string) => Promise<void>
  onDeleteTurn?: (userMessageId: string) => Promise<void>
  onBranchTurn?: (userMessageId: string) => Promise<void>
  onOpenPlan?: (path: string) => void
  pauseFollowing: () => void
  last?: LastTurnLive
  subagentState?: SubagentState
}): ReactNode {
  if (row.kind === 'divider') {
    return <CompactionDivider boundary={row.boundary} foldedCount={row.foldedCount} />
  }
  if (row.kind === 'user') {
    const onDelete = readOnly || onDeleteTurn === undefined ? undefined : () => onDeleteTurn(row.message.id)
    // 提炼触发语下面挂说明卡;其余 user 消息照旧。
    if (extractionTriggerId !== undefined && row.message.id === extractionTriggerId) {
      return (
        <div className="flex flex-col gap-2.5">
          <UserBubble message={row.message} workspaceId={workspaceId} onEdit={readOnly ? undefined : onEditMessage} onDelete={onDelete} disabled={running} onExpand={pauseFollowing} />
          {skillExtractionSourceId !== undefined && <SkillExtractionBanner sourceSessionId={skillExtractionSourceId} />}
        </div>
      )
    }
    return <UserBubble message={row.message} workspaceId={workspaceId} onEdit={readOnly ? undefined : onEditMessage} onDelete={onDelete} disabled={running} onExpand={pauseFollowing} />
  }
  if (row.kind === 'plan-receipt') {
    // 工作区未知就没法把计划正文读出来 —— 无正文的卡片只剩一句状态,
    // 不如不占这个位置。
    if (workspaceId === undefined) return null
    return <ResolvedPlanCard workspaceId={workspaceId} receipt={row.receipt} onOpenPlan={onOpenPlan} />
  }
  if (row.kind === 'subagent-report') {
    /*
      ★ 卡片状态按 `callId` 查 —— 汇报行自己只带摘要,而「是哪个子代理、
      能不能点开完整记录」都在 `subagents` 里。查不到(老转录)就退化成
      一行没有子代理名的通用文案,仍然比整条消息不存在强。
    */
    return <SubagentReportRow summary={row.summary} state={subagentState} />
  }
  return (
    <AssistantTurn
      blocks={row.blocks}
      /*
        ★ **按行各取各的,别名版。** 只有最后一行才对得上 `ChatView` 传下来的
        `model`(那是最新一次发送时的选择,存在渲染进程内存里,没跑完也知道 ——
        不必等回包);更早的历史轮次必须查 `transcript.runModel`,否则会像
        修复前那样,所有历史行一起显示"最新一轮"选的模型。
      */
      model={turnModel(row, last?.model, runModel)}
      providerName={providerName}
      runStatus={isLast ? last?.status : undefined}
      /*
        ★ **run 级的起止只属于最后一轮。** `transcript.runStartedAt` 说的是
        「当前(或刚结束的)那一次 run」,历史回合与它无关。以前无条件优先于
        `row.startedAt` 也看不出问题 —— 用时只在 RunProcessBlock 里显示,
        而那个块只对末轮渲染。一旦把用时铺到每一轮,满屏回合就会显示同一个数字。
        更早的回合只能用自己那段「提问 → 收尾发言」的时间差。
      */
      runStartedAt={isLast ? last?.runStartedAt ?? row.startedAt : row.startedAt}
      runEndedAt={isLast ? last?.runEndedAt ?? row.endedAt : row.endedAt}
      feedback={isLast ? last?.feedback : undefined}
      // 助手回合永远紧跟在引出它的提问之后 —— threadRows 会把连续的模型
      // 回复并成一行,所以前一行要么是那条提问,要么(开局补的空回合)什么都没有。
      // 中间可能夹着一条压缩分隔线,`promptOf` 会跳过去。
      prompt={prompt}
      isLast={isLast}
      running={running}
      /*
        ★ **两个来源,按轮次各取各的。** `transcript.usage` 是**当前**这一次
        run 流式累加出来的,只对最后一轮成立 —— 挂到每一轮上就是把同一个
        数字重复报四遍。更早的回合查 `runUsage`:那是从 `usage_records`
        聚合回来的落盘账,每个 run 一份,所以逐轮显示是准的。

        ★ 顺带这也是「重启后用量消失」的修法:内存里那份随进程一起没了,
        查表这条路不受重启影响。老对话(第 12 条迁移之前)的消息没有 run
        归属,`row.runId` 是 undefined,照旧不显示 —— 宁可不显示,
        也不按时间窗去猜它属于哪个 run。
      */
      usage={turnUsage(row, isLast ? last?.liveUsage : undefined, runUsage)}
      onRegenerate={readOnly || onEditMessage === undefined
        ? undefined
        : (id, text) => onEditMessage(id, text, true)}
      onDeleteReply={readOnly ? undefined : onDeleteReply}
      span={row.span}
      onBranchTurn={readOnly ? undefined : onBranchTurn}
      readOnly={readOnly}
      runId={row.runId}
      workspaceId={workspaceId}
      sessionId={sessionId}
    />
  )
})

/**
 * 计划卡片里画的那一截。卡片是 220px 高的裁切盒,看得见的只有开头 —— 把一份几千行的计划
 * 整段渲染成 Markdown 再藏掉,只是白占 DOM;全文由「打开完整计划」去看。
 * 在行边界切,免得切在一个代码块或表格的半截。
 */
const PLAN_EXCERPT_CHARS = 3_000

function planExcerpt(content: string): string {
  if (content.length <= PLAN_EXCERPT_CHARS) return content
  const cut = content.lastIndexOf('\n', PLAN_EXCERPT_CHARS)
  return content.slice(0, cut > 0 ? cut : PLAN_EXCERPT_CHARS)
}

function ResolvedPlanCard({ workspaceId, receipt, onOpenPlan }: {
  workspaceId: string
  receipt: PlanToolReceipt
  onOpenPlan?: (path: string) => void
}): ReactNode {
  const { t } = useI18n()
  const [content, setContent] = useState('')
  useEffect(() => {
    let cancelled = false
    const load = (): void => {
      void readWorkspaceFile(workspaceId, receipt.path).then((file) => {
        if (!cancelled && file.kind === 'text') setContent(file.content)
      }).catch(() => undefined)
    }
    load()
    const changed = (event: Event): void => {
      const detail = (event as CustomEvent<{ workspaceId?: string; path?: string }>).detail
      if (detail?.workspaceId === workspaceId && detail.path === receipt.path) load()
    }
    window.addEventListener('workspace-files-changed', changed)
    return () => { cancelled = true; window.removeEventListener('workspace-files-changed', changed) }
  }, [receipt.path, workspaceId])

  const actionKey = receipt.action === 'approve_current'
    ? 'agent.interaction.planApprovedCurrent'
    : receipt.action === 'approve_new_session'
      ? 'agent.interaction.planApprovedNew'
      : receipt.action === 'request_revision'
        ? 'agent.interaction.planRevisionRequested'
        : 'agent.interaction.planRejected'

  return <section className="rounded-xl border border-border bg-surface p-3" data-testid="plan-file-card">
    <div className="mb-2 flex items-center justify-between gap-3 text-[12px] text-fg-muted">
      <span>{t('agent.interaction.plan')}</span>
      <span>{t(actionKey as 'agent.interaction.planApprovedCurrent')}</span>
    </div>
    <button type="button" onClick={() => onOpenPlan?.(receipt.path)} aria-label={t('agent.interaction.openPlan')}
      className="group relative block h-[220px] w-full overflow-hidden rounded-lg border border-border bg-app p-3 text-left hover:border-accent/50">
      {content === '' ? <span className="text-[12px] text-fg-faint">{receipt.path}</span> : <AgentMarkdown content={planExcerpt(content)} />}
      <span aria-hidden="true" className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-gradient-to-t from-app to-transparent" />
      <span className="absolute right-2 bottom-2 rounded-pill bg-surface-raised px-2 py-1 text-[11px] text-fg-muted shadow-sm group-hover:text-fg">
        {t('agent.interaction.openFullPlan')}
      </span>
    </button>
  </section>
}

/**
 * 这一轮该显示哪份用量。
 *
 * 实时那份优先:当前 run 刚跑完时,它比数据库更全 —— `usage_records` 是按
 * **上游请求**记的,而一轮里最后那次请求的记录与 run 结束几乎同时落盘,
 * 抢在前面读表可能少算一次。历史轮次没有这个问题,直接查表。
 */
function turnUsage(
  row: Extract<ThreadRow, { kind: 'assistant' }>,
  live: TranscriptState['usage'],
  persisted: TranscriptState['runUsage']
): ReactNode {
  const usage = live ?? (row.runId === undefined ? undefined : persisted?.[row.runId])
  return usage === undefined ? undefined : <TaskUsage usage={usage} />
}

/**
 * 这一轮抬头该显示哪个模型别名。
 *
 * 和 `turnUsage` 的口径不同:用量要等 `message_end` 累计,只有 run 彻底
 * 跑完才可信;而"发送时选的模型"在按下发送那一刻就已确定,不必等 run 结束 ——
 * 所以调用方对 `live` 只用 `isLast` 判断,没有 `runId === null` 那道门槛。
 */
function turnModel(
  row: Extract<ThreadRow, { kind: 'assistant' }>,
  live: string | undefined,
  persisted: TranscriptState['runModel']
): string | undefined {
  return live ?? (row.runId === undefined ? undefined : persisted?.[row.runId])
}

function TaskUsage({ usage }: { usage: TranscriptState['usage'] }): ReactNode {
  const { t, locale } = useI18n()
  if (usage === undefined) return null
  const cacheRead = usage.cacheReadInputTokens ?? 0
  const cacheCreate = usage.cacheCreationInputTokens ?? 0
  const inputTotal = usage.inputTokens + cacheRead + cacheCreate
  const cacheRate = inputTotal > 0 ? cacheRead / inputTotal : 0
  /*
    ★ **不用旁边那个「用时」当分母。** 那是整轮墙钟,里面含工具执行和等授权的
    时间;`upstreamMs` 只累加真正在等模型的那几段,算出来的才是模型的输出速度
    (口径见 `tokensPerSecond`)。

    算不出来就整行不画:老对话没有这个数,一次 token 都没产出的轮次也没有。
    显示「平均 TPS 0.0」会被读成「慢得没边」,而事实是「无从谈起」。
  */
  const tps = tokensPerSecond(usage.outputTokens, usage.upstreamMs)
  /*
    ★ 和 TPS 同一个口径:算不出就整行不画,**不显示 0**。这里的「算不出」有两种,
    都由 `addCost` / `runUsageOf` 归成同一个表示:模型不在价目表,或者这一轮里
    故障切换跨了币种(没有汇率源,加起来是个看着合理的错数)。

    `null` 与 `undefined` 在展示层不必区分 —— 分开是累加那一侧的事,
    它要靠这个区别判断该不该把整轮锁死。
  */
  const cost = usage.cost == null ? undefined : formatCostMicros(usage.cost.micros, usage.cost.currency)
  // 压缩之后原始数字在界面上就没有别处可看了,挂到 title 上留一手
  const exact = (n: number): string => n.toLocaleString(locale)
  return (
    /*
      ★ 这里刻意不用 Arc 的 Tooltip:每一回合都有一条用量行,Arc 的提示每个实例都自带
        Radix Provider + Root,200 回合的长历史渲染时间翻了一倍多(thread-isolation 测试
        从 12s 涨到 27s、超时)。这个轻量版只在悬停时才挂浮层。
    */
    <Tooltip
      className="block"
      content={
        <>
          <div className="mb-1 font-medium">{t('chat.taskUsage')}</div>
          <div title={exact(inputTotal)}>
            {t('chat.taskUsageInput', { count: formatTokenCount(inputTotal) })}
          </div>
          <div title={exact(usage.outputTokens)}>
            {t('chat.taskUsageOutput', { count: formatTokenCount(usage.outputTokens) })}
          </div>
          {cacheRead > 0 && (
            <div title={exact(cacheRead)}>
              {t('chat.taskUsageCacheRead', { count: formatTokenCount(cacheRead) })}
            </div>
          )}
          {cacheCreate > 0 && (
            <div title={exact(cacheCreate)}>
              {t('chat.taskUsageCacheCreate', { count: formatTokenCount(cacheCreate) })}
            </div>
          )}
          <div>{t('chat.taskUsageCacheRate', { rate: `${(cacheRate * 100).toFixed(1)}%` })}</div>
          {tps !== undefined && <div>{t('chat.taskUsageTps', { tps: formatTokensPerSecond(tps) })}</div>}
          {cost !== undefined && <div>{t('chat.taskUsageCost', { amount: cost })}</div>}
        </>
      }
    >
      {/* ★ `tabIndex={0}` 不是多余的:这一整条只有文字、没有可聚焦元素,
          不给的话键盘用户走不到它身上,Tooltip 的 focus 触发也就无从发生。 */}
      <div
        tabIndex={0}
        data-testid="task-usage"
        className="w-fit cursor-help rounded-[4px] text-[11px] text-fg-faint outline-none focus-visible:ring-2 focus-visible:ring-accent/50"
      >
        {t('chat.taskUsageSummary', {
          input: formatTokenCount(inputTotal),
          output: formatTokenCount(usage.outputTokens)
        })}
        {cost !== undefined && ` · ${cost}`}
      </div>
    </Tooltip>
  )
}

const USER_MESSAGE_COLLAPSED_HEIGHT = 220
/**
 * 收起时最多画这么多字。220px 的裁切盒里本来就只看得见开头那几行 ——
 * 一段贴进来的几万字日志整段铺进 DOM、再用 max-height 藏掉,只是白占内存。
 * 展开时才画全文。
 */
const USER_MESSAGE_PREVIEW_CHARS = 2_000

/**
 * 用户气泡。
 *
 * ★ **不能只取 text part。** 原实现把 parts 里的文本拼起来、其余一律丢弃,
 * 于是两件事同时发生:发出去的图在自己的气泡里看不见,而**只发图不发文字**的
 * 消息因为 `text === ''` 被整条 return null —— 那条消息从界面上彻底消失,
 * 尽管它已经发给模型了。而「拖张图进来直接问」正是最常见的用法之一。
 *
 * 发送侧 `partsOf` 把文本放在最前；展示时文字仍在上方气泡，图片和
 * 文件引用在气泡下方独立成卡片。这样只发图的消息也不会消失。
 */
function UserBubble({ message, workspaceId, onEdit, onDelete, disabled, onExpand }: {
  message: AgentMessage
  onEdit?: (id: string, text: string, continueRun: boolean) => Promise<void>
  /** 删掉这条提问所在的整轮。缺省(只读面板)就不画删除键。 */
  onDelete?: () => Promise<void>
  disabled: boolean
  onExpand: () => void
  /**
   * 点开正文或下方卡片里的文件引用要用的工作区。**缺省就不画按钮** —— 只读的子代理面板
   * 拿不到它(`ChatView` 的只读分支不传 `workspaceId`),而画一枚点了没反应的
   * chip 比不画更难解释(见 `MessageFileRef`)。
   */
  workspaceId?: string
}): ReactNode {
  const { t } = useI18n()
  const [editing, setEditing] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  // 确认态超时复位:用户点了一下走开,回来时不该还停在「再点就删」上。
  useEffect(() => {
    if (!confirmDelete) return
    const timer = setTimeout(() => setConfirmDelete(false), 4000)
    return () => clearTimeout(timer)
  }, [confirmDelete])
  const [copy, setCopy] = useTransientStatus()
  const [textDraft, setTextDraft] = useState('')
  const text = message.parts
    .filter((p): p is Extract<ContentPart, { type: 'text' }> => p.type === 'text')
    .map((p) => p.text)
    .join('')
    .trim()
  const textId = useId()
  const textRef = useRef<HTMLParagraphElement>(null)
  const [textHeight, setTextHeight] = useState(0)
  const [expanded, setExpanded] = useState(false)
  useLayoutEffect(() => {
    const element = textRef.current
    if (element === null) return
    const measure = (): void => setTextHeight(element.scrollHeight)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [editing, text])
  useLayoutEffect(() => {
    setExpanded(false)
  }, [text])
  const clipped = !expanded && text.length > USER_MESSAGE_PREVIEW_CHARS
  const shownText = clipped ? text.slice(0, USER_MESSAGE_PREVIEW_CHARS) : text
  const canExpand = textHeight > USER_MESSAGE_COLLAPSED_HEIGHT + 1 || text.length > USER_MESSAGE_PREVIEW_CHARS
  const textMaxHeight = expanded && textHeight > 0 ? textHeight : USER_MESSAGE_COLLAPSED_HEIGHT
  const expandMessage = (): void => {
    onExpand()
    setExpanded(true)
  }
  const images = message.parts.filter(
    (p): p is Extract<ContentPart, { type: 'image' }> => p.type === 'image'
  )
  const fileRefs = userMessageFileRefs(text, message.parts)
  /*
    需求：气泡里的文件引用(块状的 `file_ref` 与行内 `@` 引用)点一下就在右侧工作台
    打开它。引用是**发送那一刻的快照**,文件后来被删掉、改名是常态 —— 所以先确认
    打得开再开 Tab,否则右侧会多出一个只显示错误的 Tab(判定见 `file-reference-actions.ts`)。

    ★ 收进一个 `const` 再判空:参数 `workspaceId` 是可变的,TS 不把
    `!== undefined` 的收窄带进箭头函数里 —— 直接写会让下面那行报
    「string | undefined 不能赋给 string」。
  */
  const referenceWorkspace = workspaceId
  const openReference = referenceWorkspace === undefined
    ? undefined
    : (path: string): void => { void openFileReference(referenceWorkspace, path) }

  // 文本、图片、文件引用都没有才是真的空
  if (text === '' && images.length === 0 && fileRefs.length === 0) return null

  if (editing) {
    return (
      <div className="flex justify-end">
        <div className="w-[min(85%,520px)] rounded-[18px] bg-tint px-3.5 py-2.5">
          <textarea
            autoFocus
            value={textDraft}
            onChange={(event) => setTextDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
                event.preventDefault()
                if (!disabled) {
                  void onEdit?.(message.id, textDraft, true)
                  setEditing(false)
                }
              }
            }}
            className="scroll-thin selectable min-h-[72px] w-full resize-y bg-transparent text-[13.5px] leading-relaxed text-fg focus:outline-none"
            aria-label={t('chat.editMessage')}
            data-testid="user-message-editor"
          />
          <div className="mt-2 flex justify-end gap-1.5">
            <button type="button" onClick={() => setEditing(false)} className="rounded-[6px] px-2.5 py-1 text-[12px] text-fg-muted hover:bg-tint-hover">
              {t('common.cancel')}
            </button>
            <button type="button" disabled={disabled} data-testid="user-message-save" onClick={() => { void onEdit?.(message.id, textDraft, false); setEditing(false) }} className="rounded-[6px] px-2.5 py-1 text-[12px] text-fg-muted hover:bg-tint-hover disabled:opacity-40">
              {t('common.save')}
            </button>
            <button type="button" disabled={disabled} data-testid="user-message-save-continue" onClick={() => { void onEdit?.(message.id, textDraft, true); setEditing(false) }} className="rounded-[6px] bg-accent px-2.5 py-1 text-[12px] font-medium text-accent-fg disabled:opacity-40">
              {t('chat.saveAndContinue')}
            </button>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="flex justify-end">
      <div className="group relative flex max-w-[85%] flex-col items-end gap-1.5" data-testid="user-message">
        {text !== '' && (
          <div className="group relative max-w-full rounded-[18px] bg-tint px-3.5 py-2.5" data-testid="user-message-bubble">
            {/*
              ★ 用 `MentionText` 而不是直接铺 `{text}`:输入框里 `@` 选出来的
              文件在草稿里是一段 markdown 链接,它在气泡里也得是同一个 chip ——
              否则发送那一下,用户眼里的 chip 会「变回」一串方括号,
              看起来像是发错了(而其实发出去的一直是同一个字符串)。

              ★ `break-words`:`whitespace-pre-wrap` 只在空白处断行,一段没有空格的
              长串(粘进来的 SQL、URL、base64)会整条冲出气泡右边被切掉。输入框
              (`MentionInput`)本来就带 `break-words`,这里不带的话,同一段文字在
              草稿里好好的、一发出去就断头 —— 又是那种「像是发错了」的错觉。
            */}
            <div className="relative">
              <div
                id={textId}
                data-testid="user-message-text"
                // 只折叠视觉高度，保留文字选择与读屏；键盘进入引用时展开，避免焦点落到裁切区。
                onFocusCapture={() => { if (canExpand && !expanded) expandMessage() }}
                className="overflow-clip transition-[max-height] duration-280 ease-panel motion-reduce:transition-none"
                style={{ maxHeight: `${textMaxHeight}px` }}
              >
                <p ref={textRef} className="selectable text-[13.5px] leading-relaxed break-words whitespace-pre-wrap text-fg">
                  <MentionText text={shownText} onOpen={openReference} />
                </p>
              </div>
              {canExpand && (
                <div
                  data-testid="user-message-fade"
                  aria-hidden="true"
                  className={cn(
                    'pointer-events-none absolute inset-x-0 bottom-0 h-14 bg-gradient-to-t from-tint to-transparent transition-opacity duration-200 ease-panel motion-reduce:transition-none',
                    expanded ? 'opacity-0' : 'opacity-100'
                  )}
                />
              )}
            </div>
            {canExpand && (
              // 需求：展开键是一颗居中的小胶囊而不是整行色条 —— 整行底色悬停时像一块多出来的卡片。
              <button
                type="button"
                data-testid="user-message-expand"
                aria-expanded={expanded}
                aria-controls={textId}
                aria-label={t(expanded ? 'chat.message.collapse' : 'chat.message.expand')}
                onClick={() => { if (expanded) setExpanded(false); else expandMessage() }}
                className="mx-auto mt-1.5 flex cursor-pointer items-center gap-1 rounded-full border border-stroke bg-surface-raised/70 px-2.5 py-0.5 text-[11.5px] text-fg-muted transition-colors hover:bg-tint-hover hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50"
              >
                <span>{t(expanded ? 'chat.message.collapse' : 'chat.message.expand')}</span>
                <ChevronDown
                  size={13}
                  aria-hidden="true"
                  className={cn('transition-transform duration-200 ease-panel motion-reduce:transition-none', expanded && 'rotate-180')}
                />
              </button>
            )}
          </div>
        )}
        {(fileRefs.length > 0 || images.length > 0) && (
          // 需求：附件脱离文字气泡且限制单卡高度，纯图片消息也能独立显示。
          <div className="flex max-w-full flex-wrap justify-end gap-1.5" data-testid="user-message-attachments">
            {fileRefs.map((f) => (
              <MessageFileRef key={f.path} name={f.name} path={f.path} onOpen={openReference} />
            ))}
            {images.map((img, i) => (
              <MessageImage
                key={`${img.dataRef}:${String(i)}`}
                mime={img.mime}
                dataRef={img.dataRef}
                compact
                // ★ 同一条消息里的图是一组 —— 灯箱据此给出翻页
                siblings={images.map((x) => ({ mime: x.mime, dataRef: x.dataRef }))}
                index={i}
                // 灯箱里的「用别的程序打开」需要它;没有工作区(只读面板)就整条不画
                {...(workspaceId === undefined ? {} : { workspaceId })}
              />
            ))}
          </div>
        )}
        {(text !== '' || onEdit !== undefined || onDelete !== undefined) && (
          // 操作条在气泡**下方**、右对齐;平时透明(占位不变,悬停时不会把下面的内容顶开)。
          <div className={cn(
            'flex items-center gap-0.5 text-fg-faint transition-opacity focus-within:opacity-100',
            confirmDelete || copy !== 'idle' ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'
          )} data-testid="user-message-actions">
            {text !== '' && (
              // 复制不改任何状态,只读面板里也给。复制的是原文(含 `@` 引用的 markdown),不是折叠后的预览。
              <ActionIconButton
                label={t(copy === 'failed' ? 'chat.message.copyFailed' : copy === 'done' ? 'chat.message.copied' : 'chat.message.copy')}
                tone={copy === 'failed' ? 'danger' : 'plain'}
                testId="user-message-copy"
                onClick={() => {
                  void copyText(text).then(() => setCopy('done')).catch(() => setCopy('failed'))
                }}
              >
                {copy === 'done' ? <Check size={13} /> : <Copy size={13} />}
              </ActionIconButton>
            )}
            {onEdit !== undefined && (
              <button
                type="button"
                disabled={disabled}
                aria-label={t('chat.editMessage')}
                title={t('chat.editMessage')}
                data-testid="user-message-edit"
                onClick={() => { setTextDraft(text); setEditing(true) }}
                className="rounded-[6px] p-1 hover:bg-tint-hover hover:text-fg-muted disabled:pointer-events-none disabled:opacity-40"
              >
                <Pencil size={13} />
              </button>
            )}
            {onDelete !== undefined && (
              /*
                ★ 删提问 = 删整轮(提问 + 它引出的回复与工具回执),单删提问会留下无头的回复。
                不可撤销,所以和「删除回复」一样走两次点击确认,超时自动复位。
              */
              <button
                type="button"
                disabled={disabled}
                aria-label={confirmDelete ? t('common.confirmDelete') : t('chat.message.delete')}
                title={confirmDelete ? t('common.confirmDelete') : t('chat.message.delete')}
                data-testid="user-message-delete"
                onClick={() => {
                  if (!confirmDelete) { setConfirmDelete(true); return }
                  setConfirmDelete(false)
                  void onDelete()
                }}
                className={cn(
                  'flex items-center gap-1 rounded-[6px] p-1 disabled:pointer-events-none disabled:opacity-40',
                  confirmDelete ? 'bg-danger/10 px-1.5 text-[11.5px] text-danger' : 'hover:bg-tint-hover hover:text-fg-muted'
                )}
              >
                <Trash2 size={13} />
                {confirmDelete && <span>{t('common.confirmDelete')}</span>}
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

/** 助手回合的抬头:`供应商 / 模型`。三样都是已有事件的直接投影,不新增数据。 */
function TurnHeader({
  model,
  providerName
}: {
  model: string | undefined
  providerName: string | undefined
}): ReactNode {
  if (model === undefined) return null
  return (
    <div className="flex items-center gap-1.5 text-[11.5px] text-fg-faint">
      <ProviderIcon name={[model, providerName]} size={13} />
      {providerName !== undefined && (
        <>
          <span>{providerName}</span>
          <span className="text-fg-faint/60">/</span>
        </>
      )}
      <span className="min-w-0 truncate">{model}</span>
    </div>
  )
}

function AssistantTurn({
  blocks,
  model,
  providerName,
  runStatus,
  runStartedAt,
  runEndedAt,
  feedback,
  prompt,
  isLast,
  running,
  usage,
  onRegenerate,
  onDeleteReply,
  span,
  onBranchTurn,
  readOnly,
  runId,
  workspaceId,
  sessionId
}: {
  blocks: readonly AssistantBlock[]
  model: string | undefined
  providerName: string | undefined
  runStatus: TranscriptState['status'] | undefined
  runStartedAt?: number
  runEndedAt?: number
  feedback?: ReactNode
  prompt?: TurnPrompt
  isLast: boolean
  running: boolean
  usage?: ReactNode
  onRegenerate?: (id: string, text: string) => Promise<void>
  onDeleteReply?: (fromId: string, toId: string) => Promise<void>
  /** 这一行已落盘的消息跨度;没有(纯流式 / 开局补的空回合)就没有可删的东西。 */
  span: { from: string; to: string } | undefined
  onBranchTurn?: (userMessageId: string) => Promise<void>
  readOnly: boolean
  /** 这一轮的顶层 runId,用来拉「本轮改动集」；老转录(v12 前)为 undefined。 */
  runId?: string
  workspaceId?: string
  sessionId?: string
}): ReactNode {
  const { t } = useI18n()
  // ★ 两张会话级表从 context 取:prop 下去的话,每个历史回合的 memo 都会被
  //   它们每一次身份变化打掉(见 `thread-tables.tsx`)。
  const tools = useTranscriptTools()
  const subagents = useTranscriptSubagents()
  const [lastKnownStatus, setLastKnownStatus] = useState(runStatus ?? 'done')
  if (runStatus !== undefined && runStatus !== lastKnownStatus) setLastKnownStatus(runStatus)
  // ★ `segments` 只在积木本身或两张表变时重算 —— 这一句才是「每 token 不重扫
  //   历史回合」落到实处的地方:历史回合的 `blocks` 引用是稳定的,所以一个 token
  //   也不会把它们的段重新切一遍。
  const segments = useMemo(() => assistantSegments(blocks, t('chat.tool.name'), subagents), [blocks, subagents, t])
  const lastProcessIndex = segments.reduce((last, segment, index) => segment.kind === 'process' ? index : last, -1)
  const processSegments = lastProcessIndex < 0 ? [] : segments.slice(0, lastProcessIndex + 1)
  const trailingSegments = lastProcessIndex < 0 ? segments : segments.slice(lastProcessIndex + 1)
  const goalSegments = processSegments.filter((segment) => segment.kind === 'block' && segment.block.part?.type === 'goal_status')
  // 需求:生图这类「产物即回答」的卡片在过程折进「用时」后仍要留在外面(同 goal_status 的理由,见 isPinnedToolBlock)
  const pinnedSegments = processSegments.filter((segment) => segment.kind === 'block' && isPinnedToolBlock(segment.block))
  const processItems = processSegments.flatMap((segment) => segment.kind === 'process' ? segment.items : [])
  const hasRunningSubagent = processItems.some((item) => item.kind === 'subagent' && item.state?.status === 'running')
  const hasTrailingText = trailingSegments.some((segment) => segment.kind === 'block' && isAssistantTextBlock(segment.block))
  const outcome = lastKnownStatus === 'done' ? 'ok' : lastKnownStatus
  const decision = !hasRunningSubagent
    ? decideWorkspace({
        outcome,
        itemCount: processItems.length,
        hasTrailingText
      })
    : { collapse: false, defaultOpen: false }
  const calls = processItems.flatMap((item) => {
    if (item.kind !== 'tool' || item.callId === undefined) return []
    const call = tools[item.callId]
    return call === undefined ? [] : [call]
  })
  const durationMs = runDurationOf({ runStartedAt, runEndedAt }, calls)

  /*
    需求:run 正常收束时,整个过程段折进一行「用时」摘要 —— 但**先停
    FOLD_HOLD_MS** 再折。状态一翻到 done 就瞬时换装的话,最后一段正文看起来
    像被吞掉了,用户会以为出了错;400ms 足够让注意力从「还在跑」切到
    「已出结果」,此时收起才会被读成「过程收好了」。

    ★ 只延迟「变收起」:subagent 又开跑要重新展开时必须立刻 —— 慢一拍会把
    正在跑的过程藏在一行摘要底下,那才是真的把信息藏没了。

    ★ 换装本身保持瞬时:两侧是两棵不同的子树,做跨形变高度交接要在这一层
    摆 AnimatePresence 倒腾 key,复杂度不成比例。观感交给两件事:
    `entering` 让新摘要行淡入一帧(.run-fold-enter),`useFoldAnchor` 在同一帧
    起把正文钉回原位 —— 一个负责「折进了这一行」,一个负责「答案没动」。
  */
  const [appliedCollapse, setAppliedCollapse] = useState(decision.collapse)
  const [justFolded, setJustFolded] = useState(false)
  useEffect(() => {
    if (decision.collapse === appliedCollapse) return
    if (!decision.collapse) {
      setAppliedCollapse(false)
      return
    }
    const timer = setTimeout(() => {
      setAppliedCollapse(true)
      setJustFolded(true)
    }, FOLD_HOLD_MS)
    return () => clearTimeout(timer)
  }, [decision.collapse, appliedCollapse])
  useEffect(() => {
    if (!justFolded) return
    // 比 .run-fold-enter 的 180ms 多一圈余量:类摘早了会把还在播的淡入掐掉
    const timer = setTimeout(() => setJustFolded(false), 300)
    return () => clearTimeout(timer)
  }, [justFolded])
  const turnRef = useRef<HTMLDivElement>(null)
  const blockRef = useRef<HTMLDivElement>(null)
  // 折叠点顶端 = 「用时」那一行(见 RunProcessBlock 的 ref 合并),不是 turn 顶端 ——
  // 用户翻回本 turn 顶部读提问时,换装发生在视口下方,那次绝不能动视口。
  useFoldAnchor(turnRef, appliedCollapse, blockRef)

  const body = appliedCollapse ? (
    <>
      <RunProcessBlock items={processItems} tools={tools} subagents={subagents} durationMs={durationMs} defaultOpen={decision.defaultOpen} entering={justFolded} ref={blockRef}>
        {processSegments.map((segment) => {
          if (segment.kind === 'block' && segment.block.part?.type === 'goal_status') return null
          if (segment.kind === 'block' && isPinnedToolBlock(segment.block)) return null
          if (segment.kind === 'block') {
            return <PartBlock key={segment.key} part={segment.block.part} liveBlock={segment.block.liveBlock}
              tools={tools} streaming={segment.block.streaming} cursor={segment.block.cursor}
              {...(workspaceId === undefined ? {} : { workspaceId })} />
          }
          return <ToolTimeline key={segment.key} items={segment.items} tools={tools} subagents={subagents} />
        })}
      </RunProcessBlock>
      {/* 生成的图片是回答本身,不随过程折叠 —— 摆在「用时」那一行下面、结论正文上面 */}
      {pinnedSegments.map((segment) => segment.kind === 'block' ? (
        <PartBlock key={segment.key} part={segment.block.part} liveBlock={segment.block.liveBlock}
          tools={tools} streaming={segment.block.streaming} cursor={segment.block.cursor}
          {...(workspaceId === undefined ? {} : { workspaceId })} />
      ) : null)}
      {/* Goal changes, particularly the direct-set disclosure, must not disappear in a collapsed timeline. */}
      {goalSegments.map((segment) => segment.kind === 'block'
        ? <PartBlock key={segment.key} part={segment.block.part} tools={tools} /> : null)}
      {trailingSegments.map((segment) => segment.kind === 'block' ? (
        <PartBlock key={segment.key} part={segment.block.part} liveBlock={segment.block.liveBlock}
          tools={tools} streaming={segment.block.streaming} cursor={segment.block.cursor}
          {...(workspaceId === undefined ? {} : { workspaceId })} />
      ) : <ToolTimeline key={segment.key} items={segment.items} tools={tools} subagents={subagents} />)}
    </>
  ) : (
    <>
      {segments.map((segment) => {
        if (segment.kind === 'block') {
          return <PartBlock key={segment.key} part={segment.block.part} liveBlock={segment.block.liveBlock}
            tools={tools} streaming={segment.block.streaming} cursor={segment.block.cursor}
            {...(workspaceId === undefined ? {} : { workspaceId })} />
        }
        return <ToolTimeline key={segment.key} items={segment.items} tools={tools} subagents={subagents} />
      })}
    </>
  )

  /*
    ★ 回复被删光后留下的空回合(没有内容、正常收尾、也不在等这一轮的回复)整行不画:
    只剩「模型名」页眉挂在那里,看起来就是删除没删干净。`feedback` 照常交出去 ——
    审批面板和状态行挂在末轮上,不能跟着页眉一起消失。
    停止 / 出错的空回合照旧画,理由同下面操作条那段。
  */
  if (blocks.length === 0 && outcome === 'ok' && !(isLast && running)) return feedback ?? null

  return (
    <div ref={turnRef} className="group/turn flex flex-col gap-2.5" data-testid="assistant-turn">
      <TurnHeader model={model} providerName={providerName} />
      {body}
      {/* 本轮改动审查卡 —— 改过文件才渲染,见 TurnChangeReview 内部。 */}
      <TurnChangeReview runId={runId} workspaceId={workspaceId} sessionId={sessionId} readOnly={readOnly} active={isLast && running} />
      {/*
        操作条要等这一轮跑完再出现。流式过程中「复制」拿到的是半句话,
        「重新生成」更是要先中断当前 run —— 那是另一件事,输入框旁边的停止键管它。
      */}
      {/*
        ★ 只读态整条操作栏不出现 —— 不是「把重跑和删除禁掉」,是**一个按钮都不留**。
        复制和导出本身无害,但参考形态是「标题 + 正文」;留两颗按钮在那儿,
        这个面板就又变回了一个半能用的对话界面。用时与用量在顶上的身份栏里。
      */}
      {/*
        ★ 回复被删光后留下的空末轮(没有任何内容、状态也是正常收尾)不出操作条:
        复制/导出/删除本来就没有对象,分支和重新生成挂在一个空回合下面只会像删除没删干净。
        停止 / 出错的空回合照旧保留重新生成 —— 那时它是唯一的补救入口。
      */}
      {!readOnly && !(isLast && running) && !(blocks.length === 0 && outcome === 'ok') && (
        <TurnActions
          text={assistantText(blocks)}
          prompt={prompt}
          alwaysVisible={isLast}
          disabled={running}
          durationMs={durationMs}
          usage={usage}
          onRegenerate={onRegenerate}
          onDelete={onDeleteReply === undefined || span === undefined ? undefined : () => onDeleteReply(span.from, span.to)}
          onBranch={onBranchTurn}
        />
      )}
      {feedback}
    </div>
  )
}

function PartBlock({
  part,
  liveBlock,
  tools,
  streaming = false,
  cursor = false,
  workspaceId
}: {
  part?: ContentPart
  liveBlock?: LiveBlock
  tools: TranscriptState['tools']
  streaming?: boolean
  cursor?: boolean
  /** 助手消息里的图片也要能在灯箱里交给外部程序打开 —— 只读面板没有它 */
  workspaceId?: string
}): ReactNode {
  const { t } = useI18n()
  if (liveBlock) {
    switch (liveBlock.kind) {
      case 'text':
        return <AgentMarkdown content={liveBlock.text} streaming={cursor} />
      case 'thinking':
        return <ThinkingBlock text={liveBlock.text} streaming={streaming} stats={thinkingStatsOfLive(liveBlock)} />
      case 'tool_use':
        return <ToolCallCard call={liveBlock.callId === undefined ? undefined : tools[liveBlock.callId]}
          name={liveBlock.name ?? t('chat.tool.name')} input={liveBlock.text} />
    }
  }
  if (!part) return null
  switch (part.type) {
    case 'text':
      return part.text.trim() === '' ? null : <AgentMarkdown content={part.text} />
    case 'thinking':
      return <ThinkingBlock text={part.text} streaming={false} stats={thinkingStatsOfPart(part)} />
    case 'tool_call':
      return (
        <ToolCallCard call={tools[part.callId]} name={part.name} input={part.input} />
      )
    // 工具结果折在上面那张卡片里 —— 单独再画一遍就是同一件事显示两次
    case 'tool_result':
      return null
    case 'subagent':
      return <SubagentNode summary={part.summary} />
    case 'image':
      return <MessageImage mime={part.mime} dataRef={part.dataRef} {...(workspaceId === undefined ? {} : { workspaceId })} />
    // 只出现在用户消息里(ChatView 的 partsOf),渲染由 UserBubble 负责
    case 'file_ref':
      return null
    case 'error':
      return (
        /*
          需求:`break-words` 是必须的。上游 400 的原文常常是一整串没有空格的 JSON
          (`context_length: {"error":{…`),等宽字体下它比正文列宽出好几百 px,
          而 `<p>` 自己的边框盒仍然只有列宽 —— 表现为消息本身看着一切正常,
          整个转录区底部却多出一条横向滚动条(实测溢出 348px / 列宽 502px),
          而且在 DevTools 里按 `getBoundingClientRect()` 查谁溢出**一个都查不到**。
        */
        <p className="selectable font-mono text-[12.5px] break-words text-danger">
          {part.error.code}: {agentErrorText(part.error, t)}
        </p>
      )
    // 和 `error` 并排:两者都是**只存在于 UI 那一轨**的标记(编码器一律 return null)
    case 'goal_status':
      return <GoalStatusCard part={part} />
    /*
      压缩边界由 `threadRows` 提成一条 `divider` 行(`CompactionDivider`),
      走不到这里 —— 但这个 switch 是穷尽的,少一个 case 就是 TS7030。
      ★ 真要走到这里也必须是 `null`:同一次压缩在界面上画两遍(一条线 + 一个气泡)
      比不画更难解释。
    */
    case 'compact_boundary':
      return null
  }
}
