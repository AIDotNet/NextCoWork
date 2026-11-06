/**
 * 视频任务的状态缓存 —— 一张卡片之外还要跨会话显示,所以放 store。
 *
 * ★★ **订阅 `video:jobChanged` 是唯一的数据入口**(照 `stores/mcp.ts` 那条规矩):
 * 主进程的 manager 是任务状态的真源,渲染层只是它的投影。本地 `set` 一份的话
 * 两个窗口会算出不同的结果 —— 而视频任务恰好是"一个窗口提交、另一个窗口在看"
 * 的高发场景(会话窗口 + 主窗口)。
 *
 * ★ 合并按 `revision`:主进程每次状态变化 +1。用它而不是"整份替换",
 * 是因为一条乱序到达的旧快照会把卡片退回上一个状态 —— 而那看起来像"卡住了"。
 */
import { create } from 'zustand'
import type { VideoJobView } from '../../../shared/domain/video-generation'
import { on } from '../services/ipc'
import { listVideoJobs } from '../services/video'

interface VideoJobsState {
  /** sessionId → 该会话的任务(降序,主进程已排好) */
  bySession: Record<string, VideoJobView[]>
  loadedSessions: Record<string, boolean>
  load: (sessionId: string) => Promise<void>
  /** 单条任务。卡片在工具调用结束之后靠它继续跟进 */
  job: (id: string) => VideoJobView | undefined
}

let subscribed = false

function subscribeOnce(set: (partial: Partial<VideoJobsState>) => void, get: () => VideoJobsState): void {
  if (subscribed) return
  subscribed = true
  on('video:jobChanged', (view) => {
    /*
      ★ 按 revision 合并,且**只在更新时替换**。一条迟到的旧快照不该让卡片
      退回上一个状态(那看起来像"卡住了")。
    */
    const current = get().bySession[view.sessionId] ?? []
    const index = current.findIndex((job) => job.id === view.id)
    const next = [...current]
    if (index < 0) next.unshift(view)
    else if ((next[index]?.revision ?? -1) < view.revision) next[index] = view
    else return
    set({ bySession: { ...get().bySession, [view.sessionId]: next } })
  })
}

export const useVideoJobsStore = create<VideoJobsState>((set, get) => ({
  bySession: {},
  loadedSessions: {},

  async load(sessionId) {
    subscribeOnce(set, get)
    const views = await listVideoJobs(sessionId)
    set({
      bySession: { ...get().bySession, [sessionId]: views },
      loadedSessions: { ...get().loadedSessions, [sessionId]: true }
    })
  },

  job(id) {
    for (const list of Object.values(get().bySession)) {
      const hit = list.find((job) => job.id === id)
      if (hit !== undefined) return hit
    }
    return undefined
  }
}))
