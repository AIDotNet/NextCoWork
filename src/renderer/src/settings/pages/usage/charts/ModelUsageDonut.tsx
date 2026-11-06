/**
 * 模型用量环形图 + 图例(参考图第二张右半)。
 *
 * 配色原先来自 `colors.ts` 的 accent 派生色阶(最大的一片最浓);8 个模型时相邻
 * 两片分不开,已改为分类色 —— 理由见 `colors.ts` 头注释。颜色由调用方按 key
 * 分配好传进来(`items[].color`),**这里不自己按序号取色**:否则同一个模型在
 * 这里和费用表、趋势图里会是不同的颜色。
 *
 * 需求:图例要能读全模型名。原先图例挤在环形图右侧、断点按**视口**算
 * (`min-[720px]:`),而设置浮层只有视口一半宽 —— 视口够宽、面板却很窄,
 * 图例只剩一百来像素,模型名全被截成「claude-o…」。现在用容器查询,按面板
 * 自己的宽度决定左右排还是上下排。
 *
 * 悬停 / 聚焦图例行或扇区时高亮该模型、中心改显示它的数值 —— 键盘可达,
 * 与鼠标路径等价;不改变任何数据,只是读数更方便。
 */
import { useState } from 'react'
import { Cell, Pie, PieChart, ResponsiveContainer } from 'recharts'
import { useI18n } from '../../../../i18n'
import { EmptyState } from '../../../../components/arc/empty-state/empty-state'
import { cn } from '../../../../lib/cn'
import { formatCompactNumber, formatNumber, formatPercent } from '../usage-format'
import type { TrendSeries } from './DailyTrendChart'

/** 一个模型在图例里的全部信息。`label` 已是最终文案。 */
export interface DonutItem extends TrendSeries {
  tokens: number
  requests: number
  /** 0–1,分母是所有模型的 token 合计。 */
  share: number
}

export function ModelUsageDonut({
  items,
  totalTokens
}: {
  /** 按 token 降序,尾部已合并成「其他」(`toModelShares`)。 */
  items: readonly DonutItem[]
  totalTokens: number
}): React.ReactNode {
  const { t, locale } = useI18n()
  const [activeKey, setActiveKey] = useState<string | null>(null)

  if (items.length === 0) return <EmptyState title={t('usage.models.empty')} description="" />

  const active = activeKey === null ? undefined : items.find((item) => item.key === activeKey)
  const dimmed = (key: string): boolean => activeKey !== null && activeKey !== key

  return (
    <div className="@container">
      <div className="flex flex-col items-center gap-5 @min-[480px]:flex-row @min-[480px]:items-start">
        <div className="relative size-[180px] shrink-0">
          <ResponsiveContainer width="100%" height="100%">
            <PieChart>
              <Pie
                data={items as DonutItem[]}
                dataKey="tokens"
                nameKey="label"
                innerRadius={58}
                outerRadius={84}
                paddingAngle={1.5}
                stroke="var(--color-surface)"
                strokeWidth={1.5}
                // 进场动画在切换时间范围时会整圈重画一次,晃眼
                isAnimationActive={false}
                onMouseEnter={(_, index) => setActiveKey(items[index]?.key ?? null)}
                onMouseLeave={() => setActiveKey(null)}
              >
                {items.map((item) => (
                  <Cell
                    key={item.key}
                    fill={item.color}
                    fillOpacity={dimmed(item.key) ? 0.28 : 1}
                  />
                ))}
              </Pie>
            </PieChart>
          </ResponsiveContainer>

          {/* 中心总量。★ 用绝对定位叠在图上而不是 recharts 的 label —— 后者在
              容器缩放时会跟着变字号 */}
          <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center px-12 text-center">
            <span className="max-w-full truncate text-[10px] text-fg-faint">
              {active === undefined ? t('usage.models.total') : active.label}
            </span>
            <span className="text-[15px] font-semibold tabular-nums text-fg">
              {formatCompactNumber(active === undefined ? totalTokens : active.tokens, locale)}
            </span>
            {active !== undefined && (
              <span className="text-[10px] tabular-nums text-fg-muted">
                {formatPercent(active.share, locale)}
              </span>
            )}
          </div>
        </div>

        <ul
          className="w-full min-w-0 flex-1 space-y-0.5"
          aria-label={t('usage.models.legendLabel')}
        >
          {items.map((item) => (
            <li
              key={item.key}
              tabIndex={0}
              onMouseEnter={() => setActiveKey(item.key)}
              onMouseLeave={() => setActiveKey(null)}
              onFocus={() => setActiveKey(item.key)}
              onBlur={() => setActiveKey(null)}
              className={cn(
                'rounded-[8px] px-2 py-1 outline-none transition-opacity focus-visible:ring-1 focus-visible:ring-accent motion-reduce:transition-none',
                activeKey === item.key && 'bg-tint',
                dimmed(item.key) && 'opacity-55'
              )}
            >
              <div className="flex items-center gap-2 text-[11px]">
                <span
                  className="size-2 shrink-0 rounded-full"
                  style={{ backgroundColor: item.color }}
                />
                <span className="min-w-0 flex-1 truncate text-fg" title={item.label}>
                  {item.label}
                </span>
                <span className="hidden shrink-0 tabular-nums text-fg-faint @min-[360px]:inline">
                  {t('usage.models.requests', { count: formatNumber(item.requests, locale) })}
                </span>
                <span className="w-14 shrink-0 text-right tabular-nums text-fg-muted">
                  {formatCompactNumber(item.tokens, locale)}
                </span>
                <span className="w-12 shrink-0 text-right tabular-nums text-fg-faint">
                  {formatPercent(item.share, locale)}
                </span>
              </div>
              {/* 占比条:颜色与扇区一致,扇区太细时靠它读相对大小 */}
              <div className="mt-1 ml-4 h-[3px] overflow-hidden rounded-pill bg-tint">
                <div
                  className="h-full rounded-pill"
                  style={{
                    width: `${Math.max(item.share * 100, item.tokens > 0 ? 1.5 : 0)}%`,
                    backgroundColor: item.color
                  }}
                />
              </div>
            </li>
          ))}
        </ul>
      </div>
    </div>
  )
}
