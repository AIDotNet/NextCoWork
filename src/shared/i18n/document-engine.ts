/**
 * 需求：原生退出确认在 renderer 尚未启动或正在关闭时也必须可用。
 * 文案以稳定 key 放在共享纯数据表，不把 React/i18n store 引入主进程；后续编辑器 UI 可复用此表。
 */
const zh = {
  'documents.quit.cancel': '取消退出',
  'documents.quit.discard': '丢弃更改并退出',
  'documents.quit.blocked': '文档仍在处理或还有未保存的更改。',
  'documents.quit.detail': '取消退出后可保存文档。选择“丢弃更改并退出”将永久丢弃所有未保存的文档更改；磁盘上的原文件不会被覆盖。'
} as const

type MessageKey = keyof typeof zh

const en: Record<MessageKey, string> = {
  'documents.quit.cancel': 'Cancel quit',
  'documents.quit.discard': 'Discard changes and quit',
  'documents.quit.blocked': 'Documents are still processing or have unsaved changes.',
  'documents.quit.detail': 'Cancel to save your documents. Discarding will permanently lose all unsaved document changes; original files on disk will not be overwritten.'
}

export function documentEngineMessage(locale: string, key: MessageKey): string {
  return (locale === 'zh-CN' ? zh : en)[key]
}
