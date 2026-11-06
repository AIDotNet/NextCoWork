/**
 * 账号列表的纯逻辑(`provider-accounts.ts`)。
 *
 * 写成 `.ts` + 不碰 DOM:`vitest.config.ts` 的 `include` 只收 `.ts`,
 * 写成 `.test.tsx` 会被静默跳过(同 `provider-auth.test.ts` 的理由)。
 */
import { describe, expect, it } from 'vitest'
import type { ProviderAccount } from '../../../../../../shared/domain/provider-account'
import {
  accountBadge,
  activeAccountId,
  countdownTo,
  isQuotaStale,
  needsCountdownTick,
  orderedAccounts,
  quotaBar,
  reorder
} from '../provider-accounts'

const NOW = 1_700_000_000_000

function account(id: string, order: number, extra: Partial<ProviderAccount> = {}): ProviderAccount {
  return {
    id,
    providerId: 'codex',
    issuer: 'chatgpt',
    order,
    enabled: true,
    current: order === 0,
    needsReauth: false,
    ...extra
  }
}

const limited = (untilIn: number): ProviderAccount['limit'] => ({
  until: NOW + untilIn,
  since: NOW,
  source: 'http-429',
  reason: '额度用尽'
})

describe('徽章', () => {
  it('★ 优先级不能换：停用 > 需重登 > 限流 > 可用', () => {
    expect(accountBadge(account('a', 0, { enabled: false, needsReauth: true, limit: limited(60_000) }), NOW))
      .toBe('disabled')
    expect(accountBadge(account('a', 0, { needsReauth: true, limit: limited(60_000) }), NOW))
      .toBe('needs-reauth')
    expect(accountBadge(account('a', 0, { limit: limited(60_000) }), NOW)).toBe('limited')
    expect(accountBadge(account('a', 0), NOW)).toBe('ready')
  })

  it('★★ 失效的账号不能显示成「限流中」—— 那会写出一句「1 小时后恢复」，而它永远不会恢复', () => {
    expect(accountBadge(account('a', 0, { needsReauth: true, limit: limited(3_600_000) }), NOW))
      .toBe('needs-reauth')
  })

  it('限流到点后自动变回可用', () => {
    const row = account('a', 0, { limit: limited(1_000) })
    expect(accountBadge(row, NOW + 1_001)).toBe('ready')
  })
})

describe('倒计时', () => {
  it('拆成时分秒，不拼句子（句子要按 locale 翻）', () => {
    expect(countdownTo(NOW + 3_723_000, NOW)).toEqual({
      hours: 1, minutes: 2, seconds: 3, totalMs: 3_723_000
    })
  })

  it('★ 到点之后全是 0，不给负数 —— 负数会画成「还有 -3 秒」', () => {
    expect(countdownTo(NOW - 5_000, NOW)).toEqual({ hours: 0, minutes: 0, seconds: 0, totalMs: 0 })
  })

  it('向上取整：还剩 0.4 秒时显示 1 秒，而不是 0', () => {
    expect(countdownTo(NOW + 400, NOW).seconds).toBe(1)
  })
})

describe('要不要起那个 1 秒 tick', () => {
  it('★★ 没有账号在限流时不起 —— 设置页常年开着，一个永不停的 tick 会让这一片每秒重渲', () => {
    expect(needsCountdownTick([account('a', 0)], NOW)).toBe(false)
    expect(needsCountdownTick([], NOW)).toBe(false)
  })

  it('有限流中的账号就起', () => {
    expect(needsCountdownTick([account('a', 0, { limit: limited(60_000) })], NOW)).toBe(true)
  })

  it('★ 停用的账号不算 —— 它那条闸门不会走动，也没人在看', () => {
    expect(needsCountdownTick([account('a', 0, { enabled: false, limit: limited(60_000) })], NOW))
      .toBe(false)
  })
})

describe('额度条', () => {
  it('★★ 没有快照是 null，不是 0% —— 两者在界面上必须是两句话', () => {
    expect(quotaBar(undefined)).toBeNull()
  })

  it('认出 5 小时与一周两个窗口', () => {
    expect(quotaBar({ usedPercent: 62, windowMinutes: 300, resetsAt: NOW })?.window).toBe('5h')
    expect(quotaBar({ usedPercent: 31, windowMinutes: 10_080, resetsAt: NOW })?.window).toBe('week')
    expect(quotaBar({ usedPercent: 5, windowMinutes: 60, resetsAt: NOW })?.window).toBe('other')
  })

  it('★ 百分比夹在 0–100：上游给过 >100 的数，条子会溢出圆角', () => {
    expect(quotaBar({ usedPercent: 140, windowMinutes: 300, resetsAt: NOW })?.percent).toBe(100)
    expect(quotaBar({ usedPercent: -3, windowMinutes: 300, resetsAt: NOW })?.percent).toBe(0)
  })

  it('≥90% 标告警色 —— 那是「该换号了」的最后一个提示', () => {
    expect(quotaBar({ usedPercent: 89, windowMinutes: 300, resetsAt: NOW })?.critical).toBe(false)
    expect(quotaBar({ usedPercent: 90, windowMinutes: 300, resetsAt: NOW })?.critical).toBe(true)
  })

  it('★ 超过一天的快照标成旧数据 —— 额度只在发消息时更新，界面必须说得出这是什么时候的数', () => {
    expect(isQuotaStale(NOW, NOW + 3_600_000)).toBe(false)
    expect(isQuotaStale(NOW, NOW + 25 * 3_600_000)).toBe(true)
  })
})

describe('拖拽排序', () => {
  it('往后拖', () => {
    expect(reorder(['a', 'b', 'c'], 0, 2)).toEqual(['b', 'c', 'a'])
  })

  it('往前拖', () => {
    expect(reorder(['a', 'b', 'c'], 2, 0)).toEqual(['c', 'a', 'b'])
  })

  it('★ 拖到列表末尾那一下（to === length）必须生效，否则最常见的操作看起来失灵', () => {
    expect(reorder(['a', 'b', 'c'], 0, 3)).toEqual(['b', 'c', 'a'])
  })

  it('越界的 from 原样返回，不抛', () => {
    expect(reorder(['a', 'b'], 5, 0)).toEqual(['a', 'b'])
  })
})

describe('列表顺序与「下一次会用谁」', () => {
  it('列表按轮换顺序排', () => {
    expect(orderedAccounts([account('b', 1), account('a', 0)]).map((a) => a.id)).toEqual(['a', 'b'])
  })

  it('★★ 和主进程的选择规则同源：限流的被跳过，恢复后又回到它', () => {
    const rows = [account('a', 0, { limit: limited(60_000) }), account('b', 1)]
    expect(activeAccountId(rows, NOW, true)).toBe('b')
    expect(activeAccountId(rows, NOW + 60_001, true)).toBe('a')
  })

  it('★ 关掉轮换时指向当前账号，哪怕它正被限流 —— 界面要说的是实话', () => {
    const rows = [account('a', 0, { current: true, limit: limited(60_000) }), account('b', 1)]
    expect(activeAccountId(rows, NOW, false)).toBe('a')
  })

  it('全不可用时是 null', () => {
    expect(activeAccountId([account('a', 0, { enabled: false })], NOW, true)).toBeNull()
  })
})
