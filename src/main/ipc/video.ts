/**
 * 视频任务的 IPC —— 渲染层那条卡片与后台 manager 之间的缝。
 *
 * ★★ **与 `agent:event` 分开,这是刻意的。** 那条流是 run 生命周期内的
 * (有 seq、有 attach 补齐语义),而一个视频任务的寿命**比 run 长得多**:
 * 提交它的那次 run 早就结束了,它还在云端跑。塞进 `agent:event` 会让
 * `agent:attach` 的 seq 补齐多出「这条事件不属于任何 run」的分支 ——
 * 那是 `goal` 那几条已经踩过一次的同一个坑(见 contract 里那段)。
 *
 * ★ 所有查询都按**会话**过滤:渲染层只该看到当前会话的任务。
 *   `video:get` 也校验一下它属于会话 —— 一个 id 猜对了不该就能读到别的会话的任务。
 */
import { dialog } from 'electron'
import { app } from 'electron'
import { join } from 'node:path'
import { copyFile, rm } from 'node:fs/promises'
import { windows } from '../window/registry'
import { getVideoManager } from '../video-generation/runtime'
import type { VideoJobView } from '../../shared/domain/video-generation'
import { videoJobView } from '../../shared/domain/video-generation'
import { attachmentRoot, resolveAttachmentPath } from '../net/attachment-protocol'
import { NCW_SCHEME } from '../../shared/domain/attachment'

function requireManager(): NonNullable<ReturnType<typeof getVideoManager>> {
  const instance = getVideoManager()
  if (instance === undefined) throw new Error('video generation is not available yet')
  return instance
}

export function listVideoJobs(req: { sessionId: string }): VideoJobView[] {
  const instance = getVideoManager()
  if (instance === undefined) return []
  return instance.listForSession(req.sessionId).map(videoJobView)
}

export function getVideoJob(req: { id: string }): VideoJobView | undefined {
  const instance = getVideoManager()
  if (instance === undefined) return undefined
  return instance.status(req.id)
}

export async function cancelVideoJob(req: { id: string }): Promise<{ ok: true; state: 'requested' | 'already-done' } | { ok: false; reason: string }> {
  return requireManager().cancel(req.id)
}

export async function retryVideoRetrieval(req: { id: string }): Promise<{ ok: boolean; reason?: string }> {
  return requireManager().retryRetrieval(req.id)
}

/**
 * 把成品存到用户选的位置。
 *
 * ★ 走**系统保存对话框**,和 `saveImageFile` 同一条规矩 —— 渲染层永不指定
 * 任意写入路径(方案 §9)。默认名用附件原名(它是 `generated.mp4` 这类占位),
 * 用户可以改。
 *
 * ★ 这个动作**不需要**把整个视频读进渲染层再送回来:源文件就在附件目录里,
 * 主进程自己 copy。绕一圈的话一个 400MB 的视频要过一次结构化克隆 —— 那正是
 * 这一整套"视频走 ncw 协议"要避免的事。
 */
export async function saveVideoJobFile(req: { id: string; defaultName?: string }): Promise<{ path: string } | null> {
  const job = requireManager().status(req.id)
  if (job === undefined) throw new Error('找不到该视频任务')
  const video = job.videos[0]
  if (video === undefined) throw new Error('这段视频还没有下载完成')

  const source = resolveAttachmentPath(attachmentRoot(), video.url)
  if (source === null || !video.url.startsWith(`${NCW_SCHEME}://`)) {
    throw new Error('这段视频的地址不属于本机附件,无法直接保存')
  }
  const ext = video.mime === 'video/webm' ? '.webm' : video.mime === 'video/quicktime' ? '.mov' : '.mp4'
  const raw = (req.defaultName ?? '').trim()
  const clean = raw.replace(/[/\\:*?"<>|]/g, '_').replace(/\.[a-z0-9]+$/iu, '').slice(0, 120) || 'generated-video'
  const result = await dialog.showSaveDialog({
    title: '保存视频',
    defaultPath: join(app.getPath('downloads'), `${clean}${ext}`),
    filters: [{ name: ext.slice(1).toUpperCase(), extensions: [ext.slice(1)] }]
  })
  if (result.canceled || !result.filePath) return null
  try {
    await copyFile(source, result.filePath)
  } catch (error) {
    // ★ 失败了不要留一个半截文件在用户选的位置 —— 那比"什么都没存"更难解释
    await rm(result.filePath, { force: true }).catch(() => {})
    throw error
  }
  return { path: result.filePath }
}

/** 任务状态变化 → 广播给所有窗口。由 manager 的 onChange 调。 */
export function announceVideoJob(view: VideoJobView): void {
  windows.emitToAll('video:jobChanged', view)
}
