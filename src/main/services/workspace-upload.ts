/**
 * 工作区文件的分片上传:begin → chunk × N → commit(或 abort)。
 *
 * - 数据先写进**同目录的隐藏临时文件**(`.ncw-upload-<id>.part`,权限 0600),commit 才原子地变成目标 ——
 *   中途断线/崩溃/磁盘满,目标文件要么是旧的、要么不存在,绝不会是半截。
 * - **顺序写,可续传**:`chunk` 的 `offset` 必须等于已接收字节数;对不上就 `conflict` 并告知已收多少,
 *   客户端从那里继续(重发已写过的块也只是被告知「你该从这里开始」,不会重复写)。
 * - commit 校验总大小与可选的 sha256,再落位。**默认不覆盖**:目标已存在 → `exists`(用 `link` 原子判定,
 *   不是先查后写);显式 `overwrite` 才用 rename 替换。
 * - 上传属于发起它的控制端(`owner`),别人不能续写、提交或终止;每个控制端并发 ≤ 4、总并发 ≤ 16、
 *   空闲 30 分钟自动清理临时文件;停服时清掉全部未完成的临时文件。
 * - 路径围栏与桌面写文件是同一套(`checkedPath`)。
 */
import { createHash } from 'node:crypto'
import { closeSync, createReadStream, fsyncSync, linkSync, openSync, renameSync, statSync, unlinkSync, writeSync } from 'node:fs'
import { dirname, join, basename } from 'node:path'
import { DeviceProtocolError } from '../../shared/device/errors'
import { checkedPath, fail, translateError, workspaceRoot } from './workspace-files'

export const UPLOAD_MAX_BYTES = 256 * 1024 * 1024
export const UPLOAD_CHUNK_MAX = 512 * 1024
const PER_OWNER = 4
const TOTAL = 16
const IDLE_MS = 30 * 60_000

interface Upload {
  id: string
  owner: string
  target: string
  part: string
  fd: number
  size: number
  received: number
  overwrite: boolean
  lastAt: number
}

export interface UploadManagerOptions {
  newId(): string
  now?(): number
}

export class UploadManager {
  private readonly uploads = new Map<string, Upload>()
  private readonly now: () => number

  constructor(private readonly options: UploadManagerOptions) {
    this.now = options.now ?? Date.now
  }

  begin(owner: string, req: { workspaceId: string; path: string; size: number; overwrite: boolean }): { id: string; chunkMax: number } {
    this.expire()
    if (!Number.isSafeInteger(req.size) || req.size < 0 || req.size > UPLOAD_MAX_BYTES) throw new DeviceProtocolError('resource_limit', 'Upload is too large')
    const mine = [...this.uploads.values()].filter((u) => u.owner === owner).length
    if (mine >= PER_OWNER || this.uploads.size >= TOTAL) throw new DeviceProtocolError('resource_limit', 'Too many uploads in progress')
    try {
      const target = checkedPath(workspaceRoot(req.workspaceId), req.path)
      if (!req.overwrite && existsFile(target)) fail('exists')
      const id = this.options.newId()
      const part = join(dirname(target), `.ncw-upload-${id}.part`)
      // wx:临时文件必须是新建的,不能撞上别人的文件。
      const fd = openSync(part, 'wx', 0o600)
      this.uploads.set(id, { id, owner, target, part, fd, size: req.size, received: 0, overwrite: req.overwrite, lastAt: this.now() })
      return { id, chunkMax: UPLOAD_CHUNK_MAX }
    } catch (error) {
      return translateError(error)
    }
  }

  chunk(owner: string, id: string, offset: number, bytes: Buffer): { received: number } {
    const upload = this.require(owner, id)
    if (bytes.length === 0 || bytes.length > UPLOAD_CHUNK_MAX) throw new DeviceProtocolError('resource_limit', 'Invalid chunk size')
    if (offset !== upload.received) throw new DeviceProtocolError('conflict', `Resume from byte ${upload.received}`)
    if (upload.received + bytes.length > upload.size) throw new DeviceProtocolError('resource_limit', 'Chunk exceeds the declared size')
    try {
      let written = 0
      while (written < bytes.length) written += writeSync(upload.fd, bytes, written, bytes.length - written, upload.received + written)
    } catch (error) {
      // 磁盘满/IO 错误:这次上传作废并清理,告诉调用方稳定的错误码。
      this.drop(upload)
      return translateError(error)
    }
    upload.received += bytes.length
    upload.lastAt = this.now()
    return { received: upload.received }
  }

  async commit(owner: string, id: string, sha256?: string): Promise<{ size: number }> {
    const upload = this.require(owner, id)
    if (upload.received !== upload.size) throw new DeviceProtocolError('conflict', `Upload is incomplete (${upload.received} of ${upload.size} bytes)`)
    try {
      fsyncSync(upload.fd)
      closeSync(upload.fd)
      upload.fd = -1
      if (sha256 !== undefined && (await digest(upload.part)) !== sha256.toLowerCase()) {
        this.drop(upload)
        throw new DeviceProtocolError('conflict', 'Content digest does not match')
      }
      if (upload.overwrite) renameSync(upload.part, upload.target)
      else { linkSync(upload.part, upload.target); unlinkSync(upload.part) } // link 在目标已存在时原子失败
      this.uploads.delete(id)
      return { size: upload.size }
    } catch (error) {
      if (error instanceof DeviceProtocolError) throw error
      this.drop(upload)
      return translateError(error)
    }
  }

  abort(owner: string, id: string): boolean {
    const upload = this.uploads.get(id)
    if (upload === undefined) return false
    if (upload.owner !== owner) throw new DeviceProtocolError('forbidden', 'This upload belongs to another controller')
    this.drop(upload)
    return true
  }

  /** 控制端被撤销时清理它的全部上传。 */
  dropOwner(owner: string): number {
    let n = 0
    for (const upload of [...this.uploads.values()]) if (upload.owner === owner) { this.drop(upload); n++ }
    return n
  }

  shutdown(): void {
    for (const upload of [...this.uploads.values()]) this.drop(upload)
  }

  size(): number { return this.uploads.size }

  private require(owner: string, id: string): Upload {
    this.expire()
    const upload = this.uploads.get(id)
    if (upload === undefined) throw new DeviceProtocolError('not_found', 'Upload does not exist or has expired')
    if (upload.owner !== owner) throw new DeviceProtocolError('forbidden', 'This upload belongs to another controller')
    return upload
  }

  private expire(): void {
    const t = this.now()
    for (const upload of [...this.uploads.values()]) if (t - upload.lastAt >= IDLE_MS) this.drop(upload)
  }

  private drop(upload: Upload): void {
    this.uploads.delete(upload.id)
    if (upload.fd >= 0) { try { closeSync(upload.fd) } catch { /* 已关闭。 */ } }
    try { unlinkSync(upload.part) } catch { /* 已不在。 */ }
  }
}

function existsFile(path: string): boolean {
  try { statSync(path); return true } catch { return false }
}

function digest(path: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256')
    createReadStream(path).on('data', (chunk) => hash.update(chunk)).on('end', () => resolve(hash.digest('hex'))).on('error', reject)
  })
}

/** 临时文件名的形状,供测试与清理脚本识别。 */
export const isUploadPartName = (name: string): boolean => /^\.ncw-upload-.+\.part$/.test(basename(name))
