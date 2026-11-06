/**
 * Token 趋势(参考图第二张),按模型堆叠。
 *
 * 需求:趋势图要回答「这几天的量是哪个模型跑出来的」,不只是「一共多少」。
 * 原先只画一条 accent 总量线;现在每个模型一层面积,颜色与环形图 / 费用表
 * 按同一个 key 查(调用方传 `series`,颜色分配见 `colors.ts` 的 `modelColorMap`)。
 * 堆叠的顶就是总量 —— `toModelTrend` 保证 `byModel` 之和等于 `tokens`。
 *
 * ★ 配色一律传 `var(--color-*)` 字符串。recharts 把 `stroke` / `fill` 原样落成
 * SVG 属性,浏览器自己解析变量 —— 所以切主题时颜色自动跟着走,不需要
 * `getComputedStyle`,也不需要订阅主题变化。改成在 JS 里算色值就得自己订阅,
 * 而漏订阅的表现是「切主题后图表颜色不动」,只有肉眼能发现。
 *
 * ★ 默认的 `<Tooltip>` 背景是写死的白色,深色主题下是一块刺眼的白斑。
 * 所以这里给 `content` 传自定义节点 —— 不是为了好看,是为了它在深色下能读。
 */
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from 'recharts'
import { useI18n, type Locale, type Translate } from '../../../../i18n'
import { EmptyState } from '../../../../components/arc/empty-state/empty-state'
import { formatCompactNumber, formatDayLong, formatDayShort, formatNumber } from '../usage-format'
import type { ModelTrendPoint } from '../usage-model-trend'

/** 一层堆叠。`label` 已是最终文案(「其他」由调用方翻译好)。 */
export interface TrendSeries {
  key: string
  label: string
  color: string
}

/** tooltip 里最多列几个模型 —— 序列本身上限是 8 + 其他,这里只是兜底。 */
const TOOLTIP_ROWS = 9

function TrendTooltip({
  active,
  payload,
  series,
  locale,
  t
}: {
  active?: boolean
  payload?: { payload: ModelTrendPoint }[]
  series: readonly TrendSeries[]
  locale: Locale
  t: Translate
}): React.ReactNode {
  if (active !== true || payload === undefined || payload.length === 0) return null
  const point = payload[0]!.payload
  // 只列当天有量的模型、按量降序:堆叠图里细的那几层看不清,tooltip 是唯一能读数的地方
  const rows = series
    .map((item) => ({ ...item, tokens: point.byModel[item.key] ?? 0 }))
    .filter((item) => item.tokens > 0)
    .sort((a, b) => b.tokens - a.tokens)
    .slice(0, TOOLTIP_ROWS)
  return (
    <div className="min-w-[180px] rounded-[10px] border border-border bg-surface-raised px-2.5 py-1.5 shadow-lg">
      <p className="text-[11px] text-fg">{formatDayLong(point.day, locale)}</p>
      <p className="mt-0.5 text-[10.5px] tabular-nums text-fg-muted">
        {t('usage.activity.tooltip', {
          tokens: formatCompactNumber(point.tokens, locale),
          turns: formatNumber(point.requests, locale)
        })}
      </p>
      {rows.length > 0 && (
        <ul className="mt-1.5 space-y-0.5 border-t border-hairline pt-1.5">
          {rows.map((row) => (
            <li key={row.key} className="flex items-center gap-1.5 text-[10.5px]">
              <span
                className="size-2 shrink-0 rounded-full"
                style={{ backgroundColor: row.color }}
              />
              <span className="min-w-0 max-w-[180px] flex-1 truncate text-fg-muted">
                {row.label}
              </span>
              <span className="shrink-0 tabular-nums text-fg">
                {formatCompactNumber(row.tokens, locale)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

export function DailyTrendChart({
  points,
  series
}: {
  /** 已补洞、已按粒度聚合的点(`toModelTrend` 的输出)。 */
  points: readonly ModelTrendPoint[]
  /** 堆叠层,按排名传入 —— 第一个在最底层,最大的模型因此贴着横轴,读数最稳。 */
  series: readonly TrendSeries[]
}): React.ReactNode {
  const { t, locale } = useI18n()

  if (points.length === 0) return <EmptyState title={t('usage.trend.empty')} description="" />

  return (
    <div className="min-w-0">
      <div className="h-[220px] w-full">
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart
            data={points as ModelTrendPoint[]}
            margin={{ top: 6, right: 6, bottom: 0, left: 0 }}
          >
            <CartesianGrid stroke="var(--color-hairline)" strokeDasharray="3 3" vertical={false} />
            <XAxis
              dataKey="day"
              tickFormatter={(day: string) => formatDayShort(day, locale)}
              tick={{ fontSize: 10, fill: 'var(--color-fg-faint)' }}
              tickLine={false}
              axisLine={false}
              minTickGap={24}
            />
            <YAxis
              width={44}
              tickFormatter={(value: number) => formatCompactNumber(value, locale)}
              tick={{ fontSize: 10, fill: 'var(--color-fg-faint)' }}
              tickLine={false}
              axisLine={false}
            />
            <Tooltip
              cursor={{ stroke: 'var(--color-border)' }}
              content={<TrendTooltip series={series} locale={locale} t={t} />}
            />
            {series.map((item) => (
              <Area
                key={item.key}
                // ★ dataKey 用函数而不是字符串:recharts 把字符串当对象路径解析,
                // 模型名里的「.」(glm-5.3、gpt-5.6)会被拆成嵌套字段,取到 undefined,
                // 那一层静默消失,且堆叠顶和总量对不上
                dataKey={(point: ModelTrendPoint) => point.byModel[item.key] ?? 0}
                name={item.label}
                stackId="models"
                type="monotone"
                stroke={item.color}
                strokeWidth={1.4}
                fill={item.color}
                fillOpacity={0.22}
                // 进场动画在切换时间范围 / 粒度时整张图重长一遍,多层叠在一起很晃
                isAnimationActive={false}
                // 每日一个点、53 周就是 371 个点 —— 画出来是一条毛毛虫
                dot={false}
                activeDot={{ r: 2.5, fill: item.color, stroke: 'var(--color-canvas)' }}
              />
            ))}
          </AreaChart>
        </ResponsiveContainer>
      </div>

      {/* 图例紧贴图表:环形图在下方另一块面板里,窄屏时要滚一屏才看得到,
          不在这里放一份就只能靠猜颜色 */}
      <ul
        className="mt-2 flex flex-wrap gap-x-3 gap-y-1 pl-11 text-[10.5px] text-fg-muted"
        aria-label={t('usage.models.legendLabel')}
      >
        {series.map((item) => (
          <li key={item.key} className="flex min-w-0 max-w-[180px] items-center gap-1.5">
            <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: item.color }} />
            <span className="truncate" title={item.label}>
              {item.label}
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}
