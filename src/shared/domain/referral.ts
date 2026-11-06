/**
 * 邀请奖励（奖励中心）的跨边界数据形状。
 *
 * 需求：左下角账户菜单里的「邀请好友」从「跳浏览器」改成「在应用内打开奖励中心」，
 * 而奖励中心要画的东西（邀请码 / 链接、活动金额、累计统计、邀请记录、奖励记录）
 * 全部只有服务端知道。这个文件是主进程与渲染层之间那份形状的唯一真源。
 *
 * 不变式：
 * - **金额一律是 USD 的数字**，不在这一层格式化。平台侧 `PLATFORM_CURRENCY = "USD"`，
 *   币种跟着 `currency` 字段走（协议给的领域值，不进 i18n，见渲染层 §6.5）。
 * - **`inviteUrl` 是可直接分享的绝对地址**。服务端只给相对路径（它在反代后面拿不到
 *   可靠的外部地址，见 CoWork `ClientAuthEndpoints` 那条注释），拼站点这一步在主进程做完 —— 渲染层
 *   不需要知道 `https://nextco.work` 这个常量，也就不会在第二个地方写死它。
 * - **`availability` 把三种「没东西可看」分开**：没登录 / 服务端还没上线这条接口 /
 *   运营把活动关了。它们的文案和用户的下一步动作都不同，合并成一个 `null`
 *   会让界面只能说「加载失败」。
 *
 * 故意不做的事：这里没有「奖励任务」。CoWork 至今没有任务这个概念（只有邀请事件与
 * 赠送批次），编一个字段出来等于替平台定协议 —— 界面上那一块目前是固定空态，
 * 等平台给出任务接口再往这里加字段。
 */

/** 邀请事件在邀请人视角下的状态。与 CoWork `ReferralEventStatus` 的名字一一对应。 */
export type ReferralInviteStatus = 'Pending' | 'Qualified' | 'Rewarded' | 'Rejected' | 'Expired'

/** 奖励落到谁头上：我邀请别人拿到的，还是我被邀请注册时拿到的。 */
export type ReferralRewardSide = 'inviter' | 'invitee'

export interface ReferralInvite {
  /** 好友名。**服务端已经打过码**（`a***@example.com`），渲染层原样显示，不再加工。 */
  name: string
  status: ReferralInviteStatus
  /** 这一条给邀请人带来的奖励金额；未发放时是 0。 */
  amount: number
  /** 绑定时刻（ISO 8601）。 */
  at: string
  /** 奖励发放时刻；未发放为 null。 */
  rewardedAt: string | null
}

export interface ReferralReward {
  side: ReferralRewardSide
  /** 批次发放金额。 */
  amount: number
  /** 批次剩余可用额度 —— 消费后会变小，用户问「还剩多少」时看这个。 */
  remainingAmount: number
  /** 入账时刻（ISO 8601）。 */
  at: string
  /** 过期时刻；null = 永不过期。 */
  expireAt: string | null
  /** 已被撤销（风控或管理员拒绝了对应的邀请事件）。撤销的批次仍然列出来。 */
  revoked: boolean
}

export interface ReferralCenter {
  /** 邀请码（12 位小写 hex）。 */
  code: string
  /** 可直接分享的邀请链接，主进程已拼成绝对地址。 */
  inviteUrl: string
  /** 运营总开关。false = 活动暂未开始，此时界面只展示说明，不催用户去分享。 */
  enabled: boolean
  /** 邀请人 / 被邀请人各自的奖励金额（USD）。为 0 表示平台还没配奖励。 */
  inviterAmount: number
  inviteeAmount: number
  /** 奖励额度的有效天数；null = 永不过期。 */
  giftValidDays: number | null
  /** 被邀请人要完成多少笔计费调用才触发双方发放。 */
  qualifyMinPaidCalls: number
  totalInvites: number
  rewardedInvites: number
  pendingInvites: number
  /** 累计已获得的奖励金额。 */
  earnedAmount: number
  /** 钱包币种，来自接口。**不翻译、不写死**。 */
  currency: string
  invites: ReferralInvite[]
  rewards: ReferralReward[]
}

/**
 * 拿不到数据时的原因。渲染层据此选空态文案，**不据此重试** ——
 * 重试的决定权在用户手上（面板右上角那颗刷新）。
 */
export type ReferralUnavailableReason =
  /** 本地模式或登录已失效：菜单里根本不该出现入口，但状态可能在面板打开期间变化。 */
  | 'signed-out'
  /** 平台还没部署这条接口（404）：桌面端比服务端先发版时就是这一种。 */
  | 'unsupported'
  /** 网络失败 / 服务端 5xx：可以重试。 */
  | 'network'

export type ReferralState =
  | { kind: 'ready'; center: ReferralCenter }
  | { kind: 'unavailable'; reason: ReferralUnavailableReason }
