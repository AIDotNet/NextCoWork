/**
 * SQLite 地基。**全应用唯一持有数据库句柄的地方。**
 *
 * 会话、消息、运行记录、附件和 FTS 也由这里的同一连接持久化。主进程的
 * `message_commit` 会逐条写入，最终的 `replaceHistory` 只负责异常收敛与顺序校验；
 * 不再存在独立的内存转录事实源。
 *
 * ## 规矩
 *
 * - **所有 SQL 收在 `src/main/db/` 内**,handler 和 `state/store.ts` 都不写 SQL。
 *   这不是风格洁癖:`DatabaseSync` 是**同步 API,会阻塞主进程**,将来把整个 Db
 *   挪进 `utilityProcess` 是唯一的出路,而那一步的前提就是调用点全走访问器。
 *   同样的理由:聚合查询一律带 LIMIT 和时间窗,`VACUUM` 只在空闲时跑。
 * - 迁移**只增不改**(见 `schema.ts`)。
 * - 应用数据库默认落在**用户主目录**的 `.next-cowork/data/` 下 —— 与 cwd、与是否打包
 *   都无关;调用方仍可通过 `openDatabase(dir)` 为测试或特殊部署指定目录。
 */
import { AsyncLocalStorage } from 'node:async_hooks'
import { lstatSync, mkdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { DatabaseSync, type StatementSync } from 'node:sqlite'
import { MIGRATIONS } from './schema'
import { rebuildMessageFts, reconcileMessageFts } from './message-text'

/** 库文件名。`-wal` / `-shm` 是 SQLite 自己在同目录建的兄弟文件。 */
export const DB_FILENAME = 'nextcowork.db'
export const DATABASE_DIRNAME = '.next-cowork'
/**
 * 应用数据在 profile 根下的子目录名。
 *
 * ★ 它存在的理由是**布局**,不是功能:`~/.next-cowork` 同时是 Electron 的 userData,
 * Chromium 把三十来个 profile 条目(`Cache` / `Cookies` / `Preferences` …)扁平铺在
 * 它的根层。应用自己的库和文件树再挤进同一层,两套命名风格交错,谁也认不出哪些是
 * 用户的数据、哪些是随手能删的缓存。分一层出来:根层归 Chromium,这里归我们。
 */
export const DATA_SUBDIRNAME = 'data'

const MEMORY = ':memory:'

/**
 * Electron profile 根: `~/.next-cowork/`。Chromium 的 userData 指的就是这里。
 *
 * ★ **不能用 `process.cwd()` 派生**。打包后的 cwd 没有任何意义:Windows 从快捷方式
 * 启动时它是安装目录(数据会在卸载/升级时被一起清掉),从别的目录双击 exe 又会
 * 凭空开出一个空库 —— 用户看到的是「我的会话全没了」,而不是任何一条能指向
 * 工作目录的线索。主目录对两种运行方式都稳定。
 *
 * 要隔离(探针、多实例)走命令行的 `--user-data-dir`,见 `main/index.ts` 的
 * `resolveProfileRoot()`。
 */
export function defaultProfileRoot(): string {
  return join(homedir(), DATABASE_DIRNAME)
}

/**
 * 默认数据目录: `~/.next-cowork/data/`。库、附件、skills / agents / commands 全在这儿。
 *
 * ★ 与 `defaultProfileRoot()` 刻意差一层 `data/` —— 理由见 `DATA_SUBDIRNAME`。
 * 「删除全部数据并退出」仍能一次清掉两边,靠的正是 `data/` 是 profile 根的**子目录**
 * 这一点(`ipc/storage.ts` 的 `clearLocalData` 把删除边界定在 profile 根上)。
 */
export function defaultDatabaseDirectory(): string {
  return join(defaultProfileRoot(), DATA_SUBDIRNAME)
}

/** 当前打开的文件库所在目录；测试和恢复流程可通过 openDatabase 指定目录。 */
export function databaseDirectory(): string {
  return handlePath === null || handlePath === MEMORY ? defaultDatabaseDirectory() : dirname(handlePath)
}

let handle: DatabaseSync | null = null
let handlePath: string | null = null
/**
 * 应用退出时封库。区别于 `closeDatabase()` 的**重置**语义(测试在 beforeEach 里关掉、
 * 下一次 `db()` 自动开内存库继续跑) —— 封库之后任何写入都必须失败,而不是悄悄换一个库。
 */
let sealed = false

/** 封库之后仍有人尝试读写。带上调用点比静默丢数据有用得多。 */
export class DatabaseClosedError extends Error {
  constructor() {
    super('数据库已在应用退出时关闭,这次写入没有落盘')
    this.name = 'DatabaseClosedError'
  }
}

/** 同步访问不能排队：明确拒绝，绝不借用另一个异步调用尚未提交的事务。 */
export class DatabaseBusyError extends Error {
  constructor() {
    super('数据库正在执行异步事务，请等待完成后再试')
    this.name = 'DatabaseBusyError'
  }
}

export class DatabaseTransactionClosedError extends Error {
  constructor() {
    super('事务已经结束，迟到的异步读写没有执行')
    this.name = 'DatabaseTransactionClosedError'
  }
}

interface TransactionOwner {
  database: DatabaseSync
  kind: 'sync' | 'async'
  active: boolean
}

const transactionContext = new AsyncLocalStorage<TransactionOwner>()
let activeTransaction: TransactionOwner | undefined
let asyncTransactionQueue: Promise<void> = Promise.resolve()

function assertTransactionAccess(): void {
  const owner = transactionContext.getStore()
  if (owner !== undefined && !owner.active) throw new DatabaseTransactionClosedError()
  if (activeTransaction !== undefined && activeTransaction !== owner) throw new DatabaseBusyError()
}

function releaseTransaction(owner: TransactionOwner): void {
  owner.active = false
  if (activeTransaction === owner) activeTransaction = undefined
}

/** 缓存的 statement 也在执行时复检，不能先取句柄、再在别人的事务中搭车。 */
function guardStatement(statement: StatementSync, database: DatabaseSync): StatementSync {
  const check = (): void => {
    assertTransactionAccess()
    if (handle !== database) throw new DatabaseClosedError()
  }
  return new Proxy(statement, {
    get(target, property) {
      const value: unknown = Reflect.get(target, property, target)
      if (typeof value !== 'function') return value
      return (...args: unknown[]): unknown => {
        check()
        const result: unknown = Reflect.apply(value, target, args)
        if (property !== 'iterate' || result === null || typeof result !== 'object') return result
        const guarded: object = new Proxy(result, {
          get(iterator, key): unknown {
            if (key === Symbol.iterator) return () => guarded
            const method: unknown = Reflect.get(iterator, key, iterator)
            if (typeof method !== 'function') return method
            return (...input: unknown[]): unknown => {
              if (key === 'next') check()
              return Reflect.apply(method, iterator, input)
            }
          }
        })
        return guarded
      }
    }
  })
}

/**
 * 预备语句缓存。`prepare()` 每次都要过一遍解析器,而 `listProviders()` 是
 * **每次路由决策都会走的**热路径(`runtime.ts` 的 `providerConfig.providers`)。
 *
 * ★ `StatementSync` 绑在具体的库句柄上,换库必须整个丢掉 —— 所以清空这个 Map
 * 是 `closeDatabase()` 的一部分,不是可选的优化。
 */
const prepared = new Map<string, StatementSync>()

export interface DatabaseOpenOptions {
  /** 独立服务确认命令前需要 FULL；现有桌面默认值保持不变。 */
  durability?: 'normal' | 'full'
}

function openAt(path: string, options: DatabaseOpenOptions = {}): DatabaseSync {
  const d = new DatabaseSync(path, {
    // 默认就是 true,写出来是因为 model_aliases 的 ON DELETE CASCADE 全靠它。
    // ★ 这是**连接级**开关,不是存在文件里的属性:换个连接忘了开,
    // 级联就静默失效,留下一堆指向已删 provider 的悬空别名。
    enableForeignKeyConstraints: true
  })

  // WAL:读不挡写。`:memory:` 上这句是无害的空转(内存库没有日志文件)。
  d.exec('PRAGMA journal_mode = WAL')
  // WAL 下 NORMAL 是标准搭配:崩溃不会坏库,最坏丢掉最后几个已提交事务。
  d.exec(options.durability === 'full' ? 'PRAGMA synchronous = FULL' : 'PRAGMA synchronous = NORMAL')
  /*
    单实例锁(`main/index.ts`)保证同一时刻只有一个进程开这个文件,所以争用
    几乎只会来自外面挂着的一个 sqlite3 CLI。超时取 2 秒而不是常见的 5 秒:
    `DatabaseSync` 是同步的,**等待期间整个主进程连同界面一起冻住** ——
    冻 5 秒比直接抛一个能看见的错更糟。
  */
  d.exec('PRAGMA busy_timeout = 2000')

  migrate(d)
  // 旧版本开过这个库会留下无映射的 FTS 行,不补上的话下一条消息就写不进去
  reconcileMessageFts(d)
  return d
}

/**
 * 迁移。**SQLite 的 DDL 本身是事务性的**,所以一条迁移要么整条生效、要么整条回滚,
 * 不会留下「建了三张表少建两张」的半截库 —— 这是下面那个 BEGIN/ROLLBACK 真正管用的原因。
 */
function migrate(d: DatabaseSync): void {
  d.exec(`
    CREATE TABLE IF NOT EXISTS migrations (
      version    INTEGER PRIMARY KEY,
      name       TEXT NOT NULL,
      applied_at INTEGER NOT NULL
    )
  `)

  const applied = new Set(
    d
      .prepare('SELECT version FROM migrations')
      .all()
      .map((r) => Number(r['version']))
  )

  const insert = d.prepare('INSERT INTO migrations (version, name, applied_at) VALUES (?, ?, ?)')

  for (const m of MIGRATIONS) {
    if (applied.has(m.version)) continue
    d.exec('BEGIN')
    try {
      // 第 7 版修复了一个曾被错误标记为第 5 版的列。部分数据库已
      // 执行过带 owner_id 的第 5 版,部分数据库没有;先检查再补列,
      // 让两条历史路径都能安全升级。
      if (m.version === 7) {
        const columns = d.prepare('PRAGMA table_info(attachments)').all()
        if (!columns.some((column) => String(column['name']) === 'owner_id')) {
          d.exec('ALTER TABLE attachments ADD COLUMN owner_id TEXT')
          d.exec('UPDATE attachments SET owner_id = session_id WHERE owner_id IS NULL')
        }
      }
      d.exec(m.sql)
      if (m.version === 29) rebuildMessageFts(d)
      // 审计时间戳走 Date.now() 而不是 `host.clock` 端口:数据库层够不着宿主,
      // 而这个值只用于「什么时候升的级」,没有任何测试或计价逻辑读它。
      insert.run(m.version, m.name, Date.now())
      d.exec('COMMIT')
    } catch (err) {
      d.exec('ROLLBACK')
      // `cause` 必须挂上:迁移失败时真正有用的是 SQLite 抛的原始错(哪条语句、哪一列),
      // 只保留 String(err) 的话,栈就断在这里了
      throw new Error(`迁移 ${m.version}(${m.name})失败,库已回滚到升级前: ${String(err)}`, {
        cause: err
      })
    }
  }

  /*
    `migrations` 表是权威记录(哪些跑过、什么时候跑的);`user_version` 是它的镜像,
    存在的意义是**不懂我们表结构的工具也能回答「这个文件是第几版」** ——
    随手一个 `sqlite3 nextcowork.db 'PRAGMA user_version'` 就够了。
    PRAGMA 不能用占位符,只能拼字符串;拼进去的是我们自己常量表里的整数,不是外部输入。
  */
  const latest = MIGRATIONS.reduce((max, m) => Math.max(max, m.version), 0)
  d.exec(`PRAGMA user_version = ${latest}`)
}

/**
 * 把库开在 `dir/nextcowork.db`。由 `main/index.ts` 在 app ready 时调**一次**,
 * 且必须排在 `initRuntime()` 之前 —— seed 会往 providers 表写东西。
 *
 * ★ 已经开着就抛错,不静默换库。看起来严厉,但它挡的正是本步骤要消灭的那个 bug:
 * 有人把这句挪到 `initRuntime()` 后面 → seed 写进了那个兜底的内存库 → 换成文件库时
 * 那些行凭空消失 → 症状是「配置重启后没了」,而这恰恰是**没有持久化时的原症状**,
 * 所以没人会怀疑到调用顺序上去。
 */
export function openDatabase(dir: string, options: DatabaseOpenOptions = {}): void {
  assertTransactionAccess()
  if (handle !== null) {
    throw new Error(
      handlePath === MEMORY
        ? '数据库已经以内存兜底方式打开过了 —— openDatabase() 必须排在任何读写 store 的代码之前(见 main/index.ts)'
        : `数据库已经打开:${handlePath}`
    )
  }
  /*
    ★ 目录不存在时 `DatabaseSync` 是**建不出文件**的,它只会抛
    `unable to open database file` —— 一条完全不提目录的错。全新安装、以及 dev 那个
    `…-dev` 后缀的路径都可能还没被谁建出来过,所以这一句管的是**首次启动那一次**,
    不是异常分支。
  */
  mkdirSync(dir, { recursive: true })
  handlePath = join(dir, DB_FILENAME)
  handle = openAt(handlePath, options)
  sealed = false
}

/**
 * 库句柄。没人指定过位置就开一个内存库。
 *
 * ★ 这**不是测试替身**,是「没声明位置」的正确答案 —— 和 `kernel/host.ts` 把
 * `nodeHost()` 定义成真实默认值同一个思路。于是无头测试跑的是和生产完全同一条
 * 装配路径,只是库落在内存里;而不是一条「测试专用」的分支。
 *
 * @internal 只给 `src/main/db/` 内部用。外面拿到句柄就等于外面开始写 SQL 了。
 */
export function db(): DatabaseSync {
  assertTransactionAccess()
  if (sealed) {
    /**
     * ★ 封库之后**不能**再走下面那条内存兜底 —— 那会凭空开一个跑完整套迁移的新库,
     * 把退出瞬间还在跑的 run 的转录和 run 记录静默写进去,然后连库带数据一起丢掉。
     * 一条日志都不会留。宁可在这里抛,让调用方显式处理。
     */
    throw new DatabaseClosedError()
  }
  if (handle === null) {
    handlePath = MEMORY
    handle = openAt(MEMORY)
  }
  return handle
}

/**
 * 预备语句(带缓存)。
 * @internal 同 `db()`。
 */
export function stmt(sql: string): StatementSync {
  const database = db()
  const cached = prepared.get(sql)
  if (cached !== undefined) return cached
  const s = guardStatement(database.prepare(sql), database)
  prepared.set(sql, s)
  return s
}

/**
 * 事务。
 *
 * ★ 嵌套时借用外层事务而不是再 BEGIN 一次 —— SQLite 不支持嵌套 BEGIN,
 * 硬来会抛「cannot start a transaction within a transaction」。
 * 于是访问器可以放心地互相调用,不必各自知道自己是不是最外层。
 *
 * @internal 同 `db()`。
 */
export function tx<T>(fn: () => T): T {
  const d = db()
  if (d.isTransaction) {
    const owner = transactionContext.getStore()
    if (owner === undefined || activeTransaction !== owner || owner.database !== d) throw new DatabaseBusyError()
    return fn()
  }
  const owner: TransactionOwner = { database: d, kind: 'sync', active: true }
  activeTransaction = owner
  return transactionContext.run(owner, () => {
    try {
      d.exec('BEGIN')
      const out = fn()
      if (out !== null && (typeof out === 'object' || typeof out === 'function')
        && typeof (out as { then?: unknown }).then === 'function') {
        // Promise 回调不能从同步事务逃逸；迟到的续体也会被所有者检查拦住。
        void Promise.resolve(out).catch(() => undefined)
        throw new TypeError('同步事务不能返回 Promise，请使用 txAsync')
      }
      d.exec('COMMIT')
      return out
    } catch (err) {
      try { if (d.isTransaction) d.exec('ROLLBACK') } catch { /* 保留原始错误 */ }
      throw err
    } finally {
      releaseTransaction(owner)
    }
  })
}

/**
 * 异步事务，供需要跨越安全存储/原生 API 的数据导入使用。
 *
 * `DatabaseSync` 本身是同步句柄，但导入的凭证写入接口是 Promise。若先
 * 提交 SQLite、再写凭证，第二个写入失败时就无法满足「导入失败 = 完全不变」。
 * 同一异步调用链的嵌套访问沿用外层事务；不同所有者的异步事务按 FIFO 排队。
 * await 期间无关同步读写明确报 busy，不能把它们静默并入或一起回滚。
 */
export async function txAsync<T>(fn: () => T | Promise<T>): Promise<T> {
  const current = transactionContext.getStore()
  if (current !== undefined) {
    assertTransactionAccess()
    if (current.kind === 'sync') throw new TypeError('同步事务不能嵌套异步事务，请使用 txAsync')
    return await fn()
  }
  // 排队时可以记下原连接，但不能读取未提交行；换库后的旧请求绝不写入新库。
  const d = handle ?? db()
  const previous = asyncTransactionQueue
  let next: () => void = () => {}
  asyncTransactionQueue = new Promise<void>((resolve) => { next = resolve })
  await previous
  try {
    if (handle !== d) throw new DatabaseClosedError()
    assertTransactionAccess()
    if (d.isTransaction) throw new DatabaseBusyError()
    const owner: TransactionOwner = { database: d, kind: 'async', active: true }
    activeTransaction = owner
    return await transactionContext.run(owner, async () => {
      try {
        d.exec('BEGIN')
        const out = await fn()
        d.exec('COMMIT')
        return out
      } catch (err) {
        try { if (d.isTransaction) d.exec('ROLLBACK') } catch { /* 原始错误更有用 */ }
        throw err
      } finally {
        releaseTransaction(owner)
      }
    })
  } finally {
    next()
  }
}

/** 关库。退出前调,或测试里重置。 */
export function closeDatabase(options: { final?: boolean } = {}): void {
  assertTransactionAccess()
  if (activeTransaction !== undefined) throw new DatabaseBusyError()
  prepared.clear()
  handle?.close()
  handle = null
  handlePath = null
  sealed = options.final === true
}

/**
 * 库文件与 WAL 的字节数(`storage:getStats` 用)。
 * 内存库、以及 WAL 刚被 checkpoint 掉的时候都会是 0 —— 那是事实,不是错误。
 */
export function fileStats(): { dbBytes: number; walBytes: number } {
  if (handlePath === null || handlePath === MEMORY) return { dbBytes: 0, walBytes: 0 }
  const size = (p: string): number => {
    try {
      // Database/WAL paths are application-managed files.  Never follow a
      // symlink here: a compromised or manually edited data directory must
      // not make storage statistics read the size of an unrelated external
      // file.  A symlink is reported as unavailable (0 bytes), just like a
      // missing file.
      const entry = lstatSync(p)
      return entry.isFile() ? entry.size : 0
    } catch {
      return 0
    }
  }
  return { dbBytes: size(handlePath), walBytes: size(`${handlePath}-wal`) }
}

/** 当前数据库主文件路径；未指定文件库时返回 null（测试内存库）。 */
export function databaseFilePath(): string | null {
  return handlePath === null || handlePath === MEMORY ? null : handlePath
}

/** 当前连接已经执行到的 schema 版本，供备份 manifest 校验使用。 */
export function databaseSchemaVersion(): number {
  const row = db().prepare('PRAGMA user_version').get() as Record<string, unknown>
  return Number(row['user_version'] ?? 0)
}

/** 在复制快照或替换文件前把 WAL 合并并截断。 */
export function checkpointDatabase(): void {
  db().exec('PRAGMA wal_checkpoint(TRUNCATE)')
}

/**
 * 整理库文件。**同步且可能很慢**(要重写整个文件),
 * 所以只由用户在设置-数据页显式点、不自动跑 —— 方案 §9。
 */
export function vacuumDatabase(): void {
  const d = db()
  // WAL 先落盘再 VACUUM,否则刚删掉的数据还占着 -wal,回收不到位
  d.exec('PRAGMA wal_checkpoint(TRUNCATE)')
  d.exec('VACUUM')
}

/**
 * 在 `openDatabase()` **之前**偷看一眼用户选的主题。读不出来时返回 `'system'`。
 *
 * ## 为什么需要它
 *
 * 窗口的 `backgroundColor` 要在**建窗那一刻**定下来,而建窗排在启动迁移闸门
 * 之前 —— 迁移期间用户必须能看见那一屏。而 `store.getSettings()` 是要读库的,
 * 在 `openDatabase()` 之前读库会先把库以**内存兜底**方式打开,随后真正的
 * `openDatabase()` 直接抛错:表现是「迁移跑完了,但应用起不来」。
 *
 * ★ 所以这里**自己开一个只读连接**,不碰本文件的 `db()`。这是本模块唯一的例外,
 *   而它是被位置逼出来的:调用方在建窗路径上,那里没有第二个地方能读到设置。
 *
 * ## 为什么值得为它单开一次连接
 *
 * 拿不到这个值时窗口底色只能写死,浅色用户会看到「深色空块 → 界面出来 →
 * 啪一下变浅」。一次只读连接加一条单行查询,换掉这个闪烁。
 *
 * ★ 读的是**文件里的**设置,不是内存里的 —— 所以它反映不了本次启动期间还没落盘的
 *   修改。建窗路径上不存在这种修改,所以没关系。
 */
export function peekThemePreference(databasePath: string): 'light' | 'dark' | 'system' {
  let probe: DatabaseSync | null = null
  try {
    probe = new DatabaseSync(databasePath, { readOnly: true })
    const row = probe.prepare('SELECT json FROM settings WHERE id = 1').get() as
      | { json?: unknown }
      | undefined
    const parsed: unknown = JSON.parse(String(row?.json ?? '{}'))
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return 'system'
    const theme = (parsed as { theme?: unknown }).theme
    return theme === 'light' || theme === 'dark' ? theme : 'system'
  } catch {
    /*
      ★ 任何失败都回退到 `'system'`,**不抛**。这条路在建窗之前跑,而那里没有
      任何能显示错误的地方 —— 抛出去就是「窗口都建不出来」。库不存在(全新安装)、
      损坏、被占用,全都不该阻止建窗:底色猜错只是闪一下,建不出窗是启动失败。
    */
    return 'system'
  } finally {
    try {
      probe?.close()
    } catch {
      /* 关不掉也无所谓,进程退出会收 */
    }
  }
}
