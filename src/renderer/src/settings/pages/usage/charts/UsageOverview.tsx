/**
 * 概览区的装配层 —— 拉数据、管状态,图表本身一概不画。
 *
 * ## 为什么拉两次
 *
 * 热力图固定看最近 53 周,和顶部的时间范围**不联动**(范围选「近 24 小时」时
 * 一张只有一格的热力图毫无意义)。其余四块图跟随范围。所以:
 *
 * - `allBuckets`:全历史,喂热力图
 * - `buckets`:窗口内,喂指标卡 / 趋势图 / 环形图 / 费用表
 *
 * 窗口那份**不是**在前端从全量里切出来的。窗口边界是毫秒,而桶的 `day` 是本地
 * 日期字符串,在前端切就等于把主进程的分桶口径再实现一遍 —— 两处实现一旦有出入,
 * 同一天的数字会在概览和日志表之间对不上。让主进程切,前端只负责画。
 */
import { useEffect, useMemo, useState } from 'react'
import type {
  UsageActivityStats,
  UsageDailyBucket,
  UsageWindow
} from '../../../../../../shared/domain/usage'
import { localDayOf } from '../../../../../../shared/domain/usage-activity'
import { EmptyState } from '../../../../components/arc/empty-state/empty-state'
import SegmentedControl from '../../../../components/arc/segmented-control/segmented-control'
import { useI18n } from '../../../../i18n'
import { getUsageActivityStats, getUsageDailySeries } from '../../../../services/usage'
import { formatCompactNumber } from '../usage-format'
import {
  buildHeatmap,
  OTHERS_KEY,
  toCostBreakdown,
  toDayTotals,
  toModelShares,
  totalsOf,
  type UsageGranularity
} from '../usage-overview'
import { toModelTrend } from '../usage-model-trend'
import { ActivityHeatmap } from './ActivityHeatmap'
import { colorOf, modelColorMap } from './colors'
import { CostByModel } from './CostByModel'
import { DailyTrendChart } from './DailyTrendChart'
import { ModelUsageDonut, type DonutItem } from './ModelUsageDonut'
import { OverviewCards } from './OverviewCards'

const GRANULARITIES: readonly UsageGranularity[] = ['daily', 'weekly', 'cumulative']

function Panel({
  title,
  actions,
  children
}: {
  title: string
  actions?: React.ReactNode
  children: React.ReactNode
}): React.ReactNode {
  return (
    <section className="min-w-0 rounded-[18px] bg-surface p-4">
      <header className="mb-3 flex min-h-7 items-center justify-between gap-3">
        <h3 className="text-[12px] font-medium text-fg">{title}</h3>
        {actions}
      </header>
      {children}
    </section>
  )
}

export function UsageOverview({ usageWindow }: { usageWindow: UsageWindow }): React.ReactNode {
  const { t, locale } = useI18n()
  const [buckets, setBuckets] = useState<UsageDailyBucket[]>([])
  const [allBuckets, setAllBuckets] = useState<UsageDailyBucket[]>([])
  const [activity, setActivity] = useState<UsageActivityStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [granularity, setGranularity] = useState<UsageGranularity>('daily')

  useEffect(() => {
    let active = true
    setLoading(true)
    void Promise.all([
      getUsageDailySeries(usageWindow),
      getUsageDailySeries({ to: usageWindow.to }),
      getUsageActivityStats()
    ])
      .then(([windowed, all, stats]) => {
        if (!active) return
        setBuckets(windowed)
        setAllBuckets(all)
        setActivity(stats)
      })
      .catch((error: unknown) => {
        if (!active) return
        console.error('[usage] failed to load overview series', error)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [usageWindow])

  const totals = useMemo(() => totalsOf(buckets), [buckets])
  const shares = useMemo(() => toModelShares(buckets), [buckets])
  const costs = useMemo(() => toCostBreakdown(buckets), [buckets])

  // 需求:同一个模型在环形图、趋势图、费用表里是同一个颜色。颜色只在这里按
  // token 排名分配一次,三块图都查这张表 —— 各自按序号取色就会对不上。
  const colors = useMemo(() => modelColorMap(shares.map((s) => s.key), OTHERS_KEY), [shares])
  const items = useMemo<DonutItem[]>(
    () =>
      shares.map((share) => ({
        key: share.key,
        label: share.key === OTHERS_KEY ? t('usage.models.others') : share.label,
        color: colorOf(colors, share.key),
        tokens: share.tokens,
        requests: share.requests,
        share: share.share
      })),
    [shares, colors, t]
  )

  // 趋势图先补空洞再按粒度再聚合:没有用量的日子必须是一个 0 点,否则折线会从
  // 前一天直接斜到后一天,把「歇了三天」画成「缓慢下降」。补洞与聚合原先在这里
  // 直接调 `fillDayGaps` + `applyGranularity`;现在趋势图要按模型堆叠,改由
  // `toModelTrend` 在内部调同样两步,口径不变。
  const trend = useMemo(
    () => toModelTrend(buckets, shares.map((s) => s.key), OTHERS_KEY, granularity),
    [buckets, shares, granularity]
  )

  const heatmap = useMemo(
    () => buildHeatmap(toDayTotals(allBuckets), localDayOf(usageWindow.to - 1)),
    [allBuckets, usageWindow.to]
  )

  const empty = !loading && buckets.length === 0

  return (
    // ★ `@container`:下面的栅格按**这块内容区自己的宽度**折行,而不是按视口。
    // 这一页住在设置浮层里,内容区只有视口一半左右 —— 原先用 `min-[900px]:` 这类
    // 视口断点,视口一宽就强行两栏 / 六栏,面板被挤到三百来像素,模型名全被截断。
    <div className="@container space-y-3">
      {/* 需求:概览合并了其它设备同步来的数据、下方请求日志只含本机,两处数字会对不上 ——
          不把口径说出来,用户只会以为统计算错了。没有其它设备时不画 */}
      {activity !== null && activity.syncedDevices > 0 && (
        <p className="px-1 text-[11px] leading-[1.6] text-fg-faint">
          {t('usage.sync.merged', { count: activity.syncedDevices })}
        </p>
      )}
      <OverviewCards totals={totals} activity={activity} loading={loading} />

      {/* 热力图固定看最近一年、不受粒度影响,所以这里不放粒度切换 ——
          原先切换器挂在这块面板上,实际只改下面的趋势图,点了热力图纹丝不动 */}
      <Panel title={t('usage.activity.title')}>
        <ActivityHeatmap grid={heatmap} />
      </Panel>

      {empty ? (
        <Panel title={t('usage.overview.title')}>
          <EmptyState title={t('usage.overview.empty')} description="" />
        </Panel>
      ) : (
        <>
          <Panel
            title={t('usage.trend.title')}
            actions={
              <div className="flex items-center gap-3">
                <span className="text-[10.5px] tabular-nums text-fg-faint">
                  {formatCompactNumber(totals.tokens, locale)}
                </span>
                <SegmentedControl
                  value={granularity}
                  options={GRANULARITIES.map((item) => ({
                    value: item,
                    label: t(`usage.granularity.${item}` as never)
                  }))}
                  onValueChange={(v) => setGranularity(v as UsageGranularity)}
                  label={t('usage.granularityLabel')}
                />
              </div>
            }
          >
            <DailyTrendChart points={trend} series={items} />
          </Panel>

          <div className="grid gap-3 @min-[980px]:grid-cols-2">
            <Panel title={t('usage.models.title')}>
              <ModelUsageDonut items={items} totalTokens={totals.tokens} />
            </Panel>
            <Panel title={t('usage.cost.title')}>
              <CostByModel breakdown={costs} colors={colors} />
            </Panel>
          </div>
        </>
      )}
    </div>
  )
}
