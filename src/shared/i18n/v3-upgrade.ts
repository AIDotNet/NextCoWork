/**
 * 「数据已由 NextCoWork 3.0 接管」的系统对话框文案(主进程,在建窗之前弹)。
 *
 * ★ 和 `document-engine.ts` 一样放在共享纯表里:这一刻没有渲染层,也还没打开数据库(读不到设置里的语言),
 *   语言取系统 locale。
 */
const zh = {
  'v3.owned.message': '这份数据已经由 NextCoWork 3.0 接管',
  'v3.owned.detail':
    'NextCoWork 2.x 不能再打开它:两个版本同时写同一份数据会把数据写坏。请打开 NextCoWork 3.0 继续使用,你的项目和会话都在那里。',
  'v3.owned.open': '打开 NextCoWork 3.0',
  'v3.owned.download': '下载 NextCoWork 3.0',
  'v3.owned.quit': '退出'
} as const

type MessageKey = keyof typeof zh

const en: Record<MessageKey, string> = {
  'v3.owned.message': 'This data has been taken over by NextCoWork 3.0',
  'v3.owned.detail':
    'NextCoWork 2.x can no longer open it: two versions writing the same data would corrupt it. Open NextCoWork 3.0 to continue — your projects and conversations are there.',
  'v3.owned.open': 'Open NextCoWork 3.0',
  'v3.owned.download': 'Download NextCoWork 3.0',
  'v3.owned.quit': 'Quit'
}

/** `locale` 以 `zh` 开头用中文,其余用英文(和应用的两种语言一致)。 */
export function v3UpgradeMessage(locale: string, key: MessageKey): string {
  return (locale.toLowerCase().startsWith('zh') ? zh : en)[key]
}
