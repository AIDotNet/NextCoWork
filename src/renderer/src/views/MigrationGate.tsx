/**
 * 启动迁移闸门 —— 全屏那一屏。
 *
 * ## 为什么它是「闸门」而不是「进度提示」
 *
 * 迁移在 `openDatabase()` **之前**跑,而数据库是主进程所有 IPC 的底座。
 * 所以这一屏存在的这段时间里,渲染层调任何别的频道都会失败 —— 用户不是被
 * 「禁用了控件」,而是后端根本没起来。这就是「阻止用户使用」的全部实现,
 * 不需要在任何地方加一句 `disabled`。
 *
 * ★ 反过来说:**这一屏自己不能依赖任何需要数据库的东西**。除无状态的窗口控制外，
 * 它读的 `dataMigration:*` 是闸门期间唯一可用的业务频道(见 `services/data-migration.ts`)。
 * 谁哪天顺手在这里挂一个 store 的 load,那一屏就会永远停在加载中。
 *
 * ## 四个阶段各自的界面
 *
 * - `running` —— 标题 + 整体进度 + 步骤清单。★ **不可关闭**,没有取消按钮:
 *   取消一个「每个会话一个事务」的合并没有意义(已经提交的会话不会回去),
 *   而给一个做不到的按钮就是一次会失败的承诺。
 * - `failed`  —— 错误 + 三个出口(重试 / 跳过并继续 / 打开数据目录)+ 撤销。
 *   ★ 有出口这件事本身就是「减少错误」的一半:静默继续正是这次丢数据的成因,
 *   而把用户彻底挡在门外连备份都做不了,是另一个极端。
 * - `skipped` —— 一行说明 + 继续。告诉用户旧数据还在、下次还会再试。
 * - `idle`    —— 等待完整 IPC 时画首屏骨架；超时或启动失败则切到诊断页。
 *
 * ## 什么时候放行
 *
 * 「闸门里没有事要做」**不等于**「可以挂 App」:建窗在 `registerIpc()` 之前,
 * 所以还得等主进程把 handler 装完(`MigrationState.ipcReady`)。这一条判据单独住在
 * `migration-release.ts` 里,可测 —— 它错的形态(渲染层比主进程快)起一次 Electron
 * 也未必撞得上。
 */
import { useEffect, useRef, useState } from 'react'
import type { MigrationState, MigrationStepKind } from '../../../shared/domain/data-migration'
import { Button } from '../components/arc/button/button'
import { ProgressBar } from '../components/ui/ProgressBar'
import { Spinner } from '../components/ui/Spinner'
import { useI18n } from '../i18n'
import { AppSkeleton } from '../shell/AppSkeleton'
import { WindowControls } from '../shell/WindowControls'
import { decideMigrationGate } from './migration-release'
import {
  getMigrationState,
  onMigrationProgress,
  openMigrationDataDirectory,
  retryMigration,
  skipMigration,
  undoMigration
} from '../services/data-migration'

const STEP_KEYS: Record<MigrationStepKind, string> = {
  'collapse-flat-layout': 'migration.step.collapse-flat-layout',
  'merge-legacy-rows': 'migration.step.merge-legacy-rows',
  'copy-attachment-files': 'migration.step.copy-attachment-files'
}

// 需求：主进程如果既不成功也不抛错，首屏不能无限伪装成正常加载。
const STARTUP_TIMEOUT_MS = 15_000
const STARTUP_TIMEOUT_DETAIL = `STARTUP_IPC_READY_TIMEOUT (${STARTUP_TIMEOUT_MS}ms)`
export function MigrationGate({
  state,
  onResolved
}: {
  /** 当前快照。`idle` 时调用方根本不会挂这个组件。 */
  state: MigrationState
  /** 闸门放行(`idle` 或用户选了继续)时调一次,让 App 去握手。 */
  onResolved: () => void
}): React.ReactNode {
  if (state.phase === 'failed') return <MigrationFailureView state={state} />
  if (state.phase === 'skipped') return <MigrationSkippedView onContinue={onResolved} />
  return <MigrationProgressView state={state} />
}

/** 进度。**没有按钮** —— 见文件头对「不给做不到的出口」的说明。 */
function MigrationProgressView({ state }: { state: MigrationState }): React.ReactNode {
  const { t } = useI18n()
  const percent = state.ratio === null ? null : Math.round(state.ratio * 100)
  const current = state.current
  const currentKey = current === null ? null : STEP_KEYS[current.kind]
  const currentLabel = currentKey === null ? t('migration.preparing') : t(currentKey)

  return (
    <div className="flex h-full flex-col items-center justify-center gap-6 bg-app p-10">
      <div className="w-full max-w-md">
        <div className="flex items-center gap-2">
          {/* `label` 给的是标题本身 —— 这一屏只有这一个忙碌提示,它就该被念出来。 */}
          <Spinner size="sm" label={t('migration.title')} />
          <h1 className="text-[15px] font-medium text-fg">{t('migration.title')}</h1>
        </div>
        <p className="mt-2 text-[13px] text-fg-muted">{t('migration.subtitle')}</p>

        <ProgressBar
          className="mt-6"
          value={state.ratio}
          label={percent === null ? currentLabel : t('migration.overall', { percent })}
        />
        <div className="mt-2 flex items-baseline justify-between text-[12px] text-fg-muted">
          <span>{currentLabel}</span>
          {current !== null && current.total > 0 && (
            <span className="tabular-nums">
              {t('migration.step.count', { done: current.done, total: current.total })}
            </span>
          )}
        </div>

        <StepList state={state} />

        <p className="mt-6 text-[12px] text-fg-faint">{t('migration.keepOpen')}</p>
      </div>
    </div>
  )
}

/**
 * 步骤清单。
 *
 * ★ 三段状态各有一种颜色,**并且都有文字**。只靠颜色区分进度的话,红绿色盲的
 * 用户看到的是三条一模一样的灰字 —— 而这一屏的全部信息就是这三行的状态。
 */
function StepList({ state }: { state: MigrationState }): React.ReactNode {
  const { t } = useI18n()
  if (state.steps.length === 0) return null
  return (
    <ul className="mt-6 space-y-1.5">
      {state.steps.map((kind) => {
        const done = state.completed.includes(kind)
        const active = state.current?.kind === kind
        return (
          <li key={kind} className="flex items-center gap-2 text-[12px]">
            <span
              aria-hidden
              className={
                done
                  ? 'size-1.5 rounded-full bg-accent'
                  : active
                    ? 'size-1.5 rounded-full bg-accent/60'
                    : 'size-1.5 rounded-full bg-fg-faint/40'
              }
            />
            <span className={done || active ? 'text-fg' : 'text-fg-faint'}>{t(STEP_KEYS[kind])}</span>
            <span className="text-fg-faint">
              {done
                ? t('migration.step.done')
                : active
                  ? t('migration.step.running')
                  : t('migration.step.pending')}
            </span>
          </li>
        )
      })}
    </ul>
  )
}

/**
 * 失败页。
 *
 * ★ 三个出口是**并列**的,没有主次按钮的视觉暗示 —— 该选哪个完全取决于错误
 * 是什么(磁盘满要清理后重试,权限问题只能去开目录看),而这屏判断不了。
 * 给「重试」一个高亮按钮等于替用户做了这个判断。
 *
 * ★ 「跳过并继续」在 `disk-full` / `target-locked` 下**照样给**:这两种情况下
 * 跳过之后用户能进应用、能去设置里删数据腾空间。把唯一的入口堵死在这里
 * 只会让用户去手删 `~/.next-cowork`。
 */
function MigrationFailureView({ state }: { state: MigrationState }): React.ReactNode {
  const { t } = useI18n()
  const failure = state.failure
  const code = failure?.code ?? 'unknown'
  const detail = failure?.detail ?? ''
  const stepKey = failure === null ? null : STEP_KEYS[failure.stepKind]

  return (
    <div className="flex h-full flex-col items-center justify-center gap-6 overflow-y-auto bg-app p-10">
      <div className="w-full max-w-md">
        <h1 className="text-[15px] font-medium text-danger">{t('migration.error.title')}</h1>
        <p className="mt-2 text-[13px] text-fg">{t(`migration.error.${code}`)}</p>
        {stepKey !== null && (
          <p className="mt-1 text-[12px] text-fg-muted">
            {t(stepKey)} · {t('migration.step.failed')}
          </p>
        )}
        <p className="mt-3 text-[12px] text-fg-muted">{t('migration.error.whatNow')}</p>

        {state.merged !== null && state.merged.sessions > 0 && (
          <p className="mt-2 text-[12px] text-accent">
            {t('migration.mergedSummary', {
              sessions: state.merged.sessions,
              messages: state.merged.messages
            })}
          </p>
        )}

        <div className="mt-6 flex flex-wrap items-center gap-2">
          <Button type="button" variant="secondary" onClick={() => { void retryMigration().catch(() => undefined) }}>
            {t('migration.action.retry')}
          </Button>
          <Button
            type="button"
            variant="secondary"
            onClick={() => { void skipMigration().catch(() => undefined) }}
          >
            {t('migration.action.skip')}
          </Button>
          <Button
            type="button"
            variant="secondary"
            onClick={() => { void openMigrationDataDirectory().catch(() => undefined) }}
          >
            {t('migration.action.openDataDirectory')}
          </Button>
          {state.undoAvailable && <UndoButton />}
        </div>

        {detail !== '' && <DiagnosticBlock detail={detail} />}
      </div>
    </div>
  )
}

/**
 * 「撤销本次合并」。
 *
 * ★ 只在这一屏给。合并成功之后闸门就放行了,那一屏不存在 —— 这是刻意的:
 * 撤销是一个「我这次弄错了」的动作,而它唯一的判断依据就是刚才那一屏上的
 * 行数。放一个常驻入口在设置里,用户过一个月再看到它只会问「这是撤销什么」。
 */
function UndoButton(): React.ReactNode {
  const { t } = useI18n()
  const [busy, setBusy] = useState(false)
  return (
    <Button
      type="button"
      variant="danger"
      disabled={busy}
      onClick={() => {
        setBusy(true)
        void undoMigration()
          .catch(() => undefined)
          .finally(() => setBusy(false))
      }}
    >
      {t('migration.action.undoMerge')}
    </Button>
  )
}

/**
 * 诊断信息。
 *
 * ★ 原文**不翻译**(见 `shared/domain/data-migration.ts` 的 `MigrationFailure`)。
 * 它是给搜索和贴给模型用的,`SQLITE_FULL` 翻成「磁盘已满」就再也搜不到了。
 */
function DiagnosticBlock({ detail }: { detail: string }): React.ReactNode {
  const { t } = useI18n()
  const [copied, setCopied] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => () => { if (timer.current !== null) clearTimeout(timer.current) }, [])

  return (
    <div className="mt-6">
      <div className="flex items-center justify-between">
        <span className="text-[11px] text-fg-faint">{t('migration.error.detailLabel')}</span>
        <Button
          type="button"
          size="sm"
          variant="secondary"
          onClick={() => {
            void navigator.clipboard
              .writeText(detail)
              .then(() => {
                setCopied(true)
                timer.current = setTimeout(() => setCopied(false), 2000)
              })
              .catch(() => undefined)
          }}
        >
          {copied ? t('migration.error.copied') : t('migration.error.copyDetail')}
        </Button>
      </div>
      <pre className="selectable mt-1 max-h-40 overflow-auto rounded-md bg-tint p-2 font-mono text-[11px] whitespace-pre-wrap text-fg-muted">
        {detail}
      </pre>
    </div>
  )
}

/**
 * 「已跳过」那一屏。
 *
 * ★ 它不是错误页,但**也不能直接放行** —— 用户得知道这次少了什么、以及旧数据
 * 还在。直接进应用的话,他看到的是一份「少了一部分会话」的数据,而没有任何
 * 迹象说明原因;等下次启动闸门又出现时,那才是真正的困惑。
 */
function MigrationSkippedView({ onContinue }: { onContinue: () => void }): React.ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4 bg-app p-10">
      <div className="w-full max-w-md">
        <h1 className="text-[15px] font-medium text-fg">{t('migration.skipped.title')}</h1>
        <p className="mt-2 text-[13px] text-fg-muted">{t('migration.skipped.body')}</p>
        <div className="mt-6">
          <Button type="button" variant="primary" onClick={onContinue}>
            {t('migration.skipped.continue')}
          </Button>
        </div>
      </div>
    </div>
  )
}

/** 启动链失败时保住诊断和数据目录入口，不能再退化成无提示白屏。 */
function StartupFailureView({ detail, timedOut }: { detail: string; timedOut: boolean }): React.ReactNode {
  const { t } = useI18n()
  const [openFailed, setOpenFailed] = useState(false)
  return (
    <div className="flex h-full flex-col items-center justify-center overflow-y-auto bg-app p-10">
      <div className="w-full max-w-md">
        <h1 className="text-[15px] font-medium text-danger">{t(timedOut ? 'migration.startup.delayedTitle' : 'migration.startup.failedTitle')}</h1>
        <p className="mt-2 text-[13px] text-fg-muted">{t(timedOut ? 'migration.startup.delayedBody' : 'migration.startup.failedBody')}</p>
        <Button type="button" className="mt-6" variant="secondary" onClick={() => {
          setOpenFailed(false)
          void openMigrationDataDirectory().catch(() => setOpenFailed(true))
        }}>
          {t('migration.action.openDataDirectory')}
        </Button>
        {openFailed && <p role="alert" className="mt-2 text-[12px] text-danger">{t('migration.actionFailed')}</p>}
        <DiagnosticBlock detail={detail} />
      </div>
    </div>
  )
}
/**
 * 闸门的宿主。
 *
 * ★ **状态由它持有,不由 App 持有。** App 在闸门期间会走它自己的握手 effect,
 * 而那个 effect 一跑就会 invoke `app:getBootstrap` —— 库还没打开,那一次会失败
 * 并把 `fatal` 置上,于是闸门一放行用户看到的是「首屏握手失败」。
 * 让这一层把 App **完全挡住**(而不是叠在上面),App 的 effect 就一次都不会跑。
 *
 * ★ 「挡住」的条件不止「迁移跑完了」:主进程还得把 handler 装完。放行判据整个
 * 抽在 `migration-release.ts` 里(那里写着为什么),这里只负责照它画。
 */
export function MigrationGateHost({ children }: { children: React.ReactNode }): React.ReactNode {
  const [state, setState] = useState<MigrationState | null>(null)
  const [resolved, setResolved] = useState(false)
  const [snapshotFailure, setSnapshotFailure] = useState<string | null>(null)
  const [startupTimedOut, setStartupTimedOut] = useState(false)
  const waitingForStartup = state === null || (state.ipcReady !== true && (state.phase === 'idle' || (state.phase === 'skipped' && resolved === true)))

  useEffect(() => {
    let active = true
    const accept = (next: MigrationState): void => {
      if (!active) return
      setState(next)
      setSnapshotFailure(null)
      // 仍在 idle 等 IPC 的迟到快照不能清掉超时，否则计时器不会重新武装。
      if (next.phase !== 'idle' || next.ipcReady === true) setStartupTimedOut(false)
      // 合并完成或者用户选了继续,闸门就放行 —— 之后不再回到这一屏。
      if (next.phase === 'idle') setResolved(true)
    }
    /*
      ★ 先订阅再拉快照。反过来的话,「拉到 idle」和「订阅上」之间到达的那次
      progress 会丢 —— 而闸门从 running 走到 idle 正是发生在这个窗口里最坏的一帧。
    */
    const off = onMigrationProgress(accept)
    void getMigrationState()
      .then(accept)
      .catch((error: unknown) => {
        // 原先为 IPC 子集测试直接放行；生产里没有 `ipcReady` 仍挂不了 App，只会永久留白。
        // 需求：首份快照失败时保留原始异常，让真实启动故障有可见诊断。
        if (!active) return
        const detail = error instanceof Error ? (error.stack ?? error.message) : String(error)
        setSnapshotFailure(detail === '' ? 'STARTUP_STATE_UNAVAILABLE' : detail)
      })
    return () => {
      active = false
      off()
    }
  }, [])

  useEffect(() => {
    if (!waitingForStartup || snapshotFailure !== null) return
    // 需求：主进程卡死也不能无限显示骨架；15 秒后显示诊断，但继续监听以便恢复。
    const timer = setTimeout(() => setStartupTimedOut(true), STARTUP_TIMEOUT_MS)
    return () => clearTimeout(timer)
  }, [snapshotFailure, waitingForStartup])

  const startupStalled = startupTimedOut === true || snapshotFailure !== null
  const decision = decideMigrationGate(state, resolved, startupStalled)
  if (decision === 'app') return <>{children}</>

  let screen: React.ReactNode
  if (decision === 'waiting') screen = <AppSkeleton />
  else if (decision === 'startup-failed') {
    const reportedFailure = state === null ? snapshotFailure : (state.startupFailure ?? snapshotFailure)
    screen = <StartupFailureView detail={reportedFailure ?? STARTUP_TIMEOUT_DETAIL} timedOut={reportedFailure === null} />
  } else {
    // `gate` 必然来自一份真实快照；这个兜底只服务 TypeScript 的可空收窄。
    screen = state === null ? <AppSkeleton /> : <MigrationGate state={state} onResolved={() => setResolved(true)} />
  }
  // 需求：Windows/Linux 没有原生标题栏，启动页也必须能拖动、最小化、最大化和关闭。
  return <><div aria-hidden className="app-drag fixed inset-x-0 top-0 h-[34px]" /><WindowControls />{screen}</>
}
