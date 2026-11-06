/**
 * 工作区文件的范围读取:一次读一段,供大文件下载、预览与视频 seek 使用。
 *
 * 路径围栏与桌面读文件是同一套(`checkedPath`:工作区内、符号链接按规则处理),错误同样走
 * `translateError` 的稳定错误码。版本(`revision`)由 **fstat 同一个文件描述符**得到(大小 + mtime),
 * 不是「先 stat 再 open」—— 后者在两步之间文件被替换时会把新旧内容拼在一起。
 */
import { closeSync, constants, fstatSync, openSync, readSync } from 'node:fs'
import { checkedPath, fail, translateError, workspaceRoot } from './workspace-files'

export interface WorkspaceRangeRequest {
  workspaceId: string
  /** 工作区相对路径。 */
  path: string
  offset: number
  length: number
  /** 上一块拿到的版本;与当前不符 → `conflict`。 */
  revision?: string
}

export interface WorkspaceRange {
  /** base64。 */
  data: string
  offset: number
  /** 实际返回的字节数(越过文件尾时小于请求值,可能为 0)。 */
  length: number
  size: number
  revision: string
  eof: boolean
}

export function readWorkspaceRange(req: WorkspaceRangeRequest): WorkspaceRange {
  let fd: number | null = null
  try {
    const target = checkedPath(workspaceRoot(req.workspaceId), req.path, false, 'follow-dirs')
    // O_NOFOLLOW:围栏检查之后路径被换成符号链接时不跟随(POSIX;Windows 无此标志则为 0)。
    fd = openSync(target, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0))
    const stat = fstatSync(fd)
    if (!stat.isFile()) fail('not-file')
    const revision = `${stat.size}-${Math.trunc(stat.mtimeMs)}`
    if (req.revision !== undefined && req.revision !== revision) fail('conflict')
    const available = Math.max(0, stat.size - req.offset)
    const wanted = Math.min(req.length, available)
    const buffer = Buffer.alloc(wanted)
    let read = 0
    while (read < wanted) {
      const n = readSync(fd, buffer, read, wanted - read, req.offset + read)
      if (n === 0) break
      read += n
    }
    return {
      data: buffer.subarray(0, read).toString('base64'), offset: req.offset, length: read,
      size: stat.size, revision, eof: req.offset + read >= stat.size
    }
  } catch (error) {
    return translateError(error)
  } finally {
    if (fd !== null) { try { closeSync(fd) } catch { /* 已关闭。 */ } }
  }
}
