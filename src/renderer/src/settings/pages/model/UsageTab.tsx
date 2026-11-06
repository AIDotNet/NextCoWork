import { ChevronRight, RefreshCw, Search } from 'lucide-react'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import type {
  UsageAttemptRecord,
  UsageDimensionStat,
  UsageRequestLogsPage,
  UsageStatusFilter,
  UsageSummary,
  UsageWindow
} from '../../../../../shared/domain/usage'
import { Select } from '../../../components/ui/Select'
import { TextInput } from '../../../components/ui/TextInput'
import { Switch } from '../../../components/arc/switch/switch'
import { useI18n, type Locale, type Translate } from '../../../i18n'
import { cn } from '../../../lib/cn'
import {
  getUsageModelStats,
  getUsageProviderStats,
  getUsageRequestLogs,
  getUsageSummary
} from '../../../services/usage'

type UsageRange = '24h' | '7d' | '30d' | 'all'
type UsageSection = 'requests' | 'providers' | 'models' | 'tools'

const PAGE_SIZE = 50
const RANGES: readonly UsageRange[] = ['24h', '7d', '30d', 'all']
const SECTIONS: readonly UsageSection[] = ['requests', 'providers', 'models', 'tools']

function windowFor(range: UsageRange): UsageWindow {
  const to = Date.now() + 1
  const duration =
    range === '24h'
      ? 24 * 60 * 60 * 1000
      : range === '7d'
        ? 7 * 24 * 60 * 60 * 1000
        : range === '30d'
          ? 30 * 24 * 60 * 60 * 1000
          : null
  return duration === null ? { to } : { from: to - duration, to }
}

function rangeLabel(t: Translate, range: UsageRange): string {
  return t(`usage.range.${range}` as const)
}

function sectionLabel(t: Translate, section: UsageSection): string {
  return t(`usage.section.${section}` as const)
}

function number(value: number, locale: Locale): string {
  return new Intl.NumberFormat(locale).format(value)
}

function compactNumber(value: number, locale: Locale): string {
  return new Intl.NumberFormat(locale, {
    notation: 'compact',
    maximumFractionDigits: value >= 1_000_000 ? 1 : 0
  }).format(value)
}

function percent(value: number | null, locale: Locale): string {
  if (value === null) return '—'
  return new Intl.NumberFormat(locale, {
    style: 'percent',
    maximumFractionDigits: 1
  }).format(value)
}

function costs(
  values: readonly { currency: string; micros: number }[],
  locale: Locale
): string {
  if (values.length === 0) return '—'
  return values
    .map(({ currency, micros }) => {
      const value = micros / 1_000_000
      return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency,
        minimumFractionDigits: 2,
        maximumFractionDigits: value !== 0 && Math.abs(value) < 0.01 ? 6 : 2
      }).format(value)
    })
    .join(' + ')
}

function latency(value: number | null, locale: Locale, t: Translate): string {
  if (value === null) return '—'
  if (value >= 1000) {
    return t('usage.seconds', {
      value: new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value / 1000)
    })
  }
  return t('usage.milliseconds', { value: number(Math.round(value), locale) })
}

function requestTokens(record: UsageAttemptRecord): number {
  return (
    record.inputTokens +
    record.outputTokens +
    record.cacheReadTokens +
    record.cacheWriteTokens
  )
}

export function UsageTab(): ReactNode {
  const { t, locale } = useI18n()
  const [range, setRange] = useState<UsageRange>('24h')
  const [section, setSection] = useState<UsageSection>('requests')
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState<UsageStatusFilter>('all')
  const [showAllDetails, setShowAllDetails] = useState(false)
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [offset, setOffset] = useState(0)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const [summary, setSummary] = useState<UsageSummary | null>(null)
  const [logs, setLogs] = useState<UsageRequestLogsPage | null>(null)
  const [providerStats, setProviderStats] = useState<UsageDimensionStat[]>([])
  const [modelStats, setModelStats] = useState<UsageDimensionStat[]>([])
  const [overviewLoading, setOverviewLoading] = useState(true)
  const [logsLoading, setLogsLoading] = useState(true)
  const [loadFailed, setLoadFailed] = useState(false)

  const usageWindow = useMemo(
    () => windowFor(range),
    [range, refreshVersion]
  )

  useEffect(() => {
    let active = true
    setOverviewLoading(true)
    setLoadFailed(false)
    void Promise.all([
      getUsageSummary(usageWindow),
      getUsageProviderStats(usageWindow),
      getUsageModelStats(usageWindow)
    ])
      .then(([nextSummary, providers, models]) => {
        if (!active) return
        setSummary(nextSummary)
        setProviderStats(providers)
        setModelStats(models)
      })
      .catch((error: unknown) => {
        if (!active) return
        console.error('[usage] failed to load summary', error)
        setLoadFailed(true)
      })
      .finally(() => {
        if (active) setOverviewLoading(false)
      })
    return () => {
      active = false
    }
  }, [usageWindow])

  useEffect(() => {
    let active = true
    setLogsLoading(true)
    void getUsageRequestLogs({
      ...usageWindow,
      query,
      status,
      offset,
      limit: PAGE_SIZE
    })
      .then((page) => {
        if (active) setLogs(page)
      })
      .catch((error: unknown) => {
        if (!active) return
        console.error('[usage] failed to load request logs', error)
        setLoadFailed(true)
      })
      .finally(() => {
        if (active) setLogsLoading(false)
      })
    return () => {
      active = false
    }
  }, [offset, query, status, usageWindow])

  const chooseRange = (next: UsageRange): void => {
    setRange(next)
    setOffset(0)
    setExpanded(new Set())
  }

  const retry = (): void => {
    setLoadFailed(false)
    setRefreshVersion((value) => value + 1)
  }

  const toggleExpanded = (id: string): void => {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  return (
    <div data-testid="usage-page" className="min-w-0 pb-5 pt-1">
      <div className="mb-4 flex items-center justify-end gap-2">
        <div className="flex rounded-pill bg-tint p-0.5" aria-label={t('usage.rangeLabel')}>
          {RANGES.map((item) => (
            <button
              key={item}
              type="button"
              aria-pressed={range === item}
              onClick={() => chooseRange(item)}
              className={cn(
                'h-7 rounded-pill px-3 text-[11.5px] transition-colors',
                range === item
                  ? 'bg-surface-field text-fg shadow-sm'
                  : 'text-fg-muted hover:text-fg'
              )}
            >
              {rangeLabel(t, item)}
            </button>
          ))}
        </div>
        <button
          type="button"
          aria-label={t('common.refresh')}
          title={t('common.refresh')}
          onClick={retry}
          className="flex size-8 items-center justify-center rounded-full bg-tint text-icon transition-colors hover:bg-tint-hover"
        >
          <RefreshCw size={14} className={overviewLoading || logsLoading ? 'animate-spin motion-reduce:animate-none' : ''} />
        </button>
      </div>

      {loadFailed && summary === null ? (
        <div className="flex min-h-[260px] flex-col items-center justify-center gap-3 text-center">
          <p className="text-[13px] text-fg">{t('usage.loadFailed')}</p>
          <button
            type="button"
            onClick={retry}
            className="rounded-[8px] bg-accent px-3 py-1.5 text-[11.5px] text-accent-fg"
          >
            {t('common.retry')}
          </button>
        </div>
      ) : (
        <>
          <SummaryCards summary={summary} loading={overviewLoading} locale={locale} t={t} />

          <div className="mt-4 flex w-fit max-w-full overflow-x-auto rounded-pill bg-tint p-0.5">
            {SECTIONS.map((item) => (
              <button
                key={item}
                type="button"
                aria-pressed={section === item}
                onClick={() => setSection(item)}
                className={cn(
                  'h-7 whitespace-nowrap rounded-pill px-3 text-[11.5px] transition-colors',
                  section === item
                    ? 'bg-surface-field text-fg shadow-sm'
                    : 'text-fg-muted hover:text-fg'
                )}
              >
                {sectionLabel(t, item)}
              </button>
            ))}
          </div>

          {section === 'requests' && (
            <RequestLogs
              page={logs}
              loading={logsLoading}
              query={query}
              status={status}
              showAllDetails={showAllDetails}
              expanded={expanded}
              locale={locale}
              t={t}
              onQuery={(value) => {
                setQuery(value)
                setOffset(0)
              }}
              onStatus={(value) => {
                setStatus(value)
                setOffset(0)
              }}
              onShowAllDetails={setShowAllDetails}
              onToggleExpanded={toggleExpanded}
              onPrevious={() => setOffset((value) => Math.max(0, value - PAGE_SIZE))}
              onNext={() => setOffset((value) => value + PAGE_SIZE)}
            />
          )}
          {section === 'providers' && (
            <DimensionTable
              rows={providerStats}
              loading={overviewLoading}
              heading={t('usage.provider')}
              locale={locale}
              t={t}
            />
          )}
          {section === 'models' && (
            <DimensionTable
              rows={modelStats}
              loading={overviewLoading}
              heading={t('common.model')}
              locale={locale}
              t={t}
            />
          )}
          {section === 'tools' && (
            <ToolStats rows={modelStats} summary={summary} loading={overviewLoading} locale={locale} t={t} />
          )}
        </>
      )}
    </div>
  )
}

function SummaryCards({
  summary,
  loading,
  locale,
  t
}: {
  summary: UsageSummary | null
  loading: boolean
  locale: Locale
  t: Translate
}): ReactNode {
  const successRate =
    summary === null || summary.requestCount === 0
      ? null
      : summary.successCount / summary.requestCount
  const cards = [
    {
      title: t('usage.totalRequests'),
      value: summary === null ? '—' : number(summary.requestCount, locale),
      detail:
        summary === null
          ? t('common.loading')
          : t('usage.requestSummary', {
              success: percent(successRate, locale),
              failed: number(summary.failedCount, locale)
            }),
      foot:
        summary === null
          ? ''
          : t('usage.averageLatencySummary', {
              latency: latency(summary.averageLatencyMs, locale, t),
              ttft: latency(summary.averageTimeToFirstTokenMs, locale, t)
            })
    },
    {
      title: t('usage.totalCost'),
      value: summary === null ? '—' : costs(summary.costs, locale),
      detail: t('usage.frozenPriceHint'),
      foot: t('usage.unpricedHint')
    },
    {
      title: t('usage.totalTokens'),
      value: summary === null ? '—' : compactNumber(summary.totalTokens, locale),
      detail:
        summary === null
          ? t('common.loading')
          : t('usage.inputOutputSummary', {
              input: compactNumber(summary.inputTokens, locale),
              output: compactNumber(summary.outputTokens, locale)
            }),
      foot:
        summary === null
          ? ''
          : t('usage.thinkingSummary', {
              tokens: compactNumber(summary.thinkingTokens, locale),
              estimated: number(summary.estimatedThinkingRequestCount, locale)
            })
    },
    {
      title: t('usage.cacheHitRate'),
      value:
        summary === null
          ? '—'
          : t('usage.byToken', { rate: percent(summary.cacheHitRate, locale) }),
      detail:
        summary === null
          ? t('common.loading')
          : t('usage.cacheSummary', {
              read: compactNumber(summary.cacheReadTokens, locale),
              write: compactNumber(summary.cacheWriteTokens, locale)
            }),
      foot:
        summary === null
          ? ''
          : t('usage.cacheWrite1hSummary', {
              tokens: compactNumber(summary.cacheWrite1hTokens, locale)
            })
    }
  ]

  return (
    <div className="grid grid-cols-2 gap-2 min-[760px]:grid-cols-4">
      {cards.map((card) => (
        <section
          key={card.title}
          className="min-h-[122px] rounded-[18px] bg-surface px-3.5 py-3"
          aria-busy={loading}
        >
          <p className="text-[11px] text-fg-faint">{card.title}</p>
          <p className="mt-1 truncate text-[18px] font-semibold tabular-nums text-fg">
            {card.value}
          </p>
          <p className="mt-1 text-[10.5px] leading-[1.35] text-fg-muted">{card.detail}</p>
          {card.foot !== '' && (
            <p className="mt-0.5 text-[10px] leading-[1.3] text-fg-faint">{card.foot}</p>
          )}
        </section>
      ))}
    </div>
  )
}

function RequestLogs({
  page,
  loading,
  query,
  status,
  showAllDetails,
  expanded,
  locale,
  t,
  onQuery,
  onStatus,
  onShowAllDetails,
  onToggleExpanded,
  onPrevious,
  onNext
}: {
  page: UsageRequestLogsPage | null
  loading: boolean
  query: string
  status: UsageStatusFilter
  showAllDetails: boolean
  expanded: ReadonlySet<string>
  locale: Locale
  t: Translate
  onQuery: (value: string) => void
  onStatus: (value: UsageStatusFilter) => void
  onShowAllDetails: (value: boolean) => void
  onToggleExpanded: (id: string) => void
  onPrevious: () => void
  onNext: () => void
}): ReactNode {
  const start = page === null || page.total === 0 ? 0 : page.offset + 1
  const end = page === null ? 0 : Math.min(page.total, page.offset + page.items.length)
  return (
    <section className="mt-3 min-w-0">
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <TextInput
          value={query}
          onChange={onQuery}
          placeholder={t('usage.searchPlaceholder')}
          ariaLabel={t('usage.searchLabel')}
          icon={<Search size={13} />}
          size="sm"
          className="w-[220px]"
        />
        <div className="w-[104px]">
          <Select
            inModal
            value={status}
            ariaLabel={t('usage.statusFilterLabel')}
            onValueChange={(next) => onStatus(next as UsageStatusFilter)}
            options={[
              { value: 'all', label: t('usage.status.all') },
              { value: 'success', label: t('usage.status.success') },
              { value: 'failed', label: t('usage.status.failed') }
            ]}
          />
        </div>
        <span className="flex-1" />
        <label className="flex items-center gap-2 text-[11px] text-fg-muted">
          {t('usage.showDetails')}
          <Switch
            checked={showAllDetails}
            onCheckedChange={onShowAllDetails}
            aria-label={t('usage.showDetails')}
          />
        </label>
        <span className="text-[11px] text-fg-faint">
          {t('usage.recordCount', { count: page?.total ?? 0 })}
        </span>
      </div>

      <div className="min-w-0 overflow-hidden rounded-[12px] border border-border">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[730px] table-fixed border-collapse text-left">
            <thead className="bg-canvas text-[10.5px] text-fg-faint">
              <tr className="h-8 border-b border-hairline">
                <th className="w-[105px] px-3 font-normal">{t('usage.time')}</th>
                <th className="w-[116px] px-2 font-normal">{t('usage.provider')}</th>
                <th className="px-2 font-normal">{t('common.model')}</th>
                <th className="w-[82px] px-2 text-right font-normal">{t('usage.tokens')}</th>
                <th className="w-[94px] px-2 text-right font-normal">{t('usage.cost')}</th>
                <th className="w-[84px] px-2 text-right font-normal">{t('usage.latency')}</th>
                <th className="w-[68px] px-2 text-right font-normal">{t('usage.status')}</th>
                <th className="w-8" />
              </tr>
            </thead>
            <tbody className={cn('text-[11px]', loading && 'opacity-55')}>
              {(page?.items ?? []).map((record) => {
                const open = showAllDetails || expanded.has(record.id)
                return (
                  <RequestRow
                    key={record.id}
                    record={record}
                    open={open}
                    locale={locale}
                    t={t}
                    onToggle={() => onToggleExpanded(record.id)}
                  />
                )
              })}
            </tbody>
          </table>
        </div>
        {!loading && page?.items.length === 0 && (
          <div className="flex h-[148px] items-center justify-center text-[12px] text-fg-faint">
            {query.trim() === '' && status === 'all'
              ? t('usage.empty')
              : t('usage.noMatch')}
          </div>
        )}
        {loading && page === null && (
          <div className="flex h-[148px] items-center justify-center text-[12px] text-fg-faint">
            {t('common.loading')}
          </div>
        )}
      </div>

      <div className="mt-2 flex items-center justify-end gap-2 text-[10.5px] text-fg-faint">
        <span>{t('usage.pageRange', { start, end, total: page?.total ?? 0 })}</span>
        <button
          type="button"
          onClick={onPrevious}
          disabled={page === null || page.offset === 0 || loading}
          className="rounded-[8px] border border-border px-2 py-1 text-fg-muted disabled:opacity-35"
        >
          {t('usage.previousPage')}
        </button>
        <button
          type="button"
          onClick={onNext}
          disabled={page === null || page.offset + page.items.length >= page.total || loading}
          className="rounded-[8px] border border-border px-2 py-1 text-fg-muted disabled:opacity-35"
        >
          {t('usage.nextPage')}
        </button>
      </div>
    </section>
  )
}

function RequestRow({
  record,
  open,
  locale,
  t,
  onToggle
}: {
  record: UsageAttemptRecord
  open: boolean
  locale: Locale
  t: Translate
  onToggle: () => void
}): ReactNode {
  const date = new Intl.DateTimeFormat(locale, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  }).format(new Date(record.at))
  return (
    <>
      <tr
        data-testid="usage-request-row"
        className={cn('h-9 border-b border-hairline', open && 'bg-tint/40')}
      >
        <td className="px-3 tabular-nums text-fg-faint">{date}</td>
        <td className="truncate px-2 text-fg-muted" title={record.providerName}>
          {record.providerName}
        </td>
        <td className="truncate px-2 text-fg" title={record.upstreamModel}>
          {record.upstreamModel}
        </td>
        <td className="px-2 text-right tabular-nums text-fg-muted">
          {number(requestTokens(record), locale)}
        </td>
        <td className="px-2 text-right tabular-nums text-fg">
          {record.costMicros === null || record.currency === null
            ? '—'
            : costs([{ currency: record.currency, micros: record.costMicros }], locale)}
        </td>
        <td className="px-2 text-right tabular-nums text-fg-muted">
          {latency(record.latencyMs, locale, t)}
        </td>
        <td className="px-2 text-right">
          <span
            className={cn(
              'rounded-pill px-2 py-0.5 text-[10px] tabular-nums',
              record.ok ? 'bg-accent/10 text-accent' : 'bg-danger/10 text-danger'
            )}
          >
            {record.httpStatus ?? (record.ok ? t('usage.status.success') : t('usage.status.failed'))}
          </span>
        </td>
        <td>
          <button
            type="button"
            aria-label={open ? t('usage.collapseDetails') : t('usage.expandDetails')}
            aria-expanded={open}
            onClick={onToggle}
            className="flex size-7 items-center justify-center text-icon"
          >
            <ChevronRight size={13} className={cn('transition-transform', open && 'rotate-90')} />
          </button>
        </td>
      </tr>
      {open && (
        <tr className="border-b border-hairline bg-tint/25">
          <td colSpan={8} className="px-3 py-3">
            <div className="grid grid-cols-3 gap-x-4 gap-y-2 min-[760px]:grid-cols-6">
              <DetailMetric label={t('usage.inputTokens')} value={number(record.inputTokens, locale)} />
              <DetailMetric label={t('usage.cacheReadTokens')} value={number(record.cacheReadTokens, locale)} />
              <DetailMetric label={t('usage.cacheWriteTokens')} value={number(record.cacheWriteTokens, locale)} />
              <DetailMetric label={t('usage.outputTokens')} value={number(record.outputTokens, locale)} />
              <DetailMetric
                label={t('usage.thinkingTokens')}
                value={
                  record.thinkingTokens === null
                    ? '—'
                    : `${record.thinkingTokensEstimated ? '≈' : ''}${number(record.thinkingTokens, locale)}`
                }
              />
              <DetailMetric
                label={t('usage.firstTokenLatency')}
                value={latency(record.timeToFirstTokenMs, locale, t)}
              />
            </div>
            <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-2 gap-y-1 border-t border-hairline pt-2 text-[10px] leading-[1.45]">
              <dt className="text-fg-faint">{t('usage.runId')}</dt>
              <dd className="selectable truncate font-mono text-fg-muted" title={record.runId}>{record.runId}</dd>
              <dt className="text-fg-faint">{t('usage.endpoint')}</dt>
              <dd className="selectable truncate font-mono text-fg-muted" title={record.endpoint}>{record.endpoint || '—'}</dd>
              <dt className="text-fg-faint">{t('usage.response')}</dt>
              <dd className="truncate text-fg-muted">
                {[
                  record.protocol,
                  record.responseModel,
                  record.stopReason,
                  t('usage.attemptNumber', { number: record.attempt })
                ].filter(Boolean).join(' · ')}
              </dd>
              {record.errorKind !== null && (
                <>
                  <dt className="text-fg-faint">{t('usage.error')}</dt>
                  <dd className="selectable text-danger">
                    {record.errorKind}{record.errorMessage === null ? '' : ` · ${record.errorMessage}`}
                  </dd>
                </>
              )}
              {record.pricingTier !== null && (
                <>
                  <dt className="text-fg-faint">{t('usage.pricingRule')}</dt>
                  <dd className="text-fg-muted">
                    {t('usage.pricingTier', { tier: record.pricingTier + 1 })}
                    {record.pricingWindow === null ? '' : ` · ${record.pricingWindow}`}
                  </dd>
                </>
              )}
            </dl>
            {record.thinkingTokensEstimated && (
              <p className="mt-2 text-[10px] text-fg-faint">{t('usage.thinkingEstimatedHint')}</p>
            )}
          </td>
        </tr>
      )}
    </>
  )
}

function DetailMetric({ label, value }: { label: string; value: string }): ReactNode {
  return (
    <div>
      <p className="text-[10px] text-fg-faint">{label}</p>
      <p className="mt-0.5 text-[11.5px] tabular-nums text-fg">{value}</p>
    </div>
  )
}

function DimensionTable({
  rows,
  loading,
  heading,
  locale,
  t
}: {
  rows: readonly UsageDimensionStat[]
  loading: boolean
  heading: string
  locale: Locale
  t: Translate
}): ReactNode {
  return (
    <section className="mt-3 overflow-hidden rounded-[12px] border border-border">
      <div className="overflow-x-auto">
        <table className="w-full min-w-[700px] table-fixed border-collapse text-left">
          <thead className="text-[10.5px] text-fg-faint">
            <tr className="h-8 border-b border-hairline">
              <th className="px-3 font-normal">{heading}</th>
              <th className="w-[80px] px-2 text-right font-normal">{t('usage.requests')}</th>
              <th className="w-[90px] px-2 text-right font-normal">{t('usage.successRate')}</th>
              <th className="w-[145px] px-2 text-right font-normal">{t('usage.tokenBreakdown')}</th>
              <th className="w-[112px] px-2 text-right font-normal">{t('usage.cost')}</th>
              <th className="w-[96px] px-3 text-right font-normal">{t('usage.averageLatency')}</th>
            </tr>
          </thead>
          <tbody className={cn('text-[11px]', loading && 'opacity-55')}>
            {rows.map((row) => (
              <tr key={row.id} className="h-11 border-b border-hairline last:border-b-0">
                <td className="truncate px-3 text-fg" title={row.label}>{row.label}</td>
                <td className="px-2 text-right tabular-nums text-fg-muted">
                  {number(row.requestCount, locale)}
                </td>
                <td className="px-2 text-right tabular-nums text-fg-muted">
                  {percent(row.requestCount === 0 ? null : row.successCount / row.requestCount, locale)}
                </td>
                <td className="px-2 text-right text-[10px] tabular-nums text-fg-muted">
                  {t('usage.compactTokenBreakdown', {
                    input: compactNumber(row.inputTokens, locale),
                    cache: compactNumber(row.cacheReadTokens + row.cacheWriteTokens, locale),
                    output: compactNumber(row.outputTokens, locale)
                  })}
                </td>
                <td className="px-2 text-right tabular-nums text-fg">
                  {costs(row.costs, locale)}
                </td>
                <td className="px-3 text-right tabular-nums text-fg-muted">
                  {latency(row.averageLatencyMs, locale, t)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {!loading && rows.length === 0 && (
        <div className="flex h-[170px] items-center justify-center text-[12px] text-fg-faint">
          {t('usage.empty')}
        </div>
      )}
    </section>
  )
}

function ToolStats({
  rows,
  summary,
  loading,
  locale,
  t
}: {
  rows: readonly UsageDimensionStat[]
  summary: UsageSummary | null
  loading: boolean
  locale: Locale
  t: Translate
}): ReactNode {
  const withTools = rows.filter((row) => row.toolCalls > 0 || row.toolErrors > 0)
  return (
    <section className="mt-3">
      <div className="grid grid-cols-2 gap-2">
        <div className="rounded-[14px] bg-surface px-3.5 py-3">
          <p className="text-[10.5px] text-fg-faint">{t('usage.toolCalls')}</p>
          <p className="mt-1 text-[18px] font-semibold tabular-nums text-fg">
            {summary === null ? '—' : number(summary.toolCalls, locale)}
          </p>
        </div>
        <div className="rounded-[14px] bg-surface px-3.5 py-3">
          <p className="text-[10.5px] text-fg-faint">{t('usage.toolErrors')}</p>
          <p className="mt-1 text-[18px] font-semibold tabular-nums text-fg">
            {summary === null ? '—' : number(summary.toolErrors, locale)}
          </p>
        </div>
      </div>
      <div className="mt-2 overflow-hidden rounded-[12px] border border-border">
        <table className="w-full table-fixed border-collapse text-left">
          <thead className="text-[10.5px] text-fg-faint">
            <tr className="h-8 border-b border-hairline">
              <th className="px-3 font-normal">{t('common.model')}</th>
              <th className="w-[110px] px-3 text-right font-normal">{t('usage.toolCalls')}</th>
              <th className="w-[110px] px-3 text-right font-normal">{t('usage.toolErrors')}</th>
              <th className="w-[110px] px-3 text-right font-normal">{t('usage.requests')}</th>
            </tr>
          </thead>
          <tbody className={cn('text-[11px]', loading && 'opacity-55')}>
            {withTools.map((row) => (
              <tr key={row.id} className="h-10 border-b border-hairline last:border-b-0">
                <td className="truncate px-3 text-fg" title={row.label}>{row.label}</td>
                <td className="px-3 text-right tabular-nums text-fg-muted">{number(row.toolCalls, locale)}</td>
                <td className="px-3 text-right tabular-nums text-fg-muted">{number(row.toolErrors, locale)}</td>
                <td className="px-3 text-right tabular-nums text-fg-muted">{number(row.requestCount, locale)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {!loading && withTools.length === 0 && (
          <div className="flex h-[150px] items-center justify-center text-[12px] text-fg-faint">
            {t('usage.noToolCalls')}
          </div>
        )}
      </div>
    </section>
  )
}
