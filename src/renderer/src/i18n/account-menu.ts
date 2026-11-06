/**
 * 需求：侧边栏左下角的账户菜单 —— 它从原来那张「信息 + 齿轮」合体卡片里拆出来，
 * 成了一个独立界面域，按 AGENTS.md §6.3 新建文件，不往 index.tsx 那三千行里堆。
 *
 * ★ **「退出登录」不在这里另起一个键。** 它和设置 → 账户页那颗按钮是**同一个
 * 动作**（都走 `signOutClient()`），文案复用 `auth.signOut`；另写一条的代价是
 * 两处会漂成两句不同的话，而用户看到的是同一个操作。
 *
 * ★ `accountMenuEn` 不标 `Record<keyof typeof accountMenuZh, string>` —— 带插值的
 * 条目值是函数不是 string，标了整张表都不匹配（理由同 `usage.ts` 顶上那段）。
 * zh/en 的键对齐由 `index.test.ts` 守着。
 */

/** 带参数文案的入参类型。★ 必须显式标：这个文件拿不到 `index.tsx` 里 `Messages` 的上下文。 */
type Params = Record<string, string | number>

export const accountMenuZh = {
  /** 触发按钮的可访问名。按钮上的可见文字是用户名，不能说清「点它会发生什么」 */
  'accountMenu.label': '账户菜单',
  'accountMenu.balance': '余额',
  'accountMenu.balanceRefresh': '刷新余额',
  'accountMenu.balanceRefreshFailed': ({ message }: Params) => `刷新余额失败：${message}`,
  'accountMenu.invite': '邀请好友',
  'accountMenu.inviteHint': '可以获取免费模型',
  'accountMenu.checkUpdates': '检查更新',
  'accountMenu.updateAvailable': ({ version }: Params) => `发现新版本 ${version}`,
  'accountMenu.updateLatest': '当前已是最新版本。',
  'accountMenu.updateFailed': '检查更新失败，请稍后重试。',
  'accountMenu.help': '帮助反馈',
  'accountMenu.signOutFailed': ({ message }: Params) => `退出登录失败：${message}`
}

export const accountMenuEn = {
  'accountMenu.label': 'Account menu',
  'accountMenu.balance': 'Credit balance',
  'accountMenu.balanceRefresh': 'Refresh balance',
  'accountMenu.balanceRefreshFailed': ({ message }: Params) => `Could not refresh the balance: ${message}`,
  'accountMenu.invite': 'Invite friends',
  'accountMenu.inviteHint': 'Earn free model credit',
  'accountMenu.checkUpdates': 'Check for updates',
  'accountMenu.updateAvailable': ({ version }: Params) => `Version ${version} is available`,
  'accountMenu.updateLatest': "You're up to date.",
  'accountMenu.updateFailed': 'Could not check for updates. Please try again later.',
  'accountMenu.help': 'Help & feedback',
  'accountMenu.signOutFailed': ({ message }: Params) => `Sign out failed: ${message}`
}
