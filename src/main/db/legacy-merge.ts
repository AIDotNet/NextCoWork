/**
 * 把**旧库里当前库没有的行**合并进来。
 *
 * ## 它解决的是哪一类丢数据
 *
 * `main/index.ts` 原先的 `prepareProjectDatabaseDirectory()` 第一句是
 * `if (existsSync(targetPath)) return targetDir` —— 目标库一旦存在,扁平布局收拢和
 * 旧数据根复制**两段都不再执行**,而且一个字的日志都不落。
 *
 * 于是这条路径必然发生:某次启动在 `data/` 下建了一个库(哪怕只是空跑过一次),
 * 之后用户继续用根层的旧库工作几小时,再启动时新库已经是权威,那几小时的会话
 * 永远不会出现在界面上 —— 用户看到的是「我的会话没了」,而日志里没有任何线索。
 * 本机实测就是这么丢的 15 条会话 / 2741 条消息。
 *
 * ## 三条不变式
 *
 * 1. **只 INSERT。** 不 UPDATE、不 DELETE、不 REPLACE 目标库的任何一行。
 *    合并是「补上缺的」,不是「让两边一致」—— 后者意味着要判断哪边新,
 *    而那个判断一旦错就是静默覆盖用户的新数据。
 * 2. **按主键 id 判缺失。** id 是 ULID,两个库各自生成的 id 不可能撞;
 *    本机实测 273 条会话 / 4 万条消息零碰撞。id 也在的话就是同一行,跳过
 *    (而不是比时间戳决定谁赢 —— 那正是第 1 条要避免的判断)。
 *
 *    ★ 判据必须覆盖**每一张**表,不能只判 `sessions`:子表的行不一定跟着父表走。
 *    `usage_records` 只增不删(`schema.ts` 第 20 条)、也没有指向 `sessions` 的外键,
 *    会话被删掉时它的用量行留在库里 —— 于是「这条会话缺 ⇒ 它的子行也缺」是错的,
 *    而错的表现是一次启动停在闸门上:`UNIQUE constraint failed: usage_records.id`。
 * 3. **每个会话一个事务。** 一条会话连同它的消息/runs/附件/计划一起进去,
 *    要么全在要么全不在。中途失败时已提交的会话留着,重跑会从缺失的那些继续
 *    —— 于是「重试」是幂等的,不需要先回滚。
 *
 * ## 为什么列名是运行时读出来的
 *
 * 源库可能是任意历史版本(本机那份停在 schema v22,缺 V23 的两张表)。
 * 硬编码列名的话,每加一列都要回来改这里,而**改漏了不会报错** ——
 * `INSERT INTO t (a,b) SELECT a,b` 少一列就是那一列永远为空。
 * 这里取「源与目标的列交集」,并按目标的顺序排:新列在源里没有就让它拿默认值,
 * 源里已经删掉的列自然被排除。
 *
 * ## 为什么是 ATTACH 而不是开两个连接
 *
 * 差集和拷贝都要「读源、写目标」。开两个连接就得把源的 id 全捞到 JS 里求差集
 * —— 4 万条消息就是 4 万个字符串穿过 V8 堆,在启动路径上这一步本身就是可感知的
 * 卡顿。ATTACH 之后是一次 SQL 的事。
 *
 * ★ 而且**必须**用 `file:…?mode=ro` 这种 URI 形式附加。直接
 * `ATTACH '路径' AS legacy_source` 给的是一个**可写**句柄,本模块「只 INSERT
 * 目标库」这条不变式就只剩注释在保证了。URI 形式下对源库的任何写都是
 * `attempt to write a readonly database`,由 SQLite 兜底。
 *
 * ## 与 flat-layout.ts 的分工
 *
 * `migrateFlatLayout` 管的是**同一次布局升级**(根层的库搬进 `data/`),
 * 那是 rename,搬完旧的不存在了。本文件管的是**已经分叉的两个库**,
 * 是行级合并,源库原样留着当回滚兜底。
 */
import { copyFileSync, existsSync, mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import type { MigrationErrorCode } from '../../shared/domain/data-migration'
import { backfillSessionFts, deleteSessionFts } from './message-text'

/**
 * 撤销清单在 `kv` 里的键。
 *
 * ★ 存 `kv` 而不是数据根下一个 JSON 文件:那正是这个表存在的意义,
 * 而且它会跟着 `storage:export` / 备份一起走。数据根下多一个只有本模块认识的
 * 文件,下一个人清理数据时既不敢删也不知道它是干什么的。
 */
export const MIGRATION_UNDO_KEY = 'data-migration.undo'
/** 迁入行的账户归属交接清单。和撤销清单分开，重试 / 多来源不会改变撤销边界。 */
export const MIGRATION_CLAIM_KEY = 'data-migration.workspace-claims'

/** 附加库在 ATTACH 时用的别名。★ 固定值,所有 SQL 都按它限定源表。 */
const SOURCE_SCHEMA = 'legacy_source'

/**
 * 随会话一起搬的表,按**插入顺序**。
 *
 * ★ 顺序不能重排:`attachments.message_id` 引用 `messages(id)`,
 * `file_snapshots.run_id` 引用 `file_change_sets(run_id)`,而写连接上外键约束是
 * 打开的。父表排在子表前面,插到一半失败时事务能干净地回滚。
 *
 * `by` 说明这张表靠哪一列挂到本次要搬的会话上:
 * - `id`      —— 就是会话表自己
 * - `session` —— 直接有 `session_id`
 * - `plan`    —— 有 `plan_id`,挂到本次搬进来的 `plans` / `plans_v2` 上
 * - `run`     —— 有 `run_id`,挂到本次搬进来的 `file_change_sets` 上
 *
 * `file_snapshots` 没有 id(主键是 `run_id` + `file_path`),所以它只能走 `run`。
 */
const SESSION_TABLES = [
  { table: 'sessions', by: 'id' },
  { table: 'messages', by: 'session' },
  { table: 'runs', by: 'session' },
  { table: 'usage_records', by: 'session' },
  { table: 'attachments', by: 'session' },
  { table: 'context_checkpoints', by: 'session' },
  { table: 'file_change_sets', by: 'session' },
  { table: 'plans', by: 'session' },
  { table: 'plans_v2', by: 'session' },
  { table: 'scheduled_runs', by: 'session' },
  { table: 'plan_revisions', by: 'plan', parent: 'plans' },
  { table: 'plan_revisions_v2', by: 'plan', parent: 'plans_v2' },
  { table: 'file_snapshots', by: 'run' }
] as const

/** 旧库里当前库没有的东西。 */
export interface LegacyDelta {
  sessions: number
  messages: number
  attachments: number
  workspaces: number
}

export const EMPTY_DELTA: LegacyDelta = { sessions: 0, messages: 0, attachments: 0, workspaces: 0 }

/**
 * 这份 delta 是否值得让用户等一次迁移屏。
 *
 * ★ 判据是**会话数**,不是消息数。消息数大于 0 而会话数为 0 的状态(目标库里有
 *   会话、但它的消息没进来)是本模块**修不了**的 —— 本模块只会去补「目标库里
 *   整条都缺的会话」。把它也算成「需要迁移」的话,闸门会为一件做不到的事
 *   卡住用户,而合并跑完 delta 纹丝不动,变成永远修不完的循环。
 */
export function hasAnythingToMerge(delta: LegacyDelta): boolean {
  return delta.sessions > 0
}

/**
 * 把原始异常归类。
 *
 * ★ 分档的唯一目的是让错误页知道**该给哪些出口**:磁盘满是重试有用的典型,
 * 权限问题重试和跳过都没用、只有「打开数据目录」有意义。一律归成 unknown 的话
 * 那个页面就只能给一句「失败了」,而用户手里没有任何可执行的下一步。
 *
 * 匹配的是 SQLite / Node 的英文原文,**不翻译** —— 见 `MigrationFailure.detail`。
 */
export function classifyMigrationError(err: unknown): MigrationErrorCode {
  const text = `${String(err)} ${err instanceof Error ? err.message : ''}`
  if (/SQLITE_FULL|database or disk is full|ENOSPC/i.test(text)) return 'disk-full'
  if (
    /SQLITE_READONLY|SQLITE_CANTOPEN|EACCES|EPERM|permission denied|operation not permitted|unable to open database/i.test(
      text
    )
  ) {
    return 'permission'
  }
  if (/SQLITE_CORRUPT|malformed|file is not a database|not a database/i.test(text)) return 'source-corrupt'
  if (/SQLITE_BUSY|database is locked/i.test(text)) return 'target-locked'
  return 'unknown'
}

/**
 * 把路径编成 SQLite 能认的 file: URI 主体。
 *
 * ★ **不能用 `encodeURI`。** 它按 RFC 3986 保留了 `#` 和 `?`,而在 URI 里
 *   `#` 之后是 fragment、`?` 之后是 query —— 一个含 `#` 的路径会被**静默截断**
 *   成另一个路径。截断后的路径大概率不存在,于是报错是「unable to open database」
 *   加一个用户根本没听说过的路径,而真正的原因(他的用户名里有个 `#`)完全不出现。
 *
 * `%` 必须第一个换:先换别的字符会引入新的 `%`,再换 `%` 就把它们二次编码了。
 */
function encodeUriPath(path: string): string {
  return path.replaceAll('%', '%25').replaceAll('#', '%23').replaceAll('?', '%3F')
}

/**
 * 把一个路径塞进 SQL 字面量。
 *
 * ★ ATTACH 的路径是**不能参数化**的(SQLite 只接受字面量),所以这里只能自己转义。
 *   只把 `'` 变成 `''` 是 SQLite 规定的转义法。数据根是应用自己算出来的、
 *   不含引号的路径,但用户名里带 `'` 的机器是存在的 —— 那时一个未转义的引号
 *   就是一次启动失败,且报错信息完全看不出和用户名有关。
 */
function escapeSqlLiteral(value: string): string {
  return value.replaceAll("'", "''")
}

/**
 * 以只读方式附加源库。
 *
 * ★ 用 URI + `mode=ro`(见文件头)。直接 `ATTACH '路径'` 给的是可写句柄,
 *   「只 INSERT 目标库」这条不变式就只剩注释在保证了。
 */
function attachSourceReadOnly(target: DatabaseSync, sourcePath: string): void {
  target.exec(
    `ATTACH DATABASE 'file:${escapeSqlLiteral(encodeUriPath(sourcePath))}?mode=ro' AS ${SOURCE_SCHEMA}`
  )
}

function tableExists(d: DatabaseSync, table: string, schema = 'main'): boolean {
  const row = d
    .prepare(`SELECT 1 AS ok FROM ${schema}.sqlite_master WHERE type = 'table' AND name = ?`)
    .get(table)
  return row !== undefined
}

/**
 * 源与目标共有的列,按**目标**的顺序。
 *
 * 生成列(`hidden !== 0`)排除掉:它们不能出现在 INSERT 的列清单里。
 */
function sharedColumns(target: DatabaseSync, table: string): string[] {
  const names = (schema: string): string[] =>
    target
      .prepare(`PRAGMA ${schema}.table_info(${table})`)
      .all()
      .filter((c) => Number(c['hidden'] ?? 0) === 0)
      .map((c) => String(c['name']))
  const sourceNames = new Set(names(SOURCE_SCHEMA))
  return names('main').filter((name) => sourceNames.has(name))
}

function countRows(d: DatabaseSync, sql: string): number {
  return Number(d.prepare(sql).get()?.['n'] ?? 0)
}

/**
 * 目标表的主键列,按主键序号排。
 *
 * ★ 从 `PRAGMA table_info` 读,不写死 `id`:本模块插的表里有一张的主键是复合的
 *   (`file_snapshots` = `run_id` + `file_path`),写死的话那一张的判重会**静默**
 *   失效 —— 于是又回到「只有唯一约束抛错才发现」的老路,而那正是这次要修的东西。
 */
function primaryKeyColumns(target: DatabaseSync, table: string): string[] {
  return target
    .prepare(`PRAGMA main.table_info(${table})`)
    .all()
    .filter((c) => Number(c['pk'] ?? 0) > 0)
    .sort((a, b) => Number(a['pk'] ?? 0) - Number(b['pk'] ?? 0))
    .map((c) => String(c['name']))
}

/** 差集的统一写法:`源表里 id 不在目标表里的行数`。 */
function missingCount(d: DatabaseSync, table: string): number {
  if (!tableExists(d, table) || !tableExists(d, table, SOURCE_SCHEMA)) return 0
  return countRows(
    d,
    `SELECT COUNT(*) AS n FROM ${SOURCE_SCHEMA}."${table}" s ` +
      `WHERE NOT EXISTS (SELECT 1 FROM "${table}" t WHERE t.id = s.id)`
  )
}

/**
 * 目标库里缺了源库的哪些行。**纯读**,不碰任何一个库的写路径。
 *
 * 启动路径上每个「要不要迁移」的判断都走这里 —— 没有东西要搬时它只做几次
 * `COUNT`,这是绝大多数启动会走的分支,所以不能顺手做任何别的准备。
 *
 * ★ 探测失败一律当成「没有东西要搬」。理由是失败原因里绝大多数是
 *   「这份源库压根不是我们的库 / 已经损坏」,而这两种情况下**启动必须继续**:
 *   为了一个用不上的旧库把用户挡在门外,比丢那部分旧数据更糟。
 *   真正的合并错误会在 `mergeLegacyRows` 里以可分类的形式再抛一次,那时才有得处置。
 *
 * ★ 但这条只对**源库**成立。目标库读不出来是另一回事:它是应用马上要打开的那个库,
 *   读不出来意味着启动本身有问题。把它也降级成「没有东西要搬」正是这次丢数据的形态
 *   —— 探测失败 → 判定无需迁移 → 静默继续 → 用户看到一份少了东西的数据,
 *   而日志里一个字都没有。所以目标库那几步的异常**一路抛到闸门上**。
 */
export function probeLegacyDelta(targetPath: string, sourcePath: string): LegacyDelta {
  if (!existsSync(targetPath) || !existsSync(sourcePath) || targetPath === sourcePath) {
    return EMPTY_DELTA
  }
  const target = new DatabaseSync(targetPath, { readOnly: true })
  try {
    if (!tableExists(target, 'sessions')) return EMPTY_DELTA
    try {
      attachSourceReadOnly(target, sourcePath)
    } catch {
      return EMPTY_DELTA
    }
    if (!tableExists(target, 'sessions', SOURCE_SCHEMA)) return EMPTY_DELTA
    try {
      return {
        sessions: missingCount(target, 'sessions'),
        messages: missingCount(target, 'messages'),
        attachments: missingCount(target, 'attachments'),
        workspaces: missingCount(target, 'workspaces')
      }
    } catch {
      // 差集查询失败 = 源库里那几张表的结构和我们对不上。当「这份源库用不了」处理。
      return EMPTY_DELTA
    }
  } finally {
    closeQuietly(target)
  }
}

function closeQuietly(d: DatabaseSync | null): void {
  try {
    d?.close()
  } catch {
    /* 已经关掉,或者压根没打开成功 */
  }
}

export interface MergeOptions {
  /** 目标库(当前在用的那份)。**只会被 INSERT**。 */
  targetPath: string
  /** 源库(旧的那份)。以 `mode=ro` 附加,写它会被 SQLite 拒掉。 */
  sourcePath: string
  /** 每提交完一条会话调一次。用于进度条。 */
  onProgress?: (done: number, total: number) => void
}

export interface MergeResult {
  sessions: number
  messages: number
  attachments: number
  workspaces: number
  /** 本次新建的工作区 id。撤销时要删掉它们。 */
  createdWorkspaces: string[]
  /** 本次插进去的会话 id。撤销时按它精确删除。 */
  createdSessions: string[]
}

function emptyResult(): MergeResult {
  return {
    sessions: 0,
    messages: 0,
    attachments: 0,
    workspaces: 0,
    createdWorkspaces: [],
    createdSessions: []
  }
}

/** 缺了哪些 id。按主键判，按 id 升序保证重试时进度与失败点可复现。 */
function missingIds(d: DatabaseSync, table: string): string[] {
  if (!tableExists(d, table) || !tableExists(d, table, SOURCE_SCHEMA)) return []
  return d
    .prepare(
      `SELECT s.id AS id FROM ${SOURCE_SCHEMA}."${table}" s ` +
        `WHERE NOT EXISTS (SELECT 1 FROM "${table}" t WHERE t.id = s.id) ORDER BY s.id`
    )
    .all()
    .map((r) => String(r['id']))
}

/**
 * 把源库里缺的行补进目标库。
 *
 * @returns 本次实际插入了什么。**没有东西可搬时返回全零,不抛错** ——
 *   重试路径会重复调用它,「已经搬完了」是正常结果,不是异常。
 */
export function mergeLegacyRows(options: MergeOptions): MergeResult {
  if (!existsSync(options.targetPath) || !existsSync(options.sourcePath)) return emptyResult()
  if (options.targetPath === options.sourcePath) return emptyResult()

  const result = emptyResult()
  const target = new DatabaseSync(options.targetPath, { enableForeignKeyConstraints: true })
  try {
    if (!tableExists(target, 'sessions')) return result
    attachSourceReadOnly(target, options.sourcePath)
    if (!tableExists(target, 'sessions', SOURCE_SCHEMA)) return result

    const workspaceIds = missingIds(target, 'workspaces')
    const sessionIds = missingIds(target, 'sessions')

    // 工作区先落:它没有父表,而且会话的 workspace_id 要能在界面上查到名字。
    if (workspaceIds.length > 0) {
      const columns = sharedColumns(target, 'workspaces')
      target.exec('BEGIN')
      try {
        insertByIds(target, 'workspaces', columns, workspaceIds)
        writeMigrationClaimToDatabase(target, { sessions: [], workspaces: workspaceIds })
        target.exec('COMMIT')
      } catch (err) {
        target.exec('ROLLBACK')
        throw err
      }
      result.workspaces = workspaceIds.length
      result.createdWorkspaces = [...workspaceIds]
    }

    const total = sessionIds.length
    for (const [index, sessionId] of sessionIds.entries()) {
      target.exec('BEGIN')
      try {
        const counts = insertSession(target, sessionId)
        writeMigrationClaimToDatabase(target, { sessions: [sessionId], workspaces: [] })
        target.exec('COMMIT')
        result.sessions += 1
        result.messages += counts.messages
        result.attachments += counts.attachments
        result.createdSessions.push(sessionId)
      } catch (err) {
        target.exec('ROLLBACK')
        throw err
      }
      options.onProgress?.(index + 1, total)
    }
    return result
  } finally {
    // ★ 这个句柄必须关。紧接着 openDatabase() 就要打开同一个文件,
    //   漏一个在 Windows 上就是一次启动失败(flat-layout.ts 有同一条注释)。
    closeQuietly(target)
  }
}

/** 一条会话连同它的全部子行。调用方负责 BEGIN / COMMIT。 */
function insertSession(target: DatabaseSync, sessionId: string): { messages: number; attachments: number } {
  const counts = { messages: 0, attachments: 0 }
  for (const spec of SESSION_TABLES) {
    if (!tableExists(target, spec.table) || !tableExists(target, spec.table, SOURCE_SCHEMA)) continue
    const columns = sharedColumns(target, spec.table)
    if (columns.length === 0) continue
    let inserted = 0
    switch (spec.by) {
      case 'id':
        inserted = insertByIds(target, spec.table, columns, [sessionId])
        break
      case 'session':
        inserted = insertByColumn(target, spec.table, columns, 'session_id', [sessionId])
        break
      case 'plan':
        // 孙表:挂到**本次搬进来的**那批计划上,而不是源库里同 id 的所有计划。
        inserted = insertByColumn(target, spec.table, columns, 'plan_id', [sessionId], {
          from: spec.parent,
          column: 'session_id'
        })
        break
      case 'run':
        // file_snapshots 没有 id,只能按 run_id 挂到本次搬进来的变更集上。
        inserted = insertByColumn(target, spec.table, columns, 'run_id', [sessionId], {
          from: 'file_change_sets',
          column: 'session_id'
        })
        break
    }
    if (spec.table === 'messages') counts.messages = inserted
    if (spec.table === 'attachments') counts.attachments = inserted
  }
  if (tableExists(target, 'messages_fts') && tableExists(target, 'messages')) {
    backfillSessionFts(target, sessionId)
  }
  return counts
}

/**
 * `INSERT INTO target SELECT … FROM legacy_source`。
 *
 * ★ 用 `INSERT` 不用 `INSERT OR IGNORE`。主键已经判过缺失,真撞上唯一约束说明
 *   我们的判断错了 —— 那时候**要让它抛**,让这次会话整体回滚、把错误摆到闸门上。
 *   用 OR IGNORE 的话这种错会静默变成「少了几条消息」,而那正是这次要消灭的
 *   那类故障。
 *
 * ★ 子表那条路(`insertByColumn`)把判重写进了 SQL,但同样没有用 OR IGNORE:
 *   它判的是子表自己的主键,判完之后唯一约束仍然是兜底 —— 非主键的唯一性
 *   (某个索引、某条外键)坏了照样抛。
 */
function insertByIds(
  target: DatabaseSync,
  table: string,
  columns: readonly string[],
  ids: readonly string[]
): number {
  if (ids.length === 0) return 0
  const placeholders = ids.map(() => '?').join(', ')
  const list = columns.map((c) => `"${c}"`).join(', ')
  const sql =
    `INSERT INTO "${table}" (${list}) ` +
    `SELECT ${list} FROM ${SOURCE_SCHEMA}."${table}" WHERE id IN (${placeholders})`
  return Number(target.prepare(sql).run(...ids).changes ?? 0)
}

/**
 * 按「某个父表里属于这条会话的行的 id」收窄后再插。
 *
 * `parent` 为空时收窄条件就是列本身;给定时先从 `parent` 表里选出属于这条会话的
 * 父行 id,再用它筛子表 —— 这是 `plan_revisions` / `file_snapshots` 这两张
 * 没有 `session_id` 的表唯一能挂上来的方式。
 *
 * ★ **判重写在 SQL 里,用的是子表自己的主键。** 子表的「缺」推不出来:会话缺了
 *   不等于它的每一行都缺(`usage_records` 的行活得比会话长,见文件头第 2 条)。
 *   只按父表判的话,一条会话重搬时会撞上 `UNIQUE constraint failed:
 *   usage_records.id` —— 启动停在闸门上,而错误页上那句话完全看不出是「多判了」。
 */
function insertByColumn(
  target: DatabaseSync,
  table: string,
  columns: readonly string[],
  column: string,
  values: readonly string[],
  parent?: { from: string; column: string }
): number {
  if (values.length === 0) return 0
  const list = columns.map((c) => `"${c}"`).join(', ')
  const placeholders = values.map(() => '?').join(', ')
  const scope =
    parent === undefined
      ? `"${column}" IN (${placeholders})`
      : `"${column}" IN (SELECT id FROM "${parent.from}" WHERE "${parent.column}" IN (${placeholders}))`
  const pk = primaryKeyColumns(target, table)
  /*
    ★ 没有声明主键的表(理论上会出现)不加这一条 —— 那等于回到旧行为,而不是
    把它当成「什么都不用判」。主键为 NULL 的行也漏得过去(SQLite 的 UNIQUE 里
    NULL 互不相等),但那种行本来也撞不上唯一约束,不构成新的故障。
  */
  const dedupe =
    pk.length === 0
      ? ''
      : ` AND NOT EXISTS (SELECT 1 FROM "${table}" x WHERE ${pk
          .map((c) => `x."${c}" = s."${c}"`)
          .join(' AND ')})`
  const sql =
    `INSERT INTO "${table}" (${list}) ` +
    `SELECT ${list} FROM ${SOURCE_SCHEMA}."${table}" s WHERE ${scope}${dedupe}`
  return Number(target.prepare(sql).run(...values).changes ?? 0)
}

/**
 * 找出本次要搬的会话引用的附件文件,算出它们在新根下的落点。
 *
 * ★ **拆成「先算、后拷」两步,是为了让拷贝能报进度。** 只搬行不搬文件的话,
 * `ncw://` 协议只认 `databaseDirectory()/attachments`
 * (见 `net/attachment-protocol.ts`),行搬过去了、文件还在旧根,表现是老会话里的
 * 图全是碎图 —— 而库本身完全健康,排查时不会有人想到路径上去。
 *
 * 返回的是 `[源文件, 目标文件]` 对,调用方拿着它去 `copyAttachmentFiles`。
 * 这里只读不写。
 */
export function collectAttachmentFiles(
  sourcePath: string,
  sourceRoot: string,
  targetDir: string,
  sessionIds: readonly string[]
): Array<{ from: string; to: string }> {
  if (sessionIds.length === 0 || !existsSync(sourcePath)) return []
  const from = join(sourceRoot, 'attachments')
  const to = join(targetDir, 'attachments')
  let source: DatabaseSync | null = null
  try {
    source = new DatabaseSync(sourcePath, { readOnly: true })
    if (!tableExists(source, 'attachments')) return []
    const rows = source
      .prepare(
        `SELECT path FROM attachments WHERE session_id IN (${sessionIds.map(() => '?').join(', ')})`
      )
      .all(...sessionIds)
    const pairs: Array<{ from: string; to: string }> = []
    for (const row of rows) {
      const path = typeof row['path'] === 'string' ? row['path'] : ''
      // 不在源根下的路径不是这次该搬的(可能是用户自己引用的一份外部文件),
      // 强行按源根改写出一个落点只会把它拷到一个无意义的位置。
      if (path === '' || !isUnder(from, path)) continue
      pairs.push({ from: path, to: to + path.slice(from.length) })
    }
    return pairs
  } catch {
    // 附件读不出来只降级成「这一批文件不搬」,不让它掀掉已经成功的行合并。
    return []
  } finally {
    closeQuietly(source)
  }
}

/**
 * 把算好的附件文件拷进新根。
 *
 * ★ 已存在就跳过:文件名是附件 id(ULID),同名即同一份内容。
 *   这也是「重跑一次」不会重复写盘的原因。
 *
 * ★ 源文件缺失(用户手删过)只跳过、不抛错。丢一个附件的缩略图,不该让整次启动失败。
 *
 * @returns 真正拷过来的文件路径。
 */
export function copyAttachmentFiles(
  pairs: readonly { from: string; to: string }[],
  onProgress?: (done: number, total: number) => void
): string[] {
  const copied: string[] = []
  for (const [index, pair] of pairs.entries()) {
    try {
      if (existsSync(pair.to) || !existsSync(pair.from)) continue
      mkdirSync(dirname(pair.to), { recursive: true })
      copyFileSync(pair.from, pair.to)
      copied.push(pair.to)
    } catch {
      // 单个文件拷不动(权限、路径过长)不该中断整批 —— 其余的还要搬。
    } finally {
      onProgress?.(index + 1, pairs.length)
    }
  }
  return copied
}

/**
 * `path` 是否就是 `root` 本身或它下面的一条。
 *
 * ★ 不能写成 `startsWith(root)`:那样 `/a/attachments-backup` 会被判成
 * `/a/attachments` 下的一条,然后被拷到一个同样错的落点。
 * 这个实现与 `flat-layout.ts` 里那份**必须保持一致** —— 两处判据不同的话,
 * 会出现「路径改写认了、文件拷贝不认」这种一半对一半不对的状态。
 */
export function isUnder(root: string, path: string): boolean {
  return path === root || path.startsWith(`${root}/`) || path.startsWith(`${root}\\`)
}

/**
 * 把刚合并进来的附件行的 `path` 从旧根改写到新根。
 *
 * ★ **不改写的话这些附件在界面上是碎的。** `ncw://` 协议只认
 * `databaseDirectory()/attachments`(见 `net/attachment-protocol.ts` 的
 * `resolveAttachmentPath` → `isWithinRoot`),而行里的 `path` 是**旧根下的绝对
 * 路径** —— 协议判它落在根外并拒绝。文件明明拷过来了、库也完全健康,但图显示
 * 不出来,而排查时不会有人想到路径上去。
 *
 * ★ 只改**本次合并进来的**那些行(按会话 id 收窄),不碰目标库原有的行。
 * 目标库里可能也有指向旧根的历史行,那不是这次该管的事 —— 顺手全改会把一次
 * 「补上缺的」变成一次全库重写,而全库重写正是这套设计一直在避免的东西。
 *
 * ★ 同样只改 `path` 落在旧根 `attachments/` 下面的行。引用用户自己文件系统里
 * 某个外部路径的附件(不是应用管理的)不该被动。
 *
 * @returns 改写了多少行。
 */
export function rewriteMergedAttachmentPaths(
  targetPath: string,
  sourceRoot: string,
  targetDir: string,
  sessionIds: readonly string[]
): number {
  if (sessionIds.length === 0) return 0
  const from = join(sourceRoot, 'attachments')
  const to = join(targetDir, 'attachments')
  if (from === to) return 0
  const target = new DatabaseSync(targetPath)
  try {
    const rows = target
      .prepare(
        `SELECT id, path FROM attachments WHERE session_id IN (${sessionIds.map(() => '?').join(', ')})`
      )
      .all(...sessionIds)
    let changed = 0
    for (const row of rows) {
      const path = typeof row['path'] === 'string' ? row['path'] : ''
      if (path === '' || !isUnder(from, path)) continue
      const next = to + path.slice(from.length)
      if (next === path) continue
      target.prepare('UPDATE attachments SET path = ? WHERE id = ?').run(next, String(row['id']))
      changed += 1
    }
    return changed
  } finally {
    closeQuietly(target)
  }
}

/** 本次合并往目标库里写过的行 —— 撤销时按它精确删除。 */
export interface UndoManifest {
  at: number
  source: string
  sessions: string[]
  workspaces: string[]
  /** 本次拷过来的附件文件。**撤销时不删它们**,只记下来备查。 */
  files: string[]
}

/**
 * 迁入数据的账户归属交接状态。
 *
 * 这份状态故意不复用 `UndoManifest`：撤销只针对最近一次合并，而多次重试 / 多个旧根
 * 必须累积所有待重连会话；把两种边界混在一起会让「撤销本次」误删前一次迁入的数据。
 */
export interface MigrationClaim {
  sessions: string[]
  workspaces: string[]
  claimedBy?: string
  workspaceMap?: Record<string, string>
  /** 有新行写进清单后设 true；账户重连完成才设 false，避免每次登录态读取全量扫描。 */
  pending?: boolean
}

export function writeUndoManifest(targetPath: string, manifest: UndoManifest): void {
  const target = new DatabaseSync(targetPath)
  try {
    target
      .prepare('INSERT OR REPLACE INTO kv (key, json) VALUES (?, ?)')
      .run(MIGRATION_UNDO_KEY, JSON.stringify(manifest))
  } finally {
    closeQuietly(target)
  }
}

/**
 * 撤销一次合并。**只删清单里记着的 id**,不是「恢复到某个快照」——
 * 所以合并之后用户新产生的会话不受影响。
 *
 * ★ 删会话会级联带走它的消息/runs/附件行(`ON DELETE CASCADE`),但**外键约束
 *   是连接级开关**,这里必须显式打开,否则级联静默失效,留下一堆指向已删会话的
 *   悬空行。
 *
 * ★ 附件文件**不删**。它们在新根下,和用户之后可能重新导入的同一份内容同名同源;
 *   删文件是不可逆的,而多留几个孤儿文件只是占地方。
 */
export function undoMerge(targetPath: string, manifest: UndoManifest): void {
  const target = new DatabaseSync(targetPath, { enableForeignKeyConstraints: true })
  try {
    target.exec('BEGIN')
    try {
      const remove = (table: string, ids: readonly string[]): void => {
        if (ids.length === 0) return
        const placeholders = ids.map(() => '?').join(', ')
        target.prepare(`DELETE FROM "${table}" WHERE id IN (${placeholders})`).run(...ids)
      }
      if (tableExists(target, 'messages_fts_rowid')) {
        for (const sessionId of manifest.sessions) deleteSessionFts((sql) => target.prepare(sql), sessionId)
      } else if (tableExists(target, 'messages_fts')) {
        const removeFts = target.prepare('DELETE FROM messages_fts WHERE session_id = ?')
        for (const sessionId of manifest.sessions) removeFts.run(sessionId)
      }
      remove('sessions', manifest.sessions)
      remove('workspaces', manifest.workspaces)
      /*
        账户归属时创建的工作区副本故意不在这里删：合并之后用户可能已在里面新建会话、
        修改设置或重新指向同一目录。撤销旧行不能连带删掉这些后续工作，最多留下空副本。
      */
      target.prepare('DELETE FROM kv WHERE key = ?').run(MIGRATION_UNDO_KEY)
      target.exec('COMMIT')
    } catch (err) {
      target.exec('ROLLBACK')
      throw err
    }
  } finally {
    closeQuietly(target)
  }
}

function manifestIds(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  const ids = new Set<string>()
  for (const id of value) {
    if (typeof id === 'string' && id !== '') ids.add(id)
  }
  return [...ids]
}

/** 账户归属映射只接受完整的 string → string 条目，坏项绝不参与跨作用域重连。 */
function claimWorkspaceMap(value: unknown): Record<string, string> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return {}
  const map: Record<string, string> = {}
  for (const [source, target] of Object.entries(value)) {
    if (source !== '' && typeof target === 'string' && target !== '') map[source] = target
  }
  return map
}

/** 读回撤销清单。没有(或存坏了)时返回 null。 */
export function parseUndoManifest(value: unknown): UndoManifest | null {
  if (typeof value !== 'string') return null
  try {
    const parsed: unknown = JSON.parse(value)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null
    const record = parsed as Record<string, unknown>
    return {
      at: Number(record['at'] ?? 0),
      source: String(record['source'] ?? ''),
      sessions: manifestIds(record['sessions']),
      workspaces: manifestIds(record['workspaces']),
      files: manifestIds(record['files'])
    }
  } catch {
    return null
  }
}

/** 解析账户归属清单。它和撤销清单分开，因此不要求 source / files 字段。 */
export function parseMigrationClaim(value: unknown): MigrationClaim | null {
  if (typeof value !== 'string') return null
  try {
    const parsed: unknown = JSON.parse(value)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null
    const record = parsed as Record<string, unknown>
    const claimedBy = typeof record['claimedBy'] === 'string' && record['claimedBy'] !== ''
      ? record['claimedBy']
      : undefined
    const workspaceMap = claimWorkspaceMap(record['workspaceMap'])
    const pending = record['pending'] === true ? true : record['pending'] === false ? false : undefined
    return {
      sessions: manifestIds(record['sessions']),
      workspaces: manifestIds(record['workspaces']),
      ...(claimedBy === undefined ? {} : { claimedBy }),
      ...(Object.keys(workspaceMap).length === 0 ? {} : { workspaceMap }),
      ...(pending === undefined ? {} : { pending })
    }
  } catch {
    return null
  }
}

/** 已发布的旧版本只有撤销清单；升级后首次认领把它当成一份初始账户归属清单。 */
export function migrationClaimFromUndo(value: unknown): MigrationClaim | null {
  const undo = parseUndoManifest(value)
  return undo === null ? null : { sessions: undo.sessions, workspaces: undo.workspaces }
}

/**
 * 合并两份归属清单，并在真的发现新 id 时重新标记 pending。
 *
 * 需求：写入路径和读取路径都要用同一套并集规则。只在写入时合并旧撤销清单会让
 * 「先升级、再降级迁入、又升级」那批数据被已有 claim 遮住，账户永远看不见它们。
 */
export function mergeMigrationClaims(
  existing: MigrationClaim | null,
  incoming: MigrationClaim,
  forcePending = false
): MigrationClaim {
  const existingSessions = existing?.sessions ?? []
  const existingWorkspaces = existing?.workspaces ?? []
  const sessions = manifestIds([...existingSessions, ...incoming.sessions])
  const workspaces = manifestIds([...existingWorkspaces, ...incoming.workspaces])
  const added = sessions.length > existingSessions.length || workspaces.length > existingWorkspaces.length
  const workspaceMap = { ...(incoming.workspaceMap ?? {}), ...(existing?.workspaceMap ?? {}) }
  const claimedBy = existing?.claimedBy ?? incoming.claimedBy
  return {
    sessions,
    workspaces,
    ...(claimedBy === undefined ? {} : { claimedBy }),
    ...(Object.keys(workspaceMap).length === 0 ? {} : { workspaceMap }),
    pending: forcePending || existing?.pending === true || added
  }
}

/**
 * 累积所有待账户重连的行，独立于「只撤销最近一次」的撤销边界。
 *
 * 需求：行级合并按会话提交，失败后重试只会返回剩余 id。丢掉已提交那段的 id 会使它们
 * 永远留在 local 工作区，表现为账户里只缺一部分历史会话且重试后也不会恢复。
 */
export function writeMigrationClaim(targetPath: string, claim: MigrationClaim): void {
  const target = new DatabaseSync(targetPath)
  try {
    writeMigrationClaimToDatabase(target, claim)
  } finally {
    closeQuietly(target)
  }
}

/** 读回累计归属清单；附件重试靠它找回前一次已提交、但还没搬文件的会话。 */
export function readMigrationClaim(targetPath: string): MigrationClaim | null {
  if (!existsSync(targetPath)) return null
  let target: DatabaseSync | null = null
  try {
    target = new DatabaseSync(targetPath, { readOnly: true })
    const row = target.prepare('SELECT json FROM kv WHERE key = ?').get(MIGRATION_CLAIM_KEY)
    return parseMigrationClaim(row?.['json'])
  } catch {
    return null
  } finally {
    closeQuietly(target)
  }
}

/**
 * 把归属清单和刚提交的行放进**同一 SQLite 事务**。
 *
 * 需求：磁盘满等错误可能在一条会话提交后立刻让第二个连接写 kv 失败。若清单不是
 * 同事务写入，已提交的会话没有任何可追溯 id，后续重试只会看见剩余行并把前半批
 * 永久留在 local。
 */
function writeMigrationClaimToDatabase(target: DatabaseSync, claim: MigrationClaim): void {
  const existing = parseMigrationClaim(target.prepare('SELECT json FROM kv WHERE key = ?').get(MIGRATION_CLAIM_KEY)?.['json'])
  // 需求：旧版本随时可能把最后一批迁入行写回撤销清单。每次都把它并进归属清单，
  // 否则已有归属清单会遮住降级期间迁入的数据，账户会永久少一部分历史。
  const legacy = migrationClaimFromUndo(
    target.prepare('SELECT json FROM kv WHERE key = ?').get(MIGRATION_UNDO_KEY)?.['json']
  )
  const base = legacy === null ? existing : mergeMigrationClaims(existing, legacy)
  const merged = mergeMigrationClaims(base, claim, true)
  target
    .prepare('INSERT OR REPLACE INTO kv (key, json) VALUES (?, ?)')
    .run(MIGRATION_CLAIM_KEY, JSON.stringify(merged))
}

/** 读回撤销清单。没有(或存坏了)时返回 null。 */
export function readUndoManifest(targetPath: string): UndoManifest | null {
  if (!existsSync(targetPath)) return null
  let target: DatabaseSync | null = null
  try {
    target = new DatabaseSync(targetPath, { readOnly: true })
    const row = target.prepare('SELECT json FROM kv WHERE key = ?').get(MIGRATION_UNDO_KEY)
    return parseUndoManifest(row?.['json'])
  } catch {
    // 清单坏了就当没有撤销能力 —— 但**不能**因此阻止启动。
    return null
  } finally {
    closeQuietly(target)
  }
}
