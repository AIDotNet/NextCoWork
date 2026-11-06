/**
 * 供应商账号列表的文案(设置 › 模型 › 某家供应商 › 账号)。
 *
 * 单独一个文件 —— 照 `git.ts` / `usage.ts` 的先例(§6.3):`index.tsx` 已经三千多行,
 * 新的域不再往里堆。
 *
 * ★ `providerAccountsEn` **不要**标 `Record<keyof typeof providerAccountsZh, string>`:
 * 带插值的条目值是函数不是 string,标了整张表都不匹配。zh/en 的键对齐由
 * `index.test.ts` 在运行时逐键比对,那比类型管得更准。
 */

/** 见 `usage.ts` 的同名注释:不显式标的话参数是 implicit any,spread 进 ZH 时整张表失配 */
type Params = Record<string, string | number>

export const providerAccountsZh = {
  'providerAccount.section': '账号',
  'providerAccount.sectionHint': '限流时会自动换到下一个可用账号，恢复后自动切回。',
  'providerAccount.add': '添加账号',
  'providerAccount.adding': '正在登录…',
  'providerAccount.empty': '还没有登录任何账号',
  'providerAccount.count': ({ count }: Params) => `${count} 个账号`,
  'providerAccount.unnamed': '未命名账号',
  /* ★ 「当前」和「下一个会用」是两件事,见 selectAccount 的注释 —— 文案也必须分开 */
  'providerAccount.current': '当前',
  'providerAccount.currentHint': '导出与旧版本回退使用这个账号',
  'providerAccount.active': '下一次请求会用它',
  'providerAccount.setCurrent': '设为当前账号',
  'providerAccount.remove': '删除账号',
  'providerAccount.removeConfirm': ({ name }: Params) => `删除账号「${name}」？登录态会一并清除。`,
  'providerAccount.reauth': '重新登录',
  'providerAccount.enable': '启用账号',
  'providerAccount.disable': '停用账号',
  'providerAccount.rename': '重命名',
  'providerAccount.renamePlaceholder': '备注名（留空则显示邮箱）',
  'providerAccount.dragHint': '拖动调整顺序，靠前的先用',
  'providerAccount.moveUp': '上移',
  'providerAccount.moveDown': '下移',

  'providerAccount.badge.ready': '可用',
  'providerAccount.badge.disabled': '已停用',
  'providerAccount.badge.needsReauth': '登录已失效',
  'providerAccount.badge.limited': '限流中',
  'providerAccount.limitedUntil': ({ time }: Params) => `${time} 恢复`,
  'providerAccount.countdownHours': ({ hours, minutes }: Params) => `还有 ${hours} 小时 ${minutes} 分`,
  'providerAccount.countdownMinutes': ({ minutes, seconds }: Params) => `还有 ${minutes} 分 ${seconds} 秒`,
  'providerAccount.countdownSeconds': ({ seconds }: Params) => `还有 ${seconds} 秒`,
  'providerAccount.clearLimit': '立即解除限流',
  'providerAccount.limitReason': ({ reason }: Params) => `上游返回：${reason}`,
  'providerAccount.limitBySource.quota': '额度已用尽',
  'providerAccount.limitBySource.rateLimit': '被上游限流',
  'providerAccount.limitBySource.manual': '手动停用',

  'providerAccount.quota.title': '额度',
  'providerAccount.quota.5h': '5 小时',
  'providerAccount.quota.week': '本周',
  'providerAccount.quota.other': ({ minutes }: Params) => `${minutes} 分钟窗口`,
  'providerAccount.quota.used': ({ percent }: Params) => `已用 ${percent}%`,
  'providerAccount.quota.resetsAt': ({ time }: Params) => `${time} 重置`,
  /* ★ 「没有数据」和「0%」必须是两句话 —— 见 provider-accounts.ts 的 QuotaBar 注释 */
  'providerAccount.quota.empty': '尚未获取，发一条消息后更新',
  'providerAccount.quota.fetchableEmpty': '尚未获取，点「刷新额度」拉取',
  'providerAccount.quota.stale': ({ hours }: Params) => `${hours} 小时前的数据`,
  'providerAccount.quota.refresh': '刷新额度',
  'providerAccount.quota.refreshing': '正在获取…',
  'providerAccount.quota.failed': '额度获取失败',
  /* ── 聊天圆环菜单里的「套餐额度」区块(数据同上,布局更窄) ── */
  'providerAccount.quota.menuTitle': '套餐额度',
  'providerAccount.quota.remaining': ({ percent }: Params) => `剩 ${percent}%`,
  'providerAccount.quota.menuEmpty': '这个账号还没有额度数据',
  'providerAccount.quota.noAccount': '未找到可查询额度的账号',

  'providerAccount.rotation': '账号自动切换',
  'providerAccount.rotationHint': '某个账号被限流时自动换到下一个；关闭后只用当前账号。'
}

export const providerAccountsEn = {
  'providerAccount.section': 'Accounts',
  'providerAccount.sectionHint':
    'Rate-limited accounts are skipped automatically and picked back up once they recover.',
  'providerAccount.add': 'Add account',
  'providerAccount.adding': 'Signing in…',
  'providerAccount.empty': 'No accounts signed in yet',
  'providerAccount.count': ({ count }: Params) => `${count} account${count === 1 ? '' : 's'}`,
  'providerAccount.unnamed': 'Unnamed account',
  'providerAccount.current': 'Current',
  'providerAccount.currentHint': 'Used for exports and older app versions',
  'providerAccount.active': 'Next request uses this one',
  'providerAccount.setCurrent': 'Set as current',
  'providerAccount.remove': 'Remove account',
  'providerAccount.removeConfirm': ({ name }: Params) =>
    `Remove account “${name}”? Its sign-in will be cleared.`,
  'providerAccount.reauth': 'Sign in again',
  'providerAccount.enable': 'Enable account',
  'providerAccount.disable': 'Disable account',
  'providerAccount.rename': 'Rename',
  'providerAccount.renamePlaceholder': 'Label (defaults to the email address)',
  'providerAccount.dragHint': 'Drag to reorder — the top one is used first',
  'providerAccount.moveUp': 'Move up',
  'providerAccount.moveDown': 'Move down',

  'providerAccount.badge.ready': 'Ready',
  'providerAccount.badge.disabled': 'Disabled',
  'providerAccount.badge.needsReauth': 'Sign-in expired',
  'providerAccount.badge.limited': 'Rate limited',
  'providerAccount.limitedUntil': ({ time }: Params) => `recovers at ${time}`,
  'providerAccount.countdownHours': ({ hours, minutes }: Params) => `${hours}h ${minutes}m left`,
  'providerAccount.countdownMinutes': ({ minutes, seconds }: Params) => `${minutes}m ${seconds}s left`,
  'providerAccount.countdownSeconds': ({ seconds }: Params) => `${seconds}s left`,
  'providerAccount.clearLimit': 'Clear rate limit now',
  'providerAccount.limitReason': ({ reason }: Params) => `Upstream said: ${reason}`,
  'providerAccount.limitBySource.quota': 'Quota exhausted',
  'providerAccount.limitBySource.rateLimit': 'Rate limited upstream',
  'providerAccount.limitBySource.manual': 'Disabled manually',

  'providerAccount.quota.title': 'Quota',
  'providerAccount.quota.5h': '5 hours',
  'providerAccount.quota.week': 'This week',
  'providerAccount.quota.other': ({ minutes }: Params) => `${minutes}-minute window`,
  'providerAccount.quota.used': ({ percent }: Params) => `${percent}% used`,
  'providerAccount.quota.resetsAt': ({ time }: Params) => `resets at ${time}`,
  'providerAccount.quota.empty': 'No data yet — updates after your next message',
  'providerAccount.quota.fetchableEmpty': 'No data yet — use “Refresh quota”',
  'providerAccount.quota.stale': ({ hours }: Params) => `${hours}h old`,
  'providerAccount.quota.refresh': 'Refresh quota',
  'providerAccount.quota.refreshing': 'Fetching…',
  'providerAccount.quota.failed': 'Failed to fetch quota',
  'providerAccount.quota.menuTitle': 'Plan quota',
  'providerAccount.quota.remaining': ({ percent }: Params) => `${percent}% left`,
  'providerAccount.quota.menuEmpty': 'No quota data for this account yet',
  'providerAccount.quota.noAccount': 'No account to query quota for',

  'providerAccount.rotation': 'Switch accounts automatically',
  'providerAccount.rotationHint':
    'Move to the next account when one is rate limited. When off, only the current account is used.'
}
