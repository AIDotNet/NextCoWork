import { describe, expect, it } from 'vitest'
import {
  CHART_SLOTS,
  OTHERS_COLOR,
  chartColor,
  colorOf,
  heatColor,
  modelColorMap
} from '../charts/colors'

/*
 * 这些断言看着琐碎,守的是一件具体的事:色值必须留成 `var(--color-*)` 表达式
 * 交给浏览器算。一旦有人改成在 JS 里解析成 `#36d285`,切主题后图表颜色就不动了 ——
 * 而那是只有肉眼才能发现的失效。
 */
describe('heatColor', () => {
  it('每一档都引用主题变量,不写死色值', () => {
    for (const level of [0, 1, 2, 3, 4] as const) {
      expect(heatColor(level)).toContain('var(--color-')
      expect(heatColor(level)).not.toMatch(/#[0-9a-f]{3,6}/i)
    }
  })

  it('第 0 档是槽色 —— 「没发生」不是「发生得很少」', () => {
    expect(heatColor(0)).toBe('var(--color-tint)')
    expect(heatColor(0)).not.toContain('accent')
  })

  it('档位越高 accent 占比越大', () => {
    const mix = (level: 1 | 2 | 3 | 4): number =>
      Number(/accent\) (\d+)%/.exec(heatColor(level))![1])
    expect(mix(1)).toBeLessThan(mix(2))
    expect(mix(2)).toBeLessThan(mix(3))
    expect(mix(3)).toBeLessThan(mix(4))
  })

  it('最高档是纯 accent', () => {
    expect(heatColor(4)).toContain('100%')
  })
})

describe('chartColor', () => {
  it('引用分类色变量,不写死色值', () => {
    for (let slot = 0; slot < CHART_SLOTS; slot++) {
      expect(chartColor(slot)).toBe(`var(--color-chart-${slot + 1})`)
    }
  })

  it('越界与负数回绕到已定义的槽,不产生不存在的变量名', () => {
    expect(chartColor(CHART_SLOTS)).toBe(chartColor(0))
    expect(chartColor(-1)).toBe(chartColor(CHART_SLOTS - 1))
  })
})

describe('modelColorMap', () => {
  it('前 8 个模型各占一个不同的分类色', () => {
    const keys = Array.from({ length: CHART_SLOTS }, (_, i) => `p/m${i}`)
    const map = modelColorMap(keys, '__others__')
    expect(new Set(map.values()).size).toBe(CHART_SLOTS)
  })

  it('「其他」固定中性灰,且不占分类色槽', () => {
    const map = modelColorMap(['p/a', '__others__', 'p/b'], '__others__')
    expect(map.get('__others__')).toBe(OTHERS_COLOR)
    expect(map.get('p/a')).toBe(chartColor(0))
    expect(map.get('p/b')).toBe(chartColor(1))
  })

  // 环形图和费用表排序口径不同,颜色必须只取决于 key
  it('查不到的模型(被并进尾部)给「其他」的灰,而不是新挑一个分类色', () => {
    const map = modelColorMap(['p/a'], '__others__')
    expect(colorOf(map, 'p/a')).toBe(chartColor(0))
    expect(colorOf(map, 'p/tail')).toBe(OTHERS_COLOR)
  })
})
