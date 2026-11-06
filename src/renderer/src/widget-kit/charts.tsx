/**
 * `nextcowork/charts` —— 预设好宿主配色与样式的 recharts 图表。
 *
 * 需求:模型画图最常犯的不是数据错,是**样式**错 —— 默认的粗网格、彩虹配色、
 * 和对话背景打架的白底提示框。这里把这些定死:只有横向细网格、轴文字用最淡的那档、
 * 系列色取主题的 `--color-chart-1..8`(跟着换色器走)、提示框用宿主的浮层底色。
 * 模型只需要说"哪一列是 x、哪几列是系列"。
 *
 * 要更细的控制,`Recharts` 命名空间里是原始的 recharts。
 */
import type { ReactNode } from 'react'
import {
  Area,
  AreaChart as RAreaChart,
  Bar,
  BarChart as RBarChart,
  CartesianGrid,
  Cell,
  Legend,
  Line,
  LineChart as RLineChart,
  Pie,
  PieChart as RPieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from 'recharts'

export * as Recharts from 'recharts'

/**
 * 系列色。第 9 个起循环 —— 超过 8 个系列的图本身就该换一种画法。
 *
 * ★ 必须写成**字面量**:Tailwind v4 会删掉源码里没出现过的主题变量,而拼出来的
 *   `--color-chart-${n}` 扫描器看不见 —— 症状是第二个系列起全是黑的(实测)。
 *   兜底色同 theme.css 的深色值,万一变量仍缺也不至于画成黑色。
 */
export const CHART_COLORS = [
  'var(--color-chart-1, #0a84ff)',
  'var(--color-chart-2, #ff9f0a)',
  'var(--color-chart-3, #bf5af2)',
  'var(--color-chart-4, #30d158)',
  'var(--color-chart-5, #ff6961)',
  'var(--color-chart-6, #64d2ff)',
  'var(--color-chart-7, #ff375f)',
  'var(--color-chart-8, #ffd60a)'
]

export type Series = string | { key: string; label?: string; color?: string }

interface NormalizedSeries {
  key: string
  label: string
  color: string
}

function normalize(series: readonly Series[]): NormalizedSeries[] {
  return series.map((entry, index) => {
    const spec = typeof entry === 'string' ? { key: entry } : entry
    return {
      key: spec.key,
      label: spec.label ?? spec.key,
      color: spec.color ?? CHART_COLORS[index % CHART_COLORS.length] ?? 'var(--color-accent)'
    }
  })
}

type Formatter = (value: number) => string

const AXIS_TICK = { fill: 'var(--color-fg-faint)', fontSize: 11 }

const TOOLTIP_PROPS = {
  contentStyle: {
    background: 'var(--color-surface-menu, var(--color-surface-raised))',
    border: 'none',
    borderRadius: 8,
    boxShadow: 'var(--shadow-menu, 0 4px 16px rgba(0,0,0,.18))',
    fontSize: 12,
    color: 'var(--color-fg)'
  },
  labelStyle: { color: 'var(--color-fg-muted)', marginBottom: 4 },
  itemStyle: { padding: 0 },
  cursor: { fill: 'var(--color-tint)', stroke: 'var(--color-hairline)' }
}

const LEGEND_PROPS = {
  iconType: 'circle' as const,
  iconSize: 8,
  wrapperStyle: { fontSize: 12, color: 'var(--color-fg-muted)', paddingTop: 8 }
}

interface CartesianProps<Row> {
  data: Row[]
  /** x 轴取哪一列 */
  x: keyof Row & string
  /** 系列:列名,或 `{ key, label, color }` */
  series: Series[]
  height?: number
  /** y 轴与提示框里数值的格式化 */
  format?: Formatter
  /** 显示图例。缺省:多于一个系列时显示 */
  legend?: boolean
}

function axes(x: string, format: Formatter | undefined, layout: 'horizontal' | 'vertical' = 'horizontal'): ReactNode[] {
  const value = {
    tick: AXIS_TICK,
    axisLine: false,
    tickLine: false,
    width: 48,
    ...(format === undefined ? {} : { tickFormatter: format })
  }
  const category = { dataKey: x, tick: AXIS_TICK, axisLine: { stroke: 'var(--color-hairline)' }, tickLine: false, minTickGap: 12 }
  return layout === 'horizontal'
    ? [<XAxis key="x" {...category} />, <YAxis key="y" {...value} />]
    : [<XAxis key="x" type="number" {...value} />, <YAxis key="y" type="category" {...category} width={96} />]
}

function grid(layout: 'horizontal' | 'vertical' = 'horizontal'): ReactNode {
  return <CartesianGrid stroke="var(--color-hairline)" vertical={layout === 'vertical'} horizontal={layout === 'horizontal'} />
}

function tooltip(format: Formatter | undefined): ReactNode {
  return <Tooltip {...TOOLTIP_PROPS} {...(format === undefined ? {} : { formatter: (v: unknown) => (typeof v === 'number' ? format(v) : String(v)) })} />
}

export function LineChart<Row extends Record<string, unknown>>({
  data,
  x,
  series,
  height = 240,
  format,
  legend,
  curve = 'monotone',
  dots = false
}: CartesianProps<Row> & { curve?: 'monotone' | 'linear' | 'step'; dots?: boolean }): ReactNode {
  const list = normalize(series)
  return (
    <ResponsiveContainer width="100%" height={height}>
      <RLineChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
        {grid()}
        {axes(x, format)}
        {tooltip(format)}
        {(legend ?? list.length > 1) && <Legend {...LEGEND_PROPS} />}
        {list.map((s) => (
          <Line key={s.key} type={curve} dataKey={s.key} name={s.label} stroke={s.color} strokeWidth={2} dot={dots} activeDot={{ r: 4 }} isAnimationActive={false} />
        ))}
      </RLineChart>
    </ResponsiveContainer>
  )
}

export function AreaChart<Row extends Record<string, unknown>>({
  data,
  x,
  series,
  height = 240,
  format,
  legend,
  stacked = false
}: CartesianProps<Row> & { stacked?: boolean }): ReactNode {
  const list = normalize(series)
  return (
    <ResponsiveContainer width="100%" height={height}>
      <RAreaChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
        {grid()}
        {axes(x, format)}
        {tooltip(format)}
        {(legend ?? list.length > 1) && <Legend {...LEGEND_PROPS} />}
        {list.map((s) => (
          <Area
            key={s.key}
            type="monotone"
            dataKey={s.key}
            name={s.label}
            stroke={s.color}
            fill={s.color}
            fillOpacity={0.16}
            strokeWidth={2}
            isAnimationActive={false}
            {...(stacked ? { stackId: 'stack' } : {})}
          />
        ))}
      </RAreaChart>
    </ResponsiveContainer>
  )
}

export function BarChart<Row extends Record<string, unknown>>({
  data,
  x,
  series,
  height = 240,
  format,
  legend,
  stacked = false,
  horizontal = false
}: CartesianProps<Row> & { stacked?: boolean; horizontal?: boolean }): ReactNode {
  const list = normalize(series)
  const layout = horizontal ? 'vertical' : 'horizontal'
  return (
    <ResponsiveContainer width="100%" height={height}>
      <RBarChart data={data} layout={layout} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
        {grid(layout)}
        {axes(x, format, layout)}
        {tooltip(format)}
        {(legend ?? list.length > 1) && <Legend {...LEGEND_PROPS} />}
        {list.map((s) => (
          <Bar
            key={s.key}
            dataKey={s.key}
            name={s.label}
            fill={s.color}
            radius={horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0]}
            maxBarSize={36}
            isAnimationActive={false}
            {...(stacked ? { stackId: 'stack' } : {})}
          />
        ))}
      </RBarChart>
    </ResponsiveContainer>
  )
}

export function PieChart<Row extends Record<string, unknown>>({
  data,
  name,
  value,
  height = 240,
  donut = true,
  format,
  legend = true
}: {
  data: Row[]
  /** 扇区名取哪一列 */
  name: keyof Row & string
  /** 扇区值取哪一列 */
  value: keyof Row & string
  height?: number
  donut?: boolean
  format?: Formatter
  legend?: boolean
}): ReactNode {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <RPieChart>
        {tooltip(format)}
        {legend && <Legend {...LEGEND_PROPS} />}
        <Pie
          data={data}
          dataKey={value}
          nameKey={name}
          innerRadius={donut ? '58%' : 0}
          outerRadius="82%"
          paddingAngle={donut ? 2 : 0}
          stroke="none"
          isAnimationActive={false}
        >
          {data.map((_, index) => (
            <Cell key={index} fill={CHART_COLORS[index % CHART_COLORS.length]} />
          ))}
        </Pie>
      </RPieChart>
    </ResponsiveContainer>
  )
}

/** 一条迷你趋势线,放在 `Stat` 旁边用。没有轴、没有提示框。 */
export function Sparkline({ data, color = 'var(--color-accent)', height = 32, width = 96 }: { data: number[]; color?: string; height?: number; width?: number }): ReactNode {
  const rows = data.map((v, i) => ({ i, v }))
  return (
    <div style={{ width, height }}>
      <ResponsiveContainer width="100%" height="100%">
        <RLineChart data={rows} margin={{ top: 2, right: 2, bottom: 2, left: 2 }}>
          <Line type="monotone" dataKey="v" stroke={color} strokeWidth={1.5} dot={false} isAnimationActive={false} />
        </RLineChart>
      </ResponsiveContainer>
    </div>
  )
}
