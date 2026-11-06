/**
 * 对话视图 —— 一个内层 chat Tab 的全部内容。
 *
 * 它是 per-session store 的**唯一**消费者:流式文本只让这棵子树重渲染,
 * 不会每来一个 token 就把 Tab 栏和侧边栏也刷一遍(方案 §8)。
 *
 * `startAgentEventPump()` **不在这里** —— 它在 App 根部起一次。
 * 放这儿的话五个 chat Tab 就是五个泵,同一批事件被 apply 五次。
 */
import { Upload } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState, type ComponentProps, type ReactNode } from 'react'
import { useShallow } from 'zustand/react/shallow'
import { dayPartOf, type DayPart } from '../../../../shared/domain/greeting'
import { selectModelBinding } from '../../../../shared/domain/model-selection'
import { cacheHitRateOf, hasRun, type RunUsage, type SubagentState } from '../../../../shared/agent/transcript'
import { tokensPerSecond } from '../../../../shared/agent/duration'
import { effectiveContextWindow } from '../../../../shared/agent/context-management'
import type { RunCost } from '../../../../shared/domain/pricing'
import { latestTodosFrom } from '../../../../shared/agent/todo'
import { useI18n, type TranslationKey } from '../../i18n'
import { agentErrorText } from '../../i18n/agent'
import { AgentErrorException } from '../../services/ipc'
import type { ContentPart } from '../../../../shared/agent/message'
import type { Attachment, WorkspaceAttachmentIntent } from '../../../../shared/domain/attachment'
import { isLocalEnvironment } from '../../../../shared/domain/environment'
import { isImageMime, mimeOfExt } from '../../../../shared/domain/attachment'
import type { Workspace } from '../../../../shared/domain/workspace'
import { ulid } from '../../../../shared/util/id'
import { cancelWorkspaceUpload, completeWorkspaceUpload, listSessionAttachments, pickAttachments, prepareWorkspaceUpload, removeAttachment, uploadFile } from '../../services/attachment'
import { connectionErrorKey } from '../../services/connections'
import { Dialog } from '../../components/ui/Dialog'
import { Button } from '../../components/arc/button/button'
import { updateWorkspace } from '../../services/app'
import { sessionStore, resumeQueue, retainSessionView } from '../../stores/session'
import { useVideoJobsStore } from '../../stores/video-jobs'
import { Composer, type ComposerValue, type ConversationUsageSummary, type FallbackModel } from './Composer'
import { type TrayItem } from './AttachmentTray'
import { deferAttachIntent, takeAttachIntents } from './draft-handoff'
import { PendingQueue } from './PendingQueue'
import { type TaskChecklistExecution } from './TaskChecklist'
import { ActivityCapsule } from './ActivityCapsule'
import { Thread } from './Thread'
import { ThreadLoadError, ThreadSkeleton } from './ThreadLoading'
import { SubagentLiveFeed } from './subagent-live'
import { SubagentOpenProvider } from './subagent-open'
import { ToolStopProvider } from './tool-stop'
import { openFileReference } from './file-reference-actions'
import { WorkspaceFileProvider } from './workspace-file'
import { WidgetHostProvider, type WidgetHostContextValue } from './widget-host'
import { backgroundToolCall, stopToolCall } from '../../services/shell'
import { useModelsStore } from '../../stores/models'
import { useTabsStore } from '../../stores/tabs'
import { useWindowStore } from '../../stores/window'
import { WorkspaceMarkdownProvider } from '../../components/markdown'
import { branchSession, createSession, getSessionSummary, setSessionMode, setSessionModel } from '../../services/sessions'
import { clearGoal, getGoal, setGoal } from '../../services/goal'
import { parseGoalCommand } from '../../../../shared/domain/goal'
import { toast } from '../../stores/toast'
import { skillExtractionErrorKey, startSkillExtraction } from '../skills/skill-extraction'
import { resolveMaxOutputTokens, type SendOptions, type SessionMode } from '../../../../shared/agent/run-request'
import { useSuggestTrigger } from './suggest-trigger'
import { mentionedFilesOf } from './ghost'

// 需求:空会话首屏问候按当前语言显示。切点(几点算「晚上」)是 shared 纯函数
// (dayPartOf),句子住 i18n —— 原先句子硬编码在 shared 里,英文界面下也一直是中文。
const GREETING_KEYS: Record<DayPart, TranslationKey> = {
  night: 'chat.greeting.night',
  morning: 'chat.greeting.morning',
  afternoon: 'chat.greeting.afternoon',
  evening: 'chat.greeting.evening'
}

export function ChatView({
  sessionId,
  tabId,
  workspace,
  fallbackModel,
  maxOutputTokens,
  runningOverride,
  readOnly = false,
  subagentOf
}: {
  /** null = 还没有会话的草稿 Tab,见 `shared/domain/tab.ts` 的 `chatKey` */
  sessionId: string | null
  tabId: string
  workspace: Workspace
  fallbackModel: FallbackModel
  /**
   * 设置 › 通用 › Agent 的「最大输出 Token」,只用来算压力条的输出预留。
   *
   * 缺省 = 调用方够不着应用设置(定时任务面板里的那块只读转录),按出厂值算 ——
   * 那条路上压力条只是个参考读数,不值得为它把设置再穿一层。
   */
  maxOutputTokens?: number
  /** Background scheduled runs do not subscribe to the normal renderer run pump. */
  runningOverride?: boolean
  /**
   * **别人的会话,只能看。** 目前唯一的来源是子代理卡片点开的右侧面板。
   *
   * ★ 参考形态是「标题 + 正文,零操作」:不画输入框、不画队列区、不画任务清单、
   * 不画审批面板、不画逐轮的重跑/删除。理由不是审美 —— 往一个子代理的会话里
   * 发消息这条路在主进程侧**根本没有接**,画出来的每一个控件都是一次会失败的承诺。
   */
  readOnly?: boolean
  /** 只读面板靠它接上子 run 的实时流 —— 它不画任何东西,见 `SubagentLiveFeed` */
  subagentOf?: { sessionId: string; callId: string }
}): ReactNode {
  /*
    ★ 草稿期用 tabId 作键 —— 转录 store 与未发出输入的存档都按它索引。
    这个键**只在渲染层有效**,绝不能往 IPC 上送(理由见 `chatKey` 的注释)。
  */
  const storeKey = sessionId ?? tabId
  const remote = !isLocalEnvironment(workspace.environment)
  const useSession = sessionStore(storeKey)
  /*
    这个视图挂着,这条会话的转录就留着;卸载(切走 / 关掉)之后宽限一小段时间再放,
    run 照常在主进程跑 —— 见 `retainSessionView`。
  */
  useEffect(() => retainSessionView(storeKey), [storeKey])
  const { activeRunId, lastSeq, transcript, queuedInputs, compacting, compactError, lastOptions, historyHasMore, historyLoaded, historyError, loadingEarlier } = useSession(useShallow((state) => ({
    activeRunId: state.activeRunId,
    lastSeq: state.lastSeq,
    transcript: state.transcript,
    queuedInputs: state.queuedInputs,
    compacting: state.compacting,
    compactError: state.compactError,
    lastOptions: state.lastOptions,
    historyHasMore: state.historyHasMore,
    historyLoaded: state.historyLoaded,
    historyError: state.historyError,
    loadingEarlier: state.loadingEarlier
  })))
  const {
    stop,
    promoteInput,
    editInput,
    editMessage,
    deleteReply,
    deleteTurn,
    dropInput,
    moveInputToDraft,
    retagQueuedPermission,
    retagQueuedMode,
    compactContext,
    loadEarlier,
    retryHistory
  } = useSession.getState()
  const providerById = useModelsStore((s) => s.providerById)
  const openMarkdownFile = useCallback((path: string) => {
    // 由已装插件决定用谁打开 —— 对话里引用一个 `.excalidraw` 也该落进画布
    useTabsStore.getState().openFile(workspace.id, path)
  }, [workspace.id])
  /*
    需求:工具行里的文件名点一下就打开它。
    ★ 走 `openFileReference` 而不是上面那个直接 `openFile`:工具行指向的是
    **过去某一刻**读过/改过的文件,删掉、改名是常态 —— 不先问一句就开,
    留下的是一个只显示错误的 Tab,而用户会以为是编辑器坏了。
  */
  const openToolFile = useCallback((path: string) => {
    void openFileReference(workspace.id, path)
  }, [workspace.id])

  /**
   * 拿一个**能送进 IPC 的**会话 id —— 草稿在这一刻才铸出它自己的那一个。
   *
   * 附件和发送两条路径都要过它,而且可能先后发生(先贴图再发送),
   * 所以 `bindChatSession` 是幂等的:第二次拿到的还是第一次那个 id。
   * 见 `stores/tabs.ts` 里它的注释 —— 那里写着为什么贴图也必须铸 id。
   *
   * ★ 这里是**整棵树被换掉的唯一入口**:铸出新 id 会改 `tab.ref.sessionId`,
   * `views/registry.tsx` 的 key 跟着变,本组件立刻卸载重挂。凡是要跨过这一下的
   * 东西都得显式交接 —— 附件见 `draft-handoff.ts`,焦点见 `Composer` 的挂载 effect。
   */
  const ensureSessionId = useCallback(
    (): string => useTabsStore.getState().bindChatSession(workspace.id, tabId) ?? storeKey,
    [workspace.id, tabId, storeKey]
  )

  const running = runningOverride ?? activeRunId !== null
  const goal = useSession((state) => state.goal)
  const [goalNotice, setGoalNotice] = useState<string | null>(null)
  useEffect(() => {
    if (sessionId === null) return
    let cancelled = false
    const version = useSession.getState().goalVersion
    void getGoal(sessionId).then((current) => {
      if (!cancelled && useSession.getState().goalVersion === version) useSession.setState({ goal: current })
    }).catch(() => setGoalNotice(t('goal.error.updateFailed')))
    return () => { cancelled = true }
  }, [sessionId, useSession])
  const [sessionMode, setCurrentSessionMode] = useState<SessionMode>(workspace.settings.defaultMode)
  /**
   * **这条会话自己记住的模型。** 工作区默认值只负责新会话的起点 ——
   * 在另一条会话里换模型不该把这条会话也换掉。
   *
   * undefined = 还没读到(草稿,或会话元数据还在路上);空别名 = 这条会话
   * 从来没选过,交回给 `Composer` 的兜底链。
   */
  const [sessionModel, setSessionModelState] = useState<FallbackModel | undefined>(undefined)
  /**
   * 用户在这个 Tab 里已经点过模型了。★ 用来挡住一次真实的竞态:会话元数据是
   * 异步读回来的,它晚于一次**刚发生的**点选到达时会把用户的选择盖回去。
   */
  const modelPicked = useRef(false)
  /**
   * 这条会话的提炼来源(`Session.skillSource?.sessionId`)。
   * undefined = 元数据还在路上;null = 不是提炼会话;string = 提炼会话,值是源会话 id。
   *
   * 需求(两件事共用一次读取):
   * - 是提炼会话就不给 `/skillify` —— 主进程会拒绝「提炼的提炼」,画出来就是一个必然失败的命令;
   *   元数据未知时同样不给(不能在还不知道时猜成普通会话)。
   * - 触发消息下方的说明卡(`SkillExtractionBanner`,经 Thread 渲染)要拿源会话 id 去展示来源,
   *   否则用户只看到一句触发语,不知道材料从哪来、会写到哪去。
   */
  const [skillSourceId, setSkillSourceId] = useState<string | null | undefined>(undefined)
  /*
    ★★ 视频任务要**主动拉一次**:那张卡片靠任务状态显示进度与播放器,而状态
    有两个来源 —— 实时的 `video:jobChanged` 与这里的初次加载。只订阅实时的话,
    ⌘R 重载、换窗口、或者在一个**已经完成的**任务上重新打开这条会话,卡片会
    停在"生成中" —— 因为那条广播早就过去了,而任务其实早就好了。

    ★ 与 `getSession` 那几条并列而不是塞进它们的 then:视频任务与"这条会话用什么
    模式/模型"无关,失败也不该影响那两样。
  */
  useEffect(() => {
    if (sessionId === null) return
    void useVideoJobsStore.getState().load(sessionId)
  }, [sessionId])
  useEffect(() => {
    if (sessionId === null) {
      setCurrentSessionMode(workspace.settings.defaultMode)
      setSkillSourceId(null)
      return
    }
    let cancelled = false
    // 只要元数据:不为读一个模式/模型把整段转录读过来
    void getSessionSummary(sessionId).then((detail) => {
      if (cancelled) return
      setCurrentSessionMode(detail.session.mode)
      setSkillSourceId(detail.session.skillSource?.sessionId ?? null)
      if (modelPicked.current) return
      setSessionModelState({
        model: detail.session.model,
        ...(detail.session.modelProviderId === undefined
          ? {}
          : { modelProviderId: detail.session.modelProviderId })
      })
    }).catch(() => undefined)
    return () => { cancelled = true }
  }, [sessionId, workspace.settings.defaultMode])
  /**
   * 药丸上换了模型:先记在本地(输入框重挂时要靠它),再落到会话上。
   *
   * ★ 草稿(还没有会话 id)只记本地 —— 它发出去的那一刻,主进程会把这次
   * 实际用的模型冻进新会话的元数据(见 `runtime.ts`),不必在这里抢着铸 id。
   */
  const handleModelChange = useCallback((model: string, modelProviderId?: string): void => {
    modelPicked.current = true
    setSessionModelState({ model, ...(modelProviderId === undefined ? {} : { modelProviderId }) })
    if (sessionId !== null) void setSessionModel(sessionId, model, modelProviderId).catch(() => undefined)
  }, [sessionId])
  /*
    ★ 直接按回包带回来的 providerId 查,**不再经过别名表**。
    以前是 `providerOf(transcript.model)`,而 `transcript.model` 是上游回包里的
    **真实模型名**、不是别名 —— 那条查询只在「别名恰好等于上游模型名」时才碰巧对,
    用户一改别名就查不到。现在这行说的是既成事实:这段回复实际由哪家给的,
    故障切换真换了家时它跟着变。
  */
  const provider = transcript.providerId === undefined ? undefined : providerById(transcript.providerId)
  /*
    ★ 抬头显示**发送这条消息时用户选中的那个别名**(`lastOptions.model`),
    不是 `transcript.model`。后者是上游回包里的真实模型名 —— 服务端随时可能
    悄悄换个名字(过期的预览别名、故障切换),用户选的和回包报的对不上时,
    他会以为自己选的模型没生效。这里不做任何反查,拿到什么就是什么:
    发送那一刻药丸上是什么,抬头就说什么。历史轮次的值见 `Thread` 里的
    `turnModel`,按各自的 `runId` 查 `transcript.runModel`。
  */
  const modelName = lastOptions?.model
  /**
   * 编辑消息续跑时用的模型。和 `Composer` 的兜底链同源:
   * 这条会话记住的 → 工作区选过的 → 应用默认。
   *
   * ★ 会话记住的那一档排在最前,理由和药丸一样:在这条会话里编辑一条消息重跑,
   * 用的就该是这条会话的模型,而不是另一条会话刚刚改出来的工作区默认值。
   */
  const editModel: FallbackModel = sessionModel !== undefined && sessionModel.model !== ''
    ? sessionModel
    : workspace.settings.defaultModel !== ''
      ? { model: workspace.settings.defaultModel,
          modelProviderId: workspace.settings.defaultModelProviderId }
      : fallbackModel
  /**
   * 「不经过输入框」那一类路径共用的档位:编辑后重新生成、以及后台子代理
   * 结果的手动回传。它们都没有 Composer 的实时选择可用,只能取工作区默认值
   * (和 `webSearch` 同理)。
   *
   * ★ 抽出来是因为第二个用户 —— 后台汇报 —— 在**重启之后**别无选择:
   * store 里的 `lastOptions` 不落盘,那时是 null。
   */
  const offComposerOptions = useMemo(() => ({
    workspaceId: workspace.id,
    depth: 0 as const,
    mode: sessionMode,
    thinking: workspace.settings.defaultThinking,
    webSearch: workspace.settings.webSearch,
    maxContext: workspace.settings.maxContext === true,
    permissionMode: workspace.settings.permissionMode,
    model: editModel.model,
    modelProviderId: editModel.modelProviderId,
    skillIds: workspace.settings.activeSkillIds,
    skillSelectionMode: workspace.settings.skillSelectionMode
  }), [editModel.model, editModel.modelProviderId, sessionMode, workspace])
  const onEditMessage = useCallback(
    (id: string, text: string, continueRun: boolean) => editMessage(id, text, continueRun, offComposerOptions),
    [editMessage, offComposerOptions]
  )
  /**
   * 状态行的分母 —— **和上下文圆环同一个数**,不是上一轮请求报回来的那个。
   *
   * 需求:圆环的分母是本地实时算的(开「最大上下文」当帧从 272K 变 1M),而状态行
   * 读的是 `transcript.contextUsage.window`,要到下一次发送才跟上。两者在同一屏里
   * 打架:圆环 21%,旁边却写着「接近上限,可 /compact」和「已无可折叠的历史,
   * 请开启摘要压缩或另起会话」—— 后者要求用户做的事,他刚在圆环里做完了。
   *
   * ★ 别名走 `editModel` 这条兜底链:它和药丸**同源**(会话记住的 → 工作区选过的 →
   * 应用默认),另起一条查询的话,两个分母又会在某些路径上对不上。绑定必须再走
   * `selectModelBinding`:裸 `models.find` 会在同名别名跨供应商时拿到与路由器不同的一家。
   * 输出预留读的是**全局设置项**(设置 › 通用 › Agent),和主进程装配那一侧同一个数;
   * 模型目录里声明的输出上限不再参与,只有协议上下文窗口会把它收窄。分母两侧对不上时,
   * 压力条会比真实请求更早(或更晚)告警。查不到别名就不传 —— 那时没有任何本地权威可言,
   * 退回主进程的结论。
   */
  const contextAlias = useModelsStore((s) => selectModelBinding(
    s.models,
    s.providers,
    editModel.model,
    editModel.modelProviderId
  ))
  const contextLimits = useMemo(
    () => contextAlias === undefined ? undefined : {
      window: effectiveContextWindow(contextAlias.contextWindow, workspace.settings.maxContext === true),
      maxOutputTokens: resolveMaxOutputTokens(maxOutputTokens, contextAlias.contextWindow)
    },
    [contextAlias, maxOutputTokens, workspace.settings.maxContext]
  )
  const started = hasRun(transcript, running)
  /*
    输入框「建议下一步」的触发:这个挂载亲眼看着一轮成功跑完(规则与理由见 `suggest-trigger.ts`)。
    ★ 判定放在这里而不是 Composer:队列、目标、模式、提炼会话这些事实都住在 ChatView。
  */
  const suggestTrigger = useSuggestTrigger({
    running,
    runEndedAt: transcript.runEndedAt,
    status: transcript.status,
    queued: queuedInputs.length,
    goalActive: goal !== undefined,
    planMode: sessionMode === 'plan',
    skillSourceId,
    sessionId
  })
  const mentionedFiles = useMemo(() => mentionedFilesOf(transcript.messages), [transcript.messages])
  const conversationUsage = useMemo(
    () => summarizeConversationUsage(transcript.runUsage, transcript.usage),
    [transcript.runUsage, transcript.usage]
  )
  const { t } = useI18n()
  const quickWindow = useWindowStore((s) => s.windowKind === 'quick')
  /**
   * 复制按钮旁边的「分支」—— 只把这一轮为止的转录带进一条新会话,再切过去继续聊。
   * 标题沿用当前会话的标题(而不是转录里的用户提问),这样在侧边栏里还能认出
   * 它是从哪条对话分出来的。
   *
   * ★ 标题从**本 Tab 的标题**取,不再 `getSession`:那条 IPC 回的是整段转录
   *   (外加用量聚合),长会话上光为一个标题就要结构化克隆几 MB,是点「分支」卡顿
   *   的一半来源。chat Tab 的标题就是会话标题 —— 改名/自动命名都经
   *   `sessions:changed` → `syncSessionTitle` 同步过来(`App.tsx`)。
   *   只有 Tab 标题还是空串时(打开时没带标题)才退回去问主进程。
   * 失败不在这里接:忙碌态和 toast 都归 `TurnActions`,这里只管往上抛。
   */
  const onBranchTurn = useCallback(async (userMessageId: string) => {
    if (sessionId === null) return
    const tabTitle = useTabsStore.getState().stateOf(workspace.id).tabs.find((tab) => tab.id === tabId)?.title.trim() ?? ''
    const sourceTitle = tabTitle !== '' ? tabTitle : (await getSessionSummary(sessionId)).session.title
    const branched = await branchSession(sessionId, userMessageId, t('session.branchTitle', { title: sourceTitle }))
    useTabsStore.getState().openSession(workspace.id, branched.id, branched.title)
  }, [sessionId, tabId, workspace.id, t])
  /**
   * 卡片点一下 → 右侧工作区开一个只读会话。
   *
   * ★ 标题用子代理的**任务描述**,而不是「子代理」这种类别名 —— 参考图里那个
   * Tab 叫「分析调度器架构」。同时派出去三个子代理时,三个都叫「子代理」的话,
   * Tab 栏上认不出哪个是哪个。
   *
   * ★ `subagentOf` 记的是**父会话 + callId**,跟着 Tab 一起落盘:重载之后
   * 身份栏还能从父转录里把那些格子读回来(渲染层的索引表这时候是空的)。
   */
  const openSubagent = useCallback((state: SubagentState) => {
    if (state.childSessionId === undefined || sessionId === null) return
    useTabsStore.getState().openSubagentSession(
      workspace.id,
      state.childSessionId,
      state.description ?? state.summary ?? t('chat.subagent.default'),
      { sessionId, callId: state.callId }
    )
  }, [workspace.id, sessionId, t])
  /*
    需求:掐掉正在跑的**那一条**命令(见 `shell:stopToolCall` 的契约),而不是
    停掉整轮 —— 后者是 Composer 上那颗停止按钮的职责。
    ★ 没有 run 在跑时给 `undefined`,卡片据此根本不画按钮(见 `tool-stop.tsx`)。
    ★ 失败静默:唯一的失败是「这条命令刚好在点击的同一刻结束」,而那时用户
      想要的结果已经达成,弹一条错误只会让人以为出了别的事。
  */
  /*
    需求:对话里的 widget 能把文字填进输入框、给下一条消息附上它的状态、打开工作区文件。
    ★ 草稿 Tab 还没有会话 id 时不给 —— 那时对话里也不可能有 widget(它是一次 run 的产物)。
  */
  const widgetHost = useMemo<WidgetHostContextValue | undefined>(() => {
    if (sessionId === null || sessionId === undefined) return undefined
    const target = sessionStore(sessionId)
    return {
      sessionId,
      workspaceRoot: workspace.rootPath,
      draft: (text) => { target.getState().appendDraft(text) },
      setContext: (key, title, text) => { target.getState().setWidgetContext(key, title, text) },
      openFile: openToolFile
    }
  }, [sessionId, workspace.rootPath, openToolFile])

  const stopRunningToolCall = useMemo(
    () => activeRunId === null
      ? undefined
      : (callId: string): void => { void stopToolCall(activeRunId, callId).catch(() => {}) },
    [activeRunId]
  )
  /*
    需求:一条跑得比预想久的前台命令,让它接着跑、但模型别再等它(转成后台 shell)。
    ★ 失败静默,理由同上:false 只意味着它刚好跑完了 / 后台名额已满,此刻弹错误只会误导。
  */
  const backgroundRunningToolCall = useMemo(
    () => activeRunId === null
      ? undefined
      : (callId: string): void => { void backgroundToolCall(activeRunId, callId).catch(() => {}) },
    [activeRunId]
  )
  const subagentList = useMemo(() => Object.values(transcript.subagents), [transcript.subagents])
  const todoToolName = transcript.messages.flatMap((m) => m.parts).find((p): p is Extract<ContentPart, { type: 'tool_call' }> => p.type === 'tool_call' && p.name.includes('TodoWrite'))?.name
  const todos = todoToolName === undefined ? undefined : latestTodosFrom(transcript.messages, todoToolName)
  /*
    清单此刻算不算「还在跑」。

    需求:转录里的 `in_progress` 既可能正被这一轮推着走,也可能是上一轮留下的死账,
    而两者长得一模一样。唯一分得清的证据是**这一轮自己写成功过清单** ——
    判据与 `latestTodosFrom` 同源(配对结果存在且不是错误),只是把消息范围收窄到
    当前 run:`messageRuns` 是「消息 → 产出它的 run」的表(`stores/session.ts` 的
    `recordMessageRuns`),本轮写下的 `tool_call` 与它的 `tool_result` 都在里面,
    所以按 `activeRunId` 过滤之后那一对仍然配对得上。

    ★ `messages` / `messageRuns` 在消息提交时才换引用,纯流式 token 保留它们。
      不依赖整份 `transcript`,才能避免每个 token 都为运行归属重扫历史。

    需求:收尾后也要核对清单归属,不能把上一轮的遗留项说成刚结束的新问题没做完。
    只有本窗口收到过 run_end 才判 `stopped`;重载历史没有这份实时证据,按快照展示。
    ★ 首次更新与长工具同批时,配对结果尚未提交也只能显示快照,不能提前采纳可能失败的清单。
  */
  const execution = useMemo<TaskChecklistExecution>(() => {
    const lastMessage = transcript.messages.at(-1)
    const scopeRunId = activeRunId ?? (transcript.runEndedAt !== undefined && lastMessage !== undefined
      ? transcript.messageRuns?.[lastMessage.id]
      : undefined)
    if (scopeRunId === undefined || todoToolName === undefined) return 'snapshot'
    const runMessages = transcript.messages.filter((m) => transcript.messageRuns?.[m.id] === scopeRunId)
    if (latestTodosFrom(runMessages, todoToolName) === undefined) return 'snapshot'
    return running === true ? 'running' : 'stopped'
  }, [activeRunId, running, todoToolName, transcript.messages, transcript.messageRuns, transcript.runEndedAt])

  const executePlan = useCallback((ref: { planId: string; path: string }, source: 'current_session' | 'new_session'): void => {
    const options = {
      workspaceId: workspace.id,
      depth: 0 as const,
      mode: 'code' as const,
      thinking: workspace.settings.defaultThinking,
      webSearch: workspace.settings.webSearch,
      maxContext: workspace.settings.maxContext === true,
      permissionMode: workspace.settings.permissionMode,
      model: workspace.settings.defaultModel !== '' ? workspace.settings.defaultModel : fallbackModel.model,
      modelProviderId: workspace.settings.defaultModelProviderId ?? fallbackModel.modelProviderId,
      skillIds: workspace.settings.activeSkillIds,
      skillSelectionMode: workspace.settings.skillSelectionMode,
      planExecution: ref
    }
    const start = (targetSessionId: string): void => {
      void setSessionMode(targetSessionId, 'code')
      void sessionStore(targetSessionId).getState().send(t('agent.plan.executePrompt'), options)
    }
    if (source === 'current_session') {
      retagQueuedMode('code')
      setCurrentSessionMode('code')
      start(ensureSessionId())
      return
    }
    void createSession(workspace.id, t('composer.planExecutionTitle'), undefined, 'code').then((session) => {
      useTabsStore.getState().openSession(workspace.id, session.id, session.title)
      start(session.id)
    }).catch(() => undefined)
  }, [ensureSessionId, fallbackModel.model, fallbackModel.modelProviderId, retagQueuedMode, t, workspace])

  useEffect(() => {
    void useModelsStore.getState().load()
  }, [])

  /*
    ★ **草稿附件住在这里,与 draft 同级** —— 因为发送时要把它们转成
    `ContentPart[]`,而「什么进 RunRequest」是这一层的职责,不是输入框的。

    托盘项的身份是本地 mint 的 `key` 而不是附件 id:上传是异步的,
    chip 必须在拿到 `Attachment` **之前**就存在,否则拖进 5 个文件时
    界面会空着直到第一个传完。
  */
  const [tray, setTray] = useState<TrayItem[]>([])
  const [uploadIntent, setUploadIntent] = useState<{ key: string; intent: WorkspaceAttachmentIntent } | null>(null)
  const [transferring, setTransferring] = useState(false)
  const uploadTicket = useRef<string | null>(null)
  const uploadVersion = useRef(0)
  useEffect(() => () => {
    uploadVersion.current++
    if (uploadTicket.current) void cancelWorkspaceUpload(uploadTicket.current).catch(() => {})
    uploadTicket.current = null
  }, [workspace.id, storeKey])
  // 重试要用原 File,而 TrayItem 是可序列化的展示态,不放 File
  const pendingFiles = useRef(new Map<string, File>())

  /*
    切会话时清空:附件是**这个会话**的草稿,跟着 draft 走。

    ★ **草稿铸出自己的 id 不算切会话。** 那一下 `storeKey` 从 tabId 变成
    sessionId(见 `views/registry.tsx` 里 key 的注释),但它仍是同一段对话 ——
    照清不误的话,触发绑定的那张图刚进托盘就被自己抹掉,表现正是「第一次
    粘贴没反应」。草稿文本在 `adoptDraftSession` 那边同理是搬而不是丢。
  */
  const previousStoreKey = useRef(storeKey)
  useEffect(() => {
    const adopted = previousStoreKey.current === tabId
    previousStoreKey.current = storeKey
    if (adopted) return
    setTray([])
    pendingFiles.current.clear()
  }, [storeKey, tabId])

  /*
    重启后恢复草稿附件区 —— 与 draft / 队列的 hydrate 同级。

    ★ **回填必须带守卫**:IPC 往返期间用户可能已经拖了新文件进来。
    无条件 setTray 会用旧快照盖掉刚拖进来的那些 —— 与 session store 里
    `hydrateInput` 那条守卫是同一个理由,也是持久化最常见的翻车方式。

    ★ 已知代价:`displayName` 没有单独落列,重启后退化成 ULID 文件名
    (`01J8X.png`)。修它要给 attachments 表再加一列,而这个退化只影响
    「传了图 → 关掉应用 → 重开」这一条路径上的 chip 文案。

    ★ 非图片的路径型托盘项**不在这份存档里**——它们从没上传过,`listSessionAttachments`
    自然查不到。重启后这些 chip 会丢,代价与上面那条同级:只影响「拖了非图片文件 →
    关掉应用 → 重开」这一条冷门路径。
  */
  useEffect(() => {
    // 草稿还没铸出 id 就不可能有存档 —— 拿 tabId 去问只会得到「会话不存在」
    if (sessionId === null) return
    let cancelled = false
    void listSessionAttachments(sessionId)
      .then((list) => {
        if (cancelled || list.length === 0) return
        setTray((t) =>
          t.length > 0
            ? t
            : list.map((a) => ({
                key: a.id,
                name: a.displayName,
                status: remote && !isImageMime(a.mime) ? 'awaiting-upload' as const : 'done' as const,
                attachment: a
              }))
        )
      })
      .catch((err: unknown) => {
        // 恢复失败不该拦住会话可用 —— 最坏结果是少一批草稿附件
        console.error('[attachment] 恢复草稿附件失败:', err)
      })
    return () => {
      cancelled = true
    }
  }, [sessionId, remote])

  const startUpload = useCallback(
    (key: string, file: File) => {
      setTray((t) =>
        t.map((x) => (x.key === key ? { ...x, status: 'uploading', error: undefined } : x))
      )
      void uploadFile(file, 'session', ensureSessionId())
        .then((a) => {
          setTray((t) => t.map((x) => (x.key === key ? { ...x, status: remote && !isImageMime(a.mime) ? 'awaiting-upload' : 'done', attachment: a } : x)))
          pendingFiles.current.delete(key)
        })
        .catch((error: unknown) => {
          // ★ 失败的 chip **保留**并可重试,并显示上传端给出的具体原因。
          const message = error instanceof AgentErrorException && error.error.messageKey !== undefined
            ? agentErrorText(error.error, t) : t('attachment.error.uploadFailed')
          setTray((current) =>
            current.map((x) => (x.key === key ? { ...x, status: 'error', error: message } : x))
          )
        })
    },
    [ensureSessionId, remote, t]
  )

  /*
    ★ 图片走原有的上传流程(要内联展示,协议直供);非图片**不落盘**——
    上传一份没人会去读的字节副本没有意义。这个应用本身就是个带文件系统工具的
    Agent,给它真实路径,它自己会用 Read 之类的工具去看,比塞一份复制品有用得多。
  */
  const attachFiles = useCallback(
    (files: File[]) => {
      const isImageFile = (f: File): boolean => isImageMime(f.type) || isImageMime(mimeOfExt(f.name))
      /*
        ★ 要上传就得先有会话 id,而草稿在这一刻铸 id 会让本组件立刻重挂 ——
        托盘和上传承诺都会随之作废。所以铸出来的是**新** id 时,这一批原样交给
        重挂后的实例,这里什么都不做(理由与交接办法见 `draft-attachments.ts`)。

        不需要上传的那些(本地环境下的非图片,只取路径)照旧不碰 id:
        免得拖一个 .pdf 进来就把一张白纸变成「用过的」会话。
      */
      if (files.some((f) => remote || isImageFile(f))) {
        const bound = ensureSessionId()
        if (bound !== storeKey) {
          deferAttachIntent(bound, { kind: 'files', files })
          return
        }
      }
      const items = files.map((f) => ({
        key: ulid(),
        name: f.name,
        file: f,
        isImage: isImageFile(f)
      }))
      setTray((t) => [
        ...t,
        ...items.map(({ key, name, isImage, file }) =>
          isImage || remote
            ? { key, name, status: 'uploading' as const }
            : pathTrayItem(key, name, file)
        )
      ])
      for (const { key, file, isImage } of items) {
        if (!isImage && !remote) continue
        pendingFiles.current.set(key, file)
        startUpload(key, file)
      }
    },
    [ensureSessionId, startUpload, remote, storeKey]
  )

  /**
   * 非图片文件的托盘项:同步取真实路径,不经过 IPC 上传。
   * ★ 拿不到路径(比如某些合成的剪贴板文件)时直接标错误,不放进 pendingFiles ——
   * 这不是网络失败,重试也不会有不同结果。
   */
  function pathTrayItem(key: string, name: string, file: File): TrayItem {
    const path = window.nextcowork.getPathForFile(file)
    return path === ''
      ? { key, name, status: 'error', canRetry: false, error: t('chat.pathUnavailable') }
      : { key, name, status: 'done', path, source: { kind: 'local' } }
  }

  /**
   * 菜单里的「添加附件」。★ **落进托盘的东西与拖拽/粘贴完全同形** ——
   * 图片是落好盘的 `attachment`,非图片是一条 `path`。分流本身在主进程做
   * (只有它拿得到 dialog 选中的真实路径),这里只是把两种形态摊成 chip。
   */
  const pickAttachment = useCallback(() => {
    // ★ 同 `attachFiles`:草稿在这里铸 id 会把本组件换掉,而系统对话框的结果要到
    //   几秒之后才回来 —— 那时这棵树已经没了(旧写法的症状:新对话里第一次
    //   「添加附件」选完文件,托盘里什么也不出现)。先把「开这个对话框」本身
    //   交给重挂后的实例,由它去开。
    const bound = ensureSessionId()
    if (bound !== storeKey) {
      deferAttachIntent(bound, { kind: 'pick' })
      return
    }
    // 走主进程 dialog —— 渲染层不指定路径,路径是用户在系统对话框里选定的
    void pickAttachments('session', bound, remote).then((list) => {
      setTray((current) => [
        ...current,
        ...list.map((p): TrayItem => {
          if (p.kind === 'error') return { key: ulid(), name: p.name, status: 'error', canRetry: false, error: agentErrorText(p.error, t) }
          if (p.kind === 'path') return remote
            ? { key: ulid(), name: p.name, status: 'error', canRetry: false, error: t('ssh.attachmentFailed') }
            : { key: ulid(), name: p.name, status: 'done', path: p.path, source: { kind: 'local' } }
          return {
            key: p.attachment.id,
            name: p.attachment.displayName,
            status: remote && !isImageMime(p.attachment.mime) ? 'awaiting-upload' : 'done',
            attachment: p.attachment
          }
        })
      ])
    }).catch((error: unknown) => {
      const message = error instanceof AgentErrorException && error.error.messageKey !== undefined
        ? agentErrorText(error.error, t) : t('attachment.error.uploadFailed')
      setTray((current) => [...current, { key: ulid(), name: t('composer.addAttachment'), status: 'error', canRetry: false, error: message }])
    })
  }, [ensureSessionId, remote, t, storeKey])

  /*
    ★ 接手草稿期最后那一次附件动作 —— 这个实例正是被那次「铸 id」换上来的。
    不接的话,新对话里第一次贴图 / 拖拽 / 添加附件会石沉大海,第二次才正常
    (见 `draft-handoff.ts`)。放在清空托盘那个 effect **之后**:
    它按 `storeKey` 清,而这一批本来就属于新的 key。
  */
  useEffect(() => {
    for (const intent of takeAttachIntents(storeKey)) {
      if (intent.kind === 'pick') pickAttachment()
      else attachFiles(intent.files)
    }
  }, [storeKey, attachFiles, pickAttachment])

  const removeFromTray = useCallback((key: string) => {
    setTray((t) => {
      const target = t.find((x) => x.key === key)
      // 已落盘的要连磁盘一起删 —— 否则它变成一个永远不会被引用的草稿
      if (target?.attachment !== undefined) void removeAttachment(target.attachment.id)
      return t.filter((x) => x.key !== key)
    })
    pendingFiles.current.delete(key)
  }, [])

  const cancelTransfer = (): void => {
    uploadVersion.current++
    if (uploadTicket.current) void cancelWorkspaceUpload(uploadTicket.current).catch(() => {})
    uploadTicket.current = null
    if (uploadIntent) setTray((items) => items.map((item) => item.key === uploadIntent.key ? { ...item, status: 'awaiting-upload' } : item))
    setUploadIntent(null)
    setTransferring(false)
  }

  const retryUpload = (key: string): void => {
    const item = tray.find((entry) => entry.key === key)
    if (remote && item?.attachment && !isImageMime(item.attachment.mime)) {
      if (transferring) return
      const version = ++uploadVersion.current
      if (uploadTicket.current) void cancelWorkspaceUpload(uploadTicket.current).catch(() => {})
      setTransferring(true)
      void prepareWorkspaceUpload(item.attachment.id, ensureSessionId(), workspace.id).then((intent) => {
        if (version !== uploadVersion.current) { void cancelWorkspaceUpload(intent.ticket).catch(() => {}); return }
        uploadTicket.current = intent.ticket
        setUploadIntent({ key, intent })
      }).catch((error: unknown) => {
        if (version === uploadVersion.current) setTray((items) => items.map((entry) => entry.key === key ? { ...entry, status: 'error', error: t(connectionErrorKey(error)) } : entry))
      }).finally(() => { if (version === uploadVersion.current) setTransferring(false) })
      return
    }
    const file = pendingFiles.current.get(key)
    if (file !== undefined) startUpload(key, file)
  }

  const confirmTransfer = (): void => {
    if (!uploadIntent || transferring) return
    const { key, intent } = uploadIntent
    const version = uploadVersion.current
    setTransferring(true)
    setTray((items) => items.map((item) => item.key === key ? { ...item, status: 'uploading' } : item))
    void completeWorkspaceUpload(intent.ticket).then((reference) => {
      if (version !== uploadVersion.current) return
      setTray((items) => items.map((item) => item.key === key ? { ...item, ...reference, status: 'done', attachment: undefined } : item))
      void removeAttachment(intent.attachmentId).catch(() => {})
    }).catch((error: unknown) => {
      if (version === uploadVersion.current) setTray((items) => items.map((item) => item.key === key ? { ...item, status: 'error', error: t(connectionErrorKey(error)) } : item))
    }).finally(() => {
      if (version === uploadVersion.current) { uploadTicket.current = null; setUploadIntent(null); setTransferring(false) }
    })
  }

  const transferDialog = <Dialog open={uploadIntent !== null} title={t('ssh.uploadToServer')} onClose={() => { if (!transferring) cancelTransfer() }} footer={<>
    <Button type="button" variant="secondary" disabled={transferring} onClick={cancelTransfer}>{t('common.cancel')}</Button>
    <Button type="button" variant="secondary" disabled={transferring} onClick={confirmTransfer}><Upload size={14} />{t(transferring ? 'chat.uploading' : 'ssh.uploadToServer')}</Button>
  </>}>
    {uploadIntent && <dl className="space-y-3 break-words text-[13px]">
      <div><dt className="text-fg-muted">{t('ssh.uploadSource')}</dt><dd>{uploadIntent.intent.name}</dd></div>
      <div><dt className="text-fg-muted">{t('ssh.uploadDestination')}</dt><dd>{uploadIntent.intent.connectionName}</dd><dd className="font-mono text-[12px]">{uploadIntent.intent.directory}</dd></div>
    </dl>}
  </Dialog>

  /**
   * 药丸此刻的值 → 一份 `SendOptions`。
   *
   * ★ 抽出来是因为现在有**两个**发送入口（用户按回车、`/goal` 注入 kickoff），
   *   而它们必须用同一份档位：两处各拼一遍的话，kickoff 会以一个用户没选过的
   *   模型/权限档位跑起来，而他完全看不出为什么。
   */
  function sendOptionsOf(v: ComposerValue): SendOptions {
    return {
      workspaceId: workspace.id,
      depth: 0,
      mode: v.mode,
      thinking: v.thinking,
      webSearch: v.webSearch,
      maxContext: v.maxContext,
      permissionMode: v.permissionMode,
      model: v.model,
      modelProviderId: v.modelProviderId,
      /*
        ★ 这里传空数组曾经让整个 Skill 功能在产品里悄悄失效:单测全绿,
        而模型永远看不到任何 Skill。现在传的是工作区的选装清单,
        **空清单在主进程一侧意味着「全都要」**(见 `SkillRegistry.resolve`),
        所以新建的工作区不需要用户先去哪里勾一遍。
      */
      skillIds: workspace.settings.activeSkillIds,
      skillSelectionMode: workspace.settings.skillSelectionMode
    }
  }

  /** 托盘 → ContentPart[]。只取已完成的,上传中/失败的不进 parts */
  function partsOf(text: string): ContentPart[] | undefined {
    const ready = tray.filter((x) => x.status === 'done' && (x.attachment !== undefined || x.path !== undefined))
    if (ready.length === 0) return undefined
    const parts: ContentPart[] = []
    if (text !== '') parts.push({ type: 'text', text })
    for (const x of ready) {
      if (x.path !== undefined) {
        if (remote && (x.source?.kind !== 'workspace' || x.source.workspaceId !== workspace.id)) continue
        parts.push({ type: 'file_ref', path: x.path, name: x.name, source: x.source })
        continue
      }
      const a = x.attachment as Attachment
      if (isImageMime(a.mime)) parts.push({ type: 'image', mime: a.mime, dataRef: a.url })
      else parts.push({ type: 'text', text: `[附件] ${a.displayName}` })
    }
    return parts
  }

  /**
   * `/goal [参数]`。
   *
   * ★ 四条路径的分叉在 `shared/domain/goal.ts` 的 `parseGoalCommand` —— 同一份规则
   *   要服务斜杠命令、`ProposeGoal` 工具、IPC 三个入口，写在这里就会漂。
   *
   * ★ 设立成功后把主进程给的 **kickoff** 原样发出去（`internal: true`）：
   *   会话正跑着时它走插话通道，闲着时它起一个新 run —— `send` 那一层
   *   已经把这两种情形收敛成了同一个调用（见 `stores/session.ts`）。
   *   在渲染层自己拼这段文案是错的：它是进模型上下文的英文 prompt，措辞被调过。
   */
  async function handleGoalCommand(args: string, value: ComposerValue): Promise<boolean> {
    const intent = parseGoalCommand(args)
    if (intent.kind === 'invalid') {
      setGoalNotice(intent.reason === 'empty' ? t('goal.error.empty') : t('goal.error.tooLong', { length: intent.length }))
      return false
    }
    if (intent.kind === 'set' && value.model === '') {
      setGoalNotice(t('composer.noAvailableModel'))
      return false
    }
    const targetSessionId = intent.kind === 'set' ? ensureSessionId() : sessionId
    if (targetSessionId === null) { setGoalNotice(t('goal.panel.empty')); return true }
    const target = sessionStore(targetSessionId)
    try {
      if (intent.kind === 'show') return true // Composer opens GoalPanel locally.
      if (intent.kind === 'clear') {
        const had = await getGoal(targetSessionId)
        await clearGoal(targetSessionId)
        target.setState({ goal: undefined })
        setGoalNotice(had === undefined ? t('goal.panel.empty') : t('goal.notice.cleared'))
        return true
      }
      const result = await setGoal(targetSessionId, intent.condition)
      if (!result.ok) {
        setGoalNotice(result.reason === 'too_long' ? t('goal.error.tooLong', { length: result.length }) : t('goal.error.empty'))
        return false
      }
      target.setState({ goal: result.goal })
      setGoalNotice(null)
      if (result.kickoff.length > 0) await target.getState().send('', sendOptionsOf(value), result.kickoff, true, result.goal.id)
      return true
    } catch {
      setGoalNotice(t('goal.error.updateFailed'))
      return false
    }
  }

  /**
   * `/skillify [补充说明]`:另开一条提炼会话,源会话就是这一条。
   *
   * ★ 标题在点下去这一刻现读,不用挂载时读到的那份:标题是首轮之后异步生成的,
   *   挂载时拿到的多半还是「新对话」,新会话就会叫「提炼 Skill · 新对话」。
   * 只在「有会话 id、跑过至少一轮、当前不在运行、自己不是提炼会话」时给(见下面 `onSkillifyCommand`)。
   */
  function handleSkillifyCommand(args: string): void {
    if (sessionId === null) return
    const sourceSessionId = sessionId
    void getSessionSummary(sourceSessionId)
      .then((detail) => startSkillExtraction({ workspace, sourceSessionId, sourceTitle: detail.session.title, hint: args, t }))
      .catch((error: unknown) => toast.error(t(skillExtractionErrorKey(error)), 'skill-extraction'))
  }

  // 两种布局共用同一个输入框实例的**写法**,但注意它们是两棵不同的子树 ——
  // 从空态切到有内容时 React 会重新挂载它。这没问题:草稿在 session store 里,
  // 而发出去的那一刻草稿已经清空了。
  const composer = (
    <SessionComposer
      storeKey={storeKey}
      suggestTrigger={suggestTrigger}
      started={started}
      mentionedFiles={mentionedFiles}
      workspace={workspace}
      fallbackModel={fallbackModel}
      {...(sessionModel === undefined ? {} : { sessionModel })}
      onModelChange={handleModelChange}
      onPermissionModeChange={retagQueuedPermission}
      sessionMode={sessionMode}
      onSessionModeChange={(mode) => {
        setCurrentSessionMode(mode)
        retagQueuedMode(mode)
        if (sessionId !== null) void setSessionMode(sessionId, mode).catch(() => undefined)
      }}
      onSetDefaultMode={(mode) => {
        void updateWorkspace({ id: workspace.id, settings: { defaultMode: mode } }).catch(() => undefined)
      }}
      onManageModes={quickWindow ? undefined : () => {
        sessionStorage.setItem('next-cowork:extensions-tab', 'modes')
        useWindowStore.getState().openFeature('extensions')
      }}
      running={running}
      attachments={tray}
      onAttachFiles={attachFiles}
      onPickAttachment={pickAttachment}
      onRemoveAttachment={removeFromTray}
      onRetryAttachment={retryUpload}
      sessionId={sessionId}
      contextTokens={transcript.lastInputTokens}
      contextSegments={transcript.contextUsage?.segments}
      contextCacheHitRate={cacheHitRateOf(transcript.usage)}
      conversationUsage={conversationUsage}
      onManageMcp={() => useWindowStore.getState().openSettings('connection')}
      contextCompacting={compacting}
      onCompactContext={compactContext}
      onSend={(text, v) => {
        const parts = partsOf(text)
        /*
          ★ **先铸 id,再对那个 id 的 store 发送。**

          草稿这一刻才变成一段会话。绑定会改 `tab.ref.sessionId` → `registry.tsx`
          的 key 跟着 `chatKey` 变 → 本组件立刻被卸载重挂。所以:
          - 清托盘放在 `send` **之前**(卸载之后再 setState 是无效更新);
          - `send` 不能 await —— await 之后的代码属于一棵已经不存在的树。
          重挂出来的那一个从新键的 store 里读,而乐观插入的用户消息已经在里面了。
        */
        setTray((items) => items.filter((item) => item.status !== 'done'))
        pendingFiles.current.clear()
        const targetSessionId = ensureSessionId()
        setCurrentSessionMode(v.mode)
        void setSessionMode(targetSessionId, v.mode).catch(() => undefined)
        void sessionStore(targetSessionId).getState().send(text, sendOptionsOf(v), parts)
      }}
      goal={goal}
      onGoalCommand={handleGoalCommand}
      {...(sessionId !== null && started && !running && skillSourceId === null ? { onSkillifyCommand: handleSkillifyCommand } : {})}
      onStop={stop}
    />
  )

  const goalLine = goalNotice === null ? null : (
    <p className="px-1 pb-1 whitespace-pre-wrap text-[11.5px] text-fg-faint" role="status">{goalNotice}</p>
  )

  /*
    ★ **队列区和清单跟着输入框走,不跟着转录走。** 它们属于「还没发出去的东西」那一侧,
    所以空态和常驻态两棵子树都要有它们 —— 用户在空会话里连发两条同样会排队。

    任务清单原先也住在这里(收起是队列左边的一颗球),现在搬到了右上角的活动胶囊
    (`ActivityCapsule`),这一块只剩发送队列。
  */
  const notices = (
    <div
      className="mx-auto w-full max-w-[760px] px-6 pb-2"
      data-testid="composer-notices"
    >
      {queuedInputs.length > 0 && (
        <PendingQueue
          className="max-w-none px-0 pb-0"
          items={queuedInputs}
          running={running}
          onPromote={promoteInput}
          onEdit={editInput}
          onDrop={dropInput}
          onMoveToDraft={moveInputToDraft}
          onResume={() => resumeQueue(storeKey)}
        />
      )}
      <WidgetContextChips storeKey={storeKey} />
    </div>
  )

  /*
    ★ **空会话不是「一个空的对话界面」,是另一屏。**
    参考实现(截图 c6184031)在这一屏把问候语和输入框**竖直居中**,没有空状态插画、
    没有状态行、输入框也不贴底。贴底的输入框加一张居中插画,看起来像是内容没加载出来。
    第一条消息发出去之后才切成「转录在上、输入框在下」的常驻布局。
  */
  /*
    ★★ 只读态是**另一棵树**,不是「把输入框藏起来的那棵」。

    分支写在这里(而不是给每个控件挂 `!readOnly &&`),是因为「零操作」这件事
    要能一眼验证:下面这棵树里没有 `composer` / `notices` / `todos` / `transferDialog`
    任何一个标识符,所以以后谁往常驻布局里加一个新按钮,都不会顺手漏进只读面板。
    空态那一屏同理跳过 —— 它的全部内容就是问候语加一个输入框。
  */
  // A run can already be active while its saved history and first events are still in flight.
  // Only show the welcome screen after a successful empty read, including in read-only panels.
  const historyPlaceholder = sessionId !== null && !hasRun(transcript, false) && !historyLoaded
    ? historyError !== null
      ? <ThreadLoadError onRetry={() => { void retryHistory() }} />
      : <ThreadSkeleton />
    : null

  if (readOnly) {
    return (
      <div className="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="chat-readonly">
        {subagentOf !== undefined && sessionId !== null && (
          <SubagentLiveFeed childSessionId={sessionId} parent={subagentOf} />
        )}
        {historyPlaceholder ?? <WorkspaceMarkdownProvider workspaceId={workspace.id} workspaceRoot={workspace.rootPath} onOpenFile={openMarkdownFile}>
          {/*
            ★ 只读面板只给 `root`(路径裁成相对),**不给 `open`** —— 这棵树的
            不变式是「零操作」(见上面那段),给了它文件名就会变成一枚能点的链接。
          */}
          <WorkspaceFileProvider root={workspace.rootPath}>
            <Thread
              sessionId={sessionId ?? undefined}
              transcript={transcript}
              runId={activeRunId}
              model={modelName}
              providerName={provider?.name}
              lastSeq={lastSeq}
              queued={0}
              readOnly
            />
          </WorkspaceFileProvider>
        </WorkspaceMarkdownProvider>}
      </div>
    )
  }

  if (historyPlaceholder !== null) {
    return (
      <div className="flex min-h-0 min-w-0 flex-1 flex-col">
        {historyPlaceholder}
        {goalLine}
        {notices}
        {composer}
        {transferDialog}
      </div>
    )
  }

  if (!started) {
    return (
      <div className="flex min-h-0 min-w-0 flex-1 flex-col items-center justify-center px-6 pb-10">
        <h1 className="mb-6 px-6 text-center text-[26px] leading-snug font-semibold text-fg">
          {t(GREETING_KEYS[dayPartOf(new Date().getHours())])}
        </h1>
        <div className="w-full">
          {goalLine}
          {notices}
          {composer}
          {transferDialog}
        </div>
      </div>
    )
  }

  /*
    ★ `min-w-0`:这一列是 `shell/Dock.tsx` 里那个横向 flex 的子项,默认的
    `min-width:auto` 会让它被**自己内容的最小宽度**撑开 —— 面板一窄,列宽就停在
    「转录/输入框那排容得下的最小值」(760 + 左右各 24 的内边距)而不再跟着面板走,
    而 dock 分组是 `overflow-hidden`:表现为窄面板里正文、代码块、输入框连同它下面
    那排读数一起在面板右边缘被齐刷刷切掉,没有滚动条也没有任何报错。
    有了它列宽恒等于面板宽,放不下的东西交给各自的容器决定是换行、截断还是隐藏。
  */
  return (
    <div className="relative flex min-h-0 min-w-0 flex-1 flex-col">
      <SubagentOpenProvider open={openSubagent}>
        <ToolStopProvider stop={stopRunningToolCall} background={backgroundRunningToolCall}>
          <WorkspaceMarkdownProvider workspaceId={workspace.id} workspaceRoot={workspace.rootPath} onOpenFile={openMarkdownFile}>
            <WorkspaceFileProvider root={workspace.rootPath} open={openToolFile}>
              <WidgetHostProvider value={widgetHost}>
                <Thread
                  sessionId={sessionId ?? undefined}
                  transcript={transcript}
                  runId={activeRunId}
                  model={modelName}
                  providerName={provider?.name}
                  lastSeq={lastSeq}
                  goal={goal}
                  queued={queuedInputs.length}
                  compactError={compactError}
                  {...(contextLimits === undefined ? {} : { contextLimits })}
                  onEditMessage={onEditMessage}
                  onDeleteReply={deleteReply}
                  onDeleteTurn={deleteTurn}
                  onBranchTurn={onBranchTurn}
                  workspaceId={workspace.id}
                  onOpenPlan={openMarkdownFile}
                  onExecutePlan={executePlan}
                  historyHasMore={historyHasMore}
                  loadingEarlier={loadingEarlier}
                  onLoadEarlier={loadEarlier}
                  {...(typeof skillSourceId === 'string' ? { skillExtractionSourceId: skillSourceId } : {})}
                />
              </WidgetHostProvider>
            </WorkspaceFileProvider>
          </WorkspaceMarkdownProvider>
        </ToolStopProvider>
      </SubagentOpenProvider>

      {goalLine}
      {notices}
      {composer}
      {transferDialog}
      {/*
        任务清单、子代理、后台命令收进右上角这一颗胶囊(原先清单是输入框上方的一张卡片)。
        浮在转录上方、不占版式 —— 见 `ActivityCapsule` 的文件头。
        ★ 排在最后:插在前面会挪动后面兄弟的位置,加载完历史时输入框会被重建、丢焦点。
      */}
      <ActivityCapsule
        workspaceId={workspace.id}
        todos={todos}
        execution={execution}
        subagents={subagentList}
        onOpenSubagent={openSubagent}
      />
    </div>
  )
}

function summarizeConversationUsage(
  persisted: Readonly<Record<string, RunUsage>> | undefined,
  live: RunUsage | undefined
): ConversationUsageSummary | undefined {
  const historical = Object.entries(persisted ?? {})
    .sort(([left], [right]) => right.localeCompare(left))
  const usages = historical.map(([, usage]) => usage)
  // run_end 会把实时对象原样归档；引用相同说明它已在 historical 中，不能再算一次。
  if (live !== undefined && !usages.includes(live)) usages.push(live)
  if (usages.length === 0) return undefined

  let cost: RunCost | null | undefined
  for (const usage of usages) {
    if (usage.cost === undefined) continue
    if (usage.cost === null) {
      cost = null
      continue
    }
    if (cost === null) continue
    if (cost === undefined) {
      cost = { ...usage.cost }
      continue
    }
    cost = cost.currency === usage.cost.currency
      ? { currency: cost.currency, micros: cost.micros + usage.cost.micros }
      : null
  }

  const latestTps = live === undefined
    ? historical.map(([, usage]) => tokensPerSecond(usage.outputTokens, usage.upstreamMs))
        .find((value) => value !== undefined)
    : tokensPerSecond(live.outputTokens, live.upstreamMs)

  return {
    inputTokens: usages.reduce((total, usage) => total + usage.inputTokens, 0),
    cacheReadTokens: usages.reduce((total, usage) => total + (usage.cacheReadInputTokens ?? 0), 0),
    cacheWriteTokens: usages.reduce((total, usage) => total + (usage.cacheCreationInputTokens ?? 0), 0),
    outputTokens: usages.reduce((total, usage) => total + usage.outputTokens, 0),
    ...(latestTps === undefined ? {} : { latestTps }),
    ...(cost === undefined ? {} : { cost })
  }
}

/**
 * widget 经 `host.setContext()` 发布、会随下一条消息交给模型的状态摘要。
 *
 * ★ 必须让用户看得见、删得掉:那段文字是模型写的 widget 产出的,而它会以用户这条消息的
 *   名义进模型上下文。悄悄附上去,等于让一张图替用户补了半句话。
 */
function WidgetContextChips({ storeKey }: { storeKey: string }): ReactNode {
  const { t } = useI18n()
  const useSession = sessionStore(storeKey)
  const entries = useSession((state) => state.widgetContext)
  const remove = useSession((state) => state.removeWidgetContext)
  if (entries.length === 0) return null
  return (
    <div className="flex flex-wrap items-center gap-1.5 pt-1" data-testid="widget-context">
      <span className="text-[11px] text-fg-faint">{t('widget.context.label')}</span>
      {entries.map((entry) => (
        <span
          key={entry.key}
          title={entry.text}
          className="inline-flex max-w-[260px] items-center gap-1 rounded-pill bg-tint px-2 py-0.5 text-[11px] text-fg-muted"
        >
          <span className="truncate">{entry.title.replace(/_/g, ' ')}</span>
          <button
            type="button"
            aria-label={t('widget.context.remove')}
            className="cursor-pointer border-0 bg-transparent p-0 text-fg-faint hover:text-fg"
            onClick={() => { remove(entry.key) }}
          >
            ×
          </button>
        </span>
      ))}
    </div>
  )
}

function SessionComposer({ storeKey, ...props }: { storeKey: string } & Omit<ComponentProps<typeof Composer>, 'draft' | 'onDraft'>): ReactNode {
  const useSession = sessionStore(storeKey)
  const draft = useSession((state) => state.draft)
  const setDraft = useSession((state) => state.setDraft)
  return <Composer {...props} draft={draft} onDraft={setDraft} />
}
