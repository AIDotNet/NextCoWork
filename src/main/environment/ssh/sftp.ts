import { posix, win32 } from 'node:path'
import { randomUUID } from 'node:crypto'
import type { Readable, Writable } from 'node:stream'
import { SFTPStream, type Stats } from 'ssh2-streams'
import type { EnvironmentFs, EnvironmentStat } from '../contract'
import { writeStreamToFile, type StreamSink, type StreamTarget } from '../../kernel/stream-write'
import { EnvironmentError, errorCode, missingPath, sftpError } from '../errors'
import { fromSftpPath, toSftpPath } from '../paths'

const MAX_FILE_BYTES = 32 * 1024 * 1024
const MAX_DIRECTORY_ENTRIES = 20_000

const attributes = (stat: Stats): EnvironmentStat => ({
  size: stat.size,
  mtimeMs: stat.mtime * 1000,
  mode: stat.mode,
  isDir: stat.isDirectory(),
  isFile: stat.isFile(),
  isSymbolicLink: stat.isSymbolicLink()
})

export class SftpFileSystem implements EnvironmentFs {
  readonly stream = new SFTPStream()
  readonly ready: Promise<void>
  private closed = false
  private readonly pending = new Set<(error: Error) => void>()

  constructor(input: Writable, output: Readable, private os: string, private readonly check: () => void = () => {}, private readonly onDisconnect: () => void = () => {}) {
    this.ready = new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.fail(new EnvironmentError('sftp-unavailable')); reject(new EnvironmentError('sftp-unavailable')) }, 15_000)
      this.stream.once('ready', () => { clearTimeout(timer); resolve() })
      this.stream.once('error', (error) => { clearTimeout(timer); reject(error) })
      this.stream.once('close', () => { clearTimeout(timer); reject(new EnvironmentError('disconnected')) })
    })
    this.stream.on('error', (error: Error) => this.fail(error))
    this.stream.on('close', () => this.fail(new EnvironmentError('disconnected')))
    output.on('error', (error) => this.fail(error))
    output.once('end', () => this.fail(new EnvironmentError('disconnected')))
    input.on('error', (error) => this.fail(error))
    this.stream.pipe(input)
    output.pipe(this.stream)
  }

  setPlatform(os: string): void { this.os = os }

  close(): void {
    this.fail(new EnvironmentError('disconnected'))
    this.stream.destroy()
  }

  private fail(error: Error): void {
    if (this.closed) return
    this.closed = true
    const reason = error instanceof EnvironmentError ? error : new EnvironmentError('disconnected')
    for (const reject of this.pending) reject(reason)
    this.pending.clear()
    this.stream.destroy()
    this.onDisconnect()
  }

  private request<Value>(start: (done: (error: unknown, value?: Value) => void) => unknown, mutation = false): Promise<Value> {
    try { this.check() } catch (error) { return Promise.reject(error) }
    if (this.closed) return Promise.reject(new EnvironmentError('disconnected'))
    return new Promise((resolve, reject) => {
      const fail = (error: Error): void => {
        clearTimeout(timer); this.pending.delete(fail)
        reject(mutation && error instanceof EnvironmentError && ['disconnected', 'timeout'].includes(error.code)
          ? new EnvironmentError('result-unknown') : error)
      }
      const timer = setTimeout(() => this.fail(new EnvironmentError('timeout')), 30_000)
      this.pending.add(fail)
      try {
        start((error, result) => {
          clearTimeout(timer)
          this.pending.delete(fail)
          if (errorCode(error) === 6 || errorCode(error) === 7) fail(new EnvironmentError('disconnected'))
          else if (error) reject(sftpError(error))
          else resolve(result as Value)
        })
      } catch (error) { fail(error instanceof Error ? error : new Error(String(error))) }
    })
  }

  private path(path: string): string {
    if (path.includes('\0')) throw new EnvironmentError('invalid-path')
    return toSftpPath(path, this.os)
  }

  async realpath(path: string): Promise<string> {
    return fromSftpPath(await this.request<string>((done) => this.stream.realpath(this.path(path), done)), this.os)
  }
  async stat(path: string): Promise<EnvironmentStat> {
    return attributes(await this.request<Stats>((done) => this.stream.stat(this.path(path), done)))
  }
  async lstat(path: string): Promise<EnvironmentStat> {
    return attributes(await this.request<Stats>((done) => this.stream.lstat(this.path(path), done)))
  }
  async exists(path: string): Promise<boolean> {
    try { await this.stat(path); return true } catch (error) { if (missingPath(error)) return false; throw error }
  }
  async readFile(path: string): Promise<string> { return (await this.readBytes(path)).toString('utf8') }
  async writeFile(path: string, content: string): Promise<void> {
    let existing: EnvironmentStat | undefined
    try { existing = await this.lstat(path) } catch (error) { if (!missingPath(error)) throw error }
    if (existing && !existing.isFile) throw new EnvironmentError('unsupported')
    const paths = this.os === 'win32' ? win32 : posix
    const temporary = paths.join(paths.dirname(path), `.ncw-write-${randomUUID()}.tmp`)
    let written = false
    try {
      await this.writeBytes(temporary, Buffer.from(content), { exclusive: true, mode: existing ? existing.mode & 0o777 : 0o600 })
      written = true
      await this.rename(temporary, path, existing !== undefined)
      written = false
    } finally { if (written) await this.unlink(temporary).catch(() => {}) }
  }
  async readBytes(path: string, maxBytes = MAX_FILE_BYTES): Promise<Buffer> {
    const stat = await this.stat(path)
    if (!stat.isFile) throw new EnvironmentError('unsupported')
    if (stat.size > maxBytes) throw new EnvironmentError('unsupported', 'File exceeds the transfer limit')
    return Buffer.from(await this.readFileBytes(path, Math.max(1, stat.size)))
  }
  async readFileBytes(path: string, maxBytes = 4096): Promise<Uint8Array> {
    if (!Number.isSafeInteger(maxBytes) || maxBytes < 0 || maxBytes > MAX_FILE_BYTES) throw new EnvironmentError('unsupported')
    const handle = await this.request<Buffer>((done) => this.stream.open(this.path(path), 'r', done))
    try {
      const bytes = Buffer.alloc(maxBytes)
      let offset = 0
      while (offset < bytes.length) {
        const read = await this.request<number>((done) => this.stream.readData(handle, bytes, offset,
          Math.min(32 * 1024, bytes.length - offset), offset, (error, count) => done(error, count)))
        if (read === 0) break
        offset += read
      }
      return bytes.subarray(0, offset)
    } finally { await this.request<void>((done) => this.stream.close(handle, done)).catch(() => {}) }
  }
  async writeBytes(path: string, value: Uint8Array, options: { exclusive?: boolean; mode?: number } = {}): Promise<void> {
    if (value.byteLength > MAX_FILE_BYTES) throw new EnvironmentError('unsupported', 'File exceeds the transfer limit')
    const bytes = Buffer.from(value)
    const timestamp = Math.floor(Date.now() / 1000)
    const handle = await this.request<Buffer>((done) => this.stream.open(this.path(path), options.exclusive ? 'wx' : 'w',
      { mode: options.mode ?? 0o600, atime: timestamp, mtime: timestamp }, done), true)
    let failed = false
    try {
      for (let offset = 0; offset < bytes.length; offset += 32 * 1024) {
        await this.request<void>((done) => this.stream.writeData(handle, bytes, offset,
          Math.min(32 * 1024, bytes.length - offset), offset, done), true)
      }
      await this.sync(handle)
    } catch (error) { failed = true; throw error } finally {
      const closing = this.request<void>((done) => this.stream.close(handle, done), true)
      if (failed) await closing.catch(() => {})
      else await closing
    }
  }

  private async sync(handle: Buffer): Promise<void> {
    try { await this.request<void>((done) => this.stream.ext_openssh_fsync(handle, done), true) } catch (error) {
      if (errorCode(error) === 'ENOTSUP' || (error instanceof Error && error.message === 'Server does not support this extended request')) return
      throw error
    }
  }
  async readDir(path: string): Promise<Array<{ name: string; isDir: boolean }>> {
    const handle = await this.request<Buffer>((done) => this.stream.opendir(this.path(path), done))
    const result: Array<{ name: string; isDir: boolean }> = []
    try {
      for (;;) {
        const entries = await this.request<import('ssh2-streams').FileEntry[]>((done) => this.stream.readdir(handle,
          (error, entries) => errorCode(error) === 1 ? done(null, []) : done(error, entries)))
        if (!Array.isArray(entries) || entries.length === 0) break
        for (const entry of entries) {
          if (entry.filename === '.' || entry.filename === '..') continue
          if (entry.filename.includes('/') || entry.filename.includes('\0') || (this.os === 'win32' && entry.filename.includes('\\'))) {
            throw new EnvironmentError('invalid-path')
          }
          let isDir = (entry.attrs.mode & 0o170000) === 0o040000
          if ((entry.attrs.mode & 0o170000) === 0o120000) {
            try { isDir = (await this.stat((this.os === 'win32' ? win32 : posix).join(path, entry.filename))).isDir } catch (error) {
              if (!missingPath(error)) throw error
            }
          }
          result.push({ name: entry.filename, isDir })
          if (result.length > MAX_DIRECTORY_ENTRIES) throw new EnvironmentError('unsupported', 'Directory exceeds the listing limit')
        }
      }
      return result
    } finally { await this.request<void>((done) => this.stream.close(handle, done)).catch(() => {}) }
  }
  async copyFile(source: string, destination: string, maxBytes: number, mode = 0o600): Promise<number> {
    const input = await this.request<Buffer>((done) => this.stream.open(this.path(source), 'r', done))
    let output: Buffer | undefined
    let failed = false
    try {
      const stat = await this.request<Stats>((done) => this.stream.fstat(input, done))
      if (!stat.isFile() || stat.size > maxBytes) throw new EnvironmentError('unsupported')
      const timestamp = Math.floor(Date.now() / 1000)
      output = await this.request<Buffer>((done) => this.stream.open(this.path(destination), 'wx', { mode, atime: timestamp, mtime: timestamp }, done), true)
      const bytes = Buffer.alloc(32 * 1024)
      let offset = 0
      for (;;) {
        const count = await this.request<number>((done) => this.stream.readData(input, bytes, 0, bytes.length, offset,
          (error, length) => errorCode(error) === 1 ? done(null, 0) : done(error, length)))
        if (count === 0) { await this.sync(output); return offset }
        if (offset + count > maxBytes) throw new EnvironmentError('unsupported')
        await this.request<void>((done) => this.stream.writeData(output!, bytes, 0, count, offset, done), true)
        offset += count
      }
    } catch (error) { failed = true; throw error } finally {
      try {
        if (output) {
          const closing = this.request<void>((done) => this.stream.close(output!, done), true)
          if (failed) { await closing.catch(() => {}); await this.unlink(destination).catch(() => {}) }
          else await closing
        }
      } finally { await this.request<void>((done) => this.stream.close(input, done)).catch(() => {}) }
    }
  }
  async mkdir(path: string): Promise<void> {
    const timestamp = Math.floor(Date.now() / 1000)
    await this.request<void>((done) => this.stream.mkdir(this.path(path), { mode: 0o700, atime: timestamp, mtime: timestamp }, done), true)
  }

  /**
   * 需求:`SaveVideo` 把几十到几百兆的视频写进**远程**工作区。`writeBytes` 那条路
   * 有 32 MiB 上限,而且是整包 —— 视频上都不成立。所以单开一条:边读边分块发,
   * 上限、背压、失败清理都在 `stream-write.ts` 那份共享逻辑里(见它的文件头:
   * 两份实现必须逐字相同)。
   */
  async writeStream(
    path: string,
    source: ReadableStream<Uint8Array>,
    options: { exclusive?: boolean; mode?: number; maxBytes: number },
    signal: AbortSignal
  ): Promise<{ ok: true; size: number } | { ok: false; reason: string }> {
    return writeStreamToFile(this.streamTarget(), path, source, options, signal)
  }

  private streamTarget(): StreamTarget {
    return {
      openSink: async (path, exclusive, mode): Promise<StreamSink> => {
        const timestamp = Math.floor(Date.now() / 1000)
        const handle = await this.request<Buffer>((done) => this.stream.open(this.path(path), exclusive ? 'wx' : 'w',
          { mode, atime: timestamp, mtime: timestamp }, done), true)
        return {
          // ★ 分块走 32 KiB:与 `writeBytes` 同一个粒度 —— SFTP 的单包大小是有上限的,
          //   而那个值不是我们能随手放大的。
          write: async (chunk) => {
            for (let offset = 0; offset < chunk.length; offset += 32 * 1024) {
              const slice = chunk.subarray(offset, Math.min(chunk.length, offset + 32 * 1024))
              await this.request<void>((done) => this.stream.writeData(handle, Buffer.from(slice), 0, slice.length, offset, done), true)
            }
          },
          sync: async () => { await this.sync(handle) },
          close: async () => { await this.request<void>((done) => this.stream.close(handle, done), true) }
        }
      },
      rename: async (from, to, replace) => { await this.rename(from, to, replace) },
      unlink: async (path) => { await this.unlink(path) },
      exists: async (path) => this.exists(path),
      mkdirp: async (path) => { await this.mkdirp(path) }
    }
  }
  async mkdirp(filePath: string): Promise<void> {
    const paths = this.os === 'win32' ? win32 : posix
    const parent = paths.dirname(filePath)
    if (await this.exists(parent)) return
    if (paths.dirname(parent) === parent) throw new EnvironmentError('invalid-path')
    await this.mkdirp(parent)
    try { await this.mkdir(parent) } catch (error) { if (!(await this.stat(parent)).isDir) throw error }
  }
  async rename(source: string, destination: string, replace = false): Promise<void> {
    await this.request<void>((done) => replace
      ? this.stream.ext_openssh_rename(this.path(source), this.path(destination), done)
        : this.stream.rename(this.path(source), this.path(destination), done), true)
  }
      async unlink(path: string): Promise<void> { await this.request<void>((done) => this.stream.unlink(this.path(path), done), true) }
      async rmdir(path: string): Promise<void> { await this.request<void>((done) => this.stream.rmdir(this.path(path), done), true) }
}