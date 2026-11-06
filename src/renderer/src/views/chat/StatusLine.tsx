/**
 * 本轮助手回复下方的执行状态、用量与上下文压力条。
 *
 * ★ **这里同时是 e2e 探针的读取点。** 属性是机器可读的
 * (`data-status` / `data-seq` / `data-model`),不是给人看的那句中文 ——
 * 探针去正则一句会随文案改动的话,产品界面就得永远背着一个调试字符串,
 * 而且改文案会莫名其妙挂掉 e2e。
 *
 * 两样数据都是已有事件的直接投影(方案 §8):`message_start.model`、`context_usage`。
 * 不新增任何数据。**例外是压力条的分母**:它跟圆环一样走本地权威(`contextLimits`),
 * 否则用户开「最大上下文」之后,这一行会在下一次发送之前一直按旧窗口催他压缩。
 * 分子仍然只来自事件 —— 见 `context-pressure.ts`。
 *
 * ★ **流式过程中不报 token 读数。** 它只在每次请求结束时跳一下,中间一直定在
 * 一个旧数上 —— 看着像卡住;而真要看用量,回复下方的「任务用量」给的是整轮的
 * 完整口径(含缓存与花费),比这里这个半截的累计值准。
 */
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { TranscriptState } from '../../../../shared/agent/transcript'
import type { ActiveGoal } from '../../../../shared/domain/goal'
import { agentErrorText } from '../../i18n/agent'
import { hasRun } from '../../../../shared/agent/transcript'
import { isToolResultOnly } from '../../../../shared/agent/message'
import type { ContextStatusPhase } from '../../../../shared/agent/context-management'
import { compactBoundaryOf, lastCompactBoundaryIndex } from '../../../../shared/agent/compaction'
import { activitySnapshotOf, whimsyBucketOf, type ActivitySnapshot } from '../../../../shared/domain/activity'
import { contextPressure, type CurrentContextLimits } from './context-pressure'
import { cn } from '../../lib/cn'
import { useI18n } from '../../i18n'
import { whimsyEn, whimsyZh } from '../../i18n/whimsy'
import type { Locale } from '../../i18n'
import { Spinner } from '../../components/ui/Spinner'
import { AgentActivityGrid, AgentShimmerText } from './AgentActivity'

/**
 * 4 秒:比读完一个词慢得多,又比「一直不动」快得多。
 * 再快就成了跑马灯,眼睛会一直被它拽过去 —— 而这里全部的信息量就是「还活着」。
 */
const WHIMSY_ROTATE_MS = 4000

/**
 * 压力条**平时根本不画**,过半才出现,逼近上限才变色。
 *
 * ★ 原本是「一直画着、只是不变色」,截图里那就是状态行最右边一根**孤零零的短横线** ——
 * 没有标签、没有数字、填充几乎为零,看着完全像个渲染残留,而不像一个刻意的读数。
 * 一个 3px 高、无标注的条只有在**读数本身值得看**的时候才传递信息;
 * 用了 4% 的上下文不值得看。
 */
const PRESSURE_SHOW = 0.5
const PRESSURE_WARN = 0.75

/**
 * 「已压缩」显示多久。
 *
 * 它说的是**刚刚发生过**的一件事,不是一个持续状态 —— 一直挂着的话,
 * 十轮之后那句话说的还是第一轮那次压缩,而用户会以为它指的是这一轮。
 */
const COMPACTED_SHOW_MS = 8000

export function StatusLine({
  transcript,
  running,
  waitingForResponse,
  lastSeq,
  queued,
  compactError,
  goal,
  contextLimits
}: {
  transcript: TranscriptState
  running: boolean
  waitingForResponse: boolean
  lastSeq: number
  queued: number
  /** 手动压缩(`/compact` 或双击圆环)的失败原因。自动压缩走 `contextStatus`。 */
  compactError?: string | null
  goal?: ActiveGoal
  /**
   * **此刻**药丸说了算的有效窗口与最大输出 —— 和上下文圆环同源。
   *
   * 需求:用户中途打开「最大上下文」之后,这一行的分母和那句「接近上限」要当帧
   * 跟着变,而不是等到下一次发送。为什么这里不能只靠 `transcript.contextUsage`、
   * 以及本地重判为什么不会比主进程宽松,见 `context-pressure.ts` 的文件头。
   * 只读面板(子代理)查不到药丸,不传 —— 那时退回主进程给的结论。
   */
  contextLimits?: CurrentContextLimits
}): ReactNode {
  const { t, locale } = useI18n()
  const { status, model, contextUsage, notice, contextStatus } = transcript
  // ★ 在 early return 之前调用 —— hooks 不能出现在条件分支后面。
  const whimsy = useWhimsy(running, activitySnapshotOf(transcript, waitingForResponse), locale)
  const showCompacted = useFading(contextStatus?.phase === 'compacted', contextStatus)
  // 还没发过消息的空会话没有「状态」可言 —— 参考实现在这一屏是一句问候加输入框,
  // 输入框上方什么都没有(截图 c6184031)。见 `hasRun` 说明为什么不能只看 status。
  if (!hasRun(transcript, running) && goal === undefined) return null
  /*
    回复被删光后只剩提问:没有任何一次「已完成」可言。`done` 是正常收尾的默认值,
    此时再摆一句「已完成」是在给一个不存在的回复报状态。停止 / 出错仍照常显示。
  */
  if (!running && status === 'done' && goal === undefined && transcript.live.length === 0
    && transcript.warning === undefined && endsWithUnansweredPrompt(transcript.messages)) return null

  /*
    ★ 重试 / 切换提示**压过**那句「正在等待回复…」,而不是并排再加一行:
    两者说的是同一件事的两个层次,并排会让状态行在退避期间抖动。
    ★ 只换这一句人读的文案,`data-status` 一个字节不动 —— 探针读的是属性,
    见本文件抬头那段。
  */
  const noticeText = notice === undefined
    ? undefined
    : notice.kind === 'retry'
      ? t('chat.status.retrying', { attempt: notice.attempt, reason: notice.reason })
      : t('chat.status.providerSwitched', { to: notice.to, reason: notice.reason })

  const pressure = contextPressure(contextUsage, contextLimits)
  const ratio = pressure?.ratio ?? 0

  /*
    ★ **`failed` 不是熔断。** 它说的是「这一轮按原历史发出去了」,下一轮判据仍为真时
    还会再压一次;而 `disabled` 是连续失败到上限后**不再自动重试**。
    两者必须是不同档位 —— 合成一句的话,用户要么把一次可恢复的失败当成死局,
    要么把死局当成又一次可以忽略的抖动。
  */
  const compaction = compactionLine({
    t, phase: contextStatus?.phase, showCompacted, compactError,
    saved: savedTokens(transcript)
  })

  return (
    <div className="flex w-full flex-col gap-1 text-[11.5px]">
      {goal !== undefined && <p role="status" data-testid="goal-status-line"
        title={t('goal.notice.current', { condition: goal.condition, iterations: goal.iterations, reason: goal.lastReason ?? t('goal.panel.noCheckYet') })}
        className="inline-flex min-w-0 items-center gap-1.5 text-fg-muted">
        <span className="shrink-0">{t('goal.pill.label')}</span><Dot />
        <span className="truncate">{goal.condition}</span>
        {goal.lastReason && <span className="truncate text-fg-faint">{t('goal.panel.lastCheck')}: {goal.lastReason}</span>}
        {goal.deferredSince !== undefined && <span className="truncate text-fg-faint">{t('goal.panel.deferred')}</span>}
      </p>}
      {transcript.warning !== undefined && <p role="status" className="text-warning" data-testid="goal-warning">
        {agentErrorText(transcript.warning, t)}
      </p>}
    <div
      data-testid="chat-status"
      data-status={status}
      data-seq={lastSeq}
      data-model={model ?? ''}
      data-queued={queued}
      // ★ 探针读这一个属性,不去正则那句会随文案改的中文 —— 见本文件抬头。
      data-context-phase={contextStatus?.phase ?? ''}
      className="flex w-full items-center gap-2 text-[11.5px] text-fg-faint"
    >
      {/* ★ 有提示时用 danger 而不是运行中的绿:绿色说的是「一切正常」,而此刻不是。
          比最终错误那个红框轻一档(只有文字变色,没有边框和底色)—— 重试多半会自己好。
          色板里没有 warning 这一档,不为这一处新造一个 token。 */}
      <span role="status" className={cn('inline-flex items-center gap-1.5',
        noticeText !== undefined ? 'text-danger' : running && 'text-accent')}>
        {/* 趣味词是几字即逝的短词，纯微光看不出在动，所以这里保留像素波加载图标
            （见 AgentActivity 头注释）；思考块和工具状态只留文字微光 */}
        {running && <AgentActivityGrid />}
        {noticeText ?? (running && (waitingForResponse || status === 'running') ? (
          /*
            ★ 读屏拿到的是那句**不动**的「正在等待回复…」/「运行中」,轮换的词 aria-hidden。
            `role="status"` 自带 aria-live=polite:每 4 秒换一个词就是每 4 秒打断一次朗读,
            对看不见这行字的人来说,趣味词全是噪音,而它连一个字节的状态都没多说。
            ★ `running` 为假时一律走固定文案:run 已经收尾(done/error/aborted),
            此时还在转词就是在演一个已经不存在的进度。
          */
          <>
            <span className="sr-only">
              {t(waitingForResponse ? 'chat.status.waitingResponse' : 'chat.status.running')}
            </span>
            <span aria-hidden><AgentShimmerText>{whimsy}</AgentShimmerText></span>
          </>
        ) : t(`chat.status.${status}`))}
      </span>

      {queued > 0 && (
        <>
          <Dot />
          <span>{t('chat.queue', { count: queued })}</span>
        </>
      )}

      {compaction !== undefined && (
        <>
          <Dot />
          <span
            data-testid="context-compaction"
            title={compaction.detail}
            className={cn('inline-flex items-center gap-1.5',
              compaction.tone === 'danger' ? 'text-danger' : compaction.tone === 'accent' && 'text-accent')}
          >
            {compaction.spinner && <Spinner size="xs" />}
            {compaction.text}
          </span>
        </>
      )}

      <div className="flex-1" />

      {pressure !== undefined && (ratio >= PRESSURE_SHOW || pressure.nearLimit) && (
        <div
          className="flex items-center gap-1.5"
          /* 提示里写的是**此刻**的分母(可能刚被药丸改过),不是 `contextUsage.window` ——
             两个数不一致时,用户照着圆环去对的是前者。 */
          title={t('chat.contextTooltip', { used: contextUsage?.used ?? 0, window: contextLimits?.window ?? contextUsage?.window ?? 0 })}
        >
          {pressure.nearLimit && (
            // 上下文用尽是这类应用最高频的失败(方案 §4.2)。逼近上限时
            // 明说该怎么办,而不是等它 400 之后再报一个 context_length。
            <span className="text-accent">{t('chat.contextNearLimit')}</span>
          )}
          <div className="h-[3px] w-16 overflow-hidden rounded-pill bg-tint">
            <div
              className={cn(
                'h-full rounded-pill transition-[width]',
                ratio >= PRESSURE_WARN ? 'bg-accent' : 'bg-fg-faint'
              )}
              style={{ width: `${ratio * 100}%` }}
            />
          </div>
        </div>
      )}
    </div>
    </div>
  )
}

/**
 * 运行期间轮换一个词。`active` 为假时**不起定时器**,也不推进下标 ——
 * 下一轮运行会从上一轮停住的地方接着走,而不是每次都从同一个词开始。
 *
 * ★ **分组切换时不需要重置下标。** 每个分组是各自一组料,换了分组就是换了一个数组,
 * 同一个下标落在新数组上取到的**本来就是另一个词** —— 工具一开跑,那句话当帧就变,
 * 不必等下一个 4 秒,也不必为此多起一次 setState。
 *
 * ★ **「这一步跑了多久」在这里量,不在转录里算。** 转录只有工具的 `startedAt`,
 * 而等首字节、思考、写正文这三段没有任何时间戳;而且旧转录重放时那些戳还可能缺。
 * 这里按「步」计时(见 `useStepElapsed`),四种时刻用的是同一把尺。
 */
function useWhimsy(active: boolean, snapshot: ActivitySnapshot, locale: Locale): string {
  const elapsed = useStepElapsed(`${snapshot.phase}:${snapshot.callId ?? ''}`, active)
  const bucket = whimsyBucketOf(snapshot, elapsed)
  const words = locale === 'en-US' ? whimsyEn[bucket] : whimsyZh[bucket]
  // 初值随机:否则每个会话的第一句永远是同一个词,轮换就只剩下后面几秒有意思。
  const [index, setIndex] = useState(() => Math.floor(Math.random() * words.length))
  useEffect(() => {
    if (!active) return
    const timer = setInterval(() => {
      // +1 起跳保证**一定**换一个词 —— 纯随机会挑中自己,看上去就是定时器停了。
      setIndex((prev) => (prev + 1 + Math.floor(Math.random() * (words.length - 1))) % words.length)
    }, WHIMSY_ROTATE_MS)
    return () => clearInterval(timer)
  }, [active, words.length])
  // `?? words[0]` 只是为了闭合 noUncheckedIndexedAccess:取模之后下标不可能越界。
  return words[index % words.length] ?? words[0]
}

/**
 * 当前这一步开始了多久。`key` 变了就重新计时。
 *
 * ★ **不另起定时器。** 上面那个 4 秒的轮换已经在重渲这一行了,阈值(20/30 秒)
 * 比它粗一个数量级 —— 再加一个每秒 tick 的定时器,换来的只是「慢了」这句话
 * 早出现几秒,代价是整个聊天热路径上多一个常驻定时器。
 *
 * ★ 渲染期写 ref 是有意的:这里存的是「这一步是什么时候开始的」,属于渲染的派生量,
 * 放进 state 会为一个纯装饰的判定多跑一轮渲染。
 */
function useStepElapsed(key: string, active: boolean): number {
  const step = useRef({ key, at: Date.now() })
  if (step.current.key !== key) step.current = { key, at: Date.now() }
  return active ? Date.now() - step.current.at : 0
}

function Dot(): ReactNode {
  return <span aria-hidden className="text-fg-faint/50">·</span>
}

/** 最后一条可见消息是用户提问(其后没有任何助手回复)。 */
function endsWithUnansweredPrompt(messages: TranscriptState['messages']): boolean {
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    const message = messages[i]
    if (message === undefined || message.internal === true || isToolResultOnly(message)) continue
    return message.role === 'user'
  }
  return false
}

/**
 * 压缩相位 → 状态行上的一句话。纯函数,好单测。
 *
 * 手动压缩的失败**压过**自动压缩的相位:用户刚刚亲手点了一下,
 * 他要看的是那一下的结果,而不是上一轮自动压缩留下的读数。
 *
 * ★ 原先还有 `fallback` / `exhausted` 两句,描述机械折叠「折叠了 / 折叠不动」。
 * 机械压缩已随 Claude Code 式重写删除,两句一并删除;它们各自的语气理由
 * (fallback 是正常结果不能按 danger 画、exhausted 必须可行动)保留在下面两句里:
 * `failed` 是「这一轮按原历史发出去了」的事后播报,`disabled` 是熔断后唯一可行动的那句。
 */
function compactionLine({
  t, phase, showCompacted, compactError, saved
}: {
  t: ReturnType<typeof useI18n>['t']
  phase: ContextStatusPhase | undefined
  showCompacted: boolean
  compactError?: string | null
  saved?: number
}): { text: string; tone: 'danger' | 'accent' | 'muted'; spinner: boolean; detail?: string } | undefined {
  if (compactError !== undefined && compactError !== null && compactError !== '') {
    // 原始报错进 title:它常常是一整句上游错误,铺在状态行上会把这一行撑爆,
    // 但排查的时候又只有它有用。
    return { text: t('chat.contextStatus.failed'), tone: 'danger', spinner: false, detail: compactError }
  }
  switch (phase) {
    case 'compacting':
      return { text: t('chat.contextStatus.compacting'), tone: 'accent', spinner: true }
    case 'compacted':
      if (!showCompacted) return undefined
      return {
        text: saved === undefined
          ? t('chat.contextStatus.compacted')
          : t('chat.contextStatus.compactedSaved', { saved }),
        tone: 'muted', spinner: false
      }
    case 'failed':
      return { text: t('chat.contextStatus.failed'), tone: 'danger', spinner: false }
    /*
      ★ 用 danger 而不是 muted:熔断之后自动压缩**不会再试**,占用只会继续涨,
      而唯一有用的动作全在用户那边(/compact、换更大的窗口、另起会话)。
      灰掉它等于把仅剩的一条可行动提示混进事后播报里。
    */
    case 'disabled':
      return { text: t('chat.contextStatus.disabled'), tone: 'danger', spinner: false }
    default:
      return undefined
  }
}

/**
 * 这一次压缩省下了多少 token。
 *
 * ★ 读的是转录里**最后一条压缩边界**自己带的前后读数 —— 边界就在消息流里,
 * 不必再去另一张表按 `windowIndex` 找那条检查点(找不到就悄悄少一个数字)。
 * ★ 前 ≤ 后就不说:那时候报「省下 N」是编的。
 */
function savedTokens(transcript: TranscriptState): number | undefined {
  const at = lastCompactBoundaryIndex(transcript.messages)
  if (at < 0) return undefined
  const message = transcript.messages[at]
  const boundary = message === undefined ? undefined : compactBoundaryOf(message)
  if (boundary === undefined || boundary.preTokens <= boundary.postTokens) return undefined
  return boundary.preTokens - boundary.postTokens
}

/**
 * `active` 变真之后只亮一段时间。`token` 换一个新对象就重新计时 ——
 * 靠它区分「同一次压缩」和「又压了一次」,而 phase 字符串本身分不出来。
 */
function useFading(active: boolean, token: unknown): boolean {
  const [shownFor, setShownFor] = useState<unknown>(undefined)
  useEffect(() => {
    if (!active) return
    setShownFor(token)
    const timer = setTimeout(() => setShownFor(undefined), COMPACTED_SHOW_MS)
    return () => clearTimeout(timer)
  }, [active, token])
  return active && shownFor === token
}
