import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react'
import { motion } from 'motion/react'
import { ArrowUpRight, Check, Copy, Download, FileText, Pencil } from 'lucide-react'
import type {
  AskUserQuestion,
  InteractionResponse,
  PendingInteraction
} from '../../../../shared/agent/interaction'
import { workflowLayers } from '../../../../shared/agent/workflow'
import { useI18n, type Translate } from '../../i18n'
import { AgentMarkdown } from '../../components/markdown'
import { CheckboxCards, RadioCards, type ChoiceOption } from '../../components/ui/ChoiceCards'
import { ActionIconButton, useTransientStatus } from '../../components/ui/ActionIconButton'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { listInteractions, onAgentEvent, respondInteraction } from '../../services/agent'
import { onGoalChanged } from '../../services/goal'
import { copyText, saveTextFile } from '../../services/app'
import type { PlanExecutionRef } from '../../../../shared/domain/plan-file'
import { documentKey, isDocumentDirty, useDocumentsStore } from '../../stores/documents'
import { motionScale, useMotionLevel } from '../../theme/useMotionLevel'
import { ActionRows, type ActionRowSpec } from './ActionRows'
import { isEditableTarget, isSelfHandlingButton, resolveRowKey } from './interaction-keys'
import {
  choiceOptions, deriveAnswers, initialDraft, isComplete, nextUnanswered, otherValue, shouldAdvance,
  toggle, type AskUserDraft
} from './ask-user'

/** Includes descendant runs: a child waiting for approval must not leave its parent stuck. */
export function InteractionPanel({ runId, sessionId, workspaceId, onOpenPlan, onExecute }: {
  runId?: string
  sessionId?: string
  workspaceId?: string
  onOpenPlan?: (path: string) => void
  onExecute?: (ref: PlanExecutionRef, source: 'current_session' | 'new_session') => void
}): ReactNode {
  const { t } = useI18n()
  const [pending, setPending] = useState<PendingInteraction[]>([])
  const [failed, setFailed] = useState(false)
  const [reload, setReload] = useState(0)
  useEffect(() => {
    let disposed = false
    let version = 0
    const refresh = (): void => {
      const current = ++version
      void listInteractions(runId, sessionId).then((items) => {
        if (disposed || current !== version) return
        setPending(items)
        setFailed(false)
      }).catch(() => {
        if (!disposed && current === version) setFailed(true)
      })
    }
    // Subscribe before querying, so an interaction created during the query is not lost.
    const off = onAgentEvent((envelope) => {
      if (envelope.events.some((event) => event.type === 'interaction_request' || event.type === 'interaction_resolved'
        || event.type === 'subagent_start' || event.type === 'run_end')) refresh()
    })
    const offGoal = onGoalChanged((change) => { if (change.sessionId === sessionId) refresh() })
    refresh()
    return () => { disposed = true; off(); offGoal() }
  }, [runId, sessionId, reload])
  if (pending.length === 0 && !failed) return null
  return (
    <div className="w-full" aria-live="polite">
      {failed && <button type="button" onClick={() => setReload((v) => v + 1)} className="text-[12px] text-danger">
        {t('agent.interaction.loadFailed')}
      </button>}
      {pending.map((interaction) => <InteractionCard key={interaction.id} interaction={interaction} workspaceId={workspaceId}
        onOpenPlan={onOpenPlan} onExecute={onExecute}
        onAnswered={() => setPending((items) => items.filter((item) => item.id !== interaction.id))} />)}
    </div>
  )
}

/** 提交这件事三类交互是同一份:一次只允许一发,失败留在原地让用户重试。 */
function useRespond(onAnswered: () => void, onResolved?: () => void): {
  busy: boolean
  errorKey: string | null
  setErrorKey: (key: string | null) => void
  respond: (response: InteractionResponse) => void
} {
  const [busy, setBusy] = useState(false)
  const [errorKey, setErrorKey] = useState<string | null>(null)
  return {
    busy,
    errorKey,
    setErrorKey,
    respond: (response) => {
      if (busy) return
      setBusy(true)
      setErrorKey(null)
      void respondInteraction(response).then(() => { onResolved?.(); onAnswered() }).catch(() => {
        setErrorKey('agent.interaction.failed')
        setBusy(false)
      })
    }
  }
}

// 苹果风格:胶囊按钮 / 玻璃输入框,样式在 theme.css 的 .apple-btn / .apple-field
const GHOST_BUTTON = 'apple-btn'
const PRIMARY_BUTTON = 'apple-btn-primary'
const FIELD = [
  'apple-field w-full resize-none py-2 text-[13px] leading-[1.6]',
  'disabled:opacity-40'
].join(' ')

/**
 * 交互卡共用的外壳:标题、可滚动的主体、可选的固定操作区、错误行和底部一条。
 * 工具授权的动作放在 actions 中,长参数只滚动正文,不会把授权选项推到看不见的地方。
 */
function CardShell({
  kind, title, header, children, actions, errorKey, busy, onSubmit, onKeyDown, bodyClassName, footer, takeFocus = false,
  preview = false
}: {
  kind: PendingInteraction['kind']
  title: string
  /** 标题下面那条固定不滚动的东西(多题时的切换条) */
  header?: ReactNode
  children: ReactNode
  /** 正文之外的操作区,不随参数预览或编辑器滚动。 */
  actions?: ReactNode
  errorKey: string | null
  busy: boolean
  onSubmit: (event: FormEvent) => void
  onKeyDown?: (event: KeyboardEvent<HTMLFormElement>) => void
  bodyClassName?: string
  /** 底部一条。用 `ml-auto` 自己控制靠哪边。 */
  footer: ReactNode
  /**
   * 挂载时把焦点收进这张卡。带行的卡由 `ActionRows` 自己接管,不用这个;
   * `ask_user` 的行是 Radix 管的,只能在这一层接 —— 不接的话数字键形同虚设,
   * 焦点常年在下面的输入区上。
   *
   * ★ **只在用户没在打字时接**:写到一半被抢走焦点,比没有快捷键糟得多。
   */
  takeFocus?: boolean
  /**
   * 这张卡是**工具卡片里的只读预览**(参数还在流),不是待决表里那张可作答的。
   *
   * ★ 只改两件事,别让它长成第二套外壳:
   * 1. **换掉探针属性。** QA 脚本按 `[data-interaction-kind=ask_user]` 找可作答的卡
   *    并往里填字(`scripts/agent-protocol-qa.mjs:238`);预览在 DOM 里排在它前面,
   *    带同一套属性的话 `querySelector` 会命中预览,而预览的输入框是 disabled 的 ——
   *    表现为脚本卡在「填不进去」,却看不出选错了元素。
   * 2. **不抢焦点。** 用户可能正在输入框里打字,而预览是模型每来一个 token 就重渲一次的。
   */
  preview?: boolean
}): ReactNode {
  const { t } = useI18n()
  const form = useRef<HTMLFormElement>(null)
  const hasActions = actions !== undefined
  useLayoutEffect(() => {
    if (!hasActions) return
    const card = form.current
    const viewport = card?.closest('[data-testid="thread"]')
    if (card === null || !(viewport instanceof HTMLElement)) return
    // 限制整张卡的高度,由 flex 同步压缩正文;编辑或报错时不再分两帧改正文高度。
    // 64px 留给聊天区的状态行与底部留白。
    const measure = (): void => { card.style.maxHeight = `${Math.max(0, viewport.clientHeight - 64)}px` }
    const observer = new ResizeObserver(measure)
    observer.observe(viewport)
    measure()
    return () => { observer.disconnect(); card.style.maxHeight = '' }
  }, [hasActions])
  useEffect(() => {
    if (!takeFocus || preview || isEditableTarget(document.activeElement)) return
    form.current?.focus({ preventScroll: true })
  }, [takeFocus, preview])
  return (
    <form ref={form} tabIndex={takeFocus && !preview ? -1 : undefined}
      onSubmit={onSubmit} onKeyDown={onKeyDown}
      data-testid={preview ? 'agent-interaction-preview' : 'agent-interaction'}
      {...(preview ? { 'data-preview-kind': kind } : { 'data-interaction-kind': kind })}
      className={`mb-2 flex min-h-0 flex-col rounded-card border border-stroke bg-surface-raised/80 text-fg outline-none ${hasActions ? 'scroll-thin overflow-y-auto' : 'overflow-hidden'}`}>
      {/* 需求：审批卡用一块中性表面承载内容，把强调色留给选择与主动作；
          标题和多题导航固定在上方，切题或展开输入区时不能一起滚走。 */}
      <div className="shrink-0 border-b border-hairline px-3 py-2.5">
        <h2 className="text-[13px] font-medium">{title}</h2>
        {header !== undefined && <div className="mt-2">{header}</div>}
      </div>
      <div data-testid="interaction-body"
        className={`scroll-thin min-h-0 overflow-auto px-3 pt-3 pb-0.5 ${bodyClassName ?? 'max-h-[30vh]'}`}>{children}</div>
      {hasActions && <div data-testid="interaction-actions" className="shrink-0 border-t border-hairline px-3 pt-2.5">{actions}</div>}
      {errorKey !== null && <p role="alert" className="shrink-0 px-3 pt-2 text-[12px] text-danger">{t(errorKey)}</p>}
      {busy && <p className="shrink-0 px-3 pt-2 text-[12px] text-fg-muted">{t('agent.interaction.sending')}</p>}
      <div className="mt-3 flex shrink-0 flex-wrap items-center gap-2 border-t border-hairline bg-surface/35 px-3 py-2.5">{footer}</div>
    </form>
  )
}

/** 底部那行键盘提示。没有行可点名时不显示 —— 提示一件做不到的事更糟。 */
function KeyboardHint({ show }: { show: boolean }): ReactNode {
  const { t } = useI18n()
  if (!show) return null
  return <span className="text-[11px] text-fg-faint">{t('agent.interaction.keyboardHint')}</span>
}

function InteractionCard({ interaction, workspaceId, onOpenPlan, onExecute, onAnswered }: {
  interaction: PendingInteraction
  workspaceId?: string
  onOpenPlan?: (path: string) => void
  onExecute?: (ref: PlanExecutionRef, source: 'current_session' | 'new_session') => void
  onAnswered: () => void
}): ReactNode {
  if (interaction.kind === 'goal_proposal') return <GoalProposalCard interaction={interaction} onAnswered={onAnswered} />
  if (interaction.kind === 'ask_user') return <AskUserCard interaction={interaction} onAnswered={onAnswered} />
  if (interaction.kind === 'plan_approval') return <PlanApprovalCard interaction={interaction} workspaceId={workspaceId}
    onOpenPlan={onOpenPlan} onExecute={onExecute} onAnswered={onAnswered} />
  if (interaction.kind === 'workflow_approval') return <WorkflowApprovalCard interaction={interaction} onAnswered={onAnswered} />
  if (interaction.kind === 'widget_submit') return <WidgetSubmitNotice interaction={interaction} onAnswered={onAnswered} />
  return <ToolApprovalCard interaction={interaction} onAnswered={onAnswered} />
}

/**
 * 交互模式的 widget 在等提交。**题面就是对话里那张图**,这里不再画一张卡 —— 只留一行
 * 说明 run 为什么停着,和一个"不提交了"的出口(否则用户只能停掉整轮)。
 */
function WidgetSubmitNotice({ interaction, onAnswered }: {
  interaction: Extract<PendingInteraction, { kind: 'widget_submit' }>
  onAnswered: () => void
}): ReactNode {
  const { t } = useI18n()
  const { busy, errorKey, respond } = useRespond(onAnswered)
  return (
    <div data-testid="agent-interaction" data-interaction-kind="widget_submit"
      className="mb-2 flex items-center gap-2 px-1 text-[12px] text-fg-muted">
      <span className="min-w-0 flex-1 truncate">{t('widget.submit.waiting', { title: interaction.title.replace(/_/g, ' ') })}</span>
      {errorKey !== null && <span role="alert" className="text-danger">{t(errorKey)}</span>}
      <button type="button" disabled={busy} className={GHOST_BUTTON}
        onClick={() => respond({ id: interaction.id, kind: 'widget_submit', value: null })}>
        {t('widget.submit.dismiss')}
      </button>
    </div>
  )
}

function GoalProposalCard({ interaction, onAnswered }: {
  interaction: Extract<PendingInteraction, { kind: 'goal_proposal' }>
  onAnswered: () => void
}): ReactNode {
  const { t } = useI18n()
  const { busy, errorKey, respond } = useRespond(onAnswered)
  const decide = (approved: boolean): void => respond({ id: interaction.id, kind: 'goal_proposal', approved })
  return <CardShell kind="goal_proposal" title={t('goal.proposal.title')} errorKey={errorKey} busy={busy}
    onSubmit={(event) => event.preventDefault()} footer={<KeyboardHint show={!busy} />}>
    <GoalProposalBody condition={interaction.condition} />
    <ActionRows disabled={busy} ariaLabel={t('goal.proposal.title')} rows={[
      { value: 'approve', label: t('goal.proposal.approve') },
      { value: 'decline', label: t('goal.proposal.decline') }
    ]} onRun={(value) => decide(value === 'approve')} />
  </CardShell>
}

/** 提案正文。抽出来只为一件事:预览和待决卡读的是同一段版式(见 `GoalProposalPreviewCard`)。 */
function GoalProposalBody({ condition }: { condition: string }): ReactNode {
  const { t } = useI18n()
  return <>
    <p className="mb-2 text-[12px] text-fg-muted">{t('goal.proposal.body')}</p>
    <p className="selectable mb-2 whitespace-pre-wrap break-words text-[13px]">{condition}</p>
  </>
}

function AskUserCard({ interaction, onAnswered }: {
  interaction: Extract<PendingInteraction, { kind: 'ask_user' }>
  onAnswered: () => void
}): ReactNode {
  const { t } = useI18n()
  const { busy, errorKey, respond } = useRespond(onAnswered)
  const questions = interaction.questions
  // 每道题两份状态:选中的项 + 「其它」里写的字。分开存,用户在选项之间来回切时,
  // 已经写好的那句话才不会被清掉 —— 推导规则都在 `./ask-user`。
  const [draft, setDraft] = useState<AskUserDraft>(() => initialDraft(questions))
  // ★ **一次只渲染一道题。** 三道题铺开就得滚动,而滚动条上面那半截会被误认为
  // 「就这些了」—— 用户答完看得见的部分就去点提交,然后发现按钮是灰的,却不知道
  // 还差哪一道。切换条把「一共几道、答了几道」摆在固定位置,不随内容滚走。
  const [active, setActive] = useState(0)
  const answers = deriveAnswers(questions, draft)
  const answered = answers.filter((answer) => answer.length > 0).length
  const complete = isComplete(answers)
  const question = questions[active] ?? questions[0]!
  const multi = questions.length > 1
  const options = choiceOptions(question, t('agent.interaction.other'))
  // 纯问答题没有选项可点名,直接给输入框 —— 只为一个「其它」渲染一行是多余的。
  const plain = question.options.length === 0
  const picked = draft.picked[active] ?? []
  // 没答完时右下角那颗是「下一题」而不是一颗按不动的灰键 —— 灰键只说明「不行」,
  // 不说明「下一步该干嘛」,而这里恰好有明确的下一步。
  const goNext = complete ? null : nextUnanswered(answers, active)

  function pick(value: string): void {
    const next = question.multiSelect ? toggle(options, picked, value) : [value]
    setDraft((d) => ({ ...d, picked: d.picked.map((v, i) => (i === active ? next : v)) }))
    // 单选题选完就往下走 —— 但要拿「答完这道之后」的答案去找下一道,
    // 用当前的 `answers` 会把刚答的这道也算成没答,原地打转。
    if (!shouldAdvance(question, next)) return
    const after = answers.map((answer, i) => (i === active ? next : answer))
    const target = nextUnanswered(after, active)
    if (target !== null) setActive(target)
  }

  function submit(): void {
    if (!complete) {
      if (goNext !== null) setActive(goNext)
      return
    }
    respond({ id: interaction.id, kind: 'ask_user', answers })
  }

  return (
    <CardShell
      kind="ask_user"
      title={t('agent.interaction.question')}
      bodyClassName="max-h-[46vh]"
      takeFocus
      onKeyDown={(event) => {
        // 方向键留给 Radix 的漫游焦点,别和它抢。
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') return
        const action = resolveRowKey(event, {
          count: plain ? 0 : options.length,
          active: 0,
          inEditable: isEditableTarget(event.target),
          expanded: false,
          onOtherButton: isSelfHandlingButton(event.target)
        })
        if (action === null || busy) return
        if (action.kind === 'activate') {
          const option = options[action.index]
          if (option === undefined) return
          event.preventDefault()
          pick(option.value)
          return
        }
        if (action.kind !== 'run') return
        event.preventDefault()
        submit()
      }}
      header={multi ? (
        <QuestionTabs questions={questions} answers={answers} active={active} disabled={busy}
          onChange={setActive} />
      ) : undefined}
      errorKey={errorKey}
      busy={busy}
      footer={<>
        <KeyboardHint show={!plain && options.length > 0} />
        <span className="ml-auto flex flex-wrap items-center gap-2">
          <button type="button" disabled={busy} className={GHOST_BUTTON}
            onClick={() => respond({ id: interaction.id, kind: 'ask_user', answers: null })}>
            {t('agent.interaction.dismiss')}
          </button>
          <button type={goNext === null ? 'submit' : 'button'}
            onClick={goNext === null ? undefined : () => setActive(goNext)}
            disabled={busy || (goNext === null && !complete)}
            className={PRIMARY_BUTTON}>
            {goNext !== null
              ? t('agent.interaction.nextQuestion')
              : multi
                ? t('agent.interaction.submitProgress', { done: String(answered), total: String(questions.length) })
                : t('agent.interaction.submit')}
          </button>
        </span>
      </>}
      onSubmit={(event) => { event.preventDefault(); submit() }}
    >
      <QuestionBlock
        key={active}
        question={question}
        index={active}
        options={options}
        plain={plain}
        busy={busy}
        picked={picked}
        typed={draft.typed[active] ?? ''}
        onPick={pick}
        onTyped={(next) => setDraft((d) => ({ ...d, typed: d.typed.map((v, i) => (i === active ? next : v)) }))}
        t={t}
      />
    </CardShell>
  )
}

/**
 * 多题时标题下面那条切换条。
 *
 * 抽出来是因为**只读预览也要用它**:题是一道一道流出来的,不给切换条的话
 * 后面几道在写完之前根本看不见 —— 而「一共会问我几件事」恰恰是这张预览的价值。
 */
function QuestionTabs({ questions, answers, active, disabled, onChange }: {
  questions: readonly AskUserQuestion[]
  /** 与 `questions` 对齐的答案。预览传全空数组 —— 没答过,也就没有钩。 */
  answers: readonly (readonly string[])[]
  active: number
  disabled: boolean
  onChange: (index: number) => void
}): ReactNode {
  const { t } = useI18n()
  // 题头可能长,窄窗口下让它横向滚,而不是把卡片撑破
  return (
    <div className="scroll-thin overflow-x-auto pb-0.5">
      <fieldset disabled={disabled} className="contents">
        <SegmentedControl
          label={t('agent.interaction.question')}
          value={String(active)}
          onValueChange={(value) => onChange(Number(value))}
          options={questions.map((question, index) => ({
            value: String(index),
            // 打钩的那道已经答过 —— 切换条同时是进度表
            label: (answers[index] ?? []).length > 0 ? `✓ ${question.header}` : question.header
          }))}
        />
      </fieldset>
    </div>
  )
}

/**
 * ★ **工具卡片里那张只读的问题卡 —— 与上面那张可作答的是同一套组件。**
 *
 * 需求:模型写 `AskUserQuestion` 的参数要好几秒,这几秒里用户应该已经能读到题面,
 * 只是还不能答(答案无处可交:此刻还没有 `interaction.id`)。
 *
 * ★ **不许在这里另画一套版式。** 外壳、切换条、题面全部复用 `CardShell` /
 * `QuestionTabs` / `QuestionBlock`,否则题面从预览换成可作答的那一瞬间会跳一下,
 * 而那正是用户盯着看的时刻;两套版式之后也必然各自演化,改一处漏一处。
 *
 * ★ **切题可以点,作答不能点。** 切换条只是翻页,点不出任何会失败的承诺;
 * 选项和输入框走 `busy` 那条路全部 disabled —— 与「正在提交中」共用同一种表达。
 */
export function AskUserPreviewCard({ questions }: { questions: readonly AskUserQuestion[] }): ReactNode {
  const { t } = useI18n()
  const [active, setActive] = useState(0)
  // 题在流,数组会变长也可能整体重来;夹住下标,免得越界渲染成一张空卡
  const index = Math.min(active, questions.length - 1)
  const question = questions[index]
  if (question === undefined) return null
  const empty = questions.map(() => [])
  return (
    <CardShell
      kind="ask_user"
      preview
      title={t('agent.interaction.question')}
      bodyClassName="max-h-[46vh]"
      errorKey={null}
      busy={false}
      onSubmit={(event) => event.preventDefault()}
      header={questions.length > 1
        ? <QuestionTabs questions={questions} answers={empty} active={index} disabled={false} onChange={setActive} />
        : undefined}
      footer={<span className="text-[11px] text-fg-faint">{t('agent.interaction.previewOnly')}</span>}
    >
      <QuestionBlock
        key={index}
        question={question}
        index={index}
        options={choiceOptions(question, t('agent.interaction.other'))}
        plain={question.options.length === 0}
        busy
        picked={[]}
        typed=""
        onPick={() => {}}
        onTyped={() => {}}
        t={t}
      />
    </CardShell>
  )
}

/** 目标提案的只读预览 —— 与 `GoalProposalCard` 同一套版式,理由见上面那张。 */
export function GoalProposalPreviewCard({ condition }: { condition: string }): ReactNode {
  const { t } = useI18n()
  return (
    <CardShell kind="goal_proposal" preview title={t('goal.proposal.title')} errorKey={null} busy={false}
      onSubmit={(event) => event.preventDefault()}
      footer={<span className="text-[11px] text-fg-faint">{t('agent.interaction.previewOnly')}</span>}>
      <GoalProposalBody condition={condition} />
    </CardShell>
  )
}

function QuestionBlock({ question, index, options, plain, busy, picked, typed, onPick, onTyped, t }: {
  question: AskUserQuestion
  index: number
  options: ChoiceOption[]
  plain: boolean
  busy: boolean
  picked: string[]
  typed: string
  onPick: (value: string) => void
  onTyped: (next: string) => void
  t: Translate
}): ReactNode {
  const scale = motionScale(useMotionLevel())
  const other = otherValue(question)
  const answerField = (
    <textarea
      // 选了「其它」之后输入框才出现,焦点得跟过去,否则用户要再点一下才能写
      autoFocus={!plain}
      aria-label={t('agent.interaction.answer')}
      placeholder={t('agent.interaction.answer')}
      value={typed} onChange={(e) => onTyped(e.target.value)} rows={2} maxLength={32768} disabled={busy}
      name={`answer-${index}`}
      className={FIELD}
    />
  )

  return (
    <motion.section
      data-testid="agent-question"
      data-question-index={index}
      // 需求：多题切换要像同一张审批卡内的内容推进，不能硬切得像整张卡重新挂载。
      initial={{ opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.18 * scale, ease: [0.23, 1, 0.32, 1] }}
    >
      <div className="mb-1.5 flex items-center gap-2">
        <span className="rounded-pill border border-hairline bg-surface-input/55 px-2 py-0.5 text-[11px] leading-[1.6] text-fg-muted">{question.header}</span>
        {question.multiSelect && <span className="text-[11px] text-fg-faint">{t('agent.interaction.multiSelect')}</span>}
      </div>
      <AgentMarkdown className="mb-2" content={question.question} />
      {/* ★ 「其它」的输入框长在**那一行里面**,不在列表下面 —— 挂在下面时,它和
          上面那些没选中的选项看起来是并列的两个作答入口,用户看不出哪个算数。 */}
      {plain ? answerField : (question.multiSelect
        ? <CheckboxCards values={picked} options={options} disabled={busy} ariaLabel={question.header}
          onToggle={onPick} renderExpanded={(option) => (option.value === other ? answerField : null)} />
        : <RadioCards value={picked[0] ?? ''} options={options} disabled={busy} ariaLabel={question.header}
          onValueChange={onPick} renderExpanded={(option) => (option.value === other ? answerField : null)} />)}
    </motion.section>
  )
}

/** 普通工具授权卡。 */
function ToolApprovalCard({ interaction, onAnswered }: {
  interaction: Extract<PendingInteraction, { kind: 'tool_permission' }>
  onAnswered: () => void
}): ReactNode {
  const { t } = useI18n()
  const { busy, errorKey, setErrorKey, respond } = useRespond(onAnswered)
  const original = JSON.stringify(interaction.input, null, 2)
  const [editing, setEditing] = useState(false)
  const [input, setInput] = useState(original)
  const editor = useRef<HTMLTextAreaElement>(null)
  const rule = interaction.suggestedRule
  useLayoutEffect(() => {
    // 原生 autoFocus 会滚动聊天区来露出整个 textarea,打断 Thread 的贴底跟随。
    if (editing) editor.current?.focus({ preventScroll: true })
  }, [editing])

  const rows: ActionRowSpec[] = [
    { value: 'allow_once', label: t('agent.interaction.allowOnce') },
    ...(rule === undefined ? [] : [{
      value: 'allow_always',
      label: t('agent.interaction.allowAlways'),
      // 「会往本机配置里写一条规则」是这一项的全部代价,必须跟着它,而不是
      // 缩在卡片角落的一行小字里。
      description: t('agent.interaction.allowAlwaysHint', { rule })
    }]),
    {
      value: 'allow_edited',
      label: t('agent.interaction.editArgumentsRow'),
      icon: <Pencil aria-hidden size={13} className="text-fg-faint" />,
      expands: true
    }
  ]

  function cancelEditing(): void {
    setEditing(false)
    setInput(original)
    setErrorKey(null)
  }

  function run(value: string): void {
    if (value === 'allow_once') {
      respond({ id: interaction.id, kind: 'tool_permission', decision: { kind: 'allow_once' } })
      return
    }
    if (value === 'allow_always') {
      respond({ id: interaction.id, kind: 'tool_permission', decision: { kind: 'allow_always', scope: 'workspace' } })
      return
    }
    let parsed: unknown
    try { parsed = JSON.parse(input) } catch { setErrorKey('agent.interaction.invalidJson'); return }
    respond({ id: interaction.id, kind: 'tool_permission', decision: { kind: 'allow_edited', input: parsed } })
  }

  return <CardShell
    kind="tool_permission"
    title={t('agent.interaction.permission', { tool: interaction.toolName })}
    errorKey={errorKey}
    busy={busy}
    actions={editing ? (
      <div className="flex flex-wrap items-center justify-end gap-2">
        <button type="button" disabled={busy} className={GHOST_BUTTON} onClick={cancelEditing}>
          {t('agent.interaction.keepOriginal')}
        </button>
        <button type="button" disabled={busy} className={PRIMARY_BUTTON} onClick={() => run('allow_edited')}>
          {t('agent.interaction.allowOnce')}
        </button>
      </div>
    ) : (
      <ActionRows rows={rows} disabled={busy}
        ariaLabel={t('agent.interaction.permission', { tool: interaction.toolName })}
        onRun={run} onExpandedChange={() => setEditing(true)} />
    )}
    footer={<>
      <KeyboardHint show={!busy && !editing} />
      <button type="button" disabled={busy} className={`${GHOST_BUTTON} ml-auto`}
        onClick={() => respond({ id: interaction.id, kind: 'tool_permission', decision: { kind: 'deny' } })}>
        {t('agent.interaction.deny')}
      </button>
    </>}
    onSubmit={(event) => { event.preventDefault() }}
    onKeyDown={(event) => {
      if (!editing || busy || event.defaultPrevented) return
      const action = resolveRowKey(event, {
        count: 0,
        active: 0,
        inEditable: isEditableTarget(event.target),
        expanded: true,
        onOtherButton: isSelfHandlingButton(event.target)
      })
      if (action === null) return
      event.preventDefault()
      if (action.kind === 'collapse') cancelEditing()
      else if (action.kind === 'run') run('allow_edited')
    }}
  >
    {interaction.destructive && <p className="mb-2 text-[12px] text-fg-muted">{t('agent.interaction.changes')}</p>}
    {/* 编辑器与预览共用正文滚动区;编辑态只提交修改后的参数,不提供持久授权。 */}
    {editing ? <textarea ref={editor} aria-label={t('agent.interaction.arguments')} value={input}
      onChange={(event) => setInput(event.target.value)} disabled={busy} rows={6}
      className={`${FIELD} font-mono text-[12px]`} />
      : <pre className="selectable mb-2 whitespace-pre-wrap break-all text-[12px]">{input}</pre>}
  </CardShell>
}

/** 计划文件名。路径分隔符两种都要认 —— Windows 上拿到的是反斜杠。 */
export function planFileName(path: string): string {
  const name = path.split(/[\\/]/).pop()?.trim() ?? ''
  return name === '' ? 'plan.md' : name
}

/** One Markdown file is the only plan source of truth. */
function PlanApprovalCard({ interaction, workspaceId, onOpenPlan, onExecute, onAnswered }: {
  interaction: Extract<PendingInteraction, { kind: 'plan_approval' }>
  workspaceId?: string
  onOpenPlan?: (path: string) => void
  onExecute?: (ref: PlanExecutionRef, source: 'current_session' | 'new_session') => void
  onAnswered: () => void
}): ReactNode {
  const { t } = useI18n()
  const execution = useRef<'current_session' | 'new_session' | null>(null)
  const entry = useDocumentsStore((state) => workspaceId === undefined ? undefined : state.entries[documentKey(workspaceId, interaction.path)])
  const save = useDocumentsStore((state) => state.save)
  const [feedback, setFeedback] = useState('')
  const [preparing, setPreparing] = useState(false)
  const [copied, setCopied] = useTransientStatus()
  const [exported, setExported] = useTransientStatus()
  const { busy, errorKey, setErrorKey, respond } = useRespond(onAnswered, () => {
    if (execution.current !== null && onExecute !== undefined) {
      onExecute({ planId: interaction.planId, path: interaction.path }, execution.current)
      execution.current = null
    }
  })
  const disabled = busy || preparing
  const plan = entry?.draft ?? interaction.plan

  async function saveOpenDraft(): Promise<boolean> {
    if (workspaceId === undefined || entry === undefined || !isDocumentDirty(entry)) return true
    setPreparing(true)
    const saved = await save(workspaceId, interaction.path).catch(() => false)
    setPreparing(false)
    if (!saved) setErrorKey('agent.interaction.planSaveFailed')
    return saved
  }

  async function decide(action: 'approve_current' | 'approve_new_session' | 'request_revision' | 'reject'): Promise<void> {
    if (disabled) return
    if (action === 'request_revision' && feedback.trim() === '') {
      setErrorKey('agent.interaction.planFeedbackRequired')
      return
    }
    if (!(await saveOpenDraft())) return
    if (action === 'approve_current') execution.current = onExecute === undefined ? null : 'current_session'
    if (action === 'approve_new_session') execution.current = onExecute === undefined ? null : 'new_session'
    respond({ id: interaction.id, kind: 'plan_approval', action, ...(feedback.trim() === '' ? {} : { feedback: feedback.trim() }) })
  }

  const rows: ActionRowSpec[] = [
    {
      value: 'approve_current',
      label: t(onExecute === undefined ? 'agent.interaction.approvePlan' : 'agent.interaction.executeCurrent')
    },
    ...(onExecute === undefined ? [] : [{
      value: 'approve_new_session',
      label: t('agent.interaction.executeNewSession')
    }]),
    {
      value: 'request_revision',
      label: t('agent.interaction.requestRevisionRow'),
      icon: <Pencil aria-hidden size={13} className="text-fg-faint" />,
      expands: true
    }
  ]

  return <CardShell
    kind="plan_approval"
    title={t('agent.interaction.plan')}
    bodyClassName="max-h-none"
    errorKey={errorKey}
    busy={disabled}
    footer={<>
      <KeyboardHint show={!disabled} />
      <button type="button" disabled={disabled} className={`${GHOST_BUTTON} ml-auto`}
        onClick={() => { void decide('reject') }}>
        {t('agent.interaction.abandonPlan')}
      </button>
    </>}
    onSubmit={(event) => { event.preventDefault() }}
  >
    <div className="mb-2 overflow-hidden rounded-lg border border-border bg-app">
      {/* 动作摆在固定的头部,不压在预览的渐变上 —— 那里的位置随内容长度浮动。 */}
      <div className="flex items-center gap-1 border-b border-border px-2 py-1">
        <FileText aria-hidden size={13} className="shrink-0 text-accent-soft" />
        <span className="mr-auto truncate text-[11px] text-fg-muted">{planFileName(interaction.path)}</span>
        <ActionIconButton
          testId="plan-copy"
          disabled={disabled}
          label={t(copied === 'failed' ? 'agent.interaction.copyFailed'
            : copied === 'done' ? 'agent.interaction.planCopied' : 'agent.interaction.copyPlan')}
          onClick={() => { void copyText(plan).then(() => setCopied('done')).catch(() => setCopied('failed')) }}
        >
          {copied === 'done' ? <Check size={13} /> : <Copy size={13} />}
        </ActionIconButton>
        <ActionIconButton
          testId="plan-export"
          disabled={disabled}
          label={t(exported === 'failed' ? 'agent.interaction.exportFailed'
            : exported === 'done' ? 'agent.interaction.planExported' : 'agent.interaction.exportPlan')}
          onClick={() => {
            void saveTextFile(planFileName(interaction.path), plan)
              // null = 用户在系统对话框里取消了。那不是失败,不该给成功反馈。
              .then((saved) => { if (saved !== null) setExported('done') })
              .catch(() => setExported('failed'))
          }}
        >
          {exported === 'done' ? <Check size={13} /> : <Download size={13} />}
        </ActionIconButton>
        {onOpenPlan !== undefined && <ActionIconButton
          testId="plan-open"
          disabled={disabled}
          label={t('agent.interaction.openFullPlan')}
          onClick={() => onOpenPlan(interaction.path)}
        >
          <ArrowUpRight size={13} />
        </ActionIconButton>}
      </div>
      <button type="button" disabled={disabled} onClick={() => onOpenPlan?.(interaction.path)}
        aria-label={t('agent.interaction.openPlan')}
        className="relative block h-[220px] w-full overflow-hidden p-3 text-left">
        <AgentMarkdown content={plan} />
        <span aria-hidden="true" className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-gradient-to-t from-app to-transparent" />
      </button>
    </div>
    <ActionRows
      rows={rows}
      disabled={disabled}
      ariaLabel={t('agent.interaction.plan')}
      onRun={(value) => {
        if (value === 'approve_current' || value === 'approve_new_session' || value === 'request_revision') {
          void decide(value)
        }
      }}
      renderExpanded={(value, collapse) => value !== 'request_revision' ? null : (
        <>
          <textarea autoFocus aria-label={t('agent.interaction.feedback')} placeholder={t('agent.interaction.feedback')}
            value={feedback} onChange={(event) => setFeedback(event.target.value)} rows={3} maxLength={32768}
            disabled={disabled} className={FIELD} />
          <div className="mt-1.5 flex items-center justify-end gap-2">
            <button type="button" disabled={disabled} className={GHOST_BUTTON} onClick={collapse}>
              {t('agent.interaction.cancelRevision')}
            </button>
            {/* ★ 没写字时**不给**这颗键,而不是给一颗灰的 —— 灰键只说明「不行」,
                说不出「还差什么」,而这里差的东西就在上面那个空框里。 */}
            {feedback.trim() !== '' && <button type="button" disabled={disabled} className={PRIMARY_BUTTON}
              onClick={() => { void decide('request_revision') }}>
              {t('agent.interaction.sendRevision')}
            </button>}
          </div>
        </>
      )}
    />
  </CardShell>
}

/**
 * 工作流开跑前的确认。对话里那张画布已经把图画出来了(入参推出来的全 `pending` 结构),
 * 这里只按层列一份清单 —— 再画一张画布,同一屏就有两张一模一样的图。
 *
 * ★ 按层列而不是按入参顺序:用户要确认的恰恰是「谁和谁同时跑、谁等谁」,
 *   截图里那种「汇总节点和被汇总的排在同一层」一眼就能看出来。
 */
function WorkflowApprovalCard({ interaction, onAnswered }: {
  interaction: Extract<PendingInteraction, { kind: 'workflow_approval' }>
  onAnswered: () => void
}): ReactNode {
  const { t } = useI18n()
  const { busy, errorKey, respond } = useRespond(onAnswered)
  const [feedback, setFeedback] = useState('')
  const { workflow } = interaction
  const byId = new Map(workflow.nodes.map((node) => [node.id, node]))
  const layers = workflowLayers(workflow.nodes.map((node) => ({ id: node.id, depends_on: node.dependsOn })))
    ?? [workflow.nodes.map((node) => node.id)]
  const decide = (action: 'run' | 'revise' | 'cancel'): void => {
    if (busy) return
    respond({ id: interaction.id, kind: 'workflow_approval', action,
      ...(action === 'revise' && feedback.trim() !== '' ? { feedback: feedback.trim() } : {}) })
  }
  return <CardShell
    kind="workflow_approval"
    title={t('workflow.approval.title', { title: workflow.title })}
    errorKey={errorKey}
    busy={busy}
    footer={<>
      <KeyboardHint show={!busy} />
      <button type="button" disabled={busy} className={`${GHOST_BUTTON} ml-auto`} onClick={() => decide('cancel')}>
        {t('workflow.approval.cancel')}
      </button>
    </>}
    onSubmit={(event) => { event.preventDefault() }}
  >
    <p className="mb-2 text-[12px] text-fg-muted">{t('workflow.approval.body', { count: workflow.nodes.length })}</p>
    <ol data-testid="workflow-approval-layers" className="mb-2 space-y-1.5">
      {layers.map((layer, index) => (
        <li key={layer.join('|')} className="flex gap-2 text-[12px]">
          <span className="w-12 shrink-0 tabular-nums text-fg-faint">{t('workflow.approval.step', { n: index + 1 })}</span>
          <span className="flex min-w-0 flex-wrap gap-1">
            {layer.map((id) => {
              const node = byId.get(id)!
              return <span key={id} title={node.model === undefined ? node.agent : `${node.agent} · ${node.model}`}
                className="rounded-[6px] border border-stroke bg-app px-1.5 py-0.5 text-fg">
                {node.title}
                {node.mapOver !== undefined && <span className="ml-1 text-fg-faint">· {t('workflow.node.each')}</span>}
              </span>
            })}
          </span>
        </li>
      ))}
    </ol>
    <ActionRows
      disabled={busy}
      ariaLabel={t('workflow.approval.title', { title: workflow.title })}
      rows={[
        { value: 'run', label: t('workflow.approval.run') },
        { value: 'revise', label: t('workflow.approval.revise'), icon: <Pencil aria-hidden size={13} className="text-fg-faint" />, expands: true }
      ]}
      onRun={(value) => { if (value === 'run') decide('run') }}
      renderExpanded={(value, collapse) => value !== 'revise' ? null : (
        <>
          <textarea autoFocus aria-label={t('workflow.approval.feedback')} placeholder={t('workflow.approval.feedback')}
            value={feedback} onChange={(event) => setFeedback(event.target.value)} rows={3} maxLength={32000}
            disabled={busy} className={FIELD} />
          <div className="mt-1.5 flex items-center justify-end gap-2">
            <button type="button" disabled={busy} className={GHOST_BUTTON} onClick={collapse}>
              {t('agent.interaction.cancelRevision')}
            </button>
            {feedback.trim() !== '' && <button type="button" disabled={busy} className={PRIMARY_BUTTON}
              onClick={() => decide('revise')}>
              {t('agent.interaction.sendRevision')}
            </button>}
          </div>
        </>
      )}
    />
  </CardShell>
}
