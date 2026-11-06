/**
 * 按模型的费用统计。
 *
 * ## 两件必须在界面上说清楚的事
 *
 * 1. **分币种。** 每个币种一张独立的表,各自合计。把 USD 和 CNY 的 micros 加起来
 *    会得到一个没有单位的数,而它长得和正常金额一模一样。
 * 2. **未计价 ≠ 免费。** 查不到定价的请求不进合计,页脚必须把条数说出来 ——
 *    否则「少算的钱」和「省下的钱」在界面上完全无法区分。
 *
 * 费用本身是记账时**冻结**在 `usage_records.cost_micros` 里的,事后改定价表不改
 * 历史账。所以这里只是求和,不做任何重算。
 */
import { useI18n } from '../../../../i18n'
import { EmptyState } from '../../../../components/arc/empty-state/empty-state'
import { formatCostMicros, formatNumber, formatPercent } from '../usage-format'
import type { CostBreakdown } from '../usage-overview'
import { colorOf } from './colors'

export function CostByModel({
  breakdown,
  colors
}: {
  breakdown: CostBreakdown
  /**
   * key → 颜色,和环形图 / 趋势图共用一张表(`modelColorMap`)。
   * 原先这里按「本表内的序号」取色,而本表按金额排、环形图按 token 排 ——
   * 同一个模型在两块面板里颜色不同。查不到的(不在前 N 名)给「其他」的灰。
   */
  colors: ReadonlyMap<string, string>
}): React.ReactNode {
  const { t, locale } = useI18n()

  if (breakdown.groups.length === 0) {
    return (
      <EmptyState
        title={t('usage.cost.empty')}
        description={
          breakdown.unpricedRequests > 0
            ? t('usage.cost.unpricedHint', {
                count: formatNumber(breakdown.unpricedRequests, locale)
              })
            : ''
        }
      />
    )
  }

  return (
    <div className="space-y-4">
      {breakdown.groups.map((group) => (
        <section key={group.currency} className="min-w-0">
          <header className="mb-2 flex items-baseline justify-between gap-2">
            <span className="text-[11px] text-fg-faint">{group.currency}</span>
            <span className="text-[13px] font-semibold tabular-nums text-fg">
              {formatCostMicros(group.totalMicros, group.currency, locale)}
            </span>
          </header>

          <ul className="space-y-1.5">
            {group.rows.map((row) => (
              <li key={row.key} className="min-w-0">
                <div className="flex items-center gap-2 text-[11px]">
                  <span
                    className="size-2 shrink-0 rounded-full"
                    style={{ backgroundColor: colorOf(colors, row.key) }}
                  />
                  <span className="min-w-0 flex-1 truncate text-fg" title={row.label}>
                    {row.label}
                  </span>
                  {/* 裸数字「2,610」看不出是请求数还是 token 数,带上单位 */}
                  <span className="shrink-0 tabular-nums text-fg-faint">
                    {t('usage.models.requests', { count: formatNumber(row.requests, locale) })}
                  </span>
                  <span className="w-24 shrink-0 text-right tabular-nums text-fg">
                    {formatCostMicros(row.micros, group.currency, locale)}
                  </span>
                  <span className="w-12 shrink-0 text-right tabular-nums text-fg-faint">
                    {formatPercent(row.share, locale)}
                  </span>
                </div>
                {/* 占比条。宽度直接用 share,不再取 max 归一化 —— 这里比较的是
                    「占总花费多少」,不是「相对最贵的那个多少」 */}
                <div className="mt-1 ml-4 h-[3px] overflow-hidden rounded-pill bg-tint">
                  <div
                    className="h-full rounded-pill"
                    style={{
                      width: `${Math.max(row.share * 100, row.micros > 0 ? 1.5 : 0)}%`,
                      backgroundColor: colorOf(colors, row.key)
                    }}
                  />
                </div>
              </li>
            ))}
          </ul>
        </section>
      ))}

      <p className="text-[10px] leading-[1.4] text-fg-faint">
        {breakdown.groups.length > 1 && `${t('usage.cost.currencyNote')} · `}
        {breakdown.unpricedRequests > 0
          ? t('usage.cost.unpricedHint', {
              count: formatNumber(breakdown.unpricedRequests, locale)
            })
          : t('usage.frozenPriceHint')}
      </p>
    </div>
  )
}
