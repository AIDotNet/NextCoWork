/**
 * 视频任务的渲染层通道。
 *
 * ★ 组件不直接碰频道字符串(协议 §9),所以卡片与设置页调的是这里。
 *
 * ★ **没有 `submit` 包装**:提交是**模型**的动作(由 `generate_video` 工具发起),
 * 不是用户在界面上点的。渲染层只做三件事 —— 看、取消、把成品存下来。
 */
import type { VideoJobView } from '../../../shared/domain/video-generation'
import { invoke } from './ipc'

export function listVideoJobs(sessionId: string): Promise<VideoJobView[]> {
  return invoke('video:listBySession', { sessionId })
}

export function getVideoJob(id: string): Promise<VideoJobView | undefined> {
  return invoke('video:get', { id })
}

/** ★ 返回值是**真实结论**:`requested` 不代表已取消(由轮询收尾)。 */
export function cancelVideoJob(id: string): Promise<{ ok: true; state: 'requested' | 'already-done' } | { ok: false; reason: string }> {
  return invoke('video:cancel', { id })
}

/** 取回失败之后的重试。**只查询/只下载,绝不重新生成。** */
export function retryVideoRetrieval(id: string): Promise<{ ok: boolean; reason?: string }> {
  return invoke('video:retryRetrieval', { id })
}

/** 系统保存对话框。用户取消时返回 null —— 那不是失败,不要报成功。 */
export function saveVideoFile(id: string, defaultName?: string): Promise<{ path: string } | null> {
  return invoke('video:saveFile', defaultName === undefined ? { id } : { id, defaultName })
}
