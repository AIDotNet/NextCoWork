/**
 * 一份消息的**可搜索正文** —— FTS 虚表的 `content` 列就是它。
 *
 * ## 为什么单独一个模块
 *
 * 两个写入路径必须算出**逐字相同**的字符串:
 * - `db/repo.ts` 的 `upsertFts`(每条消息正常落盘时);
 * - `db/legacy-merge.ts` 的补索引(旧库行级合并把行整条搬进来时)。
 *
 * 各写一份的话,同一段对话在两条路径上命中的词不一样,而**没有任何测试会红**
 * —— 表现是「旧库迁进来的会话搜得到一部分、搜不到另一部分」这种只在特定机器
 * 上复现的怪事。放进仓库根下这个**叶子模块**(不 import 任何仓库内模块),
 * 是为了让 `legacy-merge` 能拿走它而不引入「合并 → repo → 数据库句柄」那条依赖:
 * 合并跑在 `openDatabase()` **之前**,那时候主连接还不存在。
 *
 * 容错是刻意的:整条不是数组就当空,单个坏块(循环引用、getter 抛错)只丢它
 * 自己。迁移路径上一条畸形行都不该让整次启动失败,而搜索少一个词远好过卡住。
 */
import type { DatabaseSync, StatementSync } from 'node:sqlite'
import type { ContentPart } from '../../shared/agent/message'

export function searchableMessageText(parts: unknown): string {
  if (!Array.isArray(parts)) return ''
  const chunks: string[] = []
  for (const part of parts as readonly ContentPart[]) {
    if (part === null || typeof part !== 'object') continue
    try {
      switch (part.type) {
        case 'text':
        case 'thinking':
          if (typeof part.text === 'string') chunks.push(part.text)
          break
        case 'tool_call':
          if (typeof part.name === 'string') chunks.push(part.name)
          if (part.input !== undefined) chunks.push(JSON.stringify(part.input) ?? '')
          break
        case 'tool_result': {
          const output = (part as { output?: unknown }).output
          const content = typeof output === 'object' && output !== null
            ? (output as { content?: unknown }).content
            : undefined
          if (typeof content === 'string') chunks.push(content)
          break
        }
        case 'subagent':
          if (typeof part.summary === 'string') chunks.push(part.summary)
          break
        case 'error': {
          const error = (part as { error?: unknown }).error
          const message = typeof error === 'object' && error !== null
            ? (error as { message?: unknown }).message
            : undefined
          if (typeof message === 'string') chunks.push(message)
          break
        }
        default:
          break
      }
    } catch {
      // 单个坏块只丢它自己,不丢整条消息。
    }
  }
  return chunks.filter(Boolean).join('\n')
}

/*
  ## FTS 行一律按 rowid 增删改

  `messages_fts` 的 `message_id` / `session_id` 是 UNINDEXED 列。FTS5 只认 rowid 和
  MATCH 两种索引,`WHERE message_id = ?` 是**全表扫描**,要把 `%_content` 整张读一遍
  —— 本机 21 万条消息、约 1 GB 的索引上实测每次 0.3~0.65 秒,而它曾经挂在每条消息
  落盘的同步路径上,发一次消息就让主进程卡几秒。

  `messages_fts_rowid`(V37)是一张普通表,把 message_id / session_id 映射到 FTS 行的
  rowid。它独立于 `messages` 的 rowid:`messages` 是 TEXT 主键,VACUUM 会重排它的
  隐式 rowid,FTS 那边却不会跟着变。

  下面的函数是读写这两张表的**唯一入口**。`prepare` 由调用方给:repo 传带缓存的
  `stmt`,迁移和旧库合并传裸连接的 `prepare`。
*/

export type PrepareStatement = (sql: string) => StatementSync

/** 写入或替换一条消息的索引行,沿用它原有的 FTS rowid。调用方负责事务。 */
export function writeMessageFts(
  prepare: PrepareStatement,
  messageId: string,
  sessionId: string,
  title: string,
  content: string
): void {
  const existing = prepare('SELECT fts_rowid FROM messages_fts_rowid WHERE message_id = ?').get(messageId)
  let rowid: number | bigint
  if (existing === undefined) {
    rowid = prepare('INSERT INTO messages_fts_rowid (message_id, session_id) VALUES (?, ?)').run(messageId, sessionId)
      .lastInsertRowid
    /*
      ★ 新分到的 rowid 按理在 FTS 里是空位。占着它的只可能是 V37 之前的版本写进来的
      无映射行(见 `reconcileMessageFts`);不先腾掉,下面的 INSERT 会被 FTS5 拒成
      一句不带上下文的 `constraint failed`,整条消息落不了盘。
    */
    prepare('DELETE FROM messages_fts WHERE rowid = ?').run(rowid)
  } else {
    rowid = existing['fts_rowid'] as number | bigint
    prepare('DELETE FROM messages_fts WHERE rowid = ?').run(rowid)
    prepare('UPDATE messages_fts_rowid SET session_id = ? WHERE fts_rowid = ?').run(sessionId, rowid)
  }
  prepare('INSERT INTO messages_fts (rowid, message_id, session_id, title, content) VALUES (?, ?, ?, ?, ?)').run(
    rowid,
    messageId,
    sessionId,
    title,
    content
  )
}

/** 删掉一个会话的全部索引行。 */
export function deleteSessionFts(prepare: PrepareStatement, sessionId: string): void {
  prepare('DELETE FROM messages_fts WHERE rowid IN (SELECT fts_rowid FROM messages_fts_rowid WHERE session_id = ?)').run(sessionId)
  prepare('DELETE FROM messages_fts_rowid WHERE session_id = ?').run(sessionId)
}

/** 删掉一个会话里、`messages` 表已经没有的那些消息的索引行。 */
export function deleteOrphanSessionFts(prepare: PrepareStatement, sessionId: string): void {
  const orphans = `SELECT fts_rowid FROM messages_fts_rowid
    WHERE session_id = ? AND message_id NOT IN (SELECT id FROM messages WHERE session_id = ?)`
  prepare(`DELETE FROM messages_fts WHERE rowid IN (${orphans})`).run(sessionId, sessionId)
  prepare(`DELETE FROM messages_fts_rowid WHERE fts_rowid IN (${orphans})`).run(sessionId, sessionId)
}

/** 会话改名后同步索引里冗余的标题列。 */
export function renameSessionFts(prepare: PrepareStatement, sessionId: string, title: string): void {
  prepare('UPDATE messages_fts SET title = ? WHERE rowid IN (SELECT fts_rowid FROM messages_fts_rowid WHERE session_id = ?)').run(
    title,
    sessionId
  )
}

/** 清空整个索引。 */
export function clearMessageFts(prepare: PrepareStatement): void {
  prepare('DELETE FROM messages_fts').run()
  prepare('DELETE FROM messages_fts_rowid').run()
}

function hasRowidMap(database: DatabaseSync): boolean {
  return database.prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'messages_fts_rowid'").get() !== undefined
}

/**
 * 接受调用方的真实连接，启动迁移不得触发应用的内存库兜底。调用方负责事务。
 *
 * ★ 旧库合并跑在迁移之前,目标库可能还停在 V37 之前,没有映射表。那时退回旧写法
 * (不写 rowid、按 UNINDEXED 列删);之后 V37 会从现有 FTS 行补齐映射。
 */
function indexMessageRows(database: DatabaseSync, sessionId?: string): number {
  const rows = database.prepare(
    `SELECT m.id, m.session_id, m.parts, s.title FROM messages m JOIN sessions s ON s.id = m.session_id
     ${sessionId === undefined ? '' : 'WHERE m.session_id = ?'} ORDER BY m.id`
  ).iterate(...(sessionId === undefined ? [] : [sessionId]))
  const mapped = hasRowidMap(database)
  const prepare: PrepareStatement = (sql) => database.prepare(sql)
  const insert = database.prepare('INSERT INTO messages_fts (message_id, session_id, title, content) VALUES (?, ?, ?, ?)')
  let count = 0
  for (const row of rows) {
    let parts: unknown = []
    try { parts = JSON.parse(String(row['parts'])) } catch { /* 损坏正文只索引标题 */ }
    const args = [String(row['id']), String(row['session_id']), String(row['title'] ?? ''), searchableMessageText(parts)] as const
    if (mapped) writeMessageFts(prepare, ...args)
    else insert.run(...args)
    count++
  }
  return count
}

export function backfillSessionFts(database: DatabaseSync, sessionId: string): number {
  if (sessionId === '') return 0
  if (hasRowidMap(database)) deleteSessionFts((sql) => database.prepare(sql), sessionId)
  else database.prepare('DELETE FROM messages_fts WHERE session_id = ?').run(sessionId)
  return indexMessageRows(database, sessionId)
}

/**
 * 把映射表最大 rowid 之后、没有映射的 FTS 行补进映射。每次开库跑一遍。
 *
 * ★ 来源:V37 之前的版本(例如 /Applications 里还没升级的那份)开了同一个库。
 *   它按 message_id 删、不带 rowid 插,FTS 自增出的 rowid 全落在映射之外;
 *   新版下一次分配映射 rowid 就会撞上它们,发消息直接报 `constraint failed`。
 *
 * - 只看 `rowid > 映射最大值` 那一段:FTS 按 rowid 走 B 树,正常库上是一次空查找。
 * - 同一条消息有映射也有新行时,映射改指向新行(旧版写新行之前已经按 message_id
 *   删了旧行);同一条消息多个新行按 rowid 升序处理,最后留下的是最新那行。
 * - 剩下仍无映射的行(消息已不在)直接删掉。
 * - 尽力而为:索引坏了/没了(`searchAll` 有 LIKE 兜底)不能拦住开库,失败只回滚这一段。
 */
export function reconcileMessageFts(database: DatabaseSync): void {
  if (!hasRowidMap(database)) return
  database.exec('SAVEPOINT reconcile_fts')
  try {
    const row = database.prepare('SELECT coalesce(max(fts_rowid), 0) AS max FROM messages_fts_rowid').get()
    /*
      ★ 必须按 BigInt 绑定。node:sqlite 把 JS number 绑成 REAL,FTS5 遇到 REAL 的
      `rowid > ?` 不走 rowid 区间而是整表扫(本机库实测 220ms vs 2ms);
      `rowid = ?` 不受影响。
    */
    const max = BigInt((row?.['max'] as number | bigint | undefined) ?? 0)
    database.prepare(
      `INSERT INTO messages_fts_rowid (fts_rowid, message_id, session_id)
         SELECT f.rowid, f.message_id, f.session_id FROM messages_fts f
         WHERE f.rowid > ? AND EXISTS (SELECT 1 FROM messages m WHERE m.id = f.message_id) ORDER BY f.rowid
       ON CONFLICT(message_id) DO UPDATE SET fts_rowid = excluded.fts_rowid, session_id = excluded.session_id`
    ).run(max)
    database.prepare(
      'DELETE FROM messages_fts WHERE rowid > ? AND rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid WHERE fts_rowid > ?)'
    ).run(max, max)
    database.exec('RELEASE reconcile_fts')
  } catch (err) {
    database.exec('ROLLBACK TO reconcile_fts')
    database.exec('RELEASE reconcile_fts')
    console.warn('[db] 全文索引映射对账失败,跳过:', err)
  }
}

/** 一次性修复历史漏索引，同时清掉已删除消息的幽灵结果。 */
export function rebuildMessageFts(database: DatabaseSync): number {
  database.exec('DELETE FROM messages_fts')
  if (hasRowidMap(database)) database.exec('DELETE FROM messages_fts_rowid')
  return indexMessageRows(database)
}
