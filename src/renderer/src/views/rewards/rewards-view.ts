/**
 * 奖励中心的纯逻辑：金额/日期格式化、横幅那句话选哪一条文案、四格统计的取数。
 *
 * 需求：这些判断原本很容易散在 `RewardsOverlay.tsx` 里（「双方都有奖励 / 只有一侧
 * 有 / 平台还没配金额」是三句不同的话），而 vitest 的 `include` 只收
 * `.test.ts` —— 埋在 .tsx 里就等于没有测试。仓库既定做法是
 * 「.tsx 负责标记、同目录 .ts 负责逻辑」（`usage-format.ts` / `thread-content.ts`
 * 都是这个形状），这个文件就是那一半。
 *
 * ★ 金额一律走 `Intl` 的 currency 记法，**不手写 `$`**：币种是接口给的领域值
 * （今天是 USD，平台换币种时这里不该改代码）。手写符号在换币种那天会变成谎话。
 */
import type { Locale, Translate, TranslationKey } from '../../i18n'
import type {
  ReferralCenter,
  ReferralInviteStatus,
  ReferralRewardSide,
  ReferralUnavailableReason
} from '../../../../shared/domain/referral'

/**
 * 金额。和 `usage-format.ts` 的 `formatCostMicros` 是两个函数：那边的入参是
 * micros（百万分之一单位），这边接口给的就是主单位的小数，合并一个函数只会
 * 让某一侧偷偷差 10^6 倍。
 *
 * 币种代码非法时（协议字段被写坏）退回「数字 + 代码」，不让整块界面崩掉。
 */
export function formatMoney(value: number, currency: string, locale: Locale): string {
  try {
    return new Intl.NumberFormat(locale, {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(value)
  } catch {
    return `${new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)} ${currency}`
  }
}

/**
 * 记录表里的时间列。显示到分钟：邀请记录是按天看的东西，精确到秒只会把列撑宽。
 * 解析不出来时**原样回显**，而不是显示「Invalid Date」——后者看着像功能坏了。
 */
export function formatMoment(iso: string, locale: Locale): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return new Intl.DateTimeFormat(locale, {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
  }).format(date)
}

/** 奖励批次的过期时间列，只到天。 */
export function formatDay(iso: string, locale: Locale): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return new Intl.DateTimeFormat(locale, { year: 'numeric', month: '2-digit', day: '2-digit' }).format(date)
}

export function inviteStatusKey(status: ReferralInviteStatus): TranslationKey {
  return `rewards.status.${status}`
}

export function rewardSideKey(side: ReferralRewardSide): TranslationKey {
  return `rewards.side.${side}`
}

export function unavailableKey(reason: ReferralUnavailableReason): TranslationKey {
  return `rewards.unavailable.${reason}`
}

/**
 * 横幅底部那一句。
 *
 * ★ 四种情况必须分开，**不能凑成一句带 0 的话**：
 *   - 活动关着           → 「活动暂时还没开始」（参考图里就是这一句）
 *   - 双方都有奖励       → 「你得 X，好友得 Y」
 *   - 只有一侧有奖励     → 只说那一侧。说「好友得 $0.00」等于劝退。
 *   - 两边都是 0         → 只说规则，不报数字。平台的 `referral_config` 默认就是 0，
 *                          桌面端在那之前**编不出**任何金额（见 `AccountMenu.tsx` 那条注释）。
 */
export function bannerRewardLine(
  center: Pick<ReferralCenter, 'enabled' | 'inviterAmount' | 'inviteeAmount' | 'currency'>,
  locale: Locale
): { key: TranslationKey; params?: Record<string, string> } {
  if (!center.enabled) return { key: 'rewards.bannerDisabled' }
  const inviter = formatMoney(center.inviterAmount, center.currency, locale)
  const invitee = formatMoney(center.inviteeAmount, center.currency, locale)
  if (center.inviterAmount > 0 && center.inviteeAmount > 0) {
    return { key: 'rewards.bannerReward', params: { inviter, invitee } }
  }
  if (center.inviterAmount > 0) return { key: 'rewards.bannerRewardInviterOnly', params: { inviter } }
  if (center.inviteeAmount > 0) return { key: 'rewards.bannerRewardInviteeOnly', params: { invitee } }
  return { key: 'rewards.bannerRewardUnset' }
}

/**
 * 奖励额度的有效期那一行。`giftValidDays === null` 是**「永不过期」而不是「未知」**
 * （CoWork `ReferralConfig.GiftValidDays` 的语义），所以这一支也要出文案。
 */
export function giftValidityLine(
  center: Pick<ReferralCenter, 'giftValidDays'>,
  t: Translate
): string {
  return center.giftValidDays === null
    ? t('rewards.giftNeverExpires')
    : t('rewards.giftValidDays', { days: center.giftValidDays })
}

/**
 * 邀请链接复制出去时要跟在后面的文案池。
 *
 * 需求：邀请链接是用户自己复制到聊天里发出去的，一条光秃秃的 URL 既没说明这是什么、
 * 也没说清对朋友有什么好处，收到的人多半不会点。所以复制时随机跟一句，让这条消息
 * **自带说明** —— 原话是「复制到剪贴板的内容，URL 地址后面要接文案」。
 *
 * ★ 这些句子是写给**收到链接的人**看的，不是界面确认：每一句都得能脱离界面单独
 * 成立，「已复制」这类话放在这里全是错位的（界面的复制确认仍然是 toast）。
 *
 * ★ 类型是**非空元组**而不是 `TranslationKey[]`：空表会让 `pickShareNote` 取不到
 * 东西、只能吐 undefined（同 `i18n/whimsy.ts` 的 `Whimsy`）。句子是**料**而不是
 * 产品概念，中英两条不必是彼此的译文；但八个 key 在两张表里都得有值，缺一条就会有
 * 一条邀请把 key 本身发给朋友 —— `__tests__/rewards-view.test.ts` 钉着。
 */
export const SHARE_NOTE_KEYS: readonly [TranslationKey, ...TranslationKey[]] = [
  'rewards.shareNote1',
  'rewards.shareNote2',
  'rewards.shareNote3',
  'rewards.shareNote4',
  'rewards.shareNote5',
  'rewards.shareNote6',
  'rewards.shareNote7',
  'rewards.shareNote8'
]

/**
 * 随机挑一句。
 *
 * `random` 注入（默认 `Math.random`）：随机行为在测试里必须能钉死，否则只能断言
 * 「结果是池子里的某一条」，那等于没测 —— 池子少写一条、下标算错一位都照样过。
 */
export function pickShareNote(random: () => number = Math.random): TranslationKey {
  const index = Math.min(SHARE_NOTE_KEYS.length - 1, Math.floor(random() * SHARE_NOTE_KEYS.length))
  return SHARE_NOTE_KEYS[index] ?? SHARE_NOTE_KEYS[0]
}

/**
 * 复制进剪贴板的那段文本 = 邀请链接 + 一句文案。
 *
 * ★ **链接在最前面且原样**：后面那些字是给人和聊天软件看的，链接本身一个字都不能动
 * —— 加前缀、整体加引号、或对链接做 URL 编码都会让朋友点不开。这个函数存在的意义
 * 就是把这套格式钉在一处并且可测，而不是散在组件的模板字符串里。
 *
 * ★ 中间是**一个空格**而不是换行：URL 与正文直接相接时，链接的边界靠空白划出来，
 * 少了它有的客户端会把后面的字一起算进链接。
 */
export function inviteShareText(inviteUrl: string, note: TranslationKey, t: Translate): string {
  return `${inviteUrl} ${t(note)}`
}

export interface RewardsStat {
  /** 统计卡标题的文案 key。 */
  key: TranslationKey
  /** 已经格式化好的值 —— 组件只负责摆版式，不再判断单位。 */
  value: string
}

/**
 * 横幅下面那四格。前三格是人数、第四格是金额 —— **人数不能用金额格式化**，
 * 否则「累计邀请 $3.00」。
 */
export function summaryStats(
  center: Pick<ReferralCenter, 'totalInvites' | 'rewardedInvites' | 'pendingInvites' | 'earnedAmount' | 'currency'>,
  locale: Locale,
  t: Translate
): RewardsStat[] {
  const people = (count: number): string => t('rewards.statPeople', { count: new Intl.NumberFormat(locale).format(count) })
  return [
    { key: 'rewards.statTotal', value: people(center.totalInvites) },
    { key: 'rewards.statRewarded', value: people(center.rewardedInvites) },
    { key: 'rewards.statPending', value: people(center.pendingInvites) },
    { key: 'rewards.statEarned', value: formatMoney(center.earnedAmount, center.currency, locale) }
  ]
}
