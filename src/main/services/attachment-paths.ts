/**
 * 附件根 —— **零 Electron 的纯 Node 模块**。
 *
 * 从 `net/attachment-protocol.ts` 原样提取:那个模块要 `import electron`(注册
 * `ncw://` 协议、`nativeImage` 解码缩略图),而「附件落在哪个目录」这件事本身
 * 只依赖数据根,与协议无关。附件上传、生成视频落盘、会话图片仓都要这个根,把它
 * 留在协议模块里等于让每一个「只想写一个文件」的调用点都拖上整个 Electron 依赖。
 *
 * ★ 目录必须与 `ipc/storage.ts` 的 `attachmentDirectory()` 是**同一个**:清理扫的
 * 就是它。
 *
 * ★ `net/attachment-protocol.ts` 仍然 import 并重新 export 本模块的这两个符号,
 * 旧调用点(`ipc/*`、`host/index`、测试)一行都不用改。
 */
import { join } from 'node:path'
import { databaseDirectory } from '../db'

/** 附件根。★ 必须与 `ipc/storage.ts` 的 `attachmentDirectory()` 是同一个目录 —— 清理扫的就是它 */
export const ATTACHMENTS_DIR = 'attachments'

export function attachmentRoot(): string {
  return join(databaseDirectory(), ATTACHMENTS_DIR)
}
