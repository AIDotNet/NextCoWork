import { describe, expect, it } from 'vitest'
import { formatTokenCount } from '../agent/tokens'

describe('formatTokenCount', () => {
  it('三位数以内原样显示,不硬凑单位', () => {
    expect(formatTokenCount(0)).toBe('0')
    expect(formatTokenCount(842)).toBe('842')
    expect(formatTokenCount(999)).toBe('999')
  })

  it('带单位就固定两位小数,尾随的 0 保留', () => {
    expect(formatTokenCount(1000)).toBe('1.00K')
    expect(formatTokenCount(1471)).toBe('1.47K')
    expect(formatTokenCount(53_561)).toBe('53.56K')
    expect(formatTokenCount(534_561)).toBe('534.56K')
  })

  it('M / B 两档', () => {
    expect(formatTokenCount(1_234_567)).toBe('1.23M')
    expect(formatTokenCount(12_345_678)).toBe('12.35M')
    expect(formatTokenCount(123_456_789)).toBe('123.46M')
    expect(formatTokenCount(1_234_567_890)).toBe('1.23B')
  })

  /*
    ★ 这一组是这个函数唯一真正容易写错的地方。取整之前判断进位,999_999 会输出
    `1000.00K` —— 合法数字组成的非法读数。
  */
  it('取整进位后晋到上一档', () => {
    // 两位小数把「晋档」的门槛推到了 999.995K:这一档现在停在 999.50K 而不是进到 1M
    expect(formatTokenCount(999_500)).toBe('999.50K')
    expect(formatTokenCount(999_499)).toBe('999.50K')
    expect(formatTokenCount(999_998)).toBe('1M')
    expect(formatTokenCount(999_999)).toBe('1M')
    expect(formatTokenCount(999_999_999)).toBe('1B')
    // 两位小数那一档同样会进位:99.999K → 100.00 → 该写 `100K`,不是 `100.00K`…… ——
    // 但 100.00 不足以晋档,所以它照实写成 `100.00K`
    expect(formatTokenCount(99_960)).toBe('99.96K')
    expect(formatTokenCount(99_999)).toBe('100.00K')
  })

  it('非有限值给「—」,不给 NaN', () => {
    expect(formatTokenCount(Number.NaN)).toBe('—')
    expect(formatTokenCount(Number.POSITIVE_INFINITY)).toBe('—')
  })

  it('负数带符号 —— 时钟/计数回退时不显示成一串乱码', () => {
    expect(formatTokenCount(-5000)).toBe('-5.00K')
  })
})
