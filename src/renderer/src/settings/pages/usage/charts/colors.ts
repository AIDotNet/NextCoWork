/**
 * 图表配色。
 *
 * ## 两套色:「强度」走 accent 派生,「类别」走分类色 token
 *
 * 原先这里**全是 `color-mix`**,模型环形图和费用条也用 accent 梯度,理由是:
 * 主题只有**一个** accent,而且深浅两套里它是反的(深色亮绿 `#36d285`,
 * 浅色墨绿 `#2d4739`)。`theme.css` 里 `bg-accent/10` 那段注释论证过同一件事:
 * 派生而不是新增 —— 换色器换掉 accent 时,整套梯度自己跟着走;新增的分类色
 * 则会在换色后和界面其余部分脱节,而且没有任何地方会报错。
 *
 * 这条理由对**强度**编码(热力图:多 / 少)仍然成立,所以 `heatColor` 没动。
 * 但它对**类别**编码(这一块是哪个模型)不成立:单色相梯度到 8 个模型时相邻两片
 * 只差十来个百分点,图例八个点看着是同一个色,用户根本对不上号 —— 这是一次
 * 明确的产品需求(「更丰富的色彩展示模型信息」)推翻的。所以模型改用
 * `--color-chart-1..8` + `--color-chart-other`(定义与取值理由见 `theme.css`),
 * 它们刻意不跟换色器走:换 accent 不该改变「哪个模型是蓝的」。
 *
 * ★ 这些字符串直接进 SVG 的 `fill` / `stroke` 属性,不经过任何 JS 计算,
 * 所以不需要 `getComputedStyle` 把 token 解析成具体色值 —— 浏览器自己会算,
 * 主题一变就跟着变。一旦改成在 JS 里算色值,就得自己订阅主题变化,
 * 而漏订阅的表现是「切主题后图表颜色不动」,只有肉眼能发现。
 */

/** 热力图五档。第 0 档是「当天没有活动」,用槽色而不是极淡的 accent。 */
const HEAT_MIX = [0, 20, 42, 68, 100] as const

export function heatColor(level: 0 | 1 | 2 | 3 | 4): string {
  const mix = HEAT_MIX[level] ?? 0
  // 第 0 档不掺 accent:空格子是「没发生」,不是「发生得很少」
  if (mix === 0) return 'var(--color-tint)'
  return `color-mix(in srgb, var(--color-accent) ${mix}%, var(--color-tint))`
}

/** `theme.css` 里分类色的槽数。改槽数必须同时改两套主题里的 `--color-chart-N`。 */
export const CHART_SLOTS = 8

/** 「其他(合并的尾部)」以及没进前 N 名的模型用的中性灰。 */
export const OTHERS_COLOR = 'var(--color-chart-other)'

/** 第 `slot` 个分类色(0 起)。超出槽数时回绕,不产生不存在的变量名。 */
export function chartColor(slot: number): string {
  const index = ((Math.floor(slot) % CHART_SLOTS) + CHART_SLOTS) % CHART_SLOTS
  return `var(--color-chart-${index + 1})`
}

/**
 * 给一组模型分配颜色。`keys` 按排名传入(通常是 `toModelShares` 的顺序)。
 *
 * 需求:同一个模型在环形图、趋势堆叠图、费用条里必须是**同一个颜色**。
 * 原先三处各自按「本列表里的序号」取色,而环形图按 token 排、费用表按金额排 ——
 * 同一个模型在两张图里颜色不同,且各自看都对,只有对照时才发现对不上。
 * 所以颜色只在这里分配一次,其它图一律按 key 查表(`colorOf`)。
 *
 * ★ 按排名分配而不是按 key 哈希:8 个槽里放 8 个模型,哈希几乎必然撞色
 * (生日问题),撞色的两个模型在图上无法区分。代价是切换时间范围后排名变了,
 * 颜色可能跟着变 —— 同一屏内一致比跨范围一致重要。
 *
 * 「其他」那一项(`othersKey`)不占分类色槽,固定中性灰。
 */
export function modelColorMap(
  keys: readonly string[],
  othersKey: string
): ReadonlyMap<string, string> {
  const map = new Map<string, string>()
  let slot = 0
  for (const key of keys) {
    if (map.has(key)) continue
    if (key === othersKey) {
      map.set(key, OTHERS_COLOR)
      continue
    }
    map.set(key, chartColor(slot))
    slot++
  }
  return map
}

/**
 * 查模型颜色。查不到 = 这个模型不在前 N 名里(被并进了「其他」),
 * 所以给「其他」的灰,而不是随便挑一个分类色 —— 否则费用表里会冒出
 * 一个环形图上根本不存在的颜色。
 */
export function colorOf(map: ReadonlyMap<string, string>, key: string): string {
  return map.get(key) ?? OTHERS_COLOR
}
