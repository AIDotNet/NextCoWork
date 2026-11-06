/**
 * 备份的**重活**在这里 —— 但不在主进程里。
 *
 * ## 它解决的是哪一类卡顿
 *
 * `ipc/storage.ts` 的 `createBackup` 原先在主进程里三件事一起做:把整库
 * `readFileSync` 成一个 Buffer、对整份库算一遍 SHA-256、再用 `Buffer.concat`
 * 拼出整个归档。三件都是**按库的大小线性**的同步 CPU / 内存操作,而 SQLite 库
 * 在有历史的机器上轻易上 GB —— 结果是点一次「立即备份」界面就冻住若干秒,
 * 期间连窗口拖动都没有响应。
 *
 * 现在数据库复制、整库散列、归档拼装全部在一个 `worker_threads` 里做。数据库
 * 正文从不进入主进程的 JS 堆,归档 Buffer 也从不 post 回来 —— 回传的只有
 * `databaseSha256`、字节数和几个计数。
 *
 * ## 两步,不是一步
 *
 * 清单里的计数 / 设置 / 主密钥**必须和归档里的那份数据库同源**,否则
 * `ipc/storage.ts` 的 `validateBackupDatabase` 会在恢复时对不上账(它拿裸
 * `COUNT(*)` 和 `settings.json` 逐项比对)。而在 `await` 复制快照期间,账户切换
 * 或新消息照样在写库 —— 所以这两样只能在**快照已经落盘之后**从快照里读。
 *
 * 于是:
 *   1. `runBackupSnapshot` —— 用 SQLite 自己的异步备份 API 把当前库复制成一个
 *      自包含文件,返回它的散列、`user_version`、settings 行、以及几个计数。
 *      主进程据此拼出 manifest.json 与 settings.json 的**小字节**。
 *   2. `runBackupArchive` —— 按给定的条目顺序把快照与那些小字节打成 ZIP。
 *
 * ## 为什么是 source-eval worker,而不是一个构建入口
 *
 * `grep-regex.ts` 的先例:自包含源码 + `eval: true`,不引入新的构建入口、不依赖
 * 打包器处理 worker chunk,发行/打包那套(本批次明确不动)一个字节都不用改。
 * worker 侧只 `require('node:*')`,没有仓库内模块 —— `eval: true` 下没有模块
 * 解析器,`import './x'` 一定失败。
 *
 * ## 格式一个字节都没改
 *
 * manifest.json / database.sqlite / settings.json / credential.key /
 * global-settings.json 的条目名与顺序、store(不压缩)方式、字段全部沿用
 * `ipc/storage.ts` 里 `readZip` / `parseBackup` 已经接受的写法。归档字节与旧的
 * `makeZip` 相同(同一条目顺序、同样的无数据描述符头)。
 */

import { randomUUID } from 'node:crypto'
import { unlink } from 'node:fs/promises'
import { Worker } from 'node:worker_threads'

/**
 * 单个 worker 的 V8 堆上限。
 *
 * ★ 数据库正文只按 1MB 块读、不驻留,归档也直接写盘;真正驻留的只有小条目的
 *   字节与中央目录。给足余量,但仍然是**一个硬上限**:一个失控的 worker 最多
 *   自己 OOM,不会吃掉主进程。
 */
const WORKER_MAX_OLD_GEN_MB = 256
const WORKER_MAX_YOUNG_GEN_MB = 32

/** 默认超时。一次备份要读完整个库,给小机器留足时间,但绝不无限等。 */
export const BACKUP_WORKER_TIMEOUT_MS = 10 * 60 * 1000

export type BackupWorkerFailure = 'timeout' | 'crashed' | 'failed'

export class BackupWorkerError extends Error {
  constructor(message: string, readonly failure: BackupWorkerFailure = 'failed') {
    super(message)
    this.name = 'BackupWorkerError'
  }
}

/** 第一步的结果 —— 全部来自**快照本身**,与归档里的数据库同源。 */
export interface BackupSnapshotInfo {
  databaseSha256: string
  schemaVersion: number
  /** 快照里的 settings 行原文(JSON)。恢复端会拿它和归档里的 settings.json 对账。 */
  settingsJson: string
  sessionCount: number
  messageCount: number
  encryptedCredentials: boolean
  /** 存在 NCK1 密文(程序自管加密)时,归档必须带上对应的主密钥。 */
  hasProgramCredentials: boolean
}

/** 一条归档条目。大文件只给路径,不进 JS 堆。 */
export interface BackupArchiveEntry {
  name: string
  /** 内联字节。与 `fromPath` 二选一。 */
  data?: Uint8Array
  /** 直接从磁盘读的文件路径 —— 数据库正文与主密钥走这条。 */
  fromPath?: string
}

export interface BackupArchiveResult {
  archiveBytes: number
}

/** 跑一次 worker。失败一律以 `BackupWorkerError` 拒绝,并保证 terminate 掉线程。 */
function runWorker<T>(payload: Record<string, unknown>, timeoutMs: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let settled = false
    const worker = new Worker(WORKER_SOURCE, {
      eval: true,
      // ★ 清掉 inspect 参数:父进程带着 --inspect 启动时,子 worker 会去抢
      //   同一个调试端口,表现是备份卡住而不是报错。
      execArgv: [],
      resourceLimits: {
        maxOldGenerationSizeMb: WORKER_MAX_OLD_GEN_MB,
        maxYoungGenerationSizeMb: WORKER_MAX_YOUNG_GEN_MB
      }
    })
    const finish = (fn: () => void): void => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      // 先等线程真正退出，再让调用方删除临时快照，避免后台仍在读写它。
      try { void worker.terminate().then(fn, fn) }
      catch { fn() }
    }
    const timer = setTimeout(() => {
      finish(() => reject(new BackupWorkerError('备份归档超时', 'timeout')))
    }, Math.max(1, timeoutMs))
    // 定时器不该拖住进程退出。
    timer.unref?.()
    worker.on('message', (reply: { ok: boolean; result?: unknown; error?: string }) => {
      if (reply.ok !== true) {
        finish(() => reject(new BackupWorkerError(reply.error ?? '备份归档失败', 'failed')))
        return
      }
      finish(() => resolve(reply.result as T))
    })
    worker.on('error', (error: Error) => {
      finish(() => reject(new BackupWorkerError(error.message, 'crashed')))
    })
    worker.on('messageerror', (error: Error) => {
      finish(() => reject(new BackupWorkerError(error.message, 'crashed')))
    })
    worker.on('exit', (code: number) => {
      finish(() => reject(new BackupWorkerError(`备份归档线程意外退出 (${String(code)})`, 'crashed')))
    })
    try {
      worker.postMessage(payload)
    } catch (error) {
      finish(() => reject(new BackupWorkerError(error instanceof Error ? error.message : String(error), 'crashed')))
    }
  })
}

/**
 * 把当前库复制成一份自包含快照,并把清单要用的数字从**快照里**读出来。
 *
 * ★ 用 `node:sqlite` 的 `backup()`(在线、异步、按页复制),不是 `readFileSync`。
 *   两个差别都很实在:整库进 JS 堆的那段同步分配没了;而且 `backup()` 拿到的
 *   一定是**完整一致**的一份库,不受 WAL 是否已 checkpoint 影响。
 *
 * `sourcePath` 只被读;快照写到 `snapshotPath`。之后主进程读它、归档它 —— 它是
 * 本次备份私有的临时文件,`await` 期间账户/新消息都写不进它。
 */
export function runBackupSnapshot(options: {
  sourcePath: string
  snapshotPath: string
  timeoutMs?: number
}): Promise<BackupSnapshotInfo> {
  return runWorker<BackupSnapshotInfo>(
    { kind: 'snapshot', sourcePath: options.sourcePath, snapshotPath: options.snapshotPath },
    options.timeoutMs ?? BACKUP_WORKER_TIMEOUT_MS
  )
}

/**
 * 把快照与给定条目打成无压缩 ZIP,写到 `archivePath.tmp` 再 rename 到
 * `archivePath`。
 *
 * ★ rename 在这里做,而不是让主进程拿到字节再自己写:归档从不经过主进程的堆,
 *   而「半个归档」在磁盘上出现的窗口被压到一个目录内的原子 rename。
 *
 * 给了 `expected` 时会再数一遍快照的行数;对不上就整次失败、不发布归档。
 */
export function runBackupArchive(options: {
  snapshotPath: string
  archivePath: string
  entries: readonly BackupArchiveEntry[]
  expected?: { sessionCount: number; messageCount: number }
  timeoutMs?: number
}): Promise<BackupArchiveResult> {
  const archiveTmp = `${options.archivePath}.tmp-${randomUUID()}`
  return runWorker<BackupArchiveResult>(
    {
      kind: 'archive',
      snapshotPath: options.snapshotPath,
      archivePath: options.archivePath,
      archiveTmp,
      entries: options.entries,
      ...(options.expected === undefined ? {} : { expected: options.expected })
    },
    options.timeoutMs ?? BACKUP_WORKER_TIMEOUT_MS
  ).catch(async (error: unknown) => {
    // runWorker 已等到线程退出；超时/崩溃时也不会留下貌似可恢复的半份归档。
    try { await unlink(archiveTmp) } catch { /* 未创建或已由 worker 清理 */ }
    throw error
  })
}

/**
 * worker 自己那份源码。★ 自包含 —— 见文件头。
 *
 * 结果里**从不包含归档字节**:归档直接写进 `archivePath`。
 */
const WORKER_SOURCE = `
const { parentPort } = require('node:worker_threads')
const { createHash } = require('node:crypto')
const { closeSync, fstatSync, fsyncSync, openSync, readSync, renameSync, statSync, unlinkSync, writeSync } = require('node:fs')

const CHUNK = 1024 * 1024

function sha256File(path) {
  const hash = createHash('sha256')
  const fd = openSync(path, 'r')
  try {
    const buf = Buffer.allocUnsafe(CHUNK)
    let offset = 0
    for (;;) {
      const read = readSync(fd, buf, 0, CHUNK, offset)
      if (read <= 0) break
      hash.update(buf.subarray(0, read))
      offset += read
    }
  } finally {
    closeSync(fd)
  }
  return hash.digest('hex')
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = (c & 1) !== 0 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    table[n] = c >>> 0
  }
  return table
})()

/** 分块 CRC-32:与旧的主进程实现逐位等价。 */
function crc32Data(data, seed) {
  let c = seed === undefined ? 0xffffffff : seed
  for (let i = 0; i < data.length; i++) c = CRC_TABLE[(c ^ data[i]) & 0xff] ^ (c >>> 8)
  return c >>> 0
}

function crc32File(path, size) {
  let state = 0xffffffff
  const fd = openSync(path, 'r')
  try {
    const buf = Buffer.allocUnsafe(CHUNK)
    let offset = 0
    while (offset < size) {
      const want = Math.min(CHUNK, size - offset)
      const read = readSync(fd, buf, 0, want, offset)
      if (read <= 0) throw new Error('backup source shrank while hashing')
      state = crc32Data(buf.subarray(0, read), state)
      offset += read
    }
  } finally {
    closeSync(fd)
  }
  return (state ^ 0xffffffff) >>> 0
}

function openSource(path) {
  const { DatabaseSync } = require('node:sqlite')
  const source = new DatabaseSync(path, { readOnly: true })
  try {
    source.exec('PRAGMA busy_timeout = 2000')
    return source
  } catch (err) {
    source.close()
    throw err
  }
}

function isProgramEncrypted(blob) {
  return !!blob && blob.length >= 4 && blob[0] === 0x4e && blob[1] === 0x43 && blob[2] === 0x4b && blob[3] === 0x31
}

function snapshot(request) {
  const { backup } = require('node:sqlite')
  const source = openSource(request.sourcePath)
  return backup(source, request.snapshotPath)
    .then(() => { source.close() })
    .catch((err) => { try { source.close() } catch (e) {} throw err })
    .then(() => {
      const { DatabaseSync } = require('node:sqlite')
      const snapshot = new DatabaseSync(request.snapshotPath, { readOnly: true })
      try {
        const sessions = Number(snapshot.prepare('SELECT COUNT(*) AS n FROM sessions').get().n)
        const messages = Number(snapshot.prepare('SELECT COUNT(*) AS n FROM messages').get().n)
        const rows = snapshot.prepare('SELECT blob FROM credentials').all()
        const version = Number(snapshot.prepare('PRAGMA user_version').get().user_version)
        const settingsRow = snapshot.prepare('SELECT json FROM settings WHERE id = 1').get()
        return {
          databaseSha256: sha256File(request.snapshotPath),
          schemaVersion: version,
          settingsJson: settingsRow === undefined ? '' : String(settingsRow.json),
          sessionCount: sessions,
          messageCount: messages,
          encryptedCredentials: rows.length > 0,
          hasProgramCredentials: rows.some((row) => isProgramEncrypted(row.blob))
        }
      } finally {
        snapshot.close()
      }
    })
}

let fd = -1
let offset = 0
const central = []

function writeAll(data) {
  let written = 0
  while (written < data.length) {
    const count = writeSync(fd, data, written, data.length - written, null)
    if (count <= 0) throw new Error('backup archive write made no progress')
    written += count
  }
}

function localHeader(name, size, crc) {
  const header = Buffer.alloc(30 + name.length)
  header.writeUInt32LE(0x04034b50, 0)
  header.writeUInt16LE(20, 4)
  header.writeUInt16LE(0, 6)
  header.writeUInt16LE(0, 8) // store(不压缩)——与读取端支持的范围一致
  header.writeUInt16LE(0, 10)
  header.writeUInt16LE(0, 12)
  header.writeUInt32LE(crc, 14)
  header.writeUInt32LE(size, 18)
  header.writeUInt32LE(size, 22)
  header.writeUInt16LE(name.length, 26)
  header.writeUInt16LE(0, 28)
  name.copy(header, 30)
  return header
}

function centralHeader(name, size, crc, localOffset) {
  const entry = Buffer.alloc(46 + name.length)
  entry.writeUInt32LE(0x02014b50, 0)
  entry.writeUInt16LE(20, 4)
  entry.writeUInt16LE(20, 6)
  entry.writeUInt16LE(0, 8)
  entry.writeUInt16LE(0, 10)
  entry.writeUInt16LE(0, 12)
  entry.writeUInt16LE(0, 14)
  entry.writeUInt32LE(crc, 16)
  entry.writeUInt32LE(size, 20)
  entry.writeUInt32LE(size, 24)
  entry.writeUInt16LE(name.length, 28)
  entry.writeUInt16LE(0, 30)
  entry.writeUInt16LE(0, 32)
  entry.writeUInt16LE(0, 34)
  entry.writeUInt16LE(0, 36)
  entry.writeUInt32LE(0, 38)
  entry.writeUInt32LE(localOffset, 42)
  name.copy(entry, 46)
  return entry
}

function addEntry(entry) {
  const name = Buffer.from(entry.name, 'utf8')
  const fromPath = entry.fromPath
  const size = fromPath === undefined ? entry.data.length : statSync(fromPath).size
  const crc = fromPath === undefined ? (crc32Data(entry.data) ^ 0xffffffff) >>> 0 : crc32File(fromPath, size)
  const localOffset = offset
  writeAll(localHeader(name, size, crc))
  offset += 30 + name.length
  if (fromPath === undefined) {
    writeAll(entry.data)
    offset += size
  } else {
    const source = openSync(fromPath, 'r')
    try {
      const buf = Buffer.allocUnsafe(CHUNK)
      let read = 0
      while (read < size) {
        const want = Math.min(CHUNK, size - read)
        const got = readSync(source, buf, 0, want, read)
        if (got <= 0) throw new Error('backup source shrank while archiving')
        writeAll(buf.subarray(0, got))
        read += got
      }
    } finally {
      closeSync(source)
    }
    offset += size
  }
  central.push(centralHeader(name, size, crc, localOffset))
}

function finishArchive() {
  const centralBytes = Buffer.concat(central)
  writeAll(centralBytes)
  const end = Buffer.alloc(22)
  end.writeUInt32LE(0x06054b50, 0)
  end.writeUInt16LE(0, 4)
  end.writeUInt16LE(0, 6)
  end.writeUInt16LE(central.length, 8)
  end.writeUInt16LE(central.length, 10)
  end.writeUInt32LE(centralBytes.length, 12)
  end.writeUInt32LE(offset, 16)
  end.writeUInt16LE(0, 20)
  writeAll(end)
}

function archive(request) {
  if (request.expected) {
    const { DatabaseSync } = require('node:sqlite')
    const snapshot = new DatabaseSync(request.snapshotPath, { readOnly: true })
    try {
      const sessions = Number(snapshot.prepare('SELECT COUNT(*) AS n FROM sessions').get().n)
      const messages = Number(snapshot.prepare('SELECT COUNT(*) AS n FROM messages').get().n)
      if (sessions !== request.expected.sessionCount) throw new Error('snapshot sessionCount changed: ' + sessions)
      if (messages !== request.expected.messageCount) throw new Error('snapshot messageCount changed: ' + messages)
    } finally {
      snapshot.close()
    }
  }
  const archiveTmp = request.archiveTmp
  fd = openSync(archiveTmp, 'wx', 0o600)
  offset = 0
  central.length = 0
  try {
    for (const entry of request.entries) addEntry(entry)
    finishArchive()
    fsyncSync(fd)
    const archiveBytes = fstatSync(fd).size
    closeSync(fd)
    fd = -1
    renameSync(archiveTmp, request.archivePath)
    return { archiveBytes }
  } catch (err) {
    if (fd !== -1) { try { closeSync(fd) } catch (e) {} }
    fd = -1
    try { unlinkSync(archiveTmp) } catch (e) {}
    throw err
  }
}

parentPort.on('message', (m) => {
  try {
    const task = m.kind === 'snapshot' ? snapshot(m) : Promise.resolve(archive(m))
    task
      .then((result) => parentPort.postMessage({ ok: true, result }))
      .catch((err) => parentPort.postMessage({ ok: false, error: err && err.message ? err.message : String(err) }))
  } catch (err) {
    parentPort.postMessage({ ok: false, error: err && err.message ? err.message : String(err) })
  }
})
`
