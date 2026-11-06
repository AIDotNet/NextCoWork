/**
 * 设置 › 导入 —— 自动同步、来源行、导入历史三块。
 *
 * ## 为什么它不在「数据」页里
 *
 * 「数据」页那个「导入数据」读的是 NextCoWork 自己导出的整库备份,会合并
 * **全套设置**并按时间戳覆盖。这一页读的是别的 AI 应用留在本机的目录,
 * 语义是逐项、可重试、永不覆盖。两颗都叫「导入」的按钮挨在一起,误点一次的
 * 代价是一份外部数据盖掉整套配置 —— 所以它们在界面上永远不挨着。
 *
 * ## 状态从哪来
 *
 * 全部来自主进程,**页面里没有一份镜像**。作业跑在主进程,渲染层手上那份随时
 * 会过期;靠 `imports:changed` 事件触发重新拉取(事件是限频合并过的,所以
 * 回调里只重新拉,不试图从 payload 拼状态)。
 */
import { AlertTriangle, ChevronDown, DownloadCloud, FolderOpen, RefreshCw } from 'lucide-react'
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import type {
  ImportBatchItem,
  ImportBatchSummary,
  ImportPreview,
  ImportSourceState
} from '../../../../../shared/domain/import'
import type { ImportSourceKind } from '../../../../../shared/domain/import'
import { IMPORT_LIMITS } from '../../../../../shared/domain/import'
import type { Workspace } from '../../../../../shared/domain/workspace'
import { Button } from '../../../components/arc/button/button'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { Switch } from '../../../components/arc/switch/switch'
import { Collapsible, CollapsibleContent, CollapsibleIndicator, CollapsibleTrigger } from '../../../components/ui/Collapsible'
import { cn } from '../../../lib/cn'
import { useI18n, type Translate, type TranslationKey } from '../../../i18n'
import * as importService from '../../../services/import'
import { invoke } from '../../../services/ipc'
import { useTabsStore } from '../../../stores/tabs'
import { useWindowStore } from '../../../stores/window'
import { ImportSelectionDialog } from './ImportSelectionDialog'
import { ImportSyncDialog } from './ImportSyncDialog'
import { detectedKey, sourceNameKey } from './source-name'
import { Spinner } from '../../../components/ui/Spinner'

type Modal = { kind: 'sync' } | { kind: 'select'; preview: ImportPreview } | null

export function ImportPage(): ReactNode {
  const { t, locale } = useI18n()
  const [state, setState] = useState<ImportSourceState | null>(null)
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [batches, setBatches] = useState<ImportBatchSummary[]>([])
  const [historyTotal, setHistoryTotal] = useState(0)
  const [historyOffset, setHistoryOffset] = useState(0)
  const [modal, setModal] = useState<Modal>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [sourceKind, setSourceKind] = useState<ImportSourceKind>('claude-code')

  const sourceId = state?.detection.sourceId ?? ''

  const refresh = useCallback(async (): Promise<void> => {
    try {
      // ★ `detect` 是幂等的:已登记的来源它只刷新计数,不重置授权范围。
      const next = sourceId === '' ? await importService.detect(sourceKind) : await importService.getState(sourceId)
      setState(next)
    } catch (err) {
      setError(message(err))
    }
  }, [sourceId, sourceKind])

  const refreshHistory = useCallback(async (offset: number): Promise<void> => {
    try {
      const page = await importService.history(offset, IMPORT_LIMITS.pageSize)
      setBatches(page.batches)
      setHistoryTotal(page.total)
      setHistoryOffset(page.offset)
    } catch (err) {
      setError(message(err))
    }
  }, [])

  useEffect(() => {
    void (async () => {
      try {
        setState(await importService.detect(sourceKind))
        setWorkspaces(await invoke('workspace:list', undefined))
      } catch (err) {
        setError(message(err))
      }
    })()
    void refreshHistory(0)
  }, [refreshHistory, sourceKind])

  // 主进程推一条就重新拉一次。★ 不从 payload 拼状态 —— 事件是合并过的。
  useEffect(() => {
    return importService.onChanged(() => {
      void refresh()
      void refreshHistory(historyOffset)
    })
  }, [refresh, refreshHistory, historyOffset])

  const run = useCallback(
    async <T,>(label: string, action: () => Promise<T>): Promise<T | null> => {
      setBusy(label)
      setError(null)
      try {
        return await action()
      } catch (err) {
        setError(message(err))
        return null
      } finally {
        setBusy(null)
      }
    },
    []
  )

  const detection = state?.detection
  const sync = state?.sync
  const job = state?.job ?? null
  const detected = detection?.availability === 'detected'
  const running = job !== null && (job.phase === 'importing' || job.phase === 'scanning')

  const openSelection = async (): Promise<void> => {
    if (sourceId === '') return
    const preview = await run('preview', () =>
      importService.preview(sourceId, `preview-${String(Date.now())}`)
    )
    if (preview !== null) setModal({ kind: 'select', preview })
  }

  return (
    <>
      {/* ── 自动同步 ── */}
      <Section title={t('import.autoSync')}>
        <Row
          /*
            ★ 行标题是「启用」而不是再写一遍「自动同步」—— 卡片标题已经说过了。
            同一个词在同一张卡片里出现两次,读的人会以为那是两个不同的东西。
          */
          title={t('import.enable')}
          description={
            <>
              {t('import.autoSyncHint')}
              {/*
                状态与最近检查**并进这一行**,不再单独占一行:它们是这个开关的
                「现在怎么样」,而不是另一个设置项。原来那行的标题是状态值本身
                (「已关闭」),在一列设置项里读起来像个可点的选项。
              */}
              <span className="mt-1 block text-fg-faint">
                {statusLabel(t, sync?.status ?? 'off')}
                {' · '}
                {sync?.lastCheckAt === undefined
                  ? t('import.neverSynced')
                  : t('import.lastCheck', { time: formatTime(sync.lastCheckAt, locale) })}
                {sync?.lastSyncAt !== undefined &&
                  ` · ${t('import.lastSync', { time: formatTime(sync.lastSyncAt, locale) })}`}
              </span>
            </>
          }
        >
          <Switch
            aria-label={t('import.autoSync')}
            checked={sync?.enabled === true}
            /*
              ★ 没检测到来源、或还没授权任何类别时**开不了**。
              允许在空授权上打开的话,开关是绿的而什么都不会同步 ——
              用户会以为功能坏了,而其实是他还没选内容。
            */
            disabled={!detected || busy !== null || (sync?.categories.length ?? 0) === 0}
            onCheckedChange={(enabled) => {
              void run('sync-toggle', async () => {
                setState(await importService.updateSync(sourceId, { enabled }))
              })
            }}
          />
        </Row>
        <Row
          last
          title={t('import.syncedCategories')}
          description={
            sync === undefined || sync.categories.length === 0
              ? t('import.nothingSelected')
              : sync.categories.map((c) => t(`import.category.${c}` as TranslationKey)).join(' · ')
          }
        >
          <div className="flex items-center gap-2">
            <Button type="button" size="sm" variant="secondary" disabled={!detected} onClick={() => setModal({ kind: 'sync' })}>
              {t('import.customize')}
            </Button>
            <Button
              type="button" size="sm"
              variant="secondary"
              disabled={!detected || sync?.enabled !== true || running || busy !== null}
              onClick={() => {
                void run('sync-now', () => importService.syncNow(sourceId))
              }}
            >
              {busy === 'sync-now' ? <Spinner size="sm" /> : <RefreshCw size={13} />}
              {t('import.syncNow')}
            </Button>
          </div>
        </Row>
      </Section>

      {/* ── 来源 ── */}
      <Section title={t('import.fromOtherApps')}>
        <div className="mb-2 flex">
          <SegmentedControl
            value={sourceKind}
            options={[
              { value: 'claude-code', label: t('import.sourceClaude') },
              { value: 'codex', label: t('import.sourceCodex') },
              { value: 'opencode', label: t('import.sourceOpencode') },
              { value: 'opencowork', label: t('import.sourceOpencowork') }
            ]}
            onValueChange={(next) => setSourceKind(next as ImportSourceKind)}
            label={t('import.fromOtherApps')}
          />
        </div>
        <Row
          title={t(sourceNameKey(sourceKind))}
          description={
            detected ? (
              <>
                {/*
                  ★ 路径**独占一行**且等宽,不再和计数用 `·` 串成一条长句。
                  串在一起的话,窄一点的窗口上它会在路径中间折行,而一条被折断的
                  绝对路径是最难认的东西。`title` 给全量值,截断了也能悬停看到。
                */}
                <span
                  className="block truncate font-mono text-[11px] text-fg-faint"
                  title={detection?.configDir}
                >
                  {detection?.configDir}
                </span>
                <span className="mt-1 block">
                  {t(detectedKey(sourceKind), {
                    projects: detection?.projectCount ?? 0,
                    sessions: detection?.sessionCount ?? 0
                  })}
                  {' · '}
                  {originLabel(t, detection?.origin ?? 'auto')}
                </span>
              </>
            ) : (
              availabilityLabel(t, detection?.availability ?? 'not-found', sourceKind)
            )
          }
          descriptionTone={detected ? undefined : 'danger'}
          last
        >
          <div className="flex items-center gap-2">
            <Button
              type="button" size="sm"
              variant="secondary"
              disabled={busy !== null}
              onClick={() => {
                  void run('detect', async () => {
                  setState(await importService.detect(sourceKind))
                })
              }}
            >
              {busy === 'detect' ? <Spinner size="sm" /> : <RefreshCw size={13} />}
              {t('import.detect')}
            </Button>
            <Button
              type="button" size="sm"
              variant="secondary"
              disabled={busy !== null}
              onClick={() => {
                void run('choose', async () => {
                  setState(await importService.chooseSource(sourceKind))
                })
              }}
            >
              <FolderOpen size={13} />
              {t('import.chooseDirectory')}
            </Button>
            <Button
              type="button" size="sm"
              variant="primary"
              disabled={!detected || running || busy !== null}
              onClick={() => {
                void openSelection()
              }}
            >
              {busy === 'preview' ? <Spinner size="sm" /> : <DownloadCloud size={13} />}
              {t('import.startImport')}
            </Button>
          </div>
        </Row>
      </Section>

      {/*
        作业进度。★ 关掉设置页任务照跑,重开这里会显示同一个 job 的快照。

        ★ 卡片标题跟着阶段走,**不是**写死的「导入」—— 在一个本来就叫「导入」的
        页面里再放一张叫「导入」的卡片,等于没说。行标题原来是 `counts.imported`
        (「新增」),单独看更是不知所云。
      */}
      {job !== null && (
        <Section title={running ? t('import.jobRunning') : t('import.jobLast')}>
          <Row
            title={
              running
                ? t('import.importing', { done: job.done, total: job.total })
                : t(`import.phase.${job.phase}` as TranslationKey)
            }
            description={
              <>
                <CountsLine counts={job.counts} />
                {job.currentTitle !== undefined && running && (
                  <span className="mt-1 block truncate text-fg-faint">{job.currentTitle}</span>
                )}
              </>
            }
            density="compact"
            last
          >
            {running ? (
              <Button type="button" size="sm" variant="secondary" onClick={() => void importService.cancel(job.jobId)}>{t('import.cancel')}</Button>
            ) : (
              /* 进度条只在跑的时候有意义;跑完之后这里留一条时间,而不是一颗死按钮。 */
              <span className="text-[11.5px] text-fg-faint">
                {job.endedAt === undefined ? '' : formatTime(job.endedAt, locale)}
              </span>
            )}
          </Row>
        </Section>
      )}

      {/* ── 历史 ── */}
      <Section title={t('import.history')}>
        {batches.length === 0 ? (
          <Row title={t('import.historyEmpty')} density="single" last />
        ) : (
          batches.map((batch, index) => (
            <BatchRow key={batch.id} batch={batch} last={index === batches.length - 1} locale={locale} />
          ))
        )}
        {historyTotal > IMPORT_LIMITS.pageSize && (
          <div className="flex items-center justify-end gap-2 border-t border-border/50 py-2.5">
            <span className="mr-auto text-[11.5px] text-fg-faint">
              {t('import.pageOf', {
                from: historyOffset + 1,
                to: Math.min(historyOffset + IMPORT_LIMITS.pageSize, historyTotal),
                total: historyTotal
              })}
            </span>
            <Button
              type="button" size="sm"
              variant="secondary"
              disabled={historyOffset === 0}
              onClick={() => void refreshHistory(Math.max(0, historyOffset - IMPORT_LIMITS.pageSize))}
            >
              {t('import.prevPage')}
            </Button>
            <Button
              type="button" size="sm"
              variant="secondary"
              disabled={historyOffset + IMPORT_LIMITS.pageSize >= historyTotal}
              onClick={() => void refreshHistory(historyOffset + IMPORT_LIMITS.pageSize)}
            >
              {t('import.nextPage')}
            </Button>
          </div>
        )}
      </Section>

      {error !== null && (
        <p role="alert" className="flex items-center gap-1.5 px-1 text-[12px] text-danger">
          <AlertTriangle size={13} />
          {error}
        </p>
      )}

      <ImportSyncDialog
        open={modal?.kind === 'sync'}
        value={sync?.categories ?? []}
        busy={busy !== null}
        onClose={() => setModal(null)}
        onSave={(categories) => {
          void run('sync-save', async () => {
            setState(await importService.updateSync(sourceId, { categories }))
            setModal(null)
          })
        }}
      />

      <ImportSelectionDialog
        open={modal?.kind === 'select'}
        preview={modal?.kind === 'select' ? modal.preview : null}
        workspaces={workspaces}
        busy={busy === 'apply'}
        onClose={() => setModal(null)}
        onApply={(itemIds, targets) => {
          void run('apply', async () => {
            await importService.apply({
              previewId: modal?.kind === 'select' ? modal.preview.previewId : '',
              itemIds,
              workspaceTargets: targets,
              // ★ 防双击重入:主进程按它把重复提交折叠成同一个 job。
              requestId: `apply-${String(Date.now())}`
            })
            setModal(null)
            await refreshHistory(0)
          })
        }}
      />
    </>
  )
}

/** 一条批次,展开后按项列出结果。 */
function BatchRow({
  batch,
  last,
  locale
}: {
  batch: ImportBatchSummary
  last: boolean
  locale: string
}): ReactNode {
  const { t } = useI18n()
  const [open, setOpen] = useState(false)
  const [items, setItems] = useState<ImportBatchItem[]>([])
  const openSession = useTabsStore((s) => s.openSession)
  const closeSettings = useWindowStore((s) => s.closeSettings)

  useEffect(() => {
    if (!open || items.length > 0) return
    void importService
      .historyItems(batch.id, 0, IMPORT_LIMITS.pageSize)
      .then((page) => setItems(page.items))
      .catch(() => setItems([]))
  }, [open, items.length, batch.id])

  return (
    <Collapsible open={open} onOpenChange={setOpen} className={cn(!last && 'border-b border-border/50')}>
      <CollapsibleTrigger className="py-2.5">
        <div className="min-w-0 flex-1">
          <p className="text-[12.5px] text-fg">
            {t(sourceNameKey(batch.sourceKind))} ·{' '}
            {batch.trigger === 'auto' ? t('import.triggerAuto') : t('import.triggerManual')}
          </p>
          <p className="mt-0.5 text-[11.5px] text-fg-muted">
            {formatTime(batch.startedAt, locale)} · <CountsLine counts={batch.counts} />
          </p>
        </div>
        <CollapsibleIndicator>
          <ChevronDown size={13} />
        </CollapsibleIndicator>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <ul className="pb-2">
          {/*
            ★ 右侧三块**定宽**。原来诊断文字是自然宽度,于是每一行的「结果」和
            「打开聊天」都随诊断长短左右浮动,一列按钮参差不齐 —— 而这一列
            恰恰是用户要扫着点的。定宽之后诊断自己截断,按钮永远对齐。
          */}
          {items.map((item, index) => (
            <li
              key={`${item.batchId}-${String(index)}`}
              className="flex items-center gap-2.5 rounded-[6px] py-1.5 pl-2 hover:bg-tint"
            >
              <span className="w-[72px] shrink-0 text-[11px] text-fg-faint">
                {t(`import.category.${item.category}` as TranslationKey)}
              </span>
              <span className="min-w-0 flex-1 truncate text-[12px] text-fg" title={item.title}>
                {item.title}
              </span>
              <span
                className="w-[132px] shrink-0 truncate text-right text-[11px] text-fg-muted"
                title={item.diagnostics.map((d) => t(`import.diag.${d.code}` as TranslationKey)).join(' · ')}
              >
                {item.diagnostics.length === 0
                  ? ''
                  : t(`import.diag.${item.diagnostics[0]?.code}` as TranslationKey)}
              </span>
              <ResultBadge result={item.result} />
              {/* ★ 目标被删掉的行显示成不可打开 —— 点它不该把数据复活。 */}
              <span className="flex w-[84px] shrink-0 justify-end">
                {item.targetKind === 'session' && item.targetId !== undefined ? (
                  item.targetMissing === true ? (
                    <span className="text-[11px] text-fg-faint">{t('import.targetMissing')}</span>
                  ) : (
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      onClick={() => {
                        openSession(item.targetWorkspaceId ?? '', item.targetId ?? '', item.title)
                        closeSettings()
                      }}
                    >
                      {t('import.openChat')}
                    </Button>
                  )
                ) : null}
              </span>
            </li>
          ))}
        </ul>
      </CollapsibleContent>
    </Collapsible>
  )
}

function CountsLine({ counts }: { counts: ImportBatchSummary['counts'] }): ReactNode {
  const { t } = useI18n()
  const parts: string[] = []
  const push = (key: keyof ImportBatchSummary['counts']): void => {
    if (counts[key] > 0) parts.push(`${t(`import.counts.${key}` as TranslationKey)} ${String(counts[key])}`)
  }
  push('imported')
  push('updated')
  push('skipped')
  push('conflict')
  push('failed')
  push('incompatible')
  return <>{parts.length === 0 ? '—' : parts.join(' · ')}</>
}

/**
 * 结果徽标。★ 用**颜色**分档,不是一律灰字 —— 用户扫这一列是为了找出
 * 「哪些没成」,而在一片同色小字里找 `冲突` / `失败` 需要逐行读。
 */
function ResultBadge({ result }: { result: ImportBatchItem['result'] }): ReactNode {
  const { t } = useI18n()
  const tone =
    result === 'imported' || result === 'updated'
      ? 'text-accent'
      : result === 'failed' || result === 'conflict'
        ? 'text-danger'
        : 'text-fg-faint'
  return (
    <span className={cn('w-[52px] shrink-0 text-right text-[11px]', tone)}>
      {t(`import.counts.${resultKey(result)}` as TranslationKey)}
    </span>
  )
}

/** 结果码 → 计数键。`cancelled` 并进 `skipped`:两者都是「没做」。 */
function resultKey(result: ImportBatchItem['result']): string {
  return result === 'cancelled' ? 'skipped' : result
}

function statusLabel(t: (key: TranslationKey, p?: Record<string, string | number>) => string, status: string): string {
  return t(`import.syncStatus.${status}` as TranslationKey)
}

function originLabel(t: (key: TranslationKey) => string, origin: string): string {
  if (origin === 'env') return t('import.originEnv')
  if (origin === 'user-picked') return t('import.originPicked')
  return t('import.originAuto')
}

function availabilityLabel(
  t: Translate,
  availability: string,
  kind: ImportSourceKind
): string {
  if (availability === 'denied') return t('import.denied')
  if (availability === 'unreadable') return t('import.unreadable')
  // ★ 「没找到」必须说清是**哪家**没找到 —— 三个来源共用这一行,不带名字的话
  // 用户看不出是自己选错了来源还是目录真的不在。
  return t('import.notFound', { source: t(sourceNameKey(kind)) })
}

function formatTime(at: number, locale: string): string {
  return new Date(at).toLocaleString(locale)
}

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}

// ─── 本页的行/块外壳 ───
// ★ 照 `DataPage` / `connection/McpPane` 的先例在页内自定义,不抽公共组件:
//   这套 chrome 在仓库里已经有七八份,单独为两页抽一份反而制造了第九种。

function Section({ title, children }: { title: string; children: ReactNode }): ReactNode {
  return (
    /*
      ★★ `shrink-0` **不能省。**

      设置内容区是一个 flex 纵向容器,卡片是它的 flex item,默认 `flex-shrink: 1`。
      于是内容一旦高过视口,浏览器不是让容器滚动,而是**把卡片压扁** ——
      再配上卡片自己的 `overflow-hidden`,底部那一行就被整齐地切掉了。
      症状极像「布局溢出」,而实际上量出来 `scrollHeight === clientHeight`,
      因为卡片是被压成了那个高度,不是内容撑破了它。
    */
    <section className="mb-3 shrink-0 overflow-hidden rounded-[18px] bg-surface px-4">
      <h3 className="pt-4 pb-0.5 text-[13px] text-fg">{title}</h3>
      {children}
    </section>
  )
}

function Row({
  title,
  description,
  children,
  descriptionTone,
  density = 'default',
  last = false
}: {
  title: string
  description?: ReactNode
  children?: ReactNode
  descriptionTone?: 'danger'
  density?: 'default' | 'compact' | 'single'
  last?: boolean
}): ReactNode {
  return (
    <div
      /*
        ★ `flex-wrap` 是**降级策略**,不是装饰:三颗按钮加起来将近 300px,
        窄窗口下不换行的话,`min-w-0 flex-1` 的文字块会被挤到接近 0 宽,
        而 `truncate` 会把它裁成看不见 —— 那正是「会话没有标题」踩过的坑。
        换行之后最坏情况是按钮落到下一行,文字始终读得到。
      */
      className={cn(
        'flex flex-wrap items-center gap-x-5 gap-y-2',
        density === 'single' ? 'min-h-[48px] py-2' : density === 'compact' ? 'min-h-[58px] py-2.5' : 'min-h-[62px] py-3',
        !last && 'border-b border-border/50'
      )}
    >
      <div className="min-w-[220px] flex-1 basis-0">
        <p className="text-[13px] text-fg">{title}</p>
        {description !== undefined && (
          <p
            className={cn(
              'mt-1 text-[11.5px] leading-[1.45] break-words text-fg-muted',
              descriptionTone === 'danger' && 'text-danger'
            )}
          >
            {description}
          </p>
        )}
      </div>
      {children !== undefined && <div className="flex shrink-0 justify-end">{children}</div>}
    </div>
  )
}
