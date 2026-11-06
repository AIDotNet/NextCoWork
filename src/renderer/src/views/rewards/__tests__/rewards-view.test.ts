import { describe, expect, it } from 'vitest'
import { messagesFor, type Translate } from '../../../i18n'
import {
  SHARE_NOTE_KEYS,
  bannerRewardLine,
  formatDay,
  formatMoment,
  formatMoney,
  giftValidityLine,
  inviteShareText,
  inviteStatusKey,
  pickShareNote,
  rewardSideKey,
  summaryStats,
  unavailableKey
} from '../rewards-view'

/** 只回显 key 与参数，断言的是「选了哪条文案、带了什么数」，不依赖真实译文。 */
const t = ((key: string, params?: Record<string, string | number>): string =>
  params === undefined ? key : `${key}(${JSON.stringify(params)})`) as unknown as Translate

describe('formatMoney', () => {
  it('formats with the currency the API returned instead of a hard-coded symbol', () => {
    expect(formatMoney(5, 'USD', 'en-US')).toBe('$5.00')
    expect(formatMoney(5, 'EUR', 'en-US')).toBe('€5.00')
  })

  it('falls back to number plus code when the currency code is unusable', () => {
    // 协议字段被写坏时整块界面不能崩 —— Intl 对非法币种是抛异常的
    expect(formatMoney(5, 'not-a-currency', 'en-US')).toBe('5.00 not-a-currency')
  })
})

describe('formatMoment / formatDay', () => {
  it('echoes unparsable input instead of rendering Invalid Date', () => {
    expect(formatMoment('', 'en-US')).toBe('')
    expect(formatDay('not a date', 'zh-CN')).toBe('not a date')
  })

  it('renders a real timestamp through Intl', () => {
    expect(formatMoment('2026-09-21T08:30:00Z', 'en-US')).toMatch(/2026/)
  })
})

describe('bannerRewardLine', () => {
  const currency = 'USD'

  it('says the programme has not started when the platform switch is off', () => {
    const line = bannerRewardLine({ enabled: false, inviterAmount: 5, inviteeAmount: 5, currency }, 'en-US')
    expect(line).toEqual({ key: 'rewards.bannerDisabled' })
  })

  it('names both sides when both are funded', () => {
    const line = bannerRewardLine({ enabled: true, inviterAmount: 5, inviteeAmount: 3, currency }, 'en-US')
    expect(line.key).toBe('rewards.bannerReward')
    expect(line.params).toEqual({ inviter: '$5.00', invitee: '$3.00' })
  })

  it('mentions only the funded side so the other one is not advertised as zero', () => {
    expect(bannerRewardLine({ enabled: true, inviterAmount: 5, inviteeAmount: 0, currency }, 'en-US').key)
      .toBe('rewards.bannerRewardInviterOnly')
    expect(bannerRewardLine({ enabled: true, inviterAmount: 0, inviteeAmount: 3, currency }, 'en-US').key)
      .toBe('rewards.bannerRewardInviteeOnly')
  })

  it('invents no amount when the platform has not configured one', () => {
    // referral_config 的出厂值就是 0，桌面端编不出「邀请得 $5」
    const line = bannerRewardLine({ enabled: true, inviterAmount: 0, inviteeAmount: 0, currency }, 'en-US')
    expect(line).toEqual({ key: 'rewards.bannerRewardUnset' })
  })
})

describe('giftValidityLine', () => {
  it('reads null as never expires, not as unknown', () => {
    expect(giftValidityLine({ giftValidDays: null }, t)).toBe('rewards.giftNeverExpires')
    expect(giftValidityLine({ giftValidDays: 30 }, t)).toBe('rewards.giftValidDays({"days":30})')
  })
})

describe('summaryStats', () => {
  it('counts people as people and money as money', () => {
    const stats = summaryStats(
      { totalInvites: 3, rewardedInvites: 2, pendingInvites: 1, earnedAmount: 7.5, currency: 'USD' },
      'en-US',
      t
    )
    expect(stats.map((s) => s.key)).toEqual([
      'rewards.statTotal', 'rewards.statRewarded', 'rewards.statPending', 'rewards.statEarned'
    ])
    expect(stats[0]?.value).toBe('rewards.statPeople({"count":"3"})')
    // 第四格必须是金额 —— 走 people() 的话会出现「累计获得 3 人」
    expect(stats[3]?.value).toBe('$7.50')
  })
})

describe('message keys', () => {
  it('maps domain values onto their own namespaces', () => {
    expect(inviteStatusKey('Rewarded')).toBe('rewards.status.Rewarded')
    expect(rewardSideKey('invitee')).toBe('rewards.side.invitee')
    expect(unavailableKey('unsupported')).toBe('rewards.unavailable.unsupported')
  })
})

describe('pickShareNote / inviteShareText', () => {
  const INVITE_URL = 'https://nextco.work/login?ref=e837e871b2ea'

  it('每条都有两种语言的译文 —— 缺一条就会把 key 本身当成邀请文案发给朋友', () => {
    for (const key of SHARE_NOTE_KEYS) {
      expect(messagesFor('zh-CN')[key], key).toBeDefined()
      expect(messagesFor('en-US')[key], key).toBeDefined()
    }
  })

  it('池子至少两条 —— 只有一条时「随机挑一句」是假的', () => {
    expect(SHARE_NOTE_KEYS.length).toBeGreaterThan(1)
  })

  it('按 random 落在池子里的那一条上', () => {
    expect(pickShareNote(() => 0)).toBe(SHARE_NOTE_KEYS[0])
    expect(pickShareNote(() => 0.999)).toBe(SHARE_NOTE_KEYS[SHARE_NOTE_KEYS.length - 1])
  })

  it('★ 每一条都取得到 —— 取不到的那条等于没内置', () => {
    // 把 [0,1) 铺满，等于把每个可能的下标都试一遍
    const seen = new Set<string>()
    for (let i = 0; i < 200; i++) seen.add(pickShareNote(() => i / 200))
    expect(seen.size).toBe(SHARE_NOTE_KEYS.length)
  })

  it('★ 链接原样在最前面 —— 加前缀或做编码都会让朋友点不开', () => {
    for (const key of SHARE_NOTE_KEYS) {
      const text = inviteShareText(INVITE_URL, key, t)
      expect(text.startsWith(`${INVITE_URL} `), key).toBe(true)
    }
  })
})
