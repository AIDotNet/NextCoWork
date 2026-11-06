/**
 * 概览指标卡 —— 参考图顶部那一排。
 *
 * 复用 `UsagePage` 里 `SummaryCards` 的既有样式(`rounded-[18px] bg-surface` +
 * `grid-cols-2 min-[760px]:grid-cols-4`),同一页上两排卡不该长得不一样。
 * 卡片外观仍与之一致;栅格断点已改成容器查询(理由见下方 grid 处),
 * `SummaryCards` 那排还是视口断点,没有一并改。
 */
import type { UsageActivityStats } from '../../../../../../shared/domain/usage'
import { useI18n } from '../../../../i18n'
import {
  formatCompactNumber,
  formatCosts,
  formatDayLong,
  formatDuration,
  formatNumber
} from '../usage-format'
import type { UsageTotals } from '../usage-overview'

export function OverviewCards({
  totals,
  activity,
  loading
}: {
  totals: UsageTotals
  activity: UsageActivityStats | null
  loading: boolean
}): React.ReactNode {
  const { t, locale } = useI18n()

  const cards: { key: string; title: string; value: string; detail: string }[] = [
    {
      key: 'tokens',
      title: t('usage.metric.totalTokens'),
      value: formatCompactNumber(totals.tokens, locale),
      detail: t('usage.metric.acrossModels', { count: formatNumber(totals.modelCount, locale) })
    },
    {
      key: 'cost',
      title: t('usage.metric.totalCost'),
      // ★ 多币种在这里是并列显示而不是相加 —— formatCosts 用 `+` 连接
      value: formatCosts(totals.costs, locale),
      detail:
        totals.unpricedRequests > 0
          ? t('usage.cost.unpricedHint', {
              count: formatNumber(totals.unpricedRequests, locale)
            })
          : t('usage.cost.currencyNote')
    },
    {
      key: 'peak',
      title: t('usage.metric.peakTokens'),
      value: activity === null ? '—' : formatCompactNumber(activity.peakDayTokens, locale),
      detail:
        activity === null || activity.peakDay === null
          ? ''
          : t('usage.metric.onDay', { day: formatDayLong(activity.peakDay, locale) })
    },
    {
      key: 'chat',
      title: t('usage.metric.longestChat'),
      value: activity === null ? '—' : formatDuration(activity.longestChatMs, locale, t),
      // 这个指标会随「清理历史」变小,不说明的话数字变小看起来就是统计坏了
      detail: t('usage.metric.chatHint')
    },
    {
      key: 'current',
      title: t('usage.metric.currentStreak'),
      value:
        activity === null
          ? '—'
          : t('usage.metric.days', { days: formatNumber(activity.currentStreak, locale) }),
      detail: ''
    },
    {
      key: 'longest',
      title: t('usage.metric.longestStreak'),
      value:
        activity === null
          ? '—'
          : t('usage.metric.days', { days: formatNumber(activity.longestStreak, locale) }),
      detail: ''
    }
  ]

  return (
    // 断点是容器查询(`@container` 挂在 UsageOverview 根上),不是视口 ——
    // 原先 `min-[1100px]:grid-cols-6` 在宽视口下把六张卡塞进半宽的设置浮层,
    // 每张一百来像素,「US$3,326.94」这类值被截断
    <div className="grid grid-cols-2 gap-2 @min-[520px]:grid-cols-3 @min-[1040px]:grid-cols-6">
      {cards.map((card) => (
        <section
          key={card.key}
          className="min-h-[104px] rounded-[18px] bg-surface px-3.5 py-3"
          aria-busy={loading}
        >
          <p className="text-[11px] text-fg-faint">{card.title}</p>
          <p
            className="mt-1 truncate text-[18px] font-semibold tabular-nums text-fg"
            title={card.value}
          >
            {card.value}
          </p>
          {card.detail !== '' && (
            // 六张卡并排,说明长短差很多。不钳住的话最长的那张会把整行撑高一截,
            // 而卡片高度本来是这排的视觉基线
            <p
              className="mt-1 line-clamp-2 text-[10.5px] leading-[1.35] text-fg-muted"
              title={card.detail}
            >
              {card.detail}
            </p>
          )}
        </section>
      ))}
    </div>
  )
}
