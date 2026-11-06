/**
 * 奖励中心（邀请好友）的文案。
 *
 * 需求：账户菜单里的「邀请好友」从「跳浏览器」改成应用内的全屏浮层，浮层里的
 * 每一句话都要能跟着语言切换。照 `usage.ts` / `git.ts` 的先例单独建文件，
 * 不再往 `index.tsx` 那三千行里堆（§6.3）。
 *
 * ★ `rewardsEn` **不要**标 `Record<keyof typeof rewardsZh, string>` —— 带插值的
 * 条目值是函数不是 string，标了整张表都不匹配。zh/en 的键对齐由
 * `index.test.ts` 逐键比对守着。
 *
 * 不翻译的东西：邀请码、邀请链接、币种代码（`USD` 来自接口，是领域值，§6.5）、
 * 好友名（服务端已打码的原样显示）。
 */

/**
 * 带参数文案的入参类型。★ 必须显式标出来：这个文件拿不到 `index.tsx` 里
 * `Messages` 的上下文，不标的话参数是 implicit any，spread 进 `ZH` 时整张表都不再匹配。
 */
type Params = Record<string, string | number>

export const rewardsZh = {
  'rewards.title': '奖励中心',
  'rewards.close': '关闭奖励中心',
  'rewards.refresh': '刷新',
  'rewards.bannerEyebrow': '好东西，和朋友分享',
  'rewards.bannerTitle': '邀请新用户，一起获得更多。',
  'rewards.bannerArtAlt': '两张叠放的卡片插画',
  /** 活动开着、且双方都有奖励时的那一行。金额由调用方格式化好再传进来。 */
  'rewards.bannerReward': ({ inviter, invitee }: Params) =>
    `每邀请一位好友，你得 ${inviter}，好友得 ${invitee}`,
  'rewards.bannerRewardInviterOnly': ({ inviter }: Params) => `每邀请一位好友，你得 ${inviter}`,
  'rewards.bannerRewardInviteeOnly': ({ invitee }: Params) => `你的好友注册即得 ${invitee}`,
  /** 活动开着但平台还没配金额 —— 不能编一个数出来，只说规则。 */
  'rewards.bannerRewardUnset': '奖励金额由平台配置，开奖后会显示在这里',
  'rewards.bannerDisabled': '活动暂时还没开始',
  'rewards.qualifyHint': ({ calls }: Params) => `好友完成 ${calls} 次计费调用后，双方奖励到账`,
  'rewards.giftValidDays': ({ days }: Params) => `奖励额度有效期 ${days} 天`,
  'rewards.giftNeverExpires': '奖励额度永不过期',

  'rewards.inviteLink': '邀请链接',
  'rewards.inviteCode': '邀请码',
  'rewards.copyLink': '复制链接',
  'rewards.copyCode': '复制邀请码',
  /*
    ★ 下面八条是**复制邀请链接时随机挑一条**、跟在 URL 后面的池子
    （`rewards-view.ts` 的 `SHARE_NOTE_KEYS`）。两条约束：
    ① 它们是写给**收到链接的人**看的，必须能脱离界面单独成立 —— 「已复制」这类
       界面确认的话放在这里全是错位的（界面确认是 `rewards.copied`）；
    ② 序号只是编号，中英两条不必是彼此的译文（改文案时不用两边对着改）。
  */
  'rewards.shareNote1': '用这个链接注册，我俩都能拿到额度',
  'rewards.shareNote2': '好东西不独享，注册后双方都有奖励',
  'rewards.shareNote3': '终端、文件、浏览器一屏搞定，先安利为敬',
  'rewards.shareNote4': '我用着挺顺手，推荐给你试试',
  'rewards.shareNote5': '编码 Agent 桌面端，登录就能开工',
  'rewards.shareNote6': '一起用吧，效率高一点，下班早一点',
  'rewards.shareNote7': '这个工具挺省事，你也装一个',
  'rewards.shareNote8': '顺手分享给你，好东西不该只有我知道',
  'rewards.copied': '已复制到剪贴板',
  'rewards.copyFailed': '复制失败，请重试',
  'rewards.openInBrowser': '在浏览器中打开',
  'rewards.savePoster': '保存邀请海报',
  'rewards.posterSaving': '正在生成海报…',
  'rewards.posterSaved': ({ path }: Params) => `海报已保存到 ${path}`,
  'rewards.posterFailed': '海报生成失败，请重试',

  /*
    ★ 下面这一组是**画进图片里**的文案，不是界面文案。改的时候注意两件事：
    ① 它们进的是 SVG 的 `<text>`，**不会自动折行** —— 主标题因此是分开的两条 key；
    ② 版心固定（1920 宽的左半边），单行超过约 16 个全角字会顶到右边的插画上。
  */
  'rewards.poster.tagline': '编码型 Agent 桌面端',
  'rewards.poster.eyebrow': '好东西，和朋友分享',
  'rewards.poster.titleLine1': '邀请好友，',
  'rewards.poster.titleLine2': '一起获得更多。',
  'rewards.poster.subtitle': '邀请好友注册，双方都能拿到免费额度',
  'rewards.poster.bullet1': '内置多模型，登录即用',
  'rewards.poster.bullet2': '终端 / 文件 / 浏览器，一屏搞定',
  'rewards.poster.bullet3': '会话与配置跨设备同步',
  'rewards.poster.scanTitle': '扫码注册',
  'rewards.poster.scanHint': '扫码即自动绑定邀请关系',
  'rewards.poster.codeLabel': '邀请码',

  'rewards.statTotal': '累计邀请',
  'rewards.statRewarded': '已发放',
  'rewards.statPending': '待达标',
  'rewards.statEarned': '累计获得',
  'rewards.statPeople': ({ count }: Params) => `${count} 人`,

  'rewards.tasksTitle': '奖励任务',
  'rewards.tasksEmpty': '暂无奖励任务',
  'rewards.colTask': '任务',
  'rewards.colProgress': '进度',
  'rewards.colReward': '奖励',

  'rewards.invitesTitle': '邀请记录',
  'rewards.invitesEmpty': '暂无邀请记录',
  'rewards.colFriend': '好友',
  'rewards.colInviteStatus': '邀请状态',
  'rewards.colFriendReward': '好友奖励',
  'rewards.colInviteTime': '邀请时间',

  'rewards.rewardsTitle': '奖励记录',
  'rewards.rewardsEmpty': '暂无奖励记录',
  'rewards.colRewardSource': '来源',
  'rewards.colRewardAmount': '金额',
  'rewards.colRewardRemaining': '剩余',
  'rewards.colRewardTime': '到账时间',

  'rewards.status.Pending': '待达标',
  'rewards.status.Qualified': '待发放',
  'rewards.status.Rewarded': '已发放',
  'rewards.status.Rejected': '已拒绝',
  'rewards.status.Expired': '已过期',
  'rewards.side.inviter': '邀请好友',
  'rewards.side.invitee': '接受邀请',
  'rewards.revoked': '已撤销',
  'rewards.expiresAt': ({ date }: Params) => `${date} 过期`,
  'rewards.neverExpires': '不过期',

  'rewards.loading': '正在加载奖励信息…',
  'rewards.unavailable.signed-out': '登录后即可查看你的邀请奖励。',
  'rewards.unavailable.unsupported': '当前服务端还没有开放奖励中心，请稍后再试或到网页端查看。',
  'rewards.unavailable.network': '奖励信息加载失败，请检查网络后重试。'
}

export const rewardsEn = {
  'rewards.title': 'Rewards',
  'rewards.close': 'Close rewards',
  'rewards.refresh': 'Refresh',
  'rewards.bannerEyebrow': 'Share something good',
  'rewards.bannerTitle': 'Invite a friend, gain more together.',
  'rewards.bannerArtAlt': 'Illustration of two stacked cards',
  'rewards.bannerReward': ({ inviter, invitee }: Params) =>
    `For every friend who joins, you get ${inviter} and they get ${invitee}`,
  'rewards.bannerRewardInviterOnly': ({ inviter }: Params) => `You get ${inviter} for every friend who joins`,
  'rewards.bannerRewardInviteeOnly': ({ invitee }: Params) => `Your friend gets ${invitee} on sign-up`,
  'rewards.bannerRewardUnset': 'Reward amounts are set by the platform and will appear here',
  'rewards.bannerDisabled': 'The programme has not started yet',
  'rewards.qualifyHint': ({ calls }: Params) =>
    `Both rewards land once your friend makes ${calls} billed call(s)`,
  'rewards.giftValidDays': ({ days }: Params) => `Reward credit is valid for ${days} days`,
  'rewards.giftNeverExpires': 'Reward credit never expires',

  'rewards.inviteLink': 'Invite link',
  'rewards.inviteCode': 'Invite code',
  'rewards.copyLink': 'Copy link',
  'rewards.copyCode': 'Copy code',
  // 同中文那份的池子说明：这些句子是写给收到链接的人看的，不是界面确认。
  'rewards.shareNote1': 'Sign up with this link and we both get credit',
  'rewards.shareNote2': 'Good things are for sharing — we both get rewarded on sign-up',
  'rewards.shareNote3': 'Terminal, files and browser in one window. Consider this a recommendation.',
  'rewards.shareNote4': 'I have been using this and like it. Give it a go.',
  'rewards.shareNote5': 'A coding agent desktop app — sign in and start working',
  'rewards.shareNote6': 'Use it with me: a bit more done, a bit earlier home',
  'rewards.shareNote7': 'Handy little tool, you should install it too',
  'rewards.shareNote8': 'Sharing this on a whim — good things should not stay with one person',
  'rewards.copied': 'Copied to clipboard',
  'rewards.copyFailed': 'Could not copy. Please try again.',
  'rewards.openInBrowser': 'Open in browser',
  'rewards.savePoster': 'Save invite poster',
  'rewards.posterSaving': 'Building the poster…',
  'rewards.posterSaved': ({ path }: Params) => `Poster saved to ${path}`,
  'rewards.posterFailed': 'Could not build the poster. Please try again.',

  'rewards.poster.tagline': 'The coding agent desktop',
  'rewards.poster.eyebrow': 'Share something good',
  'rewards.poster.titleLine1': 'Invite a friend,',
  'rewards.poster.titleLine2': 'gain more together.',
  'rewards.poster.subtitle': 'Both of you get free credit when they sign up',
  'rewards.poster.bullet1': 'Many models built in, ready at sign-in',
  'rewards.poster.bullet2': 'Terminal, files and browser in one window',
  'rewards.poster.bullet3': 'Sessions and settings sync across devices',
  'rewards.poster.scanTitle': 'Scan to join',
  'rewards.poster.scanHint': 'Scanning links the invite automatically',
  'rewards.poster.codeLabel': 'Invite code',

  'rewards.statTotal': 'Invited',
  'rewards.statRewarded': 'Rewarded',
  'rewards.statPending': 'Pending',
  'rewards.statEarned': 'Earned',
  'rewards.statPeople': ({ count }: Params) => `${count}`,

  'rewards.tasksTitle': 'Reward tasks',
  'rewards.tasksEmpty': 'No reward tasks yet',
  'rewards.colTask': 'Task',
  'rewards.colProgress': 'Progress',
  'rewards.colReward': 'Reward',

  'rewards.invitesTitle': 'Invites',
  'rewards.invitesEmpty': 'No invites yet',
  'rewards.colFriend': 'Friend',
  'rewards.colInviteStatus': 'Status',
  'rewards.colFriendReward': 'Your reward',
  'rewards.colInviteTime': 'Invited',

  'rewards.rewardsTitle': 'Reward history',
  'rewards.rewardsEmpty': 'No rewards yet',
  'rewards.colRewardSource': 'Source',
  'rewards.colRewardAmount': 'Amount',
  'rewards.colRewardRemaining': 'Remaining',
  'rewards.colRewardTime': 'Credited',

  'rewards.status.Pending': 'Pending',
  'rewards.status.Qualified': 'Qualified',
  'rewards.status.Rewarded': 'Rewarded',
  'rewards.status.Rejected': 'Rejected',
  'rewards.status.Expired': 'Expired',
  'rewards.side.inviter': 'Invited a friend',
  'rewards.side.invitee': 'Accepted an invite',
  'rewards.revoked': 'Revoked',
  'rewards.expiresAt': ({ date }: Params) => `Expires ${date}`,
  'rewards.neverExpires': 'No expiry',

  'rewards.loading': 'Loading your rewards…',
  'rewards.unavailable.signed-out': 'Sign in to see your invite rewards.',
  'rewards.unavailable.unsupported': 'This server does not serve rewards yet. Try again later or check the website.',
  'rewards.unavailable.network': 'Could not load your rewards. Check your connection and try again.'
}
