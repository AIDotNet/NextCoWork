/**
 * Token 数量的显示格式化。
 *
 * 和 `duration.ts` 同一个理由放在 shared/:主进程的日志、聊天页的用量气泡、
 * 将来的 run 摘要要显示同一个数字,三处各写一份 `(n/1000).toFixed(1)` 的结果是
 * 同一个数在不同地方显示成 `53.6K` / `53.56K` / `54K`,看着像三个不同的数。
 */

/**
 * 53_561 → `53.56K`,1_234_567_890 → `1.23B`。
 *
 * 规则:**带上单位就固定两位小数**(`1.00K`、`999.50K`),尾随的 0 保留 ——
 * 一位小数那套(`53.6K`)在 1K 附近会丢掉肉眼看得见的差别,而「有时一位、有时两位」
 * 会让同一列数字对不齐。三位数以内(1000 以下)没有单位,仍原样显示整数。
 *
 * ★ **进位必须在取整之后判断。** `999_999` 按 K 算出来的尾数取两位是 `1000.00`,
 * 直接拼就是 `1000.00K` —— 合法数字组成的非法读数,得晋到 `1M`。`duration.ts` 的
 * `formatDuration` 里那两处 carry(`1m60s`)踩的是同一个坑。
 *
 * ★ **不要换成下面这三个现成的**,它们都不适用:
 * - `Intl` 的 `notation: 'compact'`(设置页用量表在用)在 zh-CN 下出的是
 *   `5.4万` —— 中文区的 compact 单位是万/亿,不是 K/M/B。
 * - `formatContextWindow`(`context-management.ts`)是给**窗口大小**用的:
 *   K 档尾数 ≥10 就取整,`53_561` 会变成 `54K`,丢掉用量数该有的精度;也没有 B 档。
 * - `compactTokens`(`settings/pages/model/pricing-table.ts`)要求整除,
 *   而且 chat 视图不该反向依赖 settings 目录。
 */
export function formatTokenCount(n: number): string {
  if (!Number.isFinite(n)) return '—'
  if (n < 0) return `-${formatTokenCount(-n)}`

  let value = n
  let unit = ''
  for (const [threshold, suffix] of UNITS) {
    if (n < threshold) break
    value = n / threshold
    unit = suffix
  }
  if (unit === '') return String(Math.round(value))

  // 保留 `toFixed` 的字符串(不转回 Number)才有两位小数的定长读数:`1.00K` 不能变 `1K`
  const mantissa = value.toFixed(2)
  // ★ 取整之后才知道进没进位:999_999 在这里的 mantissa 是 "1000.00",该晋一档。
  return Number(mantissa) >= 1000 ? promote(unit) : `${mantissa}${unit}`
}

/** 从小到大,`formatTokenCount` 的循环靠这个顺序选中**最后一个**够得着的档。 */
const UNITS: ReadonlyArray<readonly [number, string]> = [
  [1_000, 'K'],
  [1_000_000, 'M'],
  [1_000_000_000, 'B']
]

/**
 * 进位后的那一档。`B` 已经是最大档 —— 再大只能原样写 `1000B`,
 * 而那个量级的 token 数在这个应用里不会出现(真出现了,读起来也仍然是对的)。
 */
function promote(unit: string): string {
  const next = UNITS[UNITS.findIndex(([, suffix]) => suffix === unit) + 1]
  return next === undefined ? `1000${unit}` : `1${next[1]}`
}
