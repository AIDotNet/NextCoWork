/**
 * 对话右侧导航的数据投影。
 *
 * ★ 一条刻度对应的是一整轮「用户提问 + 下一次提问前的所有回复」，不是一条
 * `ThreadRow`。工具循环、压缩分隔线会把一次回答拆成多行；按行做刻度会让同一轮
 * 占掉半条导航轨，也会让用户分不清该点哪一条。
 */
import type { AgentMessage } from '../../../../shared/agent/message'
import { visibleText } from '../../../../shared/agent/message'
import { assistantText, type ThreadRow } from './thread-content'

const TITLE_LENGTH = 56
const DESCRIPTION_LENGTH = 96

export type IndexedThreadRow = {
  row: ThreadRow
  index: number
}

export type ThreadTurnGroup = {
  key: string
  rows: IndexedThreadRow[]
  navigationId?: string
  prompt?: AgentMessage
}

export type TurnNavigationItem = {
  id: string
  label: string
  description?: string
}

/** 保留第一条用户消息之前的系统补位行，但不为它制造一个不存在的用户回合。 */
export function threadTurnGroups(rows: readonly ThreadRow[]): ThreadTurnGroup[] {
  const groups: ThreadTurnGroup[] = []
  let current: ThreadTurnGroup | undefined

  rows.forEach((row, index) => {
    if (row.kind === 'user') {
      current = {
        key: `turn:${row.message.id}`,
        navigationId: row.message.id,
        prompt: row.message,
        rows: []
      }
      groups.push(current)
    } else if (current === undefined) {
      current = { key: `turn:preamble:${row.key}`, rows: [] }
      groups.push(current)
    }
    current.rows.push({ row, index })
  })

  return groups
}

function normalizePreviewText(text: string): string {
  return text.replace(/\s+/g, ' ').trim()
}

export function truncateTurnPreview(text: string, limit: number): string {
  const characters = [...normalizePreviewText(text)]
  if (characters.length <= limit) return characters.join('')
  return `${characters.slice(0, limit).join('').trim()}…`
}

function promptLabel(message: AgentMessage, fallback: string): string {
  const text = normalizePreviewText(visibleText(message))
  if (text !== '') return truncateTurnPreview(text, TITLE_LENGTH)
  const files = message.parts
    .filter((part) => part.type === 'file_ref')
    .map((part) => part.name)
  return files.length === 0 ? fallback : truncateTurnPreview(files.join(', '), TITLE_LENGTH)
}

function responsePreview(group: ThreadTurnGroup): string | undefined {
  const text = normalizePreviewText(group.rows
    .filter((entry): entry is IndexedThreadRow & { row: Extract<ThreadRow, { kind: 'assistant' }> } =>
      entry.row.kind === 'assistant')
    .map((entry) => assistantText(entry.row.blocks))
    .filter((part) => part !== '')
    .join(' '))
  return text === '' ? undefined : truncateTurnPreview(text, DESCRIPTION_LENGTH)
}

/**
 * 每个回合的刻度**按 group 对象缓存**。
 *
 * ★ 理由和 `thread-content.threadHistoryRows` 是同一个:长会话流式期间,历史组
 * 对象引用不变,而每来一个 token 都要重新算全部刻度的话,每轮的 `responsePreview`
 * 都要把它下面所有 assistant 块拼一遍取前 96 字。缓存以 group 身份为键,流式那
 * 一组每帧是新对象、自然不会被缓存命中 —— 只有它需要重算。
 *
 * 缓存的键还带 `untitledLabel`:同一组在切换语言后要重新出文案。
 *
 * ★ 缓存省的是每组那两段字符串构造(`promptLabel` / `responsePreview` 要遍历该组
 * 全部 assistant 块);`turnNavigationItems` 本身仍会新建结果数组,因为调用方的
 * memo 依赖 `turns`,`turns` 每 token 变一次。要挡的是「几百轮的预览文案跟着重算」,
 * 不是这个 O(turns) 的浅拷贝。
 */
const navigationCache = new WeakMap<ThreadTurnGroup, { label: string; item: TurnNavigationItem | null }>()

function navigationItemFor(group: ThreadTurnGroup, untitledLabel: string): TurnNavigationItem | null {
  if (group.navigationId === undefined || group.prompt === undefined) return null
  const cached = navigationCache.get(group)
  if (cached !== undefined && cached.label === untitledLabel) return cached.item
  const description = responsePreview(group)
  const item: TurnNavigationItem = {
    id: group.navigationId,
    label: promptLabel(group.prompt, untitledLabel),
    ...(description === undefined ? {} : { description })
  }
  navigationCache.set(group, { label: untitledLabel, item })
  return item
}

export function turnNavigationItems(
  groups: readonly ThreadTurnGroup[],
  untitledLabel: string
): TurnNavigationItem[] {
  return groups.flatMap((group) => {
    const item = navigationItemFor(group, untitledLabel)
    return item === null ? [] : [item]
  })
}
