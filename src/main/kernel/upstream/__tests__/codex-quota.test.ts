/**
 * Codex 额度头的解析。
 *
 * ★ 头名本身**还没有实测定死**(计划 §11 第 1 条),所以这里测的是**策略**:
 * 缺字段就不编数、相对秒折成绝对时间、越界值夹紧、认不出返回 null。
 * 将来换一组真实头名时,这些断言一条都不用改。
 */
import { describe, expect, it } from 'vitest'
import { codexQuotaHeaderNames, headerReaderOf, parseCodexQuota } from '../codex-quota'

const NOW = 1_700_000_000_000

/** 喂一个普通对象就行 —— 解析函数刻意不依赖 `Headers` */
function reader(values: Record<string, string>) {
  return (name: string): string | null => values[name] ?? null
}

const full = {
  'x-codex-primary-used-percent': '62',
  'x-codex-primary-window-minutes': '300',
  'x-codex-primary-resets-in-seconds': '7200',
  'x-codex-secondary-used-percent': '31',
  'x-codex-secondary-window-minutes': '10080',
  'x-codex-secondary-resets-in-seconds': '400000'
}

describe('两个窗口齐全', () => {
  it('解出 5 小时与一周两条，相对秒当场折成绝对时间戳', () => {
    expect(parseCodexQuota(reader(full), NOW)).toEqual({
      primary: { usedPercent: 62, windowMinutes: 300, resetsAt: NOW + 7_200_000 },
      secondary: { usedPercent: 31, windowMinutes: 10_080, resetsAt: NOW + 400_000_000 },
      capturedAt: NOW
    })
  })

  it('★ 记下 capturedAt —— 额度只在发消息时更新，界面要说得出这是什么时候的数', () => {
    expect(parseCodexQuota(reader(full), NOW)?.capturedAt).toBe(NOW)
  })
})

describe('缺字段', () => {
  it('★★ 三个字段同进同出：只有百分比的窗口整个丢掉', () => {
    const snapshot = parseCodexQuota(
      reader({ 'x-codex-primary-used-percent': '62', ...{} }),
      NOW
    )
    expect(snapshot).toBeNull()
  })

  it('★★ 没有 resetsAt 的「跑满」会变成一次永不到期的落闸 —— 所以缺它就整条不要', () => {
    const snapshot = parseCodexQuota(
      reader({
        'x-codex-primary-used-percent': '100',
        'x-codex-primary-window-minutes': '300'
      }),
      NOW
    )
    expect(snapshot).toBeNull()
  })

  it('只有一个窗口时另一个缺席，不补零', () => {
    const snapshot = parseCodexQuota(
      reader({
        'x-codex-primary-used-percent': '10',
        'x-codex-primary-window-minutes': '300',
        'x-codex-primary-resets-in-seconds': '60'
      }),
      NOW
    )
    expect(snapshot?.primary).toBeDefined()
    expect(snapshot?.secondary).toBeUndefined()
  })

  it('★★ 一个都认不出时返回 null —— null 和「两个窗口都 0%」是两件事', () => {
    expect(parseCodexQuota(reader({}), NOW)).toBeNull()
    expect(parseCodexQuota(reader({ 'content-type': 'text/event-stream' }), NOW)).toBeNull()
  })
})

describe('坏值', () => {
  it('★ 空串 / 非数字当作「没有」，不是 0 —— 0 是一个确切的读数', () => {
    expect(
      parseCodexQuota(
        reader({
          'x-codex-primary-used-percent': '',
          'x-codex-primary-window-minutes': '300',
          'x-codex-primary-resets-in-seconds': '60'
        }),
        NOW
      )
    ).toBeNull()
    expect(
      parseCodexQuota(
        reader({
          'x-codex-primary-used-percent': 'unknown',
          'x-codex-primary-window-minutes': '300',
          'x-codex-primary-resets-in-seconds': '60'
        }),
        NOW
      )
    ).toBeNull()
  })

  it('★ 百分比夹在 0–100：上游的超额宽限会报 >100，而进度条会溢出圆角', () => {
    const snapshot = parseCodexQuota(
      reader({
        'x-codex-primary-used-percent': '135',
        'x-codex-primary-window-minutes': '300',
        'x-codex-primary-resets-in-seconds': '60'
      }),
      NOW
    )
    expect(snapshot?.primary?.usedPercent).toBe(100)
  })

  it('窗口长度非正、重置秒数为负 —— 都判这条窗口不可用', () => {
    expect(
      parseCodexQuota(
        reader({
          'x-codex-primary-used-percent': '10',
          'x-codex-primary-window-minutes': '0',
          'x-codex-primary-resets-in-seconds': '60'
        }),
        NOW
      )
    ).toBeNull()
    expect(
      parseCodexQuota(
        reader({
          'x-codex-primary-used-percent': '10',
          'x-codex-primary-window-minutes': '300',
          'x-codex-primary-resets-in-seconds': '-5'
        }),
        NOW
      )
    ).toBeNull()
  })

  it('0% 是合法读数，照样解出来', () => {
    const snapshot = parseCodexQuota(
      reader({
        'x-codex-primary-used-percent': '0',
        'x-codex-primary-window-minutes': '300',
        'x-codex-primary-resets-in-seconds': '60'
      }),
      NOW
    )
    expect(snapshot?.primary?.usedPercent).toBe(0)
  })
})

describe('候选头名', () => {
  it('下划线写法也认（在实测定死之前的防御性别名）', () => {
    const snapshot = parseCodexQuota(
      reader({
        'x-codex-primary-used_percent': '20',
        'x-codex-primary-window_minutes': '300',
        'x-codex-primary-reset-after-seconds': '90'
      }),
      NOW
    )
    expect(snapshot?.primary).toEqual({
      usedPercent: 20,
      windowMinutes: 300,
      resetsAt: NOW + 90_000
    })
  })
})

describe('真 Headers 与诊断', () => {
  it('headerReaderOf 对大小写不敏感（HTTP 头本来就是）', () => {
    const headers = new Headers({
      'X-Codex-Primary-Used-Percent': '42',
      'X-Codex-Primary-Window-Minutes': '300',
      'X-Codex-Primary-Resets-In-Seconds': '30'
    })
    expect(parseCodexQuota(headerReaderOf(headers), NOW)?.primary?.usedPercent).toBe(42)
  })

  it('★ 诊断只收 x-codex-* —— 整份响应头里有 authorization 和 set-cookie', () => {
    const headers = new Headers({
      'x-codex-primary-used-percent': '42',
      authorization: 'Bearer secret',
      'content-type': 'text/event-stream'
    })
    const seen = codexQuotaHeaderNames(headers)
    expect(seen).toEqual(['x-codex-primary-used-percent=42'])
    expect(seen.join(' ')).not.toContain('secret')
  })
})
