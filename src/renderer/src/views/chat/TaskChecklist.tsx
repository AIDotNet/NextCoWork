/**
 * 输入框上方的 agent 任务清单。
 *
 * 需求：把 TodoWrite 的真实状态画成紧凑的 Task Rows；这里故意不自带演示计时器，
 * 状态只能由转录数据驱动，否则失败或完成会在界面上被动画伪造。
 *
 * 「这份清单此刻还是不是活的」由调用方用 `execution` 表态(见它的注释)——
 * 组件自己**推不出来**:转录里同样是 `in_progress` 的一项,既可能正被这一轮推着走,
 * 也可能是上一轮留下的死账。推不出来的东西不许在这里猜。
 *
 * 折叠态默认收起(见 `defaultCollapsed`),而输入框上方那条**收起就是一颗小球**
 * (见 `minimizeToBall`):它占了正文上方一整块,默认展开会在每次 TodoWrite 落地时
 * 把转录往下顶一屏;而收起来只留一行标题行等于没让出这一行 —— 那一行报的数小球上也报。
 * 调用方要默认展开时显式表态(原先工具卡片详情就是这么做的;那个位置现在换成了
 * `TodoWriteChecklist`,它自带一套「增量 + 完整清单」的标题行)。
 *
 * ★ **一张清单组件,两种标题行。** 输入框上方那条画进度环 + 计数 + 百分比;消息里那张
 * TodoWrite 卡片画「这次更新改了什么」+ 完整清单(见 `TodoWriteChecklist.tsx`)。
 * 列表本体(行怎么画、收起时怎么隐藏)只在这里实现一次 —— 两份长得一样的清单
 * 在同一屏里上下相邻,改一处漏一处一眼就能看出来。
 */
import { Check, ChevronDown } from 'lucide-react'
import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import { Spinner } from '../../components/ui/Spinner'
import { Tooltip } from '../../components/arc/tooltip/tooltip'
import { cn } from '../../lib/cn'
import { motionScale, useMotionLevel } from '../../theme/useMotionLevel'

/**
 * 收起时卡片留在原地淡出的时长,小球同时入场。
 *
 * ★ 与 `theme.css` 里 `checklist-card-exit` 的 150ms 是**一对**:Shell 只负责在这么多
 * 毫秒之后把卡片从 DOM 里摘掉(没有 `animationend` 可用 —— reduced/off 档下那段动画是
 * `animation: none`,事件永远不来)。改一边就得改另一边。
 */
export const CARD_LEAVE_MS = 150

/**
 * 清单里的一项。导出是因为**工具卡片的详情区也渲染这张清单**
 * (见 `todo-preview.ts`):两处共用一个类型,字段变了两边一起编译期报错。
 */
export type TaskChecklistItem = {
  content: string
  activeForm: string
  status: 'pending' | 'in_progress' | 'completed'
}

/**
 * 这份清单和「正在跑的那一轮」的关系。**它只决定展示,永远不改写任务本身** ——
 * item 的 `status` 与「已完成 N/M」这两个数照抄 TodoWrite,任何一档都不重算。
 *
 * - `running`:本轮真的写成功过清单,它还在被推进 —— 允许显示 activeForm 与 Spinner。
 * - `stopped`:清单所属的本轮已经收尾 —— 未完的项按「未完成」陈列,并报出剩余条数。
 * - `snapshot`:这份清单只是转录里的历史快照(工具卡片的入参预览、上一轮留下的),
 *   不代表现在还有谁在跑它。
 */
export type TaskChecklistExecution = 'running' | 'stopped' | 'snapshot'

/**
 * 折叠壳:标题行(由调用方画)+ 可收起的列表。**折叠语义只在这里实现一次** ——
 * `aria-expanded` / `aria-controls` / `inert` 三件套漏掉任何一件,屏读器都会读到
 * 一份「已经收起来却仍然可聚焦」的清单,而屏幕上看不出任何异常。
 *
 * ★ **给了 `ball` 的调用方没有「只收列表」这一档**:收起就是那颗球,再点球就是全部展开。
 * 原先收起后先留一行标题行、过 15 秒没人碰才缩成球,而那一行占的空间和它报的进度
 * 小球上都报 —— 用户按收起就是不想再占这一行,所以那层中间态和守着它的计时器一起删了
 * (需求:收起直接是小球)。代价是**展开态不会被自动收走**,这一点由用户自己按。
 *
 * ★ **收 / 展各有一段过渡**(四段动画与分工写在 `theme.css` 里):收起时卡片先留在原地
 * 淡出、小球同时 pop 入场,那交叉的 150ms 才是「过渡」本身;展开时卡片淡入、列表随后
 * 铺开。两棵子树没法互相 morph —— 这是能做到的最好效果。
 *
 * 清单内容更新(TodoWrite 落地)**不会**把小球弹回来:球上画着进度环和完成数,
 * 进度照样看得见;每来一次更新就弹开,会在一次运行里反复伸缩,比不缩更吵。
 */
export function TaskChecklistShell({
  header,
  list,
  defaultCollapsed,
  ball,
  className
}: {
  /**
   * 标题行。必须自己渲染那颗开合按钮,并接上给回来的 `toggle` / `collapsed` /
   * `listId` —— `aria-controls` 指向的正是下面那个可收起容器。
   */
  header: (props: { toggle: () => void; collapsed: boolean; listId: string }) => ReactNode
  list: ReactNode
  defaultCollapsed: boolean
  /**
   * 收起时画的那颗小球(输入框上方那条传它)。
   * **缺省 = 收起只收列表、标题行留着**:消息里那张 TodoWrite 卡片是转录的一部分,
   * 缩掉它等于改写历史的版式。必须把给回来的 `restore` 接到点击上。
   */
  ball?: (restore: () => void) => ReactNode
  className?: string
}): ReactNode {
  const listId = useId()
  const [collapsed, setCollapsed] = useState(defaultCollapsed)
  const cardRef = useRef<HTMLDivElement>(null)
  const restoredRef = useRef(false)
  const leaveMs = CARD_LEAVE_MS * motionScale(useMotionLevel())
  /*
    需求:收起是**两棵子树的交接**(卡片 → 小球),卡片当场卸载等于被凭空抽走。
    所以它先留在 DOM 里淡出 `CARD_LEAVE_MS`,这期间小球已经就位 —— 交叉的那一下
    就是「过渡」本身。
    ★ `reduced` / `off` 档(`leaveMs === 0`)不进这个状态:那两档下
      `checklist-card-exit` 是 `animation: none`,留下的是一个静止不动的卡片残影,
      比直接换掉更像卡住。
  */
  const [leaving, setLeaving] = useState(false)
  useEffect(() => {
    if (!leaving) return
    const timer = window.setTimeout(() => setLeaving(false), leaveMs)
    return () => window.clearTimeout(timer)
  }, [leaving, leaveMs])
  /*
    需求:点小球恢复后焦点落回标题行那颗开合按钮。小球在恢复的同一次提交里被卸载,
    不接住的话焦点掉回 body —— 键盘用户按 Enter 恢复后得从页首重新 Tab 一遍。
    只在「刚从小球恢复」时做:首次挂载就抢焦点会打断用户正在输入框里打的字。
  */
  useEffect(() => {
    if (collapsed || !restoredRef.current) return
    restoredRef.current = false
    cardRef.current?.querySelector<HTMLElement>(`[aria-controls="${listId}"]`)?.focus()
  }, [collapsed, listId])
  /*
    需求:点小球是「我现在要看这份清单」,所以一次点击直接把列表完全展开 ——
    原先只恢复成收起的标题行,用户必须再点一次才看得到内容,而那一行本身就是
    他刚才没在看、才被收走的东西。
    ★ 展开后**没有任何计时器会把它收回去**:`collapsed` 只由用户那两处点击改,
    正在读清单的人不会被从眼皮底下抽走列表。
  */
  const restoreFromBall = useCallback(() => {
    restoredRef.current = true
    setLeaving(false)
    setCollapsed(false)
  }, [])
  /*
    标题行那颗开合按钮的出口。收起这一下要多做一件事:让卡片进入「正在离开」,
    这样它在被摘掉之前还有 `CARD_LEAVE_MS` 可以淡出(见上面 `leaving` 那段)。
    没有小球可接的调用方永不进这个状态 —— 它的收起是自己那棵树里的一段过渡。
  */
  const toggle = useCallback(() => {
    if (collapsed) {
      setLeaving(false)
      setCollapsed(false)
      return
    }
    setCollapsed(true)
    if (ball !== undefined && leaveMs > 0) setLeaving(true)
  }, [collapsed, ball, leaveMs])
  const outerClassName = cn('mx-auto w-full max-w-[760px] px-6 pb-2', className)
  /*
    ★ 「正在离开」的那张卡片上,`collapsed` 必须**当成没收起**:它要整张一起淡出;
    列表这时候折起来的话,用户看到的是标题行单独缩一下、卡片再消失。
    `inert` / `aria-hidden` 挂在整张卡片上,所以这 150ms 里它照样点不动、也念不出来。
  */
  const listCollapsed = collapsed && !leaving
  const card = (exiting: boolean): ReactNode => (
    <div
      ref={cardRef}
      inert={exiting}
      aria-hidden={exiting}
      className={cn(
        'overflow-hidden rounded-card border border-stroke bg-surface-raised/70',
        exiting ? 'checklist-card-exit' : 'checklist-card-enter',
        /*
          需求:收/展这两段都要看得出「卡片是从左下角那颗球里长出来 / 收回那颗球里去」。
          缩放的默认原点是卡片正中,于是它朝正中收 —— 球却在左下角,两个方向对不上,
          看起来像卡片先自己缩一下、球再另外冒出来。
          只有给了 `ball` 的调用方才有这个锚点;没有球的那份(消息里的 TodoWrite 卡片)
          保持默认原点,不跟着改版式。
        */
        ball !== undefined && 'origin-bottom-left'
      )}
    >
      {header({ toggle, collapsed, listId })}
      <div
        id={listId}
        aria-hidden={listCollapsed}
        inert={listCollapsed}
        className={cn(
          'grid transition-[grid-template-rows,opacity] duration-200 ease-out motion-reduce:transition-none',
          listCollapsed ? 'grid-rows-[0fr] opacity-0' : 'grid-rows-[1fr] opacity-100',
          // 展开方向要错开一档(分工写在 theme.css):列表等卡片先淡入到一半再铺开
          !listCollapsed && !exiting && 'checklist-list-enter'
        )}
      >
        <div className="min-h-0 overflow-hidden">{list}</div>
      </div>
    </div>
  )

  /*
    ★ 缩成小球时只换掉卡片这一层 DOM,Shell 自己不卸载:`collapsed` 留在这里,
    恢复后不用重建组件就能决定展开还是收起(点球即完全展开,见 `restoreFromBall`)。
    ★ `w-fit` 是必须的:外层默认 `w-full`(那条清单本来自己占一行),球撑满它那一格
    会把队列挤到下一行去。`mx-0` 同理压掉外层的 `mx-auto`:那一格哪怕比球宽,球也必须
    贴着左边 —— 居中的话它会在收起的这 150ms 里飘在半空,然后跳回左边。
    ★ **收起的那 `CARD_LEAVE_MS` 里两棵子树同时在 DOM 里**(球已就位、卡片正在淡出),
    而球必须落在**卡片的左下角**:整块版式贴着输入框往上长,所以左下角是收/展两个方向
    共同的锚点。为此这 150ms 里球和卡片占**同一格**(都在第 2 行,球 `self-end` 压在
    左下角),动画结束后球回到 (1,1) —— 那一格的底边就是刚才那条底边,所以球不会跳。
    原先球一直待在 (1,1)、卡片单独落到第 2 行,表现为球出现在正在淡出的卡片**左上角**,
    方向正好反了。
    非 grid 容器(消息里那张卡片)里这些定位类都是空操作。
  */
  if (ball !== undefined && collapsed) {
    return (
      <>
        <div
          className={cn(
            outerClassName,
            'col-start-1 mx-0 w-fit self-end',
            leaving ? 'row-start-2' : 'row-start-1'
          )}
        >
          {ball(restoreFromBall)}
        </div>
        {leaving && <div className={cn(outerClassName, 'col-span-full row-start-2')}>{card(true)}</div>}
      </>
    )
  }

  return <div className={cn(outerClassName, 'col-span-full')}>{card(false)}</div>
}

/**
 * 清单行 —— 两份清单共用的那一段。
 *
 * `isActive` 说的是**此刻有没有人在推进这份清单**(不是转录里某一项的 status):
 * 见 `TaskChecklist` 里 `execution` 的说明。`highlight` 是「这一项在本次更新里变了」
 * (消息里那张卡片用),它只加一层极浅的底色,不改文字也不改状态。
 */
export function TaskChecklistRows({
  todos,
  isActive,
  highlight
}: {
  todos: readonly TaskChecklistItem[]
  isActive: boolean
  /** 内容 → 是否标为「本次更新动了它」。缺省 = 一个都不标。 */
  highlight?: ReadonlySet<string>
}): ReactNode {
  const { t } = useI18n()
  return (
    <ul className="scroll-thin max-h-40 overflow-y-auto border-t border-hairline px-1 py-1">
      {todos.map((item, index) => {
        /*
          需求:一行到底显示 `activeForm`(「正在更新 UI」)还是 `content`(「更新 UI」),
          取决于**此刻有没有人在做这一项**,而不是转录里它的 status 是什么。
          快照/已收尾时显示 activeForm 等于把过去式说成现在进行时。
        */
        const showActive = isActive && item.status === 'in_progress'
        const label = showActive ? item.activeForm : item.content
        // sr-only 里的状态词同理:没人在做的那一项,屏读器不能听到「任务正在执行」
        const statusLabel = item.status === 'completed'
          ? t('chat.status.done')
          : showActive
            ? t('chat.taskChecklistRunning')
            : item.status === 'in_progress'
              ? t('chat.taskChecklist.unfinished')
              : t('chat.tool.waitingStatus')
        return (
          <li
            key={`${String(index)}:${item.content}`}
            // 始终是转录里的原值:档位只改怎么画,不改这一项究竟是什么状态
            data-task-status={item.status}
            className={cn(
              'flex min-h-8 items-center gap-2 rounded-[8px] px-1.5 py-1 text-[12px]',
              showActive && 'bg-accent/[0.045]',
              highlight?.has(item.content) === true && 'bg-tint-hover/50'
            )}
          >
            <span className="relative flex size-5 shrink-0 items-center justify-center" aria-hidden>
              {item.status === 'completed' ? (
                <span className="flex size-5 items-center justify-center rounded-pill bg-accent text-accent-fg">
                  <Check size={11} strokeWidth={3} />
                </span>
              ) : showActive ? (
                // Spinner 负责应用内“减弱/关闭动效”两档，不能退回裸 CSS 旋转。
                <Spinner size="sm" className="text-accent" />
              ) : (
                <span className="flex size-5 items-center justify-center rounded-pill border border-stroke text-[9px] tabular-nums text-fg-faint">
                  {index + 1}
                </span>
              )}
            </span>
            <span className={cn(
              'min-w-0 flex-1 break-words leading-relaxed',
              item.status === 'completed' ? 'text-fg-faint' : item.status === 'in_progress' ? 'text-fg' : 'text-fg-muted'
            )}>
              {label}
            </span>
            <span className="sr-only">{statusLabel}</span>
          </li>
        )
      })}
    </ul>
  )
}

export function TaskChecklist({
  todos,
  execution = 'snapshot',
  defaultCollapsed = true,
  minimizeToBall = true,
  className
}: {
  /** TodoWrite 的当前完整快照；顺序由 agent 决定，组件只负责展示。 */
  todos: readonly TaskChecklistItem[]
  /**
   * 这份清单此刻还算不算「正在跑」。
   *
   * 需求:同一份转录数据会出现在两个位置 —— 输入框上方那条要看得出现在推进到哪,
   * 而工具卡片详情里那份是历史/入参快照(见 `todo-preview.ts`),它既不该转圈,
   * 也不该说「现在正在做」。
   * ★ 缺省是 `snapshot`,即**保守的那一侧**:调用方不表态时宁可不显示进度,
   *   也不能伪造一个「还在跑」的界面 —— 后者会让人一直等一个不会来的结果。
   */
  execution?: TaskChecklistExecution
  /**
   * 首次挂载时是否收着列表。
   *
   * 需求:同一个组件出现在两处,而两处对「默认展开」的答案不同 —— 输入框上方那条
   * 占了正文上方一整块,默认展开会在每次 TodoWrite 落地时把转录往下顶一屏;
   * 而用户主动展开卡片来读的那个位置希望直接看到内容。
   * 默认值取收起的一侧,新增调用方不用为「输入框上方」这个最常见的位置再表态一次。
   *
   * ★ 这是**挂载时的初值**,不是受控 prop:挂载之后归 `collapsed` 自己,否则
   *   ChatView 每收到一条流式事件重渲一次,用户刚点开的列表就会被按回默认态。
   *   同理,输入框上方那条要**跨 TodoWrite 更新**记住用户的展开意图 ——
   *   那一处的调用方不给 `key`,组件实例因此不会被重建(见 ChatView)。
   */
  defaultCollapsed?: boolean
  /**
   * 收起 = 一颗进度球(而不是留一行标题行),点击小球直接展开完整清单。
   *
   * 需求:收起的那一行仍夹在正文与输入框之间,而它报的数小球上也报 —— 用户按收起
   * 就是不想再占这一行,所以收起这一步直接缩成球,没有中间态。
   * 默认开,理由同 `defaultCollapsed`:缺省值服务「输入框上方」这个最常见的位置;
   * 嵌进别处(卡片详情之类)的调用方传 `false` —— 那份清单是转录的一部分,
   * 缩成球等于改写历史的版式。
   *
   * ★ 与 `defaultCollapsed` 的缺省组合起来 = **挂载时就直接是那颗球**。
   *   想要一上来就铺开清单的调用方要显式传 `defaultCollapsed={false}`。
   */
  minimizeToBall?: boolean
  /**
   * 外层定位的覆盖位。默认那套(居中、760 上限、左右留白)是**输入框上方**那个
   * 位置的需求;嵌在工具卡片详情里、或与发送队列共用一行时(见 ChatView 的
   * `composer-notices`),由调用方传 `max-w-none px-0 pb-0` 抹掉 ——
   * 卡片本体不因此分叉出第二套版式。
   */
  className?: string
}): ReactNode {
  const { t } = useI18n()
  const done = todos.filter((item) => item.status === 'completed').length
  const active = todos.find((item) => item.status === 'in_progress')
  const progress = todos.length === 0 ? 0 : done / todos.length
  /*
    只有 `running` 才谈得上「正在做」。其余两档里 `in_progress` 是**转录留下的一个
    事实**——那一项当年确实开着,但此刻没有人在推进它,所以不转圈、不高亮、不报
    「任务正在执行」,而是按「未完成」陈列。
  */
  const isActive = execution === 'running'
  /*
    需求:小球上放不下第二行那句「仍有 N 项未完成」,但这句不能因为缩起来就丢 ——
    否则停在半截的进度环看起来像还在跑(理由见标题行那段)。所以球角挂一个警示点,
    完整句子进无障碍标签和 title。判据与标题行那段同一条。
  */
  const unfinishedAfterStop = execution === 'stopped' && todos.length - done > 0
  const ballLabel = [
    t('chat.taskChecklist.restore', { done, total: todos.length }),
    ...(unfinishedAfterStop ? [t('chat.taskChecklist.stoppedIncomplete', { count: todos.length - done })] : [])
  ].join(' · ')
  /*
    需求:鼠标停在那颗球上要能看到**此刻正在做哪一项** —— 收起之后标题行那句
    activeForm 就没地方显示了,而球上只有一个完成数,看不出进度停在哪。
    判据与标题行第二行**完全同一条**(`isActive && active !== undefined`):没有人在
    推进这份清单时不显示,否则悬停会把上一轮留下的死账说成「正在做」。
    没有可说的就交 `undefined`:`Tooltip` 收到 undefined 时退化成纯包裹层、不挂任何
    监听(见它的 `content` 注释),所以「不显示」不是画一个空浮层。
  */
  const activeHint = isActive && active !== undefined ? active.activeForm : undefined

  return (
    <div
      /*
        ★ `contents`:这一层只是给测试认的标记,**不能自己成为一个盒子**。
        Shell 在收起那 150ms 里返回的是两个各自带 `col-start-1` + `self-end`(球)与
        `col-span-full`(卡片)的格子(见 `TaskChecklistShell` 与 ChatView 的
        `composer-notices`)—— 中间夹一层普通 div,这两组定位类就全成了空操作:
        表现为收起时小球被 `mx-auto` 按正在淡出的卡片宽度**居中**在半空中,
        150ms 后才「跳」回队列左边那一格;展开时则是那一列被卡片撑开、把队列挤没。
      */
      className="contents"
      // 机器可读的档位:e2e/测试读它,而不是去正则第二行那句会改的文案
      data-testid="task-checklist"
      data-execution-state={execution}
    >
      <TaskChecklistShell
        defaultCollapsed={defaultCollapsed}
        className={className}
        {...(minimizeToBall ? {
          ball: (restore: () => void) => {
            const ball = (
              <button
                type="button"
                data-testid="task-checklist-ball"
                aria-label={ballLabel}
                /*
                  ★ 有富浮层可显示时**不再挂原生 `title`**:两者都由悬停触发,同时挂会
                  在球边上先弹出浮层、一秒后再叠一个系统小黄条,两份说的还是同一件事。
                  没有正在做的那一项时浮层不存在,`title` 就是唯一的悬停说明,必须留着。
                */
                {...(activeHint === undefined ? { title: ballLabel } : {})}
                onClick={restore}
                className={cn(
                  'relative flex size-9 items-center justify-center rounded-pill border border-stroke bg-surface-raised/70',
                  /*
                    ★ 入场动画走 `theme.css` 里的 `checklist-ball-enter`,**不写成 `starting:`**。
                    两个理由:一是小球这一档是**画**出来的(收起来的球=一颗缩小的进度环),
                    `starting:scale-*` 只是 `scale` 的起始值、没有配对的状态变化,浏览器不一定
                    重放它;二是 `cn` 里同时出现 `scale-*` 与 `transition-*` 时,后者是
                    `transition-property` 这一绘图属性、会按 twMerge 的冲突表吃掉前者。
                    ★ 这里的 transition 只剩 hover 底色,和上面那几段各管各的。
                  */
                  'checklist-ball-enter transition-colors duration-150 ease-out hover:bg-tint-hover',
                  'motion-reduce:transition-none'
                )}
              >
                <ProgressRing progress={progress} done={done} />
                {unfinishedAfterStop && (
                  <span aria-hidden className="absolute top-0 right-0 size-2 rounded-pill bg-warning" />
                )}
              </button>
            )
            /*
              Arc 的 Tooltip 是 asChild:没有包裹层,球直接当触发器(以前那层 span 要靠
              `flex` 才不垫出基线空隙,现在没有这个问题)。没有正在做的那一项时不挂提示,
              球上的原生 `title` 就是唯一的悬停说明。
              ★ 提示底色是反相的(深底浅字),里面不能写 `text-fg` 这类颜色类。
            */
            return activeHint === undefined ? ball : (
              <Tooltip
                content={
                  <span className="block">
                    <span className="block">{activeHint}</span>
                    <span className="mt-0.5 block opacity-70">
                      {t('chat.taskChecklist', { done, total: todos.length })}
                    </span>
                  </span>
                }
              >
                {ball}
              </Tooltip>
            )
          }
        } : {})}
        list={<TaskChecklistRows todos={todos} isActive={isActive} />}
        header={({ toggle, collapsed, listId }) => (
          <button
            type="button"
            aria-expanded={!collapsed}
            aria-controls={listId}
            className="flex h-11 w-full min-w-0 items-center gap-2.5 px-2.5 text-left"
            onClick={toggle}
          >
            <ProgressRing progress={progress} done={done} />

            <span className="min-w-0 flex-1">
              <span className="block truncate text-[12.5px] font-medium text-fg">
                {t('chat.taskChecklist', { done, total: todos.length })}
              </span>
              {isActive && active !== undefined && (
                <span className="mt-0.5 block truncate text-[11px] text-fg-muted">{active.activeForm}</span>
              )}
              {/*
                需求:这一轮已经收尾、清单却没跑完,必须说出来 —— 否则第二行空着、进度环
                停在一半,面板看起来仍然是「在跑」,用户会去等一个已经不会发生的结果。
                ★ `todos.length - done > 0` 才显示:全部完成是正常收尾,不是异常,那时候
                  再报一句警告只会让人以为出了事。
              */}
              {execution === 'stopped' && todos.length - done > 0 && (
                <span className="mt-0.5 block truncate text-[11px] text-warning">
                  {t('chat.taskChecklist.stoppedIncomplete', { count: todos.length - done })}
                </span>
              )}
              {/*
                需求:快照档要**明说自己不是实时进度**,否则历史清单和输入框上方那条
                长得一模一样,用户分不清哪一份还有人在推。
              */}
              {execution === 'snapshot' && (
                <span className="mt-0.5 block truncate text-[11px] text-fg-faint">
                  {t('chat.taskChecklist.snapshot')}
                </span>
              )}
            </span>

            <span aria-hidden className="shrink-0 text-[10.5px] tabular-nums text-fg-faint">{Math.round(progress * 100)}%</span>
            <ChevronDown
              aria-hidden
              size={14}
              className={cn(
                'shrink-0 text-fg-faint transition-transform duration-200 motion-reduce:transition-none',
                collapsed && '-rotate-90'
              )}
            />
          </button>
        )}
      />
    </div>
  )
}

const RING_CIRCUMFERENCE = 2 * Math.PI * 9

/**
 * 进度环 + 完成数。标题行和缩起后的小球画的是**同一颗**:小球是这一行缩出来的,
 * 两处长得不一样,用户就认不出它们是同一个东西。
 */
export function ProgressRing({ progress, done }: { progress: number; done: number }): ReactNode {
  return (
    <span aria-hidden className="relative flex size-6 shrink-0 items-center justify-center">
      <svg className="absolute inset-0 -rotate-90" width="24" height="24" viewBox="0 0 24 24" aria-hidden>
        <circle cx="12" cy="12" r="9" fill="none" stroke="var(--color-stroke)" strokeWidth="2" />
        <circle
          cx="12"
          cy="12"
          r="9"
          fill="none"
          stroke="var(--color-accent)"
          strokeWidth="2"
          strokeLinecap="round"
          strokeDasharray={RING_CIRCUMFERENCE}
          strokeDashoffset={RING_CIRCUMFERENCE * (1 - progress)}
          className="transition-[stroke-dashoffset] duration-500 motion-reduce:transition-none"
        />
      </svg>
      <span className="relative text-[9px] font-semibold tabular-nums text-fg">{done}</span>
    </span>
  )
}
